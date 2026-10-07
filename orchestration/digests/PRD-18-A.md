# Digest PRD-18-A — Programme Requirements Baseline (XMR), lines 1–2715 (sections 1–15)

Source: `core-insurance-prds/PRD-18-programme-requirements-baseline.md` (8,957 lines total; this digest covers lines 1–2715 only: §1 document control → §15 persona & screen inventory). Sections 16–21, Annex A/B, the "ten decisions" closing section and the **freeze record** are outside this range and are digested elsewhere (PRD-18-B). Where this digest says "status", it is the status as stated inside §1–15.

Template mapping note: PRD-18 is not a module, so the module template (§1–15 of `_DIGEST-TEMPLATE.md`) is applied loosely. The caller asked for: per-section summary + full capture of findings register, event catalogue & interface register, state models, money-flow map, clock register, obligation register, SPI matrix, NFR baseline, and conflict flags. Those are the headings below.

---

## 0. Identity and status (template §1, §13)

- **ID / prefix:** PRD-18, programme prefix `XMR`. Identifier families (§1.1): `XMR-F-` findings, `XMR-D-` decisions, `XMR-FR-` programme requirements, `XMR-OQ-`/`XMR-AS-` open questions/assumptions (S5), `XMR-CR-`/`CR-S<n>-` change requests. Contract CD-02: `REQ-<MOD>-NNN` ≙ pack's `<MOD>-FR-NNN`.
- **Purpose:** cross-module reconciliation of PRD-01…17 against contract `00-system-contract.md` (v1.10, CD-01…CD-20, rulings R-01…R-100 treated as already decided), the baseline inventory and the integration review (rounds 1–3, "no blocking issues remain"). Runs prompt-18 checks **A–N** across modules, against law (where money/clocks depend on it; web spot-checked 2026-10-07) and against the motor MVP.
- **Version:** 1.1 = "programme baseline 1.0 (frozen)", dated 2026-10-07, **Frozen 1.0** — further changes via change control (§20, outside range). Owner: design authority (lead architect / orchestrator). Authors S1 (§1–5 + closing), S2 (§6–8), S3 (§9–12), S4 (§13–16), S5 (§17–21).
- **Change history (§1.2):** 1.0 = first consolidation after integration review round 3 and contract v1.10. **1.1 = Decisions D1–D10 accepted by programme sponsor; R-101 Greek/English switch added; G0 change requests applied in PRD-01…17; freeze verification fixes FZ-01…FZ-11; baseline frozen as 1.0 against contract v1.12.**
  - IMPORTANT: the body text of §2–15 still reads as pre-freeze ("Do not freeze the build baseline today", "37 of 48 majors need a decision before the freeze"). So the §4 register "status" is effectively *proposed resolution, gate G0/G1/G2*; per v1.1, the G0 items are said to be applied. Per-finding closure evidence is in the freeze record (outside this range) — **not verifiable from lines 1–2715**.
  - Contract version drift: PRD-18 body cites contract v1.10; the change history says frozen against **v1.12**. The "v1.11" mentioned in §2 recommendation is the intended carrier of the major-finding contract changes.
- **Distribution:** steering committee, design authority, module POs, architects, test lead, ROLE-29 compliance officer, ROLE-30 DPO, ROLE-46 legal reviewer, ROLE-26 tax specialist, internal audit.
- **Non-goals:** PRD-18 does not add module functionality; it adds programme requirements (XMR-FR-150 bitemporal convention, XMR-FR-200 capacity model, XMR-FR-250 E2E gate) and change requests.

## 1. Section summaries

### §1 Document control (l.7–66)
- §1.3 table of 17 PRDs (version cell, status, contract cited, words; ≈930k words total). All dated 2026-10-07. Version cells understate fixes and cited contract versions vary (v1.1…v1.10) (XMR-F-409); PRDs identified by file + date.
- §1.4 other inputs: contract v1.10 binding (R-99 precedes R-100; §3.9.12/§3.9.13 out of order → XMR-CR-CON-02); baseline inventory 218 owned screen items / 2,184 field rows (100% covered); integration review closed after round 3 (124 findings, 4 Blocking all resolved; R3-001 fixed; residual minors R3-002…R3-010); `_work/digests/digest_CMP.md` §1–9 had been a copy of the DAT digest (input defect, corrected); parts S2–S5 raised 134 findings → 136 after merge.

### §2 Executive summary (l.68–111)
- Programme size: **4,697 functional REQs (3,785 Must, of which 3,710 in P1 = Greek private motor MVP), 768 BR, 370 NFR, 324 screens, 302 events, 119 AI features.**
- Readiness: structurally complete (all 16 contract sections, ≥187 FRs per PRD vs CD-19 ≥120, G/W/T on all 3,870 Must rows incl. RI target-phase), but not consistent. **0 ready, 14 ready-with-conditions, 3 not ready (BIL, FIN, MIG).**
- Findings: 0 Blocker, **48 Major (40 need DA decision)**, **88 Minor (17 DA)**, total **136 (57 DA)**. By check (major/minor): A 1/9 · B 2/5 · C 4/9 · D 2/9 · E 1/4 · F 7/2 · G 5/6 · H 0/8 · I 4/7 · J 6/7 · K 0/7 · L 10/7 · M 2/3 · N 4/5. 37 of 48 majors need a decision/opinion/contract change before freeze (G0).
- Ten most serious problems: (1) money with no executor/posting rule (F-214, -108, -216); (2) Greek tax rules in four places (F-210…-213, -401); (3) fiscal double numbering & open myDATA questions (F-101, -416, -413); (4) event contracts unsafe for money (F-114, -200, -132, -118); (5) written premium four ways (F-122); (6) statutory clocks (F-230…-234, -412); (7) capacity/recovery contradictions (F-320…-324); (8) unstable priorities/MVP cut (F-360, -362…-367, -370, -374, -004); (9) Greece pack unsettled & multi-market leakage (F-307, -300…-302); (10) governance gaps (F-329, -405, -406, -107, -116, -127, -414).
- Ten steering decisions = contract D1–D10 (accepted, contract v1.11 §3.10.10): D1 one cash executor (D-255, -102, -156); D2 one `TaxCalculator` treatment rule + one tax opinion (D-152…-154, -257); D3 fiscal design (D-100, -258, -264); D4 v1 event contract (D-107, -104, -151, -110); D5 definitions of record (D-106, -105, -108, -109, -150, -159); D6 motor MVP scope (D-212, -200, -203, -273, -286); D7 clock register + legal-values go-live gate (D-158, -157, -262, -213); D8 capacity model + T1 recovery (D-207, -208); D9 build order + E2E gate (D-201, -202, -214, -250); D10 technology foundation + in-house XBRL (D-251, -253, -254, -101).
- Recommendation (pre-freeze text): freeze as baseline 1.0 once the ten decisions are recorded and CRs for the 48 majors applied to PRDs and contract (v1.11). Unverified Greek legal values may be frozen as pack data with **guarded** test expectations; legal sign-off becomes a **go-live gate**, not a freeze gate. Waves W1 and W2 (platform, configuration, masters) can start now.

### §3 PRD completeness table (l.115–202)
Method: structure vs contract §3.11 (16 sections) and pack's 23 sections; counts via script parse (`_work/stats.json`); RI uses MVP-phase MoSCoW column (target-phase in brackets); MIG uses scenario-B column. G/W/T check is syntactic only. Programme OI rows: 257, of which 200 open, merged into 52 programme questions XMR-OQ-001…052. Ratings: Ready / Ready with conditions / Not ready.

§3.2: all 17 PRDs have the 16 sections. Pack §3 Glossary not mapped (contract keeps glossary centrally §3.3); 9 PRDs have §1.7 (POL, BIL, CLM, RI, FIN, DOC, CMP, CHN, DAT), 8 have none (PTY, PFC, RAT, UW, WRK, PLT, MIG, MKT). §11 (AI) and §12 (controls) are contract additions, present in all. Smallest PRD MIG 187 FRs. 0 undefined citations.

§3.3 per-module table (full):

| PRD | Mod | REQ | Must/Should/Could/Won't | BASELINE/ENH | BR | NFR | SCR | Must P1 | Must G/W/T | UNVERIFIED | OI (open) | Readiness |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 01 | PTY | 265 | 219/35/11/0 | 228/37 | 46 | 20 | 23 | 218 | 219 | 17 | 12 (12) | With conditions |
| 02 | PFC | 231 | 200/29/2/0 | 133/98 | 52 | 21 | 20 | 200 | 200 | 23 (10) | 18 (13) | With conditions |
| 03 | RAT | 255 | 218/22/3/12 | 196/59 | 38 | 23 | 15 | 214 | 218 | 10 | 15 (13) | With conditions |
| 04 | UW | 267 | 205/54/8/0 | 214/53 | 42 | 20 | 20 | 183 | 205 | 21 | 15 (12) | With conditions |
| 05 | POL | 339 | 295/35/6/3 | 304/35 | 48 | 22 | 21 | 293 | 295 | 22 | 15 (13) | With conditions |
| 06 | BIL | 336 | 294/34/8/0 | 298/38 | 68 | 31 | 17 | 294 | 294 | 18 (12) | 14 (12) | **Not ready** |
| 07 | CLM | 244 | 193/43/8/0 | 210/34 | 41 | 20 | 20 | 193 | 193 | 13 | 12 (9) | With conditions |
| 08 | RI | 237 | 91/23/1/122 (target 176/56/5/0) | 221/16 | 42 | 16 | 20 | 91 | 176 (target) | 23 | 14 (13) | With conditions |
| 09 | FIN | 280 | 245/30/5/0 | 264/16 | 56 | 22 | 14 | 242 | 245 | 32 (13) | 15 (13) | **Not ready** |
| 10 | DOC | 302 | 252/41/9/0 | 231/71 | 38 | 18 | 17 | 250 | 252 | 25 (12) | 19 (13) | With conditions |
| 11 | CMP | 235 | 196/35/4/0 | 205/30 | 49 | 24 | 11 | 183 | 196 | 71 (20) | 19 (13) | With conditions |
| 12 | CHN | 281 | 214/61/6/0 | 251/30 | 51 | 18 | 32 | 212 | 214 | 12 (8) | 15 (12) | With conditions |
| 13 | WRK | 310 | 231/63/16/0 | 191/119 | 46 | 16 | 26 | 231 | 231 | 8 | 11 (9) | With conditions |
| 14 | PLT | 339 | 285/47/7/0 | 216/123 | 42 | 33 | 28 | 283 | 285 | 21 | 15 (15) | With conditions |
| 15 | DAT | 279 | 223/48/8/0 | 245/34 | 30 | 19 | 18 | 212 | 223 | 23 | 15 (10) | With conditions |
| 16 | MIG | 187 | 167/19/1/0 (scen. A 12/44/36/95) | 181/6 | 37 | 24 | 10 | 167 | 167 | 24 (7) | 15 (11) | **Not ready** |
| 17 | MKT | 310 | 257/46/7/0 | 124/186 | 42 | 23 | 12 | 244 | 257 | 41 | 18 (13) | With conditions |
| **Tot** | | **4,697** | **3,785/665/110/137** | **3,712/985** | **768** | **370** | **324** | **3,710** | **3,870** | **404** | **257 (200)** | 0/14/3 |

Notable self-named thin parts: MKT Greek clock values and tax rates are placeholders by design; DOC gov.gr protocols UNVERIFIED; CMP SII deadlines UNVERIFIED; FIN IFRS 17 GMM (P3) thin; MIG retention durations UNVERIFIED. POL 339 includes `REQ-POL-354` (added after round 3). 3,870 = 3,785 with RI's 176 target-phase Musts replacing its 91 phase Musts.

§3.4 rating reasons: **BIL not ready** — three P1 money flows without executor (FS clearing, redress, tax remittance), own fees untaxed, invoice numbering collides with fiscal numbering, rejected/stopped disbursements silent. **FIN not ready** — posting rules missing for FS clearing, redress, tax remittance and three RI money events; written-premium basis, IPT treatment, levy reading open. **MIG not ready** — 155 of 167 Musts depend on scenario A/B; rollback has no owner reversal operations. The other 14 need targeted changes listed per finding in §4.

### §4 Findings register (l.205–395) — method
Built from S2 (XMR-F-100…132), S3 (-200…257), S4 (-300…375), S5 (-400…421), integration-review residuals, and S1's own (-001…004). Merged rows keep lowest ID ("= XMR-F-nnn"); merged IDs remain citable. Severity: **Blocker** (cannot freeze whatever is decided), **Major** (must be fixed/decided before affected P1 capability built/tested), **Minor** (cleanup via §20 CRs). Sort: severity → check letter → ID. Counts table (§4.3) as in §2; by source S2 33, S3 30, S4 47, S5 17, IR residual 5, S1 4.

Merge map (§4.2): F-100 ⟵ F-421; F-106 ⟵ F-344; F-108 ⟵ F-215; F-113 ⟵ R3-010; F-117 ⟵ R3-005; F-119 ⟵ R3-003(c); F-213 ⟵ F-415; F-214 ⟵ F-400; F-329 ⟵ F-404; F-370 ⟵ F-417; F-409 ⟵ IR §5 doc-control note; F-411 ⟵ R3-004. IR residuals kept as own minor rows: R3-002, R3-003(b), R3-007, R3-008, R3-009. Not carried (fixed): R3-001, R3-003(a), R3-006, XRF-021 (PRD-06 §9.1 now offers dry-run on `stop`, `void`, `release`, `approveRelease`, write-off `decide`/`post`, refund `decide`).

**Status convention used below:** every row is "Open — proposed resolution, gate Gx" as written in §4/§5. Per §1.2 v1.1, all **G0** changes were "applied in PRD-01…17" and D1–D10 accepted, so G0 items are *nominally resolved at freeze*; G1/G2 items remain open by design. Verify against the freeze record (PRD-18-B).

#### 4.A Major findings (48) — full

