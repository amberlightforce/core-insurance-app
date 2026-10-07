# Digest — PRD-04 Underwriting and referral workbench (UW)

Source: `core-insurance-prds/PRD-04-underwriting-referral-workbench.md` v1.4 (freeze fix, baseline 1.0 candidate), dated 2026-10-07, binding input `00-system-contract.md` v1.11. Read in full (2,381 lines, sections 1–16.7). Stack reference: `core-insurance-infra/ARCHITECTURE-DECISIONS.md`. A few contract lines were checked to answer the extra-focus questions (CD-03, CD-05, CD-07, CD-16, wave table §3.1.3, R-56, R-83). They are marked "contract".

---

## 1. Identity

| Item | Value |
|---|---|
| Module code | **UW**, PostgreSQL schema `uw` (§7) |
| Title | Underwriting and referral workbench |
| Owners | UW product owner (accountable); Head of underwriting ROLE-13 (business sign-off); Lead architect; Compliance officer ROLE-29 for §§3, 11, 12 (§1.1) |
| Phases (R-86) | P1 motor MVP, P2 home, P3 commercial property/liability, P4 later markets (Cyprus first) |

**Purpose (§1.2).** UW decides whether the insurer accepts a risk, who may accept it and on what conditions. It turns product-bound rules into underwriting issues. It blocks quote, bind or issue until a person with the right authority decides. A decision stays valid only while the facts it relied on stay the same. Every decision is recorded with the decider, the authority used and the data seen. Scope covers:
- versioned rule sets (decision tables) bound to product versions
- the issue lifecycle by blocking point, with approval validity and invalidation
- authority types and the authority-at-decision experience on the PLT framework
- referral routing, priority scoring and SLAs on WRK queues
- the workbench and risk review
- commercial submission intake and extraction verification
- external data and report ordering; inspections and valuation
- structured declines, Greek nat-cat refusal documents and the refusal register
- contingencies, renewal underwriting, policy holds, and accumulation checks at bind

**Five key decisions (§1.2).**
1. Rules are data. They run on the PLT decision-table runtime (`REQ-PLT-174`–`178`) and use a CEL-compatible rule-expression language built in-house (D10). Activation is four-eyes.
2. Approval validity is computed: a fingerprint of approval-sensitive inputs with tolerances, compared by issue key.
3. Authority is checked at the moment of decision through `plt.Authority.check`, and the check id is stored. Large risks need a second approver.
4. Confidence lanes (STRAIGHT_THROUGH / ASSISTED / EXPERT) are outputs of deterministic referral rules, never of AI.
5. Declines are legal records. The `RefusalDocumentRule` SPI decides whether a refusal document is needed (Greek nat-cat).

**Non-goals / out of scope (§1.4).**

| Out of scope | Owner |
|---|---|
| Price calculation and monetary effect of deviations | RAT |
| Job, quote, bind, issue, cancellation and non-renewal execution; policy and term states | POL |
| Activities, queues, groups, assignment rules, SLA policies, notes, inbound documents | WRK |
| Authority profiles, grants, delegation, `authority.check`, maker-checker | PLT |
| Decision-table runtime and workflow engine | PLT |
| Rendering, templates, archive, delivery proof | DOC |
| Party data and sanctions screening | PTY |
| Product structure, question sets, nat-cat bundles and floor | PFC |
| Presentation to brokers and customers | CHN (UW has no customer UI of its own, OBL-EAA row) |
| Statutory clock register | CMP |
| Reinsurance and facultative placement | RI |
| Cat analytics and model registry | DAT |
| Outbound claims-history certificate | CLM |

---

## 2. Size metrics

| Metric | Count (source-stated where possible) |
|---|---|
| Functional REQs | **268** (§5 footer, §16.6): anchors REQ-UW-001–010 (10) + REQ-UW-030–302 (258). Unused numbers: 073–074, 103–104, 139, 163–164, 181–184, 234, 247, 275, 284 |
| By MoSCoW | **Must 206 (184 of them P1)**, Should 54, Could 8 |
| By phase | **P1 218**, P2 14, P3 36 |
| By tag | [BASELINE] 215, [ENHANCEMENT] 53 |
| Business rules | 42 (BR-UW-001–042, §10.1) |
| NFRs | 20 (NFR-UW-001–020, §14) |
| Screens | 20 (SCR-UW-01–20). Also a decision panel and an interstitial; these are SCR-UW-03 and SCR-UW-04 |
| Owned entities | 33 rows in §7.1, with HoldRule/HoldRegion, ExternalReportOrder/ExternalReport and ReferralRoutingProfile/Row grouped |
| Events produced | 19 (14 contract catalogue §8.1 + 5 added by R-30 §8.2) |
| Events consumed | ~54 event types in 31 rows (§8.3); `ClockStarted` and `EvidencePackSealed` explicitly not consumed |
| Inbound API operation groups | ~33 rows in §9.1 (well over 80 individual operations) |
| Outbound integrations | 27 module call rows (§9.2) + 7 external systems (§9.3) |
| Authority types | 13 (§12.2) |
| Maker-checker approval types | 8 (§12.3) |
| SoD rules | 6 (SOD-UW-01–06) |
| Config keys | 23 (§10.2) |
| AI features | 7 (AI-UW-01–07), all ship OFF |
| Property invariants / regulatory test cases | 9 (P1–P9) / 14 (RT-01–RT-14) |

**Motor MVP (P1):** 218 requirements, of which 184 are Must.

**Build-size estimate: L.** There are 184 Must P1 requirements and 20 screens. The module includes an in-house rule-language and decision-table integration, a temporal approval-validity and fingerprint engine with property-test invariants, authority-at-decision with preview and binding checks, and statutory clocks. It integrates with nearly every other module.

---

## 3. Owned entities

Every row carries `legal_entity_id`, `jurisdiction`, `created_at`, `created_by` and `record_version`, plus `valid_from`/`valid_to` where it has business validity. Ids are UUIDv7. Business numbers come from `REQ-PLT-014` (§7).

