# PRD-06 digest — Billing and Collections (BIL)

Source: `core-insurance-prds/PRD-06-billing-collections.md` v1.5 (2026-10-07, "Draft — for design-authority review"), 2,519 lines, read in full. Binding input stated by the PRD: `00-system-contract.md` v1.11. Cross-checked against `core-insurance-infra/ARCHITECTURE-DECISIONS.md` (ADR). The system contract and the other PRDs were **not** opened for this digest; cross-PRD IDs are reported as the PRD cites them.

---

## 1. Identity

- **Module code:** BIL. **Title:** Billing and Collections. Owner: billing and collections product owner (ROLE-14 specialisation). Design authority: lead architect, who is responsible for the billing sub-ledger and the disbursement service (§1.1).
- **Purpose (§1.2):** BIL turns charges into money. It:
  - consumes POL charge deltas (`REQ-POL-005`);
  - schedules them into invoice items under a payment plan, on a billing account whose payer may differ from the policyholder;
  - collects through every Greek channel: RF or bank payment codes, SEPA direct debit (SDD), instant payment with QR, cards via acquirer, bank transfer, and intermediary collection;
  - allocates receipts and chases arrears up to the statutory non-payment notice, then sends a cancellation request to POL;
  - pays refunds and runs the **shared disbursement service** (CD-13);
  - calculates and pays commission (agreements are held in PTY, CD-14);
  - accrues and pays written-basis levies, and reconciles the bank;
  - records every money movement in an **immutable, balanced billing sub-ledger** that FIN reconciles daily (CD-15).
- **Decision D1:** BIL is the only executor of every incoming and outgoing money movement in the system. This covers Friendly Settlement (FS) clearing, complaint redress, tax and levy payments and RI settlements.
- **The five key decisions (§1.2):**
  1. The sub-ledger is the system of record for money and is never edited (append-only; reversal-only correction; balances derived from lines).
  2. The billing account is separate from the policyholder, with a plan instance and invoice stream per policy term.
  3. There are three money states with their own balances: written, billed and collected.
  4. Statutory steps are CMP clocks with durations from the country pack, never hard-coded.
  5. One cash executor (source register plus disbursement service) with uniform gates.
- **Non-goals / out of scope (§1.4):**

  | Out of scope | Owner |
  |---|---|
  | Parties, payer role, consents, sanctions engine, intermediaries, producer codes and authorities, commission **agreements** | PTY |
  | Charge types and treatments; plans offered per product and channel | PFC |
  | Taxes as charge types; pay-in-full discount | RAT |
  | Policies, jobs, charge deltas, bind-gate orchestration, cancellation and reinstatement, **earned premium** | POL |
  | GL posting rules, books, tax and levy **returns**, period close | FIN |
  | FS claim-level matching and the clearing statement | CLM |
  | Complaint outcomes | CMP |
  | Tax computation and treatment (`TaxCalculator`) | MKT |
  | Fiscal series and numbers (sole issuer) and myDATA transport; clock register | CMP |
  | Claim financial decisions | CLM |
  | Rendering, archive, delivery proof | DOC |
  | Portals and the AI agent facade | CHN |
  | Activities, queues, search | WRK |
  | Identity, audit, authority, maker-checker, numbering, workflow, adapter host, time, encryption | PLT |
  | Configuration, SPIs, rounding, clock values | MKT |
  | Full card numbers | External acquirer or PSP (BIL holds tokens only) |

- **Phase-level exclusions (§1.5, D6):**
  - P1 is annual terms only; six-month terms are kept as a golden test.
  - Bancassurance and bank-branch payments are out of P1 unless a bank partner is signed by G0.
  - Contingent and profit commission are P2. Commercial list bill, deposit and audit plans, split payers and multi-currency are P3. Currency changeover is P4.

---

## 2. Size metrics

The counts below come from the PRD's own table (§5.20). I re-checked them with a script over the requirement table rows.

| Measure | Count |
|---|---|
| REQ-BIL total | **349** (12 contract anchors 001–012 + 337 module requirements 030–366; no gaps or duplicate IDs found) |
| By MoSCoW | **Must 307, Should 34, Could 8, Won't 0** |
| By tag | [BASELINE] 302, [ENHANCEMENT] 47 |
| By phase | **P1 = 338** (Must 307 + Should 31); P2 = 4 (Could 3, Should 1); P3 = 6 (Could 4, Should 2); P4 = 1 (Could) |
| **Motor MVP (P1) Must** | **307** — every Must is P1 |
| BR-BIL | 74 (IDs 001–127 with gaps) |
| NFR-BIL | 32 |
| Screens | 17 (SCR-BIL-01…17) |
| AI features | 7 (AI-BIL-01…07) |
| Owned entities | about 35 tables in §7.1, plus the LA-01…LA-27 chart of accounts |
| Events produced | 30 rows, 31 event names (§8.1) |
| Events consumed | 35 rows, about 64 event names (§8.2) |
| Inbound API operation rows | 44 in §9.1. Several are repeated: `moveTerm`, `Refund.decide/get`, `AccountCurrent.dispute`, `Commission.statements/dispute`, `Dsar.restrict/erase` |
| Outbound API and SPI rows | about 30 (§9.2) |
| Invariants | INV-01…INV-18 (§14.x) |
| Open issues | OI-BIL-01…14 (open, closed and partly closed mixed; see §13) |

**Non-Must requirements:**
- P1 Should: 069, 084, 090, 093, 094, 112, 115, 118, 121, 122, 123, 140, 152, 179, 180, 185, 212, 221, 223, 224, 242, 244, 246, 264, 266, 295, 306, 311, 329, 333, 347.
- Later phases: 037 (Could P3), 062 (Could P2), 064 (Could P3), 083 (Should P3), 143 (Could P3), 193 (Could P2), 232 (Could P3), 257 (Should P2), 267 (Could P2), 294 (Should P3), 352 (Could P4).

**Build-size estimate: L (very large).** There are 307 Must P1 requirements, 17 screens and an append-only double-entry ledger with deferred balance constraints. The temporal and money logic is heavy: re-spread, out-of-sequence nets, two-phase cash, reversals, statutory clocks. Six ISO 20022 message types are needed, plus a shared disbursement engine for 9 sources. This is probably the largest money module in the programme.

---

## 3. Owned entities

Storage conventions (§7.0):
- One PostgreSQL schema "per stamp".
- Every row carries `legal_entity_id`, `jurisdiction`, `created_at/by` and `record_version`.
- Validity is a half-open `[valid_from, valid_to)` interval (D5).
- Money is `decimal(19,4)` plus an ISO 4217 currency. Rounding goes through `mkt.Rounding.apply`.
- IDs are UUIDv7. Business numbers come from `plt.Number.next`.
- IBANs are field-encrypted with a blind index.
- Workflow state holds IDs only (`REQ-PLT-169`).
- Entities not in contract §3.2.2 are BIL-private (R-01).

### 3.1 Accounts, plans, charges (§7.1.1)

| Entity | Key attributes | Constraints / states |
|---|---|---|
| BillingAccount | number, payer_party_id, pty_account_id?, currency, preferred_due_day 1–28, delivery_channel, language, consolidated, billing_contact_ref, vulnerable_rule_codes (codes only) | Number unique per legal entity. States: Active ↔ Suspended; Active → Closing → Closed; Closed → Active (reopen with reason). Close requires zero balances (BR-BIL-003, REQ-BIL-035). |
| PaymentPlanDefinition | plan_code, version, EN/GR names, instalments, timing offsets, first_instalment_rule, down_payment (pct or amount), fee_rule, minimum_instalment, max_instalments_by_term, allowed_methods[], bind_before_payment enum (NONE, SDD_ACTIVE_MANDATE, COLLECT_AUTHORITY, CREDIT_APPROVED), standalone_account, eligibility decision-table ref | Code and version unique; activation is maker-checker (REQ-BIL-046) |
| PaymentPlanInstance | term ref, account, plan code and version, method, due dates[], down payment, bill_mode (DIRECT_BILL/AGENCY_BILL), producer_code_ref; status Active/Changed/Closed | One Active instance per term per payer share |
| ChargeRecord | charge_id (POL or BIL-generated), charge_origin (POL/BIL), set_id/set_size/index (D4), term, transaction, correlation_key, delta_kind, element_locator, coverage, charge_type, charge_category, billing_treatment, scheduling_rule, amount, valid period, booking_date, origin (LIVE/MIGRATION), quarantine_status | charge_id unique (idempotency); amount = Σ its invoice items |
| QuarantinedDelta | event_id, charge_id, reason, resolution | — |

### 3.2 Invoices (§7.1.2, §7.3.1–7.3.2)

- **Invoice:** kind INVOICE / CREDIT_NOTE / STATEMENT / NOTICE.
  - Number is gapless per series.
  - Immutable after Billed, except status, delivery and fiscal references.
  - Stores the CMP `fiscal_document_ref` and `mark`.
  - State machine (canonical):
    - Planned → Billed → Due.
    - Due → Paid, PartiallyPaid or Overdue (Overdue = due + grace passed, open above tolerance).
    - PartiallyPaid → Paid or Overdue. Overdue → Paid or PartiallyPaid (the overdue flag is kept).
    - Paid → Due (reversal before due date). Paid → Overdue (reversal after due date).
    - Overdue → WrittenOff. Billed/Due → Reversed (credit note offsets the invoice in full).
  - Paid and PartiallyPaid are **derived from allocations**. Display precedence is Overdue > PartiallyPaid.
