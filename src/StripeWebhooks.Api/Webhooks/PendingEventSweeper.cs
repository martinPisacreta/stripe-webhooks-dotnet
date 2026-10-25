using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StripeWebhooks.Api.Data;

namespace StripeWebhooks.Api.Webhooks;

/// <summary>
/// Re-queues events that were acknowledged but not processed: on startup (crash or restart with
/// items still in memory), when the queue was full, and after a failed attempt. This is what
/// turns the in-memory queue into at-least-once delivery to <see cref="EventProcessor"/>.
/// </summary>
public sealed class PendingEventSweeper(
    EventQueue queue,
    IServiceScopeFactory scopeFactory,
    IOptions<StripeWebhookOptions> options,
    TimeProvider clock,
    ILogger<PendingEventSweeper> logger) : BackgroundService
{
    private const int BatchSize = 500;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.SweepInterval;

        // On startup everything pending is fair game: nothing can be in flight yet.
        await SweepAsync(receivedBefore: DateTime.MaxValue, stoppingToken);

        using var timer = new PeriodicTimer(interval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                // Skip rows younger than one interval: they are most likely still in the queue.
                await SweepAsync(clock.GetUtcNow().UtcDateTime - interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task SweepAsync(DateTime receivedBefore, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

            var pending = await db.ProcessedEvents
                .AsNoTracking()
                .Where(e => e.Status == EventStatus.Pending && e.ReceivedAtUtc < receivedBefore)
                .OrderBy(e => e.Id)
                .Select(e => e.EventId)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            var requeued = pending.TakeWhile(queue.TryEnqueue).Count();
            if (requeued > 0)
            {
                logger.Requeued(requeued);
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.SweepFailed(ex);
        }
    }
}
