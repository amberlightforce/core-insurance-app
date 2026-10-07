# Digest — PRD-12 Channels: Portals, Partner APIs and AI Agent Facade (CHN)

Source: `core-insurance-prds/PRD-12-channels-portals-partner-apis.md` (2,567 lines, read in full). Version 1.4, 2026-10-07, "Draft — freeze candidate for build baseline 1.0"; binding input 00-system-contract.md v1.11 (§1.1). Section numbers below refer to the PRD.

---

## 1. Identity

| Item | Value |
|---|---|
| Module code | **CHN** |
| Title | Channels: Portals, Partner APIs and AI Agent Facade |
| Owner roles | Channel product owner (ROLE-14 specialisation, accountable); lead architect (channel back-end and external API contract); API product manager (ROLE-14 specialisation); co-reviewers ROLE-09, ROLE-29; DPO ROLE-30; security officer ROLE-39 (§1.1) |
| ID scheme | Contract anchors REQ-CHN-001…009; module requirements from REQ-CHN-030; BR-/NFR-/SCR-/AI-/OI-/CCR-CHN-; capabilities CAP-CHN-00…21; journeys J-01…J-10; local risks RK-CHN-nn (§1.1, §16.3) |

**Purpose (§1.2).** CHN is "every door into the core system for people and machines outside the insurer": customer portal and mobile app; broker and agent portal; bank-staff journeys and embedded bancassurance APIs; partner and comparison-site APIs; an MCP facade through which AI agents act for people. **CHN holds no business logic**: it composes owner modules' commands/queries, evaluates one versioned **transaction-permission matrix** before every command (outcomes `SELF_SERVICE`, `QUOTE_ONLY`, `REFER`, `FORBIDDEN` with limits), presents results in Greek and English to WCAG 2.2 AA with an always-visible "ΕΛ | EN" switch in every external shell (R-101), and governs who may call what, how often and with which evidence. It also owns the statutory **online withdrawal function** (Law 5317/2026 Art. 69 → Law 2251/1994 Art. 3ζα).

**Five key decisions (§1.2):** (1) one channel back-end (a backend-for-frontend per front-end family), many thin front ends, all forms describe-driven from `pfc.Product.describe` (REQ-PFC-222) and MKT bundles (REQ-MKT-189); (2) permission matrix as versioned, maker-checker, effective-dated data; (3) AI agents get the same doors, never more — delegated tokens, dry-run, confirmation tickets approved by a named human; (4) withdrawal function built from the statute; (5) partner APIs as a product (ACORD NGDS-aligned, idempotency, dry-run, RFC 9457, cursor pagination, 6-month deprecation, signed webhooks, sender-constrained credentials, quotas, sandbox, developer portal).

**Non-goals / out of scope (§1.4):** rating, UW rules, policy transactions, billing, claims handling, document generation (RAT/UW/POL/BIL/CLM/DOC); staff workbenches; identity infrastructure (user stores, authenticators, tokens, federation, token exchange — PLT REQ-PLT-015, -050…-074); consent and communication-preference system of record (PTY REQ-PTY-005, -150); intermediary/agency-user/bancassurance master data (PTY REQ-PTY-243…-258); back-office request handling, staff notifications, inbound document intake (WRK); AI control plane / model gateway (PLT REQ-PLT-010); complaint and DSAR handling (CMP — CHN only offers entry points); edge web-application/API protection (PLT REQ-PLT-293); marketing-site CMS (outside core; CHN provides embeddable quote entry only).

**Phase scope (§1.5):** P1 motor MVP; P2 home; P3 commercial; P4 later markets. All P1 policy terms are **annual** only (D6; §1.5 closing paragraph).

---

## 2. Size metrics

### 2.1 Requirement counts (verified by re-counting the tables; matches §5.24 and §16.6)

| | Must | Should | Could | Won't | Total |
|---|---|---|---|---|---|
| [BASELINE] | 209 | 46 | 3 | 0 | 258 |
| [ENHANCEMENT] | 8 | 19 | 3 | 0 | 30 |
| **Total** | **217** | **65** | **6** | 0 | **288** |

By phase (my recount):

| Phase | Must | Should | Could |
|---|---|---|---|
| P1 | **215** | 62 | 4 (REQ-CHN-066, -128, -174, -211) |
| P2 | 2 (REQ-CHN-299 home quote, -300 home claims) | 2 (REQ-CHN-067 trusted contact, -213 bank book after partnership end) | 0 |
| P3 | 0 | 1 (REQ-CHN-301 commercial broker submission) | 2 (REQ-CHN-073, -302) |

**Motor MVP cut = 215 Must P1 requirements** (§5.24: "the motor MVP cut is the 215 Must P1 requirements"). Note that 7 of these are R-101 language-switch requirements (REQ-CHN-319…325) and ~23 are MCP facade / AI requirements (see §12 below) — both large even though the AI features ship off by default.

### 2.2 Other counts

| Item | Count | Source |
|---|---|---|
| Business rules | 54 (BR-CHN-001…066 with gaps: 001–026, 030–033, 040–048, 050–057, 060–066) | §10.1.1, §16.6 |
| NFRs | 19 (NFR-CHN-001…019) | §14 |
| Screens | 32 (SCR-CHN-01…32) | §6, §16.6 |
| Owned entities | ~20 entity types in 12 groups (§7.1.1–7.1.12) + 5 read models (§7.1.13) | §7 |
| State machines | 5 (PermissionMatrixVersion, WithdrawalRequest, ApiClient, WebhookSubscription, ConfirmationTicket) | §7.3 |
| Events produced | 9 (8 per CCR-CHN-01 + `DisclosureReceiptRecorded`) | §8.1 |
| Events consumed | ~70 distinct event names from POL, UW, BIL, CLM, DOC, WRK, PTY, PFC, RAT, PLT, CMP, MKT, MIG | §8.2 |
| Partner webhook types | 21 | §8.3 |
| Internal operations (`chn.*`) | 27 rows / ~60 operations | §9.1.1 |
| External API products (P1) | 9 | §9.1.2 |
| External REST operations mapped | 21 rows | §9.1.3 |
| MCP tools (P1) | 18 | §9.1.4 |
| AI features | 5 (AI-CHN-01…05) | §11 |
| Open issues | 15 (OI-CHN-01…15; 06, 10, 12 closed; 14 partly closed) | §16.1 |
| CCRs | 8 (CCR-CHN-01…08, all Accepted) | §16.4 |
| Journeys | 10 (J-01…J-10) | §4 |
| Owned UI-library inventory items | 11 (30 aspect rows), all specified; 1 aspect replaced | §6.33, §15.3 |

### 2.3 Build-size estimate: **L (very large)**

215 Must P1 requirements (more than double the L threshold) and 32 screens across four distinct front-end families (customer web + mobile app, broker/agent portal, developer portal, staff config/governance), plus a public partner API product with sandbox, webhooks and onboarding, an MCP server with OAuth 2.1 resource-server semantics, and a statutory withdrawal function with RPO-0 durability. Money/temporal logic is mostly delegated to owners, but CHN depends on almost every other module being available first.

---

## 3. Owned entities (§7)

Storage: PostgreSQL schema `chn`; owner data only as identifiers or read models named `<Entity>View`. Every row carries `legal_entity_id`, `jurisdiction`, `created_at`, `created_by`, `record_version`; effective-dated rows `valid_from`/`valid_to`. Internal ids UUIDv7; business numbers (withdrawal request number, ticket number) from `plt.Number.next` (REQ-PLT-014). Retention codes `RC-CHN-*` (§7.0).

