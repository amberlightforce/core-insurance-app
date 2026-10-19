# Independent RI UI review

Reviewed PR47 original 3e9f8abf1756146a8581ee5ccb477ae0ff8577f6 and corrected 99d1c6f8d3a3acb42eee46ecf4685174fa434546 against main c85a424, SL4-UI-RI brief, REQ-RI-001/037/046/047/057, SCR-RI-02/03/04 and PITFALLS25-28/19. All inspection was read-only in the UI builder tree. Exact snapshots were git-archived to separate temporary review directories; own logs are in this registry worktree.

## Standards

PASS after corrections. BigInt minor-unit/percentage sums and largest-remainder allocations avoid binary floating point; invalid precision/negative allocation refuses. Negative layer-year outstanding raises inconsistency rather than clamping. Queries/table arrays and columns are memoized. Routes/nav are permission-aware; empty/loading/problem/no-permission states are present in Greek/English. Confirmed representative actual route screenshots: 1440 light detail, 840 editor bottom, 1440 dark registry follow shared Aegean list/record cards/stage pattern and remain readable. These fixtures do not prove a live backend journey, and no new screenshots were fabricated by this review.

## Spec

Original FAIL findings:
1. P1 dependency: exact RI accountant/manager lack PFC resolve/catalogue and PTY get/search reads. The editor requires catalogue.data before saving, so real RI accountant workflow is blocked by403. Backend48 is adding only those read roles to existing owner permission arrays with exact-role HTTP positive/negative regressions.
2. P2 DecisionBar.tsx47: contract.maker is backend actor key USER:maker, while session.user.id is maker. Raw equality leaves enterer approval buttons visible. Original12tests passed but a backend-shaped independent regression failed. Corrected99d1c6f compares USER:${user.id} and fixes fixture; real-shaped hiding test now passes. Server guard continues to enforce SoD regardless of button hiding.
3. P2 api.ts158-168/ClaimRecoveriesPage: capped25page claim read silently presents incomplete totals as complete. Corrected99d1c6f returns partial, warns atcap, shows loading while paging and labels current financial totals as loaded records. Regression verifies25requests and incomplete total warning.

Corrected UI head PASS within UI-only review scope, conditional on backend48 correction/dependency acceptance. Native independent focused tests13/13 pass at99d1c6f. Source inspection verifies all corrections. Typed optional ContractListItem.layers matches backend48ac89a41; currentversion summaries preserve layer ordering and money. Editor PATCH passes expectedRecordVersion and requires save confirmation; submit/approve/return confirmations and body-specific idempotency keys are present, with human SoD/stale guidance. Return requires a non-empty reason. Product/coverage scope is read from PFC catalogue. Backend actual-code acceptance and contiguity gaps are owned by48fixes.

Still required: root's combined real RI UI/backend acceptance after dependency sync, exact-head CI and merge review. Recovery/claim screens use typed future APIs; backend48 registry alone does not implement recovery engines or those endpoints. No conclusion of full recovery acceptance is made.

Backend correction gates completed after main `cbd1e68` merged: solution Release build has zero warnings/errors;
registry 46, PLT approvals 20, Host 35 and architecture 198 tests pass without skips. The exact-role HTTP
regressions prove RI accountant and manager can resolve/read the real catalogue and search/read the reinsurer;
party creation and product import remain forbidden. The accountant creates a treaty with `MOTOR-GR` /
`OWN-DAMAGE`; gaps and overlaps are refused on create/edit, and sorted JSON scope hashes remain distinct.
These cover the identified API integration blockers; full real-browser acceptance remains root-owned.

Evidence logs: ri47-independent-original-tests.log (12pass), ri47-independent-maker-probe.log (original defect reproduced), ri47-independent-fixed-tests.log (13pass). Temporary archives remain under C:/Users/Karl/AppData/Local/Temp/codex-ri47-review-{3e9f8ab,99d1c6f}.
