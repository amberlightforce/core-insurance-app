# Digest — PRD-03 Rating and Pricing Engine (RAT)

Source: `core-insurance-prds/PRD-03-rating-pricing-engine.md`, read in full (lines 1–1860). Version 1.1, 2026-10-07, "Baseline candidate — freeze fix applied". Binding input it cites: 00-system-contract.md v1.11; programme decisions D1–D10; PRD-18. Stack checked against `core-insurance-infra/ARCHITECTURE-DECISIONS.md`, plus a targeted grep of INFRASTRUCTURE.md (replica counts and RPO/RTO). I did not read 00-system-contract.md, so where I flag contract conflicts they come only from what PRD-03 says about the contract.

---

## 1. Identity

| Item | Value |
|---|---|
| Module code | RAT |
| Title | Rating and Pricing Engine |
| Owner roles | Pricing actuary ROLE-34 (tables, algorithms, golden tests); product owner ROLE-14 (commercial pricing policy); rating service engineer (specialises ROLE-38); co-reviewers compliance ROLE-29 and DPO ROLE-30 (§1.1) |
| ID scheme | Contract anchors REQ-RAT-001…009; module reqs REQ-RAT-030…276; BR-RAT-, NFR-RAT-, SCR-RAT-, AI-RAT-, OI-RAT-, CCR-RAT-; local risks RK-RAT-nn (§1.1) |

**Purpose (§1.2).** RAT prices every quote, policy change, renewal and rewrite as a pure, deterministic function, and runs the governed process through which actuaries change that function. Rating returns **annualised rates, never amounts** (REQ-RAT-001). A separate deterministic proration service turns rates into amounts (REQ-RAT-004). A rating result is fully determined by the **rating key**: product artefact hash, rating artefact hash, configuration hash, plus a normalised input. Taxes and levies are separate charge types, computed after premium through the `TaxCalculator` SPI. Rate change is governed like code: validation, zero-cent golden tests, an independent oracle, CompareVersions, ShadowRun, impact analysis, maker-checker approval with author ≠ approver ≠ activator, scheduled activation and rollback. Scope covers the rating runtime (single, batch, explain, dry-run), the algorithm framework, rate tables as content-addressed versioned data, discounts/loadings/deviations, the proration and day-count service shared with POL, the tax stage, legal pricing constraints, rate management, worksheets, the customer breakdown and the explanation of price change.

**Five key decisions (§1.2):**
1. Rates, not amounts.
2. Three-hash rating key: the rating artefact floats at resolution and is pinned for the term.
3. Decimal end to end; rounding only at declared points.
4. Taxes and levies after premium, per coverage, via `TaxCalculator`.
5. Rate change governed like code and evidenced like a filing.

**Non-goals / out of scope (§1.4):**

| Out of scope | Owner |
|---|---|
| Product structure, coverages, charge-type catalogue, rating-slot declaration, product reference tables | PFC |
| Product-version resolution and the resolution manifest | PFC |
| Eligibility, referral and decline rules; UW issues | UW (RAT only emits signals) |
| Policy transactions, segments, charge deltas, reverse-and-reapply, renewal engine | POL |
| Tax and levy rates, bases and rounding content | MKT country packs |
| Prohibited-factor lists and legal pricing-limit content | MKT |
| Invoicing, instalments, money movement | BIL |
| Authority framework, maker-checker, audit, identity | PLT |
| Model training, "lakehouse", monitoring marts, model registry | DAT |
| Customer and partner channels | CHN |
| Vehicle registry, valuation, geocoding sources | MKT packs via `MotorDataProvider` and `Geocoder`, called by POL/UW **before** rating |
| Actuarial modelling (GLM fitting, elasticity) | External tools; RAT imports tables |

Other exclusions:
- Live champion–challenger pricing is excluded from the MVP (§3.2, OI-RAT-07).
- No tariff-filing workflow in core (OBL-BOG).
- Commission is not a rating output; expense loadings are tariff factors only, and commission stays in BIL (CD-14) (§16.2 assumption 6).
- RAT does not emulate legacy algorithms (REQ-RAT-273).

**Phase scope (§1.5, contract D6):**
- P1 motor sells **annual terms only**. Six-month capability is kept proven through golden cases (REQ-RAT-276).
- Bancassurance is out of P1 unless a bank partner is signed by G0.
- Migration uses **scenario B**: the book converts at renewal.
- P2 home (hazard zones, fire-class IPT split), P3 commercial (by-peril, schedules, coinsurance, experience/schedule rating, ITV), P4 later markets (multi-currency, tariff notification).

---

## 2. Size metrics

Recomputed from the requirement tables with a script; they match the PRD's own §5.16 and §16.6.

| Metric | Count |
|---|---|
| Functional REQ total | **256** (REQ-RAT-001…009 anchors + 030…276) |
| Must | 219 (**215 P1**, 4 P2: REQ-RAT-107, 148, 149, 151) |
| Should | 22 (20 P1; 1 P2: 150; 1 P3: 116) |
| Could | 3 (123 P2, 134 P3, 219 P4) |
| Won't (for MVP) | 12 (257–265, 267, 268 P3; 266 P4); these are "Must for phase P3" (§5.16) |
| [BASELINE] / [ENHANCEMENT] | 196 / 60 |
| **Motor MVP (P1) requirements** | **235 total P1, of which 215 Must** |
| Business rules | 38 (BR-RAT-001…038) |
| NFRs | 23 (NFR-RAT-001…023) |
| Screens | 15 (SCR-RAT-01…15): 11 staff, 4 embedded components (10–13) |
| Owned entities | 27 rows in §7.1 (some are composite, e.g. BatchJob/BatchItemResult) |
| Events produced | 8 |
| Events consumed | 27 event types from PFC, PLT, MKT, POL, CMP, DAT |
| Inbound APIs | 21 operation groups (§9.1), roughly 45 individual operations |
| Outbound integrations | 15 rows (§9.2) |
| Config keys | 15 RAT keys + consumed MKT `cur.*`/`tax.*` keys |
| AI features | 7 (AI-RAT-01…07); P1: 01, 02, 03 (§1.5) |
| Open issues | 16 (OI-RAT-04, -09, -11, -15 closed) |
| CCRs | 8 (all accepted, R-20…R-27) |

**Build-size estimate: L.**
- There are 215 Must P1 requirements, more than twice the L threshold.
- The money and temporal logic is heavy: decimal rating, proration and day-count, tax treatment of credits, renewal capping, bitemporal resolution with `knownAt`, and the zero-cent golden and reproducibility guarantees.
- The PRD also requires an in-house CEL-compatible expression language and a compiler for a step-graph DSL.

---

## 3. Owned entities

Schema `rat`. Every business row carries `legal_entity_id`, `jurisdiction`, `created_at`, `created_by`, `record_version`. IDs are UUIDv7. Artefacts, table versions and worksheets are immutable content-addressed objects (canonical JSON, compressed) in a write-once area. There are no cross-schema reads (§7.0).

