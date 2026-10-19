Runs on Sonnet 5.5 (model: sonnet). Process: orchestration/briefs/LIGHT-WORKFLOW.md (push straight to main, no PR).

# Brief SL6-BIL-RESPREAD (wave 3, BIL, est. 3h)

**Depends on:** POL-OOS; FIN-OOS-GUARD

**Goal:** Re-spread instalments on a complete delta set from an out-of-sequence change.

**Scope**
- Consume PolicyChanged delta sets (when complete); recompute the schedule for unpaid instalments only; paid instalments untouched; the difference goes to the next unpaid instalment or a credit line.
- Idempotent per set_id; one billing correction per set; no notifications.

**Acceptance (main-path tests only)**
- Paid instalment unchanged; schedule sum equals new premium; replay no-op.

**Files owned:** src/CoreIns.Modules.Billing/Commands/Respread*, Events/PolicyChangeHandlers.cs (append)

**Rules:** defaults D-SL6-01..14 in orchestration/SLICE-PLAN-6.md section 4 are binding; do not wait on open questions. Touch only your files plus append-only shared files (module registration, permissions json, routes, i18n, ci.yml, seed-demo.py). Edit only your module's contracts yaml and regenerate with ContractGen; if regeneration conflicts after merging origin/main, regenerate again. If an EF migration collides with another WP's, regenerate yours after merging. Leave `// TODO(hardening):` notes for edge cases and list them in the report. Regulatory values only with a legalStatus tag and PRD source; otherwise mark illustrative.
Final report under 30 lines, then remove your worktree.
