# PRD-16 Digest — Data Migration and Coexistence (MIG)

Source: `core-insurance-prds/PRD-16-data-migration-coexistence.md` (1,951 lines, read in full). Version 1.2 (freeze fix, build baseline 1.0), dated 2026-10-07, binding input `00-system-contract.md` v1.11, plus PRD-18 decisions D1–D10 and ruling R-101 (§1.1).
Checked against: `core-insurance-infra/ARCHITECTURE-DECISIONS.md` (ADR).

---

## 1. Identity

| Item | Value |
|---|---|
| Module code | **MIG** |
| Title | Data Migration and Coexistence |
| Owner roles (§1.1) | Migration lead ROLE-41 (business/delivery owner); data steward ROLE-33 (DQ library, waivers); finance controller ROLE-24 (financial reconciliations); compliance officer ROLE-29 (regulator communication, hand-over evidence); DPO ROLE-30 (archive privacy); release manager ROLE-40 (cut-over runbook); lead architect (design authority) |
| Scenario notation | **A** = new licence, no book to convert. **B** = existing book converted (e.g. a Fairfax-group Greek P&C insurer). **Scenario B is the planning basis (D6)**; the programme board confirms the scenario before build wave **W5** (`REQ-MIG-212`). Requirement tables have two MoSCoW columns. The first (scenario B) **is the P1 cut** (rulings R-86, R-102). The scenario-A column is informative (§1.1). |

**Purpose (§1.2).** MIG moves an insurer's existing book, history and balances from one or more legacy systems into the core. It also keeps the two systems consistent while both are live. **It owns no business behaviour.** Every load goes through the target module's own import API. That API applies the same validation as live business and emits the same events, flagged `origin=MIGRATION` (contract §3.4.1). MIG owns:
- the per-object strategy decisions
- the DQ and mapping machinery and the pipelines
- the legacy-to-core cross-reference
- the renewal-based migration pipeline
- coexistence routing and cross-system enquiry
- reconciliation and sign-off
- rehearsals and cut-over
- the legacy archive and its data-subject operations

**Five key decisions (§1.2).**
1. One import contract for every load (`REQ-MIG-001`), proven by a conformance harness before the first mock load.
2. Personal lines default to renewal-based conversion, and the term is the unit of mastery. Mid-term and big-bang conversion are supported for books that must move at once.
3. Open claims are split by tail. Short-tail MD runs off in legacy. BI and long-tail claims convert in cohorts with full history and original clock start dates. Closed claims inside the claims-history period convert in summary. Older closed claims are archived.
4. Money must prove itself to the cent. Opening balances reconcile to legacy control totals and the legacy trial balance. Tax and levy periods split on a declared boundary. The core issues fiscal credits for legacy documents, correlated to the legacy MARK. No wave closes without named signatories.
5. Coexistence is engineered: a routing service (T1 during coexistence, R-74), cross-system enquiry, misdirected-payment rerouting, per-vehicle bureau hand-over, per-document myDATA hand-over, and an archive that outlives legacy.

**Units of hand-over (§3.2, own recommendation):**

| Unit | Hand-over or mastery it governs | Rule |
|---|---|---|
| Term | Mastery | BR-MIG-011 |
| Vehicle | Bureau hand-over | BR-MIG-022 |
| Fiscal document | Fiscal hand-over | BR-MIG-021 |
| Declaration period | Tax hand-over | BR-MIG-020 |

**Non-goals / out of scope (§1.4).**

| Out of scope | Owner |
|---|---|
| Target behaviour of business objects after load (validation, lifecycles, calculations) | Each owning module |
| Lakehouse modelling, regulatory marts, analytical legacy-zone models | DAT (`REQ-DAT-294`…`298`) |
| The choice of book and of scenario A or B | Programme board |
| Staff identities and role grants (provisioned from HR, not migrated) | PLT `REQ-PLT-001` |
| Re-authoring products, tariffs, UW rules and wordings | PFC/RAT/UW/DOC (MIG supplies cross-references only) |
| Fiscal transmission, bureau transport, clock engine, DSAR orchestration | CMP `REQ-CMP-001/002/003/005` |
| Legacy-side system changes | Legacy owner (MIG specifies them as interfaces L-01…L-08, §9.3) |
| Legal process for a portfolio transfer | Legal and compliance |
| Moving a deployment stamp between EU regions | PLT `REQ-PLT-279` |

**Scenario A footprint (§1.2, §14.y).** MIG shrinks to:
- reference-data loads
- intermediary and agreement loads (`REQ-MIG-145`)
- the conformance harness, which is also used for environment seeding
- registering any legacy number ranges

Otherwise MIG stays dormant. If the licence later acquires a book, scenario B is switched on for that legal entity.

---

## 2. Size metrics

Counts were recomputed from the tables by script and match §5.18 and §16.6.

| Metric | Count |
|---|---|
| Requirements total | **192** (6 anchors `REQ-MIG-001…006` + 186 module requirements `REQ-MIG-030…215`) |
| [BASELINE] / [ENHANCEMENT] | 186 / 6 (enhancements: 035, 041, 051, 065, 126, 180) |
| **Scenario B (P1 cut): Must / Should / Could / Won't** | **172 / 19 / 1 / 0** |
| Scenario A (informative): Must / Should / Could / Won't | 13 / 44 / 37 / 98. Scenario-A Musts: 001, 002, 038, 069, 072, 145, 166, 205, 206, 207, 209, 210, 212 |
| Phase tags | **191 requirements are P1 (Motor MVP)**; 1 is P4 (`REQ-MIG-126`, currency changeover, Could). None are P2 or P3 |
| **Motor MVP Must (P1, scenario B)** | **172** |
| Business rules | 40 (BR-MIG-001…040) |
| NFRs | 24 (NFR-MIG-001…024) |
| Screens | 10 (SCR-MIG-01…10). No inventory screens owned; 6 consumed for reference |
| Owned entities | About 30 entity types (§7.1), of which 7 are contract-listed (MigrationWave, Batch, RecordOutcome, LegacyXref, MappingSpec, DQIssue, CutoverTask) |
| Events produced | 4 (`MigrationBatchLoaded`, `MigrationBatchReconciled`, `MigrationWaveStatusChanged`, `CoexistenceMasterChanged`) |
| Events consumed | 33 event types (§8.2) |
| Owned API operation groups | 21 (§9.1). Outbound calls go to all 16 other modules (§9.2) |
| Legacy and external interfaces | 8 legacy-side interfaces (L-01…L-08) + 8 external (E-01…E-08) |
| AI features | 8 (AI-MIG-01…08), all optional and off by default |
| Open issues / CCRs / local decisions | 15 OIs (5 closed), 8 CCRs (all accepted), 10 DEC-MIG decisions, 10 risks, 6 assumptions |

**Build-size estimate: L.**
- 172 Must P1 requirements is well above the 100 threshold.
- The money logic is heavy: reconciliation to the cent across BIL, FIN, CLM and RI, tax and levy boundary splits, and fiscal credit correlation.
- There is temporal and bitemporal logic: the routing table with half-open periods, term splits, clocks imported with original dates, and the IFRS 17 cohort date.
- It touches every other module.
- Most scenario-B functionality can only be *validated* against a real legacy book.

In scenario A the module is S (13 Musts).

---

## 3. Owned entities (§7.1, §7.3)

All rows carry `legal_entity_id`, `jurisdiction`, `created_at`, `created_by` and `record_version`. Internal ids are UUIDv7. MIG's schema holds no business object of another module. CoexistenceRoute and LegacyArchiveItem are cross-module visible (R-76).

