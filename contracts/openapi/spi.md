# SPI catalogue — country-pack service provider interfaces

Contract for the **40 SPIs** of the MKT SPI catalogue (`REQ-MKT-002`, contract §3.5.8, PRD-17 §9.4). SPIs are **C# interfaces** declared in the core (MKT contracts) and implemented by country packs; they are not HTTP APIs and have no OpenAPI paths. Callers never reference a pack: they obtain the implementation bound for their legal entity, axis value and date through `mkt.Spi.bind` (in-process, `mkt.yaml`), and core code never references a country pack (ADR rule 8). New SPIs only via a contract change request.

Source: PRD-17 §9.4 (catalogue summary §9.4.0, per-SPI specifications §9.4.1–§9.4.42, extensions §9.4.43). Operation signatures are copied from the PRD. Country content (Greek, Cyprus stub, Bulgaria fixture values) is pack data and is deliberately **not** repeated here; see the cited PRD section.

## Conventions

- **Interface name**: `I<SpiName>` in `CoreIns.Mkt.Contracts.Spi` (for example `ITaxCalculator`). Method names are the PRD operation names in PascalCase (`calculate` → `CalculateAsync`). The SPI owner's operation names are binding (R-87).
- **Mode** (PRD-17 §9.4.0): S = synchronous in-process; A = asynchronous through the worker-hosted adapter ("integration hub", D-ARC-01) with a result callback or event; Batch = file/batch.
- **Idempotency**: P = pure (same input → same output); K = idempotent on the caller's key (the `idempotencyKey` argument); N/A = query.
- **Fallback** "fail closed": the caller's operation fails with `MKT-ERR-SPI-UNBOUND` or the SPI error.
- **Error categories** used by every SPI: `VALIDATION`, `RULE_MISSING`, `NOT_APPLICABLE`, `UNAVAILABLE`, `TIMEOUT`, `CONTRACT_VIOLATION` (as listed per SPI below). They map to typed exceptions/results of the interface, and the calling module translates them into its own `<MOD>-ERR-*` Problem Details.
- **Core default**: the behaviour when no pack binds the SPI for the entity and date (the core implementation).
- A **Cyprus stub pack** implements every SPI in CI to prove that nothing Greek is hard-coded (`REQ-MKT-008`).

## Catalogue summary

