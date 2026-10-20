# NEXT STEPS — hardening phase (planned 2026-10-20, main @ bb040d8)

Slices 1–6 are on main, and E2E-01..12 have specs and CI jobs. This file lists the next phase: hardening.

**How it runs (D-USR-16/20, `briefs/LIGHT-WORKFLOW.md`)**
- Every item is executed by **Sonnet 5.5** builders: build + focused tests, push straight to main, no reviewer agents.
- Hard limits: at most 15 builders at once, and no two builders in one wave touch the same files.
- After each push to main, rebuild the coreins Docker stack.
- **UI items** keep the D-USR-11 visual check. The builder posts light/dark/390px screenshots, side by side with `core-insurance-prds/design-guide/mockups/aegean-screens-v3.html`.
  - That mockup draws only three screens: home, the UW workbench and the claim file «Φάκελος ζημίας».
  - Every other screen follows its visual language: tokens, cards, tables, pills, spacing.

**Sources:** 5 read-only Sonnet surveys (UI, testing, edge cases, hardening TODOs, security/ops), plus spot checks by the planner.

**Baseline**
- `TODO(hardening)` markers: 86 in 72 files under `src/`, `web/src` and `tests/e2e`. The marker groups are mapped to the HD items below.
- About 1,450 .NET tests (1,067 integration), 103 web test files, and 12 E2E journeys.
- Only E2E-01/02/03/12 have a Playwright UI spec.

Legend: priority **P0** = fix before anyone relies on the behaviour, P1 = this phase, P2 = when capacity allows. Size: S ≤ 2 h, M ≈ half day, L ≈ a day or more (split when started). Paths are relative to the repo root; `src/X` means `src/CoreIns.Modules.X` unless the path is written out in full.

---

## 1. UI review (14)

