# SLICE PLAN 5 — underwriting workbench and pack rollback (UW referral workbench, E2E-12)

**Status:** planned 2026-10-08 (planner, following `SLICE-3-PLANNING-HANDOVER.md` §4 and §5). **Decisions:** proposed rows
D-SL5-01…D-SL5-14 (§11). The orchestrator appends them to `DECISIONS.md`.
**Process:** the same as slice 3:
- **Every builder and every reviewer runs on Sonnet 5.5** (`model: "sonnet"`, D-USR-16).
- PR workflow with a quick local gate (D-PRG-19). The PITFALLS self-check (D-PRG-20). The hot files are split
  (D-PRG-21). Small WPs are batched (D-PRG-22, D-USR-18). Docs go straight to main (D-USR-17).
- Deep, adversarial first-round reviews for three areas: **security** (SoD on referral decisions, maker-checker on pack
  rollback and product fall-back), **config/temporal** (configuration states, hash pinning, rollback windows) and
  **money**. A WP is cut so that its review takes about 45 minutes or less.

**Concurrency with other slices:** slice 3 (servicing) is building now. It owns MKT, PFC, RAT, PLT, POL, BIL, CMP, FIN,
CLM and the policy web screens in its waves. Slice 4 (finish claims + RI) is planned in parallel and will own CLM, RI and
parts of BIL/FIN. The 6–8 builder cap (D-SL3-13) is **global across slices**. Slice 5 needs at most 4 builder slots at
once, and its schedule (§8) assumes 3–4. Each WP is marked **PARALLEL-NOW** (no file overlap with slices 3/4) or
**GATED** (it waits for a named slice-3 merge).

## 1. Goal

1. **Underwriting referral workbench** («Ανάληψη κινδύνου»). A senior underwriter opens a workbench laid out as in the
   Aegean mockup v3:
   - work views with counts;
   - the referral queue;
   - a detail panel with the quote header, the risk facts the rules read, each referral reason and the authority they
     hold;
   - a decision bar for approve or reject, with a mandatory reason.

   The workbench shows up front whether the user may decide. The server computes this with the same SoD and authority
   code that `uw.Issue.decide` uses (D-UW-01), and the decide command still re-checks. The workbench uses real data
   only: there are no priority scores, SLA timers or AI summary (D-USR-13).
2. **E2E-12 pack rollback after bound business** (PRD-18-B §4, REQ-MKT-138, REQ-POL-354, REQ-PFC-213):
   - Policies are bound under the slice-3 pack data: Greece pack **0.2.0**, which carries the GR tax-treatment rules of
     SL3-MKT-TREATMENT, and product **MOTOR-GR 1.1** (D-SL3-15).
   - A release manager then rolls the GR pack back to **0.1.0**, and a checker approves. A product fall-back replaces
     1.1 with **1.2**, a copy of 1.0 (REQ-PFC-213).
   - Bound business keeps its pinned configuration hash, product version, premium, charges, invoices and journals. The
     old state is still reproducible by hash.
   - POL lists the affected bound transactions in an exception queue, marks the open quotes stale and re-rates nothing.
   - New business uses the restored configuration and the fall-back product.

   The journey is proven at API level, with a short Playwright check of the exception queue, in a new CI job `e2e12`.

## 2. Scope and OUT list

**IN** (D-SL5-01):
- salvage of the WIP workbench read model `uw.Referral.list/get` (§3);
- a decidability preview and the "Οι παραπομπές μου" view as "referrals I can decide" (REQ-UW-078);
- the workbench screen;
- a configuration-state history in MKT (hash chain, pack-version registry, resolve by any slice-5 hash);
- pack activation and rollback with maker-checker and `PackActivated`/`PackRolledBack`;
- the GR pack versions 0.1.0 and 0.2.0 as data;
- the PFC fall-back version (REQ-PFC-213), with RAT activating the source tariff for it;
- the POL rollback exception queue with review outcomes (REQ-POL-354 subset);
- a thin pack registry and exception-queue UI;
- E2E-12 and a workbench UI spec, with CI job `e2e12`.

**OUT** (D-SL5-01, each with the reason):

| Out | Reason |
|---|---|
| Referral entity with assignment, routing profile, priority score, SLA, Get next (W3-UW-03: REQ-UW-121…130) | WRK (activities, queues, assignment, SLA) is not built; PRD-04 routes through WRK. "Mine" = referrals I can decide (REQ-UW-078), which needs no assignment (D-SL5-03) |
| Approve with conditions, the "Με όρους σε εκκρεμότητα" view, contingencies, scope/validity/tolerance (REQ-UW-095, UWApproval) | Condition templates and contingencies are W3-UW-02/-04; `IssueDecisionCode` has APPROVE/REJECT only (D-UW-01) |
| AI risk summary, sanctions card, "the producer sees" presence, bulk selection and bulk decide, undo after a decision | Features not built (D-USR-13). A decision is audited and final; undo would fake reversibility. Bulk decide is a security surface that would need its own deep review |
| Out-of-sequence correction from the exception queue (reverse-and-reapply re-rated under the restored state) | Cut programme-wide (D-SL3-02). The reviewer records the outcome CORRECTION_REQUIRED; the correction itself comes with the OOS slice |
| WRK review activities | WRK is not built. Recorded on each exception as "not created (WRK not built)", like DOC notices in slice 3 |
| BIL/FIN/RAT/PFC/CMP remediation of the affected window (MKT §14.x.5) | Each module owns its own remediation, and none exists yet. E2E-12 asserts the negative instead: the rollback creates no billing entry, journal or charge delta, and every stored hash is unchanged (D-SL5-12) |
| Scheduled (future) activations, retroactive rollback, hot-fix change requests, config change-request workflow (REQ-MKT-052…066), pack signing/certification gates, deprecation | The thin proof needs immediate activation only. A rollback instant before now is refused (REQ-MKT-139: retroactive needs REQ-MKT-059) |
| Reproduction of pre-slice-5 hashes | No state history existed before slice 5. Older hashes stay `CFG-HASH-UNKNOWN`; dev/demo data only (D-SL5-06) |
| 12 transactions and versions 2.4.0 → 2.3.1 of the PRD example | The thin proof uses 3 bound policies plus 1 open quote, and GR 0.2.0 → 0.1.0 (D-SL5-13) |

## 3. The WIP workbench branch: salvage, do not rebuild (D-SL5-02)

