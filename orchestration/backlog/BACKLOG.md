# Work-package backlog — Motor MVP cut

Generated 2026-10-07 from PRD-18 Annex B.1 (requirement level), the module PRDs' §5 capability groups, PLAN.md §3 and DECISIONS.md. Machine-readable source of truth: `backlog.json` (same folder). Status of every WP starts as `not started` (deferred WPs: `deferred`).

## Totals check

| Step | Musts |
|---|---|
| PRD-18 Annex B.1 Must P1 rows (parsed from source) | 3710 |
| + D6 promoted Shoulds (POL-108, PFC-112, PFC-094, PFC-145, PFC-156, PFC-157, PTY-096, PLT-065, WRK-091) | +9 |
| + D-CON-13 promotion (REQ-RI-193, manual RI settlement) | +1 |
| − D-200(b) bancassurance demotions (PLT-057, CHN-206, CHN-207, CHN-209, CHN-214, PTY-253, PTY-254, PTY-255, PTY-257) | −9 |
| **= Backlog Must total (each in exactly one WP)** | **3711** |
| of which in deferred WPs (CHN-152, CHN-314 under D-USR-01; WRK-267 under D-USR-03) | 3 |
| **Active build scope** | **3708** |

Notes on the adjustments:
- Bancassurance: the Annex B.1 Musts in PTY §5.19 are REQ-PTY-253/254/255/257 and in CHN §5.14 are REQ-CHN-206/207/209/214; with REQ-PLT-057 that is 9 removals (PRD-18 estimated "≈ 12"; REQ-PTY-258, REQ-CHN-208/210 were already non-Must in B.1). REQ-CHN-042 is kept as a Must (only its bank part is out of scope; the front-end configuration service is needed for all channels).
- REQ-CHN-298 (responsive/accessibility) stays active; its native-app dynamic-type clause and the push channel in REQ-CHN-146/148 are deviations recorded under D-USR-01, not separate deferrals.
- Promoted requirements carry the wave of their capability group: POL-108 W4, PFC-* W2, PTY-096 W2, PLT-065 W1, WRK-091 W2, RI-193 W7. Their points are estimated with the PRD-18 §16.4 size rule (no Annex B.1 size).
- Contract anchors (REQ-<MOD>-001…029) sit in the WP that implements their family; the 24 migration import anchors are W9 WPs per owner module that must be callable from W5 (wave rule 3) and depend only on their owner module.
- `dependsOn` lists direct prerequisites only: Must→Must links from Annex B.1 and the PRD "Requires" columns (≥ 2 links or an anchor link), plus the PRD-18 §16.7 anchor chains; at most 8 per WP. Links to later waves are left out (met by Phase 1 contract-test sandbox doubles). Same-wave links are ordered providers-first, so the graph is acyclic; the "Order" column gives the suggested running order inside a wave.

### Musts by PRD

| PRD | Module | Title | Must P1 (backlog) | WPs |
|---|---|---|---|---|
| PRD-01 | PTY | Party, customer and distribution | 215 | 7 |
| PRD-02 | PFC | Product factory and configuration | 205 | 6 |
| PRD-03 | RAT | Rating and pricing engine | 214 | 6 |
| PRD-04 | UW | Underwriting, referral and workbench | 183 | 6 |
| PRD-05 | POL | Policy administration and transactions | 294 | 9 |
| PRD-06 | BIL | Billing and collections | 294 | 10 |
| PRD-07 | CLM | Claims management | 193 | 6 |
| PRD-08 | RI | Ceded reinsurance | 92 | 4 |
| PRD-09 | FIN | Finance sub-ledger and accounting | 242 | 7 |
| PRD-10 | DOC | Documents and communications | 250 | 6 |
| PRD-11 | CMP | Regulatory compliance and reporting | 183 | 7 |
| PRD-12 | CHN | Channels, portals and partner APIs | 208 | 8 |
| PRD-13 | WRK | Work management | 232 | 6 |
| PRD-14 | PLT | Platform, identity, audit and operations | 283 | 9 |
| PRD-15 | DAT | Data, analytics and regulatory marts | 212 | 7 |
| PRD-16 | MIG | Data migration and coexistence | 167 | 4 |
| PRD-17 | MKT | Multi-market configuration and country packs | 244 | 8 |
| FOUNDATION | F | Phase 1 foundations | — | 6 |
| PRD-18 | XMR | Programme baseline / E2E suite | — | 12 |
| **Total** | | | **3711** | **134** |

### WPs and Musts by wave

| Wave | WPs (feature) | of which deferred | Foundation WPs | E2E WPs | Musts | Points |
|---|---|---|---|---|---|---|
| P1 | 0 | 0 | 6 | 0 | 0 | 0 |
| W1 | 13 | 0 | 0 | 0 | 481 | 677 |
| W2 | 20 | 1 | 0 | 0 | 813 | 1198 |
| W3 | 9 | 0 | 0 | 0 | 361 | 519 |
| W4 | 8 | 0 | 0 | 0 | 341 | 552 |
| W5 | 11 | 0 | 0 | 2 | 400 | 590 |
| W6 | 13 | 1 | 0 | 4 | 306 | 481 |
| W7 | 10 | 1 | 0 | 3 | 286 | 489 |
| W8 | 17 | 0 | 0 | 2 | 532 | 741 |
| W9 | 15 | 0 | 0 | 1 | 191 | 365 |
| **Total** | 116 | 3 | 6 | 12 | 3711 | 5612 |

Feature WP size (excluding deferred and W9 import-anchor WPs): min 1, max 57, median 37 Musts. Small WPs exist only where a whole module+wave is small (e.g. PFC W6 = 1, MKT W8 = 3, BIL W7 = 9, UW W6 = 10, RI W8 = 13).

