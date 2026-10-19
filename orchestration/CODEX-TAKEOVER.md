# Codex takeover — 9 October 2026

The user authorized subagents to assess and recover Claude's interrupted work. This is the current checkpoint; older phase tables in STATUS/HANDOVER remain historical.

## Verified outcomes

| Work | Outcome |
|---|---|
| Claims banner #37 | Merged 7d470d9; focused tests and current-code visual review passed. |
| Refund backend #39 | Merged b83b72a; recovered unpublished work, build and focused integration gates passed. |
| Servicing UI #28 | Merged b501f88. Real acceptance subsequently exposed defects corrected in #46. |
| Refund UI #34 | Merged 5dd4336; typing, exact money sums and user-facing translations corrected. |
| Tax identity #44 | Merged ae43539; real RAT/MKT tax-code mismatch corrected without weakening verification. |
| Pack history and roles #43 | Merged c85a424 after original independent review failures were fixed and rechecked. Unknown configuration pins and future knownAt fail closed; DB recovery and activation locking corrected. Release-manager/design-authority roles recovered; development sign-in refused outside Development. |
| Servicing follow-up #46 | Merged 4bb59d7. Cancellation preview uses dryRun and writes only after confirmation. Issued Latin plates and preserved extension fields no longer block valid edits. |
| Integration #45 | Merged cbd1e68 after all 10 checks passed on fc79c7597364d175a6649762597fccdae47c9700. Expanded isolated servicing acceptance passes all 8 tests with no cancellation/refund capability skips. |

All merged PRs above passed all 10 required checks on their reviewed final heads.

## Acceptance evidence

The final isolated SL3 run covers premium increases/decreases, all change credit notes, strict tax identity/result correlation, finance posting, claim re-verification and human adoption, cancellation and paid refund, renewal term/finance context, and three real browser servicing journeys. Final log: agent-af172971ee62c4fc1/.logs/codex-e2e03-final.log (8 passed).

Recovered production hardening in #45 binds rating results to the request, preserves the term rating artefact, checks finance renewal entity/policy context, and validates Development JWT expiry against the test clock. Native Release build has zero warnings/errors; Policy.Change39, boundary16, FINcontext6, JWT1, signin8, ContractGen2852 and sample validation2423 pass.

## Active agents and review gates

- Root: integration #45 merged. Combined isolated acceptance now passes **10 tests**: eight servicing journeys, real RI treaty creation/edit/submission/distinct-manager approval, and rejection of maker self-approval. Test checkpoint 63dc6a6 on codex/sl4-ri-acceptance; final log agent-af172971ee62c4fc1/.logs/codex-ri-acceptance-final-full.log. Its owned Compose stack/data were cleaned. These tests used candidate RI branches and PLT role/identity checkpoints, not main alone.
- servicing_ui: reinsurance UI #47, head 7e614efd. All ten CI checks pass. Fourteen focused tests pass; typecheck/lint/i18n pass. Real JWT identity is USER:dev:riacct, distinct from picker ID riacct; UI now uses the server actorKey and fails closed when identity is missing. Greek AAD/AAL labels corrected.
- ri_registry_validation: reinsurance registry #48, head 7338948. All ten CI checks pass; registry47 and approvals20 pass. Real catalogue hyphens, layer adjacency, canonical scope hashing, exact-role read grants and PLT editor validation for dev actor IDs corrected. Independent source review and live registry acceptance passed. PR remains open.
- refund_backend: product fallback #49, head 5a5982c. All ten CI checks pass; fallback17 and architecture198 pass. Approval-instant Athens windows and immutable frozen approval/source implemented. Independent findings on channel-scoped source selection and retired fallback immutability corrected and rechecked. PR remains open; downstream RAT activation is a separate package.
- Platform package #50, head ebaad034: recovery/RI dev roles, authoritative actorKey, and owning-module-only approval withdrawal. Release build has zero warnings/errors; withdrawal6, sign-in11, approvals16, idempotency9, audit8, Platform69, Host35, Architecture198, Contracts18, generated contracts and 2423 samples pass. First CI passed 1039/1040 integration tests; sole failure was an outdated exact superuser-role expectation. Corrected expectation and focused SuperUser journey pass; new exact-head CI is running. Independent source review found no confirmed blocker within documented producer boundary. PR remains open.
- RAT fallback #52, head 2370c8d, stacked on #49: Release build zero warnings/errors; five fallback integration tests pass including real PFC import, maker/checker approval, outbox consumption and quoting fallback 1.2 under the exact source tariff. ContractGen2852/samples2423 pass. Broader gates/independent review/CI remain. This proves the rating-envelope pin with MarketFake; full bound-policy/E2E-12 proof remains.
- Preserved pack/rollback UI is now draft #51, checkpoint f7efee43, with focused validation and fixture evidence being finalized. Native MKT/POL APIs remain dependencies; not a live or merged outcome.

