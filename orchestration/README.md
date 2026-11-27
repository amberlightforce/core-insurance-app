# orchestration/ — START HERE

This folder plans and tracks how the Greek P&C core insurance system is built. Read this page first; it says which
documents are current and which are history. **Plan only from documents marked CURRENT.**

## 1. Reading order for a new session or agent

1. [`STATUS.md`](STATUS.md): where the build stands today (top lines).
2. The active plan:
   - Hardening waves 1–5: [`NEXT-STEPS.md`](NEXT-STEPS.md)
   - Build-out waves 6–27 (all missing features and pages): [`build-out/README.md`](build-out/README.md), then
     [`build-out/WAVES.md`](build-out/WAVES.md) and the item file for your module in [`build-out/items/`](build-out/items/)
3. How builders work: [`briefs/LIGHT-WORKFLOW.md`](briefs/LIGHT-WORKFLOW.md) and [`briefs/PITFALLS.md`](briefs/PITFALLS.md).
4. Rulings that constrain scope: [`DECISIONS.md`](DECISIONS.md) (search for the ids an item cites, e.g. D-USR-11).

The requirements themselves live outside this repo in `../../core-insurance-prds/` (PRD-01…18, design guide, mockup v3).

## 2. Document map

| Path | Status | What it is | Update rule |
|---|---|---|---|
| `README.md` | CURRENT | This map | Update when a document is added, retired or changes status |
| `STATUS.md` | CURRENT | Build status; newest entry on top | One line per finished wave or major event |
| `NEXT-STEPS.md` | CURRENT | Hardening phase: 66 items, waves 1–5 | Builders tick items; orchestrator after each wave |
| `build-out/` | CURRENT | Build-out phase: 295 items, waves 6–27, coverage of the master plan, open user decisions | See `build-out/README.md` §7 |
| `DECISIONS.md` | CURRENT | Append-only decision log (D-USR, D-ARC, D-SL…) | Append; never rewrite old entries |
| `briefs/LIGHT-WORKFLOW.md` | CURRENT | The builder process (D-USR-20) | Change only on a user ruling |
| `briefs/PITFALLS.md` | CURRENT | Known defects to self-check against | Append when a new class of defect is found |
| `ux/` | CURRENT (evidence) | Mockup-vs-app screenshots per UI item; `smoke-*/` route walks | Add a folder per UI item or smoke run |
| `PLAN.md` | REFERENCE | Original master plan (phases, W1–W9 waves, module order) | Frozen |
| `backlog/BACKLOG.md`, `backlog/backlog.json` | REFERENCE | All 3,711 Must requirements mapped to 126 master work packages | Frozen. **Its Status column is not maintained**; real coverage is `build-out/COVERAGE.md` |
| `tracker/` | REFERENCE | Scripts for the old claude.ai tracker artifact, fed by `backlog.json` | Not in use |
| `digests/` | REFERENCE | Short digests of each PRD, used for planning | Frozen |
| `briefs/BUILDER-TEMPLATE.md`, `briefs/REVIEWER-TEMPLATE.md` | REFERENCE | Older brief and review templates (still define *what* a brief contains) | Frozen; process is LIGHT-WORKFLOW |
| `briefs/sl3/` … `briefs/sl6/` | HISTORICAL | Builder briefs of slices 3–6 | Frozen |
| `HANDOVER.md` | HISTORICAL | Handover of 2026-10-08/09 | Frozen |
| `CODEX-TAKEOVER.md` | HISTORICAL | Recovery checkpoint of 2026-10-09 | Frozen |
| `SLICE-PLAN.md`, `SLICE-PLAN-2.md` … `SLICE-PLAN-6.md` | HISTORICAL | Thin-slice plans 1–6 (all merged) | Frozen |
| `SLICE-3-PLANNING-HANDOVER.md` | HISTORICAL | Input to slice-3 planning | Frozen |
| `SL4-*.md`, `SL5-MKT-ROLLBACK-CHECKPOINT.md` | HISTORICAL | Recovery checkpoints of single slice work packages | Frozen |

Outside this folder: `../docs/runbooks/` (operations), `../docs/regulatory/go-live-checklist.md`,
`../docs/module-pattern.md` (how a module is structured), `../contracts/openapi/*.yaml` (the API contract per module).

## 3. Phases so far

| Phase | Plan | State (2026-11-27) |
|---|---|---|
| Phase 0–1: plan and foundations | `PLAN.md` | Done |
| Slices 1–6: thin end-to-end journeys E2E-01…12 | `SLICE-PLAN*.md` | Done |
| Hardening waves 1–5 | `NEXT-STEPS.md` | Waves 1–4 done; wave 5 in progress |
| Build-out waves 6–27 | `build-out/` | Planned, not started |

## 4. Rules for documents (keep this folder clean)

- One active plan per phase. A new phase gets a new folder like `build-out/`, with a `README.md` that states its scope,
  rules and status, plus a row here.
- Work-item ids are permanent: `NX-*` (hardening), `BO-<MOD>-NN` (build-out). Never renumber or reuse; mark dropped
  items `dropped (reason)`.
- When a document stops being current, change its row here to HISTORICAL or REFERENCE and put a one-line banner at its
  top pointing back to this page. Do not delete it: decision ids and commit messages refer to it.
- Checkpoints, recovery notes and one-off reviews are HISTORICAL from the day they are written; prefer putting their
  outcome into `STATUS.md` and `DECISIONS.md`.
- Dates are absolute (2026-11-27), never "yesterday" or "next week".
