# Slice 3 planning handover — W6 policy servicing

**Written:** 2026-10-08 by the orchestrator at the end of slice 2. **For:** whoever plans and runs slice 3 (a new
orchestrator session or this one). Read this first, then `HANDOVER.md` (whole), `STATUS.md`, the tail of `DECISIONS.md`
(D-SL2-*, D-UW-01, D-USR-09..16, D-PRG-19..22) and `briefs/PITFALLS.md`.

## 1. Where things stand

- **Done:** foundations; slice 1 (E2E-01 quote → bind → invoice → pay → ledger); slice 2 (E2E-02a claims: FNOL → cover
  on the loss-date snapshot → reserves and payments with authority + maker ≠ checker approvals → BIL payout → FIN
  journals → close); UW referral decisions (D-UW-01); dev `superuser`; the Aegean restyle of every screen.
- **Proven by:** E2E-01 and E2E-02a at API and browser level, locally and in GitHub CI (the repo is public since
  D-USR-15, so Actions minutes are free).
- **Paused, not part of slice 3 unless the user asks:** the UW referral workbench (WIP branch
  `worktree-agent-ac9c66400c2402b05`, mockup «Ανάληψη κινδύνου»).
- **User preferences that bind the plan:** keep going without asking for OKs (share results as FYI); all sub-agents on
  **Sonnet 5.5** (D-USR-16); up to **6 concurrent agents** (D-USR-14); UI must match the mockup (D-USR-11/13); rebuild
  the user's Docker app (`coreins`, :25000) after every merge to main.

## 2. Goal of slice 3

Make the motor product **serviceable** after sale, as thin slices over what exists. Proposed journeys (cut versions of
PRD-18-B §4.2/4.3; read the digests before fixing scope):

| Journey | PRD-18 E2E | Thin version to prove |
|---|---|---|
| Mid-term change (endorsement) | (POL X-2/X-3, part of E2E-11) | Change vehicle or address mid-term → re-rate the remaining term → additional/return premium charge → BIL invoice or credit → FIN journals; history keeps the old segment |
| Cancellation with refund | **E2E-03** | Policyholder cancels → pro-rata refund (premium; IPT/levy treatment per open questions) → fiscal **credit note** (stub) → BIL refund disbursement (reuse SL2 disbursement) → FIN |
| Renewal | **E2E-04** | Renewal offer for the next term → re-rate → accept → new term, invoice |
| Non-payment cancellation | **E2E-07** | Unpaid invoice → waiting-period clock (CMP) → cancellation for non-payment → notice (stub) → FIN |
| Distance-selling withdrawal | **E2E-08** | Withdrawal inside the cooling-off window → full refund → FIN |

The planner decides whether all five fit one slice or whether slice 3 = change + cancellation + renewal and slice 4 =
lapse + withdrawal. Recommendation: **start with change, cancellation and renewal**; they share the hard POL work.

## 3. The hard part to solve first (blocking)

- **D-SL2-09 (POL):** (a) the knownAt race — commands stamp `recorded_from` with the clock at command start but commit
  later, so a snapshot read with knownAt = now can change once an in-flight supersession commits. Choose a settle
  horizon for default knownAt or stamp record time at commit. (b) `pol.policy` is **not bitemporal** — a policyholder
  change or rewrite must version it. (c) `superseded: true` + successor ref on snapshots, outside the byte-identical
  content. Claims already rely on snapshots (`clm` stores the snapshot ref), so changes must raise
  `ReverificationRequired` for open claims or record why not (PRD-07 REQ-CLM-002/058; PRD-18 E2E-11).
- **Out-of-sequence endorsements** (a change effective before an earlier-recorded change) are the hardest temporal
  logic in the programme; the plan must either include them with deep review or cut them explicitly.
- **Pro-rata and refund rules:** day-count, minimum premium, short-rate vs pro-rata, IPT "never reduced by
  cancellations" (PRD-09 GL-2410 note) vs levy refund (OI-POL-08 cluster, ΠΟΛ 1028/2017 applicability F-218), distance
  withdrawal tax treatment (F-401). Where a value is open, fail closed or mark `provisional` (D-REG-*), never invent.

## 4. How to plan it (step by step)

1. **Read** `orchestration/digests/PRD-05.md` (policy transactions, X-2/X-3/X-4/X-9/X-11/X-12), `PRD-06.md` (billing:
   credit notes, refunds, instalments, dunning/waiting period J-05/J-06), `PRD-09.md` (refund and cancellation
   postings, GF-04), `PRD-11.md` (fiscal credit notes, CMP clocks), `PRD-10.md` (notices — stub only),
   `PRD-17.md` (Greek pack values with legalStatus), `PRD-18-B.md` §4 (E2E-03/-04/-07/-08/-11 step tables), and the W6
   section of `backlog/BACKLOG.md` (W6 = 17 WPs, 306 Musts, 481 points; PLAN.md §3).