## Work packages per PRD

### FOUNDATION — Phase 1 foundations (orchestrator-owned)

| ID | Wave | Title | Musts | Status | Depends on |
|---|---|---|---|---|---|
| F-1a | P1 | Repo scaffold, CI and test harness | 0 | not started | — |
| F-1b | P1 | Shared kernel and platform primitives | 0 | not started | F-1a |
| F-1c | P1 | Contracts, OpenAPI skeletons, event schemas and sandbox doubles | 0 | not started | F-1b |
| F-1d | P1 | Design system, app shell and i18n | 0 | not started | F-1a |
| F-1e | P1 | Greek cross-cutting | 0 | not started | F-1b |
| F-1f | P1 | Spikes: Gotenberg PDF/A-3a, outbox throughput, CEL-subset evaluator | 0 | not started | F-1b |

### PRD-01 PTY — Party, customer and distribution (215 Musts, 7 WPs)

| ID | Wave | Order | Title | Musts | Pts | Status | Depends on |
|---|---|---|---|---|---|---|---|
| W2-PTY-01 | W2 | 1 | Party master, identifiers, names/search and addresses | 53 | 88 | not started | F-1e, W1-MKT-01, W1-MKT-02, W1-MKT-04, W1-PLT-01, W1-PLT-03, W1-PLT-04 |
| W2-PTY-02 | W2 | 14 | Roles, accounts/households, dedupe/merge and party 360 | 41 | 76 | not started | W1-MKT-01, W1-PLT-02, W1-PLT-03, W1-PLT-04, W2-DOC-01, W2-PTY-01, W2-WRK-01, W2-WRK-04 |
| W2-PTY-03 | W2 | 17 | Consent, vulnerability and sanctions screening | 32 | 63 | not started | W1-MKT-01, W1-MKT-02, W1-MKT-04, W1-PLT-01, W1-PLT-06, W2-DOC-04, W2-PTY-01, W2-WRK-01 |
| W2-PTY-04 | W2 | 5 | Intermediaries, producer codes, import/API conventions | 36 | 63 | not started | W1-MKT-01, W1-MKT-02, W1-MKT-04, W1-PLT-01, W1-PLT-02, W1-PLT-03, W1-PLT-04, W1-PLT-06 |
| W6-PTY-01 | W6 | 8 | Life events, account merge/policy move and DSR support | 22 | 37 | not started | W1-PLT-01, W1-PLT-02, W1-PLT-03, W1-PLT-05, W2-PTY-02, W2-WRK-01, W4-POL-01, W5-BIL-01 |
| W6-PTY-02 | W6 | 5 | Producer of record, commission agreements and delegated agency admin | 30 | 42 | not started | W1-MKT-01, W1-PLT-01, W1-PLT-02, W1-PLT-03, W1-PLT-04, W2-PFC-03, W2-WRK-04, W4-POL-01 |
| W9-PTY-01 | W9 | 1 | Party import API | 1 | 5 | not started | W2-PTY-01, W2-PTY-04 |

### PRD-02 PFC — Product factory and configuration (205 Musts, 6 WPs)

| ID | Wave | Order | Title | Musts | Pts | Status | Depends on |
|---|---|---|---|---|---|---|---|
| W2-PFC-01 | W2 | 15 | Product structure, metadata, elements and validation | 34 | 45 | not started | W1-MKT-01, W1-MKT-02, W1-MKT-04, W1-PLT-04, W2-PTY-01, W2-PTY-02 |
| W2-PFC-02 | W2 | 16 | Coverages, terms, offerings and question sets | 38 | 53 | not started | W1-MKT-01, W1-MKT-04, W2-DOC-01, W2-WRK-01 |
| W2-PFC-03 | W2 | 18 | Charge types, references, regulatory mapping, POG/IPID | 51 | 81 | not started | W1-MKT-01, W1-MKT-04, W1-MKT-05, W1-PLT-03, W1-PLT-05, W2-DOC-02, W2-WRK-01, W2-WRK-05 |
| W2-PFC-04 | W2 | 6 | Versioning, resolution and runtime services | 30 | 45 | not started | W1-MKT-01, W1-PLT-01, W1-PLT-03, W1-PLT-06, W2-CMP-01 |
| W2-PFC-05 | W2 | 10 | Authoring/build, review/approval/release, test assets | 51 | 74 | not started | F-1f, W1-MKT-01, W1-MKT-05, W1-PLT-01, W1-PLT-02, W1-PLT-03, W1-PLT-04, W1-PLT-05, W2-DOC-02 |
| W6-PFC-01 | W6 | 1 | Renewal version conversion rules | 1 | 5 | not started | W2-PFC-04, W2-PFC-05 |

### PRD-03 RAT — Rating and pricing engine (214 Musts, 6 WPs)

| ID | Wave | Order | Title | Musts | Pts | Status | Depends on |
|---|---|---|---|---|---|---|---|
| W3-RAT-01 | W3 | 2 | Rating contract, runtime and artefact binding | 46 | 67 | not started | W1-MKT-01, W1-MKT-02, W1-MKT-04, W1-PLT-03, W1-PLT-06, W2-PFC-03, W2-PFC-04, W2-PFC-05 |
| W3-RAT-02 | W3 | 4 | Algorithm framework and explainability | 48 | 62 | not started | W1-MKT-04, W1-PLT-01, W1-PLT-03, W1-PLT-05, W2-DOC-01, W2-PFC-04, W2-WRK-01, W3-RAT-01 |
| W3-RAT-03 | W3 | 6 | Taxes/charges, discounts/loadings, lawful pricing and proration | 53 | 82 | not started | W1-MKT-02, W1-MKT-03, W1-MKT-05, W1-PLT-02, W1-PLT-03, W2-PFC-03, W3-UW-01, W3-UW-02 |
| W3-RAT-04 | W3 | 8 | Table import, testing assets, approval and rollback | 41 | 51 | not started | W1-MKT-05, W1-PLT-02, W1-PLT-03, W1-PLT-04, W1-PLT-05, W2-DOC-04, W2-WRK-01, W2-WRK-02 |
| W8-RAT-01 | W8 | 10 | CompareVersions, ShadowRun and monitoring supply | 20 | 26 | not started | W1-PLT-06, W2-PFC-05, W3-RAT-01, W4-POL-01, W8-DAT-01, W8-DAT-02, W8-DAT-03 |
| W9-RAT-01 | W9 | 4 | Rating migration and coexistence | 6 | 7 | not started | W3-RAT-01, W3-RAT-04 |

