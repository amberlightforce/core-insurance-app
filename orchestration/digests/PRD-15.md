# PRD-15 digest — Data, analytics and regulatory data marts (DAT)

Source: `core-insurance-prds/PRD-15-data-analytics-regulatory-marts.md` (2,179 lines, read in full). Version 1.1 "freeze fix", dated 2026-10-07, status "Baseline candidate", binding input `00-system-contract.md v1.11` (§1.1).
Checked against: `core-insurance-infra/ARCHITECTURE-DECISIONS.md` (ADR), `core-insurance-infra/INFRASTRUCTURE.md` §1, §2, §8, §9, and `00-system-contract.md` CD-20 / D10 (grep only).

---

## 1. Identity

| Item | Value |
|---|---|
| Module code | **DAT** |
| Title | Data, analytics and regulatory data marts |
| Owners (§1.1) | Head of data and analytics (PO); principal data architect; chief actuary; head of regulatory reporting; AI governance lead; DPO |
| Contract decisions applied | CD-02, CD-03, CD-08, CD-11, CD-15, CD-20, R-37/R-52, R-47, D1–D10, R-101, R-102 |

**Purpose (§1.2).** DAT is the read side of the system. It turns every module's domain events and data products into governed, reconciled data for supervisors, actuaries, finance, pricing, fraud, reinsurance and management. It is also the programme's single home for **model governance**: the model registry, plus drift, performance and bias monitoring for every AI feature in every module (CD-11, contract §3.8.5). DAT "never writes back to an operational store". Its only outbound business effects are (a) result sets submitted to FIN's API and (b) monitoring events that PLT acts on (§1.2).

**Five key decisions stated by the PRD (§1.2):**
1. Events are the primary feed. CDC is only a backstop for daily reconciliation and gap detection (REQ-DAT-001, -050).
2. Bitemporality is modelled in columns, not taken from storage time travel. Every silver and gold row carries `valid_from/valid_to`, `recorded_from/recorded_to` and a submission snapshot key (REQ-DAT-002).
3. Regulatory figures come from coverage-grain mappings (PFC) plus versioned transforms (DAT) over MKT code lists. A mart run is published only when it reconciles to FIN's locked balances (REQ-FIN-155), and `RegulatoryMartPublished` carries the reconciliation status. CMP renders and validates XBRL in-house; DAT supplies a template-neutral data-point package plus the risk function's SCR results (D10; REQ-DAT-116, -300).
4. Actuarial results return only through `fin.ActuarialResults.submit` (REQ-FIN-009), versioned and approved by a second actuary in FIN. DAT never posts journals (REQ-DAT-004).
5. One registry and one monitoring service cover all 119 AI features in PRD-01…17. Breaches publish `ModelDriftDetected` / `BiasThresholdBreached`; PLT then auto-disables or flags the feature (REQ-DAT-005, REQ-PLT-227).

**Non-goals / out of scope (§1.4):**

| Out of scope | Owner |
|---|---|
| Operational stores and their logic | each module |
| Journal posting, books, period close, IFRS 17 group assignment | FIN (REQ-FIN-001/007/011) |
| Report submission, filing, submission tracker, supervisor correspondence | CMP (REQ-CMP-008) |
| XBRL / xBRL-CSV rendering and EIOPA taxonomy validation of instance documents | CMP, in-house (D10) |
| Standard-formula SCR/MCR calculation | Risk function (ROLE-48), **outside the core system** |
| AI system register and AI Act classification | CMP (REQ-CMP-007) |
| AI control plane (gateway, toggles, kill switch, interaction records) | PLT (REQ-PLT-010) |
| Regime code lists and crosswalk content | MKT (REQ-MKT-007) |
| Coverage-grain regulatory code assignment | PFC (REQ-PFC-005) |
| Accumulation check at bind; capacity derivation | UW (REQ-UW-008), RI (REQ-RI-005) |
| Retention engine, legal hold, crypto-erasure primitives, non-prod pseudonymisation | PLT (REQ-PLT-011) |
| DSAR orchestration | CMP (REQ-CMP-005); DAT is a fan-out target |
| Migration ETL into modules | MIG (REQ-MIG-001) |
| Corporate ERP and Fairfax consolidation system | External; DAT sends group feed files (REQ-DAT-138) |

**Phase scope (§1.5).** P1 = Greek private motor MVP; P2 = home; P3 = commercial property and liability with full ceded RI; P4 = later markets (Cyprus first). Per D6:
- P1 has annual terms only; six-month terms remain a golden test case.
- Migration uses scenario B (book converted at renewal), so coexistence marts run for one renewal cycle (REQ-DAT-297).
- Bancassurance is out of P1.
- Motor RI is assumed to be XoL-only; proportional net-of-cession views come in P2–P3.

A.1004 is "not applicable to motor; structure built" in P1 and Full in P2. The annual IPT report is "Optional per pack (Greece pending OI-FIN-04)". Motor cat exposure is a Should in P1.

---

## 2. Size metrics

| Measure | Count | Source |
|---|---|---|
| REQ total (incl. 9 anchors REQ-DAT-001…009) | **283** (IDs 001–009, 030–303) | §5.24, verified by table parse |
| Must / Should / Could / Won't | **225 / 50 / 8 / 0** | §5.24, verified |
| [BASELINE] / [ENHANCEMENT] | 248 / 35 | §5.24, verified |
| **Must + P1 ("Motor MVP")** | **214** | computed from the tables |
| P1 total (all MoSCoW) | 263 (214 Must, 42 Should, 7 Could) | computed |
| Must outside P1 | 11: P2 = REQ-DAT-063, 125, 126, 128, 180, 181, 183, 186, 291; P3 = 109; P4 = 102 | computed |
| Should P2/P3/P4; Could P3 | 6 / 1 / 1; 1 | computed |
| Unnumbered Must | Greek/English language switch (§6.0, "Must, P1", R-101), not in the 283 | §6.0 |
| BR | 30 (BR-DAT-001…030) | §10.1 |
| NFR | 19 (NFR-DAT-001…019) | §14 |
| Screens | 18 (SCR-DAT-01…18); owned inventory items 0; consumed inventory items 8 | §6, §6.19 |
| Owned entities | ~32 metadata entity types (§7.1.1) plus lakehouse layers (§7.1.2) | §7 |
| Events produced | 12 names in 11 rows (§8.1): 5 contract events plus 7 added by R-59 | §8.1 |
| Events consumed | essentially all events of all 16 other modules, via ~45 data contracts (§8.3) | §8.2–8.3 |
| Exposed operations | ~25 operation groups (§9.1) | §9.1 |
| Outbound integrations | 13 internal call groups (§9.2), 8 external (§9.3) | §9 |
| AI features | 8 owned (AI-DAT-01…08), plus a register of 119 programme features | §11 |
| Config keys | 16 (§10.2) | §10.2 |
| Open issues | 15 IDs (OI-DAT-01…15); 6 closed (06, 09, 10, 11, 14, 15) | §16.1 |
| CCRs | 8 (CCR-DAT-01…08), all Accepted | §16.4 |

**Build-size estimate: L.** There are 214 Must-P1 requirements (more than double the L threshold). On top of that come heavy temporal logic (a full bitemporal model over every entity, plus "as reported" reproducibility), money reconciliation to FIN to the cent, two parallel regulatory taxonomies, an ML registry and monitoring service for 119 features, and a privacy layer (crypto-shredding, tokenisation, purpose grants). Under the binding stack it also needs a re-architecture from "lakehouse" to PostgreSQL `rpt_*` schemas (see §14).

---

## 3. Owned entities

### 3.1 Storage approach (§7.0) — as written
There are two stores:
- **(a) A PostgreSQL "operational metadata schema"**, owned by DAT like any other module schema. It holds registries, contracts, mart-run headers, grants, breaches and workflow state.
- **(b) A "lakehouse"**: bronze, silver, gold, legacy zone, CDC zone, identity vault and a sealed AI environment, held as "open-format transactional tables on EU object storage" (REQ-DAT-067/068). The PRD calls it "an open-source, self-operated open table format (D10, CD-20)".

Every row carries `legal_entity_id`, `jurisdiction`, `created_at`, `created_by` and `record_version`. Silver and gold rows also carry:
- the half-open `valid_*` and `recorded_*` intervals (UTC record time, XMR-FR-150, D5);
- `source_event_ids`;
- business keys (quote, job, transaction, charge, invoice, journal ids);
- `correlation_id` (technical only), `configuration_hash` and `origin`.

### 3.2 Metadata entities (§7.1.1)

