# Digest — 00-baseline-inventory.md (Baseline screen/capability inventory)

Source: `C:\Users\Karl\Projects\coreinsurance\core-insurance-prds\00-baseline-inventory.md` (4,951 lines, read in full).
Checked against: `core-insurance-infra\ARCHITECTURE-DECISIONS.md`.
Counts below were checked with scripts against the source tables (section C, section D, section F headings, and the "Contract owner (binding)" lines).

> This source is **not a module PRD**. It has no REQ/BR/NFR/EVT IDs, entities, events, APIs or SPIs. Template sections 3–7 and 9–13 therefore mostly read "n/a". Its job is to set the **minimum UI coverage (the floor)** that PRD-01…17 must meet.

---

## 1. Identity, purpose, structure, identification

**Header (§ header table):** Version 1.0, dated 2026-10-06, owned by the lead architect/orchestrator. Status: "Binding. Nothing in this inventory may be dropped by any PRD."

**Purpose (§A).** This is the floor for PRD-01…PRD-17. Every screen, field, action and navigation link has **exactly one owning PRD** plus named consumer PRDs. The owner must specify each owned item in its PRD **section 6 (Screens and UI specification)** and cite the inventory reference. Consumers reference an item but do not re-specify it.

**Coverage rule (§A).** An item is *covered* when the owning PRD either:
- (a) specifies an equivalent region, field, action or link and cites the reference; or
- (b) explicitly replaces it with a streamlined alternative, cites the reference and says where the capability went.

Silent omission counts as a coverage gap. A US-only concept with no Greek equivalent counts as covered if the owner records "not applicable in GR, reason …".

**Vendor/vocabulary rules (§A):**
- Vendor products (Guidewire, Appian, Duck Creek, Temporal, Azure, etc.) are references for functionality only. They are never components of the system (System Contract CD-20).
- Reference terms are not our vocabulary. Fields must be translated into the Greek/EU context and the vocabulary of `00-system-contract.md` §3.3. Example: "License State" becomes issuing country plus licence category; "MVR" becomes whatever driving-record or claims-history source the market has, flagged where none exists.

**How items are identified (§A):**
| Kind | Format | Example |
|---|---|---|
| Reference screen (Guidewire-derived) | `SCR-<batch>-<NN>` | `SCR-PCSUB1-03` |
| Field / action / link on a screen | `SCR-… › <label>` / `SCR-… › action: <label>` | `SCR-PCSUB1-03 › License #` |
| UI-library capability screen | `UIL-<area><N>` (U = underwriting, C = claims, B = billing, F = finance, R = reinsurance, K = compliance, I = IT/ops, P = customer portal, A = agent/broker) | `UIL-C10` |
| Inspiration-board interaction pattern | `IB-<NN>` | `IB-07` |

Batch codes used in SCR IDs: CC (ClaimCenter), PX (partner apps), PCACC1/PCACC2 (Account/Desktop/Other), PCADM1/PCADM2 (Admin), PCPF (Policy File), PCSUB1/PCSUB2 (Submission), PCTX (policy transactions), PD1/PD2 (Product Designer).

**Structure:**
- §A purpose and rules.
- §B method and owner counts.
- §C ownership matrix of the 158 reference screens (ID, screen, batch, field rows, owner, consumers, note).
- §D 60 UI-library rows (capability level, with Library class Must/Should and evidence).
- §E 33 inspiration-board patterns (IB-01…33), the names PRDs must use in section 6.
- §F per-screen detail for all 158 SCR entries (lines 318–4893). Each entry has source frames, purpose, field table (Label / Apparent type / Req/Opt / Picklist values / Notes), actions, navigation, states/messages, the *proposed* owner, and the authoritative "Contract owner (binding)" line.
- Batch notes on shared chrome (lines 4894–4951).

**Method (§B).**
1. **Guidewire library:** 1,658 frames (1,357 unique) grouped into 152 screen types (PolicyCenter 117, Product Designer 27, ClaimCenter 6, partner apps 2). 616 representative frames were read, giving **158 entries and 2,188 field rows**.
2. **UI Library:** 60 rows (54 reference screens plus 6 agent/broker areas). Captured at capability level only, because the library's images were hotlinked and not opened. Field-level specification is left to the owning PRD.
3. **Inspiration board:** catalogued by function only; no styling recorded.
4. **Owners:** proposed by the extraction pass, then **overridden where the System Contract decides** (CD-05 authority, CD-09 form patterns/inbound documents, CD-10 groups, CD-17 holidays, etc.). The "Contract owner (binding)" line is authoritative.

**Non-goals stated:**
- No colours, fonts, spacing or visual styling. Those belong in the separate design guide (§E). Only semantic states are allowed (System Contract §3.9.9).
- No vendor product is to be used as a component (§A).

---

## 2. Size metrics / counts

### 2.1 Totals
| Item | Count |
|---|---|
| Reference-screen entries (SCR, §C and §F) | **158** (§C and §F owners match exactly) |
| Field rows across SCR entries | **2,188** |
| UI-library rows (UIL, §D) | **60** (49 Must / 11 Should) |
| Inspiration-board patterns (IB, §E) | **33** |
| **Total ownable screen/capability items (SCR + UIL)** | **218** |
| SCR entries whose proposed owner was overridden by the contract owner | **28** (§4.3) |
| UIL rows marked "No public example" (design from first principles) | **7** |
| Text markers for unseen content ("illegible", "not captured", "not seen", "not opened") | 36 occurrences |

There are **no REQ/BR/NFR/EVT/SPI IDs, no entities, events or APIs, and no AI feature IDs**, because this is not a PRD.

