# Digest — PRD-17 Multi-market framework and country packs (MKT)

Source: `core-insurance-prds/PRD-17-multi-market-country-packs.md` (v1.3, 2026-10-07, 2,762 lines, read in full). Binding input named by the PRD: `00-system-contract.md` v1.11 (CD-06, CD-07, CD-08, CD-17, CD-20, §3.5.8, decisions D1–D10, rulings R-101, R-102). Checked against `core-insurance-infra/ARCHITECTURE-DECISIONS.md`.

---

## 1. Identity

- **Module code / title:** MKT — Multi-market framework and country packs. Schema `mkt` (§7). Wave 1 (with PLT, PTY, PFC, WRK); build waves W1 (framework) and W8–W9 (cross-border, migration baseline) per PRD-18 §16.3 (§1.1).
- **Owners:** business ROLE-42 (market-entry lead / country product owner); technical: design authority (specialises ROLE-38); operational: configuration engineer ROLE-15 (§1.1).
- **Purpose (§1.2):** the framework that lets one codebase serve Greece first and then other EU markets without forking. MKT itself does **not** compute Greek tax or Cypriot levies. It owns:
  - the six-layer effective-dated configuration model, the resolver and the configuration hash;
  - the typed SPI catalogue (40 SPIs) and SPI binding;
  - the pack lifecycle: build, sign, certify, activate, hot-fix, rollback, deprecate;
  - capability switches;
  - tenancy: one stamp per regulated legal entity, with partitions inside each stamp;
  - i18n: four locale settings, the translation store, the Greek/English UI switch (R-101) and the translation-completeness gate;
  - currency roles and rounding;
  - regime code lists;
  - cross-border (FoS/FoE) pack selection;
  - governance: golden suite, architecture tests, fork ratio and readiness scorecard;
  - the reference packs (`eu`, `group`, `gr`, `cy-stub`, `bg-fixture`) and the market-entry playbook.
- **Five key decisions (§1.2):**
  1. Pack **code** ships with the release, while pack **data** activates at runtime.
  2. The configuration hash is a content address of the whole configuration state.
  3. Every SPI declares a binding axis.
  4. Tenancy is silo per regulated entity, pool inside a stamp, and bridge for group services.
  5. The Cyprus stub and the architecture tests are release gates from day one.
- **Non-goals (§1.4):**
  - Business behaviour inside other modules.
  - The **content** of tax rates, levy bases, fiscal schemas and statutory forms. That content is the pack specification, owned by ROLE-26 and the country regulatory analyst; MKT only indexes it.
  - Runtime hosting of the configuration service and technical feature flags (PLT, REQ-PLT-008).
  - Holiday, calendar, currency and FX stores (PLT, REQ-PLT-009).
  - Clock definitions, instances and the dashboard (CMP, REQ-CMP-003).
  - Coverage-to-regime assignment (PFC, REQ-PFC-005).
  - Taxonomy transforms and report generation (DAT, REQ-DAT-003).
  - Stamp infrastructure provisioning (PLT).
  - The numbering runtime (PLT, REQ-PLT-014).
  - Document rendering and template authoring (DOC).
  - Translation of legal contract wording (DOC). MKT owns UI and message translation only.

## 2. Size metrics

From the self-check (§16.6), confirmed by spot checks:

| Metric | Count |
|---|---|
| Functional requirements | **324**: REQ-MKT-001…010 (10 anchors) and 030…343 (314). No gaps. |
| MoSCoW | **274 Must / 43 Should / 7 Could / 0 Won't** |
| Phase of Must requirements | **261 Must P1 (motor MVP)**, 13 Must P4. The P4 Musts are REQ-MKT-155, 157, 158, 159, 161, 163, 201, 202, 203, 215, 218, 222 and 224 (verified). Should and Could are spread across P1, P3 and P4. |
| Tags | 124 [BASELINE] / 200 [ENHANCEMENT] |
| Business rules | 46 (BR-MKT-001…046) |
| NFRs | 26 (NFR-MKT-001…026) |
| Screens | 12 (SCR-MKT-01…12), all staff realm. 0 owned inventory screens; 9 consumed inventory items. |
| Owned entities | About 22 (§7.1, listed below) |
| Events | 10 produced; 6 consumed |
| Inbound API operation groups | About 30 (§9.1) |
| SPIs | 40 in the catalogue (§9.4.0) |
| AI features | 5 (AI-MKT-01…05), all default off |
| Architecture tests | 11 (ARCH-01…11) |
| Golden scenarios | 50 (GS-01…50) |
| Greece pack index rows | 32 (GR-01…32) |
| Risks | 11 (RK-MKT-01…11) |
| Open issues | 19 (OI-MKT-01…19): 5 closed, 2 partly closed |
| CCRs | 11 (CCR-MKT-01…11), all accepted or resolved |

**Build-size estimate: L (very large).** There are 261 Must P1 requirements. The module also holds temporal and hash logic: bitemporal config values, a Merkle-style content-addressed state, deterministic resolution with hash pinning, and reproduction of past results. Beyond that it owns 40 SPI contracts with conformance kits, the pack signing and activation lifecycle, a cross-module golden-suite harness, the translation store and its release gate, and the D2 tax-treatment rule package. It is foundational: almost every module calls REQ-MKT-001, 002 or 005.

## 3. Owned entities (§7.1)

Common rules for all rows:
- Every row carries `legal_entity_id` (or `group` scope), `jurisdiction`, `created_at`, `created_by` and `record_version`.
- Rows with business validity also carry `valid_from` and `valid_to`.
- IDs are UUIDv7.
- Retention class `RC-CFG` means retained while any fact references the hash, plus 10 years. `RC-OPS` means 24 months.
- Configuration and pack data hold no personal data except maker and checker user IDs.

| Entity | Key attributes | State machine / notes |
|---|---|---|
| **ConfigKey** (registry) | `key` (`<mod>.<path>`, unique); `owning_module`; `value_type` (REQ-MKT-035); `core_default` (required unless `pack_required`); `allowed_layers` (L0–L5); `final_at`; `merge_type` (REPLACE, ADDITIVE_LIST, MAP_DEEP, CONSTRAINED_RANGE); `resolution_axis` (default LEGAL_ENTITY_HOME); `time_basis`; `validation` (JSON schema); `unit`; flags `sensitive`, `retro_allowed`, `pack_required`, `allows_removal`; `description_en` and `description_el`; `descriptor_version` | Registered from code-declared descriptors (REQ-MKT-034) |
| **ConfigValue** | `config_value_id` (= value version id); `config_key_id`; `layer`; `layer_node` (e.g. `country:GR`); `value` (JSON); `removal_markers`; `final`; `valid_from` and `valid_to` (no overlap per key and node); `source` (CHANGE_REQUEST, PACK, IMPORT, MIGRATION); `change_request_id` or `pack_version_id`; `superseded_at` (record-time end) | Immutable once activated. A correction is a new version. Bitemporal: business validity plus record time. |
| **ConfigChangeRequest** (+ ConfigChange rows) | `number` (via `NumberingScheme`); `title`, `reason`, `legal_source_ref`; `scope`; flags `hotfix` and `retroactive`; `status`; `requested_activation_at` (UTC); maker, checkers and design-authority user IDs (maker not among checkers); `approval_request_id` (PLT); `preview_run_id` (required before submit); `base_hash`, `resulting_hash`; `changes[]` (key, layer_node, operation Add / Close / Remove-member, value, validity) | §7.3.1 (below) |
| **ConfigState / ConfigHash** | `configuration_hash` char(64) PK (SHA-256 of the canonical manifest); `stamp_id`; `parent_hash` (null only for genesis); `manifest` (node sub-manifest digests, pack versions, binding-table digest, switches, code-list versions, translation bundle version, CLDR version, group bundle version); `activated_at`; `cause` (CONFIG_CHANGE, PACK_ACTIVATION, PACK_ROLLBACK, GROUP_BUNDLE, BUNDLE_PUBLISH); `cause_ref` | Append-only hash chain. Never purged while referenced (REQ-MKT-048). |
| **Pack / PackVersion** | `pack_id` (`eu`, `group`, `gr`, `cy-stub`, `bg-fixture`); `scope` (REGION, GROUP, COUNTRY); `owners`; `version` (semver); `core_range`; `spi_versions` map; `dependencies`; `content_inventory` (SHA-256 per item); `test_only`; `signature_ref`, `provenance_ref`; `status`; `certification_evidence`; `rule_index` | §7.3.2: Built → Signed → Certified or Rejected → Published → Deprecated → Removed |
| **PackActivation** † | `legal_entity_id`; `pack_version_id`; `activation_at`; `kind` (ACTIVATE, ROLLBACK); `supersedes_activation_id`; `status`; `approval_request_id`; `resulting_hash` | Requested → PendingApproval → Scheduled → Active → Superseded. PendingApproval → Withdrawn. Scheduled → Withdrawn. |
| **SpiBinding** | `legal_entity_id`; `spi`; `axis_value` (e.g. `CY`, `GR-I`, scheme `AFM`); `qualifier`; `valid_from` and `valid_to`, unique per (entity, spi, axis, qualifier, instant); `implementation_id`; `pack_version_id`; `suspended_until` | Versioned in the configuration state |
| **CapabilitySwitch** (catalogue) | `switch_key` (`cap.*`); `reversibility` (REVERSIBLE, REVERSIBLE_WITH_MIGRATION, IRREVERSIBLE); `scopes`; `requires` and `excludes` (acyclic); `default`; `design_authority_decision_ref` | Values are ConfigValues of type capability. §7.3.3: Off → PendingOn → On → PendingOff → Off, except that IRREVERSIBLE On is terminal (`locked`). |
| **TranslationEntry** | `message_key`; `language` (BCP 47); `text` (ICU MessageFormat, no personal data); `context`, `max_length`, `customer_facing`; `glossary_terms`; `status`; translator, reviewer and `ai_interaction_id`; `bundle_version`; `surface_type`; `owning_module`, `release_scope`; `terminology_check` (PASS, WARNING, FORBIDDEN) | §7.3.4: Missing → Draft → InReview → (back to Draft, or) Approved → Published. Published or Approved → Stale on a source change. Stale → Draft. |
| **TerminologyEntry** † | `english_term`; `approved_greek`; `forbidden_renderings` (with context); `owning_module`; `glossary_source`; status (Draft, Approved, Retired); approvers (checker = legal reviewer); validity | — |
| **TranslationGateRun** † | `release_candidate`; `keys_in_scope`; `result`; `failures` (key, language, reason MISSING / STALE / PLACEHOLDER / LENGTH); `bundle_versions`; `evidence_ref` | — |
| **TaxTreatmentRule** † (pack content) | `rule_id`, `rule_version`; `jurisdiction`; `charge_category` and types; `transaction_kind`; `cancellation_source`; `action`; `customer_credit`; `authority_liability`; `fiscal_document`; `legal_status`; `legal_source_ref`; `owner_role`; `guarded_scenarios`; validity (basis TAX_POINT_DATE) | — |
| **RegimeCodeList** (+ RegimeCode, Crosswalk) | `regime`; `taxonomy_version`; `pack_id`; `status`; `codes[]` (code, `label_en`, `label_el`, parent, validity, `source_ref`); `crosswalks[]` | §7.3.7: Draft → Published → Retired (`RegimeCodeListPublished`, with `retired = true` on retirement) |
| **StatutoryClockValue** † | `clock_code` (must exist in the CMP register); `jurisdiction`; `duration`, `unit`; `calendar_id` (must exist in the PLT store); `start_rule`, `stop_rule`, `pause_rules`, `warning_thresholds`, `extension_rules` (JSON); `applies_to_running`; `legal_source`, `verified_on`; validity | — |
| **LegalEntity** † | Native and Latin legal names; LEI; `home_jurisdiction`; `supervisor`; `licence_classes`; `functional_currency`; `timezone`; `default_languages`; `stamp_id`; `status` | §7.3.5: Planned → Onboarding → Active → RunOff → Closed. Guards to Active: LEI present, packs certified and activated, checklist complete, maker-checker. Emits `LegalEntityStatusChanged`. |
| **Stamp** † | `region`, `environment`, hosted entity, `release_version`, active hash | — |
| **Partition** † | `stamp_id`; `type` (BRAND, CHANNEL, BANCA_PARTNER, BRANCH); `name`; `jurisdiction`; layer node | — |
| **CrossBorderAuthorisation** † | Entity; `host_state`; `basis` (FOS, FOE); `classes`; notification, effective and end dates; host supervisor; branch partition; claims-representative and tax-representative party refs (PTY IDs); memberships; evidence | — |
| **Market** † | `country`; `entry_basis`; target lines; owner; checklist items | §7.3.6: Candidate → Planned → InBuild → Ready → Live → Exiting → Closed |
| **GoldenSuiteRun** † | `mode` (BUILD, PREVIEW, CERTIFICATION); pack versions; per scenario × pack results, digests, divergences, invariants | Synthetic data only. RC-OPS (24 months). |
| **ChangeoverPlanView** (read model of the MIG plan) | `plan_id`, currencies, `changeover_at`, MIG status, bound MKT keys | MKT does not own the plan (R-08, R-82; §7.3.8) |
| **SpiCallLog** † | SPI, implementation, pack version, binding id, `input_digest`, outcome, latency, correlation id, caller | Digests only, never raw P2/P3 data |
| **ArchitectureException** † | Rule ARCH-nn, scope, justification, approver, expiry (90 days or less) | — |

