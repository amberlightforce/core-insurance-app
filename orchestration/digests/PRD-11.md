# PRD-11 digest — Regulatory Compliance and Reporting (CMP)

Source: `core-insurance-prds/PRD-11-regulatory-compliance-reporting.md`, v1.1, 2026-10-07, 2,106 lines, read in full. Binding input cited by the PRD: `00-system-contract.md` v1.11. Checked against `core-insurance-infra/ARCHITECTURE-DECISIONS.md`.

Conventions used in this digest: **STATED** = the PRD states the value or rule as fact (it may still carry its own UNVERIFIED flag, which I repeat). **GAP** = the PRD names or relies on the rule but gives no value or specification. **PACK** = the PRD sends the value to country-pack or configuration data.

---

## 1. Identity

- **Module code:** CMP. **Title:** Regulatory compliance and reporting. **Schema:** `cmp` (§7).
- **Owner roles (§1.1):**
  - ROLE-29 compliance officer: business owner.
  - ROLE-32 Greek regulatory analyst: owns the clock register and the regulatory-change backlog.
  - ROLE-30 DPO: owns DSARs and the disclosure log.
  - ROLE-26 tax specialist: owns the fiscal rules.
  - Lead architect: design authority.
- **Purpose (§1.2).** CMP connects the core's facts to state systems and proves compliance. It owns 12 capability areas:
  - the fiscal-document channel (AADE myDATA);
  - the motor bureau adapter (Information Centre);
  - the annual and periodic returns that DAT does not file (A.1004, ENFIA confirmations, EAEE feeds);
  - the programme-wide statutory clock register and engine;
  - complaints management;
  - DSAR orchestration and the disclosure log;
  - the obligations register with traceability to every PRD's requirements;
  - the regulatory-change backlog;
  - the AI system register, which gates AI enablement in PLT;
  - the regulatory submission tracker, including in-house XBRL rendering and EIOPA taxonomy validation (D10).
- **Five key decisions (§1.2):**
  1. Fiscal registration is asynchronous and never blocks bind.
  2. There is one clock engine, one register and one value source (MKT `StatutoryClockSet`). Values are frozen at start.
  3. The Information Centre adapter is a canonical fact stream with an isolated transport. A manual file is used at go-live.
  4. DSARs run as a fan-out saga with retention-aware outcomes.
  5. The obligations register is the traceability spine, and the AI register is a hard gate.
- **Non-goals and out of scope (§1.4). Each item belongs to another module:**

| Out of scope | Owner |
|---|---|
| Business events that trigger filings | POL, BIL, CLM |
| Charge computation, IPT and levy amounts | RAT (`REQ-RAT-009`), MKT `TaxCalculator` |
| IPT and Auxiliary Fund levy returns and their accounting | FIN (`REQ-FIN-005`). CMP only tracks the submission (REQ-CMP-008) and runs the clocks (REQ-CMP-003) |
| Report generation, regulatory marts, data-point package, Solvency II QRT content, SCR intake | DAT (`REQ-DAT-003`, `REQ-DAT-116`). CMP renders and validates the XBRL in-house (D10) |
| Execution of redress payments | BIL (`REQ-BIL-009`, D1). CMP decides and requests |
| Document rendering and delivery proof | DOC |
| Workflow engine, timers, business-day arithmetic, holidays | PLT (`REQ-PLT-007`, `REQ-PLT-009`) |
| DORA incident tooling, timers, ICT register of information | PLT (`REQ-PLT-012`). CMP tracks submissions and holds the obligation |
| Activities, queues | WRK |
| Clock durations and calendars as data | MKT (`REQ-MKT-009`) |
| Sanctions screening | PTY (`REQ-PTY-006`, CD-12). CMP consumes the evidence |
| AI toggles, kill switch, model gateway; model registry | PLT (`REQ-PLT-010`); DAT (`REQ-DAT-005`) |
| Retention engine, legal hold, cryptographic erasure | PLT (`REQ-PLT-011`) |
| Customer-facing complaint and DSAR entry points | CHN (`REQ-CHN-149`, `REQ-CHN-150`) |

- **CMP never computes a tax, rate or report value.** See §1.4, AC-02, A-4, and self-check §16.6: "CMP computes no tax".

## 2. Size metrics

| Metric | Count | Source |
|---|---|---|
| Functional requirements | **245** (9 anchors `REQ-CMP-001…009` + 236 module requirements `REQ-CMP-030…265`) | §5.13, §16.6 |
| By MoSCoW | Must **209**, Should 32, Could 4 | §5.13 |
| By tag | [BASELINE] 209, [ENHANCEMENT] 36 | §5.13 |
| **Must and P1 (Motor MVP)** | **196** (stated in §16.6; my recount agrees) | §16.6 |
| All P1 requirements | 225. Non-P1: P2 = 13 (REQ-CMP-177…189), P3 = 3 (064, 065, 067), P4 = 4 (091, 194, 205, 236) | my count from §5 |
| Business rules | 56 (`BR-CMP-001…056`) | §10.1 |
| NFRs | 26 (`NFR-CMP-001…026`) | §14 |
| Screens | 11 (SCR-CMP-01…11: ten staff screens plus the compliance home) | §6 |
| Owned UI-library items | 4 (`UIL-K1…K4`). 10 consumed items are referenced | §6.12 |
| Owned entities | about 30 (§7.1 table rows, several paired) | §7.1 |
| Events produced | 30 event names: 13 base contract events + 17 added by CCR-CMP-01/R-59. The base set (FiscalDocRegistered/Rejected, BureauEventSubmitted, BureauLagExceeded, Clock×5, ComplaintReceived/Answered, DSARReceived/Completed) is listed in §8.1 | §8.1 |
| Events consumed | about 45 event names across 22 rows | §8.2 |
| Inbound API operation groups | 27 rows in §9.1 | §9.1 |
| External integrations | 7 (§9.3) | §9.3 |
| AI features owned | 7 (AI-CMP-01…07), all default off. The register seeds 119 programme features | §11 |
| Statutory clock codes registered | 42 (41 Active, 1 Draft) | §10.5 |
| Open issues | 15 open, 9 closed | §16.1, §16.6 |

**Size estimate: L.** There are 196 Must P1 requirements, which is well above the 100 threshold. The temporal and money-adjacent logic is heavy: a durable clock engine with about 2 M concurrent instances, DEADLINE and WAITING_PERIOD semantics, frozen values, catch-up after outage, and business-day arithmetic. The module also includes an idempotent fiscal channel with duplicate checks, an in-house XBRL renderer and validator, and a 15-module DSAR saga.

## 3. Owned entities (§7.1) and state machines (§7.3)

Every row carries `legal_entity_id`, `jurisdiction`, `created_at`, `created_by` and `record_version`, plus `valid_from`/`valid_to` where noted. Intervals are half-open and stored in UTC (XMR-FR-150). Ids are UUIDv7. Business numbers come from `REQ-PLT-014`.