| Entity | Key attributes | State machine / constraints | Retention |
|---|---|---|---|
| UWRuleSet | code, name_gr/en, checkpoint (PRE_QUOTE, PRE_BIND, PRE_ISSUE, RENEWAL), LoB | code unique per LE | RC-CFG |
| UWRuleSetVersion | version major.minor, status, content_hash SHA-256, activation_at, approval_request_id, test_run_id, simulation_run_id | Draft → Submitted (tests pass, validation clean) → Approved (maker-checker) → Active (`UWRuleSetActivated`) → Superseded → Retired. Submitted/Approved → Draft (returned). Immutable after Submitted (§7.3, REQ-UW-030) | RC-CFG |
| UWRule | rule_type (Eligibility, Referral, Decline, Warning, InformationRequest); issue_type_code; severity; blocking_point (NON_BLOCKING, PRE_QUOTE, PRE_BIND, PRE_ISSUE); default scope and validity; enabled; dates; applies_*; context_element_type; hit_policy; decision_table_ref (PLT id + hash); variables json; issue_key_template; policy_clause_refs | tests required | RC-CFG |
| RuleSensitiveInput | input_code, tolerance_kind (EXACT, ABSOLUTE, RELATIVE_PCT, BAND), value | ≥ 1 per blocking rule (BR-UW-008) | RC-CFG |
| RuleTestCase | inputs, expected hits / keys / lane, boundary flag | synthetic data only | RC-CFG |
| RuleInputDefinition | vocabulary: code, labels, type, source, `deny_listed` | — | RC-CFG |
| IssueType | category (rule, manual, hold, sanctions, accumulation, natcat, information); three texts (internal, intermediary GR/EN, customer GR/EN); authority_mapping; routing_key; max_scope; max_validity; carry_forward_allowed; survives_major_version; user_overridable | maker-checker change (UW.ISSUE_TYPE_CHANGE) | RC-CFG |
| UnderwritingEvaluation | job_ref, checkpoint, rule_set_version, content_hash, configuration_hash, input_snapshot_ref, lane, raised/kept/invalidated/closed issues, duration_ms, dry_run | one per non-dry-run call | RC-UW-DECISION |
| **UWIssue** | issue_key, blocking_point, severity, status, sub_status, raising_rule_refs, explanation_trace_ref, resolved texts, referral_id, current_approval_id | See state machine below. Unique (job_ref, issue_key, issue_type_code, occurrence) among non-terminal issues | RC-UW-DECISION |
| **UWApproval** | decision (APPROVE, APPROVE_WITH_CONDITIONS, REJECT); decision_maker; second_approver; scope; validity_kind; valid_until; **fingerprint json**; rule_code; rule_set_major; authority_check_ids[]; delegation_id; snapshot_ref; carried_forward_from; ai_interaction_id; status Active/Invalidated | never deleted; invalidation appends reason, fields and timestamp | RC-UW-DECISION |
| ApprovalCondition | kind CONTINGENCY / PRODUCT_CHANGE / FREE_TEXT_CONDITION, template, params, applied_job_ref, contingency_id | — | RC-UW-DECISION |
| **Referral** | referral_number, issue_ids, requested_by, comment, lane, priority_score + factors, routing_key, activity_ref (WRK), sla_target | Requested → Assigned (`ReferralAssigned`) → InReview ↔ AwaitingInformation; InReview → Assigned (refer up / reassign); InReview → Decided; Requested/Assigned → Withdrawn. SLA breach is a flag on any non-terminal state. One open referral per job | RC-UW-DECISION |
| ReferralRoutingProfile / Row | rows: order, conditions, queue_code, skills, authority types, exclusions, priority weight, sla_policy_code, effective_from | Draft → PendingApproval → Active → Superseded | RC-CFG |
| PriorityScoringModel | factors, weight, scale | weights sum to 1; allowed factor list only | RC-CFG |
| SubmissionIntake (P3) | envelope_ref, broker producer code, insured + AFM, lines, effective date, clearance, appetite, natcat_assessment_id, account_ref, job_ref | New → Verifying → Cleared → Accepted; Verifying/Cleared → Rejected; any → Duplicate | RC-POL-QUOTE / RC-UW-DECLINE |
| IntakeValue | field_path, value, extraction ref, confidence, source (rule/AI/person), verification (Pending/Accepted/Edited/Rejected), before_value | — | as intake |
| NatCatAssessment (P3) | revenue amount / FY / source, threshold, in_scope, values and SI by class, floor %, compliant, gaps, request_received_at, clock_instance_ref | — | RC-UW-REFUSAL |
| ExternalDataProvider | report types, adapter, unit cost, mode, consent_required, lawful_basis, cache_days, expiry_days, processing_location, transfer_basis, ict_third_party_ref | — | RC-CFG |
| ExternalReportOrder / Report | subject, consent_ref, cost, status, mapped_facts, raw doc ref + hash | Ordered → Received (`ExternalReportReceived`) → Expired; Ordered → Failed → Ordered; Ordered → Cancelled; a cache hit creates no order | RC-UW-EXTDATA |
| Inspection | kind, timing, subject, provider, due, cost, findings, condition_rating, photos, validation flags, contingency_id | Ordered → Received → Completed (`InspectionCompleted`); Ordered → Overdue → Completed / Cancelled | RC-UW-INSPECTION |
| Valuation | method REBUILD / CONTENTS / VEHICLE_MARKET, table version, accepted_as_declared | — | RC-UW-DECISION |
| DeclineReason | code, category, peril, asset class, texts, letter_clause_ref | — | RC-CFG |
| **Decline** | decline_number, scope FULL/PARTIAL, reasons, declined and accepted pairs (peril × asset class), automated flag, rationale, customer wording, decision_maker (mandatory), authority checks, refusal_required/template/deadline, offer_quote_ref, review_request_ref and outcome | Drafted → (PendingSecondApproval) → Issued (`DeclineIssued`) → DocumentRequested → DocumentIssued (`RefusalDocumentIssued`) / DocumentFailed (retry) → Delivered. Issued → UnderReview → Upheld / Overturned (§7.3) | RC-UW-DECLINE or RC-UW-REFUSAL |
| RefusalDocument | DOC ref, number, hash, render and delivery status, delivery proof | DOC owns the rendered document | RC-UW-REFUSAL |
| RefusalRegisterEntry | decline, party, **AFM encrypted**, dates, kind REFUSAL / PARTIAL_REFUSAL / DEEMED_REFUSAL, refused pairs, doc number and hash, delivery proof | **append-only**; corrections are superseding entries (BR-UW-028) | RC-UW-REFUSAL |
| RegisterExport | filter, requester, reference, purpose, file, manifest_hash | — | RC-AUD-BUS |
| **Contingency** | type (document, inspection, arrears to previous insurer, risk improvement, information), term, origin approval, due date, owner, evidence classes, consequence, reminder offsets | Pending (pre-bind) → Active (`PolicyBound`; `ContingencyCreated`) → Satisfied / Waived (`ContingencyResolved`); Active → Overdue (`ContingencyOverdue`) → ConsequenceApplied / Satisfied (late) / Waived; any non-terminal → Cancelled | RC-POL-CONTRACT |
| **PolicyHold** + HoldRule / HoldRegion | hold_type (underwriting / regulatory), code, texts, start/end, issue type, template, activated_by, authority_check_id; rules (LoB, job type, date basis, coverage); regions (subdivision, postcodes, hazard-zone keys) | Draft → Scheduled / Active (`PolicyHoldActivated`) → Released (`PolicyHoldReleased`); Draft/Scheduled → Cancelled. Active holds are released, never deleted | RC-CFG |
| RenewalDirection | RENEW, RENEW_WITH_CHANGES, REFER, NON_RENEW; changes; reason; set_by; authority_check_id; campaign | (none) → RENEW/REFER (batch) → any (user); NON_RENEW only by a user with authority; locked once POL issues the offer or notice; one current per term | RC-POL-CONTRACT |
| ReunderwritingCampaign (P3) | criteria, rule set version | Draft → Evaluated → Applied / Cancelled | RC-CFG |
| **DisclosureFinding** | kind (NONDISCLOSURE_NEGLIGENT / _FRAUDULENT, AGGRAVATION_REPORTED / _DISCOVERED), discovery date, clock ref, decision (NO_ACTION, PROPOSE_AMENDMENT, TERMINATE, TERMINATE_IMMEDIATE), correct_premium, reduction_factor, pol_job_ref | Recorded (clock started) → Decided (clock stopped) → Executed | RC-POL-CONTRACT |
| AccumulationCheck (P2) | peril, zone, incremental exposure, aggregate, capacity, band, ri_data_as_of, override | — | RC-UW-DECISION |
| RuleTransferJob | IMPORT / EXPORT | InProgress → Ready / Failed / Cancelled | RC-OPS-LOG |
| UnderwritingPolicyClause | document code, clause number, version | — | RC-CFG |

**UWIssue state machine (§7.3, contract §3.2.4 + R-33).** Canonical states: Open, Approved, ApprovedWithConditions, Rejected, Invalidated, Closed. Open has sub-states New → InReview ↔ AwaitingInformation, and InReview → PendingSecondApproval.

| Transition | Guard | Event |
|---|---|---|
| → Open | new issue key; manual add; invalidation of a prior approval for the same key | `UWIssueRaised` |
| Open → Approved / ApprovedWithConditions | binding authority = allow (REQ-UW-109); not SoD-barred (116); second approval done (113); conditions valid | `UWIssueApproved` |
| Open → Rejected | authority allow; reason present | `UWIssueRejected` |
| Open → Closed | rule no longer hits, hold released, or superseded | `UWIssueClosed` |
| Approved(*) → Invalidated | VALUE_CHANGED, EXPIRED, SCOPE, MANUAL_REOPEN, RULE_MAJOR_CHANGE | `ApprovalInvalidated` |
| Rejected → Open | reopen with authority and reason | `UWIssueRaised` (reopened = true) |
| Rejected → Closed | rule no longer hits | — |

Any other transition fails with `UW-ERR-ISSUE-TRANSITION` (REQ-UW-076). An issue blocks its point while Open, Rejected or Invalidated (BR-UW-001). Sub-reasons such as JOB_CLOSED, HOLD_RELEASED and RULE_NO_LONGER_HITS also appear (§8.3, REQ-UW-059, -257).

**Retention classes (CCR-UW-04 / R-20).** RC-UW-DECISION, RC-UW-DECLINE, RC-UW-REFUSAL, RC-UW-EXTDATA and RC-UW-INSPECTION, with triggers and actions in §7.1. Durations come from the programme retention schedule as Greece-pack data. OI-UW-06 is closed: UW holds no durations itself.

---

## 4. Consumed entities / dependencies (§7.2, §9.2)

