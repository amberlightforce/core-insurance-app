# Gap-survey notes (planner input)

> Raw risk and sequencing notes from the six surveys that produced the build-out items. Read the relevant group before starting an item in its modules. Decisions listed here are consolidated in `README.md` §5.

## PFC, RAT, UW

- **Biggest gap:** the product factory is a read-only runtime. Products exist only through `import` of a JSON document (and the seeds `Seed/motor-gr*.product.json`). Nobody can author, review or release a product in the app. `/products` is a nav link to a `ModulePlaceholder` (NX-UI-14); `web/src/api/generated/pfc.d.ts` exists but nothing uses it.
- **Fake or stubbed:** `rates/rate-batch`, `worksheets/explain`, `worksheets/attach` are registered but throw DATA-UNAVAILABLE. Rating tables, tax-plan thresholds and UW rule thresholds are made up (D-SLC-04): `ILLUSTRATIVE_TEST_DATA` flag in `Rating/Domain/Artefact.cs` (`DataStatus`) and `BuiltInArtefacts.IllustrativeNote`. UW rule sets are built in code (`BuiltInRuleSets`) not stored through an API; `uw.rule_set_version` exists but nothing but seeding writes it (to confirm by the builder of BO-UW-01).
- **Contract oddity:** `pfc.yaml` has both `ProductVersion.fallBack` (`/fall-back`) and `ProductVersion.fallback` (`/fallback`); the controller serves only `/fallback`. Decide: delete the duplicate from the contract (cheap, recommended) rather than implement twice. Same for `ChangeSet.revert` naming in generated web types.
- **User decisions needed:** (1) Is a second product line (Home etc.) in scope? Without one, PFC authoring is exercised only on motor (BO-PFC-09). (2) Multi-vehicle/multi-driver rating input schema (D-SLC-16) affects PFC element cardinality, RAT, UW rules and POL: one design before BO-RAT-10. (3) Whether policy-hold `check` becomes a mandatory gate on bind in POL (affects POL/QTE flows, BO-UW-09). (4) Real external-report providers (UW-009) are out of reach; only a fake provider is proposed.
- **Ordering:** PFC lifecycle (BO-PFC-01 then 04) and RAT lifecycle (BO-RAT-05 then 06) and UW (BO-UW-01 then 02) are three independent backend chains; their UI items (PFC-10..12, RAT-14, UW-14..16) touch separate `web/src/modules/*` folders and can run in parallel, but all three edit `web/src/routes.tsx` and `web/src/app-shell/navigation.ts`: serialise those edits or give one builder the route wiring.
- **Shared-file risk:** BO-PFC-01/02/03/04 all add to `Persistence/ProductDbContext.cs` and migrations: run them in sequence, not parallel. Same for BO-RAT-01/06/07/09 (`RatingDbContext.cs`, `RatingServices.cs`) and BO-UW-01/02/05/07/08/09 (`UnderwritingDbContext.cs`).
- **Event publication:** generated events exist for PFC Submitted/Approved/Scheduled/Retired/Returned, RAT Activated/Scheduled/Withdrawn/Published, UW Decline/Contingency/PolicyHold/InspectionCompleted/ExternalReportReceived. Only `ProductVersionPublishedV1` and `RatingCalculatedV1` are known to be published today; `EventSchemaAllowList.txt` in `tests/CoreIns.IntegrationTests` needs updating with each new event.
- **Authority / maker-checker:** new approval and authority types (PFC.Release, RAT.Release, RAT.PRICE_DEVIATION, UW.RULESET_APPROVAL) should follow the existing `PFC.Fallback` pattern (`AddOwnerDecidedApprovalType`, `AddAuthorityType`); four-eyes defaults stay off for UW per D-CON-17.
- **Size caution:** BO-PFC-01, BO-PFC-04, BO-RAT-06 and BO-UW-01 are L and each includes a migration; if a builder runs long, split at the command boundary (create/edit vs abandon; submit/sign-off vs schedule).

## POL, PTY, CHN

