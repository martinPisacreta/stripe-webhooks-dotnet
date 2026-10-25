using System.Net;
using StripeWebhooks.Api.Data;
using StripeWebhooks.Tests.Infrastructure;

namespace StripeWebhooks.Tests;

public sealed class BackpressureTests(BackpressureAppFactory app) : IClassFixture<BackpressureAppFactory>
{
    [Fact]
    public async Task FullQueueStillAcknowledgesAndEveryEventIsProcessedOnce()
    {
        var eventIds = Enumerable.Range(0, 3).Select(_ => StripeEvents.NewId("evt")).ToArray();

        // The worker is held back and the queue holds one id: the first delivery fills it, the other
        // two find it full. All three must still be acknowledged right away.
        foreach (var eventId in eventIds)
        {
            using var response = await app.PostSignedAsync(
                StripeEvents.PaymentIntentSucceeded(eventId, StripeEvents.NewId("pi"), 1_000, "usd", StripeEvents.BaseCreated));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("accepted", await response.ReadAckStatusAsync());
        }

        Assert.DoesNotContain(app.Logs.Lines, line => IsQueueFull(line, eventIds[0]));
        Assert.Contains(app.Logs.Lines, line => IsQueueFull(line, eventIds[1]));
        Assert.Contains(app.Logs.Lines, line => IsQueueFull(line, eventIds[2]));
        foreach (var eventId in eventIds)
        {
            Assert.Equal(EventStatus.Pending, (await app.FindEventAsync(eventId))!.Status);
        }

        // Once the worker runs, the first id comes from the queue and the sweeper feeds in the rest.
        app.OpenWorkerGate();
        foreach (var eventId in eventIds)
        {
            Assert.Equal(EventOutcome.Applied, (await app.WaitUntilHandledAsync(eventId)).Outcome);
        }

        await app.DrainAsync();
        Assert.All(eventIds, eventId => Assert.Equal(1, app.HandlerCalls.For(eventId)));
    }

    private static bool IsQueueFull(string line, string eventId) =>
        line.Contains("Queue full", StringComparison.Ordinal) && line.Contains(eventId, StringComparison.Ordinal);
}