| Entity | Key attributes | Constraints | PD class | Retention |
|---|---|---|---|---|
| SourceSystem | code, name, technology, owner, legal entity, default encoding, L-01…L-08 status | unique code per entity | P0 | RC-MIG-EVIDENCE |
| SourceDataset | source system, object type, extract method, as-at, row count, file/query hash, encoding, staging location | immutable after sealing | P0 | RC-MIG-EVIDENCE |
| StagingRecord | dataset, legacy key, **encrypted payload**, field encodings, profile flags | encrypted at rest | P1–P3 | RC-MIG-STAGING |
| DataDictionaryEntry | source field, type, meaning GR/EN, code values, consuming rules | — | P0 | EVIDENCE |
| ProfileResult | per-field stats (null rate, distinct, min/max, patterns, top values) | top values masked for P2/P3 | P0 | EVIDENCE |
| SourceQualityRule | code, class, severity, threshold, scope, owner, version, protected target requirement | versioned; maker-checker on activation | P0 | EVIDENCE |
| DQIssue | rule, cohort, severity, count, samples, owner, remedy, due, status, WRK activity ref | one open issue per (rule, cohort) | P0 | EVIDENCE |
| Waiver | issue, scope, justification, consequence, expiry, risk owner, maker, checker(s) | checker ≠ maker; a Critical waiver also needs the migration lead | P0 | EVIDENCE |
| MappingSpec / MappingRule | target operation, version, status, rules (attribute, type, expression, crosswalk, no-home treatment), tests | one Active version per target and date | P0 | EVIDENCE |
| CodeCrosswalk | domain, legacy code, core code, version | Approved before use | P0 | EVIDENCE |
| StrategyDecision (decision-matrix row) | object type, line, treatment, criteria, owner, legal basis, RC, approval | one Active row per object × line | P0 | EVIDENCE |
| MigrationWave | scenario, strategy, scope, dates, entry/exit criteria, go/no-go ref, status | — | P0 | EVIDENCE |
| Cohort | wave, rule version, population, schedule (extract, offer), status | — | P0 | EVIDENCE |
| Batch | wave, cohort, object type, target op, mapping version, **content hash**, dry-run ref, counts, status, parent batch | load hash = dry-run hash | P0 | EVIDENCE |
| RecordOutcome | batch, staging record, step, status, core ids, errors[], correlation id, idempotency key | one per (batch, record, step) | P0 | EVIDENCE |
| LegacyXref | source, object type, legacy key, core type, core id, business number, batch, valid from/to, status, re-point history | unique (source, type, legacy key, core type); never deleted while linked objects are retained | P1 | RC-MIG-XREF |
| MigrationMatchGroup / Candidate | records, score, rule hits, survivorship, decision, reviewer, PTY merge ref | customers are never auto-merged | P1–P2 | STAGING |
| RenewalConversionCase | cohort, legacy policy and term keys, conversion input, target product version, legacy expiring and renewal premium, new uncapped and capped premium, band, decision, reviewer, POL job ref, legacy stop ack, outcome | one open case per legacy policy per cohort | P1 | EVIDENCE |
| CoexistenceRoute | object type, key, master state, term split, effective from/to, reason, source event | non-overlapping effective periods per object | P1 | RC-MIG-XREF |
| LegacyIndexEntry | legacy keys, names native/Latin, AFM token, plate, numbers, status, master state, refreshed at | minimal attributes only | P1–P2 | STAGING (deleted at decommissioning) |
| SyncMessage | direction, party, attributes, sent, ack, conflict | listed attributes only | P1 | STAGING |
| MisdirectedPayment | receiving system, reference, amount, value date, resolved object, master, transfer status, approvals | maker-checker on transfer | P2 | EVIDENCE |
| HandoverRecord | kind (bureau, fiscal, levy, mandate, payment provider), object ref, hand-over date, legacy and core confirmations, recon ref | one per object and kind | P1 | EVIDENCE |
| ReconciliationReport / ControlTotal / Break | scope, measure, legacy and core totals, difference, status, root cause, owner, disposition, signatory, approver | signing requires every break dispositioned | P0 | EVIDENCE |
| SignOffPack | wave, contents, hash, DOC ref | sealed | P0 | EVIDENCE |
| Rehearsal | type (mock n, dress, rollback, production-stamp), env, dataset, results, defects, exit report | — | P0 | EVIDENCE |
| CutoverRunbook / CutoverTask | title GR/EN, owner, system, dependencies, planned and actual times, gate, rollback point, evidence, status | dependencies acyclic | P0 | EVIDENCE |
| ControlBoardEntry | kind (issue, risk, decision, action) | — | P0 | EVIDENCE |
| PurposeRegisterEntry | object type, original purpose, new purpose, compatibility, DPO decision | — | P0 | EVIDENCE |
| CommunicationPlanItem | wave, audience, message, channel, timing, approval, DOC ref | compliance approval before send | P0 | EVIDENCE |
| LegacyArchiveDataset / Item | dataset (format, schema, dictionary, hash, RC, entity); item (legacy key, xref, sealed payload ref, restriction flag, erasure status) | sealed; changes only via restriction, erasure or appended note | P1–P3 | RC-MIG-ARCHIVE |
| ArchiveDsarTask | CMP DSAR id, task id, op, scope, per-item outcomes | idempotent on task id | P1 | EVIDENCE |
| ChangeoverPlan (R-08, R-82) | old and new currency, fixed rate (6 significant figures), changeover_at, entities, modules, per-module progress, MKT rounding ref | later markets only; MKT reads it via `ChangeoverPlanView` | P0 | EVIDENCE |

**Retention codes (§7.1, R-20):**
- `RC-MIG-STAGING`: purged 90 days after wave sign-off; the legacy index is purged at decommissioning.
- `RC-MIG-XREF`: kept for the life of the longest-retained linked object.
- `RC-MIG-EVIDENCE`: 10 years after decommissioning. **The Greek value is UNVERIFIED (OI-MIG-06).**
- `RC-MIG-ARCHIVE`: per object type, mirroring the owner's retention class (e.g. `RC-CLM-*`).

**Only projection held:** `PolicyTermStatusView` (policy id, term number, status, start, end). It is fed by `PolicyBound`, `RenewalBound`, `PolicyCancelled`, `PolicyLapsed`, `PolicyNonRenewed` and `JobWithdrawn`. InForce is derived from `valid_period` and the time service, not from an event (XMR-F-204) (§7.2).

### State machines (§7.3)

**MigrationWave (§7.3.1).** Every transition emits `MigrationWaveStatusChanged`.

| From | To | Guard |
|---|---|---|
| Planned | EntryReview | plan complete |
| EntryReview | Ready | entry criteria met and go approved |
| EntryReview | Planned | no-go |
| Ready | InProgress | first batch or cohort started |
| InProgress | Suspended | migration lead suspends, with reason |
| Suspended | InProgress | resume approved |
| InProgress | Reconciling | all batches loaded |
| Reconciling | SignedOff | all reports signed; hand-overs reconciled |
| SignedOff | Closed | hyper-care exit criteria met |
| Ready, InProgress or Reconciling | RolledBack | rollback executed and reconciled to zero; only before the point of no return |

**Batch (§7.3.2).**
- Main path: Created → Extracted → Transformed → DryRunPassed → Loading → Loaded or PartiallyLoaded → Reconciled → SignedOff.
- Dry-run failure: Transformed → DryRunFailed → Transformed (after a fix).
- DryRunPassed → Loading is allowed only if the content hash is unchanged.
- Load failure: Loading → Failed → Loading (resume from checkpoint).
- Reversal: Loaded or PartiallyLoaded → Reversed.
- `MigrationBatchLoaded` is emitted on entering Loaded or PartiallyLoaded. `MigrationBatchReconciled` is emitted on SignedOff.

**DQIssue (§7.3.3, §4.3).**
- Open → Assigned → one of FixInSource, TransformRule, Deferred or WaiverRequested.
- FixInSource or TransformRule → Verifying → Resolved → Closed.
- Verifying → Assigned if the rule still fails.
- WaiverRequested → Waived (checker approves) or → Assigned (rejected).
- Waived → Closed when the wave closes.
- Issues emit no events; follow-up work goes through WRK activities.

**MappingSpec version (§7.3.4).** Draft → InReview → Approved → Active → Superseded, and InReview → Draft when returned. Entering InReview requires all tests to pass. Entering Approved requires checker ≠ maker.

**RenewalConversionCase (§7.3.5).**
- Extracted → Transformed.
- Transformed → Failed on a DQ or dry-run failure; Failed → Transformed when re-driven.
- Transformed → Rated → WithinTolerance or OutOfTolerance.
- OutOfTolerance → UnderReview → Approved or Excluded (including the deadline fallback).
- WithinTolerance → Approved.
- Approved → Offered once the POL renewal job exists and legacy has acknowledged the stop.
- Offered → Transformed (gap event and re-conversion), → Withdrawn (legacy cancellation), → Converted (`RenewalBound`), or → NotConverted (NotTaken, Lapsed or NonRenewed).
- Terminal states: Excluded, Withdrawn, Converted, NotConverted.

**CoexistenceRoute master state (§7.3.6).** Every transition publishes `CoexistenceMasterChanged`.
- LegacyMaster → Transitioning when the converted renewal job is created.
- Transitioning → CoreMaster when the core term starts.
- Transitioning → LegacyMaster when the renewal is withdrawn, excluded or rolled back.
- LegacyMaster → CoreMaster directly at activation of a mid-term or big-bang load.
- LegacyMaster → RunOffLegacy.
- LegacyMaster or RunOffLegacy → Archived.

**CutoverTask (§7.3.7).**
- NotStarted → Ready (dependencies done) → InProgress → Done or Failed.
- Failed → InProgress (retry).
- NotStarted or Ready → Skipped (approval required).
- Any state → Blocked → Ready.

**MisdirectedPayment (§7.3.8).**
- Detected → Matched → TransferRequested → PendingApproval → Transferred → Confirmed.
- Detected → Unmatched → Matched (manual match).
- PendingApproval → Rejected.

**ChangeoverPlan (§7.3.9).** Draft → Approved (maker-checker) → Running → Completed or CompletedWithExceptions. This maps one to one to MKT's `ChangeoverPlanView`.

**Cut-over flow (§4.7, informative).** Freeze → FinalExtract → Load → Reconcile → GoNoGo → Activate (go) or Rollback (no-go before the point of no return). Activate → HyperCare; Rollback → LegacyResumed.

---

## 4. Consumed entities / dependencies (§7.2, §9.2, §15.1)

