# Digest — PRD-13 Work management (WRK)

Source: `core-insurance-prds/PRD-13-work-management.md` v1.4 (2026-10-07, "Baseline 1.0 candidate after freeze fix"), 2,799 lines, read in full. Binding input stated by the PRD: `00-system-contract.md` v1.11. Checked against `core-insurance-infra/ARCHITECTURE-DECISIONS.md`.

---

## 1. Identity

| Item | Value |
|---|---|
| Module code | WRK |
| Title | Work management: activities, queues, notes, inbound documents |
| Wave | Wave 1, alongside PLT, MKT, PTY and PFC (§1.1) |
| Contract decisions applied | CD-07 (clock warnings become activities), CD-09 (WRK owns inbound documents; binaries go to the DOC archive), CD-10 (WRK owns groups, queues and assignment), CD-11 (AI governance), CD-17 (holidays are PLT reference data), CD-18 (WRK owns global search, recent items and the command-palette back-end), CD-20 (built from scratch, vendor-neutral). Also D1, D3–D10, R-101 and R-102 (§1.1) |
| Availability tier | T2 (99.5%, RPO ≤ 15 min, RTO ≤ 4 h). Clock-activity creation is the exception: it inherits "statutory deadlines met across failover" (§14) |

**Purpose (§1.2).** WRK is the shared service that every other module uses to:
- create, route, track and close human work (activities);
- record notes;
- receive, malware-scan, classify, extract, verify and file incoming documents;
- let staff find anything quickly (global search, recent items, command palette, My Desktop).

It also owns:
- groups, queues, memberships and skills;
- assignment rules (push and pull), workload caps and absence or cover handling;
- SLA policies and escalation;
- back-office requests from intermediaries and customers;
- participants (user-to-object roles);
- staff notifications, and hooks so CHN can notify intermediaries and customers;
- the diary and calendar.

The PRD replaces the blueprint's "small core task service" with a complete module.

**Five key decisions (§1.2).**
1. **One activity model.** Every module creates human work through `wrk.Activity.create` (`REQ-WRK-001`) from a versioned pattern (`REQ-WRK-002`). Canonical states are Open → Completed / Skipped / Cancelled. Assignment state is held separately. Business modules decide *when* work is needed; WRK decides *who* does it and *by when*.
2. **Deterministic, explainable routing held as data.** Routing uses versioned decision tables in the shared CEL-compatible expression language (D10), evaluated by the PLT rules runtime (`REQ-PLT-007`), with a dry-run simulator and maker-checker activation. Routing never uses individual performance or personal traits. AI may only tag the item's *content* (EU AI Act Annex III 4(b)).
3. **Due dates on business calendars.** Dates are computed through the PLT calendar service (`REQ-PLT-009`). Holiday data comes from the pack's `HolidayCalendarProvider`, which WRK never calls directly. Statutory deadlines stay with CMP clocks (`REQ-CMP-003`); WRK mirrors them and never recalculates them.
4. **A person verifies inbound documents before they change any record.** Classification and extraction (rules first, AI optional) only produce suggestions, which a named person accepts, edits or rejects ("approve-the-diff", `IB-07`).
5. **Visibility follows the object; confidentiality narrows it.** ABAC is applied on the primary object (contract §3.9.4). Notes and documents add four confidentiality levels: General, Sensitive, Legal and Special-category health.

**Non-goals and out of scope (§1.4).**

| Out of scope | Owner | Interface |
|---|---|---|
| Business rules deciding *when* work is needed | UW, POL, BIL, CLM, CMP, RI, FIN | `wrk.Activity.create`; event-trigger table `REQ-WRK-187` |
| Statutory clock definitions, durations and instances | CMP `REQ-CMP-003`; values from MKT `REQ-MKT-009` | WRK consumes `ClockStarted/Warned/Breached/Met` |
| Outbound documents, templates, archive rules, the Documents tab | DOC `REQ-DOC-001/-004/-008` | WRK stores inbound binaries via `REQ-DOC-004` and contributes inbound rows to `-008` |
| Staff identity, roles, permissions, SoD rules | PLT `REQ-PLT-001` | WRK reads them. Groups and queues stay in WRK (CD-10) |
| Authority limits and approval decisions | PLT `REQ-PLT-003`; decision captured by the owning module | WRK only routes to users who pass `plt.Authority.check` |
| Complaints register | CMP `REQ-CMP-004` | WRK holds the complaint-handling activities |
| DSAR orchestration | CMP `REQ-CMP-005` | WRK provides export, erasure and restriction APIs |
| Portal presentation | CHN `REQ-CHN-004/-005/-009` | WRK provides request APIs and notification hooks |
| Entity-specific searches | PTY `REQ-PTY-001`, POL `REQ-POL-014`, CLM `REQ-CLM-011` | WRK federates to them |
| Holidays and calendars as reference data | PLT `REQ-PLT-009` | WRK builds working calendars on top |
| **"Machine workflows and long-running processes"** | **"PLT workflow engine (`REQ-PLT-007`)"** | Workflows raise activities. See conflict C-1 |
| **"Analytics models and the lakehouse"** | DAT `REQ-DAT-001` | See conflict C-3 |

