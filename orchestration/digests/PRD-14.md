# Digest — PRD-14 Platform: identity, audit, integration, workflow, observability and DORA operations (PLT)

Source: `core-insurance-prds/PRD-14-platform-identity-audit-operations.md` v1.4 (2026-10-07, "Candidate for build baseline 1.0"; binding input contract v1.11). Read in full, lines 1–2791 (EOF). The digest also used `core-insurance-infra/ARCHITECTURE-DECISIONS.md` and contract §3.4.1 (event envelope). INFRASTRUCTURE.md was **not** read, so only conflicts with the ADR are flagged.

---

## 1. Identity

- **Module code:** PLT. **Title:** Platform — identity, audit, integration, workflow, observability and DORA operations.
- **Purpose (§1.2):** The shared technical and control foundation that every business module depends on. The PRD lists: a **custom-built identity service** with two realms (staff; external for customers, agents, brokers and bank staff); authorisation (RBAC from app roles plus ABAC through one policy library); the shared **authority framework** (`plt.Authority.check`, CD-05) and **maker-checker** service; the append-only, hash-chained **business audit trail**; the **event backbone** (transactional outbox, relay, schema registry, idempotent consumers, DLQ, replay); the integration hub / adapter host; a **workflow engine** and decision-table runtime; the **configuration runtime and feature flags** (CD-06); **reference data** (calendars, holidays, currencies, FX, geography; CD-17); the **numbering service**; the **AI control plane** (CD-11: gateway, toggles, kill switch, interaction records); **data-protection services** (retention, legal hold, erasure, pseudonymisation); observability and SLOs; release and change control; deployment stamps and DR; security engineering; **DORA operations** (incident register, timers, register of information, third-party risk, resilience testing); architecture fitness; administration tools and the staff application shell; and the Greek/English language switch (R-101). "No business logic lives in it."
- **Scope decisions:** CD-05, CD-06, CD-10, CD-11, CD-17, CD-20 ("built from scratch, vendor-neutral").
- **Non-goals / out of scope (§1.4):**
  - Business rules, entities and screens belong to each module.
  - The configuration model, layer semantics, key types, configuration approval and the configuration explorer screen belong to MKT (REQ-MKT-001). PLT hosts the runtime and supplies the trace API (A5).
  - Business capability switches belong to MKT (REQ-MKT-004).
  - Holiday content, rounding rules and regime code lists are MKT pack data. PLT stores and serves them.
  - Groups, queues, assignment, activities and approval activities in queues belong to WRK.
  - Global search and the command-palette back end belong to WRK (REQ-WRK-006).
  - The statutory clock register belongs to CMP (REQ-CMP-003). PLT supplies the engine and calendars.
  - DSAR orchestration belongs to CMP. PLT supplies retention, erasure and pseudonymisation.
  - The AI system register belongs to CMP (REQ-CMP-007). The model registry, drift and bias belong to DAT (REQ-DAT-005).
  - Regulatory filings other than DORA belong to CMP.
  - Analytics, "lakehouse" and marts belong to DAT.
  - Customer and intermediary portal screens belong to CHN, built on PLT identity APIs.
  - Agency hierarchy rules belong to PTY and CHN.
  - The document rendering engine belongs to DOC.

## 2. Size metrics

| Metric | Count | Notes |
|---|---|---|
| Functional REQs | **352** (REQ-PLT-001…015 anchors; 030…366; 016–029 reserved, unused) | §5.23; verified by script |
| — Must / Should / Could | **298 / 47 / 7** | [BASELINE] 207/7/2 = 216; [ENHANCEMENT] 91/40/5 = 136 |
| — By phase (actual tally) | **P1 = 346** (Must 296, Should 43, Could 7); P2 = 1 (REQ-PLT-061 Should); P3 = 2 (REQ-PLT-062, -107 Should); P4 = 3 (REQ-PLT-272 Must, -279 Must, -280 Should) | §5.23 says "349 in P1, 3 in P4", which is wrong; see §14c |
| **Motor MVP (P1) requirements** | **346 total; 296 Must P1** | P1 = motor MVP (contract R-86) |
| Business rules | 44 (BR-PLT-001 … BR-PLT-044) | §10.1 |
| NFRs | 33 (NFR-PLT-001 … 033) | §14 |
| Screens | 28 (SCR-PLT-01 … 28) | covers 27 owned inventory items (21 reference + 6 UI-library) |
| Owned entities | about 60 (catalogue entities plus about 34 PLT-private † entities, CCR-PLT-01) | §7.1 |
| Events produced / consumed | 19 rows (20 events, because LegalHoldApplied/Released share a row) / 14 rows (about 18 events) | §8 |
| Configuration keys | 31 | §10.2 |
| Inbound operation families | about 50 rows in §9.1 | plus OIDC, SCIM, in-process libraries |
| Authority types registered by PLT | 5 | §12.2 |
| AI features | 7 (AI-PLT-01 … 07), all shipped off | §11 |
| Open issues / risks / CCRs | 15 OI / 11 RK / 8 CCR (all accepted) | §16 |

**Build-size estimate: L (very large; the largest platform PRD).** 298 Must requirements, 28 screens, plus several subsystems that are each sizeable on their own: identity, event backbone, workflow and decision runtime, a CEL-compatible expression language, the audit hash chain with qualified sealing, DORA tooling and the AI gateway. Even after the ADR replaces the custom identity service with Entra ID, the scope stays L.

## 3. Owned entities (§7.1; every row also carries `legal_entity_id`, `jurisdiction`, `created_at`, `created_by`, `record_version`; ids are UUIDv7; effective-dated rows use half-open `[valid_from, valid_to)` with UTC record time, D5)

### 3.1 Identity and access
- **User** (RC-ID-STAFF / RC-ID-EXT):
  - `user_id` (= OIDC `sub`), `realm` (STAFF|EXTERNAL, immutable)
  - `population` (STAFF, CUSTOMER, COMMERCIAL_CUSTOMER_USER, INTERMEDIARY, BANK_STAFF, SERVICE)
  - `username` (unique per realm, lowercase); given, middle and family name, prefix, suffix (native script; Latin form via the `NameTransliterator` SPI)
  - `employee_number`, `party_id` (PTY link), `organisation_ref`, `legal_entity_ids[]` (≥1)
  - department, `job_code`, `manager_user_id`, `work_email`, phones (E.164)
  - **`ui_language`** (BCP 47 `el`|`en`), `ui_language_source` (SWITCH, PREFERENCES, ADMIN, DEFAULT), `ui_language_changed_at`
  - `preferences` json (region_format, region_format_explicit, jurisdiction, currency, density, theme)
  - `status`, `status_reason`, `start_date`, `end_date`, `last_sign_in_at`, `terms_version_accepted`, `terms_accepted_at`
  - **State machine (§7.3):**
    - Invited→Active (enrolment); Invited→Disabled (expired or cancelled)
    - Active→Locked→Active (unlock by time, self-service MFA or admin)
    - Active→Suspended (admin or licence event) → Active (reactivate; privileged users need approval)
    - Active/Suspended→Disabled (leaver or deletion); Disabled→Active (rehire within 90 days, approval)
    - Disabled→Archived (retention)
    - Guards: Active needs ≥1 authenticator that meets policy and, for staff, ≥1 legal entity. Suspend and Disable revoke sessions and tokens within 60 s (REQ-PLT-041).
    - Events: `UserProvisioned`, `UserAccessChanged`, `UserDeprovisioned`.
- **Authenticator †:**
  - type: PASSKEY, SECURITY_KEY, PLATFORM, TOTP, EMAIL_OTP, SMS_OTP, RECOVERY_CODE, PASSWORD
  - public key or verifier hash (P2, encrypted), aaguid, backup-eligible flag, status Active/Revoked
- **Session †:** device fingerprint hash, truncated IP, acr, amr, start/last activity/end, end reason.
- **ClientApplication †:** type (staff FE, external FE, partner, service, AI agent); auth method `private_key_jwt` | `tls_client_auth` | none+PKCE; redirect URIs; scopes; token lifetimes.
- **FederationConnection †:** OIDC or SAML2, issuer, attribute mapping, JIT flag, certificate expiry.
- **Permission:** `permission_code` PK formatted `<mod>.<resource>.<action>`; module; name and description EN/GR; `data_class_touched` P0–P3; privileged flag; `introduced_in_version`.
- **Role:** type STAFF/INTERMEDIARY/CUSTOMER/BANK_STAFF/SERVICE; internal_only; owner; privileged (derived); status Active/Retired; version.
- **RolePermission**.
- **RoleAssignment †:** scope (legal entities, jurisdictions, LoBs, producer codes); valid_from/to; source BUNDLE/REQUEST/JIT/DELEGATED_ADMIN; approval_request_id; status.
- **RoleBundle †:** job_code → roles.
- **SodRule † / SodException †:** rule_code; left/right side (permission, role or duty); check_at GRANT/ACTION/BOTH; severity BLOCK/WARN. Exceptions carry a compensating control, approver and valid_to (≤ 12 months, SCR-PLT-16).
- **AccessReview / AccessReviewItem †:** review states Draft/Active/Closed; item decision KEEP/REVOKE/MODIFY/UNDECIDED.
- **PrivilegedGrant †:** Requested→Approved→Active→Expired; Active→Revoked; Requested→Rejected.
- **ServiceIdentity †:** kind module/job/adapter/AI agent; owner; scopes; workload-attestation binding; expiry; review date.

