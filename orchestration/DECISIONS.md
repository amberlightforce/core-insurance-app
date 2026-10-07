# DECISIONS — assumptions and rulings by the orchestrator

Each entry gives the decision, the reason, the PRDs affected, and its status (**Made** = I made the call under my
mandate; **Ask** = waiting for the user; **Open-Reg** = regulatory value not settled, so it is never invented).
Precedence: infra specs (ARCHITECTURE-DECISIONS.md, INFRASTRUCTURE.md) > 00-system-contract v1.12 + PRD-18 decisions
(D1–D10) > module PRDs > design guide prose > baseline inventory. The design guide's generated tokens and the v3 mockup
win over Parts 3–4 prose where they disagree (design README: v3 approved).

## A. Programme and process

| ID | Decision | Reason | Affects | Status |
|---|---|---|---|---|
| D-PRG-01 | Output repository is `C:\Users\Karl\Projects\coreinsurance\core-insurance-app`; orchestration files live in `/orchestration` | The instruction's path was a placeholder; I used the example given | all | Made (confirm Q1) |
| D-PRG-02 | At most **4** concurrent builder agents | The instruction's N was a placeholder; I used the example given | all | Made (confirm Q1) |
| D-PRG-03 | Build target = **Motor MVP cut (Must P1, 3,710 + 9 D6 promotions − bancassurance demotions)**. P2–P4 Musts, Shoulds and Coulds are not built unless needed by a P1 Must | PRD-18 §16, D6, D9 | all | Made (scope confirm Q2) |
| D-PRG-04 | Build order = PRD-18 W1–W9 capability waves, not the contract §3.1.3 six writing-waves | D9 makes W1–W9 the build baseline | all | Made |
| D-PRG-05 | A wave is executed as work packages of about 30–50 Musts along PRD capability groups; one agent per WP | A whole module (≈200 Musts) cannot be built and tested in one agent session | all | Made |
| D-PRG-06 | PRD-18 is cross-cutting: the orchestrator owns XMR-FR-250 (E2E-01…12) and uses PRD-18 §8/§9 as the event catalogue and state models of record (D5). It is not a feature module | PRD-18 is a synthesis document | PRD-18 | Made |
| D-PRG-07 | Forward dependencies are met interface-first: Phase 1 publishes every cross-module operation/event as a contract, a contract test and a sandbox double | PRD-18 §16.3 | all | Made |
| D-PRG-08 | AI features: none built before W8; all ship `Ai__Enabled=false` | Contract §3.8, infra §5, PRD-18 wave rule 4 | all | Made |
| D-PRG-09 | Migration plans on scenario B (convert at renewal), with a synthetic legacy simulator; real legacy work is out of reach | D6; no legacy source available | PRD-16 | Made |
| D-PRG-10 | Bancassurance out of P1 (bank identity design kept); annual terms only; motor RI is excess-of-loss only (A-01) | D6 | 01, 08, 12, 14 | Made |

## B. Architecture translation (infra overrides PRD wording)