| ID | Title | Scope | Pri | Size | Depends | Model |
|---|---|---|---|---|---|---|
| NX-UI-01 | One role model for routes and nav | New `web/src/auth/roles.ts` plus a single `<RequireRole roles=…>` that replaces the 9 `Require*Role.tsx` guards. `app-shell/navigation.ts` must use the same lists. Guard `/underwriting` and `policies/referrals`, which are unguarded in `routes.tsx:34-46`. Add a route-vs-nav consistency test. | P1 | M | — | Sonnet 5.5 |
| NX-UI-02 | Loading / empty / error / 403 states everywhere | Standardise on one `ProblemBanner` with a `forbidden` variant, via `staff/QueryView`. Apply it to `billing/AccountPage`, `billing/InvoicePage`, `compliance/dsar/DsarCasePage`, `party/PartySearchPage`, `policy/PolicyViewPage`, `policy/packRollback/ExceptionDetailPage`, `reinsurance/ContractPage` and `underwriting/workbench/WorkbenchPage`. Retire the ad-hoc 403 handling in `ServicingProblem.tsx` and the two `DecisionBar.tsx` files. | P1 | M | UI-01 (merged) | Sonnet 5.5 |
| NX-UI-03 | Clipped tables and off-centre empty states | Wrap tables in an overflow-x container, or switch to card rows at narrow widths: `modules/staff/SimpleTable.tsx` and the design-system table. Smoke shots `billing-receivables.png` and `claim-view.png` cut off the last column. Centre the design-system `EmptyState`. | P1 | S | — | Sonnet 5.5 |
| NX-UI-04 | Mockup-fidelity pass on post-SL-UX screens | Restyle to the Aegean language every screen built after SL-UX: billing receivables/delinquency/releases, reinsurance, migration, packs, AI control, DSAR, withdrawals, and the FS screens. Save before/after PNGs to `orchestration/ux/hardening/`, light/dark/390. | P1 | M | UI-02, UI-03 | Sonnet 5.5 |
| NX-UI-05 | Generate web types for doc/dat/rat/wrk | `web/scripts/gen-contracts.mjs` must emit `web/src/api/generated/{doc,dat,rat,wrk}`. This is a prerequisite for UI-08, UI-14 and the new screens. | P1 | S | — | Sonnet 5.5 |
| NX-UI-06 | Remove stub and fixture data from production paths | Remove the "stub" fiscal status in `billing/FiscalStatusPill.tsx` and the `releases.detail.stub` values in `billing/receivables/ReleaseDetailPage.tsx:219-224`; use real data or an explicit "not available" state. Swap the `PackRollbackException` types in `policy/packRollback/fixtures.ts` and `api/types.ts` for the generated `pol` types. | P1 | S | — | Sonnet 5.5 |
| NX-UI-07 | Replace hand-typed APIs with real reads | Clear the web `TODO(hardening)` items: `billing/delinquency/api.ts:10`, `billing/receivables/api.ts:77` (server-side filter), `compliance/dsar/api.ts:172`, `compliance/dsar/download.ts:16`, `claims/recoveries/RecoveriesTab.tsx:414,561`, `claims/fs-statements/FsStatementsPage.tsx:227`, `claims/fs/FsCasePanel.tsx:176` and `platform/ai/{AiControlPage.tsx:55,api.ts:16}`. | P2 | M | HD-08a, HD-08c | Sonnet 5.5 |
| NX-UI-08 | Work inbox (`/work`) + notification centre | New `web/src/modules/work/`: my tasks, team queue, task detail. Feed the `NotificationCenter` (`App.tsx` currently passes `notifications={[]}`) and the home dashboard. Replace the `NOT_CREATED_WRK_NOT_BUILT` placeholder on pack-rollback detail. | P1 | L | HD-05, UI-05 | Sonnet 5.5 |
| NX-UI-09 | Complaints and statutory clocks workbench (`/compliance`) | New `web/src/modules/compliance/complaints/` and `clocks/` on `/api/cmp/v1/complaints/*` (13 endpoints) and `/clocks/*` (9 endpoints). Replace the `/compliance` `ModulePlaceholder`. | P1 | L | — | Sonnet 5.5 |
| NX-UI-10 | Finance operations screens | `web/src/modules/finance/`: trial balance, manual journal submit/decide, intake exceptions, period close, earning run. Bring `FinanceHomePage` up to the UI-02 state standard. | P1 | L | UI-02 | Sonnet 5.5 |
| NX-UI-11 | Fiscal documents and regulatory submissions | Screens for `/api/cmp/v1/fiscal-documents/*` and `fiscal-reconciliation/*` (resubmit, cancel, correct, breaks), plus submissions, annual returns and the AI-system register. | P2 | L | UI-09 | Sonnet 5.5 |
| NX-UI-12 | Accessibility pass | Run axe in route-level tests for every page (`web/src/test/axe.ts`). Status pills (`InvoiceStatePill`, `ClaimStatusPill`, `StatusPills`) need a text alternative, not colour alone. Check that the notification bell, Ctrl+K palette and shortcut overlay are keyboard-reachable. Fix the `ViewsRail.tsx:41` raw onClick. Focus or announce the no-permission banner. | P2 | M | UI-02 | Sonnet 5.5 |
| NX-UI-13 | Nav icons and role homes | Give each nav destination its own icon in `app-shell/navigation.ts`; four policy-family entries currently share one. Add role homes for billing, RI, compliance and migration in `modules/home/HomePage.tsx`, or a graceful fallback. | P2 | S | UI-01 | Sonnet 5.5 |
| NX-UI-14 | Remaining placeholder modules | Replace the `/documents`, `/reports` and `/products` placeholders. Add billing ops screens: suspense, mandates, payment plans, account-currents. Split into one item per module when started. | P2 | L | UI-05 | Sonnet 5.5 |

## 2. Testing (10)