### 3.2 Authority framework (CD-05)
| Entity | Attributes |
|---|---|
| AuthorityType | `type_code` PK, `owning_module`, name EN/GR, `dimensions` (JSON schema: product, lob, amount+currency, sum insured, territory, deviation_pct, transaction_type, flag), allowed comparisons, description |
| AuthorityProfile | profile_id, code, name, description, owning_area (UW, Claims, Billing, Finance, Pricing, RI), valid_from/to, version, status Active/Retired |
| AuthorityLimit | limit_id, profile_id, type_code, dimension filters (json), comparison **AT_MOST, AT_LEAST, IN_SET, FLAG, ANY**, value (money / decimal / set), description |
| AuthorityGrant | grant_id, profile_id or explicit limit_id, grantee (user_id or role_id), valid_from/to, approval_request_id |
| AuthorityDelegation † | delegator, delegate, type_codes, max values, valid_from/to, revoked_at (depth 1, never above delegator, BR-PLT-014) |
| **AuthorityCheckResult †** | `check_id`, `actor` (user, on-behalf-of, AI actor), `type_code`, dimension values, `object_ref`, `as_at`, `decision`, `reason_code`, `limit_id`, grant or delegation id, `referral targets`, `fx_rate_id`, `decided_ref` (attached later by the module through `plt.Authority.attachDecision`), `checked_at` |

**`plt.Authority.check` contract (REQ-PLT-003, REQ-PLT-103, §9.1):**
- Inputs: `(actor, authorityType, dimensions, objectRef, asAt)`.
- Output: `decision` ∈ {allow, refer, deny}, `reason code` (e.g. `AMOUNT_ABOVE_LIMIT`), `applicable limit` with its source grant, `referral target` (users, or role and group, holding enough authority), `check id`.
- Performance: **≤ 50 ms p95, p99 ≤ 100 ms** (NFR-PLT-002).
- Errors: `PLT-ERR-UNKNOWN-TYPE`.
- Money conversion: monetary limits are evaluated in the limit's currency. The amount is converted at the REFERENCE FX rate of the check date, falling back to the latest prior rate (BR-PLT-015), and the `rate id` is recorded (REQ-PLT-106).
- AI agents are never granted authority. For AI-initiated actions the check runs on the delegating person, and any legal or financial effect needs that person's confirmation, otherwise `PLT-ERR-HUMAN-DECISION-REQUIRED` (REQ-PLT-111).
- Check results are stored for at least the retention of the decision they support (REQ-PLT-110).
- Related operations: `plt.AuthorityType.register` (build-time manifest and runtime; `PLT-ERR-TYPE-EXISTS`), `plt.Authority.attachDecision` (`PLT-ERR-CHECK-EXPIRED`), `plt.Authority.whoCanApprove`, `plt.AuthorityProfile.*`, `plt.AuthorityDelegation.*`.

### 3.3 Maker-checker
- **ApprovalType †:**
  - `approval_type_code`, `owning_module`, checker permission or authority type
  - `checkers_required` (1 or 2), threshold rule, expiry in business days (default 5, BR-PLT-016)
  - `bulk_eligible`; maker-may-withdraw and diff-display flags (REQ-PLT-113)
- **ApprovalRequest:**
  - `request_id`, `type_code`, `object_ref`, `maker_user_id`, `maker_actor` (user or AI-originated flag)
  - **`content_hash` (SHA-256)**, `payload_ref`, `diff` (json), `reason`, `status`
  - `decisions[{checker_user_id, decision, comment, decided_at, acr}]`, `due_at`, `correlation_id`
- **State machine:**
  - Draft→PendingApproval (hash fixed; `ApprovalRequested`)
  - PendingApproval→Approved, Rejected or Returned (`ApprovalDecided`)
  - Returned→PendingApproval (new hash)
  - PendingApproval→Withdrawn or Expired
  - Approved→**Consumed** (the module executes with a matching hash)
  - Approved→Expired (not executed within validity)
  - Note: REQ-PLT-114 lists states Draft→PendingApproval→Approved/Rejected/Returned/Withdrawn/Expired, without "Consumed".
- **Refusals:**
  - Maker, any editor of the content version, or the maker's delegate tries to decide → `PLT-ERR-SELF-APPROVAL` or `PLT-ERR-EDITOR-CANNOT-APPROVE` (REQ-PLT-115).
  - Service or AI identity tries to be checker → `PLT-ERR-CHECKER-MUST-BE-HUMAN` (REQ-PLT-121).
  - Payload hash differs at execution → `PLT-ERR-APPROVAL-HASH-MISMATCH` via `plt.Approval.verifyForExecution` (REQ-PLT-117).
- **Approval operations:** `plt.ApprovalType.register`, `plt.Approval.request/decide/withdraw/get/list`. Reject and return need a comment.

### 3.4 Audit
**AuditEvent** (RC-AUD-BUS; immutable; REQ-PLT-002, -123, §7.1):

| Attribute | Type | Notes | PD |
|---|---|---|---|
| audit_id | UUIDv7 | PK | P0 |
| partition, sequence | int, bigint | position in the chain | P0 |
| prev_hash, hash | bytes(32) | SHA-256 chain per stamp and partition | P0 |
| actor_type | enum USER, SERVICE, AI_AGENT | | P0 |
| actor_id, on_behalf_of_id | UUID | | P1 |
| role_codes, authority_check_id | text[], UUID | | P0 |
| operation | text `<mod>.<Resource>.<operation>` | | P0 |
| object_type, object_id, object_number | text, UUID, text | | P0–P1 |
| changes | json `[{field, before, after, pd_class}]` | P2/P3 values encrypted under a per-subject key | P1–P3 |
| reason | text | | P1 |
| channel | shared Channel code list (CHN-owned, R-84): STAFF, WEB_DIRECT, APP, BROKER_PORTAL, AGENT_PORTAL, BANK_BRANCH, BANK_EMBEDDED, PARTNER_API, AGGREGATOR, AI_AGENT, CONTACT_CENTRE | | P0 |
| correlation_id, causation_id | text (W3C trace id) | technical only (D5) | P0 |
| ai_interaction_id | UUID nullable | | P0 |
| occurred_at (business), recorded_at (UTC) | timestamptz | | P0 |
| subject_key_refs | UUID[] | used for cryptographic erasure | P0 |
| (+ legal_entity_id, jurisdiction from the §7 preamble) | | | |

**Fields required by other requirements but missing from the table:**
- `origin=MIGRATION` (REQ-PLT-345 requires it "on every … audit record").
- The **business lineage keys** of the affected object (REQ-PLT-363: "the audit library records the business keys of the affected object").

**Audit write path:**
- `plt.Audit.append` is in-process and writes in the **same DB transaction** as the business change, into a per-module **audit outbox** (`plt_audit_outbox`, REQ-PLT-365).
- A mover then transfers records to the **central audit store**, which is write-once with a retention lock and has no update or delete API (REQ-PLT-124, -126).
- **AuditAnchor †:** date, partition, `merkle_root`, `record_count`, qualified time-stamp token, seal reference. There is a daily Merkle root sealed by a qualified time stamp stored outside the DB (REQ-PLT-125).
- Overhead: audit append ≤ 5 ms p95 (NFR-PLT-004).
- Query: `plt.Audit.query` covers 90 days in ≤ 2 s p95 (REQ-PLT-128). Volume is 200 M records per year per stamp (NFR-PLT-007).
- Not audit events: `plt.Authority.check` results are stored as AuthorityCheckResult and are not duplicated as audit unless used in a decision (§12.1).

### 3.5 Events: outbox records and related tables (REQ-PLT-136, §7.1)
- **OutboxMessage**, held in *each module's schema* and named **`plt_outbox`** (REQ-PLT-365):
  - `outbox_id`, `event_id` (UUIDv7), `aggregate_type`, `aggregate_id`
  - `sequence` (gap-free per aggregate, assigned at write time, REQ-PLT-139)
  - `event_type`, `schema_version`, `envelope` (json, contract §3.4.1), `payload` (json ≤ **256 KB**)
  - `partition_key`, `created_at`, `published_at`, `publish_attempts`
  - Retention class RC-TMP after publication; published rows are purged after 7 days (REQ-PLT-149, Should).
