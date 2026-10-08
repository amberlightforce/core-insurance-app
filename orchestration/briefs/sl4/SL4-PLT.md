# Brief SL4-PLT — recovery/reinsurance roles, dev users, approval withdrawal (wave 2)

Runs on Sonnet 5.5 (model: sonnet), D-USR-16.

Wave 2, **own PR**. Review: **deep (security)**. Estimate: 2.5 h.

**Gate:** start only after **SL3-PLT-SUPPORT has merged**. It owns the dev-users file and `Program.cs` in slice 3. Run
`git merge origin/main` first and confirm it is in.

- **WP / PRD:**
  - PRD-14 REQ-PLT-004 (maker-checker; subset: withdrawal of an open approval by the module that created it);
  - the role catalogue (contract §2: ROLE-20 Recovery specialist, ROLE-27 Reinsurance manager, ROLE-28 Reinsurance
    accountant);
  - SoD rows PRD-07 §12 and PRD-08 §12.

  Decisions: **D-SL4-16**, D-SL4-14 (withdraw), D-SL2-03, D-USR-16. Digest: `orchestration/digests/PRD-14.md`.
- **Scope:**
  1. **Roles:** Staff.RecoverySpecialist, Staff.ReinsuranceAccountant, Staff.ReinsuranceManager in the PLT role
     catalogue, where Staff.BillingManager was added in slice 3.
  2. **Dev users** (append to `src/CoreIns.Host/dev-users.Development.json`; Development only):
     - `recovery` "Dev Recovery Specialist (synthetic)";
     - `riacct` "Dev Reinsurance Accountant (synthetic)";
     - `rimgr` "Dev Reinsurance Manager (synthetic)";
     - `superuser` gains the three roles. SoD still applies to it (maker ≠ checker by principal).
     - Display names must be unique and must not contain another user's name as a substring that Playwright could
       confuse (PITFALLS 30). Report the exact strings.
  3. **In-process approval withdrawal:**
     - `IPlatformApprovalService.WithdrawAsync(approvalRequestId, reason)` (typed by SL4-CONTRACTS).
     - Only the **module that created the request** may withdraw it: check the caller module identity the same way
       `verifyForExecution` binds the subject.
     - Only Open requests can be withdrawn: Decided → `PLT-ERR-STATE`, idempotent on repeat.
     - It removes the request from every inbox, audits it, and publishes `ApprovalDecided` with outcome `WITHDRAWN`.
     - **No REST endpoint** (PITFALLS 4, 6).
     - A concurrent decide and withdraw → exactly one wins, the other gets 409, never 500 (PITFALLS 15).
  4. Check that the existing approvals inbox filters out Withdrawn requests.
- **Not in scope:** authority **types and grants** are registered by the owning modules (`CLM.RECOVERY_WRITEOFF`,
  `CLM.FS_NET_SETTLEMENT` and the large-loss grants in SL4-CLM-MONEY2; `RI.CONTRACT_APPROVE` in SL4-RI-REGISTRY).
  Do not add them here.
- **Files you own:**
  - `src/CoreIns.Platform/Approvals/Withdraw*` (new) and the withdrawal path in the approval store;
  - the role catalogue entries;
  - `tests/CoreIns.Platform.Tests/Approvals/Withdraw*`, `tests/CoreIns.Host.Tests/DevUsers*` (extend).
- **Shared (append only):** `dev-users.Development.json`.
- **Tests:**
  - module A cannot withdraw module B's request;
  - a withdrawn request cannot be approved afterwards (409/state);
  - a decided request cannot be withdrawn;
  - double withdraw is idempotent;
  - withdraw racing decide;
  - the inbox excludes withdrawn requests;
  - the new dev users exist only in Development (the startup test of the dev sign-in still refuses Production);
  - superuser SoD: superuser as maker cannot approve its own request.
- **PITFALLS to self-check (likely):** 3, 4, 5, 6, 15, 30.
- **Workflow:** follow `briefs/sl4/_COMMON.md`. Merge main, run the quick gate, push, then run
  `gh pr create --base main --title "SL4-PLT: recovery/RI roles and approval withdrawal"`. Do not merge.
