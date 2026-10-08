# Brief SL3-E2E-HARNESS — e2e03 runner, time-shift helpers, CI job (S0)

**Runs on Sonnet 5.5 (model: sonnet), D-USR-16.** Batch S0. Review: light. Estimate: 2 h.

- **WP / PRD:** SL3-E2E-HARNESS. Sources: XMR-FR-250 and PRD-18 §17.1 conventions (time-shifted environment; ledger
  assertions); digest `orchestration/digests/PRD-18-B.md` §4.1. Decisions: D-SL3-12, D-USR-15, D-PRG-19. Copy the
  pattern of `tests/e2e/run-e2e02.sh` and the `e2e02` CI job.
- **Scope:**
  1. `tests/e2e/run-e2e03.sh`:
     - compose project `coreins-e2e03`, image tag `coreins-host:e2e03`, host ports **27000+** (clear of 25000 user,
       26000 e2e01, 26500 e2e02);
     - its own env file with `Platform__Time__Mode=Shiftable` for api and worker;
     - runs the specs matching `e2e03*`, `e2e04*` and `change*` (API then UI), then `down -v`;
     - `KEEP_STACK=1` and `SKIP_BUILD=1` as in e2e02;
     - **committed executable** (`git update-index --chmod=+x`).
  2. Helpers in `tests/e2e/tests/support/` (or wherever the existing support lives):
     - `advanceClock(request, {days, hours})`, calling `POST /dev/clock/advance` as `admin` (endpoint by
       SL3-PLT-SUPPORT; code it against the documented shape);
     - `signInAs(page|request, exactDisplayName)`, which selects by **exact** display name (PITFALLS 30);
     - `waitForJournals(policyNumber, expectedEntryTypes[])`, which waits for the **complete** expected set
       (PITFALLS 29);
     - `assertNoIbanInUrls(page)`;
     - `alive(page)` reuse.
  3. A placeholder spec `e2e03-health.spec.ts` that brings the stack up, checks `/health` and `GET /dev/clock`, and
     advances 1 day. It shows the harness works before the journeys exist.
  4. `.github/workflows/ci.yml`: a new job `e2e03`, the same shape as `e2e02`, running `tests/e2e/run-e2e03.sh`. **You
     own `ci.yml` in slice 3**; SL3-E2E later edits only the `e2e03` block.
- **Depends on / provides:**
  - **Depends on:** contracts only (the dev clock endpoint shape in SL3-PLT-SUPPORT's brief). Until PLT-SUPPORT merges,
    the placeholder spec may skip the advance step with a clear `test.skip` reason; remove the skip after merging main.
  - **Provides:** the harness SL3-E2E fills.
- **Files you own:** `tests/e2e/run-e2e03.sh`, `tests/e2e/stack/**` (e2e03 env), `tests/e2e/tests/support/**` (new
  helpers; existing helpers append only), `tests/e2e/tests/e2e03-health.spec.ts`, `.github/workflows/ci.yml` (`e2e03`
  job).
- **PITFALLS to self-check (likely):** 29, 30, 32 (executable bit), 33, 35 (never touch `coreins`; down -v).
- **Workflow:** `briefs/sl3/_COMMON.md`. Quick gate: `npm run format:check`, lint and typecheck in `tests/e2e`; run
  `tests/e2e/run-e2e03.sh` once locally if the machine is not saturated (otherwise let CI run it). Then push,
  `gh pr create --base main --title "SL3-E2E-HARNESS: e2e03 runner, time-shift helpers, CI job"`, do not merge.