**What is there:** `worktree-agent-ac9c66400c2402b05` is 3 commits ahead of main, on base `9fbb7fc`. Main has moved
47 commits since then.
- **Backend only:**
  - typed `uw.Referral.list/get` in `contracts/openapi/uw.yaml` (+299 lines): queues OPEN / APPROVED_TODAY / REJECTED /
    DECIDED_BY_ME_TODAY, queue counts, reasons, quote header, masked customer, vehicle and driver facts;
  - `ReferralQueries` (291 lines) and `ReferralReads` (160 lines);
  - UW migration `20261008154333_EvaluationFacts`, which stores derived rule facts without personal data: the youngest
    driver's age band, never the birth date;
  - `pol.Job.get` exposed in-process to UW (`x-consumers: UW`, plus `IPolicyJobService.GetAsync`);
  - 7 integration tests: content, queue moves after a decision, permissions, legal-entity isolation, paging, no P2.
- **No web UI.** The existing `web/src/modules/policy/ReferralsPage.tsx` is a plain list.

**Trial merge** (`git merge-tree` of origin/main with the branch): only **2 conflicts**, both hot files that D-PRG-21 has
since split:
- `src/CoreIns.Host/appsettings.json`: move the two `uw.Referral.*` permission lines into
  `src/CoreIns.Host/permissions/uw.json`;
- `tests/CoreIns.Contracts.Tests/GeneratedContractTests.cs`: drop the hand-edited count, because the test now maintains
  it itself.

The generated files, `pol.yaml`, `pol.json` samples and the POL `InProcessServices.cs` merged cleanly. They must still be
regenerated with ContractGen, because SL3-CONTRACTS regenerated every module on main. The UW module and `uw.yaml` are
untouched on main since the branch base, so the domain code lands unchanged.

**Verdict: salvage by `git merge origin/main` into the branch.** Do not rebase: the branch contains a merge commit, and
a rebase would replay it. Then extend it in the same WP (SL5-UW-WB-API). Reasons:
1. It is good, reviewed-shape code that follows the D-UW-01 patterns: legal-entity scoping, masked PTY view, no P2,
   paging cursors, tests.
2. The conflict cost is about 15 minutes.
3. A rebuild would redo about 1,200 lines of non-generated code and the migration for no gain.

The branch has **never been reviewed**. The WP's deep security review covers the salvaged code as well as the new code.

## 3a. Workbench design (D-SL5-03, D-SL5-04, D-SL5-05)

| Mockup element («Ανάληψη κινδύνου») | Built as | Data |
|---|---|---|
| Views rail: «Οι παραπομπές μου» | **MINE** = open referrals whose every open issue the caller can decide (SoD passes + authority dry-run ALLOW) | `uw.Referral.list?queue=MINE` (new) |
| «Ομάδα» | **OPEN** = all open referrals in the caller's legal entity | existing OPEN queue |
| «Λήγουν σήμερα» | **Not shown** (no SLA, D-USR-13) | — |
| «Με όρους σε εκκρεμότητα» | **Not shown** (no conditions) | — |
| «Ολοκληρωμένες σήμερα» | **DECIDED_BY_ME_TODAY** | existing |
| (extra, same style) «Απορρίφθηκαν» | **REJECTED** | existing |
| «Πρόοδος ημέρας» card | decided by me today ÷ (decided by me today + MINE) | queue counts |
| Queue grid: Πελάτης / Προτεραιότητα / SLA / Ασφάλιστρο | Πελάτης (masked name, job number, product) / **Αιτία** (first reason label + "+n") / **Σε αναμονή από** (raisedAt, relative) / Ασφάλιστρο | `ReferralListItem` |
| Detail header: overline, name, ref, «Σε παραπομπή» pill, facts row (premium, sum insured, start, producer) | Same, with vehicle value in place of sum insured | `ReferralView` |
| AI summary card | **Not shown** | — |
| Issue card: title, «Πριν τη δέσμευση» pill, value vs rule limit, «Γιατί;» | One card per issue: issue-type label, blocking point, **observed value** from the stored facts, the **rule limit** where the rule set declares it (`ReferralReason.limit`, new, nullable with a reason), rule id + version; «Γιατί;» opens the rule explanation text | facts + rule set |
| kv list: product/version, package, loss history, payment method, rule, SLA | product/version, claims in the last 5 years, vehicle age/usage/engine, youngest-driver age band, rule id; no SLA | facts |
| Decision bar: authority meter + Reject… / Approve with conditions… / Approve (Ctrl ↵) | Authority line "Your authority: UW.ISSUE_APPROVAL · <type> · from <grant>" with an allow/deny meter; **Reject…** and **Approve…** open a reason field (mandatory); Ctrl+Enter submits only with a reason; disabled with the server's reason when `canDecide = false` (e.g. "you created this quote") | `decidability` (new) |
| Receipt «Απόφαση: <name> · 14:32» + toast with undo | Receipt from `decidedBy`/`decidedAt`; toast **without** undo | issue decision view |
| Empty state «Η ουρά σας είναι άδεια» | Same illustration and text with the real "decided today" count | counts |

**Decidability (D-SL5-04).**
- `uw.Referral.get` returns `decidability` per issue and in total: `{canDecide, reasons[], authority {type, outcome,
  sourceGrantId}}`. The reasons are `SOD_CREATOR`, `SOD_PARTICIPANT`, `SOD_PRODUCER`, `SOD_EVALUATOR`, `NO_AUTHORITY`,
  `NOT_HUMAN` and `NOT_OPEN`.
- It is computed by **the same function `DecideIssues` uses**. Refactor that check into one shared method; there must
  never be two copies.
- The authority check runs as a dry-run, so it writes no check record and no approval.
- The preview never authorises: `uw.Issue.decide` re-runs every check at commit time.
- MINE uses the same function over the open set. It is capped at 500 open referrals per legal entity; above that the
  query refuses with `UW-ERR-VALIDATION` "narrow the view". This is enough for dev volumes; NFR-UW workbench scale
  comes with WRK.

**Permissions (D-SL5-05):** `uw.Referral.list/get` for Staff.UnderwritingManager and Platform.Admin, as on the WIP
branch. `superuser` already holds both. A plain underwriter keeps seeing referral state in the quote wizard.

## 4. E2E-12 design (D-SL5-06 … D-SL5-13)

**Configuration states (D-SL5-06).** MKT today builds one in-memory catalogue at startup, with one hash and no history.
Slice 5 makes the configuration state persistent and append-only:
- `mkt.config_state`: hash PK, parent hash, manifest, `activated_at`, cause GENESIS / PACK_ACTIVATION / PACK_ROLLBACK,
  cause ref.
- `mkt.pack_version`: pack id, semver, content digest and values, shipped in the release.
- `mkt.pack_activation`: entity, version, kind ACTIVATE/ROLLBACK, status, approval request id, resulting hash.

How it behaves:
- **Genesis.** On first start, MKT registers the shipped pack versions (idempotent by digest) and creates the genesis
  state from the newest shipped version of each pack. This keeps today's behaviour for every other slice and for the
  user's `coreins` stack.
- **Reads.** `resolve`, rounding and `TaxCalculator.treatment` all accept a hash, and serve any state recorded since
  slice 5 (REQ-MKT-048, property P-07).
