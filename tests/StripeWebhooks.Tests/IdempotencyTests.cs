using System.Net;
using StripeWebhooks.Api.Data;
using StripeWebhooks.Tests.Infrastructure;

namespace StripeWebhooks.Tests;

public sealed class IdempotencyTests(WebhookAppFactory app) : IClassFixture<WebhookAppFactory>
{
    [Fact]
    public async Task DuplicateDeliveryIsProcessedOnce()
    {
        var eventId = StripeEvents.NewId("evt");
        var json = StripeEvents.PaymentIntentSucceeded(eventId, StripeEvents.NewId("pi"), 2_000, "usd", StripeEvents.BaseCreated);

        using var first = await app.PostSignedAsync(json);
        await app.WaitUntilHandledAsync(eventId);

        // A Stripe retry carries the same body with a fresh signature timestamp.
        using var retry = await app.PostSignedAsync(json);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("accepted", await first.ReadAckStatusAsync());
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal("duplicate", await retry.ReadAckStatusAsync());
        Assert.Equal(1, await app.CountEventRowsAsync(eventId));
        Assert.Equal(1, app.HandlerCalls.For(eventId));
    }

    [Fact]
    public async Task ConcurrentDuplicatesAreProcessedOnce()
    {
        const int deliveries = 25;
        var eventId = StripeEvents.NewId("evt");
        var json = StripeEvents.PaymentIntentSucceeded(eventId, StripeEvents.NewId("pi"), 2_000, "usd", StripeEvents.BaseCreated);

        // Release every request at once so the inserts race each other.
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = Enumerable.Range(0, deliveries).Select(async _ =>
        {
            await start.Task;
            using var response = await app.PostSignedAsync(json);
            return (response.StatusCode, Ack: await response.ReadAckStatusAsync());
        }).ToArray();

        start.SetResult();
        var results = await Task.WhenAll(requests);
        await app.WaitUntilHandledAsync(eventId);

        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Single(results, r => r.Ack == "accepted");
        Assert.Equal(deliveries - 1, results.Count(r => r.Ack == "duplicate"));
        Assert.Equal(1, await app.CountEventRowsAsync(eventId));
        Assert.Equal(1, app.HandlerCalls.For(eventId));
    }

    [Fact]
    public async Task WorkerSeeingTheSameEventAgainDoesNotReapplyIt()
    {
        var eventId = StripeEvents.NewId("evt");
        var json = StripeEvents.PaymentIntentSucceeded(eventId, StripeEvents.NewId("pi"), 2_000, "usd", StripeEvents.BaseCreated);

        using var response = await app.PostSignedAsync(json);
        await app.WaitUntilHandledAsync(eventId);

        // What the sweeper or a restart would do: hand an already processed id to the processor again.
        await app.ProcessAgainAsync(eventId);
        await app.ProcessAgainAsync(eventId);

        Assert.Equal(1, app.HandlerCalls.For(eventId));
    }

    [Fact]
    public async Task AcknowledgedButUnprocessedEventIsRecoveredBySweeper()
    {
        var eventId = StripeEvents.NewId("evt");
        var paymentIntentId = StripeEvents.NewId("pi");
        var json = StripeEvents.PaymentIntentSucceeded(eventId, paymentIntentId, 4_200, "eur", StripeEvents.BaseCreated);

        // Simulate a crash after the 200: the row is stored as Pending but never reached the queue.
        await app.QueryAsync(async db =>
        {
            db.ProcessedEvents.Add(new ProcessedEvent
            {
                EventId = eventId,
                Type = "payment_intent.succeeded",
                EventCreated = StripeEvents.BaseCreated,
                Payload = json,
                Status = EventStatus.Pending,
                ReceivedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            });
            return await db.SaveChangesAsync();
        });

        var processed = await app.WaitUntilHandledAsync(eventId);

        Assert.Equal(EventOutcome.Applied, processed.Outcome);
        Assert.Equal(1, app.HandlerCalls.For(eventId));
        var payment = await app.FindPaymentAsync(paymentIntentId);
        Assert.NotNull(payment);
        Assert.Equal(4_200, payment.Amount);
        Assert.Equal("eur", payment.Currency);
    }
}
