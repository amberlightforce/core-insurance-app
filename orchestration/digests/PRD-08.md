# Digest — PRD-08 Ceded Reinsurance (RI)

Source: `core-insurance-prds/PRD-08-ceded-reinsurance.md` v1.1 (2026-10-07, "Baseline candidate", binding input 00-system-contract v1.11, PRD-18 decisions D1–D10 applied). 2,116 lines, read in full. Stack reference: `core-insurance-infra/ARCHITECTURE-DECISIONS.md`.

---

## 1. Identity

| Item | Value |
|---|---|
| Module code | **RI** |
| Title | Ceded Reinsurance |
| Schema | `ri` (§7.0) |
| Owner roles | Reinsurance product owner (ROLE-14 specialised); ROLE-27 reinsurance manager; ROLE-28 reinsurance accountant; lead architect (cession/recovery engines); co-reviewers ROLE-24 finance controller, ROLE-35 IFRS 17 actuary; ROLE-29 compliance (§1.1) |
| ID scheme | Anchors `REQ-RI-001…007`; module reqs from `REQ-RI-030`; `BR-RI-`, `NFR-RI-`, `SCR-RI-`, `AI-RI-`, `OI-RI-`, `CCR-RI-`, `RK-RI-`, `CAP-RI-`. Pack mapping: `REQ-` = `RI-FR-` etc. (§1.1) |

**Purpose (§1.2).** RI is the ceded-reinsurance system of record. It holds programmes → contracts (treaty and facultative) → sections → layers → participations (effective-dated versions, renewal, run-off); cedes premium per risk and per charge delta at policy-transaction time in the programme's inuring order; computes recoveries from proportional and non-proportional contracts on every claim financial movement; computes reinstatement, deposit and adjustment premiums and commissions; runs facultative placement; produces bordereaux and statements and settles balances (via BIL); monitors catastrophe accumulation for UW; supplies IFRS 17 reinsurance-held links and Solvency II data. Native multi-currency (four amounts per money row) from P1 because treaties are often USD.

**Five key decisions (§1.2):**
1. Target-minus-booked cession ledger driven only by POL facts — no separate correction process (`REQ-RI-002`, `REQ-RI-075…092`).
2. Recoveries = deterministic idempotent recomputation per contract year; batched per contract year and cat event (`REQ-RI-003`, `REQ-RI-113…145`).
3. Four amounts on every money row (original, contract, functional EUR, group USD) with PLT rate ids (`REQ-RI-204…211`).
4. Phasing by data model first: Motor = XoL only (assumption A-01/D6); proportional engine in P2.
5. RI calculates; FIN posts, BIL moves cash (only cash executor, D1), PTY owns parties/ratings/sanctions.

**Non-goals / out of scope (§1.4):** policy transactions/segments/charge deltas (POL); charge-type & coverage RI attributes (PFC); claim financials, cat coding, large-loss flags (CLM); postings, books, FX revaluation, IFRS 17 groups & measurement, GL (FIN); reinsurer/broker party records, ratings, sanctions (PTY, CD-12); payment execution, bank files, receipts (BIL, CD-13); UW accumulation decisions at bind (UW); cat analytics, modelled losses, regulatory marts, taxonomy transforms (DAT, CD-08); rendering/archive/delivery (DOC); activities/queues/inbound docs (WRK); authority, maker-checker, audit, workflow, numbering, FX, time, AI control plane (PLT); broker/reinsurer portal (CHN, later, OI-RI-09); **assumed (inward) reinsurance and retrocession** (not in scope; `direction` fixed to CEDED); reinsurance pricing/programme optimisation (DAT). IBNR recoverables are DAT/FIN estimates, never RI items (`REQ-RI-239`, A-07).

---

## 2. Size metrics

Counts recomputed by me from the §5 tables; they match the PRD self-check (§16.6).

| Metric | Count |
|---|---|
| Functional requirements | **240** (`REQ-RI-001…007`, `REQ-RI-030…262`) |
| By tag | [BASELINE] 223, [ENHANCEMENT] 17 |
| **First MoSCoW column = P1 Motor MVP cut** | **Must 93**, Should 23, Could 1, Won't 123 |
| Target-phase MoSCoW (informative) | Must 179, Should 56, Could 5 |
| By Phase column | P1 117, P2 65, P3 58, P4 0 (only `ri.Import.convertCurrency` mentioned as P4 Could inside `REQ-RI-007`) |
| Phase × target Must | P1 Must 93; P2 Must 53; P3 Must 33 |
| Business rules | 42 (`BR-RI-001…042`) |
| NFRs | 17 (`NFR-RI-001…017`) |
| Screens | 20 (`SCR-RI-01…20`); 4 owned UI-library baseline items UIL-R1…R4, all specified |
| Owned entities | ~37 entity rows in §7.1 (some grouped, e.g. DepositSchedule/Instalment/PremiumAdjustment) |
| Events produced | 26 (9 contract-catalogue + 17 accepted via R-47) |
| Events consumed | 57 event names in 23 rows (§8.2) |
| API operation groups | 36 rows in §9.1 (each with several verbs) |
| Outbound integrations | 13 rows §9.2 |
| AI features | 6 (`AI-RI-01…06`), all default off |
| Authority types | 11 |
| Open issues | 14 (OI-RI-14 closed) |
| CCRs | 7 (all Accepted) |
| Risks | 10 (`RK-RI-01…10`) |
| Golden tests | 12 (`GT-01…12`) + 8 property invariants |

**P1 composition (93 Must):** contract anchors 001, 003, 004, 007; programme/contract model (030–034, 037–040, 042); participations/security (046–051, 055); lifecycle (056–059, 065); risk key and pinned artefact (066, 073); `REQ-RI-075` (consume `ChargeDeltaEmitted`, filter cedable, count ignored); claim intake/UNL (113–116, 119, 120, 122); XoL recoveries (123–132, 136); exhaustion (150, 152); large-loss notices (168, 169); items/periods/balances (173, 174, 183); settlement execution via BIL (191, 192, 195, 262); SII counterparty (201); multi-currency (204–206, 208–210); IFRS17/SII data (224, 226–229, 231); FIN integration and close (233–237, 239, 240); DORA endpoint registration (245); migration/ops/privacy/AI governance (247–259, 261).

**Build-size estimate: L.** 240 requirements, 93 Must in P1 alone (M-sized by itself) plus 86 more Must across P2/P3; heavy money and temporal logic (bitemporal contract versions, target-minus-booked ledger, order-dependent annual aggregates, four-currency amounts, cat hours-clause optimisation, reinstatement maths). P1 slice alone is **M-to-L** (≈93 Must, ~10 screens partially needed, deterministic recovery engine with surge batching).

---

## 3. Owned entities (§7.1) and state machines (§7.3)

Common: every row carries `legal_entity_id`, `jurisdiction`, `created_at`, `created_by`, `record_version`. Contract versions bitemporal (valid × record time). Cessions, recoveries, items, settlements append-only; ledgers/trackers/balances are rebuildable projections checked by invariants (`REQ-RI-092`, `NFR-RI-008`). Money columns: `orig_amount/orig_ccy`, `contract_amount/contract_ccy`, `functional_amount`, `group_amount`, `rate_id_*`.

