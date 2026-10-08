# SLICE PLAN 4 — finish claims (W7): full E2E-02 with recoveries and RI recovery, E2E-06 Friendly Settlement

**Status:** planned 2026-10-08 (planner, from `SLICE-3-PLANNING-HANDOVER.md` §4–§6 and the W7 backlog).
**Decisions:** D-SL4-01…D-SL4-20 (proposed in §11; the orchestrator appends them to `DECISIONS.md`).
**Process:** as slice 3:
- **every builder and every reviewer runs on Sonnet 5.5 (`model: "sonnet"`, D-USR-16);**
- PR workflow with a quick local gate (D-PRG-19);
- small WPs ride on batched PRs and get no CI run of their own (D-USR-18, D-PRG-22);
- docs-only changes go straight to main (D-USR-17);
- PITFALLS self-check (D-PRG-20);
- 6–8 concurrent builders across the machine, with reviewers in parallel;
- a deep, adversarial first-round review for every money/ledger, security and temporal WP, each cut so that a review
  takes about 45 minutes or less.

**Runs beside slice 3 (binding timing rule).** Slice 3 is still building, and its CLM, BIL, FIN, CMP, MKT and PLT WPs
own files this slice needs. A slice-4 WP that touches one of those modules starts only after the slice-3 WP that owns
the area has **merged**. These are the gates **G-…** in §5 and §8. Wave 1 (RI, contracts, E2E harness, RI UI) touches
no slice-3 file and starts at once. A separate planner is planning slice 5 (UW workbench and E2E-12 pack rollback) at
the same time; the cross-slice shared files are listed in §7.3.

## 1. Goal

A claims team handles a motor accident through to the money that comes back:

1. **E2E-02 (full, as cut).** A staff handler registers a claim with an own-damage exposure and a third-party MTPL
   bodily-injury exposure. Large reserves and a payment go through authority and maker ≠ checker approvals. The
   payment is disbursed through BIL, and a stub fiscal settlement receipt is stored on it. The handler then:
   - opens a **subrogation** recovery against the at-fault insurer, outside Friendly Settlement;
   - opens a **salvage** recovery on the own-damage vehicle;
   - records the expected amounts as recovery reserves;
   - demands the money. BIL holds a receivable and records the cash, and CLM records the recovery.

   **RI** holds an illustrative motor per-risk XoL treaty. It recomputes the recoverable on every claim movement and
   publishes `RecoveryCalculated`. FIN posts the reserves, payments, recoveries by type and RI recoverables, and every
   journal balances.
2. **E2E-06 Friendly Settlement (Φιλικός Διακανονισμός)**, written against the D1 `FS_CLEARING` design:
   - **Not-at-fault leg:** the customer's FS eligibility is evaluated through the pack SPI (stub). CLM pays the
     customer and books an FS receivable at the clearing value, then submits the case.
   - **At-fault leg:** a clearing notification creates a claim with an FS payable (method `CLEARING`, no disbursement).
   - **Monthly statement:** the clearing statement is imported and matched, the net per counterparty is approved, and
     the net is handed to BIL as one `FS_CLEARING` payable or receivable.
   - **FIN:** the FS clearing account nets to zero per counterparty and statement.
3. **Claims follow-ups from slice 2** that touch the same code:
   - payment void, stop, return and reissue;
   - in-process withdrawal of leftover approvals;
   - a dry-run authority preview;
   - the list of transaction sets of a claim;
   - a typed approval diff;
   - close-guard detail in `errors[]`.

All of it runs on `docker compose` and is proven by automated API and Playwright tests in a new CI job `e2e06`.

## 2. Scope and OUT list

**IN** (D-SL4-01):
- **CLM recoveries:**
  - subrogation, salvage and Friendly Settlement recovery cases;
  - recovery reserves and recovery transactions in transaction sets, with derived net incurred;
  - write-off under authority;
  - liability facts on the claim (fault %, counterparty insurer, joint accident report);
  - MTPL third-party exposures without statutory clocks (D-SL4-09).
- **CLM Friendly Settlement (Greece pack):**
  - eligibility through the SPI;
  - the own-settlement exposure and FS receivable;
  - submission;
  - the at-fault leg from a clearing notification, with a `CLEARING` payable;
  - statement import, matching and exceptions;
  - approval of the net per counterparty and its hand-over to BIL `FS_CLEARING`;
  - tracking of BIL's outcome.
- **CLM payment corrections:**
  - void, stop and reissue;
  - `PaymentVoided` with a reversal transaction;
  - the stub fiscal settlement receipt and its MARK.
- **CLM follow-ups:**
  - `clm.TransactionSet.list` by claim;
  - the authority preview in the dry-run;
  - typed approval `diff`;
  - close-guard detail in `errors[]`;
  - PLT approval withdrawal.
- **RI (new module, minimal):**
  - per-risk XoL treaty registry with participations, maker ≠ checker approval and activation;
  - a ClaimView fed by CLM events;
  - occurrence = claim;
  - UNL per clause, layer maths, annual aggregate tracker, paid/outstanding split;
  - target-minus-booked recovery deltas per occurrence × layer × participant;
  - `RecoveryCalculated`, trace and `ri.Recovery.listByClaim`.
- **BIL:**
  - CLM receivables (salvage, subrogation; REQ-BIL-346) on a counterparty account, with cash matching and
    `CashAllocated`;
  - `FS_CLEARING` net payable (method `CLEARING`, release approval always required) and net receivable;
  - disbursement stop, void, return and reject events;
  - a Development-only manual stub bank;
  - four-eyes release for a reissue to a changed account.
- **FIN** rule set v4:
  - recoveries by type with counterparty;
  - recovery reserves;
  - recovery clearing per recovery;
  - FS clearing per counterparty and statement;
  - RI recoverable on paid and outstanding separately;
  - payment-void reversal.
- **MKT / GR pack:** the `FriendlySettlementClearing` **stub** adapter (never bound in Production) and the capability
  `cap.clm.friendly_settlement` (GR on, CY off).
- **CMP:** stub fiscal settlement receipt for claim payments.
- **PLT:**
  - roles and dev users for recovery and reinsurance;
  - in-process approval withdrawal.
- **UI:**
  - the claim file's recoveries tab, money-card lines, FS panel and RI recoverable card;
  - the FS statements screen;
  - payment corrections;
  - the RI treaty registry;
  - BIL receivables and the FS release approval.
- **E2E:** E2E-02 (cut as §3.1) and E2E-06 (§3.2), API and UI, with CI job `e2e06`.

**OUT** (each with the reason, D-SL4-01):

| Out | Reason |
|---|---|
| Statutory clocks: `CLM_MTPL_OFFER`, `CLM_MTPL_PAYMENT_DUE`, `CLM_MTPL_ASSESSMENT`, `CLM_FS_COUNTERPARTY_REPLY`, repair in kind; reasoned offer/reply documents and acceptance (E2E-02 steps 3, 6, 7) | No CMP clock engine and no DOC delivery proof exist. GR clock values are UNVERIFIED (PRD-17 GR-14, OI-CLM-02/06/13). MTPL exposures are flagged `statutoryClocks = NOT_TRACKED` and refused in Production (D-SL4-09) |
| FS disputes and counterparty replies (REQ-CLM-159) | Needs the WAITING_PERIOD clock and an unverified reply window (OI-CLM-02) |
| Portal FNOL, WRK photo intake and joint-accident-report classification (E2E-02 step 1, E2E-06 step 1) | CHN and WRK are not built. Staff FNOL records the joint report as a flag |
| Deductible recovery (REQ-CLM-146), contribution, guarantee-fund and Green Card recoveries, the Auxiliary Fund adapter | No deductible is applied yet. `MotorCompensationBodyAdapter` formats are open (OI-CLM-05) |
| Recovery milestone reminders, arbitration/award, the recovery dashboard (REQ-CLM-148 reminders, -149, -152) | Reminders need WRK; the others are Should |
| Insured's share of a recovery (REQ-CLM-153) | Should; no deductible applied |
| Payment holds (sanctions, SIU, VoP) and hold release; duplicate hold-with-override (D-SL2-10a) | No hold source exists: the screening stub returns Clear and SIU is not built |
| RI proportional cession, `ChargeDeltaEmitted` intake (REQ-RI-075), per-event and cat XoL with occurrence linking (REQ-RI-115 linking, -124, GT-12), reinstatement premiums, deposit/adjustment premiums, commissions | Motor P1 is XoL only (A-01). The E2E-02 setup is per-risk (GT-05). Per-event linking needs its own UI and review |
| RI contract versioning and endorsement, renewal, security list and ratings, collateral, statements, bordereaux, settlements (`RI_SETTLEMENT`), cash calls, large-loss notices (DOC), FX/ROE clauses | Not needed by E2E-02. Contracts are EUR (D-SL2-06 pattern). The data model keeps a version row so that versioning lands without re-deciding (D-SL4-04) |
| RI batching window (REQ-RI-130, 30 s / 500) and surge mode | One recalculation per contract year at a time under a lock, run immediately (D-SL4-19). The window is a performance feature |
| IFRS 17 RI-held groups, the loss-recovery component, daily RI/FIN reconciliation (REQ-FIN-139/142/167, E2E-02 step 15), the GL-2510 break check (REQ-FIN-249) | FIN IFRS 17 is not built; the dimension is `UNASSIGNED` |
| Claim journals by policy number, rule-set history API (D-SL2-12c) | Read conveniences; not on the journeys |
| Bank files, camt/pain, batches | Stub bank only; the manual mode stands in for returns in Development |
| E2E-07 (non-payment), E2E-08 (withdrawal) | Moved out of slice 3 into "slice 4" before slice 4 became the claims slice. They need the CMP clock engine. **Not planned here**: the orchestrator must place them in a later slice |
| UW workbench, E2E-12 pack rollback | Slice 5 (other planner) |

## 3. Journeys and REQ mapping (PRD-18 §17.3 step tables, cut)

### 3.1 E2E-02 FNOL → reserve → payment → recovery → RI recovery

Setup:
- the E2E-01 policy (MOTOR-GR), in force;
- an **illustrative per-risk XoL treaty "EUR 500k xs 250k"** (RI GT-05; A-01/OI-RI-05 open), scope MTPL coverages,
  100% placed with two reinsurer organisation parties (60/40);
