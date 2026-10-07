# Digest: PRD-02 — Product Factory and Configuration (PFC)

Source: `core-insurance-prds/PRD-02-product-factory-configuration.md` (2,145 lines, read in full). PRD version 1.4 "build-baseline freeze", dated 2026-10-07, binding input `00-system-contract.md` v1.11 (§1.1). Stack reference: `core-insurance-infra/ARCHITECTURE-DECISIONS.md` (read first).

---

## 1. Identity

| Item | Value |
|---|---|
| Module code | PFC |
| Title | Product Factory and Configuration |
| Owner roles | ROLE-14 product owner (accountable); ROLE-15 configuration engineer (schema); ROLE-34 pricing actuary and ROLE-29 compliance officer as co-reviewers (§1.1) |
| ID scheme | `REQ-PFC-001…011` contract anchors; module requirements from `REQ-PFC-030`; `BR-`, `NFR-`, `SCR-`, `AI-`, `OI-`, `CCR-PFC-`; risks `RK-PFC-` (§1.1) |

**Purpose (§1.2).** PFC defines every product as versioned, validated data, governs change, and tells every other module (RAT, UW, POL, BIL, CLM, RI, FIN, DOC, CHN, DAT, MIG) what a product contains at any date. It is a "configuration-as-code" factory: product source is YAML in a Git repository, validated against a JSON Schema and lint rules, compiled deterministically into an immutable artefact identified by a SHA-256 content hash, and approved through pull-request sign-off by named business roles. Runtime modules only see Locked, compiled, hash-identified artefacts.

**Five key decisions (§1.2):**
1. Git is the system of record for product **source**; the compiled artefact is the system of record for **runtime** (`REQ-PFC-002`). Authoring screens never edit production; every change is a change set (branch) → pull request.
2. Deterministic compilation with **pinned** (inside the hash) and **floating** references (rate versions, form editions, UW rule-set versions resolved by date and reported in a *resolution manifest* that POL stores beside the artefact hash) (`REQ-PFC-001`, `REQ-PFC-010`).
3. Layered inheritance: abstract EU base → country overlay → channel overlay; the compiler flattens layers; *final* constraints from a higher layer (e.g. Greek MTPL minimum held as an MKT final key, `REQ-MKT-001`) cannot be undercut (`REQ-PFC-003`).
4. Term-start resolution; endorsements stay on the term's version; a later version never re-rates an old term; declared version conversion at renewal for every consecutive major pair (`REQ-PFC-008`).
5. Segregated governance: author ≠ approver ≠ deployer (PLT SoD + maker-checker `REQ-PLT-004`); frozen evidence pack for Bank of Greece / audit (DORA change management; DR 2017/2358 Art. 9).

**Non-goals / out of scope (§1.4):** rating algorithms, rate tables, factor chains, worksheets, rate activation (RAT); policy transactions, reverse-and-reapply, renewal execution (POL); UW rule-set content and referrals (UW); form patterns, templates, clauses, rendering, IPID rendering (DOC, CD-09); rules runtime and workflow engine (PLT, `REQ-PLT-007`); configuration model, layers, final keys, regime code lists, SPIs (MKT, CD-06/CD-08); payment-plan definitions (BIL); commission agreements (PTY, CD-14); taxonomy transforms for marts (DAT); identity/roles/maker-checker/audit/authority (PLT); policy-form inference at transaction time (DOC). PFC holds **no tax rates** (`REQ-PFC-123`), **no commission rates** (`REQ-PFC-139`), **no tax refund key** (`REQ-PFC-116`).

**Phase scope (§1.5):** Motor MVP (P1) — full hierarchy, versions, states, resolution; element types policy line, vehicle, driver; MTPL with statutory finals, own damage, theft, fire, windscreen, roadside assistance, legal protection, driver personal accident; charges premium, IPT, Auxiliary Fund levy (two shares), policy fee, discounts, surcharges; **annual terms only** (six-month capability kept only as a golden test, decision D6); channel variants direct/broker/agent; **bank channel variants only if a bank partner is signed by G0** (bancassurance otherwise out of P1); all four regulatory regimes; full POG; full conversion; full authoring/diff/impact/scheduling/emergency; AI-PFC-02 and AI-PFC-07. P2 Home; P3 commercial property & liability (business nat-cat bundle with 70% floor, premium-audit schedules); P4 Cyprus live variant.

---

## 2. Size metrics

Counts as stated in §5.14 / §15.4 / §16.6, cross-checked against the tables.

| Metric | Count |
|---|---|
| Functional requirements (REQ-PFC) | **233** (11 contract anchors 001–011 + 222 module reqs; highest ID 251) |
| By MoSCoW | **Must 206**, Should 25, Could 2 (REQ-PFC-107, REQ-PFC-199), Won't 0 |
| By tag | [BASELINE] 133 (Must 128 / Should 4 / Could 1); [ENHANCEMENT] 100 (Must 78 / Should 21 / Could 1) |
| By phase | **P1 226**, P2 1 (REQ-PFC-177), P3 6 (REQ-PFC-089, 090, 091, 092, 107, 251), P4 0 |
| Business rules (BR-PFC) | 52 (verified: 001–008, 010–018, 020–027, 030–035, 040–042, 050–053, 060–062, 070–080) |
| NFRs (NFR-PFC) | 22 (001–022) |
| Screens (SCR-PFC) | 20 (SCR-PFC-01 … 20) |
| Owned entities (§7.1) | ~32 rows (incl. combined rows Exclusion/Condition, QuestionSet/Question, Reference subtypes) |
| Events produced | 8 (6 lifecycle + `ProductReferenceTablePublished` + `ProductRegulatoryAlertRaised`) |
| Events consumed | ~27 event types across RAT, MKT, PLT, POL, DOC, DAT, UW, CMP (§8.2) |
| Exposed operations (§9.1) | 21 operation groups (~35 individual operations) |
| Outbound integrations (§9.2) | ~34 operations across MKT, RAT, UW, DOC, BIL, POL, PTY, PLT, WRK, DAT, CMP |
| AI features | 7 (AI-PFC-01 … 07) |
| Lint rules (§14.x minimum catalogue) | 26 rule IDs (some paired /001 /002) |
| Contract change requests | 6 (all Accepted) |
| Open issues | 18 (OI-PFC-01…18; 7 closed) |

**Motor MVP (P1) count:** 226 requirements tagged P1, of which **Must P1 = 206** (all Musts are P1 — every P2/P3 requirement is Should or Could), Should P1 = 19, Could P1 = 1 (REQ-PFC-199). This is my derivation from the tables; the PRD states only the P1 total (226).

**Build-size estimate: L.** 206 Must requirements (twice the L threshold), 20 screens, plus heavy non-CRUD machinery: Git-backed authoring with item-level three-way merge, a deterministic compiler with layered inheritance and canonical-JSON hashing, an in-house CEL-compatible expression language (shared with UW/PLT, D10), a sub-millisecond in-memory resolver, temporal (window/effective-dated) resolution, version-conversion engine, and a 26-rule lint catalogue. It is the hub every other module depends on.

---

## 3. Owned entities

Storage split (§7.0): **product source** = YAML in Git (validated by `pfc-product.schema.json`, versioned); **compiled artefacts** = canonical JSON in a content-addressed write-once store; **operational records** (lifecycle state, change sets, approvals, schedules, POG, reviews, impact runs, read models, catalogue index) = PostgreSQL schema `pfc`. Every row carries `legal_entity_id`, `jurisdiction`, `created_at`, `created_by`, `record_version`, and `valid_from`/`valid_to` where it has business validity. Intervals are half-open `[valid_from, valid_to)` with UTC record time (XMR-FR-150, decision D5); screens show last day inclusive (stored `valid_to` = next day). "No other module reads this schema." Product definitions are P0 (no customer personal data). Retention: `RC-PRODUCT-DEF` (life of last policy/claim referencing the artefact + 10 years), `RC-PRODUCT-GOV` (product life + 10 years), `RC-PRODUCT-WORK` (3 years after closure for abandoned drafts).

### 3.1 Product model structure (focus)

Hierarchy (§7.4 ERD, §1.2 scope): **ProductLine → Product → ProductVersion** (per jurisdiction, legal entity, effective period) → **PolicyLineDef** (1..n) → **ElementType** (risk-unit tree) → **FieldDef**; PolicyLineDef → **CategoryDef**, **CoverageDef** → **CoverageTermDef** → **TermOption**; PolicyLineDef → **ExclusionDef**, **ConditionDef**. Version-level: **BundleDef**, **Offering → OfferingSelection**, **AvailabilityRule**, **GrandfatherRule**, **QuestionSet → Question**, **ChargeType**, **Reference** (RatingSlot, RuleSetRef, FormPatternRef, PaymentPlanRef, ReferenceTableRef), **RegulatoryMapping** (per coverage), **RenewalConversionRule**, **IpidData**. A ProductVersion may `extend` another (abstract base).