| Entity | Identifier | Key attributes | Notes / retention |
|---|---|---|---|
| RatingSlot | id; business key product code + jurisdiction + algorithm code | legal_entity_id (nullable = all), channel scope, algorithm_code, input_schema_major, binding_mode (floating/pinned) | Created from PFC slot declarations (REQ-PFC-130). RC-CFG |
| RatingAlgorithm / AlgorithmVersion | algorithm_code + semver | step graph (typed AST), sub-algorithms, GR/EN explanation templates, factor declarations, rating-cell definition, discount catalogue, default profile, decomposition order | Immutable once an Approved artefact references it. RC-RAT-DEF |
| FactorDeclaration | factor code | GR/EN names, inputs read, classification (risk/cost/commercial), display group, monotonicity per input, justification doc ref, sensitivity flag | Mandatory (REQ-RAT-080) |
| RateTable | table_code | GR/EN description, owner, type FACTOR, BASE_RATE, BAND, CURVE, LOOKUP, BONUS_MALUS, SHORT_RATE, REGION_MAP, MAPPING | RC-RAT-DEF |
| RateTableVersion | SHA-256 hash; code + version no. | dimensions, value columns (scale ≤ 8), hit policy, domain behaviour, monotonicity, hazard scheme code, rows, source import ref | Write-once after Validated; identical content reuses the hash (REQ-RAT-176) |
| RatingArtifact | SHA-256 hash; label (slot + semver) | manifest: format, compiler version, algorithm version, table version hashes, input JSON Schema, default profile, slots and compatibility ranges, hazard schemes, source commit; authors; change set | Immutable bytes |
| CompatibilityRecord | artefact hash + product version | product artefact hash, result, reasons, checked_at | Recomputed on PFC events |
| RateActivation | activation_id | slot, artefact hash, txn type NEW_BUSINESS/RENEWAL, effective_from (timestamptz, legal-entity zone), channel scope, scheduled_by, approval ref, reason | Status: Scheduled, Active, Superseded, Cancelled, RolledBack. No overlapping Active activations per slot+type+channel. Append-only. RC-RAT-GOV |
| RatingChangeSet | id | title, authors, base artefact, edited parts | Status: Open, Built, Submitted, Merged, Abandoned (distinct from PFC ChangeSet, CON-029) |
| ImportJob | import_id | file hash, DOC archive ref, mapping profile, target table, check results, acknowledgements | Status: Uploaded, Validating, Valid, Invalid, Committed, Discarded |
| MappingProfile | id | source name, column rules, owner | RC-CFG |
| GoldenCase | id | slot, tags, synthetic input, expected lines **and step values**, baseline artefact hash | Synthetic only |
| GoldenRun | id | artefact hash, suite type (golden, property, fuzz, oracle, performance, Cyprus), pass/fail, failing cases, minimised counterexamples | RC-RAT-GOV |
| RebaselineRequest | id | changed cases before/after, justification, maker, checker, PLT approval ref | Proposed → Approved/Rejected; maker ≠ checker |
| ComparisonRun | id | 2–5 artefact hashes, input set ref/hash, metrics | — |
| ShadowRun | id | candidate hash, slot, sample rate, filters, window, counts | Requested → Running → Completed / Stopped / Failed (candidate error rate > 5% or infrastructure failure) |
| ShadowRunResult | run id + pseudonymous request ref | rating cell, channel, mode, production and candidate premium, first diverging step, error | No direct identifiers; RC-RAT-CANDRUN (90 days after run end, per CCR-RAT-01) |
| RateImpactRun | id | candidate/baseline hashes, population, window, as-of, **population snapshot hash**, progress, metrics, fairness view | Requested → Running → Completed / Failed → Running (resume) / Cancelled |
| RatingGovernanceBundle | id | artefact hash, item list with hashes, pack hash, DOC archive ref | Immutable after freeze (distinct from DOC EvidencePack, R-82) |
| PricingModification | id | job, quote version, scope, type, value, reason code, justification, proposer, risk-input content hash, authority check id, approval ref, approver | State machine below |
| Worksheet | id = SHA-256 of canonical worksheet | header + lines (REQ-RAT-227), retention state QUOTE/ATTACHED/DRY_RUN, attached txn refs | P1 inputs; RC-RAT-WS / RC-POL-QUOTE / 24 h |
| WorksheetIndex | worksheet id + ref | policy, job, txn, quote version, artefact hash, mode | Supports rollback lists and DSAR |
| PriceReviewCase | id | worksheet, job/policy, channel, WRK activity, reviewer, decision, reasoning | Reviewer never an AI identity |
| PublishedStatement | id + version | type HOW_WE_PRICE / CLAIMS_HISTORY_USAGE, product, market, languages, source artefact hash, DOC ref | One current per type/product/market |
| LegacyRatingRecord | id | legacy policy/term refs, mapped coverage premiums, extract hash, origin MIGRATION | Read-only, never re-performed |
| BatchJob / BatchItemResult | batch id; batch id + item key | requester, priority class, counts, status; per-item response/error | RC-TMP 30 days after hand-over |
| RatingIdempotencyRecord | idempotency key | request hash, response ref, expires_at (7 days) | Technical table |

Retention durations for RC-RAT-* codes are **not** in the PRD. They come from the programme retention schedule loaded as Greece-pack data under REQ-PLT-011 (XMR-F-329, XMR-D-259). CCR-RAT-01 gives only the triggers and one duration (CANDRUN 90 days).

**State machines.**

*RatingArtifact* (§7.3; canonical in contract §3.2.4 via R-23):

| From → To | Trigger | Guard | Event |
|---|---|---|---|
| — → Draft | build of change set | — | — |
| Draft → Validated | validation and lint pass | no error-level findings | — |
| Validated → Draft | edit | — | — |
| Validated → Submitted | submit | evidence complete (REQ-RAT-213) | PLT ApprovalRequested |
| Submitted → Draft | returned | comment present | — |
| Submitted → Approved | last approval | maker ≠ checker | `RatingArtifactPublished` |
| Approved → Scheduled | activator schedules | SoD; compatible slot | `RateVersionScheduled` |
| Scheduled → Approved | cancel | before activation | `RateVersionScheduled` (cancelled) |
| Scheduled → Active | time reached | — | `RateVersionActivated` |
| Active → Superseded | successor active for every activation | — | successor's `RateVersionActivated` |
| Active → Withdrawn | rollback | emergency approval | `RateVersionWithdrawn` |
| Superseded/Withdrawn → Retired | no retained term/quote references it and retention reached | WorksheetIndex count 0 | — |

The PRD maps these states to PRD-18 governance stages: Authoring, Under review, Approved-not-live, Live, Replaced, Withdrawn (CR-S3-25).

*RateTableVersion:* Draft → Validated → Approved (once included in an Approved artefact) → Retired. Validated → Draft on edit.

*PricingModification:*
- Proposed → Approved (authority allow), or → PendingApproval (refer-to), or → Rejected (deny).
- PendingApproval → Approved or Rejected.
- Approved → Invalidated (risk inputs changed beyond tolerance), → Applied (job bound), or → Withdrawn.
- Invalidated → Proposed.
- `PricingModificationDecided` fires on Approved, Rejected and Invalidated.

*Worksheet retention:* QUOTE → ATTACHED (via `attach`); QUOTE → Deleted (RC-POL-QUOTE); DRY_RUN → Deleted after 24 h; ATTACHED → Deleted at transaction retention unless under legal hold.

*BatchJob status:* queued, running, completed, completed-with-errors, cancelled (REQ-RAT-054).

---

## 4. Consumed entities / dependencies

| Entity | Owner | Via | Used for |
|---|---|---|---|
| ProductVersion, compiled product artefact | PFC | `pfc.Artifact.get` (cached by hash, REQ-RAT-074); `ProductVersion*` events → RAT read model `ProductVersionView` | Slot, catalogue, charge types, day-count, refund method |
| CoverageDef, CoverageTermDef, Option | PFC | `pfc.Catalogue.get/getItem` | Lookup keys; completeness lint |
| ChargeType (category, handling flat/proratable/fully-earned, tax class, tax-base flags, display group, parent links) | PFC | `pfc.ChargeType.list` | Output grain, proration, tax stage, breakdown |
| RegulatoryMapping (IPT class, SII LoB) | PFC | `pfc.RegulatoryMapping.get` | Tax requests per coverage |
| ProductReferenceTable | PFC | Not read by RAT; POL resolves the values into the input | — |
| POG record | PFC | REQ-PFC-007 | Commercial factor approval |
| PolicyTerm, Job, PolicyTransaction, Segment, risk tree | POL | POL calls RAT; `pol.Snapshot.get` as fallback for impact | Term-pinned artefact hash; worksheet attach |
| UWIssue | UW | — (UW raises issues from RAT signals) | — |
| AuthorityType/Grant, ApprovalRequest, AuditEvent, User, SodRule | PLT | `plt.Authority.check`, `plt.AuthorityType.register`, `plt.Approval.request`, `plt.Audit.append` | Deviations, approvals, audit |
| ConfigValue, configuration hash, SPI binding | MKT | `mkt.Configuration.resolve`, `mkt.Spi.bind`, `mkt.Rounding.apply` | Rounding, order of operations, tax point basis, pack params |
| Household/account counts | PTY (through POL) | Input counts only, never member identities (REQ-RAT-120) | Multi-policy discount |
| Activity | WRK | `wrk.Activity.create` | Reviews, remediation, notifications |
| Model, ModelVersion; populations; bias tests | DAT | `dat.Population.sample/aggregate`, `dat.BiasTest.run`, `dat.Model.register`, `dat.DataContract.register` | Impact, AI, monitoring |
| AiSystem | CMP | `cmp.AiSystem.register`, `cmp.Submission.create` | AI register; notification hook |
| Documents/archive | DOC | `doc.Document.request`, `doc.Archive.store` | Governance bundles, statements, import originals |
| Claims-history certificate content | CLM (REQ-CLM-008), passed via POL input | Input | Entry class |

Platform services used (§9.2): PLT outbox, "workflow engine" (REQ-PLT-007), time service (REQ-PLT-332), config/feature flags, retention (REQ-PLT-011), AI gateway (REQ-PLT-010), observability (REQ-PLT-013), malware scan (REQ-PLT-161).

**Assumptions (§16.2):**
1. POL calls RAT synchronously in-process and stores product artefact hash, rating artefact hash and resolution hash on each transaction.
2. POL resolves all external data (valuation, geocoding, hazard zones, household counts, certificate content) before rating; RAT makes no external calls on the rating path.
3. MKT delivers `PricingConstraint` with the EU default prohibited list for the MVP.

---

## 5. Events

Envelope per contract §3.4.1. Payloads carry no P2/P3 data. Delivery is through the transactional outbox (REQ-PLT-005). `RatingCalculated` is written to the outbox in the same transaction as the worksheet store (§8.1).

The PRD uses Kafka-style wording: topic `rat.events.v1`, partition key `rating_slot_id` for rate-management events and `job_id` for `RatingCalculated` and `PricingModificationDecided`. See §14 for the stack conflict.

**Produced (§8.1):**

| Event | Trigger | Key payload | Consumers |
|---|---|---|---|
| `RatingArtifactPublished` | Artefact Approved and stored | hash, label, slots, compatible product versions + hashes, input-schema version, approval refs, state | PFC (RatingArtifactView), DAT |
| `RateVersionScheduled` | scheduled, rescheduled or cancelled | hash, slot, txn type, effective_from, channel scope, action, reason | PFC, DAT |
| `RateVersionActivated` | activation time reached | hash, slot, txn type, effective_from, predecessor hash | PFC, POL, CHN, DAT |
| `ShadowRunCompleted` | completed or stopped | run id, candidate + production hash, window, count, distribution summary, status | PFC, DAT |
| `RateVersionWithdrawn` (R-22) | rollback | withdrawn hash, reactivated hash, slots, reason, affected quote/txn counts | PFC, POL, DAT |
| `ImpactAnalysisCompleted` (R-22) | impact run done | run id, hashes, population, metrics summary | DAT |
| `PricingModificationDecided` (R-22) | Approved, Rejected or Invalidated | modification id, job, quote version, value, status, decided by | UW, POL, DAT |
| `RatingCalculated` (R-22) | FULL, QUICK, ENDORSEMENT, RENEWAL rating (not DRY_RUN or CANDIDATE) | rating key, lineage keys (quote, job, txn, per D5), product, slot, mode, channel, producer, rating-cell key + level codes, premium and tax totals per coverage, signal codes, worksheet id, bindable | DAT |