| Entity | Key attributes | Constraints / states | Retention |
|---|---|---|---|
| RIProgramme | code, name_gr/en, legal entity, portfolio scope (jsonb), period (tstzrange), currency, inuring_order (array with level), status, approval_request_id, evidence_ref | unique (entity, code, period); Draft→Submitted→Approved→Active→Expired→Closed | RC-RI-CONTRACT |
| RIContract | contract_number, stable_treaty_id, contract_year, type (QUOTA_SHARE, SURPLUS, FAC_PROPORTIONAL, FAC_XOL, AUTO_FAC, XOL_PER_RISK, XOL_PER_EVENT, CAT_XOL, AGGREGATE_XOL, STOP_LOSS), direction CEDED, programme, period+zone, attachment_basis, expiry_treatment, contract_ccy, settlement_ccys[], territory[], placed_pct, status | number unique per entity; (stable_treaty_id, contract_year) unique | RC-RI-CONTRACT |
| RIContractVersion | version_no, valid_from/to, known_from/to, reason, diff, content_hash, approval_request_id | immutable once Approved; non-overlapping valid time | RC-RI-CONTRACT |
| RISection | scope (lines, products, coverages, risk classes, perils, jurisdictions), typed exclusions, proportional terms (cession_pct, retention, lines, line_table_id, capacity_basis, SI/PML basis, max_cession, event_limit, cash_call_threshold, reserve deposit rates) | code unique per version | RC-RI-CONTRACT |
| RILayer | layer_no, attachment, limit, unlimited flag, deductible_basis (PER_RISK, PER_EVENT, AGGREGATE), aad, aal, reinstatement_count, reinstatement_rates[], reinstatement_basis, premium_basis, rate, mdp, min_premium, instalment_plan | layer_no unique per section | RC-RI-CONTRACT |
| ClauseSet | UNL (ALAE, interest, recoveries inuring), hours per peril (hours, multi-window, gap, first-loss rule), occurrence definition, ROE clause, cash call, notification, interest on deposits, commutation, indexation; free_text_clauses[] | one per version (section override) | RC-RI-CONTRACT |
| CommissionTerms | type FIXED/SLIDING/NONE, rate, scale, interpolation, carry_forward_years, profit commission, brokerage handling | — | RC-RI-CONTRACT |
| LineTable / Row | risk_class, si_band, construction, hazard_zone, retention, lines, valid_from | rows non-overlapping | RC-RI-CONTRACT |
| RIParticipation | reinsurer_party_id, broker_party_id, written_line, signed_line, lead, brokerage_pct, reinsurer_ref, rating_snapshot, valid period, screening_case_ref, status | Σ signed per layer = placed_pct | RC-RI-CONTRACT |
| SecurityList / SecurityRule | min rating per agency, max share per reinsurer/group, domiciles, period, approval | maker-checker | RC-RI-CONTRACT |
| RiskShareTimeline (RiskInterval) | policy_term_id, risk_key, valid_period, source segment ids + known_at, basis amounts, geocode/hazard keys, risk_class, ccy, applicable contracts, share waterfall (jsonb), overrides | intervals of a risk key never overlap for a known_at | RC-RI-CALC |
| CessionLedgerCell | (term, element, charge_type, interval) cumulative_charge; per section×participant booked_ceded, booked_commission, residual | unique key | RC-RI-CALC |
| Cession (delta) | cell, source charge_id(s)+delta kind, contract/section/participant, four amounts, commission, share used, status, journal_ref, `source_correlation_key`, set_id/index, calc_hash | immutable; idempotent on (charge_id, delta kind, section, participant) | RC-RI-ACCOUNT |
| CessionException | type, risk, interval, amount, cause, status, resolution, activity_id, sla_due | Open→InReview→Resolved/AcceptedNet/Cancelled | RC-RI-CALC |
| CessionOverride | MANUAL_SHARE / ACCEPT_NET / CONTRACT_SELECTION, value, reason, authority_check_id, expiry | — | RC-RI-CALC |
| FacRequest / FacQuote | number, source, policy/job, risk, basis, perils, period, requested share, deadline, status, quotes | see FSM | RC-RI-CONTRACT |
| FacCertificate | number, request, contract (FAC), term, risk, intervals, share/amount, premium terms, status | Bound, CertificateIssued, CoverEnded, Cancelled | RC-RI-CONTRACT |
| ClaimView | read model of CLM events: loss_at, geocode, line, coverages, cat_code, large_loss, per reserve line paid/open/recoveries in three currencies, last_sequence | — | RC-RI-CALC |
| Occurrence | type RISK/ACCIDENT/CAT_WINDOW/AGGREGATE, date, claim ids, linking reason, cat code, window | — | RC-RI-CALC |
| CatOccurrenceDefinition | cat code, contract, peril, proposed/confirmed windows, status, decided_by | Accumulating→Proposed→Confirmed(⇄Revised)→Locked | RC-RI-CALC |
| RIRecovery (delta) | occurrence, contract/section/layer/participant, kind PROPORTIONAL/LAYER, incurred/paid/outstanding deltas (four amounts), status, batch, trace | immutable | RC-RI-ACCOUNT |
| CalculationBatch / Trace | contract_year, triggers, engine_hash, config_hash, trace | — | RC-RI-CALC |
| AggregateTracker | cumulative layer loss, aad_consumed, aal_used, reinstatements_used, remaining_limit, status | one per layer-year; Open→Exhausted→(Open)→Closed | RC-RI-CALC |
| LayerReinstatement | reinstatement_no, amount, rate, time_factor, base_premium, premium | recovery FSM | RC-RI-ACCOUNT |
| DepositSchedule / Instalment / PremiumAdjustment | instalments Scheduled→Due→Statemented→Settled; GNPI snapshots; adjustment Pending→Calculated→Approved→Statemented | — | RC-RI-ACCOUNT |
| CommissionAdjustment | SLIDING/PROFIT, inputs, rate, delta, status | — | RC-RI-ACCOUNT |
| TechnicalAccountItem | type (18 values, `REQ-RI-173`), contract…broker, accounting period, original period, uw_year, accident_year, direction, four amounts, posting_key, status Open→Statemented→Settled, journal_ref | immutable | RC-RI-ACCOUNT |
| Bordereau | type, contract, counterparty, period, format, version, totals, reconciliation, status Draft→Generated→UnderReview→Approved→Issued→Superseded | P0/P1 (named rows by clause) | RC-RI-ACCOUNT; rows RC-RI-EXPORT |
| StatementOfAccount | number, version, contract/section, counterparty, period, ccy, settlement ccy, items, balance, direction, status, response, disputes | see FSM | RC-RI-ACCOUNT |
| CashCall | occurrence, amounts per participant, due date | Raised→Issued→PartiallyPaid→Paid; Issued→Overdue→Escalated→Paid/WrittenOff; →Cancelled | RC-RI-ACCOUNT |
| Settlement | counterparty, items, net amount, direction, ccy, value date, payee_account_id (BIL), screening, approval, bil_ref, failure reason | Proposed→PendingApproval(SanctionsHold)→Approved→Released→Completed / Failed / Cancelled | RC-RI-ACCOUNT |
| Collateral | type (LoC, CASH_DEPOSIT, FUNDS_WITHHELD, TRUST, PARENT_GUARANTEE), issuer, amount, dates, evergreen | Requested→Active⇄Expiring/Drawn→Released/Expired | RC-RI-CONTRACT |
| LargeLossNotice | allow-listed content, recipients, sent/ack | no P3 by design | RC-RI-ACCOUNT |
| ZoneAggregate | entity, peril, scheme+version, zone, line, class, gross/net TSI & PML, count, as_of, source cursor | unique key | RC-RI-CALC |
| ZoneCapacity | amount, derivation, period, approval | maker-checker | RC-RI-CONTRACT |
| UnderlyingLink | cession/recovery ↔ term, claim, underlying IFRS17 group, RI contract, RI-held group, recovery_pct | — | RC-RI-ACCOUNT |
| ExchangeMessage | TECH_ACCOUNT, CLAIM_MOVEMENT, SETTLEMENT, QUOTE, SLIP; direction, version, ack | — | RC-RI-EXPORT |

**State machines (§7.3):**
- **RIContract:** Draft→Submitted (valid per 037, wording referenced, participants not Blocked) → Draft (returned) | Approved (maker-checker on content hash; `RIContractVersioned`) → Active (period start via time service; `RIContractActivated`) → Active (endorsement; `RIContractVersioned`) → Expired (`RIContractExpired`) → RunOff → Commuted (`RIContractClosed`) → Closed (all settled, no open claims, approval; `RIContractClosed`).
- **Cession (canonical, contract §3.2.4):** Calculated→Posted (`JournalPosted`); Calculated→Exception→Calculated; Posted→Reversed only when RI issued a linked correcting delta AND FIN posted its reversal (PRD-18 §9.2.7, XMR-F-201). A FIN reversal without an RI correcting item → reconciliation break, cession stays Posted (`REQ-RI-091`).
- **Recovery / LayerReinstatement:** Calculated→Posted→Statemented→Settled; Posted→Reversed (same rule) (`REQ-RI-131`).
- **Fac:** Requested→Quoting→Quoted→Bound→CertificateIssued; Declined/Expired/Cancelled; CertificateIssued→CoverEnded/Cancelled (`REQ-RI-102`).
- **CatOccurrenceDefinition** (§4.5, §7.3.5), **AggregateTracker**, **Bordereau/Statement** (Draft→UnderReview→Approved→Issued→Agreed/Disputed→Superseded/PartiallySettled→Settled), **CashCall**, **Settlement**, **Collateral** as above.

---

## 4. Consumed entities / dependencies

