using System.Text.Json.Nodes;
using Stripe;

namespace StripeWebhooks.Tests.Infrastructure;

/// <summary>Minimal snapshot-event payloads with the fields the handlers read.</summary>
public static class StripeEvents
{
    public const long BaseCreated = 1_760_000_000; // a fixed Unix time; only the ordering matters

    public static string NewId(string prefix) => $"{prefix}_test_{Guid.NewGuid():N}";

    public static string PaymentIntentSucceeded(string eventId, string paymentIntentId, long amount, string currency, long created) =>
        Event(eventId, EventTypes.PaymentIntentSucceeded, created, new JsonObject
        {
            ["id"] = paymentIntentId,
            ["object"] = "payment_intent",
            ["amount"] = amount,
            ["amount_received"] = amount,
            ["currency"] = currency,
            ["status"] = "succeeded",
        });

    public static string ChargeRefunded(
        string eventId, string paymentIntentId, long amount, long amountRefunded, string currency, long created) =>
        Event(eventId, EventTypes.ChargeRefunded, created, new JsonObject
        {
            ["id"] = "ch_" + paymentIntentId,
            ["object"] = "charge",
            ["amount"] = amount,
            ["amount_captured"] = amount,
            ["amount_refunded"] = amountRefunded,
            ["currency"] = currency,
            ["payment_intent"] = paymentIntentId,
            ["refunded"] = amountRefunded >= amount,
            ["status"] = "succeeded",
        });

    public static string Event(string eventId, string type, long created, JsonObject dataObject) =>
        new JsonObject
        {
            ["id"] = eventId,
            ["object"] = "event",
            ["api_version"] = StripeConfiguration.ApiVersion,
            ["created"] = created,
            ["livemode"] = false,
            ["pending_webhooks"] = 1,
            ["request"] = new JsonObject { ["id"] = null, ["idempotency_key"] = null },
            ["type"] = type,
            ["data"] = new JsonObject { ["object"] = dataObject },
        }.ToJsonString();
}