- the at-fault third party is insured by insurer X (an organisation party), outside FS (the amount is above any FS
  limit);
- the claim has an own-damage exposure (insured) and an **MTPL_BI exposure** for an injured third party (a P2 person, no
  injury data captured).

Amounts come from PRD examples (GT-05, REQ-RI-119, REQ-CLM-145) and are illustrative. RI recoverables are asserted per
D-SL4-05:

| Point | BI reserve / paid / realised recoveries | UNL incurred / paid | RI incurred / paid / outstanding recoverable | RI delta (incurred) |
|---|---|---|---|---|
| a | reserve 300,000 | 300,000 / 0 | 50,000 / 0 / 50,000 | **+50,000** |
| b | reserve +600,000 (900,000) | 900,000 / 0 | 500,000 / 0 / 500,000 | **+450,000** |
| c | payment 400,000 (eroding) | 900,000 / 400,000 | 500,000 / 150,000 / 350,000 (REQ-RI-119) | 0 |
| d | subrogation 200,000 received | 700,000 / 200,000 | 450,000 / 0 / 450,000 | **−50,000** |

Each delta is split 60/40 by participant. The residual cent goes to the lead participant (D-SL4-05).

| # | PRD-18 step | In slice? | Owner (WP) | Requirement ids | Assertion in the slice |
|---|---|---|---|---|---|
| 1 | FNOL (portal), photos via WRK | **Partly**: staff FNOL; liability facts | CLM (RECOVERY-OPS, existing FNOL) | REQ-CLM-001, -076 (fault, subset) | Claim number; fault 0% insured; counterparty insurer X recorded |
| 2 | Coverage on the loss-date snapshot | **Yes** (slice 2) | CLM | REQ-CLM-002, REQ-POL-007 | Snapshot ref stored |
| 3 | `CLM_MTPL_OFFER` clock | **No** (D-SL4-09) | — | REQ-CLM-007, -165 | The MTPL_BI exposure shows `statutoryClocks = NOT_TRACKED` |
| 4 | Initial reserves within authority | **Yes**, with large-loss authority | CLM (MONEY2), PLT | REQ-CLM-003, -108, BR-CLM-010 | 300,000 refers to `claimsmgr` (illustrative ≤ 1,000,000; D-SL4-08) |
| 5 | `ReserveChanged` → FIN and RI | **Yes** | FIN (V4), RI (RECOVERY) | REQ-CLM-005, REQ-RI-113, -116, -123, -127 | FIN posts; RI deltas +50,000 then +450,000 |
| 6–7 | Reasoned offer, acceptance, payment-due clock | **No** | — | REQ-CLM-166, -167 | — |
| 8 | Payment through BIL with sanctions, VoP and disbursement | **Yes** (slice 2 path) | CLM, BIL | REQ-CLM-004, REQ-BIL-009 | 400,000 to the injured party's verified IBAN |
| 9 | `PaymentIssued`; claim clearing nets to zero | **Yes** | FIN | REQ-FIN-037 | GL-2510 = 0 per payment |
| 10 | Settlement receipt as a fiscal document | **Yes (stub)** | CMP (RECEIPT), CLM (PAYOPS) | REQ-CMP-001, -030, REQ-CLM-138, R-43 | One document per payment; MARK stored; codes `UNMAPPED-OQ-012` (D-SL4-11) |
| 11 | Subrogation vs the at-fault insurer; recovery reserve; recovery received through a BIL receivable; salvage sold | **Yes** | CLM (MONEY2, RECOVERY-OPS), BIL (RECV) | REQ-CLM-096, -102, -143, -144, -145, -147, -151, REQ-BIL-346 | Net incurred = incurred − recoveries − open recovery reserve; salvage reserve 1,500, proceeds 1,700 → recovery 1,700, open recovery reserve 0 |
| 12 | FIN recoveries by type | **Yes** | FIN (V4) | REQ-FIN-160 | Recovery journals per type with the counterparty dimension; recovery clearing nets to zero per recovery |
| 13 | RI deltas +50k, +450k, −50k | **Yes** | RI (ENGINE, RECOVERY) | REQ-RI-003, -119, -125, -126, -127, -129 | As the table above; idempotent on replay |
| 14 | `RecoveryCalculated` → FIN; CLM view via `ri.Recovery.listByClaim` | **Yes** | RI, FIN, UI (CLM-REC) | REQ-RI-004, -136, REQ-FIN-164, F-407 | FIN posts paid and outstanding recoverables separately; the claim file shows "Αντασφαλιστική ανάκτηση" |
| 15 | Daily cession/recovery reconciliation | **No** | — | REQ-FIN-167 | — |

### 3.2 E2E-06 Friendly Settlement (against D1 `FS_CLEARING`, see §4)

Setup:
- Greece pack, `cap.clm.friendly_settlement` on (non-Production), FS stub adapter bound;
- counterparty insurer Y is on the stub's member list;
- the clearing office is an organisation party with a verified payee account (VoP stub).

Amounts follow REQ-FIN-299's example: own leg 1,200, at-fault leg 3,000, net payable 1,800.

| # | PRD-18 step | In slice? | Owner (WP) | Requirement ids | Assertion in the slice |
|---|---|---|---|---|---|
| 1 | FNOL with joint accident report | **Partly**: staff FNOL with the flag; no WRK classification | CLM (RECOVERY-OPS) | REQ-CLM-001 | Liability facts: 2 vehicles, accident in Greece, 0% insured fault, insurer Y |
| 2 | `evaluateEligibility` | **Yes (stub)** | MKT (FS), CLM (FS-CASE) | REQ-CLM-155, -156, REQ-MKT-313 | Eligible with reasons, `ruleId`, `legalStatus` UNVERIFIED, `provisional: true`; CY: switch off, SPI not called |
| 3 | Own-settlement exposure; pay the customer; FS receivable at the clearing value | **Yes** | CLM (FS-CASE, MONEY2) | REQ-CLM-157, -102 | `FS_OWN_SETTLEMENT` exposure; payment 1,200; recovery reserve type FS 1,200; expected gain/loss 0.00 (basis ACTUAL) |
| 4 | `FriendlySettlementSubmitted`; dispute clock | **Partly**: submit only | CLM (FS-CASE) | REQ-CLM-159 (submit part) | Event published once; clearing reference stored |
| 5 | At-fault leg from the clearing notification; payable via the statement (method `CLEARING`) | **Yes** | CLM (FS-CASE), MKT (stub), dev inbound endpoint (D-SL4-18) | REQ-CLM-158 | Claim on our policy with an MTPL_PD exposure; payment 3,000 method `CLEARING`; **no** BIL disbursement |
| 6 | Statement imported, matched; net per counterparty | **Yes** | CLM (FS-STATEMENT) | REQ-CLM-160 | Two lines matched; recovery 1,200 (type FS) recorded; payable 3,000 settled; an unmatched line → exception list; net payable 1,800 |
| 7 | **BIL net cash with the clearing office** (fails as written; fixed by D1) | **Yes** | CLM (FS-STATEMENT), BIL (FSOUT) | REQ-CLM-264, REQ-BIL-354, -355, -357 | Net approved (`CLM.FS_NET_SETTLEMENT`) → one `FS_CLEARING` disbursement 1,800, method `CLEARING`, lines sum to the net, release approved by `billingmgr` ≠ requester → Issued |
| 8 | **FIN: cash only from BIL; FS clearing nets per statement** (fails as written; fixed by D1) | **Yes** | FIN (V4) | REQ-FIN-037, -160, -299, BR-FIN-097 | GL-2515 nets to zero for (insurer Y, statement S); no per-payment GL-2510 break for method `CLEARING` |
| 7′ | Variant: net receivable | **Yes** (API test) | BIL (RECV), CLM | REQ-BIL-356 | `bil.Receivable.register` source `FS_CLEARING`; cash matched by the statement reference; `CashAllocated` → statement Settled |

### 3.3 Payment corrections (PRD-07 J-04 subset, no E2E id)

| # | Step | Owner (WP) | Requirement ids | Assertion |
|---|---|---|---|---|
| 1 | Void an Issued, not Cleared payment (manual stub bank, Development) | CLM (PAYOPS), BIL (DISBOPS) | REQ-CLM-125, REQ-BIL-208 | `DisbursementVoided` → reversal transaction −amount linked to the original; open reserve restored (eroding); `PaymentVoided` once |
| 2 | Stop a Released, not Issued payment | CLM, BIL | REQ-CLM-126, -136, REQ-BIL-208, BIL.DisbursementStop | `BIL-ERR-NOT-STOPPABLE` once issued; otherwise Stopped → `PaymentVoided` reason STOPPED |
| 3 | Bank return / reject | BIL, CLM | REQ-BIL-210, -361 | `DisbursementReturned`/`Rejected` → `PaymentVoided` |
| 4 | Reissue to a new IBAN | CLM, BIL | REQ-CLM-127, REQ-BIL-199 | New payment linked to the original, every check re-run; a changed account waits for a BIL release approval by a user who did not change it |
| 5 | FIN reverses; RI recomputes | FIN (V4), RI | REQ-FIN-159, REQ-RI-128 | Payment journal reversed; RI negative paid delta |

## 4. E2E-06 design resolution — Friendly Settlement against D1 `FS_CLEARING` (D-SL4-02, -03, -12, -18)

PRD-18 records that E2E-06 "cannot pass as written" (F-400/XMR-F-214). As written, FS payables are "settled through the
clearing statement", CLM "posts" the monthly net, and nobody moves cash. BIL has no FS source, and FIN's GL-2510 breaks
after two days. D1 fixes it. The slice builds exactly D1:

1. **CLM never moves or posts cash.**
   - The own leg is an ordinary claim payment (BIL disbursement) plus an FS recovery reserve at the clearing value.
   - The at-fault leg is a payment transaction with method `CLEARING`. It has **no** disbursement and stays Approved
     until its statement is matched.
2. **Per statement and counterparty, CLM computes one net.**
   - The net is approved under `CLM.FS_NET_SETTLEMENT` (four-eyes above an illustrative threshold).
   - CLM hands it to BIL as `bil.Disbursement.request` (source `FS_CLEARING`, method `CLEARING`, payee the clearing
     office, statement id, claim-level lines that must sum to the net, approval evidence), or as
     `bil.Receivable.register` (source `FS_CLEARING`).
   - A rejected or stopped net returns the statement to Pending (REQ-CLM-264).
