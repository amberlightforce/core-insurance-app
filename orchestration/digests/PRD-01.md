# Digest — PRD-01 Party, customer and distribution (PTY)

Source: `core-insurance-prds/PRD-01-party-customer-distribution.md` v1.4 (2026-10-07, "Candidate for build baseline 1.0"), read in full (lines 1–2397). Binding input stated: 00-system-contract.md v1.11. Cross-checked against `core-insurance-infra/ARCHITECTURE-DECISIONS.md`. Section numbers below are PRD sections (§).

---

## 1. Identity

- **Module code:** PTY. **Title:** Party, customer and distribution.
- **Purpose (§1.2):** System of record for *who* the insurer deals with and *through whom* it distributes. Owns persons/organisations independent of role; typed, verified identifiers; Greek + Latin names and addresses; contact points; accounts and households (Account is PTY-owned per contract CD-04); relationships and life events; consent and communication preferences; vulnerable-customer and trusted-contact data; the single sanctions screening engine (CD-12); data quality (duplicates, merge/unmerge); intermediaries with register, licence, CPD and PI data; producer codes and hierarchy; producer of record (PoR) per policy term; commission agreements as versioned data (CD-14); delegated agency administration; bancassurance configuration. "Every other business module depends on it."
- **Five key decisions (§1.2):** (1) Party ≠ role ≠ account; (2) identifiers and verification as data via `IdValidator`/`RegistryLookup`/VIES, registry results only as approve-the-diff suggestions; (3) bilingual names + cross-script search, core Unicode-generic only, Greek rules from MKT/pack (XMR-D-205); (4) one sanctions engine, fail-closed for money; AML due diligence is an optional switch because Greek non-life insurers are not AML obliged entities (Law 4557/2018 Art. 3 point 3); (5) distribution as effective-dated data (categories from pack; PoR changes without policy transaction; immutable agreement versions under maker-checker).
- **Out of scope (§1.4)** (owner → interface): policies/terms/jobs/PolicyParty/PolicyDriver (POL); commission calculation/statements/payment runs/account current (BIL, CD-14); commission postings + myDATA commission documents (FIN, CMP); disbursement execution, payee bank details, verification of payee (BIL, CD-13, R-38 — PTY holds only a pointer, REQ-PTY-278); claim-specific roles beyond master (CLM); document rendering/delivery/archive (DOC); inbound document capture/classification (WRK); identities/authentication of all users (PLT; PTY holds distribution context of external users); global search/recent items/command palette (WRK, CD-18); DSAR orchestration (CMP); product availability by producer (PFC); pricing household/multi-policy discounts (RAT); portal UI for consents and broker self-admin (CHN).

## 2. Size metrics

From §16.6 self-check (verified against tables while reading):

| Metric | Count |
|---|---|
| Functional requirements | **266** = 13 anchors (REQ-PTY-001…013) + 253 module reqs (REQ-PTY-030…282) |
| Must | 216 (**215 P1**, 1 P3 = REQ-PTY-277) |
| Should | 39 (33 P1, 1 P2, 5 P3) |
| Could | 11 (1 P1, 6 P2, 2 P3, 2 P4) |
| Tags | 229 [BASELINE], 37 [ENHANCEMENT] |
| Business rules | 46 (BR-PTY-001…046) |
| NFRs | 20 (NFR-PTY-001…020) |
| Screens | 23 (SCR-PTY-01…23) |
| Owned entities | ~40 (see §3 below) |
| Events produced | 26 (15 contract catalogue §8.1 + 11 added by R-15 §8.2) |
| Events consumed | ~35 event types in 18 rows (§8.3) |
| API operation groups (inbound) | ~32 rows in §9.1 (pty.* namespace, REST under `/api/pty/v1/...`) |
| Outbound call groups | 20 rows (§9.2) |
| AI features | 8 (AI-PTY-01…08), all off by default |
| Open issues | 12 (10 open, 2 closed) |
| Risks | 7 (RK-PTY-01…07) |
| CCRs | 6, all Accepted |

- **Motor MVP (P1) Must count: 215.** All P1 requirements = 215 Must + 33 Should + 1 Could = 249. Later phases: P2 = 7 (1 Should + 6 Could); P3 = 8 (1 Must, 5 Should, 2 Could); P4 = 2 Could.
- Bancassurance Musts were demoted to Should P1 (REQ-PTY-250, -253, -254, -255, -257; -258 already Should) per D6; REQ-PTY-096 (vendor master) promoted to Must P1.
- **Build-size estimate: L.** 215 Must P1 is >2x the L threshold; plus heavy temporal logic (bitemporal party data, effective-dated hierarchy/PoR/agreements), reversible merge/unmerge with property-based invariants, a full sanctions engine with list ingestion and fail-closed payment gating, field-level encryption with blind indexes, cross-script fuzzy search at 1.5M parties ≤1 s p95, and 23 screens.

## 3. Owned entities (§7.1, §7.3)

Common columns (§7, REQ-PTY-035): `legal_entity_id`, `jurisdiction`, `created_at`, `created_by`, `record_version`; `valid_from`/`valid_to` half-open `[ )` where business validity; bitemporal entities add `recorded_from`/`recorded_to` (XMR-FR-150, D5). Internal ids UUIDv7. Data classes P0–P3. Retention classes (R-19/CCR-PTY-06): RC-PTY-CUSTOMER, RC-PTY-PROSPECT, RC-PTY-INTERMEDIARY, RC-PTY-SCREENING, RC-PTY-CONSENT, RC-PTY-AUDIT.

