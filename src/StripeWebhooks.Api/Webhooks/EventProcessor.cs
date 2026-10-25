using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stripe;
using StripeWebhooks.Api.Data;

namespace StripeWebhooks.Api.Webhooks;

/// <summary>
/// Processes one stored event. The worker may see the same event id more than once (the sweeper
/// re-queues pending rows after a restart or a failure); the conditional claim below makes the
/// business effect happen exactly once.
/// </summary>
public sealed class EventProcessor(
    PaymentsDbContext db,
    IPaymentEventHandler handler,
    IOptions<StripeWebhookOptions> options,
    TimeProvider clock,
    ILogger<EventProcessor> logger)
{
    public async Task ProcessAsync(string eventId, CancellationToken cancellationToken)
    {
        try
        {
            await ProcessInTransactionAsync(eventId, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            db.ChangeTracker.Clear();
            await RecordFailureAsync(eventId, ex, cancellationToken);
        }
    }

    private async Task ProcessInTransactionAsync(string eventId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Claim: only one transaction can move the row out of Pending. If the handler throws, the
        // rollback undoes the claim too, so the event is retried instead of silently dropped.
        var claimed = await db.ProcessedEvents
            .Where(e => e.EventId == eventId && e.Status == EventStatus.Pending)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(e => e.Status, EventStatus.Processed)
                    .SetProperty(e => e.ProcessedAtUtc, clock.GetUtcNow().UtcDateTime),
                cancellationToken);

        if (claimed == 0)
        {
            logger.AlreadyHandled(eventId);
            return;
        }

        var row = await db.ProcessedEvents.SingleAsync(e => e.EventId == eventId, cancellationToken);

        // The signature and API version were checked on receipt; this is our own stored copy.
        var stripeEvent = EventUtility.ParseEvent(row.Payload, throwOnApiVersionMismatch: false);

        row.Outcome = await handler.HandleAsync(stripeEvent, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.Processed(row.EventId, row.Type, row.Outcome);
    }

    private async Task RecordFailureAsync(string eventId, Exception error, CancellationToken cancellationToken)
    {
        var row = await db.ProcessedEvents.SingleOrDefaultAsync(e => e.EventId == eventId, cancellationToken);
        if (row is null || row.Status != EventStatus.Pending)
        {
            return;
        }

        row.Attempts++;
        row.LastError = error.Message.Length > 1000 ? error.Message[..1000] : error.Message;

        if (row.Attempts >= options.Value.MaxAttempts)
        {
            row.Status = EventStatus.Failed;
            logger.GaveUp(error, eventId, row.Attempts);
        }
        else
        {
            logger.AttemptFailed(error, row.Attempts, eventId);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