Timing targets:
- PFC's view must reflect artefact changes within 5 s (REQ-RAT-066).
- `RatingCalculated` must appear within 5 s (REQ-RAT-249).
- All nodes must resolve a new activation within 60 s (REQ-RAT-221).

Remediation after rollback goes to WRK via API, not via the event.

**Consumed (§8.2), all deduplicated on `event_id`:**

| Event(s) | From | Reaction |
|---|---|---|
| `ProductVersionApproved`, `ProductVersionPublished`, `ProductVersionScheduled` | PFC | Update ProductVersionView; compute compatibility; warn activator via WRK if none (REQ-RAT-067) |
| `ProductVersionRetired` | PFC | Mark slot compatibility; enable artefact retirement checks |
| `ProductReferenceTablePublished` | PFC | Flag dependent artefacts for re-validation |
| `ConfigChanged` | PLT | Refresh config cache (requests still pin their hash) |
| `ConfigurationActivated`, `PackActivated`, `PackRolledBack`, `RateTableChanged` | MKT | Re-run golden suites of affected slots in non-prod; warm SPI caches; notify pricing team of tax-rate changes |
| `PolicyBound`, `PolicyChanged`, `RenewalBound`, `PolicyRewritten`, `PolicyReinstated`, `PolicyCancelled` | POL | Reconcile worksheet attachment |
| `TransactionReversed` | POL | Keep worksheets attached |
| `QuoteExpired` | POL | Make quote worksheets eligible for deletion |
| `ApprovalDecided` | PLT | Advance artefact, modification and re-baseline states |
| `AuthorityGrantChanged` | PLT | Invalidate cached authority hints (checks always live) |
| `DSARReceived` | CMP | Prepare worksheet export |
| `LegalHoldApplied`, `LegalHoldReleased` | PLT | Apply or release holds |
| `AiToggleChanged`, `AiKillSwitchActivated` | PLT | Enable or disable AI-RAT within 60 s |
| `ModelDriftDetected`, `BiasThresholdBreached` | DAT | Flag or disable the AI feature; for bias on tariff factors, open a compliance activity |

---

## 6. APIs

**Exposed (§9.1; "in-process; REST under `/api/rat/v1/`"):**

| Operation(s) | Purpose | Notable errors | Idempotency |
|---|---|---|---|
| `rat.Rate.rate` | Sync rating | `RAT-ERR-ENVELOPE`, `-INPUT`, `-INPUT-UNDECLARED`, `-PROHIBITED-INPUT`, `-UNKNOWN-ARTEFACT`, `-INCOMPATIBLE-ARTEFACT`, `-DOMAIN`, `-HIT-POLICY`, `-TAX`, `-ALLOCATION`, `-CURRENCY`, `-SCHEME`, `-FORBIDDEN-MODE` | Optional Idempotency-Key, 7-day replay (REQ-RAT-048); dry-run via mode DRY_RUN |
| `rat.Rate.rateBatch` / `getBatch` / `getBatchResults` / `cancelBatch` | Batch up to 10,000 items, priority classes (renewal, impact, migration, ShadowRun) | `RAT-ERR-BATCH-LIMIT` | Key; restartable by item key |
| `rat.Worksheet.get` / `explain` / `reperform` / `export` | Worksheet access | `-WORKSHEET-NOT-FOUND`, `-RETENTION-EXPIRED` | Query |
| `rat.Worksheet.attach` | Link worksheets to a bound txn | — | Key |
| `rat.Proration.prorate` / `rat.DayCount.yearFraction` | Proration | `RAT-ERR-CONVENTION`, `-PERIOD` | Pure |
| `rat.PricingModification.propose` / `withdraw` / `get` / `list` | Deviations | `-REASON-REQUIRED`, `-AUTHORITY-DENIED`, `-FLOOR` | Key; dry-run |
| `rat.Breakdown.get` | Customer/intermediary breakdown | `-FORBIDDEN` | Query |
| `rat.ChangeExplanation.get` | Change decomposition | — | Query |
| `rat.PriceReview.request` / `decide` | Art. 22 human review | `-FORBIDDEN` for AI identities | Key |
| `rat.RatingArtifact.resolve` | Active artefact for slot/date/knownAt | `-NO-ACTIVE-ARTEFACT` | Query |
| `rat.RatingArtifact.get` / `list` / `compatibility` | Metadata | — | Query |
| `rat.RatingChangeSet.create`, `rat.RateTable.import` / `validate` / `diff` | Authoring | `-IMPORT-INVALID` | Key; dry-run |
| `rat.RatingArtifact.build` / `submit` / `return` / `schedule` / `cancelSchedule` / `rollback` | Lifecycle | `-EVIDENCE-INCOMPLETE`, `-NO-COMPATIBLE-SLOT`, `SOD-VIOLATION`, `PLT-ERR-SELF-APPROVAL` | Key; dry-run |
| `rat.GoldenSuite.run` / `requestRebaseline` | Testing | — | Key |
| `rat.Comparison.compareVersions` | 2–5 artefacts | `-INPUT-SET` | Key |
| `rat.ShadowRun.start` / `stop` / `get` | ShadowRun | `-SHADOWRUN-CONFLICT` | Key |
| `rat.ImpactAnalysis.run` / `get` / `resume` | Impact | `-DATA-UNAVAILABLE` | Key |
| `rat.BonusMalus.transition` | Next class (pure) | `-SCALE` | Pure |
| `rat.Statement.get` | Published statements | — | Query |
| `rat.LegacyRating.import` | Migration | `-IMPORT-INVALID` | Key; dry-run |
| `rat.Dsar.export` | DSAR fan-out target | — | Key |

Error model:
- Errors follow RFC 9457 with a `RAT-ERR-*` catalogue, GR/EN messages, a `retryable` flag and the correlation id (REQ-RAT-050).
- Warnings are distinct from errors: `W-DEFAULT-USED`, `W-ARTEFACT-WITHDRAWN`, `W-EXTRAPOLATED`, `W-DEFAULT-ROW`, `W-MODIFICATION-INVALIDATED` (REQ-RAT-037).
- Build lint codes: `RAT-LINT-*` (NONDETERMINISTIC, CYCLE, DECLARATION, OPTION-UNRATED, CHARGE-MISSING, TAX-IN-ALGORITHM, I18N, UNUSED-TABLE, PROHIBITED-FACTOR).
- Validation codes: `RAT-VAL-*` (DOMAIN-UNDECLARED, MONOTONIC).
- A rating call is all-or-nothing; no partial results (REQ-RAT-051).

**Request envelope (REQ-RAT-030):** legal entity, jurisdiction, product code, product artefact hash, rating artefact hash, configuration hash, mode, transaction type, rating basis date, tax point date, currency, channel, producer code, business lineage keys (quote id, job id, transaction id), correlation id, optional idempotency key. The lineage keys link rating to the journey; the W3C trace id is technical only (D5).

**Modes:**
- FULL (REQ-RAT-038), ENDORSEMENT (039), QUICK (040, `bindable=false`, defaults listed), RENEWAL (041, prior rates required), DRY_RUN (042, identical prices, 24 h worksheet), CANDIDATE (043, internal only).
- `origin = MIGRATION` flag (REQ-RAT-044).

**Response (REQ-RAT-036):**
- Per segment × element × charge type: annual rate at scale 4, currency, charge category, coverage code, element id, producing step, flat/proratable handling.
- Plus signals, warnings, defaults used, automated-decision flag (true for FULL, ENDORSEMENT, RENEWAL), bindable, worksheet id and hash.
- The full worksheet is included only when `explain=true` (REQ-RAT-057).

Limits: up to 400 segments per request, configurable; identical segments are reused within a request (REQ-RAT-035).

**Consumed APIs:** PFC, MKT, PLT, WRK, DOC, DAT, CMP and POL calls as listed in §4. `Geocoder` is called only by the test harness and for impact re-keying (§9.2). `pfc.ProductVersion.resolve` is called only by the test harness and for impact population defaults.

**Inbound callers (§15.1):**
- POL: rate, batch, proration, attach, breakdown, bonus-malus transition.
- UW: REQ-UW-096/273 rating, worksheet viewer, impact.
- CHN: dry-run, breakdown, change explanation, broker deviations.
- PFC: golden policies, impact for REQ-PFC-208.
- MKT: impact preview through `rateBatch` (REQ-MKT-054).
- MIG, RI (proration of ceded premium, REQ-RI-070/147), BIL, CLM, DOC (breakdown in documents, REQ-DOC-133), CMP (DSAR).

---

## 7. SPIs / country-pack interfaces