Note: the source uses "clause" only as a **reference to DOC** (exclusion/condition `clause_ref`, `REQ-PFC-083`; mandatory wording `REQ-PFC-247`). Clause and form definitions are owned by DOC (C-10, CD-09). There is no separately named "line of business" entity beyond ProductLine.family and the Solvency II LoB mapping.

| Entity | Identifier | Key attributes | Constraints |
|---|---|---|---|
| ProductLine | `product_line_id` UUIDv7; code | code (40), name_el/en (120), family (motor, property, liability, accident, assistance, other), display_order | code unique per legal entity (REQ-PFC-030) |
| Product | `product_id`; code | product_line_id, names/descriptions per language, abbreviation (≤10), product_type (personal, commercial), customer_type (person, organisation, any), display_order, external_codes (ACORD/LEGACY/INTEGRATION) | code unique per LE; immutable after first Locked version (REQ-PFC-031, 047) |
| ProductVersion | `product_version_id`; product code + `major.minor` | product_id, major, minor, jurisdiction (ISO 3166-1 α2), legal_entity_id, is_abstract, extends_version_id, overlay_kind (none, country, channel), channels (set, shared Channel code list R-84), contract_currency (ISO 4217), languages, nb_window_from/to, renewal_window_from/to, default_term, allowed_terms, offering_required, day_count, oos_conflict_rule, refund_methods, audit_schedules (P3), status, lifecycle_substate, artefact_hash (char 64), source_commit (char 40), schema_version, compiler_version, significant_adaptation | Locked windows of same (product, jurisdiction, LE, intersecting channel scope) non-overlapping (BR-PFC-001); Locked rows immutable except window closure and sub-state |
| PolicyLineDef | code | names, policy_line_type, display_order, coverage_currencies, rating_territory_required, reference_code, initial_value_rules | unique in version (REQ-PFC-036/037) |
| ElementType | code | names, parent_code, card_min, card_max, library_ref (code, version) | acyclic parent graph (REQ-PFC-048/049) |
| FieldDef | element + field code | labels, help texts, customer_visible, data_type (15 types, REQ-PFC-051), unit, precision, rounding_mode, required_rule, default_rule, validation_rules (expression, severity, checkpoints, message_key), visibility_rule, editability (txn type → allowed/rewrite), pii_class P0–P3, value_source (entered, derived, external, reference), spi_binding, reference_table_ref, display_group/order | pii_class mandatory (BR-PFC-078) |
| CategoryDef | code | names, kind (coverage/exclusion/condition group), display_order | — |
| CoverageDef | code | names, category, covered_element, existence (required, electable, suggested), existence_rule, initial_values, removal_rule (block/cascade), covered_party_type (first/third), reference_date_basis (term start / txn effective date), requires/excludes/implies, ri_cedable, ri_risk_class, peril_tags, include_in_new_offerings, reference_code, customer_visible | dependency graph acyclic |
| CoverageTermDef | coverage + term code | kind (option_list, range, direct, boolean, enumeration, package), value_type (money, percentage, count, days, hours, other+unit), unit, required, defaults (currency→value), enumeration_source, model_type (limit, sub-limit, deductible, sum insured, benefit, waiting period, indemnity period, co-insurance), aggregation_basis (per injured person, per accident/occurrence, per claim, aggregate per term, per item), restriction_subject, range min/max/step/default, **final_binding (config key)**, constraints, library_ref | option_list ≥ 1 option (BR-PFC-005) |
| TermOption | term + option code | value (decimal 19,4), currency, descriptions, display_order, reference_code, package_values | value ≥ bound final (BR-PFC-010) |
| ExclusionDef / ConditionDef | code | names, category, covered_element, existence, reference_date_basis, rules, customer_visible, **clause_ref**, reference_code | clause_ref required when customer_visible |
| BundleDef | code | coverage codes, SI floor rule (asset class → pack key), compliance_obligations (OBL codes) | P3 |
| AvailabilityRule | rule id | scope_item, start, end, outcome, region_codes, LE, transaction_types, channels, producer_groups, element_condition, activity_codes | end ≥ start |
| GrandfatherRule | rule id | scope_item, region_codes, LE, end_date | end > unavailable-from |
| Offering / OfferingSelection | code / offering+item | names, compliance_obligations / enabled, default_values, allowed_options, modified (derived) | required items cannot be disabled |
| QuestionSet / Question | set / question code | set type (pre-qualification, underwriting, demands-and-needs, offering selection, portal), texts, answer_type, allowed answers, display_rule, per-answer outcome (none/knock-out/referral + UW rule code), maps_to_field | display rules acyclic |
| ChargeType | code | names, customer label, category (premium, tax, levy, fee, surcharge, discount, credit), **written_premium (derived)**, sign, computed_by (rating, TaxCalculator), element_level, modifies_code, handling (pro rata, flat, fully earned), earning_pattern, cancellation_treatment, reemit_on_reapply, billing_treatment (billed / accrued_not_billed), tax_class, included_in_tax_bases, gl_key, beneficiary, fiscal_category_key, ri_cedable, commissionable, display_group, valid_from/to | no rate attributes when computed by TaxCalculator; not orphaned (BR-PFC-021) |
| Reference | kind + target | binding_mode (pinned/floating), version_or_range, checkpoint, subject item, channel | resolvable (BR-PFC-040) |
| RegulatoryMapping | coverage + regime | regime (SII_LOB, IPT_CLASS, STAT_CLASS, AUTH_CLASS, IFRS17_PORTFOLIO), code, split_pct, code_list_version | codes valid in list version; splits = 100 |
| RenewalConversionRule | source ver + target ver + item | action (map, grandfather, refer, drop_with_notice, non_renew), target_item, value_mapping (same, nearest higher, nearest lower, fixed), grandfather_until, referral_reason; source may be `LEGACY:<code>` | complete over source catalogue (BR-PFC-050) |
| IpidData | version + offering | nine sections per language, derived items + authored text, legal approval status | non-empty for customer offerings |
| ProductReferenceTable | code + version | effective period, key/value columns, rows, display values, content hash | keys unique; CCR-PFC-01 accepted |
| GoldenPolicy | product + golden id | synthetic draft, checkpoint, expected validation, expected rating per charge type, justification history | synthetic only (BR-PFC-061) |
| PogRecord | `pog_record_id`; product + record version | target/negative criteria, rationales, distribution_strategy, review_interval_months, next_review_due, status, approved_by/at | one Approved per product |
| PogTest | `pog_test_id` | record, version, scenario, method, data, result, conclusion, tester, approver, evidence_refs | approver ≠ tester |
| PogReview | `pog_review_id` | due_date, performed_at, indicators snapshot, outcome, decision_maker, remediation change set, distributor notice | immutable once recorded |
| ChangeSet | `change_set_id`; branch | product, base version/commit, target version, title, purpose, driver, is_emergency, status, owner, pull_request_ref | one Submitted per target version |
| ApprovalRecord | `approval_record_id` | version, role, decision, comment, conditions, decided_by/at, authority_check_ref, maker_checker_ref, voided | decider not an author (BR-PFC-070) |
| CompiledArtifact (index) | artefact hash | version or table, size, manifest, stored_at, verified_at | write-once |
| ReleaseSchedule | `release_schedule_id` | version, environment, activation_at (UTC + LE tz), status, scheduled_by, reason, workflow_instance_id | scheduler not author/approver (BR-PFC-071) |
| ImpactAnalysisRun | `impact_run_id` | from/to, data_as_of, counts by channel/region, RAT impact ref, dry-run counts, downstream effects | aggregates only |
| EvidencePackRef | `evidence_pack_id` | version, DOC archive id, pack hash, index | immutable |
| ProductVersionUsageView (read model) | version id | in-force, scheduled, pending renewals | refreshed nightly and on POL events |
| Read models (REQ-PFC-138) | — | RatingArtifactView, UwRuleSetView, FormPatternView, PaymentPlanView | refreshed from owner events |

### 3.2 State machines (§7.3)

**ProductVersion** (contract 3.2.4; five states): Draft, Submitted, Approved, Locked, Retired. Locked sub-states Active → ClosedToNewBusiness → RunOff (CCR-PFC-06, R-11).

| From | To | Trigger | Guards | Event |
|---|---|---|---|---|
| — | Draft | Change set created | `pfc.author`; base Locked or Draft | — |
| Draft | Submitted | Submit | schema, lint, compile, golden tests, Cyprus variant (if core touched) pass; POG present; conversion rules complete | `ProductVersionSubmitted` |
| Submitted | Draft | Return | required approver; comment; sign-offs voided | `ProductVersionReturned` |
| Submitted | Approved | Final sign-off | all required roles; authority allow; maker ≠ checker; impact report present | `ProductVersionApproved` |
| Approved | Draft | Revoke | not yet Locked; approver with reason | `ProductVersionReturned` |
| Approved | Approved | Schedule/reschedule/cancel | release manager; not author/approver; compatible rating artefact active at activation | `ProductVersionScheduled` |
| Approved | Locked | Timer fires | re-validation if approval > 30 days; references resolvable | `ProductVersionPublished` |
| Locked(Active) | Locked(ClosedToNewBusiness) | NB window end or fall-back | — | `ProductVersionScheduled` carrying window change |
| Locked | Retired | Nightly job | both windows ended; in-force and scheduled counts 0 confirmed via `REQ-POL-014` | `ProductVersionRetired` |