| ID | Decision | Reason | Affects | Status |
|---|---|---|---|---|
| D-ARC-01 | Single deployable (`api`/`worker`/`migrate` by `APP_ROLE`) on Azure Container Apps. "Adapter host", "integration hub", "nodes", "separate failure domain" all become worker-hosted adapters with WireMock doubles. "Stamp" = one resource group per legal entity (infra §8) | Infra §1, ADR | 03, 10, 11, 14, 16 | Made |
| D-ARC-02 | Events: transactional outbox (`plt.outbox_message`) → dispatcher in the worker → in-process idempotent handlers (`plt.processed_event`), dead-letter table, replay command, event archive table (replay window separate from outbox purge). Broker vocabulary maps as: topic → event-type namespace; partition key → ordering key (`aggregateId`, gap-free `aggregateSequence`); consumer group → handler registration; schema registry → JSON Schemas in `contracts/events` checked in CI. Phase 1 load-tests the ≥2,000 ev/s target (D8) | ADR "no message brokers" | all §8, contract §3.4, 14, 15 | Made |
| D-ARC-03 | Identity = Entra ID (staff) + Entra External ID (customers/brokers/later bank). PLT keeps `user_profile` (language R-101, legal entity, delegations), RBAC/ABAC, authority framework and maker-checker. It does **not** build credentials, passkeys, an own token service or SCIM. OAuth features Entra does not offer natively (RFC 8693 token exchange with `act`, DPoP, FAPI 2.0/PAR, certificate-bound partner tokens, MCP client metadata) are recorded as **deviations**: partners use client credentials with certificate auth on Entra, and agent "act-as" is modelled as an application permission + audited delegation record | ADR "we never build our own identity system" | 12, 14 (≈45 Musts re-scoped) | Made |
| D-ARC-04 | No workflow engine. Long-running processes = explicit domain state machines + deadline rows; Hangfire runs **recurring scanners** that pick due rows (`FOR UPDATE SKIP LOCKED`) plus delayed jobs for short timers. Statutory clocks live in CMP tables (~2M live clocks; no job per clock). "Durable business-day timers" = deadline computed with the PLT calendar service and stored | ADR | 02, 06, 09, 10, 11, 13, 14, 16, 17 | Made |
| D-ARC-05 | PostgreSQL **17**: no-overlap effective dating via `btree_gist` exclusion constraints; PERIOD foreign keys are checked by constraint triggers; UUIDv7 generated in .NET (`Guid.CreateVersion7`) | ADR pins PG17 | 05 (REQ-POL-079, NFR-POL-020), 01 | Made |
| D-ARC-06 | No lakehouse. Bronze/silver/gold → `rpt_raw`/`rpt_conformed`/`rpt_mart` + `dat` metadata schema. CDC is replaced by outbox event ingestion plus producer **control-total** feeds over contracts (no reading other schemas). Near-real-time operational marts use incremental event handlers; heavy marts build nightly. Feature store / ML runtime / actuarial workspace are deferred with AI (W8, off). `Lakehouse*` event names keep their catalogue name (stable contract) and mean "report marts" | ADR rule 8, "no lakehouse" | 15 (≈50 Musts reworded), 01, 08, 09, 11 | Made |
| D-ARC-07 | Documents: in-house template language + composer produce HTML; Gotenberg paginates to PDF; .NET post-processing adds PDF/A-3 embedded payload, tagging, deterministic metadata and the seal. A Phase 1 spike proves PDF/A-3a + byte-reproducibility + missing-glyph failure. Any PDF library must have a permissive licence (iText/AGPL is excluded) | ADR mandates Gotenberg; CD-20 asks in-house | 10 | Made (spike result reported) |
| D-ARC-08 | Availability, RPO/RTO and throughput: infra Stage 2 targets (RPO 15 min, RTO 4 h, one EU region) are binding. Stricter PRD NFRs (5 min/2 h, 99.9% tiers, 400–600 ratings/s, 80k docs/h) are recorded as production-hardening items and performance-tested at Stage 2 sizing, not Stage 1 | Infra is binding on hosting | 03, 05, 07, 10, 12, contract §3.9.6 | Made |
| D-ARC-09 | Outside production only synthetic data; MIG full-volume rehearsals with real data run inside the production stamp (they are not built now) | Infra rule 12 | 16 | Made |
| D-ARC-10 | Rule language: one in-house **CEL-subset evaluator in C#** (pure, no I/O), shared by PFC/UW/PLT/BIL/FIN/MKT, plus versioned decision tables in `plt`. Built in Phase 1f as a shared library | D10 "in-house"; ADR minimal dependencies | 02, 03, 04, 06, 09, 14, 17 | Made |
| D-ARC-11 | Cross-replica cache invalidation (configuration, flags, AI kill switch ≤60 s) uses PostgreSQL `LISTEN/NOTIFY` + short TTL; rate limiting uses ASP.NET Core rate limiter with partitioned state in PostgreSQL. No Redis | No cache service in the stack | 12, 14, 17 | Made |
| D-ARC-12 | Canonical JSON (RFC 8785) + SHA-256 for configuration/artefact hashes: small in-house canonicaliser with test vectors | .NET has no built-in | 02, 17 | Made |
| D-ARC-13 | Geospatial (PostGIS) is not needed in P1 (cat polygons / accumulation are P2+); deferred | Not in extension list | 07, 08 | Made |
| D-ARC-14 | Field-level encryption of P2/P3 identifiers and IBAN: envelope encryption with Key Vault key per legal entity + HMAC blind index for equality search; full-text over encrypted health notes is **not** offered (search indexes the non-sensitive summary only) | Contract §3.9.12; WRK search conflict | 01, 06, 07, 13 | Made |
| D-ARC-15 | Audit: insert-only `plt.audit_event`, hash-chained per day partition, periodic export to immutable Blob; qualified trust-service seals deferred until OI-PLT-10 is decided | Infra §9 | 14 | Made |
| D-ARC-16 | Module calls are in-process via `*.Contracts` interfaces. The "highest availability tier" sync calls (UW.evaluate, PolicyHold.check) are just in-process calls. A module never calls a module from a later wave synchronously on the bind path (R-83) | Modular monolith | all | Made |

