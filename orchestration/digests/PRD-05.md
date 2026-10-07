# Digest — PRD-05 Policy Administration and Transaction Engine (POL)

Source: `core-insurance-prds/PRD-05-policy-administration-transactions.md` (v1.5, 2026-10-07, 2,479 lines, read in full). Binding input stated by the PRD: `00-system-contract.md` v1.11. Checked against `core-insurance-infra/ARCHITECTURE-DECISIONS.md`.

---

## 1. Identity

| Item | Value |
|---|---|
| Module code | **POL** |
| Title | Policy Administration and Transaction Engine |
| Status | v1.5 "Baseline candidate — freeze fixes applied; for design-authority sign-off" (§1.1) |
| Owners | ROLE-14 policy-admin product owner (accountable); lead architect (transaction stack); ROLE-04 policy services lead (journeys); ROLE-13, ROLE-29 co-reviewers; ROLE-24 finance controller reviews charge/proration contract (§1.1) |

**Purpose (§1.2).** POL is the contract engine. It owns policies, policy terms, jobs (submission, policy change, cancellation, reinstatement, rewrite, renewal) and the **bitemporal transaction stack** recording every change to cover "as known at every moment". It turns user intent into bound transactions, materialises **immutable segments** (risk-tree snapshots carrying annual charge rates), emits **charge deltas** ("the only bridge from policy to money"), and serves the policy valid on any date as known at any record time to CLM, DOC, RI, FIN, CMP and CHN. It also runs the Greek statutory lifecycle through CMP-held clocks (contract CD-07).

**Five key decisions (§1.2):** (1) intent log + materialised segments, bitemporally indexed, with a PostgreSQL 18 `WITHOUT OVERLAPS` key making overlapping cover impossible; (2) one reverse-and-reapply algorithm for every out-of-sequence case (also reused for cancellations, prior-term changes, rebase, renewal carry-forward); (3) rates, not amounts, cross the RAT boundary; one proration function for written/earned/unearned (earned + unearned = written); (4) bind is a gate, issue is asynchronous, provisional proof of cover is immediate; (5) 50 baseline wizard screens collapse into 21 screens on one quote/change workspace.

