using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stripe;
using StripeWebhooks.Api.Data;

namespace StripeWebhooks.Api.Webhooks;

/// <summary>
/// POST /webhooks/stripe. Verifies the signature, records the event once, hands it to the worker
/// and acknowledges. No business logic runs on the request path.
/// </summary>
public sealed class WebhookReceiver(
    PaymentsDbContext db,
    EventQueue queue,
    IOptions<StripeWebhookOptions> options,
    TimeProvider clock,
    ILogger<WebhookReceiver> logger)
{
    public const string SignatureHeader = "Stripe-Signature";

    public async Task<IResult> ReceiveAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var signature = request.Headers[SignatureHeader].ToString();
        if (string.IsNullOrWhiteSpace(signature))
        {
            logger.MissingSignature(SignatureHeader);
            return Results.BadRequest(new { error = "missing Stripe-Signature header" });
        }

        // The signature covers the exact bytes Stripe sent, so verify the raw body, never a re-serialized one.
        string json;
        using (var reader = new StreamReader(request.Body))
        {
            json = await reader.ReadToEndAsync(cancellationToken);
        }

        Event stripeEvent;
        try
        {
            stripeEvent = EventUtility.ConstructEvent(
                json,
                signature,
                options.Value.WebhookSecret,
                tolerance: options.Value.SignatureToleranceSeconds,
                utcNow: clock.GetUtcNow().ToUnixTimeSeconds(),
                throwOnApiVersionMismatch: true);
        }
        catch (StripeException ex)
        {
            // Bad signature, timestamp outside the tolerance, or an API version this Stripe.net release
            // cannot deserialize safely. Stripe retries non-2xx responses, so nothing is lost while a
            // misconfiguration is fixed.
            logger.WebhookRejected(ex.Message);
            return Results.BadRequest(new { error = "invalid signature" });
        }

        var created = new DateTimeOffset(stripeEvent.Created, TimeSpan.Zero).ToUnixTimeSeconds();
        var receivedAt = clock.GetUtcNow().UtcDateTime;

        // Insert first, never "SELECT then INSERT": the unique index on EventId decides atomically which
        // delivery wins, including concurrent ones. A losing delivery affects 0 rows.
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO ProcessedEvents (EventId, Type, EventCreated, Payload, Status, Attempts, ReceivedAtUtc)
            VALUES ({stripeEvent.Id}, {stripeEvent.Type}, {created}, {json}, {nameof(EventStatus.Pending)}, 0, {receivedAt})
            ON CONFLICT (EventId) DO NOTHING
            """,
            cancellationToken);

        if (inserted == 0)
        {
            logger.DuplicateDelivery(stripeEvent.Id, stripeEvent.Type);
            return Results.Ok(new WebhookAck("duplicate"));
        }

        if (!queue.TryEnqueue(stripeEvent.Id))
        {
            logger.QueueFull(stripeEvent.Id);
        }

        return Results.Ok(new WebhookAck("accepted"));
    }
}

public sealed record WebhookAck(string Status);
