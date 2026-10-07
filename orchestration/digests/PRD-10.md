# PRD-10 Digest — Documents and Customer Communications (DOC)

Source: `core-insurance-prds/PRD-10-documents-communications.md` (2,245 lines, read in full). Version 1.4, 2026-10-07, status "Draft — freeze candidate for the build baseline". Binding input named: `00-system-contract.md v1.11` plus PRD-18 decisions D1–D10. Stack checked against `core-insurance-infra/ARCHITECTURE-DECISIONS.md` and `INFRASTRUCTURE.md`.

Counts below were recomputed mechanically from the requirement tables and match the PRD's own self-check (§5.20, §16.6).

---

## 1. Identity

| Item | Value |
|---|---|
| Module code | DOC |
| Title | Documents and Customer Communications |
| Schema | `doc` (PostgreSQL), plus a write-once archive object store (§7.0) |
| Owner roles | ROLE-14 product owner (accountable); ROLE-15 forms owner lead; ROLE-46 legal reviewer; ROLE-29 compliance; output operations manager (specialises ROLE-45); ROLE-30 DPO; ROLE-26 tax specialist (§1.1) |
| Identifier scheme | Contract anchors REQ-DOC-001…008; module requirements from REQ-DOC-030; BR-DOC-, NFR-DOC-, SCR-DOC-, AI-DOC-, OI-DOC-, CCR-DOC-, CAP-DOC-nn, J-nn, document types `DT-<CODE>`, risks RK-DOC-nn (§1.1) |

**Purpose (§1.2).** DOC is "the system's single outbound voice". It owns:
- the template and clause library, with legal approval per language (Greek is the binding master in Greece);
- policy form patterns and the binding matrix (product version × form × edition × language → template version);
- the payload builder and data dictionaries;
- a rendering engine (the PRD says "built from scratch"; see §14 conflict C1);
- the immutable archive, also used by WRK for inbound binaries and by other modules for evidence files;
- delivery through the channel the customer chose and the law permits, with proof suitable for statutory notices;
- electronic signature;
- pre-contractual documents and the provisional proof of cover (cover note) issued at bind;
- the unified Documents tab shown on every business object.

**The five key decisions (§1.2).**
1. Clauses are versioned legal objects; templates are language-independent layouts that compose them. Rendering refuses, and never falls back, when a language version is not approved.
2. Every document can be reproduced from three frozen inputs: payload, template version and engine/font-set version, all stored by hash. Byte-identical re-render is proven by a nightly sample.
3. Form patterns and the binding matrix are owned once, in DOC. PFC references forms, POL calls inference, DOC renders.
4. Proof of cover is issued immediately and is non-fiscal. Fiscal documents wait for the MARK from CMP (D3).
5. Delivery is a legal act with evidence. Statutory notices get sealed evidence packs.

**Non-goals / out of scope (§1.4).**

| Out of scope | Owner |
|---|---|
| Inbound intake, malware scan, classification, extraction | WRK (DOC only stores binaries via REQ-DOC-004 and shows inbound rows via REQ-WRK-285) |
| myDATA registration (MARK, UID, QR data) | CMP (REQ-CMP-001) |
| Communication preferences, consents, durable-medium choice, trusted contacts | PTY (REQ-PTY-005, -011, -147, -148, -149, -154) |
| Business decisions that trigger documents | POL, UW, BIL, CLM, CMP |
| Product structure and IPID data | PFC (REQ-PFC-003, -010, -132, -159) |
| Statutory clock definitions and instances | CMP (REQ-CMP-003); DOC never starts or computes a clock (REQ-DOC-325) |
| Retention mechanics, legal hold, erasure engine | PLT (REQ-PLT-011, -235…-241) |
| UI string translation, locale, transliteration, SPI definitions | MKT |
| Portal/app presentation and signing journeys | CHN |
| Staff notifications | WRK (REQ-WRK-007) |
| Marketing campaigns | Not in programme scope; DOC sends marketing only when a module asks and consent exists |
| Commission calculation | BIL (DOC only renders) |
| Claims-history certificate content | CLM (DOC renders via `ClaimsHistoryFormat`) |
| myDATA calls | DOC never calls AADE (§9.3) |

---

## 2. Size metrics

| Metric | Count | Notes |
|---|---|---|
| REQ total | **305** | REQ-DOC-001…008 (8 anchors) + REQ-DOC-030…326 (297) |
| Must / Should / Could / Won't | **255 / 41 / 9 / 0** | §5.20 |
| BASELINE / ENHANCEMENT | 234 / 71 | BASELINE: 212 Must, 20 Should, 2 Could. ENHANCEMENT: 43 Must, 21 Should, 7 Could |
| By phase | P1 297, P3 5, P4 3 | No P2 requirements, though the catalogue has P2 document types |
| **Must and P1 (Motor MVP)** | **253** | The 2 non-P1 Musts are REQ-DOC-043 and REQ-DOC-254 (nat-cat, P3) |
| Non-P1 requirements | 8 | P3: 043, 067, 170, 254, 274. P4: 091, 094, 154 |
| Business rules | 38 | BR-DOC-001…070, numbered with gaps |
| NFRs | 18 | NFR-DOC-001…018 |
| Screens | 17 | SCR-DOC-01…17 |
| Document types in the catalogue | 53 | 41 P1, 5 P2, 7 P3 (§5.1) |
| Owned entities | ≈ 30 | Includes 1:n sub-entities (§7.1) |
| Events produced | 15 | 7 contract events + 8 accepted by R-39 (counting each paired name separately) |
| Event rows consumed | 21 | 40+ distinct event names (§8.2) |
| Inbound API operations | ≈ 45 | 24 rows in §9.1, several with multiple operations, plus a provider callback endpoint |
| AI features | 6 | AI-DOC-01…06; all default off |
| Open issues | 19 | 7 closed, 1 optional, 11 open |
| CCRs | 5 | All accepted |
| Risks | 8 | RK-DOC-01…08 |

**Build-size estimate: L (well above the threshold).** There are 253 Motor-MVP Musts and 17 screens. The work also includes:
- a document rendering/layout engine with byte-for-byte determinism;
- PDF/A-3 + PDF/UA output, electronic sealing and LTV;
- an immutable archive with Merkle anchoring;
- a multi-channel delivery orchestrator with legal evidence packs;
- two e-signature integrations;
- a governed authoring tool (structured editor, redline, per-language approvals);
- a form-inference engine;
- batch rendering at about 80,000 documents/h.

This is one of the largest modules in the programme.

---

## 3. Owned entities

Source: §7.1. Entities marked † are DOC-private (R-01). FormSet/FormInstance and DataDictionary are cross-module visible (R-44). Every row carries `legal_entity_id`, `jurisdiction`, `created_at/by`, `record_version`, and `valid_from/valid_to` where it has business validity. IDs are UUIDv7. Library objects are append-only versions. Payloads are canonical JSON in the object store, referenced by hash (§7.0).