| Entity (§) | Key attributes | Retention |
|---|---|---|
| **PermissionMatrixVersion** (7.1.1) | matrix_version_id, version_no (unique per entity), status, effective_from (≥ approval), approval_ref (REQ-PLT-004), scenario_run_ref | `RC-CHN-CONFIG` (entity life + 10 y) |
| **PermissionMatrixEntry** (7.1.1) | entry_id, transaction_type (catalogue), channel (11-code list), role (ROLE-xx/SYS-xx), optional product_line / jurisdiction / producer_group, outcome, typed `limits` JSON (max_backdating_days, max_future_days, max_premium_increase {amount,currency}, max_premium_decrease, max_sum_insured, max_vehicles, allowed_payment_methods[]), step_up_required, agent_without_ticket (only for effect class NONE), refer_request_type (WRK type), final_layer NONE/EU/COUNTRY | as above |
| **PermissionDecision** (7.1.2) | decision_id, matrix_version_id, entry_id, transaction_type, channel, role, actor_id, on_behalf_of_id, object_ref, evaluated_values (from owner dry-run), outcome, reasons[], correlation_id | `RC-CHN-INTERACTION` (10 y, DPO to confirm) |
| **ChannelSession** (7.1.3) | session_id (pseudonymous), channel, device_class, ui_language (`el`/`en`), region_format, language_source (EXPLICIT/PROFILE/DEVICE/BROWSER/ENTITY_DEFAULT), identity_subject, analytics_consent_state, draft_refs (no business data), timestamps | `RC-CHN-SESSION` (90 d after end) |
| **ChannelInteraction** (7.1.4) | interaction_id, session_id, type enum (JOURNEY_STARTED, STEP_COMPLETED, QUOTE_SHOWN, DOCUMENT_DISPLAYED, DOCUMENT_OPENED, WARNING_ACKNOWLEDGED, CONSENT_GIVEN, PAYMENT_STARTED, COMMAND_SUBMITTED, REFERRAL_CREATED, ABANDONED, LANGUAGE_SWITCHED), actor/on-behalf-of/channel, object_refs, document_ref+version, occurred_at (PLT time), correlation_id (technical only, D5), **business_keys** JSON (quote/job/transaction/invoice/claim ids — lineage, D5), language, language_from | `RC-CHN-INTERACTION`; analytics-only types 25 months |
| **ConsentReceipt** (7.1.5) | receipt_id, party_id (nullable), purpose, channel_scope, choice GIVEN/REFUSED/WITHDRAWN, wording_version, pty_consent_ref (PTY is SoR), captured_at, interaction_id | `RC-CHN-CONSENT` (consent life + 5 y) |
| **WalletConsentReceipt** (7.1.6) | wallet_receipt_id, service_request_id, legal_basis_ref (e.g. 25061 ΕΞ 2024), identifiers_used (AFM, plate — encrypted, P2), data_categories_received (values not stored), step1_at, step2_at, outcome APPROVED/REFUSED/TIMED_OUT/FAILED, job_ref, channel | `RC-CHN-WALLET` (5 y) |
| **WithdrawalRequest** (7.1.7) | id, request_number, declarant_name, contract_identification, electronic_contact, reason_text, matched_policy_id/term_no, channel, signed_in, identity_subject, submitter_id (broker per REQ-CHN-174), **confirmed_at (PLT time, immutable)**, status, ack_document_ref, pol_job_ref, refund_status_ref, duplicate_of | `RC-CHN-WITHDRAWAL` (contract term + statutory limitation; default 10 y) |
| **PartnerOrganisation / ApiClient / ApiProduct / OnboardingCase** (7.1.8) | organisation_id, party_id (PTY organisation), partner_type INTERMEDIARY/AGGREGATOR/BANK/VENDOR/AGENT_PLATFORM/OTHER, ict_provider_flag + plt_third_party_ref (DORA), client_id (PLT), environment SANDBOX/PRODUCTION, api_products[], scopes[], producer_codes[], rate_limit, quota, status; ApiProduct name/version/lifecycle (Draft, Published, Deprecated, Retired)/deprecation_at/sunset_at; onboarding checklist, certification_result, approvals[] (four-eyes); contacts | `RC-CHN-PARTNER` (relationship end + 10 y) |
| **WebhookSubscription / WebhookDelivery** (7.1.9) | subscription_id, client_id, endpoint_url (HTTPS), event_types[], filters, signing_method, key_ref (secrets in key-management service), status; delivery_id, source_event_id, sequence, attempt_count, last_status, next_attempt_at, delivered_at | Subscriptions `RC-CHN-PARTNER`; deliveries 30 days then metrics only |
| **NotificationSubscription / CustomerNotification** (7.1.10; renamed from Notification per CON-029; WRK owns StaffNotification) | identity_subject, event_class, channels[]; notification_id, recipient, class ACTIONABLE/IMPORTANT/INFORMATIONAL, template_key, object_ref, created_at, read_at, delivered_channels[]; no P2/P3 in text | `RC-CHN-NOTIFY` (13 months) |
| **AgentClient / ConfirmationTicket / AiAgentActionLog** (7.1.11) | agent_client_id, organisation_id, declared_software, allowed_tools[], allowed_realms[], limits, status; ticket_id/number, tool, tool_version, transaction_type, delegating_subject, agent_actor, dry_run_ref + dry_run_hash (SHA-256), status, expires_at, approved_by/at, step_up_ref, executed_command_ref; log: declared_model_version, context_hash, input/output hash + pointer, owner_ops[], matrix_decision_id, declared_confidence, human_approver/decision, plt_ai_interaction_id, timestamps, correlation_id | `RC-CHN-AGENT-LOG` (10 y state-changing, 2 y read-only) |
| **DeepLink / AggregatorQuote / DryRunResultCache** (7.1.12) | deep_link_id, partner_client_id, carried_payload_ref (encrypted), signature, expires_at, used_at, job_ref; cache: rating_key_hash, result_ref, product/rating artefact hashes, configuration_hash, expires_at | `RC-CHN-TRANSIENT` (expiry + 7 d) |

**Read models (§7.1.13):** `PolicySummaryView` (POL events), `BillingSummaryView` (BIL), `ClaimSummaryView` (CLM), `DocumentNoticeView` (DOC), `ProducerScopeView` (PTY/PLT).

**ER (§7.4):** MatrixVersion 1–n Entry 1–n Decision; Session 1–n Interaction; Interaction 1–n ConsentReceipt, 0–1 WalletConsentReceipt; Session 1–n WithdrawalRequest; PartnerOrganisation 1–n ApiClient, 1–n OnboardingCase; ApiProduct n–n ApiClient; ApiClient 1–n WebhookSubscription 1–n WebhookDelivery; ApiClient 1–n DeepLink; AgentClient 1–n AiAgentActionLog 0–1 ConfirmationTicket; Decision 1–n AiAgentActionLog; NotificationSubscription 1–n CustomerNotification.

### 3.1 State machines (§7.3)

**PermissionMatrixVersion (7.3.1):** Draft → Submitted (guard: all scenarios pass, no change to final entries) → Approved (maker ≠ checker) | Draft (returned) | Rejected; Approved → Scheduled (effective_from future) | Active (effective_from ≤ now); Scheduled → Active | Approved (schedule withdrawn); Active → Superseded (next activates). Event `PermissionMatrixActivated` on Active.

**WithdrawalRequest (7.3.2):** [*] → Received (confirmedAt fixed) → Acknowledged (ack delivered or queued with DOC) | Duplicate (same contract already Received); Acknowledged → Submitted (`pol.Withdrawal.submit` accepted) | UnderReview (not matched / owner error); UnderReview → Submitted (matched by staff) | Rejected (human decision, reasoned reply); Submitted → Processed (void job bound, `PolicyVoided`). Guards: no transition may change `confirmed_at`; Rejected needs a named staff decision-maker and a reply document. Events: `WithdrawalRequestReceived` on Received, `WithdrawalRequestClosed` on Processed/Rejected.

**ApiClient (7.3.3):** Pending → Active → Suspended → Active; Active/Suspended → Revoked (terminal). Pending → Active (production) requires approved onboarding case with four-eyes. Event `ApiClientStatusChanged`.

**WebhookSubscription (7.3.4):** PendingVerification → Active → Paused (by partner) → Active; Active → Suspended (failure threshold) → Active (after successful test delivery). Event `WebhookSubscriptionSuspended`.

**ConfirmationTicket (7.3.5):** [*] → Pending (dry-run stored) → Approved (named human, step-up) | Rejected | Expired | Superseded (underlying job changed); Approved → Executed (owner command succeeded after re-run dry-run equal or difference accepted) | Failed (owner refused). Guard: approver = delegating subject or person authorised for the object. Mapping to `AiInteractionRecord`: Approved/Executed → Accepted; Rejected → Rejected; Expired/Superseded → Expired (contract §3.2.4).

Other lifecycles named but without a diagram: ApiProduct lifecycle (Draft, Published, Deprecated, Retired) (§7.1.8); journey/draft lifecycle via `chn.Journey.start/resume/save/abandon` (§9.1.1).

---

## 4. Consumed entities / dependencies (§7.2, §9.2, §15.1)

| Owner | Entities used | How (ops / events) |
|---|---|---|
| PTY | Party, Account, Consent, CommunicationPreference, Intermediary, ProducerCode, CommissionAgreement, AgencyUser, Bancassurance partnership | `pty.Party.*`, `pty.Account.*`, `pty.Consent.query/record/withdraw`, `pty.CommunicationPreference.resolve/set`, `pty.ProducerCode.validate`, `pty.ProducerOfRecord.get/bulkTransfer`, `pty.CommissionAgreement.resolve`, `pty.Vulnerability.query`, `pty.AgencyUser.*`, `pty.Bancassurance.*`; events `ConsentChanged`, `PartyUpdated`, `PartiesMerged`, `ProducerOfRecordChanged`, `IntermediaryLicenceChanged`, `ExternalUserAccessChanged`, `BookTransferCompleted` |
| PFC | Product/Version, Offering, QuestionSet, POG, distributor pack | `pfc.Product.describe` (REQ-PFC-222, channel variants), `pfc.Availability.check` (-011), `pfc.QuestionSet.evaluate`, `pfc.Pog.get`, `pfc.PolicyDraft.validate` (-224); events `ProductVersionPublished/Retired` |
| RAT | Worksheet, breakdown | `rat.Breakdown.get`, `rat.Worksheet.explain`, `rat.ChangeExplanation.get`, `rat.PriceReview.request`, `rat.PricingModification.propose`; `RateVersionActivated` |
| UW | UWIssue, Referral, Contingency, PolicyHold | `uw.Issue.listForChannel` (audience CUSTOMER/INTERMEDIARY), `uw.Referral.request`, `uw.Decline.requestReview`, `uw.Contingency.list`, `uw.PolicyHold.check`, `uw.Intake.create`; events UWIssue*, Referral*, Contingency*, PolicyHold*, `DeclineIssued` |
| POL | Policy, PolicyTerm, Job, PolicyTransaction | `pol.Submission.create`, `pol.Job.updateDraft/quote/newVersion/bind/withdraw`, `pol.PolicyChange.create` (dryRun), `pol.Cancellation.create`, `pol.Withdrawal.submit` (rights WITHDRAWAL / OBJECTION), `pol.Suspension.create`, `pol.Reactivation.create`, `pol.Renewal.*`, `pol.Policy.get/search`, `pol.EffectiveDate.limits`, `pol.Segment.changes`; ~16 POL events |
| BIL | BillingAccount, PaymentPlan, Invoice, Payment, Refund, PaymentInstrument, AccountCurrent, CommissionStatement | `bil.PaymentPlan.list/select/change`, `bil.BillingPreview.compute`, `bil.DownPayment.initiate/status`, `bil.Payment.take`, `bil.Mandate.create/sign`, `bil.PayeeAccount.create/verify/get`, `bil.Refund.*`, `bil.IntermediaryCollection.report`, `bil.AccountCurrent.get/dispute`, `bil.Commission.statements/dispute`; ~13 BIL events incl. `DownPaymentCleared` (bind continuation) |
| CLM | Claim, Exposure, ClaimsHistoryCertificate | `clm.Fnol.saveDraft/validate/submit`, `clm.ClaimTracking.get/list`, `clm.Conversation.send`, `clm.StatutoryOffer.recordAcceptance`, `clm.Certificate.request`, `clm.Service.request`, `clm.VendorInvoice.submit` |
| DOC | Outbound document, Delivery, SignatureEnvelope | `doc.Document.listForObject/get`, `doc.Ipid.get`, `doc.Document.request` (incl. `DT-WITHDRAWAL-ACK`), `doc.Signature.createEnvelope/get`, `doc.Delivery.status`; DOC channel adapters for e-mail/SMS (REQ-DOC-228) |
| WRK | BackOfficeRequest, InboundDocument, Activity, Note | `wrk.Request.submit/get/list`, `wrk.InboundDocument.submit`, `wrk.Activity.list`, `wrk.Note.list`, `wrk.Search.global` |
| PLT | User, Session, ServiceIdentity, AiInteractionRecord, AuditEvent | `plt.Token.exchange`, `plt.ExternalUser.*`, `plt.UserProfile.getPreferences/setLanguage`, `plt.Ai.invoke/recordOutcome`, `plt.AiToggle.resolve`, `plt.Audit.append`, `plt.Approval.request`, `plt.Number.next`, `plt.Time.now`, `plt.FeatureFlag.evaluate`, `plt.Workflow.*`, `plt.Authority.check` (by owners) |
| MKT | ConfigKey, CapabilitySwitch, TranslationEntry, SpiBinding | `mkt.Configuration.resolve`, `mkt.Capability.get`, `mkt.L10n.bundle/format`, `mkt.Spi.bind`, `MotorDataProvider.*` |
| CMP | ClockInstance, Complaint, DsarRequest, AiSystem | `cmp.Complaint.create`, `cmp.Dsar.create`, `cmp.Clock.query`, `cmp.AiSystem.get`; events `ClockStarted/Warned/Breached/Met/Elapsed` |
| MIG | Cross-reference | `mig.Routing.resolve` (REQ-MIG-149, authoritative), `CoexistenceMasterChanged` (cache accelerator) |
| DAT | Synthetic data, model registry | REQ-DAT-280/-284 sandbox data; REQ-DAT-005 model registry; data contract `dc.chn.channel.v1` |
| RI, FIN | — | No direct dependency (§15.1) |