| # | SPI | Purpose | Main callers | Mode | Binding axis | Idem. | Timeout / fallback | Core default |
|---|---|---|---|---|---|---|---|---|
| 1 | `IdValidator` | Validate/normalise identifiers (incl. scheme `VEHICLE_PLATE`) | PTY, PFC, POL, MIG, DAT, CMP (via PTY), CHN, CLM | S (verify: A) | SCHEME | P / K | 50 ms validate; verify 3 s → `Unverified` | Optional schemes: accept as `Unverified`; mandatory schemes: fail closed |
| 2 | `NameTransliterator` | Native → Latin; search variants | PTY, DOC, WRK, BIL, MIG, CHN, CLM, PLT | S | SCHEME (script + country) | P | 20 ms; fail closed | Identity for Latin; error otherwise |
| 3 | `AddressFormatter` | Parse, validate, format addresses | PTY, DOC, POL, MIG, CHN, CLM | S | RISK_LOCATION / address country | P | 50 ms; free-form | Free-form lines, no postcode check |
| 4 | `TaxCalculator` | Taxes, levies, stamps as charge types; `treatment` of each line on credits, cancellations and voids (D2) | `calculate`: RAT (POL-originated charges), BIL (own fees and receipt stamps); `treatment`: RAT, BIL, FIN (validation); RI (optional reinsurance premium tax) | S | RISK_LOCATION (+subdivision) | P | 50 ms; fail closed | None |
| 5 | `FiscalDocumentChannel` | Fiscal documents, series, registration ids | CMP (BIL, FIN consume) | A | LEGAL_ENTITY_HOME / BRANCH_LOCATION | K | 30 s per attempt; queue on outage | `NotRequired` |
| 6 | `EInvoiceProvider` | Structured e-invoices | CMP | A | LEGAL_ENTITY_HOME | K | 30 s; queue | `NotRequired` |
| 7 | `BureauAdapter` | Motor bureau / insured-vehicle register | CMP | A | RISK_LOCATION | K | 30 s; queue | `NotRequired` |
| 8 | `StatutoryClockSet` | Clock values | CMP | S | CONTRACT_LAW_JURISDICTION or per clock | P | 20 ms; EU default or fail closed | EU-layer defaults only |
| 9 | `NumberingScheme` | Identifier formats and series | PLT (all) | S | LEGAL_ENTITY_HOME | P | 20 ms; fail closed | Prefix + padded sequence |
| 10 | `HolidayCalendarProvider` | Holiday content | PLT (all other modules use the PLT calendar service, CD-17) | Batch (yearly load) | SCHEME (country/region) | P | n/a; previous year's rules flagged | Weekends only |
| 11 | `PaymentReferenceGenerator` | Payment references | BIL | S | LEGAL_ENTITY_HOME | P | 20 ms; RF default | ISO 11649 RF |
| 12 | `BankFileFormat` | Payment and statement files | BIL | S (file) | SCHEME (format) | P | n/a | SEPA ISO 20022 pain.001/008, camt.053/054 |
| 13 | `PayeeVerification` | Verification of payee | BIL | S external | LEGAL_ENTITY_HOME | K | 2 s; `NotAvailable` | `NotAvailable` |
| 14 | `RegimeCodeList` | Regulatory code lists | PFC, DAT, FIN, RI | S | SCHEME (regime) | P | 20 ms; fail closed | None (eu pack supplies SII) |
| 15 | `DocumentLanguageRule` | Binding and informative languages | DOC, PFC, UW, RI, CLM, WRK | S | CONTRACT_LAW_JURISDICTION | P | 20 ms; entity default | Binding = entity default language |
| 16 | `MotorDataProvider` | Vehicle data, wallet pre-fill, valuation | POL, CHN, UW, RAT | S external | RISK_LOCATION | K | 3 s; `NotAvailable` | `NotAvailable` |
| 17 | `RegistryLookup` | Business/tax registry | PTY | S external | SCHEME | K | 3 s; `NotAvailable` | `NotAvailable` |
| 18 | `IntermediaryRegister` | Intermediary register check | PTY | S external | LEGAL_ENTITY_HOME / host | K | 3 s; snapshot | `UnknownRegister` |
| 19 | `ComplaintRules` | Complaint deadlines, ADR bodies | CMP | S | POLICYHOLDER_RESIDENCE (+home) | P | 20 ms; fail closed | None |
| 20 | `RefusalDocumentRule` | Legal refusal documents | UW, DOC | S | RISK_LOCATION | P | 20 ms; `NotRequired` | `NotRequired` |
| 21 | `SanctionsListSource` | Lists per jurisdiction and group | PTY | Batch fetch + S list resolution | LIST | K | fetch retries; last good list with age warning | EU consolidated list |
| 22 | `ClaimsHistoryFormat` | Claims-history certificate content | CLM, DOC | S | CONTRACT_LAW_JURISDICTION | P | 20 ms; core generic format | Generic field list |
| 23 | `FriendlySettlementClearing` | Inter-insurer clearing | CLM | A | RISK_LOCATION | K | 30 s; queue | `NotApplicable` |
| 24 | `PricingConstraint` | Legal pricing limits | RAT, PFC | S | RISK_LOCATION | P | 20 ms; EU defaults | EU protected-characteristics list |
| 25 | `ConsentRules` | Consent purposes and models | PTY, CHN, DOC | S | POLICYHOLDER_RESIDENCE | P | 20 ms; strictest (opt-in) | Opt-in for marketing on all channels |
| 26 | `ESignatureProvider` | Signature providers and levels | DOC | S selection, A signing | CONTRACT_LAW_JURISDICTION | K | 30 s; next provider | Simple electronic signature with evidence |
| 27 | `DigitalIdentityProvider` | Digital identity / wallet | CHN, PLT | S selection | POLICYHOLDER_RESIDENCE | K | 5 s; none (manual) | None |
| 28 | `TaxReturnFormat` | Tax and levy returns | FIN, CMP | Batch | RISK_LOCATION (host State) | P | n/a | Generic CSV totals by class |
| 29 | `MandatoryWordingSet` | Mandatory clause references | DOC, PFC | S | CONTRACT_LAW_JURISDICTION | P | 20 ms; empty set + warning | Empty set |
| 30 | `Geocoder` | Geocoding and versioned hazard keys | PTY, POL, RAT, UW, CLM, CHN, RI, DAT | S | RISK_LOCATION | K / P | 2 s → `NotAvailable`; postcode table | `NotAvailable`; empty hazard keys |
| 31 | `PolicyLifecycleRules` | Refunds, notice, reinstatement gap, suspension | POL, BIL | S | CONTRACT_LAW_JURISDICTION | P | 20 ms; fail closed | None |
| 32 | `MotorCompensationBodyAdapter` | Guarantee fund, Green Card bureau, compensation bodies | CLM | A | RISK_LOCATION | K | 30 s; queue | `NotApplicable` |
| 33 | `StatutoryDeliveryRule` | Media, proof levels, notification dates | DOC, BIL, POL, CMP | S | CONTRACT_LAW_JURISDICTION | P | 20 ms; fail closed for statutory | Durable medium with read evidence (non-statutory) |
| 34 | `PaymentChannelProvider` | QR, national networks, acquirers | BIL, CHN | S | LEGAL_ENTITY_HOME | K | 3 s; channel not offered | No extra channels |
| 35 | `InboundDocumentProfile` | Inbound classes, schemas, parsers, thresholds | WRK | S | SCHEME | P | 50 ms | ICAO 9303 MRZ + generic classes |
| 36 | `IdentityFederationProvider` | National eID federation | PLT | S | POLICYHOLDER_RESIDENCE | P | 20 ms | None |
| 37 | `IncidentReportingChannel` | DORA incident submission | PLT | A | LEGAL_ENTITY_HOME | K | 30 s; manual | `ManualSubmissionRequired` |
| 38 | `FxRateSource` | Official FX source | PLT (FIN and RI read rates through the PLT FX service) | S | LEGAL_ENTITY_HOME | P | n/a | Euro reference rates |
| 39 | `LegacyDataProfile` | Legacy data profiling and normalisation | MIG | S / batch | SCHEME (country of legacy source) | P | 50 ms per record; finding `UNPROFILED` | Generic UTF-8/NFC checks only |
| 40 | `StatutoryDataReturnFormat` | Statutory data return layouts and files (A.1004, national statistics, EAEE) | DAT, CMP | Batch | LEGAL_ENTITY_HOME | P | n/a; validation errors returned | `NotRequired` |

## SPI specifications

### 1. `IdValidator`

- **Interface:** `IIdValidator` · **Mode:** S (verify: A) · **Binding axis:** SCHEME · **Idempotency:** P / K · **Timeout / fallback:** 50 ms validate; verify 3 s → `Unverified` · **Core default:** Optional schemes: accept as `Unverified`; mandatory schemes: fail closed
- **Operations:** `validate(scheme, value, context) → {status: Valid | ValidFormat | Invalid | Unverified, normalised, errors[]}`; `verify(scheme, value, evidenceContext) → {status: Verified | NotFound | Mismatch | NotAvailable, source, checkedAt}` (async when external).
- **Errors:** VALIDATION (`CHECK_DIGIT`, `FORMAT`, `LENGTH`), NOT_APPLICABLE (scheme not bound), UNAVAILABLE.
- **Scheme `VEHICLE_PLATE` (REQ-MKT-339):** `normalise(value, context) → {normalised, findings[]}` and `searchKey(value) → key`, pure and pack-bound, 20 ms; `LegacyDataProfile.profilePlate` delegates to it.
- **Callers:** PTY (`REQ-PTY-003`); POL for `VEHICLE_PLATE` (`REQ-POL-279`); PFC field validation; MIG; DAT.
- **Source:** PRD-17 §9.4.1 (line 1617); country content there.

