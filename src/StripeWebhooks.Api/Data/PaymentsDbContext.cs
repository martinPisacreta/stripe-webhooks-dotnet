using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace StripeWebhooks.Api.Data;

public sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : DbContext(options)
{
    public DbSet<Payment> Payments => Set<Payment>();

    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Every DateTime here is UTC; SQLite stores text without a kind, so restore it on read.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Payment>(payment =>
        {
            payment.HasKey(p => p.PaymentIntentId);
            payment.Property(p => p.PaymentIntentId).HasMaxLength(255);
            payment.Property(p => p.Currency).HasMaxLength(3);
            payment.Property(p => p.Status).HasConversion<string>().HasMaxLength(32);

            // Optimistic concurrency: the UPDATE only matches if no other event was applied since this
            // row was read (WHERE ... AND LastEventId = @original). On a database that lets two workers
            // interleave (READ COMMITTED), the loser gets DbUpdateConcurrencyException and is retried,
            // so the stale check cannot be bypassed. SQLite serializes writers, so it never fires there.
            payment.Property(p => p.LastEventId).HasMaxLength(255).IsConcurrencyToken();
        });

        modelBuilder.Entity<ProcessedEvent>(evt =>
        {
            evt.HasKey(e => e.Id);
            evt.Property(e => e.EventId).HasMaxLength(255);
            evt.HasIndex(e => e.EventId).IsUnique();
            evt.Property(e => e.Type).HasMaxLength(255);
            evt.Property(e => e.Status).HasConversion<string>().HasMaxLength(32);
            evt.Property(e => e.Outcome).HasConversion<string>().HasMaxLength(32);
            evt.Property(e => e.LastError).HasMaxLength(1000);
            evt.HasIndex(e => new { e.Status, e.NextAttemptAtUtc });
        });
    }

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        v => v,
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
}
