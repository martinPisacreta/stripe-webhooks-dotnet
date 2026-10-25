# Idempotent Stripe webhooks in ASP.NET Core

A small ASP.NET Core service (targets .NET 10 and .NET 8) that receives Stripe webhooks and updates a
`Payments` table safely when Stripe does what it is documented to do: deliver the same event more
than once, retry for up to three days when your endpoint fails or is slow (so a retry can arrive
while the first attempt is still running), and deliver events out of order.

A naive handler ("if we haven't seen this payment, fulfill the order") breaks under exactly those
conditions: two concurrent retries both pass the check and the order ships twice, or a late
`payment_intent.succeeded` overwrites a refund that already happened. This repo shows one way to
prevent that, within the limits listed below, with tests for the main failure modes.

## What it guarantees

| Behavior | How | Test |
|---|---|---|
| Only signed requests are accepted | `EventUtility.ConstructEvent` on the raw body, 300 s tolerance | `ValidSignatureIsAcceptedAndApplied`, `InvalidSignatureIsRejectedWith400AndNothingIsStored` (wrong secret, modified body, old or future timestamp, `v0` only, malformed or missing header) |
| A secret roll keeps working | Any matching `v1` entry is accepted | `HeaderSignedWithOldAndNewSecretIsAcceptedDuringASecretRoll` |
| A wrong API version says so | Checked after the signature, with its own 400 error | `CorrectlySignedEventFromAnotherApiReleaseGetsItsOwnError` |
| Oversized bodies are not read | 1 MB cap before verification, 413 | `OversizedBodyIsRejectedWith413AndNothingIsStored` (with and without `Content-Length`) |
| A retry is acknowledged, not reprocessed | Unique index on the Stripe event id, insert first | `DuplicateDeliveryIsProcessedOnce`, `EventIdHasAUniqueIndex` |
| Concurrent duplicates apply once | Same insert; the database picks one winner | `ConcurrentDuplicatesAreProcessedOnce` (10 rounds of 25 parallel requests) |
| The worker never applies an event twice | Conditional claim in the same transaction as the update | `WorkerSeeingTheSameEventAgainDoesNotReapplyIt`, `ConcurrentClaimsOfOnePendingEventApplyItOnce` |
| A handler error is retried, never half-applied | Rollback undoes claim and payment change; exponential backoff | `FailingHandlerIsRolledBackAndRetriedUntilItSucceeds`, `HandlerThatKeepsFailingEndsFailedAfterMaxAttempts`, `DefaultBackoffGrowsFourfoldAndIsCapped` |
| An acknowledged event survives a crash | Event stored before the 200; pending rows re-queued | `AcknowledgedButUnprocessedEventIsRecoveredBySweeper` |
| A full queue still acknowledges at once | `TryWrite` never blocks; the sweeper picks the row up | `FullQueueStillAcknowledgesAndEveryEventIsProcessedOnce` |
| Late events don't overwrite newer state | `event.created` compared per payment | `OutOfOrderEventIsIgnored`, `OlderRefundSnapshotDoesNotOverwriteNewerOne`, `SameSecondRefundsKeepTheLargerCumulativeAmount`, `SuccessFromTheSameSecondAsAFullRefundDoesNotUndoIt` |
| Refunds update the payment | `charge.refunded` sets cumulative `amount_refunded` | `RefundUpdatesPaymentState` |
| Unknown types don't fail | Logged, marked processed, 200 | `UnknownEventTypeIsAcknowledgedAndLogged` |

## How it works

```
 Stripe                 POST /webhooks/stripe                          SQLite
   |
   |  event  ------->   1. body over 1 MB ----------------> 413
   |                    2. verify Stripe-Signature (HMAC-SHA256, 300 s)
   |                       bad / missing / expired ---------> 400 invalid signature
   |                       signed, other API release ------> 400 unsupported api_version
   |                    3. INSERT INTO ProcessedEvents ... --------->  ProcessedEvents (inbox)
   |                       ON CONFLICT (EventId) DO NOTHING            UNIQUE (EventId)
   |                       0 rows (retry or duplicate) -----> 200      Pending -> Processed
   |                                                    {"status":"duplicate"}   | Failed
   |                    4. TryWrite(event id) to a bounded Channel
   |  <-------------    5. 200 {"status":"accepted"}
   |
   |                    Channel<string> (bounded)  <-----  PendingEventSweeper
   |                            |                          on start and every 30 s:
   |                            v                          re-queues due Pending rows
   |                    EventWorker -> EventProcessor, one transaction:
   |                      a. UPDATE ProcessedEvents SET Status = 'Processed'
   |                         WHERE EventId = @id AND Status = 'Pending'    0 rows -> skip
   |                      b. event.created < Payments.LastEventCreated  -> IgnoredStale
   |                      c. apply payment_intent.succeeded / charge.refunded -> Payments
   |                      d. COMMIT   (claim and state change land together, or neither)
   |                      on error: ROLLBACK, Attempts + 1, retry after 30 s, 2 min,
   |                                8 min ... up to 6 h; Failed after 10 attempts
```