### 2. `NameTransliterator`

- **Interface:** `INameTransliterator` · **Mode:** S · **Binding axis:** SCHEME (script + country) · **Idempotency:** P · **Timeout / fallback:** 20 ms; fail closed · **Core default:** Identity for Latin; error otherwise
- **Operations:** `transliterate(text, sourceScript, ruleSet?) → {latin, ruleSetId, warnings[]}`; `searchVariants(text) → keys[]` (the only source of transliteration and Greeklish match variants; language folding comes from the MKT language rules, REQ-MKT-340).
- **`searchVariants` key shape (F-1e, review N4):** keys are search-normalised (accent-free, upper case, single spaces). The first key is the transliteration of the whole text; the following keys are **per name part** (token): each part's own transliteration and its variants, with every single-point alternative guaranteed (at most 32 keys per part). Callers therefore match **token-wise**: a query part matches a stored name when its key equals (or trigram-matches) one of the stored part keys — W2-PTY indexes the part keys, not only the whole-name key (e.g. "Dokos" finds "Χατζηχριστοδούλου Ντόκος").
- **Errors:** VALIDATION (unsupported script), RULE_MISSING.
- **Callers:** PTY (`REQ-PTY-012`), DOC.
- **Source:** PRD-17 §9.4.2 (line 1626); country content there.

### 3. `AddressFormatter`

- **Interface:** `IAddressFormatter` · **Mode:** S · **Binding axis:** RISK_LOCATION / address country · **Idempotency:** P · **Timeout / fallback:** 50 ms; free-form · **Core default:** Free-form lines, no postcode check
- **Operations:** `parse(lines, country)`, `validate(address) → {status, errors[]}`, `format(address, purpose: POSTAL | DOCUMENT | SINGLE_LINE, script)`.
- **Errors:** VALIDATION (`POSTCODE_FORMAT`, `MISSING_LOCALITY`).
- **Callers:** PTY, DOC, POL (risk addresses).
- **Source:** PRD-17 §9.4.3 (line 1634); country content there.

### 4. `TaxCalculator`

- **Interface:** `ITaxCalculator` · **Mode:** S · **Binding axis:** RISK_LOCATION (+subdivision) · **Idempotency:** P · **Timeout / fallback:** 50 ms; fail closed · **Core default:** None
- **Operation:** `calculate(request) → result`. Request: legal entity, risk jurisdiction (+ subdivision), tax point date, product line and tax class per element, charge lines (element, charge type, premium amount, period, currency, transaction type, unit counts such as vehicles), policyholder type (consumer/business), business basis. Result: tax lines (element, charge type, category TAX | LEVY | STAMP, tax class, base, rate or fixed amount, amount, rounding rule id, rule id, legal source ref), plus document-level lines (for example per-policy or per-receipt stamps).
- **Determinism:** pure; rates from pack data; rounding through `mkt.Rounding.apply` with tax-line rules (REQ-MKT-193, 194).
- **Operation `treatment(request) → result` (D2, REQ-MKT-330).** Request: legal entity, risk jurisdiction (+ subdivision), tax point date, charge type and category, charge origin (POL or BIL), transaction kind (`NEW_BUSINESS`, `ENDORSEMENT_DEBIT`, `ENDORSEMENT_CREDIT`, `CANCELLATION`, `DISTANCE_WITHDRAWAL_VOID`, `VOID`, `RETURN_PREMIUM`, `REINSTATEMENT`, `FEE`, `REFUND`), cancellation source (shared code list, R-84), policyholder type, business basis. Result per charge type: `action` (`APPLY`, `REDUCE_PRO_RATA`, `REVERSE_AS_VOID`, `KEEP_NOT_REDUCED`, `INSURER_BEARS`), `customerCredit` (`PRO_RATA`, `FULL`, `NONE`), `authorityLiability` (`REDUCE`, `NOT_REDUCE`), `fiscalDocument` (`CREDIT_NOTE`, `NONE`), `ruleId`, `ruleVersion`, `legalStatus` (`Settled`, `Pending`), `legalSourceRef`; `action` is consistent with the three detail fields by definition (REQ-MKT-330). One schema for every caller (FZ-03). Pure, S, 20 ms, fail closed, no core default. Conformance vector `TCK-TAX-NET-REFUND`: `customerCredit = NONE` with `DISTANCE_WITHDRAWAL_VOID` or source `DistanceWithdrawal` is rejected in every pack.
- **Single-call rule (REQ-MKT-332).** The originating module calls `calculate` once per charge; every tax line carries `ruleId` and `ruleVersion` of the calculation and treatment; BIL and FIN validate credits by calling `treatment` and comparing. No module other than MKT holds a refundability, reduction or liability-point key (ARCH-11).
- **Errors:** VALIDATION (missing tax class; missing cancellation source for `CANCELLATION` or `VOID`), RULE_MISSING (no rate or treatment rule for date), NOT_APPLICABLE.
- **Callers:** `calculate` — RAT for POL-originated charges (`REQ-RAT-009`), BIL for BIL-originated fees flagged in the PFC tax base and for receipt-level stamps (`REQ-BIL-002`); `treatment` — RAT when producing tax deltas for credits, BIL and FIN to validate (`REQ-FIN-005`); RI optional `calculateReinsurancePremiumTax`.
- **Source:** PRD-17 §9.4.4 (line 1642); country content there.

### 5. `FiscalDocumentChannel`

- **Interface:** `IFiscalDocumentChannel` · **Mode:** A · **Binding axis:** LEGAL_ENTITY_HOME / BRANCH_LOCATION · **Idempotency:** K · **Timeout / fallback:** 30 s per attempt; queue on outage · **Core default:** `NotRequired`
- **Operations:** `build(fiscalSource) → document`, `submit(document, idempotencyKey) → {status: Registered | Rejected | NotRequired | Queued, registrationId, uid, rejections[]}`, `cancel(registrationId, reason)`, `series(documentType) → series rules`.
- **Errors:** VALIDATION (schema), UNAVAILABLE (queue), CONTRACT_VIOLATION.
- **Callers:** CMP (`REQ-CMP-001`); BIL and FIN consume results.
- **Source:** PRD-17 §9.4.5 (line 1654); country content there.

