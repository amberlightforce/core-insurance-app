# Brief SL3-CLM-REVERIFY — claim re-verification on policy changes (S1)

**Runs on Sonnet 5.5 (model: sonnet), D-USR-16.** Batch S1. Review: **deep (temporal)**. Estimate: 3.5 h.

- **WP / PRD:** SL3-CLM-REVERIFY.
  - Requirements:
    - REQ-CLM-002 (never mutate the claim when POL supersedes the segment; run re-verification);
    - REQ-CLM-057 (consume `PolicyChanged`, and later `TransactionReversed`; identify claims whose snapshot is
      superseded or whose policy changed with an effective date on or before the loss date; publish
      `ReverificationRequired` with old and new refs, **once per claim and cause**);
    - REQ-CLM-058 (subset: keep or adopt the new ref with a reason; adoption that removes cover goes through review);
    - REQ-POL-107 (G4 counterpart: POL never mutates claims).
  - Also consume `PolicyCancelled`: a loss on or after the cancellation effective time loses cover.
  - Sources: PRD-07 §5 CAP-CLM-02, §7 Claim entity (`snapshot_status`: Pending, Verified, ReverificationRequired);
    digest `orchestration/digests/PRD-07.md`.
  - Decisions: **D-SL3-03 (d)**, D-SL2-11 (f) (snapshot ref columns not frozen, because adoption updates them),
    D-SL2-01.
- **Scope:**
  1. **Handlers** for `PolicyChanged` and `PolicyCancelled` (outbox consumer, idempotent on event id, order-aware per
     aggregate, D-ARC-26). For each **open** claim on that policy whose loss instant is at or after the event's
     effective time:
     - call `pol.Snapshot.get(snapshotRef)` for the stored ref and read `supersession`;
     - if superseded:
       - insert a re-verification record (claim id, cause event id, old ref, new ref = `successorRef`) under a unique
         index (claim, cause);
       - set `snapshot_status = ReverificationRequired`;
       - publish `ReverificationRequired` through the outbox in the same transaction;
     - if not superseded: record nothing (log at debug, no personal data).
     - **Never change** the claim's ref, coverage, exposures or reserves in the handler.
  2. **`clm.Coverage.reverify(claimId, decision KEEP | ADOPT, reasonCode, expectedNewRef)`**, for Staff.ClaimsHandler:
     - **KEEP:** status back to Verified; the decision is recorded.
     - **ADOPT:**
       - the new ref must equal the open record's new ref, else `CLM-ERR-STALE`;
       - update the ref, and re-run the cover check for each exposure against the new snapshot;
       - an exposure whose coverage is gone → `coverageInQuestion = true`, and new payments on that exposure are
         refused (`CLM-ERR-COVERAGE-IN-QUESTION`) until a coverage decision clears it. The decision itself is out:
         clearing is a follow-up; record it.
     - Audited; an Idempotency-Key is required.
  3. The claim view exposes `snapshotStatus` and the open re-verification (old and new refs) for the UI (SL3-UI-CLM).
  4. **CLM migration:** you are the CLM migration owner (re-verification table with the unique index;
     `snapshot_status`).
- **Depends on / provides:**
  - **Depends on:** contracts for the events and `supersession`. Unit-test against the generated POL fake; the
    integration test against real POL needs **merged SL3-POL-TEMPORAL** (supersession) and SL3-POL-CHANGE (to cause a
    change). Until then, assert with the fake and mark the real-POL test to run after merging main.
  - **Provides:** the API for SL3-UI-CLM.
- **Files you own:** `src/CoreIns.Modules.Claims/Events/Policy*`, `Commands/Reverify*`, `Persistence/**` (migration
  owner), `tests/CoreIns.IntegrationTests/Claims/Reverify/**`.
- **Shared (append only):** the CLM permission file.
- **Tests:**
  - a duplicate delivery of the same event → exactly one `ReverificationRequired`;
  - two different causes → two;
  - a loss before the effective date → none;
  - superseded = false → none;
  - KEEP and ADOPT paths;
  - ADOPT with a stale `expectedNewRef` → 409;
  - ADOPT removing cover → payments refused;
  - a concurrent ADOPT and payment → no 500;
  - no personal data in the event;
  - the claim ref is untouched until ADOPT.
- **PITFALLS to self-check (likely):** 6 (no public endpoint may feed fake facts that clear `coverageInQuestion`), 12,
  13 (never pass a future knownAt; use refs as returned), 15, 22 (named arguments on the POL interface).
- **Workflow:** `briefs/sl3/_COMMON.md`: merge main, quick gate, push,
  `gh pr create --base main --title "SL3-CLM-REVERIFY: ReverificationRequired and keep/adopt"`, do not merge.