| Entity | Temporal | Key attributes | State machine |
|---|---|---|---|
| **Party** | bitemporal | party_id, party_number (unique per LE, PLT numbering, no PII encoded), party_type Person/Organisation (immutable), status, merged_into_party_id, preferred_language, birth_date (P2), birth_place, sex (only if pack requires), marital_status, occupation, date_of_death/source/evidence, legal_form_code, registration/dissolution dates, activity_codes (KAD), website, preferred_payment_instruments (map purpose → BIL PayeeAccount id, pointer only), restriction_reason/at, source_channel | See below |
| **PartyName** | bitemporal | name_kind Legal/Trade/Former/Alias; form Native/LatinGenerated/LatinAsOnDocument; script (ISO 15924); given/family/father/mother names; organisation_name; normalised_key, cross_script_key (trigram-indexed); transliterator_version, manually_overridden, source_document_ref | — |
| **PartyIdentifier** | bitemporal | scheme (AFM, DOY, GEMI, LEI, VAT, PASSPORT, NATIONAL_ID, RESIDENCE_PERMIT, DRIVING_LICENCE, TIC, EIK…), value_encrypted (P2), value_blind_index (unique per LE when scheme unique), display_suffix, issuing_country, issue/expiry, attributes JSON (DOY for AFM; licence categories, first-issue date), verification_status, source, verified_at/by, evidence_ref, validator_version | SelfDeclared → DocumentVerified / RegistryVerified; DocumentVerified → Expired; any → VerificationFailed; value change → SelfDeclared (REQ-PTY-053) |
| **Address** | bitemporal | types set {Legal, Mailing, Risk, Garaging, Billing, Business}, primary_for_types, country, structured fields (street, number, building, floor, unit, postcode, locality, municipality, regional_unit, region) + free_lines[3], latin_form, validation_state Validated/Unvalidated/Invalid, description, geocode lat/lon/precision/geocoder_version (P2), undeliverable(+since) | — |
| **ContactPoint** | bitemporal | type Mobile/Landline/Work/Fax/Email/SecureInbox, value (E.164 / lower-case email), purpose Personal/Work, is_primary per type, verification_status Unverified/Verified/Bouncing, source | Unverified → Verified; → Bouncing after threshold hard bounces |
| **OrganisationRevenue** | bitemporal | fiscal year start/end, amount, source (Declaration, FinancialStatements, Registry, Intermediary), evidence, verification_status (REQ-PTY-277, P3) | — |
| **ReinsurerRating** | bitemporal | agency, rating, outlook, rating_date, source, evidence (REQ-PTY-097) | — |
| **PartyRole** | bitemporal | role_type (config), context_type/context_ref, role_attributes JSON | valid period only |
| **PartyRelationship** | bitemporal | from/to party, type (reciprocal labels), ownership_percentage decimal(5,2), source, evidence | — |
| **Account** | valid-time | account_number, account_type Personal/Commercial, status, holder_party_id, nickname, description, preferred_language, default_producer_codes (line → code), servicing_channel Intermediary/Direct/Bank, merged_into_account_id | Pending → Active (holder confirmed and screened Clear/FalsePositive, or PotentialHit with bind blocked); Active → Withdrawn (no policies, no open jobs); Active → Merged; Merged → Active (reversal within window); Active → Closed (all terms ended > configurable period) |
| **AccountMember** | valid-time | member_role (Account holder, Household member, Additional contact, Payer contact, Driver), active, household flag | — |
| **AccountLocation** | valid-time | location_number (unique per account), code, name, non_specific, address_id (nullable if non-specific), phone, employees_count, active, primary | — |
| **RelatedAccount** | — | relation_type SplitFrom/SameGroup/Household, valid period | — |
| **LifeEvent** | — | event_type, effective_date, evidence_refs, steps JSON, reversal_window_end, actor | Draft → InProgress → Completed → Reversed (within window) |
| **MergeRecord** (immutable) | — | kind PartyMerge/AccountMerge, survivor, merged_ids, before_images (encrypted), attribute_decisions, repoint_map, unmerge_window_end, maker, checker | Executed → Unmerged / WindowClosed |
| **MatchCandidate** | — | party_ids, score, classification Match/Possible, matched_attributes, model_version, decision Pending/Merged/NotDuplicate, decider, reason | — |
| **DataQualityIssue** | — | rule_code, entity, severity, opened/closed, assignee | — |
| **ProcessingPurpose** (config) | — | purpose_code, GR/EN description, lawful_basis (Art. 6 point), art9_condition, data_categories, retention_class | — |
| **Consent** | bitemporal | purpose_code, channel Post/Email/SMS/Phone/Portal/All, kind (MarketingConsent, DurableMediumChoice, WebsiteConsent, TrustedContactConsent, VulnerabilityConsent, ProfilingConsent), state Given/Withdrawn/NotAsked, lawful_basis, source, collector_ref, wording_version, proof_ref (required except NotAsked; NO_PROOF flag for migrated), given_at/withdrawn_at | Given ↔ Withdrawn |
| **CommunicationPreference** | bitemporal | party or account, purpose (PolicyDocuments, BillingNotices, ClaimsUpdates, Servicing, Marketing), channel, language, delivery_medium Paper/DurableMedium/Website, paper_copy_requested, consent_id | — |
| **TrustedContact** | valid-time | contact_party_id, relationship_id, purposes, consent_id | — |
| **VulnerabilityIndicator** | valid-time | category (P3 if health), handling_rule_codes, source, lawful basis/consent, review_date, recorded_by — **P3** | — |
| **SanctionsList / ListVersion / ListEntry** | — | list_code, provider, version_id, published/ingested, checksum, status Validated/Active/Rejected/Superseded; entries: names, aliases, birth dates, nationalities, identifiers, programme, listed_on | Validated → Active → Superseded; Rejected |
| **ScreeningResult** (case) | — | case_number (null when Clear), party_id or adhoc_subject_hash (encrypted snapshot), trigger (Onboarding, Change, ListUpdate, Periodic, Bind, Refund, ClaimPayment, Import), caller_ref, list_versions, matches, status, payment_block, reviewer/approver/decided_at/reason/rationale, input_snapshot_hash | See below |
| **SuppressionRule** | — | party × list_entry × entry_version_hash × screened_attributes_hash, expiry, approved_by | — |
| **Intermediary** | valid-time | intermediary_code, category (pack), status, register fields (register_name, chamber, register_number, registration_category/date, register_status, verified_at, source, verification_link), acts_for, gives_advice, holdings (>10 %), home_member_state/register_ref, coordinator_intermediary_id, tier | Onboarding → Active (guards: register Active, appointment, approved agreement, screening Clear/FP, checklist); Active ↔ Suspended; Active/Suspended → Terminated (book choice made) |
| **IntermediaryPerson** | — | person_party_id, role ResponsiblePerson/DistributionStaff, knowledge_certificate_date, level, listed_in_register | — |
| **Licence** | valid-time | kind Registration/PIcover, status, insurer, policy_number, per_claim/aggregate cover, deductible | — |
| **CpdRecord / CpdCycle** | — | course, provider, hours (decimal), completed_on, certificate; cycle start/end, required_hours (pack), completed_hours, status | Compliant ↔ AtRisk → NonCompliant → Compliant (late hours, if pack allows) |
| **HierarchyNode** | valid-time | node_type Firm/Branch/Producer/Bank/BankBranch, parent (at most one per date), branch_code | — |
| **ProducerCode** | valid-time | code (unique), node_id, status, authorities (collect_premium, issue_cover_notes, bind, bind_limits JSON, service_only) | Active ↔ Suspended → Terminated (guard: book transfer chosen) |
| **Appointment** | valid-time | product_or_line, territory, channel, agreement_id | — |
| **ProducerOfRecord** | bitemporal | policy_term_ref (POL id), policy_ref, producer_code_id, valid period, reason, commission_consequence (FromEffectiveDate, AtRenewal, Split, CorrectionFromStart), split_share, mandate_evidence_ref, transfer_id, approver | No overlap / no gap per term (property test) |
| **BookTransfer** | — | source codes, selection, target mapping, mode Now/AtRenewal, consequence, effective_date, counts, per-term outcomes | Draft → PendingApproval → Approved → Running → Completed / CompletedWithErrors; not-started → Cancelled |
| **CommissionAgreement** | — | number, name, intermediary or group node, LE, currency, template_ref, start/end | — |
| **CommissionAgreementVersion** (immutable after approval) | — | version_number, effective_from, rate_lines (product_or_line, channel, transaction_type, tier, basis, value), commissionable_charge_types, overrides, splits, contingent_terms, chargeback_rule, settlement (billing_mode, frequency, document_issuer, vat_treatment_code), recalculation_required, maker, checker | Draft → PendingApproval → Approved → Active → Superseded; PendingApproval → Draft (rejected); Draft → Withdrawn. Governance stage alias (CR-S3-25): Authoring / Under review / Approved-not-live / Live / Replaced / Withdrawn |
| **AgreementAssignment** | valid-time | agreement → producer code or node | — |
| **ExternalUserLink** | valid-time | plt_user_id, intermediary, branch node, person_party_id, agency_role, status, last_attested_at | — |
| **ProducerGrant** | valid-time | plt_user_id, producer_code_id | — |
| **BancassurancePartnership** | valid-time | partner party, intermediary, agreement, products, channels, exclusivity_rules, servicing_model, end_book_option | — |

**Party state machine (§7.3):** create minimal → Prospect; create with role → Active. Prospect → Active (first role); Prospect → Anonymised (prospect retention). Active → Inactive (guard: no Scheduled/InForce term, open claim or job; REQ-PTY-033 refusal code ACTIVE_CONTRACTS); Inactive → Active. Active → Deceased (death verified); Active → Dissolved (organisation). Deceased → Active (life event reversed). Active/Inactive → Merged; Merged → Active (unmerge). Active/Inactive → Restricted; Restricted → Active (DPO lifts). Inactive/Restricted/Deceased → Anonymised (retention ends). Events: PartyCreated; PartyUpdated (STATUS) on every transition; PartiesMerged/PartyUnmerged.

**Screening case state machine (§7.3):** Clear (below threshold); PotentialHit_New → UnderReview → Escalated; UnderReview → PendingFalsePositive (maker) → FalsePositive (second analyst) or back to UnderReview (rejected); Escalated → TrueMatch (compliance officer) or → PendingFalsePositive; direct → TrueMatch on exact list-identifier match (Blocked); FalsePositive → PotentialHit_New if list entry or party attributes change. PendingFalsePositive keeps payment block. Response mapping (CCR-PTY-03/R-16): Blocked = TrueMatch, exact list-identifier match, or degraded mode on payment screens; PotentialHit = any open case; Clear = Clear or FalsePositive.

## 4. Consumed entities / dependencies (§7.2, §9.2, §15.1)