---

## 5. Events

### 5.1 Produced (§8.1; "topic `chn.events.v1`", accepted by R-47 / CCR-CHN-01, CCR-CHN-08)

| Event | Trigger | Key | Payload | Consumers |
|---|---|---|---|---|
| `WithdrawalRequestReceived` | Withdrawal confirmation | withdrawal_request_id | request number, confirmedAt, matched policy id (nullable), channel | POL (backup trigger, idempotent void on withdrawal_request_id, R-89), CMP (REQ-CMP-253), DAT |
| `WithdrawalRequestClosed` | Processed or Rejected | withdrawal_request_id | outcome, decision-maker (if rejected), policy id | DAT |
| `PermissionMatrixActivated` | Version → Active | legal_entity_id | version, effective_from, approval ref | DAT |
| `ApiClientStatusChanged` | Client activated/suspended/revoked | client_id | organisation, status, reason | DAT |
| `WebhookSubscriptionSuspended` | Failure threshold | subscription_id | client, endpoint host, failure count | DAT |
| `ConfirmationTicketDecided` | Ticket Approved/Rejected/Expired | ticket_id | tool, transaction type, decision, approver, agent client | DAT |
| `ReferToBackOfficeCreated` | Matrix REFER created WRK request | session or object id | transaction type, channel, request number | DAT |
| `ChannelJourneyAbandoned` | Journey inactive beyond threshold | session_id | journey type, last step, pseudonymous ids | DAT |
| `DisclosureReceiptRecorded` (R-95) | Pre-contractual pack presented and provided in a journey (REQ-CHN-316) | job_id | job id, document ids, versions, hashes, presented/acknowledged times, channel | POL (`DisclosureDeliveryView`, R-83), DAT |

### 5.2 Consumed (§8.2) — all idempotent on event_id

- **POL:** `QuoteIssued`, `QuoteExpired`, `JobWithdrawn`, `JobNotTaken` (dashboards, expire drafts/deep links); `PolicyBound`, `PolicyIssued`, `ProofOfCoverIssued`, `PolicyChanged`, `PolicyCancelled`, `PolicyVoided`, `PolicyReinstated`, `CancellationScheduled`, `RenewalOffered`, `RenewalBound`, `PolicyLapsed`, `PolicyNonRenewed` (read model, notifications, webhooks, withdrawal status on `PolicyVoided`).
- **UW:** `UWIssueRaised/Approved/Rejected`, `ReferralAssigned`, `ReferralSLABreached`, `ContingencyCreated/Overdue`, `PolicyHoldActivated/Released`, `DeclineIssued`.
- **BIL:** `InvoiceIssued`, `PaymentReceived`, `CashAllocated`, `PaymentReversed`, `DownPaymentCleared` (bind continuation), `DelinquencyStarted`, `NonPaymentNoticeSent`, `RefundApproved`, `RefundDisbursed`, `DisbursementRejected`, `DisbursementStopped` (D4 — refund shown as held), `CommissionCalculated`, `CommissionPaid`.
- **CLM:** `ClaimReported`, `ClaimUpdated`, `PaymentIssued`, `ClaimClosed`, `ClaimReopened`, `ClaimsHistoryCertificateIssued`. Explicitly **not** consumed: `StatutoryOfferDue`, `StatutoryOfferBreached` (§8.2 note).
- **DOC:** `DocumentRendered`, `DocumentDelivered`, `DeliveryFailed`, `DocumentSuperseded`, `SignatureCompleted`, `SignatureDeclined`.
- **WRK:** `RequestAnswered`, `RequestClosed`, `ActivityCreated` (externally owned), `DocumentLinked`.
- **PTY:** `ConsentChanged`, `CommunicationPreferenceChanged`, `PartyUpdated`, `PartiesMerged`, `ProducerOfRecordChanged`, `IntermediaryLicenceChanged`, `ExternalUserAccessChanged`, `BookTransferCompleted`.
- **PFC:** `ProductVersionPublished/Retired` (invalidate describe caches). **RAT:** `RateVersionActivated` (invalidate dry-run cache).
- **PLT:** `ConfigChanged`, `FeatureFlagChanged`, `AiToggleChanged`, `AiKillSwitchActivated` (disable AI surfaces within 60 s).
- **CMP:** `ClockStarted` (POL_WITHDRAWAL_DISTANCE, POL_REFUND_DUE), `ClockWarned`, `ClockBreached`, `ClockMet` (POL_REFUND_DUE), `ClockElapsed` (POL_WITHDRAWAL_DISTANCE — hide function on card).
- **MKT:** `TranslationBundlePublished`, `PackActivated`. **MIG:** `CoexistenceMasterChanged`.

### 5.3 Partner webhook mapping (§8.3)

`quote.issued`, `quote.expired`; `quote.referral.decided`; `policy.bound/issued/changed/cancelled/renewal_offered/lapsed`; `document.available`; `payment.received`, `payment.overdue` (collect authority only); `commission.statement_issued`; `claim.reported/status_changed/payment_issued/closed` (also vendors for assignments); `request.answered`. Payloads: identifiers + minimal data only, never P2/P3 (REQ-CHN-235, BR-CHN-047).

---

## 6. APIs

### 6.1 Internal operations exposed (§9.1.1; REST under `/api/chn/v1/`, all commands need `Idempotency-Key`, RFC 9457 errors with `CHN-ERR-*`, owner codes in `errors[]`)

`chn.Permission.evaluate` (query: transactionType, channel, actor, onBehalfOf, objectRef, proposedValues → outcome, limits, entry, version, reasons, explanation key); `chn.PermissionMatrix.createDraft/updateEntry/runScenarios/submit/schedule/get/diff`; `chn.Journey.start/resume/save/abandon`; `chn.Quote.startMotor/startHome` (DR); `chn.Wallet.start/status/cancel`; `chn.Purchase.confirm` (DR); `chn.Change.preview/confirm/refer` (DR); `chn.Withdrawal.declare` (never rejects after presence validation) and `get/list`; `chn.Notification.list/markRead`, `chn.NotificationSubscription.set`; `chn.Message.send/list`; `chn.ApiProduct.*`; `chn.Partner.register`, `chn.Onboarding.*`, `chn.Client.request/approve/suspend/revoke`; `chn.Webhook.subscribe/verify/pause/replay/deliveries`; `chn.Aggregator.quote` (always DR); `chn.DeepLink.open`; MCP methods `tools/list`, `tools/call`, `resources/read`; `chn.Ticket.get/approve/reject`; `chn.AgentLog.query/export`; `chn.CoView.start/end`; `chn.Dsar.export/restrict/erase` (DR); `chn.Language.set/get`; `chn.Import.*` (DR, sourceKey).

Named error codes: CHN-ERR-IDEMPOTENCY-KEY-REQUIRED, -IDEMPOTENCY-MISMATCH (409), -UNKNOWN-TRANSACTION, -FINAL-ENTRY, -SCENARIO-FAILED, -CHANNEL-DISABLED (403), -NOT-PERMITTED (403), -LIMIT-EXCEEDED, -WALLET-UNAVAILABLE, -WALLET-TIMEOUT, -NOT-FOUND, -ENDPOINT-NOT-HTTPS, -MAPPING, -DEEPLINK-INVALID, -TICKET-EXPIRED, -TICKET-STALE, -CODE-EXPIRED, -LANGUAGE-NOT-ENABLED, -PRECONDITION, -UNKNOWN-CHANNEL, -AGENT-ROUTE, -AGENT-DATA-RELEASE, -LANGUAGE-CONTROL-REQUIRED, -LEGACY-POLICY.

### 6.2 External partner API surface (focus)

**API products, P1 (§9.1.2):**

