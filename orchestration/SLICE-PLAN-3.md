# SLICE PLAN 3 — policy servicing thin slice (mid-term change, E2E-03 cancellation with refund, E2E-04 renewal)

**Status:** planned 2026-10-08 (planner, from `SLICE-3-PLANNING-HANDOVER.md`). **Decisions:** D-SL3-01…D-SL3-14.
**Process:** as slice 2 with the 2026-10-08 changes: **every builder and every reviewer runs on Sonnet 5.5
(`model: "sonnet"`, D-USR-16)**; PR workflow with a quick local gate (D-PRG-19); PITFALLS self-check (D-PRG-20); hot
files split (D-PRG-21); batched PRs of ≤ 3 WPs from different modules (D-PRG-22). **6–8 concurrent builder agents**
(coordinator, 2026-10-08), reviewers in addition and in parallel. Deep, adversarial first-round review for temporal,
money/ledger and security WPs; each WP is cut so that a review takes about 45 minutes or less.

**S0 precondition:** the D-PRG-21 hot-file split has merged (per-module permission files, dev users in their own file,
self-maintaining contract/operation count tests). The plan assumes it has. If it has not merged when S0 starts, the
orchestrator merges it first; otherwise expect conflicts in `appsettings*.json`, `GeneratedContractTests.cs` and
`DevelopmentSignInTests.cs` on almost every PR.

## 1. Goal

A staff user services an in-force motor policy after the sale:

1. **Mid-term change.** Change the vehicle's rating data or replace the vehicle with an effective date. POL re-rates the
   remaining term under the term's pinned artefacts and emits NET charge deltas. BIL bills an additional premium or
   credits a return premium. FIN posts. The history keeps the old segment, and claims whose cover basis changed get
   `ReverificationRequired`.
2. **Policyholder cancellation with refund (E2E-03).** Cancel at day 120. POL computes a pro-rata premium credit; the
   IPT is not credited (GR pack treatment, provisional). BIL issues a credit note with a fiscal credit document through
   the CMP stub, proposes a refund from the credit balance and pays it through the slice-2 disbursement service. FIN
   posts balanced journals with the IPT payable unchanged.
3. **Renewal (E2E-04, thin).** Inside the renewal window, staff "Renew now". POL copies the expiring risk tree, resolves
   the product version for the new term, re-rates, evaluates UW, offers and records explicit acceptance, then binds the
   new term (`RenewalBound`). BIL invoices term 2 through the CMP stub, and FIN posts.