### PRD-04 UW — Underwriting, referral and workbench (183 Musts, 6 WPs)

| ID | Wave | Order | Title | Musts | Pts | Status | Depends on |
|---|---|---|---|---|---|---|---|
| W3-UW-01 | W3 | 1 | Rule authoring and evaluation runtime | 38 | 56 | not started | F-1f, W1-PLT-02, W1-PLT-03, W1-PLT-04, W2-PFC-02, W2-PFC-04, W2-PTY-01, W2-PTY-03 |
| W3-UW-02 | W3 | 3 | Issue lifecycle, approvals and authority at decision | 38 | 58 | not started | W1-PLT-01, W1-PLT-02, W2-PTY-03, W2-WRK-03, W3-RAT-01, W3-UW-01 |
| W3-UW-03 | W3 | 5 | Referral routing, SLA and workbench | 32 | 48 | not started | W1-PLT-03, W2-PTY-02, W2-PTY-04, W2-WRK-01, W2-WRK-02, W2-WRK-03, W2-WRK-04, W2-WRK-05 |
| W3-UW-04 | W3 | 7 | External data, inspections, declines and contingencies | 38 | 61 | not started | W1-MKT-02, W1-PLT-03, W1-PLT-04, W2-DOC-01, W2-PTY-03, W2-WRK-01, W2-WRK-04, W2-WRK-05 |
| W3-UW-05 | W3 | 9 | Policy holds and operations/data protection | 27 | 34 | not started | W1-MKT-01, W1-MKT-02, W1-MKT-04, W1-PLT-03, W1-PLT-05, W1-PLT-06, W3-RAT-01 |
| W6-UW-01 | W6 | 7 | Renewal and in-force underwriting | 10 | 12 | not started | W1-MKT-06, W2-CMP-01, W2-WRK-01, W6-POL-02, W6-POL-03 |

### PRD-05 POL — Policy administration and transactions (294 Musts, 9 WPs)

| ID | Wave | Order | Title | Musts | Pts | Status | Depends on |
|---|---|---|---|---|---|---|---|
| W4-POL-01 | W4 | 3 | Policy, term, identifiers, jobs and concurrency | 41 | 61 | not started | W1-MKT-01, W1-MKT-04, W1-PLT-03, W1-PLT-04, W2-DOC-01, W2-PFC-03, W2-PTY-02, W4-POL-03 |
| W4-POL-02 | W4 | 7 | Transaction stack, segments and reverse-and-reapply | 39 | 63 | not started | W1-MKT-01, W1-PLT-02, W1-PLT-03, W2-WRK-01, W3-RAT-01, W3-UW-03, W4-POL-01 |
| W4-POL-03 | W4 | 2 | Charges, proration, billing contract and status model | 30 | 52 | not started | W1-MKT-01, W1-PLT-02, W1-PLT-03, W2-PFC-03, W2-PFC-04, W2-WRK-03, W3-RAT-02, W3-RAT-03 |
| W4-POL-04 | W4 | 5 | Quote lifecycle, risk data and parties | 44 | 66 | not started | W1-MKT-01, W1-PLT-04, W2-PFC-02, W2-PFC-04, W2-PTY-01, W2-PTY-02, W3-RAT-01, W3-RAT-03 |
| W4-POL-05 | W4 | 6 | Bind and issue, policy file/search, import and integrity | 53 | 78 | not started | W1-PLT-04, W2-PTY-01, W2-PTY-03, W2-PTY-04, W2-WRK-05, W3-UW-02, W3-UW-05, W4-DOC-01 |
| W6-POL-01 | W6 | 9 | Policy change, reinstatement/rewrite and prior-term changes | 32 | 37 | not started | W1-PLT-02, W2-DOC-01, W2-PFC-04, W2-PTY-03, W2-WRK-04, W3-UW-02, W4-CHN-01, W4-POL-03 |
| W6-POL-02 | W6 | 3 | Cancellation, void and statutory lifecycle | 33 | 53 | not started | W1-MKT-02, W1-MKT-06, W2-CMP-01, W2-DOC-04, W3-RAT-03, W5-BIL-04, W5-CMP-02, W6-CHN-01 |
| W6-POL-03 | W6 | 4 | Renewal | 21 | 37 | not started | W1-PLT-02, W2-CMP-01, W2-DOC-01, W2-PFC-04, W3-RAT-01, W4-DOC-01, W4-POL-03, W6-PFC-01 |
| W9-POL-01 | W9 | 5 | Policy import API | 1 | 5 | not started | W4-POL-02, W4-POL-05 |

### PRD-06 BIL — Billing and collections (294 Musts, 10 WPs)