### 2.2 Items per owning module
| Owner | SCR entries | SCR field rows | UIL rows (Must/Should) | **Total items** |
|---|---|---|---|---|
| POL | 50 | 777 | 0 | **50** |
| PFC | 29 | 199 | 2 (2/0) | **31** |
| PLT | 21 | 160 | 6 (6/0) | **27** |
| WRK | 20 | 313 | 0 | **20** |
| UW | 15 | 145 | 5 (3/2) | **20** |
| CLM | 6 | 214 | 12 (9/3) | **18** |
| CHN | 0 | — | 11 (9/2) | **11** |
| PTY | 9 | 284 | 0 | **9** |
| DOC | 8 | 96 | 1 (1/0) | **9** |
| BIL | 0 | — | 9 (8/1) | **9** |
| CMP | 0 | — | 4 (4/0) | **4** |
| FIN | 0 | — | 4 (4/0) | **4** |
| RI | 0 | — | 4 (2/2) | **4** |
| RAT | 0 | — | 2 (1/1) | **2** |
| DAT | 0 | — | 0 | **0** (consumer only) |
| MIG | 0 | — | 0 | **0** (consumer only) |
| MKT | 0 | — | 0 | **0** (consumer only) |
| **Total** | **158** | **2,188** | **60 (49/11)** | **218** |

SCR entries per source batch: PC-PolicyTx 28, ProductDesigner-1 17, PC-Admin-1 14, PC-Admin-2 14, PC-Account-Desktop-Other-2 14, PC-Account-Desktop-Other-1 13, PC-PolicyFile 13, PC-Submission-1 13, ProductDesigner-2 13, PC-Submission-2 11, ClaimCenter+PartnerApps 8.

### 2.3 Phase / priority
- **No phase tags (P1/P2), no "Motor MVP" flag and no Must/Should/Could on SCR entries.** The only priority signal is the UI-library "Library class" (go-live class) on UIL rows: **49 Must, 11 Should**.
- Evidence quality of UIL rows: 26 Verified, 21 Partial, 7 No public example, 6 "Agent job aids".
- Observation (not stated as a phase): almost all SCR policy screens show **Personal Auto** (submission, change, cancel, reinstate, rewrite, renew). They are therefore the natural field-level baseline for a Motor MVP. Commercial Package appears only in SCR-PCSUB1-01 (CPP questions) and SCR-PCSUB2-04.
- §B notes the Guidewire material is PolicyCenter-heavy. BIL, CLM, RI, FIN, CMP, CHN, DAT, MIG and MKT take their baseline mainly from §D and their prompt pack.

### 2.4 Build-size estimate for the UI floor
**L**. That is 218 owned items, 2,188 field rows, and 33 named patterns that every PRD's section 6 must use. POL alone carries 50 screens and 777 field rows across five transaction wizards.

---

## 3. Screens per module (compact)
Columns: ID, name, owner, then **Cls**. Cls is the Library class (Must/Should) for UIL rows; "—" means the SCR row has no priority given. Field-row counts are in brackets for SCR. **NPE** = No public example.

### POL — Policy administration (50 SCR)
| ID | Screen | Cls |
|---|---|---|
| SCR-PCACC1-09 | New Submissions [11] | — |
| SCR-PCACC1-10 | Account File – Policy Transactions [14] | — |
| SCR-PCACC2-03 | Search Exclusions and Conditions (PA line) [8] (overridden from PFC) | — |
| SCR-PCACC2-05 | Search Policies [22] | — |
| SCR-PCACC2-12 | Account File – Submission Manager [9] | — |
| SCR-PCPF-01 | Policy File – Summary [59] | — |
| SCR-PCPF-02 | Policy File – Policy Transactions [19] | — |
| SCR-PCPF-03 | Policy File – Quote [26] (RAT owns worksheet viewer component) | — |
| SCR-PCPF-04 | Policy File – PA Coverages [27] (PFC owns definitions) | — |
| SCR-PCPF-06 | Policy File – Contacts [17] (PTY owns party master) | — |
| SCR-PCPF-09 | Policy File – Policy Info [19] | — |
| SCR-PCPF-10 | Policy File – Vehicles [34] | — |
| SCR-PCPF-13 | Policy File – Drivers [33] | — |
| SCR-PCSUB1-01 | Submission: Offerings [8] (PFC owns offerings definition) | — |
| SCR-PCSUB1-02 | Submission: Qualification (PA Pre-Qual) [6] (PFC owns question sets) | — |
| SCR-PCSUB1-03 | Submission: Drivers [62] | — |
| SCR-PCSUB1-04 | Submission: Vehicles [26] | — |
| SCR-PCSUB1-05 | Location Information popup [10] | — |
| SCR-PCSUB1-07 | Submission: PA Coverages [28] | — |
| SCR-PCSUB1-08 | Submission: Policy Info [22] | — |
| SCR-PCSUB1-09 | Submission: Policy Review [16] | — |
| SCR-PCSUB1-10 | Submission: Quote [21] | — |
| SCR-PCSUB2-01 | Submission Bound confirmation [8] | — |
| SCR-PCSUB2-04 | Line Selection (Commercial Package) [3] | — |
| SCR-PCSUB2-10 | New Driver [34] | — |
| SCR-PCTX-01 | Policy Change: Start [7] | — |
| SCR-PCTX-02 | Policy Change: Offerings [2] | — |
| SCR-PCTX-03 | Policy Change: Drivers [17] | — |
| SCR-PCTX-04 | Policy Change: PA Coverages [21] | — |
| SCR-PCTX-06 | Policy Change: Quote [26] | — |
| SCR-PCTX-07 | Policy Change: Policy Review [3] | — |
| SCR-PCTX-08 | Policy Change Bound [2] | — |
| SCR-PCTX-09 | Cancellation: Start [6] | — |
| SCR-PCTX-10 | Cancellation: Confirmation [11] | — |
| SCR-PCTX-11 | Cancellation Bound [1] | — |
| SCR-PCTX-12 | Reinstatement: Start [10] | — |
| SCR-PCTX-13 | Reinstatement: Quote [7] | — |
| SCR-PCTX-14 | Rewrite: Offerings [1] | — |
| SCR-PCTX-15 | Rewrite: Policy Info [22] | — |
| SCR-PCTX-16 | Rewrite: Drivers [1] | — |
| SCR-PCTX-17 | Rewrite: Vehicles [24] | — |
| SCR-PCTX-18 | Rewrite: PA Coverages [9] | — |
| SCR-PCTX-20 | Rewrite: Quote [9] | — |
| SCR-PCTX-21 | Rewrite: Policy Review [3] | — |
| SCR-PCTX-22 | Rewrite Remainder of Term Bound [1] | — |
| SCR-PCTX-23 | Renewal: Offerings [1] | — |
| SCR-PCTX-24 | Renewal: Policy Info [17] | — |
| SCR-PCTX-25 | Renewal: Policy Review [1] | — |
| SCR-PCTX-26 | Renewal: View Quote [11] | — |
| SCR-PCTX-27 | Policy Change: Policy Info [22] | — |