† raised by CCR-MKT-11 and accepted under R-01. LegalEntity, Market and CrossBorderAuthorisation are visible across modules; the other † entities are private to MKT.

**ConfigChangeRequest state machine (§7.3.1):**

| From | To | Guard |
|---|---|---|
| Draft | Submitted | All rows valid, preview completed, no unaccepted guarded expectation |
| Submitted | Approved | Maker ≠ checker, `authority.check` passes, design-authority co-sign where required |
| Submitted | Rejected | Checker rejects |
| Submitted or Approved | Conflict | Another request activates on an overlapping key, node and period |
| Conflict | Draft | Rebase |
| Approved | Scheduled | Activation instant set |
| Scheduled | Approved | Withdraw the schedule |
| Scheduled | Active | Instant reached and integrity checks pass; emits `ConfigurationActivated` |
| Draft, Submitted or Approved | Withdrawn | Maker withdraws |
| Active | Superseded | A later request replaces all its values |

## 4. Consumed entities / dependencies (§7.2, §9.2, §15.1)

| From | What MKT uses | How |
|---|---|---|
| PLT | ApprovalRequest: maker-checker for change requests, activations, rollbacks, code lists, switches and legal-status changes | REQ-PLT-004 (`plt.Approval.request/verifyForExecution`, `plt.ApprovalType.register`) |
| PLT | AuthorityType / AuthorityProfile: the MKT_* authority types | REQ-PLT-003 |
| PLT | AuditEvent | REQ-PLT-002 |
| PLT | Configuration runtime and feature flags. PLT **implements** `mkt.Configuration.resolve` and `explain` (REQ-MKT-001). | REQ-PLT-008 |
| PLT | Outbox / events | REQ-PLT-005 |
| PLT | Integration hub hosting the external-call SPIs | REQ-PLT-006 |
| PLT | "Workflow engine" for activation timers and changeover orchestration | REQ-PLT-007 |
| PLT | Calendar, holiday, ISO 4217 and FX stores | REQ-PLT-009 |
| PLT | AI control plane (`plt.Ai.invoke`) | REQ-PLT-010 |
| PLT | Retention | REQ-PLT-011 |
| PLT | Incidents and the DORA register | REQ-PLT-012 |
| PLT | Observability | REQ-PLT-013 |
| PLT | Numbering | REQ-PLT-014 |
| PLT | Identity profile (holds the UI language) | REQ-PLT-001, REQ-PLT-015 |
| PLT | Build pipeline | REQ-PLT-265 |
| PLT | Time service `plt.Time.now`, never the system clock (REQ-MKT-324) | REQ-PLT-332 |
| PFC | ProductVersion for product context (`pfc.ProductVersion.resolve`); RegulatoryMapping validated by MKT | REQ-PFC-001, REQ-PFC-005 |
| CMP | StatutoryClockDef (`cmp.ClockDef.list`) for clock completeness | REQ-CMP-003 |
| CMP | AiSystem register; rule-index export target | REQ-CMP-007, REQ-CMP-006 |
| RAT | `rat.Rate.rateBatch`, `rat.ShadowRun.start`, `rat.ImpactAnalysis.run` for the impact preview re-rating sample | REQ-RAT-002, REQ-RAT-007 |
| PTY | Party refs (claims and tax representatives); customer communication preferences | REQ-PTY-001, REQ-PTY-005 |
| WRK | Activities, palette registration, staff notifications | REQ-WRK-001, -006, -007 |
| DOC | Archive (pack artefact archive copies, evidence) | REQ-DOC-004 |
| DAT | Model registry for AI-MKT models | REQ-DAT-005 |
| MIG | ChangeoverPlan (read model); module `convertCurrency(planId)` | REQ-MIG-001 |
| CHN | Permission-matrix version included in the hash | REQ-CHN-002 (REQ-MKT-323) |
| CHN | Channel code list owner | REQ-CHN-317 |

## 5. Events (§8)

Transport stated by the PRD: topic `mkt.events.v1`. The partition key is `stamp_id` for configuration-state events and `pack_id` for pack-lifecycle events. The envelope follows contract §3.4.1. For activation events, `configuration_hash` is the hash **after** the change. Version 1.3 adds no new events.

**Produced**

| Event | Trigger | Key payload | Consumers |
|---|---|---|---|
| ConfigurationActivated | Activation of a change request, capability switch change, group bundle or translation bundle | Previous and new hash; kind (config, capability, group_bundle, translation_bundle, clock); changed keys with nodes and validity; retro window (`retro_from`, `retro_to`); `applies_to_running` flags; CR id | PTY, PFC, RAT, POL, FIN, CMP, WRK, PLT, DAT |
| PackActivated | An ACTIVATE activation reaches its instant | Pack, version, entity, instant, previous version, new hash, SPIs whose bindings changed | PTY, PFC, RAT, POL, BIL, CLM, RI, FIN, DOC, CMP, CHN, WRK, PLT, DAT |
| PackRolledBack | A ROLLBACK activation reaches its instant | From and to versions; affected window [from, to); hashes issued in the window; reason | PTY, PFC, RAT, POL, BIL, FIN, CMP, WRK, PLT, DAT. Each module owns its remediation (e.g. POL reverse-and-reapply). |
| RateTableChanged | A pack-supplied rate table is activated | Table code, version, effective date, new hash | RAT, BIL, PLT, DAT |
| PackDeprecated (R-02) | Pack version deprecated | Pack, version, earliest removal date | DAT |
| RegimeCodeListPublished (R-02) | Code list published or retired | Regime, taxonomy version, effective date, `retired` flag | PFC, RI, FIN, DAT |
| TranslationBundlePublished (R-02) | Bundle published | Bundle version, languages, digest, UI scopes | DOC, CHN, DAT |
| GoldenSuiteCompleted (R-02) | Run completes (REQ-MKT-252, Could) | Run id, mode, pack versions, pass/fail counts, divergences | DAT |
| LegalEntityStatusChanged (R-02) | Entity status transition | Entity, old and new status, effective date | DAT |
| CrossBorderAuthorisationChanged (R-02) | Authorisation created, amended or ended | Entity, host State, basis, classes, dates | PFC, DAT |

Ruling R-03 separates two events:
- `ConfigurationActivated` (MKT) is the governance fact that a configuration version was approved and activated.
- PLT `ConfigChanged` says the runtime value set changed. Caching modules subscribe to `ConfigChanged`, and MKT uses it to confirm REQ-MKT-050.

**Consumed**

| Event | Producer | Reaction |
|---|---|---|
| ConfigChanged | PLT | Closes the activation; alert if not received within 60 s |
| FeatureFlagChanged | PLT | Architecture monitor checks flag keys for country qualifiers (REQ-MKT-076) |
| ProductVersionPublished / ProductVersionRetired | PFC | Refresh product context; re-run the regime completeness check |
| AiToggleChanged / AiKillSwitchActivated | PLT | Disable AI-MKT surfaces |
| ModelDriftDetected / BiasThresholdBreached | DAT | Flag AI-MKT features |
| IncidentDeclared | PLT | Show the SPI browser as `offline/degraded` |

All consumed events are idempotent on `event_id`.

## 6. APIs (§9.1, §9.2)

All commands require an `Idempotency-Key`. Commands that change configuration or bindings offer dry-run. Errors are RFC 9457 with codes `MKT-ERR-*`. Every query accepts the UI language and returns bilingual labels (REQ-MKT-337).

**Exposed**

| Operation | Purpose / notes |
|---|---|
| `mkt.Configuration.resolve` (REQ-MKT-001, 044) | The **only** public resolve operation in the programme (implemented by the PLT runtime). Inputs: entity, jurisdiction (+ subdivision), product and version, channel, dates per time basis, optional `configurationHash` or `knownAt`, keys or namespace. Output per key: value, source node, value version id, validity, final flag, merge trace; plus the hash. |
| `mkt.Configuration.currentHash` (047) | — |
| `mkt.Configuration.explain` (065) | Lineage |
| `mkt.Configuration.compare` (064) | Diff of two hashes |
| `mkt.Configuration.export` (063) | Signed canonical JSON |
| `mkt.ConfigChangeRequest.create/update/submit/approve/reject/schedule/withdraw/rebase/preview/import` (052–062) | Change workflow |
| `mkt.Spi.catalogue` (081) | — |
| `mkt.Spi.bind` (083) | In-process only. Inputs: entity, SPI, axis value, qualifier, date, hash. Output: implementation handle, pack version, binding id. |
| `mkt.Spi.suspend` (124) | Emergency suspension |
| `mkt.Pack.list/get/certify/publish/scheduleActivation/rollback/deprecate` (144) | Dry-run on schedule and rollback |
| `mkt.Capability.get` (071) | — |
| `mkt.L10n.bundle` / `format` (189, 336) | — |
| `mkt.L10n.searchKeys(text, language)` (178, 340) | — |
| `mkt.L10n.caseMap(text, language, mode)` (340) | — |
| `mkt.L10n.uiLanguage.resolve(context) → {language, source, allowedLanguages}` (333) | — |
| `mkt.Translation.gate` (335) | — |
| `mkt.Translation.*` (171–173, 186) | — |
| `mkt.Terminology.list/propose/approve` (342) | — |
| `TaxCalculator.calculate / treatment` (087, 330) | Through `mkt.Spi.bind`, in-process |
| `mkt.Rounding.apply(amount, currency, purpose, context) → {amount, ruleId, residual}` (195) | — |
| `mkt.Currency.rule` (190, 196) | — |
| `mkt.Currency.convertFixed` (199) | — |
| `mkt.Changeover.configuration` (198, 200) | — |
| `mkt.RegimeCode.list/get/validate/crosswalk` (208) | — |
| `mkt.RiskLocation.resolve(riskType, attributes, policyholder)` (216) | — |
| `mkt.CrossBorder.check(entity, hostState, class, date)` / `basis` (221, 222) | — |
| `mkt.StatutoryClockSet.get(clockCode, jurisdiction, date)` / `list(jurisdiction, date)` (291) | — |
| `mkt.LegalEntity.*`, `mkt.Partition.*`, `mkt.CrossBorderAuthorisation.*`, `mkt.Market.*` | Registries |

**Consumed:** see §4 above (PLT, RAT, PFC, CMP, WRK, DOC and DAT operations, plus MIG `convertCurrency(planId)`).

**Error codes (§9.5):**
- `MKT-ERR-CFG-FINAL`, `-RANGE-WIDEN`, `-KEY-UNKNOWN`, `-KEY-INVALID`, `-TIMEBASIS-MISSING`, `-L10N-MISSING`, `-SECRET`, `-HASH-UNKNOWN`, `-CODE-UNKNOWN`
- `MKT-ERR-CAP-IRREVERSIBLE`, `-DA-REQUIRED`
- `MKT-ERR-SPI-UNBOUND` and `MKT-ERR-SPI-VALIDATION / NOT_APPLICABLE / RULE_MISSING / UNAVAILABLE / TIMEOUT / CONTRACT_VIOLATION / FORMAT`. Only UNAVAILABLE and TIMEOUT are retryable.
- `MKT-ERR-PACK-DIGEST / INCOMPATIBLE / DEPENDENCY / TEST-ONLY / SIGNATURE`
- `MKT-ERR-REG-CODE-RETIRED`
- `MKT-ERR-XB-NOT-AUTHORISED`, `-XB-ENDED`
- `MKT-ERR-ENTITY-RUNOFF`
- `MKT-ERR-IDEMPOTENCY-MISMATCH`

---

## 7. SPIs / country-pack interfaces — the core of this PRD

### 7.1 Configuration model (MKT-C01, REQ-MKT-001, 030–069)

**Six layers, lowest to highest specificity (REQ-MKT-030, BR-MKT-001):**

| Layer | Name |
|---|---|
| L0 | core |
| L1 | group |
| L2 | region:EU |
| L3 | country |
| L4 | legal entity |
| L5 | product / channel |

Layer nodes (REQ-MKT-031): `core`, `group:FFH`, `region:EU`, `country:GR`, `entity:<id>`, `product:<code>`, `channel:<code>`, `product:<code>+channel:<code>`. Inside L5, product+channel beats product, which beats channel (REQ-MKT-032). Example: down payment 20 at product, 10 at channel BANK_BRANCH and 15 at product+channel resolves to 15. HOME with BANK_BRANCH resolves to 10.

**Merge semantics:**