| ID | Wave | Order | Title | Musts | Pts | Status | Depends on |
|---|---|---|---|---|---|---|---|
| W5-BIL-01 | W5 | 6 | Billing accounts, payment plans, bind gate and preview | 34 | 57 | not started | W1-MKT-01, W1-PLT-02, W1-PLT-04, W2-PTY-02, W2-PTY-03, W2-WRK-01, W4-POL-01, W4-POL-05 |
| W5-BIL-02 | W5 | 1 | Charge intake, invoices/fiscal triggers and written-basis levies | 43 | 64 | not started | W1-PLT-04, W2-CMP-01, W2-DOC-01, W2-DOC-04, W2-PFC-03, W2-WRK-01, W4-POL-02, W4-POL-03 |
| W5-BIL-03 | W5 | 10 | Payment methods, mandates, receipts and allocation | 37 | 54 | not started | W1-MKT-02, W1-PLT-02, W1-PLT-03, W1-PLT-04, W2-DOC-04, W2-PTY-01, W2-WRK-01, W4-CHN-01 |
| W5-BIL-04 | W5 | 11 | Payment reversals and delinquency/dunning | 33 | 51 | not started | W1-MKT-02, W1-MKT-06, W1-PLT-02, W1-PLT-04, W2-CMP-01, W2-DOC-01, W2-DOC-04, W2-WRK-01 |
| W5-BIL-05 | W5 | 5 | Disbursement service, bank reconciliation and workbenches | 46 | 64 | not started | W1-MKT-01, W1-MKT-02, W1-PLT-02, W1-PLT-03, W1-PLT-04, W1-PLT-05, W2-PTY-03, W2-WRK-01 |
| W5-BIL-06 | W5 | 2 | Billing sub-ledger and FIN hand-off | 31 | 37 | not started | W1-MKT-01, W1-PLT-02, W1-PLT-03, W1-PLT-05, W1-PLT-06, W2-WRK-01, W5-BIL-02 |
| W6-BIL-01 | W6 | 11 | Refunds, write-offs and moving money/policies | 29 | 42 | not started | W1-PLT-02, W1-PLT-03, W2-PTY-03, W2-WRK-03, W4-CHN-01, W4-POL-04, W5-FIN-01, W6-POL-02 |
| W6-BIL-02 | W6 | 12 | Agency bill and commission | 31 | 50 | not started | W2-DOC-01, W2-PTY-04, W2-WRK-01, W2-WRK-05, W5-CMP-01, W5-FIN-01, W6-CHN-02, W6-PTY-02 |
| W7-BIL-01 | W7 | 3 | Payee accounts and claim-related money services | 9 | 18 | not started | W1-MKT-02, W2-PTY-01, W2-PTY-03, W5-BIL-05 |
| W9-BIL-01 | W9 | 2 | Billing import API | 1 | 5 | not started | W5-BIL-01, W5-BIL-03 |

### PRD-07 CLM — Claims management (193 Musts, 6 WPs)

| ID | Wave | Order | Title | Musts | Pts | Status | Depends on |
|---|---|---|---|---|---|---|---|
| W7-CLM-01 | W7 | 1 | FNOL, coverage verification and claim structure | 47 | 81 | not started | W1-PLT-02, W1-PLT-03, W2-DOC-01, W2-PFC-02, W2-PTY-01, W2-PTY-03, W2-WRK-01, W4-POL-02 |
| W7-CLM-02 | W7 | 2 | Triage, reserves and transaction sets/authority | 31 | 51 | not started | W1-MKT-04, W1-PLT-02, W1-PLT-03, W1-PLT-04, W2-WRK-01, W2-WRK-03, W5-FIN-01, W7-CLM-01 |
| W7-CLM-03 | W7 | 10 | Payments/payees, recoveries and vendors | 39 | 60 | not started | W1-PLT-01, W1-PLT-02, W2-CMP-01, W2-PTY-03, W5-BIL-05, W5-CMP-01, W7-BIL-01, W7-CLM-02 |
| W7-CLM-04 | W7 | 6 | Greek motor ecosystem, diary and documents | 32 | 63 | not started | W1-MKT-01, W1-MKT-06, W2-CMP-01, W2-DOC-01, W2-DOC-04, W2-PTY-03, W2-WRK-01, W2-WRK-04 |
| W7-CLM-05 | W7 | 5 | Fraud/SIU, catastrophe, litigation, tracking/search and streamlining | 43 | 78 | not started | W1-MKT-04, W1-PLT-02, W1-PLT-03, W1-PLT-04, W2-PTY-02, W2-WRK-01, W2-WRK-05, W7-RI-02 |
| W9-CLM-01 | W9 | 11 | Claims import API | 1 | 5 | not started | W7-CLM-01, W7-CLM-02 |

### PRD-08 RI — Ceded reinsurance (92 Musts, 4 WPs)

| ID | Wave | Order | Title | Musts | Pts | Status | Depends on |
|---|---|---|---|---|---|---|---|
| W7-RI-01 | W7 | 7 | Programme/contract registry, participations and operations | 39 | 63 | not started | W1-PLT-02, W1-PLT-03, W1-PLT-04, W1-PLT-06, W2-DOC-04, W2-PFC-05, W2-PTY-02, W4-POL-03 |
| W7-RI-02 | W7 | 4 | XoL recoveries, technical accounting and settlement | 39 | 63 | not started | W1-MKT-04, W1-PLT-03, W2-DOC-01, W2-PTY-03, W2-WRK-01, W5-BIL-05, W5-FIN-02, W7-CLM-02 |
| W8-RI-01 | W8 | 12 | IFRS 17/SII data and RI finance close | 13 | 15 | not started | W1-PLT-03, W2-PFC-03, W5-FIN-01, W5-FIN-02, W8-DAT-01, W8-DAT-03, W8-DAT-04, W8-FIN-01 |
| W9-RI-01 | W9 | 3 | Reinsurance import API | 1 | 5 | not started | W7-RI-01, W7-RI-02 |

### PRD-09 FIN — Finance sub-ledger and accounting (242 Musts, 7 WPs)

