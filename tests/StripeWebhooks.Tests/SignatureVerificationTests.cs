using System.Net;
using StripeWebhooks.Api.Data;
using StripeWebhooks.Tests.Infrastructure;

namespace StripeWebhooks.Tests;

public sealed class SignatureVerificationTests(WebhookAppFactory app) : IClassFixture<WebhookAppFactory>
{
    public enum Tampering
    {
        WrongSecret,
        ModifiedBody,
        TimestampOutsideTolerance,
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
            Tampering.MalformedHeader => "v1=deadbeef",
            Tampering.MissingHeader => null,
            _ => throw new ArgumentOutOfRangeException(nameof(tampering)),
        };

        using var response = await app.PostAsync(body, header);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await app.CountEventRowsAsync(eventId));
        Assert.Equal(0, app.HandlerCalls.For(eventId));
    }
}