## C. Front end and design system

| ID | Decision | Reason | Affects | Status |
|---|---|---|---|---|
| D-FE-01 | Extra front-end dependencies approved with stated reasons: TanStack Table + Virtual (column pinning/grouping, virtualised queues; React Aria Table lacks them), Motion (design-system motion specs), lucide-react (icon set named by the guide, ISC), visx (charts per §6), pdfjs-dist (document preview), libphonenumber-js (phone validation), Storybook (component docs), Stylelint + `@axe-core/playwright` (token and a11y gates). Fonts Inter / Noto Sans / JetBrains Mono self-hosted (OFL). No map tiles in P1 | ADR rule 11 | design system, all UIs | Made |
| D-FE-02 | i18n = react-i18next + `i18next-icu` (ICU plural/select); dates and numbers via `Intl` / React Aria `useDateFormatter`/`useNumberFormatter`. The guide's `react-intl` samples are translated | ADR pins react-i18next | design §8/§11 | Made |
| D-FE-03 | Sign-in through MSAL React; Container Apps built-in auth is only the outer gate in Stage 1 | ADR | design §5.12/§11.10, SM-08 | Made |
| D-FE-04 | v3 gradient-border white button is the primary CTA everywhere; Parts 3–4 references to solid blue `gradient.cta` (SM-01 morph, mobile FAB, §11 token excerpts) are superseded by `tokens/aegean.css` | Design README: v3 approved | design guide | Made |
| D-FE-05 | Monetary figures never animate (no count-up on money, including commissions payable); count-up is allowed only on non-financial counts and not on first render | Rule C-07 + SM-08 (safer reading) | design guide, CHN/PTY dashboards | Made |
| D-FE-06 | Fix stale hover/sheen tokens: checkbox/switch hover uses the accent hover token; split-button divider uses `--border-strong`; MI-67 sweep uses `rgba(63,99,240,.12)` as in v3 | Stale after v3 | design system | Made |
| D-FE-07 | Table header 38 px; add tokens `--size-row-queue-{compact,default,comfortable}` = 60/64/72 px; Greek upper-casing only via `toGreekUpper()`; no raw colours (mockup's `#C81E1E`/`#A86A0C` mapped to status tokens); breakpoints from `bp.*` tokens; shell adds the «?» help button, skip link and `lang` | Guide internal inconsistencies | design system | Made |
| D-FE-08 | Design-system custom components are built where React Aria has no primitive: disabled-with-reason button wrapper, currency input (live grouping, `12k` shorthand, reject extra decimals, no ↑/↓), date field accelerators («σήμερα», `+30`), multi-select combobox with Greeklish matching, hover card, context menu, non-modal sheet/drawer, stepper, @mention composer | DESIGN-A gap list | design system | Made |
| D-FE-09 | Custom insurance icons (18) and illustrations ILL-01…10 start as placeholders from lucide + simple SVG and are listed as known stubs | Not drawn yet | design system | Made |

## D. Cross-PRD consistency rulings

| ID | Decision | Reason | Affects | Status |
|---|---|---|---|---|
| D-CON-01 | Lineage uses business keys (quote, job, transaction, charge, invoice, journal IDs) carried in the event envelope `businessKeys`; `correlationId` is the W3C trace only | D5, F-001 | all, E2E suite | Made |
| D-CON-02 | PolicyTerm states add `Voided` and `Rewritten` (terminal), and `Cancelled → InForce` via rescission, to match PolicyVoided/PolicyRewritten/CancellationRescinded events | Events exist without states | 05 | Made |
| D-CON-03 | Clock kinds = DEADLINE, WAITING_PERIOD, FIXED_DATE (D7 adds FIXED_DATE) | R-78 + D7 | 11, 05, 06, 07 | Made |
| D-CON-04 | An exposure may close when its clocks are Met, Cancelled or **Breached-and-acknowledged** (breach stays recorded) | REQ-CLM-072 would block closure forever | 07 | Made |
| D-CON-05 | A PTY account is created as `Pending` (state model §7.3 wins over the REQ-PTY-004 example) | State model is normative | 01 | Made |
| D-CON-06 | Unmerge restores the party's prior status exactly (REQ-PTY-139 wins over the state diagram) and Prospect → Merged is allowed (REQ-PTY-134) | Requirement text wins over diagram | 01 | Made |
| D-CON-07 | MIG renewal conversion end states = Converted, Excluded, Withdrawn, NotConverted; Failed is non-terminal; the count equation uses the terminal set | §7.3.5 model is consistent; equation updated | 16 | Made |
| D-CON-08 | Job: Scheduled is reached from Bound (future-dated issuance) only | Least-surprise reading; Quoted→Scheduled would bind without gates | 05 | Made |
| D-CON-09 | Every catalogued consumer must have a handler or be removed from the consumer list; `origin` added to the envelope; AiSystemStatusChanged and WithdrawalRequestReceived added to the catalogue | R3-002, contract gaps | all | Made |
| D-CON-10 | `RefundDisbursed` fires when the bank **accepts** the payment file (ISSUED, Greek default per REQ-BIL-190/BR-BIL-126); a later `RefundCleared` fires on statement confirmation. POL's 30-day withdrawal refund clock stops on RefundDisbursed | Requirement text over diagram; earlier event protects the consumer clock | 06, 05, 12 | Made |
| D-CON-11 | Tax charges posted by POL are computed on the prorated, rounded premium (REQ-RAT-162 path); the annual tax in the rating worksheet is informational | One posting rule | 03, 05, 06 | Made |
| D-CON-12 | FIN LRC premium basis default = PREMIUMS_RECEIVED (configuration default wins); golden fixture GF-02 is corrected to match, and both bases get tests | Config default is normative | 09 | Made |
| D-CON-13 | RI P1 includes **manual** settlement proposal + approval under `RI.SETTLEMENT` (REQ-RI-193 path promoted to Must P1) so the P1 execution requirements are reachable | P1 Musts otherwise unreachable | 08 | Made |
| D-CON-14 | Renewal job is created at expiry − (notice lead + 15 days preparation); the notice deadline stays expiry − pack lead. Both leads are pack configuration; the 45-day value remains Unverified (OQ-004) | REQ-POL-245 vs -318 conflict | 05 | Made |
| D-CON-15 | User search owned by PLT (admin) and WRK (global search) as two separate screens; FNOL has one CLM intake service with CHN and WRK as channels | Baseline inventory overlaps | 07, 12, 13, 14 | Made |
| D-CON-16 | Phase mismatches where a P1 Must depends on a later-phase item (RI-248/083, RI-150/149, FIN-142/140, FIN-038/166, UW-070/008, POL-010/286/304, MKT-326→DAT-128/CMP-181): build the minimum P1 subset of the dependency needed for the P1 Must and record it in the WP report | F-372 | 04, 05, 08, 09, 15, 17 | Made |
| D-CON-17 | REQ-UW four-eyes thresholds and authority matrices: ship as configuration with core default **off**, BR-UW-012 values as GR-pack proposal flagged Unverified | §10.2 vs BR-UW-012; OI-UW-08 | 04 | Made |
| D-CON-18 | System-created claim payment sets (fast-track, recurring instalments, auto-release) record the system as maker under a named configuration owner; the configuration approval is the four-eyes check | Maker undefined | 07 | Made |

## E. Regulatory rules (never invented)

| ID | Decision | Affects | Status |
|---|---|---|---|
| D-REG-01 | Every tax/levy/clock/format value lives in pack configuration with `legalStatus` (Settled / Unverified / Draft) and a source citation to the PRD requirement that states it. Values not stated in a PRD are created as **empty keys** that fail closed | all | Made |
| D-REG-02 | Production activation of the GR pack refuses non-Settled motor-path values (REQ-CMP-264, REQ-MKT-322). Non-prod environments can run Unverified values for testing | 11, 17 | Made |
| D-REG-03 | Golden tests on amounts that depend on Unverified values are "guarded" (assert structure, not amount) until the D2 legal opinion lands; the E2E scenarios follow PRD-18 §17.1 "blocked expectations" | 03, 06, 09, E2E | Made |
| D-REG-04 | Values used as stated in PRDs: IPT 15% (general) / 20% (fire) (PRD-17 GR pack); Auxiliary Fund ceiling 6%, split 70/30 (PRD-17, split still OQ-010); MTPL minimum €1,300,000 BI per injured party and €1,300,000 PD per accident, HICP-indexed (PRD-02); non-payment 1 month (PRD-05/06); MTPL reasoned offer 3 months, assessment 15/25 days, payment 10 days from proven offer delivery, repair in kind 20 days, claims-history certificate 15 days/≥5 years (PRD-07, D7); complaints 50 days (PRD-11, secondary source, Unverified); distance withdrawal 14 days + full refund within 30 days (PRD-05, Law 5317/2026 Art. 72); A.1004 by 10 January; ENFIA confirmation 28 February; DORA initial 4 h / 24 h cap, intermediate 72 h, final 1 month; GDPR breach 72 h; Solvency II 5/14 weeks. The pre-2025 IPT table (fire 20 / life 4 / other 15) quoted in PRD-11 is **not** used | all | Made |
| D-REG-05 | Cyprus stub pack uses only the synthetic values PRD-17 defines (CY-STAMP synthetic, 5% motor fund levy "assumed") and is CI-only | 17 | Made |

## F. Open questions for the user (blocking or scoping)

| ID | Question | My recommendation | Blocks |
|---|---|---|---|
| Q1 | Confirm the output repo path and the max concurrency of 4 | Keep both | Phase 1 start |
| Q2 | Scope: build the full Motor MVP cut (≈93 WPs, multi-month), or first deliver a **thin walking skeleton** (Phase 1 + a narrow vertical slice of E2E-01: one motor product, quote → bind → invoice → payment → journal) and then widen wave by wave? | Phase 1, then a walking skeleton through W1–W5 capabilities, then fill waves in order | Phase 2 shape |
| Q3 | Mobile app (PRD-12): PWA on the same React stack, React Native, or native? Infra names only React web | PWA in P1 (offline FNOL via service worker; push via Web Push); native later | W4/W6/W7 CHN |
| Q4 | Product authoring "through Git" with a self-hosted Git + CI inside the EU stamp (PRD-02, OI-PFC-06) — infra has only GitHub Actions for app CI | Store product versions in `pfc` with in-app item-level diff/three-way merge and YAML export/import; no second Git service | W2 PFC |
| Q5 | Malware scanning and OCR for inbound documents (Must even with AI off): which service? | Microsoft Defender for Storage malware scanning (Azure-native, a ClamAV container locally); OCR off in P1 (manual keying) until an AI/OCR service is approved | W2 WRK, PLT-161 |
| Q6 | Is there a GitHub remote for the repo (CI runs on GitHub Actions)? Without one, CI runs locally only | Create a private GitHub repo; until then, `git init` locally and run the same workflow via a local script | Phase 1a |

## G. Regulatory open questions carried (not blocking the build; go-live gates via D2/D7)
OQ-001(b) proof media for statutory notices · OQ-004 renewal lead value · OQ-008 16 unverified clock values (incl. 16-day
MTPL third-party notice, 15-day insurer termination, FS 10-business-day reply, objection, renewal/non-renewal leads,
myDATA transmission deadline, bureau report deadline) · OQ-010 Auxiliary Fund levy split · OQ-011 IPT liability point,
IPT on credits/withdrawal voids, fees in IPT base, levy refund on cancellation · stamp duty rate (not stated anywhere) ·
OQ-012 myDATA XSD version, document types, E3 codes (561_003 vs 563_003), B2B e-invoicing applicability · OQ-014
non-fiscal cover note legality · OQ-017 Information Centre method/format/deadline · OQ-018 Friendly Settlement limits
(€6,500/12,000/30,000 vs €5,000/15,000) and spec · statutory interest rate (OI-CLM-03) · nat-cat refusal regime
(OI-UW-01) · Law 2496/1997 UW windows (OI-UW-02) · EIOPA taxonomy 2.10 template set, A.1004/EAEE/BoG layouts
(OI-DAT-01…05) · Greek retention durations (OQ-026) · reinsurance premium tax exemption (OI-RI-01) · gov.gr Wallet and
co-signing access (OI-UW-04, OI-DOC-05) · BoG DORA channel (OI-CMP-09) · SMS sender registration (OI-DOC-04).

## H. User answers (2026-10-07)

| ID | Answer | Effect |
|---|---|---|
| Q2 | **Full cut, waves in order** (W1 → W9 per PRD-18) | D-PRG-03/04 stand; no walking-skeleton milestone. A runnable E2E-01 appears at the end of W5 |
| Q3 | **Defer mobile**: responsive web only in P1 | CHN mobile-app-specific Musts (offline FNOL, on-device model, push, dynamic type) are deferred and listed as deviations in CHN WP reports. D-USR-01 |
| Q4 | **In-app product versioning** in `pfc` with item-level diff / three-way merge and YAML export/import; no self-hosted Git | PRD-02 Git-authoring requirements are reinterpreted onto the DB store. D-USR-02 |
| Q5 | **Defender for Storage malware scanning (ClamAV locally); no OCR in P1** | WRK-267 OCR is deferred (manual keying); PLT-161 is met by Defender/ClamAV. D-USR-03 |
| Q1, Q6 | Not yet answered | Still defaulting to repo `core-insurance-app`, concurrency 4, local git |

## I. Additions from the PRD-18 part A digest

| ID | Decision | Reason | Affects | Status |
|---|---|---|---|---|
| D-CON-19 | Event set-completeness fields follow contract D4 (`set_id`, `set_size`, `index`), not PRD-18's proposed `transaction_delta_count` / `aggregate_member_index` | The contract decision wins | all events | Made |
| D-CON-20 | BIL money source `TAX_REMITTANCE` covers IPT, stamp duty **and** the levy (contract D1), not PRD-18 §5.2's narrower reading | Contract decision wins | 06, 09, 11 | Made |
| D-CON-21 | Builders apply the decided versions: BIL sources `FS_CLEARING`, `CMP_REDRESS`, `TAX_REMITTANCE`; events `DisbursementRejected` and `DisbursementStopped`; `TaxCalculator.treatment`; the four new statutory clocks | Accepted at freeze | 06, 07, 11, 17 | Made |
| D-REG-06 | The Auxiliary Fund levy split is **not hard-coded anywhere**. PRD-17 says 4.2% / 1.8% and REQ-FIN-190 says 4.5% / 1.5%, so both are kept out of code and the pack key stays `Unverified` until OQ-010 is answered | The PRDs contradict each other; never invent | 02, 06, 09, 17 | Open-Reg |
| D-REG-07 | The retention schedule has no durations (F-329). Retention is built as configuration with empty values that block purge, so legal hold and DSAR are testable with synthetic durations in non-prod only | Never invent | 14, 11, 15 | Open-Reg |
| D-PRG-11 | All PRDs are treated as citing contract v1.12 (the frozen version), whatever version their document-control cell gives | Freeze record | all | Made |

## J. Backlog (2026-10-07)

| ID | Decision | Reason | Status |
|---|---|---|---|
| D-PRG-12 | The backlog is 134 WPs: 6 foundation, 116 feature (3 deferred under D-USR-01/03), 12 E2E. It covers 3,711 Musts: 3,710 + 9 D6 promotions + REQ-RI-193 − 9 bancassurance demotions (REQ-PLT-057, REQ-PTY-253/254/255/257, REQ-CHN-206/207/209/214). REQ-CHN-042 stays Must (only its bank part is out) | Every Must is placed exactly once | Made |
| D-PRG-13 | E2E-12 (pack rollback after bound business) is scheduled in W5, not W1. W1 only delivers its harness | The scenario needs POL bind (W4) and BIL/FIN handling | Made |
| D-PRG-14 | Tracker: `orchestration/backlog/backlog.json` is the source of truth. `orchestration/tracker/export_tracker.py` exports it, and the orchestrator pushes it to the tracker artifact database on every status change | Simple, auto-updating view for the user | Made |

## K. Event catalogue rulings (F-1c)

| ID | Decision | Reason | Status |
|---|---|---|---|
| D-CON-22 | Ordering keys follow the producer PRD where it already applied PRD-18's own fixes (F-117/R3-005): PartyUnmerged → survivor `party_id`; LevyAccrued/LevyRemitted → `levy_period_id`; ConfigChanged → `resolution_context_id`; ClaimsHistoryCertificateIssued → `certificate_id` | These are later corrections of the same catalogue | Made |
| D-CON-10a | **Amends D-CON-10:** no new `RefundCleared` event. Bank-statement clearing of a refund is signalled by the existing `DisbursementCleared` | Avoid inventing an event when one already covers it | Made |
| D-CON-23 | Consumer lists are trimmed to modules whose own PRD §8 declares a handler: 143 pairs removed, 44 added, 7 kept by PRD evidence, all recorded in `contracts/events/catalog.json` `consumerChanges`. LakehouseErasureCompleted keeps PLT; ModelVersionApproved gains CMP and PLT | D-CON-09 applied | Made |
| D-CON-24 | The disbursement `source` field is an open code, with the D1 values listed (refund, claim payment, FS_CLEARING, CMP_REDRESS, TAX_REMITTANCE, …) | The contract and PRD-09 value lists differ | Made |
| D-CON-25 | `TermStarted` is not created (optional in PRD-18, undecided). 15 events have `minimal` payloads (listed in catalog.json) and are completed by the owning WP from its PRD | Never invent | Made |
| D-CON-26 | The event envelope requires `actor`, `jurisdiction`, and `aiInteractionId` (nullable) on every event, as the contract §3.4.1 lists them for all events | Contract wins over PLAN §4.3's shorter list | Made |
| D-CON-27 | Fraud scores and SIU case data are classified **P3** (criminal-offence data, GDPR Art. 10) | Conservative reading; PRD-07 is silent | Made |
| D-CON-28 | Each event declares its required lineage keys (`x-business-keys` in catalog.json), and the validator enforces them | D5 lineage must be checkable | Made |
| D-CON-29 | Lineage keys belong in the envelope `businessKeys` and need not appear in the payload. PolicyBound carries jobId + quoteId so that quote → job → transaction is traceable | D5 | Made |