- **Envelope (contract §3.4.1):**
  - `event_id`, `event_type`, `schema_version`, `occurred_at`, `recorded_at`, `producer`
  - `legal_entity_id`, `jurisdiction`, `aggregate_type`, `aggregate_id`, `sequence`
  - `correlation_id`, `causation_id`, `actor`, `ai_interaction_id`, `configuration_hash`, `payload`
  - `origin` (e.g. MIGRATION)
  - The PLT library fills the envelope from context and rejects events with missing mandatory fields (REQ-PLT-147).
  - For D4 set events, `set_id`, `set_size` and `index` are also carried (REQ-PLT-366).
- **SchemaVersion:** event_type, major, minor, JSON Schema, producer_module, compatibility_mode, declared PD fields (each field P0–P3), registered_at, status. Lineage-key fields are marked in the registry (REQ-PLT-363). Types are named topic-qualified, e.g. `wrk.DocumentReceived` (REQ-PLT-142).
- **ConsumerCheckpoint †:** consumer_group, topic, partition, position.
- **ProcessedEvent †** (`plt_processed_event`): consumer_group, event_id, processed_at. Kept for 30 days beyond stream retention.
- **Idempotency record:** `plt_idempotency_record`.
- **DeadLetter:**
  - dead_letter_id, consumer_group, event_id, event_type, aggregate_id, error_class, error_message (no PD), attempts, parked_at
  - status Parked→Replayed | Parked→DiscardPending→Discarded (approved) or back to Parked
  - resolution reason, approval_request_id

### 3.6 Integration, workflow, rules, configuration
- **AdapterDefinition †:** adapter_code, external_system, owner, protocol, endpoints per environment, credential_ref, rate_limit, degraded_mode QUEUE|BLOCK, breaker settings, third_party_id, status.
- **ExchangeRecord †** (RC-INT-ARCH): request and response as encrypted blob refs, hashes, idempotency_key, correlation_id, purpose, data categories, pseudonymised party index.
- **WorkflowDefinitionVersion † / JobDefinition † / JobRun †:** JobRun status Running→Completed/PartiallyFailed/Failed/Stopped; PartiallyFailed→Running (retry failed partitions). Triggers SCHEDULE/MANUAL/EVENT; partition counts.
- **DecisionTableVersion †:** content_hash, hit_policy, IO schema, rules, test cases, approval_ref, effective dates. Status Draft/Tested/Active/Retired.
- **RuntimeProperty †:** group, name, environment, type, value or secret_ref, version, change_record_id.
- **FeatureFlag:**
  - flag_key, description, type RELEASE / OPS_KILL / STAFF_PILOT, `regulated_behaviour`, owner, created_at, expires_at (release flags ≤ 6 months), targeting rules json, state per environment
  - Status Active→Retired; retiring needs the flag OFF everywhere and no code references.

### 3.7 Reference data and numbering
- **Calendar:**
  - code (e.g. GR-PUBLIC, GR-BANK, TARGET, GR01-COMPANY), kind COUNTRY/REGION/SETTLEMENT/ENTITY, weekend_days, time_zone, version, pack_version
  - Versions move Draft→PendingApproval→Published→Superseded and publish `ReferenceDataPublished`.
- **Holiday:** origin PACK_RULE|EXCEPTION, rule_text, zones, types (public, bank, settlement, company).
- **Currency:** ISO 4217 code, numeric code, minor units, names EN/GR.
- **FxRate:** rate decimal(18,10); type REFERENCE/MONTH_END/AVERAGE/GROUP; source; published_at; status Published/Fallback/Corrected/Superseded; corrected_from_id; approval_request_id.
- **GeographyEntry †.**
- **NumberingSeries †:** pattern, prefix, check-digit algorithm, range, reset_rule, gap_policy, current_value, block_size, reserved ranges, voided numbers.

### 3.8 AI control plane (CD-11)
- **ModelEndpoint:** provider (ICT third-party id), region (EU only), model_id and version (DAT ref), retention and training terms, approved data classes, status.
- **AiFeatureRuntime †:**
  - `ai_system_id` (the CMP `AiSystem` entry is the system of record, R-82)
  - toggle scope, gateway route, prompt template version, timeout_ms, daily_budget
  - automatic response to drift or bias (AUTO_DISABLE|FLAG)
  - cached register status with read time
  - off reason MANUAL / KILL_SWITCH / DRIFT / BIAS / REGISTER_STATUS
- **AiToggle:** toggle_id, feature_id or GLOBAL, level **TENANT, LEGAL_ENTITY, LOB, ROLE, USER**, level_ref, state ON|OFF, changed_by, reason, approval_request_id, changed_at.
- **AiInteractionRecord** (RC-AI-INT):
  - interaction_id, feature_id, model_id, model_version, template_version
  - input_hash, input_pointer (no raw P2/P3), output (encrypted where it holds PD), confidence, latency_ms
  - recommendation_state **Proposed→Accepted/Edited/Rejected/Expired**
  - final_value_ref, decision_maker_user_id, user_id, timestamps, correlation_id, fallback_used, cost units

### 3.9 Data protection
- **RetentionPolicy:** rc_code, jurisdiction, trigger_event, duration (ISO 8601, pack value), action DELETE/ANONYMISE/ARCHIVE, approved_by DPO, valid_from, version.
- **LegalHold:** scope (object refs, party ids, query), reason. States Requested→Active→ReleasePending→Released; release needs two checkers.
- **PurgeRun †:** due, purged, held and failed counts.
- **ErasureTask †:** dsar_ref; status Received/Executing/Completed/PartiallyCompleted; keys_destroyed; retained_due_to.

### 3.10 DORA and operations
- **IctIncident** (renamed from `Incident`, R-94 / CCR-PLT-08):
  - incident_number, source, severity SEV1–4, detected_at, aware_at, classified_at
  - classification MAJOR/NOT_MAJOR/PENDING, criteria_met json, recurring_group_id
  - affected services and functions, member_states
  - impact figures (clients, counterparts, transactions count and value, duration, downtime, costs)
  - data_loss flags, malicious_access, reputational flags, exercise flag, root_cause, lessons
  - breach_assessment {flag, aware_at, dpo_decision, notified_at, cmp_clock_instance_id}
  - **States:**
    - Detected (`IncidentDeclared`) → Triaged (owner, aware_at) → Classified (`IncidentClassified`)
    - Classified → Reported (major only, `IncidentReported`) → Mitigated
    - Classified → Mitigated (not major)
    - Mitigated → Resolved (root cause) → Closed (major: final report Submitted or Acknowledged, actions tracked)
    - Detected → Closed (false positive)
    - Classified → Classified (reclassify with reason; new timers)
- **IncidentReport:**
  - kind INITIAL / INTERMEDIATE / FINAL / CLIENT_INFO / CYBER_THREAT; version; due_at; content json per template; channel; receipt_ref
  - States Draft→PendingApproval→Approved→Submitted→Acknowledged; Submitted→Rejected→Draft
- **IctAsset**, **IctThirdParty** (identifier LEI/EUID/VAT), **ThirdPartyArrangement †**, **BcPlan †**.
- **DrTest:** types RESTORE, FAILOVER, STAMP_MIGRATION, CHAOS, EXIT_PLAN, PENTEST, SCENARIO, TLPT.
- **ChangeRecord:**
  - STANDARD/NORMAL/EMERGENCY; author, implementer and approver; artefact digest; rollback evidence
  - States Draft→PendingApproval→Approved→Scheduled→InProgress→Completed/RolledBack/Failed; Emergency InProgress→Completed→RetroApproved
- **DataChangeRequest †:** states Draft→DryRunPassed→PendingApproval→Approved/Rejected; Approved→Executing→Completed/Failed; Failed→Draft; Approved→Expired after 72 h (BR-PLT-040).
- **TimeOffset †** (non-production only).
- **SloDefinition †.**

## 4. Consumed entities / dependencies (§7.2, §9.2)

