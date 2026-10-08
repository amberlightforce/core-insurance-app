# Brief SL3-POL-CHANGE — in-sequence mid-term policy change (S1)

**Runs on Sonnet 5.5 (model: sonnet), D-USR-16.** Batch S1. Review: **deep (temporal + money)**. Estimate: 4 h.

- **WP / PRD:** SL3-POL-CHANGE.
  - Requirements:
    - REQ-POL-190 (start a change; effective date default now);
    - REQ-POL-191 (subset: vehicle rating-field edits);
    - REQ-POL-192 (preview before commit), REQ-POL-193 (diff), REQ-POL-195 (replace vehicle as one change),
      REQ-POL-197 (`PolicyChanged`);
    - REQ-POL-104 (G1: nothing at or after a bound cancellation → `POL-ERR-AFTER-CANCELLATION`);
    - REQ-POL-008, -135, -136, -138 (effective-date limits: change 0 days back for a CSR, 30 days for
      Staff.Underwriter, PRD-05 §10.2 defaults, illustrative);
    - REQ-POL-093 (ENDORSEMENT rating under the pinned artefact);
    - REQ-POL-005, -119, -122, -129 (deltas only on bind, never from a dry run);
    - REQ-POL-004/-134 (transition table in the same transaction).
  - Sources: PRD-05 §5.5 and §6 SCR-POL-13; digest `orchestration/digests/PRD-05.md`.
  - Decisions: D-SL3-02 (in-sequence only), D-SL3-03, D-SL3-04, D-SL3-05, D-SL3-11.
- **Scope:**
  1. `pol.PolicyChange.create` creates a PolicyChange job (Draft) on the term valid at the effective date, with
     `base_transaction_id` = term head. Refusals:
     - the term is not InForce/Scheduled → `POL-ERR-ILLEGAL-TRANSITION`;
     - outside the date limits → `POL-ERR-EFFDATE-LIMIT`; the response carries the permitted range (REQ-POL-136);
     - effective time earlier than the latest bound transaction's effective time → `POL-ERR-OUT-OF-SEQUENCE`;
     - a second open change on the term → 409 (partial unique index).
  2. Reuse `pol.Job.updateDraft`, `quote` and `bind` for change jobs:
     - **Draft:** edit the vehicle's rating fields, or replace the vehicle (new locator; MTPL stays).
     - **Quote:** RAT ENDORSEMENT mode for the post-change risk → SL3-POL-ENGINE `Change` intent → RAT servicing tax
       lines (debit → `APPLY`, credit → `KEEP_NOT_REDUCED` provisional) → `servicingPreview` plus a diff (element,
       field, before, after) grouped by section. The dry run follows exactly the same path (POL P8).
     - **Bind:**
       - take the policy lock (SL3-POL-TEMPORAL helper);
       - re-check base = head (`POL-ERR-PREEMPTED`), G1 and the in-sequence rule under the lock;
       - supersede segments: close the record period of the replaced current segments at the single record time `t`,
         and insert the new segments;
       - write the transaction (kind Change), the charge lines (`transaction_kind`, treatment rule id/version,
         `legalStatus`, `provisional`) and the outbox events in one transaction.
     - **Events:** `PolicyChanged` (effective date, changed locators, vehicle added/removed facts) and
       `ChargeDeltaEmitted` with the set fields. A no-premium change emits zero deltas and still emits
       `PolicyChanged` (REQ-POL-202 behaviour).
  3. Register your services in `PolicyModule.cs` (one appended block) and add `Api/ChangeController.cs`. Add the
     permissions `pol.PolicyChange.create` and `pol.change` for Staff.Underwriter in the POL permission file (append).
- **Depends on / provides:**
  - **Depends on:** **merged** SL3-POL-TEMPORAL and SL3-POL-ENGINE (you may start on their branches when the
    orchestrator says so, then merge main); contracts for RAT and MKT (use the fakes until SL3-RAT-PRORATE and
    SL3-MKT-TREATMENT merge, then run against the real ones).
  - **Provides:** `PolicyChanged`/deltas for BIL, FIN and CLM; the API for SL3-UI-POL-JOBS.
- **Files you own:** `src/CoreIns.Modules.Policy/Commands/Change/**`, `Api/ChangeController.cs`,
  `tests/CoreIns.IntegrationTests/Policy/Change/**`.
- **Shared (append only):** `PolicyModule.cs` and the POL permission file.
- **Do not touch:** `Persistence/**` (no migrations: ask the orchestrator for any schema need), `Queries/**`,
  `Domain/Servicing/**`, or the Cancellation/Renewal folders (two other builders work there now).
- **Tests:**
  - a change at day 200 (dev clock or a fake IClock): preview = bind amounts; one delta per element × charge type;
    Σ deltas per term = cumulative written; the old segment is still readable at the old knownAt;
  - the snapshot at an earlier loss date is not superseded, and at a later date it is;
  - debit IPT `APPLY`; credit IPT 0.00 `KEEP_NOT_REDUCED` provisional;
  - refusals: out-of-sequence, after cancellation, date limits (CSR vs underwriter), a second open change, a preempted
    bind (two changes racing → one wins, the other 409, never 500);
  - no deltas, numbers or rows from a dry run;
  - no plate or personal data in events.
- **PITFALLS to self-check (likely):** 10, 11, 12 (`transactionKind` and treatment fields always set), 13, 14, 15, 17,
  18, 21, 22.
- **Workflow:** `briefs/sl3/_COMMON.md`: merge main, quick gate (build, `--filter` Policy.Change plus the existing
  Policy bind tests), push, `gh pr create --base main --title "SL3-POL-CHANGE: in-sequence mid-term change"`, do not
  merge.
