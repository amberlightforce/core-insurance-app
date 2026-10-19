Runs on Sonnet 5.5 (model: sonnet). Process: orchestration/briefs/LIGHT-WORKFLOW.md (push straight to main, no PR).

# Brief SL6-MIG-CLM-RI-IMPORT (wave 3, CLM, RI, CMP, est. 4h)

**Depends on:** MIG-FRAME; CMP-CLOCK; RI-CESSION-DELTA

**Goal:** Import an open claim with its running clock, treaty-year aggregates, bureau handover date and fiscal series continuity.

**Scope**
- CLM importer: open BI claim (FNOL data, reserve as given, original notified date) linked to the imported policy by xref; origin=MIGRATION.
- cmp.Clock.import re-creates the running clock from its original start so the deadline is unchanged (CLM-010, CMP-009).
- RI: running motor XoL year with opening aggregates via ri.Tracker.openingAggregate.
- CMP: bureau handover date per vehicle stored (record only, no transmission); fiscal series continuity record with last legacy number and legacy MARKs kept for credit lookups.
- Reversers: claim only if no payments or reserve changes since import.

**Acceptance (main-path tests only)**
- Claim deadline equals the original; double import one tracker, one claim; reverse blocked after a payment.

**Files owned:** src/CoreIns.Modules.Claims/Migration/** (new), CMP cmp.Clock.import + cmp.Bureau.handover (small additions), RI importer file

**Rules:** defaults D-SL6-01..14 in orchestration/SLICE-PLAN-6.md section 4 are binding; do not wait on open questions. Touch only your files plus append-only shared files (module registration, permissions json, routes, i18n, ci.yml, seed-demo.py). Edit only your module's contracts yaml and regenerate with ContractGen; if regeneration conflicts after merging origin/main, regenerate again. If an EF migration collides with another WP's, regenerate yours after merging. Leave `// TODO(hardening):` notes for edge cases and list them in the report. Regulatory values only with a legalStatus tag and PRD source; otherwise mark illustrative.
Final report under 30 lines, then remove your worktree.
