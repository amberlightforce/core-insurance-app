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
| D-CON-30 | Follow-ups from the F-1c review, to be fixed by the owning WPs: (M1) JournalPosted business key should name the posting batch or run, since its payload is a journal array (W5-FIN); (M2) CessionCalculated and BillingEntryPosted should add a charge-side lineage key (transactionId or sourceCorrelationKey) (W5-BIL, W7-RI) | Non-blocking review notes | Made |

## L. Documents spike result (F-1f a)

| ID | Decision | Reason | Status |
|---|---|---|---|
| D-ARC-07a | **Refines D-ARC-07.** Gotenberg is used only for its Chromium route (`generateTaggedPdf`, `failOnConsoleExceptions`, `failOnResourceLoadingFailed`, `preferCssPageSize`). Its `pdfa`/`pdfua`/`metadata`/`embeds` options are NOT used for archive copies. An in-house .NET ArchiveFinaliser (no PDF library) produces PDF/A-3a + PDF/UA-1, embeds the payload, normalises bytes deterministically and applies the PAdES seal. Headers and footers use CSS `@page` margin boxes, not Chromium header templates. Glyph gates: a pre-render cmap check, a Gotenberg image with declared fonts only, and a post-render PdfPig font check. Documents over ~150 pages render in chunks (the merge is still to be built in W2-DOC). veraPDF runs in CI | Spike evidence: veraPDF 3a/3b/3u/UA-1 pass; identical SHA-256 across 9 renders | Made |
| D-ARC-07b | Approved dependencies: PdfPig 0.1.11 (Apache-2.0), System.Security.Cryptography.Pkcs (MIT), BouncyCastle.Cryptography (MIT, for LTV), the official ICC sRGB profile, Noto Sans 2.015 (OFL), veraPDF CLI (CI only, external process), EU DSS (LGPL sidecar, optional, validation). iText excluded | ADR rule 11 | Made |
| D-ARC-07c | Infra delta: the 2 GiB Gotenberg container cannot render 500+ page documents in one call (Chromium peaked at 5.6 GiB). Use chunking by default; a large-document Gotenberg pool is a Stage-2 option | Measured | Made |

## M. Rule engine (F-1f c)

| ID | Decision | Reason | Status |
|---|---|---|---|
| D-ARC-10a | The rule language is a CEL subset with decimal-only numbers (no double, uint or bytes), date functions, explicit `round(x, places, mode)`, and decision tables (UNIQUE/FIRST/PRIORITY with explicit integer priority/COLLECT). The decision-table content hash is SHA-256 over canonical S-expression text (separate from the RFC 8785 configuration hash in D-ARC-12). Persistence, approval workflow and DMN import/export belong to W1-PLT-04 | Builder design, accepted pending review | Made |
| OQ-ORC-01 | `ageAt` for a 29 February birthday in a common year counts the birthday on 28 February. Legal and actuarial teams to confirm whether Greek practice uses 1 March. Not stated in any PRD | Never invent | Open |

## N. API contract rulings (F-1c apis)

| ID | Decision | Reason | Status |
|---|---|---|---|
| D-API-01 | Error codes follow the contract format `<MOD>-ERR-<NNN or NAME>`. PLAN §4.2's `<MOD>-<NNN>` is superseded | Contract wins | Made |
| D-API-02 | Time-travel query parameters are `validAt` (valid time) and `knownAt` (transaction time), per the contract. PLAN's `asOf` is superseded and must not appear as a second name | One name per concept | Made |
| D-API-03 | Operation names are the owner PRD §9.1 names (R-87). Superseded names are recorded in `x-superseded-names` (e.g. `pol.Submission.create` + `pol.Job.quote`, not `pol.Quote.create`; `uw.Rules.evaluate`; `pfc.ProductVersion.resolve`) | Owner is canonical | Made |
| D-API-04 | 65 `Resource.*` operation families and 220 minimal operations are completed (members, I/O, authority types, exposure) by the owning WP, which may add operations within its family without a contract change request. Renaming or removing a published operation needs an orchestrator ruling | Interface-first, owner completes | Made |
| D-API-05 | Problem Details carries `traceId` (W3C trace, = contract `correlation_id`); lineage lives in business keys (D5) | D-CON-01 | Made |