| ID | Wave | Order | Title | Musts | Pts | Status | Depends on |
|---|---|---|---|---|---|---|---|
| W5-FIN-01 | W5 | 3 | Business-event intake and posting rules | 35 | 52 | not started | W1-MKT-03, W1-MKT-05, W1-PLT-02, W1-PLT-03, W1-PLT-04, W2-PFC-03, W4-POL-03, W5-BIL-06 |
| W5-FIN-02 | W5 | 7 | Journals, chart of accounts/dimensions and manual journals | 43 | 65 | not started | W1-MKT-01, W1-MKT-03, W1-MKT-04, W1-PLT-02, W1-PLT-03, W1-PLT-04, W1-PLT-05, W1-PLT-06 |
| W5-FIN-03 | W5 | 9 | Claims/RI entries, commission, tax/levies and actuarial intake | 43 | 67 | not started | W1-MKT-06, W1-PLT-02, W2-CMP-01, W2-PFC-03, W5-BIL-02, W5-BIL-05, W5-CMP-01, W5-FIN-01 |
| W8-FIN-01 | W8 | 6 | Earning, deferrals and IFRS 17 | 42 | 71 | not started | W1-MKT-04, W1-PLT-02, W2-PFC-03, W4-POL-02, W4-POL-03, W5-BIL-01, W6-POL-03, W7-RI-01 |
| W8-FIN-02 | W8 | 16 | Solvency II views, FX and GL extract | 31 | 42 | not started | W1-MKT-04, W1-MKT-05, W1-PLT-03, W1-PLT-05, W7-RI-02, W8-DAT-02, W8-DAT-03, W8-PLT-01 |
| W8-FIN-03 | W8 | 9 | Period close, reconciliation and finance workbench | 47 | 58 | not started | W1-PLT-01, W1-PLT-02, W1-PLT-04, W1-PLT-05, W2-PTY-01, W2-WRK-01, W5-BIL-06, W8-PLT-01 |
| W9-FIN-01 | W9 | 8 | Opening-balance import API | 1 | 5 | not started | W5-FIN-02 |

### PRD-10 DOC — Documents and communications (250 Musts, 6 WPs)

| ID | Wave | Order | Title | Musts | Pts | Status | Depends on |
|---|---|---|---|---|---|---|---|
| W2-DOC-01 | W2 | 11 | Document-type catalogue, template/clause authoring and binding matrix | 47 | 70 | not started | W1-MKT-01, W1-MKT-02, W1-PLT-02, W1-PLT-03, W1-PLT-04, W2-PFC-04, W2-PFC-05, W2-WRK-01 |
| W2-DOC-02 | W2 | 7 | Governance/translation, form patterns, accessibility and preview | 41 | 51 | not started | W1-MKT-02, W1-PLT-02, W1-PLT-03, W1-PLT-04, W1-PLT-06, W2-PFC-04 |
| W2-DOC-03 | W2 | 19 | Payload dictionary, Greek typography and rendering engine | 42 | 51 | not started | F-1f, W1-MKT-02, W1-MKT-04, W1-PLT-03, W1-PLT-04, W1-PLT-06, W2-PTY-01, W2-PTY-04, W2-WRK-01 |
| W2-DOC-04 | W2 | 8 | Archive, delivery orchestration and operations | 57 | 85 | not started | W1-MKT-01, W1-PLT-01, W1-PLT-02, W1-PLT-03, W1-PLT-05, W1-PLT-06, W2-PTY-01, W2-WRK-01 |
| W4-DOC-01 | W4 | 1 | Fiscal coupling, pre-contractual pack, e-signature and Documents tab | 39 | 66 | not started | W1-PLT-01, W1-PLT-03, W2-DOC-04, W2-PFC-05, W2-PTY-01, W2-PTY-03, W2-PTY-04, W2-WRK-04 |
| W6-DOC-01 | W6 | 10 | Batch rendering, reprint/as-of and evidence packs | 24 | 28 | not started | W1-MKT-02, W1-PLT-04, W2-CMP-01, W2-WRK-01, W4-CHN-01, W4-POL-01 |

### PRD-11 CMP — Regulatory compliance and reporting (183 Musts, 7 WPs)

| ID | Wave | Order | Title | Musts | Pts | Status | Depends on |
|---|---|---|---|---|---|---|---|
| W2-CMP-01 | W2 | 3 | Statutory clock register and engine | 28 | 37 | not started | W1-MKT-06, W1-PLT-02, W1-PLT-03, W1-PLT-04, W2-WRK-05 |
| W5-CMP-01 | W5 | 4 | Fiscal-document channel (myDATA) and e-invoicing path | 31 | 46 | not started | W1-MKT-01, W1-MKT-02, W1-PLT-02, W1-PLT-03, W1-PLT-04, W2-PTY-01, W2-WRK-01, W5-BIL-02 |
| W5-CMP-02 | W5 | 8 | Information Centre / motor bureau adapter | 24 | 33 | not started | W1-MKT-02, W1-MKT-06, W1-PLT-03, W1-PLT-05, W2-WRK-01, W4-POL-01, W4-POL-02, W4-POL-05 |
| W8-CMP-01 | W8 | 14 | Complaints, periodic returns and submission tracker | 35 | 52 | not started | W1-MKT-04, W1-PLT-01, W1-PLT-02, W1-PLT-03, W2-DOC-04, W2-PTY-01, W2-WRK-01, W8-DAT-03 |
| W8-CMP-02 | W8 | 7 | DSAR orchestration and disclosure log | 27 | 35 | not started | W1-PLT-01, W1-PLT-03, W1-PLT-04, W2-DOC-01, W2-DOC-04, W2-PTY-01, W2-PTY-03, W6-PTY-01 |
| W8-CMP-03 | W8 | 3 | Obligations register, regulatory change, AI register and workbench | 37 | 51 | not started | W1-MKT-01, W1-MKT-03, W1-MKT-05, W1-PLT-02, W1-PLT-03, W1-PLT-05, W2-WRK-01, W8-PLT-01 |
| W9-CMP-01 | W9 | 10 | Compliance migration import and hand-over | 1 | 5 | not started | W2-CMP-01, W5-CMP-01, W5-CMP-02 |

