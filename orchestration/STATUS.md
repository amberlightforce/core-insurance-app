# STATUS — Greek P&C core insurance build

**Current phase:** 1 — Foundations (plan approved 2026-10-07)
**Last updated:** 2026-10-07 (afternoon)
**Repo:** https://github.com/amberlightforce/core-insurance-app (private). GitHub push works (workflow scope fixed by user 2026-10-07). main pushed; first CI run GREEN (all jobs incl. Testcontainers integration tests + Trivy).
**Live tracker:** https://claude.ai/artifact/QwQVP63L5vGPhUskFrAzaD. Source of truth: `orchestration/backlog/backlog.json`. Update it with `tracker/set_status.py`, then push the change with ArtifactData.

Re-read STATUS.md, PLAN.md and DECISIONS.md at the start of every wave.

## Environment blockers (user action needed)
1. ~~GitHub workflow scope~~: FIXED by user.
2. Docker Desktop will not start (stale `%LOCALAPPDATA%\docker-secrets-engine\engine.sock`). Testcontainers and the image build can't run locally.
3. Windows App Control blocks freshly built DLLs. Agents build and test in WSL (SDK 10.0.401 at ~/.dotnet-coreins, ICU via ~/dn.sh), with an embedded PostgreSQL 17 at ~/pg17.

## Pending user requests
- **When Phase 1 (foundations) is done: write a detailed HANDOVER document** (requested 2026-10-07). It must cover what was built, how to run/test it, architecture and conventions, every ruling, known gaps and deferrals, environment issues, the backlog and how W1 starts, and how to operate the orchestration (tracker, briefs, review loop).

## Phase 1 work packages

| WP | Status | Notes |
|---|---|---|
| F-1a scaffold, CI, harness | **merged, CI green** | PASS on review attempt 3 (Key Vault least privilege, SCRAM bootstrap) |
| F-1b shared kernel + platform primitives (+ outbox throughput spike) | building | started after F-1a merge |
| F-1c contracts: events | **merged** | 304 event schemas, PASS on attempt 2 |
| F-1c contracts: OpenAPI | final minors, then merge | 1,110 operations; PASS with minors after D-API-06a |
| F-1c contracts: C# records | not started | after F-1b |
| F-1d design system | building | foundations, formatters, shell and Button committed; components in progress (nested builders started before D-PRG-15) |
| F-1e Greek cross-cutting | not started | next free slot |
| F-1f(a) Gotenberg PDF/A spike | **merged** | D-ARC-07a/b/c |
| F-1f(b) outbox throughput | inside F-1b | |
| F-1f(c) rule engine | final fix attempt (3/3) | escalate to user if it fails again |

## Module status (feature waves)
All 116 feature WPs: **not started** (W1 starts after Phase 1 passes). See backlog/BACKLOG.md.

## Agent-session notes
- Reviewers and builders are resumed with SendMessage so they keep context. Worktrees are under `.claude/worktrees/` (git-ignored).
- Branches: F-1a `worktree-agent-a602a1c6b1ab3deea` (merged); events `worktree-agent-a2cb28e17172c7600` (merged); OpenAPI `worktree-agent-a74cec1b448cd4c93`; rule engine `worktree-agent-aae82a473ab0a3abd`; spike `worktree-agent-a7e75bab225abc6b5` (merged); design system `worktree-agent-ad02cb192ca091af7`; F-1b new worktree.