| Merge type | Rule | Req. / priority |
|---|---|---|
| REPLACE | The most specific non-final value wins | REQ-MKT-036, Must |
| ADDITIVE_LIST | Union across layers, ordered L0 first then declared order, no duplicates. Example: `pty.id_schemes` gives [PASSPORT, VAT_EU, AFM, GEMI]. | REQ-MKT-037, Must |
| ADDITIVE_LIST removal | Only for keys flagged `allows_removal`, with an explicit removal marker; shown in explain | REQ-MKT-038, Should; BR-MKT-004 |
| MAP_DEEP | Recursive per-member REPLACE. {a:1,b:2} + {b:3,c:4} gives {a:1,b:3,c:4}. | REQ-MKT-039, Should |
| CONSTRAINED_RANGE | A lower layer may only narrow a range (min' ≥ min, max' ≤ max); widening fails with `MKT-ERR-CFG-RANGE-WIDEN` | REQ-MKT-040, BR-MKT-003 |

**Final values (REQ-MKT-041, BR-MKT-002):**
- A value marked final at node N blocks all lower nodes for overlapping periods.
- A key-level `final_at` fixes the only layer that may hold a value. Example: `fin.functional_currency` final at L4.
- A violation is rejected at draft with `MKT-ERR-CFG-FINAL`.

**Value types (REQ-MKT-035):** boolean, integer, decimal, percentage, money (currency required), duration (ISO 8601 only, e.g. `P30D`), date, enumeration, string, localised string, list, map, table reference, SPI reference, code-list reference, capability. `secret_ref` exists, and secrets themselves are rejected (REQ-MKT-067).

**Effective dating:**
- Values have `[valid_from, valid_to)` business validity plus record-time versioning. Periods may not overlap; the system proposes closing the earlier one (REQ-MKT-042).
- Each key declares one **time basis**: EFFECTIVE_DATE, TERM_START_DATE, TAX_POINT_DATE, EVENT_DATE or PROCESSING_DATE. A missing date gives `MKT-ERR-CFG-TIMEBASIS-MISSING`, and PROCESSING_DATE uses the business date in the entity timezone (REQ-MKT-043).

**Key registry (REQ-MKT-033, 034, ARCH-08):**
- Keys are registered from code-declared descriptors of the owning module.
- The build fails when code reads an unregistered key or registers a key in another module's namespace.
- Every key needs a core default unless it is `pack_required`. Pack certification fails if a `pack_required` key is missing (REQ-MKT-069).

**Resolver:**
- Deterministic and byte-identical across nodes and cache states (REQ-MKT-045).
- In-process cache per stamp node, invalidated atomically on activation (REQ-MKT-050). No request starting at T + 1 s or later may see the old hash.
- **Hash pinning** per unit of work (command, rating call, batch item): a command started under H1 completes under H1 (REQ-MKT-051, BR-MKT-009).
- If the PLT runtime is unavailable, it degrades to the last-known state in memory, **never to defaults** (NFR-MKT-011).

**Configuration hash (REQ-MKT-046–049):**
- Computed as **SHA-256 over the RFC 8785 canonical JSON** of the stamp's state manifest.
- The manifest covers:
  - every value version with its period, across all layer nodes;
  - SPI bindings;
  - capability switches;
  - pack versions with content digests;
  - regime code-list versions;
  - the translation bundle version;
  - the CLDR version;
  - the group bundle version (REQ-MKT-163);
  - the CHN permission-matrix version (REQ-MKT-323).
- States differing only in one `valid_to` give different hashes. The same state built in a different order gives the same hash.
- The state is a tree of per-node sub-manifests (Should, REQ-MKT-049): one change re-hashes one node plus the root in 200 ms or less over 20,000 values, and the diff is O(changed nodes).
- The hash is stamped on quotes, policy transactions, invoices, claim payments, journal entries, report runs and **every event envelope** (`configuration_hash`) (REQ-MKT-047, BR-MKT-010).
- Reproduction is `resolve(hash, context, date)`. Any past hash can be rebuilt, and manifests are never purged while referenced (REQ-MKT-048).
- A daily integrity check recomputes hashes. A mismatch is a security incident (NFR-MKT-013).

**Change workflow (REQ-MKT-052–066):**
- **Draft validation:** type, range, allowed layer, final conflicts, range widening, period overlap, referential integrity, and both EN and GR text.
- **Impact preview:**
  - lists the contexts whose values change;
  - shows golden-suite results for the proposed state against the current state;
  - shows a re-rating sample of 500 or more in-force policies via RAT dry-run (`cfg.preview.shadow_sample_size` = 500).
- **Guarded expectations:** a preview that changes a `guarded` golden expectation blocks submit until the expectation owner accepts (REQ-MKT-055).
- **Approvals:**
  - Maker-checker at L1–L4 and for sensitive L5 keys (REQ-MKT-056).
  - Authority type `MKT_CONFIG_APPROVAL` with dimensions layer, namespace and entity (REQ-MKT-057).
  - Drafting rights by layer (REQ-MKT-068): core and region by design authority; group by group architect; country by country product owner; entity by entity configuration administrators; product/channel by the owning module's engineers.
- **Activation:**
  - The instant is computed in UTC from the business date in the entity timezone. For example, 2027-01-01 in GR becomes 2026-12-31T22:00Z (REQ-MKT-058).
  - Past instants are allowed only for retro changes.
  - Retro changes need `retro_allowed` plus design-authority approval, and they publish the retro window. MKT never re-processes transactions (REQ-MKT-059). `cfg.retro.max_days` = P90D.
  - A scheduled activation can be withdrawn before its instant (REQ-MKT-060).
- **Concurrency:** a concurrent overlapping request goes to `conflict` and must be rebased (REQ-MKT-061).
- **Bulk import:** CSV or JSON with dry-run (Should, REQ-MKT-062).
- **Audit:** an AuditEvent for every action (REQ-MKT-066).
- **Hot-fix** (REQ-MKT-131, BR-MKT-013):
  - Allowed at country and entity layers only (`cfg.hotfix.layers` = [L3, L4]).
  - Expedited approval: checker plus on-call design authority (`MKT_HOTFIX_COSIGN`).
  - Mandatory golden preview.
  - Activation without deployment, targeting 1 business day or less (NFR-MKT-019).

**Migration-baseline state (REQ-MKT-010, §14.y):**
- Historical effective-dated values with their own hash, which converted facts carry.
- Bulk load with dry-run.
- Immutable once used by a reconciled batch. It can only be superseded by a new baseline under maker-checker.

**Merge property tests (§14.x.2):**

| ID | Property |
|---|---|
| P-01 | Resolution is deterministic and independent of insertion order |
| P-02 | A final value dominates every lower context in its period |
| P-03 | CONSTRAINED results stay within every higher range |
| P-04 | ADDITIVE results contain each non-removed member exactly once |
| P-05 | Removing a lower value never changes resolution for contexts outside that node |
| P-06 | The hash changes if and only if the manifest changes |
| P-07 | `resolve(hash, …)` is unchanged by later activations |
| P-08 | Period splitting with the same value is neutral |

REQ-MKT-243 requires 10,000 generated stacks.

### 7.2 Capability switches (MKT-C02, REQ-MKT-004, 070–079)

- Switches are keys in `cap.*` of type capability, so they are effective-dated, inside the hash and resolved by the standard resolver (REQ-MKT-071).
- Each switch has a reversibility class, allowed scopes (market, entity, product line, channel), `requires` and `excludes` validated at draft and at activation (REQ-MKT-075), and reading modules.
- IRREVERSIBLE switches need a recorded design-authority decision (`MKT_IRREVERSIBLE_SWITCH`) on top of maker-checker. Missing it gives `MKT-ERR-CAP-DA-REQUIRED`. Once on, they cannot be turned off (`MKT-ERR-CAP-IRREVERSIBLE`) (REQ-MKT-072–073).
- At minimum, these are IRREVERSIBLE: ledger posting model, functional currency, numbering scheme family, fiscal channel mode, tenancy partition model, tax point basis, and billing sub-ledger reconciliation basis.
- REVERSIBLE_WITH_MIGRATION switches need a linked migration plan (Should, REQ-MKT-074).
- PLT feature flags must not carry market behaviour. ARCH-07 fails on a flag key with a country or entity qualifier (REQ-MKT-076).

**Initial catalogue (§10.2.3, REQ-MKT-079):**

| Switch | Reversibility | Default | GR MVP | CY stub |
|---|---|---|---|---|
| `cap.fin.posting_model` | IRREVERSIBLE | standard | standard | standard |
| `cap.cur.functional_currency_lock` | IRREVERSIBLE | on at activation | on | on |
| `cap.num.scheme_family` | IRREVERSIBLE | default | GR | CY |
| `cap.cmp.fiscal_mode` (NONE/BATCH/REALTIME) | IRREVERSIBLE | NONE | REALTIME | NONE |
| `cap.cmp.einvoice_b2b` | REV_WITH_MIGRATION | off | **off at go-live** (D3) | off |
| `cap.tax.tax_point_basis_lock` | IRREVERSIBLE | on at activation | on | on |
| `cap.tenancy.partition_model` | IRREVERSIBLE | single | brands+channels+banca | single |
| `cap.bil.agency_bill` | REV_WITH_MIGRATION | off | on | off |
| `cap.bil.sepa_dd` (requires mandates) | REVERSIBLE | on | on | on |
| `cap.bil.instant_payments` | REVERSIBLE | off | on | off |
| `cap.cmp.bureau_reporting` | REVERSIBLE | off | on | on (file) |
| `cap.clm.friendly_settlement` | REVERSIBLE | off | on | off |
| `cap.chn.wallet_prefill` | REVERSIBLE | off | on | off |
| `cap.uw.natcat_refusal_docs` | REVERSIBLE | off | on from Home | off |
| `cap.xb.fos_business` | REVERSIBLE | off | off at MVP | n/a |
| `cap.l10n.dual_currency_docs` | REVERSIBLE | off | off | off (BG fixture on) |

Note: REQ-MKT-072 names a "billing sub-ledger reconciliation basis" switch that does not appear in the §10.2.3 catalogue (gap).

### 7.3 SPI framework (MKT-C03, REQ-MKT-002, 080–124)

**Definition (REQ-MKT-080):** each SPI is a versioned typed interface with:
- request and response schemas and error codes;
- idempotency class (P pure, K keyed, N/A query);
- call mode (S synchronous in-process, A asynchronous through the PLT integration hub);
- default timeout and fallback policy;
- **binding axis**, core default behaviour, and a **conformance test kit** (REQ-MKT-123).

**Binding:**
- Binding table rows: (entity, SPI, axis value, qualifier, validity, implementation id, pack version), versioned in the hash (REQ-MKT-082).
- At most one binding per (entity, SPI, axis value, qualifier, instant). Multi-scheme SPIs (`IdValidator` per scheme, `SanctionsListSource` per list) use the qualifier, and composite SPIs return an ordered set (REQ-MKT-084).
- Callers get implementations **only** via `mkt.Spi.bind` (REQ-MKT-002).

**Binding axes (REQ-MKT-085):** RISK_LOCATION, LEGAL_ENTITY_HOME, BRANCH_LOCATION, POLICYHOLDER_RESIDENCE, CONTRACT_LAW_JURISDICTION, DOCUMENT_LANGUAGE, SCHEME, LIST. The rule is "each SPI declaring exactly one axis", though some §9.4.0 rows list two (see §14c).

**Gateway (REQ-MKT-086):** applies W3C trace context, metrics, timeout, circuit breaker, response-schema validation and fallback, and writes an SpiCallLog entry (REQ-MKT-119).

**Error model (REQ-MKT-116):** VALIDATION, NOT_APPLICABLE, RULE_MISSING, UNAVAILABLE, TIMEOUT, CONTRACT_VIOLATION, mapped to RFC 9457 with a `retryable` flag.

**Purity (REQ-MKT-117, ARCH-06, TCK-PURE-01):**
- Calculation SPIs must be pure, with no I/O: `TaxCalculator`, `RegimeCodeList`, `StatutoryClockSet`, `NameTransliterator`, `AddressFormatter` formatting, `NumberingScheme` formatting, `PricingConstraint`, `DocumentLanguageRule`.
- External-call SPIs run as adapters in the PLT integration hub and pass the caller's idempotency key (REQ-MKT-118): FiscalDocumentChannel, EInvoiceProvider, BureauAdapter, RegistryLookup, IntermediaryRegister, MotorDataProvider, PayeeVerification, FriendlySettlementClearing, and the SanctionsListSource fetch.

**Versioning:** semantic interface versions. The core supports majors N and N-1 concurrently (REQ-MKT-120, NFR-MKT-022).

**Adding an SPI (REQ-MKT-121, BR-MKT-038) needs:**
- evidence of variation across 2 or more markets, or one market with a legal basis another market lacks;
- a core default;
- a conformance kit;
- a design-authority decision with a CCR reference.

**No escape hatches (REQ-MKT-122, ARCH-05):**
- Untyped map, script or reflective SPI signatures are banned.
- Rule expressions use the programme's single **CEL-compatible** typed expression language evaluated by the PLT rules runtime (D10).

**Emergency suspension of external SPI bindings (Should, REQ-MKT-124):** the binding switches to its fallback, under maker-checker, with automatic expiry after 72 hours.

### 7.4 Full SPI catalogue (§9.4.0 plus §9.4.1–9.4.43, with operation signatures as given)

Mode: S = synchronous, A = asynchronous. Idempotency: P = pure, K = keyed on the caller's key.

