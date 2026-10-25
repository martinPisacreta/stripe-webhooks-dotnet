using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StripeWebhooks.Api.Data;
using StripeWebhooks.Api.Webhooks;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOptions<StripeWebhookOptions>()
    .BindConfiguration(StripeWebhookOptions.SectionName)
    .ValidateDataAnnotations()
    .Validate(
        o => o.WebhookSecret.StartsWith("whsec_", StringComparison.Ordinal),
        "Stripe:WebhookSecret must be an endpoint signing secret (whsec_...). Set the Stripe__WebhookSecret environment variable.")
    .Validate(
        o => o.SweepInterval > TimeSpan.Zero && o.RetryBaseDelay > TimeSpan.Zero && o.RetryMaxDelay >= o.RetryBaseDelay,
        "Stripe:SweepInterval and Stripe:RetryBaseDelay must be positive, and Stripe:RetryMaxDelay at least RetryBaseDelay.")
    .ValidateOnStart();

builder.Services.AddDbContext<PaymentsDbContext>((services, db) => db
    .UseSqlite(services.GetRequiredService<IConfiguration>().GetConnectionString("Payments"))
    .AddInterceptors(new SqliteBusyTimeout()));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<EventQueue>();
builder.Services.AddScoped<WebhookReceiver>();
builder.Services.AddScoped<EventProcessor>();
builder.Services.AddScoped<IPaymentEventHandler, PaymentEventHandler>();
builder.Services.AddHostedService<EventWorker>();
builder.Services.AddHostedService<PendingEventSweeper>();

builder.Services.ConfigureHttpJsonOptions(json =>
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

// Fail fast with a clear message if the webhook secret is missing, before touching the database.
_ = app.Services.GetRequiredService<IOptions<StripeWebhookOptions>>().Value;

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
    db.Database.EnsureCreated(); // Sample only. A real service would apply EF Core migrations.
    db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;"); // Readers do not block the writer.
}

app.MapPost("/webhooks/stripe", (HttpRequest request, WebhookReceiver receiver, CancellationToken ct) =>
    receiver.ReceiveAsync(request, ct));

if (app.Environment.IsDevelopment())
{
    // Local inspection only (unauthenticated): see what the webhooks did to a payment.
    app.MapGet("/payments/{paymentIntentId}", async (string paymentIntentId, PaymentsDbContext db, CancellationToken ct) =>
        await db.Payments.AsNoTracking().SingleOrDefaultAsync(p => p.PaymentIntentId == paymentIntentId, ct)
            is { } payment
            ? Results.Ok(payment)
            : Results.NotFound());
}

app.Run();

/// <summary>Exposed for WebApplicationFactory in the test project.</summary>
public partial class Program
{
}
