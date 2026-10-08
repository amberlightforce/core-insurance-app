# Brief SL4-E2E-HARNESS — e2e06 stack, helpers and CI job (wave 1)

Runs on Sonnet 5.5 (model: sonnet), D-USR-16.

Wave 1, **batched** (no own PR, D-USR-18; the orchestrator folds it into the SL4-CONTRACTS PR or a batch). Review:
light. Estimate: 1.5 h.

- **WP / PRD:** SL4-E2E-HARNESS. PRD-18 §17.1 (E2E harness), XMR-FR-250. Pattern: slice 3's `run-e2e03.sh` and its
  `e2e03` CI job (read both first).
- **Scope:**
  1. **`tests/e2e/run-e2e06.sh`** (committed executable: `git update-index --chmod=+x`, PITFALLS 32):
     - own compose project `coreins-e2e06`, ports **28000+**, image tag `coreins-host:e2e06`;
     - System clock (no time shift needed);
     - runs the specs `e2e02x*`, `e2e06*` and `payops*`, then `down -v`.
  2. **Helpers** in `tests/e2e/support/claims4/**` (new files only; do not edit existing helpers):
     - dev sign-in by **exact display name** for the new users (`recovery`, `riacct`, `rimgr`; names from SL4-PLT,
       guessed until it merges; keep them in one constants file) (PITFALLS 30);
     - `createAndApproveXolTreaty(...)`: `riacct` creates and submits, `rimgr` approves; returns the contract id;
     - `waitForJournalSet(expectedTypes[])` extended to RI recovery and FS clearing entry types (PITFALLS 29);
     - `postFsNotification(payload)` for the Development-only `/dev/fs-clearing/notifications` (D-SL4-18);
     - `stubBank(disbursementId, action)` for `/dev/bank/disbursements/{id}/{issue|clear|return|reject}` (D-SL4-13);
     - an IBAN-not-in-URL guard reused from slice 3.
  3. **CI:**
     - add a new job block `e2e06` to `.github/workflows/ci.yml`, the same shape as `e2e03`, running a placeholder
       health spec until SL4-E2E;
     - **check the `e2e02` job's spec filter** and make sure it cannot pick up `e2e02x*` specs (they need the RI and FS
       pieces);
     - do not touch any other job block.
- **Depends on / provides:**
  - **Depends on:** the contracts only for endpoint paths; start at once.
  - **Provides:** the runner and helpers for SL4-E2E.
- **Files you own:**
  - `tests/e2e/support/claims4/**`;
  - `tests/e2e/run-e2e06.sh`;
  - `tests/e2e/tests/e2e06-health.spec.ts` (placeholder);
  - the `e2e06` block of `ci.yml`.
- **PITFALLS to self-check (likely):**
  - 29, 30, 32, 33;
  - 35 (never the user's `coreins` stack; own project, `down -v`).
- **Workflow:** follow `briefs/sl4/_COMMON.md` (batched). Run the runner once locally against a fresh stack (health
  spec only), push your branch and report it. Do not open a PR.