| Entity | Owner | How UW gets it |
|---|---|---|
| Job, PolicyTerm, Policy, Transaction, risk units | POL | `JobView` read model from POL events (`SubmissionCreated`, `QuoteIssued`, `QuoteExpired`, `PolicyBound`, `PolicyChanged`, `PolicyCancelled`, `RenewalCreated`, `JobPreempted`). Risk snapshot via API `REQ-POL-002` (`validAt`/`knownAt`) and `REQ-POL-010`. Policy search `REQ-POL-014` |
| ProductVersion, rule-set refs, question sets, peril tags, nat-cat bundles | PFC | `ProductVersionView` from `ProductVersionPublished/Retired`. APIs `REQ-PFC-001`, -006, -010, -091, -094, -109, -131 |
| ProductReferenceTable | PFC (R-01) | Valuation tables and cost indices |
| Party, Account, Intermediary, ProducerCode, ScreeningResult, Consent | PTY | APIs `REQ-PTY-001/004/005/006/008/009/032`. `PartyScreeningView` from sanctions events. Revenue by FY `REQ-PTY-277` (R-31) |
| Worksheet, PricingModification, referral signals | RAT | `REQ-RAT-001/003/005/006`. Signals RS-DEVIATION-APPLIED, RS-HISTORY-UNVERIFIED, RS-RENEWAL-CHANGE, RS-UNDERINSURED |
| Activity, Queue, AssignmentRule, SlaPolicy, Note, InboundDocument, ExtractionField, Participant | WRK | `wrk.Activity.*`, `wrk.Assignment.simulate`, `wrk.Request.*`, `wrk.Note.*`, `wrk.InboundDocument.*`, `wrk.Participant.*` |
| AuthorityType/Profile/Grant, ApprovalRequest, AuditEvent, AiInteractionRecord | PLT | `REQ-PLT-002/003/004/010/100/103/104/110/111/112/114/115` |
| RenderedDocument, Delivery | DOC | `REQ-DOC-001/004/005/008/043/254`; `doc.Delivery.evidenceFor` (`REQ-DOC-249`) |
| ClockInstance | CMP | `REQ-CMP-003`, `REQ-CMP-092` |
| Claim, CHS certificate | CLM | `REQ-CLM-008/009/011` |
| Zone aggregates and capacity | RI | `ZoneAccumulationView`, `REQ-RI-005/212/218` |
| Config and SPI bindings | MKT | `REQ-MKT-001/002/103/307`, `mkt.Configuration.resolve` |
| Simulation samples, model registry, precedent store | DAT | `REQ-DAT-001/005/169/170/187` |

The contract (§3.1.2 / PRD summary) says UW depends on PTY, PFC, RAT, POL (anchors), WRK, PLT, DOC and RI/DAT. POL, CHN and RAT depend on UW. Wave 2 is RAT, POL, UW.

---

## 5. Events

Topic `uw.events.v1`. Partition key: `job_id` (job-scoped), `hold_id`, `term_id` (contingency, renewal direction) or `rule_set_id`. Payloads carry ids and codes only: no free text and no P2/P3 data (§8).

### Produced

| Event | Trigger | Key payload | Consumers |
|---|---|---|---|
| UWIssueRaised | Issue enters Open | issue id, type, key hash, blocking point, severity, lane, reopened | POL, WRK, CHN, DAT |
| UWIssueApproved | Approved / ApprovedWithConditions | approver, second approver, scope, validity, condition kinds, check ids | POL, WRK, CHN, DAT |
| UWIssueRejected | Rejected | approver, reason code | POL, WRK, CHN, DAT |
| ApprovalInvalidated | approval invalidated | approval id, reason, changed field codes | POL, WRK, DAT |
| ReferralAssigned | WRK `ActivityAssigned` | queue/user, SLA target, priority | WRK, CHN, DAT |
| ReferralSLABreached | WRK `SLABreached` / escalation | SLA target, breach minutes | WRK, CHN, DAT |
| DeclineIssued | decline committed | number, scope, reason codes, declined pairs, automated | POL, CHN, DAT |
| RefusalDocumentIssued | refusal rendered | document id/number, template | DAT (others query the register) |
| ContingencyCreated | contingency Active | term, type, due, owner | WRK, CHN, DAT |
| ContingencyOverdue | due passed | consequence code | WRK, POL, CHN, DAT |
| PolicyHoldActivated / PolicyHoldReleased | hold activation / release | code, scope summary, dates | POL, CLM, CHN, WRK, DAT |
| InspectionCompleted | inspection done | condition rating, result ref | WRK, DAT |
| ExternalReportReceived | report received | order, type, status, expiry | WRK, DAT |
| UWIssueClosed (R-30) | Closed | reason | POL, DAT |
| UWRuleSetActivated (R-30) | version Active | code, version, hash, time, checkpoint | PFC, DAT |
| ContingencyResolved (R-30) | Satisfied / Waived / ConsequenceApplied | outcome, consequence job ref | POL, DAT |
| RenewalDirectionSet (R-30) | direction set or changed | term, direction, reason, change codes | POL, DAT |
| DisclosureFindingDecided (R-30) | finding decided | kind, decision, POL job ref | POL, CLM, DAT |

### Consumed (§8.3, summarised)

| From | Events | Reaction |
|---|---|---|
| POL | SubmissionCreated, QuoteIssued, QuoteExpired | Maintain JobView. On QuoteExpired, close the referral as Withdrawn; stop the nat-cat clock only if the request is withdrawn |
| POL | PolicyBound | Activate contingencies; mark approvals bound; accumulation pending-bound list |
| POL | PolicyIssued, PolicyChanged | Update view; re-check holds |
| POL | PolicyCancelled, PolicyRewritten, PolicyNonRenewed, RenewalCreated | Cancel or carry contingencies; lock direction; trigger RENEWAL evaluation via a POL call |
| POL | JobPreempted, TransactionReapplied | Re-evaluate the job (REQ-UW-101) |
| POL | JobWithdrawn, JobNotTaken | Close the referral as Withdrawn; cancel the WRK activity; close issues (JOB_CLOSED) |
| PFC | ProductVersionPublished/Retired; ProductVersionApproved | Refresh view; run compatibility golden tests |
| PTY | SanctionsHitRaised/Cleared; PartiesMerged/PartyUnmerged; IdentifierVerified; IntermediaryLicenceChanged | Raise or close SANCTIONS_REVIEW; re-point references; update verification facts; block referral requests from lapsed producers |
| CLM | ClaimReported | Renewal review candidates; loss-history cache |
| CLM | CatEventDeclared/Changed | Draft policy hold (never auto-activated, REQ-UW-300) |
| WRK | DocumentClassified/Linked/Verified | Intakes and evidence |
| WRK | ActivityAssigned/Completed, SLABreached, ActivityEscalated | Referral state |
| WRK | RequestAnswered | Resume from AwaitingInformation |
| DOC | DocumentRendered/RenderFailed/Delivered, DeliveryFailed | Refusal document status |
| CMP | ClockWarned/Breached/Met/Elapsed | Deemed refusal on breach; `UW_AMENDMENT_ACCEPTANCE` Met/Elapsed; `POL_INSURER_TERMINATION_NOTICE` Elapsed shown on the finding |
| PLT | AuthorityGrantChanged | Re-route referrals that are no longer eligible |
| PLT | AiToggleChanged, AiKillSwitchActivated, ConfigChanged | Non-AI path; config refresh |
| DAT | ModelDriftDetected, BiasThresholdBreached | Disable or flag the AI feature |
| DAT | AccumulationSnapshotPublished | Refresh utilisation cache |
| RI | RIContractActivated/Versioned, AccumulationThresholdBreached, FacPlacementBound | Capacity cache; alert and re-evaluation; record fac support |
| RAT | PricingModificationDecided | Re-evaluate PRICING_DEVIATION |

All handlers are idempotent on `event_id`, or `event_id` + `party_id` / `clock_instance_id`.

---

## 6. APIs

**Conventions (§9).** Operation names are `uw.<Resource>.<operation>`; REST paths `/api/uw/v1/...`. Commands carry `Idempotency-Key`. Errors are RFC 9457 with `UW-ERR-*` codes. Pagination is cursor-based and filtering is ABAC. Business outcomes are structured results, not errors.

### Exposed (§9.1)

| Operation(s) | Notes |
|---|---|
| `uw.Rules.evaluate` | Dry-run supported. Idempotent on job + snapshot hash. Errors: RULESET-UNRESOLVED, EVAL-UNAVAILABLE (retryable) |
| `uw.Rules.evaluateBatch` | Renewal and campaign batches |
| `uw.Issue.blockingStatus` | Bind-gate query |
| `uw.Issue.list/.listForChannel/.get/.explain` | |
| `uw.Issue.decide` | Single or many. Errors: AUTHORITY-DENIED, AUTHORITY-REFER, SOD, ISSUE-TRANSITION, HUMAN-DECISION-REQUIRED, STALE. Dry-run gives an authority and condition preview |
| `uw.Issue.reopen/.addManual/.close`; `uw.Approval.history` | |
| `uw.Referral.request/.withdraw/.get/.list/.referUp/.requestInformation` | Errors: NOT-REFERRABLE, LICENCE-INVALID |
| `uw.Review.lock/.release` | |
| `uw.Decline.create/.get/.requestReview/.decideReview` | |
| `uw.RefusalRegister.query/.export` | |
| `uw.Contingency.create/.update/.satisfy/.waive/.extend/.applyConsequence/.list` | |
| `uw.PolicyHold.check` | T1 path |
| `uw.PolicyHold.create/.update/.copy/.activate/.release/.impact` | |
| `uw.Accumulation.check` | |
| `uw.ExternalReport.order/.get/.list/.reorder/.cancel` | |
| `uw.Inspection.order/.submitResult/.complete/.get` | |
| `uw.Valuation.compute` | |
| `uw.Intake.create/.get/.verifyValue/.clear/.accept/.reject` | |
| `uw.NatCat.assess` | |
| `uw.RenewalDirection.get/.set/.bulkSet` | |
| `uw.Campaign.*` | |
| `uw.DisclosureFinding.record/.decide/.get/.list` | |
| `uw.RuleSet.*` | create, draft, edit, validate, test, simulate, submit, approve via PLT, schedule, export, import |
| `uw.IssueType.search/.get/.upsert` | |
| `uw.Routing.*`, `uw.PriorityModel.*` | |
| `uw.DataSubject.export/.restrict/.erase` | |
| `uw.Import.*` | |