**Non-goals / out of scope (§1.4)** — owned elsewhere, POL only calls them:
- Accounts, parties, addresses, consents, sanctions engine, intermediaries, producer of record, commission → **PTY**.
- Product definition, version resolution, renewal conversion rules, question sets, charge types → **PFC**.
- Rating, worksheets, taxes and levies (via MKT `TaxCalculator`, incl. `treatment` for credits/cancellations, D2), deviations → **RAT** (tax rules MKT).
- UW rules, referrals, declines, contingencies, holds, accumulation → **UW**.
- Billing accounts, plans, payment demands ("ειδοποίηση πληρωμής", D3), refunds and **all money movement (BIL is the only cash executor, D1)**, delinquency, non-payment notice → **BIL**.
- Fiscal documents/myDATA (**CMP is the only issuer of fiscal series and numbers**, D3), Information Centre reporting, clock register → **CMP**.
- Document rendering, delivery proof, cover-note rendering, Documents tab → **DOC**.
- Claims (CLM), cessions (RI), earning runs/IFRS 17 measurement/journals (FIN), portals/partner APIs/AI agent facade/withdrawal-function presentation (CHN), activities/notes/participants/global search (WRK), workflow, audit, authority, maker-checker, numbering, time (PLT), configuration/SPIs/clock values/risk location (MKT).
- Explicit "Won't": US assigned-risk (REQ-POL-049), archiving toggle (REQ-POL-243), transfer between legal entities (REQ-POL-240, Won't/P3). Rewrite to new account is Should; transfer Won't in P1 (§1.5).

---

## 2. Size metrics

Counts from §5.18 and §16.6, re-tallied from the tables (my grep of the REQ rows agrees: 345 rows).

| Measure | Count |
|---|---|
| Functional requirements | **345** (REQ-POL-001…014 anchors + 030…360) |
| [BASELINE] / [ENHANCEMENT] | 304 / 41 |
| Must / Should / Could / Won't | **300 / 36 / 6 / 3** |
| Phase P1 / P2 / P3 / P4 | **330 / 9 / 5 / 1** |
| **Must × P1 (Motor MVP)** | **298** (my tally; 2 Musts are P2: REQ-POL-286 buildings, REQ-POL-304 accumulation feed) |
| Should × P1 | 28; Could P1 2; Won't P1 2 |
| Business rules | 50 (BR-POL-001…050; BR-POL-048 listed out of order after 050) |
| NFRs | 22 (NFR-POL-001…022) |
| Screens | 21 (SCR-POL-01…21), replacing 50 owned baseline reference screens |
| Owned entities | ~20 in §7.1 (incl. technical/read-model rows) |
| Events produced | 27 (§8.1) |
| Events consumed | ~72 distinct event names from 14 producers (§8.2) |
| API operations exposed | ~40 operations in §9.1 table (many grouped rows) |
| AI features | 6 (AI-POL-01…06), all off by default |
| Golden/property suite | ≥ 60 motor golden scenarios; 10 property invariants P1–P10; ≥ 1,000,000 generated sequences per release |

**Build-size estimate: L.** ~300 Must requirements, the heaviest temporal and money logic in the programme (bitemporal segments, reverse-and-reapply with conflict resolution, proration/earning shared with BIL/FIN, NET charge deltas with completeness sets, prior-term ripple, renewal batch at 40k/h), plus 21 dense screens.

---

## 3. Owned entities (§7.1) and state machines (§7.3)

Storage (§7.0): schema `pol`; every table carries `legal_entity_id`, `jurisdiction`, optional `jurisdiction_subdivision`, `created_at`, `created_by`, `record_version`; RLS on `legal_entity_id`. Hybrid storage: append-only `policy_transaction` log with typed JSONB intents validated against a versioned intent schema; immutable `segment` rows referencing a content-addressed `snapshot` (RFC 8785 canonical JSON, SHA-256); bitemporal `valid_period tstzrange` + `record_period tstzrange`. `segment_current` has `PRIMARY KEY (term_id, valid_period WITHOUT OVERLAPS)`; superseded rows move to `segment_history`. `btree_gist`; `PERIOD` FKs from element index rows. Hash partitioning by `policy_id` (32 partitions) for `segment_history`, `charge_delta`, `policy_transaction`. UUIDv7 generated by the app. Valid periods half-open `[from, to)`; record periods UTC; zone conversion only at the edge (XMR-FR-150, D5).

| Entity | Key attributes | Constraints / notes |
|---|---|---|
| **Policy** | policy_id, policy_number, product_code, line_of_business, product_type, account_id (PTY ref), primary_language, predecessor_policy_id, legacy_alias, status_cache | policy_number unique per legal entity; account changes only via move or rewrite (REQ-POL-037). Retention RC-POL-CONTRACT (trigger: later of last term end and last linked claim closure; duration from Greece-pack retention schedule, XMR-D-259) |
| **PolicyTerm** | term_id, term_number (≥1), period tstzrange, term_type, state, outcome (Renewed, NonRenewed, Lapsed, Rewritten, Cancelled, Voided), product_version, artefact_hash, **rating_artefact_hash (pinned)**, resolution_manifest, resolution_hash, configuration_hash_at_inception, currency, day_count, conflict_rule_ref, business_basis (DOMESTIC/FOS/FOE), IFRS 17 *proposed* portfolio/cohort/measurement model, billing_account_ref, payment_plan_ref, producer_of_record_ref, producer_of_service_ref, channel_variant, written_date, first_issued_at, issued_at, head_transaction_id, head_version, predecessor_term_id | (policy_id, term_number) unique; pinned columns immutable after first bind (trigger); `WITHOUT OVERLAPS` on (policy_id, period) "for non-Cancelled-flat terms" |
| **Job** | job_id, job_number, job_type, term_id, state, sub_state, flags (referred, preempted), effective_at, base_transaction_id, intent jsonb, reason code/text, source (cancellation, R-84 list), kind (Standard, Void, Flat), refund_method, scheduled_at, validity_until, channel, actor, on_behalf_of, producer_code, lease_holder/lease_until, rewrite_type, reinstatement_type, acceptance_mode/deadline, renewal_run_item_id | one open Cancellation per term (partial unique index) |
| **QuoteVersion** | version_no, label, draft_risk_tree, question_answers (set code, version, answers, by, at), offering, payment_plan_option, rating_result_ref, totals, uw_issue_refs, state, quoted_at, validity_until, manifest_hash_at_quote, configuration_hash_at_quote | States Draft → Quoted → Superseded / Expired; Quoted → Draft on edit (§7.3.3); ≤ 20 versions per job (SCR-POL-07) |
| **PolicyTransaction** | transaction_id/number, kind, effective_at, recorded_at, sequence (per policy, gap-free), based_on_transaction_id, reverses_ids[], reapplication_of_id, aggregate_id, static_locator, intent, voided_by_cancellation, configuration_hash, artefact_hash, rating_artefact_hash, resolution_hash, gate_evidence, authority_check_ids, correlation_id, actor, on_behalf_of, ai_interaction_id, origin (LIVE, MIGRATION) | **Append-only** (no UPDATE/DELETE grants; trigger rejects). Kinds: Issuance, Change, Cancellation, Reinstatement, Rewrite, Renewal, Reversal, Reapplication, CarryForward, Suspension, Reactivation, Void (REQ-POL-075). State: Bound on insert; "Reversed" is *derived* from links, never a mutable status (§7.3.4) |
| **Segment** | segment_id, term_id, transaction_id, valid_period, record_period, snapshot_hash, rating_result_id, suspended_elements[], charges_summary | Current set non-overlapping; immutable content |
| **Snapshot** | snapshot_hash (PK), body (policy data, elements with static locators, coverages, terms, parties, suspension flags) | Content-addressed, write-once; dedupe (REQ-POL-081, Should) |
| **SegmentElementIndex** | segment_id, element_locator, element_type, natural_key_kind (PLATE, VIN, GREEN_CARD, PARTY), natural_key_normalised, valid_period | Temporal FK (`PERIOD`) to segment validity |
| **RatingResult** | input_hash, rating_artefact_hash, rate_version, rates, worksheet_ref | Write-once; re-materialisation never re-rates (REQ-POL-092) |
| **Element** (in snapshot): Vehicle, PolicyDriver, Building, Location, ScheduledItem | static locator, element type + library version, PFC fields, lineage_locator | Validated against artefact (REQ-PFC-224) |
| **PolicyParty** (in snapshot) | locator, party_id, role (PrimaryNamedInsured, SecondaryNamedInsured, AdditionalNamedInsured, Insured, LossPayee, Mortgagee, Lessor, Assignee, CertificateHolder), relationship, attached element, interest details (contract/loan no., certificate_required, notice_days, co_payee, assignment_document_ref) | Must exist in PTY with role |
| **ChargeDelta** | charge_id (UUIDv7), transaction/term/policy ids, element_locator, coverage_code, charge_type, charge_category, delta_kind (NET, REVERSAL, REAPPLY, ORIGINAL), valid_period, amount decimal(19,4), currency, booking_date, correlation_key, **set_id/set_size/index** (D4), tax_treatment_ref, flags (ri_cedable, commissionable, flat), regulatory_keys, origin | Append-only; Σ per term = cumulative written (REQ-POL-122) |
| **TransactionConflict** | job/aggregate ids, element_locator, field_path, original/your/later values, later_transaction_id, rule_applied, proposal, resolution (ACCEPT_YOURS, DISCARD_YOURS, READD, RULE), decided_by, authority_check_id, ai_interaction_id | Open → Resolved / Obsolete (head moved) (§7.3.5) |
| BindGateResult | gate_code, result (PASS, FAIL, WARN, DEFERRED), reason, evidence_ref | — |
| CancellationDetail | source, reason, refund_method, refund_breakdown, notice_refs, evidence_refs, bil_request_ref, notice_clock_ref | — |
| ProofOfCoverRef | document_ref, cover_note_number (= DOC document number), valid_until | Non-fiscal, no amounts (D3) |
| GreenCardAssignment | term, element_locator, green_card_number, series_range_ref, issued_at | Unique per series; POL is issuer (XMR-F-130) |
| ClockLink | object, clock_code, clock_instance_ref (CMP), started_at, status_cache | — |
| RenewalRun / RenewalRunItem | run, entity, product, expiry window, partition, counts; item idempotency key | Item: Pending → Running → Done/Failed → Retrying → Done/Failed(terminal → exception activity) / Suspended(hold) → Pending (§7.3.6). Retention RC-OPS-LOG |
| PolicyCommandIdempotencyRecord | key, operation, request_hash, result_ref | Technical table, ≥ 7 days (REQ-POL-070) |
| AggravationNotice | notified_at, facts, source, resulting_job_id | — |
| Read models | ActivityGateView, DisclosureDeliveryView, PartyView, AccountView, ProducerOfRecordView, ClaimView, BillingStatusView, ClockView, BureauStatusView, PolicyHoldView | Event-fed; "rebuilt on demand from owner APIs" |

### 3.1 PolicyTerm state machine (§7.3.1, REQ-POL-130)
States: **Scheduled, InForce, PendingCancellation, Cancelled, Expired**. NonRenewed/Lapsed are *outcomes*, not states. No "Suspended" state (AC-9: suspension is element-level, display flag only).

| From | Trigger | To | Guard / event |
|---|---|---|---|
| — | bind, effective > now | Scheduled | gates pass; PolicyBound / RenewalBound / PolicyRewritten |
| — | bind, effective ≤ now | InForce | gates; MTPL not retroactive (REQ-POL-137) |
| Scheduled | start instant reached | InForce | **no event** — state derived on read (REQ-POL-131; §8.1 "Derived InForce", XMR-F-204) |
| Scheduled | flat cancel, rewrite, void | Cancelled | PolicyCancelled / PolicyVoided |
| InForce | cancellation scheduled | PendingCancellation | effective > now; CancellationScheduled |
| PendingCancellation | rescind | InForce | CancellationRescinded |
| PendingCancellation / InForce | cancellation bound | Cancelled | PolicyCancelled |
| InForce | objection/withdrawal exercised | Cancelled (outcome Voided) | clock running; PolicyVoided |
| Cancelled | reinstatement bound | InForce | rules + authority; PolicyReinstated |
| InForce | end reached | Expired | PolicyNonRenewed / PolicyLapsed where outcome applies |
| Cancelled, Expired | policy change | forbidden | POL-ERR-ILLEGAL-TRANSITION |
| Expired | prior-term change | Expired (content changes) | authority; PolicyChanged |

### 3.2 Job state machine (§7.3.2, REQ-POL-050/051)
Canonical states: **Draft, Quoted, Bound, Withdrawn, Declined, NotTaken, Expired, Scheduled, Rescinded**. Flags: **Referred** (blocking UW issues open), **Preempted** (base ≠ head). No "Issued" or "Preempted" state (AC-10). Sub-states only: Submission `Draft.QuickQuote`; Renewal `Draft.Converting`, `Quoted.Offered`, `Quoted.Accepted` (events carry `job_state` + `sub_state`).
Transitions: Draft→Quoted (quote); Quoted→Draft (edit); Quoted→Bound (gates pass, not preempted); Quoted→Scheduled (cancellation, deferred renewal); Scheduled→Bound (effective time / acceptance); Scheduled→Rescinded; Draft/Quoted→Withdrawn; Draft/Quoted→Declined (DeclineIssued); Quoted→NotTaken (customer declines / acceptance deadline passed); Draft→Expired (inactivity), Quoted→Expired (validity).

| Job type | States | Guards | Events |
|---|---|---|---|
| Submission | Draft, Quoted, Bound, Withdrawn, Declined, NotTaken, Expired | knock-outs block quote | SubmissionCreated, QuoteIssued, QuoteExpired, PolicyBound, PolicyIssued, JobWithdrawn, JobNotTaken |
| PolicyChange | Draft, Quoted, Bound, Withdrawn, Expired | term InForce or (prior-term) Expired; not after cancellation date | QuoteIssued, PolicyChanged, JobPreempted |
| Cancellation | Draft, Quoted, Scheduled, Bound, Rescinded, Withdrawn | one open per term | CancellationScheduled/Rescinded, PolicyCancelled, PolicyVoided |
| Reinstatement | Draft, Quoted, Bound, Withdrawn | term Cancelled | PolicyReinstated |
| Rewrite | Draft, Quoted, Bound, Withdrawn, Expired | term InForce, Scheduled or Cancelled | PolicyCancelled, PolicyRewritten |
| Renewal | Draft, Quoted (Offered, Accepted), Scheduled, Bound, Withdrawn, Declined, NotTaken, Expired | no non-renewal decision; expiring term not Cancelled | RenewalCreated/Offered/Bound, PolicyNonRenewed, PolicyLapsed |

Job compatibility per term (BR-POL-010): PolicyChange × n; Cancellation ≤ 1; Reinstatement only when Cancelled and ≤ 1; Rewrite ≤ 1, incompatible with open Cancellation; Renewal ≤ 1 per next term; Suspension/Reactivation are PolicyChange kinds. Transition table checked in the same DB transaction as the state change (REQ-POL-134) and published as data (`pol.StatusModel.get`, REQ-POL-133, Should).

---

## 4. Consumed entities / dependencies (§7.2, §9.2, §15.1)

| Owner | What POL reads | How |
|---|---|---|
| PTY | Account, Party, PartyRole, Address, ContactPoint, Consent, CommunicationPreference, ScreeningResult, ProducerCode, ProducerOfRecord, Vulnerability | `pty.Account.get/create`, `pty.Party.get/search/create`, `pty.PartyRole.assign/query`, `pty.Consent.query`, `pty.CommunicationPreference.resolve`, `pty.Screening.screen`, `pty.ProducerCode.validate`, `pty.ProducerOfRecord.get`, `pty.Vulnerability.query`; events (merges, PoR changes, sanctions, moves) |
| PFC | ProductVersion, artefact, manifest, ElementType, CoverageDef, ChargeType, QuestionSet, Offering, RegulatoryMapping, conversion rules | `pfc.ProductVersion.resolve`, `pfc.Artifact.get`, `pfc.Catalogue.get`, `pfc.ChargeType.list`, `pfc.RegulatoryMapping.get`, `pfc.QuestionSet.get/evaluate`, `pfc.RenewalConversion.convert`, `pfc.Availability.check`, `pfc.Product.describe`, `pfc.PolicyDraft.validate` |
| RAT | Annual rates per element × charge type, worksheets, deviations, tax lines + `TaxCalculator.treatment` result, day-count library, bonus-malus | `rat.Rate.rate`, `rat.Rate.rateBatch` (≤ 400 segments/request, REQ-POL-089), `rat.Worksheet.get/explain/attach`, `rat.BonusMalus.transition`; modes QUICK, ENDORSEMENT, RENEWAL |
| UW | Issues, blocking status, referrals, declines, contingencies, holds, accumulation, external reports, renewal direction, disclosure findings | `uw.Rules.evaluate`, `uw.Issue.blockingStatus`, `uw.Referral.request`, `uw.Decline.create`, `uw.Contingency.list`, `uw.PolicyHold.check`, `uw.Accumulation.check`, `uw.ExternalReport.order`, `uw.RenewalDirection.get`, `uw.DisclosureFinding.get` |
| BIL | Billing account, payment plans, down-payment status, payment/refund events | `bil.BillingAccount.get/attachTerm/moveTerm`, `bil.PaymentPlan.list/select`, `bil.DownPayment.status`; deltas/refunds travel by event |
| CMP | Clocks, bureau status, fiscal registration | `cmp.Clock.start/pause/stop/query`, `cmp.Bureau.status` |
| DOC | Documents, cover note, delivery proof | `doc.Document.request/requestBatch/preview`, `doc.ProofOfCover.issue`, `doc.Delivery.status` (view rebuild only), `doc.Document.listForObject` |
| WRK | Activities, participants, notes, evidence | `wrk.Activity.create`, `wrk.Participant.assignByRules`, notes; `wrk.Activity.blockingStatus` only to rebuild ActivityGateView (never at bind, R-83) |
| FIN | IFRS 17 assignment, period status | `fin.Ifrs17Group.assignment`, `fin.Period.status` |
| RI | Cessions (P3) | `ri.Cession.listByPolicy` |
| CLM | Claims (ClaimView) | events only |
| PLT | Authority, maker-checker, audit, numbering, time, workflow | `plt.Authority.check`, etc. |
| MKT | Configuration, hashes, risk location, cross-border, rounding, business basis, SPIs | `mkt.Configuration.resolve/currentHash`, `mkt.RiskLocation.resolve`, `mkt.CrossBorder.check` (sync at bind), `mkt.Rounding.apply` |

Assumptions on dependencies (§16.2): A-1 RAT returns annual rates and batch-rates; A-2 UW synchronous blocking-status query at four checkpoints; A-3 BIL owns non-payment notice and supplies the date; A-4 DOC renders a cover note within 30 s; A-5 CMP fetches vehicle details via `pol.Policy.get` (events carry locators only).

---

## 5. Events

Envelope per contract §3.4.1 via PLT outbox library (`REQ-PLT-147`); topic name `pol.events.v1`; partition key `policy_id` (`legal_entity_id` for `RenewalRunCompleted`, R-100); gap-free sequence per policy; payloads carry ids and minimum business data, no names/birth dates/addresses (§8).

### 5.1 Produced (§8.1) — 27

| Event | Trigger | Key payload |
|---|---|---|
| SubmissionCreated | submission job created | job, account, product+version, channel, producer code |
| QuoteIssued | version Quoted | job, type, version, product version, premium summary, validity, referred |
| QuoteExpired | validity ends | job, version |
| PolicyBound | submission bound | policy number, term id/number, transaction, period, product version, artefact/resolution/config hashes, PoR, account, payer, plan ref, IFRS 17 *proposed* tags, motor flag |
| PolicyIssued | mandatory issuance steps done (REQ-POL-178) | term, transaction, issued at, document refs |
| PolicyChanged | change, carry-forward, suspension or reactivation bound | transaction, kind, effective date, changed locators/types, vehicle-cover facts (added, removed, suspended, reactivated), prior-term flag |
| CancellationScheduled / CancellationRescinded | scheduling / rescind | job, term, source, reason, date, refund method |
| PolicyCancelled | cancellation bound | transaction, term, source, reason, effective date, refund method, rewrite link, termination clock instance (REQ-POL-356), third-party cover end + clock instance (REQ-POL-357) |
| PolicyVoided | void ab initio | transaction, term, statutory reason, cost-of-cover rule |
| PolicyReinstated | reinstatement bound | transaction, term, gap (none or period), reason |
| PolicyRewritten | rewrite bound | old/new term, rewrite type, new policy id/number |
| RenewalCreated / RenewalOffered / RenewalBound | renewal lifecycle | job, expiring term, product version, conversion summary / offer version, premium, acceptance mode, deadline / new term, transaction, artefact hash, PoR |
| PolicyNonRenewed | insurer decision, or expiry after customer decline | term, source, reason, notice date |
| PolicyLapsed | term expires with outcome Lapsed | term, lapse date |
| TransactionReversed | each reversal in an aggregate | original id, reversal id, aggregate correlation key, affected segment ids, voided-by-cancellation flag, set_id/set_size/index, origin |
| TransactionReapplied | each reapplication | original id, new id, correlation key, set fields |
| JobPreempted | open job's base no longer head | job, preempting transaction |
| ChargeDeltaEmitted | every bound transaction | charge id, term, locator, coverage, charge type/category, delta kind, net amount, currency, valid period, booking date, transaction, correlation key, tax treatment ref (D2), set fields (D4), origin |
| JobWithdrawn, JobNotTaken, ProofOfCoverIssued, OutOfSequenceConflictRaised, PolicyMoved, RenewalRunCompleted | added by CCR-POL-01 (accepted R-34) | see §8.1 |

Completeness rule (D4): a consumer processes a set only when it holds all `set_size` members; incomplete beyond its threshold → exception activity in the consumer.

### 5.2 Consumed (§8.2), with reaction
- **PTY**: AccountMerged / PolicyMoveRequested / AccountStatusChanged (re-point, execute move, refuse when a job is open); PartiesMerged / PartyUnmerged / PartyUpdated (re-point refs without transactions; garaging-address change → activity); ProducerOfRecordChanged (update term refs without transaction); SanctionsHitRaised / Cleared (flag jobs and policies); IntermediaryLicenceChanged (re-evaluate producer gate).
- **PFC**: ProductVersionPublished / Retired / Scheduled (refresh caches; mark quotes stale).
- **RAT**: RateVersionActivated / Withdrawn (open quotes keep their guaranteed artefact; withdrawn → info state); PricingModificationDecided.
- **UW**: UWIssueRaised / Approved / Rejected / Closed, ApprovalInvalidated (Referred flag, gates); DeclineIssued (job Declined); ContingencyResolved / Overdue; RenewalDirectionSet; DisclosureFindingDecided (execute consequence); PolicyHoldActivated / Released (gate and renewal suspension).
- **BIL**: DownPaymentCleared (gate; renewal acceptance by payment); CancellationForNonPaymentRequested (create and bind or hold; idempotent on event id + BIL request id); PaymentReceived / CashAllocated (rescind; auto-reinstate); PaymentPlanChanged; DelinquencyResolved (rescind held/scheduled non-payment cancellation); ChargesScheduled (issuance step done); RefundDisbursed (display; CMP stops `POL_REFUND_DUE`).
- **FIN**: Ifrs17GroupAssigned (store assignment; proposals unchanged); PeriodClosed / PeriodReopened (closed-period guard G5).
- **CLM**: ClaimReported / Closed / Reopened, PaymentIssued (ClaimView for G4 and REQ-POL-141).
- **DOC**: DocumentRendered / Delivered / DeliveryFailed (issuance progress; delivery-based clocks; DisclosureDeliveryView); SignatureCompleted / Declined.
- **CMP**: FiscalDocRegistered / Rejected; BureauEventSubmitted / LagExceeded / FactRejected (corrections only through a policy job, OI-CMP-21); ClockStarted / Warned / Breached / Met; ClockElapsed (waiting-period expiry, R-78).
- **WRK**: ActivityCreated / Completed / Cancelled / Skipped (ActivityGateView).
- **CHN**: DisclosureReceiptRecorded; WithdrawalRequestReceived (backup trigger, idempotent on `withdrawal_request_id`, R-89).
- **PLT**: ConfigChanged (refresh ≤ 10 s); AiToggleChanged / AiKillSwitchActivated (≤ 60 s).
- **DAT**: ModelDriftDetected / BiasThresholdBreached.
- **MKT**: PackActivated / ConfigurationActivated; PackRolledBack (REQ-POL-354 exception queue, never silent re-rate).
- Explicitly **not consumed**: CrossBorderAuthorisationChanged, DocumentBatchCompleted, EvidencePackSealed, ReverificationRequired.

All consumers dedupe on `event_id`.

---

## 6. APIs

### 6.1 Exposed (§9.1) — in-process; REST under `/api/pol/v1/`; all commands need `Idempotency-Key`; RFC 9457 errors with `POL-ERR-*`; DR = dry-run
- Jobs: `pol.Submission.create` (DR), `pol.Job.updateDraft` (DR pricing), `pol.Job.quote`/`requote` (DR), `pol.Job.newVersion`/`copyVersion`/`compareVersions`, `pol.Job.bind` (DR; canonical, R-87), `pol.Job.withdraw`/`notTaken`, `pol.Job.rebase` (DR), `pol.Job.resolveConflicts`, `pol.Job.lease`/`releaseLease`, `pol.Job.list`/`get`.
- Job types: `pol.PolicyChange.create`, `pol.Cancellation.create`/`schedule`/`rescind`, `pol.Withdrawal.submit` (right WITHDRAWAL/OBJECTION; error POL-ERR-RIGHT-EXPIRED), `pol.Reinstatement.create`, `pol.Rewrite.create`, `pol.Suspension.create`/`pol.Reactivation.create`, `pol.Renewal.create`/`offer`/`accept`/`decline`/`nonRenew` (POL-ERR-NOTICE-TOO-LATE), `pol.RenewalRun.start`/`retryItem`/`pause`, `pol.PolicyMove.execute`.
- Queries: `pol.Policy.get`, `pol.Term.get`, `pol.Term.timeline` (validAt/knownAt), `pol.Snapshot.get`, `pol.Policy.getMany` (≤ 200), `pol.Policy.search`, `pol.Earning.compute`, `pol.Charges.reconcile`, `pol.Segment.changes` (cursor feed), `pol.StatusModel.get`, `pol.EffectiveDate.limits`.
- Migration: `pol.Import.policy`/`term` (idempotent per sourceKey, DR), `pol.Import.reverse` (REQ-POL-358), `pol.Import.convertCurrency` (Could, P4).
- GDPR: `pol.Dsar.export`, `pol.Dsar.restrict`.

Named error codes seen: POL-ERR-IDEMPOTENCY-MISMATCH (409), ILLEGAL-TRANSITION, PRODUCT-UNAVAILABLE, PRODUCER-INVALID, LOCKED, VALIDATION, QUICK-QUOTE-NOT-BINDABLE, RATING, GATE-FAILED, PREEMPTED, REBASE-REQUIRED, QUOTE-STALE, CONFLICTS-OPEN, HUMAN-CONFIRMATION-REQUIRED, NOT-PREEMPTED, AUTHORITY-REQUIRED, EFFDATE-LIMIT, AFTER-CANCELLATION, JOB-CONFLICT, RIGHT-EXPIRED, NOTICE-TOO-LATE, OPEN-JOB, NOT-FOUND, MIGRATION-REVERSAL-BLOCKED, SEGMENT-INVARIANT, ACCOUNT-IMMUTABLE, RETROACTIVE-MTPL, OOS-TOO-LARGE.

Idempotency records kept ≥ 7 days (REQ-POL-070). Dry-run uses the same code path in a rolled-back unit of work (REQ-POL-071); no number, row, event or delta from dry-run, quote or failed bind (REQ-POL-129, REQ-POL-030).

### 6.2 Consumed — see section 4. External systems are never called directly (§9.3): vehicle registry / gov.gr Wallet via `MotorDataProvider` (UNVERIFIED availability, OI-POL-13); Information Centre via CMP `BureauAdapter` (channel and deadline UNVERIFIED, OI-POL-06); myDATA via CMP `FiscalDocumentChannel` via BIL; geocoding via `Geocoder` (degraded mode: postcode centroid).

---

## 7. SPIs / country-pack interfaces (§9.2, §10.4)

| SPI | Use in POL | Greece pack | Cyprus stub |
|---|---|---|---|
| `NumberingScheme` (REQ-MKT-096) | policy, job, Green Card numbers | Greek series; Green Card series fed by bureau-allocated ranges | different pattern (REQ-MKT-269) |
| `StatutoryClockSet` (via CMP, REQ-MKT-009) | all clock values; POL holds no durations (REQ-POL-319) | values in MKT §10.4.3 + POL codes; activation per value needs Settled legal status (D7) | withdrawal 30 days; no objection clock; no third-party notice |
| `StatutoryDeliveryRule` (contract R-41) | start time of delivery-based clocks (REQ-POL-186, 356) | proof rules | — |
| `PolicyLifecycleRules` (CCR-POL-03, R-36) | void refund per right/sale mode, notice recipients/periods, reinstatement-without-gap legality, suspension effects, third-party MTPL period, other insurer-cancellation notice rules | implementation | stub |
| `TaxCalculator.treatment` (MKT, applied by RAT; POL never calls it, D2) | tax/levy lines on cancellation/credit/void | IPT not refunded on policyholder cancellation; withdrawal void routed by source | stamp duty treatment |
| `BureauAdapter` (CMP) | bureau reporting of POL facts | Information Centre | file-export stub |
| `MotorDataProvider` (REQ-MKT-105) | plate/VIN pre-fill | registry + gov.gr Wallet | `NotAvailable` |
| `Geocoder` (REQ-MKT-305) | garaging/risk locations, called directly by POL | — | — |
| `IdValidator` scheme `VEHICLE_PLATE` (`normalise`, `searchKey`) | plate normalisation; no plate rule in core (REQ-POL-279) | Greek/Latin look-alike letters | Cypriot pattern |
| `AddressFormatter` | postcodes (5 digits GR) | — | — |
| Unnamed pack-bound telematics provider SPI | P2 telematics bands (REQ-POL-359); "provider is named before P2" | — | — |

Configuration keys (§10.2, MKT model): `pol.effdate.<txnType>.backdate_max/.futuredate_max` (defaults: change 0 d CSR / 30 d UW; submission 0 d back / 60 d future), `pol.bind.gates`, `pol.quote.validity_days` (30), `pol.draft.inactivity_days` (90), `pol.lease.minutes` (30), `pol.oos.max_reversed` (400), `pol.conflict.authority_threshold` (25 EUR), `pol.charges.delta_mode` (NET; final P1–P2), `pol.renewal.lead_days` (45), `pol.renewal.acceptance_mode` (BY_PAYMENT), `pol.renewal.grace_days` (0), `pol.renewal.reoffer_tolerance` (5 EUR / 1%), `pol.reinstate.nogap_window_days` (30), `pol.reinstate.auto_window_days` (15), `pol.suspension.*`, `pol.cancel.reasons.<source>`, `pol.cancel.insurer_notice`, `pol.covernote.enabled/.validity_days` (true / 30), `pol.priorterm.max_terms` (2), `pol.rematerialise.sample_percent` (1.0), `pol.ai.*`. Product flags declared in PFC data: `needsCoverNote`, `suspendable` per charge type, `carryForward=false` field groups, `rewriteOnExistingVersion` (§10.3).

---

## 8. Screens (§6)

Shared conventions (§6.0): PLT staff shell, command palette (examples "new motor quote for 0001234", "endorse 1469673074", "cancel 1469673074", "open renewal 0000716388", "compare T5 T7"), context bar, **one job workspace instead of wizards** (split pane `IB-05`: section navigator / editable panel / always-visible premium and issues panel with live dry-run), autosave (replaces Save Draft), edit lease (replaces Release Lock), read-only policy file with as-of (`validAt`) and `knownAt` for auditors, bound data never edited in place. Semantic states only (loading, empty, error, warning, info, success, permission denied, locked, stale, conflict, pending-approval, AI-suggested/generated, adverse, overdue, breached). Greek/English language switch on every screen without reload or draft loss (R-101; screen missing either language fails release gate); UI language never changes document language. Permissions: `pol.read`, `pol.quote`, `pol.bind`, `pol.change`, `pol.cancel`, `pol.reinstate`, `pol.rewrite`, `pol.renewal.manage`, `pol.renewal.manual`, `pol.conflict.resolve`, `pol.bind.holdIssue`, `pol.ops`, `pol.p2.read`; ABAC on legal entity, LoB, producer scope. Design-guide patterns referenced: IB-01, 02, 03, 05, 07, 08, 09, 10, 11, 12, 13, 14, 16, 17, 18, 19, 21, 22, 24, 26, 28, 29, 31, 32, 33.

| ID | Name | Personas | One line |
|---|---|---|---|
| SCR-POL-01 | Policy search | ROLE-03/04/10/45/43 | 21+ criteria incl. AFM (via PTY), plate (normalised), VIN (ISO 3779 check), Green Card no.; Policy/Job switch |
| SCR-POL-02 | Start quote | ROLE-04/05/06/08/11 | One dialog: account, producer, quote type, effective date-time (MTPL not before now), product list, offering, pre-qualification questions (Greek motor set) |
| SCR-POL-03 | Job workspace: shell + Policy section | quoting roles | Named insureds, policy address, masked AFM, term type/number, dates, written date and rate-as-of (system-set), channel variant, PoR/PoS, assign new policy number (rewrite) |
| SCR-POL-04 | Drivers panel | ROLE-04/05/10 | Embedded PTY person component; licence, issuing country, driver type, year first licensed, claims-free / bonus-malus, accidents, violations, training, usage %; gender never captured |
| SCR-POL-05 | Vehicles + garaging | ROLE-04/05/10 | Plate/VIN registry pre-fill, value, use, security features, assigned drivers, interested parties, Green Card no., cover status with Suspend/Reactivate |
| SCR-POL-06 | Coverages + exclusions/conditions search | ROLE-04/05/10 | Rendered from `pfc.Product.describe`; per-vehicle matrix; statutory items locked |
| SCR-POL-07 | Premium panel, versions, comparison | all quoting | Hero total cost, breakdown with proration factor, versions (≤ 20), compare up to 4 |
| SCR-POL-08 | Bind panel + confirmation | ROLE-04/05/06/08/10 | Gate checklist, payment plan, payer, hold issuance, explicit confirmation, cover note, issuance progress |
| SCR-POL-09 | Account policy transactions + submissions | ROLE-03/04/05/06 | Two lists embedded in PTY account file |
| SCR-POL-10 | Policy file summary | all servicing | Term financials (earned premium, incurred, loss ratio), open jobs, claims, billing, clocks, issuance and bureau status |
| SCR-POL-11 | Policy file contract sections (as-of) | servicing, ROLE-22, ROLE-43 | Read-only panels + Charges section (deltas, booking date, delta kind, invoice status) |
| SCR-POL-12 | Transaction history + compare | ROLE-04/10/22/43 | Grouped by term and aggregate (reversals/reapplications nested), diff tree |
| SCR-POL-13 | Policy change workspace + premium preview | ROLE-04, 03, 10; 05/06 via CHN | Start dialog, diff, prorated change per coverage, out-of-sequence notice, preempted-jobs list |
| SCR-POL-14 | Cancellation | ROLE-04/03/13; SYS-02 | Source, reason, evidence, refund method, effective date, refund per charge type incl. non-refundable tax, notices; Cancel now / Schedule / Rescind |
| SCR-POL-15 | Reinstatement | ROLE-04/13 | With/without gap, reason, voided transactions to reapply, gap warning "will be reported to the Information Centre" |
| SCR-POL-16 | Rewrite | ROLE-04/10/11 | Full term / remainder / new account |
| SCR-POL-17 | Renewal management | ROLE-04/10/45 | Conversion results, carried-forward changes, offer, acceptance mode and deadline, non-renewal |
| SCR-POL-18 | Out-of-sequence conflicts + preemption | ROLE-04/10/45 | Original / yours / later value, rule proposal, accept or discard per row, authority |
| SCR-POL-19 | Prior-term change entry | ROLE-04/10 | Term selector (authority `POL.PriorTermChange`), ripple table per term |
| SCR-POL-20 | Statutory clock panel | ROLE-04/29/03 | Clock, start, deadline, kind, status, linked action |
| SCR-POL-21 | Operations: renewal runs + exception queues | ROLE-45, ROLE-38 | Renewal partitions, gate/issuance failures, conflicts, preempted jobs, held non-payment cancellations, bureau lag |

Inventory coverage (§6.22, §15.3): 50 owned baseline screens → 46 specified (13 with replaced/N-A rows), 4 replaced wholly, 0 missing. N/A in Greece: SSN, Base State, Assigned Risk, US MVR/violations, Good Student, US coverages (BI/PD, MedPay, UM/UIM, PIP, Tort, Stacked, Mexico), demo items, Reload PCFs, archiving toggle.

---

## 9. Regulatory, tax and statutory rules

### 9.1 Rules the source states explicitly (with IDs)

**Cooling-off: objection and withdrawal (Law 2496/1997; Directive (EU) 2023/2673 / Law 5317/2026)**

| Rule | Value stated | IDs | Status |
|---|---|---|---|
| Objection when policy deviates from application | **one month**, start at policy delivery; exercise → **void ab initio** | OBL-CON Art. 2 §5; REQ-POL-305, 307, 218; BR-POL-030; clock `POL_OBJECTION` | Verified 2026-10-07 |
| Objection when required information/terms not provided | **14 days** from delivery; **right expires 10 months after first premium payment** (hard stop) | Art. 2 §6; REQ-POL-306; BR-POL-031; clock `POL_OBJECTION_INFO` (WAITING_PERIOD) | Verified |
| Withdrawal, non-life contracts > 1 year | **14 days from receipt of the policy**; **suspended while objection runs**; **not available where immediate cover was requested** | Art. 8 (paragraph UNVERIFIED, OI-POL-03); REQ-POL-308, 309; BR-POL-032; clock `POL_WITHDRAWAL_LONGTERM` | Partly verified |
| Distance withdrawal | **14 days from the later of conclusion or receipt of terms**; if terms never received, pack long stop **12 months + 14 days from conclusion** (Greece pack, via CHN variant "terms not received") | REQ-POL-308; REQ-CHN-161; clock `POL_WITHDRAWAL_DISTANCE` | Settled (R-55) |
| Online withdrawal function | Directive (EU) 2023/2673 applies from **19 June 2026**; Greek Law 5317/2026 (Gazette A 108/10.07.2026) Arts 69–72; **Art. 72: no charge on withdrawal from an insurance contract** → Greece pack refunds **total paid**, no charge for cover enjoyed | OBL-DMFS; REQ-POL-308, 314, 207; BR-POL-034; AC-4 | Settled |
| Refund after distance withdrawal | within **30 days** — Law 2251/1994 Art. 3ιστ (inserted by Law 5317/2026 Art. 72); clock `POL_REFUND_DUE` variant DISTANCE_WITHDRAWAL starts at receipt of withdrawal declaration; variant OTHER starts at the void; CMP stops it on BIL `RefundDisbursed` at pack key `bil.refund.paid_point` (Greece: ISSUED) | REQ-POL-219; BR-POL-035; AC-3 | Distance variant verified; other variants UNVERIFIED (OI-POL-04) |
| Objection/withdrawal clock hygiene | WAITING_PERIOD clocks: stop with outcome Exercised on void; cancel on termination for another reason; on `ClockElapsed` record right lapsed | REQ-POL-310 (R-78) | — |
| Exercise mechanics | typed command `pol.Withdrawal.submit` (right OBJECTION or WITHDRAWAL) → Cancellation of kind Void, effective inception; durable acknowledgement through DOC (`DT-WITHDRAWAL-ACK`); backup on CHN `WithdrawalRequestReceived` | REQ-POL-307, 314 | — |

Note: the PRD's own earlier "refund minus cost of cover" prompt statement is superseded by Art. 72 for distance withdrawal (AC-4). For objection and long-term withdrawal the refund is "per pack rule" (BR-POL-034), with the amount not stated.

**Non-payment (Law 2496/1997 Art. 6).** Insurer may terminate by written notice; cover ends **one month after notification** (Settled R-60, K-01). The prompt's two-week variant is superseded (AC-1). BIL owns notice and WAITING_PERIOD clock `BIL_NONPAY_NOTICE`; POL cancels only on `CancellationForNonPaymentRequested`, at the BIL-supplied date, and holds the job if the notice reference is missing (REQ-POL-012, 210, 311; BR-POL-033). Vulnerable-customer and open-claim exceptions route to a PSR. Auto-rescind when arrears are cleared before effect (REQ-POL-211).

**Disclosure and aggravation (Arts 3–4).** Written questions presumed the only material facts → store question-set version and answers on the quote (REQ-POL-149). Aggravation notified **within 14 days**; insurer may cancel or adjust → aggravation intake hands to UW (`UW_AGGRAVATION_ACTION`), POL executes (REQ-POL-312). Insurer termination after non-disclosure/aggravation takes effect after a notice period, **15 days per the clock register (UNVERIFIED, OI-UW-02)**: WAITING_PERIOD clock `POL_INSURER_TERMINATION_NOTICE` starts at proven delivery of the notice; cancellation binds at the elapse instant, "no date arithmetic in POL" (REQ-POL-356, 221; BR-POL-042). An insurer-proposed premium increase needs explicit customer acceptance (gate CUSTOMER_ACCEPTANCE, REQ-POL-204).

**Motor (MTPL).**
- No retroactive MTPL inception: new business and reinstatement-with-gap effective time never before the bind instant, **no override** (REQ-POL-137; BR-POL-020 country-final; Directive 2009/103/EC as amended by 2021/2118). MTPL coverage non-removable on vehicles (PFC final, REQ-PFC-088).
- Third-party effect of termination: P.D. 237/1986 Art. 11a — third-party cover ends only after a notice period, **16 days per the clock register (UNVERIFIED, OI-POL-16 / XMR-OQ-002)**; clock `POL_MTPL_THIRDPARTY_NOTICE`, started at proven delivery; `PolicyCancelled`/`PolicyVoided`/`PolicyLapsed` carry the expected third-party cover end (REQ-POL-357; BR-POL-049).
- Lapse grace for MTPL renewals: **0 days** (BR-POL-028; REQ-POL-254 example).
- Policyholder cancellation of MTPL vehicle cover for reasons other than sale, scrappage, plate deposit, change of insurer or export may require evidence (REQ-POL-220, Should; BR-POL-043).
- Suspension on plate deposit is **market practice, not a statutory right** (AC-7; OI-POL-07); product option, Greece default enabled (REQ-POL-236).

**Information Centre obligations as stated (Law 5113/2024 Art. 15, amending Art. 27b §4, 6, 8 of P.D. 237/1986; OBL-MOT, verified via taxheaven 2026-10-07).** Insurers report to the Information Centre: vehicle registration numbers, policy numbers, validity periods, **every case where the policy becomes invalid or no longer covers a registered vehicle**, and **Green Card numbers**; **data kept 7 years**; method and deadlines set by **ministerial decision** (deadline UNVERIFIED, OI-POL-06). POL's duties:
- Publish in its events every fact changing whether/how a registered vehicle is covered: issue, renewal bound, vehicle added/removed/replaced, plate change, suspension, reactivation, cancellation, void, lapse, reinstatement (with gap data), Green Card number (REQ-POL-315; BR-POL-036). AC-5 widened the prompt's triggers to suspension, vehicle replacement and void.
- Vehicle replacement emits two bureau-relevant facts in `PolicyChanged` (REQ-POL-195); single-vehicle removal is a policy change with bureau reporting (REQ-POL-224).
- Reinstatement with gap publishes the gap period for bureau reporting (REQ-POL-226).
- Void reports no cover from inception or from the void date, per pack rule (REQ-POL-218).
- Lapse triggers bureau reporting (REQ-POL-254).
- Store Green Card number per vehicle per term; POL is the issuer from a pack series fed by **bureau-allocated ranges**; exhausted range → operations activity, step pending (REQ-POL-185; XMR-F-130; OI-POL-11 closed).
- CMP owns transport; G-5 target: **100% of motor policy events handed to CMP within 5 minutes of bind**. Interim route (XMR-D-265): CMP manual file with daily lag monitoring (OI-POL-06).
- Show bureau status per transaction; warn on `BureauLagExceeded`; show `BureauFactRejected`; correct only via a policy job (REQ-POL-316; §8.2).
- Migration: the first core event for a converted motor policy must not duplicate the legacy bureau record (§14.y).
- Open question: whether reinstatement without gap is lawful once the bureau has been told the vehicle was uninsured (OI-POL-15).

**Tax.**
- Greek IPT, Law 5177/2025 Art. 43, rates by class "for example **15% general, 20% fire**" (OBL-TAX). POL never computes tax (REQ-POL-124).
- Circulars ΠΟΛ 1028/2017 and ΠΟΛ 1032/2018: **premium tax not refunded on cancellation**. Whether they still apply after Law 5177/2025 is part of the single tax opinion (D2). Greece pack default: IPT non-refundable on policyholder cancellation; withdrawal void "routed by cancellation source"; **a refund net of tax is never produced** for distance withdrawal (REQ-POL-207, 215; BR-POL-041).
- Auxiliary Fund levy treatment recorded once in `REQ-MKT-322`; guarded golden expectations until the opinion lands (OI-POL-08 closed per D2).
- myDATA: MARK before delivery of premium receipts; TRANSACTION trigger for motor; **never block bind on MARK** (REQ-POL-177). Cover note is non-fiscal, carries **no premium, tax or other amounts**, numbered with the DOC document number (REQ-POL-176; D3; OI-POL-01 closed unless the tax opinion is negative).

**Other stated obligations.** IDD (Directive 2016/97; Law 4583/2018): bind gate requires durable-medium delivery evidence of IPID, pre-contractual info, terms, intermediary info and demands-and-needs per channel; never silently skipped (REQ-POL-172). Sanctions screening of policyholder, insureds, payer and interested parties at bind, party addition and reinstatement (REQ-POL-171). IFRS 17 (Reg. 2021/2036): POL proposes portfolio, annual cohort and measurement model (default PAA); FIN assigns (REQ-POL-034). Solvency II lineage via regulatory mapping keys on deltas (REQ-POL-120). AI Act deployer duties (§11). eIDAS: explicit renewal acceptance or change consent via signature or authenticated confirmation (REQ-POL-257). EAA: WCAG 2.2 AA (NFR-POL-018). DORA: POL is T1; declare ICT third parties (REQ-POL-352). GDPR: no personal data leaves the EU. Nat-cat for businesses (Law 5116/2024, Verify; P3).

### 9.2 Referenced but NOT specified (gaps)
- Withdrawal Art. 8 paragraph number (OI-POL-03).
- 30-day refund duty outside distance sales (OI-POL-04).
- Information Centre deadline, format, channel: ministerial decision not obtained (OI-POL-06).
- Legal basis of plate-deposit suspension (OI-POL-07).
- Legal form of bank interests (assignment vs loss payee) and required notices (OI-POL-09; AC-8).
- Acceptable proof of non-payment notification (OI-POL-02 / XMR-OQ-001).
- Insurer termination 15-day value (OI-UW-02) and third-party 16-day value (OI-POL-16).
- Reinstatement-without-gap lawfulness for MTPL after bureau notification (OI-POL-15).
- Refund amounts for objection and long-term withdrawal ("pack rule"); "cost-of-cover rule" field in `PolicyVoided`, content unspecified.
- Renewal notice and non-renewal notice lead values: "pack value (UNVERIFIED; activation requires Settled status)" (BR-POL-047). Only examples use 45 days (renewal) and 30 days (non-renewal).
- Whether ΠΟΛ circulars survive Law 5177/2025 (tax opinion, D2).
- Retention durations: in the programme retention schedule, not in the PRD.
- Batch iterator source citation UNVERIFIED (§3.2).

### 9.3 Deferred to configuration / country pack
All clock durations (`StatutoryClockSet` via CMP; REQ-POL-319); refund methods per source (PFC REQ-PFC-134: ProRata, ShortRate, Flat, MinimumRetained, FullRefund); tax treatment (`TaxCalculator.treatment`); void refunds, notice recipients, other insurer-cancellation notice periods (`PolicyLifecycleRules`, `pol.cancel.insurer_notice`); suspension reasons, evidence, max days (365), extension (BR-POL-039); reinstatement no-gap window (30 days non-payment, 0 insurer, BR-POL-037); renewal acceptance mode, grace and lead; cover-note enablement and validity (30 days); effective-date limits; numbering formats; rounding (`mkt.Rounding.apply`).

---

## 10. Greek-market specifics

- **AFM**: never entered or stored in POL. Held by PTY, shown masked unless `pol.p2.read` (REQ-POL-300; SCR-POL-03). Searchable via PTY with audited P2 searches (REQ-POL-014, 320).
- **myDATA**: CMP-owned via BIL; never blocks bind; documents needing a MARK requested only after `FiscalDocRegistered` (REQ-POL-177, 178).
- **gov.gr Wallet / vehicle registry** pre-fill through `MotorDataProvider` (Should, REQ-POL-278; availability UNVERIFIED OI-POL-13); degraded mode is manual entry.
- **Information Centre**: see 9.1. Green Card numbering from bureau ranges.
- **Greek plates**: normalisation of Greek/Latin look-alike letters, spaces and hyphens ("ikx-1234" ≡ "ΙΚΧ1234") via `IdValidator` `VEHICLE_PLATE` (REQ-POL-279); accent/case/script-insensitive search (REQ-POL-014).
- **Language**: Greek binding in Greece for documents; policy has a primary language from account preference; UI GR/EN switch (R-101); glossary of record PRD-18 §6 (e.g. job = εργασία συμβολαίου, cancellation = ακύρωση, termination by notice = καταγγελία σύμβασης, statutory clock = νόμιμη προθεσμία; "αναστολή" reserved for suspension of cover). Payment demand label "ειδοποίηση πληρωμής" (D3). † marks working translations.
- **Display of term end**: e.g. "έως 14/01/2028 24:00" per pack display rule; half-open periods (REQ-POL-041).
- **Address**: postcode 5 digits; County → regional unit (περιφερειακή ενότητα); State → region.
- **EUR**: thresholds in EUR (€25 conflict authority; €5/1% re-offer tolerance); amounts `decimal(19,4)`; multi-currency deltas only P3 (REQ-POL-127). Euro changeover tool for later markets (P4).
- **Bonus-malus**: next class via `rat.BonusMalus.transition` at renewal (REQ-POL-249); claims-free/bonus-malus captured on drivers.
- **Greek motor coverages** from PFC: MTPL with statutory minimums, own damage, theft, fire, glass, legal protection, roadside assistance, driver personal accident (§6.0). Taxes: IPT and Auxiliary Fund levy as separate charge types.
- **Gender-neutral pricing**: gender never captured for rating (SCR-POL-04).
- **Bancassurance** out of P1 unless a bank partner is signed by G0 (D6).
- **Fairfax USD reporting via FIN** is mentioned for group reporting (§13).

---

## 11. Controls (§12)

**Authority types registered with PLT (`REQ-PLT-100`):** `POL.EffectiveDateOverride` (product, txn type, days), `POL.BackdatedCoverReduction`, `POL.ConflictResolution` (premium effect; threshold €25, BR-POL-009), `POL.ClosedPeriodChange` (entity, period), `POL.InsurerCancellation`, `POL.RefundMethodOverride`, `POL.Reinstatement` (days since cancellation, gap), `POL.Rewrite`, `POL.NonRenewal`, `POL.PriorTermChange` (terms back; default max 2, BR-POL-044), `POL.Bind` (staff binding beyond producer authority / limits). UW referral authority stays with UW (`REQ-UW-010`); producer bind authority with PTY (`REQ-PTY-205`).

**Maker-checker (`REQ-PLT-004`):** bulk non-renewal (REQ-POL-267); legal-entity transfer (P3); data-change scripts on POL data (REQ-POL-344, the only non-transaction correction path); cancelling a running renewal run in production; POL configuration changes at entity layer or above; insurer-initiated void outside statutory rights.

**Segregation of duties (`REQ-PLT-082`):** override requester ≠ approver; a producer cannot approve conflict resolutions on own book above threshold; bulk non-renewal maker ≠ checker; a user cannot reinstate a policy they cancelled for insurer reasons without a second authority holder.

**Audit (`REQ-PLT-002`):** every job lifecycle step (with field-level before/after on draft intents), leases, effective-date defaults/edits/overrides (REQ-POL-143), gate evaluations (stored as evidence record on the transaction, REQ-POL-189), conflict decisions, reinstatement/rewrite decisions, renewal offers/acceptances/non-renewals, moves, party re-points, data-change scripts, AI outcomes, configuration hash used. NFR-POL-016: 100% of bound transactions linked to AuditEvent, correlation id, config hash. Lineage by business keys (job, transaction, aggregate, charge ids), not trace id (REQ-POL-087, D5).

**Human confirmation:** explicit bind confirmation; AI-initiated binds need the delegating human's confirmation (REQ-POL-181).

**GDPR:** P0–P3 classification per attribute (§7.1); minimal events (NFR-POL-015, 0 P2/P3 fields in payload schemas); plates/VINs/loan numbers encrypted at rest; precise geocodes field-level encrypted (NFR-POL-014); party identifiers never stored; DSAR export and restriction APIs; erasure blocked by retention and recorded (REQ-POL-338, 339).

---

## 12. AI features (§11)

All optional, **off by default** at tenant level, each with a non-AI path; called only via PLT EU model gateway; none decides binding, declining, cancelling, pricing acceptance or conflict outcomes. Kill switch ≤ 60 s (REQ-POL-348); `AiInteractionRecord` on every touched record (REQ-POL-353).

| ID | Feature | Classification | Phase | MVP need |
|---|---|---|---|---|
| AI-POL-01 | Document-to-quote pre-fill (registration certificate, previous schedule, claims-history certificate) | High-risk controls by default (uncertain) | P1 pilot, off by default | Not needed (manual + registry pre-fill) |
| AI-POL-02 | Change-request interpreter (free text → draft intent) | High-risk controls by default | P2 | No |
| AI-POL-03 | Conflict resolution assistant | High-risk controls by default; suggestion only | P2 | No |
| AI-POL-04 | Renewal review brief | Limited risk with high-risk controls (uncertain) | P3 | No |
| AI-POL-05 | Premium change explainer | Limited risk (Art. 50) | P1 pilot, off by default | No (deterministic worksheet template fallback) |
| AI-POL-06 | Policy data anomaly detection | Minimal risk with high-risk controls where it could lead to referral | P3 | No |

Each has monitoring profiles (MP-B/D/E), bias bands 0.8–1.25, fallbacks, GR/EN transparency texts. Thresholds are "initial values needing DAT calibration" (§16.6).

---

## 13. Open issues, assumptions, CCRs (§16)

| ID | Subject | Status |
|---|---|---|
| OI-POL-01 | Cover note before MARK | **Closed** (D3): non-fiscal, no amounts; reopen only if tax opinion negative |
| OI-POL-02 | Non-payment notice | Duration closed (one month, R-60); **proof of notification open** (XMR-OQ-001) |
| OI-POL-03 | Withdrawal Art. 8 paragraph and interaction with Law 2251/1994 | **Open** |
| OI-POL-04 | 30-day refund outside distance sales | **Open** (distance variant settled) |
| OI-POL-05 | Greek transposition of Dir. 2023/2673 | **Closed** (Law 5317/2026) |
| OI-POL-06 | Information Centre deadline/format/channel | **Open**; interim CMP manual file (XMR-D-265) |
| OI-POL-07 | Plate-deposit suspension legal basis / premium credit vs extension | **Open** |
| OI-POL-08 | Auxiliary Fund levy refund | **Closed** (D2, REQ-MKT-322) |
| OI-POL-09 | Legal form of bank interests | **Open** |
| OI-POL-10 | IFRS 17 group assignment | **Closed** (FIN system of record) |
| OI-POL-11 | Green Card numbering | **Closed** (POL issues from bureau ranges) |
| OI-POL-12 | Retention trigger | **Closed by citation** |
| OI-POL-13 | Registry / gov.gr Wallet availability | **Open** |
| OI-POL-14 | Renewal acceptance mode | **Closed** (D7: payment as acceptance + explicit acceptance) |
| OI-POL-15 | Reinstatement without gap for MTPL after bureau notified | **Open** |
| OI-POL-16 | Third-party MTPL notice (Art. 11a, 16 days) | **Open** |

Assumptions A-1…A-8 (§16.2), including A-7 annual terms only for MVP (D6) and A-8 migration scenario B (book converted at renewal) until the board confirms before wave W5.

Risks RK-POL-01…08 (§16.3). Highest: reverse-and-reapply defects (Medium/High), statutory facts wrong (Medium/High), bureau lag causing uninsured-vehicle fines (Medium/High), cover note judged fiscal (Low/High).

CCRs (§16.4), **all Accepted**: CCR-POL-01 six new events (R-34); CCR-POL-02 clock codes `POL_OBJECTION_INFO`, `POL_WITHDRAWAL_LONGTERM`, `POL_REFUND_DUE`, `POL_NONRENEWAL_NOTICE` (R-35); CCR-POL-03 SPI `PolicyLifecycleRules` (R-36); CCR-POL-04 FIN anchor REQ-FIN-011, amended to event-driven assignment (R-37).

"Ten decisions before building" (§16.5): items 2, 5 and 8 are marked decided (D4, D3, D7). Still open: (1) confirm the storage model and **PostgreSQL 18 temporal keys**; (3) LATER_WINS default and threshold; (4) implement the "floating at resolution, pinned for the term" rating-artefact rule; (6) approve the bind-gate set per channel; (7) approve the Greek statutory values; (9) plate-deposit suspension; (10) implement rulings R-34…R-37, R-52, R-55 and D1–D10.

---

## 14. Conflicts and ambiguities found

### (a) With the infra/stack (ARCHITECTURE-DECISIONS.md)
1. **PostgreSQL 18 is normative; the ADR says PostgreSQL 17.** REQ-POL-079 ("PostgreSQL 18 temporal key `UNIQUE (term_id, valid_period WITHOUT OVERLAPS)`"), §1.2 decision 1, §7.0 ("module's PostgreSQL 18 database", `PERIOD` FKs, `uuidv7()`), NFR-POL-020 and §16.5 decision 1. The ADR says "PostgreSQL 17 (move to 18 for temporal constraints once available on the Azure managed service)", and rule 1 says "exclusion constraints". On PG 17: use `EXCLUDE USING gist (term_id WITH =, valid_period WITH &&)` with btree_gist. `PERIOD` foreign keys need a trigger or constraint-trigger substitute. UUIDv7 is generated by the application, which the PRD already allows.
2. **Workflow engine.** §2 SYS-02 "Statutory clock workflows … run by CMP on PLT workflow engine"; §1.4 "Workflow engine … PLT"; REQ-POL-178 "orchestrate issuance after bind as a workflow (PLT)"; `plt` "workflow" in §9.2 (REQ-PLT-007). The ADR rules out workflow servers and BPM: state and deadlines go in domain tables, and Hangfire runs due work. Build issuance orchestration as a POL-owned state record plus outbox/Hangfire steps.
3. **Lakehouse.** §8.1: "DAT ingests every event **for the lakehouse**." The ADR rules out data lakehouses; reporting marts are PostgreSQL schemas.
4. **Broker vocabulary.** "Topic `pol.events.v1`; partition key `policy_id`" (§8, R-100). NFR-POL-015 "**Schema registry** lint". There is no broker. Treat "topic" as a logical outbox stream name and "partition key" as an ordering key for the in-order dispatcher. A schema registry has to be some in-repo artefact (e.g. JSON Schema files linted in CI).
5. **"Adapter host".** §9.3 "SPIs run in the PLT adapter host"; OBL-RES "external SPIs run in the EU adapter host". In a modular monolith, is this a separate deployable? Needs a decision.
6. **"Per stamp"** throughput (NFR-POL-007, 008) implies deployment stamps. The ADR has no such concept; Container Apps scale-out only.
7. **Vendor names** (Socotra, Guidewire) appear only as research evidence (§3.2, §16.7). No vendor product is required.
8. **Hash-partitioning** 32 partitions (§7.0) is compatible with PostgreSQL. Note it for EF Core migrations.

### (b) With the system contract / other PRDs (as noticed)
- Event consumer lists differ between §8.1 and CCR-POL-01's proposed text. Examples: `ProofOfCoverIssued` lists CHN, DAT in §8.1 but CHN, CMP, DAT in the CCR; `PolicyMoved` lists BIL, CMP, DAT vs PTY, BIL, WRK, CHN, DAT; `OutOfSequenceConflictRaised` lists DAT vs WRK, DAT. §8.1 says its list is authoritative (consumers with handlers, XMR-CR-ALL-02).
- CCR-POL-04 originally had a synchronous FIN call at bind with the group pinned. The amended ruling and REQ-POL-034 have FIN assign asynchronously, with POL holding proposals only. Consistent now, but the CCR text body is stale.
- `ClockView` (§7.1) lists ClockStarted/Warned/Met/Breached, while §8.2 also consumes `ClockElapsed`. Minor.
- RAT is a Wave-2 peer cited "by anchors only" (REQ-RAT-001…009). Many deeper RAT IDs are cited (REQ-RAT-035, 039, 040, 041, 058, 063, 123, 138, 163, 164, 229) and need verification against PRD-03. Same for UW, BIL, CMP, DOC and other later-wave IDs cited by number.

### (c) Internal contradictions
1. **Renewal lead vs renewal-notice deadline.** REQ-POL-245 creates the job at `expiry − lead_days`, "not earlier" (45 days → 2027-04-17). REQ-POL-318 / BR-POL-047 set the `POL_RENEWAL_NOTICE` deadline at `expiry − pack lead` (example 45 days → 2027-04-17). With equal values the offer must be delivered the same day the job is created. REQ-POL-250's example offers on 2027-04-10, before the creation date allowed by REQ-POL-245. REQ-POL-318's example creates the job on 2027-04-01 with a 45-day lead. The relationship between `pol.renewal.lead_days` (job creation) and the pack's notice lead (deadline) is undefined; job lead must be greater than notice lead.
2. **Segment coverage invariant vs reinstatement with gap.** REQ-POL-079 says current segments must "exactly cover the term's in-force span". REQ-POL-226 says no segment covers the gap. BR-POL-001 carves out "excluding suspension-free gaps only for reinstatement with gap", which is unclear. The commit-time check needs an explicit gap model.
3. **PolicyTerm WITHOUT OVERLAPS "for non-Cancelled-flat terms"** (§7.1) is a conditional temporal key. PG temporal PK/UNIQUE cannot be partial; it needs an exclusion constraint with a WHERE. The rewrite boundary must also be exact.
4. **Insurer cancellation date**: REQ-POL-208 has POL compute "earliest date allowed by the pack's notice rule" for other grounds (example 30 days), with key `pol.cancel.insurer_notice`. REQ-POL-221/356 forbid date arithmetic for non-disclosure/aggravation. Two mechanisms for insurer notice. Acceptable, but the boundary must be explicit.
5. **REQ-POL-240** is Won't with phase P3, while §1.5 lists "transfer between legal entities" as a P3 capability.
6. **Cover note numbering**: REQ-POL-176 uses the DOC document number; §7.1 ProofOfCoverRef says "(= the DOC document number, XMR-F-130)"; §16.4 has no conflict. BR-POL-046 says validity "until policy documents issued or N days (30)". A-4 has DOC render within 30 s, G-4 is ≤ 60 s p95. Consistent but worth noting.
7. BR-POL-048 is listed after BR-POL-050. Ordering only.

### (d) Cannot be built without a decision
- **Policy number format.** Not specified anywhere. Only "formatted by the Greece pack scheme" (REQ-POL-030), `NumberingScheme` (REQ-MKT-096), "Greek series", unique per legal entity, and Cyprus "different pattern". Palette examples are inconsistent ("1469673074" 10 digits, "0001234"/"0123456" 7 digits, "0000716388" 10 digits). Job, transaction, Green Card and account number formats are also unspecified. Rules that are stated: assigned at first bind; no number consumed by dry-run or failed bind (gap policy "released per gap policy", REQ-POL-182); unchanged across renewals, reinstatements and rewrites unless rewrite to new account or "assign new policy number" (REQ-POL-031, 234; BR-POL-045); transaction numbers monotonic per policy, reversal/reapplication get new numbers (REQ-POL-047); terms 1-based and never reused (REQ-POL-032); first core term after renewal conversion = legacy term count + 1 (REQ-POL-341); legacy numbers may be kept when the pack allows a legacy series (§14.y).
- Greek statutory values to activate (D7): renewal/non-renewal notice leads, 15-day insurer termination, 16-day third-party notice, non-distance refund duty.
- Bind gate set per channel for motor MVP (§16.5 #6).
- Suspension on plate deposit: offer it or not; premium credit vs extension (OI-POL-07).
- Information Centre transport and deadline (OI-POL-06).
- PG 17 vs 18 (see (a)1).
- Issuance orchestration mechanism without a workflow engine (see (a)2).
- Objection / long-term withdrawal refund amounts (pack rule unspecified).

---

## 15. Build notes

**Hardest parts.**
1. **Reverse-and-reapply engine** (§5.4, normative steps 1–11): detect → guards G1–G5 → reverse latest first → apply → reapply earliest first (ties by original sequence) → field-level conflict check (LATER_WINS default, EARLIER_WINS, REFER; element-removed always refers; never silently ignore an instruction) → validate and rate only changed segments under the **term-pinned** rating artefact (ENDORSEMENT mode) → do not re-emit flat fees → NET delta per element × charge type × period between pre- and post-aggregate current segments → one atomic DB transaction under the head-version check → `TransactionReversed`/`TransactionReapplied` with set fields → WRK activities for claims and bureau re-report. It must also cover recursion (an aggregate later reversed, REQ-POL-099), G2 voided-by-cancellation, G5 closed periods (deltas booked in the open period), max 400 reversals, and performance of 52 reversals in ≤ 5 s p95 and 400 in ≤ 30 s.
2. **Bitemporal segment store** with immutable content-addressed snapshots, `knownAt` queries, non-overlap/coverage invariants enforced in the DB, and nightly 1% re-materialisation with byte-identical hashes.
3. **Proration/earning shared function** (annual rate × days ÷ basis via RAT day-count library), with exact earned + unearned = written after rounding residuals. Written premium (premium/surcharge/discount, booking-date basis) and written charges (all categories) are distinct measures (REQ-POL-117, 355).
4. **Preemption/rebase/carry-forward** and **prior-term ripple** with per-term deltas, all-or-nothing.
5. **Renewal batch iterator** at ≥ 40,000/h with partition checkpoints, idempotent items and hold suspension.
6. **Clock-driven statutory flows**: delivery-triggered start, WAITING_PERIOD vs DEADLINE semantics, all values from CMP/MKT.

**Must exist first.** PLT outbox + idempotency + audit + numbering + time service + authority; MKT configuration/hash and SPI scaffolding (`NumberingScheme`, `IdValidator`, `PolicyLifecycleRules`, `StatutoryClockSet`) with the Greece pack and Cyprus stub; PFC product-version resolve / describe / validate / charge types / conflict rule / refund methods; RAT rate + day-count library (or stubs honouring the "annual rates per element × charge type" contract); PTY party/account/screening/producer; CMP clock API. BIL/DOC/WRK/CLM can initially be stubs fed by event read models (bind must never depend synchronously on them, NFR-POL-009).

**Suggested slicing (tracer bullets, Motor MVP).**
1. Policy/term/transaction/segment schema with DB invariants, intent log, materialisation and `pol.Policy.get` (validAt/knownAt), plus property tests P2/P3/P5/P9.
2. Submission → quote → bind (minimal gates) → policy number → charge deltas (NET, set fields) → `PolicyBound`; proration + `pol.Earning.compute`; P1/P4/P7/P8.
3. In-sequence policy change with preview and diff; preemption + rebase; leases.
4. Out-of-sequence aggregate with conflicts (SCR-POL-18), guards G1–G4, then G5.
5. Cancellation (sources, refund methods, schedule/rescind, BIL non-payment intake, flat), void/withdrawal/objection with clocks, refund-due clock.
6. Reinstatement (no-gap as reversal; with gap), rewrite variants, suspension/reactivation.
7. Renewal engine (batch iterator, conversion, offer, BY_PAYMENT acceptance, lapse, non-renewal, notice clocks) and carry-forward; prior-term changes.
8. Bind-gate framework full set, issuance orchestration, cover note, Green Card, bureau facts.
9. Policy file screens, search, operations queues, migration import/reverse, pack-rollback handling; AI features last (all off by default).
