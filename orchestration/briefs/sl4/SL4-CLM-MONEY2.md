# Brief SL4-CLM-MONEY2 — claim money engine extension, slice-4 CLM schema, slice-2 follow-ups (wave 2)

Runs on Sonnet 5.5 (model: sonnet), D-USR-16.

Wave 2, **own PR**. Review: **deep (money)**. Estimate: 4.5 h.

**Gate:** start only after **SL3-CLM-REVERIFY has merged**. It owns `Modules.Claims/Persistence/**` and the CLM
migration in slice 3. Run `git merge origin/main` and confirm its migration is in before you add yours.

You are the **CLM migration owner for the whole of slice 4**. Later CLM WPs (RECOVERY-OPS, FS-CASE, FS-STATEMENT,
PAYOPS) add no migrations, so your schema must cover them. Read their rows in `SLICE-PLAN-4.md` §5 before you design it.

- **WP / PRD:**
  - REQ-CLM-095 / -096 (derived balances incl. recoveries, open recovery reserve, net incurred);
  - REQ-CLM-097 (unchanged);
  - REQ-CLM-102 (recovery reserves with reason and approval rules);
  - REQ-CLM-107…-113 (sets, authority, approval, stale);
  - REQ-CLM-150 (write-off authority, engine part);
  - REQ-CLM-072/073 (close guard with recoveries: an open recovery keeps the claim Open/Settled; recovery reserves
    may stay open on a closed exposure);
  - BR-CLM-008/010.

  Sources: `orchestration/digests/PRD-07.md` §3 (ClaimFinancialTransaction kinds, Recovery, FsCase, FsStatement,
  ClaimPayment method), §11 (authority types), §15 (hardest part 1). Decisions: **D-SL4-06, -07, -08, -12, -13, -14**,
  D-SL2-03, D-SL2-13 (aggregate authority, one approval per referred authority, SET_STALE → REJECTED).
- **Scope:**
  1. **Schema** (one migration):
     - `TransactionKind` gains `RecoveryReserve` and `Recovery`. Transactions keep their sealed, append-only rows.
     - A **recovery** table (claim, exposure?, type SUBROGATION | SALVAGE | FRIENDLY_SETTLEMENT, counterparty party id,
       expected amount, status Open → Demanded → Agreed → Closed / WrittenOff, milestones jsonb, salvage fields:
       estimate, buyer party, sale price; receivable id; `record_version`).
     - **liability facts** columns on the claim (D-SL4-17).
     - The **FS case** table (role, counterparty insurer, eligibility result + rule id/version/legalStatus/provisional,
       clearing value, clearing reference, status) and the **FS statement** table and **lines** (period, counterparty,
       statement id, line direction/amount/clearing ref, match status, net, approval request id, BIL disbursement or
       receivable ref, status Pending/Requested/Settled/Rejected).
     - Payment columns `method` (incl. `CLEARING`), `fs_statement_id`, `reissue_of`, `reversal_of`, `fiscal_mark`, and
       a `statutory_clocks` flag on exposures.
     - The unique and check constraints that the later WPs need (one open FS case per claim and role; one statement per
       period × counterparty; a payment may be reissued once per original).
  2. **Engine:**
     - `BuildTransactionSet` accepts `RECOVERY_RESERVE` lines bound to a recovery (and its exposure/line), with a
       mandatory reason (`CLM-ERR-RESERVE-REASON`).
     - Derived balances follow REQ-CLM-096 per line, exposure and claim; `clm.Financials.get(asOf)` returns them.
     - Payment method `CLEARING` never calls BIL on submit/approve: the payment stays Approved and eroding until
       FS-STATEMENT settles it.
  3. **Authority** (PITFALLS 1, 2):
     - a recovery-reserve increase is checked under `CLM.RESERVE` on the resulting **open recovery reserve of the
       exposure**;
     - register `CLM.RECOVERY_WRITEOFF` and `CLM.FS_NET_SETTLEMENT` with the illustrative grants of D-SL4-07/-12, in
       `Authority/ClaimsAuthorityTypes.cs`;
     - **large-loss grants (D-SL4-08):** Staff.ClaimsManager `CLM.RESERVE`/`CLM.PAYMENT` up to 1,000,000.00
       (illustrative), DENY above;
     - **BR-CLM-010:** any referred amount > 50,000.00 needs an approver ≠ maker, even if the maker could self-approve.
       Update the slice-2 tests that asserted DENY above 50,000.00.
  4. **In-process system sets:** `IClaimFinancialEngine.SubmitSystemSetAsync(claimId, lines, evidence)` for the later
     WPs:
     - `evidence` = { kind BIL_ALLOCATION | FS_NOTIFICATION | FS_STATEMENT_LINE | DISBURSEMENT_OUTCOME, ref };
     - maker = the system principal, with the evidence stored on the set and audited;
     - amounts come only from the evidence, never from a client (PITFALLS 7);
     - Recovery transactions need no authority (D-SL4-07);
     - **payments and reserves still go through authority**, and a referred system set waits for a human approver
       (it never self-approves);
     - idempotent on the evidence ref: a unique index, so a replay creates no second set.
  5. **Follow-ups (D-SL4-14):**
     - `clm.TransactionSet.list(claimId)`;
     - the dry-run returns `authorityPreview[]` (type, cost type, computed aggregate amount, WITHIN/REFER/DENY,
       required role), computed by **the same code path** as submit;
     - typed approval `diff`;
     - close guard returns `errors[]` with codes and exposure ids;
     - when a set becomes REJECTED (decision or SET_STALE), call PLT `WithdrawAsync` for every other open approval of
       that set. Use the generated fake until SL4-PLT merges, then the real one.
  6. **Events** (always set; PITFALLS 12):
     - `ReserveChanged` for recovery reserves (kind `RECOVERY_RESERVE`, `recoveryId`);
     - `RecoveryRecorded` from approved Recovery transactions (lines per reserve line/exposure/cost type, counterparty,
       receivable id or FS statement id);
     - `TransactionSetApproved` unchanged.