**D.1 Where the portals should live (decision requested).**
- Recommendation: put both portals in the existing Vite app (`web/`) as separate route trees with their own shell: `/portal/*` for customers (`web/src/portal/`) and `/agency/*` for brokers and agents (`web/src/agency/`), mounted in `web/src/routes.tsx` beside, not under, the staff `AppShell` route (the staff root is `path: '/'`, line ~508).
- Why this is simpler: one build, one Docker image, one dev sign-in, one generated API client (`web/src/api`), the same design system, tokens, i18n loader and test setup, and the withdrawal page already lives there. Role guards (`RequireRole`) already exist. Cost is a few days less than a second app.
- Isolation that keeps the option open: portal code imports only from `design-system`, `api`, `i18n`, `format` and `auth`, never from `modules/*` staff pages (an ESLint `no-restricted-imports` rule enforces this), and the staff `NavRail` and `AppShell` are never rendered for these roles.
- Alternative: a second Vite entry (`web/portal.html` + `vite.config.ts` multi-page build) over the same source, served at a different host. Needed only when REQ-CHN-310 (independent deployment of front ends) or a different CSP/domain for customers is required. This is a cheap step later, if the route-tree rule above is respected. A fully separate repo or package is not worth it now.
- Customer pages must be mobile-first (D-USR-01: responsive web only); the mockup covers only three staff screens, so the portal follows the Aegean language per NX rules.

**D.2 Security and scoping risks.**
- `Customer.Portal` today only grants the withdrawal operations (`src/CoreIns.Host/permissions/chn.json`); there is no user-to-party link, and the pol/pty/bil/doc permission files give it nothing. Every portal read must go through a CHN scope service (BO-CHN-01), not by adding `Customer.Portal` to the staff endpoints. Giving the role `pol.Policy.get` directly would expose every policy in the legal entity.
- The withdrawal declare page at `channels/withdrawal` is unauthenticated in the UI sense (EU rule: reachable without login); the API still needs `Customer.Portal`. Check how an anonymous declaration is meant to authenticate before BO-CHN-12 moves the page. This needs a user decision (public anonymous token with rate limit vs sign-in).
- Broker scoping (REQ-CHN-227, BR-CHN-057 agent-client data release) depends on producer-of-record and agency grants, so BO-CHN-09/10/14 must not ship before BO-PTY-09 and BO-PTY-12.

**D.3 Things that look built but are partial or stubbed.**
- PTY sanctions screening is a stub marked `STUB-NO-SANCTIONS-LIST` (always Clear outside Production, fails closed in Production). Production go-live is blocked until BO-PTY-07 plus a real provider adapter. User decision: which list provider.
- `PoliciesHomePage.tsx` still tells users there is no policy search although the endpoint exists (BO-POL-01 removes the gap).
- `Policy` copies the producer code as a string onto each term; there is no producer-of-record entity, so a producer change cannot be done without a policy transaction until BO-PTY-09.
- The party DSAR participants exist (`Party/Services/Dsar`, `Policy/Services/Dsar`, `Channels/Services/Dsar`) but the module-level `*.Dsar.*` operations in the contracts are not routed; erase/subject-match gaps remain under NX-HD-09.
- `chn.json` permissions list `chn.Ticket.*` and `chn.Agent.mcp` but no controller serves them.
- `pty.yaml` has no operations for agency administration (REQ-PTY-243..252), nor a `duplicates` read; both are contract additions (BO-PTY-12, BO-PTY-05). The contract owner must approve those additions.

**D.4 Sequencing hint.** Backend order that unblocks the most: BO-PTY-01/02 then BO-PTY-03/04 (accounts), BO-POL-02/03, BO-CHN-01, then BO-CHN-02/03/04 with BO-CHN-12 in parallel. File-conflict warnings for parallel builders: BO-POL-07 and BO-POL-08 both want `src/Policy/Api` lifecycle routes (use separate controller files as stated); BO-PTY-01/02/05 all edit `PartiesController.cs` or `PartyRows.cs`, so run them one after another or give each its own controller file; BO-CHN-02/03/04 each get their own controller file but share `ChannelsDbContext.cs` (migrations should be merged by one builder).

**D.5 Deferred.** W6-CHN-03 and W7-CHN-02 (mobile-only features, D-USR-01) and bancassurance sections (D-200(b)) are intentionally not proposed. Push notifications are a deviation (D-USR-01): in-app plus e-mail/SMS only.

**D.6 Open decisions for the user.** (1) Portal placement as in D.1. (2) Anonymous vs signed-in withdrawal (D.2). (3) Screening provider. (4) Whether billing staff should get `pol.Policy.search`. (5) Whether to approve the `pty.yaml` contract additions.

## BIL, FIN

