# Contracts

Machine-readable contracts between modules. This folder is checked in CI. C# contract types (`*.Contracts`) are
generated or written from these files later; when they disagree, these files win until the C# types catch up.

| Folder | What it holds |
|---|---|
| `events/` | The cross-module event catalogue (F-1c events): envelope, shared types, one JSON Schema per event, `catalog.json`, and the validator |

## Event contracts (`contracts/events`)

### Layout

```
contracts/events/
  envelope.schema.json            standard envelope (every event)
  common.schema.json              shared payload types (Money, Uuid, ObjectRef, DatePeriod, ...)
  catalog.json                    every event: producer, consumers + handlers, ordering key, trigger, source, status
  catalog.schema.json             shape of catalog.json
  validate.py                     CI check
  <module>/<EventName>.v<major>.schema.json   one schema per event and major version (module in lower case)
```

Sources: the catalogue of record is PRD-18 §8.2 (302 events). Two decided additions bring it to 304:
`DisbursementRejected` and `DisbursementStopped` (contract D4, D-CON-21). `AiSystemStatusChanged`,
`WithdrawalRequestReceived` (D-CON-09) and `ClockElapsed` (R-78) are already in PRD-18. Payload fields come from the
owning module PRD's §8 "Produced" table; the consumer list comes from each consuming PRD's §8 "Consumed" table.

### Envelope

Every event is one JSON object: the envelope fields plus `payload`. Field names are camelCase, except the contract D4
set-completeness fields, which keep their contract names (`set_id`, `set_size`, `index`, D-CON-19).

| Field | Meaning |
|---|---|
| `eventId` | UUIDv7; consumers are idempotent on it (`plt.processed_event`) |
| `eventType`, `schemaVersion` | catalogue name and `major.minor` of its schema |
| `producer` | module code; exactly one producer per event type |
| `aggregateType`, `aggregateId`, `aggregateSequence` | ordering key and gap-free per-aggregate sequence (starts at 1) |
| `occurredAt`, `recordedAt` | business time and commit time, RFC 3339 UTC (`Z`) |
| `legalEntity`, `jurisdiction` | legal entity code (stamp); jurisdiction where the contract asks for it |
| `configurationHash` | SHA-256 of the resolved configuration; for MKT activation events, the hash after the change |
| `businessKeys` | lineage keys (quote, job, policy, transaction, charge, invoice, claim, journal ids…), D5 / D-CON-01 |
| `correlationId` | W3C trace id, technical tracing only (never used for lineage) |
| `causationId` | id of the causing event or command, or null |
| `actor`, `aiInteractionId` | who acted (user / service / AI agent) and the AI interaction record, if any |
| `origin` | `LIVE`, `MIGRATION` (converted business) or `REPLAY` (re-emitted by the replay command) |
| `dataClassification` | highest personal-data class in the payload (P0–P3); each event schema pins it |
| `set_id`, `set_size`, `index` | D4 set completeness; all three or none; **required** on `ChargeDeltaEmitted`, `TransactionReversed`, `TransactionReapplied` |
| `payload` | event-specific; bound by the event schema's `$defs/Payload` |

An event schema composes the envelope with `allOf`, pins `eventType`, `producer`, `aggregateType`,
`dataClassification` and the schema major, and sets `unevaluatedProperties: false`. Payloads use
`additionalProperties: false`, so a producer cannot emit a field the schema does not declare.

### Payload conventions

- Money is `{ "amount": "<decimal string>", "currency": "<ISO 4217>" }`. Amounts in several currencies use
  `MoneyByCurrency3` (transaction, functional, group) or `MoneyByCurrency4` (+ contract currency, RI).
- Dates are ISO `YYYY-MM-DD`; instants are RFC 3339 UTC. Periods are `{from, to}` with `to = null` when open.
- Internal ids are UUIDs (UUIDv7 generated in .NET). Business numbers (policy, claim, invoice…) are strings whose
  format belongs to the PLT numbering scheme. Where the contract defines a format, the schema enforces it: clock codes
  `<MOD>_<NAME>`, AI feature ids `AI-<MOD>-<NN>`, retention classes `RC-…`, obligations `OBL-…`, data contracts
  `dc.<mod>.<entity>.v<n>`, product versions `major.minor`.
- Code values (reason codes, statuses, types) are `Code` strings: the code list lives in configuration, not in the
  schema. A closed `enum` is used only where the PRD lists the values.
- Optional fields may be absent or null (`oneOf [type, null]`, not in `required`); every other field is required.
- `OpenObject` marks a structure the PRD names but does not define (e.g. "metrics summary"). Consumers must not rely on
  its members until the producer defines them in a minor version.
- Payloads carry ids and minimum business data. No names, addresses, IBANs, card data or document content (contract
  §3.4.1). Envelope fields are not repeated in the payload (e.g. `origin`).

### Personal data classification

