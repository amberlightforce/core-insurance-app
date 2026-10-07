# Digest — PRD-09 Finance Sub-ledger and Accounting (FIN)

Source: `core-insurance-prds/PRD-09-finance-subledger-accounting.md` (2,213 lines, read in full). Version 1.2, dated 2026-10-07, status "Draft — baseline 1.0 freeze candidate". Binding input: 00-system-contract.md v1.11, plus PRD-18 decisions D1–D10. Section numbers below (§) are the PRD's own.

---

## 1. Identity

- **Module code:** FIN. **Title:** Finance Sub-ledger and Accounting. Owners (§1.1): product owner for finance systems (ROLE-14 specialisation) is accountable. The lead architect owns the posting engine, books and reconciliation. ROLE-24 finance controller owns close, controls and the GL interface. ROLE-25 insurance accountant owns posting rules and the chart. ROLE-26 tax specialist owns tax and levy. ROLE-35 IFRS 17 actuary co-owns grouping and PAA.
- **Purpose (§1.2):** FIN turns business facts published by POL, BIL, CLM, RI, DAT and its own runs into immutable, balanced journals. Versioned, approved posting rules do the mapping. There are up to three **books** per legal entity (LOCAL_GAAP, IFRS17, SOLVENCY_II). Every line holds transaction, functional and USD group currency with rate ids. FIN also:
  - runs earning (UPR / PAA LRC / SII premium base) from POL segments;
  - assigns IFRS 17 portfolio, cohort and profitability group at initial recognition;
  - accounts for IPT and the Auxiliary Fund levy and prepares their returns;
  - revalues foreign-currency balances and translates to USD;
  - orchestrates period close and reconciles six ways (policy, billing, sub-ledger, GL, RI, fiscal);
  - sends summarised journals to the corporate ERP.
- **Five key decisions (§1.2):**
  1. Two ledgers (BIL billing sub-ledger, FIN books), reconciled daily (CD-15). There is one posting source per economic fact.
  2. Rules, not code, make journals. There is no default account; unmatched events go to an intake exception queue.
  3. One earning computation for every book (`pol.Earning.compute`).
  4. FIN assigns IFRS 17 grouping once, at recognition, from events. Bind never waits for FIN (R-37).
  5. The statutory basis is configuration. In Greece IFRS17 is both the statutory and group book, and LOCAL_GAAP is inactive (Law 4308/2014 Art. 1 §3; CCR-FIN-01).
- **Non-goals / out of scope (§1.4):**

| Out of scope | Owner |
|---|---|
| Billing sub-ledger, receivables, cash, suspense of unapplied cash, commission calculation and payment runs, levy remittance payments, bank reconciliation | BIL |
| Charge deltas, segments, the earned-premium function, IFRS 17 tag proposals | POL |
| Charge types, GL keys, earning patterns, regulatory mapping | PFC |
| Claim financial calculations | CLM |
| Cession and RI recovery calculations, bordereaux, statements | RI |
| Actuarial models (IBNR, RA, discounting, SII technical provisions), regulatory marts and report generation (QRT, BoG, XBRL) | DAT, CMP |
| myDATA transport, submission tracker, statutory clock register | CMP |
| Corporate ERP GL, AP, fixed assets, payroll, consolidation | External |
| FX rates store, calendars, numbering, audit, authority, maker-checker, workflow, time | PLT |
| Configuration and SPIs | MKT |
| Activities and queues | WRK |

- **Important boundary:** FIN never posts to a GL suspense account (REQ-FIN-084). "Suspense" (unapplied cash) belongs to BIL. FIN's equivalent is the "intake exception" (R-82).

## 2. Size metrics

| Metric | Count | Source |
|---|---|---|
| REQ (functional) | **290** (REQ-FIN-001…011 anchors + 030…308, contiguous) | §5.18, §16.6 |
| — Must / Should / Could / Won't | 256 / 29 / 5 / 0 | §5.18 |
| — Must by phase | P1 251, P2 2 (REQ-FIN-140, -168), P3 3 (REQ-FIN-166, -304, -305), P4 0 | §5.18 |
| — [BASELINE] / [ENHANCEMENT] | 268 / 22 | §5.18 |
| BR | 62 (BR-FIN-001…111, non-contiguous). I recounted from §10.1 and it matches §16.6 | §10.1 |
| NFR | 23 (NFR-FIN-001…023) | §14 |
| Screens | 14 (SCR-FIN-01…14); owned UI-library items UIL-F1…F4 all specified | §6, §6.15 |
| Owned entities | ~31 in §7.1 tables (counting CloseTemplate/CloseTask as one, IntakeException as a view) | §7.1 |
| Events produced | 13 (5 contract base + 8 added by CCR-FIN-02/R-47) | §8.1 |
| Event types consumed | ~70 across BIL, POL, CLM, RI, DAT, CMP, PFC, MKT, PLT, WRK, MIG | §8.2 |
| Inbound API operation groups | ~30 groups in §9.1 (each with several operations) | §9.1 |
| AI features | 7 (AI-FIN-01…07), all off by default | §11 |
| Golden fixtures | GF-01…GF-14; 11 property invariants | §14.x |
| Open issues | OI-FIN-01…15 (7 closed: 01, 02, 03, 05, 07, 08, 15) | §16.1 |
| CCRs | CCR-FIN-01…06, all Accepted | §16.4 |

**Phase breakdown (my own computation from the tables):**

| Phase | Must | Should | Could | Total |
|---|---|---|---|---|
| P1 | 251 | 24 | 0 | 275 |
| P2 | 2 | 1 (REQ-FIN-174) | 1 (REQ-FIN-102) | 4 |
| P3 | 3 | 2 (REQ-FIN-130, -133) | 3 (REQ-FIN-103, -114, -269) | 8 |
| P4 | 0 | 2 (REQ-FIN-196, -214) | 1 (REQ-FIN-296) | 3 |

- **Motor MVP (P1) count:** 275 requirements, of which **251 are Must**.
- **Build-size estimate: L.** There are 251 Must P1 requirements, more than twice the L threshold. The module is all money and temporal logic: multi-book posting engine, a CEL-compatible rule language (D10), deferred balancing constraints in three currencies, bitemporal as-of balances, daily incremental earning over 1.2 M terms, IFRS 17 grouping and PAA, FX revaluation and translation, six-way reconciliation, tax returns, close orchestration and 14 screens.

## 3. Owned entities

Common to all business rows (§7.0):
- Schema `fin`.
- Every row carries `legal_entity_id`, `jurisdiction`, `created_at/by` and `record_version`. Business-valid rows also carry half-open `[valid_from, valid_to)` with record time in UTC.
- Money is `decimal(19,4)` plus ISO 4217 currency. Ids are UUIDv7. Journal numbers come from PLT.
- No P2/P3 data is stored.

### 3.1 Intake and rules (§7.1.1)

| Entity | Key attributes | Constraints / state |
|---|---|---|
| BusinessEvent | source module/event id, event type, schema version, aggregate + sequence, entity, jurisdiction, accounting & business date, 3-currency amounts + rate ids, refs (charge, term, transaction, invoice, entry, claim, exposure, cession, result set), dimensions, origin LIVE/MIGRATION, config hash, correlation id, business lineage keys (D5), set completeness `set_id/set_size/index` (D4), disbursement source type (D1), causation id, status, exception reason, attempts, journal ids | unique (source_module, source_event_id); state machine §7.3.1 |
| FinanceEventCatalogueEntry | event type, producer, schema versions, relevance POSTING/CONTEXT/RECONCILIATION/IGNORED, amount fields, books | one per type and major version |
| PostingRule | key (event type, charge category, bill mode, jurisdiction, book) + qualifiers, specificity, line templates (account or derivation, side, amount expression, dimension assignments), pack id/version, EN/GR description | exactly one match per key at compile |
| RuleSetVersion | entity, book, version no., state, effective_from, content hash, compile/golden results, impact preview, maker, checker, reason | maker ≠ checker; effective_from not in a Locked period |
| AccountDerivation | book; PFC GL key × qualifiers → account; effective period | no overlap |

**BusinessEvent states (§7.3.1):**

| From | To | Guard / event |
|---|---|---|
| (new) | Received | — |
| Received | Waiting | dependency missing (sequence gap, group, mapping, rate) |
| Waiting | Received | dependency arrived |
| Waiting | Suspended | timeout; emits `BusinessEventSuspended` |
| Received | Posted | journals committed in all active books; emits `JournalPosted` |
| Received | NoPosting | CONTEXT/RECONCILIATION relevance, or an explicit NO_POSTING rule |
| Received | Suspended | no rule, invalid, account inactive, tax rule violation |
| Suspended | Received | re-run after a fix |
| Suspended | Rejected | duplicate or out of scope; four-eyes |

**RuleSetVersion states (§7.3.2):**
- Draft → Submitted (compile and golden tests pass) → Approved (checker ≠ maker) → Active (effective date reached) → Superseded.
- Submitted or Approved → Draft (returned).
- Draft, Submitted or Approved → Withdrawn.
- Emits `PostingRuleSetActivated` on Active.

