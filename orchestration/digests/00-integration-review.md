# Digest: 00-integration-review.md (cross-PRD integration review)

Source: `C:\Users\Karl\Projects\coreinsurance\core-insurance-prds\00-integration-review.md` (1,531 lines, read in full to EOF). This is not a module PRD, so the module template sections (entities, screens, size metrics and so on) are replaced by the five sections the orchestrator asked for. Line references ("l.") are to the review file unless a PRD is named.

**Two status layers.** These are kept apart throughout:
- **Review status** is the status the review itself records at close. The contract was then at **v1.10** (rulings R-01 … R-100).
- **On-disk spot-check (2026-10-07)** is my own grep of the current PRDs and contract. It is not part of the review. The contract on disk is now **v1.12, "Frozen with programme baseline 1.0"**. Most PRDs carry a "Freeze fix (contract v1.11, PRD-18)" note. PRD-18 §20 turned the review's open Minor items into change requests (`XMR-CR-*`), and most of them now appear applied. Where I checked an item, the result is shown as "Disk:".

---

## 1. Purpose and method

**Purpose (l.1–9).** The review checks PRD-01 … PRD-17 against `00-system-contract.md` and `00-baseline-inventory.md` along six dimensions from orchestration brief §6:
1. coverage of the baseline inventory;
2. cross-references, events and interfaces;
3. consistency of entities, states, enumerations and terms;
4. AI compliance;
5. regulatory coverage;
6. styling leaks.

**Verdict at close (l.9).** "No blocking issues remain." One Major item from round 3 (R3-001) was fixed after the round. The remaining Minor items are listed in §5 and carried into the PRD-18 change requests.

**Reviewers (l.7).** Seven reviewer agents, none of which wrote a PRD:
- four round-1 reviewers: COV; XRF; CON; AIC/REG/STY;
- two round-2 verifiers;
- one round-3 verifier.

**Method (§1, l.11–17).**
1. **Mechanical pre-scan** (`_work/integration_scan.py`, `_work/check_prds.py`) before every round. It checked:
   - section structure, requirement counts and tags;
   - contract anchors and dangling citations;
   - §15 dependency reciprocity;
   - events consumed without a producer, or produced twice;
   - duplicate entity headings;
   - AI governance keywords, obligation mentions, styling and vendor words;
   - field-level inventory label matching.
2. **Round 1.** Four parallel reviewers, one per dimension. Each finding carries a severity (Blocking/Major/Minor), evidence, a proposed fix and a fix owner (Appendices A–D).
3. **Design-authority rulings.** Every cross-module question was decided centrally in the contract (v1.8: R-78 … R-92). Owners fixed their PRDs in place and kept requirement IDs stable.
4. **Round 2.** Two fresh verifiers re-checked every round-1 finding and swept for regressions (Appendices E–F). The new rulings were R-93 … R-100.
5. **Round 3.** One fresh verifier re-checked every round-2 item and every partly resolved round-1 item, then ran a final sweep for Blocking-class problems (Appendix G).

Appendix H is the mechanical scan output at close.

**Results by round (§2, l.21–25).**

| Round | Raised | Blocking | Major | Minor | Close of round |
|---|---|---|---|---|---|
| 1 | 98 (COV 6, XRF 22, CON 36, AIC 17, REG 15, STY 2) | 4 | 42 | 52 | All 4 Blocking resolved in fix round 1 |
| 2 | 16 (R2A 12, R2B 4) | 0 | 6 | 10 | Round-1 items: 86 Resolved, 11 Partially resolved, 1 Open (later fixed) |
| 3 | 10 (R3-001 … R3-010) | 0 | 1 | 9 | 28 re-checked: 19 Resolved, 9 Partially resolved (all Minor), 0 Open; R3-001 fixed after round 3 |

Two counting notes:
- The round-1 CON appendix headline says "2 Blocking, 20 Major, 14 Minor" (l.302), but its own table rows sum differently.
- The AIC appendix says "Blocking 1, Major 12, Minor 21. Total 34" (l.441).

The §2 totals are the review's official figures.

**Final status by dimension (§3, l.38–45).**

| Dimension | Result at close |
|---|---|
| Coverage | **Screen level: 100%** of 218 owned items (158 reference screens and 60 UIL rows). 200 are specified and 18 wholly replaced. **Field level: 100%** after fix round 1 (2,184 field rows). **Actions:** 1,180 items, all accounted for |
| Cross-references | 0 dangling citations (13,000+ occurrences); 174+ contract anchors all defined; 0 non-reciprocated §15 pairs; 0 consumed events without a producer; 0 events with two producers. Residual Minor: 119 producer consumer-list entries naming a module with no handler (R3-002) |
| Consistency | 0 entities owned by two PRDs (seven collisions resolved by R-82). Canonical states conform. Code lists unified (R-84, R-99). All clock codes are in the CMP register with a kind. All document types are in the DOC catalogue (53 codes). Phase and MoSCoW vocabulary normalised (R-86). No Must depends on a later-phase or Could/Won't requirement |
| AI compliance | 119 features, each with every §3.8.4 item, in both registers (PRD-11 §11.9 and PRD-15 §11.3). 42 run under high-risk-level controls. No hard boundary crossed. Every PRD works with AI off. No Must depends on AI |
| Regulatory | All 20 obligations met by Must requirements. PRD-11 §15.2 merged traceability covers all 17 PRDs. UNVERIFIED facts are tracked as open issues (see PRD-18) |
| Styling | None. Legitimate exceptions only: "Green Card", the WCAG non-colour rule, DOC font-set rules, and component names allowed by R-98 |

**Contract changes caused by the review (§4, l.49).** Recorded in contract §3.10:
- v1.8: R-78 … R-92;
- v1.9: R-93 … R-95;
- v1.10: R-96 … R-100;
- plus the post-round correction of the quote performance budget (R3-006).

**Key rulings referenced in the review.**

| Ruling | What it decided |
|---|---|
| R-78 | Clock kinds DEADLINE and WAITING_PERIOD, state Elapsed, event `ClockElapsed` |
| R-79 | AI-PLT-02 redesigned as an entitlement recommender |
| R-82 | Entity renames and single owners |
| R-83 | No synchronous T2 call at bind (event-fed gate views) |
| R-84 | Channel, cancellation-source and refund-method code lists |
| R-85 | IPT liability-point key; Cyprus fixture; nat-cat motor floor |
| R-86 | P1–P4 and four-word MoSCoW |
| R-87 | The owner's operation name is canonical; contract §3.4.2 consumer column is indicative |
| R-88 | UW import scope for migration |
| R-89 | Coexistence consumers (later superseded by R-96) |
| R-91 | Removed pill, badge, skeleton and spinner |
| R-93 | Non-EU transfers allowed with a Chapter V basis |
| R-94 | PLT `IctIncident` |
| R-95 | CHN `DisclosureReceiptRecorded` |
| R-96 | Event-fed routing cache, with `mig.Routing.resolve` authoritative |
| R-97 | Lakehouse erasure completes synchronously; the event is evidence only |
| R-98 | Interaction-pattern component names allowed |
| R-99 | One channel grouping (CHN); BIL term "refund payout method" |
| R-100 | Each producer declares its partition keys; no contract key table |