| API product | Audience | Resources | Scopes |
|---|---|---|---|
| Product information v1 | All partners | products, offerings, IPID | `product.read` |
| Motor quote and bind v1 | Intermediaries, banks | quotes (quick, full, dry-run), versions, issues, referral requests, binds, cover notes | `motor.quote`, `motor.bind`, `uw.referral` |
| Aggregator motor quote v1 | Aggregators | aggregator quotes (dry-run only), deep links | `aggregator.quote` |
| Policy servicing v1 | Intermediaries, banks | policies, changes (preview/submit), cancellation requests, renewals, documents | `policy.read`, `policy.change`, `policy.cancel.request` |
| Billing v1 | Collecting intermediaries, banks | billing accounts, invoices, payment links, collection reports, account current, commission statements | `billing.read`, `billing.collect`, `commission.read` |
| Claims v1 | Intermediaries, banks | FNOL, tracking, document upload | `claim.report`, `claim.read` |
| Vendor services v1 | Vendors (SYS-04) | service assignments, invoices | `vendor.service`, `vendor.invoice` |
| Webhooks v1 | All | subscriptions, deliveries | `webhook.manage` |
| Book synchronisation v1 | Broker software | changes since cursor | `book.sync` |

**Resource → owner mapping (§9.1.3):** `GET /products/{code}/describe`; `GET /products/{code}/ipid`; `POST /quotes` (create/dry-run); `POST /quotes/{id}/versions`, `GET …/compare`; `GET /quotes/{id}/issues`; `POST /quotes/{id}/referral-requests`; `POST /quotes/{id}/bind`; `GET /policies/{no}/cover-note`; `GET /policies/{no}?validAt=`; `POST /policies/{no}/changes` (dryRun); `POST /policies/{no}/cancellation-requests` (POL or WRK per matrix); `GET /{object}/{id}/documents`; `GET /billing-accounts/{no}`; `POST /invoices/{no}/payment-links`; `POST /collections`; `GET /account-current`, `/commission-statements`; `POST /claims`; `GET /claims/{no}/tracking`; `POST /documents`; `GET /book/changes?cursor=`; `/vendor/services`, `/vendor/invoices`; plus `POST /api/chn/v1/aggregator/quotes` (J-07, §4.7). All under `/api/chn/v<major>/`.

**Contract rules (Must P1 unless noted):**
- Idempotency-Key on every command; key → result kept ≥ 7 days (REQ-CHN-218); BR-CHN-040 says CHN keeps 30 days for partner commands; replay returns original, different body → 409.
- `dryRun` on every command changing money, cover or legal status; dry-run results `bindable: false` (REQ-CHN-219).
- RFC 9457 errors with localised `title`/`detail` by `Accept-Language` (el/en; default from client registration, entity default `en`), codes never change (REQ-CHN-220, -324).
- Cursor pagination, `limit` ≤ 200, stable sort; `validAt` on policy queries (REQ-CHN-221).
- Major version in path; additive within major; ≥ 6 months deprecation notice via portal, e-mail and `Deprecation`/`Sunset` headers; parallel run (REQ-CHN-222, BR-CHN-041).
- OpenAPI 3.1 docs generated from the schemas the gateway enforces; contract tests (REQ-CHN-215).
- Schema validation at edge and in CHN; unknown fields in commands rejected (REQ-CHN-228).
- Rate limits and quotas per client and API product, `429` + `Retry-After` + remaining-quota headers (REQ-CHN-225). Defaults: aggregator dry-run 20 req/s (burst 40), intermediary APIs 10 req/s; daily quotas per contract (BR-CHN-042).
- Organisation data scoping: intermediary sees own producer codes' book; aggregator only its quotes (else 404); bank only its partnership (REQ-CHN-227, BR-CHN-048).
- Call logging without personal data (REQ-CHN-230); suspension/revocation effective at edge ≤ 60 s (REQ-CHN-240).
- Credentials: private-key JWT or mTLS, no shared secrets, per-client scopes, key rotation/expiry, bound to organisation, API products, legal entity, channel code, producer codes (REQ-CHN-224, REQ-PLT-058). Tokens ≤ 10 min with refresh rotation (NFR-CHN-008). FAPI 2.0 (PAR, PKCE, sender-constrained) for customer-authorised partner flows — **Should** (REQ-CHN-226).
- Onboarding via developer portal; production needs checklist (contract, DORA/outsourcing, security review, certification run) and four-eyes approval (REQ-CHN-223, BR-CHN-046). Certification suite is Should (REQ-CHN-239).
- Sandbox per API product on DAT synthetic data with deterministic scenarios (referral, decline, payment failure, document delay, claim progression) and time-travel (REQ-CHN-238).
- Developer portal: catalogue, interactive docs, bilingual guides, changelog, deprecations, status, usage, credentials and webhook management, support (REQ-CHN-237).
- Partner document upload with malware scan (REQ-CHN-244). DORA ICT third-party registration of partners providing ICT services (REQ-CHN-245, Must); incident inputs (REQ-CHN-246, Should).
- Should P1: ACORD NGDS mapping (REQ-CHN-217, lowered per CON-021), API status page (-229), async operation status (-231), webhook ordering per aggregate with sequence numbers (-236), broker-software user attribution via user-assertion or token exchange (-241), vendor APIs (-242), bulk book sync (-243), all comparison-site intake REQ-CHN-247…256.

### 6.3 Webhooks (focus)

Subscription (REQ-CHN-232): HTTPS only, signing secret/key, filters, event types limited by API products → status PendingVerification. Verification challenge before activation; every delivery signed (RFC 9421 HTTP Message Signatures default, or HMAC with rotating secret) with timestamp and unique delivery id (REQ-CHN-233). At-least-once; exponential backoff 30 s → 1 h for 24 h; then failed; suspension after **50 consecutive failures** (BR-CHN-043; REQ-CHN-234 says "configurable failure count"); partner notified; replay any delivery from last 30 days (new delivery id, original event id). Payloads minimal (REQ-CHN-235). Order per aggregate + sequence numbers (REQ-CHN-236, Should). First attempt ≤ 10 s p95 after event (NFR-CHN-018). Delivery state recoverable by replay from the event stream (NFR-CHN-006).

### 6.4 MCP tool catalogue, P1 (§9.1.4)

READ: `product_info`, `explain_price`, `policy_summary`, `billing_status`, `claim_status`, `documents_list`/`document_get`, `search` (staff agents only). DRY_RUN: `quote_motor_dry_run`, `policy_change_preview`. STATE_CHANGING without ticket: `quote_motor_save`, `payment_link_create`, `claim_report_draft`, `request_submit`. STATE_CHANGING with ticket: `quote_motor_bind`, `policy_change_submit`, `cancellation_request`, `claim_report_submit`. Each maps to one channel transaction type and named owner operations.

### 6.5 Outbound integrations (§9.3)

gov.gr Wallet via KED (OAuth 2.0, push consent; only through `MotorDataProvider`); gov.gr authentication / EUDI Wallet (via PLT, UNVERIFIED for private insurers, OI-PLT-06); card acquirer / instant payments (hosted, via BIL `PaymentChannelProvider`, to be selected); mobile platform push services via CHN push adapter in PLT's adapter host (REQ-PLT-006); SMS/e-mail via DOC; aggregators and broker software (REST over mTLS or private-key JWT); bank IdPs (OIDC or SAML 2.0 via REQ-PLT-057; D6); bank systems (REST, FAPI 2.0); AI agent clients (MCP streamable HTTP, OAuth 2.1, RFC 9728, RFC 8707); maps/geocoding via `Geocoder` SPI.

---

## 7. SPIs / country-pack interfaces

| SPI / pack item | Use in CHN | Ref |
|---|---|---|
| `MotorDataProvider` (MKT REQ-MKT-105, -259, -316) | `vehicleByPlate` lookup; `requestWalletConsent(subject, purpose, legalBasis, channel) → pendingRef`, `consentResult(pendingRef)`, `cancelWalletConsent(pendingRef)`; binding attrs `legalBasisRef`, `channelsSupported`, `timeoutMinutes`, `retentionYears` | REQ-CHN-082, -110…-112; CCR-CHN-04 |
| `DigitalIdentityProvider`, `IdentityFederationProvider` | National eID / EUDI wallet sign-in and proofing | REQ-CHN-066 (Could) |
| `IdValidator`, `AddressFormatter`, `NameTransliterator` | AFM (9 digits, check digit), postcodes, ELOT 743 | SCR-CHN-01/02, §10.4 |
| `ConsentRules` (REQ-MKT-111) | Consent and tracker re-ask | REQ-CHN-291, BR-CHN-062 |
| `StatutoryClockSet` (REQ-MKT-287/-288) | Withdrawal period values (via POL) | REQ-CHN-161 |
| `PolicyLifecycleRules` | Withdrawal refund basis; suspension (plate deposit) | BR-CHN-026, REQ-CHN-128 |
| `PaymentChannelProvider`, `PaymentReferenceGenerator` (BIL) | Hosted payment flows | §10.4 |
| `StatutoryDeliveryRule` | Acknowledgement media (Greece: e-mail + portal inbox) | §10.4 |
| `DocumentLanguageRule` | Document language independent of UI switch | BR-CHN-065 |
| `Geocoder` | FNOL location | SCR-CHN-09 |
| `TaxCalculator.treatment` (MKT, via originating module) | Displayed only; never computed in CHN (D2) | REQ-CHN-090 |
| `FriendlySettlementClearing` (CLM) | Joint accident report flow | REQ-CHN-154 |

Multi-market (§10.4): Cyprus stub deliberately different — labels, 14 days from `eu` pack, refund **less cost of cover** (to prove the switch), `MotorDataProvider` = `NotAvailable`, cards only, WEB_DIRECT only, a third test locale; no CHN code changes for a new market (REQ-MKT-008).

---

## 8. Screens (§6)

