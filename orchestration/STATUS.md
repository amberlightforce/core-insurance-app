# STATUS — Greek P&C core insurance build

**Current phase:** 3 — Claims slice (SLICE-PLAN-2.md, D-USR-10) **COMPLETE** (2026-10-08): all slice-2 WPs merged; E2E-02a passes at API and UI level locally and in GitHub CI (run 37813614543, all 6 jobs green on 9a08c1a). Next: slice 3 (W6 servicing) when the user says so. Phase 2 thin E2E slice **COMPLETE** (2026-10-08): all 10 slice WPs merged; E2E-01 passes on Docker (API + Playwright UI) and in GitHub CI; accepted after a real-Chrome walkthrough (D-SLC-21).
**Last updated:** 2026-10-07 (night)
**Repo:** https://github.com/amberlightforce/core-insurance-app (private). GitHub push works (workflow scope fixed by user 2026-10-07). main pushed; first CI run GREEN (all jobs incl. Testcontainers integration tests + Trivy).
**Live tracker:** https://claude.ai/artifact/QwQVP63L5vGPhUskFrAzaD. Source of truth: `orchestration/backlog/backlog.json`. Update it with `tracker/set_status.py`, then push the change with ArtifactData.

Re-read STATUS.md, PLAN.md and DECISIONS.md at the start of every wave.

## Environment blockers (user action needed)
1. ~~GitHub workflow scope~~: FIXED by user.
2. ~~Docker Desktop~~: works now (Docker 29.3.1, reachable from WSL). Testcontainers are used (D-USR-08).
3. ~~Windows App Control / missing SDK~~: resolved. Native Windows build and tests verified (D-ARC-30); WSL retired.

4. **Repo made PUBLIC by the user (2026-10-08)** so Actions minutes are free; re-check whether CI runs. Previously: **GitHub Actions billing (user action needed, 2026-10-08):** since 08:04Z every CI and CodeQL job is refused before start: "recent account payments have failed or your spending limit needs to be increased" (Billing & plans). No CI has run on any claims-slice merge (d0ccaed onwards); the local merge gate (D-PRG-17/18) is the only verification until it is fixed. Re-run CI on main once billing is restored. **User chose to skip CI and gate locally (D-USR-12).**

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
| SL2-UI-CLM | **merged** (round 1 031dc50, round 2) | sonnet | live browser walks PASS both rounds; web 1252 |
| SL2-E2E | **merged** (phase A f47e5d6, phase B UI spec) | sonnet | E2E-02a API passes on fresh stack; amounts reconcile to the cent; CI job e2e02 added (not yet run: Actions billing) |


## Side work packages (2026-10-08)
| WP | Status | Notes |
|---|---|---|
| FIX-403 banner | **merged** (d0ccaed) | role-forbidden actions explained |
| FIX-E2E-RACE | **merged** (44a54ea, d95bf5e) | E2E-01/02 waited on partial journals; no product regression |
| UW referral decisions (D-UW-01) | **merged** (e9f01ca) | deep review FAIL (creator could approve; rejection laundering) → fixed; uwsenior |
| Dev superuser | **merged** | all roles, Development only, SoD unchanged |
| SL-UX restyle (D-USR-11/13) | **merged** (1ea0ad2) | accepted by user; focus ring + claim file |
| UW referral workbench (D-USR-13) | **paused by user** (WIP branch worktree-agent-ac9c66400c2402b05) | |
| Wizard vehicle value + plain rating errors | **merged** | user report 2026-10-08 |

## Phase 4 — slice 3 (planned)
Plan: `SLICE-PLAN-3.md`. Decisions: D-SL3-01..14. Briefs: `briefs/sl3/` (S0 and S1). Every builder and reviewer runs on Sonnet 5.5 (D-USR-16), with 6–8 concurrent builders. Precondition: the D-PRG-21 hot-file split has merged.

| WP | Batch | Module | Depends on | Review | Est. | Status |
|---|---|---|---|---|---|---|
| SL3-CONTRACTS | S0 | contracts (all) | — | light | 2.5 h | planned |
| SL3-POL-TEMPORAL | S0 | POL (Persistence, Queries) | contracts | deep (temporal) | 4 h | planned |
| SL3-POL-ENGINE | S0 | POL (Domain/Servicing) | contracts | deep (money) | 3.5 h | planned |
| SL3-MKT-TREATMENT | S0 | MKT + GR/CY pack | contracts | deep (money) | 2.5 h | planned |
| SL3-PFC-MOTOR11 | S0 | PFC seed | — | light | 1.5 h | planned |
| SL3-RAT-PRORATE | S0 | RAT | contracts | deep (money) | 3 h | planned |
| SL3-PLT-SUPPORT | S0 | PLT (dev clock, roles, authority) | contracts | deep (security) | 2.5 h | planned |
| SL3-E2E-HARNESS | S0 | tests/e2e, ci.yml | contracts | light | 2 h | planned |
| SL3-POL-CHANGE | S1 | POL (Commands/Change) | merged TEMPORAL + ENGINE | deep (temporal+money) | 4 h | planned |
| SL3-POL-CANCEL | S1 | POL (Commands/Cancellation) | merged TEMPORAL + ENGINE | deep (temporal+money) | 3.5 h | planned |
| SL3-POL-RENEW | S1 | POL (Commands/Renewal) | merged TEMPORAL + ENGINE | deep (temporal) | 4 h | planned |
| SL3-BIL-CREDIT | S1 | BIL | contracts | deep (money/ledger) | 4 h | planned |
| SL3-CMP-CREDIT | S1 | CMP | contracts | light | 2 h | planned |
| SL3-FIN-RULES | S1 | FIN | contracts | deep (ledger) | 3 h | planned |
| SL3-CLM-REVERIFY | S1 | CLM | contracts (+ merged TEMPORAL for the real-POL test) | deep (temporal) | 3.5 h | planned |
| SL3-UI-POL-FILE | S1 | web policy/file | contracts | light + visual | 3 h | planned |
| SL3-BIL-REFUND | S2 | BIL | merged BIL-CREDIT | deep (money+security) | 4.5 h | planned |
| SL3-UI-POL-JOBS | S2 | web policy/servicing | contracts (live walk after POL S1) | light + visual | 4 h | planned |
| SL3-UI-BIL | S2 | web billing/refunds | contracts (live walk after BIL-REFUND) | light + visual | 3 h | planned |
| SL3-UI-CLM | S2 | web claims/reverify | contracts | light + visual | 2 h | planned |
| SL3-E2E | S3 | tests/e2e, seed-demo | all merged | light (integration) | 5 h | planned |

## Module status (feature waves)
All 116 feature WPs: **not started** (W1 starts after Phase 1 passes). See backlog/BACKLOG.md.

## Agent-session notes
- Reviewers and builders are resumed with SendMessage so they keep context. Worktrees are under `.claude/worktrees/` (git-ignored).
- Branches: F-1a `worktree-agent-a602a1c6b1ab3deea` (merged); events `worktree-agent-a2cb28e17172c7600` (merged); OpenAPI `worktree-agent-a74cec1b448cd4c93`; rule engine `worktree-agent-aae82a473ab0a3abd`; spike `worktree-agent-a7e75bab225abc6b5` (merged); design system `worktree-agent-ad02cb192ca091af7`; F-1b new worktree.