Earlier rulings cited: R-12 (staff monitoring), R-15 and R-39 (keys), R-38 (BIL `PaymentInstrument`), R-42 (FS SPI), R-55 (full refund on distance withdrawal), R-57 (EAA applies), R-59, R-60 (one owner per clock), R-67 (monitoring profiles), R-68 (`StatutoryDataReturnFormat`), R-69, R-70 (MIG events).

---

## 2. Every finding with resolution status

Legend:
- **R** = Resolved.
- **PR** = Partially resolved.
- **Ruling** = the contract ruling or version that settled the item.
- **Final** = status at review close.
- **Disk** = my on-disk spot-check (§0).

### 2.1 Items NOT fully resolved at review close (exhaustive)

These are §5 of the review (l.51–59) plus every item that round 3 left Partially resolved.

| ID | Sev. | Modules | Description | Review status at close | Fix asked for | Disk (2026-10-07 spot-check) |
|---|---|---|---|---|---|---|
| **R3-001** | Major | POL, MKT | `PackRolledBack` had no POL handler or requirement. MKT names POL as a consumer and as the remediation example. Policies quoted or issued in a rolled-back window carry hashes of the withdrawn pack version, and no requirement assigned who finds and fixes them | **Fixed after round 3** (l.9) | REQ-POL-354: consume the event; list open jobs and bound transactions in the window; re-quote open jobs; raise a WRK activity per bound transaction for reverse-and-reapply under maker-checker, never automatic | Fixed: REQ-POL-354 exists (PRD-05 l.589, l.1762) |
| **R3-002** (= XRF-013 residue) | Minor | All producers; esp. CMP, BIL, FIN, CLM, POL, WRK | Producer §8.1 consumer lists name 119 module/event pairs with no §8.2 handler: 86 plain pairs, plus 33 CMP rows saying "intended consumers … to add §8.2 handlers" (R-59). Full list: App. G §3 (l.1332–1383). Under contract l.530 these lists are indicative, and no statutory flow breaks | **Open** (carried to PRD-18 §20) | Producers trim their lists, or consumers add handlers. At least BIL/FIN for `FiscalDocCancelled` and POL for `BureauFactRejected` | Partly applied: POL `PolicyIssued` consumers are now CHN, CMP, PTY, UW, WRK, DAT (BIL/DOC/FIN/RI removed). PRD-18 §20.3 records R3-002 as "Open (10 CMP rows; plain pairs unchanged)" at the time it was written. Not fully re-verified |
| **R3-003(a)** (= R2A-011 residue) | Minor | POL, DOC | Crossed fix: DOC calls `pol.Policy.getMany` (REQ-DOC-137), while REQ-POL-351 said "no PRD calls it" | **Fixed with R3-001** (l.55) | REQ-POL-351 / §15 list DOC as caller | Fixed (per PRD-18 §20.3; phrase absent from PRD-05) |
| **R3-003(b)** (= R2A-010 residue) | Minor | PLT, MKT | PLT added `RC-MKT-CFG` to retention table B, but MKT uses `RC-CFG` and defines no `RC-MKT-CFG` | **Open** | Delete the PLT row | Fixed: PRD-14 l.2135 says the `RC-MKT-CFG` row is withdrawn and MKT uses `RC-CFG` |
| **R3-003(c)** (= XRF-014 residue) | Minor | FIN, WRK | WRK handles `PeriodReopened` (pattern FIN-PERIOD-REOPENED), but FIN §8.1 listed only DAT | **Open** | FIN adds WRK | Fixed: PRD-09 l.1476 lists POL, WRK, DAT |
| **R3-004** | Minor | CLM, MKT | REQ-CLM-155 cites "PRD-17 §9.4.40", which PRD-17 says is unused. The FS extension sits in §9.4.43 (base §9.4.23). This was the only dangling section reference | **Open** | Cite "§9.4.23, extended in §9.4.43" | Fixed (PRD-07 l.616) |
| **R3-005** (= XRF-016 / R2A-012(7) residue) | Minor | PTY, CLM, contract | Partition-key text does not match the rulings. PRD-01 said a per-module key table was "pending with the orchestrator", although R-100 said there would be none. PRD-07 keyed `ClaimsHistoryCertificateIssued` on requester party_id and Cat events on `cat_event_id` and cited R-39, but R-39 says "CLM partition key is claim_id" | **Open** | PRD-01: "declared per R-100". PRD-07: certificate on `certificate_id` and cat events on `cat_event_id`, per R-100. Contract: R-39 → "claim_id for claim-scoped events" | Fixed: PRD-01 l.1567 "declared per R-100"; PRD-07 l.1420 `certificate_id (per R-100)`; contract l.1371 records the R-39 amendment |
| **R3-006** | Minor | Contract | (a) Contract §3.9.7 still read "quote (rate + UW evaluate + documents preview) ≤ 2 s", contradicting NFR-POL-001 and NFR-DOC-018. (b) §3.10.9 listed R-100 before R-99 | **Corrected post-round** (l.49) | Contract: "quote (rate + UW evaluate + POL data preview) ≤ 2 s; quote-document preview on demand ≤ 700 ms (DOC)"; reorder | Fixed (PRD-18 §20.3; "documents preview" no longer in the contract) |
| **R3-007** (= XRF-008 / R2A-001 residue) | Minor | CHN, MIG | CHN cites the routing read by anchor `REQ-MIG-004` and R-89, not by operation `mig.Routing.resolve` / `REQ-MIG-149` and R-96 | **Open** | Cite the operation in §8.2, §9.2 and §15 | Fixed: PRD-12 l.546, l.1797, l.1940, l.2420 cite REQ-MIG-149 and R-96 |
| **R3-008** (= AIC-011 residue) | Minor | CMP, PFC, DAT | The CMP register §11.9 gives AI-PFC-04 bias "No (aggregate)". The owner and DAT say "bias check on region proxies" | **Open** | Fix the CMP cell | Fixed (PRD-11 l.1657) |
| **R3-009** (= R2A-012(3) residue + new) | Minor | RAT, BIL, MKT | Four wording residues: (a) RAT prose says the Cyprus `CY-STAMP` charge is "zero from 2026-01-01", but its GWT and MKT/PFC say "no stamp line"; (b) BIL l.209 "Posting-rule table"; (c) REQ-MKT-327 example groupings "intermediated, direct, bank" are not CHN's group codes; (d) PRD-17 l.1050 cites closed OI-MKT-12 | **Open** | Correct each | (a) Fixed in PRD-03 l.1374 ("no stamp line"); (b) fixed (phrase gone from PRD-06); (c) "intermediated, direct, bank" no longer in PRD-17 but still in **PRD-02 l.394** (not named by the review); (d) not re-checked |
| **R3-010** | Minor | RAT/POL, BIL/FIN | Two technical table names were each defined in §7.1 of two PRDs: `IdempotencyRecord` (PRD-03, PRD-05) and `InvariantResult` (PRD-06, PRD-09). Not an ownership conflict, but it weakens "one name, one owner" | **Open** | Mark them module-internal, or prefix them | Fixed: `RatingIdempotencyRecord`, `PolicyCommandIdempotencyRecord`, `BillingInvariantResult`, `LedgerInvariantResult` |
| Doc-control versions | Minor | Many | Document-control version cells in several PRDs cite older contract versions (l.59) | **Open** | XMR-CR-ALL-01 | Not verified; Freeze-fix notes suggest it was applied |
| COV-006 | Minor | PLT (CLM, POL done) | PLT has no general grid-conventions line saying that row select and column chooser satisfy baseline references | **PR, optional, "no action required"** (l.1287) | Optional one-liner | Not checked |
| XRF-008 / R2A-001 | Major | MIG + BIL, CHN, CLM, CMP, DOC, WRK, DAT | Coexistence events: producer and consumer lists contradicted each other | **PR**. Producer and consumers agree under R-96; only CHN's citation lagged (→ R3-007) | — | Fixed via R3-007 |
| XRF-013 | Minor | All producers | Over-inclusive consumer lists | **PR** → R3-002 | — | See R3-002 |
| XRF-014 | Minor | FIN/WRK | Handlers not listed by their producer: 174 → 11 → 1 | **PR** → R3-003(c) | — | Fixed |
| XRF-016 | Minor | PTY, CLM, contract | Partition keys | **PR** → R3-005 | — | Fixed |
| R2A-010 | Minor | CHN, CMP, MKT, PLT | Stale statuses and code references | **PR** → R3-003(b) | — | Fixed |
| R2A-011 | Minor | RI, POL, DOC | Consumer-list mismatches | **PR** → R3-003(a) | — | Fixed |
| R2A-012 | Minor | Several | Grouped residues; (3) and (7) remained | **PR** → R3-009(a), R3-005 | — | Fixed |