| From | What | Via |
|---|---|---|
| POL | Policy, PolicyTerm, Job (PolicyTermView, JobView read models built from POL events); guards for location removal, member removal, withdraw, moves | events (§8.3) + `pol.Policy.get`, `pol.Term.get`, `pol.Job.list` (REQ-POL-002); `pol.Rewrite.create` (REQ-POL-001); policy search delegated to REQ-POL-014 |
| POL | PolicyParty, PolicyDriver (context links; driver policy attributes) | REQ-POL-010 |
| CLM | Claim tracking (ClaimView), claim roles | `clm.ClaimTracking.list` (REQ-CLM-009); `ClaimReported` |
| BIL | BillingAccountView (billing tiles, merge preview); payee instrument validation | `bil.BillingAccount.get` (REQ-BIL-001); `bil.PayeeAccount.get` (REQ-BIL-345); BIL events |
| PFC | Charge-type catalogue (commissionable base); product/line codes; availability rules; question sets | `pfc.ChargeType.list` (REQ-PFC-004), REQ-PFC-011, REQ-PFC-006 |
| WRK | Activities, queues, notes, inbound documents, notifications, SLA, global search feed | `wrk.Activity.*`, `wrk.Notification.*` (REQ-WRK-001/003/004/005/006/007/008/009) |
| DOC | Evidence archive; document requests; delivery events | `doc.Archive.store` (REQ-DOC-004), `doc.Document.request` (DT-INTERMEDIARY-CHANGE, DT-GENERAL-LETTER, REQ-DOC-001), DocumentDelivered/DeliveryFailed |
| PLT | RBAC/ABAC, audit, authority types, maker-checker, events infra, adapter host, workflow, AI control plane, retention engine, observability, numbering, external identity | REQ-PLT-001…015 (`plt.Number.next`, `plt.Audit.append`, `plt.Approval.*`, `plt.Authority.check`, `plt.Workflow.start/signal`, `plt.ExternalUser.invite`, `plt.Retention.*`, `plt.Ai.invoke`, `plt.AiToggle.resolve`, `plt.Adapter.invoke`) |
| MKT | Configuration (code lists, thresholds), SPI bindings, capability switch (AML), localisation, Cyprus stub pack | `mkt.Configuration.resolve` (REQ-MKT-001), `mkt.Spi.catalogue` (REQ-MKT-002), REQ-MKT-004, REQ-MKT-005, REQ-MKT-008; language rules REQ-MKT-178; transliteration variants REQ-MKT-091 |
| CMP | DSAR requests; AI system register; complaints | `DSARReceived`, `ComplaintReceived/Answered`; `cmp.AiSystem.get` (REQ-CMP-007) |
| CHN | Email verification message; channel code list and channel grouping | REQ-CHN-009; REQ-CHN-317 (R-84, R-99) |
| DAT | Impact simulation data (REQ-DAT-001); model registry (REQ-DAT-005); drift/bias events | — |
| MIG | `MigrationBatchLoaded` | event |

## 5. Events

Logical topic `pty.events.v1`; partition keys per R-100 (party_id; PartiesMerged and PartyUnmerged both keyed on survivor; account_id; intermediary_id; policy_term_ref; agreement/transfer/list ids). Catalogue of record is PRD-18 §8 (D5). **No P2/P3 values in payloads** (REQ-PTY-042, NFR-PTY-011). Consumer lists authoritative per R-87.

### 5.1 Produced — contract catalogue (§8.1)

| Event | Trigger | Key payload | Consumers |
|---|---|---|---|
| PartyCreated | creation (any channel, import) | party_id, number, type, status, roles, origin | DAT |
| PartyUpdated | attribute-group change or status transition | party_id, number, changed groups (IDENTITY, NAME, IDENTIFIERS, ADDRESSES, CONTACT_POINTS, PREFERENCES, CONSENTS, STATUS, VULNERABILITY, ROLES, RELATIONSHIPS), new status, `backdated`, `effective_from` (R-04) | BIL, CHN, CLM, CMP, DAT, DOC, MIG, POL, RI, WRK |
| PartiesMerged | merge executed | survivor, merged ids, re-point map, window end, merge_id | BIL, CHN, CLM, CMP, DAT, DOC, MIG, PLT, POL, RI, UW, WRK |
| PartyUnmerged | unmerge | merge_id, survivor, restored ids, reverse re-point map | same minus CHN |
| IdentifierVerified | verification status change | party_id, scheme, status, source, verified_at (no value) | CMP, DAT, UW |
| AccountCreated | account created | id, number, type, holder | DAT, WRK |
| AccountMerged | merge executed or reversed | source/target, policy ids, reversal flag | BIL, DAT, POL, WRK |
| PolicyMoveRequested | move/split approved | request id, policy ids, source/target, effective date | BIL, DAT, POL, WRK |
| ConsentChanged | given/withdrawn/objection | party, purpose, channel, kind, state, proof ref | CHN, CLM, CMP, DAT, DOC |
| CommunicationPreferenceChanged | preference change | party, purpose, channel, language, medium | BIL, CHN, CLM, DAT, DOC |
| SanctionsHitRaised | case → PotentialHit or TrueMatch | party (or ad-hoc hash), case no., status, block flag, caller refs | BIL, CLM, DAT, POL, RI, UW, WRK |
| SanctionsHitCleared | FalsePositive approved | party, case no., block=false | same |
| ProducerOfRecordChanged | PoR row by change/transfer | term ref, policy ref, old/new code, effective date, reason, consequence, split, transfer id | BIL, CHN, DAT, PLT, POL, WRK (DOC explicitly not) |
| IntermediaryLicenceChanged | register/authorisation/licence/PI/CPD status change | intermediary, status kind, status, expiry | BIL, CHN, DAT, PLT, POL, UW |
| CommissionAgreementVersioned | version approved | agreement id/number, version, effective from, recalculation flag | BIL, DAT |

### 5.2 Produced — added by R-15 (§8.2)

PartyRoleChanged (DAT, RI); PartyRelationshipChanged (DAT); LifeEventRecorded (party, type, effective date, reversed flag → BIL, DAT; POL/CLM get WRK activities instead, REQ-PTY-122); VulnerabilityStatusChanged (flag + rule codes → BIL, CLM, DAT); AccountStatusChanged (DAT, POL); IntermediaryStatusChanged (BIL, DAT); ProducerCodeChanged (BIL, DAT); AppointmentChanged (DAT); BookTransferCompleted (CHN, DAT); ExternalUserAccessChanged (CHN); SanctionsListActivated (DAT).

All events from migration carry `origin=MIGRATION` (REQ-PTY-013); import reversal re-uses existing events with reversal batch id, no new type (REQ-PTY-282).

### 5.3 Consumed (§8.3) — all idempotent on event_id

| Event(s) | From | Reaction |
|---|---|---|
| PolicyBound, PolicyIssued | POL | create roles (policyholder, insured, payer, driver), create PoR row, update PolicyTermView and account measures (upsert on term, role, party) |
| PolicyChanged | POL | update roles (drivers) and PolicyTermView |
| PolicyCancelled, PolicyReinstated, PolicyRewritten, PolicyVoided | POL | end/restore roles; rewrite updates account policy list |
| RenewalBound, PolicyNonRenewed, PolicyLapsed | POL | PoR for new term (applies pending at-renewal transfer); measures |
| SubmissionCreated, QuoteIssued | POL | JobView |
| ClaimReported, ClaimClosed, ClaimReopened | CLM | claim roles; ClaimView |
| InvoiceIssued, PaymentReceived, WriteOffPosted, DelinquencyStarted, CancellationForNonPaymentRequested | BIL | BillingAccountView; delinquency and non-pay counts |
| DocumentDelivered, DeliveryFailed | DOC | bounce/undeliverable handling (REQ-PTY-080, -084) |
| DocumentClassified, DocumentLinked | WRK | offer document verification task |
| DSARReceived | CMP | export / erasure evaluation / restriction; one execution per request id |
| ComplaintReceived / ComplaintAnswered | CMP | show/clear info on party 360; vulnerability review prompt |
| ActivityCompleted | WRK | close steward/screening tasks |
| ConfigChanged, ConfigurationActivated, PackActivated, PackRolledBack | PLT, MKT | reload thresholds, code lists, SPI bindings |
| AiToggleChanged, AiKillSwitchActivated | PLT | enable/disable AI-PTY features (≤60 s) |
| ModelDriftDetected, BiasThresholdBreached | DAT | auto-disable/flag AI feature |
| RetentionPurgeCompleted | PLT | reconcile anonymised parties |
| MigrationBatchLoaded | MIG | batch duplicate detection + bulk screening |

## 6. APIs

**Exposed (§9.1)** — all commands require `Idempotency-Key`, typed preconditions, RFC 9457 errors with `PTY-ERR-*` codes (REQ-PTY-273); external REST `/api/pty/v1/...`:

