using Microsoft.EntityFrameworkCore;
using Stripe;
using StripeWebhooks.Api.Data;

namespace StripeWebhooks.Api.Webhooks;

public sealed class PaymentEventHandler(
    PaymentsDbContext db,
    TimeProvider clock,
    ILogger<PaymentEventHandler> logger) : IPaymentEventHandler
{
    public async Task<EventOutcome> HandleAsync(Event stripeEvent, CancellationToken cancellationToken)
    {
        switch (stripeEvent.Data.Object)
        {
            case PaymentIntent intent when stripeEvent.Type == EventTypes.PaymentIntentSucceeded:
                return await ApplyAsync(stripeEvent, intent.Id, (payment, _) =>
                {
                    payment.Amount = intent.AmountReceived;
                    payment.Currency = intent.Currency;
                    // A PaymentIntent snapshot carries no refund data, so refunds already recorded stay.
                }, cancellationToken);

            case Charge charge when stripeEvent.Type == EventTypes.ChargeRefunded:
                if (string.IsNullOrEmpty(charge.PaymentIntentId))
                {
                    logger.NoPaymentIntent(stripeEvent.Id, charge.Id);
                    return EventOutcome.IgnoredNoPaymentIntent;
                }

                return await ApplyAsync(stripeEvent, charge.PaymentIntentId, (payment, sameSecond) =>
                {
                    payment.Amount = charge.AmountCaptured;
                    payment.Currency = charge.Currency;
                    // amount_refunded is cumulative. Two events created in the same second cannot be
                    // ordered by `created`, so keep the larger value instead of trusting arrival order.
                    payment.AmountRefunded = sameSecond
                        ? Math.Max(payment.AmountRefunded, charge.AmountRefunded)
                        : charge.AmountRefunded;
                }, cancellationToken);

            default:
                logger.UnhandledType(stripeEvent.Type, stripeEvent.Id);
                return EventOutcome.IgnoredUnhandledType;
        }
    }

    private async Task<EventOutcome> ApplyAsync(
        Event stripeEvent,
        string paymentIntentId,
        Action<Payment, bool> apply,
        CancellationToken cancellationToken)
    {
        var created = new DateTimeOffset(stripeEvent.Created, TimeSpan.Zero).ToUnixTimeSeconds();
        var payment = await db.Payments.SingleOrDefaultAsync(p => p.PaymentIntentId == paymentIntentId, cancellationToken);

        // Stripe does not guarantee delivery order. Each event carries a snapshot of the object, so the
        // newest `created` wins and anything older than what we already applied is ignored.
        if (payment is not null && created < payment.LastEventCreated)
        {
            logger.StaleEvent(stripeEvent.Type, stripeEvent.Id, paymentIntentId, created, payment.LastEventCreated);
            return EventOutcome.IgnoredStale;
        }

        var sameSecond = payment is not null && created == payment.LastEventCreated;
        if (payment is null)
        {
            // The first event seen for a payment can be a refund when deliveries arrive out of order.
            payment = new Payment { PaymentIntentId = paymentIntentId };
            db.Payments.Add(payment);
        }

        apply(payment, sameSecond);
        payment.Status = DeriveStatus(payment);
        payment.LastEventCreated = created;
        payment.LastEventId = stripeEvent.Id;
        payment.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        return EventOutcome.Applied;
    }

    private static PaymentStatus DeriveStatus(Payment payment) => payment.AmountRefunded switch
    {
        <= 0 => PaymentStatus.Succeeded,
        var refunded when refunded >= payment.Amount => PaymentStatus.Refunded,
        _ => PaymentStatus.PartiallyRefunded,
    };
}