- **InvoiceItem:** carries the charge_id.
  - States: Planned → Billed → Open → Settled. Planned → Cancelled. Open → WrittenOff. Settled → Open (reversal).
  - Guard: Σ allocations ≤ amount.
- **Dispute:** applies to an invoice item, an account-current item or a commission line. States Open / Resolved / Rejected, with hold_until.

### 3.3 Money in (§7.1.3)

- **PaymentInstrument:** the system of record for **all** payer and payee bank accounts and card tokens across the system (R-38, CCR-BIL-05).
  - Holds purposes[], encrypted IBAN plus blind index, holder name in native and Latin forms, source, evidence, verification status (Unverified, VoPMatched, VoPCloseMatch, VoPNoMatch, VoPNotAvailable, Confirmed), VoP result and date, `valid_from/to`, `cooling_off_until`, card token, scheme, last4, expiry and SCA set-up ref.
  - Status Active / Superseded / Revoked. A change creates a new valid period and never overwrites.
  - Personal-data class P2.
- **Mandate:** reference unique per creditor identifier.
  - States PendingSignature → Active → Suspended → Active. Active → Cancelled (debtor request, payer change, MD01/AC04). Active → Expired (dormancy).
  - Events `MandateActivated` and `MandateCancelled`.
- **CollectionInstruction:** channel SDD / CARD / PAYMENT_CODE / INSTANT / LINK; end_to_end_id unique.
  - States Planned → Submitted → Settled / Rejected / Expired. Settled → Returned.
- **StatementLine:** bank line with encrypted counterparty IBAN; statement + sequence unique.
- **Receipt (incoming Payment):** constraint Σ allocations ≤ amount.
  - States Received → Allocated / PartiallyAllocated / Suspense.
  - PartiallyAllocated → Allocated or Suspense. Suspense → Allocated or PartiallyAllocated.
  - Allocated or PartiallyAllocated → Reversed. Suspense → Refunded. Allocated → Refunded (overpayment).
- **Allocation:** amount > 0; Σ per receipt ≤ receipt; Σ per item ≤ item. Records rule_id, actor, suggestion_source (RULE/AI/MANUAL), ai_interaction_id and reversed_by.
- **SuspenseItem:** Open → Suggested → Allocated, Transferred or Refunded. Open or Suggested → Escalated → (same outcomes) or Unclaimed.
- **Reversal:** kind REJECT / RETURN / REFUND_CLAIM / CHARGEBACK / FAILED_TRANSFER / RECALL, with reason code and category, actions applied and dispute deadline.

### 3.4 Money out, delinquency, agency, commission, taxes, ledger (§7.1.5, §7.3)

- **Refund:** requester ≠ approver.
  - States Proposed → PendingApproval → Approved → Disbursing → Paid.
  - Proposed or PendingApproval → Rejected. Approved → Held (refund-hold window) → Approved.
  - Disbursing → AwaitingProof (refund via intermediary) → Paid. Disbursing → Returned → Proposed.
- **Disbursement (canonical):** source_module and source_type from the register; approval evidence plus content hash; duplicate key (payee account, amount, source ref); P2.
  - Requested → PendingApproval → Approved → Released → Issued → Cleared.
  - Rejected from PendingApproval or Released (bank). Stopped from PendingApproval or Approved.
  - Issued → Voided (recall) or Returned. Cleared → Returned (late return).
  - PendingApproval sub-states: AwaitingApproval, SanctionsHold, VoPHold, DuplicateHold, BankChangeFourEyes.
- **DisbursementBatch:** Prepared, PendingRelease, Released, Acknowledged, Rejected. Maker ≠ checker above threshold.
- **WriteOff:** type SMALL_BALANCE / BAD_DEBT / CREDIT. Proposed → PendingApproval → Approved → Posted → PartiallyRecovered; or Rejected.
- **DelinquencyPlan:** versioned; steps with offset, basis, action, channel, template and conditions; restart rules.
- **DelinquencyProcess:** one Active per term.
  - Active → Arrangement → (broken) Active, or (kept) Resolved.
  - Active → NoticePending → NoticeRunning (clock started on proof).
  - NoticeRunning → Resolved (cured) or → CancellationRequested.
  - CancellationRequested ↔ Held (vulnerable or open-claim rule). CancellationRequested → Cancelled or Resolved (rescinded).
  - Active → Resolved.
- **PaymentArrangement:** PROMISE or INSTALMENTS; Active / Kept / Broken.
- **AccountCurrent:** Draft → Issued → PartiallySettled → Settled; Issued → Overdue → Settled. Invariant: opening + movements = closing.
- **AccountCurrentItem:** COLLECTED / UNREPORTED_DUE / COMMISSION / CHARGEBACK / ADJUSTMENT / REMITTANCE / DIFFERENCE.
- **CommissionCalculation:** immutable. Kinds STANDARD, OVERRIDE, RETURN, CHARGEBACK, ADJUSTMENT, CONTINGENT, PROFIT.
- **CommissionStatement:** Draft → Issued ↔ Withheld → Paid / PartiallyPaid.
- **CommissionPaymentRun.**
- **TaxLevyPeriod:** levies only; IPT periods belong to FIN (R-82). Open → Closed → RemittanceRequested → Remitted. Clock `BIL_AUXF_REMIT`.
- **BankAccount (own).**
- **ReconciliationMatch:** Σ lines = Σ entries ± approved difference.
- **BillingLedgerEntry:** accounting_date, business_date, record_time, event_type, source refs, correlation_id, reverses_entry_id, adjustment type, hash chain value. **Σ debits = Σ credits per currency (deferred constraint); append-only.**
- **BillingLedgerLine:** LA code, side, amount, currency, functional and group amounts with rates, dimensions.
- **BillingLedgerRule:** version, event_type × charge_category × bill_mode × jurisdiction → debit and credit LA accounts plus an amount expression. Maps to LA accounts only, never to GL (R-82). Exactly one match per key.
- **BillingInvariantResult:** module-internal.

### 3.5 Sub-ledger chart LA-01…LA-27 (§7.1.4)

| Code | Account | Type and normal balance | Notes |
|---|---|---|---|
| LA-01 | Written unbilled receivable | Asset Dr | |
| LA-02 | Billed receivable — payer | Asset Dr | |
| LA-03 | Billed receivable — intermediary | Asset Dr | |
| LA-04 | Premium written clearing | Clearing Cr | |
| LA-05 | Fees and surcharges clearing | Clearing Cr | |
| LA-06 | IPT payable | Liability Cr | |
| LA-07 | Levy payable | Liability Cr | |
| LA-08 | Levy expense clearing (insurer share) | Clearing Dr | |
| LA-09 | Cash in transit | Asset Dr | |
| LA-10 | Cash at bank (per account) | Asset Dr | |
| LA-11 | Suspense / unapplied cash | Liability Cr | Includes pre-bind deposits |
| LA-12 | Refunds payable / customer credit | Liability Cr | |
| LA-13 | Disbursements in transit | Liability Cr | |
| LA-14 | Commission payable | Liability Cr | |
| LA-15 | Commission expense clearing | Clearing Dr | |
| LA-16 | Write-off clearing | Clearing Dr | |
| LA-17 | Claim payments clearing | Clearing Dr | |
| LA-18 | Acquirer and bank fees | Clearing Dr | |
| LA-19 | Tolerance clearing | Clearing, either side | |
| LA-20 | Inter-account transfer clearing | Clearing | Must net to zero daily |
| LA-21 | Unclaimed funds | Liability | |
| LA-22 | RI settlements clearing | Clearing, either side | |
| LA-23 | Reinsurer receivable | Asset | |
| LA-24 | FS clearing | Clearing, either side | |
| LA-25 | Redress clearing | Clearing Dr | |
| LA-26 | Other tax payable | Liability | E.g. stamp duty |
| LA-27 | IPT written not yet due | Liability | Liability point DUE; moves to LA-06 at the due date |

The chart can be extended by packs (REQ-BIL-282).

### 3.6 Append-only ledger design (focus)

- **Structure:** entry header plus at least 2 lines in BIL's schema (REQ-BIL-279).
- **Balance:** a deferred constraint at commit enforces a zero balance per currency (REQ-BIL-280, NFR-BIL-001, INV-01).
- **No UPDATE or DELETE:** triggers reject UPDATE and DELETE **for every role including the application owner**, and each attempt raises a security event (REQ-BIL-281, NFR-BIL-002). This is stricter than the ADR, which only removes rights from the application role; the two are compatible.
- **Entries are generated only through the BillingLedgerRule table** (REQ-BIL-286, BR-BIL-110).
  - Amount expressions use the programme's "typed, deterministic rule-expression language (CEL-compatible, D10)" with decimal arithmetic and explicit rounding points (`REQ-MKT-006`).
  - Packs contribute rows. Rule changes are maker-checker.
  - If no rule matches, the event is quarantined with an activity. There is never a default account (REQ-BIL-287).