All three run on `docker compose` and are proven by automated E2E tests (API + Playwright) in a new CI job `e2e03`.
They run on a **time-shifted** stack, using a Development-only, forward-only clock advance (PRD-18 §17.1 "time-shifted
environment").

## 2. Scope and OUT list

**IN** (D-SL3-01):
- the POL temporal fixes of D-SL2-09 (§4);
- the in-sequence policy change: vehicle rating fields, vehicle replacement (REQ-POL-195), garaging postcode where it is
  a rating input;
- policyholder cancellation "now" and flat cancellation of a Scheduled term (REQ-POL-217);
- pro-rata refund method per product;
- the tax treatment from `TaxCalculator.treatment`;
- BIL credit note, refund (authority, maker ≠ checker above the threshold, screening, VoP stub, disbursement source
  `BIL_REFUND`), `RefundApproved`/`RefundDisbursed`;
- CMP fiscal credit request (stub, role CREDIT, correlated to the original);
- FIN rule set v3 for credits and refunds, with the REQ-FIN-182 tax check;
- manual renewal with explicit acceptance;
- CLM re-verification on POL changes;
- UI for all of the above;
- E2E-03, E2E-04 (thin) and the change journey (API + UI), with a CI job.

**OUT** (each with the reason, D-SL3-01/02):

| Out | Reason |
|---|---|
| **Out-of-sequence endorsements** (reverse-and-reapply, conflicts SCR-POL-18, guards G2–G5, E2E-11) | The hardest temporal algorithm in the programme (PRD-05 §5.4, 11 normative steps). Not needed by E2E-03/04. In-sequence changes (effective ≥ the term's latest bound transaction) prove the segment store, the deltas and re-verification. Fails closed: `POL-ERR-OUT-OF-SEQUENCE` (D-SL3-02). Planned as its own slice with a deep review |
| Non-payment cancellation (E2E-07) | Needs the CMP clock engine (WAITING_PERIOD, `BIL_NONPAY_NOTICE`), delinquency, DOC statutory notice with proof (OI-DOC-03 / XMR-OQ-001 open). Slice 4 |
| Distance withdrawal (E2E-08) | Needs the CHN withdrawal function and `pol.Withdrawal.submit`. The IPT/levy treatment of the void is open (F-401, REQ-MKT-331 `REVERSE_AS_VOID` vs `INSURER_BEARS`, Pending). Slice 4 |
| Scheduled (future-dated) cancellation, rescind, the SYS-02 scheduler | Needs a Hangfire executor at the effective instant. "Cancel now" and flat cancellation prove the money path |
| Reinstatement, rewrite, suspension, policyholder change, producer change | Not in the journeys. A policyholder change would need `pol.policy` versioning (§4 b) |
| Renewal batch run, lapse, non-renewal, BY_PAYMENT acceptance, renewal/non-renewal notice clocks, conversion with grandfathering, bonus-malus | Renewal is manual ("Renew now", REQ-POL-258) with explicit acceptance (REQ-POL-257). `POL_RENEWAL_NOTICE` has no statutory value (UNVERIFIED placeholder, PRD-17 §9b). BY_PAYMENT needs BIL renewal down-payment intake (REQ-BIL-366) |
| DOC documents (endorsement schedule, cancellation notice, offer letter, credit-note rendering) | DOC is not built. Notices are recorded as "not sent (DOC not built)" |
| Bureau facts (COVER_END, vehicle replaced), RI, IFRS 17 assignment and earning run, commission chargeback, DAC write-off | Modules not built; motor RI is XoL-only (A-01) |
| Instalments and re-spread, refund-hold windows, card refunds | ANNUAL plan only (D-SLC-10) |
| More than one open change job per term, preemption/rebase UI | One open PolicyChange per term (partial unique index). Bind checks base = head, else `POL-ERR-PREEMPTED` |

## 3. Journeys and REQ mapping (PRD-18 §17.3 step tables, cut)

### 3.1 E2E-03 Policyholder cancellation → refund → fiscal credit note

Setup: the E2E-01 policy (MOTOR-GR, ANNUAL), paid in full at day 0. The stack clock is advanced **+120 days**.
Expected amounts (provisional, illustrative tariff): premium 430.00 × 245/365 = **288.63** credit (TERM_RATIO, D-SL3-04).
The IPT is not credited (KEEP_NOT_REDUCED, provisional, D-SL3-05). The refund of **288.63** is auto-approved within the
EUR 500 limit (PRD-06 §10.2 default, illustrative).

| # | PRD-18 step | In slice? | Owner (WP) | Requirement ids | Assertion in the slice |
|---|---|---|---|---|---|
| 1 | Back-office cancellation job, source `Policyholder` | **Yes** (staff UI, no WRK) | POL (SL3-POL-CANCEL), UI (SL3-UI-POL-JOBS) | REQ-POL-205, -208, -209 (Cancel now only), -004 | Source from the code list; effective = request time; job Draft → Quoted → Bound |
| 2 | Refund method by source, per element × charge type, tax treatment | **Yes** | POL (CANCEL, ENGINE), PFC (MOTOR11), RAT (PRORATE), MKT (TREATMENT) | REQ-POL-206, -207, -214, -215, REQ-PFC-134 (subset), -116, REQ-RAT-004, -155, -156 (TERM_RATIO, ACT/365F), REQ-MKT-330, -331 | Premium credit = −288.63; IPT delta 0 with treatment `KEEP_NOT_REDUCED`, `legalStatus` PendingOpinion, `provisional: true` |
| 3 | `PolicyCancelled`; negative `ChargeDeltaEmitted` | **Yes** | POL (CANCEL) | REQ-POL-005, -119, -122, -216, -212 (notices recorded as not sent) | One delta per element × charge type; Σ deltas per term = cumulative written; term Cancelled |
| 4 | Bureau COVER_END | No (D-SL3-01) | — | REQ-POL-315 | — |
| 5 | Credit billed at once, planned items stopped, credit note against the original invoice | **Yes** | BIL (SL3-BIL-CREDIT) | REQ-BIL-074, -091, -073, -079, -319, INV-05 | Credit note `CREDIT_NOTE` references invoice 1; no IPT-payable debit from a cancellation entry |
| 6 | Fiscal credit correlated to the original MARK | **Yes (stub)** | CMP (SL3-CMP-CREDIT), BIL | REQ-BIL-096, -097, REQ-CMP-032 (subset), BR-CMP-004 | Idempotency key role CREDIT; exactly one credit document; codes `UNMAPPED-OQ-012` (D-SL3-07) |
| 7 | Refund from the credit balance: authority, maker-checker, sanctions, VoP, payout method | **Yes** | BIL (SL3-BIL-REFUND), PLT (SUPPORT) | REQ-BIL-007, -181, -182, -183, -184, -186 (verified IBAN only), -187 (payer), -188, -189, -191, -354 (`BIL_REFUND`) | Auto-approve ≤ 500.00; above that, a PLT approval by another user; screening/VoP stubs fail closed |
| 8 | `RefundApproved`, `RefundDisbursed` | **Yes** | BIL (REFUND) | REQ-BIL-190 (paid point ISSUED, D-SL3-09) | `RefundDisbursed` once, at Issued. The `POL_REFUND_DUE` clock is out (no CMP clocks) |
| 9 | FIN: pro-rata credit, IPT payable not reduced, commission chargeback, DAC write-off | **Partly** (no commission/DAC) | FIN (SL3-FIN-RULES) | REQ-FIN-036, -182, -183, -297 (NET only), GF-04 (subset) | Every journal balanced; Dr GL-2110 / Cr GL-1210 for the credit; GL-2410 unchanged; refund clearing nets to zero |
| 10 | RI negative deltas | No | — | REQ-RI-075 | — |

### 3.2 E2E-04 Renewal (thin)

Setup: the E2E-01 policy; the clock is advanced to **expiry − 30 days**, inside `pol.renewal.lead_days` = 45 (PRD-05
§10.2 default, product configuration).

| # | PRD-18 step | In slice? | Owner (WP) | Requirement ids | Assertion in the slice |
|---|---|---|---|---|---|
| 1 | Renewal job in the window; artefact pinned | **Partly**: "Renew now" (REQ-POL-258) for one term, no batch | POL (SL3-POL-RENEW) | REQ-POL-245 (window check), -246, -258, -263 (`RenewalCreated`) | One open renewal per term; outside the window → `POL-ERR-VALIDATION` |
| 2 | PFC conversion 3.2 → 4.0 with grandfathering | **Partly**: version resolution only (1.0 → 1.1 where effective), no conversion rules | PFC (MOTOR11), POL | REQ-POL-246, REQ-PFC-001 (resolve) | The new term pins the version resolved at its start date |
| 3 | Renewal rating with cap and change explanation | **Partly**: RENEWAL mode, no cap, no explanation | RAT (PRORATE), POL | REQ-POL-249 (subset), REQ-RAT-002 (subset) | Worksheet stored; term 2 annual premium shown next to term 1 |
| 4 | UW renewal checkpoint referral | **Yes** (existing PRE_BIND rules; referral decided by `uwsenior`) | POL, UW (no UW code change) | REQ-POL-201, -260 (subset), D-UW-01 | A referred renewal cannot be accepted until approved |
| 5 | Offer document, `RenewalOffered`, notice clock | **Partly**: `RenewalOffered`, no document, no clock | POL | REQ-POL-250 (subset), -263 | Job `Quoted.Offered` |
| 6 | Acceptance (explicit, or payment) | **Partly**: explicit acceptance by staff, channel STAFF | POL, UI | REQ-POL-257, -253 (explicit only) | Records channel, actor and time |
| 7 | `RenewalBound`; charges for the new term | **Yes** | POL | REQ-POL-005, -033, -263 | New term n+1 with `predecessor_term_id`; charge deltas; Σ = term written |
| 8 | IFRS 17 assignment | No | — | REQ-FIN-011 | — |
| 9 | Invoice, fiscal doc, MARK receipt as E2E-01 steps 9–11 | **Yes** (stub fiscal) | BIL (CREDIT: RenewalBound intake), CMP (existing), FIN (existing rules) | REQ-BIL-002, -096 | Term 2 invoice; journals balanced |
| 10 | RI | No | — | — | — |

### 3.3 Mid-term change (POL X-2 in-sequence, part of E2E-11)

| # | Step | Owner (WP) | Requirement ids | Assertion |
|---|---|---|---|---|
| 1 | Start a change with an effective date (defaults: now; backdating within the limits) | POL (SL3-POL-CHANGE) | REQ-POL-190, -008, -135, -136, -104 (G1) | Limits: change 0 days back for CSR, 30 days for Staff.Underwriter (PRD-05 §10.2 defaults). Before the latest bound transaction → `POL-ERR-OUT-OF-SEQUENCE` |
| 2 | Edit the vehicle or replace it; preview before/after annual, prorated change, taxes, total | POL, RAT, UI | REQ-POL-191 (subset), -192, -193, -195, -126 | The dry-run equals the real bind (POL P8) |
| 3 | Bind: supersede segments, NET deltas, `PolicyChanged` | POL (CHANGE, ENGINE, TEMPORAL) | REQ-POL-005, -093, -115, -119, -122, -123, -197, -129 | Old segment kept in history; the new segments' record time > the watermark; a +debit gets IPT `APPLY`; a −credit gets `KEEP_NOT_REDUCED` |
| 4 | BIL bills an additional premium, or credits and proposes a refund | BIL (CREDIT, REFUND) | REQ-BIL-073, -074, -091, -181 | Invoice or credit note per term |
| 5 | FIN posts | FIN (RULES) | REQ-FIN-036, -183 | Balanced; IPT on an endorsement credit unchanged |
| 6 | Open claim whose cover basis changed → `ReverificationRequired`; handler keeps or adopts | CLM (SL3-CLM-REVERIFY), UI-CLM | REQ-CLM-002, -057, -058 (subset), REQ-POL-107 (G4, subset) | Exactly one event per claim and cause; the claim keeps its old ref until adoption |

## 4. Temporal design (resolves D-SL2-09) — recommendation adopted as D-SL3-03

**(a) knownAt race → a per-policy record-time watermark, stamped under the policy lock.** Recommended over a settle
horizon (command duration is unbounded, and replica clock skew needs a guess) and over commit timestamps (they bypass
`IClock`, so the time-shifted E2E would break).

1. **Writers.** Every POL command that records anything for a policy first runs
   `SELECT … FROM pol.policy WHERE policy_id = $1 FOR UPDATE` (new policies: in the same transaction as the insert).
   Only after the lock is held does it take its record time:
   `t = max(IClock.Now, policy.last_recorded_at + 1 µs)`.
   - Every row the command writes (term version, segments, transaction, charge lines, job state, closing `recorded_to`)
     uses that single `t`.
   - The command sets `pol.policy.last_recorded_at = t`.
   - Writers are serialised per policy, so each writer's `t` is strictly greater than every **committed** watermark.
     The second of two racing commands returns `POL-ERR-STALE` or waits; it never fails with a 500 (PITFALLS 15).
2. **Readers** (`pol.Snapshot.get`, `pol.Policy.get`, `pol.Term.get/timeline`, as-of reads) take no lock. They read the
   policy's committed `last_recorded_at = W` and use **effective knownAt = min(requested knownAt or now, W)**.
   - Any row that commits later has `recorded_from > W ≥ effective knownAt`, so the answer can never change.
   - Clock skew between api and worker replicas does not matter, because nothing compares the reader's clock with the
     writer's.
   - Snapshot refs carry the effective knownAt.
   - A ref whose knownAt is greater than the current watermark was never issued by POL. It is refused as forged; this
     subsumes the slice-2 "future knownAt" check (PITFALLS 13).
3. **Responses** show "as known at <effective knownAt>". The UI displays it next to the as-of date.

**(b) `pol.policy` versioning → freeze now, version when needed.** In slice 3 nothing changes a policy-level attribute
(policyholder, account and producer changes are OUT).
- The migration adds `last_recorded_at` and a trigger that refuses any UPDATE of a content column (number, legal entity,
  jurisdiction, product code, policyholder, account, `recorded_at`). Only `last_recorded_at` and `record_version` may
  change.
- The immutability is now enforced, not assumed. It is proven by an app-role test.
- When a policyholder change or rewrite lands, the mutable attributes move to an append-only bitemporal
  `pol.policy_version` (valid + record periods, exclusion constraint, the same watermark), and snapshot reads join it.
  This design is recorded so that the later WP does not re-decide it.

**(c) `superseded` + successor ref → live metadata outside the content hash.**
- `pol.Snapshot.get` returns
  `supersession: { superseded: bool, successorRef?: string, supersededAt?: instant }`
  beside the immutable `content` and `contentHash`.
- It is computed at read time: the content hash at (validAt, current watermark) is compared with the content hash of
  the ref.
- **A different segment id with identical content is not superseded.** A mid-term split leaves earlier dates
  unchanged, so this avoids re-verification noise.
- The bytes and hash of `content` stay identical on every re-read (POL P5); only `supersession` can change.

**(d) Claims → `ReverificationRequired` in CLM.**
- CLM consumes `PolicyChanged` and `PolicyCancelled` (and later `TransactionReversed`). For each open claim on the
  policy whose loss date is at or after the change's effective date (REQ-CLM-057), it reads the snapshot at the loss
  date.
- If the snapshot is superseded (c), CLM publishes `ReverificationRequired` (old ref, new ref, cause event id) **once per
  claim and cause**, enforced by a unique index. It sets `snapshot_status = ReverificationRequired` and never changes
  the claim's ref, reserves or cover by itself (REQ-CLM-002).
- The handler keeps or adopts the new ref with a reason (`clm.Coverage.reverify`, REQ-CLM-058). Adoption that removes
  the exposure's cover sets `coverageInQuestion`, and new payments on that exposure are refused until it is cleared.
- POL itself never mutates claims (G4). It does not consume `ReverificationRequired` (PRD-05 §8.2).

**Out-of-sequence → cut, fail closed (D-SL3-02).** A change or cancellation whose effective time is earlier than the
effective time of the term's latest bound non-reversed transaction is refused with `POL-ERR-OUT-OF-SEQUENCE` (422,
typed in S0). The check runs under the policy lock, so it is race-free. The rule is property-tested.

**Day count and segment arithmetic (D-SL3-04).**
- Days are whole **Europe/Athens calendar dates**: `days(from, to) = AthensDate(to) − AthensDate(from)`. Periods are
  half-open.
- A segment from day 0 14:23 to day 120 14:00 has 120 days, and the rest of the term has 245 days; together they make
  exactly the term's days.
- TERM_RATIO: amount = annual rate × segment days ÷ term days (365 or 366). Rounding is MKT's premium rule per element
  × charge type. Two rules close the residuals:
  - the credit of a flat or same-day cancellation equals the written amount exactly;
  - rounding residuals stay with the elapsed part, so earned + unearned = written.

## 5. Batches, WPs and the critical path

Each WP lists its **real dependency**: **C** = typed contracts only (codes against generated fakes), **M:<WP>** = that
WP's implementation must be merged. Reviews: **deep** = adversarial with probe tests (temporal, money/ledger,
security); **light** = checklist plus a visual review for UI. Estimates are builder wall-clock hours on this laptop.
All agents run on Sonnet 5.5.