### PFC — Product factory (29 SCR + 2 UIL)
| ID | Screen | Cls |
|---|---|---|
| SCR-PD1-02 | Product Model Home (tiles) [6] | — |
| SCR-PD1-03 | Policy Lines List [4] | — |
| SCR-PD1-04 | Policy Line (Basics) [7] | — |
| SCR-PD1-05 | Policy Line Coverages List [5] | — |
| SCR-PD1-06 | Add Coverage dialog [6] | — |
| SCR-PD1-07 | Coverage (Basics) [11] | — |
| SCR-PD1-08 | Changes Side Panel [3] | — |
| SCR-PD1-09 | Changes (Change Review Page) [5] | — |
| SCR-PD1-10 | Settings Menu / Synchronize Product Model [4] | — |
| SCR-PD1-11 | Coverage Terms List [4] | — |
| SCR-PD1-12 | Add Term dialog [7] | — |
| SCR-PD1-13 | Coverage Term (Basics) [14] | — |
| SCR-PD1-14 | Coverage Term Options (+ Add Option) [9] | — |
| SCR-PD1-15 | Coverage Term Option (Basics) [4] | — |
| SCR-PD1-16 | Coverage Term Availability [10] | — |
| SCR-PD1-17 | Coverage Availability [13] | — |
| SCR-PD2-01 | Coverage Term Option: Availability [8] | — |
| SCR-PD2-02 | Policy Line: Exclusions (+ Add) [11] | — |
| SCR-PD2-03 | Exclusion detail [11] | — |
| SCR-PD2-04 | Policy Line: Conditions (+ Add) [11] | — |
| SCR-PD2-05 | Condition detail [8] | — |
| SCR-PD2-06 | Policy Line: Categories [3] | — |
| SCR-PD2-07 | Products list [4] | — |
| SCR-PD2-08 | Product (basics) [12] | — |
| SCR-PD2-09 | Product: Question Sets [1] | — |
| SCR-PD2-10 | Product: Offerings [3] | — |
| SCR-PD2-11 | Offering: Selections [6] | — |
| SCR-PD2-12 | Coverage: Offerings [3] | — |
| SCR-PD2-13 | Product: Availability [6] | — |
| UIL-U7 | Product configuration | Must |
| UIL-U9 | Product and rate version approval (no approval/promotion step shown in reference) | Must |

### PLT — Platform (21 SCR + 6 UIL)
| ID | Screen | Cls |
|---|---|---|
| SCR-PCACC1-01 | Login [2] | — |
| SCR-PCACC1-06 | Server Tools – Batch Process Info [9] | — |
| SCR-PCACC1-07 | Internal Tools – Reload [0] | — |
| SCR-PCACC1-08 | Internal Tools – PC Sample Data [3] (non-production; overridden from MIG) | — |
| SCR-PCACC2-02 | Error pages [4] | — |
| SCR-PCACC2-10 | Location Info (PCF diagnostic) [6] | — |
| SCR-PCACC2-11 | Server Tools – View Logs [4] | — |
| SCR-PCACC2-14 | Internal Tools – Testing System Clock [4] | — |
| SCR-PCADM1-05 | Runtime Properties [5] | — |
| SCR-PCADM1-08 | Users (search) [13] | — |
| SCR-PCADM1-09 | User Detail [34] | — |
| SCR-PCADM1-14 | Authority Profiles list [4] (CD-05; overridden from UW) | — |
| SCR-PCADM2-01 | Authority Profile detail [8] (CD-05; overridden from UW) | — |
| SCR-PCADM2-05 | Holidays list [5] (CD-17; overridden from WRK) | — |
| SCR-PCADM2-06 | Add Holiday [4] (CD-17; overridden from WRK) | — |
| SCR-PCADM2-08 | Data Change [11] (maker-checker; overridden from MIG) | — |
| SCR-PCADM2-12 | New User [22] | — |
| SCR-PCADM2-13 | Roles list [6] | — |
| SCR-PCADM2-14 | Role Detail [9] | — |
| SCR-PCSUB1-13 | Application shell and global menus [5] | — |
| SCR-PD1-01 | Product Designer Login [2] | — |
| UIL-U6 | Underwriting authority administration (CD-05) | Must |
| UIL-K5 | Audit log viewer | Must |
| UIL-I1 | Staff user and role administration | Must |
| UIL-I2 | Batch and workflow monitor (ref: Temporal UI) | Must |
| UIL-I3 | Integration and message queue monitor | Must |
| UIL-I4 | ICT incident log and DORA reporting | Must |

### WRK — Work management (20 SCR)
| ID | Screen | Cls |
|---|---|---|
| SCR-PCACC1-02 | Desktop – My Summary [47] | — |
| SCR-PCACC2-01 | Account File Participants [5] (overridden from PTY) | — |
| SCR-PCACC2-07 | Desktop – My Activities [13] | — |
| SCR-PCACC2-08 | Desktop – Assign Activities [14] | — |
| SCR-PCACC2-09 | Account File – New Note [10] | — |
| SCR-PCACC2-13 | Desktop – My Accounts [5] (overridden from PTY) | — |
| SCR-PCADM2-03 | Activity Patterns list [10] | — |
| SCR-PCADM2-04 | New Activity Pattern [22] | — |
| SCR-PCADM2-07 | Activity Pattern Detail [22] | — |
| SCR-PCADM2-09 | Groups search [8] (CD-10; overridden from PLT) | — |
| SCR-PCADM2-10 | Group Detail [14] (CD-10; overridden from PLT) | — |
| SCR-PCADM2-11 | Search Users (from Group) [13] (CD-10; overridden from PLT) | — |
| SCR-PCPF-05 | Policy File – Participants [3] (overridden from UW) | — |
| SCR-PCPF-11 | Policy File – New Activity worksheet [13] | — |
| SCR-PCPF-12 | Policy File – Activity Detail worksheet [16] | — |
| SCR-PCSUB1-12 | Activity worksheets in wizard [14] | — |
| SCR-PCSUB2-07 | Workplan (submission) [11] | — |
| SCR-PCSUB2-08 | Notes (submission) + New Note [19] | — |
| SCR-PX-01 | Indico Intake – Claims Inbox [18] (CD-09 inbound docs; overridden from CHN) | — |
| SCR-PX-02 | Indico Intake – Claim Review [36] (CD-09; overridden from CHN) | — |

