# Master-plan coverage (what of W1–W9 is really built)

> The master backlog (`../backlog/BACKLOG.md`, `backlog.json`) still says "not started" for every work package. This file is the real picture as of 2026-11-27 (main `a49ab865`), from six read-only code surveys. Each gap is closed by the build-out item listed in the item files' **Covers** column.

Built: `done` / `mostly` / `partial` / `none` / `deferred`.

## PFC, RAT, UW

### Work packages
| Master WP | Title | Built | Evidence | Missing (short) |
|---|---|---|---|---|
| W2-PFC-01 | Product structure, metadata, elements, validation | partial | `src/Product/Domain/ArtefactCompiler.cs` (Lint PFC-LINT-*), `Persistence/ProductRows.cs` (product, product_version), `Seed/motor-gr.product.json` | Only import of a whole JSON document. No draft/edit path, no UI, one product (MOTOR-GR 1.0 and 1.1) |
| W2-PFC-02 | Coverages, terms, offerings, question sets | partial | `Queries/CatalogueQueries.cs` (`GET catalogue/{id}`, `get-item`), `Domain/QuestionEvaluator.cs` (`POST question-sets/evaluate`) | Offerings and demands-and-needs (PFC-112), peril tags (PFC-094) not exposed or authored. No editing |
| W2-PFC-03 | Charge types, references, regulatory mapping, POG/IPID | partial | `GET charge-types` (`CatalogueQueries.ListChargeTypesAsync`), `references` block in the artefact | No `regulatory-mappings`, `pogs`, `legacy-xrefs`, `reference-tables` endpoints or tables; no POG review/test records, no IPID data |
| W2-PFC-04 | Versioning, resolution, runtime services | mostly | `POST product-versions/resolve` (`Queries/ProductReader.cs`), `GET artifacts/{id}`, in-process `Services/InProcessServices.cs` | `availability/check`, `products/describe`, `policy-drafts/validate` missing |
| W2-PFC-05 | Authoring/build, review/approval/release, test assets | partial | Compiler RFC 8785 + SHA-256 (`ArtefactCompiler.cs`), `POST product-versions/import` (locks), emergency fall-back with maker-checker (`Commands/Fallback.cs`, approval `PFC.Fallback`), `ProductVersionPublishedV1` emitted | Change sets, submit/sign-off/schedule/cancel-schedule, diff/merge, YAML import/export, Submitted/Approved/Scheduled/Retired/Returned events, test assets |
| W6-PFC-01 | Renewal version conversion rules | none | none (`renewal-conversions/convert` absent) | Conversion rules, endpoint, E2E-04 use |
| W3-RAT-01 | Rating contract, runtime, artefact binding | mostly | `POST rates/rate` (`Api/RatingController.cs`, `Services/RatingServices.cs`), `Domain/RatingEngine.cs`, three-hash binding, worksheets `GET worksheets/{id}`, `POST rating-artifacts/resolve`, modes FULL/QUICK/DRY_RUN/ENDORSEMENT/RENEWAL (`Services/ModesPolicy.cs`) | `rateBatch` throws DATA-UNAVAILABLE (+ getBatch/getBatchResults/cancelBatch); only annual terms and 1 vehicle + 1 driver (D-SLC-16); no caching beyond artefact cache |
| W3-RAT-02 | Algorithm framework, explainability | partial | Step kinds BASE/FACTOR/MIN/ROUND in `Domain/Artefact.cs`; worksheet carries step trace | `worksheets/explain` and `attach` throw DATA-UNAVAILABLE; no `breakdowns/{id}`, `change-explanations`, `reperform`, `export`; no other step kinds (loading, discount, cap) |
| W3-RAT-03 | Taxes, discounts/loadings, lawful pricing, proration | partial | Tax plan via MKT config (`RatingServices.cs`, GR-IPT), `Services/ProrationService.cs` + `Api/ProrationController.cs` (`proration/prorate`), `Services/ServicingTaxService.cs` | `day-count/year-fraction`, pricing modifications (propose/withdraw/get/list), price reviews, referral signals, bonus-malus, statements |
| W3-RAT-04 | Table import, test assets, approval, rollback | none | Tables and artefact only seeded from `Domain/BuiltInArtefacts.cs` by `RatingStore.EnsureSeededAsync`; DB has `rate_table_version`, `rating_artifact`, `rate_activation` | No rate-table import/validate/diff, artefact build/submit/return/schedule/cancel/rollback, golden suites, changeset |
| W8-RAT-01 | CompareVersions, ShadowRun, monitoring supply | none | none | compare-versions, shadow-runs, impact-analysises, statements |
| W9-RAT-01 | Rating migration and coexistence | none | none (`legacy-ratings/import` absent) | Legacy rating import, coexistence |
| W3-UW-01 | Rule authoring and evaluation runtime | partial | `POST rules/evaluate` (`Commands/EvaluateRules.cs`), `Domain/RuleSets.cs` (decision tables on `CoreIns.Rules`, test-case gate, `BuiltInRuleSets` UW-MOTOR-GR-Q/-B) | `rules/evaluate-batch`; all `rule-sets/*` authoring, `issue-types/*`; no accumulation/NatCat check |
| W3-UW-02 | Issue lifecycle, approvals, authority at decision | mostly | `GET issues`, `GET issues/blocking-status`, `POST issues/decide` (`Commands/DecideIssues.cs`, `Domain/DecisionEligibility.cs`, `IssueReconciliation.cs`), fingerprint invalidation, authority `UW.ISSUE_APPROVAL` | `issues/{id}`, `explain`, `reopen`, `add-manual`, `close`, `list-for-channel`, `approval/history`; `ApprovalInvalidatedV1` publication unverified |
| W3-UW-03 | Referral routing, SLA, workbench | partial | `GET referrals`, `GET referrals/{id}` (`Services/ReferralQueries.cs`), web `web/src/modules/underwriting/workbench/*`, routes `/underwriting`, `/policies/referrals` | `referrals/request|withdraw|refer-up|request-information`, `review/lock|release`, WRK task routing, SLA/priority |
| W3-UW-04 | External data, inspections, declines, contingencies | none | Only the automated DECLINE outcome (D-SLC-17: job stays Draft, no record) | declines + refusal register, contingencies, external reports, inspections, valuation, intakes, nat-cat |
| W3-UW-05 | Policy holds, operations, data protection | partial | `Services/Dsar/UnderwritingDsarParticipant.cs` (DSAR via PLT, subject matching done in NX-HD-09) | `policy-holds/*`, accumulation, `data-subject/*` HTTP endpoints, restriction marker (TODO(hardening) line 12) |
| W6-UW-01 | Renewal and in-force UW | none | none | `renewal-directions/*`, disclosure findings, renewal checkpoint rules |

### Contract operations
**PFC: 10 of 34 operationIds implemented** (resolve, import, fallback, decideFallback, Artifact.get, Catalogue.get, Catalogue.getItem, ChargeType.list, QuestionSet.get, QuestionSet.evaluate). Contract lists both `fallBack` (`fall-back`) and `fallback`; only the latter is built (see D).
Not implemented:
- Authoring: ChangeSet.create, ChangeSet.edit, ChangeSet.revert, ChangeSet.rebase, ChangeSet.abandon, ProductVersion.diff, ProductImport.import
- Release: ProductVersion.submit, signOff, schedule, cancelSchedule, fallBack
- Runtime: Availability.check, Product.describe, PolicyDraft.validate, RenewalConversion.convert, ImpactAnalysis.run
- Reference data: RegulatoryMapping.get, Pog.get, Pog.update, Pog.recordReview, Pog.recordTest, LegacyXref.get, ReferenceTable.get