| # | SPI | Operations (signatures as stated) | Callers | Mode / axis / idem. | Timeout → fallback | Core default |
|---|---|---|---|---|---|---|
| 1 | **IdValidator** | `validate(scheme, value, context) → {status: Valid\|ValidFormat\|Invalid\|Unverified, normalised, errors[]}`; `verify(scheme, value, evidenceContext) → {status: Verified\|NotFound\|Mismatch\|NotAvailable, source, checkedAt}` (async when external). Scheme **`VEHICLE_PLATE`**: `normalise(value, context) → {normalised, findings[]}`, `searchKey(value) → key` (pure, 20 ms; REQ-MKT-339). Errors: VALIDATION (`CHECK_DIGIT`, `FORMAT`, `LENGTH`), NOT_APPLICABLE, UNAVAILABLE. | PTY, PFC, POL, MIG, DAT, CMP (via PTY), CHN, CLM | S (verify A) / SCHEME / P, K | 50 ms validate; verify 3 s → `Unverified` | Optional schemes accepted as `Unverified`; mandatory schemes fail closed. Plate: trim + upper-case, `Unverified`. |
| 2 | **NameTransliterator** | `transliterate(text, sourceScript, ruleSet?) → {latin, ruleSetId, warnings[]}`; `searchVariants(text) → keys[]` (the only source of transliteration and Greeklish variants) | PTY, DOC, WRK, BIL, MIG, CHN, CLM, PLT | S / SCHEME (script + country) / P | 20 ms; fail closed | Identity for Latin input; error otherwise |
| 3 | **AddressFormatter** | `parse(lines, country)`, `validate(address) → {status, errors[]}` (`POSTCODE_FORMAT`, `MISSING_LOCALITY`), `format(address, purpose: POSTAL\|DOCUMENT\|SINGLE_LINE, script)` | PTY, DOC, POL, MIG, CHN, CLM | S / RISK_LOCATION or address country / P | 50 ms; free-form | Free-form lines, no postcode check |
| 4 | **TaxCalculator** | `calculate(request) → result`; `treatment(request) → result` (D2); optional `calculateReinsurancePremiumTax(riPremiumLines, jurisdiction, date)` (R-49, Should P3). Details in §7.5. | `calculate`: RAT (POL-originated charges), BIL (own fees and receipt stamps). `treatment`: RAT, BIL, FIN. RI: the reinsurance tax operation only. | S / RISK_LOCATION (+ subdivision) / P | 50 ms (treatment 20 ms); fail closed | **None** |
| 5 | **FiscalDocumentChannel** | `build(fiscalSource) → document`; `submit(document, idempotencyKey) → {status: Registered\|Rejected\|NotRequired\|Queued, registrationId, uid, rejections[]}`; `cancel(registrationId, reason)`; `series(documentType) → series rules` | CMP (BIL and FIN consume results) | A / LEGAL_ENTITY_HOME or BRANCH_LOCATION / K | 30 s per attempt; queue on outage | `NotRequired` |
| 6 | **EInvoiceProvider** (Must P1 interface, D3) | `issue(invoice, recipient, idempotencyKey) → {status, providerRef}`; `status(providerRef)` | CMP | A / LEGAL_ENTITY_HOME / K | 30 s; queue | `NotRequired` |
| 7 | **BureauAdapter** | `report(policyEvent, idempotencyKey) → {status, bureauRef}`; `reconcile(period) → differences[]` | CMP | A / RISK_LOCATION / K (event id) | 30 s; queue | `NotRequired` |
| 8 | **StatutoryClockSet** | `get(clockCode, jurisdiction, date)`, `list(jurisdiction, date)` returning StatutoryClockValue | CMP | S / CONTRACT_LAW_JURISDICTION or per clock / P | 20 ms; EU default or fail closed | EU-layer defaults only |
| 9 | **NumberingScheme** | `format(identifierType, sequence, context) → string`; `validate(identifierType, value)`; `series(identifierType, context) → series id` | PLT numbering | S / LEGAL_ENTITY_HOME / P | 20 ms; fail closed | Prefix + zero-padded sequence without personal data |
| 10 | **HolidayCalendarProvider** | `holidays(country, region?, year) → [{date, type: PUBLIC\|BANK\|REGIONAL, name_en, name_local}]` | PLT calendar store | Batch (yearly) / SCHEME / P | n/a; previous year's rules flagged | Weekends only |
| 11 | **PaymentReferenceGenerator** | `generate(billingAccount, purpose) → reference`; `validate(reference)`; `parse(reference) → billing account hint` | BIL | S / LEGAL_ENTITY_HOME / P | 20 ms; RF | ISO 11649 RF |
| 12 | **BankFileFormat** | `writePayments(batch, format)`, `readStatement(file, format)`, `writeDirectDebits(batch, format)` | BIL | S (file) / SCHEME / P | n/a | SEPA ISO 20022 pain.001/008, camt.053/054 |
| 13 | **PayeeVerification** | `verify(iban, name, payeeType) → {result: Match\|CloseMatch\|NoMatch\|NotAvailable, suggestedName?}` | BIL (CLM via BIL) | S external / LEGAL_ENTITY_HOME / K | 2 s; `NotAvailable` | `NotAvailable` |
| 14 | **RegimeCodeList** | `list(regime, date\|version)`, `validate(regime, code, date)`, `crosswalk(regime, code, fromVersion, toVersion)` | PFC, DAT, FIN, RI | S / SCHEME / P | 20 ms; fail closed | None (the `eu` pack supplies SII) |
| 15 | **DocumentLanguageRule** | `rules(documentType, jurisdiction, customerLanguage) → {binding, informative[], customerChoiceAllowed}` | DOC, PFC, UW, RI, CLM, WRK | S / CONTRACT_LAW_JURISDICTION / P | 20 ms; entity default | Binding = entity default language |
| 16 | **MotorDataProvider** | `vehicleByPlate(plate)`, `vehicleByVin(vin)`, `walletPrefill(consentToken)`, `valuation(vehicle)`. Wallet consent (R-56, R-87): `requestWalletConsent(subject, purpose, legalBasis: INSURER_DECISION\|INTERMEDIARY_DECISION, channel) → pendingRef` (A, K, 5 min expiry); `consentResult(pendingRef) → {status, data, legalBasis, channel}`; `cancelWalletConsent(pendingRef) → {status: Cancelled}` | POL, CHN, UW, RAT | S external / RISK_LOCATION / K | 3 s; `NotAvailable` (manual entry within 300 ms) | `NotAvailable` |
| 17 | **RegistryLookup** | `byTaxNumber(id)`, `byRegistryNumber(id)` → name, legal form, address, status, source, timestamp | PTY | S external / SCHEME / K | 3 s; `NotAvailable` | `NotAvailable` |
| 18 | **IntermediaryRegister** | `check(registerNumber, date) → {status, categories, source: LIVE\|SNAPSHOT, snapshotDate}` | PTY | S external / LEGAL_ENTITY_HOME or host / K | 3 s; snapshot | `UnknownRegister` |
| 19 | **ComplaintRules** | `rules(jurisdiction, complainantType, channel, date) → {ackDeadline, responseDeadline, holdingResponseRule, adrBodies[], mandatoryTextRefs[]}` | CMP | S / POLICYHOLDER_RESIDENCE (+ home) / P | 20 ms; fail closed | None |
| 20 | **RefusalDocumentRule** | `evaluate(declineRecord) → {required, templateRef, deadline}`; extension (R-29): `responseDeadline(requestType, jurisdiction, date) → {duration, clockCode}`, `registerExport(afm, period) → file` | UW, DOC | S / RISK_LOCATION / P | 20 ms; `NotRequired` | `NotRequired` |
| 21 | **SanctionsListSource** | `lists(entity, jurisdiction) → ordered list ids`; `fetch(listId) → list version` (batch through the hub) | PTY | Batch + S / LIST / K | Retries; last good list with age warning | EU consolidated list |
| 22 | **ClaimsHistoryFormat** | `format(jurisdiction) → {fields[], templateRef, retentionYears}` | CLM, DOC | S / CONTRACT_LAW_JURISDICTION / P | 20 ms; generic | Generic field list |
| 23 | **FriendlySettlementClearing** | `submit(agreedClaim)`, `settlementStatement(period)`; extension (R-42): `evaluateEligibility(claimFacts)`, `submitDispute(receivableRef, reason)`, `recordReply(disputeRef, decision)`, `receiveNotification(message)` | CLM | A / RISK_LOCATION / K | 30 s; queue | `NotApplicable` |
| 24 | **PricingConstraint** (R-05, R-24) | `prohibitedFactors(jurisdiction, line, date)`, `check(ratingArtefactSummary) → violations[]`, `maxChange(jurisdiction, line) → limit or none`; extension: `proxyAttributes(jurisdiction, line) → [{attribute, justificationRequired, reason}]`, `fairnessDefaults(jurisdiction, line) → {maxRenewalIncreaseWithoutReferral, newVsRenewalParity, source}` | RAT, PFC | S / RISK_LOCATION / P | 20 ms; EU defaults | EU protected-characteristics list |
| 25 | **ConsentRules** (R-05) | `rules(jurisdiction, purpose, channel) → {model: OPT_IN\|OPT_OUT\|NOT_REQUIRED, lawfulBasisDefault, proofRetention}` | PTY, CHN, DOC | S / POLICYHOLDER_RESIDENCE / P | 20 ms; strictest (opt-in) | Opt-in for marketing |
| 26 | **ESignatureProvider** (R-05) | `select(documentType, jurisdiction) → providers[] with level`; `sign(envelope)` (async) | DOC | S select, A sign / CONTRACT_LAW_JURISDICTION / K | 30 s; next provider | Simple electronic signature with evidence |
| 27 | **DigitalIdentityProvider** (R-05; **Could**) | `providers(jurisdiction, journey) → list` | CHN, PLT | S / POLICYHOLDER_RESIDENCE / K | 5 s; manual | None |
| 28 | **TaxReturnFormat** (R-05, R-49) | `generate(period, totalsByClass, entity) → {file, format, controlTotals}`; extension: detail lines, adjustments, `validate(file)`, period scheme, filing-channel descriptor | FIN, CMP; BIL reads `periodScheme` | Batch / RISK_LOCATION (host State) / P | n/a | Generic CSV totals by class |
| 29 | **MandatoryWordingSet** (R-05) | `clauses(documentType, productLine, jurisdiction, date) → clause codes and versions` | DOC, PFC | S / CONTRACT_LAW_JURISDICTION / P | 20 ms; empty set + warning | Empty set |
| 30 | **Geocoder** (R-17, R-26) | `geocode(address) → {lat, lon, precision: ROOFTOP\|STREET\|POSTCODE\|MUNICIPALITY, source, sourceVersion}`; `hazardKeys(location, schemes[]?, date) → [{schemeCode, schemeVersion, zone, score?, source}]`; `schemes(jurisdiction, date) → [{schemeCode, versions[], status: ACTIVE\|PARALLEL\|RETIRED}]` | PTY, POL, RAT, UW, CLM, CHN, RI, DAT | geocode S external (K, 2 s); hazardKeys pure (20 ms) / RISK_LOCATION | 2 s → `NotAvailable`; postcode table | `NotAvailable`; empty hazard keys |
| 31 | **PolicyLifecycleRules** (R-36, R-55) | `refundRule(kind: CANCELLATION\|VOID, source: Policyholder\|Insurer\|NonPayment\|DistanceWithdrawal\|LongTermWithdrawal\|Objection\|Statutory, context) → {method: ProRata\|ShortRate\|Flat\|MinimumRetained\|FullRefund, deduction rules, refundClockCode, ruleId, legalSource}`; `cancellationNotice(source, termLength, jurisdiction) → {noticePeriod, clockCode, deliveryRuleRef}`; `reinstatementGap(lapseDays, context) → {allowed, conditions}`; `suspensionEffects(context) → {coverEffect, premiumEffect, maxDuration}` | POL, BIL | S / CONTRACT_LAW_JURISDICTION / P | 20 ms; fail closed | None; `reinstatementGap` defaults to `allowed = false` |
| 32 | **MotorCompensationBodyAdapter** (R-41) | `notify(claimRef, bodyType: GUARANTEE_FUND\|GREEN_CARD_BUREAU\|COMPENSATION_BODY, payload, idempotencyKey) → {status, bodyRef}`; `requestReimbursement(...)`; `receiveNotification(message) → typed event`; `settlementStatement(period)` | CLM | A / RISK_LOCATION / K | 30 s; queue | `NotApplicable` |
| 33 | **StatutoryDeliveryRule** (R-41) | `rules(documentType, jurisdiction, recipientType, date) → {acceptedMedia[], channels[], proofLevel, notificationDateRule, evidenceItems[], durableMediumConsentRequired}` | DOC, BIL, POL, CMP | S / CONTRACT_LAW_JURISDICTION / P | 20 ms; fail closed for statutory | Durable medium with read evidence (non-statutory) |
| 34 | **PaymentChannelProvider** (R-41) | `createCollection(invoice, channel) → {collectionRef, payload, expiry}`; `parseNotification(message) → {collectionRef, amount, payerRef, status}`; `capabilities(entity) → channels[]` | BIL, CHN | S / LEGAL_ENTITY_HOME / K | 3 s; channel not offered | No extra channels |
| 35 | **InboundDocumentProfile** (R-05) | `classes(jurisdiction)`; `schema(documentClass) → fields, validation, confidence thresholds`; `parse(documentClass, payload: MRZ\|QR\|barcode) → fields, checkResults` | WRK | S / SCHEME / P | 50 ms | ICAO 9303 MRZ + generic classes |
| 36 | **IdentityFederationProvider** (R-05; **Should**) | `providers(jurisdiction, realm) → federation descriptors (protocol, issuer, assurance level, attribute mapping)` | PLT | S / POLICYHOLDER_RESIDENCE / P | 20 ms | None |
| 37 | **IncidentReportingChannel** (R-05) | `submit(report, idempotencyKey) → {status: Submitted\|ManualSubmissionRequired\|Rejected, receipt}`; `format(reportType) → schema` | PLT | A / LEGAL_ENTITY_HOME / K | 30 s; manual | `ManualSubmissionRequired` |
| 38 | **FxRateSource** (R-05) | `source(jurisdiction, rateType) → {provider, publicationTime, currencies, fallbackSource}` | PLT | S / LEGAL_ENTITY_HOME / P | n/a | Euro reference rates |
| 39 | **LegacyDataProfile** (R-71) | `profilePlate(value) → {normalised, findings[] (LOOKALIKE_NORMALISED, INVALID_SERIES, FORMAT)}` (delegates to IdValidator `VEHICLE_PLATE`); `detectEncoding(bytes, hints) → {encoding, text (NFC), confidence}`; `checkTransliteration(native, latin) → {consistent, expectedLatin, ruleSetId}`; `identifierHeuristics(scheme, value, partyType) → findings[]`; `rules(jurisdiction)` | MIG | S + batch / SCHEME (legacy source country) / P | 50 ms per record; `UNPROFILED` (never silently accepted) | UTF-8 and NFC checks only |
| 40 | **StatutoryDataReturnFormat** (R-68, R-92) | `returnTypes(entity, date) → [{returnType, taxonomyVersion, periodScheme, dueRuleClockCode}]`; `layout(returnType, taxonomyVersion)`; `build(martRun) → {file, controlTotals, warnings}`; `validate(file) → {valid, errors[]}`; `filingChannel(returnType) → descriptor` | DAT (build), CMP (validate) | Batch / LEGAL_ENTITY_HOME / P | n/a | `NotRequired` |