| From | What | How |
|---|---|---|
| PTY | Party (identity link, DSAR subject); Intermediary, ProducerCode (ABAC scope, licence suspension via read model `IntermediaryAccessView`); Account (commercial org) | `pty.Party.get/search`, `pty.ProducerCode.validate`, `pty.ProducerOfRecord.get`, `pty.Account.get`; events `PartiesMerged`, `PartyUnmerged`, `IntermediaryLicenceChanged`, `ProducerOfRecordChanged` |
| WRK | Group, Queue, Delegation, Activity | `wrk.Group.list`; activities created through events (`ApprovalRequested` etc.); `ActivityCompleted`, `SLABreached` |
| MKT | ConfigKey/Value/Hash; Pack, SpiBinding; translation store; rounding (REQ-MKT-006); golden-suite harness | `mkt.Configuration.currentHash`, `mkt.Spi.bind`; events `ConfigurationActivated`, `PackActivated`, `PackRolledBack`, `RateTableChanged`, `TranslationBundlePublished` |
| CMP | AiSystem (gate); DsarRequest; clock engine; submission tracker | `cmp.AiSystem.get`, `cmp.Clock.start/stop` (`CMP_GDPR_BREACH_NOTIFY`), `cmp.Submission.create/recordFiling`; events `AiSystemStatusChanged`, `DSARReceived` |
| DAT | Model, ModelVersion | `dat.Coverage.status`; events `ModelDriftDetected`, `BiasThresholdBreached`, `ModelVersionRegistered`, `ModelVersionApproved`, `LakehouseErasureCompleted` |
| DOC | Archive for evidence; client notices | `doc.Archive.store`, `doc.Document.request` |
| Every module | purge, export and correction APIs | retention engine, DSAR export, data change |

## 5. Events (§8; topic `plt.events.v1`; partition key = stable aggregate id, R-100)

