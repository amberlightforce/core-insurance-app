# BUILD-OUT — waves 6–27 (planned 2026-11-27, main @ a49ab865)

**What this is:** the plan for everything in the original master plan (PRDs 01–17, backlog waves W1–W9) that is
still missing after slices 1–6 and hardening waves 1–5 (`../NEXT-STEPS.md`). It is the only current plan for new
functionality. Hardening waves 1–5 stay in `../NEXT-STEPS.md` and finish first.

**Where things are**

| File | What it holds | Who edits it |
|---|---|---|
| `README.md` (this file) | Rules, phases, P0 list, open user decisions, totals | Planner/orchestrator |
| [`WAVES.md`](WAVES.md) | The schedule: every wave, its items, what each waits for, and wave status | Orchestrator, after each wave |
| [`items/<MODULE>.md`](items/) | The work items, one file per module, at NEXT-STEPS detail, with a **Status** column | The builder sets its own item's Status |
| [`COVERAGE.md`](COVERAGE.md) | What of each master-plan work package (W1-PFC-01 …) is really built, and which contract operations exist | Read-only reference (snapshot 2026-11-27) |
| [`SURVEY-NOTES.md`](SURVEY-NOTES.md) | Risks, fakes/stubs and sequencing notes per module group | Read-only reference |

Item files: [WEB](items/WEB.md) · [PTY](items/PTY.md) · [PFC](items/PFC.md) · [RAT](items/RAT.md) · [UW](items/UW.md) ·
[POL](items/POL.md) · [BIL](items/BIL.md) · [CLM](items/CLM.md) · [RI](items/RI.md) · [FIN](items/FIN.md) ·
[DOC](items/DOC.md) · [CMP](items/CMP.md) · [CHN](items/CHN.md) · [WRK](items/WRK.md) · [PLT](items/PLT.md) ·
[DAT](items/DAT.md) · [MIG](items/MIG.md) · [MKT](items/MKT.md)

---

## 1. How it runs

Same process as the hardening phase (D-USR-16/20, `../briefs/LIGHT-WORKFLOW.md`):
- Every item is executed by a **Sonnet 5.5** builder: build + focused tests, push straight to main, no reviewer agents.
- At most **15 builders at once**. No two builders in one wave touch the same file.
- After each push to main, rebuild the coreins Docker stack.
- **UI items** keep the D-USR-11 visual check: light, dark and 390 px screenshots, side by side with
  `core-insurance-prds/design-guide/mockups/aegean-screens-v3.html` (Aegean visual language for every screen the mockup
  does not draw).
- New REST routes must be added to `contracts/openapi/<mod>.yaml` first, then ContractGen and web types regenerated.
  If an item adds operations that are not in the master contract, it says so ("contract addition") in its commit message.
- Open regulatory values keep their fail-closed default (`legalStatus` Unverified/PendingOpinion, refused in Production),
  recorded as a decision (D-USR-19).

**Builder brief for one item** (paste into the builder prompt):
> Build item `<ID>` from `orchestration/build-out/items/<MOD>.md` following `orchestration/briefs/LIGHT-WORKFLOW.md`.
> Read the item row, its Depends rows, and the `<group>` section of `orchestration/build-out/SURVEY-NOTES.md`. When you
> push, set the item's Status to `done <short-sha>` in the same commit. List any `TODO(hardening)` you leave.

**Wave rules used to build the schedule** (keep them when you move items):
1. An item starts only when every item in its **Depends** is `done`. Open NX items it depends on must be merged too.
2. At most one item per module per wave that adds an EF migration (one model snapshot per DbContext).
3. No two items in a wave name the same file in their Scope (controllers, readers, contracts, pages).
4. Additive shared files are allowed in parallel: i18n json, `src/CoreIns.Host/permissions/*.json`, `web/src/auth/roles.ts`,
   module DI registration. Rebase and keep both sides.
5. `web/src/routes.tsx` and `web/src/app-shell/navigation.ts`: after **BO-WEB-01** (wave 6) modules register their own
   routes and rail entries, so UI items no longer edit these two files. Every UI item depends on BO-WEB-01.
6. P0 first, then items that unblock the most others. P2 items wait for the post-MVP phase unless a P0/P1 item needs
   them (three were promoted to P1 for that reason: BO-POL-08, BO-BIL-14, BO-DAT-05).

**After each wave**
- Rebuild the coreins Docker stack and re-walk the page list (every rail entry plus the new routes of the wave).
- Share UI screenshots against the mockup as FYI.
- Set the wave's Status in `WAVES.md`, update `../STATUS.md` (one line), and tick finished items.

## 2. Phases