| Entity | Key attributes | Constraints |
|---|---|---|
| DocumentType | code, names GR/EN, owning_module, subject_types[], trigger_mode (API/EVENT), trigger_event, trigger_condition, recipients jsonb, dictionary_ref, language_rule_ref, channel_rule (STD/STAT/PORTAL/SMS), fallback_chain, required_proof_level, signature_level, seal, retention_class, statutory_notice, fiscal, fiscal_rationale, fiscal_adviser_ref, batch_eligible, numbering_scheme, layer (core/pack/entity), final_attributes[], version, validity, status | (code, version) unique per legal entity; one trigger mode per subject transition (BR-DOC-003) |
| Template / TemplateVersion | code, category, titles; version: content (template-language tree), dictionary_ref (code + major), block_refs (code, mode, version), clause_slots, rendering_profile_ref, page_masters, tests_ref, status, effective_from/to, change_note, ai_assisted, content_hash | Published versions are immutable |
| Block / BlockVersion † | As Template, with kind = block | — |
| Clause / ClauseVersion | code, category, titles, linked_pfc_codes[]; version: status, effective dates, variables schema, per-language texts (ClauseLanguageText †: language, ICU text, state Draft/InReview/Approved/Stale, content_hash), ai_assisted | Binding language approved first (BR-DOC-004) |
| TranslationApproval | object_ref, language, approver, role, decided_at, decision, attestation_text, compared_binding_version_id | One Approved per object version × language |
| ReviewComment † | anchor range, blocking flag, resolved_by/at | — |
| DataDictionary / DictionaryVersion | code (DD-POLICY…), major.minor, fields jsonb (path, type, format class, source operation, requirement id, p_class, required, description GR/EN, derivation) | — |
| RenderingProfile † | brand theme reference, font_set_version, page settings, seal profile, accessibility settings | — |
| FontSet † | fonts (name, file hash, licence ref), Unicode coverage map | — |
| EngineVersion † | version, build artefact hash, runnable flag | Must stay runnable (REQ-DOC-161) |
| FormPattern / FormPatternEdition | code, number, edition, names, category, policy_line, post_quote_data, endorsement_number, priority, reference_code, products jsonb, transaction_types[], ordered availability_rows (available, start incl., end excl., jurisdiction, subdivision, legal_entity), group_code, replaces_pattern_id, inference (condition type, pfc_item_ref, all_instances, rule table ref), change_behaviour (EVERY/ON_CHANGE/NEVER), status | (number, edition) and (code) unique per legal entity |
| Binding | product_version_range or document_type, form_pattern_edition_ref, language, channel, template_ref, mode (PINNED/FLOATING), template_version_id, effective_from (incl.)/to (excl.), status, approval_request_id | No overlap per key and period (BR-DOC-040) |
| FormSet † / FormInstance | job_id, quote_version or transaction_id, inferred_at, knownAt; instance: pattern edition, inferred, manual_reason, inference_reason, replacing_pattern, endorsement_number, in_force, effective_date | One per job version and per bound transaction |
| DocumentRequest | type, subject_ref, reference dates (validAt/knownAt), requester (user/service/AI agent), idempotency_key, delivery_instruction, language_override, batch_id, priority lane, correlation_id, status (+ sub-state), failure_class, retries | Idempotency key unique per caller for 7 days |
| Payload † | dictionary_version, canonical_json_ref, payload_hash, header (subject, reference dates, configuration_hash, artefact_hash, correlation_id, source versions) | Hash unique per content |
| RenderedDocument | document_number, type, request_id, subject_refs[], language, binding_or_informative, counterpart_document_id, template_versions, clause_versions, engine_version, font_set_version, rendering_profile_version, payload_hash, archive_item_id, html_item_id, content_hash, page_count, sealed, status, superseded_by, hidden, hidden_reason, ai_interaction_ids[] | document_number unique per legal entity |
| ArchiveItem | hash, size, media_type, original_filename, origin_module/ref, object_links[], retention_class, retention_start, legal_hold_state (cached from PLT), confidentiality, p_class, scan_ref | Binary is write-once |
| ArchiveRendition † | parent, kind (normalised, redacted, html, signed; reprint overlays are not stored), hash | — |
| ArchiveDayRoot † | date, partition, merkle_root, item_count, anchored audit ref | — |
| Delivery | document_id, recipient_role/party, medium (PAPER/DURABLE_OTHER/WEBSITE), channel, legal_basis (rule version, consent proof ref), status, required/achieved proof level, notification_date, is_copy, fallback_of_delivery_id | — |
| DeliveryAttempt | channel, provider, provider_ref, contact-point/address **reference** (not the value), sent_at, status events, proof artefact refs, failure reason | — |
| EvidencePack | delivery ids, document_id, items (kind, archive ref, hash), notification_date, rule version, sealed_at, seal_ref, time_stamp_ref, supplements_pack_id | Immutable once sealed |
| SignatureEnvelope | subject_ref, documents (refs + hashes), signatories jsonb (party, role, identifier scheme, masked identifier, order, level, provider, status), provider refs, deadline, reminders, status, signed_archive_refs, validation_report_refs | Identifier encrypted |
| BatchRun † | batch_key, types, window, pinned binding snapshot hash, engine version, partitions jsonb with checkpoints, counts, status | — |
| FailureItem † | request or delivery id, class, reason, retryable, owner_team, statutory flag, clock ref, assigned_to, resolution | — |
| PrintManifest † | vendor profile, slot, pieces/pages/sheets/inserts, checksum, vendor reports, reconciliation status | — |
| ChannelIdentity † | legal entity, kind (SMS sender, email domain), value, registration evidence, expiry | — |
| PrintVendorProfile † | formats, window geometry, inserts, cut-offs, registered services, active | — |
| GoldenDocument † | template_version, case id, rendition hash, approver | — |

**Retention classes** (§7.1). Durations are pack data and the Greek values are UNVERIFIED; OI-DOC-09 is closed by citing the programme retention schedule.

| Class | Covers | Trigger |
|---|---|---|
| RC-DOC-CONTRACTUAL | Contractual documents | Object retention end |
| RC-DOC-CORRESP | Correspondence | Sent date |
| RC-DOC-PRECONTRACT | Pre-contractual documents | Quote expiry; reclassified to CONTRACTUAL on bind |
| RC-DOC-STATNOTICE | Statutory notices | Later of contract end and limitation period |
| RC-DOC-CLAIM | Claim letters | Follows the claim |
| RC-DOC-COMPLAINT | Complaint letters | Follows the complaint |
| RC-DOC-REFUSAL | Refusal documents | Decision date |
| RC-DOC-FISCAL | Fiscal images | Financial year end |
| RC-DOC-FINANCIAL | Financial statements | Financial year end |
| RC-DOC-EVIDENCE | Evidence packs | Retention end of the documents proved |
| RC-DOC-LIBRARY | Library objects | Retirement + longest retention of documents issued with them |
| RC-DOC-OPS | Operational rows | Delete after 13 months; preview/failure payloads after 30 days |
| RC-DOC-INTERNAL | Internal exports | Creation |

### State machines (§7.3)

- **Outbound document** (canonical, contract §3.2.4): Requested → Rendering → Rendered / Failed → Superseded.
  - Sub-states: Requested.Queued, Requested.AwaitingFiscal, Requested.AwaitingData; Failed.Retrying, Failed.Final; Superseded.Replaced, Superseded.Withdrawn.
  - Transitions: Queued ⇄ AwaitingFiscal (exits on `FiscalDocRegistered`); Queued ⇄ AwaitingData; Requested → Rendering (not held, payload valid); Rendering → Rendered (archived, hashed, sealed where required; emits `DocumentRendered`); Rendering → Failed (emits `DocumentRenderFailed`); Failed → Rendering (retryable and under the limit, or manual retry); Rendered/Requested → Superseded (emits `DocumentSuperseded`). A statutory notice that has been delivered cannot be withdrawn.
- **Delivery:** Pending → Sent → Delivered (required proof level reached; `DocumentDelivered`). Also Pending → Failed; Sent → Bounced; Sent → Failed (confirmation window expired). Bounced and Failed emit `DeliveryFailed`; a fallback creates a new linked Delivery.
- **Library objects** (template/block/clause versions, form pattern editions, document types, bindings): Draft → InReview (lint and tests pass) → Approved (all required approvals per language and role) → Published (effective date or publish now) → Retired (no future references). InReview → Draft on changes requested. Draft can be deleted if never published. Guards: maker ≠ checker; binding language first; Stale blocks Approved. Mapping to PRD-18 §9.4 governance stages (CR-S3-25): Authoring / Under review / Approved, not live / Live / Replaced or Withdrawn.
- **Signature envelope:** Draft → Sent → Completed / Declined / Expired (Expired emits `SignatureDeclined` with reason EXPIRED); Draft/Sent → Voided. Per-signatory sub-states: Pending, Notified, Viewed, Signed, Declined. Note: SCR-DOC-14 and REQ-DOC-271 list Viewed/Signed as envelope statuses (see §14 C14).
- **Batch run:** Scheduled → Running ⇄ Paused → Completed / CompletedWithFailures / Cancelled. Partitions: Pending → Running → Done / Failed → Running (re-run).
- **Evidence pack:** Open → Sealed → Supplemented. Supplementing creates a new linked pack; the original never changes.

---

## 4. Consumed entities / dependencies

From §7.2 and §9.2. DOC builds payloads **only** through owners' query APIs as of the reference point, never from their tables (REQ-DOC-126).

| From | Entities | API / event |
|---|---|---|
| PTY | Party, ContactPoint, Address, PartyRole, names in two scripts ("as on ID document") | `pty.Party.get`; REQ-PTY-001/-002/-012 |
| PTY | CommunicationPreference, Consent | `pty.CommunicationPreference.resolve`, `pty.Consent.query` (REQ-PTY-005, -147…-150); events `CommunicationPreferenceChanged`, `ConsentChanged` |
| PTY | Trusted contacts, vulnerability handling | `pty.Vulnerability.query` (REQ-PTY-011) |
| PTY | Intermediary, ProducerCode, ProducerOfRecord | `pty.ProducerCode.validate`, `pty.ProducerOfRecord.get` (REQ-PTY-008/-009/-197) |
| PTY | Merge/unmerge, address changes | `PartiesMerged`, `PartyUnmerged`, `PartyUpdated` |
| PFC | ProductVersion, coverage/exclusion/condition catalogue, Offering, structured IPID data, channel set | `pfc.ProductVersion.resolve`, `pfc.Catalogue.get` (REQ-PFC-001/-003/-159); `ProductVersionPublished` (with `ipidChanged`), `ProductVersionRetired` |
| POL | Policy, Term, Job, Transaction, risk units (bitemporal snapshots) | `pol.Policy.get` (validAt/knownAt), `pol.Policy.getMany` (≤ 200 per call, REQ-POL-351), job queries; many lifecycle events, mostly for read-model refresh |
| RAT | Worksheet premium breakdown | `rat.Breakdown.get` (REQ-RAT-008) |
| BIL | Invoices (payment notices), receipts, refunds, delinquency, commission, account current | `bil.*` getters (§9.2); events `PaymentReceived`, `RefundApproved` |
| CLM | Claim tracking, claims-history certificate | `clm.ClaimTracking.get`; `ClaimReported`, `ClaimsHistoryCertificateIssued` |
| CMP | FiscalDocument (series, number, MARK, QR payload); statutory clocks; DSAR | `cmp.FiscalDocument.get` / `qrPayload`; `FiscalDocRegistered/Rejected`; clock query + `Clock*` events; `DSARReceived` |
| UW | Refusal payload (REQ-UW-223) | Via API request (not consumed as an event) |
| WRK | Inbound documents | `wrk.InboundDocument.list`; `DocumentLinked/Verified/Quarantined`; `wrk.Activity.create` |
| PLT | Numbering, audit, approvals, authority, legal hold, retention, workflow engine, integration hub, time service, AI gateway | `plt.Number.next`, `plt.Audit.append`, `plt.Approval.request`, `plt.Authority.check`, `plt.LegalHold.check`, etc. |
| MKT | Configuration, SPIs, l10n formatting, translation store | `mkt.Configuration.resolve`, `mkt.L10n.format`, SPI bindings |
| MIG | Coexistence routing | `mig.Routing.resolve` (synchronous, authoritative); cache refreshed from `CoexistenceMasterChanged`, `MigrationWaveStatusChanged` (R-96) |

