# Build-out waves 6–27

> Generated schedule. Rules and how to run a wave: [`README.md`](README.md). Item detail: `items/<MODULE>.md`.
> A wave may start an item as soon as all its **Depends** are merged; it does not wait for the whole previous wave.

| Wave | Phase | Items | P0 | Modules | Status |
|---|---|---|---|---|---|
| 6 | MVP | 15 | 8 | WEB, PTY, PFC, RAT, UW, POL, RI, FIN, DOC, PLT, DAT, MIG | not started |
| 7 | MVP | 15 | 5 | PTY, PFC, UW, BIL, CLM, RI, FIN, DOC, CHN, DAT, MIG | not started |
| 8 | MVP | 15 | 4 | PTY, PFC, RAT, POL, BIL, CLM, RI, FIN, DAT, MIG, MKT | not started |
| 9 | MVP | 15 | 2 | PTY, RAT, POL, CLM, RI, FIN, DOC, CMP, CHN, PLT, MIG | not started |
| 10 | MVP | 15 | 1 | PTY, UW, BIL, RI, CMP, CHN, WRK, DAT | not started |
| 11 | MVP | 15 | 0 | PTY, PFC, RAT, POL, BIL, FIN, DOC, CHN, DAT, MIG, MKT | not started |
| 12 | MVP | 15 | 0 | UW, BIL, CLM, RI, CMP, CHN, WRK | not started |
| 13 | MVP | 15 | 0 | PTY, PFC, POL, BIL, CLM, FIN, PLT, MIG, MKT | not started |
| 14 | MVP | 15 | 0 | UW, BIL, RI, CHN, WRK | not started |
| 15 | MVP | 15 | 0 | CLM, FIN, DOC, CMP, DAT | not started |
| 16 | MVP | 15 | 0 | PTY, PFC, POL, FIN, PLT, MIG | not started |
| 17 | MVP | 15 | 0 | PTY, RAT, UW, RI, MIG | not started |
| 18 | MVP | 8 | 0 | UW, WRK, MIG | not started |
| 19 | MVP | 1 | 0 | MIG | not started |
| 20 | MVP | 1 | 0 | MIG | not started |
| 21 | Post-MVP | 15 | 0 | PTY, PFC, RAT, BIL, CLM, RI, FIN, CHN, PLT, MKT | not started |
| 22 | Post-MVP | 15 | 0 | UW, BIL, CLM, FIN, CHN, WRK | not started |
| 23 | Post-MVP | 15 | 0 | BIL, CLM, CMP, CHN, DAT | not started |
| 24 | Post-MVP | 15 | 0 | FIN, DOC, CHN, DAT, MIG | not started |
| 25 | Post-MVP | 15 | 0 | PFC, PLT, MIG, MKT | not started |
| 26 | Post-MVP | 15 | 0 | PTY, RAT, POL, PLT, MIG | not started |
| 27 | Post-MVP | 15 | 0 | UW, POL, RI, WRK, MIG | not started |

## Wave 6 — MVP (15 items)