### 6. `EInvoiceProvider`

- **Interface:** `IEInvoiceProvider` · **Mode:** A · **Binding axis:** LEGAL_ENTITY_HOME · **Idempotency:** K · **Timeout / fallback:** 30 s; queue · **Core default:** `NotRequired` · **Origin:** (Must P1 interface, D3)
- **Operations:** `issue(invoice, recipient, idempotencyKey) → {status, providerRef}`, `status(providerRef)`.
- **Callers:** CMP. **Priority:** Must P1 as an interface with core default `NotRequired` (D3, REQ-MKT-089).
- **Source:** PRD-17 §9.4.6 (line 1662); country content there.

### 7. `BureauAdapter`

- **Interface:** `IBureauAdapter` · **Mode:** A · **Binding axis:** RISK_LOCATION · **Idempotency:** K · **Timeout / fallback:** 30 s; queue · **Core default:** `NotRequired`
- **Operations:** `report(policyEvent, idempotencyKey) → {status, bureauRef}`, `reconcile(period) → differences[]`.
- **Callers:** CMP (`REQ-CMP-002`).
- **Source:** PRD-17 §9.4.7 (line 1667); country content there.

### 8. `StatutoryClockSet`

- **Interface:** `IStatutoryClockSet` · **Mode:** S · **Binding axis:** CONTRACT_LAW_JURISDICTION or per clock · **Idempotency:** P · **Timeout / fallback:** 20 ms; EU default or fail closed · **Core default:** EU-layer defaults only
- **Operations:** `get(clockCode, jurisdiction, date)`, `list(jurisdiction, date)` returning StatutoryClockValue (§7.1).
- **Callers:** CMP (`REQ-CMP-003`).
- **Source:** PRD-17 §9.4.8 (line 1672); country content there.

### 9. `NumberingScheme`

- **Interface:** `INumberingScheme` · **Mode:** S · **Binding axis:** LEGAL_ENTITY_HOME · **Idempotency:** P · **Timeout / fallback:** 20 ms; fail closed · **Core default:** Prefix + padded sequence
- **Operations:** `format(identifierType, sequence, context) → string`, `validate(identifierType, value)`, `series(identifierType, context) → series id`.
- **Callers:** PLT numbering service (`REQ-PLT-014`).
- **Source:** PRD-17 §9.4.9 (line 1677); country content there.

### 10. `HolidayCalendarProvider`

- **Interface:** `IHolidayCalendarProvider` · **Mode:** Batch (yearly load) · **Binding axis:** SCHEME (country/region) · **Idempotency:** P · **Timeout / fallback:** n/a; previous year's rules flagged · **Core default:** Weekends only
- **Operations:** `holidays(country, region?, year) → [{date, type: PUBLIC | BANK | REGIONAL, name_en, name_local}]`.
- **Callers:** PLT calendar store (`REQ-PLT-009`).
- **Source:** PRD-17 §9.4.10 (line 1682); country content there.

### 11. `PaymentReferenceGenerator`

- **Interface:** `IPaymentReferenceGenerator` · **Mode:** S · **Binding axis:** LEGAL_ENTITY_HOME · **Idempotency:** P · **Timeout / fallback:** 20 ms; RF default · **Core default:** ISO 11649 RF
- **Operations:** `generate(billingAccount, purpose) → reference`, `validate(reference)`, `parse(reference) → billing account hint`.
- **Callers:** BIL (`REQ-BIL-004`).
- **Source:** PRD-17 §9.4.11 (line 1687); country content there.

### 12. `BankFileFormat`

- **Interface:** `IBankFileFormat` · **Mode:** S (file) · **Binding axis:** SCHEME (format) · **Idempotency:** P · **Timeout / fallback:** n/a · **Core default:** SEPA ISO 20022 pain.001/008, camt.053/054
- **Operations:** `writePayments(batch, format)`, `readStatement(file, format)`, `writeDirectDebits(batch, format)`.
- **Callers:** BIL (`REQ-BIL-004`, `REQ-BIL-009`).
- **Source:** PRD-17 §9.4.12 (line 1692); country content there.

### 13. `PayeeVerification`

- **Interface:** `IPayeeVerification` · **Mode:** S external · **Binding axis:** LEGAL_ENTITY_HOME · **Idempotency:** K · **Timeout / fallback:** 2 s; `NotAvailable` · **Core default:** `NotAvailable`
- **Operations:** `verify(iban, name, payeeType) → {result: Match | CloseMatch | NoMatch | NotAvailable, suggestedName?}`.
- **Callers:** BIL (`REQ-BIL-007`, `REQ-BIL-009`); CLM through BIL.
- **Source:** PRD-17 §9.4.13 (line 1697); country content there.

### 14. `RegimeCodeList`

- **Interface:** `IRegimeCodeList` · **Mode:** S · **Binding axis:** SCHEME (regime) · **Idempotency:** P · **Timeout / fallback:** 20 ms; fail closed · **Core default:** None (eu pack supplies SII)
- **Operations:** `list(regime, date|version)`, `validate(regime, code, date)`, `crosswalk(regime, code, fromVersion, toVersion)`.
- **Callers:** PFC (`REQ-PFC-005`), DAT (`REQ-DAT-003`), FIN.
- **Source:** PRD-17 §9.4.14 (line 1702); country content there.

### 15. `DocumentLanguageRule`

- **Interface:** `IDocumentLanguageRule` · **Mode:** S · **Binding axis:** CONTRACT_LAW_JURISDICTION · **Idempotency:** P · **Timeout / fallback:** 20 ms; entity default · **Core default:** Binding = entity default language
- **Operations:** `rules(documentType, jurisdiction, customerLanguage) → {binding, informative[], customerChoiceAllowed}`.
- **Callers:** DOC (`REQ-DOC-002`).
- **Source:** PRD-17 §9.4.15 (line 1707); country content there.

