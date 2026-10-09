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
| Integration #45 | Ready for review, head fc79c7597364d175a6649762597fccdae47c9700; CI still running. Expanded isolated servicing acceptance passes all 8 tests with no cancellation/refund capability skips. |

All merged PRs above passed all 10 required checks on their reviewed final heads.

## Acceptance evidence

The final isolated SL3 run covers premium increases/decreases, all change credit notes, strict tax identity/result correlation, finance posting, claim re-verification and human adoption, cancellation and paid refund, renewal term/finance context, and three real browser servicing journeys. Final log: agent-af172971ee62c4fc1/.logs/codex-e2e03-final.log (8 passed).

Recovered production hardening in #45 binds rating results to the request, preserves the term rating artefact, checks finance renewal entity/policy context, and validates Development JWT expiry against the test clock. Native Release build has zero warnings/errors; Policy.Change39, boundary16, FINcontext6, JWT1, signin8, ContractGen2852 and sample validation2423 pass.

## Active agents and review gates

- Root: integration #45 CI/merge, independent review of fallback #49, programme documentation and cross-module acceptance.
- servicing_ui: reinsurance UI #47. Canonical USER actor keys and honest partial recovery totals corrected at head 99d1c6f; 13 focused tests, typecheck/lint/i18n pass. Fixture visual evidence exists; real backend acceptance remains required.
- ri_registry_validation: reinsurance registry #48. Independent UI review found lookup permissions, actor-key and partial totals defects. Backend review found rejection of real hyphenated catalogue codes and missing layer adjacency validation. Fixes and exact-role HTTP regressions are under validation; no merge yet.
- refund_backend: product fallback #49. Approval-instant Athens windows, frozen configuration/source, owner execution, maker/principal separation and proof consistency implemented. Fallback14, architecture198 and generated contracts/sample gates pass. Independent review found source selection must filter channel scope before choosing the predecessor; correction requested. CI/review/dependency gates remain.

Reinsurance approval proof and fallback proof depend on trusted PLT producers. Their consistency checks do not authenticate arbitrary writes made with the full shared application database credential. The broader database trust boundary remains a documented limitation, not a completed hardening claim. FU-BIL-REFUND-HARDEN also remains open.

## Preserved work and next queue

Before edits, all branches/tags were saved in a verified Git bundle and modified/untracked code and screenshots were copied to C:/Users/Karl/Projects/coreinsurance/recovery/2026-10-09-codex-takeover/.

The shared checkout remains training-portal; root integration is in agent-af172971ee62c4fc1 (integ), main in orchestrator. No Claude worktrees were removed. The user's coreins stack remains untouched. The isolated coreins-e2e03 acceptance stack and its owned data were cleaned after the passing run.

Next: finish #45 checks; close #47/#48 review findings and run real combined RI acceptance; finish #49 independent review and merge dependencies. Preserved pack/rollback UI (agent-a45575c6160f065bc) still needs recovery. MKT activation/rollback, RAT fallback activation, POL exceptions, E2E-12 and dependent slice-4 recoveries/receivables/finance/FS/payment work remain in the existing programme. A fallback product cannot quote until RAT activation exists.

GitHub is public and Actions is operational. Original plans and fail-closed business defaults still govern scope. Production regulatory decisions, fiscal/bank stubs and deployment remain open; selected acceptance journeys do not prove every Must requirement.