### S0 — foundations and contracts (8 WPs, start together)

| WP | Module (owned area) | Scope | PRD / REQ | Depends on | Review | PITFALLS | Est. |
|---|---|---|---|---|---|---|---|
| **SL3-CONTRACTS** | contracts + generated `*.Contracts` (all modules) | Type every new op and event (§6) and regenerate; samples valid; error codes incl. `POL-ERR-OUT-OF-SEQUENCE` | D-API-06/06a; §6 | none | light (orchestrator + reviewer: completeness and additivity) | 12, 21, 22, 23 | 2.5 h |
| **SL3-POL-TEMPORAL** | POL `Persistence/`, `Queries/` (reader, snapshots, search), `Commands/JobSupport.cs`, `Domain/Codes.cs` | §4 (a)(b)(c): watermark, lock-then-stamp helper, knownAt clamp, forged-ref refusal, `pol.policy` freeze trigger, supersession; **the whole slice-3 POL schema migration** (job columns for source/refund method/acceptance/base txn/expiring term; term `predecessor_term_id`, cancelled state; charge line `transaction_kind`/`cancellation_source`/`treatment_rule_id`/`treatment_rule_version`; partial unique indexes: one open Cancellation, one open PolicyChange, one open Renewal per term) | REQ-POL-002, -007, -079 (subset), -086, -134; D-SL2-09 | C (supersession fields; can start on storage before C merges) | **deep (temporal)** | 13, 14, 15, 17, 22, 24, 34 | 4 h |
| **SL3-POL-ENGINE** | POL `Domain/Servicing/` (new folder, **no DB, no migration**) + its tests | Pure servicing engine: given current segments (annual rates per element × charge type), a term and an intent (change with new rates / end cover at t / new term), produce new segments and NET deltas per element × charge type × period; Athens day count; TERM_RATIO and ACT/365F; flat-cancel exact; residual rule; calls `IProration` (RAT) and the tax-line port through interfaces. Property tests: Σ deltas = cumulative written (P7), split symmetry, reversal exactness | REQ-POL-115, -116, -119, -121 (NET), -122, -123, -214; REQ-RAT-004 | C | **deep (money)** | 10, 11, 14, 17 | 3.5 h |
| **SL3-MKT-TREATMENT** | MKT `Services/`, `Domain/`; `CountryPacks.GR/Configuration` | Implement `ITaxCalculator.TreatmentAsync` (D2) with GR rule rows from REQ-MKT-331 (all PendingOpinion → `provisional`; refused in Production per D-SLC-09); cancellation source code list (REQ-POL-205) as an MKT code list; `RULE_MISSING` for any levy/stamp line (no rule); forbid a net-of-tax withdrawal (TCK-TAX-NET-REFUND vector); CY stub `REDUCE_PRO_RATA` | REQ-MKT-087, -330, -331, -332 (subset), REQ-POL-205 | C | **deep (money)** | 10, 11, 24 | 2.5 h |
| **SL3-PFC-MOTOR11** | PFC `Seed/`, artefact validation | Publish **MOTOR-GR 1.1** (write-once; 1.0 untouched): day count TERM_RATIO (provisional, PRD-03 recommendation), refund method per source (Policyholder → ProRata *illustrative*; DistanceWithdrawal → FullRefund *Settled*, Art. 72; other sources absent → fail closed), mid-term change permissions (vehicle fields, replace vehicle), renewal window; resolution by date picks 1.1 from its effective date | REQ-PFC-010, -066 (subset), -134 (subset), -135, -116 | none | light | 10, 24 | 1.5 h |
| **SL3-RAT-PRORATE** | RAT `Services/`, `Api/` | `rat.Proration.prorate` (TERM_RATIO, ACT/365F; refuses a convention the artefact does not declare, `RAT-ERR-CONVENTION`); ENDORSEMENT and RENEWAL rating modes under the term-pinned artefact; tax lines on credits/debits through `TaxCalculator.treatment` carrying ruleId/ruleVersion/legalStatus/provisional | REQ-RAT-004, -155, -156, -165, -009 (subset), REQ-POL-093, -124 | C (treatment via fake) | **deep (money)** | 10, 11, 12, 14 | 3 h |
| **SL3-PLT-SUPPORT** | PLT `Time/`, dev-users file, authority registration | (1) **Dev clock advance**: `POST /dev/clock/advance {days|hours}`, Development + `Platform:Time:Mode=Shiftable` only, Platform.Admin, audited, **forward-only**, offset in `plt.dev_clock` read by api and worker (≤ 1 s cache); never registered in Production (startup refusal test). (2) Role **Staff.BillingManager** + dev user `billingmgr`; `superuser` gains it (SoD unchanged). (3) Authority types `BIL.Refund` (amount, currency, payee changed; illustrative grants: Staff.Billing auto ≤ 500.00, Staff.BillingManager ≤ 5,000.00) and `POL.EffectiveDateOverride` registered | REQ-PLT-332 (subset), REQ-BIL-188, -326; PRD-05 §12 | C | **deep (security)** | 4, 5, 18, 35 | 2.5 h |
| **SL3-E2E-HARNESS** | `tests/e2e/` (support, stack), `.github/workflows/ci.yml` (e2e03 job) | `run-e2e03.sh` (own project `coreins-e2e03`, ports 27000+, image tag `coreins-host:e2e03`, Shiftable clock, executable bit); helpers: `advanceClock(days)`, dev sign-in by exact display name, wait-for-complete-journal-set, IBAN-not-in-URL guard; CI job `e2e03` running a placeholder health spec until SL3-E2E | PRD-18 §17.1; XMR-FR-250 | C (clock endpoint path) | light | 29, 30, 32, 33, 35 | 2 h |

