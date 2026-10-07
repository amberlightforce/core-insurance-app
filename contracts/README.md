# Contracts

Machine-readable contracts between modules. This folder is checked in CI. C# contract types (`*.Contracts`) are
generated or written from these files later; when they disagree, these files win until the C# types catch up.

| Folder | What it holds |
|---|---|
| `events/` | The cross-module event catalogue (F-1c events): envelope, shared types, one JSON Schema per event, `catalog.json`, and the validator |
| `openapi/` | The cross-module API contracts (F-1c apis): one OpenAPI 3.1 document per module, shared components, the SPI catalogue, the operation index, and the validator |

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
| `legalEntity`, `jurisdiction` | legal entity code (stamp) and ISO 3166-1 jurisdiction; both **required** on every event (D-CON-26) |
| `configurationHash` | SHA-256 of the resolved configuration; for MKT activation events, the hash after the change |
| `businessKeys` | lineage keys (quote, job, policy, transaction, charge, invoice, claim, journal ids…), D5 / D-CON-01; never empty, and each event schema requires the keys listed in its catalogue `x-business-keys` (D-CON-28) |
| `correlationId` | W3C trace id, technical tracing only (never used for lineage) |
| `causationId` | id of the causing event or command, or null |
| `actor`, `aiInteractionId` | who acted (user / service / AI agent) and the AI interaction record; both **required** on every event, `aiInteractionId` is null when no AI was involved (D-CON-26) |
| `origin` | `LIVE`, `MIGRATION` (converted business) or `REPLAY` (re-emitted by the replay command) |
| `dataClassification` | highest personal-data class in the payload (P0–P3); each event schema pins it |
| `set_id`, `set_size`, `index` | D4 set completeness; all three or none; `1 <= index <= set_size`; **required** on `ChargeDeltaEmitted`, `TransactionReversed`, `TransactionReapplied` |
| `payload` | event-specific; bound by the event schema's `$defs/Payload` |

An event schema composes the envelope with `allOf`, pins `eventType`, `producer`, `aggregateType`,
`dataClassification`, the required `businessKeys` and the schema major, and sets `unevaluatedProperties: false`.
Payloads use `additionalProperties: false`, so a producer cannot emit a field the schema does not declare.

**Rules JSON Schema cannot express** (enforced by `validate.py` and, later, by the C# envelope type and the outbox
writer): `set_id`, `set_size` and `index` come together, `index >= 1` and `index <= set_size`. The C# envelope must
reject such an event at construction, before the outbox row is written.

**Business keys (D-CON-28).** Each catalogue entry lists `x-business-keys`: the aggregate's own id plus the required
business-object ids in the payload (e.g. `ChargeDeltaEmitted` → policyId, chargeId, policyTermId, transactionId;
payload `termId` is carried as `policyTermId`). Ids of people (P1) are not lineage keys, except the party or account id
of PTY party/account events, whose aggregate is that party or account; those values are P1 pseudonymous ids. `a|b`
means at least one of the two (events whose aggregate can be one of several kinds).

### Payload conventions

- Money is `{ "amount": "<decimal string>", "currency": "<ISO 4217>" }`. Amounts in several currencies use
  `MoneyByCurrency3` (transaction, functional, group) or `MoneyByCurrency4` (+ contract currency, RI). Named totals
  use `MoneyTotals` (name → Money); named terms mixing amounts and rates use `NamedAmounts` (Money or decimal string).
- **No bare JSON numbers.** No payload schema uses `type: number`; integers appear only as typed counts. Open
  structures reject numbers at any depth. `validate.py` checks this statically and injects a numeric value into every
  structured payload member and every Money amount of every sample, which must be rejected.
- Dates are ISO `YYYY-MM-DD`; instants are RFC 3339 UTC. Periods are `{from, to}` with `to = null` when open.
- Internal ids are UUIDs (UUIDv7 generated in .NET). Business numbers (policy, claim, invoice…) are strings whose
  format belongs to the PLT numbering scheme. Where the contract defines a format, the schema enforces it: clock codes
  `<MOD>_<NAME>`, AI feature ids `AI-<MOD>-<NN>`, retention classes `RC-…`, obligations `OBL-…`, data contracts
  `dc.<mod>.<entity>.v<n>`, product versions `major.minor`.
