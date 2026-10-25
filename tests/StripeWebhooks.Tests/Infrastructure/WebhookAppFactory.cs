using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Stripe;
using StripeWebhooks.Api.Data;
using StripeWebhooks.Api.Webhooks;

namespace StripeWebhooks.Tests.Infrastructure;

/// <summary>
/// Runs the real app in memory against its own SQLite file, with a webhook secret generated for this
/// run, an instrumented payment handler (call counter and injectable failures), and captured logs.
/// </summary>
/// <remarks>
/// The default sweeps only once, at startup, so in tests that use this factory every event has to
/// reach the worker through the receiver's own enqueue: the fast-ack path is what is under test, not
/// the sweeper fallback. Subclasses change the settings for the retry and backpressure tests.
/// </remarks>
public class WebhookAppFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"stripe-webhooks-tests-{Guid.NewGuid():N}.db");

    private HttpClient? _client;

    public string WebhookSecret { get; } = StripeSignature.NewTestSecret();

    public HandlerCallCounter HandlerCalls { get; } = new();

    public HandlerFaults Faults { get; } = new();

    public LogSink Logs { get; } = new();

    private HttpClient Client => _client ??= CreateClient();

    protected virtual IReadOnlyDictionary<string, string?> Settings { get; } = new Dictionary<string, string?>
    {
        ["Stripe:SweepInterval"] = "01:00:00",
    };

    /// <summary>Posts a body signed with this run's secret, as Stripe would.</summary>
    public Task<HttpResponseMessage> PostSignedAsync(string json) =>
        PostAsync(json, StripeSignature.Header(json, WebhookSecret, DateTimeOffset.UtcNow));

    public Task<HttpResponseMessage> PostAsync(string body, string? signatureHeader) =>
        PostAsync(new StringContent(body, Encoding.UTF8, "application/json"), signatureHeader);

    public async Task<HttpResponseMessage> PostAsync(HttpContent content, string? signatureHeader)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/webhooks/stripe", UriKind.Relative))
        {
            Content = content,
        };
        if (signatureHeader is not null)
        {
            request.Headers.TryAddWithoutValidation(WebhookReceiver.SignatureHeader, signatureHeader);
        }

        return await Client.SendAsync(request);
    }

    /// <summary>Waits until the worker has finished with the event: Processed or Failed.</summary>
    /// <param name="eventId">The Stripe event id.</param>
    /// <param name="whilePending">
    /// Optional check for "no partial effect". It runs before each read of the row and returns an
    /// assertion, which is invoked only if the row is still Pending afterwards. Reading the other state
    /// first makes this race-free: whatever it saw was committed while the event was still Pending.
    /// </param>
    public async Task<ProcessedEvent> WaitUntilHandledAsync(string eventId, Func<Task<Action>>? whilePending = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var assertWhilePending = whilePending is null ? null : await whilePending();
            var row = await FindEventAsync(eventId);
            if (row is { Status: not EventStatus.Pending })
            {
                return row;
            }

            assertWhilePending?.Invoke();

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"{eventId} was not handled in time (row: {row?.Status.ToString() ?? "missing"}).");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Posts a marker event and waits until it is handled. The worker is the channel's only reader and
    /// takes ids in order, so everything enqueued before the marker has been fully processed by then.
    /// </summary>
    public async Task DrainAsync()
    {
        var markerId = StripeEvents.NewId("evt");
        var json = StripeEvents.Event(markerId, "test.drain_marker", StripeEvents.BaseCreated, new JsonObject
        {
            ["id"] = StripeEvents.NewId("cus"),
            ["object"] = "customer",
        });

        using var response = await PostSignedAsync(json);
        response.EnsureSuccessStatusCode();
        await WaitUntilHandledAsync(markerId);
    }

    public Task<ProcessedEvent?> FindEventAsync(string eventId) =>
        QueryAsync(db => db.ProcessedEvents.AsNoTracking().SingleOrDefaultAsync(e => e.EventId == eventId));

    public Task<int> CountEventRowsAsync(string eventId) =>
        QueryAsync(db => db.ProcessedEvents.CountAsync(e => e.EventId == eventId));

    public Task<Payment?> FindPaymentAsync(string paymentIntentId) =>
        QueryAsync(db => db.Payments.AsNoTracking().SingleOrDefaultAsync(p => p.PaymentIntentId == paymentIntentId));

    public async Task<T> QueryAsync<T>(Func<PaymentsDbContext, Task<T>> query)
    {
        await using var scope = Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<PaymentsDbContext>());
    }

    /// <summary>Stores a Pending row directly, as if the app had crashed right after acknowledging it.</summary>
    public Task InsertPendingAsync(string eventId, string json, DateTime nextAttemptAtUtc) =>
        QueryAsync(async db =>
        {
            db.ProcessedEvents.Add(new ProcessedEvent
            {
                EventId = eventId,
                Type = JsonNode.Parse(json)!["type"]!.GetValue<string>(),
                EventCreated = JsonNode.Parse(json)!["created"]!.GetValue<long>(),
                Payload = json,
                Status = EventStatus.Pending,
                ReceivedAtUtc = DateTime.UtcNow,
                NextAttemptAtUtc = nextAttemptAtUtc,
            });
            return await db.SaveChangesAsync();
        });

    /// <summary>What the sweeper or a restart does: hand an id to the processor, outside the worker.</summary>
    public async Task ProcessAgainAsync(string eventId)
    {
        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<EventProcessor>().ProcessAsync(eventId, CancellationToken.None);
    }

    protected virtual void AddTestServices(IServiceCollection services)
    {
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        var settings = new Dictionary<string, string?>(Settings)
        {
            ["Stripe:WebhookSecret"] = WebhookSecret,
            ["ConnectionStrings:Payments"] = $"Data Source={_databasePath}",
        };
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));

        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddProvider(Logs);
        });

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(HandlerCalls);
            services.AddSingleton(Faults);
            services.AddScoped<PaymentEventHandler>();
            services.AddScoped<IPaymentEventHandler>(sp => new InstrumentedHandler(
                sp.GetRequiredService<PaymentEventHandler>(),
                sp.GetRequiredService<HandlerCallCounter>(),
                sp.GetRequiredService<HandlerFaults>()));
            AddTestServices(services);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            try
            {
                System.IO.File.Delete(file);
            }
            catch (IOException)
            {
                // Best effort: it is a uniquely named file in the temp folder.
            }
        }
    }
}

