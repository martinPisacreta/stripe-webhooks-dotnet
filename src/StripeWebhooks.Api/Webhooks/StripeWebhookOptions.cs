using System.ComponentModel.DataAnnotations;

namespace StripeWebhooks.Api.Webhooks;

public sealed class StripeWebhookOptions
{
    public const string SectionName = "Stripe";

    /// <summary>
    /// Endpoint signing secret (whsec_...). Never commit it: set the
    /// <c>Stripe__WebhookSecret</c> environment variable or use user secrets.
    /// </summary>
    [Required]
    public string WebhookSecret { get; set; } = "";

    /// <summary>Maximum age of the Stripe-Signature timestamp. Stripe's libraries default to 300.</summary>
    [Range(1, 3600)]
    public long SignatureToleranceSeconds { get; set; } = 300;

    /// <summary>Capacity of the in-memory queue between the endpoint and the worker.</summary>
    [Range(1, 1_000_000)]
    public int QueueCapacity { get; set; } = 1_000;

    /// <summary>How often pending rows are re-queued (crash recovery, full queue, retries).</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Attempts before an event is marked Failed and left for a manual replay.</summary>
    [Range(1, 100)]
    public int MaxAttempts { get; set; } = 5;
}
