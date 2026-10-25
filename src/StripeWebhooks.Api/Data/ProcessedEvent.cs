namespace StripeWebhooks.Api.Data;

/// <summary>
/// One row per Stripe event id. The unique index on <see cref="EventId"/> is the idempotency guard:
/// the row is inserted before the 200 is returned, so a retry or a concurrent duplicate cannot
/// create a second row, and the worker applies each row at most once (see EventProcessor).
/// </summary>
public sealed class ProcessedEvent
{
    public long Id { get; set; }

    public string EventId { get; set; } = "";

    public string Type { get; set; } = "";

    /// <summary>The event's <c>created</c> timestamp (Unix seconds).</summary>
    public long EventCreated { get; set; }

    /// <summary>Raw JSON body as received, already signature-verified.</summary>
    public string Payload { get; set; } = "";

    public EventStatus Status { get; set; }

    public EventOutcome? Outcome { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }

    public DateTime ReceivedAtUtc { get; set; }

    public DateTime? ProcessedAtUtc { get; set; }
}

public enum EventStatus
{
    /// <summary>Stored and acknowledged, waiting for the worker.</summary>
    Pending,

    /// <summary>Applied (or deliberately ignored) inside a committed transaction.</summary>
    Processed,

    /// <summary>Gave up after the configured number of attempts; needs a manual replay.</summary>
    Failed,
}

public enum EventOutcome
{
    Applied,
    IgnoredStale,
    IgnoredUnhandledType,
    IgnoredNoPaymentIntent,
}