| ID (merged) | Chk | Description (condensed) | Resolution | Owner | DA decision | Gate (§5.1) |
|---|---|---|---|---|---|---|
| XMR-F-101 | A | BIL (`REQ-BIL-086`) and CMP (`REQ-CMP-038`) both assign gap-free fiscal-series numbers to what can be one legal document; printed no. vs myDATA no. may differ | CMP sole issuer of fiscal series/numbers; BIL invoice no. = non-fiscal payment-demand no.; DOC prints CMP series, number, MARK (held until registration) | DA; BIL, CMP | D-100 | G0 |
| XMR-F-107 | B | No module owns XBRL/xBRL-CSV rendering & EIOPA taxonomy validation for SII QRTs and BoG returns; SCR figures have no path back; CD-20 forbids buying a tool without decision | CMP in-house renderer+validator under `REQ-CMP-008` consuming DAT data-point package; DAT ingests risk function's SCR results as signed-off mart input; two taxonomies in parallel in H1 2027 | DA; CMP, DAT | D-101 (≈D-281) | G0 owner; G1 build |
| XMR-F-108 (=F-215) | B | Complaint redress requested from BIL disbursement, but BIL accepts only BIL/CLM/RI sources, no approval-evidence/ledger rule for CMP; FIN no redress rule; source enum omits RI though `REQ-BIL-348` accepts RI | Route by type: premium-related → BIL credit/refund; claim-related → CLM ex-gratia; else new BIL source `CMP_REDRESS` with CMP approved outcome as evidence, billing-ledger rule, FIN redress-expense rule; add RI to enum | BIL, CMP, FIN, CLM | D-102 (=D-156 redress part) | G0 |
| XMR-F-114 | C | No set-size in `ChargeDeltaEmitted`, `TransactionReversed`, `TransactionReapplied` → consumers can split invoices/fiscal docs | Add `transaction_delta_count`, `transaction_delta_index` to `ChargeDeltaEmitted`; `aggregate_member_count`, `aggregate_member_index` to all three (additive in v1); incomplete sets older than threshold → exception activity | POL; BIL, RI, FIN, DAT, CMP | D-107 | G0 (v1 schema) |
| XMR-F-116 | C | Contract promises one correlation id quote→journal line (W3C trace id) but batch work starts new traces (`REQ-PLT-147`); `REQ-PLT-132` cannot reconstruct; POL/RI "correlation key/id" naming clash | `correlation_id` = trace id (technical only). Journey via business lineage keys: job → transaction → charge id → invoice item → allocation → billing entry → journal line. Reword `REQ-PLT-132`, G3, contract §3.2.3/§3.5.6; RI renames to `source_correlation_key` | DA; PLT | D-105 | G0 |
| XMR-F-118 | C | CMP publishes `AiSystemStatusChanged` for PLT to disable feature; PLT has no handler → suspended AI keeps running | PLT consumes it, toggles off **within 60 s** for Suspended/Retired, publishes `AiToggleChanged`; re-checks on gateway calls when cache stale; contract §3.8.2 adds register suspension as disable trigger | PLT; CMP | D-110 | G0 contract; G1 build (before any AI feature enabled) |
| XMR-F-132 | C | RI `DepositPremiumDue` (`REQ-RI-154`, Should P1), `PremiumAdjustmentCalculated` (`-156`), `CommissionAdjusted` (`-161`) have no consumer; FIN `REQ-FIN-165` is Should P3 | FIN adds §8.2 handlers & posting rules (ceded XoL premium accrual, adjustment, RI commission adj.) or RI routes via `StatementIssued`; `REQ-FIN-165` to P1 for deposit premium | FIN, RI | No | G1 (before W7) |
| XMR-F-122 (see F-217) | D | Written premium defined 4 ways (contract at bind net of reversals; POL all charge deltas incl. tax/levy/fee; DAT premium deltas by accounting date excl. taxes/fees; FIN whatever BIL entry carries) | **One definition: charge deltas of premium, surcharge and discount categories, net of reversals, excluding tax, levy and fee; time basis booking date; period assigned by FIN.** POL's all-category total renamed "written charges" | DA; chief actuary; POL, FIN, DAT | D-106 | G0 |
| XMR-F-127 | D | Greek translations: "τιμολόγιο" is a fiscal document; "καταγγελία" used for complaint and termination | Invoice = **ειδοποίηση πληρωμής** only; complaint = **παράπονο** only; termination by notice = **καταγγελία σύμβασης**; ROLE-46 confirms before any DOC template approved | ROLE-46; DOC | D-109 | G0 decision; G1 templates |
| XMR-F-200 | E | BIL disbursement Rejected (approver or bank pain.002) or Stopped emits no event; `DisbursementCleared` doesn't list CLM → claim payment stuck "Submitted"; RI settlements same | BIL publishes `DisbursementRejected`, `DisbursementStopped` (disbursement id, source type & id, reason code); CLM listed on `DisbursementCleared`; CLM & RI consume all three; CLM maps Rejected → Returned path | BIL, CLM | D-151 | G0 (v1 schema) |
| XMR-F-210 (see -218, -401) | F | Tax refundability defined outside `TaxCalculator` in 4 modules under 3 keys (PFC `gr.ipt.refund_on_cancel`, POL, BIL, FIN `tax.ipt.*`) → over-refund + recon break; `REQ-FIN-182` Greek rule written as core | New MKT-owned `TaxCalculator.treatment(chargeType, transactionKind, cancellationSource)`; RAT applies; POL emits RAT output; BIL & FIN validate; PFC drops key; `REQ-FIN-182` "per pack rule" | ROLE-26; MKT | D-152 | G0 |
| XMR-F-211 | F | BIL-originated instalment/dishonour/late fees never pass RAT; BIL calls `TaxCalculator` only for receipt stamps; IPT base covers "rights of every kind" (Law 5177/2025 Art. 43, verified) | Originating module calls `TaxCalculator` once per charge: RAT for POL charges, BIL for own charges using PFC tax-base flag; extend contract §3.5.8 callers; tax adviser confirms whether instalment fees are in IPT base | BIL; ROLE-26 | D-152 | G0 decision; G1 build W5 |
| XMR-F-212 | F | Greece default `tax.ipt.liability_point` = WRITTEN (`REQ-MKT-328`) but statute taxes "απαιτητά ασφάλιστρα" (premiums due) → over-declaration on instalments | Tax opinion before freeze, close OI-FIN-03; **unless opinion supports WRITTEN, default = DUE**; BIL & FIN support both | ROLE-26; finance controller | D-153 (+D-257 item) | G0 |
| XMR-F-213 (=F-415) | F | Auxiliary Fund levy: three readings of 70/30 split (over whole 6%, over 4.5%, or 1.5% only); stamp duty on policyholder share, base on MTA, refund on cancel open; MKT hard-codes 4.2%/1.8%. 6% ceiling + 70/30 split confirmed; components not | One legal opinion (split, stamp duty, base, cancellation) recorded once in `REQ-MKT-322`; PFC/BIL/FIN/POL cite; keys-only with `guarded` golden expectations (`REQ-MKT-055`) until then | ROLE-46 + ROLE-26 | D-154 (=D-256) | G0 structure; G2 values |
| XMR-F-214 (=F-400) | F | FS payables "settled through the clearing statement", CLM "posts" monthly net; nobody moves cash; BIL has no FS source; `clm.payment.methods` lacks CLEARING; FIN GL-2510 expects BIL disbursement → break after 2 days; E2E-06 fails | BIL source **`FS_CLEARING`**: CLM hands monthly net payable (disbursement) or net receivable (`bil.Receivable.register`) per counterparty & statement; FIN posts cash from BIL, nets clearing per statement, excludes method Clearing from `BR-FIN-096`; CLM adds `CLEARING` method (S3's `CLM_FS_SETTLEMENT` withdrawn) | Lead architect; CLM, BIL, FIN | D-255 (=D-155) | G0 |
| XMR-F-216 | F | Quarterly IPT payment to AADE and stamp duty on levy have no cash executor | BIL source **`TAX_REMITTANCE`** triggered by approved FIN `TaxReturn`, under clock `FIN_IPT_RETURN`, four-eyes; FIN links return to payment (alt: treasury outside core + BIL recon rule) | Finance controller; ROLE-26 | D-156 (tax part) | G0 |
| XMR-F-401 (see -210) | F | Law 5317/2026 Art. 72: distance-withdrawal consumer pays nothing → refund incl. IPT & levy; `REQ-FIN-182` & BIL INV-05 forbid reducing IPT payable by cancellation credit; undefined | Tax opinion: void ab initio (IPT reversed via credit fiscal doc) vs cancellation (insurer bears tax); net-of-tax refund excluded; make rules source-aware via `TaxCalculator.treatment` (source `DistanceWithdrawal`) | ROLE-26 | D-257 | G0 switch; G2 opinion |
| XMR-F-230 (see F-002) | G | `CLM_MTPL_PAYMENT_DUE` starts at offer acceptance; BoG Act 87/2016 Art. 6 counts 10 days "from the offer" | Start at **proven delivery of the reasoned offer** (`StatutoryOfferIssued` + DOC proof); stop at `PaymentIssued`; legal reviewer confirms late-acceptance reading | CLM; Greek reg. analyst | D-158 | G0 decision; G1 |
| XMR-F-231 (see -111) | G | Five statutory periods outside the clock register: repair in kind ≤20 days; 15-day termination effect after disclosure notice; one-month deemed termination of unaccepted amendment; MTPL third-party notice 16 days (UNVERIFIED); 12-month+14-day distance-withdrawal long stop | Register `CLM_REPAIR_IN_KIND`, `POL_INSURER_TERMINATION_NOTICE`, `UW_AMENDMENT_ACCEPTANCE`, and (if confirmed) `POL_MTPL_THIRDPARTY_NOTICE`; long-stop variant on row 3; MKT supplies values | Compliance officer; CLM, UW, POL | D-158 | G1 |
| XMR-F-232 | G | 72-h GDPR breach timed by PLT incident service and CMP engine | CMP owns `CMP_GDPR_BREACH_NOTIFY`; PLT starts/stops via `REQ-CMP-003` when incident flagged as personal-data breach; PLT drops own timer; DORA timers stay in PLT (R-61) | ROLE-30; platform eng. | D-157 | G1 |
| XMR-F-233 (see -412) | G | `POL_RENEWAL_NOTICE` starts at "renewal offered" (can't detect late offer); `POL_NONRENEWAL_NOTICE` measured from decision not back from expiry | Both become **FIXED_DATE**: deadline = expiry − pack lead; start when renewal job created / non-renewal decided; Met on `DocumentDelivered` with statutory proof; breach consequence from pack | Compliance; POL | No | G1 |
| XMR-F-234 | G | Payee cooling-off: BIL 30 days vs CLM 72 hours on same `PaymentInstrument` (R-38) | One BIL key `bil.payee.cooling_off` per purpose, evaluated only by BIL at release; CLM reads `cooling_off_until` | BIL; claims mgr | No | G1 |
| XMR-F-300 | I | Greek search/casing (tonos/diaeresis removal, final-sigma, reverse ELOT digraphs, Greeklish, `el-Upper`) written as core in PTY, WRK, DOC | Core = Unicode-generic normalisation; language folding/casing = MKT language rules; transliteration only via `NameTransliterator.searchVariants` | MKT ROLE-42 + PTY, WRK, DOC | D-205 | G0 (W2 builds search) |
| XMR-F-301 | I | Runtime plate normalisation "through the pack normaliser" — no SPI defines it; only migration-only `profilePlate` | `IdValidator` scheme `VEHICLE_PLATE` with `normalise` and `searchKey` (pure, pack-bound); `profilePlate` reuses | MKT | D-204 | G1 |
| XMR-F-302 (see -416) | I | Must P1 routing via `EInvoiceProvider` but SPI is Should P3 (`REQ-MKT-089`); Greek B2B e-invoicing applicability to insurance Uncertain | `REQ-MKT-089` Must P1 interface, core default `NotRequired`; `REQ-CMP-062`, `-063` Must conditional on `cap.cmp.einvoice_b2b`; applicability recorded before go-live | ROLE-29 + ROLE-26; MKT | D-206 | G1 |
| XMR-F-307 | I | 22 of 32 Greece pack rows Verify/UNVERIFIED/Uncertain; 17 on motor MVP path (myDATA, Information Centre, clock values, FS, motor compensation bodies, notice delivery proof, payment codes, DORA channel) | **Pack certification gate**: no motor-path binding activated in production unless Settled; each row gets owner + closing date | ROLE-42 + owners | Steering gate D-213 (=D-261) | G0 rule; G2 values |
| XMR-F-320 | J | CHN 50 quotes/s + 200 aggregator dry-runs/s (≤25% rated) → ≤100 rated quotes/s ≈ 400 ratings/s; RAT designed 300/s, UW 100/s, PLT headroom 100 quotes/s w/o 2× margin | XMR-FR-200: **RAT ≥ 400 ratings/s with 1.5× headroom; UW ≥ 200 evaluations/s** (alt: CHN caps aggregator share) | DA; RAT, UW, CHN | D-207 | G0 |
| XMR-F-321 | J | PLT sizes event platform 50M/yr, 1,000/s; DAT ~60M; FIN alone ≥1,000/s; FIN assumes ~3M claim fin events/yr vs CLM 0.9M | **~60M events/yr, designed for 180M, ≥ 2,000/s sustained**; FIN & CLM align claim event volume | ROLE-38; DAT, FIN, CLM | No (D-207 in §5.1) | G0 |
| XMR-F-322 | J | Renewal batch: POL 30,000/h (150k in 5 h); PLT 150k ≤4 h; UW 40k/night ≤60 min; RAT 100k in 30 min; DOC 300k docs in 4 h | **150,000 terms end-to-end ≤ 4 h (POL ≥ 40,000/h); nightly 40,000 in ≤ 1 h per stage** | DA; POL, UW | No (D-207 in §5.1) | G0 |
| XMR-F-323 | J | Cat surge: CLM 1,000 FNOL/h for 24 h (~32,000 claims); RI 3,000 claims in 72 h; WRK 20,000 activities/h for 72 h | One catastrophe design event in XMR-FR-200; RI, FIN, WRK, DOC size to it; close OI-CLM-08 | ROLE-18; RI, WRK | No (D-207) | G1 |
| XMR-F-324 | J | DOC T1 path (pre-contractual pack, cover note, archive write, `evidenceFor`) RTO 4 h vs T1 ≤ 2 h; withdrawal ack (`REQ-DOC-044`) missing from T1 list | `NFR-DOC-008` RTO ≤ 2 h for T1 path; add `REQ-DOC-044` to `NFR-DOC-007` | DOC | No (D-208 records) | G0 |
| XMR-F-329 (=F-404) | J | No programme retention schedule; durations open in 15 PRDs; contradictory placeholders (20 yrs after policy end & 5 yrs declined apps — "not found in Greek statute"; 5/3/10 yrs CMP; 10-yr claim file MIG) | One programme retention schedule (DPO + legal), loaded as Greece-pack data under `REQ-PLT-011` before system test; each `RC-*` gets duration + legal source; crypto-shredding for "lakehouse" and audit | ROLE-30 + ROLE-29, ROLE-46 | D-259 | G1 (before system test) |
| XMR-F-004 | L | RI A-01 assumes motor XoL-only; says proportional engine "ready in P1 regardless" but `REQ-RI-002`, `-076`, `-084` are Won't P1; Fairfax group programme unseen (OI-RI-05) | Confirm A-01 from group programme docs; if confirmed reword to "built in P2"; else promote `REQ-RI-002`, `-075`…`-088` to Must P1 and fix NET delta mode | ROLE-27 + ROLE-28, ROLE-35 | D-273 | G0 |
| XMR-F-360 | L | Dependencies column mixes requires/used-by/family; 781 of 4,890 Must→Must links point to later wave; 249 of 314 capability groups form one cycle; 21 ranges span unallocated IDs | Split column into "Requires" and "Used by" (anchors add "Family") in contract §3.11 | DA; all owners | D-201 | G0 rule; G1 migration |
| XMR-F-362 | L | Period close (`REQ-FIN-007`, `-232`) depends on closed-period guard G5 `REQ-POL-108` (Should) | Promote `REQ-POL-108` Must P1 | POL; fin. controller | D-203 | G0 |
| XMR-F-363 | L | IDD demands-and-needs `REQ-CHN-086` (Must) needs `REQ-PFC-112` (Should) | Promote `REQ-PFC-112` Must P1 | PFC | D-203 | G0 |
| XMR-F-364 | L | Cat event mgmt (`REQ-CLM-006`, `-210`) needs peril tags `REQ-PFC-094` (Should) | Promote `REQ-PFC-094` Must P1 | PFC | D-203 | G0 |
| XMR-F-365 | L | Vendor panels (`REQ-CLM-188`, `-189`) need vendor master `REQ-PTY-096` (Should) | Promote `REQ-PTY-096` Must P1 | PTY | D-203 | G0 |
| XMR-F-366 | L | SII taxonomy versioning `REQ-FIN-154` needs `REQ-PFC-145` (Should); taxonomy 2.10.0 from Q1 2027 | Promote `REQ-PFC-145` Must P1 | PFC | D-203 | G0 |
| XMR-F-367 (see -252) | L | POG monitoring/distributor pack `REQ-PFC-007`, `-156`, `-157` Should; IDD POG (Del. Reg. 2017/2358 Arts 7–8, article nos UNVERIFIED) | Promote `REQ-PFC-156`, `-157` Must P1 | ROLE-14; ROLE-29 | D-203 | G0 |
| XMR-F-370 (=F-417) | L | Bancassurance half in MVP (bank-staff federation `REQ-PLT-057`, attribution Must; bank journeys `REQ-CHN-006`, producer code `REQ-PTY-258`, co-branded sign-in `REQ-PLT-074` Should); partner date unconfirmed | Out of P1 unless bank partner contracted by G0; demote bank-specific Musts to Should P1 fast-follow; keep identity design | Steering (ROLE-14, ROLE-09) | D-200 (=D-271 bank) | G0 |
| XMR-F-374 | L | 155 of 167 MIG Musts depend on scenario B (convert at renewal); under A only 12 Must; ~6% of MVP cut | Board decides A or B by fixed date before W5; **plan on B** | Steering | D-212 (=D-268) | G0 (planning basis) |
| XMR-F-405 (see -001) | M | Nobody owns cross-module E2E suite | XMR-FR-250 + suite E2E-01…12, owned by named programme test lead, release gate on PLT pipeline (`REQ-PLT-265`) | DA | D-250 | G0 |
| XMR-F-406 | M | Owners haven't named reversal op for `origin=MIGRATION` objects (E2E-05 step 10) | Each owner adds named reversal requirement before mock 2 under `REQ-POL-013`, `REQ-BIL-012`, `REQ-CLM-010`, `REQ-RI-007`, `REQ-FIN-010`, `REQ-PTY-013`; MIG lists under `REQ-MIG-005` | ROLE-41 + owners | D-260 | G1 (before mock 2) |
| XMR-F-412 (see -233) | N | No renewal acceptance mode / notice period decided; MIG fixes offers at T-45, extracts at T-90 | Decide acceptance mode (**recommended: payment as acceptance, explicit acceptance available**) and one shared value for `POL_RENEWAL_NOTICE` and `mig.renewal.offer_lead_days` | ROLE-14 + ROLE-46 | D-262 | G0 |
| XMR-F-413 | N | Cover note non-fiscal & without amounts; legal permissibility open (POL risk R-05) | Tax opinion before G0; keep non-fiscal unless negative | ROLE-26 + ROLE-29 | D-264 | G0 |
| XMR-F-414 | N | Information Centre transport, format, deadline unpublished | Interim **manual file** with daily lag monitoring and sev-2 incident at PRD-11 threshold; transport stays a pack component | ROLE-32 | D-265 | G2 |
| XMR-F-416 (see -101, -302) | N | Nine myDATA questions open across 7 PRDs incl. fiscal trigger point; `cmp.fiscal.mark_before_issue` and `bil.fiscal.mark_before_delivery` lack shared certification rule | **TRANSACTION trigger for motor; premium receipts MARK-before-delivery**; one certification rule over both keys (OI-CMP-13); myDATA doc types confirmed by tax adviser | ROLE-26 + CMP lead | D-258 | G0 |

Gate totals (§5.1): G0 37 (nine have later G1/G2 step); G1 only 10; G2 only 1 (F-414).

**Decision status cross-check (from `00-system-contract.md` v1.12 §3.10.10, outside PRD-18 range but binding):** D1–D10 accepted "as recommended" on 2026-10-07, which closes the *decision* for F-101, -107, -108, -114, -116, -118, -122, -127, -200, -210…-216, -230…-234, -301, -302, -307, -320…-324, -360, -362…-367, -370, -374, -004, -405, -412, -413, -416 (and minors F-115, -239, -253, -254, -342, -361). Still open by design: tax/legal opinion values (F-212, -213, -401, -413 — interim pack defaults with guarded tests; sign-off is a go-live gate), RI A-01 confirmation (F-004, before G0), migration scenario board confirmation before W5 (F-374), retention schedule (F-329, G1), migration reversal ops (F-406, before mock 2), Information Centre transport (F-414, G2), Greece-pack Verify/UNVERIFIED values (F-307, G2). **Note D4 renames the F-114 completeness fields to `set_id`, `set_size`, `index`** (see conflicts C.b).

#### 4.B Minor findings (88 rows) — condensed

| ID | Chk | Gist | Resolution | DA |
|---|---|---|---|---|
| F-100 (=F-421) | A | Module-local IDs (risk `R-nn`, `C-PTY-nn`, `CAP-PFC-nn`, `WRK-Cnn`, `MKT-Cnn`, `GS-01…50` vs `GS-SII-01`, MIG `D-01`) collide in form with programme IDs; capability maps miss ranges (`REQ-BIL-343…353`, `REQ-RI-259`, `REQ-WRK-397…404`, `REQ-UW-300`) | Prefix: `RK-<MOD>-nn`, `CAP-<MOD>-nn`, `GS-MKT-nn`, `DEC-MIG-nn`; extend §1.6 maps (XMR-CR-ALL-03) | No |
| F-102 | A | Config resolution exposed twice: `mkt.Configuration.resolve/explain` and `plt.Config.resolve/trace/reconstruct` | One public op set named by MKT, implemented by PLT runtime; `plt.Config.*` internal | No |
| F-103 | A | CMP and DAT both "build" A.1004 return | DAT builds via `StatutoryDataReturnFormat`; CMP records/validates/approves/submits; same for EAEE stats | No |
| F-104 | A,B | Accumulation band thresholds in UW and RI | UW owns one key per peril & zone scheme; RI reads; drop `BR-RI-031` param | No |
| F-105 | A,C | Motor offer clock warning reaches staff by 3 paths | One path: WRK trigger table on CMP clock events; CLM's `StatutoryOfferDue`/`Breached` stay claim facts for CHN/DAT | No |
| F-106 (=F-344) | A,C,K | BIL–FIN daily control-total exchange both push and pull; `bil.Reconciliation.fin` undefined in BIL §9.1 | BIL pushes to `fin.Reconciliation.exchange` after daily close; `bil.Reconciliation.fin` = read for break investigation | No |
| F-409 | A,N | Doc-control version cells inconsistent; contract versions mixed; R-01…R-99 vs R-100 | One version, contract v1.10 R-01…R-100, ordered change log (XMR-CR-ALL-01) | D-288 |
| F-410 | A,N | Self-check counts disagree (UW 265 vs 267; MKT 304 vs 310, SPI counts 23/38/40, GS-45 vs 47; DOC 6 vs 5 closed; CMP 13/12 modules vs 15 rows) | Recompute (CR-UW-01, MKT-03/-04, DOC-01, CMP-03) | No |
| R3-003(b) | A | PLT lists `RC-MKT-CFG`; MKT uses `RC-CFG` | Delete row in PRD-14 (CR-PLT-01) | No |
| F-109 | B | RAT telematics (`REQ-RAT-123`, Could P2) "supplied by POL" — no POL req | Before P2: POL adds via pack provider under `REQ-POL-010`, or RAT drops | No |
| F-110 | B | Commercial premium audit has no POL job (`REQ-BIL-064` Could P3) | Before P3: POL premium-audit job; PFC audit schedules | No |
| F-111 (see -231) | B | Effect of cancellation on third-party MTPL cover & notification assigned jointly POL/CMP; UNVERIFIED | POL owns via `PolicyLifecycleRules`; CMP reports insured-vehicle fact; verify P.D. 237/1986; close OI-BIL-02 | No |
| F-112 | B | DAT reads every module DB via log-based CDC (read-only, recon only, 35 days, P2/P3 hashed) — couples to physical schemas | Named exception: only tables declared in producer data contract; producer owns change notice; CDC never feeds silver/gold | D-103 |
| F-113 (+R3-010) | B | Outbox/idempotency/dedupe/audit tables in module schemas with no contract rule; `IdempotencyRecord` (RAT, POL) & `InvariantResult` (BIL, FIN) defined twice | Codify: PLT library tables in each module schema, written only in module's txn, read only by PLT relay; mark/prefix pairs | D-103 |
| F-001 | C,M | XMR-FR-250 requires one correlation id end-to-end — impossible (batch new traces) | Reword: steps linked by D-105 lineage keys; each command's trace complete; PLT journey view `REQ-PLT-132` is assertion tool | within D-105 |
| F-115 | C,L | POL lets entity choose GROSS delta mode P1 (`REQ-POL-121`), RI parity only P2 (`REQ-RI-084` Won't P1); key `pol.billing.delta_mode` misnamed | Key `pol.charges.delta_mode` = **NET for P1 and P2**; GROSS only after `REQ-RI-084`; amends D-254 | D-104 |
| F-117 (+R3-005) | C | Partition keys not stable aggregate id (FIN month-end hot partition; `ConfigChanged`; merge/unmerge) | FIN keys by journal/period/break/return/run id; `ConfigChanged` by resolution-context id; levies by levy-period id; merge events by survivor/merge id; amend R-39 "claim_id for claim-scoped events" | No |
| F-119 (+R3-003(c)) | C | Producers omit consumers with handlers (PFC on `UWRuleSetActivated`; POL, WRK on FIN `Ifrs17GroupAssigned`/`PeriodReopened`; RI, UW on DAT `AccumulationSnapshotPublished`) | Producers add consumers | No |
| F-120 | C | Calls cited without operation name (contract §3.5.7) | Name ops: `clm.Financials.dailyTotals`/`.get`, `bil.Invoice.get`, `bil.Refund.decide`, `clm.TransactionSet.approve`; POL handles `ChargesScheduled` or BIL drops statement | No |
| F-121 | C,D | Event names global but read as another module's fact (WRK vs DOC `Document*`; RI `StatementIssued` etc.; CLM `PaymentIssued`) | No rename at v1; topic-qualified names in registry; v2 unambiguous | within D-108 |
| F-411 (=R3-004) | C | `REQ-CLM-155` cites unused PRD-17 §9.4.40 | → §9.4.23 extended in §9.4.43 | No |
| R3-002 | C | 119 producer-listed consumers lack §8.2 handler (86 plain + 33 CMP "intended") | Trim or add handlers: BIL, FIN for `FiscalDocCancelled`; POL for `BureauFactRejected`; PLT for `AiSystemStatusChanged` | No (D-108 rule) |
| R3-007 | C | CHN cites routing read as `REQ-MIG-004`/R-89 not `mig.Routing.resolve`/`REQ-MIG-149` (R-96) | CR-CHN-01 | No |
| F-123 | D | "Account" names five things; PTY household narrower than contract | Qualified forms; contract household replaced by PTY's | No |
| F-124 | D | "Transaction" five concepts, no glossary entry | Entries; CHN "channel transaction types" | No |
| F-125 | D | Recovery/Exposure/Reserve second meanings | Reinsurance recovery, risk exposure, unearned premium reserve | No |
| F-126 | D | Further homonyms (segment, treaty, referral, delegation, renewal conversion, submissions, statements, groups, retention) | Qualified forms §6.3 | No |
| F-128 | D | Greek inconsistencies (εργασία, αναστολή, έκδοση, απόδοση; καταστατική, εκκίνηση προθεσμίας) | Apply §6.4 via MKT translation store & DOC clause library; ROLE-46 | D-109 |
| F-129 | D,G | Undefined terms (`booking_date`, accounting/business date, rating basis, tax point, notification date); money calcs must name time basis | Definitions §6.5/§6.7 | No |
| F-130 | D | Unresolved identifier issuers (Green Card no., statutory cover-note no., A.1004 reg. no., first core term no. after migration, BIL charge ids) | Green Card: POL via pack series fed by bureau ranges; cover note: DOC document no.; A.1004: CMP verifies with AADE; migration: legacy term count + 1; charge ids `charge_origin = BIL`; amend contract §3.2.3 | No |
| F-131 | D | No single glossary of record | §6 of PRD-18 is glossary of record; contract §3.3/§3.11 point to it; DAT business glossary seeded | D-109 |
| R3-009 | D | Wording residues (stamp "zero from 2026-01-01" → "no stamp line"; "posting-rule table" → "billing-ledger rule table"; group examples; closed OI-MKT-12 ref) | CR-RAT-01, BIL-01, MKT-01/-02 | No |
| F-201 | E | Cession Posted→Reversed triggered by "FIN journal reversed" but RI corrects by new linked items | Cession/RIRecovery Reversed only when RI issues correcting item; module-sourced journals corrected only via source module | No |
| F-202 | E | Quote, PolicyTransaction, Reserve, Refund lack canonical model | Adopt §9.2.1, 9.2.3, 9.2.5, 9.2.6 into contract §3.2.4 | D-150 |
| F-203 | E | 11 governed artefacts use different state vocabularies | `governance_stage` mapping (§9.4) as CMP evidence / DAT lineage dimension; no renames | No |
| F-204 | E | Scheduled→InForce derived, no event | POL §8.1 states derivation rule (or `TermStarted` from daily sweep) | No |
| F-217 (see -122) | F | DAT GWP from `ChargeDeltaEmitted`; FIN from `BillingEntryPosted`; BIL quarantined deltas differ | DAT reconciles to FIN per period, quarantined = explained difference; BIL publishes quarantine status | No |
| F-218 (see -210) | F,H | "IPT not refunded on cancellation" rests on ΠΟΛ 1028/2017; ΠΟΛ 1032/2018 cited nowhere; both predate Law 5177/2025 (Art. 43 silent on cancellations) | Cite both circulars in Greece pack rule; tax adviser confirms | No |
| F-002 (see -230) | G | XMR-CR-CLM-04 vs CR-S3-16 contradict | Withdraw CR-CLM-04; apply CR-S3-16 | No |
| F-235 | G,H | `POL_REFUND_DUE` legal basis: Art. 4θ §5 (UNVERIFIED) vs Art. 3ιστ (= Law 5317/2026 Art. 72, verified); "issue counts as payment" key unnamed | Cite Art. 3ιστ for DistanceWithdrawal; name `bil.refund.paid_point` (Greece ISSUED) | No |
| F-236 | G,H | DORA initial-notification deadline in past if classification >24 h after awareness; Del. Reg. (EU) 2025/301 → 4 h of classification; weekends missing | Deadline = classification + 4 h, capped at awareness + 24 h only when classification within 24 h; pack key for weekend rules | No |
| F-237 | G | No stop event for `CLM_MTPL_ASSESSMENT`/`CLM_MTPL_PAYMENT_DUE` | Stop on assessor report accepted; on `PaymentIssued` for accepted amount | No |
| F-238 | G | Clock-owning CMP reqs are Should (`REQ-CMP-138`, `-202`, `-057`, `-190`, `-115`) | Raise `-138`, `-202`, `-115` to Must P1; keep `-057`, `-190` until verified | No |
| F-239 | G | Bitemporal column names/interval ends/date-only zones differ | XMR-FR-150 (§11.5) as contract §3.2.1 amendment | D-159 |
| F-250 | H | OBL-EAA merged traceability omits RAT, UW, FIN, DAT | Add them | No |
| F-251 | H | PLT SII citations are mostly DORA security controls | Keep `REQ-PLT-285…298` under DORA; only `-313…317` under SII once OI-PLT-05 verified | No |
| F-252 (see -367) | H | Traceability shows no MoSCoW/phase (159 non-Must citations; OBL-NATCAT 13/36 below Must) | `REQ-CMP-209` stores MoSCoW & phase per link, computes "met in phase Pn" | No |
| F-253 | H | Equal treatment in pricing (Dir. 2004/113/EC, C-236/09, Law 3769/2009) has no obligation code | Add **OBL-EQT** (RAT, CMP) to contract §3.7 and PRD-11 §15.2 | D-160 |
| F-254 | H | Directive (EU) 2025/1 (IRRD; transposition by 29 Jan 2027, verified) absent | Add **OBL-IRRD** (Verify); CMP backlog; DAT data needs | D-160 |
| F-255 | H | SII deadlines UNVERIFIED in CMP; 5 weeks (Q) & 14 weeks (annual) confirmed 2026; Dir. (EU) 2025/2 applies from 30 Jan 2027 | Mark verified for 2026; effective-dated pack data | No |
| F-257 | H | CLM omits OBL-PAY | PRD-07 §3.1 add OBL-PAY (`REQ-CLM-004`, `-123`) | No |
| R3-008 | H | CMP AI register: AI-PFC-04 bias testing "No (aggregate)" vs owner "bias check on region proxies" | CR-CMP-01 | No |
| F-303 | I,L | `RegistryLookup` (`REQ-MKT-106`) Should vs PTY Must P1 caller | `REQ-MKT-106` Must P1 | No |
| F-304 | I,L | Wallet-consent ops Should (`REQ-MKT-316`); names differ; no cancel | Must P1 conditional on `cap.chn.wallet_prefill`; align names (R-87); add/drop cancel | No |
| F-305 | I,C | SPI op names differ from owner's (CLM CCR-CLM-03, RI `BR-RI-025`, RAT CCR-RAT-05) | Callers adopt MKT names (R-87) | No |
| F-306 | I | Catalogue caller lists incomplete; RI calls `FxRateSource`, WRK `HolidayCalendarProvider` directly (CD-17 says via PLT) | RI uses `plt.Fx.getRate`; WRK uses PLT calendar | No |
| F-308 | I | Cyprus stub = Greece for `BankFileFormat`, `PayeeVerification`, `FxRateSource`; no-op for five SPIs | Synthetic non-SEPA bank-file variant, different VoP profile; accepted no-op list | No (D-215) |
| F-309 | I | Greek values in core text (`REQ-POL-306`, `REQ-CLM-122`, `-182`, `REQ-FIN-190`, `REQ-BIL-270`, `REQ-MKT-314`, `-322`, `REQ-MIG-124`) | Reword per §13.4 | No |
| F-310 | I | KAD activity codes outside `RegimeCodeList` | Regime `ACTIVITY_CODE` | No |
| F-325 | J | DSAR export/erasure targets: 1 business day / 1 hour / 10 calendar days / 2 business days | One CMP SLA table per module type | No (D-209) |
| F-326 | J | Access-token lifetime PLT ≤10 min vs CHN agent ≤15 min | **≤ 10 min with refresh rotation** | No (D-210) |
| F-327 | J | "Stamp provisioned" PLT ≤1 working day vs MKT ≤10 | Define both terms | No |
| F-328 | J | Derived volumes disagree: 1.2M vehicles vs 840k policies; 20k vs 25k external users; RI cession events | Cite XMR-FR-200 | No |
| F-330 | J | BIL & CLM daily totals to FIN by 06:00; RI none | RI NFR: daily RI↔FIN totals by 06:00 | No |
| F-331 | J | Clock warnings within 60 s but clock engine T2 (RTO 4 h); catch-up undefined | Catch-up: overdue warnings fire flagged "late", deadlines unchanged | No |
| F-332 | J | Bind ≤800 ms p95 only if gates concurrent | `NFR-POL-002` states concurrent gate evaluation + gate budget | No |
| F-340 | K | "Finance operations analyst" different base roles in FIN/CMP | One specialisation of ROLE-25 with fiscal permission set | No |
| F-341 | K | Same specialisation different names | Specialisation register contract §3.2.5 | D-211 |
| F-342 | K | Specialisations on unsuitable/no base roles | Add **ROLE-48 Risk manager**, **ROLE-49 Model validator** | D-211 |
| F-343 | K | Two payment personas on one BIL disbursement | CLM stop calls `bil.Disbursement.stop` under BIL authority type | No |
| F-345 | K,L | Must cat dashboard (`REQ-CLM-214`) relies on Should `REQ-DAT-008` | Build from CLM data | No |
| F-346 | K | ROLE-44 supervisor access inconsistent | Exports + CMP evidence room; DAT screens only by evidence-room grant | No |
| F-347 | K | Thin screens SCR-UW-18/-19; SCR-PLT-15…18, 20…22, 27, 28; SCR-RI-12…14, -18 | Complete field tables before wave build | No |
| F-361 | L | 159 Must-anchor → Should/Could/Won't family links | Rule: anchor's MVP scope = its Must family | D-202 |
| F-368 | L | `REQ-CHN-060` Must relies on `REQ-PLT-065` Should | `REQ-PLT-065` Must P1 | within D-203 |
| F-369 | L | Priority score `REQ-WRK-091` Should | Must P1 | within D-203 |
| F-371 | L | Must→Should/Could cross-module links (enrichment/wrong ref) | Reword per §16.6 | No |
| F-372 | L | P1 Musts depend on P2 Musts (`REQ-UW-070`→`-008`; `REQ-DAT-111`→`-186`; `REQ-MKT-326`→`REQ-DAT-128`, `REQ-CMP-181`; `REQ-POL-010`→`-286`, `-304`) | "When the P2 capability is active" or split | No |
| F-373 | L | 18 intra-module Must→Should links | Promote or reword | No |
| F-375 | L | RI & MIG carry two MoSCoW columns | First MoSCoW column = P1 cut | No |
| F-407 | M | RI recoverable on claim view `REQ-CLM-229` Should | Keep; E2E-02 uses `ri.Recovery.listByClaim` | No |
| F-418 | M | No scenario proves AI kill-switch fallback | E2E-10 | No |
| F-419 | M,B | DSAR fan-out text 13 modules vs map 15 rows | CR-CMP-03; E2E-09 asserts §9.2.2 | No |
| F-003 | N | Part decisions overlap/conflict (D-155/D-255 etc.; D-254 vs D-104); §19.2.1 lists D-250…288 only | §5.2 reconciliation | Yes |
| F-402 | N | Stale OI statuses (OI-RAT-15, -01; OI-RI-14, -10; OI-DOC-19) | Close/retitle | No |
| F-403 | N,G | Non-payment duration settled (one month, R-60 K-01) still open in POL/BIL | Close duration part; proof part stays XMR-OQ-001 | No |
| F-408 | N,B | UW & WRK pattern codes differ; 4 UW patterns missing in WRK catalogue | CR-UW-02, CR-WRK-03 | No |
| F-420 | N,L | GS-02 (six-month term) golden runs every build while PRDs assume annual-only motor at MVP | GS-02 becomes capability test, no Greek product expectation | D-286 |

(Row count in the source's minor table = 88 incl. merged-kept rows; all listed above.)

### §5 Proposed resolutions (l.398–505)
- §5.1: per-major resolution, PRD text to change, contract change, decision, gate (folded into table 4.A above). Contract changes named: §3.2.3 invoice row (F-101); §3.4.2 payload outlines (F-114) and new events (F-200); §3.2.3/§3.5.6 (F-116); §3.8.2 disable triggers (F-118); §3.3 Written premium (F-122) and Invoice/Complaint/Cancellation rows (F-127); §3.5.8 `TaxCalculator` operations and callers (F-210, -211) and scheme listing (F-301); §3.9.7 cites XMR-FR-200 (F-320, -321); §3.11 (F-360); §3.9 release-gate clause (F-405).
- §5.2: 77 part decisions (S2 D-100…110 = 11; S3 D-150…160 = 11; S4 D-200…215 = 16; S5 D-250…288 = 39). Reconciled IDs:

| Reconciled | Alias | Subject | Position |
|---|---|---|---|
| D-255 | D-155 | FS cash path | S5 option (b); BIL source `FS_CLEARING`; Clearing excluded from 2-day break `BR-FIN-096` |
| D-102 | D-156 redress part | Complaint redress | Route by type; `CMP_REDRESS`; FIN redress-expense rule; RI in enum |
| D-156 | — | Tax remittance | BIL source `TAX_REMITTANCE` (IPT and stamp duty) |
| D-154 | D-256 | Aux. Fund levy reading | One legal opinion before G0; keys-only + guarded expectations; `REQ-MKT-322` |
| D-153 | D-257 liability item | IPT liability point | Opinion before G0; default DUE unless WRITTEN supported |
| D-257 | — | Other IPT treatments (endorsement credits, non-cancellation returns, withdrawal void, RI premium tax basis) | Pack data via `TaxCalculator.treatment`; build behind pack switch |
| D-212 | D-268 scenario item | Migration A/B | Plan B; board decides before W5; D-268 keeps only Fairfax group reporting manual |
| D-200 | D-271 bank item | Bancassurance P1 | Out unless partner signed by G0; D-271 keeps permission matrix, aggregator, MCP, WCAG audit, cancellation self-service |
| D-213 | D-261 go-live item | Greek legal values | D-261 approves value-package structure now; D-213 gates production activation on Settled |
| D-104 | amends D-254 | Charge delta mode | NET final at core for P1 & P2; key `pol.charges.delta_mode`; D-254's "NET default with GROSS supported" from P3 only |
| D-101 | ≈ D-281 | XBRL | CMP renders/validates/files; DAT figures + SCR intake |
| D-100 | complements D-258 | Fiscal numbering | D-100 who numbers; D-258 trigger, doc types, MARK timing |
| D-105 | governs XMR-FR-250 | Journey lineage | Lineage keys, not single trace id |
| D-203 | — | Should→Must | Nine: `REQ-POL-108`, `REQ-PFC-112`, `-094`, `-145`, `-156`, `-157`, `REQ-PTY-096`, `REQ-PLT-065`, `REQ-WRK-091` |
| D-259 | — | Retention schedule | Adopts F-329 resolution |

CR corrections: XMR-CR-CLM-04 withdrawn; CR-S3-11/-12 use `FS_CLEARING`; CR-S3-14 and S2 PRD-11 row are one CR on `REQ-CMP-134`; `bil.Disbursement.stop`/`void` dry-run gap closed at source (XRF-021).

Decisions referenced in §2–5 but whose text is outside range: D-250 (E2E gate), D-251, D-253, D-254 (technology foundation — "Confirm the technology foundation", decision 10). **Flag:** D-251/-253/-254 are the PRD-18 "technology foundation" decisions; their content must be checked against ARCHITECTURE-DECISIONS.md in PRD-18-B.



### §6 Unified glossary EN/EL (l.508–984) — summary
- Rules: one term, one definition, one owner; base = contract §3.3's 132 rows + module §1.7 + ruling terms (R-32, R-46, R-54, R-76). Greek binding for customer documents (contract §3.9.10). `†` = PRD working translation, `‡` = PRD-18 proposal; all go to ROLE-46 before any template using them is approved (`REQ-DOC-002`). Published code identifiers keep spelling (`RatingArtifact`). **§6 is the glossary of record (D-109; contract D5)**; seeds DAT business glossary (`REQ-DAT-256`).
- **Unified definitions (§6.2):** Written premium = premium + surcharge + discount charge deltas (incl. credits) of bound transactions, net of reversals, excl. tax/levy/fee; time basis **booking date**; period by FIN. POL's all-category sum = **written charges**. Correlation id = technical W3C trace id; correlation key = POL out-of-sequence aggregate id; business lineage = job → transaction → charge id → invoice item → allocation → billing entry → journal line. Unqualified words: account = PTY customer account; transaction = POL policy transaction; recovery/exposure/reserve = CLM meanings (RI "reinsurance recovery", "risk exposure"; UPR never "reserve"); segment = POL (CLM "handling segment"); referral = UW (CHN "back-office referral"); submission = POL job only; statement, delegation, cohort, retention, version always qualified; golden record = MIG.
- **Greek corrections (§6.4):** invoice = ειδοποίηση πληρωμής (never τιμολόγιο); complaint = παράπονο; cancellation = ακύρωση; termination by notice = καταγγελία σύμβασης; activity = ενέργεια; policy hold = πάγωμα εργασιών ‡; blocking point = σημείο φραγής ‡; issue = έκδοση ασφαλιστηρίου; product version = έκδοση προϊόντος; rating artefact = αρχείο τιμολόγησης ‡; statutory clock = νόμιμη προθεσμία ‡; clock instance = τρέχουσα προθεσμία ‡; channel = κανάλι διανομής; journal entry = λογιστική εγγραφή; remittance = απόδοση ασφαλίστρων από διαμεσολαβητή ‡; refund payout method = τρόπος καταβολής επιστροφής ‡.
- **Core identifiers (§6.6):** agree for party (survivor keeps number, `REQ-PTY-034`), account, policy (unchanged across renewals; legacy number as alias), job, transaction (monotonic per policy), MARK/UID, claim (issued only on FNOL submit `REQ-CLM-043`), journal (gap-free per LE/book/fiscal year `REQ-FIN-069`); all via `plt.Number.next`/`NumberingScheme`. Gaps: term number after renewal-based migration (→ legacy count + 1), charge id issued also by BIL (→ `charge_origin = BIL`), Green Card / cover-note / A.1004 issuers (F-130). Conflicts: invoice number (F-101), correlation id (F-116).
- **A–Z glossary (§6.7, ~350 terms)** not reproduced. Build-relevant facts: AFM 9 digits mod-11; artefact hash = SHA-256 over RFC 8785 canonical JSON; configuration hash over stamp manifest; rating key = product artefact hash + rating artefact hash + configuration hash (R-21); static locator UUIDv7; code lists — cancellation source {Policyholder, Insurer, NonPayment, DistanceWithdrawal, LongTermWithdrawal, Objection, Statutory} (void is a kind, R-84), refund method {ProRata, ShortRate, Flat, MinimumRetained, FullRefund}, charge schedule {IMMEDIATE, SPREAD, FOLLOW_PARENT, ACCRUE_ONLY}, blocking points {PRE_QUOTE, PRE_BIND, PRE_ISSUE, NON_BLOCKING}, lanes {STRAIGHT_THROUGH, ASSISTED, EXPERT}, renewal direction {RENEW, RENEW_WITH_CHANGES, REFER, NON_RENEW}, books {LOCAL_GAAP, IFRS17, SOLVENCY_II}, business basis {DOMESTIC, FOS, FOE}, time bases {EFFECTIVE_DATE, TERM_START_DATE, TAX_POINT_DATE, EVENT_DATE, PROCESSING_DATE}, feature flags {RELEASE, OPS_KILL, STAFF_PILOT} (never business switches, CD-06), channel groupings {DIRECT, AGENCY, BROKER, BANCASSURANCE, AGGREGATOR}, delta kind {NET, REVERSAL, REAPPLY, ORIGINAL}, bill mode {DIRECT_BILL, AGENCY_BILL}, proof levels {sent, delivered, opened, acknowledged, registered-delivered, signed-for, returned}, master system {LegacyMaster, Transitioning, CoreMaster, RunOffLegacy, Archived}, close period {Open, SoftClosed, Locked, Reopened}; disparate-impact band 0.8–1.25; cover note = non-fiscal (Law 2496/1997 Art. 2 §2). **Stack-relevant entries:** "Lakehouse — read-side analytical store of open-format transactional tables" with bronze/silver/gold (DAT); "Change set — PFC authoring unit (Git branch to pull request)"; feature store (see conflicts).

### §7 Capability ownership map (l.985–1238) — summary
- **167 capabilities XC-01…XC-167**, each with one owner, owning REQs, consumers (from consumers' own §8.2/§9.2) and basis (CD/R ruling). Grouped PTY 01–13, PFC/RAT 14–26, UW 27–35, POL 36–48, BIL 49–62, CLM 63–75, RI 76–82, FIN 83–91, DOC 92–100, CMP 101–110, CHN 111–118, WRK 119–126, PLT 127–140, DAT 141–149, MIG 150–155, MKT 156–167.
- Ownership rulings to carry into code boundaries: sanctions = one PTY engine (CD-12) called by POL bind, BIL refund/disbursement, CLM payment, RI settlement; proration/day count = RAT library + API; tax calc = RAT via `TaxCalculator` (rates in MKT pack); charge deltas = POL ("only bridge to money"); bind gate = POL on read models (R-83); earned premium function POL, run FIN; all cash in/out = BIL (CD-13), payee bank accounts + VoP = BIL (R-38); billing sub-ledger BIL, books FIN (CD-15); IFRS 17 grouping system of record FIN (R-37, R-52); fiscal numbering + channel = CMP (D-100); Information Centre = CMP `BureauAdapter`; clock register/engine = CMP (values MKT, timers PLT, work WRK); DSAR orchestration CMP; **XBRL rendering unowned → CMP in-house (D-101)**; AI register CMP / runtime PLT / monitoring DAT; MCP agent facade CHN (R-81); identity, authority & maker-checker, audit, event infrastructure (outbox, schema registry, replay, DLQ), **"workflow engine, rules runtime, jobs" (XC-132, `REQ-PLT-007`)**, config runtime & flags, reference data & time service, numbering, AI control plane, retention/legal hold, DORA ops, observability & stamps = PLT; **"lakehouse layers" (XC-142)**, regulatory marts, feature store, model registry = DAT; coexistence routing `mig.Routing.resolve` = MIG (R-96); configuration model, SPI catalogue, packs, switches, i18n, rounding, regime code lists, golden suite + Cyprus stub, clock values, shared code lists = MKT.
- §7.3 multi-claimed: resolved (documents DOC; allocation BIL; tasks WRK; consent PTY; clocks CMP; sanctions PTY; IPT returns split R-82/R-85; payee accounts BIL; MIG party matching via PTY engine). Earned premium: DAT sources from FIN (`REQ-FIN-155`), never recomputes. Findings F-101…F-106, F-118, F-126.
- §7.4 orphans: XBRL & SCR intake (F-107), redress (F-108), telematics (F-109), premium audit (F-110), cancellation effect on third-party MTPL (F-111). Out of scope: assumed RI/retrocession, health & workers' comp, marketing, corporate ERP (GL extract `REQ-FIN-008`), Fairfax consolidation (group pack `REQ-FIN-210`); AML CDD switched off for Greek non-life (`REQ-PTY-186`); no tariff filing in Greece.
- §7.5 schema boundaries: DAT log-based CDC on every module DB (`REQ-DAT-050…056`, 35-day restricted zone) and platform technical tables (outbox, idempotency, audit append) inside module schemas → both codified as named exceptions (D-103). Named write-path exceptions: DAT analytical legacy zone (`REQ-DAT-294`), MIG legacy archive. Corrections via owner APIs (`REQ-PLT-331`); MIG loads only via import APIs (`REQ-MIG-001`); `<Entity>View` read models allowed.

### §8 Programme event catalogue and interface contract register (l.1240–1700)

#### 8.1 Event rules as baselined
Contract §3.4.1 rules stand: past-tense PascalCase facts; **transactional outbox**; topic `<mod>.events.v<major>`; standard envelope; idempotent consumers on `event_id`; P0–P3 classification per field (R-66); `origin=MIGRATION` for converted business. PRD-18 adds:
1. This catalogue = **programme catalogue of record (XMR-D-108)**; contract §3.4.2 becomes a pointer; schema registry (`REQ-PLT-142`) holds it machine-readably.
2. Consumers = **declared handlers only** (checked at source 2026-10-07). DAT ingests every topic (`REQ-DAT-001`) and is omitted. "Listed w/o handler" = producer-named consumers with no handler (R3-002 trim).
3. Partition key = stable internal id of the aggregate whose order consumers depend on (R-100); exceptions flagged (F-117).
4. Registry type names topic-qualified (`wrk.DocumentReceived`, `doc.DocumentRendered`) (F-121).
5. Sets that must be processed as a whole carry completeness fields (F-114, D-107).

Totals: **302 events, 17 producers; 0 events with two producers; 0 consumed events without producer; 92 events consumed only by DAT** (§8.5).

#### 8.2 Event catalogue (full, condensed). Columns: Event | trigger (short) | partition key | consumers with declared handlers (DAT omitted) | listed w/o handler / notes. "DAT" = consumed only by DAT.

**PTY — `pty.events.v1`**

| Event | Trigger | Key | Consumers | W/o handler / notes |
|---|---|---|---|---|
| PartyCreated | party created (any channel/import) | party_id | DAT | analytics, MIG recon counts |
| PartyUpdated | attribute-group change/status; `backdated`, `effective_from` (R-04) | party_id | BIL, CHN, CLM, CMP, DOC, MIG, POL, RI, WRK | — |
| PartiesMerged | merge executed (survivor, merged ids, re-point map, window end, merge_id) | survivor party_id | BIL, CHN, CLM, CMP, DOC, MIG, PLT, POL, RI, UW, WRK | key differs from Unmerged (F-117) |
| PartyUnmerged | unmerge | restored party_id | BIL, CLM, CMP, DOC, MIG, PLT, POL, RI, UW, WRK | key should be survivor id (F-117) |
| IdentifierVerified | verification status change (no value) | party_id | CMP, UW | — |
| AccountCreated | | account_id | WRK | — |
| AccountMerged | merge executed or reversed (R-15) | target account_id | BIL, POL, WRK | — |
| PolicyMoveRequested | move/split approved | target account_id | BIL, POL, WRK | — |
| ConsentChanged | consent given/withdrawn/objection | party_id | CHN, CLM, CMP, DOC | — |
| CommunicationPreferenceChanged | | party_id | BIL, CHN, CLM, DOC | — |
| SanctionsHitRaised / SanctionsHitCleared | PotentialHit/TrueMatch / FalsePositive approved | party_id (or ad-hoc hash) | BIL, CLM, POL, RI, UW, WRK | — |
| ProducerOfRecordChanged | PoR row by change/transfer | policy_term_ref | BIL, CHN, PLT, POL, WRK | key wording R3-005 |
| IntermediaryLicenceChanged | register/authorisation/licence/PI/CPD change | intermediary_id | BIL, CHN, PLT, POL, UW | — |
| CommissionAgreementVersioned | version approved | agreement id | BIL | — |
| PartyRoleChanged | | party_id | RI | — |
| PartyRelationshipChanged | | from party_id | DAT | — |
| LifeEventRecorded | recorded/reversed | party_id | BIL | `REQ-PTY-122` says POL, CLM "can react" — no handler |
| VulnerabilityStatusChanged | flag/rule codes (no reasons) | party_id | BIL, CLM | — |
| AccountStatusChanged | | account_id | POL | — |
| IntermediaryStatusChanged / ProducerCodeChanged | | intermediary_id | BIL | — |
| AppointmentChanged | | intermediary_id | DAT | — |
| BookTransferCompleted | bulk book transfer | transfer id | CHN | — |
| ExternalUserAccessChanged | agency user access | intermediary_id | CHN | — |
| SanctionsListActivated | new list version | list code | DAT | — |

**PFC — `pfc.events.v1`** (key product_id; every `ProductVersion*` payload carries rating-slot declaration, R-27)

| Event | Trigger | Key | Consumers | Notes |
|---|---|---|---|---|
| ProductVersionSubmitted | Draft → Submitted | product_id | DAT | — |
| ProductVersionApproved | last sign-off (hash, approvers, evidence pack) | product_id | FIN, RAT, UW | — |
| ProductVersionScheduled | activation (re)scheduled / windows | product_id | POL, RAT | — |
| ProductVersionPublished | Locked & published (`ipidChanged`) | product_id | CHN, DOC, FIN, MKT, POL, RAT, UW | — |
| ProductVersionRetired | Locked → Retired | product_id | CHN, DOC, MKT, POL, RAT, UW | — |
| ProductVersionReturned | returned to Draft | product_id | DAT | — |
| ProductReferenceTablePublished | | table code | RAT | — |
| ProductRegulatoryAlertRaised | final key / code-list change breaks versions | product_id | DAT | PFC creates WRK activities by API |

**RAT — `rat.events.v1`** (rate mgmt keyed rating_slot_id; job events job_id)

| Event | Trigger | Key | Consumers | W/o handler |
|---|---|---|---|---|
| RatingArtifactPublished | artefact approved & stored | rating_slot_id | PFC | POL |
| RateVersionScheduled | | rating_slot_id | PFC | CHN, POL, WRK |
| RateVersionActivated | activation time reached | rating_slot_id | CHN, PFC, POL | — |
| ShadowRunCompleted | | rating_slot_id | PFC | WRK |
| RateVersionWithdrawn | rollback | rating_slot_id | POL | CHN, CMP, PFC, WRK |
| ImpactAnalysisCompleted | | rating_slot_id | DAT | PFC |
| PricingModificationDecided | approved/rejected/invalidated | job_id | POL, UW | WRK |
| RatingCalculated | FULL/QUICK/ENDORSEMENT/RENEWAL | job_id (request id for anon quotes) | DAT | monitoring feed by design |

**UW — `uw.events.v1`**

| Event | Key | Consumers | Notes |
|---|---|---|---|
| UWIssueRaised / UWIssueApproved / UWIssueRejected | job_id | CHN, POL, WRK | — |
| ApprovalInvalidated | job_id | POL, WRK | — |
| UWIssueClosed | job_id | POL | — |
| ReferralAssigned / ReferralSLABreached | job_id | CHN, WRK | — |
| DeclineIssued | job_id | CHN, POL | — |
| RefusalDocumentIssued | job_id | DAT | DOC, CMP read `uw.RefusalRegister.query` |
| ContingencyCreated | term_id | CHN, WRK | — |
| ContingencyOverdue | term_id | CHN, POL, WRK | — |
| ContingencyResolved | term_id | POL | — |
| PolicyHoldActivated / PolicyHoldReleased | hold_id | CHN, CLM, POL, WRK | — |
| InspectionCompleted / ExternalReportReceived | job_id | WRK | — |
| UWRuleSetActivated | rule_set_id | PFC | new: UW lists DAT only (F-119) |
| RenewalDirectionSet | term_id | POL | — |
| DisclosureFindingDecided | term_id | CLM, POL | — |

**POL — `pol.events.v1`** (key policy_id; RenewalRunCompleted keyed legal_entity_id)

| Event | Trigger | Consumers | W/o handler / notes |
|---|---|---|---|
| SubmissionCreated | | DOC, PTY, UW, WRK | CHN |
| QuoteIssued | quote version Quoted | CHN, DOC, PTY, UW, WRK | — |
| QuoteExpired | validity ends | BIL, CHN, RAT, UW, WRK | — |
| PolicyBound | submission bound (policy no., term, transaction, period, product version, artefact/resolution/configuration hashes, PoR, account, payer, plan ref, IFRS 17 proposals) | BIL, CHN, CMP, DOC, FIN, MIG, PFC, PTY, RAT, RI, UW, WRK | — |
| PolicyIssued | mandatory issuance steps complete (`REQ-POL-178`) | CHN, CMP, PTY, UW, WRK | BIL, DOC, FIN, RI |
| PolicyChanged | change/carry-forward/suspension/reactivation bound | BIL, CHN, CLM, CMP, DOC, FIN, PTY, RAT, RI, UW | — |
| CancellationScheduled | | BIL, CHN, DOC, WRK | CLM, CMP |
| CancellationRescinded | | BIL, DOC, WRK | CHN, CMP |
| PolicyCancelled | cancellation bound (source, reason, eff. date, refund method, rewrite link) | BIL, CHN, CLM, CMP, DOC, FIN, MIG, PFC, PTY, RAT, RI, UW, WRK | — |
| PolicyVoided | void ab initio (statutory reason, cost-of-cover rule) | BIL, CHN, CLM, CMP, DOC, FIN, MIG, PTY, RI | — |
| PolicyReinstated | | BIL, CHN, CLM, CMP, DOC, FIN, PTY, RAT, RI, WRK | — |
| PolicyRewritten | | BIL, CLM, CMP, DOC, FIN, PFC, PTY, RAT, RI, UW | CHN |
| RenewalCreated | renewal job created | BIL, RI, UW, WRK | CHN, CMP, DOC |
| RenewalOffered | offer, premium summary, acceptance mode, deadline | BIL, CHN, DOC, MIG, WRK | CMP |
| RenewalBound | | BIL, CHN, CMP, FIN, MIG, PFC, PTY, RAT, RI, WRK | DOC |
| PolicyNonRenewed | | BIL, CHN, CMP, DOC, MIG, PFC, PTY, UW, WRK | — |
| PolicyLapsed | | BIL, CHN, CMP, DOC, MIG, PFC, PTY, WRK | — |
| TransactionReversed | each reversal in aggregate (correlation key, segments, voided-by-cancellation flag) | BIL, CLM, CMP, DOC, FIN, RAT, RI, WRK | needs aggregate completeness fields (F-114) |
| TransactionReapplied | each reapplication | BIL, CLM, CMP, DOC, FIN, RI, UW, WRK | as above |
| JobPreempted | base no longer head | UW, WRK | CHN |
| ChargeDeltaEmitted | every bound transaction (charge id, term, element locator, coverage, charge type, delta kind, net amount, currency, valid period, booking date, transaction id, correlation key) | BIL, FIN (context), RI | needs transaction + aggregate completeness fields (F-114) |
| JobWithdrawn / JobNotTaken | | BIL, CHN, MIG, UW | WRK (JobWithdrawn) |
| ProofOfCoverIssued | cover note requested & rendered | CHN | CMP |
| OutOfSequenceConflictRaised | | DAT | WRK |
| PolicyMoved | | BIL, CMP | CHN, PTY, WRK |
| RenewalRunCompleted | batch finished (key legal_entity_id) | DAT | WRK |

**BIL — `bil.events.v1`** (key billing_account_id; disbursements disbursement_id; commission/account current intermediary_id; levies legal_entity_id)

| Event | Trigger | Consumers | Notes |
|---|---|---|---|
| InvoiceIssued | invoice/credit note Billed (totals by category, due, method, fiscal ref) | CHN, FIN, PTY | — |
| PaymentReceived | receipt created / intermediary collection recognised (incl. RI settlement ref) | CHN, CLM, DOC, FIN, MIG, POL, PTY, RI | — |
| CashAllocated | allocation batch (`arrears_cleared` per term, `down_payment`) | CHN, CLM, FIN, MIG, POL, RI | — |
| PaymentReversed | | CHN, FIN | — |
| DownPaymentCleared | | CHN, POL | bind gate (`REQ-BIL-003`) |
| DelinquencyStarted | | CHN, PTY, WRK | — |
| NonPaymentNoticeSent | notice delivered with proof and clock started | CHN, WRK | — |
| CancellationForNonPaymentRequested | `ClockElapsed` of `BIL_NONPAY_NOTICE` with arrears and no hold | POL, PTY, WRK | — |
| RefundApproved / RefundRejected | | CHN, DOC, FIN (Approved); DAT (Rejected) | — |
| RefundDisbursed | refund disbursement cleared | CHN, CMP, FIN, POL | stops `POL_REFUND_DUE` |
| WriteOffPosted | | FIN, PTY | — |
| DisbursementIssued / DisbursementVoided / DisbursementReturned | bank ack / recall / beneficiary return (key disbursement_id) | CLM, FIN, RI | — |
| DisbursementCleared | statement debit matched | FIN, RI | F-200: add CLM; add `DisbursementRejected`, `DisbursementStopped` (D-151) |
| CommissionCalculated / CommissionPaid | (key intermediary_id) | CHN, FIN | — |
| CommissionStatementIssued | (fiscal mode) | FIN | — |
| AccountCurrentIssued | | DAT | — |
| LevyAccrued / LevyRemitted | daily & period close / remittance cleared (key legal_entity_id) | FIN | key should be levy-period id (F-117) |
| ChargesScheduled | deltas scheduled | DAT | BIL says "POL issuance tracking" — POL no handler (F-120) |
| BillingEntryPosted | every billing-ledger entry | FIN (**sole posting source**, `REQ-FIN-036`) | — |
| DelinquencyResolved | | POL | — |
| PaymentPlanChanged | | POL | — |
| MandateActivated / MandateCancelled / BillingAccountChanged | | DAT | — |

**CLM — `clm.events.v1`** (key claim_id; cat events cat_event_id; certificate requester party_id)

| Event | Trigger | Consumers | Notes |
|---|---|---|---|
| ClaimReported | FNOL submitted (snapshot ref, loss & notice dates, cat code, channel) | CHN, DOC, FIN, POL, PTY, RI, UW, WRK | — |
| CoverageVerified / ReverificationRequired | | WRK | — |
| ExposureCreated | (SII LoB, accident date) | FIN, RI | — |
| ReserveChanged | approved reserve change per line (delta and open ×3 currencies, IFRS 17 ref, cat code) | FIN, RI | — |
| TransactionSetApproved | | FIN, RI | — |
| TransactionSetRejected | | DAT | WRK |
| PaymentIssued | on `DisbursementIssued` (amounts ×3, payee, method, ex-gratia) | CHN, FIN, POL, RI | — |
| PaymentVoided | void/stop/return confirmed | FIN, RI | CHN |
| RecoveryRecorded | | FIN, RI | — |
| ClaimClosed / ClaimReopened | | CHN, FIN, POL, PTY, RI, WRK (+ MIG for Closed) | — |
| ClaimUpdated | | CHN, RI | WRK |
| CatEventAssigned | | FIN, RI | — |
| CatEventDeclared / CatEventChanged | (key cat_event_id) | RI, UW | WRK |
| FraudScoreReceived | | WRK | — |
| StatutoryOfferDue / StatutoryOfferBreached | | WRK | CMP; duplicate signal (F-105) |
| StatutoryOfferIssued | reasoned offer/reply delivered | CMP | CHN |
| ClaimsHistoryCertificateIssued | (key requester party_id) | CHN, CMP, DOC | — |
| CoverageDecisionRecorded | | DAT | CHN, CMP |
| VendorAssigned / VendorInvoiceApproved | | DAT | CHN |
| SiuCaseOpened / SiuCaseConcluded | | DAT (restricted) | CMP |
| FriendlySettlementSubmitted | FS case to clearing | DAT | money path of FS balances (F-214) |

**RI — `ri.events.v1`**

| Event | Trigger | Key | Consumers | Notes |
|---|---|---|---|---|
| RIContractActivated | approved version reaches period start | ri_contract_id | FIN, UW | — |
| RIContractVersioned | | ri_contract_id | UW | — |
| RIProgrammeApproved / RIContractExpired / RIContractClosed | | programme or contract id | DAT | — |
| CessionCalculated | cession deltas booked (cession ids, charge ids, risk key, contract/section/participant, amounts ×4, posting key, LoB, IFRS 17 refs, correlation id) | **policy_id** | FIN | — |
| CessionExceptionRaised / CessionExceptionResolved | | policy_id | FIN, WRK (Raised); — (Resolved) | — |
| FacPlacementBound | | ri_contract_id | UW | — |
| RecoveryCalculated | recovery deltas batch (occurrence, claims, layer, participant, deltas ×4) | ri_contract_id | CLM, FIN | ambiguous name (F-121) |
| ReinstatementPremiumDue | | ri_contract_id | FIN | — |
| DepositPremiumDue | XoL instalment due (`REQ-RI-154`, Should P1) | ri_contract_id | DAT | **no FIN posting source (F-132)** |
| PremiumAdjustmentCalculated / CommissionAdjusted | adjustment approved | ri_contract_id | DAT | **F-132** |
| BordereauGenerated | | ri_contract_id | FIN (context) | — |
| StatementIssued | statement of account issued | statement_id | FIN | ambiguous name (F-121) |
| StatementAgreed / StatementDisputed | | statement_id | DAT | — |
| CashCallRaised | | ri_contract_id | FIN | — |
| SettlementRecorded | settlement completed (FX difference, BIL ref) | counterparty_party_id | FIN | — |
| LayerExhausted / CatOccurrenceConfirmed / LargeLossNotified / CollateralChanged | | ri_contract_id | DAT | — |
| AccumulationThresholdBreached | zone band rises | zone_key | UW | — |
| ReinsurerSecurityAlertRaised | downgrade/stale rating | reinsurer party id | DAT | — |

**FIN — `fin.events.v1`** (key legal_entity_id except Ifrs17GroupAssigned; F-117 proposes journal/period/break/return/run ids)

| Event | Consumers | Notes |
|---|---|---|
| JournalPosted | CLM, RI | — |
| EarningRunCompleted / GLExtractSent / FxRevaluationCompleted / PostingRuleSetActivated / BusinessEventSuspended / ActuarialResultSetPosted | DAT | WRK activities by API for suspended events |
| PeriodClosed (Locked or re-locked) | BIL, RI, WRK | — |
| PeriodReopened | POL, WRK | FIN lists DAT only (R3-003c, F-119) |
| ReconciliationBreakRaised / ReconciliationBreakResolved | BIL, MIG, RI, WRK (Raised); MIG (Resolved) | — |
| Ifrs17GroupAssigned (key assignment_subject_id) | POL | FIN lists DAT only (F-119) |
| TaxReturnFiled | CMP | — |

**DOC — `doc.events.v1`** (key document_id; envelopes envelope_id; batches batch_id)

| Event | Consumers | Notes |
|---|---|---|
| DocumentRequested | DAT | — |
| DocumentRendered (number, hash, archive id, recipients) | BIL, CHN, CLM, PFC, POL, RI, UW, WRK | — |
| DocumentRenderFailed | UW, WRK | — |
| DocumentDelivered / DeliveryFailed (proof level, **notification date**, evidence pack / reason, fallback) | BIL, CHN, CLM, CMP, POL, PTY, RI, UW, WRK | — |
| SignatureCompleted / SignatureDeclined (key envelope_id) | CHN, POL, WRK | — |
| DocumentSuperseded | CHN | — |
| DocumentBatchCompleted / EvidencePackSealed / TemplateVersionPublished / ClauseVersionPublished / BindingChanged | DAT | others read by API |
| FormPatternPublished / FormPatternRetired (key form_pattern_id) | PFC | — |

**CMP — `cmp.events.v1`** (fiscal key fiscal_document_id; bureau policy_id; clocks clock_instance_id; others own id)

| Event | Consumers | W/o handler / notes |
|---|---|---|
| FiscalDocRegistered / FiscalDocRejected (source type/id, doc type, MARK, UID, QR ref / cause) | BIL, CLM, DOC, FIN, MIG, POL, WRK | — |
| FiscalDocCancelled | DAT | intended BIL, FIN, CLM (R3-002) |
| BureauEventSubmitted | MIG, POL | — |
| BureauLagExceeded | MIG, POL, WRK | — |
| BureauFactRejected | DAT | intended POL, WRK (R3-002) |
| ClockStarted | BIL, CLM, FIN, WRK | consumers correlate by subject |
| ClockWarned | BIL, CHN, CLM, DOC, FIN, POL, UW, WRK | — |
| ClockBreached (DEADLINE expired while running; late flag) | BIL, CHN, CLM, DOC, FIN, MIG, POL, UW, WRK | — |
| ClockMet | BIL, CLM, FIN, POL, UW, WRK | — |
| ClockElapsed (WAITING_PERIOD expired, R-78) | BIL, CHN, CLM, POL, WRK | trigger of follow-on actions |
| ClockPaused / ClockResumed / ClockCancelled | DAT | intended WRK; `REQ-WRK-193` cancels activities without declared handler (R3-002) |
| ComplaintReceived | PFC, PTY, WRK | — |
| ComplaintAnswered | WRK | — |
| ComplaintAcknowledged / ComplaintEscalated / ComplaintClosed | DAT | intended WRK |
| DSARReceived / DSARCompleted | DOC, PLT, PTY, RAT, WRK / WRK | fan-out is synchronous; event = preparation only |
| DSARExtended / DSARTaskOverdue | DAT | intended CHN, WRK |
| RegulatorySubmissionFiled / RegulatorySubmissionRejected | DAT | `REQ-DAT-119` uses them but DAT §8.2 omits; intended FIN, PLT |
| AnnualReturnSubmitted | DAT | intended FIN |
| ObligationChanged / ControlTestFailed / RegulatoryChangeAssessed | DAT | intended WRK, MKT |
| AiSystemStatusChanged (key feature id) | DAT | **Major F-118: PLT has no handler** |

**CHN — `chn.events.v1`**

| Event | Key | Consumers | Notes |
|---|---|---|---|
| WithdrawalRequestReceived | withdrawal_request_id | CMP, POL (backup void, idempotent on request id, `REQ-POL-314`) | — |
| WithdrawalRequestClosed | withdrawal_request_id | DAT | — |
| DisclosureReceiptRecorded (pre-contractual pack on durable medium, `REQ-CHN-316`) | job_id | POL (`DisclosureDeliveryView`, R-83, R-95) | — |
| PermissionMatrixActivated / ApiClientStatusChanged / WebhookSubscriptionSuspended / ConfirmationTicketDecided / ReferToBackOfficeCreated / ChannelJourneyAbandoned | various | DAT | CCR-CHN-01 names more consumers (R3-002) |

**WRK — `wrk.events.v1`**

| Event | Consumers | Notes |
|---|---|---|
| ActivityCreated (mandatory, gate_blocking, sequence) | CHN, POL (`ActivityGateView`) | — |
| ActivityAssigned | UW | CHN |
| ActivityCompleted | BIL, CLM, CMP, FIN, MIG, PLT, POL, PTY, RI, UW | — |
| ActivitySkipped / ActivityCancelled | POL | — |
| ActivityEscalated | UW | — |
| SLABreached | PLT, UW | — |
| NoteAdded / NoteRevised / NoteDeleted (no text) | DAT | CHN, CMP |
| DocumentReceived (inbound) | CLM | name collides with DOC family (F-121) |
| DocumentClassified | CLM, PTY, UW | — |
| DocumentLinked | CHN, CLM, DOC, PTY, UW | BIL, CMP, POL |
| DocumentVerified / DocumentQuarantined | DOC, UW / DOC | PLT |
| RequestReceived / RequestAnswered / RequestClosed | CHN, UW (Answered); CHN (Closed) | CHN (Received) |
| ParticipantChanged | DAT | — |

**PLT — `plt.events.v1`**

| Event | Key | Consumers | Notes |
|---|---|---|---|
| ConfigChanged | context hash | all 16 modules | key should be resolution-context id (F-117) |
| FeatureFlagChanged | flag_key | CHN, FIN, MKT, WRK | — |
| AiToggleChanged / AiKillSwitchActivated | feature_id (or GLOBAL) | all 15 modules with AI, CMP | — |
| IncidentDeclared / IncidentClassified / IncidentReported (DORA) | incident_id | CMP, MKT, WRK (Declared); CMP (others) | WRK (Classified, Reported) |
| RetentionPurgeCompleted | rc_code | CMP, PTY | — |
| UserProvisioned / UserAccessChanged / UserDeprovisioned | user_id | WRK (Changed, Deprovisioned) | CHN, WRK (Provisioned) |
| AuthorityGrantChanged | profile_id or user_id | BIL, CLM, FIN, RAT, UW, WRK | — |
| ApprovalRequested / ApprovalDecided | request_id | WRK / BIL, CLM, FIN, RAT, RI, WRK | — |
| LegalHoldApplied / LegalHoldReleased | hold_id | BIL, CLM, DOC, FIN, MIG, RAT, RI | CMP, POL, PTY (enforced centrally) |
| ReferenceDataPublished | reference set code | FIN, RI | BIL, CMP, WRK |
| DeadLetterParked / JobRunFailed / ChangeDeployed | consumer group / job / change | DAT | WRK, CMP |

**DAT — `dat.events.v1`**

| Event | Key | Consumers | Notes |
|---|---|---|---|
| ModelVersionRegistered | model_id | CMP, PLT | — |
| ModelDriftDetected / BiasThresholdBreached | model_id (or rating artefact) | every module with AI, CMP, PLT, MKT | — |
| RegulatoryMartPublished | mart_run_id | CMP, FIN, MIG | — |
| ActuarialResultsPublished | run_id | FIN | — |
| AccumulationSnapshotPublished | snapshot_id | RI, UW | DAT says none (F-119) |
| LakehouseErasureCompleted | task_id | CMP (evidence only, R-97) | — |
| ModelVersionApproved / ModelVersionRetired / DataContractVersionPublished / DataReconciliationBreakRaised / DataReconciliationBreakResolved | own id | — | informational |

**MIG — `mig.events.v1`** (MIG's own events not flagged `origin=MIGRATION`)

| Event | Key | Consumers | Notes |
|---|---|---|---|
| MigrationBatchLoaded | batch_id | CMP, FIN, PTY, WRK | — |
| MigrationBatchReconciled | batch_id | FIN, WRK | — |
| MigrationWaveStatusChanged | wave_id | BIL, CMP, DOC, WRK | — |
| CoexistenceMasterChanged | route_id | BIL, CHN, CLM, CMP, DOC, WRK | cache only; `mig.Routing.resolve` authoritative (R-96) |

**MKT — `mkt.events.v1`** (key stamp_id; pack lifecycle pack_id)

| Event | Consumers | Notes |
|---|---|---|
| ConfigurationActivated | CMP, FIN, PFC, PLT, POL, PTY, RAT, WRK | governance fact (R-03) |
| PackActivated | BIL, CHN, CLM, CMP, DOC, FIN, PFC, PLT, POL, PTY, RAT, RI, WRK | — |
| PackRolledBack | BIL, CMP, FIN, PFC, PLT, POL (`REQ-POL-354`), PTY, RAT, WRK | R3-001 fixed |
| RateTableChanged | BIL, PLT, RAT | FIN |
| RegimeCodeListPublished (key pack_id) | FIN, PFC, RI | — |
| TranslationBundlePublished | CHN, DOC | — |
| CrossBorderAuthorisationChanged | PFC | CMP, POL (POL checks synchronously) |
| PackDeprecated / GoldenSuiteCompleted / LegalEntityStatusChanged | DAT | PLT, WRK, "all modules" |

**New events proposed by PRD-18 (not yet in producers' catalogues at §8 text):** `DisbursementRejected`, `DisbursementStopped` (BIL, F-200/D-151); completeness fields `transaction_delta_count`/`_index`, `aggregate_member_count`/`_index` (F-114/D-107); optional `TermStarted` daily sweep (F-204); PLT handler for `AiSystemStatusChanged` emitting `AiToggleChanged` (F-118).

#### 8.3 Ordering, idempotency and correlation agreements

| Flow | Agreement | Gap |
|---|---|---|
| Out-of-sequence reverse-and-reapply to money | POL writes reversals, new txn, reapplications, deltas and events **in one DB transaction**, all on `policy_id` partition, gap-free sequence, one correlation key (`REQ-POL-006`, `-112`). BIL nets GROSS sets by correlation key before scheduling (`REQ-BIL-075`), unwinds exact items (`-077`); RI one recomputation per interval (`REQ-RI-084`); FIN links pairs (`REQ-FIN-297`); DAT correction sets (`REQ-DAT-042`, `-299`); CLM re-verification (`REQ-CLM-057`); CMP re-derives bureau facts. Idempotent on charge id (+ delta kind for RI) | completeness (F-114); GROSS not supported by RI P1 (F-115) |
| Charge deltas to books | BIL posts written entry; FIN posts only from `BillingEntryPosted`, uses deltas for reconciliation (`REQ-FIN-036`, `-245`); one posting source per fact (CD-15) | GWP definition (F-122) |
| Bind gate | POL evaluates gates from read models (BIL `DownPaymentCleared`, UW issue events, WRK `ActivityCreated/Completed`, DOC `DocumentDelivered`, CHN `DisclosureReceiptRecorded`), shows staleness, **never calls a T2 module synchronously (R-83)** | — |
| Non-payment → cancellation | BIL starts `BIL_NONPAY_NOTICE` on DOC delivery proof; CMP `ClockElapsed`; BIL re-checks arrears & holds before `CancellationForNonPaymentRequested` (dedupe on request id); POL cancels (`REQ-POL-311`); payment after request → rescind (`REQ-POL-211`) | — |
| Claim payment | CLM approves set (content hash) → `bil.Disbursement.request`; BIL verifies hash (`REQ-BIL-198`); `DisbursementIssued` → CLM `PaymentIssued` → RI, FIN. FIN posts expense leg from CLM, cash leg from BIL via clearing account | — |
| RI settlement | RI → `bil.Disbursement.request` (`RI_SETTLEMENT`); `DisbursementIssued/Cleared` → RI | source enum omits RI (F-108) |
| Fiscal documents | BIL or CLM → `cmp.FiscalDocument.request` (Idempotency-Key); `FiscalDocRegistered` → BIL MARK, DOC releases held docs, FIN recon, POL issuance progress | numbering owner (F-101) |
| Statutory clocks | owners start/stop via synchronous `cmp.Clock.*`; CMP events keyed by instance; consumers idempotent on event_id + instance; duplicate stops no-ops | 3 warning paths (F-105) |
| Party merge/unmerge | different keys → replay order not guaranteed | F-117 |
| Coexistence routing | `CoexistenceMasterChanged` caches; `mig.Routing.resolve` authoritative (R-96) | R3-007 |
| Configuration | MKT `ConfigurationActivated`/`PackActivated`/`PackRolledBack` → PLT `ConfigChanged`; caches refreshed **within 10–60 s**; POL lists rollback-window transactions (`REQ-POL-354`) | key (F-117) |
| AI governance | DAT drift/bias → PLT auto-disable → `AiToggleChanged` (≤ 60 s); CMP `AiSystemStatusChanged` not handled | F-118 |
| DSAR | CMP synchronous fan-out; `LakehouseErasureCompleted` evidence only (R-97) | — |
| Withdrawal | `chn.Withdrawal.declare` → `pol.Withdrawal.submit`; event as backup; POL idempotent on `withdrawal_request_id` (R-89) | — |
| Maker-checker | PLT `ApprovalRequested` → WRK activity; `ApprovalDecided` → owner executes after `plt.Approval.verifyForExecution`; content-hash bound | — |
| Migration flag | `origin=MIGRATION` → FIN, RI, DAT opening-balance handling only (`REQ-FIN-043`, `REQ-RI-007`, `REQ-DAT-045`) | — |
| Quote-to-journal traceability | not achievable across batch runs | F-116 |

Two mermaid diagrams in source: (1) OOS sequence POL → BIL/RI (reversed/reapplied/deltas with index i of m) → BIL waits for m deltas, nets, requests fiscal doc → `BillingEntryPosted` → FIN; RI recompute → `CessionCalculated` → FIN; CMP `FiscalDocRegistered` → BIL. (2) Quote → bind flow: QuoteIssued → PolicyBound + ChargeDeltaEmitted (gated by DownPaymentCleared) → BillingEntryPosted (written) / InvoiceIssued → FiscalDocRegistered → DocumentRendered/Delivered; PaymentReceived + CashAllocated → BillingEntryPosted (collected) → JournalPosted; PolicyBound → CessionCalculated → JournalPosted.

#### 8.4 Interface contract register (synchronous cross-module operations)
All owners: RFC 9457 errors with `<MOD>-ERR-*` codes; `Idempotency-Key` on commands (contract §3.5.3–3.5.4). Defining-REQ column omitted for length; each operation is defined by the owner's anchor REQ (`REQ-<MOD>-001…0xx`) in its PRD §9.1.

| Owner | Operations | Callers | Idem / dry-run / perf | Status |
|---|---|---|---|---|
| PTY | `pty.Party.search/get/create/update/validate`; `pty.PartyRole.*` | POL, BIL, CLM, RI, UW, CHN, CMP, PLT, FIN, MIG | validate-only | OK |
| PTY | `pty.Account.*` incl. `merge`, `requestPolicyMove`, `split` | POL, BIL, CHN (via WRK request), PLT | dry-run merge/move | OK |
| PTY | `pty.Consent.query`, `pty.CommunicationPreference.resolve` | POL, BIL, CLM, DOC, CHN, UW | query | OK |
| PTY | `pty.Screening.screen` / `bulk` | POL bind, BIL (`REQ-BIL-007`, `-200`), CLM (`-133`), RI (`-049`, `-191`), UW, MIG | **sync ≤ 300 ms** | OK |
| PTY | `pty.ProducerCode.validate`, `pty.ProducerOfRecord.get/change/bulkTransfer`, `pty.CommissionAgreement.resolve/simulate` | POL, BIL, CHN, DOC, PFC, PLT, UW | dry-run | OK |
| PTY | `pty.Vulnerability.query`; `pty.Dsar.*`; `pty.Import.*` | POL, BIL, CLM, DOC, CHN; CMP; MIG | | OK |
| PFC | `pfc.ProductVersion.resolve`; `pfc.Artifact.get`; `pfc.Catalogue.get`; `pfc.ChargeType.list`; `pfc.RegulatoryMapping.get` | POL, RAT, RI, FIN, CLM, BIL, DOC, MKT, MIG | query | OK |
| PFC | `pfc.QuestionSet.*`, `pfc.Availability.check`, `pfc.Product.describe`, `pfc.PolicyDraft.validate`, `pfc.Pog.get`, `pfc.RenewalConversion.convert` | POL, CHN, UW, BIL | pure | OK |
| RAT | `rat.Rate.rate` / `rateBatch` | POL, UW, CHN (dry-run), PFC, MKT, MIG | optional key; DRY_RUN | OK |
| RAT | `rat.Worksheet.*`, `rat.Breakdown.get`, `rat.ChangeExplanation.get`, `rat.PriceReview.*`, `rat.PricingModification.*`, `rat.BonusMalus.transition`, `rat.Proration.prorate`, `rat.DayCount.yearFraction` | POL, UW, CHN, DOC, PFC, RI | | OK |
| UW | `uw.Rules.evaluate`, `uw.Issue.blockingStatus`, `uw.Issue.listForChannel`, `uw.Referral.request`, `uw.Decline.*`, `uw.Contingency.*`, `uw.PolicyHold.check`, `uw.Accumulation.check`, `uw.ExternalReport.order`, `uw.RenewalDirection.get` | POL, CHN, PFC | dry-run on evaluate, decide | OK |
| UW | `uw.DisclosureFinding.record/get/list`; `uw.IssueType.search`; `uw.DataSubject.*`; `uw.Import.*` | CLM; PLT, CMP, MIG | | OK |
| POL | `pol.Submission.create`, `pol.Job.updateDraft/quote/newVersion/bind/withdraw`, `pol.PolicyChange.create`, `pol.Cancellation.create`, `pol.Withdrawal.submit`, `pol.Suspension.create`, `pol.Renewal.*`, `pol.Rewrite.create`, `pol.PolicyMove.execute` | CHN, UW, BIL (dry-run), PTY, MIG | key; dry-run | OK (`pol.Job.bind` canonical, XRF-003) |
| POL | `pol.Policy.get`, `pol.Term.get/timeline`, `pol.Snapshot.get`, `pol.Policy.search`, `pol.Policy.getMany`, `pol.Segment.changes` | CLM, RI, FIN, DAT, DOC, CMP, UW, RAT, PFC, CHN, WRK | **`pol.Policy.get` ≤ 150 ms** | OK |
| POL | `pol.Earning.compute`, `pol.Charges.reconcile`; `pol.Import.policy/term`, `pol.Dsar.*` | FIN, BIL, RI; MIG, CMP | | OK |
| BIL | `bil.BillingAccount.*`, `bil.PaymentPlan.list/select/change`, `bil.DownPayment.status/initiate`, `bil.BillingPreview.compute` | POL, CHN, PFC, PTY | dry-run | OK |
| BIL | `bil.Disbursement.request/stop/void/get` | CLM, RI, **CMP (redress)** | key; dry-run | **Gap** CMP source (F-108) |
| BIL | `bil.PayeeAccount.create/verify/get`; `bil.Receivable.register` | CLM, RI, PTY, CHN | dry-run | OK |
| BIL | `bil.Payment.take`, `bil.Mandate.*`, `bil.Refund.get/list`, `bil.IntermediaryCollection.report`, `bil.AccountCurrent.*`, `bil.Commission.*` | CHN | key | OK |
| BIL | `bil.Ledger.query/balance`, `bil.TaxLevy.periods/detail`, `bil.Reconciliation.fin` | FIN | query | **Gap** (F-106) |
| BIL | unnamed "account, invoice, payment, refund, statement queries"; "refund decision API" | DOC; WRK | | **Gap** (F-120): name `bil.Invoice.get`, `bil.Refund.decide` |
| CLM | `clm.Fnol.*`, `clm.ClaimTracking.get/list`, `clm.Certificate.request`, `clm.Service.request`, `clm.VendorInvoice.submit`, `clm.StatutoryOffer.recordAcceptance`, `clm.Conversation.send` | CHN, PTY, POL, WRK, DOC | key; dry-run | OK |
| CLM | `clm.Claim.search/get`, `clm.Financials.get`, `clm.CatEvent.aggregate`; `clm.Dsar.*`; `clm.Import.*` | WRK, CMP, UW, RI; CMP; MIG | | OK |
| CLM | unnamed "daily financial totals"; "payment decision API" | FIN; WRK | | **Gap** (F-120): `clm.Financials.dailyTotals`, `clm.TransactionSet.approve` |
| RI | `ri.Accumulation.zone/query/footprint`, `ri.Cession.preview`, `ri.Cession.listByPolicy`, `ri.Recovery.listByClaim`, `ri.Contract.get/list/versionAt`, data products | UW, POL, CLM, FIN, DAT | query | OK |
| FIN | `fin.Ifrs17Group.assignment/get/riHeldAssignment`, `fin.Period.status` | POL, CLM, RI, DAT, BIL | query | OK |
| FIN | `fin.Reconciliation.exchange` | BIL | key | **Gap** (F-106) |
| FIN | `fin.ActuarialResults.*`, `fin.ActuarialInputs.extract`; `fin.Import.*`, `fin.Dsar.*` | DAT; MIG, CMP | | OK |
| DOC | `doc.Document.request/requestBatch/preview/listForObject/get`, `doc.ProofOfCover.issue`, `doc.Ipid.get`, `doc.Delivery.status/evidenceFor`, `doc.Signature.*` | POL, BIL, CLM, RI, CMP, UW, PTY, PFC, RAT, PLT, WRK, CHN, MIG | key; dry-run | OK |
| DOC | `doc.Archive.store/get/verify` | WRK, PFC, RAT, PTY, PLT, MKT, BIL, CLM, RI, FIN, DAT, MIG | key | OK |
| DOC | `doc.Forms.infer`, `doc.FormPattern.validateReferences`, `doc.Binding.resolve`, `doc.Clause.get`, `doc.Template.list` | POL, PFC, WRK | | OK |
| CMP | `cmp.FiscalDocument.request/get/listBySource/qrPayload`, `cmp.FiscalReconciliation.*` | BIL, CLM, DOC, FIN | key; dry-run | OK |
| CMP | `cmp.Clock.start/pause/resume/stop/cancel/extend/get/query`, `cmp.ClockDef.list` | POL, BIL, CLM, UW, FIN, CHN, DOC, MKT | key; dry-run on start | OK |
| CMP | `cmp.Bureau.status`, `cmp.Complaint.*`, `cmp.Dsar.create`, `cmp.Submission.*`, `cmp.AiSystem.register/get`, `cmp.Evidence.submit`, imports | POL, CLM, CHN, FIN, RAT, PLT, PFC, MKT, MIG | key | OK |
| CHN | `chn.Permission.evaluate` | WRK | query | OK |
| WRK | `wrk.Activity.create/complete/blockingStatus`, `wrk.Participant.*`, `wrk.Note.*`, `wrk.InboundDocument.*`, `wrk.Request.submit`, `wrk.Search.global`, `wrk.Assignment.simulate` | every module | key; dry-run on create | OK |
| PLT | `plt.Authority.check`, `plt.AuthorityType.register`, `plt.Approval.*`, `plt.Audit.append`, `plt.Number.next/reserve`, `plt.Time.now`, `plt.Calendar.*`, `plt.Fx.getRate`, **`plt.Workflow.*`**, `plt.Adapter.invoke`, `plt.Ai.invoke`, `plt.AiToggle.resolve`, `plt.LegalHold.check`, `plt.Consumer.replay` | every module | key | OK |
| PLT | `plt.Config.resolve/trace/reconstruct` | MKT explorer | query | **Gap** duplicates `mkt.Configuration.*` (F-102) |
| DAT | `dat.Population.sample/aggregate`, `dat.Kpi.get`, `dat.Model.register`, `dat.BiasTest.run`, `dat.DataContract.register`, `dat.Coverage.status`, `dat.Accumulation.snapshot`, `dat.Subject.export`, `dat.Erasure.execute`, `dat.Restriction.apply` | RAT, PFC, UW, RI, PLT, CMP | key | OK |
| MIG | `mig.Routing.resolve`, `mig.Xref.register/resolve`, `mig.Enquiry.get` | BIL, CHN, CLM, CMP, DOC, WRK, DAT, every import API | **T1, ≤ 50 ms** | OK |
| MKT | `mkt.Configuration.resolve/currentHash/explain`, `mkt.Spi.bind`, `mkt.Rounding.apply`, `mkt.Currency.*`, `mkt.RegimeCode.*`, `mkt.RiskLocation.resolve`, `mkt.CrossBorder.check`, `mkt.StatutoryClockSet.get`, `mkt.Capability.get`, `mkt.L10n.*` | every module | query | OK |
| MKT | SPI catalogue (**40 SPIs** incl. `StatutoryDataReturnFormat` `REQ-MKT-326`) | per contract §3.5.8 | per SPI | OK |

#### 8.5 DAT-only events (92) by producer
PTY 4, PFC 3, RAT 2, UW 1, POL 2, BIL 6, CLM 7, RI 14 (three carry money: DepositPremiumDue, PremiumAdjustmentCalculated, CommissionAdjusted → need FIN handlers F-132), FIN 6, DOC 6, CMP 17 ("intended consumers"; `AiSystemStatusChanged` needs PLT handler), CHN 7, WRK 5, PLT 4, DAT 5, MKT 3. Action: keep for analytics/evidence; trim producer consumer lists per R3-002.

### §9 Unified state models for cross-module entities (l.1701–2004)

#### 9.1 Method and result
Owner §7.3 state models vs contract §3.2.4, then every non-owner's use. Integration review found owners conformant; one systemic conflict (clock terminal semantics, CON-002) closed by **R-78**. PRD-18 confirms state *names* conform; problems are in **events carrying state changes** (consumer holds a state no producer feeds) and **four entities with no canonical model: Quote, PolicyTransaction, Reserve, Refund** (→ D-150 adds them to contract §3.2.4).

| Entity | Owner | Canonical in contract §3.2.4? | Non-owner use | Open issue |
|---|---|---|---|---|
| Quote (Job Draft/Quoted + QuoteVersion) | POL | Job only | conforms (CHN, RAT, UW use Job states; RAT price guarantee keyed to quote expiry) | F-202 |
| PolicyTerm | POL | Yes | conforms (MIG suspension fixed, CON-028) | F-204 |
| PolicyTransaction | POL | No | conforms | F-202 |
| Invoice / InvoiceItem | BIL | Yes | conforms (CMP, DOC, CHN) | — |
| Payment incoming (Receipt) | BIL | Yes | conforms | — |
| Refund | BIL | No | partly (POL clock stop depends on refund state) | F-202, F-235 |
| Disbursement ↔ ClaimPayment ↔ RI Settlement | BIL/CLM/RI | Disbursement yes | **No** — no BIL event for Rejected/Stopped/Cleared-to-CLM | F-200 |
| Claim / Exposure | CLM | Yes | conforms | — |
| Reserve (ReserveLine + reserve txns) | CLM | No (only TransactionSet) | conforms (RI ClaimView, FIN) | F-202 |
| TransactionSet | CLM | Yes | conforms (Posted from FIN `JournalPosted`) | — |
| Cession / RIRecovery | RI | Cession yes | ambiguous Posted → Reversed trigger | F-201 |
| Outbound document / Delivery | DOC | Yes | conforms (POL `DisclosureDeliveryView`, R-83) | — |
| FiscalDocument | CMP | Yes | conforms (BIL, CLM, DOC AwaitingFiscal) | — |
| Activity | WRK | Yes | conforms; UW Referral, FIN CloseTask, MIG CutoverTask, CMP DsarTask are owner-private linked lifecycles | — |
| Complaint | CMP | Yes | conforms | — |
| ClockInstance | CMP | Yes (R-78) | conforms (BIL, POL, CHN, CLM, WRK act on `ClockElapsed`; R2A-002) | §11 content issues |
| Governed configuration artefacts | various | ProductVersion only (+RatingArtifact by ref, R-23) | n/a | F-203 |

#### 9.2 Single models (all states and transitions)

**9.2.1 Quote (POL Job pre-bind + QuoteVersion), proposed canonical**
- [*] → **Draft** (SubmissionCreated / RenewalCreated / job created)
- Draft → **Quoted** (rate + UW evaluate pass; `QuoteIssued`)
- Quoted → Draft (edit → new QuoteVersion; previous Superseded)
- Quoted → **Bound** (bind gates pass; `PolicyBound` / `RenewalBound`)
- Quoted → **Scheduled** (future-effective cancellation or deferred renewal); Scheduled → Bound (effective time or acceptance); Scheduled → **Rescinded** (`CancellationRescinded`)
- Draft → **Withdrawn**, Quoted → Withdrawn (`JobWithdrawn`)
- Draft → **Declined**, Quoted → Declined (UW `DeclineIssued`)
- Quoted → **NotTaken** (customer declines / acceptance deadline; `JobNotTaken`)
- Draft → **Expired** (inactivity **90 days**, `BR-POL-016`); Quoted → Expired (validity end `QuoteExpired`; `BR-POL-015` per channel)
- Flags, not states: **Referred** (blocking UW issues open), **Preempted** (base not head; rebase). QuoteVersion states: Draft, Quoted, Superseded, Expired. Sub-states: Draft.QuickQuote; Draft.Converting (renewal); Quoted.Offered / Quoted.Accepted (renewal).
- Rules: a quote = (Job, QuoteVersion); only a Quoted version can be bound; RAT pins rating artefact until quote expiry (`REQ-RAT-058`); CHN never holds its own quote state (`REQ-CHN-041` drafts only); DOC renders DT-QUOTE from a Quoted version.

**9.2.2 PolicyTerm (POL), canonical**
- [*] → **Scheduled** (bind with effective > now); [*] → **InForce** (bind with effective ≤ now)
- Scheduled → InForce (start reached; **derived, no event** F-204)
- Scheduled → **Cancelled** (flat cancellation, rewrite, void; `PolicyCancelled`/`PolicyVoided`)
- InForce → **PendingCancellation** (`CancellationScheduled`); PendingCancellation → InForce (rescind before effective `CancellationRescinded`); PendingCancellation → Cancelled (effective reached `PolicyCancelled`)
- InForce → Cancelled (cancellation bound effective ≤ now; or void after objection/withdrawal `PolicyVoided`)
- Cancelled → InForce (reinstatement `PolicyReinstated`)
- InForce → **Expired** (end reached; outcome Renewed / NonRenewed / Lapsed / Rewritten); Expired → Expired (prior-term change `PolicyChanged`)
- Statutory gates: `BIL_NONPAY_NOTICE` Elapsed → BIL `CancellationForNonPaymentRequested` → POL cancellation (`REQ-POL-012`, `-311`). `POL_OBJECTION`, `POL_OBJECTION_INFO`, `POL_WITHDRAWAL_DISTANCE`, `POL_WITHDRAWAL_LONGTERM` Met with outcome Exercised → void (`REQ-POL-218`). **Suspension is a transaction kind, never a term state** (`REQ-POL-236`).

**9.2.3 PolicyTransaction (POL), proposed canonical**
- [*] → **Bound** (commit of a bound job: Issuance, Change, Cancellation, Reinstatement, Rewrite, Renewal, CarryForward, Suspension, Reactivation, Void)
- Bound → **Reversed** (a later Reversal transaction links to it; `TransactionReversed`; **row never updated**) → [*]
- Reapplications = new Bound rows linked by `reapplication_of_id` (`TransactionReapplied`). Every reversal & reapplication emits `ChargeDeltaEmitted` with the same correlation key (`REQ-POL-005`, `-121`). Consumers (BIL `REQ-BIL-002`, RI `REQ-RI-086`, FIN `REQ-FIN-036`, CLM `REQ-CLM-057`, DAT `REQ-DAT-042`) treat Reversed as derived from link events.

**9.2.4 Invoice and incoming payment (BIL), canonical**
- Invoice: [*] → **Planned** → **Billed** (invoice run; `InvoiceIssued`; fiscal trigger per `bil.fiscal.trigger_point`) → **Due**; Due → **Paid** (allocations = amount; `CashAllocated`); Due → **PartiallyPaid**; Due → **Overdue** (due + grace, open > tolerance; `DelinquencyStarted`); PartiallyPaid → Paid / Overdue; Overdue → Paid; Overdue → **WrittenOff** (`WriteOffPosted`); Paid → Due (payment reversed before due; `PaymentReversed`); Paid → Overdue (reversed after due); Billed → **Reversed** and Due → Reversed (credit note offsets in full; fiscal credit document 5.1/11.4).
- Receipt: [*] → **Received** (`PaymentReceived`) → **Allocated** / **PartiallyAllocated** / **Suspense**; PartiallyAllocated → Allocated; Suspense → Allocated / **Refunded**; Allocated → **Reversed** (`PaymentReversed`); Allocated → Refunded.
- `CollectionInstruction` (pre-receipt: card auth, instant payment initiation, SEPA submission): Planned → Submitted → Settled / Rejected / Expired; Settled → Returned; maps to no Receipt state until settlement evidence (`REQ-BIL-131`). CHN never infers payment success (`REQ-CHN-100`).

**9.2.5 Outgoing money — Disbursement (BIL)**
- [*] → **Requested** → **PendingApproval** (sanctions, VoP, duplicate, bank-change holds) → **Approved**; PendingApproval → **Rejected** (approver; *no event today*); PendingApproval → **Stopped** (*no event*); Approved → Stopped (*no event*); Approved → **Released** (batch release); Released → **Issued** (bank ack; `DisbursementIssued`); Released → Rejected (bank rejection; *no event*); Issued → **Cleared** (statement debit matched; `DisbursementCleared`); Issued → **Voided** (recall; `DisbursementVoided`); Issued → **Returned** (beneficiary bank return; `DisbursementReturned`); Cleared → Returned (late return).
- Mapping to source-module states:

| BIL Disbursement | BIL Refund | CLM ClaimPayment | RI Settlement | Event to source |
|---|---|---|---|---|
| Requested/PendingApproval/Approved | Disbursing | Submitted | Approved | sync response to `bil.Disbursement.request` |
| Rejected (approver or bank) | Returned → Proposed | **not mapped** (stays Submitted) | not mapped | **none** (F-200) |
| Stopped | n/a | Stopped (`REQ-CLM-126` relies on sync stop reply) | Cancelled | **none** for approver-initiated stop |
| Released | Disbursing | Submitted | Released | none needed |
| Issued | Disbursing (or Paid where pack treats issue as payment, `REQ-BIL-190`) | Issued → `PaymentIssued` | Released | `DisbursementIssued` (CLM, RI, FIN) |
| Cleared | Paid → `RefundDisbursed` | Cleared | Completed → `SettlementRecorded` | `DisbursementCleared` (RI, FIN only; CLM unfed, F-200) |
| Voided / Returned | Returned → Proposed | Voided/Returned → `PaymentVoided` | Failed | `DisbursementVoided`, `DisbursementReturned` |

Resolution (F-200/D-151): add `DisbursementRejected`, `DisbursementStopped`; CLM consumes `DisbursementCleared`; CLM maps Rejected → Returned path.

**9.2.6 Claim, exposure, reserve, transaction set (CLM)**
- Claim/Exposure: [*] → **Draft** (FNOL draft) → **Open** (FNOL submitted; `ClaimReported`, `ExposureCreated`) → **Closed** (last exposure closed; `ClaimClosed`; outcome Completed/Denied/Withdrawn/Duplicate/NoPayment); Closed → Open (reopen with reason `ClaimReopened`); Draft → Closed (discarded; Withdrawn, no number). Open sub-states New, InProgress, UnderInvestigation, Settled.
- ReserveLine (proposed): [*] → **OpenLine** (first reserve txn approved; `ReserveChanged`); OpenLine → OpenLine (reserve, eroding payment, recovery-reserve txns approved); OpenLine → **FinalLine** (final payment or release to zero; `final_flag`); FinalLine → OpenLine (claim/exposure reopened). Balance derived from immutable `ClaimFinancialTransaction` rows of kind Reserve, Payment, RecoveryReserve, Recovery (`REQ-CLM-003`, `-093…105`). RI (`ClaimView`, `REQ-RI-116`) and FIN (`REQ-FIN-158`) consume only approved deltas.
- TransactionSet: [*] → **Draft** → **Submitted** → **Approved** (authority allow; `TransactionSetApproved`, `ReserveChanged`, `RecoveryRecorded`); Submitted → **PendingApproval** (refer or four-eyes; `ApprovalRequested`) → Approved / **Rejected** (`TransactionSetRejected`; Expired sub-state); Approved → **Posted** (FIN `JournalPosted` with set reference).

**9.2.7 Cession and RI recovery (RI)**
- [*] → **Calculated** (`CessionCalculated` / `RecoveryCalculated`); Calculated → **Exception** (`CessionExceptionRaised`) → Calculated (resolved/retried); Calculated → **Posted** (FIN `JournalPosted` references item); Posted → **Statemented** (in issued statement or cash call; RIRecovery, TechnicalAccountItem) → **Settled** (`SettlementRecorded`); Posted → **Reversed** only when RI issues a linked correcting item AND FIN posts its reversal (F-201).

**9.2.8 Outbound document and fiscal document**
- OutboundDocument: [*] → **Requested** (`DocumentRequested`; sub-states Queued, AwaitingFiscal, AwaitingData) → **Rendering** → **Rendered** (`DocumentRendered`) / **Failed** (`DocumentRenderFailed`; Retrying, Final); Failed → Rendering (retry); Rendered → **Superseded** (`DocumentSuperseded`; Replaced, Withdrawn); Requested → Superseded.
- FiscalDocument (CMP): [*] → **Pending** → **Submitted** → **Registered** (MARK; `FiscalDocRegistered`; DOC leaves AwaitingFiscal); Submitted → **Rejected** (`FiscalDocRejected`); Pending → Rejected (local schema failure); Submitted → Pending (technical failure after negative duplicate check); Rejected → Pending (corrected); Registered → **Cancelled** (where pack allows); Rejected → Cancelled (source withdrawn, four-eyes).
- Delivery (DOC) separate: Pending → Sent → Delivered / Bounced / Failed. Statutory clocks start/stop on `DocumentDelivered` with required proof level (`StatutoryDeliveryRule`, R-41). **No clock may use `DocumentRendered` as proof of delivery.**

**9.2.9 Activity, complaint, clock instance**
- Activity (WRK): [*] → **Open** (`ActivityCreated`; sub-states New, InProgress, Waiting) → **Completed** / **Skipped** / **Cancelled**. Assignment state separate: Unassigned, Queued, Assigned.
- Complaint (CMP): [*] → **Received** (`ComplaintReceived`; ACK and REPLY clocks start) → **Acknowledged** → **UnderInvestigation** → **Answered** (`ComplaintAnswered`; REPLY clock Met) → **Closed**; Answered → **Escalated** (ADR or supervisor) → Closed.
- ClockInstance (CMP): [*] → **Running** (`ClockStarted`); Running ⇄ **Paused**; Running → **Warned** (`ClockWarned`); Running/Warned → **Met** (stopped early: Met, Cured, Exercised, Withdrawn); Running/Warned → **Breached** (DEADLINE kind only; `ClockBreached`); Running/Warned → **Elapsed** (WAITING_PERIOD kind only; `ClockElapsed`); Running → **Cancelled**; Breached → Breached (late completion, MetLate).

#### 9.3 Event coverage gaps
| Transition | Producer event | Needed by | Gap |
|---|---|---|---|
| Disbursement → Rejected / Stopped | none | CLM, BIL Refund, RI, FIN | F-200 |
| Disbursement → Cleared | `DisbursementCleared` (RI, FIN) | CLM, DAT | F-200 |
| PolicyTerm Scheduled → InForce | none (derived) | MIG `PolicyTermStatusView`, DAT `PolicyTermView`, CHN policy card | F-204 |
| Refund → Paid | `RefundDisbursed` on Cleared, or on Issued per pack (`REQ-BIL-190`) | POL (`POL_REFUND_DUE` stop), CHN, CMP | F-235 (key `bil.refund.paid_point`) |
| Cession Posted → Reversed | FIN `JournalPosted` of reversal | RI | F-201 |
| TransactionSet Approved → Posted | FIN `JournalPosted` (carries claim transaction set ref) | CLM (`REQ-CLM-113`) | none |

#### 9.4 Linked owner-private lifecycles & governed artefacts
- UW Referral (`REQ-UW-004`): Requested → Assigned → InReview ⇄ AwaitingInformation → Decided / Withdrawn (Assigned ↔ Activity Assigned; AwaitingInformation ↔ Open.Waiting; Decided ↔ Completed; Withdrawn ↔ Cancelled).
- PTY screening case (`REQ-PTY-176`): PotentialHit_New → UnderReview → PendingFalsePositive / Escalated → FalsePositive / TrueMatch (decision ↔ Completed).
- CMP DsarTask (`REQ-CMP-005`): Pending → InProgress → Completed / Failed / NotApplicable / Overdue.
- FIN CloseTask, ReconciliationBreak; RI CessionException; MIG DQIssue, CutoverTask: own states; resolution ↔ Completed; SLA from WRK (`REQ-WRK-009`). Link = `activity_ref`.
- Governed artefacts → `governance_stage` (Authoring / Under review / Approved-not-live / Live / Replaced / Withdrawn) mapping across PFC ProductVersion (Draft / Submitted / Approved / Locked {Active, ClosedToNewBusiness, RunOff} / Locked RunOff / Retired), RAT RatingArtifact (Draft, Validated / Submitted / Approved, Scheduled / Active / Superseded / Withdrawn, Retired), UW & FIN RuleSetVersion, DOC library (Draft / InReview / Approved / Published / Retired), WRK pattern, MKT ConfigChangeRequest / PackVersion (Draft/Built, Signed → Submitted/Certified → Approved, Scheduled/Published → Active → Superseded/Deprecated → Withdrawn/Removed), CHN PermissionMatrixVersion, PTY CommissionAgreementVersion, MIG MappingSpec. No renames; used as CMP evidence and DAT lineage dimension (F-203).

### §10 End-to-end money-flow map (l.2006–2136)

#### 10.1 Principles enforced
1. **Charges are the only bridge from policy to money**: POL emits `ChargeDeltaEmitted` per element × charge type × period (`REQ-POL-005`); BIL, RI, FIN, DAT consume the same charge ids; no module re-derives a premium.
2. **Two ledgers, no shared tables (CD-15)**: BIL billing sub-ledger on LA accounts via `BillingLedgerRule` (`REQ-BIL-286`, R-82); FIN books (IFRS17, SOLVENCY_II, LOCAL_GAAP where active) via `PostingRule` rule sets (`REQ-FIN-048…066`); reconciled daily (`REQ-BIL-011`, `REQ-FIN-244…249`).
3. **One posting source per fact (`BR-FIN-004`)**: premium, cash, refunds, commission, fees, taxes from BIL `BillingEntryPosted`; claims from CLM events; reinsurance from RI events; IBNR & risk adjustment from approved actuarial result sets; earning from FIN's own run over POL segments (`pol.Earning.compute`).
4. **Taxes and levies computed once, by one SPI**: RAT calls `TaxCalculator` after premium using Greece pack data (`REQ-RAT-009`, `-109`; `REQ-MKT-087`, `-257`, `-322`); POL, BIL, FIN, CMP, DAT carry lines and never recompute (`REQ-POL-124`; PRD-09 A-7; `REQ-CMP-035`). Violations: F-210, -211, -212.
5. **Cash moves only through BIL (CD-13)**: collections (`REQ-BIL-004`) and disbursements (`REQ-BIL-009`, `-197`). Four flows lack a BIL path (F-214, -215, -216).

#### 10.2 Map (mermaid in source) — edges
RAT (rates + tax lines) → POL; POL `ChargeDeltaEmitted` → BIL charge intake (`REQ-BIL-002`) and → RI; POL segments → FIN earning run; BIL charge intake → invoices/receipts/allocation (`REQ-BIL-004`) → commission (`REQ-BIL-008`, agreement version from PTY `REQ-PTY-010`); BIL charge intake → levy accrual/remittance (`REQ-BIL-269…274`); BIL invoices, commission, disbursement → `BillingEntryPosted` → FIN intake (`REQ-FIN-001`); levy `LevyAccrued`/`LevyRemitted` → FIN; CLM `ReserveChanged`/`PaymentIssued`/`RecoveryRecorded` → FIN; CLM claim payment request → BIL disbursement; CLM financial events → RI; RI `CessionCalculated`/`RecoveryCalculated` → FIN; RI `RI_SETTLEMENT` → BIL disbursement; BIL ⇄ Banks/SEPA; BIL levy remittance → Auxiliary Fund; BIL fiscal trigger → AADE myDATA; CLM settlement receipt → AADE; BIL settlements → reinsurers; FIN actuarial results & earning → FIN intake. **Broken (dotted):** FIN tax return → AADE IPT payment has no executor (F-216); CLM FS net settlement → FS clearing office has no BIL path (F-214); CMP redress → BIL disbursement not accepted (F-215/F-108).

#### 10.3 Flow register (all 35 flows)
LA = BIL sub-ledger account (PRD-06 §7.1.4); GL = FIN reference chart (PRD-09 §7.1.4).

| # | Flow | Origin | Carrier | BIL sub-ledger (rule BIL) | FIN books (rule FIN) & source | Cash executor | Fiscal doc (CMP) | Reconciliation | Status |
|---|---|---|---|---|---|---|---|---|---|
| M-01 | Written premium | RAT (`REQ-RAT-001`) → POL delta (`REQ-POL-005`) | ChargeDelta premium | LA-01 Dr / LA-04 Cr (`REQ-BIL-066`) | GL-1210/1220 Dr / GL-2110 LRC Cr (`REQ-FIN-036`, `-066`) from `BillingEntryPosted` | — | per `bil.fiscal.trigger_point` (`REQ-BIL-096`, `REQ-CMP-030`) | POL↔BIL (`REQ-POL-122`); BIL↔FIN (`REQ-FIN-244`) | OK; DAT GWP from deltas (F-217) |
| M-02 | Earned/unearned premium | `pol.Earning.compute` (`REQ-POL-117`, `-118`) | segments | — | earning run (`REQ-FIN-003`, `-104…120`) | — | — | invariant earned + unearned = written | OK |
| M-03 | Policy fee (flat) | PFC charge type (`REQ-PFC-126`) → RAT (`REQ-RAT-111`) → POL | ChargeDelta fee | LA-01 / LA-05 | from BIL entry (`REQ-FIN-118`) | — | yes | as M-01 | OK; in IPT base (`REQ-PFC-117`) |
| M-04 | Instalment, dishonour, late fees | BIL charges (`REQ-BIL-050`, `-081`, `-152`) | BIL charge id | LA-01 / LA-05 | from BIL entry | — | yes (`REQ-BIL-081`) | BIL↔FIN | **Gap: no IPT computed (F-211)** |
| M-05 | IPT | RAT → `TaxCalculator` (`REQ-RAT-009`, `-109`; `REQ-MKT-087`) | ChargeDelta tax (IPT class) | LA-06 accrued at pack liability point (`REQ-BIL-275`) | GL-2410 (`REQ-FIN-178…188`) | **none (F-216)** | other-tax line (`REQ-CMP-035`) | BIL↔FIN; DAT IPT mart (`REQ-DAT-129…131`) | liability point open (F-212); refundability in four places (F-210) |
| M-06 | IPT on cancellation & endorsement credits | POL applies PFC treatment (`REQ-POL-215`) | ChargeDelta | BIL quarantines negative IPT delta on cancellation (`REQ-BIL-079`, `-183`) | FIN suspends (`REQ-FIN-182`); endorsement credits per pack REDUCE/NOT_REDUCE (`REQ-FIN-183`) | — | credit document | — | F-210, F-218 |
| M-07 | Aux Fund levy policyholder share `GR-AUXF-PH` | RAT → `TaxCalculator` (`REQ-PFC-124`, `REQ-MKT-322`) | ChargeDelta levy, billed | LA-01 / LA-07 | GL-2420 (`REQ-FIN-189…196`) | BIL remittance disbursement (`REQ-BIL-272`) | shown on policy | `LevyAccrued` vs written MTPL premium (`REQ-BIL-274`) | split open (F-213) |
| M-08 | Aux Fund levy insurer share `GR-AUXF-INS` | `TaxCalculator` (`REQ-PFC-125`) | ChargeDelta levy, ACCRUE_ONLY | LA-08 Dr / LA-07 Cr (`REQ-BIL-068`) | GL-6150 Dr / GL-2420 Cr | as M-07 | no | as M-07 | F-213 |
| M-09 | Stamp duty on PH levy share `GR-STAMP-AUXF-PH` | `TaxCalculator` (`REQ-PFC-244`) | ChargeDelta tax | pack-defined payable | GL-2425 (OI-FIN-02) | none stated | pack | — | rate UNVERIFIED; executor as F-216 |
| M-10 | Levy remittance to Fund | BIL period close (`REQ-BIL-272`; clock `BIL_AUXF_REMIT`) | TaxLevyPeriod (R-82) | LA-07 Dr / LA-13 Cr, then LA-10 | from BIL entry; `LevyRemitted` settles LevyReturn (`REQ-FIN-194`) | BIL disbursement | no | — | OK |
| M-11 | Billing, collection, allocation, suspense | BIL (`REQ-BIL-004`, `-071`, `-131…140`) | Invoice, Receipt | LA-02, LA-09/10, LA-11 | from BIL entry | BIL | receipt per pack | bank↔BIL | OK |
| M-12 | Pre-bind deposit / down payment | BIL (`REQ-BIL-055`, `-056`); bind gate `DownPaymentCleared` (`REQ-BIL-003`) | Receipt to suspense | LA-10 / LA-11 | from BIL entry | BIL | — | — | OK |
| M-13 | Payment reversals, chargebacks | BIL (`REQ-BIL-147…160`) | Reversal | exact negation | from BIL entry | BIL | credit where pack requires | — | OK |
| M-14 | Cancellation credits & refunds | POL refund method (`REQ-POL-206`; `REQ-PFC-134`) → BIL refund (`REQ-BIL-007`, `-181…196`) | negative ChargeDelta → Refund | LA-04 Dr / LA-01 Cr; LA-02 Dr / LA-12 Cr; LA-12 Dr / LA-13 Cr | from BIL entry | BIL disbursement | credit 5.1 / 11.4 (`REQ-CMP-032`) | `POL_REFUND_DUE` | OK; clock-stop key unnamed (F-235) |
| M-15 | Distance-withdrawal refund (full premium) | POL void (`REQ-POL-308`, R-55); `PolicyLifecycleRules` FullRefund | as M-14 | as M-14 | as M-14 | BIL | credit | `POL_REFUND_DUE` DistanceWithdrawal variant | citation fix (F-235); tax treatment F-401 |
| M-16 | Write-off & later recovery | BIL (`REQ-BIL-217…222`) | WriteOff | LA-16 | from BIL entry | — | — | — | OK |
| M-17 | Commission calc, accrual, payable, chargeback | PTY agreement (`REQ-PTY-010`) → BIL (`REQ-BIL-008`, `-249…268`) | CommissionCalculation | LA-15 Dr / LA-14 Cr | acquisition cash flows GL-2310/2315 (`REQ-FIN-171…177`); incurred basis uses BIL accrued lines (`REQ-BIL-252`) | BIL payment run | self-billing (`REQ-CMP-034`) | BIL↔FIN | OK; calculated once in BIL |
| M-18 | Agency bill & account current | BIL (`REQ-BIL-010`, `-233…248`) | AccountCurrent | LA-03, LA-14 | from BIL entry (GL-1220) | intermediary remittance into BIL | — | — | OK |
| M-19 | Claim reserves | CLM (`REQ-CLM-003`, `-093…105`) | ClaimFinancialTransaction Reserve | — | GL-5110 / GL-2210 from `ReserveChanged` (`REQ-FIN-037`, `-158`) | — | — | CLM↔FIN (`REQ-CLM-106`) | OK |
| M-20 | Claim payments (incl. vendor invoices, recurring) | CLM (`REQ-CLM-004`, `-119…142`, `-196…199`) | Payment txn → Disbursement | LA-17 Dr / LA-13 Cr, then LA-10 | GL-2210 → GL-2510 clearing from `PaymentIssued`; cash side from BIL (`REQ-FIN-159`, `-249`) | BIL | settlement receipt (R-43, `REQ-CMP-033`) | CLM↔BIL↔FIN | state-propagation gaps (F-200) |
| M-21 | Deductible & salvage receipts | CLM recovery (`REQ-CLM-141`, `-145`, `-146`) → `bil.Receivable.register` | Recovery + BIL receivable | LA-02 | recovery accounts from `RecoveryRecorded` (`REQ-FIN-160`) | BIL collection | per pack | — | OK |
| M-22 | Subrogation | CLM (`REQ-CLM-143…151`) | Recovery | via BIL receipt | `RecoveryRecorded` | BIL collection | — | — | OK |
| M-23 | FS receivable (we pay our customer, recover at clearing value) | CLM (`REQ-CLM-157`), Greece pack | Payment + RecoveryReserve type FriendlySettlement | LA-17 for indemnity | `PaymentIssued`, `ReserveChanged`, `RecoveryRecorded` | BIL for indemnity | receipt | FS statement (`REQ-CLM-160`) | net settlement no cash path (F-214) |
| M-24 | FS payable & monthly net settlement | CLM (`REQ-CLM-158`, `-160`) | Payment method Clearing, no disbursement | **none** | FIN posts payment but GL-2510 never nets → break after 2 days (`REQ-FIN-249`, `BR-FIN-096`) | **none** | — | — | **F-214** → `FS_CLEARING` |
| M-25 | Aux Fund & Green Card recoveries/recourse | CLM (`REQ-CLM-161…164`) via `MotorCompensationBodyAdapter` | Recovery/Payment | via BIL | `RecoveryRecorded` (`REQ-FIN-160`, `-170`) | BIL | — | — | OK |
| M-26 | Statutory interest on late MTPL offer | CLM (`REQ-CLM-169`; cost type StatutoryInterest) | Payment | LA-17 | from `PaymentIssued` | BIL | receipt | — | depends on clock start (F-230) |
| M-27 | Ceded premium & ceding commission | RI on charge deltas (`REQ-RI-002`, `-075…088`; RI-cedable flag `REQ-PFC-122`) | Cession | — (BIL carries cash only) | GL-1310 / GL-2320 / GL-5210 from `CessionCalculated` (`REQ-FIN-038`, `-141`, `-163`) | BIL (RI_SETTLEMENT) | no (Greece: no RI premium tax) | RI↔FIN daily (`REQ-RI-236`) | OK; proportional cession is P2 (CON-019) |
| M-28 | RI recoveries (prop., XoL, cat) | RI (`REQ-RI-003`, `-116…145`) | RIRecovery | — | GL-1320 / GL-4210 from `RecoveryCalculated` (`REQ-FIN-142`, `-164`) | BIL receipt from reinsurer (`REQ-BIL-349`) | no | RI↔FIN | OK |
| M-29 | Reinstatement, deposit, adjustment premiums; sliding-scale & profit commission | RI (`REQ-RI-146…166`) | TechnicalAccountItem | — | from RI events (`REQ-FIN-165`) | BIL | no | statement = items = journals (`REQ-RI-175…186`) | **P3** (but see F-132: deposit premium needed P1) |
| M-30 | RI settlements & cash calls | RI (`REQ-RI-187…196`) → BIL (`REQ-BIL-348…351`) | Settlement | LA-22, LA-23 | from BIL entry | BIL | no | — | OK |
| M-31 | IBNR, risk adjustment, loss component, discounting | DAT result set (`REQ-DAT-004`) → FIN approval (`REQ-FIN-009`, `-273…279`) | ActuarialResultSet | — | FIN | — | — | — | OK |
| M-32 | FX revaluation & USD group translation | FIN (`REQ-FIN-006`, `-202…211`); rates PLT (`REQ-PLT-009`) | FxRevaluationRun | three amounts on BIL lines (`REQ-BIL-294`) | FIN | — | — | DAT USD feeds (`REQ-DAT-138…141`) | OK |
| M-33 | Complaint redress / goodwill | CMP (`REQ-CMP-134`) | redress request | **none** (BIL accepts BIL, CLM, RI only `REQ-BIL-197`) | **no rule** | — | — | — | **F-215/F-108** → `CMP_REDRESS` |
| M-34 | IPT payment to tax authority | FIN return Approved (`REQ-FIN-180`, `-187`) | TaxReturn | **none** | FIN "tax payment journal" (PRD-09 l.1021) | **none** | — | bank debit unmatched in BIL | **F-216** → `TAX_REMITTANCE` |
| M-35 | Migration opening balances | MIG via import APIs (`REQ-MIG-115`, `-118`, `-129`, `-136`) | `origin=MIGRATION` | BIL opening balances (`REQ-BIL-012`, `-290`) | FIN opening balances (`REQ-FIN-010`, `-281…285`) | — | legacy MARK hand-over (`REQ-CMP-249`) | MIG control totals at **€0.00 tolerance** (`REQ-MIG-172`) | OK |

New BIL disbursement sources implied by resolutions: `FS_CLEARING` (D-255), `CMP_REDRESS` (D-102), `TAX_REMITTANCE` (D-156); existing sources BIL, CLM, RI (`RI_SETTLEMENT`).

LA accounts named in §10: LA-01, LA-02, LA-03, LA-04, LA-05, LA-06, LA-07, LA-08, LA-09/10, LA-11, LA-12, LA-13, LA-14, LA-15, LA-16, LA-17, LA-22, LA-23 (contract/BIL range LA-01…LA-23). GL accounts named: GL-1210, 1220, 1310, 1320, 2110 (LRC), 2210, 2310, 2315, 2320, 2410 (IPT), 2420 (levy), 2425 (stamp on levy), 2510 (claims clearing), 4210, 5110, 5210, 6150.

#### 10.4 Taxes and levies (verbatim-precise values stated)
| Step | IPT | Auxiliary Fund levy (PH & insurer) | Stamp duty on PH levy share | Verdict |
|---|---|---|---|---|
| Rate & base | **Greece pack: 15% general, 20% fire. Base: "απαιτητά ασφάλιστρα" and contract rights of every kind (Law 5177/2025 Art. 43, verified 2026-10-07)** | **6% ceiling; 70% insurer / 30% insured (Law 5113/2024 Art. 14, verified 2026-10-07)**; how 70/30 applies to components open | pack rate UNVERIFIED (OI-FIN-02) | values are pack data (`REQ-MKT-087`, `-322`) |
| Computation | RAT → `TaxCalculator` after premium (`REQ-RAT-109`); recomputed on prorated amounts in amount mode (`REQ-RAT-162`) | same | same (`REQ-PFC-244`) | once by one SPI, POL charges only; BIL fees untaxed (F-211); BIL's receipt-stamp call (`REQ-BIL-277`) is a legitimate second caller |
| Refundability & credits | four definitions, three keys (PFC `gr.ipt.refund_on_cancel`; POL `BR-POL-041`; BIL `REQ-BIL-079`, `-183`; FIN `REQ-FIN-182`, `-183` `tax.ipt.*` REDUCE/NOT_REDUCE) | PFC treatment; OI-BIL-03, OI-POL-08 open | — | F-210 |
| Liability point | `tax.ipt.liability_point` WRITTEN default (`REQ-MKT-328`); BIL accrues (`REQ-BIL-275`), FIN posts (`REQ-FIN-178`) | written basis irrespective of collection (law) | — | default contradicts statute "due" (F-212) |
| Billing | BIL bills tax lines received | PH share billed; insurer share ACCRUE_ONLY from PFC `accrued_not_billed` (CON-024) | billed | consistent |
| Fiscal docs | other-tax category via PFC fiscal-category key (`REQ-PFC-121`, `REQ-CMP-035`); CMP never derives a rate | PH share shown on policy | pack | consistent |
| Finance | GL-2410; **quarterly** return under clock `FIN_IPT_RETURN` (`REQ-FIN-180`) | GL-2420 / GL-6150; remittance under `BIL_AUXF_REMIT` | GL-2425 | payment executor missing (F-216) |
| Reporting | DAT IPT report (`REQ-DAT-129…131`; optional pending OI-FIN-04) | DAT levy views reconcile to `LevyAccrued`/`LevyRemitted` | — | consistent |
| Reinsurance | **never ceded** (`REQ-RI-075`, `BR-RI-025`) | never ceded | never ceded | consistent |

### §11 Statutory and business clock register (l.2139–2252)

#### 11.1 Rules
- **List of record = PRD-11 §10.5 (R-60).** Every row: one owner (module that starts/stops), one value source (`StatutoryClockSet`, MKT `REQ-MKT-009`, `-285…292`), one workflow (CMP engine `REQ-CMP-003`; MIRROR rows run in PLT under R-61), one kind (R-78: DEADLINE → Met/Breached; WAITING_PERIOD → Met/Elapsed).
- Result: **33 of 38 rows consistent**; 5 rows with content defects (F-230, -232, -233, -235, -236); 5 statutory periods outside register (F-231); 1 business control with two values (F-234).
- Verification: **V** = verified vs primary/official secondary source 2026-10-07; **P** = peer-verified in PRDs and consistent; **U** = UNVERIFIED.

#### 11.2 Statutory clock register (38 rows, verbatim-precise)

| # | Code | Kind | Owner (start/stop) | Start | Stop | Greece value | Workflow | Legal source | Ver. | Finding / note |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | `POL_OBJECTION` | WAITING_PERIOD | POL (`REQ-POL-305`) | delivery of a policy that deviates from the application (`DocumentDelivered`, proof per `StatutoryDeliveryRule`) | objection received (Met, outcome Exercised) or Elapsed | **1 month** | CMP | Law 2496/1997 Art. 2 §5 | P (OI-MKT-14 open) | POL, MKT, CMP, CHN agree |
| 2 | `POL_OBJECTION_INFO` | WAITING_PERIOD | POL (`REQ-POL-306`) | delivery without prior information | Exercised or Elapsed | **14 days; hard stop 10 months after first premium** | CMP | Art. 2 §6 | P | agree |
| 3 | `POL_WITHDRAWAL_DISTANCE` | WAITING_PERIOD | POL (`REQ-POL-308`); CHN reads (`REQ-CHN-161`) | later of conclusion and receipt of terms | Exercised or Elapsed | **14 calendar days** (EU minimum, CONSTRAINED) | CMP | Directive (EU) 2023/2673; Law 5317/2026 Art. 71 (new Art. 3ιε of Law 2251/1994) | V | CHN long stop (12 months + 14 days when terms never received) missing → F-231 |
| 4 | `POL_WITHDRAWAL_LONGTERM` | WAITING_PERIOD | POL (`REQ-POL-309`) | receipt of policy | Exercised or Elapsed | **14 days** | CMP | Law 2496/1997 Art. 8 (paragraph U, OI-POL-03) | U | agree |
| 5 | `POL_REFUND_DUE` | DEADLINE | POL starts (`REQ-POL-219`); event stop on BIL `RefundDisbursed` | DistanceWithdrawal variant: receipt of withdrawal declaration (CHN `confirmedAt`); other sources: the void or cancellation | `RefundDisbursed` | **30 calendar days** | CMP | Law 2251/1994 Art. 3ιστ (inserted by Law 5317/2026 Art. 72) for distance withdrawal; others U (OI-POL-04) | V (distance) | CMP/POL cite Art. 4θ §5, CHN Art. 3ιστ; stop fires on Cleared or Issued by unnamed pack choice (`REQ-BIL-190`) → F-235 |
| 6 | `POL_RENEWAL_NOTICE` | DEADLINE | POL (`REQ-POL-250`, `-318`) | "renewal offered" | "term start" | pack (U) | CMP | pack-defined | U | cannot detect late offer → F-233 |
| 7 | `POL_NONRENEWAL_NOTICE` | DEADLINE | POL (`REQ-POL-251`) | non-renewal decision | notice delivered | pack (U) | CMP | pack-defined | U | measured from decision not expiry → F-233 |
| 8 | `BIL_NONPAY_NOTICE` | WAITING_PERIOD | BIL (`REQ-BIL-006`, `-169…172`) | proven notification of statutory notice | Cured (Met) or Elapsed → `CancellationForNonPaymentRequested` | **1 month from notification (K-01)** | CMP | Law 2496/1997 Art. 6 | V | BIL, POL, DOC, MKT, MIG, WRK agree; prompt's "two weeks" superseded |
| 9 | `BIL_AUXF_REMIT` | DEADLINE | BIL (`REQ-BIL-272`); FIN links LevyReturn (`REQ-FIN-192`) | end of each **calendar two-month period** | remittance disbursed (`LevyRemitted`) | **15 days after period end** | CMP | Law 5113/2024 Art. 14 | P (statute confirms 6% ceiling, 70/30; 15 days peer-verified) | agree (K-05) |
| 10 | `CLM_MTPL_OFFER` | DEADLINE | CLM (`REQ-CLM-007`, `-165…169`) | claim received, per third-party exposure (`REQ-CLM-036`) | proven delivery of reasoned offer or reply | **3 months** | CMP | Directive 2009/103/EC Art. 22; P.D. 237/1986 Art. 6 §6; BoG Act 87/2016 Art. 3 | V | agree |
| 11 | `CLM_MTPL_ASSESSMENT` | DEADLINE | CLM (`REQ-CLM-191`) | submission of any Art. 4 document | assessment completed (stop not stated in PRD-07) | **15 days in Greece / 25 days abroad** | CMP | Act 87/2016 Art. 5 | V | stop missing → F-237 |
| 12 | `CLM_MTPL_PAYMENT_DUE` | DEADLINE | CLM (`REQ-CLM-167`) | **offer acceptance** (as written in CLM, CMP, MKT) | payment issued (stop not stated) | **10 days** | CMP | Act 87/2016 Art. 6: "δεν επιτρέπεται να υπερβαίνει τις δέκα ημέρες από την προσφορά" | V | start contradicts source → F-230, F-237 |
| 13 | `CLM_FS_COUNTERPARTY_REPLY` | WAITING_PERIOD | CLM (`REQ-CLM-159`) | dispute or request sent | reply received or Elapsed → FS escalation | **10 business days** | CMP | FS market agreement | U (OI-CLM-02) | agree |
| 14 | `CLM_HISTORY_STATEMENT` | DEADLINE | CLM (`REQ-CLM-008`, `-170`) | request recorded | `ClaimsHistoryCertificateIssued` | **15 days** | CMP | Law 5113/2024 Art. 7; Directive 2009/103/EC as amended by (EU) 2021/2118 | P (K-06) | agree |
| 15 | `UW_NATCAT_RESPONSE` | DEADLINE | UW (`REQ-UW-224`, `-179`; P3) | request received from in-scope business | offer or refusal document delivered | **30 days; silence = refusal** | CMP | Law 5116/2024; JMD 96806/2025 | U (OI-UW-01) | agree |
| 16 | `UW_NONDISCLOSURE_ACTION` | DEADLINE | UW (`REQ-UW-271`) | discovery | decision recorded | **1 month** | CMP | Law 2496/1997 Art. 3(3) | U (OI-UW-02) | downstream 15-day termination effect and 1-month amendment acceptance not registered → F-231 |
| 17 | `UW_AGGRAVATION_ACTION` | DEADLINE | UW (`REQ-UW-271`; `REQ-POL-312`) | notification | decision recorded | **1 month** | CMP | Art. 4(2) | U | as row 16 → F-231 |
| 18 | `FIN_IPT_RETURN` | DEADLINE | FIN (`REQ-FIN-180`) | quarter end | `TaxReturnFiled` | **end of third month after quarter end (Q1→June, Q2→September, Q3→December, Q4→March)** | CMP | Law 5177/2025 Art. 43 §6 | V | payment executor F-216 |
| 19 | `FIN_LEVY_RETURN` | DEADLINE | FIN | period end | `TaxReturnFiled` | **not bound in Greece (K-05)** | CMP | pack | — | agree |
| 20 | `CMP_COMPLAINT_ACK` | DEADLINE | CMP (`REQ-CMP-126`) | `ComplaintReceived` | acknowledgement delivered | **proposed 5 business days** | CMP | Act 88/2016 / EIOPA guidelines (period not found) | U | agree |
| 21 | `CMP_COMPLAINT_REPLY` | DEADLINE | CMP (`REQ-CMP-126`, `-135`) | `ComplaintReceived` | reply delivered (`REQ-DOC-253`) | **50 calendar days** | CMP | BoG Act 88/2016 | P (REG-010 resolved) | agree |
| 22 | `CMP_SUPERVISOR_REFERRAL` | DEADLINE | CMP (`REQ-CMP-138`, **Should**) | referral received | reply filed | fixed date given at start | CMP | as stated in referral | n/a | F-238 |
| 23 | `CMP_COMPLAINT_STATS` | DEADLINE | CMP (`REQ-CMP-146`) | period end | filed | U (OI-CMP-10) | CMP | BoG | U | — |
| 24 | `CMP_DSAR_RESPONSE` | DEADLINE | CMP (`REQ-CMP-153`) | receipt, or identity verification where pack says so | response delivered | **1 month, extendable by 2 months** | CMP | GDPR Art. 12(3) | V | PTY, DAT, MIG, WRK sub-deadlines fit inside |
| 25 | `CMP_GDPR_BREACH_NOTIFY` | DEADLINE | **CMP engine (`REQ-CMP-174`) and PLT timer (`REQ-PLT-306`, `BR-PLT-036`)** | awareness | notification sent or "not required" recorded | **72 hours** | **two workflows** | GDPR Art. 33 | V | double-timed vs CD-07, R-61 → F-232 |
| 26 | `CMP_FISCAL_TRANSMISSION` | DEADLINE | CMP (`REQ-CMP-057`, **Should**) | document issue | Registered | U (OI-CMP-03) | CMP | myDATA framework | U | F-238 |
| 27 | `CMP_BUREAU_REPORT` | DEADLINE | CMP (`REQ-CMP-082`) | insured-vehicle fact created | acknowledged | **provisional 48 h breach / 24 h warning** | CMP | Law 5113/2024 Art. 15 (ministerial decision U) | U | MIG hand-over agrees (`REQ-MIG-162`) |
| 28 | `CMP_A1004_RETURN` | DEADLINE | CMP (`REQ-CMP-177`, P2) | reference-year end | submitted | **10 January** | CMP | A.1004/2024 as amended by A.1185/2024 | P (K-09, R-85) | DAT aligned (CON-014) |
| 29 | `CMP_ENFIA_CONFIRM` | DEADLINE | CMP (`REQ-CMP-188`, P2) | request window opens | transmitted | **28 February (confirmed yearly, K-10)** | CMP | ENFIA decision | P | — |
| 30 | `CMP_EAEE_STATS` | DEADLINE | CMP (`REQ-CMP-190`, Should) | period end | delivered | U | CMP | market agreement | U | — |
| 31 | `CMP_SII_QRT_QUARTERLY` | DEADLINE | CMP tracker (`REQ-CMP-196`); content DAT | quarter end | filed | **5 weeks (solo)** | CMP | Delegated Reg. (EU) 2015/35 Art. 312 | V for 2026 cycle; may change under Directive (EU) 2025/2 from 30 Jan 2027 | F-255 |
| 32 | `CMP_SII_ANNUAL` | DEADLINE | CMP tracker | year end | filed | **14 weeks (solo)** | CMP | as above | V (2026) | F-255 |
| 33 | `CMP_SII_SFCR` | DEADLINE | CMP tracker | year end | published and filed | **14 weeks** | CMP | as above | V (2026) | F-255 |
| 34 | `CMP_SUPERVISOR_QUERY` | DEADLINE | CMP (`REQ-CMP-202`, **Should**) | question received | answer filed | fixed date given at start | CMP | as stated | n/a | F-238 |
| 35 | `PLT_DORA_INITIAL` | DEADLINE | PLT (`REQ-PLT-303`); CMP MIRROR (`REQ-CMP-115`, Should) | classification as major | initial notification submitted | **"earlier of classification + 4 h and awareness + 24 h"** | PLT (MIRROR) | Delegated Reg. (EU) 2025/301 | V | formula breaks when classification > 24 h after awareness → F-236 |
| 36 | `PLT_DORA_INTERMEDIATE` | DEADLINE | PLT; CMP MIRROR | initial notification | intermediate report submitted | **72 h** | PLT | as above | V | agree |
| 37 | `PLT_DORA_FINAL` | DEADLINE | PLT; CMP MIRROR | latest intermediate report | final report submitted | **1 month** | PLT | as above | V | agree |
| 38 | `PLT_DORA_ROI` | DEADLINE | PLT (`REQ-PLT-315`); CMP tracker (`REQ-CMP-204`) | reference date 31 December | filed | U (OI-CMP-09) | PLT | Implementing Reg. (EU) 2024/2956 | U | — |

#### 11.3 Proposed register changes (D-157, D-158)
| Change | Code | Kind | Owner | Start → stop | Greece value (status) | Source | Finding |
|---|---|---|---|---|---|---|---|
| New | `CLM_REPAIR_IN_KIND` | DEADLINE | CLM (`BR-CLM-022`) | repair agreement → repair completed | **20 days (V)** | Act 87/2016 Art. 6 | F-231 |
| New | `POL_INSURER_TERMINATION_NOTICE` | WAITING_PERIOD | POL, UW as requester (`REQ-UW-272`), pack rule `PolicyLifecycleRules` | termination notice delivered → Elapsed = effective date | **15 days for non-disclosure and aggravation (U, OI-UW-02); others per pack** | Law 2496/1997 Arts 3–4 | F-231 |
| New | `UW_AMENDMENT_ACCEPTANCE` | WAITING_PERIOD | UW (`REQ-UW-272`) | amendment proposal delivered → accepted (Met) or Elapsed = deemed termination | **1 month (U)** | Law 2496/1997 Art. 3 | F-231 |
| New, if OI-BIL-02 confirms | `POL_MTPL_THIRDPARTY_NOTICE` | WAITING_PERIOD | POL (`REQ-BIL-173` hand-over; `REQ-POL-311`) | insurer notice of termination → third-party cover ends | **16 days (U)** | P.D. 237/1986 Art. 11a | F-231 |
| Amend row 3 | `POL_WITHDRAWAL_DISTANCE` | — | POL | add variant "terms never received": **long stop 12 months + 14 days** | V | Law 5317/2026 Art. 71 | F-231 |
| Amend row 5 | `POL_REFUND_DUE` | — | POL / BIL | stop event = `RefundDisbursed` raised at pack key **`bil.refund.paid_point` (ISSUED by default)** | — | Law 2251/1994 Art. 3ιστ | F-235 |
| Amend rows 6, 7 | `POL_RENEWAL_NOTICE`, `POL_NONRENEWAL_NOTICE` | DEADLINE with FIXED_DATE | POL | start when renewal job created (or non-renewal decided); **deadline = term expiry − pack lead**; Met on `DocumentDelivered` of offer/notice; Breached → consequence from pack (renew on existing terms or extend) | U | pack | F-233 |
| Amend row 12 | `CLM_MTPL_PAYMENT_DUE` | — | CLM | **start at the reasoned offer (delivery proof of `StatutoryOfferIssued`); stop at `PaymentIssued`** | 10 days (V) | Act 87/2016 Art. 6 | F-230 |
| Amend row 25 | `CMP_GDPR_BREACH_NOTIFY` | — | CMP engine only; PLT starts/stops via `REQ-CMP-003`, removes own timer | — | 72 h | GDPR Art. 33 | F-232 |
| Amend row 35 | `PLT_DORA_INITIAL` | — | PLT | **deadline = classification + 4 h, capped at awareness + 24 h only while classification happens within 24 h of awareness**; apply pack weekend/holiday rule where authority allows | V | Del. Reg. (EU) 2025/301 Art. 5 | F-236 |

Also implied (F-237): `CLM_MTPL_ASSESSMENT` stop on assessor report accepted.

#### 11.4 Business clock register (cross-module timers)
| Timer | Owner & value | Relying modules | Consistent? |
|---|---|---|---|
| Quote validity | POL `pol.quote.validity_days`: **30 days (staff and intermediary), 7 (web, app), 1 (aggregator, partner API)** (`BR-POL-015`) | CHN drafts (`BR-CHN-016`, earlier of 30 days or quote validity); RAT price guarantee (`REQ-RAT-058`) | Yes |
| Renewal lead / offer | POL **45 days motor, 60 days home** (`BR-POL-026`) | MIG offer lead 45 days (`BR-MIG-010`); UW pre-renewal window **P75D** (`REQ-UW-262`); RAT & PFC impact horizon **120 days** | Yes (statutory floor `POL_RENEWAL_NOTICE` U) |
| Invoice grace | BIL **5 days** (`bil.invoice.grace_days`) | delinquency start; CHN | Yes |
| Payee bank-account change cooling-off | **BIL `cooling_off_until`, default 30 days (`REQ-BIL-199`, `-343`)** | **CLM `BR-CLM-011` 72 hours**; PLT SoD `BR-PLT-005(e)` | **No (F-234)** |
| Pre-bind deposit holding | BIL **2 business days** (`BR-BIL-011`) | POL `JobNotTaken` | Yes |
| Reinstatement without gap | POL **30 days for non-payment, 0 for insurer** (`BR-POL-037`) | BIL delinquency (`REQ-BIL-158`) | Yes |
| Unmerge window | PTY **90 days** (`BR-PTY-009`) | POL, BIL, CLM re-pointing | Yes |
| Approval validity | PLT **5 business days** default (`BR-PLT-016`) | RAT & PFC 30-day re-validation before scheduling (`BR-RAT-031`, `BR-PFC-042`) | Yes (different objects) |
| Idempotency retention | contract **≥ 7 days** | CHN partner commands **30 days** (`BR-CHN-040`) | Yes |
| Reconciliation timing | FIN daily **by 07:00** (`NFR-FIN-011`); BIL→FIN **by 06:00** (`NFR-BIL-021`); CLM **by 06:00** (`NFR-CLM-019`) | FIN close cockpit | Yes (RI none — F-330) |
| Claim payment without BIL disbursement → break | FIN **2 days** (`BR-FIN-096`) | CLM FS payables (`REQ-CLM-158`) | **Conflicts** (F-214) |
| AI kill-switch effect | **≤ 60 s** (contract §3.8.2) | every module | Yes |

Also stated elsewhere in §1–15: Draft quote inactivity expiry 90 days (`BR-POL-016`); config caches refreshed within 10–60 s; incomplete event sets older than a threshold → exception activity (threshold not given).

#### 11.5 Bitemporal consistency and XMR-FR-150
Module conventions: POL `valid_period` (tstzrange) / `record_period`, transaction `recorded_at`, half-open [start, end), legal-entity zone, `validAt`/`knownAt` (`REQ-POL-002`); RAT stateless, start inclusive end exclusive (`REQ-RAT-157`); PTY `valid_from`/`valid_to`, row version time, convention not stated; RI `valid_from`/`valid_to` + `known_from`/`known_to`; CLM not bitemporal, stores snapshot ref `snapshot_known_at`, uses POL `validAt`/`knownAt` (`REQ-POL-007`); FIN JournalEntry `accounting_date`, `business_date`, `posted_at`, BalanceSnapshot as-of record time; DOC Binding/FormPattern `effective_from`/`effective_to` inclusive/exclusive (`REQ-DOC-099`); MKT ConfigValue effective period + activation instant (retro changes approved separately, `cfg.retro.max_days`); DAT silver/gold `valid_from`/`valid_to` + `recorded_from`/`recorded_to`. Disagreements: three record-time naming pairs; interval-end inclusivity stated only by POL/RAT/DOC; zone of date-only values stated only by POL/RAT → off-by-one-day joins in DAT silver.

**XMR-FR-150 (proposed contract §3.2.1 rule 3):** every bitemporal or effective-dated row shall store effective time as `valid_from`/`valid_to` (or `valid_period` range) and record time as `recorded_from`/`recorded_to` (or `record_period`); every interval half-open [from, to); instants in **UTC**; date-only business values (term start, due date, deadline day) interpreted in the time zone of the row's `legal_entity_id` (PLT time service `REQ-PLT-332`). APIs expose only `validAt` and `knownAt`; DAT silver keeps the same names. Acceptance: G term [2027-01-01, 2028-01-01) and claim with loss at 2027-12-31T23:30 Europe/Athens; W CLM, RI, DAT resolve segment; T all three select the same segment, and none selects it for a loss at 2028-01-01T00:00 local. (Decision D-159.)

### §12 Unified obligation register and regulatory traceability (l.2256–2362)

#### 12.1 Method
Seed = PRD-11 §15.2 merged traceability (after REG-001, REG-015; covers all 17 PRDs; seed of CMP obligations register `REQ-CMP-209`). **1,490 REQ/BR/NFR citations parsed — all resolve**; REQ citations: **1,141 Must, 159 Should/Could/Won't**. Result: no obligation met by nobody; EAA missing 4 modules (F-250); PLT SII overstated (F-251); obligations partly on non-Musts (F-252); two obligations without code (F-253 OBL-EQT, F-254 OBL-IRRD); OBL-PAY not traced by CLM (F-257); six citation/value corrections from spot-checks (F-212, -218, -230, -235, -236, -255).

#### 12.2 Obligation register (programme view) — full
"Req (Must)" = citations in PRD-11 §15.2 (REQ+BR+NFR), Must REQ count in brackets. "Mods" = PRDs whose §3.1 lists it.

| OBL | Instruments (as cited) | Contract status | Status after review | Primary owners | Mods | Req (Must) | Key Must REQs | Clocks | Gaps |
|---|---|---|---|---|---|---|---|---|---|
| OBL-SII | Dir. 2009/138/EC; Del. Reg. (EU) 2015/35; ITS (EU) 2023/894; Law 4364/2016; Dir. (EU) 2025/2 | Settled / Verify dates | 5 weeks Q, 14 weeks annual/SFCR confirmed 2026; Dir. 2025/2 from 30 Jan 2027 | DAT, FIN, CMP | 13 | 129 (104) | `REQ-DAT-003`, `-092…119`; `REQ-FIN-149…157`; `REQ-CMP-008`, `-195…201`; `REQ-PFC-005` | 31–33 | F-251, F-255 |
| OBL-IDD | Dir. (EU) 2016/97; Law 4583/2018; Del. Reg. (EU) 2017/2358 (POG); Impl. Reg. (EU) 2017/1469 (IPID) | Settled | unchanged | PTY, PFC, CHN, DOC | 14 | 99 (76) | `REQ-PTY-005`, `-008`, `-187…197`; `REQ-PFC-007`, `-148…161`; `REQ-CHN-086…095`; `REQ-DOC-007`; `REQ-POL-003`, `-172` (disclosure gate R-83) | — | `REQ-PFC-156/157` Should (F-367) |
| OBL-GDPR | Reg. (EU) 2016/679; Law 4624/2019 | Settled | 72-h breach & 1-month DSAR confirmed | PTY, CMP, PLT, DAT | 17 | 178 (155) | `REQ-CMP-005`, `-150…176`; `REQ-PLT-011`, `-235…248`; `REQ-DAT-009`, `-262…285`; `REQ-MIG-006` | 24, 25 | F-232; retention classes exist for every module (R-20) |
| OBL-DORA | Reg. (EU) 2022/2554; Del. Reg. (EU) 2025/301; Impl. Reg. (EU) 2024/2956 | Settled / Verify RTS | 4 h / 24 h / 72 h / 1 month confirmed; late-classification rule missing | PLT, CMP | 17 | 159 (116) | `REQ-PLT-012`, `-262…319`; `REQ-CMP-115`, `-203`, `-204`; per-module change control (`REQ-PFC-200…219`, `REQ-RAT-213…226`); `REQ-UW-186` | 35–38 | F-236 |
| OBL-AIA | Reg. (EU) 2024/1689 as amended by Reg. (EU) 2026/1744 (Digital Omnibus; in force 27 Jul 2026) | Settled / Uncertain classification | Standalone high-risk obligations from **2 Dec 2027**; content marking from **2 Dec 2026** | CMP, PLT, DAT, CHN | 17 | 103 (92) | `REQ-CMP-007`, `-237…246`; `REQ-PLT-010`, `-221…234`; `REQ-DAT-005`, `-197…225`; pricing `REQ-RAT-080`; fraud `REQ-CLM-208`, `-258`, `-259` | — | WRK cites only `BR-WRK-041` |
| OBL-BOG | BoG ECA 169/2020 CPD; Act 88/2016 complaints; Act 87/2016 MTPL claims | Verify | Acts 87 & 88/2016 confirmed in use | CMP, DAT, PTY, PLT | 12 | 49 (38) | `REQ-CMP-006`, `-208…221`; `REQ-DAT-120…123`; `REQ-PTY-191`, `-192` | 22, 23, 34 | F-238 |
| OBL-TAX | Law 5177/2025 Art. 43 (IPT); Law 5113/2024 Art. 14 (Aux Fund); AADE myDATA decisions; ΠΟΛ 1028/2017 & ΠΟΛ 1032/2018; A.1004/2024 as amended A.1185/2024; ENFIA | Verify | IPT rates, base, quarterly return confirmed; levy ceiling & 70/30 confirmed; IPT non-refund confirmed for 2017–18 guidance, not re-confirmed under Law 5177/2025 | CMP, FIN, BIL, RAT | 13 | 167 (118) | `REQ-RAT-009`, `-109…112`; `REQ-BIL-079`, `-269…277`; `REQ-FIN-005`, `-178…201`; `REQ-CMP-001`, `-030…067`, `-177…194`; `REQ-MKT-087`, `-322`, `-328` | 9, 18, 19, 26, 28, 29 | F-210, -211, -212, -213, -216, -218 |
| OBL-MOT | Dir. 2009/103/EC as amended (EU) 2021/2118; P.D. 237/1986; Law 5113/2024; Act 87/2016; FS market agreement | Verify | Act 87/2016 Arts 3, 5, 6 confirmed; Art. 6 payment start differs from PRDs | POL, CLM, CMP, PFC | 14 | 129 (93) | `REQ-CLM-007`, `-008`, `-155…173`, `-191`; `REQ-CMP-002`, `-068…091`; `REQ-POL-137`, `-315`, `-316`; `REQ-PFC-003`, `-085…088` | 10–14, 27 | F-230, -231, -214 |
| OBL-RES | GDPR Ch. V; EIOPA cloud guidelines + DORA | Settled | unchanged (R-93) | PLT | 17 | 34 (20) | `REQ-PLT-221`, `-285` | — | `REQ-RI-221` Won't P1, OK under R-93 |
| OBL-CON | Law 2496/1997; Law 2251/1994 | Verify | Art. 6 one month confirmed; objection one month (secondary) | POL, BIL, DOC, CMP | 11 | 111 (87) | `REQ-POL-012`, `-305…313`; `REQ-BIL-006`, `-168…174`; `REQ-DOC-250…256`; `REQ-CMP-003`, `-092…121` | 1, 2, 4, 6, 7, 8, 16, 17 | F-231, -233 |
| OBL-DMFS | Dir. (EU) 2023/2673; Law 5317/2026 Arts 69–72 (Arts 3ιε–3ιστ of Law 2251/1994) | Settled | Art. 72 → 3ιστ: **30 days from receipt; no charge on withdrawal from an insurance contract** | CHN, POL, BIL, DOC | 8 | 44 (33) | `REQ-CHN-007`, `-160…174`; `REQ-POL-308`, `-314`; `REQ-BIL-181`, `-196`; `REQ-DOC-044` | 3, 5 | F-235 |
| OBL-EAA | Dir. (EU) 2019/882; Law 4994/2022 | Programme decision R-57 | unchanged | CHN (DOC) | 15 | 26 (11) | `REQ-CHN-295…298`; `REQ-DOC-290…297`; `NFR-CHN-010…012` | — | F-250 |
| OBL-AML | Law 4557/2018 (Uncertain); EU restrictive measures; OFAC (group) | AML Uncertain; sanctions Settled | unchanged | PTY | 9 | 33 (29) | `REQ-PTY-006`, `-169…185`; `REQ-BIL-007`, `-200`; `REQ-CLM-004`, `-133`; `REQ-RI-049`, `-191` | — | — |
| OBL-IFRS17 | IFRS 17 (Reg. (EU) 2021/2036); Law 4308/2014 | Settled | unchanged | FIN, DAT | 8 | 73 (55) | `REQ-FIN-001`, `-011`, `-121…147`; `REQ-DAT-004`, `-144…156` | — | — |
| OBL-COMP | EIOPA complaints guidelines; BoG Act 88/2016 | Verify | 50 calendar days (peer-verified) | CMP | 9 | 42 (36) | `REQ-CMP-004`, `-122…137`; `REQ-DOC-253` | 20–23 | F-215 |
| OBL-NATCAT | Law 5116/2024 as amended Law 5162/2024; JMD 96806/2025 | Verify | not re-verified (OI-UW-01) | UW, DOC | 7 | 39 (23) | `REQ-UW-005`, `-176`, `-179`, `-222…233`; `REQ-DOC-043`, `-254`; `REQ-MKT-103` | 15 | F-252 (13/36 non-Must; `REQ-RAT-257`, `-262` Won't) |
| OBL-EIDAS | Reg. (EU) 910/2014 as amended (EU) 2024/1183 | Verify | unchanged | DOC, CHN, PLT | 8 | 25 (19) | `REQ-DOC-006`, `-260…271`; `REQ-CHN-141`; `REQ-PLT-130` | — | — |
| OBL-PAY | PSD2; Reg. (EU) 260/2012; Reg. (EU) 2024/886 (VoP from **9 Oct 2025**) | Settled / Verify | unchanged | BIL, CHN | 2 | 30 (20) | `REQ-BIL-106…118`, `-147…152`, `-203…205`; `REQ-CHN-103`, `-135` | — | F-257 |
| OBL-PCI | PCI DSS v4.x (contractual) | Settled | unchanged | BIL, CHN, PLT | 3 | 9 (7) | `REQ-BIL-116`; `REQ-CHN-103`, `-134`, `-135`; `NFR-PLT-033` | — | — |
| OBL-EPRIV | Dir. 2002/58/EC; Law 3471/2006 | Settled | unchanged | CHN, PTY | 4 | 11 (9) | `REQ-CHN-290…294`; `REQ-PTY-150` | — | — |
| **OBL-EQT (proposed)** | Dir. 2004/113/EC; CJEU C-236/09 *Test-Achats*; Law 3769/2009 | — | verified by PRD-03 | RAT, CMP | 1 | — | `REQ-RAT-033`, `-144`; `PricingConstraint` (R-05, R-24); AI boundary §3.8.3 item 3 | — | F-253 |
| **OBL-IRRD (proposed)** | Dir. (EU) 2025/1; transposition by 29 Jan 2027 | — | instrument confirmed; Greek transposition UNVERIFIED | CMP, DAT, FIN | 0 | — | to add: recovery-plan & resolution data from DAT marts; tracker (`REQ-CMP-008`, `-226`) | — | F-254 |

Note: the obligation register is keyed by OBL code with *lists* of owning REQs, not one owning REQ per obligation; the "Key Must REQs" column above is the source's owning-requirement list.

#### 12.3 Traceability matrix (obligation × module)
Matrix of 22 obligations × 17 modules (● = in module §3.1 and PRD-11 §15.2; ◐ = §3.1 only). All 17 modules carry OBL-GDPR, OBL-DORA, OBL-AIA, OBL-RES. Only ◐ cells: **OBL-EAA for RAT, UW, FIN, DAT** (F-250). OBL-PAY traced only by BIL, CHN (CLM missing, F-257). Prompt-named checks pass: DORA all 17 (REG-004 resolved); GDPR retention classes `RC-<MOD>-*` in every module with PLT engine `REQ-PLT-011` (residue `RC-MKT-CFG`); EAA in CHN/DOC journeys; IDD POG `REQ-PFC-148…161`; AI Act pricing (RAT) and fraud (CLM) cite REQ IDs, default high-risk controls (contract §3.8.1).

#### 12.4 Legal spot-check log (web, 2026-10-07)
| # | Claim | Result | Effect |
|---|---|---|---|
| L-1 | Non-payment: contract dissolved one month after notification (Law 2496/1997 Art. 6; K-01) | Confirmed | row 8 V |
| L-2 | MTPL (BoG Act 87/5.4.2016): assessment 15/25 days (Art. 5); payment 10 days; repair in kind 20 days (Art. 6); offer 3 months (Art. 3) | Confirmed values; **Art. 6 counts 10 days "από την προσφορά" (from the offer), not acceptance; repair in kind counts from the agreement** | F-230, F-231 |
| L-3 | IPT **15% general / 20% fire / 4% life**; base "απαιτητά ασφάλιστρα" + rights of every kind; quarterly declaration by end of third month (Law 5177/2025 Art. 43) | Confirmed; article has no cancellation/refund provision | F-211, F-212 |
| L-4 | IPT not refunded on cancellation (ΠΟΛ 1028/2017) | Confirmed as AADE guidance; ΠΟΛ 1032/2018 keeps no-refund, subject to set-off under older law; validity after Law 5177/2025 UNVERIFIED | F-218 |
| L-5 | Aux Fund: ceiling **6% of gross written MTPL premium, 70% insurers / 30% insured** (Law 5113/2024 Art. 14) | Ceiling & 70/30 confirmed; 4.5%/1.5% components UNVERIFIED | F-213 |
| L-6 | Distance withdrawal: refund within 30 calendar days of receiving withdrawal statement; nothing payable on withdrawal from an insurance contract (Law 5317/2026 Art. 72) | Confirmed; inserts Art. 3ιστ into Law 2251/1994; Art. 71 inserts Art. 3ιε | F-235 |
| L-7 | DORA: initial within 4 h of classification and ≤ 24 h from awareness; intermediate 72 h; final 1 month (Del. Reg. (EU) 2025/301) | Confirmed + rule for classification later than 24 h (4 h from classification) and national weekend handling | F-236 |
| L-8 | AI Act Digital Omnibus = Reg. (EU) 2026/1744 | Confirmed: OJ, in force 27 Jul 2026; high-risk from 2 Dec 2027 | REG-007 stands |
| L-9 | SII reporting 5 weeks Q / 14 weeks annual (solo) | Confirmed for 2026 cycle; Dir. (EU) 2025/2 from 30 Jan 2027 | F-255 |
| L-10 | IRRD Dir. (EU) 2025/1 | Confirmed: OJ 8 Jan 2025, transposition by 29 Jan 2027 | F-254 |

Sources: 15 URLs (lawspot.gr, e-nomothesia.gr, taxheaven.gr, nextdeal.gr, aade.gr, advisera.com, cssf.lu, hunton.com, milliman.com, algoodbody.com), accessed 2026-10-07.

### §13 SPI coverage matrix (l.2365–2460)

Catalogue = contract §3.5.8, **40 SPIs**, owner MKT (`REQ-MKT-002`); specs PRD-17 §9.4.1…9.4.42, extended §9.4.43 (R-24, R-29, R-42, R-49, R-56); Greece implementations in Greece pack index PRD-17 §10.4.2 (**GR-01…GR-32**); Cyprus stub §9.4.x/§10.4.7. Greece status: Settled / Verify / UNVERIFIED / Uncertain / Market practice. Cyprus stub classes: Different / No-op / Same / None.

#### 13.2 Matrix (all 40)
| # | SPI | Owning REQ; priority | Callers (modules; caller REQ IDs in source) | Greece impl (REQ; GR row; status) | Cyprus stub | Motor MVP note |
|---|---|---|---|---|---|---|
| 1 | `IdValidator` | `REQ-MKT-090`; Must P1 | PTY, PFC, MIG, DAT, CMP via PTY, CHN, CLM | AFM mod-11, GEMI, VAT with EL prefix (VIES), ID-document formats — `REQ-MKT-260`; GR-11; Settled (format) | Different (TIC 8 digits + letter) | Required; home for `VEHICLE_PLATE` (F-301) |
| 2 | `NameTransliterator` | `REQ-MKT-091`; Must P1 | PTY, DOC, WRK, BIL, MIG, CHN, CLM, PLT | **ELOT 743 Type 2 (ISO 843 aligned)** — GR-12; Settled | Different in rule-set id only | Required; `searchVariants` carries Greek folding (F-300) |
| 3 | `AddressFormatter` | `REQ-MKT-092`; Must P1 | PTY, DOC, POL, MIG, CHN, CLM | 5-digit postcodes, street-number order, Greek & Latin forms (no index row) | Different (4-digit, English-first) | Required |
| 4 | `TaxCalculator` | `REQ-MKT-087`; Must P1; RI op `calculateReinsurancePremiumTax` `REQ-MKT-315` Should P3 | RAT; BIL receipt stamps; FIN recon; PFC/POL via RAT; RI optional | IPT by class (15% general, 20% fire, Law 5177/2025 Art. 43) and Aux Fund levy (`REQ-MKT-322`; OI-MKT-17) — `REQ-MKT-257`; GR-01, GR-02 | Different (synthetic flat stamps to 2025-12-31, 5% motor fund levy) | Required; new `treatment` op (D-152) |
| 5 | `FiscalDocumentChannel` | `REQ-MKT-088`; Must P1 | CMP; BIL, FIN consume; MIG via CMP; PFC fiscal-category key | AADE myDATA doc types, series, MARK/UID, `cmp.fiscal.mark_before_issue` — `REQ-MKT-258`; GR-03; **Verify** | No-op `NotRequired` | Required; go-live critical (F-307) |
| 6 | `EInvoiceProvider` | `REQ-MKT-089`; **Should P3** | CMP | via `cap.cmp.einvoice_b2b`; applicability Uncertain — GR-04; OI-MKT-08 | None | priority contradiction (F-302) |
| 7 | `BureauAdapter` | `REQ-MKT-093`; Must P1 | CMP; POL via CMP; MIG via CMP | Information Centre reporting — `REQ-MKT-259`; GR-05; **Verify** (operator, channel) | Different (file export) | go-live critical (F-307) |
| 8 | `StatutoryClockSet` | `REQ-MKT-094`, `-285`, `-286`, `-291`; Must P1 | CMP; via CMP POL, BIL, CLM, UW, CHN, FIN, DAT, MIG | §10.4.3 values — `REQ-MKT-261`, `-321`; GR-14; **UNVERIFIED** | Different (synthetic) | values unverified (F-307) |
| 9 | `NumberingScheme` | `REQ-MKT-096`; Must P1 | PLT numbering for all | formats incl. fiscal series — `REQ-MKT-262`; GR-20; Verify | Different | Required |
| 10 | `HolidayCalendarProvider` | `REQ-MKT-097`; Must P1 | PLT calendars; via PLT BIL, CMP, FIN, CLM; WRK lists it directly | fixed holidays + Orthodox Easter movable days — GR-16; Settled | Different | WRK must use PLT calendar (F-306) |
| 11 | `PaymentReferenceGenerator` | `REQ-MKT-098`; Must P1 | BIL; CHN, MIG via BIL | RF references + Greek bank payment-code formats — GR-18; Verify | Different (subset, RF only) | go-live critical |
| 12 | `BankFileFormat` | `REQ-MKT-100`; Must P1 | BIL; MIG via BIL | **SEPA ISO 20022** core default; local variants if banks require | **Same** | variability not exercised (F-308) |
| 13 | `PayeeVerification` | `REQ-MKT-102`; Must P1 | BIL; CLM via BIL `PaymentInstrument` | insurer's PSP (pack binding) | **Same** | Required |
| 14 | `RegimeCodeList` | `REQ-MKT-104`, `-208`; Must P1 | PFC, DAT, FIN, RI, RAT via PFC | BoG statistical, IPT, A.1004, EAEE classes — GR-08 Verify, GR-09 Verify, GR-24 Market practice | Different | KAD outside it (F-310) |
| 15 | `DocumentLanguageRule` | `REQ-MKT-101`; Must P1 | DOC, PFC, UW, RI, CLM, WRK | **Greek binding, English informative** — GR-17; Settled (contract §3.9.10) | Different (English binding) | Required |
| 16 | `MotorDataProvider` | `REQ-MKT-105` Must P1; wallet-consent ops `REQ-MKT-316` **Should P1** | POL, CHN, UW, RAT indirect, PFC vehicle catalogue, CLM via POL, MIG | vehicle registry & gov.gr Wallet pre-fill — `REQ-MKT-259`; GR-10; Verify | Different | wallet priority inconsistent (F-304) |
| 17 | `RegistryLookup` | `REQ-MKT-106`; **Should P1** | PTY; UW via PTY | **AADE RgWsPublic2 and GEMI** — `REQ-MKT-260` (Must); GR-11 | Stub (canned) | priority contradiction (F-303) |
| 18 | `IntermediaryRegister` | `REQ-MKT-095`; Must P1 | PTY | Chamber register with file fallback — GR-13; Verify | Different | go-live critical |
| 19 | `ComplaintRules` | `REQ-MKT-099` Must P1; host-State `REQ-MKT-220` Should P4 | CMP, DOC, CLM via CMP | 50-day final reply (Act 88/2016), Greek ADR bodies — GR-15; Verify | Different | Required |
| 20 | `RefusalDocumentRule` | `REQ-MKT-103`, ext `-307`; Must P1 | UW; DOC | nat-cat refusals (Law 5116/2024), 30-day response — GR-07 Verify; GR-31 UNVERIFIED | No-op | MVP on core default `NotRequired`; Greek rule P2/P3 |
| 21 | `SanctionsListSource` | `REQ-MKT-107`; Must P1 | PTY; via PTY BIL, CLM, RI, POL | EU, UN, national; group adds OFAC — GR-21; Settled | Different (EU, UN) | Required |
| 22 | `ClaimsHistoryFormat` | `REQ-MKT-108`; Must P1 | CLM, DOC, RAT input mirrors, MIG | Greek certificate content — GR-19; Verify | core generic | Required |
| 23 | `FriendlySettlementClearing` | `REQ-MKT-109`, ext `-313`; Must P1 | CLM; CHN joint accident report via CLM; FIN | FS clearing, `cap.clm.friendly_settlement` on — `REQ-MKT-259`; GR-06; Verify | No-op `NotApplicable` | go-live critical |
| 24 | `PricingConstraint` | `REQ-MKT-110`, ext `-306`; Must P1 | RAT, PFC lint, UW, DAT bias | EU gender-neutral pricing + Greek limits (Verify); fairness defaults policy not law (UNVERIFIED) | Different (synthetic 10% renewal cap) | Required |
| 25 | `ConsentRules` | `REQ-MKT-111`; Must P1 | PTY, CHN, DOC | Greek e-privacy (OBL-EPRIV) | Different (opt-in all marketing) | Required |
| 26 | `ESignatureProvider` | `REQ-MKT-112`; Must P1 | DOC | **gov.gr co-signing** (scope Verify) with commercial fallback | Different | Required |
| 27 | `DigitalIdentityProvider` | `REQ-MKT-113`; **Could P1** | CHN via PLT; PLT | national wallet/identity providers "as available" | None | optional |
| 28 | `TaxReturnFormat` | `REQ-MKT-114`, ext `-314`; Must P1; host-State `REQ-MKT-227` Could P4 | FIN, CMP, DAT | IPT & Aux Fund levy returns — GR-22; Verify | Different | levy periods also in BIL (F-309) |
| 29 | `MandatoryWordingSet` | `REQ-MKT-115`; Must P1 | DOC, PFC lint | mandatory pre-contractual & policy wording — GR-23; Verify | Different (smaller) | Required |
| 30 | `Geocoder` | `REQ-MKT-305`; Must P1 | PTY, POL, RAT (P2), UW, RI, DAT, CHN, CLM | provider by pack; hazard schemes (seismic, flood, wildfire) — GR-26; UNVERIFIED content | Different | geocoding required (garaging); hazards P2 |
| 31 | `PolicyLifecycleRules` | `REQ-MKT-308`; Must P1; Greek rule `-309` | POL, BIL, CHN, DOC | distance withdrawal = full refund (Law 5317/2026 Art. 72, R-55) — GR-25 Settled; other refund methods UNVERIFIED | Different (pro rata less cost of cover; 30-day reinstatement gap) | Required |
| 32 | `MotorCompensationBodyAdapter` | `REQ-MKT-310`; Must P1 | CLM; FIN via CLM | Auxiliary Fund, Hellenic Motor Insurers' Bureau, compensation body — GR-27; Verify | Different | go-live critical; op names differ (F-305) |
| 33 | `StatutoryDeliveryRule` | `REQ-MKT-311`; Must P1 | DOC, BIL, POL, CMP, CHN | non-payment notice by **registered letter or proven electronic delivery** (Law 2496/1997 Art. 6) — GR-28; Verify | Different (e-mail read receipt) | go-live critical |
| 34 | `PaymentChannelProvider` | `REQ-MKT-312`; Must P1 | BIL, CHN, MIG | instant-payment QR & national payment networks — GR-29; Verify | Different (card acquirer only) | Required |
| 35 | `InboundDocumentProfile` | `REQ-MKT-317`; Must P1 | WRK; CLM, UW via WRK | vehicle registration cert, joint accident report, ID card, tax-clearance — GR-30; Market practice | Different | Required |
| 36 | `IdentityFederationProvider` | `REQ-MKT-318`; **Should P1** | PLT | national public-admin authentication & EU Digital Identity Wallet (Verify) | None | optional |
| 37 | `IncidentReportingChannel` | `REQ-MKT-319`; Must P1 | PLT; CMP tracks | Bank of Greece channel & format — GR-32; **UNVERIFIED** (OI-PLT-01) | core default `ManualSubmissionRequired` | Required (DORA); manual fallback |
| 38 | `FxRateSource` | `REQ-MKT-320`; Must P1 | PLT; FIN via PLT; RI directly | euro reference rates | **Same** (Bulgaria fixture differs) | RI must use `plt.Fx` (F-306) |
| 39 | `LegacyDataProfile` | `REQ-MKT-325`; Must P1 | MIG | Greek plates incl. Latin look-alikes, VIN, **ELOT 928 / Windows-1253**, AFM heuristics | Different | required if scenario B |
| 40 | `StatutoryDataReturnFormat` | `REQ-MKT-326`; Must P1 | DAT, CMP | A.1004 (P2), BoG statistical templates, EAEE feed (layout UNVERIFIED) | Different | required P1 via `REQ-DAT-120` |

Non-catalogue hooks: plate "pack normaliser" `REQ-POL-279` (F-301); KAD `REQ-PFC-107` (F-310); `mkt.CrossBorder.check` `REQ-POL-175` (MKT API, acceptable, P4 gate); Greek name-search folding `REQ-PTY-001`, `-065`, `-067`, `REQ-WRK-326` (F-300).

#### 13.3 Catalogue integrity
- Every SPI has ≥1 caller: **Pass** (40/40; `DigitalIdentityProvider`, `IdentityFederationProvider` only optional callers).
- Every used SPI exists: **Fail (1)** plate normaliser (F-301); KAD bypass (F-310).
- SPI priority ≥ strongest caller: **Fail (3)** `EInvoiceProvider` (F-302), `RegistryLookup` (F-303), `MotorDataProvider` wallet ops (F-304).
- Operation names match owner (R-87): **4 residual mismatches** — CLM CCR-CLM-03 (`notifyClaim`, `receiveClaim`, `recoveryDemand`, `settlementStatus` vs MKT `notify`, `requestReimbursement`, `receiveNotification`, `settlementStatement`); CHN CCR-CHN-04 (`walletConsentStart/Status/Cancel` vs `requestWalletConsent`, `consentResult`); RI (`reinsurancePremiumTax` vs `calculateReinsurancePremiumTax`); RAT CCR-RAT-05 (`renewalFairnessDefaults` vs `fairnessDefaults`) (F-305).
- Contract "main callers" incomplete: `Geocoder` (CLM, CHN, RI, DAT), `DocumentLanguageRule` (PFC, UW, RI, CLM), `RegimeCodeList` (RI), `NameTransliterator` (WRK, BIL, MIG), `IdValidator` (PFC, MIG, DAT); RI→`FxRateSource`, WRK→`HolidayCalendarProvider` should go via PLT (CD-17) (F-306).
- Greece pack go-live binding (`REQ-MKT-255`): **Open** — of 32 rows: **6 Settled, 2 cited law with open reading (GR-01, GR-02), 17 Verify, 4 UNVERIFIED, 1 Uncertain, 2 Market practice**; 17 unsettled on motor path (GR-03, -05, -06, -09, -10, -13, -14, -15, -18, -19, -20, -22, -23, -27, -28, -29, -32) + GR-02 split (F-307).
- Cyprus stub (`REQ-MKT-263`): **Partial** — 28 Different (2 partly), 1 canned, 5 No-op/core default (`FiscalDocumentChannel`, `RefusalDocumentRule`, `FriendlySettlementClearing`, `ClaimsHistoryFormat`, `IncidentReportingChannel`), 3 Same (`BankFileFormat`, `PayeeVerification`, `FxRateSource`), 3 None (`EInvoiceProvider`, `DigitalIdentityProvider`, `IdentityFederationProvider`) (F-308).

#### 13.4 Multi-market leakage register
Continues CON-033 (46 candidate rows, ~15 market-specific). Residual leakage: `REQ-PTY-001`, `-065`, `-067` and `REQ-WRK-326` (Greek search folding; reverse ELOT digraphs ou, mp/b, nt/d, gk/g, ch) → core Unicode-generic (NFD, combining-mark removal, case fold) + MKT language rules + `NameTransliterator.searchVariants` (F-300); `REQ-DOC-145`, `-146` CLDR `el-Upper`, final sigma → keyed by language via MKT (F-300); `REQ-POL-279` plates → `IdValidator` `VEHICLE_PLATE` (F-301); `REQ-POL-306` 14-day/10-month in core → `StatutoryClockSet` (F-309); `REQ-CLM-182` Greek SMS segments (UCS-2 70/67 chars) → generic GSM-7 vs UCS-2 (F-309); `REQ-CLM-122` "no cheques in Greece pack" → pack data (F-309); `REQ-FIN-190` "4.5% and 1.5%" → reference `REQ-MKT-322` (F-309); `REQ-BIL-270` vs `REQ-MKT-314` levy periods → `TaxReturnFormat` period scheme (F-309); `REQ-MIG-124` MARK in core → `FiscalDocumentChannel` correlation (F-309); `REQ-PFC-107` KAD → `RegimeCodeList` `ACTIVITY_CODE` (F-310).

### §14 Non-functional baseline (l.2463–2579)

#### 14.1 Programme-level targets (contract §3.9 restated; every module inherits unless §14.3 deviation)
| Area | Target | Source |
|---|---|---|
| Availability tiers | **T1 99.9% monthly, RPO ≤ 5 min, RTO ≤ 2 h; T2 99.5%, RPO ≤ 15 min, RTO ≤ 4 h; T3 99.0%, RPO ≤ 24 h, RTO ≤ 24 h.** Identity service designed to **99.95%**; tokens valid until expiry during identity outage | contract §3.9.6, R-13 |
| T1 dependency rule | synchronous dependency of a T1 op is itself T1 or has documented degraded mode; statutory deadlines met across failover | §3.9.6 |
| Interactive | **page ready ≤ 1.5 s p95; palette ≤ 300 ms; global & party search ≤ 1 s** | §3.9.7 |
| APIs | **reads ≤ 300 ms; commands ≤ 800 ms excl. external calls; single rating ≤ 200 ms; quote (rate + UW evaluate) ≤ 2 s**; document preview outside quote budget | §3.9.7, R3-006 |
| Events | **outbox to consumer ≤ 5 s p95; read models ≤ 10 s** | §3.9.7 |
| AI | **first token ≤ 2 s, complete ≤ 10 s, fallback default 5 s; kill switch ≤ 60 s** | §3.9.7; `NFR-PLT-014` |
| Volumes | capacity model XMR-FR-200 | §3.9.7 |
| Accessibility | **WCAG 2.2 AA** for every UI in Greek and English | §3.9.8 |
| Languages | Greek and English mandatory for every UI string, message, document, template at go-live | §3.9.5 |
| Security | **TLS 1.2+**; encryption at rest; field-level encryption of P2/P3 identifiers and IBANs; keys in platform KMS; **OWASP ASVS L2** external apps (identity **L3**, `NFR-PLT-015`); pen tests before go-live and annually; **no cardholder data anywhere** (`NFR-PLT-033`, `NFR-BIL-012`) | §3.9.12 |
| Privacy & residency | no personal data in logs/events beyond pseudonymous ids; EU processing; non-EU transfers only with recorded GDPR Ch. V basis (R-93, `REQ-PLT-285`) | §3.9.11, R-93 |
| Observability | **OpenTelemetry** traces, metrics, logs; business SLIs per module; alerts to PLT incident mgmt | §3.9.11 |
| Time | current time only from PLT time service (`REQ-PLT-332`, R-06) | §3.9.13 |
| Retention | classes per entity table, durations as pack data (R-20) — **programme schedule missing** (F-329) | R-20 |

#### 14.2 Programme capacity model — XMR-FR-200 (proposed)
*"The programme shall maintain one capacity model, owned by the design authority and confirmed by MIG (legacy book) and DAT (event and data volumes), stating the design value, peak factor and test multiple for each quantity below; every module NFR shall cite it and size to it."*

| Quantity | Programme value | Basis | Module status |
|---|---|---|---|
| Parties | **1.5 million** | contract | consistent (PLT 1.5 m external identities) |
| Intermediaries / users | **20,000 intermediaries; 25,000 intermediary users** | contract; `NFR-PLT-006` | PTY "20 k external users" conflicts (F-328) |
| In-force policies | **1.2 million (motor ≈ 840,000)** | contract (motor ≈ 70%) | CMP `NFR-CMP-008` "≈ 1.2 million motor vehicles" needs derivation (F-328) |
| Policy transactions | **2 million/year (≈ 8,000/working day; peak 3×)** | contract; POL §14 | consistent |
| Motor renewals | **70,000/month; peak 120,000/month (January, September)** | POL §14 | consistent |
| Renewal batch | **150,000 terms end-to-end ≤ 4 h; nightly 40,000 in ≤ 1 h per stage** | `NFR-PLT-005`, `NFR-UW-007` | POL ≥ 30,000/h; RAT 100k in 30 min; DOC 300k docs in 4 h (F-322) — resolution POL ≥ 40,000/h |
| Quotes | **50 full quotes/s + 200 aggregator dry-runs/s, ≤ 25% rated → 100 rated quotes/s ≈ 400 ratings/s** | contract; `NFR-CHN-003` | RAT 300/s; UW 100/s; PLT 100 quotes/s (F-320) — resolution RAT ≥ 400/s with 1.5× headroom; UW ≥ 200/s |
| Claims | **150,000/year (≈ 600/business day); ≈ 1.5 exposures/claim, ≈ 4 financial txns/exposure** | contract; CLM §14 | consistent |
| Claim financial events | **≈ 0.9 million/year** | CLM §14 | FIN assumes ≈ 3 million (F-321) |
| Catastrophe surge | **one design event: 1,000 FNOL/h for 24 h, ≈ 32,000 claims over 72 h** (OI-CLM-08 to confirm) | `NFR-CLM-004` | RI 3,000 claims/72 h; WRK 20,000 activities/h for 72 h (F-323) |
| Invoices; receipts; fiscal docs | **5 million; ≈ 6 million; ≈ 6 million / year** | contract; BIL; `NFR-CMP-003` | consistent |
| Documents | **10 million/year, peak day 400,000; inbound 3 million documents (≈ 9 million files)** | contract; `NFR-DOC-004`; `NFR-WRK-005` | consistent |
| Domain events | **≈ 60 million/year (design 3× = 180 million); sustained ≥ 2,000/s, burst 5,000/s** | `NFR-DAT-010` | PLT 50 m/yr at 1,000/s (`NFR-PLT-008`) below (F-321) |
| Journal lines | **60–100 million/year** | FIN §14 | BIL ledger lines 40 m (design 150 m) consistent |
| Audit records | **200 million/year/stamp** | `NFR-PLT-007` | consistent |
| Clock instances | **3 million started/year; ≤ 2 million running** | `NFR-CMP-010` | consistent |
| Activities | **8 million/year; 500,000 open; 2,000 concurrent staff** | `NFR-WRK-001`, `-005` | consistent |
| SPI calls | **≈ 30 million/year** | `NFR-MKT-009` | consistent |

#### 14.3 Per-module tiers and deviations
| Module | Tiers | Deviations | Verdict |
|---|---|---|---|
| PTY | T1 query, search, screening, producer validation, agreement resolution; T2 stewardship (`NFR-PTY-008`) | reads **≤ 100 ms** (`NFR-PTY-003`); screening **≤ 300 ms p95, ≤ 800 ms p99** (`-004`); duplicate suggestions ≤ 500 ms (`-002`) | Accepted (p99 feeds bind budget F-332) |
| PFC | runtime T1; authoring/release T2 (`NFR-PFC-009`, `-010`) | resolve **p99 < 1 ms in memory** (`NFR-PFC-001`); keeps serving if PFC DB down | Accepted |
| RAT | rating API & proration T1; rate mgmt T2 | **200 ms p95 at 300 ratings/s** (`NFR-RAT-001`) | below channel load (F-320) |
| UW | module T2; `uw.Rules.evaluate`, `uw.Issue.blockingStatus`, `uw.PolicyHold.check` T1 | evaluate fails closed, no degraded mode (`REQ-UW-057`) | Accepted; batch F-322 |
| POL | T1 (`NFR-POL-011`) | OOS reverse of **52 txns ≤ 5 s, 400 ≤ 30 s** (`NFR-POL-003`); change dry-run ≤ 1 s (`-004`); bind continues with DOC, WRK, CMP down (`-009`, R-83) | Accepted; F-322, F-332 |
| BIL | payment intake, down-payment status, preview T1; workbenches, batches, disbursement release T2 | delta intake ≤ 5 s (`NFR-BIL-003`); month-end close ≤ BD2 (`-021`); FIN recon by 06:00 (`-022`) | Accepted |
| CLM | FNOL API & tracking T1; workbench T2 | FNOL submit **≤ 1.5 s** (`NFR-CLM-001`); transaction set **≤ 1.2 s** incl. sync screening & authority (`-003`); FNOL accepted when POL/PTY/WRK degraded (`-006`) | Accepted; F-323 |
| RI | T2 (`NFR-RI-009`) | cession ≤ 5 s; recovery ≤ 60 s incl. 30 s batching (`-001`, `-002`); accumulation zone 300 ms on UW bind path (UW degraded mode `REQ-UW-280`, P2) | Accepted; F-323, F-330 |
| FIN | T2; bind and FNOL never depend on FIN (`NFR-FIN-008`) | posting latency **≤ 10 s p95** (`-003`); sub-ledger lock **≤ WD+3** (`-010`); daily recon **by 07:00** (`-011`) | Accepted; F-321 |
| DOC | T1-designed path (cover note, IPID, pre-contractual pack, archive write, `doc.Delivery.evidenceFor`); rest T2 (`NFR-DOC-007`) | archive RTO **≤ 4 h** (`NFR-DOC-008`) | **Contradiction** (F-324 → RTO ≤ 2 h) |
| CMP | fiscal channel, bureau adapter, clock engine **T2** (`NFR-CMP-001`) | clock start/stop from T1 flows queued with original timestamps; **MARK p95 < 60 s** (`-002`); DSAR module export 1 business day (`-012`) | Accepted; F-325, F-331 |
| CHN | T1 quote, bind, payment initiation, FNOL, withdrawal, partner quote/bind; T2 dev portal, webhooks, agent facade (`NFR-CHN-004`) | withdrawal store **RPO 0** (`-005`); agent tokens ≤ 15 min (`-008`) | RPO 0 accepted; tokens F-326 (→ ≤ 10 min) |
| WRK | T2; no T1 op calls WRK synchronously (`REQ-WRK-402`, R-83) | lists ≤ 1 s at 500,000 open (`NFR-WRK-001`) | Accepted |
| PLT | identity, event relay, authority, config runtime T1; identity 99.95% (`NFR-PLT-009`); audit T1 writes/T2 query; DORA tooling T2 (`-010`) | identity ASVS L3 (`-015`); access tokens **≤ 10 min** (`-016`); stamp provisioning ≤ 1 working day (`-023`) | F-321, F-327 |
| DAT | T3; signed-off mart runs, registry, contracts at T2 RPO/RTO (R-69, `NFR-DAT-001`, `-004`) | event-to-gold **≤ 2 min** (`-002`); erasure propagation ≤ 10 calendar days (`-013`) | Accepted |
| MIG | routing & cross-ref T1 during coexistence (`NFR-MIG-005`, R-74); other T3, T2 during cut-over (`-007`) | auto-throttle to protect T1 budgets (`-003`) | Accepted |
| MKT | resolver, SPI gateway, calculation SPIs, rounding T1; management T2; analytics T3 (`NFR-MKT-011`) | resolution **≤ 2 ms p95** in a rating call (`-001`); degrade to last-known state, never defaults; new entity stamp ≤ 10 working days (`-018`) | Accepted; F-327 |

#### 14.4 Synchronous dependencies of T1 operations
- `pol.Job.bind` (`REQ-POL-003`): `uw.Issue.blockingStatus` T1 fail closed — Pass; `bil.DownPayment.status` T1 (`NFR-BIL-005`) — Pass; `pty.Screening.screen`, `pty.ProducerCode.validate` T1 (list-source outage → last good list; payments never unscreened `NFR-PTY-017`) — Pass; disclosure evidence `doc.Delivery.evidenceFor` / `DisclosureDeliveryView` T1-designed but RTO 4 h, degraded via CHN `DisclosureReceiptRecorded` — **Fail on RTO (F-324)**; gate-blocking WRK activities T2 not called synchronously (`REQ-WRK-402`) — Pass; CMP fiscal channel T2, never blocks bind (`BR-CMP-008`) — Pass; RI accumulation (P2) T2, UW degraded mode — Pass; PFC resolve, MKT resolver, PLT authority & identity T1, last-known state / in-memory — Pass.
- `pol.Job.quote`: `rat.Rate.rate`, `uw.Rules.evaluate`, `pfc.PolicyDraft.validate` T1 — Pass (capacity F-320).
- CHN withdrawal (`REQ-CHN-007`): `pol.Withdrawal.submit` T1; declaration store independent (`NFR-CHN-005`) — Pass; DOC durable-medium ack (`REQ-DOC-044`) not in DOC T1 list — **Gap (F-324)**.
- `clm.Fnol.submit`: `pol.Snapshot.get` T1, FNOL accepted when POL degraded — Pass; CMP clock start T2 queued with original receipt time — Pass (F-331).
- BIL payment intake: `PaymentChannelProvider` external; channel not offered after **3 s** — Pass.
- MIG routing: legacy enquiry external; degraded index answer **≤ 500 ms** (`NFR-MIG-006`) — Pass.

#### 14.5 Budget composition
- Quote ≤ 2 s: CHN 100 ms + POL 300 ms + PFC validate 100 ms + RAT 200 ms × ≈4 ratings + UW 150 ms + MKT ≤ 2 ms → **≤ 1.45 s sequential — fits**.
- Bind ≤ 800 ms (excl. external): UW 50 + BIL 100 + PTY screening 300 (p99 800) + PTY producer 100 + authority 50 + POL commit → **fits only if gate calls run in parallel** (F-332).
- FNOL ≤ 1.5 s: POL snapshot 150 ms + PTY 100 ms + CLM — fits.
- Cover note ≤ 60 s to customer: DOC render ≤ 10 s, receipt by preferred channel ≤ 60 s (`NFR-DOC-001`); POL G-4 proof of cover ≤ 60 s — consistent.

#### 14.6 Consistent (no finding)
WCAG 2.2 AA & EL/EN in every module; P2/P3 field-level encryption (PTY, POL, BIL, CLM, UW, WRK, DAT), no P2/P3 in events; no cardholder data (BIL, CHN, PLT); event latencies (WRK ≤ 10 s, CHN notification ≤ 10 s, FIN posting ≤ 10 s, RI recovery ≤ 60 s, DAT ≤ 2 min) on top of 5 s delivery; close order: BIL & CLM totals to FIN by 06:00, FIN by 07:00 next business day; BIL closes ≤ BD2 before FIN locks ≤ WD+3; DAT signs off ≤ 5 business days after lock; determinism/replay 100% reproducibility (RAT, PFC, POL, BIL, FIN, RI, MKT, DOC).

#### 14.7 Missing targets
Retention durations for every RC class (F-329); RI daily recon completion time (F-330); clock-warning catch-up after CMP outage (F-331); parallel evaluation of bind gates (F-332).

### §15 Persona and screen inventory (l.2582–2714)

#### 15.1 Merged persona register
Contract §3.2.5: **47 roles (ROLE-01…47) + 4 system actors (SYS-01…04)**; PRDs add personas only as "X (specialises ROLE-nn)". The register maps each role to its main screens per module (the per-persona screen inventory). Highlights: ROLE-01 Customer and ROLE-05/06 Agent/Broker and ROLE-08 Bank employee reach owner modules only via CHN screens (CHN 01–26); ROLE-03 CSR and ROLE-04 Policy services span PTY, POL, BIL, CLM, DOC, WRK; ROLE-10…13 underwriting (UW, POL, RAT); ROLE-14/15 product & configuration (PFC, RAT, DOC, MKT); ROLE-16…21 claims (CLM, WRK); ROLE-22…28 billing, finance, tax, reinsurance (BIL, FIN, RI, CMP); ROLE-29 Compliance and ROLE-30 DPO touch nearly every module; ROLE-33 data steward; ROLE-34/35 actuaries (RAT, DAT, FIN); ROLE-37…40 IT/SRE/security/release (PLT); ROLE-41 migration; ROLE-42 market-entry (MKT); ROLE-43 auditor read-only everywhere; ROLE-44 BoG supervisor (exports/evidence room; inconsistent F-346); ROLE-45 team leader; ROLE-46 legal reviewer/translator; ROLE-47 executive (DAT 04). SYS-01 AI agent (CHN MCP facade; **never approves**, `REQ-PLT-111`); SYS-02 batch job/workflow; SYS-03 partner/comparison site; SYS-04 vendor. Proposed **ROLE-48 Risk manager**, **ROLE-49 Model validator** (F-342; adopted in contract R-102).

#### 15.2 Persona consistency findings: F-340, F-341, F-342, F-343, F-346 (see register).

#### 15.3 Screen inventory — 324 screens
PTY SCR-PTY-01…23; PFC 01…20; RAT 01…15 (4 components embedded in POL & CHN); UW 01…20 (18, 19 thin); POL 01…21 (job workspace hosts PTY, RAT, UW, DOC, BIL components); BIL 01…17 (SCR-BIL-04 embedded in POL bind/change); CLM 01…20 (20 = content contract for CHN); RI 01…20 (12…14, 18 compact); FIN 01…14; DOC 01…17 (documents tab & forms panel embedded); CMP 01…11 (11 Should); CHN 01…32 (front ends on owner APIs only); WRK 01…26 (workbench components in PLT shell); PLT 01…28 (15…18, 20…22, 27, 28 thin); DAT 01…18; MIG 01…10; MKT 01…12. Thin screens = builder-detail risk only (F-347).

#### 15.4 Cross-module screen data
34 cross-module operations cited by screens checked against owner §9.1 (R-87), e.g. `pol.Charges.reconcile`, `bil.TaxLevy.periods`, `bil.PayeeAccount.get`, `bil.Ledger.query`, `clm.CatEvent.aggregate`, `clm.ClaimTracking.get`, `clm.Claim.search`, `dat.Population.aggregate`, `dat.Kpi.get`, `doc.Ipid.get`, `doc.Document.listForObject`, `rat.Comparison.compareVersions`, `pfc.Availability.check`, `pol.EffectiveDate.limits`, `pol.Withdrawal.submit`, `pol.Policy.getMany`, `uw.Issue.listForChannel`, `fin.ActuarialResults.*`, `mig.Routing.resolve`, `wrk.Desktop.get`, `wrk.Group.list`, `cmp.ClockDef.list`, `plt.Fx.getRate`, `ri.Accumulation.zone`. Gaps: SCR-FIN-05 needs `bil.Reconciliation.fin` (PRD-06 §9.1 defines only `bil.Reconciliation.bank`) — F-344/F-106; SCR-CLM-15 cat dashboard (Must) relies on `REQ-DAT-008` (Should) — F-345. SCR-UW-18 / SCR-DAT-08 accumulation map needs RI zone capacity (`REQ-RI-005` Won't P1, Must P2) — consistent with P2.

#### 15.5 Baseline inventory coverage
218 items (158 reference screens + 60 UI-library rows): 200 specified, 18 wholly replaced, 0 N/A, **0 gaps**. Per owner: POL 50 (46/4), PFC 31 (26/5), PLT 27 (22/5), WRK 20, UW 20, PTY 9 (8/1), DOC 9 (8/1), CLM 18 (16/2), CHN 11, BIL 9, RAT/FIN/RI/CMP 14 total; DAT, MIG, MKT own none. 2,184 field rows 100% specified/replaced/N-A (COV-002 closed).

#### 15.6 Inspiration-board patterns
All 33 IB-01…IB-33 cited by ≥1 PRD. Most cited: IB-01 command palette, IB-02 keyboard list nav, IB-03 sidebar work views, IB-05 split-pane workspace, IB-07 approve-the-diff, IB-08 explain-why, IB-21 semantic status system, IB-24 quiet chrome, IB-26 global filter bar, IB-28 exception-first view, IB-29 log-style dense table, IB-32 binary status. Least cited: IB-27 self-enriching record (PTY, UW, DAT), IB-25 task-adaptive layout (5), IB-31 agentic task run (5), IB-06 agent plan panel (6), IB-22 AI as a surface (7), IB-30 conversational analytics (7). No PRD specifies styling.

---

## Template sections not covered above (as they apply to a programme baseline)

**Size metrics (template §2):** programme totals 4,697 REQ (Must 3,785 / Should 665 / Could 110 / Won't 137); BASELINE 3,712 / ENH 985; 768 BR; 370 NFR; 324 screens; 302 events (17 producers; 92 DAT-only); 40 SPIs; 119 AI features; 167 capabilities; 38 statutory clock rows (+4 new + 1 variant proposed); 35 money flows; 22 obligations (+2 proposed); 136 findings (48 major / 88 minor); 77 part decisions; 52 programme open questions (XMR-OQ-001…052, detail outside range). **Motor MVP (P1) Must count: 3,710** (S4 cut, per §3.3; Annex B / §16 outside range). S/M/L: n/a — PRD-18 is not a buildable module; its own new build items are XMR-FR-150 (bitemporal convention, contract rule), XMR-FR-200 (capacity model doc + NFR resizing), XMR-FR-250 (E2E suite gate), plus the changes it pushes into modules (three new BIL disbursement sources, two new BIL events, `TaxCalculator.treatment`, PLT `AiSystemStatusChanged` handler, CMP XBRL renderer, four new clocks).

**AI features (template §12):** 119 AI features programme-wide; this range only adds: CMP `AiSystemStatusChanged` must disable via PLT within 60 s (F-118, D-110); kill switch ≤ 60 s; AI never approves (`REQ-PLT-111`); AI Act high-risk obligations from 2 Dec 2027, content marking from 2 Dec 2026 (Reg. (EU) 2026/1744); pricing and fraud models classified high-risk-controls by default (contract §3.8.1); E2E-10 tests modules fall back together (F-418); R3-008 AI-PFC-04 bias-testing mismatch.

**Controls (template §11):** maker-checker via PLT `ApprovalRequested`/`ApprovalDecided`, content-hash bound, `plt.Approval.verifyForExecution` (§8.3); four-eyes on `TAX_REMITTANCE` (F-216) and on fiscal doc cancellation of Rejected (§9.2.8); `governance_stage` mapping as maker-checker evidence (F-203); CLM stop of disbursement must use BIL authority type (F-343); SoD `BR-PLT-005(e)` relates to payee cooling-off (§11.4); journals sourced from module events corrected only via source module (F-201); audit records 200 m/yr/stamp.

**Greek-market specifics (template §10):** AFM mod-11; GEMI; DOY; VIES EL prefix; AADE RgWsPublic2; myDATA MARK/UID, TRANSACTION trigger for motor, MARK-before-delivery for premium receipts, fiscal credit doc types 5.1/11.4; Information Centre (transport unpublished; interim manual file, D-265); gov.gr Wallet pre-fill, gov.gr co-signing; ELOT 743 Type 2 transliteration; ELOT 928/Windows-1253 legacy encoding; Orthodox Easter calendar; IPT 15%/20% (4% life); Aux Fund levy 6% ceiling 70/30 (components open); Friendly Settlement clearing office; Hellenic Motor Insurers' Bureau; BoG Acts 87/2016, 88/2016; A.1004 (10 January), ENFIA (28 February); EAEE statistics; KAD codes; EUR with Fairfax USD group reporting; Greek is binding document language; R-101 EL/EN UI switch (contract v1.11).

---

## C. Conflicts and ambiguities

### C.a With the infra stack (`core-insurance-infra/ARCHITECTURE-DECISIONS.md`)
1. **Workflow engine.** PRD-18 XC-132 assigns PLT a "Workflow engine, rules runtime, jobs" (`REQ-PLT-007`); §8.4 lists `plt.Workflow.*` as a cross-module operation; §11.1 says clocks run in a "workflow" (CMP engine; PLT MIRROR); contract CD-07 "PLT provides the workflow engine"; contract D10 accepts an "open-source, self-operated … workflow engine". **Infra: no workflow servers/BPM; Hangfire runs due work, deadlines are domain records.** Resolution needed: `plt.Workflow.*` = Hangfire-backed state-machine + timer service; no engine.
2. **Event stream / broker concepts.** §8.1 topics `<mod>.events.v<major>`, partition keys (R-100), schema registry (`REQ-PLT-142`), replay, DLQ (`DeadLetterParked`), consumer groups; contract D10 "open-source, self-operated event stream". **Infra: transactional outbox dispatched in order to in-process handlers; no broker.** Partition-key ordering, replay, DLQ and "schema registry" must be re-expressed as outbox ordering per aggregate key, handler checkpoints, parked-message table and a code-generated event catalogue. Throughput target ≥ 2,000 events/s sustained, burst 5,000/s, 180 m/yr design (XMR-FR-200) must be proven on PostgreSQL outbox + in-process dispatch.
3. **Lakehouse.** DAT XC-142 "Lakehouse layers", bronze/silver/gold, "open-format transactional tables" (glossary), `LakehouseErasureCompleted`, `dat.Erasure.execute`, crypto-shredding "for the lakehouse" (F-329), feature store; contract D10 "open-source … lakehouse table format". **Infra: explicitly no lakehouse; reporting/regulatory marts are PostgreSQL schemas built by scheduled jobs.**
4. **DAT log-based CDC from every module DB** (`REQ-DAT-050…056`, accepted as named exception D-103). **Infra rule 8: a module never reads another module's tables** (NetArchTest). CDC backstop would need a decision (e.g. drop, or PostgreSQL logical replication into DAT schema as a sanctioned exception).
5. **Identity.** PLT "custom-built identity service (staff and external realms)" (contract CD-20, §2; PRD-18 XC-127; NFR identity ASVS L3, 99.95%, token lifetime ≤ 10 min). **Infra: Microsoft Entra ID (staff) and Entra External ID (customers, brokers, bank staff); never build our own identity.** Direct conflict with CD-20 wording; PRD-18 NFRs need re-mapping to Entra capabilities (token lifetime, MFA, federation for bank staff `REQ-PLT-057`).
6. **Vendor neutrality (CD-20) vs named Azure services.** Contract forbids naming vendor platforms; infra names Azure Container Apps, PostgreSQL Flexible Server, Key Vault, Blob, App Insights, Communication Services, Gotenberg, Entra. Not a functional conflict but every PRD "generic capability" must be mapped (KMS → Key Vault; rendering engine → Gotenberg PDF/A; e-mail → Communication Services).
7. **Rule-expression language.** Contract D10 (accepted): one typed deterministic CEL-compatible rule language implemented in-house, shared by PFC, UW and PLT rules runtime. Not in infra; a significant build item with no listed library (infra "minimal dependencies").
8. **"Change set = Git branch to pull request"** for PFC authoring (glossary) implies Git integration inside the product — not addressed in infra.
9. **MCP AI agent facade** (CHN XC-113, SYS-01) and AI gateway/control plane (PLT) — no AI infrastructure listed in infra.
10. Compatible items (no conflict): PostgreSQL bitemporal `tstzrange`/half-open intervals (XMR-FR-150) align with infra exclusion constraints; RFC 9457 + `Idempotency-Key` (§8.4) align with infra rule 6; OpenTelemetry (§14.1) aligns; append-only ledgers and "one posting source per fact" align with infra rule 1/4; maker-checker/audit align with rule 9; country packs behind SPIs align with rule 10 (infra lists IdValidator, TaxCalculator, FiscalDocumentChannel, BureauAdapter, StatutoryClockSet; PRD-18 has 40 SPIs); Cyprus stub in CI aligns.

### C.b With `00-system-contract.md`
1. **Contract version drift.** PRD-18 body binds contract v1.10 (R-01…R-100); §1.2 says frozen against **v1.12**; contract v1.11 accepted D1–D10 and R-101/R-102; XMR-CR-ALL-01 (in contract R-102) says PRDs cite "v1.11". Builders should use contract v1.12 + PRD-18 §6/§8/§9 as records of definition.
2. **Completeness field names (F-114 vs D4).** PRD-18 proposes `transaction_delta_count`, `transaction_delta_index`, `aggregate_member_count`, `aggregate_member_index`; contract D4 (accepted) says "completeness fields (`set_id`, `set_size`, `index`)". **Conflict — contract D4 should win; PRD-18 §4/§5/§8 text is stale.**
3. **`TAX_REMITTANCE` scope.** PRD-18 §5.2 narrows D-156 to "IPT and stamp-duty payment"; contract D1 says "IPT, levy and stamp-duty payments to authorities" (levy remittance already exists via `BIL_AUXF_REMIT`). Ambiguous whether levy remittance moves to the new source.
4. **IPT liability point.** PRD-18 F-212: "default DUE unless opinion supports WRITTEN"; `REQ-MKT-328` Greece default WRITTEN; contract D2 sets **DUE** as interim default. Builders: DUE.
5. **CD-07 "PLT provides the workflow engine"** and contract §2 "core language Kotlin or .NET", "custom-built identity service" vs infra (see C.a); D10 fixes .NET.
6. **Correlation id.** Contract §3.2.3/§3.5.6 promised one correlation id quote→journal; PRD-18 F-116/D-105 (accepted in D5) replaces with business lineage. Contract §3.3/§3.4.2 now point to PRD-18 records.
7. **R-39 partition key** wording — amended by R-102 (XMR-CR-CON-01).
8. **Written premium** contract §3.3 definition superseded by D5/§6.

### C.c Internal contradictions within lines 1–2715
- Document control says "Frozen 1.0" (v1.1) while §2 says "Do not freeze the build baseline today" and §5 gates are "before the freeze" — body not updated after freeze.
- F-321, F-322, F-323 marked "DA: No" in §4.4 but §5.1 lists decision D-207 for them.
- Money flow M-29 (deposit, adjustment premiums) "P3" vs F-132 resolution "deposit premium moves to P1" (G1 before W7).
- F-213 says "MKT already hard-codes 4.2% / 1.8%" while §13.4 says `REQ-FIN-190` holds "4.5% and 1.5%" — two different hard-coded readings of the levy split.
- §14.2 intermediaries 20,000 vs 25,000 intermediary users (F-328); CMP "1.2 million motor vehicles" vs 840,000 motor policies.
- §11.2 row 6/7 renewal notices: statutory floor unknown while business lead is 45 days motor (BR-POL-026) and MIG T-45/T-90 (F-412).
- DOC T1 path with T2 RTO (F-324); CMP clock engine T2 vs 60 s warnings (F-331).
- Payee cooling-off 30 days (BIL) vs 72 hours (CLM) (F-234).
- `plt.Config.*` duplicates `mkt.Configuration.*` (F-102).
- `REQ-CLM-155` dangling §9.4.40 reference (F-411).

### C.d Cannot be built without a decision / external input
- Tax/legal opinion values: IPT liability point (interim DUE), Aux Fund levy split / stamp duty / MTA base / refund (keys only, guarded golden), withdrawal-void IPT treatment, instalment fees in IPT base, ΠΟΛ 1028/2017 continued validity. Legal sign-off = go-live gate.
- myDATA document types and trigger confirmations (D-258/D3); cover-note permissibility opinion (D-264).
- Information Centre transport/format/deadline (interim manual file).
- Retention schedule durations (D-259) — blocks purge/legal hold/DSAR retention-end tests.
- Renewal-offer statutory notice value (shared `POL_RENEWAL_NOTICE` / `mig.renewal.offer_lead_days`).
- 22 of 32 Greece-pack rows not Settled (17 on motor path) — production activation gate.
- RI A-01 (motor XoL-only) confirmation from Fairfax programme docs.
- Migration scenario A vs B (plan on B; board before W5).
- Bancassurance in/out (out unless partner signed by G0).
- BoG DORA incident channel (GR-32 UNVERIFIED; manual fallback).
- B2B e-invoicing applicability to insurance (Uncertain; `NotRequired` default).
- MTPL third-party notice 16 days (P.D. 237/1986 Art. 11a, U).
- Infra mapping decisions in C.a (workflow, event stream, lakehouse, CDC, identity).

---

## D. Build notes
- **Hardest:** (1) the money path end-to-end — POL charge deltas with completeness sets → BIL netting/scheduling → CMP fiscal numbering → FIN single posting source, with three new BIL sources (`FS_CLEARING`, `CMP_REDRESS`, `TAX_REMITTANCE`) and disbursement terminal events; (2) `TaxCalculator.treatment` pack rule consumed consistently by RAT/POL/BIL/FIN with guarded golden expectations; (3) the 38+5-row statutory clock register on Hangfire + CMP domain records with catch-up ("late" flag) semantics; (4) bitemporal convention XMR-FR-150 across POL/RI/DAT/FIN; (5) proving ≥ 2,000 events/s and 150k-term renewal in ≤ 4 h on an in-process outbox.
- **Must exist first (W1/W2 per §2):** PLT (identity mapping to Entra, audit, numbering, time service, outbox/dispatch, authority & maker-checker, Hangfire jobs), MKT (config layers, SPI binding, Greece pack + Cyprus stub, `StatutoryClockSet`, `TaxCalculator`), PFC, PTY, DOC, WRK masters. Glossary §6, event catalogue §8 and canonical models §9 should be codified as shared contract projects before module build.
- **Slicing suggestion:** freeze event contract v1 (with contract-D4 field names) and state machines as code-level contracts first; then E2E-01 (quote→bind→invoice→fiscal→payment→ledger) as the tracer bullet across POL/BIL/CMP/FIN; then claims E2E-02 with disbursement terminal events; then clocks (BIL_NONPAY_NOTICE → cancellation E2E-07; withdrawal E2E-08).
- **Ignore for motor MVP:** proportional RI (P2 unless A-01 fails), M-29 except deposit premium, XBRL renderer build (G1, not P1 motor path but needed before SII filings), A.1004/ENFIA (P2), nat-cat refusal rule (P2/P3), DigitalIdentityProvider/IdentityFederationProvider (optional).
