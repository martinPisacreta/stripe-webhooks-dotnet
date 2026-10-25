using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StripeWebhooks.Api.Data;
using StripeWebhooks.Api.Webhooks;
using StripeWebhooks.Tests.Infrastructure;

namespace StripeWebhooks.Tests;

public sealed class SignatureVerificationTests(WebhookAppFactory app) : IClassFixture<WebhookAppFactory>
{
    public enum Tampering
    {
        WrongSecret,
        ModifiedBody,
        TimestampOutsideTolerance,
        TimestampInTheFuture,
        OnlyV0Signature,
        MalformedHeader,
        MissingHeader,
    }

    [Fact]
    public async Task ValidSignatureIsAcceptedAndApplied()
    {
        var eventId = StripeEvents.NewId("evt");
        var paymentIntentId = StripeEvents.NewId("pi");
        var json = StripeEvents.PaymentIntentSucceeded(eventId, paymentIntentId, 2_000, "usd", StripeEvents.BaseCreated);

        using var response = await app.PostSignedAsync(json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("accepted", await response.ReadAckStatusAsync());

        var processed = await app.WaitUntilHandledAsync(eventId);
        Assert.Equal(EventStatus.Processed, processed.Status);
        Assert.Equal(EventOutcome.Applied, processed.Outcome);
        Assert.Equal(0, processed.Attempts);

        var payment = await app.FindPaymentAsync(paymentIntentId);
        Assert.NotNull(payment);
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.Equal(2_000, payment.Amount);
        Assert.Equal("usd", payment.Currency);
        Assert.Equal(0, payment.AmountRefunded);
        Assert.Equal(DateTimeKind.Utc, payment.UpdatedAtUtc.Kind);
    }

    [Theory]
    [InlineData(Tampering.WrongSecret)]
    [InlineData(Tampering.ModifiedBody)]
    [InlineData(Tampering.TimestampOutsideTolerance)]
    [InlineData(Tampering.TimestampInTheFuture)]
    [InlineData(Tampering.OnlyV0Signature)]
    [InlineData(Tampering.MalformedHeader)]
    [InlineData(Tampering.MissingHeader)]
    public async Task InvalidSignatureIsRejectedWith400AndNothingIsStored(Tampering tampering)
    {
        var eventId = StripeEvents.NewId("evt");
        var json = StripeEvents.PaymentIntentSucceeded(eventId, StripeEvents.NewId("pi"), 2_000, "usd", StripeEvents.BaseCreated);
        var now = DateTimeOffset.UtcNow;

        var body = tampering == Tampering.ModifiedBody
            ? json.Replace("\"amount_received\":2000", "\"amount_received\":1", StringComparison.Ordinal)
            : json;
        Assert.Equal(tampering == Tampering.ModifiedBody, body != json);

        var header = tampering switch
        {
            Tampering.WrongSecret => StripeSignature.Header(json, StripeSignature.NewTestSecret(), now),
            Tampering.ModifiedBody => StripeSignature.Header(json, app.WebhookSecret, now),
            Tampering.TimestampOutsideTolerance => StripeSignature.Header(json, app.WebhookSecret, now.AddMinutes(-10)),
            Tampering.TimestampInTheFuture => StripeSignature.Header(json, app.WebhookSecret, now.AddMinutes(10)),
            // Stripe adds a fake v0 entry to test-mode events. Only v1 may be trusted (no downgrade),
            // even when the v0 value happens to be an HMAC with the right secret.
            Tampering.OnlyV0Signature => StripeSignature.Header(json, now, "v0", app.WebhookSecret),
            Tampering.MalformedHeader => "v1=deadbeef",
            Tampering.MissingHeader => null,
            _ => throw new ArgumentOutOfRangeException(nameof(tampering)),
        };

        using var response = await app.PostAsync(body, header);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            tampering == Tampering.MissingHeader ? "missing Stripe-Signature header" : "invalid signature",
            await response.ReadErrorAsync());
        Assert.Equal(0, await app.CountEventRowsAsync(eventId));
        Assert.Equal(0, app.HandlerCalls.For(eventId));
    }

    [Fact]
    public async Task HeaderSignedWithOldAndNewSecretIsAcceptedDuringASecretRoll()
    {
        var eventId = StripeEvents.NewId("evt");
        var json = StripeEvents.PaymentIntentSucceeded(eventId, StripeEvents.NewId("pi"), 2_000, "usd", StripeEvents.BaseCreated);

        // The configured secret is the second entry, as when the app already switched to the new secret.
        var header = StripeSignature.Header(json, DateTimeOffset.UtcNow, "v1", StripeSignature.NewTestSecret(), app.WebhookSecret);
        using var response = await app.PostAsync(json, header);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("accepted", await response.ReadAckStatusAsync());
        Assert.Equal(EventOutcome.Applied, (await app.WaitUntilHandledAsync(eventId)).Outcome);
    }

    [Theory]
    [InlineData("2025-03-31.basil")]
    [InlineData("2020-08-27")]
    public async Task CorrectlySignedEventFromAnotherApiReleaseGetsItsOwnError(string apiVersion)
    {
        var eventId = StripeEvents.NewId("evt");
        var json = StripeEvents.Event(eventId, "payment_intent.succeeded", StripeEvents.BaseCreated, new()
        {
            ["id"] = StripeEvents.NewId("pi"),
            ["object"] = "payment_intent",
            ["amount"] = 2_000,
            ["amount_received"] = 2_000,
            ["currency"] = "usd",
        }, apiVersion);

        using var response = await app.PostSignedAsync(json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.ReadErrorAsync();
        Assert.StartsWith($"unsupported api_version {apiVersion}; this endpoint expects the ", error, StringComparison.Ordinal);
        Assert.Contains(Stripe.StripeConfiguration.ApiVersion, error, StringComparison.Ordinal);
        Assert.Contains(app.Logs.Lines, line => line.StartsWith("Error: ", StringComparison.Ordinal)
            && line.Contains(eventId, StringComparison.Ordinal)
            && line.Contains(apiVersion, StringComparison.Ordinal));
        Assert.Equal(0, await app.CountEventRowsAsync(eventId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OversizedBodyIsRejectedWith413AndNothingIsStored(bool sendContentLength)
    {
        var maxBodyBytes = app.Services.GetRequiredService<IOptions<StripeWebhookOptions>>().Value.MaxBodyBytes;
        var eventId = StripeEvents.NewId("evt");
        var json = StripeEvents.Event(eventId, "customer.created", StripeEvents.BaseCreated, new()
        {
            ["id"] = StripeEvents.NewId("cus"),
            ["object"] = "customer",
            ["description"] = new string('x', maxBodyBytes),
        });

        // Correctly signed, so only the size can be the reason for the rejection.
        var header = StripeSignature.Header(json, app.WebhookSecret, DateTimeOffset.UtcNow);
        using HttpContent content = sendContentLength
            ? new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            : new UnknownLengthContent(System.Text.Encoding.UTF8.GetBytes(json));
        using var response = await app.PostAsync(content, header);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, await app.CountEventRowsAsync(eventId));
    }

    /// <summary>A body sent without Content-Length, like a chunked request.</summary>
    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