| Module | What MIG uses | Via |
|---|---|---|
| PTY | Party, Account, Consent, Intermediary, ProducerCode, CommissionAgreement; merge and unmerge; bulk screening; party update for sync | `pty.Import.*` (`REQ-PTY-013`, 269–272), `pty.Party.merge/unmerge/update` (`REQ-PTY-007`, `001`), `pty.Screening.bulk` (`REQ-PTY-185/006/272`); events `PartiesMerged`, `PartyUnmerged`, `PartyUpdated` |
| PFC | ProductVersion resolution, legacy-source conversion rules, code cross-references, product import | `pfc.ProductVersion.resolve` (`REQ-PFC-001`), `REQ-PFC-008/240/241/242`, charge types `REQ-PFC-004`, regulatory mapping `REQ-PFC-005` |
| RAT | `rateBatch` in RENEWAL mode with the legacy prior premium, worksheet explain, legacy rating import, BM mapping table, reconciliation | `REQ-RAT-002/003/005/044/269…274` |
| UW | Renewal-checkpoint evaluation; term-level and register imports | `REQ-UW-001`, `REQ-UW-295` |
| POL | Policy and term imports, renewal from an imported term, renewal dry-run, job withdrawal, snapshot at loss date, backdating | `REQ-POL-001/002/006/007/008/009/010/011/013/340…343/358`; events `RenewalOffered/Bound`, `PolicyNonRenewed/Lapsed`, `JobWithdrawn/NotTaken`, `PolicyBound/Cancelled/Voided` |
| BIL | Account, open items, mandates, tokens, opening balances, agency items, commission history; suspense; disbursement; reconciliation | `REQ-BIL-001/004/006/008/009/012/290/339/365`; events `CashAllocated`, `PaymentReceived`, `Disbursement{Cleared,Rejected,Stopped}` |
| CLM | Claim and financial-history import, reconciliation, certificates, Friendly Settlement statement | `REQ-CLM-007/008/009/010/160/236/266`; events `ClaimClosed`, `ClaimReopened` |
| RI | Programme, contract, cession and recovery history, open items, reconciliation | `REQ-RI-006/007/247/248/249/261` |
| FIN | Opening balances, IFRS 17 groups, earning baseline, reconciliations, MIGRATION_ADJUSTMENT, GL extract, parallel close | `REQ-FIN-005/008/010/043/215/281…285/294/307`; events `ReconciliationBreakRaised/Resolved` |
| DOC | Legacy document import (reprint-only), document requests, evidence archive | `REQ-DOC-001/002/004/005/311/312/321` |
| CMP | Complaint, DSAR, clock and fiscal-document imports; bureau hand-over; fiscal correlation; DSAR fan-out (inbound to MIG) | `REQ-CMP-003/005/007/008/009/032/038/054/086/087/154…158/249/254`; events `BureauEventSubmitted`, `BureauLagExceeded`, `FiscalDocRegistered/Rejected`, `ClockBreached` |
| CHN | Channel imports (identity links as invitations, consent receipts, webhooks); broker book view | `REQ-CHN-005/039/315` |
| WRK | Activities for remediation, review and breaks; note imports; command palette | `REQ-WRK-001/003/006/115/198/252/386/387/399/400`; event `ActivityCompleted` |
| PLT | Approvals, audit, authority, numbering, legal hold, time service, change records, pseudonymisation, workflow engine, adapter host, retention engine, AI control plane, user and role imports | `REQ-PLT-001…007/010…014/148/215/237…242/263/266/267/270/320/330…333/345…349`; events `LegalHoldApplied/Released`, `AiToggleChanged`, `AiKillSwitchActivated`, `ConfigChanged` |
| MKT | Configuration resolve, capability switch, migration-baseline configuration state (`REQ-MKT-010`), `convertFixed`, language switch | `REQ-MKT-001/002/004/005/010/062/199` |
| DAT | Reconciliation views, legacy zone, lineage, model registry, coexistence marts | `REQ-DAT-001/005/007/045/253/294…298`; event `RegulatoryMartPublished` |

---

## 5. Events

### 5.1 Produced (§8.1)

The PRD names the topic `mig.events.v1`. MIG's own events are **not** flagged `origin=MIGRATION`. Payloads carry ids and counts only, never P2 or P3 data.

