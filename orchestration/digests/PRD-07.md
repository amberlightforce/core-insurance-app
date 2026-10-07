# Digest — PRD-07 Claims Management (CLM)

Source: `core-insurance-prds/PRD-07-claims-management.md` (2,083 lines, read in full). Version 1.4 "freeze fix for build baseline 1.0", dated 2026-10-07, status "Draft — for design-authority review" (§1.1). Binding inputs cited: 00-system-contract.md v1.11 (rulings R-07, R-38…R-43, R-77, R-78, R-80, R-82, R-84, R-86, R-87, R-91, R-96, R-100, R-101, R-102; decisions D1–D10), PRD-18 change requests, prompt pack prompt 7, `inv_CLM.md` inventory.

---

## 1. Identity

- **Module code:** CLM. **Title:** Claims Management. Owns PostgreSQL schema `clm` (§7.0).
- **Purpose (§1.2):** claims system of record. Takes FNOL from any channel; verifies cover against an immutable POL snapshot at loss date; structures the claim into exposures (one coverage × one claimant); manages reserves, payments and recoveries as approved **transaction sets** on **reserve lines** (exposure × cost type × cost category) with derived balances; runs the Greek motor ecosystem (Friendly Settlement, Auxiliary Fund, Green Card, statutory offer clock, claims-history certificate) behind country-pack SPIs; coordinates vendors, litigation, fraud/SIU and catastrophe operations; publishes claim financial events for RI, FIN and DAT.
- **Five key decisions (§1.2):** (1) ledger-shaped claim financials, immutable `ClaimFinancialTransaction`, balances derived never stored as editable totals (REQ-CLM-003, 095…101); (2) snapshot never mutated; out-of-sequence POL changes raise `ReverificationRequired` and a human decides (REQ-CLM-002); (3) one payment pipeline: CLM decides what/whom, **BIL is the only cash executor (D1)**, PTY screens synchronously, payee accounts live in BIL PaymentInstrument (R-38) (REQ-CLM-004, 131); (4) Greek motor ecosystem as pack plug-ins (`FriendlySettlementClearing`, `MotorCompensationBodyAdapter`, `ClaimsHistoryFormat`, CMP clocks); Cyprus stub proves core is not Greek-shaped; (5) exception-first handling at cat scale (D8 design event: 1,000 FNOL/hour for 24 h, ≈32,000 claims over 72 h).
- **Non-goals / out of scope (§1.4):** policy data/snapshots (POL); coverage semantics, perils, regulatory mapping (PFC); party master, vendor directory, sanctions engine, vulnerability, consents (PTY); disbursement execution, VoP, bank files, FS net cash movement (BIL); cession and RI recovery calculation (RI); postings/journals (FIN); rendering, archive, delivery proof (DOC); inbound documents, activities, queues, notes, participants, global search (WRK); clock register and instances, fiscal documents, complaints (CMP); portals, partner APIs, AI agent facade (CHN); authority framework, maker-checker, audit, workflow, numbering, time, AI control plane (PLT); model registry, IBNR, marts (DAT); configuration model, SPIs, clock values (MKT); UW non-disclosure decisions (CLM records findings only). **Not in programme scope:** assumed reinsurance claims, workers' compensation, health insurance claims. Bancassurance FNOL out of P1 unless a bank partner signed by G0 (D6, §2).
- **Phases (§1.5):** P1 Motor MVP (Greece); P2 Home; P3 Commercial property & liability; P4 later markets (Cyprus first).

---

## 2. Size metrics

From §5.17 (verified against tables):

| Measure | Count |
|---|---|
| Functional requirements | **248** (REQ-CLM-001…011 contract anchors + REQ-CLM-030…266) |
| MoSCoW | **Must 197 / Should 43 / Could 8 / Won't 0** |
| Tag | [BASELINE] 214 / [ENHANCEMENT] 34 |
| Phase | **P1 242** / P2 3 (REQ-CLM-032, 186, 213) / P3 2 (034, 154) / P4 1 (237) |
| Business rules | 42 (BR-CLM-001…042) |
| NFRs | 20 (NFR-CLM-001…020) |
| Screens | 20 (SCR-CLM-01…20; SCR-CLM-20 is a content contract rendered by CHN) |
| AI features | 8 (AI-CLM-01…08), all default off |
| Owned entities | ~27 named in §7.1 (Claim, FnolSnapshot, Exposure, Claimant, ClaimContactRole, Incident, HealthProcessingBasis, ReserveLine, ClaimFinancialTransaction, TransactionSet, ClaimPayment, PayeeAccountView [read model only], RecurringSchedule, Recovery, FsCase, FsStatement, StatutoryOfferRecord, CatEvent, VendorPanel/PanelMember, VendorAssignment, VendorInvoice, LitigationMatter, FraudAlert, SiuCase, ClaimsHistoryCertificate, ClaimMessage/Conversation, DocumentChecklistItem, plus supporting CertificateRequest, LargeLossRecord, DuplicateLink, ReverificationRecord) |
| Events produced | 27 (16 contract + 11 added by R-39) |
| Events consumed | 53 named event types from POL, PTY, BIL, CMP, WRK, DOC, RI, FIN, UW, PLT, MIG, DAT, MKT |
| Exposed API operation groups | 26 rows in §9.1 (REST under `/api/clm/v1/`) |
| Authority types registered | 12 (§12) |
| SoD rules | 7 (SOD-CLM-01…07) |
| Retention classes | 4 (RC-CLM-FILE, -HEALTH, -SIU, -CERT) |
| Golden scenarios | 300 claim scenarios (§14.x) |

**Motor MVP (P1) count: 242 requirements** — 197 Must (every Must is P1), 42 Should, 3 Could (REQ-CLM-115, 217, 253). The P1 Must set is the MVP floor.

**Build-size estimate: L.** 197 Must requirements, 20 screens, a ledger-style financial engine with derived balances and set-level authority, a payment status loop with BIL, six statutory clocks, monthly FS clearing reconciliation, catastrophe-scale throughput and migration import/reverse — well beyond the >100 / heavy-money threshold.

---

## 3. Owned entities (§7.1) and state machines (§7.3)

Storage rules (§7.0): every row carries `legal_entity_id`, `jurisdiction`, `created_at`, `created_by`, `record_version`; financial transactions append-only; projections derived and rebuildable (REQ-CLM-100); claim is **not bitemporal** (stores POL snapshot reference plus a display-only denormalised copy never used for decisions); no IBANs in CLM; P2 identifiers and P3 injury/medical fields field-level encrypted with platform KMS keys.

| Entity | Key attributes | Constraints / notes |
|---|---|---|
| Claim | claim_id, claim_number, policy_id/number, snapshot_segment_id + snapshot_known_at, snapshot_status (Pending, Verified, ReverificationRequired), product code/version, LoB, loss_at, notice_on, loss_cause, loss_location, description, handling_segment, cat_event_id, fault_insured_pct + fault_source, status, sub_status, outcome, reopen_count, flags jsonb (high_risk[], litigation, siu, p3_present, vulnerable, coverage_in_question), channel, legacy_ref | claim_number unique per legal entity; policy reference immutable except via re-verification adoption; RC-CLM-FILE |
| FnolSnapshot | payload jsonb as submitted (P3 parts encrypted), channel, reporter, submitted_at | one per claim, immutable (REQ-CLM-044) |
| Exposure | exposure_number, kind, coverage_code (or EXGRATIA, STATUTORY), claimant_id, incident_id, handler, status/sub_status/outcome, coverage_decision (Pending, Covered, CoveredWithReservation, PartiallyCovered, NotCovered), reasons, decision maker, authority_check_id, claim_received_on, vat_recovery_status | (claim_id, exposure_number) unique; one open exposure per (coverage, claimant, incident) unless reason (REQ-CLM-063) |
| Claimant | party_id (PTY), type (insured, third party, guarantee fund, bureau), representative, contact_prohibited | |
| ClaimContactRole | party_id, role, related_object, active, comments, `[valid_from, valid_to)` half-open UTC record time (D5) | |
| Incident | type (Vehicle, Property, Injury, Liability); vehicle/property facts; injury fields (P3, encrypted) | injury data RC-CLM-HEALTH |
| HealthProcessingBasis | basis code, notice_document_id | one active per party per claim (REQ-CLM-080) |
| ReserveLine | exposure_id, cost_type (Indemnity, ExpenseAllocated, ExpenseUnallocated, StatutoryInterest), cost_category, currency, final_flag | unique (exposure, cost_type, cost_category, currency) |
| ClaimFinancialTransaction | txn_number, reserve_line_id, kind (Reserve, Payment, RecoveryReserve, Recovery), amount + functional_amount + group_amount + fx_rate_id, eroding, payment_type (Partial, Final, Supplementary, Recurring), status, reverses_txn_id, set_id, reason_code, accounting_date | immutable after Approved except status progression; corrections by linked negative transaction |
| TransactionSet | maker, content_hash, approval_request_id (PLT), authority_check_ids[], four_eyes, rejection_reason, journal_ref | single claim per set |
| ClaimPayment | payee, co_payees[], method (SEPA_CT, SEPA_INST, VENDOR, OFFSET, CLEARING), payment_instrument_id (BIL), disbursement_id (null for CLEARING), fs_statement_id, complaint_ref, screening refs, vop_result, fiscal series/number (from CMP, D3), mark, advice doc, schedule_id | one disbursement per payment except CLEARING |
| PayeeAccountView (read model) | instrument id, masked IBAN (last 4), verification status/date, `cooling_off_until` (set and evaluated by BIL only), claim change evidence | CLM owns **no** payee-account entity (R-38, R-82) |
| RecurringSchedule | amount, frequency, start/end, review_on, approval_ref, status | |
| Recovery | type (Subrogation, Salvage, Deductible, FriendlySettlement, GuaranteeFund, GreenCard, Contribution, Other), counterparty, expected_amount, status, milestones, arbitration, allocation rule | |
| FsCase (Greece pack) | role (OwnInsurer, AtFaultInsurer), counterparty insurer, eligibility_result + rule version, clearing_value, clearing_reference, dispute, statement_line_ref | |
| FsStatement | period, counterparty totals, lines, reconciliation, net per counterparty, net settlement (direction, amount, approval_request_id, BIL disbursement/receivable ref, source `FS_CLEARING`, status Pending/Requested/Settled/Rejected) | one per period per legal entity |
| StatutoryOfferRecord | kind (ReasonedOffer, ReasonedReply), amount breakdown, place/time/method, document_id, delivered_at (from DOC proof), acceptance, repair-in-kind agreement, interest_basis | links CMP clock instance ids |
| CatEvent | cat_code, names GR/EN, perils, start/end, area (codes or polygon), status, surge settings, approval_ref | cat_code unique per legal entity |
| VendorPanel / PanelMember | service_type, region, members (vendor party, status, capacity, skills, rates_ref, sla_targets) | |
| VendorAssignment (Service) | service_number, related_object, service_type, vendor, status, next_action, action_owner, target/completed dates, metrics, quote | |
| VendorInvoice | invoice_reference, addressed_to, amount, vat, lines, mark, status, status_reasons, payment_id | unique (vendor, invoice_reference) |
| LitigationMatter | matter_type, court, case_number, counsel, claimed_amount, status, hearings, settlement (amount, authority_check_id), judgment | |
| FraudAlert | source (Rules, Model), model_version, band, indicators, decision | RC-CLM-SIU |
| SiuCase | claim_ids[], allegation_type, investigator, status, findings, outcome, amounts recovered, referral, payment_hold (on, reason, released_by[]) | restricted ABAC; may hold criminal-offence data |
| ClaimsHistoryCertificate | number, requester, policies, period, content + hash, format_version, document_id, version, supersedes_id | RC-CLM-CERT |
| ClaimMessage / Conversation | party, channel, status, visibility, owner, messages | |
| DocumentChecklistItem | document_class, status (Pending, Received, Verified, Waived), waive_reason, due_on, linked docs | |