| Entity | Key attributes / constraints | Retention |
|---|---|---|
| DataContract / DataContractVersion | id `dc.<mod>.<entity>.v<major>` + minor; producer; source kind EVENT / DATA_PRODUCT / CDC; schema ref; per-field P0–P3 class; keys; freshness; quality refs; two stewards; a breaking change creates a new major | RC-DAT-META (life + 10 y) |
| QuarantineRecord | contract version, reason code, first seen, encrypted payload pointer (per-subject keys), release actor/time | RC-DAT-QUAR (90 d after release) |
| IngestionCursor | source, cursor, last pull, control totals; monotonic | RC-DAT-OPS (2 y) |
| RegimeDefinition / TaxonomyTransform / TransformVersion | regime, taxonomy version, applicability rule, template set per frequency, data-point rules, fixtures, activation approval; maker-checker activation | RC-DAT-META |
| TreatyLineMapping | regime, taxonomy version, treaty type, proportional flag, target line category, effective period; complete per regime | RC-DAT-META |
| MartRun | mart (SII, BOG_STAT, A1004, IPT_ANNUAL, EAEE, GROUP_USD, …), entity, period, regime, taxonomy version, transform and mapping versions, record-time cut, inputs hash, status, reconciliation status, preparer, signer, supersedes, CMP submission ref; immutable after SignedOff; number from PLT numbering | RC-DAT-MART (10 y after period, longer if an open supervisory matter references it) |
| MartFigure | run, data point (template, row, column, open-axis keys), computed value, adjustment, final value (= computed + adjustment), unit, lineage pointer | as MartRun |
| MartAdjustment | figure, amount, reason, evidence ref (DOC archive), maker ≠ checker | as MartRun |
| **ScrResultSet** (new in v1.1) | entity, period, taxonomy version(s), method/parameter version, module and sub-module figures, input run ids, file hash, preparer (ROLE-48), acceptor, approver; status Submitted / Rejected / Accepted / Superseded; acceptor ≠ preparer and approver ≠ acceptor; must be complete against the template set's SCR data points | RC-DAT-MART |
| ReconciliationDefinition / ReconciliationResult | scope CDC, MART_TO_FIN, CROSS_MART or MIGRATION; measures, tolerance, differences, hashed keys | RC-DAT-DQ (10 y) |
| DQRule / DQResult | data product, dimension, expression, severity, threshold, owner, effective period | RC-DAT-DQ |
| Break (DQ incident) | type, severity, product, module, period, amount, keys, owner, WRK ref, resolution | RC-DAT-DQ |
| LineageNode / LineageEdge | node types incl. event type, contract field, column, transform, mart figure, KPI, journal ref, result set, SCR result set, governed artefact version with `governance_stage`; acyclic per run | RC-DAT-META |
| CatalogueEntry / GlossaryTerm | EN/GR descriptions (certification requires both), owner, classification, retention, certification | RC-DAT-META |
| KpiDefinition | code, EN/GR names and definitions, formula, grain, filters, sources, owner, refresh tier, certification, version | RC-DAT-META |
| Plan / PlanVersion | entity × line × channel × month × measure; approved before use | RC-DAT-MART |
| Model / ModelVersion | owning module, AI features, CMP register ref, owner, developer, validator (≠ developer), type, model card, artefact hash, data snapshot hash, PLT endpoint ref, status | RC-DAT-AIDOC (10 y after retirement) |
| Feature / FeatureVersion | name, entity, definition, lineage, P-class, allowed purposes, freshness, owner, dependent models; protected-proxy rule | RC-DAT-META |
| MonitoringProfile | MP-A…MP-H: metrics, thresholds, cadences, sample sizes | RC-DAT-AIDOC |
| AiFeatureCoverage | keyed on CMP `AiSystem` id (system of record, R-82); model ids, profile, thresholds, bias plan, Covered / NotCovered. Does **not** hold class, toggle scope or breach response (those are CMP `AiSystem` and PLT `AiFeatureRuntime`) | RC-DAT-AIDOC |
| ModelMonitor | metric time series per feature × version × window, with threshold evaluation | RC-DAT-AIDOC |
| BiasReport | method, proxy groups, rates, ratios, CIs, band, result, reviewer; aggregates only | RC-DAT-AIDOC |
| MonitoringBreach | type DRIFT / PERFORMANCE / BIAS / GOVERNANCE, event id, WRK ref, remediation, justification | RC-DAT-AIDOC |
| ActuarialInputSet | valuation date, sources with control totals, inputs hash, reconciliation status (must be Reconciled before submission) | RC-DAT-ACT (10 y) |
| ActuarialRun | input set, model version, code version, parameters, outputs hash, preparer, attestation | RC-DAT-ACT |
| ActuarialSubmission | run, result types, grain, payload hash, FIN result-set id (FIN owns the result set, R-82), supersedes | RC-DAT-ACT |
| ActuarialResultSetView | read model of FIN `ActuarialResultSet` status; balances are not duplicated | RC-DAT-ACT |
| ExposureSnapshot / ModelledLossSet | as-at date, scheme versions, sums insured by zone; provider, model version, snapshot ref | RC-DAT-CAT (10 y) |
| PurposeGrant | purpose, data set, columns, grantee, expiry ≤ 12 months, approvals | RC-DAT-PRIV (6 y after expiry) |
| LakehouseErasureTask / LakehouseRestrictionTask | CMP DSAR ref, subject token, layers, keys destroyed, restriction reason, evidence | RC-DAT-PRIV |
| SyntheticDataSet | generator, parameters, privacy test results, release status | RC-DAT-META |
| ExportLog | user, object, filters, rows, purpose | RC-DAT-PRIV |

The retention durations are proposals. Final values come from the programme retention schedule, loaded as Greece-pack data under REQ-PLT-011 before system test (XMR-F-329; OI-DAT-06 closed).

### 3.3 Lakehouse layers (§7.1.2) — maps to infra `rpt_*`

| PRD layer | Contents | Natural infra home (infra §2) |
|---|---|---|
| Bronze | `bronze.<mod>_<event>_v<major>`, immutable append-only, envelope + payload as received, partitioned by producer and record date; P1–P3 values encrypted under per-subject keys | `rpt_raw` |
| CDC zone | `cdc.<mod>_<table>`, 35-day retention, reconciliation only | **no infra home** (see §14) |
| Identity vault | `vault.subject_token`, `vault.identifier` | **no infra home** |
| Silver | ~37 bitemporal `<Entity>View`s: PartyView, AccountView, IntermediaryView, ProducerOfRecordView, PolicyTermView, SegmentView, CoverageView, RiskUnitView, ChargeView, InvoiceView, PaymentView, BillingLedgerView, ClaimView, ExposureView, ReserveLineView, ClaimFinancialView, RecoveryView, CatEventView, CessionView, RIRecoveryView, RIContractView, JournalLineView, Ifrs17GroupView, DocumentView, DeliveryView, ActivityView, InboundDocumentView, FiscalDocumentView, BureauEventView, ClockInstanceView, ComplaintView, AiInteractionView, ConfigurationView, ProductVersionView, RegulatoryMappingView, RatingEventView, UWDecisionView (REQ-DAT-071 lists the minimum 20) | `rpt_conformed` |
| Gold | regulatory (`gold.sii_*`, `bog_stat_*`, `a1004`, `ipt_annual`, `eaee_*`, `group_usd_*` with `mart_run_id`); actuarial (triangles, exposure measures, IFRS 17 PAA inputs, large loss); finance; pricing; UW; fraud; operations; distribution; customer; cat; AI monitoring | `rpt_mart` |
| Feature store | offline `fs.<entity>_<feature>` + online key-value store (≤ 50 ms p95) | **no infra home** |
| Legacy zone | `legacy.<object>` | possibly `rpt_raw`; not stated |
| Sealed AI environment | bias proxy inference, generative synthesis; ephemeral ≤ 30 days | **no infra home** |

### 3.4 State machines (§7.3)

- **MartRun:**
  - Main path: Building → Review → PendingSignOff → SignedOff → HandedOff → Superseded.
  - Side paths: Building → Failed (pipeline error or critical DQ failure); Failed → Building (rerun); Review → Building (rerun after source fix); PendingSignOff → Review (signer returns); SignedOff → Superseded (resubmission before hand-off).
  - Guards: Review → PendingSignOff needs no open blocking break (REQ-DAT-054) and a status of Reconciled or approved explained differences. PendingSignOff → SignedOff needs signer ≠ preparer, authorised via REQ-PLT-004. SignedOff → HandedOff needs the export package to exist and emits `RegulatoryMartPublished`. HandedOff → Superseded emits `RegulatoryMartPublished` with `supersedes`.
- **ModelVersion:** Draft → InValidation (card complete) → Validated (validator ≠ developer) → Approved (maker-checker). InValidation → Draft on findings. Approved ⇄ Suspended (breach auto-disable via PLT, or owner suspension; return needs remediation plus re-approval). Approved or Suspended → Retired. Events: `ModelVersionRegistered` on creation, `ModelVersionApproved`, `ModelVersionRetired`.
- **MonitoringBreach:** Open → Acknowledged → Remediating → ReTesting → Closed. ReTesting → Remediating when the re-test fails. Open → Closed as a false positive needs AI governance lead approval. Opening emits `ModelDriftDetected` or `BiasThresholdBreached`.
- **Break:** Open → Assigned → Resolving → Resolved; Open → Resolved (AUTO_REPLAY); Resolving → AcceptedDifference (maker-checker). Events `DataReconciliationBreakRaised` / `…Resolved`.
- **DataContractVersion:** Draft → Active → Deprecated → Retired. A minor version stays Active; a breaking change creates a new major Draft. Emits `DataContractVersionPublished` on Active.
- **ActuarialSubmission:** Draft → Ready (attested) → Validated (FIN validate clean or justified) → Submitted. FIN-side states (Approved, Posted, Rejected, Superseded) are mirrored in ActuarialResultSetView; Rejected returns the submission to Draft. Emits `ActuarialResultsPublished` on Submitted and on Posted.
- **PurposeGrant:** Requested → PendingApproval → Active → Expired / Revoked; PendingApproval → Rejected.
- **LakehouseErasureTask:** Received → Checking → Erasing → Completed; Checking → Restricted → Completed; any state → Failed → Erasing (retry). Emits `LakehouseErasureCompleted`.
- **Governance stage mapping (REQ-DAT-302, CR-S3-25).** Each own state maps to the PRD-18 §9.4 stages Authoring / Under review / Approved not live / Live / Replaced / Withdrawn. Examples:
  - ModelVersion Draft → Authoring; InValidation and Validated → Under review; Approved and Suspended → Live; Retired → Replaced or Withdrawn.
  - A TransformVersion awaiting its activation date → Approved not live.

