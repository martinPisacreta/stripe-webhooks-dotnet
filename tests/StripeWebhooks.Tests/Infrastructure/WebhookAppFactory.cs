using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;
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
/// run, a fast sweeper, a call counter around the payment handler, and captured logs.
/// </summary>
public sealed class WebhookAppFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"stripe-webhooks-tests-{Guid.NewGuid():N}.db");

    public string WebhookSecret { get; } = StripeSignature.NewTestSecret();

    public HandlerCallCounter HandlerCalls { get; } = new();

    public LogSink Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Stripe:WebhookSecret"] = WebhookSecret,
            ["Stripe:SweepInterval"] = "00:00:00.200",
            ["ConnectionStrings:Payments"] = $"Data Source={_databasePath}",
        }));

        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddProvider(Logs);
        });

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(HandlerCalls);
            services.AddScoped<PaymentEventHandler>();
            services.AddScoped<IPaymentEventHandler>(sp => new CountingHandler(
                sp.GetRequiredService<PaymentEventHandler>(),
                sp.GetRequiredService<HandlerCallCounter>()));
        });
    }

    /// <summary>Posts a body signed with this run's secret, as Stripe would.</summary>
    public Task<HttpResponseMessage> PostSignedAsync(string json) =>
        PostAsync(json, StripeSignature.Header(json, WebhookSecret, DateTimeOffset.UtcNow));

    public async Task<HttpResponseMessage> PostAsync(string body, string? signatureHeader)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/webhooks/stripe", UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (signatureHeader is not null)
        {
            request.Headers.TryAddWithoutValidation(WebhookReceiver.SignatureHeader, signatureHeader);
        }

        return await Client.SendAsync(request);
    }

    /// <summary>Waits until the background worker has finished with the event (or gives up).</summary>
    public async Task<ProcessedEvent> WaitUntilHandledAsync(string eventId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var row = await QueryAsync(db => db.ProcessedEvents.AsNoTracking().SingleOrDefaultAsync(e => e.EventId == eventId));
            if (row is { Status: not EventStatus.Pending })
            {
                return row;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"{eventId} was not handled in time (row: {row?.Status.ToString() ?? "missing"}).");
            }

            await Task.Delay(20);
        }
    }

    public Task<int> CountEventRowsAsync(string eventId) =>
        QueryAsync(db => db.ProcessedEvents.CountAsync(e => e.EventId == eventId));

    public Task<Payment?> FindPaymentAsync(string paymentIntentId) =>
        QueryAsync(db => db.Payments.AsNoTracking().SingleOrDefaultAsync(p => p.PaymentIntentId == paymentIntentId));

    public async Task<T> QueryAsync<T>(Func<PaymentsDbContext, Task<T>> query)
    {
        await using var scope = Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<PaymentsDbContext>());
    }

    public async Task ProcessAgainAsync(string eventId)
    {
        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<EventProcessor>().ProcessAsync(eventId, CancellationToken.None);
    }

    private HttpClient Client => _client ??= CreateClient();

    private HttpClient? _client;

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
}

/// <summary>Counts how many times the business handler ran for each event id.</summary>
public sealed class HandlerCallCounter
{
    private readonly ConcurrentDictionary<string, int> _calls = new();

    public void Record(string eventId) => _calls.AddOrUpdate(eventId, 1, (_, count) => count + 1);

    public int For(string eventId) => _calls.GetValueOrDefault(eventId);
}

internal sealed class CountingHandler(IPaymentEventHandler inner, HandlerCallCounter counter) : IPaymentEventHandler
{
    public Task<EventOutcome> HandleAsync(Event stripeEvent, CancellationToken cancellationToken)
    {
        counter.Record(stripeEvent.Id);
        return inner.HandleAsync(stripeEvent, cancellationToken);
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