- **Current hash.** The current state is read from the database, cached for at most 1 s, in both api and worker. The
  existing unit-of-work pinning stays (REQ-MKT-051, REQ-POL-088). POL's existing `QUOTE-STALE` check at bind therefore
  starts firing after an activation, with no POL change.
- **Older hashes.** Hashes from before slice 5 stay `CFG-HASH-UNKNOWN`. They are dev data only.

**GR pack versions (D-SL5-07).**
- **0.1.0** is the GR content as shipped before SL3-MKT-TREATMENT: IPT rates and the other pre-slice-3 values. It is
  historical content, not invented.
- **0.2.0** is 0.1.0 plus the GR treatment rows. It is a MINOR bump, because it adds rules (REQ-MKT-129).
- Both ship in the release, because pack code ships and pack data activates (PRD-17 §2 principle 1). Genesis activates
  0.2.0.
- The CY stub is versioned the same way, but nothing activates or rolls it back.

**Activation and rollback (D-SL5-08, REQ-MKT-003/135/137/138/139/144/149).**
1. A release manager calls `mkt.Pack.rollback {pack, legalEntity, toVersion, reason ≥ 20 chars}`.
   - The dry-run preview returns: from/to versions, the affected window [activation of `from`, now), the hashes issued
     in the window, and the key diff (added, removed, changed).
   - A real call creates a `PackActivation` (ROLLBACK, PendingApproval) and an **in-process** PLT approval request.
     Its content hash covers {pack, entity, from, to, kind, reason}. The approval type is `MKT.PackActivation`, and
     the authority is `MKT_PACK_ACTIVATION`.
2. A **different** user with Platform.DesignAuthority decides with `mkt.PackActivation.decide`.
   - The maker cannot approve, and neither can the maker's principal (PITFALLS 5).
   - `verifyForExecution` binds subject + type + content hash (PITFALLS 3).
3. On approval, one transaction:
   - writes the new state (parent = current, cause PACK_ROLLBACK);
   - sets the activation Active and the previous one Superseded;
   - publishes `PackRolledBack` through the outbox, once, with from/to, window, hashes issued and reason.

   States are never deleted or rewritten. `mkt.Pack.scheduleActivation` is the same path with kind ACTIVATE (instant =
   now only) and publishes `PackActivated`.

Refusals:
- an instant before the activation of the version being rolled back (REQ-MKT-139, BR-MKT-016);
- a target version that is not Published;
- a rollback to the version that is already active.

**Product fall-back (D-SL5-09, REQ-PFC-213).**
1. `pfc.ProductVersion.fallback {product, defectiveVersion, reason}` is a maker-checker call: the maker is
   Platform.ReleaseManager. The checker is another user with the `PFC.EmergencyChange` grant, which is illustrative and
   given to Platform.DesignAuthority.
2. On approval it publishes **MOTOR-GR 1.2**:
   - a write-once copy of 1.0's content (ACT/365F, no refund methods), with `fallbackOf: 1.0` and `replaces: 1.1`;
   - a new-business window from the fall-back business date;
   - 1.1's new-business window ends on that date, in the same transaction (REQ-PFC-033).
3. 1.1 terms stay on 1.1, and 1.1 is still retrievable by hash. Renewal resolution is unchanged: the renewal window is
   not touched (REQ-PFC-166/167).
4. RAT activates the source version's rating artefact for 1.2 on `ProductVersionPublished{fallbackOf}`. It is the same
   artefact hash, so the tariff is not invented.

**POL on `PackRolledBack` (D-SL5-10, REQ-POL-354 subset).** The handler is idempotent on `event_id`. It:
- finds every **bound transaction** and every **open quote version** whose stored `configuration_hash` is in the event's
  "hashes issued" list;
- writes one exception row per affected bound transaction (unique on activation + transaction), with policy, term,
  hash, product version, and `wrkActivity: NOT_CREATED_WRK_NOT_BUILT`;
- marks the open quote versions `staleReason = PACK_ROLLBACK`. Their bind is refused already by `QUOTE-STALE`.

It never touches segments, charges or terms, so there are no deltas and no watermark bump.
- `pol.PackRollbackException.list/get` serve the queue.
- `pol.PackRollbackException.review {outcome NO_ACTION | CORRECTION_REQUIRED, reason}` is for Staff.UnderwritingManager
  (illustrative) and is audited.
- The correction itself is OUT (D-SL3-02).

**In-term transactions after a rollback (D-SL5-11).**
- A later transaction on an old term runs under the **current, restored** configuration.
- It keeps the term's pinned product and rating artefacts (REQ-POL-033, REQ-RAT-063, REQ-POL-088). The configuration
  pinned on the term records what was in force at bind; it is not a licence to keep using a withdrawn state.
- The consequence: a cancellation quote on a term bound under 0.2.0, made after the rollback to 0.1.0, **fails closed**
  with `RULE_MISSING`, because 0.1.0 has no treatment rule. It works again once a pack with the rules is re-activated.
  E2E-12 asserts both the refusal and the recovery.
- Open question Q1 asks the user to confirm.

### E2E-12 step table (PRD-18-B §4 E2E-12, cut)

Setup: an isolated stack `coreins-e2e12` (ports 28000+, image tag `coreins-host:e2e12`). The System clock is used; no
time shift is needed. Genesis has GR **0.2.0** active (hash H2), and MOTOR-GR 1.1 resolves for terms starting on or
after 2026-10-01 (D-SL3-15).