- **Files you own:**
  - `src/CoreIns.Modules.Claims/Domain/Financials.cs`, `Domain/ClaimFinancialGuard.cs`;
  - `Commands/BuildTransactionSet.cs`, `SubmitTransactionSet.cs`, `ApplyApprovalDecision.cs`, `CloseClaim.cs`,
    `FinancialSupport.cs`;
  - `Queries/FinancialsReader.cs`, `Services/ClaimFinancialEngine.cs` (new), `Authority/**`;
  - **`Persistence/**`** (the migration);
  - `Api/FinancialsControllers.cs` (set list);
  - `tests/CoreIns.IntegrationTests/Claims/Money2/**`.
- **Shared (append only):** `ClaimsModule.cs`, the CLM permission file.
- **Not yours:**
  - recovery case commands and `CashAllocated` (RECOVERY-OPS);
  - FS commands (FS-CASE / FS-STATEMENT);
  - void/stop/reissue and `RecordDisbursementOutcome.cs` (PAYOPS);
  - `SubmitFnol.cs` / `CreateExposure.cs` (RECOVERY-OPS).
- **Tests:**
  - net incurred per REQ-CLM-096's example;
  - a recovery reserve split into 20 small lines still refers or denies on the total (PITFALLS 1);
  - a set mixing a reserve and a recovery reserve needs one approval per referred authority;
  - a 300,000 reserve refers to ClaimsManager; 1,000,000.01 is denied; 60,000 made by a manager still needs a second
    approver;
  - a `CLEARING` payment never reaches BIL;
  - a system set replayed with the same evidence → one set;
  - a system set with a client-supplied amount is impossible (no API path);
  - the dry-run preview equals what submit decides;
  - a rejected set withdraws its sibling approvals;
  - close guard with an open recovery → `errors[]`;
  - concurrent submits → 409, not 500;
  - app role cannot UPDATE transactions;
  - E2E-02a (slice 2) still passes (`run-e2e02.sh` once before the PR).
- **PITFALLS to self-check (likely):** 1, 2, 3, 4, 5, 7, 8, 10, 12, 15, 22.
- **Workflow:** follow `briefs/sl4/_COMMON.md`. Merge main, run the quick gate, push, then run
  `gh pr create --base main --title "SL4-CLM-MONEY2: recovery kinds, CLEARING, system sets, slice-4 CLM schema"`. Do
  not merge.