### PRD-12 CHN — Channels, portals and partner APIs (208 Musts, 8 WPs)

| ID | Wave | Order | Title | Musts | Pts | Status | Depends on |
|---|---|---|---|---|---|---|---|
| W4-CHN-01 | W4 | 4 | Channel foundation, permission matrix and partner APIs | 48 | 83 | not started | W1-MKT-01, W1-PLT-01, W1-PLT-02, W1-PLT-03, W1-PLT-06, W2-WRK-03, W4-POL-01, W4-POL-03 |
| W4-CHN-02 | W4 | 8 | Identity journeys, motor quote-and-buy, gov.gr Wallet and accessibility | 47 | 83 | not started | F-1d, W1-PLT-01, W2-PFC-02, W2-PFC-03, W2-PTY-01, W3-RAT-02, W4-DOC-01, W4-POL-04, W4-POL-05 |
| W6-CHN-01 | W6 | 2 | Self-service, payments, documents, messaging and withdrawal | 40 | 75 | not started | W2-CMP-01, W2-DOC-01, W2-PTY-01, W2-PTY-03, W4-DOC-01, W4-POL-01, W5-BIL-01, W5-BIL-03 |
| W6-CHN-02 | W6 | 6 | Broker/agent portal, agency self-admin and channel data | 32 | 62 | not started | W1-MKT-01, W1-PLT-02, W2-PTY-04, W3-RAT-02, W4-DOC-01, W4-POL-01, W4-POL-05, W6-PTY-02 |
| W6-CHN-03 | W6 | 13 | Mobile-app-only channel features (deferred) | 1 | 1 | deferred | W6-CHN-01 |
| W7-CHN-01 | W7 | 8 | Claims in channels | 6 | 11 | not started | W7-BIL-01, W7-CLM-01, W7-CLM-04, W7-CLM-05 |
| W7-CHN-02 | W7 | 9 | Mobile-app-only channel features (deferred) | 1 | 1 | deferred | W7-CHN-01, W7-CLM-01 |
| W8-CHN-01 | W8 | 15 | MCP AI agent facade, customer assistant and tracker consent | 33 | 54 | not started | W1-PLT-01, W1-PLT-03, W3-UW-01, W4-CHN-01, W4-POL-01, W8-CMP-03, W8-DAT-01, W8-PLT-01 |

### PRD-13 WRK — Work management (232 Musts, 6 WPs)

| ID | Wave | Order | Title | Musts | Pts | Status | Depends on |
|---|---|---|---|---|---|---|---|
| W2-WRK-01 | W2 | 4 | Activity patterns, lifecycle and system-generated activities | 49 | 66 | not started | W1-MKT-01, W1-MKT-04, W1-PLT-01, W1-PLT-02, W1-PLT-03, W1-PLT-04, W1-PLT-06, W2-CMP-01 |
| W2-WRK-02 | W2 | 13 | Calendars/SLA, groups/queues, assignment/routing and delegation | 45 | 57 | not started | W1-MKT-01, W1-PLT-01, W1-PLT-02, W1-PLT-03, W1-PLT-04, W1-PLT-05, W2-CMP-01, W2-WRK-03 |
| W2-WRK-03 | W2 | 12 | Team views, authority-aware routing, back-office requests and participants | 37 | 50 | not started | W1-MKT-01, W1-PLT-01, W1-PLT-02, W1-PLT-03, W1-PLT-04, W2-CMP-01, W2-PTY-04 |
| W2-WRK-04 | W2 | 9 | Notes and inbound documents | 46 | 63 | not started | W1-MKT-02, W1-MKT-04, W1-PLT-01, W1-PLT-03, W1-PLT-05, W2-DOC-04, W2-PTY-01, W2-WRK-05 |
| W2-WRK-05 | W2 | 2 | My Desktop/global search, staff notifications, data protection and admin | 54 | 79 | not started | F-1e, F-1d, W1-MKT-01, W1-MKT-04, W1-MKT-05, W1-PLT-01, W1-PLT-02, W1-PLT-03, W1-PLT-05, W2-PTY-01 |
| W2-WRK-06 | W2 | 20 | OCR / text recognition for inbound documents (deferred) | 1 | 1 | deferred | W2-WRK-04 |

### PRD-14 PLT — Platform, identity, audit and operations (283 Musts, 9 WPs)