- [BO-WEB-01](items/WEB.md#bo-web-01) — Module route and nav auto-registration · P0 · M · after: —
- [BO-PFC-01](items/PFC.md#bo-pfc-01) — Draft and change-set store · P0 · L · after: —
- [BO-RAT-05](items/RAT.md#bo-rat-05) — Rate-table import, validate, diff · P0 · L · after: —
- [BO-UW-01](items/UW.md#bo-uw-01) — Rule-set authoring API · P0 · L · after: —
- [BO-RI-03](items/RI.md#bo-ri-03) — Contract versions: endorse and versionAt · P0 · M · after: —
- [BO-FIN-03](items/FIN.md#bo-fin-03) — Intake exception actions · P0 · M · after: —
- [BO-MIG-01](items/MIG.md#bo-mig-01) — Batches run in the background · P0 · L · after: —
- [BO-MIG-06](items/MIG.md#bo-mig-06) — Party importer · P0 · L · after: —
- [BO-PTY-01](items/PTY.md#bo-pty-01) — Party update, validate and identifier verify · P1 · M · after: —
- [BO-PTY-08](items/PTY.md#bo-pty-08) — Intermediary lifecycle, CPD and register import · P1 · L · after: —
- [BO-POL-02](items/POL.md#bo-pol-02) — Policy read APIs: getMany, status model, effective-date limits, segment changes · P1 · M · after: —
- [BO-RI-08](items/RI.md#bo-ri-08) — Recovery recalculate, adjust and occurrences · P1 · L · after: —
- [BO-DOC-05](items/DOC.md#bo-doc-05) — Persisted template and clause catalogue · P1 · L · after: —
- [BO-PLT-02](items/PLT.md#bo-plt-02) — Approval request, withdraw, verify-for-execution, type register · P1 · M · after: —
- [BO-DAT-01](items/DAT.md#bo-dat-01) — Data-contract registry and event ingestion into rpt_raw · P1 · L · after: —

## Wave 7 — MVP (15 items)

- [BO-PFC-04](items/PFC.md#bo-pfc-04) — Release lifecycle: submit, sign-off, schedule · P0 · L · after: BO-PFC-01
- [BO-UW-02](items/UW.md#bo-uw-02) — Rule-set release and issue-type catalogue · P0 · L · after: BO-UW-01
- [BO-FIN-01a](items/FIN.md#bo-fin-01a) — Manual journals: persistence, create/list/get/withdraw · P0 · M · after: BO-WEB-01
- [BO-FIN-04](items/FIN.md#bo-fin-04) — Period close: state machine and tasks · P0 · L · after: BO-FIN-03
- [BO-MIG-10](items/MIG.md#bo-mig-10) — Reconciliation engine · P0 · L · after: BO-MIG-06
- [BO-PTY-03](items/PTY.md#bo-pty-03) — Party roles and relationships · P1 · M · after: BO-PTY-01
- [BO-BIL-08](items/BIL.md#bo-bil-08) — Suspense: model and API · P1 · M · after: —
- [BO-CLM-01](items/CLM.md#bo-clm-01) — Exposure lifecycle API · P1 · M · after: —
- [BO-RI-10](items/RI.md#bo-ri-10) — Deposit, adjustment premium and commission · P1 · L · after: BO-RI-03
- [BO-DOC-01](items/DOC.md#bo-doc-01) — Document library read API (cross-object search) · P1 · M · after: —
- [BO-DOC-04](items/DOC.md#bo-doc-04) — Archive completeness and evidence-pack export · P1 · M · after: —
- [BO-DOC-06](items/DOC.md#bo-doc-06) — Payload dictionary and template composer · P1 · L · after: BO-DOC-05
- [BO-CHN-05](items/CHN.md#bo-chn-05) — Transaction-permission matrix · P1 · L · after: —
- [BO-DAT-02](items/DAT.md#bo-dat-02) — Conformed bitemporal fact tables · P1 · L · after: BO-DAT-01
- [BO-MIG-02](items/MIG.md#bo-mig-02) — Legacy simulator and staging · P1 · M · after: —

## Wave 8 — MVP (15 items)

- [BO-CLM-05](items/CLM.md#bo-clm-05) — MTPL statutory clocks wired to CMP · P0 · M · after: BO-CLM-01
- [BO-FIN-01b](items/FIN.md#bo-fin-01b) — Manual journals: submit/decide and posting · P0 · M · after: BO-FIN-01a
- [BO-FIN-06a](items/FIN.md#bo-fin-06a) — Earning engine and run · P0 · L · after: BO-FIN-04
- [BO-MIG-11](items/MIG.md#bo-mig-11) — Sign-off workflow · P0 · M · after: BO-MIG-10
- [BO-PTY-04](items/PTY.md#bo-pty-04) — Accounts and households · P1 · L · after: BO-PTY-03
- [BO-PFC-05](items/PFC.md#bo-pfc-05) — Runtime services: availability, describe, draft validation · P1 · M · after: —
- [BO-RAT-04](items/RAT.md#bo-rat-04) — Rating artefact catalogue reads · P1 · S · after: —
- [BO-POL-09](items/POL.md#bo-pol-09) — Renewal decline and non-renewal · P1 · M · after: —
- [BO-BIL-09](items/BIL.md#bo-bil-09) — Disbursement request and payee list/verify · P1 · M · after: —
- [BO-CLM-02](items/CLM.md#bo-clm-02) — Claim reopen, merge and FNOL draft · P1 · M · after: BO-CLM-01
- [BO-CLM-08](items/CLM.md#bo-clm-08) — Claim tracking and catastrophe events · P1 · M · after: —
- [BO-RI-11](items/RI.md#bo-ri-11) — Technical account items and balances · P1 · L · after: BO-RI-08, BO-RI-10
- [BO-DAT-03](items/DAT.md#bo-dat-03) — Mart engine: run, get, figure, compare · P1 · L · after: BO-DAT-02
- [BO-MIG-03](items/MIG.md#bo-mig-03) — DQ rule library and profiling · P1 · L · after: BO-MIG-02
- [BO-MKT-01](items/MKT.md#bo-mkt-01) — Config change requests: create, update, submit, decide, withdraw · P1 · L · after: BO-PLT-02

## Wave 9 — MVP (15 items)

- [BO-RAT-06](items/RAT.md#bo-rat-06) — Artefact build and lifecycle · P0 · L · after: BO-RAT-04, BO-RAT-05
- [BO-CHN-01](items/CHN.md#bo-chn-01) — Customer identity link and portal authorisation · P0 · M · after: BO-PTY-04
- [BO-PTY-02](items/PTY.md#bo-pty-02) — Addresses and contact points · P1 · M · after: BO-PTY-01
- [BO-PTY-05](items/PTY.md#bo-pty-05) — Duplicate detection, merge and unmerge · P1 · L · after: BO-PTY-01
- [BO-PTY-10](items/PTY.md#bo-pty-10) — Commission agreements: versions and approval · P1 · L · after: BO-PTY-08
- [BO-POL-03](items/POL.md#bo-pol-03) — Quote job lifecycle: list, newVersion, requote, copyVersion, notTaken · P1 · L · after: —
- [BO-CLM-04](items/CLM.md#bo-clm-04) — Transaction-set decision routes and daily totals · P1 · M · after: —
- [BO-RI-01](items/RI.md#bo-ri-01) — Programme aggregate and write API · P1 · M · after: —
- [BO-RI-05](items/RI.md#bo-ri-05) — Participations, security rules, line tables · P1 · L · after: BO-RI-03
- [BO-RI-15](items/RI.md#bo-ri-15) — Cash calls · P1 · L · after: BO-RI-11
- [BO-FIN-08](items/FIN.md#bo-fin-08) — Reconciliation framework · P1 · L · after: BO-FIN-04
- [BO-DOC-02](items/DOC.md#bo-doc-02) — Document lifecycle operations · P1 · M · after: —
- [BO-CMP-06](items/CMP.md#bo-cmp-06) — Fiscal document operations (backend) · P1 · L · after: —
- [BO-PLT-05](items/PLT.md#bo-plt-05) — User directory and role assignments · P1 · L · after: BO-PLT-02
- [BO-MIG-04](items/MIG.md#bo-mig-04) — Mapping specs and code tables · P1 · L · after: BO-MIG-03

## Wave 10 — MVP (15 items)

- [BO-CHN-12](items/CHN.md#bo-chn-12) — Portal shell and routing (UI) · P0 · M · after: BO-CHN-01
- [BO-PTY-09](items/PTY.md#bo-pty-09) — Producer of record · P1 · L · after: BO-PTY-08
- [BO-PTY-11](items/PTY.md#bo-pty-11) — Commission resolution and simulation · P1 · M · after: BO-PTY-10
- [BO-UW-03](items/UW.md#bo-uw-03) — Batch evaluation and issue reads · P1 · M · after: —
- [BO-UW-05](items/UW.md#bo-uw-05) — Referral commands and review lock · P1 · L · after: —
- [BO-BIL-03](items/BIL.md#bo-bil-03) — Payment plan catalogue, select/change, preview · P1 · M · after: —
- [BO-BIL-07](items/BIL.md#bo-bil-07) — Receipt list, unallocate, reallocate · P1 · M · after: —
- [BO-BIL-14](items/BIL.md#bo-bil-14) — Levy payment request and levy tracking · P1 · M · after: BO-BIL-09
- [BO-RI-14](items/RI.md#bo-ri-14) — Reinsurer statements · P1 · L · after: BO-RI-11
- [BO-CMP-09](items/CMP.md#bo-cmp-09) — Regulatory submissions tracker (backend) · P1 · L · after: BO-DOC-04
- [BO-CHN-02](items/CHN.md#bo-chn-02) — Portal read API: dashboard, policies, policy detail · P1 · M · after: BO-CHN-01, BO-POL-02
- [BO-WRK-02](items/WRK.md#bo-wrk-02) — Assignment rules, simulate, user validation · P1 · M · after: —
- [BO-DAT-04](items/DAT.md#bo-dat-04) — Mart adjust, sign-off and export · P1 · M · after: BO-DAT-03
- [BO-DAT-05](items/DAT.md#bo-dat-05) — Lineage and coverage · P1 · M · after: BO-DAT-03
- [BO-DAT-06](items/DAT.md#bo-dat-06) — Model registry lifecycle and drift metrics · P1 · M · after: —

## Wave 11 — MVP (15 items)

- [BO-PTY-07](items/PTY.md#bo-pty-07) — Screening cases and bulk · P1 · L · after: —
- [BO-PTY-12](items/PTY.md#bo-pty-12) — Delegated agency administration · P1 · L · after: BO-PTY-08, BO-PTY-09
- [BO-PFC-10](items/PFC.md#bo-pfc-10) — UI: product catalogue `/products` and `/products/:code` · P1 · L · after: BO-PFC-05, BO-WEB-01
- [BO-RAT-02](items/RAT.md#bo-rat-02) — Worksheet read side: explain, reperform, export, attach · P1 · M · after: —
- [BO-POL-06](items/POL.md#bo-pol-06) — Scheduled cancellation and rescind · P1 · M · after: —
- [BO-POL-08](items/POL.md#bo-pol-08) — Suspension and reactivation · P1 · M · after: —
- [BO-POL-10](items/POL.md#bo-pol-10) — Renewal run (batch) · P1 · L · after: BO-POL-09
- [BO-BIL-06a](items/BIL.md#bo-bil-06a) — Mandates and instruments: model and API · P1 · L · after: —
- [BO-FIN-09](items/FIN.md#bo-fin-09) — Tax returns (IPT) · P1 · L · after: BO-FIN-04, BO-BIL-09
- [BO-DOC-12](items/DOC.md#bo-doc-12) — UI: document library `/documents` · P1 · L · after: BO-DOC-01, BO-DOC-02, BO-WEB-01
- [BO-CHN-04](items/CHN.md#bo-chn-04) — Portal billing, documents, notifications, messages · P1 · L · after: BO-CHN-02, BO-DOC-01
- [BO-DAT-11](items/DAT.md#bo-dat-11) — UI /reports: report catalogue and run page · P1 · L · after: BO-DAT-03, BO-WEB-01
- [BO-MIG-07](items/MIG.md#bo-mig-07) — Renewal-based conversion waves · P1 · L · after: BO-MIG-04, BO-MIG-06
- [BO-MKT-02](items/MKT.md#bo-mkt-02) — Config change requests: schedule, rebase, preview, import · P1 · M · after: BO-MKT-01
- [BO-MKT-06](items/MKT.md#bo-mkt-06) — Statutory clock sets and regime codes · P1 · M · after: —

## Wave 12 — MVP (15 items)

- [BO-UW-04](items/UW.md#bo-uw-04) — Issue lifecycle commands · P1 · M · after: BO-UW-03
- [BO-UW-07](items/UW.md#bo-uw-07) — Declines and refusal register · P1 · L · after: BO-DOC-06
- [BO-BIL-01](items/BIL.md#bo-bil-01) — Billing account lifecycle API · P1 · M · after: —
- [BO-BIL-10](items/BIL.md#bo-bil-10) — Disbursement batches · P1 · L · after: BO-BIL-09
- [BO-BIL-11](items/BIL.md#bo-bil-11) — Bank reconciliation · P1 · L · after: BO-BIL-08
- [BO-CLM-03](items/CLM.md#bo-clm-03) — Coverage decision · P1 · M · after: BO-CLM-02
- [BO-CLM-06](items/CLM.md#bo-clm-06) — Statutory offer (MTPL) · P1 · L · after: BO-CLM-05, BO-CLM-04
- [BO-CLM-07](items/CLM.md#bo-clm-07) — Claims-history certificate · P1 · M · after: BO-DOC-06
- [BO-RI-16](items/RI.md#bo-ri-16) — Manual settlement · P1 · L · after: BO-RI-14, BO-RI-15
- [BO-CMP-01](items/CMP.md#bo-cmp-01) — UI: complaints list `/compliance/complaints` · P1 · M · after: BO-WEB-01
- [BO-CMP-07](items/CMP.md#bo-cmp-07) — Fiscal reconciliation (backend) · P1 · M · after: BO-CMP-06
- [BO-CHN-03](items/CHN.md#bo-chn-03) — Portal self-service commands: change preview/confirm, renewal, cancellation request, contact update · P1 · L · after: BO-CHN-02, BO-PTY-02, BO-POL-09
- [BO-WRK-03](items/WRK.md#bo-wrk-03) — Persisted queues and pattern catalogue · P1 · L · after: —
- [BO-WRK-04](items/WRK.md#bo-wrk-04) — New system-generated activity patterns and raisers · P1 · M · after: —
- [BO-WRK-07](items/WRK.md#bo-wrk-07) — Notes API · P1 · M · after: —

## Wave 13 — MVP (15 items)

- [BO-PTY-06](items/PTY.md#bo-pty-06) — Consent and communication preferences · P1 · M · after: BO-PTY-01
- [BO-PFC-02](items/PFC.md#bo-pfc-02) — Change-set revert and rebase · P1 · M · after: BO-PFC-01
- [BO-PFC-03](items/PFC.md#bo-pfc-03) — Version diff and YAML export/import · P1 · M · after: BO-PFC-01
- [BO-POL-01](items/POL.md#bo-pol-01) — Policy search on `/policies` (UI) · P1 · M · after: BO-WEB-01
- [BO-POL-07](items/POL.md#bo-pol-07) — Reinstatement and rewrite · P1 · L · after: BO-POL-06
- [BO-BIL-13](items/BIL.md#bo-bil-13) — Write-offs and money transfers · P1 · L · after: BO-BIL-07
- [BO-CLM-14](items/CLM.md#bo-clm-14) — Import and DSAR routes · P1 · M · after: —
- [BO-FIN-07](items/FIN.md#bo-fin-07) — Posting rule authoring · P1 · L · after: —
- [BO-FIN-10](items/FIN.md#bo-fin-10) — Levy returns · P1 · L · after: BO-FIN-09, BO-BIL-14
- [BO-PLT-01](items/PLT.md#bo-plt-01) — Audit query, export and verify endpoints · P1 · M · after: —
- [BO-PLT-04](items/PLT.md#bo-plt-04) — Dead-letter discard and outbox/consumer ops · P1 · M · after: —
- [BO-MIG-12](items/MIG.md#bo-mig-12) — Rehearsal, cut-over and rollback plan · P1 · L · after: BO-MIG-11, BO-MIG-01
- [BO-MKT-03](items/MKT.md#bo-mkt-03) — Configuration explain, compare, export · P1 · M · after: —
- [BO-MKT-04](items/MKT.md#bo-mkt-04) — Pack certify, publish, deprecate · P1 · M · after: —
- [BO-MKT-11](items/MKT.md#bo-mkt-11) — UI /admin/config: change requests · P1 · L · after: BO-MKT-01, BO-MKT-02, BO-WEB-01

## Wave 14 — MVP (15 items)

- [BO-UW-08](items/UW.md#bo-uw-08) — Contingencies · P1 · M · after: BO-UW-04
- [BO-UW-09](items/UW.md#bo-uw-09) — Policy holds and accumulation · P1 · L · after: BO-POL-02
- [BO-BIL-04](items/BIL.md#bo-bil-04) — Down-payment bind gate · P1 · M · after: BO-BIL-03
- [BO-BIL-06b](items/BIL.md#bo-bil-06b) — SEPA direct-debit collection run · P1 · L · after: BO-BIL-06a
- [BO-BIL-12](items/BIL.md#bo-bil-12) — BIL side of BIL-vs-FIN reconciliation and ledger reads · P1 · M · after: BO-FIN-08
- [BO-BIL-19](items/BIL.md#bo-bil-19) — UI: suspense workbench (`/billing/suspense`) · P1 · L · after: BO-BIL-08, BO-WEB-01
- [BO-BIL-20](items/BIL.md#bo-bil-20) — UI: mandates and payment plans (`/billing/mandates`, plan section on `/billing/accounts/:accountId`) · P1 · L · after: BO-BIL-06a, BO-BIL-03, BO-WEB-01
- [BO-RI-02](items/RI.md#bo-ri-02) — Programme approval · P1 · M · after: BO-RI-01
- [BO-RI-04](items/RI.md#bo-ri-04) — Contract end-of-life: expire, commute, close, simulate · P1 · L · after: BO-RI-03
- [BO-RI-07](items/RI.md#bo-ri-07) — Cession preview, rerun and exceptions · P1 · M · after: —
- [BO-RI-13](items/RI.md#bo-ri-13) — Bordereaux · P1 · L · after: BO-RI-11, BO-DOC-06
- [BO-CHN-13](items/CHN.md#bo-chn-13) — Customer portal pages · P1 · L · after: BO-CHN-12, BO-CHN-02, BO-CHN-03, BO-CHN-04
- [BO-WRK-01](items/WRK.md#bo-wrk-01) — Activity update, bulk reassign, team summary, keyset paging · P1 · M · after: BO-WEB-01
- [BO-WRK-11](items/WRK.md#bo-wrk-11) — Inbound documents API and malware scan · P1 · L · after: BO-DOC-04
- [BO-WRK-14](items/WRK.md#bo-wrk-14) — Desktop, global search, palette, recent items (backend) · P1 · L · after: BO-DOC-01

## Wave 15 — MVP (15 items)

- [BO-CLM-15](items/CLM.md#bo-clm-15) — FS partial collection and statement lines · P1 · M · after: —
- [BO-CLM-16](items/CLM.md#bo-clm-16) — Claims UI: exposure and claim actions · P1 · M · after: BO-CLM-01, BO-CLM-02, BO-CLM-03, BO-WEB-01
- [BO-CLM-17](items/CLM.md#bo-clm-17) — Claims UI: MTPL offer and certificate · P1 · M · after: BO-CLM-06, BO-CLM-07, BO-WEB-01
- [BO-FIN-02](items/FIN.md#bo-fin-02) — Journal reverse and balance reads · P1 · M · after: —
- [BO-FIN-06b](items/FIN.md#bo-fin-06b) — Earning simulate and result · P1 · M · after: BO-FIN-06a
- [BO-DOC-03](items/DOC.md#bo-doc-03) — Reprint four-eyes and provider-conflict task · P1 · S · after: BO-WRK-04
- [BO-DOC-07](items/DOC.md#bo-doc-07) — PDF rendering (Gotenberg, PDF/A) · P1 · L · after: BO-DOC-06, BO-DOC-04
- [BO-DOC-13](items/DOC.md#bo-doc-13) — UI: Documents tab on policy, claim, party · P1 · M · after: BO-DOC-12, BO-WEB-01
- [BO-CMP-02](items/CMP.md#bo-cmp-02) — UI: complaint case page and actions · P1 · L · after: BO-CMP-01, BO-WEB-01
- [BO-CMP-03](items/CMP.md#bo-cmp-03) — UI: statutory clocks `/compliance/clocks` · P1 · M · after: —
- [BO-CMP-04](items/CMP.md#bo-cmp-04) — Complaint engine hardening · P1 · M · after: BO-WRK-04
- [BO-CMP-05](items/CMP.md#bo-cmp-05) — Complaint acknowledgement clock definition · P1 · M · after: —
- [BO-CMP-08](items/CMP.md#bo-cmp-08) — Motor bureau adapter (backend) · P1 · L · after: —
- [BO-CMP-14](items/CMP.md#bo-cmp-14) — UI: fiscal documents and reconciliation (split of NX-UI-11) · P1 · L · after: BO-CMP-06, BO-CMP-07
- [BO-DAT-12](items/DAT.md#bo-dat-12) — UI /reports/:martId: figures, compare, lineage, sign-off · P1 · L · after: BO-DAT-04, BO-DAT-05, BO-DAT-11, BO-WEB-01

## Wave 16 — MVP (15 items)

- [BO-PTY-15](items/PTY.md#bo-pty-15) — Party edit, addresses and contacts (UI) · P1 · M · after: BO-PTY-01, BO-PTY-02, BO-WEB-01
- [BO-PTY-16](items/PTY.md#bo-pty-16) — Party 360 tabs: roles, relationships, accounts, consents, vulnerability (UI) · P1 · L · after: BO-PTY-03, BO-PTY-04, BO-PTY-06, BO-POL-01, BO-WEB-01
- [BO-PFC-07](items/PFC.md#bo-pfc-07) — Renewal version conversion · P1 · M · after: BO-PFC-05
- [BO-PFC-11](items/PFC.md#bo-pfc-11) — UI: authoring editor `/products/:code/drafts/:id` · P1 · L · after: BO-PFC-10, BO-PFC-01, BO-PFC-02, BO-PFC-03, BO-WEB-01
- [BO-PFC-12](items/PFC.md#bo-pfc-12) — UI: review, release and fall-back `/products/releases` · P1 · M · after: BO-PFC-10, BO-PFC-04, BO-WEB-01
- [BO-POL-05](items/POL.md#bo-pol-05) — Quotes list and version compare (UI) · P1 · L · after: BO-POL-03, BO-WEB-01
- [BO-POL-13](items/POL.md#bo-pol-13) — Policy lifecycle actions in the policy file (UI) · P1 · L · after: BO-POL-02, BO-POL-06, BO-POL-07, BO-POL-08, BO-POL-09, BO-WEB-01
- [BO-FIN-15](items/FIN.md#bo-fin-15) — Import REST anchors · P1 · M · after: BO-FIN-06a
- [BO-FIN-17](items/FIN.md#bo-fin-17) — Finance ops against the real API · P1 · M · after: BO-FIN-01b, BO-FIN-04, BO-FIN-06a, BO-WEB-01
- [BO-FIN-19](items/FIN.md#bo-fin-19) — UI: tax and levy returns (`/finance/returns`, `/finance/returns/:id`) · P1 · L · after: BO-FIN-09, BO-FIN-10, BO-WEB-01
- [BO-PLT-07](items/PLT.md#bo-plt-07) — Retention catalogue, legal holds, PLT subject ops · P1 · L · after: —
- [BO-PLT-12](items/PLT.md#bo-plt-12) — UI /admin: settings home · P1 · M · after: BO-WEB-01
- [BO-PLT-13](items/PLT.md#bo-plt-13) — UI /admin/audit: audit trail viewer · P1 · M · after: BO-PLT-01, BO-WEB-01
- [BO-PLT-14](items/PLT.md#bo-plt-14) — UI /admin/approvals: approvals inbox · P1 · M · after: BO-PLT-02, BO-WEB-01
- [BO-MIG-05](items/MIG.md#bo-mig-05) — Dry-run conformance · P1 · M · after: BO-MIG-06

## Wave 17 — MVP (15 items)

- [BO-PTY-18](items/PTY.md#bo-pty-18) — Intermediaries and producer codes (UI) · P1 · L · after: BO-PTY-08, BO-WEB-01
- [BO-RAT-01](items/RAT.md#bo-rat-01) — Batch rating · P1 · L · after: —
- [BO-RAT-03](items/RAT.md#bo-rat-03) — Breakdown and change explanation · P1 · M · after: BO-RAT-02
- [BO-RAT-07](items/RAT.md#bo-rat-07) — Golden test suites · P1 · M · after: BO-RAT-06
- [BO-RAT-09](items/RAT.md#bo-rat-09) — Pricing modifications (discounts, loadings, referral signals) · P1 · L · after: BO-RAT-06, BO-UW-05
- [BO-RAT-14](items/RAT.md#bo-rat-14) — UI: rating artefact and worksheet screens `/rating` · P1 · L · after: BO-RAT-02, BO-WEB-01
- [BO-UW-12](items/UW.md#bo-uw-12) — Data subject endpoints and restriction marker · P1 · M · after: —
- [BO-UW-13](items/UW.md#bo-uw-13) — Renewal directions and disclosure findings · P1 · L · after: BO-UW-02, BO-POL-10
- [BO-RI-12](items/RI.md#bo-ri-12) — Reinsurer notices · P1 · M · after: BO-DOC-06
- [BO-RI-21](items/RI.md#bo-ri-21) — RI import API and importers · P1 · L · after: BO-RI-01, BO-RI-03
- [BO-RI-23](items/RI.md#bo-ri-23) — RI UI: programmes · P1 · L · after: BO-RI-01, BO-RI-02, BO-WEB-01
- [BO-RI-24](items/RI.md#bo-ri-24) — RI UI: cessions and exceptions · P1 · M · after: BO-RI-07, BO-WEB-01
- [BO-RI-25](items/RI.md#bo-ri-25) — RI UI: contract versions and participations · P1 · L · after: BO-RI-03, BO-RI-04, BO-RI-05
- [BO-RI-26](items/RI.md#bo-ri-26) — RI UI: accounts, statements, settlements · P1 · L · after: BO-RI-13, BO-RI-14, BO-RI-16, BO-WEB-01
- [BO-MIG-08](items/MIG.md#bo-mig-08) — Policy conversion at renewal: term data and tax/levy split · P1 · L · after: BO-MIG-07

## Wave 18 — MVP (8 items)

- [BO-UW-14](items/UW.md#bo-uw-14) — UI: underwriting extras on the workbench · P1 · L · after: BO-UW-03, BO-UW-05, BO-UW-07, BO-UW-08, BO-WEB-01
- [BO-UW-15](items/UW.md#bo-uw-15) — UI: rule-set authoring `/underwriting/rules` · P1 · L · after: BO-UW-01, BO-UW-02, BO-WEB-01
- [BO-WRK-05](items/WRK.md#bo-wrk-05) — SLA business calendars and priority score · P1 · M · after: —
- [BO-WRK-08](items/WRK.md#bo-wrk-08) — UI: notes panel · P1 · M · after: BO-WRK-07, BO-WEB-01
- [BO-WRK-12](items/WRK.md#bo-wrk-12) — UI: inbound document intake `/work/inbound` · P1 · L · after: BO-WRK-11, BO-WEB-01
- [BO-WRK-15](items/WRK.md#bo-wrk-15) — UI: command palette and global search · P1 · M · after: BO-WRK-14
- [BO-WRK-16](items/WRK.md#bo-wrk-16) — UI: supervisor team view · P1 · M · after: BO-WRK-01, BO-WRK-02, BO-WEB-01
- [BO-MIG-09](items/MIG.md#bo-mig-09) — Open-claim tail split · P1 · L · after: BO-CLM-14

## Wave 19 — MVP (1 items)

- [BO-MIG-17](items/MIG.md#bo-mig-17) — MIG UI: waves and quality · P1 · L · after: BO-MIG-03, BO-MIG-04, BO-MIG-07, BO-WEB-01

## Wave 20 — MVP (1 items)

- [BO-MIG-18](items/MIG.md#bo-mig-18) — MIG UI: reconciliation, sign-off, cut-over · P1 · L · after: BO-MIG-10, BO-MIG-11, BO-MIG-12

## Wave 21 — Post-MVP (15 items)

- [BO-PTY-13](items/PTY.md#bo-pty-13) — Life events, account merge/split/policy move · P2 · L · after: BO-PTY-04, BO-PTY-05
- [BO-PFC-06b](items/PFC.md#bo-pfc-06b) — Reference tables and legacy cross-references · P2 · M · after: —
- [BO-RAT-11](items/RAT.md#bo-rat-11) — Compare versions, shadow runs, bonus-malus · P2 · L · after: BO-RAT-06
- [BO-BIL-15a](items/BIL.md#bo-bil-15a) — Agency bill: intermediary collections · P2 · L · after: BO-PTY-10, BO-BIL-08
- [BO-CLM-09](items/CLM.md#bo-clm-09) — Vendors and service requests · P2 · M · after: —
- [BO-CLM-12](items/CLM.md#bo-clm-12) — Diary and claimant conversations · P2 · L · after: BO-DOC-06, BO-CLM-02
- [BO-RI-18](items/RI.md#bo-ri-18) — Facultative placements · P2 · L · after: BO-RI-05, BO-DOC-06
- [BO-RI-19](items/RI.md#bo-ri-19) — Accumulation and capacity · P2 · L · after: BO-RI-08
- [BO-FIN-13](items/FIN.md#bo-fin-13) — GL extract and group pack · P2 · L · after: BO-FIN-04
- [BO-FIN-14a](items/FIN.md#bo-fin-14a) — IFRS 17 groups and movements · P2 · L · after: BO-FIN-06a
- [BO-CHN-07](items/CHN.md#bo-chn-07) — Customer and broker claim reporting (backend) · P2 · M · after: BO-CHN-02, BO-CLM-08
- [BO-CHN-08](items/CHN.md#bo-chn-08) — Partner API foundation: products, partners, clients, webhooks · P2 · L · after: BO-CHN-05
- [BO-PLT-09](items/PLT.md#bo-plt-09) — Numbering, time, jobs and expression endpoints · P2 · M · after: —
- [BO-MKT-05](items/MKT.md#bo-mkt-05) — Capabilities as pack data + `Capability.get` · P2 · M · after: —
- [BO-MKT-08](items/MKT.md#bo-mkt-08) — Currency rule, fixed conversion, changeover · P2 · S · after: —

## Wave 22 — Post-MVP (15 items)

- [BO-UW-10](items/UW.md#bo-uw-10) — External reports, inspections, valuation · P2 · L · after: BO-PFC-06b
- [BO-BIL-02](items/BIL.md#bo-bil-02) — Attach and move term between accounts · P2 · M · after: BO-BIL-01
- [BO-BIL-05](items/BIL.md#bo-bil-05) — Invoice copy and dispute · P2 · S · after: BO-DOC-06
- [BO-BIL-15b](items/BIL.md#bo-bil-15b) — Account current · P2 · M · after: BO-BIL-15a
- [BO-BIL-16](items/BIL.md#bo-bil-16) — Commission calculation and statements · P2 · L · after: BO-PTY-11, BO-BIL-15a
- [BO-BIL-18](items/BIL.md#bo-bil-18) — Import and DSAR REST anchors · P2 · M · after: —
- [BO-BIL-22](items/BIL.md#bo-bil-22) — UI: bank reconciliation and write-offs (`/billing/reconciliation`, `/billing/write-offs`) · P2 · L · after: BO-BIL-11, BO-BIL-13, BO-WEB-01
- [BO-CLM-10](items/CLM.md#bo-clm-10) — Vendor invoices · P2 · M · after: BO-CLM-09
- [BO-FIN-12](items/FIN.md#bo-fin-12) — FX revaluation and translation · P2 · L · after: BO-MKT-08, BO-FIN-04
- [BO-FIN-14b](items/FIN.md#bo-fin-14b) — Actuarial inputs and results · P2 · L · after: BO-FIN-14a, BO-FIN-01b
- [BO-CHN-06](items/CHN.md#bo-chn-06) — Customer quote-and-buy journeys (backend) · P2 · L · after: BO-CHN-01, BO-CHN-05
- [BO-CHN-09](items/CHN.md#bo-chn-09) — Partner resource API: product, quote, policy, documents · P2 · L · after: BO-CHN-08, BO-POL-03, BO-PTY-12
- [BO-CHN-11](items/CHN.md#bo-chn-11) — Channel DSAR, agent logs, co-view · P2 · M · after: BO-CHN-04
- [BO-CHN-15](items/CHN.md#bo-chn-15) — Customer claims UI · P2 · M · after: BO-CHN-07, BO-CHN-12
- [BO-WRK-09](items/WRK.md#bo-wrk-09) — Back-office requests API · P2 · M · after: BO-WRK-03

## Wave 23 — Post-MVP (15 items)

- [BO-BIL-17](items/BIL.md#bo-bil-17) — Commission payments · P2 · M · after: BO-BIL-16, BO-BIL-10
- [BO-BIL-21](items/BIL.md#bo-bil-21) — UI: account-currents and commission (`/billing/account-currents`, `/billing/commission`) · P2 · L · after: BO-BIL-15b, BO-BIL-16, BO-WEB-01
- [BO-CLM-11](items/CLM.md#bo-clm-11) — Fraud score and SIU referral · P2 · M · after: BO-CLM-02
- [BO-CLM-13](items/CLM.md#bo-clm-13) — Redress and litigation · P2 · L · after: BO-CLM-04
- [BO-CLM-18](items/CLM.md#bo-clm-18) — Claims UI: tracking, cat events, vendors, diary · P2 · L · after: BO-CLM-08, BO-CLM-10, BO-CLM-12, BO-WEB-01
- [BO-CMP-10](items/CMP.md#bo-cmp-10) — Annual returns and ENFIA (backend) · P2 · L · after: BO-CMP-09
- [BO-CMP-11](items/CMP.md#bo-cmp-11) — Taxonomy and XBRL · P2 · M · after: BO-CMP-09
- [BO-CMP-12](items/CMP.md#bo-cmp-12) — Evidence, traceability and import endpoints · P2 · M · after: —
- [BO-CMP-13](items/CMP.md#bo-cmp-13) — Obligations register and regulatory change · P2 · L · after: —
- [BO-CHN-10](items/CHN.md#bo-chn-10) — Partner finance, book and claims resources · P2 · L · after: BO-CHN-09, BO-BIL-15b, BO-BIL-16
- [BO-CHN-16](items/CHN.md#bo-chn-16) — Staff permission-matrix admin UI · P2 · M · after: BO-CHN-05, BO-WEB-01
- [BO-CHN-17](items/CHN.md#bo-chn-17) — Partner developer console UI · P2 · L · after: BO-CHN-08, BO-WEB-01
- [BO-DAT-07](items/DAT.md#bo-dat-07) — Purpose grants and DSAR on marts · P2 · M · after: BO-DAT-02
- [BO-DAT-08](items/DAT.md#bo-dat-08) — Accumulation views · P2 · M · after: BO-DAT-02
- [BO-DAT-09](items/DAT.md#bo-dat-09) — Actuarial supply and SCR results · P2 · L · after: BO-DAT-04, BO-FIN-14b

## Wave 24 — Post-MVP (15 items)

- [BO-FIN-05](items/FIN.md#bo-fin-05) — Close evidence pack · P2 · M · after: BO-FIN-04
- [BO-FIN-11](items/FIN.md#bo-fin-11) — Fiscal reconciliation · P2 · M · after: BO-CMP-06, BO-FIN-08
- [BO-FIN-16](items/FIN.md#bo-fin-16) — DSAR REST anchors · P2 · S · after: —
- [BO-FIN-18](items/FIN.md#bo-fin-18) — UI: reconciliation and breaks (`/finance/reconciliation`) · P2 · L · after: BO-FIN-08, BO-WEB-01
- [BO-FIN-20](items/FIN.md#bo-fin-20) — UI: posting rules admin (`/finance/posting-rules`) · P2 · L · after: BO-FIN-07, BO-WEB-01
- [BO-FIN-21](items/FIN.md#bo-fin-21) — UI: GL extracts, FX and IFRS 17 (`/finance/gl-extracts`, `/finance/fx`, `/finance/ifrs17`) · P2 · L · after: BO-FIN-12, BO-FIN-13, BO-FIN-14a, BO-WEB-01
- [BO-DOC-08](items/DOC.md#bo-doc-08) — Proof of cover and IPID · P2 · M · after: BO-DOC-05
- [BO-DOC-09](items/DOC.md#bo-doc-09) — Batch, as-of render, import · P2 · M · after: BO-DOC-05, BO-DOC-06
- [BO-DOC-10](items/DOC.md#bo-doc-10) — E-signature envelopes (stub provider) · P2 · L · after: BO-DOC-04
- [BO-DOC-11](items/DOC.md#bo-doc-11) — Subject export / restrict / erase over HTTP · P2 · S · after: —
- [BO-DOC-14](items/DOC.md#bo-doc-14) — UI: template catalogue viewer/admin · P2 · M · after: BO-DOC-05, BO-DOC-06, BO-DOC-12
- [BO-CHN-14](items/CHN.md#bo-chn-14) — Broker/agent portal shell and pages · P2 · L · after: BO-CHN-09, BO-CHN-10, BO-PTY-12, BO-CHN-12, BO-WEB-01
- [BO-DAT-10](items/DAT.md#bo-dat-10) — Feature store, synthetic data, analytics ask · P2 · M · after: BO-DAT-06, BO-DAT-02
- [BO-DAT-13](items/DAT.md#bo-dat-13) — UI /reports/quality and /reports/models · P2 · M · after: BO-DAT-01, BO-DAT-05, BO-DAT-06, BO-DAT-11
- [BO-MIG-13](items/MIG.md#bo-mig-13) — Misdirected-payment rerouting · P2 · M · after: —

## Wave 25 — Post-MVP (15 items)

- [BO-PFC-06a](items/PFC.md#bo-pfc-06a) — POG, IPID and regulatory mapping data · P2 · L · after: —
- [BO-PFC-08](items/PFC.md#bo-pfc-08) — Product impact analysis · P2 · M · after: BO-PFC-01, BO-POL-02
- [BO-PFC-09](items/PFC.md#bo-pfc-09) — Second product seed (needs user decision, see D) · P2 · L · after: BO-RAT-04
- [BO-PLT-03](items/PLT.md#bo-plt-03) — Authority endpoints · P2 · M · after: —
- [BO-PLT-06](items/PLT.md#bo-plt-06) — External users and privileged access · P2 · L · after: BO-PLT-05
- [BO-PLT-08](items/PLT.md#bo-plt-08) — Calendars, holidays, FX and currency · P2 · L · after: —
- [BO-PLT-10](items/PLT.md#bo-plt-10) — Feature flags by LE and role, AI input allow-list · P2 · M · after: —
- [BO-PLT-11](items/PLT.md#bo-plt-11) — Migration import operations · P2 · M · after: BO-PLT-05
- [BO-MIG-14](items/MIG.md#bo-mig-14) — Bureau and fiscal hand-overs · P2 · M · after: BO-MIG-11
- [BO-MIG-15](items/MIG.md#bo-mig-15) — Legacy archive and decommissioning · P2 · L · after: BO-MIG-02
- [BO-MKT-07](items/MKT.md#bo-mkt-07) — Localisation and terminology service · P2 · L · after: BO-MKT-01
- [BO-MKT-09](items/MKT.md#bo-mkt-09) — SPI catalogue, bind, suspend · P2 · M · after: BO-MKT-01
- [BO-MKT-10](items/MKT.md#bo-mkt-10) — Risk location and cross-border · P2 · M · after: BO-MKT-06
- [BO-MKT-12](items/MKT.md#bo-mkt-12) — UI: effective configuration, capabilities, clocks (read-only) · P2 · M · after: BO-MKT-03, BO-MKT-05, BO-MKT-06, BO-MKT-11
- [BO-MKT-13](items/MKT.md#bo-mkt-13) — UI: pack lifecycle actions · P2 · M · after: BO-MKT-04, BO-WEB-01

## Wave 26 — Post-MVP (15 items)

- [BO-PTY-14](items/PTY.md#bo-pty-14) — PTY DSAR endpoints and party import API · P2 · L · after: BO-PTY-05, BO-PTY-07
- [BO-PTY-17](items/PTY.md#bo-pty-17) — Duplicates and merge workbench (UI) · P2 · M · after: BO-PTY-05, BO-WEB-01
- [BO-PTY-19](items/PTY.md#bo-pty-19) — Producer of record and commission screens (UI) · P2 · L · after: BO-PTY-09, BO-PTY-10, BO-PTY-11, BO-WEB-01
- [BO-PTY-20](items/PTY.md#bo-pty-20) — Screening case queue (UI) · P2 · M · after: BO-PTY-07, BO-WEB-01
- [BO-RAT-08](items/RAT.md#bo-rat-08) — Day count and year fraction endpoint · P2 · S · after: —
- [BO-RAT-10](items/RAT.md#bo-rat-10) — Multi-term and multi-risk rating · P2 · L · after: —
- [BO-RAT-12](items/RAT.md#bo-rat-12) — Impact analysis · P2 · M · after: BO-RAT-11, BO-POL-02
- [BO-RAT-13](items/RAT.md#bo-rat-13) — Legacy rating import · P2 · M · after: —
- [BO-POL-04](items/POL.md#bo-pol-04) — Quote compare, rebase, conflict resolution, job lease · P2 · L · after: BO-POL-03
- [BO-POL-11](items/POL.md#bo-pol-11) — Earning and charge reconciliation · P2 · M · after: BO-POL-08
- [BO-POL-14](items/POL.md#bo-pol-14) — Renewal run console (UI) · P2 · M · after: BO-POL-10, BO-WEB-01
- [BO-POL-15](items/POL.md#bo-pol-15) — Policy move between accounts · P2 · M · after: BO-PTY-13
- [BO-PLT-15](items/PLT.md#bo-plt-15) — UI /admin/dead-letters and /admin/operations · P2 · M · after: BO-PLT-04, BO-PLT-09
- [BO-PLT-16](items/PLT.md#bo-plt-16) — UI /admin/users: users and roles · P2 · M · after: BO-PLT-05
- [BO-MIG-16](items/MIG.md#bo-mig-16) — Cross-system enquiry with legacy view · P2 · M · after: BO-MIG-02

## Wave 27 — Post-MVP (15 items)

- [BO-UW-06](items/UW.md#bo-uw-06) — SLA, priority and routing · P2 · M · after: BO-UW-05
- [BO-UW-11](items/UW.md#bo-uw-11) — Intakes and nat-cat · P2 · M · after: BO-UW-10
- [BO-UW-16](items/UW.md#bo-uw-16) — UI: holds and declines `/underwriting/holds`, `/underwriting/declines` · P2 · M · after: BO-UW-07, BO-UW-09
- [BO-POL-12](items/POL.md#bo-pol-12) — Policy import API and policy DSAR endpoints · P2 · M · after: —
- [BO-RI-06](items/RI.md#bo-ri-06) — Treaty types: quota share and surplus · P2 · L · after: BO-RI-03
- [BO-RI-09](items/RI.md#bo-ri-09) — Catastrophe occurrences · P2 · M · after: BO-RI-08, BO-CLM-08
- [BO-RI-17](items/RI.md#bo-ri-17) — Collateral · P2 · M · after: BO-RI-15
- [BO-RI-20](items/RI.md#bo-ri-20) — IFRS 17 data and RI close · P2 · L · after: BO-RI-16, BO-DAT-03
- [BO-RI-22](items/RI.md#bo-ri-22) — RI DSAR routes · P2 · S · after: —
- [BO-RI-27](items/RI.md#bo-ri-27) — RI UI: FAC and accumulation · P2 · L · after: BO-RI-18, BO-RI-19
- [BO-WRK-06](items/WRK.md#bo-wrk-06) — Absence and delegation · P2 · M · after: BO-WRK-02
- [BO-WRK-10](items/WRK.md#bo-wrk-10) — UI: back-office requests · P2 · M · after: BO-WRK-09, BO-WEB-01
- [BO-WRK-13](items/WRK.md#bo-wrk-13) — Participants · P2 · M · after: BO-WRK-02
- [BO-WRK-17](items/WRK.md#bo-wrk-17) — Data protection, exports, imports · P2 · M · after: BO-WRK-07
- [BO-MIG-19](items/MIG.md#bo-mig-19) — MIG UI: archive and enquiry · P2 · M · after: —
