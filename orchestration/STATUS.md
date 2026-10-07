# STATUS — Greek P&C core insurance build

**Current phase:** 1 — Foundations (plan approved 2026-10-07)
**Repo:** https://github.com/amberlightforce/core-insurance-app (private) · **Live tracker:** https://claude.ai/artifact/QwQVP63L5vGPhUskFrAzaD (source of truth: `orchestration/backlog/backlog.json`, pushed to the tracker database by the orchestrator on every status change)
**Last updated:** 2026-10-07

Re-read STATUS.md, PLAN.md and DECISIONS.md at the start of every wave.

## Phase 0 reading progress

| Source | Digest | Status |
|---|---|---|
| PRD-01 … PRD-17 | digests/PRD-NN.md | done |
| 00-system-contract | digests/00-system-contract.md | done |
| 00-integration-review | digests/00-integration-review.md | done |
| 00-baseline-inventory | digests/00-baseline-inventory.md | done |
| PRD-18 (4 parts) | digests/PRD-18-A..D.md | done |
| Design guide (2 parts) | digests/DESIGN-A/B.md | done |

## Module status

| PRD | Module | Status | Owner agent | Blockers |
|---|---|---|---|---|
| FOUNDATION | F-1a scaffold, CI, test harness | building | builder agent (worktree) | .NET 10 SDK not installed locally (building via Docker) |
| FOUNDATION | F-1b … F-1f | not started | — | after F-1a |
| PRD-01 … PRD-17 | all modules | not started | — | Phase 1 |
| — | Backlog generation (WP split) | building | backlog agent | — |