| ID | Wave | Order | Title | Musts | Pts | Status | Depends on |
|---|---|---|---|---|---|---|---|
| W1-PLT-01 | W1 | 7 | Identity: staff and external realms (on Entra ID) | 36 | 52 | not started | F-1b, F-1c, W1-MKT-04, W1-PLT-02 |
| W1-PLT-02 | W1 | 1 | Authorisation, authority framework and maker-checker | 40 | 56 | not started | F-1b, F-1c |
| W1-PLT-03 | W1 | 2 | Business audit trail, event infrastructure and integration hub | 40 | 57 | not started | F-1f, F-1b, F-1c, W1-PLT-02 |
| W1-PLT-04 | W1 | 10 | Decision-table runtime, configuration runtime and numbering | 36 | 50 | not started | F-1f, F-1b, F-1c, W1-MKT-02, W1-MKT-03, W1-MKT-05, W1-PLT-02, W1-PLT-05, W1-PLT-06 |
| W1-PLT-05 | W1 | 3 | Reference data and data-protection services | 29 | 40 | not started | F-1e, F-1b, F-1c, W1-PLT-02, W1-PLT-03 |
| W1-PLT-06 | W1 | 6 | Observability, environments/release and stamps/BC-DR | 31 | 38 | not started | F-1b, F-1c, W1-MKT-04, W1-MKT-05, W1-PLT-05 |
| W1-PLT-07 | W1 | 13 | Security engineering, architecture fitness and admin shell | 29 | 39 | not started | F-1d, F-1b, F-1c, W1-MKT-04, W1-MKT-05, W1-PLT-01, W1-PLT-02, W1-PLT-03, W1-PLT-06 |
| W8-PLT-01 | W8 | 1 | AI control plane and DORA operations | 36 | 53 | not started | W1-MKT-02, W1-PLT-02, W1-PLT-07, W2-CMP-01, W2-PTY-01, W2-WRK-01 |
| W9-PLT-01 | W9 | 6 | Migration import operations and coexistence | 6 | 11 | not started | W1-PLT-02, W1-PLT-03 |

### PRD-15 DAT — Data, analytics and regulatory marts (212 Musts, 7 WPs)

| ID | Wave | Order | Title | Musts | Pts | Status | Depends on |
|---|---|---|---|---|---|---|---|
| W8-DAT-01 | W8 | 2 | Event ingestion, data contracts, control totals and reference data | 33 | 42 | not started | W1-MKT-01, W1-PLT-03, W1-PLT-05, W2-PFC-03, W4-POL-02, W4-POL-03, W6-PTY-01, W8-PLT-01 |
| W8-DAT-02 | W8 | 4 | Report-mart layers, bitemporal models, DQ/lineage and catalogue | 41 | 56 | not started | W1-PLT-03, W1-PLT-04, W2-WRK-01, W4-POL-01, W5-BIL-06, W5-FIN-02, W8-CMP-03, W8-DAT-01 |
| W8-DAT-03 | W8 | 5 | Regulatory mapping, SII, national and group marts | 35 | 47 | not started | W1-MKT-04, W1-PLT-02, W1-PLT-03, W2-PFC-03, W5-BIL-06, W5-CMP-01, W5-CMP-02, W5-FIN-02 |
| W8-DAT-04 | W8 | 11 | Actuarial supply, pricing/claims analytics and BI | 36 | 48 | not started | W1-PLT-01, W1-PLT-03, W3-UW-01, W5-FIN-03, W7-CLM-01, W7-CLM-05, W8-FIN-01, W8-RAT-01 |
| W8-DAT-05 | W8 | 8 | Feature store, model registry and AI monitoring | 35 | 51 | not started | W1-PLT-02, W2-WRK-01, W2-WRK-04, W8-CMP-03, W8-DAT-02, W8-PLT-01 |
| W8-DAT-06 | W8 | 17 | Personal-data governance, synthetic data and mart operations | 28 | 36 | not started | W1-PLT-01, W1-PLT-02, W1-PLT-03, W1-PLT-05, W1-PLT-06, W2-PTY-03, W8-CMP-02, W8-PLT-01 |
| W9-DAT-01 | W9 | 9 | DAT migration and coexistence support | 4 | 5 | not started | W8-DAT-01 |

### PRD-16 MIG — Data migration and coexistence (167 Musts, 4 WPs)

| ID | Wave | Order | Title | Musts | Pts | Status | Depends on |
|---|---|---|---|---|---|---|---|
| W9-MIG-01 | W9 | 12 | Strategy, discovery, mapping and load pipeline | 46 | 85 | not started | W1-MKT-01, W1-MKT-02, W1-PLT-02, W1-PLT-03, W1-PLT-04, W2-PTY-01, W9-MKT-01, W9-PLT-01 |
| W9-MIG-02 | W9 | 15 | Party, renewal-based policy and billing/finance conversion | 40 | 73 | not started | W1-PLT-04, W2-PTY-04, W9-BIL-01, W9-FIN-01, W9-MIG-01, W9-POL-01, W9-PTY-01, W9-RAT-01 |
| W9-MIG-03 | W9 | 13 | Claims, RI and other data conversion; coexistence | 34 | 62 | not started | W4-POL-01, W5-BIL-03, W7-CLM-04, W9-CLM-01, W9-CMP-01, W9-DAT-01, W9-MIG-01, W9-RI-01 |
| W9-MIG-04 | W9 | 14 | Hand-overs, reconciliation, rehearsals/cut-over, decommissioning, security | 47 | 82 | not started | W1-PLT-02, W1-PLT-03, W1-PLT-04, W1-PLT-05, W2-DOC-01, W8-CMP-02, W8-CMP-03, W9-MIG-01 |

### PRD-17 MKT — Multi-market configuration and country packs (244 Musts, 8 WPs)

| ID | Wave | Order | Title | Musts | Pts | Status | Depends on |
|---|---|---|---|---|---|---|---|
| W1-MKT-01 | W1 | 11 | Configuration model, capability switches and tenancy | 51 | 72 | not started | F-1b, F-1c, W1-PLT-01, W1-PLT-02, W1-PLT-03, W1-PLT-04, W1-PLT-05, W1-PLT-06 |
| W1-MKT-02 | W1 | 8 | SPI framework and catalogue | 42 | 62 | not started | F-1b, F-1c, W1-PLT-03, W1-PLT-05, W1-PLT-06 |
| W1-MKT-03 | W1 | 9 | Pack packaging, lifecycle and reference packs | 43 | 61 | not started | F-1e, F-1b, F-1c, W1-PLT-02, W1-PLT-03, W1-PLT-05 |
| W1-MKT-04 | W1 | 5 | i18n/l10n, currency and rounding, regime code lists | 43 | 64 | not started | F-1e, F-1b, F-1c, W1-PLT-02, W1-PLT-03, W1-PLT-05 |
| W1-MKT-05 | W1 | 4 | Governance, golden suite and operations UI | 31 | 37 | not started | F-1d, F-1b, F-1c, W1-PLT-02 |
| W1-MKT-06 | W1 | 12 | Statutory clock values and integration additions | 30 | 49 | not started | F-1b, F-1c, W1-PLT-05 |
| W8-MKT-01 | W8 | 13 | Cross-border business | 3 | 4 | not started | W4-POL-01, W4-POL-04, W8-DAT-03 |
| W9-MKT-01 | W9 | 7 | Migration-baseline configuration state | 1 | 5 | not started | W1-MKT-01, W1-MKT-03 |

