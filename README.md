# Stripe webhooks in .NET, applied exactly once

A small ASP.NET Core (.NET 8) service that receives Stripe webhooks and updates a `Payments` table
safely when Stripe does what it is documented to do: deliver the same event more than once, retry
for up to three days when your endpoint fails or is slow (so a retry can arrive while the first
attempt is still running), and deliver events out of order.

A naive handler ("if we haven't seen this payment, fulfill the order") breaks under exactly those
conditions: two concurrent retries both pass the check and the order ships twice, or a late
`payment_intent.succeeded` overwrites a refund that already happened. This repo shows one way to
make that impossible, with tests that reproduce each failure mode.

## What it guarantees

| Behavior | How | Test |
|---|---|---|
| Only signed requests are accepted | `EventUtility.ConstructEvent` on the raw body, 300 s tolerance | `InvalidSignatureIsRejectedWith400AndNothingIsStored` (wrong secret, modified body, old timestamp, malformed or missing header) |
| A retry is acknowledged, not reprocessed | Unique index on the Stripe event id, insert first | `DuplicateDeliveryIsProcessedOnce` |
| Concurrent duplicates apply once | Same insert; the database picks one winner | `ConcurrentDuplicatesAreProcessedOnce` (25 parallel requests) |
| The worker never applies an event twice | Conditional claim in the same transaction as the update | `WorkerSeeingTheSameEventAgainDoesNotReapplyIt` |
| A 200 is never lost on a crash | Event stored before the 200; pending rows re-queued | `AcknowledgedButUnprocessedEventIsRecoveredBySweeper` |
| Late events don't overwrite newer state | `event.created` compared per payment | `OutOfOrderEventIsIgnored`, `OlderRefundSnapshotDoesNotOverwriteNewerOne` |
| Refunds update the payment | `charge.refunded` sets cumulative `amount_refunded` | `RefundUpdatesPaymentState` |
| Unknown types don't fail | Logged, marked processed, 200 | `UnknownEventTypeIsAcknowledgedAndLogged` |

## How it works

```
 Stripe                 POST /webhooks/stripe                          SQLite
   |
   |  event  ------->   1. verify Stripe-Signature (HMAC-SHA256, 300 s)
   |                       bad / missing / expired ---------> 400
   |                    2. INSERT INTO ProcessedEvents ... --------->  ProcessedEvents
   |                       ON CONFLICT (EventId) DO NOTHING            UNIQUE (EventId)
   |                       0 rows (retry or duplicate) -----> 200 {"status":"duplicate"}
   |                    3. TryWrite(event id) to a bounded Channel
   |  <-------------    4. 200 {"status":"accepted"}
   |
   |                    Channel<string> (bounded)  <-----  PendingEventSweeper
   |                            |                          on start and every 30 s:
   |                            v                          re-queues Pending rows
   |                    EventWorker -> EventProcessor, one transaction:
   |                      a. UPDATE ProcessedEvents SET Status = 'Processed'
   |                         WHERE EventId = @id AND Status = 'Pending'    0 rows -> skip
   |                      b. event.created < Payments.LastEventCreated  -> IgnoredStale
   |                      c. apply payment_intent.succeeded / charge.refunded -> Payments
   |                      d. COMMIT   (claim and state change land together, or neither)
```

The request path does three cheap things: verify, insert, enqueue. Business logic runs in the
background so the 2xx goes back quickly, which is what Stripe asks for.

## Run the tests

Requires the .NET 8 runtime and the .NET 8 SDK or newer (checked with SDK 9 and 10).

```bash
dotnet test
```

The tests start the real app in memory with `WebApplicationFactory`, each test class against its own
SQLite file, with a webhook secret generated per run. Signatures are built by a small helper that
implements Stripe's scheme independently of Stripe.net
([`StripeSignature.cs`](tests/StripeWebhooks.Tests/Infrastructure/StripeSignature.cs)), so the SDK
call is checked rather than trusted. A counting wrapper around the payment handler is how the tests
prove "processed once" instead of only checking final state.

## Try it with the Stripe CLI

```bash
stripe login
stripe listen --forward-to localhost:5080/webhooks/stripe
# prints: Your webhook signing secret is whsec_...
```

In a second terminal:

```bash
export Stripe__WebhookSecret=whsec_...          # PowerShell: $env:Stripe__WebhookSecret = "whsec_..."
dotnet run --project src/StripeWebhooks.Api      # listens on http://localhost:5080
```

