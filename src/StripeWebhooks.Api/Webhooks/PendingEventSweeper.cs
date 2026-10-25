using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StripeWebhooks.Api.Data;

namespace StripeWebhooks.Api.Webhooks;

/// <summary>
/// Offers due Pending rows to the worker: on startup (crash or restart with items still in memory),
/// when the queue was full, and when a failed attempt's backoff has elapsed. This is what turns the
/// in-memory queue into at-least-once delivery to <see cref="EventProcessor"/>.
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
        // The first sweep recovers what a crash or restart left behind. Requests can already be arriving,
        // so the same id may be offered by the receiver and by the sweeper: EventQueue keeps one copy,
        // and the claim in EventProcessor absorbs any duplicate that still gets through.
        await SweepAsync(stoppingToken);

        using var timer = new PeriodicTimer(options.Value.SweepInterval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await SweepAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            var now = clock.GetUtcNow().UtcDateTime;

            var due = await db.ProcessedEvents
                .AsNoTracking()
                .Where(e => e.Status == EventStatus.Pending && e.NextAttemptAtUtc <= now)
                .OrderBy(e => e.NextAttemptAtUtc)
                .ThenBy(e => e.Id)
                .Select(e => e.EventId)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            var requeued = 0;
            foreach (var eventId in due)
            {
                if (queue.Contains(eventId))
                {
                    continue; // Still waiting in the channel, or being processed right now.
                }

                if (!queue.TryEnqueue(eventId))
                {
                    break; // Full: the rest waits for the next tick.
                }

                requeued++;
            }

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
