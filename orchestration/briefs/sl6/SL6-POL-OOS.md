Runs on Sonnet 5.5 (model: sonnet). Process: orchestration/briefs/LIGHT-WORKFLOW.md (push straight to main, no PR).

# Brief SL6-POL-OOS (wave 1, POL, est. 5h)

**Depends on:** -

**Goal:** Out-of-sequence mid-term change by reverse-and-reapply, with a locked finance period handled and downstream consumers told once.

**Scope**
- Op pol.Change.createOutOfSequence (or an earlier effectiveDate on pol.Change.create): reverse later segments, apply the new change, re-apply the later ones, in one transaction; publishes a delta set with set_id, set_size, index (D4). Push the event shape as your first commit.
- Mode NET default and final for P1-P2 (D-SL6-10); GROSS exists as a flag, unit test only.
- Rate with the term's pinned artefacts (REQ-POL-033); never re-rate under a different configuration.
- Locked finance month: ask `IFinancePeriodService.IsLocked(date)` (FIN-OOS-GUARD supplies; until then a stub returning false); POL emits the correction delta with correctionOf=<original txn> and never an event dated inside a locked month (G5, REQ-POL-108).
- CLM re-verification via the existing PolicyChanged consumer, raised once per claim per set.
- Table pol.oos_correction (set_id, original refs, mode, status).

**Acceptance (main-path tests only)**
- Integration: 3 sequential changes, insert an earlier one: final segment premiums equal the in-order result; delta set sums to the premium difference.
- One ReverificationRequired for the open claim, not per segment.

**Files owned:** src/CoreIns.Modules.Policy/Commands/Change/Oos/** (new), Domain/Servicing/Oos*, Api/OosController.cs, second POL migration (regenerate after merging POL-WITHDRAW), pol.yaml (Change ops only), events/pol (append)

**Rules:** defaults D-SL6-01..14 in orchestration/SLICE-PLAN-6.md section 4 are binding; do not wait on open questions. Touch only your files plus append-only shared files (module registration, permissions json, routes, i18n, ci.yml, seed-demo.py). Edit only your module's contracts yaml and regenerate with ContractGen; if regeneration conflicts after merging origin/main, regenerate again. If an EF migration collides with another WP's, regenerate yours after merging. Leave `// TODO(hardening):` notes for edge cases and list them in the report. Regulatory values only with a legalStatus tag and PRD source; otherwise mark illustrative.
Final report under 30 lines, then remove your worktree.
