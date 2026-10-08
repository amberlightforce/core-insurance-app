Runs on Sonnet 5.5 (model: sonnet), D-USR-16.

# Brief SL5-UW-WB-API — referral workbench API: salvage the WIP branch, decidability preview, MINE view (wave 1)

Wave 1, **PARALLEL-NOW**. Review: **deep (security: SoD on referral decisions)**. Estimate: 3.5 h. Meaningful PR.

- **WP / PRD:** SL5-UW-WB-API.
  - Requirements:
    - REQ-UW-078 (issues I can decide, via a dry authority check per issue);
    - REQ-UW-010 (show required authority, own limit or source grant and the outcome **before commit**; preview subset);
    - REQ-UW-111 (decision view);
    - SOD-UW-02, BR-UW-013 (job creator, requester, producer, editors and evaluators may not decide);
    - SCR-UW-01 and SCR-UW-03 data needs (subset).
  - Sources: PRD-04 (`../core-insurance-prds/PRD-04-underwriting-referral-workbench.md`) §6 SCR-UW-01/03, §12;
    digest `orchestration/digests/PRD-04.md` (§SoD, §authority, workbench).
  - Decisions: D-UW-01, D-SL5-02, D-SL5-03, D-SL5-04, D-SL5-05, D-USR-13.
- **Step 1 — salvage (do this first, about 30 min).**
  - The branch is `worktree-agent-ac9c66400c2402b05`. Work **on that branch** (or a branch made from it). Run
    `git fetch origin && git merge origin/main`. **Do not rebase**: the branch holds a merge commit.
  - Expected conflicts (from a trial merge):
    - `src/CoreIns.Host/appsettings.json`: delete the two `uw.Referral.*` lines there and add them to
      `src/CoreIns.Host/permissions/uw.json`, with the same roles: Staff.UnderwritingManager, Platform.Admin.
    - `tests/CoreIns.Contracts.Tests/GeneratedContractTests.cs`: take main's version. The count test maintains itself
      since D-PRG-21.
  - Then regenerate (`dotnet run --project tools/CoreIns.ContractGen`), run `--check` and `validate_samples.py`, build,
    and run `ReferralWorkbenchTests` and the existing UW tests.
  - Commit "SL5-UW-WB-API: merge main into the workbench WIP".
  - The WIP has never been reviewed. Read `ReferralQueries.cs`, `ReferralReads.cs` and the `EvaluationFacts` migration
    as if they were yours, and fix what you find.
