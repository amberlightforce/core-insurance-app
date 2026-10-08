# Brief SL3-CMP-CREDIT — fiscal credit documents through the stub channel (S1)

**Runs on Sonnet 5.5 (model: sonnet), D-USR-16.** Batch S1. Review: light, plus the money checklist. Estimate: 2 h.

- **WP / PRD:** SL3-CMP-CREDIT.
  - Requirements: REQ-CMP-001, -030, -032 (subset: credit documents correlated to the original), BR-CMP-004
    (idempotency key = source type + source id + role + revision), REQ-CMP-038 (CMP sole issuer), REQ-BIL-097 (credits
    get credit-type requests).
  - E2E-03 step 6. Sources: PRD-11 §9.1 (myDATA document types are market guidance, OI-CMP-03); digest
    `orchestration/digests/PRD-11.md` §9.
  - Decisions: **D-SL3-07** (placeholder codes `UNMAPPED-OQ-012` / `STUB-`; 11.4 and 5.1/5.2 recorded only as
    candidates), D-SL2-05 (stub never in Production), D-SLC-19.
- **Scope:**
  1. `cmp.FiscalDocument.request` accepts role `CREDIT` with `correlatedDocumentId`.
     - Refuse a CREDIT whose correlated document does not exist or is not Registered (`CMP-ERR-VALIDATION`).
     - Refuse a CREDIT for a source whose correlated document is a credit itself.
  2. **Idempotency:** the key is source type + source id + role + revision. A replay returns the same document; one
     credit per source revision.
  3. **The GR stub channel** (`CountryPacks.GR/Fiscal/MyDataStubFiscalChannel.cs`):
     - registers credits synchronously with document type `UNMAPPED-OQ-012`, series and number from CMP's own credit
       series (CMP is the sole issuer), MARK `STUB-…`, and the correlation stored;
     - never registered in Production (existing guard; add a test for the credit path).
  4. `cmp.FiscalDocument.get/listBySource` show role and correlation.
- **Depends on / provides:**
  - **Depends on:** contracts only. Start when SL3-CONTRACTS merges.
  - **Provides:** what SL3-BIL-CREDIT calls.
- **Files you own:** `src/CoreIns.Modules.Compliance/**` (CMP migration owner if a column is needed),
  `src/CoreIns.CountryPacks.GR/Fiscal/**`, `tests/CoreIns.IntegrationTests/Compliance/**` (create the folder), the CMP
  permission file (append).
- **Never invent:** no real myDATA codes. 11.4 and 5.1 stay out of the code, mentioned only in a comment citing PRD-11
  BR-CMP-001 and OI-CMP-03.
- **Tests:**
  - a credit for a registered original → Registered with a placeholder type and correlation;
  - a replay → the same document;
  - a second revision → a new document;
  - no original → refused;
  - a credit of a credit → refused;
  - Production → the stub is not bound.
- **PITFALLS to self-check (likely):** 9 (the idempotency key really fires: test it), 10, 12.
- **Workflow:** `briefs/sl3/_COMMON.md`: merge main, quick gate, push,
  `gh pr create --base main --title "SL3-CMP-CREDIT: fiscal credit documents (stub)"`, do not merge.
