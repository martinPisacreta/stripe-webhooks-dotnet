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
            payment.Property(p => p.LastEventId).HasMaxLength(255);
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
            evt.HasIndex(e => new { e.Status, e.ReceivedAtUtc });
        });
    }

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        v => v,
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
}