### UW — Underwriting (15 SCR + 5 UIL)
| ID | Screen | Cls |
|---|---|---|
| SCR-PCADM1-06 | Policy Holds list + detail [17] | — |
| SCR-PCADM1-07 | New Policy Hold [11] | — |
| SCR-PCADM1-10 | Underwriting Rules list [19] | — |
| SCR-PCADM1-11 | Underwriting Rule Detail [25] | — |
| SCR-PCADM1-12 | Create New Rule [20] | — |
| SCR-PCADM1-13 | Import/Export Status (UW rules) [12] | — |
| SCR-PCADM2-02 | Issue Type Search [7] | — |
| SCR-PCSUB1-06 | Submission: Risk Analysis [6] | — |
| SCR-PCSUB1-11 | Issues that block Issuance [1] | — |
| SCR-PCSUB2-02 | Risk Approval Details [13] | — |
| SCR-PCSUB2-03 | Submission Declined [2] (decline record + refusal document; overridden from POL) | — |
| SCR-PCSUB2-11 | Pre-Quote Issues [2] | — |
| SCR-PCTX-05 | Policy Change: Risk Analysis [7] | — |
| SCR-PCTX-19 | Rewrite: Risk Analysis [2] | — |
| SCR-PCTX-28 | Policy Change: Issues that block Issuance [1] | — |
| UIL-U1 | Underwriter workbench and referral queue | Must |
| UIL-U2 | Submission intake, triage and routing | Should |
| UIL-U3 | Risk and account review | Must |
| UIL-U4 | Approve/decline with authority check (no authority check shown in reference) | Must |
| UIL-U5 | Decline/refusal letter (Greek nat-cat refusals) — NPE | Should |

### CLM — Claims (6 SCR + 12 UIL)
| ID | Screen | Cls |
|---|---|---|
| SCR-CC-01 | Claim – Parties Involved – Contacts [38] | — |
| SCR-CC-02 | Claim – Hi Marley Case (SMS) [23] | — |
| SCR-CC-03 | Claim – Summary and Actions menu [47] | — |
| SCR-CC-04 | Inspections – CCG IQ vendor request [20] | — |
| SCR-CC-05 | Claim – Services [67] | — |
| SCR-CC-06 | Search – Claims [19] | — |
| UIL-C1 | FNOL (staff intake) | Must |
| UIL-C2 | Adjuster workspace and claim summary | Must |
| UIL-C3 | Exposures (coverage × claimant) | Must |
| UIL-C4 | Reserves | Must |
| UIL-C5 | Payments | Must |
| UIL-C6 | Recoveries and subrogation (incl. Friendly Settlement) | Must |
| UIL-C7 | Claim diary and tasks (incl. statutory 3-month clock) | Must |
| UIL-C8 | Claim documents and photos | Must |
| UIL-C9 | Vendor and adjuster assignment | Should |
| UIL-C10 | Reserve and payment approval inbox (no inbox example) | Must |
| UIL-C11 | Catastrophe event management | Should |
| UIL-C12 | Fraud alert and SIU case | Should |

### CHN — Channels/portals (11 UIL)
| ID | Screen | Cls |
|---|---|---|
| UIL-C13 | Customer claim reporting and tracking | Should |
| UIL-P1 | Quote and buy (motor, home) | Must |
| UIL-P2 | Payments and billing (customer) | Must |
| UIL-P3 | Policy documents (customer) | Must |
| UIL-P4 | Online withdrawal button (EU, from June 2026) — NPE | Must |
| UIL-A1 | Agent: quote, submission, pre-quote UW issues, rewrite, withdraw | Must |
| UIL-A2 | Agent: changes, OOS changes, cancel, reinstate, renewals | Must |
| UIL-A3 | Agent: desktop, activities, Team tab, notes, account merge/move | Must |
| UIL-A4 | Broker: commercial quoting, referrals, contingencies, issuing | Should |
| UIL-A5 | Agent: billing accounts, pay plans, payments, refunds, notices | Must |
| UIL-A6 | Agency users, producers and commissions | Must |

### PTY — Party (9 SCR)
| ID | Screen | Cls |
|---|---|---|
| SCR-PCACC1-03 | New Account – Enter Account Information (duplicate check) [11] | — |
| SCR-PCACC1-04 | New Account – Create Account [28] | — |
| SCR-PCACC1-05 | Account File – Summary [53] | — |
| SCR-PCACC1-11 | Producer organization search popup [19] | — |
| SCR-PCACC1-12 | Account File – Contacts [36] | — |
| SCR-PCACC1-13 | Account File – Locations [31] | — |
| SCR-PCACC2-04 | Account Holder Summary [38] | — |
| SCR-PCACC2-06 | Search Accounts (+ Admin/gear menus) [23] | — |
| SCR-PCSUB2-09 | Primary Named Insured (Contact/Roles/Addresses) [45] | — |

### DOC — Documents (8 SCR + 1 UIL)
| ID | Screen | Cls |
|---|---|---|
| SCR-PCADM1-01 | Policy Form Patterns search [12] (CD-09; overridden from PFC) | — |
| SCR-PCADM1-02 | New Policy Form Pattern [22] (CD-09; overridden from PFC) | — |
| SCR-PCADM1-03 | Coverage/condition/exclusion picker [6] (CD-09; overridden from PFC) | — |
| SCR-PCADM1-04 | Form Pattern detail [19] (CD-09; overridden from PFC) | — |
| SCR-PCPF-07 | Policy File – Documents [13] | — |
| SCR-PCPF-08 | Policy File – Forms [6] | — |
| SCR-PCSUB2-05 | Forms (submission wizard) [4] | — |
| SCR-PCSUB2-06 | Documents (submission wizard) [14] | — |
| UIL-U11 | Document template and clause editor | Must |

