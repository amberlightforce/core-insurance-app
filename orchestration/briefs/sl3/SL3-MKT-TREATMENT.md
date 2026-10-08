# Brief SL3-MKT-TREATMENT — `TaxCalculator.treatment` with the GR and CY rules (S0)

**Runs on Sonnet 5.5 (model: sonnet), D-USR-16.** Batch S0. Review: **deep (money)**. Estimate: 2.5 h.

- **WP / PRD:** SL3-MKT-TREATMENT.
  - Requirements: REQ-MKT-087, REQ-MKT-330 (`treatment`), REQ-MKT-331 (GR defaults), REQ-MKT-332 (single-call rule,
    subset: every line carries ruleId and ruleVersion), REQ-POL-205 (cancellation-source code list held by MKT).
  - Sources: PRD-17 §9.4.4; digest `orchestration/digests/PRD-17.md` §7.5, §9a; PRD-18 XMR-F-210/-401, F-218.
  - Decisions: D-SL3-05, D-SL3-06, D-REG-01..07, D-REG-06a, D-SLC-09.
- **Scope:**
  1. Implement `ITaxCalculator.TreatmentAsync` (`src/CoreIns.Modules.Market.Contracts/Spi/ITaxCalculator.cs`):
     - pure, synchronous in effect, fail closed;
     - `RULE_MISSING` when no rule matches, with **no core default**;
     - `MKT-ERR-SPI-VALIDATION` for transaction kind `ENDORSEMENT` or a missing source on CANCELLATION/VOID.
  2. **GR rule rows (pack data in `CountryPacks.GR/Configuration`),** exactly as REQ-MKT-331. Each row has `ruleId`,
     `ruleVersion`, `legalStatus` **PendingOpinion** and a `legalSourceRef` (PRD-17 REQ-MKT-331; ΠΟΛ 1028/2017;
     Law 5317/2026 Art. 72):
     - IPT charge category, CANCELLATION (source Policyholder), ENDORSEMENT_CREDIT, RETURN_PREMIUM, REFUND →
       `KEEP_NOT_REDUCED`;
     - NEW_BUSINESS, ENDORSEMENT_DEBIT, FEE → `APPLY`;
     - DISTANCE_WITHDRAWAL_VOID → `REVERSE_AS_VOID`, with the routing key `tax.treatment.withdrawal_void_routing`
       registered but not used by the slice.
     - Levy and stamp categories: **no rows**, so the result is `RULE_MISSING` (D-SL3-06).
     - Sources other than Policyholder: no rows, so the result is `RULE_MISSING`.
  3. Every result carries `provisional = (legalStatus != Settled)`. In Production, a non-Settled row is refused by the
     existing production gate (D-SLC-09); prove it with a test.
  4. **The forbidden combination** (`customerCredit = NONE` with DISTANCE_WITHDRAWAL_VOID or source DistanceWithdrawal)
     is rejected when a pack loads. Add conformance vector `TCK-TAX-NET-REFUND`.
  5. **CY stub:** `REDUCE_PRO_RATA` for every credit, synthetic, marked as such.
  6. **Cancellation-source code list** (Policyholder, Insurer, NonPayment, DistanceWithdrawal, LongTermWithdrawal,
     Objection, Statutory) as an MKT code list, resolvable by other modules through the existing configuration
     resolver.
  7. **ARCH-11 guard** (architecture test): no module other than MKT registers a key starting `tax.treatment.` or reads
     one.
- **Depends on / provides:**
  - **Depends on:** contracts only (SL3-CONTRACTS may touch `ITaxCalculator` additively; merge main when it lands).
    Start at once.
  - **Provides:** what RAT, POL, BIL and FIN call.
- **Files you own:**
  - `src/CoreIns.Modules.Market/Services/Tax*`, `Domain/TaxTreatment*`;
  - `src/CoreIns.CountryPacks.GR/Configuration/**` (treatment rows only), `src/CoreIns.CountryPacks.CY/**` (treatment
    rows);
  - `tests/CoreIns.IntegrationTests/Market/Treatment/**`, `tests/CoreIns.CountryPacks.Tests` (new classes);
  - the MKT permission file (append).
  - **Do not touch** `CountryPacks.GR/Fiscal/**`: SL3-CMP-CREDIT owns it.
- **Never invent:** no levy refundability, no stamp rate, no IPT reduction for a source the PRDs do not give. A missing
  value stays missing.
- **PITFALLS to self-check (likely):** 10 (fail closed, `RULE_MISSING`, never a default), 11, 24 (the PRD outranks this
  brief; if REQ-MKT-331 reads differently, follow it and report).
- **Workflow:** `briefs/sl3/_COMMON.md`: merge main, quick gate, push,
  `gh pr create --base main --title "SL3-MKT-TREATMENT: TaxCalculator.treatment GR/CY rules"`, do not merge.