**Net position.**
- At review close, the still-open items were R3-002, R3-003(b), R3-003(c), R3-004, R3-005, R3-007, R3-008, R3-009(a–d), R3-010, the document-control versions, and optional COV-006.
- On disk today, nearly all are applied. The ones that remain open or unverified are:
  - **R3-002**: consumer-list trimming is only partly verifiable;
  - the **"intermediated, direct, bank"** wording, which still exists in PRD-02 l.394;
  - **R3-009(d)**: not checked;
  - **document-control versions**: not checked;
  - **COV-006**: optional.

### 2.2 Round-1 Blocking findings (all Resolved)

| ID | Modules | Description | Resolution |
|---|---|---|---|
| CON-001 | CLM, BIL | Payee bank account defined twice (CLM `PayeeAccount` and BIL `PaymentInstrument`) | R-38/R-82. BIL `PaymentInstrument` is the only record. CLM keeps `PayeeAccountView` and calls BIL. OI-CLM-10 closed. R in round 2 |
| CON-002 | CMP, BIL, POL (CHN, CLM, WRK) | Clock terminal states had opposite meanings. Non-payment cancellation could never fire, and every uncured case logged a false statutory breach. The same applied to the objection and withdrawal clocks | R-78: kinds DEADLINE and WAITING_PERIOD, state Elapsed, event `ClockElapsed`. All 38 clocks have a kind. BIL cancels on `ClockElapsed` (REQ-BIL-006/172). POL: REQ-POL-310/311. R in round 2; follow-on R2A-002 resolved in round 3 |
| XRF-005 | MKT, DAT, CMP | SPI `StatutoryDataReturnFormat` (R-68) was never specified | REQ-MKT-326, PRD-17 §9.4.42 (operations `returnTypes`, `layout`, `build`, `validate`; default `NotRequired`; axis LEGAL_ENTITY_HOME). Cited by DAT and CMP. R; traceability residue R2A-009 resolved |
| AIC-001 | PLT (DAT) | AI-PLT-02 scored individual employees (hard boundary 6; age-band bias tests) | R-79: redesigned as an entitlement right-sizing recommender on role/permission aggregates (organisational units with at least 10 holders); no per-person data; no age-band tests. R in round 2 |

### 2.3 Round-1 COV findings

| ID | Sev. | Modules | Description | Final |
|---|---|---|---|---|
| COV-001 | Major | PLT (+WRK, UW, PTY, MKT, MIG) | Six Administration › Utilities items (Import/Export Data, Script Parameters, Spreadsheet Export Formats, Inbound/Outbound Files) had no disposition | R (round 2): PRD-14 maps them to REQ-MIG-072, REQ-PLT-345…349, -098/352, SCR-PLT-13/REQ-MKT-001, REQ-PLT-351 |
| COV-002 | Minor | POL, PTY | Account-level accident/violation count had no home | R (POL SCR-POL-04 derived row from CLM read model) |
| COV-003 | Minor | PTY, POL | Training class: N/A in PTY, specified in POL | R (owned by POL) |
| COV-004 | Minor | Inventory | "52" vs 60 UIL rows | R |
| COV-005 | Minor | CLM | No "Mark work complete" action for vendor services | R (REQ-CLM-262) |
| COV-006 | Minor | PLT, CLM, POL | Cosmetic grid labels covered only implicitly | PR, optional (see §2.1) |

### 2.4 Round-1 XRF findings (interfaces; see also §3)