| From | What | How | Req |
|---|---|---|---|
| POL | Charge deltas (charge id, element, charge type, period, amount, set_id/set_size/index) | `ChargeDeltaEmitted`, `TransactionReversed`/`TransactionReapplied` | REQ-POL-005/006/112/121 |
| POL | Segments, risk attributes, geocodes/hazard keys, sums insured | `pol.Segment.changes`, `pol.Policy.get`, `pol.Term.timeline` (segment feed) | REQ-POL-094/002/085/304 |
| POL | Loss-date snapshot | `pol.Snapshot.get` | REQ-POL-007 |
| POL | Cedable totals reconciliation | `pol.Charges.reconcile` | REQ-POL-122 |
| PFC | RI-cedable/commissionable flags, RI risk class, perils, SII LoB, pinned artefact | `pfc.ChargeType.list`, `pfc.Catalogue.get`, `pfc.RegulatoryMapping.get`, `pfc.Artifact.get` | REQ-PFC-004/122/093/094/005/002/003 |
| CLM | Claim financial events, claim attributes (loss_at), cat events, event aggregates, large-loss flag | events + `clm.Claim.get`, `clm.Financials.get`, `clm.CatEvent.aggregate` | REQ-CLM-005/006/061/090/101/210/215/226/227 |
| PTY | Reinsurer/broker parties, roles, LEI, rating history; sanctions screening; merges | `pty.Party.get`, `pty.PartyRole.query`, `pty.Screening.screen`, events | REQ-PTY-001/002/093/097/006/007 |
| BIL | Disbursement (RI_SETTLEMENT), receivable registration, payee accounts; status events | `bil.Disbursement.request/get`, `bil.Receivable.register`, `bil.PayeeAccount.get` | REQ-BIL-009/197/343/345/348–351 |
| FIN | Journal postings, breaks, period close, IFRS 17 groups, RI-held group assignment, revaluation | events + `fin.Ifrs17Group.riHeldAssignment`, journal query, close checklist | REQ-FIN-001/002/006/007/011/139/295 |
| UW | Accumulation band key (single source) | `mkt.Configuration.resolve` | REQ-UW-279 |
| UW | Over-capacity referral | notification | REQ-UW-282 |
| DOC | Render, archive, deliver, Documents tab, DT-RI-* types | `doc.Document.request` etc. | REQ-DOC-001/004/005/008/030 |
| WRK | Activities, queues, inbound docs, search projection | `wrk.Activity.create` etc. | REQ-WRK-001/003/005/007/325 |
| PLT | Authority, maker-checker, audit, numbering (`RI_CONTRACT`, `RI_FAC`), time service, FX `plt.Fx.getRate`, workflow, adapter host, AI gateway, retention, outbox | various | REQ-PLT-002/003/004/005/006/007/010/011/103/117/121/200/201/202/209/285/332 |
| MKT | Configuration, rounding, code lists, Geocoder, TaxCalculator | `mkt.Configuration.resolve`, `mkt.Rounding.apply` | REQ-MKT-001/002/007/195/315/087 |
| RAT | Day-count service | — | REQ-RAT-004 |
| DAT | Model registry, accumulation snapshots, modelled PML | `AccumulationSnapshotPublished` | REQ-DAT-005/008 |
| MIG | Calls RI import APIs | inbound | REQ-MIG-001/002/005/136–140 |

---

## 5. Events

Topic named `ri.events.v1` (§8); partition keys: `policy_id` (cession events), `ri_contract_id` (recovery, reinstatement, deposit, adjustment, commission, cash-call, contract events), `statement_id`, `counterparty_party_id` (settlement), `zone_key` (accumulation), `reinsurer_party_id` (security alerts). Outbox via PLT library (`REQ-PLT-147`, `REQ-PLT-005`, `REQ-PLT-137`); gap-free sequence per partition; no personal data.

### 5.1 Produced (§8.1)

| Event | Status | Trigger | Key payload | Consumers |
|---|---|---|---|---|
| `RIContractActivated` | Contract | approved version reaches period start (058) | contract, stable treaty id, year, version, type, period, ccy, sections/layers, participants | FIN, UW, DAT |
| `CessionCalculated` | Contract | cession deltas booked (002) | cession ids, charge ids, term, risk, interval, contract/section/participant, ceded + commission (four amounts), share, posting key, SII LoB, IFRS17 refs, `source_correlation_key`, set id | FIN, DAT |
| `CessionExceptionRaised` | Contract | 094, 260 | exception id, type, term, risk, interval, amount | WRK, FIN (context), DAT |
| `RecoveryCalculated` | Contract | recovery batch (003, 118) | batch, recovery ids, occurrence, claims, contract/layer/participant, incurred/paid/outstanding deltas (four), claim-level totals, posting key, SII LoB, IFRS17 refs | FIN, CLM, DAT |
| `ReinstatementPremiumDue` | Contract | 146 | reinstatement, layer, occurrence, no, amount, rate, time factor, premium | FIN, DAT |
| `BordereauGenerated` | Contract | bordereau approved (180) | totals per participant/ccy, doc id | FIN (context), DAT |
| `StatementIssued` | Contract | 178 | statement+version, counterparty, period, balance, direction, items | FIN, DAT |
| `CashCallRaised` | Contract | 187 | occurrence, amounts per participant, due date | FIN, DAT |
| `SettlementRecorded` | Contract | 195 (Completed/partial/manual only) | items settled, ccy, FX difference, BIL ref | FIN, DAT |
| `RIProgrammeApproved` | R-47 | programme approved | programme, contracts, period | DAT |
| `RIContractVersioned` | R-47 | version approved | diff summary | UW (capacity cache), DAT |
| `RIContractExpired` / `RIContractClosed` | R-47 | period end / closure/commutation | — | DAT |
| `CessionExceptionResolved` | R-47 | 100 | resolution, override ref | DAT |
| `FacPlacementBound` | R-47 | 107 | certificate, term, risk, share | UW, DAT |
| `LayerExhausted` | R-47 | 150 | layer, year, occurrence | DAT |
| `CatOccurrenceConfirmed` | R-47 | 141 | windows, decided by | DAT |
| `DepositPremiumDue` | R-47 | 154 | instalment, four amounts, posting key, `source_correlation_key` | FIN (handler added by PRD-09, XMR-F-132), DAT |
| `PremiumAdjustmentCalculated` / `CommissionAdjusted` | R-47 | 156, 161 | inputs, delta (four), posting key | FIN (PRD-09 handler), DAT |
| `LargeLossNotified` | R-47 | 168 | occurrence, recipients, doc | DAT |
| `StatementAgreed` / `StatementDisputed` | R-47 | response | disputed items | DAT |
| `CollateralChanged` | R-47 | 200 | change, amount | DAT |
| `AccumulationThresholdBreached` | R-47 | 216 | zone, utilisation, band | UW (alert, re-evaluates PRE_BIND jobs), DAT |
| `ReinsurerSecurityAlertRaised` | R-47 | 051 | party, rating, contracts | DAT |

Note: §8.1 says payloads carry "three currencies as the contract requires (original/transaction, functional, group) plus contract currency" — i.e. the four RI amounts.

### 5.2 Consumed (§8.2)