3. **BIL is the only cash executor** (REQ-BIL-354…357).
   - The source register gains the `FS_CLEARING` row with method `CLEARING`.
   - The counterparty is screened and VoP-checked once, with no per-claimant VoP.
   - Every `FS_CLEARING` disbursement needs a release approval by a second user, whatever the amount. With no batches
     in the slice, this stands in for the batch four-eyes rule (D-SL4-12).
   - Entries go through LA-24.
4. **FIN posts claim facts from CLM against GL-2515** (Dr incurred / Cr GL-2515 for `CLEARING` payables; Dr GL-2515 /
   Cr recoveries for FS recoveries).
   - It posts the cash only from BIL `FS_CLEARING` entries.
   - GL-2515 must net to zero per counterparty and statement within the statement window (BR-FIN-097).
   - Method `CLEARING` is excluded from the per-payment GL-2510 rule. This replaces the slice-2 fail-closed suspense of
     D-SL2-12a.
5. **The market agreement is unknown** (OI-CLM-01/02: limits, averages, eligibility, file format, reply window). The
   Greece pack therefore binds a **stub** `FriendlySettlementClearing` that is never bound in Production. In Production
   the switch resolves but the SPI is unbound, so FS fails closed with `CLM-ERR-FS-DISABLED`.
   - The stub's eligibility limit is the **lower** of the two conflicting candidates in the PRDs (EUR 5,000 material
     damage, C-01), labelled `legalStatus UNVERIFIED`, `provisional: true`.
   - The clearing value basis is **ACTUAL** (the averages are unknown).
   - Statements are built by the stub from the cases submitted to it and the notifications it sent, in a documented
     JSON test format, not the EAEE format.
   - The at-fault notification enters through a Development-only endpoint, as `/dev/clock` does.

Assertions are **structural plus the illustrative amounts above**. No FS value is presented as the market's.

## 5. Waves, WPs and the critical path

Each WP lists its **real dependency**:
- **C** = typed contracts only (codes against generated fakes);
- **M:<WP>** = that WP's implementation must be merged;
- **G:<slice-3 WP>** = a slice-3 merge gate.

Reviews:
- **deep** = adversarial, with probe tests (money/ledger, security, temporal);
- **light** = checklist, plus a visual review for UI.

Estimates are builder wall-clock hours on this laptop. **All agents run on Sonnet 5.5.**

### Wave 1 — starts now, touches no slice-3 file (5 WPs)

| WP | Module (owned area) | Scope | PRD / REQ | Depends on | Review | PITFALLS | Est. |
|---|---|---|---|---|---|---|---|
| **SL4-CONTRACTS** | contracts + generated `*.Contracts`; `Market.Contracts/Spi/IFriendlySettlementClearing.cs` | Type every op, event and SPI of §6 and regenerate; samples valid; new error codes | D-API-06/06a; §6 | none | light (completeness, additivity) | 12, 18, 21, 22, 23 | 3 h |
| **SL4-RI-REGISTRY** | RI `Persistence/**` (**RI migration owner, wave 1**), `Registry/**`, `Api/Contracts*`, `ReinsuranceModule.cs`; permission file `ri.json` (new) | RI schema; per-risk XoL contract (scope, clause, layers, participations, Σ signed lines = placed %); one version row (valid + record period); Draft → Submitted → Approved → Active → Expired; maker ≠ checker via PLT approval (`RI.CONTRACT_APPROVE`, enterer ≠ approver); activation at period start (Athens, `IClock`); `RIContractActivated`; create/update/submit/approve/get/list/applicable | REQ-RI-001, -030, -031, -032 (XoL), -037, -038, -046, -047, -056, -057, -058, -065, -231 | C (can start on storage before C merges) | **deep (security + temporal)** | 3, 4, 5, 8, 13, 14, 15, 17 | 4 h |
| **SL4-RI-ENGINE** | RI `Domain/Recovery/**` (**no DB, no DI**) + its tests | Pure engine: UNL per clause; per-occurrence layer loss; annual aggregate R(Cₖ) − R(Cₖ₋₁) with full-year restatement; paid/outstanding split; target − booked deltas per occurrence × layer × participant; rounding residual to the lead; trace object; property tests (idempotence, order independence, Σ participants = layer × placed %); goldens GT-05, REQ-RI-116/119/123/125/126 examples | REQ-RI-116, -117 (realised only), -119, -122, -123, -125, -126, -127, -129, -132 (trace data) | C | **deep (money)** | 10, 11 | 3.5 h |
| **SL4-E2E-HARNESS** | `tests/e2e/support/claims4/**` (new), `tests/e2e/run-e2e06.sh`, `.github/workflows/ci.yml` (new `e2e06` job block only) | Own stack (project `coreins-e2e06`, ports 28000+, image tag `coreins-host:e2e06`, executable bit); helpers: dev sign-in for the new users, create/approve RI treaty, wait for complete journal sets (incl. RI and FS), FS dev notification, manual stub-bank actions; placeholder health spec; make sure the `e2e02` job's spec filter excludes `e2e02x*` | PRD-18 §17.1; XMR-FR-250 | C | light (batched, no own PR, D-USR-18) | 29, 30, 32, 33, 35 | 1.5 h |
| **SL4-UI-RI** | `web/src/modules/reinsurance/**` (new) | Treaty registry (list), treaty editor (layer diagram, participations, clause), submit; approve/return for `rimgr`; treaty detail; MSW fakes from samples; Greek-first | SCR-RI-02, -03, -04 (subsets) | M: CONTRACTS | light + visual | 25–28 | 3.5 h |

### Wave 2 — each WP starts at its gate (9 WPs)

| WP | Module (owned area) | Scope | PRD / REQ | Depends on | Review | PITFALLS | Est. |
|---|---|---|---|---|---|---|---|
| **SL4-PLT** | PLT `Approvals/**` (withdraw), dev-users file, role catalogue | Roles Staff.RecoverySpecialist (ROLE-20), Staff.ReinsuranceManager (ROLE-27), Staff.ReinsuranceAccountant (ROLE-28); dev users `recovery`, `rimgr`, `riacct`; `superuser` gains them (SoD unchanged); in-process `WithdrawAsync(approvalId, reason)` callable only by the module that created the request, audited, no REST | REQ-PLT-004 (subset); PRD-07 §12; PRD-08 §12 | **G: SL3-PLT-SUPPORT**; C | **deep (security)** | 3, 4, 5, 6 | 2.5 h |
| **SL4-MKT-FS** | `CountryPacks.GR/Claims/**` (new), `CountryPacks.CY/Claims/**` (new), MKT capability resolution for `cap.clm.friendly_settlement`, `Host/Hosting/CountryPackBinding.cs` (append one binding) | GR stub `IFriendlySettlementClearing` (never in Production): eligibility rules with provisional limit (D-SL4-03), member list, `submit`, `receiveNotification`, `settlementStatement` built from its own store; CY `NotApplicable`; capability switch GR on (non-Production) / CY off | REQ-MKT-004, -109, -313, REQ-CLM-155, -156; PRD-17 GR-06 | **G: SL3-MKT-TREATMENT**; C | light + regulatory checklist | 10, 24, 36, 37 | 2.5 h |
| **SL4-CMP-RECEIPT** | CMP fiscal source mapping, GR stub channel mapping | Fiscal request from source `CLM_CLAIM_PAYMENT`, role ISSUE, category `CLAIM_SETTLEMENT_RECEIPT`; idempotency source + id + role + revision; stub codes `UNMAPPED-OQ-012` (OI-CLM-04); CY `NotRequired`; `CLEARING` payments refused (no receipt) | REQ-CMP-001, -030 (subset), R-43, REQ-CLM-138 (CMP side) | **G: SL3-CMP-CREDIT**; C | light (+ money checklist) | 9, 10, 12 | 1.5 h |
| **SL4-RI-RECOVERY** | RI `Intake/**`, `Recovery/**` (commands, persistence use), `Api/Recovery*`, **RI migration (wave 2)** | ClaimView consumer (ExposureCreated, ReserveChanged, PaymentIssued, PaymentVoided, RecoveryRecorded, ClaimClosed/Reopened), idempotent on event id, monotonic `aggregateSequence`; claim attributes via `clm.Claim.get`; applicability (contract active at the loss date, scope); occurrence = claim; recalculation under a per-contract-year lock; sealed booked recovery rows; `RecoveryCalculated`; trace; `ri.Recovery.listByClaim/listByContract/trace` (as of record time); closed claim → outstanding 0 | REQ-RI-003, -004, -113, -114, -115 (per risk), -122, -127, -128, -129, -131 (Calculated only), -132, -136 | M: RI-REGISTRY, RI-ENGINE; C (CLM events) | **deep (money)** | 8, 9, 10, 12, 15, 22 | 4.5 h |
| **SL4-FIN-V4** | FIN `Seed/gr-test.finance.v4.json`, `Posting/**` (new handlers + rule wiring), `Domain/PostingRules.cs` | Rule set v4: recovery reserve; `RecoveryRecorded` by type with counterparty; recovery clearing per recovery against BIL receivable cash; FS: `CLEARING` payable and FS recovery → GL-2515, BIL `FS_CLEARING` cash → GL-2515, nets per counterparty + statement (check), `CLEARING` excluded from GL-2510; `RecoveryCalculated` → recoverable on paid and outstanding separately; `PaymentVoided` → reversal; every new path fail-closed on missing fields | REQ-FIN-037, -159, -160, -164, -299, BR-FIN-096/097, GF-05/GF-10 (subsets) | **G: SL3-FIN-RULES**; C | **deep (ledger)** | 8, 9, 10, 11, 12 | 4 h |
| **SL4-CLM-MONEY2** | CLM `Domain/Financials.cs`, `Commands/BuildTransactionSet.cs`, `SubmitTransactionSet.cs`, `ApplyApprovalDecision.cs`, `CloseClaim.cs`, `Queries/FinancialsReader.cs`, new `Services/ClaimFinancialEngine.cs`, `Authority/**`, **`Persistence/**` (CLM migration owner for the whole slice)** | Money engine extension: kinds RecoveryReserve/Recovery; recovery-case table and FS case/statement tables and payment links (whole slice-4 CLM schema); derived recoveries / open recovery reserve / net incurred; authority for recovery reserves (exposure total, PITFALLS 1) and `CLM.RECOVERY_WRITEOFF`/`CLM.FS_NET_SETTLEMENT` types; payment method `CLEARING` (no disbursement); reissue link columns; in-process `IClaimFinancialEngine.SubmitSystemSetAsync` for evidence-backed system sets; large-loss illustrative grants and BR-CLM-010 four-eyes (D-SL4-08); follow-ups: `clm.TransactionSet.list`, dry-run authority preview, typed approval diff, close-guard `errors[]`, withdraw leftover approvals on reject/stale (PLT) | REQ-CLM-095, -096, -097, -102, -107…-113, -150 (authority), REQ-CLM-072/073 (recoveries) | **G: SL3-CLM-REVERIFY**; C (PLT withdraw via fake until M: SL4-PLT) | **deep (money)** | 1, 2, 3, 4, 5, 7, 8, 10, 12, 15 | 4.5 h |
| **SL4-BIL-RECV** | BIL `Commands/Receivables*`, `Services/ReceivableMatching.cs`, `Events/**` (CashAllocated publication), source-register rows, **`Persistence/**` (BIL migration owner, wave 2)** | `bil.Receivable.register` for source `CLM_CLAIM_PAYMENT` (direction in: salvage, subrogation) and `FS_CLEARING` (net receivable); counterparty billing account (type `CLAIM_RECOVERY` or `CLEARING`); payment reference; `bil.Payment.take` matches by reference to a receivable; `CashAllocated` with claim/recovery/statement refs; sealed entries + `BillingEntryPosted` (`RECEIVABLE_REGISTERED`, `RECEIVABLE_COLLECTED`); Production refuses CLM receivables (fiscal treatment open, D-SL4-06); `bil.Receivable.get/list` | REQ-BIL-346, -354 (rows), -356, -125/-127 (matching subset) | **G: SL3-BIL-REFUND**; C | **deep (money)** | 8, 9, 10, 11, 12, 18, 20 | 3.5 h |
| **SL4-UI-BIL** | `web/src/modules/billing/receivables/**` (new) | Receivables list/detail (source, counterparty, claim link, payment reference, open/paid), record a receipt against a receivable; FS_CLEARING release approval for `billingmgr` (approvals-inbox pattern); disbursement detail shows CLEARING method and statement ref | SCR-BIL (receivables subset); REQ-BIL-346, -355, -356 | C; live walk after M: BIL-RECV, BIL-FSOUT | light + visual | 18–20, 25–28 | 2.5 h |
| **SL4-UI-CLM-REC** | `web/src/modules/claims/recoveries/**` (new), `claims/payments/**` (new), **`ClaimViewPage.tsx` (sole owner in slice 4)** | Claim file: Recoveries tab (cases, recovery reserve via the set builder, demand, receipts, write-off), money-card lines "Ανάκτηση · …" and net incurred, "Αντασφαλιστική ανάκτηση" card from `ri.Recovery.listByClaim`, liability facts editor, transaction-set list, authority preview in the builder, close-guard detail, payment void/stop/reissue actions; mounts the FS slots from `claims/fs/index.ts` | SCR-CLM-02, -06, -07, -08 (subsets); REQ-CLM-229 | **G: SL3-UI-CLM**; C; live walk after M: RECOVERY-OPS, PAYOPS | light + visual | 25–28 | 4 h |