| Entity | Key attributes | Constraints | PII | Retention |
|---|---|---|---|---|
| **FiscalDocument** | source_type, source_id, role (ISSUE/CREDIT/CANCELLATION), idempotency_key (unique), document_type (pack code), series, number, issue_date, counterparty_party_id, counterparty_snapshot (json: tax id, tax office, name, address), totals, currency, status, mark, uid, authentication_code, qr_url, correlated_mark, channel (DIRECT/EINVOICE_PROVIDER/NOT_REQUIRED), provisional_flag, schema_version | one per idempotency key; lines sum to totals | P2 | RC-CMP-FISCAL (value UNVERIFIED) |
| FiscalDocumentLine | line_no, fiscal_category_key, net_amount, vat_category, vat_exemption_code, other_tax_category, other_tax_amount, income_classification, e3_code, charge_id/payment_id | — | P0 | as parent |
| FiscalSubmission (renamed `FiscalTransmission` at the next major schema version, XMR-F-126) | document ids[], attempt_no, sent_at, transport_status, response_ref, outcome per doc, duplicate_check_result, charge_delta_set_id/set_size | append-only | P0 | RC-CMP-FISCAL |
| FiscalRejection | cause_group, codes[], messages[], opened/resolved, resolution, activity_id | one open per document | P0 | |
| EInvoiceSubmission | provider_ref, provider_status, delivered_at | | P0 | |
| FiscalReconciliationRun / Break | break type MISSING/MISMATCH/ORPHAN/DUPLICATE | | P0 | |
| **BureauEvent** (insured-vehicle fact) | policy_id, term_no, transaction_id, vehicle_ref, plate, vin, fact_type, cover_from/to, effective_at, green_card_no, origin (LIVE/MIGRATION), supersedes_fact_id, status, lag_clock_id | effective order per vehicle | P1 | RC-CMP-BUREAU: 7 years after deregistration or expiry |
| BureauSubmission | transport (REAL_TIME_API/BATCH_FILE/MANUAL_FILE), file_ref, bureau_refs | | P1 | |
| BureauReconciliationBreak | types MISSING_AT_AUTHORITY, EXTRA_AT_AUTHORITY, MISSING_IN_CMP, PERIOD_MISMATCH, HANDOVER_OVERLAP, HANDOVER_GAP | | P1 | |
| **StatutoryClockDef** | code, names GR/EN, basis_type, legal_source, owner_module, subject_type, start/stop/pause defs, cancel reasons, consequence, warning participant role, jurisdictions, recalculation flag, mode (ENGINE/MIRROR), motor_path flag, legal_value_status per jurisdiction (Settled/Unverified), status, version, valid_from/to | maker-checker | P0 | RC-CMP-REGISTER |
| **ClockInstance** | clock_code, def_version, subject_type/id, jurisdiction, variant, started_at, deadline_at, frozen_value (json), value_version, calendar_version, status, met_at, days_late, outcome, causation_event_id, idempotency_key; late_emission flag | one Running per code and subject unless the definition allows more | P0 | RC-CMP-CLOCK |
| ClockSuspension | paused_at, resumed_at, reason | non-overlapping | P0 | |
| **Complaint** | number, received_at, channel, complainant/representative party ids, complainant_type, description + language, category, product_line, cause, vulnerable_flag (no reason), status, ack/reply clock ids, outcome, remedy, redress, redress_route (BIL_CREDIT_REFUND/CLM_EX_GRATIA/CMP_REDRESS), redress_request_ref, redress_status, root_cause, adr_body/reference, closed_reason, previous_complaint_id | | P1 (P3 possible) | RC-CMP-COMPLAINT: default 5 years after closure, UNVERIFIED |
| ComplaintLink, ComplaintAction | | | | |
| **DsarRequest** | number, type, scope, channel, subject_party_ids[], identity_assurance, received_at, clock_id, extension, status, outcome, response_document_id | | P2 | RC-CMP-DSAR: 3 years, UNVERIFIED |
| DsarTask | module, operation, status, due_at, attempts, result_ref, per-item outcomes | one per module + operation | P1 | |
| DsarItemDecision | disclose/redact/withhold + reason | | P1 | |
| **DisclosureLog** | subject, recipient, recipient_type, purpose, legal_basis, data_categories[], disclosed_at, transfer_mechanism, source module/ref | append-only | P1 | |
| **AnnualReturn** / Line | return_type (A1004/ENFIA_CONFIRMATIONS/EAEE_FEED), reference_period, status, record_count, control_totals, file_ref, manifest_hash, receipt_ref, amended_from | one active per type + period | P2 | RC-CMP-RETURN |
| EnfiaConfirmation | taxpayer_afm, property_id (ΑΤΑΚ), declared_policy_no, matched_policy_id, proposal, decision, reason, cover_days, transmitted_at, ack_ref | | P2 | |
| SubmissionDefinition / **RegulatorySubmission** | type, authority, frequency, clock_code, source module, roles, format version; period, status, content_ref+hash, taxonomy_version, xbrl_instance_ref+hash, validation report, approvals, filing ref, receipt, response | approver ≠ preparer | P0 | |
| **TaxonomyPackage** | authority (EIOPA/BoG), version, applicable reference periods, package_ref+hash, status, approver | maker-checker; two or more may be Active | P0 | |
| Obligation, ObligationLink (target_type REQ/BR/NFR/CLOCK/PACK_RULE/CONTROL; target_moscow, target_phase) | | target must exist | P0 | |
| Control / ControlTest, **Evidence** (immutable, hash), Finding | | | P0/P1 | RC-CMP-EVIDENCE: 10 years, UNVERIFIED |
| RegulatoryChange / ImpactAssessment | | | P0 | |
| **AiSystem** | system of record for feature identity, AI Act class, data classes and status (R-82) | class ≠ Prohibited | P0 | |

**State machines:**

- **FiscalDocument (§7.3.1).**
  - Transitions: Pending → Submitted → Registered | Rejected. Pending → Rejected on a local schema failure. Submitted → Pending on a technical failure, only after a negative duplicate check. Rejected → Pending through correction (REQ-CMP-049). Registered → Cancelled, only where the pack allows. Rejected → Cancelled when the source is withdrawn (four-eyes).
  - Pending has sub-states Queued (circuit open) and Provisional (outage mode).
- **BureauEvent (§7.3.2).** Pending → Submitted → Acknowledged. Submitted → Rejected → Pending. Any non-final state → Superseded.
- **ClockInstance (§7.3.3, R-78).**
  - Running ⇄ Paused. Running/Warned → Warned on each threshold.
  - Running/Warned → Met on an early stop, with outcome Met, Cured, Exercised or Withdrawn.
  - Running/Warned → **Breached** applies only to DEADLINE clocks. Running/Warned → **Elapsed** applies only to WAITING_PERIOD clocks.
  - Breached → Breached records a late completion: sub-state MetLate, and `ClockMet` is published with a late flag.
  - Running, Paused or Warned → Cancelled.
- **Complaint (§7.3.4).**
  - Received → Acknowledged → UnderInvestigation → Answered → Closed. Answered → Escalated → Closed.
  - Guards: Answered requires an outcome and an approved reply. Closed requires a root cause for upheld outcomes, and redress status Paid (or a recorded reason) where redress was awarded.
- **DsarRequest (§7.3.5).** Received → IdentityPending → InProgress → InReview → Completed. Extended is a sub-state of InProgress. Any state → Refused or Withdrawn.
- **AnnualReturn (§7.3.6).** Draft → Validated → Approved → Submitted → Accepted | Rejected. Rejected → Draft. Accepted → Amended, which creates a new linked return.
- **RegulatorySubmission (§7.3.7).** Planned → AwaitingContent → InReview → Approved → Filed → Accepted | Rejected. Rejected → InReview. Filed → Superseded. Approval is blocked while any blocking taxonomy or source validation error remains.
- **RegulatoryChange (§7.3.8).** New → Assessing → Assessed → Planned → Implemented → Verified → Closed. New or Assessing → NotApplicable.
- **AiSystem (§7.3.9).** Draft → InReview → Approved. InReview → Draft. Approved → Suspended → Approved or Retired. Approved → InReview on a material change.
- **DsarTask states (REQ-CMP-155):** Pending, InProgress, Completed, Failed, NotApplicable, Overdue.

## 4. Consumed entities / dependencies (§7.2, §9.2.1, §15.1)

| From | What | How |
|---|---|---|
| PTY | Party, identifiers (AFM verified `REQ-PTY-003`), DOY (`REQ-PTY-059`), address, merged ids (`REQ-PTY-007`), vulnerability (`REQ-PTY-011`), consent pack (`REQ-PTY-160`), screening evidence (`REQ-PTY-181`), processing purposes (`REQ-PTY-151`), DSAR ops (`REQ-PTY-161…165`) | `pty.Party.get/search`, `pty.Dsar.*`; `PartyTaxView` read model refreshed on `PartyUpdated` and `IdentifierVerified` |
| POL | Policy/term/vehicle as-of the transaction (`REQ-POL-002`, `REQ-POL-315`), Green Card (`REQ-POL-185`) | `pol.Policy.get(validAt/knownAt)`, `pol.Policy.search`; `MotorCoverView` built from POL events |
| BIL | Fiscal sources (`REQ-BIL-096…101`, `-263`), refunds (`RefundDisbursed`), disbursements | inbound `cmp.FiscalDocument.request`; outbound `bil.Disbursement.request` (CMP_REDRESS), credit or refund request |
| CLM | Claim payment fiscal sources (`REQ-CLM-138`), complaint hand-off (`REQ-CLM-187`), ex-gratia (`clm.Redress.request`, `REQ-CLM-265`) | API and events |
| PFC | Fiscal-category key per charge type (`REQ-PFC-121`), charge-type catalogue (`REQ-PFC-004`), coverage-grain regulatory mapping (`REQ-PFC-005`), motor flag (`REQ-PFC-001`), POG (`REQ-PFC-007`, `-156`) | §10.3 hooks |
| DOC | Letters, packages, delivery proof (`REQ-DOC-045`, `-253`, `-249`), archive (`REQ-DOC-004`), holding of MARK-bearing receipts (`REQ-DOC-213`) | `doc.Document.request`, `doc.Delivery.status/evidenceFor` |
| WRK | Activities, queues, notifications, deterministic redaction flags (`REQ-WRK-373/374`) | `wrk.Activity.create/complete` |
| PLT | Workflow/timers/calendars (`REQ-PLT-007`, `-168`, `-197`), adapter host (`-006`), exchange archive (`-156`), incidents (`-012`), numbering (`-014`), approvals/authority/audit (`-004/-003/-002`), retention (`-011`), DORA (`-303/-305/-315`), third-party register (`-316`), processing inventory (`-244`) | in-process calls |
| MKT | `StatutoryClockSet` values, SPIs, capability switches, rule index (`REQ-MKT-148`), certification gate (`REQ-MKT-255/256/290`), language (`REQ-MKT-005`) | SPI bindings |
| DAT | Marts, data-point package and SCR results (`REQ-DAT-116`), A.1004 file via `StatutoryDataReturnFormat` (`REQ-DAT-125/128`), model registry (`REQ-DAT-005`), DSAR ops (`REQ-DAT-009/270/275`) | API and events |
| FIN | TaxReturn/LevyReturn (`REQ-FIN-180/187/192`) | events + clock stop |
| MIG | Import contract (`REQ-MIG-001`), coexistence routing (`mig.Routing.resolve`, `REQ-MIG-149`), hand-overs (`REQ-MIG-004`, `-122`, `-124`, `-117`, `-130`, `-162`, `-163`) | API and events |

## 5. Events

**Produced (§8.1).** Topic name `cmp.events.v1`. Payloads carry ids only, never P2 or P3 values.

