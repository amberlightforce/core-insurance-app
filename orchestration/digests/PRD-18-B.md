# Digest PRD-18 (part B) — Programme requirements baseline, lines 2716–4789

Source: `core-insurance-prds/PRD-18-programme-requirements-baseline.md`, lines 2716–4789 (read in full; the
assigned range ended at 4781 but the S5 findings table runs to 4783 and the count line to 4785, so those were read
too). Sections covered: §16 Motor MVP cut list, §17 cross-module E2E scenarios, §18 open-questions and assumptions
registers, §19 decision log, §20 change requests, §21 sources, Self-check, "Ten decisions before freezing", Freeze
recommendation, Freeze record, Annex A part registers (S2, S3, S4, S5 findings, decisions and CRs).

Cross-checked against: `00-system-contract.md` §3.10.10 (v1.11/v1.12: "Programme decisions D1–D10 … accepted by the
programme sponsor, 2026-10-07") and `core-insurance-infra/ARCHITECTURE-DECISIONS.md` (Accepted, 2026-10-07).

Notation: "OQ" = XMR-OQ, "D-nnn" = XMR-D-nnn, "F-nnn" = XMR-F-nnn, "CR" = change request. Gates: **G0** before the
build baseline is frozen; **G1** before system integration test (SIT) of the affected journey; **G2** before motor
go-live; **G3** before the phase that needs it (P2 home, P3 commercial, P4 later markets).

---

## 1. Identity

- **Module code / title:** PRD-18 (XMR, cross-module review), Programme Requirements Baseline. This part was written by
  sub-authors S4 (§13–16), S5 (§17–21) and S1 (self-check, ten decisions, freeze). It also holds Annex A, the S2–S5
  registers.
- **Purpose of this part:** (a) define the **Motor MVP cut**: every Must with phase P1 (contract R-86: P1 = Greek
  private motor MVP), placed into journeys J0–J13, build waves W1–W9 and a relative size index; (b) define the
  **programme E2E suite** E2E-01…E2E-12 as the release gate (new programme requirement XMR-FR-250); (c) merge the 257
  PRD open issues into 52 programme questions (XMR-OQ-001…052) and 15 assumptions (XMR-AS-01…15); (d) log the
  decisions already taken (CD-01…20, R-01…R-100, the PRD design stances) and those still required (D-250…288); (e) list
  targeted change requests per PRD; (f) record the freeze.
- **Non-goals:** no PRD is rewritten. CRs are targeted edits with requirement IDs kept stable (§20.1). Size points are
  "a relative index for sequencing and capacity planning, not an effort estimate" (§16.4).

## 2. Size metrics

| Quantity | Value | Source |
|---|---|---|
| Must requirements, all 17 PRDs (S4 parse, RI phase column) | **3,785** | §16.1 |
| Must P1 = the **Motor MVP cut** | **3,710** (5,614 size points) | §16.1, §16.2 |
| Must in P2–P4 (not built for MVP) | **75** | §16.8 |
| By tag (all 3,785 Musts) | 3,233 BASELINE / 552 ENHANCEMENT (`_work/stats.json` says 551) | §16.1 |
| Must rows with G/W/T (S5 pass, RI target-phase column, MIG scenario-B) | 3,870 | §17.2 |
| `_work/stats.json` Must count (predates REQ-POL-354) | 3,784 | Self-check deviation 3 |
| Total requirement rows in all PRDs | about 4,697 | Self-check A/I |
| Must dependency links tested | 5,153 (4,890 Must→Must) | §16.6 |
| Contract anchors (XL, 5 points) | 169 of the Musts | §16.4 |
| Events in the catalogue | 302 | Self-check C |
| Screens | 324 | Self-check K |
| PRD open issues merged | 257 (57 closed/superseded/optional, 200 open) → 52 OQs + 15 assumptions | §18.1 |
| PRD "ten decisions" merged | 170 → D-250…288 | §19.2 |
| Findings in this baseline | Blocker 0. S2: 9 major / 24 minor. S3: 13 / 18. S4: 19 / 29. S5: 10 / 12. Freeze text: "48 major inconsistencies", 37 of them G0 | Annex A; Freeze recommendation |
| Effect of accepted decision D6 on the cut | +9 Musts (promoted Shoulds, about +14 points). Bancassurance out of P1 ("removes ≈ 12 Musts", D-200 option (b)) unless a bank partner is signed by G0 | D6, D-200, D-203 |

**Build-size estimate: L (programme).** 3,710 P1 Musts, 9 waves, heavy money and temporal logic in every journey.

### Size rule (§16.4)

| Size | Points | Rule | Count (of 3,785) |
|---|---|---|---|
| XL | 5 | Contract anchor (PRD §5.0/§5.1) | 169 |
| L | 3 | Text > 500 characters or ≥ 4 cross-module dependencies | 53 |
| M | 2 | Text > 250 characters or 2–3 cross-module dependencies | 1,150 |
| S | 1 | Otherwise | 2,413 |

---

## 3. Motor MVP cut (§16)

### 3.1 Definition and scope choices (§16.1)

- **Cut = every Must in phase P1.** 3,710 IDs, each listed in §16.5 and again, with journey, wave, size and cross-module
  dependencies, in Annex A.1. Annex A.1 is outside this line range.
- **Scope choice 1, migration scenario.** PRD-16 plans on **scenario B** (existing book converted at renewal). 167 MIG
  Musts apply under B; under scenario A (new licence, no book) only 12 are Must. The board had not decided (PRD-16
  D-01), so the cut follows B (F-374, D-212). **Accepted (D6): plan on B; the board confirms before W5.**
- **Scope choice 2, reinsurance.** PRD-08 has a phase MoSCoW and a target-phase MoSCoW. The cut uses the phase column:
  91 RI Musts, all P1 (programme and contract registry, excess-of-loss recoveries, finance hand-off). Proportional
  cession is Must only at its target phase P2. **D6: motor XoL-only assumed (RI A-01), to be confirmed from the group
  programme documents before G0.** If A-01 fails, proportional cession (REQ-RI-002, -075…-088) moves into P1.
- **Accepted D6 adjustments (contract §3.10.10):**
  - The nine motor-critical Shoulds are promoted to Must P1: REQ-POL-108, REQ-PFC-112, REQ-PFC-094, REQ-PFC-145,
    REQ-PFC-156, REQ-PFC-157, REQ-PTY-096, REQ-PLT-065, REQ-WRK-091.
  - **Bancassurance is out of P1** unless a bank partner is signed by G0. The bank identity design is kept.
    Per D-200(b), REQ-PLT-057, REQ-CHN-042 (bank part), REQ-CHN-206 and -214, and the Musts of PTY §5.19 and CHN §5.14
    are demoted to Should P1 as a fast-follow.
  - **Annual terms only** in P1. The six-month-term golden scenario GS-02 stays as a capability test.

### 3.2 Journeys (§16.2), P1 Musts

| Journey | Name | Musts | Points | Share | Modules |
|---|---|---|---|---|---|
| J0 | Platform, configuration and multi-market foundations | 520 | 735 | 13.1% | PLT 277, MKT 243 |
| J1 | Party, distribution and commission | 278 | 480 | 8.6% | PTY 217, BIL 31, CHN 25, FIN 5 |
| J2 | Product, rating and underwriting | 580 | 838 | 14.9% | RAT 208, PFC 199, UW 173 |
| J3 | Quote to bind and issue | 384 | 618 | 11.0% | POL 206, CHN 140, DOC 38 |
| J4 | Billing, fiscal document, payment and ledger | 374 | 544 | 9.7% | BIL 224, FIN 95, CMP 55 |
| J5 | Servicing: change, cancellation, withdrawal, refund, reinstatement | 134 | 207 | 3.7% | POL 65, CHN 40, BIL 29 |
| J6 | Renewal | 32 | 54 | 1.0% | POL 21, UW 10, PFC 1 |
| J7 | Claims: FNOL to payment and recovery | 208 | 363 | 6.5% | CLM 192, BIL 9, CHN 7 |
| J8 | Ceded reinsurance (motor XoL) | 77 | 125 | 2.2% | RI 77 |
| J9 | Finance close, tax and financial reporting | 154 | 225 | 4.0% | FIN 141, RI 13 |
| J10 | Compliance: clocks, complaints, DSAR, obligations, AI register | 127 | 175 | 3.1% | CMP 127 |
| J11 | Documents, communications and work management | 443 | 605 | 10.8% | WRK 231, DOC 212 |
| J12 | Data and analytics | 208 | 280 | 5.0% | DAT 208 |
| J13 | Migration and coexistence | 191 | 365 | 6.5% | MIG 167 plus 24 import anchors spread over 11 modules |

### 3.3 Per-module P1 totals (§16.2 matrix and §16.5 headings)

| Module | Musts P1 | Points | Journeys |
|---|---|---|---|
| PLT | 283 | 397 | J0 277, J13 6 |
| MKT | 244 | 354 | J0 243, J13 1 |
| PTY | 218 | 377 | J1 217, J13 1 |
| PFC | 200 | 298 | J2 199, J6 1 |
| RAT | 214 | 295 | J2 208, J13 6 |
| UW | 183 | 269 | J2 173, J6 10 |
| POL | 293 | 450 | J3 206, J5 65, J6 21, J13 1 |
| BIL | 294 | 442 | J1 31, J4 224, J5 29, J7 9, J13 1 |
| CLM | 193 | 338 | J7 192, J13 1 |
| RI | 91 | 145 | J8 77, J9 13, J13 1 |
| FIN | 242 | 360 | J1 5, J4 95, J9 141, J13 1 |
| DOC | 250 | 351 | J3 38, J11 212 |
| CMP | 183 | 259 | J4 55, J10 127, J13 1 |
| CHN | 212 | 377 | J1 25, J3 140, J5 40, J7 7 |
| WRK | 231 | 315 | J11 231 |
| DAT | 212 | 285 | J12 208, J13 4 |
| MIG | 167 | 302 | J13 167 |
| **Total** | **3,710** | **5,614** | |

### 3.4 What is IN / OUT per module (§16.5 capability groups, §16.8 later-phase Musts)

"In" means the capability group has P1 Musts, in the wave shown. A range lists only consecutive Must IDs, so an ID
missing from a range is not a P1 Must of that group. "Out" lists the P2–P4 Musts from §16.8 and the known exclusions.

**PLT (283 / 397).**
- W1:
  - anchors REQ-PLT-001–009, 011, 013–015;
  - identity, staff realm (030–049 subset) and external realm (050–073 subset);
  - authorisation and access governance (075–098);
  - authority framework (100–111);
  - maker-checker (113–121);
  - business audit trail (123–135);
  - event infrastructure (136–148, 152);
  - integration hub / adapter host (153–164);
  - workflow engine and decision-table runtime (165–177);
  - configuration runtime and feature flags (179–193);
  - reference data (195–208);
  - numbering (209–215);
  - data-protection services (235–248);
  - observability (249–261);
  - environments and release (262–270, 341);
  - stamps and BC/DR (271, 273–278, 281, 283, 284);
  - security engineering (285–297);
  - architecture fitness (320–324);
  - admin tools and shell (327–333, 337, 344);
  - integration-review additions (351, 353).
- W8: anchors 010 (AI control plane) and 012; AI control plane 217–234; DORA operations 299–318.
- W9: migration import operations and coexistence 345–350.
- Out: REQ-PLT-272 and 279 (P4).

**MKT (244 / 354).**
- W1:
  - anchors 001–009;
  - configuration model (030–069);
  - capability switches (070–079);
  - SPI framework and catalogue (080–123);
  - pack packaging and lifecycle (125–149);
  - tenancy and legal entities (150–153, 160, 162);
  - i18n/l10n (165–189);
  - currency and rounding (190–199, 204);
  - regime code lists (205–213);
  - governance and quality (230–254);
  - reference packs (255–273);
  - statutory clock values (285–291);
  - operations UI (293–302);
  - integration additions (305–329).
- W8: cross-border business 216, 221, 226.
- W9: anchor 010 (migration baseline configuration).
- Out (P4): 155, 157–159, 161, 163, 201–203, 215, 218, 222, 224.
- Accepted changes outside the cut: D3 makes the B2B e-invoicing interface Must P1, and S4 CR-S4-19 raises REQ-MKT-089,
  -106 and -316 to Must P1.

**PTY (218 / 377).**
- W2:
  - anchors 001–006, 008, 009, 011, 012;
  - party master;
  - identifiers and verification;
  - names, transliteration and search;
  - addresses;
  - party roles;
  - accounts and households;
  - data quality, duplicates, merge and unmerge;
  - consent, trusted contacts and vulnerable customers;
  - sanctions screening (169–185);
  - intermediary register, licence and CPD;
  - hierarchy and producer codes;
  - party/account 360;
  - import/API.
- W6:
  - anchors 007, 010;
  - life events;
  - account merge, policy move and household split;
  - DSR support (161–168);
  - producer of record and book transfer;
  - commission agreements;
  - delegated agency administration;
  - bancassurance configuration (253–257; out of P1 under D6).
- W9: anchor 013.
- Out: REQ-PTY-277 (P3).
- Promoted by D6: REQ-PTY-096 (vendor master).

**PFC (200 / 298).**
- W2:
  - anchors 001–007, 009–011;
  - structure and metadata;
  - elements and validation;
  - coverages and terms;
  - offerings and question sets;
  - charge types (113–128);
  - references;
  - regulatory mapping;
  - POG and IPID data;
  - versioning and renewal conversion;
  - authoring and build;
  - review, approval and release;
  - runtime services;
  - test assets and import.
- W6: anchor 008 (renewal conversion).
- Promoted by D6: REQ-PFC-094 (peril tags), -112 (demands-and-needs question set), -145 (taxonomy version on
  mappings), -156 (POG monitoring indicators) and -157 (distributor information pack).

**RAT (214 / 295).**
- W3:
  - anchors 001–006, 008, 009;
  - rating contract and runtime;
  - artefact binding and caching;
  - algorithm framework;
  - coverage allocation, taxes and charges (105–115);
  - discounts and loadings;
  - market inputs and lawful pricing;
  - proration and day count (155–165);
  - table import;
  - testing assets;
  - approval, scheduling and rollback;
  - explainability (227–247).
- W8: anchor 007; CompareVersions, ShadowRun and impact analysis (197–211); monitoring data supply.
- W9: migration (269–275).
- Out (P2): 107, 148, 149, 151.

**UW (183 / 269).**
- W3:
  - anchors 001–007, 009, 010;
  - rule authoring;
  - evaluation runtime;
  - issue lifecycle;
  - authority at decision;
  - referral routing and SLA;
  - workbench;
  - external data;
  - inspections;
  - declines and refusal documents;
  - contingencies;
  - policy holds;
  - operations and data protection.
- W6: renewal and in-force UW (260–272).
- Out: P2 Musts 008 (accumulation), 197, 208, 210, 276–280; P3 Musts 152, 169, 176, 179, 211, 217, 223, 224, 226,
  228–231.

**POL (293 / 450).**
- W4:
  - anchors 001–008, 010, 011, 014;
  - policy, term and identifiers;
  - jobs, concurrency and preemption;
  - transaction stack and segments;
  - reverse-and-reapply;
  - charges, proration and the billing contract;
  - status model;
  - quote lifecycle;
  - bind and issue (170–189);
  - risk data and parties;
  - policy file, search and history;
  - import and integrity.
- W6:
  - anchors 009 (renewal) and 012 (cancellation);
  - policy change;
  - cancellation and void;
  - reinstatement, rewrite, suspension and transfer;
  - renewal (245–265);
  - prior-term changes;
  - statutory lifecycle (305–319).
- W9: anchor 013.
- Out (P2): 286 and 304 (buildings and risk-unit feeds).
- Promoted by D6: REQ-POL-108 (guard G5, closed accounting period).

**BIL (294 / 442).**
- W5:
  - anchors 001–006, 009, 011;
  - billing accounts;
  - payment plans, bind gate and preview;
  - charge intake;
  - invoices and fiscal triggers;
  - payment methods and mandates;
  - receipts, allocation and suspense;
  - reversals;
  - delinquency and dunning (161–178);
  - disbursement service (197–214);
  - written-basis levies and taxes (269–278);
  - billing sub-ledger;
  - bank reconciliation;
  - FIN hand-off;
  - workbenches.
- W6:
  - anchors 007 (refunds), 008 and 010;
  - refunds;
  - write-offs;
  - moving money and policies;
  - agency bill;
  - commission (249–268).
- W7: payee accounts and claim-related money services (343–353).
- W9: anchor 012.
- D1 adds the sources FS_CLEARING, CMP_REDRESS and TAX_REMITTANCE (new requirements, not yet in the cut counts).

**CLM (193 / 338).**
- All W7:
  - anchors 001–009, 011;
  - FNOL;
  - policy verification and coverage decisions;
  - claim structure and lifecycle;
  - triage;
  - reserves;
  - transaction sets and authority;
  - payments, payees and payment safety;
  - recoveries;
  - Greek motor ecosystem (155–172);
  - diary and documents;
  - vendors;
  - fraud and SIU;
  - catastrophe operations;
  - litigation;
  - tracking and feeds;
  - streamlining enhancements.
- W9: anchor 010.

**RI (91 / 145).**
- W7:
  - anchors 001, 003, 004;
  - programme and contract model;
  - participations and security;
  - contract lifecycle;
  - risk determination (066, 073);
  - proportional cession: only REQ-RI-075, to ignore non-cedable deltas;
  - claim intake and loss determination;
  - XoL recoveries (123–136);
  - reinstatements and exhaustion;
  - notifications to reinsurers;
  - technical accounting (173, 174, 183);
  - cash calls and settlement;
  - collateral;
  - multi-currency;
  - data exchange (245);
  - operations (247–259).
- W8: IFRS 17 and SII data (224–231); finance integration and close (233–240).
- W9: anchor 007.
- Out: proportional cession engine (P2).

**FIN (242 / 360).**
- W5:
  - anchors 001, 002, 004, 006 and 005;
  - business-event intake (030–047, 297);
  - posting rules;
  - journals and intake exceptions;
  - chart of accounts and dimensions;
  - claims and RI entries;
  - commission accounting;
  - tax, levies and fiscal reconciliation (178–201);
  - manual journals;
  - actuarial intake.
- W8:
  - anchors 003, 007–009, 011;
  - earning and deferrals;
  - IFRS 17 (121–148, 295);
  - SII views;
  - FX;
  - period close;
  - reconciliation (244–260);
  - GL extract;
  - migration ops and workbench.
- W9: anchor 010.
- Out: 140, 168 (P2); 166 (P3).

**DOC (250 / 351).**
- W2:
  - anchors 001–005;
  - document-type catalogue;
  - template and clause authoring;
  - governance and translation;
  - form patterns;
  - binding matrix;
  - payload dictionary;
  - Greek typography;
  - rendering engine;
  - archive;
  - delivery orchestration;
  - accessibility;
  - preview and test suites;
  - operations and import.
- W4:
  - anchors 006, 007 (pre-contractual pack) and 008;
  - fiscal coupling, proof of cover and pre-contractual documents (213–224);
  - e-signature;
  - Documents tab.
- W6: batch rendering; reprint and as-of rendering; proof and evidence packs.
- Out (P3): 043, 254.

**CMP (183 / 259).**
- W2: anchor 003 (clocks); statutory clock register and engine (092–121, 252).
- W5:
  - anchors 001 (fiscal channel) and 002 (bureau);
  - fiscal-document channel (030–061);
  - e-invoicing provider path (062, 063, 066);
  - Information Centre adapter (068–090, 254).
- W8:
  - anchors 004–008;
  - complaints;
  - DSAR and disclosure log (150–176, 255);
  - annual returns (191, 192);
  - submission tracker;
  - obligations register;
  - regulatory change backlog;
  - AI system register;
  - workbench.
- W9: anchor 009.
- Out: P2 Musts 177–183, 185–188 (A.1004 annual property return and related); P4 Musts 091, 194.

**CHN (212 / 377).**
- W4:
  - anchors 001, 002, 004, 008;
  - channel foundation;
  - transaction-permission matrix;
  - identity journeys;
  - **motor quote and buy (080–106)**;
  - gov.gr Wallet pre-fill (112, 114–116);
  - partner APIs;
  - accessibility.
- W6:
  - anchors 005, 007 (withdrawal) and 009;
  - self-service;
  - payments;
  - documents;
  - messaging;
  - withdrawal function (160–173);
  - broker and agent portal (180–199);
  - agency self-administration;
  - bancassurance (206, 207, 209, 214; out of P1 under D6);
  - channel data and contact-centre co-view.
- W7: claims (151–158).
- W8: anchor 003 (MCP); MCP AI agent facade (260–282); customer assistant and AI transparency; analytics and tracker
  consent.
- Out: 299, 300 (P2).

**WRK (231 / 315).**
- All W2:
  - anchors 001–010;
  - activity patterns;
  - lifecycle;
  - calendars and SLA;
  - groups and queues;
  - assignment and routing;
  - absence and delegation;
  - team views;
  - system-generated activities;
  - authority-aware routing;
  - back-office requests;
  - notes;
  - inbound documents (260–298);
  - participants;
  - My Desktop and global search;
  - staff notifications;
  - diary;
  - data protection;
  - admin;
  - integration additions.
- Promoted by D6: REQ-WRK-091 (priority score).

**DAT (212 / 285).**
- All W8:
  - anchors 001–007, 009;
  - ingestion and data contracts;
  - CDC backstop (050–057);
  - reference data;
  - **layered lakehouse (067–079)**;
  - bitemporal models;
  - regulatory mapping;
  - SII marts;
  - national marts;
  - group reporting;
  - actuarial supply;
  - pricing and claims analytics;
  - feature store (190–196);
  - model registry;
  - AI monitoring;
  - BI;
  - DQ and lineage;
  - catalogue;
  - personal-data governance in the lakehouse (262–279);
  - synthetic data;
  - operations;
  - delta-mode handling (299).
- W9: migration support (294–298).
- Out: P2 063, 125, 126, 128, 180, 181, 183, 186, 291; P3 109; P4 102.
- Section 14 below flags the infrastructure conflict.

**MIG (167 / 302).**
- All W9:
  - anchors 001–006;
  - strategy;
  - discovery;
  - mapping;
  - pipeline;
  - party conversion;
  - renewal-based policy conversion (091–108);
  - billing, finance, tax and fiscal;
  - claims conversion;
  - RI conversion;
  - other module data;
  - coexistence (148–161);
  - external hand-overs;
  - reconciliation and sign-off;
  - rehearsals, cut-over, rollback and hyper-care;
  - decommissioning;
  - security and AI governance.
- Valid only under scenario B.

### 3.5 Build sequence (§16.3), waves W1–W9

The dependency column cannot give a build order mechanically (F-360). 781 of the 4,890 Must→Must links point forward,
and 249 of the 314 capability groups sit in one cycle. The sequence is therefore taken from the architecture and
validated against the links. A forward link is met **interface-first**: the owner publishes its §9.1 operation and
event schema, with a contract test and a sandbox double, before the consuming wave starts.

| Wave | Name | Musts | Points | Cumulative | Modules (Musts) |
|---|---|---|---|---|---|
| W1 | Foundations | 481 | 678 | 678 (12%) | PLT 241, MKT 240 |
| W2 | Masters and shared services | 806 | 1,190 | 1,868 (33%) | WRK 231, PFC 199, DOC 187, PTY 161, CMP 28 |
| W3 | Pricing and underwriting | 361 | 519 | 2,387 (43%) | RAT 188, UW 173 |
| W4 | Policy core and quote-to-bind | 340 | 550 | 2,937 (52%) | POL 206, CHN 95, DOC 39 |
| W5 | Money: billing, fiscal, posting | 400 | 590 | 3,527 (63%) | BIL 224, FIN 121, CMP 55 |
| W6 | Servicing, renewal, distribution money | 314 | 493 | 4,020 (72%) | POL 86, CHN 77, BIL 60, PTY 56, DOC 24, UW 10, PFC 1 |
| W7 | Claims and RI recoveries | 285 | 488 | 4,508 (80%) | CLM 192, RI 77, BIL 9, CHN 7 |
| W8 | Close, reporting, compliance, analytics, optional AI | 532 | 741 | 5,249 (93%) | DAT 208, FIN 120, CMP 99, PLT 36, CHN 33, RAT 20, RI 13, MKT 3 |
| W9 | Migration and cut-over | 191 | 365 | 5,614 (100%) | MIG 167 + 24 import anchors |

Wave contents, from the mermaid diagram:

- **W1:** PLT identity, audit, events, authority, maker-checker, workflow, numbering and time; MKT configuration, SPIs,
  packs and the Cyprus stub in CI.
- **W2:** PFC product as data; PTY party, account, producer and screening; DOC templates, archive and delivery; WRK
  activities and queues; CMP clocks.
- **W3:** RAT rating, taxes via TaxCalculator and proration; UW rules, issues, referrals and authority.
- **W4:** POL transaction stack, out-of-sequence (OOS) handling, quote and bind gates; CHN quote-and-buy and partner
  API; DOC IPID, pre-contractual pack and cover note.
- **W5:** BIL charges, plans, invoices, payments, allocation, disbursement and delinquency; CMP myDATA and Information
  Centre; FIN intake and posting.
- **W6:** POL change, cancel, withdrawal, reinstate and renew; CHN self-service and broker portal; BIL refunds,
  commission and agency bill.
- **W7:** CLM FNOL to payment and recovery, plus the Greek motor ecosystem; RI motor XoL.
- **W8:** FIN close, IFRS 17 and SII; CMP complaints, DSAR and submissions; DAT marts; optional AI.
- **W9:** MIG import, conversion, coexistence, reconciliation and rehearsals.
- Side edges: W2 → W9 ("import APIs and mock loads from W5"); W4 → W8 (events).

**Wave rules:**

1. A wave is done when every Must in it passes its G/W/T and the E2E scenarios that end in that wave pass.
2. A contract anchor is done when every Must in its family is done (D-202).
3. The migration import APIs (REQ-POL-013, REQ-BIL-012, REQ-CLM-010, REQ-FIN-010, REQ-PTY-013, REQ-RI-007,
   REQ-CMP-009, REQ-MKT-010) are placed in W9 but **must be callable from W5** so mock migration 1 can run.
4. The optional AI features (all default off, contract §3.8) and the AI control plane REQ-PLT-010 sit in W8. No earlier
   Must depends on an AI feature.

By points per wave: W1 12%, W2 21% (largest, and the first candidate for parallel teams), W3 9%, W4 10%, W5 11%,
W6 9%, W7 9%, W8 13%, W9 7%. Accepted as the planning baseline by **D9**, to be recomputed after the
Requires / Used by / Family column split.

### 3.6 Anchor-level dependency chains (§16.7)

"→" reads "is needed by":

- MKT-001 config → PFC-001 resolve → RAT-001 rate → POL-011 quote. MKT-087 TaxCalculator → RAT-009 taxes → POL-011.
- PFC-001 → UW-001 evaluate → UW-002 blocking status → POL-003 bind gates. PTY-006 screening, PTY-008 producer codes,
  BIL-003 plans/down payment and DOC-007 pre-contractual pack all feed POL-003.
- POL-003 → POL-005 ChargeDeltaEmitted → BIL-002 charge intake → BIL-011 events to FIN → FIN-001 posting. PFC-004
  charge types → BIL-002. BIL-002 → BIL-096 fiscal trigger → CMP-001 fiscal channel (MKT-088 FiscalDocumentChannel →
  CMP-001). BIL-004 payments → BIL-011. PLT-005 events → FIN-001.
- CHN-007 withdrawal → POL-012 cancellation. MKT-009 clock values → CMP-003 clocks → BIL-006 non-payment → POL-012 →
  BIL-007 refunds (PTY-006 and PLT-003 → BIL-007). POL-009 renewal → POL-005.
- CLM-001 FNOL → CLM-002 cover at loss date → CLM-003 financial transactions → CLM-004 payment via BIL → BIL-009
  disbursement (PTY-006 → CLM-004; PLT-003 → CLM-003). CLM-003 → CLM-005 claim financial events → RI-003 XL recoveries
  and FIN-001.
- PLT-014 numbering → POL-011. MKT-002 SPI catalogue → MKT-087.

Examples of mechanical chains: POL-012 (W6) → BIL-006 (W5) → CMP-003 (W2) → MKT-009 (W1); CLM-004 (W7) → BIL-009
(W5) → PTY-006 (W2); DAT-003 (W8) → PFC-005 (W2) → MKT-007 (W1); MIG-003 (W9) → POL-341 (W4); CHN-007 (W6) →
DOC-044 (W2) → PLT-332 (W1).

### 3.7 Dependency check results (§16.6)

| Result | Links | Disposition |
|---|---|---|
| Must → Must, same or earlier phase | 4,890 − 6 | Pass |
| Anchor → own Should/Could/Won't family member | 159 | Family listing, not a dependency (F-361, D-202) |
| Must → non-Must in another module | 71 | 7 motor-critical promote (F-362…369), bancassurance scope (F-370), the rest reworded (F-371) |
| Must → Should/Could, same module | 18 | Owner review (F-373) |
| Must P1 → Must of a later phase | 6 | F-372: UW-070 → UW-008; DAT-111 → DAT-186; MKT-326 → DAT-128, CMP-181; POL-010 → POL-286, -304 |
| Range notation spanning unallocated IDs | 21 | Notation artefact (F-360) |

**Promotions (all accepted by D6):**
- POL-108, needed by FIN-007 and FIN-232;
- PFC-112, needed by CHN-086;
- PFC-094, needed by CLM-006 and CLM-210;
- PTY-096, needed by CLM-188 and CLM-189;
- PFC-145, needed by FIN-154;
- PFC-156 and PFC-157, needed by DAT-166 and the PFC-007 anchor;
- PLT-065, needed by CHN-060;
- WRK-091, needed by UW-129 and BIL-178.

**Rewordings (F-371):**
- CHN-052, -054 (cite WRK-008), -070, -103, -182;
- CLM-001, -036, -114, -192/-262, -214 (build from CLM data, F-345), -248;
- RAT-033 (wrong reference), RAT-111 / FIN-181 (applies when the product defines a fee);
- UW-048 vs PLT-178 (DMN import/export: demote one or promote the other), UW-295, UW-301;
- BIL-080/-177 (the manual reinstatement path is the Must), BIL-089 (cite DOC-001);
- FIN-187;
- DAT-097, -109, -138, -216.

---

## 4. Programme E2E suite (§17), the Phase 3 E2E backlog

### 4.1 Governing requirement and conventions

**XMR-FR-250 (Must / P1).** Owners: the design authority and a named programme test lead; PLT hosts it and MKT
supplies the harness.

- The programme keeps E2E-01…E2E-12 as executable tests.
- Each test runs against the **Greece pack and the Cyprus stub**, on **synthetic data**, through **public owner APIs
  and events only**.
- As written, each test asserts one correlation id from the originating command to the last journal line, document and
  regulatory fact. **This assertion was amended** (F-001; accepted D5): journey lineage uses **business keys** (quote,
  job, transaction, charge, invoice, journal ids), and the W3C trace id is technical only.
- The suite runs on every release candidate in the PLT pipeline (REQ-PLT-265), reuses the MKT golden-suite harness and
  expectation ownership (REQ-MKT-008, REQ-MKT-055), and **blocks promotion** on any failure.
- Acceptance: G release candidate RC-n with all module suites green; W the suite runs; T each of E2E-01…12 passes under
  both packs, the trace is complete (REQ-PLT-002, REQ-POL-087), and any failing step names the owning requirement ID.
- Accepted by **D9** (named test lead; release gate) and D-250 option (b).

**Conventions (§17.1):**
- **Packs:** GR is the Greece pack. CY is the Cyprus stub (REQ-MKT-008), with CY differences stated per scenario.
  Expected amounts are owned by the pack tax specialist (REQ-MKT-055).
- **Time:** all time comes from the PLT time service (REQ-PLT-332, contract §3.9.13). Scenarios run in a
  **time-shifted environment** (REQ-FIN-294 pattern).
- **Correlation:** POL carries the correlation id, configuration hash and resolution hash into every transaction,
  segment, delta and event (REQ-POL-087). BIL lines carry dimensions (REQ-BIL-283). FIN journals drill back to source
  (REQ-FIN-079).
- **Ledger assertions** reuse existing invariants:
  - POL P1 (earned + unearned = written) and P7 (Σ deltas = cumulative written);
  - BIL INV-01…INV-15;
  - FIN's ten property invariants (REQ-FIN-254);
  - the daily BIL↔FIN reconciliation (REQ-FIN-244).
- **Blocked expectations:** where an open question blocks a value, the step names the question and asserts
  **structure, not amount**.

### 4.2 Catalogue (§17.2)

| ID | Journey | Origin | Phase | Modules (first touch) | Reused PRD fragments | Status at source |
|---|---|---|---|---|---|---|
| E2E-01 | Quote → bind → invoice → fiscal document → payment → ledger | Check M (1) | P1 | CHN, POL, PFC, RAT, MKT, UW, PTY, BIL, DOC, CMP, FIN, RI, DAT, PLT | POL X-1; BIL #1; CMP #1; DAT; DOC #1; FIN GF-02; MKT GS-01, GS-04 | Specifiable; amounts blocked by T-02 (levy split) |
| E2E-02 | FNOL → reserve → payment → recovery → RI recovery | Check M (2) | P1 | CHN, CLM, POL, PTY, WRK, CMP, DOC, BIL, FIN, RI, DAT | CLM; FIN GF-05; RI GT-05, GT-12; MKT GS-26, GS-29 | Needs a motor XoL treaty (RI A-01, OI-RI-05) |
| E2E-03 | Policyholder cancellation → refund → fiscal credit note | Check M (3) | P1 | CHN, POL, PFC, RAT, BIL, PTY, CMP, DOC, FIN, RI, WRK | FIN GF-04; BIL #4, J-06; MKT GS-15; POL X-9 | Levy refund open (OI-POL-08 cluster) |
| E2E-04 | Renewal | Check M (4) | P1 | POL, PFC, RAT, UW, WRK, DOC, CMP, CHN, BIL, FIN, RI | POL X-4; PFC (c); RAT (e); UW; MKT GS-21, GS-22 | Acceptance mode undecided (D-262), since decided by D7 |
| E2E-05 | Migration of an in-force motor policy | Check M (5) | P1 | MIG, MKT, PTY, POL, BIL, CLM, RI, FIN, CMP, DOC, WRK, CHN, DAT | MIG; POL 14.y; BIL 14.y; CMP #9; RI #5 | Rollback step blocked (OI-MIG-12, F-406) |
| E2E-06 | Friendly Settlement | Added | P1 | CHN, CLM, MKT, CMP, WRK, BIL, FIN, DOC | CLM J-02; MKT GS-27 | **Cannot pass as written** (F-400); D1 fixes it |
| E2E-07 | Non-payment cancellation via waiting-period clock | Added | P1 | BIL, DOC, PTY, CMP, WRK, POL, FIN, RI, CHN | BIL J-05; DOC #3, RT-06, RT-07; CMP #2; POL X-12; MKT GS-16 | Electronic proof media open (OI-DOC-03) |
| E2E-08 | Distance withdrawal | Added | P1 | CHN, POL, DOC, CMP, MKT, BIL, PTY, FIN, RI | CHN; POL X-11; BIL #7; MKT GS-17 | IPT/levy on the full refund undefined (F-401) |
| E2E-09 | DSAR access and erasure across modules | Added | P1 | CMP, all personal-data modules, DAT, MIG, PLT, DOC | CMP #5, #6; DAT; WRK; MIG | Retention durations open (F-404) |
| E2E-10 | AI kill switch across modules | Added | P1 | PLT, all AI modules, CMP, DAT, CHN | PLT; CHN; DAT; UW RT-14 | Specifiable |
| E2E-11 | OOS change with open claim and cession (regression) | Added | P1 (P2 for QS) | POL, RAT, BIL, FIN, CLM, RI, WRK, DAT | POL X-2, X-3; FIN GF-03; DAT; RI #1 | Specifiable |
| E2E-12 | Pack rollback after bound business (regression, R3-001) | Added | P1 | MKT, PLT, POL, WRK, BIL, FIN, RAT | MKT 14.x.5; REQ-POL-354 | Specifiable |

Check M result: G/W/T is present on all 3,870 Must rows. The check is syntactic only, so it does not judge whether each
criterion is testable. Per PRD: PTY 219, PFC 200, RAT 218, UW 205, POL 295, BIL 294, CLM 193, RI 176 (target phase),
FIN 245, DOC 252, CMP 196, CHN 214, WRK 231, PLT 285, DAT 223, MIG 167, MKT 257. Before this baseline, no PRD owned an
end-to-end scenario (F-405).

### 4.3 Scenarios in detail

#### E2E-01 Quote → bind → invoice → fiscal document → payment → ledger

**Setup:**
- Greece pack; product MOTOR-GR published and Locked/Active; rating artefact active.
- Legal entity GR01 with book profile IFRS17 + SOLVENCY_II (R-48).
- Channel `WEB_DIRECT`; instalment plan with down payment (MKT GS-04).
- New party with a valid AFM from the PTY golden vectors; one vehicle, MTPL + own damage.

| # | Owner | Step | Requirement IDs | Assertion |
|---|---|---|---|---|
| 1 | CHN→POL | Quote via the permission matrix (`SELF_SERVICE`); `pol.Job.quote` | CHN-002, CHN-004, POL-001, POL-011 | Quote ≤ 2 s p95 (contract §3.9.7); dry-run equals real (POL P8) |
| 2 | PFC, RAT, MKT | Version resolution; rating keyed by three hashes; taxes and levies as separate charge types via `TaxCalculator` | PFC-001, PFC-004, RAT-001, RAT-008, RAT-009 | Worksheet stored; tax lines = **IPT 15% class (Law 5177/2025 Art. 43, verified 2026-10-07)** plus Auxiliary Fund levy line(s) per open T-02 |
| 3 | UW | PRE_QUOTE and PRE_BIND evaluation | UW-001, UW-002 | Blocking status false |
| 4 | DOC/CHN | IPID and pre-contractual pack; `DisclosureReceiptRecorded` or `DocumentDelivered` feeds POL `DisclosureDeliveryView` | DOC-007, CHN-316, R-83, R-95 | DISCLOSURE gate satisfied from the read model, **not a synchronous call** |
| 5 | BIL | Down payment by card (`PaymentChannelProvider`); `DownPaymentCleared` | BIL-003, BIL-004 | DOWN_PAYMENT gate satisfied |
| 6 | POL | `pol.Job.bind`, gates (UW, down payment, sanctions, disclosures, consents, holds); `PolicyBound`; `ChargeDeltaEmitted` per element × charge type | POL-003, POL-005, PTY-006, PTY-008, POL-087 | One delta per charge; Σ deltas = term written (POL-122) |
| 7 | POL→DOC | Cover note at bind, not blocked by fiscal registration | POL-176, POL-177, DOC-007 | Within 30 s (POL A-4), **no amounts** (DOC RT-09); fiscal status was open (OI-POL-01, D-264), since decided non-fiscal by D3 |
| 8 | CMP (bureau) | Insured-vehicle COVER_START fact from the POL event | POL-315, CMP-002 | Fact queued; interim transport MANUAL_FILE (OI-CMP-01); lag monitor running |
| 9 | BIL | Charges scheduled into the plan; invoice; `InvoiceIssued` | BIL-002, BIL-089 | INV-02, INV-03 |
| 10 | BIL→CMP | Fiscal request at `bil.fiscal.trigger_point` | BIL-096, CMP-001, CMP-030, CMP-031 | One FiscalDocument per idempotency key; **bind-to-MARK p95 < 60 s** |
| 11 | CMP→DOC | `FiscalDocRegistered` → receipt rendered with MARK | DOC-213 | A document held in AwaitingFiscal is released exactly once |
| 12 | BIL | Instalment 2 by SEPA direct debit; allocation waterfall | BIL-004 | INV-08 |
| 13 | BIL→FIN | `BillingEntryPosted` for written, billed, collected, IPT and levy accruals; daily control totals | BIL-011, FIN-036, FIN-244 | Zero unexplained breaks; FIN posts written premium **only from BIL** |
| 14 | FIN | IFRS 17 group assigned on `PolicyBound`; earning run | FIN-011, FIN-003 | One current assignment per term; earned + unearned = written |
| 15 | RI | `ChargeDeltaEmitted` consumed; only RI-cedable charge types | RI-075 | Motor P1 is XoL only: no proportional cession; ignored IPT and levy deltas counted |
| 16 | DAT | Lineage charge → invoice → fiscal doc → journal → SII figure | DAT-001, DAT-007 | "Same correlation id at every hop" (read as business lineage keys, per D5) |

**CY variant:** the fiscal channel returns `NotRequired`; no bureau call; tax lines per the stub; no `CY-STAMP` line
for terms starting on or after 2026-01-01 (REQ-MKT-265).

#### E2E-02 FNOL → reserve → payment → recovery → RI recovery

**Setup:**
- Policy from E2E-01, in force.
- Motor per-risk XoL **EUR 500k xs 250k** (RI GT-05) active for the underwriting year.
- At-fault third party insured by another Greek insurer, outside Friendly Settlement (amount above the FS limit).
- Claim parts: own-damage claim, plus an MTPL bodily-injury exposure to a third party (exercises the offer clock).

| # | Owner | Step | Requirement IDs | Assertion |
|---|---|---|---|---|
| 1 | CHN→CLM | FNOL (portal, offline-capable), photos through WRK intake | CHN-152, CLM-001, WRK-005 | Claim number issued (PLT-014); duplicate check |
| 2 | CLM→POL | Coverage verified on the snapshot valid at the loss date | CLM-002, POL-007 | Snapshot ref stored; byte-identical on re-read (POL P5) |
| 3 | CLM→CMP | `CLM_MTPL_OFFER` started at claim receipt (**3 months, Dir. 2009/103/EC Art. 22 as transposed**) | CLM-007, CLM-165, CMP-003 | Clock kind DEADLINE; start = receipt date, not registration date |
| 4 | CLM | Initial reserves as a transaction set within authority | CLM-003, PLT-003 | Exact limit allowed; one cent above refers |
| 5 | CLM→FIN/RI/DAT | `ReserveChanged` with IFRS 17 group, SII LoB, cat code | CLM-005, CLM-227 | FIN posts the reserve; RI recomputes (no recovery below 250k) |
| 6 | CLM→DOC | Reasoned offer rendered and delivered; clock Met on proven delivery | CLM-166 | Met on delivery evidence, not on rendering |
| 7 | CLM | Offer accepted; `CLM_MTPL_PAYMENT_DUE` started | CLM-167 | Source says deadline = acceptance + 10 days. **Superseded by D7: the clock starts at proven delivery of the offer** (BoG Act 87/2016 Art. 6, F-230); XMR-CR-CLM-04 withdrawn |
| 8 | CLM→BIL→PTY | Payment to a BIL `PaymentInstrument`; sanctions; VoP; disbursement | CLM-004, BIL-009, BIL-343, PTY-006 | INV-12: no release without Clear screening, acceptable VoP and approvals |
| 9 | CLM→FIN | `PaymentIssued`; BIL disbursement entry | FIN-037 | Claim-payment clearing account nets to zero per payment |
| 10 | CLM→CMP | Settlement receipt registered as a fiscal doc (CLAIM_PAYMENT source) | CMP-001, CMP-030, R-43 | MARK stored on the payment (doc type per OI-CLM-04) |
| 11 | CLM | Subrogation vs the at-fault insurer; recovery reserve; recovery received through a BIL receivable; salvage sold | CLM-102, CLM-143, -144, -145, BIL-346 | Net incurred = incurred − recoveries − open recovery reserve (CLM-096) |
| 12 | FIN | Recoveries posted by type | FIN-160 | Recovery accounts per type |
| 13 | RI | BI development to EUR 900k → layer loss 500k; then 700k after subrogation | RI-003, RI-127 | Deltas **+50k, +450k, −50k** (GT-05); idempotent and order-independent |
| 14 | RI→FIN/CLM | `RecoveryCalculated` | RI-004, FIN-164, CLM-229 | FIN posts recoverable on paid and reserve separately; the CLM view is Should, so assert via `ri.Recovery.listByClaim` (RI-136, F-407) |
| 15 | FIN/RI | Daily cession and recovery reconciliation | FIN-167 | Zero breaks |

**CY variant:** offer clock differs only by pack data; FS switched off (`cap.clm.friendly_settlement`, REQ-CLM-156);
fiscal receipt `NotRequired`.

#### E2E-03 Policyholder cancellation → refund → fiscal credit note

**Setup:** policy from E2E-01, paid in full at day 0. The policyholder asks to cancel at **day 120** through the CSR.
Portal cancellation is REFER by default (BR-CHN-007, OI-CHN-15).

| # | Owner | Step | Requirement IDs | Assertion |
|---|---|---|---|---|
| 1 | CHN/WRK→POL | Back-office request; staff runs the Cancellation job, source `Policyholder` | WRK-008, POL-205, -208, -209 | Source from the shared list (R-84) |
| 2 | POL/PFC | Refund method by source (`ProRata`), per element × charge type with charge-type cancellation treatment | POL-206, -207, PFC-134 | **IPT non-refundable (ΠΟΛ 1028/2017, POL AC-6)**; levy refund open (OI-POL-08, OI-BIL-03, OI-FIN-07). Under D2 the treatment comes from `TaxCalculator.treatment` |
| 3 | POL | `PolicyCancelled`; negative `ChargeDeltaEmitted` | POL-005, POL-212 | Notices to policyholder, payer and interested parties |
| 4 | CMP | Bureau COVER_END | POL-315, CMP-002 | One fact per vehicle |
| 5 | BIL | Credit billed at once; planned items stopped; credit note against the original invoice | BIL-074, -091, -089 (`DT-CREDIT-NOTE`) | INV-05: no cancellation-sourced debit to IPT payable |
| 6 | BIL→CMP | Fiscal credit correlated to the original MARK | BIL-096, CMP-032 | Idempotency key role CREDIT; one credit document |
| 7 | BIL→PTY | Refund from the credit balance: authority, maker-checker, sanctions, VoP, refund payout method | BIL-007, BIL-186, PTY-006 | Defaults: **auto-limit EUR 500; four-eyes EUR 5,000** (BIL §12) |
| 8 | BIL | `RefundApproved`, `RefundDisbursed` | BIL-190 | Stops `POL_REFUND_DUE` where started (POL-219, variant OTHER) |
| 9 | FIN | Pro rata credit; IPT payable not reduced; commission chargeback; acquisition-cost write-off | FIN-036, FIN-182 | FIN GF-04 expectations |
| 10 | RI | Negative deltas, RI-cedable types only | RI-075 | No-op for motor XoL apart from exposure data |

#### E2E-04 Renewal

**Setup:**
- Term expiring in 60 days; new rating artefact R2 active.
- PFC version 4.0 supersedes 3.2, with a conversion rule and a grandfathered option.
- A UW renewal rule refers a driver with two at-fault claims.

| # | Owner | Step | Requirement IDs | Assertion |
|---|---|---|---|---|
| 1 | POL | Renewal run creates a job in the window; rate artefact pinned per batch item | POL-009, RAT-002 | `RenewalCreated`; batch restart idempotent (POL X-10) |
| 2 | PFC | Conversion 3.2 → 4.0 with grandfathering | PFC-008 | Converted option kept; manifest hash stored |
| 3 | RAT | Renewal rating with cap and fairness rule; change explanation | RAT-002, RAT-008 | Cap applied; explanation available to CHN |
| 4 | UW→WRK | Renewal checkpoint referral; underwriter approves; `RenewalDirectionSet` | UW-001, UW-004 | Carried-forward approval reused where fingerprints match |
| 5 | POL→DOC→CMP | Offer document; `RenewalOffered`; `POL_RENEWAL_NOTICE` where the pack defines it | POL-250, -318, -263 | Notice value open (OI-POL-14, OI-MIG-02, D-262). **D7: renewal and non-renewal notices are FIXED_DATE deadlines** |
| 6 | CHN→POL | Explicit acceptance, or payment as acceptance | POL-257 | Recorded with channel and identity. **D7: payment as acceptance, with explicit acceptance available** |
| 7 | POL | `RenewalBound`; charges for the new term | POL-005, -263 | New term pinned to R2 |
| 8 | FIN | IFRS 17 assignment on `RenewalBound` | FIN-011 | New cohort where the year changes (IFRS 17 para. 22) |
| 9 | BIL/CMP/DOC | Invoice, fiscal doc, MARK receipt, as E2E-01 steps 9–11 | BIL-096, DOC-213 | As E2E-01 |
| 10 | RI | New UW year attaches to the renewed XoL contract | RI-001 | Contract year resolved by term start |

**Variants:**
- non-renewal by UW direction (`POL_NONRENEWAL_NOTICE`, R-35);
- lapse on no acceptance;
- renewal across a tax-rate change date (MKT GS-22);
- producer transfer at renewal (POL X-6, REQ-PTY-222).

#### E2E-05 Migration of an in-force motor policy

**Setup:**
- Legacy policy GR-1, term 2026-07-01…2027-06-30, two drivers, SEPA mandate, one open BI claim, under the motor XoL
  treaty.
- Two runs: (a) mid-term conversion at 2027-01-01; (b) renewal-based conversion at 2027-07-01 (default, MIG D-02).

| # | Owner | Step | Requirement IDs | Assertion |
|---|---|---|---|---|
| 1 | MIG/MKT | Migration-baseline configuration state H0 | MKT-010 | Converted transactions carry H0; 2026 values re-resolve |
| 2 | MIG→PTY | Party import: dry-run, then real twice; xref | MIG-001, MIG-002, PTY-013 | `origin=MIGRATION`; the second load is a no-op |
| 3 | POL | (a) opening transaction with legacy written premium per element × charge type, opening deltas flagged; (b) first core term as a renewal from imported expiring data | POL-013, -340, -341, -342, MIG-003 | (a) BIL/FIN treat the deltas as opening balances, not new business; (b) renewal job Quoted.Offered |
| 4 | BIL | Opening balances; mandate with original reference and signature date | BIL-012, BIL-339 | Legacy outstanding = core opening receivable to the cent; signed by a controller, approved by a different user (MIG-005) |
| 5 | CLM | Open BI claim with the running clock re-created from the original start | CLM-010, CMP-009 | Deadline unchanged |
| 6 | RI | Running XoL year with opening aggregates | RI-007 | One tracker after a double import |
| 7 | FIN | Opening balances reconciled to the legacy TB and the module imports | FIN-010 | No `origin=MIGRATION` posting treated as new business |
| 8 | CMP | Bureau hand-over date per vehicle; fiscal series continuity | CMP-086, CMP-009 | No gap or duplicate cover period; legacy MARKs kept for credits |
| 9 | MIG→CHN/BIL/CLM/CMP/DOC/WRK | Routing: 2026-12-15 Transitioning; 2027-02-01 CoreMaster; caches refreshed from `CoexistenceMasterChanged`; authoritative read `mig.Routing.resolve` | MIG-004, MIG-149, CMP-254, R-96 | Exactly one master per policy and date |
| 10 | MIG | Rollback of the batch | MIG-076, MIG-190 | **Blocked**: owners have not named reversal operations for `origin=MIGRATION` objects (OI-MIG-12, F-406; D-260, G1 before mock 2) |

#### E2E-06 Friendly Settlement

**Setup:**
- Two-vehicle accident in Greece with a joint accident report.
- Our insured 0% at fault; material damage within the pack FS limit (`gr.fs.limit.*`, UNVERIFIED, OI-CLM-01).
- Second leg: our insured 100% at fault, and the counterparty settles under FS.

| # | Owner | Step | Requirement IDs | Assertion |
|---|---|---|---|---|
| 1 | CHN/WRK→CLM | FNOL with joint accident report | CHN-154, WRK-005, CLM-001 | Report classified by WRK `InboundDocumentProfile` |
| 2 | CLM→MKT SPI | `FriendlySettlementClearing.evaluateEligibility` | CLM-155, CLM-156, MKT-313 | Result with reasons; CY: switch off, SPI not called |
| 3 | CLM | FS own-settlement exposure; pay the customer or repairer; FS receivable as a recovery reserve at the clearing value | CLM-157 | Expected gain or loss shown |
| 4 | CLM→EAEE | `FriendlySettlementSubmitted`; dispute path starts `CLM_FS_COUNTERPARTY_REPLY` (WAITING_PERIOD) | CLM-159, R-40, R-78 | `ClockElapsed` triggers FS escalation |
| 5 | CLM | At-fault leg: claim from the clearing notification; FS payable settled via the clearing statement (method Clearing) | CLM-158 | — |
| 6 | CLM | Monthly statement imported, matched; net posted as recoveries and payments | CLM-160 | Unmatched lines to the FS exception queue |
| 7 | BIL | Net cash with the clearing office | **None as written** | **Fails** (F-400) |
| 8 | FIN | Cash side only from BIL disbursement; clearing account nets per payment | FIN-037, FIN-160 | **Fails** |

**Fix, accepted as D1:**
- BIL adds an `FS_CLEARING` source and registers the monthly net per clearing statement as one payable or receivable,
  then executes or matches the cash.
- FIN posts the cash from BIL, with clearing netting per statement (CR-FIN-02).
- CLM hands the net to BIL (CR-CLM-03).
- `clm.payment.methods` gains `CLEARING` (today: SEPA_CT, SEPA_INST, OFFSET).
- Naming: the S3 register calls the source `CLM_FS_SETTLEMENT` (F-214, D-155, CR-S3-11/12). D1 and the contract use
  **`FS_CLEARING`**.

#### E2E-07 Non-payment cancellation through the waiting-period clock

| # | Owner | Step | Requirement IDs | Assertion |
|---|---|---|---|---|
| 1 | BIL | Instalment returned (SEPA AM04); re-presentation fails; delinquency | BIL-005, BIL-006 | Delinquency per plan |
| 2 | BIL→DOC | `DT-NONPAY-NOTICE` by the statutory medium; evidence pack; notification date returned | DOC-005, DOC-038, `StatutoryDeliveryRule` (R-41) | Registered post (RT-06) unless electronic proof is accepted (OI-DOC-03; D-263 recommends registered post only at go-live) |
| 3 | BIL→CMP | `cmp.Clock.start` `BIL_NONPAY_NOTICE` from the notification date; value **one month** (K-01, R-60) | BIL-006, CMP-003, MKT-009 | Law 2496/1997 Art. 6 §2 one month (secondary source, 2026-10-07). **Unproven delivery: no clock starts** |
| 4 | WRK | Clock warning → activity | WRK-193 | Auto-completes on `ClockElapsed` |
| 5 | CMP→BIL | `ClockElapsed` (WAITING_PERIOD, R-78) | BIL-172 | `CancellationForNonPaymentRequested` only if arrears remain above tolerance (INV-11) |
| 6 | POL | Cancellation at the date BIL supplies; exceptions (open claim, vulnerable customer) to a PSR; consuming twice yields one job | POL-012, -210, -311, PTY-011 | Idempotency (POL X-12) |
| 7 | CMP/BIL/FIN/DOC | Bureau COVER_END; credit note and fiscal credit; FIN posts; cancellation notice | CMP-002, BIL-091, CMP-032, FIN-182 | As E2E-03 steps 4–9 |

**Variants:**
- (a) cure before elapse → clock Met (Cured), no request; POL rescinds any scheduled cancellation (REQ-POL-211);
- (b) vulnerable-customer hold;
- (c) returned registered letter → `DeliveryFailed`, supplementary pack, PTY address flag (DOC RT-07).

#### E2E-08 Distance withdrawal

| # | Owner | Step | Requirement IDs | Assertion |
|---|---|---|---|---|
| 1 | CHN | Withdrawal function: declaration, confirmation step, `confirmedAt` from PLT time | CHN-007, CHN-165 | Confirmation at **23:59:59 Europe/Athens on the last day is in time (DST day too)** |
| 2 | CHN→POL | `pol.Withdrawal.submit` with `receivedAt` = `confirmedAt`; backup `WithdrawalRequestReceived` | CHN-168, POL-308, POL-314, R-89 | Exactly one void even if the command fails after confirmation (POL X-11) |
| 3 | POL/DOC | Acknowledgement on a durable medium (`DT-WITHDRAWAL-ACK`), de-duplicated between CHN and POL on the request number | DOC-044, R-84 | One e-mail; acknowledgement timestamp = request time (DOC RT-11) |
| 4 | POL/MKT | Void; refund by `PolicyLifecycleRules` = `FullRefund`; `POL_REFUND_DUE` (variant DISTANCE_WITHDRAWAL) | POL-219, MKT-309, R-55 | **Refund = total paid; cost-of-cover deduction 0.00 (Law 5317/2026 Art. 72, verified 2026-10-07).** CY stub: pro rata |
| 5 | BIL | Refund with screening and VoP; `RefundDisbursed` stops `POL_REFUND_DUE` | BIL-007, BIL-190 | Within the clock (30 calendar days per Art. 3ιστ, F-235) |
| 6 | BIL→CMP/FIN | Fiscal credit for the voided premium; FIN posts the reversal | CMP-032, FIN-182 | **Undefined** for IPT and levy (F-401). Under D2 the default routes the withdrawal void by cancellation source; the customer refund is the same under either reading, only the insurer's tax position differs |
| 7 | CMP | Bureau void fact | POL-315 | Cover period removed |
| 8 | CMP | Withdrawal declaration with no POL record within 1 h raises an activity | CMP-253 | Compliance safety net |

#### E2E-09 DSAR access and erasure across modules

| # | Owner | Step | Requirement IDs | Assertion |
|---|---|---|---|---|
| 1 | CMP | DSAR registered; **one-month** deadline clock (DEADLINE) | CMP-005, CMP-003 | Clock from receipt |
| 2 | CMP | Fan-out by the configured map | CMP-154 | Map lists **PTY, POL, BIL, CLM, UW, RI, DOC, CHN, WRK, PLT, FIN, RAT (export only), DAT, MIG, CMP**. PFC and MKT hold none (R-62). CMP-154's "13 modules" text is wrong (CR-CMP-03, F-419): assert against §9.2.2 (15 rows) |
| 3 | Modules | Export, e.g. PTY-161, BIL-335, CLM-233, CHN-304, FIN-290, DOC-314, DAT-009, MIG-006 | as listed | Seeded-party completeness: found = seeded (CMP-167, WRK-372) |
| 4 | Modules | Erasure where allowed; restriction with legal basis and retention end where retention applies (ledger, invoices, claim file, documents) | BIL-336, POL-339, DOC-314, CHN-304, CMP-158, PLT-011 | Every outcome is erased, anonymised or restricted with a basis; legal hold blocks purge (DOC RT-17) |
| 5 | DAT | `dat.Erasure.execute` twice with the same request id → keys destroyed once; the synchronous response completes the CMP task | DAT-275, CMP-255, R-97 | `LakehouseErasureCompleted` is evidence only |
| 6 | MIG | Legacy archive export; erasure restricted for a claim file under retention | MIG-006 | Restriction recorded |
| 7 | CMP | DPO review with third-party redaction; disclosure log | CMP-005 | Deadline met |

**Blocker:** retention durations are unset or contradictory across 15 PRDs (F-404, D-259, G1), so "retention end"
values cannot be asserted yet.

#### E2E-10 AI kill switch across modules

| # | Owner | Step | Requirement IDs | Assertion |
|---|---|---|---|---|
| 1 | PLT | Enable a pilot feature per module where the CMP register status is Approved | PLT-010, CMP-007, PLT-233 | Enablement refused for any feature not Approved |
| 2 | Users/agents | In-flight: CLM FNOL assistant (AI-CLM-01) drafting; CHN MCP agent ticket pending; BIL suspense match suggestion; PTY duplicate explainer; UW decision in progress (UW RT-14) | CHN-003 | — |
| 3 | PLT | Global kill switch | PLT-220 | **Full effect within 60 s across the stamp**; in-flight calls cancelled; user input kept |
| 4 | Modules | Consume `AiKillSwitchActivated`; deterministic fallback | PTY-279, BIL-353, CHN-277, FIN-298 + every module §11 | Agent tools return **503** with a neutral message; pending tickets stay approvable by humans |
| 5 | DAT/CMP | Drift or bias breach: DAT publishes; PLT disables the single feature; CMP register status changes | DAT-005, DAT-218 | **AI-CLM-04 ratio 0.74 against band 0.8–1.25 auto-disables** |
| 6 | All | Re-run E2E-01 and E2E-02 with AI off | contract §3.8 | Identical outcomes |

D4 adds that PLT consumes `AiSystemStatusChanged` and disables within 60 s. This should be added as an extra
E2E-10 assertion (F-118).

#### E2E-11 Out-of-sequence change with an open claim and a cession (regression)

- POL reverse-and-reapply across a **locked FIN month**, in NET and GROSS modes (REQ-POL-006, -121, -122,
  REQ-FIN-297).
- Assertions:
  - the CLM snapshot is unchanged and `ReverificationRequired` is raised once (REQ-CLM-002, POL X-2);
  - BIL re-spreads (BIL #2);
  - DAT written premium is the same in both modes (REQ-DAT-299);
  - for a **P2** home QS treaty, cession target minus booked is correct (REQ-RI-002, REQ-RI-083).
- Notes:
  - D4 fixes NET as a final core key for P1–P2, so the GROSS run is a capability test only (D-104: GROSS only after
    REQ-RI-084).
  - D6 promotes REQ-POL-108 (guard G5, no OOS posting into a closed period), which applies here.
  - D4 adds completeness fields (`set_id`, `set_size`, `index`) that consumers use to process delta sets.

#### E2E-12 Pack rollback after bound business (regression)

- MKT rolls the Greece pack back from **2.4.0 to 2.3.1** with **12 motor transactions** bound under it (REQ-MKT-003,
  `PackRolledBack`).
- POL lists them in an exception queue and raises WRK review activities. **Nothing is re-rated silently.**
- A reviewer raises a reverse-and-reapply correction and binds it explicitly (REQ-POL-354).
- BIL and FIN handle the affected window (MKT 14.x.5). Every step is audited (REQ-PLT-002).

### 4.4 Data and environment prerequisites (§17.4)

| Item | Owner | Source requirement |
|---|---|---|
| Synthetic parties, plates, AFMs, IBANs; sandbox scenarios | DAT, CHN | REQ-DAT-280, REQ-CHN-238 |
| Golden motor policies (≥ 200 per product; ≥ 60 POL scenarios) | PFC, POL | REQ-PFC-234, POL X-7 |
| Golden calendars GR and CY 2025–2030 | PLT | REQ-PLT-007, -009 |
| myDATA development-endpoint contract tests | CMP | NFR-CMP-020 |
| Bank-file library (pain.002, camt.053/054) | BIL | BIL 14.x golden (3) |
| Time-shifted environment | PLT | REQ-PLT-332 |
| Seeded DSAR party across all modules | CMP | REQ-CMP-167 |

---

## 5. Open-questions register (§18.2)

Method (§18.1): 257 source issues (PTY 12, PFC 18, RAT 15, UW 15, POL 15, BIL 14, CLM 12, RI 14, FIN 15, DOC 19,
CMP 19, CHN 15, WRK 11, PLT 15, DAT 15, MIG 15, MKT 18) were merged into 52 questions in themes T-01…T-14. Flags:
**C** contradictory, **S** stale status, **V** UNVERIFIED legal fact.

**Status.** In this range **no XMR-OQ row is marked closed**. The freeze record carries "open questions in section 18"
into the build. Where an accepted decision (D1–D10, contract §3.10.10) settles the design stance, I note it under
"Post-freeze". That note is my cross-reference, not a status the source records. The legal or factual question
usually remains open.

### T-01 Statutory clocks and consumer contract rights

| ID | Question | Sources | Flags | Owner | Gate | Decision link / post-freeze |
|---|---|---|---|---|---|---|
| OQ-001 | Non-payment cancellation: (a) notice duration; (b) acceptable proof of notification | OI-POL-02, OI-BIL-01, OI-MKT-14 (closed part), OI-DOC-03 | S (a), V (b) | ROLE-46 Legal reviewer | (a) G0, (b) G1 | (a) settled by K-01/R-60 at one month (close via CR-POL-03, CR-BIL-02); **(b) OPEN** (D-263 recommends registered post at go-live) |
| OQ-002 | P.D. 237/1986 Art. 11/11a 16-day rule: text, recipient, interplay | OI-BIL-02 | V | ROLE-29 | G1 | **OPEN**; D7 adds the MTPL third-party notice clock "if confirmed" |
| OQ-003 | Withdrawal and refund rights: long-term withdrawal (Law 2496/1997 Art. 8); 30-day refund (Law 2251/1994 Art. 4θ §5) for non-distance sales; Law 5317/2026 transitional rules and who owes the withdrawal function on intermediary sites; Art. 70 content; objection and non-renewal notice durations | OI-POL-03, -04, OI-CHN-03, OI-DOC-16, OI-MKT-14 | V | ROLE-46 (ROLE-32) | G1 | **OPEN** (D-266) |
| OQ-004 | Renewal acceptance mode and advance notice period | OI-POL-14, OI-MIG-02 | C (MIG T-45/T-90 with no legal value) | ROLE-14 | G0 | Acceptance mode decided by D7 (payment as acceptance; one shared lead time). **Lead-time value still OPEN** (legal memo) |
| OQ-005 | MTPL claims: statutory interest rate and basis; offer-clock pause for missing documents; certificates at termination without request | OI-CLM-03, -06, -11 | V | ROLE-46 | G1 | **OPEN** |
| OQ-006 | Nat-cat and disclosure law for UW: JMD 96806/2025 Art. 3(ζ); Law 2496/1997 Arts 3–4; revenue-year rule; business-vehicle nat-cat floor; five-zone seismic map | OI-UW-01, -02, -11, OI-PFC-04, OI-RAT-13 | V | ROLE-46 | G1 (disclosure), G3 (nat-cat) | **OPEN** |
| OQ-007 | Complaints: BoG Act 88/2016 primary text; Greek ADR bodies; current BoG Acts 60/2016, 169/2020, 180/2020; periodic complaint return | OI-CMP-06, -10 | V | ROLE-29 | G1 | **OPEN** |
| OQ-008 | Remaining UNVERIFIED Greek clock values (CMP §10.5 rows 1, 5, 6, 7, 13, 15, 20, 23, 26, 27, 30–33, 38) | OI-CMP-17 | V | ROLE-32 | G2 | **OPEN**; D7 certification gate: production activation only with Settled status |
| OQ-009 | Suspension on plate deposit; MTPL reinstatement without gap after the bureau was told the vehicle is uninsured | OI-POL-07, -15 | V | ROLE-46 | G1 | **OPEN** (D-286: suspension behind a switch once the memo closes) |

### T-02 Taxes, levies and fiscal documents

| ID | Question | Sources | Flags | Owner | Gate | Decision link / post-freeze |
|---|---|---|---|---|---|---|
| OQ-010 | Auxiliary Fund levy: split basis (70/30 of the whole 6%, 4.5% only, or 1.5% only), rates, policyholder stamp duty, base for mid-term changes and cancellations, refund, return form | OI-FIN-01, -02, -07, OI-MKT-17, OI-BIL-03, OI-POL-08 | C, V | ROLE-46 + ROLE-26 | G0 | **OPEN** (opinion commissioned, D2). Keys only: `tax.levy.auxfund.split_basis`, `.establishment_component`, `.ph_stamp_duty_rate`; guarded expectations |
| OQ-011 | IPT: refund on cancellation; distance-withdrawal void; liability point (due vs written) and instalments; IPT on non-cancellation returns; RI-premium exemption; return form and annual report | OI-PFC-03, OI-FIN-03, OI-BIL-05, OI-FIN-08, OI-RI-01, OI-FIN-04, OI-DAT-04, new F-401 | C, V | ROLE-26 | G0 (void, liability point), G2 (returns) | **OPEN** (opinion). D2 defaults: liability point **DUE**; void routed by cancellation source |
| OQ-012 | myDATA: doc types and classifications (premiums, credits, claim settlements, self-billed commission); E3 561_003 vs 563_003; premium-tax category codes; ERP API 2.x fields, batch limit, deadline; trigger point; the two "MARK before" keys; notice of software change and dual transmission during migration | OI-PFC-11, OI-BIL-06, OI-FIN-05, OI-CLM-04, OI-CMP-03, -04, -13, OI-MIG-04, OI-PTY-10 | C, V | ROLE-26 | G0 (trigger), G1 (codes) | Trigger decided by D3 (TRANSACTION for motor; MARK before delivery; one certification rule). **Codes and types OPEN** |
| OQ-013 | Do VAT-exempt premium documents to businesses fall under mandatory B2B e-invoicing (A.1128/2025 as amended by A.1044/2026)? | OI-CMP-02, OI-MKT-08 | V | ROLE-26 | G2 | **OPEN**; D3: interface Must P1, `NotRequired` default |
| OQ-014 | Provisional proof of cover: non-fiscal cover note without amounts before the MARK; which documents show MARK/QR; maximum validity (30 days assumed) | OI-POL-01, OI-DOC-01, -10 | C | ROLE-26 + ROLE-29 | G0 | Design decided by D3 (non-fiscal, no amounts), **subject to the D2 opinion: legal answer OPEN** |
| OQ-015 | A.1004 annual property return layout (deadline 10 January, settled); ENFIA interface | OI-DAT-02, OI-CMP-05 | V | ROLE-26 | G3 (P2) | **OPEN** (not MVP) |
| OQ-016 | Fiscal obligations of a Greek entity's branch abroad | OI-MKT-15 | — | ROLE-26 | G3 (P4) | **OPEN** (not MVP) |

### T-03 Motor ecosystem and external bodies

| ID | Question | Sources | Flags | Owner | Gate | Decision link / post-freeze |
|---|---|---|---|---|---|---|
| OQ-017 | Information Centre (Law 5113/2024 Art. 15): ministerial decision on method, format, channel and deadlines; operator; relation to P.D. 237/1986 | OI-POL-06, OI-CMP-01, OI-MIG-03 | V | ROLE-32 | G2 | **OPEN**; interim MANUAL_FILE with lag thresholds as incidents (D-265) |
| OQ-018 | Friendly Settlement: agreement text, eligibility, limits (EUR 6,500 from secondary press vs 5,000/15,000 in the prompt), clearing-value method, disputes, file spec, reply window (10 business days); monthly net cash path | OI-CLM-01, -02, new F-400 | V, C | ROLE-20 + ROLE-32 | G0 (cash path), G2 (values) | Cash path decided by D1 (`FS_CLEARING`). **Values and spec OPEN**; joining FS at go-live is conditional (D-278) |
| OQ-019 | Green Card and Auxiliary Fund interfaces; Green Card numbering; paper and print rule; mandatory Green Card number field; capture of sex | OI-CLM-05, OI-POL-11, OI-PFC-10, OI-DOC-08, OI-PTY-12 | V | ROLE-32 | G1 | **OPEN** |
| OQ-020 | External risk data: gov.gr Wallet consent (JMD 25061 EX 2024), vehicle registry and valuation, make/model tables, hazard and cadastre, claims-history exchange, credit or driving-record sources | OI-UW-04, OI-POL-13, OI-RAT-12, OI-PFC-07, OI-CHN-01, OI-UW-05, -09, OI-WRK-08 | V | ROLE-42 | G0 (fallback), G2 (contracts) | **OPEN**; D-267 recommends manual capture as first-class fallback; no credit data in MVP |
| OQ-021 | EAEE statistical feeds: categories, format, frequency, basis | OI-PTY-09, OI-CMP-16, OI-DAT-05 | — | ROLE-32 | G2 | **OPEN** |

### T-04 Regulatory reporting, prudential and accounting

| ID | Question | Sources | Flags | Owner | Gate | Decision link / post-freeze |
|---|---|---|---|---|---|---|
| OQ-022 | SII and BoG reporting: taxonomy 2.10.0 template set; deadlines (Del. Reg. 2015/35 Art. 312); article numbers; BoG national templates; Dir. 2025/2 timing; MID Art. 16 | 13 sources (OI-PFC-12, -13, OI-RI-03, -04, OI-DAT-01, -03, -07, OI-CMP-08, OI-FIN-06, OI-PLT-05, OI-RAT-10, OI-MKT-03, -18) | V | ROLE-32 | G2 (first QRT on core Q1 2027) | **OPEN** |
| OQ-023 | IFRS 17 paragraph references; profitability-group inputs at bind not in `PolicyBound` | OI-FIN-12, OI-RI-06, OI-DAT-08, OI-FIN-14 | V | ROLE-35 | G1 | **OPEN** |
| OQ-024 | Accounting set-up: statutory basis; Greek ΕΛΠ chart view; corporate ERP, extract and acknowledgement; XBRL tool boundary | OI-RI-10, OI-FIN-11, -13, OI-DAT-11 | S | ROLE-24 | G1 | Basis settled (R-48). XBRL decided by D10 (in-house in CMP). **ERP and chart view OPEN** |
| OQ-025 | Group context: Fairfax timetable, chart, templates, rate types; group file; scenario A or B (Eurolife completion unverified) | OI-FIN-09, OI-DAT-12, OI-MKT-13, OI-MIG-01 | V | ROLE-24; board | G0 (scenario), G2 | Plan on B (D6); **board confirmation before W5 OPEN; group manual OPEN** |

### T-05 Data protection, retention and AI

| ID | Question | Sources | Flags | Owner | Gate | Decision link / post-freeze |
|---|---|---|---|---|---|---|
| OQ-026 | Retention durations and legal basis for every `RC-*` class | 15 PRDs (OI-PTY-06 … OI-MIG-06) | C, V | ROLE-30 DPO | G1 | **OPEN** (D-259: one schedule as Greece-pack data in REQ-PLT-011) |
| OQ-027 | Lawful basis and DPIA: BI health data; AI processing of P3 content; AI-DAT-07 proxy inference; vulnerable-customer data; team views; production-stamp rehearsal | OI-CLM-07, OI-WRK-07, OI-DAT-13, OI-PTY-05, OI-WRK-06, OI-MIG-07 | C (MIG rehearsal vs REQ-PLT-263) | ROLE-30 | G1 | **OPEN** (D-269) |
| OQ-028 | GDPR Art. 22 for motor pricing and automated declines; HDPA guidance; automated declines on comparison sites | OI-RAT-06, OI-UW-07, -13 | V | ROLE-30 | G2 | **OPEN**; `cap.uw.automated_decline` default off (D-270) |
| OQ-029 | AI Act classification: tariff executor is not AI (recital 12); Annex III 4(b) for staff task allocation | OI-RAT-05, OI-WRK-03 | — | ROLE-29 + ROLE-30 | G1 | **OPEN** |
| OQ-030 | ePrivacy: Greek telemarketing opt-out register (Law 3471/2006 Art. 11); HDPA tracker guidance | OI-PTY-07, OI-CHN-05 | V | ROLE-30 | G2 | **OPEN** |
| OQ-031 | Article citations of Law 4624/2019 Arts 22 and 27, DORA Arts 28/30, AI Act Art. 50 dates | OI-WRK-02 | V | ROLE-29 | G2 | **OPEN** |

### T-06 Operational resilience, identity and platform

| ID | Question | Sources | Flags | Owner | Gate | Decision link / post-freeze |
|---|---|---|---|---|---|---|
| OQ-032 | DORA in Greece: BoG incident channel and format; implementing regulation for forms ((EU) 2025/302 reported); register-of-information date; Law 5193/2025 penalties; BoG ECA 180/2020 and cloud outsourcing notice; TLPT; prior notice of the core migration; Cyprus authority | 10 sources (OI-PLT-01…15, OI-CMP-09, OI-CHN-07, OI-MIG-05) | V | ROLE-39 | G2 (P4 for CY) | **OPEN**; manual fallback REQ-PLT-305 (D-285) |
| OQ-033 | Identity: eIDAS 2 relying-party duties; gov.gr OAuth for private insurers; HR SCIM; PLT as IdP for legacy during coexistence | OI-PLT-06, -08, -09, -13 | V | Identity architect | G1 | **OPEN** |
| OQ-034 | Trust services: QTSP selection; seal level; gov.gr co-signing onboarding | OI-PLT-10, OI-DOC-15, -05 | — | ROLE-39 + ROLE-46 | G2 | **OPEN** |
| OQ-035 | Source repository and CI hosting inside the EU stamp: per entity or shared | OI-PFC-06 | — | ROLE-38 | G0 | **OPEN** in source (D-253 recommends a repository per stamp). Infra decides GitHub Actions; see §14 |

### T-07 Documents and communications

| ID | Question | Sources | Flags | Owner | Gate | Decision link / post-freeze |
|---|---|---|---|---|---|---|
| OQ-036 | Language of record: statutory source for "Greek policies drafted in Greek"; binding language for a real CY pack | OI-PFC-14, OI-DOC-02, OI-MKT-09 | V | ROLE-46 | G1 (GR), G3 (CY) | **OPEN** |
| OQ-037 | IPID Impl. Reg. 2017/1469 article level; Law 4583/2018 POG articles; Greek transposition of IDD Art. 20(1) | OI-PFC-08, OI-DOC-07, OI-PFC-09, OI-CHN-04 | V | ROLE-29 | G1 | **OPEN** |
| OQ-038 | Delivery channels: EETT sender-ID; gov.gr digital mailbox for private insurers; print vendor with registered mail and e-proof (ELTA) | OI-DOC-04, -06, -14 | — | ROLE-45 | G2 | **OPEN** |
| OQ-039 | Document-type catalogue gaps | OI-RAT-15, OI-RI-14 (open at source) | S | DOC owner | G0 | Codes exist (`DT-PRICING-STATEMENT`, seven `DT-RI-*`): close at source via CR-RAT-03 and CR-RI-01 (housekeeping) |

### T-08 Party, distribution and channels

| ID | Question | Sources | Flags | Owner | Gate | Decision link / post-freeze |
|---|---|---|---|---|---|---|
| OQ-040 | Registries and identifiers: AADE RgWsPublic2 terms and limits; GEMI rate limit; Chamber register; new Greek ID card (12-digit personal number); Cyprus TIC algorithm | OI-PTY-01, -02, -08, OI-WRK-04, OI-PTY-11, OI-MKT-07 | V | ROLE-32 | G1 (GR), G3 (CY) | **OPEN** |
| OQ-041 | Intermediary regulation: BoG ECA 169/1/29.4.2020 CPD rules (block or warn); client-money arrangements beyond Law 4583/2018 Art. 25 | OI-PTY-03, OI-BIL-12 | V | ROLE-29 | G1 | **OPEN** (D-276: CPD warn at go-live) |
| OQ-042 | Sanctions: Greek authority and procedure for frozen-funds reports; Fairfax screening policy (lists, thresholds, OFAC) | OI-PTY-04 | — | ROLE-29 | G1 | **OPEN** |
| OQ-043 | Channel and partner scope: ACORD NGDS; aggregator volumes; P1 bank partner, aggregator, white-label; self-service cancellation; broker and reinsurer portals; vendor portal; job aids | OI-CHN-02, -11, -14, -15, OI-RI-09, OI-CHN-08, OI-UW-14 | C | ROLE-09 | G0, G2 | Bancassurance out unless signed (D6). Partner selection and **aggregator OPEN**; cancellation stays REFER (D-271) |

### T-09 to T-14

| ID | Question | Sources | Flags | Owner | Gate | Decision link / post-freeze |
|---|---|---|---|---|---|---|
| OQ-044 | Banking and cards: Greek payment-code formats, file variants, statement timing; acquirer, token portability, SCA for recurring; unclaimed money; late-payment interest and fees on consumer instalments; legacy collection metrics | OI-BIL-04, -07, OI-MIG-15, OI-BIL-09, -11, -10 | V | Billing PO + treasury | G0 (acquirer, banks), G2 | **OPEN** (D-287: select by G0; RF payment codes) |
| OQ-045 | Pricing policy: tariff communication (Law 4364/2016); live champion-challenger excluded from MVP; renewal fairness default; broker-visible factors; Cyprus constraints; PFC–RAT–POL binding confirmation | OI-RAT-02, -07, -08, -14, -03, -01 | S | ROLE-14 | G0 (fairness), G1 | **OPEN** (D-272: fairness default "signal") |
| OQ-046 | UW four-eyes thresholds per entity and line; UW rows in the WRK catalogue | OI-UW-08, -12, OI-WRK-05 | C (`UW-RENEWAL-REVIEW` vs `UW-REVIEW-RENEWAL`; 4 patterns missing) | Head of UW | G1 | **OPEN** (CR-UW-02, CR-WRK-03) |
| OQ-047 | Premium-audit schedules (P3); PML source for commercial risks | OI-PFC-17, OI-RI-11 | — | ROLE-11 | G3 | **OPEN** (not MVP) |
| OQ-048 | Legal form of a bank's interest in mortgaged homes and leased cars; required notices | OI-POL-09 | V | ROLE-46 | G3 (mortgagees), **G1 (leased cars)** | **OPEN** |
| OQ-049 | Actual RI programme: any motor proportional treaty; XoL layers; unlimited MTPL; indexation; surplus capacity; hours clauses; collateral; OED and cat platform; ACORD readiness | OI-RI-05, -02, -13, -12, -07, -08 | V | ROLE-27 | G0 for OI-RI-05 | **OPEN**; XoL-only assumed (D6), confirm before G0 |
| OQ-050 | Migration gaps: reversal operation per owner for `origin=MIGRATION`; legacy interfaces L-03, L-04, L-06, L-07, L-08; run-off claims on converted policies; due dates of migrated activities; mid-term conversion (`LEGACY-PREMIUM`) | OI-MIG-12, -13, -14, OI-WRK-10, OI-RAT-09 | — | ROLE-41 | G1 (mock 2) | **OPEN** (D-260) |
| OQ-051 | Cyprus and Bulgaria facts: CY motor fund contribution and historical stamps; Reg (EC) 1103/97; BG dual display and transliteration | OI-MKT-06, -04, -05, -11 | V | ROLE-42 | G3 (P4) | **OPEN** (not MVP) |
| OQ-052 | Time-saving estimates and prompt statistics (EAEE fraud ≈ 10% etc.) unverified | OI-PFC-16, OI-CHN-09, OI-WRK-09, OI-PLT-12, OI-MKT-16, OI-CLM-08 | V | UX research lead | G2 | **OPEN** |

### Closed at source (§18.2, traceability table)

| Source issue | Closed by |
|---|---|
| OI-PFC-01, OI-RAT-01 (title) | R-21, R-27 |
| OI-PFC-02 | Superseded |
| OI-PFC-05 | R-85 (CY stamp duty repealed) |
| OI-PFC-15, OI-RAT-04, OI-UW-03, OI-CMP-07, OI-CHN-06, OI-DAT-09, OI-WRK-02 (Omnibus part) | REG-007: Reg. (EU) 2026/1744, OJ 24 July 2026, in force 27 July 2026 |
| OI-PFC-18 | Contract §3.8.4, AIC-008 |
| OI-UW-10, -15 | R2B-002 |
| OI-POL-05 | R-55 |
| OI-POL-10, OI-FIN-15 | R-37, R-52 |
| OI-BIL-13 | R-85 |
| OI-BIL-14, OI-DOC-19, OI-CHN-12 | DOC codes; R-84 |
| OI-CLM-09, OI-DOC-11, OI-WRK-11 | Act 88/2016 verified (REG-010; 50 days) |
| OI-CLM-10 | R-38 |
| OI-CLM-12, OI-CMP-15, -19, OI-DAT-14, -15 | R-80, AIC-007 |
| OI-DOC-13 | R-57 (EAA applies) |
| OI-DOC-17, -18 | R-86, R2B-002 |
| OI-CMP-11 | R-62 |
| OI-CMP-14 | R-60 (K-05) |
| OI-CMP-18 | REQ-MKT-326 |
| OI-CHN-10 | REQ-DAT-280/284 |
| OI-DAT-10 | R-85 |
| OI-MIG-08…11 | R-77, R-73, R-72 |
| OI-MKT-01, -02 | Law 5177/2025 Art. 43 (15%/20%), R-85 |
| OI-MKT-10, -12 | R-06; `RC-CFG` |
| OI-DOC-12 | Optional |

---

## 6. Assumptions register (§18.3)

| ID | Assumption | Flag | Owner | Validation | Gate |
|---|---|---|---|---|---|
| AS-01 | Programme volumes (contract §3.9.7): 1.2 m in-force policies (motor ≈ 70%); 2 m transactions/yr; 150,000 claims; 5 m invoices; peak 50 quotes/s | C minor (RAT 840,000 vs 700,000 motor terms; POL ≈ 70,000 renewals/month; FIN tests a 1.5 m-term book; MIG: book unknown) | Migration lead + DAT | Legacy profiling; re-baseline NFRs | G0 (D8 adopts XMR-FR-200) |
| AS-02 | Motor MVP uses annual terms | C (MKT GS-02 six-month) | POL PO + MKT | GS-02 kept as a capability test | G0 (decided D6) |
| AS-03 | Motor P1 is XoL only, no QS | V (OI-RI-05); FIN GF-05 contains a QS recovery | ROLE-27 | Programme documents | G0 (assumed D6, confirmation pending) |
| AS-04 | BIL owns the non-payment notice and cancellation date; DOC supplies the notification date | Consistent (K-01, R-78) | — | E2E-07 | G1 |
| AS-05 | One posting source per fact: FIN posts BIL facts only from `BillingEntryPosted`; claim cash only from BIL disbursement | C (FS, REQ-CLM-158) | Lead architect | D-255 / D1 | G0 (resolved D1) |
| AS-06 | POL emits NET deltas by default | Consistent | — | E2E-11 | G1 (D4: NET final) |
| AS-07 | Auxiliary Fund levy computed by `TaxCalculator` at rating, billed or accrued by BIL, reconciled by FIN | C (three readings) | ROLE-26 | Legal opinion | G0 |
| AS-08 | PLT capabilities arrive before dependents' integration tests: decision-table runtime for nested UW contexts **≤ 150 ms**, bulk ABAC, malware scan, durable business-day timers, external realm, config runtime, workflow, KMS, pipeline | V (UW runtime performance) | Lead platform architect | API-first contract tests; UW spike | G1 (D10: spike before G0) |
| AS-09 | External providers contracted in time: acquirer, ISO 20022 banks, EU e-mail/SMS/print/QTSP, EU fraud scoring, cat-model vendors, **hosting with two EU regions**, legacy extracts, clearing statement, public sanctions lists | V; UW RK4 rates "not contracted at go-live" High | Procurement lead | Procurement plan with DORA register | G0 |
| AS-10 | Legal working positions: not an AML obliged entity; insurer is controller; premiums VAT-exempt; manual Information Centre file acceptable; nat-cat exemptions by authorities; no cheques; agents' commission basis COLLECTED; instalment and dishonour fees permitted; IFRS statutory; annual motor/home PAA-eligible; EUR functional, USD group | V (AML, fees, manual file); IFRS settled (R-48) | ROLE-29/46/24 | Legal opinions | G1 |
| AS-11 | Interfaces: POL calls RAT in-process and resolves external data first; UW evaluates at four checkpoints; CMP reads vehicles via `pol.Policy.get`; POL requests contractual docs via API; CLM events carry the IFRS 17 group from FIN; DAT actuarial results by WD+2; DAT computes regulatory figures and CMP files; DSAR within one business day; CHN `receivedAt` is the statutory receipt time | Consistent (XBRL boundary since decided D10) | Lead architect | Contract tests | G1 |
| AS-12 | Ownership: household is a facet of the personal account; POL holds policy-context party links and drivers; BIL holds payee bank accounts; average reserve tables are actuary config; tariffs produced outside; commission is not a rating output | Consistent (CD-04, CD-14, R-38) | — | — | — |
| AS-13 | Migration: legacy daily extracts and per-policy stop interfaces; 12–13 months coexistence covers motor; import APIs scale; legacy fiscal docs with MARKs available; mandate creditor id unchanged; legacy master for legacy products | V (L-03…L-08) | ROLE-41 | Legacy impact assessment | G1 |
| AS-14 | Organisation: ≤ 50 authors; one Greek legal entity and one stamp at MVP; Greek and English staff UI; corporate directory and HR available; partners federate over OIDC/SAML; one broker plus agents is the P1 intermediary channel | C with PTY A8 | Programme director | Steering decision | G0 |
| AS-15 | Retention placeholders (20 y after policy end; 5 y declined; 5/3/10 y in CMP) are good enough for design | C | ROLE-30 | Retention schedule | G1 |

**Contradictions summary (§18.4):** (1) levy split (F-415); (2) withdrawal full refund vs IPT (F-401); (3) FS
outside BIL (F-400); (4) retention placeholders (F-404); (5) renewal lead time without a legal value (F-412);
(6) stale statuses (F-402, F-403); (7) GS-02 six-month term (F-420); (8) bank partner vs bancassurance date (D-271);
(9) UW pattern codes vs WRK (F-408); (10) cover note designed before the legal answer (F-413).

---

## 7. Decision log (§19)

### 7.1 Decisions already taken (binding, §19.1)

**Orchestration CD-01…CD-20**, with the residual risk this review found:

| ID | Decision | Residual risk |
|---|---|---|
| CD-01 | 16-section PRD structure | None |
| CD-02 | `REQ-/BR-/NFR-<MOD>-NNN` IDs | Local `R-nn` risk IDs collide with rulings (F-421) |
| CD-03 | Anchors 001–029 reserved | None |
| CD-04 | Account in PTY, policy contracts in POL | None |
| CD-05 | Authority framework in PLT | Default thresholds not approved (D-275) |
| CD-06 | Config model MKT, runtime and flags PLT, switches MKT | Pack rollback added late; covered by E2E-12 |
| CD-07 | CMP owns the clock register and instances; MKT values; modules start/stop; WRK activities; mirror clocks (R-61) | 16 values UNVERIFIED; renewal-notice value missing |
| CD-08 | Regulatory mapping split MKT / PFC / DAT | MTPL SII LoB vs statistical class citations differ in PFC |
| CD-09 | DOC owns outbound, archive and Documents tab; WRK inbound | None |
| CD-10 | WRK owns groups, queues and assignment; PLT identity | UW/WRK routing split (D-280) |
| CD-11 | AI governance split PLT / DAT / CMP | 119 features registered; enablement at go-live undecided (D-274) |
| CD-12 | PTY owns sanctions screening | List sources and degraded mode (D-276) |
| CD-13 | BIL owns payment execution; CLM requests disbursement | FS bypassed BIL; fixed by D1 |
| CD-14 | Commission: PTY agreements, BIL calculation, FIN postings | Self-billing fiscal treatment (OQ-012) |
| CD-15 | BIL sub-ledger and FIN books reconciled daily | FS broke the single cash source; fixed by D1 |
| CD-16 | Wave order from dependencies | None |
| CD-17 | Calendars, currencies and FX are PLT reference data | None (but RI and WRK call SPIs directly, F-306) |
| CD-18 | Entity search by owner; global search by WRK | None |
| CD-19 | ≥ 120 FRs per PRD; every Must with G/W/T | Met (smallest PRD: MIG, 187) |
| CD-20 | **Built from scratch, vendor-neutral** | None found |

**Contract rulings R-01…R-100** by theme (§19.1.2), with open residuals:

- **Entity ownership:** two technical table names are still shared (`IdempotencyRecord`, `InvariantResult`; R3-010
  open). `RC-MKT-CFG` vs `RC-CFG` (R3-003b open).
- **Events and keys:** about 150 events added; producer §8 is authoritative; partition key = stable aggregate id.
  Residuals:
  - 119 consumer pairs without a handler (R3-002 open);
  - R-39 "CLM key is claim_id" vs certificate and cat keys (R3-005);
  - FIN `PeriodReopened` omits WRK (R3-003c).
- **SPIs:** 14+ added (`PricingConstraint`, `Geocoder`, `PolicyLifecycleRules`, `StatutoryDeliveryRule`,
  `PaymentChannelProvider`, `MotorCompensationBodyAdapter`, `FriendlySettlementClearing`, `TaxReturnFormat`,
  `StatutoryDataReturnFormat`, `LegacyDataProfile`, `MotorDataProvider` wallet operations). REQ-CLM-155 cites a
  non-existent §9.4.40 (R3-004 open).
- **Statutory clocks:** K-01 non-payment is one month; K-05 is a single Auxiliary Fund clock; DEADLINE vs
  WAITING_PERIOD kinds.
- **Four-eyes extensions:** R-18, R-25, R-45, R-51, R-64, R-75.
- **Pricing binding:** three-hash key, "floating at resolution, pinned for the term".
- **Settled legal facts:**
  - R-48 IFRS is the Greek statutory book;
  - R-55 distance withdrawal refunds the full premium (Law 5317/2026 Art. 72), with tax left undefined (F-401);
  - R-57 EAA applies;
  - R-85 A.1004 is due by 10 January, and Cyprus stamp duty is repealed.
- **AI governance:** R-12, R-58, R-67, R-79–81. AI-PFC-04 bias cell open (R3-008).
- **Availability:** R-13, R-66, R-74, R-83 (bind uses read models, never synchronous T2 calls).
- **Migration:** R-08, R-77, R-88, R-96 (`mig.Routing.resolve` with event-fed caches).
- **Vocabulary:** R-86 sets the P1–P4 and MoSCoW vocabulary; R-97 says DAT erasure completes synchronously.

**PRD design stances (§19.1.3),** which bind build design:

- **PTY:** party ≠ role ≠ account; one fail-closed sanctions engine.
- **PFC:** **Git as source of record** with a compiled artefact.
- **RAT:** deterministic executor, three-hash key, no external call on the rating path.
- **UW:** rules as data on the PLT decision-table runtime; confidence lanes are rules, never AI.
- **POL:** intent log plus materialised bitemporal segments; NET deltas; reverse-and-reapply; bind gates from read
  models.
- **BIL:** immutable sub-ledger; `BillingEntryPosted` is the FIN intake; one disbursement service; fail-closed VoP.
- **CLM:** ledger-shaped financials; one payment pipeline with BIL.
- **RI:** target-minus-booked cession ledger; deterministic recovery recomputation.
- **FIN:** two ledgers reconciled daily; IFRS 17 grouping once at recognition.
- **DOC:** reproducible from three frozen inputs; proof of cover immediate, fiscal documents wait for the MARK.
- **CMP:** fiscal registration asynchronous and never blocks bind; one clock engine; DSAR as a fan-out saga.
- **CHN:** no business logic in front ends; deny-by-default permission matrix; MCP facade with tickets.
- **WRK:** one activity model.
- **PLT:** "**Custom identity service**"; append-only audit with hash chain; AI control plane.
- **DAT:** events first, CDC backstop; bitemporal; "as reported" snapshots.
- **MIG:** import APIs only; renewal-based conversion; one master per policy and date.
- **MKT:** pack code ships with the release, pack data activates at runtime; configuration hash as content address;
  silo per legal entity; Cyprus stub as a release gate.

### 7.2 Decisions required (D-250…D-288) and status

PRD-18 itself records these as "still required". The status column applies the contract §3.10.10 acceptance of D1–D10,
which absorbed many of them. "Open" means not covered by D1–D10 in the sources I read.

| ID | Subject | Recommendation (source) | Gate | Status after freeze |
|---|---|---|---|---|
| D-250 | Owner of the E2E suite | (b) programme test lead, PLT pipeline, MKT harness | G0 | **Decided (D9)**; test lead to be named |
| D-251 | Technology foundation: Kotlin or .NET; open-source event stream, workflow engine, monitoring; hosting with two EU regions; table format; decimal library; one expression language; UW 150 ms spike | One language and one expression language; UW spike before G0 | G0 | **Decided (D10)**: .NET, CEL-compatible in-house expression language. D10 text keeps "open-source, self-operated event stream, workflow engine and lakehouse table format", which **conflicts with infra** (§14) |
| D-252 | Identity and trust: passkey-only or 90-day OTP exception; federate from the corporate directory; OIDC first and SAML for banks; gov.gr co-signing onboarding with commercial fallback; seal level | As stated | G0 (onboarding), G2 | **Open** (not in D1–D10). Infra: Entra ID / External ID |
| D-253 | Configuration and pack model confirmations (pack code vs data, hash, SPI binding, silo per entity, fork-ratio 12%/15%, golden suite of 50 scenarios, Greek tax rounding order, YAML schema v1, repository topology, three-hash key) | Confirm; repository per stamp | G0 | **Decided (in D10 coverage)** |
| D-254 | Core engine confirmations: POL intent log; NET default; LATER_WINS with REFER field groups; motor bind gates; CLM model; RI ledger; FIN single source | Confirm, with the FS exception | G0 | **Decided (D10, amended by D4)** |
| D-255 | FS cash path | (b) BIL `FS_CLEARING` | G0 | **Decided (D1)** |
| D-256 | Auxiliary Fund levy reading | Opinion; keys only; guarded expectations | G0 | **Mechanism decided (D2); reading OPEN** |
| D-257 | IPT treatments incl. withdrawal void | Exclude net-of-tax refund; AADE position; source-aware rule | G0 | **Mechanism decided (D2); reading OPEN** |
| D-258 | Fiscal design: trigger, doc types, MARK timing, B2B switch | TRANSACTION; MARK before delivery; one certification rule; e-invoicing off | G0 | **Decided (D3)**; doc-type codes open (OQ-012) |
| D-259 | Retention schedule | One DPO-approved schedule as Greece-pack data | G1 | **Open** (next in line) |
| D-260 | Migration strategy D-02…D-10 and reversal operations per owner | Adopt MIG recommendations; reversals named before mock 2 | G1 | **Open** (next in line) |
| D-261 | Greek statutory value package | Approve structure now; values frozen with legal sign-off | G0 (structure), G2 (values) | **Structure decided (D7)**; values open |
| D-262 | Renewal acceptance mode and lead times | Payment as acceptance | G0 | **Decided (D7)**; value open |
| D-263 | Proof media for statutory notices | Registered post only at go-live; electronic once counsel confirms | G1 | **Open** |
| D-264 | Provisional proof of cover | Non-fiscal cover note, subject to opinion | G0 | **Decided (D3)**, subject to the D2 opinion |
| D-265 | Interim Information Centre method | Manual file; severity-2 incident at the threshold | G2 | **Open** (next in line) |
| D-266 | Withdrawal-function scope (phone, intermediary sites) | (b) also on intermediary sites via API once OQ-003 confirms | G1 | **Open** |
| D-267 | External motor data sources and fallback | Manual capture fallback; start Wallet onboarding | G0 | **Open** (not in D1–D10) |
| D-268 | Legacy scenario A or B; group reporting | Plan for B | G0 | **Decided (D6)** pending board confirmation before W5 |
| D-269 | DPIA package | Defer AI-DAT-07 and AI-PTY-06 unless approved | G1 | **Open** |
| D-270 | Automated declines and Art. 22 | Off at go-live | G2 | **Open** (default off) |
| D-271 | Channels and partners | Matrix per PRD-12 §10.1.3; bank and aggregator only if signed by G0; MCP limited to first-party assistants; WCAG external audit; cancellation REFER | G0 | **Bancassurance part decided (D6)**; rest open |
| D-272 | Pricing policy (step language, TERM_RATIO day count, fairness, oracle, factors, tariff notification) | Adopt RAT recommendations; fairness "signal" | G0 | **Open** (not in D1–D10) |
| D-273 | Reinsurance (A-01, clause options, accumulation, exchange channels, security, RI accounts, IFRS 17 RI-held granularity) | Confirm A-01 before G0 | G0 (A-01), G3 | **A-01 assumed (D6)**, confirmation pending |
| D-274 | AI features enabled at go-live | Pilot list off by default: AI-PFC-02, -07, AI-RAT-01, -02, AI-CLM-01, -05, AI-BIL-01, -03, AI-DOC-02, -04, AI-RI-01, AI-PTY-01, -04; fraud scoring rules-only | G2 | **Open** (next in line) |
| D-275 | Authority limits, four-eyes and SoD defaults (CLM four-eyes EUR 50,000; BIL refunds EUR 500 / 5,000) | Approve PRD defaults; review after 3 months | G1 | **Open** |
| D-276 | Party and distribution policies (unmerge 90 days, account-merge reversal 30 days; lists and OFAC; bind fail-closed for payment screening; CPD warn) | As stated | G1 | **Open** |
| D-277 | Finance and IFRS 17 elections | Confirm book profile; elections before G1 | G1 | **Open** |
| D-278 | Claims operating model (FS at go-live, SIU, vendors, 72-hour cat replay as a go-live gate) | As stated | G2 | **Open** |
| D-279 | Document operations (two print vendors, providers, wording freeze 12 weeks before go-live, in-house engine, **PDF/A-3** with embedded payload, versioned Greek fonts) | Adopt DOC recommendations | G0 (procurement), G2 | **Open** |
| D-280 | Work management model (state mapping, quarantine release, confidentiality, push/pull, motor catalogue, UW vs WRK routing) | Adopt WRK; UW owns priority inputs, WRK assignment | G1 | **Open** |
| D-281 | Data platform (events-first with CDC backstop, XBRL boundary, SII taxonomy transition 2.8.x / 2.10.0, actuarial tooling) | Adopt DAT; CMP renders and files | G1 | **Partly decided (XBRL, D10)**; rest open |
| D-282 | Compliance model (DSAR map, complaint rules, regulatory change intake) | Approve; fix CMP-154 | G1 | **Open** |
| D-283 | Vehicle data sourcing | (c) local tables refreshed from the provider | G1 | **Open** |
| D-284 | UW model confirmations | Confirm | G0 | Not explicitly in D1–D10 (D10 confirms "engines as written"); treat as **open / implied** |
| D-285 | DORA operating model | Approve with manual fallback | G2 | **Open** |
| D-286 | Motor product scope: household discount, six-month terms, plate-deposit suspension | No household discount, annual only, suspension behind a switch | G0 | **Annual terms decided (D6)**; household discount and suspension not stated in D6 (my reading: open) |
| D-287 | Banking and card providers | Select by G0; RF payment codes | G0 | **Open** |
| D-288 | Document-control normalisation | (b) one version, contract v1.10 cited, recomputed counts | G0 | Freeze record condition 3 says G0 CRs applied (FZ-01…11). Treat as done |

§19.2.2 maps all 170 PRD "ten decisions" to these IDs. Three were already settled: RI 5 (R-48), CHN 2 (R-55) and
CLM 2 (R-38).

### 7.3 The ten freeze-gating decisions D1–D10 and their status

**Cross-check result:** contract §3.10.10 (v1.11, kept in v1.12) says D1–D10 were "accepted as recommended" by the
programme sponsor on 2026-10-07. PRD-18's freeze record agrees ("Condition 1 — D1–D10 recorded: Met"). **All ten are
DECIDED.** The underlying legal readings (D2 tax/levy, the D3 cover-note opinion, D7 clock values) remain open and are
gated to G2.

| # | Decision | Accepted content (contract §3.10.10) | Covers | Residual open |
|---|---|---|---|---|
| D1 | One cash executor | BIL is the only money executor. New sources `FS_CLEARING`, `CMP_REDRESS`, `TAX_REMITTANCE`, each with approval evidence, a billing-ledger rule and a FIN posting rule; `RI_SETTLEMENT` in the enumeration; `CLEARING` payment method (about 8 BIL requirements, 3 FIN rules) | F-214/-400, F-108/-215, F-216; D-255, -102, -156 | New BIL/FIN requirements must be written |
| D2 | Greek tax and levy package | `TaxCalculator.treatment` owned by MKT; the originating module calls `TaxCalculator` once; BIL taxes its own fees; PFC drops `gr.ipt.refund_on_cancel`; FIN-182 becomes a pack rule. Opinion commissioned. Defaults: liability point **DUE**, levy keys only, withdrawal void routed by cancellation source, net-of-tax refund excluded; guarded expectations; legal sign-off gates go-live | F-210…-213, -218, -401; D-152…-154, -256, -257 | Opinion (levy split, IPT liability point, withdrawal void, fees in IPT base) |
| D3 | Fiscal documents | CMP is the only issuer of fiscal series and numbers; TRANSACTION trigger for motor; MARK before **delivery** for premium receipts under one certification rule over the two keys; cover note non-fiscal without amounts (subject to D2); B2B e-invoicing interface Must P1 with `NotRequired`; BIL invoices are "ειδοποίηση πληρωμής", never "τιμολόγιο" | F-101, -413, -416, -302, -127; D-100, -258, -264, -206 | myDATA doc types and codes |
| D4 | v1 event contract | Additive completeness fields (`set_id`, `set_size`, `index`) on charge-delta and OOS aggregate events; NET delta mode final for P1–P2; `DisbursementRejected`, `DisbursementStopped`; PLT consumes `AiSystemStatusChanged`, disabling within 60 s | F-114, -115, -200, -118, -132; D-107, -104, -151, -110 | Field names differ from F-114's proposal (`transaction_delta_count`, `aggregate_member_count`); use the contract names |
| D5 | Definitions of record | Written premium = premium + surcharge + discount categories, net of reversals, **excluding tax, levy, fee**, booking-date basis. Journey lineage by business keys (trace id technical only). Glossary = PRD-18 §6; event catalogue = PRD-18 §8; canonical Quote, PolicyTransaction, Reserve and Refund (§9); intervals half-open `[valid_from, valid_to)`, UTC record time (XMR-FR-150) | F-122, -116, -001, -127, -128, -131, -202, -239 | — |
| D6 | Motor MVP scope | Scenario **B** (board confirms before W5); bancassurance **out** unless a partner is signed by G0; motor RI **XoL-only** (confirm before G0); 9 Shoulds promoted; **annual terms only** | F-374, -370, -004, -362…-369, -420; D-212, -200, -273, -203, -286 | A-01 confirmation; board scenario confirmation |
| D7 | Statutory clocks | Adopt S3 §11.3: MTPL payment clock from proven offer delivery; new clocks (repair in kind, insurer termination notice, amendment acceptance, MTPL third-party notice if confirmed, distance-withdrawal long stop); GDPR breach clock owned by CMP; renewal and non-renewal notices FIXED_DATE. Renewal = payment as acceptance; one shared lead time. Value structure approved; certification gate (Settled status) for production; CR-CLM-04 withdrawn | F-230…-233, -412, -307, -002; D-158, -157, -262, -213 | Clock values (OQ-008) |
| D8 | Capacity and recovery | XMR-FR-200: about 100 rated quotes/s peak; RAT ≥ 400 ratings/s (1.5× headroom); UW ≥ 200 evaluations/s; about 60 m events/yr, designed for 180 m, **≥ 2,000 events/s**; renewal 150,000 terms ≤ 4 h; one cat design event; DOC T1 path (pre-contractual pack, cover note, archive write, withdrawal acknowledgement) RTO ≤ 2 h | F-320…-324, -328; D-207, -208 | MIG/DAT confirmation of volumes |
| D9 | Build order and acceptance | Requires / Used by / Family columns; W1–W9 baseline; named test lead owns E2E-01…12 as a release gate | F-360, -361, -375, -405, -418, -001; D-201, -202, -214, -250 | Name the test lead |
| D10 | Technology foundation | **.NET (C#)**; one typed deterministic rule-expression language, CEL-compatible, in-house, shared by PFC, UW and the PLT rules runtime; decimal with explicit rounding (REQ-MKT-006); "open-source, self-operated event stream, workflow engine and lakehouse table format (CD-20 still applies)"; UW 150 ms spike before G0; engines confirmed with the D4 amendment; XBRL in-house in CMP (two EIOPA taxonomies in parallel), DAT supplies the data-point package and SCR results | F-107; D-251, -253, -254, -101, -281 | **Conflict with infra** on event stream, workflow engine and lakehouse (§14) |

**Next in line, not freeze-gating:** D-259 retention (G1); D-260 migration reversals (G1, before mock 2); D-205 and
D-204 search folding and plate normalisation as pack rules (G1; D-205 due before W2 builds search); D-265 Information
Centre (G2); D-274 AI at go-live (G2).

---

## 8. Freeze recommendation and freeze record

**Recommendation as written:** "The baseline is not ready to freeze today. It can be frozen as baseline 1.0 once the
ten decisions above are recorded and the G0 changes are applied."

- The documents are complete:
  - all 16 sections present;
  - 187–339 FRs per PRD;
  - every Must has G/W/T;
  - no undefined or duplicated IDs;
  - no blocker found.
- **48 major inconsistencies, 37 of them must be fixed before freeze:**
  - money: three paths with no cash executor; tax defined in four places on open readings; two fiscal-number issuers;
    four definitions of written premium;
  - events: v1 payloads cannot signal set completeness;
  - clocks: one clock may start from the wrong event (BoG Act 87/2016);
  - sizing and scope: contradictory capacity figures; the cut rests on three undecided scope choices.
- BIL, FIN and MIG were "not ready on their own" pending D1, D2, D5 and D6.
- **Conditions:**
  1. D1–D10 recorded;
  2. contract v1.11 with the §5.1 contract changes;
  3. owners apply the CRs behind the 37 G0 majors and D-288;
  4. mechanical scan and re-check pass;
  5. the D2 opinion is commissioned (it need not have landed).
- **May proceed now:** W1 and W2 under interface-first contracts. The 10 G1 majors and 88 minors are applied during
  the build, each before SIT of its journey.

**Freeze record, baseline 1.0 (2026-10-07):**
- **Frozen**: PRD-01…17, PRD-18, `00-system-contract.md` **v1.12**, `00-baseline-inventory.md`,
  `00-integration-review.md` with freeze addendum.
- Condition status:
  - C1 met (sponsor; contract §3.10.10);
  - C2 met (v1.11 rulings, body aligned in v1.12, FZ-01);
  - C3 met (37 G0 majors: 30 resolved and 7 partial, all 7 closed by FZ-02…FZ-11);
  - C4 met (0 dangling references, 0 multiple producers, 0 non-reciprocated dependencies, 0 malformed rows;
    `check_prds.py` passes);
  - C5 commissioned (Greece-pack readings run as guarded switches, REQ-MKT-331 and REQ-MKT-343; legal sign-off is a
    G2 gate).
- **R-101 Greek/English switch** is in the baseline:
  - an "ΕΛ | EN" control in the shell header and command palette;
  - no reload and no lost input;
  - choice stored as BCP 47 `el`/`en` in the PLT identity profile (REQ-PLT-357, REQ-CHN-321, REQ-MKT-005,
    REQ-MKT-333…338);
  - a translation-completeness gate blocks releases;
  - customer documents follow the customer's language preference.
- **Carried into the build:** the 10 G1 majors, the 88 minors (§20) and the §18 open questions.

Note: these requirement IDs (PLT-357, CHN-321, MKT-331, -333…338, -343) are **post-freeze additions** and are **not in
the §16 cut counts** (3,710). The Phase 1 backlog must add them, plus the D1 BIL/FIN additions and the 9 promotions.

---

## 9. Change requests per PRD (§20; status)

Sources (§20.1): integration-review Minor items R3-002…R3-010 (re-checked 2026-10-07), document-control defects, and
findings F-400…421. Status: §20.3 says every item other than R3-001, R3-003(a) and R3-006 was **still open** when PRD-18
was written. The freeze record says the CRs behind the 37 G0 majors and D-288 (document-control normalisation) were then
applied (FZ-01…11). It does not give per-CR status. So the G0 ones (marked **G0**) are presumably applied, and the rest
are "carried into the build".

| CR | PRD | Change (abbreviated) | Source | G0? |
|---|---|---|---|---|
| ALL-01 | All 17 | One binding input "contract v1.10, R-01…R-100"; ordered change log; recomputed self-check | F-409, D-288 | G0 |
| ALL-02 | CLM, DAT, MKT, PLT, POL, RAT, RI, WRK, CMP | Trim §8.1 consumers to modules with a §8.2 handler (86 pairs) or add the handlers | R3-002 | — |
| ALL-03 | PFC, RAT, POL, RI, FIN, MIG | Rename local `R-nn` risk IDs to `RK-<MOD>-nn` | F-421 | — |
| PTY-01…05 | PRD-01 | Version cell; per-R-100 keys (l.1562); CCR-PTY-02 "Accepted (R-15)"; §3.1 vs §16.7 settled-citation mismatch; REQ-PTY-277 Must/P3 contradiction (make Should or justify) | F-409, R3-005 | 01 G0 |
| PFC-01…04 | PRD-02 | Binding inputs; REQ-PFC-009 lists all 8 events; CCR-PFC-03 consumer alignment; AI-PFC-01 phase P2 | — | 01 G0 |
| RAT-01…06 | PRD-03 | RAT-110 "no stamp line from 2026-01-01"; version; close OI-RAT-15 and retitle OI-RAT-01; J-03 cite RAT-127/129; RAT-066 add `RateVersionWithdrawn`; `IdempotencyRecord` internal or renamed | R3-009a, R3-010 | 02 G0 |
| UW-01…03 | PRD-04 | Contract versions; 265 vs 267 count; use `UW-REVIEW-RENEWAL` and add 4 patterns in WRK; CCR-UW-03 "Accepted (R-30)" | F-408 | 01 G0 |
| POL-01…05 | PRD-05 | Versions; CAP-POL-17 range; close the OI-POL-02 duration part; **REQ-POL-207 distance-withdrawal void (refund = total paid; tax via D2)**; `IdempotencyRecord` | F-401, F-403 | 04 G0 |
| BIL-01…06 | PRD-06 | "Billing-ledger rule table"; close the OI-BIL-01 duration part; **new FS_CLEARING source after REQ-BIL-351**; **INV-05 conditional on cancellation source**; S-18 v1.10; `InvariantResult` | F-400, F-401 | 03, 04 G0 |
| CLM-01…07 | PRD-07 | CLM-155 → "PRD-17 §9.4.23, extended in §9.4.43"; key certificate and cat events per R-100; **CLM-158/160 hand the FS net to BIL; add CLEARING method**; CLM-04 (**withdrawn by D7**); OBL-EIDAS cite CLM-167; binding inputs; cite the REQ-MKT ID for `MotorCompensationBodyAdapter` | R3-004, R3-005, F-400 | 03 G0 |
| RI-01…03 | PRD-08 | Close OI-RI-14; close the OI-RI-10 basis part (R-48); binding inputs v1.10 | F-402 | — |
| FIN-01…05 | PRD-09 | `PeriodReopened` consumer WRK; **FIN-037 FS case: cash from BIL `FS_CLEARING`, netting per statement**; **FIN-182 source-aware per D2**; version and CCR-FIN-02; `InvariantResult` | R3-003c, F-400, F-401 | 02, 03 G0 |
| DOC-01 | PRD-10 | Self-check closed count 6 vs 5 + 1 optional | F-410 | — |
| CMP-01…06 | PRD-11 | AI-PFC-04 bias cell; 10 "intended consumers" rows (BIL/FIN for `FiscalDocCancelled`, POL for `BureauFactRejected`); **CMP-154 module count = 15-row map**; status; `cmp.fiscal.mark_before_issue` certification rule; "42 high-risk" counting rule | R3-008, R3-002, F-419 | — |
| CHN-01…04 | PRD-12 | Cite `mig.Routing.resolve` (REQ-MIG-149, anchor MIG-004), R-96; CHN-064 vs BR-CHN-013 step-up lists identical; consumer lists of 4 events; dagger legend | R3-007 | — |
| WRK-01…04 | PRD-13 | P1/P2 vocabulary; remove "proposed" on R-02 events; **add UW patterns**; capability-map ranges and source v1.1 | F-408 | — |
| PLT-01…02 | PRD-14 | Delete `RC-MKT-CFG` row (l.1929); contract v1.8 and the "Ph = M, H, C, L" vocabulary | R3-003b | — |
| DAT-01…02 | PRD-15 | Contract version; add `RegulatorySubmissionFiled/Rejected` to §8.2 | — | — |
| MIG-01…03 | PRD-16 | Status v1.10; DORA row cites MIG-187; **list each owner's reversal op for `origin=MIGRATION`** once D-260 decides | F-406 | — |
| MKT-01…07 | PRD-17 | MKT-327 uses CHN group codes (DIRECT, AGENCY, BROKER, BANCASSURANCE, AGGREGATOR); drop the OI-MKT-12 phrase; self-check 304 vs 310 and SPI count 23/38/40; Bulgaria GS-47 vs GS-45; MKT-321 clock list matches §10.4.3; **GS-02 as capability test**; contract version | R3-009c/d, F-410, F-420 | 06 G0 |
| CON-01…02 | Contract | R-39 key wording; §3.9.12/13 order | R3-005 | — |

**Annex part CRs (merged by S1).**
- **S2 per-PRD CRs (Annex):**
  - PTY `PartyUnmerged` key;
  - POL completeness fields, NET final, "written charges", Green Card numbering;
  - BIL non-fiscal invoices, `CMP_REDRESS`, RI source, push reconciliation;
  - CLM single offer-warning path;
  - RI `source_correlation_key`;
  - FIN handlers for RI deposit, adjustment and commission events;
  - DOC prints the CMP series and MARK;
  - CMP XBRL renderer;
  - PLT consumes `AiSystemStatusChanged`, marks `plt.Config.*` internal, lineage-based REQ-PLT-132;
  - DAT ingests SCR results, restricts CDC;
  - MKT `mkt.Configuration.*` as the only resolve operations.
- **S3 CR-S3-01…25:**
  - BIL publishes `DisbursementRejected/Stopped`; `bil.refund.paid_point` (Greece ISSUED); `bil.payee.cooling_off`
    per purpose;
  - CLM consumes them; RI consumes them;
  - FIN corrects via the source module;
  - PFC removes `gr.ipt.refund_on_cancel`;
  - the `TaxCalculator.treatment` operation;
  - BIL taxes its own fees;
  - FS, redress and tax remittance sources;
  - clock fixes in CMP, MKT, CLM, UW, POL and PLT (GDPR clock to CMP; DORA formula = classification + 4 h);
  - contract additions XMR-FR-150, canonical models, OBL-EQT, OBL-IRRD.
- **S4 CR-S4-01…20:**
  - column split;
  - PTY, WRK and DOC Greek folding → MKT language rules;
  - promotions;
  - NFR resizing (RAT, UW, POL ≥ 40,000/h, PLT event model);
  - DOC RTO ≤ 2 h;
  - MKT-089, -106, -316 Must;
  - `IdValidator` VEHICLE_PLATE;
  - Cyprus stub variants;
  - persona register with ROLE-48 Risk manager and ROLE-49 Model validator.

---

## 10. Key findings from Annex A

These matter for the build and are not already covered above.

- **F-101 / D3:** CMP is the only fiscal-number issuer. BIL invoice numbers are non-fiscal; DOC prints the CMP series
  and MARK.
- **F-106:** BIL pushes daily totals to `fin.Reconciliation.exchange`. `bil.Reconciliation.fin` is read-only and must
  be defined (F-344).
- **F-112 / F-113 / D-103:** two cross-schema exceptions:
  - PLT outbox, inbox/dedupe and idempotency tables live in each module schema, are written by that module and are read
    by the PLT relay;
  - DAT log-based CDC only on data-contract-declared tables, never feeding silver/gold, 35-day retention.
- **F-117:** partition keys. FIN by journal/period/run id; `ConfigChanged` by resolution-context id; PTY merge and
  unmerge on the survivor id.
- **F-118 / D4:** PLT must consume `AiSystemStatusChanged`.
- **F-120:** unnamed operations to define: `clm.Financials.dailyTotals`, `bil.Invoice.get`, `bil.Refund.decide`,
  `clm.TransactionSet.approve`.
- **F-127:** Greek terms. Invoice = ειδοποίηση πληρωμής; complaint = παράπονο; termination by notice = καταγγελία
  σύμβασης.
- **F-130:** identifier issuers:
  - Green Card via a POL pack series fed by bureau ranges;
  - cover-note number = DOC document number;
  - first core term number on migration = legacy count + 1;
  - BIL-originated charges carry `charge_origin = BIL`.
- **F-132:** FIN needs handlers for RI `DepositPremiumDue`, `PremiumAdjustmentCalculated` and `CommissionAdjusted`
  (motor XoL deposit premium in P1).
- **F-200 / D4:** BIL `DisbursementRejected` and `DisbursementStopped`. CLM maps Rejected to `PaymentVoided`.
- **F-201:** RI-sourced journals are corrected only through RI.
- **F-204:** PolicyTerm InForce is derived from `valid_period` and the time service. Consumers must state this.
- **F-211:** IPT base = "due premiums and rights of every kind arising from the insurance contract" (Law 5177/2025
  Art. 43). BIL-originated fees need a `TaxCalculator` call.
- **F-212:** the statute taxes "απαιτητά ασφάλιστρα". Greece default set to **DUE** (D2), replacing the REQ-MKT-328
  default WRITTEN.
- **F-213:** the 6% ceiling and 70/30 split are confirmed. The 4.2% / 1.8% hard-coded in REQ-MKT-322 is one reading.
- **F-230 / D7:** MTPL payment within 10 days from the offer (BoG Act 87/2016 Art. 6).
- **F-231:** new clocks:
  - repair in kind within 20 days (Act 87/2016 Art. 6, verified);
  - 15-day termination effect after a non-disclosure notice;
  - one-month deemed termination of an unaccepted amendment (Law 2496/1997 Art. 3);
  - 16-day MTPL third-party notice (UNVERIFIED);
  - long stop of 12 months + 14 days for distance withdrawal.
- **F-232:** the GDPR 72-hour breach clock is owned by the CMP engine. PLT starts and stops it; the DORA timers stay in
  PLT.
- **F-234:** one payee cooling-off key in BIL (BIL 30 days vs CLM 72 hours today).
- **F-235:** `POL_REFUND_DUE` is 30 calendar days from receipt (Art. 3ιστ Law 2251/1994 via Law 5317/2026 Art. 72).
  `bil.refund.paid_point` Greece default ISSUED.
- **F-236:** DORA initial notification = classification + 4 h, capped at awareness + 24 h only if classified within
  24 h (Del. Reg. 2025/301).
- **F-238:** raise REQ-CMP-138, -202 and -115 to Must P1 (supervisor replies, fiscal transmission, DORA mirror).
- **F-253 / F-254:** new obligation codes OBL-EQT (equal treatment in pricing) and OBL-IRRD (Dir. (EU) 2025/1,
  transposition by 29 Jan 2027).
- **F-255:** SII deadlines for 2026: 5 weeks quarterly, 14 weeks annual and SFCR.
- **F-300 / F-301:** Greek search folding moves to MKT language rules. Plate normalisation becomes an `IdValidator`
  scheme `VEHICLE_PLATE` (`normalise`, `searchKey`).
- **F-307 / D-213:** 22 of 32 Greece pack index rows are not Settled (17 on the motor path). Certification gate.
- **F-320…F-332:** capacity figures (see D8):
  - bind ≤ 800 ms p95 requires concurrent gate evaluation (screening 300 ms p95);
  - clock warnings within 60 s while the clock engine is T2 (RTO 4 h), so a catch-up rule is needed;
  - daily RI↔FIN totals by 06:00.

---

## 11. Sources (§21) and self-check limits

- **Verified 2026-10-07:**
  - Law 5317/2026 Art. 72: consumer pays nothing on distance withdrawal; silent on IPT.
  - Law 5177/2025 Art. 43: IPT 20% fire, 15% other non-life, 4% life; no refund or reinsurance provision.
  - AADE A.1185/2024: A.1004 due 10 January.
  - Reg. (EU) 2026/1744 (Digital Omnibus on AI): OJ 24 July 2026, in force 27 July 2026 (secondary sources).
  - Law 2496/1997 Art. 6 §2: one-month non-payment period (secondary source).
- **UNVERIFIED in this session:** Directive 2009/103/EC Art. 22, three-month offer (EUR-Lex returned no text).
- **Totals:** 376 source rows across the PRDs. A large share is "citation, not opened" or secondary.
- **Self-check limits:**
  - no semantic duplicate scan of the 4,697 requirements;
  - payloads checked field by field only for money and gate events;
  - Greek checked for consistency, not legal correctness;
  - primary texts not fetched: Law 2496/1997 (HTTP 403), Law 5113/2024 Art. 14, JMD 96806/2025, DORA register window,
    myDATA transmission deadline, Del. Reg. 2017/2358 Arts 7–8;
  - multi-market leakage scan is heuristic;
  - screen operations checked by name only;
  - build order not mechanically derivable;
  - the G/W/T check is syntactic.
- **Deviations:**
  - no single writer read all PRDs (about 930,000 words);
  - `digest_CMP.md` was defective at first;
  - three Must counts (3,785 / 3,870 / 3,784);
  - S1 withdrew CR-CLM-04 (F-002) and reworded the XMR-FR-250 acceptance (F-001);
  - S3 §12.4 spot-check URLs still to be appended to §21.

---

## 12. Regulatory, tax and statutory values stated explicitly in this range

| Rule | Value | Status | Where |
|---|---|---|---|
| Greek IPT motor and other non-life | **15%**; fire 20%; life 4% (Law 5177/2025 Art. 43) | Verified 2026-10-07 | E2E-01 step 2; §21 #2 |
| IPT base | "due premiums and rights of every kind" | Verified | F-211 |
| IPT non-refundable on cancellation | ΠΟΛ 1028/2017 (and ΠΟΛ 1032/2018) | Applicability under Law 5177/2025 unverified (F-218) | E2E-03 step 2 |
| Auxiliary Fund levy | 6% ceiling, 70/30 split confirmed; component reading open | OPEN (OQ-010) | F-213 |
| Non-payment notice period | **One month** from notification (Law 2496/1997 Art. 6 §2; K-01, R-60) | Settled; secondary verification | E2E-07 step 3 |
| MTPL reasoned offer | **3 months** from claim receipt (Dir. 2009/103/EC Art. 22) | Source not re-fetched | E2E-02 step 3 |
| MTPL payment | **10 days** from the offer (BoG Act 87/2016 Art. 6); D7: from proven delivery | Decided D7 | F-230 |
| Repair in kind | 20 days (Act 87/2016 Art. 6) | Verified | F-231 |
| MTPL third-party notice | 16 days (P.D. 237/1986 Art. 11a) | UNVERIFIED | F-231, OQ-002 |
| Distance withdrawal refund | Full premium, cost of cover 0.00 (Law 5317/2026 Art. 72); refund within **30 calendar days** (Art. 3ιστ) | Verified | E2E-08; F-235 |
| Withdrawal long stop | 12 months + 14 days | Stated | F-231 |
| Withdrawal deadline cut-off | 23:59:59 Europe/Athens on the last day | Test assertion | E2E-08 |
| DSAR deadline | One month | — | E2E-09 |
| Complaints final reply | 50 days (BoG Act 88/2016, REG-010) | Verified per closure table | §18.2 closed |
| GDPR breach notification | 72 hours | — | F-232 |
| DORA initial notification | Classification + 4 h (capped at awareness + 24 h if classified within 24 h) | Verified (Del. Reg. 2025/301) | F-236 |
| SII deadlines 2026 | 5 weeks quarterly; 14 weeks annual and SFCR | Verified for 2026 | F-255 |
| A.1004 annual property return | 10 January | Verified (P2 scope) | §21 #3 |
| Cyprus stamp duty | Repealed from 2026-01-01 | Settled (R-85) | E2E-01 CY |
| IRRD transposition | 29 Jan 2027 | Verified | F-254 |

**Gaps** (referenced but not specified): levy split and rates, myDATA document types and codes and transmission
deadline, Information Centre format and deadline, FS limits, renewal notice lead time, retention durations, MTPL
statutory interest, 16 UNVERIFIED clock values.

---

## 13. Greek-market specifics in this range

AFM golden vectors (E2E-01); myDATA MARK, the bind-to-MARK p95 < 60 s target and the TRANSACTION trigger; the
Information Centre bureau facts COVER_START, COVER_END and void, with interim MANUAL_FILE; Friendly Settlement via EAEE;
the Auxiliary Fund levy; IPT 15%; gov.gr Wallet pre-fill (CHN-112…116); gov.gr co-signing (D-252); EUR; Europe/Athens
time zone with DST; Greek legal terminology fixes (F-127); the R-101 ΕΛ | EN switch; registered post as the statutory
medium for non-payment notices at go-live (D-263); SEPA AM04 return; RF payment codes (D-287); the Hellenic Motor
Insurers' Bureau and the Auxiliary Fund for Green Card (OQ-019).

---

## 14. Conflicts and ambiguities

### (a) With infra / stack (ARCHITECTURE-DECISIONS.md overrides older PRD wording)

1. **D10 (contract §3.10.10) vs infra.** D10 says "Open-source, self-operated event stream, workflow engine and lakehouse
   table format". Infra explicitly does **not** use message brokers, workflow servers or data lakehouses. It uses a
   transactional outbox with in-process handlers, Hangfire, and PostgreSQL reporting schemas. Both are dated
   2026-10-07, and infra says it overrides PRD wording. **Infra must win.** D10 has to be read as: event stream = the
   outbox; workflow engine = explicit state machines plus Hangfire; lakehouse = PostgreSQL marts. This needs a
   recorded reconciliation.
2. **DAT capability groups in the cut assume a lakehouse:**
   - "5.4 Layered lakehouse" (REQ-DAT-067–079, 13 Musts);
   - "5.20 Personal-data governance in the lakehouse" (262–279, 17 Musts);
   - "5.14 Feature store" (190–196);
   - "5.2 Change-data-capture backstop" (050–057, log-based CDC of every module's database);
   - `LakehouseErasureCompleted`;
   - crypto-shredding for the lakehouse (D-259).

   CDC across schemas also conflicts with the infra rule "never reads another module's tables". D-103 proposes a named
   exception that infra does not record. About 50 DAT Musts need reinterpretation onto PostgreSQL mart schemas.
3. **PLT "5.10 Workflow engine and decision-table runtime"** (REQ-PLT-165–177, W1) and "5.8 Event infrastructure"
   (136–148, 152). These cover schema registry, topic-qualified names, "v2 topic migration" and partition keys (R-100,
   F-117 hot partitions). Under infra they become outbox ordering keys and plain handlers, with no BPM engine. The
   DMN import/export dependency (REQ-UW-048 → REQ-PLT-178) is BPM-flavoured.
4. **Identity.** The PLT stance is "Custom identity service". PLT 5.2/5.3 cover the staff and external realms, and
   D-252 covers passkey-only, a 90-day OTP exception and OIDC/SAML federation. Infra: "Microsoft Entra ID … Entra
   External ID … we never build our own identity system". About 34 PLT identity Musts must be mapped onto Entra
   configuration.
5. **Hosting.** XMR-AS-09 assumes "hosting with two EU regions", and D-251 lists hosting with two EU regions. Infra:
   Azure, EU region (singular) on Container Apps. BC/DR Musts (PLT 5.18 deployment stamps) need reconciling.
6. **Repository and CI.** OQ-035 (repository and CI "inside the EU stamp") and D-253 (repository per stamp) vs infra
   GitHub Actions with OIDC to Azure. Unresolved data-residency question for CI.
7. **Documents.** D-279 recommends an "in-house engine, PDF/A-3 with embedded payload and versioned Greek font sets".
   Infra uses HTML → PDF/A via **Gotenberg** (headless Chromium). Compatible if Gotenberg's PDF/A-3 output plus an
   embedded attachment meets the need. Needs a spike.
8. **Capacity vs the in-process outbox.** D8 requires ≥ 2,000 events/s sustained and a 180 m/yr design, which an
   in-process outbox dispatcher on PostgreSQL must meet. Infra says "Azure Service Bus can be attached later". Flag it
   as a performance risk to test early.
9. **Rule expression language.** D10 calls for a CEL-compatible, in-house, typed deterministic language shared by PFC,
   UW and PLT. Infra does not mention it; it fits "few dependencies" but is a significant build item. The UW 150 ms
   nested-context spike is required "before G0".
10. **PFC "Git as source of record"** with a compiled artefact and YAML schema v1 is not reflected in infra. It needs a
    design: Git-backed product authoring inside a .NET monolith.
11. Vendor-neutral items not in infra (QTSP, print vendors, acquirer, sanctions lists) need procurement, not a stack
    change.

### (b) With the system contract and other PRDs

- **XMR-FR-250 vs D5.** The E2E suite as written asserts "one correlation id from the originating command to the last
  journal line"; D5 and F-001 say journey lineage uses business keys and the trace id is technical only. Phase 3 tests
  must assert lineage by business keys.
- **D4 completeness field names** (`set_id`, `set_size`, `index`) differ from F-114's proposal
  (`transaction_delta_count`, `aggregate_member_index`, etc.). Use the contract.
- **FS source name:** `FS_CLEARING` (D1, contract) vs `CLM_FS_SETTLEMENT` (F-214, D-155, CR-S3-11/12). Use
  `FS_CLEARING`.
- **E2E-02 step 7** (deadline = acceptance + 10 days) contradicts D7 (from proven offer delivery). Use D7.
- **IPT liability point:** REQ-MKT-328 Greece default WRITTEN vs D2 default DUE.
- **The cut counts (3,710) exclude** the 9 promotions, the D1 BIL/FIN additions, R-101 requirements (REQ-PLT-357,
  REQ-CHN-321, REQ-MKT-333…338), REQ-MKT-331 and -343, the S4 promotions of MKT-089, -106 and -316, and the S3 raises
  of CMP-138, -202 and -115. They also include about 12 bancassurance Musts now out. **Recount after the freeze-time
  edits.**
- **The freeze recommendation text** ("not ready to freeze today") and **the freeze record** ("frozen") sit together;
  the record supersedes.

### (c) Internal contradictions

- Three Must counts: 3,785 / 3,870 / 3,784.
- `_work/stats.json` gives 551 ENHANCEMENT vs 552.
- REQ-CMP-154 says 13 modules against a 15-row map.
- UW pattern codes do not match WRK.
- REQ-PTY-277 is Must/P3.
- The GS-02 six-month term conflicts with annual-only.
- PFC/RAT/POL/RI/FIN/MIG `R-nn` risk IDs collide with contract rulings.

### (d) Cannot be built without a decision or answer

- Levy split and amounts (E2E-01/03 amounts).
- IPT on a withdrawal void (E2E-08 step 6, insurer tax side).
- Migration reversal operations (E2E-05 step 10; D-260 G1).
- Retention durations (E2E-09; D-259 G1).
- myDATA document types and codes.
- Information Centre transport.
- FS agreement spec and limits.
- Renewal lead-time value.
- A-01 motor RI confirmation (if a QS exists, the proportional engine enters P1).
- Board confirmation of scenario B before W5.
- Test-lead appointment.

---

## 15. Build notes

- **Hardest parts:**
  - the money spine (POL NET deltas with completeness sets → BIL sub-ledger → CMP fiscal/MARK → FIN posting with daily
    reconciliation);
  - reverse-and-reapply across closed periods (E2E-11);
  - statutory clocks with time-shift testing;
  - the in-house CEL-compatible rules runtime under a 150 ms budget;
  - the in-house XBRL renderer;
  - migration coexistence routing and the unnamed rollback.
- **Must exist first (W1):**
  - PLT time service (time-shiftable, REQ-PLT-332);
  - audit;
  - outbox and events;
  - authority and maker-checker;
  - numbering;
  - MKT configuration, hash and SPI catalogue with the Greece pack and the Cyprus stub in CI;
  - TaxCalculator (with `treatment`, D2).
- **E2E harness (Phase 3):**
  - implement XMR-FR-250 on the MKT golden harness with Playwright and Testcontainers;
  - run every scenario under GR and CY;
  - use guarded or structural assertions for levy and IPT amounts until the D2 opinion;
  - mark E2E-05 step 10 pending D-260;
  - write E2E-06 against the D1 `FS_CLEARING` design (it does not pass as originally written);
  - assert lineage by business keys (D5);
  - add the `AiSystemStatusChanged` assertion to E2E-10.
- **Slicing:** follow W1–W9 with interface-first contracts. Each wave's exit criterion is its Musts' G/W/T plus the
  E2E scenarios ending in that wave:
  - W5: E2E-01 money parts;
  - W6: E2E-03, -04, -07, -08, -12;
  - W7: E2E-02, -06;
  - W8: E2E-09, -10, -11;
  - W9: E2E-05.

  The migration import APIs must be callable from W5 for mock 1.