### BIL — Billing (9 UIL)
| ID | Screen | Cls |
|---|---|---|
| UIL-B1 | Billing account summary (staff) | Must |
| UIL-B2 | Unapplied cash and suspense — NPE | Must |
| UIL-B3 | Payment exceptions and returns (SEPA returns, chargebacks) | Must |
| UIL-B4 | Refund approval and disbursement (IBAN check / VoP) — NPE | Must |
| UIL-B5 | Write-off — NPE | Should |
| UIL-B6 | Delinquency and dunning | Must |
| UIL-B7 | Intermediary collection and account current | Must |
| UIL-B8 | Bank reconciliation | Must |
| UIL-F5 | Commission plans and statements (listed under Finance, owned by BIL) | Must |

### CMP — Compliance (4 UIL)
| ID | Screen | Cls |
|---|---|---|
| UIL-K1 | Complaints case management (50-day reply clock) | Must |
| UIL-K2 | Regulatory report review (Solvency II QRTs) | Must |
| UIL-K3 | Fiscal document rejection queue (myDATA analogue) | Must |
| UIL-K4 | GDPR subject access requests | Must |

### FIN — Finance (4 UIL)
| ID | Screen | Cls |
|---|---|---|
| UIL-F1 | Journal and GL posting review | Must |
| UIL-F2 | Period close | Must |
| UIL-F3 | Premium tax and Auxiliary Fund levy returns — NPE | Must |
| UIL-F4 | IFRS 17 and Solvency II reporting | Must |

### RI — Reinsurance (4 UIL)
| ID | Screen | Cls |
|---|---|---|
| UIL-R1 | Treaty and programme set-up | Must |
| UIL-R2 | Cession review | Should |
| UIL-R3 | Reinsurance recoveries | Must |
| UIL-R4 | Bordereaux and statement of account | Should |

### RAT — Rating (2 UIL)
| ID | Screen | Cls |
|---|---|---|
| UIL-U8 | Rate tables and rating factors | Must |
| UIL-U10 | Rate testing and impact analysis — NPE | Should |

### DAT, MIG, MKT — no owned items (consumer only).

### Inspiration-board patterns (§E, mandatory names, not owned screens)
- IB-01 Command palette (back end owned by WRK global search)
- IB-02 Keyboard list navigation
- IB-03 Sidebar work views with counts
- IB-04 Priority-ranked work queue
- IB-05 Split-pane workspace
- IB-06 Agent plan panel
- IB-07 Approve-the-diff (Accept/Edit/Why?)
- IB-08 Explain-why popover with sources
- IB-09 Inline AI summary card with citations
- IB-10 Streaming draft
- IB-11 Work-left home
- IB-12 Progress-to-done
- IB-13 Statement view
- IB-14 Edit in place with autosave and undo. Never for fields that need a transaction, approval or maker-checker.
- IB-15 Relationship graph
- IB-16 Density setting
- IB-17 Hero metric
- IB-18 Floating overlay layer
- IB-19 Presence indicators
- IB-20 Pinned comments
- IB-21 Semantic status system
- IB-22 AI as a surface
- IB-23 State-change motion
- IB-24 Quiet chrome
- IB-25 Task-adaptive layout (generative UI)
- IB-26 Global filter bar
- IB-27 Self-enriching record
- IB-28 Exception-first view
- IB-29 Log-style dense table
- IB-30 Conversational analytics (DAT)
- IB-31 Agentic task run (stops at human approval gates)
- IB-32 Binary status at a glance
- IB-33 User theme preference

---

## 4. Ownership anomalies: no owner, duplicate owners, gaps

### 4.1 Items with no owner
- **None.** Every SCR and UIL row has exactly one owner. §C and §F owners agree for all 158 SCR entries (checked by script).
- **Modules that own nothing:** **DAT, MIG, MKT**. They take their baseline from their prompt pack (§B). **RAT owns no reference screen**, only UIL-U8 and UIL-U10, plus a *component* (the rating worksheet viewer) inside POL's SCR-PCPF-03.

### 4.2 Duplicate or split ownership (same function, different owners or layers)
| Case | Detail | Risk |
|---|---|---|
| User search shown twice | SCR-PCADM1-08 Users search (**PLT**) and SCR-PCADM2-11 Search Users from Group (**WRK**, CD-10) have nearly identical fields (Username, First/Last name, Group Name, Search Only Unassigned Users, User types, Role, Available Producer Code, Organization). | Two implementations of one picker. Agree on one shared component: PLT owns users; WRK owns the group-membership use. |
| Screen owner ≠ definition owner (stated in §C notes) | SCR-PCPF-03 (POL screen; **RAT owns worksheet viewer component**); SCR-PCPF-04 and SCR-PCSUB1-07 (POL selection; **PFC owns coverage definitions**); SCR-PCSUB1-01 (POL; **PFC owns offerings**); SCR-PCSUB1-02 (POL; **PFC owns question sets**); SCR-PCPF-06 (POL policy-context roles; **PTY owns party master**); SCR-PCACC2-03 (POL selection of conditions/exclusions; PFC catalogue). | Intended layering, but each pair needs a contract: PFC product-version API → POL rendering. |
| Authority | SCR-PCADM1-14 and SCR-PCADM2-01 (authority profiles) and UIL-U6 are all **PLT (CD-05)**. UIL-U4 "Approve or decline with authority check" is **UW**, and UIL-C10 "Reserve and payment approval inbox" is **CLM**. UW issue types (SCR-PCADM2-02) are **UW**, but they are the grant "Type" lookup inside PLT's authority profile. | PLT framework, UW/CLM consumers. Issue-type catalogue (UW) and authority grants (PLT) are coupled. |
| Holidays vs activity timing | Holidays SCR-PCADM2-05/06 are **PLT (CD-17)**. Activity Patterns using "Business days" (SCR-PCADM2-04/07) are **WRK**. | Business-day calendar is a PLT dependency of WRK, BIL and CLM. |
| Commissions | UIL-F5 "Commission plans and statements" sits in the Finance group but is owned by **BIL**. UIL-A6 "Agency users, producers and commissions" is **CHN** (consumers PTY, BIL). | Three-way split (BIL plans/statements, CHN agency view, PTY producers). |
| Customer claim reporting | UIL-C13 sits in the Claims group but is owned by **CHN**. UIL-C1 (staff FNOL) is **CLM**. AI intake SCR-PX-01/02 is **WRK** (CD-09). | Three FNOL entry points, three owners. They need one shared FNOL command in CLM. |
| Policy holds | UW owns SCR-PCADM1-06/07, but they appear as UW issues in POL wizards (SCR-PCTX-05, SCR-PCTX-28 "WildFire No PolicyChange"). | Consistent, but the POL ↔ UW blocking contract must be explicit. |
| Repeated screens with one owner (consolidation candidates) | Drivers: SCR-PCSUB1-03, SCR-PCTX-03, SCR-PCTX-16, SCR-PCPF-13 (POL). Risk Analysis: SCR-PCSUB1-06, SCR-PCTX-05, SCR-PCTX-19 (UW). Blocking issues: SCR-PCSUB1-11, SCR-PCTX-28 (UW). Activity worksheets: SCR-PCPF-11, SCR-PCPF-12, SCR-PCSUB1-12 (WRK). Documents: SCR-PCPF-07, SCR-PCSUB2-06 (DOC). Forms: SCR-PCPF-08, SCR-PCSUB2-05 (DOC). | Each can be one component reused across transaction types, as allowed by coverage rule (b). |