| Event | Trigger | Key payload | Consumers |
|---|---|---|---|
| FiscalDocRegistered | Registered | source type/id, doc type, MARK, UID, QR ref, correlated MARK | POL, BIL, CLM, FIN, DOC, WRK, DAT, MIG |
| FiscalDocRejected | Rejected | source, cause group, codes | same |
| FiscalDocCancelled | Registered → Cancelled | cancellation MARK, reason | BIL, FIN, DAT |
| BureauEventSubmitted | fact submitted / ack update | policy, term, fact type, vehicle ref, submission id, status | POL, DAT, MIG |
| BureauLagExceeded | lag clock breached (once per fact and threshold) | policy, fact, lag, threshold | POL, WRK, DAT, MIG |
| BureauFactRejected | authority rejection | fact, codes | POL, DAT |
| ClockStarted/Warned/Breached/Met/Elapsed | instance transitions | code, subject, deadline, threshold, late flag | POL, BIL, CLM, DOC, CHN, WRK, DAT |
| ClockPaused/Resumed/Cancelled | transitions | code, subject, new deadline | DAT |
| ComplaintReceived / ComplaintAnswered | transitions | complaint, links, clocks, outcome | PTY, WRK, DAT |
| ComplaintAcknowledged / Escalated / Closed | transitions | ADR body | DAT |
| DSARReceived / DSARCompleted | transitions | request, type, subject ids, scope, deadline / outcome | PTY, RAT, DOC, WRK, PLT, DAT |
| DSARExtended / DSARTaskOverdue | | new deadline / module, task | DAT |
| RegulatorySubmissionFiled / Rejected | | type, period, receipt | DAT |
| AnnualReturnSubmitted | | type, period, receipt | DAT |
| ObligationChanged / ControlTestFailed | | | DAT |
| RegulatoryChangeAssessed | | impacts | DAT |
| AiSystemStatusChanged | register status change | old/new status, class | PLT (disables a Suspended or Retired feature within 60 s, D4), DAT |

**Consumed (§8.2).**

| Event | Producer | Reaction |
|---|---|---|
| PolicyBound, PolicyIssued, PolicyChanged, PolicyCancelled, PolicyVoided, PolicyReinstated, PolicyRewritten, RenewalBound, PolicyLapsed, PolicyNonRenewed, TransactionReversed/Reapplied | POL | Derive insured-vehicle facts (motor) |
| PolicyMoved | POL | Recompute facts if the policy number changes |
| TransactionReversed/Reapplied with `set_id`, `set_size`, `index` | POL | Hold members until the set is complete (D4) |
| RefundDisbursed | BIL | Stop `POL_REFUND_DUE` |
| DocumentDelivered / DeliveryFailed | DOC | Stop complaint and DSAR clocks |
| StatutoryOfferIssued | CLM | Stop `CLM_MTPL_OFFER` where CLM does not call stop |
| ClaimsHistoryCertificateIssued | CLM | Stop `CLM_HISTORY_STATEMENT` |
| TaxReturnFiled | FIN | Record filing; this does **not** stop `FIN_IPT_RETURN` |
| RegulatoryMartPublished | DAT | Attach content; trigger XBRL rendering |
| IncidentDeclared / Classified / Reported | PLT | Mirror DORA clocks; attach GDPR-breach facts |
| DisbursementIssued / Cleared / Rejected / Stopped | BIL | Track redress status; reopen the task on Rejected or Stopped |
| AiToggleChanged / AiKillSwitchActivated | PLT | Update the AI register |
| ModelVersionRegistered / Approved, ModelDriftDetected, BiasThresholdBreached | DAT | Update the register; open a review |
| ConfigurationActivated / PackActivated / PackRolledBack | MKT | Apply clock value changes; channel bindings |
| ConfigChanged | PLT | Refresh cache |
| PartyUpdated / PartiesMerged / PartyUnmerged / IdentifierVerified, ConsentChanged | PTY | Refresh tax view; re-point records; objection evidence |
| ActivityCompleted | WRK | Complete actions and tests |
| RetentionPurgeCompleted | PLT | Evidence |
| MigrationBatchLoaded, CoexistenceMasterChanged, MigrationWaveStatusChanged | MIG | Bureau hand-over and routing cache |
| WithdrawalRequestReceived | CHN | Withdrawal evidence (REQ-CMP-253) |
| LakehouseErasureCompleted | DAT | Audit evidence only, never a completion path (R-97) |

Explicitly **not consumed:** `EvidencePackSealed`, `RefusalDocumentIssued`, `StatutoryOfferDue`/`Breached`.

## 6. APIs

**Exposed (§9.1).** All are "in-process; REST under `/api/cmp/v1/`". Idempotency-Key is required on commands. Many operations support dry-run.

| Group | Operations |
|---|---|
| Fiscal documents | `cmp.FiscalDocument.request`, `get`, `listBySource`, `qrPayload`, `resubmit`, `bulkResubmit`, `correct`, `cancel` |
| Fiscal reconciliation and certification | `cmp.FiscalReconciliation.run/breaks`; `cmp.FiscalRule.certify` (MARK certification rule, called by MKT) |
| Bureau | `cmp.Bureau.status`, `retransmit`, `manualFile.generate`, `manualFile.recordResponse`, `reconcile`, `handover` |
| Clocks | `cmp.Clock.start/pause/resume/stop/cancel/extend/get/query/import`; `cmp.ClockDef.list/get/propose` |
| Complaints | `cmp.Complaint.create/classify/link/acknowledge/addAction/submitReply/approveReply/escalate/close/get/query/procedureText/import` |
| DSARs and disclosures | `cmp.Dsar.create/verifyIdentity/startFanOut/retryTask/decideItem/approve/extend/refuse/get/query/import`; CMP's own `cmp.Dsar.export/restrict/erase`; `cmp.DisclosureLog.record/query` |
| Returns | `cmp.AnnualReturn.generate/validate/approve/submit/recordReceipt/amend`; `cmp.EnfiaConfirmation.import/propose/decide/transmit` |
| Submissions and taxonomies | `cmp.Submission.create/attachContent/comment/approve/recordFiling/recordResponse/evidencePack/renderXbrl/validateTaxonomy`; `cmp.Taxonomy.import/activate/list` |
| Registers | `cmp.Obligation.*`, `cmp.Control.*`, `cmp.Evidence.submit`, `cmp.Traceability.export`, `cmp.RegulatoryChange.*`, `cmp.AiSystem.register/submitForReview/approve/suspend/retire/get/list` |

- **Error codes:** CMP-ERR-FISCAL-TOTAL, -SOURCE-UNKNOWN, -IDEMPOTENCY-MISMATCH, -NOT-FOUND, -NOT-REJECTED, -APPROVAL-REQUIRED, -TRANSPORT-UNAVAILABLE, -CLOCK-UNKNOWN, -CLOCK-NO-VALUE, -CLOCK-STATE, -CLOCK-CANCEL-REASON, -EXTENSION-NOT-ALLOWED, -NOT-A-COMPLAINT, -RULES-MISSING, -SOD, -IDENTITY-NOT-VERIFIED, -HELD, -VALIDATION-BLOCKING, -APPROVAL-SAME-USER, -TAXONOMY-UNKNOWN, -DATAPOINT-UNMAPPED, -REQ-UNKNOWN, -AI-PROHIBITED, -AI-CHECKLIST-INCOMPLETE.
- **Consumed:** see §4. The DSAR fan-out map (§9.2.2) has 17 rows:
  - 15 modules provide data-subject operations: PTY, RAT (export only; erasure blocked by retention), UW, POL (no erase, restrict only), BIL, CLM, RI (restrict only), FIN (erasure refused because of books retention), DOC, CHN, WRK, PLT (cryptographic erasure), CMP, DAT and MIG.
  - PFC and MKT are not applicable (R-62).
- **External (§9.3):**

| System | Detail | Status |
|---|---|---|
| AADE myDATA REST | XML per AADE XSD, "user-id and subscription-key headers" | P1; field behaviour UNVERIFIED |
| Certified e-invoicing provider | REST, behind a switch | |
| Information Centre | transport unknown | manual file at P1 |
| AADE secure file upload (A.1004) | | P2 |
| myPROPERTY (ENFIA) | | P2; UNVERIFIED |
| EAEE statistics | file | P1; format UNVERIFIED |
| Bank of Greece reporting channels | via DAT packages and PLT `IncidentReportingChannel` | manual filing with receipt is acceptable at P1; UNVERIFIED |

## 7. SPIs / country-pack interfaces (§10.4)

- **SPIs used:** `FiscalDocumentChannel` (incl. `.series`, `.cancel`), `EInvoiceProvider`, `BureauAdapter` (`report`, `reconcile`), `StatutoryClockSet` (`mkt.StatutoryClockSet.get(code, jurisdiction, startDate)`), `ComplaintRules` (`rules(jurisdiction, complainantType, channel, date)`), `TaxReturnFormat`, `StatutoryDataReturnFormat` (`REQ-MKT-326`), `NumberingScheme`, `HolidayCalendarProvider` (via PLT), `IdValidator` (scheme `VEHICLE_PLATE` with operation `normalise`; AFM check digit), `StatutoryDeliveryRule` (via DOC), `IncidentReportingChannel` (via PLT).
- **Core default:** `cmp.fiscal.channel_binding` = NotRequired.
- **Cyprus stub (deliberately different):**
  - fiscal: NotRequired;
  - bureau: a different file layout and a 72 h synthetic threshold;
  - complaints: 45 calendar days, Financial Ombudsman;
  - returns: none (no A.1004, no ENFIA, REQ-CMP-194);
  - EIOPA taxonomy only;
  - clock values: synthetic (§10.5 last column).
  - REQ-CMP-091 and REQ-CMP-194 are Must P4.

## 8. Screens (§6)