**Numbering of the §9.4 sub-sections:** rows 30–40 are documented in §9.4.31–9.4.42, and §9.4.30 and §9.4.40 are unused. Row 39 is §9.4.41 and row 40 is §9.4.42.

**SPIs considered and rejected (§9.4.44):**

| Candidate | Outcome |
|---|---|
| `LevyCalculator` | Folded into TaxCalculator |
| `RegulatoryMapper` | Covered by RegimeCodeList plus the PFC assignment |
| `RiskLocationResolver` | Core rule at the region:EU layer |
| `CurrencyRounding` | Configuration keys |
| `BusinessDayCalculator` | PLT service |

### 7.5 TaxCalculator in detail (§9.4.4, REQ-MKT-087, 330–332, BR-MKT-043)

**`calculate(request) → result`**
- Request: legal entity; risk jurisdiction (+ subdivision); tax point date; product line and tax class per element; charge lines (element, charge type, premium amount, period, currency, transaction type, unit counts such as vehicles); policyholder type (consumer or business); business basis.
- Result: tax lines (element, charge type, category TAX \| LEVY \| STAMP, tax class, base, rate or fixed amount, amount, rounding rule id, rule id, legal source ref), plus document-level lines (per-policy or per-receipt stamps).
- Behaviour: pure; rates come from pack data; rounding goes through `mkt.Rounding.apply` with the tax-line rules.

**`treatment(request) → result`**
- Request: legal entity; risk jurisdiction (+ subdivision); tax point date; charge type and category; charge origin (POL or BIL); policyholder type; business basis.
- Transaction kind: `NEW_BUSINESS`, `ENDORSEMENT_DEBIT`, `ENDORSEMENT_CREDIT`, `CANCELLATION`, `DISTANCE_WITHDRAWAL_VOID`, `VOID`, `RETURN_PREMIUM`, `REINSTATEMENT`, `FEE`, `REFUND`. The value `ENDORSEMENT` is invalid and returns `MKT-ERR-SPI-VALIDATION`.
- Cancellation source: from the shared code list. It is required for CANCELLATION and VOID, and implied as `DistanceWithdrawal` for DISTANCE_WITHDRAWAL_VOID.
- Behaviour: pure, synchronous, 20 ms, fail closed (`MKT-ERR-SPI-RULE_MISSING`), no core default. This is the one schema used by every caller (FZ-03).

Each result row carries `ruleId`, `ruleVersion`, `legalStatus` (Settled or Pending), `legalSourceRef` and the configuration hash. The `action` value is shorthand for three detail fields:

| Action | customerCredit | authorityLiability | fiscalDocument |
|---|---|---|---|
| `APPLY` | (charge taxed normally) | — | — |
| `REDUCE_PRO_RATA` | PRO_RATA | REDUCE | NONE |
| `REVERSE_AS_VOID` | FULL | REDUCE | CREDIT_NOTE |
| `KEEP_NOT_REDUCED` | NONE | NOT_REDUCE | NONE |
| `INSURER_BEARS` | FULL | NOT_REDUCE | NONE |

**Forbidden in every pack:** `customerCredit = NONE` with DISTANCE_WITHDRAWAL_VOID or source DistanceWithdrawal, which would be a refund net of tax. The conformance vector `TCK-TAX-NET-REFUND` enforces this.

**Single-call rule (REQ-MKT-332):**
- The module that originates a charge calls `calculate` once:
  - RAT for POL-originated charges;
  - BIL for its own instalment, dishonour and late-payment fees flagged in the PFC tax base, and for receipt stamps;
  - RI only via the reinsurance premium tax operation.
- Every tax line carries the `ruleId` and `ruleVersion` of both the calculation and the treatment.
- BIL and FIN validate credits by calling `treatment` and comparing the rule id and outcome. A mismatch raises a typed exception, and FIN posts nothing for that line.
- **ARCH-11** fails the build if:
  - a module other than MKT registers a refundability, reduction or liability-point key (for example the removed PFC key `gr.ipt.refund_on_cancel`); or
  - code outside the TaxCalculator implementation reads `tax.treatment.*`.

### 7.6 Other SPI-adjacent services owned by MKT

- **Rounding** (REQ-MKT-191–195):
  - Keys `cur.rounding.<purpose>` hold mode (HALF_UP, HALF_EVEN, DOWN, UP, CEILING, FLOOR), scale and level (line, element, document, instalment).
  - Pack tax-class rules `cur.rounding.tax.<class>` are final at L3.
  - Precedence: tax-class rule, then purpose rule, then currency default (BR-MKT-027).
  - `cur.order_of_operations` has four options:
    1. round premium per element before tax, or not;
    2. compute tax on the base before or after rounding;
    3. sum-then-round or round-then-sum per document;
    4. send the instalment remainder to the first or last instalment.
  - Core default for the order of operations: round per line, tax on the rounded base, round-then-sum, remainder to the first instalment.
  - Worked example: two elements of 10.005 give 20.02 when rounded per element and 20.01 when summed then rounded.
  - Minor units come from ISO 4217 data held by PLT.
- **Currency roles** (REQ-MKT-190): transaction, functional (per entity and book), group (USD for Fairfax, `cur.group` final at L1), and settlement.
- **Rate types** (REQ-MKT-196, 197): `cur.rate_type.<purpose>` (REFERENCE, GROUP_MONTH_END, spot, fixed). The missing-rate policy uses the latest rate within N days (`cur.missing_rate.max_age` P3D), otherwise fails.
- **Fixed-rate conversion** (REQ-MKT-198–199, BR-MKT-029): six significant figures, no inverse rates, legacy-to-legacy via EUR, rounding after conversion. Worked example: 100 BGN gives 51.13 EUR. The rate key is `cur.changeover.<plan>.rate`.
- **Risk location** (REQ-MKT-216, BR-MKT-021, Solvency II Art. 13(13)):

  | Risk | Located in |
  |---|---|
  | Buildings | State where the property is |
  | Vehicles | State of registration |
  | Travel of 4 months or less (`xb.risk_location.travel_threshold` P4M) | State where the policy was taken out |
  | Otherwise | Policyholder residence, or the establishment for a legal person |

  Dispatched vehicles may be located in the destination State for 30 days after delivery, at the policyholder's election (REQ-MKT-217, Should P4).
- **Language rules** (REQ-MKT-340): `mkt.L10n.searchKeys` and `mkt.L10n.caseMap` apply CLDR `el-Upper` (tonos removed in upper case), context-sensitive final sigma, and accent and diaeresis folding. The data is versioned with the CLDR version.

---

## 8. Screens (§6)

All screens are staff realm only and support the R-101 "ΕΛ | EN" switch without reload or loss of unsaved input. Shared patterns:
- `IB-24` quiet chrome, `IB-02` keyboard list navigation, `IB-16` density, `IB-29` log tables;
- `IB-01` command palette (REQ-MKT-304), `IB-21` semantic status system;
- contract §3.9.9 states;
- RFC 9457 error display with a correlation id.

| ID | Name (EN / GR) | Personas | One line | Patterns |
|---|---|---|---|---|
| SCR-MKT-01 | Configuration explorer / Εξερεύνηση παραμετροποίησης | Country PO, config admin, tax specialist, auditor, DA, tester | Effective value, source layer and lineage for an entity, product, channel and date; as-of mode by hash; compare, export, propose change. Ready in 1.5 s or less. | IB-05 split pane, IB-03 work views, IB-08 explain-why, IB-26 filter bar |
| SCR-MKT-02 | Configuration change request / Αίτημα αλλαγής παραμετροποίησης | Maker, checkers, tax specialist | Tabs: Changes (editable grid), Impact (approve-the-diff), Approvals and schedule. Legal source required for `tax.*`, `clk.*`, `reg.*`. Hot-fix and retro flags. AI-MKT-03 rows. | IB-14 edit in place, IB-07, IB-28, IB-19, IB-20 |
| SCR-MKT-03 | Pack registry / Μητρώο πακέτων χώρας | Release manager, DA, market-entry lead, auditor | Versions, gate badges (signature, compatibility, conformance, golden, translations, clocks), per-entity schedule, rollback, rule index | IB-32 |
| SCR-MKT-04 | SPI catalogue browser / Κατάλογος SPI | DA, market-entry lead, architects, compliance | Matrix of SPI × market (Implemented, Default accepted, Stub, Missing, N/A); binding history; propose SPI; suspend binding | IB-08 |
| SCR-MKT-05 | Translation management / Διαχείριση μεταφράσεων | Translator, reviewer, developer | Work queue, ICU editor, terminology check, completeness-gate result (no waiver action) | IB-04, IB-05, IB-10, IB-07 |
| SCR-MKT-06 | Market-readiness scorecard and fork-ratio dashboard / Πίνακας ετοιμότητας αγοράς | Market-entry lead, DA, leadership | 9 readiness dimensions, waivers, fork ratio per pack, core country branches (target 0), open architecture exceptions | IB-17, IB-26, IB-32, IB-12 |
| SCR-MKT-07 | Golden-suite results / Αποτελέσματα χρυσού συνόλου δοκιμών | Tester, tax specialists, release manager | GR, CY and BG side by side per scenario; expected vs unexpected divergence; invariants; AI-MKT-04 triage | IB-28, IB-32, IB-08 |
| SCR-MKT-08 | Capability switch board / Πίνακας διακοπτών δυνατοτήτων | Country PO, DA, config admin | Switch × scope grid; IRREVERSIBLE shown `locked` | — |
| SCR-MKT-09 | Legal entity and stamp registry / Μητρώο νομικών οντοτήτων και stamps | Group architect, market-entry lead, platform engineer | Entity (LEI with ISO 17442 check), stamp, partitions, stand-up checklist, group dependency graph | IB-11, IB-15 |
| SCR-MKT-10 | Regime code list manager / Διαχείριση κωδικολογίων εποπτικών καθεστώτων | Regulatory analyst, config admin, data steward | Versions, codes with EN and GR labels, crosswalks, import reconciliation | — |
| SCR-MKT-11 | Cross-border authorisation register / Μητρώο διασυνοριακής δραστηριότητας | Compliance, market-entry lead | FoS and FoE records, representatives, memberships. Should P4. | IB-26 |
| SCR-MKT-12 | Market-entry tracker / Παρακολούθηση εισόδου σε αγορά | Market-entry lead | Playbook checklist, definition of done, AI-MKT-05 gap analysis. Should P4. | IB-11, IB-12, IB-32, IB-31 |

## 9. Regulatory, tax and statutory rules

### 9a. Stated explicitly in the source (with IDs)