| # | Step | Owner (WP) | Requirement ids | Assertion |
|---|---|---|---|---|
| 1 | Genesis state and registry | MKT (STATE) | REQ-MKT-003, -046, -047, -125 (subset) | `mkt.Pack.list` shows GR 0.1.0 and 0.2.0 Published, 0.2.0 Active; current hash H2; `treatment(CANCELLATION, Policyholder)` → `GR-TRT-IPT-CANCEL-POLICYHOLDER` |
| 2 | Bind 3 policies under H2 / 1.1 (one through a referral decided in the workbench by `uwsenior`); leave 1 full quote open | POL, UW (existing) | REQ-POL-033, -087, -088; D-UW-01 | Each term pins H2, product 1.1, its artefact hash, premium; invoices and journals as E2E-01 |
| 3 | Rollback request with dry-run | MKT (ROLLBACK) | REQ-MKT-136 (subset), -138, -144 | Preview: from 0.2.0, to 0.1.0, window [genesis, now), hashes [H2], key diff = the 8 `tax.treatment.rule.*` keys removed |
| 4 | Maker cannot approve; `designauth` approves | MKT, PLT | REQ-MKT-137, REQ-PLT-004; PITFALLS 3–5 | Self-approval `PLT-ERR-SELF-APPROVAL`; approval → new state H3 (parent H2, cause PACK_ROLLBACK); exactly one `PackRolledBack` |
| 5 | Reproduction | MKT (STATE) | REQ-MKT-048, -051; P-07 | `resolve(H2)` still returns the treatment rows; under H3 `treatment` → `RULE_MISSING`; H2's manifest unchanged |
| 6 | Product fall-back 1.1 → 1.2 | PFC (FALLBACK), RAT (FALLBACK) | REQ-PFC-213, -033, -178 | Resolve by today's date → 1.2 (ACT/365F); 1.1 resolvable by hash; the 3 terms still pin 1.1 |
| 7 | Exception queue, stale quote, nothing re-rated | POL (ROLLBACK) | REQ-POL-354 (subset), -033 | Queue lists exactly the 3 bound transactions; the open quote `staleReason = PACK_ROLLBACK` and its bind → `QUOTE-STALE`; terms, premiums, charges unchanged; **no** new `ChargeDeltaEmitted`, `BillingEntryPosted` or journal since step 3 |
| 8 | Review outcome | POL, UI (PACKS) | REQ-POL-354 (subset), REQ-PLT-002 | `uwsenior` records NO_ACTION on one and CORRECTION_REQUIRED on another, with reasons; audit events present; exception shows `wrkActivity NOT_CREATED_WRK_NOT_BUILT` |
| 9 | New business after the rollback | POL, RAT, BIL, FIN (existing) | REQ-POL-088, REQ-MKT-051 | New quote and bind pin H3 and product 1.2; invoice and balanced journals |
| 10 | In-term transaction on an H2 term | POL (slice-3 CANCEL), MKT | D-SL5-11 | Cancellation quote → `RULE_MISSING` (fail closed, explained); after `scheduleActivation` of 0.2.0 (maker-checker again, new state H4, `PackActivated`) the same quote succeeds with the KEEP_NOT_REDUCED treatment |
| 11 | BIL/FIN affected-window handling | — | MKT §14.x.5 | OUT (D-SL5-12); covered by the negative assertions of step 7 |
| 12 | Audit | all | REQ-PLT-002, REQ-MKT-066 | One audit event per request, decision, activation, identification and review |

UI check (Playwright, short): as `releasemgr` open the pack registry and see the rolled-back version marked; as
`uwsenior` open the exception queue and record one review.

## 5. Waves, WPs and the critical path

Dependency notation: **C** = typed contracts only (code against generated fakes); **M:<WP>** = that WP must be merged;
**G:<slice-3 WP>** = gated on a slice-3 merge. Reviews: **deep** = adversarial with probe tests; **light** = checklist
(plus visual for UI). The estimates are builder wall-clock hours. All agents run on Sonnet 5.5.

### Wave 1 — PARALLEL-NOW (5 WPs; start as slots allow)

| WP | Module (owned area) | Scope | PRD / REQ | Depends on | Review | PITFALLS | Est. |
|---|---|---|---|---|---|---|---|
| **SL5-UW-WB-API** | UW (all of `Modules.Underwriting`, **UW migration owner**), `uw.yaml`, `permissions/uw.json` | Salvage the WIP branch (§3); **first commit: contract additions** (queue MINE, `decidability`, `ReferralReason.limit`, `observed`), so the UI can start; shared SoD + authority check extracted from `DecideIssues` and reused for the preview and MINE; rule-limit metadata for the three motor referral rules | REQ-UW-078, -010 (preview subset), -111, SOD-UW-02, BR-UW-013; D-UW-01 | none (salvage) | **deep (security)** | 1–6, 12, 15, 18, 21–23 | 3.5 h |
| **SL5-UI-UW-WB** | `web/src/modules/underwriting/**` (new) | The «Ανάληψη κινδύνου» workbench per §3a: views rail with counts and day progress, queue grid, detail panel, decision bar with reason, keyboard (↑/↓, Enter, Ctrl+Enter), receipt, empty state; route `policies/referrals` swapped to the workbench; rail entry | SCR-UW-01 (subset), SCR-UW-03 (subset); D-USR-11/13 | C (WIP + first commit of UW-WB-API); live walk after M:UW-WB-API | light + **visual** | 18, 19, 25–28 | 4 h |
| **SL5-CONTRACTS-PACKS** | `contracts/openapi/mkt.yaml`, `pfc.yaml`, **new paths only** in `pol.yaml`, `contracts/events/` (new event schemas), generated `*.Contracts` of MKT/PFC/POL | Type §6 (MKT pack ops and events, PFC fallback, POL exception queue, `staleReason`), samples valid | D-API-06/06a | none | light | 12, 21, 22 | 2 h |
| **SL5-PFC-FALLBACK** | PFC (`Modules.Product`, **PFC migration owner**), `permissions/pfc.json`, PFC authority file | `pfc.ProductVersion.fallback` (dry-run) + `decideFallback` with in-process PLT approval; write-once copy as the next minor version; window close in one transaction; `ProductVersionPublished{fallbackOf, replaces}`; approval type `PFC.Fallback`, authority `PFC.EmergencyChange` (illustrative grant) | REQ-PFC-213, -033, -166, -178; PRD-02 §12.2/12.3 | C | **deep (config/temporal + security)** | 3–5, 10, 17, 21, 24 | 3 h |
| **SL5-PLT-ROLES** | dev-users file, role catalogue | Roles Platform.ReleaseManager, Platform.DesignAuthority; dev users `releasemgr` ("Dev Release Manager (synthetic)") and `designauth` ("Dev Design Authority (synthetic)"); `superuser` gains both (SoD unchanged); sign-in test | PRD-17 ROLE-40; REQ-MKT-137 | **G:SL3-PLT-SUPPORT** (owns the dev-users file in slice 3) | light + security checklist; **batched** (D-USR-18) with SL5-CONTRACTS-PACKS | 4, 5, 30 | 1 h |

### Wave 2 — GATED on slice-3 S0 merges (3 WPs)

| WP | Module (owned area) | Scope | PRD / REQ | Depends on | Review | PITFALLS | Est. |
|---|---|---|---|---|---|---|---|
| **SL5-MKT-STATE** | MKT (`Modules.Market`, **MKT migration owner**), `CountryPacks.GR/Configuration/**`, `CountryPacks.CY/**` (version split only) | §4 D-SL5-06/07: persisted states + hash chain, pack-version registry from shipped data (GR 0.1.0 and 0.2.0), genesis, hash-aware resolve/rounding/treatment, current-state cache ≤ 1 s in api and worker, `mkt.Pack.list/get`; prove that new business quotes and binds under 0.1.0 content | REQ-MKT-003 (subset), -046, -047, -048, -050, -051, -125 (subset), -129; P-06, P-07 | C; **G:SL3-MKT-TREATMENT** | **deep (config/temporal)** | 10, 13, 17, 21, 24, 36, 37 | 4.5 h |
| **SL5-RAT-FALLBACK** | RAT (`Modules.Rating` activation path only) | Consume `ProductVersionPublished{fallbackOf}` → activate the source version's artefact for the fall-back version (same hash), idempotent; refuse when the source has no active artefact | REQ-PFC-213 (RAT side), REQ-RAT-063 | C; **G:SL3-RAT-PRORATE** | light (+ config checklist) | 10, 12, 21 | 1.5 h |
| **SL5-UI-PACKS** | `web/src/modules/market/packs/**` (new), `web/src/modules/policy/packRollback/**` (new) | Pack registry (packs, versions, active per entity, activation history with rolled-back marking); rollback request with preview and reason; approve/reject for the checker; exception queue + review dialog | SCR-MKT-03 (subset), REQ-MKT-295 (subset); REQ-POL-354 (UI subset) | C; live walk after M:MKT-ROLLBACK, M:POL-ROLLBACK | light + **visual** | 18, 25–28 | 3 h |