### State machines

**Claim and Exposure (§7.3.1; REQ-CLM-071; contract §3.2.4).** States Draft → Open → Closed; Closed → Open on reopen. Open sub-states New, InProgress, UnderInvestigation, Settled. Closure outcomes Completed, Denied, Withdrawn, Duplicate, NoPayment.

| From | To | Trigger / guard | Events |
|---|---|---|---|
| — | Draft | FNOL draft saved, or partner/repairer FNOL needing confirmation | — |
| Draft | Open(New) | FNOL submitted/confirmed; mandatory fields; claim number issued | `ClaimReported`, `ExposureCreated` |
| New | InProgress | handler first action/first contact; handler assigned | `ClaimUpdated` |
| Open(any) | UnderInvestigation | coverage in question, SIU, liability disputed | `ClaimUpdated` |
| Open(any) | Settled | all exposures settled but follow-up (recovery) open | `ClaimUpdated` |
| Open | Closed(outcome) | guards REQ-CLM-072/073 | `ClaimClosed` |
| Draft | Closed(Withdrawn) | draft discarded after confirmation declined | none (no number) |
| Closed | Open | reopen with coded reason; `CLM.REOPEN` authority after configured period (default 365 days) | `ClaimReopened` |

Exposure close guard (REQ-CLM-072): open reserves zero, no payment pending/awaiting disbursement, statutory clocks Met or Cancelled, no open litigation; recovery reserves may stay open. Claim close (REQ-CLM-073): all exposures Closed and no SIU case requiring it open; open recovery keeps claim Open/Settled unless "recovery only". Exposure Denied = approved NotCovered + delivered denial letter.

**TransactionSet (§7.3.2):** Draft → Submitted → Approved (within authority, no four-eyes) | PendingApproval (refer or four-eyes; approval request with content hash). PendingApproval → Approved (checker ≠ maker, hash equal, re-validation passes) | Rejected (reason, or expiry as sub-state Expired) | Draft (returned as new copy). Approved → Posted (FIN `JournalPosted`, journal ref). Stale approval fails with `CLM-ERR-SET-STALE` (REQ-CLM-112).

**ClaimPayment (§7.3.3):** Pending → Approved (set approved) | Rejected (set rejected). Approved ↔ OnHold (sanctions, SIU, VoP). Approved → Submitted (disbursement requested; mirrors BIL Requested/PendingApproval/Approved/Released) → Issued (`DisbursementIssued`, publishes `PaymentIssued`) → Cleared. Submitted → Stopped (`DisbursementStopped`/stop confirmed); Submitted → DisbursementRejected; Issued → Voided; Issued → Returned; Cleared → Returned (late return). Each of Stopped/Voided/Returned/DisbursementRejected publishes `PaymentVoided` with reason and creates a reversal transaction (reserve restored if eroding). CLEARING payments: Approved → Settled when the statement net is Settled. One-to-one mapping to BIL Disbursement states for Released, Issued, Cleared, Rejected, Stopped, Voided, Returned (D4).

**Recovery (§7.3.4):** Open → Demanded → Agreed → Closed; Demanded → Disputed → (InArbitration | InLitigation | Agreed | WrittenOff); any open → WrittenOff (`CLM.RECOVERY_WRITEOFF`); Closed → Open on new information.

**FsCase (§7.3.5):** EligibilityPending → Eligible | NotEligible; Eligible → Submitted → Accepted | Disputed; Disputed → Accepted | Rejected; Accepted → Settled (statement matched) → Reconciled (net settled through BIL `FS_CLEARING`).

**FsStatement net settlement:** Pending → Requested → Settled | Rejected; a rejected/stopped net payable returns to Pending (REQ-CLM-264).

**CatEvent (§7.3.6):** Draft → Active (four-eyes) → Closed; Active → Active (area/date change, re-tag); Closed → Active (four-eyes).

**Service:** Requested → Accepted | Declined; Accepted → InProgress → WorkComplete; any → Cancelled. **Invoice:** Submitted → InReview → Approved → Paid; InReview → Rejected; Submitted/InReview → Withdrawn; Approved → PaidOutside (§7.3.7).

**LitigationMatter:** Open → HearingScheduled → Judgment → (Appeal → Judgment) → Closed; Open/HearingScheduled → Settled → Closed. **SiuCase:** Open → Investigating → Concluded → Open (reopen). **Certificate:** Requested → Assembled → InReview (only if flagged) → Issued → Superseded. **StatutoryOfferRecord:** Drafted → Issued → Delivered (reasoned offer starts `CLM_MTPL_PAYMENT_DUE`) → Accepted | Rejected | Lapsed; Accepted → Paid or RepairInKindAgreed → Repaired (§7.3.8).

**ReserveLine (§7.3.9, canonical per D5):** OpenLine → OpenLine (further approved transactions) → FinalLine (final payment or release to zero) → OpenLine (reopen).

**Offer clock (journey J-03 diagram, §4.3):** Running → Warned → Met | Breached; Running → Met; Running → Cancelled (exposure withdrawn/duplicate); Breached → interest accrues until payment. CMP ClockInstance states: Running, Paused, Warned, Met, Breached, Cancelled.

**Financial formulas (§1.7, REQ-CLM-095/096):** open reserve = Σ reserve − Σ eroding payments (Approved and later, not Voided), never < 0; incurred = paid + open reserve; open recovery reserve = Σ recovery reserves − Σ recoveries (≥ 0); net incurred = incurred − recoveries − open recovery reserve. Payment exceeding open reserve: auto-add reserve increase to same set or reject (REQ-CLM-097, BR-CLM-008). Final payment proposes release of remainder (REQ-CLM-099). Recovery allocation default pro rata to paid (REQ-CLM-151, BR-CLM-039).

---

## 4. Consumed entities / dependencies (§7.2, §9.2, §15.1)

