namespace StripeWebhooks.Api.Data;

/// <summary>
/// Local view of a Stripe PaymentIntent, built only from webhook events.
/// Amounts are in the currency's minor unit (cents for USD), exactly as Stripe sends them.
/// </summary>
public sealed class Payment
{
    public string PaymentIntentId { get; set; } = "";

    public long Amount { get; set; }

    /// <summary>Three-letter ISO currency code, lower case as Stripe sends it ("usd").</summary>
    public string Currency { get; set; } = "";

    public long AmountRefunded { get; set; }

    public PaymentStatus Status { get; set; }

    /// <summary>
    /// <c>created</c> of the newest event applied to this payment (Unix seconds).
    /// Events older than this are stale and ignored.
    /// </summary>
    public long LastEventCreated { get; set; }

    public string LastEventId { get; set; } = "";

    public DateTime UpdatedAtUtc { get; set; }
}

public enum PaymentStatus
{
    Succeeded,
    PartiallyRefunded,
    Refunded,
}