### 16. `MotorDataProvider`

- **Interface:** `IMotorDataProvider` · **Mode:** S external · **Binding axis:** RISK_LOCATION · **Idempotency:** K · **Timeout / fallback:** 3 s; `NotAvailable` · **Core default:** `NotAvailable`
- **Operations:** `vehicleByPlate(plate)`, `vehicleByVin(vin)`, `walletPrefill(consentToken)`, `valuation(vehicle)`; wallet consent (§9.4.43, REQ-MKT-316): `requestWalletConsent`, `consentResult` and `cancelWalletConsent(pendingRef) → {status: Cancelled}` (K), which CHN calls to cancel a pending consent when the user abandons the journey.
- **Callers:** POL (`REQ-POL-010`), CHN, UW, RAT.
- **Source:** PRD-17 §9.4.16 (line 1712); country content there.

### 17. `RegistryLookup`

- **Interface:** `IRegistryLookup` · **Mode:** S external · **Binding axis:** SCHEME · **Idempotency:** K · **Timeout / fallback:** 3 s; `NotAvailable` · **Core default:** `NotAvailable`
- **Operations:** `byTaxNumber(id)`, `byRegistryNumber(id)` → registered name, legal form, address, status, source, timestamp.
- **Callers:** PTY (`REQ-PTY-001`).
- **Source:** PRD-17 §9.4.17 (line 1717); country content there.

### 18. `IntermediaryRegister`

- **Interface:** `IIntermediaryRegister` · **Mode:** S external · **Binding axis:** LEGAL_ENTITY_HOME / host · **Idempotency:** K · **Timeout / fallback:** 3 s; snapshot · **Core default:** `UnknownRegister`
- **Operations:** `check(registerNumber, date) → {status, categories, source: LIVE | SNAPSHOT, snapshotDate}`.
- **Callers:** PTY (`REQ-PTY-008`).
- **Source:** PRD-17 §9.4.18 (line 1722); country content there.

### 19. `ComplaintRules`

- **Interface:** `IComplaintRules` · **Mode:** S · **Binding axis:** POLICYHOLDER_RESIDENCE (+home) · **Idempotency:** P · **Timeout / fallback:** 20 ms; fail closed · **Core default:** None
- **Operations:** `rules(jurisdiction, complainantType, channel, date) → {ackDeadline, responseDeadline, holdingResponseRule, adrBodies[], mandatoryTextRefs[]}`.
- **Callers:** CMP (`REQ-CMP-004`).
- **Source:** PRD-17 §9.4.19 (line 1727); country content there.

### 20. `RefusalDocumentRule`

- **Interface:** `IRefusalDocumentRule` · **Mode:** S · **Binding axis:** RISK_LOCATION · **Idempotency:** P · **Timeout / fallback:** 20 ms; `NotRequired` · **Core default:** `NotRequired`
- **Operations:** `evaluate(declineRecord) → {required, templateRef, deadline}`.
- **Callers:** UW (`REQ-UW-005`), DOC.
- **Source:** PRD-17 §9.4.20 (line 1732); country content there.

### 21. `SanctionsListSource`

- **Interface:** `ISanctionsListSource` · **Mode:** Batch fetch + S list resolution · **Binding axis:** LIST · **Idempotency:** K · **Timeout / fallback:** fetch retries; last good list with age warning · **Core default:** EU consolidated list
- **Operations:** `lists(entity, jurisdiction) → ordered list ids`; `fetch(listId) → list version` (batch, through the hub).
- **Callers:** PTY (`REQ-PTY-006`).
- **Source:** PRD-17 §9.4.21 (line 1737); country content there.

### 22. `ClaimsHistoryFormat`

- **Interface:** `IClaimsHistoryFormat` · **Mode:** S · **Binding axis:** CONTRACT_LAW_JURISDICTION · **Idempotency:** P · **Timeout / fallback:** 20 ms; core generic format · **Core default:** Generic field list
- **Operations:** `format(jurisdiction) → {fields[], templateRef, retentionYears}`.
- **Callers:** CLM (`REQ-CLM-008`), DOC.
- **Source:** PRD-17 §9.4.22 (line 1742); country content there.

### 23. `FriendlySettlementClearing`

- **Interface:** `IFriendlySettlementClearing` · **Mode:** A · **Binding axis:** RISK_LOCATION · **Idempotency:** K · **Timeout / fallback:** 30 s; queue · **Core default:** `NotApplicable`
- **Operations:** `evaluateEligibility(claimFacts)`, `submit(agreedClaim)`, `receiveNotification(message)`, `settlementStatement(period, counterparty?)`; `submitDispute(receivableRef, reason)` and `recordReply(disputeRef, decision)` are typed but not implemented in slice 4 (D-SL4-01). Capability key `cap.clm.friendly_settlement`. C# contract: `Market.Contracts/Spi/IFriendlySettlementClearing.cs` (SL4-CONTRACTS).
- **Callers:** CLM (`REQ-CLM-155`…`REQ-CLM-160`, `REQ-CLM-264`).
- **Source:** PRD-17 §9.4.23 (line 1747); country content there.

### 24. `PricingConstraint`

- **Interface:** `IPricingConstraint` · **Mode:** S · **Binding axis:** RISK_LOCATION · **Idempotency:** P · **Timeout / fallback:** 20 ms; EU defaults · **Core default:** EU protected-characteristics list · **Origin:** (R-05)
- **Operations:** `prohibitedFactors(jurisdiction, line, date)`, `check(ratingArtefactSummary) → violations[]`, `maxChange(jurisdiction, line) → limit or none`.
- **Callers:** RAT (`REQ-RAT-001`), PFC (`REQ-PFC-002`).
- **Source:** PRD-17 §9.4.24 (line 1752); country content there.

### 25. `ConsentRules`