All screens: custom-coded front ends calling the CHN back-end only; describe-driven fields; semantic states from contract §3.9.9; standard loading/empty/error/permission/offline states; every screen carries the "ΕΛ | EN" shell control (§6.0). Design-guide patterns cited: IB-01 command palette, IB-02 keyboard list nav, IB-03 sidebar work views, IB-05 split pane, IB-07 approve-the-diff, IB-08 explain-why, IB-11 work-left home, IB-13 statement view, IB-16 density, IB-18 overlay, IB-20 pinned comments, IB-21 semantic status, IB-22 AI as a surface, IB-23 state-change motion, IB-24 quiet chrome, IB-26 global filter bar, IB-28 exception-first, IB-29 log-style dense table, IB-32 binary status. Permission codes: PUB, CUS, DEL, PRD, AAD, BNK, DEV, CPO, APM, SEC, CMP, CSR.

| ID | Name | Persona | One line |
|---|---|---|---|
| SCR-CHN-01 | Public quote entry (quick quote) | ROLE-01/02 anonymous | Product, start date, plate, AFM (optional), main driver DOB, garaging postcode, Wallet action, indicative price |
| SCR-CHN-02 | Quote and buy journey | ROLE-01/02, ROLE-03 assisted | Stepper Vehicle→Drivers→Needs→Cover→Questions→Details→Review & documents→Payment→Confirmation; price panel |
| SCR-CHN-03 | gov.gr Wallet consent | PUB/CUS | Modal two-step consent with 60-min countdown and manual fallback |
| SCR-CHN-04 | Customer dashboard | CUS/DEL | Policy cards, billing, claims, requests, documents, renewals, notifications, withdrawal action |
| SCR-CHN-05 | Policy detail | CUS/DEL/PRD | Cover as of today, coverages, contingencies, history, documents, allowed actions |
| SCR-CHN-06 | Self-service change | CUS/PRD | Intent → describe form → dry-run diff → confirm / send for review |
| SCR-CHN-07 | Payments and billing | CUS/DEL | Statement view, pay now, methods, IBAN, e-mandate, due day, refunds |
| SCR-CHN-08 | Document vault | CUS/DEL/PRD | Documents, accessible version, signing, paper request |
| SCR-CHN-09 | Claim reporting | CUS/PRD/PUB (third party) | Guided FNOL with map, photos, joint accident report, autosave, offline |
| SCR-CHN-10 | Claim tracking | CUS/DEL/PRD/PUB link | Milestones, outstanding docs, payments, offer accept, payee IBAN |
| SCR-CHN-11 | Messages and notification centre | CUS/PRD | Threads; notifications Actionable/Important/Informational |
| SCR-CHN-12 | Consent and preference centre | CUS/PUB | Marketing consents, comms prefs, medium, correspondence vs interface language, region format, trackers, authorised AI agents, trusted contacts, DSAR |
| SCR-CHN-13 | Withdrawal function (signed-in and public) | ROLE-01/02, ROLE-06 on instruction | Three steps: declaration, confirmation, receipt; no trackers |
| SCR-CHN-14 | Sign-in, registration, account security | PUB/CUS/PRD | Hosted sign-in, OTP, passkey, policy linking, sessions, recovery |
| SCR-CHN-15 | Customer assistant panel (AI, optional) | CUS | Disclosure, Q&A with sources, approve-the-diff cards, hand-off |
| SCR-CHN-16 | Broker and agent dashboard | PRD | Work views with counts, palette, density |
| SCR-CHN-17 | Broker quote and compare workspace | PRD | Split pane, versions, issues, IDD checklist, bind, cover note |
| SCR-CHN-18 | Issue response and referral status | PRD | Referral threads, info requests, decisions, contingencies |
| SCR-CHN-19 | Broker servicing | PRD | Changes, OOS, cancel, reinstate, renewals under matrix |
| SCR-CHN-20 | Book of business | PRD | Search/filter/export (audited) |
| SCR-CHN-21 | Producer notifications | PRD | As SCR-CHN-11 with producer event types |
| SCR-CHN-22 | Commissions and account current | PRD/AAD | Statements, collection report upload, disputes |
| SCR-CHN-23 | Producer billing accounts | PRD | SCR-CHN-07 read-only + send payment link + notices |
| SCR-CHN-24 | Agency administration | AAD | Users, roles, producer codes, removal with renewal transfer, attestation, team view |
| SCR-CHN-25 | Back-office request | CUS/PRD | Typed pre-filled request (replaces UIL-A2 processing-request form) |
| SCR-CHN-26 | Bank embedded quote and sale (white-label) | BNK | **Should P1 fast-follow (D6)** |
| SCR-CHN-27 | Developer portal: catalogue and docs | PUB/DEV | API products, OpenAPI try-it, deprecations, status |
| SCR-CHN-28 | Developer portal: org, credentials, sandbox | DEV/APM | Org (AFM), partner type, JWKS/cert, scopes, API message language, checklist, four-eyes approval |
| SCR-CHN-29 | Developer portal: webhooks and usage | DEV | Subscriptions, signing method, deliveries, replay, usage |
| SCR-CHN-30 | Permission matrix editor (staff) | CPO | Grid of transaction × channel per role, limits, scenarios, diff |
| SCR-CHN-31 | Channel governance: partners and AI agents | SEC/APM/CMP | Exception-first oversight, logs, suspend/revoke, evidence export |
| SCR-CHN-32 | Contact-centre co-view | CSR | Read-only customer view with consent code |

Inventory: 11 owned UI-library items (UIL-P1…P4, UIL-C13, UIL-A1…A6), all specified; UIL-A2 processing-request form replaced by SCR-CHN-25 (§6.33). Consumed: SCR-PCACC1-01 Login, SCR-PCACC2-02 error pages (PLT), SCR-PCACC1-03/-04 (PTY), SCR-CC-01…05 (CLM), SCR-PX-01/-02 (WRK), SCR-PD2-* (PFC), UIL-U1, UIL-B7, UIL-F5.

---

## 9. Regulatory, tax and statutory rules

### 9.1 Stated explicitly by the source

| Rule | Detail | IDs |
|---|---|---|
| Withdrawal function label (Greece) | "Πατήστε εδώ για υπαναχώρηση" (or equally clear), continuously available during the withdrawal period, prominent; English informative "Withdraw from contract here" | Law 2251/1994 Art. 3ζα §1 (Law 5317/2026 Art. 69); REQ-CHN-160, BR-CHN-020 |
| Declaration fields | Name, contract identification, electronic contact only; reason optional; no sign-in, no step-up | Art. 3ζα §2; REQ-CHN-164, BR-CHN-022, REQ-CHN-064 |
| Confirmation label | "Επιβεβαίωση υπαναχώρησης"; English "Confirm withdrawal" | Art. 3ζα §3; REQ-CHN-165 |
| Acknowledgement | Durable medium, without undue delay, with declaration content and date/time; CHN target: requested within 30 s, delivered within 1 minute; direct DOC fallback (`DT-WITHDRAWAL-ACK`) if POL unreachable 30 s | Art. 3ζα §4; REQ-CHN-167, BR-CHN-024 |
| Presumption of timeliness | Function used before expiry = timely; `confirmedAt` from PLT time is the statutory receipt time; never auto-reject | Art. 3ζα §5; REQ-CHN-165, -166, -170, BR-CHN-023, -025 |
| Withdrawal period | 14 calendar days from later of conclusion and receipt of terms; max 12 months + 14 days if terms never received; exclusions incl. travel/baggage < 1 month; ancillary contracts end without cost | Art. 3ιε (Law 5317/2026 Art. 71); REQ-CHN-161, -172 (values from POL/MKT, not CHN) |
| Refund | Within 30 calendar days of receipt of declaration; on withdrawal from an insurance contract the consumer pays no amount → Greece pack full refund; cancellation source `DistanceWithdrawal`, refund method `FullRefund` | Art. 3ιστ §1–2 (Law 5317/2026 Art. 72); BR-CHN-026, REQ-CHN-168, -169, CCR-CHN-03 |
| Refund clock | `POL_REFUND_DUE`, variant DISTANCE_WITHDRAWAL starting at receipt of declaration, read via `cmp.Clock.query` | REQ-CHN-169 (PRD-11 §10.5, R-60/K-04) |
| Pre-contractual information | In Greek, before consumer is bound; pack shown inline and delivered on durable medium before bind | Art. 3ιδ (Law 5317/2026 Art. 70); REQ-CHN-092, -093, -095, BR-CHN-010 |
| Withdrawal entry is final | WITHDRAW on WEB_DIRECT/APP for ROLE-01 always SELF_SERVICE during the period; cannot be configured away | REQ-CHN-058, BR-CHN-021 |
| IDD demands and needs / IPID | Before cover selection in non-advised sale; mismatch warning with explicit acknowledgement; IPID opened before confirm | IDD Art. 20; REQ-CHN-086, -087, -092, BR-CHN-011 |
| Information medium | Paper by default unless customer chooses otherwise and e-mail verified; free paper copy on request | Law 4583/2018 Art. 33; REQ-CHN-094, -142 |
| Intermediary information | Shown before conclusion for intermediated online sales; direct: insurer identity, register, supervisor | Law 4583/2018 Art. 28; Art. 3ιδ; REQ-CHN-095 |
| Duty of disclosure | Pack statement shown before underwriting questions | Law 2496/1997 Art. 3; REQ-CHN-096 |
| Objection right | Separate labelled function → `pol.Withdrawal.submit` right OBJECTION (Should) | Law 2496/1997; REQ-CHN-129 |
| Negative target market | Channel rule WARN/STOP/REFER (default STOP direct, WARN producers) | DR 2017/2358 Art. 5; REQ-CHN-088, BR-CHN-012 |
| GDPR Art. 22 | Automated-decision notice with human-review link (RAT price / UW decline, incl. aggregator responses) | REQ-CHN-091, -253 |
| ePrivacy / HDPA trackers | No non-essential storage before opt-in; accept all / reject all / settings equal prominence; no pre-ticked; re-ask after 12 months or wording change | Dir. 2002/58/EC Art. 5(3); HDPA 25.02.2020; REQ-CHN-290, -291, BR-CHN-062 |
| AI Act Art. 50(1) | Disclose AI interaction before first exchange, from 2 Aug 2026 | REQ-CHN-283 |
| AI Act Art. 50(2) | Machine-readable marking; delayed to 2 Dec 2026 for existing systems (Reg. (EU) 2026/1744) but implemented from day one | REQ-CHN-286 |
| PSD2 SCA / PCI DSS v4.x | Card and instant payments only via BIL's hosted acquirer flow; CHN never handles card data; CSP restricts payment frames | REQ-CHN-103, -134, -135, -312 |
| EAA / WCAG | Treated as applicable (R-57); WCAG 2.2 AA; accessibility statement per front end | REQ-CHN-295…298, NFR-CHN-010…012 |
| gov.gr Wallet (intermediaries) | Two-stage consent; refusal by rejection or inaction within 1 hour; via KED with OAuth 2.0; logs 5 years; use only for the motor policy; no profiling; no sharing except selected insurer | JMD 15331 ΕΞ 2025 (B 2461/20.05.2025), Art. 3 §6; REQ-CHN-110…118, BR-CHN-030…033 |
| Payment notice wording | BIL invoices labelled "ειδοποίηση πληρωμής", never "τιμολόγιο" (D3); receipts are fiscal documents with CMP series, number and MARK | REQ-CHN-133, SCR-CHN-23 |
| DORA | Partners providing ICT services registered as ICT third parties | Reg. (EU) 2022/2554 Art. 28–30; REQ-CHN-245 |
| Sanctions | Channels never reveal screening status | REQ-PTY-183; REQ-CHN-104, -276 |

