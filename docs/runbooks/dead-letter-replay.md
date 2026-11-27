# Dead-letter replay

Source: `src/CoreIns.Platform/Events/OutboxProcessor.cs`, `OutboxReplayService.cs`, `PlatformEvents.cs`, `DeadLetters/DeadLetterApi.cs`;
contract `contracts/openapi/plt.yaml` (`plt.DeadLetter.list/get/replay`; `discard` and `plt.Consumer.replay` are not served yet).

## How a message gets parked
The worker dispatches outbox rows to handlers. A failing handler is retried with exponential backoff (1 s base, 5 min cap)
up to `MaxAttempts` (default 8), or parked at once on a `PermanentEventFailureException`. Parking inserts a row into
`plt.outbox_dead_letter` (status `Parked`) and raises `DeadLetterParked`. The aggregate is not held while parked
(D-ARC-26), so later events of the same aggregate are delivered meanwhile.

## Triage
All three endpoints need the `Platform.Admin` role (permissions `plt.DeadLetter.list`, `.get`, `.replay`; no ops role exists yet).
Responses carry metadata and the last error only, never the event payload (it may hold personal data).
```
GET /api/plt/v1/dead-letters?status=Parked&module=CLM&eventType=ClaimOpened&parkedFrom=2026-10-01T00:00:00Z&limit=50
GET /api/plt/v1/dead-letters/{deadLetterId}
```
Newest first; follow `nextCursor` (pass it as `cursor`) for the next page. Filters: `handler`, `module` (handler-name prefix),
`eventType`, `status` (`Parked`, `Replayed`, `DiscardPending`, `Discarded`), `parkedFrom` (inclusive), `parkedTo` (exclusive).
Read `errorClass` and `errorMessage` first, then find the worker's "parked" log line (handler, event type, event id). The
original event is in `plt.event_archive` (join on `event_id`); read it with SQL only when you must.

## Fix, then replay
1. Fix the cause (data, configuration, or a code fix). Replaying before the fix just re-parks the message.
2. Replay with `POST /api/plt/v1/dead-letters/replay` (header `Idempotency-Key: <new uuid>` required, dry runs included). Body: name letters
   (`{"deadLetterIds": ["..."]}`) or select Parked ones by filter (`{"module": "CLM", "eventType": "ClaimOpened",
   "parkedFrom": "...", "parkedTo": "..."}`); an empty body is refused. At most 100 letters per call, oldest first; repeat for the rest.
   - **Dry run first:** add `?dryRun=true` (or header `X-Dry-Run: true`). Nothing runs; each Parked match is reported as `WouldReplay`.
   - Response: `matched`, `replayed`, `stillFailing`, `skipped` and one `results[]` entry per letter with `outcome`
     (`Replayed`, `StillFailing` with the new `errorMessage`, `AlreadyResolved`, `NotFound`, `HandlerMissing`, `WouldReplay`).
     A still-failing handler leaves the letter `Parked` (attempts + 1); fix and replay again.
   - Idempotent: the same key returns the stored response without re-running handlers. Replaying letters that are all already
     resolved returns 409 `PLT-ERR-DEAD-LETTER-RESOLVED` and runs nothing; a mixed request skips the resolved ones.
   - Every replay is audited (`plt.DeadLetter.replay`, with the letter ids and the status changes).
   - Under the hood this is `OutboxReplayService.ReplayDeadLetterAsync`; behaviour is covered by
     `tests/CoreIns.IntegrationTests/Platform/DeadLetterApiTests.cs` and `OutboxReplayTests.cs`.
3. For bulk re-delivery of archived events to one handler use `ReplayAsync(handlerName, ReplayFilter)` (time window,
   aggregate, or event ids; archive order; `origin = REPLAY`).
4. Handlers must be idempotent. A replayed dead letter arrives out of order, so order-sensitive handlers compare
   `EventEnvelope.AggregateSequence` with the last sequence they applied.
5. Confirm `status = 'Replayed'` and that the business effect happened.

## Do not
Delete rows from `plt.outbox_dead_letter` or change `status` with SQL. Discarding is designed as a
`DiscardPending` / `Discarded` flow (`plt.DeadLetter.discard`, not served yet; until it is, leave unwanted letters Parked and note why).
