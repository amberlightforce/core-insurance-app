# Brief SL4-CMP-RECEIPT — stub fiscal settlement receipt for claim payments (wave 2)

Runs on Sonnet 5.5 (model: sonnet), D-USR-16.

Wave 2, **batched** with SL4-MKT-FS. Review: light, plus a money checklist. Estimate: 1.5 h.

**Gate:** start only after **SL3-CMP-CREDIT has merged**. It owns `Modules.Compliance/**` and the GR stub fiscal
channel in slice 3.

- **WP / PRD:**
  - REQ-CMP-001 and REQ-CMP-030 (subset: one fiscal document per idempotency key);
  - R-43 (fiscal documents sourced from claim payments; CCR-CLM-05);
  - REQ-CLM-138 (the CMP side only);
  - BR-CMP-004 (idempotency key).

  Sources: `orchestration/digests/PRD-11.md` and `orchestration/digests/PRD-07.md` §9.1 (myDATA settlement receipt:
  B2C 13.1/13.2 with classification 2.3/2.5 or 2.95, B2B self-billing 2.1; "market guidance, not a decision";
  OI-CLM-04). Decisions: **D-SL4-11**, D-SL3-07 (the same stub pattern), D-SL2-05.
- **Scope:**
  1. Accept `cmp.FiscalDocument.request` with `sourceType = CLM_CLAIM_PAYMENT`, role `ISSUE` and category
     `CLAIM_SETTLEMENT_RECEIPT`. Allow the source in the source/role matrix. Refuse a `CREDIT` or `CANCELLATION` role
     for this source in the slice (no correction flow yet).
  2. **Idempotency key** = source type + source id (the claim payment id) + role + revision. A second request with
     the same key returns the same document; a different payload with the same key → `CMP-ERR-IDEMPOTENCY-MISMATCH`.
  3. **GR stub channel:**
     - maps the category to the placeholder codes `UNMAPPED-OQ-012` (document type and classification), and returns a
       `STUB-` MARK;
     - records PRD-07's candidate types (13.1/13.2/2.1) **only** in a comment and the document's `legalSourceRef`,
       never as codes (status Verify);
     - `FiscalDocRegistered` is published as for invoices.
  4. **CY:** `NotRequired`, with no document number consumed.
  5. A payment with method `CLEARING` must never get a receipt. Refuse `CLM_CLAIM_PAYMENT` requests whose lines carry
     a `CLEARING` marker (`CMP-ERR-VALIDATION`). CLM also never asks (SL4-CLM-PAYOPS); this is the second fence.
- **Depends on / provides:**
  - **Depends on:** contracts (category documented), and the merged SL3-CMP-CREDIT.
  - **Provides:** what SL4-CLM-PAYOPS calls on `PaymentIssued`.
- **Files you own:**
  - the CMP source/category mapping files under `src/CoreIns.Modules.Compliance/**` that you change (list them in the
    report);
  - `src/CoreIns.CountryPacks.GR/Fiscal/MyDataStubFiscalChannel.cs` (mapping only);
  - `tests/CoreIns.IntegrationTests/Compliance/ClaimReceipt*`.
- **Tests:**
  - one document per payment;
  - replay → same document;
  - mismatch → 409;
  - codes `UNMAPPED-OQ-012`;
  - CY `NotRequired`;
  - `CLEARING` refused;
  - no personal data in `FiscalDocRegistered`;
  - the stub is not bound in Production (existing startup test still green).
- **PITFALLS to self-check (likely):** 9, 10, 12, 18, 36.
- **Workflow:** follow `briefs/sl4/_COMMON.md` (batched). Merge main, run the quick gate, push your branch and report
  it. Do not open a PR.