| Event | Trigger | Partition key | Payload | Consumers |
|---|---|---|---|---|
| `MigrationBatchLoaded` (contract §3.4.2) | Batch enters Loaded or PartiallyLoaded (`REQ-MIG-077`) | batch_id | batch, wave, cohort, module, target op, object type, counts by outcome, mapping version, content hash | PTY (duplicate detection, bulk screening), FIN (`REQ-FIN-281`), CMP (bureau and clock checks), WRK, DAT (`REQ-DAT-253`) |
| `MigrationBatchReconciled` (contract §3.4.2) | Reconciliation signed (`REQ-MIG-005`) | batch_id | batch, wave, match status, break counts per measure, signatories, report ref | FIN, WRK (`REQ-WRK-198` break activities), DAT |
| `MigrationWaveStatusChanged` (R-70) | Wave state transition | wave_id | wave code, scenario, old and new state, scope summary, effective time | BIL, CMP (`REQ-CMP-254`), DOC (`REQ-DOC-321`), WRK (`REQ-WRK-399`), DAT |
| `CoexistenceMasterChanged` (R-70) | Route state transition | route_id (stable for the object's life) | route id, object type, core id, legacy key, old and new master, term split, effective from, reason | BIL, CHN (`REQ-CHN-039`), CLM (`REQ-CLM-261`), CMP (`REQ-CMP-254`), DOC (`CoexistenceRouteView`, `REQ-DOC-321`), WRK (`REQ-WRK-400`) |

**Ruling R-96.** Consumers may keep event-fed routing caches. The synchronous T1 call `mig.Routing.resolve` stays authoritative and is the fallback when a cache entry is missing or stale. DAT takes only wave events and calls `resolve` with `asOf` for history.

### 5.2 Consumed (§8.2)

All consumers are idempotent on event_id.

| Event(s) | Producer | Reaction |
|---|---|---|
| `PartiesMerged`, `PartyUnmerged` | PTY | Re-point the cross-reference; update match groups |
| `PartyUpdated` | PTY | Sync to legacy (L-04) for parties with legacy-mastered objects |
| `RenewalOffered`, `RenewalBound`, `PolicyNonRenewed`, `PolicyLapsed`, `JobWithdrawn`, `JobNotTaken` | POL | Case outcome (`REQ-MIG-108`); routing transitions |
| `PolicyBound`, `PolicyCancelled`, `PolicyVoided` with `origin=MIGRATION` | POL | Load and reversal confirmation |
| `CashAllocated`, `PaymentReceived` (suspense reason "legacy") | BIL | Misdirected-payment detection |
| `DisbursementCleared`, `DisbursementRejected`, `DisbursementStopped` (D4) | BIL | Transfer status. Rejected or Stopped returns the item to the queue |
| `BureauEventSubmitted`, `BureauLagExceeded` | CMP | Hand-over confirmation; hyper-care metrics |
| `FiscalDocRegistered`, `FiscalDocRejected` | CMP | Fiscal hand-over confirmation; rejection rate |
| `ClockBreached` | CMP | Hyper-care metric |
| `ReconciliationBreakRaised`, `ReconciliationBreakResolved` | FIN | Link FIN breaks to migration breaks |
| `RegulatoryMartPublished` | DAT | Status of the coexistence reporting check |
| `ActivityCompleted` | WRK | Remediation and review progress |
| `LegalHoldApplied`, `LegalHoldReleased` | PLT | Archive holds |
| `AiToggleChanged`, `AiKillSwitchActivated` | PLT | Disable AI surfaces within 60 s |
| `ConfigChanged` | PLT | Refresh parameters |
| `ClaimClosed`, `ClaimReopened` | CLM | Run-off tracking; decommissioning dependency counts; hyper-care |

**Explicitly not consumed:** `ClockStarted`, `ClockWarned`, `ClockMet`, `ClockElapsed`. CMP may drop MIG as a consumer of these.

---

## 6. APIs

### 6.1 Exposed (§9.1)

The PRD places these in-process and as REST under `/api/mig/v1/`.

| Operation | Purpose | Notable errors | Idempotency / dry-run | Req |
|---|---|---|---|---|
| `mig.Xref.register` | Called **by module import APIs** to record legacy → core links | `MIG-ERR-XREF-CONFLICT` | natural key | 002 |
| `mig.Xref.resolve` / `list` | Resolve legacy ↔ core ids, with history; page through links | `MIG-ERR-NOT-FOUND` | query | 002 |
| `mig.Routing.resolve(objectType, key, asOf)` | Master system, term split, deep link. **T1, p95 ≤ 50 ms at 500 rps** | `MIG-ERR-AMBIGUOUS-KEY` | query | 004, 149 |
| `mig.Routing.override` | Authorised override | `MIG-ERR-APPROVAL-REQUIRED` | required; dry-run shows impacted modules | 148 |
| `mig.Enquiry.get` | Unified cross-system summary | `MIG-ERR-LEGACY-UNAVAILABLE` (degraded answer) | query | 152 |
| `mig.Wave.*`, `mig.Cohort.*` | Plan and run | `MIG-ERR-ENTRY-CRITERIA` | required; population preview | 031–033 |
| `mig.Batch.start/pause/resume/reverse/redrive/get/outcomes` | Pipeline control | `MIG-ERR-DRYRUN-REQUIRED`, `MIG-ERR-DEPENDENCY`, `MIG-ERR-REVERSAL-UNREGISTERED` | required; `reverse` supports dry-run | 066–078, 211 |
| `mig.DqIssue.*`, `mig.Waiver.*` | DQ workflow | `MIG-ERR-WAIVER-SCOPE` | required | 047–050 |
| `mig.MappingSpec.*` | Author, test, approve, activate | `MIG-ERR-TESTS-FAILED` | required; tests act as dry-run | 054–062 |
| `mig.RenewalConversion.*` | List, decide, reconvert | `MIG-ERR-AUTHORITY`, `MIG-ERR-AFTER-BIND` | required; decision dry-run returns a POL preview | 091–108 |
| `mig.Match.*` | Match decisions | — | required; dry-run | 081–085 |
| `mig.Reconciliation.*` | Run, list, sign, approve | `MIG-ERR-BREAKS-OPEN` | required | 171–177 |
| `mig.MisdirectedPayment.*` | Match and transfer | `MIG-ERR-APPROVAL-REQUIRED` | required; dry-run | 157–158 |
| `mig.Cutover.*` | Runbook execution | `MIG-ERR-GATE-CLOSED` | required | 186–190 |
| `mig.ReversalRegister.*` | Register and test owner reversal ops | — | required; conformance run | 211 |
| `mig.ScenarioDecision.*` | Planning basis and confirmation | `MIG-ERR-APPROVAL-REQUIRED` | required | 212 |
| `mig.Archive.search/get` | Archive access (masked) | `MIG-ERR-PERMISSION` | query | 196 |
| `mig.Archive.subjectExport/rectificationNote/restrict/erase` | DSAR operations for CMP | `MIG-ERR-LEGAL-HOLD` (outcome restricted) | DSAR task id; `erase` supports dry-run | 006 |
| `mig.ImportContract.conformance` | Run the harness against a module operation | — | required | 001 |
| `mig.Changeover.*` | Currency changeover plans | — | required; dry-run | 126 |

All commands carry an `Idempotency-Key`. Errors are RFC 9457 with `MIG-ERR-*` codes. A replay with a different payload returns `409 MIG-ERR-IDEMPOTENCY-MISMATCH`. Commands that change routing, money hand-over or legal status also support dry-run (`REQ-MIG-206`).

### 6.2 Consumed

See §4 above. The **import contract `REQ-MIG-001`** requires every module holding migratable data to provide:
- (a) operations named `<mod>.Import.<object>` taking batches with legacy source keys
- (b) `dryRun` returning would-create, would-update, would-match or rejected-with-typed-errors, with no side effects
- (c) idempotency per legacy key and per `Idempotency-Key`; a replay with a different payload returns `<MOD>-ERR-IDEMPOTENCY-MISMATCH`
- (d) the same validation and materialisation as live entry
- (e) a per-record outcome with the created core ids
- (f) xref registration through `mig.Xref.register`
- (g) the same domain events as live business, flagged `origin=MIGRATION`
- (h) an optional `convertCurrency(planId)` for money-holding modules (R-08)
- (i) a reconciliation query returning counts and control totals per MIG batch id

**Conformance table (§5.0.1).** It covers PTY, PFC, RAT, UW, POL, BIL, CLM, RI, FIN, DOC, CMP, CHN, WRK, PLT, DAT and MKT. All import gaps are closed by R-72, R-73 and R-77.
- DAT is "not applicable by design": the legacy zone is not a module schema.
- CHN imports are Should in CHN and accepted for the MVP.
- Reversal operations are named by POL (`REQ-POL-358`), BIL (`REQ-BIL-365`), CLM (`REQ-CLM-266`), RI (`REQ-RI-261`), FIN (`REQ-FIN-307`) and PTY (`REQ-PTY-282`). They still have to be registered and conformance-tested by gate G1, before mock 2.
- UW, DOC, CMP, WRK, CHN and PLT have no reversal operation requested yet (§14.x.1).

### 6.3 Legacy-side and external interfaces (§9.3)

| ID | Interface | Direction | Notes |
|---|---|---|---|
| L-01 | Extract | Legacy → MIG | Read replica or encrypted file, through the PLT adapter host |
| L-02 | Delta feed | Legacy → MIG | Daily; change timestamps or full compare |
| L-03 | Stop renewal per policy | MIG → Legacy | Acknowledgement required; a failure blocks cohort entry |
| L-04 | Party changes to legacy | MIG → Legacy | Acknowledgement within 1 business day |
| L-05 | Legacy enquiry | MIG → Legacy | Read API; fallback to the legacy index |
| L-06 | Misdirected payments from legacy | Legacy → MIG | — |
| L-07 | Stop bureau reporting per vehicle | MIG → Legacy | — |
| L-08 | Freeze and read-only mode | MIG → Legacy | Procedure plus verification test |

All of L-03, L-04, L-06, L-07 and L-08 "require legacy change" (OI-MIG-13).

External interfaces:
- E-01 lakehouse legacy zone (DAT)
- E-02 Information Centre (via CMP; transport UNVERIFIED)
- E-03 myDATA (via CMP)
- E-04 banks and PSPs (via BIL)
- E-05 intermediaries
- E-06 reinsurers
- E-07 GL/ERP (via FIN)
- E-08 legacy document archive (bulk binaries with hashes)

---

## 7. SPIs / country-pack interfaces (§9.2, §10.4, `REQ-MIG-046`)

**Used:**
- `IdValidator`: AFM check digit and name-class match. Scheme `VEHICLE_PLATE` with `normalise` and `searchKey`; this is the same pure, pack-bound scheme POL, CMP and CHN use at runtime (XMR-F-301, XMR-D-204). VIN (ISO 3779).
- `NameTransliterator` (ELOT 743)
- `AddressFormatter`
- `NumberingScheme`
- `FiscalDocumentChannel` (via CMP)
- `BureauAdapter` (via CMP)
- `StatutoryClockSet` (via CMP)
- `PaymentReferenceGenerator` and `BankFileFormat` (via BIL)
- `MotorDataProvider` (enrichment)
- `TaxCalculator.treatment` (D2, by the originating module)

**Defined: `LegacyDataProfile`** (CCR-MIG-02, accepted as R-71). MKT defines it and packs implement it. It covers:
- plate canonicalisation (incl. `profilePlate`, which reuses `IdValidator`)
- legacy encodings
- transliteration consistency
- identifier plausibility and ownership heuristics

**Multi-market design (§10.4):**
- The Cyprus stub swaps in TIC format, Cyprus plates, four-digit postcodes and different encodings.
- Synthetic values: offer lead 30, monthly tax periods, synthetic retention.
- Cyprus has no fiscal channel and uses a stub bureau.

---

## 8. Screens (§6)

All are staff workbench screens with no business logic. Shared patterns (design guide IB-xx):
- IB-01 command palette
- IB-02 keyboard list navigation
- IB-03 sidebar work views
- IB-16 density
- IB-19 presence
- IB-21 semantic status
- IB-24 quiet chrome
- IB-29 dense log tables

Every screen supports the R-101 language switch "ΕΛ | EN" without reload or loss of input. Common states on every screen: loading, empty, error (RFC 9457 + correlation id), permission (P2/P3 masked), offline/degraded (`stale` with timestamp).

Permissions: `mig.view`, `mig.operate`, `mig.map.edit`, `mig.map.approve`, `mig.dq.remediate`, `mig.dq.waive`, `mig.review.price`, `mig.match.review`, `mig.reconcile.sign`, `mig.routing.override`, `mig.cutover.run`, `mig.archive.read`, `mig.archive.dsar`.

| ID | Name | Personas | One line | Patterns |
|---|---|---|---|---|
| SCR-MIG-01 | Migration control dashboard | Migration lead, analyst; compliance, ICT risk and auditor read-only | Waves, cohorts, batches, errors, reconciliation status, next gate, go/no-go; actions: create wave, start cohort (entry criteria), batch control, go/no-go, board pack | IB-11, 12, 17, 26, 28, 05, 06 |
| SCR-MIG-02 | DQ issue queue | Data steward, remediation owner, analyst, migration lead | Triage, remedy, waivers (justification ≥ 30 chars, consequence, expiry, checker ≠ maker) | IB-04, 05, 02, 29, 20, 09, 07 |
| SCR-MIG-03 | Mapping specification editor and version history | Analyst (maker), object business owner (checker), auditor | Rules per target attribute; typed expression; crosswalk; no-home treatment; tests; activate from a date | IB-05, 07, 20, 19, 32, 08 |
| SCR-MIG-04 | Renewal-based migration review | Conversion reviewer, UW manager, pricing actuary | Out-of-tolerance cases: legacy expiring and renewal price, new uncapped and capped, band, drivers, defaults applied; approve, adjust (authority check), refer, exclude | IB-04, 05, 13, 08, 09, 26 |
| SCR-MIG-05 | Party match and merge review | Match reviewer, data steward, CSR lead | Score, rule hits, existing core party, survivorship per attribute; confirm, reject, defer, split, un-merge | IB-04, 15, 08 |
| SCR-MIG-06 | Batch reconciliation with sign-off | Finance controller, tax specialist, RI manager, claims manager, compliance, migration lead, auditor | Legacy vs core totals per measure, breaks and dispositions, signatory plus distinct approver, wave pack | IB-13, 28, 32 |
| SCR-MIG-07 | Coexistence routing and cross-system enquiry | CSR, PSR, claims handler, billing ops, migration lead | Search by AFM, plate, policy, claim or payment ref; master state; legacy and core panels; deep link; misdirected-payment queue; override (maker-checker) | IB-01, 21, 05, 09, 03 |
| SCR-MIG-08 | Cut-over runbook tracker | Cut-over manager, task owners, migration lead, compliance, ICT risk, auditor | Tasks, dependencies (acyclic), critical path and slack, gates, rollback points, evidence | IB-12, 23, 31, 06, 29 |
| SCR-MIG-09 | Wave, cohort and decision-matrix planner | Migration lead, object owners, DPO | Matrix (object × line) with treatment, RC, legal basis; cohort rule editor with population preview; capacity warning | IB-14 |
| SCR-MIG-10 | Legacy archive enquiry and DSAR tasks | DPO, auditor, claims handler, compliance | Search the archive; masked record view; DSAR task queue with CMP countdown; outcomes | IB-26, 04, 29 |

**Consumed inventory screens (reference only, §6.11):**
- `SCR-PCACC1-06` Batch Process Info (PLT, `SCR-PLT-10`)
- `SCR-PCACC1-08` PC Sample Data (PLT, `REQ-PLT-333/243`). Used for **synthetic data for conformance-harness tests; never for rehearsals with legacy data.**
- `SCR-PCACC2-14` Testing System Clock (`REQ-PLT-332`)
- `SCR-PCADM1-05` Runtime Properties
- `SCR-PCADM1-13` Import/Export Status (UW)
- `SCR-PCADM2-08` Data Change. **Never used to load migration data** (`REQ-MIG-072`).

---

## 9. Regulatory, tax and statutory rules

### 9.1 Stated explicitly by the source

| Rule | Value as stated | IDs |
|---|---|---|
| DORA risk assessment | Required on each major change (Art. 8(3)), yearly on legacy ICT systems and before and after connecting them (Art. 8(7)). **Each wave that connects a legacy system or changes production infrastructure or processes needs a linked DORA ICT risk assessment and change record before go/no-go** | REQ-MIG-036, BR-MIG-037, NFR-MIG-012 |
| DORA change management | Art. 9(4)(e) and Delegated Reg. 2024/1774 Art. 17: independent approval and tested fall-back. Cut-over is a PLT change record | REQ-MIG-187/188/189 |
| DORA testing / exit | Arts. 24–25: mocks and rehearsals are recorded as tests. Art. 28(8): the decommissioning plan is the exit plan; the register of information is updated | REQ-MIG-181…185, 193, 201 |
| IFRS 17 | Paras. 22 and 24: migrated terms keep their original recognition date and group; **conversion is never a recognition date** | REQ-MIG-120, F-07 |
| GDPR | Art. 5(1)(b) and 6(4): purpose register with DPO decision; new uses (e.g. model training) need a compatibility assessment and are blocked otherwise. Art. 5(1)(c),(e), 17(3)(b),(e), 25, 32 also apply | REQ-MIG-037, 030, 196–204 |
| Consent proof | A legacy consent without proof is stored as `NO_PROOF` and treated as not given (GDPR Art. 7(1)) | REQ-MIG-087 |
| Information Centre | Law 5113/2024 Art. 15: insurers report plate, policy number, validity period, invalidity or end of cover, and Green Card number; the manner of submission is set by ministerial decision (§2). Hand-over is **per vehicle**: legacy reports up to the day before, the core from the start of the core term (renewal) or the conversion date (mid-term) | REQ-MIG-162/163, BR-MIG-022 |
| Continuity of compulsory cover | The core renewal term starts at the legacy expiry date and time, with no gap or overlap (example: legacy ends 2027-06-30 24:00, core starts 2027-07-01 00:00) | REQ-MIG-102, BR-MIG-011 |
| Claims-history statement | Directive 2009/103/EC as amended by 2021/2118; Impl. Reg. 2024/1855; Law 5113/2024 Art. 7. Closed claims within the certificate period convert in summary; **default 5 years** (`mig.claims.history_summary_years`, country layer) | REQ-MIG-133, BR-MIG-003 |
| MTPL claim clocks | Reasoned offer within **three months**; payment within **ten days from the offer** (BoG Act 87/2016 Art. 6, per XMR-F-230). `CLM_MTPL_PAYMENT_DUE` starts at proven delivery of the reasoned offer (D7), never at acceptance. With no legacy delivery evidence, the offer issue date is used and a DQ issue is raised. Example: offer delivered 2027-05-02 gives deadline 2027-05-12; claim received 2027-04-01 gives `CLM_MTPL_OFFER` deadline 2027-07-01 | REQ-MIG-130, BR-MIG-023 |
| Running clocks | Original start and deadline are kept and never recomputed at conversion | BR-MIG-023, REQ-MIG-117, 147 |
| Non-payment notice | Law 2496/1997 Art. 6 §2: one month from notification. Imported with the original start; a notice period is never restarted. Example: notified 2027-05-10 gives deadline 2027-06-10 | REQ-MIG-117 |
| Complaints clock | `CMP_COMPLAINT_REPLY` keeps the original start. Example: complaint received 30 days before conversion leaves "20 days remaining" (implies a 50-day period; the source gives no explicit value) | REQ-MIG-147 |
| Auxiliary Fund contribution | Law 5113/2024 Art. 14: **two-month periods, remittance 15 days after period end** | REQ-MIG-122/123, BR-MIG-020 |
| IPT | Law 5177/2025 Art. 43 §6: **quarterly return**. The transition quarter is a combined return with a source column | REQ-MIG-122/123 |
| Tax and levy boundary | Each premium is declared by the system that **issued its fiscal document** (by issue date). The boundary date is the start of an Auxiliary Fund period and preferably an IPT quarter. Core remittances are executed by BIL as source `TAX_REMITTANCE` (D1). Tax treatment comes from a single `TaxCalculator.treatment` call (D2) | REQ-MIG-122, BR-MIG-020 |
| myDATA credits | The core issues credits for legacy-issued documents through CMP (sole issuer of series and numbers, D3), correlated to the **legacy MARK** (types 5.1 / 11.4 per Greece pack). The legacy fiscal document (MARK, UID, series, amounts) must be imported first | REQ-MIG-124/164/165, BR-MIG-021 |
| Converted motor fiscal rules | TRANSACTION trigger; premium receipts registered before delivery under one certification rule; the cover note is non-fiscal | REQ-MIG-165 |
| Distance marketing | Directive 2023/2673; Law 5317/2026 Arts. 69–72. Conversion notices are informational and never create a new withdrawal right | REQ-MIG-168/169 |
| AML / sanctions | Bulk screening per batch; first contract activation is blocked for an unscreened party | REQ-MIG-088 |
| EU residency | Staging, index, archive and all processing stay in the EU stamp | REQ-MIG-202, NFR-MIG-021 |
| AI Act | Deployer duties: logging (`AiInteractionRecord`), human oversight, kill switch within 60 s | REQ-MIG-209/210 |
| Written premium (D5) | Premium, surcharge and discount charge categories, net of reversals, excluding tax, levy and fee, on a booking-date basis | REQ-MIG-172 |

### 9.2 Referenced but not specified (gaps / UNVERIFIED)

- **Renewal advance notice period.** No statutory period was found; market practice is 30–50 days (S-28, secondary). The placeholder is **45 days**. It needs Settled legal status before production activation (OI-MIG-02, F-09).
- **Information Centre transport, deadline and operator.** These await a ministerial decision (OI-MIG-03 / OI-CMP-01). The interim route is CMP's manual file with daily lag monitoring (XMR-D-265).
- **PD 237/1986 vs Law 5113/2024.** The codification relationship is UNVERIFIED (C-03, OI-MIG-03).
- **myDATA issuer-software change notification duty.** Not found (OI-MIG-04, REQ-MIG-166, gate G0). The treatment of two transmitting systems for one VAT number is also open.
- **Bank of Greece prior notification** of a core migration. No act found (OBL-BOG, OI-MIG-05). Law 5193/2025 is peer-verified, but no MIG-specific article was identified.
- **Greek retention durations** for the archive and evidence. Placeholder 10 years after decommissioning (OI-MIG-06, NFR-MIG-023; gate G1 via XMR-D-259).
- **myDATA 2.0.2** activated 10 Sep 2026. This is a search result only; the fiscal reconciliation must tolerate both API versions (F-10).
- **Completion of the Fairfax/Eurolife transaction** is UNVERIFIED (F-01, OI-MIG-01).
- **SEPA rulebook** is cited only "(peer)" (REQ-MIG-116).

The source counts **7 UNVERIFIED items** in §16.6.

### 9.3 Deferred to configuration or country pack

- **Greece pack:** offer lead time; claims-history period; correlation rule; fiscal series; RC durations; plate, AFM and postcode rules; encodings.
- **Legal-entity configuration (§10.2):**
  - tolerance bands (default ±10% auto; +25% review)
  - review deadline offset (5 days before offer)
  - match thresholds (upper 92, review 75)
  - short-tail definition (MD cost types, expected closure ≤ 6 months)
  - tax and levy boundary date
  - misdirected-payment threshold (€0, so every transfer is four-eyes)
  - sampling size 400
  - staging holding 90 days
  - hyper-care exit 10 days
  - signatories table
  - coexistence throughput floor
  - capacity confirmation threshold 10%
  - extract lead 90 days; review buffer 10 days

---

## 10. Greek-market specifics

| Topic | What the source says |
|---|---|
| **AFM** | Check digit via `IdValidator`. A legacy-known AFM that fails the check loads as `VerificationFailed` with a DQ issue; an unknown invalid AFM is rejected (`REQ-MIG-090`). Ownership consistency: one AFM on different people is reported, not auto-corrected (`REQ-MIG-053`). Deterministic match on verified AFM plus party type (`REQ-MIG-081`). Rule DQ-PTY-003 example: "AFM present for Greek individual policyholders", Critical, 100% |
| **GEMI** | Deterministic match key (`REQ-MIG-081`) |
| **Plates** | Greek registration series, including Greek-letter plates written with Latin look-alikes (e.g. "ΙΚΒ1234" vs "IKB1234"), normalise to one canonical plate (`REQ-MIG-046`) |
| **Encoding** | ELOT 928 = ISO/IEC 8859-7 vs Windows-1253, which differ at specific code points (e.g. "Ά"). Detection is **per field, never per file**. Text converts to UTF-8 NFC with a round-trip check; an undefined byte becomes a DQ issue and is never replaced (`REQ-MIG-052`, F-06) |
| **Transliteration** | ELOT 743 via `NameTransliterator`. Probabilistic matching across Greek and Latin names (e.g. "Παπαδόπουλος Γεώργιος" vs "PAPADOPOULOS GEORGIOS") (`REQ-MIG-082`) |
| **Postcode** | Five digits, via `AddressFormatter` |
| **myDATA** | MARK correlation for credits; types 5.1 and 11.4; legacy series registered so the core series cannot collide (e.g. legacy series "A" for type 11.2 at branch 0) |
| **Information Centre** | Per-vehicle hand-over; transport is unpublished |
| **Auxiliary Fund / IPT** | Two-month periods remitted at +15 days; quarterly IPT |
| **Friendly Settlement** | Open receivables and payables convert with clearing references so the first monthly statement after cut-over reconciles (`REQ-MIG-131`, `REQ-CLM-160`). Green Card and Auxiliary Fund cases convert too (Should, `REQ-MIG-132`) |
| **Bonus-malus** | Converted only through an approved RAT mapping version (`REQ-RAT-272`); an unmapped class blocks the case (BR-MIG-018). Example: legacy B4 maps to class 4 |
| **Bank of Greece** | ROLE-44 receives notifications and evidence packs; notification duty unverified |
| **gov.gr** | Not mentioned |
| **Language** | All labels, messages, errors and code lists in Greek and English; R-101 switch on all 10 screens; field tables give EN/GR labels; glossary terms proposed (CCR-MIG-08), e.g. συνύπαρξη συστημάτων, κύριο σύστημα, μετάπτωση κατά την ανανέωση, κοόρτη μετάπτωσης. Customer documents follow the customer's language preference, not the UI switch |
| **EUR** | The Greece pack needs no currency changeover. Money compares at Decimal(19,4) to **€0.00 unexplained** (NFR-MIG-010, BR-MIG-031) |
| **Market context** | F-01: Fairfax keeps 80% of Eurolife P&C, so a Greek P&C book is a credible scenario B candidate. MIG names no legacy system |

---

## 11. Controls

**Authority types registered with PLT (§12.2):**

| Authority type | Dimensions | Holder |
|---|---|---|
| `MIG_PRICE_DEVIATION` (uses RAT deviation authority `REQ-RAT-005`) | product, % deviation, premium | Conversion reviewers, UW managers |
| `MIG_WAIVER` | severity, count | Data steward (Major, Minor); migration lead (Critical) |
| `MIG_ROUTING_OVERRIDE` | object type, duration | Migration lead |
| `MIG_PAYMENT_TRANSFER` | amount | Billing operations lead |
| `MIG_ROLLBACK` | wave type | Cut-over manager with migration lead |

**Maker-checker list (§12.3; CCR-MIG-07 / R-75):**
- mapping-spec and crosswalk versions
- DQ waivers
- decision-matrix rows
- go/no-go (multi-approver: business owner, finance controller, compliance officer, ICT risk manager, release manager; proposed by the migration lead; `REQ-MIG-033`)
- routing overrides
- misdirected-payment transfers
- reconciliation sign-off
- runbook task skips
- rollback execution
- production-stamp rehearsal and purge
- archive erasure outcomes that delete data
- PTY customer merges where in-force policies exist (`REQ-MIG-084`)

**Segregation of duties (§12.4):**
- SOD-MIG-01: the mapping author is not the approver.
- SOD-MIG-02: the approver of a mapping version may not alone start the production load that uses it.
- SOD-MIG-03: the preparer, the signatory and the second approver are three different people.
- SOD-MIG-04: the waiver requester is not the approver.
- SOD-MIG-05: a conversion reviewer is barred from cases where they are the intermediary or a relative (PTY conflict flag).
- SOD-MIG-06: the user who overrides a route is not the approver.
- SOD-MIG-07: users with production business-transaction roles in the same entity get no raw staging or archive access, except DPO-approved investigation roles.

**Named signatories (`REQ-MIG-176`, BR-MIG-032):**

| Report type | Signatory |
|---|---|
| Counts | Migration lead |
| Billing balances, trial balance, reserves | Finance controller |
| Tax and levy split, fiscal | Tax specialist |
| Ceded balances | Reinsurance manager |
| Bureau hand-over | Compliance officer |
| Claims | Claims manager |

Every report also needs a distinct second approver.

**Audit (§12.1, `REQ-MIG-205`).** Every MIG action is written through `REQ-PLT-002` with the batch or wave id and `origin=MIGRATION` where data is loaded. This covers mapping versions, DQ rules, waivers, waves, entry overrides, go/no-go, batch control, real loads (content hash plus dry-run ref), match decisions, review decisions with authority result, routing overrides, payment transfers, signatures, gates, skips, rollbacks, archive access and exports, DSAR operations, and production-stamp rehearsal approval and purge.

**GDPR handling:**
- purpose register (`REQ-MIG-037`)
- minimal legacy index (`REQ-MIG-151`)
- party sync limited to the attributes legacy needs (`REQ-MIG-156`, BR-MIG-028)
- encrypted extracts with no raw downloads; only masked samples, with permission (`REQ-MIG-202`)
- ABAC on staging and archive by entity, object type, P2/P3 and purpose (`REQ-MIG-203`)
- **pseudonymised non-production datasets with a residual-identifier check** (`REQ-MIG-079/204`)
- staging purged 90 days after sign-off (`REQ-MIG-199`)
- archive DSAR: erase, or restrict with basis and end date; cryptographic erasure of per-subject keys is allowed; rectification notes never alter sealed copies (`REQ-MIG-198`)
- legal holds block purge and erasure (`REQ-MIG-200`)
- archive DSAR operations complete within 2 business days of the CMP task (NFR-MIG-016)

---

## 12. AI features (§11)

All features are **off by default**. They run through the PLT model gateway (EU, zero retention). Each needs an approved CMP register entry (`REQ-CMP-007`) and a DAT model registry entry (`REQ-DAT-005`). Each has an `AiInteractionRecord` and stops within 60 s of the kill switch. **Every MIG requirement is met with AI fully off** (`REQ-MIG-209`, §11 statement). **None is needed for the MVP.**

| ID | Feature | Classification | Key constraint |
|---|---|---|---|
| AI-MIG-01 | Mapping suggestion assistant | Minimal | Tokenised samples (≤ 20 per field); confidence < 0.6 or timeout 5 s shows nothing; still maker-checker; monitoring MP-C |
| AI-MIG-02 | Party match explainer and scorer | **Uncertain, so high-risk controls** | Can move a pair *into review*, never into auto-match; trained on pseudonymised mock 1–2 pairs; bias parity 0.8–1.25 across transliteration patterns; drift or bias auto-disables; MP-A |
| AI-MIG-03 | DQ anomaly detector and remediation suggester | Minimal | Profile stats and masked samples only; MP-E |
| AI-MIG-04 | Conversion review brief | **High-risk controls** (pricing-adjacent) | Excludes names, nationality, address below rating zone and protected proxies; AI never adjusts price; customer privacy-notice text given GR and EN; MP-A |
| AI-MIG-05 | Legacy document classifier | Minimal, with high-risk data controls | Medical classes excluded; bulk Accept only when confidence ≥ 0.95 and a 200-document sample has ≤ 1% errors; accuracy ≥ 98%; MP-D |
| AI-MIG-06 | Reconciliation break explainer | Minimal | No personal data; MP-C |
| AI-MIG-07 | Cut-over runbook agent | Minimal | Read-only checks and drafting; agent identity bound to the cut-over manager's permissions; every gate is a human action; MP-C |
| AI-MIG-08 | Cross-system enquiry summary | Minimal | Uses only the enquiry result already permitted; P3 excluded; MP-C |

---

## 13. Open issues / assumptions / CCRs

**Open issues (§16.1):**

| ID | Topic | Status |
|---|---|---|
| OI-MIG-01 | Scenario and book | Planning basis closed (D6). Board confirmation before W5 and capacity confirmation (`REQ-MIG-215`) still **open** |
| OI-MIG-02 | Renewal-notice value | Design closed (D7). Greek value **open** (placeholder 45) |
| OI-MIG-03 | Information Centre transport; PD 237/1986 | **Open**. Interim manual file (XMR-D-265, gate G2) |
| OI-MIG-04 | myDATA issuer-change notification | **Open** (gate G0). Fiscal design closed by D3 |
| OI-MIG-05 | BoG notification | **Open** |
| OI-MIG-06 | Greek retention durations | **Open** (gate G1 before system test) |
| OI-MIG-07 | Production-stamp rehearsal with real data | **Open** (DPIA addendum) |
| OI-MIG-08 | Validation parity in RAT, UW, DOC | Closed (R-77) |
| OI-MIG-09 | `convertCurrency` in owners | Closed (R-77) |
| OI-MIG-10 | PLT import API | Closed (R-73) |
| OI-MIG-11 | MKT baseline anchor | Closed (R-72) |
| OI-MIG-12 | Reversal operations | Named by 6 owners. **Open** for UW, DOC, CMP, WRK, CHN, PLT and for conformance testing (gate G1) |
| OI-MIG-13 | Feasibility of legacy interfaces L-03, 04, 06, 07, 08 | **Open** |
| OI-MIG-14 | CLM does not support "claims in legacy, policy in core" | **Open**. MIG default: short-tail claims on converted policies stay in legacy run-off with enquiry only |
| OI-MIG-15 | Card token migration by the acquirer | **Open** |

**Assumptions (§16.2):**
- A-01: legacy can provide daily replicas or extracts and accept stop instructions.
- A-02: annual renewals, so 12–13 months of coexistence; non-annual terms may extend it by one term.
- A-03: import APIs scale to NFR-MIG-002.
- A-04: legacy fiscal documents for in-force terms have MARKs.
- A-05: the mandate creditor identifier is unchanged.
- A-06: the contract §3.9.7 volumes are representative.

**CCRs (§16.4).** All eight are accepted:
- CCR-MIG-01: two new events (R-70)
- CCR-MIG-02: `LegacyDataProfile` SPI (R-71)
- CCR-MIG-03: CMP anchor `REQ-CMP-009` (R-72)
- CCR-MIG-04: T1 split for routing and xref reads (R-74)
- CCR-MIG-05: PLT imports (R-73)
- CCR-MIG-06: `REQ-MKT-010` (R-72)
- CCR-MIG-07: maker-checker extension (R-75)
- CCR-MIG-08: cross-module entities and glossary (R-76)

No new CCRs were raised in v1.2.

**Local decisions (§16.5):**
- DEC-MIG-01 is decided for planning (D6).
- DEC-MIG-02…10 are "Recommended" and proposed for adoption at gate G1 (XMR-D-260): strategy per line; numbering (preserve legacy numbers where `NumberingScheme` allows; first core term number = legacy term count + 1); tolerance and capping; claims split; tax/levy boundary; legacy interface investment; rehearsal approach; archive technology and retention; signatories.

**Risks (§16.3).** RK-MIG-01…10. The top two are both rated High impact: DQ worse than profiled (High likelihood), and first-renewal price shock (Medium likelihood).

---

## 14. Conflicts and ambiguities

### (a) With the binding stack (ADR)

1. **"Synthetic data only outside production" (ADR §2 rule 12) vs pseudonymised real data in non-production.** This is the most significant conflict. The PRD repeatedly allows **pseudonymised full-volume copies of the real legacy book** outside production:
   - C-08
   - `REQ-MIG-079` "run pseudonymised full-volume pipelines in non-production"
   - `REQ-MIG-181` "mock 2 full volume pseudonymised"
   - `REQ-MIG-182` dress rehearsal "on production-like volumes in pre-production"
   - `REQ-MIG-204`, BR-MIG-036 "Pseudonymised data only outside production"
   - NFR-MIG-022
   - AI-MIG-02, 04 and 05, which train on "pseudonymised legacy party pairs / conversion cases / legacy documents from mock 1–3"

   It relies on `REQ-PLT-263`, which "allows only pseudonymised or synthetic data outside production". The ADR permits **synthetic only**. Under a strict ADR reading:
   - mocks 2 and 3 and the dress rehearsal would have to run inside the production stamp, or on synthetic look-alikes
   - the AI training data sources are not allowed
   - `REQ-MIG-185` (a real-data rehearsal in the production stamp) becomes the *only* way to rehearse with real data

   **Needs a decision.** Either the ADR is relaxed to allow pseudonymised data with residual checks, or MIG's rehearsal model moves into a production-stamp rehearsal environment.
2. **Workflow engine.**
   - `REQ-MIG-067`: "run pipelines as **workflow-engine workflows**"
   - `REQ-MIG-186`: runbook "timers driven by the workflow engine"
   - §2 SYS-02: "Batch job or workflow-engine workflow"
   - §9.2: PLT "workflow engine" (`REQ-PLT-007`)

   The ADR uses **Hangfire** and explicitly forbids workflow servers. This must be read as checkpointed Hangfire jobs with state in MIG tables.
3. **Lakehouse.** §1.4, §2, `REQ-MIG-135`, E-01 and §13.2 refer to "the lakehouse legacy zone" (DAT `REQ-DAT-294`). The ADR says **no data lakehouse**; marts are PostgreSQL schemas. Where the DAT legacy zone physically lives needs mapping (presumably a PostgreSQL schema or Blob Storage datasets).
4. **Broker vocabulary.** §8.1 uses "topic `mig.events.v1`" and a "Partition key" per event. The ADR uses a transactional outbox to in-process handlers with no broker. Partition keys should be read as per-key ordering keys in the outbox.
5. **"PLT adapter host"** (L-01, §9.2) and **"PLT model gateway"** (§11) are not stack components named in the ADR. **SFTP** file delivery (`REQ-MIG-066` example) is not in INFRASTRUCTURE summary terms either. Infrastructure for legacy connectivity, including any private network link to the legacy data centre, is unspecified.
6. **Rule-expression language "compatible with CEL" (D10, `REQ-MIG-054`).** This implies a CEL evaluator dependency in .NET, which falls under the ADR's minimal-dependency rule. The library choice is not decided here.
7. **Archive format.** `REQ-MIG-195` calls for "columnar or delimited open format". Columnar (Parquet) would add a dependency; storage is presumably Blob Storage. **Cryptographic erasure of per-subject keys** (`REQ-MIG-198`) implies per-subject key management in Key Vault or an envelope scheme. Both are undecided.
8. **Availability tier T1** for `mig.Routing.resolve` and `mig.Xref.resolve` during coexistence (R-74, NFR-MIG-005: 99.9%, p95 50 ms, 500 rps). This is fine in a monolith, but MIG becomes a T1 dependency of CHN, BIL and CLM.
9. Microservices, Kafka, Camunda, Kubernetes, MediatR and similar are **not mentioned**. Vendor names (BriteCore, Guidewire) appear only as research sources S-22 and S-24.

### (b) With the system contract or other PRDs

- `PolicyTermStatusView` (§7.2) is said to be fed by all `PolicyBound`, `PolicyCancelled`, `PolicyLapsed` and `PolicyNonRenewed` events. §8.2 lists `PolicyBound` and `PolicyCancelled` only "with `origin=MIGRATION`", so the consumed-event list is narrower than the view needs.
- **OI-MIG-14.** Renewal-based migration creates "short-tail claim in legacy on a policy now mastered in core", which CLM (PRD-07 §14.y) does not support.
- CHN imports are only Should in CHN (§5.0.1), yet `REQ-MIG-144` depends on them (MIG lists it as Should too, so this is consistent).
- Reversal operations for UW, DOC, CMP, WRK, CHN and PLT are "not yet requested in PRD-18" (§14.x.1). This is a cross-PRD gap.
- `REQ-MIG-001` (a) says operations are named `<mod>.Import.<object>`, but several modules deviate: `pfc.ProductImport.import`, `rat.LegacyRating.import`, `doc.Document.import`, `cmp.Bureau.handover`, `fin.Import.reverseBatch`. This is naming drift, not a functional gap.
- `RC-MIG-EVIDENCE` "10 years after decommissioning" is a placeholder. The programme retention schedule (XMR-D-259) is pending.

### (c) Internal contradictions

- **RenewalConversionCase terminal states.** `REQ-MIG-003` acceptance says each term ends in "Converted, Excluded, Withdrawn, Failed" and "transformed = converted + excluded + withdrawn + failed". The §7.3.5 state model makes **Failed non-terminal** (Failed → Transformed when re-driven) and adds the terminal **NotConverted** (NotTaken, Lapsed, NonRenewed), which `REQ-MIG-003` omits. The count equation cannot balance once NotConverted cases exist.
- **Tolerance band defaults.** §10.2 gives "±10% auto; +25% review". `REQ-MIG-097` and DEC-MIG-04 also include a "+10% to +25% proceed with capping and notice" band and "any decrease beyond −20%: underwriter review". The default table omits the −20% rule.
- **`cap.mig.scenario` default is A** (§10.2), while the planning basis is B (D6). This is consistent only if non-production environments explicitly set B, because BR-MIG-040 forbids B in production until confirmation. Worth stating explicitly in seed configuration.
- **`LegacyDataProfile`** is called "proposed (CCR-MIG-02)" in §9.2 but "accepted (R-71)" in §16.4 and `REQ-MIG-046`. The wording is stale.
- **Conformance timing.** `REQ-MIG-001` says "before the first mock load", and the §14.x test table says "before mock 1". But reversal-operation conformance is from mock 2 (G1). These are consistent but split.
- **Dress rehearsal.** `REQ-MIG-182` says "production-like volumes in pre-production ... real signatories" but does not restate pseudonymisation. This is implied by `REQ-MIG-079/204`.
- **NFR volumes.** NFR-MIG-001 (60,000 open claims, 25 million documents, 30 million notes) is given "as stated" against the contract §3.9.7 figures (150,000 claims a year, 10 million documents a year). The derivation is not shown, and all values are subject to `REQ-MIG-215` re-baselining.

### (d) Cannot be built or finished without a decision

- **The scenario confirmation (A vs B) before W5** (`REQ-MIG-212`). It decides whether 159 of the 172 scenario-B Musts stay Must in P1 (only 13 are Must in scenario A). In scenario A, 98 requirements become Won't.
- **The actual legacy system and book**: source inventory, dictionary, crosswalks, mapping specs, golden records, decision-matrix approvals, and capacity confirmation (`REQ-MIG-215`).
- **Legacy-side interface feasibility** (OI-MIG-13, DEC-MIG-07). L-03 is a hard gate for offers (BR-MIG-016).
- **The non-production data policy** vs ADR rule 12 (see 14(a)1), plus OI-MIG-07 and DEC-MIG-08.
- **The Greek renewal offer lead value** (Settled status needed for production cohorts, `REQ-MIG-214`).
- **The tax and levy boundary date and the finance month-end** (DEC-MIG-06).
- **Numbering:** preserve legacy numbers vs alias (DEC-MIG-03).
- **Claims split thresholds** (DEC-MIG-05) and OI-MIG-14.
- **Archive storage technology and format, and the retention schedule** (DEC-MIG-09, OI-MIG-06).
- **Choice of CEL evaluator library** (D10).

---

## 15. Build notes

### 15.1 What can be built without a real legacy source

Extra focus. Under ADR rule 12 ("synthetic data only outside production"), all build and test work uses a **synthetic legacy simulator**: a fake legacy database or file generator plus fake L-01…L-08 endpoints. The PRD itself anticipates synthetic data for the conformance harness (`SCR-PCACC1-08` PC Sample Data, §6.11) and for property-based invariants on "generated" data (§14.x).

| Capability | Buildable with synthetic data only? | Notes |
|---|---|---|
| Import contract and conformance harness (`REQ-MIG-001`, 069–072, 211) | **Yes, fully.** It is also needed in scenario A and for environment seeding (§14.y) | Harness checks: dry-run with no side effects, replay idempotency, mismatch error, same validation as live (shared vectors), `origin=MIGRATION` on events, xref registration, reconciliation query. NetArchTest enforces "no reference to a module persistence package" (`REQ-MIG-072`) |
| Cross-reference `LegacyXref` (002, 085, 089) | **Yes** | Re-pointing on `PartiesMerged`/`PartyUnmerged`; xref bijective except merges (invariant 8) |
| Routing table, `mig.Routing.resolve`, routing caches, events (004, 148–150, 153–154) | **Yes** | Pure temporal logic: half-open periods with exclusion constraints, term split, BR-MIG-012 defaults. Invariant 3: exactly one master per policy and date. Scenario A and new business always resolve to CoreMaster, so a minimal resolve can ship early to unblock CHN, BIL and CLM |
| Legacy index, cross-system enquiry, degraded mode (151, 152) | Yes, against a simulator | Real L-05 is legacy-dependent |
| Party sync, misdirected payments, bank-file split (155–159) | Logic yes; end-to-end needs legacy L-04 and L-06 | BIL executes money (D1); MIG only requests |
| Mapping-spec engine: versioned specs, CEL-style expressions, crosswalks, unit tests, golden records, lineage (054–065) | **Engine yes** | Real specs and crosswalks require the real dictionary |
| DQ rule library, profiling, issues, waivers, encoding conversion, Greek checks via the pack SPI (044–053) | **Engine yes** | Encoding tests use synthetic ELOT 928 / Windows-1253 byte fixtures. Real profiling and `REQ-MIG-215` need the real book |
| Pipeline orchestration: checkpointed jobs, dependency order, dry-run hash gate, deltas, deferral, re-drive, throttling, observability (066–080) | **Yes** | Implement as Hangfire jobs with checkpoint state in MIG tables, not a workflow engine |
| Batch reversal via register (076, 211) | Yes, once owners ship reversal ops | Gate G1 |
| Party matching and survivorship (081–090) | Algorithms yes, with synthetic Greek/Latin name pairs | Thresholds need tuning on real data |
| Renewal-based migration pipeline (091–108, 213, 214) | **Orchestration yes**, against synthetic cohorts and a fake L-03 | Needs POL `REQ-POL-341`, RAT `rateBatch` RENEWAL with prior premium, PFC conversion rules, and the time service (`REQ-PLT-332`) for date travel. Tolerance and capping need real price distributions to calibrate |
| Mid-term and big-bang (109–114) | Yes, synthetic | All Should in P1 |
| Billing, finance, tax and fiscal openings (115–126) | **Reconciliation logic yes** | Proof against a real legacy trial balance needs a real extract. The tax/levy boundary split and duplicates-and-gaps check (123) are testable synthetically (invariant 7) |
| Claims, RI, other modules (127–147) | Orchestration yes | Depends on each owner's import API |
| **Reconciliation engine** (005, 171–180) | **Yes, fully** | Three-way counts (BR-MIG-030), control totals per D5 measure, tolerance (€0.00 unexplained; rounding explained per record), breaks and dispositions, signatories with maker-checker, sealed packs. Core totals come only from module reconciliation operations, never their schemas (`REQ-MIG-173`). Ideal for property-based tests |
| Rehearsal tooling, runbook tracker, rollback, hyper-care (181–192) | **Tooling yes** | Mocks 1–3 and the dress rehearsal as *events* need the real book; see the conflict in 14(a)1 |
| Archive store and DSAR ops, retention, legal hold (006, 193–201) | **Yes**, with synthetic archive datasets | Seeded-subject DSAR tests (§14.x) |
| Governance: decision matrix, waves and cohorts, go/no-go, control board, purpose register, regulator plan, scenario decision (030–041, 212) | **Yes** | Pure CRUD plus approvals |
| Screens SCR-MIG-01…10 | **Yes** | — |
| AI features | Optional; defer | Training-data sources conflict with ADR rule 12 |

**Cannot be done without the real legacy:**
- source inventory and dictionary contents
- real crosswalks and approved mapping specs
- real profiling and capacity confirmation (`REQ-MIG-215`)
- L-01…L-08 implementations (OI-MIG-13)
- mocks 1–3 and the dress rehearsal with real volumes
- financial proof against the legacy trial balance
- calibration of match thresholds and tolerance bands
- the legacy deep links

### 15.2 Staging model (as specified)

- **Extracts.**
  - Extracts land encrypted in a MIG staging store in the EU stamp (`REQ-MIG-066`, 202).
  - `SourceDataset` records the source, hash, counts, as-at time and encoding, and is immutable once sealed.
  - Each `StagingRecord` holds an **encrypted payload** plus per-field encodings and profile flags.
- **Access and purge.**
  - Access is by ABAC, with SoD-MIG-07.
  - Raw downloads are forbidden.
  - Staging is purged per RC-MIG-STAGING, 90 days after wave sign-off (+7 days tolerance, NFR-MIG-024).
  - Staging is included in DSAR exports (`REQ-MIG-199`).
- **Batch content.** Each batch is bound to a **content hash**: a real load needs a passed dry-run on the identical hash (BR-MIG-007, `REQ-MIG-070`). Idempotency key = hash(legacy system, object type, legacy key, target op, wave) (BR-MIG-008).
- **Checkpoints and recovery.**
  - Checkpoints every ≤ 1,000 records or 60 s, with resume within 5 minutes (NFR-MIG-008).
  - Staging RPO ≤ 15 min and RTO ≤ 4 h during cut-over (NFR-MIG-013).
- **Implementation implication.** A dedicated `mig` PostgreSQL schema for metadata, outcomes, xref and routes. Encrypted payloads go in PostgreSQL (bytea with envelope keys from Key Vault) or Blob Storage. That choice is undecided.

### 15.3 Coexistence and sync (as specified)

- **Mastery is per term** (BR-MIG-011).
  - Transitioning means the expiring term is in legacy and the renewal term is in the core.
  - Actions are offered only in the master system (`REQ-MIG-153`).
- **Routing reads.**
  - `resolve` is synchronous T1 and authoritative.
  - Consumer caches are fed by `CoexistenceMasterChanged`, partitioned on the stable route_id.
  - DAT uses `asOf` for history. Routing history is the source of truth for combined reporting with no double counting (`REQ-MIG-160`).
- **Party sync.**
  - Core → legacy: from `PartyUpdated` via L-04, acknowledged within 1 business day.
  - Legacy → core: from deltas via PTY's update path.
  - Conflicts are resolved by last *verified* change; otherwise they go to review (BR-MIG-027).
  - Only servicing attributes are synced (BR-MIG-028).
- **Payments.**
  - Misdirected payments run as a queue with maker-checker. BIL executes the transfer, keeping the original value date.
  - Bank files split per system by creditor account or reference prefix, with daily totals (`REQ-MIG-159`).
- **Hand-overs.**
  - Bureau per vehicle (L-07 + CMP).
  - Fiscal per document (core credits correlated to the legacy MARK).
  - Levy and IPT per period.
  - Mandates switch creditor system on a communicated date, with the original reference and no re-signature (`REQ-MIG-116`, BR-MIG-019).
- **RI.** GNPI and aggregates cover both books via periodic legacy aggregates with source keys, idempotent on re-run (`REQ-MIG-139`).

### 15.4 Reconciliation (as specified)

- **Per-batch counts** (`REQ-MIG-171`):
  - extracted = transformed + rejected-in-transform + deferred
  - transformed = loaded + rejected-by-module + skipped-duplicate
  - loaded must equal the module's own count by batch id
- **Money measures (D5)** (`REQ-MIG-172`): written, earned and unearned premium; tax and levies; receivables with ageing; unapplied cash; refunds; claims reserves and paid; recoveries; ceded premium and recoveries; ceded balances; commission. Each is computed by legal entity, line and currency.
- **Tolerance:**
  - counts must match with zero difference
  - money must have €0.00 unexplained, with rounding explained per record
  - status is Matched, Matched-with-rounding, or Break
- **Break dispositions:** fix and re-run; an opening adjustment via `REQ-BIL-290` or `REQ-FIN-215` MIGRATION_ADJUSTMENT with approval; or accept with justification. Breaks raise WRK activities (`REQ-WRK-198`).
- **Specialised reconciliations:**
  - trial-balance proof with sub-ledger ↔ control accounts (118)
  - no double counting: zero current-period journals from migration events (119, BR-MIG-025)
  - CLM ↔ RI ↔ FIN liabilities (121)
  - tax and levy split, duplicates and gaps (123)
  - earned/unearned per term > €0.01 is a break (114)
  - bureau overlaps and gaps reach zero before a wave closes (163)
  - party and AFM coverage (179)
  - re-performance sample of 400 per cohort, re-rating plus document re-render (178)
  - parallel month-end closes (183)
  - reconciliation history across mocks (180)
- **Sign-off:** a sealed wave sign-off pack goes to DOC (177). `MigrationBatchReconciled` is published only after signing plus a distinct second approver.
- This aligns with the ADR testing rule "nightly reconciliation jobs in every environment".

### 15.5 Hardest parts

1. The **renewal-based migration pipeline** end to end: cross-module orchestration over POL, RAT, UW, PFC, DOC and CMP, gap-event re-conversion, the stop-acknowledgement gate, the deadline fallback, and the D7 shared lead time.
2. **Financial reconciliation to the cent** across five money modules, plus the tax/levy/fiscal boundary.
3. The **T1 routing service** with bitemporal routes, term splits and event-fed caches across six consumers.
4. **Batch reversal** depending on reversal operations other teams must ship by G1.
5. The **legacy-side interfaces**, which are outside the team's control.

### 15.6 What must exist first

- PLT: approvals, audit, authority, numbering, time service, pseudonymisation/tokens, retention, legal hold, job scheduler.
- MKT: configuration, capability switch, `REQ-MKT-010` migration baseline.
- Owners' import APIs: PTY first, then PFC, RAT, POL, BIL, CLM, RI, FIN, DOC, CMP, WRK.
- Reconciliation queries.
- The `IdValidator`, `NameTransliterator` and `AddressFormatter` pack SPIs and the `LegacyDataProfile` SPI.

Note: `mig.Xref.register` is **called by the owners' import APIs**, so the xref service (`REQ-MIG-002`) must exist before or alongside the first module import.

### 15.7 Suggested slicing

| Slice | Content | Scenario |
|---|---|---|
| S1 | Import contract types, conformance harness, xref, minimal `Routing.resolve` (default CoreMaster), audit and idempotency, NetArchTest rule | A and B; unblocks every module |
| S2 | Governance: decision matrix, scenario decision (212), waves and cohorts, control board, entry criteria | — |
| S3 | Staging and synthetic legacy simulator, source inventory, dictionary, profiling, DQ library, issues, waivers, encoding conversion | — |
| S4 | Mapping specs, crosswalks, tests, golden records | — |
| S5 | Pipeline jobs (Hangfire) with dependency order, dry-run hash gate, outcomes, deltas, re-drive, `MigrationBatchLoaded` | — |
| S6 | Reconciliation and sign-off engine, `MigrationBatchReconciled` | — |
| S7 | Party conversion and match review | — |
| S8 | Routing table full, legacy index, enquiry, coexistence events | — |
| S9 | Renewal-based migration pipeline and SCR-MIG-04 | — |
| S10 | BIL, FIN, CLM, RI openings and the tax/fiscal/bureau hand-overs | — |
| S11 | Party sync, misdirected payments | — |
| S12 | Runbook, rollback, reversal register, hyper-care | — |
| S13 | Archive, DSAR, decommissioning | — |
| S14 | AI features (optional) | — |
| — | Mid-term and big-bang (Should) | Last |

In scenario A, only S1, a subset of S2, `REQ-MIG-145` intermediary loads and the controls Musts are needed.