| SPI | Operations used | Where |
|---|---|---|
| `TaxCalculator` (contract §3.5.8, REQ-MKT-087) | `calculate` (rate mode and amount mode), `treatment(chargeType, transactionKind, cancellationSource)` (contract D2) | REQ-RAT-009, 109–112, 162. Fail closed on RULE_MISSING or VALIDATION; never a core default tax. Levies are folded into TaxCalculator; there is no `LevyCalculator` (C-06) |
| `PricingConstraint` (R-05, extended R-24; REQ-MKT-110) | `prohibitedFactors`, `proxyAttributes`, `fairnessDefaults`, `check`, `maxChange` | Build-time and runtime checks; renewal ceiling (REQ-RAT-098, 144, 181) |
| `Geocoder` (R-17, amended by CCR-RAT-07/R-26) | Hazard keys tagged with scheme code + version | Through POL inputs; harness and impact re-keying |
| `MotorDataProvider` | Vehicle data and valuation | Through POL inputs, with provenance |
| `RegimeCodeList` | via PFC IPT classes | §10.4 |
| MKT config/rounding | `mkt.Configuration.resolve` (by hash), `mkt.Rounding.apply` (purposes RATE, PREMIUM, tax class) | REQ-RAT-072, 102, 159 |

SPI binding is resolved per legal entity, risk location and date (REQ-RAT-073). Example: a Cyprus-registered vehicle on a Greek entity's policy uses the Cyprus `TaxCalculator`.

**Cyprus stub (§10.4, REQ-RAT-154, 194):**
- Different bonus-malus scale and entry rules.
- Synthetic `CY-STAMP` flat per-policy charge, effective to 2025-12-31, with no stamp line from 2026-01-01.
- Motor fund levy via the Cyprus TaxCalculator; golden cases assert 5% of MTPL premium (synthetic).
- Synthetic renewal cap (an example in REQ-RAT-098 shows +15%).
- Pinned binding mode.
- EU default prohibited factors.
- Runs in CI on every main-branch build.

There is also a Bulgaria fixture with a per-vehicle levy (REQ-MKT-272, §14.x).

---

## 8. Screens

Shared conventions (§6.0):
- Staff screens live in a pricing workbench using design-guide patterns: quiet chrome IB-24, sidebar work views IB-03, keyboard lists IB-02, density IB-16, command palette IB-01 (REQ-WRK-006), status system IB-21, binary pass/fail IB-32, edit-in-place IB-14 (Draft only), approve-the-diff IB-07, explain-why IB-08.
- Semantic states: `adverse`, `pending-approval`, `stale`, `read-only`, `locked`, `offline/degraded`.
- Greek labels marked † are working translations; ‡ marks glossary-of-record corrections, e.g. "Αρχείο τιμολόγησης ‡" for rating artefact (XMR-F-128).
- **R-101 ΕΛ|EN switch** on every screen and component, changing language without reload or loss of input. It never changes the language of published statements or customer documents (Must, P1).
- Permissions: `rat.read`, `.author`, `.approve`, `.activate`, `.emergency`, `.worksheet.read`, `.worksheet.read.sensitive`, `.deviation.propose`, `.review.decide`, `.breakdown.extended`, `.monitor.read`, evaluated with ABAC on legal entity, jurisdiction and LoB.
- WCAG 2.2 AA everywhere, with tabular equivalents for charts.

| ID | Name | Personas | One line |
|---|---|---|---|
| SCR-RAT-01 | Rate management home | Actuary, analyst, approver, activator, product owner | Work-left home (IB-11), cards for work waiting, slot table with activation timeline, hero metric (IB-17) |
| SCR-RAT-02 | Rate table browser and version comparison | Actuary, analyst, auditor, UW (read) | Split pane (IB-05); cell-level diff; read-only in production (implements UIL-U8) |
| SCR-RAT-03 | Rating algorithm and factor viewer | Actuary, analyst, compliance | Step graph per coverage with factor declarations; Draft editing; pinned comments IB-20 |
| SCR-RAT-04 | Table import wizard | Actuary, analyst | Upload → Map → Validate/commit (IB-12, IB-28); file ≤ 50 MB with malware scan; outlier acknowledgement comment ≥ 10 chars; AI-RAT-02 mapping |
| SCR-RAT-05 | Rating test harness | Actuary, analyst, UW (Active only), partner dev (sandbox) | Form generated from the input schema; step-by-step worksheet; compare two artefacts; save as golden (synthetic only) |
| SCR-RAT-06 | Golden test results and re-baseline approval | Actuary (maker), actuarial approver (checker) | Per-suite pass/fail; expected vs actual; re-baseline justification ≥ 30 chars |
| SCR-RAT-07 | ShadowRun comparison dashboard | Actuary, analyst, product owner | Filter bar IB-26; sample 0.1–100% (default 10); window ≤ 30 days; error state when > 1% candidate failures |
| SCR-RAT-08 | Impact analysis dashboard | Actuary, analyst, PO, compliance, approvers | Distribution, capped vs uncapped, fairness panel, written-premium change; stale when as-of > 7 days |
| SCR-RAT-09 | Rate version approval, scheduling and rollback | Approvers, PO, compliance, activator | Governance bundle checklist, approvals panel, schedule (NB and renewal dates), rollback; reason ≥ 20 chars; presence IB-19 |
| SCR-RAT-10 | Worksheet viewer (embedded) | UW, CSR, policy services, auditor, DPO | Hosted in POL/UW screens; P2 inputs masked without permission; re-perform; PDF export via DOC |
| SCR-RAT-11 | Pricing modification panel (embedded) | UW, UW manager, broker | Live authority check; before/after dry-run price; justification ≥ 20 chars; AI-RAT-05 suggestion |
| SCR-RAT-12 | Customer premium breakdown (embedded) | Customer, agent, broker, bank employee | Statement view IB-13; coverage, discount, tax/levy and fee lines; factor categories with direction; review link |
| SCR-RAT-13 | Price change explanation (embedded) | Customer, intermediary, CSR, UW | Waterfall of effects in declared order; AI-RAT-04 summary beside the deterministic list |
| SCR-RAT-14 | Bonus-malus scale editor/viewer | Actuary, compliance, CSR | Class table, transition matrix, entry rules, class-path simulator |
| SCR-RAT-15 | Rating monitoring dashboard | Actuary, analyst, PO, compliance | Conversion, retention, loss ratio, adequacy, fairness by cell; stale when DAT data > 48 h; IB-30 delegated to DAT |

Inventory: RAT owns UIL-U8 and UIL-U10, both specified. It supplies the rate side of UIL-U9 (consumed from PFC) and is embedded in UIL-P1 (CHN). It references 28 consumed reference screens: POL submission, transaction and policy-file screens; PLT authority profiles SCR-PCADM1-14 and SCR-PCADM2-01; UW rules; PFC designer (§6.16).

---

## 9. Regulatory, tax and statutory rules

### 9.1 Stated explicitly in the source

| Rule | Exact statement | IDs |
|---|---|---|
| Claims-history certificate (Greece) | Law 5113/2024 Art. 7, replacing Art. 6γ of PD 237/1986: right to a certificate covering **at least five years**, answered **within fifteen days** on the Commission template (§1); no discrimination by nationality or previous member state of residence (§2); certificates from other member states treated equally for premium and discounts (§3); insurer publishes its general policy on certificate use (§4); commercially sensitive detail need not be published (§6). Verified 2026-10-07 (taxheaven.gr) | OBL-MOT; REQ-RAT-140–143, 245; BR-RAT-020, 021 |
| EU claims-history | Directive 2009/103/EC Art. 16 as amended by Directive (EU) 2021/2118; Commission Implementing Reg. (EU) 2024/1855 of 3 July 2024 (template sections A–F, applies from **24 July 2025**). Issuing state is never a rating factor. Art. 16 numbering taken from secondary sources (OI-RAT-10) | REQ-RAT-140, 141 |
| Gender | Directive 2004/113/EC; CJEU C-236/09 Test-Achats (1 Mar 2011) invalidating Art. 5(2) **with effect from 21 Dec 2012**; Greek Law 3769/2009 (FEK 105/A/1.7.2009). Sex/gender never a factor, directly or by proxy. Other non-discrimination grounds are "Verify" | OBL-EQT; BR-RAT-022; REQ-RAT-144 |
| GDPR | Arts. 5(1)(c), 13(2)(f), 14(2)(g), 15(1)(h), 22; Greek Law 4624/2019; CJEU C-634/21 SCHUFA (7 Dec 2023), so the final price is treated as within Art. 22 scope by design | REQ-RAT-033, 036, 231, 238–242 |
| AI Act | Reg. (EU) 2024/1689 Annex III point 5(c): life and health pricing is high-risk, P&C is not listed; recital 12: rules defined solely by natural persons are not AI systems. Digital Omnibus Reg. (EU) 2026/1744 (OJ L 24 July 2026; in force 27 July 2026) postpones Annex III obligations | OBL-AIA; OI-RAT-05 |
| EIOPA AI opinion | Published 6 Aug 2025; proportionate governance of non-high-risk AI | §11 |
| IDD / price walking | Directive (EU) 2016/97 Art. 17(1); EIOPA-BoS-23/076 (March 2023): no unfair price walking, approval of differential pricing at a sufficiently high level, renewal monitoring | REQ-RAT-145–147, 207, 252, 256; BR-RAT-023–025 |
| Tariff notification | Solvency II Arts. 21(1) and 181(1): member states shall not require prior approval or systematic notification of premium scales; 181(2) allows communication of conditions for compulsory insurance. No filing workflow in core; pack flag `rat.tariff_notification_required` default false | OBL-BOG; REQ-RAT-219 (Could, P4); BR-RAT-032 |
| Statutory MTPL minimums | PFC finals (REQ-PFC-003); RAT must rate the statutory-minimum option; RAT holds no limits | REQ-RAT-075 |
| Solvency II LoB | Delegated Reg. (EU) 2015/35 Annex I lines; all premium allocated to coverage grain | REQ-RAT-105–108 |
| Nat-cat (P2/P3) | Greek Law 5116/2024 Art. 5 as amended by Law 5162/2024 Art. 25: mandatory business nat-cat cover (earthquake, flood, forest fire), priced as pack-driven PFC cover; motor-vehicle floor UNVERIFIED (OI-PFC-04) | REQ-RAT-148–151, 257, 262 |
| Accessibility | Directive (EU) 2019/882; Greek Law 4994/2022, treated as applying (R-57); WCAG 2.2 AA | NFR-RAT-017 |
| DORA | Reg. (EU) 2022/2554; Delegated Reg. (EU) 2024/1774 Art. 17 change management | REQ-RAT-213–226 |
| Residency | GDPR Chapter V; EU only | NFR-RAT-012 |
| Cyprus Stamp Duty | "the Cyprus Stamp Duty Law was repealed from 2026"; stub has no stamp line from 2026-01-01 | REQ-RAT-110 |