The request path does four cheap things: cap, verify, insert, enqueue. Business logic runs in the
background so the 2xx goes back quickly, which is what Stripe asks for.

## Run the tests

Requires the .NET 10 SDK.

```bash
dotnet test
```

The suite runs once per target framework: `net10.0`, and `net8.0` on the .NET 8 runtime if it is
installed (otherwise rolled forward to .NET 10). CI installs both runtimes.

The tests start the real app in memory with `WebApplicationFactory`, each test class against its own
SQLite file, with a webhook secret generated per run. Signatures are built by a small helper that
implements Stripe's scheme independently of Stripe.net
([`StripeSignature.cs`](tests/StripeWebhooks.Tests/Infrastructure/StripeSignature.cs)), so the SDK
call is checked rather than trusted. A wrapper around the payment handler counts calls and can be
told to throw for chosen events, which is how the tests prove "processed once" and "rolled back"
instead of only checking final state. The default test host sweeps only at startup, so those tests
can pass only through the receiver's own enqueue; separate hosts cover retries (short delays) and a
full queue (capacity 1, worker held back).

SQLite serializes writers, so the concurrency tests exercise the insert-first dedupe and the claim,
but not a server database's locking. See [Limits](#limits).

## Try it with the Stripe CLI

```bash
stripe login
stripe listen --latest --forward-to localhost:5080/webhooks/stripe
# prints: Your webhook signing secret is whsec_...
```

In a second terminal:

```bash
export Stripe__WebhookSecret=whsec_...          # PowerShell: $env:Stripe__WebhookSecret = "whsec_..."
# or, for Development: dotnet user-secrets set Stripe:WebhookSecret whsec_... --project src/StripeWebhooks.Api
dotnet run --project src/StripeWebhooks.Api -f net10.0      # listens on http://localhost:5080
```

In a third:

```bash
stripe trigger payment_intent.succeeded
stripe trigger charge.refunded
curl http://localhost:5080/payments/pi_...       # Development only: what the webhooks did
```

The secret is read from configuration (`Stripe:WebhookSecret`); the app refuses to start without a
`whsec_` value. Nothing secret lives in the repo.

**API version.** Stripe.net 53.0.0 is pinned to API version `2026-09-30.endive` and only
deserializes events from the same release (any `*.endive` version). `--latest` makes the CLI send
events in the latest API version; without it the CLI uses your account's default, and an account on
an older release gets `400 {"error":"unsupported api_version ..."}` for every event. For a real
endpoint, create it with an `endive` API version. Stripe keeps retrying a 400, but it emails you
about an endpoint that keeps failing and can eventually disable it, so fix a mismatch quickly. If
Stripe has moved on to a newer release, upgrade Stripe.net.

**Rolling the secret.** When you roll an endpoint's secret in the Dashboard, Stripe signs each
delivery with both the old and the new secret until the old one expires (two `v1` entries in the
header). Switch `Stripe__WebhookSecret` to the new value any time before the old one expires. The
app holds one secret, so it serves one endpoint; a Connect endpoint has its own secret and needs its
own route or deployment.

## Design notes

- **Inbox.** `ProcessedEvents` works as a transactional inbox: every verified event lands there as
  `Pending` and moves to `Processed` or `Failed`.
- **Insert first, not "check then insert".** A `SELECT` followed by an `INSERT` lets two concurrent
  deliveries both see "not found". The unique index makes the insert itself the decision, and
  `ON CONFLICT DO NOTHING` returns 0 rows to the loser without an exception.
- **Durable before the 200.** The payload is stored before the acknowledgment. The `Channel` only
  carries ids, so losing it (restart, crash, full queue) delays processing but loses nothing: the
  sweeper re-queues due `Pending` rows on startup and on a timer.
- **At-least-once into the worker, exactly-once effect on the local database.** The worker can see
  an id more than once. The claim (`UPDATE ... WHERE Status = 'Pending'`) runs in the same
  transaction as the `Payments` change, so either both commit or neither does. If the handler
  throws, the rollback undoes the claim, `Attempts` goes up and `NextAttemptAtUtc` moves back:
  30 s, 2 min, 8 min, 32 min, 2 h 8 min, then every 6 h. After `MaxAttempts` (10, about a day) the
  row is `Failed`. Stripe already got a 200 for it and will not redeliver, so a `Failed` row needs a
  manual replay once the cause is fixed:
  `UPDATE ProcessedEvents SET Status = 'Pending', Attempts = 0, NextAttemptAtUtc = CURRENT_TIMESTAMP WHERE EventId = 'evt_...'`.