- **Corrections:** only by reversal (exact negation linked to the original) or by a linked difference adjustment (REQ-BIL-288).
- **Atomicity:** multi-leg operations are linked entries committed atomically (REQ-BIL-289).
- **Manual adjustments:** only for named types (tolerance correction, migration correction, bank fee), with evidence and maker-checker (REQ-BIL-290, BR-BIL-111).
- **Dimensions** (REQ-BIL-283): legal entity, jurisdiction, billing account, term, charge id, charge type, coverage, product, channel, producer code, bill mode, receipt, invoice item, disbursement, period.
- **Each entry also carries** (REQ-BIL-284):
  - accounting date (business date after cut-off) and UTC record time;
  - the causing event;
  - business lineage keys (job, transaction, charge, invoice item, receipt, allocation, disbursement, entry). The W3C trace id is a technical correlation only (D5).
- **Balances:** derived from lines, with checkpointed snapshots verified nightly against full sums. A mismatch raises a severity-high incident (REQ-BIL-285).
- **Periods:**
  - Daily accounting dates and monthly close. Close is refused while there are open breaks or failing invariants (REQ-BIL-291, BR-BIL-112).
  - Late events post in the open period and carry the original business date (REQ-BIL-292).
  - FIN `PeriodReopened` re-runs the control-total push but never alters entries (§8.2).
- **Hash chain:** per legal entity and day, sealed into the PLT audit seal (REQ-BIL-295, **Should**).
- **Partitioning and archive:** by legal entity and month; closed years archived under `RC-BIL-LEDGER` (REQ-BIL-297).
- **Pending vs posted cash** (LA-09 vs LA-10) is a two-step posting pattern, inspired by TigerBeetle but built in PostgreSQL (REQ-BIL-131, 299).
- **Only BIL writes the sub-ledger.** There are no shared tables, enforced by an architecture test (REQ-BIL-300).
- **Invariants:** local checks on each commit; global checks nightly (REQ-BIL-296, 321; INV-01…18; property tests with ≥ 1,000,000 generated sequences per release).

---

## 4. Consumed entities and dependencies

| From | What | Via |
|---|---|---|
| PTY | Party, Payer role, contact points, communication preferences, consents | `pty.Party.get`, `pty.PartyRole.query`, `pty.CommunicationPreference.resolve`, `pty.Consent.query`; `PartyView` read model (`PartyCreated/Updated`, `PartiesMerged`, `PartyUnmerged`) |
| PTY | Account link; merges and moves | `pty.Account.get`; `AccountMerged`, `PolicyMoveRequested` |
| PTY | Sanctions screening (fail closed) | `pty.Screening.screen` (`REQ-PTY-006`); `SanctionsHitRaised/Cleared` |
| PTY | Producer codes (collect authority, licence), producer of record, commission agreements, vulnerability rules | `pty.ProducerCode.validate`, `pty.ProducerOfRecord.get`, `pty.CommissionAgreement.resolve`, `pty.Vulnerability.query`; events `ProducerOfRecordChanged`, `CommissionAgreementVersioned`, `ProducerCodeChanged`, `IntermediaryLicenceChanged/StatusChanged`, `VulnerabilityStatusChanged`, `LifeEventRecorded`, `CommunicationPreferenceChanged` |
| PFC | Charge-type catalogue and treatments | `pfc.ChargeType.list`: category, flat/pro-rata, cancellation treatment, tax class and base flag, billing treatment, beneficiary/GL key, fiscal category, commissionable flag, levy charge types; `REQ-PFC-004, 113, 114, 116, 117, 119–122, 124, 125` |
| PFC | Plans offered per product, version and channel | `pfc.Product.describe` (`REQ-PFC-133`) |
| RAT | Pay-in-full discount, taxes as charges (indirect, via POL) | `REQ-RAT-121`, `REQ-RAT-009` |
| POL | Charge deltas; policy lifecycle events (bound, changed, cancel scheduled/rescinded, cancelled/voided, reinstated, rewritten, renewal created/offered/bound, lapsed/non-renewed, transaction reversed/reapplied, job withdrawn/not taken/quote expired, moved) | Events; `pol.Charges.reconcile` (daily); `pol.Earning.compute` (read only); `pol.PolicyChange.create` (dry-run re-price) |
| POL | `PolicyTermView` | Read model |
| CMP | Fiscal documents (MARK) | `cmp.FiscalDocument.request/get/listBySource`; `FiscalDocRegistered/Rejected/Cancelled` |
| CMP | Clocks | `cmp.Clock.start/stop/get`; `Clock*` events |
| CMP | DSAR fan-out; AI register | `REQ-CMP-005`; `REQ-CMP-007` |
| DOC | Rendering, delivery with statutory proof, archive | `doc.Document.request`, `doc.Delivery.status`, `doc.Archive.store`, `doc.Delivery.evidenceFor`; `DocumentDelivered`, `DeliveryFailed`, `DocumentRendered` |
| CLM | Disbursement requests; FS net payables and receivables; deductible and salvage receivables | Calls into BIL |
| RI | `RI_SETTLEMENT` payables and receivables | Calls into BIL |
| CMP | `CMP_REDRESS` | Calls into BIL |
| FIN | `TAX_REMITTANCE`; `fin.Reconciliation.exchange` (push target); `ReconciliationBreakRaised`, `PeriodClosed`, `PeriodReopened` | Calls and events |
| WRK | Activities, notes, palette, explained priority score (`REQ-WRK-091`); `ActivityCompleted` | API and event |
| PLT | Authority, approval, numbering, audit, time, calendars, adapter host, batch, workflow, encryption, retention, AI gateway and toggles | API and platform services |
| MKT | Configuration, rounding, capability switches, SPIs | API and SPIs |
| MIG | Routing cache (`CoexistenceMasterChanged`, `MigrationWaveStatusChanged`) and authoritative `mig.Routing.resolve` | Event and API |
| DAT | `ModelDriftDetected`, `BiasThresholdBreached` | Events |

---

## 5. Events

Envelope per contract §3.4.1. Payloads carry IDs and amounts only — never IBANs, card data or names (REQ-BIL-338, NFR-BIL-011). All events go through the outbox in the same transaction as the ledger entries (REQ-BIL-332).

The PRD uses broker vocabulary: "Topic `bil.events.v1`; partition key `billing_account_id`". The exceptions are `disbursement_id`, `intermediary_id` and `levy_period_id` (R-100) (§8). See §14.

### 5.1 Produced (§8.1)

| Event | Trigger | Key payload | Consumers |
|---|---|---|---|
| `InvoiceIssued` | Invoice or credit note Billed | id, number, kind, totals by category, due date, method, fiscal trigger ref | FIN, CHN, DAT, PTY |
| `PaymentReceived` | Receipt against an account (incl. recognised intermediary collection) | receipt, channel, method, amount, value and accounting dates, intermediary id, RI ref | FIN, CHN, DOC, DAT, POL, CLM, RI, PTY, MIG |
| `CashAllocated` | Allocation batch committed | allocations (item, term, amount), `arrears_cleared` per term, `down_payment` | FIN, CHN, DAT, POL, CLM, RI, MIG |
| `PaymentReversed` | Reversal processed | reversal, kind, reason category and code, items reopened, `down_payment` | FIN, CHN, DAT |
| `DownPaymentCleared` | Down payment cleared or deferred condition met (also renewal acceptance, REQ-BIL-366) | job id, term, amount, receipts, mode CLEARED/DEFERRED | POL, CHN, DAT |
| `DelinquencyStarted` | Process starts | term, overdue amount, plan and version, process | CHN, WRK, PTY, DAT |
| `NonPaymentNoticeSent` | Notice delivered with proof and clock started | document id, proof ref, clock id, deadline | CHN, WRK, DAT |
| `CancellationForNonPaymentRequested` | `ClockElapsed` with arrears and no hold | request id, term, overdue, notice doc, proof ref, clock id, requested effective date, motor flag | POL, WRK, PTY, DAT |
| `RefundApproved` | Refund approved | amount, payee, refund payout method, decision maker | FIN, DOC, CHN, DAT |
| `RefundDisbursed` | §8.1 says "Refund disbursement **Cleared**"; REQ-BIL-190 says at `bil.refund.paid_point` (Greece **ISSUED**) — **conflict, see §14** | refund, disbursement, amount, value date | FIN, CHN, POL, DAT, CMP |
| `WriteOffPosted` | Write-off posted | type, reason, items, amount | FIN, DAT, PTY |
| `DisbursementIssued` | pain.002 acknowledgement | source module, type and id, amount, value date, method | CLM, RI, FIN, DAT |
| `DisbursementCleared` (R-39) | Statement debit matched | source, value date | CLM, RI, FIN, CMP, MIG, DAT |
| `DisbursementVoided` | Recall confirmed | source, reason | CLM, RI, FIN |
| `DisbursementReturned` | Beneficiary bank return | reason code | CLM, RI, FIN |
| `DisbursementRejected` (D4) | Rejected by approver, evidence check or bank pain.002 | reason code, rejecting party | CLM, RI, FIN, CMP, CHN, WRK, MIG, DAT |
| `DisbursementStopped` (D4) | Stopped | reason, actor | Same as above |
| `CommissionCalculated` | Calculation batch | intermediary, period, totals by kind | FIN, CHN, DAT |
| `CommissionPaid` | Commission disbursement Cleared | statements, amount | FIN, CHN, DAT |
| `CommissionStatementIssued` (R-39) | Statement issued | net payable, fiscal mode | FIN, DAT |
| `LevyAccrued` | Daily increment and period close | levy type, share, period, amount, cumulative, final flag | FIN, DAT |
| `LevyRemitted` | Levy payment Cleared | type, period, amount | FIN, DAT |
| `ChargesScheduled` (R-39) | Deltas scheduled | term, transaction, charge ids | DAT |
| `BillingEntryPosted` (R-39) | **Every ledger entry**; FIN's sole posting source for BIL facts (`REQ-FIN-036`) | entry, event type, dates, lines with accounts, sides, amounts and dimensions | FIN, DAT |
| `DelinquencyResolved` (R-39) | Process ends without cancellation | outcome (Paid, Tolerance, Arrangement kept, Rescinded) | POL, DAT |
| `MandateActivated` / `MandateCancelled` | Mandate state | mandate, reference, reason | DAT |
| `BillingAccountChanged` | Account create, payer, status, terms | | DAT |
| `PaymentPlanChanged` | Plan, due day or method | | POL, DAT |
| `AccountCurrentIssued` | Account current issued | net due, due date | DAT |
| `RefundRejected` | Refund rejected | reason | DAT |