### Wave 3 — after the wave-2 cores merge (5 WPs)

| WP | Module (owned area) | Scope | PRD / REQ | Depends on | Review | Est. |
|---|---|---|---|---|---|---|
| **SL4-CLM-RECOVERY-OPS** | CLM `Commands/Recoveries/**`, `Commands/Liability*`, `Events/Billing*`, `Api/RecoveriesController.cs`, `CreateExposure.cs` (MTPL guard), `SubmitFnol.cs` (liability facts) | Recovery case lifecycle (Subrogation, Salvage), milestones, subrogation proposal on fault (REQ-CLM-144), salvage details, demand → `bil.Receivable.register`, consume `CashAllocated` → system set (Recovery, allocation pro rata to paid or user-specified) → `RecoveryRecorded`; write-off; liability facts; MTPL third-party exposures `NOT_TRACKED` + Production refusal | REQ-CLM-076 (subset), -143…-147, -150, -151, REQ-CLM-062 (MTPL guard) | M: CLM-MONEY2; C: BIL | **deep (money)** | 4 h |
| **SL4-CLM-FS-CASE** | CLM `Commands/FriendlySettlement/Case*`, `Api/FriendlySettlementController.cs`, `Events/FsClearing*`, `Host` dev endpoint file (D-SL4-18) | Capability gate; eligibility via SPI with stored result; own-settlement exposure + FS receivable (recovery reserve type FS at the clearing value, expected gain/loss); submit → `FriendlySettlementSubmitted`; at-fault notification → create/match claim, MTPL_PD exposure, payable method `CLEARING` (system set from the notification evidence, authority still checked) | REQ-CLM-155…-158 | M: CLM-MONEY2, MKT-FS | **deep (money)** | 4 h |
| **SL4-CLM-FS-STATEMENT** | CLM `Commands/FriendlySettlement/Statement*`, `Queries/FsStatementReader.cs` | Import via SPI, match lines to receivables/payables, record FS recoveries and settle `CLEARING` payables (system sets), exceptions list, net per counterparty, approval (`CLM.FS_NET_SETTLEMENT`, four-eyes above threshold), hand-over to BIL (disbursement or receivable), consume BIL outcome; rejected/stopped → Pending | REQ-CLM-160, -264 | starts **on the FS-CASE branch** 1.5 h after it; merges after M: FS-CASE; C: BIL | **deep (money)** | 3.5 h |
| **SL4-BIL-FSOUT** | BIL `Commands/Disbursements.cs` (FS_CLEARING request path), `Commands/ReleaseApproval*`, source register | `FS_CLEARING` disbursement: lines sum = net, CLM approval evidence verified (`verifyForExecution`), method `CLEARING` only for this source (`BIL-ERR-SOURCE` otherwise), counterparty screening + VoP once, **release approval always** by `Staff.BillingManager` ≠ requester (PLT approval), LA-24 entries with statement ref | REQ-BIL-197, -198, -354, -355, -357 | M: BIL-RECV | **deep (money + security)** | 3 h |
| **SL4-UI-CLM-FS** | `web/src/modules/claims/fs/**` (new, exports the slots), `claims/fs-statements/**` (new) | FS header fact (Επιλέξιμη ✓ / Μη επιλέξιμη with reasons, as the mockup), FS case card with expected gain/loss, at-fault leg badge, FS statements screen (lines, matched/unmatched, net per counterparty, approve net, BIL status) for `recovery`/`claimsmgr` | SCR-CLM-09 (subset); REQ-CLM-155…-160, -264 | **G: SL3-UI-CLM**; C; live walk after M: FS-STATEMENT | light + visual | 3.5 h |

### Wave 4 — payment corrections (tail, off the E2E-06 path) (2 WPs)

| WP | Module (owned area) | Scope | PRD / REQ | Depends on | Review | Est. |
|---|---|---|---|---|---|---|
| **SL4-CLM-PAYOPS** | CLM `Commands/Payments/**`, `Commands/RecordDisbursementOutcome.cs`, `Api/PaymentsController.cs`, `Events/Fiscal*` | `clm.Payment.void/stop/reissue`; consume `DisbursementStopped/Voided/Returned/Rejected` → reversal transaction (reserve restored if eroding) → `PaymentVoided` once; reissue = new payment linked to the original via the engine (all checks re-run); fiscal settlement receipt request on `PaymentIssued` (not for `CLEARING`) and MARK stored from `FiscalDocRegistered` | REQ-CLM-125, -126, -127, -136, -138 | M: RECOVERY-OPS (CLM batch order), CMP-RECEIPT; C: BIL | **deep (money)** | 3.5 h |
| **SL4-BIL-DISBOPS** | BIL `Commands/DisbursementOps*`, `Services/PaymentAdapters.cs` (manual stub mode), dev bank endpoints | `bil.Disbursement.stop/void` (BIL.DisbursementStop authority, `BIL-ERR-NOT-STOPPABLE`); Returned/Rejected processing; events typed; reversal entries restoring the source payable; Development-only manual stub-bank mode (`Billing:StubBank:Mode=Manual`, dev endpoints issue/clear/return/reject); changed-account four-eyes at release (REQ-BIL-199) for reissues; D-SL2-10e (same IBAN, new holder → new version + VoP) | REQ-BIL-199, -208, -210, -361, -362 | M: BIL-FSOUT | **deep (money + security)** | 3.5 h |

### Wave 5 — integration

| WP | Module (owned area) | Scope | Depends on | Review | Est. |
|---|---|---|---|---|---|
| **SL4-E2E** | `tests/e2e/tests/e2e02x*`, `e2e06*`, `payops*`; `infra/local/seed-demo.py --claims4`; integration fixes (small, listed) | API specs: E2E-02 §3.1 (all assertions of the amount table, every journal balanced, GL-2510 and recovery clearing 0, RI deltas per participant, replay idempotent), E2E-06 §3.2 incl. the receivable variant (GL-2515 = 0 per statement), payment corrections §3.3. Playwright UI specs for E2E-02 and E2E-06 with `alive(page)`. CI job `e2e06` turned on | phase A (API) at M: RECOVERY-OPS, RI-RECOVERY, FIN-V4, BIL-RECV; phase B at M: all | light (integration) | 5 h |

### Critical path

There are two converging chains, and both start at slice-3 gates:

- **G: SL3-CLM-REVERIFY → CLM-MONEY2 → CLM-FS-CASE (+ FS-STATEMENT on its branch) → SL4-E2E (E2E-06);**
- **G: SL3-BIL-REFUND → BIL-RECV → BIL-FSOUT → SL4-E2E (E2E-06).**

RI (wave 1 → RI-RECOVERY) is off the critical path, so it is started first to absorb its deep review early.

## 6. Interface-first: contracts typed by SL4-CONTRACTS

All changes are additive. Every event field a consumer depends on is **always set** (PITFALLS 12). No personal data
goes in events (names, IBANs, plates, AFMs; PITFALLS 18).