- `pty.Party.search / get (validAt, knownAt, profile) / create / update / validate / merge / unmerge`
- `pty.Identifier.verify` (registry/VIES; PTY-ERR-REGISTRY-UNAVAILABLE retryable)
- `pty.PartyRole.assign / end / query` (PTY-ERR-ROLE-DATA-MISSING structured)
- `pty.Relationship.add / end / query`
- `pty.Account.create / get / search / setHolder / addMember / removeMember / withdraw`; `pty.Account.merge / requestPolicyMove / split` (dry-run)
- `pty.Location.add / update / remove`
- `pty.LifeEvent.start / complete / reverse` (effects-preview dry-run)
- `pty.Consent.query / record / withdraw`; `pty.CommunicationPreference.resolve / set`; `pty.Vulnerability.query / record / remove`
- `pty.Screening.screen` (sync), `pty.Screening.bulk / caseDecide / caseGet`
- `pty.ProducerCode.validate / search`; `pty.Intermediary.create / update / suspend / terminate / importRegister`; `pty.Appointment.*`, `pty.ProducerCode.*`
- `pty.ProducerOfRecord.get / change / bulkTransfer / cancel`
- `pty.CommissionAgreement.resolve / get / createVersion / submit / approve / compare / simulate`
- `pty.AgencyUser.*`; `pty.Bancassurance.*`
- `pty.Dsar.export / evaluateErasure / restrict / liftRestriction`
- `pty.Import.*` (parties, accounts, intermediaries, producer codes, PoR history, agreements; dry-run) and `pty.Import.reverse` (REQ-PTY-282)
- Dry-run mandatory for: merge, unmerge, account merge, policy move, PoR change, bulk transfer, agreement version approval, import (REQ-PTY-273).
- Named error codes seen: PTY-ERR-ID-CHECKDIGIT, -STALE, -DUPLICATE-IDENTIFIER, -MERGED, -MERGE-CROSS-ENTITY, -WINDOW-CLOSED, -OVERLAP, -ACCOUNT-HAS-JOBS, -MEMBER-IN-USE, -OPEN-JOB, -LOCATION-IN-USE, -LAST-LEGAL-ADDRESS, -EVIDENCE-REQUIRED, -PROOF-REQUIRED, -NO-EMAIL, -SCREEN-INPUT, -SELF-APPROVAL, -ACTIVATION-INCOMPLETE, -MANDATE-MISSING, -RATE-ABOVE-MAX, -BACKDATE, -SPLIT-SUM, -OUT-OF-HIERARCHY, -BRANCH-FILE, -NOT-FOUND, -LIVE-DEPENDANT, -NOT-MIGRATED, -IDEMPOTENCY-MISMATCH (409), -POSTCODE-FORMAT, -BANK-DATA-NOT-HELD, -QUERY-TOO-SHORT. Validation reason codes: REGISTER_INACTIVE, NOT_APPOINTED, CPD_NON_COMPLIANT, TRAINING_MISSING, LISTS_STALE, LIVE_DEPENDANT, ACTIVE_CONTRACTS.

**Consumed:** see §4 above (POL, CLM, BIL, PFC, WRK, DOC, PLT, MKT, CMP).

**Latency budgets (NFR-PTY-003/004):** `Party.get`, `ProducerCode.validate`, `CommissionAgreement.resolve`, `CommunicationPreference.resolve` ≤100 ms p95; `Screening.screen` ≤300 ms p95 / ≤800 ms p99.

## 7. SPIs / country-pack interfaces

Used through `REQ-MKT-002` / `plt.Adapter.invoke` (§9.2, §15.1, §15.1a):

| SPI | Greece pack | MKT counterpart |
|---|---|---|
| `IdValidator` | AFM mod-11 (REQ-PTY-049); store validator version | REQ-MKT-090 |
| `NameTransliterator` (+ `searchVariants`) | ELOT 743 / ISO 843; digraph alternatives (ou, mp/b, nt/d, gk/g, ch) | REQ-MKT-091 |
| `AddressFormatter` | 5-digit postcode, postcode–locality list, autofill, Greek order | REQ-MKT-092 |
| `RegistryLookup` | AADE RgWsPublic2 + GEMI open data | REQ-MKT-095 |
| `IntermediaryRegister` | chamber register: manual verification + file import (no API) | REQ-MKT-106 |
| `SanctionsListSource` | EU, UN, OFAC (group) files; optional commercial provider | REQ-MKT-107 |
| `NumberingScheme` | formats | PLT REQ-PLT-014 |
| `Geocoder` (added by R-17 / CCR-PTY-04) | EU-hosted provider "to select" | not listed in §9.2 / §15.1 SPI lists |
| `ConsentRules` | — | REQ-MKT-111 (appears only in §15.1a) |
| MKT language rules for search keys | tonos/dialytika removal, final-sigma folding, Greek upper-casing | REQ-MKT-178 |

Multi-market (§10.4): Cyprus stub pack in CI must exercise TIC validation, 4-digit postcodes and Cyprus intermediary categories to prove "PTY core contains no Greek assumptions" (REQ-MKT-008). Bulgaria fixture pack referenced in REQ-PTY-065 AC.

## 8. Screens (§6)

General rules: GR/EN labels, ΕΛ|EN switch (R-101) changes all UI text instantly without reload or input loss, but never changes customer communication language; semantic states (loading, empty, error RFC 9457 + correlation id, permission, stale, conflict, offline/degraded); density `IB-16`; keyboard list nav `IB-02`; quiet chrome `IB-24`; P2 masked unless `pty.p2.reveal`; every list has column chooser and masked export (`pty.export`). Shell, settings and Administration menu are PLT-owned; palette WRK-owned.

| ID | Name | Persona | One line |
|---|---|---|---|
| SCR-PTY-01 | Party and account search | ROLE-03/04, 05/06/08 book-scoped, 10/11, 17, 22, 29, 33 | Single box, any script, number-format detection; tabs Parties/Accounts/Producer codes/Contacts; quick-look overlay `IB-18` |
| SCR-PTY-02 | Create/edit person or organisation | ROLE-03, 05/06/08 via CHN, 33 | Split-pane `IB-05` form + live duplicate panel; registry suggestions `IB-27` + `IB-07` approve-the-diff; create party+account in one transaction |
| SCR-PTY-03 | Producer picker | ROLE-03/04/07/09 | Overlay picker; rows failing validation in warning state, unselectable in bind contexts |
| SCR-PTY-04 | Party 360 | many | Tiles from owner read models; AI summary card optional `IB-09`; info states vulnerability/screening/merged/restricted/deceased |
| SCR-PTY-05 | Account 360 | ROLE-03/04, 05/06/08, 10/11, 45 | Account dashboard, Actions menu, Refresh now replaces Recalculate |
| SCR-PTY-06 | Account contacts and roles | ROLE-03/04 | Member list + detail tabs; change holder; guarded remove |
| SCR-PTY-07 | Account locations | ROLE-03/04/11 | Location register; guarded remove |
| SCR-PTY-08 | Party panel in policy context | ROLE-03/04, 05/06, 10 | Embedded PTY component hosted by POL job; edits write to PTY immediately; driver policy attributes stay POL |
| SCR-PTY-09 | Household and relationship editor | ROLE-03/11/33 | List primary, graph `IB-15` toggle |
| SCR-PTY-10 | Life-event wizard | ROLE-03/04/33 | Stepper `IB-12`; reverse within window (`pty.lifeevent.reverse`) |
| SCR-PTY-11 | Data-steward review queue | ROLE-33; ROLE-09 for intermediaries | Priority queue `IB-04`, views `IB-03`, locking `IB-19` |
| SCR-PTY-12 | Merge preview and unmerge | ROLE-33 maker/checker | Side-by-side survivorship, impacts, dry-run re-point map |
| SCR-PTY-13 | Account merge, policy move, household split | ROLE-04 + checker | Three modes with preview |
| SCR-PTY-14 | Consent and preference centre | ROLE-03, 30; customers via CHN | Purpose × channel/language/medium matrix; marketing; trusted contacts; vulnerability (P3-gated); history `IB-29` |
| SCR-PTY-15 | Sanctions hit review | compliance analyst, ROLE-29 | Case queue, side-by-side party vs list entry, four-eyes FP |
| SCR-PTY-16 | Intermediary hierarchy browser | ROLE-09, 07 (read), 24 | Tree with counts; exception-first `IB-28` |
| SCR-PTY-17 | Intermediary and producer profile | ROLE-09, 07, 05/06, 29 | Binary status at a glance `IB-32` (Register, Authorisation, CPD, PI, Agreement, Appointment) |
| SCR-PTY-18 | Appointment and producer-code editor | ROLE-09 maker/checker | Effective-dated grid, authorities, diff |
| SCR-PTY-19 | Commission agreement editor | distribution finance analyst; ROLE-09/finance controller checker | Versions, rate lines, base, overrides/splits, chargebacks, settlement, diff, simulation |
| SCR-PTY-20 | PoR change and bulk book transfer | ROLE-04, 09, 07, 06 | Single/bulk modes, dry-run, progress, per-term outcomes |
| SCR-PTY-21 | Agency user administration | ROLE-07, bank admin, ROLE-09 | Users, roles, grants, attestation, audit; also in broker portal |
| SCR-PTY-22 | Data-quality scorecard | ROLE-33, 30, 09, 45 | Hero metrics `IB-17`, filter bar `IB-26`, drill-down |
| SCR-PTY-23 | Bancassurance partnership configuration | ROLE-09 | Should P1 fast-follow (D6) |

