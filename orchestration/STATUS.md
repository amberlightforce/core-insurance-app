# STATUS — Greek P&C core insurance build

**Current phase:** 2 — Thin E2E slice (foundations complete; user go-ahead 2026-10-07, D-USR-08). Plan: `SLICE-PLAN.md`
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
| SL-0 platform wiring + Party | **in review** (deep) | opus | built 2927240: integration 82/82, arch 196/196, web 1112; compose + smoke OK. Follow-ups D-SLC-05/06 done at 86b9973 |
| SL-FIX-WEB | **merged** | sonnet | 1109/1109 native |
| SL-POL | building | opus | based on SL-0 branch (started before SL-0 review ends, to save time) |
| SL-MKT | building | sonnet | based on SL-0 branch |
| SL-PFC | building | sonnet | based on SL-0 branch |
| SL-RAT-UW | queued | sonnet | starts when the SL-0 reviewer frees a slot (max 4) |
| SL-BIL, SL-FIN, SL-UI | not started | | after S1 |
| SL-E2E | not started | | after S2 |

## Module status (feature waves)
All 116 feature WPs: **not started** (W1 starts after Phase 1 passes). See backlog/BACKLOG.md.

## Agent-session notes
- Reviewers and builders are resumed with SendMessage so they keep context. Worktrees are under `.claude/worktrees/` (git-ignored).
- Branches: F-1a `worktree-agent-a602a1c6b1ab3deea` (merged); events `worktree-agent-a2cb28e17172c7600` (merged); OpenAPI `worktree-agent-a74cec1b448cd4c93`; rule engine `worktree-agent-aae82a473ab0a3abd`; spike `worktree-agent-a7e75bab225abc6b5` (merged); design system `worktree-agent-ad02cb192ca091af7`; F-1b new worktree.