2. **List the requirement ids** each journey step needs (from the E2E step tables), and the open questions that block
   amounts. Record scope cuts and illustrative values as decision rows `D-SL3-01…`.
3. **Write `orchestration/SLICE-PLAN-3.md`** in the format of `SLICE-PLAN-2.md`: goal, OUT list, batches S0–S3 for up
   to 6 agents, each WP with scope, PRD/REQ ids, review depth (deep first round for money/ledger, temporal and
   security, light otherwise), and which module it owns. **One WP per module per batch** (no two builders in the same
   module at once — that is what causes the worst conflicts: shared DbContext and migration snapshots).
4. **Interface-first ordering:** S0 types every new contract (POL change/cancel/renew ops and events, BIL credit/refund,
   CMP credit note + clock, FIN refund postings) so S1 builders code against generated fakes in parallel; an
   integration WP wires real modules at the end.
5. **Critical path:** expected POL temporal (D-SL2-09) → POL change/cancel → BIL credit/refund → UI → E2E. Put POL
   first and give it the deep review; start BIL/FIN/CMP/UI in parallel on the typed contracts.
6. **Brief every builder from `briefs/BUILDER-TEMPLATE.md`** (PR workflow, quick local gate, PITFALLS self-check) and
   name the pitfalls most likely for that WP (e.g. POL: items 13–17; BIL/FIN: 8–12; UI: 25–28).
7. **Finish with an integration/E2E WP** adding E2E-03 (and the others in scope) at API and Playwright level to
   `tests/e2e`, a runner script (committed executable), and a CI job.
8. **Estimate:** about 12–16 agent wall-clock hours at 6 agents (slice 2 took about 10 h for 8 WPs plus side work).

## 5. How to run it (process changes agreed on 2026-10-08)

- **Pull-request workflow (D-PRG-19):** builders push their own branch and open a PR; GitHub runs the full suite (build,
  every .NET test project, web gates, contract sample validation, CodeQL ≥ 7 gate, E2E-01 and E2E-02a — add the new
  E2E job) as parallel jobs; the orchestrator merges only when every check is green, so main is never red. Locally:
  quick gate only (build, touched tests, ContractGen `--check`, web lint/typecheck + touched vitest files).
  Watch PRs with `gh pr checks <url> --watch` in the background, never by polling in the foreground.
- **Small batched checks (D-PRG-22):** up to 3 reviewed WPs from **different modules** may go on one
  `batch/<name>` branch and one PR; if it is red, split it at once so the failing WP is obvious.
- **Pitfalls up front (D-PRG-20):** every brief points at `briefs/PITFALLS.md`; reviewers check it first. Add each new
  defect class you see to that file.
- **Hot files split (D-PRG-21):** per-module permission files, dev users in their own file, self-maintaining count tests
  (a WP is doing this now — check it merged before slice 3 starts; if not, expect conflicts in `appsettings*.json`,
  `GeneratedContractTests.cs` and `DevelopmentSignInTests.cs`).
- **Models:** every builder and reviewer `model: "sonnet"` (D-USR-16). Deep reviews keep their depth: adversarial
  probe tests in a scratch copy.
- **Merging and Docker:** merge in `.claude/worktrees/orchestrator` (branch main), never in the shared main checkout
  (another session uses it on branch `training-portal`). After each merge to main: push, then
  `docker compose -p coreins -f infra/local/compose.yaml up -d --build` from the orchestrator worktree.
- **Machine:** Core Ultra 7 165U (14 threads), 31.5 GB RAM, WSL capped at 12 GB with gradual memory reclaim, about
  20 GB free disk. Keep isolated stacks few and torn down; vitest `--maxWorkers=2`.
- **Memory files to keep current:** `STATUS.md` (per-WP rows), `DECISIONS.md` (append only), `HANDOVER.md` at slice end,
  the live tracker (`ArtifactData` `meta/summary` on https://claude.ai/artifact/QwQVP63L5vGPhUskFrAzaD).

## 6. Open items to carry into slice 3

- Slice-2 follow-ups worth folding in when touching the same code: transaction sets of a claim read; dry-run authority
  checks; typed approval `diff`; close-guard detail in `errors[]`; PLT in-process withdraw for leftover approvals;
  payment holds / reissue / void (D-SL2-13).
- Business questions still open (HANDOVER §9) that affect slice 3 amounts: IPT/levy refundability, motor IPT class,
  stamp duty, myDATA credit-note codes, instalment plans.