All screens follow the shared conventions in §6.0:
- Greek/English switch ("ΕΛ | EN") with no reload and the page `lang` attribute updated (R-101, Must P1, NFR-CMP-026 ≤ 1 s).
- Semantic status system. Clock states map to default, warning, breached, success, info and read-only.
- AI output is labelled `AI-suggested` and becomes `AI-generated` once accepted.
- Edit-in-place is used only for free-text notes.
- 24 permissions `cmp.*`.

| ID | Name | Personas | One line |
|---|---|---|---|
| SCR-CMP-01 | Obligations register and traceability view | ROLE-29, ROLE-32, auditors, supervisor (evidence room) | Split pane; tabs Requirements/Controls/Evidence/Clocks/Pack rules/History; relationship graph; met-by-phase computation |
| SCR-CMP-02 | Statutory clock dashboard | compliance, analyst, team leads | Hero metric, exception-first list of Warned and Breached instances, Definitions tab, MI, calendar dry-run (non-prod) |
| SCR-CMP-03 | Fiscal document status and rejection queue (`UIL-K3`) | ROLE-25 fiscal, ROLE-26, ROLE-24 | KPI tiles, rejections by cause, transport log, reconciliation, AI explainer |
| SCR-CMP-04 | Information Centre feed monitor and reconciliation | ROLE-04 bureau, compliance | Flow strip, lag histogram, manual files, hand-over tabs |
| SCR-CMP-05 | Complaints case management (`UIL-K1`) | ROLE-31, compliance, DPO | Queue by deadline; record with clock countdowns; reply draft |
| SCR-CMP-06 | DSAR handling and disclosure log (`UIL-K4`) | DPO team | Fan-out tracker, item-level redaction, disclosure log, records of processing |
| SCR-CMP-07 | Annual property return and ENFIA confirmations | ROLE-26 | P2 |
| SCR-CMP-08 | Regulatory report review and submission tracker (`UIL-K2`) | compliance, finance controller, actuaries, ICT risk | Calendar, XBRL render and validate, pinned comments, approval, filing |
| SCR-CMP-09 | Regulatory change backlog | ROLE-32 | Board plus horizon timeline |
| SCR-CMP-10 | AI system register | compliance, feature owners | Filters; governance checklist |
| SCR-CMP-11 | Compliance home | compliance roles | `IB-11 Work-left home` cards |

Design-guide patterns referenced: IB-01, 02, 03, 04, 05, 06 (as a non-AI fan-out tracker), 07, 08, 09, 10, 11, 12, 14, 15, 16, 17, 20, 21, 24, 26, 28, 29, 32.

## 9. Regulatory, tax and statutory rules

### 9.1 Rules STATED explicitly (with IDs)

**myDATA / fiscal (OBL-TAX; F-01; BR-CMP-001, -003)**

- **Document types (BR-CMP-001, Greece pack, L3).** The PRD treats these as market guidance (S-01, the informal AADE–EAEE group, 16.01.2023), held as pack data and confirmed by the tax adviser (OI-CMP-03).

| Case | Document type |
|---|---|
| Issue to a business | 2.1 (category A1) |
| Issue to an individual | 11.2 (category A2) |
| Credit to a business | 5.1 (correlated) or 5.2 (uncorrelated) |
| Credit to an individual | 11.4 |
| Claim settlement receipt, B2C | 13.1 / 13.2 (taken from CLM BR-CLM-027) |
| Claim settlement, B2B | self-billing 2.1 |
| Commission | self-billing per FIN mapping |

- **Line classifications (BR-CMP-003, REQ-CMP-035):**
  - income classification 1.3;
  - VAT category 7 (0%), exemption under Art. 22 Law 2859/2000;
  - premium-tax "other-tax category" per IPT charge type, taken from the pack mapping;
  - E3 code 561_001 for B2B. The B2C E3 code is open (561_003 vs 563_003, OI-CMP-04).
  - Classifications come only from the pack mapping of the PFC fiscal-category key, never from core constants.
- **B2B vs B2C (REQ-CMP-036, BR-CMP-002).** Business = an organisation, or a person flagged professional, holding a verified AFM at the issue date. Business documents carry a snapshot of AFM, DOY, legal name and address (REQ-CMP-037).
- **Idempotency key (BR-CMP-004).** Source type + source id + role + revision.
- **Sole issuer (BR-CMP-050, REQ-CMP-038, D3).** CMP is the only issuer of fiscal series and numbers. Numbering is gap-free per series and branch through PLT numbering. BIL invoices are non-fiscal "ειδοποίηση πληρωμής", never "τιμολόγιο".
- **MARK timing (BR-CMP-051, REQ-CMP-260, D3).** One certification rule: `cmp.fiscal.mark_before_issue` and `bil.fiscal.mark_before_delivery` must agree. In the Greece pack, motor uses the TRANSACTION trigger and premium receipts carry the MARK before delivery. Fiscal registration never blocks bind (BR-CMP-008). The cover note is non-fiscal and carries no amounts (`REQ-POL-176`).
- **Retries and parsing:**
  - business validation errors are never auto-retried (BR-CMP-005);
  - every response is parsed per document regardless of HTTP status (BR-CMP-006);
  - technical retries back off from 30 s, factor 2, max 30 min, jitter ±20%, with a duplicate check (`RequestTransmittedDocs`) before each resend (BR-CMP-007, REQ-CMP-045).
- **Batching and validation:**
  - micro-batch of 500 documents or 2 s, never above the channel limit (BR-CMP-009, Greece default);
  - local schema validation before transmission; failures get cause SCHEMA and no channel call (REQ-CMP-040);
  - zero-tolerance line-sum check (REQ-CMP-039).
- **Rejection ageing (BR-CMP-010).** Warning at 2 business days, escalation at 5 business days.
- **Reconciliation (BR-CMP-012).** Daily per entity and series. Breaks older than 2 business days go to the FIN close cockpit.
- **Cancellation (BR-CMP-011, `cmp.fiscal.cancellation_allowed` default false).** Channel cancellation only where the pack permits, with maker-checker.
- **Credits (BR-CMP-014).** Credits correlate to the original MARK where the pack requires it.

**E-invoicing (OBL-TAX, F-07)**

- Decision A.1128/2025 as amended by A.1044/2026 (FEK B 880/17.02.2026):
  - phase 1 (gross income > €1 million) from 2 March 2026;
  - phase 2 from 1 October 2026;
  - issuance only through a certified provider or AADE's free application;
  - B2C excluded.
- Applicability to VAT-exempt insurance documents is **UNVERIFIED** (OI-CMP-02).
- The `EInvoiceProvider` interface is Must P1 with core default `NotRequired` (D3). Routing (REQ-CMP-062/063) is a conditional Must.
- The switch `cap.cmp.einvoice_b2b` can only be turned on after an evidenced applicability decision (REQ-CMP-066, BR-CMP-013).

**Information Centre (OBL-MOT, Law 5113/2024 Art. 15; F-08)**

- **Content (stated):** plate, policy number, validity, every invalidity or stop-cover event, and Green Card numbers.
- **Retention:** seven years (REQ-CMP-085, BR-CMP-019).
- Method, format, channel and deadlines are set by a ministerial decision that has not been found (**GAP**, OI-CMP-01).
- Lag thresholds are **provisional**: 24 h warning and 48 h breach, UNVERIFIED (BR-CMP-017, REQ-CMP-082).
- A severity-2 incident opens when ≥ 1 fact breaches or the transport is down beyond the warning threshold (BR-CMP-018, REQ-CMP-084).
- The interim method is a manual file (XMR-D-265).
- No reporting for quotes or non-motor products. No retroactive cover reporting where the pack forbids it (REQ-CMP-071, BR-CMP-015).

**Statutory clock engine rules (BR-CMP-020…027, 049, 053, 055)**

- Values are frozen at start. A later change applies to running instances only if flagged `applies_to_running` (BR-CMP-020, REQ-CMP-098, REQ-CMP-109).
- **Fail closed** when no pack value exists (BR-CMP-021, REQ-CMP-099).
- Month arithmetic uses the same day number, else the last day of the month. Deadlines end at **23:59:59 local time** of the jurisdiction, Europe/Athens (BR-CMP-022, REQ-CMP-100).
- Units supported: CALENDAR_DAYS, BUSINESS_DAYS, WEEKS, MONTHS, FIXED_DATE, hours (implied by rows 25 and 27), and a roll-forward option (REQ-CMP-101).
- Default warnings at 50%, 80% and 3 business days before the deadline (BR-CMP-024, REQ-CMP-105).
- Paused time extends the deadline (BR-CMP-025). Extensions only where the value set permits (BR-CMP-026). Cancellation only with a listed reason (BR-CMP-027).
- A WAITING_PERIOD clock ends as Elapsed and is never a breach (BR-CMP-049, R-78).
- Catch-up after an outage: missed events are emitted once, in order, flagged late. Deadlines never move (BR-CMP-053, REQ-CMP-262).
- In production, a motor-path definition is Active only with a **Settled, certified** value (BR-CMP-055, REQ-CMP-264, D7).

**Statutory clock register (§10.5) — values as STATED for the Greece pack**