## O. Scaffold review (F-1a)

| ID | Decision | Reason | Status |
|---|---|---|---|
| D-FE-10 | MSAL React and OIDC cookie sign-in (incl. browser access to the Hangfire dashboard) are deferred to the W1-PLT identity WP | They belong with the identity work; the scaffold stays bearer-only | Made |
| D-ARC-17 | Floating-point ban is enforced by the in-repo Roslyn analyser COREINS001 (tools/CoreIns.Analyzers), not BannedApiAnalyzers, which does not catch `double`/`float` keywords or literals. This is a doc delta to ADR §2 rule 2 | Builder evidence, reviewer verified | Made |
| D-ARC-18 | Tests run on xunit.v3 + Microsoft.Testing.Platform with central package management. All test projects (including the rule engine) conform | Scaffold standard | Made |
| D-ARC-19 | Azure DB bootstrap (roles, ownership, extensions) is one idempotent SQL script shared by local pg-init and an Azure bootstrap step. The infra spec §6 delta is recorded in the repo docs | Review D1 | Made |
| D-ARC-10b | Rule engine: silent decimal precision loss is forbidden. `+ - *` that would exceed decimal precision raise RULE-PRECISION-LOSS. Division rounds to 28 significant digits and money must pass through an explicit `round()` | ADR rule 2 (explicit rounding) | Made |
| D-ARC-10c | Rule engine: expected result types are non-null unless declared nullable; a null result raises RULE-NULL-VALUE at run time | Money amounts must never be null | Made |
| D-ARC-10d | Rule engine: the evaluation cost budget is proportional to the work done, with an allocation cap and a wall-clock deadline. Rule and table hashes cover the input schema and host-function signatures, and the canonical text is injective | Review F-1f(c) D1–D3 | Made |
| D-API-06 | **Contract maturity.** A published v1 operation counts as *pre-release* until the first consumer WP that calls it merges. Until then, its owner may tighten it: type `Unspecified` members, add required inputs and enums, split fields. After that, the normal rule applies (additive within a major; breaking means a new major). Critical-chain anchor operations (PLAN §2) are fully typed now in F-1c | Resolves the conflict between interface-first and owner-completes (review D-4) | Made |
| D-API-07 | Dry-run is required where the PRD requires it, and also on every channel facade that wraps an operation offering dry-run (e.g. `chn.PartnerQuote.bind` mirrors `pol.Job.bind`) | Contract §3.5.3 read with PRD silence | Made |
| D-API-08 | "One name per concept" covers parameters and body fields: the valid-time instant is always `validAt`, the transaction-time instant always `knownAt`. PRD spellings (`asAt`, `asOf`, `date`, `versionOrAsAt`) are kept only in `x-prd-name`. Operation names from the PRDs (`dat.Query.asOf`, `doc.Document.renderAsOf`, `DOC-ERR-ASOF-UNSUPPORTED`) stay, under D-API-03 | Review D-2/D-11 | Made |
| D-API-09 | Each time-travel input appears exactly once, as a query parameter, never duplicated in the body | Contract §3.5.5 | Made |
| D-API-10 | There is no separate `pol.Job.issue`. Issuance happens inside `pol.Job.bind` (the `holdIssuance` flag plus the PRE_ISSUE UW checkpoint), as PRD-05 §9.1 defines. My brief's mention of `pol.Job.issue` is withdrawn | Owner PRD is canonical | Made |
| D-API-11 | `cmp.FiscalDocument.request.sourceType` stays an open code (pre-release, D-API-06) until W5-CMP settles one value list between REQ-CMP-030 and integration review XRF-001 | Owner WP decides within pre-release | Made |
| D-ARC-10e | Rule engine: the effective RuleLimits are part of the environment fingerprint and hash. RULE-TIMEOUT/RULE-CANCELLED are operational, fail-closed and retryable, never a business outcome. Values crossing the engine boundary are bounded in weight | Re-review F-1f N1/M2 | Made |
| D-FE-11 | App rail selected state uses the v3 mockup's 3 px indicator bar (open item M6 closed) | v3 is approved | Made |
| D-FE-12 | Abbreviated numbers keep Intl's no-break space before «χιλ./εκ./δισ.» | Typographic norm | Made |
| D-FE-13 | Amount in words below 1 € reads «πενήντα λεπτά» (no «μηδέν ευρώ»); exactly 0 reads «μηδέν ευρώ» | Natural reading | Made |
| D-FE-14 | Greek status labels use the contract glossary; any other label is flagged `glossary: pending` for business sign-off (non-blocking) | Language of record | Made |
| D-PRG-15 | Builder agents must not spawn their own sub-agents (the concurrency cap of 4 covers all agents) | Keep the user's limit | Made |
| D-PRG-16 | While GitHub CI cannot run (the active account lacks the `workflow` scope), a WP that passes independent review with the full local suite green (Docker-only tests excepted) is merged into **local** main as "merged, CI pending". The first CI run after pushing is enabled confirms it, and any failure is fixed forward with top priority | Unblocks dependent waves; the Docker/CI gap is environmental | Made |
| D-API-06a | **Narrows D-API-06.** "Fully typed now" covers the quote, bind, money and claims chains only. Lifecycle-chain operations (pol.Cancellation.*, cmp.Clock.*, bil.Refund.*, bil.Delinquency.*, mkt.StatutoryClockSet.*) and the remaining ri.Recovery.* operations are typed by their owning WPs (W2-CMP, W5-BIL, W6-POL, W1-MKT, W7-RI) while still pre-release, i.e. before their first consumer merges. Each owning WP brief must include "type your pre-release operations" | Interface-first without blocking F-1c; the pre-release rule protects consumers | Made |
| D-API-12 | `worksheetId` is the SHA-256 of the canonical worksheet (PRD-03 data model), in both the API (RateRateResponse) and the event (RatingCalculated) | Owner data model wins; both still pre-release | Made |
| D-API-13 | `uw.PolicyHold.check.riskLocations` shape is typed by W3-UW from PRD-04 (postcode / hazard-zone keys via Geocoder) | Not defined in REQ-UW-007 | Made |
| D-FE-15 | A sanctions-screening TrueMatch may render as a solid red pill (third solid use besides breached and conflict). It is a blocking compliance state, so the guide's two-item list is extended | Severity parity | Made |
| D-FE-16 | DataTable extras left out in Phase 1 (drag-reorder columns, saved views, inline edit, typed filters, E/F/G V shortcuts, Shift+F10 context menu, hover quick actions, reorder animation, background bulk jobs over 500 rows) are added by the first feature WP that needs them, inside the design system (not restyled locally) | Phase 1 delivers the core table | Made |
| D-ARC-20 | A country pack may reference another country pack's *shared algorithm* (CY reuses the GR ELOT 743 engine under its own rule-set id). Core and modules still never reference packs. If a third pack needs it, the engine moves to `CoreIns.CountryPacks.Common` | Avoid duplicating the algorithm; boundary rule protects core | Made |
| D-ARC-21 | SPI method names follow spi.md, with an `Async` suffix (ValueTask for synchronous ones). The namespace is `CoreIns.Modules.Market.Contracts.Spi`. Valid-time parameters are named `validAt` (D-API-08), never `asOf` or `date` | Consistency with API rulings | Made |
| D-FE-17 | ConfirmDialog first focus: the first input field if the dialog has fields, otherwise «Άκυρο» (least destructive) | WAI-ARIA APG dialog guidance | Made |
| D-FE-18 | ConfirmDialog level 3 «Αποστολή για έγκριση» uses the primary style, not danger (it routes for approval rather than executing) | Semantics | Made |
| D-FE-19 | Timeline day headers are real headings; no `role="feed"` (a list per day) | Screen-reader heading navigation | Made |
| D-FE-20 | MentionComposer stays a multi-line textarea, with a listbox popup driven by the keyboard and live-region announcements (ARIA forbids combobox on textarea) | Comments are multi-line | Made |
| D-FE-21 | The design guide's own label table (Part 1 §2.1.4) is the source for Greek/English status labels; labels not in the contract glossary stay `glossary: pending` for business sign-off | Guide is approved; glossary governs terms | Made |
| D-ARC-22 | Greek postcode→locality autofill (REQ-PTY-076) is deferred to W2-PTY. F-1e defines an `IPostcodeDirectory` data contract with the PRD vector as a test fixture. No postcode list is invented | Reference data not in the PRDs | Made |
| D-ARC-23 | Encryption key rotation is replica-safe: a demotion time is persisted, Retire waits for demotion + cache refresh + re-scan, search covers every readable version, and KEK re-wraps verify that the key version changed | Review F-1e M3/M4 | Made |
| D-FE-22 | Calendar month and weekday names follow the UI language (React Aria locale), while the date preview and read-only text follow the user's region-format setting. The split is accepted | Mirrors D-FE locale split | Made |
| D-FE-23 | Route-level code splitting is applied when module WPs add real routes: each module's routes are `lazy()`-loaded from W1 on, and the shell keeps a size budget warning in CI | 500 kB single chunk is fine for Phase 1 | Made |
| D-FE-24 | The Relationship graph component (§4.38, needs visx per D-FE-01) and the AI agent-plan/streaming surfaces (§4.37 partial) are built by the first feature WP that needs them (W2-PTY households / W8 AI). pdf.js document rendering is built in W2-DOC | Phase 1 delivers what W1–W2 consume | Made |
| D-FE-01a | **Amends D-FE-01:** react-aria-components and @internationalized/date (React Aria family, which the ADR already approves), axe-core (engine of the approved @axe-core/playwright) and @testing-library/user-event (keyboard tests) are approved | Review F-1d R8 | Made |
| D-FE-25 | Client-side Greeklish transliteration (`web/src/format/search.ts`) follows the same ELOT 743 vectors as the backend `ElotTransliterator`; the backend tests' vectors are mirrored as frontend tests. Percentage-point unit is «μ.» (guide §6.4). All number, money and percent formatting goes through `src/format` | One source of truth | Made |
| D-ARC-23a | Key rotation operating constraints: (1) write transactions that touch encrypted columns must finish within MaxStaleForWrite + 2 × RetirementMargin (enforced by PostgreSQL `idle_in_transaction_session_timeout` / statement timeouts, and by `IRetirementScan`, which refuses while older transactions exist (`pg_stat_activity.xact_start`)); (2) both retirement steps run from one operator job on NTP-synced hosts; (3) the key store is always read from the PostgreSQL primary, never a lagging replica; (4) a documented un-retire step (Retired → Retiring) exists for recovery | Review F-1e residual risks R1–R3 | Made |

