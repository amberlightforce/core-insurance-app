# STATUS — Greek P&C core insurance build

**Current phase:** 3 — Claims slice (SLICE-PLAN-2.md, D-USR-10) **IN PROGRESS** (2026-10-08). Phase 2 thin E2E slice **COMPLETE** (2026-10-08): all 10 slice WPs merged; E2E-01 passes on Docker (API + Playwright UI) and in GitHub CI; accepted after a real-Chrome walkthrough (D-SLC-21).
**Last updated:** 2026-10-07 (night)
**Repo:** https://github.com/amberlightforce/core-insurance-app (private). GitHub push works (workflow scope fixed by user 2026-10-07). main pushed; first CI run GREEN (all jobs incl. Testcontainers integration tests + Trivy).
**Live tracker:** https://claude.ai/artifact/QwQVP63L5vGPhUskFrAzaD. Source of truth: `orchestration/backlog/backlog.json`. Update it with `tracker/set_status.py`, then push the change with ArtifactData.

Re-read STATUS.md, PLAN.md and DECISIONS.md at the start of every wave.

## Environment blockers (user action needed)
1. ~~GitHub workflow scope~~: FIXED by user.
2. ~~Docker Desktop~~: works now (Docker 29.3.1, reachable from WSL). Testcontainers are used (D-USR-08).
3. ~~Windows App Control / missing SDK~~: resolved. Native Windows build and tests verified (D-ARC-30); WSL retired.

## Pending user requests
- None open. Handover delivered (`HANDOVER.md`). Pause lifted by the user (D-USR-08).

## Phase 1 work packages
All six (F-1a … F-1f) **merged**; see HANDOVER §3. CI on main: .NET, Bicep, images green; web job red on ICU-dependent compact-number tests → SL-FIX-WEB.

## Slice work packages (SLICE-PLAN.md)

| WP | Status | Owner | Notes |
|---|---|---|---|
| SL-0 platform wiring + Party | **merged** (5720b79) | opus | review 1 FAIL (M1 plaintext P2 in idempotency store) → fixed 4e849b4, orchestrator-verified; main: build clean, integration 83, arch 196, platform 47, host 18 |
| SL-FIX-WEB | **merged** | sonnet | 1109/1109 native |
| SL-POL | **merged** (5ed800f) | opus | review 1 FAIL (M1 history erase, M2 status) → fixed 82548c7, orchestrator re-check; gate green: integration 257, all suites |
| SL-MKT | **merged** (ca3c127) | sonnet | light review PASS; D-REG-06a split removed; full gate green (10 suites) |
| SL-PFC | **merged** (ae022f0) | sonnet | light review PASS; full gate green (integration 135, arch 196) |
| SL-RAT-UW | **merged** (77a6947) | sonnet | review 1 FAIL (M1 IPT legalStatus/fail-open) → fixed 0060a00, orchestrator re-check; gate green: integration 237, all suites |
| SL-BIL | **merged** (0d4ac79) | opus | review 1 FAIL (D1 mutable posted entry, D2 stranded set) → fixed e064cbc, orchestrator re-check; gate green: integration 295 |
| SL-FIN | **merged** (511c73f) | opus | review 1 FAIL (M1 appendable posted journal) → fixed 4f40ac9 (D-ARC-34 seal), orchestrator re-check; real-BIL trial posted cleanly; gate green: integration 279 |
| SL-UI | **merged** (8533652) | sonnet | light review PASS; web 1189 tests; viewed live on Docker |
| SL-E2E | **merged** (b541059) | sonnet | E2E-01 API test passes on fresh Docker stack (465.76 EUR, 8 balanced journals); CI job e2e01; Playwright UI run next |

## Claims slice work packages (SLICE-PLAN-2.md)

| WP | Status | Owner | Notes |
|---|---|---|---|
| SL2-PLT | building | opus | approvals, claims roles, screening stub |
| SL2-POL-SNAP | building | sonnet | snapshot at loss date |
| SL2-BIL-DISB | building | opus | payee accounts, disbursement, stub bank |
| SL2-CLM-CORE | building | opus | claim reference vertical |
| SL2-CLM-MONEY | not started | opus | S1 |
| SL2-FIN-CLM | not started | opus | S1 |
| SL2-UI-CLM | not started | sonnet | S1 |
| SL2-E2E | not started | sonnet | S2 |

## Module status (feature waves)
All 116 feature WPs: **not started** (W1 starts after Phase 1 passes). See backlog/BACKLOG.md.

## Agent-session notes
- Reviewers and builders are resumed with SendMessage so they keep context. Worktrees are under `.claude/worktrees/` (git-ignored).
- Branches: F-1a `worktree-agent-a602a1c6b1ab3deea` (merged); events `worktree-agent-a2cb28e17172c7600` (merged); OpenAPI `worktree-agent-a74cec1b448cd4c93`; rule engine `worktree-agent-aae82a473ab0a3abd`; spike `worktree-agent-a7e75bab225abc6b5` (merged); design system `worktree-agent-ad02cb192ca091af7`; F-1b new worktree.