Every payload field carries `x-classification` (`P0` not personal, `P1` personal, `P2` personal-sensitive, `P3`
special category; contract §3.2.1, R-66). Fields above P0 also carry `x-personal-data: true`, and the catalogue lists
them per event (`personalDataFields`). The envelope `dataClassification` must equal the highest class in the payload.
The envelope `actor` is P1. Party, user and requester ids are classified P1 because they point at natural persons. The
only P2 fields are the fraud score band (`FraudScoreReceived`) and the SIU case fields (`SiuCaseOpened`,
`SiuCaseConcluded`, restricted). There is no P3 field.

### Consumers (D-CON-09)

A module is a consumer only if its own PRD §8 "Consumed" table declares a handler. Each consumer in `catalog.json`
cites that handler (`PRD-nn §8.x`) and its reaction. DAT ingests every topic into the report store (REQ-DAT-001; no
lakehouse, D-ARC-06) except `CoexistenceMasterChanged`, which PRD-15 says it does not consume.

`catalog.json` → `consumerChanges` records the trim:

- `removed`: consumer entries named by PRD-18 §8.2 ("listed without handler", the R3-002 residue), by a producer, or
  declared "not consumed" by the consumer PRD, which have no handler. They are not consumers.
- `keptBecauseHandlerExists`: entries flagged as unhandled that the consumer PRD now handles (e.g. POL handles
  `BureauFactRejected`, BIL and FIN handle `FiscalDocCancelled`, PLT handles `AiSystemStatusChanged`).
- `addedVsPrd18`: handlers declared by consumer PRDs that PRD-18's consumer column does not show.

To add a consumer: add the handler to the consumer PRD/module, then add the consumer entry here citing it.

### Versioning and compatibility

- `schemaVersion` is `major.minor`. The file name carries the major (`PolicyBound.v1.schema.json`); the minor is in
  `x-schema-version` and in the catalogue `version`.
- **Within a major, changes are additive only** (minor bump): add an optional (nullable) payload field, add an
  optional envelope field, widen a pattern, add a value to an open `Code` field, define the members of an
  `OpenObject`, or add a consumer.
- **Breaking** (new major): remove or rename a field, change a type or format, make an optional field required, add
  a value to a closed `enum`, change the ordering key or aggregate, or change what the event means.
- A new major is published **in parallel**: add `<EventName>.v2.schema.json` and a second catalogue entry with
  version `2.0` and topic `<mod>.events.v2`. The producer writes both v1 and v2 outbox rows until every handler has
  moved to v2; then v1 is retired by removing its catalogue entry and schema in a later change.
- Producers validate what they emit against the current schema (contract tests). Consumers are tolerant readers:
  they ignore payload fields they do not know and never fail on a higher minor of the same major.
- Event names never change. `Lakehouse*` names keep their catalogue name (stable contract) and mean "report store"
  (D-ARC-06). Registry names are topic-qualified (`wrk.DocumentReceived`, `ri.StatementIssued`) so that similar
  names in two modules are never confused (XMR-F-121).

### Broker vocabulary mapping (D-ARC-02)

There is no message broker. Events go through a transactional outbox (`plt.outbox_message`, written in the same
transaction as the change) to a dispatcher in the worker, which calls in-process idempotent handlers.

| PRD / contract term | Meaning in this system |
|---|---|
| topic `<mod>.events.v<major>` | event-type namespace: the `producer` + major of the schema (`catalog.json` `topic`) |
| partition key | ordering key: `aggregateId` with gap-free `aggregateSequence` (`catalog.json` `orderingKey`) |
| consumer group | handler registration of a module for an event type (`catalog.json` `consumers`) |
| schema registry | the JSON Schemas in `contracts/events`, checked in CI by `validate.py` |
| DLQ | dead-letter table; parked messages raise `DeadLetterParked` |
| replay | replay command (`plt.Consumer.replay`); replayed events carry `origin = REPLAY` |
| exactly-once processing | at-least-once dispatch + idempotent handlers on `eventId` (`plt.processed_event`) |
| retention of the stream | event archive table; its replay window is separate from outbox purge |

### Payload status

`catalog.json` `status` is `full` when every field in the PRD's payload outline is mapped, or `minimal` when the PRD
gives only names or placeholders (e.g. "complaint, ADR body", "inputs summary"). Minimal schemas carry
`x-payload-status: minimal` and an `x-payload-note`, and add no business rule that the PRD does not state.

### Validation

```
pip install "jsonschema>=4.18"
python contracts/events/validate.py --require-jsonschema
```

It checks that every catalogue entry has a schema file and every schema file a catalogue entry, that every schema is
valid JSON Schema 2020-12 and composes the envelope, that every `$ref` resolves, that names are unique per major with
one producer each, that producers and consumers are module codes and every consumer cites a handler, that
classification is consistent, that D4 set fields are required where they must be, and that a synthetic sample of
every event validates (and an unknown payload field is rejected). Without `jsonschema` it runs only the structural
checks (and fails if `--require-jsonschema` is given).