### 5.2 Consumed (§8.2)

All consumers are idempotent: on `charge_id` for deltas, on `event_id` otherwise, and on `event_id` + clock id for clocks.

- **POL:**
  - `ChargeDeltaEmitted`: validate, write the entry, schedule once the set is complete, validate the tax treatment, calculate commission.
  - `PolicyBound`: create the plan instance, attach the term, allocate the deposit.
  - `PolicyChanged`: context only.
  - `CancellationScheduled/Rescinded`: preview credits; close the delinquency on rescind.
  - `PolicyCancelled/Voided`: stop planned items, bill credits, propose a refund, close the delinquency.
  - `PolicyReinstated`: catch-up invoice.
  - `PolicyRewritten`.
  - `RenewalCreated/Offered/Bound`: plan carry-forward.
  - `PolicyLapsed/NonRenewed`, `JobWithdrawn/NotTaken`, `QuoteExpired`: return deposits.
  - `TransactionReversed/Reapplied`: out-of-sequence sets.
  - `PolicyMoved`.
- **PTY:** party, merge and move events; preferences; vulnerability; life events (death → payer and mandate review); sanctions hits; producer of record; agreement versioning; producer, licence and status changes.
- **CMP:**
  - `FiscalDocRegistered`: store the MARK and release held premium receipts.
  - `FiscalDocRejected`: error state plus activity `CMP-FISCAL-REJECTED`.
  - `FiscalDocCancelled`: mark the item and include it in reconciliation.
  - Clocks: `BIL_NONPAY_NOTICE` (WAITING_PERIOD): `ClockElapsed` → cancellation request; `ClockMet` (Cured) → close. `BIL_AUXF_REMIT` (DEADLINE): `ClockMet` on remittance; `ClockBreached` → incident and late-interest review (R-78).
- **DOC:** delivered or failed (notice proof, clock start), rendered.
- **FIN:** `ReconciliationBreakRaised`, `PeriodClosed`, `PeriodReopened`.
- **PLT:** `ApprovalDecided`, `AuthorityGrantChanged`, `ConfigChanged`, `AiToggleChanged`, `AiKillSwitchActivated`, `LegalHoldApplied/Released`.
- **MKT:** `PackActivated`, `PackRolledBack`, `RateTableChanged`.
- **MIG:** `CoexistenceMasterChanged`, `MigrationWaveStatusChanged`.
- **WRK:** `ActivityCompleted`.
- **DAT:** `ModelDriftDetected`, `BiasThresholdBreached`.
- **Explicitly not consumed:** `EvidencePackSealed` (DOC) and `SettlementRecorded` (RI).

---

## 6. APIs

The PRD describes these as "in-process; REST under `/api/bil/v1/`". Every command takes an `Idempotency-Key`. Every command that changes money or legal status supports dry-run (§5 conventions).

**Exposed (§9.1):**
- `bil.BillingAccount.create/get/search/update/close/reopen/changePayer/attachTerm/moveTerm`
- `bil.PaymentPlan.list/select/change`; `bil.BillingPreview.compute` (always side-effect free); `bil.DownPayment.status/initiate`
- `bil.Invoice.get/list/sendCopy/dispute`
- `bil.PaymentInstrument.add/remove/list`; `bil.PayeeAccount.create/verify/get/list`; `bil.Mandate.create/sign/amend/cancel/get`
- `bil.Payment.take`; `bil.Receipt.get/list`; `bil.Allocation.allocate/unallocate/reallocate`; `bil.Suspense.list/suggest/transfer/refundToSender`
- `bil.IntermediaryCollection.report/reportFile`; `bil.AccountCurrent.get/list/dispute`
- `bil.Delinquency.get/list/recordContact/arrange/releaseHold`
- `bil.Refund.propose/get/list/decide/resubmit`
- `bil.Disbursement.request/get/list/stop/void`; `bil.Receivable.register`; `bil.DisbursementBatch.prepare/release/approveRelease`
- `bil.WriteOff.propose/decide/post`; `bil.Transfer.money`
- `bil.Commission.calculations/statements/dispute/runPayments/simulate`
- `bil.TaxLevy.periods/detail/requestLevyPayment`
- `bil.Ledger.query/balance`; `bil.Reconciliation.bank/invariants/fin` (`fin` is read-only and never triggers the exchange)
- `bil.Import.account/openItems/mandate/paymentMethodToken/openingBalances/agencyItems/commissionHistory/reverse/convertCurrency`
- `bil.Dsar.export/restrict/erase`

**Error codes** include `BIL-ERR-BALANCE-NOT-ZERO`, `-PAYER-ROLE`, `-APPROVAL-REQUIRED`, `-CURRENCY`, `-ENTITY`, `-NOT-ELIGIBLE`, `-NOT-PERMITTED`, `-METHOD-UNAVAILABLE`, `-IBAN-INVALID`, `-CHARSET`, `-VOP-UNAVAILABLE`, `-MANDATE-STATE`, `-OVER-ALLOCATION`, `-NO-COLLECT-AUTHORITY`, `-NOTICE-CLOCK-RUNNING`, `-NO-CREDIT`, `-SOURCE`, `-METHOD-NOT-ALLOWED`, `-AMOUNT-MISMATCH`, `-REDRESS-ROUTE`, `-APPROVAL-MISMATCH`, `-PAYEE-BLOCKED`, `-DUPLICATE`, `-NOT-STOPPABLE`, `-HELD-ITEMS`, `-DAY-NOT-CLOSED`, `-LIVE-ACTIVITY`, `-PLAN`, `-LEGAL-HOLD`, `-OPEN-OBLIGATION`, `-CONFIRMATION-REQUIRED`, `-DUE-DAY-CHANGE-LIMIT`; plus `PLT-ERR-SOD` and `PLT-ERR-AUTHORITY`.

**Consumed (§9.2):** PTY, PFC, POL, CMP, DOC, WRK, PLT, MKT, MIG and FIN operations as listed in §4. The only direction of the FIN reconciliation is BIL pushing to `fin.Reconciliation.exchange`.

**External integrations (§9.3, through the PLT adapter host):**
- Greek banks over host-to-host or e-banking upload: ISO 20022 **pain.001, pain.008, pain.002, camt.053, camt.054** via `BankFileFormat`.
- DIAS RF payment codes.
- Card acquirer: hosted fields, REST, settlement reports, chargebacks.
- Instant payment and QR provider.
- VoP service (synchronous, ≤ 2 s).
- AADE myDATA (via CMP only).
- Auxiliary Fund credit transfer.
- FS clearing body and counterparties.
- AADE tax payments (payment identifier from FIN).
- Intermediaries via CHN or file.

---

## 7. SPIs / country-pack interfaces

The PRD uses these SPIs (§9.2, §10.4):

| SPI | Use |
|---|---|
| `PaymentReferenceGenerator` (`REQ-MKT-098`) | RF ISO 11649 default; Greek bank codes per collecting bank (REQ-BIL-115) |
| `BankFileFormat` (`REQ-MKT-100`) | ISO 20022 variants |
| `PayeeVerification` (`REQ-MKT-102`) | VoP |
| `TaxCalculator` (`REQ-MKT-087`), incl. `treatment(chargeType, transactionKind, cancellationSource)` (`REQ-MKT-330`, D2) | Returns `customerCredit` PRO_RATA/FULL/NONE, `authorityLiability` REDUCE/NOT_REDUCE, `fiscalDocument` CREDIT_NOTE/NONE |
| `StatutoryClockSet` (via CMP, `REQ-MKT-009`) | Clock durations |
| `PolicyLifecycleRules` (`REQ-MKT-002`) | Notice mandatory elements and proof list |
| `StatutoryDeliveryRule` (R-41) | Accepted proof media |
| `FiscalDocumentChannel` (via CMP) | myDATA transport |
| `NumberingScheme` and `HolidayCalendarProvider` (via PLT) | Numbers and calendars |
| `NameTransliterator` (`REQ-MKT-180`) | SEPA Latin names |
| `PaymentChannelProvider` (R-41, CCR-BIL-03: `createCollection`, `parseNotification`, `capabilities`) | Country collection channels |
| `TaxReturnFormat` (`REQ-MKT-314`) | Levy period scheme |
| `FriendlySettlementClearing` (`REQ-MKT-109`) | Clearing counterparties |

