# Brief SL3-FIN-RULES — posting rule set v3 for credits and refunds, tax-treatment check (S1)

**Runs on Sonnet 5.5 (model: sonnet), D-USR-16.** Batch S1. Review: **deep (ledger)**. Estimate: 3 h.

- **WP / PRD:** SL3-FIN-RULES.
  - Requirements:
    - REQ-FIN-036 (post only from BIL `BillingEntryPosted`; POL/BIL business events are context only);
    - REQ-FIN-182 (validate tax/levy payable movements on credits against `TaxCalculator.treatment`; suspend
      `TAX_RULE_VIOLATION`);
    - REQ-FIN-183 (endorsement credits by the same treatment, `ENDORSEMENT_CREDIT`);
    - REQ-FIN-297 (NET mode);
    - REQ-FIN-001 (unmatched → suspended NO_RULE, never a default account);
    - GF-04 subset: pro-rata premium credit, IPT per the GR treatment (NOT_REDUCE), no commission chargeback, no DAC.
  - E2E-03 step 9. Sources: PRD-09 §4.11 table (credit delta: Dr GL-2110 LRC / Cr GL-1210 receivable; IPT payable
    unchanged), §9.1 (IPT on cancellation); digest `orchestration/digests/PRD-09.md`.
  - Decisions: D-SLC-12, D-SL2-08, D-SL2-12, D-SL3-05, D-SL3-06, D-ARC-34.
- **Scope:**
  1. **Rule set v3** (`Seed/gr-test.finance.v3.json`, effective-dated after v2; v2 untouched). Rules:
     - for BIL `CREDIT_WRITTEN` and `CREDIT_BILLED` (premium category): mirror the WRITTEN/BILLED rules with the sides
       reversed;
     - for `REFUND_APPROVED`: credit balance → refund payable;
     - for the disbursement entry types with source `BIL_REFUND`: refund payable → disbursements in transit (GL-2530)
       → cash (GL-1110).
     - Accounts are the existing PRD-09 illustrative codes. If a refund-payable account is needed and PRD-09 §4 has
       none, use the BIL LA mapping and mark it *Technical placeholder*, as v2 did. Report it; never invent a GL code
       silently.
     - Rules match on entry type × charge category × source; specificity and compile checks as v2.
  2. **Tax-treatment check (REQ-FIN-182/183).** For any servicing entry line that touches GL-2410 IPT payable (or
     levy GL-2420), call `TaxCalculator.treatment` with the line's `transactionKind`, `cancellationSource` and charge
     type. Compare with the line:
     - a debit (reduction) where the treatment says NOT_REDUCE → **suspend** the whole event, reason
       `TAX_RULE_VIOLATION`, no journal;
     - a credit kept where it says REDUCE → suspend likewise;
     - `RULE_MISSING` → suspend;
     - missing `transactionKind` on a servicing entry → suspend (PITFALLS 10).
  3. **Invariants:** every journal balanced and sealed (existing); per refund, the refund clearing nets to zero once
     the BIL disbursement entries post (as GL-2510 per claim payment in D-SL2-08).
- **Depends on / provides:**
  - **Depends on:** contracts only (the BIL entry types and dimensions from SL3-CONTRACTS). Build the fixtures from the
    generated samples; run against real BIL once SL3-BIL-CREDIT and SL3-BIL-REFUND merge (SL3-E2E proves the full
    chain).
  - **Provides:** the journals the E2E asserts.
- **Files you own:** `src/CoreIns.Modules.Finance/Seed/gr-test.finance.v3.json`, `Posting/**`,
  `Domain/PostingRules.cs`, `tests/CoreIns.IntegrationTests/Finance/Servicing/**`. No FIN migration is expected; if you
  need one, you are the FIN migration owner.
- **Tests:**
  - a cancellation credit of −288.63 premium with an IPT line 0.00 → journals Dr GL-2110 288.63 / Cr GL-1210 288.63,
    GL-2410 untouched;
  - a forged entry reducing GL-2410 by 43.29 on source Policyholder → suspended `TAX_RULE_VIOLATION`, no journal;
  - an endorsement debit → written journals as new business;
  - refund approved → released → cleared → the refund clearing nets to 0 and cash is credited 288.63;
  - an unknown entry type → NO_RULE;
  - a duplicate event → one journal;
  - an append to a posted journal as the app role → refused (existing seal; regression).
- **PITFALLS to self-check (likely):** 8, 10, 11, 12.
- **Workflow:** `briefs/sl3/_COMMON.md`: merge main, quick gate, push,
  `gh pr create --base main --title "SL3-FIN-RULES: rule set v3 for credits/refunds + tax-treatment check"`, do not
  merge.