| # | Code | Kind | Greece value | Legal source | Status per PRD |
|---|---|---|---|---|---|
| 1 | POL_OBJECTION | WAIT | 1 month | Law 2496/1997 Art. 2 §5 | **UNVERIFIED** (OI-MKT-14) |
| 2 | POL_OBJECTION_INFO | WAIT | 14 days; hard stop 10 months after first premium | Art. 2 §6 | stated |
| 3 | POL_WITHDRAWAL_DISTANCE | WAIT | 14 calendar days; `TermsNotReceived` long stop 12 months + 14 days | Dir. 2023/2673; Law 5317/2026 Arts. 69–72 | verified (PRD-18 §12.4) |
| 4 | POL_WITHDRAWAL_LONGTERM | WAIT | 14 days | Law 2496/1997 Art. 8 (paragraph UNVERIFIED) | |
| 5 | POL_REFUND_DUE | DEADLINE | 30 calendar days | Law 2251/1994 Art. 3ιστ (distance variant verified); other variants UNVERIFIED | stop on `RefundDisbursed` |
| 6 | POL_RENEWAL_NOTICE | DEADLINE FIXED_DATE | expiry − pack lead | pack-defined | **UNVERIFIED** |
| 7 | POL_NONRENEWAL_NOTICE | DEADLINE FIXED_DATE | expiry − pack lead | pack-defined | **UNVERIFIED** |
| 8 | BIL_NONPAY_NOTICE | WAIT | **1 month from proven notification** (K-01) | Law 2496/1997 Art. 6 §2 | legal confirmation outstanding (OI-BIL-01) |
| 9 | BIL_AUXF_REMIT | DEADLINE | 15 days after each two-month period | Law 5113/2024 Art. 14 | peer-verified |
| 10 | CLM_MTPL_OFFER | DEADLINE | 3 months | Dir. 2009/103/EC Art. 22 | |
| 11 | CLM_MTPL_ASSESSMENT | DEADLINE | 15 days (Greece); 25 days (abroad) | Act 87/2016 Art. 5 | |
| 12 | CLM_MTPL_PAYMENT_DUE | DEADLINE | 10 days **from proven delivery of the offer** | Act 87/2016 Art. 6 | verified |
| 13 | CLM_FS_COUNTERPARTY_REPLY | WAIT | 10 business days, GR-BANK calendar | FS agreement | **UNVERIFIED** (OI-CLM-02, AC-09) |
| 14 | CLM_HISTORY_STATEMENT | DEADLINE | 15 days (EU layer) | Law 5113/2024 Art. 7; Reg. 2024/1855 | verified (K-06) |
| 15 | UW_NATCAT_RESPONSE | DEADLINE | 30 days; silence = refusal | Law 5116/2024; JMD 96806/2025 | **UNVERIFIED** (OI-UW-01) |
| 16 | UW_NONDISCLOSURE_ACTION | DEADLINE | 1 month | Law 2496/1997 Art. 3(3) | |
| 17 | UW_AGGRAVATION_ACTION | DEADLINE | 1 month | Art. 4(2) | |
| 18 | FIN_IPT_RETURN | DEADLINE | end of third month after quarter end; Met only when Filed **and** `TAX_REMITTANCE` cleared | Law 5177/2025 Art. 43 §6 | peer-verified |
| 19 | FIN_LEVY_RETURN | DEADLINE | not bound for the Auxiliary Fund in Greece (K-05) | pack-defined | |
| 20 | CMP_COMPLAINT_ACK | DEADLINE | entity standard of 5 business days *proposed* | Act 88/2016 (period not found) | **UNVERIFIED** |
| 21 | CMP_COMPLAINT_REPLY | DEADLINE | 50 calendar days; a holding response does not pause it | Act 88/2016 (secondary source) | primary text not fetched (OI-CMP-06) |
| 22 | CMP_SUPERVISOR_REFERRAL | DEADLINE | FIXED_DATE from the referral letter | as stated in referral | |
| 23 | CMP_COMPLAINT_STATS | DEADLINE | — | | **UNVERIFIED** (OI-CMP-10) |
| 24 | CMP_DSAR_RESPONSE | DEADLINE | 1 month, +2 months extension | GDPR Art. 12(3) | |
| 25 | CMP_GDPR_BREACH_NOTIFY | DEADLINE | 72 hours from awareness; CMP is the only timer | GDPR Art. 33 | |
| 26 | CMP_FISCAL_TRANSMISSION | DEADLINE | — | myDATA framework | **UNVERIFIED** (OI-CMP-03) |
| 27 | CMP_BUREAU_REPORT | DEADLINE | provisional 48 h breach / 24 h warning | Law 5113/2024 Art. 15 | **UNVERIFIED** |
| 28 | CMP_A1004_RETURN | DEADLINE | **10 January** of the following year, roll forward | A.1004/2024 amended by A.1185/2024 | K-09 |
| 29 | CMP_ENFIA_CONFIRM | DEADLINE | 28 February (leaflet typo "28.02.2025") | A.1014/2024 and successors | K-10, set yearly |
| 30 | CMP_EAEE_STATS | DEADLINE | — | market agreement | **UNVERIFIED** |
| 31 | CMP_SII_QRT_QUARTERLY | DEADLINE | 5 weeks (solo), effective-dated | Del. Reg. 2015/35 Art. 312 | verified for the 2026 cycle; may change from 30 Jan 2027 |
| 32 | CMP_SII_ANNUAL | DEADLINE | 14 weeks (solo) | same | verified 2026 |
| 33 | CMP_SII_SFCR | DEADLINE | 14 weeks (solo) | same | verified 2026 |
| 34 | CMP_SUPERVISOR_QUERY | DEADLINE | FIXED_DATE from the question | | |
| 35 | PLT_DORA_INITIAL | DEADLINE, MIRROR | classification + 4 h, capped at awareness + 24 h only when classification is within 24 h of awareness | Del. Reg. (EU) 2025/301 Art. 5 | MIRROR (PLT timer) |
| 36 | PLT_DORA_INTERMEDIATE | MIRROR | 72 h from initial notification | | |
| 37 | PLT_DORA_FINAL | MIRROR | 1 month from latest intermediate | | |
| 38 | PLT_DORA_ROI | MIRROR | — (reference date 31 December) | ITS 2024/2956; Law 5193/2025 | **UNVERIFIED** (OI-CMP-09) |
| 39 | CLM_REPAIR_IN_KIND | DEADLINE | 20 days | Act 87/2016 Art. 6 | verified |
| 40 | POL_INSURER_TERMINATION_NOTICE | WAIT | 15 days (non-disclosure and aggravation) | Law 2496/1997 Arts. 3–4 | **UNVERIFIED** (OI-UW-02) |
| 41 | UW_AMENDMENT_ACCEPTANCE | WAIT | 1 month | Art. 3 | **UNVERIFIED** |
| 42 | POL_MTPL_THIRDPARTY_NOTICE | WAIT | 16 days | P.D. 237/1986 Art. 11a | **UNVERIFIED**, **Draft**; starts are refused |

OI-CMP-17 lists rows #1, #5 (non-distance variants), #6, #7, #13, #15, #20, #23, #26, #27, #30, #38, #40, #41 and #42 as UNVERIFIED.

**Complaints (OBL-COMP)**

- Reasoned written reply within 50 calendar days, under BoG Executive Committee Act 88/5.4.2016 (FEK B 1109/19.04.2016). The source is secondary.
- Claim notifications are never complaints (BR-CMP-028, REQ-CMP-125). Only a human may decide "not a complaint".
- A published procedure is required (REQ-CMP-141).
- Deadlines come from `ComplaintRules` by complainant residence and entity home; the stricter applies (BR-CMP-029).
- If no rules exist, the system fails closed: the strictest entity-level value applies and a task is raised (REQ-CMP-127).
- The handler must not be the person who made the decision complained about (BR-CMP-030).
- Second-user approval is needed for rejected outcomes or redress above €500, the entity default (BR-CMP-031).
- Answered complaints auto-close after 90 days without escalation (BR-CMP-033).
- Root cause is required for upheld complaints (BR-CMP-032).
- Redress is routed by type (BR-CMP-052, D1):
  - premium-related → BIL credit or refund;
  - claim-related → CLM ex-gratia;
  - anything else → BIL `CMP_REDRESS` disbursement.
- Categories (REQ-CMP-128) follow EIOPA-BoS-12/069 and are pack-configurable code lists.

**GDPR / DSAR (OBL-GDPR)**

- One month, extendable by two months, with notice within the first month (BR-CMP-034, REQ-CMP-166).
- The 72 h breach clock is owned by CMP (BR-CMP-039, REQ-CMP-174, D7).
- Erasure becomes restriction where retention applies (BR-CMP-036; Art. 17(3)(b),(e)).
- The DPO approves every package. P3 items go only through the DPO workflow (BR-CMP-037, REQ-CMP-175).
- Portal requests inherit the session assurance level when it is at least "substantial". Staff-registered requests need identity evidence (BR-CMP-038).
- **SLA table (REQ-CMP-263, BR-CMP-056):**
  - defaults: export and restriction 1 business day, erasure 5 business days;
  - deviations: PLT export 1 hour, DAT erasure 10 calendar days, MIG 2 business days;
  - each task is due at the earlier of its SLA and 5 business days before the DSAR deadline;
  - a map change whose SLA cannot fit inside the statutory month is refused.
- Refusal for manifestly unfounded requests includes the HDPA complaint route (REQ-CMP-165).

**Returns (P2)**

- **A.1004/2024 + A.1185/2024 fields (F-09, REQ-CMP-179):**
  - AFM and name, policy number, start and end, covered risks, insured capital;
  - address: prefecture, municipality, street and number, postcode;
  - floor, area, construction year, unique policy registration number;
  - optional previous-year registration number.