| ID | Title | Scope | Pri | Size | Depends | Model |
|---|---|---|---|---|---|---|
| NX-TST-01 | E2E: replace fixed sleeps with condition polls (set A) | Shared `expect.poll`/`toPass` helper in `tests/e2e/tests/support/api.ts`. Fix: `e2e11.spec.ts:191` (8 s sleep before the "exactly once" check; poll for the outbox to drain, then assert), `e2e07.spec.ts:168`, `e2e12.spec.ts:172` and `support/packs.ts:73`. The E2E-06 import loop (`e2e06.spec.ts:~160`) must fail loudly when no attempt matches. | P0 | M | — | Sonnet 5.5 |
| NX-TST-02 | E2E: dev clock instead of wall clock (set B) | Replace the `Date.now() < start + 4000` loops in `e2e02*.spec.ts`, `e2e09.spec.ts` and `support/claims4/policy.ts` with the dev-clock API. `e2e03-health.spec.ts:16` must fail in CI, not skip, when `/dev/clock` is missing. | P1 | M | — | Sonnet 5.5 |
| NX-TST-03 | Flake visibility and E2E runtime | `tests/e2e/playwright.config.ts`: report tests that only passed on retry, consider `retries: 1`, upload traces and the HTML report on failure. `run-e2e*.sh`: use `docker compose up --wait` instead of sleep loops. `.github/workflows/ci.yml`: a nightly-only slot for the slow E2E-07/08. | P1 | M | TST-01 | Sonnet 5.5 |
| NX-TST-04 | Route-vs-OpenAPI conformance test | Enumerate Host endpoints via `EndpointDataSource` and diff them against `contracts/openapi/*.yaml` operations, with an allow-list of known gaps. The allow-list becomes the backlog for HD-08. Location: `tests/CoreIns.Contracts.Tests` (or `tests/CoreIns.Host.Tests`). | P0 | M | — | Sonnet 5.5 |
| NX-TST-05 | Real events vs event schemas | Validate the payloads that integration tests actually emit (`tests/CoreIns.Testing.Contracts/RecordingEventPublisher.cs`) against `contracts/events` JSON schemas, not only the generated samples. | P1 | M | — | Sonnet 5.5 |
| NX-TST-06a | Playwright UI specs: E2E-05, E2E-07, E2E-08 | New `tests/e2e/tests/e2e05-ui.spec.ts`, `e2e07-ui.spec.ts` and `e2e08-ui.spec.ts` against `web/src/modules/{migration,billing/delinquency,channels/withdrawal}`. Run them from the existing `run-e2eNN.sh`; no `ci.yml` change. | P1 | L | UI-02 | Sonnet 5.5 |
| NX-TST-06b | Playwright UI specs: E2E-06, E2E-09, E2E-10 | New `e2e06-ui.spec.ts`, `e2e09-ui.spec.ts` and `e2e10-ui.spec.ts` for FS statements, DSAR and the AI kill switch. Same runner rule as TST-06a. | P1 | L | UI-02 | Sonnet 5.5 |
| NX-TST-07 | Thin modules, part 1 | Integration tests for Party (11 today: merge, duplicates, AFM, GDPR attributes), Channels (7), Documents (5) and Data (3): error paths, idempotency, authorisation. Location: `tests/CoreIns.IntegrationTests/{Party,Channels,Documents,Data}/`. | P1 | M | — | Sonnet 5.5 |
| NX-TST-08 | Thin modules, part 2 | Compliance/DSAR erasure edge cases and statutory clock breaches. Migration rerun, rollback and malformed legacy rows, seeded from `tests/e2e/fixtures/legacy-gr1.json`. Location: `tests/CoreIns.IntegrationTests/{Compliance,Dsar,Migration}/`. | P1 | M | EC-10 | Sonnet 5.5 |
| NX-TST-09 | Web coverage gate and per-page state tests | Add `vitest --coverage` with a threshold in `web/vite.config.ts` and the `web` CI job. Add per-page error, empty and forbidden tests for `modules/{claims,reinsurance,compliance/dsar,market,channels,platform}`. | P2 | M | UI-02 | Sonnet 5.5 |

The endpoint authorisation matrix is listed once, as NX-OT-01.

## 3. Edge cases (12)