### 9.2 Referenced but not specified (gaps)

- JMD **25061 ΕΞ 2024** (B 4101/2024) — insurers' legal basis for Wallet: text UNVERIFIED (OI-CHN-01); time-out, retention, data set, interface for an insurer channel assumed from the intermediary decision.
- Law 5317/2026 entry-into-force/transitional provisions and whether duty falls on insurer, intermediary or both on intermediary websites (OI-CHN-03).
- Greek article transposing IDD Art. 20(1) demands and needs (OI-CHN-04).
- Law 3471/2006 article numbers; HDPA primary source (OI-CHN-05).
- DORA ITS on register of information (Implementing Reg. (EU) 2024/2956, "from knowledge", OI-CHN-07).
- eIDAS relying-party dates (OI-PLT-06).
- IPT and levies are displayed but not specified here (D2; owned by originating module via `TaxCalculator`).
- Withdrawal period lengths, exclusions, objection period: all from POL/MKT clocks.

### 9.3 Deferred to configuration / country pack

Withdrawal labels (BR-CHN-020, `chn.withdrawal.labels`), periods (`StatutoryClockSet`), refund basis (`PolicyLifecycleRules`), Wallet binding and 60-min time-out (BR-CHN-030/031, `chn.wallet.*`), 5-year Wallet retention (BR-CHN-032), consent rules and 12-month re-ask, region-format pairing (BR-CHN-066), acknowledgement media (`StatutoryDeliveryRule`), policy-link verification method (REQ-CHN-061), plate format, AFM validation, nat-cat statement for business packages (REQ-CHN-302, P3), ENFIA-related property identifiers ATAK/KAEK (REQ-CHN-299, P2).

---

## 10. Greek-market specifics

- **AFM:** owner AFM on quick quote (optional; required for Wallet), policyholder AFM via `IdValidator` (9 digits, check digit); used for policy linking (policy number + AFM + DOB, REQ-CHN-008/-061); broker/book search by AFM; partner organisation AFM in developer portal.
- **gov.gr Wallet** pre-fill (Should P1 for the feature, Must for fallback/receipts/purpose limitation: REQ-CHN-112, -114, -115, -116): data set — owner AFM, name, father's name, licence number, birth date, licence issue/expiry, categories; plate, ownership, VIN, vehicle type, fuel, make, paint, engine capacity, power, seats, first-registration dates, last modification (§3.2). Purpose code `WALLET_MOTOR_QUOTE`. Intermediary-retrieved registry data accepted with source INTERMEDIARY_REGISTRY (REQ-CHN-117, Should).
- **gov.gr authentication / EUDI wallet:** Could (REQ-CHN-066), UNVERIFIED for private insurers.
- **myDATA:** not named directly; receipts show CMP series/number and **MARK** (REQ-CHN-133); documents pending fiscal registration (REQ-CHN-102, -231).
- **Information Centre / bureau:** only indirect — claims history and bonus–malus "obtained by POL/RAT from the bureau where the pack provides it" (J-01 step 2). **Friendly Settlement (Φιλικός Διακανονισμός)** joint accident report is Must P1 (REQ-CHN-154).
- **Plate deposit** suspension (Could, REQ-CHN-128).
- **ENFIA** property identifiers ATAK/KAEK for home (P2, REQ-CHN-299).
- **Greek language:** Greek binding and default; English always present; statutory Greek labels; Greek terms with † (unconfirmed working translations) or ‡ (PRD-18 glossary) need ROLE-46 confirmation before use in customer templates (§1.7); "κανάλι διανομής" for channel; Greek plates normalised for case/script; accent- and script-insensitive search (SCR-CHN-20); ELOT 743 transliteration; ROLE-46 legal review of statutory Greek text (NFR-CHN-013).
- **EUR:** money formats via `mkt.L10n.format`; example "1.234,56 €" for el-GR.
- **Time zone:** Europe/Athens for withdrawal deadlines, DST test cases (§14.x).
- **Market context:** Greek bancassurance exclusivities shifting (Piraeus/Ethniki, NBG) → partnerships are data (§3.2).

---

## 11. Controls

**Authority types registered:** none — "CHN registers no authority type" (§12.2). CHN passes acting user, on-behalf-of and `actorType`; owners call `plt.Authority.check` (CD-05). Matrix is an *additional* restriction only (REQ-CHN-052).

**Maker-checker (§12.3):**

| Change | Maker | Checker |
|---|---|---|
| Matrix version activation | Channel product owner | 2nd CPO or distribution manager (ROLE-09) |
| Production partner client activation | API product manager | Security officer or 2nd APM |
| API product version publish/deprecate | APM | Lead architect |
| Agent client allow-listing for production | Security officer | Channel product owner |
| Tool definition changes | Developer | Approver via REQ-PLT-225 |
| AI toggles tenant/entity for AI-CHN-01…05 | Feature owner | PLT approval (REQ-PLT-228) |
| Withdrawal labels and statutory message-catalogue entries | CPO | Compliance officer |
| Bulk book transfer on producer removal | Agency administrator | Insurer distribution manager (REQ-PTY-247, R-18) |

**SoD (§12.4):** matrix maker ≠ approver; agency admin cannot self-grant; co-view CSR cannot act; approver of partner production access cannot be a contact of that partner; an AI agent cannot approve its own ticket.

**Audit (§12.1):** permission evaluations (decision record linked to correlation id), matrix lifecycle, withdrawal lifecycle, wallet outcomes, consents, partner onboarding/client lifecycle/key rotation, webhook changes/replays, agent tool calls and ticket decisions with step-up proof, co-view sessions, broker book exports, agency admin (via PTY REQ-PTY-251). AuditEvent per facade-executed command with actor = agent, on-behalf-of, `ai_interaction_id` (REQ-CHN-272).

**Step-up list (BR-CHN-013 = REQ-CHN-064):** payee/refund bank account change, payment-method change, sign-in contact change, cancellation request, approval of agent tickets, claim offer acceptance. Never for withdrawal, complaints, document access.

**Session/identity controls:** idle 15 min customer / 30 min producer / bank ≤ 30 min, absolute 12 h (BR-CHN-014); policy-link 5 attempts / 15 min → 30-min lock (BR-CHN-015); intermediary accounts suspended within 5 min of licence suspension (BR-CHN-019); MFA always for intermediaries and bank staff (REQ-CHN-063); fraud controls only add verification, never permanent refusal (REQ-CHN-070); no visual-puzzle-only challenge.

**GDPR handling:** consent receipts; DSAR entry (REQ-CHN-149); `chn.Dsar.export/restrict/erase` fan-out with retention-conflict recording (REQ-CHN-304); minimisation, `<Entity>View` read models only, P2 masked (REQ-CHN-305); no P2/P3 in notifications or webhooks (BR-CHN-047, -060); pseudonymous analytics without field values (REQ-CHN-292); EU hosting only (REQ-CHN-293, NFR-CHN-009); statutory/sensitive pages excluded from analytics (REQ-CHN-294, BR-CHN-063); account deletion routed to CMP (REQ-CHN-072).

---

## 12. AI features (§11) — all off by default (BR-CHN-056), must be in AI register before enabling (REQ-CHN-289)

| ID | Feature | Classification | Toggle scope | MVP need |
|---|---|---|---|---|
| AI-CHN-01 | Bilingual customer service assistant | Limited risk (Art. 50(1)); EIOPA expectations applied | Tenant, entity, LoB, role, user opt-out; default off | §1.5 says **Could, off by default** in P1; but REQ-CHN-283…287 (disclosure, hand-off, governed tools, labelling, works-without-AI) are **Must P1**. Not needed for go-live functionality. |
| AI-CHN-02 | External and first-party agent access via MCP facade | **Uncertain → high-risk-level controls** | Tenant, entity, LoB, realm, per agent client; default off | REQ-CHN-003 anchor + 22 family requirements are **Must P1** ("Read and dry-run tools; confirmation tickets for changes", §1.5) |
| AI-CHN-03 | Channel abuse and agent-anomaly guard | High-risk controls applied (uncertain), aligned with AI-PLT-05 | Tenant, entity; default off | Not required; deterministic rate limits are the fallback (REQ-CHN-225, -273, -311) |
| AI-CHN-04 | Claim photo capture guidance | Limited risk | Tenant, entity, LoB, user; default off | Not required; static shot list fallback |
| AI-CHN-05 | Partner payload mapping assistant | Minimal risk | Tenant, entity, role; default off | Not required; manual mapping editor |

Also surfaces owner AI features to external audiences where permitted (Should, REQ-CHN-288): AI-CLM-01, AI-DOC-05, AI-POL-05, AI-BIL-07. Monitoring profiles MP-B, MP-H, MP-D (PRD-15 §11.2); bias parity 0.8–1.25.

