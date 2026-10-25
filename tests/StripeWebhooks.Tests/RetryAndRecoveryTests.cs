using System.Net;
using StripeWebhooks.Api.Data;
using StripeWebhooks.Tests.Infrastructure;

namespace StripeWebhooks.Tests;

/// <summary>The paths after the 200: a handler that throws, retries with backoff, giving up, crash recovery.</summary>
public sealed class RetryAndRecoveryTests(RetryingAppFactory app) : IClassFixture<RetryingAppFactory>
{
    [Fact]
    public async Task FailingHandlerIsRolledBackAndRetriedUntilItSucceeds()
    {
        var eventId = StripeEvents.NewId("evt");
        var paymentIntentId = StripeEvents.NewId("pi");
        app.Faults.FailTimes(eventId, 2);

        using var response = await app.PostSignedAsync(
            StripeEvents.PaymentIntentSucceeded(eventId, paymentIntentId, 2_000, "usd", StripeEvents.BaseCreated));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var processed = await app.WaitUntilHandledAsync(eventId, NoPaymentWhilePending(paymentIntentId));

        Assert.Equal(EventStatus.Processed, processed.Status);
        Assert.Equal(EventOutcome.Applied, processed.Outcome);
        Assert.Equal(2, processed.Attempts);
        Assert.Contains("Injected handler failure", processed.LastError, StringComparison.Ordinal);
        Assert.Equal(3, app.HandlerCalls.For(eventId));

        var payment = await app.FindPaymentAsync(paymentIntentId);
        Assert.NotNull(payment);
        Assert.Equal(2_000, payment.Amount);
        Assert.Equal(eventId, payment.LastEventId);
        Assert.Contains(app.Logs.Lines, line => line.Contains($"Attempt 1 for {eventId} failed; retrying in", StringComparison.Ordinal));
        Assert.Contains(app.Logs.Lines, line => line.Contains($"Attempt 2 for {eventId} failed; retrying in", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandlerThatKeepsFailingEndsFailedAfterMaxAttempts()
    {
        var eventId = StripeEvents.NewId("evt");
        var paymentIntentId = StripeEvents.NewId("pi");
        var json = StripeEvents.PaymentIntentSucceeded(eventId, paymentIntentId, 2_000, "usd", StripeEvents.BaseCreated);
        app.Faults.FailAlways(eventId);

        using var response = await app.PostSignedAsync(json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var failed = await app.WaitUntilHandledAsync(eventId, NoPaymentWhilePending(paymentIntentId));

        Assert.Equal(EventStatus.Failed, failed.Status);
        Assert.Null(failed.Outcome);
        Assert.Equal(RetryingAppFactory.MaxAttempts, failed.Attempts);
        Assert.Contains("Injected handler failure", failed.LastError, StringComparison.Ordinal);
        Assert.Null(await app.FindPaymentAsync(paymentIntentId));
        Assert.Contains(app.Logs.Lines, line => line.StartsWith("Error: ", StringComparison.Ordinal)
            && line.Contains($"Giving up on {eventId} after {RetryingAppFactory.MaxAttempts} attempts", StringComparison.Ordinal));

        // Stripe got its 200 long ago; a late redelivery is still acknowledged as a duplicate.
        using var redelivery = await app.PostSignedAsync(json);
        Assert.Equal(HttpStatusCode.OK, redelivery.StatusCode);
        Assert.Equal("duplicate", await redelivery.ReadAckStatusAsync());

        // Several sweep intervals later, nothing has picked the Failed row up again.
        await Task.Delay(TimeSpan.FromMilliseconds(400));
        await app.DrainAsync();
        Assert.Equal(RetryingAppFactory.MaxAttempts, app.HandlerCalls.For(eventId));
        Assert.Equal(EventStatus.Failed, (await app.FindEventAsync(eventId))!.Status);
    }

    [Fact]
    public async Task AcknowledgedButUnprocessedEventIsRecoveredBySweeper()
    {
        var eventId = StripeEvents.NewId("evt");
        var paymentIntentId = StripeEvents.NewId("pi");
        var json = StripeEvents.PaymentIntentSucceeded(eventId, paymentIntentId, 4_200, "eur", StripeEvents.BaseCreated);

        // Simulate a crash after the 200: the row is stored as Pending but never reached the queue.
        await app.InsertPendingAsync(eventId, json, nextAttemptAtUtc: DateTime.UtcNow.AddMinutes(-5));

        var processed = await app.WaitUntilHandledAsync(eventId);

        Assert.Equal(EventOutcome.Applied, processed.Outcome);
        Assert.Equal(1, app.HandlerCalls.For(eventId));
        var payment = await app.FindPaymentAsync(paymentIntentId);
        Assert.NotNull(payment);
        Assert.Equal(4_200, payment.Amount);
        Assert.Equal("eur", payment.Currency);
    }

    /// <summary>While the event is Pending, the payment it creates must not exist yet: no partial effect.</summary>
    private Func<Task<Action>> NoPaymentWhilePending(string paymentIntentId) => async () =>
    {
        var payment = await app.FindPaymentAsync(paymentIntentId);
        return () => Assert.Null(payment);
    };
}