**RAT: 4 of 47 implemented** (Rate.rate, Worksheet.get, RatingArtifact.resolve, Proration.prorate).
Not implemented:
- Rate/batch: Rate.rateBatch, getBatch, getBatchResults, cancelBatch (service stub throws)
- Worksheets: Worksheet.explain, reperform, export, attach (explain/attach stubs throw)
- Pricing: DayCount.yearFraction, PricingModification.propose/withdraw/get/list, Breakdown.get, ChangeExplanation.get, PriceReview.request/decide, BonusMalus.transition, Statement.get
- Artefact lifecycle: RatingArtifact.get/list/compatibility/build/submit/return/schedule/cancelSchedule/rollback, RatingChangeSet.create, RateTable.import/validate/diff, GoldenSuite.run/requestRebaseline
- Analysis: Comparison.compareVersions, ShadowRun.start/stop/get, ImpactAnalysis.run/get/resume
- Migration/privacy: LegacyRating.import, Dsar.export

**UW: 6 of 82 implemented** (Rules.evaluate, Issue.blockingStatus, Issue.list, Issue.decide, Referral.list, Referral.get).
Not implemented:
- Rules: Rules.evaluateBatch, RuleSet.create/draft/edit/validate/test/simulate/submit/approve/schedule/export/import, IssueType.search/get/upsert
- Issues: Issue.get/explain/listForChannel/reopen/addManual/close, Approval.history
- Referrals: Referral.request/withdraw/referUp/requestInformation, Review.lock/release
- Declines: Decline.create/get/requestReview/decideReview, RefusalRegister.query/export
- Contingencies: Contingency.create/list/update/satisfy/waive/extend/applyConsequence
- Holds/accumulation: PolicyHold.check/create/update/copy/activate/release/impact, Accumulation.check, NatCat.assess
- External data: ExternalReport.order/get/list/reorder/cancel, Inspection.order/submitResult/complete/get, Valuation.compute, Intake.create/get/verifyValue/clear/accept/reject
- Renewal: RenewalDirection.get/set/bulkSet, DisclosureFinding.record/decide/get/list
- Privacy: DataSubject.export/restrict/erase

## POL, PTY, CHN

### Work packages
| Master WP | Title | Built | Evidence | Missing (short) |
|---|---|---|---|---|
| W2-PTY-01 | Party master, identifiers, names/search, addresses | mostly | `src/Party/Api/PartiesController.cs` (create, get, search GET and POST), `Domain/NameForms.cs`, `Persistence/PartyRows.cs` (names, search keys, identifiers, addresses, contact points), `web/src/modules/party/*` | No update, validate or merge. No identifier verify. No address or contact-point endpoints (they can only be set at create). No party edit UI. |
| W2-PTY-02 | Roles, accounts/households, dedupe/merge, party 360 | none | Only `Services/Ai/DuplicateExplainer.cs` and state models in `Domain/StateModels.cs` | No role, relationship, account, household, duplicate-detect, merge/unmerge or 360 view. No tables for them. |
| W2-PTY-03 | Consent, vulnerability, sanctions screening | partial | `Api/VulnerabilitiesController.cs` (query/record/remove, HD-02), `Api/ScreeningController.cs` + `Services/ScreeningStub.cs` | Consent and communication preferences absent. Screening is a stub (always Clear outside Production, fails closed in Production). No case queue, no bulk. |
| W2-PTY-04 | Intermediaries, producer codes, import conventions | partial | `Api/IntermediariesController.cs` (create, PATCH, producer-code validate/search), `Persistence/PartyRows.cs` `IntermediaryRow`, `ProducerCodeRow` | No suspend/terminate, no register import, no CPD, hierarchy, authorities or appointments, conflict-of-interest data. No UI. |
| W6-PTY-01 | Life events, account merge/policy move, DSR support | none | `Services/Dsar/PartyDsarParticipant.cs` and `DsarRestrictionRow` only | Life events, account merge/split/policy move, pty.Dsar.* endpoints. |
| W6-PTY-02 | Producer of record, commission agreements, agency admin | none | Policy carries a plain `ProducerCode` string (`src/Policy/Commands/BindJob.cs:207`) | Producer-of-record history/change/transfer, commission agreement model and resolve, agency users and grants (no operations in `pty.yaml` for agency admin). |
| W9-PTY-01 | Party import API | none | `pty.yaml` has only `pty.Import.reverse`; no `Party/MigrationImport` | Importer and reverse. |
| W4-POL-01 | Policy/term, identifiers, jobs, concurrency | partial | `src/Policy/Api/PolicyControllers.cs` (submission, update-draft, quote, bind, job get), `Commands/JobSupport.cs` | Job list, new version, requote, copy, compare, rebase, resolve conflicts, lease, not-taken. |
| W4-POL-02 | Segments, snapshots, reverse-and-reapply | mostly | `GET snapshots/get`, `terms/timeline`, `Commands/Change/Oos/{OosPlanner,OosApplier}.cs`, `POST policy-changes/out-of-sequence` | `pol.Segment.changes` read. Edge tests are NX-EC-04/05. |
| W4-POL-03 | Charges, proration, billing contract, status model | partial | `Domain/Charges.cs`, `Domain/Servicing/*`, `Persistence` `ChargeLineRow` | `pol.StatusModel.get`, `pol.EffectiveDate.limits`, `pol.Earning.compute`, `pol.Charges.reconcile`. |
| W4-POL-04 | Quote lifecycle, risk data, parties | partial | `Commands/{UpdateDraft,QuoteJob,UwEvaluation}.cs`, `web/src/modules/quote/*` | Requote, versions/compare, not-taken, withdraw is done (`jobs/withdraw`). Driver and other-party risk trees unverified. |
| W4-POL-05 | Bind/issue, policy file/search, import, integrity | mostly | `Commands/BindJob.cs`, `GET policies/{id}`, `policies/search` (GET and POST), `web/src/modules/policy/file/*` | Policy search not on the UI. `pol.Policy.getMany`. Import API. Integrity checks. |
| W6-POL-01 | Change, reinstatement/rewrite, prior-term changes | partial | `Commands/Change/*` (in-sequence and out-of-sequence change), `web/.../servicing/ChangeWorkspacePage.tsx` | Reinstatement, rewrite, suspension, reactivation, policy move. |
| W6-POL-02 | Cancellation, void, statutory lifecycle | mostly | `Commands/Cancellation/CancelPolicy.cs`, non-payment (`NonPayment/*`, PSR queue), `Commands/Withdrawal/SubmitWithdrawal.cs`, `servicing/CancellationPage.tsx` | Scheduled cancellation and rescind. |
| W6-POL-03 | Renewal | partial | `Commands/Renewal/{CreateRenewal,OfferRenewal,AcceptRenewal}.cs`, `servicing/RenewalPage.tsx` | Decline, non-renew, renewal run (batch start/pause/retry) with D-CON-14 timing. |
| W9-POL-01 | Policy import API | partial | `src/Policy/MigrationImport/{PolicyMigrationImporter,PolicyMigrationReverser}.cs` (MIG path) | `pol.Import.policy/term/reverse/convertCurrency` endpoints. |
| W4-CHN-01 | Channel foundation, permission matrix, partner API | none | Only the withdrawal module; `src/Channels/Persistence/ChannelsDbContext.cs` has `WithdrawalRequests`, `Tickets` | Permission matrix, partner API gateway, API products, clients, webhooks, portal shell, ChannelSession. |
| W4-CHN-02 | Identity journeys, motor quote-and-buy, wallet, a11y | none | none | Journeys, quote/buy, wallet, deep links. |
| W6-CHN-01 | Self-service, payments, documents, messaging, withdrawal | partial | `src/Channels/Api/WithdrawalsController.cs` (declare, confirm, close, get, list), `Commands/Withdrawals.cs`, `Services/WithdrawalSweeper.cs`, `web/src/modules/channels/withdrawal/*`, route `channels/withdrawal` | Withdrawal is done. Dashboard, policy view, change, renewal, billing, documents, notifications and messaging for customers: none. |
| W6-CHN-02 | Broker/agent portal, agency self-admin, co-view | none | none | All. |
| W6-CHN-03 | Mobile-app-only features | deferred | D-USR-01 | Not built in P1. |
| W7-CHN-01 | Claims in channels | none | `Channels/Ai/AgentTickets.cs` only | Customer and broker FNOL, tracking. |
| W7-CHN-02 | Mobile-app-only features | deferred | D-USR-01 | Not built in P1. |
| W8-CHN-01 | MCP facade, customer assistant, tracker consent | partial | `Channels/Ai/AgentTickets.cs` (ticket get/approve/reject permissions in `src/CoreIns.Host/permissions/chn.json`) | `chn.Agent.mcp`, ticket endpoints (none are routed), assistant, consent. Off by default anyway. |

