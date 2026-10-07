# PRD-18 digest D — lines 6701–8944 (Annex B.1 remainder, B.2, B.3, B.4)

Source: `core-insurance-prds/PRD-18-programme-requirements-baseline.md`, lines 6701–8944 (every line read). Annex B = "Motor MVP cut at requirement level (detail for §16)". Note: the B.1 per-module subsections are headed `A.1.n <MOD> (N Musts, P points)` in the source even though they sit under `## B.1 Motor MVP cut (phase P1 Musts) by module` (line 4797); this digest keeps the source numbering.

Column legend (source): J = journey (J0–J13), W = wave (W1–W9), Sz = size (S/M/L/XL), Tag = B (baseline parity) or E (enhancement), Deps = Must deps in other modules ("REQ-" prefix dropped). Titles are the first words of the source summary (the source itself truncates summaries).

## 1. B.1 Motor MVP cut (P1 Musts) — remainder by module

### 1.0 Counts

| Module | Section | Heading (Musts / points) | Rows in range | Started before 6701? | S | M | L | XL | B | E |
|---|---|---|---|---|---|---|---|---|---|---|
| BIL | A.1.8 (heading line 6469) | 294 / 442 | 66 | YES — list starts line 6469; only tail BIL-280…BIL-353 in range | 50 | 16 | 0 | 0 | 57 | 9 |
| CLM | A.1.9 (line 6768) | 193 / 338 | 193 | no | 85 | 93 | 4 | 11 | 177 | 16 |
| RI | A.1.10 (line 6966) | 91 / 145 | 91 | no | 51 | 34 | 2 | 4 | 90 | 1 |
| FIN | A.1.11 (line 7062) | 242 / 360 | 242 | no | 159 | 70 | 2 | 11 | 238 | 4 |
| DOC | A.1.12 (line 7309) | 250 / 351 | 250 | no | 177 | 61 | 4 | 8 | 207 | 43 |
| CMP | A.1.13 (line 7564) | 183 / 259 | 183 | no | 135 | 38 | 1 | 9 | 166 | 17 |
| CHN | A.1.14 (line 7752) | 212 / 377 | 212 | no | 78 | 119 | 7 | 8 | 204 | 8 |
| WRK | A.1.15 (line 7969) | 231 / 315 | 231 | no | 178 | 42 | 1 | 10 | 168 | 63 |
| DAT | A.1.16 (line 8205) | 212 / 285 | 212 | no | 165 | 37 | 2 | 8 | 209 | 3 |
| MIG | A.1.17 (line 8422) | 167 / 302 | 167 | no | 55 | 101 | 5 | 6 | 167 | 0 |

Total requirement rows in range: 1847 (BIL partial). For every module fully in range the row count equals the heading Must count. Modules before BIL (PLT, MKT, PTY, PFC, RAT, UW, POL, BIL head) are outside this range.

### 1.1 BIL (66 rows in range)

J/W mix: J4/W5×57, J7/W7×9