### Wave 3 — GATED (2 WPs)

| WP | Module (owned area) | Scope | PRD / REQ | Depends on | Review | PITFALLS | Est. |
|---|---|---|---|---|---|---|---|
| **SL5-MKT-ROLLBACK** | MKT commands (`Commands/Packs*`), **MKT migration owner (wave 3)**, `permissions/mkt.json`, MKT authority file | `mkt.Pack.rollback` / `scheduleActivation` (dry-run preview), `mkt.PackActivation.decide/list/get`, in-process PLT approval bound to content hash, atomic state write + outbox `PackRolledBack`/`PackActivated`, REQ-MKT-139 refusals, audit | REQ-MKT-003, -135, -136 (subset), -137, -138, -139, -144, -149; BR-MKT-016 | M:MKT-STATE, M:PLT-ROLES | **deep (config/temporal + security)** | 3–6, 9, 10, 12, 15, 17 | 4 h |
| **SL5-POL-ROLLBACK** | POL `Events/PackRollback*`, `Commands/PackRollback/**`, `Api/PackRollbackController.cs`, **POL migration owner (slice 5)** | §4 D-SL5-10: consumer, identification by stored hash, exception rows, stale quote marking, list/get/review, audit; no segment/charge/term writes | REQ-POL-354 (subset), -033, -087 | C; **G: slice-3 POL S1 merged** (SL3-POL-TEMPORAL owns the POL schema for all of slice 3; the builder may code earlier and add the migration last) | **deep (config/temporal)** | 9, 10, 12, 15, 17, 21, 22 | 3.5 h |

### Wave 4 — integration (1 WP)

| WP | Module (owned area) | Scope | Depends on | Review | PITFALLS | Est. |
|---|---|---|---|---|---|---|
| **SL5-E2E** | `tests/e2e/tests/e2e12*`, `uw-workbench*`, `tests/e2e/run-e2e12.sh` (executable), `.github/workflows/ci.yml` (**new `e2e12` block only**), `infra/local/seed-demo.py --packs` (flag only) | E2E-12 API spec (§4 table), short Playwright checks (registry, exception queue review), workbench Playwright spec (referred quote → `uwsenior` decides in the workbench → `underwriter` binds; self-decision refused with the explained reason); CI job `e2e12` | M: all slice-5 WPs; **G:SL3-E2E** (owns `ci.yml` in slice 3) | light (integration) | 29–35 | 4.5 h |

### Critical path

**G:SL3-MKT-TREATMENT → SL5-MKT-STATE → SL5-MKT-ROLLBACK → SL5-E2E.** POL-ROLLBACK runs alongside, gated on the end of
the slice-3 POL work. The workbench (UW-WB-API → UI-UW-WB) is independent and finishes first. It is usable about 6–7 h
after the start.

## 6. Interface-first contracts

All changes are additive. Every field that a consumer depends on is **always set** (PITFALLS 12).

| Module | Operations (typed request/response/errors) | Events | Typed by |
|---|---|---|---|
| UW | `uw.Referral.list` queue `MINE`; `ReferralView.decidability {canDecide, reasons[], authority}` and per issue; `ReferralReason.observed`, `.limit` (nullable + `limitUnavailableReason`); error `UW-ERR-VALIDATION` "narrow the view" | — | SL5-UW-WB-API (first commit) |
| MKT | `mkt.Pack.list/get`; `mkt.Pack.rollback` and `mkt.Pack.scheduleActivation` (dry-run → preview {from, to, window, hashesIssued[], keyDiff[]}); `mkt.PackActivation.decide/list/get`; `mkt.Configuration.resolve` documented for any slice-5 hash; errors `MKT-ERR-PACK-VERSION-UNKNOWN`, `-PACK-NOT-PUBLISHED`, `-PACK-ALREADY-ACTIVE`, `-PACK-ROLLBACK-INSTANT`, `-PACK-ACTIVATION-STATE` | `PackActivated`, `PackRolledBack` (pack, entity, fromVersion, toVersion, window {from, to}, hashesIssued[], reason, activationId, resultingHash) | SL5-CONTRACTS-PACKS |
| PFC | `pfc.ProductVersion.fallback` (dry-run → preview {newVersion, source, windows}); `pfc.ProductVersion.decideFallback`; errors `PFC-ERR-FALLBACK-SOURCE`, `-FALLBACK-STATE` | `ProductVersionPublished` + `fallbackOf`, `replaces` | SL5-CONTRACTS-PACKS |
| POL | `pol.PackRollbackException.list/get/review`; quote version view `staleReason` (`PACK_ROLLBACK` / null) | — | SL5-CONTRACTS-PACKS |
| PLT | none (approval types and authority types are registered by MKT/PFC in their own files) | — | — |

## 7. Parallelism and conflict map

Rule: no two concurrent WPs, **across slices 3, 4 and 5**, touch the same DbContext, migration folder or generated
contract folder. One migration owner per module per wave.