### Contract operations
**PTY: 13 of 67 operationIds implemented** (party search/searchByCriteria/get/create, vulnerability query/record/remove, screening screen, producer-code validate/search/searchByCriteria, intermediary create/update).
Not implemented, by area:
- Party: update, validate, merge, unmerge; Identifier.verify
- Roles/relationships: PartyRole.assign/end/query; Relationship.add/end/query
- Accounts: Account.create/get/search/setHolder/addMember/removeMember/withdraw/merge/requestPolicyMove/split
- Locations: Location.add/update/remove
- Life events: LifeEvent.start/complete/reverse
- Consent/preferences: Consent.query/record/withdraw; CommunicationPreference.resolve/set
- Screening: Screening.bulk/caseDecide/caseGet
- Intermediary: suspend, terminate, importRegister
- Producer of record: get, change, bulkTransfer, cancel
- Commission: resolve, get, createVersion, submit, approve, compare, simulate
- DSAR/import: Dsar.export/evaluateErasure/restrict/liftRestriction; Import.reverse

**POL: 22 of 58 operationIds implemented** (plus one route not in the contract: `GET policy-changes/{jobId}/preview`).
Not implemented, by area:
- Jobs: requote, newVersion, copyVersion, compareVersions, notTaken, rebase, resolveConflicts, lease, releaseLease, list
- Cancellation: schedule, rescind
- Lifecycle: Reinstatement.create, Rewrite.create, Suspension.create, Reactivation.create, PolicyMove.execute
- Renewal: decline, nonRenew; RenewalRun.start/retryItem/pause
- Reads: Policy.getMany, Segment.changes, StatusModel.get, EffectiveDate.limits
- Finance: Earning.compute, Charges.reconcile
- Import/DSAR: Import.policy/term/reverse/convertCurrency; Dsar.export/restrict
- Queue: Psr.list, Psr.decide (the PSR work is served through WRK tasks, `Services/PsrWorkTasks.cs`)

**CHN: 5 of 86 operationIds implemented** (Withdrawal declare/confirm/close/get/list).
Not implemented, by area:
- Permission matrix: Permission.evaluate; PermissionMatrix.createDraft/updateEntry/runScenarios/submit/schedule/get/diff
- Customer journeys: Journey.start/resume/save/abandon; Quote.startMotor/startHome; Purchase.confirm; Wallet.start/status/cancel; Change.preview/confirm/refer
- Notifications and messages: Notification.list/markRead; NotificationSubscription.set; Message.send/list; Language.set/get
- Partner platform: ApiProduct.create/list/get/publish/deprecate/retire; Partner.register; Client.request/approve/suspend/revoke; Webhook.subscribe/verify/pause/replay/deliveries; Aggregator.quote; DeepLink.open
- Agent: Agent.mcp; Ticket.get/approve/reject; AgentLog.query/export; CoView.start/end
- DSAR: Dsar.export/restrict/erase
- Partner resources (31): PartnerProduct.describe/getIpid; PartnerQuote.create/newVersion/compareVersions/listIssues/requestReferral/bind; PartnerPolicy.getCoverNote/get/change/requestCancellation; PartnerDocument.listForObject/upload; PartnerBillingAccount.get; PartnerInvoice.createPaymentLink; PartnerCollection.report; PartnerAccountCurrent.get; PartnerCommissionStatement.list; PartnerClaim.report/getTracking; PartnerBook.changes; PartnerVendorService.request; PartnerVendorInvoice.submit

## BIL, FIN

### Work packages
| Master WP | Title | Built | Evidence | Missing (short) |
|---|---|---|---|---|
| W5-BIL-01 | Billing accounts, payment plans, bind gate, preview | partial | `src/Billing/Api/BillingControllers.cs` (GET billing-accounts/{id}); `BillingAccountRow`, `PlanInstanceRow` in `Persistence/BillingDbContext.cs` (accounts are created by intake) | account create/update/search/close/reopen/changePayer/attachTerm/moveTerm; PaymentPlan list/select/change; BillingPreview.compute; DownPayment status/initiate. Only ANNUAL billing |
| W5-BIL-02 | Charge intake, invoices, fiscal triggers, levies | mostly | `src/Billing/Commands/Intake.cs`, `Services/TermBilling.cs`, `Commands/FiscalOutcome.cs`, `Api/BillingControllers.cs` (invoices get/list), `Api/BillingReadControllers.cs` (tax-levy) | invoice sendCopy/dispute; TaxLevy.requestLevyPayment; instalment invoices (NX-EC-11); levy per authority and remitted-vs-outstanding (`Queries/TaxLevyReader.cs:14`) |
| W5-BIL-03 | Payment methods, mandates, receipts, allocation | partial | `POST payments/take`, `GET receipts/{id}`, `POST allocations/allocate` (BillingControllers.cs); `Services/Allocator.cs`, `Services/PaymentAdapters.cs` | no mandate or payment-instrument tables or endpoints (only migrated mandates read, `MigratedRecordsController`); Receipt.list; unallocate/reallocate; suspense list/suggest/transfer/refund-to-sender (only a `SuspenseReason` column on receipts); partial payments refused |
| W5-BIL-04 | Reversals and delinquency/dunning | mostly | `Api/DelinquencyControllers.cs` (get, list, record-contact, arrange, release-hold); `Services/DelinquencySweeper.cs`, `Services/NonpayService.cs`; `Commands/Delinquency.cs`; `src/Finance/Posting/PaymentVoidPosting.cs` | payment reversal/bounce is reachable only via dev bank controller (`Api/DevBankController.cs`); dunning schedule beyond the statutory notice; multi-entity sweep (DelinquencySweeper.cs:30) |
| W5-BIL-05 | Disbursement, bank recon, workbenches, ops | partial | `Api/DisbursementControllers.cs` (payee create/get, disbursements get/list/approve-release/stop/void); `Commands/Disbursement*.cs`, `Commands/PayeeAccounts.cs`; `Services/Ai/SuspenseMatch.cs` | Disbursement.request public API, batches, PayeeAccount.list/verify, Reconciliation.bank/invariants/fin, Import.* and Dsar.* REST (DSAR participant exists in `Services/Dsar/`) |
| W5-BIL-06 | Billing sub-ledger and FIN hand-off | mostly | `Domain/Ledger.cs`, `Services/LedgerWriter.cs`, `BillingEntryPostedHandler` in `src/Finance/Posting/` | Ledger.query/balance reads; BIL-vs-FIN control totals |
| W5-FIN-01 | Event intake and posting rules | mostly | `src/Finance/Posting/{Intake,IntakeHandlers,RenewalBoundHandler}.cs`, `Api/FinanceControllers.cs` (`GET posting-rules`), `Seed/gr-test.finance.v1..v5.json` | PostingRules draft/compile/test/previewImpact/submit/decide; intake-exception rerun/markDuplicate/reject |
| W5-FIN-02 | Journals, CoA, manual journals | partial | `GET journals/query`, `GET journals/{id}` (FinanceControllers.cs); `Posting/JournalWriter.cs`, `Posting/JournalReversal.cs`; `GET balances/trial-balance` (`Api/FinanceOpsControllers.cs`) | ManualJournal create/list/get/submit/withdraw/decide (no table, no migration; UI already calls them); Journal.reverse REST; Balance.get; multi-currency lines |
| W5-FIN-03 | Claims/RI entries, commission, tax/levies, actuarial intake | partial | `Posting/{ClaimFacts,RecoveryFacts,RiFacts,FsBilEntries}.cs`, `Posting/TaxTreatmentCheck.cs`, migration `20261021100000_LevyCreditPosting.cs` | commission accounting; TaxReturn.* and LevyReturn.* (16 ops); FiscalReconciliation.run/results; ActuarialInputs.extract; ActuarialResults.* |
| W6-BIL-01 | Refunds, write-offs, moving money | partial | `Api/RefundControllers.cs` (propose, decide, resubmit, get, list); `Domain/Refunds.cs`, `Commands/Refunds.cs`, `Commands/Credits.cs` | WriteOff propose/decide/post; Transfer.money; moving policies between accounts |
| W6-BIL-02 | Agency bill and commission | none | no controller, no table | IntermediaryCollection.report/reportFile; AccountCurrent get/list/dispute; Commission calculations/statements/dispute/runPayments/simulate |
| W7-BIL-01 | Payee accounts and claim money services | mostly | `Commands/PayeeAccounts.cs`, `Commands/Disbursements.cs`, `Services/InProcessServices.cs`, `Persistence/FsClearingDatabaseSql.cs` | PayeeAccount.list/verify; clearing payee account from BIL (NX-HD-11) |
| W8-FIN-01 | Earning, deferrals, IFRS 17 | none | no earning tables in `src/Finance/Persistence/FinanceDbContext.cs`; UI `web/src/modules/finance/ops/EarningRunPage.tsx` calls `earning/status` and `earning/run`, which 404 | Earning.run/status/simulate/result; Ifrs17Group.*; Ifrs17.movements; WrittenPremium.query |
| W8-FIN-02 | SII views, FX, GL extract | none | none | Fx.revalue/translate/runs; GLExtract.*; GroupPack.export |
| W8-FIN-03 | Period close, reconciliation, workbench | partial | `GET periods` (FinanceOpsControllers.cs), `FinancialPeriodRow`, dev-only `DevPeriodsController` (FinanceControllers.cs:202); UI `web/src/modules/finance/ops/{PeriodClosePage,TrialBalancePage,IntakeExceptionsPage}.tsx` | Period.status; Close.tasks/signOff/softClose/lock/reopen/acceptBreak/evidencePack; Reconciliation.exchange/detail/breaks/match/run |
| W9-BIL-01 | Billing import API | partial | `src/Billing/MigrationImport/{BillingMigrationImporter,BillingMigrationReverser}.cs` (driven by MIG, not by `bil.Import.*` REST) | Import.reverse, Import.convertCurrency endpoints; non-EUR (Importer.cs:121) |
| W9-FIN-01 | Opening-balance import API | partial | `src/Finance/MigrationImport/{FinanceMigrationImporter,FinanceMigrationReverser}.cs` | Import.openingBalances/ifrs17Groups/earningBaseline/lic/reverseBatch/convertCurrency endpoints; foreign-currency TB (Importer.cs:105) |