- **Interface:** `IConsentRules` · **Mode:** S · **Binding axis:** POLICYHOLDER_RESIDENCE · **Idempotency:** P · **Timeout / fallback:** 20 ms; strictest (opt-in) · **Core default:** Opt-in for marketing on all channels · **Origin:** (R-05)
- **Operations:** `rules(jurisdiction, purpose, channel) → {model: OPT_IN | OPT_OUT | NOT_REQUIRED, lawfulBasisDefault, proofRetention}`.
- **Callers:** PTY (`REQ-PTY-005`), CHN, DOC.
- **Source:** PRD-17 §9.4.25 (line 1757); country content there.

### 26. `ESignatureProvider`

- **Interface:** `IESignatureProvider` · **Mode:** S selection, A signing · **Binding axis:** CONTRACT_LAW_JURISDICTION · **Idempotency:** K · **Timeout / fallback:** 30 s; next provider · **Core default:** Simple electronic signature with evidence · **Origin:** (R-05)
- **Operations:** `select(documentType, jurisdiction) → providers[] with level`, `sign(envelope)` (async).
- **Callers:** DOC (`REQ-DOC-006`).
- **Source:** PRD-17 §9.4.26 (line 1762); country content there.

### 27. `DigitalIdentityProvider`

- **Interface:** `IDigitalIdentityProvider` · **Mode:** S selection · **Binding axis:** POLICYHOLDER_RESIDENCE · **Idempotency:** K · **Timeout / fallback:** 5 s; none (manual) · **Core default:** None · **Origin:** (R-05)
- **Operations:** `providers(jurisdiction, journey) → list`.
- **Callers:** CHN (`REQ-CHN-008`), PLT (`REQ-PLT-015`).
- **Source:** PRD-17 §9.4.27 (line 1767); country content there.

### 28. `TaxReturnFormat`

- **Interface:** `ITaxReturnFormat` · **Mode:** Batch · **Binding axis:** RISK_LOCATION (host State) · **Idempotency:** P · **Timeout / fallback:** n/a · **Core default:** Generic CSV totals by class · **Origin:** (R-05)
- **Operations:** `generate(period, totalsByClass, entity) → {file, format, controlTotals}`.
- **Callers:** FIN (`REQ-FIN-005`); BIL reads `periodScheme` for levy aggregation (one source for levy periods).
- **Source:** PRD-17 §9.4.28 (line 1772); country content there.

### 29. `MandatoryWordingSet`

- **Interface:** `IMandatoryWordingSet` · **Mode:** S · **Binding axis:** CONTRACT_LAW_JURISDICTION · **Idempotency:** P · **Timeout / fallback:** 20 ms; empty set + warning · **Core default:** Empty set · **Origin:** (R-05)
- **Operations:** `clauses(documentType, productLine, jurisdiction, date) → clause codes and versions`.
- **Callers:** DOC (`REQ-DOC-002`, `REQ-DOC-003`), PFC.
- **Source:** PRD-17 §9.4.29 (line 1777); country content there.

### 30. `Geocoder`

- **Interface:** `IGeocoder` · **Mode:** S · **Binding axis:** RISK_LOCATION · **Idempotency:** K / P · **Timeout / fallback:** 2 s → `NotAvailable`; postcode table · **Core default:** `NotAvailable`; empty hazard keys · **Origin:** (R-17, R-26)
- **Operations:** `geocode(address) → {lat, lon, precision: ROOFTOP | STREET | POSTCODE | MUNICIPALITY, source, sourceVersion}`; `hazardKeys(location, schemes[]?, date) → [{schemeCode, schemeVersion, zone, score?, source}]`; `schemes(jurisdiction, date) → [{schemeCode, versions[], status: ACTIVE | PARALLEL | RETIRED}]`.
- **Inputs/outputs:** address in native or Latin form (`AddressFormatter` output); hazard keys always carry scheme code and version, so a rating artefact can pin a version while a newer one runs in parallel.
- **Mode, idempotency, timeout:** geocode S external through the integration hub, K on address digest, 2 s → result `NotAvailable` (caller continues with postcode-level fallback where the pack provides a postcode-to-zone table); hazardKeys S pure over pack hazard tables, 20 ms.
- **Errors:** VALIDATION (unparseable address), RULE_MISSING (no scheme for date), UNAVAILABLE, TIMEOUT.
- **Core default:** geocode `NotAvailable`; hazardKeys empty list.
- **Callers:** PTY (`REQ-PTY-012`), POL (`REQ-POL-010`), RAT (`REQ-RAT-001`), UW (`REQ-UW-008`).
- **Source:** PRD-17 §9.4.31 (line 1782); country content there.

### 31. `PolicyLifecycleRules`

- **Interface:** `IPolicyLifecycleRules` · **Mode:** S · **Binding axis:** CONTRACT_LAW_JURISDICTION · **Idempotency:** P · **Timeout / fallback:** 20 ms; fail closed · **Core default:** None · **Origin:** (R-36)
- **Operations:** `refundRule(kind: CANCELLATION | VOID, source: Policyholder | Insurer | NonPayment | DistanceWithdrawal | LongTermWithdrawal | Objection | Statutory, context) → {method: ProRata | ShortRate | Flat | MinimumRetained | FullRefund, deduction rules, refundClockCode, ruleId, legalSource}` (source and method codes from the shared code lists, contract R-84); `cancellationNotice(source, termLength, jurisdiction) → {noticePeriod, clockCode, deliveryRuleRef}`; `reinstatementGap(lapseDays, context) → {allowed, conditions}`; `suspensionEffects(context) → {coverEffect, premiumEffect, maxDuration}`.
- **Mode, idempotency, timeout:** S, pure, 20 ms; fail closed.
- **Errors:** VALIDATION, RULE_MISSING, NOT_APPLICABLE.
- **Core default:** none (fail closed); `reinstatementGap` default `allowed = false`.
- **Callers:** POL (`REQ-POL-004`, `REQ-POL-001`), BIL (`REQ-BIL-007`).
- **Source:** PRD-17 §9.4.32 (line 1793); country content there.