## Progress denominator and critical path

The three current plans contain 54 work packages: slice 3 has 21, slice 4 has 22, slice 5 has 11. Mapping their packages to merged PRs gives 28/54 (52%) merged: slice 3 21/21 (100%), slice 4 2/22 (9%), slice 5 5/11 (45%). Six further packages are being finished or reviewed (#47/#48/#49/#50/#51/#52), leaving 20 further packages. These are unweighted package counts, not effort, requirements coverage or production readiness. There are 26 baseline package closures remaining, plus identified fixes and integration findings.

Slice-4 critical path: PLT withdrawal -> CLM money engine -> Friendly Settlement case -> statement/matching/approval -> E2E-02/E2E-06. In parallel: BIL receivables -> FS outgoing settlement -> combined E2E. MKT FS capability, FIN v4, RI recovery, payment corrections and UI must converge before full slice acceptance. The old slice-3 merge gates are now open.

Slice-5 critical path: merged MKT state/roles -> MKT activation/rollback -> POL exception consumer and live pack UI -> E2E-12. PFC fallback -> RAT fallback must also converge at E2E-12. Underwriting workbench API/UI are already merged.

The original full programme is larger: 134 backlog packages and 3711 P1 Must requirements. Its 2026-10-07 tracker still marks only six foundational packages merged, so it cannot provide a reliable current overall percentage. Reconcile requirement evidence before quoting a full-programme completion figure; WRK/DOC/channel breadth, reporting/close/compliance, migration/cutover, external bank/fiscal channels and production regulatory gates remain beyond these thin slices.

The real RI recovery endpoint `/api/ri/v1/recoveries/list-by-contract` returns 404 because the recovery package is not implemented. Acceptance verifies an explicit unavailable-data message, without invented zero totals or an empty-success state. It does **not** establish financial recovery acceptance. Registry ACTIVE status alone does not complete slice 4.

Additional follow-up: refund UI decision eligibility compares picker user.id with requestedBy, which the backend exposes as an actor-derived UUID. This can offer a maker a decision control incorrectly; server separation-of-duties enforcement remains authoritative. Repair the identity/eligibility contract and add a real dev-user regression before claiming that UI check complete.

Reinsurance approval proof and fallback proof depend on trusted PLT producers. Their consistency checks do not authenticate arbitrary writes made with the full shared application database credential. The broader database trust boundary remains a documented limitation, not a completed hardening claim. FU-BIL-REFUND-HARDEN also remains open.

## Preserved work and next queue

Before edits, all branches/tags were saved in a verified Git bundle and modified/untracked code and screenshots were copied to C:/Users/Karl/Projects/coreinsurance/recovery/2026-10-09-codex-takeover/.

The shared checkout remains training-portal; root integration is in agent-af172971ee62c4fc1 (codex/sl4-ri-acceptance), main in orchestrator. No Claude worktrees were removed. The user's coreins stack remains untouched. The isolated coreins-e2e03 acceptance stack and its owned data were cleaned after the passing run.

Next: finish exact-head CI/review on #47/#50 and close dependency ordering for #48/#49; preserve the passing combined RI tests for integration. Finish the current RAT producer-to-consumer activation proof and pack UI checkpoint. Then implement MKT activation/rollback, POL exceptions, E2E-12 and dependent slice-4 recoveries/receivables/finance/FS/payment work as separately reviewed programme packages. Pack UI screenshots currently use fixtures; historical activation contracts lack complete issued-hash history. A fallback product cannot quote until RAT activation exists.

GitHub is public and Actions is operational. Original plans and fail-closed business defaults still govern scope. Production regulatory decisions, fiscal/bank stubs and deployment remain open; selected acceptance journeys do not prove every Must requirement.

