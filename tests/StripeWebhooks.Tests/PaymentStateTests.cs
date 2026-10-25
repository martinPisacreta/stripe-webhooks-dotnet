using System.Net;
using System.Text.Json.Nodes;
using StripeWebhooks.Api.Data;
using StripeWebhooks.Tests.Infrastructure;

namespace StripeWebhooks.Tests;

public sealed class PaymentStateTests(WebhookAppFactory app) : IClassFixture<WebhookAppFactory>
{
    private const long T = StripeEvents.BaseCreated;

    [Fact]
    public async Task RefundUpdatesPaymentState()
    {
        var pi = StripeEvents.NewId("pi");

        await DeliverAsync(StripeEvents.PaymentIntentSucceeded(StripeEvents.NewId("evt"), pi, 2_000, "usd", T));
        await DeliverAsync(StripeEvents.ChargeRefunded(StripeEvents.NewId("evt"), pi, 2_000, amountRefunded: 500, "usd", T + 60));

        var partial = await app.FindPaymentAsync(pi);
        Assert.NotNull(partial);
        Assert.Equal(PaymentStatus.PartiallyRefunded, partial.Status);
        Assert.Equal(500, partial.AmountRefunded);
        Assert.Equal(2_000, partial.Amount);

        var fullRefundId = StripeEvents.NewId("evt");
        await DeliverAsync(StripeEvents.ChargeRefunded(fullRefundId, pi, 2_000, amountRefunded: 2_000, "usd", T + 120));

        var refunded = await app.FindPaymentAsync(pi);
        Assert.NotNull(refunded);
        Assert.Equal(PaymentStatus.Refunded, refunded.Status);
        Assert.Equal(2_000, refunded.AmountRefunded);
        Assert.Equal(fullRefundId, refunded.LastEventId);
        Assert.Equal(T + 120, refunded.LastEventCreated);
    }

    [Fact]
    public async Task OutOfOrderEventIsIgnored()
    {
        var pi = StripeEvents.NewId("pi");
        var refundId = StripeEvents.NewId("evt");
        var succeededId = StripeEvents.NewId("evt");

        // The refund (newer) arrives before the success (older), which Stripe allows.
        await DeliverAsync(StripeEvents.ChargeRefunded(refundId, pi, 2_000, amountRefunded: 2_000, "usd", T + 60));
        var stale = await DeliverAsync(StripeEvents.PaymentIntentSucceeded(succeededId, pi, 2_000, "usd", T));

        Assert.Equal(EventStatus.Processed, stale.Status);
        Assert.Equal(EventOutcome.IgnoredStale, stale.Outcome);

        var payment = await app.FindPaymentAsync(pi);
        Assert.NotNull(payment);
        Assert.Equal(PaymentStatus.Refunded, payment.Status);
        Assert.Equal(2_000, payment.AmountRefunded);
        Assert.Equal(refundId, payment.LastEventId);
    }

    [Fact]
    public async Task OlderRefundSnapshotDoesNotOverwriteNewerOne()
    {
        var pi = StripeEvents.NewId("pi");

        await DeliverAsync(StripeEvents.PaymentIntentSucceeded(StripeEvents.NewId("evt"), pi, 2_000, "usd", T));
        await DeliverAsync(StripeEvents.ChargeRefunded(StripeEvents.NewId("evt"), pi, 2_000, amountRefunded: 2_000, "usd", T + 120));
        var stale = await DeliverAsync(StripeEvents.ChargeRefunded(StripeEvents.NewId("evt"), pi, 2_000, amountRefunded: 500, "usd", T + 60));

        Assert.Equal(EventOutcome.IgnoredStale, stale.Outcome);
        var payment = await app.FindPaymentAsync(pi);
        Assert.NotNull(payment);
        Assert.Equal(PaymentStatus.Refunded, payment.Status);
        Assert.Equal(2_000, payment.AmountRefunded);
    }

    [Fact]
    public async Task SameSecondRefundsKeepTheLargerCumulativeAmount()
    {
        var pi = StripeEvents.NewId("pi");

        // `created` has one-second resolution, so these two cannot be ordered by it. The full refund
        // happens to arrive first; the partial snapshot from the same second must not shrink it.
        await DeliverAsync(StripeEvents.PaymentIntentSucceeded(StripeEvents.NewId("evt"), pi, 2_000, "usd", T));
        await DeliverAsync(StripeEvents.ChargeRefunded(StripeEvents.NewId("evt"), pi, 2_000, amountRefunded: 2_000, "usd", T + 60));
        var tie = await DeliverAsync(StripeEvents.ChargeRefunded(StripeEvents.NewId("evt"), pi, 2_000, amountRefunded: 500, "usd", T + 60));

        Assert.Equal(EventOutcome.Applied, tie.Outcome);
        var payment = await app.FindPaymentAsync(pi);
        Assert.NotNull(payment);
        Assert.Equal(2_000, payment.AmountRefunded);
        Assert.Equal(PaymentStatus.Refunded, payment.Status);
    }

    [Fact]
    public async Task SuccessFromTheSameSecondAsAFullRefundDoesNotUndoIt()
    {
        var pi = StripeEvents.NewId("pi");

        await DeliverAsync(StripeEvents.ChargeRefunded(StripeEvents.NewId("evt"), pi, 2_000, amountRefunded: 2_000, "usd", T));
        var tie = await DeliverAsync(StripeEvents.PaymentIntentSucceeded(StripeEvents.NewId("evt"), pi, 2_000, "usd", T));

        // Only strictly older events are stale. A tie is applied, and the success snapshot carries no
        // refund data, so the refund already recorded stands.
        Assert.Equal(EventOutcome.Applied, tie.Outcome);
        var payment = await app.FindPaymentAsync(pi);
        Assert.NotNull(payment);
        Assert.Equal(PaymentStatus.Refunded, payment.Status);
        Assert.Equal(2_000, payment.AmountRefunded);
        Assert.Equal(2_000, payment.Amount);
    }

    [Fact]
    public async Task UnknownEventTypeIsAcknowledgedAndLogged()
    {
        var eventId = StripeEvents.NewId("evt");
        var json = StripeEvents.Event(eventId, "customer.created", T, new JsonObject
        {
            ["id"] = StripeEvents.NewId("cus"),
            ["object"] = "customer",
        });

        var processed = await DeliverAsync(json);

        Assert.Equal(EventStatus.Processed, processed.Status);
        Assert.Equal(EventOutcome.IgnoredUnhandledType, processed.Outcome);
        Assert.Contains(app.Logs.Lines, line => line.Contains("unhandled event type customer.created", StringComparison.Ordinal)
            && line.Contains(eventId, StringComparison.Ordinal));
    }

    private async Task<ProcessedEvent> DeliverAsync(string json)
    {
        var eventId = JsonNode.Parse(json)!["id"]!.GetValue<string>();
        using var response = await app.PostSignedAsync(json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await app.WaitUntilHandledAsync(eventId);
    }
}