### Contract operations
**BIL: 27 of 90 operationIds implemented** (`contracts/openapi/bil.yaml`). Implemented: BillingAccount.get; Invoice.get/list; Payment.take; Receipt.get; Allocation.allocate; TaxLevy.periods/detail; Delinquency.get/list/recordContact/arrange/releaseHold; PayeeAccount.create/get; Disbursement.get/list/approveRelease/stop/void; Receivable.register/get; Refund.propose/get/list/decide/resubmit. Extra routes not in the contract: `migrated-records/{receivables,mandates}` (BillingReadControllers.cs:56 TODO), `dev/bank/*`, `dev/bil/delinquency/sweep`.

Not implemented (63):
- Accounts and plans: BillingAccount.create/update/search/close/reopen/changePayer/attachTerm/moveTerm; PaymentPlan.list/select/change; BillingPreview.compute; DownPayment.status/initiate
- Invoices: Invoice.sendCopy/dispute
- Instruments and mandates: PaymentInstrument.add/remove/list; Mandate.create/sign/amend/cancel/get
- Receipts and allocation: Receipt.list; Allocation.unallocate/reallocate
- Suspense: Suspense.list/suggest/transfer/refundToSender
- Payees and disbursements: PayeeAccount.list/verify; Disbursement.request; DisbursementBatch.prepare/release/approveRelease; Receivable.list (not found in `ReceivablesController.cs`)
- Write-off and transfer: WriteOff.propose/decide/post; Transfer.money
- Agency and commission: IntermediaryCollection.report/reportFile; AccountCurrent.get/list/dispute; Commission.calculations/statements/dispute/runPayments/simulate
- Tax/levy and ledger: TaxLevy.requestLevyPayment; Ledger.query/balance
- Reconciliation: Reconciliation.bank/invariants/fin
- Import and DSAR: Import.reverse/convertCurrency; Dsar.export/restrict/erase

**FIN: 6 of 88 operationIds implemented** (`contracts/openapi/fin.yaml`): Journal.query, Journal.get, PostingRules.list, Balance.trialBalance, IntakeException.list, Period.list. Extra: dev `POST api/fin/v1/dev/periods/{period}/{state}`.

Not implemented (82):
- IFRS 17: Ifrs17Group.assignment/riHeldAssignment/get/list/correctAssignment; Ifrs17.movements; WrittenPremium.query
- Journals and balances: Balance.get; Journal.reverse; ManualJournal.create/list/submit/withdraw/decide/get
- Posting rules: PostingRules.draft/compile/test/previewImpact/submit/decide
- Intake exceptions: IntakeException.rerun/markDuplicate/reject
- Earning: Earning.run/status/simulate/result
- Periods and close: Period.status; Close.tasks/signOff/softClose/lock/reopen/acceptBreak/evidencePack
- Reconciliation: Reconciliation.exchange/detail/breaks/match/run
- Returns: TaxReturn.list/get/recalculate/addAdjustment/generateFile/transition/recordFiling/requestPayment; LevyReturn (same 8); FiscalReconciliation.run/results
- FX, GL, group: Fx.revalue/translate/runs; GroupPack.export; GLExtract.generate/replay/corrective/acknowledge/list
- Actuarial: ActuarialInputs.extract; ActuarialResults.submit/validate/approve/reject/get
- Import and DSAR: Import.convertCurrency/openingBalances/ifrs17Groups/earningBaseline/lic/reverseBatch; Dsar.export/restrict

## CLM, RI, MIG