**Architecture test ARCH-01 (`REQ-MKT-231`)** forbids country literals in BIL code.

**Cyprus stub (CI, §10.4):**
- RF-only references.
- Synthetic notice clock of 15 business days.
- Synthetic per-receipt stamp fixture (`REQ-MKT-265`): non-zero up to 2025-12-31, zero from 2026-01-01.
- Synthetic 5% motor fund levy.
- No QR channel; fiscal channel `NotRequired`; different numbering.
- The worked example runs under both packs (G-10).

---

## 8. Screens

All screens support the Greek/English switch (R-101, `REQ-MKT-005`) without reload. Customer documents follow the customer's language, not the UI language. A missing translation fails the release gate (§6.0). Common patterns:
- statement view `IB-13`;
- exception-first `IB-28`;
- priority with explanation `IB-04`;
- split pane `IB-05`;
- command palette `IB-01`;
- edit-in-place only for non-money preferences `IB-14`;
- AI surfaces `IB-07` / `IB-08` / `IB-22`;
- the `IB-29` log;
- work-left home `IB-11`;
- binary pass/fail `IB-32`.

| ID | Name | Personas | One line |
|---|---|---|---|
| SCR-BIL-01 | Billing account summary | ROLE-22, 23, 03, 24 | Context bar, headline balance with direction, money-state cards, terms and plans, history; inventory UIL-B1 |
| SCR-BIL-02 | Invoice and notice viewer | ROLE-03, 22, 26 | Items, allocations, fiscal panel (CMP series, number, MARK), delivery proof; "Explain invoice" (AI-BIL-07) |
| SCR-BIL-03 | Payment entry | ROLE-03, 22, (08 via CHN) | Overlay; card only via pay-by-link or hosted field; allocation preview |
| SCR-BIL-04 | Payment plan change with billing preview | ROLE-03, 22, 05 | Before/after schedule; embedded in POL bind panel and change workspace |
| SCR-BIL-05 | Unapplied cash and suspense workbench | ROLE-22, 45 | Exception-first queue; rule and AI suggestions; allocate, split, transfer, refund to sender (UIL-B2) |
| SCR-BIL-06 | Payment exceptions and returns queue | ROLE-22 | R-transactions and chargebacks needing judgement; representment (UIL-B3) |
| SCR-BIL-07 | Refund approval and disbursement queue | Disbursement approver, ROLE-22, 24, 29 (read) | All 9 sources; VoP, sanctions, cooling-off, authority; batch release with second approver (UIL-B4) |
| SCR-BIL-08 | Write-off with authority check | ROLE-22, 23, 24 | Posting preview, authority result (UIL-B5) |
| SCR-BIL-09 | Delinquency workbench | ROLE-23, 45, 29 | Priority queue, clock deadlines, arrangements, held cancellations; "statutory step actions never offer skip" (UIL-B6) |
| SCR-BIL-10 | Move money and move policy | ROLE-22 | Source and target, preview, maker-checker when payers differ |
| SCR-BIL-11 | Agency account current and reconciliation | Agency accountant | Statements, remittance matching, differences, ageing, exposure (UIL-B7) |
| SCR-BIL-12 | Commission statements and payment run | Distribution finance | Drill-down to rule ids; payment run release (UIL-F5) |
| SCR-BIL-13 | Bank reconciliation | ROLE-24, 22 | Closing-balance pass/fail, breaks, manual and forced match (UIL-B8); AI-BIL-03 |
| SCR-BIL-14 | Payment methods and mandates | ROLE-03, 22 | Instruments, mandates, VoP, method per term |
| SCR-BIL-15 | Billing operations home and ledger explorer | ROLE-22, 24, 25, 45, 43 | Work-left cards, run status, FIN reconciliation status, ledger explorer with balance as of a date |
| SCR-BIL-16 | Levies and tax payables | ROLE-26, 24 | Levy periods, remittance clock, IPT accrual (read-only), LA-27, tax payments |
| SCR-BIL-17 | Billing configuration | ROLE-14, 15, 25, 43 | Plans, delinquency plans, waterfall, tolerances, reason codes, scheduling, ledger rules; impact preview; maker-checker |

- **Inventory:** 9 of 9 owned UI-library items covered (UIL-B1…B8, UIL-F5). There are no owned reference screens. 21 consumed items are reference only.
- **Self-noted thin areas:** SCR-BIL-15…17 have fewer fields than the other screens (§16.6).

---

## 9. Regulatory, tax and statutory rules

### 9.1 Rules the PRD states explicitly (§3.1, §3.2, §10.1)