**Greece**
- **IPT 15% general and 20% fire** under Law 5177/2025 Art. 43. Peer-verified per PRD-06 and PRD-09; OI-MKT-01 closed. Sources: §3.1 OBL-TAX, GR-01, §9.4.4. The rates are pack data and ARCH-04 forbids rate literals in core.
- **IPT liability point:** pack key `tax.ipt.liability_point`, default **DUE**, because the statute taxes «απαιτητά ασφάλιστρα» (premiums due). Legal status Pending (REQ-MKT-328, 331a).
- **Auxiliary Fund contribution** (Law 5113/2024 Art. 14, amending P.D. 237/1986 Art. 20):
  - **6% ceiling** and **70% insurer / 30% policyholder split**, both verified on 2026-10-07 (REQ-MKT-322, GR-02).
  - Two charge types: `GR-AUXF-INS` (accrued, not billed) and `GR-AUXF-PH` (billed and shown on the policy).
  - A stamp-duty line on the policyholder share.
  - Provisional reading `split_basis = WHOLE_CEILING`. Example: MTPL 300.00 gives PH 5.40 and INS 12.60.
  - Components, establishment condition, stamp-duty rate, base (written premium per D5 unless the opinion says otherwise), mid-term base and cancellation treatment are **reading keys only**, Pending.
  - Remittance clock `BIL_AUXF_REMIT`: 15 days after each calendar two-month period. Levy return periods are two months (§9.4.43).
- **IPT return** `FIN_IPT_RETURN`: quarterly, due by the end of the third month after quarter end (Law 5177/2025 Art. 43 §6). The period scheme is calendar quarters.
- **D2 Greece treatment defaults (REQ-MKT-331)**, all with legal status Pending:

  | Case | Action |
  |---|---|
  | Withdrawal / void | `REVERSE_AS_VOID`; the alternative `INSURER_BEARS` is selectable via `tax.treatment.withdrawal_void_routing` |
  | Ordinary cancellation, endorsement credit, return premium, refund | `KEEP_NOT_REDUCED` |
  | New business, endorsement debit, fee | `APPLY` |
  | Fees in the IPT base | Follow the PFC tax-base flag (`tax.ipt.fee_in_base.<feeType>`) |
  | Refund net of tax on withdrawal | Excluded |

- **Distance withdrawal** (Law 5317/2026 Arts 69–72, R-55):
  - 14 calendar days.
  - **Full premium refund including tax and levy lines**, with no charge for cover enjoyed (Art. 72).
  - Refund within `POL_REFUND_DUE` (30 days).
  - Long-stop variant of 12 months + 14 days when the terms were never received (Art. 71, verified).
  - Sources: REQ-MKT-309, §10.4.3.
- **Non-payment notice:** 1 month from proven notification before dissolution (Law 2496/1997 Art. 6 §2; R-60) (`BIL_NONPAY_NOTICE`).
- **MTPL claim clocks:**
  - Reasoned offer within 3 months (`CLM_MTPL_OFFER`).
  - Material-damage assessment: 15 days for an accident in Greece, 25 days abroad, from claim submission (Act 87/2016 Art. 5).
  - Payment due 10 days from proven delivery of the reasoned offer (Act 87/2016 Art. 6, verified).
  - Repair in kind within 20 days of the repair agreement (Act 87/2016 Art. 6, verified).
- **Complaints:** final reply in 50 calendar days (BoG Act 88/2016, FEK B 1109/2016) (`CMP_COMPLAINT_REPLY`, §9.4.19).
- **Non-disclosure and aggravation insurer action:** 1 month (Law 2496/1997 Arts 3(3) and 4(2)).
- **Objection when pre-contractual information was missing:** 14 days from delivery, hard stop 10 months after the first premium (Law 2496/1997 Art. 2 §6).
- **Claims-history statement:** 15 days (Law 5113/2024 Art. 7; placed at the EU layer, which is an oddity).
- **AFM:** nine digits with a mod-11 check. Example valid value: `090000045`. GEMI format; VAT with EL prefix via VIES.
- **ELOT 743 Type 2** transliteration (aligned with ISO 843). Example: "Γεώργιος Παπαδόπουλος" becomes "Georgios Papadopoulos".
- **Postcodes:** 5 digits.
- **Holidays:** Orthodox Easter-based movable days. 2027: Easter Sunday 2027-05-02, Easter Monday 2027-05-03.
- **Timezone:** Europe/Athens.
- **myDATA:** MARK must be obtained **before** delivery for premium receipts; motor uses the TRANSACTION trigger. One certification rule covers `cmp.fiscal.mark_before_issue` and `bil.fiscal.mark_before_delivery`. CMP is the only issuer of fiscal series and numbers (D3, REQ-MKT-258).
- **Nat-cat business response:** 30 days (`UW_NATCAT_RESPONSE`), with silence counted as refusal (JMD 96806/2025, UNVERIFIED).
- **A.1004 return:** due 10 January of the following year (§9.4.42, citing PRD-11).
- **B2B e-invoicing:** Decision A.1044/2026 moved the start for large businesses to 2 March 2026; all others from 1 October 2026. Whether it applies to insurance premium documents is **Uncertain** (F13, OI-MKT-08).

**EU**
- Solvency II Art. 157: premium taxes are due only in the State of risk (BR-MKT-020).
- Art. 13(13) risk-location rules; Arts 145–152 (FoE, FoS, motor host bureau and fund membership, claims representative); Art. 159 statistics.
- MID 2009/103/EC Art. 15 (30-day dispatched vehicle) and Art. 22 (3-month offer).
- EU-layer defaults in the `eu` pack (REQ-MKT-287): distance withdrawal 14 calendar days; DSAR 1 month, extendable by 2; motor offer 3 months.
- EU minimums may be lengthened but never shortened (REQ-MKT-288, BR-MKT-034).
- Euro conversion (Reg. 1103/97): 6 significant figures, no inverse rates. BGN fixed at 1.95583 from 2026-01-01 (Reg. 2025/1409).
- GDPR Art. 12(3) one month. DORA applies from 17 January 2025. Gender-neutral pricing (CJEU C-236/09).

### 9b. Referenced but not specified (gaps)

- **Greek clock values marked UNVERIFIED** (§10.4.3):
  - `POL_OBJECTION`: 1 month, Art. 2 §5 (OI-MKT-14).
  - `POL_WITHDRAWAL_LONGTERM`: 14 days, paragraph unverified (OI-POL-03).
  - `POL_REFUND_DUE`: "other" variant (OI-POL-04).
  - `POL_RENEWAL_NOTICE`: placeholder of 45 days shared with `mig.renewal.offer_lead_days`; "no statutory period found".
  - `POL_NONRENEWAL_NOTICE`: no value at all.
  - `POL_INSURER_TERMINATION_NOTICE`: 15 days (OI-UW-02).
  - `POL_MTPL_THIRDPARTY_NOTICE`: 16 days, P.D. 237/1986 Art. 11a; registered only if OI-BIL-02 confirms it.
  - `CLM_FS_COUNTERPARTY_REPLY`: 10 business days (OI-CLM-02).
  - `UW_AMENDMENT_ACCEPTANCE`: 1 month.
  - `CMP_COMPLAINT_ACK`: 5 business days proposed as an entity standard.
- **Not fully specified:**
  - The Auxiliary Fund component structure.
  - The myDATA document types and schemas ("Verify (CMP)").
  - The Information Centre operator and format ("to be verified by CMP").
  - The Friendly Settlement agreement rules.
  - The gov.gr Wallet channel.
  - The Greek bank payment-code format ("per pack spec").
  - Statutory notice delivery: registered letter or equivalent electronic delivery, UNVERIFIED.
  - Greek pricing limits beyond gender ("Verify").
  - Greek e-privacy consent rules.
  - gov.gr co-signing scope.
  - Hazard schemes (UNVERIFIED content).
  - The Motor compensation bodies: Auxiliary Fund, Hellenic Motor Insurers' Bureau, and the compensation body (UNVERIFIED).
  - The BoG DORA incident channel (OI-PLT-01).
  - The EAEE statistics layout.
  - Mandatory wording content.
  - Claims-history certificate content.
- **Facts the PRD itself flags as weak:**
  - MID Art. 22 and Reg. 1103/97 article numbers (OI-MKT-04, OI-MKT-18).
  - Cyprus MIF 5% primary law (OI-MKT-06).
  - The Bulgarian dual-display primary text (OI-MKT-05).
  - The Directive 2025/2 dates (OI-MKT-03).
- **Production gate:** of 32 Greece index rows, **22 are not Settled and 17 sit on the motor path** (REQ-MKT-343 rationale). Production activation of any non-Settled motor-path value is refused.

### 9c. Deferred to configuration or the pack (by design)

Statement in §3.1: "Rates, thresholds and deadlines are pack data, never core requirements."
- All tax and levy rates, bases, classes and stamps: `tax.ipt.*`, `tax.levy.<code>.*`, `tax.stamp.*`, `tax.levy.auxfund.*`, `tax.treatment.*`.
- Tax point basis (IRREVERSIBLE).
- Rounding rules and order of operations (decision 10 is still to confirm Greek rounding with finance).
- All clock values (`clk.<clockCode>`).
- Refund methods except distance withdrawal ("per GR pack specification").
- Holiday content, numbering formats, payment reference formats, bank file variants, mandatory regimes (`reg.mandatory_regimes`, GR = [SII_LOB, GR_BOG_CLASS, GR_IPT_CLASS]).
- Statutory minimum MTPL amounts, as country-layer finals read by PFC (§10.3).

---

## 10. Greek-market specifics

| Area | Source text |
|---|---|
| **AFM** | `IdValidator` scheme AFM, mod-11 check digit (REQ-MKT-260, §9.4.1, GR-11 Settled format). `LegacyDataProfile.identifierHeuristics` flags, for example, a legal person's AFM on a natural-person record. Registry lookup via the AADE registry service and GEMI (`RegistryLookup`). VAT with EL prefix via VIES. |
| **myDATA** | `FiscalDocumentChannel` GR: document types, series, MARK and UID, cancellation. MARK before delivery for premium receipts (D3). `cap.cmp.fiscal_mode` = REALTIME (IRREVERSIBLE). Status "Verify (CMP)" (GR-03, motor path, G2 gate). CMP alone issues fiscal series. BIL payment demands are «ειδοποίηση πληρωμής», never «τιμολόγιο», and use non-fiscal numbers (§9.4.9, REQ-MKT-342). |
| **B2B e-invoicing** | `EInvoiceProvider` is a Must P1 interface defaulting to `NotRequired`. `cap.cmp.einvoice_b2b` is off at go-live. Applicability is Uncertain (OI-MKT-08). |
| **gov.gr** | Wallet pre-fill via `MotorDataProvider` with two legal bases (INSURER_DECISION, INTERMEDIARY_DECISION) and consent operations (REQ-MKT-316). gov.gr co-signing via `ESignatureProvider` (Verify scope). National public-administration authentication via `IdentityFederationProvider` (Verify, PLT). |
| **Information Centre / bureau** | `BureauAdapter` GR: Information Centre reporting of insured vehicles, one submission per policy event, keyed on the event id (REQ-MKT-093, 259). Operator and channel to be verified by CMP (GR-05). `MotorCompensationBodyAdapter`: Auxiliary Fund, Hellenic Motor Insurers' Bureau (Green Card), compensation body. Friendly Settlement clearing (GR-06). |
| **Greek language** | Greek and English mandatory for the Greek entity (`l10n.languages`, `l10n.ui_languages` final at L3). Greek is the binding document language with English informative (`DocumentLanguageRule`, GR-17 Settled). The always-visible "ΕΛ \| EN" switch and the palette action "Switch language / Αλλαγή γλώσσας". |
| **Language resolution order** | Saved profile choice, then device choice (anonymous visitors), then browser language if el or en, then entity default (Greek). |
| **Text processing** | NFC normalisation. Final sigma and diaeresis folding. CLDR `el-Upper` without tonos ("οδός" → "ΟΔΟΣ"). Accent-insensitive cross-script search ("papadopoulos" or "ΠΑΠΑΔΟΠΟΥΛΟΣ" finds "Παπαδόπουλος"). UCA collation for el-GR. ARCH-03 forbids Greek literals in core. Legacy encodings ELOT 928 / Windows-1253. |
| **Vehicle plates** | Greek-letter plates keyed with Latin look-alikes are normalised to the Greek series ("ikx-1234" → "ΙΚΧ1234"). |
| **Terminology of record** (REQ-MKT-342) | See the table below. A forbidden rendering fails customer-facing text and warns on staff text. Changes need legal-reviewer approval. |
| **EUR** | Transaction and functional currency EUR; group reporting USD. `cur.functional` is `pack_required`, final at L4. el-GR format `1.234,56 €` (REQ-MKT-005 acceptance). |
| **Regulatory code lists** | `GR_BOG_CLASS`, `GR_IPT_CLASS`, `GR_A1004`, `GR_EAEE_CLASS`; `SII_LOB` from the `eu` pack. |
| **Intermediaries** | Chamber register (Law 4583/2018) with file-snapshot fallback. |

Terminology of record (REQ-MKT-342):