- Scope: individuals' residential policies covering earthquake, fire or flood (REQ-CMP-178).
- Deadline 10 January (BR-CMP-040). Upload through a secure web file service.
- DAT builds the file. CMP checks it, approves it and submits it.
- **ENFIA (F-10, BR-CMP-041):**
  - owners apply on myPROPERTY by 16.02.2026 (ΑΤΑΚ);
  - the insurer confirms matches for earthquake, fire and flood cover with **cover days ≥ 90**;
  - the authority's reduction is 20% where taxable value ≤ €500,000, 10% above, pro rata for 3–12 months. These figures are stated for context; CMP does not compute them.

**Regulatory submissions**

- Maker-checker with approver ≠ preparer (BR-CMP-042).
- No approval while blocking validations or open comments remain (BR-CMP-043).
- **Taxonomies (BR-CMP-054, REQ-CMP-258):**
  - EIOPA 2.8.2 for Q4 and annual 2026; 2.10.0 from the Q1 2027 reference period;
  - both versions are live in H1 2027, so two must run in parallel;
  - output format is xBRL-XML or xBRL-CSV, as the taxonomy prescribes (REQ-CMP-256);
  - BoG national checks "where the pack defines them".
- DORA: Regulation (EU) 2022/2554 and Greek Law 5193/2025, with the Bank of Greece as competent authority. CMP tracks; PLT drafts and times (REQ-CMP-203, REQ-CMP-204).

**AI Act (OBL-AIA).** Regulation 2024/1689 as amended by the Digital Omnibus, Regulation (EU) 2026/1744:
- Art. 50 applies from 2 August 2026;
- Annex III obligations apply no later than 2 December 2027;
- the Art. 50(2) marking concession runs to 2 December 2026 for systems already on the market.

Prohibited AI is refused. High-risk-controls entries need a complete governance checklist. Customer-facing entries need approved GR/EN transparency text. The reviewer must differ from the owner (BR-CMP-046). Reviews are quarterly for high-risk entries and annual for others (BR-CMP-047).

**Regulatory change.** Changes are assessed within 10 business days of intake, and pack activation must come before the effective date (BR-CMP-048).

### 9.2 Rules REFERENCED but NOT specified (gaps)

- **IPT rate(s):** **not stated.**
  - CMP never derives a rate (§1.4, F-02, AC-02).
  - The only rates in the document are the **pre-reform** 2023 guide figures: fire 20%, life 4%, other classes 15%, exempt 0%. F-02 says explicitly that these pre-date Law 5177/2025 Art. 43. **Do not use them.**
  - The current IPT rate and base are a GAP for the RAT/MKT `TaxCalculator`.
  - The PRD states only the IPT return cadence: quarterly, by the end of the third month after the quarter.
- **Premium-tax "other-tax category" codes after Law 5177/2025:** GAP (OI-CMP-04).
- **Auxiliary Fund contribution rate and base:** **not stated.** Only the remittance deadline is given: 15 days after each two-month period (Law 5113/2024 Art. 14). There is also no stated rate for the stamp duty, the fire brigade levy, or any other levy.
- **Annual IPT detail report** `FIN_IPT_ANNUAL_DETAIL` (31 March per `REQ-DAT-129`): not registered, because there is no legal basis yet (OI-FIN-04, CON-035).
- **myDATA API specifics:** all UNVERIFIED (AC-01, F-06, OI-CMP-03). The XSD has not been downloaded because AADE PDFs refuse automated fetch. Unverified items:
  - the two header names (user-id and subscription-key);
  - the 5,000-documents-per-call limit;
  - the 40-character UID, authentication code and QR URL;
  - business errors inside HTTP 200;
  - the ERP API version (v1.0.9, 2024; providers v2.0.1, March 2026; an open-source client uses v2.0.2; a move to 2.x is expected in 2026);
  - the transmission deadline;
  - the B2C E3 code;
  - the outage procedure (REQ-CMP-058, "where the pack defines one").
  - The operation names in J-01 (`SendInvoices`, `RequestTransmittedDocs`) are design assumptions.
- **Information Centre:** ministerial decision on method, format, channel and deadlines; operator name; any monthly onward transmission (AC-08, AC-10, OI-CMP-01). The real lag thresholds are also unknown.
- **E-invoicing applicability** to type 2.1 VAT-exempt premium documents (OI-CMP-02).
- **Complaints:** acknowledgement period; holding-response rules; Greek ADR bodies competent for insurance (the Hellenic Financial Ombudsman appears not to cover insurance; the Consumer Ombudsman does); the periodic complaint statistics return and its format (OI-CMP-06, OI-CMP-10). The primary text of Act 88/2016 has not been fetched.
- **BoG Acts** 60/2016, 169/2020 and 180/2020: UNVERIFIED (OI-CMP-10).
- **BoG regulatory submission channel and national templates/checks:** GAP (OI-CMP-08). Solvency II deadlines after Directive 2025/2 (from 30 Jan 2027) may change.
- **DORA:** BoG channel and register-of-information window (OI-CMP-09).
- **ENFIA:** insurer interface to myPROPERTY (portal, file or API) and the yearly date (OI-CMP-05).
- **A.1004:** who issues the "unique policy registration number" (OI-CMP-20). AADE upload channel details are UNVERIFIED.
- **EAEE statistics feeds:** content, frequency and legal basis (OI-CMP-16). Green Card bureau data is market practice only (REQ-CMP-193 is Could).
- **Retention durations** for RC-CMP-*: deferred to the programme retention schedule (XMR-D-259, NFR-CMP-023). Values in §7.1 ("5 years", "3 years", "10 years", "tax-records period") are UNVERIFIED.
- **IRRD** (Directive 2025/1) Greek transposition (OI-CMP-23).
- **D2 tax and legal opinion:** IPT on a distance-withdrawal void, fees in the IPT base, whether the cover note stays non-fiscal (OI-CMP-24, a go-live gate).
- **AML applicability** is "Uncertain" in PTY (OBL-AML). Sanctions are "Settled". CMP only holds PTY's screening evidence as control evidence (REQ-CMP-217). No sanctions or AML rules are specified in CMP.
- **OBL-EQT:** gender is settled (CJEU C-236/09); other non-discrimination grounds are "Verify" (ROLE-46).

### 9.3 Rules deferred to configuration or country pack (§10.2)

| Key | Default |
|---|---|
| `cmp.fiscal.channel_binding` | NotRequired |
| `cmp.fiscal.mark_before_issue` | core false; GR true for premium receipts |
| `cmp.fiscal.batch_size` / `batch_wait_ms` / `rate_limit` | 500 / 2000 / pack |
| `cmp.fiscal.retry.*` | as BR-CMP-007 |
| `cmp.fiscal.schema_version` | pack |
| `cmp.fiscal.cancellation_allowed` | false |
| `cmp.fiscal.outage_mode` | none |
| `cap.cmp.einvoice_b2b` | off |
| `cmp.bureau.transport` | MANUAL_FILE (GR P1) |
| `cmp.bureau.lag.warn_h` / `breach_h` | 24 / 48, provisional |
| `cmp.bureau.incident_threshold` | 1 |
| `cmp.bureau.reconcile.schedule` | daily if the transport supports queries, else monthly |
| `cmp.clock.default_warnings` | 50%, 80%, 3 BD |
| `cmp.complaint.categories` / `root_causes` | pack lists |
| `cmp.complaint.redress_approval_threshold` | €500 |
| `cmp.complaint.autoclose_days` | 90 |
| `cmp.dsar.fanout_map` | as §9.2.2 |
| `cmp.dsar.task_due_bd` | 5 |
| `cmp.dsar.task_sla` | as REQ-CMP-263 |
| `cmp.set.incomplete_threshold_min` | 15 |
| `cmp.submission.taxonomies` | 2.8.2 / 2.10.0 |
| `cmp.return.a1004.layout_version` | pack |
| `cmp.submission.definitions` | pack |
| `cmp.obligation.freshness` | control frequency × 1.5 |

All clock durations come from MKT `StatutoryClockSet` and are certified through `REQ-MKT-290`.

## 10. Greek-market specifics

- **AFM:** verified per `REQ-PTY-003`. AFM check digit through `IdValidator` (REQ-CMP-180). Counterparty snapshot with **DOY** (`REQ-PTY-059`). AFM is masked by permission on screens. No AFM, names or plates appear in events or logs (NFR-CMP-015).
- **myDATA:** see §9. MARK, UID, authentication code and QR URL are stored. The QR payload is served to DOC (REQ-CMP-054). Nightly contract tests run against the myDATA development environment (NFR-CMP-020). Throughput targets (NFR-CMP-003):
  - about 6 M documents a year (5 M invoices plus credits and claim receipts);
  - 50 documents/s sustained for 1 h;
  - a backlog of 30,000 drained in ≤ 30 min.
- **Information Centre:** a service unit of the Auxiliary Fund (AC-10). Plate normalisation treats Greek and Latin look-alike letters as one key (REQ-CMP-069). Green Card numbers are reported (REQ-CMP-090). Volumes (NFR-CMP-008):
  - about 880 k insured vehicles, designed for 1.2 M;
  - about 2.5 M facts a year;
  - peak 20 k facts an hour.
