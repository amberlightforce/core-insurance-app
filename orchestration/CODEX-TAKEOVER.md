# Codex takeover — 9 October 2026

The user authorized subagents to carry out the recovery assessment and next steps. This file records verified outcomes and the active queue; the older HANDOVER and STATUS phase tables contain stale entries.

## Latest verified checkpoint

- Merged #28 (servicing UI) as b501f88, #34 (refund UI) as 5dd4336, and #44 (jurisdiction-qualified MKT tax identity) as ae43539. Each had all 10 checks green on its final head and independent review.
- Real expanded SL3 acceptance: 3 passed / 5 failed on the second run. The real browser caught cancellation preview mutating immediately through POST /cancellations; the mocked draft-job assumption was incorrect. A follow-up fixes preview to dryRun=true and delays the actual POST until confirmation. It also fixes validation of the existing read-only Latin plate. Both are in the next isolated run.
- Test fixes now check all credit notes belonging to the change transaction, use keyboard checkbox activation, and capture traces/screenshots on failure. API renewal and cancellation/refund pass; claims re-verification remains under diagnosis.
- Draft #45 publishes the recovered integration and expanded real-browser acceptance. Independent review added strict RAT result correlation and restored Development JWT expiration/lifetime-order checks. No merge until remaining defects and exact-head acceptance/CI are resolved.
- #43 corrected head 04bdc3d has Release/Market166/Rating39/sign-in8/superuser1/architecture198/ContractGen2852/samples2423 passing; final CI E2E jobs remain pending. Original review failures and corrected adversarial probes are preserved.
- RI UI is preserved at a checkpoint while urgent servicing fixes finish. RI registry recovery is active, including list-layer summaries and a reproduced raw DB approval bypass; a PLT-owned proof predicate is being developed for independent review.
## Preserved work

Before edits, all local branches/tags were saved in a verified Git bundle and modified/untracked source files and screenshots were copied to:

`C:/Users/Karl/Projects/coreinsurance/recovery/2026-10-09-codex-takeover/`

The root shared checkout remains on training-portal. Integration is in the existing `integ` worktree (agent-af172971ee62c4fc1), main in `orchestrator`. No Claude worktrees were removed. The user's coreins stack has not been restarted or stopped; validation uses coreins-e2e03 with separate ports/data.

## Verified progress

| Item | Result |
|---|---|
| SL3-UI-CLM #37 | Merged as 7d470d9 after all 10 checks green on caf1b59. Independent focused Vitest 10/10; current-code Playwright mocked visual checks at 1440 light/dark and 840 light show translated seeded cover names, changed values, missing cover indicator, no page errors or horizontal overflow. Screenshots remain preserved in the builder tree under orchestration/ux/sl3-ui-clm/codex-review. |
| SL3-BIL-REFUND #39 | Recovered unpublished merge 923ae18 pushed, exact remote SHA confirmed. All 10 CI/CodeQL checks green. Native build zero warnings/errors; ContractGen current; Refunds 32, Architecture 198, Contracts 18, Host 35, Disbursement 22 and DevClock 48 tests pass. Merged as b83b72a. FU-BIL-REFUND-HARDEN remains documented. |
| SL3-UI-POL-JOBS #28 | Recovered uncommitted translation/catalogue changes. Fixed stale renewal heading assertion and actual asynchronous catalogue/table caching issue. Focused 18 tests pass; lint and typecheck pass reported; current-head CI and fresh visual evidence pending. |
| SL3-UI-BIL #34 | TS2322 fixed using explicit KeyValueItem literal typing. Floating point netting sum replaced with shared BigInt minor-unit helpers and large-amount regression. Current-head CI/focused test/visual checks pending. |
| SL5-MKT-STATE #43 | Independent review FAIL: six real PostgreSQL probes reproduced five major defects on original 0623713. Reviewer report/probes preserved under C:/Users/Karl/AppData/Local/Temp/codex-pack43-review. Builder corrections cover unknown pin/future knownAt refusal, DB failures, shipped-version startup recovery, and exclusive transaction lock enforcement. Root re-check and updated CI required before merge. |
| SL5-PLT-ROLES | Uncommitted roles/dev-user WIP recovered and committed by refund agent. Focused sign-in tests including non-Development rejection and new JWT roles pass. Batch-ready branch gate ongoing; fold into meaningful pack PR rather than creating a tiny PR. |
| SL3-E2E | Existing integ commits preserved and merged with current refund/UI work. Native solution build passed; E2E TypeScript passed before browser spec addition. Real API journeys running on isolated stack. New browser tests cover change/cancel/refund/renewal; route/capability skips removed from cancellation test so missing refund support cannot produce false acceptance. |

## Active ownership and dependencies

- Root: independent reviews/merges, SL3 integration API and browser tests, documentation and final demo refresh.
- servicing_ui agent: #28 then #34 gates and visual evidence; next recover SL4-UI-RI WIP in agent-afd5fa56de8e07945.
- refund_backend agent: #39 recovery then SL5-PLT-ROLES in agent-ad6062491a131f6ec; no independent tiny roles PR.
- pack_state_review agent: original independent #43 review, then corrections in its original builder tree; root separately rechecks corrections.

After servicing acceptance, recover product fallback (agent-aabc3a30177867bfc), pack/rollback UI (agent-a45575c6160f065bc), and RI registry (agent-a1fd0c774fb4c3051). Finish MKT activation/rollback, RAT fallback, POL exceptions and E2E-12, and the dependent slice-4 recoveries/receivables/finance/FS/payment work in the existing plans. No work is complete solely because its test harness is green.

## Environment and remaining programme

GitHub repository is public; Actions and existing authentication as amberlightforce work. Historical Sonnet-specific Claude instructions are not available models in this Codex runtime; subagents inherit the current model while retaining the required review depth and process.

The original plans continue to govern scope and fail-closed regulatory defaults. Production regulatory/business decisions, fiscal stubs, deployment and broader untouched requirements remain open. Twelve selected E2E journeys alone do not establish all Must requirements are implemented.