**How WRK avoids being a BPM engine (the PRD's own design, extra focus).**
- WRK models **activities** (human to-dos with due dates). It does not model process graphs.
- Owning modules hold the business state machines and decide when work is needed. They raise work either through an API call (`REQ-WRK-185`) or through a data-driven **event-trigger table**: event type + payload condition → pattern + link mapping + assignment hint + dedup key (`REQ-WRK-187`).
- Work closes in one of three ways:
  - auto-close on declared events (`REQ-WRK-043`, `-195`);
  - an owner API call (`REQ-WRK-196`);
  - a human outcome.
- Approval decisions are never recorded in WRK. The owner's decision API completes the activity (`REQ-WRK-202`).
- There is no sequencing, branching or orchestration between activities beyond these mechanisms:
  - follow-up and recurrence chaining (`REQ-WRK-038`, `-067`);
  - optional follow-on patterns on `ClockElapsed` (`REQ-WRK-193`);
  - checklists inside one activity (`REQ-WRK-073`).

Even so, the PRD **delegates timers, sweeps, bulk jobs and decision tables to a "PLT workflow engine"** (`REQ-WRK-090`; §9.2 `plt.Workflow.*`; §15.1 row `REQ-PLT-007`). Under the binding ADR this must become Hangfire jobs plus domain tables (see §14).

---

## 2. Size metrics

Counts below are recomputed from the requirement tables. Tags: BASELINE = function present in the reference product or practice; ENHANCEMENT = beyond it.

| Metric | Count |
|---|---|
| Functional requirements (REQ-WRK) | **313**: Must 235 / Should 62 / Could 16 |
| By phase | **P1 (motor MVP) 307** (235 Must + 62 Should + 10 Could); **P2 6** (all Could: `-072`, `-155`, `-250`, `-296`, `-364`, `-391`); no P3 or P4 requirement rows |
| **Motor MVP (Must, P1)** | **235** |
| Tags | 191 BASELINE / 122 ENHANCEMENT by my count. The self-check (§16.6) says 192 / 123, which does not add up to 313: a small discrepancy |
| Business rules (BR-WRK) | 46 |
| NFRs (NFR-WRK) | 16 |
| Screens | 26 (`SCR-WRK-01` … `-26`). They replace 20 owned reference screens with 333 field rows (coverage 100%, 0 missing) |
| Owned entities | About 35 (see §3), incl. the `SearchDocumentView` read model and the POL-facing event fields |
| Events produced | 19 (10 original contract events + 9 added by R-02) |
| Events consumed | About 92 event types across 39 handler rows (§8.2), from PTY, UW, POL, BIL, CLM, RI, FIN, DOC, CMP, PLT, MIG, MKT, DAT |
| API operation families exposed | About 27 groups / about 90 operations (§9.1) |
| Outbound integrations | About 30 calls (§9.2), plus 7 external integrations (§9.3) |
| AI features | 10 (`AI-WRK-01`…`-10`). All ship **off**; none is required for the MVP |
| Open issues / CCRs / risks | 11 OI (2 closed) / 7 CCR (all Accepted) / 9 risks (RK-WRK-01…09) |
| System-generated activity catalogue (5.10) | 49 pattern rows (some are families, e.g. `CMP-CLOCK-WARN-*`, `MIG-WAVE-*`) |

**Anchor family Must counts (§5.1).** Each anchor's "family" is the set of Must requirements that elaborate it, i.e. its MVP scope.

| Anchor | Must members |
|---|---|
| 001 | 37 |
| 002 | 14 |
| 003 | 50 |
| 004 | 15 |
| 005 | 32 |
| 006 | 22 |
| 007 | 6 |
| 008 | 11 |
| 009 | 10 |
| 010 | 7 |

The data-protection requirements (5.19), the administration requirements (5.20), `-399` and `-403` are module-wide and belong to no family.

**Build-size estimate: L.**
- 235 Must requirements and 26 screens.
- Several heavy subsystems:
  - a routing engine with atomic claim, caps, SoD and authority re-checks;
  - an SLA and escalation timer engine on business calendars with pause rules;
  - a full inbound-document pipeline (malware scan, OCR, classification, split, extraction, verification workbench, redaction, eIDAS evidence);
  - a cross-module search projection with Greek/Latin normalisation and ABAC filtering at ≤ 300 ms p95;
  - about 92 consumed event types and a 49-row trigger catalogue;
  - DSAR, erasure and retention across all of it.
- Every other module depends on it from Wave 1.

---

## 3. Owned entities (§7.1)

**Common columns.** Every row carries `legal_entity_id`, `jurisdiction`, `created_at`, `created_by` and `record_version`, plus `valid_from`/`valid_to` where the row has business validity.
- Validity periods are half-open `[valid_from, valid_to)` in UTC (D5, XMR-FR-150). This covers memberships, skills, absences, delegations, participants and all config versions.
- IDs are UUIDv7. Business numbers come from `REQ-PLT-014`.
- WRK holds **no binaries**; the DOC archive does.
- Personal-data classes P0–P3. Retention classes `RC-WRK-*` take their durations from the programme retention schedule (`BR-WRK-060`).

| Entity | Key attributes | State machine |
|---|---|---|
| **ActivityPattern / ActivityPatternVersion** | code (≤ 40, unique per entity, immutable); version_no; status; effective_from/to (Active versions do not overlap); subject / short_subject / description as maps of lang → text (GR and EN needed to activate); activity_class Task or Event; category, type; default_priority (Urgent/High/Normal/Low); mandatory, gate_blocking (⇒ mandatory), recurring + RRULE-like recurrence_rule, automated_only; object_types; scope (jurisdiction, LE, LoB, product, channel); target_rule / escalation_rule {days, hours, start_point, day_basis}, with escalation ≥ target; escalation_actions; sla_policy_id; fallback_queue_id (required); outcomes {code, labels, note_required}; checklist; DOC template refs; note template; auto_close {event_type, condition, outcome}; raised_by_module | Draft → PendingApproval → Active → Retired. A never-used Draft can be deleted. Mapped to PRD-18 §9.4 governance stages (CR-S3-25) |
| **Activity** | activity_id; activity_number; pattern_id + version (frozen, BR-WRK-004); state Open/Completed/Skipped/Cancelled; sub_state New/InProgress/Waiting; waiting_reason, wake_up_at, wake_up_event; escalated flag; assignment_state Unassigned/Queued/Assigned; assignee user/group/queue; assigned_by/at, method, rule_ref; subject, description (P1, or P3 when the object is P3); priority; mandatory, recurring; target_at, escalation_at; calendar_id + calendar_version; sla_policy_id, sla_elapsed_seconds, sla_paused_seconds; clock_ref (CMP ClockInstance; dates read-only when set); outcome_code, completion_note_id, closed_at/by, close_reason; created_by_module, cause_ref, dedup_key (unique among Open); predecessor_activity_id; priority_score + score_factors; origin LIVE/MIGRATION; legacy_ref | See below |
| **ActivityLink** | object_type/id/owning_module/display_number; link_role Primary/Secondary/Evidence (exactly one Primary); valid_from/to (a re-point ends the old link) | — |
| **ActivityChecklistItemState**, **ActivityHistory** | History is append-only (state, assignment and date changes) | — |
| **Group** | code, name GR/EN, type, description; parent_group_id (no cycles); organisation_ref; supervisor_user_id (must be a manager member); security_zone (→ PLT ABAC); default_load_factor 0–200; working_location (pack region, used to pick the calendar); privileged flag (membership changes need maker-checker); producer_codes; regions | Active / Inactive (Inactive only when no open items or rules reference it) |
| **GroupMembership** | group_id, user_id (unique active pair); member, manager; load_factor, load_factor_permission; valid period | — |
| **UserSkill** | user, skill_code, level, validity | — |
| **Absence** | user, type, period, cover user per group, open-items handling, status message. Sickness is stored as "Absence – other" unless the entity enables the sickness type (avoids health data) | Scheduled → Active → Ended; Scheduled/Active → Revoked |
| **Delegation** (work delegation) | delegator, delegate, scope (view/act on the work list), period | Scheduled / Active / Ended / Revoked |
| **Queue** | code, names, owning group; mode Push/Pull/Hybrid; ordering (priority score / due date / FIFO); cherry_pick; max_claimed_per_user (default 10); visibility groups; throttle; paused flag; status | — |
| **AssignmentRuleSet / AssignmentRule** | rule_set version, status, effective_from, maker, checker. Rows: order, condition (decision-table expression evaluated by `REQ-PLT-007`), outcome type/target, strategy, required skills, authority type, fallback | Draft → PendingApproval → Active → Superseded |
| **SlaPolicy** | scope, target, warning % (default 75), escalation, day basis, pause reasons, version | Same config lifecycle |
| **EventTrigger** | event_type, schema_version range, condition, pattern code, link mapping, assignment hint, dedup key fields, version, status | Same config lifecycle |
| **BackOfficeRequest** | request_number; type + version; requester_realm (Intermediary/Customer/BankStaff/StaffOnBehalf); requester party/user/producer_code; object; typed data JSON; status; channel (shared Channel code list, R-84: `BROKER_PORTAL`, `AGENT_PORTAL`, `WEB_DIRECT`, `APP`, `PARTNER_API`, `CONTACT_CENTRE`, `BANK_BRANCH`, `BANK_EMBEDDED`, `STAFF`, `AI_AGENT`); activity_id; linked_transaction_ref; complaint_ref; SLA fields | Submitted → InReview ⇄ AwaitingRequester; InReview → Answered → Closed (or Answered → InReview on follow-up within the window); Submitted → Withdrawn; InReview → Rejected (incl. Redirected to complaint) |
| **RequestType** (catalogue), **RequestMessage** | Message: author realm/ref, text, attachments (→ InboundDocument ids), visibility Requester/Internal, sent_at, ai_interaction_id | — |
| **Note** | topic_code; confidentiality General/Sensitive/Legal/SpecialCategoryHealth; primary and secondary objects; current_version_no; author (immutable); created_at; language (ISO 639-1); pinned; intermediary_visible (only when General); soft-delete fields; activity_id; party_mentions (for DSAR); origin, legacy_author_text | Active → (edit → NoteVersion; `NoteRevised`) → Deleted (soft; `NoteDeleted`) → Purged. "Restricted" is a separate DSAR flag |
| **NoteVersion** | subject, text (field-level encrypted when P3), edited_by/at, reason | — |
| **NoteTopic**, **NoteTemplate** | config | — |
| **InboundEnvelope** | inbound number; intake route EMAIL/UPLOAD/SCAN/API/STAFF_UPLOAD/MIGRATION; originating Channel; sender; received_at; message metadata (subject, message-id); source realm; ack sent; status | — |
| **InboundDocument** | inbound_number; envelope, parent_document_id, page_range (split children); filename, media type, size, sha256; archive_ref, rendition_refs, redacted_ref; scan_status Pending/Clean/Infected/Unscannable (Infected ⇒ no archive_ref); status; class_code / source / confidence; confidentiality; recognised_text_ref (encrypted store); duplicate_of; signature_evidence; verified_by/at; retention_class; origin | See below |
| **ClassificationResult** | classifier Rule/AI, version, class, confidence, split proposals, ai_interaction_id | — |
| **ExtractionField** | field code, typed value, confidence, page, region, source Rule/AI/Person, status Suggested/Accepted/Edited/Rejected, final value, decided_by, ai_interaction_id | Suggested → Accepted / Edited / Rejected |
| **DocumentLink** | object ref; role Primary/Secondary; source Suggested/Manual; evidence; valid period; linked_by | — |
| **DocumentClass** | Catalogue (pack-supplied plus entity additions; see SCR-WRK-26) | — |
| **Participant / ParticipantRole** | object ref, role code, user_id, group_id, valid period, method Rule/Manual/Bulk/Inherited/Migration | — |
| **SavedSearch** (P1), **RecentItem** (max 50 per user, 90-day retention), **StaffNotification** (1-year retention), **StaffNotificationPreference**, **CommandRegistryEntry** | — | — |
| **SearchDocumentView** (read model) | type, id, numbers, display names (native and Latin), normalised tokens, status, entity, jurisdiction, producer code, confidentiality, owner, master-system marker (`REQ-WRK-400`). P2 tokens are hashed | — |

**Activity lifecycle (§7.3, `REQ-WRK-050`, `BR-WRK-002`).**
- Inside Open: [New → InProgress ⇄ Waiting; New → Waiting]. Waiting returns to InProgress on the wake-up date or wake-up event.
- Open → Completed when: the outcome is valid, the checklist is done, and for approvals the owner has confirmed. Publishes `ActivityCompleted`.
- Open → Skipped when not mandatory. Publishes `ActivitySkipped`.
- Open → Cancelled by the raising module, a workflow, or a user with permission. Publishes `ActivityCancelled`.
- Escalated is a flag on Open. `ActivityEscalated` fires once per escalation date; changing the date re-arms it (BR-WRK-010).
- The in-progress, waiting and escalated states in the source prompt are mapped to sub-states and flags (§3.3 A4).

**Assignment-state machine (§7.3).**
- Unassigned → Queued (a rule sends it to a queue) → Assigned (push, claim or manual).
- Assigned → Queued on release, absence or deactivation.
- Any state → Unassigned only when no fallback exists, and this raises an alert.

**InboundDocument lifecycle (§7.3).**
- Received → Scanning.
- Scanning → Quarantined (infected or unscannable), or Scanning → Classified (clean). Classified publishes `DocumentClassified`.
- Classified → NeedsAttention (low confidence, missing required data, duplicate, inconsistency) or → Verified. Verified publishes `DocumentLinked`.
- NeedsAttention → Verified or → Rejected; Classified → Rejected.
- Quarantined → Rejected, or → Scanning after release and rescan.
- Verified → Verified on re-link.
- `DocumentReceived` is published after the archive write, or at quarantine (with no archive ref).

---

## 4. Consumed entities / dependencies (§7.2, §9.2, §15.1)

| Entity | Owner | Via |
|---|---|---|
| User, Role, Permission, ABAC attributes | PLT | `REQ-PLT-001` queries and policy decisions (assumes a bulk ABAC PDP, §16.2 #1) |
| AuthorityType / Profile / Grant | PLT | `plt.Authority.check` (`REQ-PLT-003`); `AuthorityGrantChanged` event |
| ApprovalRequest | PLT | `REQ-PLT-004`; `ApprovalRequested` / `ApprovalDecided` (`REQ-PLT-116`) |
| Calendar / Holiday | PLT | `plt.Calendar.addBusinessDays / nextBusinessDay / businessDaysBetween / isBusinessDay` (`REQ-PLT-009`, `REQ-PLT-197`), which return calendar versions |
| RetentionPolicy / LegalHold | PLT | `REQ-PLT-011` |
| AiInteractionRecord, AiToggle, model gateway | PLT | `REQ-PLT-010` |
| Malware scan / quarantine | PLT | `REQ-PLT-161` (R-06) |
| Numbering | PLT | `REQ-PLT-014` / SPI `NumberingScheme` |
| Integration hub (mailbox, SMTP, HR feed, scan drop) | PLT | `REQ-PLT-006` |
| Workflow engine and rules runtime | PLT | `REQ-PLT-007` (`plt.Workflow.*`, `plt.DecisionTable.evaluate/test/activate/explain`) — **conflict** |
| Party, Account, ProducerCode, ProducerOfRecord, vulnerability flag, preferences, address | PTY | `REQ-PTY-001/-004/-005/-008/-009/-011/-012`; events `PartyUpdated`, `PartiesMerged`, `AccountMerged`, `ProducerOfRecordChanged` |
| Policy, PolicyTerm, Job | POL | `REQ-POL-001/-002/-011/-014`; `JobView` read model fed by POL events |
| Claim, Exposure | CLM | `REQ-CLM-009` status, `REQ-CLM-011` search (`clm.Claim.search`), `REQ-CLM-001` FNOL draft, `REQ-CLM-003` `clm.TransactionSet.approve`; `ClaimView` |
| BillingAccount, Refund, Disbursement | BIL | `REQ-BIL-007` `bil.Refund.decide`; `REQ-BIL-009` `bil.Disbursement.*` |
| Complaint, ClockInstance, DsarRequest, AI register, obligations register | CMP | `REQ-CMP-003/-004/-005/-006/-007` |
| Templates, archive, Documents tab, signature validation | DOC | `REQ-DOC-001/-002/-004/-006/-008` |
| Config keys, packs, capability switches, language rules, transliteration | MKT | `REQ-MKT-001/-002/-003/-004/-005/-008/-091`; `NameTransliterator.searchVariants`; `InboundDocumentProfile` SPI |
| Product codes, LoB, product availability | PFC | `REQ-PFC-001/-010/-011` |
| Transaction-permission matrix, MCP facade, portal notifications | CHN | `REQ-CHN-002/-003/-009` |
| Model registry | DAT | `REQ-DAT-005` |
| Import, xref, coexistence | MIG | `REQ-MIG-001/-002/-004/-005/-148` |

**Read models inside WRK** (built from events; WRK never reads another module's tables, §5.16 `REQ-WRK-325`):
- `SearchDocumentView` (party, account, policy, job, claim, billing account, complaint, request, activity, note metadata, inbound metadata);
- `JobView`;
- `ClaimView`;
- `AccountView` / `PartyView`.

---

## 5. Events

Topic named `wrk.events.v1`. Payloads carry ids and minimal data only: no note text, no extraction values, no P2/P3 data (§8; NFR-WRK-011). The catalogue of record is PRD-18 §8 (D5). Consumers are idempotent on `event_id` plus the trigger dedup key; out-of-order events are handled with a per-aggregate `sequence`.

### 5.1 Produced (§8.1)

| Event | Trigger | Key payload | Consumers |
|---|---|---|---|
| ActivityCreated | Created | number, pattern code + version, category, primary and secondary refs, priority, target_at, escalation_at, created_by_module, cause_ref, **mandatory, gate_blocking** | POL (`ActivityGateView`, R-83), CHN, DAT |
| ActivityAssigned | Any assignment change | previous and new assignee, method, rule ref, reason | UW, DAT |
| ActivityCompleted | Completed | outcome, closed_at/by, primary ref, pattern, elapsed, SLA status | PTY, UW, POL, BIL, CLM, RI, FIN, CMP (clock evidence), PLT, MIG, DAT |
| ActivityEscalated | Escalation time reached | pattern, actions applied, new priority/assignee | UW, DAT |
| SLABreached | Open past target at the sweep, or closed after target | SLA policy, breach minutes, pattern or request type, channel | UW, PLT, DAT |
| NoteAdded | Note created | object ref, topic, confidentiality, author, language (no text) | DAT |
| DocumentReceived | Archived or quarantined | envelope, channel, media type, size, scan status, archive ref | CLM, DAT |
| DocumentClassified | Classification stored | class, source, confidence, split flag | PTY, UW, CLM, DAT |
| DocumentLinked | Verification or re-link | object refs, class, confidence, verified_by, previous link; signature validity (`REQ-WRK-404`) | PTY, UW, CLM, DOC, CHN, DAT |
| RequestAnswered | Final reply | number, type, requester, object, answered_at, SLA status | UW, CHN, DAT |
| ActivitySkipped / ActivityCancelled | Skip / cancel | reason (+ actor) | POL, DAT |
| RequestReceived / RequestClosed | Submit / close | type, realm, object, channel / final status, reason | DAT / CHN, DAT |
| DocumentVerified | Verification complete (incl. "No business object") | class, verified_by | UW, DOC, DAT |
| DocumentQuarantined | Malware or unscannable | reason, channel | DOC, DAT |
| NoteRevised / NoteDeleted | Edit / soft delete | version, actor, reason | DAT |
| ParticipantChanged | Add / change / end | role, old and new user/group, method | DAT |

`REQ-WRK-402` (Must): the four activity lifecycle events must carry primary object, pattern code, mandatory, gate_blocking, state and sequence. POL's bind then evaluates gates from its own `ActivityGateView` without calling WRK.

### 5.2 Consumed (§8.2), grouped

| Producer | Events | WRK reaction |
|---|---|---|
| PTY | PartyUpdated (with backdated flag, CCR-WRK-06), PartiesMerged/PartyUnmerged, AccountCreated/AccountMerged/PolicyMoveRequested, SanctionsHitRaised/Cleared, ProducerOfRecordChanged | Contact-changed activity `REQ-WRK-190`; re-point links/notes/participants/docs `REQ-WRK-070`; sanctions activity + auto-close; proposed bulk reassignment `REQ-WRK-133`; projection |
| UW | UWIssueRaised/Approved/Rejected, ApprovalInvalidated, ReferralAssigned, ReferralSLABreached, ContingencyCreated/Overdue/Resolved, PolicyHoldActivated/Released, InspectionCompleted, ExternalReportReceived | Triggers, auto-close, notifications |
| POL | SubmissionCreated, QuoteIssued, QuoteExpired, PolicyBound, PolicyIssued, RenewalCreated/Offered/Bound, PolicyNonRenewed, PolicyLapsed, CancellationScheduled/Rescinded, PolicyCancelled, PolicyReinstated, TransactionReversed/Reapplied, JobPreempted | Projection; triggers. Out-of-sequence handling reads `set_id`/`set_size`/`index` (D4) so one activity is raised per claim per set |
| BIL | DelinquencyStarted, NonPaymentNoticeSent, CancellationForNonPaymentRequested, DisbursementRejected, DisbursementStopped | Triggers; disbursement follow-ups and cancellation of open approvals (`REQ-WRK-405`) |
| CLM | ClaimReported/Closed/Reopened, CoverageVerified, ReverificationRequired, FraudScoreReceived | Triage trigger, close behaviour `REQ-WRK-071`, triggers by band |
| RI | CessionExceptionRaised | Trigger |
| FIN | ReconciliationBreakRaised/Resolved, PeriodClosed, PeriodReopened | Triggers / auto-close (`REQ-WRK-198`) |
| DOC | DocumentRendered/RenderFailed/Delivered, DeliveryFailed, SignatureCompleted/Declined | Triggers, auto-close, evidence links |
| CMP | FiscalDocRejected/Registered, BureauLagExceeded, **ClockStarted/Warned/Breached/Met/Elapsed**, ComplaintReceived/Answered, DSARReceived/Completed | Clock activities `REQ-WRK-192/-193`; ClockStarted stored for the 60-s monitor `REQ-WRK-194` |
| PLT | IncidentDeclared, ApprovalRequested/Decided, UserDeprovisioned, UserAccessChanged, AuthorityGrantChanged, AiToggleChanged, AiKillSwitchActivated, ConfigChanged, FeatureFlagChanged | Incident tasks; PLT-APPROVAL activity; leaver re-route within 15 min; eligibility re-check; AI fallback within 60 s; cache reload |
| MKT | ConfigurationActivated, PackActivated, PackRolledBack | Reload calendar bindings, classes, request types |
| DAT | ModelDriftDetected, BiasThresholdBreached | Flag or disable AI feature; PLT-AI-DRIFT activity |
| MIG | MigrationWaveStatusChanged, CoexistenceMasterChanged, MigrationBatchLoaded/Reconciled | Planning activities; master marker within 10 s; projection rebuild, break activities |

**Explicitly not consumed:**
- `BusinessEventSuspended` (FIN);
- `ClauseVersionPublished` and `TemplateVersionPublished` (DOC);
- `DataReconciliationBreakRaised/Resolved` (DAT);
- `ProductRegulatoryAlertRaised` (PFC);
- `ReferToBackOfficeCreated` (CHN);
- **`StatutoryOfferDue` / `StatutoryOfferBreached` (CLM).** Statutory work comes only from CMP clock events (XMR-F-105).

---

## 6. APIs

### 6.1 Exposed (§9.1)

All operations follow contract §3.5:
- names `wrk.<Resource>.<op>`, REST `/api/wrk/v1/...`;
- `Idempotency-Key` on every command;
- RFC 9457 errors with `WRK-ERR-*` codes;
- cursor pagination and ABAC filtering on lists.

| Family | Operations | Notes |
|---|---|---|
| Activity | create (dry-run), get, list, update, assign (claim/release/"rules"/"me"; dry-run), complete, skip, cancel, bulkReassign (dry-run; ≤ 50,000), blockingStatus | Errors include PATTERN-NOT-APPLICABLE, OBJECT-ACCESS, DUPLICATE (returns existing), STALE, PERMISSION, CLOCK-READ-ONLY, TARGET-INELIGIBLE, OUTCOME-REQUIRED, NOTE-REQUIRED, CHECKLIST, MANDATORY, LIMIT-EXCEEDED |
| Assignment | simulate | Pure dry-run returning the rule path and rejected candidates with reasons |
| Queue | getNext (QUEUE-EMPTY), Queue/Group/Membership/Skill CRUD | — |
| Config | ActivityPattern.* (TRANSLATION-MISSING, ESCALATION-BEFORE-TARGET), AssignmentRuleSet.*, SlaPolicy.*, EventTrigger.* | Maker-checker; test, replay or dry-run |
| Availability | Absence.*, Delegation.* (OVERLAP) | — |
| Supervision | Team.summary, Export.create (file in the DOC archive with short retention) | — |
| Request | submit (validation dry-run), get, list, reply, requestInfo, withdraw, close, toComplaint | OBJECT-ACCESS, VALIDATION, CAPABILITY-OFF |
| Note | create, get, list, search, edit, delete, history, export | EDIT-WINDOW, PERMISSION |
| InboundDocument | submit (streamed files), get, list, verify (completeness dry-run), split, link, relink, reject, redact, reprocess, export | FILE-TYPE, FILE-SIZE, CAPABILITY-OFF, REQUIRED-MISSING, P3-PERMISSION |
| Participant | assignByRules, list, objectsFor, change | PARTICIPANT-ACCESS |
| Navigation | Desktop.get, Search.global, Palette.query, RecentItem.list, SavedSearch.*, CommandRegistry.register | — |
| Notification | Notification.*, NotificationPreference.*, Follow.* | MANDATORY-TYPE |
| DSAR | DataSubject.export / erase (dry-run) / restrict | — |
| Migration | Import.* (activities, notes, groups, participants, inbound metadata) | Dry-run, idempotent, origin MIGRATION |

Other error codes in the text: `WRK-ERR-DUPLICATE-CODE`, `-USER-INACTIVE`, `-PARTICIPANT-ACCESS`, `-PROVIDER-NOT-REGISTERED`, `-CAPABILITY-OFF`, `-FILE-TYPE`.

### 6.2 Consumed

See §4. The notable synchronous calls are:
- `plt.Authority.check` (at routing, at claim time and at completion: `REQ-WRK-203`);
- the PLT calendar service;
- `plt.DecisionTable.*`;
- PLT malware scan;
- DOC archive store and retention update;
- owner decision APIs: `clm.TransactionSet.approve`, `bil.Refund.decide`, `bil.Disbursement.*`;
- `clm.Claim.search`, `pty.Party.search`, POL search (search federation and link suggestions);
- `REQ-CLM-001` FNOL draft hand-off;
- `REQ-CMP-004` complaint intake;
- `REQ-CMP-005` disclosure log;
- `REQ-CHN-002` permission matrix;
- `REQ-CHN-009` notifications.

---

## 7. SPIs / country-pack interfaces

**Used by WRK.**

| SPI | Purpose |
|---|---|
| `NameTransliterator.searchVariants` (`REQ-MKT-091`) | The only source of cross-script and Greeklish variants (`REQ-WRK-326`) |
| `NumberingScheme` | Activity, request and inbound number formats |
| **`InboundDocumentProfile`** | Defined at WRK's request (CCR-WRK-05, R-05, contract §3.5.8): country-specific document classes, extraction schemas, deterministic parsers (MRZ/QR) and confidence thresholds |

**Also relevant.**
- MKT language rules (`REQ-MKT-005`).
- `HolidayCalendarProvider`: explicitly **not** called by WRK. Holiday content reaches WRK only through the PLT calendar service (CD-17, XMR-F-306, `REQ-WRK-080`).
- `StatutoryClockSet` and `ComplaintRules`: CMP/MKT-owned; WRK consumes only the resulting clock events.
- `DocumentLanguageRule`: governs customer-facing outbound text requested through DOC (`REQ-WRK-407`).
- `ClaimsHistoryFormat`: pack data referenced for the seed pattern UW-CLAIMS-HISTORY.

Pack data supplied for WRK (`REQ-WRK-395`): calendar binding, document classes, retention class periods, request types. A Cyprus stub runs the WRK golden suite in CI.

---

## 8. Screens (§6)

**General rules for every WRK screen.**
- Inspiration-board patterns only; no styling specified.
- Keyboard list navigation (`IB-02`), density (`IB-16`), column chooser and sort (`REQ-WRK-393`), status indicators (`IB-21`).
- States: loading, empty, error (RFC 9457 localised), permission (object hidden; actions disabled with reason), offline/degraded (stale).
- Permission codes are `wrk.<area>.<action>`.
- The PLT shell owns the chrome.

**R-101 language switch (Must P1).**
- Every screen switches Greek ↔ English without reload and without losing unsaved input.
- Bilingual reference data is relabelled; business data is shown as stored.
- A missing translation fails the release gate.
- The page `lang` attribute updates.

| ID | Name | Personas | One line |
|---|---|---|---|
| SCR-WRK-01 | My Desktop | ROLE-03/04/10/11/12/16/17/22/31/45 | Work-left home (`IB-11`): counters (activities, submissions, policy changes, renewals, cancellations, claims, requests, each with overdue sub-count); priority-ranked list with explain popover; read-only POL job lists; My Accounts; split pane; stale state if the projection lags > 30 s |
| SCR-WRK-02 | My activities | Staff | Full worklist with filters incl. "Delegated to me"; bulk Assign/Skip/Complete; export |
| SCR-WRK-03 | Queue view / claim | Queue members, supervisors | Get next, claim (cherry-pick), release with reason, pause queue, SLA countdown, required skill, age |
| SCR-WRK-04 | Activity detail | Assignee | Edit-in-place with autosave; outcome; checklist; inline note; history; Complete / Complete-and-create-new / Skip / Waiting / follow-up / doc from template; Approve/Decline calls the owner module |
| SCR-WRK-05 | Create activity | Staff | Pattern search; computed dates showing the calendar used; assignment preview via `wrk.Assignment.simulate` |
| SCR-WRK-06 | Assign / reassign | Staff, supervisors | Suggested list (load n/cap) or find user/group/queue; reason required; bulk distribution preview; ineligible targets disabled with reason |
| SCR-WRK-07 | Object workplan | Embedded in POL/UW/CLM/BIL | All activities on an object |
| SCR-WRK-08 | Team view | ROLE-45/18/13, office manager | Person × age-band matrix; capacity n/cap; unassigned list; bulk reassign; move queue; pause; surge mode (maker-checker); export |
| SCR-WRK-09 | Work delegation and out-of-office | All, managers | Absences, cover per group, open-items handling, work delegation; PLT authority delegations shown read-only |
| SCR-WRK-10 | Notes panel | All | Composer (topic, related-to, confidentiality, intermediary-visible, pin); live accent-insensitive filter; edit history; soft delete |
| SCR-WRK-11 | Intake inbox | Intake clerk, ROLE-16/17/04 | Every envelope from every channel; status, class + confidence band, route + channel, SLA countdown; preview with rules/AI summary and source email |
| SCR-WRK-12 | Verification workbench | Intake clerk | Split pane: data sections + document viewer; per-field Accept/Edit/Reject with source highlight; split editor; duplicate; confidentiality; links; redaction; tabs Data/Correspondence/Documents/Ask/History. The SIU flag is removed (CLM decides) |
| SCR-WRK-13 | Inbound documents for an object | All | Inbound rows inside DOC's Documents tab; upload, re-link, redact, audited download |
| SCR-WRK-14 | SLA dashboard | Supervisors, ops | On-time rate, breaches, ageing, time to complete avg/p90, statutory warnings by days left, intake; stale if facts are > 15 min old |
| SCR-WRK-15 | Command palette / global search / recent items | All | Records / Go to / Actions; prefixes acc, pol, sub, job, clm, req, act, doc; facets; saved searches; NL mode (AI); Switch-language action |
| SCR-WRK-16 | Participants | All | Role, user, group, validity, method; presence indicators |
| SCR-WRK-17 | Activity pattern catalogue | WRK admin | List, filter, retire, export/import; includes a baseline seed-pattern disposition table |
| SCR-WRK-18 | Pattern editor / detail | WRK admin | All pattern attributes; activation via maker-checker; compare versions |
| SCR-WRK-19 | Groups | WRK admin | Tree; tabs Basics, Members, Producer codes, Queues, Regions, Skills |
| SCR-WRK-20 | User picker | Admin | PLT users plus WRK group info; "only unassigned users" |
| SCR-WRK-21 | Queues and assignment rules | WRK admin | Queue form; decision-table editor with AND/OR builder; simulator; golden-test diff; maker-checker |
| SCR-WRK-22 | Back-office request workspace | CSR, ROLE-04, ROLE-22 | Thread, internal notes, reply (final flag, templates, AI draft), ask for info, start transaction, register as complaint |
| SCR-WRK-23 | Notification centre and preferences | All | Per-type matrix (in-app / email now / digest / off); mandatory types locked; quiet hours; follows |
| SCR-WRK-24 | Calendar and diary | All; claim diary for CLM | Day/week/month/list; drag to reschedule with reason; statutory items read-only; iCal export |
| SCR-WRK-25 | SLA policies, escalation, event triggers | WRK admin | SLA policy fields; trigger table (event, condition, pattern, link mapping, hint, dedup key); dry-run over last N days |
| SCR-WRK-26 | Intake channels, document classes, request types, note topics | WRK admin, ROLE-37, ROLE-30 | Mailboxes, scan drop points, class catalogue (thresholds, verification mode, AI allowed, P3 flag), request types, topics, templates |

The PRD admits (§16.6) that `SCR-WRK-21`, `-25` and `-26` are specified at attribute level only, without full field tables.

**Design-guide patterns referenced (IB-nn = inspiration-board pattern; UIL-xx = UI-library screen).**

| Code | Pattern |
|---|---|
| IB-01 | Command palette |
| IB-02 | Keyboard list navigation (J/K) |
| IB-03 | Sidebar counts |
| IB-04 | Priority-ranked queue |
| IB-05 | Split pane |
| IB-07 | Approve-the-diff |
| IB-08 | Explain-why popover |
| IB-09 | AI summary with citations |
| IB-10 | Streaming draft |
| IB-11 | Work-left home |
| IB-12 | Progress to done |
| IB-14 | Edit in place |
| IB-16 | Density setting |
| IB-17 | Hero metric |
| IB-18 | Floating overlay |
| IB-19 | Presence indicators |
| IB-21 | Status indicators |
| IB-23 | State-change motion |
| IB-24 | Quiet chrome |
| IB-26 | Global filter bar / applied filters |
| IB-28 | Exception-first |
| IB-29 | Dense table |
| IB-30 | Conversational analytics (DAT) |
| IB-32 | Pass/fail health tiles |

Consumed UI-library rows that embed WRK components: UIL-U1, U2, C2, C7, C8, C9, C10, K1, I1, A3.

---

## 9. Regulatory, tax and statutory rules

### 9.1 Stated explicitly in the source

| Rule | Value as stated | Where |
|---|---|---|
| Complaint reply period | "50-calendar-day reasoned reply", from Bank of Greece Executive Committee Act 88/5.4.2016 (FEK B 1109/19.04.2016). CMP holds the verification of record | §3.1 OBL-COMP; §3.3 A2; OI-WRK-11 closed |
| Motor claims offer | "reasoned offer or reply within three months", via CMP clocks; status "Verify"; owned by CLM/CMP | §3.1 OBL-MOT |
| Prescription of insurance claims | Law 2496/1997 Art. 10: four years for damage insurance, five for personal insurance, from the end of the year in which the claim arose (secondary source). Civil Code general limitation is 20 years | §3.1 OBL-CON; F10 |
| AI Act transparency | Art. 50 applies from **2 August 2026**; Commission guidelines adopted 20 July 2026 (secondary). The Digital Omnibus (Regulation (EU) 2026/1744) postpones stand-alone Annex III high-risk obligations | §3.1 OBL-AIA; F7, F8 |
| AI Act Annex III 4(b) | Task allocation based on individual behaviour or traits, and monitoring of workers, is high-risk → `BR-WRK-041`, `REQ-WRK-176`, CCR-WRK-07. Wording UNVERIFIED (`OI-WRK-03`) | F9 |
| GDPR | Art. 5(1)(c)/(e), 9, 12(3) (one-month reply), 15, 17, 18, 25, 30, 32, 35; Greek Law 4624/2019 Art. 22 (special categories) and Art. 27 (employee data) | §3.1 |
| DORA | Art. 28/30: no external document-AI, OCR or scanning provider unless recorded in the PLT register of information with EU location and exit plan; the rules-only path is the tested exit (`REQ-WRK-401`, Must) | §3.1 OBL-DORA |
| EIOPA cloud outsourcing | EIOPA-BoS-20-002 (from 1 Jan 2021): EU-only processing (`NFR-WRK-012`) | OBL-RES |
| eIDAS | Regulation 910/2014 as amended by 2024/1183: keep signature and validation evidence; show whether a QES is present (`REQ-WRK-404`, Must) | OBL-EIDAS |
| EAA / accessibility | Directive 2019/882; Greek Law 4994/2022; WCAG 2.2 AA (`REQ-WRK-394`, `NFR-WRK-015`) | OBL-EAA (R-57) |
| Greek ID cards | Old cards stop being valid as travel documents from **3 August 2026** and lose validity for public dealings in **September 2027**; a 12-digit personal number is being introduced (secondary) | F6; `OI-WRK-04` |
| Search query retention | Raw query text is not kept beyond 24 h (`REQ-WRK-337`, `BR-WRK-063`). Internal rule, justified by GDPR minimisation | — |

**Internal operating defaults stated** (configuration, not law):

| Setting | Default |
|---|---|
| Note edit window | 24 h |
| Workload cap | 40 |
| Sticky routing window | 10 business days |
| SLA warning | 75% |
| Request reopen window | 10 business days |
| Max file size | 50 MB |
| Duplicate window | 30 days |
| Confidence thresholds (BR-WRK-031) | identity numbers 0.98, dates 0.95, amounts 0.95, free text 0.85 |
| Bulk reassignment limit | 50,000 |
| Recent items | 50 |
| RecentItem retention | 90 days |
| StaffNotification retention | 1 year |

### 9.2 Referenced but not specified (gaps)

- **Retention durations.** The prompt's "20 years after policy end" and "5 years for declined applications" were **not found in Greek statute** (§3.3 A1). WRK defines classes only. Durations come from "the one programme retention schedule" loaded as Greece-pack data under `REQ-PLT-011` before system test (XMR-D-259; `BR-WRK-060`; RK-WRK-07). The schedule itself is not in this PRD.
- **Bank of Greece acts** 60/2016 (governance) and 180/2020 (cloud outsourcing), and Credit and Insurance Committee decision 122/3/15.12.2014: **UNVERIFIED**, tracked in CMP `OI-CMP-10`.
- **Motor statutory-offer clock.** Durations and start points live in CMP `StatutoryClockSet`; contract status "Verify".
- **D7 clock codes:** `CLM_REPAIR_IN_KIND`, `POL_INSURER_TERMINATION_NOTICE`, `UW_AMENDMENT_ACCEPTANCE`, `POL_MTPL_THIRDPARTY_NOTICE` ("if confirmed"), the distance-withdrawal long stop, and FIXED_DATE renewal/non-renewal notices. Named only; durations are not given.
- **Article-level citations** that rest on secondary sources (`OI-WRK-02`).
- **Lawful basis for P3 AI processing** (GDPR Art. 9(2)(f) / Law 4624/2019 Art. 22, "to be confirmed by the DPO", `OI-WRK-07`).
- **DPIA and works-council consultation** for team views and AI-WRK-05 (`OI-WRK-06`).

### 9.3 Deferred to configuration or the country pack

- Holidays (via PLT).
- Clock codes and durations (CMP/MKT).
- Document classes, extraction schemas, thresholds (`InboundDocumentProfile`).
- Retention periods.
- Request types.
- Numbering formats.
- Language rules and transliteration.
- `ComplaintRules`.
- `DocumentLanguageRule`.
- Capability switches: `wrk.customerRequests` (on), `wrk.aiIntake`, `wrk.icalFeed`, `wrk.hrAbsenceFeed` (off).

"No core requirement in this PRD contains Greek-specific values" (§10.4).

---

## 10. Greek-market specifics

| Topic | What the PRD says |
|---|---|
| AFM | Extracted from vehicle registration certificates (`REQ-WRK-273`); an identifier pattern for search ranking (`REQ-WRK-327`) and link suggestion (`REQ-WRK-272`); strict 0.98 threshold example (`REQ-WRK-275`) |
| myDATA | Not named. Indirectly: Greek invoice class "with MARK QR" (the MARK is the myDATA registration mark), with deterministic QR extraction (`REQ-WRK-274`, §10.4); `CMP-FISCAL-REJECTED` activity on `FiscalDocRejected` (CMP is the only issuer of fiscal series, D3) |
| gov.gr | Only as a source to confirm the ID-card transition (`OI-WRK-04`) |
| Information Centre / bureau | Seed pattern "Get Bureau Data" replaced by an Information Centre check via `REQ-CMP-002`, with an activity only on exception; `CMP-BUREAU-LAG` activity on `BureauLagExceeded` |
| Documents | Old and new GR identity cards (MRZ), vehicle registration certificate, driving licence, KTEO technical inspection certificate, joint accident report (Friendly Settlement form), claims-history certificate from the previous insurer (replaces the US MVR), refusal letter from another insurer. US credit reports are not applicable (`OI-WRK-08`) |
| Calendars | Movable Orthodox Easter-based holidays; golden set 2026–2030 incl. Clean Monday, Good Friday, Easter Monday, Whit Monday, 15 Aug, 28 Oct, regional holidays (e.g. Thessaloniki 26 Oct) |
| Language | Greek + English mandatory for the staff UI (R-101); Greek binding for customer replies via `DocumentLanguageRule`; Greek final-sigma folding and upper-casing; ELOT 743 / Greeklish variants as pack data; accent-insensitive search ("παπαδοπ" ↔ "papadop", "atyxima" ↔ "ατύχημα") |
| Identifiers | AMKA-like pattern detection for raising note confidentiality (`REQ-WRK-249`); plate "ΙΚΧ-1234" and VIN; SSN marked N/A-GR |
| EUR | Example amounts in EUR (refund 3,000 EUR; approval 12,000 EUR; CMP_REDRESS 800 EUR). No currency logic in WRK |
| Greek request types | Change of garaging address, Green Card request, claims-history certificate request |
| Regulator | Bank of Greece (complaints Act 88/2016) |

---

## 11. Controls (§12)

**Authority types.** WRK registers **none for money**. Approval activities carry the owning module's authority types (e.g. `BIL.REFUND`, `CLM.PAYMENT`, `BIL.DISBURSEMENT`); WRK only routes by them (`REQ-WRK-200/-201/-203/-205`, `-405`). Authority is re-checked at claim time and at completion (`-203`), and on `AuthorityGrantChanged` (`-398`).

**PLT permissions registered:**
- `wrk.activity.cancelMandatory`, `.mandatoryOverride`, `.completeAny`;
- `wrk.note.correct`, `.delete`, `wrk.note.level.<level>`;
- `wrk.intake.verify`, `.redact`, `.unredactedView` (P3);
- `wrk.team.view`, `.bulkReassign`;
- `wrk.export`;
- `wrk.admin.*`.

The screens also use: `wrk.activity.read/edit/priority/dates/assignOther/create`, `wrk.note.create/shareIntermediary/pin`, `wrk.request.read/reply`, `wrk.intake.read`, `wrk.absence.manage`, `wrk.participant.edit`, `wrk.admin.pattern/group`.

**Maker-checker list (`REQ-PLT-004`):**
- assignment rule-set activation;
- event-trigger activation;
- SLA policy activation at legal-entity layer;
- pattern activation when the pattern is gate-blocking or mandatory (SCR-WRK-18 says activation "via maker-checker for legal-entity scope", which is broader);
- membership changes of privileged groups;
- document-class confidentiality or retention changes;
- note-topic retention changes;
- surge-mode activation;
- AI toggles at tenant and legal-entity level (PLT-run);
- release of quarantined files (security officer as checker).

**Segregation of duties:**
- The maker is never a candidate for their own approval activity, even if they are the only group member: the item goes to the fallback queue (`REQ-WRK-134`, `-204`; `WRK-ERR-TARGET-INELIGIBLE` "Maker"). This also excludes users barred by a PLT SoD rule.
- No self-verification of one's own expense or vendor invoice where the class bars it.
- A team leader cannot approve their own rule-set change.
- DSAR exports are released by the DPO in CMP, not by the preparer.

**Audit (`REQ-PLT-002`).** Every change is audited, with field-level before/after values, including:
- per-item results of bulk jobs;
- every verification decision per field;
- reads of Legal and Special-category health notes and documents (`REQ-WRK-380`);
- exports (filter and row count);
- downloads;
- AI outcomes.

`NFR-WRK-013` requires a daily reconciliation of state changes against audit counts.

**GDPR handling:**
- **Confidentiality levels:** each maps to a PLT permission (`REQ-WRK-236`). Users may raise a level; only permitted users may lower it (BR-WRK-021).
- **Special-category health (P3):**
  - field-level encryption, keys in the KMS (`NFR-WRK-009`);
  - excluded from event payloads; search shows metadata only to users without P3;
  - no AI processing unless the feature is P3-registered (`REQ-WRK-246`, BR-WRK-036/-042);
  - redaction produces a new archive version; the unredacted original needs P3 (`-283`).
- **Deterministic detection** of health terms and IDs to suggest raising a level (`-249`).
- **DSAR:** export across activities, notes and versions, requests and messages, inbound documents and extraction fields, participants and recent items, including merged parties (`-370`).
- **Erasure and restriction:** restriction replaces erasure when retention applies, and the conflict is recorded (`-371`, BR-WRK-061).
- **Quarterly DSAR completeness test** with seeded data, target 100% (`-372`).
- **Party-mention indexing** in free text (`-373`); DPO third-party redaction before disclosure (`-374`); CMP disclosure log (`-375`).
- **Retention classes, legal hold and purge** including the projection, recent items and notifications (`-376`…`-378`).
- **Declined-submission class** RC-WRK-NOTE-DECLINED (`-379`).
- **Staff monitoring limits:**
  - no individual performance scores or rankings (`-176`);
  - analytics assignee ids pseudonymised (§13);
  - sickness absence stored as "other" by default.
- **Staff emails** carry no P2/P3 data (`-347`, BR-WRK-071).

---

## 12. AI features (§11)

**Works-without-AI statement.**
- The whole module is complete with every AI feature off.
- The acceptance suite runs with the global kill switch on (`REQ-WRK-396`).
- All features ship **off**.
- Every call goes through the PLT model gateway to EU endpoints with zero retention.
- Each call writes an `AiInteractionRecord`; each feature is registered in the CMP AI register and the DAT model registry.
- Fallback timeouts: 5 s for interactive features, 60 s for batch.

**None of the features is required for the Motor MVP.** Decision #5 in §16.5 asks whether AI-WRK-01/02 are in MVP scope at all. Section 1.5 lists AI classification and extraction as "Should (toggle off by default)" for P1.

| ID | Feature | Classification | Gate / threshold | Works without AI |
|---|---|---|---|---|
| AI-WRK-01 | Document classification and split detection | Minimal | ≥ 95% top-1 on the labelled Greek test set before enablement (NFR-WRK-010); weekly drop > 5 pts flags it; MP-D | Rules (channel, cover sheet, request type, keywords) + manual |
| AI-WRK-02 | Field extraction (GR/EN) | Uncertain → high-risk-level controls | ≥ 97% field accuracy on high-volume motor classes; auto-disable per class if edit rate > 30% over 500 docs; script-gap > 5 pts; medical sub-feature off and needs P3 registration; MP-D | MRZ/QR/barcode parsers + manual entry |
| AI-WRK-03 | Intake summary + envelope Q&A ("Ask") | Minimal | Citation validity ≥ 95%; MP-C | Rules summary |
| AI-WRK-04 | Cross-document consistency check | Uncertain → high-risk controls | Must never imply fraud; flag-rate parity 0.8–1.25 auto-disables; MP-E | Deterministic structured-field comparison (`REQ-WRK-292`) |
| AI-WRK-05 | Content-based routing tags | Uncertain → high-risk controls | Tags describe the item, never a person; DPO/works-council review first; parity by customer language 0.8–1.25 auto-disables; tag accuracy ≥ 90%; MP-D | Rules on structured attributes |
| AI-WRK-06 | Note/history summarisation; translation for display | Minimal | MP-C | Notes list, filters, pinned alerts |
| AI-WRK-07 | Drafted replies to requests | Limited (Art. 50) | Mandatory GR/EN disclosure text on sent replies; MP-B | Reply templates (`REQ-WRK-223`) |
| AI-WRK-08 | NL search / command intent | Minimal | Latency ≤ 2 s; MP-F | Lexical search, prefixes, facets |
| AI-WRK-09 | Special-category and identifier detection | Minimal (protective; P3-registered) | Recall ≥ 95% on health terms; false positives ≤ 20%; MP-D | Deterministic patterns and term lists (`REQ-WRK-249`) |
| AI-WRK-10 | Queue backlog forecasting | Minimal | MAPE > 25% for 2 weeks; MP-G | Linear trend (`REQ-WRK-170`) |

Toggle scope: tenant → legal entity → LoB → role → user, plus a per-feature kill switch. On `AiKillSwitchActivated`, features fall back within 60 s. Labels: AI-suggested until accepted, then AI-generated (BR-WRK-040).

---

## 13. Open issues, assumptions, risks, CCRs (§16)

**Open issues.**

| ID | Topic | Status |
|---|---|---|
| OI-WRK-01 | Retention periods | **Closed** per XMR-D-259: durations from the programme retention schedule (`REQ-PLT-011`) |
| OI-WRK-02 | Secondary-source citations (Law 4624/2019, DORA, AI Act Art. 50) | Open; the Omnibus part is closed per R-85 |
| OI-WRK-03 | AI Act Annex III 4(b) wording and scope for staff task allocation | Open |
| OI-WRK-04 | Greek ID card transition and 12-digit number vs extraction schemas and PTY identifiers | Open |
| OI-WRK-05 | Each module confirms or extends its rows of the 5.10 catalogue | Open (UW aligned; CLM offer row replaced by the CMP clock row; BIL D1/D4 rows added) |
| OI-WRK-06 | DPIA + employee-representative consultation for team views and AI-WRK-05 | Open; needed before go-live |
| OI-WRK-07 | Lawful basis for P3 AI (AI-WRK-01, -02 medical, -09) | Open (DPO) |
| OI-WRK-08 | Lawfulness of credit or driving-record sources for GR motor | Open (UW) |
| OI-WRK-09 | Time-saving estimates in §4 | Open (usability tests) |
| OI-WRK-10 | Migrated open activities: keep legacy due dates or recompute | Open; decided per wave |
| OI-WRK-11 | BoG act for the 50-day complaint reply | **Closed** (Act 88/2016; CMP `OI-CMP-06` tracks the remainder) |

**Assumptions (§16.2):**
1. PLT provides a bulk ABAC PDP fast enough for list and search filtering.
2. PLT provides `REQ-PLT-161` malware scanning.
3. The DOC archive supports retention-class updates after storage and multiple renditions.
4. CMP publishes `ClockWarned` with object ref and participant hint for every clock needing human work.
5. Volumes come from contract §3.9.7, to be confirmed by DAT and MIG.
6. Staff email may carry object numbers and generic subjects.
7. GR + EN are sufficient for the MVP staff UI.

**Risks RK-WRK-01…09:**
- catastrophe surge;
- P3 data leakage;
- incomplete catalogue at go-live;
- routing rule errors;
- Greek AI extraction quality;
- staff-monitoring concerns;
- retention schedule not approved before system test;
- search projection lag;
- missing translations blocking release under R-101.

**CCRs (all Accepted in contract v1.6):**

| CCR | What was accepted |
|---|---|
| CCR-WRK-01 (R-01) | WRK-owned entity list added to contract §3.2.2 |
| CCR-WRK-02 (R-02) | 9 additional events |
| CCR-WRK-03 (R-06) | No new anchor; cite `REQ-PLT-161` for malware scanning |
| CCR-WRK-04 (R-07) | CLM anchor `REQ-CLM-011` for claim search |
| CCR-WRK-05 (R-05) | SPI `InboundDocumentProfile` |
| CCR-WRK-06 (R-04) | WRK consumes `PartyUpdated` (with backdated flag) and `ProducerOfRecordChanged` |
| CCR-WRK-07 (R-12) | AI routing hard boundary |

**"Ten decisions before build" (§16.5):**
1. Retention classes vs the schedule.
2. State mapping.
3. Quarantine release model.
4. Confidentiality-level model and permissions.
5. Whether AI intake is in MVP, and which classes go first.
6. AI routing limitation + DPIA.
7. Push/pull/hybrid default per function.
8. Approve the 5.10 catalogue.
9. Migrated-work scope.
10. Mailbox and scanning operating model (in-house vs outsourced; DORA).

---

## 14. Conflicts and ambiguities

### (a) With the infra stack (ARCHITECTURE-DECISIONS.md)

| # | Line / ID | Issue |
|---|---|---|
| C-1 | §1.4 "Machine workflows and long-running processes — PLT workflow engine (`REQ-PLT-007`)"; **`REQ-WRK-090`** "run escalation and SLA sweeps as **workflow-engine timers** that survive failover"; §9.2 "Workflow engine and rules runtime (`plt.Workflow.*` …) — Timers, sweeps, bulk jobs, decision tables"; `REQ-WRK-132` and `-187` require `REQ-PLT-007`; §15.1 row `REQ-WRK-090/-120/-132 → REQ-PLT-007` | **The ADR forbids workflow servers.** It says deadlines are domain records and Hangfire only executes them. `REQ-WRK-090` must be read as follows: `target_at`/`escalation_at`/warning-at stored on Activity rows; Hangfire recurring sweep jobs (and/or delayed jobs) that query due rows; idempotent processing via BR-WRK-010 "once per escalation date" plus a stored escalated/warned marker. That gives "survives failover / process missed timers on recovery" for free because the source of truth is the table. Bulk reassign (`-132`) becomes a Hangfire background job with a job-status table. PLT's PRD must not ship a BPM engine for this |
| C-2 | `REQ-WRK-120`, `-003`, `-135`, §7.1 AssignmentRule "decision-table expression evaluated by `REQ-PLT-007`", D10 "shared typed deterministic rule-expression language compatible with **CEL**", `plt.DecisionTable.evaluate/test/activate/explain` | Not forbidden, but it needs a stack decision: a .NET CEL evaluator (third-party package needing a stated reason under rule 11) or a home-grown typed expression evaluator. "PLT rules runtime" must not turn into a BRMS product. WRK routing depends on it in Wave 1 |
| C-3 | §1.4 "Analytics models and the **lakehouse** (DAT)"; §9.3 row "Lakehouse — Planned (MVP)" | **The ADR forbids a data lakehouse.** Reporting marts are PostgreSQL schemas built by scheduled jobs. The WRK data contracts to DAT (§13) should target PostgreSQL marts |
| C-4 | §8 "Topic `wrk.events.v1`; … **Partition key**: `activity_id` …" | Broker vocabulary (topics, partitions). The ADR uses a transactional outbox dispatched in order to in-process handlers, with no broker. Per-aggregate ordering must come from the outbox dispatcher (per aggregate `sequence`, §8.2) rather than partitions. Harmless if read as a logical channel name |
| C-5 | NFR-WRK-003 "WRK consumers keep pace with the programme event stream sized at ≥ **2,000 events/s sustained**" (D8); NFR-WRK-004: 20,000 activity creations/h + 5,000 docs/h for 72 h | A single-PostgreSQL outbox with in-process dispatch at 2,000 events/s sustained (WRK consumes ~92 event types) is a performance risk. It needs early load testing of the outbox dispatcher design |
| C-6 | §14 "WRK is availability tier T2 … No T1 operation calls WRK synchronously"; `REQ-WRK-069` AC "G WRK unavailable; W POL binds J; T POL evaluates its `ActivityGateView`"; NFR-WRK-008 "if WRK is unavailable, POL bind is unaffected" | In a modular monolith WRK cannot be "unavailable" while POL is up, except for WRK-schema or dispatcher degradation. The R-83 read-model pattern is still valid (module isolation, no cross-schema reads), but per-module availability tiers and chaos tests need reinterpretation for one deployable |
| C-7 | `REQ-WRK-267` (Must) "run **text recognition** on images and scanned PDFs in Greek and English"; NFR-WRK-006 | **No OCR technology in the stack.** OCR is Must even with AI off. Options: a self-hosted OCR container (e.g. Tesseract with Greek data; needs a stated reason) or an Azure OCR service (an external ICT provider → `REQ-WRK-401` DORA register + exit path). Decision needed |
| C-8 | `REQ-WRK-263` / `REQ-PLT-161` malware scanning; NFR-WRK-009 isolated quarantine store | No scanner named in the ADR or INFRASTRUCTURE (e.g. Defender for Storage malware scanning, or a ClamAV container). A separate Blob container is needed for quarantine |
| C-9 | `REQ-WRK-266` normalisation to PDF/A (incl. HEIC photos, DOCX, XLSX, EML, MSG) | Gotenberg (in the stack) converts HTML/Office to PDF/A via LibreOffice; HEIC → web rendition and EML/MSG parsing need extra libraries. Should-level, but needs a decision |
| C-10 | `REQ-WRK-261` mailbox monitoring via "IMAP, JMAP" (§9.3); staff email SMTP | The ADR names Azure Communication Services (email) for **outbound** only. Inbound mailbox ingestion (probably Microsoft Graph / Exchange Online, or IMAP) is not covered by the stack. Scan drop via SFTP → Azure Blob SFTP? Not specified |
| C-11 | `REQ-WRK-325/-326/-333`: global search over 1.5 M docs, palette ≤ 300 ms p95 at 2,000 concurrent users, stemming per language (`-241`), similarity near-duplicates (`-271`) | The ADR offers PostgreSQL full-text search only; there is no search engine. Feasible with tsvector + unaccent/pg_trgm and custom Greek folding from MKT rules, but the latency target with ABAC post-filtering ("do not reveal hidden results in counts", `-328`) needs a design spike. Greek stemming dictionaries in PG are limited |
| C-12 | `REQ-WRK-111` (live sidebar counts within 10 s), `-316` (counters without page reload), `-352`, in-app notifications within 10 s (`REQ-WRK-007`) | Needs server push (SignalR / SSE) or polling. Not in the ADR stack list; TanStack Query polling would satisfy it. Minor |
| C-13 | §9.2 "Integration hub adapters", "AI control plane / model gateway", "MCP facade" (`REQ-WRK-338`, `REQ-CHN-003`) | PLT/CHN components assumed to exist. Not stack conflicts, but WRK intake and notifications are blocked on them |
| C-14 | Vendor names (Guidewire, Pega, Appian, Indico logo, Docsumo, EDC) | Research references only (§16.6, "vendor products cited only as research references"). No conflict |

### (b) With the system contract or other PRDs

- **Gate query vs read model.** §15.1 row "POL | `REQ-WRK-037`, `-069` | `REQ-POL-003` | **Gate uses `wrk.Activity.blockingStatus`** | inbound" contradicts R-83 and `REQ-WRK-069`/`-402`, which say POL bind uses its own `ActivityGateView` fed by events and never calls `blockingStatus`. This is a stale row.
- **Calendar ownership.** `REQ-WRK-080` and BR-WRK-006 put the "working week" at entity layer in WRK config (`wrk.calendar.workingWeek`), while holidays and business-day maths are PLT's. The split between WRK (working week, hours, group schedules `-093`/`-157`) and the PLT calendar service (which receives "calendar set … combined with the legal entity's working week") is not crisp: who computes business *hours*?
- **Catalogue rows depend on events other PRDs must confirm (`OI-WRK-05`).** E.g. `QuoteExpired` "warning offset (trigger on `QuoteIssued` with offset)". The trigger table (`REQ-WRK-187`) has no delay or offset concept, so this needs a delayed-trigger feature or a POL-emitted warning event. Others: `PolicyHoldActivated`, `ExternalReportReceived`, `InspectionCompleted`, `JobPreempted`, `BureauLagExceeded`, `ModelDriftDetected` (WRK features).
- **Disbursement sources.** `RI_SETTLEMENT` appears in `REQ-WRK-405` but not in the 5.10 row or `REQ-WRK-200`.
- **External provider for mailbox/scan (decision #10)** vs DORA `REQ-WRK-401`: unresolved.
- **Back-office request realm `BankStaff`** exists in the model, but bancassurance is not in P1 unless a bank partner is signed by G0 (D6, `REQ-WRK-210`).

### (c) Internal contradictions

- **Tag counts.** Self-check (§16.6) says "192 [BASELINE], 123 [ENHANCEMENT]" = 315, but the table total is 313 (my count is 191 / 122). Must/Should/Could (235/62/16) match.
- **Phase vs catalogue.** `REQ-WRK-189` (Must P1) requires *every* row of 5.10 to exist as an Active pattern/trigger at go-live. But 5.10 includes P3 rows (`UW-NATCAT-RESPONSE`, `UW-CLEARANCE`), `MIG-WAVE-*` (whose requirement `-399` is only Should), `PLT-AI-DRIFT` for features that ship off, and `POL_MTPL_THIRDPARTY_NOTICE` "(if confirmed)".
- **Pattern activation scope.** §12 limits maker-checker to gate-blocking or mandatory patterns; SCR-WRK-18 says "activation via maker-checker for legal-entity scope".
- **Business-hours day basis.** It appears in the pattern editor (SCR-WRK-18) and `NFR`/`REQ-WRK-093`, but `-093` (group working hours) is only Could while FNOL triage (5.10) has a "4 business hours" SLA and DOC-RENDER-FAILED is 4 business hours. If `-093` is Could, which hours does a business-hours SLA use? The entity default Mon–Fri 09:00–17:00 (§10.2).
- **Escalation trigger date.** `REQ-WRK-062` labels "escalated indicator when the escalation date has passed" as semantic state `warning`, and `REQ-WRK-087` also uses `warning` for the SLA warning threshold. The same visual state carries two meanings.
- **SLA dates on patterns and on SlaPolicy.** `REQ-WRK-084` says policy "durations drive the dates", but patterns also carry target and escalation rules (`-040`). Precedence is unstated.
- **P3 encryption vs search.** `REQ-WRK-246`/NFR-WRK-009 require field-level encryption of P3 note text and recognised text, yet `REQ-WRK-241`/`-267`/`-293` require full-text search over them for permitted users. You cannot index encrypted text in PG FTS without storing tokens; the PRD only says "P2 tokens hashed". A design decision is needed.
- **Unsaved drafts.** `REQ-WRK-235` keeps unsaved note drafts "locally per user and object". If local means the browser, P3 health text would sit in browser storage, which conflicts with P3 protection.

### (d) Cannot build without a decision

1. Timer/sweep architecture replacing the "workflow engine" (C-1). Effectively decided by the ADR (Hangfire + domain tables), but the PLT PRD must be aligned.
2. Rule-expression evaluator technology (CEL-compatible, D10) and the PLT decision-table API (C-2).
3. OCR engine and malware scanner choices, with DORA registration where external (C-7, C-8, `REQ-WRK-401`).
4. Inbound mailbox technology and scanning operating model (C-10; §16.5 #10).
5. Search implementation in PostgreSQL meeting 300 ms with ABAC (C-11), and how P3 text is searchable.
6. Confidentiality-level → PLT permission model (§16.5 #4).
7. Programme retention schedule values (`BR-WRK-060`). Classes can be built, but purge cannot be tested without them.
8. Push/pull default per function (§16.5 #7) and the approved 5.10 catalogue (#8, `OI-WRK-05`).
9. DPIA for team views before go-live (`OI-WRK-06`).
10. Whether AI-WRK-01/02 are in MVP (#5). Not blocking, because rules-only is complete.

---

## 15. Build notes

**Hardest parts.**
1. **Routing and assignment engine** (`REQ-WRK-120`…`-142`, `-200`…`-206`, `-397/-398`):
   - decision-table evaluation;
   - strategies (round robin with a persisted pointer, least-loaded with load factors, skills-then-least-loaded);
   - caps, throttles, absence/cover, sticky routing;
   - SoD and maker exclusion; authority checks at route, claim and completion;
   - **atomic claim / Get next** (`-127`: `SELECT … FOR UPDATE SKIP LOCKED` is the natural PG answer);
   - explainable dry-run;
   - bulk reassignment of 50 k in 15 min;
   - leaver re-route within 15 min.
   A 500-case routing golden set is specified (§14.x).
2. **SLA and calendar engine.** Business days and hours via the PLT calendar plus group working hours and schedules; the warning/escalation/breach sweep; pause and resume per waiting reason (`-085`); read-only mirrored clock deadlines (`-086`); calendar version stamping (`-082`) and explicit recompute (`-083`); exactly-once escalation (BR-WRK-010). All implemented as Hangfire sweeps over indexed due-at columns.
3. **Inbound document pipeline.** Streaming upload → malware scan → DOC archive → OCR → rules classification → split proposals → extraction (MRZ/QR/barcode deterministic) → duplicate detection (hash + text similarity) → link suggestion via owner APIs → verification workbench with region highlighting → redaction rendition → eIDAS evidence → hand-off to owner APIs. Plus buffering when DOC is down (encrypted, up to 24 h) and at-least-once with dedup (NFR-WRK-006/-008).
4. **Search projection** over about 10 owners' events, with Greek/Latin normalisation from MKT rules, identifier detection, ABAC filtering without count leakage, and owner fallback when stale.
5. **Event-trigger table.** A generic payload-condition evaluator over ~92 consumed event types; dedup keys; dry-run against recorded events; dead-letter and reprocess (`-197`, `-392`); the 60-s missing-activity alert for `ClockWarned` (`-194`).
6. **Confidentiality and P3 handling** across notes, documents, search, exports, events, logs and AI, with a 4 × 12 × 3 test matrix.

**Must exist first.**
- PLT:
  - identity / ABAC PDP (`REQ-PLT-001`) and audit (`-002`);
  - authority check (`-003`) and maker-checker (`-004`);
  - outbox/event infrastructure (`-005`) and numbering (`-014`);
  - calendar service (`-009`/`-197`);
  - rules runtime (`-007`, minus the "workflow engine");
  - retention (`-011`) and malware scan (`-161`).
- MKT: config keys, capability switches, language rules, packs.
- DOC: archive `REQ-DOC-004`.

WRK is Wave 1 with these, so build against stubs or contracts. Business modules depend on `REQ-WRK-001` and `-003` early: UW referrals and POL `ActivityGateView` (needs `-402` event fields from day one).

**Suggested slicing (vertical, tracer-bullet).**
1. **Activity core:** patterns (versioned, GR/EN validation), `wrk.Activity.create/get/list/complete/skip/cancel`, canonical states, idempotency, optimistic lock, numbering, ABAC visibility, audit, outbox events incl. `-402` gate fields. Screens SCR-WRK-04/-05/-07 + My activities.
2. **Groups, queues, rules:** groups/memberships/skills/queues admin, decision-table routing with simulate, push/pull with atomic Get next, caps, fallback queue, maker-checker on rule sets. SCR-WRK-03/-06/-19/-20/-21.
3. **Time:** PLT calendar integration, target/escalation, SLA policies, pause, Hangfire sweeps for warning/escalation/breach, priority score. SCR-WRK-14 basic, SCR-WRK-25 SLA part.
4. **Event-driven work:** trigger table + dedup + dead-letter; CMP clock path (`-192/-193/-194`); auto-close; PLT approvals (`-397`), leaver handling (`-140`); the motor catalogue rows module by module.
5. **Notes:** CRUD, confidentiality, edit window/versions, search, P3 encryption. SCR-WRK-10.
6. **Intake (rules-only):** submit, scan, archive, OCR, rules classification, verification workbench, linking, `DocumentLinked`, staff upload, duplicates, split, redaction. SCR-WRK-11/-12/-13/-26.
7. **Back-office requests** + CHN hooks. SCR-WRK-22.
8. **Desktop, search, palette, recent items, notifications.** SCR-WRK-01/-15/-23.
9. **Supervision:** team view, bulk reassign, export, absence/delegation, surge mode. SCR-WRK-08/-09.
10. **Participants, DSAR/erasure/retention, migration import, diary/calendar.** SCR-WRK-16/-24.
11. **AI features** (post-MVP or toggle-gated), each behind `NFR-WRK-010` gates.