| Event(s) | Producer | Reaction | Idempotency |
|---|---|---|---|
| `ChargeDeltaEmitted` | POL | cession ledger update; complete sets only (075, 083, 260) | charge id + delta kind; set_id/size/index |
| `PolicyBound`, `PolicyChanged`, `RenewalBound`, `PolicyCancelled`, `PolicyVoided`, `PolicyReinstated`, `PolicyRewritten` | POL | refresh intervals, accumulation, fac cover (067, 112, 212) | event_id |
| `TransactionReversed`, `TransactionReapplied` | POL | group aggregate for one recomputation (084, 260) | set fields |
| `RenewalCreated` | POL | propose renewal fac (111) | event_id |
| `ClaimReported`, `ExposureCreated`, `ReserveChanged`, `TransactionSetApproved`, `PaymentIssued`, `PaymentVoided`, `RecoveryRecorded`, `ClaimClosed`, `ClaimReopened`, `CatEventAssigned` | CLM | ClaimView, occurrences, recovery batches (113–128) | event_id; per-claim sequence (out-of-order held, 113) |
| `CatEventDeclared`, `CatEventChanged` | CLM | event definitions, surge mode (138, 133) | event_id |
| `ClaimUpdated` | CLM | refresh loss time/location/large-loss (114) | event_id |
| `PartyUpdated`, `PartiesMerged`, `PartyUnmerged`, `PartyRoleChanged` | PTY | ratings, re-pointing (051, 250) | event_id |
| `SanctionsHitRaised`, `SanctionsHitCleared` | PTY | hold/release (049, 191) | event_id |
| `DisbursementIssued`, `DisbursementCleared`, `DisbursementVoided`, `DisbursementReturned` | BIL | settlement status (192) | disbursement id |
| `DisbursementRejected`, `DisbursementStopped` | BIL | Failed / Cancelled, items reopened, activity (262) | disbursement id |
| `PaymentReceived`, `CashAllocated` | BIL | receipts vs RI receivables (192) | event_id |
| `JournalPosted`, `ReconciliationBreakRaised`, `PeriodClosed` | FIN | Posted status, breaks, period alignment (235, 174) | event_id |
| `ReferenceDataPublished` (FX) | PLT | fallback replacement, corrections (210, 211) | event_id |
| `ApprovalDecided` | PLT | execute approved action bound to hash (057) | event_id |
| `AiToggleChanged`, `AiKillSwitchActivated` | PLT | disable AI within 60 s (257) | event_id |
| `LegalHoldApplied`, `LegalHoldReleased` | PLT | block purge (252) | event_id |
| `ConfigChanged` | PLT | refresh config ≤10 s (258) | event_id |
| `PackActivated`, `RegimeCodeListPublished` | MKT | refresh pack rules / code lists | event_id |
| `ActivityCompleted` | WRK | close notices/chases | event_id |
| `DocumentRendered`, `DocumentDelivered`, `DeliveryFailed` | DOC | delivery status | event_id |
| `ModelDriftDetected`, `BiasThresholdBreached` | DAT | flag/disable AI | event_id |
| `AccumulationSnapshotPublished` | DAT | refresh modelled PML / capacity inputs (215, 222, 220) | event_id |

---

## 6. APIs

### 6.1 Exposed (§9.1; REST under `/api/ri/v1/`, all commands `Idempotency-Key`, RFC 9457 errors `RI-ERR-*`, DR = dry-run)

| Operation group | Purpose | Req |
|---|---|---|
| `ri.Programme.create/update/renew/submit/approve/return` (DR submit, renew); `get/list` | programme lifecycle / registry | 030, 057, 060, 001 |
| `ri.Contract.create/update/endorse/submit/approve/expire/commute/close` (DR); `get/list/versionAt/applicable`; `simulate` | contract lifecycle, registry, simulation | 031–065, 001, 045 |
| `ri.Participation.add/replace/list` | participations (errors SIGNED-LINES, PARTY-BLOCKED) | 046–055 |
| `ri.Security.rules.set/get/report` | security list | 050, 053 |
| `ri.LineTable.set/get` | line tables | 036 |
| `ri.Cession.preview`, `listByPolicy/get`, `rerun` | waterfall preview, cession review, controlled re-cession | 074, 090, 089 |
| `ri.CessionException.list/assign/resolve/rerun` | exceptions | 093–100 |
| `ri.Fac.request/generateSlip/recordQuote/importQuotes/bind/issueCertificate/decline/cancel/get/list` | facultative | 101–112 |
| `ri.Recovery.listByClaim/listByContract/trace`, `recalculate`, `adjust`; `ri.Occurrence.link/unlink` | recoveries | 136, 128, 135, 115 |
| `ri.CatOccurrence.propose/confirm/revise/estimate` | hours-clause windows | 140, 141, 219 |
| `ri.AggregateTracker.get` | layer schedule | 152 |
| `ri.DepositSchedule.get/reschedule`; `ri.Adjustment.calculate/approve` | XoL premium | 153–159 |
| `ri.Commission.adjust/approve/history` | commissions | 160–166 |
| `ri.Notice.draft/send/acknowledge` | notifications | 168–172 |
| `ri.Item.list`, `ri.Balance.report` | items, MVP balances | 173, 183 |
| `ri.Bordereau.generate/submit/approve/issue` | bordereaux | 006, 175 |
| `ri.Statement.generate/submit/approve/issue/recordResponse/reissue` | statements | 006, 176–179 |
| `ri.CashCall.raise/issue/recordReceipt/cancel` | cash calls | 187, 188 |
| `ri.Settlement.propose/approve/release/recordManual` | settlements | 189–196 |
| `ri.Collateral.create/draw/release/list` | collateral | 197–203 |
| `ri.Accumulation.zone/query/footprint/estimate`; `setCapacity/exportOed/importModelResults` | accumulation API (UW bind path) | 005, 212–222 |
| `ri.Ifrs17Link.query`; `ri.DataProduct.get` (recoverables, counterparty, programme, catastrophe, USD) | reporting data | 224–232 |
| `ri.Close.run/status`; `ri.Reconciliation.fin` | close | 236, 237 |
| `ri.Exchange.send/receive` | ACORD/spreadsheet exchange | 241–246 |
| `ri.Import.programme/contract/cessionHistory/recoveryHistory/aggregateTracker/openItems`; `ri.Import.reverse`; (`ri.Import.convertCurrency` P4 Could) | migration | 007, 247–249, 261 |
| `ri.Dsar.export/restrict` | GDPR | 253 |

Error codes named: RI-ERR-VALIDATION, -SHARES-OVER-100, -NOT-FOUND, -REINSURER-ID, -CONTRACT-CLOSED, -SIGNED-LINES, -PARTY-BLOCKED, -OVERLAP, -APPROVAL-REQUIRED, -AUTHORITY, -SECURITY, -DEADLINE, -RECON-BREAK, -EXCEPTIONS-OPEN, -SANCTIONS-HOLD, -SCHEME-UNKNOWN, -FORMAT, -IMPORT-VALIDATION, -REVERSAL-BLOCKED.

Consumers of RI APIs: UW (`ri.Accumulation.zone` at PRE_BIND, `ri.Cession.preview`), CLM (`ri.Recovery.listByClaim`), POL (`ri.Cession.listByPolicy` embedded in policy file, REQ-POL-326), FIN/DAT/CMP/MIG (registry, data products, import), CMP (DSAR fan-out).

### 6.2 Consumed — see §4 table (POL, PFC, CLM, PTY, BIL, FIN, DOC, WRK, PLT, MKT, DAT, RAT). External systems are reached only via PLT adapter host (`REQ-PLT-153`): brokers/reinsurers (ACORD-aligned Ruschlikon messages or CSV/XLSX secure file exchange), cat-model platforms (OED files, CSV event loss tables), rating agencies via PTY, banks via BIL (§9.3).

---

## 7. SPIs / country-pack interfaces (§10.4)

