Runs on Sonnet 5.5 (model: sonnet). Process: orchestration/briefs/LIGHT-WORKFLOW.md (push straight to main, no PR).

# Brief SL6-UI-PLT-AI (wave 3, web, est. 3h)

**Depends on:** its backend WP merged, or build against generated types

**Goal:** UI: AI feature list with status/toggle, global kill switch with confirm, interaction log.

**Scope**
- Aegean mockup v3 is binding; reuse existing pages as pattern (claims approvals inbox for list + detail + decision bar).
- el default, en; use generated API types.
- Vitest for the main component; one screenshot in orchestration/ux/<wp>/ (FYI, no review gate).

**Acceptance (main-path tests only)**
- Typecheck, lint, vitest pass; page renders against the dev stack.

**Files owned:** web/src/modules/platform/ai/**; routes.tsx and nav (append only); own i18n namespace files

**Rules:** defaults D-SL6-01..14 in orchestration/SLICE-PLAN-6.md section 4 are binding; do not wait on open questions. Touch only your files plus append-only shared files (module registration, permissions json, routes, i18n, ci.yml, seed-demo.py). Edit only your module's contracts yaml and regenerate with ContractGen; if regeneration conflicts after merging origin/main, regenerate again. If an EF migration collides with another WP's, regenerate yours after merging. Leave `// TODO(hardening):` notes for edge cases and list them in the report. Regulatory values only with a legalStatus tag and PRD source; otherwise mark illustrative.
Final report under 30 lines, then remove your worktree.