### 32. `MotorCompensationBodyAdapter`

- **Interface:** `IMotorCompensationBodyAdapter` · **Mode:** A · **Binding axis:** RISK_LOCATION · **Idempotency:** K · **Timeout / fallback:** 30 s; queue · **Core default:** `NotApplicable` · **Origin:** (R-41)
- **Operations:** `notify(claimRef, bodyType: GUARANTEE_FUND | GREEN_CARD_BUREAU | COMPENSATION_BODY, payload, idempotencyKey) → {status, bodyRef}`; `requestReimbursement(...)`; `receiveNotification(message) → typed event`; `settlementStatement(period)`.
- **Mode, idempotency, timeout:** A through the hub, K, 30 s per attempt, queue on outage.
- **Errors:** VALIDATION, UNAVAILABLE, CONTRACT_VIOLATION.
- **Core default:** `NotApplicable`.
- **Callers:** CLM (`REQ-CLM-003`).
- **Source:** PRD-17 §9.4.33 (line 1803); country content there.

### 33. `StatutoryDeliveryRule`

- **Interface:** `IStatutoryDeliveryRule` · **Mode:** S · **Binding axis:** CONTRACT_LAW_JURISDICTION · **Idempotency:** P · **Timeout / fallback:** 20 ms; fail closed for statutory · **Core default:** Durable medium with read evidence (non-statutory) · **Origin:** (R-41)
- **Operations:** `rules(documentType, jurisdiction, recipientType, date) → {acceptedMedia[], channels[], proofLevel, notificationDateRule (for example deemed delivery for unclaimed registered mail), evidenceItems[], durableMediumConsentRequired}`.
- **Mode, idempotency, timeout:** S, pure, 20 ms; fail closed for statutory notices.
- **Errors:** RULE_MISSING, NOT_APPLICABLE.
- **Core default:** none for documents flagged statutory; durable-medium delivery with read evidence for others.
- **Callers:** DOC (`REQ-DOC-005`), BIL (`REQ-BIL-006`), POL, CMP.
- **Source:** PRD-17 §9.4.34 (line 1813); country content there.

### 34. `PaymentChannelProvider`

- **Interface:** `IPaymentChannelProvider` · **Mode:** S · **Binding axis:** LEGAL_ENTITY_HOME · **Idempotency:** K · **Timeout / fallback:** 3 s; channel not offered · **Core default:** No extra channels · **Origin:** (R-41)
- **Operations:** `createCollection(invoice, channel) → {collectionRef, payload (for example QR content), expiry}`; `parseNotification(message) → {collectionRef, amount, payerRef, status}`; `capabilities(entity) → channels[]`.
- **Mode, idempotency, timeout:** createCollection S external, K, 3 s → channel not offered; parseNotification S pure.
- **Errors:** VALIDATION, NOT_APPLICABLE (channel unsupported), UNAVAILABLE.
- **Core default:** capabilities empty (bank transfer with `PaymentReferenceGenerator` only).
- **Callers:** BIL (`REQ-BIL-004`), CHN (`REQ-CHN-004`).
- **Source:** PRD-17 §9.4.35 (line 1823); country content there.

### 35. `InboundDocumentProfile`

- **Interface:** `IInboundDocumentProfile` · **Mode:** S · **Binding axis:** SCHEME · **Idempotency:** P · **Timeout / fallback:** 50 ms · **Core default:** ICAO 9303 MRZ + generic classes · **Origin:** (R-05)
- **Operations:** `classes(jurisdiction) → document classes`; `schema(documentClass) → extraction fields, validation, confidence thresholds`; `parse(documentClass, payload: MRZ | QR | barcode) → fields, checkResults`.
- **Mode, idempotency, timeout:** S, pure, 50 ms.
- **Errors:** VALIDATION (parse failure), NOT_APPLICABLE.
- **Core default:** ICAO 9303 MRZ parser and generic classes (identity document, invoice, letter).
- **Callers:** WRK (`REQ-WRK-005`).
- **Source:** PRD-17 §9.4.36 (line 1833); country content there.

### 36. `IdentityFederationProvider`

- **Interface:** `IIdentityFederationProvider` · **Mode:** S · **Binding axis:** POLICYHOLDER_RESIDENCE · **Idempotency:** P · **Timeout / fallback:** 20 ms · **Core default:** None · **Origin:** (R-05)
- **Operations:** `providers(jurisdiction, realm) → federation descriptors (protocol, issuer, assurance level, attribute mapping)`.
- **Mode:** S, configuration-like, 20 ms. **Errors:** NOT_APPLICABLE. **Core default:** none. **Callers:** PLT (`REQ-PLT-015`).
- **Source:** PRD-17 §9.4.37 (line 1843); country content there.

### 37. `IncidentReportingChannel`

- **Interface:** `IIncidentReportingChannel` · **Mode:** A · **Binding axis:** LEGAL_ENTITY_HOME · **Idempotency:** K · **Timeout / fallback:** 30 s; manual · **Core default:** `ManualSubmissionRequired` · **Origin:** (R-05)
- **Operations:** `submit(report, idempotencyKey) → {status: Submitted | ManualSubmissionRequired | Rejected, receipt}`; `format(reportType) → schema`.
- **Mode:** A, K, 30 s; fallback `ManualSubmissionRequired`. **Errors:** VALIDATION, UNAVAILABLE. **Core default:** `ManualSubmissionRequired`. **Callers:** PLT (`REQ-PLT-012`).
- **Source:** PRD-17 §9.4.38 (line 1849); country content there.

### 38. `FxRateSource`

- **Interface:** `IFxRateSource` · **Mode:** S · **Binding axis:** LEGAL_ENTITY_HOME · **Idempotency:** P · **Timeout / fallback:** n/a · **Core default:** Euro reference rates · **Origin:** (R-05)
- **Operations:** `source(jurisdiction, rateType) → {provider, publicationTime, currencies, fallbackSource}`.
- **Mode:** S, pure. **Errors:** RULE_MISSING. **Core default:** euro foreign exchange reference rates. **Callers:** PLT (`REQ-PLT-009`).
- **Source:** PRD-17 §9.4.39 (line 1855); country content there.