**MCP facade specifics (Must P1):** one MCP server per legal-entity stamp, streamable HTTP (REQ-CHN-260); tool classes READ/DRY_RUN/STATE_CHANGING, versioned and approved via REQ-PLT-225 (-261); OAuth 2.1 resource server with RFC 9728 metadata, audience = facade URI (RFC 8707), no tokens in query strings, 401 + `WWW-Authenticate` (-262); **never pass token through** — `plt.Token.exchange` (RFC 8693) to delegated token (subject = person, actor = agent, scopes = intersection) (-263); agent clients FIRST_PARTY / CUSTOMER_CHOSEN / BROKER_PLATFORM via pre-registration or allow-listed Client ID Metadata Documents, no dynamic registration in prod; FIRST_PARTY models only via PLT EU gateway (-264, R-81); `insufficient_scope` step-up with consent screen naming agent and tools (-265); person can see and revoke agents (-266); matrix with channel AI_AGENT (-267); dry-run first + ticket (-268); named human approval with step-up for bind, change bind, cancellation, payment initiation, payment-method/payee change, claim submission, withdrawal on behalf, signing (-269); ticket TTL 30 min default, max 24 h, stale invalidation, re-run at approval (-270, BR-CHN-050); full `AiAgentActionLog` (-271); AuditEvent with `ai_interaction_id` (-272); rate limits 60 calls/min, 10 tickets/day/person, circuit-break > 30 % refusals in 10 min for 15 min (-273, BR-CHN-051, -054); PLT safety screening incl. prompt injection (-274); `actorType = AI_AGENT` to UW/POL for confidence lanes (-275); customer-safe texts only (-276); kill switch → 503 within 60 s, pending tickets still approvable in portal (-277); governance view (-279); agent activity history for the person (-281); adversarial test suite in CI (-282); agent data-release rule BR-CHN-057 (-318). Should: MCP resources (-278), staff-side agents via same facade (-280).

---

## 13. Open issues / assumptions / CCRs

**Open issues (§16.1):**

| ID | Topic | Status |
|---|---|---|
| OI-CHN-01 | JMD 25061 ΕΞ 2024 text for insurer Wallet access | Open (ROLE-32) |
| OI-CHN-02 | ACORD NGDS licensing for non-members | Open (APM) |
| OI-CHN-03 | Law 5317/2026 transitional provisions; insurer vs intermediary duty on intermediary websites | Open (ROLE-32 + ROLE-46) |
| OI-CHN-04 | Greek article for IDD demands and needs | Open (ROLE-29) |
| OI-CHN-05 | Law 3471/2006 articles; HDPA primary source | Open (ROLE-30) |
| OI-CHN-06 | Digital Omnibus on AI | **Closed** (Reg. (EU) 2026/1744; R-85, REG-007) |
| OI-CHN-07 | DORA ITS number/content | Open (ROLE-39) |
| OI-CHN-08 | Vendor portal references not re-opened | Open (CPO) |
| OI-CHN-09 | Time-saving estimates are design estimates | Open (usability tests) |
| OI-CHN-10 | Sandbox synthetic data | **Closed** (DAT REQ-DAT-280/-284) |
| OI-CHN-11 | Aggregator volumes / reuse ratio | Design value closed per D8 (cap 50 rated quotes/s); actual volumes open |
| OI-CHN-12 | CHN as requester of `DT-WITHDRAWAL-ACK` | **Closed** (R-84) |
| OI-CHN-13 | Retention for RC-CHN-INTERACTION, -WITHDRAWAL, -AGENT-LOG | Open → programme retention schedule (XMR-D-259) |
| OI-CHN-14 | Bank scope | Partly closed (D6); aggregator selection, bank partner, bank-hosted white-label still open |
| OI-CHN-15 | Customer cancellation SELF_SERVICE at go-live? (BR-CHN-007 proposes REFER) | Open, decide after pilot |

**Assumptions (§16.2):** A-1 PLT external realm, token exchange, delegated admin delivered before CHN integration testing; A-2 POL accepts `receivedAt` from CHN as statutory receipt time; A-3 owners expose customer/intermediary-audience texts; **A-4 mobile app built from the same channel back-end as the web portal**; A-5 one broker + agents as P1 intermediary channel, one aggregator Should, bank out of MVP unless signed by G0; A-6 house producer code for direct (`DIRECT-GR`).

**CCRs (§16.4), all Accepted:** CCR-CHN-01 CHN events (R-47); -02 cross-module-visible entities (R-54); -03 OBL-DMFS Settled + Greece full refund + reword REQ-POL-308 + close OI-POL-05 (R-55); -04 async `MotorDataProvider` wallet operations + legal bases (R-56; names aligned to REQ-MKT-316); -05 config key type "reference to versioned CHN artefact" so matrix version enters the configuration hash (R-56); -06 renumber contract §3.8.3 items (R-58); -07 OBL-EAA "Applies" (R-57); -08 `DisclosureReceiptRecorded` (R-95).

**Risks (§16.3):** RK-CHN-01…10 (aggregator peaks, prompt injection, ack delay, refund rule misimplemented, wallet onboarding delays, matrix misconfiguration, accessibility regressions, credential compromise, front-end logic creep, translation completeness blocking releases).

**Ten pre-build decisions (§16.5):** matrix model and go-live default matrix; Greek refund rule (CCR-CHN-03); withdrawal scope for phone/intermediary-website contracts; Wallet onboarding and legal basis; P1 aggregator and bank fast-follow partner; MCP facade go-live policy (first-party only vs allow-listed third parties, ticket rules, rate limits); ACORD membership; retention periods; self-service limits and step-up list; WCAG 2.2 AA + external audit (closed per R-57/R-101).

---

## 14. Conflicts and ambiguities found

### (a) With the infra/stack (ARCHITECTURE-DECISIONS.md, INFRASTRUCTURE.md)

1. **Broker vocabulary for events.** §8.1 "topic `chn.events.v1`, partition key in brackets"; NFR-CHN-006 "webhook delivery state recoverable by replay from the event stream". The stack has no broker: transactional outbox + in-process handlers (ADR §1). Treat "topic" as an outbox stream name and "key" as ordering key; webhook replay needs the outbox (or a webhook delivery table) kept for ≥ 30 days. Needs explicit mapping, not a redesign.
2. **Mobile app has no stack decision.** REQ-CHN-004 "customer portal (web) and mobile app"; REQ-CHN-152 offline FNOL completion in the app; REQ-CHN-298 "native app accessibility settings (dynamic type, screen readers)"; REQ-CHN-313 app offline read views; AI-CHN-04 "on-device model preferred"; push notifications (REQ-CHN-148, -314); NFR-CHN-015 "mobile app on the two latest major OS versions". The ADR only names React 19 + Vite web front ends served by ASP.NET Core. **Decision needed:** PWA vs React Native/Expo vs native. The Must P1 scope assumes a native-capable app. Push needs a provider (FCM/APNs directly or Azure Notification Hubs — not in INFRASTRUCTURE.md).
3. **"One backend-for-frontend per front-end family"** (§1.2 decision 1) and independent deployment of front ends and channel back-end (REQ-CHN-310). This conflicts with the modular-monolith single deployable unless BFFs are controller areas inside the monolith. Front ends can still deploy as static assets; "independently of owner modules" cannot be literal in one deployable. Needs an interpretation ruling.
4. **API gateway / API management.** AC-10 says vendor API-management products are forbidden (CD-20) and quotas are "capabilities of the custom gateway". REQ-CHN-228 validates "at the edge and in CHN"; REQ-CHN-240 revocation "effective within 60 seconds at the edge". INFRASTRUCTURE.md adds Azure Front Door Premium with WAF for PRD-12. Front Door cannot do per-client OAuth quota enforcement or JWT revocation. In practice this means ASP.NET Core rate-limiting middleware plus a revocation cache inside the monolith (state shared across ≥ 2 replicas, so PostgreSQL-backed or a distributed counter). Must be designed. No Redis is in the stack.
5. **Identity platform fit (Entra External ID). This is the main External ID risk; I am inferring product behaviour here and it needs verification.** The PRD assumes OAuth features that Entra may not provide natively:
   - RFC 8693 token exchange (`plt.Token.exchange`, REQ-CHN-263). Entra offers On-Behalf-Of, which is not RFC 8693 subject/actor delegation with an `act` claim.
   - Pushed authorisation requests and FAPI 2.0 (REQ-CHN-226, Should).
   - mTLS sender-constrained (certificate-bound) tokens (REQ-CHN-224, NFR-CHN-008).
   - MCP Client ID Metadata Documents and Protected Resource Metadata (REQ-CHN-262, -264).
   - Passkey-first sign-in for the external realm (REQ-CHN-060, -063).
   - Bank SAML federation (§9.3).
   - Per-scope consent screens naming the agent and tools (REQ-CHN-265).
   - Per-partner hosted sign-in co-branding (REQ-CHN-212, REQ-PLT-074).

   PRD-12 delegates all of these to PLT, but CHN's Must requirements (REQ-CHN-262…266) depend on them. **Decision needed:** what PLT can deliver on External ID, and what CHN or PLT must build as a custom token service. The ADR says "we never build our own identity system".
6. **"Workflow" calls.** §9.2 lists `plt.Workflow.*` (REQ-PLT-007). The ADR forbids workflow servers. It is probably a Hangfire/state-machine abstraction in PLT, but confirm.
7. **RPO/RTO mismatch.** NFR-CHN-005 requires RPO 0 for confirmed withdrawal declarations "through synchronous replication". NFR-CHN-006 requires RPO ≤ 5 min and RTO ≤ 2 h for T1 channel data. NFR-CHN-004 requires 99.9 % for T1. INFRASTRUCTURE.md §8 production target is **RPO 15 min, RTO 4 h** (zone-redundant HA). Zone-redundant HA replicates synchronously within the region, but the documented targets disagree. Needs reconciliation.
8. **Edge static caching / EU-only assets** (NFR-CHN-001, REQ-CHN-293): Front Door is global. EU data residency for analytics/session stores is fine, but CDN edge locations need a statement.
9. **Event-name drift is minor.** "Topic", "partition" and "microservices" (the last only in the ACORD quote, §3.2) are wording, not design. There is no Kafka, BPM, lakehouse or low-code mention. Vendor products named (Socotra, Guidewire, Duck Creek, Nationwide) are reference only (CD-20).

