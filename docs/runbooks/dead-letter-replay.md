# Dead-letter replay

Source: `src/CoreIns.Platform/Events/OutboxProcessor.cs`, `OutboxReplayService.cs`, `PlatformEvents.cs`;
contract `contracts/openapi/plt.yaml` (`plt.DeadLetter.*`, `plt.Consumer.replay`).

## How a message gets parked
The worker dispatches outbox rows to handlers. A failing handler is retried with exponential backoff (1 s base, 5 min cap)
up to `MaxAttempts` (default 8), or parked at once on a `PermanentEventFailureException`. Parking inserts a row into
`plt.outbox_dead_letter` (status `Parked`) and raises `DeadLetterParked`. The aggregate is not held while parked
(D-ARC-26), so later events of the same aggregate are delivered meanwhile.

## Triage
```sql
SELECT dead_letter_id, handler, event_type, aggregate_type, aggregate_id, attempts, error_class, error_message, status
FROM plt.outbox_dead_letter WHERE status = 'Parked' ORDER BY 1;
```
Statuses: `Parked`, `Replayed`, `DiscardPending`, `Discarded`. Read `error_class` and `error_message` first, then find the
worker's "parked" log line (handler, event type, event id). The original event is in `plt.event_archive` (join on `event_id`).

## Fix, then replay
1. Fix the cause (data, configuration, or a code fix). Replaying before the fix just re-parks the message.
2. Replay is `OutboxReplayService.ReplayDeadLetterAsync(deadLetterId)`: it re-runs that handler for that event; success sets
   `Replayed`, failure leaves it `Parked` with the new error.
   The contract exposes `GET /api/plt/v1/dead-letters` (`plt.DeadLetter.list`) and `POST /api/plt/v1/dead-letters/replay`
   (permission `plt.DeadLetter.replay`, idempotency key required, dry-run supported). **The service is registered
   (`PlatformModule`) but these routes are marked `x-status: minimal`; confirm they are wired before relying on them.** The
   behaviour is exercised in `tests/CoreIns.IntegrationTests/Platform/OutboxReplayTests.cs`.
3. For bulk re-delivery of archived events to one handler use `ReplayAsync(handlerName, ReplayFilter)` (time window,
   aggregate, or event ids; archive order; `origin = REPLAY`).
4. Handlers must be idempotent. A replayed dead letter arrives out of order, so order-sensitive handlers compare
   `EventEnvelope.AggregateSequence` with the last sequence they applied.
5. Confirm `status = 'Replayed'` and that the business effect happened.

## Do not
Delete rows from `plt.outbox_dead_letter` or change `status` with SQL. Discarding goes through the
`DiscardPending` / `Discarded` flow (`plt.DeadLetter.discard`).