- **Step 2 — contract first (push within the first hour, so SL5-UI-UW-WB can build on it).** Additive edits to
  `contracts/openapi/uw.yaml`:
  - `ReferralQueueCode` gains **`MINE`**. `ReferralQueueCounts` gains `mine`.
  - `ReferralView.decidability`, plus a per-issue `decidability` beside each entry of `ReferralView.issues` (add a
    wrapper or a field, without breaking `uw.Issue.list`'s item schema), **always set**
    (`canDecide: bool`, `reasons: DecidabilityReason[]`, `authority: {type, issueType, outcome ALLOW|REFER|DENY,
    sourceGrantId?}`):
    - the reasons are `SOD_CREATOR`, `SOD_PARTICIPANT`, `SOD_PRODUCER`, `SOD_EVALUATOR`, `NOT_HUMAN`, `NO_AUTHORITY`
      and `NOT_OPEN`;
    - `canDecide` on the view is true only when it is true for every Open issue.
  - `ReferralReason.observed` (string, the fact value the rule read) and `ReferralReason.limit` (string or null). When
    the limit is null, `limitUnavailableReason` is always set (e.g. `RULE_DECLARES_NO_LIMIT`).
  - Error: `UW-ERR-VALIDATION` "narrow the view" when MINE would scan more than 500 open referrals.
  - Regenerate; samples valid. Push.
- **Step 3 — implementation.**
  1. **One SoD + authority function.** Extract the participant / creator / producer / evaluator / human checks and the
     `UW.ISSUE_APPROVAL{issueType}` authority check from `Commands/DecideIssues.cs` into one internal service, e.g.
     `Domain/DecisionEligibility.cs`.
     - `DecideIssues` and the preview **both call it**. Never keep two copies.
     - For the preview the authority check runs **dry**: no check record, no approval request and no audit "decision"
       event. Find out how `plt.Authority.check` does a dry run (the D-SL2 "dry-run authority checks" follow-up). If
       there is no dry mode, report it and use the in-process evaluation without persistence; do not add a PLT
       endpoint.
  2. `uw.Referral.get` fills `decidability` per issue and in total.
  3. `uw.Referral.list?queue=MINE` returns the open referrals where `canDecide` is true for the caller. Above 500 open
     referrals in the entity it refuses with `UW-ERR-VALIDATION`. `counts.mine` comes from the same computation.
  4. `observed` and `limit` for the three motor referral rules (DRIVER_AGE, VEHICLE_AGE, VEHICLE_VALUE):
     - `observed` comes from the stored derived facts;
     - `limit` is read from the rule set's declared threshold. Add a declarative `explain: {fact, limit}` to those rules
       in the seed rule set if none exists. The values are the **existing** illustrative thresholds; never new numbers;
     - every other rule gets `limit = null` + `RULE_DECLARES_NO_LIMIT`.
  5. `uw.Issue.decide` behaviour is unchanged apart from the shared function. Its existing D-UW-01 tests must stay
     green.
- **Depends on / provides:**
  - **Depends on:** nothing (salvage).
  - **Provides:** the API that SL5-UI-UW-WB and the SL5-E2E workbench spec use.
- **Files you own:** `src/CoreIns.Modules.Underwriting/**` (**UW migration owner**; the salvaged `EvaluationFacts`
  migration plus at most one new one), `contracts/openapi/uw.yaml`, the generated UW contracts,
  `tests/CoreIns.IntegrationTests/Underwriting/**`, `src/CoreIns.Host/permissions/uw.json`.
- **Shared, additive only:**
  - `contracts/openapi/pol.yaml`: the one `x-consumers: UW` line from the WIP;
  - `src/CoreIns.Modules.Policy/Services/InProcessServices.cs`: the `GetAsync` in-process method from the WIP.
  - Slice-3 POL WPs may be editing near it. Re-merge `origin/main` and regenerate **just before** the PR, and keep both
    sides on a conflict.
- **Do not touch:** `web/**` (SL5-UI-UW-WB), any other module.
- **Tests (integration, Testcontainers). Add to `ReferralWorkbenchTests`, or a new `ReferralDecidabilityTests`:**
  - The **job creator**, an editor/quoter, the producer and an evaluator each get `canDecide = false` with the right
    reason. `uwsenior` on someone else's quote gets `canDecide = true`.
  - **Parity:** for each preview case, `uw.Issue.decide` gives the same allow/deny. This is a table-driven test over
    one fixture: the preview and the commit must never disagree.
  - The preview writes nothing: there are no new rows in the authority-check, approval or audit decision tables.
  - MINE contains exactly the decidable referrals. After a decision the referral leaves MINE and appears in
    DECIDED_BY_ME_TODAY. Counts match the lists.
  - Another legal entity's referral never appears and its get is a 404. There is no P2 (no birth date or full name) in
    any response; search for the real value (PITFALLS 31).
  - `observed`/`limit` for a 1991 vehicle (REFER-OLD-VEHICLE) show the vehicle age and the rule limit.
  - A Staff.Underwriter without the manager role gets 403 on list/get.
- **Reviewer focus (deep, security):**
  - Try to make the preview say "yes" where decide says "no", and the reverse.
  - Try to read another entity's referral or the unmasked name.
  - Try to launder a rejection through a fact refresh (PITFALLS 6).
  - Look for a MINE-count side channel across entities.
  - Check that dry-run checks persist nothing.
- **PITFALLS to self-check (likely):** 1–6, 12, 15, 18, 21, 22, 23.
- **Workflow:** `briefs/sl5/_COMMON.md`. PR title:
  `SL5-UW-WB-API: referral workbench API (salvaged read model, decidability, MINE)`. Do not merge.