### Work packages
| Master WP | Title | Built | Evidence | Missing (short) |
|---|---|---|---|---|
| W7-CLM-01 | FNOL, coverage, claim/exposure structure | mostly | `src/CoreIns.Modules.Claims/Api/ClaimsControllers.cs` (fnol submit/validate/get, claims get/patch/close/search, exposures POST, coverage/reverify); `Commands/SubmitFnol.cs`, `CreateExposure.cs`, `ReverifyCoverage.cs`; web `/claims/fnol`, `/claims/:id` | `Fnol.saveDraft`, `Claim.reopen`, `Claim.merge`, `Exposure.update/close/reopen`, `Coverage.decide`; MTPL third-party exposures run with `statutory_clocks = NOT_TRACKED` and Production refuses them (`CreateExposure.cs:84`) |
| W7-CLM-02 | Triage, reserves, transaction sets, authority | mostly | `Api/FinancialsControllers.cs` (transaction-sets build/submit/get/list, financials/get); `Commands/BuildTransactionSet.cs`, `SubmitTransactionSet.cs`, `ApplyApprovalDecision.cs`; `Authority/ClaimsAuthorityTypes.cs`; `/claims/approvals` | Triage and assignment (no WRK task link), `TransactionSet.approve/reject/return` as claim-module routes (approval runs through the platform approvals API), `Financials.dailyTotals` |
| W7-CLM-03 | Payments/payees, recoveries, vendors | mostly | `Api/PaymentsController.cs` (void/stop/reissue), `FinancialsControllers.cs` (payments list, payee-accounts), `Api/RecoveriesController.cs` (6 ops); web `claims/payments`, `claims/recoveries` | Vendors and services (`Service.*`), `VendorInvoice.*`, `Redress.request`; no vendor tables in `Persistence/ClaimsDbContext.cs` |
| W7-CLM-04 | Greek motor ecosystem, diary, documents | partial | `Api/FriendlySettlementController.cs` (8 ops), web `claims/fs`, `claims/fs-statements`, `/claims/fs-statements` | MTPL offer clock and `StatutoryOffer.*` (3), claims-history `Certificate.*` (3), diary, claim documents, `Conversation.*` (3); FS partial collection (`Events/FsNetEventHandlers.cs:144`) |
| W7-CLM-05 | Fraud/SIU, cat, litigation, tracking, streamlining | partial | `Services/Ai/FnolAssist.cs` (POST `fnol/assist`, not in contract); `claims/search` GET and POST | `Fraud.score`/SIU, `CatEvent.aggregate`, litigation (no operationId exists in `clm.yaml`), `ClaimTracking.list/get` |
| W9-CLM-01 | Claims import API | partial | `MigrationImport/ClaimMigrationImporter.cs`, `ClaimMigrationReverser.cs` (in-process `IMigrationImporter`) | `clm.Import.claim/financialHistory/convertCurrency/reverse` have no HTTP route or typed service; opening reserve not posted to the ledger (`ClaimMigrationImporter.cs:50`) |
| W7-RI-01 | Programme/contract registry, participations, operations | partial | `src/CoreIns.Modules.Reinsurance/Api/ContractsController.cs` (create, patch, submit, approve, get, list, applicable), `ProgrammesController.cs` (read only), `Registry/*`; web `/reinsurance`, `/reinsurance/contracts/*` | Programme write side (create, update, renew, submit, approve, return; `RiReadModels.cs:162` says no aggregate yet), `Contract.endorse/expire/commute/close/versionAt/simulate`, `Participation.add/replace`, `SecurityRules`, `Security.report`, `LineTable`; contract type locked to `XOL_PER_RISK` (`ReinsuranceDbContext.cs:67`) |
| W7-RI-02 | XoL recoveries, accounting, settlement | partial | `Recovery/RecoveryBooker.cs`, `Domain/Recovery/XolRecoveryEngine.cs`, `Intake/ClaimIntake.cs`, `Api/RecoveriesController.cs` (list-by-claim, list-by-contract, trace), `Cession/*` + `Api/CessionsController.cs`; web `/reinsurance/claims/:claimId` | `Recovery.recalculate/adjust`, occurrences, cat occurrences, aggregate tracker read, deposits, adjustments, commission, notices, items/balances, bordereaux, statements, cash calls, settlements (manual path D-CON-13), collateral, FAC, cession `preview/rerun`, exception assign/resolve/rerun |
| W8-RI-01 | IFRS 17 / SII data and RI close | none | no `Ifrs17`, `Close`, `Reconciliation`, `Exchange` code in `src/CoreIns.Modules.Reinsurance` | all 12 ops; depends on DAT and FIN |
| W9-RI-01 | Reinsurance import API | partial | `MigrationImport/ReinsuranceMigrationImporter.cs` (object type `TreatyYear` only) and reverser | `ri.Import.programme/contract/cessionHistory/recoveryHistory/aggregateTracker/openItems/reverse` have no routes; importer only opens a tracker aggregate |
| W9-MIG-01 | Strategy, discovery, mapping, load pipeline | partial | `src/CoreIns.Modules.Migration/Commands/BatchCommands.cs` (create, approve, start, pause, resume, reverse), `RedriveAndConformanceCommands.cs`, `XrefAndRoutingCommands.cs`; web `/admin/migration`, `/admin/migration/batches/:id` | Discovery and profiling, DQ rule library and waivers, mapping specs, a legacy simulator source, batches that run in the background (they run inside one request, `BatchCommands.cs:415`), dry-run conformance (`RedriveAndConformanceCommands.cs:98`) |
| W9-MIG-02 | Party, policy, billing/finance conversion | partial | importers in `Policy/MigrationImport/PolicyMigrationImporter.cs`, `Billing/MigrationImport/BillingMigrationImporter.cs`, `Finance/MigrationImport/FinanceMigrationImporter.cs`, `Compliance/MigrationImport/ComplianceMigrationImporter.cs` | No Party importer, no renewal-based conversion orchestration (wave planner, term as unit of mastery, end-state counts D-CON-07), no tax/levy period split, no fiscal credit for legacy MARK |
| W9-MIG-03 | Claims, RI and other conversion; coexistence | partial | `Api/MigrationController.cs` routing resolve/override, `enquiries/get`, `Services/MigrationArchiveReader.cs` (`EnquireAsync`), `RoutingPanel.tsx` | Open-claim tail split and cohorts, claims-history summary conversion, misdirected-payment rerouting; the enquiry returns master marks only (`EnquiryResponse`), no legacy data |
| W9-MIG-04 | Hand-overs, reconciliation, cut-over, decommissioning | partial | archive ops in `MigrationController.cs` (search, get, subject-export, rectification-note, restrict, erase) and `Services/Dsar/*` | Reconciliation and sign-off, rehearsals and cut-over/rollback plan, bureau and myDATA hand-overs, decommissioning; the "archive" is the stored batch item records (`MigrationArchiveReader`), not a legacy archive; rectification note is not persisted (`ArchiveCommands.cs:155`) |

### Contract operations
Count method: HTTP routes in `src/CoreIns.Modules.<M>/Api` (plus `Services/Ai/FnolAssist.cs`) against `operationId`s in `contracts/openapi/<mod>.yaml`. Dev-only and non-contract routes (`fnol/assist`) are not counted.

**CLM: 35 of 73 operationIds implemented.** Not implemented:
- FNOL: `clm.Fnol.saveDraft`
- Claim: `clm.Claim.reopen`, `clm.Claim.merge`
- Exposure: `clm.Exposure.update`, `.close`, `.reopen`
- Coverage: `clm.Coverage.decide`
- Transaction sets: `clm.TransactionSet.approve`, `.reject`, `.return` (decisions go through the platform approvals API today)
- Financials/Redress: `clm.Financials.dailyTotals`, `clm.Redress.request`
- MTPL: `clm.StatutoryOffer.issue`, `.recordAcceptance`, `.recordRepairAgreement`; `clm.Certificate.request`, `.assemble`, `.issue`
- Tracking/cat: `clm.ClaimTracking.get`, `.list`, `clm.CatEvent.aggregate`
- Vendors: `clm.Service.request`, `.update`, `.cancel`; `clm.VendorInvoice.submit`, `.decide`
- Fraud/diary: `clm.Fraud.score`; `clm.Conversation.send`, `.schedule`, `.list`
- Import: `clm.Import.claim`, `.financialHistory`, `.convertCurrency`, `.reverse`
- DSAR: `clm.Dsar.export`, `.rectify`, `.restrict`, `.purge` (an in-process `ClaimsDsarParticipant` exists, no routes)

**RI: 16 of 116 operationIds implemented.** Implemented: `Programme.list/get`, `Contract.create/update/submit/approve/get/list/applicable`, `Participation.list`, `Cession.listByPolicy/get`, `CessionException.list`, `Recovery.listByClaim/listByContract/trace`. Not implemented (100):
- Programme: `create`, `update`, `renew`, `submit`, `approve`, `return`
- Contract: `endorse`, `expire`, `commute`, `close`, `versionAt`, `simulate`
- Participation/security: `Participation.add/replace`, `SecurityRules.set/get`, `Security.report`, `LineTable.set/get`
- Cession: `Cession.preview`, `.rerun`; `CessionException.assign`, `.resolve`, `.rerun`
- FAC (10): `request`, `generateSlip`, `recordQuote`, `importQuotes`, `bind`, `issueCertificate`, `decline`, `cancel`, `get`, `list`
- Recovery: `Recovery.recalculate`, `.adjust`; `Occurrence.link/unlink`; `CatOccurrence.propose/confirm/revise/estimate`; `AggregateTracker.get`
- Premium side: `DepositSchedule.get/reschedule`; `Adjustment.calculate/approve`; `Commission.adjust/approve/history`
- Notices: `Notice.draft/send/acknowledge`
- Accounting: `Item.list`, `Balance.report`, `Bordereau.generate/submit/approve/issue`, `Statement.generate/submit/approve/issue/recordResponse/reissue`
- Cash: `CashCall.raise/issue/recordReceipt/cancel`, `Settlement.propose/approve/release/recordManual`, `Collateral.create/list/draw/release`
- Accumulation (7): `zone`, `query`, `footprint`, `estimate`, `setCapacity`, `exportOed`, `importModelResults`
- Finance/data: `Ifrs17Link.query`, `DataProduct.get`, `Close.run/status`, `Reconciliation.fin`, `Exchange.send/receive`
- Import: `Import.programme/contract/cessionHistory/recoveryHistory/aggregateTracker/openItems/reverse`
- DSAR: `Dsar.export`, `Dsar.restrict` (participant exists, no routes)