| Owner | What CLM reads/uses | Via |
|---|---|---|
| POL | Policy, term, segment snapshot at loss date, co-payees (interested parties), policy search, term timeline for certificates | `pol.Snapshot.get` (REQ-POL-007), `pol.Policy.search` (REQ-POL-014), `pol.Policy.get`, `pol.Term.timeline` (REQ-POL-085, 002), REQ-POL-289; events `TransactionReversed`, `PolicyChanged` etc. |
| PFC | Coverage catalogue, term semantics (limits, deductibles, aggregation basis), peril tags, SII LoB/statistical class/IFRS 17 portfolio, retired artefacts by hash, question sets, wording refs | `pfc.Catalogue.get`, `pfc.Artifact.get` (REQ-PFC-003, 076, 094, 005, 181, 006) |
| PTY | Parties, roles, vendor directory, consent, preferences, vulnerability, sanctions screening, merge events | `pty.Party.*`, `pty.Screening.screen` (REQ-PTY-006), `pty.Vulnerability.query`, `pty.Consent.query`, `pty.CommunicationPreference.resolve` |
| BIL | Disbursement execution and status; PaymentInstrument (payee accounts, VoP, cooling-off); receivables (deductible, salvage); premium offset; FS net settlement | `bil.Disbursement.request/stop/void/get/list` (REQ-BIL-009, 197…214), `bil.PayeeAccount.create/verify/get` (REQ-BIL-343…345), `bil.Receivable.register` (REQ-BIL-355…357), REQ-BIL-346, 347 |
| CMP | Clock instances; fiscal documents (settlement receipt MARK); complaints; DSAR fan-out; AI system register; redress routing | `cmp.Clock.*` (REQ-CMP-003), `cmp.FiscalDocument.request/listBySource` (REQ-CMP-001, 033, 055), `cmp.Complaint.create/link` (REQ-CMP-004, 122, 124), REQ-CMP-005, 007, 134 |
| DOC | Rendering, delivery proof, archive, Documents tab component, e-signature | `doc.Document.request/get/listForObject`, `doc.Delivery.status/evidenceFor`, REQ-DOC-001, 004, 005, 006, 008, 041, 042, 234 |
| WRK | Activities, participants/assignment, notes, inbound documents, approval activities, delegation, surge routing, global search projection | `wrk.Activity.create`, `wrk.Participant.assignByRules` (REQ-WRK-302), `wrk.Note.*`, `wrk.InboundDocument.*`, REQ-WRK-172, 200, 202, 205 |
| PLT | Authority check, approval, audit, numbering (`plt.Number.next(CLAIM)`), time service, "workflow engine"/decision tables (REQ-PLT-007), AI gateway, FX rates (REQ-PLT-009), retention, legal hold, register of information, step-up (REQ-PLT-053) | REQ-PLT-002, 003, 004, 007, 009, 010, 011, 012, 053, 100…122, 209, 238, 285, 315, 332 |
| MKT | Configuration, rounding, capability switch, SPIs | `mkt.Configuration.resolve`, `mkt.Rounding.apply` (REQ-MKT-195), REQ-MKT-004, 108, 109, 310, 313 |
| UW | Non-disclosure findings and proportional-reduction factor; policy holds | `uw.DisclosureFinding.record` (REQ-UW-270), REQ-UW-273, REQ-UW-007 |
| FIN | IFRS 17 group reference; journal posted confirmations | `fin.Ifrs17Group.get` (REQ-FIN-011), `JournalPosted` |
| RI | Reinsurance recoverable per claim (read-only) | `RecoveryCalculated`, `ri.Recovery.listByClaim` (REQ-RI-003, 136) |
| MIG | Coexistence master routing | `CoexistenceMasterChanged`, `mig.Routing.resolve` (REQ-MIG-149) |
| DAT | Model drift/bias signals; model registry | `ModelDriftDetected`, `BiasThresholdBreached`, REQ-DAT-005 |

Read models kept in CLM (§7.2): `PolicySnapshotView`, `PartyView`, `ScreeningView`, `DisbursementView`, `ClockView`, `RiRecoveryView`, `FiscalDocView`.

**Who consumes CLM** (inbound): POL claim guard via `ClaimView`/tracking (REQ-CLM-060, 300 ms); RI financial events, `clm.CatEvent.aggregate`; FIN events + `clm.Financials.dailyTotals`; DAT feeds `dc.clm.*`; CHN FNOL/tracking/certificate/vendor APIs; CMP redress and DSAR; MIG import/reverse; PTY/UW tracking; WRK search federation; RAT consumes certificate content (REQ-RAT-140).

---

## 5. Events (§8)

Section header: "Topic `clm.events.v1`. Partition key `claim_id` for claim-scoped events (R-39 as amended by R-102)"; other events keyed by aggregate id per R-100. Envelope per contract §3.4.1; catalogue of record PRD-18 §8. Payloads carry ids and minimum data; no P2/P3, payee names masked, IBAN never. Per-claim order, gap-free sequence (REQ-CLM-005, NFR-CLM-018).

### 5.1 Produced (27)

| Event | Trigger | Key payload | Consumers |
|---|---|---|---|
| `ClaimReported` | FNOL submitted | claim number, policy id/number, snapshot ref, loss/notice date, cause, line, product, cat code, channel, claimant party ids+roles, handling segment | WRK, RI, FIN, CHN, DAT, DOC, PTY, POL, UW |
| `CoverageVerified` | first verification, each completed re-verification | snapshot ref, outcome, decision maker | WRK, DAT |
| `ReverificationRequired` | POL superseded segment / changed policy at or before loss date | old ref, new ref, cause event id | WRK, DAT |
| `ExposureCreated` | exposure created | id/number, kind, coverage code, claimant, SII LoB, accident date | RI, FIN, DAT |
| `ReserveChanged` | approved reserve or recovery-reserve change per line | line key, kind, delta and new open amount in 3 currencies, set id, accident date, policy term, product, SII LoB, IFRS 17 group, cat code, handling segment | RI, FIN, DAT |
| `TransactionSetApproved` | set approved | set id, txn ids, totals by kind, approvers, authority check ids, four-eyes flag | RI, FIN, DAT |
| `PaymentIssued` | `DisbursementIssued` received | payment id, txn ids, lines, amounts (3 currencies), payee party id, method, disbursement id, ex-gratia flag, complaint ref | RI, FIN, DAT, CHN, POL |
| `PaymentVoided` | void/stop/return/rejection confirmed | payment id, reversal txn ids, reason (Voided, Stopped, Returned, Rejected) | RI, FIN, DAT |
| `RecoveryRecorded` | recovery received and approved | type, counterparty, amounts, allocation by line | RI, FIN, DAT |
| `ClaimClosed` / `ClaimReopened` | close / reopen | outcome or reason, totals | WRK, CHN, DAT, RI, PTY, POL, FIN, MIG (`ClaimClosed`) |
| `CatEventAssigned` | claim tagged/untagged | cat code, method, previous code | RI, DAT, FIN |
| `FraudScoreReceived` | rules or model score stored | band, source, model version, checkpoint — no features | WRK, DAT |
| `StatutoryOfferDue` / `StatutoryOfferBreached` | CMP `ClockWarned`/`ClockBreached` for offer clock | exposure, claimant, clock instance, deadline | DAT (claim facts only; staff work items come only from WRK trigger table on CMP clock events, XMR-F-105) |
| `ClaimsHistoryCertificateIssued` | certificate issued (key certificate_id) | id/number, requester, period, document id | DOC, CHN, CMP, DAT |
| `ClaimUpdated` (R-39) | sub-status, milestone, handler, segment, flags change; within 5 s of milestone (REQ-CLM-241) | changed attribute groups, milestone, sub-status | CHN, DAT, RI |
| `CoverageDecisionRecorded` (R-39) | coverage decision approved | exposure, decision, reasons, decision maker | DAT |
| `TransactionSetRejected` (R-39) | set rejected or expired | set id, reason | DAT |
| `CatEventDeclared` / `CatEventChanged` (R-39) | event activated/changed/closed (key cat_event_id) | code, perils, period, area, status | RI, DAT, UW |
| `VendorAssigned` / `VendorInvoiceApproved` (R-39) | service accepted / invoice approved | service id, vendor, type; invoice id, amount | DAT |
| `SiuCaseOpened` / `SiuCaseConcluded` (R-39) | SIU lifecycle (key first linked claim) | case id, outcome code (no narrative) | DAT (restricted) |
| `FriendlySettlementSubmitted` (R-39) | FS case submitted to clearing | fs case id, counterparty, clearing value | DAT |
| `StatutoryOfferIssued` (R-39) | reasoned offer/reply delivered (proven delivery) | exposure, kind, delivered at, document id, delivery evidence id | CMP, DAT |

Migration events flagged `origin=MIGRATION` (REQ-CLM-010, 237, 266).

### 5.2 Consumed (53 types, §8.2)

| Event(s) | From | Reaction |
|---|---|---|
| `TransactionReversed`, `TransactionReapplied`, `PolicyChanged`, `PolicyCancelled`, `PolicyReinstated`, `PolicyRewritten`, `PolicyVoided` | POL | re-verification detection (one per claim+cause), refresh policy status indications |
| `PartiesMerged`, `PartyUnmerged`, `PartyUpdated` | PTY | re-point references idempotently (REQ-CLM-070) |
| `SanctionsHitRaised`, `SanctionsHitCleared` | PTY | hold/release pending payments |
| `VulnerabilityStatusChanged`, `ConsentChanged`, `CommunicationPreferenceChanged` | PTY | refresh handling rules, channel |
| `DisbursementIssued/Cleared/Rejected/Stopped/Voided/Returned` | BIL | payment status; publish `PaymentIssued`/`PaymentVoided`; for `FS_CLEARING` update statement net status |
| `PaymentReceived`, `CashAllocated` | BIL | record deductible/salvage recoveries; settle FS net receivable |
| `ClockStarted/Warned/Met/Breached/Elapsed` | CMP | show clocks; publish offer due/breached; start interest accrual; `CLM_FS_COUNTERPARTY_REPLY` is a WAITING_PERIOD clock whose `ClockElapsed` triggers FS escalation; others are DEADLINE kind (R-78) |
| `FiscalDocRegistered`, `FiscalDocRejected` | CMP | store MARK; rejections to ops view |
| `DocumentLinked`, `DocumentReceived`, `DocumentClassified`, `ActivityCompleted` | WRK | checklists, photos, FNOL hand-off, first-contact milestone |
| `DocumentDelivered`, `DeliveryFailed`, `DocumentRendered` | DOC | offer delivery evidence (stops `CLM_MTPL_OFFER`, starts `CLM_MTPL_PAYMENT_DUE`), certificate delivery |
| `RecoveryCalculated` | RI | `RiRecoveryView` |
| `JournalPosted` | FIN | set → Posted |
| `DisclosureFindingDecided`, `PolicyHoldActivated/Released` | UW | show finding/factor; dashboard holds |
| `AiToggleChanged`, `AiKillSwitchActivated` | PLT | hide AI within 60 s (REQ-CLM-258) |
| `AuthorityGrantChanged`, `ApprovalDecided` | PLT | re-route pending sets; execute approved sets |
| `LegalHoldApplied/Released` | PLT | block purge |
| `CoexistenceMasterChanged` | MIG | FNOL routing cache |
| `ModelDriftDetected`, `BiasThresholdBreached` | DAT | auto-disable/flag AI |
| `ConfigChanged`, `PackActivated` | PLT, MKT | refresh rules and code lists |