## P. Shared kernel rulings (F-1b)

| ID | Decision | Reason | Status |
|---|---|---|---|
| D-CON-08a | **Amends D-CON-08.** Job: `Quoted → Bound`; on bind with a future issuance/effective date the job goes `Bound → Scheduled`, and then `Scheduled → Issued` (or `Withdrawn`). There is no `Scheduled → Bound` transition, so no loop | Removes the cycle the literal D-CON-08 created | Made |
| D-CON-31 | FIXED_DATE statutory clocks behave as deadlines (they can be Met or Breached) | D7 gives no other semantics | Made |
| D-CON-32 | ReserveLine states use PRD-18 §9.2.6 names (`OpenLine`, `FinalLine`) | State model of record (D5) | Made |
| D-API-14 | Platform error codes are registered: PLT-ERR-IDEMPOTENCY-KEY-REQUIRED, -IDEMPOTENCY-KEY-INVALID, -IDEMPOTENCY-MISMATCH, -IDEMPOTENCY-IN-PROGRESS, -VALIDATION, -INVALID-STATE-TRANSITION, -AUTHORITY-DENIED, -AUTHORITY-REFERRAL-REQUIRED, -INTERNAL (module-prefixed equivalents allowed) | Contract §3.5.4 error model | Made |
| D-API-15 | Problem Details `type` is the relative URI `/problems/<CODE>` (RFC 9457 allows relative references). The api serves a human-readable page per code | No invented external domain | Made |
| D-CON-33 | Legal entity identity: rows and SPIs use `LegalEntityId` (UUIDv7); the event envelope and configuration use `LegalEntityCode` (e.g. `GR-TEST`). The registry and mapping belong to MKT (W1-MKT). DataProtection's local `LegalEntityId` is replaced by the SharedKernel type in W1 | Both forms exist in the contract | Made |
| D-ARC-24 | Modules may reference `CoreIns.Platform.*` (incl. DataProtection) and `CoreIns.Rules`, besides other modules' Contracts and SharedKernel | Shared infrastructure libraries | Made |
| D-ARC-25 | Every time source goes through IClock. The platform registers a `TimeProvider` backed by IClock. Default constructors falling back to `TimeProvider.System` (GreekIdValidator, AzureKeyVaultKeyProvider) are removed in W1 | Testable, shiftable time (REQ-PLT-332) | Made |
| D-CON-08b | **Supersedes D-CON-08 and D-CON-08a, which were wrong.** The Job state model follows contract §3.2.4 exactly: `Draft → Quoted → Bound`; terminal `Withdrawn`, `Declined`, `NotTaken`, `Expired`; `Scheduled` is for future-effective Cancellation and Renewal jobs (`Quoted → Scheduled → Bound` on the effective date, or `Scheduled → Rescinded`); `Referred` and `Preempted` are flags, not states. There is no `Issued` job state (issuance is a PolicyTransaction fact) | Orchestrator error caught in F-1b review; the contract wins | Made |
| D-ARC-26 | Outbox dead letters: parking a failed handler does NOT hold the aggregate. Later events continue, and a replayed dead letter arrives out of order. Handlers whose correctness depends on order must check `aggregateSequence` and refuse/park out-of-order input. Documented in Platform/Events | PRD-14 dead-letter states; liveness | Made |
| D-ARC-27 | SharedKernel Money arithmetic guards precision loss like the rule engine (D-ARC-10b): multiply/divide results that cannot be represented exactly raise, and division needs an explicit rounding mode | ADR §2 rule 2 | Made |