---

## 4. Consumed entities / dependencies (§7.2, §8.3, §9.2)

| Owner | Entities | How DAT reads them |
|---|---|---|
| PTY | Party, Account, PartyRole, Consent, Intermediary, ProducerOfRecord | Events `dc.pty.party/account/consent/distribution/screening.v1`; nightly snapshot pull; values tokenised |
| PFC | ProductVersion, CoverageDef, ChargeType, RegulatoryMapping | Events plus pull `pfc.Catalogue.get` and `pfc.RegulatoryMapping.get` (REQ-PFC-005/144/145) |
| RAT | RatingArtifact, rating events | Events; pull `rat.impact.v1` (REQ-RAT-212) |
| UW | Issue, Referral, Decline, contingencies | Events `dc.uw.decision/contingency.v1` |
| POL | Policy, Term, Job, Transaction, Segment, risk units, ChargeDelta | Events (`dc.pol.policy/correction/charge.v1`); pull `pol.Segment.changes` (≤ 15 min); `pol.Policy.get` (validAt/knownAt) |
| BIL | Invoice, Payment, ledger, commission, disbursement (D1 sources), quarantine status | Events plus daily ledger extract (REQ-BIL-011) |
| CLM | Claim, Exposure, ReserveLine, financials, Recovery, CatEvent, SIU outcome | Events; pull reserving feed (REQ-CLM-228) and SIU feedback (REQ-CLM-207) |
| RI | Contract, Cession, Recovery, IFRS 17 links, programme / recoverables / counterparty / cat / USD products | Events; pulls REQ-RI-201/223/227/229/230/231/232; `ri.Accumulation.*` |
| FIN | JournalLine, Ifrs17Group, ClosePeriod, ActuarialResultSet | Events (`PeriodClosed` triggers marts); pulls of reconciled balances REQ-FIN-155, group data REQ-FIN-147, actuarial extract REQ-FIN-279, dimension dictionary REQ-FIN-097, group pack REQ-FIN-210, IPT returns REQ-FIN-005, posted written premium REQ-FIN-036 |
| DOC | RenderedDocument, Delivery | Events plus daily counts (REQ-DOC-318); DOC archive for packages (REQ-DOC-004) |
| CMP | FiscalDocument, BureauEvent, ClockInstance, Complaint, DsarRequest, AiSystem | Events; clock `CMP_A1004_RETURN`; submission tracker (REQ-CMP-201) |
| WRK | Activity, InboundDocument | Events (assignee pseudonymised) |
| PLT | AiInteractionRecord, ModelEndpoint, AiToggle, FxRate, AuditEvent, schema registry, event archive | Events plus pull of interaction records (REQ-PLT-223) and metrics (REQ-PLT-234) |
| MKT | Config hash, Pack, RegimeCodeList, crosswalks, group pack | Events; `mkt.RegimeCode.list/get/validate` |
| MIG | Batches, waves, cross-reference | Events; `mig.Routing.resolve` (REQ-MIG-149) |
| CHN | Journey analytics | Aggregated or pseudonymous, with consent only (REQ-DAT-038) |

**Foundation dependencies (the "Requires" column):**
- PLT: event infrastructure REQ-PLT-005/140/145; workflow engine REQ-PLT-007/330; adapter host REQ-PLT-006; maker-checker REQ-PLT-004; audit REQ-PLT-002; retention REQ-PLT-237/238; key management and crypto-erasure REQ-PLT-240/241; non-prod REQ-PLT-242/243; ABAC REQ-PLT-001.
- WRK: REQ-WRK-001.
- FIN: REQ-FIN-155 and REQ-FIN-273…279.
- PFC: REQ-PFC-005.
- MKT: REQ-MKT-007/205/207/208/209/210/326.
- POL: REQ-POL-005/006/094.

---

## 5. Events

Topic `dat.events.v1`. Partition keys: `mart_run_id`, `model_id`, `run_id`, `task_id`, `break_id` (§8).

### 5.1 Produced (§8.1)

| Event | Trigger | Key payload | Consumers |
|---|---|---|---|
| `ModelVersionRegistered` | Version created (REQ-DAT-202) | model id, version, module, AI features, artefact hash, status | CMP (REQ-CMP-007), PLT (REQ-PLT-218) |
| `ModelDriftDetected` | Performance / drift / acceptance breach after a minimum-sample check (REQ-DAT-215) | feature, version, metric, value, threshold, window, sample, severity, breach id | all modules + PLT (REQ-PLT-227) |
| `BiasThresholdBreached` | CI of the bias ratio lies wholly outside the band (REQ-DAT-218) | feature or artefact, version, proxy group, ratio, CI, band, sample, breach id | all modules incl. RAT artefacts, PLT |
| `RegulatoryMartPublished` | Mart run signed off and handed off (REQ-DAT-115) | mart, entity, period, regime, taxonomy version, run id, reconciliation status, inputs hash, supersedes | CMP (REQ-CMP-008/197), FIN, MIG |
| `ActuarialResultsPublished` | Submitted to FIN, and again when posted (REQ-DAT-152) | run, result set id, book(s), valuation date, model version, status, journal refs | FIN |
| `ModelVersionApproved` (R-59) | Approved (REQ-DAT-202) | model, version, approvers, effective date | none declared (informational) |
| `ModelVersionRetired` (R-59) | Retired | model, version, reason | none declared |
| `DataContractVersionPublished` (R-59) | Contract version becomes Active | contract, version, breaking flag, producer | none declared |
| `DataReconciliationBreakRaised` / `…Resolved` (R-59) | Break opened / resolved (REQ-DAT-246, Should) | type, module, entity, period, amount, marts, resolution | none declared |
| `AccumulationSnapshotPublished` (R-59) | Exposure snapshot complete (REQ-DAT-189, Should, P2) | snapshot id, as-at, scheme versions, totals | RI, UW |
| `LakehouseErasureCompleted` (R-59) | Erasure or restriction done (REQ-DAT-275) | DSAR ref, ERASED/RESTRICTED, layers, counts, reason | CMP, **evidence only** (R-97); completion goes through the synchronous `dat.Erasure.execute` response |

### 5.2 Consumed (§8.2)
Every event is consumed idempotently on `event_id`, ordered by aggregate `sequence`, validated against its contract and landed in bronze first. The ones that drive behaviour:

| Event(s) | From | Reaction |
|---|---|---|
| `PeriodClosed`, `PeriodReopened` | FIN | Trigger mart runs per regime and active taxonomy version (REQ-DAT-104) |
| `ActuarialResultSetPosted` + FIN acknowledgements | FIN | Return-path status (REQ-DAT-154) |
| `ReconciliationBreakRaised` | FIN | Folded into the finance close view; no stated DAT action |
| `ChargeDeltaEmitted`, `TransactionReversed`, `TransactionReapplied` | POL | Correction sets and written premium (REQ-DAT-042/043/299) |
| `PartiesMerged`, `PartyUnmerged` | PTY | Re-point party references as new record-time versions (REQ-DAT-044) |
| `ConsentChanged` | PTY | Purpose-view exclusion (REQ-DAT-277) |
| `FiscalDocRegistered`, `FiscalDocRejected` | CMP | IPT line fiscal identity (REQ-DAT-130); rejection-rate KPI (REQ-DAT-136) |
| `BureauEventSubmitted`, `BureauLagExceeded` | CMP | Information Centre lag KPI (REQ-DAT-135) |
| `RegulatorySubmissionFiled` / `Rejected` | CMP | Status on the mart run; a rejection opens a resubmission candidate (REQ-DAT-119/117) |
| `DSARReceived` / `DSARCompleted` | CMP | Task tracking (REQ-DAT-270) |
| `CatEventDeclared` / `CatEventChanged` | CLM | Event footprint (REQ-DAT-183) |
| `AiToggleChanged`, `AiKillSwitchActivated` | PLT | AI coverage status |
| `LegalHoldApplied` / `Released` | PLT | Erasure vs restriction (REQ-DAT-272) |
| `ReferenceDataPublished` | PLT | Bitemporal FX and calendars (REQ-DAT-058) |
| `RegimeCodeListPublished` | MKT | Code lists (REQ-DAT-059) |
| `ConfigurationActivated`, `PackActivated`, `PackRolledBack`, `RateTableChanged` | MKT | Configuration dimension keyed by hash (REQ-DAT-061) |
| `ProductVersionPublished` | PFC | Catalogue and mapping refresh (REQ-DAT-060) |
| `MigrationBatchLoaded` / `Reconciled` | MIG | Migration reconciliation (REQ-DAT-253) |
| `MigrationWaveStatusChanged` | MIG | Coexistence scope (REQ-DAT-297/298). DAT does **not** consume `CoexistenceMasterChanged` (R-89) |
| `DisbursementRejected`, `DisbursementStopped` | BIL | Disbursement views (D4) |
| BIL quarantine status (in BIL data contract) | BIL | GWP reconciliation (REQ-DAT-301) |

The full data-contract inventory per producer is in §8.3: about 45 contracts across PTY, PFC, RAT, UW, POL, BIL, CLM, RI, FIN, DOC, CMP, CHN, WRK, PLT, MKT and MIG. Freshness is NRT ≤ 2 min, D (daily), on event, or ≤ 15 min (segments).

---

## 6. APIs

### 6.1 Exposed (§9.1)
REST under `/api/dat/v1/`. Every command takes an `Idempotency-Key`. Errors are RFC 9457 with codes `DAT-ERR-NNN`. Queries are cursor-paginated and ABAC-filtered.