- **Fake-looking UI (biggest risk):** NX-UI-10 shipped screens for manual journals, intake-exception actions, period close and earning run, but the backend only has GET trial-balance, GET periods, GET intake-exceptions and journal/posting-rule reads. `web/src/modules/finance/ops/api.ts` calls `manual-journals*`, `intake-exceptions/{rerun,mark-duplicate,reject}`, `close/*` and `earning/*`, all of which 404 today; they pass vitest only because the API is mocked. BO-FIN-01a/01b, 03, 04 and 06a are therefore P0, and BO-FIN-17 adds a real end-to-end check. NX-TST-04 (route vs OpenAPI) would have caught this.
- **Period close is dev-only:** a period can only be locked through `DevPeriodsController` (`FinanceControllers.cs:202`). Production has no way to close or lock a period, so the closed-period guards used by POL cannot be exercised for real.
- **ANNUAL-only billing:** `PlanInstanceRow` exists but only ANNUAL is ever billed. Mandates, direct debit and down-payment bind all depend on NX-EC-11. Order: EC-11, then BO-BIL-03, 04, 06a, 06b.
- **No mandate or suspense tables:** the `/billing/mandates` and `/billing/suspense` screens in NX-UI-14 cannot be built until BO-BIL-06a and BO-BIL-08 exist; NX-UI-14 should be replaced by BO-BIL-19..22 (its split) with those dependencies.
- **User decisions needed:** (1) Agency bill and commission (W6-BIL-02) and IFRS 17, FX, GL extract and group pack (W8-FIN-01/02) are large; I found no D-USR deferral for them, so they are proposed P2. Confirm MVP scope. (2) Does the GL extract have a real target system and format? (3) IPT and levy returns depend on open legal questions OQ-010 and OQ-011; BO-FIN-09/10 must ship with PendingOpinion rows and Production refusal.
- **Contract drift:** `GET migrated-records/{receivables,mandates}` (`BillingReadControllers.cs:56`) are not in `bil.yaml`; add them or drop them once BO-BIL-06a gives mandates a real read. `Receivable.list` was not found in `ReceivablesController.cs` (only register and get); the web list uses migrated-records.
- **File-collision hints:** BO-BIL-01, 03, 04, 07 use new controller files so they do not clash on `Api/BillingControllers.cs`. BO-FIN-01a/03/04/06a each add a controller and a migration under `src/Finance/Persistence/Migrations`; give migrations distinct timestamps and do not run two in one wave without coordination. `routes.tsx` and `navigation.ts` get one UI builder per wave.
- **Counts:** BIL 24 items (BO-BIL-01..22; 06a/06b and 15a/15b are split), FIN 24 items (BO-FIN-01a/01b, 02..05, 06a/06b, 07..13, 14a/14b, 15..21).

## CLM, RI, MIG

**Risks and fakes**
- RI is the largest gap: 16 of 116 operations, and the whole premium side (deposits, items, bordereaux, statements, cash calls, settlements) is absent. Recoveries are booked, but nothing turns them into a reinsurer account or cash. Decide early whether a go-live without RI accounting is acceptable; if not, BO-RI-10..16 are P0 and the plan is one to two weeks of builders.
- Contract type is hard-locked to `XOL_PER_RISK` (check constraint `ck_contract_type`). The quota-share cession already works, but from a dev-seed treaty row (`Registry/CessionTreatyRegistry.cs` header says approved DEV_SEED, never updated). So QS cession in Production has no treaty source. NX-HD-07 moved the intake to the registry only for the XoL contracts. User decision: D-PRG-10 says motor RI is XoL only; is QS/surplus wanted for non-motor (BO-RI-06)?
- Programme is a read projection, not an aggregate (`Cession/RiReadModels.cs:162`). `Programme.list/get` exist but cannot be written, so the UI for programmes cannot offer create or renew until BO-RI-01.
- MIG: the 22 operations all exist, but they cover only the mechanics (batches, xref, routing). The "archive" is the stored batch item rows, and the "enquiry" returns routing marks only. Neither is a legacy archive or a real cross-system view. The RI importer opens one tracker aggregate per treaty year and nothing else. There is no Party importer, so a real conversion cannot start (BO-MIG-06 is P0).
- Reconciliation, sign-off and cut-over have no code and no test (only the per-currency BIL/FIN reconciliation test mentioned in NX-EC-10). They are the go-live gate in PRD-16 ("no wave closes without named signatories"). BO-MIG-10/11 are P0.
- Batches execute inside one HTTP request (`BatchCommands.cs:415`), so pause is not real and a large batch can time out. BO-MIG-01 is P0 for any realistic volume.
- CLM MTPL: third-party MTPL exposures work only with `statutory_clocks = NOT_TRACKED` and are refused in Production (D-SL4-09). The CMP clock service now exists (it is used by the claim importer), so BO-CLM-05 is the single step that makes MTPL claims usable in Production. Clock values stay Unverified (D-REG-04, OQ-008); keep them fail-closed.
- Claims authority limits are illustrative (D-SL2-03, D-SL4-08). They need business values before go-live; not a build item.
- Transaction-set approve/reject/return exist only through the platform approvals API, so the contract shows them as missing while the behaviour works. BO-CLM-04 closes the contract gap; if the planner prefers, mark those three operationIds as allow-listed (platform path) in the NX-TST-04 conformance list instead of building routes.
- DSAR operations (`clm.Dsar.*`, `ri.Dsar.*`) are served through the Compliance DSAR engine and the module participants, not through module routes. Likewise `*.Import.*`. Decide per module whether to add routes (BO-CLM-14, BO-RI-22, BO-RI-21) or allow-list them; the contract allows both.
- Erase in the CLM and RI DSAR participants is a no-op for "non-regulated classes" (`ClaimsDsarParticipant.cs:61`, `ReinsuranceDsarParticipant.cs:61`). Fine until F-404 retention classes exist.