| WP | Owns (files/folders) | Shared files touched | Conflicts with slice 3 / 4 | Run |
|---|---|---|---|---|
| SL5-UW-WB-API | `Modules.Underwriting/**` (migration owner), `contracts/openapi/uw.yaml`, generated UW contracts, `tests/…/Underwriting/**`, `permissions/uw.json` | `pol.yaml` (one `x-consumers` line), generated `IPolicyJobService.cs`, POL `Services/InProcessServices.cs` (one method, already written), INDEX/samples (regenerate) | No slice-3/4 WP owns UW. The POL in-process method is additive; **re-merge main and regenerate just before the PR** | **PARALLEL-NOW** |
| SL5-UI-UW-WB | `web/src/modules/underwriting/**`, `underwriting/i18n/*` | `web/src/routes.tsx` (swap the one `policies/referrals` element line), staff shell rail (append one entry) | Slice-3 UI WPs append routes; disjoint folders. `policy/ReferralsPage.tsx` is left in place (deleted later by the orchestrator) | **PARALLEL-NOW** |
| SL5-CONTRACTS-PACKS | `mkt.yaml`, `pfc.yaml`, `contracts/events/pack-*.schema.json`, generated MKT/PFC contracts | `pol.yaml` (new paths/schemas **appended**, no edits of existing ones), generated POL contracts, INDEX/samples | Slice-3 POL S1 WPs may add `pol.yaml` fields: append-only on both sides, regenerate on conflict. Slice-4 contract work touches `clm.yaml`/`ri.yaml` only | **PARALLEL-NOW** (land in a quiet window) |
| SL5-PFC-FALLBACK | `Modules.Product/**` except `Seed/motor-gr*.json` (migration owner), `permissions/pfc.json`, PFC authority file | none | SL3-PFC-MOTOR11 is merged; no other slice-3/4 PFC WP. If slice 4 schedules PFC work, serialise | **PARALLEL-NOW** |
| SL5-PLT-ROLES | dev-users file (append), role catalogue (append) | `dev-users.Development.json` | SL3-PLT-SUPPORT owns the dev-users file in slice 3: **wait for its merge**; append-only after that | GATED (short) |
| SL5-MKT-STATE | `Modules.Market/**` (migration owner, wave 2), `CountryPacks.GR/Configuration/**`, `CountryPacks.CY/**` | `Host/Hosting/CountryPackBinding.cs` (registration only) | SL3-MKT-TREATMENT owns these folders: **wait for its merge**. Slice 4: if E2E-08 (withdrawal void routing) lands in slice 4 and touches MKT, serialise after this WP | GATED |
| SL5-RAT-FALLBACK | `Modules.Rating/Events/ProductVersion*`, activation service (one file) | `RatingModule.cs` (one DI line) | SL3-RAT-PRORATE owns RAT services: **wait for its merge** | GATED |
| SL5-UI-PACKS | `web/src/modules/market/packs/**`, `web/src/modules/policy/packRollback/**`, own i18n namespaces | `routes.tsx` (append) | Never edits `policy/file/**`, `policy/servicing/**` (slice 3) | PARALLEL-NOW (contracts) |
| SL5-MKT-ROLLBACK | `Modules.Market/Commands/Packs*`, `Api/PacksController.cs`, migration (owner, wave 3), `permissions/mkt.json`, MKT authority file | none | after MKT-STATE | GATED |
| SL5-POL-ROLLBACK | `Modules.Policy/Events/PackRollback*`, `Commands/PackRollback/**`, `Api/PackRollbackController.cs`, POL migration (owner, slice 5) | `PolicyModule.cs` (one DI block), `permissions/pol.json` (append) | **Wait until slice-3 POL S1 (CHANGE, CANCEL, RENEW) and any TEMPORAL follow-up merge.** If slice 4 has a POL WP, serialise | GATED |
| SL5-E2E | `tests/e2e/tests/e2e12*`, `uw-workbench*`, `run-e2e12.sh`, `infra/local/seed-demo.py` (one new flag) | `ci.yml` (new `e2e12` block only) | SL3-E2E owns `ci.yml` and `seed-demo.py` in slice 3: **wait for its merge**; slice 4's E2E adds its own block | GATED |

**Shared files and their owner in slice 5:**

| File | Rule |
|---|---|
| `appsettings*.json` | Untouched (D-PRG-21) |
| `Program.cs` | Not touched by slice 5 |
| `routes.tsx`, staff shell rail, `PolicyModule.cs`, per-module permission/authority files | Append-only; on a conflict keep both sides. Exception: SL5-UI-UW-WB swaps the single `policies/referrals` element line |
| `ci.yml` | Only SL5-E2E (`e2e12` block), after SL3-E2E merged |
| `dev-users.Development.json` | Only SL5-PLT-ROLES, after SL3-PLT-SUPPORT merged |
| `DECISIONS.md`, `STATUS.md`, `HANDOVER.md`, `PITFALLS.md` | Orchestrator only |

## 8. Rolling schedule

The t values are wall-clock hours from the slice-5 start. Slice 5 takes 3–4 builder slots within the global cap. The
schedule assumes that slice-3 S0 (MKT-TREATMENT, RAT-PRORATE, PLT-SUPPORT) merges within about 2–3 h, and slice-3 POL
S1 within about 8–10 h.

| t (h) | Merging / in review | Starts building |
|---|---|---|
| 0 | — | Wave 1: UW-WB-API, UI-UW-WB (on the WIP contract), CONTRACTS-PACKS, PFC-FALLBACK (4) |
| 0.5–1 | UW-WB-API pushes its contract commit | UI-UW-WB rebases on it |
| 2 | CONTRACTS-PACKS PR → light review → merge, **batched with PLT-ROLES** once SL3-PLT-SUPPORT is in (D-USR-18) | PLT-ROLES (1 h) as soon as its gate opens |
| 2.5–3 | — | MKT-STATE (gate SL3-MKT-TREATMENT); RAT-FALLBACK (gate SL3-RAT-PRORATE) as slots free |
| 3.5–4.5 | UW-WB-API PR → **deep security review**; PFC-FALLBACK PR → **deep review** | UI-PACKS (contracts) |
| 5–6 | UW-WB-API fixes → merged; UI-UW-WB live walk → PR (visual review, side-by-side shots) | — |
| 6–7 | UI-UW-WB merged: **the workbench is usable** (user FYI screenshots); PFC-FALLBACK merged; RAT-FALLBACK PR (light) → merged | — |
| 7–8.5 | MKT-STATE PR → **deep review** → fixes → merged | MKT-ROLLBACK |
| 8–10 | (slice-3 POL S1 merges) | POL-ROLLBACK |
| 9–12 | UI-PACKS PR (contract fakes; live walk later) | SL5-E2E phase A (harness, API spec against merged parts; gate SL3-E2E for `ci.yml`) |
| 12–14 | MKT-ROLLBACK PR → **deep review** → fixes → merged; POL-ROLLBACK PR → **deep review** → fixes → merged | UI-PACKS live walk |
| 14–17 | UI-PACKS merged; SL5-E2E phase B (Playwright), integration fixes; `e2e12` green on CI | Acceptance walk in real Chrome (FYI screenshots) |

**Estimate:**
- About 35 builder hours across 11 WPs.
- About 6 review hours: 5 deep reviews at about 45 min each, 6 light ones at about 25 min.
- About 6 hours of fix rounds.
- That gives roughly 47 agent-hours in total, or **about 16–18 wall-clock hours** at 3–4 slots.
- The UW workbench part finishes in about 6–7 h. The rest is set by the slice-3 gates on MKT and POL.
- If slice 3's POL S1 slips, POL-ROLLBACK and E2E slip with it, one for one.

## 9. Rules for every slice-5 builder

