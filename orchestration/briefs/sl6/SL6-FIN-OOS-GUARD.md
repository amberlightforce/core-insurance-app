Runs on Sonnet 5.5 (model: sonnet). Process: orchestration/briefs/LIGHT-WORKFLOW.md (push straight to main, no PR).

# Brief SL6-FIN-OOS-GUARD (wave 2, FIN, est. 3h)

**Depends on:** POL-OOS event shape; b-finv4 merged

**Goal:** Finance side of out-of-sequence: no postings into closed periods, correction deltas posted into the open period.

**Scope**
- Consume PolicyChanged delta sets (set_id, set_size, index); process only when complete; one correction journal per set.
- Guard G5 (REQ-POL-108): a journal dated in a locked period is rejected; the correction posts in the first open period with reference correctionOf.
- Same totals in NET and GROSS (REQ-FIN-297 capability): unit test only for GROSS.
- Provide `IFinancePeriodService.IsLocked(date)` (read-only) for POL-OOS; a dev-only endpoint to lock a month for E2E.

**Acceptance (main-path tests only)**
- Set of 3 deltas -> one balanced journal; locked month -> posted in the open period; replay no duplicate.

**Files owned:** src/CoreIns.Modules.Finance/Posting/Oos*, Domain/PostingRules.cs (edit), permissions fin.json

**Rules:** defaults D-SL6-01..14 in orchestration/SLICE-PLAN-6.md section 4 are binding; do not wait on open questions. Touch only your files plus append-only shared files (module registration, permissions json, routes, i18n, ci.yml, seed-demo.py). Edit only your module's contracts yaml and regenerate with ContractGen; if regeneration conflicts after merging origin/main, regenerate again. If an EF migration collides with another WP's, regenerate yours after merging. Leave `// TODO(hardening):` notes for edge cases and list them in the report. Regulatory values only with a legalStatus tag and PRD source; otherwise mark illustrative.
Final report under 30 lines, then remove your worktree.