`uw.Contingency.list`, `uw.DataSubject.erase/.restrict` and `uw.Decline.requestReview` are listed twice in §9.1. Selected error codes are in §9.4.

### Consumed (§9.2)

| Module | Calls |
|---|---|
| PFC | Resolution, question sets, bundles, peril tags |
| POL | Query, risk units, job commands (dry-run first), search: `REQ-POL-001/002/010/011/012/014/155/156/313` |
| RAT | Rate (correct-premium dry-run), worksheet explain |
| PTY | Party/account, consent, screening, producer-code validate |
| WRK | See §4 |
| PLT | Authority check, type registration, who-can-approve, approval request/decide, decision-table runtime (`REQ-PLT-007`, `174`–`178`), audit, AI gateway (`010`, `217`, `223`), numbering, time (`332`), retention (`011`), integration hub (`006`) |
| DOC | Document request, delivery |
| CMP | Clocks; complaints (`REQ-CMP-004/122`) |
| RI | Accumulation |
| CLM | Claim search |
| MKT | Config and SPIs |
| DAT | Registry, samples, precedents |

**External (§9.3).** All are "to be contracted" or UNVERIFIED:
- gov.gr Wallet consent (OI-UW-04)
- vehicle registry and valuation (OI-UW-04)
- CHS exchange (MVP via upload, OI-UW-05)
- hazard and geocoding data (OI-UW-05)
- inspection vendors (Home phase)
- AADE / GEMI via PTY `RegistryLookup`
- ΔΙ.Μ.Ε.Α. register file export (OI-UW-01)

---

## 7. SPIs / country-pack interfaces

| SPI / key | Use | Requirements |
|---|---|---|
| `RefusalDocumentRule` (`REQ-MKT-103`, extended by R-29: `evaluate(declineRecord)`, `responseDeadline(requestType)`, `registerExport(afm, period)`) | Whether a refusal document is required, plus template, deadline and export format. Core default `NotRequired`; Greece pack binds from P3; Cyprus stub `NotRequired`/`NotApplicable` | REQ-UW-005, -222, -231; BR-UW-023 |
| `MotorDataProvider` (incl. `.valuation`; R-56 adds asynchronous wallet-consent operations) | Vehicle and licence data, gov.gr Wallet pre-fill, vehicle market value | REQ-UW-196, -212 |
| `Geocoder` | Hazard-zone keys for holds, accumulation and property rules | REQ-UW-197, -251, -277 |
| `StatutoryClockSet` (via CMP, `REQ-MKT-009`) | Values for UW_NATCAT_RESPONSE, UW_NONDISCLOSURE_ACTION, UW_AGGRAVATION_ACTION, UW_AMENDMENT_ACCEPTANCE; POL_NONRENEWAL_NOTICE lead | REQ-UW-224, -267, -271, -302 |
| `IdValidator`, `AddressFormatter` | AFM and postcode validation on SCR-UW-06/11/13 | §6 |
| `DocumentLanguageRule` | Letter language (Greek binding vs English binding in Cyprus) | §6.0, §10.4 |
| `PricingConstraint` deny-list | Protected inputs (BR-UW-016) | REQ-UW-190 |
| `RegistryLookup` (PTY) | Business registry | REQ-UW-198 |

Config keys owned by the PFC country pack: `gr.natcat.revenue_threshold` (€500,000) and `gr.natcat.floor_pct` (70). §10.4 requires that no UW code branches on jurisdiction and that golden tests run against the Greece and Cyprus packs.

---

## 8. Screens (§6)

All screens are staff UI. Every one has the Greek/English switch without reload (R-101, Must P1). † marks working Greek translations.

| ID | Name | Persona | One line | Ph |
|---|---|---|---|---|
| SCR-UW-01 | Underwriter workbench | ROLE-10/11/13/45 | Sidebar work views with counts, KPI strip, priority-ranked queue, split pane with brief / fact sheet / decision panel, Get next, exception-first toggle | P1 |
| SCR-UW-02 | Risk analysis panel in a job | ROLE-10/11/12; producers via CHN | Issues grouped by blocking point; tabs Issues, Contingencies, Prior policies, Claims, Driving and claims history, External reports, Inspections; lock and request approval | P1 |
| SCR-UW-03 | Issue decision with authority at decision | ROLE-10/11/13 | Required authority vs your limit and source; outcome allow / refer / deny; Approve becomes "Refer to X"; scope, validity, tolerance, conditions, history | P1 |
| SCR-UW-04 | Blocking-issues interstitial | producers, CSRs | Overlay listing blockers with referral status | P1 |
| SCR-UW-05 | Risk and account review | ROLE-10/11/13/29 | 8 tabs: Summary, Risk details (changed-since-approval markers), Loss history, Financials, Sanctions, Documents, History, Quote | P1 (Financials P3) |
| SCR-UW-06 | Submission intake and extraction verification | ROLE-12/11 | Document viewer with region highlight, click-to-verify, clearance, nat-cat result, appetite triage | P3 |
| SCR-UW-07 | Rule-set catalogue | rule author, ROLE-13/14, auditors | Filters, grid, as-of date, import/export, enable/disable | P1 |
| SCR-UW-08 | Rule editor with tests and simulation | rule author, approver, ROLE-34 | Tabs: details, decision table, issue details, approval sensitivity, tests (boundary generator), simulation, history | P1 |
| SCR-UW-09 | Rule import/export and activation status | authors, ROLE-41 | Job lists and activation schedule | P1 |
| SCR-UW-10 | Issue-type catalogue and lookup | ROLE-13; PLT grant editor | Texts, authority mapping, validity limits; lookup mode | P1 |
| SCR-UW-11 | Policy holds | ROLE-13 | List and detail, rules, regions, impact dry-run, templates, activate and release | P1 |
| SCR-UW-12 | Decline and refusal document | ROLE-10/11/13 | Stepper: scope and reasons → wording → refusal document → confirm with authority; peril × asset-class matrix; response clock | P1 (nat-cat P3) |
| SCR-UW-13 | Refusal register and export | ROLE-29/11/13/43 | Filter by AFM, export with purpose and requester | P3 |
| SCR-UW-14 | Contingency tracker | ROLE-12/10/11 | Exception-first list; satisfy, waive, extend, apply consequence | P1 |
| SCR-UW-15 | Inspections and valuation | ROLE-10/11/12/19 | Orders, photos, findings, valuation, ITV; inspector view hides financials | P1 (valuation P2) |
| SCR-UW-16 | External reports panel | ROLE-10/11/12 | Order, re-order, use cached, consent, cost | P1 |
| SCR-UW-17 | Renewal review queue and campaigns | ROLE-10/11/13 | Directions, changes preview, notice deadline, campaigns | P1 (campaigns P3) |
| SCR-UW-18 | Accumulation map | ROLE-11/13/27 | Zone map and utilisation (self-check calls it "thin") | P3 |
| SCR-UW-19 | Underwriting authority overview | ROLE-13/29/43 | Types, matrix, delegations, usage; edits deep-link to SCR-PLT-07 ("thin") | P1 |
| SCR-UW-20 | Referral routing and priority configuration | ROLE-13/45 | Rule grid with AND/OR builder, scoring model, test, maker-checker activation | P1 |

**Design-guide patterns referenced.**
- IB-01 command palette; IB-02 keyboard list navigation; IB-03 sidebar with counts; IB-04 ranked queue; IB-05 split pane
- IB-07 approve-the-diff; IB-08 explain-why; IB-09 inline AI card; IB-10 streaming draft
- IB-11 work-left home; IB-12 progress; IB-13 statement view; IB-14 edit in place; IB-15 relationship graph; IB-16 density; IB-17 hero metric
- IB-18 overlay; IB-19 presence; IB-20 pinned comments; IB-21 semantic status; IB-23 motion; IB-24 quiet chrome; IB-25 task-adaptive
- IB-26 global filter bar; IB-27 self-enriching suggestions; IB-28 exception-first; IB-29 dense log; IB-31 agentic task run; IB-32 binary status; IB-33 theme

