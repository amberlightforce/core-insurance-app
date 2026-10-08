# Brief SL3-BIL-CREDIT — credits, credit notes, mid-term and renewal billing (S1)

**Runs on Sonnet 5.5 (model: sonnet), D-USR-16.** Batch S1. Review: **deep (money/ledger)**. Estimate: 4 h.

- **WP / PRD:** SL3-BIL-CREDIT.
  - Requirements:
    - REQ-BIL-073 (credits against unbilled items first, then billed credit items, net credit left on the account);
    - REQ-BIL-074 (cancellation credit billed at once; planned items of the cancelled term stopped from the effective
      date);
    - REQ-BIL-076 (deltas on billed periods: new items, never edit billed invoices);
    - REQ-BIL-079 (validate every tax/levy delta against `TaxCalculator.treatment`; quarantine on mismatch);
    - REQ-BIL-089, -091 (credit note referencing the original invoice, gapless series);
    - REQ-BIL-096, -097 (fiscal request; credits get role CREDIT);
    - REQ-BIL-319, INV-05 (no cancellation-sourced debit to IPT payable beyond the treatment);
    - REQ-BIL-002 (subset: the renewal term invoice).
  - E2E-03 steps 5–6, E2E-04 step 9. Sources: PRD-06 §5.4/§5.5, §7.3; digest `orchestration/digests/PRD-06.md` §3, §5,
    §9.
  - Decisions: D-SL3-05, D-SL3-06, D-SL3-07, D-SLC-12, D-SLC-19, D-ARC-34.
- **Scope:**
  1. **Intake of negative and servicing deltas.** Use the existing `ChargeDeltaEmitted` intake (idempotent on charge
     id; complete set by set fields). It now reads `transactionKind`, `cancellationSource` and the treatment fields.
     - For each tax delta, call `TaxCalculator.treatment` and compare the action and rule id.
     - A mismatch or `RULE_MISSING` → quarantine the set with a reason; nothing is billed (fail closed, PITFALLS 10).
  2. **Credits** (ANNUAL plan):
     - a negative premium delta first reduces unbilled items of the same term and charge type;
     - otherwise it becomes a billed credit item on a **CREDIT_NOTE** (new kind) that references the original invoice
       (`originalInvoiceId`), numbered from its own gapless series (technical prefix `CN`, D-SLC-08 pattern);
     - `PolicyCancelled` stops the planned items of the term from the effective date.
     - **Allocation of a credit note:**
       - against the open balance of the original invoice if it is unpaid;
       - otherwise left as account credit, for SL3-BIL-REFUND to propose a refund from.
  3. **Debits** (positive change deltas): an immediate invoice for the additional premium plus `APPLY` IPT, as for new
     business (the existing path with kind INVOICE).
  4. **`RenewalBound`:** attach term n+1 to the same billing account (payer from the account) and invoice it like a new
     bind.
  5. **Fiscal:** request a CMP fiscal document for each credit note with role CREDIT and `correlatedDocumentId` = the
     original's fiscal document (contract from SL3-CONTRACTS; the CMP behaviour is SL3-CMP-CREDIT).
  6. **Ledger:**
     - new entry types `CREDIT_WRITTEN` and `CREDIT_BILLED` (LA chart, PRD-06 §3.5): reversal of written and billed
       receivable for premium;
     - **no IPT payable movement** where the treatment is NOT_REDUCE;
     - every entry sealed at creation (D-ARC-34);
     - `BillingEntryPosted` with the line dimensions `transactionKind`, `cancellationSource` and `treatmentRuleId`
       **always set** on servicing entries;
     - an invariant test that the IPT payable LA account carries no cancellation-sourced debit (REQ-BIL-319).
  7. **BIL migration:** you are the BIL migration owner in S1 (invoice kind, `original_invoice_id`, credit-note series,
     item state Cancelled). SL3-BIL-REFUND adds its own migration in S2, after you merge.
- **Depends on / provides:**
  - **Depends on:** contracts only (POL events, CMP request, MKT treatment fakes). Run the integration tests against
    the real POL/MKT once they merge.
  - **Provides:** credit balances and credit notes for SL3-BIL-REFUND; entries for SL3-FIN-RULES.
- **Files you own:** `src/CoreIns.Modules.Billing/Commands/Credits*`, `Services/TermBilling.cs`,
  `Events/IntakeHandlers.cs`, `Domain/Ledger.cs` (new rules), `Persistence/**` (migration owner),
  `tests/CoreIns.IntegrationTests/Billing/Credits/**`.
- **Shared (append only):** the BIL permission file.
- **Do not touch:** `Commands/Disbursements.cs` or `PayeeAccounts.cs` (SL3-BIL-REFUND's area in S2).
- **Tests:**
  - **E2E-03 money:** a paid invoice 494.51, a cancellation credit −288.63 with IPT 0.00 `KEEP_NOT_REDUCED` → a
    credit note of 288.63 referencing invoice 1; account credit 288.63; IPT payable unchanged; one fiscal CREDIT
    request; entries balanced and sealed (a test as the app role).
  - An unpaid invoice plus a cancellation → the credit note offsets the open balance and leaves earned premium + IPT
    open.
  - A mid-term debit → a new invoice.
  - `RenewalBound` → a term-2 invoice.
  - A tax delta that disagrees with the treatment → quarantined, nothing billed.
  - Duplicate delivery of events → one credit note.
- **PITFALLS to self-check (likely):** 8, 9 (the credit-note duplicate key is the business reference: source
  transaction + original invoice), 10, 11, 12.
- **Workflow:** `briefs/sl3/_COMMON.md`: merge main, quick gate, push,
  `gh pr create --base main --title "SL3-BIL-CREDIT: credits, credit notes, mid-term and renewal billing"`, do not
  merge.