| Module | Operations (typed request/response/errors) | Events / SPI |
|---|---|---|
| RI | `ri.Contract.create/update/submit/approve/get/list/applicable`. Create takes: type `XOL_PER_RISK` only; legal entity; contract year; period; currency EUR; scope (product codes, coverage codes); clause (`alaeIncluded`, `statutoryInterestIncluded`, `recoveriesInure = REALISED_ONLY`); layers (no, attachment, limit, aad, aal?); participations (reinsurer party, signed line %, lead); placed %. `approve` takes decision APPROVE/RETURN and a reason. `ri.Recovery.listByClaim(claimId, knownAt?)`, `listByContract`, `trace(batchId)`. Errors `RI-ERR-VALIDATION`, `-SIGNED-LINES`, `-STATE`, `-SOD`, `-STALE` | `RIContractActivated` typed. `RecoveryCalculated` typed: batch, contract, contract year, occurrence, claim ids, deltas[layer, participant, incurred, paid, outstanding] in 4 amounts (equal while EUR), claim totals, posting key (item RECOVERY, contract type, direction CEDED, line, participant type), SII LoB, `ifrs17GroupRef` = `UNASSIGNED`, `sourceCorrelationKey` |
| CLM | `clm.Recovery.create/get/list/recordMilestone/demand/writeOff`, salvage details (estimate, buyer party, sale price). `clm.Claim.update` liability facts (insured fault %, fault source, counterparty insurer party, joint report flag, vehicle count, accident in Greece). `clm.TransactionSet.build` accepts `RECOVERY_RESERVE` lines (`recoveryId`); its dry-run returns `authorityPreview[]` (type, cost type, amount, outcome WITHIN/REFER/DENY, role). `clm.TransactionSet.list(claimId)`. Typed approval `diff`. Close guard `errors[]` items (code, exposureIds). `clm.FriendlySettlement.evaluate/openOwnSettlement/submit/get/list/importStatement/getStatement/approveNet`. `clm.Payment.void/stop/reissue`. Payment view gains `method` (incl. `CLEARING`), `reissueOf`, `fiscalMark?`. Errors `CLM-ERR-FS-DISABLED`, `-ALLOCATION`, `-NOT-STOPPABLE`, `-RECOVERY-STATE`, `-MTPL-CLOCKS-REQUIRED` | `RecoveryRecorded` typed (recovery id, type, counterparty party id, lines per reserve line/exposure/cost type, 3 amounts, accounting date, receivable id?, fsStatementId?). `ReserveChanged` kind `RECOVERY_RESERVE` + `recoveryId`. `PaymentIssued` method `CLEARING` + `fsStatementId?`. `PaymentVoided` typed (payment id, reason VOIDED/STOPPED/RETURNED/REJECTED, reversal txn ids, lines). `FriendlySettlementSubmitted` typed |
| BIL | `bil.Receivable.register` typed (source type `CLM_CLAIM_PAYMENT` or `FS_CLEARING`, source id, counterparty party, claim id?, recovery id?, statement ref?, purpose SALVAGE/SUBROGATION/FS_NET, amount, due date → receivable id, billing account id, payment reference). `bil.Receivable.get/list` (slice addition, D-SL4-06). `bil.Payment.take` gains `paymentReference`/`receivableId` matching. `bil.Disbursement.request` for `FS_CLEARING` (statement ref, lines[claim id, fs case id, amount], method `CLEARING`). `bil.Disbursement.approveRelease` (slice addition, D-SL4-12). `bil.Disbursement.stop/void` typed. Errors `BIL-ERR-SOURCE`, `-DUPLICATE`, `-FISCAL-TREATMENT-OPEN`, `-LINES-MISMATCH`, `-NOT-STOPPABLE`, `-SOD` | `CashAllocated` + receivable id, claim id, recovery id, statement ref, source type. `DisbursementStopped/Voided/Returned/Rejected` typed (disbursement, source type + id, reason, rejecting party). `BillingEntryPosted` entry types `RECEIVABLE_REGISTERED`, `RECEIVABLE_COLLECTED`, `FS_CLEARING_RELEASED`, `FS_CLEARING_CLEARED`, `DISBURSEMENT_REVERSED`, with dims claim id, recovery id, statement ref, counterparty party id |
| MKT | — | SPI `IFriendlySettlementClearing` (`Market.Contracts/Spi`): `EvaluateEligibilityAsync(claimFacts)` → result ELIGIBLE / NOT_ELIGIBLE / NOT_APPLICABLE, reasons, ruleId, ruleVersion, legalStatus, provisional, clearing value basis; `SubmitAsync` → clearing reference; `ReceiveNotificationAsync` → typed notification; `SettlementStatementAsync(period, counterparty?)` → statement id, lines (clearing ref, direction, amount), net per counterparty. `submitDispute`/`recordReply` typed but not implemented (OUT). Capability key `cap.clm.friendly_settlement` |
| CMP | `cmp.FiscalDocument.request` accepts source type `CLM_CLAIM_PAYMENT`, category `CLAIM_SETTLEMENT_RECEIPT` | — |
| PLT | In-process only: `IPlatformApprovalService.WithdrawAsync(approvalRequestId, reason)` (no REST, owner module only) | `ApprovalDecided` gains outcome `WITHDRAWN` (if not present) |
| Dev only (outside the public contract, like `/dev/clock`) | `POST /dev/fs-clearing/notifications`; `POST /dev/bank/disbursements/{id}/{issue\|clear\|return\|reject}` | — |

## 7. Parallelism and conflict map

The rule is that no two concurrent WPs touch the same DbContext, migration folder or generated contract folder. Where
CLM has two or three builders at once (waves 2–4), they own **disjoint folders**, and only SL4-CLM-MONEY2 adds a CLM
migration. Later CLM WPs ask the orchestrator for schema changes, and those go to a MONEY2 follow-up.

### 7.1 Ownership

| WP | Owns (files/folders) | Shared files touched (append only) | Gate / rule | Merge risk |
|---|---|---|---|---|
| SL4-CONTRACTS | `contracts/**`, `src/*.Contracts/Generated/**`, `Market.Contracts/Spi/IFriendlySettlementClearing.cs`, `INDEX.md` (regenerated) | count tests (self-maintaining) | Edits only new ops/schemas in `clm.yaml`/`bil.yaml`. If a slice-3 WP typed its own op meanwhile, merge textually, then regenerate. **Never concurrent with a slice-5 contracts WP** (§7.3) | med |
| SL4-RI-REGISTRY | `Modules.Reinsurance/Persistence/**` (RI migration owner, wave 1), `Registry/**`, `Api/Contracts*`, `ReinsuranceModule.cs`, `Host/permissions/ri.json` (new); `tests/…/Reinsurance/Registry/` | migrate-job DbContext list in `Host/Database/**` (append one line) | — | low |
| SL4-RI-ENGINE | `Modules.Reinsurance/Domain/Recovery/**`; `tests/…/Reinsurance/Engine/` | none | No DB, no DI | low |
| SL4-E2E-HARNESS | `tests/e2e/support/claims4/**`, `run-e2e06.sh` | `ci.yml` (new `e2e06` block only) | Never edits `e2e01–03` blocks or existing helpers | low |
| SL4-UI-RI | `web/src/modules/reinsurance/**` | `routes.tsx`, staff nav list (append) | — | low |
| SL4-PLT | `Platform/Approvals/Withdraw*`, PLT role catalogue | `dev-users.Development.json` (append) | **G: SL3-PLT-SUPPORT** | low |
| SL4-MKT-FS | `CountryPacks.GR/Claims/**`, `CountryPacks.CY/Claims/**`, `Modules.Market/Services/Capability*` | `Host/Hosting/CountryPackBinding.cs` (append), GR/CY configuration (append keys) | **G: SL3-MKT-TREATMENT**; serialise with slice-5 MKT WPs (§7.3); no MKT migration | med |
| SL4-CMP-RECEIPT | CMP fiscal source/category mapping files, `CountryPacks.GR/Fiscal/MyDataStubFiscalChannel.cs` | CMP permission file | **G: SL3-CMP-CREDIT** | low |
| SL4-RI-RECOVERY | `Modules.Reinsurance/Intake/**`, `Recovery/**`, `Api/Recovery*`, `Persistence/**` (RI migration owner, wave 2) | `ReinsuranceModule.cs` (DI append), `ri.json` | M: RI-REGISTRY + RI-ENGINE | low |
| SL4-FIN-V4 | `Modules.Finance/Seed/gr-test.finance.v4.json`, `Posting/Recovery*`, `Posting/Fs*`, `Posting/RiFacts.cs`, `Posting/ClaimFacts.cs` (edit), `Domain/PostingRules.cs` | FIN permission file | **G: SL3-FIN-RULES**; FIN migration only if needed (owner) | med |
| SL4-CLM-MONEY2 | as §5 wave 2, incl. `Persistence/**` (**CLM migration owner for the slice**) and `Authority/**` | `ClaimsModule.cs`, CLM permission file | **G: SL3-CLM-REVERIFY** | high (core files) |
| SL4-BIL-RECV | `Modules.Billing/Commands/Receivables*`, `Services/ReceivableMatching.cs`, `Persistence/**` (BIL migration owner, wave 2) | `Events/IntakeHandlers.cs` (append handler), `Commands/Payments.cs` (matching hook), BIL permission file | **G: SL3-BIL-REFUND** | med |
| SL4-UI-BIL | `web/src/modules/billing/receivables/**` | `routes.tsx` | Never edits `billing/refunds/**` or `InvoicePage.tsx` (slice 3) | low |
| SL4-UI-CLM-REC | `web/src/modules/claims/recoveries/**`, `claims/payments/**`, **`ClaimViewPage.tsx`**, `claims/FinancialsTab.tsx`, `claims/ClaimMoney.tsx` | `routes.tsx`, claims i18n (own namespace files) | **G: SL3-UI-CLM** (it owns the banner slot in `ClaimViewPage.tsx`) | med |
| SL4-CLM-RECOVERY-OPS | as §5 wave 3 | `ClaimsModule.cs`, CLM permission file | M: MONEY2; no migration | med |
| SL4-CLM-FS-CASE | `Commands/FriendlySettlement/Case*`, `Api/FriendlySettlementController.cs`, `Events/FsClearing*`, `Host/Hosting/DevFsClearingEndpoints.cs` (new) | `ClaimsModule.cs`, CLM permission file | M: MONEY2, MKT-FS; no migration | med |
| SL4-CLM-FS-STATEMENT | `Commands/FriendlySettlement/Statement*`, `Queries/FsStatementReader.cs` | `ClaimsModule.cs` | On the FS-CASE branch; no migration | med |
| SL4-BIL-FSOUT | `Commands/Disbursements.cs` (FS path), `Commands/ReleaseApproval*` | BIL permission file | M: BIL-RECV; no migration unless agreed with RECV | med |
| SL4-UI-CLM-FS | `web/src/modules/claims/fs/**`, `claims/fs-statements/**` | `routes.tsx` | Never edits `ClaimViewPage.tsx`; exports slot components that UI-CLM-REC mounts | low |
| SL4-CLM-PAYOPS | `Commands/Payments/**`, `Commands/RecordDisbursementOutcome.cs`, `Api/PaymentsController.cs`, `Events/Fiscal*` | `ClaimsModule.cs` | M: RECOVERY-OPS; no migration | med |
| SL4-BIL-DISBOPS | `Commands/DisbursementOps*`, `Services/PaymentAdapters.cs`, `Host/Hosting/DevBankEndpoints.cs` (new) | `Commands/Disbursements.cs` (state hooks) | M: BIL-FSOUT; BIL migration owner, wave 4, if needed | med |
| SL4-E2E | `tests/e2e/tests/e2e02x*`, `e2e06*`, `payops*`; `infra/local/seed-demo.py` (`--claims4` block) | `ci.yml` (`e2e06` block) | After SL3-E2E merged (seed-demo owner in slice 3) | low |