Not consumed: `EvidencePackSealed` (DOC).

---

## 6. APIs

### 6.1 Exposed (§9.1; all state-changing commands idempotent, money/cover/legal-status commands support dry-run per contract §3.5.3)

| Operation | Purpose | Req |
|---|---|---|
| `clm.Fnol.submit/saveDraft/get/validate` | FNOL for all channels (staff, portal/app, broker, partner API, WRK hand-off, FS clearing, migration) | 001 |
| `clm.Claim.get/update/close/reopen/merge` | lifecycle | 061, 071…074, 042 |
| `clm.Claim.search` | owner search (R-07), ≤1 s p95, hidden claims never counted | 011 |
| `clm.Exposure.create/update/close/reopen` | exposures | 062, 063, 072 |
| `clm.Coverage.decide/reverify` | decisions, re-verification | 051, 058 |
| `clm.TransactionSet.build/submit/approve/reject/return` | financial sets | 003, 107…113 |
| `clm.Payment.void/stop/reissue` | corrections (stop executed by BIL) | 125…127, 136 |
| `clm.Redress.request` | CMP claim-related complaint redress paid as ex-gratia | 265 |
| `clm.Financials.get(claimId, asOf)` | time-travel balances | 101 |
| `clm.Financials.dailyTotals(legalEntity, accountingDate)` | FIN reconciliation | 106 |
| `clm.Recovery.*` | recoveries | 143…151 |
| `clm.FriendlySettlement.*` | eligibility, submission, statements, disputes, net approval and BIL hand-over | 155…160, 264 |
| `clm.StatutoryOffer.issue/recordAcceptance/recordRepairAgreement` | offers, acceptance (CHN calls), repair in kind | 166, 167, 263 |
| `clm.Certificate.request/assemble/issue` | claims-history certificate | 008, 170…173 |
| `clm.ClaimTracking.get/list` | customer-safe status, ABAC-filtered; staff variant | 009 |
| `clm.CatEvent.*`, `clm.CatEvent.aggregate` | events, RI/DAT aggregates | 006, 210…215 |
| `clm.Service.request/update/cancel`, `clm.VendorInvoice.submit/decide` | vendors via CHN partner API | 188…200 |
| `clm.Fraud.score` (outbound hook), `clm.Siu.*` | fraud, SIU | 201…209 |
| `clm.Litigation.*` | matters | 218…223 |
| `clm.Conversation.send/schedule/list` | messaging | 181…184 |
| `clm.Import.claim/financialHistory/convertCurrency/reverse` | migration (R-77), changeover (P4), rollback | 010, 237, 266 |
| `clm.Dsar.export/rectify/restrict/purge` | GDPR rights via CMP fan-out | 232, 233 |

No payee-account API in CLM (removed; R-38, R-82). Error codes named: CLM-ERR-FNOL-001, -FNOL-CONSENT, -IDEMPOTENCY-MISMATCH (409), -ILLEGAL-TRANSITION, -CLOSE-GUARD, -SEARCH-CRITERIA, -EXPOSURE-DUPLICATE, -AUTHORITY, -RESERVE-REASON, -NOT-PAYABLE, -DUPLICATE-PAYMENT, -SET-STALE, -SOD, -NOT-STOPPABLE, -POLICY-UNVERIFIED, -ALLOCATION, -FS-DISABLED, -OFFER-CONTENT, -CERT-REQUESTER, -CAT-AREA, -INVOICE-DUPLICATE, -SIU-ACCESS, -CONTACT-PROHIBITED, -NO-CONSENT, -IMPORT-VALIDATION, -REVERSE-LIVE-ACTIVITY, -HELD.

### 6.2 Consumed — see section 4 table (POL, PFC, PTY, BIL, CMP, DOC, WRK, PLT, MKT, UW, FIN, RI, MIG).

### 6.3 External integrations (§9.3)

EAEE FS clearing office (via `FriendlySettlementClearing` adapter in PLT hub; file or API "per EAEE specification"; to be specified, OI-CLM-01); Green Card bureau and foreign correspondents and Auxiliary Fund (via `MotorCompensationBodyAdapter`; e-mail/structured files today; to be specified, OI-CLM-05); fraud-scoring service (optional, EU-hosted, REST via PLT hub/AI gateway); estimating platforms and repair networks (REST/webhooks via PLT hub, P1 Should); messaging providers (via DOC delivery and PLT hub); assistance/towing (CHN partner API); AADE myDATA (via CMP `FiscalDocumentChannel`).

---

## 7. SPIs / country-pack interfaces (§10.4, §9.2)

| SPI | Use | Notes |
|---|---|---|
| `FriendlySettlementClearing` | `evaluateEligibility(claimFacts)`, `submit`, `submitDispute`, `recordReply`, `receiveNotification`, `settlementStatement` | Defined by MKT (REQ-MKT-109, 313; PRD-17 §9.4.23 extended §9.4.43); operations extended by CCR-CLM-04 / R-42. Cyprus stub returns `NotApplicable`. Italy's direct-indemnity scheme cited as another implementation |
| `MotorCompensationBodyAdapter` | `notify`, `requestReimbursement`, `receiveNotification`, `settlementStatement` | REQ-MKT-310, PRD-17 §9.4.33, R-41 (CCR-CLM-03), R-87 |
| `ClaimsHistoryFormat` | certificate template sections, `retentionYears` | REQ-MKT-108 |
| `StatutoryClockSet` (via CMP) | clock values | REQ-MKT-009 |
| `FiscalDocumentChannel` (via CMP) | settlement receipt; Cyprus returns NotRequired | |
| `PayeeVerification` (via BIL PaymentInstrument) | VoP | R-38 |
| `SanctionsListSource` (via PTY) | screening | |
| `NumberingScheme` (via PLT) | claim numbering | |
| `Geocoder`, `AddressFormatter`, `NameTransliterator`, `HolidayCalendarProvider`, `FxRateSource` | location, Greek postcode, Greek/Latin, business-day clocks, FX | |
| `InboundDocumentProfile` (via WRK), `ComplaintRules` (via CMP), `DocumentLanguageRule` | doc classes, complaint wording, letter language | |
| `MotorDataProvider` (through POL) | vehicle market value for total-loss proposal | REQ-CLM-247 |

Capability switch `cap.clm.friendly_settlement` (off by default, on in GR; REQ-CLM-156, REQ-MKT-004). Core vs pack split listed in §10.4: core holds claim/exposure/financial model, transaction sets, payments pipeline, recoveries framework, clocks integration, certificate mechanism, vendors, litigation, cat, fraud, tracking; pack holds FS, fund/bureau, clock values, statutory interest, offer content rule, certificate format, fiscal rules, cost categories, checklists, payment methods, SMS script handling, complaints wording.

---

## 8. Screens (§6)