**MIG: 22 of 22 operationIds implemented.** The contract is complete, but its scope is the thin batch/xref/routing/archive surface. PRD-16 areas with no operationId at all (discovery, profiling, mapping, reconciliation, cut-over, hand-overs) are listed as contract additions in section C.

## DOC, CMP, WRK

### Work packages
| Master WP | Title | Built | Evidence | Missing (short) |
|---|---|---|---|---|
| W2-DOC-01 | Doc-type catalogue, template/clause authoring, binding matrix | partial | `src/Documents/Domain/Templates.cs` (4 hard-coded letter templates: DT-NONPAY-NOTICE, DT-WITHDRAWAL-ACK, DT-CANCELLATION-NOTICE, DT-REFUND-ADVICE); `POST /api/doc/v1/documents/request` | No catalogue/template/clause tables or authoring, no binding matrix, no `document-types`/`templates`/`clauses`/`bindings` endpoints, no request-batch |
| W2-DOC-02 | Governance/translation, form patterns, accessibility, preview | none | templates carry el+en text constants only | Tracked changes, translation workflow, forms.*, form-patterns, preview, accessible output |
| W2-DOC-03 | Payload dictionary, Greek typography, rendering engine | partial | `DocumentCommands.cs` `RequestDocumentHandler` renders constant HTML; `DocumentRow.ContentHash/RenderedAt` | No payload dictionary, no template->HTML composer, no Gotenberg, no PDF/A-3 post-processing (no "pdf" anywhere in the module) |
| W2-DOC-04 | Archive, delivery orchestration, operations | partial | `archives/store`, `archives/{id}`, `deliveries/status`, `deliveries/evidence-for`, `evidence-packs/{id}`, `documents/reprint`, `provider-events/{provider}`; `StatutoryDeliveryRule` | Stub post/email providers only; no verify/addRendition/retention class/listForObject archive ops, no resend, hide/unhide, supersede, withdraw, deleteDraft, evidence-pack export, import; no retention/immutability; no subject export/restrict/erase HTTP (DSAR participant is in-process: `Services/Dsar/DocumentsDsarParticipant.cs`) |
| W4-DOC-01 | Fiscal coupling, pre-contractual pack, e-signature, Documents tab | none | only POL/CHN/BIL call `IDocumentRequestService` for letters | IPID, proof of cover, cover note + fiscal coupling, signatures.* (5 ops), Documents tab on policy/claim/party, `/documents` UI (still `ModulePlaceholder`, `web/src/app-shell/navigation.ts:74`) |
| W6-DOC-01 | Batch rendering, reprint/as-of, evidence packs | partial | `documents/reprint` (reason required), `evidence-packs/{id}` | request-batch, renderAsOf, evidence-pack export, proof packs; reprint four-eyes (TODO `DocumentCommands.cs:361`) |
| W2-WRK-01 | Activity patterns, lifecycle, system-generated activities | mostly | `src/Work/Api/ActivityController.cs` (9 routes), `Domain/WorkPatterns.cs`, `Events/PackRolledBackReviewHandler.cs`; `Services/WorkActivityService.cs` used by POL/BIL (PSR tasks, NX-HD-11) | Patterns are 4 code constants (TODO WorkPatterns.cs:29); `PATCH activities/{id}`, pattern overrides ignored (`ActivityCommands.cs:63`), linking/visibility rules, only PSR/pack-rollback raisers |
| W2-WRK-02 | Calendars/SLA, groups/queues, assignment/routing, delegation | partial | `Jobs/SlaSweeper.cs` (target/breach, `SLABreached` once), 2 queues, `queues/get-next`, `activities/assign` | Wall-clock minutes, no business calendars, no priority score (WRK-091), no assignment rules/simulate (TODO `ActivityCommands.cs:138`), no user validation (`ActivityEngine.cs:176`), no `ActivityAssigned` event (`:344`), no groups admin, absence/delegation, `bulk-reassign` |
| W2-WRK-03 | Team views, authority-aware routing, back-office requests, participants | partial | UI `web/src/modules/work/WorkPage.tsx` scope `team`, `TaskDetailPage.tsx` | `teams/summary` (UI counts client-side: `work/api.ts:93`), authority-aware routing, requests.* (7), participants.* (4), diary |
| W2-WRK-04 | Notes and inbound documents | none | none | notes.* (8), inbound-documents.* (11), ClamAV/Defender scan |
| W2-WRK-05 | My Desktop/global search, staff notifications, data protection, admin | partial | `web/src/modules/work/notifications.tsx` feeds `NotificationCenter`; `WorkDsarParticipant.cs` | desktop.get, search.global, palette.query, recent-items, command-registries, exports, `data-subject.*` HTTP, imports (5); DSAR participant does not scrub notes (TODO `WorkDsarParticipant.cs:47`) |
| W2-WRK-06 | OCR (deferred) | deferred | D-USR-03 | not built by decision |
| W2-CMP-01 | Statutory clock register and engine | mostly | `src/Compliance/Api/ClocksController.cs` (start/pause/resume/stop/cancel/extend/get/query, defs list+get), `Clocks/ClockSweeper.cs` | `clock-defs/propose`, `clocks/import` HTTP; only 2 clock defs coded (CMP_COMPLAINT_REPLY, CMP_DSAR_RESPONSE); CMP_COMPLAINT_ACK fails closed (`ClockDomain.cs:104`); no clock admin/detail UI |
| W5-CMP-01 | Fiscal-document channel (myDATA), e-invoicing | partial | `Api/FiscalDocumentsController.cs` (`request`, `{id}`), `Commands/RequestFiscalDocument.cs`, stub channel (`Domain/FiscalDocuments.cs`), credit-doc tests | list-by-source, qr-payload, resubmit, bulk-resubmit, correct, cancel, reconciliation run/breaks, fiscal-rules certify, import; real myDATA channel (OQ-012 Unverified); UI = open NX-UI-11 |
| W5-CMP-02 | Information Centre / motor bureau adapter | none | `MigrationImport/ComplianceMigrationImporter.cs:85` only touches plates | bureaus.status/retransmit/reconcile/handover, bureau-manual-files.generate/recordResponse |
| W8-CMP-01 | Complaints, periodic returns, submission tracker | partial | `Complaints/ComplaintsController.cs` (12 routes), `ComplaintEngine.cs` (state machine, 50-day clock, SoD reply approval); UI `modules/compliance/home/ComplianceHomePage.tsx` lists complaints | complaint UI beyond a list (no detail/actions); complaints/import; annual-returns (6), enfia (4), submissions (9), taxonomies (3), XBRL; hardening TODOs in ComplaintEngine (:149, :302, :403) |
| W8-CMP-02 | DSAR orchestration and disclosure log | mostly | `Dsar/DsarController.cs` (13 + disclosure 2), `DsarEngine.cs`; UI `modules/compliance/dsar/*`; WRK participant now DONE | `dsar/import`; retention durations empty (D-REG-07); erase edge cases = open NX-HD-09/NX-TST-08 |
| W8-CMP-03 | Obligations register, regulatory change, AI register, workbench | partial | `AiRegister/AiRegisterController.cs` (7 ops incl. list) | Obligations register and regulatory-change backlog have NO operations in `cmp.yaml`; compliance workbench; AI register UI = NX-UI-11 |
| W9-CMP-01 | Compliance migration import and hand-over | partial | `MigrationImport/ComplianceMigrationImporter.cs` (in-process, used by MIG) | `*/import` HTTP ops, `bureaus/handover`, `ClockService.cs:55` handOverDates TODO |