### 5.1 Produced
| Event | Status | Trigger | Key payload | Consumers |
|---|---|---|---|---|
| ConfigChanged | Catalogue | Resolved value set of a context changes (activation or effective-date rollover at midnight in the jurisdiction's time zone) | key(s), layer, old hash, new hash, effective_at; partition = `resolution_context_id` | all 16 modules |
| FeatureFlagChanged | Catalogue | flag state or targeting changes | flag_key, env, old/new state, actor | CHN, DAT, FIN, MKT, WRK |
| AiToggleChanged | Catalogue | toggle change; automatic disable on drift, bias or register status | feature, level, level_ref, new state, actor, reason (e.g. REGISTER_STATUS) | all |
| AiKillSwitchActivated | Catalogue | global or per-feature kill | scope, actor, reason, activated_at | all |
| IncidentDeclared | Catalogue | incident created | number, severity, services, detected_at | CMP, DAT, MKT, WRK |
| IncidentClassified | Catalogue | classification decided or changed | class, criteria met, timer due dates | CMP, DAT |
| IncidentReported | Catalogue | report submitted | kind, version, submitted_at, receipt ref | CMP, DAT |
| RetentionPurgeCompleted | Catalogue | purge run per RC code and module | rc code, module, due/purged/held/failed | CMP, DAT, PTY |
| UserProvisioned | R-02 | user created (Invited) | user id, population, legal entities, job code, org ref | DAT (the J2 text also shows WRK receiving it) |
| UserAccessChanged | R-02 | role, scope or status change | change type, roles ±, new status | WRK |
| UserDeprovisioned | R-02 | user Disabled | reason class, effective_at | WRK |
| AuthorityGrantChanged | R-02 | profile, limit, grant or delegation change | profile, type codes, users affected, valid period | BIL, CLM, DAT, FIN, RAT, UW, WRK |
| ApprovalRequested | R-02 | request submitted | type, object ref, eligible checkers (role or permission), due_at | DAT, WRK |
| ApprovalDecided | R-02 | decided, withdrawn or expired | decision, checker, decided_at | BIL, CLM, DAT, FIN, RAT, RI, WRK |
| LegalHoldApplied / LegalHoldReleased | R-02 | hold activated or released | scope refs, reason class | BIL, CLM, DAT, DOC, FIN, MIG, RAT, RI |
| ReferenceDataPublished | R-02 | calendar version, FX set or currency published | kind, code, version, effective dates | DAT, FIN, RI |
| DeadLetterParked | R-02 | message parked | event type, consumer, error class | DAT |
| JobRunFailed | R-02 | run Failed or PartiallyFailed | job, run id, failed partitions, error class | DAT |
| ChangeDeployed | R-02 | production deploy done or rolled back | change id, modules, versions, outcome | DAT |

### 5.2 Consumed
| Event | Producer | Reaction |
|---|---|---|
| PartiesMerged / PartyUnmerged | PTY | re-point identity links within 1 min (REQ-PLT-067) |
| IntermediaryLicenceChanged | PTY | suspend or reactivate all agency users within **5 min** (REQ-PLT-071, BR-PLT-013) |
| ProducerOfRecordChanged | PTY | refresh ABAC scope cache |
| ConfigurationActivated / PackActivated / PackRolledBack | MKT | refresh config cache within 30 s; compute hashes; emit ConfigChanged |
| RateTableChanged | MKT | refresh reference caches |
| DSARReceived | CMP | prepare subject export and erasure tasks |
| ModelDriftDetected / BiasThresholdBreached | DAT | automatically disable or flag the feature within 60 s (REQ-PLT-227) |
| AiSystemStatusChanged | CMP | if Suspended or Retired: tenant-level toggle OFF within 60 s of `occurred_at`, publish AiToggleChanged(REGISTER_STATUS); if Approved: refresh cache only (stays off until re-enabled) — REQ-PLT-354 |
| ModelVersionRegistered / ModelVersionApproved | DAT | mark endpoints for review or approved |
| ActivityCompleted | WRK | close incident actions and review tasks |
| SLABreached | WRK | escalate approvals nearing expiry |
| LakehouseErasureCompleted | DAT | append an audit record linked to the ErasureTask; no state change (R-97) |
| TranslationBundlePublished | MKT | refresh shell and sign-in translations without restart |

## 6. APIs (§9; REST `/api/plt/v1/...`; commands carry `Idempotency-Key`; RFC 9457 errors with codes `PLT-ERR-*`)

**Exposed (main groups):**
- **Identity:**
  - OIDC endpoints: authorize, token, userinfo, JWKS, introspect, revoke, end-session, back-channel logout, PAR
  - `plt.Token.exchange` (RFC 8693, AI agents); `plt.Token.grantAgentScope` (internal)
  - SCIM `/scim/v2/Users`, `/Groups`
  - `plt.User.create/update/suspend/reactivate/disable/get/search` (dry-run shows SoD and approvals)
  - `plt.ExternalUser.register/link/recover/invite`
  - `plt.UserProfile.getPreferences` / `setLanguage` (`PLT-ERR-UNSUPPORTED-LANGUAGE`)
- **Access:** `plt.Role.*`, `plt.RoleAssignment.grant/revoke`, `plt.Policy.evaluate` (in-process; permit/deny, reason, policy version; ≤ 1 ms p95), `plt.AccessReview.*`, `plt.PrivilegedAccess.request/approve/revoke`.
- **Authority and approvals:** `plt.AuthorityType.register`, `plt.Authority.check`, `plt.Authority.attachDecision`, `plt.Authority.whoCanApprove`, `plt.AuthorityProfile.*`, `plt.AuthorityDelegation.*`, `plt.ApprovalType.register`, `plt.Approval.request/decide/withdraw/get/list`, `plt.Approval.verifyForExecution`.
- **Audit:** `plt.Audit.append` (in-process), `plt.Audit.query/export/verify`, `plt.Lineage.walk(startKey)`.
- **Events:** `plt.Outbox.publish` (in-process; `PLT-ERR-SCHEMA`, `PLT-ERR-SCHEMA-PII`), `plt.Schema.register/check` (CI; `PLT-ERR-INCOMPATIBLE`), `plt.Consumer.replay`, `plt.DeadLetter.list/replay/discard`.
- **Integration:** `plt.Adapter.invoke` (a workflow activity; `PLT-ERR-CIRCUIT-OPEN` retryable), `plt.Exchange.search`, `plt.Transfer.list/retry`.
- **Workflow and rules:** `plt.Workflow.start/signal/query/cancel/terminate`, `plt.Job.run/stop/schedule/history`, `plt.DecisionTable.evaluate/test/activate/explain`, `plt.Expression.check/evaluate` (CEL-compatible).
- **Configuration:** `mkt.Configuration.resolve` / `explain` are *implemented* by PLT; `plt.Config.resolve/trace/reconstruct` are internal (XMR-F-102). Also `plt.FeatureFlag.evaluate` and admin operations, and `plt.RuntimeProperty.*`.
- **Reference data:**
  - `plt.Calendar.isBusinessDay/addBusinessDays/nextBusinessDay/previousBusinessDay/businessDaysBetween/endOfPeriod` (returns calendar versions); `plt.Calendar.generate/publish`; `plt.Holiday.addException`
  - `plt.Fx.getRate(from, to, date, type)` (triangulation via EUR; returns rate id; `PLT-ERR-NO-RATE`), `listRates`, `correct`; `plt.Currency.get`
- **Numbering:** `plt.Number.next/reserve/void`.
- **AI:** `plt.Ai.invoke` (gateway), `plt.Ai.recordOutcome`, `plt.AiToggle.resolve/set`, `plt.Ai.killSwitch`.
- **Data protection:** `plt.Retention.registerDataset/schedule/runs`, `plt.LegalHold.apply/release/check` (≤ 20 ms p95 for 100 refs), `plt.Erasure.execute`, `plt.Subject.export`.
- **DORA:** `plt.Incident.*`, `plt.IncidentReport.*`, `plt.Ror.export`, `plt.ThirdParty.*`.
- **Other:**
  - `plt.Time.now` (mandatory for every module; non-production offset)
  - `plt.Import.users/roleAssignments/authorityGrants/identityInvitations` (MIG R-73; dry-run mandatory; idempotent on batch id + record key)
  - `plt.DataChange.*`
  - `plt.Pipeline.e2eGate`

**Consumed:** see §4. External integrations (§9.3):
- Corporate directory: OIDC or SAML.
- HR: SCIM (OI-PLT-09).
- Partner bank IdPs.
- gov.gr OAuth 2.0 (UNVERIFIED).
- EUDI Wallet (OpenID4VP, P4 or later).
- SMS and email providers.
- ECB reference rates feed.
- Qualified trust service provider: RFC 3161 time stamps and qualified seals (OI-PLT-10).
- Bank of Greece DORA channel and register submission (UNVERIFIED).
- Hellenic DPA breach form (manual).
- Platform services: KMS, vault, monitoring, SIEM, CI/CD.
- EU AI endpoints.

## 7. SPIs / country-pack interfaces

All are MKT-owned definitions (REQ-MKT-002); PLT is the main caller:
- `NumberingScheme` (formats, check digits, gapless flags)
- `HolidayCalendarProvider` (fixed dates, Orthodox and Western Easter offsets, weekday and substitution rules)
- `IdentityFederationProvider` (R-05; gov.gr / wallet)
- `IncidentReportingChannel` (R-05; Bank of Greece)
- `FxRateSource` (R-05; ECB default)
- `AddressFormatter` (served geography data; E.164 hint)
- `NameTransliterator` (Latin form of names)
- `DigitalIdentityProvider` (wallet pre-fill, referenced in REQ-PLT-066)

Cyprus stub pack in CI proves the engine is not Greek-shaped (§10.4).

## 8. Screens (§6; all GR/EN, WCAG 2.2 AA, IB-* design-guide patterns)

| ID | Name | Persona | One line |
|---|---|---|---|
| SCR-PLT-01 | Hosted sign-in and MFA | all staff; external (CHN embeds) | passkey first, OTP, step-up, generic errors, ΕΛ\|EN switch; IB-18 |
| SCR-PLT-02 | Users search/list | ROLE-37, identity admin, ROLE-43 | IB-05 split pane, IB-26 filter bar; bulk suspend/export |
| SCR-PLT-03 | User detail / new user | ROLE-37 | tabs Basics, Access, Attributes (ABAC), Authority, Profile/region, Security, History; no admin-set passwords; IB-07, IB-19 |
| SCR-PLT-04 | Roles and role detail | identity admin, role owners | permissions grid, holders, producer-code scope; SoD warnings |
| SCR-PLT-05 | Access review campaigns | ROLE-39, reviewers | IB-12, IB-28, IB-02 (K keep / R revoke) |
| SCR-PLT-06 | Privileged access and emergency accounts | engineers, ROLE-39 | JIT grants 0.5–8 h with countdown; service identities |
| SCR-PLT-07 | Authority profiles and grants | ROLE-13/18/22/24 | grants (type, comparison, value), delegations, impact preview, "Who can approve?" |
| SCR-PLT-08 | Approval inbox (PLT types) | checkers | IB-04 queue, IB-07 approve-the-diff, IB-23 |
| SCR-PLT-09 | Audit log viewer | ROLE-29/43/39/30 | IB-29 dense log, journey view, integrity status, sealed export |
| SCR-PLT-10 | Batch and workflow monitor | ROLE-38, ROLE-41 | jobs/workflows/schedules; IB-28, IB-32; retry partitions |
| SCR-PLT-11 | Integration and queue monitor | ROLE-38 | adapters, breakers, topics, consumer lag, DLQ peek/replay/discard, file transfers |
| SCR-PLT-12 | Feature-flag console | ROLE-40/38/14 | targeting rules, per-env state, regulated flag needs checker |
| SCR-PLT-13 | Runtime properties | ROLE-38/40 | typed values, secret refs, import/export diff |
| SCR-PLT-14 | Calendars and holidays | ROLE-33, ROLE-37 | generated calendars, exceptions, publish via maker-checker |
| SCR-PLT-15 | Currencies, FX rates and numbering | ROLE-24/25/38 | FX corrections with reason; series usage |
| SCR-PLT-16 | SoD rules | ROLE-39 | rules, exceptions, Simulate |
| SCR-PLT-17 | AI control centre | ROLE-39, AI governance lead | global kill switch, toggle matrix, endpoints, budgets; IB-17 |
| SCR-PLT-18 | Retention and legal hold | ROLE-30/29, legal | schedules, holds (dual release), purge runs |
| SCR-PLT-19 | Incident log with DORA timers | incident manager, ROLE-39/38/30 | classification wizard, timers, report drafts, breach tab |
| SCR-PLT-20 | Register of information and ICT third parties | ROLE-39 | providers, arrangements, IB-15 concentration graph, export |
| SCR-PLT-21 | BC/DR and resilience test log | ROLE-38/39/43 | tests, targets vs measured RTO/RPO |
| SCR-PLT-22 | Change log and release dashboard | ROLE-40 | IB-11, quality gates incl. E2E, test-lead acknowledgement |
| SCR-PLT-23 | Data change | ROLE-38/41 | dry-run, approval, step-up execute, rollback |
| SCR-PLT-24 | Log explorer | ROLE-38 | search, tail, trace id, time-boxed log level |
| SCR-PLT-25 | Non-production clock and sample data | devs, testers | absent from production builds |
| SCR-PLT-26 | App shell, preferences, error pages, inspector | all staff | IB-24 Quiet chrome, IB-01 palette, IB-03, IB-16, IB-33, ΕΛ\|EN |
| SCR-PLT-27 | Service health, SLO and fitness dashboard | ROLE-38/40 | IB-32, IB-17, fork ratio, E2E gate |
| SCR-PLT-28 | My security settings (staff) | self | passkeys, sessions, recovery codes |

Common states on every screen: loading, empty, error (with correlation id), permission denied, stale, offline/degraded. Every grid gets a column chooser, sort and CSV export (REQ-PLT-352).

## 9. Regulatory, tax and statutory rules

### 9.1 Stated explicitly
- **DORA major-incident classification (BR-PLT-030…033, Delegated Reg. (EU) 2024/1772 Art. 8–9):**
  - An incident is **major** when a critical service is affected AND either malicious unauthorised access may cause data loss, OR ≥ 2 other thresholds are met.
  - Client and counterpart thresholds: clients > 10 % of the service's clients or > 100 000; counterparts > 30 %; transactions > 10 % of the daily average number or value.
  - Duration and spread thresholds: duration > 24 h, or downtime > 2 h for services supporting critical or important functions; ≥ 2 Member States; economic impact > € 100 000.
  - Also counted: data-loss impact (availability, authenticity, integrity, confidentiality) and the reputational criteria of Art. 2.
  - **Recurring** incidents: same apparent root cause, ≥ 2 occurrences in 6 months, assessed monthly and classified collectively (BR-PLT-035).
- **DORA reporting deadlines (BR-PLT-034, REQ-PLT-303, Delegated Reg. (EU) 2025/301 Art. 5):**
  - Initial notification: `classified_at + 4 h`, capped at `aware_at + 24 h` **only if** `classified_at ≤ aware_at + 24 h`. Otherwise `classified_at + 4 h`, and never a past deadline.
  - The pack weekend and holiday rule (`plt.dora.weekendRule`) applies where the authority allows it.
  - Intermediate report: `initial_submitted_at + 72 h`. Final report: latest intermediate + **1 month**.
  - Warnings fire at 50 % and 90 % of each window; timers must be correct to the minute (NFR-PLT-026).
  - Worked examples:
    - Awareness 08:00, classified 10:00 → initial due 14:00.
    - Awareness 2026-11-09 08:00, classified 2026-11-10 20:00 → due 2026-11-11 00:00.
    - Intermediate submitted 2026-11-10 10:00 → final due 2026-12-10 10:00.
- **GDPR breach (BR-PLT-036, REQ-PLT-306, Art. 33):**
  - Notify within 72 h of awareness unless the DPO records the breach is unlikely to result in risk.
  - The clock is CMP's `CMP_GDPR_BREACH_NOTIFY`. PLT only starts and stops it and keeps no timer of its own (D7).
- **Register of information (BR-PLT-037; DORA Art. 28(3); Implementing Reg. (EU) 2024/2956):** at least yearly, data as of **31 December**, per entity plus consolidated; submission date from the pack.
- **DORA change management (Delegated Reg. (EU) 2024/1774 Art. 17; BR-PLT-024):**
  - Approver ≠ author ≠ implementer.
  - Emergency changes are retro-approved within 2 business days.
  - Rollback evidence is required.
- **TLPT (Delegated Reg. (EU) 2025/1190, applicable 8 Jul 2025):** on production, at least every 3 years, if the insurer is designated (REQ-PLT-319, Could; OI-PLT-14).
- **Bank of Greece is the competent authority** for (re)insurers under Greek Law 5193/2025 Arts. 148–152.
  - NIS2 (Greek Law 5160/2024) excludes DORA entities, so there is no separate NIS2 process.
  - BoG ECA 180/17.12.2020 adopted the EIOPA cloud-outsourcing guidelines (REQ-PLT-316 notification record).
- **Data residency (REQ-PLT-285, R-85, BR-PLT-028):**
  - Hosting, backups, monitoring and AI endpoints are **EU only**.
  - Other third parties may receive PD outside the EU only with a GDPR Chapter V basis recorded in the third-party register first; otherwise `PLT-ERR-TRANSFER-BASIS-MISSING`.
- **AI (OBL-AIA Art. 12, 14, 26, 50):**
  - Kill switch takes full effect in ≤ 60 s; features default off; interactive timeout 5 s (BR-PLT-029).
  - Endpoints must be EU-hosted with zero data retention and no training on customer data (REQ-PLT-221).
  - A feature cannot be enabled without an Approved CMP register entry (REQ-PLT-233, -354; BR-PLT-044).
- **Passwords (REQ-PLT-035, NIST SP 800-63B-4 as benchmark):** ≥ 15 characters, checked against a breached list, no composition rules, no expiry, memory-hard hash.
- **OAuth (RFC 9700):** only authorization code + PKCE; no implicit or ROPC grants; exact redirect match; sender-constrained tokens (DPoP or mTLS); refresh rotation with reuse detection; access tokens ≤ 10 min.
- **Vulnerability SLAs (BR-PLT-039):** critical 7 days (72 h if exploited and internet-facing), high 30, medium 90.
- **PCI DSS (NFR-PLT-033):** no PAN, CVV or track data in any PLT component; provider-hosted card capture and tokens only.
- **Accessibility:** WCAG 2.2 AA; sign-in without cognitive tests (SC 3.3.8); page `lang` attribute follows the switch (SC 3.1.1/3.1.2).

### 9.2 Referenced but not specified (gaps)
- Bank of Greece incident channel, format, national window and register timetable (OI-PLT-01).
- The number of the incident-report template implementing regulation ("2025/302", OI-PLT-02).
- Law 5193/2025 penalties (OI-PLT-03).
- The text of ECA 180/2020 and its interplay with DORA (OI-PLT-04).
- Solvency II Art. 41/49 and DR 2015/35 Art. 258/274 numbers (OI-PLT-05).
- eIDAS 2 relying-party duties and dates (OI-PLT-06).
- gov.gr use by private insurers (OI-PLT-08).
- Which Cyprus authority receives DORA reports (OI-PLT-15).
- The harmonised report template schema is "loaded for the jurisdiction" but not defined.

### 9.3 Deferred to configuration or pack
- **All retention durations:**
  - `plt.retention.<rc>.duration`; a single programme retention schedule (XMR-D-259) approved by DPO + compliance + legal and loaded as Greece-pack data before system test (OI-PLT-11).
  - PLT-owned codes: RC-ID-STAFF, RC-ID-EXT, RC-SEC-LOG, RC-AUD-BUS, RC-INT-ARCH, RC-EVT-ARCH, RC-OPS-LOG, RC-AI-INT, RC-DORA, RC-CHG, RC-CFG, RC-REF, RC-TMP.
  - Module-owned RC codes are included by reference (§10.1 B).
- Other pack or configuration items:
  - Holiday rules and numbering formats.
  - DORA weekend rule and reporting window; register submission date.
  - FX default source.
  - Identity federation providers.
  - Session, lockout and inactivity parameters (§10.2).

## 10. Greek-market specifics
- **Calendar:**
  - Orthodox Easter computus is in core. Examples: Clean Monday = Orthodox Easter − 48 days (2027-03-15); Good Friday 2027-04-30; Easter Monday 2027-05-03.
  - `addBusinessDays(2027-04-29, 1)` = 2027-05-04 (REQ-PLT-197).
  - Golden Orthodox Easter dates 2025–2030 are listed in §14.x.
  - Regional holidays by zone, e.g. Attica.
  - Next year's calendar is published by 1 October; generation runs three years ahead.
- **Time zone:** Europe/Athens; effective-date rollover at local midnight (REQ-PLT-183, -206).
- **Language:**
  - Mandatory Greek and English on every screen, with an always-visible "ΕΛ | EN" switch and the palette action "Switch language / Αλλαγή γλώσσας".
  - Switch applies in ≤ 1 s p95 without reload or loss of input.
  - The choice is stored as `ui_language` and returned as the OIDC `locale` claim.
  - Resolution order (BR-PLT-043): profile → device → browser (if el or en) → entity default (Greek for Greek entities).
  - Region format is a separate setting; documents follow the customer's language (REQ-PLT-355…361).
  - Text search is accent-insensitive Greek/Latin. The middle name is labelled "πατρώνυμο".
- **Currency and FX:** EUR; ECB euro reference rates daily with a TARGET-holiday fallback; Fairfax group USD rates of type GROUP (REQ-PLT-208).
- **AFM:** appears only in pseudonymisation acceptance ("no AFM from production appears", REQ-PLT-242). The customer registration link may use tax id + policy number + DOB (REQ-PLT-051, pack-defined).
- **myDATA:** only appears as the adapter example (REQ-PLT-006 GWT, REQ-PLT-157). Fiscal gapless series are issued by CMP using this numbering service (BR-PLT-027, D3).
- **gov.gr:** OAuth 2.0 TaxisNet authentication via `IdentityFederationProvider`, Could P1, UNVERIFIED.
- **Information Centre / bureau:** not mentioned in PRD-14.
- **Regulators and authorities:** Bank of Greece (DORA, cloud outsourcing; ROLE-44 receives sealed exports with no login in P1, OI-PLT-07); Hellenic DPA (manual breach form).

## 11. Controls
- **Authority types registered by PLT (§12.2):**
  - PLT_PRIVILEGED_GRANT_APPROVAL (role privilege level, duration)
  - PLT_DATA_CHANGE_APPROVAL (module, risk class, rows)
  - PLT_FX_CORRECTION (pair, deviation %)
  - PLT_INCIDENT_CLASSIFICATION (severity)
  - PLT_DORA_REPORT_APPROVAL (report kind)
- **Maker-checker list (PLT-owned, §12.3):**
  - Privileged role grants; MFA reset for privileged users; intermediary-admin recovery.
  - Authority profile and grant changes above threshold, and per-user overrides.
  - SoD rule changes and exceptions.
  - Production runtime-property changes; regulated flag changes in production; production business-workflow termination; production scheduler suspension.
  - DLQ discards; calendar publication; FX manual corrections.
  - AI toggles at tenant and LE level, and kill-switch **deactivation** (activation is immediate and reviewed afterwards).
  - Prompt-template versions.
  - Retention schedule changes; legal-hold release (**dual**); emergency-account use review (**dual**).
  - Data changes; DORA report approval; change approval (independence rule).
  - Imported privileged assignments and authority-grant batches (R-73).
  - Business four-eyes items owned by other modules (R-18, R-25, R-45, R-51, R-75) run through REQ-PLT-004.
- **SoD minimum (BR-PLT-005, core final):**
  - (a) The author of a rate table, UW rule set, posting rule, product version, configuration change, decision table or code change ≠ its approver or deployer.
  - (b) Maker ≠ checker.
  - (c) A requester of privileged access ≠ its approver.
  - (d) A user admin may not grant roles to themselves.
  - (e) The person who changed a payee bank account may not release a disbursement to it.
  - (f) A data-change creator may not approve, nor execute without step-up.
  - (g) A reviewer may not review their own access.
  - Modules are expected to add rules (§12.4).
  - Rules are checked at grant and at action time; failures return `PLT-ERR-SOD`.
- **Audit specifics:**
  - Same transaction as the business change; hash chain; daily Merkle root with a qualified time stamp; sealed exports.
  - Daily and on-demand verification; a break is a security incident.
  - Access to the audit trail is itself audited.
  - P2/P3 values are encrypted under per-subject keys, so cryptographic erasure keeps the chain verifiable (REQ-PLT-240/241, BR-PLT-017).
  - Identity and security logs go to the SIEM as a separate stream (REQ-PLT-135).
- **Controls cannot be flagged:** no flag or property may disable audit, authority, maker-checker, SoD or encryption (REQ-PLT-191, BR-PLT-023; `PLT-ERR-CONTROL-NOT-FLAGGABLE`).
- **GDPR handling:**
  - P2/P3 masked by default unless the caller holds the data-class permission (REQ-PLT-080).
  - PD scrubbed from logs (CI test).
  - Pseudonymised non-production data; production restores into non-production are blocked.
  - Erasure re-applied after any restore; backups kept 35 days.
  - DSAR export of PLT-held data within 1 h (a recorded deviation, XMR-D-209).
  - Processing-inventory feed to CMP.
  - Legal holds block purge and erasure everywhere.
  - Row-level security on `legal_entity_id` and `jurisdiction` for every business table, enforced by an architecture test (REQ-PLT-081).

## 12. AI features (§11; every feature is off at tenant level by default; none is required for MVP; G10: PLT works fully with the kill switch active)

| ID | Name | Classification | Toggle scope | MVP need |
|---|---|---|---|---|
| AI-PLT-01 | Incident triage and DORA report drafting | Minimal | Tenant, LE, role; timeout 10 s | No |
| AI-PLT-02 | Entitlement right-sizing recommender (entitlements, never persons, R-79) | High-risk controls retained (conservative) | Tenant, LE | No |
| AI-PLT-03 | Ops anomaly hints | Minimal | Tenant, **stamp**, role | No |
| AI-PLT-04 | Natural-language audit and log search (query shown before run) | Minimal | Tenant, role | No |
| AI-PLT-05 | Adaptive sign-in risk score (only triggers step-up; 300 ms timeout) | **High-risk controls (uncertain)** | Tenant, LE, **population** | No; rule-based REQ-PLT-055 fallback |
| AI-PLT-06 | Data-change and script reviewer | Minimal | Tenant, role | No |
| AI-PLT-07 | Contract clause checker (DORA Art. 30) | Minimal | Tenant, role | No |

The AI **control plane** itself (REQ-PLT-010, -217…-234, -353, -354, -361) is **Must P1**:
- The gateway is the only egress; network policy blocks direct calls.
- Endpoint registry; per-feature allow-lists and P3 blocking (`PLT-ERR-AI-DATA-CLASS`).
- Interaction records; recommendation lifecycle; prompt-template versioning.
- Safety screening; delegated tokens with the `act` claim; dry-run before agent commands; budgets (Should).
- Requests carry the user's UI language.

## 13. Open issues, assumptions and CCRs (§16)
- **Open issues:** OI-PLT-01…15, all open. Topics are listed in §9.2 plus:
  - OI-07: BoG supervisor access.
  - OI-09: HR SCIM capability.
  - OI-10: QTSP selection.
  - OI-11: Greek retention durations.
  - OI-12: time-saving estimates.
  - OI-13: PLT as IdP for the legacy system during coexistence.
  - OI-14: TLPT designation.
  - OI-15: Cyprus authority.
- **Assumptions:**
  1. A corporate directory and an HR feed exist.
  2. Bank federation is available if a partner signs (D6).
  3. **Two EU regions, CMK, immutable backups.**
  4. **Open-source building blocks are acceptable: container orchestration, PostgreSQL, a log-based broker, a durable-execution engine, OTel collectors (CD-20).**
  5. Fairfax policies apply at the group layer.
  6. Volumes per XMR-FR-200.
- **CCRs (all accepted):**
  - CCR-PLT-01: private entities (R-01).
  - CCR-PLT-02: 12 events (R-02).
  - CCR-PLT-03: ConfigurationActivated vs ConfigChanged semantics (R-03).
  - CCR-PLT-04: three SPIs (R-05).
  - CCR-PLT-05: mandatory time service; designated targets REQ-PLT-161/265/332 (R-06).
  - CCR-PLT-06: identity at 99.95 %; tokens valid during outage (R-13).
  - CCR-PLT-07: OBL-RES wording (R-93).
  - CCR-PLT-08: Incident → IctIncident (R-94).
- **Programme decisions (§16.5):**
  - Closed: #1 (.NET, D10), #2 (open-source self-operated components, D10), #6 (bancassurance out of P1, D6).
  - Still open: #3 hosting provider and regions; #4 passkey-only vs OTP exception; #5 federate staff sign-in vs PLT-held credentials; #7 gov.gr; #8 QTSP; #9 DORA operating model and BoG channel; #10 retention durations and crypto-erasure approach.
- **Risks:** RK-PLT-01…11 (custom IdP defect; late anchors; DORA channel; event/workflow instability; governance friction; key management; provider concentration; alert fatigue; untranslated strings; CEL divergence or performance; flaky E2E gate).

## 14. Conflicts and ambiguities

### (a) With the infra/stack (ARCHITECTURE-DECISIONS.md)
1. **Custom-built identity service vs Entra ID / External ID.**
   - The PRD builds its own OIDC/OAuth server with two realms: REQ-PLT-001 "custom-built identity service", REQ-PLT-015, REQ-PLT-032…074; ASVS **L3** for the identity service (REQ-PLT-296, NFR-PLT-015/020); RK-PLT-01; assumption A8 "Contract v1.1: custom-built identity service".
   - It also defines its own Authenticator, Session, ClientApplication and FederationConnection tables, passkey storage, password hashing, JWKS key rotation, DPoP, refresh-reuse detection and SCIM server.
   - The ADR says: "Microsoft Entra ID for staff … Entra External ID for customers, brokers and bank staff … we never build our own identity system."
   - **This is the largest conflict.** About 45 identity requirements, SCR-PLT-01/28 and the identity entities would need to be re-specified as Entra configuration plus a thin PLT layer that keeps authorisation, ABAC, authority, the user directory mirror and profile.
   - Items needing a decision:
     - DPoP / sender-constrained tokens (REQ-PLT-040).
     - A Greek/English switch on the *hosted* sign-in page (REQ-PLT-355, -358) and the `locale` claim from `ui_language` (REQ-PLT-357).
     - The external user-store schema and the `producer_codes` claim (REQ-PLT-015 GWT).
     - Token exchange with the `act` claim for AI agents (REQ-PLT-060; Entra OBO differs).
     - Back-channel logout; uniform anti-enumeration timing; bank SAML federation into External ID.
     - Emergency accounts held outside the IdP.
2. **Message broker and "topics".**
   - The PRD has an in-house relay that publishes to **per-module topics** `<mod>.events.v<major>`, partitioned, with ≥ 30-day stream retention, consumer groups, stream namespaces per stamp and mTLS denial (REQ-PLT-005, -138, -140, -152).
   - It also refers to an "open log-based broker" (§3.2 event-stream limits; assumption 4) and an event stream encrypted at rest (REQ-PLT-286).
   - The ADR says: "Transactional outbox … dispatched in order to in-process handlers"; "No message broker".
   - Topic, partition, retention and stream-archive semantics have to be re-expressed over PostgreSQL. Examples: the outbox and an archive table *are* the log; replay comes from the archive; REQ-PLT-149's 7-day purge then conflicts with replay needs.
3. **Durable workflow engine.**
   - The PRD requires a durable-execution engine: REQ-PLT-007, -165 "records workflow history as events, replays workflow code deterministically", -166 namespaces and task queues per module, -167 version markers, signals, reset; REQ-PLT-153 "every external call runs as a workflow activity"; assumption 4 "a durable-execution engine".
   - The ADR says: "Not used: workflow servers"; "Hangfire … state machines with deadlines … stored as domain records".
   - Needs a decision on how to meet REQ-PLT-165/167/170/171 with Hangfire plus domain state tables.
4. **Container orchestration and Kubernetes.**
   - The PRD requires a "container orchestration cluster with network policies" (REQ-PLT-271); pod security, admission control verifying image signatures and no privileged containers (REQ-PLT-295); "module pod" (REQ-PLT-217); cluster audit to SIEM (REQ-PLT-294).
   - The ADR says: Azure Container Apps; no Kubernetes. The controls must be mapped to ACA and Azure equivalents.
5. **Mutual TLS between services (REQ-PLT-286) and "service-to-service" calls.** These are largely moot in a modular monolith, since modules call in-process. Only the Gotenberg container and edges apply.
6. **Self-operated monitoring stack, SIEM, on-call and alert routing.**
   - The PRD wants an "EU-hosted monitoring stack per stamp" (REQ-PLT-252), on-call schedules, escalation and acknowledgement timers (REQ-PLT-257), and a 24×7 SIEM (REQ-PLT-294).
   - Assumption 4 / CD-20 says "no product named".
   - The ADR picks Azure Monitor / Application Insights. SIEM and on-call tooling are not chosen (build vs buy).
   - Retention targets: traces 14 d, logs 90 d, metrics 13 months.
7. **Monitoring and identity "per stamp" with a global routing layer** (REQ-PLT-271, -273, -274, NFR-PLT-023): must be checked against INFRASTRUCTURE.md (not read).
8. **Environment ladder** (REQ-PLT-262): ephemeral environment per PR within 20 min. Needs checking against the infra plan.
9. **Vendor and product questions that remain:**
   - Malware scanning engine (REQ-PLT-161).
   - QTSP for RFC 3161 time stamps and qualified seals (REQ-PLT-125/130).
   - HSM-backed KMS with customer-managed keys per stamp (REQ-PLT-286/288; Key Vault Premium / Managed HSM).
   - SCIM HR source.
   - Synthetic checks from two EU locations.
   - Feature-flag store: in-house per PRD; ADR silent.
10. **In-house CEL-compatible expression language (REQ-PLT-364, D10)** and DMN import/export (REQ-PLT-178). Not an ADR conflict, but a large build; the ADR's "minimal dependencies" rule favours in-house.
11. Consistent with the ADR: PostgreSQL schema-per-module plus a `plt_` table prefix (REQ-PLT-365), RLS, NetArchTest (REQ-PLT-321), OpenTelemetry, idempotency keys, RFC 9457, `decimal` and no floats in expressions, SBOM and signed artefacts (REQ-PLT-264), and audit fields as in ADR rule 9.

### (b) With the contract and other PRDs
- **Correlation-id journey wording:** SCR-PLT-09 layout ("'Journey' view showing the correlation chain across modules") contradicts D5 / REQ-PLT-132 / REQ-PLT-363, which say the correlation id is "never used to join steps of a journey". The REQ-PLT-128 GWT ("given a correlation id … all audit records across modules for that trace") is technical only, which is acceptable.
- **AuditEvent / OutboxMessage shapes vs requirements:**
  - Neither AuditEvent nor OutboxMessage in §7.1 has `origin`, although REQ-PLT-345 requires origin MIGRATION on every audit record and event.
  - AuditEvent lacks lineage-key fields, although REQ-PLT-363 says the audit library records them.
  - The `set_id/set_size/index` (REQ-PLT-366) location is unspecified (envelope or payload).
  - The envelope field `producer` and `configuration_hash` are contract fields; `origin` is mentioned in the contract text but is not in the contract's field list.
- **`UserProvisioned` consumers:** the §8.1 consumer list is DAT only, but the J2 journey (§4.2: "WRK receives `UserProvisioned` and assigns groups by rule") has WRK receiving `UserProvisioned` to assign groups.
- **AI toggle levels:** AiToggle.level is {TENANT, LEGAL_ENTITY, LOB, ROLE, USER} (REQ-PLT-010/219), but AI-PLT-03 uses scope "stamp" and AI-PLT-05 uses "population". These levels are not modelled.
- **Approval state "Consumed"** appears in §7.3 but not in REQ-PLT-114's state list.
- **Inventory floor:** "21 owned reference screens and 6 UI-library screens" vs 28 SCR-PLT screens. This is consistent: 27 items map to 28 screens.

### (c) Internal contradictions
- **Phase counts:** §5.23 says "349 requirements in P1, 3 in P4". The actual tally is P1 346, P2 1 (REQ-PLT-061), P3 2 (REQ-PLT-062, -107), P4 3. The "Must" total of 298 includes 2 P4 Musts (REQ-PLT-272 provisioning in ≤ 1 working day; REQ-PLT-279 stamp-migration rehearsal), while goal G9 and NFR-PLT-023 still state the ≤ 1 working-day stamp target generally.
- **Ordering:** BR-PLT-042 appears before BR-PLT-041; NFR-PLT-033 before NFR-PLT-032 (cosmetic).
- **REQ-PLT-003 vs REQ-PLT-103:** decision values "refer-to" vs "refer". Should be one enum `ALLOW|REFER|DENY`.
- **Consumer retry:** REQ-PLT-143 has a default of 8 attempts; BR-PLT-019 adds backoff base 1 s, cap 5 min. These are consistent.
- **Outbox retention:** REQ-PLT-140 needs a ≥ 30-day stream and an archive for replay, while REQ-PLT-149 purges outbox rows after 7 days. This only works if a separate archive store exists, which is unnamed under the no-broker stack.

### (d) Cannot be built without a decision
1. Identity: Entra configuration vs the custom build. Decide what stays in PLT (directory mirror, roles and ABAC, authority, profile `ui_language`, SCIM target).
2. How "topics", partitions, stream retention and replay are realised on PostgreSQL plus in-process dispatch.
3. Workflow-engine semantics on Hangfire: deterministic replay, versioning, signals, visibility.
4. QTSP choice (OI-PLT-10) for audit sealing and evidence packs.
5. Hosting regions, primary and DR (§16.5 #3), and the stamp/global-layer design vs INFRASTRUCTURE.md.
6. Retention durations (OI-PLT-11). The engine can be built first, but values are needed before system test.
7. BoG DORA channel and format (OI-PLT-01): build the manual-submission fallback first.
8. SIEM and on-call tooling: build or buy.
9. Malware-scanning engine.
10. Exact location of `origin` and lineage keys in the audit and event schemas.

## 15. Build notes
- **Must exist first, because every module depends on it** (the anchors cited by later waves; RK-PLT-02 asks for an API-first contract-test pack in the first increment):
  1. `plt.Time.now` (mandatory; fitness test bans system-clock calls).
  2. Module skeleton: per-module schema, DB role, RLS session context (REQ-PLT-081, -322), `plt_` tables.
  3. `plt.Outbox.publish` + `plt_outbox` + `plt_processed_event` + `plt_idempotency_record` + envelope filling; in-order in-process dispatcher; idempotent consumer with DLQ.
  4. `plt.Audit.append` + `plt_audit_outbox` + central store, with the hash chain added later.
  5. Auth token validation + `plt.Policy.evaluate` (RBAC+ABAC) + permission manifest.
  6. `plt.Authority.check` + type registration.
  7. `plt.Approval.*` maker-checker with content hash.
  8. Configuration runtime (`mkt.Configuration.resolve`) + configuration hash.
  9. Calendars and business-day arithmetic; FX; numbering.
  10. Architecture fitness tests (NetArchTest).
- **Hardest parts:**
  - (i) Identity: either the custom IdP or reconciling the PRD with Entra.
  - (ii) The audit hash chain with cryptographic erasure. Per-subject keys need a key hierarchy; the chain must verify after key destruction; qualified sealing.
  - (iii) Ordered, gap-free per-aggregate outbox delivery with set-aware consumers (D4), replay and archive.
  - (iv) Deterministic, versioned workflows and timers on Hangfire.
  - (v) The CEL-compatible decimal expression language plus the decision-table runtime (20 ms p95).
  - (vi) DORA timer logic, including late classification and the weekend rule.
  - (vii) The AI kill switch with in-flight cancellation in ≤ 60 s and no user-input loss.
  - (viii) The language switch on every screen without reload, with a translation-completeness gate.
- **Suggested slicing:**
  - **S1 foundation libraries** (time, outbox, consumer, audit append, policy library, idempotency, RLS, fitness tests, OTel).
  - **S2 authority + maker-checker + SoD** (SCR-PLT-07, -08, -16).
  - **S3 configuration runtime + flags + runtime properties** (SCR-PLT-12, -13).
  - **S4 reference data + numbering** (SCR-PLT-14, -15).
  - **S5 identity integration (Entra) + user/role admin + access reviews + JIT** (SCR-PLT-01…06, -28) + language profile.
  - **S6 audit store, hash chain, viewer, lineage, export** (SCR-PLT-09).
  - **S7 jobs, workflows and adapter host + integration monitor + DLQ** (SCR-PLT-10, -11, -24).
  - **S8 data protection** (retention, holds, erasure, pseudonymisation; SCR-PLT-18).
  - **S9 AI control plane** (gateway, toggles, kill switch; SCR-PLT-17); all features off.
  - **S10 DORA** (incidents, timers, reports, register, DR log; SCR-PLT-19…21).
  - **S11 release/change, E2E gate, health dashboard, data change, non-production tools** (SCR-PLT-22, -23, -25, -27).
  - **S12 shell** (SCR-PLT-26). Needed early as a skeleton for every UI; the full set of features can come later.