### 7.2 Shared files and their single owner in slice 4

| File | Rule |
|---|---|
| `ClaimViewPage.tsx` | SL4-UI-CLM-REC only (after SL3-UI-CLM merged); FS parts come in as slot components from `claims/fs/index.ts` |
| `ClaimsModule.cs`, per-module permission files, `routes.tsx`, `dev-users.Development.json`, `CountryPackBinding.cs` | Append only; on conflict keep both sides |
| CLM `Persistence/**` | SL4-CLM-MONEY2 only |
| BIL `Persistence/**` | SL4-BIL-RECV (wave 2), then SL4-BIL-DISBOPS (wave 4) |
| RI `Persistence/**` | SL4-RI-REGISTRY (wave 1), then SL4-RI-RECOVERY (wave 2) |
| `ci.yml` | SL4-E2E-HARNESS adds the `e2e06` block; SL4-E2E edits only that block |
| `seed-demo.py` | SL4-E2E only, after SL3-E2E merged |
| `DECISIONS.md`, `STATUS.md`, `HANDOVER.md`, `PITFALLS.md` | Orchestrator only |

### 7.3 Cross-slice rules (slice 3 running, slice 5 planned in parallel)

- **Slice 3 has priority on its gates.** A slice-4 WP never edits a file that a running or unmerged slice-3 WP owns
  (`SLICE-PLAN-3.md` §7). The gates are listed in §8.
- **Contracts:** SL4-CONTRACTS and any slice-5 contracts WP must not run at the same time. Generated folders and
  `INDEX.md` are resolved by regeneration only (D-PRG-21).
- **MKT:** SL4-MKT-FS owns only the new `Claims/` pack folders and the capability resolver. Slice-5 MKT work (pack
  rollback) must not edit those, and vice versa. Neither adds an MKT migration without the orchestrator serialising
  them.
- **Dev users and roles:** append only. Slice 5's UW roles and slice 4's recovery/RI roles are disjoint.
- **CI:** one job block per slice (`e2e03`, `e2e06`, slice 5's own). No job edits another slice's block.

## 8. Rolling schedule (6–8 builders across the machine, pipelined)

t = slice-4 wall-clock hours from the start of wave 1. Slice-3 gate times are taken from `SLICE-PLAN-3.md` §8 shifted to
now (slice 3 is in S0). **When a gate slips, the gated WP slips with it; nothing starts early on slice-3 files.**
Slice 3 keeps priority on builder slots; slice 4 uses what is free (about 4 slots until t≈8, then 6–8).

| t (h) | Gates expected (slice 3) | Merging / in review (slice 4) | Starts building (slice 4) |
|---|---|---|---|
| 0 | — | — | **Wave 1:** CONTRACTS, RI-REGISTRY, RI-ENGINE, E2E-HARNESS (4) |
| 2–3 | G: SL3-MKT-TREATMENT, SL3-PLT-SUPPORT | CONTRACTS PR (light) → merge, HARNESS rides on it (D-USR-18) | UI-RI, UI-BIL (contracts); MKT-FS; PLT |
| 4–5 | G: SL3-CMP-CREDIT | RI-ENGINE PR → deep review; RI-REGISTRY PR → deep review | CMP-RECEIPT |
| 5–7 | G: SL3-UI-CLM | MKT-FS + CMP-RECEIPT one batch PR (light); PLT PR → deep review; RI fixes → merged | RI-RECOVERY; UI-CLM-REC, UI-CLM-FS (contracts) |
| 7–8 | G: SL3-FIN-RULES, **SL3-CLM-REVERIFY** | UI-RI PR (light + visual) | FIN-V4; **CLM-MONEY2** |
| 8–12 | G: **SL3-BIL-REFUND** (≈ t12) | PLT merged; UI-BIL PR (contracts-only round) | **BIL-RECV** (t12) |
| 11–13 | — | RI-RECOVERY PR, FIN-V4 PR → deep reviews | — |
| 12.5–14.5 | — | CLM-MONEY2 PR → deep review → fix → merged | CLM-RECOVERY-OPS, **CLM-FS-CASE** (t14.5) |
| 15.5–17 | — | BIL-RECV PR → deep review → merged; RI-RECOVERY, FIN-V4 merged | **BIL-FSOUT** (t17); CLM-FS-STATEMENT on the FS-CASE branch (t16) |
| 18.5–20.5 | — | RECOVERY-OPS, FS-CASE PRs → deep reviews → merged | SL4-E2E phase A (E2E-02 API); CLM-PAYOPS (t20.5) |
| 20–22 | — | FS-STATEMENT PR, BIL-FSOUT PR → deep reviews → merged | SL4-E2E E2E-06 API; BIL-DISBOPS (t22); UI live walks |
| 22–26 | — | UI-CLM-REC, UI-CLM-FS, UI-BIL merged after live walks | SL4-E2E phase B (UI specs) |
| 24–28 | — | PAYOPS, DISBOPS PRs → deep reviews → merged; `e2e06` green on CI | Slice acceptance walk in real Chrome (FYI screenshots) |

**Estimate:**
- about 75 builder hours;
- about 15 review hours (13 deep reviews at ≈ 45 min, 9 light at ≈ 25 min);
- about 10–12 hours of fix rounds;
- so roughly **100–105 agent-hours** in all.

Wall-clock:
- **about 26–28 hours from the start of wave 1 at 6–8 agents.** Slice 4 ends about 10–12 hours after slice 3's E2E
  merges.
- **Without the slice-3 gates** (slice 3 already merged), the same plan runs in about 18–20 hours, bounded by the
  CLM-MONEY2 → FS-CASE → FS-STATEMENT → E2E chain.
- The wave-4 payment corrections (PAYOPS, DISBOPS, about 9 agent-hours with reviews) are the first thing to defer to
  slice 6 if wall clock matters more than D-SL2-13. Deferring them only removes E2E-02 step 10's MARK assertion (it
  moves with PAYOPS).

## 9. Rules for every slice-4 builder

- Everything in HANDOVER §4 applies, including the module pattern, personal data, money, time and concurrency rules.
- Briefs are filled from `briefs/BUILDER-TEMPLATE.md` and say "Runs on Sonnet 5.5 (model: sonnet), D-USR-16". The
  ready briefs are in `orchestration/briefs/sl4/` (waves 1–2). The orchestrator writes wave 3–5 briefs from the §5 rows
  when wave 2 merges, so that they include what the reviews found.
- **Money:** CLM never moves or posts cash; BIL is the only cash executor (D1); FIN posts only from module events.
  Authority is checked on aggregates (exposure totals, cumulative paid, recovery reserve totals; PITFALLS 1).
  System-recorded sets (BIL cash evidence, FS notifications and statements) carry their evidence reference and never
  accept client-supplied amounts (PITFALLS 7).
- **Regulatory values** come only from the PRDs, with `legalStatus`. FS values and the claim fiscal receipt codes are
  UNVERIFIED/open: provisional outside Production, unbound or refused in Production (D-SL4-03, -06, -09, -11).
- **Commercial values are illustrative:** the XoL treaty, authority grants, the FS net four-eyes threshold, cost
  categories and the GL placeholders.
- **UI:**
  - The claim file is the mockup's «Φάκελος ζημίας». Recoveries appear as money-card lines in the success colour
    («Ανάκτηση · Υποκατάσταση», «Ανάκτηση · Διάσωση», «Ανάκτηση · Φιλικός Διακανονισμός (αναμενόμενη)»).
  - The FS state is a header fact («Φιλικός Διακανονισμός Επιλέξιμη ✓»).
  - The RI recoverable is a card in the right column.
  - The RI treaty pages and FS statements follow the record PageHeader + Section cards pattern.
  - Approvals follow the claims approvals inbox.
  - Side-by-side screenshots go in `orchestration/ux/<wp>/` (D-USR-11).
- Commit early (PITFALLS 34). Push your branch; open a PR only if your WP is meaningful (D-USR-18). The orchestrator
  merges.

## 10. Open business questions that block amounts, and how the slice handles them