- Code values (reason codes, statuses, types) are `Code` strings: the code list lives in configuration, not in the
  schema. A closed `enum` is used only where the PRD lists the values.
- Optional fields may be absent or null (`oneOf [type, null]`, not in `required`); every other field is required.
- `OpenObject` marks a structure the PRD names but does not define (e.g. "metrics summary"). Consumers must not rely on
  its members until the producer defines them in a minor version. Its values may be strings, booleans, null, arrays
  or objects, never numbers.
- Payloads carry ids and minimum business data. No names, addresses, IBANs, card data or document content (contract
  §3.4.1). Envelope fields are not repeated in the payload (e.g. `origin`).

### Personal data classification

Every payload field carries `x-classification` (`P0` not personal, `P1` personal, `P2` personal-sensitive, `P3`
special category; contract §3.2.1, R-66). Fields above P0 also carry `x-personal-data: true`, and the catalogue lists
them per event (`personalDataFields`). The envelope `dataClassification` must equal the highest class in the payload.
The envelope `actor` is P1. Party, account, user and requester ids are classified P1 because they point at natural
persons (`accountId` is P1 everywhere). The fraud score band (`FraudScoreReceived`) and the SIU
case fields (`SiuCaseOpened`, `SiuCaseConcluded`, restricted contract) are **P3** (D-CON-27), so those events carry
`dataClassification = P3`. There is no P2 field.

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
every event validates. Each sample also drives negative tests that must fail: an unknown payload field; a missing
`actor`, `jurisdiction` or `aiInteractionId`; a missing declared business key or an empty `businessKeys`; missing set
fields on a set event; `index > set_size` or `index < 1`; a bare number in any structured payload member or Money
amount; a PINNED rating slot without `pinnedArtefactHash`. Without `jsonschema` it runs only the structural checks
(and fails if `--require-jsonschema` is given).

`--instance FILE...` additionally validates concrete events (one event or a JSON array), for example test fixtures or
captured outbox rows, against their schema and the semantic rules above.

## APIs (`contracts/openapi`)

### Layout

```
contracts/openapi/
  common.yaml          shared components: Problem Details, parameters (Idempotency-Key, traceparent, validAt,
                       knownAt, dryRun, cursor/limit), page envelope, value types ($ref to ../events/common.schema.json),
                       security schemes (Entra ID, Entra External ID, partner client credentials, provider signature)
  <module>.yaml        one OpenAPI 3.1 document per module (pty … mkt): every operation of the module PRD §9.1
  anchors.yaml         the 174 contract anchors (contract §3.6.3) and how operations cover them
  spi.md               the 40 country-pack SPIs (REQ-MKT-002, PRD-17 §9.4): C# interfaces, not HTTP
  INDEX.md             generated table of every operation with consumers, wave and status
  validate.py          CI check (+ requirements.txt with pinned versions)
```