| ID | Sev. | Description | Final |
|---|---|---|---|
| XRF-001 | Major | BIL called `cmp.FiscalDocument.submit`; CMP defines `request` | R |
| XRF-002 | Major | `mkt.Config.resolve` / `resolveModel` vs `mkt.Configuration.resolve` | R |
| XRF-003 | Major | `pol.PolicyChange.bind` vs `pol.Job.bind` | R (`pol.Job.bind`) |
| XRF-004 | Major | Ten more misnamed operations (a–j) | R; added `uw.DisclosureFinding.get/list` (REQ-UW-301) and `pol.Policy.getMany` (REQ-POL-351) |
| XRF-005 | Blocking | See §2.2 | R |
| XRF-006 | Major | `SignatureCompleted` had no POL handler | R (POL §8.2) |
| XRF-007 | Major | WRK had no handler for PLT `ApprovalRequested`/`Decided` or user events, so maker-checker requests would reach no queue | R (REQ-WRK-397/398/140, pattern PLT-APPROVAL) |
| XRF-008 | Major | MIG coexistence events had no consumers | PR → R2A-001 → R3-007 (fixed on disk) |
| XRF-009 | Major | `WithdrawalRequestReceived` backup trigger had no POL/CMP handler (statutory withdrawal right) | R (POL idempotent handler on `withdrawal_request_id`; REQ-CMP-253) |
| XRF-010 | Major | MIG vs UW disagreed on importing in-flight referrals and issues | R (R-88; REQ-UW-295, REQ-MIG-110) |
| XRF-011 | Minor | `LakehouseErasureCompleted` had two completion paths | Open in round 2 → R2A-003 → R (R-97) |
| XRF-012 | Minor | `POL_REFUND_DUE` stopped twice | R (event-declared stop by CMP; POL's stop is an idempotent no-op) |
| XRF-013 | Minor | 333 listed consumers without a handler | PR → R3-002 |
| XRF-014 | Minor | 174 handlers not listed by their producer | PR → R3-003(c) |
| XRF-015 | Minor | Contract §3.4.2 consumer lists stale | R (R-87: column is indicative) |
| XRF-016 | Minor | Partition keys not covered by a ruling | PR → R3-005 |
| XRF-017 | Minor | Stale "proposed / not yet written" labels | R |
| XRF-018 | Minor | SPI callers and naming (PricingConstraint, MandatoryWordingSet, StatutoryDeliveryRule, TaxReturnFormat callers, `checkEligibility`/`evaluateEligibility`) | PR → R2A-004 → R |
| XRF-019 | Minor | Five semantically wrong citations | R |
| XRF-020 | Minor | GROSS delta mode not handled by FIN/DAT | R (REQ-FIN-297, REQ-DAT-299) |
| XRF-021 | Minor | Money-moving BIL commands lacked dry-run | R |
| XRF-022 | Minor | FIN P1 Musts depended on RI P2/P3; FIN Must depended on a WRK Should | R (FIN-140/168 P2, FIN-166 P3; WRK-198 Must P1) |

### 2.5 Round-1 CON findings

| ID | Sev. | Description | Final |
|---|---|---|---|
| CON-001, CON-002 | Blocking | See §2.2 | R |
| CON-003 | Major | FIN `SuspenseItem` collided with BIL | R → FIN `IntakeException` |
| CON-004 | Major | BIL `PostingRule` collided with FIN | R → `BillingLedgerRule` (residue R2A-007, then R3-009b) |
| CON-005 | Major | RAT `EvidencePack` collided with DOC | R → `RatingGovernanceBundle` |
| CON-006 | Major | `ChangeoverPlan` in MIG and MKT | R → MKT `ChangeoverPlanView` |
| CON-007 | Major | `AiFeature` / `AiSystem` / `AiFeatureCoverage` overlap | R: CMP `AiSystem` is the system of record; PLT `AiFeatureRuntime`; DAT coverage only |
| CON-008 | Major | `ActuarialResultSet` in FIN and DAT | R → DAT `ActuarialSubmission` + `ActuarialResultSetView` |
| CON-009 | Major | Tax/levy periods modelled twice (BIL/FIN) | R: BIL `TaxLevyPeriod` covers levy remittance only; FIN `TaxReturn` holds IPT (residue R2A-007 resolved) |
| CON-010 | Major | Channel enumeration differed across PFC, CHN, PTY and BIL | R (R-84; then R2A-005 grouping → R-99) |
| CON-011 | Major | Cancellation source / refund method enumerations differed | R (R-84: 7 sources owned by POL; MinimumRetained added) |
| CON-012 | Major | Non-`DT` document codes (`BIL_INVOICE`, `CHN_WITHDRAWAL_ACK`, `CLM_RESERVATION_OF_RIGHTS`) | R (`DT-INVOICE`, `DT-WITHDRAWAL-ACK`, new `DT-RESERVATION-OF-RIGHTS`) |
| CON-013 | Major | IPT liability basis hard-coded as written in BIL; FIN used a pack rule | R (R-85, key `tax.ipt.liability_point` WRITTEN/DUE) |
| CON-014 | Major | A.1004 deadline: DAT 25 January vs CMP 10 January | R (10 January, A.1185/2024) |
| CON-015 | Major | Cyprus stamp golden fact conflicted (law repealed from 2026) | R (REQ-MKT-265 synthetic fixture with sunset) |
| CON-016 | Major | Bind (T1) called WRK (T2) synchronously | R (R-83: POL `ActivityGateView` from events) |
| CON-017 | Major | Disclosure gate depended on DOC (T2) | R (POL `DisclosureDeliveryView` / CHN `DisclosureReceiptRecorded`; gate blocks if no evidence) |
| CON-018 | Major | Quote 2 s budget included a DOC preview with no budget | PR → R2A-006 → R (preview outside the quote; DOC on-demand ≤ 700 ms) |
| CON-019 | Major | FIN MVP Musts depended on RI P2/P3 | R |
| CON-020 | Major | PTY LEI validation Should/Commercial under a P1 Must | R (REQ-PTY-051 Must P1) |
| CON-021 | Major | CHN Must ACORD mapping on a PFC Could/Should | R (CHN-217 Should; PFC-047 Must; PFC-231 Should) |
| CON-022 | Major | "Reinstatement" and "Submission" glossary clashes | R (`LayerReinstatement`; "Regulatory submission") |
| CON-023 | Minor | Greek labels inconsistent with §3.3 | R |
| CON-024 | Minor | Charge vocabulary drift (stamp category, `ACCRUE_ONLY`, IPT code) | R |
| CON-025 | Minor | RI "ALAE" not mapped to CLM cost types | R (ALAE = `ExpenseAllocated`) |
| CON-026 | Minor | Residual "refund less cost of cover" contradicted R-55 | R |
| CON-027 | Minor | PLT retention catalogue stale | R |
| CON-028 | Minor | MIG used an undefined POL suspension sub-state | R |
| CON-029 | Minor | Private-entity homonyms | R (`MigrationMatchCandidate`, `CustomerNotification`/`StaffNotification`, `LakehouseErasureTask`, `RatingChangeSet`, `RateImpactRun`, `GlAccount`, `IctIncident`) |
| CON-030 | Minor | Phase/MoSCoW vocabularies differed | R (R-86) |
| CON-031 | Minor | WRK Musts without GWT; ENHANCEMENTs without rationale | R |
| CON-032 | Minor | P1 Musts citing later-phase capabilities (e.g. POL-285 → PTY-077 geocoding) | R |
| CON-033 | Minor | Greek-specific behaviour in core requirements with no pack/SPI qualifier | R |
| CON-034 | Minor | MKT marked IPT/levy UNVERIFIED while peers had verified them | R |
| CON-035 | Minor | DAT annual IPT detail report as an MVP Must without a legal basis | R (Should, pack-optional pending OI-FIN-04) |
| CON-036 | Minor | RI reinstatement states; CLM "(proposed)" label | R |

### 2.6 Round-1 AIC / REG / STY findings

| ID | Sev. | Description | Final |
|---|---|---|---|
| AIC-001 | Blocking | See §2.2 | R |
| AIC-002 | Major | 111 features named no monitoring profile or breach response | R (all 119 have them) |
| AIC-003 | Major | 8 MIG features missing from the CMP and DAT registers | R (119 in both) |
| AIC-004 | Major | AI text in renewal letters with no per-letter human Accept | R (deterministic letters only; REQ-DOC-320 needs an Accepted `AiInteractionRecord`) |
| AIC-005 | Major | MIG AI score could feed party auto-match | R (AI can move a pair only into review) |
| AIC-006 | Major | MCP staff and broker agents sent personal data outside the EU gateway | R (REQ-CHN-264, REQ-CHN-318, BR-CHN-057) |
| AIC-007 | Major | AI-CLM-05 next-best-action classed Minimal | R (split; AI-CLM-08 high-risk controls) |
| AIC-008 | Major | AI-assistance statement was pack-conditional | R (mandatory; `doc.ai.statement_wording`) |
| AIC-009 | Major | High-risk features lacked bias metrics | R |
| AIC-010 | Minor | Thresholds and owners missing | R |
| AIC-011 | Minor | Class inconsistencies between owner and registers | PR → R in round 3 (residue R3-008, fixed on disk) |
| AIC-012 | Minor | MIG bulk accept | R (sample of 200; reject above 1%) |
| AIC-013 | Minor | AI-CHN-03 lacked disclosure; class differed from AI-PLT-05 | R |
| AIC-014 | Minor | GR/EN transparency texts missing | R |
| AIC-015 | Minor | Confidence missing from explanations | R |
| AIC-016 | Minor | Model-registry rows incomplete | R |
| AIC-017 | Minor | Steward productivity was an individual-level measure | R (team-level) |
| REG-001 | Major | §15.2 traceability omitted MIG and DAT | R |
| REG-002 | Major | OBL-AIA rows had no requirement IDs | R |
| REG-003 | Major | OBL-NATCAT refusal docs and `RefusalDocumentRule` were Should | R (REQ-DOC-043/254 Must P3; REQ-MKT-103/307 Must P1) |
| REG-004 | Major | OBL-DORA missing in PTY, POL, CLM | R (REQ-PTY-281, REQ-POL-352, REQ-CLM-260) |
| REG-005 | Minor | Missing PCI, RES, SII and NATCAT rows | R (incl. NFR-PLT-033: no card data in PLT) |
| REG-006 | Minor | EAA "Uncertain" in BIL, CHN, PLT | R (R-57); R2B-003 did the same for PFC, POL, CLM |
| REG-007 | Minor | Digital Omnibus status conflicted | PR → R in round 3 (Reg. (EU) 2026/1744, OJ 24 July 2026) |
| REG-008 | Minor | IPT verification status | R |
| REG-009 | Minor | Motor nat-cat scope (DAT) | R (businesses only; no motor floor, R-85) |
| REG-010 | Minor | Complaint-rule verification status | R |
| REG-011 | Minor | Non-EU residency rule | R (R-93, REQ-PLT-285) |
| REG-012 | Minor | UNVERIFIED rows without an OI | R |
| REG-013 | Minor | §3.1 rows without IDs | R |
| REG-014 | Minor | Obligations met only by non-Must requirements | R |
| REG-015 | Minor | §15.2 vs PRD-11 §3.1 | R |
| STY-001 | Minor | Monospace typeface instruction | R |
| STY-002 | Minor | pill / badge / skeleton / spinner | R (R-91) |

### 2.7 Round-2 findings

| ID | Sev. | Description | Final |
|---|---|---|---|
| R2A-001 | Major | Coexistence events: MIG said consumers do not subscribe, but BIL, CLM, DOC and CMP had added handlers | PR (R-96) → R3-007 (fixed on disk) |
| R2A-002 | Major | WRK had no `ClockElapsed` path, so waiting-period activities stayed open forever | R (REQ-WRK-193; CMP list now BIL, POL, CHN, CLM, WRK, DAT) |
| R2A-003 | Major | DSAR lakehouse erasure: CMP and DAT applied opposite fixes (risk to GDPR clock `CMP_DSAR_RESPONSE`) | R (R-97: synchronous completion; event is evidence only) |
| R2A-004 | Major | `checkEligibility` vs `evaluateEligibility` names had swapped sides | R (`evaluateEligibility`) |
| R2A-005 | Major | Two channel groupings (CHN vs PTY) | R (R-99: CHN REQ-CHN-317 is the only grouping: DIRECT, AGENCY, BROKER, BANCASSURANCE, AGGREGATOR) |
| R2A-006 | Major | Quote budget: DOC put its preview inside 2 s; POL put it outside | R (outside; on-demand ≤ 700 ms) |
| R2A-007 | Minor | BIL leftovers of R-82 | R |
| R2A-008 | Minor | BIL "refund method" also meant the payout route | R ("refund payout method", R-99) |
| R2A-009 | Minor | `StatutoryDataReturnFormat` traceability | R |
| R2A-010 | Minor | Stale statuses (`DisclosureReceiptRecorded`, stray codes, `RC-MKT-CFG`) | PR → R3-003(b) (fixed on disk) |
| R2A-011 | Minor | `FacPlacementBound` consumers; `getMany` callers | PR → R3-003(a) (fixed) |
| R2A-012 | Minor | Seven grouped residues | PR → R3-009(a), R3-005 (fixed on disk) |
| R2B-001 | Minor | Register drift (AI-CHN-03 class, AI-PLT-02 name, AI-DAT rows, 40 vs 42 count) | R |
| R2B-002 | Minor | Stale open issues OI-CLM-12, OI-DOC-18, OI-UW-15 | R |
| R2B-003 | Minor | EAA "Uncertain" in PFC, POL, CLM | R |
| R2B-004 | Minor | chips / toast / tooltip / drawer / banner wording | R (R-98 allows them as interaction-pattern names) |

---

## 3. Cross-module interface mismatches (events, APIs and entities named differently)

Every entry is Resolved in the review unless marked. They are listed so builders know the canonical name, and the superseded name to reject if it appears in older text.

### 3.1 API operation names (canonical name = owner's §9.1, R-87)

| Wrong name used by caller | Caller(s) | Canonical | Source |
|---|---|---|---|
| `cmp.FiscalDocument.submit` | BIL | `cmp.FiscalDocument.request` (source types `BIL_INVOICE`/`BIL_CREDIT`/`BIL_COMMISSION`, dry-run) | XRF-001 |
| `mkt.Config.resolve`, `mkt.Config.resolveModel` | BIL, CLM, FIN, MIG, PLT | `mkt.Configuration.resolve` (REQ-MKT-001) | XRF-002 |
| `pol.PolicyChange.bind` | POL acceptance criterion, CHN (incl. MCP tool `policy_change_submit`) | `pol.Job.bind(jobId, …)` | XRF-003 |
| `pty.Preferences.query` | BIL | `pty.Consent.query` / `pty.CommunicationPreference.resolve` | XRF-004a |
| `pty.Intermediary.validate` | PLT | `pty.ProducerCode.validate` | XRF-004b |
| `cmp.Submission.register` / `.track` | FIN / PLT | `cmp.Submission.create` / `attachContent` / `recordFiling` | XRF-004c |
| `pfc.Product.resolve` | MIG | `pfc.ProductVersion.resolve` | XRF-004d |
| `plt.Replay.request` | FIN | `plt.Consumer.replay`, `plt.DeadLetter.replay` | XRF-004e |
| `plt.Audit.write` | MIG | `plt.Audit.append` | XRF-004f |
| `uw.DisclosureFinding.create` / `get` | CLM | `uw.DisclosureFinding.record` / `decide`; `get` and `list` added (REQ-UW-301) | XRF-004g |
| `wrk.InboundDocument.listForObject` | DOC | `wrk.InboundDocument.list` | XRF-004h |
| `mkt.Spi.binding` | CHN, PLT | `mkt.Spi.bind` | XRF-004i |
| `pol.Policy.getMany`-style | DOC | `pol.Policy.getMany(ids, validAt)` added (REQ-POL-351); DOC now calls it (REQ-DOC-137) | XRF-004j, R2A-011, R3-003a |
| `FriendlySettlementClearing.checkEligibility` | CLM | `evaluateEligibility` (PRD-17 §9.4.23, extended §9.4.43) | XRF-018, R2A-004, R3-004 |
| Routing read cited as `REQ-MIG-004` | CHN, BIL, DOC, CMP, WRK | Operation `mig.Routing.resolve` = `REQ-MIG-149` (anchor REQ-MIG-004) | R2A-001, R3-007 |
| `wrk.Activity.blockingStatus` at bind | POL | Used only to rebuild `ActivityGateView`, never at bind (R-83) | CON-016 |

### 3.2 Event naming, consumers and keys

- **Clock terminal events.** `ClockMet` was misused for elapsed waiting periods. The correct event is **`ClockElapsed`** for WAITING_PERIOD clocks; `ClockBreached` applies only to DEADLINE clocks. "Cured" is a stop outcome that maps to the Met state. (CON-002, R-78)
- **Coexistence events.** `CoexistenceMasterChanged` and `MigrationWaveStatusChanged` are keyed on the stable `route_id`.
  - `CoexistenceMasterChanged` consumers: BIL, CHN, CLM, CMP, DOC, WRK.
  - `MigrationWaveStatusChanged` consumers: BIL, CMP, DOC, WRK, DAT.
  - The routing cache is event-fed, with `mig.Routing.resolve` authoritative. (R-96)
- **`LakehouseErasureCompleted`.** Audit evidence only, never a completion path. (R-97)
- **`DisclosureReceiptRecorded`.** A CHN event (R-95), feeding the POL disclosure gate.
- **`SignatureCompleted` / `SignatureDeclined`.** Consumed by POL for acceptance. (XRF-006)
- **PLT approval and user events.** `ApprovalRequested`, `ApprovalDecided`, `UserDeprovisioned`, `UserAccessChanged` and `AuthorityGrantChanged` are consumed by WRK (pattern PLT-APPROVAL). (XRF-007)
- **Partition keys.** R-100: each producer declares the stable id of the aggregate the event describes. Examples:
  - `PartiesMerged` / `PartyUnmerged` on the survivor `party_id`;
  - `ClaimsHistoryCertificateIssued` on `certificate_id`;
  - Cat events on `cat_event_id`;
  - FIN events on `legal_entity_id`; `Ifrs17GroupAssigned` on `assignment_subject_id`.
- **Topic naming.** `<mod>.events.v1` (App. B, l.256).
- **Consumer lists.** Producer §8.1 lists are over-inclusive (R3-002). The authority is the consumer's §8.2 handler. The full residual list is in App. G §3 (l.1332–1383).

### 3.3 Entity renames (R-82 and related)

| Old / colliding name | Module | Canonical name |
|---|---|---|
| CLM `PayeeAccount` | CLM | `PayeeAccountView` → BIL `PaymentInstrument` (REQ-BIL-343) |
| FIN `SuspenseItem` | FIN | `IntakeException` (εξαίρεση λογιστικοποίησης †); "Suspense" = BIL unapplied cash |
| BIL `PostingRule` | BIL | `BillingLedgerRule` (FIN keeps `PostingRule`) |
| RAT `EvidencePack` | RAT | `RatingGovernanceBundle` (DOC keeps `EvidencePack`) |
| MKT `ChangeoverPlan` | MKT | `ChangeoverPlanView` (MIG owns `ChangeoverPlan`) |
| PLT `AiFeature` | PLT | `AiFeatureRuntime` (CMP `AiSystem` is the system of record; DAT `AiFeatureCoverage`) |
| DAT `ActuarialResultSet` | DAT | `ActuarialSubmission` + `ActuarialResultSetView` |
| BIL `TaxLevyPeriod` (IPT part) | BIL | Levy remittance only; IPT periods and filing in FIN `TaxReturn` |
| RI `Reinstatement` | RI | `LayerReinstatement` |
| CMP "Submission" | CMP | `RegulatorySubmission` / "Regulatory submission" |
| `MatchCandidate`, `Notification`, `ErasureTask`, `ChangeSet`, `ImpactAnalysisRun`, `Account (GL)`, PLT `Incident` | MIG, CHN/WRK, DAT, RAT, FIN, PLT | `MigrationMatchCandidate`, `CustomerNotification`/`StaffNotification`, `LakehouseErasureTask`, `RatingChangeSet`, `RateImpactRun`, `GlAccount`, `IctIncident` |
| `IdempotencyRecord`, `InvariantResult` | RAT/POL, BIL/FIN | Module-internal (disk: `RatingIdempotencyRecord`, `PolicyCommandIdempotencyRecord`, `BillingInvariantResult`, `LedgerInvariantResult`) |

### 3.4 Code lists and enumerations

- **Channel codes (R-84; CHN-owned, MKT-held, REQ-MKT-327).**
  - Codes: `WEB_DIRECT`, `APP`, `BROKER_PORTAL`, `AGENT_PORTAL`, `BANK_BRANCH`, `BANK_EMBEDDED`, `PARTNER_API`, `AGGREGATOR`, `AI_AGENT`, `CONTACT_CENTRE`, plus `STAFF`.
  - Superseded spellings: `DIRECT_WEB`, "Agency", "DIRECT", "Broker".
  - **One grouping only** (R-99, REQ-CHN-317): DIRECT, AGENCY, BROKER, BANCASSURANCE, AGGREGATOR.
  - Disk residue: PRD-02 l.394 still has "intermediated, direct, bank".
- **Cancellation source (R-84, POL-owned).** Policyholder, Insurer, NonPayment, DistanceWithdrawal, LongTermWithdrawal, Objection, Statutory. Void is a kind, not a source.
- **Refund method (PFC calculation code list).** Includes MinimumRetained. BIL's payout route is a separate concept called **"refund payout method"** (R-99).
- **Document types.** Only `DT-*` codes from the PRD-10 catalogue (53 codes). `DT-RESERVATION-OF-RIGHTS` was added. CHN may request `DT-WITHDRAWAL-ACK`; POL/CHN de-duplicate per REQ-DOC-044.
- **Charges.** Stamp duty is category tax, tax class STAMP. BIL `ACCRUE_ONLY` is derived from PFC `accrued_not_billed`. IPT liability-point key: `tax.ipt.liability_point` (WRITTEN/DUE).
- **Retention.** `RC-CFG` (PLT, MKT); `RC-DAT-ANALYTIC` → `RC-DAT-MART`.
- **Cost types.** ALAE = CLM `ExpenseAllocated`.

---

## 4. End-to-end flow gaps the review identified (and how they were closed)

Each gap is a place where a business flow would silently fail. All are closed in the review; R3-001 was fixed post-round. Builders should treat each as a mandatory integration test.

| # | Flow | Gap | Closure | IDs to test |
|---|---|---|---|---|
| F1 | Non-payment → cancellation | Cured customer → `ClockMet`, no action; uncured → `ClockBreached`, ignored by BIL. Cancellation never fired and false breaches were logged | WAITING_PERIOD + `ClockElapsed` → BIL `CancellationForNonPaymentRequested` → POL. WRK closes the activity on `ClockElapsed` | REQ-BIL-006, -171, -172; REQ-POL-311; REQ-CMP-092, -106, -252; REQ-WRK-193 |
| F2 | Objection and withdrawal windows (statutory) | Expiry was treated as an insurer breach | WAITING_PERIOD; POL records the lapse on `ClockElapsed` | REQ-POL-310 |
| F3 | Distance withdrawal | No backup if `pol.Withdrawal.submit` fails after the customer confirms | POL handler on `WithdrawalRequestReceived`, idempotent on `withdrawal_request_id`; CMP evidence | REQ-CHN-168, REQ-POL-314, REQ-CMP-253 |
| F4 | Maker-checker (contract §3.9.2) | PLT approval requests reached no queue | WRK pattern PLT-APPROVAL, routed to eligible checkers, never the maker | REQ-PLT-116, REQ-WRK-397/398/140 |
| F5 | Renewal acceptance by e-signature | POL had no handler for `SignatureCompleted` | POL §8.2 row | REQ-DOC-270, REQ-POL-257 |
| F6 | Bind with WRK down | T1 bind called T2 WRK | Event-fed `ActivityGateView` | REQ-POL-175, REQ-WRK-069/402, R-83 |
| F7 | Bind with DOC down (IDD disclosure) | Gate blocked, or was silently skipped | `DisclosureDeliveryView` / CHN receipt; the gate blocks if there is no evidence; CHN shows the degraded state | REQ-POL-172, NFR-POL-009, REQ-CHN-040, REQ-CHN-316, REQ-DOC-322 |
| F8 | Quote ≤ 2 s p95 | Budget included an unbudgeted DOC preview | DOC preview is on demand (≤ 700 ms) and outside the quote; contract §3.9.7 corrected | NFR-POL-001, NFR-DOC-018, REQ-DOC-323 |
| F9 | Coexistence during migration | Routing events had no consumers, then contradictory consumers | R-96: event-fed caches + authoritative T1 `mig.Routing.resolve` | REQ-MIG-004/149, REQ-CHN-039, REQ-CLM-261, REQ-DOC-321, REQ-CMP-254 |
| F10 | DSAR erasure in DAT (GDPR Art. 12(3), clock `CMP_DSAR_RESPONSE`) | Two opposite completion paths | Synchronous completion; event = evidence | REQ-CMP-255, REQ-DAT-275, R-97 |
| F11 | Pack rollback | No POL remediation for policies priced under the withdrawn pack | REQ-POL-354 (WRK activity, maker-checker reverse-and-reapply, never automatic) | REQ-MKT-138, -149, REQ-POL-354 |
| F12 | Fiscal documents (myDATA) for billing | BIL called a non-existent operation | `cmp.FiscalDocument.request` | REQ-CMP-030…035 |
| F13 | A.1004 return | DAT mart scheduled from 25 January, after the 10 January filing date | Scheduled from `CMP_A1004_RETURN` minus lead time | REQ-DAT-125, BR-CMP-040 |
| F14 | IPT accrual vs FIN journals | BIL accrued on written basis; FIN used the pack point (DUE would break reconciliation) | One pack key `tax.ipt.liability_point` | REQ-BIL-275, REQ-FIN-178 |
| F15 | Out-of-sequence GROSS delta mode | FIN and DAT did not handle GROSS | REQ-FIN-297, REQ-DAT-299 | REQ-POL-005 |
| F16 | Nat-cat refusal (commercial) | Statutory refusal was Must, but the document and SPI were Should | Raised to Must | REQ-UW-222…231, REQ-DOC-043/254, REQ-MKT-103/307 |
| F17 | FIN P1 postings | Depended on RI P2/P3 features | Re-phased | REQ-FIN-140/166/168 |
| F18 | Garaging geocoding at P1 | Depended on Home-phase PTY | REQ-PTY-077 Must P1 | REQ-POL-285 |
| F19 | Vendor service completion | No completion action or event | REQ-CLM-262 | SCR-CLM-12 |
| F20 | Admin import/export/file monitoring | Undispositioned Utilities menu | PLT/MIG/MKT mappings | REQ-PLT-345…352, REQ-MIG-072 |
| F21 | Refund-due clock | Stopped twice → `CMP-ERR-CLOCK-STATE` | Event-declared stop by CMP; POL's call is an idempotent no-op | REQ-POL-219, R-60 |

**Round-3 statutory-flow sweep (l.1385–1398).**
- Every one of the 38 clocks has an owner that starts it.
- Each WAITING_PERIOD clock has a `ClockElapsed` handler: rows 1–4 POL, row 8 BIL, row 13 CLM.
- `POL_REFUND_DUE` stops on BIL `RefundDisbursed` (REQ-BIL-190).

**Residual flow risk.** 119 listed-but-unhandled consumer pairs (R3-002) break no flow. But the 33 CMP "intended consumer" rows mean that, unless handlers were added, BIL and FIN never react to `FiscalDocCancelled`, and POL never reacts to `BureauFactRejected`. Treat both as open design questions until confirmed in PRD-11, PRD-06, PRD-09 and PRD-05.

**Outside this review.** PRD-18 §20 raises a further flow gap: Friendly Settlement net clearing between CLM, BIL and FIN (XMR-F-400, a new `FS_CLEARING` settlement source). It is not part of this review but sits on the same seam.

---

## 5. Conflicts with the infra stack (ARCHITECTURE-DECISIONS.md)

The review never mentions the infra stack. It predates or ignores ARCHITECTURE-DECISIONS.md (dated the same day). It reports "No violation" of vendor-neutrality, with "PostgreSQL … fixed by the contract's architecture context" (l.311), which is consistent with the ADR. The conflicts and tensions below come from vocabulary and design assumptions in the review and the rulings it records.

| # | Review wording / assumption | ADR position | Conflict level | Build interpretation |
|---|---|---|---|---|
| I-1 | **"Lakehouse"** is baked into names: `LakehouseErasureCompleted`, `LakehouseErasureTask`, "DAT lakehouse" (CON-029), "lakehouse erasure can be long-running" (R2A-003) | ADR §1 "Not used: data lakehouses"; reporting marts are PostgreSQL schemas built by scheduled jobs | **Direct naming conflict** | Implement DAT as PostgreSQL mart schemas plus Hangfire jobs. Keep the event name for contract stability (or rename via CCR). Erasure is a synchronous SQL operation, which fits R-97 |
| I-2 | **Broker-style event vocabulary**: topics `<mod>.events.v1`; partition keys per event (R-15, R-39, R-100); `consumer_group`; `plt.Consumer.replay`, `plt.DeadLetter.replay`, `DeadLetterParked`; "event schema registry" (REQ-PLT-142); PLT event archive (REQ-PLT-140) | No message broker; transactional outbox dispatched **in order** to in-process handlers; Service Bus attachable later | **Terminology tension, not a blocker** | Map "topic" to the outbox stream per module, "partition key" to the per-aggregate ordering key used by the dispatcher, and "consumer group" to per-handler checkpoints. Dead-letter and replay are outbox features to build (Hangfire retries). Do not introduce Kafka or a schema-registry product; a JSON-schema table in PostgreSQL suffices |
| I-3 | **Availability tiers T1/T2/T3 and "module X unavailable" degraded modes** (CON-016, CON-017, R-83, NFR-POL-009, NFR-WRK-008) assume independently failing services | Modular monolith: one deployable; modules share process and database | **Partly moot** | Within one process, "WRK down" means only a module-level failure (exception or timeout). Keep the event-fed read models (`ActivityGateView`, `DisclosureDeliveryView`, `CoexistenceRouteView`), because they also satisfy "never read another module's tables". The **DOC render path is genuinely separate** (Gotenberg container), so the DOC-down degraded mode and the quote-preview exclusion (F8) remain real |
| I-4 | **Statutory clocks** as `ClockInstance` domain records with kinds, states and events (R-78) | Hangfire runs due work; "statutory deadlines are stored as domain records" | **Consistent** | CMP clock table + Hangfire scheduled jobs for warn, elapse and breach transitions |
| I-5 | **WRK queues and routing, maker-checker activities** (PLT-APPROVAL) | No workflow servers or BPM engines | **Consistent** if built as state machines | Implement WRK as tables and state machines, not as an embedded BPM engine |
| I-6 | **AI-PLT-05 "Adaptive sign-in risk scoring (external realm)"**, step-up checks, PLT identity events (`UserProvisioned`, etc.) | Entra ID / Entra External ID; "we never build our own identity system" | **Potential conflict** | Prefer Entra External ID's built-in risk-based conditional access. A home-grown AI sign-in risk model would duplicate the identity platform. Needs a decision (flag for PLT) |
| I-7 | **"PLT EU model gateway"** (REQ-PLT-217), model registry (REQ-DAT-005), MCP facade (AI-CHN-02, REQ-CHN-264) | ADR names no LLM provider, gateway or MCP library | **Gap, not a conflict** | AI is off-by-default and no Must depends on it. A gateway component (EU-hosted model endpoint, e.g. Azure OpenAI EU) needs an ADR addendum before any AI feature ships |
| I-8 | **ACORD NGDS mapping, partner APIs, webhooks** (REQ-CHN-217, `WebhookSubscriptionSuspended`) | REST + OpenAPI | **Consistent** | — |
| I-9 | **e-signature / eIDAS** (`SignatureCompleted`, REQ-PLT-130), **Bank of Greece / myDATA / bureau** adapters as SPIs | Country packs behind typed interfaces (ADR §2 rule 10) | **Consistent** | SPI list (40 SPIs in contract §3.5.8) maps to ADR rule 10. Cyprus stub pack in CI; the review's synthetic `CY-STAMP` fixture (REQ-MKT-265) belongs there |
| I-10 | **Dry-run on every money-moving command** (XRF-021), idempotency (`IdempotencyRecord`) | ADR rule 6: `Idempotency-Key` on every command; RFC 9457 errors | **Consistent** | Idempotency tables are module-internal (R3-010 renames) |
| I-11 | **Append-only ledgers; reverse-and-reapply; GROSS/NET delta** (XRF-020) | ADR rules 1 and 4 | **Consistent** | — |
| I-12 | **Golden suites** (RAT, MKT; Cyprus fixture), **reconciliations** (policy ↔ billing ↔ sub-ledger ↔ myDATA) | ADR §3 golden tests and nightly reconciliations | **Consistent** | — |
| I-13 | **DORA register / ICT third parties** (REG-004), **non-EU transfers with a Chapter V basis** (R-93) | Azure EU region | **Consistent** | — |

No mention of Kubernetes, microservices, Camunda/BPM, Kafka by name, MediatR, AutoMapper or MassTransit appears in the review.

---

## Still-open items (summary)

**At review close (contract v1.10):**
- R3-002: consumer-list trimming; 119 pairs, incl. 33 CMP "intended consumers" rows.
- R3-003(b): `RC-MKT-CFG`.
- R3-003(c): FIN `PeriodReopened` → WRK.
- R3-004: REQ-CLM-155 section reference.
- R3-005: partition-key wording in PRD-01 and PRD-07; contract R-39.
- R3-007: CHN routing-operation citation.
- R3-008: AI-PFC-04 bias cell.
- R3-009(a–d): wording residues.
- R3-010: shared technical table names.
- Stale document-control contract versions.
- COV-006: optional.

R3-001, R3-003(a) and R3-006 were fixed at or just after close.

**On disk now (contract v1.12, frozen).** By spot-check, R3-003(b), R3-003(c), R3-004, R3-005, R3-007, R3-008, R3-009(a)(b)(c-in-PRD-17) and R3-010 are fixed. What remains open or unverified:
- **R3-002**: partly trimmed; the CMP "intended consumers" rows and the 86 plain pairs are not fully verified;
- **"intermediated, direct, bank"** still in PRD-02 l.394, a sibling of R3-009(c) that the review did not name;
- **R3-009(d)**: PRD-17 l.1050, not checked;
- **document-control versions**;
- **COV-006**: optional.