Design-guide patterns referenced: IB-01 command palette, IB-02, IB-03, IB-04, IB-05, IB-06 agent plan, IB-07 approve-the-diff, IB-08 explanation popover, IB-09 AI card, IB-12, IB-13 statement view, IB-14 edit in place, IB-15 graph, IB-16, IB-17, IB-18, IB-19 presence, IB-21 semantic status, IB-24, IB-25, IB-26, IB-27 self-enriching record, IB-28 exception-first, IB-29 log layout, IB-30 (DAT), IB-32. Inventory coverage (§6.x, §15.3): 9 owned reference screens, 284 field rows: 226 specified, 35 replaced, 23 not applicable in GR (County/State, US SSN, MVR, good student, violations), 0 missing.

## 9. Regulatory, tax and statutory rules

### 9.1 Explicitly stated (verbatim-precise)

| Rule | Source / ID |
|---|---|
| Greek intermediary categories: insurance agent, coordinator of insurance agents, insurance broker (acts on customer's written mandate), ancillary insurance intermediary; credit institution acts as agent; **no "tied intermediary" category in Greek law** | Law 4583/2018 Art. 4 §1 items 3–7; REQ-PTY-187, BR-PTY-027, CCR-PTY-01 (Accepted, R-14) |
| Register data (chamber special register; Central Union of Chambers single information point; name, category, chamber, register number, responsible persons, distribution staff, AFM and DOY, address) | Art. 19 §2, §10, §11; REQ-PTY-188/189/194/249 |
| Applications and policies must show **name, tax number and register number** of the intermediary in direct contact, other contracted intermediaries and the coordinator | Art. 28 §3; REQ-PTY-197 |
| Customer information: identity, address, category, register number + verification means, advice, on whose behalf, premium collection, nature of remuneration (8 items) | Art. 28 §1, Art. 29; REQ-PTY-196, -241 |
| Conflict data: holdings **above 10 %** of voting rights or capital, both directions; basis of acting | Art. 29; REQ-PTY-195 |
| Delivery medium: **paper by default**; durable medium other than paper only if choice offered, chosen, appropriate and (Greece) email provided; website only with consent + electronic notification + availability; **paper copy on request, free of charge**; email address = evidence of regular internet access | Art. 33 §2–§6 (IDD Art. 23); REQ-PTY-005, -083, -147, -148, -149; BR-PTY-012/013/014 |
| PI cover minimum **EUR 1,564,610 per claim, EUR 2,315,610 aggregate from 9 Oct 2024** (pack data) | BoG decision 230/2/17.6.2024 (Gov. Gazette B 3680/26.6.2024); BR-PTY-031 |
| CPD **75 hours per five-year cycle** from knowledge certificate date (UNVERIFIED); IDD Art. 10(2) minimum 15 h/year | BoG ECA 169/1/29.4.2020 (trade press); BR-PTY-029; OI-PTY-03 |
| Greek non-life insurer **not** an AML obliged entity (life only) → AML switch off; sanctions mandatory | Law 4557/2018 Art. 3 point 3; REQ-PTY-186 |
| Asset freeze: screen before any payment; payment block; true-match handling | Council Reg. (EU) 269/2014 Art. 2; REQ-PTY-006, -169…-185 |
| Lists: EU consolidated + UN SC consolidated minimum; national per pack; OFAC SDN by group config (Fairfax) | REQ-PTY-170, BR-PTY-019 |
| VIES prefix: Greece "**EL**", never "GR" (auto-correct with info) | OBL-TAX; REQ-PTY-050, BR-PTY-003 |
| GDPR: lawful basis per purpose; consent proof + withdrawal (Art. 7); one-month response extendable by two (via CMP); Art. 15–18, 20, 21, 30; Art. 17(3)(b),(e) retention exemption; Art. 18 restriction | REQ-PTY-150…168 |
| Special categories safeguards (encryption, pseudonymisation, access restriction) → vulnerability as P3 | Law 4624/2019 Art. 22; REQ-PTY-155, -168 |
| Distance marketing of financial services: intermediary identity, address, phone, email, register details, supervisory authority, and same for a third party, available to CHN pre-contract pages | Directive (EU) 2023/2673 → Law 5317/2026 Art. 70 §1(α),(β),(δ),(ε) (R-55); REQ-PTY-196/197 |
| EAA treated as applying to online sales/servicing → WCAG 2.2 AA | Dir. 2019/882, Law 4994/2022, R-57; NFR-PTY-012 |
| DORA: ICT third-party register of information, incidents | Reg. 2022/2554 Art. 17, 28; REQ-PTY-281 |
| AI Act deployer duties (Art. 12, 14, 26, 50) | REQ-PTY-279/280 |
| AFM: exactly 9 digits, reject 000000000, check digit = ((Σ_{i=1..8} d_i × 2^(9−i)) mod 11) mod 10 = d9; "EL" prefix only in VAT scheme | REQ-PTY-049, BR-PTY-002 |
| LEI: 20 alphanumeric, ISO 17442 check digits ISO 7064 MOD 97-10 (core, country-independent); mandatory for reinsurers (R-53) | REQ-PTY-051, -097 |
| Greek postcode 5 digits with postcode–locality consistency | REQ-PTY-076 |
| Phone E.164, Greece default +30 | REQ-PTY-082 |

### 9.2 Referenced but NOT specified (gaps)

- Competent Greek authority + procedure for reporting frozen funds; Fairfax OFAC policy wording (OI-PTY-04, REQ-PTY-179 "UNVERIFIED").
- CPD Act text: cycle start, carry-over, who is subject (OI-PTY-03).
- AADE RgWsPublic2 protocol, auth with special access codes for a system account, quotas, permitted storage (OI-PTY-01). GEMI official rate limit (aggregator says 8/min; OI-PTY-02).
- Chamber register bulk extract/API and legal basis for automated retrieval (OI-PTY-08).
- Telemarketing opt-out register mechanics (Law 3471/2006 Art. 11; OI-PTY-07; REQ-PTY-153 Could P2).
- Vulnerable-customer guidance EIOPA/BoG (OI-PTY-05).
- Law 4624/2019 article that restricts DSAR disclosure of screening cases (REQ-PTY-162, DPO to confirm).
- EAEE distribution statistics format (OI-PTY-09).
- Whether sex must be captured for any Greek identifier/document, e.g. Green Card (OI-PTY-12).
- Cyprus intermediary law and registry API (OI-PTY-11).
- Greek "competent authority" reporting format for true matches.

### 9.3 Deferred to configuration / country pack

Retention periods for all RC-PTY-* classes (programme retention schedule under REQ-PLT-011, XMR-D-259; OI-PTY-06 closed — earlier 20y/5y placeholders withdrawn); prospect retention (BR-PTY-040); CPD hours/cycle; PI minimums; intermediary categories; identifier scheme catalogue; legal-form codes; DOY list; postcode list; consent purposes; website availability days; vulnerability categories/handling rules; screening lists and thresholds; maximum commission rates (`pty.commission.maxRates`, default none); intermediary VAT treatment code; self-billing default (OI-PTY-10 closed per D3: CMP is sole fiscal issuer).

## 10. Greek-market specifics

- **AFM** (REQ-PTY-049): see formula. Test vectors (§14.1): valid 123456783, 111111114, 800000002, 100000090 (check 10 → 0), 090000045; invalid 123456789 (check digit), 000000000, 12345678, 1234567830, 12345678A; VAT "EL123456783" valid, "GR123456783" corrected to EL. (I recomputed all five valid vectors; they satisfy the formula.) Error code PTY-ERR-ID-CHECKDIGIT in GR+EN. Stored encrypted with keyed blind index for exact search (REQ-PTY-060). Uniqueness: one non-merged party per legal entity at any valid time (REQ-PTY-052, BR-PTY-001); conflict → duplicate suggestion, not bare error. Migration: legacy AFMs failing check digit loaded as VerificationFailed + DQ issue when MIG flags them legacy-known (§14.2). DOY stored as coded attribute of AFM, prefilled from registry (REQ-PTY-059), used on fiscal documents (CMP). Masked to last 3 digits without P2 permission (REQ-PTY-044); reveal audited with purpose; search by AFM audited with scheme only (REQ-PTY-072, Should).
- **Registries:** AADE RgWsPublic2 (TAXISnet + special access codes; basic registry data for legal persons and natural persons with business income); GEMI open data (KAD, company data). Results stored as evidence (hash, timestamp, source) and shown as suggestions; never overwrite (BR-PTY-004). Outage → SelfDeclared, verification queued; never blocks creation (NFR-PTY-016). VIES verification Should P3.
- **Names / search:** native + ELOT 743 generated Latin + "as on ID document" Latin (preferred for Green Card, payments, sanctions, cross-border; REQ-PTY-063). Father's name recommended (warning if missing). Search key rules (REQ-PTY-065): core = NFD decomposition, combining-mark removal, Unicode case folding, punctuation/space collapse; Greek pack via MKT language rules = tonos/dialytika removal, final-sigma folding, Greek upper-casing; cross-script keys from `NameTransliterator.searchVariants`. Example: "Σωτηρόπουλος" → normalised "ΣΩΤΗΡΟΠΟΥΛΟΣ", cross-script includes "SOTIROPOULOS". Trigram indexes, exact/prefix before fuzzy (REQ-PTY-066). Reverse digraphs: "Dokos" finds "Ντόκος" (REQ-PTY-067). Exact-match toggle default off. Golden set ≥2,000 pairs incl. accents, final sigma, dialytika, ELOT digraphs (Ευθυμίου → Efthymiou, Αυγερινός → Avgerinos, Αγγελόπουλος → Angelopoulos, Μπακογιάννης → Bakogiannis, Καμπάνης → Kampanis, Ντόκος → Ntokos, Χατζηδάκης → Chatzidakis, Ψαρρός → Psarros, Γεωργίου → Georgiou), passport variants Yiorgos/Giorgos/Georgios, reversed order, double family names, Cyrillic. Address example: "Λεωφ. Κηφισίας 124, 11526 Αθήνα" → "Leof. Kifisias 124, 11526 Athina". **Greek collation for sorting** required (NFR-PTY-013) — mechanism not specified.
- **Addresses:** no County/State in GR; municipality/region derived from postcode; Greek document order.
- **Intermediaries:** Greek categories, chamber register (file + manual), coordinators (REQ-PTY-211), Art. 28 §3 document data, CPD/PI.
- **Language:** Greek binding, English informative; customer document language from preference (REQ-PTY-159), not UI switch.
- **Currency:** agreement currency default EUR; PI amounts EUR.
- **myDATA:** not touched directly; commission fiscal documents via CMP (D3, REQ-PTY-240). **gov.gr:** not mentioned. **Information Centre/bureau:** not mentioned in PTY.
- **No traffic-violation points register** available to insurers → violation/MVR fields N/A (SCR-PTY-08).

## 11. Controls

- **Audit (§12.1, via REQ-PLT-002):** every CRUD on every PTY entity; P2 reveal with purpose; P2 search (scheme only); registry request + response hash; suggestion accept/reject; merges/unmerges/account merge/move/split (before-images ref); not-a-duplicate; life-event steps and reversals; consent capture/withdrawal with proof; P3 vulnerability views; screening runs, case decisions, suppressions; list activation/rejection; PoR/transfer outcomes; agreement transitions; delegated-admin actions and attestations; AI outcomes.
- **Authority types registered (§12.2, REQ-PLT-003):** PTY_MERGE_INFORCE (LE, number of in-force terms); PTY_BOOK_TRANSFER (terms, written premium); PTY_COMMISSION_APPROVAL (max rate, LoB, LE); PTY_SANCTIONS_DECISION (FP vs true match); PTY_AUTHORITY_GRANT (collect premium, bind).
- **Maker-checker (§12.3):** party merge/unmerge with in-force or open-claim roles (REQ-PTY-144); account merge/move/split touching in-force terms (REQ-PTY-130); sanctions FP clearance (REQ-PTY-177); commission agreement versions (REQ-PTY-234); producer-code authorities (REQ-PTY-205); bulk transfers and hierarchy moves (REQ-PTY-214, -220); bancassurance activation/switch (when on); migration import reversal (REQ-PTY-282); screening thresholds and list-set config; AI toggles at tenant/entity; unmerge after window via data-change procedure (REQ-PTY-138); backdated agreement with recalculation flag (REQ-PTY-235).
- **SoD (§12.4):** SOD-PTY-01 maker ≠ checker; -02 intermediary/agency users cannot approve own agreements/transfers; -03 FP proposer cannot approve; compliance roles cannot hold BIL/CLM payment-release permissions; -04 merge executor cannot approve its unmerge; -05 agency admin no self-grant/out-of-hierarchy; -06 agreement drafter cannot hold BIL commission payment-run approval.
- **GDPR handling (focus):**
  - Classification P0–P3 per attribute, enforced in masking, exports, events, AI minimisation (REQ-PTY-168). Field-level encryption of identifiers and merge before-images; blind indexes; key rotation without downtime (REQ-PTY-060, NFR-PTY-010). No P2/P3 in events/logs (NFR-PTY-011, 0 violations in automated log scanning).
  - Nationality released only to identification, sanctions and statutory reporting; never to RAT/UW service identities (REQ-PTY-039, BR-PTY-045). Sex only where pack requires; never for pricing (REQ-PTY-031).
  - Consent: per channel and purpose with lawful basis, source, wording version, timestamp, collector, proof (REQ-PTY-150); purposes register with Art. 6/9 basis + retention class (REQ-PTY-151); withdrawal immediate, query returns withdrawn at once, `ConsentChanged` within 5 s (REQ-PTY-152); objection = withdraw all marketing + suppression (REQ-PTY-166); most-restrictive wins on merge (BR-PTY-016); migrated without proof = not given (REQ-PTY-271); evidence pack export (REQ-PTY-160).
  - **DSAR:** export all PTY personal data in structured machine-readable form to CMP within 24 h (REQ-PTY-161); exclude legally restricted screening details with legal reason code for DPO review (REQ-PTY-162); rectification via normal edit flows with DSAR reference as reason (REQ-PTY-163).
  - **Erasure:** evaluate against retention: erase/anonymise what has no basis; otherwise set **Restricted** and record retention conflict with obligation ref (REQ-PTY-164). Restricted = hidden from search except DPO/legal, excluded from marketing/analytics feeds, all updates refused except release (REQ-PTY-165). Retention end computed from latest role end + pack class rules, handed to PLT retention engine for anonymisation keeping only non-personal statistical attributes (REQ-PTY-167). Prospects proposed for purge after pack period (REQ-PTY-045, Should).
  - Vulnerability: P3, consumers get rule codes only (REQ-PTY-011, BR-PTY-017), review date default 12 months (BR-PTY-018), removed on request.
  - External providers get minimum data; non-government providers EU-hosted (NFR-PTY-009).
- **ABAC (REQ-PTY-070, -245):** intermediary users see only parties linked to accounts/policies whose PoR is a granted producer code; bank staff only partner book; staff only their legal entity (no error, just no result, REQ-PTY-035). Grant changes effective in PLT within 60 s (REQ-PTY-252). Quarterly attestation, 14-day grace, then suspend (REQ-PTY-248, BR-PTY-039).
- **Tipping-off:** screening status never shown to customers, intermediaries or bank staff; neutral "pending compliance review" (REQ-PTY-183).

### KYC / screening detail (focus)

- Triggers (REQ-PTY-172): onboarding with customer/payee/intermediary/vendor/reinsurer role; change of name, birth date, nationality, identifier, country of residence; every list activation (delta rescreen of all active parties vs new/changed entries); periodic full rescreen (default monthly, BR-PTY-025); synchronous from POL (bind), BIL (refunds and **every** disbursement incl. FS_CLEARING, CMP_REDRESS, RI_SETTLEMENT payees; D1: BIL is only cash executor), CLM (claim payment requests). Imported parties screened before first contract activation (REQ-PTY-272). Ad-hoc payees screenable (name, optional DOB, nationality, country, identifiers).
- Ingestion (REQ-PTY-171): at least daily and within 2 h of a provider signal; checksum/signature; immutable versions; reject bad version and continue on last good + alert.
- Matching (REQ-PTY-173): all name forms + aliases/former names; normalisation, transliteration-aware comparison, token reordering, fuzzy; DOB/nationality/country/identifiers as positive or negative evidence.
- Classification (REQ-PTY-174, BR-PTY-020): thresholds per list and party type; exact list-identifier match → Blocked without review.
- Payment block for any PotentialHit/TrueMatch not FalsePositive (REQ-PTY-175).
- Degraded mode (REQ-PTY-182, BR-PTY-021/022): list age > 48 h default or engine down → payment screens Blocked LISTS_STALE (fail closed); bind per entity config (default fail closed); quotes deferred to bind.
- SLA: 1 business day payment-blocking cases, 3 others (BR-PTY-026). Suppression expiry default 12 months (BR-PTY-024). FP needs reason code, rationale (min 20 chars on screen), distinguishing evidence, second analyst (REQ-PTY-177). True match: block, escalate, activities for BIL/CLM/POL and authority report, republish `SanctionsHitRaised` (REQ-PTY-179). Evidence per screening: input snapshot hash, list versions, matches, scores, decision trail (REQ-PTY-181, NFR-PTY-014 reproducible).
- Related-party screening (directors, UBOs) Should P3 (REQ-PTY-180). AML extension (PEP, source of funds, risk rating) Could P4 behind `pty.aml.extension` switch (REQ-PTY-186). No KYC beyond identifier verification for Greek non-life; assumption 7 notes group "may still require KYC-light checks, configurable" — not specified further.
- Volume: monthly rescreen 1.5 M parties ≤4 h; delta ≤30 min (NFR-PTY-005); ~50,000 sync screens/day peak; 300–3,000 list entries change/month.

## 12. AI features (§11)

All off by default at tenant level; via `plt.Ai.invoke` EU gateway, zero retention; registered in CMP AI register and DAT model registry; never take final decisions; `AiInteractionRecord` per output (REQ-PTY-280); kill switch ≤60 s with no input loss (REQ-PTY-279); 5 s timeout fallback. Module is complete with all AI off.

| ID | Feature | Classification | Phase eligibility | Needed for MVP? |
|---|---|---|---|---|
| AI-PTY-01 | Duplicate-match explainer + survivorship assistant | Minimal risk; MP-C | P1 eligible | No |
| AI-PTY-02 | Sanctions hit triage note | High-risk controls applied (uncertain); MP-B; auto-disable on breach; zero tolerance "likely FP" on later TrueMatch | P2 | No |
| AI-PTY-03 | Identity/registry document extraction to party | Limited risk; MP-D; ≥98 % identifier accuracy | P2 | No |
| AI-PTY-04 | Natural-language party search (query text only) | Minimal risk; MP-F | P1 eligible | No |
| AI-PTY-05 | Life-event detection + next-best-action | High-risk controls applied; MP-A | P3 | No |
| AI-PTY-06 | Vulnerability signal suggestion | High-risk controls; MP-A; DPO approval to enable | P3 | No |
| AI-PTY-07 | Commission agreement drafting/comparison | Minimal risk; MP-C | P3 | No |
| AI-PTY-08 | Party/account summary card | Limited risk; MP-C | P2 | No |

GR/EN privacy-notice sentences are given verbatim for AI-PTY-01, 02, 03, 05, 06. REQ-PTY-279/280 (governance, logging) are Must P1 even with features off.

## 13. Open issues / assumptions / CCRs

**Open issues (§16.1):** OI-PTY-01 AADE protocol/limits (open); -02 GEMI rate limit (open); -03 CPD Act (open); -04 sanctions reporting authority + Fairfax policy (open); -05 vulnerable-customer guidance (open); -06 retention periods (**closed**, XMR-D-259); -07 opt-out register (open); -08 chamber register access (open); -09 EAEE format (open); -10 commission fiscal documents (**closed**, D3); -11 Cyprus (open); -12 sex capture for Greek documents (open).

**Assumptions (§16.2):** volumes per XMR-FR-200 (1.5 M parties, 20,000 intermediaries, 25,000 intermediary users); household = facet of Personal account; POL holds policy-context links; BIL holds bank details (R-38); PLT supports external realms with PTY-held grants; public sanctions files, commercial provider optional; Greek non-life not AML obliged (KYC-light configurable); registry data storable as evidence subject to OI-PTY-01; migration scenario B (convert at renewal, D6) until board confirms before wave W5; bancassurance not live at P1 unless bank partner signed by G0.

**Prompt-vs-finding assumptions (§3.3 A1–A10):** e.g. A1 no tied category; A7 not AML obliged; A8 bancassurance configurable, out of P1 cut.

**Risks:** RK-PTY-01…07 (registry limits, sanctions FP rate on Greek names, legacy duplicates, register without API, vulnerability misuse, commission backdating disputes, bancassurance short-notice).

**CCRs (all Accepted, contract v1.2):** CCR-PTY-01 (Greek categories, R-14); -02 (R-15 events + AccountMerged reversal flag); -03 (screening state ↔ response mapping, R-16); -04 (`Geocoder` SPI, R-17); -05 (maker-checker list additions, R-18); -06 (retention class codes, R-19).

**Module decisions still open (§16.5):** 1 matching model + steward staffing; 2 unmerge/account-merge windows (90/30 proposed); 3 sanctions sources + OFAC scope; 4 thresholds + bind degraded mode; 5 retention (settled); 6 register approach; 7 CPD enforcement block vs warn; 8 PoR commission consequence options/default; 9 household discounts in motor MVP and MVP relationship types; 10 AI features at go-live and DPO conditions for AI-PTY-06.

## 14. Conflicts and ambiguities found

### (a) With infra / stack (ARCHITECTURE-DECISIONS.md)

1. **Lakehouse:** §13.3 "All analytics run in the lakehouse on pseudonymised data (`REQ-DAT-007`)." Stack explicitly forbids lakehouses; must be read as PostgreSQL reporting marts.
2. **Workflow engine:** persona "SYS-02 Workflow-engine workflow" (§2); `plt.Workflow.start`, `plt.Workflow.signal` for life events, transfers, list ingestion, batch matching (§9.2); REQ-PLT-007 described as "Workflow engine" (§15.1). Stack forbids workflow servers/BPM; implement as domain state machines (LifeEvent, BookTransfer, list version) + Hangfire jobs.
3. **Broker/stream language:** "Topic `pty.events.v1`; partition keys" (§8), "`ConsentChanged` is on the stream by 10:00:05" (REQ-PTY-152 AC). Stack has outbox with in-process ordered dispatch, no broker; partition key → ordering key per aggregate in the outbox dispatcher. The 5 s SLA must be met by the outbox poller.
4. **Read models from other modules:** PolicyTermView, JobView, ClaimView, BillingAccountView, account measures refreshed ≤10 s (NFR-PTY-018, REQ-PTY-108). Compatible only if built from events in PTY's schema (rule 8: never read other modules' tables).
5. **Field-level encryption + keyed blind index + "platform key-management service"** (REQ-PTY-060, NFR-PTY-010): needs a concrete Key Vault + envelope-encryption design not in infra docs; key rotation "without downtime" with blind indexes implies re-indexing strategy (undefined).
6. **Bitemporal + non-overlap**: PostgreSQL 17 lacks native temporal PK/FK (`WITHOUT OVERLAPS` is PG18); exclusion constraints with btree_gist needed for every valid-time entity (consistent with rule 1, but implementation burden).
7. **MCP AI agent facade** (REQ-PTY-276) — owned by CHN; not a stack conflict but needs CHN design.
8. Vendor-specific: "Fairfax" group OFAC requirement (REQ-PTY-170, §13.2); reference product screens (SCR-PCACC*…) — informational only.