| Phase | Waves | Items | Ends with |
|---|---|---|---|
| Hardening (existing) | 1–5 | 66 (`../NEXT-STEPS.md`) | Wave 5 in progress at the time of planning |
| **Build-out MVP** | **6–20** | **190** (P0 20, P1 170) | Products, Reports, Documents and Admin pages real; product/rate/rule authoring and release; finance close; customer portal; RI accounting; migration reconciliation and sign-off |
| Build-out post-MVP | 21–27 | 105 (P2) | Broker/agent portal and partner API, agency bill and commission, IFRS 17/FX/GL extract, FAC and accumulation, legacy archive, remaining contract operations |

Waves 18–20 are short because they are the tail of the migration chain (MIG-17 → MIG-18). Post-MVP waves may start
alongside them.

## 3. Where the missing pages land

| Page today | Items that make it real | Waves |
|---|---|---|
| `/products` placeholder | Back end BO-PFC-01, 04, 05 → UI BO-PFC-10 (catalogue), 11 (authoring), 12 (release) | 6–16 |
| `/reports` placeholder | Back end BO-DAT-01, 02, 03, 04 → UI BO-DAT-11 (catalogue/run), 12 (figures, sign-off) | 6–15 |
| `/documents` placeholder | Back end BO-DOC-05, 01, 06, 07 → UI BO-DOC-12 (library), 13 (Documents tab) | 6–15 |
| `/admin` placeholder (heading wrongly says «Home») | Back ends BO-PLT-02, 01, 04 → BO-PLT-12 (admin home, fixes the heading), BO-PLT-13..15; BO-MKT-11 (`/admin/config`) | 6–16 |
| Policies: no search | BO-POL-01 (the API already exists) | 13 |
| Customer portal (none) | MVP: BO-CHN-01 (identity link, P0) → BO-CHN-02..04 (portal APIs) → BO-CHN-12 (shell `/portal/*`) → BO-CHN-13 (customer pages). Post-MVP: quote-and-buy, claims reporting (BO-CHN-06, 07, 15) | 7–14, then 21–22 |
| Broker/agent portal (none) | Post-MVP: partner API BO-CHN-08..10 → BO-CHN-14 (`/agency/*`), after BO-PTY-09/12 (producer of record, agency admin) | 21–24 |
| Finance ops screens calling 404 endpoints | BO-FIN-01a/b, 03, 04, 06a (P0), then BO-FIN-17 rewires the screens | 6–16 |

This plan **supersedes NX-UI-14** (placeholders + billing ops screens: now BO-PFC-10..12, BO-DAT-11..13, BO-DOC-12..14,
BO-BIL-19..22) and **splits NX-UI-11** (fiscal screens: BO-CMP-14 plus back ends BO-CMP-06/07/09).

## 4. P0 items (20)

