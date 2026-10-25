using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StripeWebhooks.Api.Webhooks;

namespace StripeWebhooks.Tests.Infrastructure;

/// <summary>
/// Short sweep interval and retry delays, three attempts: failures, retries and crash recovery
/// complete in well under a second instead of minutes.
/// </summary>
public sealed class RetryingAppFactory : WebhookAppFactory
{
    public const int MaxAttempts = 3;

    protected override IReadOnlyDictionary<string, string?> Settings { get; } = new Dictionary<string, string?>
    {
        ["Stripe:SweepInterval"] = "00:00:00.100",
        ["Stripe:RetryBaseDelay"] = "00:00:00.050",
        ["Stripe:RetryMaxDelay"] = "00:00:00.200",
        ["Stripe:MaxAttempts"] = MaxAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}

/// <summary>
/// A queue that holds one id and a worker that does not start until <see cref="OpenWorkerGate"/>, so a
/// test can fill the queue on purpose. The sweeper runs every 100 ms as the fallback.
/// </summary>
public sealed class BackpressureAppFactory : WebhookAppFactory
{
    private readonly TaskCompletionSource _workerGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override IReadOnlyDictionary<string, string?> Settings { get; } = new Dictionary<string, string?>
    {
        ["Stripe:QueueCapacity"] = "1",
        ["Stripe:SweepInterval"] = "00:00:00.100",
    };

    public void OpenWorkerGate() => _workerGate.TrySetResult();

    protected override void AddTestServices(IServiceCollection services)
    {
        var worker = services.Single(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(EventWorker));
        services.Remove(worker);
        services.AddSingleton<IHostedService>(sp =>
            new GatedWorker(ActivatorUtilities.CreateInstance<EventWorker>(sp), _workerGate.Task));
    }

    /// <summary>Starts the real <see cref="EventWorker"/> only once the gate opens.</summary>
    private sealed class GatedWorker(EventWorker inner, Task gate) : IHostedService, IDisposable
    {
        private Task _started = Task.CompletedTask;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _started = gate.ContinueWith(_ => inner.StartAsync(CancellationToken.None), TaskScheduler.Default).Unwrap();
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (!gate.IsCompleted)
            {
                return; // The test failed before opening the gate: the worker never started.
            }

            await _started.WaitAsync(cancellationToken);
            await inner.StopAsync(cancellationToken);
        }

        public void Dispose() => inner.Dispose();
    }
}