| Question | Source | Handling in slice 4 |
|---|---|---|
| FS agreement: limits (EUR 6,500 press vs 5,000/15,000 prompt), eligibility rules, clearing-value averages, dispute rules, statement file spec | OI-CLM-01, C-01, OQ-018 | Stub adapter, never in Production; limit 5,000 (lower candidate) UNVERIFIED/provisional; basis ACTUAL; JSON test statement (D-SL4-03) |
| FS counterparty reply window | OI-CLM-02 | Disputes OUT |
| Whether the company joins FS clearing at go-live | D-278, PRD-07 §16.5 item 4 | Capability switch; Production unbound → `CLM-ERR-FS-DISABLED` |
| myDATA settlement-receipt document types/classifications | OI-CLM-04, OQ-012 | Stub `UNMAPPED-OQ-012` (D-SL4-11) |
| Fiscal treatment of salvage/subrogation receivables | REQ-BIL-346 ("its own fiscal treatment"), not stated | No fiscal request; CLM receivables refused in Production (D-SL4-06) |
| Motor RI programme: XoL only?, layers, unlimited MTPL layer, ALAE/interest inclusion, recoveries inuring, participants | A-01, OI-RI-05 | Illustrative GT-05 treaty "500k xs 250k", ALAE included, interest excluded, realised recoveries inure (D-SL4-05) |
| Claims authority limits and four-eyes threshold; FS net-settlement threshold | PRD-07 §16.5 item 3; BR-CLM-010/042 defaults | ClaimsManager ≤ 1,000,000 illustrative; four-eyes ≥ 50,000; FS net four-eyes > 50,000 (D-SL4-08, -12) |
| Motor cost categories incl. salvage/subrogation lines | PRD-07 §16.5 item 1 | Illustrative (D-SL2-04 extended) |
| Real Greek chart of accounts; recovery and recovery-clearing accounts; RI recoverable accounts; IFRS 17 RI-held groups | HANDOVER §9; PRD-09 §16.5 item 6 | GL-2515, GL-1320, GL-4210 from PRD-09 *Illustrative*; recovery income and recovery clearing are **placeholders** (D-SL4-10); RI-held group `UNASSIGNED` |
| MTPL statutory clocks (offer 3 months, payment due 10 days, assessment 15/25 days), interest rate | GR-14 UNVERIFIED, OI-CLM-03/06/13 | Clocks OUT; MTPL exposures `NOT_TRACKED`, refused in Production (D-SL4-09) |
| Reinsurer security floor and ratings | PRD-08 §9.3 (no core default) | Security list OUT |

## 11. Proposed decisions (the orchestrator appends these rows to `DECISIONS.md`)

| ID | Decision | Reason | Status |
|---|---|---|---|
| D-SL4-01 | **Slice 4 scope = finish claims (W7), `SLICE-PLAN-4.md`.**<br>**In:**<br>- E2E-02 cut to steps 1–2, 4–5, 8–14 (staff FNOL, large reserves, payment, stub settlement receipt, subrogation and salvage recoveries through BIL receivables, RI recovery);<br>- E2E-06 against D1 `FS_CLEARING` (steps 1–8 minus WRK classification and disputes);<br>- minimal RI (per-risk XoL registry + recovery engine);<br>- payment void/stop/return/reissue;<br>- the slice-2 CLM follow-ups;<br>- UI;<br>- CI job `e2e06`.<br>**Out:**<br>- statutory clocks, offers and documents (E2E-02 steps 3, 6, 7);<br>- FS disputes;<br>- deductible and guarantee-fund recoveries;<br>- payment holds;<br>- RI cession, per-event/cat, reinstatements, versioning, settlements, notices, batching window;<br>- IFRS 17 RI-held groups and daily RI reconciliation (step 15);<br>- E2E-07/08, which still need a home after the CMP clock engine | Every out item is blocked by an open question or an unbuilt module (CMP clocks, DOC, WRK, IFRS 17), or is not needed to prove the two journeys | Made |
| D-SL4-02 | **E2E-06 is built against D1 `FS_CLEARING`; PRD-18's "fails as written" steps 7–8 are replaced.**<br>- CLM never moves cash: the own leg is an ordinary claim payment plus an FS recovery reserve at the clearing value; the at-fault leg is a payment with method `CLEARING` and no disbursement.<br>- Per counterparty and statement, CLM computes one net, approves it (`CLM.FS_NET_SETTLEMENT`) and hands it to BIL as an `FS_CLEARING` disbursement (method `CLEARING`, clearing office payee, lines summing to the net) or receivable; a rejected or stopped net returns to Pending.<br>- FIN posts CLM facts against GL-2515 and the cash only from BIL `FS_CLEARING` entries; GL-2515 nets to zero per counterparty and statement (BR-FIN-097); `CLEARING` is excluded from the GL-2510 per-payment rule, replacing D-SL2-12a's suspense | PRD-18 F-400/XMR-F-214, D1, REQ-CLM-158/160/264, REQ-BIL-354…357, REQ-FIN-037/299 | Made |
| D-SL4-03 | **Friendly Settlement values come from a stub adapter, never bound in Production.**<br>- The GR `FriendlySettlementClearing` stub (like D-SL2-05) evaluates eligibility with: both insurers on a stub member list; accident in Greece; two vehicles; material damage only; liability 0/100; amount ≤ **EUR 5,000**. That is the lower of the two conflicting PRD candidates (C-01), `legalStatus UNVERIFIED`, `provisional: true`.<br>- Clearing value basis **ACTUAL** (averages unknown).<br>- Statements come from the stub's own store in a documented JSON test format.<br>- `cap.clm.friendly_settlement`: GR on outside Production, CY off (`NotApplicable`, SPI not called). In Production the SPI is unbound, so FS fails closed with `CLM-ERR-FS-DISABLED` | OI-CLM-01/02 and OQ-018 leave the agreement text, limits, averages and file format open; values are never invented | Open-Reg |
| D-SL4-04 | **Minimal RI module.**<br>- Contract type `XOL_PER_RISK` only, EUR only (four amounts equal, rate ids null).<br>- One contract version row with valid and record periods, so that versioning and endorsement land later without re-deciding.<br>- Maker ≠ checker approval through PLT (`RI.CONTRACT_APPROVE`; enterer ≠ approver).<br>- Activation at the period start (Europe/Athens).<br>- Participations are PTY organisation parties whose signed lines sum to the placed %. No security list, ratings or LEI (REQ-PTY-097 deferred).<br>- Out: cession and `ChargeDeltaEmitted` intake, per-event/cat layers and occurrence linking, reinstatements, deposit premiums, commissions, statements, settlements, notices, FX/ROE | Motor P1 is XoL only (A-01); E2E-02 needs per-risk recovery only (GT-05) | Made |
| D-SL4-05 | **RI recovery rules for the slice.**<br>- Occurrence = claim (per risk).<br>- In-scope exposures: the contract's coverage scope (illustrative treaty: MTPL coverages).<br>- UNL incurred = Indemnity paid + open reserve (+ ALAE when the clause includes it; statutory interest only when included; never ExpenseUnallocated) − **realised** recoveries (REQ-RI-117 default; recovery reserves do not inure). UNL paid = paid − realised recoveries.<br>- Layer loss = min(max(UNL − A, 0), L), with an annual aggregate R(C) = min(max(C − AAD, 0), AAL) and full contract-year restatement (BR-RI-007).<br>- Paid recoverable is computed on UNL paid; outstanding = incurred − paid recoverable.<br>- Deltas = target − booked per occurrence × layer × participant, split by signed line, rounded per MKT, with the residual to the lead participant.<br>- A closed claim has outstanding 0.<br>- The illustrative treaty for tests is GT-05 "EUR 500k xs 250k", ALAE included, interest excluded, 60/40 placed. E2E-02 asserts §3.1's table | REQ-RI-116…-127, GT-05, REQ-RI-119 example; A-01/OI-RI-05 open, so the treaty is test data | Made |
| D-SL4-06 | **The cash for CLM recoveries comes through BIL receivables (REQ-BIL-346).**<br>- Salvage and subrogation demands call `bil.Receivable.register` with source `CLM_CLAIM_PAYMENT`, whose register row gains direction "in". PRD-06 names deductible and salvage; E2E-02 step 11 uses the same path for subrogation.<br>- The receivable sits on a counterparty billing account of slice type `CLAIM_RECOVERY`.<br>- Staff record the receipt with `bil.Payment.take` by payment reference. BIL publishes `CashAllocated` with the claim and recovery ids, and CLM then records the Recovery transaction in an evidence-backed **system set** (maker = system principal, evidence = BIL allocation id, client amounts never accepted).<br>- No fiscal document is requested: the fiscal treatment is open, so BIL refuses CLM receivables in Production.<br>- `bil.Receivable.get/list` are slice additions (reads) | One cash executor (D1); the PRD names no fiscal rule for these receivables | Made |
| D-SL4-07 | **Authority for recoveries.**<br>- A recovery-reserve increase is checked under `CLM.RESERVE` on the resulting **open recovery reserve of the exposure** (aggregate, PITFALLS 1), with D-SL2-03's illustrative limits; a reason code is required (REQ-CLM-102).<br>- Write-off uses `CLM.RECOVERY_WRITEOFF` (illustrative: Staff.ClaimsManager ≤ 50,000.00, Staff.RecoverySpecialist ≤ 5,000.00).<br>- Recording received cash needs no authority (no money leaves), but only the BIL-evidence path may record it | PRD-07 §12 lists the types; values are open (§16.5 item 3) | Made |
| D-SL4-08 | **Large-loss authority (illustrative), extending D-SL2-03/13.**<br>- Staff.ClaimsManager `CLM.RESERVE`/`CLM.PAYMENT` limit rises from 50,000.00 to **1,000,000.00**; above that, DENY.<br>- BR-CLM-010: a set whose referred amount exceeds **50,000.00** always needs an approver other than the maker, even when the maker's own authority covers it.<br>- The handler limit (5,000.00) is unchanged | GT-05 needs reserves of 300k and 900k; the values stay illustrative test data | Made |
| D-SL4-09 | **MTPL third-party exposures without statutory clocks.**<br>- Outside Production, MTPL_PD and MTPL_BI exposures for third-party claimants may be created with `statutoryClocks = NOT_TRACKED`, shown in the UI.<br>- Production refuses them (`CLM-ERR-MTPL-CLOCKS-REQUIRED`) until the CMP clock WP lands.<br>- No injury or P3 fields are captured | The CMP clock engine is not built and the GR clock values are UNVERIFIED (GR-14); fail closed | Open-Reg |
| D-SL4-10 | **FIN rule set v4 accounts.**<br>- PRD-09 *Illustrative* accounts: GL-5110, GL-2210, GL-2510, **GL-2515** (FS clearing), **GL-1320** (RI held asset for incurred claims, with dimension `recoverableBasis` PAID/OUTSTANDING), **GL-4210** (amounts recovered from reinsurers).<br>- **Placeholders**, flagged `PLACEHOLDER` (not in the PRD-09 chart): a recovery income account per type (subrogation, salvage, FS) and a **claim recovery clearing** account that nets to zero per recovery once BIL posts the receipt.<br>- The RI-held IFRS 17 group dimension is `UNASSIGNED` | The Greek chart and GL mapping are open (HANDOVER §9); the placeholders are never presented as the chart | Made |
| D-SL4-11 | **Fiscal settlement receipt through the stub channel.**<br>- On `PaymentIssued` (not for `CLEARING`), CLM requests a CMP document: source `CLM_CLAIM_PAYMENT`, role ISSUE, category `CLAIM_SETTLEMENT_RECEIPT`, idempotency source + id + role + revision. It stores the MARK on `FiscalDocRegistered`.<br>- Codes are `UNMAPPED-OQ-012` (OI-CLM-04); PRD-07's 13.1/13.2/2.1 types are recorded only as candidates (market guidance, Verify).<br>- CY returns `NotRequired`; the stub is never bound in Production | R-43, REQ-CLM-138; document types open | Open-Reg |
| D-SL4-12 | **FS net approval and BIL release.**<br>- `CLM.FS_NET_SETTLEMENT`, illustrative: Staff.RecoverySpecialist ≤ 50,000.00 (BR-CLM-042 default threshold); above that, four-eyes with Staff.ClaimsManager.<br>- Every `FS_CLEARING` disbursement needs a **release approval** by a Staff.BillingManager other than the requester (new `bil.Disbursement.approveRelease`). With no disbursement batches in the slice, this stands in for "every FS_CLEARING batch regardless of amount".<br>- The clearing office is an organisation party from the pack binding with a verified payee account; it is screened and VoP-checked once, with no claimant-level VoP | D1; REQ-BIL-206/357; values illustrative | Made |
| D-SL4-13 | **Payment corrections in, holds out.**<br>- Void (Issued, not Cleared), stop (Released, not Issued), return and reject create a linked reversal transaction (reserve restored if eroding) and publish `PaymentVoided` once.<br>- Reissue creates a new payment linked to the original and re-runs every check. A changed account waits for a BIL release approval by a user who did not change it (REQ-BIL-199).<br>- A Development-only manual stub-bank mode (`Billing:StubBank:Mode=Manual`, dev endpoints) lets tests hold a disbursement at Released/Issued.<br>- D-SL2-10e is fixed: the same IBAN with a new holder name gives a new account version and re-runs VoP.<br>- Sanctions/SIU/VoP holds and D-SL2-10a's hold-with-override stay out (no hold source exists) | D-SL2-12c/13 follow-ups; REQ-CLM-125…127, REQ-BIL-208/210/361 | Made |
| D-SL4-14 | **Slice-2 follow-ups.**<br>**Folded in:**<br>- `clm.TransactionSet.list` by claim;<br>- the dry-run `authorityPreview`;<br>- typed approval `diff`;<br>- close-guard detail in `errors[]`;<br>- PLT in-process `WithdrawAsync` (the owning module only) used by CLM when a set is rejected or stale;<br>- void/stop/reissue (D-SL4-13);<br>- FIN payment-void reversal.<br>**Cut, with reason:**<br>- payment holds (D-SL4-13);<br>- the GL-2510 break check (REQ-FIN-249), claim journals by policy number and rule-set history (reads, not on the journeys) | `SLICE-3-PLANNING-HANDOVER.md` §6 | Made |
| D-SL4-15 | **Slice-4 process.**<br>- 22 WPs in five waves; the CLM, BIL, FIN, CMP, MKT and PLT WPs are gated on the merge of the slice-3 WP that owns the area (§8). Wave 1 (contracts, RI, E2E harness, RI UI) starts at once.<br>- Disjoint-folder ownership where CLM has 2–3 builders at once; one migration owner per module per wave (CLM: MONEY2 for the whole slice).<br>- Never concurrent with a slice-5 contracts WP; MKT folders split with slice 5.<br>- **Every builder and reviewer runs on Sonnet 5.5.**<br>- Deep first-round reviews for 13 money/ledger/security/temporal WPs; reviews ≤ 45 min | Speed without touching slice 3's files or losing review depth | Made |
| D-SL4-16 | **Roles and dev users.**<br>- Staff.RecoverySpecialist (ROLE-20, `recovery`), Staff.ReinsuranceAccountant (ROLE-28, `riacct`, enters treaties), Staff.ReinsuranceManager (ROLE-27, `rimgr`, approves treaties).<br>- `superuser` gains them; SoD is unchanged.<br>- FS net maker: `recovery`; approver above the threshold: `claimsmgr`. Treaty maker: `riacct`; checker: `rimgr` | Contract role catalogue; SoD PRD-08 §12 (enterer ≠ approver) | Made |
| D-SL4-17 | **Liability facts on the claim.**<br>- Insured fault %, fault source, counterparty insurer (organisation party), joint accident report flag, vehicle count and accident-in-Greece flag, set at FNOL or by `clm.Claim.update` and audited.<br>- Subrogation is proposed when the insured's fault is < 100%, a counterparty insurer is known, the claim is not FS-eligible, and the exposure is paid or reserved (REQ-CLM-144).<br>- No other vehicle's plate or driver is stored (P2 not needed) | REQ-CLM-076/144/155 need the facts; WRK classification of the joint report is out | Made |
| D-SL4-18 | **The FS at-fault notification enters through a Development-only endpoint** `POST /dev/fs-clearing/notifications`. It is outside the public contract set like `/dev/clock` and `/dev/sign-in`, requires Platform.Admin, is audited and is never registered in Production (startup test). It hands the message to the stub's `ReceiveNotificationAsync`, and CLM's handler creates or matches the claim | No EAEE channel exists (OI-CLM-01) | Made |
| D-SL4-19 | **RI intake ordering and batching.**<br>- RI relies on the in-process dispatcher's per-aggregate order (D-ARC-26) and records the last applied `aggregateSequence` per claim. A lower or equal sequence is a no-op (idempotent on event id).<br>- Each movement triggers an immediate recalculation of its contract year under an advisory lock (one in flight per contract year).<br>- The 30 s / 500 batching window (REQ-RI-130) is deferred; the final totals equal a single full recalculation (REQ-RI-129) | Performance feature; determinism is kept | Made |
| D-SL4-20 | **E2E-02/-06 expected values are PRD examples, illustrative.**<br>- RI: GT-05 deltas +50k, +450k, −50k and the REQ-RI-119 split at 900k incurred / 400k paid.<br>- Salvage: REQ-CLM-145 (reserve 1,500, proceeds 1,700).<br>- FS: REQ-FIN-299 (payables 3,000, FS recovery 1,200, net payable 1,800).<br>- Assertions are structural wherever a value is open | XMR "blocked expectations assert structure, not amount" | Made |