**Permissions (§6.0).** `uw.view`, `uw.referral.decide`, `.request`, `uw.rules.author/approve/view`, `uw.hold.manage`, `uw.decline`, `uw.register.view/export`, `uw.intake.verify`, `uw.report.order`, `uw.inspection.manage/perform`, `uw.contingency.manage`, `uw.renewal.direct`, `uw.config.manage`.

**Inventory and mapping.** §6.21 maps the baseline checking sets (PreQuote and others). §6.22/§15.3 cover the inventory: 20 owned items, 20 specified, 0 missing. The MVR tab and MVR / Upgrade / All checking sets are not applicable in GR.

---

## 9. Regulatory, tax and statutory rules

### 9a. Explicitly stated

| ID | Rule as stated | Status in source |
|---|---|---|
| OBL-NATCAT / F1 / BR-UW-025 | Law 5116/2024 Art. 5 as amended by Law 5162/2024 Art. 25. Applies to businesses with annual gross revenue **> €500,000** in the preceding fiscal year. Perils: forest fire, flood, earthquake. Assets: owned buildings, equipment, raw materials, goods, commercial vehicles, production means, stored products. Cover **≥ 70 %** of combined value. **€10,000 fine, doubled if not remedied within 30 days** of notification. Exclusion from state aid | Verified 2026-10-07 (taxheaven.gr) |
| OBL-NATCAT / F2 / BR-UW-024 | JMD 96806/2025 (ΦΕΚ B' 2810/05.06.2025) Art. 3(ζ). The insurer must answer within **30 days**; silence = refusal. Two refusals from different insurers → 2-year exemption; two more after re-application → permanent exemption. Partial refusals exempt only the refused perils/assets. If uninsurable assets > **30 %**, the obligation is limited to insurable items. Checked by ΔΙ.Μ.Ε.Α. | **UNVERIFIED** (secondary sources, OI-UW-01) |
| BR-UW-026 | Compliance needs every bundle peril per asset class, and SI ≥ floor × declared value per class (`BR-PFC-013/014`) | L3 final |
| C-12 / R-85 | No nat-cat obligation for private motor; commercial vehicles only as business assets | Settled by ruling |
| C-11 | Vehicle floor: UW follows `BR-PFC-014`; the "100 %" claim is UNVERIFIED (OI-PFC-04) | — |
| C-02 | No insurer duty to offer the insurable share. UW offers it as configurable behaviour (`uw.natcat.offer_insurable_share`, default true) — a recommendation, not law | — |
| C-03 | The JMD allows an answer "by any suitable means". UW still issues a written document (recommendation) | — |
| OBL-CON / F4 / BR-UW-036 | Law 2496/1997 Art. 3(1)–(7) and 4(1)–(2). Disclosure is bounded by the insurer's written questions. The insurer may terminate or propose an amendment **within one month** of learning. An amendment not accepted **within one month** = termination. Termination is effective **15 days** after notice reaches the policyholder. Fraud allows immediate termination. Negligent non-disclosure gives proportional reduction (Art. 3(5)). Aggravation must be reported **within 14 days** of knowledge | English translation from 2002; current text **UNVERIFIED** (OI-UW-02). Production activation needs Settled status |
| OBL-GDPR / F5 | Art. 22 safeguards for solely automated decisions; CJEU C-634/21 SCHUFA (a determining score is itself an automated decision); Art. 13–15 meaningful logic; Art. 5(1)(c) and (e) minimisation and storage limitation; Greek Law 4624/2019 | Texts not re-fetched |
| REQ-UW-219 | Automated declines only with the capability switched on, plus: (a) notice that the decision was automated, (b) a human-review route, (c) main factors, (d) recorded as automated. Straight-through *acceptance* is not adverse; straight-through *decline* is (F5) | — |
| OBL-IDD | Directive 2016/97 Art. 17 and Art. 20(1); Greek Law 4583/2018 | Not re-fetched |
| OBL-SII | Dir. 2009/138 Art. 41, 44(2)(a); Del. Reg. 2015/35 Art. 260(1)(a) | — |
| OBL-AIA / F6 | AI Act Art. 50 applies from **2 Aug 2026**. Digital Omnibus Reg. (EU) 2026/1744 (OJ 24 Jul 2026, in force 27 Jul 2026): Annex III obligations no later than **2 Dec 2027**; Art. 50(2) marking concession to **2 Dec 2026** for systems already on the market. Annex III 5(c) covers life and health only. EIOPA-BoS-25-360 (6 Aug 2025) | Per PRD-11 |
| OBL-EQT | Gender-neutral pricing and underwriting (C-236/09 Test-Achats; Dir. 2004/113/EC) | Settled (gender); Verify (other grounds) |
| OBL-MOT / F11 / BR-UW-031 | Dir. 2009/103 Art. 16 as amended by Dir. 2021/2118. CHS template per Impl. Reg. 2024/1855 (applies from **24 Jul 2025**). Statements from other Member States must not be treated less favourably | — |
| OBL-DORA | ICT third-party register for data and inspection providers (REQ-UW-186) | Settled |
| OBL-RES / R-85 | No personal data to providers processing outside the EU (REQ-UW-187, `REQ-PLT-285`) | Settled |
| OBL-EAA | Dir. 2019/882; Greek Law 4994/2022. WCAG 2.2 AA | Per contract |
| OBL-AML | EU restrictive measures, group OFAC screening (CD-12) | Settled |
| OBL-EIDAS | Reg. 910/2014 as amended by 2024/1183; gov.gr consent | **Verify** |

### 9b. Referenced but NOT specified (gaps)

- Exact content and form of the nat-cat refusal answer, and the ΔΙ.Μ.Ε.Α. export format (OI-UW-01; REQ-UW-231 "format to be confirmed").
- Which fiscal year applies before the tax-return deadline (REQ-UW-177, OI-UW-11).
- Current consolidated Greek text of Law 2496/1997 (OI-UW-02).
- Hellenic DPA guidance on automated underwriting decisions (OI-UW-07).
- Lawfulness of credit or driving-record data for Greek motor (OI-UW-09; working position: none in MVP).
- Non-renewal notice lead time: POL owns it as `POL_NONRENEWAL_NOTICE`; the example in REQ-UW-267 uses 30 days, but no value is stated as law.
- The vehicle nat-cat floor (OI-PFC-04).
- IPID / pre-contract information delivery is not covered by UW at all (see §10).

### 9c. Deferred to configuration / country pack

- All statutory durations: `StatutoryClockSet` (C-04, BR-UW-036, D7).
- Refusal rule and register export: `RefusalDocumentRule`.
- Threshold and floor: PFC keys.
- Four-eyes thresholds (OI-UW-08).
- Decline and non-renewal reason lists (ADDITIVE keys).
- Protected-input deny-list additions.
- Report evidence period: `uw.report.evidence_days` P30D.
- Retention durations: programme schedule.
- `uw.natcat.regime_active` (L3, final).

---

## 10. Greek-market specifics

**AFM.**
- Stored encrypted on RefusalRegisterEntry (NFR-UW-012).
- Register filter and export by AFM (REQ-UW-229–231, SCR-UW-13).
- Validated via `IdValidator` on intake (SCR-UW-06) and register.
- SOD-UW-05: no export for an AFM where the user holds a party role.
- Excluded from AI inputs (AI-UW-01/03).
- Intake extracts AFM and ΚΑΔ activity codes.

**myDATA.** Not mentioned in PRD-04 (UW has no fiscal documents).

**gov.gr Wallet consent (extra focus).**

| Aspect | Source position |
|---|---|
| Basis | F12 (newmoney.gr press report, S15): citizens can consent to share vehicle registration, licence and personal details with an insurer or intermediary for a motor contract |
| Requirement | REQ-UW-196 **Must P1**: obtain vehicle and licence data through `MotorDataProvider` (Greece pack: vehicle registry and gov.gr Wallet consent pre-fill). Store the consent token reference, data received and verification time. GWT: wallet consent token T is recorded on the order with the registration data and an `IdentifierVerified`-style verification time |
| Obligation | OBL-EIDAS, status Verify |
| Integration | §9.3: REST through the integration hub. The channel is owned by CHN/POL per `REQ-MKT-002` §9.4.16. Status "to be contracted", **UNVERIFIED (OI-UW-04)** |
| Contract | R-56: `MotorDataProvider` gains asynchronous wallet-consent operations with legal-basis and channel attributes |
| Consent gate | REQ-UW-188: consent or other lawful basis checked via `REQ-PTY-005` before every order that needs it (`UW-ERR-CONSENT-MISSING`). SCR-UW-16 shows the consent reference |
| Risk and decisions | Top risk 3 (§1.2); RK-UW-04 (High likelihood: providers not contracted at go-live; mitigation: MVP relies on uploads and question sets, providers optional per rule); decision 16.5 #5 (contract or defer, and decide the MVP fallback) |
| Not specified | The fallback when the Wallet is unavailable, for a Must P1 requirement |

**Claims history.** Claims-history statements replace the US MVR (REQ-UW-148, -195). MVP is by upload; a structured exchange is UNVERIFIED (OI-UW-05).

**Business registry.** AADE/GEMI via PTY only (REQ-UW-198).

**Hazard data.** Flood, seismic and forest-fire zones, and cadastre data: sources to be contracted (OI-UW-05).

**Information Centre / bureau.** Not referenced in PRD-04.

**Greek language rules.**
- Every label and text is GR/EN; a missing translation fails submission (`UW-ERR-TRANSLATION-MISSING`, REQ-UW-298, NFR-UW-016).
- Refusal documents are Greek binding with English informative (SCR-UW-12).
- The language switch never changes letter language.
- Greek legal reviewer ROLE-46 validates the † labels.
- Sample Greek intermediary texts are given (REQ-UW-066, -071, -178).

**EUR.** Thresholds in EUR (€500,000, €1,000,000, €50,000, €300, €250,000). Money shows its currency. Rounding follows `REQ-MKT-006` (REQ-UW-033).

**Time.** Europe/Athens in the activation example (REQ-UW-041). All time comes from the PLT time service (REQ-UW-288).

**IDD demands-and-needs / pre-contract information (extra focus).**
- OBL-IDD maps to: REQ-UW-160, **Should P1** (an "Advice" note recording demands and needs and the recommendation, when underwriters advise customers directly; PRE_BIND issue `ADVICE_RECORD_MISSING` blocks bind for direct-advice jobs flagged by the channel); REQ-UW-218, Must (customer-facing decline wording, Art. 17 fair treatment).
- §3.1: "demands and needs captured through PFC question sets (`REQ-PFC-109`) by POL/CHN". UW does not own demands-and-needs capture.
- **IPID and pre-contractual information are not mentioned anywhere in PRD-04.** Per the contract, they belong to DOC (`REQ-DOC-007`) and to the POL bind gate (R-83: DOC delivery evidence or a CHN durable-medium receipt; bind is blocked if neither exists).
- The related **policyholder** pre-contract disclosure duty (Law 2496/1997 Art. 3(1)) is handled by REQ-UW-063: question codes, versions and answers are stored as the disclosure record, with links to hits.

---

## 11. Controls

**Authority types (§12.2, registered via `REQ-PLT-100`).**

| Type | Comparison |
|---|---|
| UW.ISSUE_APPROVAL | in set |
| UW.SUM_INSURED | at most |
| UW.LIMIT | at most |
| UW.PREMIUM | at most |
| UW.RISK_SCORE | at most |
| UW.DECLINE | flag |
| UW.NON_RENEWAL | flag; also used for disclosure terminations |
| UW.POLICY_HOLD | flag |
| UW.CONDITION_WAIVER | flag |
| UW.ACCUMULATION_OVERRIDE | at most (money over capacity) |
| UW.SPECIAL_APPROVAL | flag; always with a second approver |
| UW.CLEARANCE | flag |
| UW.REPORT_COST | at most |

Pricing deviations use the RAT type (`REQ-RAT-005`).

**Authority-at-decision (extra focus).**

| Requirement | What it does |
|---|---|
| REQ-UW-010 (anchor) | Register types; call `plt.Authority.check` inside every decision command; store the check id; show required authority, own limit and source grant, outcome (allow/refer/deny) and escalation target **before commit** |
| REQ-UW-106 | Issue type → authority type and dimension mapping |
| REQ-UW-108 | Dimensions computed from job values: product, line, territory from risk location, transaction type, SI, limit, premium, deviation %, risk score |
| REQ-UW-109 | Preview check when the panel opens **and** a binding check inside the command. If authority changed in between: `UW-ERR-AUTHORITY-DENIED`, nothing recorded |
| REQ-UW-110 | Panel layout; Approve is replaced by "Refer to <target>", which re-routes via `REQ-WRK-201` with the check id |
| REQ-UW-111 | Store check ids, types, values, limits and delegation id |
| REQ-UW-112 | Auto refer-up; the original user stays as a participant |
| REQ-UW-113 | Four-eyes above BR-UW-012 thresholds; sub-status PendingSecondApproval |
| REQ-UW-114 (Should) | Special approval |
| REQ-UW-115 | No AI or service decider (`UW-ERR-HUMAN-DECISION-REQUIRED`) |
| REQ-UW-116 | SoD (`UW-ERR-SOD`) |
| REQ-UW-117 (Should) | Delegation and holiday cover shown |
| REQ-UW-118 (Should) | "Who can approve?" (`REQ-PLT-112`) |
| REQ-UW-119 (Should) | `AuthorityGrantChanged` → re-route |

Also: REQ-UW-078 ("issues I can decide" via a dry authority check per issue); BR-UW-011 (commit only if every required type allows); NFR-UW-003 (decision command incl. binding check and audit p95 ≤ 800 ms). §14.1 authority boundary tests cover limit −1 cent, at limit, +1, delegation active/expired, grant changed mid-flow, and four-eyes thresholds.

**Maker-checker (§12.3).** UW.RULESET_ACTIVATION, UW.LARGE_RISK_APPROVAL, UW.LARGE_RISK_DECLINE, UW.SPECIAL_APPROVAL, UW.ROUTING_ACTIVATION, UW.PRIORITY_MODEL_ACTIVATION, UW.ISSUE_TYPE_CHANGE, UW.BULK_NONRENEWAL. All use `REQ-PLT-004`. Imports cannot bypass four-eyes (REQ-UW-049).

**SoD (§12.4).**

| Rule | Content |
|---|---|
| SOD-UW-01 | Author or editor may not approve activation |
| SOD-UW-02 | Job creator, requester or producer may not decide |
| SOD-UW-03 | First approver ≠ second approver |
| SOD-UW-04 | Rule author may not review the automated decline from their rule |
| SOD-UW-05 | No register export for an AFM where the user holds a party role |
| SOD-UW-06 | Intermediary roles may not hold `uw.referral.decide` |

**Audit (§12.1, REQ-UW-285–287).**
- Operations: UW.ISSUE.*, UW.APPROVAL.INVALIDATED, UW.REFERRAL.*, UW.DECLINE.*, UW.REFUSAL.DOCUMENT / REGISTER.EXPORTED, UW.RULESET.*, UW.HOLD.*, UW.REPORT.*, UW.CONTINGENCY.*, UW.RENEWAL.DIRECTION, UW.DISCLOSURE.*, UW.CONFIG.*.
- Every decision stores the snapshot ref (quote version, worksheet ref, report ids, rule hash) and the configuration hash.
- NFR-UW-014: 100 % reconstructable.

**GDPR.**
- Consent check (188); minimisation, keeping only mapped fields plus a raw hash (189, NFR-UW-013); deny-list (190).
- DSAR export (289); erasure and restriction that keeps legally required decline/refusal records "restricted" and records the conflict (290).
- Pseudonymisation outside production (291); retention class per entity, with CI failing if one is missing (292); external-report retention (293); a distinct declined-application class (294).
- Art. 22 safeguards (219–220); EU-only providers (187).

---

## 12. AI features (§11)

All ship **OFF**. Each has a kill switch that returns to the non-AI path within 60 s (`REQ-PLT-220`), also on `AiSystemStatusChanged` (D4). Calls go through the PLT gateway to EU endpoints with zero retention; each writes an `AiInteractionRecord`. The module is complete without AI (G6, P6, RT-14, REQ-UW-065). NFR-UW-020: first token ≤ 2 s, complete ≤ 10 s, fallback at 5 s.

| ID | Feature | Classification / controls | Req / MoSCoW / Ph | Needed for MVP? |
|---|---|---|---|---|
| AI-UW-01 | Referral and submission risk brief (citations) | Uncertain → high-risk controls; MP-B | REQ-UW-159 Should P1 | No (deterministic fact sheet) |
| AI-UW-02 | Commercial submission extraction | Minimal risk with high-risk controls (AIC-011); MP-D | REQ-UW-175 Could P3 | No |
| AI-UW-03 | Suggested decision and conditions | High-risk controls; MP-A; never for decline proposals in MVP | No dedicated REQ (surfaces on SCR-UW-03); §1.5 lists it as motor MVP "shipped off" | No |
| AI-UW-04 | Decline / customer wording drafter | Limited risk (Art. 50); MP-B | REQ-UW-233 Could P1 | No |
| AI-UW-05 | Rule authoring and simulation assistant | Minimal; MP-C | REQ-UW-054 Could P1 | No |
| AI-UW-06 | Inspection photo triage | High-risk controls; MP-A | REQ-UW-214 Could P2 | No |
| AI-UW-07 | Re-underwriting candidate finder | High-risk controls; never proposes NON_RENEW; MP-A | REQ-UW-269 Could P3 | No |

None is needed for MVP. Hard rules: AI never sets lanes, eligibility or referral outcomes (REQ-UW-065, BR-UW-015), and AI confidence may only affect queue ordering.

---

## 13. Open issues / assumptions / CCRs

| ID | Topic | Status |
|---|---|---|
| OI-UW-01 | JMD 96806/2025 text, answer form, ΔΙ.Μ.Ε.Α. format | **Open, UNVERIFIED** |
| OI-UW-02 | Law 2496/1997 consolidated text; clock values need Settled status | **Open, UNVERIFIED** |
| OI-UW-03 | Digital Omnibus | Closed (Reg. 2026/1744) |
| OI-UW-04 | gov.gr Wallet / vehicle registry / valuation channel and contract | **Open, UNVERIFIED** |
| OI-UW-05 | Hazard, cadastre, CHS exchange data | **Open, UNVERIFIED** |
| OI-UW-06 | Retention durations | Closed (programme schedule) |
| OI-UW-07 | HDPA guidance on automated decisions | Open, UNVERIFIED |
| OI-UW-08 | Four-eyes threshold values | Open |
| OI-UW-09 | Credit / driving-record data lawfulness | Open (MVP: none) |
| OI-UW-10 | RAT alignment | Closed |
| OI-UW-11 | Nat-cat revenue year | Open, UNVERIFIED |
| OI-UW-12 | WRK pattern codes | Closed (UW-REFERRAL, UW-CONTINGENCY-DUE/-OVERDUE, UW-HOLD-REVIEW, UW-REPORT-RECEIVED, UW-INSPECTION-ORDER, UW-REVIEW-RENEWAL, UW-INFO-REQUEST, UW-NATCAT-RESPONSE, UW-DISCLOSURE-DECISION, UW-CLEARANCE) |
| OI-UW-13 | Automated declines on comparison sites in MVP | Open (default off; decide after DPIA) |
| OI-UW-14 | Nationwide job aids not retrieved | Open |
| OI-UW-15 | DOC/MKT nat-cat priority alignment | Closed (R2B-002) |

**Assumptions A1–A7.**
- A1: POL calls evaluate at every quote (PRE_QUOTE + PRE_BIND), at bind (PRE_ISSUE) and at renewal creation.
- A2: RAT signals are named facts.
- A3: the PLT runtime supports nested collections, or UW flattens them; the pre-G0 spike decides.
- A4: motor is not accumulation-checked.
- A5: any submission flagged as a nat-cat request starts the clock.
- A6: the authorities, not the insurer, decide exemptions.
- A7: volumes assume a 7 % referral rate.

**Risks RK-UW-01–07** (§16.3).

**CCRs (§16.4).** All 8 were accepted in contract v1.3: CCR-UW-01 (clocks, R-28), -02 (`RefusalDocumentRule` extension, R-29), -03 (5 events, R-30), -04 (retention classes, R-20), -05 (PTY revenue, R-31), -06 and -07 (catalogue and glossary, R-32), -08 (transitions, R-33). v1.4 raises no new CCR.

**Ten pre-build decisions (§16.5).**

| # | Decision | Status |
|---|---|---|
| 1 | Approve the fingerprint model | Open |
| 2 | Rule language | Closed by D10 (CEL-compatible, in-house; spike pending) |
| 3 | Four-eyes thresholds and authority matrix | Open |
| 4 | Automated declines in MVP | Open |
| 5 | External data contracting and MVP fallback | Open |
| 6 | JMD reading, template and export | Open |
| 7 | Implement clocks, events and retention incl. `UW_AMENDMENT_ACCEPTANCE` | Open |
| 8 | PTY revenue vs declared revenue precedence | Open |
| 9 | Routing split: UW profile vs WRK assignment rules | Open |
| 10 | Which AI features enter the register | Open |

---

## 14. Conflicts and ambiguities found

### (a) With the infra stack (ARCHITECTURE-DECISIONS.md)

1. **"Workflow engine" in PLT.** §1.4 says "Decision-table runtime and workflow engine | PLT | `REQ-PLT-007`…", and the batch requirement REQ-UW-072 relies on `REQ-PLT-172/173` ("partitions and checkpoints"). The stack explicitly excludes workflow servers and BPM engines and uses Hangfire with domain state machines. Interpret as Hangfire jobs plus domain tables.
2. **Event topics and partitions.** §8 says "Topic `uw.events.v1`, partition key = `job_id`…", which is broker vocabulary (Kafka-style). The stack uses an outbox dispatched in order to in-process handlers. The per-aggregate ordering intent can be preserved; the topic and partition semantics need translating.
3. **DMN-compatible export** (REQ-UW-048, `REQ-PLT-178` Should) and an **in-house CEL-compatible expression language** (D10, REQ-UW-032/033). Neither is mentioned in the stack. Building a typed, deterministic CEL subset with decimal arithmetic is a significant engineering item. Taking an off-the-shelf CEL library would need a dependency decision, and D10 says "implemented in-house".
4. **AI endpoints.** "EU-hosted endpoints with zero retention" through the PLT gateway (§11). The stack names no model provider. This is a gap, not a conflict.
5. **Real-time features** (IB-19 presence, REQ-UW-158 Should; live counts and SLA countdowns). The stack lists no push channel; SignalR is not listed. Needs a decision or polling.
6. **Integration hub** (`REQ-PLT-006`) is fine as an in-process adapter host, but must not become a separate integration server.
7. **Vendor names** (Guidewire PolicyCenter, Appian, McKinsey) appear only as research sources (S10–S13) and baseline inventory references (SCR-PC* frames). This does not conflict with CD-20 because no product is specified.
8. **Identity.** The contract's PRD-14 summary says "Custom-built identity service", while the ADR mandates Entra ID. UW depends on PLT for roles, SoD and authority; the ADR maps "app roles to business authority". The split between Entra app roles and the PLT authority framework must be decided. This is not UW's conflict to resolve, but UW is a heavy consumer.

### (b) With the contract or other PRDs

1. **POL↔UW cycle (extra focus).**
   - UW **Requires** `REQ-POL-001/002/011/012` (job commands, risk snapshot, decline marking, cancellation request) and reads POL events into `JobView`.
   - POL requires `REQ-UW-001/002/003/005–008` (evaluate, bind gate `REQ-POL-003`, holds, accumulation) and consumes UW events (`UWIssueApproved`, `RenewalDirectionSet`, `DisclosureFindingDecided`…).
   - **How the contract breaks it:**
     - CD-03 reserves and pre-defines anchors (`REQ-UW-001…010`, `REQ-POL-0xx`) so both PRDs could cite real IDs before the other existed.
     - CD-16 / wave table puts POL and UW in the same Wave 2, "mutually dependent… written together, citing anchors".
     - At runtime the PRD makes the direction asymmetric:
       - POL **calls** UW synchronously only on the T1 path (`evaluate`, `blockingStatus`, `PolicyHold.check`, designed T1, NFR-UW-008).
       - UW **never decides POL state**. It sends dry-run-first POL commands for product changes, consequences, declines and cancellation requests. It publishes facts (direction, findings) that POL reads by event or `uw.RenewalDirection.get`.
       - UW does no date arithmetic for terminations: POL owns `POL_INSURER_TERMINATION_NOTICE` and `POL_NONRENEWAL_NOTICE`.
       - UW reads POL through a read model plus the snapshot API.
     - R-83 (contract): bind does not call T2 modules synchronously.
   - **Build implication (my note, not the source):** project references must not form a cycle. Each module's public contract assembly should be referenced only by the other's implementation, and NetArchTest should guard this.
2. **Anchor phase vs content.** REQ-UW-005 is Must P1 although every refusal-document member is P3. The PRD resolves this with the `NotRequired` default; just be aware.
3. **Four-eyes default mismatch.** BR-UW-012 states defaults "`uw.four_eyes.sum_insured` (default €1,000,000 per risk; motor not applicable), `uw.four_eyes.premium` (default €50,000 annual)". §10.2 lists core default "none (off)" for both, and OI-UW-08 leaves the values open. It is unclear which default applies; the €1m in REQ-UW-113's GWT is an example.
4. **AI-UW-03 has no REQ-UW requirement.** It appears in §1.5 and on SCR-UW-03, but no functional requirement carries it. AI-UW-01's REQ-UW-159 is Should, yet it ships "off" in the motor MVP per §1.5.
5. **Must P1 rows that depend on uncontracted externals:**
   - REQ-UW-196 needs the gov.gr Wallet and vehicle registry (OI-UW-04).
   - REQ-UW-251 (hazard-zone keys in hold regions) is Must P1, but `Geocoder` hazard sources are P2 / UNVERIFIED (REQ-UW-197 P2, OI-UW-05). Postcode regions work in P1; hazard zones probably do not.
   - REQ-UW-195 CHS works by upload only.
6. **Clock register.** `UW_AMENDMENT_ACCEPTANCE` is added "by PRD-18 §11.3 (D7, CR-S3-17)" and is not in the original CMP register #15–#17. CMP must carry it (§16.4).
7. **WRK pattern codes** depend on the WRK catalogue additions in OI-UW-12, closed per XMR-CR-WRK-03. Verify that PRD-13 actually lists them.

### (c) Internal contradictions and inconsistencies

1. Duplicate rows in §9.1: `uw.Contingency.list`, `uw.DataSubject.erase/.restrict` and `uw.Decline.requestReview` each appear twice with slightly different detail.
2. The ApprovedWithConditions → Invalidated and Rejected → Open transitions are documented as R-33 additions. §7.3 also has Rejected → Closed, which is not mentioned in R-33.
3. **REQ-UW-271** routes the disclosure decision to "UW.NON_RENEWAL **or** UW.ISSUE_APPROVAL" holders. §12.2 says UW.NON_RENEWAL is "used by … disclosure terminations". Which authority gates TERMINATE vs PROPOSE_AMENDMENT is not specified.
4. §1.2 says AI may "suggest", and §1.5 lists AI-UW-03 in motor MVP, yet the AI-UW-03 toggle note says "never enabled for decline proposals in the MVP". This is consistent but subtle.
5. §5 footer counts "P1 218" while §1.5 says the motor MVP includes "accumulation — (not checked)". Fine, but REQ-UW-070 (Must P1) references the P2 accumulation switch.
6. `SubmissionIntake` retention uses "RC-POL-QUOTE (accepted)". That is a POL class, not a UW class: cross-module retention ownership.
7. SLA: G2 targets a median ≤ 4 business hours, but SLA policies are WRK data (BR-UW-021). UW states no actual SLA values beyond the example `UW-PL-4H`.

### (d) Cannot be built without a decision

- Four-eyes thresholds and the go-live authority matrix (OI-UW-08, decision #3).
- The CEL runtime: whether nested contexts run in the runtime or are flattened in UW (A3; pre-G0 spike, NFR-UW-001 p95 ≤ 150 ms at 200 evals/s).
- The external data MVP fallback (OI-UW-04/05, decision #5). Needed before REQ-UW-196 can pass acceptance.
- Automated declines in any MVP channel (OI-UW-13).
- Precedence between PTY revenue and declared revenue (decision #8, P3).
- The routing split between UW profile and WRK assignment rules (decision #9): who owns the final pick.
- Greek legal confirmation of the JMD and Law 2496/1997 before clocks go to production (OI-UW-01/02). P1 needs UW_NONDISCLOSURE_ACTION and UW_AMENDMENT_ACCEPTANCE values Settled.
- The real-time push mechanism (presence, live counts).

---

## 15. Build notes

### Quote/submission lifecycle as seen from UW (extra focus)

UW does not own the quote lifecycle (`REQ-POL-011`). Its touch-points are:

| Moment | What UW does |
|---|---|
| Every quote | PRE_QUOTE (before rating is released to channels) and PRE_BIND (after rating) are evaluated, so bind blockers show at quote time (REQ-UW-056) |
| Bind | PRE_ISSUE |
| Renewal job creation and re-quote | RENEWAL |
| Re-evaluation triggers | Requote, rebase after preemption, out-of-sequence reapply, sanctions change, report received, hold released (REQ-UW-101) |
| Channel visibility | POL sets the "Referred" flag in its view; CHN shows "needs underwriter approval" |
| Evaluation failure | Fail closed: typed error, POL refuses to quote or bind (REQ-UW-057) |
| Partner and AI pricing | Dry-run evaluation, nothing persisted (REQ-UW-058) |
| Review lock | Producer cannot edit while locked; auto-release after 24 h (REQ-UW-082/083) |
| Return to producer | REQ-UW-162 |
| Decline | POL marks the job Declined (`REQ-POL-011`) |
| Overturned automated decline | Job returns to Quoted (REQ-UW-220) |
| QuoteExpired, JobWithdrawn, JobNotTaken | Referral Withdrawn, issues closed (§8.3) |
| Commercial intake (P3) | New → Verifying → Cleared → Accepted, creating the PTY account and POL submission job (REQ-UW-170); unverified values are blocked (REQ-UW-169) |

### Referral rules and SLAs (extra focus)

| Topic | Rule |
|---|---|
| Creation | When blocking issues need someone else's decision (REQ-UW-004), or automatically for lanes ASSISTED/EXPERT where the channel permission matrix says refer (REQ-UW-120; `uw.referral.auto_create_lanes`) |
| Consolidation | One open referral per job; new issues join it (REQ-UW-124, BR-UW-018) |
| Routing | UW routing profile (routing key per issue type and lane → queue, skills, authority types, SLA policy code, priority weight; maker-checker) passed as hints to `wrk.Activity.create`, where WRK assignment rules pick the assignee (REQ-UW-121/122) |
| Restricted routing | Declines, non-renewals and sanctions issues go only to authority holders (REQ-UW-126) |
| Priority | Score = Σ weight × normalised factor over appetite fit, premium, broker tier, deadline proximity, live-channel waiting, vulnerability, lane; weights sum to 1; explained by factor contributions; recomputed daily and on change; no staff-performance factors (REQ-UW-127–130, BR-UW-019/020) |
| SLA | WRK SLA policies per pattern, channel, line and priority on business calendars (REQ-UW-131). Example UW-PL-4H: created Friday 16:00 → target Monday 12:00. Pauses while awaiting information (132). Breach → `ReferralSLABreached` (133) |
| Targets | G2: personal lines median ≤ 4 business hours, p95 ≤ 1 business day |
| Channels | List per R-84; bank channels configured but inactive in P1 (D6) |
| Modes | Pull ("Get next", default for personal lines) or push (135); workload caps (138, Could) |

### Hardest parts

1. **Approval fingerprint and validity engine.** Issue-key reconciliation (raise/keep/invalidate/close), tolerances (EXACT/ABS/REL/BAND), scope ordering, validity kinds, carry-forward across jobs and renewals, and rule-major-change invalidation. It must satisfy properties P1–P4 and P9.
2. **Rule platform.** An in-house CEL-compatible typed language with decimal semantics; decision tables with hit policies; nested "for each" contexts; explanation traces; content hashing; validation (overlaps, unreachable rows); test tables with a boundary generator; simulation over 90 days of history; four-eyes, effective-dated activation. All within p95 150 ms at 200 evals/s.
3. **Authority-at-decision.** Preview plus binding check, refer/deny UX, delegation, SoD and second approver. Most of the machinery lives in PLT, so PLT's framework must exist first.
4. **Cross-module choreography.** About 54 consumed event types, idempotent handlers, and the POL/WRK/CMP/DOC sagas for contingencies, disclosure findings and refusal documents.
5. **Workbench at volume.** 50,000 open referrals in scope; first page ≤ 1.5 s; Get next ≤ 500 ms.

### Must exist first

- PLT: authority framework, maker-checker, audit, numbering, time service, decision-table runtime and expression language, outbox.
- MKT: configuration keys and SPI registry (`RefusalDocumentRule` default, `MotorDataProvider` stub).
- PFC: product versions with rule-set references and question sets.
- WRK: activities, queues, SLA, the UW-REFERRAL pattern.
- POL: risk snapshot and job commands, the T1 gate call.
- CMP: clock API (for P1 disclosure clocks).
- PTY: consent and screening.

### Suggested slicing (P1 motor)

| Slice | Content |
|---|---|
| S1 | Issue-type catalogue, rule set / version / rule data model, decision-table authoring without the editor UI (import plus validation plus tests), evaluate with dry-run, blockingStatus, the UWIssue state machine. Golden motor set |
| S2 | Approvals: decide with authority preview and binding check, scope / validity / fingerprint / invalidation / carry-forward. Property tests P1–P5, P9. SCR-UW-02/03/04 |
| S3 | Referrals: WRK activity, routing profile, priority model, SLA events, Get next. SCR-UW-01, SCR-UW-20 |
| S4 | Declines without refusal documents (P1); automated-decline safeguards behind a switch; human review. SCR-UW-12 (without nat-cat) |
| S5 | Contingencies, inspections (photo self-inspection), external reports (CHS upload, `MotorDataProvider` stub with Wallet consent ref). SCR-UW-14/15/16 |
| S6 | Policy holds (postcode regions first). SCR-UW-11 |
| S7 | Renewal directions plus batch evaluation; disclosure findings with CMP clocks (incl. `UW_AMENDMENT_ACCEPTANCE`). SCR-UW-17 |
| S8 | Rule editor UI and simulation (SCR-UW-07/08/09); authority overview (SCR-UW-19); DSAR, retention, import APIs; metrics |
| Later | P2: accumulation, hazard `Geocoder`, valuation/ITV, vendor inspections. P3: intake/extraction, nat-cat compliance, refusal documents, register and export, campaigns, accumulation map. AI features last, all behind toggles |