| ID | Name | Personas | One line |
|---|---|---|---|
| SCR-CLM-01 | FNOL intake (staff) | ROLE-16, 17, 05/06 | Split-pane: policy card at loss date + sectioned form; live coverage/duplicate/fraud validation; exposure proposals; vendor booking; confirmation |
| SCR-CLM-02 | Claim workspace and summary | ROLE-17, 18, 20, 21 | Context bar, hero net-incurred metric, cards (basics, financials, high-risk, exposures, services, activities, notes, parties, clocks), AI summary/NBA; landing queue with KPI strip |
| SCR-CLM-03 | Claim search | all staff | Criteria (claim/policy no., party, AFM, plate/VIN, dates, status…), results, saved searches, export, bulk assign |
| SCR-CLM-04 | Parties involved | ROLE-16, 17, 20 | Contacts grid, roles, contact-prohibited, PTY identity read-only, transfer roles |
| SCR-CLM-05 | Exposures | ROLE-17, 18 | Coverage × claimant detail, decision dialog, clocks, balances; issue offer/reply, record acceptance |
| SCR-CLM-06 | Financials: reserves and balances | ROLE-17, 18, 35 (read) | Statement view by line; reserve side panel builds set with before→after; as-of view; AI reserve suggestion |
| SCR-CLM-07 | Payments | ROLE-17, 18, finance clerk | Payment side panel: payee, co-payees, lines, method, BIL instrument, checks panel (sanctions, VoP, duplicate, limit, authority, clocks); void/stop/reissue |
| SCR-CLM-08 | Recoveries | ROLE-20 | Dashboard, milestones, subrogation decision, arbitration/award, receipts, write-off |
| SCR-CLM-09 | FS queue and counterparty balances | ROLE-20, 18 | Exception-first; balances per insurer; statement reconciliation; approve net settlement and hand to BIL; only when FS switch on |
| SCR-CLM-10 | Diary and statutory deadlines | ROLE-17, 18, 45 | Priority list + calendar, clocks pinned with countdown, team view |
| SCR-CLM-11 | Documents and photos | ROLE-17, 19, 16 | Checklist beside DOC Documents tab component, gallery, request docs, waive, copies |
| SCR-CLM-12 | Services: vendor assignment and service request | ROLE-17, 18, 19 | Master–detail; pre-filled service request; ranked vendor proposals with map; status, history, metrics, docs; assessment clock |
| SCR-CLM-13 | Vendor invoices | ROLE-17, 18 | Line items vs estimate, reserve line, auto-pay check results, MARK/addressed-to |
| SCR-CLM-14 | Approval inbox | ROLE-18, senior handlers | Priority queue with sidebar views (payments, reserves, recoveries, coverage, reopenings, four-eyes, bulk), keyboard nav, split-pane decision detail, approve/return/reject |
| SCR-CLM-15 | Catastrophe event management and dashboard | ROLE-18, 45, 27 | Event dialog, re-tag preview, live dashboard, map, surge controls, bulk message, legal hold |
| SCR-CLM-16 | Fraud alerts and SIU case | ROLE-21, 18 | Alert list, scorecard, indicators, relationship graph, SIU case workspace, payment hold |
| SCR-CLM-17 | Litigation | BI handler, ROLE-18, ROLE-46 | Matters, counsel, hearings, budget/costs, settlement authority |
| SCR-CLM-18 | Claims-history certificate issuance | ROLE-03, 17, 29 | Request queue with clock, assembled preview per SPI template, review items, issue/correct |
| SCR-CLM-19 | Claim conversation | ROLE-16, 17, 20 | Docked thread per claim+party, templates, scheduled send, GSM-7/UCS-2 segment counter, translation, AI draft |
| SCR-CLM-20 | Customer/broker reporting and tracking (content contract for CHN) | ROLE-01, 02, 05, 06, repairer | What CHN shows/captures; never reserves, fraud, SIU, internal notes |

**Design-guide patterns referenced:** IB-01 command palette, IB-02 keyboard list nav, IB-03 sidebar views with counts, IB-04 priority-ranked queue, IB-05 split pane, IB-07 approve-the-diff, IB-08 explanation, IB-09 AI summary with citations, IB-10 streaming draft, IB-12 reconciliation progress, IB-13 statement view, IB-14 edit in place with autosave/undo, IB-15 relationship graph, IB-16 density, IB-17 hero metrics, IB-18 side panel/overlay, IB-19 presence, IB-21 status, IB-23 state-change motion, IB-24 quiet chrome, IB-25 task-adaptive layout, IB-26 global filter bar, IB-28 exception-first list, IB-29 log-style dense table, IB-32 binary status list, IB-33 theme preference. Semantic states only (contract §3.9.9). Shell is PLT staff shell (REQ-PLT-327). Money, coverage decisions, closure and reopening never edit in place (§6.0). Language switch "ΕΛ | EN" on every screen without reload or loss of input (R-101); customer documents follow customer preference/`DocumentLanguageRule`, not the UI switch. Permissions list (§6.0): `clm.read`, `clm.fnol`, `clm.handle`, `clm.financials.read`, `clm.reserve`, `clm.pay`, `clm.recover`, `clm.approve`, `clm.coverage.decide`, `clm.p3.read`, `clm.siu`, `clm.fraud.denial`, `clm.cat.manage`, `clm.vendor.manage`, `clm.certificate`, `clm.ops`, `clm.payments.view`; ABAC on legal entity, line, producer scope, confidentiality, assignment.

Inventory (§6.21, §15.3): 18 owned items (SCR-CC-01…06, UIL-C1…C12); 16 specified, 2 wholly replaced (SCR-CC-02 Hi Marley case → SCR-CLM-19; SCR-CC-04 vendor form → pre-filled service request). Field-level N/A in GR: Tax Filing Status, Work Status, FROI Snapshot; Gender not captured.

---

## 9. Regulatory, tax and statutory rules

### 9.1 Stated explicitly in the source

| Rule | Value as stated | Source / IDs |
|---|---|---|
| MTPL reasoned offer / reasoned reply | Within **three months** of the claim: reasoned offer when liability not contested and damage quantified, otherwise reasoned reply; interest payable when offer late | Directive 2009/103/EC Art. 22 (as applied by Art. 19); Greek P.D. 237/1986 Art. 6 par. 6; BoG Act 87/5.4.2016 (FEK B 1109/19.4.2016) Art. 3 (OBL-MOT, F1, F2, C-03). Clock `CLM_MTPL_OFFER` **per third-party MTPL claimant exposure** (not per claim), started at claim **receipt date** (not registration date), stopped Met on **proven delivery** (not rendering) of reasoned offer or reply (REQ-CLM-007, 033, 165, 166; BR-CLM-020). Example: received 2027-02-01 → deadline 2027-05-01. Applies to exposure kinds MTPL_PD, MTPL_BI (`clm.offer.exposure_kinds`) |
| Claim submission channels and receipt evidence | Receipt evidence recorded; acknowledgement with claim number | Act 87/2016 Art. 4 (REQ-CLM-036) |
| MTPL material-damage expert assessment | **15 days** (accident in Greece) or **25 days** (abroad) **from claim submission** | Act 87/2016 Art. 5; clock `CLM_MTPL_ASSESSMENT`, stops when the handler accepts the assessor's report (REQ-CLM-191, BR-CLM-023, BR-CLM-022) |
| Offer content | amount (per head of damage), basis, place, time and method of payment; reasoned reply: points disputed, liability position, missing evidence | Act 87/2016 Art. 6; BR-CLM-019; REQ-CLM-166 |
| Payment after offer | Payment within **10 days**, counted **from proven delivery of the reasoned offer** (D7; earlier versions counted from acceptance — C-11); reasoned reply starts no payment clock; stops at `PaymentIssued` for the accepted amount | Act 87/2016 Art. 6; `CLM_MTPL_PAYMENT_DUE` (REQ-CLM-167, BR-CLM-022). Example: delivered 2027-04-10 → deadline 2027-04-20. Late acceptance → reported Breached pending legal reading (OI-CLM-13) |
| Repair in kind | Within **20 days of agreement** | Act 87/2016 Art. 6; `CLM_REPAIR_IN_KIND` from agreement to WorkComplete with claimant/assessor confirmation; cancelled if agreement withdrawn (REQ-CLM-263). Example 2027-05-03 → 2027-05-23 |
| Copies of claim documents | to insured and injured parties on request | Act 87/2016 Art. 8 (REQ-CLM-179) |
| Statutory interest | Interest after breach on amount subsequently offered or awarded, **from the deadline to payment date**, simple basis, pack day count; paid as cost type StatutoryInterest; waiver needs `CLM.INTEREST_WAIVE` + four-eyes | Directive Art. 22 (REQ-CLM-169, BR-CLM-021). **Rate not stated** (see gaps). Example formula 10,000 × R × 60/365 |
| Claims-history certificate | On request, covering **at least the last five years**, within **15 days**, on the Commission template (applies from **24 July 2025**); equal treatment of other Member States' certificates | Law 5113/2024 Art. 7 (replacing P.D. 237/1986 Art. 6γ); Implementing Reg. (EU) 2024/1855; `CLM_HISTORY_STATEMENT` (REQ-CLM-008, 170…173; BR-CLM-024/025). Example 2027-03-01 → 2027-03-16. Default 5 years configurable minimum (C-05) |
| Auxiliary Fund (Επικουρικό Κεφάλαιο) | Pays for unknown vehicles (property damage only with **death or serious injury ≥ 5 days hospitalisation**), uninsured vehicles, insurer in liquidation; recourse against liable insurer | Law 5113/2024 Art. 10–11 (replacing P.D. 237/1986 Art. 17, amending Art. 19) (REQ-CLM-161, 162; BR-CLM-026) |
| Green Card / compensation body | Greek Green Card bureau (Γραφείο Διεθνούς Ασφάλισης) is the compensation body; foreign insurers act via correspondents | Directive 2009/103/EC Art. 24; P.D. 10/2003 (secondary source) (REQ-CLM-163, 164); Art. 21 cited for claims abroad |
| Friendly Settlement (market agreement, EAEE) | Own insurer of not-at-fault driver pays and recovers from at-fault insurer via EAEE clearing office, **monthly settlement based on averages**; material damage since **1 May 2000**, minor BI since **1 Sept 2005**; joint statement no longer strictly necessary | eaee.gr (S10); REQ-CLM-155…160, 264; BR-CLM-017/018/042 |
| FS limits | **€6,500** material damage; **€12,000** per injured person; **€30,000** per accident (secondary press) vs prompt **€5,000/€15,000** — conflict C-01; **UNVERIFIED**, pack keys `gr.fs.limit.*` | F6, C-01, OI-CLM-01 |
| myDATA claim settlement receipt (Εξοφλητική Απόδειξη Αποζημίωσης) | Insurer issues and transmits; **B2C document type 13.1/13.2** with expense classification **2.3/2.5 or informational 2.95**; **B2B self-billing 2.1**; in FS the non-at-fault insurer's payment does not affect its results, the at-fault insurer records the clearing note as expense; repairers invoice the vehicle owner, not the insurer | EAEE informal myDATA working group thematic units on AADE (16.01.2022/2023) — "market guidance, not a decision" (F7, OBL-TAX, REQ-CLM-138, 197, BR-CLM-027); CMP sole issuer of fiscal series/numbering (D3); motor uses TRANSACTION trigger (OI-CLM-04) |
| VoP | Mandatory for SEPA credit transfers in euro from **9 October 2025**; no match blocks release; close match requires four-eyes; not possible requires evidence | Reg. (EU) 2024/886 Art. 5c amending Reg. 260/2012 (F9, REQ-CLM-134, BR-CLM-028) |
| Sanctions | EU restrictive measures and "Fairfax OFAC screening" before any payment; synchronous at submit and release; degraded screening (LISTS_STALE) holds payments (fail closed) | CD-12 (REQ-CLM-004, 133; BR-CLM-029) |
| Complaints | BoG Executive Committee Act 88/5.4.2016 (FEK B 1109/2016), reply **50 calendar days**; claim notification is not a complaint | OBL-COMP, F10, C-09 (REQ-CLM-187, 053) |
| GDPR special categories | Art. 9, Art. 5(1)(c), Art. 15–22; Greek Law 4624/2019 Art. 22 | REQ-CLM-079, 080, 233 |
| Prescription / retention floor | Law 2496/1997 Art. 10: **4 years** (damage), **5 years** (personal), from year end (secondary, per PRD-13); retention never shorter | OBL-CON, BR-CLM-041 |
| Proportional reduction for negligent non-disclosure | Law 2496/1997 Art. 3(5); UW factor proposed, handler confirms | REQ-CLM-054 |
| Solvency II / IFRS 17 / BoG stats data | accident year, UW year, SII LoB, national statistical class, IFRS 17 group on every financial event | OBL-SII, OBL-IFRS17, OBL-BOG (REQ-CLM-005, 227, 228) |
| AI Act | No AI decision on coverage, denial, payment, fraud; logging; human decision points; kill switch | Reg. (EU) 2024/1689 (REQ-CLM-208, 258, 259) |
| DORA | ICT third parties registered (register of information) and EU processing before activation | Reg. (EU) 2022/2554 (REQ-CLM-260) |
| EAA | WCAG 2.2 AA, plain-language status | Directive 2019/882, R-57 (NFR-CLM-013, REQ-CLM-225) |