In a third:

```bash
stripe trigger payment_intent.succeeded
stripe trigger charge.refunded
curl http://localhost:5080/payments/pi_...       # Development only: what the webhooks did
```

The secret is read from configuration (`Stripe:WebhookSecret`); the app refuses to start without a
`whsec_` value. Nothing secret lives in the repo.

**API version.** Stripe.net 53.0.0 is pinned to API version `2026-09-30.endive`. `ConstructEvent`
rejects events from a different major release, and the endpoint answers those with 400 and a log
line that says why, so Stripe keeps retrying while you fix it. `stripe listen` sends events in your
account's default API version; if that is an older release, run `stripe listen --latest ...`, and for
a real endpoint create it with the version the SDK expects.

## Design notes

- **Insert first, not "check then insert".** A `SELECT` followed by an `INSERT` lets two concurrent
  deliveries both see "not found". The unique index makes the insert itself the decision, and
  `ON CONFLICT DO NOTHING` returns 0 rows to the loser without an exception.
- **Durable before the 200.** The payload is stored before the acknowledgment. The `Channel` only
  carries ids, so losing it (restart, crash, full queue) delays processing but loses nothing: the
  sweeper re-queues pending rows on startup and on a timer.
- **At-least-once into the worker, exactly-once effect.** The worker can see an id more than once.
  The claim (`UPDATE ... WHERE Status = 'Pending'`) runs in the same transaction as the `Payments`
  change, so either both commit or neither does. If the handler throws, the claim rolls back and
  the event is retried, up to `MaxAttempts` (5), then marked `Failed` for a manual replay
  (set `Status` back to `Pending`).
- **Ordering.** Stripe does not guarantee order. Each event carries a snapshot of the object, so the
  newest `created` per payment wins and older events are recorded as `IgnoredStale`. `created` has
  one-second resolution; on a tie the larger cumulative `amount_refunded` wins. Status is derived
  from amounts, so a success can never "un-refund" a payment.
- **Backpressure.** The queue is bounded (`QueueCapacity`, 1000). `TryWrite` never blocks a request;
  if the queue is full the row simply waits for the sweeper.
- **Amounts** are `long` in the currency's minor unit with the currency code next to them, exactly
  as Stripe sends them. No floating point, no implicit currency.

## Limits

This is a focused sample, not a framework. Things a production service would add or change:

- **Database.** SQLite with `EnsureCreated` keeps the sample self-contained. Use PostgreSQL or SQL
  Server with migrations. `ON CONFLICT` works on PostgreSQL; on SQL Server catch unique-key errors
  2627/2601 or use `MERGE ... WITH (HOLDLOCK)`.
- **Scale-out.** One worker per process. With several instances on a shared database the claim still
  applies each event once, but two different events for the same payment could be applied at the
  same time; lock the payment row (`SELECT ... FOR UPDATE`, `UPDLOCK`) or add a concurrency token.
- **External side effects** (emails, fulfillment APIs) cannot join the database transaction. Write
  them to an outbox in the same transaction, or pass the Stripe event id as the idempotency key of
  the downstream call.
- **Event coverage.** Only `payment_intent.succeeded` and `charge.refunded`. Refund failures
  (`charge.refund.updated`) and disputes need their own handling. Only snapshot events, not v2
  thin events.
- **Retention.** `ProcessedEvents` keeps every payload, which can include customer data. Add a
  retention job, keeping dedupe rows at least as long as Stripe can retry.
- **A 200 means "stored", not "processed".** Monitor `Pending` age and `Failed` rows.
- `GET /payments/{id}` has no auth and is mapped only in Development.

## Layout

```
src/StripeWebhooks.Api/
  Program.cs                      composition, endpoint mapping
  Webhooks/WebhookReceiver.cs     verify, insert-first dedupe, enqueue, ack
  Webhooks/EventProcessor.cs      claim + handler + commit in one transaction, retries
  Webhooks/PaymentEventHandler.cs payment_intent.succeeded, charge.refunded, stale check
  Webhooks/EventQueue.cs          bounded Channel<string>
  Webhooks/EventWorker.cs         background consumer
  Webhooks/PendingEventSweeper.cs crash recovery and retries
  Data/                           EF Core model: Payments, ProcessedEvents
tests/StripeWebhooks.Tests/       xUnit + WebApplicationFactory
```

## License

MIT, see [LICENSE](LICENSE).