- `TaxCalculator.calculateReinsurancePremiumTax` (R-49, `REQ-MKT-315`; renamed from CCR-RI-06's `reinsurancePremiumTax` per R-87) and `TaxCalculator.treatment` (D2). Guarded by config `ri.tax.reinsurance_premium_tax_enabled` (default false, country layer). Greece pack: none; Cyprus stub returns NotApplicable unless configured.
- `RegimeCodeList` (MKT `REQ-MKT-007`), `Geocoder` (hazard schemes with scheme+version, R-26, `REQ-MKT-002`), `NumberingScheme`, `DocumentLanguageRule`, `SanctionsListSource` (via PTY), `FxRateSource` only indirectly through PLT (`plt.Fx.getRate`; RI must never call it directly, CD-17, XMR-F-306).
- "No new SPI is required"; country variation limited to tax, collateral rules (`ri.collateral.rules`, BR-RI-021) and code lists.

---

## 8. Screens (§6)

All in PLT staff shell, command palette, Greek/English switch R-101 (language switch never changes document language — slips/statements follow `DocumentLanguageRule`). Semantic states only (contract §3.9.9). 17 permissions: `ri.read`, `ri.contract.edit`, `ri.contract.approve`, `ri.security.manage`, `ri.cession.review`, `ri.exception.resolve`, `ri.fac`, `ri.recovery.read`, `ri.event.define`, `ri.account`, `ri.statement.approve`, `ri.settlement`, `ri.collateral`, `ri.accumulation`, `ri.accumulation.capacity`, `ri.export`, `ri.ops`; ABAC on legal entity and line.

| ID | Name | Persona | One line | Phase relevance (derived from backing reqs) |
|---|---|---|---|---|
| SCR-RI-01 | Reinsurance home (work left) | 27, 28 | `IB-11` cards (exceptions, fac, events, statements, cash calls, settlements, collateral, security alerts, exhaustion), hero metrics `IB-17`, close progress `IB-12` | P1 (partial cards) |
| SCR-RI-02 | Programme designer with layer diagram | 27; read 24, 48 | split pane `IB-05`, inuring drag-reorder, layer diagram (040), graph `IB-15`, diff panel | P1 |
| SCR-RI-03 | Contract editor | 27, 28 | tabs Terms, Sections & layers, Clauses, Premium & commission, Participations, Documents, Versions; AI-RI-01 approve-the-diff `IB-07` | P1 |
| SCR-RI-04 | Participation panel and security | 27; 48 read | signed-line 100% bar, rating age, sanctions, concentration | P1 |
| SCR-RI-05 | Line tables and cession rules | 27, cat actuary | dense log table `IB-29`, "test a risk" via `ri.Cession.preview` | P2/P3 |
| SCR-RI-06 | Cession review for a policy | 28, 27, 11, 35 | policy timeline + waterfall fac→surplus→QS→net; embedded in POL policy file | P2 |
| SCR-RI-07 | Cession exceptions queue | 28, 27 | exception-first `IB-28`, queue `IB-04`, AI-RI-02 | P2 |
| SCR-RI-08 | Facultative placement workspace | 27, 11 | stepper Request/Slip/Quotes/Bind/Certificate; `DT-RI-FAC-SLIP`, `DT-RI-FAC-CERT` | P3 |
| SCR-RI-09 | Recoveries dashboard | 28, 27, 24, 18 | filter bar `IB-26`, hero metrics, layers with aggregate position, backlog | P1 |
| SCR-RI-10 | Claim and occurrence recovery detail | 28, 18, 27 | gross → UNL → layer loss → recoverable (`IB-13`), trace, notices (AI-RI-04); `DT-RI-LOSS-NOTICE`, `DT-RI-EVENT-NOTICE`, `DT-RI-CASH-CALL` | P1 |
| SCR-RI-11 | Catastrophe-event recovery calculation | 27, 28; 18 read | loss timeline, proposed windows, alternatives, confirm (`RI.EVENT_DEFINITION`) | P2 |
| SCR-RI-12 | Aggregate tracker and reinstatement schedule | 28, 27 | chronological occurrences with running totals | P1 (tracker) / P3 (reinstatement premium) |
| SCR-RI-13 | Deposit premium and adjustment schedule | 28 | instalments, GNPI, projected adjustment | P1 Should / P3 |
| SCR-RI-14 | Commission adjustments | 28 | LR, scale position, history | P3 |
| SCR-RI-15 | Bordereaux generator | 28 | params, preview, reconciliation pass/fail `IB-32`, AI-RI-03; `DT-RI-BORDEREAU` | P3 |
| SCR-RI-16 | Statement of account | 28; 24 approve | technical account view `IB-13`, response/disputes; `DT-RI-SOA` | P2/P3 |
| SCR-RI-17 | Cash calls and settlement | 28, 24 | two work views, sanctions, BIL status, failure/stop reason | P1 (execution) / P3 |
| SCR-RI-18 | Collateral register | 28, 48, 24 | coverage and expiries | P3 |
| SCR-RI-19 | Accumulation dashboard | cat actuary, 27, 48; 11 read | zone table/map, footprint panel, OED export, AI-RI-05 `IB-30` | P2 |
| SCR-RI-20 | Reinsurance close and reconciliation | 28, 24 | `IB-11` + binary controls `IB-32` | P1 |

Design-guide patterns referenced: IB-01, 02, 03, 04, 05, 06, 07, 08, 09, 10, 11, 12, 13, 14, 15, 16, 17, 19, 20, 21, 23, 24, 25, 26, 28, 29, 30, 31, 32, 33. Document types (R-84; OI-RI-14 closed): `DT-RI-FAC-SLIP`, `DT-RI-FAC-CERT`, `DT-RI-LOSS-NOTICE`, `DT-RI-EVENT-NOTICE`, `DT-RI-CASH-CALL`, `DT-RI-BORDEREAU`, `DT-RI-SOA`. §16.6 notes field tables for SCR-RI-12…14 and SCR-RI-18 are "compact" and must be completed before their wave (XMR-F-347).

---

## 9. Regulatory, tax and statutory rules

### 9.1 Stated explicitly by the source

| Rule | Source / ID |
|---|---|
| **No tax on ceded premium in Greece**: taxes and levies are never ceded or commissioned; RI computes reinsurance-premium tax only if the pack's `TaxCalculator.calculateReinsurancePremiumTax` returns one; "Greece pack: none (legal basis open)" | BR-RI-025, OBL-TAX, C-01, OI-RI-01 |
| **Law 5177/2025 Art. 43** (Greek IPT) lists exemptions (long-term life, ships and aircraft, shipping companies, health of minors) and does **not** mention reinsurance; the prompt's claim of an Art. 43 reinsurance exemption is rejected | C-01, §3.1 |
| Example deltas: IPT €45 and Auxiliary Fund levy €18 on €300 premium are not ceded (only €300 enters ledger) | REQ-RI-075 |
| GNPI on written basis = premium, surcharge and discount charge categories, net of reversals, excluding tax, levy and fee, by booking date (D5, XMR-D-106) | REQ-RI-155 |
| **Ceded business reported as reinsurers' share of the direct SII line**; LoB 13–28 (non-proportional *accepted*) never used for ceded; e.g. MTPL recovery carries SII LoB 4 | REQ-RI-228, C-05, F-06 |
| SII recoverables: provide gross recoverables + inputs (rating, collateral); DAT computes counterparty default adjustment (Directive Art. 81, DR Art. 42, DR Art. 192 via EIOPA Q&A 1181) | REQ-RI-229, F-04 |
| IFRS 17 ¶60–70A; loss-recovery component ¶66A–66B: store fixed % of claims recoverable per proportionate contract and underlying group | REQ-RI-225, F-03 |
| IFRS 17 is statutory and group book in Greece (R-48) | A-02 |
| Sanctions: EU restrictive measures + group OFAC screening before approval and release of every settlement; Blocked participants refused | REQ-RI-049, REQ-RI-191, OBL-AML |
| GDPR minimisation: notices limited to claim number, loss date, cause, region-level location, line, reserve/paid, injury severity band and count, status — never health details, names of injured, identifiers | REQ-RI-169 |
| Pseudonymise identifiers in all external exports unless named-bordereau clause; non-EU counterparty for named bordereau needs Chapter V transfer basis in register (`REQ-PLT-285`) | REQ-RI-254, OBL-RES |
| OED exports only to EU-hosted platforms in register of information | REQ-RI-221 |
| DORA: every external exchange endpoint (P1 e-mail delivery identities; P3 ACORD endpoints) registered as ICT third party before first use | REQ-RI-245 |
| AI Act: AI-RI-04 classified limited risk (Art. 50); others minimal | §11 |
| BoG (Law 4364/2016): insurers need a risk-management policy covering reinsurance; no legal rule on level/type of reinsurance | OBL-BOG, F-07 |
| IDD / Law 4583/2018: brokers are PTY intermediaries with register data | OBL-IDD |
| Treaty identity: stable treaty id + contract year for supervisory continuity | REQ-RI-231 |
| EIOPA taxonomy 2.10 adds nat-cat templates at CRESTA level; RI supports CRESTA as a hazard scheme | F-05, REQ-RI-213, REQ-RI-223 |
| RI documents are non-fiscal; if a fiscal series were needed CMP issues it (D3) | A-08 |

### 9.2 Referenced but NOT specified (gaps)

- Legal basis for no reinsurance-premium tax in Greece (OI-RI-01) — pending D2 tax/legal opinion.
- Solvency II Directive Art. 41/44 and DR Art. 209–214 article numbers; Bank of Greece acts on reinsurance strategy/concentration (OI-RI-04, UNVERIFIED).
- EIOPA taxonomy 2.10 S.30/S.31 codes and application date (OI-RI-03, C-03, C-04).
- IFRS 17 primary text (OI-RI-06, secondary sources only).
- Collateral rules for third-country reinsurers / equivalence, collateral % (OI-RI-12; BR-RI-021 "framework without values").
- Greek reinsurance-intermediary register treatment; ACORD readiness (OI-RI-08).
- Retention durations RC-RI-* (OI-RI-10; programme schedule XMR-D-259).
- Greek motor/property programme shape, unlimited MTPL layers, indexation (OI-RI-05, F-11 UNVERIFIED).
- Hours-clause durations per peril (C-06, OI-RI-13; seeds UNVERIFIED).
- OED version (OI-RI-07).

### 9.3 Deferred to configuration / country pack

Tax hook (BR-RI-025), collateral rules (BR-RI-021), security floor `ri.security.min_rating.<agency>` (no core default), concentration thresholds (default 25/25/25%), statement cycle (quarterly/45/30 days), ageing buckets, exception SLAs (BR-RI-032), batch window (30 s / 500), surge window (10 s), large-loss default 50% of attachment, rating max age 365 days, incomplete-set wait 15 min, accumulation lag 15 min, hazard schemes (`Geocoder`), sanction lists, numbering formats, document language, retention durations.

---

## 10. Greek-market specifics

- **AFM**: only mentioned negatively — OED exports must contain "no names or AFM" (REQ-RI-221).
- **myDATA**: not mentioned; RI documents are non-fiscal (A-08, D3).
- **gov.gr / Information Centre / bureau**: not applicable / not mentioned.
- **Bank of Greece** supervision (Law 4364/2016); ROLE-44 supervisor gets evidence packs via CMP evidence room, no screen access (§2).
- **Greek tax**: Law 5177/2025 Art. 43 discussion (C-01); IPT and Auxiliary Fund levy are non-cedable charge types (REQ-RI-075).
- **Greek language**: all screens bilingual with R-101 switch; Greek labels given per field, † marks working translations for ROLE-46; AI-RI-04 drafts bilingual notices; Greek transparency texts supplied for AI-RI-01 and AI-RI-04.
- **Currency**: EUR functional, USD group (Fairfax group reporting, REQ-RI-232); treaties often USD.
- **Time zone**: Europe/Athens contract activation (REQ-RI-058).
- **Hazard zoning**: Greek seismic zoning + CRESTA for P2 (§16.5 decision 7); example zone `GR-EQ-Z3`.
- **Motor**: motor XoL with unlimited MTPL bodily-injury layers and indexation (stability) clause modelled as clause data (REQ-RI-043, Should P1; UNVERIFIED for Greek programmes).
- Legal entity example `GR01`.

---

## 11. Controls

**Authority types (§12, registered via `REQ-PLT-100`; dimensions entity, line, amount+ccy, contract type):** `RI.CONTRACT_APPROVE`, `RI.SECURITY_EXCEPTION`, `RI.RETENTION_OVERRIDE`, `RI.LINE_OVERRIDE`, `RI.FAC_BIND`, `RI.EVENT_DEFINITION`, `RI.RECOVERY_ADJUSTMENT`, `RI.SETTLEMENT`, `RI.STATEMENT_APPROVE`, `RI.COLLATERAL`, `RI.CAPACITY_SET`.

**Maker-checker (RI additions to contract §3.9.2, accepted R-51 / CCR-RI-07):** programme and contract version approval; participation changes after approval; security list; line tables and PML factors; retention/line overrides above second threshold; re-cession runs above threshold; cat window revisions; manual recovery adjustments above threshold; premium and commission adjustments above threshold; statement and bordereau approval; settlement approval and release above threshold; manual settlements; collateral draws/releases; zone capacity changes; commutations; write-off of reinsurer balances. Approvals bound to content hash (`REQ-PLT-004`, `REQ-PLT-117`).

**SoD (§12):** contract enterer ≠ approver; statement preparer ≠ approver; settlement proposer ≠ approver/releaser; capacity setter ≠ approver; `ri.settlement` users cannot change payee bank accounts in BIL (`REQ-BIL-199`); AI identities never checker (`REQ-PLT-121`).

**Audit (§12):** field-level before/after via `REQ-PLT-002` for every lifecycle action listed (programme/contract, participations, security, line tables, overrides, re-cession, occurrence linking, cat windows, manual adjustments, deposit/commission adjustments, notices, bordereaux/statements, cash calls, settlements, collateral, capacity, exports with recipient, AI outcomes, queries on P1 named bordereaux). Calculation traces per batch with engine and config hash (`REQ-RI-132`, `REQ-RI-258`). Traceability ≤3 clicks or 1 API call to source event and journal (`NFR-RI-012`). No deletion of referenced contracts/versions/participations (`REQ-RI-065`); no RI item reversed — corrections are new linked items (`REQ-RI-240`).

**GDPR:** RI holds identifiers not names for retail; P1 data only in commercial named bordereaux by clause; DSAR export/restrict (`REQ-RI-253`); pseudonymisation (`REQ-RI-254`); allow-listed notices (`REQ-RI-169`); `NFR-RI-010` 0 P2/P3 findings; retention codes RC-RI-CONTRACT/ACCOUNT/CALC/EXPORT with legal holds (`REQ-RI-252`).

---

## 12. AI features (§11) — all default **off**, tenant/entity/role/user toggle, kill switch ≤60 s (`REQ-RI-257`), `AiInteractionRecord` (`REQ-RI-259`), registered in `REQ-CMP-007` and DAT registry `REQ-DAT-005`. Module works fully with AI off.

| ID | Feature | Classification | Phase (§1.5) | Needed for MVP? | Fallback |
|---|---|---|---|---|---|
| AI-RI-01 | Treaty wording and slip extraction (approve-the-diff in SCR-RI-03) | Minimal | P1 pilot, off by default | No | manual entry |
| AI-RI-02 | Cession exception resolution assistant | Minimal | P2 | No | `REQ-RI-096` actions |
| AI-RI-03 | Statement/bordereau reconciliation agent (`IB-31`) | Minimal | P3 | No | rule matcher `REQ-RI-243` |
| AI-RI-04 | Large-loss and event notice drafting (bilingual) | **Limited risk (AI Act Art. 50)** | P3 | No | templates |
| AI-RI-05 | Programme and recovery analytics assistant (NL→query) | Minimal | P3 | No | reports, `ri.Contract.simulate` |
| AI-RI-06 | Cession and recovery anomaly detection | Minimal | P2 | No | invariants `REQ-RI-092` |

Monitoring profiles MP-D, MP-C, MP-C, MP-B, MP-F, MP-E (PRD-15 §11.2). The hours-clause window optimiser (`REQ-RI-140`) is explicitly deterministic, not AI. Only AI-related Must P1 items are the governance hooks `REQ-RI-257` and `REQ-RI-259`.

---

## 13. Open issues / assumptions / CCRs

**Open issues (§16.1):**

| ID | Topic | Status |
|---|---|---|
| OI-RI-01 | Legal basis for no RI premium tax (Art. 43) | Open — D2 tax/legal opinion |
| OI-RI-02 | Surplus capacity basis used in wordings (C-02) | Open |
| OI-RI-03 | EIOPA 2.10 template list/codes | Open (UNVERIFIED) |
| OI-RI-04 | SII Art. 41/44, DR 209–214; BoG acts | Open (UNVERIFIED) |
| OI-RI-05 | Actual motor/property programme; **confirm A-01 before G0** | Open — critical for MVP scope |
| OI-RI-06 | IFRS 17 primary text | Open (FIN ownership part closed R-52) |
| OI-RI-07 | OED version / cat platform | Open |
| OI-RI-08 | ACORD readiness; intermediary register | Open |
| OI-RI-09 | Broker/reinsurer portal via CHN | Open (P3 decision) |
| OI-RI-10 | Retention durations | Open (basis part closed R-48) |
| OI-RI-11 | PML source for commercial | Open (P3) |
| OI-RI-12 | Collateral rules third-country | Open |
| OI-RI-13 | Hours-clause durations | Open |
| OI-RI-14 | DT-RI-* document types | **Closed** (XMR-CR-RI-01) |

**Assumptions (§16.2):** A-01 motor XoL-only in P1 (D6, confirm before G0); A-02 RI book-neutral; A-03 CLM events carry line, coverage, SII LoB, IFRS17 ref, cat code, three-currency amounts, loss time via `clm.Claim.get`; A-04 POL segment feed exposes SI/geocodes/hazard keys from P2; A-05 BIL RI_SETTLEMENT confirmed (R-53); A-06 FIN assigns RI-held group (R-52); A-07 recoveries on case reserves and payments only; A-08 RI docs non-fiscal; A-09 annual terms only in P1 (D6).

**CCRs (§16.4), all Accepted:** CCR-RI-01 `REQ-PTY-097` → Must/P1 with rating history + LEI (R-53); CCR-RI-02 BIL RI_SETTLEMENT + RI receivables (R-53; REQ-BIL-348…351); CCR-RI-03 17 new RI events (R-47); CCR-RI-04 FIN assigns RI-held groups (R-52; REQ-FIN-139, 295); CCR-RI-05 glossary terms owned by RI (R-54); CCR-RI-06 `TaxCalculator` reinsurance op (R-49; implemented as `calculateReinsurancePremiumTax`, REQ-MKT-315); CCR-RI-07 maker-checker list (R-51).

**Conflicts with prompt (§3.3):** C-01 (Art. 43 exemption not borne out), C-02 (HMRC 50% vs 40% — both configurable via capacity basis), C-03/C-04 (taxonomy dates/codes UNVERIFIED), C-05 (LoB 13–28 are accepted RI), C-06 (hours clause typicals unverified), C-07 (resolved by R-53).

**Ten pre-build decisions (§16.5):** ledger design and POL segment feed as authority; A-01 before G0; RI settlement bank accounts/cut-offs; IFRS17 group granularity (contract vs section) with FIN; FIN posting rules for RI items incl. deposit/adjustment/commission (XMR-F-132); default clause options per entity (ROE, ALAE, notification threshold, surplus basis); accumulation capacity ownership and P2 hazard schemes; counterparty exchange channels; security policy parameters; migration scope for RI history.

**Risks:** RK-RI-01…10 (wording variety, cat surge, ledger divergence, tax misunderstanding, master data (resolved), FX clause misapplication, taxonomy change, personal data leak, motor QS exists, partial delta set / rejected disbursement).

---

## 14. Conflicts and ambiguities found

### (a) With infra/stack

1. **"Lakehouse"** — `REQ-RI-227` (Must P1): "provide `ri.Ifrs17Link.query` … and a daily data product **for the lakehouse**." ARCHITECTURE-DECISIONS says no lakehouse; reporting marts are PostgreSQL schemas. Reinterpret as a DAT mart feed.
2. **Broker vocabulary** — §8: "Topic `ri.events.v1`. Partition keys: …" and "gap-free sequence per partition"; `REQ-RI-004` "per-aggregate order … gap-free sequence on the contract partition". The stack is an in-process outbox (no broker). Needs mapping: "partition key" → outbox ordering key per aggregate; "topic" → event namespace. Ordering by `ri_contract_id` for recoveries vs `policy_id` for cessions must be enforced by the outbox dispatcher.
3. **Workflow engine** — `REQ-RI-102` runs fac placement "as a workflow with deadline reminders" via `REQ-PLT-007` (PLT workflow); §1.4 lists "workflow" under PLT. Stack forbids workflow servers/BPM: implement as an explicit state machine + Hangfire timers.
4. **Recalculation batching / surge mode** (`REQ-RI-130`, `REQ-RI-133`, BR-RI-026: 30 s window or 500 movements, one batch in flight per contract year, priority queue, back-pressure) — no broker; must be built on outbox + Hangfire (or a DB-backed queue with advisory locks). Not specified how.
5. **Geospatial** — footprint queries over 400,000 located risks with polygon inclusion ≤5 s p95 (`REQ-RI-218`, `NFR-RI-007`, BR-RI-035) imply a spatial index (PostGIS extension). Not in the stack list; needs a decision (Azure PG Flexible supports PostGIS).
6. **"Scraped" metrics** — `REQ-RI-256` "W scraped; T all series exist" suggests Prometheus pull; stack is OpenTelemetry → Azure Monitor. Minor wording.
7. **External formats** — XLSX generation/import (`REQ-RI-175`, `REQ-RI-242`), ACORD messages (`REQ-RI-241`), OED files (`REQ-RI-221`) need third-party libraries/specs not chosen (stack rule 11 minimal dependencies).
8. Vendor names: Duck Creek (S-13) cited only as research reference (CD-20) — no conflict. No mention of Kafka, Camunda, microservices, Kubernetes, Java, or other DBs.

### (b) With system contract / other PRDs

1. **FIN handlers owned by "PRD-09"** — §8.1 says FIN consumers of `DepositPremiumDue`, `PremiumAdjustmentCalculated`, `CommissionAdjusted` were "added by PRD-09 per XMR-F-132". Verify PRD-09 is FIN; `DepositPremiumDue` is Should P1, so FIN must handle it in P1 if RI ships it.
2. **RI↔FIN bootstrap ordering** — `REQ-RI-224` (Must P1) stamps every cession/recovery with the RI-held group from `fin.Ifrs17Group.riHeldAssignment`, which FIN assigns on consuming `RIContractActivated` (`REQ-FIN-139`). The behaviour when a claim movement arrives before FIN has assigned the group (e.g. migrated contracts, retro activation) is not stated.
3. **Amount count wording** — §1.2/`REQ-RI-204` say four amounts; §8 says "three currencies as the contract requires … plus contract currency". Consistent in substance but confirm the contract §3.2.1 rule 5 envelope permits a fourth amount.
4. **WRK pattern list** — §15.1 says patterns "RI-FAC-QUOTE and RI-FAC-VALIDATION already listed by WRK", but RI uses many more activity patterns (RI-CESSION-EXCEPTION, RI-LARGE-LOSS-NOTICE, RI-SECURITY-ALERT, RI-LAYER-EXHAUSTED, RI-FAC-CHANGE-NOTICE, RI-SOA-DISPUTE, RI-CASH-CALL-CHASE, RI-COLLATERAL-SHORTFALL, RI-SETTLEMENT-FAILED); `RI-FAC-VALIDATION` is never used in RI. WRK registration of the others is not confirmed.
5. **ClaimReported / ClaimUpdated** consumed (§8.2) but `REQ-RI-113`'s consumed list omits them (they are under `REQ-RI-114`). Minor.
6. Settlement FSM (§7.3.8) fails on `DisbursementReturned`/`DisbursementVoided` too, but `REQ-RI-262` and `REQ-RI-192` cover only Rejected/Stopped/Cleared — no requirement/acceptance test for Returned/Voided.

### (c) Internal contradictions (mostly P1 cut vs dependencies)

1. **Settlement in P1 without proposal/approval.** `REQ-RI-192` (execute via BIL), `REQ-RI-195`, `REQ-RI-262`, `REQ-RI-191` (screen "immediately before approval and before release") are **Must P1**, but `REQ-RI-189` (propose settlement) and `REQ-RI-190` (approve under `RI.SETTLEMENT`, maker-checker) are **Won't P1 / P3**. Only manual settlement `REQ-RI-193` is Should P1. How a P1 settlement is created and approved is undefined.
2. **Cession ledger import in P1.** `REQ-RI-248` (Must P1) "import opening cumulative cessions per risk interval" and Requires `REQ-RI-083` (Won't P1, P2); `REQ-RI-007` includes `ri.Import.cessionHistory`. §14.y says "(P2+)". The P1 scope of 248 should be recoveries only.
3. **Cat XoL (P2) requires reinstatement premium (P3).** `REQ-RI-144` Must P2 Requires `REQ-RI-146` (Won't P1, Must **P3**); J-05 (home phase) shows reinstatement premium computed; §1.5 puts reinstatement premiums in P3. Also anchor `REQ-RI-003` (Must P1) text includes "booking recovery deltas with reinstatement premiums pro rata to amount (and to time …)", while 146/147/149 are P3.
4. **Exhaustion in P1 needs reinstatement counting.** `REQ-RI-150` (Must P1) tracks "remaining reinstatements"; `REQ-RI-149` (consume reinstatements in sequence, never beyond allowed) is P3. BR-RI-008/038 (aggregate = (n+1)×Lim) must therefore be built in P1 without the premium part.
5. **Close in P1 includes P3 pieces.** `REQ-RI-237` (Must P1) includes "accrual of deposit instalments, adjustment estimates" — deposits are Should P1 (153/154), adjustments P3 (156).
6. **Reconciliation split.** `REQ-RI-236` daily control totals Must P1, but the bordereau = statement = items = journals reconciliation `REQ-RI-182` is P2; `REQ-RI-183` balances report Must P1 references "deposits due and paid" from Should P1 features.
7. **Anchor phase vs family.** `REQ-RI-006` anchor is P3, but its family includes P2 Must items (176, 177, 178, 182, 185, 186); §1.5 puts QS quarterly statements in Home (P2). Anchor phase understated.
8. **A-01 promotion list includes an already-Must item.** A-01/OI-RI-05 say if a motor QS exists, "`REQ-RI-002` and `REQ-RI-075`…`REQ-RI-088` move to Must P1" — `REQ-RI-075` is already Must P1, and 077–079 are P3 (surplus/PML/fac), which a motor QS does not need. The contingency list is imprecise.
9. **Example arithmetic.** `REQ-RI-132`: "a recovery of €450k … trace shows UNL €950k − attachment €250k capped at limit €500k with aggregate position" — the layer formula gives €500k; €450k is only true if €50k of aggregate deductible was consumed, which is not stated. All other worked examples I checked (002, 003, 070, 076, 078, 083, 116, 119, 123, 124, 134, 137, 147, 156, 161, 163, 214, 215, GT-04, GT-06) are arithmetically consistent.
10. **Cat programme in P1 examples.** `REQ-RI-030` example programme MOTOR-GR-2027 contains `MCAT-2027` (cat contract) and `REQ-RI-031` lists CAT_XOL among P1 types, but cat XoL recovery (137–145) is P2 and "Motor not checked" for accumulation. P1 can store but not calculate cat contracts — should be stated.
11. **NFR-RI-003** (32,000-claim cat replay across 4 cat layers) is unphased; cat layers are P2. For P1 it presumably applies to per-event motor XoL only.
12. **`REQ-RI-090` `listByPolicy`** is Should P2, but POL's policy-file reinsurance section (`REQ-POL-326`) consumes it — check POL's phase for that section.

### (d) Cannot be built without a decision

- **A-01 / OI-RI-05** — motor XoL-only must be confirmed before G0; otherwise ~14 cession reqs jump into P1 (RK-RI-09).
- Settlement flow in P1 (item c1 above).
- Hours-clause "exact optimisation over loss timestamps" (`REQ-RI-140`) — algorithm and complexity bound for ~32,000 claims × multiple windows not specified (GT-07 says "equals the brute-force maximum").
- Batching/queue mechanism on outbox/Hangfire for surge (a4).
- PostGIS or alternative for footprint (a5).
- Default clause options per entity (ROE, ALAE, notification %, surplus basis — OI-RI-02, decision 6).
- Security floor values (no core default), collateral rules (OI-RI-12), concentration limits.
- IFRS 17 group granularity contract vs section (decision 4).
- Retention durations (OI-RI-10) before system test.
- Bordereau/statement XLSX/ACORD formats and libraries.

---

## 15. Build notes

**Hardest parts**
1. **Recovery engine (P1):** ClaimView with per-claim re-sequencing (out-of-order hold), UNL composition per clause (BR-RI-028/029), per-risk and per-event occurrences with user linking, layer maths with unlimited layers and placed %, order-dependent annual aggregates R(Cₖ) − R(Cₖ₋₁) with full contract-year restatement (BR-RI-007), paid/outstanding split, four-currency conversion with ROE clause variants evaluated in contract currency, target-minus-booked recovery deltas per occurrence × layer × participant, batching (30 s / 500, one batch in flight per contract year), idempotence and order-independence (NFR-RI-004, property invariants 3–5). This is the P1 core.
2. **Bitemporal contract versions** (`versionAt(validAt, knownAt)`, exclusion constraints on valid time) with maker-checker on content hash.
3. **P2 cession ledger:** per term × element × charge type × interval cumulative ledger, share timeline aligned to POL segments, inuring waterfall, delta-set completeness (`set_id/set_size/index`, INCOMPLETE_SET timeout), rounding residuals to lead with ≤1 minor unit drift, NET/GROSS parity.
4. **Cat hours-clause optimiser** and surge mode (P2).
5. **Accounting tail (P3):** reinstatement premiums, deposit/adjustment, sliding-scale/profit commission, statements, bordereaux, cash calls, netting, settlement, collateral, ACORD.

**Must exist first**
- PLT: outbox with ordered per-aggregate dispatch, audit, authority/maker-checker with content hash, numbering (`RI_CONTRACT`), time service, `plt.Fx.getRate` with fallback, AI kill switch hooks, retention engine.
- MKT: `mkt.Rounding.apply`, configuration resolve.
- PTY: `REQ-PTY-097` reinsurer master data with LEI + rating history (Must P1 per R-53), `pty.Screening.screen`.
- CLM: claim financial events with per-claim sequence and cost types (`Indemnity`, `ExpenseAllocated`, `StatutoryInterest`, `ExpenseUnallocated`), `clm.Claim.get` with loss_at, large-loss flag.
- FIN: business-event intake, posting keys, `JournalPosted`, RI-held group assignment on `RIContractActivated` (`REQ-FIN-139/295`), daily control-total intake by 06:00 (NFR-RI-017), opening balances for `origin=MIGRATION`.
- BIL: RI_SETTLEMENT disbursement and receivable registration (`REQ-BIL-348…351`) and Rejected/Stopped events.
- PFC: RI-cedable flags, RI risk class, SII LoB mapping (`REQ-PFC-122/093/005`).
- POL: `ChargeDeltaEmitted` (P1 only for GNPI/ignored counts; full use P2), segment feed (P2).
- DOC: DT-RI-LOSS-NOTICE for P1 large-loss notices.

**Suggested slicing**
- **S1 (P1 foundation):** schema `ri`; programme/contract/section/layer/clause/participation model with bitemporal versions, validation (037, 038), approval and activation; security list, rating snapshot, sanctions gate; registry APIs (`REQ-RI-001`); SCR-RI-02/03/04; import contract + reversal (247, 261).
- **S2 (P1 recovery engine):** ClaimView consumer, occurrences, UNL, per-risk/per-event layers, aggregate trackers, exhaustion (without reinstatement premium), batching, traces, `ri.Contract.simulate`; golden GT-05, GT-06, GT-08, GT-12; property tests; SCR-RI-09/10/12.
- **S3 (P1 money and FIN cycle):** four-amount FX, technical account items, posting keys, `source_correlation_key`, `RecoveryCalculated`/`RIContractActivated` to FIN, `JournalPosted` loop, daily control totals, RI close (SCR-RI-20), balances report, IFRS17 links, SII data products, large-loss notices (DOC), settlement via BIL including Rejected/Stopped (after the P1 proposal/approval gap is resolved).
- **S4 (P1 hardening):** migration imports of aggregates/open items/recoveries with reconciliation; ops view; retention; DSAR; DORA endpoint registration; AI hooks (AI-RI-01 pilot optional).
- **P2:** proportional cession ledger + exceptions (SCR-RI-05/06/07), QS commission, proportional recoveries, cat XoL with hours clause and surge (SCR-RI-11), accumulation API for UW (SCR-RI-19), QS statements.
- **P3:** surplus/PML/fac (SCR-RI-08), reinstatement premiums, adjustments, commissions (SCR-RI-13/14), bordereaux (SCR-RI-15), full statement/settlement/cash calls/collateral (SCR-RI-16/17/18), ACORD.

**RI↔FIN cycle summary (extra focus).** RI is book-neutral: it emits business events (`CessionCalculated`, `RecoveryCalculated`, `ReinstatementPremiumDue`, `DepositPremiumDue`, `PremiumAdjustmentCalculated`, `CommissionAdjusted`, `CashCallRaised`, `StatementIssued`, `SettlementRecorded`, `RIContractActivated`) carrying posting key (item type, contract type, section type, direction, line, participant type; `REQ-RI-234`), four amounts with rate ids, SII LoB, underlying IFRS 17 group and RI-held group refs, and `source_correlation_key` (`REQ-RI-233`; the W3C trace id is not used for lineage). FIN posts per book without calling back (`REQ-RI-004`) and returns `JournalPosted` (→ Posted) and `ReconciliationBreakRaised` (`REQ-RI-235`). FIN assigns RI-held IFRS 17 groups on `RIContractActivated` and serves them via `fin.Ifrs17Group.riHeldAssignment`, which RI reads to stamp later events (`REQ-RI-224`). Daily control totals per item type, contract and currency go to FIN by 06:00, before FIN closes reconciliation at 07:00 (`REQ-RI-236`, `NFR-RI-017`). FIN alone revalues; RI provides open balances per currency (`REQ-RI-208`). RI close feeds FIN's close checklist (`REQ-RI-237` → `REQ-FIN-007`). A FIN journal reversal with no RI correcting delta raises a break and does not reverse the RI record (`REQ-RI-091`, `REQ-RI-131`). Migration opening events are flagged `origin=MIGRATION` and FIN does not post them as new business (`REQ-RI-007`, `REQ-FIN-010`).

**Is RI in the Motor MVP? Yes, partly.** 93 Must P1 requirements. In P1: the full data model, contract registry, motor per-risk and per-event XoL recovery tracking with aggregate trackers and exhaustion, large-loss notification, recovery events to FIN, reinsurer security, sanctions, multi-currency, IFRS 17/SII recoverable data, a reinsurer balances report, BIL settlement execution, and migration. Deposit-premium schedules are Should. Not in P1: proportional cession (Won't P1; it depends on A-01 being confirmed before G0), cat XoL, accumulation (UW bind check from P2), statements (P2), bordereaux, fac, reinstatement premiums, commissions, cash calls and collateral (P3).