### 39. `LegacyDataProfile`

- **Interface:** `ILegacyDataProfile` · **Mode:** S / batch · **Binding axis:** SCHEME (country of legacy source) · **Idempotency:** P · **Timeout / fallback:** 50 ms per record; finding `UNPROFILED` · **Core default:** Generic UTF-8/NFC checks only · **Origin:** (R-71)
- **Operations:** `profilePlate(value) → {normalised, findings[] (LOOKALIKE_NORMALISED, INVALID_SERIES, FORMAT)}` (delegates normalisation to `IdValidator` scheme `VEHICLE_PLATE`, REQ-MKT-339, so runtime and migration agree); `detectEncoding(bytes, hints) → {encoding, text (NFC), confidence}`; `checkTransliteration(native, latin) → {consistent, expectedLatin, ruleSetId}`; `identifierHeuristics(scheme, value, partyType) → findings[]` (for example AFM of a legal person on a natural-person record); `rules(jurisdiction) → profiling rule catalogue with versions`.
- **Mode, idempotency, timeout:** S and batch, pure, 50 ms per record; fallback: record flagged `UNPROFILED` for manual remediation (never silently accepted).
- **Errors:** VALIDATION, NOT_APPLICABLE, RULE_MISSING.
- **Core default:** UTF-8 validity and NFC normalisation only.
- **Callers:** MIG (`REQ-MIG-046`).
- **Source:** PRD-17 §9.4.41 (line 1861); country content there.

### 40. `StatutoryDataReturnFormat`

- **Interface:** `IStatutoryDataReturnFormat` · **Mode:** Batch · **Binding axis:** LEGAL_ENTITY_HOME · **Idempotency:** P · **Timeout / fallback:** n/a; validation errors returned · **Core default:** `NotRequired` · **Origin:** (R-68, R-92)
- **Operations:** `returnTypes(entity, date) → [{returnType, taxonomyVersion, periodScheme, dueRuleClockCode}]`; `layout(returnType, taxonomyVersion) → {fields, codes (via `RegimeCodeList`), file format, encoding, control totals}`; `build(martRun) → {file, controlTotals, warnings}`; `validate(file) → {valid, errors[] (field, code, message key)}`; `filingChannel(returnType) → descriptor (portal upload, web service, file transfer; credentials by key-management reference)`.
- **Mode, idempotency, timeout:** batch, pure (P) over the mart run and pack layout; no external call (submission is CMP's, `REQ-CMP-008`).
- **Errors:** VALIDATION, RULE_MISSING (no layout for taxonomy version), NOT_APPLICABLE.
- **Core default:** `NotRequired` for every return type.
- **Callers:** DAT (`REQ-DAT-128`, under `REQ-DAT-003`) builds; CMP (`REQ-CMP-181`, under `REQ-CMP-008`) validates before regulatory submission and records the filing.
- **Source:** PRD-17 §9.4.42 (line 1871); country content there.

## Extensions by rulings (PRD-17 §9.4.43)

Country notes (GR / CY stub) are omitted here as pack content; see PRD-17 §9.4.43.

| SPI | Extension | Ruling |
|---|---|---|
| `TaxCalculator` (§9.4.4) | Optional `calculateReinsurancePremiumTax(riPremiumLines, jurisdiction, date)` for RI; default `NotApplicable` | R-49 |
| `TaxCalculator` (§9.4.4) | `treatment(request)` returning customer credit, authority liability, fiscal document, rule id and legal status per charge type; single-call rule for originating modules | D2 (contract §3.10.10), R-102 (CR-S3-24) |
| `IdValidator` (§9.4.1) | Scheme `VEHICLE_PLATE` with `normalise` and `searchKey` | PRD-18 XMR-D-204 |
| `MotorDataProvider` (§9.4.16) | `requestWalletConsent(subject, purpose, legalBasis: INSURER_DECISION \| INTERMEDIARY_DECISION, channel) → pendingRef` (A, K, 5 min expiry); `consentResult(pendingRef) → {status, data, legalBasis, channel}`; `cancelWalletConsent(pendingRef) → {status: Cancelled}` (K); owner names binding on CHN (R-87); Must P1 when `cap.chn.wallet_prefill` is on (REQ-MKT-316) | R-56 |
| `RefusalDocumentRule` (§9.4.20) | `responseDeadline(requestType, jurisdiction, date) → {duration, clockCode}`; `registerExport(afm, period) → file` | R-29 |
| `FriendlySettlementClearing` (§9.4.23) | `evaluateEligibility(claimFacts)`, `submitDispute(receivableRef, reason)`, `recordReply(disputeRef, decision)`, `receiveNotification(message)` (canonical names; the SPI owner’s names are binding per R-87, callers use `evaluateEligibility`) | R-42 |
| `PricingConstraint` (§9.4.24) | `proxyAttributes(jurisdiction, line) → [{attribute, justificationRequired, reason}]`; `fairnessDefaults(jurisdiction, line) → {maxRenewalIncreaseWithoutReferral, newVsRenewalParity, source}` | R-24 |
| `TaxReturnFormat` (§9.4.28) | Detail lines, adjustments, `validate(file)`, period scheme (GR IPT calendar quarters; levy two-month periods) and filing-channel descriptor | R-49 |

## SPIs considered and not added (PRD-17 §9.4.44)

| Candidate | Decision | Reason |
|---|---|---|
| `LevyCalculator` | Folded into `TaxCalculator` | Contract §3.5.8; levies differ only by category and base |
| `RegulatoryMapper` | Covered by `RegimeCodeList` + PFC assignment | CD-08 |
| `RiskLocationResolver` | Core rule at region:EU layer | EU law is uniform (Art. 13(13)); no country variation found |
| `CurrencyRounding` | Configuration keys, not an SPI | Pure data |
| `BusinessDayCalculator` | PLT service over pack holiday data | CD-17 |
