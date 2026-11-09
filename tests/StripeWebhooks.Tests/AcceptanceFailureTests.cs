using System.Net;
using Microsoft.EntityFrameworkCore;
using StripeWebhooks.Tests.Infrastructure;

namespace StripeWebhooks.Tests;

public sealed class AcceptanceFailureTests(WebhookAppFactory app) : IClassFixture<WebhookAppFactory>
{
    [Fact]
    public async Task StorageFailureIsNotAcknowledgedAndTheRetryIsAccepted()
    {
        var eventId = StripeEvents.NewId("evt");
        var json = StripeEvents.PaymentIntentSucceeded(eventId, StripeEvents.NewId("pi"), 2_000, "usd", StripeEvents.BaseCreated);

        // Make every insert into the inbox fail, as a locked or full database would.
        await app.QueryAsync(db => db.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER fail_inbox BEFORE INSERT ON ProcessedEvents BEGIN SELECT RAISE(ABORT, 'simulated storage failure'); END;"));
        using var failed = await app.PostSignedAsync(json);

        await app.QueryAsync(db => db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_inbox;"));
        var rowsAfterFailure = await app.CountEventRowsAsync(eventId);

        // Stripe's retry of the same event, with a fresh signature timestamp.
        using var retry = await app.PostSignedAsync(json);
        await app.WaitUntilHandledAsync(eventId);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal(0, rowsAfterFailure);
        Assert.Contains(app.Logs.Lines, line => line.StartsWith("Error:", StringComparison.Ordinal) && line.Contains(eventId, StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal("accepted", await retry.ReadAckStatusAsync());
        Assert.Equal(1, app.HandlerCalls.For(eventId));
    }
}