Sources: each module PRD §9.1 "Inbound operations owned" (the owner's names are canonical, R-87), the callers'
§9.2 "Outbound calls" (consumers), PRD-12 §9.1.3/§9.1.4 (partner resources and MCP tools), PRD-18 §8.4 (interface
register), the integration review §3.1 (superseded names) and `orchestration/backlog/backlog.json` (waves).

### One contract, two transports (D-ARC-16)

Modules call each other **in-process** through `CoreIns.<Module>.Contracts` interfaces whose methods carry the same
operation names (`pol.Job.bind` → `IPolJobs.BindAsync`, for example). The OpenAPI document is the contract source for
both: the request/response schemas, error codes, idempotency and dry-run rules apply to the in-process call too. HTTP
serves the UI, partners and tests only; one module never calls another over HTTP or reads its tables.

### Operation conventions

- **operationId** `<module>.<Resource>.<verb>`, exactly the PRD §9.1 name. PRD names with four parts are folded into
  the resource (`ri.Security.rules.set` → `ri.SecurityRules.set`; the PRD spelling is kept in `x-prd-name`). Names other
  modules or older texts used for the same operation are listed in `x-superseded-names` and must not be used.
- **Path** `/api/<module>/v1/<resources>` (kebab-case, plural): `get` → `GET …/{id}`, `list` → `GET …`,
  `create` → `POST …`, `update` → `PATCH …/{id}`, other reads → `GET …/<verb>`, every other operation →
  `POST …/<verb>` with identifiers in the body (command/query style, contract §3.5.1).
- **Commands and queries** (`x-operation-kind`). Every command requires the `Idempotency-Key` header (UUID, kept ≥ 7
  days, replay returns the original result, a different payload → 409 `<MOD>-ERR-IDEMPOTENCY-MISMATCH`). Queries may be
  `POST` when they compute (`rat.Rate.rate`, `plt.Authority.check`) and are side-effect free. Commands fail with a typed
  error and never partially.
- **Dry-run** (`x-dry-run: true`) adds the `dryRun` query parameter (header `X-Dry-Run: true` is equivalent): full result,
  no side effects. `x-dry-run-note` quotes the PRD's idempotency/dry-run cell.
- **Errors**: RFC 9457 `application/problem+json` with `type`, localised `title`, `status`, `detail`, `code`
  (`<MOD>-ERR-<NNN>` or `<MOD>-ERR-<NAME>`, contract §3.5.4), `traceId` (the contract's `correlation_id`, W3C trace id,
  never a business key), `retryable` and field `errors[]`. `x-error-codes` lists the codes the PRD names with the HTTP
  status they map to (not found 404; stale/lock/duplicate 409; permission/authority/SoD 403; unavailable 503; other
  business preconditions 422). Business-rule outcomes that are not errors (UW issues) are results, not errors.
- **Time travel** (D-API-02, D-API-08, D-API-09): the valid-time instant is always `validAt` and the transaction-time
  instant always `knownAt`, contract §3.5.5. PRD spellings (`asOf`, `asAt`, `date`, "as of record time") are recorded
  only in the operation's `x-prd-param-names`; operation and error names from the PRDs (`dat.Query.asOf`,
  `doc.Document.renderAsOf`, `DOC-ERR-ASOF-UNSUPPORTED`) stay (D-API-03). Each time-travel input appears exactly once,
  as a query parameter, never in the request body.
- **Lists**: every list-style query (`list*`, `search`, `query`, `history`, …) uses cursor pagination (`cursor`,
  `limit` ≤ 200) and the shared page envelope, filtered by the caller's legal entity and ABAC scope; a list with a
  PRD-stated natural bound carries `x-bounded` with the reason instead.
- **Security**: Entra ID bearer tokens (staff), Entra External ID (customers, intermediaries), client credentials with
  certificate for partners (D-ARC-03). `x-permission` names the permission (by convention the operationId) that app
  roles map to; `x-authority-types` lists the authority types the PRD associates with the operation's requirements
  (checked through `plt.Authority.check`, `x-authority-source` cites the PRD lines). Every call carries `traceparent`.
- **Traceability**: `x-requirement` (REQ ids; anchors first; `REQ-X-NNN..REQ-X-MMM` = a range the PRD cites),
  `x-anchor` / `x-anchor-mapped` (contract anchors; *mapped* = attached by the contract builder because the PRD row
  cites detailed requirements), `x-wave` (earliest build wave of the operation's Must-P1 requirements in the backlog;
  `unscheduled` = no Must-P1 requirement, built only when a P1 Must needs it), `x-work-packages`, `x-source` (PRD
  section and line).
- **Exposure**: `x-in-process: true` + `x-exposure: [internal]` = called only module-to-module (the HTTP route exists for
  tests); otherwise `x-exposure` lists `ui` and/or `partner` (reachable through the CHN partner API or MCP facade,
  `x-partner-routes`). `x-consumers` lists the modules that call the operation.