| Operation | Req | Notable errors / dry-run |
|---|---|---|
| `dat.DataContract.register` / `get` / `list` | 030, 031 | ERR-001 breaking change without new major; dry-run returns compatibility |
| `dat.Ingestion.replay` | 039 | ERR-002 scope too broad without approval; dry-run gives counts |
| `dat.Quarantine.release` | 034 | — |
| `dat.Query.asOf` (view, validAt, knownAt) | 082 | ERR-010 view not permitted |
| `dat.Population.sample` / `aggregate` | 087, 163, 167, 169 | ERR-011 too large → async job |
| `dat.Kpi.get` | 166, 226, 231 | — |
| `dat.Mart.run` | 104 | ERR-020 unmapped; ERR-021 open blocking break; dry-run gives scope and unmapped list |
| `dat.Mart.get` / `figure` / `compare` | 085 | — |
| `dat.Mart.adjust` | 114 | ERR-022 run locked |
| `dat.Mart.signOff` | 115 | ERR-023 signer is preparer |
| `dat.Mart.export` (package ref in DOC archive, manifest hash) | 116 | ERR-024 not signed off |
| `dat.ScrResults.submit` / `accept` / `get` | 300 | ERR-025 SCR data point missing; ERR-026 acceptor is preparer |
| `dat.Lineage.trace` | 248, 249, 302 | — |
| `dat.Accumulation.snapshot` / `footprint` / `trend` | 180–187 | — |
| `dat.Model.register` / `get` / `submitValidation` / `recordValidation` / `approve` / `retire` | 197–207 | ERR-030 validator = developer; ERR-031 card incomplete |
| `dat.Coverage.status` (PLT calls before enablement) | 209 | — |
| `dat.Monitor.metrics` (labels and metrics from modules) | 210, 211 | ERR-032 unknown feature |
| `dat.BiasTest.run` | 216 | ERR-033 sample below minimum |
| `dat.FeatureStore.getOnline` / `buildTrainingSet` | 191, 192 | ERR-040 purpose not allowed |
| `dat.Actuarial.inputSet` / `run` / `resultSet.submit` / `supersede` | 144–155 | ERR-050 inputs unreconciled; ERR-051 model not approved; dry-run calls FIN validate |
| `dat.Subject.export`, `dat.Erasure.execute`, `dat.Restriction.apply` | 270–275 | ERR-060 legal hold → restriction |
| `dat.PurposeGrant.request` / `decide` / `revoke` | 264 | ERR-061 |
| `dat.Synthetic.generate` | 280, 281 | ERR-070 privacy test failed |
| `dat.Analytics.ask` | 234 | — |

### 6.2 Consumed (§9.2)
- **FIN:** `fin.ActuarialResults.validate` / `submit` / `get`; `fin.Ifrs17Group.assignment` / `get`; FIN data products.
- **POL:** `pol.Segment.changes`, `pol.Policy.get`.
- **PFC:** `pfc.Catalogue.get`, `pfc.RegulatoryMapping.get`.
- **MKT:** `mkt.RegimeCode.list` / `get` / `validate`.
- **RI:** `ri.Accumulation.zone` / `query` / `footprint`.
- **CLM:** reserving feed and SIU feedback. **RAT:** impact export.
- **PLT:** event infra and replay, workflow engine, adapter host, maker-checker, audit, retention, key management, AI records, enablement check, numbering, time service, ABAC.
- **WRK:** activities and notifications. **DOC:** archive.
- **CMP:** tracker, AI register, DSAR, clocks.
- **SPIs** (see §7).

### 6.3 External (§9.3; all through the PLT adapter host)

| External | Direction | Format | Status |
|---|---|---|---|
| CMP in-house XBRL renderer (internal) | Out | CSV/JSON data-point package + manifest, one per taxonomy version | P1; settled by D10 |
| Risk function SCR results | In | CSV/JSON result file + manifest via `dat.ScrResults.submit` | P1; layout to agree with ROLE-48 |
| Fairfax group reporting | Out | CSV/JSON default per REQ-FIN-210, over SFTP or API | P1; format UNVERIFIED (OI-DAT-12) |
| Cat-model providers | Out/In | open exposure format; ELT / EP curves | P2; provider TBD |
| Hazard-data providers | In | geospatial zonings via `Geocoder` | P2 |
| EAEE | Out | pack layout | P1; UNVERIFIED (OI-DAT-05) |
| AADE (A.1004, IPT annual) | Out via CMP | pack format | P1 IPT / P2 A.1004; UNVERIFIED (OI-DAT-02/04) |
| Bank of Greece | Out via CMP | data points | P1; UNVERIFIED (OI-DAT-03) |

---

## 7. SPIs / country-pack interfaces

| SPI | Use | Source |
|---|---|---|
| `StatutoryDataReturnFormat` (new, CCR-DAT-06 → R-68, REQ-MKT-326; operations `returnTypes`, `layout`, `build`, `validate`; PRD-17 §9.4.42) | A.1004 file, national statistical templates, EAEE layout, motor market extracts | REQ-DAT-120, 128, 133, 137 |
| `RegimeCodeList` (MKT) | code lists and crosswalks per taxonomy | REQ-DAT-059, 094 |
| `IdValidator` | A.1004 tax-number validity | REQ-DAT-127 |
| `Geocoder` | hazard scheme codes and versions; parallel schemes (R-26) | REQ-DAT-064 |
| `TaxReturnFormat` | tax return totals reconciliation (FIN, CMP) | §10.4 |
| `TaxCalculator.treatment` | IPT amounts are taken as computed upstream, never recomputed (D2) | REQ-DAT-129 |
| `StatutoryClockSet` | deadlines (A.1004 via CMP clock `CMP_A1004_RETURN`) | §10.4, REQ-DAT-125 |
| `PricingConstraint` (R-24) | prohibited-proxy list for bias tests and feature refusal | BR-DAT-022, §10.4 |
| PLT retention catalogue with pack values | RC-DAT-* durations | §7.1.1 |

**Cyprus stub (§10.4).** There is no A.1004, no Greek IPT report and no EAEE feed. It has a different statistical class list and a single-class premium tax layout. Solvency II is identical. It must run through the same engine with no core change (REQ-DAT-102, Must, P4, but tested in CI).

---

## 8. Screens (§6)

Shared conventions (§6.0):
- PLT staff shell (REQ-PLT-327, IB-24) with command palette IB-01.
- Global filter bar IB-26; data-as-of on every widget; explain-why popover IB-08.
- States only from contract §3.9.9.
- Permission codes `dat.<area>.<action>`; P2/P3 masked unless a purpose grant applies.
- Greek/English switch without reload or loss of input (R-101, REQ-MKT-005).

| ID | Name | Personas | Purpose / notable patterns |
|---|---|---|---|
| SCR-DAT-01 | Regulatory report review (mart run) | ROLE-32, 24, 29, 48 (SCR tab), 43 RO, 44 RO via CMP evidence-room grant (REQ-CMP-222) | Split pane IB-05; template list with IB-32 status; data-point grid; lineage via IB-08; diff IB-07 vs previous / as-reported; progress IB-12; adjustments (maker-checker); sign-off (`dat.mart.signoff`, comment ≤ 2,000 chars); export; resubmission; SCR results tab. Consumes UIL-K2 (CMP), UIL-F4 (FIN) |
| SCR-DAT-02 | DQ scorecard and reconciliation breaks | ROLE-33, 32, 24, 38, 41 | Exception-first IB-28; work views IB-03; hero IB-17; priority list IB-04; resolutions AUTO_REPLAY / SOURCE_FIX / CONTRACT_CHANGE / ACCEPTED_DIFFERENCE (maker-checker); overdue after 2 business days |
| SCR-DAT-03 | Lineage explorer | ROLE-32, 35, 33, 43, 44 (by grant) | Graph IB-15; dense log IB-29; as-known-at switch; audited download |
| SCR-DAT-04 | Executive dashboard | ROLE-47, ROLE-24 | Hero metrics GWP, PIF, LR, RET, CMPL vs plan / prior; IB-30 entry |
| SCR-DAT-05 | Motor portfolio dashboard | ROLE-14, 34, 13 | QV, CONV, FREQ, SEV, ICLAG, FDREJ |
| SCR-DAT-06 | Claims operations dashboard | ROLE-18, 45 | Open claims, cycle, FCSLA, offer clocks; cat-event mode IB-25; team-level only |
| SCR-DAT-07 | Distribution dashboard | ROLE-09, ROLE-07 (own book) | Hierarchy drill; ABAC producer scope |
| SCR-DAT-08 | Catastrophe accumulation map | ROLE-27, 11, 13, 18, 34 | Map canvas + zone layer + footprint overlay; gross/net; modelled loss; data-table alternative (NFR-DAT-016). Consumes UIL-C11 |
| SCR-DAT-09 | Model registry | ROLE-36, 49, 29, 34, 35 | Work views; model card; pinned comments IB-20; diff IB-07 |
| SCR-DAT-10 | Model and AI-feature monitoring | ROLE-29, 49, owners, 36; ROLE-21 restricted | IB-32 per feature; breaches IB-04; bias table; maker-checker thresholds |
| SCR-DAT-11 | Data catalogue and glossary | all analysts, 33, 30 | Search-first, accent-insensitive EN/GR; tabs; IB-27 AI drafts |
| SCR-DAT-12 | Data contracts and ingestion health | ROLE-33, 38 | Dense table; schema diff; quarantine release; replay (dry-run first); links to PLT batch monitor and log explorer |
| SCR-DAT-13 | Actuarial data and result-set workbench | ROLE-35 | Work-left IB-11; triangle grid; statement view IB-13; agent plan panel IB-06; FIN status |
| SCR-DAT-14 | Mapping and taxonomy transform workbench | ROLE-32, 33 | Regimes/versions, coverage mapping grid (read-only, PFC-owned), transform rules, treaty-line mapping, fixtures |
| SCR-DAT-15 | Pricing analytics workbench | ROLE-34 | Rating cells, expected vs actual, bias results. Consumes UIL-U10 |
| SCR-DAT-16 | Governed self-service and conversational analytics | ROLE-36, 34, 35, execs | IB-30; query shown and editable; IB-09 citations; small cells suppressed |
| SCR-DAT-17 | Lakehouse privacy console | ROLE-30, 33 | Grant requests (expiry ≤ 12 m, default 90 d), erasure backlog, unclassified columns |
| SCR-DAT-18 | Data operations home | ROLE-33, 32, 38 | Work-left IB-11; quarter-end progress IB-12; motion IB-23 |