### 4.3 Owner overrides (proposed → binding), 28 entries
- **MIG → PLT:** SCR-PCACC1-08 (sample data), SCR-PCADM2-08 (Data Change).
- **PTY → WRK:** SCR-PCACC2-01, SCR-PCACC2-13.
- **PFC → POL:** SCR-PCACC2-03, SCR-PCPF-04, SCR-PCSUB1-01, SCR-PCSUB1-02, SCR-PCSUB1-07, SCR-PCTX-04, SCR-PCTX-18 (the last three were proposed as "PFC with POL host" or "PFC/POL").
- **PFC → DOC (CD-09):** SCR-PCADM1-01, -02, -03, -04.
- **UW → PLT (CD-05):** SCR-PCADM1-14, SCR-PCADM2-01.
- **WRK → PLT (CD-17):** SCR-PCADM2-05, SCR-PCADM2-06.
- **PLT → WRK (CD-10):** SCR-PCADM2-09, -10, -11.
- **RAT → POL:** SCR-PCPF-03.
- **UW → WRK:** SCR-PCPF-05.
- **PTY → POL:** SCR-PCPF-06.
- **POL → UW:** SCR-PCSUB2-03.
- **CHN → WRK (CD-09):** SCR-PX-01, SCR-PX-02.

### 4.4 Gaps and data-quality defects in the inventory
**"No public example"** (owner must design from first principles and say so; §D):
- UIL-U5 decline letter
- UIL-U10 rate testing
- UIL-B2 suspense
- UIL-B4 refund approval
- UIL-B5 write-off
- UIL-F3 premium tax and Auxiliary Fund levy
- UIL-P4 online withdrawal button

**Partial evidence with an explicitly missing control:**
- UIL-U4: "no authority-level check shown".
- UIL-U9: "no approval or promotion step shown".
- UIL-C10: "no inbox example".
- UIL-C3: "no separate exposure entity in reference".

**Thin domains (§B):** BIL, CLM, RI, FIN, CMP, CHN, DAT, MIG and MKT rely mainly on §D and the prompt pack. CLM has field detail for only 6 screens, mostly vendor-integration pages (Hi Marley, CCG IQ).

**Garbled or truncated consumer cells in §C and §F** (fix before coverage checks):
- SCR-PCADM2-03: consumers read "- patterns drive generated activities". The proposed consumers UW, POL and CLM were lost.
- SCR-PCADM2-04: consumers read "Document/Email Template lookups), UW, POL". The **DOC** consumer was truncated away.
- SCR-PCADM2-14: consumers read "permissions defined per function", with no module codes.
- SCR-PCADM2-02: consumers read "pattern: select-from-search lookup", with no module codes.
- SCR-PCADM2-12 and SCR-PCADM2-13: no consumers, though UW and PTY were proposed for -12.
- Several UIL rows have consumers "—": C3, B1, B3, F1, F2, R1, R2, I2, I3.

**Unseen sub-content** (36 markers), for example:
- Authority Profile detail reached from the User link (SCR-PCADM1-09) was not captured.
- Tabs not seen: Attributes/Access/Region (SCR-PCADM1-09, SCR-PCADM2-12); Hold Regions (SCR-PCADM1-06/07); Additional Coverages content (SCR-PCPF-04); Associated Transactions/Policies (SCR-PCACC1-12); the "Policy Change" tab of the form pattern (SCR-PCADM1-02); the Advanced tab of UW rules (SCR-PCADM1-11/12); Damages Assessment, Recommended Email Response and Coverage Information sections (SCR-PX-02).
- Line Items on the invoice (SCR-CC-05).

Owners must design these themselves or record them as N/A.

**PRD count mismatch:** this inventory says PRD-01…**17**, while ARCHITECTURE-DECISIONS says "PRD-01 … PRD-**18**". 17 module codes appear here: POL, PFC, PLT, WRK, UW, PTY, DOC, CLM, BIL, CHN, CMP, FIN, RAT, RI, DAT, MIG, MKT. Confirm which PRD-18 exists and whether it has any baseline.

---

## 5. Conflicts with the infra stack and vendor concepts not to copy literally

