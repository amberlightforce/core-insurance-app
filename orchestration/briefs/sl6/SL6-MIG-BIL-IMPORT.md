Runs on Sonnet 5.5 (model: sonnet). Process: orchestration/briefs/LIGHT-WORKFLOW.md (push straight to main, no PR).

# Brief SL6-MIG-BIL-IMPORT (wave 2, BIL, FIN, est. 4.5h)

**Depends on:** MIG-FRAME; BIL-DELINQ merged (BIL migration); FIN-OOS-GUARD merged (FIN migration)

**Goal:** Import opening receivables, SEPA mandate and opening finance balances.

**Scope**
- BIL importer: opening receivable per invoice with legacy outstanding to the cent (sum check against payload), mandate with original reference and signature date, origin=MIGRATION. Approval by a different user (MIG-005, MIG-FRAME mechanism).
- FIN importer: opening-balance journal origin=MIGRATION, reconciled to the payload's legacy trial-balance totals (difference must be 0.00 else refuse); no revenue recognition, nothing treated as new business.
- Reversers per D-SL6-05: only when nothing was posted or collected against the opening balance.

**Acceptance (main-path tests only)**
- Opening receivable equals legacy outstanding; FIN reconciliation 0.00; double import no-op; mismatch refused.

**Files owned:** src/CoreIns.Modules.Billing/Migration/** (new), src/CoreIns.Modules.Finance/Migration/** (new), migrations after the two predecessors

**Rules:** defaults D-SL6-01..14 in orchestration/SLICE-PLAN-6.md section 4 are binding; do not wait on open questions. Touch only your files plus append-only shared files (module registration, permissions json, routes, i18n, ci.yml, seed-demo.py). Edit only your module's contracts yaml and regenerate with ContractGen; if regeneration conflicts after merging origin/main, regenerate again. If an EF migration collides with another WP's, regenerate yours after merging. Leave `// TODO(hardening):` notes for edge cases and list them in the report. Regulatory values only with a legalStatus tag and PRD source; otherwise mark illustrative.
Final report under 30 lines, then remove your worktree.