**Read models held by DOC** (§7.2):
- PolicyFormContextView: from `SubmissionCreated`, `QuoteIssued`, `PolicyBound`, `PolicyChanged`.
- ProductVersionView.
- CommunicationPreferenceView: a cache. Statutory notices always re-resolve.
- InboundDocumentView.
- CoexistenceRouteView.

---

## 5. Events

Topic `doc.events.v1` (§8). Partition keys: document_id, envelope_id, batch_id, or library object id. Payloads carry ids, types, statuses and hashes, **never** names, addresses, identifiers or content (REQ-DOC-172, NFR-DOC-012).

### 5.1 Produced (§8.1)

| Event | Trigger | Key payload | Main consumers | Status |
|---|---|---|---|---|
| `DocumentRequested` | Request accepted | request id, type, subject, requester type, batch id, held reason | DAT | Contract |
| `DocumentRendered` | Rendered and archived | number, type, subject, language, binding flag, content hash, archive id, recipients (roles, party ids) | POL (emits `ProofOfCoverIssued`), UW (emits `RefusalDocumentIssued`), PFC, BIL, CLM, RI, CHN, WRK, DAT | Contract |
| `DocumentRenderFailed` | Final failure only (retries are not published) | type, subject, failure class, retryable, failure item id | UW, WRK, DAT | Contract |
| `DocumentDelivered` | Delivery reached the required proof level | delivery id, recipient role and party, channel, medium, proof level, **notification date**, evidence pack id | POL (clocks REQ-POL-186; `DisclosureDeliveryView` R-83), BIL, CLM, CMP, PTY, UW, RI, CHN, WRK, DAT | Contract |
| `DeliveryFailed` | Delivery Failed or Bounced | reason (HARD_BOUNCE, RETURNED_MAIL, INVALID_NUMBER, PROVIDER_REJECTED, EXPIRED), contact point/address id, fallback delivery id | PTY (REQ-PTY-080/-084), POL, BIL, CLM, etc. | Contract |
| `SignatureCompleted` | Envelope completed and validated | subject, documents, signed archive ids, roles, provider, level | POL (REQ-POL-257, R-89), CHN, WRK, DAT | Contract |
| `SignatureDeclined` | Declined or expired | reason DECLINED/EXPIRED, role | POL, CHN, WRK, DAT | Contract |
| `DocumentSuperseded` | Replaced or withdrawn | superseded by, reason | CHN, DAT | R-39 |
| `DocumentBatchCompleted` | Batch ended | counts per status | DAT | R-39 |
| `EvidencePackSealed` | Sealed or supplemented | pack id, delivery ids, notification date, supplement flag | DAT | R-39 |
| `FormPatternPublished` / `FormPatternRetired` | Edition lifecycle | code, number, edition, languages approved, effective period | PFC (FormPatternView REQ-PFC-138), DAT | R-39 |
| `TemplateVersionPublished` / `ClauseVersionPublished` | Library version published | code, version, languages, period | DAT | R-39 |
| `BindingChanged` | Binding row activated or ended | product range or type, form, language, template version, period | DAT | R-39 |

### 5.2 Consumed (§8.2)

| Event(s) | From | Reaction |
|---|---|---|
| `ProductVersionPublished` (with `ipidChanged`), `ProductVersionRetired` | PFC | Re-validate bindings (REQ-DOC-123); pre-render IPIDs (REQ-DOC-220) |
| `SubmissionCreated`, `QuoteIssued` | POL | Refresh read model only |
| `PolicyBound/Changed/Cancelled/Voided/Reinstated/Rewritten`, `RenewalOffered`, `PolicyNonRenewed`, `PolicyLapsed`, `CancellationScheduled/Rescinded` | POL | Refresh read model; confirm form sets of bound transactions. **No documents are triggered**: POL requests them by API (A-2), unless a pack type declares an event trigger |
| `TransactionReversed`, `TransactionReapplied` | POL | Mark form sets reversed; no automatic re-issue |
| `ClaimReported` | CLM | Trigger DT-CLAIM-ACK |
| `ClaimsHistoryCertificateIssued` | CLM | Trigger DT-CLAIMS-HISTORY |
| `RefundApproved` | BIL | Trigger DT-REFUND-ADVICE |
| `PaymentReceived` | BIL | Create a **held** DT-PREMIUM-RECEIPT request when the pack flags receipts as fiscal |
| `FiscalDocRegistered`, `FiscalDocRejected` | CMP | Release held documents, or keep the hold and notify (REQ-DOC-213/-215) |
| `CommunicationPreferenceChanged`, `ConsentChanged` | PTY | Invalidate cache; re-resolve pending deliveries |
| `PartiesMerged`, `PartyUnmerged` | PTY | Re-point archive links (REQ-DOC-203) |
| `PartyUpdated` | PTY | Re-resolve queued deliveries (REQ-DOC-235) |
| `DocumentLinked/Verified/Quarantined` | WRK | Refresh InboundDocumentView |
| `LegalHoldApplied/Released` | PLT | Refresh hold cache |
| `ConfigChanged`, `PackActivated`, `TranslationBundlePublished` | PLT/MKT | Refresh config; re-validate language completeness |
| `AiToggleChanged`, `AiKillSwitchActivated` | PLT | Disable AI surfaces within 60 s |
| `DSARReceived` | CMP | Prepare export or restriction |
| `ClockWarned`, `ClockBreached`, `ClockStarted`, `ClockMet`, `ClockElapsed` | CMP | Maintain clock links and statutory risk on deliveries and failure items; never start or stop clocks |
| `CoexistenceMasterChanged`, `MigrationWaveStatusChanged` | MIG | Refresh the coexistence cache |

Explicitly **not consumed**: `RefusalDocumentIssued` (UW).

---

## 6. APIs

### 6.1 Exposed (§9.1)

All commands take an `Idempotency-Key`, return RFC 9457 errors with `DOC-ERR-*` codes, and support dry-run per contract §3.5.3. "E" means exposed externally through the CHN gateway.

| Operation | Purpose | Requirement |
|---|---|---|
| `doc.Document.request` (E) | Main request: type, subject, recipients or resolve, language override, payload supplement, delivery instruction (resolve/suppress/explicit), sync flag, `dryRun` | REQ-DOC-001 |
| `doc.Document.requestBatch` | Batch request, per-item keys; batch id returned in ≤ 2 s | REQ-DOC-176 |
| `doc.Document.preview` (E) | DRAFT rendition, no side effects | REQ-DOC-306, -323 |
| `doc.Document.get` / `listForObject` (E, customer-scoped variant) | Metadata and lists | REQ-DOC-008, -289 |
| `doc.Document.reprint` / `resend` / `renderAsOf` | Copies and as-of rendering | REQ-DOC-205, -211, -208 |
| `doc.Document.hide` / `unhide` / `deleteDraft` / `supersede` / `withdraw` | List hygiene and supersession | REQ-DOC-278, -279, -210, -185 |
| `doc.DocumentType.list` | Catalogue | REQ-DOC-049 |
| `doc.Template.list`, `doc.Clause.get` | Library lookup | REQ-DOC-002 |
| `doc.Forms.infer` (≤ 300 ms p95), `addManual` / `removeManual`, `get` | Forms | REQ-DOC-102, -103, -114 |
| `doc.FormPattern.validateReferences` | PFC lint | REQ-DOC-113 |
| `doc.Binding.resolve` | Template version for a key and date | REQ-DOC-124 |
| `doc.ProofOfCover.issue(transactionId)` | Cover note; idempotent per transaction | REQ-DOC-224 |
| `doc.Ipid.get` (E) | Current IPID | REQ-DOC-220 |
| `doc.Delivery.status`, `doc.Delivery.evidenceFor(subjectRef, purpose)` | Delivery queries; feeds POL's bind gate | REQ-DOC-249 |
| `doc.EvidencePack.get` / `export` | Evidence | REQ-DOC-257 |
| `doc.Signature.createEnvelope` / `send` / `remind` / `void` / `get` (E) | Envelopes | REQ-DOC-006, -260…-266 |
| `doc.Archive.store` / `get` / `verify` / `addRendition` / `setRetentionClass` / `listForObject` | Archive, used by many modules | REQ-DOC-004, -187…-202 |
| `doc.Subject.export` / `restrict` / `erase` | DSAR, called by CMP | REQ-DOC-313, -314 |
| `doc.Document.import` | Migration (dry-run, idempotent, full validation per R-77) | REQ-DOC-311 |
| `POST /api/doc/v1/provider-events/{provider}` | Signed callbacks from email, SMS and signature providers | REQ-DOC-228, -246 |

Error codes named across the PRD:

`DOC-ERR-TYPE-UNKNOWN`, `-PAYLOAD-INVALID`, `-LANGUAGE-NOT-APPROVED`, `-BINDING-MISSING`, `-BINDING-OVERLAP`, `-FORBIDDEN`, `-AI-TEXT-UNACCEPTED`, `-COEXISTENCE-LEGACY`, `-BATCH-INVALID`, `-NOT-FOUND`, `-CHANNEL-NOT-PERMITTED`, `-NO-CONSENT`, `-ASOF-UNSUPPORTED`, `-IMMUTABLE`, `-ALREADY-DELIVERED`, `-CONTEXT-MISSING`, `-NOT-BOUND`, `-SIGNATORY-IDENTIFIER-MISSING`, `-PROVIDER-UNAVAILABLE`, `-HUMAN-SIGNATURE-REQUIRED`, `-HASH-MISMATCH`, `-SCAN-REQUIRED`, `-IMPORT-INVALID`, `-FINAL-OVERRIDE`, `-BINDING-FIRST`, `-DUPLICATE-FORM`, `-GLYPH-MISSING`, `-SENDER-UNREGISTERED`.

Lint codes: `DOC-LINT-CONCAT-001`, `DOC-LINT-MANDATORY-001`.

### 6.2 Consumed