- **gov.gr:** not mentioned. Identity assurance for DSARs comes from the CHN session level ("substantial").
- **Other Greek authorities and services:** AADE (myDATA, A.1004 upload, ENFIA via myPROPERTY), Bank of Greece (supervisor, DORA authority), EAEE (statistics), HDPA (DSAR complaint route), Consumer Ombudsman (ADR).
- **Calendar:** Europe/Athens. Property tests over 2026–2035 must cover Clean Monday, Orthodox Easter, Whit Monday, 15 August, leap years and month ends (NFR-CMP-021, §14.x). Also tested: the 50-day complaint deadline at day 50 23:59 vs day 51 00:01, and the DSAR deadline from 31 January.
- **Greek language rules:**
  - Greek is binding for complaint replies and DSAR responses; English is informative (NFR-CMP-019).
  - Terminology (§1.7, PRD-18 §6.4):
    - complaint = "παράπονο", never "καταγγελία";
    - statutory clock = "νόμιμη προθεσμία", never "καταστατική";
    - clock instance = "τρέχουσα προθεσμία";
    - BIL invoice = "ειδοποίηση πληρωμής", never "τιμολόγιο";
    - "regulatory submission" (κανονιστική υποβολή) is never called a plain "submission", which is a POL job (R-90).
  - Many translations are marked † or ‡ for confirmation by ROLE-46.
- **EUR:** money is rounded with MKT currency rules and zero tolerance (REQ-CMP-039). Thresholds are in € (redress €500).

## 11. Controls

- **Authority types registered with PLT (§12):**

| Authority type | Dimensions |
|---|---|
| `CMP_FISCAL_OVERRIDE` | entity, document type, amount |
| `CMP_FISCAL_CANCEL` | entity, amount |
| `CMP_COMPLAINT_DECISION` | entity, product line, redress amount |
| `CMP_DSAR_APPROVE` | entity, data class P1–P3 |
| `CMP_SUBMISSION_APPROVE` | entity, submission type |
| `CMP_CLOCK_DEFINE` | jurisdiction |
| `CMP_AI_REVIEW` | AI class |

- **Maker-checker list (§12):**
  - fiscal channel configuration (REQ-CMP-060);
  - document-level fiscal overrides (REQ-CMP-051);
  - channel cancellation;
  - bureau transport configuration and lag thresholds at entity level;
  - clock definitions (analyst + compliance officer, REQ-CMP-094) and clock extensions;
  - complaint replies with a rejected outcome or redress above the threshold;
  - DSAR package approval (DPO team maker, DPO checker) and refusals;
  - annual return approval (REQ-CMP-182);
  - every regulatory submission (REQ-CMP-200);
  - obligation and control changes;
  - regulatory-change assessment (REQ-CMP-229);
  - AI register approval and un-suspension;
  - evidence-room grants;
  - XBRL taxonomy import and activation (REQ-CMP-258);
  - redress above the threshold before a `CMP_REDRESS` disbursement;
  - DSAR SLA table changes.
- **SoD:**
  - fiscal override maker ≠ checker;
  - complaint handler ≠ decision-maker of the complained-about decision;
  - redress approver ≠ handler, and CMP never pays;
  - AI owner ≠ reviewer;
  - submission preparer ≠ approver;
  - control owner ≠ tester for key controls;
  - redactor ≠ approver for P3 packages.
- **Audit:** a long list of CMP audit events through `REQ-PLT-002` (§12), covering every fiscal, bureau, clock, complaint, DSAR, return, submission, register, AI and evidence-room action. Evidence is immutable; supersession is the only change (REQ-CMP-216). Every evidence-room access is audited (REQ-CMP-222).
- **GDPR handling of CMP data:**
  - ABAC on complaints; data-protection complaints are visible only to the DPO team (REQ-CMP-142);
  - P3 is masked except in the DPO workflow (REQ-CMP-175);
  - CMP provides its own export, restrict and erase operations (REQ-CMP-250). Fiscal snapshots are restricted with the tax retention basis;
  - retention classes: RC-CMP-FISCAL, BUREAU, CLOCK, COMPLAINT, DSAR, RETURN, EVIDENCE, REGISTER (REQ-CMP-248);
  - EU-only data and AI calls (NFR-CMP-016);
  - channel credentials in key management, never in logs (NFR-CMP-014).

## 12. AI features (§11)

All features are default **off** and run through the PLT gateway on EU endpoints. The module is complete without AI. No AI feature decides an outcome, a disclosure, a filing or a classification.

| ID | Feature | Class | Needed for MVP? |
|---|---|---|---|
| AI-CMP-01 | Complaint triage: classification, linking, "possible claim notification" flag | High-risk controls (uncertain) | No. Fallback is manual code lists |
| AI-CMP-02 | Bilingual complaint reply drafter | Limited risk (Art. 50), with high-risk-level accuracy review; letters carry an AI-assistance statement | No. Templates |
| AI-CMP-03 | Fiscal rejection explainer and correction proposer | Minimal | No. Code catalogue |
| AI-CMP-04 | Regulatory change impact analyser | Minimal, with high-risk-level documentation | No. Checklist |
| AI-CMP-05 | DSAR third-party and special-category redaction assistant | High-risk controls (uncertain); recall ≥ 98% on the seeded corpus; auto-disable on breach | No. WRK deterministic flags |
| AI-CMP-06 | Regulatory report pre-validation | Minimal | No |
| AI-CMP-07 | AI Act classification assistant | Minimal | No |

**The AI register itself is NOT optional:**
- REQ-CMP-007 and REQ-CMP-237…246 are mostly Must P1.
- The register is seeded with 119 features across 17 modules (§11.9). 39 of them carry high-risk controls; the earlier figure of 42 is withdrawn.
- PLT refuses enablement without an Approved entry (`REQ-PLT-233`).
- `AiSystemStatusChanged` must disable a Suspended or Retired feature within 60 s.

## 13. Open issues / assumptions / CCRs

**Open (15):**

| ID | Issue | Notes |
|---|---|---|
| OI-CMP-01 | Information Centre decision | interim manual file adopted |
| OI-CMP-02 | B2B e-invoicing applicability | design settled per D3 |
| OI-CMP-03 | myDATA API 2.x, XSD, headers, batch limit, transmission deadline | |
| OI-CMP-04 | B2C E3 code; premium-tax category codes | |
| OI-CMP-05 | ENFIA interface and date | |
| OI-CMP-06 | Act 88/2016 primary text; ADR bodies | |
| OI-CMP-08 | BoG channel and national templates; post-2027 SII deadlines | |
| OI-CMP-09 | DORA BoG channel; register-of-information window | |
| OI-CMP-10 | BoG Acts 60/2016, 169/2020, 180/2020; complaint statistics return | |
| OI-CMP-16 | EAEE feeds | |
| OI-CMP-17 | UNVERIFIED clock values | |
| OI-CMP-20 | A.1004 registration-number issuer | |
| OI-CMP-22 | `POL_MTPL_THIRDPARTY_NOTICE` legal basis and 16 days | |
| OI-CMP-23 | IRRD transposition | |
| OI-CMP-24 | D2 tax opinion effects | go-live gate |

**Closed (9):** OI-CMP-07, 11, 12, 13, 14, 15, 18, 19, 21.

**Assumptions:**
- A-1: premiums are VAT-exempt.
- A-2: a manual file is an acceptable interim method for the Information Centre.
- A-3: PLT provides durable business-day timers and the exchange archive.
- A-4: DAT produces all report content.
- A-5: modules complete DSAR operations within one business day.
- A-6: the insurer is the controller.
- §3.3 has another set: A-1…A-4 plus AC-01…AC-10 (prompt facts vs sources).

**Risks:** RK-CMP-01…08. The top ones are the Information Centre specification (High/High), late e-invoicing applicability, wrong statutory values, and the XBRL renderer lagging a taxonomy release.

**CCRs:** none new in v1.1. CCR-CMP-01…06 were all accepted (R-59…R-64):
- 17 new events;
- §10.5 becomes the clock list of record;
- MIRROR clocks;
- anchors `REQ-DAT-009` and `REQ-MIG-006`;
- `OBL-PAY` and `OBL-PCI`;
- CMP authority and four-eyes items.

**Clock conflicts K-01…K-12 (§10.5):** K-01, K-02, K-04 and K-05 were adopted by R-60, K-08 by R-61; the others are resolved in the table.

**Ten pre-build decisions (§16.5):**
- **Settled:** #3 and #5 (D3), #4 (R-60/61, D7), #6 (R-62), #7 (closed by citation), #9 (D4).
- **Still open:** #1 (Information Centre method and thresholds), #2 (e-invoicing), #8 (Greek complaint rules), #10 (regulatory-change operating model).

## 14. Conflicts and ambiguities found

### (a) With infra/stack

1. **"Workflow engine" language throughout.**
   - Where it appears:
     - REQ-CMP-041: "run each fiscal document through a workflow on the PLT workflow engine";
     - REQ-CMP-155: "fan-out as a workflow saga";
     - REQ-CMP-108: "workflow engine's durable timers";
     - A-3;
     - SYS-02: "Batch job or workflow-engine workflow";
     - §1.4: PLT owns "Workflow engine, timers".
   - The stack bans workflow servers and BPM engines. It uses Hangfire with deadlines stored as domain records.
   - It needs mapping to: explicit state machines in `cmp` tables, a Hangfire recurring "due-clock scanner", and outbox handlers.
2. **Broker vocabulary.** §8 says "Topic `cmp.events.v1`. Partition keys: fiscal_document_id … clock_instance_id …". CCR-CMP-01 also says "on `cmp.events.v1`". The stack has no broker: the transactional outbox dispatches to in-process handlers. Treat "topic" and "partition key" as logical ordering keys only. Per-key ordering through the outbox must still be guaranteed, for example per-vehicle order for bureau facts (BR-CMP-016).
3. **"Lakehouse."**
   - Where it appears: §1.2 ("returns that the lakehouse does not file"), event `LakehouseErasureCompleted`, "lakehouse DSAR" (§9.2.1), CCR-CMP-04 ("in the lakehouse").
   - The stack explicitly excludes lakehouses; reporting marts are PostgreSQL schemas. The event name and DAT erasure semantics need renaming or reinterpretation.
