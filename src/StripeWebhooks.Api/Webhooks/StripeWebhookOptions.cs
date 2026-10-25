using System.ComponentModel.DataAnnotations;

namespace StripeWebhooks.Api.Webhooks;

public sealed class StripeWebhookOptions
{
    public const string SectionName = "Stripe";

    /// <summary>
    /// Endpoint signing secret (whsec_...). Never commit it: set the <c>Stripe__WebhookSecret</c>
    /// environment variable, or in Development run
    /// <c>dotnet user-secrets set Stripe:WebhookSecret whsec_... --project src/StripeWebhooks.Api</c>.
    /// </summary>
    [Required]
    public string WebhookSecret { get; set; } = "";

    /// <summary>Maximum age of the Stripe-Signature timestamp. Stripe's libraries default to 300.</summary>
    [Range(1, 3600)]
    public long SignatureToleranceSeconds { get; set; } = 300;

    /// <summary>
    /// Largest body read before the signature is checked; larger requests get 413. Stripe event
    /// payloads are usually a few KB, because embedded lists are truncated.
    /// </summary>
    [Range(1_024, 16 * 1024 * 1024)]
    public int MaxBodyBytes { get; set; } = 1024 * 1024;

    /// <summary>Capacity of the in-memory queue between the endpoint and the worker.</summary>
    [Range(1, 1_000_000)]
    public int QueueCapacity { get; set; } = 1_000;

    /// <summary>How often the sweeper looks for pending rows that are due (crash recovery, full queue, retries).</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Wait after the first failed attempt. Each further failure waits 4x longer, up to <see cref="RetryMaxDelay"/>.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromHours(6);

    /// <summary>
    /// Attempts before an event is marked Failed. With the default delays, 10 attempts span about a day.
    /// Stripe already got a 200, so it will not redeliver a Failed event: it needs a manual replay.
    /// </summary>
    [Range(1, 100)]
    public int MaxAttempts { get; set; } = 10;

    /// <summary>30 s, 2 min, 8 min, 32 min, 2 h 8 min, then 6 h with the defaults.</summary>
    public TimeSpan RetryDelay(int failedAttempts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failedAttempts, 1);
        var factor = Math.Pow(4, Math.Min(failedAttempts - 1, 30));
        return TimeSpan.FromTicks((long)Math.Min(RetryBaseDelay.Ticks * factor, RetryMaxDelay.Ticks));
    }
}