| ID | J/W | Sz | Tag | Title (first words) | Deps |
|---|---|---|---|---|---|
| BIL-280 | J4/W5 | S | B | enforce that every entry balances to zero per currency… | — |
| BIL-281 | J4/W5 | S | B | make posted entries and lines append-only through database triggers… | PLT-046 |
| BIL-282 | J4/W5 | S | B | define the sub-ledger chart of accounts (LA-01LA-21, section 7.1.4)… | MKT-001 |
| BIL-283 | J4/W5 | S | B | carry dimensions on each line: legal entity, jurisdiction, billing… | FIN-001 |
| BIL-284 | J4/W5 | S | B | store accounting date (business date per cut-off) and record… | PLT-132 |
| BIL-285 | J4/W5 | S | B | derive balances (per account, term, invoice item, receipt, intermediary,… | PLT-013 |
| BIL-286 | J4/W5 | M | B | generate entries only through a billing-ledger-rule table keyed by… | PLT-004, MKT-001 |
| BIL-287 | J4/W5 | S | B | refuse to post when no rule matches an event… | WRK-001 |
| BIL-288 | J4/W5 | S | B | correct entries only by reversal (exact negation linked to… | — |
| BIL-289 | J4/W5 | S | B | group multi-leg business operations (for example receipt and allocation,… | — |
| BIL-290 | J4/W5 | S | B | provide manual sub-ledger adjustments only for named adjustment types… | PLT-004 |
| BIL-291 | J4/W5 | S | B | maintain sub-ledger periods (daily accounting dates and monthly periods)… | FIN-007 |
| BIL-292 | J4/W5 | S | B | post late events after month close in the open… | — |
| BIL-293 | J4/W5 | S | B | expose bil.Ledger.query for entries and lines by any dimension… | PLT-079 |
| BIL-296 | J4/W5 | S | B | run invariant checks continuously on each commit for local… | — |
| BIL-297 | J4/W5 | S | B | partition ledger tables by legal entity and month and… | PLT-011 |
| BIL-298 | J4/W5 | S | B | provide the ledger explorer screen (SCR-BIL-15 panel) with entries,… | — |
| BIL-299 | J4/W5 | S | B | keep pending (cash in transit) and posted cash separately… | — |
| BIL-300 | J4/W5 | S | B | forbid any module other than BIL from writing to… | PLT-320 |
| BIL-301 | J4/W5 | S | B | keep a register of the legal entity's bank accounts… | PLT-004 |
| BIL-302 | J4/W5 | S | B | reconcile each statement line with BIL entries automatically by… | MKT-100 |
| BIL-303 | J4/W5 | S | B | reconcile batch debits (one bank debit for a disbursement… | — |
| BIL-304 | J4/W5 | S | B | list reconciliation breaks (unmatched bank lines, unmatched BIL cash… | — |
| BIL-305 | J4/W5 | S | B | allow manual matching (one-to-one, one-to-many, many-to-many) with reason, and… | — |
| BIL-307 | J4/W5 | S | B | compare statement closing balances with the sub-ledger cash account… | — |
| BIL-308 | J4/W5 | S | B | detect missing statements (by schedule) and duplicate or out-of-order… | PLT-013 |
| BIL-309 | J4/W5 | S | B | provide the bank reconciliation screen (SCR-BIL-13). | — |
| BIL-310 | J4/W5 | S | B | reconcile the levy-remittance and claim-payment bank accounts in the… | — |
| BIL-312 | J4/W5 | S | B | report reconciliation status per bank account and day (auto-matched… | DAT-001 |
| BIL-313 | J4/W5 | M | B | publish one billing business event per ledger entry type… | FIN-001 |
| BIL-314 | J4/W5 | S | B | compute daily control totals per sub-ledger account, event type,… | FIN-007 |
| BIL-315 | J4/W5 | S | B | re-publish events for a date range on FIN request… | PLT-145 |
| BIL-316 | J4/W5 | S | B | supply FIN with the earned/unearned basis only by reference… | POL-118 |
| BIL-317 | J4/W5 | S | B | verify daily that the sum of invoice items equals… | — |
| BIL-318 | J4/W5 | S | B | verify that written = billed + unbilled and billed… | — |
| BIL-319 | J4/W5 | S | B | verify that no cancellation-sourced credit has reduced the IPT… | — |
| BIL-320 | J4/W5 | S | B | verify that allocations never exceed receipts and that the… | — |
| BIL-321 | J4/W5 | S | B | run the invariant suite (section 14.x) nightly and on… | PLT-012 |
| BIL-322 | J4/W5 | S | B | reconcile BIL commission payable and disbursement totals with FIN… | FIN-001 |
| BIL-323 | J4/W5 | S | E | provide a billing operations home (work-left, IB-11) with cards… | — |
| BIL-324 | J4/W5 | M | B | monitor billing batch runs (invoice, collection, dunning, statement, commission,… | PLT-171, PLT-173 |
| BIL-325 | J4/W5 | M | B | register its activity patterns with WRK (BIL-DELINQUENCY, BIL-NONPAY-CANCEL hold,… | WRK-001, WRK-002, WRK-043 |
| BIL-326 | J4/W5 | S | B | register authority types with PLT: BIL.Refund, BIL.WriteOff, BIL.PaymentArrangement, BIL.BatchRelease,… | PLT-100 |
| BIL-327 | J4/W5 | S | B | register approval types with PLT maker-checker per section 12. | PLT-113 |
| BIL-328 | J4/W5 | S | B | write audit events for every change of billing data,… | PLT-002 |
| BIL-330 | J4/W5 | S | B | check MKT capability switches for billing features per entity… | MKT-004 |
| BIL-331 | J4/W5 | S | B | obtain the current time only from the PLT time… | PLT-332 |
| BIL-332 | J4/W5 | M | B | publish all BIL events through the outbox in the… | PLT-005, PLT-137 |
| BIL-334 | J4/W5 | S | B | classify BIL personal data per attribute (section 7.1) and… | PLT-079 |
| BIL-335 | J4/W5 | S | B | provide a DSAR export of a party's billing data… | CMP-005 |
| BIL-336 | J4/W5 | S | B | respond to erasure requests by restricting processing where retention… | PLT-011 |
| BIL-337 | J4/W5 | S | B | apply retention classes RC-BIL-LEDGER, RC-BIL-INSTRUMENT, RC-BIL-MANDATE, RC-BIL-CORRESP and RC-BIL-STATEMENT… | PLT-011 |
| BIL-338 | J4/W5 | S | B | keep personal data out of events beyond ids and… | PLT-245 |
| BIL-339 | J4/W5 | S | B | reconcile migrated opening balances to legacy totals per account… | MIG-005 |
| BIL-340 | J4/W5 | M | B | declare degraded modes per external dependency (bank adapters queue;… | PLT-159, PLT-164 |
| BIL-341 | J4/W5 | S | E | provide configuration screens for payment plans, delinquency plans, waterfall,… | MKT-001 |
| BIL-342 | J4/W5 | M | B | expose all BIL query and command APIs to CHN… | CHN-003, PLT-060 |
| BIL-343 | J7/W7 | M | E | be the system of record for payee bank accounts… | CLM-131, PLT-287 |
| BIL-344 | J7/W7 | M | E | provide bil.PayeeAccount.verify running PayeeVerification on a stored account or… | MKT-102, CLM-134 |
| BIL-345 | J7/W7 | M | E | provide bil.PayeeAccount.get/list by party, purpose and as-of date, returning… | PLT-079, PTY-001 |
| BIL-346 | J7/W7 | M | B | accept from CLM a receivable request for non-premium amounts… | CLM-146 |
| BIL-348 | J7/W7 | M | E | accept disbursement requests with source type RI_SETTLEMENT from RI… | RI-192, PTY-006 |
| BIL-349 | J7/W7 | M | E | register reinsurer receivables on RI's request (bil.Receivable.register with source… | RI-192, MKT-098 |
| BIL-350 | J7/W7 | M | E | match incoming reinsurer cash (statement lines, including non-SEPA credits… | RI-192 |
| BIL-351 | J7/W7 | M | E | post RI settlement disbursements and reinsurer receipts to dedicated… | FIN-038, FIN-244 |
| BIL-353 | J7/W7 | M | B | consume AiToggleChanged and AiKillSwitchActivated and, within the PLT target… | PLT-010, PLT-219, PLT-223 |

### 1.2 CLM (193 rows in range)

J/W mix: J7/W7×192, J13/W9×1

| ID | J/W | Sz | Tag | Title (first words) | Deps |
|---|---|---|---|---|---|
| CLM-001 | J7/W7 | XL | B | expose the FNOL API clm.Fnol.submit, clm.Fnol.saveDraft, clm.Fnol.get and clm.Fnol.validate… | PLT-209, CHN-001 |
| CLM-002 | J7/W7 | XL | B | verify coverage at FNOL against the POL snapshot valid… | POL-007, POL-086 |
| CLM-003 | J7/W7 | XL | B | record all claim money as immutable claim financial transactions… | PLT-003, PLT-004 |
| CLM-004 | J7/W7 | XL | B | request disbursement of every approved claim payment through REQ-BIL-009,… | BIL-009, PTY-006, PTY-175 |
| CLM-005 | J7/W7 | XL | B | publish claim financial events (ExposureCreated, ReserveChanged, TransactionSetApproved, PaymentIssued, PaymentVoided,… | RI-003, FIN-001, FIN-011, PFC-005 |
| CLM-006 | J7/W7 | XL | B | maintain catastrophe events (code, name, perils, start and end… | RI-003 |
| CLM-007 | J7/W7 | XL | B | start, pause, resume and stop the statutory offer clock… | CMP-003, MKT-009 |
| CLM-008 | J7/W7 | XL | B | issue the claims-history certificate on request of a policyholder… | MKT-108, DOC-001, POL-002 |
| CLM-009 | J7/W7 | XL | B | provide clm.ClaimTracking.get and clm.ClaimTracking.list returning, for a claim, party… | CHN-004, CHN-005, PTY-264 |
| CLM-010 | J13/W9 | XL | B | provide clm.Import.claim and clm.Import.financialHistory for migration, loading claims, exposures,… | MIG-001, MIG-002, MIG-127, MIG-133 |
| CLM-011 | J7/W7 | XL | B | provide clm.Claim.search by claim number, exposure number, party (via… | PTY-068, MKT-178, WRK-006, WRK-328 |
| CLM-030 | J7/W7 | M | B | capture a common FNOL core for every line: loss… | PLT-332, PTY-012 |
| CLM-031 | J7/W7 | M | B | capture motor FNOL data: insured vehicle from the snapshot… | POL-010 |
| CLM-033 | J7/W7 | M | B | accept third-party claims against our insured identified by our… | — |
| CLM-035 | J7/W7 | M | E | define FNOL question sets per product, line and loss… | MKT-001, PFC-006 |
| CLM-036 | J7/W7 | L | B | record for every claim and third-party claim the receipt… | DOC-005 |
| CLM-037 | J7/W7 | M | B | accept photos and videos at FNOL from customers, brokers… | WRK-005, CHN-004 |
| CLM-039 | J7/W7 | S | E | save FNOL drafts automatically while being entered, resumable by… | — |
| CLM-040 | J7/W7 | S | B | accept joint accident report data (both drivers, vehicles, insurers,… | WRK-268 |
| CLM-041 | J7/W7 | M | B | detect probable duplicate claims at FNOL and on every… | — |
| CLM-043 | J7/W7 | S | B | issue the claim number only when the FNOL is… | PLT-209 |
| CLM-044 | J7/W7 | S | B | record the FNOL snapshot (all data as submitted, channel,… | PLT-002 |
| CLM-045 | J7/W7 | S | E | accept FNOL submissions while POL is degraded by queuing… | POL-007 |
| CLM-046 | J7/W7 | M | B | provide next-step guidance at FNOL completion: required documents for… | PTY-005, DOC-001 |
| CLM-047 | J7/W7 | M | B | search policies for FNOL by plate, VIN, policy number,… | POL-014 |
| CLM-048 | J7/W7 | M | B | compute coverage indications from the snapshot: whether the vehicle… | POL-007, PFC-003, PFC-076 |
| CLM-049 | J7/W7 | M | B | handle losses reported against policies cancelled, lapsed, expired, suspended… | WRK-001 |
| CLM-050 | J7/W7 | M | B | allow a claim against an unverified policy (policy not… | PLT-003 |
| CLM-051 | J7/W7 | M | B | record a coverage decision per exposure: Covered, CoveredWithReservation (reservation… | PLT-003, PLT-110 |
| CLM-052 | J7/W7 | M | B | produce a reservation-of-rights letter (DOC type DT-RESERVATION-OF-RIGHTS) through DOC… | DOC-001, DOC-005 |
| CLM-053 | J7/W7 | M | B | produce a denial letter (DOC type DT-CLAIM-DENIAL) through DOC… | DOC-001, DOC-042, CMP-004 |
| CLM-056 | J7/W7 | S | B | apply deductibles and limits from the snapshot to payments:… | PFC-076 |
| CLM-057 | J7/W7 | M | B | consume TransactionReversed and PolicyChanged from POL, identify claims whose… | POL-107, WRK-185 |
| CLM-058 | J7/W7 | M | B | present re-verification as a side-by-side diff of the old… | — |
| CLM-059 | J7/W7 | S | B | publish CoverageVerified when coverage is first verified and whenever… | WRK-195 |
| CLM-060 | J7/W7 | S | B | expose reported, open and paid claims per vehicle, location… | POL-141 |
| CLM-061 | J7/W7 | M | B | hold a Claim with claim number, policy reference and… | — |
| CLM-062 | J7/W7 | M | B | hold Exposures each tying exactly one coverage (coverage code… | PFC-003 |
| CLM-063 | J7/W7 | S | B | reject an exposure that would duplicate an open exposure… | — |
| CLM-064 | J7/W7 | M | B | hold Incidents: vehicle incident (vehicle, damage areas, drivable, location,… | — |
| CLM-065 | J7/W7 | S | E | propose exposures from FNOL facts (loss cause, involved vehicles… | — |
| CLM-066 | J7/W7 | M | B | hold claim contacts as references to PTY parties with… | PTY-001, PTY-002 |
| CLM-067 | J7/W7 | M | B | create or resolve the PTY party for a new… | PTY-001, PTY-068 |
| CLM-068 | J7/W7 | S | B | record a "contact prohibited" flag and preferred contact time… | — |
| CLM-070 | J7/W7 | S | B | re-point claim party references on PartiesMerged and reverse them… | PTY-007 |
| CLM-071 | J7/W7 | M | B | enforce the claim state model (Draft, Open, Closed; Closed… | — |
| CLM-072 | J7/W7 | S | B | close an exposure only when its open reserves are… | — |
| CLM-073 | J7/W7 | S | B | close the claim when all exposures are Closed and… | — |
| CLM-074 | J7/W7 | S | B | reopen a closed claim or exposure with a coded… | PLT-003 |
| CLM-075 | J7/W7 | S | B | show "open N days", age since notice, and time… | PLT-332 |
| CLM-076 | J7/W7 | M | B | hold a claim-level fault assessment (insured at fault 0%,… | — |
| CLM-077 | J7/W7 | S | B | track high-risk indicators on the claim (large loss, litigation,… | — |
| CLM-078 | J7/W7 | S | B | apply vulnerable-customer handling rules from pty.Vulnerability.query to claim communication… | PTY-011 |
| CLM-079 | J7/W7 | M | B | classify injury, medical and health-related fields as P3, encrypt… | PLT-001, PLT-127 |
| CLM-080 | J7/W7 | S | B | record the lawful basis for processing health data per… | PTY-151 |
| CLM-081 | J7/W7 | S | B | keep a full claim history timeline (state changes, assignments,… | PLT-128 |
| CLM-083 | J7/W7 | S | B | assign a handling segment (FastTrack, Standard, Complex, LargeLoss) at… | PLT-007 |
| CLM-084 | J7/W7 | S | B | re-evaluate the segment when reserves cross configured thresholds, injuries… | WRK-001 |
| CLM-085 | J7/W7 | M | B | assign the claim handler participant and exposure handlers at… | WRK-010, WRK-120, WRK-302 |
| CLM-086 | J7/W7 | S | B | show "Pending assignment" with the queue when no handler… | WRK-010 |
| CLM-087 | J7/W7 | S | E | apply fast-track handling: simplified required documents, auto-reserve, and straight-through… | PLT-003 |
| CLM-088 | J7/W7 | M | B | raise the triage activity CLM-FNOL-TRIAGE for Standard and above,… | WRK-001, WRK-002, WRK-185 |
| CLM-090 | J7/W7 | S | B | record a large-loss notification (estimate, cause, reinsurance relevance) for… | RI-003 |
| CLM-093 | J7/W7 | S | B | create reserve lines per exposure × cost type (Indemnity,… | MKT-001 |
| CLM-094 | J7/W7 | S | B | set initial reserves at exposure creation from configured rules:… | PLT-007 |
| CLM-095 | J7/W7 | S | B | compute open reserve per line as Σ reserve transactions… | — |
| CLM-096 | J7/W7 | M | B | compute paid, incurred (paid + open reserve), recoveries, open… | — |
| CLM-097 | J7/W7 | S | B | when an eroding payment exceeds the open reserve of | — |
| CLM-098 | J7/W7 | S | B | require a reason code and optional note for every… | PLT-002 |
| CLM-099 | J7/W7 | S | B | propose a final reserve release on exposure closure and… | — |
| CLM-100 | J7/W7 | M | B | maintain balance projections per line, exposure and claim updated… | PLT-013 |
| CLM-101 | J7/W7 | S | B | provide clm.Financials.get(claimId, asOf) returning balances as of any past… | — |
| CLM-102 | J7/W7 | S | B | support recovery reserves per line (expected subrogation, salvage, deductible,… | — |
| CLM-103 | J7/W7 | M | B | hold every claim financial transaction in transaction currency with… | PLT-009, MKT-006 |
| CLM-104 | J7/W7 | S | B | round all amounts with mkt.Rounding.apply per currency and purpose. | MKT-195 |
| CLM-106 | J7/W7 | M | B | reconcile daily claim financial totals (by entity, currency, cost… | FIN-001, DAT-007 |
| CLM-107 | J7/W7 | M | B | build a transaction set from one or more reserve,… | — |
| CLM-108 | J7/W7 | M | B | run plt.Authority.check for every transaction in the set on… | PLT-003, PLT-100, PLT-103 |
| CLM-109 | J7/W7 | L | B | route a referred set to approvers whose authority allows… | PLT-004, PLT-117, WRK-200, WRK-202 |
| CLM-110 | J7/W7 | M | B | require a second, different authorised approver (four-eyes) for sets… | PLT-004, PLT-115 |
| CLM-111 | J7/W7 | S | B | approve or reject a set as a unit; rejection… | — |
| CLM-112 | J7/W7 | S | B | re-validate a set at approval (balances, limits, payee screening,… | PLT-117 |
| CLM-113 | J7/W7 | S | B | publish TransactionSetApproved and the per-line financial events on approval… | FIN-001 |
| CLM-114 | J7/W7 | M | E | provide an approval inbox for reserves, payments, recoveries, coverage… | — |
| CLM-116 | J7/W7 | S | B | let an approver approve with a reduced amount only… | PLT-115 |
| CLM-119 | J7/W7 | M | B | support payment types Partial, Final (closes the line's reserve),… | — |
| CLM-120 | J7/W7 | S | B | split one payment request across several reserve lines or… | BIL-009 |
| CLM-121 | J7/W7 | M | B | record payees and co-payees from claim contacts; co-payees from… | POL-289, PLT-003 |
| CLM-122 | J7/W7 | M | B | support payment methods SEPA credit transfer, SEPA instant credit… | BIL-009 |
| CLM-123 | J7/W7 | M | B | reference payee bank accounts for claim payments by the… | BIL-343, BIL-344, BIL-345 |
| CLM-125 | J7/W7 | S | B | void an issued payment not yet cleared by requesting… | BIL-009 |
| CLM-126 | J7/W7 | S | B | request a stop of a released but not executed… | BIL-009 |
| CLM-127 | J7/W7 | S | B | reissue a voided, stopped or returned payment as a… | — |
| CLM-128 | J7/W7 | S | B | consume DisbursementIssued, DisbursementVoided and DisbursementReturned and update payment status,… | BIL-009 |
| CLM-129 | J7/W7 | S | B | detect duplicate payments before submit: same payee and amount… | — |
| CLM-130 | J7/W7 | M | B | block payments on a claim that is Draft, on… | — |
| CLM-131 | J7/W7 | L | B | treat any new or changed payee bank account as… | PLT-004, PLT-053, BIL-009, BIL-199, BIL-343 … |
| CLM-132 | J7/W7 | S | B | forbid the user who created or changed a payee… | PLT-001 |
| CLM-133 | J7/W7 | M | B | screen every payee and co-payee through pty.Screening.screen at submit… | PTY-006, PTY-182 |
| CLM-134 | J7/W7 | M | B | show the VoP result returned by BIL (match, close… | BIL-009 |
| CLM-136 | J7/W7 | S | B | let a finance payments clerk see claim payment requests… | PLT-001 |
| CLM-137 | J7/W7 | M | B | produce a payment advice to the payee (amount, claim,… | DOC-001, PTY-005 |
| CLM-138 | J7/W7 | M | B | request registration of the claim settlement receipt for each… | CMP-001, CMP-033 |
| CLM-139 | J7/W7 | S | B | handle VAT in claim payments by cost category and… | MKT-001 |
| CLM-141 | J7/W7 | S | B | deduct the policy deductible from the first indemnity payment… | — |
| CLM-143 | J7/W7 | M | B | hold Recovery cases per claim with type (Subrogation, Salvage,… | PTY-002 |
| CLM-144 | J7/W7 | S | B | propose subrogation when fault assessment shows another party liable… | — |
| CLM-145 | J7/W7 | S | B | manage salvage: total-loss vehicles with salvage value estimate, buyer… | BIL-004 |
| CLM-146 | J7/W7 | S | B | manage deductible recoveries from the insured with a demand… | BIL-004 |
| CLM-147 | J7/W7 | M | B | record recoveries received (amount, date, counterparty, reference) as recovery… | RI-003, FIN-001 |
| CLM-148 | J7/W7 | S | B | track recovery milestones (demand sent, acknowledged, liability accepted, partial… | WRK-001 |
| CLM-150 | J7/W7 | S | B | write off unrecoverable recovery reserves with reason under authority… | PLT-003 |
| CLM-155 | J7/W7 | M | B | evaluate Friendly Settlement eligibility through FriendlySettlementClearing.evaluateEligibility (R-42, REQ-MKT-313, PRD-17… | MKT-109, MKT-259, MKT-313 |
| CLM-156 | J7/W7 | S | B | gate FS functionality on capability switch cap.clm.friendly_settlement per market… | MKT-004 |
| CLM-157 | J7/W7 | M | B | Greece pack: when our customer is not at fault… | — |
| CLM-158 | J7/W7 | M | B | Greece pack: when our insured is at fault and… | — |
| CLM-159 | J7/W7 | M | B | manage FS disputes and counterparty replies: record dispute reasons,… | CMP-003 |
| CLM-160 | J7/W7 | M | B | import the monthly FS settlement statement through FriendlySettlementClearing.settlementStatement, match… | MKT-109, BIL-004 |
| CLM-161 | J7/W7 | M | B | handle cases where our insured's loss was caused by… | PTY-002 |
| CLM-162 | J7/W7 | M | B | handle recourse claims from a motor guarantee fund against… | — |
| CLM-163 | J7/W7 | M | B | handle claims where a foreign-registered vehicle caused a loss… | — |
| CLM-165 | J7/W7 | M | B | start CLM_MTPL_OFFER for each MTPL exposure whose claimant is… | CMP-003 |
| CLM-166 | J7/W7 | M | B | produce the reasoned offer (amount per head of damage,… | DOC-001, DOC-005 |
| CLM-167 | J7/W7 | M | B | record acceptance of an offer (portal with step-up, signed… | PLT-053, DOC-006 |
| CLM-168 | J7/W7 | S | B | publish StatutoryOfferDue when the offer clock is warned and… | WRK-192 |
| CLM-169 | J7/W7 | M | B | compute statutory interest after breach on the amount subsequently… | MKT-001, PLT-004 |
| CLM-170 | J7/W7 | S | B | accept certificate requests from the policyholder (portal, staff, broker… | CMP-003 |
| CLM-171 | J7/W7 | M | B | assemble certificate content from POL contract periods (REQ-POL-002) and… | MKT-108, POL-002 |
| CLM-172 | J7/W7 | M | B | render and deliver the certificate through DOC (type DT-CLAIMS-HISTORY,… | DOC-001, DOC-005 |
| CLM-174 | J7/W7 | M | B | show the claim diary: WRK activities on the claim… | WRK-001, CMP-003 |
| CLM-175 | J7/W7 | M | B | let users create on-demand activities on a claim from… | WRK-001, WRK-004 |
| CLM-176 | J7/W7 | M | B | maintain required-document checklists per claim type, exposure kind, segment… | WRK-289, WRK-195 |
| CLM-177 | J7/W7 | M | B | send document requests to customers, brokers and vendors with… | CHN-004, CHN-005 |
| CLM-178 | J7/W7 | M | B | show claim documents and photos through the DOC Documents… | DOC-008, WRK-005 |
| CLM-179 | J7/W7 | S | B | provide copies of claim documents to the insured and… | DOC-005 |
| CLM-180 | J7/W7 | S | B | show latest notes on the claim summary and the… | WRK-004 |
| CLM-181 | J7/W7 | M | E | provide a claim conversation per claim and party across… | PTY-005, DOC-005, CHN-009 |
| CLM-182 | J7/W7 | M | B | count Greek SMS segments (UCS-2: 70 characters single, 67… | PTY-159 |
| CLM-183 | J7/W7 | S | B | archive every inbound and outbound claim message through the… | DOC-004 |
| CLM-184 | J7/W7 | M | B | receive inbound customer replies (SMS, e-mail, portal), link them… | WRK-260, WRK-007 |
| CLM-185 | J7/W7 | M | B | notify claimants and brokers at milestones (claim registered, assessor… | PTY-005, DOC-001 |
| CLM-187 | J7/W7 | S | B | let staff raise a complaint in CMP from the… | CMP-004 |
| CLM-188 | J7/W7 | M | B | maintain vendor panels per service type (repair, assessment, towing… | — |
| CLM-189 | J7/W7 | S | B | propose vendors for a service by distance from the… | — |
| CLM-190 | J7/W7 | M | B | create a service request from the claim pre-filled with… | CHN-001 |
| CLM-191 | J7/W7 | M | B | start the assessment clock (CLM_MTPL_ASSESSMENT, R-40, 15 days for… | CMP-003, CMP-092 |
| CLM-192 | J7/W7 | S | B | track service status (Requested, Accepted, Declined, InProgress, WorkComplete, Cancelled)… | — |
| CLM-193 | J7/W7 | S | B | compute service-level metrics per service (response time, timeliness against… | DAT-003 |
| CLM-194 | J7/W7 | S | B | receive vendor quotes and estimates (line items, labour, parts,… | — |
| CLM-196 | J7/W7 | S | B | receive vendor invoices (reference, submitted date, amount, VAT, line… | CHN-001 |
| CLM-197 | J7/W7 | S | B | record the MARK and issuer of fiscal documents on… | CMP-001 |
| CLM-198 | J7/W7 | M | B | auto-approve and pay matched vendor invoices when rules pass… | — |
| CLM-200 | J7/W7 | S | B | show per service the related documents (report, invoice) with… | DOC-008 |
| CLM-201 | J7/W7 | M | B | evaluate rule-based fraud indicators at FNOL, on material updates… | PLT-007 |
| CLM-203 | J7/W7 | M | B | present fraud alerts to SIU triage with indicators, explanations,… | WRK-001 |
| CLM-204 | J7/W7 | M | B | open SIU cases linked to one or more claims… | WRK-307 |
| CLM-205 | J7/W7 | S | B | let the SIU investigator request a payment hold on… | — |
| CLM-206 | J7/W7 | M | B | restrict SIU case data and SIU flags to SIU… | WRK-054, WRK-328 |
| CLM-207 | J7/W7 | M | B | record SIU outcomes as labelled feedback (outcome, confirmed indicators)… | DAT-001, DAT-005 |
| CLM-208 | J7/W7 | S | B | word all customer-facing communication during an SIU review neutrally… | — |
| CLM-210 | J7/W7 | M | B | let claims managers declare catastrophe events with name (Greek… | PLT-004 |
| CLM-211 | J7/W7 | S | B | tag claims to an active event automatically when loss… | — |
| CLM-214 | J7/W7 | M | B | provide an event dashboard: claims reported per day, open,… | — |
| CLM-215 | J7/W7 | S | B | supply event-level aggregates to RI for catastrophe excess-of-loss recovery… | RI-003 |
| CLM-218 | J7/W7 | M | B | hold litigation matters per claim and exposure: court, case… | PTY-002 |
| CLM-219 | J7/W7 | S | B | assign counsel from the legal panel through a legal… | — |
| CLM-220 | J7/W7 | S | B | record court dates and deadlines as activities with reminders,… | WRK-001 |
| CLM-221 | J7/W7 | S | B | reserve and pay legal costs on cost type ExpenseAllocated… | — |
| CLM-222 | J7/W7 | S | B | require settlement authority CLM.SETTLEMENT for agreeing a litigation settlement… | PLT-003 |
| CLM-224 | J7/W7 | S | B | maintain customer-facing milestones per claim (Registered, Documents requested, Under… | — |
| CLM-225 | J7/W7 | S | B | provide plain-language status texts in Greek and English per… | MKT-005 |
| CLM-226 | J7/W7 | S | B | publish ClaimReported with claim, policy snapshot reference, loss date,… | PLT-005 |
| CLM-227 | J7/W7 | M | B | include on every financial event the accident date, notification… | PFC-005, FIN-011 |
| CLM-228 | J7/W7 | M | B | provide a reserving feed to DAT: daily case reserve… | DAT-001, DAT-004 |
| CLM-230 | J7/W7 | S | B | provide the claim search screen with criteria and results… | — |
| CLM-231 | J7/W7 | S | B | feed WRK's global search projection with claim identifiers, display… | WRK-325 |
| CLM-232 | J7/W7 | S | B | register CLM retention data sets (RC-CLM-FILE, RC-CLM-HEALTH, RC-CLM-SIU, RC-CLM-CERT)… | PLT-011 |
| CLM-233 | J7/W7 | S | B | provide DSAR export, rectification and restriction operations for claim… | CMP-005 |
| CLM-234 | J7/W7 | S | B | emit business SLIs (FNOL to first contact, FNOL to… | PLT-013 |
| CLM-235 | J7/W7 | M | B | provide an operations view of failed integrations (BIL requests,… | PLT-005, CMP-001 |
| CLM-236 | J7/W7 | S | B | reconcile migrated claims (counts, open reserves, paid to date… | MIG-005 |
| CLM-238 | J7/W7 | M | E | handle glass-only and towing/assistance-only FNOLs straight through: on submit… | PLT-103 |
| CLM-239 | J7/W7 | M | E | run the Friendly Settlement eligibility pre-check during FNOL as… | MKT-109 |
| CLM-240 | J7/W7 | M | E | provide a one-step "pay and settle" action that builds,… | — |
| CLM-241 | J7/W7 | M | E | publish ClaimUpdated within 5 seconds of every customer-facing milestone… | CHN-155, CHN-156 |
| CLM-245 | J7/W7 | M | E | compute a deterministic offer-deadline risk for each running CLM_MTPL_OFFER… | WRK-001 |
| CLM-246 | J7/W7 | S | E | propose and, where the segment rule allows, automatically appoint… | — |
| CLM-248 | J7/W7 | M | E | on online acceptance of an offer by a claimant | BIL-009 |
| CLM-251 | J7/W7 | M | E | link claims from the same accident automatically (our insured's… | — |
| CLM-258 | J7/W7 | M | B | consume AiToggleChanged and AiKillSwitchActivated and, within 60 seconds, hide… | PLT-010 |
| CLM-259 | J7/W7 | M | B | write an AiInteractionRecord for every CLM AI output and… | PLT-010, PLT-002, PLT-111 |
| CLM-260 | J7/W7 | L | E | activate an external claims integration (estimating platform, repair network,… | PLT-012, PLT-159, PLT-285, PLT-315 |
| CLM-262 | J7/W7 | M | B | record completion of a service's work either by the… | WRK-195 |

### 1.3 RI (91 rows in range)

J/W mix: J8/W7×77, J9/W8×13, J13/W9×1

| ID | J/W | Sz | Tag | Title (first words) | Deps |
|---|---|---|---|---|---|
| RI-001 | J8/W7 | XL | B | provide the programme and contract registry API — ri.Programme.get/list,… | PTY-097 |
| RI-003 | J8/W7 | XL | B | calculate non-proportional recoveries on every claim financial event —… | CLM-005, CLM-006, CLM-215 |
| RI-004 | J8/W7 | XL | B | publish every cession, recovery, commission, reinstatement, deposit, adjustment, cash-call,… | FIN-001, FIN-006, FIN-011 |
| RI-007 | J13/W9 | XL | B | provide import APIs for migration — ri.Import.programme, ri.Import.contract, ri.Import.cessionHistory,… | MIG-001, MIG-002, MIG-136, MIG-137, MIG-138 … |
| RI-030 | J8/W7 | S | B | hold reinsurance programmes with code, name (GR/EN), legal entity,… | — |
| RI-031 | J8/W7 | M | B | hold reinsurance contracts with number (from plt.Number.next(RI_CONTRACT)), type (QUOTA_SHARE,… | PLT-209 |
| RI-032 | J8/W7 | M | B | hold sections per contract with code, name, scope (legal… | PFC-093 |
| RI-033 | J8/W7 | M | B | hold layers per non-proportional section with layer number, attachment… | — |
| RI-034 | J8/W7 | M | B | hold contract clauses as typed parameters from a clause… | DOC-004 |
| RI-037 | J8/W7 | M | B | validate contract consistency before submission: periods within the programme… | — |
| RI-038 | J8/W7 | S | B | allow a contract to declare a placed percentage below… | — |
| RI-039 | J8/W7 | S | B | hold the attachment basis per contract: LOSSES_OCCURRING (loss date… | — |
| RI-040 | J8/W7 | S | B | render a programme layer diagram from the contract data:… | — |
| RI-042 | J8/W7 | M | B | store references to the archived slip, wording, endorsements and… | DOC-004, DOC-008 |
| RI-046 | J8/W7 | M | B | hold participations per section or layer with reinsurer party… | PTY-002, PTY-097 |
| RI-047 | J8/W7 | M | B | require that every reinsurer and broker referenced holds an… | PTY-093, PTY-097 |
| RI-048 | J8/W7 | S | B | store on each participation the security rating snapshot used… | PTY-097 |
| RI-049 | J8/W7 | M | B | screen reinsurers and brokers through pty.Screening.screen when a participation… | PTY-006 |
| RI-050 | J8/W7 | M | B | maintain an approved security list per legal entity with… | PLT-003, PLT-004 |
| RI-051 | J8/W7 | S | B | consume rating updates for reinsurer parties (via PartyUpdated) and… | WRK-001 |
| RI-055 | J8/W7 | S | B | split every contract-level amount to participants by signed line… | MKT-195 |
| RI-056 | J8/W7 | M | B | version contracts: every change after approval creates a new… | — |
| RI-057 | J8/W7 | M | B | route programme and contract approval through maker-checker (REQ-PLT-004) bound… | PLT-004, PLT-117 |
| RI-058 | J8/W7 | S | B | activate approved contracts automatically at period start (time service)… | PLT-332 |
| RI-059 | J8/W7 | M | B | move contracts to Expired at period end, keep them… | — |
| RI-065 | J8/W7 | S | B | prevent deletion of any contract, version or participation that… | PLT-002 |
| RI-066 | J8/W7 | M | B | define the reinsurance risk per RI risk class as… | POL-010, PFC-093 |
| RI-073 | J8/W7 | M | B | read RI-cedable and RI risk class attributes per coverage… | PFC-002, POL-120 |
| RI-075 | J8/W7 | M | B | consume ChargeDeltaEmitted idempotently on charge id and delta kind,… | POL-005, PFC-122 |
| RI-113 | J8/W7 | M | B | consume ExposureCreated, ReserveChanged, TransactionSetApproved, PaymentIssued, PaymentVoided, RecoveryRecorded, ClaimClosed, ClaimReopened… | CLM-005, CLM-101 |
| RI-114 | J8/W7 | M | B | read claim attributes needed for reinsurance — loss date-time,… | CLM-226 |
| RI-115 | J8/W7 | M | B | assign claims to occurrences per contract occurrence definition: per… | — |
| RI-116 | J8/W7 | M | B | compute ultimate net loss per occurrence and contract per… | — |
| RI-119 | J8/W7 | S | B | split every recovery into paid recoverable (on paid UNL)… | — |
| RI-120 | J8/W7 | S | B | convert claim amounts into contract currency per the contract's… | PLT-200 |
| RI-122 | J8/W7 | S | B | reduce outstanding recoverable to zero when a claim closes… | CLM-005 |
| RI-123 | J8/W7 | S | B | compute per-occurrence layer loss for each layer as min(max(UNL… | — |
| RI-124 | J8/W7 | S | B | compute per-event layer loss on the combined UNL of… | — |
| RI-125 | J8/W7 | M | B | maintain an annual aggregate tracker per layer and contract… | — |
| RI-126 | J8/W7 | M | B | derive each occurrence's recovery as R(Cₖ) − R(Cₖ₋₁) where… | — |
| RI-127 | J8/W7 | S | B | book recovery deltas per occurrence × layer × participant… | — |
| RI-128 | J8/W7 | S | B | trigger recalculation on every consumed reserve, payment, payment void,… | CLM-005 |
| RI-129 | J8/W7 | S | B | make recalculation idempotent and order-independent within a contract year:… | — |
| RI-130 | J8/W7 | M | B | batch recalculation per contract year and per catastrophe event:… | — |
| RI-131 | J8/W7 | S | B | hold each recovery delta in states Calculated → Posted… | FIN-002 |
| RI-132 | J8/W7 | S | B | store a calculation trace per occurrence and batch (inputs,… | PLT-002 |
| RI-136 | J8/W7 | M | B | provide ri.Recovery.listByClaim, ri.Recovery.listByContract and ri.Recovery.trace returning recoverable incurred, paid… | CLM-005 |
| RI-150 | J8/W7 | S | B | track exhaustion per layer (remaining limit and remaining reinstatements),… | WRK-001 |
| RI-152 | J8/W7 | S | B | provide ri.AggregateTracker.get per layer and year with the occurrence-by-occurrence… | — |
| RI-168 | J8/W7 | M | B | detect when an occurrence's UNL reaches the contract's notification… | DOC-001, CLM-090 |
| RI-169 | J8/W7 | M | B | limit notice content to claim number, loss date, loss… | — |
| RI-173 | J8/W7 | L | B | record every RI money fact as an immutable technical… | — |
| RI-174 | J8/W7 | S | B | assign each item an accounting period from its booking… | FIN-007 |
| RI-183 | J8/W7 | S | B | provide a reinsurer balances report for the MVP: per… | — |
| RI-191 | J8/W7 | S | B | screen the counterparty with pty.Screening.screen immediately before approval and… | PTY-006 |
| RI-192 | J8/W7 | L | B | execute settlements due by us through bil.Disbursement.request (source type… | BIL-009, BIL-197, BIL-348, BIL-349, BIL-350 |
| RI-195 | J8/W7 | S | B | publish SettlementRecorded for every settlement completion, partial settlement and… | — |
| RI-201 | J8/W7 | S | B | provide counterparty data per participant for Solvency II counterparty… | DAT-003 |
| RI-204 | J8/W7 | M | B | hold on every cession, recovery and account item the… | PLT-200, MKT-006 |
| RI-205 | J8/W7 | S | B | evaluate attachments, limits, aggregates, retentions and cash-call thresholds in… | — |
| RI-206 | J8/W7 | S | B | support rate-of-exchange clause variants: rate at date of payment,… | — |
| RI-208 | J8/W7 | S | B | provide open RI balances per currency to FIN for… | FIN-006 |
| RI-209 | J8/W7 | S | B | round each amount by currency and purpose through mkt.Rounding.apply. | MKT-195 |
| RI-210 | J8/W7 | S | B | use the PLT FX fallback rule (previous business day… | PLT-201 |
| RI-224 | J9/W8 | M | B | persist, for every cession and recovery delta, the link… | FIN-011, FIN-139, FIN-295 |
| RI-226 | J9/W8 | S | B | track cumulative recoverable losses separately per underlying group and… | DAT-001 |
| RI-227 | J9/W8 | M | B | provide ri.Ifrs17Link.query by reinsurance contract, underlying group, period and… | DAT-001, DAT-004 |
| RI-228 | J9/W8 | S | B | carry on every ceded amount the underlying direct Solvency… | PFC-005 |
| RI-229 | J9/W8 | S | B | provide recoverables per participant, contract, line and currency (paid… | DAT-003 |
| RI-231 | J9/W8 | S | B | identify each treaty uniquely across years (stable treaty identifier… | — |
| RI-233 | J9/W8 | S | B | carry the correlation id of the originating policy transaction… | PLT-002 |
| RI-234 | J9/W8 | S | B | provide FIN with a posting key per RI item… | FIN-004 |
| RI-235 | J9/W8 | S | B | consume JournalPosted and ReconciliationBreakRaised to set Posted status and… | FIN-002 |
| RI-236 | J9/W8 | S | B | run a daily control-total exchange with FIN per item… | FIN-001 |
| RI-237 | J9/W8 | S | B | provide an RI period close: cut-off of items, recalculation… | FIN-007 |
| RI-239 | J9/W8 | S | B | provide estimates of IBNR-related recoverables only as DAT inputs,… | DAT-004 |
| RI-240 | J9/W8 | S | B | reverse no RI item; corrections are new items linked… | PLT-002 |
| RI-245 | J8/W7 | M | B | register each external exchange endpoint in the PLT adapter… | PLT-159, PLT-012 |
| RI-247 | J8/W7 | S | B | import contracts, participations and clause parameters from legacy systems… | MIG-001 |
| RI-248 | J8/W7 | S | B | import opening cumulative cessions per risk interval and opening… | — |
| RI-249 | J8/W7 | S | B | reconcile migrated RI data (contracts, aggregates, recoveries, open balances)… | MIG-005 |
| RI-250 | J8/W7 | S | B | respond to PartiesMerged and PartyUnmerged by re-pointing participation, broker… | PTY-007 |
| RI-251 | J8/W7 | S | B | publish search documents for contracts, fac requests, statements and… | WRK-325 |
| RI-252 | J8/W7 | M | B | register RI retention data sets (RC-RI-CONTRACT, RC-RI-ACCOUNT, RC-RI-CALC, RC-RI-EXPORT)… | PLT-235, PLT-237 |
| RI-253 | J8/W7 | S | B | provide DSAR export and restriction for the limited personal… | CMP-005 |
| RI-254 | J8/W7 | M | B | pseudonymise policy and party identifiers in every external export… | — |
| RI-255 | J8/W7 | S | B | provide an operations view of failed event consumption, recalculation… | PLT-144 |
| RI-256 | J8/W7 | S | B | emit OpenTelemetry business metrics: cession latency, recovery recalculation latency… | PLT-013 |
| RI-257 | J8/W7 | S | B | respond to AiToggleChanged and AiKillSwitchActivated within 60 s by… | PLT-010 |
| RI-258 | J8/W7 | M | B | respond to ConfigChanged and PackActivated by refreshing cached configuration… | MKT-051, PLT-008 |
| RI-259 | J8/W7 | M | E | record every AI-RI interaction as an AiInteractionRecord through the… | PLT-010 |

### 1.4 FIN (242 rows in range)

J/W mix: J9/W8×120, J4/W5×95, J9/W5×21, J1/W5×5, J13/W9×1

| ID | J/W | Sz | Tag | Title (first words) | Deps |
|---|---|---|---|---|---|
| FIN-001 | J4/W5 | XL | B | consume business events from POL, BIL, CLM, RI, DAT… | BIL-011, BIL-313, CLM-005, RI-004, DAT-004 … |
| FIN-002 | J4/W5 | XL | B | make posted journals and journal lines immutable and correct… | PLT-002, PLT-125 |
| FIN-003 | J9/W8 | XL | B | run earning from POL policy segments by calling the… | POL-094, POL-118, PFC-115 |
| FIN-004 | J4/W5 | XL | B | maintain charts of accounts per legal entity and book… | PLT-004, PFC-120 |
| FIN-005 | J9/W5 | XL | B | account for insurance premium tax and levies (accrual, payable,… | MKT-114, BIL-270, BIL-275, BIL-276, CMP-008 |
| FIN-006 | J4/W5 | XL | B | store every journal line in transaction, functional and group… | PLT-200, PLT-208, MKT-204 |
| FIN-007 | J9/W8 | XL | B | orchestrate period close per legal entity (calendar, cut-off, task… | PLT-007, BIL-291 |
| FIN-008 | J9/W8 | XL | B | produce summarised GL extracts per legal entity, book and… | PLT-006 |
| FIN-009 | J9/W8 | XL | B | provide the actuarial results intake API (fin.ActuarialResults.submit, validate, approve,… | DAT-004, PLT-004 |
| FIN-010 | J13/W9 | XL | B | provide an opening-balance import API for migration (fin.Import.openingBalances, fin.Import.ifrs17Groups,… | MIG-001, MIG-005, POL-340, BIL-012, CLM-010 … |
| FIN-011 | J9/W8 | XL | B | assign, on consuming PolicyBound and RenewalBound (and on rewrite… | POL-034, PFC-005, PFC-143, CLM-227 |
| FIN-030 | J4/W5 | S | B | maintain a finance event catalogue listing every consumed event… | — |
| FIN-031 | J4/W5 | S | B | consume events through the PLT idempotent-consumer library, de-duplicating on… | PLT-143 |
| FIN-032 | J4/W5 | S | B | validate each event's envelope (legal entity, jurisdiction, aggregate, sequence,… | PLT-005 |
| FIN-033 | J4/W5 | M | B | process events of one aggregate in sequence order and… | PLT-140 |
| FIN-034 | J4/W5 | M | B | normalise each posting-relevant event into a BusinessEvent with source… | CLM-005 |
| FIN-035 | J4/W5 | S | B | derive the accounting date as the business date when… | — |
| FIN-036 | J4/W5 | M | B | post written premium, billed and collected facts, levies, IPT,… | BIL-313, POL-005 |
| FIN-037 | J4/W5 | M | B | post claim financial facts only from CLM's events (ReserveChanged,… | CLM-005, BIL-009 |
| FIN-038 | J4/W5 | M | B | post ceded premium, ceded commission, RI recoveries, reinstatement premiums… | RI-004 |
| FIN-039 | J4/W5 | M | B | post actuarial results only from approved result sets submitted… | — |
| FIN-040 | J4/W5 | S | B | hold an event in Waiting when a required reference… | — |
| FIN-041 | J4/W5 | S | B | measure intake completeness per source and day by comparing… | BIL-314 |
| FIN-042 | J4/W5 | M | B | request replay of a source's events for a date… | PLT-145, BIL-315 |
| FIN-043 | J4/W5 | S | B | treat events flagged origin=MIGRATION as opening-balance or history events… | — |
| FIN-044 | J4/W5 | S | B | store the configuration hash, rule-set version id, source event… | PLT-132 |
| FIN-045 | J4/W5 | S | B | keep the intake path stateless with respect to other… | PLT-009 |
| FIN-046 | J4/W5 | M | B | expose intake metrics per source (lag, throughput, waiting, suspended,… | PLT-013, PLT-146 |
| FIN-047 | J4/W5 | S | B | allow an authorised operator to re-run a suspended or… | — |
| FIN-048 | J4/W5 | M | B | model posting rules as rows keyed by event type,… | — |
| FIN-049 | J4/W5 | S | B | resolve exactly one rule per key using a specificity… | — |
| FIN-050 | J4/W5 | M | B | derive accounts from the PFC GL key of the… | PFC-004, PFC-120 |
| FIN-051 | J4/W5 | S | B | evaluate amount expressions over a typed, side-effect-free expression language… | MKT-195 |
| FIN-052 | J4/W5 | S | B | group rules into RuleSetVersions per legal entity and book… | PLT-008 |
| FIN-053 | J4/W5 | M | B | compile a rule-set version before submission, checking key coverage… | — |
| FIN-054 | J4/W5 | S | B | run a golden posting suite (fixed events with expected… | MKT-008 |
| FIN-055 | J4/W5 | S | B | require maker-checker approval to activate a rule-set version, the… | PLT-004 |
| FIN-057 | J4/W5 | S | B | accept posting-rule rows contributed by country packs (for example… | MKT-003 |
| FIN-058 | J4/W5 | S | B | support rules that post to some books only (for… | — |
| FIN-059 | J4/W5 | S | B | record, on every journal, the rule ids and rule-set… | — |
| FIN-061 | J4/W5 | S | B | support derived postings computed by FIN runs (earning, accruals,… | — |
| FIN-064 | J4/W5 | S | B | block activation of a rule-set version whose effective date… | — |
| FIN-065 | J4/W5 | M | E | notify finance owners when PFC publishes a product version… | PFC-009, WRK-001 |
| FIN-066 | J4/W5 | S | B | support separate bill-mode keys (DIRECT, AGENCY) so agency-bill receivables… | BIL-010 |
| FIN-067 | J4/W5 | M | B | store each journal as a JournalEntry header (journal number,… | — |
| FIN-068 | J4/W5 | S | B | enforce at commit, through a deferred database constraint, that… | — |
| FIN-069 | J4/W5 | M | B | assign journal numbers through the PLT numbering service per… | PLT-014, PLT-209 |
| FIN-070 | J4/W5 | S | B | make journals and lines append-only through database triggers rejecting… | — |
| FIN-072 | J4/W5 | S | B | reverse a journal by creating a journal with every… | — |
| FIN-073 | J4/W5 | S | B | prevent a journal from being reversed twice and prevent… | — |
| FIN-074 | J4/W5 | S | B | post linked journals for multi-leg facts (for example a… | — |
| FIN-075 | J4/W5 | M | B | carry the dimension set on each line: legal entity,… | — |
| FIN-076 | J4/W5 | S | B | reject a line whose account requires a dimension that… | — |
| FIN-077 | J4/W5 | S | B | maintain account balances per book, account, period and a… | PLT-013 |
| FIN-078 | J4/W5 | S | B | provide fin.Journal.query by any dimension, source reference, rule, period,… | — |
| FIN-079 | J4/W5 | M | B | link each journal to its source and allow drill-down… | BIL-293, POL-002 |
| FIN-080 | J4/W5 | M | B | hold in the intake exception queue every event that… | — |
| FIN-081 | J4/W5 | S | B | raise an alert and a WRK activity when an… | WRK-001 |
| FIN-082 | J4/W5 | S | B | show the total intake exception amount per book and… | — |
| FIN-083 | J4/W5 | S | B | allow a intake exception to be resolved by re-run… | PLT-004 |
| FIN-084 | J4/W5 | S | B | never post an intake exception to a GL suspense… | — |
| FIN-085 | J4/W5 | S | B | publish JournalPosted for each journal (or each batch of… | CLM-113 |
| FIN-087 | J4/W5 | S | B | partition journals by legal entity, book and month and… | PLT-011 |
| FIN-088 | J4/W5 | S | B | forbid any module other than FIN from writing to… | PLT-321 |
| FIN-089 | J4/W5 | S | E | hold a book profile per legal entity naming the… | MKT-001 |
| FIN-091 | J4/W5 | M | B | hold accounts per chart with code, Greek and English… | — |
| FIN-092 | J4/W5 | M | B | maintain the entity chart per book and mappings from… | — |
| FIN-093 | J4/W5 | S | B | define mandatory and allowed dimensions per account (for example… | — |
| FIN-094 | J4/W5 | M | B | maintain dimension value lists with sources: from MKT regime… | MKT-007, MKT-104 |
| FIN-095 | J4/W5 | S | B | validate the completeness of mappings: every account with a… | — |
| FIN-096 | J4/W5 | S | B | require maker-checker approval for any change to accounts, mappings… | PLT-004 |
| FIN-097 | J4/W5 | S | B | publish a dimension dictionary (name, meaning, source, allowed values,… | DAT-001 |
| FIN-098 | J4/W5 | S | B | provide the reference chart of accounts shipped with the… | MKT-003 |
| FIN-100 | J4/W5 | S | B | produce trial balances per entity, book, period and view… | — |
| FIN-101 | J4/W5 | S | B | retire an account only when its balance is zero… | — |
| FIN-104 | J9/W8 | S | B | run earning per legal entity daily (default) or at… | PLT-172 |
| FIN-105 | J9/W8 | S | B | compute earned-to-date per term, element and charge type as… | POL-118 |
| FIN-106 | J9/W8 | S | B | earn only charge types whose earning pattern (REQ-PFC-115) is… | PFC-115 |
| FIN-107 | J9/W8 | S | B | post, per run, the difference between earned-to-date and the… | — |
| FIN-108 | J9/W8 | M | B | select terms for each run as (a) terms with… | POL-094 |
| FIN-109 | J9/W8 | S | B | recompute earning for terms affected by out-of-sequence changes, cancellations,… | POL-006 |
| FIN-110 | J9/W8 | S | B | store, per term and run, earned-to-date, unearned and written… | — |
| FIN-111 | J9/W8 | S | B | post in LOCAL_GAAP (when active) written premium to a… | — |
| FIN-112 | J9/W8 | M | B | support, per portfolio and book, the election to defer… | — |
| FIN-113 | J9/W8 | S | B | amortise deferred acquisition costs in proportion to the earning… | — |
| FIN-115 | J9/W8 | M | B | verify on every run, per term, that earned +… | — |
| FIN-116 | J9/W8 | S | B | partition earning runs by legal entity, product line and… | PLT-173 |
| FIN-118 | J9/W8 | S | B | earn flat fees according to their earning pattern (fully… | PFC-114 |
| FIN-119 | J9/W8 | S | B | handle Scheduled (bound, not yet effective) terms by holding… | POL-004 |
| FIN-120 | J9/W8 | S | B | provide a UPR and earned-premium report per entity, period,… | — |
| FIN-121 | J9/W8 | M | B | hold the entity's IFRS 17 portfolio scheme (from the… | PFC-143, POL-034, MKT-007 |
| FIN-122 | J9/W8 | S | B | determine the annual cohort from the initial-recognition date and… | — |
| FIN-123 | J9/W8 | M | B | determine the initial-recognition date of a term as the… | BIL-003 |
| FIN-124 | J9/W8 | M | B | assign the profitability group (ONEROUS, NO_SIGNIFICANT_POSSIBILITY, REMAINING) through a… | PLT-174 |
| FIN-125 | J9/W8 | S | B | assign the measurement model (PAA or GMM) per group… | — |
| FIN-126 | J9/W8 | M | B | never reassess an assignment after initial recognition; corrections of… | PLT-004 |
| FIN-127 | J9/W8 | S | B | treat each renewal term and each rewritten term as… | POL-009 |
| FIN-128 | J9/W8 | S | B | maintain Ifrs17Group records (portfolio, cohort, profitability group, measurement model,… | — |
| FIN-129 | J9/W8 | M | B | evaluate PAA eligibility per group at inception: eligible when… | — |
| FIN-131 | J9/W8 | M | B | maintain the PAA LRC per group as: premiums received… | — |
| FIN-132 | J9/W8 | M | B | support the entity policy choice for the LRC premium… | — |
| FIN-134 | J9/W8 | M | B | compute an onerous indicator per group each period from… | — |
| FIN-135 | J9/W8 | S | B | post a loss component when the actuary confirms an… | — |
| FIN-137 | J9/W8 | S | B | accept LIC inputs: case reserves and payments from CLM… | CLM-005 |
| FIN-138 | J9/W8 | M | B | support the entity policy to not discount the LIC… | — |
| FIN-139 | J9/W8 | M | B | create and assign reinsurance-held groups for each RI contract… | RI-001, RI-058 |
| FIN-141 | J9/W8 | S | B | measure reinsurance-held groups under PAA where eligible, recognising the… | — |
| FIN-142 | J9/W8 | S | B | recognise a loss-recovery component on reinsurance-held groups when an… | — |
| FIN-144 | J9/W8 | S | B | not offset reinsurance-held balances against issued insurance balances in… | — |
| FIN-145 | J9/W8 | M | B | treat each insurance-contract group and reinsurance-held group as a… | — |
| FIN-146 | J9/W8 | M | B | produce IFRS 17 movement tables per group, portfolio and… | DAT-003 |
| FIN-147 | J9/W8 | M | B | supply DAT with group-level data (assignments, movements, balances, links… | DAT-001, DAT-004 |
| FIN-148 | J9/W8 | S | B | provide the IFRS 17 group and cohort explorer (SCR-FIN-07)… | — |
| FIN-149 | J9/W8 | M | B | maintain the SOLVENCY_II book using the same business events,… | — |
| FIN-150 | J9/W8 | S | B | carry the SII line of business on every premium,… | PFC-140 |
| FIN-151 | J9/W8 | S | B | supply premium-provision data (unearned premium, future premiums within contract… | DAT-003 |
| FIN-152 | J9/W8 | S | B | supply claims-provision inputs (case reserves, paid triangles by accident… | DAT-003 |
| FIN-153 | J9/W8 | S | B | reconcile the statutory book to the SOLVENCY_II book per… | — |
| FIN-154 | J9/W8 | M | B | carry the SII taxonomy version on SII mappings and… | MKT-007 |
| FIN-155 | J9/W8 | M | B | publish reconciled balances per SII LoB, national statistical class… | DAT-003, DAT-007 |
| FIN-157 | J9/W8 | S | B | provide the IFRS 17 and Solvency II reporting view… | — |
| FIN-158 | J4/W5 | S | B | post claim reserve changes (ReserveChanged) to incurred-claims expense and… | CLM-005 |
| FIN-159 | J4/W5 | S | B | post claim payments (PaymentIssued) by moving the paid amount… | BIL-009 |
| FIN-160 | J4/W5 | M | B | post recoveries received (RecoveryRecorded) by type (subrogation, salvage, deductible,… | CLM-147 |
| FIN-161 | J4/W5 | S | B | post claim expense payments (allocated adjustment expenses) to the… | CLM-005 |
| FIN-162 | J4/W5 | M | B | reconcile daily claim totals with CLM (REQ-CLM-106) by entity,… | CLM-106, CLM-113 |
| FIN-163 | J4/W5 | S | B | post ceded premium, ceding commission and profit/sliding-scale commission from… | RI-004 |
| FIN-164 | J4/W5 | S | B | post RI recoveries (RecoveryCalculated) as amounts recoverable from reinsurers… | RI-003 |
| FIN-167 | J4/W5 | S | B | reconcile daily cession and recovery totals with RI per… | RI-004 |
| FIN-169 | J4/W5 | S | B | carry accident year, underwriting year, cat code and claim… | CLM-227 |
| FIN-171 | J1/W5 | M | B | post commission expense and commission payable from BIL commission… | BIL-249, BIL-313 |
| FIN-172 | J1/W5 | S | B | recognise commission expense on the incurred basis (when the… | BIL-252 |
| FIN-173 | J1/W5 | S | B | post return commission and chargebacks by reversal or negative… | BIL-254 |
| FIN-175 | J1/W5 | S | B | reconcile commission payable movements and payments daily with BIL… | BIL-322 |
| FIN-176 | J1/W5 | M | B | reconcile commission self-billing fiscal documents registered through CMP (Greece… | CMP-001, PTY-240 |
| FIN-178 | J9/W5 | M | B | post IPT payable per IPT class from BIL written… | BIL-275, PFC-142 |
| FIN-179 | J9/W5 | M | B | compute, for each IPT class and return period, the… | MKT-087, MKT-193 |
| FIN-180 | J9/W5 | M | B | hold return periods and due dates per tax from… | CMP-003, MKT-009 |
| FIN-181 | J9/W5 | M | B | include contract rights and fees in the IPT base… | PFC-117 |
| FIN-182 | J9/W5 | M | B | never reduce IPT payable from a cancellation-sourced credit; a… | BIL-079, BIL-319 |
| FIN-183 | J9/W5 | S | B | handle IPT on non-cancellation return premiums (for example endorsement… | PFC-116 |
| FIN-184 | J9/W5 | M | B | create a TaxReturn per tax, entity and period at… | — |
| FIN-185 | J9/W5 | M | B | reconcile each return to the IPT payable account balance… | BIL-276 |
| FIN-186 | J9/W5 | S | B | render each return through the TaxReturnFormat SPI bound for… | MKT-114 |
| FIN-187 | J9/W5 | L | B | manage return states Draft, Prepared, Reviewed, Approved, Filed, Accepted,… | PLT-004, CMP-008, CMP-195, CMP-196, CMP-197 … |
| FIN-189 | J9/W5 | M | B | post levy payables per levy type and share from… | BIL-269, PFC-124, PFC-125 |
| FIN-190 | J9/W5 | M | B | hold levy components (Greece pack: 4.5% and 1.5% components)… | MKT-087, MKT-131 |
| FIN-191 | J9/W5 | S | B | assign levy lines to the pack's levy period (Greece… | BIL-270 |
| FIN-192 | J9/W5 | L | B | create a LevyReturn per levy type, entity and period… | BIL-272, CMP-003 |
| FIN-193 | J9/W5 | S | B | reconcile the levy accrual per period to the written… | BIL-274 |
| FIN-194 | J9/W5 | S | B | post the levy remittance from BIL's disbursement entry and… | BIL-273 |
| FIN-197 | J9/W5 | M | B | reconcile fiscal registrations (FiscalDocRegistered with MARK, document type, income… | CMP-001, BIL-096 |
| FIN-198 | J9/W5 | S | B | hold the expected fiscal classification per charge type from… | PFC-121 |
| FIN-200 | J9/W5 | S | B | track rejected fiscal documents (FiscalDocRejected) as open reconciliation items… | CMP-001 |
| FIN-201 | J9/W5 | S | B | provide the tax and levy return preparation screen (SCR-FIN-09). | — |
| FIN-202 | J9/W8 | M | B | convert each line from transaction to functional currency at… | PLT-200, MKT-196 |
| FIN-203 | J9/W8 | S | B | compute the group (USD) amount on each line at… | PLT-208 |
| FIN-204 | J9/W8 | S | B | accept amounts already converted by the source (for example… | RI-004 |
| FIN-205 | J9/W8 | M | B | run FX revaluation per entity and book at period… | PLT-200 |
| FIN-206 | J9/W8 | S | B | support revaluation policy per entity: reverse on the first… | — |
| FIN-207 | J9/W8 | S | B | post realised FX differences on settlement of foreign-currency balances… | PLT-200 |
| FIN-208 | J9/W8 | S | B | exclude non-monetary items (for example deferred acquisition cost assets… | — |
| FIN-209 | J9/W8 | M | B | translate the group book into USD at period end… | PLT-208, MKT-204 |
| FIN-210 | J9/W8 | M | B | produce the group reporting pack (group-chart trial balance, IFRS… | MKT-128 |
| FIN-211 | J9/W8 | S | B | lock the rates used for a period's revaluation and… | PLT-202 |
| FIN-212 | J9/W8 | S | B | provide the FX revaluation run screen (SCR-FIN-10) with run… | — |
| FIN-213 | J9/W8 | S | B | run revaluation and translation scenarios (EUR functional with USD,… | MKT-008 |
| FIN-215 | J4/W5 | M | B | allow manual journals only from configured templates (ACCRUAL, PREPAYMENT,… | — |
| FIN-216 | J4/W5 | S | B | require a reason, a description in Greek or English… | DOC-004 |
| FIN-217 | J4/W5 | M | B | route each manual journal for approval through maker-checker with… | PLT-003, PLT-004 |
| FIN-218 | J4/W5 | S | B | check segregation-of-duties rules at approval (for example a user… | PLT-082 |
| FIN-219 | J4/W5 | S | B | support auto-reversing accruals that reverse on the first day… | PLT-172 |
| FIN-220 | J4/W5 | S | B | support multi-book manual journals posting the same adjustment to… | — |
| FIN-224 | J4/W5 | S | B | forbid manual journals on accounts flagged system-only (for example… | — |
| FIN-225 | J4/W5 | S | B | provide the manual journal entry and approval screen (SCR-FIN-06). | — |
| FIN-226 | J4/W5 | S | B | list manual journals by period, template, maker, approver and… | PLT-130 |
| FIN-227 | J9/W8 | S | B | hold a close calendar per legal entity (monthly, quarterly… | PLT-009 |
| FIN-228 | J9/W8 | S | B | generate close tasks per period from a versioned close… | PLT-007 |
| FIN-229 | J9/W8 | M | B | execute SYSTEM tasks (cut-off, accrual run, earning run, revaluation,… | PLT-007, PLT-172 |
| FIN-230 | J9/W8 | S | B | apply per-source cut-off timestamps (default: period end 23:59:59 local… | BIL-291 |
| FIN-231 | J9/W8 | S | B | move a period to SoftClosed at cut-off, allowing only… | — |
| FIN-232 | J9/W8 | M | B | coordinate cut-off with BIL, CLM and RI by consuming… | BIL-291 |
| FIN-233 | J9/W8 | S | B | require MANUAL sign-off tasks to be completed by a… | PLT-001 |
| FIN-234 | J9/W8 | S | E | show close progress as a single completion measure and… | WRK-001 |
| FIN-235 | J9/W8 | S | B | allow Lock only when all mandatory tasks are complete,… | — |
| FIN-236 | J9/W8 | S | B | allow a break to be accepted for Lock only… | PLT-004 |
| FIN-237 | J9/W8 | S | B | publish PeriodClosed on Lock with entity, period, books and… | BIL-291 |
| FIN-238 | J9/W8 | S | B | allow reopening a Locked period only by the controller… | PLT-004 |
| FIN-239 | J9/W8 | S | B | lock quarters and years only after their months are… | — |
| FIN-240 | J9/W8 | S | B | support a year-end close producing the closing of income… | — |
| FIN-241 | J9/W8 | S | B | generate a close evidence pack per period (task sign-offs,… | PLT-130 |
| FIN-243 | J9/W8 | S | B | provide the period-close cockpit (SCR-FIN-04). | — |
| FIN-244 | J9/W8 | S | B | run a daily BIL ↔ FIN reconciliation exchanging control… | BIL-314 |
| FIN-245 | J9/W8 | S | B | reconcile POL written premium (pol.Charges.reconcile) per term and charge… | POL-122 |
| FIN-246 | J9/W8 | S | B | reconcile CLM financials to FIN daily per REQ-FIN-162 and… | CLM-106 |
| FIN-247 | J9/W8 | S | B | reconcile FIN to the corporate ERP by comparing extract… | — |
| FIN-248 | J9/W8 | S | B | reconcile fiscal registrations to invoiced charges and journals per… | — |
| FIN-249 | J9/W8 | S | B | check every clearing account (claim payments, commission, disbursements in… | — |
| FIN-250 | J9/W8 | S | B | create ReconciliationBreaks with leg, type (MISSING, AMOUNT, CLASSIFICATION, TIMING,… | — |
| FIN-251 | J9/W8 | M | B | publish ReconciliationBreakRaised for each new break and ReconciliationBreakResolved on… | WRK-198 |
| FIN-252 | J9/W8 | S | B | open a PLT incident for breaks above the configured… | PLT-012 |
| FIN-253 | J9/W8 | S | B | auto-match timing differences within a configured window and tolerance… | — |
| FIN-254 | J9/W8 | M | B | run the FIN invariant suite nightly and on demand… | PLT-012 |
| FIN-255 | J9/W8 | S | B | produce a six-way month-end reconciliation report (policy, billing, sub-ledger,… | — |
| FIN-256 | J9/W8 | S | B | reconcile earning results across books per REQ-FIN-115 and report… | — |
| FIN-257 | J9/W8 | S | B | support manual matching of break items with reason and… | PLT-004 |
| FIN-258 | J9/W8 | S | B | provide the reconciliation breaks dashboard (SCR-FIN-05). | — |
| FIN-259 | J9/W8 | S | B | answer BIL's and CLM's break investigation requests through fin.Reconciliation.detail… | BIL-314 |
| FIN-260 | J9/W8 | S | B | store reconciliation results per run for audit (inputs, totals,… | PLT-011 |
| FIN-261 | J9/W8 | S | B | configure the GL extract per entity and book: summarisation… | — |
| FIN-262 | J9/W8 | S | B | generate each extract from journals with accounting dates in… | — |
| FIN-263 | J9/W8 | S | B | include a header and control record (entity, book, period,… | — |
| FIN-264 | J9/W8 | S | B | render the extract in the configured format (default: CSV… | PLT-006 |
| FIN-265 | J9/W8 | S | B | publish GLExtractSent with extract id, entity, book, period and… | — |
| FIN-266 | J9/W8 | S | B | record ERP acknowledgement (accepted, rejected with reasons, partially accepted)… | — |
| FIN-267 | J9/W8 | S | B | replay an extract by id (same content and id)… | — |
| FIN-268 | J9/W8 | S | B | block period Lock until all extracts for the period… | — |
| FIN-270 | J9/W8 | S | B | provide the GL extract monitor (SCR-FIN-11). | — |
| FIN-271 | J9/W8 | S | B | register the ERP interface as an ICT third-party dependency… | PLT-012 |
| FIN-273 | J4/W5 | M | B | accept actuarial result sets with run id, model id… | DAT-004 |
| FIN-274 | J4/W5 | M | B | validate result sets for completeness (every active group or… | — |
| FIN-275 | J4/W5 | S | B | require approval of a result set by a ROLE-35… | PLT-004 |
| FIN-276 | J4/W5 | S | B | post movements of approved result sets through posting rules… | — |
| FIN-277 | J4/W5 | S | B | supersede a posted result set with a corrected set… | — |
| FIN-278 | J4/W5 | S | B | publish acknowledgements of result sets (accepted, rejected, posted) to… | DAT-004 |
| FIN-279 | J4/W5 | S | B | supply DAT with the actuarial input extract (paid, case… | DAT-004 |
| FIN-280 | J4/W5 | S | B | show result-set status and validation findings on the IFRS… | — |
| FIN-281 | J9/W8 | S | B | import opening balances per account, book and dimension set… | MIG-005 |
| FIN-282 | J9/W8 | S | B | import IFRS 17 group assignments for migrated in-force terms… | POL-340 |
| FIN-283 | J9/W8 | S | B | import an earning baseline (earned-to-date per migrated term at… | POL-340 |
| FIN-284 | J9/W8 | S | B | import open LIC and RI balances at group and… | CLM-010 |
| FIN-286 | J9/W8 | S | E | provide a finance operations home (SCR-FIN-14) with intake health,… | — |
| FIN-287 | J9/W8 | S | B | provide the journal browser with drill-down (SCR-FIN-02). | — |
| FIN-288 | J9/W8 | S | B | provide the posting-rule browser and change request screen (SCR-FIN-01),… | — |
| FIN-290 | J9/W8 | M | B | hold no P2 or P3 values in journals, keep… | CMP-005, PLT-011 |
| FIN-291 | J9/W8 | S | B | mask intermediary and payee names in FIN screens unless… | PTY-001 |
| FIN-292 | J9/W8 | M | B | apply retention classes RC-FIN-BOOKS (journals, rules, returns, close evidence),… | PLT-011 |
| FIN-293 | J9/W8 | S | B | run all scheduled finance jobs as workflows with business-calendar… | PLT-172 |
| FIN-294 | J9/W8 | S | B | obtain the current time only from the PLT time… | PLT-332 |
| FIN-295 | J9/W8 | M | B | expose fin.Ifrs17Group.riHeldAssignment(riContractId, sectionId?, validAt) returning the reinsurance-held group (code,… | RI-001, RI-058, RI-224 |
| FIN-297 | J4/W5 | M | B | handle POL charge deltas in both delta modes (REQ-POL-121):… | POL-121, BIL-075 |
| FIN-298 | J9/W8 | M | B | switch every FIN AI surface (AI-FIN-0107) off within the… | PLT-010, CMP-007, DAT-005 |

### 1.5 DOC (250 rows in range)

J/W mix: J11/W2×187, J3/W4×38, J11/W6×24, J11/W4×1

| ID | J/W | Sz | Tag | Title (first words) | Deps |
|---|---|---|---|---|---|
| DOC-001 | J11/W2 | XL | B | provide the document request API doc.Document.request that, given a… | PLT-005, PLT-014, PTY-005 |
| DOC-002 | J11/W2 | XL | B | provide the template and clause library: templates, blocks and… | MKT-101, PLT-004 |
| DOC-003 | J11/W2 | XL | B | own form patterns (form number, edition, name, applicability by… | PFC-132, POL-001, PLT-007 |
| DOC-004 | J11/W2 | XL | B | provide the immutable archive API doc.Archive.* (store, get, verify,… | PLT-011, PLT-238, PLT-239, WRK-265 |
| DOC-005 | J11/W2 | XL | B | orchestrate delivery per recipient: resolve medium and channel from… | PTY-005, PTY-147, PLT-006 |
| DOC-006 | J3/W4 | XL | B | provide electronic signature through signature envelopes (documents, signatories identified… | MKT-112 |
| DOC-007 | J3/W4 | XL | B | issue pre-contractual documents (IPID per product version and offering,… | POL-172, POL-176, PFC-159 |
| DOC-008 | J11/W4 | XL | B | provide the document list query doc.Document.listForObject and the Documents… | WRK-285, PLT-001 |
| DOC-030 | J11/W2 | M | E | maintain a document-type catalogue in which each type defines… | MKT-001 |
| DOC-031 | J11/W2 | S | E | version the document-type catalogue entries with effective dates and… | PLT-004 |
| DOC-032 | J11/W2 | S | E | allow a country pack or legal entity layer to… | MKT-001 |
| DOC-033 | J11/W2 | S | B | render the policy schedule and the policy wording forms… | — |
| DOC-034 | J11/W2 | S | E | subscribe to the events named as triggers in the… | PLT-143 |
| DOC-035 | J11/W2 | S | E | forbid a document type from having both an API… | — |
| DOC-036 | J11/W2 | M | B | render the Green Card per insured vehicle with the… | POL-185, PTY-012 |
| DOC-038 | J11/W2 | M | B | render the statutory non-payment notice requested by BIL with… | BIL-006, MKT-115 |
| DOC-039 | J11/W2 | S | B | send copies of notices to trusted contacts whose purposes… | PTY-011 |
| DOC-040 | J11/W2 | M | B | send copies to intermediaries of record for document types… | PTY-009, CHN-005 |
| DOC-041 | J11/W2 | M | B | render the claims-history certificate with the fields and template… | CLM-008, MKT-108 |
| DOC-042 | J11/W2 | S | B | render claim acknowledgements, reasoned offers, denials and settlement letters… | CLM-007 |
| DOC-044 | J11/W2 | M | B | render the withdrawal acknowledgement (DT-WITHDRAWAL-ACK) on a durable medium… | PLT-332, CHN-167 |
| DOC-045 | J11/W2 | S | B | render complaint acknowledgements and reasoned replies requested by CMP… | CMP-004 |
| DOC-046 | J11/W2 | M | B | render intermediary commission statements and account-current statements from BIL-supplied… | BIL-008, BIL-010 |
| DOC-048 | J11/W2 | S | E | list, per business transaction, the documents that would be… | POL-001 |
| DOC-050 | J11/W2 | S | E | store templates as structured, language-independent documents (a tree of… | — |
| DOC-051 | J11/W2 | M | B | provide reusable blocks (letterhead, insurer identity and regulator statement,… | — |
| DOC-052 | J11/W2 | M | B | provide clauses as versioned wording objects with code, title… | PFC-003, PFC-083 |
| DOC-053 | J11/W2 | S | B | express variable text in clauses and templates as whole-sentence… | MKT-171 |
| DOC-054 | J11/W2 | S | B | support conditional sections whose condition is a typed expression… | — |
| DOC-055 | J11/W2 | M | B | support repeating regions and tables over payload collections (vehicles,… | — |
| DOC-056 | J11/W2 | S | E | support clause slots in templates that resolve to the… | MKT-115 |
| DOC-057 | J11/W2 | S | E | validate, before submission, that every clause code required by… | MKT-115 |
| DOC-058 | J11/W2 | M | B | provide a browser-based structured editor for templates, blocks and… | — |
| DOC-059 | J11/W2 | S | E | restrict editing of legal text to clause objects and… | — |
| DOC-060 | J11/W2 | S | E | hold non-legal labels (column headings, captions) as translation entries… | MKT-171 |
| DOC-061 | J11/W2 | M | B | refuse to render a customer-facing document in a language… | MKT-175, WRK-001 |
| DOC-062 | J11/W2 | S | B | support page masters (A4 portrait and landscape, letter for… | — |
| DOC-063 | J11/W2 | S | B | support text-only templates for SMS, push and email subject… | — |
| DOC-064 | J11/W2 | S | B | support images and barcodes (QR, Code 128, DataMatrix) generated… | CMP-001 |
| DOC-065 | J11/W2 | S | B | support document assembly from several templates (pack) with a… | — |
| DOC-066 | J11/W2 | S | E | allow templates to declare sample payloads and test cases… | — |
| DOC-068 | J11/W2 | M | E | store the AI-assistance flag and AI interaction id on… | PLT-223 |
| DOC-069 | J11/W2 | S | B | provide search over templates, blocks and clauses by code,… | MKT-178 |
| DOC-070 | J11/W2 | S | E | show where-used relationships for each clause and block version… | — |
| DOC-072 | J11/W2 | S | E | support formatting semantics (heading levels, emphasis as semantic emphasis,… | — |
| DOC-075 | J11/W2 | S | B | manage templates, blocks and clauses through the states Draft,… | — |
| DOC-076 | J11/W2 | S | B | track changes at character level between a new version… | — |
| DOC-077 | J11/W2 | S | B | require approval of each language version separately by a… | MKT-101 |
| DOC-078 | J11/W2 | S | B | mark informative language versions Stale when the binding text… | — |
| DOC-079 | J11/W2 | M | B | route approvals by object category: statutory and pre-contractual clauses… | PLT-004 |
| DOC-080 | J11/W2 | S | B | record a translation approval per language version with approver,… | PLT-002 |
| DOC-083 | J11/W2 | S | B | publish an approved object version with an effective-from date… | — |
| DOC-084 | J11/W2 | S | B | forbid editing a Published version; changes create a new… | — |
| DOC-085 | J11/W2 | S | B | retire a version only when no Active binding, form… | — |
| DOC-087 | J11/W2 | S | E | show the reviewer a rendered preview of every document… | — |
| DOC-088 | J11/W2 | S | B | allow reviewers to attach pinned comments to a text… | — |
| DOC-090 | J11/W2 | M | E | export the complete approval history of any template, block… | — |
| DOC-092 | J11/W2 | S | B | audit every create, edit, comment, approval, publication and retirement… | PLT-002 |
| DOC-095 | J11/W2 | M | B | maintain form patterns with code, number, edition, name (GR/EN),… | — |
| DOC-096 | J11/W2 | S | B | enforce uniqueness of form number + edition per legal… | — |
| DOC-097 | J11/W2 | S | B | hold the products to which a form pattern applies… | PFC-001 |
| DOC-098 | J11/W2 | S | B | hold the transaction types to which a form pattern… | POL-001 |
| DOC-099 | J11/W2 | S | B | hold availability rows ordered by evaluation order, each with… | MKT-216 |
| DOC-100 | J11/W2 | S | B | support jurisdictional replacement: a group code and "jurisdiction-specific version… | — |
| DOC-101 | J11/W2 | M | B | support inference conditions as decision-table rules evaluated by the… | PLT-007 |
| DOC-102 | J11/W2 | M | B | evaluate doc.Forms.infer(jobId, versionNo) for a quote or transaction, returning… | POL-001, POL-002 |
| DOC-104 | J11/W2 | S | E | manage form pattern editions through Draft, InReview, Approved, Published… | PLT-004 |
| DOC-105 | J11/W2 | S | B | assign endorsement numbers sequentially per policy term to forms… | PLT-014 |
| DOC-106 | J11/W2 | S | B | order forms in the form set by priority then… | — |
| DOC-108 | J11/W2 | S | B | provide the coverage, condition or exclusion picker as an… | PFC-003 |
| DOC-109 | J11/W2 | S | B | search form patterns by number, name, product, group code,… | — |
| DOC-110 | J11/W2 | S | E | compute, for a draft edition, the impact on product… | POL-002 |
| DOC-111 | J11/W2 | S | B | duplicate a form pattern as a new edition or… | — |
| DOC-113 | J11/W2 | M | B | answer doc.FormPattern.validateReferences(productVersion, refs) for PFC lint, returning for each… | PFC-132, MKT-101 |
| DOC-114 | J11/W2 | S | B | keep form sets as of record time so that… | POL-002 |
| DOC-115 | J11/W2 | M | B | maintain binding rows: product version (or range), form pattern,… | PFC-001 |
| DOC-116 | J11/W2 | S | B | resolve exactly one template version for a product version… | — |
| DOC-117 | J11/W2 | S | B | validate on activation that every form in every Locked… | MKT-101 |
| DOC-118 | J11/W2 | S | B | pin, per document request, the resolved binding rows and… | — |
| DOC-119 | J11/W2 | S | E | require maker-checker approval for binding changes, with the product… | PLT-004 |
| DOC-120 | J11/W2 | S | E | display the binding matrix as a grid with product… | — |
| DOC-121 | J11/W2 | S | B | resolve bindings for non-policy document types (letters, notices, statements)… | — |
| DOC-123 | J11/W2 | M | B | consume ProductVersionPublished and ProductVersionRetired and re-validate bindings for the… | PFC-009, WRK-001 |
| DOC-125 | J11/W2 | M | B | maintain versioned data dictionaries per subject (policy, quote, cancellation,… | POL-002 |
| DOC-126 | J11/W2 | M | B | build the payload for a request by calling the… | POL-002, PTY-001 |
| DOC-127 | J11/W2 | S | B | freeze the payload at generation: store it as canonical… | — |
| DOC-128 | J11/W2 | S | B | include in a payload only the fields declared in… | PTY-001 |
| DOC-129 | J11/W2 | S | B | validate the payload against the dictionary schema before rendering… | — |
| DOC-130 | J11/W2 | S | B | enrich payloads with formatted values only at rendering time,… | MKT-169 |
| DOC-131 | J11/W2 | S | B | accept a payload supplement from the requesting module (for… | — |
| DOC-132 | J11/W2 | M | B | include in application and policy payloads the intermediary data… | PTY-008, PTY-197 |
| DOC-133 | J11/W2 | S | B | include premium breakdown data from rat.Breakdown.get for quote, schedule… | RAT-008 |
| DOC-134 | J11/W2 | S | B | version dictionaries with backward-compatible additions within a major version… | — |
| DOC-136 | J11/W2 | S | B | record in the payload header the subject reference, reference… | PLT-147 |
| DOC-138 | J11/W2 | S | B | include computed display fields (for example "days until expiry")… | — |
| DOC-139 | J11/W2 | M | E | never call AI services during payload building or rendering… | PLT-217, PLT-224 |
| DOC-140 | J11/W2 | M | B | determine binding and informative languages per document from DocumentLanguageRule.rules(documentType,… | MKT-101, PTY-159 |
| DOC-141 | J11/W2 | S | B | mark every non-binding language version with the pack's statement… | MKT-115 |
| DOC-142 | J11/W2 | S | B | apply four independent document settings — language, region format,… | MKT-166 |
| DOC-143 | J11/W2 | S | B | format dates, numbers, percentages and currency with CLDR data… | MKT-169 |
| DOC-145 | J11/W2 | S | B | perform upper-case transforms of Greek text with the CLDR… | — |
| DOC-146 | J11/W2 | S | B | lower-case Greek text with context-sensitive final sigma and preserve… | — |
| DOC-147 | J11/W2 | M | B | print names in native script and, where a type… | PTY-012, MKT-091 |
| DOC-148 | J11/W2 | S | B | format postal addresses with AddressFormatter output purpose "postal label"… | MKT-092 |
| DOC-149 | J11/W2 | S | B | embed only fonts declared in the versioned font set… | — |
| DOC-150 | J11/W2 | S | B | subset and embed fonts in PDFs and include ToUnicode… | — |
| DOC-155 | J11/W2 | M | B | provide a rendering engine built in-house that lays out… | PLT-321 |
| DOC-156 | J11/W2 | S | B | produce, from one render, the archive rendition (PDF/A), a… | — |
| DOC-157 | J11/W2 | S | E | produce archive renditions conforming to PDF/A-3 (level A, accessible)… | — |
| DOC-158 | J11/W2 | S | B | render text-channel outputs (SMS, push, email subject) as plain… | — |
| DOC-159 | J11/W2 | S | B | render a single document synchronously within NFR-DOC-001 targets and… | — |
| DOC-160 | J11/W2 | M | E | render deterministically: identical payload, template version set, engine version,… | — |
| DOC-161 | J11/W2 | S | E | version the engine and font sets and keep every… | PLT-264 |
| DOC-162 | J11/W2 | S | B | classify render failures (payload invalid, binding missing, language not… | PLT-005 |
| DOC-163 | J11/W2 | M | B | retry retryable failures with exponential backoff up to the… | WRK-001, PLT-154 |
| DOC-164 | J11/W2 | S | B | detect layout overflow (content not fitting fixed areas such… | — |
| DOC-165 | J11/W2 | S | E | run rendering workers stateless and horizontally scalable, isolated from… | PLT-165 |
| DOC-166 | J11/W2 | S | B | stamp each document with a document number from plt.Number.next… | PLT-209 |
| DOC-167 | J11/W2 | S | B | apply the insurer's electronic seal (advanced or qualified per… | MKT-112 |
| DOC-168 | J11/W2 | S | B | render watermarks or overlays (DRAFT for previews, SPECIMEN for… | — |
| DOC-169 | J11/W2 | S | B | prevent rendering of documents marked SPECIMEN-only (test data) in… | PLT-263 |
| DOC-171 | J11/W2 | S | B | support combining rendered documents into print-ready mail pieces (letter… | — |
| DOC-172 | J11/W2 | S | B | publish DocumentRendered with document id, number, type, subject reference,… | PLT-141 |
| DOC-173 | J11/W2 | S | B | attach to every document request the correlation id of… | PLT-250 |
| DOC-174 | J11/W2 | S | B | continue to accept document requests when a downstream channel… | PLT-164 |
| DOC-175 | J11/W6 | S | B | run batch runs as PLT workflows that group document… | PLT-173 |
| DOC-176 | J11/W6 | S | B | accept batch requests through doc.Document.requestBatch with per-item idempotency keys… | — |
| DOC-177 | J11/W6 | S | E | pin the binding snapshot and engine version at batch… | — |
| DOC-178 | J11/W6 | S | B | schedule batch runs within configured windows per legal entity… | PLT-172 |
| DOC-179 | J11/W6 | S | B | report batch progress (items requested, rendered, failed, delivered, print… | — |
| DOC-180 | J11/W6 | S | B | let the output manager pause, resume, cancel (with reason)… | PLT-171 |
| DOC-181 | J11/W6 | S | B | maintain a failure queue of documents that could not… | WRK-001 |
| DOC-182 | J11/W6 | S | E | prioritise failure-queue items by statutory flag and remaining time… | CMP-003 |
| DOC-184 | J11/W6 | S | B | produce consolidated print files per print vendor slot from… | — |
| DOC-185 | J11/W6 | S | E | cancel pending batch items when their subject changes such… | POL-250 |
| DOC-187 | J11/W2 | M | B | store archive binaries in EU-hosted write-once object storage with… | PLT-011 |
| DOC-188 | J11/W2 | M | B | record per archive item: archive id, SHA-256 hash, size,… | — |
| DOC-189 | J11/W2 | S | B | support renditions and versions of an archive item (for… | WRK-283 |
| DOC-190 | J11/W2 | M | B | restrict access to archive items by the access rules… | PLT-001, PLT-002 |
| DOC-191 | J11/W2 | M | B | assign each archive item a retention class (RC-DOC-* for… | PLT-235, PLT-237 |
| DOC-192 | J11/W2 | S | B | check legal holds through plt.LegalHold.check before any purge or… | PLT-239 |
| DOC-193 | J11/W2 | S | B | let the owning module change an item's retention class… | WRK-282 |
| DOC-194 | J11/W2 | S | E | verify archive integrity: hash re-computation of a daily rolling… | PLT-012 |
| DOC-196 | J11/W2 | S | B | provide doc.Archive.get returning a short-lived, single-use signed download link… | — |
| DOC-197 | J11/W2 | S | B | accept binaries up to the configured size (default 100… | — |
| DOC-198 | J11/W2 | S | B | refuse to store binaries that WRK has not marked… | WRK-263 |
| DOC-200 | J11/W2 | S | B | execute erasure tasks from CMP for archive items whose… | CMP-005 |
| DOC-201 | J11/W2 | S | B | store evidence files for other modules (PFC evidence packs,… | PFC-205 |
| DOC-202 | J11/W2 | S | B | list archive items by object with pagination and filters… | — |
| DOC-203 | J11/W2 | S | B | re-point archive links when PTY publishes PartiesMerged or PartyUnmerged. | PTY-007 |
| DOC-204 | J11/W2 | S | B | replicate the archive to a second EU location within… | PLT-013 |
| DOC-205 | J11/W6 | S | B | reprint an archived document by retrieving the archived bytes… | — |
| DOC-206 | J11/W6 | S | B | provide copies of documents and of information given during… | CHN-004 |
| DOC-208 | J11/W6 | M | B | render a current-state document "as of" a business date… | POL-002 |
| DOC-210 | J11/W6 | S | B | supersede a document when a later document replaces it… | — |
| DOC-211 | J11/W6 | S | B | resend an archived document to the original recipient or… | — |
| DOC-212 | J11/W6 | S | B | limit reprint and resend of statutory notices to permitted… | — |
| DOC-213 | J3/W4 | M | B | hold documents whose type is flagged fiscal (Greece pack:… | CMP-001 |
| DOC-214 | J3/W4 | S | B | print the MARK and render the QR code from… | CMP-001 |
| DOC-215 | J3/W4 | S | B | raise a WRK activity and show warning state on… | WRK-001 |
| DOC-216 | J3/W4 | M | B | issue the provisional proof of cover (cover note) as… | POL-176, POL-177 |
| DOC-217 | J3/W4 | S | E | record, per document type, the fiscal-coupling decision (fiscal flag,… | — |
| DOC-218 | J3/W4 | L | B | render and deliver the pre-contractual pack (IPID, pre-contractual information,… | POL-172, PTY-005 |
| DOC-219 | J3/W4 | M | B | render the IPID from PFC's structured IPID data (REQ-PFC-159)… | PFC-159 |
| DOC-220 | J3/W4 | M | E | pre-render and archive the IPID per product version, offering… | PFC-161, PFC-009 |
| DOC-221 | J3/W4 | M | B | for distance sales, deliver pre-contractual information on a durable | CHN-004 |
| DOC-222 | J3/W4 | S | B | replace the cover note by the policy pack: when… | CHN-004 |
| DOC-223 | J3/W4 | M | B | let intermediaries with the producer authority "issue cover notes"… | PTY-008, CHN-005 |
| DOC-224 | J3/W4 | S | B | provide doc.ProofOfCover.issue(transactionId) idempotent per transaction and return the document… | POL-176 |
| DOC-225 | J11/W2 | M | B | resolve, per recipient and document, the delivery medium (paper,… | PTY-005 |
| DOC-226 | J11/W2 | S | B | use paper as the default medium for IDD information… | PTY-147 |
| DOC-227 | J11/W2 | S | B | deliver an additional paper copy free of charge when… | PTY-149 |
| DOC-228 | J11/W2 | M | B | send emails through the email channel adapter with the… | PLT-006 |
| DOC-229 | J11/W2 | S | E | protect email attachments containing P2 data by delivering a… | CHN-004 |
| DOC-230 | J11/W2 | M | B | deliver to the customer portal inbox (CHN) with read… | CHN-004, CHN-009 |
| DOC-231 | J11/W2 | S | B | for website delivery, send the electronic notification of the | PTY-148 |
| DOC-232 | J11/W2 | S | B | apply each type's fallback chain (for example email →… | PTY-084 |
| DOC-233 | J11/W2 | S | B | publish DeliveryFailed with reason (hard bounce, returned mail, invalid… | PLT-005 |
| DOC-234 | J11/W2 | S | B | publish DocumentDelivered per recipient when the delivery reaches the… | POL-186 |
| DOC-235 | J11/W2 | S | B | re-resolve recipients and addresses at delivery time for queued… | PTY-012 |
| DOC-236 | J11/W2 | S | B | keep documents available in the portal for the customer… | CHN-004 |
| DOC-238 | J11/W2 | S | B | let a permitted user suppress delivery of a specific… | PLT-004 |
| DOC-239 | J11/W2 | M | B | refuse delivery of marketing content without marketing consent for… | PTY-005, PTY-150 |
| DOC-240 | J11/W2 | M | B | compose SMS texts with an encoding and segment calculator… | — |
| DOC-241 | J11/W2 | S | B | send SMS only with alphanumeric sender IDs registered for… | — |
| DOC-242 | J11/W2 | S | E | never put personal data beyond first name and document… | — |
| DOC-245 | J11/W2 | S | B | support delivery to intermediaries through the broker portal and… | CHN-009 |
| DOC-246 | J11/W2 | M | B | integrate print-and-post vendors through print vendor profiles (file format,… | PLT-006, PLT-153 |
| DOC-247 | J11/W2 | M | B | register every delivery provider (email, SMS, print vendor, signature… | PLT-012 |
| DOC-248 | J11/W2 | S | B | reconcile print vendor reports against handed-over manifests daily and… | WRK-001 |
| DOC-249 | J11/W2 | S | B | expose doc.Delivery.status(documentId) and doc.Delivery.evidenceFor(subjectRef, purpose) for callers and channels. | — |
| DOC-250 | J11/W6 | L | B | assemble an evidence pack for every statutory-notice delivery containing:… | PLT-125 |
| DOC-251 | J11/W6 | S | E | classify proof levels (SENT, DELIVERED, OPENED, ACKNOWLEDGED, REGISTERED_DELIVERED, SIGNED_FOR,… | — |
| DOC-252 | J11/W6 | M | B | deliver the non-payment notice, insurer cancellation notice, non-renewal notice… | — |
| DOC-253 | J11/W6 | S | B | provide CMP with complaint-reply delivery proof including the delivery… | CMP-004 |
| DOC-255 | J11/W6 | S | E | show statutory notices with an overdue or breached state… | CMP-003 |
| DOC-256 | J11/W6 | S | B | never alter a sealed evidence pack; later events (for… | — |
| DOC-257 | J11/W6 | S | B | export evidence packs individually or in bulk (by type,… | — |
| DOC-258 | J11/W6 | S | B | record the legal basis and rule version used for… | MKT-002 |
| DOC-260 | J3/W4 | M | B | create signature envelopes with documents (archived, hashed), signatories (party… | PTY-001 |
| DOC-261 | J3/W4 | S | B | select the provider and level per document type, jurisdiction… | MKT-112 |
| DOC-262 | J3/W4 | M | B | implement the gov.gr co-signing provider: upload the document as… | — |
| DOC-263 | J3/W4 | S | B | implement a commercial signature provider adapter supporting advanced and… | MKT-112 |
| DOC-264 | J3/W4 | S | B | send reminders per schedule, allow resend of the signing… | — |
| DOC-265 | J3/W4 | S | B | let a permitted user void an envelope with reason… | — |
| DOC-266 | J3/W4 | S | B | record a signatory's decline with reason and publish SignatureDeclined. | — |
| DOC-267 | J3/W4 | S | B | validate signed outputs (signature or seal validity, certificate chain,… | — |
| DOC-269 | J3/W4 | S | B | offer signing journeys to customers inside the CHN portal… | CHN-004 |
| DOC-270 | J3/W4 | S | B | publish SignatureCompleted with envelope id, documents, signed archive references… | POL-257 |
| DOC-271 | J3/W4 | S | B | show envelope status per signatory (Draft, Sent, Viewed, Signed,… | — |
| DOC-273 | J3/W4 | S | E | not let AI agents or services sign on behalf… | PLT-111 |
| DOC-275 | J3/W4 | M | B | provide the Documents tab as an embeddable component registered… | PLT-327, PLT-337 |
| DOC-276 | J3/W4 | M | B | filter the list by name (accent-insensitive contains), related-to object,… | MKT-178 |
| DOC-277 | J3/W4 | M | B | show columns name, actions, type or class, status (document… | PLT-328 |
| DOC-278 | J3/W4 | S | B | let users with permission doc.document.hide hide and unhide documents… | PLT-002 |
| DOC-279 | J3/W4 | S | B | replace the baseline "Delete Selected" by: delete for unsent… | PLT-237 |
| DOC-280 | J3/W4 | S | B | offer "New document" as a split action: generate from… | WRK-005 |
| DOC-281 | J3/W4 | M | B | provide row actions view (in-app viewer with page thumbnails),… | — |
| DOC-282 | J3/W4 | S | B | show a degraded-state banner when the archive or rendering… | PLT-164 |
| DOC-283 | J3/W4 | S | B | merge inbound rows from REQ-WRK-285 with outbound rows in… | WRK-285 |
| DOC-284 | J3/W4 | S | B | apply ABAC to rows: intermediaries see only documents for… | PLT-001 |
| DOC-285 | J3/W4 | M | B | provide the Forms panel for a job version or… | — |
| DOC-289 | J3/W4 | S | B | provide CHN with a customer-scoped document list query that… | CHN-004 |
| DOC-290 | J11/W2 | S | B | produce customer-facing PDFs as tagged PDFs with logical reading… | — |
| DOC-291 | J11/W2 | S | E | conform customer PDFs to PDF/UA (ISO 14289) and verify… | — |
| DOC-292 | J11/W2 | S | B | provide an HTML alternative of every customer document in… | CHN-004 |
| DOC-293 | J11/W2 | S | B | tag language changes within content (for example English terms… | MKT-183 |
| DOC-296 | J11/W2 | S | B | ensure colour is never the only carrier of meaning… | — |
| DOC-298 | J11/W2 | S | B | preview any template, block, clause or form with a… | — |
| DOC-299 | J11/W2 | S | B | maintain test suites per template: named sample payloads with… | — |
| DOC-300 | J11/W2 | S | E | compare renditions against golden documents after template, clause, font… | PLT-265 |
| DOC-301 | J11/W2 | S | E | run all template test suites and the Greek text… | PLT-265 |
| DOC-306 | J11/W2 | S | B | provide the dry-run of a document request through doc.Document.preview… | CHN-001 |
| DOC-307 | J11/W2 | M | B | provide the output operations dashboard with render throughput, latency… | PLT-253 |
| DOC-308 | J11/W2 | S | B | emit business SLIs: bind-to-cover-note availability time, issuance-to-delivery time per… | PLT-255 |
| DOC-309 | J11/W2 | S | B | manage SMS sender identities, email sender domains and print… | PLT-004 |
| DOC-310 | J11/W2 | S | B | expose configuration keys (section 10.2) through the MKT configuration… | MKT-001 |
| DOC-311 | J11/W2 | L | B | provide doc.Document.import for migration: legacy document binaries with metadata… | MIG-001, MIG-002 |
| DOC-312 | J11/W2 | S | B | import legacy templates and wordings only as Draft objects… | — |
| DOC-313 | J11/W2 | S | B | provide DSAR export of documents where the data subject… | CMP-005 |
| DOC-314 | J11/W2 | S | B | provide doc.Subject.restrict and doc.Subject.erase for CMP, applying REQ-DOC-200 rules… | CMP-005 |
| DOC-315 | J11/W2 | S | B | keep payloads under the same retention class as their… | PLT-237 |
| DOC-316 | J11/W2 | S | B | pseudonymise payloads and archive content copied to non-production environments,… | PLT-242 |
| DOC-317 | J11/W2 | S | B | provide the archive explorer for auditors: search by number,… | PLT-002 |
| DOC-318 | J11/W2 | M | B | send document volumes, delivery outcomes, channel mix, failure classes… | DAT-001, DAT-007 |
| DOC-319 | J11/W2 | S | B | run the module fully with all AI features disabled,… | PLT-220 |
| DOC-320 | J11/W2 | M | E | accept AI-generated text in a payload supplement only when… | PLT-224 |
| DOC-321 | J11/W2 | L | B | decide whether an object is mastered in the core… | MIG-149 |
| DOC-322 | J11/W2 | M | E | keep IPID rendering, the pre-contractual pack, the cover note… | POL-172 |
| DOC-323 | J11/W2 | M | E | serve quote-document previews (DRAFT, no archive, no seal) on… | — |

### 1.6 CMP (183 rows in range)

J/W mix: J10/W8×99, J4/W5×55, J10/W2×28, J13/W9×1

| ID | J/W | Sz | Tag | Title (first words) | Deps |
|---|---|---|---|---|---|
| CMP-001 | J4/W5 | XL | B | provide the fiscal-document channel: accept fiscal sources from BIL… | MKT-088, BIL-096, CLM-138, PLT-006 |
| CMP-002 | J4/W5 | XL | B | provide the motor bureau adapter: derive insured-vehicle facts from… | MKT-093, POL-315 |
| CMP-003 | J10/W2 | XL | B | provide the statutory clock register and clock API: clock… | MKT-009, PLT-007, PLT-168 |
| CMP-004 | J10/W8 | XL | B | provide complaints management: intake from every channel, classification, linking… | MKT-099, CHN-150, DOC-045 |
| CMP-005 | J10/W8 | XL | B | provide DSAR orchestration: intake with identity verification, a fan-out… | PTY-161, PLT-248, WRK-370 |
| CMP-006 | J10/W8 | XL | B | provide the obligations register and traceability: obligations (OBL- codes… | MKT-148, PLT-244 |
| CMP-007 | J10/W8 | XL | B | provide the AI system register: one entry per AI… | PLT-010, PLT-233, DAT-005 |
| CMP-008 | J10/W8 | XL | B | provide the regulatory report submission tracker: regulatory submission calendar… | DAT-003, FIN-187, PLT-305 |
| CMP-009 | J13/W9 | XL | B | provide migration import and external hand-over: import APIs for… | MIG-001, MIG-117, MIG-124, MIG-130, MIG-162 … |
| CMP-030 | J4/W5 | M | B | accept fiscal requests through cmp.FiscalDocument.request with source type (TRANSACTION,… | BIL-096 |
| CMP-031 | J4/W5 | S | E | derive the fiscal idempotency key as source type +… | PLT-005 |
| CMP-032 | J4/W5 | S | B | accept credit and cancellation requests referencing the original FiscalDocument… | BIL-097 |
| CMP-033 | J4/W5 | S | B | accept claim-payment fiscal requests from CLM (ruling R-43), with… | CLM-138 |
| CMP-034 | J4/W5 | M | B | accept commission fiscal requests from BIL for self-billing agreements… | BIL-101, PTY-240 |
| CMP-035 | J4/W5 | M | B | resolve every line's fiscal classification (document line category, income… | PFC-121, MKT-088 |
| CMP-036 | J4/W5 | M | B | determine business versus individual per document from PTY at… | PTY-001, PTY-003 |
| CMP-037 | J4/W5 | M | B | include on business documents the counterparty's tax identifier, tax… | PTY-059, PTY-012 |
| CMP-038 | J4/W5 | S | B | assign series and number to each fiscal document through… | PLT-014 |
| CMP-039 | J4/W5 | S | B | round line and document totals with the MKT currency… | MKT-006 |
| CMP-040 | J4/W5 | S | E | validate each built document against the channel's current schema… | MKT-088 |
| CMP-041 | J4/W5 | S | B | run each fiscal document through a workflow on the… | PLT-007 |
| CMP-042 | J4/W5 | S | E | transmit documents in micro-batches (size and maximum wait per… | PLT-006 |
| CMP-043 | J4/W5 | M | B | parse every channel response at document level regardless of… | — |
| CMP-044 | J4/W5 | S | B | never automatically retry a document rejected with a business… | — |
| CMP-045 | J4/W5 | M | B | retry technical failures (timeouts, server errors, throttling, Unknown results)… | — |
| CMP-046 | J4/W5 | S | B | publish FiscalDocRegistered (fiscal document id, source type and id,… | PLT-005 |
| CMP-047 | J4/W5 | S | B | maintain a rejection queue grouping open rejections by cause… | — |
| CMP-048 | J4/W5 | M | B | create a WRK activity CMP-FISCAL-REJECTED per rejection (or per… | WRK-001, WRK-043 |
| CMP-049 | J4/W5 | M | B | let an authorised analyst correct a rejected document by… | PLT-002 |
| CMP-051 | J4/W5 | S | B | require maker-checker approval for document-level field overrides and for… | PLT-004 |
| CMP-053 | J4/W5 | M | B | reconcile daily, per legal entity and series, the registered… | BIL-100, FIN-197 |
| CMP-054 | J4/W5 | S | B | expose cmp.FiscalDocument.get, listBySource and qrPayload so BIL, CLM, FIN… | DOC-214 |
| CMP-056 | J4/W5 | S | B | queue fiscal documents during a channel outage, keep accepting… | PLT-006 |
| CMP-059 | J4/W5 | S | B | archive every request and response to the channel (encrypted,… | PLT-156 |
| CMP-060 | J4/W5 | M | B | keep a per-entity fiscal channel configuration (environment, credential reference… | PLT-004, PLT-008 |
| CMP-061 | J4/W5 | S | B | show per document the full history (source, lines, classifications,… | — |
| CMP-062 | J4/W5 | S | B | route business-counterparty documents to the EInvoiceProvider SPI instead of… | MKT-004 |
| CMP-063 | J4/W5 | S | B | keep the same FiscalDocument lifecycle, events and idempotency for… | — |
| CMP-066 | J4/W5 | S | E | record the applicability decision (in scope, out of scope,… | MKT-004 |
| CMP-068 | J4/W5 | M | B | consume PolicyBound, PolicyIssued, PolicyChanged, PolicyCancelled, PolicyVoided, PolicyReinstated, PolicyRewritten, RenewalBound,… | POL-002, POL-315 |
| CMP-069 | J4/W5 | M | B | derive canonical insured-vehicle facts (COVER_START, COVER_END, COVER_VOID, VEHICLE_REPLACED, PLATE_CHANGED,… | POL-185 |
| CMP-070 | J4/W5 | S | E | net out-of-sequence changes: on TransactionReversed/TransactionReapplied it recomputes the vehicle's… | POL-006 |
| CMP-071 | J4/W5 | S | B | not report facts for quotes, unbound jobs or non-motor… | — |
| CMP-072 | J4/W5 | S | B | submit facts through BureauAdapter.report with the fact id as… | MKT-093 |
| CMP-073 | J4/W5 | S | B | run the bureau transport on the PLT adapter host… | PLT-006 |
| CMP-074 | J4/W5 | S | B | support transport implementations as pack components selected by configuration:… | MKT-093 |
| CMP-075 | J4/W5 | S | B | record acknowledgements per fact (Acknowledged with bureau reference, Rejected… | — |
| CMP-076 | J4/W5 | S | B | let an authorised user retransmit selected facts or include… | PLT-002 |
| CMP-077 | J4/W5 | S | B | route bureau rejections to the motor operations queue as… | WRK-001 |
| CMP-078 | J4/W5 | S | B | publish BureauEventSubmitted (policy, fact type, plate reference, submission id)… | POL-316 |
| CMP-079 | J4/W5 | S | B | reconcile periodically (Greece pack default daily where the transport… | MKT-093 |
| CMP-080 | J4/W5 | S | E | reconcile CMP's expected state to POL's in-force motor book… | POL-002 |
| CMP-081 | J4/W5 | S | B | compute lag per fact as the time from the… | PLT-332 |
| CMP-082 | J4/W5 | S | B | hold lag thresholds as pack data per fact type… | MKT-009 |
| CMP-083 | J4/W5 | S | B | publish BureauLagExceeded (policy, fact, lag, threshold) when a fact… | WRK-001 |
| CMP-084 | J4/W5 | S | E | open a severity-2 incident in PLT incident management when… | PLT-012 |
| CMP-085 | J4/W5 | S | B | retain insured-vehicle facts, bureau transmissions and acknowledgements for seven… | PLT-011 |
| CMP-086 | J4/W5 | M | B | record the bureau hand-over date per vehicle for migration,… | MIG-004 |
| CMP-087 | J4/W5 | S | E | provide a hand-over reconciliation per migration wave listing vehicles… | MIG-004 |
| CMP-088 | J4/W5 | S | B | provide cmp.Bureau.status(policyId or plate) returning the latest fact, acknowledgement… | — |
| CMP-090 | J4/W5 | S | B | report Green Card numbers per vehicle and term as… | POL-185 |
| CMP-092 | J10/W2 | M | B | hold one StatutoryClockDef per clock code with: code (<MOD>_<NAME>),… | MKT-009 |
| CMP-093 | J10/W2 | S | B | seed the register with every clock code in §10.5… | — |
| CMP-094 | J10/W2 | S | B | require maker-checker approval (regulatory analyst and compliance officer) to… | PLT-004 |
| CMP-095 | J10/W2 | S | B | provide cmp.Clock.start(clockCode, subjectRef, jurisdiction, startTime, causationEventId, Idempotency-Key) returning the… | — |
| CMP-096 | J10/W2 | S | B | provide pause, resume, stop(outcome), cancel(reason), extend(reason, legalBasis), get and… | — |
| CMP-097 | J10/W2 | M | E | also start and stop clocks from events declared in… | BIL-007 |
| CMP-098 | J10/W2 | M | B | obtain duration, unit, calendar id, start rule, stop rule,… | MKT-286, MKT-291 |
| CMP-099 | J10/W2 | S | B | fail closed when no pack value exists: refuse the… | MKT-094 |
| CMP-100 | J10/W2 | M | B | compute deadlines with PLT calendar arithmetic (REQ-PLT-197) in the… | PLT-197 |
| CMP-101 | J10/W2 | S | B | support units CALENDAR_DAYS, BUSINESS_DAYS, WEEKS, MONTHS and FIXED_DATE (for… | MKT-285 |
| CMP-102 | J10/W2 | S | B | support variant keys in value sets (for example accident… | CLM-191 |
| CMP-103 | J10/W2 | S | B | pause an instance on its declared pause events or… | POL-308 |
| CMP-104 | J10/W2 | S | B | apply extension only where the value set permits it… | PLT-004 |
| CMP-105 | J10/W2 | S | B | publish ClockWarned at each warning threshold (defaults: 50% and… | WRK-192 |
| CMP-106 | J10/W2 | S | B | move a DEADLINE-kind instance to Breached at the deadline… | WRK-193 |
| CMP-107 | J10/W2 | S | B | keep a Breached instance open until stopped, recording the… | CLM-007 |
| CMP-108 | J10/W2 | S | B | survive failover without losing or duplicating timers, using the… | PLT-165 |
| CMP-111 | J10/W2 | S | B | provide the statutory clock dashboard (SCR-CMP-02) with counts by… | — |
| CMP-112 | J10/W2 | S | B | produce management information per month: started, met, met late,… | DAT-001 |
| CMP-113 | J10/W2 | S | B | show on every instance the frozen value set, legal… | — |
| CMP-114 | J10/W2 | S | B | send breach alerts to the owning module's supervisors and… | WRK-007 |
| CMP-116 | J10/W2 | S | E | run a full regulatory calendar dry-run in non-production: given… | PLT-332 |
| CMP-117 | J10/W2 | S | B | expose clock instances to the subject's owner through cmp.Clock.query(subjectRef)… | CHN-169 |
| CMP-118 | J10/W2 | S | B | allow cancel only with a reason from the definition's… | PLT-002 |
| CMP-120 | J10/W2 | S | E | flag definitions whose pack value is UNVERIFIED in the… | — |
| CMP-121 | J10/W2 | S | B | provide pack-certification support: cmp.ClockDef.list(jurisdiction) returning every Active definition applicable… | MKT-290 |
| CMP-122 | J10/W8 | M | B | accept complaints through cmp.Complaint.create from the customer portal and… | CHN-150, WRK-219, CLM-187 |
| CMP-123 | J10/W8 | S | B | record complainant type (policyholder, insured, claimant, beneficiary, prospective customer,… | PTY-001 |
| CMP-124 | J10/W8 | M | B | link a complaint to one or more parties, policies,… | CLM-011, POL-014 |
| CMP-125 | J10/W8 | S | B | check at intake whether the incoming message is a… | CLM-001 |
| CMP-126 | J10/W8 | S | B | call ComplaintRules.rules(jurisdiction, complainantType, channel, date) at receipt and start… | MKT-099 |
| CMP-127 | J10/W8 | S | B | fail closed when ComplaintRules returns no rules for the… | MKT-099 |
| CMP-128 | J10/W8 | M | B | classify each complaint by product line, subject category (sales… | MKT-007 |
| CMP-129 | J10/W8 | S | B | send an acknowledgement through DOC (document type DT-COMPLAINT-ACK with… | DOC-045 |
| CMP-130 | J10/W8 | M | B | assign complaints to the complaints queue in WRK through… | WRK-003, PLT-001 |
| CMP-131 | J10/W8 | S | B | record investigation actions (ComplaintAction: type, owner, due date, outcome,… | WRK-001 |
| CMP-132 | J10/W8 | M | B | pull evidence on demand from owners: consent and preference… | PTY-160, DOC-249 |
| CMP-134 | J10/W8 | S | B | record the outcome (upheld, partly upheld, rejected, withdrawn), the… | BIL-009 |
| CMP-135 | J10/W8 | M | B | issue the reasoned reply through DOC (DT-COMPLAINT-REPLY) as a… | DOC-045, DOC-253 |
| CMP-136 | J10/W8 | S | B | require approval of the reply by a second complaints… | PLT-004 |
| CMP-137 | J10/W8 | S | B | move an Answered complaint to Escalated when the complainant… | — |
| CMP-139 | J10/W8 | S | B | close a complaint after Answered when no escalation is… | — |
| CMP-141 | J10/W8 | S | B | publish the complaints procedure text per entity and language… | CHN-004 |
| CMP-142 | J10/W8 | S | B | keep complaint data under RC-CMP-COMPLAINT and restrict access to… | PLT-001 |
| CMP-143 | J10/W8 | S | B | route data-protection complaints (category data protection) to the DPO… | — |
| CMP-145 | J10/W8 | S | B | require root-cause coding (process, product, people, system, third party,… | — |
| CMP-146 | J10/W8 | M | B | produce complaint statistics per period, entity, product and category… | — |
| CMP-149 | J10/W8 | S | B | publish ComplaintReceived and ComplaintAnswered and events ComplaintAcknowledged, ComplaintEscalated, ComplaintClosed… | PLT-005 |
| CMP-150 | J10/W8 | S | B | accept DSARs through cmp.Dsar.create from CHN (REQ-CHN-149), staff and… | CHN-149 |
| CMP-151 | J10/W8 | S | B | record identity assurance: accept the CHN-authenticated identity level as… | PLT-015 |
| CMP-152 | J10/W8 | M | B | resolve the data subject to PTY party ids, including… | PTY-001, PTY-007 |
| CMP-153 | J10/W8 | S | B | start CMP_DSAR_RESPONSE at receipt (or at identity verification where… | — |
| CMP-154 | J10/W8 | S | B | hold the DSAR fan-out map as configuration: per module… | — |
| CMP-155 | J10/W8 | S | B | run the fan-out as a workflow saga: create one… | PLT-007 |
| CMP-156 | J10/W8 | S | B | retry failed tasks with backoff and, after the task… | WRK-001 |
| CMP-157 | J10/W8 | S | B | evaluate erasure scope through PTY first (pty.Dsar.evaluateErasure) and pass… | PTY-164 |
| CMP-158 | J10/W8 | M | B | collect per-item outcomes from modules (erased, anonymised, restricted with… | FIN-290, POL-339 |
| CMP-159 | J10/W8 | S | B | execute rectification requests by creating a correction activity per… | PTY-163 |
| CMP-160 | J10/W8 | S | B | produce the response letter listing actions taken per category… | DOC-001 |
| CMP-161 | J10/W8 | S | B | assemble an access or portability package with an index… | DOC-001 |
| CMP-162 | J10/W8 | S | B | require DPO review and approval of every package before… | WRK-374 |
| CMP-163 | J10/W8 | S | B | handle objection requests by passing them to PTY consent… | PTY-005 |
| CMP-164 | J10/W8 | S | B | deliver responses through DOC using a secure channel (portal… | DOC-005 |
| CMP-166 | J10/W8 | S | B | extend the deadline by up to two further months… | — |
| CMP-167 | J10/W8 | S | E | run a quarterly seeded DSAR completeness test with a… | WRK-372 |
| CMP-168 | J10/W8 | S | E | flag modules without a data-subject operation in the fan-out… | — |
| CMP-169 | J10/W8 | S | B | handle online-account deletion requests routed from PLT (REQ-PLT-070) as… | PLT-070 |
| CMP-170 | J10/W8 | S | B | maintain a disclosure log recording each disclosure of personal… | — |
| CMP-171 | J10/W8 | M | B | populate the disclosure log from CMP's own transmissions (Information… | PLT-156 |
| CMP-172 | J10/W8 | S | B | accept disclosure-log entries from modules through cmp.DisclosureLog.record (for example… | WRK-375 |
| CMP-174 | J10/W8 | S | B | run clock CMP_GDPR_BREACH_NOTIFY (72 hours from awareness) for personal-data… | PLT-012 |
| CMP-175 | J10/W8 | S | B | apply special handling for special-category data: P3 items are… | CLM-233 |
| CMP-176 | J10/W8 | S | B | publish DSARReceived and DSARCompleted and events DSARExtended and DSARTaskOverdue… | PLT-005 |
| CMP-191 | J10/W8 | M | B | link FIN's IPT and levy returns (REQ-FIN-180, REQ-FIN-192) to… | FIN-180, FIN-192 |
| CMP-192 | J10/W8 | S | B | record filing of FIN returns when FIN publishes TaxReturnFiled… | FIN-187 |
| CMP-195 | J10/W8 | M | B | maintain a regulatory submission calendar per legal entity generated… | — |
| CMP-196 | J10/W8 | S | B | start the deadline clock of each regulatory submission from… | — |
| CMP-197 | J10/W8 | S | B | attach the content produced by the source (DAT package… | DAT-003 |
| CMP-198 | J10/W8 | S | B | show validation results, reconciliation status and period-on-period variances supplied… | DAT-007 |
| CMP-200 | J10/W8 | S | B | require maker-checker approval of every regulatory submission by roles… | PLT-004 |
| CMP-201 | J10/W8 | S | B | record filing (channel, time, filer, filing reference), the authority's… | — |
| CMP-203 | J10/W8 | S | B | register DORA incident reports prepared and submitted by PLT… | PLT-305 |
| CMP-204 | J10/W8 | S | B | register the annual DORA register-of-information export (REQ-PLT-315) as a… | PLT-315 |
| CMP-206 | J10/W8 | S | B | produce a regulatory submission evidence pack (content hash, approvals,… | DOC-004 |
| CMP-208 | J10/W8 | M | B | hold Obligation records: code (OBL-<CODE> or OBL-<CODE>-<NN> sub-obligation), title… | — |
| CMP-209 | J10/W8 | S | B | seed the register with the contract §3.7 obligations and… | — |
| CMP-210 | J10/W8 | S | B | link obligations to requirement IDs (REQ-, BR-, NFR- of… | — |
| CMP-211 | J10/W8 | S | E | import the requirement catalogue (ID, module, title, priority, phase,… | — |
| CMP-212 | J10/W8 | S | B | link obligations to pack rules through MKT's rule index… | MKT-148 |
| CMP-213 | J10/W8 | S | B | hold Control records: name, type (preventive, detective, corrective), nature… | — |
| CMP-214 | J10/W8 | S | B | schedule control tests per frequency, assign them as WRK… | WRK-001 |
| CMP-215 | J10/W8 | S | E | generate evidence automatically for automated controls from module signals… | — |
| CMP-216 | J10/W8 | S | B | store Evidence immutably (archive reference via REQ-DOC-004, hash, period,… | DOC-004 |
| CMP-217 | J10/W8 | S | B | accept evidence from modules through cmp.Evidence.submit (for example sanctions… | PTY-181 |
| CMP-218 | J10/W8 | S | B | run review cycles: notify the owner before each obligation's… | WRK-001 |
| CMP-219 | J10/W8 | S | E | compute per obligation a status (Covered, Partially covered, Gap)… | — |
| CMP-220 | J10/W8 | S | B | provide the traceability view (SCR-CMP-01) from obligation to requirements,… | — |
| CMP-221 | J10/W8 | S | B | export the traceability matrix per obligation, module or release… | — |
| CMP-226 | J10/W8 | M | B | record regulatory changes with source (instrument, Gazette reference, regulator… | — |
| CMP-227 | J10/W8 | S | E | create backlog items automatically from UNVERIFIED pack values, open… | — |
| CMP-228 | J10/W8 | S | B | record an impact assessment per change: affected obligations, requirements,… | MKT-148 |
| CMP-229 | J10/W8 | S | B | require compliance officer approval of the assessment before planning. | PLT-004 |
| CMP-230 | J10/W8 | S | B | raise configuration change requests in MKT for pack value… | MKT-001 |
| CMP-231 | J10/W8 | S | B | plan each change against a pack version and release,… | MKT-003 |
| CMP-232 | J10/W8 | S | B | verify implementation by linking golden test results (REQ-MKT-008) and… | MKT-008 |
| CMP-233 | J10/W8 | S | B | provide the backlog view (SCR-CMP-09) with priority by effective… | — |
| CMP-237 | J10/W8 | M | B | hold AiSystem entries per AI feature: feature id (AI-<MOD>-NN),… | DAT-005 |
| CMP-238 | J10/W8 | S | B | seed the register with every AI feature in §11.9… | — |
| CMP-239 | J10/W8 | M | B | require compliance review of class and justification, with a… | PLT-004 |
| CMP-240 | J10/W8 | S | B | record transparency status per customer-facing feature (Art. 50 disclosure… | — |
| CMP-241 | J10/W8 | S | B | expose cmp.AiSystem.get(featureId) returning status so PLT refuses enablement of… | PLT-233 |
| CMP-242 | J10/W8 | S | B | suspend an entry when PLT reports the feature's kill… | PLT-010 |
| CMP-243 | J10/W8 | S | B | show the register (SCR-CMP-10) with filters by module, class,… | — |
| CMP-244 | J10/W8 | M | B | consume ModelDriftDetected, BiasThresholdBreached, AiToggleChanged and AiKillSwitchActivated and record them… | DAT-005, PLT-010 |
| CMP-246 | J10/W8 | S | B | publish event AiSystemStatusChanged (R-59). | PLT-005 |
| CMP-248 | J10/W8 | S | B | register CMP retention classes RC-CMP-FISCAL, RC-CMP-BUREAU, RC-CMP-CLOCK, RC-CMP-COMPLAINT, RC-CMP-DSAR,… | PLT-011 |
| CMP-249 | J10/W8 | M | B | provide import APIs for migration of open complaints, open… | MIG-001 |
| CMP-250 | J10/W8 | S | B | provide DSAR export, restriction and erasure operations for CMP-held… | — |
| CMP-251 | J10/W8 | S | B | expose adapter health (fiscal channel, e-invoicing provider, bureau transport,… | PLT-013 |
| CMP-252 | J10/W2 | M | B | classify every clock definition as DEADLINE or WAITING_PERIOD; a… | BIL-006 |
| CMP-254 | J4/W5 | L | B | keep an event-fed bureau routing cache from CoexistenceMasterChanged and… | MIG-004, MIG-149, MIG-162, MIG-163 |
| CMP-255 | J10/W8 | M | B | complete the DSAR's DAT erasure or restriction task from… | DAT-009, DAT-270, DAT-275 |

### 1.7 CHN (212 rows in range)

J/W mix: J3/W4×95, J5/W6×40, J3/W8×33, J1/W6×25, J3/W6×12, J7/W7×7

| ID | J/W | Sz | Tag | Title (first words) | Deps |
|---|---|---|---|---|---|
| CHN-001 | J3/W4 | XL | B | provide the external API gateway contract for partners: an… | PLT-058, PLT-293, POL-001, RAT-042, UW-058 |
| CHN-002 | J3/W4 | XL | B | hold and evaluate the transaction-permission matrix: for every transaction… | PLT-004, WRK-008, POL-194, UW-120, BIL-063 |
| CHN-003 | J3/W8 | XL | B | provide the MCP AI agent facade: an MCP server… | PLT-010, PLT-060, PLT-231, PTY-276, WRK-338 … |
| CHN-004 | J3/W4 | XL | B | provide the customer portal (web) and mobile app for… | PLT-015, MKT-189 |
| CHN-005 | J1/W6 | XL | B | provide the broker and agent portal covering dashboard, quote… | PLT-072, PTY-244, UW-084, BIL-247, BIL-268 |
| CHN-007 | J5/W6 | XL | B | provide the EU online withdrawal function for distance contracts… | POL-308, POL-314, DOC-044, PLT-332, MKT-287 |
| CHN-008 | J3/W4 | XL | B | provide customer and intermediary identity journeys on the PLT… | PLT-015, PLT-050, PLT-051, PLT-052, PLT-053 … |
| CHN-009 | J5/W6 | XL | B | manage notification and webhook subscriptions: customers and intermediaries choose… | WRK-351, DOC-245, UW-237 |
| CHN-030 | J3/W4 | M | B | serve each front-end family (customer web and app, broker… | PLT-265 |
| CHN-031 | J3/W4 | M | B | build quote, change and claim forms from owner descriptions… | PFC-222, PFC-006, CLM-035 |
| CHN-032 | J3/W4 | M | B | validate inputs in the front end only for immediate… | PFC-224, CLM-001 |
| CHN-033 | J3/W4 | M | B | obtain every UI string, date, number and currency format… | MKT-189, MKT-168 |
| CHN-034 | J3/W4 | M | B | keep a ChannelSession per signed-in or anonymous journey (channel,… | PLT-015 |
| CHN-035 | J3/W4 | M | B | record a ChannelInteraction for every significant journey step (journey… | PLT-002 |
| CHN-036 | J3/W4 | M | B | resolve the legal entity, jurisdiction and available channels for… | MKT-004, MKT-071 |
| CHN-037 | J3/W4 | S | B | propagate W3C trace context from the front end through… | PLT-013 |
| CHN-038 | J3/W4 | M | B | map owner error codes to channel messages through a… | PTY-183, UW-066 |
| CHN-040 | J3/W4 | L | E | support a degraded mode per front end when a… | PLT-013, POL-172 |
| CHN-042 | J3/W4 | M | E | expose a front-end configuration service delivering per entity, channel… | MKT-001 |
| CHN-045 | J3/W4 | S | B | maintain a catalogue of transaction types (section 10.1.2) each… | POL-001 |
| CHN-046 | J3/W4 | M | B | store matrix entries keyed by transaction type, channel, role,… | POL-008 |
| CHN-047 | J3/W4 | S | B | resolve the applicable entry by most-specific match (all optional… | — |
| CHN-048 | J3/W4 | M | B | version the matrix as a whole: draft, submitted, approved… | PLT-004, PLT-332 |
| CHN-049 | J3/W4 | M | B | evaluate the matrix through chn.Permission.evaluate(transactionType, channel, actor, onBehalfOf, objectRef,… | — |
| CHN-050 | J3/W4 | S | B | evaluate limits against the owner's dry-run result (premium change,… | POL-192 |
| CHN-051 | J3/W4 | S | B | apply the matrix identically to portal journeys, partner APIs,… | — |
| CHN-052 | J3/W4 | M | B | treat the matrix as an additional restriction only: an… | PLT-003 |
| CHN-053 | J3/W4 | S | B | hide or disable actions the matrix forbids for the… | — |
| CHN-054 | J3/W4 | M | B | when the outcome is REFER, create a typed back-office | WRK-211, POL-194 |
| CHN-056 | J3/W4 | S | E | provide a matrix test harness that evaluates a matrix… | — |
| CHN-058 | J3/W4 | S | B | keep certain entries final at the EU or country… | MKT-288 |
| CHN-060 | J3/W4 | M | B | provide customer registration with e-mail or mobile number verified… | PLT-051, PLT-052 |
| CHN-061 | J3/W4 | M | B | link a registered identity to the customer's PTY party… | PLT-051, PLT-056 |
| CHN-062 | J3/W4 | S | E | create the online identity as part of a quote-and-buy… | PLT-051 |
| CHN-063 | J3/W4 | M | B | provide sign-in with passkey, one-time code fallback and password… | PLT-052, PLT-054 |
| CHN-064 | J3/W4 | M | B | request step-up authentication before actions the owner or the… | PLT-053 |
| CHN-065 | J3/W4 | M | B | provide account recovery journeys for customers (second passkey, two… | PLT-063, PLT-064 |
| CHN-068 | J3/W4 | S | B | show and let the user end active sessions and… | PLT-069 |
| CHN-069 | J3/W4 | S | B | apply idle and absolute session timeouts per channel from… | PLT-015 |
| CHN-070 | J3/W4 | M | B | apply onboarding fraud controls: velocity limits on registrations per… | PLT-056 |
| CHN-071 | J3/W4 | M | B | let the customer change sign-in e-mail or mobile number… | PLT-069, PTY-001 |
| CHN-072 | J3/W4 | M | B | route a request to delete the online account to… | PLT-070, CMP-005 |
| CHN-080 | J3/W4 | M | B | offer a public motor quote entry (no sign-in) that… | POL-145, POL-136 |
| CHN-081 | J3/W4 | S | B | offer only products and offerings available for the entity,… | PFC-011 |
| CHN-082 | J3/W4 | M | B | capture vehicle data by plate or VIN with lookup… | MKT-105 |
| CHN-083 | J3/W4 | M | B | capture policyholder, owner and driver data required by the… | PTY-001, PTY-002 |
| CHN-084 | J3/W4 | S | B | save the quote draft automatically after each step and… | POL-011 |
| CHN-085 | J3/W4 | M | B | show an indicative price as soon as the minimum… | POL-147, POL-148 |
| CHN-086 | J3/W4 | M | B | run the demands-and-needs question set defined for the product… | POL-149 |
| CHN-087 | J3/W4 | S | B | warn the customer, and record the warning, when the… | POL-149 |
| CHN-088 | J3/W4 | M | B | flag sales that match the product's negative target market… | PFC-150, PFC-007 |
| CHN-089 | J3/W4 | M | B | let the customer compare offerings and coverage options side… | POL-011, RAT-001 |
| CHN-090 | J3/W4 | S | B | present the price breakdown from rat.Breakdown.get (REQ-RAT-008) with premium… | RAT-008 |
| CHN-091 | J3/W4 | M | B | show the automated-decision notice with a request-human-review link when… | RAT-240, UW-220 |
| CHN-092 | J3/W4 | M | B | show the IPID for the selected offering (doc.Ipid.get, REQ-DOC-220)… | DOC-220, DOC-007 |
| CHN-093 | J3/W4 | M | B | request delivery of the pre-contractual pack on a durable… | DOC-218, DOC-221, POL-172 |
| CHN-094 | J3/W4 | M | B | capture the customer's choice of information medium (paper, durable… | PTY-083, DOC-226 |
| CHN-095 | J3/W4 | S | B | show, for intermediated online sales, the intermediary information required… | PTY-196 |
| CHN-096 | J3/W4 | M | B | collect the answers to underwriting questions defined by the… | POL-149, PFC-006 |
| CHN-097 | J3/W4 | M | B | capture consents required for the purchase (electronic communication, marketing… | PTY-150, POL-173 |
| CHN-098 | J3/W4 | M | B | show payment plans available for the quote (bil.PaymentPlan.list) with… | BIL-003, BIL-058 |
| CHN-099 | J3/W4 | S | E | check the bind gates in dry-run (pol.Job.bind with dryRun:… | POL-003 |
| CHN-100 | J3/W4 | M | B | collect the down payment before bind through BIL (bil.DownPayment.initiate)… | BIL-054, BIL-055 |
| CHN-101 | J3/W4 | M | B | call pol.Job.bind with the customer as confirming party only… | POL-001, POL-003 |
| CHN-102 | J3/W4 | M | B | display and offer download of the provisional proof of… | DOC-224, DOC-222 |
| CHN-103 | J3/W4 | M | B | take card and instant payments only through BIL's hosted… | BIL-116, BIL-117 |
| CHN-104 | J3/W4 | M | B | show a neutral pending message, never screening or fraud… | PTY-183, BIL-056 |
| CHN-105 | J3/W4 | M | B | show a neutral "not available online" message with contact… | UW-254, WRK-211 |
| CHN-106 | J3/W4 | M | B | show the customer text of underwriting issues (uw.Issue.listForChannel with… | UW-084, UW-120 |
| CHN-112 | J3/W4 | S | B | stop waiting and fall back to manual entry when… | MKT-105 |
| CHN-114 | J3/W4 | S | B | keep manual entry available and equally prominent at every… | — |
| CHN-115 | J3/W4 | M | B | store a WalletConsentReceipt per consent (service request id, legal… | PLT-011 |
| CHN-116 | J3/W4 | M | B | use wallet data only for the quote and the… | PTY-151 |
| CHN-120 | J5/W6 | L | B | show a customer dashboard (IB-11 work-left home) listing the… | POL-002, BIL-001, CLM-009, DOC-289 |
| CHN-121 | J5/W6 | M | B | show a policy detail view as of today (pol.Policy.get,… | POL-002 |
| CHN-122 | J5/W6 | M | B | offer intent-based self-service changes — change of address, replace… | POL-191, POL-195 |
| CHN-123 | J5/W6 | M | B | preview every change with POL dry-run (pol.PolicyChange.create with dryRun)… | POL-192, POL-193, BIL-058 |
| CHN-124 | J5/W6 | M | B | bind a self-service change only after an explicit confirmation… | POL-001, POL-194 |
| CHN-125 | J5/W6 | M | B | update correspondence address, phone and e-mail through PTY with… | PTY-001, PTY-012 |
| CHN-126 | J5/W6 | M | B | show renewal offers (RenewalOffered) with the new price, the… | POL-009 |
| CHN-127 | J5/W6 | M | B | let customers request cancellation (non-renewal, cancellation at a date,… | POL-012, WRK-211, WRK-260 |
| CHN-130 | J5/W6 | S | B | let customers request certificates and statements (claims-history certificate clm.Certificate.request,… | CLM-170 |
| CHN-131 | J5/W6 | M | B | show contingencies on a policy with customer texts, due… | UW-246, WRK-260 |
| CHN-133 | J5/W6 | M | B | show the customer's billing summary (IB-13 statement view): balance… | BIL-001, BIL-145 |
| CHN-134 | J5/W6 | M | B | let the customer pay any due or overdue amount… | BIL-144, BIL-004 |
| CHN-135 | J5/W6 | M | B | let the customer set or change the payment method… | BIL-105, BIL-106, BIL-104 |
| CHN-136 | J5/W6 | M | B | let the customer change the due day or payment… | BIL-059, BIL-063 |
| CHN-137 | J5/W6 | M | B | show refunds with status (proposed, approved, paid with date… | BIL-196, BIL-343, BIL-344 |
| CHN-139 | J5/W6 | M | B | provide a document vault per customer listing customer-visible documents… | DOC-289, DOC-008 |
| CHN-140 | J5/W6 | S | B | record portal opening of documents delivered to the portal… | DOC-230 |
| CHN-141 | J5/W6 | S | B | present signing tasks for documents needing e-signature and hand… | DOC-269 |
| CHN-143 | J5/W6 | M | B | provide secure messaging between customers or intermediaries and the… | CLM-181, CLM-184, WRK-211 |
| CHN-144 | J5/W6 | S | B | maintain a notification centre per user with three classes… | — |
| CHN-145 | J5/W6 | S | B | derive notifications from owner events (section 8.2), filter them… | PLT-005 |
| CHN-146 | J5/W6 | S | B | let users choose notification channels (in-app, push, e-mail, SMS)… | PTY-005 |
| CHN-147 | J5/W6 | M | B | provide a consent and preference centre for customers that… | PTY-158, PTY-005 |
| CHN-148 | J5/W6 | S | B | send e-mail and SMS notifications through DOC's channel adapters… | DOC-228 |
| CHN-149 | J5/W6 | S | B | offer the customer an entry to submit a data-subject… | CMP-005 |
| CHN-150 | J5/W6 | S | B | offer a complaint entry in every portal and app… | CMP-004 |
| CHN-151 | J7/W7 | M | B | provide claim reporting for customers and intermediaries through clm.Fnol.saveDraft,… | CLM-001, CLM-030, CLM-031 |
| CHN-152 | J7/W7 | S | E | autosave FNOL drafts (device and channel draft store), allow… | CLM-001 |
| CHN-153 | J7/W7 | M | B | let the reporter attach photos, video and documents with… | WRK-260, PLT-161 |
| CHN-154 | J7/W7 | M | B | support the joint accident report flow of the market's… | CLM-001, CLM-155 |
| CHN-155 | J7/W7 | S | B | show claim tracking (clm.ClaimTracking.get, REQ-CLM-009): customer-safe status, milestones, next… | CLM-009 |
| CHN-156 | J7/W7 | S | B | notify claim reporters on ClaimReported, status changes (ClaimUpdated), document… | CLM-177 |
| CHN-158 | J7/W7 | L | B | let claimants propose or confirm their payee bank account… | BIL-343, BIL-344, BIL-345, CLM-166 |
| CHN-160 | J5/W6 | M | B | display the withdrawal function, labelled with the pack's statutory… | POL-308 |
| CHN-161 | J5/W6 | M | B | determine eligibility and deadline from POL (withdrawal clock POL_WITHDRAWAL_DISTANCE… | POL-308, CMP-003 |
| CHN-163 | J5/W6 | S | E | provide a public withdrawal page reachable from the footer… | — |
| CHN-164 | J5/W6 | M | B | collect the declaration fields — name, contract identification (policy… | PTY-001 |
| CHN-165 | J5/W6 | M | B | present a confirmation step summarising the declaration with a… | PLT-332 |
| CHN-166 | J5/W6 | M | B | persist the WithdrawalRequest durably before replying to the customer,… | POL-314 |
| CHN-167 | J5/W6 | M | B | trigger the acknowledgement on a durable medium containing the… | DOC-001, DOC-044 |
| CHN-168 | J5/W6 | M | B | call pol.Withdrawal.submit (right WITHDRAWAL, receivedAt = confirmedAt, channel reference,… | POL-314, BIL-181, BIL-196 |
| CHN-169 | J5/W6 | M | B | show the refund deadline (Greece pack: 30 calendar days… | CMP-003, CMP-117 |
| CHN-170 | J5/W6 | M | B | accept and acknowledge every confirmed declaration even when the… | WRK-211 |
| CHN-171 | J5/W6 | S | B | prevent duplicate processing when the same customer confirms more… | POL-001 |
| CHN-173 | J5/W6 | S | B | keep the function fully keyboard- and screen-reader-operable, available in… | — |
| CHN-180 | J1/W6 | M | B | provide a broker and agent dashboard (IB-11 work-left home,… | POL-331, UW-084, WRK-001 |
| CHN-181 | J1/W6 | M | B | let producers start a quote for an existing account… | PTY-004, PTY-245, POL-145 |
| CHN-182 | J1/W6 | M | B | provide a split-pane quote workspace (IB-05) with describe-driven data… | PFC-222 |
| CHN-183 | J1/W6 | M | B | let producers create, copy and compare quote versions side… | POL-150, POL-151 |
| CHN-184 | J1/W6 | M | B | show underwriting issues to producers with intermediary texts, blocking… | UW-084, UW-066 |
| CHN-185 | J1/W6 | M | B | let producers request approval of one or more issues… | UW-085, WRK-260 |
| CHN-187 | J1/W6 | M | B | show the producer the IDD steps of an intermediated… | DOC-218, PTY-196 |
| CHN-188 | J1/W6 | M | B | let producers bind within their producer code's bind authority… | POL-174, DOC-223 |
| CHN-189 | J1/W6 | M | B | let producers withdraw quotes and mark them not taken… | POL-073, POL-155 |
| CHN-190 | J1/W6 | M | B | let producers service policies in their book: policy changes… | POL-001, POL-008 |
| CHN-191 | J1/W6 | M | B | provide a book-of-business view filtered to the user's producer… | POL-014, PLT-072 |
| CHN-192 | J1/W6 | M | B | show renewals due, renewal offers, non-renewals and lapses for… | POL-009, RAT-236 |
| CHN-193 | J1/W6 | M | B | provide producer views of billing accounts, payment plans, invoices,… | BIL-001, BIL-237 |
| CHN-194 | J1/W6 | M | B | show commission statements, calculations and payments for the user's… | BIL-247, BIL-268 |
| CHN-197 | J1/W6 | M | B | provide documents for objects in the producer's scope (DOC… | DOC-008, DOC-245 |
| CHN-198 | J1/W6 | S | B | let producers upload documents to jobs, policies, claims and… | WRK-260 |
| CHN-199 | J1/W6 | M | B | let producers report claims for customers in their book… | CLM-001, CLM-009 |
| CHN-200 | J1/W6 | M | B | let the agency administrator invite users, suspend and reactivate… | PTY-244, PTY-246, PLT-073 |
| CHN-201 | J1/W6 | M | B | let the administrator grant and revoke producer-code access per… | PTY-245, PTY-252 |
| CHN-202 | J1/W6 | M | B | when a producer is removed, require the administrator to | PTY-247, PTY-009, PLT-004 |
| CHN-206 | J1/W6 | M | B | accept bank-staff users federated from the partner bank's identity… | PLT-057, PTY-250 |
| CHN-207 | J1/W6 | M | B | offer bank channels only the products, offerings and transactions… | PTY-253, PFC-011 |
| CHN-209 | J1/W6 | M | B | show and deliver the IDD disclosures for credit-institution agents… | PTY-257, PTY-196 |
| CHN-214 | J1/W6 | S | B | attribute every bank-channel transaction to partnership, branch and employee… | — |
| CHN-215 | J3/W4 | S | B | publish an API catalogue of API products (section 9.1.2),… | PLT-265 |
| CHN-216 | J3/W4 | M | B | expose external resources for product description, quote (quick, full,… | POL-001, CLM-001, DOC-008 |
| CHN-218 | J3/W4 | S | B | require Idempotency-Key on every external command, keep key →… | POL-001 |
| CHN-219 | J3/W4 | M | B | offer dryRun on every external command that changes money,… | RAT-042, UW-058 |
| CHN-220 | J3/W4 | S | B | return precondition failures as typed RFC 9457 errors with… | POL-004 |
| CHN-221 | J3/W4 | S | B | paginate list operations by cursor with limit ≤ 200… | POL-002 |
| CHN-222 | J3/W4 | M | B | version external APIs by major version in the path,… | — |
| CHN-223 | J3/W4 | M | B | onboard partners through the developer portal: organisation registration, sandbox… | PLT-058, PLT-004 |
| CHN-224 | J3/W4 | M | B | register partner clients in PLT with private-key JWT or… | PLT-058 |
| CHN-225 | J3/W4 | S | B | enforce per-client and per-API-product rate limits (requests per second,… | PLT-293 |
| CHN-227 | J3/W4 | S | B | scope partner data access by organisation: an intermediary partner… | PLT-072 |
| CHN-228 | J3/W4 | S | B | validate every external request against the published schema at… | PLT-293 |
| CHN-230 | J3/W4 | S | B | record every external call (client, operation, result code, latency,… | PLT-013 |
| CHN-232 | J3/W4 | S | B | let partner clients subscribe webhooks to event types permitted… | — |
| CHN-233 | J3/W4 | S | B | verify webhook endpoints by a challenge before activation and… | — |
| CHN-234 | J3/W4 | M | B | deliver webhooks at least once with exponential backoff for… | PLT-005 |
| CHN-235 | J3/W4 | S | B | carry in webhook payloads only identifiers, event type, occurred… | — |
| CHN-237 | J3/W4 | S | B | provide a developer portal with API catalogue, interactive documentation,… | — |
| CHN-238 | J3/W4 | M | B | provide a sandbox per API product on synthetic data… | PLT-332 |
| CHN-240 | J3/W4 | S | B | let the API product manager suspend or revoke a… | PLT-058 |
| CHN-244 | J3/W4 | S | B | allow partners to upload documents through the API with… | WRK-260 |
| CHN-245 | J3/W4 | S | B | record, for each partner organisation, whether it provides ICT… | PLT-012 |
| CHN-260 | J3/W8 | S | B | run an MCP server per legal-entity stamp over the… | — |
| CHN-261 | J3/W8 | S | B | classify every tool as READ, DRY_RUN or STATE_CHANGING, version… | PLT-225 |
| CHN-262 | J3/W8 | M | B | act as an OAuth 2.1 resource server: publish Protected… | PLT-015 |
| CHN-263 | J3/W8 | M | B | never pass the received token to owner APIs; it… | PLT-060 |
| CHN-264 | J3/W8 | L | B | register agent clients by kind — FIRST_PARTY (insurer assistants,… | PLT-058, PLT-217 |
| CHN-265 | J3/W8 | M | B | request step-up of scopes through the insufficient_scope challenge (403… | PLT-015 |
| CHN-266 | J3/W8 | S | B | show each person the agents they have authorised, their… | PLT-069 |
| CHN-267 | J3/W8 | S | B | evaluate the permission matrix with channel AI_AGENT and the… | — |
| CHN-268 | J3/W8 | M | B | execute STATE_CHANGING tools in dry-run first and return the… | PLT-231, POL-001 |
| CHN-269 | J3/W8 | M | B | require a named human approval of the confirmation ticket… | PLT-053, PLT-002 |
| CHN-270 | J3/W8 | M | B | expire confirmation tickets after a configurable time (default 30… | POL-001 |
| CHN-271 | J3/W8 | L | B | log every tool call in AiAgentActionLog: agent client id,… | PLT-002, PLT-223 |
| CHN-272 | J3/W8 | S | B | write an AuditEvent for every executed owner command initiated… | PLT-002 |
| CHN-273 | J3/W8 | S | B | apply per-agent-client and per-delegating-person rate limits (calls per minute,… | — |
| CHN-274 | J3/W8 | M | B | screen tool inputs and outputs through the PLT safety… | PLT-230 |
| CHN-275 | J3/W8 | M | B | pass actorType = AI_AGENT and the agent client id… | UW-001, UW-120 |
| CHN-276 | J3/W8 | S | B | return to agents only customer-safe or intermediary-safe texts (the… | PTY-183 |
| CHN-277 | J3/W8 | M | B | honour the PLT AI kill switch and per-feature toggles… | PLT-219, PLT-220 |
| CHN-279 | J3/W8 | S | B | provide an agent-governance view for the security officer and… | PLT-130 |
| CHN-281 | J3/W8 | S | B | present to the delegating person, in the portal, a… | — |
| CHN-282 | J3/W8 | S | B | run agent adversarial test suites (tool misuse, scope escalation,… | PLT-265 |
| CHN-283 | J3/W8 | S | B | inform users that they are interacting with an AI… | — |
| CHN-284 | J3/W8 | S | B | offer a hand-off to a human at any time… | WRK-211 |
| CHN-285 | J3/W8 | M | B | restrict the assistant to explaining, navigating and preparing actions… | — |
| CHN-286 | J3/W8 | S | B | label AI-generated text shown to customers as AI-generated and… | — |
| CHN-287 | J3/W8 | S | B | keep every journey fully usable without the assistant: when… | PLT-219 |
| CHN-289 | J3/W8 | M | B | register each CHN AI feature and the agent facade… | CMP-007, PLT-233 |
| CHN-290 | J3/W8 | S | B | load no non-essential cookie, local storage item or tracker… | PTY-150 |
| CHN-291 | J3/W8 | M | B | store tracker consent per purpose (analytics, personalisation, marketing) with… | MKT-111, PTY-150 |
| CHN-292 | J3/W8 | M | B | run first-party, privacy-preserving journey analytics (step funnels, errors, abandonment)… | DAT-001, DAT-038 |
| CHN-293 | J3/W8 | S | B | host analytics, tag management and session stores in the… | PLT-265 |
| CHN-294 | J3/W8 | S | E | exclude statutory and sensitive pages (withdrawal, complaints, data-subject requests,… | — |
| CHN-295 | J3/W4 | M | B | meet WCAG 2.2 AA in every customer, intermediary, bank… | — |
| CHN-296 | J3/W4 | S | B | publish an accessibility statement per front end (conformance status,… | WRK-211 |
| CHN-297 | J3/W4 | S | B | offer accessible alternatives for documents (HTML view of policy… | DOC-290 |
| CHN-298 | J3/W4 | S | B | provide responsive layouts down to the WCAG reflow width… | — |
| CHN-303 | J3/W6 | S | B | classify every CHN-owned attribute (P0–P3) and retention class (section… | PLT-011 |
| CHN-304 | J3/W6 | M | B | implement export, restriction and erasure operations for CHN-held personal… | CMP-005 |
| CHN-305 | J3/W6 | S | B | minimise personal data in CHN stores: no copies of… | — |
| CHN-306 | J3/W6 | S | B | refresh CHN read models from owner events within 10… | PLT-005 |
| CHN-309 | J3/W6 | S | B | expose channel health indicators (IB-32 binary status at a… | PLT-013 |
| CHN-310 | J3/W6 | S | B | deploy front ends and the channel back-end independently of… | PLT-008 |
| CHN-311 | J3/W6 | M | B | protect public journeys against automated abuse (quote scraping, credential… | PLT-293 |
| CHN-312 | J3/W6 | S | B | apply content security policies, sub-resource integrity, secure cookies, anti-CSRF… | — |
| CHN-314 | J3/W6 | S | B | send push notifications only to devices registered by the… | PLT-069 |
| CHN-316 | J3/W6 | L | B | issue a durable-medium presentation receipt when the pre-contractual pack… | DOC-218, DOC-220, POL-172 |
| CHN-317 | J3/W6 | L | B | own the Channel code list held in MKT (REQ-MKT-001)… | MKT-001, PFC-011 |
| CHN-318 | J3/W6 | M | B | enforce the agent-client data-release rule (BR-CHN-057): before granting a… | PLT-285, PLT-012, PLT-060 |

### 1.8 WRK (231 rows in range)

J/W mix: J11/W2×231

| ID | J/W | Sz | Tag | Title (first words) | Deps |
|---|---|---|---|---|---|
| WRK-001 | J11/W2 | XL | B | provide the Activity API wrk.Activity.* to create an activity… | PLT-002, PLT-005 |
| WRK-002 | J11/W2 | XL | B | maintain an activity pattern catalogue: versioned, effective-dated patterns per… | MKT-001, MKT-005, DOC-002 |
| WRK-003 | J11/W2 | XL | B | own groups, queues and assignment rules: hierarchical groups of… | PLT-001, PLT-007 |
| WRK-004 | J11/W2 | XL | B | provide the Notes API wrk.Note.* to create, read, edit… | PLT-001, PLT-002 |
| WRK-005 | J11/W2 | XL | B | provide inbound document intake wrk.InboundDocument.*: receive files from any… | DOC-004, DOC-008, CHN-001 |
| WRK-006 | J11/W2 | XL | B | provide global search, recent items and the command-palette back-end:… | PTY-001, POL-014, PLT-001, MKT-005 |
| WRK-007 | J11/W2 | XL | B | deliver staff notifications in-app and by email for subscribed… | PLT-006, CHN-009 |
| WRK-008 | J11/W2 | XL | B | manage back-office requests from intermediaries and customers: typed requests… | CHN-005, CHN-002, PLT-014 |
| WRK-009 | J11/W2 | XL | B | apply SLA policies and escalation: per pattern, request type,… | PLT-009, PLT-007 |
| WRK-010 | J11/W2 | XL | B | manage participants: user-to-object assignments by participant role (for example… | PLT-001, PTY-004, POL-002 |
| WRK-030 | J11/W2 | S | B | identify each pattern by a unique code per legal… | — |
| WRK-031 | J11/W2 | S | E | version patterns: edits create a Draft version, activation makes… | MKT-001 |
| WRK-032 | J11/W2 | S | E | let an administrator scope a pattern by jurisdiction, legal… | PFC-001 |
| WRK-033 | J11/W2 | S | B | support activity classes Task (work to complete) and Event… | — |
| WRK-034 | J11/W2 | M | B | classify patterns by category from a configurable list including… | — |
| WRK-036 | J11/W2 | S | B | hold a default priority (Urgent, High, Normal, Low) on… | PLT-001 |
| WRK-037 | J11/W2 | S | B | mark a pattern as Mandatory so its activities cannot… | POL-003 |
| WRK-038 | J11/W2 | S | B | mark a pattern as Recurring with a recurrence rule… | — |
| WRK-039 | J11/W2 | S | B | mark a pattern as Automated only so it can… | — |
| WRK-040 | J11/W2 | M | B | define target (due) and escalation rules on patterns as… | — |
| WRK-041 | J11/W2 | S | E | let a pattern declare required outcomes (a closed list… | — |
| WRK-043 | J11/W2 | S | E | let a pattern declare auto-close events (for example ClockMet,… | — |
| WRK-044 | J11/W2 | S | E | validate patterns on activation: Greek and English subject present,… | DOC-002 |
| WRK-045 | J11/W2 | S | E | prevent deletion of a pattern that has ever been… | — |
| WRK-050 | J11/W2 | S | B | implement the canonical activity states Open, Completed, Skipped and… | — |
| WRK-051 | J11/W2 | S | B | keep assignment state (Unassigned, Queued, Assigned) separate from lifecycle… | — |
| WRK-052 | J11/W2 | S | B | issue an activity number from the PLT numbering service… | PLT-014 |
| WRK-053 | J11/W2 | S | B | link each activity to exactly one primary business object… | — |
| WRK-054 | J11/W2 | S | E | make an activity visible only to users who may… | PLT-001 |
| WRK-055 | J11/W2 | S | B | store subject, description, priority, mandatory flag, recurring flag, target… | — |
| WRK-056 | J11/W2 | S | B | let a permitted user edit subject, description, priority, target… | PLT-002 |
| WRK-057 | J11/W2 | S | B | complete an activity when the assignee (or a user… | PLT-005 |
| WRK-058 | J11/W2 | S | B | skip a non-mandatory activity with a skip reason, publishing… | — |
| WRK-059 | J11/W2 | S | E | cancel an activity only by the raising module, a… | — |
| WRK-062 | J11/W2 | S | B | show an overdue indicator when the target date has… | — |
| WRK-063 | J11/W2 | S | B | let a user add a note while creating, working… | — |
| WRK-064 | J11/W2 | S | B | maintain an activity history (state changes, assignments, date changes,… | PLT-002 |
| WRK-065 | J11/W2 | S | E | lock an activity optimistically using record_version and reject stale… | — |
| WRK-066 | J11/W2 | S | E | allow an activity to be put into Waiting with… | — |
| WRK-068 | J11/W2 | S | B | expose wrk.Activity.list filtered by assignee, group, queue, object, pattern,… | — |
| WRK-069 | J11/W2 | M | E | provide wrk.Activity.blockingStatus(object) returning open mandatory activities that the owning… | POL-003 |
| WRK-070 | J11/W2 | M | E | re-point activity links when PTY merges parties or accounts… | PTY-007, PTY-004 |
| WRK-075 | J11/W2 | S | B | show on every object a workplan (all activities linked… | — |
| WRK-080 | J11/W2 | S | B | compute business-day dates using working calendars that combine PLT… | PLT-009 |
| WRK-082 | J11/W2 | S | E | store on each activity the calendar id and version… | — |
| WRK-084 | J11/W2 | S | B | define SLA policies as data: scope (pattern, category, request… | MKT-001 |
| WRK-085 | J11/W2 | S | E | pause the SLA clock while an activity is Waiting… | — |
| WRK-086 | J11/W2 | S | E | never pause or move a deadline mirrored from a… | CMP-003 |
| WRK-087 | J11/W2 | S | B | raise an SLA warning when elapsed time crosses the… | — |
| WRK-088 | J11/W2 | S | B | escalate at the escalation date by applying the pattern's… | — |
| WRK-089 | J11/W2 | S | B | publish SLABreached when an activity or request closes after… | PLT-005 |
| WRK-090 | J11/W2 | S | E | run escalation and SLA sweeps as workflow-engine timers that… | PLT-007 |
| WRK-100 | J11/W2 | S | B | maintain groups with code, name (GR/EN), type, parent group,… | — |
| WRK-101 | J11/W2 | S | B | configure group types as a code list (for example… | — |
| WRK-102 | J11/W2 | S | B | manage memberships with user, member flag, manager flag, load… | PLT-001 |
| WRK-104 | J11/W2 | S | E | reject a membership for a user who is inactive… | PLT-001 |
| WRK-105 | J11/W2 | S | B | search groups by name, type, available producer code, organisation… | — |
| WRK-106 | J11/W2 | S | B | associate producer codes with groups so producer-based routing and… | PTY-008 |
| WRK-109 | J11/W2 | S | E | maintain user skills (code list such as motor material… | — |
| WRK-110 | J11/W2 | M | B | maintain queues with code, name (GR/EN), owning group, mode… | — |
| WRK-111 | J11/W2 | S | E | let a member see live counts per queue (open,… | — |
| WRK-112 | J11/W2 | S | E | prevent deleting a group or queue that has open… | — |
| WRK-113 | J11/W2 | S | B | search users to add to a group by username,… | PLT-001 |
| WRK-120 | J11/W2 | M | B | evaluate assignment rule sets as ordered decision tables whose… | PLT-007 |
| WRK-121 | J11/W2 | S | B | support candidate selection outcomes: named user, participant role on… | — |
| WRK-122 | J11/W2 | S | B | support group strategies: round robin, least loaded (open items… | — |
| WRK-123 | J11/W2 | S | B | enforce workload caps per user (maximum open items overall… | — |
| WRK-124 | J11/W2 | S | B | exclude absent users (from out-of-office or PLT status) and… | — |
| WRK-125 | J11/W2 | S | B | support push mode, where the system assigns queue items… | — |
| WRK-126 | J11/W2 | S | B | support pull mode with "Get next", which gives the… | — |
| WRK-127 | J11/W2 | S | B | make claiming atomic so that two members can never… | — |
| WRK-128 | J11/W2 | S | B | let a member claim a specific item from a… | — |
| WRK-129 | J11/W2 | S | B | reassign one activity to a user, group or queue… | — |
| WRK-131 | J11/W2 | S | E | offer assignment dry-run wrk.Assignment.simulate returning the selected target, the… | — |
| WRK-132 | J11/W2 | S | E | bulk reassign up to 50,000 activities in one asynchronous… | PLT-007 |
| WRK-134 | J11/W2 | M | E | respect segregation-of-duties rules from PLT so that an approval… | PLT-001, PLT-004 |
| WRK-135 | J11/W2 | S | E | version assignment rule sets with Draft, PendingApproval, Active and… | PLT-004 |
| WRK-139 | J11/W2 | S | B | record for each assignment the method (rule, manual, pull,… | PLT-002 |
| WRK-140 | J11/W2 | M | E | on PLT UserDeprovisioned, unassign the user's open activities and | PLT-001 |
| WRK-141 | J11/W2 | S | B | list unassigned items for supervisors with Priority, Subject, object… | — |
| WRK-150 | J11/W2 | S | B | let a user, or a manager for a user,… | — |
| WRK-151 | J11/W2 | S | E | optionally move a user's open items to the cover… | — |
| WRK-152 | J11/W2 | S | B | let a user delegate visibility and action on their… | PLT-003 |
| WRK-153 | J11/W2 | S | E | record delegation of authority only through the PLT authority… | PLT-003 |
| WRK-158 | J11/W2 | S | B | end delegations and absences automatically at their end time… | — |
| WRK-165 | J11/W2 | S | B | provide a team view of open activities for the… | — |
| WRK-166 | J11/W2 | S | B | let supervisors drill from a count to the item… | — |
| WRK-167 | J11/W2 | S | B | export any work list (activities, queue items, requests, inbound… | PLT-002 |
| WRK-168 | J11/W2 | S | B | provide an SLA dashboard with on-time rate, breached count,… | — |
| WRK-171 | J11/W2 | S | E | let a supervisor move items between queues in bulk… | — |
| WRK-173 | J11/W2 | S | B | show each team member's current open count, overdue count,… | — |
| WRK-176 | J11/W2 | S | E | limit individual-level productivity metrics to counts and ages of… | — |
| WRK-177 | J11/W2 | S | B | show the ageing of unassigned queue items with the… | — |
| WRK-185 | J11/W2 | S | B | accept activity creation from any module through wrk.Activity.create with… | — |
| WRK-186 | J11/W2 | S | E | de-duplicate system-generated activities by (pattern, primary object, cause key)… | — |
| WRK-187 | J11/W2 | M | E | maintain an event-trigger table (event type, condition on payload,… | PLT-005, PLT-007 |
| WRK-188 | J11/W2 | S | E | version trigger rows, test them against recorded events in… | PLT-004 |
| WRK-189 | J11/W2 | S | E | ship the initial motor catalogue of system-generated activities listed… | — |
| WRK-190 | J11/W2 | S | B | raise the "contact changed" activity when PTY reports a… | PTY-001 |
| WRK-191 | J11/W2 | M | E | raise an activity when POL reports an out-of-sequence reversal… | POL-006, CLM-002 |
| WRK-192 | J11/W2 | M | B | create an activity on ClockWarned for each clock code… | CMP-003, CMP-004 |
| WRK-193 | J11/W2 | M | B | close clock activities from CMP clock events according to… | CMP-003 |
| WRK-194 | J11/W2 | S | E | raise an operational alert to PLT incident management when… | PLT-013 |
| WRK-195 | J11/W2 | S | E | auto-close activities on the events declared on their pattern… | — |
| WRK-196 | J11/W2 | S | B | let an owning module close or update its own… | UW-006 |
| WRK-197 | J11/W2 | S | E | park trigger failures (unknown object, missing pattern) in a… | PLT-005 |
| WRK-198 | J11/W2 | M | E | raise activities from MigrationBatchReconciled breaks and ReconciliationBreakRaised to the… | FIN-007, MIG-005 |
| WRK-200 | J11/W2 | M | B | route approval activities (underwriting referrals, claim payments and reserves,… | PLT-003 |
| WRK-201 | J11/W2 | S | B | follow the "refer-to" answer of plt.Authority.check to route to… | PLT-003 |
| WRK-202 | J11/W2 | M | B | never record the approval decision itself; the approver acts… | CLM-003, BIL-007 |
| WRK-203 | J11/W2 | S | E | re-check authority at claim time and at completion, because… | PLT-003 |
| WRK-204 | J11/W2 | S | B | exclude the maker and anyone barred by SoD from… | PLT-004 |
| WRK-206 | J11/W2 | M | E | integrate with the PLT maker-checker service so that every… | PLT-004 |
| WRK-210 | J11/W2 | M | B | define request types as data: code, names (GR/EN), allowed… | MKT-001 |
| WRK-211 | J11/W2 | M | B | accept requests through wrk.Request.submit called by CHN for portal… | CHN-005, CHN-004 |
| WRK-212 | J11/W2 | S | B | check the requester's right to request for the object… | PTY-009 |
| WRK-214 | J11/W2 | S | B | route each request as an activity per the request… | — |
| WRK-215 | J11/W2 | S | B | let staff reply to the requester with text and… | CHN-009 |
| WRK-216 | J11/W2 | S | B | let staff ask the requester for information, setting status… | — |
| WRK-217 | J11/W2 | S | B | keep the request conversation as an ordered thread of… | — |
| WRK-219 | J11/W2 | S | E | let staff convert a request that expresses dissatisfaction into… | CMP-004 |
| WRK-220 | J11/W2 | S | B | show requesters the request status, SLA target and replies… | CHN-005 |
| WRK-221 | J11/W2 | S | B | link attachments of requests as inbound documents through intake… | — |
| WRK-222 | J11/W2 | S | B | let staff create a request on behalf of a… | — |
| WRK-230 | J11/W2 | M | B | store notes with topic (required), subject (optional), text (required,… | — |
| WRK-231 | J11/W2 | S | B | let the user choose the related object from the… | POL-002 |
| WRK-232 | J11/W2 | S | B | configure note topics as a code list (General, Underwriting,… | — |
| WRK-234 | J11/W2 | S | B | let the user save or cancel a note being… | — |
| WRK-236 | J11/W2 | S | B | support confidentiality levels General, Sensitive, Legal and Special-category health,… | PLT-001 |
| WRK-237 | J11/W2 | S | B | let the author edit a note's text, subject, topic… | — |
| WRK-238 | J11/W2 | S | E | block edits after the edit window except by users… | — |
| WRK-239 | J11/W2 | S | E | show the edit history of a note with each… | — |
| WRK-240 | J11/W2 | S | B | allow deletion of a note only by its author… | — |
| WRK-241 | J11/W2 | M | B | search notes by full text (Greek and English, accent-insensitive,… | MKT-005 |
| WRK-242 | J11/W2 | S | B | list notes for an object showing for each note… | — |
| WRK-246 | J11/W2 | M | E | treat Special-category health notes as P3: field-level encryption, no… | PLT-010, PLT-011 |
| WRK-248 | J11/W2 | S | B | provide wrk.Note.export(object or party) returning all notes and versions… | CMP-005 |
| WRK-251 | J11/W2 | S | B | keep a "View notes" link from activities to the… | — |
| WRK-252 | J11/W2 | S | B | accept notes from migration with original author name, original… | MIG-001 |
| WRK-260 | J11/W2 | M | B | accept inbound files through wrk.InboundDocument.submit from CHN portal and… | PLT-006, CHN-001 |
| WRK-261 | J11/W2 | S | B | monitor configured mailboxes through the PLT integration hub, ingest… | PLT-006 |
| WRK-263 | J11/W2 | S | B | scan every file for malware before any further processing,… | PLT-161 |
| WRK-264 | J11/W2 | S | B | reject files above the configured size, of disallowed types,… | — |
| WRK-265 | J11/W2 | S | B | store each clean binary through the DOC archive with… | DOC-004 |
| WRK-267 | J11/W2 | S | B | run text recognition on images and scanned PDFs in… | — |
| WRK-268 | J11/W2 | L | B | classify each document into the document-class catalogue (for example… | — |
| WRK-270 | J11/W2 | S | B | detect multi-document files and propose split points with a… | — |
| WRK-271 | J11/W2 | S | B | detect exact duplicates by hash and near-duplicates by recognised-text… | — |
| WRK-272 | J11/W2 | M | B | suggest links to accounts, parties, policies, jobs, claims, billing… | POL-014, PTY-001, CLM-009 |
| WRK-273 | J11/W2 | S | B | extract key fields per document class into typed extraction… | — |
| WRK-274 | J11/W2 | S | E | extract machine-readable data deterministically where available (MRZ on identity… | — |
| WRK-275 | J11/W2 | S | B | apply confidence thresholds per document class and per field;… | — |
| WRK-276 | J11/W2 | S | B | continue intake on the rules-only path when AI is… | PLT-010 |
| WRK-277 | J11/W2 | S | B | route a verification activity per envelope or per document… | — |
| WRK-278 | J11/W2 | S | B | present the verification workbench with the document viewer and… | — |
| WRK-279 | J11/W2 | S | B | let the verifier accept, edit or reject class, split,… | PLT-010 |
| WRK-280 | J11/W2 | S | B | prevent any extracted value from changing another module's record… | POL-001 |
| WRK-281 | J11/W2 | S | B | mark verification complete only when class is confirmed, at… | — |
| WRK-282 | J11/W2 | S | B | on verification, update the document's retention class in the | DOC-004 |
| WRK-283 | J11/W2 | M | B | support redaction of regions or pages for special-category or… | DOC-004 |
| WRK-284 | J11/W2 | S | B | assign a confidentiality level to each inbound document (default… | — |
| WRK-285 | J11/W2 | S | B | list inbound documents for any object with name, class,… | DOC-008 |
| WRK-286 | J11/W2 | S | B | let a permitted user re-link a verified document to… | — |
| WRK-287 | J11/W2 | S | B | let a verifier reject an inbound document as not… | — |
| WRK-289 | J11/W2 | S | B | notify the owning module of new verified documents on… | — |
| WRK-293 | J11/W2 | S | B | keep inbound-document metadata searchable in global search by number,… | — |
| WRK-294 | J11/W2 | S | B | provide wrk.InboundDocument.export(party or object) for DSAR returning metadata, extraction… | CMP-005 |
| WRK-295 | J11/W2 | S | B | let staff upload documents directly on any object, choosing… | — |
| WRK-298 | J11/W2 | M | B | maintain the document-class catalogue as data with class code,… | MKT-002 |
| WRK-300 | J11/W2 | S | B | store participants per object with participant role, assigned user,… | — |
| WRK-301 | J11/W2 | S | B | configure participant roles per object type (account: creator, account… | — |
| WRK-302 | J11/W2 | S | B | assign participants automatically at creation by rules (same rule… | — |
| WRK-303 | J11/W2 | S | B | let permitted users edit participants (add, change user or… | — |
| WRK-305 | J11/W2 | S | E | include participants in bulk reassignment jobs (leaver, book move). | — |
| WRK-306 | J11/W2 | S | B | expose wrk.Participant.list(object) and wrk.Participant.objectsFor(user, role) for modules and My… | — |
| WRK-307 | J11/W2 | S | E | let participant membership grant visibility of an object only… | PLT-001 |
| WRK-310 | J11/W2 | M | B | provide My Desktop with counters for activities, submissions, change… | POL-001, CLM-009 |
| WRK-311 | J11/W2 | M | B | show on My Desktop the user's activities, submissions, renewals… | POL-001, POL-011 |
| WRK-312 | J11/W2 | S | E | compose My Desktop from widgets permitted for the user's… | — |
| WRK-313 | J11/W2 | S | B | let the user complete, skip and assign activities directly… | — |
| WRK-315 | J11/W2 | M | B | offer "Create" quick actions on My Desktop for New… | POL-001, PTY-004 |
| WRK-325 | J11/W2 | M | E | maintain a search projection SearchDocumentView from owners' events (party,… | PLT-005 |
| WRK-326 | J11/W2 | S | B | normalise queries and indexed text by case folding, removing… | MKT-005 |
| WRK-327 | J11/W2 | S | E | recognise identifier patterns in queries (policy, claim, job, account,… | — |
| WRK-328 | J11/W2 | S | B | filter every search result by ABAC (legal entity, jurisdiction,… | PLT-001 |
| WRK-329 | J11/W2 | M | B | provide a global search results page with facets by… | PTY-001, POL-014 |
| WRK-330 | J11/W2 | S | B | record recent items per user (last 50 objects opened)… | — |
| WRK-331 | J11/W2 | S | E | remove recent items the user can no longer see… | — |
| WRK-332 | J11/W2 | M | B | provide the command-palette back-end wrk.Palette.query returning grouped results (Records,… | — |
| WRK-333 | J11/W2 | S | B | return palette results within 300 ms p95 and search… | — |
| WRK-334 | J11/W2 | S | B | support quick-jump by typed prefix, for example "acc 5617099410",… | — |
| WRK-336 | J11/W2 | S | E | degrade gracefully when the projection is stale or the… | POL-014 |
| WRK-337 | J11/W2 | S | E | log search queries only as pseudonymised usage metrics and… | — |
| WRK-338 | J11/W2 | S | B | let AI agents (SYS-01) use global search only with… | CHN-003 |
| WRK-340 | J11/W2 | S | B | keep the application's "Go to" list of destinations (desktop… | — |
| WRK-345 | J11/W2 | S | B | generate notifications for assignment, reassignment away, SLA warning, escalation,… | — |
| WRK-346 | J11/W2 | M | B | let each user set preferences per notification type: in-app,… | — |
| WRK-347 | J11/W2 | S | B | send staff email notifications through the PLT integration hub… | PLT-006 |
| WRK-349 | J11/W2 | S | B | provide a notification centre with unread count, mark read,… | — |
| WRK-351 | J11/W2 | S | B | expose notification hooks to CHN: for requests, activities externally… | CHN-009 |
| WRK-354 | J11/W2 | S | B | render notifications in the user's language setting. | MKT-005 |
| WRK-360 | J11/W2 | S | B | provide a personal calendar view (day, week, month) of… | — |
| WRK-361 | J11/W2 | S | B | let a user create a personal reminder (diary entry)… | — |
| WRK-366 | J11/W2 | S | B | show a claim-diary view for claims handlers listing diary… | CLM-007 |
| WRK-370 | J11/W2 | M | B | implement the DSAR export API wrk.DataSubject.export(party_id, scope) returning all… | CMP-005, PTY-007 |
| WRK-371 | J11/W2 | S | B | implement erasure and restriction APIs that pseudonymise or restrict… | PLT-011 |
| WRK-372 | J11/W2 | S | E | run a quarterly DSAR completeness test with seeded notes… | — |
| WRK-373 | J11/W2 | S | E | index party references in notes, activities and inbound documents… | — |
| WRK-374 | J11/W2 | S | B | allow the DPO to review flagged items and redact… | — |
| WRK-375 | J11/W2 | S | B | record each DSAR export and erasure action in the… | CMP-005 |
| WRK-376 | J11/W2 | M | B | assign retention classes to activities, notes (by topic and… | PLT-011 |
| WRK-377 | J11/W2 | S | B | respect legal holds placed by PLT on objects and… | PLT-011 |
| WRK-378 | J11/W2 | S | B | purge or anonymise WRK data at retention end, including… | PLT-011 |
| WRK-379 | J11/W2 | S | B | keep declined-submission notes and documents under a distinct retention… | UW-005 |
| WRK-380 | J11/W2 | S | B | log every read of Special-category health and Legal notes… | PLT-002 |
| WRK-385 | J11/W2 | S | B | provide administration screens for patterns, groups, queues, assignment rules,… | MKT-001 |
| WRK-386 | J11/W2 | M | B | provide migration import APIs for open activities, notes, groups,… | MIG-001, MIG-002 |
| WRK-387 | J11/W2 | S | B | keep the legacy reference on migrated items for cross-system… | MIG-002 |
| WRK-389 | J11/W2 | S | B | publish business SLIs (time from receipt to verification, activity… | PLT-013 |
| WRK-390 | J11/W2 | S | B | check capability switches from MKT for WRK features per… | MKT-004 |
| WRK-392 | J11/W2 | S | B | allow the administrator to reprocess dead-lettered triggers and intake… | PLT-005 |
| WRK-394 | J11/W2 | S | E | support keyboard list navigation on every WRK list (J/K… | — |
| WRK-395 | J11/W2 | S | B | provide the Greece and Cyprus pack data for WRK… | MKT-008 |
| WRK-396 | J11/W2 | S | B | run with all AI features off and pass the… | PLT-010 |
| WRK-397 | J11/W2 | M | E | consume PLT ApprovalRequested to create a PLT-APPROVAL activity linked… | PLT-004, PLT-116 |
| WRK-398 | J11/W2 | M | E | consume PLT UserAccessChanged and AuthorityGrantChanged and re-evaluate the eligibility… | PLT-001, PLT-003 |
| WRK-400 | J11/W2 | M | E | consume MIG CoexistenceMasterChanged and update the master-system marker of… | MIG-004, MIG-148 |
| WRK-401 | J11/W2 | M | E | use an external document-AI, OCR or scanning provider only… | PLT-012, PLT-010 |
| WRK-402 | J11/W2 | M | E | carry in ActivityCreated, ActivityCompleted, ActivitySkipped and ActivityCancelled the fields… | POL-003 |
| WRK-403 | J11/W2 | M | E | use the shared MKT code lists of ruling R-84… | MKT-001, DOC-001 |
| WRK-404 | J11/W2 | M | E | preserve, for every inbound signed document, the electronic signature… | DOC-004, DOC-006 |

### 1.9 DAT (212 rows in range)

J/W mix: J12/W8×208, J13/W9×4

| ID | J/W | Sz | Tag | Title (first words) | Deps |
|---|---|---|---|---|---|
| DAT-001 | J12/W8 | XL | B | ingest every event published on the producing modules' topics… | PLT-005, PLT-140 |
| DAT-002 | J12/W8 | XL | B | model every silver and gold entity bitemporally with valid… | POL-002, POL-094 |
| DAT-003 | J12/W8 | XL | B | build regulatory marts — Solvency II quantitative reporting inputs,… | FIN-155, PFC-005, MKT-007, CMP-008 |
| DAT-004 | J12/W8 | XL | B | supply actuaries with reconciled reserving and IFRS 17 measurement… | FIN-009, FIN-273, FIN-274, FIN-275, FIN-276 … |
| DAT-005 | J12/W8 | XL | B | provide the programme's model registry and monitoring service: register… | PLT-010, PLT-223, PLT-227, PLT-234, CMP-007 |
| DAT-006 | J12/W8 | XL | B | provide a feature store with offline (point-in-time correct) and… | — |
| DAT-007 | J12/W8 | XL | B | run data-quality rules and scorecards per data product, reconcile… | PLT-002, WRK-001 |
| DAT-009 | J12/W8 | XL | B | provide lakehouse data-subject operations callable by CMP's DSAR orchestration… | CMP-005, CMP-154, CMP-155, CMP-157, CMP-158 … |
| DAT-030 | J12/W8 | M | B | hold a data-contract registry where each contract has id… | PLT-005 |
| DAT-031 | J12/W8 | S | B | version contracts semantically: additive changes create a minor version;… | PLT-005 |
| DAT-032 | J12/W8 | S | B | validate each record against its contract (schema, required fields,… | — |
| DAT-033 | J12/W8 | S | B | store quarantined records with reason code, contract version, first-seen… | — |
| DAT-034 | J12/W8 | S | B | release quarantined records after a contract correction or producer… | — |
| DAT-036 | J12/W8 | S | B | consume idempotently on event_id and preserve per-aggregate ordering using… | PLT-005 |
| DAT-037 | J12/W8 | S | B | repair detected sequence gaps by requesting replay of the… | PLT-145 |
| DAT-038 | J12/W8 | M | B | ingest channel journey analytics (REQ-CHN-292) only in aggregated or… | CHN-292, PTY-005 |
| DAT-039 | J12/W8 | S | B | support full and partial replay per contract from the… | PLT-145 |
| DAT-040 | J12/W8 | S | B | handle late events (recorded_at older than the latest processed… | — |
| DAT-041 | J12/W8 | S | B | order out-of-order events within an aggregate by envelope sequence,… | — |
| DAT-042 | J12/W8 | M | B | apply POL reverse-and-reapply as a correction set keyed by… | POL-005, POL-006 |
| DAT-043 | J12/W8 | S | B | hold events of an incomplete correction set in a… | — |
| DAT-044 | J12/W8 | S | B | apply the PTY party merge and unmerge re-point maps… | PTY-007 |
| DAT-045 | J12/W8 | S | B | treat events with origin=MIGRATION exactly like live events and… | MIG-001 |
| DAT-046 | J12/W8 | S | B | ingest declared data products by pull (cursor-based APIs such… | POL-094 |
| DAT-048 | J12/W8 | S | E | run producer-side contract tests in the producing module's build… | PLT-265 |
| DAT-049 | J12/W8 | S | B | carry the envelope fields (correlation_id, causation_id, configuration_hash, ai_interaction_id, actor)… | PLT-223 |
| DAT-050 | J12/W8 | S | B | capture row-level changes from each module's database through a… | PLT-005 |
| DAT-051 | J12/W8 | M | B | reconcile daily, per module and entity, row counts and… | — |
| DAT-052 | J12/W8 | S | B | identify keys present in the CDC zone but missing… | — |
| DAT-053 | J12/W8 | S | B | repair a CDC break by requesting event replay for… | — |
| DAT-054 | J12/W8 | S | B | escalate a break unresolved after 2 business days to… | PLT-012 |
| DAT-055 | J12/W8 | S | B | exclude P2 and P3 columns from the CDC zone… | PTY-168 |
| DAT-056 | J12/W8 | S | B | retain CDC zone data for 35 days only. | PLT-237 |
| DAT-057 | J12/W8 | S | B | report reconciliation results per module and day on the… | — |
| DAT-058 | J12/W8 | S | B | ingest PLT reference data (calendars, holidays, currencies, FX rates… | PLT-009 |
| DAT-059 | J12/W8 | S | B | ingest MKT regime code lists and crosswalks on RegimeCodeListPublished… | MKT-208 |
| DAT-060 | J12/W8 | S | B | ingest product catalogue and coverage-grain regulatory mappings per product… | PFC-005 |
| DAT-061 | J12/W8 | S | B | ingest configuration activations (ConfigurationActivated, PackActivated, PackRolledBack, RateTableChanged) to maintain… | MKT-001 |
| DAT-066 | J12/W8 | S | B | ingest group reference data from the MKT group pack… | MKT-128 |
| DAT-067 | J12/W8 | M | B | store analytical data as open-format columnar tables with transactional… | — |
| DAT-068 | J12/W8 | S | B | keep all lakehouse storage, compute and backups in EU… | PLT-006 |
| DAT-069 | J12/W8 | S | B | organise bronze as immutable, append-only landing tables per contract… | — |
| DAT-070 | J12/W8 | M | B | encrypt P1–P3 payload values in bronze under per-subject keys… | PLT-240, PLT-241 |
| DAT-071 | J12/W8 | M | B | build the silver conformed entities listed in section 7.1.2… | — |
| DAT-072 | J12/W8 | S | B | use the producing module's internal UUIDv7 identifiers as silver… | — |
| DAT-073 | J12/W8 | S | B | build gold marts per domain: regulatory, actuarial, finance, pricing,… | — |
| DAT-074 | J12/W8 | S | B | partition silver and gold tables by legal entity and… | — |
| DAT-075 | J12/W8 | M | B | apply retention per layer and class: bronze for the… | PLT-237 |
| DAT-076 | J12/W8 | S | B | evolve silver and gold schemas through versioned transformation code… | PLT-265 |
| DAT-077 | J12/W8 | M | B | run transformations as declarative, versioned pipelines on the PLT… | PLT-007, PLT-330 |
| DAT-078 | J12/W8 | S | B | maintain near-real-time incremental builds for operational marts (event to… | — |
| DAT-079 | J12/W8 | S | B | compact small files, maintain table statistics and expire unreferenced… | — |
| DAT-080 | J12/W8 | S | B | set recorded_from from the envelope recorded_at of the event… | — |
| DAT-081 | J12/W8 | S | B | guarantee that a query "as known at" record time… | — |
| DAT-082 | J12/W8 | S | B | provide parameterised analytical views asAt(validDate), asKnownAt(recordTime) and asAtAsKnownAt(validDate, recordTime)… | — |
| DAT-083 | J12/W8 | S | B | build period snapshots (month-end and quarter-end in-force, open claims,… | FIN-007 |
| DAT-084 | J12/W8 | M | B | freeze, for every signed-off mart run, an "as reported"… | — |
| DAT-085 | J12/W8 | S | B | compute, for a resubmission, the difference between the "as… | — |
| DAT-087 | J12/W8 | S | B | serve bitemporal point-in-time populations (for example in-force terms as… | RAT-211 |
| DAT-088 | J12/W8 | S | B | attach to every bitemporal query result the valid date,… | — |
| DAT-089 | J12/W8 | S | B | model claim and reserve histories so that development by… | — |
| DAT-090 | J12/W8 | S | B | model finance journal lines from JournalPosted and FIN data… | FIN-002 |
| DAT-091 | J12/W8 | S | E | run property-based tests asserting bitemporal invariants (no overlapping versions… | — |
| DAT-092 | J12/W8 | M | B | hold taxonomy transforms as versioned data per regime and… | MKT-205 |
| DAT-093 | J12/W8 | S | B | resolve each fact's regime code from the PFC mapping… | PFC-005 |
| DAT-094 | J12/W8 | S | B | convert codes between taxonomy versions using MKT crosswalks (one-to-one,… | MKT-207 |
| DAT-095 | J12/W8 | S | B | run two taxonomy versions of a regime in parallel… | MKT-209 |
| DAT-096 | J12/W8 | S | B | block a mart run when any in-scope fact lacks… | MKT-210 |
| DAT-097 | J12/W8 | M | B | own the treaty-type → reinsurance line mapping (proportional, non-proportional… | RI-228 |
| DAT-098 | J12/W8 | S | B | never map ceded business to accepted-reinsurance lines; ceded amounts… | RI-228 |
| DAT-099 | J12/W8 | S | B | test each transform version against golden fixtures and the… | — |
| DAT-100 | J12/W8 | S | B | activate a transform version only through maker-checker by a… | PLT-004 |
| DAT-104 | J12/W8 | S | B | build Solvency II quantitative reporting inputs per entity, reference… | FIN-007 |
| DAT-105 | J12/W8 | S | B | compute premiums written, earned, claims incurred, changes in other… | FIN-155 |
| DAT-106 | J12/W8 | S | B | compute premiums, claims and expenses by country of risk… | MKT-226 |
| DAT-107 | J12/W8 | M | B | supply non-life technical-provision inputs per LoB: case reserves, paid… | FIN-273 |
| DAT-108 | J12/W8 | S | B | build non-life claims-development triangles (gross paid, gross RBNS, reinsurance… | — |
| DAT-110 | J12/W8 | S | B | supply counterparty default risk inputs per reinsurer (LEI, rating,… | RI-201 |
| DAT-111 | J12/W8 | S | B | supply premium and reserve volume measures per LoB and… | — |
| DAT-112 | J12/W8 | M | B | reconcile every Solvency II mart run to FIN's reconciled… | FIN-155 |
| DAT-114 | J12/W8 | S | B | let the regulatory analyst record a documented manual adjustment… | PLT-004 |
| DAT-115 | J12/W8 | L | B | require sign-off of a mart run by a finance… | PLT-004, CMP-008, CMP-197, CMP-198 |
| DAT-116 | J12/W8 | S | B | export a signed-off mart run in a template-neutral, machine-readable… | CMP-008 |
| DAT-117 | J12/W8 | S | B | support resubmission by creating a new run version linked… | — |
| DAT-120 | J12/W8 | S | B | build statistical-return inputs for the national central bank (ECB… | — |
| DAT-121 | J12/W8 | S | B | classify each coverage fact by the national statistical class… | PFC-144 |
| DAT-122 | J12/W8 | S | B | reconcile statistical returns to FIN balances per national statistical… | FIN-155 |
| DAT-123 | J12/W8 | S | B | supply receivables from policyholders and intermediaries by ageing bucket… | BIL-011 |
| DAT-132 | J12/W8 | S | B | restrict the IPT and A.1004 marts to users with… | — |
| DAT-134 | J12/W8 | S | B | aggregate EAEE feeds so that no record identifies a… | — |
| DAT-135 | J12/W8 | M | B | compute Information Centre reporting lag per motor policy event… | CMP-002, POL-315 |
| DAT-136 | J12/W8 | S | B | compute fiscal-document rejection rates by document type, rejection code… | CMP-001 |
| DAT-138 | J12/W8 | M | B | produce Fairfax group feeds in USD (premiums, claims, reserves,… | FIN-006, FIN-210 |
| DAT-139 | J12/W8 | S | B | build group feeds per deployment stamp and let a… | — |
| DAT-140 | J12/W8 | S | B | never recompute USD amounts with rates other than those… | FIN-006 |
| DAT-141 | J12/W8 | S | B | reconcile each group feed to FIN's group reporting pack… | FIN-210 |
| DAT-142 | J12/W8 | S | B | send group feeds through the PLT adapter host with… | PLT-006 |
| DAT-144 | J12/W8 | L | B | ingest CLM's reserving feed (REQ-CLM-228), FIN's actuarial input extract… | CLM-228, FIN-279, FIN-147, RI-227 |
| DAT-145 | J12/W8 | M | B | build paid, incurred, case-reserve and claim-count triangles by accident… | CLM-228 |
| DAT-146 | J12/W8 | S | B | build exposure measures (vehicle-years, policy-years, sums insured, earned premium)… | POL-094 |
| DAT-147 | J12/W8 | S | B | reconcile triangle inputs to FIN's actuarial extract and journals… | FIN-279 |
| DAT-148 | J12/W8 | M | B | build IFRS 17 PAA measurement inputs per group and… | FIN-011, FIN-147 |
| DAT-149 | J12/W8 | S | B | provide a governed actuarial workspace in the lakehouse where… | — |
| DAT-150 | J12/W8 | M | B | assemble actuarial result sets with run id, model id… | FIN-273 |
| DAT-151 | J12/W8 | S | B | call fin.ActuarialResults.validate before submission and show FIN's findings (completeness,… | FIN-009 |
| DAT-152 | J12/W8 | S | B | submit result sets through fin.ActuarialResults.submit with an idempotency key… | FIN-009 |
| DAT-153 | J12/W8 | S | B | require the preparing actuary to attest the run (method,… | FIN-275 |
| DAT-154 | J12/W8 | S | B | consume FIN acknowledgements (accepted, rejected with reasons, posted with… | FIN-278 |
| DAT-155 | J12/W8 | S | B | version result sets so that a corrected set for… | FIN-277 |
| DAT-156 | J12/W8 | S | B | register each reserving or measurement model (for example chain-ladder… | — |
| DAT-159 | J12/W8 | S | B | supply large-loss and catastrophe-loss separation (claims above a threshold,… | CLM-006 |
| DAT-161 | J12/W8 | M | B | build a rating-cell monitoring mart from RatingCalculated, quote, bind… | RAT-249, RAT-251 |
| DAT-162 | J12/W8 | S | B | compute renewal retention by price-change band, tenure, channel and… | RAT-252 |
| DAT-163 | J12/W8 | S | B | serve dat.Population.sample for RAT (in-force sample, renewal window, stratified… | RAT-204 |
| DAT-166 | J12/W8 | S | B | supply product KPIs (in-force, loss ratio, cancellation and withdrawal… | — |
| DAT-167 | J12/W8 | M | B | serve aggregated in-force, renewal and open-quote counts by product… | PFC-174, PFC-207 |
| DAT-168 | J12/W8 | S | B | build an underwriting mart: rule hits, issues, decisions, lanes,… | UW-001 |
| DAT-169 | J12/W8 | S | B | supply historical evaluation samples to UW rule simulation (REQ-UW-046)… | UW-046 |
| DAT-172 | J12/W8 | S | B | not provide competitor-price or price-optimisation (demand-based individual pricing) analytics… | — |
| DAT-173 | J12/W8 | S | B | build a claims operations mart: FNOL volumes by channel,… | CLM-001 |
| DAT-175 | J12/W8 | S | B | ingest SIU outcomes as labelled feedback (REQ-CLM-207) and maintain… | CLM-207 |
| DAT-177 | J12/W8 | S | B | compute FraudScore band distributions, alert rates and dismissal rates… | — |
| DAT-178 | J12/W8 | S | B | build a catastrophe-event claims mart (claims, incurred, paid by… | CLM-006 |
| DAT-190 | J12/W8 | S | B | define features as versioned code with name, entity, description… | — |
| DAT-191 | J12/W8 | S | B | build offline training sets with point-in-time joins on record… | — |
| DAT-192 | J12/W8 | S | B | serve online features for models invoked by modules through… | PLT-217 |
| DAT-193 | J12/W8 | M | B | reject registration of a feature that uses a protected… | — |
| DAT-194 | J12/W8 | S | B | record every model–feature dependency and block retirement of a… | — |
| DAT-195 | J12/W8 | S | B | monitor feature distributions (population stability, missing rates) per model… | — |
| DAT-196 | J12/W8 | S | B | erase or re-tokenise a subject's online and offline feature… | — |
| DAT-197 | J12/W8 | M | B | hold a model registry where each model has id,… | CMP-007 |
| DAT-198 | J12/W8 | M | B | record per model version: training data summary (sources, period,… | — |
| DAT-199 | J12/W8 | S | B | register externally hosted foundation-model versions used through the PLT… | PLT-218 |
| DAT-200 | J12/W8 | S | B | run training pipelines reproducibly (pinned code, data snapshot hash,… | — |
| DAT-201 | J12/W8 | S | B | require independent validation of a model version (validator ≠… | PLT-004 |
| DAT-202 | J12/W8 | S | B | approve a model version for production only through maker-checker… | PLT-004 |
| DAT-203 | J12/W8 | S | B | hand off approved model versions to deployment by publishing… | PLT-223 |
| DAT-204 | J12/W8 | S | B | reject an AiInteractionRecord model id/version that is not Approved… | PLT-223 |
| DAT-206 | J12/W8 | S | B | schedule periodic revalidation per model risk tier (high-risk controls:… | WRK-001 |
| DAT-207 | J12/W8 | S | B | retire model versions with reason, keeping cards, evaluations and… | — |
| DAT-208 | J12/W8 | M | B | export registry and monitoring evidence per AI system for… | CMP-007, CMP-237, CMP-244 |
| DAT-209 | J12/W8 | M | B | maintain the programme AI-feature coverage register (section 11.3) and… | PLT-010, CMP-241 |
| DAT-210 | J12/W8 | M | B | compute, per AI feature and model version, the metrics… | PLT-223, PLT-234 |
| DAT-211 | J12/W8 | S | B | compute outcome-based performance (for example precision at alert threshold,… | WRK-005 |
| DAT-212 | J12/W8 | S | B | compute input and output drift (population stability index, distribution… | — |
| DAT-213 | J12/W8 | S | B | compute override and acceptance trends as a proxy for… | PLT-234 |
| DAT-214 | J12/W8 | S | B | hold monitoring thresholds per feature (metric, warning, breach, minimum… | PLT-004 |
| DAT-215 | J12/W8 | S | B | publish ModelDriftDetected (feature, model version, metric, value, threshold, window,… | PLT-227 |
| DAT-216 | J12/W8 | M | B | run bias tests for every customer-affecting AI feature before… | — |
| DAT-217 | J12/W8 | M | B | estimate proxy group membership where protected attributes are not… | — |
| DAT-218 | J12/W8 | S | B | publish BiasThresholdBreached (feature or artefact, version, proxy group, ratio,… | PLT-227 |
| DAT-219 | J12/W8 | S | B | record remediation (owner, root cause, action, accepted justification by… | WRK-001 |
| DAT-220 | J12/W8 | S | B | create a WRK activity for the feature owner and… | WRK-001 |
| DAT-221 | J12/W8 | S | B | monitor generative features with sampled human quality review (groundedness,… | — |
| DAT-223 | J12/W8 | S | B | never use AI monitoring data to evaluate individual staff;… | WRK-176 |
| DAT-224 | J12/W8 | S | B | provide the monitoring dashboard (SCR-DAT-10) per feature with metrics,… | — |
| DAT-226 | J12/W8 | M | B | hold a KPI catalogue in which every KPI has… | — |
| DAT-227 | J12/W8 | S | B | compute KPIs only through the catalogue definitions, so that… | — |
| DAT-228 | J12/W8 | S | B | provide the executive dashboard (SCR-DAT-04) with hero metrics and… | — |
| DAT-229 | J12/W8 | S | B | provide the motor portfolio dashboard (SCR-DAT-05), the claims operations… | — |
| DAT-231 | J12/W8 | S | B | publish product KPIs to PFC for the POG record… | — |
| DAT-232 | J12/W8 | S | B | apply row-level security on every dashboard and query from… | PLT-001 |
| DAT-233 | J12/W8 | S | B | offer governed self-service: certified data products and a semantic… | — |
| DAT-237 | J12/W8 | S | B | export dashboard data with the KPI definition and as-of… | PLT-002 |
| DAT-238 | J12/W8 | S | B | exclude individual staff productivity measures from all dashboards; operational… | WRK-176 |
| DAT-240 | J12/W8 | S | B | hold data-quality rules per data product (completeness, validity, consistency,… | — |
| DAT-241 | J12/W8 | S | B | evaluate rules on every pipeline run and score each… | — |
| DAT-242 | J12/W8 | S | B | stop promotion of a gold mart build when a… | — |
| DAT-243 | J12/W8 | S | B | reconcile mart totals to source-of-record totals defined per mart… | BIL-011 |
| DAT-245 | J12/W8 | M | B | raise reconciliation breaks and critical rule failures as incidents… | WRK-001, PLT-012 |
| DAT-247 | J12/W8 | S | B | capture column-level lineage automatically from pipeline code for every… | — |
| DAT-248 | J12/W8 | S | B | trace any regulatory figure or KPI value to the… | — |
| DAT-250 | J12/W8 | S | B | record lineage for actuarial result sets from FIN journals… | FIN-276 |
| DAT-251 | J12/W8 | S | B | measure freshness per data product against its contract objective… | — |
| DAT-253 | J12/W8 | S | B | reconcile converted business from MIG batches (MigrationBatchLoaded, MigrationBatchReconciled) to… | MIG-005 |
| DAT-254 | J12/W8 | S | B | keep data-quality and reconciliation results for 10 years as… | — |
| DAT-255 | J12/W8 | S | B | maintain a data catalogue of every data product, table,… | — |
| DAT-256 | J12/W8 | S | B | maintain a business glossary seeded from contract §3.3 and… | — |
| DAT-260 | J12/W8 | S | B | provide catalogue search with Greek and English terms, accent-insensitive. | — |
| DAT-261 | J12/W8 | S | B | publish the catalogue's personal-data inventory (column, class, purposes, retention)… | CMP-006 |
| DAT-262 | J12/W8 | S | B | classify every column P0–P3 from the producer's contract classification,… | — |
| DAT-263 | J12/W8 | M | B | tokenise direct identifiers (names, tax numbers, IBANs, plates, phone… | PLT-011 |
| DAT-264 | J12/W8 | S | B | grant access to P2 and P3 columns and to… | PLT-004 |
| DAT-265 | J12/W8 | S | B | apply masking and row filters at query time by… | PLT-001 |
| DAT-266 | J12/W8 | S | B | exclude P3 data (health and injury details, criminal-offence data)… | CLM-207 |
| DAT-267 | J12/W8 | S | B | generalise quasi-identifiers (birth date to age band, postcode to… | — |
| DAT-268 | J12/W8 | S | B | log every read of P2/P3 data and every vault… | PLT-002 |
| DAT-269 | J12/W8 | S | B | register lakehouse data sets and their purge handlers with… | PLT-237 |
| DAT-270 | J12/W8 | M | B | Elaborating REQ-DAT-009, the system shall implement the DSAR fan-out… | CMP-005 |
| DAT-271 | J12/W8 | M | B | execute erasure by destroying the subject's encryption keys (bronze),… | PLT-240, PLT-241 |
| DAT-272 | J12/W8 | S | B | check legal holds and retention obligations before erasure and… | PLT-238 |
| DAT-273 | J12/W8 | S | B | keep regulatory figures of submitted mart runs reproducible after… | — |
| DAT-275 | J12/W8 | M | B | return erasure and restriction outcomes with evidence (layers affected,… | — |
| DAT-276 | J12/W8 | S | B | apply retention classes RC-DAT-* (section 7.1) to DAT-owned entities. | PLT-011 |
| DAT-277 | J12/W8 | S | B | honour PTY consent withdrawals for purposes that require consent… | PTY-005 |
| DAT-278 | J12/W8 | S | B | keep a DPIA register entry for each high-risk processing… | CMP-006 |
| DAT-279 | J12/W8 | S | B | provide the DPO a privacy console (SCR-DAT-17) for classifications,… | — |
| DAT-280 | J12/W8 | S | B | generate synthetic analytical data sets (portfolios, claims development, payments,… | PLT-243 |
| DAT-281 | J12/W8 | S | E | test synthetic data sets for privacy leakage (nearest-neighbour distance,… | — |
| DAT-282 | J12/W8 | S | B | deliver lakehouse copies to non-production only as pseudonymised (format-preserving… | PLT-242 |
| DAT-283 | J12/W8 | S | B | provide golden datasets for regulatory tests (section 14.x) as… | — |
| DAT-286 | J12/W8 | S | B | emit OpenTelemetry traces, metrics and logs for every pipeline… | PLT-013 |
| DAT-287 | J12/W8 | S | B | budget and allocate compute and storage cost per data… | — |
| DAT-288 | J12/W8 | S | B | isolate workloads so that self-service and training cannot delay… | — |
| DAT-289 | J12/W8 | S | B | back up bronze, mart runs, the registry and the… | PLT-012 |
| DAT-290 | J12/W8 | S | B | test recovery at least annually, measuring achieved recovery times… | PLT-012 |
| DAT-292 | J12/W8 | S | B | deploy pipeline, transform and mart changes only through the… | PLT-265 |
| DAT-294 | J13/W9 | S | B | load legacy historical data needed for actuarial and regulatory… | MIG-002 |
| DAT-295 | J13/W9 | S | B | map legacy identifiers to core identifiers using MIG's cross-reference… | MIG-002 |
| DAT-297 | J13/W9 | M | B | support coexistence reporting by combining legacy and core facts… | MIG-004, MIG-149 |
| DAT-298 | J13/W9 | S | B | detect duplicates between legacy and core facts for the… | — |
| DAT-299 | J12/W8 | M | B | handle both ChargeDeltaEmitted delta modes of POL (pol.billing.delta_mode, REQ-POL-005):… | POL-005 |

### 1.10 MIG (167 rows in range)

J/W mix: J13/W9×167

| ID | J/W | Sz | Tag | Title (first words) | Deps |
|---|---|---|---|---|---|
| MIG-001 | J13/W9 | XL | B | define, publish and enforce the import-API contract that every… | PLT-148, PLT-005 |
| MIG-002 | J13/W9 | XL | B | maintain the legacy-to-core cross-reference (LegacyXref): for each legacy source… | PTY-007, PLT-215, DAT-295 |
| MIG-003 | J13/W9 | XL | B | provide the renewal conversion pipeline: per cohort, early extract… | POL-341, RAT-269, PFC-242 |
| MIG-004 | J13/W9 | XL | B | provide coexistence routing and cross-system enquiry: a routing table… | — |
| MIG-005 | J13/W9 | XL | B | provide reconciliation and sign-off per batch and per wave:… | BIL-339, CLM-236, RI-249, FIN-281, DAT-253 |
| MIG-006 | J13/W9 | XL | B | provide legacy archive data-subject operations callable by CMP DSAR… | CMP-005, CMP-154, CMP-158, PLT-011 |
| MIG-030 | J13/W9 | L | B | hold a migration decision matrix with one row per… | PLT-004 |
| MIG-031 | J13/W9 | M | B | define waves (scope, scenario, strategy, planned dates, entry and… | — |
| MIG-032 | J13/W9 | M | B | enforce entry criteria before a wave or cohort starts… | — |
| MIG-033 | J13/W9 | M | B | run a go/no-go decision per wave and per cut-over… | PLT-004 |
| MIG-034 | J13/W9 | M | B | keep a migration control board record set: issue log,… | — |
| MIG-036 | J13/W9 | M | B | require, before go/no-go of any wave that connects a… | PLT-012, PLT-266 |
| MIG-037 | J13/W9 | M | B | hold a purpose register for migrated data: per object… | DAT-294 |
| MIG-038 | J13/W9 | M | B | support both programme scenarios as configuration: scenario A (new… | MKT-004 |
| MIG-040 | J13/W9 | M | B | maintain a regulator communication plan per wave (supervisor, tax… | CMP-008 |
| MIG-042 | J13/W9 | M | B | keep a source inventory: each legacy source system, database,… | — |
| MIG-043 | J13/W9 | S | B | keep a data dictionary per source: tables or record… | — |
| MIG-044 | J13/W9 | M | B | profile every source field on each extract: null and… | — |
| MIG-045 | J13/W9 | M | B | hold a DQ rule library of versioned rules in… | — |
| MIG-046 | J13/W9 | L | B | run Greek-specific checks as country-pack rules behind the LegacyDataProfile… | PTY-003, MKT-002 |
| MIG-047 | J13/W9 | S | B | create DQ issues from rule failures, grouping failed records… | — |
| MIG-048 | J13/W9 | M | B | route DQ issues to business remediation owners as WRK… | WRK-001, WRK-003 |
| MIG-049 | J13/W9 | S | B | verify remediation by re-running the failing rule on the… | — |
| MIG-050 | J13/W9 | M | B | support waivers with scope (rule, cohort, record list), business… | PLT-004 |
| MIG-052 | J13/W9 | M | B | convert legacy text to UTF-8 per field using the… | — |
| MIG-053 | J13/W9 | M | B | check ownership consistency of identifiers across legacy records (one… | — |
| MIG-054 | J13/W9 | M | B | hold mapping specifications as versioned data: per target object… | — |
| MIG-055 | J13/W9 | S | B | require maker-checker approval of every mapping-specification version and code… | PLT-004 |
| MIG-056 | J13/W9 | L | B | maintain code crosswalks from legacy codes to core codes:… | PFC-241, RAT-272 |
| MIG-057 | J13/W9 | S | B | detect unmapped codes in every extract and stop the… | — |
| MIG-058 | J13/W9 | M | B | classify every legacy attribute without a home in the… | — |
| MIG-059 | J13/W9 | M | B | handle core-mandatory attributes without a legacy source by one… | POL-010 |
| MIG-060 | J13/W9 | M | B | define bitemporal loading rules per object: which history is… | POL-340, POL-342 |
| MIG-061 | J13/W9 | S | B | carry a configuration hash for converted facts by referencing… | MKT-010 |
| MIG-062 | J13/W9 | S | B | unit-test mapping specifications with versioned test cases (input records… | — |
| MIG-063 | J13/W9 | M | B | keep golden records: a curated set of legacy records… | — |
| MIG-066 | J13/W9 | M | B | extract from legacy sources through read replicas, files or… | PLT-006 |
| MIG-067 | J13/W9 | M | B | run pipelines as workflow-engine workflows with steps extract →… | PLT-007 |
| MIG-068 | J13/W9 | M | B | create batches with id, wave, cohort, object type, target… | — |
| MIG-069 | J13/W9 | S | B | derive idempotency keys deterministically from (legacy system, object type,… | — |
| MIG-070 | J13/W9 | S | B | require a successful dry-run of the same batch content… | — |
| MIG-071 | J13/W9 | S | B | record a RecordOutcome per record and step: status (Pending,… | — |
| MIG-072 | J13/W9 | S | B | load only through the target module's import operations and… | PLT-320 |
| MIG-073 | J13/W9 | S | B | throttle loads per target module to the module's declared… | — |
| MIG-074 | J13/W9 | S | B | process delta extracts (records changed in legacy since the… | — |
| MIG-075 | J13/W9 | S | B | support record deferral: moving records from a batch to… | — |
| MIG-076 | J13/W9 | M | B | support batch reversal before a wave's point of no… | — |
| MIG-077 | J13/W9 | M | B | publish MigrationBatchLoaded when a batch finishes loading (counts by… | PLT-005 |
| MIG-078 | J13/W9 | S | B | let an authorised analyst re-drive rejected records after a… | — |
| MIG-079 | J13/W9 | S | B | run pseudonymised full-volume pipelines in non-production using the platform's… | — |
| MIG-081 | J13/W9 | M | B | match legacy parties deterministically before load: same verified AFM… | — |
| MIG-082 | J13/W9 | L | B | match probabilistically where deterministic keys are absent or conflict,… | PTY-269 |
| MIG-083 | J13/W9 | S | B | apply survivorship rules per attribute (most recent verified, most… | — |
| MIG-084 | J13/W9 | M | B | send customer match groups confirmed by reviewers to PTY… | PTY-007 |
| MIG-085 | J13/W9 | S | B | support un-merge of a migration match by reversing the… | — |
| MIG-086 | J13/W9 | S | B | convert household and organisation relationships (spouse, dependant, director, parent… | PTY-004 |
| MIG-087 | J13/W9 | S | B | convert consents and communication preferences with original source, timestamp… | PTY-271 |
| MIG-088 | J13/W9 | M | B | trigger bulk sanctions screening of every imported party per… | PTY-006, PTY-272 |
| MIG-089 | J13/W9 | S | B | preserve legacy identifiers (legacy customer numbers, legacy party keys… | PLT-215 |
| MIG-090 | J13/W9 | S | B | load legacy AFMs that fail the check digit but… | PTY-003 |
| MIG-091 | J13/W9 | M | B | extract expiring legacy terms early, at a lead time… | — |
| MIG-092 | J13/W9 | M | B | transform each extracted term into a renewal-conversion input: party… | — |
| MIG-093 | J13/W9 | M | B | resolve the target product version through PFC resolution (REQ-PFC-001)… | PFC-001, PFC-008 |
| MIG-094 | J13/W9 | M | B | run a dry-run renewal for each case: POL renewal… | POL-001, UW-001 |
| MIG-095 | J13/W9 | M | B | re-rate each cohort through rat.Rate.rateBatch (REQ-RAT-002) in RENEWAL mode… | RAT-002, RAT-269, RAT-270 |
| MIG-096 | J13/W9 | S | B | compare prices per case: legacy expiring premium, legacy renewal… | RAT-003 |
| MIG-097 | J13/W9 | M | B | classify each case against tolerance bands set per cohort… | — |
| MIG-098 | J13/W9 | M | B | route out-of-tolerance cases to the conversion review queue (WRK… | WRK-003, PTY-011 |
| MIG-099 | J13/W9 | M | B | let the reviewer approve, adjust, refer or exclude a… | RAT-005, PLT-003 |
| MIG-100 | J13/W9 | S | B | apply a fallback at the decision deadline: an undecided… | — |
| MIG-101 | J13/W9 | M | B | create the renewal job in POL for each approved… | POL-009, POL-341 |
| MIG-102 | J13/W9 | M | B | guarantee continuity of cover: the converted renewal term starts… | PLT-014, POL-342 |
| MIG-103 | J13/W9 | S | B | stop the legacy renewal for every converted policy through… | — |
| MIG-104 | J13/W9 | M | B | issue the renewal offer and documents from the core… | DOC-001 |
| MIG-105 | J13/W9 | M | B | carry over bonus-malus class (through the RAT mapping table… | RAT-272, BIL-012, PTY-009 |
| MIG-106 | J13/W9 | M | B | handle gap events between extract and inception from daily… | POL-011 |
| MIG-107 | J13/W9 | S | B | support re-conversion of a case (re-extract, re-transform, re-rate, re-review)… | POL-011 |
| MIG-108 | J13/W9 | M | B | track each case's outcome after offer (Bound, NotTaken, Lapsed,… | — |
| MIG-115 | J13/W9 | M | B | load billing opening balances per billing account at the… | BIL-012 |
| MIG-116 | J13/W9 | M | B | convert direct-debit mandates with original mandate reference, signature date,… | BIL-012 |
| MIG-117 | J13/W9 | M | B | restart open delinquency processes at their equivalent BIL step… | BIL-006, CMP-249 |
| MIG-118 | J13/W9 | M | B | load finance opening balances through fin.Import.openingBalances (REQ-FIN-010) per account,… | FIN-010, FIN-281 |
| MIG-119 | J13/W9 | M | B | ensure no double counting: module events with origin=MIGRATION are… | FIN-043 |
| MIG-120 | J13/W9 | M | B | supply IFRS 17 data for migrated in-force terms: original… | FIN-282, FIN-283 |
| MIG-121 | J13/W9 | M | B | load open claims liabilities and RI balances at group… | FIN-284, CLM-236, RI-249 |
| MIG-122 | J13/W9 | M | B | fix a tax and levy boundary date per legal… | FIN-005, BIL-012 |
| MIG-123 | J13/W9 | M | B | produce a tax and levy split reconciliation for each… | FIN-005 |
| MIG-124 | J13/W9 | M | B | apply the fiscal hand-over rule: for any credit, cancellation… | CMP-032, CMP-249 |
| MIG-125 | J13/W9 | S | B | load commission history and intermediary balances (bil.Import.commissionHistory, bil.Import.agencyItems) so… | BIL-008 |
| MIG-127 | J13/W9 | M | B | apply claims treatment rules per claim from the decision… | — |
| MIG-128 | J13/W9 | M | B | resolve each converted claim's policy snapshot: where the policy… | POL-007, CLM-010 |
| MIG-129 | J13/W9 | M | B | convert open claims with full history: claim, exposures, claimants… | CLM-010 |
| MIG-130 | J13/W9 | M | B | re-create running statutory claim clocks (for example CLM_MTPL_OFFER, CLM_MTPL_ASSESSMENT,… | CMP-249, CLM-007 |
| MIG-131 | J13/W9 | M | B | convert Friendly Settlement open receivables and payables with clearing… | CLM-160 |
| MIG-133 | J13/W9 | M | B | convert closed-claim summaries for at least the claims-history certificate… | CLM-008 |
| MIG-134 | J13/W9 | S | B | assign converted claims to handlers through WRK with a… | WRK-001 |
| MIG-135 | J13/W9 | S | B | send closed claims history older than the summary period… | DAT-294 |
| MIG-136 | J13/W9 | M | B | convert treaty and facultative contracts in force and in… | RI-007, RI-247 |
| MIG-137 | J13/W9 | S | B | convert cession and recovery history for in-force and open-claim… | RI-248 |
| MIG-138 | J13/W9 | M | B | convert outstanding balances with reinsurers (open account items, cash… | RI-007, FIN-284 |
| MIG-139 | J13/W9 | M | B | supply RI with coexistence inputs for treaties covering both… | RI-007 |
| MIG-140 | J13/W9 | S | B | reconcile RI conversion per batch (contracts and terms hash,… | RI-249 |
| MIG-141 | J13/W9 | M | B | convert only term-level and register underwriting items (ruling R-88):… | UW-295 |
| MIG-142 | J13/W9 | M | B | import legacy documents (policy schedules, certificates, letters, claim documents)… | DOC-311, DOC-312 |
| MIG-143 | J13/W9 | M | B | import open activities, notes, queues and participants through wrk.Import.*… | WRK-386, WRK-252 |
| MIG-145 | J13/W9 | M | B | load intermediaries, hierarchy, producer codes, appointments and commission agreements… | PTY-270 |
| MIG-146 | J13/W9 | S | B | supply MKT with historical configuration values (legacy tax, levy… | MKT-010 |
| MIG-147 | J13/W9 | S | B | import open complaints and open DSARs with history and… | CMP-249 |
| MIG-148 | J13/W9 | M | B | maintain routing entries (CoexistenceRoute) per object (policy, term, claim,… | — |
| MIG-149 | J13/W9 | M | B | answer mig.Routing.resolve(objectType, key, asOf) for core ids, legacy keys,… | — |
| MIG-150 | J13/W9 | S | B | define default routing for unknown objects: a key not… | — |
| MIG-151 | J13/W9 | M | B | build a legacy index (keys, names in native and… | — |
| MIG-152 | J13/W9 | M | B | provide cross-system enquiry (mig.Enquiry.get) returning a unified summary for… | POL-002, CLM-009, BIL-001 |
| MIG-153 | J13/W9 | M | B | offer actions only in the master system: core actions… | POL-001 |
| MIG-154 | J13/W9 | M | B | make routing available to channels and modules: CHN (REQ-CHN-039)… | — |
| MIG-155 | J13/W9 | M | B | synchronise party changes in both directions during coexistence for… | PTY-001 |
| MIG-156 | J13/W9 | S | B | limit synchronisation to the attributes legacy needs to service… | — |
| MIG-157 | J13/W9 | M | B | detect misdirected payments: receipts arriving in the core with… | BIL-004 |
| MIG-158 | J13/W9 | M | B | transfer misdirected payments to the master system with maker-checker:… | BIL-009 |
| MIG-159 | J13/W9 | S | B | split bank files and collections per system by creditor… | BIL-004 |
| MIG-160 | J13/W9 | M | B | run combined reporting checks during coexistence with DAT: no… | DAT-297, DAT-298 |
| MIG-161 | J13/W9 | M | B | specify and test the legacy-side interfaces (L-01 extract, L-02… | — |
| MIG-162 | J13/W9 | M | B | drive the Information Centre hand-over per vehicle: for each… | CMP-086 |
| MIG-163 | J13/W9 | S | B | run the bureau hand-over reconciliation per wave (REQ-CMP-087) and… | CMP-087 |
| MIG-164 | J13/W9 | M | B | import legacy fiscal document history (MARK, UID, series, number,… | CMP-249, CMP-054 |
| MIG-165 | J13/W9 | M | B | reserve fiscal series for the core that cannot collide… | CMP-038, PLT-215 |
| MIG-166 | J13/W9 | S | B | record the issuer-software change decision for myDATA transmissions (whether… | — |
| MIG-167 | J13/W9 | M | B | coordinate payment-provider hand-over: creditor-system switch dates for mandates, card… | BIL-012 |
| MIG-168 | J13/W9 | M | B | hold a customer and intermediary communication plan per wave:… | DOC-001, DOC-005 |
| MIG-169 | J13/W9 | M | B | make conversion communications informational, not contractual: the renewal offer… | DOC-002 |
| MIG-171 | J13/W9 | S | B | compute three-way counts per batch: extracted = transformed +… | — |
| MIG-172 | J13/W9 | M | B | compute financial control totals per batch and wave, by… | — |
| MIG-173 | J13/W9 | S | B | obtain core-side totals from the module reconciliation operations (REQ-BIL-339,… | — |
| MIG-174 | J13/W9 | S | B | apply tolerance thresholds per measure (counts: zero; money: €0.00… | — |
| MIG-175 | J13/W9 | M | B | manage breaks with root cause, owner, disposition (fix and… | FIN-215, BIL-290 |
| MIG-176 | J13/W9 | M | B | require named signatories per report type (counts: migration lead;… | PLT-004 |
| MIG-177 | J13/W9 | S | B | produce a wave sign-off pack combining all signed reconciliations,… | DOC-004 |
| MIG-178 | J13/W9 | M | B | run re-performance sampling: for a statistically chosen sample per… | RAT-001, DOC-001 |
| MIG-179 | J13/W9 | S | B | reconcile party and identifier coverage per batch (count of… | PTY-013 |
| MIG-181 | J13/W9 | M | B | support at least three mock conversions before the first… | — |
| MIG-182 | J13/W9 | S | B | run a dress rehearsal on production-like volumes in pre-production… | — |
| MIG-183 | J13/W9 | M | B | run parallel month-end closes for at least one month… | FIN-008, FIN-294 |
| MIG-184 | J13/W9 | S | B | use the non-production time service to run rehearsals at… | PLT-332 |
| MIG-186 | J13/W9 | M | B | manage a cut-over runbook: tasks with owner, system, dependencies,… | PLT-007 |
| MIG-187 | J13/W9 | M | B | link each production cut-over to a PLT change record… | PLT-266, PLT-267 |
| MIG-188 | J13/W9 | M | B | define rollback points and a point of no return… | — |
| MIG-189 | J13/W9 | S | B | rehearse rollback at least once per wave type and… | — |
| MIG-190 | J13/W9 | S | B | execute renewal cohort rollback before the point of no… | POL-011 |
| MIG-191 | J13/W9 | S | B | run hyper-care after each production wave: daily review of… | — |
| MIG-192 | J13/W9 | M | B | close hyper-care only when exit criteria hold for a… | — |
| MIG-193 | J13/W9 | M | B | plan legacy decommissioning with prerequisites (no LegacyMaster or RunOffLegacy… | — |
| MIG-194 | J13/W9 | S | B | instruct legacy into a read-only period (interface L-08) once… | — |
| MIG-195 | J13/W9 | M | B | build the legacy archive as a MIG-owned store in… | — |
| MIG-196 | J13/W9 | S | B | provide archive access for authorised staff, auditors and regulators:… | PLT-002 |
| MIG-197 | J13/W9 | M | B | apply retention to archive datasets by RC code per… | PLT-237, PLT-238 |
| MIG-198 | J13/W9 | M | B | implement the archive data-subject operations of REQ-MIG-006 with retention-aware… | — |
| MIG-199 | J13/W9 | S | B | include migration staging in data-subject operations and purge staging… | — |
| MIG-200 | J13/W9 | M | B | support legal holds on archive datasets and records from… | PLT-238, PLT-239 |
| MIG-202 | J13/W9 | M | B | encrypt extracts in transit (TLS 1.2+ or encrypted file… | PLT-006 |
| MIG-203 | J13/W9 | M | B | restrict access to staging and archive by ABAC (legal… | PLT-001 |
| MIG-204 | J13/W9 | S | B | pseudonymise every dataset used outside production with PLT's consistent… | PLT-242 |
| MIG-205 | J13/W9 | S | B | audit every MIG action (mapping change, approval, batch start,… | PLT-002 |
| MIG-206 | J13/W9 | M | B | implement every MIG command with an Idempotency-Key, typed preconditions,… | — |
| MIG-207 | J13/W9 | S | B | label every MIG screen, message and error in Greek… | MKT-005 |
| MIG-208 | J13/W9 | S | B | hold MIG parameters (lead times, tolerance bands, thresholds, sampling… | MKT-001 |
| MIG-209 | J13/W9 | M | B | run every MIG AI feature through the PLT AI… | PLT-010, CMP-007 |
| MIG-210 | J13/W9 | L | B | for every MIG AI feature, record an AiInteractionRecord (feature, | PLT-010, CMP-007 |

## 2. B.2 Later-phase Musts (not in motor MVP cut)

Total: 75 rows. By phase: P2=39, P3=18, P4=18.

| Module | Total | P2 | P3 | P4 | IDs (phase) |
|---|---|---|---|---|---|
| PLT | 2 | 0 | 0 | 2 | PLT-272 (P4), PLT-279 (P4) |
| MKT | 13 | 0 | 0 | 13 | MKT-155 (P4), MKT-157 (P4), MKT-158 (P4), MKT-159 (P4), MKT-161 (P4), MKT-163 (P4), MKT-201 (P4), MKT-202 (P4), MKT-203 (P4), MKT-215 (P4), MKT-218 (P4), MKT-222 (P4), MKT-224 (P4) |
| PTY | 1 | 0 | 1 | 0 | PTY-277 (P3) |
| RAT | 4 | 4 | 0 | 0 | RAT-107 (P2), RAT-148 (P2), RAT-149 (P2), RAT-151 (P2) |
| UW | 22 | 9 | 13 | 0 | UW-008 (P2), UW-152 (P3), UW-169 (P3), UW-176 (P3), UW-179 (P3), UW-197 (P2), UW-208 (P2), UW-210 (P2), UW-211 (P3), UW-217 (P3), UW-223 (P3), UW-224 (P3), UW-226 (P3), UW-228 (P3), UW-229 (P3), UW-230 (P3), UW-231 (P3), UW-276 (P2), UW-277 (P2), UW-278 (P2), UW-279 (P2), UW-280 (P2) |
| POL | 2 | 2 | 0 | 0 | POL-286 (P2), POL-304 (P2) |
| FIN | 3 | 2 | 1 | 0 | FIN-140 (P2), FIN-166 (P3), FIN-168 (P2) |
| DOC | 2 | 0 | 2 | 0 | DOC-043 (P3), DOC-254 (P3) |
| CMP | 13 | 11 | 0 | 2 | CMP-091 (P4), CMP-177 (P2), CMP-178 (P2), CMP-179 (P2), CMP-180 (P2), CMP-181 (P2), CMP-182 (P2), CMP-183 (P2), CMP-185 (P2), CMP-186 (P2), CMP-187 (P2), CMP-188 (P2), CMP-194 (P4) |
| CHN | 2 | 2 | 0 | 0 | CHN-299 (P2), CHN-300 (P2) |
| DAT | 11 | 9 | 1 | 1 | DAT-063 (P2), DAT-102 (P4), DAT-109 (P3), DAT-125 (P2), DAT-126 (P2), DAT-128 (P2), DAT-180 (P2), DAT-181 (P2), DAT-183 (P2), DAT-186 (P2), DAT-291 (P2) |

Themes: **P2** = property/home (hazard zones and schemes, rebuild-cost valuation, insurance-to-value, accumulation checks, buildings/risk units, A.1004 residential-property return and ENFIA confirmations, home quote/claims in CHN, nat-cat exposure data and SII nat-cat set in DAT, IFRS 17 issued↔RI-held group linkage, cession-exception handling in FIN). **P3** = commercial and nat-cat refusals (organisation revenue, commercial financials, intake verification gate, nat-cat compliance check/clock, refusal documents/register, RI statements to FIN, outgoing RI programme inputs). **P4** = multi-entity / multi-market (stamp provisioning and migration, group services, cross-entity enquiry, cross-border/FoS register and checks, currency changeover dual display, Cyprus stubs, other-market regimes).

| ID | Phase | Tag | Title (first words) | Deps |
|---|---|---|---|---|
| PLT-272 | P4 | B | provision a new, empty, tested stamp in at most… | — |
| PLT-279 | P4 | E | rehearse stamp migration (moving a stamp to another EU… | — |
| MKT-155 | P4 | E | keep group services (product library publication, reference data distribution,… | — |
| MKT-157 | P4 | B | export the dependency map of group services to stamps… | PLT-012 |
| MKT-158 | P4 | E | provide cross-entity enquiry for group staff as a read-only… | PLT-001, PLT-002 |
| MKT-159 | P4 | E | label every cross-entity enquiry result row with its legal… | — |
| MKT-161 | P4 | E | stand up a new legal entity from a stamp… | — |
| MKT-163 | P4 | E | distribute group-layer configuration to stamps as signed configuration bundles… | — |
| MKT-201 | P4 | E | provide the counter-currency amount for document rendering while a… | DOC-001 |
| MKT-202 | P4 | B | never rewrite historical money facts on changeover; facts before… | — |
| MKT-203 | P4 | E | hold the dual-display rule as an effective-dated key l10n.dual_display… | — |
| MKT-215 | P4 | E | decide which pack applies per behaviour using each SPI's… | — |
| MKT-218 | P4 | E | maintain a cross-border authorisation register (entity CrossBorderAuthorisation, CCR-MKT-11 accepted… | — |
| MKT-222 | P4 | E | provide mkt.CrossBorder.check(entity, hostState, class, date) returning authorised or not… | POL-003, PFC-011 |
| MKT-224 | P4 | E | let a key declare a resolution axis so that… | — |
| PTY-277 | P3 | E | hold, for an Organisation, annual gross revenue per fiscal… | UW-176 |
| RAT-107 | P2 | B | For property products the system shall allocate premium between… | PFC-142 |
| RAT-148 | P2 | B | Property inputs for seismic, flood and wildfire hazard shall… | — |
| RAT-149 | P2 | B | A rating artefact shall be able to support two… | — |
| RAT-151 | P2 | B | support flood and wildfire zone factors and sum-insured curves… | — |
| UW-008 | P2 | B | perform an accumulation check at PRE_BIND for products flagged… | — |
| UW-152 | P3 | B | show Financials for commercial accounts: declared revenue by year,… | — |
| UW-169 | P3 | B | prevent any intake value from reaching PTY or POL… | WRK-280 |
| UW-176 | P3 | B | run a nat-cat compliance check for business risks in… | — |
| UW-179 | P3 | B | start the nat-cat response clock on receipt of a… | CMP-003 |
| UW-197 | P2 | B | obtain hazard-zone keys for risk locations through Geocoder and… | — |
| UW-208 | P2 | B | provide a rebuild-cost valuation tool for buildings using construction… | PFC-001 |
| UW-210 | P2 | B | compute insurance-to-value (sum insured ÷ valuation) and raise an… | — |
| UW-211 | P3 | B | feed declared asset values and valuations into the nat-cat… | — |
| UW-217 | P3 | B | support decline scope FULL or PARTIAL; a partial decline… | — |
| UW-223 | P3 | B | request the refusal document (DOC document type DT-NATCAT-REFUSAL, or… | DOC-001, DOC-043 |
| UW-224 | P3 | B | start a statutory clock (clock code UW_NATCAT_RESPONSE, CMP register… | CMP-003 |
| UW-226 | P3 | B | support refusal by peril and asset class so that… | — |
| UW-228 | P3 | B | track the refusal document to rendered, delivered or failed… | DOC-005 |
| UW-229 | P3 | B | keep a refusal register entry per refusal (decline number,… | DOC-004, DOC-254 |
| UW-230 | P3 | B | show the refusal register (SCR-UW-13) filtered by tax identifier… | — |
| UW-231 | P3 | B | export the register by tax identifier and period in… | PLT-002, MKT-307 |
| UW-276 | P2 | B | flag per product version whether accumulation checks apply and… | — |
| UW-277 | P2 | B | compute the job's incremental exposure per peril and hazard… | — |
| UW-278 | P2 | B | read zone aggregate and capacity from the RI accumulation… | — |
| UW-279 | P2 | B | classify the result into bands (within capacity, approaching capacity,… | — |
| UW-280 | P2 | E | fail safe when accumulation data is unavailable: raise an… | — |
| POL-286 | P2 | B | hold buildings with the PFC building element fields (construction,… | PFC-063 |
| POL-304 | P2 | B | expose risk-unit data for UW accumulation and DAT through… | — |
| FIN-140 | P2 | B | link each issued group to the reinsurance-held groups that… | RI-004 |
| FIN-166 | P3 | B | post statements of account and settlements (StatementIssued, SettlementRecorded) to… | — |
| FIN-168 | P2 | B | consume CessionExceptionRaised as context and expect no posting until… | — |
| DOC-043 | P3 | B | render nat-cat refusal documents and confirmations from the template… | UW-005, MKT-103 |
| DOC-254 | P3 | B | provide UW with refusal-document delivery proof and hash for… | UW-229 |
| CMP-091 | P4 | B | support a Cyprus-stub bureau implementation producing a differently laid-out… | MKT-008 |
| CMP-177 | P2 | B | hold an AnnualReturn record per return type, legal entity… | — |
| CMP-178 | P2 | B | generate the residential property return from the DAT A.1004… | DAT-003 |
| CMP-179 | P2 | B | populate per record: policyholder AFM and name, policy number,… | POL-010, PTY-012 |
| CMP-180 | P2 | B | validate records (mandatory fields, AFM check digit through IdValidator,… | PTY-003, WRK-001 |
| CMP-181 | P2 | B | receive the return file DAT renders through the pack's… | MKT-002 |
| CMP-182 | P2 | B | require maker-checker approval before regulatory submission of any annual… | PLT-004 |
| CMP-183 | P2 | B | submit through the pack's upload channel where automated, otherwise… | DOC-004 |
| CMP-185 | P2 | B | import ENFIA match requests (taxpayer AFM, property identifier ΑΤΑΚ,… | — |
| CMP-186 | P2 | B | auto-match each request against POL residential policies (policy number,… | POL-002 |
| CMP-187 | P2 | B | let an analyst decide unmatched requests and confirm or… | — |
| CMP-188 | P2 | B | transmit confirmations before CMP_ENFIA_CONFIRM and store the authority's acknowledgement. | — |
| CMP-194 | P4 | B | provide a Cyprus-stub returns configuration with a different return… | MKT-008 |
| CHN-299 | P2 | B | provide home quote and buy on the same journey… | PFC-222, POL-010 |
| CHN-300 | P2 | B | support home claims reporting with property question sets and… | CLM-035 |
| DAT-063 | P2 | B | exchange exposure data with external catastrophe-model and hazard-data providers… | PLT-006, PLT-314 |
| DAT-102 | P4 | B | support national and other-market regimes through the same transform… | MKT-008 |
| DAT-109 | P3 | B | build outgoing reinsurance programme and reinsurers' share inputs from… | RI-231 |
| DAT-125 | P2 | B | build, as the content source for CMP's return generation… | CMP-003 |
| DAT-126 | P2 | B | populate per record: policyholder tax number and name, policy… | POL-010, PTY-001 |
| DAT-128 | P2 | B | render the A.1004 file through the country pack's statutory… | CMP-008, MKT-326 |
| DAT-180 | P2 | B | build exposure snapshots of in-force risk units with geocode,… | POL-304 |
| DAT-181 | P2 | B | aggregate exposure by peril, scheme, zone, line, product, gross… | — |
| DAT-183 | P2 | B | overlay a catastrophe event's area (regions, postcodes or polygon… | CLM-210 |
| DAT-186 | P2 | B | build the Solvency II natural-catastrophe data set (sums insured… | — |
| DAT-291 | P2 | B | record external data providers (cat models, hazard data, market… | PLT-314 |

## 3. B.3 Baseline inventory coverage by screen

Heading: "218 owned items" (158 reference-screen entries from `00-baseline-inventory.md` §C + 60 UI-library rows from §D). Rows in range: 218. Extracted mechanically 2026-10-07; integration review (Appendix A §3.1) verified screen coverage 218/218 and field-level coverage 100% after fix round 1. Disposition words: Specified, Replaced, N/A (not applicable in Greece), "(target named)" = row names target screens without a keyword (verified as specified).

**MVP flag: the B.3 table has NO MVP/phase column** (columns: Inventory ID, Baseline screen, Owner, Owner PRD screens, Disposition words). No MVP flag can be quoted from this section; MVP scope must come from §16 / B.1.

**Uncovered items: 0** — every row names at least one owner `SCR-<MOD>-NN` screen.

Items per owner: POL 50, PFC 31, PLT 27, WRK 20, UW 20, CLM 18, CHN 11, PTY 9, DOC 9, BIL 9, FIN 4, RI 4, CMP 4, RAT 2.

"(target named)" only (20): SCR-PX-01, SCR-PX-02, SCR-PCACC1-02, SCR-PCACC2-01, SCR-PCACC2-07, SCR-PCACC2-08, SCR-PCACC2-09, SCR-PCACC2-13, SCR-PCADM2-04, SCR-PCADM2-07, SCR-PCADM2-09, SCR-PCADM2-10, SCR-PCADM2-11, SCR-PCADM2-12, SCR-PCPF-05, SCR-PCPF-11, SCR-PCPF-12, SCR-PCSUB1-12, SCR-PCSUB2-07, SCR-PCSUB2-08.

Rows containing N/A (18): SCR-CC-01, SCR-CC-03, SCR-PCACC1-09, SCR-PCACC2-05, SCR-PCADM2-03, SCR-PCPF-04, SCR-PCPF-13, SCR-PCTX-04, SCR-PCTX-17, SCR-PCTX-18, SCR-PCSUB1-03, SCR-PCSUB1-04, SCR-PCSUB1-07, SCR-PCSUB2-10, SCR-PD1-02, SCR-PD1-10, SCR-PD1-12, SCR-PD1-13. Sole disposition N/A: SCR-PCADM2-03.

Rows "Replaced" only (12): SCR-CC-02, SCR-PCACC1-03, SCR-PCACC1-07, SCR-PCACC2-03, SCR-PCACC2-10, SCR-PCACC2-11, SCR-PCSUB1-05, SCR-PCSUB1-09, SCR-PCSUB2-09, SCR-PD1-01, SCR-PD1-03, SCR-PD1-06.

| Inventory ID | Baseline screen | Owner | Owner screens (SCR- dropped) | Disposition |
|---|---|---|---|---|
| SCR-CC-01 | Claim - Parties Involved - Contacts | CLM | CLM-04, CLM-19 | N/A, Replaced, Specified |
| SCR-CC-02 | Claim - Hi Marley Case (incl. Hi Marley Connect side panel) | CLM | CLM-04, CLM-19 | Replaced |
| SCR-CC-03 | Claim - Summary (Overview) and Actions menu | CLM | CLM-02 | N/A, Replaced, Specified |
| SCR-CC-04 | Inspections - CCG IQ (vendor service request form) | CLM | CLM-12 | Specified |
| SCR-CC-05 | Claim - Services | CLM | CLM-06, CLM-12, CLM-13 | Specified |
| SCR-CC-06 | Search - Claims | CLM | CLM-03 | Specified |
| SCR-PX-01 | Indico Intake - Claims Inbox | WRK | WRK-11 | (target named) |
| SCR-PX-02 | Indico Intake - Claim Review | WRK | WRK-12 | (target named) |
| SCR-PCACC1-01 | Login | PLT | PLT-01 | Specified |
| SCR-PCACC1-02 | Desktop - My Summary | WRK | WRK-01 | (target named) |
| SCR-PCACC1-03 | New Account - Enter Account Information | PTY | PTY-01, PTY-02 | Replaced |
| SCR-PCACC1-04 | New Account - Create Account | PTY | PTY-01, PTY-02, PTY-03 | Replaced, Specified |
| SCR-PCACC1-05 | Account File - Summary | PTY | PTY-05 | Replaced, Specified |
| SCR-PCACC1-06 | Server Tools - Batch Process Info | PLT | PLT-10 | Replaced, Specified |
| SCR-PCACC1-07 | Internal Tools - Reload | PLT | PLT-25 | Replaced |
| SCR-PCACC1-08 | Internal Tools - PC Sample Data | PLT | PLT-25 | Specified |
| SCR-PCACC1-09 | New Submissions | POL | POL-02 | N/A, Replaced, Specified |
| SCR-PCACC1-10 | Account File - Policy Transactions | POL | POL-09 | Specified |
| SCR-PCACC1-11 | New Account - Organizations (producer organization search popup) | PTY | PTY-03 | Replaced, Specified |
| SCR-PCACC1-12 | Account File - Contacts | PTY | PTY-06 | Specified |
| SCR-PCACC1-13 | Account File - Locations | PTY | PTY-07 | Specified |
| SCR-PCACC2-01 | Account File Participants | WRK | WRK-16 | (target named) |
| SCR-PCACC2-02 | Error pages (Internal Server Exception / Render Exception) | PLT | PLT-26 | Specified |
| SCR-PCACC2-03 | Search Exclusions And Conditions for Personal Auto Line | POL | POL-06 | Replaced |
| SCR-PCACC2-04 | Account Holder Summary (Contact file summary) | PTY | PTY-04 | Specified |
| SCR-PCACC2-05 | Search Policies | POL | POL-01 | N/A, Specified |
| SCR-PCACC2-06 | Search Accounts (incl. Administration menu and Settings gear menu) | PTY | PTY-01 | Specified |
| SCR-PCACC2-07 | Desktop - My Activities | WRK | WRK-02 | (target named) |
| SCR-PCACC2-08 | Desktop - Assign Activities | WRK | WRK-06 | (target named) |
| SCR-PCACC2-09 | Account File - New Note (worksheet on Account Summary) | WRK | WRK-10 | (target named) |
| SCR-PCACC2-10 | Location Info (PCF location diagnostic page) | PLT | PLT-26 | Replaced |
| SCR-PCACC2-11 | Server Tools - View Logs | PLT | PLT-24 | Replaced |
| SCR-PCACC2-12 | Account File - Submission Manager | POL | POL-09 | Specified |
| SCR-PCACC2-13 | Desktop - My Accounts | WRK | WRK-01 | (target named) |
| SCR-PCACC2-14 | Internal Tools - Testing System Clock | PLT | PLT-25 | Specified |
| SCR-PCADM1-01 | Policy Form Patterns (search + results list) | DOC | DOC-05 | Specified |
| SCR-PCADM1-02 | New Policy Form Pattern (wizard-style tabbed create/edit) | DOC | DOC-06 | Specified |
| SCR-PCADM1-03 | Selected coverage, condition, or exclusion is used (picker) | DOC | DOC-06 | Specified |
| SCR-PCADM1-04 | Form Pattern (view/edit detail) | DOC | DOC-06 | Specified |
| SCR-PCADM1-05 | Runtime Properties | PLT | PLT-13 | Specified |
| SCR-PCADM1-06 | Policy Holds (list + hold details) | UW | UW-11 | Specified |
| SCR-PCADM1-07 | New Policy Hold | UW | UW-11 | Specified |
| SCR-PCADM1-08 | Users (search) | PLT | PLT-02 | Specified |
| SCR-PCADM1-09 | User Detail (tabbed view/edit) | PLT | PLT-03 | Replaced, Specified |
| SCR-PCADM1-10 | Underwriting Rules (list, filters, import/export menu) | UW | UW-07 | Replaced, Specified |
| SCR-PCADM1-11 | Underwriting Rule Detail (view/edit) | UW | UW-08 | Replaced, Specified |
| SCR-PCADM1-12 | Create New Rule | UW | UW-08 | Specified |
| SCR-PCADM1-13 | Import/Export Status (underwriting rules) | UW | UW-09 | Specified |
| SCR-PCADM1-14 | Authority Profiles (list) | PLT | PLT-07 | Specified |
| SCR-PCADM2-01 | Authority Profile (detail / edit) | PLT | PLT-07 | Specified |
| SCR-PCADM2-02 | Issue Type Search | UW | UW-10 | Specified |
| SCR-PCADM2-03 | Activity Patterns (list) | WRK | WRK-17 | N/A |
| SCR-PCADM2-04 | New Activity Pattern | WRK | WRK-18 | (target named) |
| SCR-PCADM2-05 | Holidays (list) | PLT | PLT-14 | Specified |
| SCR-PCADM2-06 | Add Holiday | PLT | PLT-14 | Replaced, Specified |
| SCR-PCADM2-07 | Activity Pattern Detail | WRK | WRK-18 | (target named) |
| SCR-PCADM2-08 | Data Change | PLT | PLT-23 | Replaced, Specified |
| SCR-PCADM2-09 | Groups (search) | WRK | WRK-19 | (target named) |
| SCR-PCADM2-10 | Group Detail | WRK | WRK-19 | (target named) |
| SCR-PCADM2-11 | Search Users (from Group) | WRK | WRK-20 | (target named) |
| SCR-PCADM2-12 | New User | PLT | PLT-03 | (target named) |
| SCR-PCADM2-13 | Roles (list) | PLT | PLT-04 | Specified |
| SCR-PCADM2-14 | Role Detail (view / edit) | PLT | PLT-04 | Replaced, Specified |
| SCR-PCPF-01 | Policy File - Summary | POL | POL-10 | Specified |
| SCR-PCPF-02 | Policy File - Policy Transactions | POL | POL-12 | Specified |
| SCR-PCPF-03 | Policy File - Quote | POL | POL-07, POL-11 | Specified |
| SCR-PCPF-04 | Policy File - PA Coverages | POL | POL-11 | N/A, Replaced, Specified |
| SCR-PCPF-05 | Policy File - Participants | WRK | WRK-16 | (target named) |
| SCR-PCPF-06 | Policy File - Contacts | POL | POL-11 | Replaced, Specified |
| SCR-PCPF-07 | Policy File - Documents | DOC | DOC-10 | Replaced, Specified |
| SCR-PCPF-08 | Policy File - Forms | DOC | DOC-11 | Specified |
| SCR-PCPF-09 | Policy File - Policy Info | POL | POL-11 | Specified |
| SCR-PCPF-10 | Policy File - Vehicles | POL | POL-05, POL-11 | Specified |
| SCR-PCPF-11 | Policy File - New Activity (worksheet) | WRK | WRK-05 | (target named) |
| SCR-PCPF-12 | Policy File - Activity Detail (worksheet) | WRK | WRK-04 | (target named) |
| SCR-PCPF-13 | Policy File - Drivers | POL | POL-04, POL-11 | N/A, Replaced, Specified |
| SCR-PCTX-01 | Policy Change wizard: Start Policy Change | POL | POL-13 | Specified |
| SCR-PCTX-02 | Policy Change wizard: Offerings | POL | POL-13 | Specified |
| SCR-PCTX-03 | Policy Change wizard: Drivers | POL | POL-04 | Replaced, Specified |
| SCR-PCTX-04 | Policy Change wizard: PA Coverages | POL | POL-06 | N/A, Specified |
| SCR-PCTX-05 | Policy Change wizard: Risk Analysis | UW | UW-02 | Replaced, Specified |
| SCR-PCTX-06 | Policy Change wizard: Quote | POL | POL-13 | Specified |
| SCR-PCTX-07 | Policy Change wizard: Policy Review | POL | POL-12, POL-13 | Specified |
| SCR-PCTX-08 | Policy Change wizard: Policy Change Bound | POL | POL-13, POL-18 | Specified |
| SCR-PCTX-09 | Cancellation wizard: Start Cancellation | POL | POL-14 | Specified |
| SCR-PCTX-10 | Cancellation wizard: Confirmation | POL | POL-14 | Specified |
| SCR-PCTX-11 | Cancellation wizard: Cancellation Bound | POL | POL-14 | Specified |
| SCR-PCTX-12 | Reinstatement wizard: Start Reinstatement | POL | POL-15 | Specified |
| SCR-PCTX-13 | Reinstatement wizard: Quote | POL | POL-15 | Specified |
| SCR-PCTX-14 | Rewrite wizard: Offerings | POL | POL-16 | Specified |
| SCR-PCTX-15 | Rewrite wizard: Policy Info | POL | POL-03, POL-16 | Specified |
| SCR-PCTX-16 | Rewrite wizard: Drivers | POL | POL-04, POL-16 | Replaced, Specified |
| SCR-PCTX-17 | Rewrite wizard: Vehicles | POL | POL-05, POL-16 | N/A, Specified |
| SCR-PCTX-18 | Rewrite wizard: PA Coverages | POL | POL-06, POL-16 | N/A, Specified |
| SCR-PCTX-19 | Rewrite wizard: Risk Analysis | UW | UW-02 | Specified |
| SCR-PCTX-20 | Rewrite wizard: Quote | POL | POL-07, POL-16 | Specified |
| SCR-PCTX-21 | Rewrite wizard: Policy Review | POL | POL-12, POL-16 | Specified |
| SCR-PCTX-22 | Rewrite wizard: Rewrite Remainder of Term Bound | POL | POL-16 | Specified |
| SCR-PCTX-23 | Renewal wizard: Offerings | POL | POL-17 | Specified |
| SCR-PCTX-24 | Renewal wizard: Policy Info | POL | POL-17 | Specified |
| SCR-PCTX-25 | Renewal wizard: Policy Review | POL | POL-17 | Specified |
| SCR-PCTX-26 | Renewal wizard: View Quote | POL | POL-17 | Specified |
| SCR-PCTX-27 | Policy Change wizard: Policy Info | POL | POL-03, POL-13 | Specified |
| SCR-PCTX-28 | Policy Change wizard: Issues that block Issuance | UW | UW-04 | Specified |
| SCR-PCSUB1-01 | Submission wizard: Offerings | POL | POL-02 | Specified |
| SCR-PCSUB1-02 | Submission wizard: Qualification (PA Pre-Qualification) | POL | POL-02 | Replaced, Specified |
| SCR-PCSUB1-03 | Submission wizard: Drivers | POL | POL-04 | N/A, Replaced, Specified |
| SCR-PCSUB1-04 | Submission wizard: Vehicles | POL | POL-05 | N/A, Specified |
| SCR-PCSUB1-05 | Location Information (popup page from Vehicles) | POL | POL-05 | Replaced |
| SCR-PCSUB1-06 | Submission wizard: Risk Analysis | UW | UW-02 | Replaced, Specified |
| SCR-PCSUB1-07 | Submission wizard: PA Coverages | POL | POL-06 | N/A, Specified |
| SCR-PCSUB1-08 | Submission wizard: Policy Info | POL | POL-03, POL-09 | Specified |
| SCR-PCSUB1-09 | Submission wizard: Policy Review | POL | POL-07 | Replaced |
| SCR-PCSUB1-10 | Submission wizard: Quote | POL | POL-07, POL-08 | Specified |
| SCR-PCSUB1-11 | Issues that block Issuance | UW | UW-04 | Specified |
| SCR-PCSUB1-12 | Activity worksheets (New Activity / Activity Detail) in wizard | WRK | WRK-04, WRK-05 | (target named) |
| SCR-PCSUB1-13 | PolicyCenter application shell and global menus (as seen on wizard pages) | PLT | PLT-26 | Specified |
| SCR-PCSUB2-01 | Submission Bound (confirmation) | POL | POL-08 | Specified |
| SCR-PCSUB2-02 | Risk Approval Details | UW | UW-03 | Replaced, Specified |
| SCR-PCSUB2-03 | Submission Declined | UW | UW-12 | Specified |
| SCR-PCSUB2-04 | Line Selection (Commercial Package) | POL | POL-02 | Specified |
| SCR-PCSUB2-05 | Forms (submission wizard) | DOC | DOC-11 | Specified |
| SCR-PCSUB2-06 | Documents (submission wizard) | DOC | DOC-10 | Replaced, Specified |
| SCR-PCSUB2-07 | Workplan (submission) | WRK | WRK-07 | (target named) |
| SCR-PCSUB2-08 | Notes (submission) incl. New Note worksheet | WRK | WRK-10 | (target named) |
| SCR-PCSUB2-09 | Primary Named Insured (Contact Detail / Roles / Addresses) | PTY | PTY-08 | Replaced |
| SCR-PCSUB2-10 | New Driver | POL | POL-04 | N/A, Replaced |
| SCR-PCSUB2-11 | Pre-Quote Issues | UW | UW-04 | Specified |
| SCR-PD1-01 | Product Designer Login | PLT | PLT-01 | Replaced |
| SCR-PD1-02 | Product Model Home (tiles) | PFC | PFC-01, PFC-02, PFC-09, PFC-13, PFC-19 | N/A, Replaced |
| SCR-PD1-03 | Policy Lines List | PFC | PFC-01, PFC-02 | Replaced |
| SCR-PD1-04 | Policy Line (Basics) | PFC | PFC-02 | Replaced, Specified |
| SCR-PD1-05 | Policy Line Coverages List | PFC | PFC-04 | Specified |
| SCR-PD1-06 | Add Coverage (dialog) | PFC | PFC-04 | Replaced |
| SCR-PD1-07 | Coverage (Basics) | PFC | PFC-04 | Replaced, Specified |
| SCR-PD1-08 | Changes Side Panel | PFC | PFC-13 | Specified |
| SCR-PD1-09 | Changes (Change Review Page) | PFC | PFC-13 | Replaced, Specified |
| SCR-PD1-10 | Settings Menu and Synchronize Product Model Dialog | PFC | PFC-13, PFC-16 | N/A, Replaced |
| SCR-PD1-11 | Coverage Terms List | PFC | PFC-05 | Specified |
| SCR-PD1-12 | Add Term (dialog) | PFC | PFC-05 | N/A, Replaced |
| SCR-PD1-13 | Coverage Term (Basics) | PFC | PFC-05 | N/A, Specified |
| SCR-PD1-14 | Coverage Term Options (list + Add Option dialog) | PFC | PFC-05 | Replaced, Specified |
| SCR-PD1-15 | Coverage Term Option (Basics) | PFC | PFC-05 | Specified |
| SCR-PD1-16 | Coverage Term Availability | PFC | PFC-06 | Replaced, Specified |
| SCR-PD1-17 | Coverage Availability | PFC | PFC-06 | Specified |
| SCR-PD2-01 | Coverage Term Option: Availability | PFC | PFC-06 | Specified |
| SCR-PD2-02 | Policy Line: Exclusions (list and Add Exclusion popup) | PFC | PFC-07 | Replaced, Specified |
| SCR-PD2-03 | Exclusion (detail) | PFC | PFC-07, PFC-16 | Replaced, Specified |
| SCR-PD2-04 | Policy Line: Conditions (list and Add Condition popup) | PFC | PFC-07 | Specified |
| SCR-PD2-05 | Condition (detail) | PFC | PFC-07 | Specified |
| SCR-PD2-06 | Policy Line: Categories | PFC | PFC-07 | Specified |
| SCR-PD2-07 | Products (list) | PFC | PFC-01 | Specified |
| SCR-PD2-08 | Product (basics) | PFC | PFC-02 | Specified |
| SCR-PD2-09 | Product: Question Sets | PFC | PFC-09 | Specified |
| SCR-PD2-10 | Product: Offerings | PFC | PFC-08 | Specified |
| SCR-PD2-11 | Offering: Selections | PFC | PFC-08 | Specified |
| SCR-PD2-12 | Coverage: Offerings | PFC | PFC-04, PFC-08 | Replaced, Specified |
| SCR-PD2-13 | Product: Availability | PFC | PFC-06 | Specified |
| UIL-U1 | Underwriter workbench and referral queue | UW | UW-01 | Specified |
| UIL-U2 | Submission intake, triage and routing | UW | UW-06, UW-20 | Specified |
| UIL-U3 | Risk and account review (summary, risk details, loss history, sanctions, documen | UW | UW-05 | Specified |
| UIL-U4 | Approve or decline with authority check | UW | UW-03, UW-12 | Specified |
| UIL-U5 | Decline or refusal letter (Greek nat-cat refusals) | UW | UW-12, UW-13 | Specified |
| UIL-U6 | Underwriting authority administration | PLT | PLT-07 | Specified |
| UIL-U7 | Product configuration | PFC | PFC-01, PFC-02, PFC-11, PFC-15 | Specified |
| UIL-U8 | Rate tables and rating factors | RAT | RAT-02, RAT-03, RAT-04, RAT-14 | Specified |
| UIL-U9 | Product and rate version approval | PFC | PFC-11, PFC-15 | Specified |
| UIL-U10 | Rate testing and impact analysis | RAT | RAT-05, RAT-06, RAT-07, RAT-08 | Specified |
| UIL-U11 | Document template and clause editor | DOC | DOC-01, DOC-03, DOC-07 | Specified |
| UIL-C1 | First notice of loss (staff intake) | CLM | CLM-01 | Specified |
| UIL-C2 | Adjuster workspace and claim summary | CLM | CLM-02, CLM-03 | Specified |
| UIL-C3 | Exposures (coverage × claimant) | CLM | CLM-05 | Specified |
| UIL-C4 | Reserves | CLM | CLM-06 | Specified |
| UIL-C5 | Payments | CLM | CLM-07 | Replaced, Specified |
| UIL-C6 | Recoveries and subrogation (incl. Friendly Settlement) | CLM | CLM-08, CLM-09 | Specified |
| UIL-C7 | Claim diary and tasks (incl. statutory 3-month clock) | CLM | CLM-10 | Specified |
| UIL-C8 | Claim documents and photos | CLM | CLM-11 | Specified |
| UIL-C9 | Vendor and adjuster assignment | CLM | CLM-12 | Specified |
| UIL-C10 | Reserve and payment approval inbox | CLM | CLM-14 | Specified |
| UIL-C11 | Catastrophe event management | CLM | CLM-01, CLM-15 | Specified |
| UIL-C12 | Fraud alert and SIU case | CLM | CLM-16 | Specified |
| UIL-C13 | Customer claim reporting and tracking | CHN | CHN-09, CHN-10, CHN-11 | Specified |
| UIL-B1 | Billing account summary (staff view) | BIL | BIL-01, BIL-02 | Specified |
| UIL-B2 | Unapplied cash and suspense | BIL | BIL-05 | Specified |
| UIL-B3 | Payment exceptions and returns (SEPA returns, chargebacks) | BIL | BIL-06 | Specified |
| UIL-B4 | Refund approval and disbursement | BIL | BIL-07 | Specified |
| UIL-B5 | Write-off | BIL | BIL-08 | Specified |
| UIL-B6 | Delinquency and dunning | BIL | BIL-09 | Specified |
| UIL-B7 | Intermediary collection and account current | BIL | BIL-11 | Specified |
| UIL-B8 | Bank reconciliation | BIL | BIL-13 | Specified |
| UIL-F1 | Journal and GL posting review | FIN | FIN-02, FIN-06 | Specified |
| UIL-F2 | Period close | FIN | FIN-04 | Specified |
| UIL-F3 | Premium tax and Auxiliary Fund levy returns | FIN | FIN-09 | Specified |
| UIL-F4 | IFRS 17 and Solvency II reporting | FIN | FIN-07, FIN-12 | Specified |
| UIL-F5 | Commission plans and statements | BIL | BIL-12 | Specified |
| UIL-R1 | Treaty and programme set-up | RI | RI-02, RI-03, RI-04, RI-05 | Specified |
| UIL-R2 | Cession review | RI | RI-06, RI-07 | Specified |
| UIL-R3 | Reinsurance recoveries | RI | RI-09, RI-10, RI-11, RI-12 | Specified |
| UIL-R4 | Bordereaux and statement of account | RI | RI-15, RI-16, RI-17, RI-20 | Specified |
| UIL-K1 | Complaints case management (50-day reply clock) | CMP | CMP-05 | Specified |
| UIL-K2 | Regulatory report review (Solvency II QRTs) | CMP | CMP-08 | Specified |
| UIL-K3 | Fiscal document rejection queue (myDATA analogue) | CMP | CMP-03 | Specified |
| UIL-K4 | GDPR subject access requests | CMP | CMP-06 | Specified |
| UIL-K5 | Audit log viewer | PLT | PLT-09 | Specified |
| UIL-I1 | Staff user and role administration | PLT | PLT-02, PLT-06 | Specified |
| UIL-I2 | Batch and workflow monitor | PLT | PLT-10 | Specified |
| UIL-I3 | Integration and message queue monitor | PLT | PLT-11 | Replaced, Specified |
| UIL-I4 | ICT incident log and DORA reporting | PLT | PLT-19 | Specified |
| UIL-P1 | Quote and buy (motor, home) | CHN | CHN-01, CHN-02, CHN-03 | Specified |
| UIL-P2 | Payments and billing | CHN | CHN-02, CHN-07 | Specified |
| UIL-P3 | Policy documents | CHN | CHN-05, CHN-08 | Specified |
| UIL-P4 | Online withdrawal button (EU, from June 2026) | CHN | CHN-13 | Specified |
| UIL-A1 | Quote, submission, pre-quote UW issues, rewrite, withdraw | CHN | CHN-17, CHN-18, CHN-25 | Specified |
| UIL-A2 | Policy changes, out-of-sequence changes, cancel, reinstate, renewals | CHN | CHN-06, CHN-19 | Specified |
| UIL-A3 | Desktop, activities, Team tab, notes, account merge and move | CHN | CHN-16, CHN-24 | Specified |
| UIL-A4 | Commercial quoting, referrals, contingencies, issuing | CHN | CHN-05, CHN-17, CHN-18 | Specified |
| UIL-A5 | Billing accounts, pay plans, payments, refunds, notices | CHN | CHN-22, CHN-23 | Specified |
| UIL-A6 | Agency users, producers and commissions | CHN | CHN-22, CHN-24 | Specified |

## 4. B.4 Inspiration-board patterns cited per PRD

Mechanical count of `IB-nn` mentions in each PRD (patterns from `00-baseline-inventory.md` §E, IB-01…IB-33), 2026-10-07.

| PRD | Screens (SCR) | Count cited | Not cited |
|---|---|---|---|
| PTY | 23 | 26 | IB-10, IB-11, IB-20, IB-22, IB-23, IB-31, IB-33 |
| PFC | 20 | 24 | IB-04, IB-11, IB-13, IB-15, IB-18, IB-25, IB-27, IB-30, IB-31 |
| RAT | 15 | 22 | IB-04, IB-06, IB-10, IB-15, IB-18, IB-22, IB-23, IB-25, IB-27, IB-31, IB-33 |
| UW | 20 | 30 | IB-06, IB-22, IB-30 |
| POL | 21 | 25 | IB-04, IB-06, IB-15, IB-20, IB-23, IB-25, IB-27, IB-30 |
| BIL | 17 | 26 | IB-06, IB-15, IB-20, IB-25, IB-27, IB-30, IB-31 |
| CLM | 20 | 26 | IB-06, IB-11, IB-20, IB-22, IB-27, IB-30, IB-31 |
| RI | 20 | 30 | IB-18, IB-22, IB-27 |
| FIN | 14 | 27 | IB-01, IB-06, IB-25, IB-27, IB-31, IB-33 |
| DOC | 17 | 26 | IB-06, IB-13, IB-15, IB-25, IB-27, IB-30, IB-31 |
| CMP | 11 | 23 | IB-13, IB-18, IB-19, IB-22, IB-23, IB-25, IB-27, IB-30, IB-31, IB-33 |
| CHN | 32 | 19 | IB-04, IB-06, IB-09, IB-10, IB-12, IB-14, IB-15, IB-17, IB-19, IB-25, IB-27, IB-30, IB-31, IB-33 |
| WRK | 26 | 24 | IB-06, IB-13, IB-15, IB-20, IB-22, IB-25, IB-27, IB-31, IB-33 |
| PLT | 28 | 25 | IB-06, IB-10, IB-13, IB-20, IB-22, IB-25, IB-27, IB-31 |
| DAT | 18 | 27 | IB-10, IB-14, IB-18, IB-19, IB-31, IB-33 |
| MIG | 10 | 26 | IB-10, IB-18, IB-22, IB-25, IB-27, IB-30, IB-33 |
| MKT | 12 | 24 | IB-06, IB-13, IB-18, IB-22, IB-23, IB-25, IB-27, IB-30, IB-33 |

PRDs citing each pattern (17 PRDs max). Range ends at line 8944 = IB-20; rows IB-21…IB-33 lie after the range and were not read:

IB-01=16, IB-02=17, IB-03=17, IB-04=13, IB-05=17, IB-06=6, IB-07=17, IB-08=17, IB-09=16, IB-10=11, IB-11=14, IB-12=16, IB-13=11, IB-14=15, IB-15=10, IB-16=17, IB-17=16, IB-18=10, IB-19=14, IB-20=11

Observations: UW and RI cite the most patterns (30 each); CHN the fewest (19) despite the most screens (32); MIG has fewest screens (10). IB-02/03/05/07/08/16 are cited by all 17 PRDs; IB-06 is least cited in range (6: PTY, PFC, RI, CMP, DAT, MIG); FIN is the only PRD not citing IB-01. Patterns also surface inside B.1 rows: IB-11 work-left home (BIL-323 billing ops home, CHN-120 customer dashboard, CHN-180 broker dashboard), IB-03 sidebar (CHN-180), IB-05 split-pane (CHN-182), IB-13 statement view (CHN-133), IB-32 binary status (CHN-309).