See §4 above. The PLT services include "workflow engine" and "integration hub" (see §14 C2 and C3).

### 6.3 External integrations (§9.3)

| System | Protocol stated | Status |
|---|---|---|
| Email provider (EU) | SMTP+TLS or REST, signed webhooks | Planned |
| SMS aggregator (EU, Greek connectivity) | REST or SMPP via PLT hub | Planned (OI-DOC-04) |
| Print-and-post vendors (2, for failover) | SFTP (PDF/A or PDF print streams + JSON/CSV manifest) and status files; optional REST | Planned (OI-DOC-14) |
| gov.gr co-signing | Government interface, specification only on onboarding (UNVERIFIED) | Dependency (OI-DOC-05) |
| Commercial e-signature (EU QTSP) | REST + webhooks | Planned |
| Qualified time stamps and seal certificates | RFC 3161; certificate in the key-management service | Planned |
| gov.gr digital mailbox | Unknown | Not available (OI-DOC-06) |
| AADE myDATA | Not called by DOC | — |

---

## 7. SPIs / country-pack interfaces

All are MKT-owned SPIs used by DOC (§9.2, §10.2, §10.4).

| SPI | Use | Greece pack | Cyprus stub |
|---|---|---|---|
| `DocumentLanguageRule.rules(type, jurisdiction, customerLanguage)` | Binding and informative languages | Greek binding, English informative on preference | English binding, Greek informative |
| `MandatoryWordingSet.clauses` | Mandatory clause slots and lint | Pre-contractual, non-payment, objection/withdrawal and language statements (e.g. `GR-NONPAY-01`, `GR-LANG-01`) | Smaller synthetic set |
| `ESignatureProvider.select` | Provider and level per type, jurisdiction and signatory | gov.gr co-signing + commercial provider | Simple signature with evidence |
| `RefusalDocumentRule` | Nat-cat refusal template (P3) | Nat-cat rules | NotRequired |
| `ClaimsHistoryFormat` | Certificate fields | Greek fields | Generic |
| `NameTransliterator` | Latin fallback names | ELOT 743 | Same algorithm |
| `AddressFormatter` | Postal label vs document body | Greek postal format | CY format |
| `ConsentRules` | Marketing consent | — | — |
| `NumberingScheme` | Document numbers via `plt.Number.next` | Pack formats | Different prefix |
| `StatutoryDeliveryRule` (new, CCR-DOC-04 / R-41) | Accepted media, proof levels, notification-date derivation (incl. deemed delivery for unclaimed registered mail), evidence items | Registered post for termination notices | Email with read receipt (synthetic) |
| `FiscalDocumentChannel` (CMP-side) | MARK/QR data reaches DOC via CMP | myDATA | No fiscal hold |
| `TaxCalculator.treatment` | **Not called by DOC**: taxes are printed as supplied (D2, REQ-DOC-133) | — | — |

MKT also supplies the casing and lowering language rules (`REQ-MKT-005`). DOC core must contain no Greek-specific casing logic (XMR-F-300). A Bulgaria fixture proves Cyrillic handling and dual-currency rendering (REQ-DOC-145, -154). Architecture test REQ-PLT-323: no DOC code branches on jurisdiction.

---

## 8. Screens

All 17 screens run in the PLT staff shell, support the Greek/English UI switch (R-101), and use cursor pagination and semantic states. Permissions are listed in §6.0.

| ID | Name | Persona | One line |
|---|---|---|---|
| SCR-DOC-01 | Template and clause editor | ROLE-15 (edit), ROLE-46, ROLE-29/-14 | Split-pane structured editor with outline, block palette, dictionary variables, conditions, live preview, tests, where-used, pinned comments, presence and AI drafts |
| SCR-DOC-02 | Wording library | ROLE-15/46/29/14, ROLE-43 RO | Search and work views (My drafts, Awaiting my approval, Stale translations, Effective next 30 days, Retired); XLIFF export/import |
| SCR-DOC-03 | Review and approval with tracked changes | ROLE-46, ROLE-29, ROLE-14, ROLE-15 | Redline per language, side-by-side binding vs informative, affected-document previews, approvals checklist, consistency attestation |
| SCR-DOC-04 | Binding matrix | ROLE-15, ROLE-14 approve | Grid of product versions × forms × languages, with cell states bound/gap/conflict/pending; bulk re-bind |
| SCR-DOC-05 | Form pattern catalogue | ROLE-15/14/29/43 | Replaces SCR-PCADM1-01 |
| SCR-DOC-06 | Form pattern workspace | ROLE-15; ROLE-14/29 approve | Tabs Basics, Applicability, Availability, Inference, Change behaviour, Bindings, Impact, History; coverage picker overlay replaces SCR-PCADM1-03 |
| SCR-DOC-07 | Preview and test suites | ROLE-15/46/14, ROLE-40 | Sample or production-specimen preview, test cases, golden diffs, shadow comparison, branch coverage |
| SCR-DOC-08 | Document-type catalogue | ROLE-15; ROLE-29/26/30 approve; ROLE-42 | Type attributes; layer origin; final attributes are locked |
| SCR-DOC-09 | Batch monitor and failure queue | ROLE-45 | Runs, progress and ETA; failure queue ranked by statutory risk |
| SCR-DOC-10 | Documents tab | CSR, agents, handlers, auditors | Unified inbound/outbound list; replaces SCR-PCPF-07 and SCR-PCSUB2-06 |
| SCR-DOC-11 | Forms panel | ROLE-04/05/06/10/11/03 | Form set with inference reasons; replaces SCR-PCPF-08 and SCR-PCSUB2-05 |
| SCR-DOC-12 | Document viewer and actions | ROLE-03/04/17/31/43 | Reprint, resend, as-of, verify, supersede chain |
| SCR-DOC-13 | Delivery status and evidence pack | ROLE-03/22/23/29/31/43/44 | Per-recipient timeline, evidence, manual evidence, exports |
| SCR-DOC-14 | Signature envelope management | ROLE-04/05/06/08/11/17 | Envelope create, send, remind, void |
| SCR-DOC-15 | Channel configuration | ROLE-45, ROLE-37, ROLE-29 approve | SMS sender IDs, email domains, print vendor profiles, quiet hours, fallback chains |
| SCR-DOC-16 | Archive explorer | ROLE-43, ROLE-30, ROLE-44 | Hash and seal verification, Merkle proof, retention and hold |
| SCR-DOC-17 | Output operations dashboard | ROLE-45, ROLE-38 | Throughput, latency, lanes, provider status |

**Design-guide patterns referenced:** IB-01 (palette), IB-02 (keyboard lists), IB-03 (sidebar work views), IB-04 (priority queue), IB-05 (split pane), IB-07 (approve-the-diff), IB-08 (explain-why), IB-09 (citations), IB-10 (streaming draft), IB-11 (work-left home), IB-12 (progress-to-done), IB-14 (edit in place), IB-16 (density), IB-17 (hero metrics), IB-18 (quick-look overlay), IB-19 (presence), IB-20 (pinned comments), IB-21 (semantic status), IB-22 (AI-suggested state), IB-23 (state-change motion), IB-24 (quiet chrome), IB-26 (global filter bar), IB-28 (exceptions-first), IB-29 (dense list), IB-32 (binary status / pass-fail), IB-33 (theme).

**Inventory coverage (§6.18, §15.3).** DOC owns 9 inventory items: 8 Guidewire reference screens plus UIL-U11. All 9 are covered: 8 specified, 1 replaced wholly (SCR-PCADM1-03), 0 missing. Workers' Compensation and Inland Marine menu entries are marked not applicable in Greece.

**Notable permission strings:**

`doc.read`, `doc.document.request`, `.hide`, `.resend`, `.reprint`, `.asof`, `doc.author`, `doc.approve.legal.<lang>`, `doc.approve.compliance`, `doc.approve.product`, `doc.forms.edit`, `doc.forms.manual`, `doc.binding.edit`, `doc.catalogue.edit`, `doc.ops`, `doc.channel.admin`, `doc.archive.audit`, `doc.signature.manage`, `doc.evidence.read`.

Two more are used in screens but missing from the §6.0 list: `doc.preview.prod` (SCR-DOC-07) and `doc.evidence.manage` (SCR-DOC-13). See §14 C12.

---

## 9. Regulatory, tax and statutory rules

### 9.1 Stated explicitly in the source