### (b) With system contract / other PRDs

1. Contract §3.7 row OBL-AML says AML for Greek non-life is "Uncertain … AML controls only if the PTY PRD concludes they apply"; PTY concludes **not obliged** (REQ-PTY-186) — consistent, but the contract text should be updated.
2. `Geocoder` SPI (R-17) is used by REQ-PTY-077 (Must P1), yet missing from the SPI lists in §9.2 and §15.1 (MKT REQ-MKT-002 row) and REQ-PTY-077's Requires column; provider "To select".
3. `ConsentRules` SPI (REQ-MKT-111) appears only in §15.1a; never used in any PTY requirement.
4. REQ-PTY-122 states POL and CLM do **not** subscribe to `LifeEventRecorded`; consumers must match PRD-05/07 §8.
5. §8 says DOC is not a consumer of `ProducerOfRecordChanged`; REQ-PTY-218 rationale says "DOC (intermediary-change notice) react" — mild contradiction (resolved by REQ-PTY-226 direct request).
6. Channel codes owned by CHN (REQ-CHN-317, R-84/R-99); PTY must not define its own grouping (BR-PTY-046) — PTY depends on CHN config being available.

### (c) Internal contradictions

1. **Retention placeholders:** §3.1 and §3.3 A3 say the 20-year / 5-year placeholders are **withdrawn**, but §7 intro still states "RC-PTY-CUSTOMER (… reported market practice 20 years after last contract end, UNVERIFIED), RC-PTY-PROSPECT (… reported 5 years, UNVERIFIED)".
2. **Account initial status:** REQ-PTY-004 AC: account "created in status Active"; §7.3 Account model starts at Pending → Active only after holder "confirmed and screened Clear or FalsePositive, or PotentialHit with bind blocked". J1 has screening inside the creation transaction. Which status on create, and is Pending ever observable?
3. **Unmerge target status:** state model has only `Merged → Active: unmerge`; a party that was Inactive (or Prospect) before merge would return to Active, contradicting REQ-PTY-139 ("restores every PTY row to its pre-merge content"). Also Prospect → Merged transition is absent although REQ-PTY-134 allows auto-merge of Prospects.
4. **Restricted vs active business:** Active → Restricted allowed, and Restricted refuses all updates and hides from CSR search (REQ-PTY-165), but a party with in-force policies/claims/payments still needs servicing, role maintenance from POL events and screening — undefined how event-driven role updates behave on a Restricted party.
5. **VIES / VAT:** REQ-PTY-050 (EL prefix storage and correction) is Should **P3**, but BR-PTY-003 and REQ-PTY-049 (EL only within VAT scheme) and REQ-PTY-047 (VAT scheme in Greece pack, Must P1) imply P1 handling of VAT values.
6. `AccountMerged` carries a reversal flag (Must event, §8.1), but the account-merge reversal itself (REQ-PTY-128) is only Should P1, while BR-PTY-010 defines a 30-day window as a business rule. Unclear whether reversal is in the P1 cut.
7. REQ-PTY-037 states edit-in-place for "low-risk attributes" incl. contact points, but contact points are in `PartyUpdated` CONTACT_POINTS group and some flows (durable medium email) depend on them — undo within 10 s must also revert verification state (unspecified).
8. REQ-PTY-071/NFR-PTY-001 fix ≤1 s p95 at 1.5 M; NFR-PTY-019 demands linear scale to 5 M "without schema change" — no latency target at 5 M.