| ID | Title | Wave |
|---|---|---|
| [BO-WEB-01](items/WEB.md#bo-web-01) | Module route and nav auto-registration | 6 |
| [BO-PFC-01](items/PFC.md#bo-pfc-01) | Draft and change-set store | 6 |
| [BO-RAT-05](items/RAT.md#bo-rat-05) | Rate-table import, validate, diff | 6 |
| [BO-UW-01](items/UW.md#bo-uw-01) | Rule-set authoring API | 6 |
| [BO-RI-03](items/RI.md#bo-ri-03) | Contract versions: endorse and versionAt | 6 |
| [BO-FIN-03](items/FIN.md#bo-fin-03) | Intake exception actions | 6 |
| [BO-MIG-01](items/MIG.md#bo-mig-01) | Batches run in the background | 6 |
| [BO-MIG-06](items/MIG.md#bo-mig-06) | Party importer | 6 |
| [BO-PFC-04](items/PFC.md#bo-pfc-04) | Release lifecycle: submit, sign-off, schedule | 7 |
| [BO-UW-02](items/UW.md#bo-uw-02) | Rule-set release and issue-type catalogue | 7 |
| [BO-FIN-01a](items/FIN.md#bo-fin-01a) | Manual journals: persistence, create/list/get/withdraw | 7 |
| [BO-FIN-04](items/FIN.md#bo-fin-04) | Period close: state machine and tasks | 7 |
| [BO-MIG-10](items/MIG.md#bo-mig-10) | Reconciliation engine | 7 |
| [BO-FIN-01b](items/FIN.md#bo-fin-01b) | Manual journals: submit/decide and posting | 8 |
| [BO-FIN-06a](items/FIN.md#bo-fin-06a) | Earning engine and run | 8 |
| [BO-CLM-05](items/CLM.md#bo-clm-05) | MTPL statutory clocks wired to CMP | 8 |
| [BO-MIG-11](items/MIG.md#bo-mig-11) | Sign-off workflow | 8 |
| [BO-RAT-06](items/RAT.md#bo-rat-06) | Artefact build and lifecycle | 9 |
| [BO-CHN-01](items/CHN.md#bo-chn-01) | Customer identity link and portal authorisation | 9 |
| [BO-CHN-12](items/CHN.md#bo-chn-12) | Portal shell and routing (UI) | 10 |

## 5. Open user decisions

Builders do **not** stop on these: they build the **default** and record it as a decision in `../DECISIONS.md`. The user
can change a default at any time; the affected items are listed so they can be re-planned.

| # | Decision | Default the builders use | Items affected |
|---|---|---|---|
| 1 | Where the portals live | Same Vite app, separate route trees with their own shell: `/portal/*` (customers), `/agency/*` (brokers); no staff imports (lint rule) | BO-CHN-01..17 |
| 2 | Withdrawal declaration: anonymous or signed in | Stays reachable without sign-in, via a one-time token + rate limit | BO-CHN-12 |
| 3 | Sanctions screening provider | Keep the stub; it fails closed in Production (go-live blocker) | BO-PTY-07 |
| 4 | `pty.yaml` additions (agency admin, duplicates) | Add them as contract additions | BO-PTY-05, BO-PTY-12 |
| 5 | Second product line (e.g. Home) in scope | Motor only; no second product | BO-PFC-09 |
| 6 | Multi-vehicle / multi-driver rating input | Single vehicle, single main driver until decided | BO-RAT-10 |
| 7 | Policy-hold check as a mandatory gate on bind | Advisory only (warns, does not block) | BO-UW-09 |
| 8 | Duplicate `fallBack` operation in `pfc.yaml` | Delete the duplicate from the contract | BO-PFC-04 |
| 9 | Agency bill, commission, IFRS 17, FX, GL extract in MVP | No: post-MVP (P2) | BO-BIL-15..17, BO-FIN-12..14 |
| 10 | GL extract target system and format | Generic CSV + JSON extract | BO-FIN-13 |
| 11 | Go live without RI premium accounting; QS/surplus treaty types | RI accounting is MVP (P1); QS/surplus post-MVP (D-PRG-10: motor RI is XoL) | BO-RI-06, BO-RI-10..16 |
| 12 | New containers: ClamAV (malware scan); Gotenberg already runs locally | Add ClamAV to local compose; Azure cost noted in the item | BO-WRK-11, BO-DOC-07 |
| 13 | Complaint acknowledgement business days; redress approval threshold | 5 business days, EUR 500, both `Unverified` | BO-CMP-04, BO-CMP-05 |
| 14 | Obligations register / regulatory change in scope | Post-MVP | BO-CMP-13 |
| 15 | myDATA / e-invoicing provider, Greek XBRL taxonomy | Stub channel stays; layouts `Unverified` | BO-CMP-06, BO-CMP-11 |
| 16 | Which management reports come first | The three marts proposed in BO-DAT-03; regulatory layouts stay `Unverified` | BO-DAT-03, BO-DAT-11 |
| 17 | Identity: Entra app roles or in-app grants as the source | Entra stays authoritative; the app mirrors and audits roles | BO-PLT-05, BO-PLT-06 |
| 18 | Post and e-mail delivery vendor | Stub providers (blocked in Production) | DOC (no item yet) |
| 19 | DSAR/Import/TransactionSet routes: build or allow-list | Allow-list where the platform path already works (NX-TST-04 list) | BO-CLM-04, BO-CLM-14, BO-RI-21, BO-RI-22 |
| 20 | Billing staff get `pol.Policy.search` | Yes, read-only | BO-POL-01 |

## 6. Totals

**295 items** (P0 20, P1 170, P2 105; S 8, M 149, L 138) in waves 6–27.

| Module | Items | | Module | Items |
|---|---|---|---|---|
| WEB | 1 | | FIN | 24 |
| PTY | 20 | | DOC | 14 |
| PFC | 13 | | CMP | 14 |
| RAT | 14 | | CHN | 17 |
| UW | 16 | | WRK | 17 |
| POL | 15 | | PLT | 16 |
| BIL | 24 | | DAT | 13 |
| CLM | 18 | | MIG | 19 |
| RI | 27 | | MKT | 13 |

Contract coverage at planning time (implemented of contract operationIds): PTY 13/67, PFC 10/34, RAT 4/47, UW 6/82,
POL 22/58, BIL 27/90, CLM 35/73, RI 16/116, FIN 6/88, DOC 10/44, CMP 46/90, CHN 5/86, WRK 9/57, PLT 12/101, DAT 6/46,
MIG 22/22, MKT 10/51. Detail: `COVERAGE.md`.

**Deliberately not planned:** mobile-app-only features (D-USR-01), bancassurance (D-200(b)), the PLT workflow engine
operations (D-ARC-04), PostGIS (D-ARC-13), real-volume migration rehearsals (D-ARC-09).

## 7. Changing this plan

- New work: add a row to the right `items/<MOD>.md` with the next free number, then add it to a wave in `WAVES.md`
  that respects §1 rules. Never reuse or renumber an ID.
- Dropping work: set Status to `dropped (reason)`; do not delete the row.
- Moving an item between waves: change its **Wave** cell and the `WAVES.md` list together.