### Contract operations
**DOC: 10 of 44 operationIds implemented.** Done: Document.request, .get, .listForObject, .reprint; Delivery.status, .evidenceFor; EvidencePack.get; Archive.store, .get; ProviderEvent.receive (stub providers, refused in Production).
Not implemented:
- Document lifecycle: requestBatch, preview, resend, renderAsOf, hide, unhide, deleteDraft, supersede, withdraw, import.
- Catalogue: DocumentType.list, Template.list, Clause.get, Binding.resolve.
- Forms: Forms.infer, addManual, removeManual, get; FormPattern.validateReferences.
- Cover/IPID: ProofOfCover.issue, Ipid.get.
- Signatures: createEnvelope, send, remind, void, get.
- Archive: verify, addRendition, setRetentionClass, listForObject; EvidencePack.export.
- Subject: export, restrict, erase (HTTP; in-process DSAR participant exists).

**CMP: 46 of 90 operationIds implemented.** Done: Clock start/pause/resume/stop/cancel/extend/get/query; ClockDef list/get; Complaint create/classify/link/acknowledge/addAction/submitReply/approveReply/escalate/close/get/query/procedureText; Dsar 13 ops; DisclosureLog record/query; AiSystem 7 ops; FiscalDocument request/get.
Not implemented:
- Fiscal: FiscalDocument listBySource, qrPayload, resubmit, bulkResubmit, correct, cancel, import; FiscalReconciliation run, breaks; FiscalRule.certify.
- Bureau: Bureau status/retransmit/reconcile/handover; BureauManualFile generate/recordResponse.
- Clocks: ClockDef.propose; Clock.import.
- Imports: Complaint.import, Dsar.import.
- Returns: AnnualReturn generate/validate/approve/submit/recordReceipt/amend; EnfiaConfirmation import/propose/decide/transmit.
- Submissions: Submission create/attachContent/comment/approve/recordFiling/recordResponse/evidencePack/renderXbrl/validateTaxonomy; Taxonomy import/activate/list.
- Evidence: Evidence.submit; Traceability.export.

**WRK: 9 of 57 operationIds implemented.** Done: Activity create/list/get/assign/complete/skip/cancel/blockingStatus; Queue.getNext.
Not implemented:
- Activity: update (PATCH), bulkReassign; Assignment.simulate; Team.summary; Export.create.
- Requests: submit/get/list/reply/requestInfo/withdraw/close/toComplaint (8).
- Notes: create/list/get/search/edit/delete/history/export (8).
- Inbound documents: submit/get/list/verify/split/link/relink/reject/redact/reprocess/export (11).
- Participants: assignByRules/list/objectsFor/change.
- Desktop/search: Desktop.get, Search.global, Palette.query, RecentItem.list, CommandRegistry.register.
- DataSubject: export/erase/restrict (HTTP).
- Import: activities/notes/groups/participants/inboundMetadata.

## DAT, PLT, MKT

### Work packages
| Master WP | Title | Built | Evidence | Missing (short) |
|---|---|---|---|---|
| W1-PLT-01 | Identity: staff and external realms | partial | `src/CoreIns.Host/Hosting/SecurityExtensions.cs` (Entra JWT + dev sign-in, `Platform.Admin` app role); `web/src/auth/roles.ts` | No user_profile, no `plt.User.*`, `ExternalUser.*`, `Token.exchange/grantAgentScope`, role-assignment grant/revoke, privileged access, UserProfile prefs. Roles come only from the token. |
| W1-PLT-02 | Authorisation, authority, maker-checker | mostly | `Platform/Authority/Authority.cs` (`IAuthorityService.CheckAsync`, config-driven grants); `Platform/Approvals/ApprovalsController.cs` (`decide`, `get`, `list`); `Platform/Authorization/Permissions.cs` | HTTP for `Authority.check/attachDecision/whoCanApprove`, `AuthorityType.register`, `Approval.request/withdraw/verifyForExecution`, `ApprovalType.register`, `Policy.evaluate`; authority grants are config, not data. |
| W1-PLT-03 | Audit, event infrastructure, integration hub | mostly | `Platform/Audit/AuditStore.cs` (hash chain, `VerifyAsync`); `Platform/Events/{OutboxProcessor,OutboxReplayService}.cs`; `Platform/DeadLetters/DeadLetterApi.cs` (list/get/replay) | No `Audit.query/export/verify` endpoints; no `DeadLetter.discard`, `Outbox.publish`, `Consumer.replay`, `Schema.register/check`, `Adapter.invoke`, `Exchange.search`, `Transfer.list/retry`; no integration-hub adapter wrappers. |
| W1-PLT-04 | Decision tables, config runtime, numbering | partial | `Platform/Numbering/Numbering.cs` (`NextAsync`); `Platform/Configuration/ConfigurationResolver.cs`; rules in `src/CoreIns.Rules` | No HTTP for `Number.next/reserve/void`, `DecisionTable.*`, `Expression.*`, `Workflow.*`, `Job.*`; feature flags are a single global boolean (`Platform/Ai/AiModule.cs:82` TODO). |
| W1-PLT-05 | Reference data and data-protection services | partial | `Platform/Dsar/PlatformDsarParticipant.cs`; `Platform.DataProtection`; `Platform/Time/DevClock.cs` | No calendars/holidays/FX/currency (`Calendar.*`, `Holiday.addException`, `Fx.*`, `Currency.get`), no retention catalogue/runs, no legal holds, no `Erasure/Subject/Ror.export` at PLT level. |
| W1-PLT-06 | Observability, environments/release, stamps/DR | partial | `src/CoreIns.Host/Hosting/ObservabilityExtensions.cs`; `Platform/Telemetry/CoreInsMetrics.cs`; `src/CoreIns.Host/Health/` | Covered by open NX-OT-04/06/07; `Pipeline.e2eGate`, release/environment control not built. |
| W1-PLT-07 | Security engineering, fitness rules, admin shell | partial | `tests/CoreIns.ArchitectureTests`; `web/src/app-shell/navigation.ts` (`adminNavItem` -> `/admin`) | `/admin` itself is `ModulePlaceholder` (heading resolves to «Home»); no admin tools: audit viewer, approvals inbox, dead letters, users. |
| W1-MKT-01 | Config model, capability switches, tenancy | partial | `src/Market/Domain/ConfigurationCatalogue.cs`, `Services/ConfigurationEngine.cs`, `Api/ConfigurationController.cs` (`resolve`, `current-hash`); `Queries/LegalEntityRegistry.cs`; `Services/CapabilityService.cs` | No `Configuration.explain/compare/export`; no `Capability.get` endpoint; capability state hard-coded (`Services/CapabilityCatalogue.cs:15` TODO). |
| W1-MKT-02 | SPI framework and catalogue | partial | `Services/TaxCalculatorService.cs`, `src/CoreIns.CountryPacks.GR/{Fiscal,Identifiers,Transliteration}`, `Host/Hosting/CountryPackBinding.cs` | No `Spi.catalogue/bind/suspend` (binding is by host config). |
| W1-MKT-03 | Pack packaging, lifecycle, reference packs | mostly | `Api/PacksController.cs` (`rollback`, `schedule-activation`, `pack-activations` get/list/decide); `Services/PackRegistryService.cs` (list/get); `Commands/PackActivation.cs`; `web/src/modules/market/packs/*`; `src/CoreIns.CountryPacks.{GR,CY}` | `Pack.certify`, `publish`, `deprecate`; GR/CY packs are code, not uploaded versions. |
| W1-MKT-04 | i18n/l10n, currency and rounding, regime codes | partial | `Api/ConfigurationController.cs` `rounding/apply`; `Domain/Rounding.cs`; web i18n JSON bundles in `web/src/*/i18n` | No `L10n.*`, `Terminology.*`, `Translation.gate`, `Currency.rule/convertFixed`, `Changeover.configuration`, `RegimeCode.*`. |
| W1-MKT-05 | Governance, golden suite, operations UI | partial | Maker-checker only for pack activation (`Commands/PackActivation.cs`); golden tests in `tests/CoreIns.CountryPacks.Tests` | No `ConfigChangeRequest.*` (10 ops), no config governance UI. Only `/admin/packs` exists. |
| W1-MKT-06 | Statutory clock values, integration additions | partial | Clock values live in CMP (`src/Compliance/Clocks/ClockDomain.cs`) and pack data | No `StatutoryClockSet.get/list`, no Unverified fail-closed read model in MKT (check EC-12). |
| W8-PLT-01 | AI control plane and DORA operations | mostly | `Platform/Ai/{AiModule,AiCommands,AiControlPlane,AiGate}.cs` (`ai-toggle/*`, `ai/kill-switch`, `ai/invoke`, `ai/record-outcome`); `web/src/modules/platform/ai/AiControlPage.tsx` | DORA incident operations absent (not in plt.yaml ops; lives in CMP); per-feature input allow-list (`AiCommands.cs:98`). |
| W8-MKT-01 | Cross-border business | none | none | `RiskLocation.resolve`, `CrossBorder.check/basis`. |
| W8-DAT-01 | Event ingestion, data contracts, control totals, ref data | none | Schema names only (`DataModule.cs` consts `rpt_raw/rpt_conformed/rpt_mart`, no tables) | Everything: contracts registry, ingestion handler, quarantine, replay, control totals. |
| W8-DAT-02 | Report-mart layers, bitemporal, DQ/lineage, catalogue | none | none (`DataModule.cs:22` says "No lakehouse, marts, accumulation or analytics") | Conformed layer, `Query.asOf`, `Population.*`, `Kpi.get`, `Mart.*` read side, `Lineage.trace`, `Coverage.status`, `Accumulation.*`. |
| W8-DAT-03 | Regulatory mapping, SII, national, group marts | none | none | `Mart.run/adjust/signOff/export`, `ScrResults.*`. Layouts Unverified (D-REG). |
| W8-DAT-04 | Actuarial supply, analytics, BI | none | none | `Actuarial.inputSet/run/supersede`, `ActuarialResultSet.submit`, `Analytics.ask`. |
| W8-DAT-05 | Feature store, model registry, AI monitoring | partial | `src/Data/Commands/ModelCommands.cs` (`models/register`, `models/{id}`, `bias-tests/run` -> `BiasThresholdBreachedV1`, suspend on breach); tables `model`, `bias_report` | `Model.submitValidation/recordValidation/approve/retire`, `Monitor.metrics` (drift), `FeatureStore.*`; bias CI is a point estimate (`ModelCommands.cs:128`). |
| W8-DAT-06 | Personal-data governance, synthetic data, mart ops | partial | `src/Data/Commands/SubjectCommands.cs` (`subject/export`, `erasure/execute`, `restriction/apply`), crypto-shredding via `Domain/SubjectKeyProtector.cs`; dev `dev/subject-facts` | `PurposeGrant.request/decide/revoke`, `Synthetic.generate`; operates on `subject_fact` rows only, not on real marts (none exist). |
| W9-PLT-01 | Migration import operations | none | none | `plt.Import.users/roleAssignments/authorityGrants/identityInvitations`. |
| W9-MKT-01 | Migration-baseline configuration state | none | `config_state` table exists (`Persistence/MarketDbContext.cs:76`) | `ConfigChangeRequest.import` + H0 state. |
| W9-DAT-01 | DAT migration and coexistence | none | none | DAT-294..298. |