| English | Approved Greek |
|---|---|
| invoice (BIL payment demand) | «ειδοποίηση πληρωμής» (never «τιμολόγιο») |
| complaint | «παράπονο» |
| termination by notice | «καταγγελία σύμβασης» |
| statutory clock | «νόμιμη προθεσμία» |
| clock instance | «τρέχουσα προθεσμία» |
| channel | «κανάλι διανομής» |
| policy hold | «πάγωμα εργασιών» |
| blocking point | «σημείο φραγής» |
| product version | «έκδοση προϊόντος» |
| journal entry | «λογιστική εγγραφή» |
| policy term (REQ-MKT-185) | «ασφαλιστική περίοδος» |

**Greece pack index (§10.4.2): status by row**

| Status | Rows |
|---|---|
| Settled | GR-11, 12, 16, 17, 21, 25; GR-01 rates only |
| Pending opinion (D2) | GR-01 (liability point and treatment), GR-02 |
| Verify | GR-03, 05, 06, 07, 08, 09, 10, 13, 15, 18, 19, 20, 22, 23, 27, 28, 29 |
| UNVERIFIED | GR-14 (clocks), GR-26, GR-31, GR-32 |
| Uncertain | GR-04 |
| Market practice | GR-24, GR-30 |

Motor-path rows must be Settled by gate **G2** (before motor go-live).

**Cyprus stub pack (`cy-stub`) — deliberately different (REQ-MKT-263–269, 341; §10.4.7)**

| Area | Cyprus stub |
|---|---|
| Identifier | TIC: 8 digits + 1 Latin letter, format only, `ValidFormat`. `60000001A` is ValidFormat; `6000001A` is Invalid. VAT CY via VIES. Cyprus plate format. |
| Stamp charge | **`CY-STAMP` synthetic**: flat per-policy and per-receipt charges until 2025-12-31. From 2026-01-01 **no line at all** (absent, not zero), because stamp duty was repealed by Law 239(I)/2025 (R-85, GS-44). Synthetic amounts are €2 / €1 below a synthetic threshold and €0.07 per receipt (AC-1, AC-2). |
| Motor fund levy | **5% of MTPL premium** (assumed): 300.00 gives 15.00. |
| Tax treatment | `REDUCE_PRO_RATA` for every credit |
| Reinsurance tax | Synthetic 1% line |
| Fiscal channel | No-op `NotRequired` |
| E-invoicing | Unbound |
| Bureau | File export with a different layout |
| Document language | English binding, Greek informative (a stub design choice to prove language is data; OI-MKT-09) |
| Postcodes and format | 4-digit postcodes; English-first formatting |
| Transliteration | Same ELOT algorithm with a different rule-set id |
| Numbering | Different prefixes and a check character |
| Payments | RF references only. Bank files add `CY-SYNTH-FIXED` fixed-width beside SEPA. Payee verification uses its own thresholds and `CloseMatch` rule. Card acquirer only. |
| Refunds and reinstatement | Distance withdrawal gives ProRata less a cost-of-cover deduction; reinstatement allowed within 30 days |
| Delivery proof | Email with read receipt for all notices (weaker proof level) |
| Other variations | Synthetic national regime lists, different ADR body and deadlines, a synthetic 10% renewal cap, one seismic scheme with two parallel versions, the Motor Insurers' Fund statement |

Cyprus clock values (all synthetic):

| Clock | Value |
|---|---|
| Distance withdrawal | 30 days; long stop 6 months |
| Non-payment notice | 15 business days |
| Complaint reply | 45 days |
| Refund due | 14 days |
| Renewal and non-renewal notices | 30 days each |
| Insurer termination | 30 days |
| Amendment acceptance | 21 days |
| MTPL payment | 15 days |
| Repair in kind | 30 days |
| MTPL assessment | 20 days |
| Auxiliary-equivalent remittance | Monthly, 20 days |

**Accepted no-op or same-as-Greece list:** FiscalDocumentChannel, RefusalDocumentRule, FriendlySettlementClearing, ClaimsHistoryFormat, IncidentReportingChannel, and FxRateSource.

**Bulgaria fixture** (`bg-fixture`, `test_only`, REQ-MKT-270–274):
- Cyrillic script ("Иван Петров" → "Ivan Petrov").
- EIK identifier of 9 or 13 digits.
- 2% premium tax plus a fixed per-vehicle levy.
- BGN→EUR changeover at 1.95583 on 2026-01-01; dual display from 2025-08-08 to 2026-08-08; 195.58 BGN converts to 100.00 EUR.
- bg-BG collation; BNB fixing source before 2026.

## 11. Controls (§12)

- **Authority types registered with PLT:**

  | Authority type | Dimensions | Typical holders |
  |---|---|---|
  | `MKT_CONFIG_APPROVAL` | layer, namespace, entity | Country PO; finance controller for `cur.*` and `fin.*`; DA for L0/L2 |
  | `MKT_PACK_ACTIVATION` | pack, entity, kind | — |
  | `MKT_IRREVERSIBLE_SWITCH` | — | Design authority |
  | `MKT_HOTFIX_COSIGN` | — | On-call design authority |
  | `MKT_CODELIST_PUBLISH` | — | — |
  | `MKT_ARCH_EXCEPTION` | — | — |
  | `MKT_GROUP_ENQUIRY` | entity set, purpose | — |

- **Maker-checker list (§12.3):**
  - legal-status changes to Settled (legal reviewer as checker);
  - terminology base changes;
  - configuration changes at L1–L4 and sensitive L5 keys;
  - capability switches (IRREVERSIBLE also need the DA);
  - pack activation and rollback;
  - SPI binding suspension;
  - code-list publication;
  - entity status changes;
  - cross-border authorisation changes;
  - changeover keys;
  - readiness waivers;
  - customer-facing translations (translator ≠ reviewer).
- **Segregation of duties:**

  | Rule | Conflict |
  |---|---|
  | SOD-MKT-01 | The maker cannot approve or co-sign their own change request |
  | SOD-MKT-02 | The release manager who schedules an activation cannot approve it |
  | SOD-MKT-03 | A pack code author cannot waive a failing gate for it |
  | SOD-MKT-04 | A translator cannot review their own customer-facing translation |
  | SOD-MKT-05 | Whoever accepts a changed expectation cannot be the CR maker, unless they are the named owner and a second checker approves |
  | SOD-MKT-06 | An exception requester cannot approve their own exception |
  | SOD-MKT-07 | Whoever proposes a Settled status or terminology change cannot approve it; the checker is the legal reviewer |

- **Audit (§12.1):** every CR action, retro approval, switch change, pack lifecycle step, activation and rollback, binding suspension, translation publish, gate run, terminology change, legal-status change, code-list publish, registry change, architecture exception, readiness waiver, cross-entity enquiry (with purpose code) and state export.
- **Architecture tests ARCH-01…11 (§14.x.3):**

  | Test | Fails when |
  |---|---|
  | ARCH-01 | Core compares country, jurisdiction or entity to a literal, or enumerates country codes in a conditional |
  | ARCH-02 | Core imports or reflects on a pack namespace |
  | ARCH-03 | Core contains Greek or Cyrillic literals outside resources, tests and fixtures |
  | ARCH-04 | Core tax or money paths contain currency or rate literals (e.g. `0.15m`, `0.018m`) |
  | ARCH-05 | An SPI has an untyped escape-hatch signature |
  | ARCH-06 | A calculation SPI performs I/O |
  | ARCH-07 | A flag key carries a country or entity qualifier |
  | ARCH-08 | Code reads an unregistered key |
  | ARCH-09 | A pack depends on module internals instead of the core SPI and public APIs |
  | ARCH-10 | A group service holds stamp database credentials |
  | ARCH-11 | Tax-treatment keys appear outside MKT, or `tax.treatment.*` is read outside TaxCalculator |

  Exceptions last 90 days at most, need DA approval, and are kept in a register (REQ-MKT-235).
- **Fork ratio** (REQ-MKT-236–237): warns at 12% and fails at 15% per pack. The build also fails if core country branches are greater than zero. The G6 target is 12% or less at motor MVP.
- **GDPR:**
  - No personal data in configuration, translations, fixtures or SPI logs (digests only); a PII scanner runs in CI (NFR-MKT-015, REQ-MKT-250).
  - Locale settings are never derived from nationality or protected characteristics (REQ-MKT-167).
  - One stamp per entity keeps controller boundaries clear.
  - Cross-entity enquiry is read-only, purpose-coded, masked by the user's permission in each entity, and audited in every stamp queried (REQ-MKT-158, P4).
- **Secrets:** rejected as configuration values; only key-management references are allowed (REQ-MKT-067).
- **Pack signatures:** verified at every load and activation. A failure raises a security incident (REQ-MKT-132).

## 12. AI features (§11)

All five features: registered in CMP (REQ-MKT-329); PLT toggles default **off**; called through the EU model gateway; no personal data; `AI-suggested` until a human acts; honour the kill switch. **None is needed for MVP** ("works without AI" is stated for each).

| ID | Feature | Classification | Toggle scope | Human decision |
|---|---|---|---|---|
| AI-MKT-01 | Configuration change impact explainer | Minimal risk | Tenant, entity, role | Checker approves or rejects in SCR-MKT-02. 5 s timeout. Hallucinated citations must be 0. |
| AI-MKT-02 | Translation draft assistant | Limited risk (Art. 50) | Tenant, entity, language, role | Translator, plus reviewer ≠ translator for customer-facing strings. Glossary adherence 98% or more. |
| AI-MKT-03 | Regulatory change → configuration drafter | **High-risk-level controls (uncertain)** by default rule §3.8.1 | Tenant, entity, key namespace, role | Maker row by row, then the normal preview and checker. Auto-disabled per namespace on a golden failure traced to an AI row. Post-acceptance correction rate below 5%. |
| AI-MKT-04 | Golden-suite divergence triage | Minimal | Tenant, role | Tester. Top-1 accuracy 70% or more. |
| AI-MKT-05 | Market-entry gap analyser | Minimal | Tenant, role | Market-entry lead (IB-31 agentic run stops at approval) |

The PRD notes that the evaluation datasets for AI-MKT-04 and AI-MKT-05 are not yet defined (§16.6).

## 13. Open issues / assumptions / CCRs (§16)

**Open issues**

| ID | Topic | Status |
|---|---|---|
| OI-MKT-01 | Greek IPT rates | **Closed** (Law 5177/2025) |
| OI-MKT-02 | Auxiliary Fund rate | **Closed** (6% ceiling); residual items moved to 17, then 19 |
| OI-MKT-03 | Directive 2025/2 dates | Open |
| OI-MKT-04 | Reg. 1103/97 article numbers | Open |
| OI-MKT-05 | Bulgarian dual-display primary text | Open (low priority) |
| OI-MKT-06 | Cyprus MIF 5% and stamp amounts | Open (only for CY hardening) |
| OI-MKT-07 | TIC check letter | Open |
| OI-MKT-08 | Greek B2B e-invoicing applicability | **Partly closed** (interface per D3); applicability decision open |
| OI-MKT-09 | Real Cyprus binding language | Open |
| OI-MKT-10 | PLT CI anchor | **Closed** (REQ-PLT-265) |
| OI-MKT-11 | Bulgarian transliteration tables | Open |
| OI-MKT-12 | Retention class | **Closed** (RC-CFG) |
| OI-MKT-13 | Fairfax rate source and timing | Open |
| OI-MKT-14 | Greek objection and non-renewal values | **Partly closed** (structure per D7); values open |
| OI-MKT-15 | Branch fiscal obligations | Open |
| OI-MKT-16 | Playbook effort calibration | Open |
| OI-MKT-17 | Auxiliary Fund readings | **Closed into OI-MKT-19** |
| OI-MKT-18 | Owner-verified articles (MID Art. 22, ELOT 743 detail, 1103/97) | Open |
| **OI-MKT-19** | **The single D2 tax and legal opinion** (see below) | Open; a **G2 go-live gate, not a build gate** |

OI-MKT-19 covers:
- the IPT liability point;
- the Auxiliary Fund split, components, stamp duty, base, mid-term base and refund;
- IPT on a distance-withdrawal void;
- endorsement credits and return premiums;
- fees in the base;
- whether the non-fiscal cover note may stay amount-free.

Closing it also closes OI-FIN-01, -02, -03 and -07, OI-BIL-03, OI-PFC-02 and -03, and OI-POL-08.

**Assumptions (§16.2):**
1. PLT provides the config runtime, workflow engine, integration hub, KMS and build pipeline.
2. Pack code ships with the core release train.
3. Only the Greek stamp is in production at MVP.
4. Contract §3.9.7 volumes hold.
5. Cyprus values are synthetic.

**CCRs:** CCR-MKT-01…11 are all accepted:
- 01–05: the six new SPIs (R-05);
- 06: events (R-02);
- 07: CI anchor, resolved by citing REQ-PLT-265 (R-06);
- 08: `convertCurrency(planId)` (R-08);
- 09: jurisdiction = risk location (R-09);
- 10: subdivision field (R-09);
- 11: new entities (R-01).

No new CCRs in v1.3.

