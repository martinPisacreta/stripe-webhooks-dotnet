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
        await app.DrainAsync(); // Anything the retry might have enqueued has been processed by now.

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
        // A check-then-insert race does not lose every time, so give it many chances to show up.
        const int rounds = 10;
        const int deliveries = 25;

        for (var round = 0; round < rounds; round++)
        {
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
            await app.DrainAsync();

            Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
            Assert.Single(results, r => r.Ack == "accepted");
            Assert.Equal(deliveries - 1, results.Count(r => r.Ack == "duplicate"));
            Assert.Equal(1, await app.CountEventRowsAsync(eventId));
            Assert.Equal(1, app.HandlerCalls.For(eventId));
        }
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
    public async Task ConcurrentClaimsOfOnePendingEventApplyItOnce()
    {
        var eventId = StripeEvents.NewId("evt");
        var paymentIntentId = StripeEvents.NewId("pi");
        var json = StripeEvents.PaymentIntentSucceeded(eventId, paymentIntentId, 2_000, "usd", StripeEvents.BaseCreated);

        // Pending and not due yet, so the sweeper leaves it alone and only the processors below race.
        await app.InsertPendingAsync(eventId, json, nextAttemptAtUtc: DateTime.UtcNow.AddHours(1));

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claims = Enumerable.Range(0, 10).Select(async _ =>
        {
            await start.Task;
            await app.ProcessAgainAsync(eventId);
        }).ToArray();
        start.SetResult();
        await Task.WhenAll(claims);

        var row = await app.FindEventAsync(eventId);
        Assert.NotNull(row);
        Assert.Equal(EventStatus.Processed, row.Status);
        Assert.Equal(EventOutcome.Applied, row.Outcome);
        Assert.Equal(1, app.HandlerCalls.For(eventId));
        Assert.NotNull(await app.FindPaymentAsync(paymentIntentId));
    }
}
