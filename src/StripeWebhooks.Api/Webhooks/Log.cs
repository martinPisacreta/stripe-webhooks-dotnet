namespace StripeWebhooks.Api.Webhooks;

/// <summary>Source-generated log messages (no boxing or formatting when the level is off).</summary>
internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected webhook: missing {Header} header")]
    public static partial void MissingSignature(this ILogger logger, string header);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected webhook: {Reason}")]
    public static partial void WebhookRejected(this ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Duplicate delivery of {EventId} ({EventType}) acknowledged without reprocessing")]
    public static partial void DuplicateDelivery(this ILogger logger, string eventId, string eventType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Queue full; {EventId} stays pending and the sweeper will pick it up")]
    public static partial void QueueFull(this ILogger logger, string eventId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{EventId} was already handled; skipping")]
    public static partial void AlreadyHandled(this ILogger logger, string eventId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Processed {EventId} ({EventType}): {Outcome}")]
    public static partial void Processed(this ILogger logger, string eventId, string eventType, Data.EventOutcome? outcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Attempt {Attempts} for {EventId} failed; it will be retried")]
    public static partial void AttemptFailed(this ILogger logger, Exception error, int attempts, string eventId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Giving up on {EventId} after {Attempts} attempts; replay it manually")]
    public static partial void GaveUp(this ILogger logger, Exception error, string eventId, int attempts);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unexpected error while processing {EventId}")]
    public static partial void WorkerError(this ILogger logger, Exception error, string eventId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Re-queued {Count} pending event(s)")]
    public static partial void Requeued(this ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Sweep failed; will try again on the next tick")]
    public static partial void SweepFailed(this ILogger logger, Exception error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ignoring unhandled event type {EventType} ({EventId})")]
    public static partial void UnhandledType(this ILogger logger, string eventType, string eventId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ignoring {EventId}: charge {ChargeId} has no PaymentIntent")]
    public static partial void NoPaymentIntent(this ILogger logger, string eventId, string chargeId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ignoring stale {EventType} {EventId} for {PaymentIntentId}: created {Created} < last applied {LastCreated}")]
    public static partial void StaleEvent(this ILogger logger, string eventType, string eventId, string paymentIntentId, long created, long lastCreated);
}