4. **"Adapter host in a separate failure domain"** (REQ-CMP-073, NFR-CMP-006).
   - "A severity-2 incident … through PLT" and "T2 engine (RTO 4 h)" vs T1 bind (NFR-CMP-001) imply separate deployables or tiers.
   - In a modular monolith this needs a decision: a separate Container App for the adapter worker, or in-process circuit breakers only.
   - NFR-CMP-001 ("commands queued if CMP is unavailable") is close to meaningless when CMP is in-process with POL. It needs reinterpretation, for example as outbox-buffered clock commands.
5. **"Regional failover"** (REQ-CMP-108 AC) and "EU stamp", "one stamp per entity" (NFR-CMP-016, NFR-CMP-024). Check these against INFRASTRUCTURE.md: are there multiple regions or multiple stamps?
6. **In-house XBRL renderer and EIOPA taxonomy validator (D10, REQ-CMP-256…258).** .NET has no mainstream XBRL/DPM validation library. Writing a conformant validator (formula and filing rules, two taxonomies in parallel, xBRL-CSV and xBRL-XML) is a very large build. It conflicts with the "minimal dependencies / boring tech" principle unless a library is chosen, but D10 forbids an "external reporting tool". This needs a decision on what counts as a library vs a tool.
7. **Vendor names** (Odoo, Transcend, Salesforce) appear only as UI research references (F-15). No conflict.
8. **"Key-management service"** maps to Key Vault. **"PLT model gateway"** and the DAT model registry are fine. No Kafka, Camunda, Kubernetes or other languages are mentioned.

### (b) With the system contract or other PRDs

- **K-01:** non-payment notice of one month vs the prompt's two weeks. MKT §10.4.3 must update.
- **K-02:** MKT `REQ-MKT-094` uses `POL_NONPAY_NOTICE`. The canonical code is `BIL_NONPAY_NOTICE`, and a start with the old code fails.
- **K-04:** refund start variants between PRD-05 and PRD-12.
- **K-05:** Auxiliary Fund deadline modelled twice (PRD-06 vs PRD-09 CCR-FIN-04).
- **K-06:** PRD-17 marks the history statement UNVERIFIED while PRD-07 verified it.
- **K-08:** CD-07 says one engine, but DORA timers stay in PLT (MIRROR).
- **K-12:** two MARK-timing keys.
- **CON-035:** PRD-15 `REQ-DAT-129` sets a 31 March annual IPT detail report that PRD-09 cannot find a legal basis for.
- **AC-04:** the prompt's A.1004 deadline of 25 January is superseded by 10 January.
- **AC-05 / K-10:** ENFIA date typo.
- Other PRDs must drop CMP from their consumer lists for `EvidencePackSealed`, `RefusalDocumentIssued`, `StatutoryOfferDue` and `StatutoryOfferBreached` (§8.2).
- MKT §10.4.3 must record calendar days for the 50-day complaint reply (K-11).

### (c) Internal contradictions and inconsistencies

- **A-5 vs REQ-CMP-263.** A-5 says each module completes DSAR operations "within one business day". REQ-CMP-263 and NFR-CMP-012 allow 5 business days for erasure, 10 calendar days for DAT and 2 business days for MIG.
- **BR-CMP-035 vs BR-CMP-056.** BR-CMP-035 says the task due time is "5 business days before the DSAR deadline". BR-CMP-056 says the earlier of the SLA and that date. There are also two config keys (`cmp.dsar.task_due_bd`, `cmp.dsar.task_sla`). BR-035 is effectively superseded.
- **Event status.** §8.1 marks `ComplaintAcknowledged`, `ComplaintEscalated` and `DSARExtended` as "Contract (R-59)", but §7.3.4 and §7.3.5 still call them "proposed". CCR-CMP-01's text says "marked Proposed".
- **"41 Active" vs REQ-CMP-264.** §10.5 and REQ-CMP-121 say 41 Active definitions, including Unverified motor-path values such as #40, #13 and #6. REQ-CMP-264 says these cannot be Active in production. "Active" therefore means non-production register status only. It is unclear how many motor-path clocks can actually activate in production at go-live.
- **ROLE-22 vs ROLE-25.** AI-CMP-03 names its user as a "ROLE-22 specialisation". Everywhere else the fiscal analyst is a ROLE-25 specialisation (§2).
- **REQ-CMP-250 placement.** It is listed in REQ-CMP-005's elaboration (DSAR), but counted in CAP-CMP-12.
- **Duplicate API row.** §9.1 lists `cmp.FiscalDocument.listBySource` twice (in the get row and in its own row). This is harmless.
- **REQ-CMP-033 B2C claim receipt type.** The test refers to "the pack type". BR-CMP-001 gives 13.1/13.2 "per CLM BR-CLM-027", which this PRD does not verify.
- **REQ-CMP-196 vs §10.5 rows 31–33.** REQ-CMP-196's source cell says Art. 312 values are "UNVERIFIED", while rows 31–33 say "verified for the 2026 cycle".

### (d) Things that cannot be built without a decision

1. **Information Centre transport, layout, deadline and real lag thresholds** (OI-CMP-01). Only the manual-file path and the canonical fact model can be built now.
2. **myDATA XSD, API version, headers and limits** (OI-CMP-03). The XSD must be obtained manually before the build. Schema validation (REQ-CMP-040) needs it.
3. **E3 code and premium-tax category codes after the IPT reform** (OI-CMP-04). These are needed for the Greece pack mapping.
4. **IPT rate and Auxiliary Fund rate:** not in CMP. The RAT/MKT digests must supply them.
5. **Production activation** of the UNVERIFIED motor-path clock values (OI-CMP-17, REQ-CMP-264). These include the objection, renewal and non-renewal leads, FS reply and insurer termination notice. Without Settled values, those clocks fail closed in production.
6. **XBRL build-vs-library decision** and BoG national checks (D10, OI-CMP-08).
7. **Complaint acknowledgement period and ADR list** (OI-CMP-06).
8. **E-invoicing applicability** (OI-CMP-02). The interface can be built with the NotRequired default.
9. **Hangfire vs "durable timers" design** for about 2 M concurrent clock instances with 60-second event latency (NFR-CMP-009, NFR-CMP-010). One Hangfire job per instance is a poor fit.

## 15. Build notes

**Hardest parts**

1. **Clock engine (CAP-CMP-04).** This is the programme dependency for POL, BIL, CLM, UW, FIN and PLT. It needs:
   - DEADLINE vs WAITING_PERIOD semantics;
   - frozen value sets from MKT;
   - business-day and month arithmetic with a Greek calendar (PLT `REQ-PLT-197`);
   - pause and extension;
   - catch-up emission after an outage that is ordered and fires once;
   - idempotent start and stop;
   - MIRROR clocks;
   - about 2 M concurrent instances, with warnings and breaches within 60 s.
   - Recommended design: a `cmp.clock_instance` table with `next_threshold_at`, an index, and a Hangfire recurring scanner (every 15–30 s) that emits through the outbox.
2. **Fiscal channel.** Needs exactly-once registration under timeouts (duplicate check before resend), micro-batching, per-document response parsing, rejection workflow, gap-free fiscal numbering under concurrency (REQ-CMP-038 tests 1,000 concurrent numbers), complete-set gating (REQ-CMP-261), daily reconciliation, and property tests that prove zero duplicate MARKs.
3. **In-house XBRL rendering and taxonomy validation** (D10), with two taxonomies in parallel. This is the biggest technical-risk item (RK-CMP-08).
4. **Bureau fact derivation with out-of-sequence netting** (REQ-CMP-070) and per-vehicle ordering, plus the migration hand-over and reconciliation.
5. **DSAR saga** across 15 modules, with the SLA table, retention-conflict outcomes, item-level redaction and the quarterly seeded completeness test (REQ-CMP-167). This needs every other module's data-subject operations to exist first.

**Must exist first**
- PLT: audit, approvals and maker-checker, authority, numbering (`REQ-PLT-014`), holiday calendars and business-day arithmetic, outbox, Hangfire, exchange archive, incidents.
- MKT: pack model, `StatutoryClockSet`, `FiscalDocumentChannel`, `BureauAdapter`, `ComplaintRules` SPIs, certification gate.
- PTY: party with verified AFM.
- PFC: fiscal-category keys.
- POL and BIL events for the fiscal and bureau flows.

**Suggested slicing**
1. Clock register and engine with the §10.5 seed, API, dashboard (SCR-CMP-02), and the legal-status gate. This unblocks other modules.
2. Obligations register skeleton and the AI register gate (REQ-CMP-007, REQ-CMP-241, REQ-CMP-242). PLT needs these to enable any AI.
3. Fiscal channel against a myDATA sandbox double: request → build → batch → register/reject → rejection queue → reconciliation (SCR-CMP-03).
4. Information Centre: canonical facts, manual-file transport, lag clock and incident, reconciliation (SCR-CMP-04).
5. Complaints (SCR-CMP-05) with DOC delivery and redress routing.
6. DSAR saga and disclosure log (SCR-CMP-06), added progressively as modules ship their operations.
7. Regulatory submission tracker; XBRL renderer and validator (SCR-CMP-08).
8. Regulatory-change backlog (SCR-CMP-09) and compliance home.
9. P2: A.1004 and ENFIA (SCR-CMP-07).
10. Migration import APIs, aligned with the MIG timeline.

AI features (AI-CMP-01…07) are all post-MVP.