Illegal transitions return `PFC-ERR-ILLEGAL-TRANSITION` (REQ-PFC-162). Discarded drafts stay Draft with ChangeSet.status = Abandoned.

**ChangeSet:** Open → Submitted → Merged (version Approved and merged to main); Submitted → Open (returned); Open → Abandoned (owner, or after 180 days inactive with 14 days' notice, BR-PFC-075). No events; audit per edit.
**PogRecord:** Draft → Approved → Superseded. **PogReview:** Due → Overdue → Recorded (immutable).
**ReleaseSchedule:** Scheduled → Fired / Cancelled / Failed; Failed → Scheduled.

### 3.3 Versioning and activation (focus)

- Numbering `major.minor`; system proposes **major** when the diff contains structural changes (coverage removed, term kind changed, element type changed, required field added), else minor; owner override recorded with justification (REQ-PFC-165, BR-PFC-002).
- New version = branch from latest Locked (or an existing Draft) (REQ-PFC-164, 182). Locked versions and artefacts are immutable; any change → new version.
- Each Locked version has a **new-business window** and a **renewal window** (REQ-PFC-166). Publishing a successor sets predecessor's NB end in the same transaction (REQ-PFC-033). Overlaps fail `PFC-LINT-OVERLAP-001`.
- Resolution rules: new business and renewal resolve by **term start date**, not quote date (REQ-PFC-167, BR-PFC-003); renewals prefer the version the predecessor's conversion rules target; policy changes, cancellations, reinstatements **do not resolve** — POL uses the term's stored hash (REQ-PFC-168, BR-PFC-004). Publishing a later version never changes artefact, pinned references or validation outcome of existing terms (REQ-PFC-169). Abstract bases never resolvable (`PFC-ERR-ABSTRACT`, BR-PFC-007). Time-travel resolve with `knownAt` (REQ-PFC-170, Should).
- Activation: release manager schedules an Approved version for a date-time in the legal entity's time zone (≥ now + 15 min, BR-PFC-079; ≤ NB window start); at that time the "workflow engine" locks the version, publishes to the runtime store and emits `ProductVersionPublished`; resolve must return it within 60 s (REQ-PFC-211). Cancel/reschedule with reason (REQ-PFC-212). Approval older than 30 days requires re-validation (REQ-PFC-217, BR-PFC-042). Retry with backoff and alert on publish failure (J-02).
- Same hash promoted dev → test → UAT → pre-prod → prod, never recompiled (REQ-PFC-210).
- Fall-back: close defective version to NB and re-open predecessor's windows via a new minor of the predecessor (e.g. 3.3 = copy of 3.2 with new windows); terms written on the defective version stay on it (REQ-PFC-213).
- Emergency path: reduced but segregated sign-off (product owner + compliance, neither an author), authority `PFC.EmergencyChange`, same automated checks (REQ-PFC-214); retrospective full review within 5 working days (REQ-PFC-215).
- Close to new business on date or when a violated statutory final takes effect (REQ-PFC-178, BR-PFC-012). Withdrawal requires run-off decision: convert to successor or non-renew; POL performs non-renewal and starts notice as a FIXED_DATE deadline on the CMP clock register (REQ-PFC-179, D7). Auto-Retire per REQ-PFC-180. Retired artefacts remain retrievable by hash (REQ-PFC-181, 227).

### 3.4 Artefact, hash and configuration hash (focus)

- Artefact = SHA-256 of canonical serialisation; retrievable by hash for the life of any policy/claim referencing it; store rejects modification (REQ-PFC-002, 197).
- Compiler deterministic: same source commit, base artefacts, pinned references, compiler version → byte-identical output (REQ-PFC-193). Serialisation **RFC 8785 JCS**, sorted keys, normalised decimals, no build timestamps (REQ-PFC-194); §7.6 adds: UTF-8, **decimals as strings with fixed scale**, hash lowercase hex; typical motor artefact ≤ 2 MB (NFR cap 5 MB).
- Artefact parts (§7.6): `manifest` (hashed: artefactFormat, schemaVersion, compilerVersion, product, version, jurisdiction, legalEntity, sourceCommit, baseArtefacts, pinnedReferences, **finalsUsed (key, layer, value, effective intervals, configurationHash)**, regimeCodeListVersions); `product` (hashed flattened structure incl. provenance map); `rules` (hashed compiled typed AST + decision-table refs); `envelope` (NOT hashed: buildTime, pipelineRunId, signer, signature over hash).
- **Configuration hash:** REQ-PFC-195 — the manifest records "final configuration keys used with configuration hash"; source is `mkt.Configuration.currentHash` (§9.2, `REQ-MKT-001`). Levy split changes do **not** change the artefact hash (REQ-PFC-125 GWT) because rates/splits are MKT pack data read by `TaxCalculator`.
- **Resolution manifest + resolution hash** (REQ-PFC-221): artefact hash + floating references resolved (rating artefact hash, UW rule-set versions per checkpoint, form-pattern editions, reference-table versions, payment-plan versions) and a hash over the manifest. Rating artefact is "floating at resolution and pinned for the term": POL stores the manifest on the term (`REQ-POL-033`), in-term transactions reuse it (`REQ-RAT-063`).
- Nightly rebuild of every Locked artefact from manifest; mismatch → PLT incident (REQ-PFC-196, BR-PFC-062, G-2). Artefacts signed by pipeline key in KMS (NFR-PFC-013). Runtime loads all Locked artefacts at start-up and reports ready only after hash verification (REQ-PFC-228).

---

## 4. Consumed entities / dependencies

| Entity | Owner | Use | Via |
|---|---|---|---|
| ConfigKey / ConfigValue / ConfigHash | MKT | Final keys (statutory minima), pack parameters (floors, splits), review intervals | `mkt.Configuration.resolve`, `currentHash` (`REQ-MKT-001`); `ConfigurationActivated`, `PackActivated`, `PackRolledBack` |
| RegimeCodeList | MKT | SII LoB, IPT class, stat class, auth class, IFRS 17, activity codes (`ACTIVITY_CODE`, KAD) | `mkt.RegimeCode.list` (`REQ-MKT-007`); `RegimeCodeListPublished` |
| SpiBinding | MKT | TaxCalculator, IdValidator, MotorDataProvider, FiscalDocumentChannel, DocumentLanguageRule, PricingConstraint, MandatoryWordingSet | `mkt.Spi.bind` (`REQ-MKT-002/110/115`) |
| TranslationEntry, currency/rounding | MKT | Labels; UI strings; rounding | `REQ-MKT-005`, `REQ-MKT-006` |
| RatingArtifact / RateTable | RAT | Rating-slot compatibility; RatingArtifactView | `rat.RatingArtifact.resolve`; RAT events; `REQ-RAT-061/064/066/067` |
| UWRuleSet | UW | Floating refs per checkpoint | `UWRuleSetActivated`; `uw.Rules.evaluate` |
| FormPattern / Binding / Clause | DOC | Form and clause references | `doc.FormPattern.validateReferences`, `doc.Clause.get`; DOC events |
| PaymentPlan | BIL | Plans offered per channel | `bil.PaymentPlan.list` |
| Policy / PolicyTerm counts | POL | Impact and retirement | `pol.Policy.search` (`REQ-POL-014/320`); POL events |
| Producer codes | PTY | Producer-group availability | `pty.ProducerCode.validate` (`REQ-PTY-008`); party role check `REQ-PTY-002`; party address `REQ-PTY-012` |
| User/Role/Authority/ApprovalRequest/Audit | PLT | RBAC/ABAC, SoD, authority, maker-checker, audit | `REQ-PLT-001…004` |
| Activity, notifications | WRK | Reviews, alerts, retrospective reviews | `wrk.Activity.create` (`REQ-WRK-001/007`) |
| RenderedDocument archive | DOC | Evidence packs, specs | `doc.Archive.*` (`REQ-DOC-004`) |
| Mart data, KPIs, Model registry | DAT | Impact aggregates, POG indicators, AI model registry | `dat.Population.aggregate`, `dat.Kpi.get`, `dat.Model.register` |
| AiSystem | CMP | AI register | `cmp.AiSystem.register` (`REQ-CMP-007`) |

---

## 5. Events

Envelope per contract 3.4.1; "Topic `pfc.events.v1`; partition key `product_id`" (§8 — see conflicts). Delivery via transactional outbox (`REQ-PLT-005`), exactly one event per transition, consumers dedupe on `event_id`. Every `ProductVersion*` payload carries the rating-slot declaration (algorithm code, input-schema version, pinned/floating, compatible range) (REQ-PFC-245, R-27). No personal data in payloads.

### 5.1 Produced (§8.1; REQ-PFC-009 "eight events")

| Event | Trigger | Key payload | Consumers |
|---|---|---|---|
| `ProductVersionSubmitted` | Draft → Submitted | product code, version, candidate hash, change classes, significant adaptation, required sign-off roles, change set id | DAT |
| `ProductVersionApproved` | Last sign-off | code, version, hash, approver roles + timestamps, evidence pack id | RAT, UW, FIN, DAT |
| `ProductVersionScheduled` | Schedule/reschedule/cancel, or Locked window change | code, version, hash, environment, activation time, NB and renewal windows, reason | RAT, POL, DAT |
| `ProductVersionPublished` | Locked at activation | code, version, hash, windows, jurisdiction, LE, channels, `ipidChanged`, changed areas | RAT, UW, POL, FIN, DOC, CHN, MKT, DAT |
| `ProductVersionRetired` | Locked → Retired | code, version, hash, retirement date | RAT, UW, POL, DOC, CHN, MKT, DAT |
| `ProductVersionReturned` (R-02) | Submitted/Approved → Draft | code, version, returned-by role, reason category | DAT |
| `ProductReferenceTablePublished` (R-02) | Table version published | table code, version, hash, effective period | RAT, DAT |
| `ProductRegulatoryAlertRaised` (R-02) | Final or code-list change makes versions non-compliant | code, versions, key/code list, effective date, violation count | DAT (PFC opens WRK activities itself) |

Consumer lists name only modules with declared handlers (R-87).

### 5.2 Consumed (§8.2)

| Event | Producer | Reaction |
|---|---|---|
| `RatingArtifactPublished`, `RateVersionActivated`, `RateVersionScheduled` | RAT | Update RatingArtifactView; re-check rating-slot compatibility for Approved versions; warn release manager |
| `RateVersionWithdrawn` | RAT | Same + re-check floating refs in Locked versions; `PFC-ERR-NO-RATING` warning (XMR-CR-RAT-05) |
| `ShadowRunCompleted` | RAT | Attach to ImpactAnalysisRun |
| `ConfigurationActivated`, `PackActivated`, `PackRolledBack` | MKT | Re-lint versions using affected finals/code lists (REQ-PFC-087); refresh pack parameter cache |
| `RegimeCodeListPublished` | MKT | Re-validate mappings (REQ-PFC-141, 145) |
| `CrossBorderAuthorisationChanged` | MKT | Re-lint host-State versions; raise `ProductRegulatoryAlertRaised` when authorisation ends |
| `ConfigChanged` | PLT | Refresh runtime parameters |
| `AiToggleChanged`, `AiKillSwitchActivated` | PLT | Enable/disable AI-PFC features |
| `PolicyBound`, `RenewalBound`, `PolicyCancelled`, `PolicyRewritten`, `PolicyNonRenewed`, `PolicyLapsed` | POL | Update ProductVersionUsageView (indicative; retirement re-checks via `REQ-POL-014`) |
| `DocumentRendered` | DOC | Link evidence pack/spec documents |
| `FormPatternPublished`, `FormPatternRetired` | DOC | Refresh FormPatternView; re-lint retired editions (`PFC-LINT-FORM-001`) |
| `BindingChanged` | DOC | Refresh view; re-lint bindings; flag impact report |
| `ClauseVersionPublished` | DOC | Re-check clause refs and mandatory wording (`PFC-LINT-WORDING-001`) |
| `UWRuleSetActivated` | UW | Refresh UwRuleSetView; floating-range compatibility |
| `ModelDriftDetected`, `BiasThresholdBreached` | DAT | Flag or auto-disable AI feature |
| `ComplaintReceived` | CMP | Increment POG indicator (count by product) |

Explicitly **not** consumed: `TemplateVersionPublished` (DOC), `ComplaintAnswered` (CMP).

---

## 6. APIs

### 6.1 Exposed (§9.1; in-process, REST under `/api/pfc/v1/`)

| Operation | Purpose | Notes / errors | Req |
|---|---|---|---|
| `pfc.ProductVersion.resolve` | Resolve Locked version: jurisdiction, legalEntity, product, channel, transactionType (NewBusiness, Renewal), date, optional knownAt, predecessorHash | Returns version, artefactHash, resolutionManifest, resolutionHash. `PFC-ERR-NO-VERSION` (reason, e.g. `CHANNEL-NOT-OFFERED`, not yet effective, closed), `-UNKNOWN-PRODUCT` (404), `-ABSTRACT`, `-NO-RATING` | 001, 167, 221 |
| `pfc.Artifact.get` | By hash, optional parts | `PFC-ERR-UNKNOWN-HASH` | 002, 227 |
| `pfc.Catalogue.get` / `getItem` | Coverages, terms, options, exclusions, conditions, categories, element types, fields, offerings, charge types; incl. finals (key, layer, value) | `PFC-ERR-UNKNOWN-ITEM` | 003, 226 |
| `pfc.ChargeType.list` | Charge catalogue incl. `writtenPremium` | — | 004, 250 |
| `pfc.RegulatoryMapping.get` | Coverage, regime, taxonomy version | — | 005 |
| `pfc.QuestionSet.get` / `evaluate` | Visibility/required/knock-out/referral | pure | 006 |
| `pfc.Pog.get` | POG record; `includeDistributorPack`; `reviewOverdue` | — | 007, 157 |
| `pfc.RenewalConversion.convert` | sourceHash or `LEGACY:<code>`, targetHash, selections, renewal date → converted, grandfathered, referrals, non-renew | `PFC-ERR-NO-CONVERSION-RULES`; pure, dry-run by nature | 008 |
| `pfc.Availability.check` | Item + context → available, reason, winning rule id | — | 011 |
| `pfc.Product.describe` | product or hash, channel, txn type, language, date → ordered steps, fields, rules, offerings, coverages, payment plans | — | 222 |
| `pfc.PolicyDraft.validate` | hash, checkpoint, draft → `errors[]` (level, rule, path, message key, GR/EN message, severity), RFC 9457 style; partial drafts | pure | 224, 225 |
| `pfc.ChangeSet.create/edit/revert/rebase/abandon` | Authoring | Idempotency-Key; `PFC-ERR-CONFLICT`, `-FORBIDDEN` | 182–188 |
| `pfc.ProductVersion.submit` | Submit | `PFC-ERR-CHECKS-FAILED`; dry-run runs checks | 186 |
| `pfc.ProductVersion.signOff` | Approve/return | `PFC-ERR-MAKER-IS-CHECKER`, `-AUTHORITY` | 204, 206 |
| `pfc.ProductVersion.schedule/cancelSchedule/fallBack` | Release | `PFC-ERR-SOD`, `-NO-RATING`; dry-run | 211–213 |
| `pfc.ProductVersion.diff` | Business-language diff | — | 200 |
| `pfc.ImpactAnalysis.run` | Async impact report | `PFC-ERR-DATA-UNAVAILABLE` | 207–209 |
| `pfc.Pog.update/recordReview/recordTest` | POG | — | 148–158 |
| `pfc.ProductImport.import` | Legacy import to Draft change set; dry-run; idempotent; `origin=MIGRATION` | `PFC-ERR-IMPORT-INVALID` | 240 |
| `pfc.LegacyXref.get` | Legacy → core codes | — | 241 |
| `pfc.ReferenceTable.get` | Rows by code + version/date | — | 063 |

Also an in-process client library caching artefacts by hash (REQ-PFC-230, Should). Errors RFC 9457 with `PFC-ERR-*` codes, localised (REQ-PFC-232).

### 6.2 How other modules consume product definitions (focus; §10.3 + anchors)

| Consumer | What PFC defines | Anchor / mechanism |
|---|---|---|
| RAT | Rating slot + input schema, element/field catalogue, coverages and terms, charge types (premium, discount, surcharge, fee), reference tables, day-count | REQ-PFC-002/003/004/010; RAT declares compatibility (`REQ-RAT-061`); manifest names active rating artefact |
| UW | Question sets with knock-out/referral flags, rule-set refs per checkpoint (pre-quote, pre-bind, pre-issue, renewal), availability, conversion referrals, nat-cat compliance markers | 006/008/010/011 |
| POL | Resolution, describe, validate, term lengths, editability, refund methods per cancellation source, out-of-sequence rule, conversion; stores manifest on term; endorsements via `Artifact.get(hash)` | 001/002/008/010 |
| BIL | Billing treatment, tax-base flags for BIL-originated fees (D2: originating module calls TaxCalculator once), payment plans offered | 004/010 |
| CLM | Term semantics (limits, aggregation, deductibles), peril tags; old artefacts by hash | 003 |
| RI | RI-cedable flags, risk class, perils | 003/004 |
| FIN | Earning pattern, GL keys, beneficiary, IFRS 17 portfolios, `writtenPremium` (D5) | 004/005 |
| DOC | Form refs per coverage, clause refs, IPID data, labels; `ipidChanged` flag | 010/003/159/161 |
| CHN | Describe (no front-end logic; evaluates via QuestionSet.evaluate / PolicyDraft.validate, REQ-PFC-223), offerings, availability, POG + distributor pack, demands-and-needs sets, ACORD codes | 001/006/007/011 |
| DAT | Regulatory mappings, catalogue, lifecycle events | 005/009 |
| MIG | Legacy xref, import, legacy-source conversion | 008, 240–242 |
| PTY | Product code existence for commission agreements | 139 |

Runtime design: resolver served from an **in-memory index per deployment stamp, no DB/network on request path** (REQ-PFC-220), p99 < 1 ms; refresh within 5 s of publish/retire/schedule across all instances (REQ-PFC-229); runtime keeps serving from memory if PFC DB is down (NFR-PFC-009). Floating refs resolved from local read models without synchronous calls (REQ-PFC-138).

### 6.3 Consumed (§9.2) — summarised
MKT (`mkt.Configuration.resolve/currentHash`, `mkt.Spi.bind`, `mkt.RegimeCode.list`, translation store, `mkt.Currency.rule`, `mkt.Rounding.apply`); RAT (`rat.Rate.rate` dry-run, `rat.GoldenSuite.run`, `rat.RatingArtifact.resolve`, `rat.Comparison.compareVersions`, `rat.ImpactAnalysis.run`, `rat.Worksheet.get/explain`); UW (`uw.Rules.evaluate`); DOC (`doc.FormPattern.validateReferences`, `doc.Clause.get`, `doc.Archive.store/get`, `doc.Document.request/preview`, `doc.Ipid.get`); BIL (`bil.PaymentPlan.list`); POL (`pol.Policy.search`); PTY (`pty.ProducerCode.validate`); PLT (`plt.Policy.evaluate`, `plt.Audit.append`, `plt.Authority.check`, `plt.AuthorityType.register`, `plt.Approval.request/verifyForExecution`, `plt.Outbox.publish`, `plt.Adapter.invoke` for Git/CI, PLT workflow engine and rules runtime, `plt.FeatureFlag.evaluate`, `plt.Ai.invoke/recordOutcome`, retention, incidents, observability); WRK (`wrk.Activity.create`, notifications); DAT (`dat.Population.aggregate`, `dat.Kpi.get`, `dat.Model.register`); CMP (`cmp.AiSystem.register`, `cmp.Evidence.submit`).

External (§9.3): Git repository service (self-hosted in EU stamp, operated by PLT, webhooks to integration hub); CI pipeline (runners operated by PLT); rules runtime (PLT, in-process); DOC template repo; "Lakehouse (DAT)"; partner APIs (CHN, ACORD-aligned).

---

## 7. SPIs / country-pack interfaces

All via MKT `REQ-MKT-002` unless noted (PFC defines none of its own):

| SPI | PFC use | Req |
|---|---|---|
| `TaxCalculator` (incl. `treatment(chargeType, transactionKind, cancellationSource)`) | Computes tax and levy charge types after premium; tax/levy cancellation treatment (`PACK_TAX_TREATMENT`) | 116, 117, 123–125, 128, 244; `REQ-MKT-087` |
| `IdValidator` | Field validators, e.g. AFM check digit (`afm.invalid`) | 053 |
| `MotorDataProvider` | External vehicle make/model catalogue with reference-table fallback | 064 |
| `FiscalDocumentChannel` | Maps fiscal-category key per charge type to myDATA categories | 121 |
| `DocumentLanguageRule` | Binding language per document type | 058, BR-PFC-008 |
| `PricingConstraint` (`prohibitedFactors`, `proxyAttributes`) | Compile-time lint of rating inputs (`PFC-LINT-PRICING-001`) | 246; `REQ-MKT-110` |
| `MandatoryWordingSet` (`clauses(documentType, productLine, jurisdiction, date)`) | Lint mandatory clauses bound (`PFC-LINT-WORDING-001`) | 247; `REQ-MKT-115` |
| `RegimeCodeList` | Regime codes incl. `ACTIVITY_CODE` (KAD) | 005, 107, 140–145; `REQ-MKT-007` |

Multi-market split (§10.4): core holds mechanisms (final binding, charge-type attributes, mapping model, labels, region picker, abstract EU base); pack holds values (minima, rates, classes, myDATA mapping, code lists, binding language, regions, activity codes, country overlay). Cyprus stub compiles and golden-tests on every main build (REQ-PFC-239). Another EU market must supply finals, TaxCalculator, fiscal mapping, code lists, language rule, regions/activity codes, overlay, translations, golden policies.

---

## 8. Screens (§6)

Shared conventions (§6.0): run inside PLT staff workbench shell; command palette (`IB-01`); workspace context bar with status (`IB-21`) and validation summary; breadcrumb + left structure tree (split pane `IB-05`); edit in place with autosave/undo (`IB-14`), each save a commit; permissions `pfc.read`, `pfc.author`, `pfc.charge.edit`, `pfc.regmap.edit`, `pfc.pog.edit`, `pfc.approve.<role>`, `pfc.release`, `pfc.audit.read` with ABAC on LE, jurisdiction, product line; semantic states only (contract 3.9.9); **Greek/English switch (R-101)** without reload or loss of edits, UI strings in MKT translation store, missing translation fails the release gate (Must, P1); glossary: version "έκδοση προϊόντος", offering "πακέτο κάλυψης", channel "κανάλι διανομής"; † marks working Greek translations pending ROLE-46.

| ID | Name | Persona | One line |
|---|---|---|---|
| SCR-PFC-01 | Product catalogue and home | All PFC roles; auditors RO | Filter bar (`IB-26`), "Needs attention" exception panel (`IB-28`), catalogue table, work views (`IB-03`) |
| SCR-PFC-02 | Version workspace: structure tree and product basics | ROLE-14/15/42 | Tree, product and policy-line basics, provenance, rebase; "Modifiers" replaced (rating → RAT) |
| SCR-PFC-03 | Element type and field editor | ROLE-15 | Element cardinality, field grid, rule editor, describe preview |
| SCR-PFC-04 | Coverage list and coverage editor | ROLE-15 | Inline add, existence, dependencies, RI, perils, offerings |
| SCR-PFC-05 | Coverage term and option editor | ROLE-15, actuary | Term kinds, options grid with bulk paste, finals panel |
| SCR-PFC-06 | Availability and grandfathering editor | ROLE-15/13 | Inherited rules, rule/grandfather grids, availability tester with explain-why (`IB-08`) |
| SCR-PFC-07 | Exclusions, conditions and categories | ROLE-15/46 | Tabs; clause reference to DOC |
| SCR-PFC-08 | Offerings matrix | ROLE-14/15 | Items × offerings grid, defaults, allowed options, compliance marker |
| SCR-PFC-09 | Question set editor | ROLE-15/13 | Questions, display rules, knock-out/referral, maps-to-field |
| SCR-PFC-10 | Charge-type editor | ROLE-15, ROLE-26/25 review | Financial treatment, "used by" orphan detection |
| SCR-PFC-11 | Rule-set and form mapping | ROLE-15 | References, binding mode, compatibility (`IB-32`), payment plans, refund matrix (7 sources), day-count, OOS rule |
| SCR-PFC-12 | Regulatory mapping table | ROLE-15/29/25 | Coverages × regimes grid, export |
| SCR-PFC-13 | Changes panel and change review | Authors | Pending edits, revert, validate only, submit |
| SCR-PFC-14 | Version diff and impact analysis | Approvers | Business-language diff, change classes, impact (`IB-17`), AI narrative card (`IB-09`) |
| SCR-PFC-15 | Approval and PR status with sign-off evidence | Approvers | Check strip, sign-offs, evidence pack (UIL-U9) |
| SCR-PFC-16 | Release: promotion and scheduling | ROLE-40 | Environment pipeline, schedule, fall-back, emergency |
| SCR-PFC-17 | Mapping editor for version conversion at renewal | ROLE-15/13 | Diff-generated mapping grid, dry-run |
| SCR-PFC-18 | POG record and IPID data | ROLE-29/14/46 | Target market, distribution strategy, testing, monitoring, reviews, IPID, distributor pack |
| SCR-PFC-19 | Product reference tables | ROLE-15 | Versioned tables, CSV import, diff |
| SCR-PFC-20 | Product test harness | ROLE-15 testers | Draft builder from describe, validate, dry-run rate, golden policies |

Design-guide patterns referenced: IB-01, 02, 03, 05, 06, 07, 08, 09, 10, 12, 14, 16, 17, 19, 20, 21, 22, 23, 24, 26, 28, 29, 32, 33. Inventory coverage (§6.21): 31 owned items (29 Guidewire Product Designer reference screens + UIL-U7, UIL-U9): 26 specified, 5 replaced, 0 missing. All baseline "scripts" replaced by declarative rules; "Available Date" fields replaced by inherited availability; "Column" N/A; Synchronize credential dialog removed; Audit Schedules tile N/A for motor (OI-PFC-17).

---

## 9. Regulatory, tax and statutory rules

### 9.1 Stated explicitly by the source

| Rule | Value as stated | IDs |
|---|---|---|
| **Compulsory Greek MTPL** minima | PD 237/1986 Art. 6(5) as amended by Law 5113/2024 Art. 5: **€1,300,000 per injured party (bodily injury)** and **€1,300,000 per accident (property damage)**, **indexed by HICP**; held as final country-layer keys `gr.mtpl.min_bi_per_person`, `gr.mtpl.min_pd_per_accident` | OBL-MOT (§3.1); REQ-PFC-085–088; BR-PFC-010, 011; §10.2 |
| MTPL required on every vehicle | Greece pack: MTPL **required and non-removable on every vehicle**; BI limit per injured person and PD limit per accident bound to the finals; MTPL electable → `PFC-LINT-STATUTORY-001` | REQ-PFC-088; REQ-PFC-071 (removal → `PFC-VAL-REMOVAL-BLOCKED`); REQ-PFC-055 example |
| Indexation handling | Final key change → re-lint within 10 min, WRK activity per owner, prefilled regulatory change set; NB window closes day before final takes effect unless compliant successor Locked | REQ-PFC-087; BR-PFC-012, 080; J-03 |
| Directive 2009/103/EC as amended by 2021/2118 | Minimum amounts, periodic review (via Greek transposition) | OBL-MOT; REQ-PFC-085 |
| Auxiliary Fund levy | PD 237/1986 Art. 20(1)–(2) as amended by Law 5113/2024 Art. 14: **ceiling 6% of gross written MTPL premium**, components **4.5% and 1.5%**, burden split insurer/policyholder; **policyholder share bears stamp duty**; computed on policies concluded or renewed per two-month period regardless of collection. Modelled as `GR-AUXF-PH` (billed, levy, beneficiary guarantee fund) + `GR-AUXF-INS` (accrued_not_billed) + `GR-STAMP-AUXF-PH` (tax on the PH share). Values, split, base and refund = MKT pack data, reading recorded once in `REQ-MKT-322` after D2 opinion; guarded golden expectations (`REQ-MKT-055`) | OBL-MOT/OBL-TAX; REQ-PFC-124, 125, 244; BR-PFC-020; C-03 |
| IPT | Law 5177/2025 Art. 43: **20% fire class, 15% other classes, 4% life**; base = premiums **and all rights derived from the contract** (fees in base); exemptions for ship/aircraft (Law 551/1970) and shipping companies (Law 27/1975); **quarterly returns**. No refund-on-cancellation provision found in Art. 43. Rates/exemptions = `TaxCalculator` pack data; PFC holds IPT class per coverage and tax-base flag per charge | OBL-TAX (§3.1 cites REQ-PFC-117, 122, 123, 142); also REQ-PFC-126; BR-PFC-027; C-04, C-05, C-06 |
| Tax/levy treatment on cancellation | Tax and levy charge types carry `PACK_TAX_TREATMENT` → `TaxCalculator.treatment` (source-aware; `DistanceWithdrawal` routed separately); refund net of tax never configured; `gr.ipt.refund_on_cancel` removed (CR-S3-05). Greece defaults until D2 opinion: liability point DUE, withdrawal void routed by cancellation source | REQ-PFC-116; BR-PFC-024 |
| Business nat-cat (P3) | Law 5116/2024 Art. 5 as amended by Law 5162/2024 Art. 25: businesses with annual gross revenue **> €500,000** must insure owned buildings and listed assets (equipment, raw materials, goods, commercial and professional vehicles) against **forest fire, flood, earthquake** for **≥ 70% of asset value**; fines and exclusion from state aid. Bundle taken as a whole; floor `gr.natcat.floor_pct` = 70 per asset class; threshold `gr.natcat.revenue_threshold` €500,000 evaluated by UW | OBL-NATCAT; REQ-PFC-089–092; BR-PFC-013–015 |
| IDD POG | Directive 2016/97 Art. 25; DR 2017/2358 Art. 4–9: approval process, target + negative target market, testing incl. scenarios, monitoring and regular review, distributor info, documentation. Default review 12 months, reminder 30 days, escalation after 7 days; retention ≥ product life + 10 years | REQ-PFC-007, 148–158; BR-PFC-030–034 |
| IPID | Implementing Reg. 2017/1469; IDD Art. 20(5)–(8): nine structured sections per product version and offering, GR+EN; empty section blocks submission | REQ-PFC-159–161; BR-PFC-035 |
| Demands and needs | IDD Art. 20(1): demands-and-needs question set mapping to recommended offerings | REQ-PFC-112 |
| Solvency II LoB | DR 2015/35 Annex I (non-life lines 1–12, NP reinsurance 25–28); MTPL example = LoB 4 | REQ-PFC-005, 140, 145 |
| IFRS 17 | Para. 14 portfolios, endorsed by Reg. 2021/2036 | REQ-PFC-143 |
| Authorised classes | Directive 2009/138/EC Annex I Part A / Law 4364/2016; block coverage in a class the LE is not authorised for (example: class 18 assistance; MTPL class 10) | REQ-PFC-144, 146; BR-PFC-077 |
| Policy content | Law 2496/1997 Art. 2 (minimum policy content; objection right) | OBL-CON; REQ-PFC-159, 067 |
| Equal treatment | CJEU C-236/09 Test-Achats, contract §3.7/R-102: no protected characteristic as rating input; proxies need actuarial justification | OBL-EQT; REQ-PFC-246 |
| DORA change management | Reg. 2022/2554 Art. 9(4)(e); DR 2024/1774 Art. 17: independence of approver from requester/implementer, audit trail, emergency changes, fall-back | REQ-PFC-198, 200–219; §12 |
| AI Act | Reg. 2024/1689 Annex III 5(c) covers life/health pricing only; P&C not listed; amended by Digital Omnibus Reg. 2026/1744 (Annex III obligations ≤ 2 Dec 2027) | OBL-AIA; §11 |
| EAA | Directive 2019/882 applies (R-57); staff screens WCAG 2.2 AA | NFR-PFC-020 |

### 9.2 Referenced but NOT specified (gaps, flagged UNVERIFIED in source)

- Green Card certificate number as mandatory policy field — legal source UNVERIFIED (OI-PFC-10); field still specified as Must (REQ-PFC-062).
- "Greek policies drafted in Greek in principle" — UNVERIFIED (OI-PFC-14); still drives Must REQ-PFC-058.
- Nat-cat vehicle floor (prompt said 100%; not found) — OI-PFC-04.
- Law 4583/2018 POG article numbers — OI-PFC-09.
- IPID article-level requirements (length, symbols, headings) — OI-PFC-08.
- Bank of Greece statistical class list — OI-PFC-13.
- Solvency II consolidated Annex I / Directive 2025/2 taxonomy timing — OI-PFC-12.
- Levy split reading (4.2%/1.8% vs. 70/30 on 4.5% only), stamp-duty rate, base, refund — deferred to D2 opinion (`REQ-MKT-322`).
- IPT reinsurance exemption and liability point — D2 opinion.
- Insurance-specific myDATA document types — confirmed by tax adviser into the Greece pack (OI-PFC-11 closed per D3); codes not given in PRD.
- Cyprus language rule (OI-PFC-14).

### 9.3 Deferred to configuration / country pack
All rates (IPT, levy shares, stamp duty), tax classes, exemptions, refund treatment of taxes, MTPL minima values and dates, nat-cat floor and threshold, myDATA mapping, regime code lists, binding language, regions, activity codes, POG intervals, approval age, emergency review days etc. (§10.2 config key list). Cyprus stub: `cy.stamp.amount` synthetic, effective to 2025-12-31, no line from 2026-01-01 (R-85, `REQ-MKT-265`) — explicitly "test fixture, not law" (BR-PFC-026).

---

## 10. Greek-market specifics

- **AFM:** validated through `IdValidator` scheme AFM (check digit, message key `afm.invalid`) (REQ-PFC-053); PII scanner fails CI if a real AFM pattern appears in golden files (REQ-PFC-236).
- **myDATA:** fiscal-category key per charge type → `FiscalDocumentChannel` mapping (REQ-PFC-121); CMP sole issuer of fiscal series/numbers; TRANSACTION trigger for motor (D3).
- **gov.gr:** not mentioned.
- **Information Centre / bureau:** not used by PFC; Hellenic Motor Insurers' Bureau rules named only as the place to close OI-PFC-10.
- **KAD:** economic activity codes as MKT regime `ACTIVITY_CODE` (REQ-PFC-107, Could, P3).
- **Bank of Greece:** statistical class per coverage (REQ-PFC-144); supervisor receives POG/evidence packs (ROLE-44).
- **Greek language:** Greek and English labels mandatory for GR versions (REQ-PFC-041, `PFC-LINT-I18N-001`); Greek label/help text is the binding master for customer-visible items, requiring ROLE-46 legal approval (REQ-PFC-058, BR-PFC-008); customer-visible vs internal flag (REQ-PFC-059); accent-insensitive search; region picker with Greek geography (region, regional unit, postcode) replacing US "State" (REQ-PFC-108); glossary terms fixed (§6.0); AI-assistance statement in Greek (AI-PFC-06).
- **EUR:** contract currency EUR; other coverage currencies only if enabled by MKT for GR (REQ-PFC-040).
- **Greek motor specifics:** MTPL finals, Green Card field (Greece pack overlay of motor policy line, required at issue for MTPL, REQ-PFC-062), Auxiliary Fund levy charge types, IPT 15% for motor (`GR-OTHER-15`), motor fire `GR-FIRE-20`.
- Time zone: activation in legal entity's time zone (Europe/Athens example).

---

## 11. Controls

**Authority types (§12.2, registered with PLT `REQ-PLT-003`):** `PFC.ProductApproval` (product line, jurisdiction, LE, sign-off role, change class); `PFC.EmergencyChange` (product line, jurisdiction); `PFC.Release` (environment, jurisdiction); `PFC.PogSignOff`; `PFC.Retirement`.

**Required sign-offs by change class (REQ-PFC-203, BR-PFC-073; config `pfc.signoff.matrix`):** product owner always; actuarial for terms/options/charges/rating refs; UW for availability, question sets, UW refs, conversion referrals; compliance for POG, IPID, regulatory mapping, channel changes; legal for customer-visible wording/labels; tax specialist for tax/levy charges; accountant for GL keys and IFRS 17. Change classes: structural, cover-reducing, cover-extending, price-affecting, wording, availability, regulatory, cosmetic (REQ-PFC-201).

**Maker-checker list (§12.3):** version approval and promotion; emergency changes; POG approval and review outcomes; reference-table publication; regulatory mapping changes (compliance checker); charge-type changes (tax or accounting checker); retirement and run-off; fall-back (second release manager or head of product).

**SoD (§12.4):** SOD-PFC-01 author ↔ approver; -02 author/approver ↔ deployer; -03 POG tester ↔ approver of that evidence; -04 `pfc.release` ↔ `pfc.author` in same LE in production (grant-time; exceptions need security officer); -05 emergency declarer ↔ retrospective reviewer. Refusal codes `MAKER-IS-CHECKER`, `SOD-VIOLATION`.

**Audit (§12.1):** ChangeSetCreated/Abandoned, ProductItemEdited (incl. AI interaction id), Rebased/ConflictResolved, VersionSubmitted/Returned/Approved/ApprovalRevoked, SignOffRefused, ReleaseScheduled/Cancelled/Fired/FallBackExecuted, EmergencyChangeDeclared/RetrospectiveReviewCompleted, ArtefactPublished/VerificationFailed, Pog*, RegulatoryAlertRaised, ProductImported, PermissionDenied. Every edit = signed commit attributed to identity-service subject + PLT audit event (REQ-PFC-183, NFR-PFC-014). Main branch protected: approved PRs only, signed commits, no history rewrite, required checks (REQ-PFC-198).

**Evidence pack (REQ-PFC-205):** diff, lint/validation, golden results, impact report, conversion dry-run, POG testing, sign-offs with authority results, artefact hash; hashed, stored in DOC archive; retrievable ≤ 60 s (NFR-PFC-015).

**GDPR:** product definitions P0; every FieldDef must have a PII class P0–P3 (lint `PFC-LINT-PII-001`); golden/test data synthetic only with PII scanner in CI; impact uses aggregates; AI inputs exclude customer data; EU residency of repo, CI runners, artefact store, evidence, AI calls (NFR-PFC-021).

---

## 12. AI features (§11)

Module works fully with all AI off; AI only drafts; acceptance only via explicit human Accept/Edit (`IB-07`) then full lint/golden/sign-off; ship **default off** at tenant level; PLT EU gateway, zero retention; `AiInteractionRecord` for every interaction (REQ-PFC-249); kill switch cancels in-flight calls within 60 s and keeps non-AI drafts (REQ-PFC-248, D4). No AI feature submits, approves, schedules, promotes or retires.

| ID | Name | Classification | Phase | MVP? |
|---|---|---|---|---|
| AI-PFC-01 | Configuration change drafter | Minimal | P2 | No |
| AI-PFC-02 | Diff and impact narrator | Minimal | P1 | Optional ("candidate for MVP enablement"; recommended in §16.5 item 10) |
| AI-PFC-03 | Wording and consistency checker | Minimal | P2 | No |
| AI-PFC-04 | POG monitoring analyst / test-scenario proposer | Minimal with high-risk controls applied | P3 | No |
| AI-PFC-05 | Renewal conversion mapper | Minimal | P3 | No |
| AI-PFC-06 | IPID drafter | Limited (Art. 50); mandatory customer AI-assistance statement (GR/EN text given) | P2 | No |
| AI-PFC-07 | Bilingual label assistant | Limited; legal approval still required | P1 | Optional ("candidate for MVP") |

None is required for MVP; REQ-PFC-248/249 (AI controls) are Must P1 regardless. Monitoring profiles MP-B/MP-C reference PRD-15 §11.2.

---

## 13. Open issues / assumptions / CCRs

**Open issues (§16.1):**

| ID | Status | Subject |
|---|---|---|
| OI-PFC-01 | Closed (R-21) | PFC↔RAT binding: floating at resolution, pinned for the term |
| OI-PFC-02 | Closed per D2 | Levy split/stamp duty/base/refund → `REQ-MKT-322`; legal sign-off is go-live gate |
| OI-PFC-03 | Closed per D2 | IPT cancellation treatment → `TaxCalculator.treatment` |
| OI-PFC-04 | **Open** | Nat-cat floor for business vehicles; private cars in scope |
| OI-PFC-05 | Closed (R-85) | Cyprus stamp duty repealed 2026 |
| OI-PFC-06 | **Open** | Git repo and CI hosting in EU stamp: per-entity vs shared repo |
| OI-PFC-07 | **Open** | Vehicle make/model local tables vs `MotorDataProvider` only |
| OI-PFC-08 | **Open** | IPID regulation article-level requirements |
| OI-PFC-09 | **Open** | Law 4583/2018 POG article numbers |
| OI-PFC-10 | **Open** | Legal source for Green Card number field |
| OI-PFC-11 | Closed per D3 | myDATA |
| OI-PFC-12 | **Open** | SII Annex I consolidation / 2025/2 taxonomy timing |
| OI-PFC-13 | **Open** | BoG statistical class list |
| OI-PFC-14 | **Open** | Greek-language drafting rule; Cyprus language rule |
| OI-PFC-15 | Closed (REG-007) | Digital Omnibus on AI |
| OI-PFC-16 | **Open** | Validate UX effort savings |
| OI-PFC-17 | Partially open | Premium-audit P3: ownership decided (PFC schedules, POL job, BIL bills); content open |
| OI-PFC-18 | Closed | AI statement mandatory on IPIDs |

**Assumptions (§16.2):** ≤ 50 authors, Git invisible to business users; RAT/UW/DOC version their own definitions and publish activation events; Motor MVP = one Greek LE + one EU base, Cyprus CI-only until P4; DAT aggregates available at motor go-live (else impact degrades to POL counts without regional breakdown); quoted values verified 2026-10-07.

**Risks (§16.3):** RK-PFC-01 schema over-generality; -02 (retired); -03 authoring UX rejected; -04 unverified values wrong; -05 conversion gaps → mass referrals; -06 non-deterministic compile; -07 approval bottlenecks.

**CCRs (§16.4), all Accepted in contract v1.11:** CCR-PFC-01 ProductReferenceTable (R-01); -02 `ProductVersionReturned` (R-02); -03 `ProductReferenceTablePublished`, `ProductRegulatoryAlertRaised` (R-02); -04 PFC operational entities (R-01); -05 retention codes (R-10); -06 Locked sub-states (R-11).

**Ten pre-build decisions (§16.5):** (1) pinned/floating model + POL-stored manifest; (2) implement R-21 binding in resolver; (3) closed per D10 (in-house CEL-compatible language); (4) repo topology/hosting (OI-PFC-06); (5) YAML schema v1 scope for motor; (6) sign-off matrix and authority types; (7) emergency policy; (8) legal confirmation of unverified Greek values (D7 certification gate: production activation of each motor-path value requires Settled legal status); (9) reference-table ownership and vehicle data sourcing; (10) AI features at go-live (02 and 07 recommended).

---

## 14. Conflicts and ambiguities

### (a) With infra/stack (ARCHITECTURE-DECISIONS.md)

1. **"Workflow engine" for activation timers.** §1.4 lists "Rules runtime and workflow engine" as PLT (`REQ-PLT-007`); REQ-PFC-211 "the workflow engine locks the version"; J-02 diagram participant "Workflow engine"; SYS-02 "Batch job or workflow-engine workflow"; ReleaseSchedule has `workflow_instance_id`; §9.2 "PLT workflow engine and rules runtime". Stack: **no workflow servers**; use Hangfire with deadlines stored as domain records. Map to: ReleaseSchedule row + Hangfire delayed job; `workflow_instance_id` → Hangfire job id.
2. **Event "topic" and "partition key".** §8: "Topic `pfc.events.v1`; partition key `product_id`"; J-02 participant "Event stream". Stack: no broker; outbox dispatched in order to in-process handlers. Treat topic/partition as logical names (ordering per product_id must be preserved by the outbox dispatcher).
3. **"Lakehouse (DAT)"** in §9.3 external integrations ("Query API on gold models"). Stack explicitly excludes lakehouses; DAT = PostgreSQL reporting schemas.
4. **Git repository and CI hosting.** §9.3: "Git repository service (self-hosted within the EU stamp, operated by PLT)", CI runners "operated by PLT", webhooks to "integration hub"; NFR-PFC-021 requires repo and CI runners in the EU stamp; OI-PFC-06 open. Stack uses GitHub Actions (OIDC to Azure) for app CI/CD; infra does not provide a self-hosted Git service or product-CI runners. Needs a decision (e.g. GitHub with EU data residency / self-hosted runners in Azure EU, or Azure DevOps Repos, or a bare repo managed by the app). Running the compiler/lint/golden pipeline inside the app vs. in external CI is also undecided.
5. **Rules runtime / decision tables (REQ-PFC-065, D10).** In-house CEL-compatible expression language "shared with UW and the PLT rules runtime" plus "decision table evaluated by the PLT rules runtime". Not forbidden, but it is bespoke infrastructure the stack does not name; must not become a BPM/rules server. Large build cost.
6. **"Integration hub" / `plt.Adapter.invoke` (REQ-PLT-006)** for Git and CI adapters — not a stack component; implement as in-process adapters.
7. **Multi-instance cache refresh ≤ 5 s "across all instances of the stamp" (REQ-PFC-229, NFR-PFC-007)** with in-process outbox dispatch only: a modular monolith on several Container Apps replicas needs a cross-instance signal (e.g. PostgreSQL LISTEN/NOTIFY or polling). Not addressed by the stack.
8. **Content-addressed write-once runtime store "replicated within the deployment stamp"** (REQ-PFC-197) — not specified; likely Blob Storage with immutability policy, or a PostgreSQL table with no UPDATE/DELETE grants. Decision needed.
9. **Dependencies implied:** JSON Schema draft 2020-12 validator, RFC 8785 canonicaliser, YAML parser, Git library, CEL-like parser — each needs a stated reason under rule 11 (minimal dependencies).
10. Vendor names (Guidewire Product Designer, Socotra, BriteCore) appear only as baseline/vendor practice references, not as dependencies — no conflict.

### (b) With the system contract / other PRDs (as noticed)

- C-03: PRD-02 v1.0 read the levy 70/30 burden on the 4.5% component only; PRD-06 and PRD-09 read it as on the whole 6% (4.2%/1.8%). Settled structurally by D2 but numerical value still pending `REQ-MKT-322`.
- C-10: prompt had PFC owning forms/clauses; contract CD-09 gives them to DOC.
- C-11: prompt status set lacked Submitted; contract adds it.
- C-12: product-scoped lookups had no owner → CCR-PFC-01 (accepted).
- §1.2 summary says the hash "is written onto every rated transaction", while REQ-PFC-221 says POL stores the manifest on the **term** and in-term transactions reuse it. Likely consistent (term-level), but wording differs.
- `ProductVersionPublished` consumer list includes MKT; no MKT reaction is described in this PRD.

### (c) Internal contradictions / ambiguities

1. **Are windows inside the artefact hash?** Source YAML includes `windows` (§7.5) and the `product` part of the artefact is hashed (§7.6). Yet REQ-PFC-033 changes a Locked predecessor's NB end "in the same transaction" on publication, REQ-PFC-178/BR-PFC-012 close NB windows on Locked versions, and §7.1 allows "Locked rows immutable except windows closure and sub-state". Meanwhile REQ-PFC-213 fall-back creates "3.3 (copy of 3.2 with new windows)" — implying windows are version content. Need to decide: windows held only on the operational ProductVersion row (outside the hash) vs. in the artefact.
2. **Locked sub-state change emits `ProductVersionScheduled`** (state table) — overloading a scheduling event for window changes; consumers must handle it.
3. **Approved → Draft (revoke)** emits `ProductVersionReturned` in the table but the state diagram shows no event; REQ-PFC-009 GWT is consistent with table.
4. **Configuration hash semantics:** manifest stores `finalsUsed` with values, effective intervals and `configurationHash` at compile time. If MKT changes a final later (indexation), the Locked artefact retains the old snapshot; re-lint is event-driven (REQ-PFC-087). The nightly rebuild (REQ-PFC-196) must use the manifest's recorded config, not current config, or hashes will drift — the PRD implies but does not spell this out.
5. **Resolution with transactionType** — §9.1 lists only NewBusiness and Renewal; REQ-PFC-001 says "transaction type"; REQ-PFC-168 forbids resolution for changes/cancellations/reinstatements. Consistent, but describe/availability accept more transaction types (rewrite, reinstatement).
6. REQ-PFC-062 (Green Card field) and REQ-PFC-058 (Greek binding) are Must P1 while their legal basis is UNVERIFIED (OI-PFC-10, OI-PFC-14).
7. Event naming mix: RAT event names `RatingArtifactPublished`, `RateVersionActivated`, `RateVersionScheduled`, `RateVersionWithdrawn` all consumed — check against PRD-03 event catalogue.
8. NFR numbering out of order (NFR-PFC-021 listed before 020) — cosmetic.
9. §6.0 says abstract base "Extends" field references an "abstract base Locked version", while REQ-PFC-043 also allows extending non-abstract versions via overlays (country/channel overlays). Whether a channel overlay must extend an abstract base or a concrete country version is not explicit (§7.5 example: GR country overlay extends abstract MOTOR-EU).

### (d) Cannot build without a decision

- Repository/CI hosting and where the compile/lint/golden pipeline runs (OI-PFC-06; §16.5 item 4).
- YAML schema v1 scope for motor (§16.5 item 5).
- Expression language implementation (D10 closed in principle — in-house CEL-compatible — but grammar, type system and the decision-table format are unspecified here).
- Artefact store technology and cross-instance cache invalidation.
- Levy and tax values (D2 opinion, `REQ-MKT-322`) — golden expectations remain "guarded".
- Bank channel in P1 depends on a partner being signed by G0 (D6).
- Windows-in-hash question (14c.1).

---

## 15. Build notes

**Hardest parts:**
1. **Compiler + hashing**: layered flattening (base/country/channel) with finals enforcement, provenance per attribute, RFC 8785 canonical JSON with decimals-as-strings, byte-for-byte determinism across machines, nightly rebuild verification.
2. **Expression language** (typed, side-effect-free, CEL-compatible, in-house, compiled to typed AST; non-determinism lint) shared with UW and PLT.
3. **Git-backed authoring hidden behind screens**: every edit a signed commit, change sets as branches, item-level three-way merge with UI conflict resolution, PR-based approval, protected main.
4. **Resolver**: in-memory index, term-start window resolution, floating reference resolution from read models, resolution manifest + hash, multi-instance freshness ≤ 5 s, p99 < 1 ms.
5. **Version conversion at renewal**: completeness lint, value-mapping functions, grandfathering, cover-reduction referrals, dry-run over in-force population (needs DAT/POL), ≥ 50 conversions/s.
6. **Validation engine**: three levels (field, element, policy), severities × checkpoints, partial drafts, structured bilingual errors, p95 ≤ 100 ms.
7. Lint catalogue (26 rules) spanning MKT, DOC, BIL, RAT, UW data.

**What must exist first:** PLT (identity/roles, SoD, authority, maker-checker, audit, outbox, Hangfire-based timers, translation store), MKT (configuration model with final keys + configuration hash, regime code lists, SPI bindings incl. TaxCalculator stub, shared Channel and Cancellation-source code lists), and the expression-language core. RAT rating-slot compatibility and DOC form-pattern/clause catalogues are needed for reference lint, but can be stubbed via read models.

**Suggested slicing:**
1. Schema v1 (motor) + YAML loader + JSON Schema validation + compiler (no overlays) + canonical hash + artefact store + `Artifact.get` / `Catalogue.get` / `ChargeType.list`.
2. Version lifecycle (Draft→Locked) with Hangfire activation, windows, resolver (in-memory) + `ProductVersionPublished` via outbox.
3. Expression language + field/element/policy validation + `PolicyDraft.validate` + `Product.describe` + `QuestionSet.evaluate`.
4. Finals binding + MKT event-driven re-lint; Greece MTPL lint; charge types incl. levy/stamp and `writtenPremium`; regulatory mapping lint.
5. Overlays/inheritance + Cyprus stub variant in CI.
6. Authoring UI over change sets (Git), diff, sign-off matrix, SoD, evidence pack, promotion.
7. Availability/grandfathering, offerings matrix, conversion engine + dry-run, retirement.
8. POG + IPID data; then AI-PFC-02/07 if enabled.