- **Ordering.** Stripe does not guarantee order. Each event carries a snapshot of the object, so the
  newest `created` per payment wins and strictly older events are recorded as `IgnoredStale`.
  `created` has one-second resolution; on a tie the larger cumulative `amount_refunded` wins. Status
  is derived from amounts, so a success can never "un-refund" a payment. The alternative is to
  ignore the snapshot and fetch the PaymentIntent from the Stripe API on each event: no ordering
  problem, at the cost of an API call per event and a worker that depends on Stripe being reachable.
- **Backpressure.** The queue is bounded (`QueueCapacity`, 1000). `TryWrite` never blocks a request;
  if the queue is full the row waits for the sweeper. The queue tracks which ids it holds, so the
  sweeper does not fill it with copies of work that is already on its way.
- **Request size.** Bodies over `MaxBodyBytes` (1 MB) get 413 before any HMAC is computed, checked
  on `Content-Length` and again while reading, for chunked bodies.
- **Amounts** are `long` in the currency's minor unit with the currency code next to them, exactly
  as Stripe sends them. No floating point, no implicit currency.

## Limits

This is a focused sample, not a framework. Things a production service would add or change:

- **Database.** SQLite with `EnsureCreated` keeps the sample self-contained; use PostgreSQL or SQL
  Server with migrations. SQLite has a single writer: EF Core's `BeginTransaction` maps to
  `BEGIN IMMEDIATE` (Serializable), so the claim, the stale check and the `Payments` update never
  interleave with another writer, and the tests run under that stronger guarantee. (A slow handler
  holds that lock too, which delays the receiver's inserts.) On PostgreSQL or SQL Server at READ
  COMMITTED, the unique index and the conditional `UPDATE` claim are still enough for exactly-once.
  The stale check alone is not, because two workers can read the same payment and both write. Here
  `LastEventId` is an optimistic concurrency token: the second `UPDATE ... WHERE LastEventId =
  @original` matches no row, EF throws `DbUpdateConcurrencyException`, and the event goes through
  the normal retry, which re-reads the payment and judges it again. A primary-key violation when two
  events create the same payment, and SQL Server SNAPSHOT update conflicts (error 3960), take the
  same path. The SQLite tests cannot produce these conflicts; `PaymentUpdatesAreGuardedByLastEventId`
  only pins the model, and there is no server-database job in CI. `ON CONFLICT` works on
  PostgreSQL; on SQL Server catch unique-key errors 2627/2601 or use `MERGE ... WITH (HOLDLOCK)`.
- **Scale-out.** One worker per process, events applied one at a time. Several instances on a
  shared server database still apply each event once (claim) and in order (concurrency token); the
  queue's id tracking is per process, so two sweepers may offer the same row and the claim absorbs it.
- **External side effects** (emails, fulfillment APIs) cannot join the database transaction. Write
  them to an outbox in the same transaction, or pass the Stripe event id as the idempotency key of
  the downstream call.
- **Event coverage.** Only `payment_intent.succeeded` and `charge.refunded`. Refund failures
  (`charge.refund.updated`) and disputes need their own handling. Only snapshot events, not v2
  thin events.
- **Retention.** `ProcessedEvents` keeps every payload, which can include customer data. Add a
  retention job, keeping dedupe rows at least as long as Stripe can retry.
- **A 200 means "stored", not "processed".** Monitor `Pending` age and `Failed` rows; a `Failed`
  row was acknowledged to Stripe and only a replay will apply it.
- `GET /payments/{id}` has no auth and is mapped only in Development.

## Layout

```
src/StripeWebhooks.Api/
  Program.cs                      composition, endpoint mapping
  Webhooks/WebhookReceiver.cs     size cap, verify, API version, insert-first dedupe, enqueue, ack
  Webhooks/EventProcessor.cs      claim + handler + commit in one transaction, retries with backoff
  Webhooks/PaymentEventHandler.cs payment_intent.succeeded, charge.refunded, stale check
  Webhooks/EventQueue.cs          bounded Channel<string> that tracks the ids it holds
  Webhooks/EventWorker.cs         background consumer
  Webhooks/PendingEventSweeper.cs crash recovery, full-queue fallback, due retries
  Data/                           EF Core model: Payments, ProcessedEvents; SQLite busy timeout
tests/StripeWebhooks.Tests/       xUnit + WebApplicationFactory
```

## License

MIT, see [LICENSE](LICENSE).