### 9.2 Referenced but NOT specified (gaps)

- **Statutory interest rate and basis** for late MTPL compensation in Greece — not found; `gr.clm.statutory_interest.rate` UNVERIFIED (C-04, OI-CLM-03).
- **FS counterparty reply window** — prompt says 10 business days, "not found in public sources"; `CLM_FS_COUNTERPARTY_REPLY` value UNVERIFIED (C-02, OI-CLM-02).
- **FS limits, eligibility rules, clearing-value averages, dispute rules, clearing file specification** — agreement text not public (OI-CLM-01).
- **Whether the offer clock pauses** (e.g. missing claimant documents) — open (OI-CLM-06), though REQ-CLM-007 names pause/resume.
- **Late acceptance reading** for payment-due clock (OI-CLM-13).
- **Certificates at policy termination without request** — unchecked (OI-CLM-11).
- **myDATA document types/classifications** to be confirmed by tax adviser (OI-CLM-04).
- **Green Card bureau / Auxiliary Fund interface formats and channels** (OI-CLM-05).
- **Lawful basis catalogue for health data and RC-CLM-HEALTH durations** (OI-CLM-07).
- **MTPL minimum amounts** — C-03 notes Law 5113/2024 Art. 5 changed minimum amounts but no values are stated (the €1,300,000 BI limit in REQ-CLM-056 is only an acceptance-test example).
- **Bank of Greece statistical returns** status "Verify" (OBL-BOG); **eIDAS** electronic offer acceptance "Verify" (OBL-EIDAS).
- **Alternative dispute resolution body / complaints route** text for denial letters — "from the pack" (REQ-CLM-053), not given.
- **Business vs calendar days** for clocks — "per pack" (§14.x); examples all compute calendar days.
- Retention durations — from programme retention schedule (REQ-PLT-011), not given.

### 9.3 Deferred to configuration / country pack

All clock values via `StatutoryClockSet` (production activation of each Greek value requires **Settled legal status — D7 certification gate**, BR-CLM-022); FS limits/averages (`gr.fs.*`); interest rate; offer content checklist; certificate format and minimum years (`clm.certificate.min_years` = 5); fiscal receipt rules (`clm.fiscal.receipt_rules`); payment methods per payee type (`clm.payment.methods`: SEPA_CT, SEPA_INST, VENDOR, OFFSET; CLEARING for FS payables only; cheque not listed); VAT treatment by claimant VAT-recovery status (BR-CLM-040); cost categories, checklists, document classes; complaints wording.

---

## 10. Greek-market specifics

- **AFM**: FNOL policy search and claim search by AFM (REQ-CLM-047, 011; `IdValidator`); SSN → AFM mapping (§6.0); AFM excluded from AI inputs (AI-CLM-01).
- **Greek/Latin script**: search accent-insensitive in Greek or Latin ("papadopoulos" finds "Παπαδόπουλος", REQ-CLM-011); `NameTransliterator`; display names in native and Latin (REQ-CLM-231); SMS segments: Greek text counts as UCS-2, transliteration offered (REQ-CLM-182, BR-CLM-030 default 3 segments).
- **Addresses**: region (περιφέρεια), 5-digit postcode via `AddressFormatter`, regional unit used in cat areas.
- **myDATA / AADE**: settlement receipt MARK stored on payment; vendor invoice MARK and "addressed to owner" flag (REQ-CLM-138, 197).
- **EAEE**: Friendly Settlement clearing office (Γραφείο Συμψηφισμού), EAEE statistics in DAT marts (§13).
- **Auxiliary Fund**, **Green Card bureau (Γραφείο Διεθνούς Ασφάλισης)**, joint accident report (Δήλωση Ατυχήματος).
- **Bank of Greece** Acts 87/2016 and 88/2016; BoG statistical returns.
- **gov.gr**: not mentioned. **Information Centre / bureau (e.g., uninsured-vehicle information centre)**: not mentioned beyond Green Card bureau.
- **EUR**: functional currency EUR; group currency **USD** (Fairfax reporting, REQ-CLM-103); BGN appears only in FX examples (REQ-CLM-140, 237).
- **Language**: all UI strings, letters, SMS templates and status texts in Greek and English before release (NFR-CLM-014); certificate rendered in Greek with English version (J-08, REQ-CLM-172); † marks working Greek translations needing ROLE-46 review.
- VAT on indemnity depends on claimant VAT-recovery status (Greek motor practice, REQ-CLM-139).
- Not applicable in GR: workers' compensation fields (Work Status, FROI, 3-point contact), Tax Filing Status; Gender not captured.

---

## 11. Controls (§12)

**Authority types registered with PLT (REQ-PLT-100):**

| Type | Dimensions | Notes |
|---|---|---|
| `CLM.RESERVE` | product, line, exposure kind, cost type, amount (exposure total incurred), currency, handling segment | per exposure total after change |
| `CLM.PAYMENT` | product, line, cost type, amount per payment, cumulative per claim, currency | daily aggregate (REQ-PLT-107) |
| `CLM.COVERAGE_DECISION` | line, decision (NotCovered, CoveredWithReservation), exposure amount band | denial authority |
| `CLM.SETTLEMENT` | line, amount | litigation settlements, separate from payment (REQ-CLM-222) |
| `CLM.EXGRATIA` | line, amount | plus four-eyes |
| `CLM.RECOVERY_WRITEOFF` | type, amount | |
| `CLM.LIMIT_OVERRIDE` | line (flag) | payments above remaining limit |
| `CLM.REOPEN` | days since closure | |
| `CLM.COPAYEE_REMOVE` | line (flag) | |
| `CLM.INTEREST_WAIVE` | country (flag) | plus four-eyes |
| `CLM.FASTTRACK` (system profile) | line, amount | straight-through; defaults €600 glass, €1,500 own damage (BR-CLM-014) |
| `CLM.FS_NET_SETTLEMENT` | counterparty, amount, direction | four-eyes above €50,000 default (BR-CLM-042) |

Also BIL's own authority type governs disbursement stops (REQ-CLM-126, 136).

**Maker-checker list (REQ-PLT-004):** payments/reserves above handler authority; any set above four-eyes threshold (BR-CLM-010, default €50,000) regardless of maker's authority; payments to a payee account changed within BIL's cooling-off window; VoP close-match releases; ex-gratia (incl. complaint redress); statutory interest waivers; FS net settlement above threshold; recovery write-offs above threshold; cat event activation and area changes; SIU hold release (handler + SIU lead, BR-CLM-033); configuration changes to average reserve tables, segmentation rules, fraud thresholds, fast-track profiles; FS difference acceptance above threshold. Approver may not edit amounts — must return to maker (REQ-CLM-116). Approval bound to content hash; re-validation at approval (REQ-CLM-109, 112). Bulk approval only for bulk-eligible fast-track sets (REQ-CLM-115). Sets expire after configured business days (REQ-CLM-117).