### 5.1 Direct conflicts with ARCHITECTURE-DECISIONS.md
| Inventory item | What the reference shows | Conflict with stack | Translate to |
|---|---|---|---|
| UIL-I2 Batch and workflow monitor (Must) | "Verified (Temporal UI): workflow list, timeline with failures, event history, child workflows" | No workflow servers or BPM engines; **Hangfire** runs jobs, and lifecycles are domain state machines (§1, §2 rule 5, §4) | Hangfire job dashboard plus a domain view of state-machine instances and statutory deadlines (deadline records live in domain tables). No "child workflows". |
| UIL-I3 Integration and message queue monitor (Must) | "queue and dead-letter counts, peek and receive, purge; consumer-group lag" | **No message brokers**; transactional outbox with in-process handlers (§1) | Outbox monitor: pending, dispatched, failed and parked rows per handler, retry/replay (idempotent), handler lag. "Purge" must not delete audit-relevant events. "Consumer-group lag" (Kafka) does not apply. |
| Admin › Monitoring menu: "Messages, Message Queues, Workflows, Workflow Statistics" (SCR-PCSUB2-05, SCR-PCTX-05, batch notes PC-Admin-2) | Guidewire messaging and workflow engine | Same as above | Outbox + Hangfire views only. |
| SCR-PCTX-23/24 renewal header "Workflow: (Running)" / "Wait Timeout/Manual" | Guidewire workflow engine driving renewals | No workflow engine | Renewal state machine plus Hangfire-scheduled deadline jobs; show state and next due action. |
| SCR-PCACC1-06 Batch Process Info (Run/Stop, Suspend Scheduler, Cron "S M H DOM M DOW") | Guidewire batch processes (ActivityRetire, PolicyHoldJobEval, PremiumCeding, etc.) | Compatible in function | Hangfire recurring jobs (Hangfire uses cron syntax but without the Quartz seconds field and "?"). Do not copy batch names. |
| SCR-PCACC1-07 Reload PCF Files / Web Templates / **Workflow Engine** / Display Names | Hot reload of the Guidewire config runtime | No runtime config reload in a compiled .NET monolith; no workflow engine | Not applicable. Record as "N/A, reason: config deployed via CI/EF migrations". |
| SCR-PCACC2-10 Location Info (PCF file structure) | Guidewire PCF page-composition diagnostics | React + Vite SPA; no PCF | Not applicable (could be a dev-only route inspector). |
| SCR-PCACC2-02 Error pages with Java stack traces ("java.lang.ClassCastException …", "Gosu") | Server-rendered Jetty error pages | Errors must be **RFC 9457 Problem Details** (§2 rule 6). Stack traces go to Serilog/OpenTelemetry, never to the UI | Problem Details rendered with a correlation ID. |
| SCR-PCACC1-01 Login, SCR-PD1-01 PD Login (username/password); SCR-PCADM1-09 and SCR-PCADM2-12 Password, Confirm Password, Locked, Active | In-app credential store | **Entra ID / Entra External ID**; "we never build our own identity system" (§1) | Sign-in redirect (OIDC). User admin maps Entra identities to app roles and authority. Passwords and lock-out are managed in Entra, so these are N/A in-app. |
| SCR-PD1-10 and SCR-PD2-03 "Synchronize Product Model" dialog (Test Server URL, User name, Password; "deploys … to a running test server without restarting") | Hot deploy of product model with credentials | Entra auth; no hot redeploy | Product-version publish and promote (UIL-U9 approval) with effective dating; no credentials dialog. |
| SCR-PCADM2-08 Data Change (free-form **Gosu** code executed against prod data; queries created via SOAP API) | Arbitrary script execution | Rule 1 (DB enforces invariants), rule 4 (nothing financial edited; reversals only), rule 9 (audit) | Controlled data-correction request with maker-checker (as the binding note says), typed correction commands, and never edits to ledgers. No script editor. |
| SCR-PCACC2-14 Testing System Clock (shift server time) | Changes the global server clock | Rule 3: calculations are pure, with no clock inside; clock is injected | Per-environment injectable test clock in non-prod only; must never be reachable in production. |
| SCR-PCACC1-08 PC Sample Data | Canned dataset loader | Rule 12: synthetic data only outside production | Synthetic-data loader, non-prod only. |
| SCR-PCADM1-05 Runtime Properties (New/Import/Export) | Editable runtime properties | Config and secrets in Key Vault / app config via Bicep | Read-only effective-config view at most; secrets never displayed. |
| SCR-PCACC2-11 Server Tools – View Logs (tail/grep pclog.log) | File log viewer | Serilog + OpenTelemetry → Application Insights | Link to App Insights; no file tail in app. |
| SCR-PD1-04, SCR-PD1-07, SCR-PD1-16/17, SCR-PD2-01, SCR-PD2-03, SCR-PD2-05 scripts (Initialization, Removal, Existence, Availability scripts; "Gosu-like code") | Embedded scripting in the product model | No scripting engine on the stack; rule 3 needs pure, reproducible calculations | Declarative availability and eligibility rules in versioned product config. If expressions are needed, use a small typed rule DSL evaluated by pure functions. |
| SCR-PCADM1-11/12 UW rule condition builder (Left/Right Expression, "hopDwelling.DistanceToFireHydrant > 400", Context "For Each Dwelling", Checking Sets "PreQuote, PreBind, MVR…", Promote to Stage) | Guidewire rules engine | No rules engine product | UW rules as versioned, typed rule definitions (UW module), approval and promotion via maker-checker. Do not copy checking-set names. |
| §A list "Guidewire, Appian, Duck Creek, **Temporal, Azure** … are never components" | — | **Azure is the hosting platform** in ARCHITECTURE-DECISIONS §1 (Container Apps, PostgreSQL Flexible Server, Key Vault, etc.) | Read §A as "Azure *screens* (e.g. Service Bus explorer) are reference only". Azure services are the hosting stack. Wording needs clarifying. |
| §C note "RAT owns worksheet viewer component" and IB-01 "back-end owned by WRK global search" | — | Rule 8: modules expose only public contracts; no cross-module table reads | Cross-module UI components must consume the owner's API/contract, not its tables. |

### 5.2 Vendor and partner products named (reference only, never components)
- **Guidewire:** PolicyCenter, ClaimCenter, Product Designer.
- **Partner apps:** Hi Marley (SMS, SCR-CC-01/02); CCG IQ, StrikeCheck and HVACi (vendor inspections, SCR-CC-04/05); Indico (AI claim intake, SCR-PX-01/02).
- **UI library evidence:** Appian CU (U1), AMIG (U6), Openkoda, Instanda and BriteCore (U7), Instanda and Akur8 RATE (U8), Quadient (U11), Temporal UI (I2), Peppol dashboard (K3), Transcend (K4).
- **Inspiration board:** Linear, Raycast, Superhuman, Claude Code, Cursor, Notion, Ramp, Pylon, Intercom, Mercury, Stripe, Attio, Airtable, Carbon, Bloomberg, Railway, Palantir, Figma, Datadog, Brex, Resend, Supabase, Hex, Vercel.
- PRDs must not name these as integrations unless a separate decision adds a Greek-market partner.