### 3.2 Journals and balances (§7.1.2)

| Entity | Key attributes | Constraints |
|---|---|---|
| JournalEntry | journal number, entity, book, accounting/business date, period, source type (EVENT, RUN, MANUAL, MIGRATION, ACTUARIAL) and refs, rule ids + rule-set version, reverses/reversed_by, reason, status (Posted, Reversed), correlation id, hash-chain value, posted_at | number unique per entity, book and fiscal year (gap-free, REQ-FIN-069); append-only |
| JournalLine | account, side, amount_txn + currency, amount_functional, amount_group (USD), rate ids and types, dimensions (REQ-FIN-075 list), line text | Σ debit = Σ credit per txn currency, functional and group (deferred constraint, REQ-FIN-068); append-only via triggers for every role including the schema owner (REQ-FIN-070) |
| BalanceSnapshot | entity, book, account, period, balance dimension key, debit/credit/net per currency, as-of record time, verification status | derived; verified nightly (REQ-FIN-077) |
| IntakeException | view over BusinessEvent (R-82): reason, root-cause group, owner, activity id, resolution (RERUN, DUPLICATE, REJECTED), approval ref | one per suspended event |

**Dimension set on every line (REQ-FIN-075):**
- entity, book, jurisdiction, LoB, product and version, coverage, charge type;
- SII LoB, national statistical class;
- IFRS 17 portfolio, cohort and group;
- RI contract and section, treaty year;
- accident year, underwriting year, cost type;
- channel, producer code, intermediary id;
- claim id, policy term id, transaction id, charge id;
- cat code, currency, business date.

### 3.3 Chart, mappings, dimensions, book profile (§7.1.3)

- **BookProfile:** active books, statutory book, group book, functional currency, and fiscal, cohort and group calendars. It also holds the entity's policy settings: revaluation, LRC premium basis, multi-currency groups, and acquisition-cash-flow elections per portfolio. Effective-dated, maker-checker.
- **Chart:** kind is ENTITY, LOCAL_STATUTORY_VIEW, GROUP, SII_VIEW or IFRS17_PRESENTATION. Owner is core, pack, entity or group.
- **GlAccount** (contract name "Account (GL)", CON-029): code, EN/GR names, type (asset, liability, equity, income, expense, memo, clearing), normal balance, monetary flag, currency behaviour, system-only flag, mandatory and allowed dimensions, status, effective period.
- **AccountMapping:** many-to-one, effective-dated. Must be complete for every non-zero balance (REQ-FIN-095).
- **DimensionDefinition / DimensionValue:** value sources are MKT code lists, owner events or FIN lists.

**Reference chart (§7.1.4, illustrative codes; packs and entities extend):**

| Range | Accounts |
|---|---|
| Assets | GL-1110 Cash; 1150 Cash in transit; 1210 / 1220 Premium receivable (policyholder / intermediary); 1310 RI held asset for remaining coverage; 1320 RI held asset for incurred claims; 1330 Loss-recovery component; 1410 DAC (LOCAL_GAAP, non-monetary); 1510 SII RI recoverables |
| Liabilities | GL-2110 LRC excl. loss component (system-only); 2120 LRC loss component; 2130 UPR (LOCAL_GAAP, system-only); 2210 / 2220 / 2230 / 2240 LIC case / IBNR / risk adjustment / discounting; 2250 / 2260 SII best estimate / risk margin; 2310 Commission payable; 2315 Accrued commission; 2320 Reinsurers current accounts; 2410 IPT payable per class ("Never reduced by cancellations"); 2420 Levy payable; 2425 Stamp duty on levy (Greece pack); 2430 Withholding; 2530 Disbursements in transit (BIL LA-13) |
| Clearing | GL-2510 Claim payments; 2515 Friendly Settlement (Greece pack); 2520 Commission |
| Equity | GL-3110 Retained earnings; 3210 Translation reserve (group view); 3310 Insurance finance reserve (OCI); 3410 SII valuation differences |
| Income | GL-4010 Insurance revenue; 4020 Premiums written and 4030 Change in UPR (LOCAL_GAAP); 4110 Fee income; 4210 Amounts recovered from reinsurers |
| Expense | GL-5110 Incurred claims; 5120 Claims handling; 5130 Onerous losses; 5210 Allocation of RI premiums; 6110 Acquisition cash flows; 6120 Amortisation; 6150 Levy expense; 6160 Tax and levy borne on refunds; 6210 Complaint redress |
| Finance and FX | GL-7110 Insurance finance expense; 7210 / 7220 FX unrealised / realised |
| Memo | GL-9010 Memo policy count |

### 3.4 Earning, IFRS 17, SII (§7.1.5)

- **EarningRun:** mode DAILY, PERIOD_END or SIMULATION; as-of date; knownAt; segment cursor; partitions; totals per book; invariants. One non-simulation run per entity and as-of date.
  - States (§7.3.5): Scheduled → Running → Completed / CompletedWithExceptions / Failed. Failed → Running (retry failed partitions only).
  - Emits `EarningRunCompleted` on Completed or CompletedWithExceptions.
- **EarningResult:** written, earned-to-date, unearned and earned-in-run per term × element × charge type, with pattern version and the business periods affected. Invariant: earned + unearned = written.
- **Ifrs17Group:** code (portfolio-cohort-profitability-model), kind ISSUED or REINSURANCE_HELD, currency policy, PAA eligibility and evidence, coverage span.
  - States (§7.3.9): Open → ClosedToNewContracts → RunOff → Derecognised.
  - The profitability class never changes. Onerous status (None / Indicated / Confirmed / Released) lives on OnerousAssessment.
- **Ifrs17GroupAssignment:** term (or RI contract) → group, recognition date, POL proposal, decision-table version and rule id, version, correction reason and approvals. One current version per term; versions are immutable.
- **Ifrs17AssignmentRuleSet:** decision table. Maker-checker by ROLE-35 and ROLE-24.
- **RiHeldLink:** issued ↔ RI-held, many-to-many, with share and effective period.
- **OnerousAssessment.**
- **ActuarialResultSet:** approver ≠ submitter.
  - States (§7.3.10): Submitted → Validated / ValidationFailed → Approved / Rejected → Posted → Superseded.

### 3.5 Close, recon, manual, tax, FX, extract (§7.1.6)

**ClosePeriod:** kind MONTH, QUARTER or YEAR; cut-offs per source.

| From | To | Guard / event |
|---|---|---|
| Open | SoftClosed | cut-off reached for all sources |
| SoftClosed | Open | cut-off extension approved |
| SoftClosed | Locked | REQ-FIN-235 gates pass and controller signs off; emits `PeriodClosed` |
| Locked | Reopened | four-eyes approval with impact list; emits `PeriodReopened` |
| Reopened | Locked | gates pass again; emits `PeriodClosed` with re-lock flag |

**CloseTemplate / CloseTask:** task type SYSTEM, CHECK or MANUAL. Task status Pending / Running / Done / Failed / Skipped-with-approval.

**ReconciliationRun / ReconciliationBreak:**
- Break types: MISSING, AMOUNT, CLASSIFICATION, TIMING, DUPLICATE.
- States (§7.3.6): Open → Investigating → Resolved → Closed. Open or Investigating → Accepted (still counted open) → Resolved. Open → Resolved automatically for timing differences.

**ManualJournal (§7.3.4):**
- Draft → Submitted → PendingApproval → Approved → Posted.
- PendingApproval → Rejected → Draft.
- Submitted or PendingApproval → Withdrawn. PendingApproval → Expired.

**LedgerInvariantResult:** renamed from InvariantResult (XMR-CR-FIN-05).

**TaxReturn / LevyReturn / ReturnAdjustment:**
- TaxReturn is the only holder of IPT periods, due dates and filing status (R-82).
- States (§7.3.7): Draft → Prepared → Reviewed → Approved (approver ≠ preparer) → Filed (emits `TaxReturnFiled`) → Accepted / Rejected → Amended.
- TaxReturn: Approved or Filed → Settled when the `TAX_REMITTANCE` payment clears.
- LevyReturn: Filed or Approved → Settled on `LevyRemitted`.
- Draft, Prepared or Reviewed → Draft (returned or recalculated).

**FxRevaluationRun:** REVALUATION or TRANSLATION_USD. Rates are locked after period Lock.

**GLExtract:**
- Each line appears in exactly one extract per target.
- States (§7.3.8): Generated → Sent (emits `GLExtractSent`) → Acknowledged / Rejected / PartiallyAccepted → Superseded.

**Retention classes:** `RC-FIN-BOOKS`, `RC-FIN-INTAKE`, `RC-FIN-EXTRACT`. Durations come from the pack and are UNVERIFIED (OI-FIN-10).

## 4. Consumed entities / dependencies (§7.2, §9.2, §15.1)