### Contract operations
**DAT: 6 of 46 operationIds implemented** (`Subject.export`, `Erasure.execute`, `Restriction.apply`, `BiasTest.run`, `Model.register`, `Model.get`; plus a dev-only `dev/subject-facts` that is not in the contract).
NOT implemented, by area:
- Data contracts and ingestion: `DataContract.register/get/list`, `Ingestion.replay`, `Quarantine.release`
- Query and analytics: `Query.asOf`, `Population.sample/aggregate`, `Kpi.get`, `Analytics.ask`, `Lineage.trace`, `Coverage.status`
- Marts: `Mart.run/get/figure/compare/adjust/signOff/export`, `ScrResults.submit/accept/get`
- Accumulation: `Accumulation.snapshot/footprint/trend`
- Models and monitoring: `Model.submitValidation/recordValidation/approve/retire`, `Monitor.metrics`, `FeatureStore.getOnline/buildTrainingSet`
- Actuarial: `Actuarial.inputSet/run/supersede`, `ActuarialResultSet.submit`
- Privacy and synthetic: `PurposeGrant.request/decide/revoke`, `Synthetic.generate`

**PLT: 12 of 101 implemented** (`AiToggle.resolve/set`, `Ai.killSwitch/invoke/recordOutcome`, `FeatureFlag.evaluate`, `Approval.decide/get/list`, `DeadLetter.list/get/replay`). Audit, authority, numbering and the DSAR participant exist as in-process services with no HTTP.
NOT implemented, by area:
- Identity and access: `Token.exchange/grantAgentScope`, `User.create/update/get/suspend/reactivate/disable/search`, `ExternalUser.register/link/recover/invite`, `RoleAssignment.grant/revoke`, `Policy.evaluate`, `PrivilegedAccess.request/approve/revoke`, `UserProfile.getPreferences/setLanguage`
- Authority and approvals: `AuthorityType.register`, `Authority.check/attachDecision/whoCanApprove`, `ApprovalType.register`, `Approval.request/withdraw/verifyForExecution`
- Audit, events, integration: `Audit.append/query/export/verify`, `Outbox.publish`, `Schema.register/check`, `Consumer.replay`, `DeadLetter.discard`, `Adapter.invoke`, `Exchange.search`, `Transfer.list/retry`, `Lineage.walk`, `Pipeline.e2eGate`
- Workflow and runtime: `Workflow.start/signal/query/cancel/terminate`, `Job.run/stop/schedule/history`, `DecisionTable.evaluate/test/activate/explain`, `Expression.check/evaluate`
- Reference data: `Calendar.*` (8), `Holiday.addException`, `Fx.getRate/listRates/correct`, `Currency.get`, `Number.next/reserve/void`, `Time.now`
- Retention and privacy: `Retention.registerDataset/schedule/runs`, `LegalHold.apply/release/check`, `Erasure.execute`, `Subject.export`, `Ror.export`
- Migration: `Import.users/roleAssignments/authorityGrants/identityInvitations`

**MKT: 10 of 51 implemented** (`Configuration.resolve/currentHash`, `Rounding.apply`, `Pack.list/get/rollback/scheduleActivation`, `PackActivation.get/list/decide`).
NOT implemented, by area:
- Configuration: `Configuration.explain/compare/export`, `ConfigChangeRequest.create/update/submit/approve/reject/schedule/withdraw/rebase/preview/import`
- SPI and capability: `Spi.catalogue/bind/suspend`, `Capability.get`
- Pack lifecycle: `Pack.certify/publish/deprecate`
- Localisation: `L10n.bundle/format/searchKeys/caseMap`, `L10nUiLanguage.resolve`, `Translation.gate`, `Terminology.list/propose/approve`
- Currency and codes: `Currency.rule/convertFixed`, `Changeover.configuration`, `RegimeCode.list/get/validate/crosswalk`
- Cross-border and clocks: `RiskLocation.resolve`, `CrossBorder.check/basis`, `StatutoryClockSet.get/list`