### S1 — servicing behaviour (8 WPs)

| WP | Module (owned area) | Scope | PRD / REQ | Depends on | Review | PITFALLS | Est. |
|---|---|---|---|---|---|---|---|
| **SL3-POL-CHANGE** | POL `Commands/Change/`, `Api/ChangeController.cs`, `tests/…/Policy/Change/` | `pol.PolicyChange.create`; change jobs reuse `updateDraft`/`quote`/`bind` (dry-run = real); effective-date limits (CSR 0 d, UW 30 d back) with `POL.EffectiveDateOverride`; G1; in-sequence guard; vehicle field edit and replace; preview + diff; bind under the lock with base = head (`POL-ERR-PREEMPTED`); segments via ENGINE, rates via RAT ENDORSEMENT; `PolicyChanged` + `ChargeDeltaEmitted` with set fields and `transactionKind` | REQ-POL-190, -191 (subset), -192, -193, -195, -197, -104, -008, -135, -136, -138, -093, -005, -129 | M: TEMPORAL, ENGINE; C: RAT, MKT | **deep (temporal + money)** | 10–15, 17, 21, 22 | 4 h |
| **SL3-POL-CANCEL** | POL `Commands/Cancellation/`, `Api/CancellationController.cs`, `tests/…/Policy/Cancellation/` | `pol.Cancellation.create` (source Policyholder; others refused, fail closed); refund method from the artefact by source; "Cancel now" (effective = request time) and flat cancel of a Scheduled term; quote shows the refund per charge type incl. non-refundable tax; bind → term Cancelled (new term version), credit deltas via ENGINE, tax via RAT/treatment; `PolicyCancelled`; changes after the cancellation refused (G1) | REQ-POL-205, -206, -207, -208, -209 (now only), -214, -215, -216, -217, -212 (recorded only), -004 | M: TEMPORAL, ENGINE; C | **deep (temporal + money)** | 10–15, 17 | 3.5 h |
| **SL3-POL-RENEW** | POL `Commands/Renewal/`, `Api/RenewalController.cs`, `tests/…/Policy/Renewal/` | `pol.Renewal.create` ("Renew now" in the window), copy the risk tree at expiry, resolve the product version at the new term start, RENEWAL rating, PRE_BIND UW (referral path reused), `offer`, `accept` (explicit, STAFF channel), bind new term n+1 (Scheduled, `predecessor_term_id`, pinned artefacts), stale check: expiring head moved → `POL-ERR-REBASE-REQUIRED`; `RenewalCreated/Offered/Bound` | REQ-POL-245 (window), -246, -249 (subset), -250 (subset), -253 (explicit), -257, -258, -263, -033 | M: TEMPORAL; C | **deep (temporal)** | 5, 6, 13–15, 17 | 4 h |
| **SL3-BIL-CREDIT** | BIL intake/commands/ledger + **BIL's S1 migration** | Negative deltas: credits against unbilled items first, then billed credit items; `PolicyCancelled` stops planned items; **CREDIT_NOTE** invoice referencing the original (gapless series); treatment validation of tax deltas (quarantine on mismatch); fiscal request role CREDIT to CMP; mid-term debit → immediate invoice; `RenewalBound` → attach term n+1 to the account and invoice it; sealed entries + `BillingEntryPosted` with `transactionKind`, `cancellationSource`, `treatmentRuleId` dims | REQ-BIL-073, -074, -076, -079, -089, -091, -096, -097, -319, INV-05 | C (CMP, MKT fakes) | **deep (money/ledger)** | 8–12 | 4 h |
| **SL3-CMP-CREDIT** | CMP `Commands/`, `Domain/`, stub channel | `cmp.FiscalDocument.request` role CREDIT with `correlatedDocumentId`; idempotency key = source type + id + role + revision; one credit per source revision; stub returns `STUB-`/`UNMAPPED-OQ-012` codes; refuses a CREDIT without an original | REQ-CMP-001, -030, -032 (subset), BR-CMP-004 | C | light (+ money checklist) | 9, 10, 12 | 2 h |
| **SL3-FIN-RULES** | FIN `Seed/` (rule set v3), `Posting/` | Rules for the new BIL entry types (credit written/billed, refund approved/released/cleared); REQ-FIN-182/183 check: any entry that reduces GL-2410 IPT payable where the treatment says NOT_REDUCE (or a missing treatment) → suspended `TAX_RULE_VIOLATION`; accounts *Illustrative (PRD-09)*; refund clearing nets to zero per refund | REQ-FIN-036, -182, -183, -297 (NET), GF-04 (subset) | C (BIL entry contract) | **deep (ledger)** | 8, 10, 11, 12 | 3 h |
| **SL3-CLM-REVERIFY** | CLM `Events/`, `Commands/Reverify*`, migration (CLM's S1 migration) | §4 (d): consume `PolicyChanged`/`PolicyCancelled`, compare via supersession, `ReverificationRequired` once per claim + cause (unique index), `snapshot_status`; `clm.Coverage.reverify` keep/adopt with reason; adoption without cover → `coverageInQuestion` and payment block on that exposure | REQ-CLM-002, -057, -058 (subset); REQ-POL-107 (subset) | C (supersession fields); M: TEMPORAL before its integration test runs against real POL | **deep (temporal)** | 6, 12, 13, 15, 22 | 3.5 h |
| **SL3-UI-POL-FILE** | `web/src/modules/policy/file/` (new), `PolicyViewPage.tsx` | Policy file: term list incl. renewal term, transaction history (kind, effective, recorded, premium change), charges per transaction, "as known at", action bar (Change / Cancel / Renew now, permission-aware), supersession badge; Greek-first, all states | PRD-05 SCR-POL-10/11/12 (subset); REQ-POL-128, -132 | C (MSW fakes from samples) | light + visual | 25–28 | 3 h |

### S2 — money out and the remaining UI (4 WPs; start as S1 slots free)

| WP | Module (owned area) | Scope | PRD / REQ | Depends on | Review | PITFALLS | Est. |
|---|---|---|---|---|---|---|---|
| **SL3-BIL-REFUND** | BIL `Commands/Refunds.cs`, refund rows + **BIL's S2 migration** | `bil.Refund.propose/get/list/decide/resubmit`; netting of the credit balance (same term first); minimum refund 5.00 (PRD default, illustrative); payee = payer, verified IBAN via `bil.PayeeAccount` (purpose REFUND); authority `BIL.Refund` on the **refund total** (one open refund per account), auto ≤ 500.00, else a PLT approval decided by another user (requester/editor ≠ approver, payee-account changer ≠ approver); screening + VoP stubs fail closed; disbursement source `BIL_REFUND` via the slice-2 service; `RefundApproved`, `RefundDisbursed` at ISSUED, `RefundRejected`; sealed LA-12/LA-13 entries | REQ-BIL-007, -181…-191, -354, -198…-206 (subset) | M: BIL-CREDIT; C: PLT | **deep (money + security)** | 1–5, 7–10, 18, 20 | 4.5 h |
| **SL3-UI-POL-JOBS** | `web/src/modules/policy/servicing/` (new) | Change workspace (start dialog with the permitted date range, vehicle edit/replace, before/after premium preview, diff, bind confirmation, out-of-sequence/preempted errors explained); cancellation (source, effective now, refund breakdown per charge type with "IPT not refunded (provisional)", confirm); renewal panel (Renew now, offer, referral state, accept) | SCR-POL-13, -14, -17 (subsets); REQ-POL-136, -192, -193, -199 | C; live walk after M: POL S1 | light + visual | 25–28 | 4 h |
| **SL3-UI-BIL** | `web/src/modules/billing/refunds/` (new), `InvoicePage.tsx` | Credit note on the invoice page (kind, original invoice link, fiscal stub notice); refunds queue and detail with breakdown (REQ-BIL-184); approve/reject for `billingmgr`; payee IBAN capture (masked, POST only) | SCR-BIL-02, -07 (subsets) | C; live walk after M: BIL-REFUND | light + visual | 18–20, 25–28 | 3 h |
| **SL3-UI-CLM** | `web/src/modules/claims/reverify/` (new), `ClaimViewPage.tsx` | Re-verification banner on the claim file; old vs new snapshot side by side (vehicle, coverages); keep/adopt with reason | REQ-CLM-058 (subset) | C | light + visual | 25–27 | 2 h |

### S3 — integration

| WP | Module (owned area) | Scope | Depends on | Review | PITFALLS | Est. |
|---|---|---|---|---|---|---|
| **SL3-E2E** | `tests/e2e/tests/e2e03*`, `e2e04*`, `change*`; `infra/local/seed-demo.py --servicing`; integration fixes in any module (small, listed) | API specs: E2E-03 (288.63 credit, IPT unchanged, credit note, fiscal CREDIT, refund Disbursed, every journal balanced, GL-2410 unchanged, refund clearing 0), E2E-04 (term 2 bound, invoiced, journals), change (+debit invoice; −credit refund; re-verification on an open claim). Playwright UI specs for the three journeys with `alive(page)`. `run-e2e03.sh` runs all; CI job `e2e03` turned on | M: all | light (integration) | 29–35 | 5 h |

### Critical path

**SL3-POL-TEMPORAL (S0) → SL3-POL-CANCEL / -CHANGE (S1) → SL3-E2E (S3).** The money path runs alongside it:
CONTRACTS → BIL-CREDIT → BIL-REFUND → E2E. POL is first and gets the deep review. BIL, FIN, CMP, CLM and UI start on
the typed contracts in parallel.

## 6. Interface-first S0: contracts typed by SL3-CONTRACTS

All changes are additive. Every new event field that a consumer depends on is **always set** (PITFALLS 12).

| Module | Operations (typed request/response/errors) | Events |
|---|---|---|
| POL | `pol.PolicyChange.create`; `pol.Cancellation.create` (source, reason code, effective, kind Standard/Flat); `pol.Renewal.create/offer/accept`; `pol.Job.quote/bind` responses gain `servicingPreview` (before/after annual, prorated per element × charge type, tax lines with treatment and `provisional`, total change, refund due); `pol.Term.timeline` typed; `pol.Snapshot.get` gains `supersession` and `effectiveKnownAt`; `pol.Policy.get` gains `effectiveKnownAt`; errors `POL-ERR-OUT-OF-SEQUENCE`, `-PREEMPTED`, `-REBASE-REQUIRED`, `-AFTER-CANCELLATION`, `-EFFDATE-LIMIT`, `-STALE` | `PolicyChanged` (effective, changed locators, vehicle facts), `PolicyCancelled` (source, refund method, effective), `RenewalCreated/Offered/Bound`, `ChargeDeltaEmitted` + `transactionKind`, `cancellationSource`, `treatmentRuleId/Version`, `legalStatus`, `provisional` |
| RAT | `rat.Proration.prorate`; `rat.Rate.rate` mode ENDORSEMENT/RENEWAL + `pinnedArtefactHash` | — |
| MKT | `ITaxCalculator.TreatmentAsync` request/result confirmed (transaction kinds, source, action, ruleId/ruleVersion/legalStatus); cancellation-source code list | — |
| BIL | `bil.Invoice.get/list` kind `CREDIT_NOTE` + `originalInvoiceId`; `bil.Refund.propose/get/list/decide/resubmit`; `bil.PayeeAccount.create` purpose `REFUND` | `BillingEntryPosted` entry types `CREDIT_WRITTEN`, `CREDIT_BILLED`, `REFUND_APPROVED`, `DISBURSEMENT_*` for source `BIL_REFUND`, line dims `transactionKind`, `cancellationSource`, `treatmentRuleId`; `RefundApproved`, `RefundDisbursed`, `RefundRejected` typed |
| CMP | `cmp.FiscalDocument.request` role `CREDIT` + `correlatedDocumentId` | — |
| CLM | `clm.Coverage.reverify` (keep/adopt, reason code); claim view `snapshotStatus` | `ReverificationRequired` typed (old ref, new ref, cause event id) |
| PLT | `/dev/clock/advance` (Development only, outside the public contract set, like `/dev/sign-in`) | — |

## 7. Parallelism and conflict map

Rule: no two concurrent WPs touch the same DbContext, migration folder or generated contract folder. Where a module has
two or three builders at once (POL in S0 and S1), they own **disjoint folders**, and only the WP marked *migration
owner* may add an EF migration in that batch.

| WP | Owns (files/folders) | Shared files touched | Rule | Merge risk |
|---|---|---|---|---|
| SL3-CONTRACTS | `contracts/**`, `src/*.Contracts/Generated/**`, `contracts/openapi/INDEX.md` | count tests (self-maintaining after D-PRG-21) | sole contract owner in S0; later contract changes only by the module's own WP, additive, re-run ContractGen | low |
| SL3-POL-TEMPORAL | `Modules.Policy/Persistence/**` (**POL migration owner, S0**), `Queries/**`, `Commands/JobSupport.cs`, `Domain/Codes.cs`, `PolicyOptions.cs`; `tests/…IntegrationTests/Policy/Temporal/` | `docs/module-pattern.md` (append a "Record time" section) | sole owner of POL schema for the whole slice | med |
| SL3-POL-ENGINE | `Modules.Policy/Domain/Servicing/**`; `tests/…IntegrationTests/Policy/Servicing/` | none | no DB, no DI changes (registered by S1 WPs) | low |
| SL3-MKT-TREATMENT | `Modules.Market/Services/Tax*`, `Domain/TaxTreatment*`; `CountryPacks.GR/Configuration/**`, `CountryPacks.CY/**` (treatment rows); `tests/…/Market/Treatment/` | MKT permission file (append) | — | low |
| SL3-PFC-MOTOR11 | `Modules.Product/Seed/motor-gr-1.1.product.json`, artefact validation | none | 1.0 seed is never edited | low |
| SL3-RAT-PRORATE | `Modules.Rating/Services/Proration*`, `Services/Modes*`, `Api/Proration*` | RAT permission file (append) | — | low |
| SL3-PLT-SUPPORT | `Platform/Time/**`, PLT `dev_clock` migration (**PLT migration owner**), dev-users file, PLT authority registration, `Host/Program.cs` (clock wiring only) | `Program.cs`, dev-users file | sole owner of `Program.cs` and dev users in slice 3 | med |
| SL3-E2E-HARNESS | `tests/e2e/support/**`, `tests/e2e/stack/**`, `tests/e2e/run-e2e03.sh`, `.github/workflows/ci.yml` (job `e2e03` only) | `ci.yml` | sole owner of `ci.yml` in slice 3; SL3-E2E edits only the `e2e03` block | low |
| SL3-POL-CHANGE | `Commands/Change/**`, `Api/ChangeController.cs`, `tests/…/Policy/Change/` | `PolicyModule.cs` (append one DI block), POL permission file (append) | **no migration**; schema requests go to the orchestrator → TEMPORAL follow-up | med |
| SL3-POL-CANCEL | `Commands/Cancellation/**`, `Api/CancellationController.cs`, `tests/…/Policy/Cancellation/` | same as CHANGE | same | med |
| SL3-POL-RENEW | `Commands/Renewal/**`, `Api/RenewalController.cs`, `tests/…/Policy/Renewal/` | same as CHANGE | same | med |
| SL3-BIL-CREDIT | `Modules.Billing/Commands/Credits*`, `Services/TermBilling.cs`, `Events/IntakeHandlers.cs`, `Persistence/**` (**BIL migration owner, S1**) | BIL permission file (append) | — | med |
| SL3-CMP-CREDIT | `Modules.Compliance/**`, `CountryPacks.GR/Fiscal/MyDataStubFiscalChannel.cs` | CMP permission file | `CountryPacks.GR/Fiscal` is not touched by MKT-TREATMENT (Configuration only) | low |
| SL3-FIN-RULES | `Modules.Finance/Seed/gr-test.finance.v3.json`, `Posting/**`, `Domain/PostingRules.cs` | none | — | low |
| SL3-CLM-REVERIFY | `Modules.Claims/Events/Policy*`, `Commands/Reverify*`, `Persistence/**` (**CLM migration owner**) | CLM permission file | — | low |
| SL3-UI-POL-FILE | `web/src/modules/policy/file/**`, `PolicyViewPage.tsx`, `policy/i18n/file.*` | `web/src/routes.tsx` (append) | own i18n namespace file; own API file `file/api.ts` | low |
| SL3-BIL-REFUND (S2) | `Modules.Billing/Commands/Refunds*`, `Persistence/**` (**BIL migration owner, S2**) | BIL permission file | starts only after BIL-CREDIT merged | med |
| SL3-UI-POL-JOBS (S2) | `web/src/modules/policy/servicing/**`, `policy/i18n/servicing.*` | `routes.tsx` (append) | never edits `file/**` or `quote/**` | low |
| SL3-UI-BIL (S2) | `web/src/modules/billing/refunds/**`, `InvoicePage.tsx` | `routes.tsx` (append) | — | low |
| SL3-UI-CLM (S2) | `web/src/modules/claims/reverify/**`, `ClaimViewPage.tsx` (banner slot only) | — | — | low |
| SL3-E2E (S3) | `tests/e2e/tests/**` (new specs), `infra/local/seed-demo.py` | `ci.yml` (`e2e03` block) | — | low |

**Shared files left and their single owner:**

| File | Rule |
|---|---|
| `appsettings*.json` | Untouched after D-PRG-21; module settings go in module files |
| `Program.cs` | Owned by SL3-PLT-SUPPORT |
| `ci.yml` | Owned by SL3-E2E-HARNESS, then SL3-E2E (`e2e03` block only) |
| `routes.tsx`, `PolicyModule.cs`, per-module permission files | Append-only; on conflict keep both sides |
| `DECISIONS.md`, `STATUS.md`, `HANDOVER.md`, `PITFALLS.md` | Orchestrator only; builders put decisions and open questions in their PR body |
| `seed-demo.py` | SL3-E2E only |

## 8. Rolling schedule (6–8 builders, pipelined)

The hours below are wall-clock from the S0 start. Each line says what is in CI/review and what starts. Reviews run in
parallel with the next builders.

| t (h) | Merging / in review | Starts building (slots in use) |
|---|---|---|
| 0 | — | S0: CONTRACTS, POL-TEMPORAL, POL-ENGINE, MKT-TREATMENT, PFC-MOTOR11, RAT-PRORATE, PLT-SUPPORT, E2E-HARNESS (8) |
| 1.5–2.5 | PFC-MOTOR11 PR (light), E2E-HARNESS PR (light), CONTRACTS PR (light) → merge (one D-PRG-22 batch: PFC + e2e harness + contracts) | CMP-CREDIT, FIN-RULES, UI-POL-FILE (on contracts) |
| 2.5–3 | MKT-TREATMENT, PLT-SUPPORT PRs → deep reviews | BIL-CREDIT, CLM-REVERIFY (contracts) |
| 3–4 | RAT-PRORATE, POL-ENGINE PRs → deep reviews | UI-CLM (contracts) as a slot frees |
| 4–4.5 | POL-TEMPORAL PR → deep review (critical) | POL-CHANGE, POL-CANCEL, POL-RENEW may start **on the TEMPORAL + ENGINE branches** (handover practice: start on an unreviewed reference branch), merging main when they land |
| 5–6 | S0 deep fixes re-checked light → merged | — |
| 5–7 | CMP-CREDIT, UI-POL-FILE, UI-CLM PRs (light) → merged | UI-POL-JOBS, UI-BIL (contracts; live walks later) |
| 7–8 | BIL-CREDIT, FIN-RULES, CLM-REVERIFY PRs → deep reviews | BIL-REFUND (needs BIL-CREDIT merged ≈ t 9) |
| 8–10 | POL-CHANGE, POL-CANCEL, POL-RENEW PRs → three deep reviews in parallel | SL3-E2E phase A (API specs against the merged parts) |
| 10–12 | POL S1 fixes → merged; BIL-CREDIT merged | UI live walks against real POL/BIL (UI-POL-JOBS, UI-BIL) |
| 13–14 | BIL-REFUND PR → deep review → fix → merged | SL3-E2E phase B (UI specs) |
| 15–18 | UI-POL-JOBS, UI-BIL merged; SL3-E2E integration fixes; `e2e03` green on CI | Slice acceptance walk in real Chrome (FYI screenshots to the user) |

**Estimate:** about 67 builder hours, about 13 review hours (14 deep reviews at ≈ 45 min, 7 light at ≈ 25 min) and
about 10 hours of fix rounds, so roughly 90 agent-hours in all. That gives **about 16–19 wall-clock hours at 6–8
concurrent builders**. The critical path is TEMPORAL → POL S1 → BIL-REFUND → E2E. The handover's 12–16 h was for the
narrower 6-agent cut; this plan has 21 WPs against 8 in slice 2, and keeps every deep review.

## 9. Rules for every slice-3 builder

- Everything in HANDOVER §4 applies, including the module pattern, personal data, money, time and concurrency rules.
- Briefs are filled from `briefs/BUILDER-TEMPLATE.md` and say "Runs on Sonnet 5.5 (model: sonnet), D-USR-16". The
  ready briefs are in `orchestration/briefs/sl3/`.
- Regulatory values only from the PRDs, with their `legalStatus`. Anything not Settled is flagged `provisional`
  outside Production and refused in Production (D-REG-01..07, D-SLC-09). Open values stay absent and fail closed
  (D-SL3-05..08).
- Commercial values are marked **illustrative**: TERM_RATIO, the ProRata refund method, refund limits 500.00/5,000.00,
  the 5.00 minimum refund and the 45-day renewal lead.
- Policy servicing UI: the Aegean mockup v3 has only the home, «Ανάληψη κινδύνου» and «Φάκελος ζημίας» screens. Policy
  file and servicing screens follow the **«Φάκελος ζημίας» pattern**: record PageHeader, stage strip → term timeline,
  money card → term premium card, history list. The change workspace follows the **quote wizard step pattern**.
  Refund approvals follow the **claims approvals inbox**. The re-verification banner uses the claim file's alert slot.
  Side-by-side screenshots go in `orchestration/ux/<wp>/` (D-USR-11).
- Commit early (PITFALLS 34). Push your branch and open a PR. The orchestrator merges.