### 9.2 Premium build-up and charges (as stated)

**Stage order (BR-RAT-007, Core final):** technical premium → commercial factors → discounts → modifications → floors → minimum premium → renewal cap → fairness rule → commercial rounding → allocation → taxes and levies.

- **Technical vs commercial:** the worksheet separates the technical premium (risk and cost factors) from commercial adjustments (REQ-RAT-093). Expense, commission and profit loadings are cost-classified steps (REQ-RAT-094, Should; example 25% for `BANK_BRANCH`).
- **Discounts:** stacking groups use multiplicative, additive or best-of modes in declared sequence (REQ-RAT-118). There is an overall cap per coverage and per policy, default **40%** (BR-RAT-008), and a floor as a share of technical premium.
- **Technical floor:** default ratio **0.70** (BR-RAT-009). Modifications cannot breach it and emit `RS-FLOOR-APPLIED` (REQ-RAT-128).
- **Minimum premiums:** per coverage and per policy; a policy top-up is allocated pro rata (REQ-RAT-087).
- **Maximum premium:** applied as a cap, or as signal `RS-PREMIUM-ABOVE-MAX` (REQ-RAT-088).
- **Renewal cap:** applies to the tariff-driven change only, computed as the new artefact's rate on the prior risk divided by the prior rate; risk changes are then applied uncapped (REQ-RAT-096). Capping state is recorded so rates converge over successive renewals (097). A pack `maxChange` overrides a looser product cap (098, BR-RAT-013). `RS-RENEWAL-CHANGE` fires above ±20% by default (099, BR-RAT-011).
- **Renewal fairness:** renewal premium must not exceed the new-business-equivalent price on the same artefact by more than the tolerance. Default tolerance is 0%; default action is "signal", with "cap" recommended (BR-RAT-024, REQ-RAT-146). Tenure may reduce but not increase price (BR-RAT-025).
- **Allocation:** every premium output is attributed to a coverage and a PFC charge type, with nothing left unallocated at policy level (REQ-RAT-105). Rules are pro rata, to named coverages, or equal per element, with the rounding residual going deterministically to the largest line (106, BR-RAT-019). Exactness is checked and fails with `RAT-ERR-ALLOCATION` (108).
- **Discount as own charge type:** a discount can be its own charge type linked to a parent premium charge type, e.g. `DISC-MULTI` linked to `PREM-OD` (REQ-RAT-114).
- **Fees:**
  - A flat fee charge type such as `FEE-POL` (REQ-PFC-126) is a rating output only when the product defines it. It enters the tax base where REQ-PFC-117 flags it (REQ-RAT-111).
  - Example in the acceptance criteria: fee 10.00 with IPT 15% gives 1.50. These are illustrative numbers, not stated rates.
  - BIL taxes its own fees (contract D2).
  - Flat charges are never prorated, and fully-earned charges are returned in full for their segment (REQ-RAT-160).
- **Taxes and levies:**
  - Computed after the final premium by one `TaxCalculator.calculate` call per request. Inputs per element and coverage: annual premium rate, charge type, tax class, risk jurisdiction and subdivision, tax point date, policyholder type, transaction type, plus flat fees in the base (REQ-RAT-109).
  - Returned as separate charge types of category tax or levy, with tax class, base, rate or fixed amount, rule id and legal-source reference. Proportional taxes are annual rates; per-policy or per-unit amounts are flat charges (REQ-RAT-110).
  - Taxes are never folded into a premium rate (REQ-RAT-009). The algorithm may not produce tax lines (`RAT-LINT-TAX-IN-ALGORITHM`, REQ-RAT-115).
- **Stamp duty:** modelled as "category tax with tax class STAMP" (REQ-RAT-110).
- **Credits and reversals:** `TaxCalculator.treatment` decides the tax delta. Examples: "no reduction" produces no negative IPT line; "reduce pro rata" produces one. The rule id is recorded (REQ-RAT-109, 162; D2; CR-S3-10).
- **Tax on prorated amounts:** when `cur.order_of_operations` says "tax on base after rounding", proration recomputes tax on the rounded prorated premium in amount mode (REQ-RAT-162).
- **Multi-jurisdiction:** each element's jurisdiction goes to `TaxCalculator` (REQ-RAT-116, Should, P3).
- **Home IPT split (P2):** coverages with different IPT classes, e.g. FIRE `GR-FIRE-20` and WATER `GR-OTHER-15`, are split by the coverage structure, never post hoc (REQ-RAT-107).
- **Written premium (D5):** premium, surcharge and discount charge categories, net of reversals, excluding tax, levy and fee (REQ-RAT-208).

### 9.3 Rounding and precision (as stated)

- All factor, rate and money arithmetic uses **.NET decimal (28 significant digits, D10)**. Intermediate results use **round-half-even**. Rounding happens only at the explicit points of REQ-MKT-006. No binary floating point is allowed on any rating, proration or tax path, enforced by a fitness test (REQ-RAT-100, BR-RAT-005).
- Table values: declared scale, at most **8 dp**; excess is rejected at import (REQ-RAT-101).
- **Rates are output at scale 4** through `mkt.Rounding.apply` purpose RATE, with the rule id recorded. The example rule id `RATE-HALF-UP-4` is labelled "pack data" (REQ-RAT-102, BR-RAT-006).
- Amounts:
  - Rounded by proration via `mkt.Rounding.apply`, purpose PREMIUM or the tax-class rule, following `cur.order_of_operations` (REQ-RAT-159).
  - Residuals are returned so POL can keep cumulative amounts exact.
  - Amount scale is per currency rule. The example `PREMIUM-HALF-UP-2` appears only in an acceptance criterion.
- Optional commercial rounding (to cents or whole euros) is an explicit step. The difference stays on the same coverage (REQ-RAT-103, Should).
- Reversal is the exact negation of amounts and residuals (REQ-RAT-163).
- Under TERM_RATIO, the unrounded sum over contiguous segments equals annual rate × term length exactly (REQ-RAT-158).
- Day-count conventions are ACT/365F, ACT/ACT, 30E/360 and TERM_RATIO, read from the product. An undeclared convention gives `RAT-ERR-CONVENTION` (REQ-RAT-156, 165).
- Periods are half-open `[valid_from, valid_to)` in the legal-entity time zone (REQ-RAT-157, D5).
- Short-rate tables are RAT tables of type SHORT_RATE, referenced by PFC refund methods. Refund methods per R-84: `ProRata`, `ShortRate`, `Flat`, `MinimumRetained`, `FullRefund` (REQ-RAT-161, §10.3).
- Bonus-malus factors use decimal scale 4 (SCR-RAT-14).

### 9.4 Referenced but NOT specified (gaps)

| Gap | Where referenced |
|---|---|
| **Greek IPT rates per class.** The class codes `GR-OTHER-15` and `GR-FIRE-20` and the "IPT 15%" example suggest 15% and 20%, but no rate is stated as law. The statute is cited as Law 5177/2025 Art. 43, with content in PRD-02 §3.2 and REQ-MKT-257 | OBL-TAX; REQ-RAT-009, 107, 111 |
| **Auxiliary Fund levy rate and base** (Law 5113/2024 Art. 14). Pack data only; the levy-split reading is open under OI-FIN-01; contract D2 commissions a tax and legal opinion, with "guarded Greece-pack defaults" until it arrives (REQ-MKT-055) | OBL-TAX |
| **Greek stamp duty.** Not mentioned for Greece at all. Only the generic STAMP tax class and the Cyprus fixture appear | REQ-RAT-110 |
| Greek tax rounding rules, `cur.order_of_operations` and `tax.tax_point_basis` values | §10.2 (owned by MKT/PRD-17) |
| Default day-count convention for Greek motor ("TERM_RATIO recommended", to be confirmed with finance) | §16.5 item 4 |
| Retention durations of RC-RAT-DEF, -GOV, -WS (programme schedule) | §7.1 note; OI-RAT-11 |
| Greek transposition of SII Arts. 21/181 (Law 4364/2016 article) and Bank of Greece practice: UNVERIFIED | OI-RAT-02 |
| Cyprus pricing constraints: UNVERIFIED | OI-RAT-03, C-11 |
| Statutory MTPL minimum amounts (PFC) | REQ-RAT-075 |
| Art. 22 applicability given Art. 22(2)(a) | OI-RAT-06 |
| Other non-discrimination grounds ("Verify") | OBL-EQT |
| Nat-cat motor-vehicle floor UNVERIFIED | OBL-NATCAT |
| Five-zone seismic map legal adoption timing | OI-RAT-13 |
| Bias test band 0.8–1.25 is from contract §3.8.5; the specific proxy attributes come from `PricingConstraint.proxyAttributes` (pack) | BR-RAT-035 |