public static class HttpResponseExtensions
{
    public static async Task<string?> ReadAckStatusAsync(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<WebhookAck>())?.Status;

    public static async Task<string?> ReadErrorAsync(this HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())?["error"]?.GetValue<string>();
}

/// <summary>Counts how many times the business handler ran for each event id.</summary>
public sealed class HandlerCallCounter
{
    private readonly ConcurrentDictionary<string, int> _calls = new();

    public void Record(string eventId) => _calls.AddOrUpdate(eventId, 1, (_, count) => count + 1);

    public int For(string eventId) => _calls.GetValueOrDefault(eventId);
}

/// <summary>Makes the handler throw for chosen event ids, a set number of times or always.</summary>
public sealed class HandlerFaults
{
    private readonly ConcurrentDictionary<string, int> _remaining = new();

    public void FailTimes(string eventId, int times) => _remaining[eventId] = times;

    public void FailAlways(string eventId) => _remaining[eventId] = int.MaxValue;

    public bool ShouldFail(string eventId)
    {
        while (_remaining.TryGetValue(eventId, out var left) && left > 0)
        {
            if (left == int.MaxValue || _remaining.TryUpdate(eventId, left - 1, left))
            {
                return true;
            }
        }

        return false;
    }
}

internal sealed class InstrumentedHandler(
    IPaymentEventHandler inner, HandlerCallCounter counter, HandlerFaults faults) : IPaymentEventHandler
{
    public async Task<EventOutcome> HandleAsync(Event stripeEvent, CancellationToken cancellationToken)
    {
        counter.Record(stripeEvent.Id);

        // The real handler stages its Payments change first, so an injected failure proves that the
        // rollback discards it rather than finding nothing to discard.
        var outcome = await inner.HandleAsync(stripeEvent, cancellationToken);
        if (faults.ShouldFail(stripeEvent.Id))
        {
            throw new InvalidOperationException($"Injected handler failure for {stripeEvent.Id}");
        }

        return outcome;
    }
}

/// <summary>Keeps formatted log lines in memory so tests can assert on them.</summary>
public sealed class LogSink : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyCollection<string> Lines => _lines;

    public ILogger CreateLogger(string categoryName) => new SinkLogger(_lines);

    public void Dispose()
    {
    }

    private sealed class SinkLogger(ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                lines.Enqueue($"{logLevel}: {formatter(state, exception)}");
            }
        }
    }
}