**SoD (§12):** SOD-CLM-01 payment maker ≠ approver; -02 user who changed payee account ≠ maker/approver of payments to it (REQ-CLM-132, `CLM-ERR-SOD`); -03 finance payments clerk cannot create/approve claim payments; -04 SIU investigator on a case cannot approve payments on its claims; -05 vendor-management admin cannot approve invoices of vendors they on-boarded within 90 days; -06 compliance users clearing sanctions false positives cannot release claim payments (aligned SOD-PTY-03); -07 author of reserve-table/segmentation changes ≠ approver.

**Audit specifics (REQ-PLT-002):** FNOL submission; coverage decisions with check id; re-verification (old/new refs, diff hash); every set state change with maker, approvers, hash, check ids; payee account create/change (masked before/after, evidence); holds/releases; void/stop/reissue; P3 and SIU read access; close/reopen; cat activation/area change; certificate issue/correction; offer issue/acceptance; message send metadata; exports; AI interactions by reference.

**Payment safety controls:** payable guards (REQ-CLM-130, BR-CLM-013); duplicate payment detection (REQ-CLM-129, BR-CLM-012, 30-day window); bank-account change as controlled change through BIL, VoP, step-up/document evidence, four-eyes during BIL `cooling_off_until` — CLM holds no cooling-off value (REQ-CLM-131, BR-CLM-011, XMR-F-234); payee indicators for parties not on claim, staff accounts, shared accounts (REQ-CLM-135).

**GDPR handling:** P3 injury/medical fields field-level encrypted, restricted to `clm.p3.read` + assignment/SIU relationship, masked elsewhere, excluded from events and AI unless registered (REQ-CLM-079, NFR-CLM-009/010); lawful basis per injured person recorded before medical documents requested (REQ-CLM-080); DSAR export/rectify/restrict/purge with SIU exemption (REQ-CLM-233); retention classes with legal holds (REQ-CLM-232); SIU data hidden from tracking, search counts and exports, all access audited (REQ-CLM-206); search never reveals hidden claims in counts (REQ-CLM-011).

---

## 12. AI features (§11)

All ship **off** at tenant level, run via PLT model gateway to EU-hosted endpoints with zero retention, write `AiInteractionRecord`, registered in CMP AI register (REQ-CMP-007) and DAT model registry (REQ-DAT-005), kill switch ≤ 60 s (REQ-CLM-258), human Accept/Edit/Reject required (REQ-CLM-259). Module works fully without AI.

| ID | Feature | Classification | Phase / MVP need | Fallback |
|---|---|---|---|---|
| AI-CLM-01 | FNOL intake assistant (text, transcript, photos → fields) | Limited risk (Art. 50) with high-risk controls for customer-facing use | P1 (pilot, off) — not required | Standard form |
| AI-CLM-02 | Photo damage assessment assist | High-risk controls by default | P2 — not MVP | Assessor rules |
| AI-CLM-03 | Reserve suggestion with comparables | High-risk controls | P2 — not MVP | Average reserve table |
| AI-CLM-04 | Fraud propensity score | High-risk controls (classification uncertain) | P3 ("AI scoring off by default" in P1; §1.5 table lists AI-CLM-04 under P3, fraud row says "AI scoring pilot" in P2) — not MVP | Rules (REQ-CLM-201) |
| AI-CLM-05 | Claim brief (summary with citations) | Minimal risk | P1 (pilot, off) — not required | Standard panels |
| AI-CLM-06 | Correspondence drafting | Limited risk | P2 — not MVP; offers keep structured DOC template | Templates |
| AI-CLM-07 | Payment anomaly detection | High-risk controls | P3 — not MVP | REQ-CLM-129, 135 |
| AI-CLM-08 | Next-best-action | High-risk controls (classification uncertain) | P2 — not MVP | Diary priorities, deadline risk |

None is needed for MVP. Monitoring profiles MP-A/B/C/E (PRD-15), bias bands 0.8–1.25. Customer transparency texts given in EN and GR for each customer-relevant feature.

---

## 13. Open issues, assumptions, CCRs (§16)

**Open issues:**

| ID | Issue | Status |
|---|---|---|
| OI-CLM-01 | FS agreement text, limits, clearing value, disputes, file spec | Open (cash path closed per D1) |
| OI-CLM-02 | FS reply window | Open |
| OI-CLM-03 | Statutory interest rate/basis | Open (D7 gate) |
| OI-CLM-04 | myDATA settlement receipt treatment | Partly closed per D3; doc types open |
| OI-CLM-05 | Green Card bureau / Auxiliary Fund interfaces | Open (operation names fixed by MKT) |
| OI-CLM-06 | Offer clock pause rules | Open |
| OI-CLM-07 | Health-data lawful basis, RC-CLM-HEALTH durations | Open (DPIA) |
| OI-CLM-08 | Unverified statistics | Closed per D8 |
| OI-CLM-09 | Act 88/2016 text | Closed |
| OI-CLM-10 | Payee account ownership | Closed by R-38 |
| OI-CLM-11 | Certificate at termination without request | Open |
| OI-CLM-12 | AI-CLM-08 registrations | Closed |
| OI-CLM-13 | Late acceptance and payment-due clock | Open; interim: Breached with acceptance date as evidence |

**Assumptions:** A-1 MVP = private cars/light vehicles (fleets, heavy vehicles in P3); A-2 no cheques; A-3 clearing office provides machine-readable monthly statement per counterparty; A-4 WRK provides claims mailbox and doc classes; A-5 average reserve tables are configuration, not models; A-6 vendors via CHN partner APIs/vendor portal; A-7 external fraud scoring EU-hosted and DORA-registered.

**Risks:** RK-CLM-01…08 (FS rules late; account-takeover fraud; cat surge; clock misconfiguration; P3 leakage; FIN period-end breaks; BIL boundary drift; AI perceived as deciding).

**CCRs (all Accepted):** CCR-CLM-01 new events + partition keys (R-39, R-100, R-102); CCR-CLM-02 clock codes `CLM_MTPL_ASSESSMENT`, `CLM_MTPL_PAYMENT_DUE`, `CLM_FS_COUNTERPARTY_REPLY` (R-40; D7 added `CLM_REPAIR_IN_KIND` and moved payment-due start); CCR-CLM-03 `MotorCompensationBodyAdapter` (R-41); CCR-CLM-04 `FriendlySettlementClearing` operations extended (R-42); CCR-CLM-05 fiscal documents sourced from claim payments (R-43).

**Ten pre-build decisions (§16.5):** (1) financial model — closed per D10, motor cost-category lists still to approve; (2) R-38 split contract tests; (3) authority types, default limits, four-eyes threshold; (4) FS agreement and whether MVP joins FS clearing at go-live; (5) interest rule, pause rules, late-acceptance reading; (6) myDATA doc types; (7) fraud approach (rules vs external service) and SIU model; (8) vendor integration partners and vendor-portal scope; (9) migration scope for closed claims and financial history granularity (scenario B, D6); (10) cat operating model and design-event replay as go-live gate.

---

## 14. Conflicts and ambiguities found

### (a) With infra/stack (ARCHITECTURE-DECISIONS.md)

1. **Broker vocabulary.** §8: "Topic `clm.events.v1`. Partition key `claim_id`…"; §14: programme event model "≈ 60 million events per year (designed for 180 million, ≥ 2,000 events/s sustained)". The stack has **no message broker** — a PostgreSQL transactional outbox dispatched in order to in-process handlers. "Topic/partition key" must be reinterpreted as outbox stream name and per-aggregate ordering key; 2,000 events/s sustained through a PostgreSQL outbox with in-process handlers needs an explicit capacity check.
2. **"Workflow engine".** SYS-02 "Workflow engine — run statutory clock hooks, recurring payments, cat surge timers, clearing cycles" (§2); §9.2 outbound "plt… workflow engine"; REQ-CLM-083, 094, 124, 201 require `REQ-PLT-007` (decision tables / workflow). Stack forbids workflow servers/BPM engines; timers must be Hangfire jobs with deadlines as domain records, decision tables as plain versioned config + pure functions. Needs a ruling on what "PLT workflow engine" means in code.
3. **"PLT integration hub" / "AI gateway" / "model registry"** (REQ-CLM-195, 260, §9.3) — ambiguous as to whether an ESB/iPaaS is implied; in the stack this should be in-process adapters. Flag for clarification only.
4. **Geospatial.** Cat area as polygon, auto-tagging by location, vendor ranking by distance, map pins (REQ-CLM-189, 210, 211). PostGIS (or equivalent) is not named in the stack; a decision is needed (extension availability on Azure PostgreSQL Flexible Server, or a simpler region/postcode model).
5. **Messaging (SMS) provider.** Two-way SMS (REQ-CLM-181, 184) — stack names Azure Communication Services for e-mail only; SMS provider for Greek numbers is undecided.
6. **E-signature / step-up for offer acceptance** (REQ-CLM-167, OBL-EIDAS "Verify") — e-signature provider not in stack.
7. **Vendor products** appear only as research/baseline (Guidewire ClaimCenter S15; Hi Marley in baseline screens, replaced). No conflict, but the financial model is explicitly "Guidewire-shaped" (REQ-CLM-003 rationale).
8. **Time-travel balances** (`clm.Financials.get(asOf)`, REQ-CLM-101) must be implemented over append-only rows by record time — consistent with stack, but note the claim itself is "not bitemporal" (§7.0).

### (b) With system contract / other PRDs