Dashboards (SCR-04…08) are SVG/chart and map heavy. NFR-DAT-016 requires data-table alternatives for every chart and for the map.

---

## 9. Regulatory, tax and statutory rules

### 9.1 Stated explicitly (with IDs)

| Rule | Text / value | ID |
|---|---|---|
| Solvency II taxonomy applicability | 2.8.x (2.8.2 per F-01) applies through Q4/annual 2026; 2.10.0 from the Q1 2027 reference period. An undertaking whose financial year ends before 30 Jan 2027 applies 2.8.0, after it 2.10.0 (EIOPA Q&A 3589). A calendar-year Greek insurer therefore files annual 2026 under 2.8.x and Q1 2027 under 2.10.0, running two transforms in parallel in H1 2027 | F-01, REQ-DAT-095, BR-DAT-004 |
| SII review | Directive (EU) 2025/2: transposition by 29 Jan 2027, application from 30 Jan 2027 | F-02 |
| SII templates | ITS (EU) 2023/894 (repealed 2015/2450); template set per frequency held as data | F-03, REQ-DAT-104 |
| SII template families named | S.05.01 (premiums-claims-expenses), S.05.02 / S.04.05 (by country), S.19.01 (non-life triangles) | REQ-DAT-105, 106, 108 |
| Written premium of record (D5) | Sum of net `ChargeDeltaEmitted` for premium, surcharge and discount categories, net of reversals, **excluding tax, levy and fee**, by booking date. POL's all-category total is "written charges" and is never reported as written premium | BR-DAT-001, REQ-DAT-042 |
| Earned premium | Taken from FIN's earned balances (REQ-FIN-155), never recomputed | REQ-DAT-105 |
| Mart reconciliation tolerance | Default €1 absolute per data point and 0.01% relative per template; regime may override | BR-DAT-005 |
| Reconciliation statuses | Reconciled / ReconciledWithExplainedDifferences / Unreconciled (examples: €0.40 within €1 → Reconciled; €12,000 → Unreconciled) | REQ-DAT-112 |
| Ceded lines | Ceded amounts use the underlying direct line, never accepted-RI lines | BR-DAT-008, REQ-DAT-098 |
| Treaty type → RI line | Owned by DAT (CD-08); e.g. motor XoL → non-proportional category of the direct line | REQ-DAT-097, BR-DAT-009 |
| Counterparty default inputs | Per reinsurer: LEI, rating, CQS input, recoverables, collateral, funds withheld (DR 2015/35 Art. 192 per contract) | REQ-DAT-110 |
| **SCR results intake** | Risk-function result file with module and sub-module figures, input run ids, method/parameter version, preparer and approver. Validated for completeness against the template set's SCR data points; maker-checker acceptance; included in the package with lineage to the file hash. Reject reason SCR_DATA_POINT_MISSING | REQ-DAT-300 |
| Data-point package (XBRL hand-off) | Template-neutral, machine-readable: taxonomy codes and version, units, period, entity identifier, SCR results, manifest with hashes; one package per taxonomy version; manifest hash = stored inputs hash | REQ-DAT-116 |
| **A.1004/2024** (amends ΠΟΛ.1033/2014; Law 4987/2022 Art. 15) | Annual residential property insurance data for ENFIA. Population = residential property policies in force **at any time** in the reference year. Fields: policyholder tax number and name, policy number, cover start and end, covered risks (earthquake, fire, flood), insured capital covering those risks, property address (region, municipality, street, postcode), floor, area, construction year, policy's unique registration number | F-04, REQ-DAT-125/126, BR-DAT-010 |
| A.1004 deadline | **10 January** of the following year (Decision A.1185/2024; PRD-11 K-09, R-85), held in CMP clock `CMP_A1004_RETURN`. Content lead time 5 business days; dry-run ≥ 8 weeks before. The prompt's "25 January" and a "28 February" snippet are rejected | C-02, F-05, REQ-DAT-125, cfg `dat.a1004.*` |
| A.1004 completeness rules | Tax number valid via `IdValidator`; floor, area and construction year present; gaps → WRK activities | REQ-DAT-127 (Should P2) |
| **Annual IPT report** | Transaction-level, reported due **31 March**. Line: policy number, fiscal document series and number, fiscal document date, insured, tax registration number, class of business, premium, IPT rate, IPT. IPT from `TaxCalculator.treatment` on the charge (D2). Fiscal identity only from CMP (D3), never BIL "ειδοποίηση πληρωμής". Lines without a registered fiscal doc are flagged and excluded from the total. Reconciled to FIN's four quarterly IPT returns | REQ-DAT-129–131, BR-DAT-011 (all **Should**; legal basis UNVERIFIED) |
| Bank of Greece statistics | ECB Regulation (EU) 1374/2014 (ECB/2014/50); reuses SII facts plus pack add-ons; reconciled per national statistical class; class 10 (motor vehicle liability) for MTPL | F-07, REQ-DAT-120–122 |
| Receivables ageing | By bucket from BIL for SII/BoG returns | REQ-DAT-123 |
| Cross-border | Premiums and claims by host State, class and basis (SII Art. 159) | REQ-DAT-124 (Should P4) |
| EAEE | Motor first (property P2): premium production, policies and vehicles, claim counts and amounts, reserves, commissions, expenses, by association classes, in the pack layout; aggregates only, minimum cell 10 | REQ-DAT-133 (Should), 134 (Must), BR-DAT-012 |
| Information Centre lag | Time from motor POL event to CMP `BureauEventSubmitted`; breaches `BureauLagExceeded` | REQ-DAT-135, KPI ICLAG |
| Fairfax USD | FIN's three-currency amounts and group rate type only; never recomputed; reconciled to FIN group pack (REQ-FIN-210) before sending, otherwise blocked; one feed per stamp | REQ-DAT-138–142, BR-DAT-013 |
| IRRD | Recovery-plan indicators and resolution datasets as pack-defined extracts (Directive (EU) 2025/1, Verify) | REQ-DAT-303 (Should) |
| IFRS 17 | PAA (para. 53 verified); result types IBNR, RA, DISCOUNT_UNWIND, DISCOUNT_RATE_CHANGE, ULAE, LOSS_COMPONENT, SII_BEST_ESTIMATE, SII_RISK_MARGIN, RI_HELD_RA | REQ-DAT-148, 150 |
| POG | Product KPIs to PFC under DR 2017/2358 Art. 7 | REQ-DAT-166, 231 |
| Equal treatment | Gender-neutral pricing (CJEU C-236/09, Dir. 2004/113/EC); features with protected characteristics or listed proxies (name-derived gender/ethnicity, language preference, nationality, fine-grained location) refused for pricing/UW/claims models | REQ-DAT-193, BR-DAT-022 |
| Bias band | Disparate-impact ratio 0.8–1.25; breach when the 95% CI lies wholly outside; minimum sample 500 per group | BR-DAT-018 |
| Drift | PSI warning > 0.10, breach > 0.25 | BR-DAT-019 |
| GDPR erasure propagation | ≤ 10 calendar days from task receipt (Art. 12(3)) | NFR-DAT-013 |
| CDC retention | 35 days | REQ-DAT-056 |
| DQ evidence retention | 10 years | REQ-DAT-254 |
| Large-loss threshold | Default €500,000 motor | BR-DAT-016 |

