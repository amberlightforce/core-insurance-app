# Brief SL3-PFC-MOTOR11 — product version MOTOR-GR 1.1 for servicing (S0)

**Runs on Sonnet 5.5 (model: sonnet), D-USR-16.** Batch S0. Review: light. Estimate: 1.5 h.

- **WP / PRD:** SL3-PFC-MOTOR11.
  - Requirements: REQ-PFC-010 (version references refund method and day count), REQ-PFC-066 (subset: mid-term change
    permissions), REQ-PFC-116 (charge-type cancellation treatment), REQ-PFC-134 (subset: refund method per source),
    REQ-PFC-135 (day count).
  - Sources: PRD-02 §5 CAP-PFC-06; digest `orchestration/digests/PRD-02.md`; PRD-03 §16 item 4 (TERM_RATIO
    recommended for Greek motor, "confirm with finance").
  - Decisions: D-SL3-04, D-SL3-08, D-SLC-04.
- **Scope:**
  1. Publish **MOTOR-GR 1.1** as a new write-once version (`src/CoreIns.Modules.Product/Seed/`). **Never edit the 1.0
     seed.**
     - Effective from 2026-01-01 for new business and renewal, so resolution by date picks 1.1, while terms already
       pinned to 1.0 keep 1.0.
     - Otherwise identical to 1.0, with the changes in items 2–5.
  2. `dayCount`: **TERM_RATIO**, carrying a `provisional` note: "PRD-03 recommends TERM_RATIO for Greek motor; confirm
     with finance". If the schema has no place for the note, add an additive `notes`/`status` field and report it.
  3. `refundMethods` per cancellation source:
     - `Policyholder: ProRata`, marked **illustrative**;
     - `DistanceWithdrawal: FullRefund`, legalStatus **Settled**, source Law 5317/2026 Art. 72 (PRD-05 REQ-POL-207,
       PRD-17 REQ-MKT-309);
     - every other source **absent**, so POL refuses it (fail closed).
     - The REQ-PFC-134 lint (all seven sources) is not built; say so in the report.
  4. Mid-term change permissions: vehicle field edits (value, use and the other rating fields) and vehicle replacement
     are permitted; MTPL stays non-removable (REQ-PFC-088).
  5. Renewal window as in 1.0.
  6. Charge types keep `cancellationTreatment` (`FOLLOW_PRODUCT_REFUND` for premium; `PACK_TAX_TREATMENT` for tax and
     levy). Check that the artefact validation refuses a refund method on a tax or levy charge type (REQ-PFC-116 GWT).
  7. Rating artefact: the same illustrative tariff, referenced by hash as in 1.0. Do not change rates.
- **Depends on / provides:**
  - **Depends on:** nothing.
  - **Provides:** the artefact that POL CHANGE/CANCEL/RENEW and RAT read. Existing E2E-01/02 must still pass, with new
    policies now binding on 1.1. Check that their asserted premium (430.00 + 64.51) is unchanged.
- **Files you own:** `src/CoreIns.Modules.Product/Seed/motor-gr-1.1.product.json` (new), artefact validation
  additions, `tests/CoreIns.IntegrationTests/Product/Motor11/**`.
- **Tests:**
  - resolve on 2026-10-08 → 1.1;
  - a term pinned to 1.0 resolves its own artefact by hash;
  - TERM_RATIO is declared;
  - the refund method for Policyholder is ProRata (illustrative), and an unknown source → absent;
  - a refund method on IPT → validation error;
  - E2E-01 premium unchanged.
- **PITFALLS to self-check (likely):** 10 (absent stays absent), 24 (the PRD outranks this brief).
- **Workflow:** `briefs/sl3/_COMMON.md`: merge main, quick gate, push,
  `gh pr create --base main --title "SL3-PFC-MOTOR11: MOTOR-GR 1.1 (TERM_RATIO, refund methods, change permissions)"`,
  do not merge.