| Owner | What FIN reads | How |
|---|---|---|
| POL | PolicyTerm, Transaction, Segment, ChargeDelta | Events `PolicyBound`, `RenewalBound`, `PolicyRewritten`, `ChargeDeltaEmitted`, change/cancel/reverse/reapply events. API: `pol.Earning.compute` (REQ-POL-118), `pol.Segment.changes` (REQ-POL-094), `pol.Charges.reconcile` (REQ-POL-122), `pol.Policy.get` |
| PFC | ChargeType, RegulatoryMapping: GL key, earning pattern, tax class and base flag, fiscal category, SII LoB splits, IFRS 17 portfolio, stat class, taxonomy version | `pfc.ChargeType.list`, mapping query; `ProductVersionApproved/Published` |
| BIL | BillingLedgerEntry (the posting source for all money facts), TaxLevyPeriod (Auxiliary Fund remittance only), CommissionCalculation, disbursements and receivables with source types | `BillingEntryPosted`. API: `bil.Ledger.query/balance`, `bil.TaxLevy.periods`, `bil.Reconciliation.fin` (investigation only), `bil.Disbursement.request` (`TAX_REMITTANCE`), replay (REQ-BIL-315). BIL pushes daily totals into `fin.Reconciliation.exchange` |
| CLM | ClaimFinancialTransaction, ReserveLine, Recovery | Events `ReserveChanged`, `PaymentIssued`, `PaymentVoided`, `RecoveryRecorded`; daily totals (REQ-CLM-106) |
| RI | RIContract, Cession, RIRecovery, StatementOfAccount | Events (§5). API: `ri.Contract.get/list/versionAt`, open balances (REQ-RI-207/208), posting keys, reconciliation totals |
| CMP | FiscalDocument; clocks; submissions | `FiscalDocRegistered/Rejected/Cancelled`, `Clock*`. API: `cmp.Clock.start/stop/get`, `cmp.Submission.create/recordFiling` |
| DAT | Model registry; actuarial result sets | DAT calls `fin.ActuarialResults.*`; `ActuarialResultsPublished`, `RegulatoryMartPublished` |
| PLT | FX rates, calendars, numbering, audit, authority, approval, SoD, workflow, scheduler, decision tables, incidents, time, replay, AI control plane, retention, evidence export | `plt.*` operations (§9.2) |
| MKT | Configuration, rounding, code lists, packs, SPIs | `mkt.Configuration.resolve`, `mkt.Rounding.apply`, SPIs |
| PTY | Intermediary and party names (display only) | `pty.Party.get` |
| DOC | Evidence and return files | `doc.Archive.store/get` |
| WRK | Activities | `wrk.Activity.create/complete` |
| MIG | Batch events | `MigrationBatchLoaded/Reconciled` |

**Rules on synchronous calls:**
- Intake must make no synchronous calls to other modules during posting, except PLT reference data and MKT configuration (REQ-FIN-045).
- Earning makes synchronous calls to POL, but only for terms that changed (REQ-FIN-108).

## 5. Events

Topic `fin.events.v1` (§8). The partition key is `legal_entity_id` for every FIN event except `Ifrs17GroupAssigned`, which is keyed by `assignment_subject_id`. XMR-F-117 per-aggregate keying is explicitly not applied. Payloads carry ids, totals and lineage keys, and no personal data.

### 5.1 Produced (§8.1)

| Event | Trigger | Key payload | Consumers |
|---|---|---|---|
| `JournalPosted` | Journal or run batch committed | journal ids/numbers, book, accounting date, period, source refs, totals per currency | CLM (REQ-CLM-113), RI (REQ-RI-091, -235), DAT |
| `EarningRunCompleted` | Run Completed / CompletedWithExceptions | run id, as-of, mode, earned per book and LoB, exception count | DAT |
| `PeriodClosed` | Lock or re-lock | period id, kind, books, lock time, key totals, re-lock flag | BIL (REQ-BIL-291), RI, WRK, DAT |
| `ReconciliationBreakRaised` | New break | break id, leg, type, amount, currency, owner module, severity, refs | WRK (REQ-WRK-198), BIL, RI, DAT, MIG |
| `GLExtractSent` | Extract delivered | extract id, target, book, range, totals, hash | DAT |
| `Ifrs17GroupAssigned` | Assignment stored (initial or corrected; issued or RI-held) | subject kind TERM / RI_CONTRACT, ids, group id/code, portfolio, cohort, profitability, model, recognition date, version | POL (REQ-POL-034), DAT |
| `PostingRuleSetActivated` | Version Active | version id, book, effective from, hash | DAT |
| `BusinessEventSuspended` | Intake exception | event id, source, reason, amount | DAT |
| `ReconciliationBreakResolved` | Break Resolved | break id, resolution | DAT, MIG |
| `PeriodReopened` | Reopened | period, reason, approvers, impact | POL (REQ-POL-108), WRK, DAT |
| `FxRevaluationCompleted` | FX run done | run id, type, period, totals | DAT |
| `TaxReturnFiled` | Return Filed | return id, tax code, period, totals, filing ref | CMP (REQ-CMP-192), DAT |
| `ActuarialResultSetPosted` | Set Posted or Rejected | set id, run id, status, findings count | DAT (REQ-DAT-004) |

### 5.2 Consumed (§8.2)

| Event(s) | Producer | Handling |
|---|---|---|
| `BillingEntryPosted` | BIL | **Posting source** for written, billed, collected, allocated, reversed, refunded, written-off, commission, levy, IPT and disbursement facts (REQ-FIN-036) |
| `ChargeDeltaEmitted` | POL | Context and reconciliation. Processed as complete sets via `set_id/set_size/index` (REQ-FIN-308) |
| `PolicyBound`, `RenewalBound`, `PolicyRewritten` | POL | IFRS 17 assignment (REQ-FIN-011, -127) |
| `PolicyChanged/Cancelled/Voided/Reinstated`, `TransactionReversed/Reapplied` | POL | Earning recompute flags; transaction kind and cancellation source for the tax check; complete sets |
| `InvoiceIssued`, `PaymentReceived`, `CashAllocated`, `PaymentReversed`, `RefundApproved/Disbursed`, `WriteOffPosted`, `Disbursement{Issued,Voided,Returned}`, `Commission{Calculated,Paid,StatementIssued}` | BIL | Context and reconciliation only |
| `DisbursementCleared/Rejected/Stopped` | BIL | Clearing checks. For `TAX_REMITTANCE`: return Settled, clock stop, or an unpaid activity |
| `LevyAccrued`, `LevyRemitted` | BIL | Levy reconciliation; LevyReturn Settled |
| `ReserveChanged`, `PaymentIssued`, `PaymentVoided`, `RecoveryRecorded` | CLM | **Posting source** for claims |
| `ExposureCreated`, `TransactionSetApproved`, `ClaimReported/Closed/Reopened`, `CatEventAssigned` | CLM | Context only |
| `RIContractActivated` | RI | Create the RI-held group (REQ-FIN-139) |
| `CessionCalculated`, `RecoveryCalculated`, `ReinstatementPremiumDue`, `StatementIssued`, `SettlementRecorded`, `CashCallRaised` | RI | **Posting source** for RI |
| `DepositPremiumDue` | RI | Posting source, P1 (REQ-FIN-165) |
| `PremiumAdjustmentCalculated`, `CommissionAdjusted` | RI | Posting source from RI's P3 |
| `CessionExceptionRaised`, `BordereauGenerated` | RI | Context only |
| `ActuarialResultsPublished`, `RegulatoryMartPublished` | DAT | Fetch and validate the set; marts status |
| `FiscalDocRegistered/Rejected/Cancelled` | CMP | myDATA reconciliation. A cancelled registration reopens the item awaiting a new MARK |
| `ClockStarted/Warned/Breached/Met` | CMP | Return due-date states. `ClockElapsed` is explicitly not consumed |
| `ProductVersionApproved/Published` | PFC | Unresolvable GL key check (REQ-FIN-065) |
| `RegimeCodeListPublished`, `PackActivated`, `PackRolledBack`, `ConfigurationActivated` | MKT | Dimension values, pack rows |
| `ReferenceDataPublished`, `ConfigChanged`, `FeatureFlagChanged`, `ApprovalDecided`, `AuthorityGrantChanged`, `AiToggleChanged`, `AiKillSwitchActivated`, `LegalHoldApplied/Released` | PLT | Release Waiting events, reload config, complete approvals, AI off, retention |
| `ActivityCompleted` | WRK | Close MANUAL tasks |
| `ModelDriftDetected`, `BiasThresholdBreached` | DAT | AI auto-disable |
| `MigrationBatchLoaded/Reconciled` | MIG | Opening-balance reconciliation |

All consumers de-duplicate on `event_id` (REQ-FIN-031).

## 6. APIs

### 6.1 Exposed (§9.1)

In-process operations, also exposed as REST under `/api/fin/v1/`.

