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

4. **GitHub Actions billing (user action needed, 2026-10-08):** since 08:04Z every CI and CodeQL job is refused before start: "recent account payments have failed or your spending limit needs to be increased" (Billing & plans). No CI has run on any claims-slice merge (d0ccaed onwards); the local merge gate (D-PRG-17/18) is the only verification until it is fixed. Re-run CI on main once billing is restored.

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
| SL2-PLT | **merged** (c7df013) | opus | deep review FAIL (D1 HTTP request let maker set authority; D2 AI-maker principal could approve) → fixed, orchestrator re-check; gate green: integration 342, all 10 suites |
| SL2-POL-SNAP | **merged** (597f2fe) | sonnet | deep review FAIL (D1 forged future knownAt ref) → fixed, orchestrator re-check; gate green: integration 303, all 10 suites |
| SL2-BIL-DISB | **merged** | opus | deep review FAIL (D1 duplicate key never fired) → fixed + M1/M3/M4/M6, orchestrator re-check; gate green: integration 325, all 10 suites |
| SL2-CLM-CORE | **merged** (98ccd8c) | opus | deep review FAIL (D1 did not compile against merged POL) → fixed + hardening, orchestrator re-check; gate green: integration 379, all 10 suites |
| SL2-CLM-MONEY | **merged** (f556712) | opus | deep review FAIL (D1 split reserves escaped DENY, D2 no cumulative payment check, D3 mixed set checked one authority) → fixed per D-SL2-13, orchestrator re-check; gate green: integration 406, all 10 suites |
| SL2-FIN-CLM | **merged** (601189c) | opus | deep review PASS first round; gate green: integration 391, all 10 suites; fail-closed follow-ups D-SL2-12 |
| SL2-UI-CLM | round 1 **merged** (031dc50); round 2 building | sonnet | round 1 light review + live browser check PASS; web 1241 |
| SL2-E2E | phase A **merged** (f47e5d6); phase B (UI spec) after UI round 2 | sonnet | E2E-02a API passes on fresh stack; amounts reconcile to the cent; CI job e2e02 added (not yet run: Actions billing) |

## Module status (feature waves)
All 116 feature WPs: **not started** (W1 starts after Phase 1 passes). See backlog/BACKLOG.md.

## Agent-session notes
- Reviewers and builders are resumed with SendMessage so they keep context. Worktrees are under `.claude/worktrees/` (git-ignored).
- Branches: F-1a `worktree-agent-a602a1c6b1ab3deea` (merged); events `worktree-agent-a2cb28e17172c7600` (merged); OpenAPI `worktree-agent-a74cec1b448cd4c93`; rule engine `worktree-agent-aae82a473ab0a3abd`; spike `worktree-agent-a7e75bab225abc6b5` (merged); design system `worktree-agent-ad02cb192ca091af7`; F-1b new worktree.
