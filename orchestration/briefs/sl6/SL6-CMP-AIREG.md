Runs on Sonnet 5.5 (model: sonnet). Process: orchestration/briefs/LIGHT-WORKFLOW.md (push straight to main, no PR).

# Brief SL6-CMP-AIREG (wave 2, CMP, est. 3h)

**Depends on:** PLT-AI interface; CMP-CLOCK merged (migration)

**Goal:** AI system register that decides which features may be enabled.

**Scope**
- Ops: cmp.AiSystem.register|submitForReview|approve|suspend|retire|get|list. Statuses DRAFT, IN_REVIEW, APPROVED, SUSPENDED, RETIRED; approver must differ from submitter.
- Seed pilot features as DRAFT: AI-CLM-01 FNOL assistant, AI-CLM-04 (bias-tested), AI-BIL-SUSPENSE, AI-PTY-DUP, AI-CHN-MCP, AI-UW-RT14 (use PRD ids if they differ). Dev-only seed endpoint approves them for E2E.
- Implement `IAiRegisterLookup` from the table; emit AiSystemStatusChanged on each change.
- Permission cmp.AiSystem.approve for Staff.ComplianceOfficer (add dev user `dpo` if missing).

**Acceptance (main-path tests only)**
- Approve and suspend emit events. PLT-AI enable refused for DRAFT, allowed for APPROVED (cross-module test via the interface).

**Files owned:** src/CoreIns.Modules.Compliance/AiRegister/** (new), migration (after CMP-CLOCK), cmp.yaml (AiSystem ops), events/cmp AiSystemStatusChanged

**Rules:** defaults D-SL6-01..14 in orchestration/SLICE-PLAN-6.md section 4 are binding; do not wait on open questions. Touch only your files plus append-only shared files (module registration, permissions json, routes, i18n, ci.yml, seed-demo.py). Edit only your module's contracts yaml and regenerate with ContractGen; if regeneration conflicts after merging origin/main, regenerate again. If an EF migration collides with another WP's, regenerate yours after merging. Leave `// TODO(hardening):` notes for edge cases and list them in the report. Regulatory values only with a legalStatus tag and PRD source; otherwise mark illustrative.
Final report under 30 lines, then remove your worktree.