### (d) Cannot build without a decision

1. Screening thresholds, matching weights and list sources (public files vs commercial provider) — §16.5 items 1, 3, 4.
2. AADE/GEMI access (OI-PTY-01/02) — REQ-PTY-054 is Must P1 with no confirmed protocol.
3. Intermediary register supply (file format, frequency) — OI-PTY-08; REQ-PTY-189 Must P1.
4. CPD block vs warn and cycle rules — OI-PTY-03, §16.5 item 7 (default block on).
5. Competent authority for TrueMatch reports — OI-PTY-04.
6. Geocoding provider for Must P1 REQ-PTY-077.
7. Greek collation strategy (ICU collation `el-GR` vs app-side sort) for NFR-PTY-013, and whether search keys are computed in C# (pack rules) vs DB functions — PRD forbids language rules in core, so DB `unaccent`/ICU nondeterministic collations cannot be the matching mechanism without being pack-supplied.
8. Retention schedule data (XMR-D-259) must be loaded before system test.
9. Household discount scope in motor MVP (§16.5 item 9).
10. Whether sex must be captured (OI-PTY-12) — affects Person schema and Green Card output.

## 15. Build notes

**Hardest parts**
1. **Merge/unmerge with full reversibility** (REQ-PTY-007, -136…-139): attribute-level survivorship over bitemporal rows, encrypted before-images, re-point maps consumed by 12 modules, post-merge change assignment on unmerge, property-based test of 10,000 random sequences. Consumers must implement re-pointing too.
2. **Sanctions engine** (REQ-PTY-169…185): list ingestion (EU/UN/OFAC formats), cross-script fuzzy matching with evidence scoring, case workflow with four-eyes, suppression rules keyed on entry version hash, delta and full rescreen at 1.5 M in ≤4 h, ≤300 ms sync screen, fail-closed degraded mode. False-positive tuning on Greek names is a known risk (RK-PTY-02).
3. **Cross-script search at scale** (REQ-PTY-001, -065…-071): key generation via pack SPI in C#, pg_trgm GIN indexes on key columns, ABAC filtering by producer grants inside ≤1 s p95; blind-index exact search for identifiers; live duplicate suggestions ≤500 ms.
4. **Bitemporal party data** (REQ-PTY-036) with `validAt`/`knownAt` reads and exclusion constraints on every valid-time table.
5. **Distribution temporal model:** effective-dated hierarchy, producer codes, appointments, authorities, PoR per term (no gaps/overlaps), bulk transfers of 10,000 terms ≤30 min, at-renewal resolution, deterministic agreement resolution (≤100 ms) with backdating guard.
6. Field-level encryption + key rotation; P0–P3 masking across UI, exports, events, logs.

