namespace StripeWebhooks.Api.Webhooks;

/// <summary>Single consumer of <see cref="EventQueue"/>. Each event gets its own DI scope and DbContext.</summary>
public sealed class EventWorker(
    EventQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<EventWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var eventId in queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var processor = scope.ServiceProvider.GetRequiredService<EventProcessor>();
                    await processor.ProcessAsync(eventId, stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    // Never let one event stop the worker; the row stays pending and is retried.
                    logger.WorkerError(ex, eventId);
                }
                finally
                {
                    queue.MarkDone(eventId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down. Anything still queued is pending in the database and is re-queued on start.
        }
    }
}