**Deferrals to respect**
- D-PRG-10: motor RI is excess-of-loss only, so FAC (BO-RI-18), accumulation (BO-RI-19), surplus (BO-RI-06) are P2.
- D-ARC-13: no PostGIS; accumulation uses zone codes (BO-RI-19).
- D-ARC-09: only synthetic data; real-volume rehearsals are out (BO-MIG-12 stays synthetic).
- D-PRG-09: scenario B (convert at renewal) is the planning basis. Scenario A needs only reference loads and the conformance harness.
- WRK OCR and mobile deferrals do not touch these modules.

**Ordering hints**
- CLM: BO-CLM-01 before 02, 03, 05, 16. BO-CLM-04 touches `FinancialsControllers.cs` alone. BO-CLM-09/10 share a controller file, so one builder or two waves.
- RI: BO-RI-03 is the foundation for 04, 05, 10, 21, 25 (contract versions). BO-RI-11 (items) is the foundation for 13..16, 20. DOC template engine (a DOC WP in another survey) gates BO-RI-12, 13, 18 and BO-CLM-07, 12.
- MIG: BO-MIG-02 -> 03 -> 04 feed 07, 17. BO-MIG-06 gates 05, 07, 10. Contract additions (new operationIds in `mig.yaml`) need ContractGen regeneration and web types (`NX-UI-05` merged), one builder per wave for `contracts/openapi/mig.yaml`.
- Items that add contract operationIds not in the master contract: BO-CLM-13 (litigation), BO-MIG-02/03/04/07/10/11/12/15. Mark them contract additions in the item PR.
- Only 3 of the items here have any dependency on open NX work (NX-TST-04 allow-list removal applies to all new routes; the rest are free of open NX items).

## DOC, CMP, WRK

1. **Compliance landing is shallow.** NX-UI-09 merged only a landing (`modules/compliance/home/ComplianceHomePage.tsx`) that lists clocks and complaints; `home/api.ts` itself says fields are read defensively and commands follow later. No complaint detail, no clock actions. Marked partial; BO-CMP-01..03 carry the real UI. The nav still carries a `/documents` placeholder.
2. **Documents is a letter stub, not a document system.** Four constant HTML templates, stub post/email providers (blocked in Production), no PDF at all. Callers (POL withdrawal/cancellation, BIL non-payment, CHN) only depend on `IDocumentRequestService`; keep that contract stable through BO-DOC-05..07. Real post/e-mail provider selection is an open vendor decision; no item proposed.
3. **Contract gap:** no document-library search op exists in `doc.yaml` (BO-DOC-01 adds `doc.Document.search`). `wrk.yaml` has no delegation/absence or pattern-admin ops (BO-WRK-03, -06 add them). `cmp.yaml` has no obligations/regulatory-change ops (BO-CMP-13). Contract edits regenerate web types in `web/src/api/generated`; schedule these builders so they do not overlap with other contract-editing items.
4. **User decisions needed:** (a) CMP_COMPLAINT_ACK business-day value (OI-CMP-06 Unverified) and complaint redress approval threshold (EUR 500 per BR-CMP-031 is in a TODO); (b) whether obligations register/regulatory change (W8-CMP-03) is in P1 scope; (c) Gotenberg and ClamAV as new container dependencies (infra + bicep cost); (d) real myDATA/e-invoicing provider and the Greek XBRL taxonomy are Unverified (OQ-012) so BO-CMP-06/07/11 stay on the stub channel; (e) retention class durations are empty (D-REG-07): BO-DOC-04 must not invent them.
5. **Looks built but is stubbed:** fiscal documents run on a stub channel with synthetic MARK/UID (`Domain/FiscalDocuments.cs` `IsStub`, `Services/InProcessServices.cs:56` NOT-AVAILABLE); `ComplaintsController.cs` procedure text is a stand-in; WRK patterns/queues are code constants; WRK `WorkDsarParticipant` reports "nothing held" for notes that do not exist yet, so it must be extended when BO-WRK-07 lands (otherwise DSAR erase silently misses notes).
6. **File collisions to avoid:** BO-DOC-01/02/04 all edit `Api/DocumentsController.cs` and `Queries/DocumentReader.cs` (run sequentially or split controllers into partial classes in the first one); BO-CMP-06/07 share `FiscalDocumentsController.cs`; BO-WRK-01/02/03 share `Work/Commands/ActivityEngine.cs` and `Api/ActivityController.cs`. BO-DOC-12/13 share `web/src/modules/documents/`; BO-WRK-08 and BO-WRK-15 touch pages owned by policy/party/home builders, so run them in a later wave.
7. NX-TST-08 (compliance/DSAR edge tests) and NX-HD-09 (DSAR erase) are still open and overlap BO-CMP-12/BO-WRK-17/BO-DOC-11 on DSAR participants; sequence after them.
8. Counts: DOC 14 items, CMP 14 items (BO-CMP-14 is a re-split note, not a build), WRK 17 items.

