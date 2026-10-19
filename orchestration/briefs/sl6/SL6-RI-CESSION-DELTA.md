Runs on Sonnet 5.5 (model: sonnet). Process: orchestration/briefs/LIGHT-WORKFLOW.md (push straight to main, no PR).

# Brief SL6-RI-CESSION-DELTA (wave 2, RI, est. 3.5h)

**Depends on:** POL-OOS event shape; SL4-RI-RECOVERY merged

**Goal:** Cession target minus booked for premium delta sets, quota share first.

**Scope**
- Consume PolicyChanged delta sets; for a P2 home quota-share treaty: target cession = share x written premium after the set, minus booked cessions already recorded for the term; record the difference once per set_id.
- Motor XoL: no premium cession effect; provide `ri.Tracker.openingAggregate` idempotent on (treaty, year) for MIG-CLM-RI-IMPORT.
- NET booked only; GROSS capability unit test (D-SL6-10).
- Anything the treaty registry does not know -> RI exception row, never a guess.

**Acceptance (main-path tests only)**
- Booked + difference == target after OOS; replay no change; opening aggregate twice -> one tracker.

**Files owned:** src/CoreIns.Modules.Reinsurance/Cession/** (new), ReinsuranceModule.cs (DI append), RI migration if needed (owner)

**Rules:** defaults D-SL6-01..14 in orchestration/SLICE-PLAN-6.md section 4 are binding; do not wait on open questions. Touch only your files plus append-only shared files (module registration, permissions json, routes, i18n, ci.yml, seed-demo.py). Edit only your module's contracts yaml and regenerate with ContractGen; if regeneration conflicts after merging origin/main, regenerate again. If an EF migration collides with another WP's, regenerate yours after merging. Leave `// TODO(hardening):` notes for edge cases and list them in the report. Regulatory values only with a legalStatus tag and PRD source; otherwise mark illustrative.
Final report under 30 lines, then remove your worktree.