### 9.5 Deferred to configuration or country pack

- **Pack (MKT):** tax and levy rates, bases and rounding; prohibited-factor lists (EU default list includes sex and nationality); proxy attributes; fairness defaults; `maxChange`; `rat.tariff_notification_required`; `rat.location.min_granularity`; deviation reason codes.
- **Artefact (product data):** bonus-malus scale (no compulsory Greek scale exists; class count is product data, BR-RAT-016); renewal cap; minimum premiums; discount caps; floor.
- **Entity layer (L4):** fairness action and tolerance (OI-RAT-08 open); revalidation 30 days; outlier 25%; emergency review 5 working days.

---

## 10. Greek-market specifics

- **AFM:** mentioned only as a personal-data pattern that blocks golden-case commits (REQ-RAT-182). AFM is never a rating input.
- **myDATA, gov.gr, Information Centre/bureau:** not mentioned anywhere in PRD-03.
- **Greek law cited:**
  - Law 5113/2024 Art. 7 (claims-history) and Art. 14 (Auxiliary Fund levy).
  - Law 5177/2025 Art. 43 (IPT).
  - Law 3769/2009 (gender equal treatment).
  - Law 4624/2019 (GDPR implementation).
  - Law 4364/2016 (SII transposition, UNVERIFIED).
  - Law 5116/2024 Art. 5 as amended by Law 5162/2024 Art. 25 (nat-cat).
  - Law 4994/2022 (accessibility).
  - PD 237/1986 Art. 6γ.
- **Supervisor:** Bank of Greece. It reads governance bundles and the claims-history usage statement on request; there is no systematic notification (ROLE-44).
- **Greek motor inputs (REQ-RAT-135):**
  - Location and vehicle: rating region (postcode → RAT region table), make/model group, value, power kW, engine capacity, fuel, vehicle age, use.
  - Driver and history: driver age, years since licence, bonus-malus class, claims-history summary, owner type.
- **Bonus-malus:**
  - There is no compulsory scale in Greece, but company scales are near-universal and "bonus-malus protection" is sold as an add-on. A new insurer places the driver on its own scale (gocar.gr market practice).
  - The scale is modelled as a versioned transition table plus a protection flag; the transition function is pure (REQ-RAT-137–139).
  - Legacy class mapping is handled for migration (REQ-RAT-272).
- **Claims-history usage statement:** published in Greek and English through DOC (`DT-PRICING-STATEMENT`) before any artefact changing entry rules is activated. Default lead time is 1 day (BR-RAT-021).
- **Seismic map:** a new five-zone map (0.13g–0.37g, Eurocode 8 / ESHM20) replaces the three-zone one, reported in April 2026. Scheme codes `GR-SEISMIC-2003` and `GR-SEISMIC-EC8-2026` run in parallel (REQ-RAT-148–150, P2).
- **Language:**
  - All labels, templates and statements exist in Greek and English; the lint gate enforces it (REQ-RAT-104, NFR-RAT-018).
  - **Greek is the binding text** in customer documents (REQ-RAT-247).
  - The R-101 switch applies to all screens.
  - † and ‡ glossary markers are used; the legal reviewer is ROLE-46.
  - AI-RAT-04 includes a Greek customer transparency text.