## DAT, PLT, MKT

1. **DAT is the largest hole.** `src/Data` has no marts, no ingestion and no analytics (`DataModule.cs:22` says so). Only GDPR ops on a stand-in `subject_fact` table and the model registry exist, so the missing `/reports` page has no back end at all. BO-DAT-01..04 are the minimum for a real `/reports`; BO-DAT-11 cannot be built against anything until BO-DAT-03 lands. Possible UI cheat if the user wants a quick /reports: a page of KPI tiles fed by each module's existing list endpoints. That would duplicate logic, so it is not proposed.
2. **Decision needed: report catalogue.** Which marts the business wants first is not in the PRD digests beyond regulatory ones, whose layouts are Unverified (SII QRT, A.1004, national). BO-DAT-03 proposes three management marts; the user should confirm. Regulatory layout work (BO-DAT-04/09) must stay `Unverified` and not be treated as filing-ready.
3. **Decision needed: identity ownership.** BO-PLT-05/06 add an in-app user directory and role assignments, while today roles come only from Entra app roles (`SecurityExtensions.cs`). The user must decide whether the app (a) mirrors and audits Entra roles only, or (b) becomes the grant source (then the token role claim must be reconciled). External ID realm details for BO-PLT-06 are not in the repo.
4. **Looks built, but is thin.**
   - Authority is config-driven grants (`AuthorityOptions`; illustrative grants are dropped outside Development), not data. There is no way to change a limit at run time.
   - `FeatureFlag.evaluate` is one global boolean.
   - Pack lifecycle covers only rollback and scheduled activation; GR/CY packs are compiled code, so "publish a pack version" has no runtime meaning until BO-MKT-04.
   - `dev/subject-facts` in Data and `dev_clock` in Platform are dev-only paths; production DSAR on DAT is therefore untested against real mart data.
5. **/admin heading bug.** The heading «Home» comes from `ModulePlaceholder` in `web/src/App.tsx:124`: `useCurrentNavItem()` finds no match for `/admin` because every admin nav entry points at a sub-route. Fixed in BO-PLT-12.
6. **Audit has no read API.** Compliance and the DSAR export cannot be demonstrated end to end in the UI until BO-PLT-01 lands, though the chain itself works.
7. **Overlaps to keep out.** Do not re-do: paging helper (NX-HD-12), HTTP hardening and correlation ids (NX-OT-05/06), DSAR participants (NX-HD-09), AI server-side facts (NX-HD-14), WRK module (NX-HD-05; the `/admin` tiles and approvals UI do not depend on it). PLT workflow engine ops (`Workflow.start/signal/query/cancel/terminate`) are deliberately not proposed: D-ARC-04 replaced the workflow engine with state machines plus deadline scanners, so these five operations should be removed from `plt.yaml` or left on the NX-TST-04 allow-list (user to confirm).
8. **File-collision hints for the planner.** BO-PLT-01/02/03/04 each add their own controller file; only BO-PLT-02 edits `ApprovalsController.cs` and only BO-PLT-04 edits `DeadLetterApi.cs`. BO-MKT-04 and BO-MKT-13 share the pack files: run in the order 04 then 13. BO-PLT-12 and BO-DAT-11 both edit `web/src/app-shell/navigation.ts` and `web/src/auth/roles.ts`: do not run them in the same wave as BO-MKT-11 and BO-PLT-13.

