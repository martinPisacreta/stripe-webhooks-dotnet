using System.Data.Common;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
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
        var settings = options.Value;

        var signature = request.Headers[SignatureHeader].ToString();
        if (string.IsNullOrWhiteSpace(signature))
        {
            logger.MissingSignature(SignatureHeader);
            return Results.BadRequest(new { error = "missing Stripe-Signature header" });
        }

        // The endpoint is public and unauthenticated until the signature is checked, so cap what it reads.
        var json = await ReadBodyAsync(request, settings.MaxBodyBytes, cancellationToken);
        if (json is null)
        {
            logger.BodyTooLarge(settings.MaxBodyBytes);
            return Results.Json(
                new { error = $"body larger than {settings.MaxBodyBytes} bytes" },
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        Event stripeEvent;
        try
        {
            // The API version is checked separately below, so that a correctly signed event from another
            // API release is not reported as a bad signature.
            stripeEvent = EventUtility.ConstructEvent(
                json,
                signature,
                settings.WebhookSecret,
                tolerance: settings.SignatureToleranceSeconds,
                utcNow: clock.GetUtcNow().ToUnixTimeSeconds(),
                throwOnApiVersionMismatch: false);
        }
        catch (StripeException ex)
        {
            // Wrong secret, modified body, or a timestamp outside the tolerance (in either direction).
            logger.WebhookRejected(ex.Message);
            return Results.BadRequest(new { error = "invalid signature" });
        }

        var apiVersion = stripeEvent.ApiVersion ?? "";
        if (!EventUtility.IsCompatibleApiVersion(apiVersion))
        {
            // Signed by Stripe, but in an API release this Stripe.net version cannot deserialize safely.
            // 400 keeps Stripe retrying while the endpoint's API version (or the SDK) is fixed.
            logger.UnsupportedApiVersion(stripeEvent.Id, apiVersion, StripeConfiguration.ApiVersion);
            return Results.BadRequest(new
            {
                error = $"unsupported api_version {apiVersion}; this endpoint expects the "
                    + $"{ReleaseOf(StripeConfiguration.ApiVersion)} release ({StripeConfiguration.ApiVersion})",
            });
        }

        var created = new DateTimeOffset(stripeEvent.Created, TimeSpan.Zero).ToUnixTimeSeconds();
        var receivedAt = clock.GetUtcNow().UtcDateTime;

        // Insert first, never "SELECT then INSERT": the unique index on EventId decides atomically which
        // delivery wins, including concurrent ones. A losing delivery affects 0 rows.
        int inserted;
        try
        {
            inserted = await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO ProcessedEvents (EventId, Type, EventCreated, Payload, Status, Attempts, ReceivedAtUtc, NextAttemptAtUtc)
                VALUES ({stripeEvent.Id}, {stripeEvent.Type}, {created}, {json}, {nameof(EventStatus.Pending)}, 0, {receivedAt}, {receivedAt})
                ON CONFLICT (EventId) DO NOTHING
                """,
                cancellationToken);
        }
        catch (DbException ex)
        {
            // Not acknowledged: Stripe retries any non-2xx, and 503 says the failure is on our side, not in the request.
            logger.AcceptanceFailed(ex, stripeEvent.Id);
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

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

    /// <summary>Returns the raw body as text, or null if it is larger than <paramref name="maxBytes"/>.</summary>
    private static async Task<string?> ReadBodyAsync(HttpRequest request, int maxBytes, CancellationToken cancellationToken)
    {
        if (request.ContentLength > maxBytes)
        {
            return null;
        }

        // Kestrel then enforces the same cap on bodies sent without Content-Length (chunked).
        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeLimit)
        {
            sizeLimit.MaxRequestBodySize = maxBytes;
        }

        using var body = new MemoryStream();
        var chunk = new byte[16 * 1024];
        try
        {
            int read;
            while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (body.Length + read > maxBytes)
                {
                    return null;
                }

                body.Write(chunk, 0, read);
            }
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return null;
        }

        // The signature covers the exact bytes Stripe sent, so verify the raw body, never a re-serialized one.
        return Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length);
    }

    /// <summary>"2026-09-30.endive" becomes "endive". Stripe.net accepts any version of its own release.</summary>
    private static string ReleaseOf(string apiVersion) =>
        apiVersion[(apiVersion.LastIndexOf('.') + 1)..];
}

public sealed record WebhookAck(string Status);