**Ten pre-build decisions (§16.5):** decisions 1–6 and 8–10 stand as accepted MKT design stances. Decision 7 (Greek e-invoicing) is partly settled by D3. Decision 10 (Greek rounding order of operations) is listed as accepted, yet no concrete Greek value is given anywhere in the PRD.

**Risks:** RK-MKT-01…11. The top items:
- country logic leaking into core (High/High);
- a late or different D2 opinion (RK-MKT-09);
- the no-waiver translation gate blocking urgent releases (RK-MKT-10);
- modules keeping local refundability logic (RK-MKT-11).

## 14. Conflicts and ambiguities found

### (a) With the infra and stack (ARCHITECTURE-DECISIONS.md)

1. **Lakehouse.** Decision 4 (§1.2 line "product library, reference data, identity federation, lakehouse and group reporting span stamps"), REQ-MKT-155 ("lakehouse ingestion") and the REQ-MKT-157 example ("two stamps using the group lakehouse") all assume a group lakehouse. The ADR explicitly excludes "data lakehouses".
2. **Workflow engine.** §9.2 says "PLT workflow engine | Activation timers, changeover orchestration | REQ-PLT-007". §1.1 says PLT hosts the "workflow engine". The ADR excludes workflow servers and uses **Hangfire** for timers. This must map to Hangfire jobs over domain tables.
3. **Broker semantics.** §8 uses "Topic `mkt.events.v1`; partition key `stamp_id` … `pack_id`". REQ-MKT-150 and 160 say "event namespace" and "event stream namespaces … per stamp". This is broker or topic language, while the ADR uses a transactional outbox dispatched in order to in-process handlers. The "partition key" ordering guarantees need re-expressing as outbox ordering.
4. **Rules runtime.** REQ-MKT-122 and D10 require a "CEL-compatible" rule-expression language evaluated by a "PLT rules runtime". The ADR has no rules engine. A .NET CEL implementation would be a new third-party dependency, which needs a stated reason (ADR rule 11).
5. **ICU MessageFormat.** REQ-MKT-171 stores translations in ICU MessageFormat, and REQ-MKT-336 needs server-side rendering of message keys. The ADR front end uses **react-i18next**, whose native format is not ICU (it needs the i18next-icu plugin). .NET has no built-in ICU MessageFormat, so a library or custom formatter is needed. XLIFF 2.0 import and export is also required (Should).
6. **CLDR pinning.** The CLDR version is pinned in the hash (REQ-MKT-170, 046), and formatting is CLDR-based on both server and UI. .NET on Linux uses system or app-local ICU and browsers use their own Intl/CLDR data, so one pinned CLDR version across server, browser and documents (Gotenberg/Chromium) is not achievable by default. A decision is needed (app-local ICU for .NET, a bundled formatter in the UI, or a narrower scope).
7. **RFC 8785 JSON canonicalisation** for the hash (REQ-MKT-046) is not in the .NET BCL. It needs a dependency or an in-house implementation with golden vectors.
8. **Runtime pack loading and old code.** REQ-MKT-142 (Should) needs a "reproduction service that loads historical pack versions in an isolated sandbox". REQ-MKT-120 needs SPI majors N and N-1 at once. REQ-MKT-132 and 133 verify signatures and compatibility "at load". These sit uneasily with decision 1 ("pack code ships with the release", no runtime code loading, §16.5 decision 1) and the single-deployable monolith. Old pack code is not in the current release, so reproducing it needs AssemblyLoadContext or archived builds. This needs a decision.
9. **"Pack loader", signing and "registry".** These imply a separate artefact repository and runtime signature verification of compiled code, beyond GitHub Actions build signing. Data-only packs (REQ-MKT-145) also imply a data-artefact pipeline that is separate from the code release.
10. **Stamps versus the single deployment.** The ADR assumes one Azure environment. MKT requires silo stamps per regulated entity with separate keys, secrets, identity clients, event namespaces and network segments (REQ-MKT-160), plus group services as separate deployables (REQ-MKT-155). This is compatible in principle (multiple Container Apps environments) but is not reflected in the ADR or INFRASTRUCTURE. P1 has one stamp only.
11. **UI language storage.** The UI language "persisted in the PLT identity profile for both realms" (REQ-MKT-005, 333). Entra ID / External ID does not naturally hold an app preference, so this is presumably an app-side profile table. Ownership needs clarifying.
12. **Vendor names.** These appear only in sources (Guidewire S20, SAP CAP S22, AWS S23) and do not affect the build.

### (b) With the contract or other PRDs

- **Who owns resolve.** REQ-MKT-001 makes `mkt.Configuration.resolve` and `explain` the only public operations "the PLT configuration runtime implements". §7 places ConfigValue, ConfigState and related entities in the `mkt` schema, while PLT owns the runtime and caching. It is unclear which module's code computes merges and hashes, and how PLT reads MKT tables without breaking ADR rule 8 ("never reads another module's tables").
- **Contract SPI list.** ADR rule 10 lists IdValidator, TaxCalculator, FiscalDocumentChannel, BureauAdapter and StatutoryClockSet. All five are present.
- **Golden-suite dependencies.** The golden suite (REQ-MKT-008, §14.x.1) runs quote, bind, change, cancel, renew, claim and pay steps across RAT, POL, BIL, CLM, CMP and DOC. MKT therefore depends on almost every module to satisfy its own Must P1 gate. It also hosts the E2E-01…12 programme scenarios (D9).
- **Activation dependency.** REQ-MKT-255 and 343 block production GR activation until 17 motor-path rows owned by other modules (CMP, CLM, BIL, CHN, PTY, DOC, FIN, PLT) are Settled. The go-live gate is cross-programme.

### (c) Internal contradictions and ambiguities

- **"Exactly one axis" versus two-axis rows.** REQ-MKT-085 says "each SPI declaring exactly one axis", but §9.4.0 lists two axes for several rows:
  - StatutoryClockSet "CONTRACT_LAW_JURISDICTION or per clock";
  - FiscalDocumentChannel "LEGAL_ENTITY_HOME / BRANCH_LOCATION";
  - IntermediaryRegister "LEGAL_ENTITY_HOME / host";
  - AddressFormatter "RISK_LOCATION / address country";
  - ComplaintRules "POLICYHOLDER_RESIDENCE (+home)".
  
  The DOCUMENT_LANGUAGE axis is declared but no SPI uses it.
- **Auxiliary Fund status.** REQ-MKT-256 acceptance shows "UNVERIFIED rate status", while REQ-MKT-322 and GR-02 say the 6% ceiling is verified.
- **External-call list incomplete.** REQ-MKT-118 omits Geocoder.geocode, PaymentChannelProvider.createCollection, MotorCompensationBodyAdapter, IncidentReportingChannel and ESignatureProvider.sign, though §9.4 marks them external, asynchronous or keyed.
- **Pure-SPI list incomplete.** REQ-MKT-117 omits several SPIs that §9.4 marks pure, such as PolicyLifecycleRules, StatutoryDeliveryRule, ConsentRules, MandatoryWordingSet and ClaimsHistoryFormat.
- **Missing switch.** REQ-MKT-072 lists a "billing sub-ledger reconciliation basis" IRREVERSIBLE switch that is absent from the §10.2.3 catalogue.
- **P4 items in a P1 family.** The REQ-MKT-006 anchor's "Must family" includes REQ-MKT-201–203, which are Must **P4**, while the anchor is P1.
- **EU-layer clock list.** REQ-MKT-287 lists three EU-layer defaults, but §10.4.3 adds a fourth (`CLM_HISTORY_STATEMENT` 15 days) that cites a Greek law (5113/2024 Art. 7) as the EU-layer source.
- **Time-basis spelling.** §10.2.2 abbreviates time bases ("PROCESSING", "EFFECTIVE", "TAX_POINT") against the enum names in REQ-MKT-043. `l10n.languages` is marked "ADDITIVE" alongside a core default of [en].
- **Withdrawal example.** REQ-MKT-309 uses premium 420.00 "of which IPT 54.78" (15% of 365.22). That is consistent for IPT, but the example omits the Auxiliary Fund levy that an MTPL premium would carry.
- **Stale assumption.** AC-9 (§3.3) says the Greek values in §10.4.3 are UNVERIFIED, but several are now marked verified (Act 87/2016 Art. 6, Law 5317/2026 Art. 71). AC-9 is stale.
- **Hash churn.** The translation bundle version is part of the hash (REQ-MKT-046, 172). Every translation publish therefore creates a new configuration state and hash in every stamp. That is intended, but it means a high rate of hash churn and `ConfigurationActivated` events.
- **Missing requirement for RateTableChanged.** The event is produced (§8.1) but no requirement defines when a "rate table" exists as distinct from config values.

### (d) Cannot be built without a decision

1. Where the merge, hash and resolver code lives (MKT versus PLT), and how PLT caches MKT data under ADR rule 8.
2. How packs are packaged in .NET: separate assemblies per pack, how "signing at load" is done, how N-1 SPI majors and historical reproduction are hosted, and how data-only pack versions are published and stored.
3. CEL library choice (or deferral) for D10 rule expressions.
4. ICU MessageFormat on both server and react-i18next; CLDR pinning strategy.
5. Greek rounding order of operations (§16.5 decision 10). No Greek value is given; only the core default exists.
6. Event "partition" ordering semantics on the outbox, and the "event namespace per stamp".
7. The D2 opinion (OI-MKT-19) and the D7 clock values. These do **not** block the build (they are guarded pack switches) but they block G2 go-live.
8. Fork-ratio LOC measurement in a .NET solution: which projects count as "pack" and which as "core".

## 15. Build notes

**Hardest parts**
- The **content-addressed configuration state**:
  - a Merkle manifest with RFC 8785 canonicalisation;
  - bitemporal non-overlapping values (PostgreSQL exclusion constraints);
  - deterministic multi-layer resolution with four merge types, finals and constrained ranges;
  - per-unit-of-work hash pinning;
  - atomic cache switch-over across nodes within 1 s;
  - p95 of 2 ms or less for 200 keys;
  - reproduction of any past hash for 10 or more years.
  
  Property tests P-01…P-08 and NFR-MKT-013 integrity checks follow from this.
- **TaxCalculator with `treatment`** and the D2 rule package. This is money code, so it needs golden, property and mutation tests and conformance vectors, and BIL and FIN must validate against it by rule id.
- **The golden-suite runner.** About 50 cross-module scenarios on two packs plus a fixture, run twice per build for determinism in 20 minutes or less. It is reused for impact previews (RAT shadow re-rating of 500 policies) and certification. It effectively needs the whole system.
- **Pack lifecycle:** signing, compatibility checks, certification gates, per-entity activation, rollback events with affected windows, and data-only activation.
- **Translation store and the no-waiver completeness gate** across every module (up to 60,000 keys), plus immediate language switching on both shells.

**Must exist first**
- PLT basics: maker-checker (REQ-PLT-004), audit, outbox, authority, time service, identity.
- The MKT key registry with code-declared descriptors and resolver. Every module's configuration reads depend on it.
- `mkt.Spi.bind` with the gateway and error model.
- The ARCH tests (NetArchTest) from day one.
- `mkt.Rounding.apply`, because RAT, BIL, FIN, CLM and RI call it.
- The translation-entry registration build step, because every UI depends on it.

**Suggested slicing**
1. **Key registry and descriptors**, plus resolve with REPLACE and final. Six layers, effective dating, time basis. Hash v1 (flat manifest). Hash on the event envelope. ARCH-01…04 and 08.
2. **Change workflow** with maker-checker, audit, scheduling (Hangfire), atomic activation, `ConfigurationActivated`, pinning, explain and compare. Then ADDITIVE, CONSTRAINED, MAP_DEEP and the property tests.
3. **SPI framework:** catalogue, binding table and axes, gateway, error model, conformance-kit harness. Then the pure SPIs needed for motor: IdValidator (AFM, VEHICLE_PLATE), NameTransliterator, AddressFormatter, NumberingScheme, StatutoryClockSet, DocumentLanguageRule, RegimeCodeList, PricingConstraint, PolicyLifecycleRules.
4. **Rounding and currency services; TaxCalculator** `calculate` and `treatment` for GR and CY with D2 Pending switches. TCK-TAX-NET-REFUND.
5. **Pack manifest and lifecycle:** registry, certification checks (`pack_required`, clocks, translations), activation, rollback, the legal-status gate (REQ-MKT-343), the `eu`, `group`, `gr`, `cy-stub` and `bg-fixture` packs as data. Capability switches.
6. **i18n:** translation store, bundles, UI-language resolve, completeness gate, terminology base, search keys and case mapping, pseudo-localisation.
7. **External SPIs** through adapters (myDATA via CMP, Information Centre, MotorDataProvider and wallet, RegistryLookup, IntermediaryRegister, PayeeVerification, bank files), aligned with consuming modules.
8. **Golden-suite runner and fork-ratio metric** grown incrementally as modules land. Impact preview once RAT shadow runs exist.
9. **P4 later:** cross-border register and checks, cross-entity enquiry, group bundles and multi-stamp, market playbook (SCR-MKT-11, SCR-MKT-12), changeover activation.
