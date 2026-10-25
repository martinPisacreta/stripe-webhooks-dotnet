using Stripe;
using StripeWebhooks.Api.Data;

namespace StripeWebhooks.Api.Webhooks;

/// <summary>
/// Applies one verified event. Implementations stage changes on the scoped
/// <see cref="PaymentsDbContext"/> and do not call SaveChanges: <see cref="EventProcessor"/> commits
/// them in the same transaction that marks the event as processed.
/// </summary>
public interface IPaymentEventHandler
{
    Task<EventOutcome> HandleAsync(Event stripeEvent, CancellationToken cancellationToken);
}