- **IFRS 17**
  - `fin.Ifrs17Group.assignment(termId, validAt)` and `riHeldAssignment(riContractId, sectionId?, validAt)`. Both return PENDING rather than an error if not yet assigned.
  - `fin.Ifrs17Group.get/list`; `correctAssignment` (approval required).
  - `fin.Ifrs17.movements`.
- **Journals and balances:** `fin.Journal.query/get`; `fin.Journal.reverse` (errors `FIN-ERR-ALREADY-REVERSED`, `-PERIOD-LOCKED`, `-SOURCE-OWNED`); `fin.Balance.get/trialBalance` (with knownAt).
- **Posting rules:** `fin.PostingRules.list/draft/compile/test/previewImpact/submit/decide`.
- **Intake exceptions:** `fin.IntakeException.list/rerun/markDuplicate/reject`.
- **Manual journals:** `fin.ManualJournal.create/submit/withdraw/decide/get/list`.
- **Earning:** `fin.Earning.run/status/simulate/result`.
- **Period and close:** `fin.Period.status/list` (POL's closed-period guard); `fin.Close.tasks/signOff/softClose/lock/reopen/acceptBreak/evidencePack`.
- **Reconciliation:** `fin.Reconciliation.exchange` (receives BIL, CLM and RI daily totals; FIN owns the receiving side, XMR-F-106); `fin.Reconciliation.detail/breaks/match/run`; `fin.FiscalReconciliation.run/results`.
- **Tax and levy:** `fin.TaxReturn.list/get/recalculate/addAdjustment/generateFile/transition/recordFiling/requestPayment`; `fin.LevyReturn.*`.
- **FX and group:** `fin.Fx.revalue/translate/runs`; `fin.GroupPack.export`.
- **GL extract:** `fin.GLExtract.generate/replay/corrective/acknowledge/list`.
- **Actuarial:** `fin.ActuarialResults.submit/validate/approve/reject/get`; `fin.ActuarialInputs.extract`.
- **Chart:** `fin.Chart.*`, `fin.Mapping.*`, `fin.Dimension.*`.
- **Migration:** `fin.Import.openingBalances/ifrs17Groups/earningBaseline/lic`; `fin.Import.reverseBatch`; `fin.Import.convertCurrency` (optional, P4).
- **Other:** `fin.WrittenPremium.query`; `fin.Dsar.export/restrict`.
- **Command rules:** every command is idempotent. Dry-run is available where §9.1 marks it.
- **Named error codes:** `FIN-ERR-COMPILE`, `-GOLDEN`, `-EVIDENCE-REQUIRED`, `-UNBALANCED`, `-TEMPLATE-ACCOUNT`, `-RUN-IN-PROGRESS`, `-GATES-FAILED`, `-APPROVAL-REQUIRED`, `-TOTALS-SCHEMA`, `-TOLERANCE`, `-RECON-UNEXPLAINED`, `-SPI-UNBOUND`, `-RETURN-NOT-APPROVED`, `-RATE-MISSING`, `-EXTRACT-STATE`, `-RESULTSET-INCOMPLETE`, `-MAPPING-INCOMPLETE`, `-IMPORT-*`, `-IMPORT-REVERSAL-BLOCKED`, `-NOT-FOUND`, plus `PLT-ERR-SOD`.

### 6.2 Consumed

Listed in §4 above (POL, PFC, BIL, CLM, RI, CMP, DAT, DOC, WRK, PLT, MKT, PTY).

### 6.3 External integrations (§9.3)

All go through the PLT adapter host:
- Corporate ERP: CSV/JSON plus acknowledgement. The ERP is not selected (OI-FIN-13).
- Fairfax group consolidation: format UNVERIFIED (OI-FIN-09).
- AADE IPT return: via `TaxReturnFormat`; form UNVERIFIED (OI-FIN-04).
- Auxiliary Fund: form UNVERIFIED.
- myDATA: via CMP only.
- FX source: via PLT.

## 7. SPIs / country-pack interfaces (§10.4, §9.2)

- **`TaxReturnFormat`.** FIN is the main caller. It renders returns with control totals (REQ-FIN-186).
  - CCR-FIN-03 (Accepted R-49) extends it with `generate(period, totalsByClass, detailLines?, adjustments, entity)`, `validate(file)`, `channel()` (FILE_UPLOAD / API / VIA_CMP + clock code) and `periodScheme(taxCode)`.
- **`TaxCalculator`.** Provides rates for reconciliation checks, and the `treatment(chargeType, transactionKind, cancellationSource)` operation.
  - `treatment` returns `authorityLiability` REDUCE or NOT_REDUCE, plus `ruleId` and `ruleVersion` (REQ-MKT-330, D2).
- **Other SPIs:** `RegimeCodeList`; `StatutoryClockSet` (via CMP); `FiscalDocumentChannel` (via CMP); `NumberingScheme`, `HolidayCalendarProvider`, `FxRateSource` (via PLT); `MotorCompensationBodyAdapter` (CLM's; FIN only posts its results, REQ-FIN-160, -170).
- **Pack contributions:**
  - posting-rule rows, editable only through pack versioning (REQ-FIN-057);
  - chart extensions (e.g. GL-2425, GL-2515) and the Greek statutory chart view;
  - tax and levy definitions, liability point, periods, due dates and treatments;
  - fiscal classification mapping, code lists, statutory basis default, retention durations.
- Architecture tests forbid country literals in FIN code (REQ-MKT-008, REQ-PLT-321).
- **Cyprus stub (§10.4):**
  - LOCAL_GAAP active (proves multi-GAAP and DAC);
  - no Auxiliary Fund levy;
  - synthetic flat charges effective to 2025-12-31 and zero from 2026 (REQ-MKT-265);
  - synthetic motor fund contribution;
  - fiscal leg disabled (`NotRequired`);
  - cohort calendar from 1 April (synthetic).
  - GF-01 and GF-02 run under both packs.

## 8. Screens (§6)

**Shared conventions (§6.0):**
- Staff workbench only.
- Global filter: entity, book (default statutory), period (default current open) and currency view (transaction / functional / USD; default functional).
- Semantic states only (contract §3.9.9).
- Greek/English switch (R-101, REQ-MKT-005): instant, no reload, no loss of input. Business data shows as stored. Generated return files keep their own language.
- 26 FIN permissions are registered with PLT (`fin.read` … `fin.audit.read`).
- Design-guide patterns cited: IB-02, 03, 04, 05, 07, 08, 09, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 26, 28, 29, 30 and 32.

| ID | Name | Personas | One line |
|---|---|---|---|
| SCR-FIN-01 | Posting-rule browser and change request | ROLE-25 author, ROLE-24 checker, ROLE-43 | Split pane (IB-05). Rule key fields, line templates, compile and golden results, impact preview, approve-the-diff (IB-07). Pack rows read-only. AI-FIN-01 drafts |
| SCR-FIN-02 | Journal browser and drill-down | ROLE-25/24/26/28, group analyst, ROLE-43 | Log-style table (IB-29). 3-currency lines, rule link, reversal links, lineage strip, as-of balance. Compare books. Reverse via CORRECTION template. AI-FIN-06 |
| SCR-FIN-03 | Intake exception queue | Fin ops analyst, ROLE-25 | Priority queue grouped by root cause. Re-run, Mark duplicate, Reject (four-eyes). Items `overdue` after 2 business days |
| SCR-FIN-04 | Period-close cockpit | ROLE-24, task owners | Work-left home (IB-11). Progress indicator, gates panel, task board, Soft close / Lock / Reopen, evidence pack. AI-FIN-04 |
| SCR-FIN-05 | Reconciliation breaks dashboard | Fin ops, ROLE-24/26/28 | Status per leg per day. Exception-first break list, two-sided matching, force match (four-eyes), accept for lock. AI-FIN-02 |
| SCR-FIN-06 | Manual journal entry with approval | ROLE-25 maker, ROLE-24 checker | Template form, live balance per currency, evidence to DOC, approval timeline, CSV upload, auto-reverse and recurrence |
| SCR-FIN-07 | IFRS 17 group and cohort explorer | ROLE-35, ROLE-24, ROLE-28 | Groups, assignment evidence, onerous indicator, RI-held graph (IB-15), movement tables. Confirm onerous; edit decision table. AI-FIN-07 |
| SCR-FIN-08 | Earning run monitor | Fin ops, ROLE-25, ROLE-35 | Runs, partitions, invariants, simulation |
| SCR-FIN-09 | Tax and levy return preparation | ROLE-26 preparer, ROLE-24 approver, ROLE-29 | Statement view. Totals by class, levy shares, adjustments, reconciliation checks, file, status, filing ref and receipt, `TAX_REMITTANCE` payment link. AI-FIN-05. Shows "pack default, guarded" info |
| SCR-FIN-10 | FX revaluation run | Group analyst, ROLE-24 | REVALUATION or TRANSLATION_USD. Rates with fallback flags, balances revalued, translation difference, group pack export |
| SCR-FIN-11 | GL extract monitor | Fin ops, ROLE-24 | Extracts, control totals, ack status. Replay, corrective, manual ack (approval), accept gap |
| SCR-FIN-12 | IFRS 17 and Solvency II reporting view | ROLE-24, -35, -29, -43 | Tabs: IFRS 17 statement, SII balance sheet, statutory-to-SII reconciliation, actuarial sets (approve/reject), marts, taxonomy version |
| SCR-FIN-13 | Chart of accounts and mappings | ROLE-25, ROLE-24, ROLE-33 | Account tree, mappings per view, dimension rules, impact preview, four-eyes |
| SCR-FIN-14 | Finance operations home | Fin ops, ROLE-24 | Work views with counts, interface health, posting-lag hero metric (`warning` > 5 min, `error` > 15 min) |

Inventory (§6.15): UIL-F1 → SCR-FIN-02 and -06; UIL-F2 → SCR-FIN-04; UIL-F3 → SCR-FIN-09; UIL-F4 → SCR-FIN-07 and -12. Consumed screens are referenced only.

## 9. Regulatory, tax and statutory rules

### 9.1 Explicitly stated by the source

**IFRS 17 (OBL-IFRS17, §3.1):**
- Para. 14 (portfolios), 16 (three profitability groups), 22 (no group holds contracts issued more than one year apart), 24 (groups fixed at initial recognition, never reassessed). Implemented by REQ-FIN-011, -121…128 and BR-FIN-030…034.
- Profitability groups are ONEROUS, NO_SIGNIFICANT_POSSIBILITY and REMAINING (REQ-FIN-124). Under PAA the default is "no contract onerous at initial recognition unless facts and circumstances indicate otherwise". The configured decision-table default is "all NSP" (§10.2).
- Cohort = the cohort-calendar period containing the recognition date; default is the calendar year (BR-FIN-030, REQ-FIN-122).
- Initial recognition = earliest of coverage start, first payment due, and the date the group becomes onerous (BR-FIN-031, REQ-FIN-123; para. 25 numbering UNVERIFIED).
- Each renewal or rewrite is a new contract; endorsements stay in the term's group (BR-FIN-034).
- Para. 53–59, PAA:
  - Eligible if every contract's coverage is ≤ 1 year (BR-FIN-035, threshold 12 months), else with an approved actuarial assessment. Otherwise the group is flagged GMM_REQUIRED.
  - LRC = premiums received (or receivable per policy) − acquisition cash flows paid (unless expensed) ± amortisation − revenue ± financing ± FX ± loss component (REQ-FIN-131, para. 55).
  - No time-value adjustment where premium due is ≤ 1 year from coverage (REQ-FIN-133, para. 56).
  - Onerous facts and circumstances indicator; default threshold is expected combined ratio 100% (BR-FIN-038, para. 57).
  - Loss component (para. 58).
  - Acquisition cash flows may be expensed if coverage ≤ 1 year (para. 59(a)); default IFRS17 EXPENSE, LOCAL_GAAP DEFER.
  - LIC need not be discounted if claims are paid within 1 year (para. 59(b)); P&L or OCI option per portfolio.
- Para. 30 with IAS 21: groups (issued and RI-held) are monetary items, revalued at the closing rate. IFRIC agenda decision (Oct 2022): multi-currency group policy is an entity choice; default SINGLE_CURRENCY_GROUPS (BR-FIN-060).
- Reinsurance held (para. 60–70A, 66A–66B; numbering UNVERIFIED): separate groups per RI contract or section, links with share, a loss-recovery component at the recovery share, and no offset against issued balances in any view (REQ-FIN-139…144).
- Movement tables per para. 98–105 (UNVERIFIED): LRC excluding LC, LC, LIC (PV and RA), revenue and expense analysis (REQ-FIN-146).
- EU endorsement: Commission Regulation (EU) 2021/2036.

**Statutory basis:**
- Law 4308/2014 Art. 1 §3 requires public-interest entities, including insurers, to report under IFRS.
- ΣΛΟΤ 1083/2016 exempts branches of foreign insurers.
- So the Greek entity's book profile is IFRS17 (statutory and group) + SOLVENCY_II, with LOCAL_GAAP inactive (BR-FIN-001).

**IPT (Law 5177/2025 Art. 43):**
- The tax object is premiums due ("απαιτητά ασφάλιστρα") and contract rights of every kind (§2).
- Rates: **20% fire, 4% life, 15% other** (§2).
- Computed on total premium without deduction and passed on to the policyholder (§4). Exemptions in §5.
- **Quarterly declaration to AADE:** Q1 by end of June, Q2 by end of September, Q3 by end of December, Q4 by end of March of the following year (§6). Form set by AADE Governor decision (§7).
- FIN reads these as pack data (BR-FIN-071, `tax.ipt.return_scheme`).
- IPT base includes contract rights and fees flagged in the base (REQ-FIN-181, REQ-PFC-117).
- The prompt's "annual transaction-level report by 31 March" was **not found** in Art. 43 (AC-5). It is kept as an optional pack return type (OI-FIN-04).

**IPT liability point:** pack key `tax.ipt.liability_point` (REQ-MKT-328), WRITTEN or DUE. Greece default is **DUE** per D2 until the tax opinion (REQ-FIN-178, BR-FIN-070).

**IPT on cancellation:** AADE ΠΟΛ 1028/2017 says IPT is not refunded on cancellation, giving Greece pack treatment NOT_REDUCE (BR-FIN-072).
- Distance withdrawal (Law 5317/2026 Art. 72, "the consumer pays nothing") is routed by its own cancellation source and decided by the programme opinion.
- Refund net of tax is excluded (D2).
- Tax kept payable but refunded to the customer is expensed as "tax borne by the insurer" (REQ-FIN-302, BR-FIN-085, GL-6160).

**Auxiliary Fund levy (Law 5113/2024 Art. 14, replacing P.D. 237/1986 Art. 20 §1; §3.1, AC-2, AC-3):**
- Contribution of **not more than 6% of gross written MTPL premium**, allocated **4.5%** (Fund liabilities Art. 19 §1 (a), (b), (d)) and **1.5%** (Art. 19 §1 (c), 19a, 19b; levied only on insurers established in Greece).
- The percentages can be changed by Bank of Greece decision within the 6% ceiling.
- Burden is **70% insurers / 30% insured**.
- The policyholder share is shown on the policy and is exempt from all taxes except stamp duty, which insurers remit under P.D. 160/1978.
- **Remittance within 15 days after each calendar two-month period**, for contracts concluded or renewed in that period, **irrespective of collection**. Default interest applies on late payment.
- FIN's reading (AC-3): the 70/30 split applies to the whole contribution (at 6%, insurer 4.2% and policyholder 1.8%). FIN holds **no rate values**; it accrues whatever `REQ-MKT-322` holds.
- Levy payable is posted on the written basis irrespective of collection (REQ-FIN-189, BR-FIN-080).
- Two-month period by base point, Greece default the conclusion or renewal date (REQ-FIN-191). Example: renewal concluded 28 Feb, effective 1 Mar, belongs to Jan–Feb.
- LevyReturn due 15 days after period end (REQ-FIN-192). The **only clock is BIL's `BIL_AUXF_REMIT`** (R-60/K-05). `FIN_LEVY_RETURN` is not bound in Greece.
- Default-interest accrual is proposed on late remittance (REQ-FIN-194).
- Stamp duty on the policyholder share (REQ-FIN-195, Should) is paid via `TAX_REMITTANCE`.
- The establishment-restricted component is handled by entity attribute (REQ-FIN-196, Should P4).

**myDATA (A.1138/2020 and successors; AADE / Hellenic Association informal group guide, 16 Jan 2023):**
- Commission self-billing uses document type **2.1** (category A1), credit notes **5.1/5.2**, expense classification **2.5**, VAT category **7** (Art. 22 VAT Code exemption), E3 code **585_009** (§3.1; REQ-FIN-176).

**Solvency II:**
- Directive 2009/138/EC Art. 75–86. Delegated Reg. (EU) 2015/35 Annex I LoB at coverage grain with % splits (REQ-FIN-150).
- **EIOPA taxonomy 2.10.0 applies from the Q1 2027 reference date; Q4/annual 2026 uses 2.8.x.** Parallel mappings are required (REQ-FIN-154).
- Statutory-to-SII reconciliation every quarter (REQ-FIN-153).

**Other obligations:**
- GDPR / Law 4624/2019: no P2/P3 in FIN; DSAR erasure is refused under book retention and a restriction is recorded instead (REQ-FIN-290).
- OBL-RES: the group pack and extracts to Fairfax (Canada) carry aggregated P0 data only (REQ-FIN-210).
- DORA: the ERP is registered as an ICT third party (REQ-FIN-271).
- AI Act: section 11 applies.
- EAA / Law 4994/2022: WCAG 2.2 AA (R-57).
- IRRD Directive (EU) 2025/1: FIN supplies reconciled balances (REQ-FIN-155). Greek transposition is "Verify".
- Fairfax: reports IFRS in USD; adopted IFRS 17 on 1 Jan 2023.

### 9.2 Referenced but NOT specified (gaps)

- IPT return form and content, and whether an annual detail report exists (OI-FIN-04).
- Auxiliary Fund levy return form and procedure (UNVERIFIED, §9.3).
- Levy split reading (whole vs 4.5% vs 1.5%), current rates, stamp-duty rate and base, levy base for mid-term changes and refunds. All go to the single programme opinion (D2). Legal sign-off is a go-live gate.
- IPT treatment of endorsement credits and of distance withdrawal: guarded pending the opinion.
- Accounting-records retention article and duration under Law 4308/2014 (OI-FIN-10).
- Bank of Greece financial reporting forms and timetable (OI-FIN-06).
- Premium-side myDATA document types (handed to CMP, D3).
- Commission withholding-tax rate (REQ-FIN-177).
- Greek ΕΛΠ chart view need (OI-FIN-11).
- IFRS 17 paragraph numbering outside 22, 24, 30 and 53–59 (OI-FIN-12).
- Fairfax timetable, chart, template, control framework and rate types (OI-FIN-09).
- Default-interest rate on late levy remittance ("at the pack's rate", REQ-FIN-194, not given).

### 9.3 Deferred to configuration or country pack

All rates, periods and deadlines are country-pack data (§3.1 preamble). The `tax.*` keys are final at L3 (§10.2):
- `tax.ipt.liability_point`
- `tax.ipt.return_scheme`
- `tax.levy.auxfund.components / .split / .base_point / .period_scheme / .remit_due_rule / .established_only_components`
- `tax.return.materiality` (€10,000)

Entity policy keys are listed in §10.2.

## 10. Greek-market specifics

- **AFM:** not mentioned in PRD-09. Party references are ids only.
- **myDATA:** reached only through CMP (`FiscalDocumentChannel`).
  - FIN reconciles `FiscalDocRegistered` (MARK, document type, classifications) to BIL invoices, credit notes and journals by count and amount per classification (REQ-FIN-197).
  - Expected classification per charge type comes from the PFC fiscal-category key (REQ-FIN-198).
  - Rejections stay open on the close cockpit (REQ-FIN-200).
  - Commission self-billing is reconciled per intermediary and month (REQ-FIN-176).
  - Claim-payment fiscal documents are reconciled where the pack defines them (REQ-FIN-199, Should; R-43).
  - D3 (OI-FIN-05 closed): CMP is the only issuer of fiscal series; motor uses the TRANSACTION trigger; premium receipts are MARKed before delivery.
- **gov.gr / Information Centre / bureau:** gov.gr and the Information Centre are not mentioned. The Greek motor insurers' bureau and the Auxiliary Fund appear only as claim recovery or payable counterparties posted from CLM events via `MotorCompensationBodyAdapter` (REQ-FIN-160, -170).
- **Friendly Settlement:** a Greece-pack clearing account GL-2515 nets per counterparty and clearing statement. Cash comes only via BIL `FS_CLEARING` (REQ-FIN-299, BR-FIN-097). The window is `fin.recon.fs_statement_window_days`, defaulting to statement period + 10 days.
- **AADE:** IPT quarterly returns. Payment via BIL `TAX_REMITTANCE`, with the authority as payee from the pack (REQ-FIN-301).
- **Bank of Greece:** can change the levy percentages; it is the source of the BoG returns (OI-FIN-06); ROLE-44 receives evidence packs via compliance.
- **Language:** all labels, account names, template names and return labels are in Greek and English (NFR-FIN-016).
  - Greek terms of record (‡) come from PRD-18 §6.4; † terms are working translations awaiting ROLE-46.
  - Manual journal descriptions may be in Greek or English, with the language recorded (REQ-FIN-216).
  - AI notice text is given in Greek ("Πρόταση από βοηθό ΤΝ — απαιτείται έλεγχος και έγκριση").
- **EUR:** functional currency (A-2). USD group currency. RI balances may be in USD or GBP. Non-euro functional currencies are P4 (REQ-FIN-214). Euro changeover import is optional P4 (REQ-FIN-296).
- **Calendar:** close calendar working days come from the PLT Greek holiday calendar (REQ-FIN-227).

## 11. Controls

**Authority types (§12):**
- `FIN.ManualJournal` (amount, currency, book, template)
- `FIN.ForceMatch` (difference)
- `FIN.BreakAcceptance` (amount)
- `FIN.PeriodReopen`
- `FIN.ReturnApproval` (tax type, amount)
- `FIN.ActuarialApproval` (result type)
- `FIN.Ifrs17AssignmentCorrection`
- `FIN.IntakeExceptionReject` (amount)

**Maker-checker list (§12; CCR-FIN-05 / R-51):**
- posting-rule versions (REQ-FIN-055)
- manual journals (REQ-FIN-217; amount routing, maker never checker)
- chart, mappings, dimension rules and book profiles
- IFRS 17 decision table and assignment corrections (two approvals: actuary and controller)
- actuarial set approval (submitter ≠ approver)
- period reopen
- break acceptance for lock
- forced matches above tolerance
- intake exception rejection
- manual extract acknowledgement
- tax and levy return approval
- manual FX rate correction (PLT)

**SoD rules (§12, data in PLT REQ-PLT-082):**

| Rule | Constraint |
|---|---|
| SoD-FIN-01 | Rule author ≠ approver |
| SoD-FIN-02 | Manual journal maker ≠ approver. A `fin.rules.edit` holder cannot approve manual journals on accounts touched by their unapproved drafts |
| SoD-FIN-03 | Return preparer ≠ approver |
| SoD-FIN-04 | Actuarial submitter ≠ approver |
| SoD-FIN-05 | BIL ledger-adjustment rights holders cannot accept BIL–FIN breaks |
| SoD-FIN-06 | Investigator ≠ acceptor |
| SoD-FIN-07 | BIL disbursement approvers cannot approve FIN manual journals on cash accounts |
| SoD-FIN-08 | The tax return approver cannot release the `TAX_REMITTANCE` disbursement in BIL |

**Audit:**
- The full FIN audit event list is in §12, via REQ-PLT-002, append-only.
- Daily hash-chain per entity, book and accounting day, sealed through PLT (REQ-FIN-071 is Should; NFR-FIN-017 says "seal daily").
- Database triggers block UPDATE/DELETE on journals and log a security event (REQ-FIN-070).
- Evidence packs are sealed via `REQ-PLT-130` (REQ-FIN-241).
- Manual journal listings for auditors (REQ-FIN-226).

**Manual journals:**
- Templates only: ACCRUAL, PREPAYMENT, RECLASSIFICATION, CORRECTION, ACTUARIAL_FALLBACK, IFRS17_TOPSIDE, TAX_ADJUSTMENT, FX_ADJUSTMENT, MIGRATION_ADJUSTMENT (REQ-FIN-215).
- Evidence is mandatory (REQ-FIN-216).
- System-only accounts (e.g. GL-2110) accept manual entries only via IFRS17_TOPSIDE with controller approval (REQ-FIN-224).
- Unapproved drafts expire after 7 days (BR-FIN-052).

**GDPR:**
- No P2/P3 in tables, events or logs; checked by a CI schema scan (NFR-FIN-012).
- Intermediary and payee names are masked unless the user has permission, and fetched from PTY only for display (REQ-FIN-291).
- DSAR: export references; refuse erasure under retention and record a restriction (REQ-FIN-290, `fin.Dsar.restrict`).
- AI inputs exclude P1–P3 (§11).

## 12. AI features (§11)

All features ship **off**, run through the PLT EU gateway with zero retention, are classified minimal risk, and require a human for every effect. "FIN works fully with this entire section disabled." The kill switch removes AI surfaces within 60 s, keeps drafts, and logs an `AiInteractionRecord` (REQ-FIN-298, Must P1).

| ID | Name | Phase (§1.5) | Monitoring profile | Needed for MVP? |
|---|---|---|---|---|
| AI-FIN-01 | Rule drafter (proposes rule rows for NO_RULE groups; 5 s timeout, confidence ≥ 0.6) | P1 (pilot, off) | MP-C | No |
| AI-FIN-02 | Break explainer and match proposals | P1 (pilot, off) | MP-C (reversal > 2%) | No |
| AI-FIN-03 | Journal anomaly detector (journal features only, never staff) | P2 | MP-E | No |
| AI-FIN-04 | Close and variance commentary drafter (zero numeric mismatches) | P3 | MP-C | No |
| AI-FIN-05 | Return pre-validator | P2 | MP-C | No |
| AI-FIN-06 | "Ask finance" conversational analytics (read-only, shows the query) | P3 | MP-F | No |
| AI-FIN-07 | Onerous early warning | P3 | MP-G | No |

Only the kill-switch and logging plumbing (REQ-FIN-298) is a P1 Must.

## 13. Open issues / assumptions / CCRs

**Open issues (§16.1):**

| ID | Topic | Status |
|---|---|---|
| OI-FIN-01 | Levy split, rates, procedure | Closed per D2 (into programme opinion) |
| OI-FIN-02 | Stamp duty on policyholder levy share | Closed per D2 |
| OI-FIN-03 | IPT liability point | Closed per D2 (Greece default DUE) |
| OI-FIN-04 | IPT return form (AADE Governor decision Art. 43 §7); annual detail report | **Open** |
| OI-FIN-05 | myDATA document types | Closed per D3 (remaining with CMP OI-CMP-03/-04) |
| OI-FIN-06 | Bank of Greece financial reporting forms and timetable | **Open** |
| OI-FIN-07 | Levy base for mid-term changes; refund on cancellation | Closed per D2 |
| OI-FIN-08 | IPT on non-cancellation return premiums | Closed per D2 |
| OI-FIN-09 | Fairfax timetable, chart, template, control framework, rate types | **Open** |
| OI-FIN-10 | Record retention period | **Open** |
| OI-FIN-11 | Greek ΕΛΠ chart view need | **Open** |
| OI-FIN-12 | Unverified IFRS 17 paragraph numbers | **Open** |
| OI-FIN-13 | ERP selection, extract format, acknowledgement | **Open** |
| OI-FIN-14 | Profitability-group pricing indicators at bind (not in `PolicyBound`) | **Open** |
| OI-FIN-15 | POL vs FIN IFRS 17 tags | Closed by R-52 |

**Assumptions (§16.2):**
- A-1: motor and home annual terms are PAA-eligible.
- A-2: EUR functional, USD group, RI in USD/GBP.
- A-3: BIL publishes `BillingEntryPosted` for every entry, with charge category, type, coverage and term refs.
- A-4: CLM events carry the group reference; otherwise FIN waits.
- A-5: DAT delivers actuarial results by WD+2 of quarter end.
- A-6: the ERP accepts daily summarised journals and acknowledges them.
- A-7: the levy is computed by `TaxCalculator` at rating and billed or accrued by BIL; FIN only reconciles it.

**Risks (§16.3):**
- RK-FIN-01 double posting or gaps.
- RK-FIN-02 earning run too slow.
- RK-FIN-03 Greek tax readings wrong.
- RK-FIN-04 IFRS 17 judgement poorly evidenced.
- RK-FIN-05 rule errors.
- RK-FIN-06 close timetable missed.
- RK-FIN-07 volume growth.
- RK-FIN-08 group requirements unknown.
- RK-FIN-09 motor RI may include a quota share although D6 assumes XoL only (confirm before G0).

**CCRs (§16.4), all Accepted:**

| CCR | Ruling | Change |
|---|---|---|
| CCR-FIN-01 | R-48 | Book profile; statutory basis per entity |
| CCR-FIN-02 | R-47 | 8 new FIN events |
| CCR-FIN-03 | R-49 | `TaxReturnFormat` extension |
| CCR-FIN-04 | R-50, amended by R-60/K-05 | Clocks `FIN_IPT_RETURN` / `FIN_LEVY_RETURN` |
| CCR-FIN-05 | R-51 | Maker-checker list additions |
| CCR-FIN-06 | R-52 | POL tags are proposals; FIN is the system of record |

**Decisions before build (§16.5):**
- Taken: 1 (book profile), 2 (levy, as a process), 3 (IPT point), 4 (single source), 5 (IFRS 17 design; but OI-FIN-14 is open).
- Specified: 9 (actuarial contract).
- **Open:**
  - 6: IFRS 17 policy elections — acquisition cash flows, LRC premium basis, LIC discounting/OCI, multi-currency, RI commission policy. Entity decision needed before W7 configuration.
  - 7: ERP.
  - 8: Fairfax manual.
  - 10: retention and Greek chart scope.

## 14. Conflicts and ambiguities found

### (a) With the infrastructure / technology stack

1. **Broker-style vocabulary.** These terms assume a log-based broker; the binding stack has a PostgreSQL outbox with in-process ordered handlers and no broker. Builders must map them to outbox cursors and handler checkpoints:
   - §8 "Topic `fin.events.v1`" with per-event "partition key";
   - REQ-FIN-046 metric "dead-lettered", example lag "on `bil.events.v1`";
   - NFR-FIN-009 "intake resumes from **consumer offsets**";
   - REQ-FIN-042 / `plt.Consumer.replay` "replay".
   - Ordering per aggregate `sequence` with gap detection (REQ-FIN-033) needs an explicit outbox design.
2. **"Workflow engine."** REQ-FIN-229 "through the workflow engine in dependency order", J-05 "dependent tasks on the workflow engine (`REQ-PLT-007`)", §9.2 `plt.Workflow.start` and REQ-FIN-293 "run all scheduled finance jobs as workflows". The stack forbids workflow servers and BPM engines and uses Hangfire with state in domain tables. Close-task dependencies must be built as a FIN state machine plus Hangfire jobs.
3. **Lakehouse.** §9.3 row "Actuarial models (lakehouse)". The stack forbids lakehouses; DAT marts are PostgreSQL schemas.
4. **In-house CEL-compatible expression language "run by the PLT rules runtime"** (REQ-FIN-051, D10). This is not forbidden, but it is a significant in-house component (parser, type checker, deterministic evaluator) that must exist in PLT before FIN rules work. No library is named; adding one (e.g. a CEL .NET port) would need a stated reason under engineering rule 11.
5. **`plt.DecisionTable.evaluate` (REQ-PLT-174)** for profitability groups is another PLT runtime dependency of the same kind.
6. **Append-only enforcement.** REQ-FIN-070 asks for triggers that reject UPDATE/DELETE "for every role including the schema owner". ARCHITECTURE-DECISIONS §2.1 instead says the application role has no UPDATE/DELETE rights. Both are compatible, but a schema owner can disable triggers, so "including the schema owner" is not strictly enforceable with triggers alone. Use both mechanisms and alert on `ALTER TABLE … DISABLE TRIGGER`.
7. **Deployment "stamps"** (NFR-FIN-005 "per stamp"; OBL-RES "EU-hosted stamps"). The concept is not defined in ARCHITECTURE-DECISIONS (single Azure Container Apps deployment). This is a capacity assumption to confirm.
8. **Throughput target.** NFR-FIN-005 requires ≥ 2,000 events/s sustained and 5,000/s burst for 15 min, each producing two book journals with deferred constraints, on one PostgreSQL Flexible Server. That is ambitious for a modular monolith and needs early load testing.
9. **Hangfire is never named;** "PLT scheduler" `plt.Job.run` is assumed to map to it. No conflict otherwise: the PRD does not mention Kafka, Camunda, microservices, specific ERP or vendor products. Aptitude and SAS are cited only as "reference only; no product adopted".

### (b) With the system contract or other PRDs

1. **Auxiliary Fund levy split (AC-3).** PRD-02 §3.1 reads 70/30 on the 4.5% component only. PRD-06 §3.2 and FIN read it over the whole 6% (4.2% / 1.8%). A third reading (1.5% only) is possible. Resolution is the D2 opinion, with values held only in REQ-MKT-322.
2. **Event keying.** The S2 proposal XMR-F-117 to key FIN events by journal, period, break or run id was **not applied**; the catalogue of record keeps `legal_entity_id`. Consumers that need per-claim order must use the source sequence in the payload.
3. **CR-S3-11/-12 source name** `CLM_FS_SETTLEMENT` was superseded by `FS_CLEARING` (D1). Any PRD still using the old name conflicts.
4. **PRD-15 DAT is referred to as a "lakehouse"** (§9.3). The DAT digest should check this.
5. **REQ-FIN-001 / REQ-FIN-086.** `BusinessEventSuspended` is a contract event (R-47) and is emitted by the state machine (§7.3.1), yet its requirement REQ-FIN-086 is only **Should**.
6. **IPT clock ownership.** CMP owns the clock register. The FIN return clock stops only when Filed **and** the remittance has cleared (REQ-FIN-180, XMR-F-216). CMP's definition of "met" must match.

### (c) Internal contradictions and inconsistencies

1. **LRC premium basis vs worked example.** The default `fin.ifrs17.lrc_premium_basis` is PREMIUMS_RECEIVED (§10.2). REQ-FIN-132's acceptance says that under that policy "the LRC excludes [a written unpaid premium]". But the §4.11 worked example and GF-02 post the written entry Dr GL-1210 receivable / Cr **GL-2110 LRC** 400.00 before cash. Presentation mapping can reconcile the two, but the ledger posting rule is ambiguous.
2. **G-2 vs waiting timeouts.** G-2 says no event may be "received but neither posted nor held as an intake exception after 15 minutes". BR-FIN-021 allows Waiting for up to 30 min (gaps) or **24 h** (group assignment), and REQ-FIN-308 allows 30 min for incomplete sets.
3. **REQ-FIN-002 (Must) relies on REQ-FIN-060 (Should)** as the only FIN-side correction path for journals sourced from other modules.
4. **RI-held links are P2, but P1 depends on them.**
   - REQ-FIN-140 (links issued ↔ RI-held) is Must **P2**.
   - REQ-FIN-142 (loss-recovery component "on the linked reinsurance-held group") is Must **P1**.
   - REQ-FIN-295 returns links only "from P2".
   - P1 is XoL-only (D6), so how the loss-recovery component is linked in P1 is unclear.
5. **RI statements and settlements: P1 or P3?** REQ-FIN-038 (Must P1) lists `StatementIssued` and `SettlementRecorded` as posting sources and RI_SETTLEMENT cash as P1. But REQ-FIN-166 (statements and settlements posting and reconciliation) is Must **P3**.
6. **`CashCallRaised`** is consumed as an RI posting source (§8.2) but appears in no requirement (REQ-FIN-038 omits it).
7. **Hash chain.** REQ-FIN-071 (daily hash chain) is **Should**, but NFR-FIN-017 requires "daily hash-chain seal" at 100% and invariant suite REQ-FIN-254 (Must) includes "journal hash chain".
8. **LOCAL_GAAP in CI.** REQ-FIN-090 (activate LOCAL_GAAP; Cyprus proof) is **Should**. Goal G-10 requires the Cyprus multi-book golden scenarios to pass at 100% on every build, and GF-04 needs LOCAL_GAAP DAC write-off. In effect it is mandatory for CI.
9. **Journal line volume.** §1.2 says "about 100 million journal lines a year"; §14 says "about 60–100 million".
10. **Earning schedule wording.** REQ-FIN-104 schedules "23:30 local each day". REQ-FIN-293 says the earning run is "scheduled at 23:30 on business days; W a holiday; T it still runs (calendar days)", which is self-contradictory wording.
11. **REQ-FIN-003 example.** "written €365.00 on 1 Jan … run for 10 Jan → earned €10.00 (or the POL-rounded value)" leaves inclusive or exclusive day-count to POL. Acceptable, but the test cannot be fixed without POL's convention.
12. **Tax-rule suspension.** REQ-FIN-182 suspends an entire BIL entry on TAX_RULE_VIOLATION. The premium-credit legs of that entry then also do not post, which guarantees a BIL↔FIN break until BIL corrects it. This is intentional but should be confirmed.

### (d) Cannot be built without a decision

1. IFRS 17 accounting-policy elections (§16.5 item 6): acquisition cash flows, LRC premium basis, LIC discounting and OCI, multi-currency group policy, RI commission policy (`fin.ifrs17.ri_commission_policy`). Defaults exist but the entity must decide.
2. Profitability-group inputs at bind (OI-FIN-14). Pricing indicators are not in `PolicyBound`, so the decision table (REQ-FIN-124) has no inputs beyond portfolio, product and channel. The default "all NSP" works for go-live only.
3. ERP selection, format and acknowledgement mechanism (OI-FIN-13). REQ-FIN-264 defaults to CSV/JSON, but acknowledgement (REQ-FIN-266) and the Lock gate (REQ-FIN-268) depend on it.
4. Fairfax group chart, calendar, template and rate types (OI-FIN-09), needed for REQ-FIN-203/-209/-210.
5. Greek tax values (D2 opinion) — a go-live gate. IPT form (OI-FIN-04) and levy return form are needed to implement the Greece `TaxReturnFormat`.
6. Retention durations (OI-FIN-10).
7. Scope of the year-end close and of the SII reconciliation inside a sub-ledger:
   - REQ-FIN-240 closes P&L to retained earnings per book.
   - REQ-FIN-153 asserts "IFRS17 total equity + valuation differences = SII excess of assets over liabilities".
   - The ERP owns investments, fixed assets and payroll (§1.4), so FIN's books do not hold total equity. What "total equity" means in FIN needs a decision.
8. **Translation "group-view layer"** (REQ-FIN-209). Translation differences post to GL-3210 "in a group-view layer", but books are only LOCAL_GAAP, IFRS17 and SOLVENCY_II. Whether the USD translation produces journals (in which book), or a derived view, is undefined.
9. **Where FIN's mandatory 3-currency lines come from for actuarial and earning postings.** The group rate type and date basis (REQ-MKT-204) is pending the Fairfax rate types.

## 15. Build notes

**Hardest parts:**
1. **Posting engine and rule language.**
   - Deterministic resolution with specificity (most qualifiers wins, ties are a compile error).
   - A compile step checking catalogue and charge-type coverage, account existence, dimension completeness and template balance.
   - A golden suite gating submission; impact preview in a sandbox; effective-dated rule-set versions.
   - Multi-book atomic posting (REQ-FIN-074).
   - Deferred balancing constraints in three currencies.
   - Idempotency, sequence-gap handling and complete-set handling (REQ-FIN-308) under 2,000 events/s.
2. **Earning.**
   - An incremental daily run over 1.2 M terms in ≤ 60 min, with POL called only for changed terms.
   - Recompute-from-inception catch-up for out-of-sequence changes.
   - Idempotent "post the difference" design.
   - One EarningResult feeding three books, with invariants proving earned + unearned = written and UPR = LRC premium component = SII base.
3. **IFRS 17.** Event-driven assignment with Waiting dependencies, immutable versions, the PAA LRC roll-forward, onerous and loss-component mechanics, RI-held groups including XoL allocation accrual from activation (REQ-FIN-303), and movement tables derived from journals.
4. **Close and reconciliation fabric.**
   - Six legs, plus earning-across-books and clearing legs.
   - The break lifecycle with WRK and PLT incidents.
   - Lock gates; reopen with impact list; evidence packs.
5. **Tax and levy returns.** Return population freeze, pack-driven treatment validation (`TaxCalculator.treatment`), the BIL `TAX_REMITTANCE` round-trip, and CMP clocks.

**Must exist first:**
- **PLT:** idempotent consumer and outbox with per-aggregate sequence; numbering (gap-free); FX rates with rate ids and group rate types; authority and maker-checker and SoD; audit and seal; time service; scheduler; decision tables; rules runtime (D10).
- **MKT:** config resolution; rounding; code lists; `TaxCalculator.treatment`; `TaxReturnFormat` binding for Greece and the Cyprus stub.
- **PFC:** GL keys, earning patterns, tax classes, SII LoB and IFRS 17 portfolio mapping.
- **BIL:** `BillingEntryPosted` contract including source types, set fields, and the daily totals push.
- **POL:** `pol.Earning.compute`, `pol.Segment.changes`, `pol.Charges.reconcile`, `PolicyBound` with portfolio proposal.
- **CLM:** financial events carrying the group reference.

**Suggested slicing (tracer bullets):**
1. Schema `fin`: JournalEntry/Line with deferred balance constraint, append-only triggers and grants, partitioning, gap-free numbering, BookProfile, GlAccount and reference chart, AccountDerivation.
2. Intake: BusinessEvent states, catalogue, idempotency, envelope validation, sequence wait, intake exception queue (API + SCR-FIN-03) with WRK activity.
3. Posting engine v1: rule model, specificity, expression evaluator (or a stub pending D10), compile, golden suite, RuleSetVersion lifecycle with maker-checker (SCR-FIN-01).
   - First end-to-end: GF-02 worked motor policy, `BillingEntryPosted` written entry → IFRS17 + SOLVENCY_II journals.
4. IFRS 17 assignment on `PolicyBound`/`RenewalBound` + query API + `Ifrs17GroupAssigned` (needed early by CLM and RI).
5. Earning run (daily, incremental, difference posting, invariants) + GF-01 PAA fixture + SCR-FIN-08.
6. Claims and clearing: CLM events, claim clearing account, FS clearing (GF-05, GF-10).
7. Reconciliation: `fin.Reconciliation.exchange`, BIL leg, POL three-way, CLM and RI legs, breaks, SCR-FIN-05.
8. Close: periods, cut-off, accounting-date derivation, tasks via Hangfire, gates, Lock/Reopen, `PeriodClosed`, `fin.Period.status`, SCR-FIN-04.
9. Manual journals with templates, evidence, approval and SoD (SCR-FIN-06); journal browser (SCR-FIN-02); trial balances and mappings (SCR-FIN-13).
10. Tax and levy: IPT at the liability point, treatment validation, TaxReturn/LevyReturn lifecycle, `TAX_REMITTANCE`, myDATA leg (GF-08, GF-11), SCR-FIN-09.
11. FX: three-currency conversion, revaluation, USD translation, group pack (GF-07), SCR-FIN-10.
12. GL extract with acknowledgement and replay (SCR-FIN-11). Actuarial intake (SCR-FIN-12). RI money events (GF-12).
13. Migration imports and `reverseBatch`; DSAR; retention; AI kill-switch plumbing.
14. Cyprus stub LOCAL_GAAP and DAC golden runs (required for G-10 even though REQ-FIN-090 is Should).

**Test obligations:**
- 14 golden fixtures (GF-01…14).
- 11 property invariants, also run nightly in production (REQ-FIN-254).
- Regulatory test cases (§14.x).
- An integrated six-way month-end test with seeded faults.
- A 1.5 M-term performance test.
- A time-shifted close rehearsal (REQ-FIN-294).