| ID | Title | Scope | Pri | Size | Depends | Model |
|---|---|---|---|---|---|---|
| NX-EC-01 | One rounding policy for money | RI hard-codes `AwayFromZero` in `src/Reinsurance/Recovery/RecoveryBooker.cs:82` and `Cession/CessionModels.cs:62`; route it through the MKT rounding rule (`src/Market/Domain/Rounding.cs`). Extract one shared mode-to-`MidpointRounding` mapper; it is duplicated in `src/CoreIns.Rules/RuleLanguage.cs`, `Market/Domain/Rounding.cs`, `Policy/Commands/Change/ChangeServicing.cs` and `Rating/Domain/RatingEngine.cs`. Add a table test for negative midpoints and an architecture test that bans literal-mode or default `decimal.Round` in money modules (allow-list the existing sites for EC-02). | P0 | M | — | Sonnet 5.5 |
| NX-EC-02 | Fix silent default (banker's) rounding sites | `Rating/Domain/RatingFacts.cs:162`, `Claims/Commands/LiabilityFacts.cs:23`, `Underwriting/Domain/RuleSets.cs:61,78` (rule input and cache key), `Policy/Commands/Cancellation/CancelPolicy.cs:583` and `Finance/Queries/JournalReader.cs:198`. Use the rule, or validate with `IsRoundedToMinorUnits` instead of rounding. Then empty the EC-01 allow-list. | P0 | S | EC-01 | Sonnet 5.5 |
| NX-EC-03 | Time zone must never fall back to UTC silently | Remove the `AthensOrUtc()` fallbacks in `Underwriting/Commands/EvaluateRules.cs:262` and `Product/Commands/ImportProductVersion.cs:262`. Take the zone from `LegalEntityRegistry`, not the hard-coded "Europe/Athens" in `Product/Domain/FallbackPlanner.cs:47` and `Rating/Events/ProductVersionFallbackHandler.cs:26`. Fail fast at startup if tzdata is missing, and add a `Dockerfile` check. | P0 | S | — | Sonnet 5.5 |
| NX-EC-04 | Temporal edge tests | Parameterised tests:<br>- 29 Feb inception, DST-spanning terms, and cancellation on a 23 h or 25 h day (`Policy/Domain/PolicyTime.cs`).<br>- 366-day ACT/365F (`Cancellation/PinnedArtefact.cs`).<br>- Month-end and DST statutory deadlines (`Compliance/Clocks/ClockDomain.cs:115-127`, `Channels/Domain/WithdrawalWindow.cs`).<br>Fix whatever breaks. | P1 | M | — | Sonnet 5.5 |
| NX-EC-05 | Out-of-sequence change across a renewal boundary | Integration tests where an OOS delta lands in a term that was already renewed, ceded or invoiced. Assert that BIL respread, RI cession reconcile and FIN accruals converge, and that terms never overlap (`Policy/Commands/Change/Oos/{OosPlanner,OosApplier}.cs`, `Policy/Commands/Renewal/AcceptRenewal.cs:435`). New test files only, plus fixes. | P1 | L | — | Sonnet 5.5 |
| NX-EC-06 | Exact cent splits | One largest-remainder helper, used by:<br>- XoL participant split, where negative totals currently skip the loop (`Reinsurance/Domain/Recovery/XolRecoveryEngine.cs:116`).<br>- Claims recovery tail allocation, which can drop a negative remainder (`Claims/Services/RecoveryEvidenceSource.cs:115`).<br>- Multi-treaty quota-share targets and premium lines that span a treaty boundary (`Reinsurance/Cession/CessionIntake.cs:123-160`).<br>Add property tests: the parts sum exactly to the total, and no part deviates by more than 1 cent. | P1 | M | EC-01, HD-07 | Sonnet 5.5 |
| NX-EC-07 | Outbox replay, ordering and lease expiry | Tests in a new `tests/CoreIns.IntegrationTests/Platform/OutboxReplayTests.cs`:<br>- Ordering-sensitive handlers (cession, ledger posting, claim financials) dedupe on `event_id` or refuse an out-of-order `aggregateSequence` after a dead-letter replay (`src/CoreIns.Platform/Events/{OutboxProcessor,OutboxReplayService}.cs`).<br>- A 60 s lease against a slow handler does not double-apply. | P1 | M | — | Sonnet 5.5 |
| NX-EC-08 | Idempotency business keys and lost updates | DB-level unique business keys for receipts (bank reference), disbursements and refunds, so a retry after the 7-day idempotency expiry cannot double-post (`src/CoreIns.Platform/Idempotency/IdempotencyStore.cs`, BIL persistence). Two-session race tests for double reserve change, double payment approval and concurrent RI recovery booking. | P1 | M | HD-06 | Sonnet 5.5 |
| NX-EC-09 | Segregation-of-duties matrix | Generic test over every `ApprovalType` covering maker = checker, maker's delegate, edit-then-approve, and checker authority below the amount. Fix these:<br>- Add amount authority to `Billing/Commands/DisbursementOps.cs:46` (seed `BIL.DisbursementStop`).<br>- `Channels/Ai/AgentTickets.cs:244`: verify the step-up proof and bind it to the subject.<br>- `Migration/Commands/BatchCommands.cs:204-278`: a content change after the dry run must invalidate the approval. | P1 | M | — | Sonnet 5.5 |
| NX-EC-10 | Migration importer gaps | Fixes and tests:<br>- Opening receivable sub-ledger entry (`Billing/MigrationImport/BillingMigrationImporter.cs:121,147,191`).<br>- FIN reversal into a closed period (`Finance/MigrationImport/FinanceMigrationReverser.cs:41`).<br>- CMP clock reversal (`Claims/MigrationImport/ClaimMigrationReverser.cs:15`).<br>- An RI reverser (`Reinsurance/MigrationImport/ReinsuranceMigrationImporter.cs:14`).<br>- Policy importer TODOs (`Policy/MigrationImport/PolicyMigrationImporter.cs:114,134,198`).<br>- Re-run idempotency, and a per-currency BIL-vs-FIN reconciliation test. | P1 | M | HD-08b | Sonnet 5.5 |
| NX-EC-11 | Instalment billing | `Billing/Services/TermBilling.cs:348` bills ANNUAL only, and `Billing/Services/Allocator.cs` refuses partial payments. Build:<br>- The instalment split, by largest remainder with a pro-rata tax/levy/fee split.<br>- Partial-payment allocation.<br>- Re-spread after an endorsement.<br>Property tests: the parts sum exactly, and premium + tax + levy = gross. | P1 | L | HD-06, EC-08 | Sonnet 5.5 |
| NX-EC-12 | Regulatory go-live checklist test | A CI test that lists every pack value still `legalStatus` Unverified or PendingOpinion (clocks OQ-008, levy split OQ-010, IPT point OQ-011, stamp, myDATA OQ-012) as an artefact. Assert that Production activation of the synthetic CY pack (`src/CoreIns.CountryPacks.CY/CyTreatmentRules.cs`) is refused. Add structure-only golden tests for the GR levy lines. Location: `tests/CoreIns.CountryPacks.Tests`. | P2 | S | — | Sonnet 5.5 |

## 4. Hardening — known gaps and `TODO(hardening)` groups (15)

| ID | Title | Scope | Pri | Size | Depends | Model |
|---|---|---|---|---|---|---|
| NX-HD-01 | Scheduler for the overdue sweep | New `src/Billing/Services/DelinquencySweeper.cs`, a hosted service on the worker role modelled on `Compliance/Clocks/ClockSweeper.cs` and `Policy/.../PsrQueue.cs:298` (`NonPaymentSweeper`). It runs once per legal entity as a service actor. Interval and an on/off switch go in `BillingOptions`; register it in `BillingModule.cs`. Today only the dev-only `Api/DevDelinquencyController.cs` triggers it, so production never opens delinquency cases. | P0 | S | — | Sonnet 5.5 |
| NX-HD-02 | Real vulnerable-customer check | `Policy/Commands/Cancellation/NonPayment/NonPaymentCancellation.cs:56-65` always returns "not vulnerable" (fail-open).<br>- Implement PTY `Vulnerability.query/record/remove` (`src/Party/` service + controller) and add a party key to `contracts/openapi/pty.yaml`.<br>- Call it by party from POL, and confirm `Billing/Services/NonpayService.cs` uses the same check.<br>- Interim: unknown = vulnerable, routed to the PSR queue.<br>- Test: a vulnerable party is never cancelled for non-payment. | P0 | M | — | Sonnet 5.5 |
| NX-HD-03 | WRK DSAR participant so DSARs can complete | WRK is a placeholder (`src/Work/WorkModule.cs`), so `Compliance/Dsar/DsarEngine.cs:32,58,106,632` marks it NOT_BUILT and no DSAR ever completes.<br>- Add `src/Work/Services/Dsar/WrkDsarParticipant.cs`, which reports "nothing held" until HD-05 lands.<br>- Add a minimal `wrk` persistence and migration, and the Host wiring.<br>- Check that no other participant is still NOT_BUILT. | P0 | S | — | Sonnet 5.5 |
| NX-HD-04 | Withdrawal read leaks across customers | `src/Channels/Api/WithdrawalsController.cs:64`: a portal customer can read any request in the legal entity. Restrict portal callers to requests on their own policies, and add a 403/404 test. | P0 | S | — | Sonnet 5.5 |
| NX-HD-05 | WRK module core | Tasks, queues, assignment and SLA per `contracts/openapi/wrk.yaml` (the subset the UI needs), the `SLABreachedV1` event, and PSR/pack-rollback review activities (D-SL5-10). Then the HD-03 participant reports real data. | P1 | L | HD-03 | Sonnet 5.5 |
| NX-HD-06 | Un-quarantine tax and levy credits | `Billing/Domain/Ledger.cs:27`, `Domain/Servicing.cs:103` and `Services/TermBilling.cs:~169` currently quarantine credits (`TaxCreditNotSupported`).<br>- Add tax and levy credit posting kinds, the FIN intake rule and reversal accounts (`src/Finance/Posting/*`), and GR/CY credit treatment rows.<br>- Rows that are not legally settled get `legalStatus` PendingOpinion and refuse production activation (D-SL3-05/06, D-SL6-15).<br>- Fail-closed tests prove no partial post.<br>- Also fix partial voids (`Finance/Posting/PaymentVoidPosting.cs:53`). | P1 | M | — | Sonnet 5.5 |
| NX-HD-07 | RI treaties from the registry, not config | `Reinsurance/Cession/CessionIntake.cs` reads the illustrative `CessionTreatyOptions` (`CessionModels.cs:23`). Read the approved contract version in force from `Reinsurance/Registry` instead (`version-at`/`applicable`), and keep config only as a dev seed. Fix the `CessionIntake.cs:24,62` and `TrackerService.cs:12` TODOs, and enable the GROSS-mode assertion deferred at `tests/e2e/tests/e2e11.spec.ts:118`. | P1 | M | HD-08a, EC-01 | Sonnet 5.5 |
| NX-HD-08a | RI read APIs | New `src/Reinsurance/Api/{CessionsController,ProgrammesController}.cs`: cessions (by policy, by id), cession exceptions, programmes and participations from `ri.yaml`. Use keyset paging. | P1 | M | TST-04 (allow-list) | Sonnet 5.5 |
| NX-HD-08b | Migration read APIs | Add `enquiries/get`, `batches/redrive`, `archives/*` (search, get, subject-export, rectification-note, restrict, erase) and `import-contracts/conformance` to `src/Migration/Api/MigrationController.cs` per `mig.yaml`. | P1 | M | TST-04 | Sonnet 5.5 |
| NX-HD-08c | Billing read APIs | Add list and search reads for delinquency cases and migrated billing records, the TaxLevy controller (`IBillingTaxLevyService`), and a server-side disbursement filter. Location: `src/Billing/Api/*`, new controller files. | P1 | M | TST-04 | Sonnet 5.5 |
| NX-HD-09 | DSAR erase and subject matching | Erase steps in `src/{Billing,Claims,Finance,Reinsurance}/Services/Dsar/*DsarParticipant.cs:60` (respect retention holds). Subject matching in `Rating`/`Underwriting` (`:9`) and `Channels/Services/Dsar/ChannelsDsarParticipant.cs:55,79`. Also fix `Party/Services/Dsar/PartyDsarWork.cs:26`, `Policy/Services/Dsar/PolicyDsarWork.cs:100`, `src/CoreIns.Platform/Dsar/PlatformDsarParticipant.cs:25`, and the poisoned shared transaction at `Compliance/Dsar/DsarEngine.cs:603`. | P1 | M | HD-03 | Sonnet 5.5 |
| NX-HD-10 | Background reliability (CHN/POL) | Add a sweeper for CONFIRMED withdrawals and an outbox retry (`Channels/Commands/Withdrawals.cs:287,298`). Wire the notification channel (`Channels/ChannelsOptions.cs:28`). Permanent refusals should dead-letter immediately instead of retrying (`Policy/.../NonPaymentCancellation.cs:202`). | P1 | M | HD-04 | Sonnet 5.5 |
| NX-HD-11 | Cross-module stubs | Fix these TODOs:<br>- Party name in non-payment letters (`NonPaymentCancellation.cs:121`, `Billing/Services/NonpayService.cs:118`).<br>- Request the DT-WITHDRAWAL-ACK document (`Policy/Commands/Cancellation/CancelPolicy.cs:390`).<br>- Clearing payee account from BIL (`Claims/ClaimsOptions.cs:107`).<br>- Withdrawal days and bind date (`Policy/PolicyOptions.cs:45`, `Channels/Domain/WithdrawalPolicyPort.cs:60`).<br>- `Policy/Commands/Withdrawal/SubmitWithdrawal.cs:140`.<br>- `Claims/Events/FsNetEventHandlers.cs:144`. | P2 | M | HD-02, HD-10 | Sonnet 5.5 |
| NX-HD-12 | Pagination standard | A shared platform keyset-paging helper with max page size and stable sort. Apply it to the TODO readers: `Compliance/Clocks/ClockReader.cs:23`, `Policy/.../PsrQueue.cs:119`, `Policy/Queries/PackRollbackReader.cs:17`, `Compliance/Dsar/DsarCommands.cs:214` and `Reinsurance/Recovery/RecoveryReader.cs:131`. Then audit the other list endpoints; about 9 of 48 controllers page today. | P1 | M | HD-09 (DsarCommands) | Sonnet 5.5 |
| NX-HD-13 | Encryption and redaction leftovers | KMS/HSM key wrap in `Channels/Persistence/ChannelsDbContext.cs:103` and `Data/Persistence/DataDbContext.cs:36`. FNOL redaction in `Claims/Services/FnolAssist.cs:88`. | P2 | M | — | Sonnet 5.5 |
| NX-HD-14 | AI assists use server-side facts | `Billing/Services/Ai/SuspenseMatch.cs:76`, `Party/Services/Ai/DuplicateExplainer.cs:68` and `Underwriting/Ai/ReferralSummary.cs:45` take facts supplied by the caller; load them server-side. Fix the AgentTickets execution and resource stubs (`Channels/Ai/AgentTickets.cs:136,183,201`) and `src/CoreIns.Platform/Ai/{AiCommands.cs:98,AiModule.cs:82}`. | P2 | M | EC-09 | Sonnet 5.5 |
| NX-HD-15 | Remaining FIN/DOC/CMP/MKT TODOs | Fix:<br>- `Finance/Posting/Intake.cs:227`<br>- `Compliance/Commands/RequestFiscalDocument.cs:129`<br>- `Documents/Commands/DocumentCommands.cs:272,357`<br>- `Market/Services/CapabilityCatalogue.cs:15`<br>- `Data/Commands/ModelCommands.cs:128`<br>- `Policy/.../Oos/OosPlanner.cs:57`<br>- `Migration/Commands/BatchCommands.cs:389` | P2 | S | HD-06 | Sonnet 5.5 |

## 5. Other — security, ops, observability, docs (13)

| ID | Title | Scope | Pri | Size | Depends | Model |
|---|---|---|---|---|---|---|
| NX-OT-01 | Endpoint authorisation matrix | A test in `tests/CoreIns.Host.Tests` that enumerates every route and asserts a permission policy or an explicit allow-list entry, so the fallback "any authenticated user" (`src/CoreIns.Host/Hosting/SecurityExtensions.cs`) never silently covers a mutating endpoint. Decide on anonymous OpenAPI (`src/CoreIns.Host/Program.cs:120`): Development only. | P0 | M | — | Sonnet 5.5 |
| NX-OT-02 | CI security gates | In `.github/workflows/ci.yml`:<br>- gitleaks (the repo is public).<br>- `dotnet list package --vulnerable --include-transitive` and `npm audit --omit=dev` (CI uses `--no-audit` today).<br>- Trivy config/IaC scan for bicep and the Dockerfile.<br>- A guard that no non-local config or bicep enables `DevAuthentication` or `ASPNETCORE_ENVIRONMENT=Development`.<br>In `.github/dependabot.yml`: add docker, compose and github-actions. | P0 | M | — | Sonnet 5.5 |
| NX-OT-03 | Bicep secret hygiene | `infra/azure/main.bicep:171-175` builds connection strings that contain passwords into module params. Mark them `@secure()`, keep them in Key Vault only, and make sure no deployment output exposes them (`infra/azure/modules/keyvault.bicep`). | P0 | S | — | Sonnet 5.5 |
| NX-OT-04 | Backups and DR | Prod PITR and geo-redundancy in `infra/azure/prod.bicepparam` and the postgres module. Blob immutability for documents and the audit archive. A restore runbook in `docs/runbooks/backup-restore.md`. | P1 | M | OT-03 | Sonnet 5.5 |
| NX-OT-05 | HTTP hardening | HSTS, CSP, nosniff, an explicit CORS policy, rate limiting on sign-in and write endpoints, and request body limits. Location: `src/CoreIns.Host/Program.cs`, `Hosting/SecurityExtensions.cs`. | P1 | M | OT-01 | Sonnet 5.5 |
| NX-OT-06 | Correlation ids | Middleware that accepts or creates `X-Correlation-Id` and pushes it into the Serilog LogContext and the Activity. Return it in ProblemDetails and the response header, and link it to the audit `CorrelationId`. Location: `src/CoreIns.Host/Hosting/ObservabilityExtensions.cs`, `src/CoreIns.Platform/Errors/ProblemDetailsMapper.cs`. | P1 | M | OT-05 | Sonnet 5.5 |
| NX-OT-07 | Health depth and business metrics | Readiness probes for Hangfire, the worker role, blob storage and Gotenberg (`src/CoreIns.Host/Health/`). Custom `Meter`s for outbox lag, dead letters, sweeper heartbeat (HD-01, ClockSweeper, NonPaymentSweeper), idempotency conflicts and payment failures. Alerts in `infra/azure/modules/monitoring.bicep` and probes in `container-app.bicep`. | P1 | M | OT-06, HD-01 | Sonnet 5.5 |
| NX-OT-08 | Runbooks | `docs/` holds only `module-pattern.md`. Add `docs/runbooks/` for: deploy/rollback, migration failure, secret rotation, dead-letter replay, incident triage by correlation id, the dev-superuser policy, and the regulatory go-live gates (from EC-12). | P1 | M | — | Sonnet 5.5 |
| NX-OT-09 | Worktree and branch cleanup | 58 worktrees (about 52 `agent-*`, plus `r37`, `rev` and `codex-pack43-review` in temp folders) and 163 local branches. Only remove worktrees that are clean and fully merged. Never touch the main checkout (`training-portal`) or `orchestrator`. Review the 4–5 unmerged branches (`codex/sl3-integration-hardening`, `codex/sl4-ri-acceptance`, `worktree-agent-a82e2edea9dd13067`, `worktree-agent-ad6062491a131f6ec`) and list them for the user before dropping. Triage the ~40 `origin/dependabot/*` branches after OT-02. Orchestrator housekeeping; it uses no builder slot. Run it while no builders are active. | P1 | S | — | Sonnet 5.5 |
| NX-OT-10 | Demo seed expansion | `infra/local/seed-demo.py` seeds only one policy, one quote and optional claims. Extend it to billing, finance, RI, compliance and migration demo data. Make it idempotent and add a smoke run in `infra/local/smoke.sh`. This also gives the UI mockup passes realistic data. | P2 | M | — | Sonnet 5.5 |
| NX-OT-11 | Index and query review at seeded volume | Use `pg_stat_statements` and `EXPLAIN` on the hot paths: policy, billing and claim lookups, and the Greek unaccent search (`infra/database/greek-search.sql`). Add missing FK and tenant indexes per module `Persistence/*DbContext.cs`. | P2 | M | OT-10, HD-12 | Sonnet 5.5 |
| NX-OT-12 | Deploy safety | A required-reviewer gate on the `prod` environment and a post-deploy smoke beyond `/health/ready` (`.github/workflows/deploy.yml`, `scripts/run-job.sh`). Document the expand/contract migration policy. | P2 | M | OT-08 | Sonnet 5.5 |
| NX-OT-13 | Log, container and repo hygiene | A PII destructuring policy (AFM, IBAN) with a test (`src/CoreIns.Platform.DataProtection/`, `appsettings.json`). A Dockerfile `HEALTHCHECK` and a read-only root filesystem in the container app. Gitignore `TestResults/`. Fold the two-file `core-insurance-infra` repo's docs into `docs/` or point to it. | P2 | S | — | Sonnet 5.5 |

**Totals:** UI 14, Testing 10, Edge cases 12, Hardening 17 (15 IDs, HD-08 is split a/b/c), Other 13 = **66 work packages**.

**P0 (12):** TST-01, TST-04, EC-01, EC-02, EC-03, HD-01, HD-02, HD-03, HD-04, OT-01, OT-02, OT-03.

---

## 6. Execution order (waves)

Wave rules:
- At most 15 concurrent Sonnet 5.5 builders.
- No shared files within a wave. `routes.tsx`, `navigation.ts` and `ci.yml` get at most one builder per wave.
- i18n json and module DI registration are appended additively (LIGHT-WORKFLOW).
- A wave can start as soon as its own dependencies are merged; it does not wait for the whole previous wave.

**Wave 1 — P0 and foundations (15 builders + orchestrator housekeeping)**
HD-01, HD-02, HD-03, HD-04, EC-01, EC-03, TST-01, TST-02, TST-04, OT-01, OT-02, OT-03, UI-01, UI-03, UI-05. The orchestrator runs OT-09 itself before the wave starts.
- Watch: HD-02 regenerates `pty` contracts, and UI-05 adds the doc/dat/rat/wrk web types; both touch `web/src/api/generated` in different subfolders.
- Watch: EC-01 and HD-03 both touch `tests/CoreIns.ArchitectureTests`, in different files.

**Wave 2 (15)**
HD-05, HD-08a, HD-08b, HD-08c, HD-09, HD-10, EC-02, EC-04, EC-07, UI-02, UI-09, TST-03, TST-05, TST-07, OT-05.

**Wave 3 (14)**
HD-06, HD-07, HD-12, EC-05, EC-09, EC-12, UI-06, UI-07, UI-08, TST-06a, TST-06b, OT-04, OT-06, OT-08.

**Wave 4 (10)**
EC-06, EC-08, EC-10, HD-11, HD-13, HD-15, UI-04, UI-10, OT-07, OT-10.

**Wave 5 (11)**
EC-11, TST-08, TST-09, HD-14, UI-11, UI-12, UI-13, UI-14, OT-11, OT-12, OT-13.

**After each wave**
- Rebuild the coreins Docker stack and re-walk the smoke route list (`orchestration/ux/smoke-2026-10-20/report.json` format).
- Share UI screenshots against the mockup as FYI.
- Update `STATUS.md` and the backlog tracker.
- Any open regulatory value keeps its fail-closed default and is recorded as a decision (D-USR-19).