- **Status**: `x-status: full` = inputs and outputs are taken from the PRD row (field names from the PRD; types only where
  the name makes them unambiguous: ids, dates, instants, hashes, Money, currency, language, flags; otherwise
  `Unspecified`); `minimal` = the PRD row gives only names ("as named", "per operation"). `x-io-shared: N` = the PRD row
  states one input/output list for N operations; the owning work package narrows it per operation. PRD fields that
  join two concepts are split (`source type and id` → `sourceType` + `sourceId`; `product or hash` → `product` |
  `hash` with a `oneOf` rule). `x-typed` marks the operations fully typed now under D-API-06 as narrowed by
  **D-API-06a**: the **quote, bind, money and claims** critical chains of PLAN §2 (required lists, PRD-stated enums,
  booleans, Money, ids with patterns; `x-typed` cites the requirements used). The **lifecycle chain**
  (`pol.Cancellation.*`, `cmp.Clock.*`, `bil.Refund.*`, `bil.Delinquency.*`, `mkt.StatutoryClockSet.*`) and the
  remaining `ri.Recovery.*` operations are typed by their owning WPs (W2-CMP, W5-BIL, W6-POL, W1-MKT, W7-RI) while
  still pre-release. `x-todo-owner` marks a field whose shape a named WP still has to type (D-API-13).
  `x-operation-families` lists `Resource.*` families whose members the PRD does not name; they are completed by the
  owning work package. No business rule is invented in either case.
- `x-excluded` records PRD rows deliberately not modelled (OIDC endpoints and SCIM under D-ARC-03; the PLT-internal
  configuration operations, XMR-F-102; CLM's removed payee-account API; the `TaxCalculator` SPI, which is in `spi.md`).
- `x-deviation` records where the infrastructure decisions change the meaning of a PRD operation (D-ARC-01/03/04).

### Partner API and MCP

The external partner resources of PRD-12 §9.1.3 are CHN operations `chn.Partner<Resource>.<verb>` with the PRD's method
and path; their names are derived (`x-name-derived: true`), `x-delegates-to` lists the owner operations CHN composes, and
`x-partner-scopes` the PRD-12 §9.1.2 scopes. The MCP agent facade is one JSON-RPC operation `chn.Agent.mcp` carrying
`tools/list`, `tools/call` and `resources/read`; `x-mcp-tools` reproduces the PRD-12 §9.1.4 tool catalogue.

### Versioning and maturity (D-API-06)

Every operation carries `x-maturity`. **`pre-release`** (all operations today): the published v1 operation may still
be tightened by its owner until the first consumer work package that calls it merges: typing `Unspecified` members,
adding required inputs and enums, splitting fields. When that consumer merges, the owner sets **`stable`** and the
normal rule applies. Critical-chain anchor operations are already fully typed (`x-typed`) so their consumers build
against the final shape.

The major version is in the path (`/v1/`). For `stable` operations, changes within a major are additive: new operations, new optional request
fields, new response fields, new error codes, defining an `Unspecified` member or expanding a family. Removing or
renaming an operation or field, making a field required, or changing semantics needs a new major served in parallel;
deprecation notice ≥ 6 months for external (partner) operations and ≥ 1 release internally (contract §3.5.2).

### Validation

```
pip install -r contracts/openapi/requirements.txt
python contracts/openapi/validate.py --require-spec-validator
python contracts/openapi/validate.py --write-index      # after changing a module document
```

It validates every document against the OpenAPI 3.1 schema (`openapi-spec-validator`), resolves every `$ref`
(including into `contracts/events/common.schema.json`), and checks: unique `<module>.<Resource>.<verb>` operationIds;
`x-requirement` on every operation and family; every state-changing operation is a command with a required
`Idempotency-Key` and lists `<MOD>-ERR-IDEMPOTENCY-MISMATCH`; dry-run operations declare `dryRun`; every operation
declares `traceparent`; every 4xx/5xx response is Problem Details; error-code format; exposure consistency; declared
security schemes and path parameters; `x-maturity`; no parameter or property named `asOf` / `asAt` and no
time-travel input repeated in the body (D-API-08/09); pagination or `x-bounded` on list queries; anchor coverage
(`anchors.yaml`); the 40 SPIs in `spi.md`; and that `INDEX.md` is current. It warns about property names that join two
concepts with `And` / `Or`.