### (b) With the system contract / other PRDs

1. **REQ-POL-308 refund "less cost of cover"** vs Law 5317/2026 Art. 72 full refund. CCR-CHN-03 was accepted, but POL/BIL must actually implement `PolicyLifecycleRules` full refund for Greece (RK-CHN-04). Check that PRD-05 and PRD-06 were updated.
2. **PRD-05 OI-POL-05** says Greece has not transposed the directive; PRD-12 AC-1 says that is superseded. Verify the PRD-05 digest.
3. **Wallet legal basis:** the prompt and PRD-17 attributed B 2461/2025 to insurers; PRD-12 corrects this to 25061 ΕΞ 2024 (AC-3, CCR-CHN-04). Check that the MKT `MotorDataProvider` binding (REQ-MKT-316) carries `legalBasisRef` and `channelsSupported`.
4. **Contract §3.8.3 item numbering** (CCR-CHN-06; AC-11). PRD-12 cites "item 5" as "AI never bypasses controls" but also "item 6" for staff-monitoring scores (REQ-CHN-195), and the CCR text proposes the agent rule becomes item 6. Citations may point at the wrong items.
5. **Idempotency retention.** Contract minimum 7 days (REQ-CHN-218) vs BR-CHN-040, where CHN keeps 30 days for partner commands. This is consistent but has two values, so implement 30.
6. **Notification entity rename** CON-029 (`CustomerNotification`; WRK owns `StaffNotification`). Check that WRK matches.
7. **CLM consumer lists:** PRD-12 asks CLM to drop CHN from the `StatutoryOfferDue/Breached` consumers (§8.2).
8. **CCR-CHN-01 consumer lists** were aligned in v1.4 to DAT-only for governance events (XMR-CR-CHN-03). Other PRDs may still list WRK, PLT, BIL or CMP as consumers.

### (c) Internal contradictions / inconsistencies

1. **Customer assistant priority.** §1.5 says "Customer assistant (AI) — Could, off by default" for P1, yet REQ-CHN-283, -284, -285, -286, -287 and -289 are **Must P1**, and no requirement says "build the assistant" at Could. The Musts are conditional on the assistant existing. Clarify whether the assistant panel (SCR-CHN-15) is in the MVP.
2. **MCP facade "off by default" but all Must P1.** BR-CHN-056 makes the facade off at tenant level, while about 23 Must P1 requirements build it. §16.5 decision 6 (first-party only vs third parties at go-live) is still open. That is significant MVP scope for a switched-off feature.
3. **Webhook suspension threshold.** REQ-CHN-234 says "a configurable failure count", BR-CHN-043 says "50 consecutive failures". This is compatible as a default, but note it.
4. **Withdrawal for bank channel.** The default matrix (§10.1.3) has WITHDRAW = REFER for Bank, while REQ-CHN-058 makes WITHDRAW final SELF_SERVICE only for WEB_DIRECT/APP. It is not a contradiction, but bank customers get no self-service on that channel.
5. **REQ-CHN-064 vs AI-agent withdrawal.** Withdrawal never needs step-up, but REQ-CHN-269 lists "withdrawal on behalf" among ticket actions needing step-up, while the matrix makes AI_AGENT WITHDRAW = REFER ("human uses the function"). This is consistent in effect but confusing.
6. **AI-CHN-02 users include ROLE-03** (staff), and REQ-CHN-280 (staff agents) is Should, while the `search` tool is "staff agents only" in the P1 catalogue.
7. **Bank items still in Must families?** REQ-CHN-006 has no family (D6). REQ-CHN-042 is Must but mentions partner co-brands "when REQ-CHN-212 is delivered", which is conditional and fine. NFR-CHN-010 and the language switch include the bank shell "when delivered". This is consistent.
8. **REQ-CHN-162** (phone-concluded contracts get the function) is Should, and §16.5 decision 3 is still open, so scope is unclear.

### (d) Cannot be built without a decision

- Mobile app technology and push provider (see (a)2).
- Identity capabilities on Entra External ID for token exchange, PAR/FAPI, mTLS-bound tokens, MCP metadata/CIMD and passkeys (see (a)5).
- Rate limiting / quota / revocation implementation without API management or Redis (see (a)4).
- Go-live default matrix values and limits (BR-CHN-005/006/007; §10.1.3 is a "proposal"; OI-CHN-15).
- Aggregator selection and its schema mapping (OI-CHN-14); the bank partner (D6).
- Wallet onboarding and legal basis (OI-CHN-01). The feature is Should, but receipts and fallback are Must.
- Card acquirer / instant-payment provider ("To be selected (BIL)", §9.3). This blocks J-01 payment.
- ACORD NGDS licensing (OI-CHN-02). The mapping is Should, so the external schema can start without it.
- Retention periods (OI-CHN-13).
- MCP facade go-live policy (§16.5 #6).

---

## 15. Build notes

**Hardest parts**
1. **The transaction-permission matrix engine** (REQ-CHN-045…058). It covers most-specific-match resolution, deny-by-default, limits evaluated on owner dry-run results, versioning with four-eyes, final EU/country entries, a scenario harness (≥ 400 scenarios at go-live, §14.x) and a property test that no FORBIDDEN or REFER outcome ever reaches an owner command. Every journey, API and tool sits on it.
2. **External OAuth/identity surface on External ID** (see conflict (a)5). This covers delegated tokens with actor claims, the MCP resource-server metadata, sender-constrained partner credentials and ≤ 60 s revocation.
3. **The withdrawal function** with zero-loss durability (RPO 0), an acknowledgement within 1 minute that is independent of POL (direct DOC fallback, de-duplication on request number), confirmation-time semantics around 23:59:59 Europe/Athens including DST, public (no sign-in) access, no trackers, and no automatic rejection.
4. **Partner platform as a product.** This covers the developer portal (3 screens), onboarding with four-eyes, the sandbox with deterministic scenarios and time-travel, signed webhooks with retry, suspension, 30-day replay and per-aggregate ordering, quotas and bulkheads per partner, and OpenAPI-from-enforced-schema contract tests.
5. **Describe-driven dynamic forms** (REQ-CHN-031) and an R-101 language switch that re-renders in place within 300 ms p95 without losing input. Owner error texts already on screen must be re-rendered from their codes, with a completeness gate in CI (REQ-CHN-320, -325, NFR-CHN-019).
6. **Breadth of composition.** CHN calls roughly 13 modules and consumes about 70 event types into 5 read models, with "updating" stale states (REQ-CHN-306).

**Must exist first**
- PLT: external realm, token exchange, `plt.Time.now`, `plt.Number.next`, audit, approvals, feature flags, AI control plane (A-1).
- MKT: capability switches, L10n bundles/format, the R-101 language setting and completeness gate, the channel code list (REQ-MKT-001), and `MotorDataProvider`.
- PFC: `describe` and availability.
- POL: submission/quote/bind/change/withdrawal, dry-run, `EffectiveDate.limits`, the disclosure gate with `DisclosureDeliveryView`.
- RAT: breakdown. UW: `Issue.listForChannel`, referrals.
- BIL: hosted payment, down payment, billing preview.
- DOC: IPID, proof of cover, `DT-WITHDRAWAL-ACK`, vault listing.
- WRK: `Request.submit`, inbound documents.
- PTY: party, consent, producer codes, agency users.
- CLM: FNOL and tracking.

**Suggested slicing (motor MVP)**
1. **Foundation:**
   - Channel back-end skeleton, channel code list (REQ-CHN-317), message catalogue (-038), trace and business keys (-037).
   - Session and interaction store (-034, -035), config service (-042), language switch R-101 (-033, -319…325).
   - Matrix engine and editor with scenario harness (-045…058, SCR-CHN-30).
2. **Identity journeys** on External ID (REQ-CHN-060…072, SCR-CHN-14).
3. **Direct motor quote-and-buy, manual path first:**
   - J-01 (REQ-CHN-080…106, -316; SCR-CHN-01/02).
   - Hosted payment via BIL; disclosure receipts.
   - Wallet pre-fill later as a Should increment (SCR-CHN-03).
4. **Withdrawal function** (REQ-CHN-160…173, SCR-CHN-13). It is statutory, T1 and largely independent, so build it early and test it hard.
5. **Customer servicing:**
   - Dashboard and policy detail (SCR-CHN-04/05).
   - Self-service changes with refer (SCR-CHN-06/25).
   - Billing (SCR-CHN-07), vault (SCR-CHN-08).
   - Claims FNOL/tracking (SCR-CHN-09/10).
   - Messages and notifications (SCR-CHN-11), preference centre (SCR-CHN-12).
6. **Broker/agent portal:**
   - SCR-CHN-16…25: quote workspace, issues and referrals, servicing, book, commissions and account current.
   - Agency administration.
7. **Partner API platform:**
   - Motor quote and bind, servicing, claims, billing, webhooks (REQ-CHN-215…246).
   - Developer portal (SCR-CHN-27…29), sandbox.
   - Aggregator intake and deep links (Should, REQ-CHN-247…256).
8. **MCP facade:**
   - Read and dry-run tools first, then tickets for state changes.
   - Logging, governance view (SCR-CHN-31), adversarial suite (REQ-CHN-260…282, -318).
   - Gated by §16.5 decision 6. Ships off by default.
9. **Fast-follows:**
   - Bank journeys (D6: REQ-CHN-006, -206…214, SCR-CHN-26).
   - Customer assistant (AI-CHN-01), other AI features.
   - Home (P2), commercial (P3).