## 12. STATUS rows (the orchestrator adds this section to `STATUS.md`)

```
## Phase 5 — slice 4 (planned): finish claims (W7)
Plan: `SLICE-PLAN-4.md`. Decisions: D-SL4-01..20. Briefs: `briefs/sl4/` (waves 1–2). Every builder and reviewer runs on Sonnet 5.5 (D-USR-16). Runs beside slice 3: CLM/BIL/FIN/CMP/MKT/PLT WPs start only after the gating slice-3 WP merges (G:).

| WP | Wave | Module | Depends on | Review | Est. | Status |
|---|---|---|---|---|---|---|
| SL4-CONTRACTS | 1 | contracts (all) | — | light | 3 h | planned |
| SL4-RI-REGISTRY | 1 | RI (registry, persistence) | contracts | deep (security+temporal) | 4 h | planned |
| SL4-RI-ENGINE | 1 | RI (Domain/Recovery) | contracts | deep (money) | 3.5 h | planned |
| SL4-E2E-HARNESS | 1 | tests/e2e, ci.yml | contracts | light (batched) | 1.5 h | planned |
| SL4-UI-RI | 1 | web reinsurance | M: contracts | light + visual | 3.5 h | planned |
| SL4-PLT | 2 | PLT (roles, approvals withdraw) | G: SL3-PLT-SUPPORT | deep (security) | 2.5 h | planned |
| SL4-MKT-FS | 2 | GR/CY pack FS stub, MKT capability | G: SL3-MKT-TREATMENT | light + reg. checklist | 2.5 h | planned |
| SL4-CMP-RECEIPT | 2 | CMP | G: SL3-CMP-CREDIT | light | 1.5 h | planned |
| SL4-RI-RECOVERY | 2 | RI (intake, recovery) | M: RI-REGISTRY, RI-ENGINE | deep (money) | 4.5 h | planned |
| SL4-FIN-V4 | 2 | FIN | G: SL3-FIN-RULES | deep (ledger) | 4 h | planned |
| SL4-CLM-MONEY2 | 2 | CLM (money engine, migration) | G: SL3-CLM-REVERIFY | deep (money) | 4.5 h | planned |
| SL4-BIL-RECV | 2 | BIL (receivables, migration) | G: SL3-BIL-REFUND | deep (money) | 3.5 h | planned |
| SL4-UI-BIL | 2 | web billing/receivables | contracts (live walk later) | light + visual | 2.5 h | planned |
| SL4-UI-CLM-REC | 2 | web claims/recoveries, claim file | G: SL3-UI-CLM | light + visual | 4 h | planned |
| SL4-CLM-RECOVERY-OPS | 3 | CLM (recoveries) | M: CLM-MONEY2 | deep (money) | 4 h | planned |
| SL4-CLM-FS-CASE | 3 | CLM (FS case) | M: CLM-MONEY2, MKT-FS | deep (money) | 4 h | planned |
| SL4-CLM-FS-STATEMENT | 3 | CLM (FS statement) | FS-CASE branch | deep (money) | 3.5 h | planned |
| SL4-BIL-FSOUT | 3 | BIL (FS_CLEARING out) | M: BIL-RECV | deep (money+security) | 3 h | planned |
| SL4-UI-CLM-FS | 3 | web claims/fs | G: SL3-UI-CLM | light + visual | 3.5 h | planned |
| SL4-CLM-PAYOPS | 4 | CLM (payment corrections) | M: RECOVERY-OPS, CMP-RECEIPT | deep (money) | 3.5 h | planned |
| SL4-BIL-DISBOPS | 4 | BIL (stop/void/return) | M: BIL-FSOUT | deep (money+security) | 3.5 h | planned |
| SL4-E2E | 5 | tests/e2e, seed-demo | all merged | light (integration) | 5 h | planned |
```