1. **Payment status vocabulary** differs: REQ-CLM-004 lists disbursement statuses "Requested, Released, Issued, Cleared, Rejected, Stopped, Voided, Returned"; §7.3.3 uses Pending/Approved/OnHold/Submitted/Issued/Cleared/Stopped/Voided/Returned/DisbursementRejected/Settled; §7.1 transaction status enum lists "Pending, Approved, Rejected, Expired, Submitted, Issued, Cleared, OnHold, Stopped, Voided, Returned, DisbursementRejected" — **"Settled" (CLEARING) is missing from the entity enum**. Must be reconciled with BIL Disbursement states (contract §3.2.4, PRD-18 §9.2.5).
2. **Group currency USD** (REQ-CLM-103, "Fairfax reporting") and "Fairfax OFAC screening" — group-specific requirements; confirm they are contract rules.
3. **Clock kinds**: offer, assessment, payment-due, repair-in-kind, certificate are DEADLINE; `CLM_FS_COUNTERPARTY_REPLY` is WAITING_PERIOD (§8.2, R-78, PRD-11 §10.5 row 13) — CMP must support both.
4. **`StatutoryOfferDue`/`Breached` are not work triggers** — WRK creates warning activities only from CMP clock events (XMR-F-105); builders must not wire CLM events to WRK.
5. **FS cash path**: CLM must never move or post cash (D1); BIL REQ-BIL-354…357 must exist with source `FS_CLEARING`, method `CLEARING`.
6. **DOC** may drop CLM as consumer of `EvidencePackSealed` (§8.2) — cross-PRD cleanup.
7. Personas used in screens but not in §2 persona table: ROLE-45 (SCR-CLM-10, 15), ROLE-27 (SCR-CLM-15), ROLE-03 (SCR-CLM-18), ROLE-46 (SCR-CLM-17) — check contract role list.

### (c) Internal contradictions

1. **Exposure close guard vs breached clocks.** REQ-CLM-072 requires statutory clocks "Met or Cancelled" to close an exposure; a Breached offer clock (interest paid) is neither, so an exposure with a past breach could never close. Needs a rule (e.g., Breached-and-settled counts).
2. **ReserveLine key**: REQ-CLM-093 / glossary define line = exposure × cost type × cost category; §7.1 uniqueness includes **currency**. Multi-currency lines (REQ-CLM-140) make this matter.
3. **NFR-CLM-009** requires "field-level encryption of IBANs" in CLM, but CLM stores no IBAN (R-38, REQ-CLM-123, §7.0). Stale text.
4. **Offer clock pause**: REQ-CLM-007 says "start, pause, resume and stop", J-03 state diagram has no Paused, and pause rules are open (OI-CLM-06). J-03 diagram also lacks Running → Breached without a warning.
5. **Reasoned reply document type**: REQ-CLM-166 uses `DT-CLAIM-DENIAL` for a reasoned reply, the same type as a coverage denial letter (REQ-CLM-053) — ambiguous template/evidence semantics.
6. **Draft claims and numbering**: REQ-CLM-038 creates "a claim in Draft" for partner/repairer FNOL; REQ-CLM-043 issues numbers only at submit; §7.3.1 Draft → Closed(Withdrawn) "no claim number". Unclear whether a Draft claim is a persisted Claim row (with id, without number) or an FNOL draft; affects REQ-CLM-039 drafts too.
7. **Stale approval** (REQ-CLM-112) returns "the set to Draft" (same set) whereas return (REQ-CLM-111, §7.3.2) creates a copy; transition not in §7.3.2.
8. **AI phase table**: §1.5 places AI-CLM-04 in P3 and "AI scoring pilot" in P2 in the fraud row; P1 lists "AI scoring off by default".
9. **Statistics**: change log v1.0 says 41 BRs; current count 42 (BR-CLM-042 added in v1.4) — consistent after update, just note.
10. **Calendar vs business days**: all examples compute calendar days (15/10/20 days), §14.x says certificate clock "business vs calendar days per pack"; the FS reply window is stated in business days (C-02).

### (d) Cannot be built without a decision

- Statutory interest rate/basis/day count (OI-CLM-03) — blocks REQ-CLM-169 production values.
- Greek clock values need Settled legal status before production activation (D7 gate).
- FS limits, averages, eligibility rules, statement file format, reply window (OI-CLM-01, -02) — blocks REQ-CLM-155…160, 264 in production; also whether MVP joins FS clearing at go-live.
- Green Card bureau and Auxiliary Fund message formats (OI-CLM-05).
- myDATA document types/classifications (OI-CLM-04).
- Offer clock pause semantics (OI-CLM-06) and late-acceptance reading (OI-CLM-13).
- Default authority limits and four-eyes thresholds (§16.5 item 3); motor cost-category list (item 1).
- Fraud approach (rules only vs external EU service) and vendor integration partners (items 7, 8).
- Geospatial approach, SMS provider, e-signature provider (stack gaps above).
- Who is the "maker" for system-initiated sets: fast-track system profile (REQ-CLM-087, 238), recurring instalments "without re-approval" (REQ-CLM-124), auto-release on online acceptance "under the handler's standing authority" without handler action (REQ-CLM-248) — SoD/maker-checker semantics for system actors need a ruling.
- Migration: summary vs full financial history (item 9).

---

## 15. Build notes

**Hardest parts.**
1. **Claim financial engine**: append-only transactions on reserve lines, transaction sets approved as a unit with per-transaction authority checks (incl. cumulative/daily aggregates), content-hash approvals, stale re-validation, automatic reserve top-up and final release, reversal-based voids, three-currency amounts with FX ids, derived balances with same-transaction projections, nightly invariant check, `asOf` time travel, gap-free per-claim event sequence. Property-based tests over ≥1,000,000 generated sequences per release (§14.x) and mutation testing apply.
2. **Payment status loop with BIL**: screening at submit and release, VoP outcomes, cooling-off read from BIL, holds, stop/void/return/reject → `PaymentVoided` + reversal + reissue, idempotent consumption, daily CLM↔BIL and CLM↔FIN reconciliation by 06:00 (NFR-CLM-019).
3. **Statutory clocks**: six CMP clocks with start/stop evidence from DOC delivery proof and BIL/WRK events; queued clock starts replayed with original receipt time across failover (NFR-CLM-007); deadline-risk and interest computation.
4. **Friendly Settlement**: eligibility SPI, own-insurer receivables at clearing value with gain/loss line, at-fault payables with `CLEARING` method, monthly statement matching and exceptions, net per counterparty approved and handed to BIL — with the agreement text still unknown.
5. **Catastrophe throughput**: 1,000 FNOL/hour for 24 h with FNOL p95 ≤ 1.5 s, events to RI/FIN within 5 s p95, dashboard freshness ≤ 10 s, auto-tagging and re-tagging.
6. **Degraded-mode FNOL** (T1): accept FNOL when POL/PTY/WRK degraded, with queued verification and assignment.
7. **Migration**: import with same validation as live, recreated clocks, `reverse` refusing claims with live activity.

**Must exist first.** PLT: authority check, approval with content hash, audit, numbering, time service, FX rates, rounding (MKT), retention/legal hold. POL `pol.Snapshot.get`/search; PFC coverage terms and regulatory mapping; PTY party, roles, screening; BIL disbursement API + events and PaymentInstrument; CMP clock register with CLM codes and fiscal document request; DOC rendering/delivery proof/document types (`DT-CLAIM-ACK`, `-OFFER`, `-DENIAL`, `-SETTLEMENT`, `-CLAIMS-HISTORY`, `-RESERVATION-OF-RIGHTS`); WRK activities/participants/inbound documents; outbox infrastructure; MKT SPI registry with Greece and Cyprus stub packs.

**Suggested slicing.**
1. Claim/exposure/incident/contacts model, state machine, FNOL API (submit/draft/validate, idempotency, dry-run), snapshot reference, coverage indications, numbering, `ClaimReported`/`ExposureCreated`, search, tracking query (REQ-CLM-001, 002, 030…050, 061…075, 009, 011).
2. Financial core: reserve lines, transactions, transaction sets, authority/four-eyes, derived balances, invariants, financial events, FIN daily totals, `asOf` (REQ-CLM-003, 005, 093…118).
3. Payments: payees/co-payees, BIL disbursement loop, screening, VoP, account-change controls, void/stop/reissue, duplicate checks, fiscal receipts via CMP, VAT, deductibles (REQ-CLM-004, 119…142, 265).
4. Coverage decisions, letters, re-verification on POL events (REQ-CLM-051…060).
5. Statutory motor: offer/payment-due/repair-in-kind/assessment clocks, offer documents and acceptance, interest, certificate (REQ-CLM-007, 008, 165…173, 191, 263, 245, 246).
6. Recoveries (subrogation, salvage, deductible) and recovery reserves (REQ-CLM-143…153).
7. Vendors, services, invoices, auto-pay, fast-track straight-through (REQ-CLM-087, 188…200, 238, 250, 262).
8. Diary, checklists, documents, conversation, notifications (REQ-CLM-174…187).
9. Fraud rules and SIU, holds (REQ-CLM-201…209).
10. Cat events, tagging, surge, dashboard, RI aggregates (REQ-CLM-006, 210…217).
11. Greek pack: FS (eligibility, cases, statement reconciliation, net to BIL), Auxiliary Fund and Green Card via adapter (REQ-CLM-155…164, 239, 264) — gated on OI-CLM-01/05.
12. Litigation (REQ-CLM-218…223); migration import/reverse and coexistence routing (REQ-CLM-010, 236, 261, 266); DSAR/retention (REQ-CLM-232, 233); AI features last, all optional.