### 5.3 Guidewire and US concepts that must NOT be copied literally (translate per §A and system contract §3.3)
**Data-model and UI-framework terms:**
- Job / "job wizard", Policy Period, Branch, Sub #, Submission Manager, "Rewrite Remainder of Term", "Rewrite New Account", Preemption, "Release Lock / Lock for Review", Versions "Start Multi-Version / Side-by-Side", "Unsaved Work" drafts.
- PCF, Gosu, typelists / Typekey terms, "Covered Object Type" (PersonalAutoExcl/Cond), Existence "Electable/Required", Term Types "Direct/Generic/Option/Package/Typekey", Columns "ChoiceTerm1/BooleanTerm1", Grandfather States, Audit Schedules, System Tables, Change Lists, Workspaces, "Commit All".

**Organisation and access terms:**
- Producer Code, Producer of Record / of Service, Producer Tier, Affinity Group, UW Company, Security Zone, Load Factor, Regions.
- Role permission codes (advancesubmission, bindpolchange…), "All Permissions (Deprecated)".

**US demo data and US-only concepts (§A says translate or mark "not applicable in GR"):**
- SSN / Tax ID (SSN) / Official IDs → AFM and Greek ID.
- US states, ZIP "#####-####", County, "Default Base State", "Jurisdiction" lists of US, CA, AU and DE states → Greek regional units and postal codes; jurisdiction = country pack.
- "License State" / License # / License Class "LIC CDL:ELG" → issuing country plus licence category.
- MVR (order, status, "Do Not Order MVR", Retrieve MVR, violations and points) → driving-record or claims-history source, flagged where none exists in GR.
- PIP per state, Full/Limited Tort, Uninsured/Underinsured Motorist, Mexico Coverage, "Auto Liability Package 25/50/25 / CSL", Good Student / Good Driver discounts, Passive Restraint, Anti-Theft Discount.
- "AL Tax" / "Taxes & Surcharges" → Greek premium tax and levies via country-pack TaxCalculator.
- USD and multi-currency lists (AUD, CAD, JPY, RUB) → EUR.
- "Check / Check #" cheques → SEPA payments.
- "Assigned Risk", Workers' Comp, CPP / Commercial Package lines, Industry Code, Regional Format "United States (English)" → el-GR / en via react-i18next.

**ClaimCenter terms:** "3-Point Contact", Workers' Comp claim segment, FROI snapshot, "Address Book".

**Recommendation for the orchestrator:** the coverage checker should accept "N/A in GR, reason" for these items, as §A allows.

---

## 6. Other template sections
- **3 Owned entities / 4 Consumed / 5 Events / 6 APIs / 7 SPIs:** n/a. Implied needs only: a PLT business-day calendar (CD-17); PLT authority framework (CD-05); WRK groups and queues (CD-10); DOC form patterns and inbound documents (CD-09); a country pack for jurisdictions and tax.
- **8 Screens:** see §3 above; patterns IB-01…33.
- **9 Regulatory rules stated explicitly** (by name only; none is specified with rates or deadlines):
  - Greek nat-cat refusal letter (UIL-U5)
  - "statutory 3-month clock" on claims (UIL-C7)
  - "50-day reply clock" for complaints (UIL-K1)
  - Premium tax and **Auxiliary Fund** levy returns (UIL-F3)
  - IFRS 17 / Solvency II QRTs (UIL-F4, UIL-K2)
  - myDATA analogue fiscal-document rejection queue (UIL-K3)
  - GDPR SAR (UIL-K4)
  - DORA ICT incident reporting (UIL-I4)
  - EU online withdrawal button "from June 2026" (UIL-P4)
  - SEPA returns/chargebacks and IBAN check (VoP) (UIL-B3, UIL-B4)
  - Friendly Settlement (UIL-C6)
  - Cancellation notice periods (SCR-PCTX-09 → CMP)

  All parameters are gaps, to be defined by the owning PRD or country pack.
- **10 Greek-market specifics:** only through the translation rule (§A) and the UIL rows above. AFM, gov.gr and the bureau/Information Centre are not mentioned.
- **11 Controls:**
  - Maker-checker named for Data Change (SCR-PCADM2-08).
  - Authority framework (CD-05; SCR-PCADM1-14, SCR-PCADM2-01, UIL-U6, UIL-U4, UIL-C10, UIL-B5 "authority limits").
  - Audit log viewer (UIL-K5; reference shows an identity log, "not business events").
  - IB-14 forbids inline edit on fields that need a transaction, approval or maker-checker.
- **12 AI features** (patterns only, no toggles): IB-06–IB-10, IB-22, IB-25, IB-27, IB-30, IB-31; SCR-PX-01/02 AI intake. IB-07 / IB-31 require human approval before any system-of-record change.
- **13 Open issues:** none listed in the source. The issues raised in this digest are in §4.4 and §5.

## 7. Build notes
- **Hardest to cover:**
  - The POL wizard family (50 screens, 777 field rows): submission, change (incl. out-of-sequence/preemption), cancel, reinstate, rewrite, renew. Shared components (drivers, vehicles, coverages, quote, review/diff) should cover several SCR IDs each under coverage rule (b).
  - The PFC product model (29 screens), which must be redesigned without Gosu scripting.
- **Must exist first:**
  - PLT shell (SCR-PCSUB1-13), Entra sign-in, users, roles and authority (CD-05), holidays (CD-17).
  - WRK activities, groups and queues (CD-10).
  - PTY account/party.
  - PFC product version.
  - Then POL submission → quote → bind for Personal Auto (motor).
- **Suggested coverage tracking:** a machine-readable matrix of the 218 IDs (plus `›` field references) per PRD section 6, with the status "specified / replaced (where) / N/A in GR (reason)". The orchestrator should fix the garbled consumer cells in §4.4 first.
