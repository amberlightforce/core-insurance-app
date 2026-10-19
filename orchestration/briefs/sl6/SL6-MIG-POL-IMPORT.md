Runs on Sonnet 5.5 (model: sonnet). Process: orchestration/briefs/LIGHT-WORKFLOW.md (push straight to main, no PR).

# Brief SL6-MIG-POL-IMPORT (wave 2, POL, est. 4.5h)

**Depends on:** MIG-FRAME; POL-OOS merged (migration)

**Goal:** Import an in-force policy: opening transaction (mid-term) or imported expiring term for renewal-based conversion.

**Scope**
- Implement IMigrationImporter/IMigrationReverser for POLICY: legacy number, term dates, risk (vehicle, two drivers), written premium per element x charge type, IPT/levy, status.
- (a) mid-term: transaction type OPENING with deltas flagged opening=true; BIL/FIN treat them as opening balances, not new business. (b) renewal-based: term imported as expiring data, renewal job reaches Quoted.Offered via the existing renewal command.
- origin=MIGRATION, baselineHash from MIG-FRAME; imported premium taken as given, no re-rating.
- Reverser: remove the imported policy only if no transaction other than OPENING exists (D-SL6-05).
- Idempotent via xref.

**Acceptance (main-path tests only)**
- Import twice -> one policy; opening deltas flagged; renewal job offered; reverse blocked after an endorsement.

**Files owned:** src/CoreIns.Modules.Policy/Migration/** (new), migration (regenerate), pol.yaml (pol.*.import ops), events/pol

**Rules:** defaults D-SL6-01..14 in orchestration/SLICE-PLAN-6.md section 4 are binding; do not wait on open questions. Touch only your files plus append-only shared files (module registration, permissions json, routes, i18n, ci.yml, seed-demo.py). Edit only your module's contracts yaml and regenerate with ContractGen; if regeneration conflicts after merging origin/main, regenerate again. If an EF migration collides with another WP's, regenerate yours after merging. Leave `// TODO(hardening):` notes for edge cases and list them in the report. Regulatory values only with a legalStatus tag and PRD source; otherwise mark illustrative.
Final report under 30 lines, then remove your worktree.