### PRD-18 XMR — Programme E2E suite (XMR-FR-250)

Wave = the wave at whose end the scenario can first pass (PLAN.md §3). E2E-12: PLAN lists only its harness in W1; the scenario itself needs POL bind (W4) and BIL/FIN handling of the rollback window, so it is placed in W5.

| ID | Wave | Title | Musts | Status | Depends on |
|---|---|---|---|---|---|
| E2E-01 | W5 | E2E-01 Quote -> bind -> invoice -> fiscal document -> payment -> ledger | 0 | not started | W2-PFC-03, W2-PFC-04, W2-PTY-03, W2-PTY-04, W3-RAT-01, W3-RAT-02, W3-RAT-03, W3-UW-01, W3-UW-02, W4-CHN-01, W4-DOC-01, W4-POL-01, W4-POL-03, W4-POL-04, W4-POL-05, W5-BIL-01, W5-BIL-02, W5-BIL-03, W5-BIL-06, W5-CMP-01, W5-CMP-02, W5-FIN-01 |
| E2E-02 | W7 | E2E-02 FNOL -> reserve -> payment -> recovery -> RI recovery | 0 | not started | W1-PLT-02, W2-CMP-01, W2-PTY-03, W2-WRK-04, W4-POL-02, W5-BIL-05, W5-CMP-01, W5-FIN-01, W5-FIN-03, W7-BIL-01, W7-CLM-01, W7-CLM-02, W7-CLM-03, W7-CLM-04, W7-RI-02 |
| E2E-03 | W6 | E2E-03 Policyholder cancellation -> refund -> fiscal credit note | 0 | not started | W2-PFC-03, W2-PTY-03, W2-WRK-03, W4-POL-03, W5-BIL-02, W5-CMP-01, W5-CMP-02, W5-FIN-03, W6-BIL-01, W6-POL-02 |
| E2E-04 | W6 | E2E-04 Renewal | 0 | not started | W3-RAT-01, W3-RAT-02, W3-UW-01, W3-UW-03, W4-DOC-01, W4-POL-03, W5-BIL-02, W6-PFC-01, W6-POL-03 |
| E2E-05 | W9 | E2E-05 Migration of an in-force motor policy | 0 | not started | W5-CMP-02, W9-BIL-01, W9-CLM-01, W9-CMP-01, W9-FIN-01, W9-MIG-01, W9-MIG-02, W9-MIG-03, W9-MIG-04, W9-MKT-01, W9-POL-01, W9-PTY-01, W9-RI-01 |
| E2E-06 | W7 | E2E-06 Friendly Settlement | 0 | not started | W1-MKT-06, W2-WRK-04, W5-FIN-01, W5-FIN-03, W7-BIL-01, W7-CLM-01, W7-CLM-04 |
| E2E-07 | W6 | E2E-07 Non-payment cancellation through the waiting-period clock | 0 | not started | W1-MKT-06, W2-CMP-01, W2-DOC-04, W2-PTY-03, W2-WRK-01, W5-BIL-02, W5-BIL-04, W5-CMP-01, W5-CMP-02, W5-FIN-03, W6-POL-02 |
| E2E-08 | W6 | E2E-08 Distance withdrawal | 0 | not started | W1-MKT-06, W2-DOC-01, W5-CMP-01, W5-FIN-03, W6-BIL-01, W6-CHN-01, W6-POL-02 |
| E2E-09 | W8 | E2E-09 DSAR access and erasure across modules | 0 | not started | W1-PLT-05, W2-CMP-01, W2-DOC-04, W4-POL-05, W5-BIL-05, W6-CHN-02, W6-PTY-01, W7-CLM-05, W8-CMP-02, W8-DAT-06, W8-FIN-03 |
| E2E-10 | W8 | E2E-10 AI kill switch across modules | 0 | not started | W8-CHN-01, W8-CMP-03, W8-DAT-05, W8-PLT-01 |
| E2E-11 | W7 | E2E-11 Out-of-sequence change with an open claim and a cession (regression) | 0 | not started | W4-POL-02, W4-POL-03, W5-BIL-02, W5-FIN-01, W7-CLM-01, W7-RI-02 |
| E2E-12 | W5 | E2E-12 Pack rollback after bound business (regression) | 0 | not started | W1-MKT-03, W1-MKT-05, W1-PLT-03, W4-POL-05, W5-BIL-06, W5-FIN-01 |

## Deferred requirements

- REQ-WRK-267 (W2, W2-WRK-06): run text recognition on images and scanned PDFs in Greek… — deferred: D-USR-03
- REQ-CHN-314 (W6, W6-CHN-03): send push notifications only to devices registered by the… — deferred: D-USR-01
- REQ-CHN-152 (W7, W7-CHN-02): autosave FNOL drafts (device and channel draft store)… — deferred: D-USR-01

## Requirements that could not be placed

None: every requirement of the adjusted Motor MVP cut maps to exactly one WP (checked by script).