| Rule | Source text / value | IDs |
|---|---|---|
| IDD information on **paper by default**; another durable medium or a website only under conditions; paper copy free on request | IDD Art. 23; Law 4583/2018 Art. 33 (§3 paper on request, §5 website conditions) | REQ-DOC-226, -227, -231, -236; BR-DOC-010/-011/-012 |
| Website delivery needs consent, an electronic notification of the address and location of the information, and continued availability for the configured period | Default `doc.delivery.website_availability_days` = 365 (core) | BR-DOC-012 |
| IPID format | Implementing Reg. (EU) 2017/1469: fixed title, headings and icons in a fixed order; **two sides of A4, exceptionally three**; overflow is a failure, never a silent truncation | REQ-DOC-219, -164; BR-DOC-014 (max 3 pages, EU final). Article numbers UNVERIFIED (OI-DOC-07) |
| Intermediary name, AFM, register number (and coordinator) on applications and policy schedules | Law 4583/2018 Art. 28 §3 | REQ-DOC-132 |
| Policy is proof of the contract; provisional cover document allowed if agreed; minimum contents; terms delivered with the policy | Law 2496/1997 Art. 2 §1–§4 | REQ-DOC-033 (one policy pack with contents list), -216, -206 |
| Termination for non-payment requires written notice; cover ends **one month after notification** | Law 2496/1997 Art. 6 §2. DOC supplies the notification date; BIL owns the clock | REQ-DOC-038, -250…-256; BR-DOC-020 |
| Greece pack: non-payment, insurer cancellation, non-renewal and mortgagee notices by **registered post with tracking, plus an electronic copy**, until counsel approves electronic-only | Market practice; UNVERIFIED (OI-DOC-03) | REQ-DOC-252; BR-DOC-021; `doc.statutory.electronic_only` = false |
| Greek only for documents submitted to the supervisor | Law 4364/2016 Art. 262. Greek binding for customer documents is a pack rule, not statute (AC-1) | REQ-DOC-140 |
| Distance sales: pre-contractual information on a durable medium before conclusion; terms immediately after conclusion if the customer asked for immediate cover; record which case applied | Directive 2023/2673; Law 5317/2026 Art. 70 (not re-read, OI-DOC-16) | REQ-DOC-221 |
| Withdrawal acknowledgement on a durable medium **within 1 minute**, timestamp from the PLT time service, one per withdrawal request id | R-84 | REQ-DOC-044 |
| Distance withdrawal refund: Greece full premium (R-55) | Via `PolicyLifecycleRules` | DT-VOID-NOTICE row |
| Fiscal documents show **MARK + QR** (QR contains the MARK) | AADE A.1035/2020 as reflected in A.1126/2024. Insurer scope UNVERIFIED; closed operationally by D3 | REQ-DOC-213, -214, -217 |
| CMP is the **only** issuer of fiscal series and numbers; premium receipts are MARK-before-delivery (`cmp.fiscal.mark_before_issue`, `bil.fiscal.mark_before_delivery`) | D3 | REQ-DOC-213, -214 |
| BIL payment notice is **non-fiscal** ("ειδοποίηση πληρωμής", never "τιμολόγιο"); prints the BIL payment-demand number, never a fiscal number | D3, XMR-F-127 | DT-INVOICE, REQ-DOC-214, -326 |
| Cover note is **non-fiscal**, shows **no premium or invoice amounts**, states it is not a tax document; its DOC document number is the statutory cover-note number | D3, XMR-F-130; subject to the D2 opinion | REQ-DOC-216; BR-DOC-018 (core, final) |
| Cover note validity "until delivery of the policy documents and at most N days" | N = 30 default, UNVERIFIED (OI-DOC-10) | BR-DOC-015; `doc.cover_note.validity_days` |
| Taxes, levies and stamp duty printed exactly as supplied by the originating module's single `TaxCalculator.treatment` call | D2 | REQ-DOC-133 |
| Green Card on plain white paper, fixed pack layout, Latin names "as on ID document" | Directive 2009/103/EC (as amended by 2021/2118); Council of Bureaux rule UNVERIFIED (OI-DOC-08) | REQ-DOC-036, -147; BR-DOC-016 |
| Claims-history certificate on request; reasoned offer or reply within three months | Contract (Verify, CLM) | REQ-DOC-041, -042 |
| Complaint reasoned reply within **50 calendar days** | BoG Executive Committee Act 88/5.4.2016 (FEK B 1109/19.04.2016), verified by CMP. Deadline text comes from `ComplaintRules` | REQ-DOC-045, -253 |
| Nat-cat answer within 30 days; refusal documents | Law 5116/2024 Art. 5 as amended by Law 5162/2024 Art. 25; JMD 96806/2025 (refusal rules UNVERIFIED in PRD-04) | REQ-DOC-043, -254 (P3) |
| gov.gr co-signing (legal persons upload a document, citizens sign with TaxisNet; output has the Ministry's advanced electronic seal, verification code and QR) | MD 1785/2025, Gov. Gazette B 2742/03.06.2025, Art. 4A; service live September 2025 | REQ-DOC-262 |
| eIDAS seal and signatures | Reg. 910/2014 as amended by 2024/1183 | REQ-DOC-167, -260…-274 |
| Marketing SMS and email need consent and an unsubscribe | Directive 2002/58/EC; Law 3471/2006 | REQ-DOC-239; BR-DOC-033 |
| Registered alphanumeric SMS sender IDs; operators block unregistered or mismatched IDs | EETT measures; decision number UNVERIFIED (OI-DOC-04) | REQ-DOC-241; BR-DOC-030 |
| SMS encoding: GSM-7 160/153; UCS-2 **70/67**; UCS-2 when Greek lower-case or accented characters are present | 3GPP TS 23.038 (citation not opened) | REQ-DOC-240; BR-DOC-031 (max segments default 3) |
| Accessibility: tagged PDF, PDF/UA (ISO 14289), HTML alternative at WCAG 2.2 AA | EAA Directive 2019/882 / Law 4994/2022, treated as applying (R-57) | REQ-DOC-290…-297 |
| AI-assistance statement mandatory on every customer document containing AI-drafted text; packs set only wording and position | AI Act Art. 50 and Art. 26; contract §3.8.4 | REQ-DOC-068; BR-DOC-070 |
| GDPR minimisation, retention, erasure-vs-retention conflict (restrict instead) | GDPR Arts 5(1)(c), 5(1)(e), 15, 17, 18, 25; Law 4624/2019 | REQ-DOC-128, -191, -200, -313…-315 |
| Providers are ICT third parties: register, degraded modes, exit plan, two print vendors | DORA 2022/2554 | REQ-DOC-247; NFR-DOC-010 |
| EU residency of providers and archive | GDPR Ch. V | NFR-DOC-012 |
| Exportable approval histories and evidence packs for Bank of Greece | OBL-BOG (Verify) | REQ-DOC-090, -257 |
| Statutory clocks start, stop or are met from **proven delivery** | D7: DT-CLAIM-OFFER → `CLM_MTPL_PAYMENT_DUE`; DT-CANCEL-NOTICE → `POL_INSURER_TERMINATION_NOTICE` (+ `POL_MTPL_THIRDPARTY_NOTICE`); DT-CHANGE-ACCEPTANCE → `UW_AMENDMENT_ACCEPTANCE`; DT-RENEWAL-OFFER / DT-NONRENEWAL → Met of `POL_RENEWAL_NOTICE` / `POL_NONRENEWAL_NOTICE`; distance-sale terms → `POL_WITHDRAWAL_DISTANCE` (long stop if not delivered). Cites Act 87/2016 Art. 6 and Law 2496/1997 Arts 3–4 | REQ-DOC-325, -042 |
| Renewal offer must state that payment of the renewal premium is acceptance, and how to accept explicitly | D7 | DT-RENEWAL-OFFER row |
| PDF/A-3 archive rendition with the payload embedded; PDF/UA | ISO 19005-3; ISO 14289 | REQ-DOC-157, -291 |

### 9.2 Referenced but not specified (gaps)

- Greek retention durations for RC-DOC-*. Deferred to the programme retention schedule (XMR-D-259), loaded as pack data before system test.
- Notification-date derivation for unclaimed registered mail: "deemed date after N days" (BR-DOC-020) with N not given.
- Seal level, advanced or qualified (OI-DOC-15, BR-DOC-007).
- Whether insurer premium documents legally require MARK/QR. UNVERIFIED; handled operationally by D3 plus the D2 opinion as a go-live gate.
- Cover-note acceptance by traffic police / Information Centre and its maximum validity (OI-DOC-10).
- Green Card layout and paper rule text (OI-DOC-08).
- IPID article numbers (OI-DOC-07).
- Content of Law 5317/2026 Art. 70 (OI-DOC-16).
- EETT sender-ID decision (OI-DOC-04).
- gov.gr co-signing API (OI-DOC-05). Private-insurer access to the gov.gr mailbox (OI-DOC-06).
- Court-accepted proof of electronic notification (OI-DOC-03).
- Legal basis for Greek as the binding language (OI-DOC-02).
- The "pack" text of mandatory clauses (e.g. `GR-NONPAY-01`, `GR-LANG-01`): codes are named, wording is not given.
- The deadline in DT-COMPLAINT-ACK comes from `ComplaintRules`; no acknowledgement deadline is stated in DOC.

### 9.3 Deferred to configuration or the country pack

Durations, deadlines and media rules are pack data (§3.1 header). Specifically:
- `DocumentLanguageRule`, `MandatoryWordingSet`, `StatutoryDeliveryRule`;
- fiscal flag per type;
- fallback chains and confirmation windows (defaults: email 48 h, SMS 24 h, post 15 days; BR-DOC-025);
- quiet hours (21:00–09:00);
- website availability days;
- cover-note validity;
- hold alert hours (24);
- batch windows (22:00–06:00) and interactive reserve (30%);
- retry policy (5 attempts, exponential, 30 s base);
- integrity sample rates (1/365 hash daily; 0.1% re-render);
- preview payload retention (30 days);
- `doc.channel.govgr.enabled` (false);
- email attachment policy (ATTACH_IF_NO_P2);
- seal level;
- numbering formats;
- form edition pattern (Greece `YYYY-NN`).

---

## 10. Greek-market specifics

### AFM
- Printed for intermediaries (REQ-DOC-132).
- Used to declare gov.gr co-signatories (REQ-DOC-262). The identifier is shown masked on SCR-DOC-14 and is required for gov.gr.
- Excluded from payloads where not printed (REQ-DOC-128 example).

### myDATA
DOC never calls AADE. It holds fiscal-flagged documents (premium receipt, fiscal credit note, others the tax specialist designates) in `Requested.AwaitingFiscal` until `FiscalDocRegistered`, then prints CMP's series, number, MARK and QR exactly as supplied.
- Hold alert after 24 h with a WRK activity (REQ-DOC-215).
- The cover note stays valid during a myDATA outage (RT-08).
- Example given: series A-PR, number 1047, MARK 400001234567890 (REQ-DOC-214).

### gov.gr
- **Co-signing:** Must, P1 (REQ-DOC-262), depends on onboarding (OI-DOC-05). Fallback is the commercial provider (RK-DOC-04).
- **Digital mailbox:** a Could adapter (REQ-DOC-244), behind a capability switch that defaults off (OI-DOC-06).

### Information Centre / bureau
Only mentioned in OI-DOC-10 (cover-note acceptance) and OI-DOC-08 (Hellenic Motor Insurers' Bureau Green Card guidance). Not otherwise integrated by DOC.

### Greek language and typography (CAP-DOC-07; §14.x Greek text suite)
- **Binding language:** Greek binding, English informative on preference. Every informative version carries the `GR-LANG-01` "binding version prevails" statement (REQ-DOC-140/-141). There is no language fallback, ever (REQ-DOC-061, BR-DOC-001).
- **Four settings, independent:** language, region format, jurisdiction and currency (REQ-DOC-142).
- **Formats:** el-GR is dd/mm/yyyy, decimal comma, thousands dot, € after the amount with a space ("1.234,56 €"). Uses pinned CLDR (REQ-DOC-143).
- **Upper case:** CLDR `el-Upper` removes tonos and other accents in all-caps; title case keeps accents. Implemented through MKT-supplied language rules with no Greek logic in the DOC core (REQ-DOC-145, XMR-F-300). Example: "Ασφαλιστήριο Οχήματος" → "ΑΣΦΑΛΙΣΤΗΡΙΟ ΟΧΗΜΑΤΟΣ". The dialytika retention case ("Ευρωπαϊκή") is in the test suite.
- **Final sigma** on lower-casing, via Unicode SpecialCasing Final_Sigma; user-entered final sigma in names is preserved (REQ-DOC-146).
- **Names:** native script, plus Latin "as on ID document" from PTY. Falls back to the ELOT 743 `NameTransliterator` only when that form is missing, and the fallback is flagged in metadata (REQ-DOC-147). Example: "HRISTOS", not "Christos".
- **Addresses:** `AddressFormatter`, postal-label vs body purpose, in the destination country's script (REQ-DOC-148).
- **Fonts:** only the declared versioned font set may be used. A glyph-coverage check at render time fails with `DOC-ERR-GLYPH-MISSING` (example U+1F00, polytonic) (REQ-DOC-149). Fonts are subset and embedded with ToUnicode, so extracted text equals the source in NFC (REQ-DOC-150).
- **Hyphenation and line breaking:** Greek and English rules; never hyphenate identifiers, amounts or plates (REQ-DOC-151, Should).
- **Collation:** UCA with CLDR tailoring (REQ-DOC-152, Should).
- **Punctuation:** Greek question mark (;) and ano teleia (·) stored canonically after NFC (U+037E normalises to U+003B), with lint for misuse (REQ-DOC-153, Should).
- **Amounts in words** with grammatical gender, e.g. "χίλια διακόσια πενήντα ευρώ" (REQ-DOC-144, Should).
- **Search:** accent-insensitive in the library and the Documents tab (REQ-DOC-069, -276).
- **Glossary check** (REQ-DOC-326, Must). Approval is blocked for legally wrong Greek terms:
  - "τιμολόγιο" must be "ειδοποίηση πληρωμής" for a non-fiscal demand;
  - a complaint is "παράπονο", not "καταγγελία";
  - termination by notice is "καταγγελία σύμβασης".
- **Document language follows the recipient's PTY preference, never the staff UI switch** (REQ-DOC-324, R-101). Staff may preview either language; an English copy on request records the reason.
- **ICU MessageFormat** whole sentences with plural and select. Concatenating translatable fragments is a lint error (REQ-DOC-053). Labels come from the MKT translation store (REQ-DOC-060).
- **Greek SMS:** UCS-2 at 70/67 characters per segment (REQ-DOC-240). Test cases: 71 characters → 2 segments (RT-15); boundaries at 70/71 and 134/135.

### EUR
All examples are in EUR. Dual-currency display is P4 Could (BG fixture, REQ-DOC-154).

### Other Greek-specific items
- EETT sender register.
- Registered post via the print vendor (ELTA services, OI-DOC-14).
- `DT-GREEN-CARD` per vehicle (REQ-DOC-036).
- Glossary terms (§1.7).

---

## 11. Controls

### Authority types registered with PLT (§12)

| Authority | Dimensions | Use |
|---|---|---|
| DOC.STATUTORY_SUPPRESS | document type, legal entity | Suppress or withdraw a statutory notice |
| DOC.MANUSCRIPT | product, line of business | Manuscript endorsements (P3) |
| DOC.BULK_RESEND | count, document type | Bulk resend above 100 documents |

### Maker-checker list (§12; accepted into contract §3.9.2 by R-45 / CCR-DOC-03)
- Template, block and clause version approval per language, and publication.
- Emergency correction with **two checkers** plus an incident or change reference (REQ-DOC-086).
- Form pattern edition publication and retirement (product owner + compliance).
- Binding changes (product owner as checker).
- Document-type changes to statutory, fiscal, retention or channel attributes (compliance, tax specialist, DPO respectively).
- Suppression or re-addressing of statutory notices.
- Channel identities and print vendor switch.
- Golden baseline approval.
- Rendering profile and font-set changes.
- Manual evidence on statutory deliveries.
- Legal-hold release (PLT).

Approval routing is in BR-DOC-005:
- statutory and pre-contractual clauses → legal + compliance;
- coverage clauses → legal + product owner;
- templates and blocks → forms-owner checker.

### Segregation of duties
- No one approves a version they edited.
- A translator cannot approve the binding language unless they are also a legal approver for it and not the editor.
- Whoever changes a binding cannot approve it.
- Output operations users cannot edit templates.
- Senders of statutory notices cannot add manual evidence to the same notice.
- AI agents hold no approval, signature or suppression rights (REQ-PLT-111, REQ-DOC-273).

### Audit (REQ-DOC-092, §12)
Every library action; document requests including actor, on-behalf-of and AI agent; reprint, resend, as-of, hide and supersede; delivery suppression, re-route and manual evidence; archive store, view, download, verify, reclassification, restrict and erase; envelope actions; channel config; golden approvals; production-data preview; batch control.

### Integrity
- SHA-256 per item.
- Write-once storage with retention lock and per-legal-entity encryption keys (REQ-DOC-187).
- Daily Merkle root anchored in the PLT audit anchor (REQ-DOC-195, Should).
- Daily rolling hash verification covering every item yearly, plus a 0.1% re-render sample. Any mismatch is a security incident (REQ-DOC-194, NFR-DOC-009).
- Signed single-use download links valid ≤ 5 min (REQ-DOC-196).

### GDPR
- Payload minimisation per dictionary with a P-class per field. P2 and P3 fields are refused unless the type declares the purpose; IBAN is masked to the last 4 digits (REQ-DOC-128).
- No P2 data in email attachments; a portal link is used instead (REQ-DOC-229, BR-DOC-026).
- SMS carries at most first name and document reference (REQ-DOC-242).
- DSAR export (REQ-DOC-313). Restrict or erase with per-item outcome (REQ-DOC-314). Retention-vs-erasure conflict → restrict (REQ-DOC-200).
- Preview and failed payloads deleted after 30 days (REQ-DOC-315).
- Pseudonymised copies or synthetic documents in non-production; SPECIMEN overlay forced there (REQ-DOC-169, -316).
- No personal data in events or logs (NFR-DOC-012).
- Hidden documents are never deleted (REQ-DOC-278). Issued documents are deleted only by the retention engine (REQ-DOC-279).

---

## 12. AI features (§11)

All features go through the PLT gateway to EU endpoints with zero retention. Each is registered in the CMP AI register and the DAT model registry before enabling. All have a kill switch (60 s) and **default off**. DOC is fully functional without AI (REQ-DOC-319). There is no AI call during payload building or rendering (REQ-DOC-139). AI text enters a customer document only as an accepted payload supplement with an `AiInteractionRecord` in state Accepted or Edited (REQ-DOC-320).

| ID | Feature | Classification | Phase (§1.5) | Needed for MVP? |
|---|---|---|---|---|
| AI-DOC-01 | Clause drafting / plain-language rewrite | Limited risk (Art. 50) | P2 | No |
| AI-DOC-02 | Translation draft (GR ⇄ EN); terminology accuracy ≥ 98%; ICU error rate must be 0 | Limited risk | **P1 pilot, off by default** | No (XLIFF and human translation is the fallback). Helps the legal bottleneck (RK-DOC-01) |
| AI-DOC-03 | Wording change impact explainer | Minimal risk | P3 | No |
| AI-DOC-04 | Readability and alt-text assistant | Limited risk | **P1 pilot, off by default** | No (deterministic lint REQ-DOC-295 and manual alt text) |
| AI-DOC-05 | Customer "explain my policy" | Limited risk with **high-risk-level controls**; citation accuracy ≥ 99%; unsupported claims ≤ 0.5% | P2 | No |
| AI-DOC-06 | Delivery-failure triage | Uncertain → high-risk-level controls; trained on 12 months of masked failure history | P3 | No |

Customer statements are given verbatim in GR/EN for AI-DOC-01 and AI-DOC-05. The core default statement clause is `CORE-AI-01`.

---

## 13. Open issues / assumptions / CCRs

### Open issues (§16.1)

| ID | Topic | Status |
|---|---|---|
| OI-DOC-01 | Fiscal coupling | **Closed per D3**. The D2 tax/legal opinion remains a go-live gate (XMR-F-413) |
| OI-DOC-02 | Legal basis for Greek binding | Open (ROLE-46) |
| OI-DOC-03 | Proof media for non-payment and cancellation notices | Open; counsel |
| OI-DOC-04 | EETT sender-ID register | Open |
| OI-DOC-05 | gov.gr co-signing onboarding and API | Open (programme dependency) |
| OI-DOC-06 | gov.gr mailbox for private insurers | Open |
| OI-DOC-07 | IPID article numbers | Open |
| OI-DOC-08 | Green Card CoB rule | Open |
| OI-DOC-09 | Greek retention periods | Closed by citation (XMR-F-329, XMR-D-259) |
| OI-DOC-10 | Cover-note max validity and acceptance | Open; needs Settled legal status for production (D7 gate) |
| OI-DOC-11 | Complaint 50 days | Closed |
| OI-DOC-12 | Nationwide source | Optional |
| OI-DOC-13 | EAA applicability | Closed (R-57) |
| OI-DOC-14 | Print vendor Greek registered mail with e-proof (ELTA) | Open |
| OI-DOC-15 | Seal level | Open |
| OI-DOC-16 | Law 5317/2026 Art. 70 content | Open |
| OI-DOC-17 | MKT SPIs Must P1 | Closed |
| OI-DOC-18 | AIC-004 alignment | Closed |
| OI-DOC-19 | `DT-PRICING-STATEMENT` added | Closed |

### Assumptions (§16.2)
- A-1: CMP supplies MARK and QR.
- A-2: POL requests contractual documents by API, not by event.
- A-3: BIL owns the non-payment clock.
- A-4: WRK malware-scans files before storage.
- A-5: PTY returns the legally permitted medium.
- A-6: Planning volumes hold; WRK inbound volume is an estimate.
- A-7: EU email, SMS, print and QTSP providers can be procured for P1.
- A-8: The design guide supplies brand themes.

### Assumptions/conflicts with the prompt (§3.3)
AC-1…AC-10, all resolved in the text. AC-9 makes Payload a DOC-private entity. AC-10 introduces the sub-states.

### CCRs (§16.4), all Accepted
- CCR-DOC-01 (R-39): 8 new events.
- CCR-DOC-02 (R-44): FormSet/FormInstance and DataDictionary cross-module visible; sub-states.
- CCR-DOC-03 (R-45): maker-checker additions.
- CCR-DOC-04 (R-41): `StatutoryDeliveryRule` SPI.
- CCR-DOC-05 (R-46): glossary terms.

### "Ten decisions before building" (§16.5)
1. Fiscal coupling: done by D3; D2 opinion pending.
2. Statutory proof media.
3. Seal level and QTSP.
4. gov.gr onboarding and the commercial e-signature provider.
5. Two print vendors.
6. Email and SMS providers, and EETT registration.
7. Retention periods.
8. Staffing the legal wording programme with a freeze date.
9. Template language and engine architecture: "foundation decided by D10 (.NET core; CEL-compatible rule expressions; **in-house engine**)"; remaining items are PDF/A-3 level A with embedded payload and licensed font sets with full Greek coverage.
10. PFC/DOC binding governance.

### Risks
RK-DOC-01…08. Top risks: legal approval capacity (High/High), statutory proof rejected in court, renewal capacity.

---

## 14. Conflicts and ambiguities found

### (a) With the infra/stack (ARCHITECTURE-DECISIONS.md overrides PRD wording)

**C1 — In-house rendering engine vs Gotenberg (major).**
The PRD says:
- §1.2: "a rendering engine built from scratch";
- §1.7: "DOC's from-scratch layout and output component";
- REQ-DOC-155 (Must): "a rendering engine built in-house … without any third-party document engine product", with the acceptance criterion "no dependency on a commercial document engine exists";
- §16.5 item 9: "in-house engine (D10)".

These all derive from contract CD-20. The binding stack instead mandates "HTML templates rendered to PDF/A by headless Chromium (**Gotenberg** container)", and INFRASTRUCTURE.md sets `DocRender__Url=http://gotenberg:3000`.

Resolution needed:
- Reinterpret the "engine" as DOC's template-tree → HTML/CSS composer (in-house, .NET) plus Gotenberg/Chromium as the paginator and PDF writer. Gotenberg is open source, not a "commercial document engine", so the REQ-DOC-155 acceptance criterion can arguably still pass, but the "without any third-party document engine product" wording cannot.
- REQ-DOC-155's layout features then map to CSS paged media in Chromium: widows/orphans, keep-together, page masters, footnotes. Footnotes and "page x of y" per section are limited in Chromium; see C1a.

**C1a — Gotenberg capability gaps against Must requirements** (needs a technical spike; these are not stated in the PRD):

| Requirement | Gap |
|---|---|
| REQ-DOC-157 (PDF/A-3 **level A**, accessible, with the payload embedded as an associated file + XMP metadata) | Gotenberg's PDF/A conversion produces "b"-level conformance (PDF/A-1b/2b/3b) as far as is known. Level A plus tags plus embedded files likely needs post-processing in .NET or a different path |
| REQ-DOC-291 (PDF/UA) | Chromium tagged-PDF output must survive PDF/A conversion |
| REQ-DOC-160 (byte-identical re-render: fixed document ids, creation timestamp from the payload) | Chromium/Gotenberg write creation dates and IDs. Needs deterministic metadata injection, or must define "identical" on a normalised form |
| REQ-DOC-161 (old engine versions runnable for the full retention) | Means keeping pinned Gotenberg/Chromium image digests runnable for decades |
| REQ-DOC-149 (glyph-missing must fail) | Chromium silently falls back to system fonts. The container must hold only declared fonts and DOC must pre-check coverage in .NET |
| REQ-DOC-151 (Greek hyphenation) | Depends on Chromium hyphenation dictionaries in the container |
| REQ-DOC-167 (seal after PDF/A, LTV) | Must be done in .NET with a PAdES-capable library. Library and licence choice is undecided; iText is AGPL/commercial |
| REQ-DOC-170 (≥ 500-page streaming, bounded memory) | Hard with a single Chromium render |
| REQ-DOC-062 (address window per vendor profile) | Feasible with CSS |

**C2 — "PLT workflow engine"** (J-04 diagram; REQ-DOC-175 "batch runs as PLT workflows" with partitions and checkpoints; §9.2 "workflow engine"). The stack has no workflow server: Hangfire plus domain tables. Batch checkpoints, partitions and resume must be modelled as `doc` tables with Hangfire jobs.

**C3 — "Topic `doc.events.v1`; partition key …"** (§8) and "event stream" are broker vocabulary. The stack uses an outbox dispatched in order to in-process handlers, with no broker. Map "topic" to an event-type namespace and "partition key" to an ordering key.

**C4 — "PLT integration hub" / SFTP via hub** (REQ-DOC-246, J-04), and SMPP via the hub. There is no hub service in the infra; the `worker` integration adapters play this role. SFTP to print vendors is not in INFRASTRUCTURE.md (no SFTP client or service listed).

**C5 — Storage and recovery:**
- REQ-DOC-204 requires replication of the archive to a second EU location. INFRASTRUCTURE.md storage is LRS; geo-redundancy is only listed under "any real customer data" upgrades.
- REQ-DOC-187 requires per-legal-entity encryption keys. Infra has one storage account and customer-managed keys only in Stage 2.
- Infra "soft delete on" in a container with immutability policies needs checking against "no delete except retention purge".
- Retention-engine purge after expiry must work within Azure immutability semantics: the policy must be time-based retention matching the RC class, or a legal-hold style.

**C6 — Capacity.** NFR-DOC-003 asks for about 80,000 documents/h (≥ 40,000 terms/h) and "≥ 100 documents/s sustained per stamp". Infra sizes Gotenberg at 1 vCPU / 2 GiB with 0–2 replicas (scale to zero). Scale-to-zero also threatens the cover-note budget (render ≤ 3 s p95, available ≤ 10 s p95) and preview ≤ 700 ms p95 (NFR-DOC-018) because of cold starts.

**C7 — Key-management service** for seal keys with dual control (NFR-DOC-011). Azure Key Vault Standard (infra) holds software keys. A qualified seal may require an HSM / QSCD (Key Vault Premium/Managed HSM or a remote QTSP seal) — undecided (OI-DOC-15).

**C8 — Vendor references.** Quadient and Guidewire are cited as references only (CD-20 compliant), not as dependencies. No Kafka, Camunda, microservices or lakehouse dependencies in DOC.

**C9 — SMS provider** is undecided in infra (decision #4, "Mocked until chosen"). Email is Azure Communication Services Email per infra, while the PRD keeps "provider selection by procurement". Webhook and event semantics (delivered, bounce, complaint) must be mapped to ACS events.

### (b) With the system contract / other PRDs (noticed)

- Contract CD-20 ("built from scratch … no document engines") and D10 conflict with ARCHITECTURE-DECISIONS (Gotenberg). The infra doc says it overrides the PRD pack, but the contract is labelled "binding". Needs an explicit programme ruling.
- Contract text (line 51) says "core language Kotlin or .NET", "custom-built identity service", "lakehouse". These are overridden by the stack (.NET, Entra ID). DOC references identity only via PLT permissions, so impact is low.
- DOC states that bind never calls DOC synchronously except to request the cover note asynchronously (§14 intro). Yet REQ-DOC-159 offers synchronous rendering for the cover note, and G-1 requires ≤ 60 s. This is consistent but subtle: POL must not block bind on DOC.
- `RefusalDocumentIssued`: DOC tells UW it "may remove DOC from that event's consumers" (§8.2). This implies UW's PRD still lists DOC as a consumer, which is a minor cross-PRD inconsistency.

### (c) Internal contradictions and inconsistencies

| # | Issue |
|---|---|
| C10 | REQ-DOC-008's acceptance criterion says "W the user lacks P3 permission" with no data-class context beyond inbound rows. Fine, but the Documents tab target (1.5 s p95) is defined only for ≤ 500 documents (NFR-DOC-006) |
| C11 | §1.5 phases AI-DOC-02 and -04 as "P1 pilot, off by default"; §11 has no phase column. Consistent but implicit |
| C12 | Permissions `doc.preview.prod` (SCR-DOC-07) and `doc.evidence.manage` (SCR-DOC-13) are used but not in the §6.0 permission list |
| C13 | The catalogue has P2 document types (DT-RI-LOSS-NOTICE, -EVENT-NOTICE, -BORDEREAU, -SOA, DT-MORTGAGEE-NOTICE) but no P2 requirements. REQ-DOC-252 (P1 Must) includes the **mortgagee notice**, whose document type is P2 |
| C14 | Envelope statuses: §7.3.4 has Draft/Sent/Completed/Declined/Expired/Voided, with Viewed/Signed per signatory. REQ-DOC-271 lists "Draft, Sent, Viewed, Signed, Declined, Expired, Voided" per signatory. SCR-DOC-14 states mix both (adds "Completed") |
| C15 | Delivery diagram J-08 (§4.8) differs from §7.3.2: §7.3.2 adds Pending → Failed and Sent → Failed on window expiry. Use §7.3.2 |
| C16 | The DT-COVER-NOTE channel is "STD + SMS link", while REQ-DOC-242 forbids the plate in SMS. The cover note itself contains plate and VIN, so the SMS must be a link only (consistent if respected) |
| C17 | REQ-DOC-215 hold alert is 24 h, while REQ-DOC-213's example releases after 40 s. The cover note stays valid. The hold itself has no maximum; documents can wait indefinitely on `FiscalDocRejected` |
| C18 | BR-DOC-052 says "hash 1/365 of items per day minimum", REQ-DOC-194 says "covering every item at least yearly". Consistent; scheduling detail is left to the build |
| C19 | REQ-DOC-044 requires the withdrawal acknowledgement "within 1 minute"; there is no NFR row for it, but it is on the T1 path (NFR-DOC-007) |
| C20 | REQ-DOC-160 says "byte-identical", but reprints add an overlay layer (REQ-DOC-168) and REQ-DOC-235 re-renders the address block "as a cover sheet change". Reproducibility applies to the archive rendition only, which needs care in the design |
| C21 | §16.6 says 18 NFRs and 38 BRs, matching the tables. §5.20 counts match. No count inconsistencies found |

### (d) Cannot be built without a decision

1. **Engine architecture ruling.** Gotenberg/Chromium versus REQ-DOC-155 "in-house, no third-party engine", and how PDF/A-3a, PDF/UA, embedded payload, determinism and the glyph check are achieved (C1/C1a).
2. **PAdES seal library and seal level/QTSP** (OI-DOC-15), plus key custody (C7).
3. **Statutory proof media** (OI-DOC-03) and `StatutoryDeliveryRule` values, including the deemed-delivery N days.
4. **Providers:** SMS (with EETT sender registration), print vendors with registered mail and returns data (two of them), commercial e-signature QTSP, gov.gr co-signing API (no specification public).
5. **Greek retention durations** (programme schedule) to configure blob immutability per RC class.
6. **Mandatory clause texts** for the Greece `MandatoryWordingSet` and the Green Card layout; content comes from legal.
7. **Cover-note validity N** and the D2 opinion before production.
8. **The template language format.** REQ-DOC-050 requires a "declarative template language" text artefact with a schema; it is not specified. Must be designed, e.g. JSON/YAML tree to HTML.
9. **The CEL-compatible expression language** (D10) shared with PFC and UW is a dependency. Its evaluator must exist (PLT rules runtime) before conditions and inference work.

---

## 15. Build notes

### Hardest parts
1. **Deterministic, accessible, archival rendering.** PDF/A-3a + PDF/UA + embedded payload + seal + LTV, byte-reproducible across engine versions for decades, with Greek typography. With Gotenberg, determinism and conformance need post-processing in .NET plus pinned images. Golden-document CI over the full estate in ≤ 2 h (NFR-DOC-017).
2. **Authoring tool.** Structured block editor, ICU messages, condition builder, character-level redline per language, pinned comments, presence, per-language approval routing, Stale propagation, previews of affected documents. This is heavy front-end work (React).
3. **Delivery orchestration with legal evidence.** Medium resolution against PTY consent, fallback chains with confirmation windows, proof levels, notification-date derivation, sealed (and supplementary) evidence packs, print vendor SFTP manifests and reconciliation, registered-mail returns.
4. **Form inference engine** (decision tables in CEL, availability rows, jurisdiction replacement, endorsement numbering, change behaviour) at ≤ 300 ms p95, with impact analysis over open quotes and renewals.
5. **Batch throughput:** about 80,000 documents/h with lanes, pinning, checkpoints and print cuts, on Hangfire.
6. **Archive** used by every module: immutability, retention classes, legal hold, Merkle day roots, DSAR and erasure conflicts, migration import of about 2 million legacy PDFs.

### Must exist first
- PLT: numbering, audit, approvals/maker-checker, authority, legal hold, retention catalogue, time service, outbox, Hangfire.
- MKT: configuration and the SPIs `DocumentLanguageRule`, `MandatoryWordingSet`, `ESignatureProvider`, `NameTransliterator`, `AddressFormatter`, `NumberingScheme`, `StatutoryDeliveryRule`; translation store; l10n formatting.
- PTY: party, names in two scripts, preferences, consent, producer codes.
- PFC: catalogue, IPID data, product version events.
- POL: snapshots and the job API.
- CMP: fiscal events.
- The shared CEL evaluator.
- The blob immutable container and Gotenberg (both present in infra).

### Mandatory P1 motor documents
DT-QUOTE, DT-APPLICATION (ES), DT-IPID, DT-PRECONTRACT-INFO, DT-INTERMEDIARY-INFO, DT-DEMANDS-NEEDS, DT-COVER-NOTE (SEAL, non-fiscal), DT-POLICY-SCHEDULE (SEAL), DT-POLICY-WORDING, DT-GREEN-CARD, DT-INSURANCE-CERT (Should requirement REQ-DOC-037 for the QR verification page), DT-PREMIUM-RECEIPT (fiscal, held for MARK), DT-ENDORSEMENT, DT-CHANGE-ACCEPTANCE, DT-RENEWAL-OFFER, DT-NONRENEWAL (STAT), DT-CANCEL-NOTICE (STAT when insurer-initiated), DT-VOID-NOTICE, DT-WITHDRAWAL-ACK, DT-REINSTATEMENT, DT-INVOICE (non-fiscal payment notice), DT-CREDIT-NOTE, DT-BILLING-STATEMENT, DT-NONPAY-NOTICE (STAT), DT-REFUND-ADVICE, claim types (ACK, OFFER, DENIAL, RESERVATION-OF-RIGHTS, SETTLEMENT, CLAIMS-HISTORY), complaint types (ACK, REPLY), DT-INTERMEDIARY-CHANGE, DT-COMMISSION-STMT, DT-ACCOUNT-CURRENT, DT-DSAR-EXPORT, DT-INCIDENT-CLIENT, DT-GENERAL-LETTER, DT-PRICING-STATEMENT, DT-PRODUCT-SPEC.

That is 41 P1 types. Policy pack = schedule + wording forms as one item with a table of contents (REQ-DOC-033). In the journey (J-01) the policy pack is schedule, wording, Green Card and receipt, released after `FiscalDocRegistered`.

### Delivery channels in P1
Email, SMS, portal inbox, print-and-post, registered post via the print vendor, intermediary copy (portal inbox + webhook API), and hand-over in branch (Should). gov.gr mailbox is Could and off by default. Quiet hours apply.

### Consent for e-delivery
- Paper is the default (REQ-DOC-226, BR-DOC-010).
- A durable medium other than paper, or website delivery, only if PTY returns it with valid consent proof.
- Website delivery additionally needs a notification and availability (REQ-DOC-231, -236).
- A standing paper request creates an extra free paper copy (REQ-DOC-227).
- The consent proof reference and rule version are stored on each Delivery and in each evidence pack (REQ-DOC-258).
- Marketing requires channel consent plus an unsubscribe (REQ-DOC-239).
- Statutory notices re-resolve preferences at delivery time; no cache.

### Suggested slicing (tracer bullets)

| Slice | Content |
|---|---|
| S1 | Archive API (store, get, verify; blob immutable; hash; retention class; legal-hold check), used early by WRK and others |
| S2 | Minimal request pipeline: document type catalogue (seeded), data dictionary, payload freeze, template tree → HTML → Gotenberg PDF/A, numbering, `DocumentRendered`, Documents tab read-only. Target the cover note end-to-end (`doc.ProofOfCover.issue`, T1 path, ≤ 10 s) plus the Greek text CI suite and golden tests |
| S3 | Delivery: email (ACS) and portal inbox, PTY resolution, `DocumentDelivered` / `DeliveryFailed`, fallback chain. Pre-contractual pack + IPID pre-render + `evidenceFor` for POL's bind gate |
| S4 | Fiscal hold/release with CMP (premium receipt), policy pack with forms. Form patterns + inference + binding matrix (seeded via admin first, full governance UI later) |
| S5 | Statutory notices: registered post via a print vendor adapter (SFTP manifests and status files), proof levels, evidence pack sealing (seal library), non-payment / cancellation / non-renewal |
| S6 | Library authoring: editor, redline, per-language approvals, publication, Stale, glossary check, lint. Until then templates are loaded as reviewed text artefacts |
| S7 | Batch runs (Hangfire partitions, pinning, failure queue, print cuts) at renewal scale; capacity test |
| S8 | E-signature (commercial provider first; gov.gr once onboarded), SMS with registered sender, as-of rendering, reprint/resend, DSAR/erasure, migration import, accessibility hardening (PDF/UA, HTML alt), ops dashboard |
| Later | P3 nat-cat, manuscript endorsements, large-schedule streaming; P4 dual currency, filing records; AI features |