### 9.2 Referenced but not specified (gaps)
- SII Directive and DR article numbers: UNVERIFIED (OI-DAT-07). REQ-DAT-107 cites "Art. 76–86 (UNVERIFIED article range)".
- Taxonomy 2.10.0 template set and validation rules for a Greek non-life solo undertaking: not specified (OI-DAT-01). They are deferred to regime data, and **no template list is enumerated**.
- BoG reporting deadlines and national templates beyond ECB add-ons: OI-DAT-01 / OI-DAT-03.
- A.1004 file layout and field formats: OI-DAT-02 (AADE spec / FEK B' 134/2024 not retrieved).
- Legal basis and format of the IPT annual report: OI-DAT-04, merged into OI-FIN-04.
- EAEE classes and layout: OI-DAT-05.
- Fairfax file format and calendar: OI-DAT-12.
- IFRS 17 disclosure paragraph numbers (para. 130 etc.): OI-DAT-08.
- IRRD Greek transposition: Verify.
- AI Act standalone high-risk obligations from 2 Dec 2027 (Reg. 2026/1744): "no design change".
- Motor nat-cat mandatory scope: not asserted (Law 5116/2024 Art. 5 as amended by 5162/2024 Art. 25 via PRD-04). Motor cat exposure stays Should (F-12, OI-DAT-10 closed).
- Solvency II statutory retention for supervisory records: RC-DAT-MART "10 y after period" is a proposal only.

### 9.3 Deferred to configuration / country pack
- Taxonomy applicability table (`dat.taxonomy.applicability`).
- Template set per frequency and data-point rules: region:EU layer per taxonomy version.
- Regimes active per entity: `dat.mart.regimes_active`. Greece = SII, BOG_STAT, A1004 (P2), EAEE, GROUP_USD; **IPT_ANNUAL off** until OI-FIN-04.
- Tolerances; A.1004 lead times; minimum cell; national class lists; statutory return layouts (`StatutoryDataReturnFormat`); deadlines (`StatutoryClockSet` / CMP clocks); proxy list (`PricingConstraint`); retention durations; IRRD content and frequency; hazard schemes.

---

## 10. Greek-market specifics

- **AFM / tax numbers.** A.1004 and IPT marts carry P2 tax identifiers. They need a purpose grant `STATUTORY_TAX` (REQ-DAT-132), with vault resolution only for that build (REQ-DAT-263), audited with purpose and row count (REQ-DAT-268). Validity is checked via `IdValidator` (REQ-DAT-127).
- **myDATA / fiscal documents.** DAT consumes `FiscalDocRegistered` (MARK plus fiscal series and number) and `FiscalDocRejected` from CMP. CMP is the only fiscal issuer (D3). DAT computes the fiscal-document rejection rate KPI FDREJ (REQ-DAT-136) and uses fiscal identity on IPT lines (REQ-DAT-130). DAT does not talk to AADE directly; all AADE output goes via CMP.
- **AADE returns.** A.1004 (P2) and the annual IPT report (pack-optional) are handed to CMP for submission.
- **gov.gr:** not referenced in DAT.
- **Information Centre (bureau).** Lag KPI only (REQ-DAT-135); submissions are CMP's.
- **EAEE:** market-practice statistics feed (REQ-DAT-133/134); market benchmarks as reference data (REQ-DAT-065, Could).
- **Bank of Greece:** statistical returns via CMP (REQ-DAT-120–124).
- **ENFIA:** context for A.1004.
- **Greek language.**
  - Every label EN/GR; † marks working translations (§6.0).
  - The KPI catalogue has Greek names (e.g. Εγγεγραμμένα ασφάλιστρα, Δεδουλευμένα ασφάλιστρα, Δείκτης ζημιών).
  - Catalogue search is accent-insensitive (example "ασφαλιστρα" → Written premium, REQ-DAT-260).
  - Certification needs both EN and GR descriptions (§7.1.1).
  - Generative quality is monitored for Greek vs English parity (REQ-DAT-221/222).
  - The AI-DAT-07 privacy notice is given in Greek and English.
  - A translation completeness gate applies to all DAT UI strings (§6.0).
- **EUR / USD.** Money shown with currency; group views in USD with rate type. FX from PLT reference data, bitemporal (REQ-DAT-058). Tolerances in EUR.
- **Fairfax:** owning group; USD feeds on the group calendar from the MKT group pack (REQ-DAT-066).

---

## 11. Controls (§12)

**Authority types registered with PLT (REQ-PLT-003):**
- `DAT_MART_SIGNOFF` (legal entity, regime)
- `DAT_MART_ADJUSTMENT` (amount, regime)
- `DAT_MODEL_APPROVAL` (AI class, module)
- `DAT_PURPOSE_GRANT` (data class, purpose)
- `DAT_ACCEPTED_DIFFERENCE` (amount)

**Maker-checker list (R-64):**
- taxonomy transform and treaty-line mapping activation (REQ-DAT-100);
- mart adjustments (114) and mart sign-off (115, BR-DAT-006);
- SCR results acceptance (300);
- acceptance of reconciliation differences;
- model version approval (202);
- monitoring threshold changes (214);
- bias-breach justification acceptance;
- purpose grants to P2/P3 and to the identity vault (264);
- breaking data-contract versions;
- synthetic data release;
- certified KPI definition changes;
- plan version approval (230).

**Segregation of duties:**
- model developer ≠ validator ≠ approver;
- mart preparer ≠ signer;
- DAT result-set preparer ≠ FIN approver (REQ-FIN-275, a different ROLE-35);
- purpose-grant requester ≠ approver;
- DPO approval required for P3 grants;
- stewards cannot grant themselves access;
- SRE break-glass to bronze is time-limited and reviewed;
- SCR: acceptor ≠ preparer and approver ≠ acceptor.

**Audit (REQ-PLT-002).** Audited actions:
- contract changes, quarantine release, replay;
- transform and mapping activation;
- mart start, adjust, sign-off, export, resubmission;
- lineage downloads;
- actuarial attestations, submissions, supersessions;
- model lifecycle events; threshold changes; breach acknowledgements and closures;
- grant decisions; every P2/P3 read and vault resolution (REQ-DAT-268); exports (REQ-DAT-237);
- erasure and restriction executions; synthetic releases.

**GDPR handling (§5.20–5.21):**
- Columns are classified P0–P3 from producer contracts; deploying an unclassified column fails (REQ-DAT-262; CCR-DAT-04 makes field classification mandatory in schemas).
- Direct identifiers are tokenised in silver and gold with keyed consistent pseudonyms; resolvable values live only in the identity vault (263).
- Purpose grants are needed for P2/P3 (264). Masking and row filters apply on every access path, including AI agents (265).
- P3 is excluded except in coded form in the reserving and fraud datasets (266).
- Quasi-identifiers are generalised and cells < 10 suppressed (267, BR-DAT-024).
- Purges run daily per RC code (269).
- DSAR export, erasure and restriction (270–275):
  - crypto-shredding of per-subject keys in bronze (070, 271);
  - restriction instead of erasure under legal hold or retention (272; e.g. OPEN_CLAIM_RETENTION);
  - figures of submitted marts stay reproducible after erasure (273);
  - models trained on erased subjects are flagged (274, Should).
- Consent withdrawal is honoured (277). A DPIA register entry is kept per high-risk processing (278).
- Non-prod gets only tokenised or synthetic data (282, BR-DAT-026), with privacy-leakage tests on synthetic data (281).
- EU-only residency (068).
- Staff-monitoring boundary: there are no per-user metrics anywhere (REQ-DAT-223, 238, BR-DAT-021).

---

## 12. AI features (§11)

**Works-without-AI (§11).** DAT is fully functional with AI off. The registry and monitoring service (REQ-DAT-005) is **not** an AI feature. It is deterministic governance that must exist before any module enables AI.

| ID | Name | Class | Profile | Toggle default | MVP need |
|---|---|---|---|---|---|
| AI-DAT-01 | Conversational analytics (NL → semantic-layer query; no row data to the model) | MIN | MP-F (query error > 3%) | off; tenant/entity/role | Not needed (REQ-DAT-234 Should) |
| AI-DAT-02 | Regulatory figure pre-validation and variance narrative | MIN | MP-C | off; entity/role | Not needed (no REQ; fallback REQ-DAT-085) |
| AI-DAT-03 | DQ anomaly detection (warnings only) | MIN | MP-E | off; entity | Not needed (REQ-DAT-252 Should) |
| AI-DAT-04 | Mapping and crosswalk suggestion (accepted only in PFC/MKT) | MIN | MP-C | off; tenant | Not needed (REQ-DAT-103 Could) |
| AI-DAT-05 | Catalogue docs and lineage explainer | MIN | MP-C | off; tenant | Not needed (REQ-DAT-259 Could) |
| AI-DAT-06 | Reserving diagnostics assistant | MIN (elevated documentation) | MP-C + confirmation rate < 50% | off; entity/role | Not needed (REQ-DAT-157 Should) |
| AI-DAT-07 | Bias proxy estimation (name-script + regional composition; sealed env; aggregates only) | **HR** (uncertain); DPIA mandatory; DPO approval to enable | MP-D adapted (calibration error > 5 pts → auto-disable) | off; tenant | Not strictly. Fallback = regional-composition proxies only. Note REQ-DAT-217 (Must P1) requires privacy-preserving proxy estimation, which can be met without AI-DAT-07 |
| AI-DAT-08 | Synthetic data generator (generative, inside the prod sealed env) | MIN | MP-C + privacy pass rate; auto-disable | off; tenant | Not needed (REQ-DAT-285 Could; rule-based generator REQ-DAT-280 is Must) |

**Programme registry (§11.3).**
- 119 features: 111 from other modules plus DAT's 8. 42 have high-risk controls.
- Each is assigned a profile from MP-A…MP-H with defaults:
  - MP-A: PSI 0.10 / 0.25; performance drop > 10%; override change > 15 pts; annual revalidation.
  - MP-B: factual error > 2%, harmful > 0.5%; review sample ≥ 200/month.
- Default breach response: a bias breach auto-disables HR / LR+HR features and flags others. Drift → flag, escalating to auto-disable after two consecutive breach windows for HR.
- MVP impact: the **registry and monitoring service itself is Must P1 and blocking**. G-4 says 100% of AI features in production must be governed; REQ-DAT-209 blocks enablement via PLT. It is needed whenever any module turns on any AI feature.

---

## 13. Open issues / assumptions / CCRs (§16)

| ID | Issue | Status |
|---|---|---|
| OI-DAT-01 | Taxonomy 2.10.0 template set and validation rules for a Greek non-life solo; BoG deadlines | Open (ROLE-32) |
| OI-DAT-02 | A.1004 file layout and formats (deadline closed: 10 Jan) | Open (ROLE-26) |
| OI-DAT-03 | BoG national templates; ECB regulation amendments | Open |
| OI-DAT-04 | IPT annual report legal basis and format → merged into OI-FIN-04. If confirmed, REQ-DAT-129…131 become Must | Open |
| OI-DAT-05 | EAEE classes and layout | Open |
| OI-DAT-06 | Retention durations; submission-evidence erasure exemption | Closed (XMR-F-329 / XMR-D-259) |
| OI-DAT-07 | Verify SII article numbers | Open |
| OI-DAT-08 | Verify IFRS 17 paragraph numbers | Open |
| OI-DAT-09 | AI Omnibus Reg. 2026/1744 | Closed |
| OI-DAT-10 | Motor nat-cat floor | Closed (R-85) |
| OI-DAT-11 | XBRL renderer ownership | Closed (D10: CMP in-house) |
| OI-DAT-12 | Fairfax file format and calendar | Open (ROLE-24) |
| OI-DAT-13 | Lawful basis and DPIA for AI-DAT-07 | Open (ROLE-30) |
| OI-DAT-14 | AI-CMP features | Closed |
| OI-DAT-15 | AI-CLM-05 / 08 split | Closed |

**Assumptions:**
- A-01 volumes: 1.2 m in-force; 2 m transactions/yr; 150,000 claims/yr; ~60 m events/yr, design 180 m, ≥ 2,000 events/s (D8).
- A-02 one lakehouse per stamp.
- A-03 FIN publishes reconciled balances at lock.
- A-04 producers publish complete events incl. correlation keys.
- A-05 XBRL rendering and filing are CMP's.
- A-06 standard-formula SCR by the risk function, no internal model.
- A-07 cat vendors accept pseudonymised exposure.

**Risks:** RK-DAT-01 two taxonomies at go-live; 02 unverified Greek formats; 03 erasure vs reproducibility; 04 proxy-inference sensitivity; 05 producer event completeness; 06 cost of bitemporal history at 180 m events/yr; 07 missing monitoring labels.

**CCRs:** CCR-DAT-01…08, all Accepted:
- R-59: 7 new events.
- R-64: maker-checker list.
- R-65: ROLE-47 Executive.
- R-66: mandatory P0–P3 field classification in the schema registry.
- R-67: monitoring profile and breach response in every AI feature.
- R-68: `StatutoryDataReturnFormat` SPI.
- R-69: DAT cross-module entities, plus T2 RPO/RTO for signed-off runs, registry and contracts.

**"Ten decisions before build" (§16.5).**
- Closed by D10: #1 (lakehouse format) and #4 (XBRL).
- Still open: #2 events-first + CDC backstop; #3 bitemporal standard and "as reported" snapshot design; #5 taxonomy transition plan incl. legacy sourcing of annual 2026; #6 erasure design with the DPO; #7 monitoring profiles, thresholds and breach responses; #8 proxy-inference method vs regional proxies only; #9 actuarial tooling environment (language/libraries); #10 cost budgets and quotas.

---

## 14. Conflicts and ambiguities

### (a) With the binding infra / stack (ADR overrides)

1. **Lakehouse vs "no lakehouse"; this is the central conflict.**
   - The ADR explicitly excludes "data lakehouses". It says: "Reporting and regulatory marts are PostgreSQL schemas built by scheduled jobs … A read replica is added when reporting load requires it" (ADR §1, §4).
   - INFRASTRUCTURE.md §2 gives DAT the schemas `rpt_raw`, `rpt_conformed`, `rpt_mart`, with Hangfire jobs "Nightly mart builds, reconciliations" and Blob "Report exports". §9 says dashboards are "Built into the React app over `rpt_mart`".
   - The PRD instead mandates "open-format columnar tables with transactional table semantics … on object storage … without a vendor lakehouse product" (REQ-DAT-067, Must) and "open-source, self-operated open table format" (§7.0, §16.5 #1). The system contract D10 says the same ("Open-source, self-operated event stream, workflow engine and lakehouse table format"). The ADR overrides both.
   - **Proposed reading:** bronze → `rpt_raw`, silver → `rpt_conformed`, gold → `rpt_mart`, all PostgreSQL. REQ-DAT-067 is then re-interpreted as "transactional tables (PostgreSQL MVCC)". The object-storage and open-format wording is dropped.
   - Requirements needing rewording: REQ-DAT-067, 068 ("lakehouse storage, compute"), 079 (file compaction / snapshot expiry has no meaning in PostgreSQL; becomes VACUUM, partition maintenance), 074 (partitioning: OK with PostgreSQL declarative partitioning), 289 ("restore the lakehouse … by replaying bronze").
   - The terms "lakehouse", "bronze/silver/gold" and "lakehouse privacy" appear throughout §1.7, §5, §7 and the screen names (SCR-DAT-17 "Lakehouse privacy console"). Events `LakehouseErasureCompleted` and the task names are contract-level and are presumably kept as names.
2. **Zones with no infra home.** None of these has a schema in infra §2:
   - CDC reconciliation zone (`cdc.*`);
   - identity vault (`vault.*`);
   - feature store (offline `fs.*` and an **online key-value store** at ≤ 50 ms p95, 200 req/s; REQ-DAT-192, NFR-DAT-008);
   - legacy zone (`legacy.*`);
   - sealed AI environment.

   The DAT **operational metadata schema** (registries, contracts, mart-run headers, grants, breaches) is also missing: infra lists only `rpt_*` for PRD-15, not a `dat` schema. A decision is needed. Options: add `dat` (metadata) plus `rpt_vault` / `rpt_cdc` / `rpt_legacy`, or fold them into the three `rpt_*` schemas. The vault in particular needs stricter grants than the rest of `rpt_*`.
3. **CDC on module databases vs module-boundary rule.**
   - ADR rule 8: a module "never reads another module's tables", checked by NetArchTest.
   - REQ-DAT-050–057 (all Must P1) require "read-only, log-based change-data-capture" from producer tables. The PRD limits this to tables the producer declares in its contract with source kind CDC (named exception XMR-D-103), but it is still a cross-schema read.
   - In a single PostgreSQL database this would be logical replication / `pg_logical` or direct reads. Either needs an explicit ADR exception, or a replacement: producers expose daily control-total data products (count and sum per entity) instead of CDC. The ADR §3 "nightly reconciliation jobs … policy ↔ billing ↔ sub-ledger ↔ myDATA" suggests the latter fits.
4. **"Topics", "partition key", "event stream", 30-day stream retention vs outbox and no broker.**
   - REQ-DAT-001 says "events published on the producing modules' topics (`<mod>.events.v<major>`)". §8 says "Topic `dat.events.v1`; partition key = …". NFR-DAT-006 says "Producers' 30-day stream retention plus archive; DAT outage never blocks producers".
   - The ADR has a transactional outbox dispatched in order to in-process handlers, with no broker. Topics and partition keys become outbox event-type naming and ordering keys.
   - The "30-day stream retention" must map to outbox / PLT event-archive retention. REQ-DAT-037/039 replay from the "PLT event archive" (REQ-PLT-145) is compatible if PLT keeps an archive table.
5. **"PLT workflow engine" vs Hangfire / no workflow server.** REQ-DAT-077 (Must) runs transformations "as declarative, versioned pipelines on the PLT workflow engine with dependencies, retries and run history visible in the PLT batch monitor". Under the ADR this becomes Hangfire jobs (continuations for dependencies) plus a DAT pipeline-run table; the batch monitor is the Hangfire dashboard and/or a PLT screen. "Dependents are skipped, not run on stale data" must then be coded by hand.
6. **Who generates regulatory output files.**
   - INFRASTRUCTURE.md §9: "Regulatory outputs (myDATA, Solvency II, IPT, A.1004, EAEE) — Generated in C# from `rpt_mart`, validated against XSD / XBRL taxonomy". This does not say which module.
   - PRD-15 (D10, REQ-DAT-116, OI-DAT-11) puts XBRL rendering and taxonomy validation in **CMP**, while A.1004/EAEE/BoG files are built by DAT through `StatutoryDataReturnFormat` (REQ-DAT-128) and handed to CMP for submission.
   - The infra line is consistent only if read as "CMP's C# code reads the DAT package". Confirm the package hand-off is via DOC archive / Blob rather than CMP reading `rpt_mart` directly; the latter would breach rule 8.
7. **Freshness: "nightly mart builds" (infra) vs ≤ 2 min p95 event-to-gold (PRD).** REQ-DAT-078, NFR-DAT-002 and G-6 require operational marts within 2 minutes. That needs incremental outbox-driven handlers writing to `rpt_conformed` / `rpt_mart`, not just nightly Hangfire builds. This is feasible in-process but heavier than infra implies, and it loads the primary database: the infra §8 read-replica trigger becomes likely early.
8. **ML / training / feature store / actuarial workspace language.**
   - REQ-DAT-149 needs a "governed actuarial workspace in the lakehouse where actuaries run reserving and measurement code".
   - REQ-DAT-200 needs reproducible training pipelines; REQ-DAT-212 needs embedding drift; REQ-DAT-191/192 need point-in-time training sets and online serving.
   - §16.5 #9 says core DAT is C#, but the "actuarial workspace language is an analytical-tooling choice". This implies Python/R notebooks, which the ADR does not list.
   - The ADR "Minimal dependencies" rule and "AI feature approved → Azure OpenAI" (infra §8) do not cover model training or hosting of predictive models (fraud scoring AI-CLM-04 etc.). A decision is needed on where predictive models are trained and served.
9. **Object-storage backups and immutable retention (REQ-DAT-289)** vs PostgreSQL backups plus Blob. This works if mart-run packages go to immutable Blob via DOC.
10. **Crypto-shredding with per-subject keys in bronze (REQ-DAT-070, NFR-DAT-012)** at 60–180 m events/yr. Infra provides Key Vault and `pgcrypto` (PTY). Per-subject keys at that scale need a key table encrypted under a Key Vault master key, which PLT REQ-PLT-240/241 must define. Not specified in infra.
11. **Volume in PostgreSQL.** NFR-DAT-010: about 0.6 bn silver fact rows over 10 years at base volume (1.8 bn at design). In a single General Purpose PostgreSQL alongside OLTP this needs partitioning and archiving strategy, and probably the read replica. The ADR's "ample" claim needs testing against this NFR.
12. **Vendor reference.** Guidewire Cloud Data Access is cited as "vendor practice (reference only, CD-20)" (F-10). This is harmless.

### (b) With the system contract / other PRDs
- Contract D10 / CD-20 itself prescribes a self-operated lakehouse, event stream and workflow engine. The ADR supersedes these; the contract is stale on this point.
- Contract §3.1 architecture context says "core language Kotlin or .NET". D10 fixed .NET, so it is consistent with the ADR.
- REQ-DAT-003's anchor Family includes Must requirements that are P2/P3/P4 (REQ-DAT-102 P4, 109 P3, 125/126/128 P2). §5 says "an anchor's MVP scope is its Must family", which would wrongly pull P2–P4 items into MVP. Read the MVP scope as Must ∩ P1.
- REQ-DAT-008 anchor is **Should P1**, but its Family lists REQ-DAT-180, 181, 183, 186, which are **Must P2**. This is consistent with "becomes Must for P2" but confusing.
- REQ-DAT-111 (Must P1) promises nat-cat data "when the P2 data set (REQ-DAT-186) is active". This is fine as conditional.
- REQ-DAT-109 (RI programme inputs from REQ-RI-231) is **P3**, but REQ-DAT-097/098 (treaty-type mapping, ceded lines) are Must P1, and P1 motor is XoL-only. The P1 SII reinsurers'-share figures (S.05.01 "reinsurers' share") must then come from cession facts (REQ-RI-228) without the programme product. Plausible, but confirm the P1 template set needs no outgoing-RI programme template (S.30.x-type) inputs. This is not stated.
- `PeriodReopened` is consumed (§8.3), but no requirement says what happens to signed-off mart runs if FIN reopens a period. **Gap.**
- `ReconciliationBreakRaised` (FIN) is consumed with no defined reaction.
- §8.1 `ActuarialResultsPublished` lists FIN as consumer, while §15.1 says "REQ-FIN-001 (consumes `ActuarialResultsPublished`)" against our ID REQ-DAT-003 (the regulatory marts anchor). This looks like a cross-reference slip; it should be REQ-DAT-004.
- Infra lists PRD-15 external integration "Group reporting" only. The PRD also has cat-model and hazard providers (P2), all via the "PLT adapter host" (REQ-PLT-006), which corresponds to the infra "integration worker".

### (c) Internal contradictions / inconsistencies
- §1.5 lists the annual IPT report as "Optional per pack" and REQ-DAT-129–131 are **Should**. Yet the anchor REQ-DAT-003 (Must) text includes "the annual transaction-level IPT report where the pack requires it", and REQ-DAT-132 (purpose-grant restriction for IPT/A.1004 marts) is **Must P1**. This is consistent only with "if enabled". The Greece config default is IPT_ANNUAL **off**.
- §9.3 says "Planned P1 (IPT)" for AADE, although it is off by default pending OI-FIN-04.
- REQ-DAT-063 (cat-model exchange through the adapter host) is Must **P2**, while REQ-DAT-184 (export) is Should P2. Minor.
- REQ-DAT-102 (Cyprus stub runs in CI) is Must **P4**, but the §10.4 text and ADR rule 10 ("A Cyprus stub pack runs in CI") imply CI from the start.
- The language-switch requirement (§6.0, "Must, P1") has no REQ ID, so it is not counted in the 283.
- The §1.2 AI count says "All 119 AI features declared in PRD-01…PRD-17 (including CMP's seven, MIG's eight and DAT's own eight)". The §11.3 table is consistent with this; not independently recounted.
- NFR-DAT-004 puts signed-off mart runs, registry, contracts and bronze at RPO 15 min / RTO 4 h (T2). CCR-DAT-08 lists "signed-off mart runs, registry and data contracts" at T2 and does not mention bronze.
- §1.1 lists the DPO as owner role for "lakehouse privacy" (terminology only).

### (d) Cannot be built without a decision
1. Physical mapping of bronze / silver / gold / CDC / vault / feature store / legacy / sealed env onto PostgreSQL schemas (`rpt_raw`, `rpt_conformed`, `rpt_mart` plus missing ones), and where DAT's metadata schema lives.
2. CDC backstop: allow a cross-schema read exception, or replace it with producer control-total data products.
3. ML runtime for training, online feature serving and actuarial code: .NET only, or a sanctioned Python sidecar container. This affects REQ-DAT-149, 191, 192, 200, 212 and AI-DAT-03/06/07/08.
4. Taxonomy 2.10.0 and 2.8.x template sets and data-point rules (OI-DAT-01). Nothing in the PRD enumerates the data points, so transforms cannot be authored until the EIOPA DPM is loaded as regime data.
5. SCR result-file layout with ROLE-48 per taxonomy version (§9.3).
6. A.1004 file layout (OI-DAT-02); BoG templates (OI-DAT-03); EAEE layout (OI-DAT-05); Fairfax format and calendar (OI-DAT-12); IPT legal basis (OI-FIN-04).
7. Per-subject key management design for crypto-shredding at event scale.
8. Proxy-inference method and DPIA for AI-DAT-07 (OI-DAT-13); otherwise regional proxies only.
9. Behaviour on `PeriodReopened` after mart sign-off.

---

## 15. Build notes

**Hardest parts:**
1. **The bitemporal conformed layer with correction sets** (REQ-DAT-002, 040–044, 080–091, 299; BR-DAT-001/002):
   - half-open valid and record intervals;
   - late-arrival buffers keyed by `set_id/set_size/index` with a 30-minute window;
   - GROSS vs NET delta modes yielding identical totals;
   - "as known at" reproducibility.

   Property-based invariants (10,000 generated cases per release) are Must (REQ-DAT-091). PostgreSQL `tstzrange` with exclusion constraints (`btree_gist`, already used by POL) fits well.
2. **Mart engine with versioned transforms, two taxonomies in parallel, crosswalk splits, reconciliation to FIN to the cent, maker-checker sign-off and "as reported" freeze**, with byte-identical regeneration within 4 h (NFR-DAT-014). Transforms are data (rules per data point) plus golden fixtures (REQ-DAT-092, 099).
3. **Lineage from figure to event ids** (REQ-DAT-247/248). Automatic column-level lineage "from pipeline code" is non-trivial in hand-written C#/SQL. Expect an explicit lineage-annotation convention in the transform framework.
4. **Privacy layer:** tokenisation, vault, purpose grants, query-time masking on every access path, crypto-shredding, restriction vs erasure, and reproducibility after erasure.
5. **Model registry and monitoring for 119 features:** PSI, disparate-impact with CIs, generative sampled review workflow, and the gating API `dat.Coverage.status` that PLT calls before enablement.

**Must exist first:**
- PLT outbox and event archive with replay (REQ-PLT-005/145) and schema registry with field classification (R-66).
- PLT maker-checker (REQ-PLT-004), audit (REQ-PLT-002), ABAC (REQ-PLT-001), retention and key services (REQ-PLT-237/240/241).
- WRK activities (REQ-WRK-001).
- Producers emitting contract-conformant events, especially POL `ChargeDeltaEmitted` with completeness fields.
- FIN `PeriodClosed` plus reconciled balances (REQ-FIN-155) and the actuarial result API (REQ-FIN-009, 273–279).
- PFC regulatory mapping (REQ-PFC-005/145) and MKT regime code lists and crosswalks (REQ-MKT-207/208).

**Suggested slicing (P1, in order):**
1. **Foundation:** DAT metadata schema; data-contract registry; ingestion handler on the outbox (idempotent on `event_id`, sequence ordering, gap detection, quarantine); `rpt_raw` landing; replay from the PLT archive.
2. **Bitemporal `rpt_conformed`:** PolicyTermView, SegmentView, ChargeView (correction sets, delta modes), ClaimView, ReserveLineView, JournalLineView, PartyView (tokenised); `dat.Query.asOf`; property tests.
3. **Model registry plus coverage gate** (REQ-DAT-197–209). This is early because PLT and every module's AI enablement depend on it, even with zero AI on.
4. **Written-premium KPI and GWP reconciliation to FIN** (BR-DAT-001, REQ-DAT-301), followed by KPI catalogue and dashboards (operational, ≤ 2 min).
5. **SII mart for motor:** regime definitions as data, S.05.01 / S.05.02 / S.19.01 inputs, transforms 2.8.x and 2.10.0, reconciliation to FIN, adjustments, sign-off, package export, SCR intake, `RegulatoryMartPublished`. Then BoG statistics, EAEE, Fairfax USD.
6. **Actuarial supply:** triangles, PAA inputs, input-set reconciliation, return path to FIN.
7. **DQ / lineage / catalogue / privacy console / DSAR operations.**
8. **AI monitoring computations** (profiles MP-A…H, bias tests).
9. **Coexistence and legacy zone** for migration scenario B (REQ-DAT-294–298).

Deferred to P2: A.1004, cat accumulation, cat-model exchange.