## Q. Cost and pace reset (user, 2026-10-07 evening)

| ID | Decision | Effect | Status |
|---|---|---|---|
| D-USR-04 | **Lighter reviews.** One review pass per WP that blocks only on blockers/majors. Minor-only fixes are spot-checked by the orchestrator, not fully re-reviewed. Reviewers run the build, tests and acceptance criteria and spot-check; they do not write adversarial suites, except for money/ledger, temporal/bitemporal and security code, which keep a deep review | Roughly halves review cost | User |
| D-USR-05 | **Model by risk.** Sonnet for routine WPs (screens, CRUD, reference data, simple modules) and for readers; the strongest model for the policy timeline, billing ledger, finance posting, rating, and security | Large saving on routine work | User |
| D-USR-06 | **Thin E2E slice next.** After foundations, build one motor product end-to-end (quote → bind → invoice → payment → journal, E2E-01 happy path) across W1–W5 modules before widening. **Supersedes Q2's "full cut, waves in order" for sequencing only**; the full scope is unchanged | Visible working system early | User |
| D-USR-07 | **Pause after foundations.** Finish the running F-1b fixes and F-1c C# types (single light re-review), write the handover, then stop and wait for the user's go-ahead before any feature work | Spend control | User |
| D-CON-08c | Job keeps `Quoted → Draft` (Edit). It is a transition between existing contract states taken from PRD-18 (state model of record, D5), not a new state, so it doesn't conflict with §3.2.4 | Clarifies D-CON-08b | Made |
| D-ARC-28 | Outbox claim tuning (planner hints) is deferred: 2,380 ev/s with a write handler meets the ≥2,000 target. Re-measure under real load in the E2E slice | D-USR-04 cost control | Made |