- Everything in HANDOVER §4 applies. Briefs are in `orchestration/briefs/sl5/` and include `_COMMON.md`.
- Each brief starts with "Runs on Sonnet 5.5 (model: sonnet), D-USR-16". Reviewers run on Sonnet 5.5 too, with the
  depth stated in the brief.
- Regulatory values only from the PRDs, each with its `legalStatus`. Slice 5 adds **no regulatory value**:
  - GR 0.1.0 and 0.2.0 are existing content;
  - 1.2 is a copy of 1.0;
  - the grants and roles are **illustrative**.
- UI: the Aegean mockup v3 «Ανάληψη κινδύνου» screen is binding for the workbench (D-USR-11/13). It has no pack screen,
  so the pack registry and exception queue follow the **claims approvals inbox** pattern (list + detail + decision
  bar). Side-by-side screenshots go in `orchestration/ux/<wp>/`.
- Commit early (PITFALLS 34). Push your branch and open a PR, or for small WPs push only (D-USR-18). The orchestrator
  merges.

## 10. Open questions

| # | Question | Default taken (so nobody waits) |
|---|---|---|
| Q1 | After a rollback, should in-term transactions on terms bound under the rolled-back pack use the restored configuration (so a cancellation fails closed without treatment rules), or the term's pinned hash? | Restored configuration; fail closed; recovery by re-activating a pack with the rules (D-SL5-11). PRD basis: REQ-POL-088 / REQ-MKT-051 pin per unit of work; REQ-POL-354 re-rates corrections "under the restored configuration" |
| Q2 | After slice 3, does new-business rating need a `NEW_BUSINESS` treatment row? If yes, GR 0.1.0 blocks new business after the rollback | From the SL3-RAT-PRORATE code read, no: treatment is used only on the servicing tax path. SL5-MKT-STATE proves it with a test; if wrong, the orchestrator decides (stop the proof at step 8, or roll back to a 0.1.1 that holds the PRD's APPLY rows) |
| Q3 | "Οι παραπομπές μου" as "referrals I can decide" vs. real assignment | Can-decide (REQ-UW-078); assignment arrives with WRK |
| Q4 | Who reviews rollback exceptions? | Staff.UnderwritingManager, illustrative (PRD: a WRK review activity, role unnamed) |
| Q5 | Fall-back version number: PRD example "3.3 copy of 3.2" when 4.0 is defective; for 1.1 defective, 1.1 is taken | Next free minor: **1.2**, with `fallbackOf: 1.0` |

## 11. Proposed decisions (append to DECISIONS.md)

| ID | Decision | Reason | Status |
|---|---|---|---|
| D-SL5-01 | **Slice 5 scope (planned 2026-10-08, `SLICE-PLAN-5.md`).**<br>**In:**<br>- UW referral workbench (salvaged read model, decidability preview, MINE view, mockup screen);<br>- MKT configuration-state history and pack registry;<br>- pack activation/rollback with maker-checker and `PackActivated`/`PackRolledBack`;<br>- PFC fall-back version (REQ-PFC-213) with RAT activation;<br>- POL rollback exception queue (REQ-POL-354 subset);<br>- thin registry/exception UI;<br>- E2E-12 + workbench UI spec, CI job `e2e12`.<br>**Out:** referral assignment/routing/priority/SLA (WRK not built); approve with conditions; AI summary; bulk decide; undo; OOS correction (D-SL3-02); WRK activities; BIL/FIN/RAT/CMP window remediation; scheduled/retro activation, change requests, signing/certification; pre-slice-5 hash reproduction | Thin slice over what exists; the WRK, OOS and change-request machinery are their own waves | Made |
| D-SL5-02 | **The WIP workbench branch `worktree-agent-ac9c66400c2402b05` is salvaged, not rebuilt.**<br>- Merge `origin/main` into it; do not rebase, because it holds a merge commit.<br>- A trial merge shows 2 conflicts, both D-PRG-21 hot files: move the `uw.Referral.*` permissions to `permissions/uw.json`; drop the hand count in `GeneratedContractTests.cs`.<br>- Regenerate contracts. Extend the branch in SL5-UW-WB-API.<br>- The code was never reviewed; the WP's deep security review covers it | UW and `uw.yaml` are untouched on main since the base; about 1,200 lines of sound code, a migration and 7 tests would be redone for nothing | Made |
| D-SL5-03 | **Workbench views map to real data only.**<br>- «Οι παραπομπές μου» = open referrals the caller can decide (REQ-UW-078);<br>- «Ομάδα» = all open referrals in the legal entity;<br>- «Ολοκληρωμένες σήμερα» = decided by me today;<br>- plus «Απορρίφθηκαν».<br>- "Due today" and "conditions pending" are not shown.<br>- The priority and SLA columns become reason and waiting-since.<br>- No AI summary, sanctions card, presence, bulk select or undo | D-USR-13: no priority, SLA or AI until the features exist; PRD-04 assignment goes through WRK | Made |
| D-SL5-04 | **Decidability preview = the decide check, run dry.**<br>- One shared method computes SoD (creator, editors/quoter, producer, evaluator, non-human) and the `UW.ISSUE_APPROVAL` authority as a dry-run.<br>- `uw.Referral.get` returns it per issue, and MINE filters by it.<br>- `uw.Issue.decide` re-runs it at commit; the preview never authorises.<br>- MINE is capped at 500 open referrals per entity (`UW-ERR-VALIDATION` above) | One source of truth for SoD; the UI explains why a decision is disabled; no second code path to launder (PITFALLS 5–6) | Made |
| D-SL5-05 | **Workbench access:** `uw.Referral.list/get` for Staff.UnderwritingManager and Platform.Admin only, as on the WIP branch. Underwriters keep the referral state in the quote wizard | Least privilege; the deciders are managers (D-UW-01) | Made |
| D-SL5-06 | **MKT configuration states are persisted and append-only.**<br>- Hash chain with parent, cause GENESIS/PACK_ACTIVATION/PACK_ROLLBACK.<br>- Pack-version registry from the data shipped in the release; genesis activates the newest shipped version per pack.<br>- `resolve`, rounding and `treatment` serve any state recorded since slice 5 (REQ-MKT-048, P-07).<br>- The current state is cached ≤ 1 s in api and worker; unit-of-work pinning unchanged (REQ-MKT-051).<br>- Pre-slice-5 hashes stay `CFG-HASH-UNKNOWN` (dev data only) | Rollback needs history; keeps other slices' behaviour at genesis | Made |
| D-SL5-07 | **GR pack versions are existing content.** 0.1.0 = the GR values as shipped before SL3-MKT-TREATMENT; 0.2.0 = 0.1.0 + the GR treatment rows (MINOR, REQ-MKT-129). Genesis activates 0.2.0. No value is invented or changed | E2E-12 must roll back the slice-3 pack data without inventing a "defective" value | Made |
| D-SL5-08 | **Pack activation and rollback.**<br>- Immediate instant only.<br>- Maker-checker in every environment: the maker is Platform.ReleaseManager, the checker another user with Platform.DesignAuthority. Both are new roles; dev users `releasemgr`/`designauth`; `superuser` gains both, with SoD unchanged.<br>- An in-process PLT approval of type `MKT.PackActivation`, bound to the content hash of {pack, entity, from, to, kind, reason}; authority `MKT_PACK_ACTIVATION` (illustrative grant).<br>- One transaction writes the new state and the outbox event.<br>- Refused: an instant before the activation of the version rolled back (REQ-MKT-139), an unpublished target, a no-op rollback.<br>- States are never rewritten | REQ-MKT-137/138/139; PITFALLS 3–5 | Made |
| D-SL5-09 | **Product fall-back (REQ-PFC-213).**<br>- **MOTOR-GR 1.2** = a write-once copy of 1.0 content, with `fallbackOf: 1.0` and `replaces: 1.1`; new-business window from the fall-back date.<br>- 1.1's new-business window ends that date, in the same transaction; renewal windows untouched.<br>- Maker-checker: approval type `PFC.Fallback`, authority `PFC.EmergencyChange` (illustrative grant to Platform.DesignAuthority).<br>- RAT activates the source version's artefact (same hash) for 1.2 | PRD "3.3 copy of 3.2" pattern; the next free minor is 1.2; no tariff invented | Made |
| D-SL5-10 | **POL on `PackRolledBack` (REQ-POL-354 subset).**<br>- Identifies bound transactions and open quote versions by stored `configuration_hash` ∈ the hashes issued in the window.<br>- One exception row per affected transaction, with `wrkActivity NOT_CREATED_WRK_NOT_BUILT`.<br>- Open quotes get `staleReason PACK_ROLLBACK`; bind already refuses them with `QUOTE-STALE`.<br>- A reviewer records NO_ACTION or CORRECTION_REQUIRED with a reason (audited; Staff.UnderwritingManager, illustrative).<br>- No segment, charge or term is touched, and nothing is re-rated; the OOS correction is out (D-SL3-02) | "Never re-rate silently"; WRK and OOS not built | Made |
| D-SL5-11 | **In-term transactions after a rollback use the current (restored) configuration.** They keep the term's pinned product and rating artefacts (REQ-POL-033, REQ-RAT-063). A cancellation on a term bound under GR 0.2.0, quoted after the rollback to 0.1.0, fails closed with `RULE_MISSING` until a pack with treatment rules is re-activated; E2E-12 asserts both | REQ-POL-088/REQ-MKT-051 pin per command; REQ-POL-354 re-rates corrections under the restored configuration. User to confirm (Q1) | Made |
| D-SL5-12 | **No BIL/FIN/RAT/CMP remediation of the rollback window in slice 5.** E2E-12 asserts the negative: after the rollback there is no new charge delta, billing entry or journal, and the stored hashes on terms, invoices and journals are unchanged | Each module owns its remediation (PRD-17 `PackRolledBack` row); none is specified for the thin proof | Made |
| D-SL5-13 | **E2E-12 thin cut.** 3 bound policies + 1 open quote (PRD: 12 transactions); GR 0.2.0 → 0.1.0 (PRD example 2.4.0 → 2.3.1); product fall-back 1.1 → 1.2; recovery by re-activating 0.2.0. Isolated stack `coreins-e2e12` with the System clock; CI job `e2e12`; workbench Playwright spec in the same job | Proves pinning, reproduction, the exception queue and new-business routing without OOS | Made |
| D-SL5-14 | **Slice-5 process.**<br>- 11 WPs in 4 waves; at most 4 builder slots, inside the global 6–8 cap shared with slices 3 and 4.<br>- **PARALLEL-NOW:** UW-WB-API, UI-UW-WB, CONTRACTS-PACKS, PFC-FALLBACK, UI-PACKS.<br>- **GATED** on slice-3 merges: PLT-ROLES (SL3-PLT-SUPPORT), MKT-STATE (SL3-MKT-TREATMENT), RAT-FALLBACK (SL3-RAT-PRORATE), POL-ROLLBACK (slice-3 POL S1), E2E (SL3-E2E).<br>- Deep first-round reviews: UW-WB-API (security), PFC-FALLBACK, MKT-STATE, MKT-ROLLBACK, POL-ROLLBACK (config/temporal; maker-checker security).<br>- **Every builder and reviewer runs on Sonnet 5.5** (D-USR-16) | Use idle modules without colliding with slices 3/4 | Made |

## 12. STATUS rows (for `STATUS.md`, new section "Phase 6 — slice 5 (planned)")

Plan: `SLICE-PLAN-5.md`. Decisions: D-SL5-01..14. Briefs: `briefs/sl5/` (waves 1 and 2). Every builder and reviewer
runs on Sonnet 5.5 (D-USR-16). The UW workbench WIP branch `worktree-agent-ac9c66400c2402b05` is salvaged by
SL5-UW-WB-API.

| WP | Wave | Module | Depends on | Review | Est. | Parallel with sl3/sl4 | Status |
|---|---|---|---|---|---|---|---|
| SL5-UW-WB-API | 1 | UW (+ WIP salvage) | — | deep (security) | 3.5 h | yes | planned |
| SL5-UI-UW-WB | 1 | web underwriting | contracts (UW-WB-API first commit) | light + visual | 4 h | yes | planned |
| SL5-CONTRACTS-PACKS | 1 | contracts mkt/pfc/pol(new paths)/events | — | light | 2 h | yes (quiet window) | planned |
| SL5-PFC-FALLBACK | 1 | PFC | contracts | deep (config + security) | 3 h | yes | planned |
| SL5-PLT-ROLES | 1 | PLT dev users/roles | merged SL3-PLT-SUPPORT | light (batched) | 1 h | gated | planned |
| SL5-MKT-STATE | 2 | MKT + GR/CY pack data | merged SL3-MKT-TREATMENT | deep (config/temporal) | 4.5 h | gated | planned |
| SL5-RAT-FALLBACK | 2 | RAT | merged SL3-RAT-PRORATE; contracts | light | 1.5 h | gated | planned |
| SL5-UI-PACKS | 2 | web market/packs, policy/packRollback | contracts (live walk later) | light + visual | 3 h | yes | planned |
| SL5-MKT-ROLLBACK | 3 | MKT | merged MKT-STATE, PLT-ROLES | deep (config + security) | 4 h | gated | planned |
| SL5-POL-ROLLBACK | 3 | POL | merged slice-3 POL S1; contracts | deep (config/temporal) | 3.5 h | gated | planned |
| SL5-E2E | 4 | tests/e2e, ci.yml (e2e12) | all slice-5 merged; merged SL3-E2E | light (integration) | 4.5 h | gated | planned |