**Must exist first:** PLT (identity/ABAC, audit, maker-checker/approvals, numbering, outbox events, adapter host, retention engine, AI toggle stubs); MKT (configuration layers, SPI binding catalogue, Greece pack with IdValidator/NameTransliterator/AddressFormatter + language rules REQ-MKT-178/091, Cyprus stub pack); WRK activities/queues (steward, sanctions); DOC archive for evidence. POL events/queries are needed for roles, PoR, guards and 360 tiles but PTY can start with contract stubs.

**Suggested slicing**
1. Party core: Person/Organisation, names (native/Latin/as-on-ID), identifiers with AFM/LEI validation + encryption/blind index, addresses (Greek format), contact points, status lifecycle, bitemporal reads, `pty.Party.*` API, PartyCreated/Updated events, audit/masking.
2. Search: search keys via SPI, trigram indexes, ABAC filter, WRK palette feed, SCR-PTY-01/02/04 (basic).
3. Accounts + roles + households + locations (CD-04): account API, members, holder change, role catalogue, POL-event-driven role maintenance, account 360 shell with read models.
4. Sanctions engine: list ingestion, screening sync API, cases, four-eyes FP, suppressions, degraded mode, bulk rescreen; SCR-PTY-15. (Gates POL bind and BIL/CLM payments — high priority.)
5. Distribution: intermediaries, register/CPD/PI, hierarchy, producer codes, appointments, authorities, `ProducerCode.validate`; SCR-PTY-16/17/18.
6. PoR + book transfer + commission agreements (versions, resolution, chargebacks, settlement); SCR-PTY-19/20.
7. Consent/preferences/trusted contacts/vulnerability; DSAR export/erasure/restriction; SCR-PTY-14.
8. Data quality: matching model, batch detection, steward queue, merge/unmerge, account merge/move/split; SCR-PTY-11/12/13/22.
9. Life events (SCR-PTY-10), delegated agency admin (SCR-PTY-21), registry lookup (AADE/GEMI) adapters.
10. Import APIs + reversal (REQ-PTY-013, -269…-272, -282) aligned with MIG; AI governance hooks (REQ-PTY-279/280/281). Bancassurance (SCR-PTY-23) as Should fast-follow.
