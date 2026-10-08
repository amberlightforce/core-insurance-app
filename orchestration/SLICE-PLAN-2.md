# SLICE PLAN 2 — claims thin slice (E2E-02a: FNOL → coverage → reserve → payment → ledger)

**Approved by:** the user (D-USR-10, 2026-10-08). **Process:** as slice 1 (D-USR-04..09, D-PRG-15/17/18): Sonnet by
default; deep review on the strongest model for security, money and temporal code, **first round only**.

**Goal:** a staff claims handler registers an own-damage claim on an in-force motor policy. Cover is verified on the
policy snapshot valid at the loss date. A reserve is set within authority; a larger reserve and the payment are
referred and approved by a claims manager (maker ≠ checker). The payment goes to the insured's payee account through a
BIL disbursement (stub screening, stub VoP, stub bank). CLM publishes `PaymentIssued`, the remaining reserve is released,
the claim closes, and FIN shows balanced journals in which the claim-payment clearing account nets to zero. It runs on
`docker compose` and is proven by an automated E2E-02a test (API + Playwright) in CI.

Scope is cut from PRD-18 E2E-02 steps 1, 2, 4, 5 (FIN part), 8, 9 plus close (REQ-CLM-072/073). Everything else in E2E-02
(offer clocks, fiscal settlement receipt, recoveries, RI) is out (D-SL2-01).

## Batches (max 4 concurrent agents)

| Batch | WP | Scope | PRD / REQ source | Model | Review |
|---|---|---|---|---|---|
| S0 | **SL2-PLT** Approvals + claims roles + screening stub | PLT approval requests (maker-checker, content hash, checker ≠ maker, stale on hash change, `ApprovalDecided` event); CLM authority types registered with illustrative grants (D-SL2-03); dev users `claims` (Staff.ClaimsHandler) and `claimsmgr` (Staff.ClaimsManager); PTY `pty.Screening.screen` **stub** returning Clear, never bound in Production (D-SL2-05) | PRD-14 approvals/authority, PRD-01 REQ-PTY-006 (stub) | strongest | deep (security), 1st round |
| S0 | **SL2-POL-SNAP** Policy snapshot at loss date | `pol.Snapshot.get` (policy + segment valid at an instant, immutable ref, byte-identical re-read), policy lookup by number for FNOL (POST body, no P2 in URL) | REQ-POL-007, -014 (subset) | Sonnet | deep (temporal), 1st round |
| S0 | **SL2-BIL-DISB** Payee accounts + disbursement | `bil.PayeeAccount.create/get` (IBAN encrypted + blind index, masked last 4, VoP **stub** Match), `bil.Disbursement.request/get` for source `CLM_PAYMENT` (duplicate key, approval evidence + content hash, screening via PTY contract), **stub bank channel** Released → Issued → Cleared (never bound in Production), `Disbursement{Issued,Cleared}` events, sealed sub-ledger entries + `BillingEntryPosted` for disbursement facts | REQ-BIL-009, -197..214 (subset), -343..345 (subset) | strongest | deep (money), 1st round |
| S0 | **SL2-CLM-CORE** Claim reference vertical | `clm` schema; FNOL submit/get/validate (staff channel), claim number (gapless, D-SL2-07), claim/exposure/incident/claimant model and state machine (Draft → Open(New/InProgress) → Closed), coverage verification against the POL snapshot via the generated POL interface, `ClaimReported`, `CoverageVerified`, `ExposureCreated`, `ClaimClosed`, `clm.Claim.search` (POST), close guard | REQ-CLM-001, 002, 030..050, 061..075, 011 (subset) | strongest | deep (temporal + P2), 1st round |
| S1 | **SL2-CLM-MONEY** Claim financial engine | Reserve lines, append-only sealed `ClaimFinancialTransaction`s (D-ARC-34), transaction sets build/submit/approve/reject with per-transaction authority + PLT approvals, derived balances, payment within open reserve (top-up or reject), final payment releases remainder, payment → `bil.Disbursement.request`, consume `DisbursementIssued` → `PaymentIssued`, `ReserveChanged`, `TransactionSetApproved`, `clm.Financials.get(asOf)` | REQ-CLM-003, 004, 005, 093..118, 119..131 (subset) | strongest | deep (money), 1st round |
| S1 | **SL2-FIN-CLM** Claims postings | Posting rules as data for CLM `ReserveChanged`/`PaymentIssued` and BIL disbursement `BillingEntryPosted`; claim dimensions; claim-payment clearing nets to zero per payment (D-SL2-08) | PRD-09 GF-05 (subset), REQ-FIN-037 | strongest | deep (ledger), 1st round |
| S1 | **SL2-UI-CLM** Claims staff screens | FNOL (policy lookup, loss date, cause, description), claim view (coverage, exposures, financials), transaction-set builder (reserve / payment), approvals inbox for the manager, payee account capture (masked), close claim; Greek-first | PRD-07 §6 SCR-CLM-01..05 (subset) | Sonnet | light |
| S2 | **SL2-E2E** E2E-02a | API test + Playwright UI spec on the compose stack, CI job, `seed-demo.py` adds a closed claim, integration fixes | E2E-02 (cut) | Sonnet | light |

S1 builders code against the generated interfaces and fakes of the S0 modules; S1 starts once SL2-CLM-CORE is merged
(the CLM module pattern) and as soon as the other S0 contracts are typed.

## Rules for every slice-2 builder
- Everything in HANDOVER §4 applies: module pattern (`docs/module-pattern.md`), personal data, money, time, concurrency.
- Native Windows build and tests; Testcontainers for integration tests (D-ARC-30, D-USR-08).
- Amounts are EUR only: functional and group amounts equal the transaction amount with no FX rate (D-SL2-06).
- Authority limits, cost categories and the stub adapters are **illustrative / stub**, marked as such (D-SL2-03..05).
- The claim is **not bitemporal** (PRD-07 §7.0); it stores the POL snapshot reference.