| Rule | Stated content | IDs |
|---|---|---|
| Law 2496/1997 Art. 6 §1 | Premium payable in money, in one sum or instalments. Cover does not start before payment of the single premium or first instalment, unless the contract or circumstances provide otherwise. | Bind gate: REQ-BIL-003, 054–057, BR-BIL-010 |
| Law 2496/1997 Art. 6 §2 | Delay on an overdue instalment → written statement that further delay dissolves the contract **one month after notification**. No term-length distinction; the prompt's "two weeks" was not found (AC-1, closed per R-60). | REQ-BIL-006, 168–174; BR-BIL-040–044 |
| P.D. 237/1986 Art. 11 / 11a (as amended by Law 3557/2007) | MTPL termination is opposable to injured third parties only for accidents **16 days** after the insurer's notification. Paragraph and recipient **UNVERIFIED** (OI-BIL-02). | REQ-BIL-173; POL owns clock `POL_MTPL_THIRDPARTY_NOTICE` (D7) |
| Law 5113/2024 Art. 14 (amends P.D. 237/1986 Art. 20) | Auxiliary Fund contribution on gross written MTPL premium: **4.5% + 1.5% = 6%**, split **70% insurer / 30% policyholder** (**4.2% / 1.8%**). Policyholder share shown on the policy. Remitted **within 15 days after each calendar two-month period, irrespective of collection**. Late payment bears default interest. Purpose of the 4.5/1.5 split is UNVERIFIED (OI-BIL-03). | REQ-BIL-269–274; BR-BIL-070–072; charge types `GR-AUXF-PH` (billed, IMMEDIATE) and `GR-AUXF-INS` (ACCRUE_ONLY) |
| Law 5177/2025 Art. 43 | IPT on premiums due ("απαιτητά ασφάλιστρα") and contract rights of every kind: **15% general, 20% fire**; quarterly declaration to AADE. Return deadlines UNVERIFIED (OI-BIL-13, now closed as FIN's). | REQ-BIL-275–277, 360, 363 |
| AADE ΠΟΛ 1028/2017 | **IPT not refunded on cancellation.** Greece pack default for ordinary cancellations: `authorityLiability = NOT_REDUCE`, `customerCredit = NONE`. | REQ-BIL-079, 183, 319; BR-BIL-025; NFR-BIL-030; INV-05 |
| Law 5317/2026 Art. 72 / Law 2251/1994 Art. 3ιστ | Nothing payable by a consumer withdrawing from a distance contract. Refund **within 30 calendar days** of receipt of the withdrawal (OBL-DMFS). A refund net of tax for a distance-withdrawal void is excluded. | REQ-BIL-079, 181, 183, 190 |
| AADE myDATA A.1138/2020 (and A.1188/2022, A.1049/2024) | Electronic transmission of fiscal documents. Insurance document types UNVERIFIED (OI-BIL-06). | REQ-BIL-086, 096–101 |
| Law 4583/2018 Art. 25 §1 | A customer paying premium to an intermediary is discharged even if the intermediary does not remit, unless the customer knowingly paid an intermediary without collection authority (burden of proof on the insurer). | BR-BIL-090; REQ-BIL-166, 233–240, 248 |
| Law 4583/2018 Art. 25 §2 | Money the insurer pays to an intermediary for the customer discharges the insurer only when the customer receives it. | REQ-BIL-192; BR-BIL-094 |
| Law 4583/2018 Art. 28 | Intermediary information, including premium collection. | REQ-BIL-234 |
| Reg. (EU) 260/2012; EPC SDD Core Rulebook 2025 (in force 5 Oct 2025); EPC173-14 v8.0 | Mandates with unique reference and creditor id; pain.008. Debtor refund right **8 weeks** (authorised, MD06) and **13 months** (unauthorised). Reason codes e.g. AM04, MS02, MD06, MD01, AC04. | REQ-BIL-106–115, 147–155; BR-BIL-050–055 |
| Reg. (EU) 2024/886 (Art. 5c) | **Verification of payee** for euro credit transfers **from 9 Oct 2025**; outcomes Match / CloseMatch / NoMatch / Unable. Non-consumer bulk opt-out is possible but BIL never relies on it. | REQ-BIL-203–205, 343–345; BR-BIL-060–062 |
| PSD2 / Del. Reg. 2018/389 | SCA at card set-up and customer-initiated payments; stored-token charges flagged merchant-initiated. EBA Q&A UNVERIFIED (OI-BIL-07). | REQ-BIL-116–118 |
| PCI DSS v4.0.1 (11 Jun 2024; future-dated requirements from 31 Mar 2025) | Acquirer-hosted capture; BIL stores token, last four and expiry only. | REQ-BIL-116, NFR-BIL-012 |
| Sanctions (EU, group OFAC lists) | Screen before every disbursement; fail closed on Blocked, PotentialHit or stale lists. | REQ-BIL-200, 201, 214; BR-BIL-063 |
| GDPR / Law 4624/2019 | IBAN and payment data are P2; field encryption; DSAR; retention codes. | REQ-BIL-334–338 |
| DORA, AI Act, EAA (R-57), Solvency II, IFRS 17 / Law 4308/2014 | Mechanism-level only. | §3.1 |

### 9.2 Rules referenced but not specified (gaps)

- **Accepted electronic proof methods** for the statutory notice (OI-BIL-01 / XMR-OQ-001).
- **Exact text and recipient of the MTPL 16-day rule** (OI-BIL-02).
- **Auxiliary Fund:** purpose of the components, **refund treatment on cancellation** (the worked example *assumes* non-refundable), and the remittance form and declaration (OI-BIL-03).
- **Greek bank** payment-code formats, file variants and statement timing (OI-BIL-04).
- **IPT on instalments:** follow premium or bill upfront, and whether instalment fees are in the IPT base (OI-BIL-05).
- **myDATA document types** for insurance transactions, credits and commission self-billing (OI-BIL-06).
- **Acquirer, token portability and recurring SCA** (OI-BIL-07).
- **Unclaimed-money rules** (OI-BIL-09).
- **Whether late-payment interest or fees may be charged to consumers** on instalments (OI-BIL-11).
- **Intermediary client-money arrangements** (OI-BIL-12).
- **Statutory books retention:** "closed by citation" to the programme retention schedule; Greece value UNVERIFIED in §7.1.
- **Cash-acceptance limits** (REQ-BIL-123: "cash limit rule from the pack").
- **AADE tax payment identifier format** (held by FIN).
- **Late-interest computation** on a breached levy deadline: only "late-interest review" is mentioned.

### 9.3 Rules deferred to configuration or country pack

- **Clock values:** `BIL_NONPAY_NOTICE` duration (Greece one month; Cyprus 15 business days synthetic) and `BIL_AUXF_REMIT` (Greece 15 days after period end, `bil.levy.remit_due_rule`).
- **Tax rates and treatments:** levy rates and split (`REQ-MKT-322`); IPT rates; `tax.ipt.liability_point` (WRITTEN or DUE; **Greece default DUE** until the tax opinion, R-85).
- **Periods and fiscal settings:** levy period scheme (`TaxReturnFormat`); fiscal trigger point (`bil.fiscal.trigger_point`, Greece motor TRANSACTION); MARK before delivery (Greece true).
- **Pack lists:** unclaimed period; proof-method order; refund payee rule; dishonour-fee permission.
- **Legal sign-off gate:** the tax readings (IPT liability point, levy split, withdrawal-void IPT, fees in the base) are held as pack keys with "guarded golden expectations" (`REQ-MKT-055`). Legal sign-off is a **go-live gate** (§1.2, RK-BIL-10).

### 9.4 Operational defaults stated (§10.1–10.2)

| Area | Defaults |
|---|---|
| Invoicing | Grace 5 days; invoice lead time 30 days (payment code) / 14 (SDD) |
| SDD | Pre-notification 14 calendar days (shorter if agreed in mandate); mandate dormancy 36 months; re-present AM04 once at +7 days; repeated failure 2 in 6 months |
| Tolerance and refunds | Tolerance €1.00 / 0.5%; refund minimum €5.00; refund auto-approve €500, maker-checker €5,000 |
| Disbursement controls | Batch release four-eyes €10,000; VoP NotAvailable threshold €1,000; payee cooling-off 30 days per purpose |
| Timing | Deposit holding period 2 business days; cut-off 18:00; incomplete delta set 15 minutes; agency report deadline 10 days |
| Commission | Payable basis default COLLECTED |

---

## 10. Greek-market specifics

- **EUR** functional currency. Money is `decimal(19,4)`.
- **AFM:** not mentioned in PRD-06. The commission self-billing fiscal request carries the "intermediary's VAT treatment code" (REQ-BIL-101).
- **myDATA interplay (focus):**
  - BIL invoices are **non-fiscal payment demands** labelled «ειδοποίηση πληρωμής», **never «τιμολόγιο»** (D3, REQ-BIL-086, 088, glossary).
  - Invoice numbers come from a non-fiscal gapless PLT series. Failed numbers are voided with a reason (`REQ-PLT-211`).
  - **CMP is the sole issuer** of fiscal series and numbers (`REQ-CMP-038`). BIL stores CMP series, number and MARK on items for display.
  - Fiscal trigger via `cmp.FiscalDocument.request` (`REQ-CMP-001`): **TRANSACTION** (one per bound transaction's charges) for Greek motor, or INVOICE. Lines carry PFC fiscal-category keys (REQ-BIL-096).
  - Triggers are sent per term (REQ-BIL-078) and only once the delta set is complete (REQ-BIL-364).
  - BIL-originated fees are included; credits and cancellations get credit-type requests (REQ-BIL-097).
  - Request source types: TRANSACTION, INVOICE, CREDIT, FEE, COMMISSION. BIL dry-runs before commit (§9.2).
  - `FiscalDocRegistered` → store the MARK. `FiscalDocRejected` → error and activity `CMP-FISCAL-REJECTED`, auto-closed on later registration. `FiscalDocCancelled` → mark and reconcile (REQ-BIL-098).
  - **MARK before delivery** applies only to **premium receipts** and other DOC types flagged fiscal (`bil.fiscal.mark_before_delivery`, shared certification rule with `cmp.fiscal.mark_before_issue`). It never blocks bind, collection or allocation. Invoices are never held (REQ-BIL-099, BR-BIL-121).
  - **Daily reconciliation** of registered fiscal documents vs invoiced charges and credits by type and amount: missing, mismatch, orphan (REQ-BIL-100, INV-07, G-8).
  - **Commission self-billing** fiscal triggers where the agreement says the insurer issues; matching of intermediary-issued documents before payment (REQ-BIL-101, 263, 264).
- **Payment codes:** RF ISO 11649 default via DIAS-connected banks, plus bank-specific codes per collecting bank (REQ-BIL-114, 115). IRIS-type instant QR (REQ-BIL-121, Should).
- **SEPA names:** Greek holder names must be transliterated to Latin and confirmed (REQ-BIL-104).
- **Greek Friendly Settlement** inter-insurer clearing: `FS_CLEARING`, method `CLEARING` (REQ-BIL-355–357).
- **Auxiliary Fund levy (MTPL)** and **IPT 15/20%**, as in §9.
- **Statutory notice:** `DT-NONPAY-NOTICE` with Greek binding content.
- **Language:** EL/EN invoices and notices with Greek binding (NFR-BIL-025). Greek labels marked † are working translations; ‡ marks glossary-amended terms (PRD-18 §6). Greek customer-transparency texts for AI features await ROLE-46 confirmation.
- **Information Centre / bureau:** only as a question inside OI-BIL-02 ("Information Centre?"). **gov.gr:** not mentioned.

---

## 11. Controls

- **Authority types** (REQ-BIL-326, §12), with dimensions:

  | Authority type | Dimensions |
  |---|---|
  | `BIL.Refund` | amount, currency, product, reason, payee changed |
  | `BIL.WriteOff` | amount, type, reason |
  | `BIL.PaymentArrangement` | days, instalments, amount |
  | `BIL.BatchRelease` | batch total, method |
  | `BIL.VoPOverride` | amount |
  | `BIL.CommissionAdjustment` | amount |
  | `BIL.ManualAllocationReversal` | amount |
  | `BIL.SuspenseRefund` | amount |
  | `BIL.DisbursementApproval` | BIL-sourced, amount, purpose |
  | `BIL.DisbursementStop` | amount, source type; used by BIL and source-module users alike, XMR-F-343 |

- **Maker-checker** (REQ-BIL-327, §12; CCR-BIL-04 accepted R-45):
  - refunds above threshold;
  - write-offs above threshold;
  - disbursements to a payee account changed within the cooling-off period;
  - batch release above threshold;
  - activation of plan, delinquency-plan, waterfall, tolerance, reason-code and scheduling configuration;
  - billing-ledger-rule versions;
  - manual sub-ledger adjustments;
  - forced bank-reconciliation matches above tolerance;
  - money transfers between different payers;
  - payer change with credit balance or active mandate;
  - unclaimed-funds transfer;
  - VoP override;
  - commission payment-run release;
  - bank-account register changes;
  - levy payment release;
  - **every `TAX_REMITTANCE`, `FS_CLEARING` and `CMP_REDRESS` batch regardless of amount** (D1);
  - source-register changes;
  - also manual unallocate/reallocate above an amount (REQ-BIL-141).
- **SoD** (`REQ-PLT-082`):
  - refund requester or editor ≠ approver;
  - payee-account changer ≠ disbursement approver within cooling-off;
  - batch preparer ≠ release approver;
  - write-off requester ≠ approver;
  - ledger-rule author ≠ approver;
  - forced-match maker ≠ checker;
  - configuration author ≠ approver;
  - **the approver of a source's evidence cannot release the BIL batch that pays it**;
  - no approvals on accounts where the staff member is payer or a related party.
- **Disbursement gates** (REQ-BIL-198–206):
  - verify the source's approval evidence by content hash, so there is no double approval;
  - sanctions screening before approval and again before release if stale;
  - VoP per payee;
  - cooling-off four-eyes;
  - duplicate detection across BIL and CLM;
  - fail closed when VoP or sanctions are down (REQ-BIL-214);
  - optional daily approver aggregate limit (REQ-BIL-212, Should).
- **Audit:** every billing data change, approval, override, configuration change, AI outcome and P2 unmask goes through `REQ-PLT-002` (REQ-BIL-328, NFR-BIL-014). Moves and transfers are audited and reversible by counter-move (REQ-BIL-230).
- **GDPR:**
  - Personal-data classes P0/P1/P2 per attribute. P2: IBAN, card last4 and expiry, mandate debtor, statement lines, disbursements.
  - Masking without P2 permission (REQ-BIL-334).
  - DSAR export via CMP fan-out (335).
  - Erasure: delete instruments not needed for open obligations; restrict the ledger with reason "statutory retention" (336).
  - Retention codes `RC-BIL-LEDGER/INSTRUMENT/MANDATE/CORRESP/STATEMENT`, with durations from the programme retention schedule as Greece pack data (337).
  - Personal data is kept out of events and logs (338).
- **Vulnerable customers:** codes only, never the reason. Rules: extended timing, no automatic method switch, human contact before notice or cancellation (REQ-BIL-044, 174).
- **AI agents:** every money-moving command requires explicit human confirmation in the channel (REQ-BIL-342).

---

## 12. AI features (§11)

All features ship **off**. They are gated by MKT switches and PLT toggles and go through the PLT EU gateway with an `AiInteractionRecord`. None of them may release money, request cancellation, approve, change a plan or alter a statutory step. The kill switch takes effect in ≤ 60 s with deterministic fallback (REQ-BIL-353).

| ID | Feature | Classification | Phase | MVP need |
|---|---|---|---|---|
| AI-BIL-01 | Suspense match assistant | Minimal risk; profile MP-D | P1 (§1.5) | Not needed — rule matching (REQ-BIL-127/128) suffices. REQ-BIL-139 (Must) requires the suggestion UX, AI optional |
| AI-BIL-02 | Collections next-best action | **High-risk controls** (uncertain); MP-A | P3 | No |
| AI-BIL-03 | Bank reconciliation break explainer | Minimal; MP-C | P1 pilot | No |
| AI-BIL-04 | Intermediary remittance advice reader | Minimal; MP-D | P2 | No |
| AI-BIL-05 | Bilingual billing correspondence drafter | Limited (Art. 50); MP-B; never for statutory notices | P2 | No |
| AI-BIL-06 | Billing leakage and anomaly detection | High-risk controls by default; MP-E | P3 | No |
| AI-BIL-07 | Invoice explainer (staff and customers) | Limited (Art. 50); MP-B | P2 | No (template fallback) |

**For MVP, AI is optional.** Only REQ-BIL-353 (kill-switch handling) and the suggestion hooks are Must.

---

## 13. Open issues, assumptions, CCRs

**Open issues (§16.1):**

| ID | Topic | Status |
|---|---|---|
| OI-BIL-01 | Notice proof methods | Duration closed (R-60); proof open → XMR-OQ-001 |
| OI-BIL-02 | MTPL 16-day rule | Open; responsibilities settled D7 |
| OI-BIL-03 | Levy components, refund treatment, remittance form | Mechanism settled D2; values await the single tax and legal opinion; go-live gate |
| OI-BIL-04 | Bank formats | Open |
| OI-BIL-05 | IPT on instalments; fees in base | Folded into the D2 opinion |
| OI-BIL-06 | myDATA document types | Open; trigger, issuer and MARK closed D3 |
| OI-BIL-07 | Acquirer, SCA | Open |
| OI-BIL-08 | Retention | Closed by citation, XMR-F-329 |
| OI-BIL-09 | Unclaimed money | Open |
| OI-BIL-10 | Legacy collection baseline | Open |
| OI-BIL-11 | Late interest or fees on consumers | Open |
| OI-BIL-12 | Client money | Open |
| OI-BIL-13 | IPT liability point | Closed R-85/D2 |
| OI-BIL-14 | DOC types | Closed |

**Assumptions (§16.2):**
- **A-1:** POL emits NET deltas with category, coverage and treatments.
- **A-2:** All collecting banks support camt.053/054 and pain.001/002/008.
- **A-3:** An acquirer with hosted fields, tokens, merchant-initiated charges and refunds is contracted.
- **A-4:** COLLECTED basis for agents; WRITTEN for some brokers.
- **A-5:** Instalment and dishonour fees are permitted (OI-BIL-11 may restrict).
- **A-6:** FIN posts only from `BillingEntryPosted`.
- **A-7:** CLM has no PayeeAccount entity.

**Risks (§16.3):** RK-BIL-01…11.
- Allocation and reversal correctness at volume.
- Statutory notice errors.
- Bank-file variants.
- Agency credit loss.
- Fiscal mapping.
- Misdirected payments.
- FIN and CLM boundary drift.
- Card-token migration.
- Unsettled tax readings.
- Fraud surface of the new money sources.

**CCRs (§16.4):** all accepted, and there are no new CCRs in v1.5.
- **CCR-BIL-01 (R-39):** adds 11 events.
- **CCR-BIL-02 (R-40):** clock codes `BIL_NONPAY_NOTICE` and `BIL_AUXF_REMIT`.
- **CCR-BIL-03 (R-41):** `PaymentChannelProvider` SPI.
- **CCR-BIL-04 (R-45):** maker-checker additions.
- **CCR-BIL-05 (R-38):** BIL PaymentInstrument is the system of record for payee accounts.

**Pre-build decisions (§16.6, "ten decisions"):**
- **Decided:** 2 (D3), 6 (D2 mechanism), 9 (FIN posting and push).
- **Partly decided:** 1 (duration only).
- **Still open:**
  - 3 — banks, channels and payment-code scheme;
  - 4 — acquirer;
  - 5 — default waterfall, tolerances and refund thresholds per legal entity;
  - 7 — agency-bill policy;
  - 8 — commission basis and self-billing approach;
  - 10 — migration approach for mandates, agency balances and coexistence routing.

---

## 14. Conflicts and ambiguities found

### (a) With the infrastructure / stack (ADR)

1. **Broker vocabulary.** §8 says "Topic `bil.events.v1`; partition key `billing_account_id` …". The ADR has no broker: events go through an outbox to in-process handlers. Reading needed: "partition key" = ordering key for per-aggregate in-order dispatch from the outbox.
2. **"Workflow" for delinquency.** REQ-BIL-163: "run each delinquency process as a workflow with durable business-day timers that survives failover … per `REQ-PLT-169`" (also `REQ-PLT-007`, `REQ-PLT-168`). The ADR forbids workflow servers. Under the ADR this must be a domain state machine (DelinquencyProcess plus due-step dates in tables) executed by Hangfire.
3. **"Stamp".** §7.0: "BIL owns one PostgreSQL schema per stamp". REQ-BIL-163 AC: "W the stamp fails over". This implies a multi-stamp (cell) deployment, which neither the ADR nor the summary mentions. Clarification is needed (one stamp = one Container Apps environment plus one Flexible Server?).
4. **Lakehouse.** §15.1 lists "DAT | REQ-BIL-335 | REQ-DAT-009 | DSAR (lakehouse copy)". The ADR says there is no lakehouse; DAT marts are PostgreSQL schemas.
5. **CEL-compatible expression language (D10)** for billing-ledger-rule amount expressions (REQ-BIL-286). The ADR does not name a CEL engine for .NET, and ADR §2 rule 11 requires a stated reason for each dependency. This needs a decision: adopt a CEL library, or build a restricted evaluator with `decimal` semantics.
6. **Append-only ledger triggers.** REQ-BIL-281 (triggers for every role, including the owner) goes beyond ADR rule 1 (no UPDATE/DELETE grants on the app role). They are compatible, but migrations and partition archiving (REQ-BIL-297) must work around the triggers. Detaching or moving a partition must not be blocked.
7. **Vendor references** (Guidewire, TigerBeetle, Fowler) are explicitly "reference only, no product adopted". No conflict.
8. **The PRD does not mention** Kafka, Camunda, microservices or other languages or DBs. REST under `/api/bil/v1/` plus in-process calls matches the modular monolith. Money as `decimal(19,4)` matches NUMERIC.

### (b) With the contract or other PRDs (as noticed)

1. **`ChargesScheduled` consumer.** REQ-BIL-084 says "POL has no handler and does not track it; XMR-F-120". Its "Used by" column cites `REQ-POL-178`, and CCR-BIL-01's rationale cites "POL issuance tracking (REQ-POL-178)". §8.1 lists DAT only.
2. **The PRD self-references contract v1.11** and many rulings (R-xx, D1–D10, XMR-F-xxx). These were not verified against `00-system-contract.md` for this digest.
3. **Bancassurance.** REQ-BIL-123 (Should, P1) records bank-branch payments, but bancassurance is out of P1 unless a partner is signed by G0 (D6). It is conditional scope.

### (c) Internal contradictions

1. **`RefundDisbursed` timing.** REQ-BIL-190, BR-BIL-126 and §7.3.5 say it is published at `bil.refund.paid_point`, with **Greece default ISSUED** (bank accepts the file). But:
   - §8.1 says the trigger is "Refund disbursement **Cleared**";
   - the J-06 diagram publishes after camt.053 Cleared;
   - worked-example step 21 says "Bank debit confirms refund (camt.053); `RefundDisbursed`".

   This affects when POL's `POL_REFUND_DUE` clock stops (the 30-day DMFS deadline).
2. **Notice duration.** BR-BIL-041 says "Greece pack: one month from notification, **UNVERIFIED pending legal review**". §3.1, AC-1 and OI-BIL-01 say the duration is **settled per R-60**.
3. **Levy remittance posting.** Worked-example step 6 posts LA-07 → LA-10 directly. REQ-BIL-272 and REQ-BIL-211 require a `BIL_LEVY_PAYMENT` disbursement via LA-13 in transit. The example simplifies this.
4. **IPT liability point.** The worked example (§4.13) is shown with liability point WRITTEN "for readability", while the Greece default is DUE (LA-27 → LA-06 at each instalment due date). Golden tests must use DUE. The PRD states this, but the main table does not show it.
5. **Open-issue count.** §16.6 says "9 open OIs" but lists 10 IDs (01, 02, 03, 04, 05, 06, 07, 09, 11, 12). It also omits OI-BIL-10, which §16.1 lists as open, so the real total is 11. OI-BIL-12 is also out of order in §16.1.
6. **Duplicate API rows.** §9.1 lists `bil.BillingAccount.moveTerm`, `bil.Refund.decide/get`, `bil.AccountCurrent.dispute`, `bil.Commission.statements/dispute` and `bil.Dsar.restrict/erase` twice. These are harmless but need deduplication in the OpenAPI.
7. **Down-payment allocation in the worked example.** Step 5 accrues commission on collected premium on the bind date (deposit allocated). Step 2 posts written including the levy policyholder share in LA-01 as 350.40 (premium 300 + IPT 45 + levy PH 5.40). This is consistent. The arithmetic was checked: closing cash 152.40 holds.

### (d) Things that cannot be built without a decision

1. **Statutory notice proof methods** (OI-BIL-01). Without them, `PolicyLifecycleRules`/`StatutoryDeliveryRule` cannot be configured and no non-payment cancellation can go live (INV-11).
2. **Tax and legal opinion (D2)** on the IPT liability point, the levy refund on cancellation, IPT on a withdrawal void, and fees in the IPT base. This is a go-live gate. The engine can be built, but the Greek pack values cannot be finalised.
3. **myDATA document types** (OI-BIL-06): CMP mapping; what fiscal document a "premium receipt" (`DT-PREMIUM-RECEIPT`) is under the TRANSACTION trigger; self-billing commission types.
4. **Bank selection and file variants** (OI-BIL-04) and **acquirer selection** (OI-BIL-07). The ISO 20022 adapters, RF vs bank codes and card flows cannot be finished without them.
5. **CEL / expression engine choice** for BillingLedgerRule (D10 vs ADR minimal dependencies).
6. **The meaning of "stamp"** for schema and deployment topology.
7. **Late-payment interest or fees for consumers** (OI-BIL-11). The dishonour and late fee charge types depend on it (A-5).
8. **Default waterfall, tolerances, refund and batch thresholds, agency exposure policy, and commission basis per intermediary type** (§16.5 items 5, 7, 8). These are business sign-offs.

---

## 15. Build notes

**Hardest parts:**
1. **Ledger core:**
   - double-entry, append-only, deferred per-currency balance constraint and triggers;
   - posting through the BillingLedgerRule table with an expression evaluator;
   - linked atomic multi-leg entries; reversal-only correction;
   - snapshot balances plus nightly verification; monthly partitions; hash chain.

   Everything else posts through this, so it must be the first slice. Property tests INV-01…18 and mutation testing are mandated by the ADR.
2. **Charge intake and scheduling:**
   - idempotent on charge_id; delta-set completeness (`set_id`/`set_size`/`index`, 15-minute incomplete alert);
   - written-state posting; quarantine; scheduling rules IMMEDIATE / SPREAD / FOLLOW_PARENT / ACCRUE_ONLY;
   - cent-exact instalment rounding with the remainder on the first or last instalment; minimum-instalment reduction;
   - re-spread of mid-term deltas; credits against unbilled items first;
   - out-of-sequence NET sets; billed-period deltas never editing billed invoices;
   - deterministic replay (NFR-BIL-024).
3. **Allocation and reversals:**
   - deterministic 6-step matching, then fuzzy matching;
   - a configurable waterfall that writes allocation rows: referenced invoice → overdue oldest → within an item tax/levy → fees → premium → due → future;
   - DB-enforced Σ allocation limits under 100 concurrent writers (NFR-BIL-029);
   - two-step cash (LA-09 → LA-10); tolerance absorption;
   - R-transaction reason-code action table; chargebacks; reversal after cancellation or refund (variant B).
4. **Delinquency to statutory cancellation:**
   - the domain state machine (Hangfire-driven) above;
   - DOC proof → CMP clock start at the proven notification time;
   - cure → `ClockMet`; `ClockElapsed` → exactly one request; vulnerable and open-claim holds;
   - never shortened (`BIL-ERR-NOTICE-CLOCK-RUNNING`); agency-bill intermediary-first rule.
5. **Disbursement service:**
   - 9 registered sources with evidence-hash verification;
   - sanctions, VoP and cooling-off gates with fail-closed behaviour;
   - batches with four-eyes; pain.001 out, pain.002 and camt in;
   - state machine with 5 hold sub-states; Rejected and Stopped events to every source.
6. **ISO 20022 file handling** (6 message types, per-bank variants), behind the `BankFileFormat` SPI with sandbox doubles.
7. **Commission:**
   - per-delta and per-collection calculation with splits and overrides;
   - WRITTEN / BILLED / COLLECTED basis; chargebacks; backdated recalculation by difference lines;
   - statements, netting and payment runs; self-billing fiscal.
8. **Daily FIN control-total push** and **POL written reconciliation**, plus the nightly invariant suite.

**Must exist first (upstream):**
- PLT: numbering, authority and approval, audit, time service, calendars, adapter host, batch monitoring, encryption and blind index, outbox.
- MKT: configuration, rounding, the SPIs (`TaxCalculator.treatment`, `PaymentReferenceGenerator`, `BankFileFormat`, `PayeeVerification`, `StatutoryClockSet`), Greece and Cyprus stub packs.
- PFC: charge-type catalogue with treatments and plans offered.
- POL: `ChargeDeltaEmitted` with completeness fields, bind gate.
- PTY: parties, payer role, screening, producer codes, commission agreements.
- CMP: clocks and the fiscal request API (a stub is acceptable).
- DOC: render, deliver and proof (stub).
- WRK: activities.

**Downstream modules that block on BIL:** POL (bind gate `DownPaymentCleared`), CLM, RI, CMP and FIN (disbursement service, `BillingEntryPosted`).

**Suggested slicing (vertical, MVP-first):**
1. **S1 Ledger and FIN feed:**
   - LA chart, entry and line tables with constraints and triggers, BillingLedgerRule plus evaluator;
   - `BillingEntryPosted` via outbox; `bil.Ledger.query/balance`; invariant framework.
2. **S2 Accounts, plans and charge intake:**
   - BillingAccount, plan catalogue and instance, preview and down-payment status;
   - ChargeRecord intake with completeness, quarantine, written entries and scheduling;
   - invoice run, non-fiscal numbering, DOC request, fiscal trigger to the CMP stub.

   This slice runs the worked example's steps 2–3.
3. **S3 Money in (RF, bank transfer):**
   - camt.053/054 intake, Receipt, matching, waterfall, suspense, tolerance;
   - pre-bind deposit → `DownPaymentCleared`; bank reconciliation basics.
4. **S4 SDD:** mandates, pre-notification, pain.008 runs, pain.002/camt returns, reason-code table, re-presentation, repeated-failure switch.
5. **S5 Delinquency and statutory notice:** plans, process, clock integration, cancellation request, reinstatement payment, post-cancellation plan.
6. **S6 Refunds and the disbursement service:**
   - source register (BIL sources first, then CLM, RI, FS, CMP and FIN sources);
   - gates, batches, pain.001; all disbursement events; PayeeAccount system of record.
7. **S7 Agency bill and commission:** collection reports, account current, remittance matching, ageing and exposure; commission calc, statements, payment runs.
8. **S8 Levies and taxes:** levy accrual, periods, `BIL_AUXF_REMIT`, levy payment; IPT accrual with DUE liability point (LA-27); `TAX_REMITTANCE`; BIL-originated fee tax.
9. **S9 Cards and instant (acquirer-dependent),** write-offs, moves, DSAR and retention, migration import and reverse, workbenches polish.
10. **AI** last; all features are optional.

Use the worked example (§4.13, variants A and B) as the golden acceptance test from S2 onward, under both the Greece and Cyprus packs.
