# CoreIns.Platform

Cross-cutting primitives every module uses: the unit of work, the command pipeline, the transactional outbox, the
hash-chained audit log, idempotency, Problem Details, request context and time. Everything lives in the `plt`
PostgreSQL schema.

## Unit of work

`DbSession` holds one connection and one transaction per DI scope. Module `DbContext`s, the outbox (`OutboxStaging`)
and the audit (`AuditStaging`) join it as `ITransactionParticipant`s:

- `BeforeCommitAsync` writes staged rows in the transaction.
- `AfterCommitAsync` runs after a successful commit.
- `AfterRollbackAsync` runs after a rollback or a failed commit.

Audit records staged "regardless of outcome" (rejections, failures) are cleared only after a successful commit. If the
commit fails, they are written in a transaction of their own.

## Command pipeline

The pipeline runs validation, then transaction, idempotency, audit, authority, and finally the handler. A handler
publishes events with `IEventPublisher`. They are written to `plt.outbox_message` in the same transaction as the domain
change, each with a gap-free per-aggregate `aggregateSequence`.

## Outbox dispatch

`OutboxProcessor` (hosted by `OutboxDispatcherService` in the worker role) works in rounds:

1. **Claim.** It claims pending messages under a lease (`FOR UPDATE SKIP LOCKED`). Several dispatchers may run at once.
   A message is claimable only if no earlier undispatched event of its aggregate is in backoff or leased elsewhere, so
   a blocked aggregate never starves the others.
2. **Deliver.** It delivers each aggregate's run in waves. Wave *k* holds the *k*-th event of every run, and every
   handler of a wave finishes before the next wave starts. All of a module's handlers therefore see each aggregate's
   events in sequence order, whatever event types they subscribe to. Within a wave, a handler gets one micro-batch
   transaction (its `plt.processed_event` markers and its effects commit together). A failing micro-batch is retried
   event by event.
3. **Finish.** It completes and archives delivered messages in `plt.event_archive`. A failed message is scheduled for
   retry with backoff (1 s doubling, capped at 5 min) and the rest of its run is released. Only the current lease
   owner can record a retry, so a dispatcher whose lease expired cannot overwrite the new owner's state.

Handlers must be idempotent. Delivery is at least once per handler, and the processed markers make the committed
effect happen exactly once.

## Dead letters (D-ARC-26)

- **Retries hold the aggregate.** While an event is being retried, later events of the same aggregate wait.
- **Parking does not hold the aggregate.** After `MaxAttempts` (default 8), the failing handler's event is parked in
  `plt.outbox_dead_letter` and a `DeadLetterParked` event is raised. The aggregate's later events are then delivered.
- **Replay arrives out of order.** A parked event replayed later with `plt.DeadLetter.replay` (`OutboxReplayService`)
  reaches its handler after events with a higher `aggregateSequence`. `plt.Consumer.replay` re-delivers archived
  events with `origin = REPLAY`.
- **Order-sensitive handlers must check `aggregateSequence`.** Such a handler stores the last sequence it applied per
  aggregate, compares `EventEnvelope.AggregateSequence` with it, and ignores or reconciles a stale event. It must not
  assume arrival order.

## Audit

`plt.audit_event` is insert-only. The application role has INSERT and SELECT, and a trigger refuses UPDATE, DELETE and
TRUNCATE for every role. Records form a SHA-256 hash chain per UTC day.

The chain head (`plt.audit_chain_head`) is read-only to the application. It is created and locked by the SECURITY
DEFINER function `plt.audit_chain_lock(date)`. It is advanced by a SECURITY DEFINER insert trigger, which refuses a row
that does not extend the chain. `AuditChainVerifier` recomputes a day's chain and reports the first broken link.

## Idempotency

State-changing HTTP requests need an `Idempotency-Key` (UUID). A retry with the same key and body replays the stored
status, body and the `Location`, `ETag`, `Content-Location` and `Retry-After` headers. Cookies are never stored or
replayed. A different body gets a 409 problem.

## Problem Details (D-API-15)

Errors are RFC 9457 problems. `type` is the relative URI `/problems/<CODE>`, and the host serves an anonymous Greek and
English page for each code at `GET /problems/{code}`.
