# Brief SL3-POL-TEMPORAL — POL record-time watermark, policy freeze, supersession, slice-3 POL schema (S0)

**Runs on Sonnet 5.5 (model: sonnet), D-USR-16.** Batch S0. Review: **deep (temporal)**, adversarial, with probe
tests. Estimate: 4 h. **Critical path.**

- **WP / PRD:** SL3-POL-TEMPORAL. `SLICE-PLAN-3.md` §4 (a)(b)(c) and D-SL3-03 are binding; DECISIONS D-SL2-09 is the
  problem statement.
  - Requirements: REQ-POL-002 (as-of reads), REQ-POL-007 (snapshot stability and supersession), REQ-POL-079 (subset:
    DB invariants), REQ-POL-086, REQ-POL-134 (transition checked in the same transaction).
  - PRD: `core-insurance-prds/PRD-05-policy-administration-transactions.md` §5.3, §7.0–7.3; digest
    `orchestration/digests/PRD-05.md` §3, §15.
- **Scope:**
  1. **Watermark and lock-then-stamp.**
     - Add `pol.policy.last_recorded_at timestamptz not null`, backfilled from the latest row of each policy.
     - Add a helper in `Commands/JobSupport.cs` (e.g. `PolicyWriteLock.AcquireAsync(policyId)`). It runs
       `SELECT … FOR UPDATE` on `pol.policy`, then returns the record time
       `t = max(IClock.Now, last_recorded_at + 1 µs)`, truncated to microseconds, and sets `last_recorded_at = t` in
       the same transaction.
     - Move the existing bind path onto it. New policies set the watermark on insert. Every row a command writes uses
       that single `t`.
     - Racing writers on one policy: the loser gets `POL-ERR-STALE` (409) or waits; never a 500.
  2. **Reads clamp knownAt.** `PolicyReader`, `PolicySnapshots`, `pol.Policy.get`, `pol.Term.get/timeline` and search
     use **effective knownAt = min(requested or now, committed last_recorded_at)** and return it as
     `effectiveKnownAt`. A snapshot ref whose knownAt is greater than the current watermark is refused as malformed
     (forged). This replaces the future-knownAt check, which must keep refusing.
  3. **Freeze `pol.policy`.** Add a BEFORE UPDATE trigger that refuses any change to a content column (policy number,
     legal entity, jurisdiction, product code, policyholder, account, `recorded_at`, `created_by`). Only
     `last_recorded_at` and `record_version` may change. DELETE is refused. Prove it with a test **as the app role**.
  4. **Supersession** on `pol.Snapshot.get`: `supersession { superseded, successorRef, supersededAt }`.
     - It is computed live by comparing the content hash at (validAt, current watermark) with the hash of the ref's
       content.
     - A different segment id with identical content is **not** superseded.
     - `content` and `contentHash` stay byte-identical on every re-read.
  5. **The whole slice-3 POL schema, in one migration.** You are the only POL migration owner for slice 3; the S1 POL
     WPs add none.
     - Job columns: `cancellation_source`, `cancellation_kind` (Standard/Flat), `refund_method`, `reason_code`,
       `base_transaction_id`, `expiring_term_id`, `acceptance_channel`, `accepted_at`, `accepted_by`, `sub_state`.
     - Term: `predecessor_term_id`, plus the `Cancelled` state and `cancelled_at` on term versions.
     - Charge line: `transaction_kind`, `cancellation_source`, `treatment_rule_id`, `treatment_rule_version`.
     - Partial unique indexes: one open PolicyChange, one open Cancellation and one open Renewal job per term (open =
       Draft/Quoted/Scheduled).
     - Codes in `Domain/Codes.cs`: job types PolicyChange/Cancellation/Renewal; their states and sub-states
       (`Quoted.Offered`); transaction kinds; cancellation sources (from the MKT code list); error codes incl.
       `POL-ERR-OUT-OF-SEQUENCE`.
     - Check the list against SL3-POL-ENGINE, -CHANGE, -CANCEL and -RENEW in `SLICE-PLAN-3.md` §5. A column they need
       later means a follow-up migration through you.
  6. Append a short "Record time and the policy watermark" section to `docs/module-pattern.md`.
- **Depends on / provides:**
  - **Depends on:** SL3-CONTRACTS for `effectiveKnownAt` and `supersession`. Start with items 1, 3 and 5; wire item 2
    and the item 4 response fields after `git merge main` once CONTRACTS lands.
  - **Provides:** the lock helper and the schema that SL3-POL-CHANGE, -CANCEL and -RENEW build on, and the supersession
    that SL3-CLM-REVERIFY reads.
- **Files you own:** `src/CoreIns.Modules.Policy/Persistence/**`, `Queries/**`, `Commands/JobSupport.cs`,
  `Domain/Codes.cs`, `PolicyOptions.cs`, `tests/CoreIns.IntegrationTests/Policy/Temporal/**` and
  `docs/module-pattern.md` (append only).
- **Do not touch:** `Domain/Servicing/**` (SL3-POL-ENGINE is working there in parallel) or other modules.
- **Tests (each a real DB test unless pure):**
  - two concurrent writers on one policy: strictly increasing record times, no 500;
  - a reader during an uncommitted write, then re-read by ref after the commit: byte-identical;
  - a writer whose clock is behind the watermark still stamps above it (inject a fake IClock);
  - a ref with knownAt above the watermark is refused;
  - the freeze trigger works as the app role;
  - supersession: false after a split with identical content, true after a content change;
  - the partial unique indexes reject a second open job;
  - existing POL and E2E-02 snapshot tests stay green.
- **PITFALLS to self-check (likely):** 13 (no future knownAt from any input), 14 (Athens end-of-day for a date-form
  validAt; DST), 15 (409, never 500), 17 (never close or rewrite record periods retroactively), 22, 24, 34.
- **Workflow:** `briefs/sl3/_COMMON.md`: merge main, quick gate (build, `--filter` your Policy/Temporal classes and the
  existing PolicySnapshot tests, ContractGen `--check`), push,
  `gh pr create --base main --title "SL3-POL-TEMPORAL: POL watermark, policy freeze, supersession, slice-3 schema"`,
  do not merge.