- **Currency:** EUR only in the MVP. A non-product currency gives `RAT-ERR-CURRENCY` (REQ-RAT-053). Multi-currency is REQ-RAT-266 (Won't, P4).
- **Time zone:** activation dates are in the legal entity's zone, e.g. Europe/Athens (REQ-RAT-220).

---

## 11. Controls

**Authority types registered with PLT (§12.2):**

| Type | Dimensions | Used for |
|---|---|---|
| `RAT.PricingDeviation` | product, LoB, channel, txn type, signed deviation %, premium amount + currency | REQ-RAT-005, 126, 267 |
| `RAT.RateArtifactApproval` | product line, jurisdiction, change class (factor-only, structural, new factor, commercial factor) | REQ-RAT-215 |
| `RAT.RateActivation` | product line, jurisdiction | REQ-RAT-220 |
| `RAT.EmergencyRateChange` | product line, jurisdiction | REQ-RAT-224 |
| `RAT.PriceReviewDecision` | product line, channel | REQ-RAT-241 |

**Maker-checker (§12.3):**
- Rating artefact approval: actuarial approver plus product owner. Compliance is added when a factor is added, a classification changes or a commercial factor is involved. No approver may be an author (REQ-RAT-215).
- Golden re-baseline, by an approver who did not author it (REQ-RAT-188; CCR-RAT-06/R-25).
- Referred pricing modifications, approved by someone other than the proposer (REQ-RAT-127).
- Emergency rollback: product owner plus compliance officer, neither being the author or activator, with a retrospective review within 5 working days via WRK (REQ-RAT-224).
- RAT config changes at country or entity layer (through MKT).
- AI toggles at tenant or entity level (PLT).
- Commercial factors need product-owner approval with a POG reference (REQ-RAT-145).
- Bias test result from DAT must be within band, or a compliance-approved justification is needed (REQ-RAT-147, Should).

**SoD rules (registered in PLT SodRule, §12.4):**

| Rule | Severity |
|---|---|
| Change-set author ≠ artefact approver | Block |
| Author or approver ≠ activator (BR-PLT-005) | Block |
| Re-baseline requester ≠ approver | Block |
| Modification proposer ≠ approver | Block |
| Emergency approver ≠ rollback activator | Block |
| Price-review decider ≠ user who set the reviewed modification | **Warn** |

**Audit (§12.1, REQ-PLT-002).** Audited actions:
- Table import, edits and outlier acknowledgements (comment required).
- Algorithm and declaration diffs.
- Build, submit, return, approve, schedule, cancel, activate and rollback (reason required for return, cancel and rollback).
- Re-baseline requests.
- ShadowRun and impact configuration.
- Modification lifecycle (reason code and justification required).
- Price reviews (reasoning required).
- Worksheet attach, export, re-perform and DSAR export.
- Access to P1 inputs with sensitive permission.
- AI interactions (`AiInteractionRecord`).

Write-once artefact and table store: any modification attempt is rejected and audited (REQ-RAT-071). The artefact envelope is signed and verified at load (NFR-RAT-013). Every import archives the original file with its hash through DOC (REQ-RAT-183).

**Governance bundle (REQ-RAT-214):** change summary, table diffs, validation and lint results, golden and oracle results, comparison, ShadowRun and impact reports, factor declarations, constraint check and approvals. It is hashed and archived through DOC.

**GDPR handling:**
- Inputs are restricted to the declared schema; undeclared attributes are rejected (REQ-RAT-033).
- Prohibited attributes are rejected at build and at runtime (REQ-RAT-144).
- Names, national IDs, nationality, sex, health and contact data are never inputs (§7.1 note).
- Personal-data scan on imports and golden cases (REQ-RAT-182).
- ShadowRun results are pseudonymous (REQ-RAT-202).
- Oracle data is pseudonymised and stays in the EU stamp (REQ-RAT-193).
- No personal data in OTel spans or logs, with CI log scanning (REQ-RAT-059, NFR-RAT-015).
- DSAR export of worksheets in Greek and English through the CMP fan-out (REQ-RAT-231).
- Legal holds (REQ-RAT-232).
- Art. 22 automated-decision flag, human review path, decisions by humans only (REQ-RAT-239–242).
- "How we calculate your premium" statement (REQ-RAT-238).
- DPO reviews input schemas per major version (NFR-RAT-016).
- Intermediaries get a fuller breakdown with `rat.breakdown.extended`. Commercially sensitive factor values are hidden from customers (REQ-RAT-235, 246; Law 5113/2024 Art. 7 §6).

---

## 12. AI features

All AI features are off by default and the module is complete without them (BR-RAT-030). No AI output is part of the rating function. Calls go through the PLT EU model gateway (REQ-PLT-010). The kill switch takes effect within 60 s. Every feature has an `AiInteractionRecord`, a DAT model registry entry (REQ-DAT-005) and a CMP register entry.

| ID | Name | Classification | Inputs | Human decision point | Phase / MVP need |
|---|---|---|---|---|---|
| AI-RAT-01 | Rate-change narrative for approvers | Minimal risk | Diffs and aggregate metrics (P0) | AI-suggested on SCR-RAT-09; author accepts or edits; 10 s timeout falls back to the deterministic summary | P1 per §1.5; not needed (recommended off by default, §16.5 #10) |
| AI-RAT-02 | Table import mapping assistant | Minimal | Headers + 20 sample rows (P0); files flagged as personal data never sent | Per-column accept or reject; top-1 accuracy ≥ 95% | P1; not needed (saved profiles suffice) |
| AI-RAT-03 | Anomaly detection in tables and ShadowRun results | Minimal | Table versions, aggregated metrics | Actuary marks "expected" or "fix" | P1; not needed (rule-based checks remain mandatory) |
| AI-RAT-04 | Plain-language price-change explanation | Limited risk (Art. 50) with high-risk-level accuracy controls (contract §3.8.1) | Decomposition effects only | Shown beside, never instead of, the deterministic list; never in renewal letters or archived docs (REQ-DOC-139); auto-disable on factual-error breach (> 2%) | P2 |
| AI-RAT-05 | Deviation recommendation for underwriters | Default high-risk controls (pricing-affecting) | Cell key, premiums, aggregated outcomes; protected characteristics and proxies excluded | Suggestion only; UW must propose herself (REQ-RAT-134) | P3 |
| AI-RAT-06 | Demand and retention estimate | Minimal, but outputs may not create loyalty-penalising factors (BR-RAT-025) | Aggregated renewal outcomes | Labelled AI-suggested; never feeds rating | P2 |
| AI-RAT-07 | Golden and edge-case generator | Minimal | Schema and table domains | Expected outputs always from the oracle or manual calculation, never AI | P3 |

None is needed for the MVP. The deterministic tariff executor is argued not to be an AI system under recital 12 (OI-RAT-05, pending CMP register decision).

---

## 13. Open issues / assumptions / CCRs

**Open issues (§16.1):**

| ID | Issue | Status |
|---|---|---|
| OI-RAT-01 | PFC↔RAT resolution must be proven by contract test REQ-PFC-221 vs REQ-RAT-064, with POL storing the artefact hash (XMR-CR-RAT-03) | Open until the test passes |
| OI-RAT-02 | Greek transposition of SII Arts. 21/181; BoG practice | Open, UNVERIFIED |
| OI-RAT-03 | Cyprus pricing constraints | Open, UNVERIFIED |
| OI-RAT-04 | Digital Omnibus citation | Closed (R-85, REG-007) |
| OI-RAT-05 | Tariff executor not an AI system; register entry | Open (CMP) |
| OI-RAT-06 | Art. 22 applicability to motor pricing | Open (DPO) |
| OI-RAT-07 | Live champion–challenger pricing | Excluded from MVP; open decision |
| OI-RAT-08 | Default renewal fairness action for the Greek entity (signal vs cap) | Open, "before first renewal season" |
| OI-RAT-09 | Mid-term migration / `LEGACY-PREMIUM` | Closed per D6 (scenario B) |
| OI-RAT-10 | Directive 2009/103/EC Art. 16 numbering | Open |
| OI-RAT-11 | Retention durations | Closed by citation to the programme schedule |
| OI-RAT-12 | Valuation provider and vehicle grouping | Open (POL/CHN supplier selection) |
| OI-RAT-13 | Five-zone seismic map adoption | Open (monitor) |
| OI-RAT-14 | Showing sensitive factor values to brokers | Open |
| OI-RAT-15 | DOC doc type `DT-PRICING-STATEMENT` | Closed. REQ-RAT-238's acceptance criterion still says "document type pending in DOC's catalogue, OI-RAT-15", which is stale text |
| OI-RAT-16 | Telematics bands held by POL before P2, otherwise REQ-RAT-123 dropped | Open |

**Assumptions (§16.2):**
1. POL calls RAT synchronously in-process and stores the hashes.
2. POL resolves all external data before rating.
3. MKT delivers `PricingConstraint` for the MVP.
4. Actuaries produce the tariff outside the system.
5. Rates are annual; P1 sells annual terms only.
6. Commission is not a rating output.

The C-01 to C-12 conflict resolutions in §3.3 also act as assumptions:
- Latency target is ≤ 200 ms, not the prompt's 300 ms.
- Motor volume is sized for 840k terms, with 700k as the low case.
- Levies go through `TaxCalculator`, not a `LevyCalculator`.
- Bonus-malus class count is product data.

**CCRs (§16.4), all accepted (R-20…R-27):**

| CCR | Ruling | Change |
|---|---|---|
| CCR-RAT-01 | R-20 | Retention codes RC-RAT-DEF, RC-RAT-GOV, RC-RAT-WS, RC-RAT-CANDRUN (90 days) |
| CCR-RAT-02 | R-21 | Three-hash rating key; PolicyTransaction gains rating artefact hash and resolution hash |
| CCR-RAT-03 | R-22 | Four new events |
| CCR-RAT-04 | R-23 | RatingArtifact canonical states |
| CCR-RAT-05 | R-24 | `PricingConstraint.proxyAttributes` / `fairnessDefaults` |
| CCR-RAT-06 | R-25 | Re-baseline and emergency rollback added to the maker-checker list |
| CCR-RAT-07 | R-26 | Geocoder scheme code and version |
| CCR-RAT-08 | R-27 | PFC events carry the rating-slot declaration |

The freeze fix (v1.1) added no new CCR.

**Ten pre-build decisions (§16.5):**
1. Binding (ruled).
2. Step-type list approval: still open; the expression language is closed by D10.
3. Decimal (closed, D10).
4. Greek day-count default (TERM_RATIO recommended): open.
5. Fairness action: open.
6. Golden-suite ownership and who maintains the Python oracle: open.
7. Storage budget: open; durations are closed.
8. Permitted commercial factors for the MVP and the POG path: open.
9. Notification and Art. 22 positions: open.
10. AI features in the MVP: open.

**Risks:** RK-RAT-01…08 (§16.3), covering binding errors, latency, proxy discrimination, import bypass, worksheet growth, rollback remediation, unverified regulation and step-language limits for P3.

---

## 14. Conflicts and ambiguities found

### (a) With the infra stack (ARCHITECTURE-DECISIONS.md / INFRASTRUCTURE.md)

1. **Broker-style event vocabulary.** §8 says "Topic `rat.events.v1`; partition key `rating_slot_id` … `job_id`", and REQ-RAT-249 says the event "appears on `rat.events.v1` within 5 s". The stack has no message broker, only an outbox dispatched in order to in-process handlers. The topic and partition semantics need to be reinterpreted as outbox stream and ordering keys. Per-slot ordering must be guaranteed by the outbox dispatcher.
2. **"Lakehouse".**
   - §1.4: DAT owns "Model training, lakehouse".
   - REQ-RAT-193: "the export stays in the lakehouse workspace".
   - REQ-RAT-212: "export … to the lakehouse … data contract `rat.impact.v1`".
   - SCR-RAT-08 action "export to lakehouse"; §9.3 oracle "file contract in the lakehouse workspace"; §13.

   The stack explicitly excludes lakehouses; reporting uses PostgreSQL marts. The oracle exchange and impact exports need a different home, such as Blob Storage or a PostgreSQL mart.
3. **"Workflow engine".** REQ-RAT-221 says "At the scheduled time the workflow engine shall activate the artefact". §9.2 says "workflow engine" (REQ-PLT-007), and SYS-02 is "Batch job / workflow". The stack uses Hangfire, with no workflow servers. Activation should be a Hangfire job driven by RateActivation domain rows.
4. **Horizontal scale vs Container Apps sizing.**
   - NFR-RAT-005 requires "Linear to 16 nodes within 10%".
   - REQ-RAT-045 tests "four nodes behind the in-process router".
   - REQ-RAT-054 says batch is "processed in parallel across nodes".
   - REQ-RAT-056 requires worker pools isolated from interactive rating.

   INFRASTRUCTURE §6.2 has `api` at 0–3 replicas and `worker` at **exactly 1 replica**. The 400/600 ratings/s target (NFR-RAT-001), the 150,000 renewals per hour (NFR-RAT-004) and 1,000,000 impact terms in ≤ 2 h (REQ-RAT-210) are not evidently achievable on that sizing. A capacity decision is needed.
5. **Availability tier.** NFR-RAT-009 requires T1: 99.9%, **RPO ≤ 5 min, RTO ≤ 2 h**. INFRASTRUCTURE gives a production target of **RPO 15 min, RTO 4 h**. These conflict.
6. **"Stateless rating service" / "nodes" / "stamps".** §1.2 calls it "one fast stateless service", and NFR-RAT-012 says "Rating nodes may be shared across stamps". In a modular monolith RAT is a module inside the `api` process, so it cannot be shared across stamps (one resource group per stamp). The "service" wording should be read as a module.
7. **Dependencies the stack does not list:**
   - JSON Schema 2020-12 validation (REQ-RAT-031).
   - RFC 8785 canonical JSON (REQ-RAT-032, 060).
   - Spreadsheet workbook import (REQ-RAT-167).
   - An in-house CEL-compatible expression language (REQ-RAT-076, D10).
   - A "version-controlled repository" for algorithm sources (REQ-RAT-178; Git-backed or DB-backed is unspecified).
   - Artefact signing (NFR-RAT-013; Key Vault implied).

   Each needs a stated dependency decision under rule 11.
8. **Other languages.** The independent oracle is "the actuaries' Python model" (§14.x). It runs outside the system, but the file contract and execution environment are undefined.
9. **Vendor mentions.** Socotra, Guidewire, OpenL Tablets, Camunda DMN docs, Earnix, Akur8, hyperexponential and Instanda appear only as research references (§3.2, §16.7, §6.16). They are not adopted, so this is not a conflict.

### (b) With the system contract or other PRDs (as visible from PRD-03)

- **REQ-RAT-109 vs REQ-RAT-162:** the tax stage exists twice. Rating computes tax on annual rates; proration recomputes tax on rounded amounts when the order of operations says so. Which output POL must post as the charge (rate-derived or amount-derived tax) depends on `cur.order_of_operations` (PRD-17). This needs a single rule agreed with POL and BIL.
- **REQ-RAT-135 vs §9.4:** REQ-RAT-135 maps postcode to rating region through a **RAT** region table (table type REGION_MAP). §9.4 says values derived from PFC product reference tables "arrive in the normalised input, resolved by POL", and "RAT does not re-look them up". Ownership of the postcode→region mapping needs clarifying.
- **REQ-RAT-238:** still says the DOC document type is "pending … OI-RAT-15", but OI-RAT-15 is closed with `DT-PRICING-STATEMENT`.
- **Consumers of the four R-22 events:** CCR-RAT-03 lists PFC, POL, UW, WRK, DAT, CMP, CHN. §8.1 limits consumers to modules with handlers; for example, `RatingCalculated` goes to DAT only. Other PRDs need to agree.
- **REQ-RAT-094 vs §16.2 assumption 6:** REQ-RAT-094 has "commission … loadings" as rating steps, while assumption 6 says "commission is not a rating output". This is only consistent if the loading is a gross-up factor, not commission calculation (BIL CD-14).
- **NFR-RAT-001 vs contract:** contract §3.9.7 sets ≤ 200 ms; the prompt said 300 ms. This is resolved in favour of the contract (C-01).

### (c) Internal contradictions and ambiguities

1. **"Side-effect-free" vs persistence.**
   - REQ-RAT-001 calls `rat.Rate.rate` "synchronous, side-effect-free".
   - REQ-RAT-003 and NFR-RAT-001 include worksheet persistence; REQ-RAT-048 stores idempotency records; REQ-RAT-249 writes `RatingCalculated` to the outbox in the worksheet transaction.

   The pure function is the engine core (REQ-RAT-047, arch rule 3). The API operation has side effects. The design must split a pure `Rate(input, artefact, config) → result+worksheet` from a persistence shell.
2. **QUICK vs DRY_RUN.** J-01 (§4.1) says "Channel calls quick-quote dry-run (`REQ-RAT-040`)", and its diagram shows "create submission (dry-run, quick)" leading to `mode QUICK`. But QUICK and DRY_RUN are distinct modes with different retention and `RatingCalculated` behaviour: QUICK emits the event, DRY_RUN does not, and DRY_RUN worksheets last 24 h. Which mode an anonymous aggregator quote uses is unclear.
3. **Commercial rounding vs allocation order.** BR-RAT-007 puts commercial rounding **before** allocation. REQ-RAT-103 rounds "final annual coverage premiums". Policy-level items allocated after rounding could un-round the coverage premiums.
4. **ShadowRun failure thresholds.** §7.3 says the run is Failed when the candidate error rate is above 5%. SCR-RAT-07 shows `error` above 1% and `warning` above 0%. These are compatible as UI state vs lifecycle state, but should be confirmed.
5. **Fairness default.**
   - BR-RAT-024: default action "signal; recommendation cap".
   - Config default `signal`.
   - REQ-RAT-146's acceptance criterion uses "cap".
   - OI-RAT-08 is still open.

   The Greek-entity default is undecided.
6. **Rounding mode wording.** "Round-half-even for intermediate results" (REQ-RAT-100) sits beside the example output rule `RATE-HALF-UP-4` (REQ-RAT-102). This is consistent only because output rules are pack data. Builders must not hard-code either.
7. **.NET decimal precision.** BR-RAT-005 sets "precision 28". .NET decimal gives 28–29 significant digits with scale ≤ 28. Factor chains on large sums insured could hit scale limits silently. The overflow and rounding behaviour of intermediates needs a test policy.
8. **Golden suite size.** §14.x says "at least 2,000 motor cases at MVP" per slot. REQ-RAT-185's example uses 3,000. Not a contradiction, but the target is unclear.
9. **REQ-RAT-201 vs BR-RAT-036.** REQ-RAT-201 says the ShadowRun is "stoppable at any time"; BR-RAT-036 sets max 30 days and 100% sample. REQ-RAT-200's acceptance criterion requires production p95 to stay within 2 ms even at a 20% sample, which needs async off-path capture of inputs.
10. **Retention of PricingModification.** It "follows the transaction or quote", but the entity has no explicit link to the attaching transaction beyond the job.

### (d) Cannot be built without a decision

- Greek IPT class rates, Auxiliary Fund levy rate and base (OI-FIN-01 / D2 legal opinion), and any Greek stamp duty. RAT itself only needs the SPI, but no golden case can be baselined without pack values.
- The default Greek day-count convention (§16.5 #4).
- The renewal fairness action and tolerance for the Greek entity (OI-RAT-08).
- The step-type list approval (§16.5 #2) and the CEL subset specification, which is shared with PFC, UW and PLT.
- Oracle ownership, language and runtime (§16.5 #6).
- Which commercial factors are allowed in the MVP and the POG path (§16.5 #8).
- Capacity and hosting for the throughput NFRs vs Container Apps replica limits, and the RPO/RTO conflict.
- Where the impact and oracle "lakehouse" exports live.
- The valuation provider and vehicle-group source (OI-RAT-12), needed for real tariffs, though not for the engine.
- The worksheet storage budget (NFR-RAT-008 says ≤ 8 KB compressed average).

---

## 15. Build notes

**Hardest parts:**
1. **The rating engine core.** A typed step-graph DSL compiler, a CEL-compatible expression evaluator shared with PFC, UW and PLT, and immutable in-memory artefacts. The compiler covers step types, hit policies, banding, interpolation and domain behaviours, and the evaluator must be byte-deterministic. Targets: p95 ≤ 40 ms compute and ≤ 200 ms end to end at 400–600 ratings/s, with the worksheet persisted.
2. **Determinism and reproducibility.**
   - RFC 8785 canonicalisation and content-addressed artefacts, tables and worksheets.
   - Pinned compiler version; byte-identical rebuilds a year later (REQ-RAT-179).
   - Nightly re-perform of 10,000 worksheets (REQ-RAT-244).
   - Fitness tests banning float, clock and I/O on the rating path (NFR-RAT-022).
3. **Proration and tax correctness.**
   - Four day-count conventions, half-open periods, residual handling and exact reversal symmetry.
   - Short-rate tables.
   - Tax on credits via `treatment`.
   - Order-of-operations-dependent tax recomputation.
   - In-process and API parity over 100,000 generated cases (REQ-RAT-164).
4. **Governance pipeline.** Import, validation (gaps, overlaps, monotonicity, outliers, PD scan), build and lint, then golden, property, fuzz, oracle, Cyprus and performance runs. After that: CompareVersions, ShadowRun and impact analysis (1M terms in ≤ 2 h, resumable, snapshot-hashed), a frozen governance bundle, maker-checker with three-way SoD, scheduled activation per slot, transaction type and channel, and rollback with remediation lists.
5. **Bitemporal resolution.** `resolve(slot, txnType, basisDate, knownAt)` with separate NB and renewal effective dates, channel scope, no overlap (exclusion constraint), and PFC's view updated within 5 s. Term-pinning for in-term transactions.
6. **Renewal mechanics.** Tariff-only capping with convergence state, pack `maxChange`, new-business-equivalent re-rate for fairness, and a change decomposition that sums exactly in a declared order.

**Must exist first:**
- From MKT:
  - Configuration by hash.
  - `mkt.Rounding.apply`.
  - The SPI binding.
  - A `TaxCalculator` with `treatment`, even with stub values.
  - A `PricingConstraint` with the EU default list.
- From PFC: the product artefact with catalogue, charge types (handling, tax class, base flags), day-count, refund methods, rating-slot declaration and `ProductVersion*` events.
- From PLT: outbox, audit, authority check, maker-checker, SoD registry, time service, retention and legal hold.
- From DOC: archive and render.
- From WRK: activities.
- From DAT: populations (impact only).
- The Cyprus stub pack, for CI from day one.

**Suggested slicing (P1):**
1. **Pure core library.** Decimal arithmetic plus the fitness analyser, canonical JSON and hashing, the proration and day-count library (REQ-RAT-155–166, 276), and the bonus-malus transition. It has no dependencies and is property-testable immediately.
2. **Expression language and step-graph compiler.** Step types of REQ-RAT-077, tables with hit policies, bands, curves and domain behaviours, lint codes. Artefact = compiled bundle with hash.
3. **Rating runtime.** Envelope, input schema validation and normalisation, modes FULL, QUICK and DRY_RUN, allocation, tax hand-off via the SPI stub, signals and warnings, worksheet build, all-or-nothing. Golden-test harness in xUnit with a zero-cent gate.
4. **Persistence shell.** Worksheet store (content-addressed, retention states), idempotency, `attach`, `RatingCalculated` via outbox, OTel without personal data.
5. **Artefact binding.** Slots, compatibility vs PFC, RateActivation with `resolve`/`knownAt`, events to PFC, ENDORSEMENT and RENEWAL modes, term pinning.
6. **Discounts, deviations and authority.** PricingModification lifecycle, SCR-RAT-11.
7. **Rate management UI and pipeline.** Import wizard, table browser and diff, golden and re-baseline, governance bundle, approvals, scheduling and rollback (SCR-RAT-01–06, 09, 14).
8. **Evidence at scale.** Batch (Hangfire), CompareVersions, impact analysis, ShadowRun (SCR-RAT-07, 08).
9. **Transparency.** Breakdown, change explanation, statements, price review, DSAR (SCR-RAT-10, 12, 13).
10. **Monitoring.** SCR-RAT-15, data-quality alerts, price dispersion.

AI features and P2/P3 come later. This order gets a golden-tested motor tariff rating end to end through POL before any governance UI is built.
