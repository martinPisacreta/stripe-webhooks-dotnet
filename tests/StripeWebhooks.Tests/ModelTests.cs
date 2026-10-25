using Microsoft.EntityFrameworkCore;
using StripeWebhooks.Api.Data;

namespace StripeWebhooks.Tests;

/// <summary>
/// The guarantees that do not depend on SQLite's single writer live in the model. These pin them,
/// because a timing-based test cannot prove their absence every time.
/// </summary>
public sealed class ModelTests
{
    [Fact]
    public void EventIdHasAUniqueIndex()
    {
        using var db = CreateContext();
        var entity = db.Model.FindEntityType(typeof(ProcessedEvent))!;

        var index = Assert.Single(entity.GetIndexes(), i => i.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(ProcessedEvent.EventId) }));
        Assert.True(index.IsUnique);
    }

    [Fact]
    public void PaymentUpdatesAreGuardedByLastEventId()
    {
        using var db = CreateContext();
        var entity = db.Model.FindEntityType(typeof(Payment))!;

        Assert.True(entity.FindProperty(nameof(Payment.LastEventId))!.IsConcurrencyToken);
        Assert.Equal(new[] { nameof(Payment.PaymentIntentId) }, entity.FindPrimaryKey()!.Properties.Select(p => p.Name));
    }

    private static PaymentsDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>().UseSqlite("Data Source=:memory:").Options);
}
