# Brief SL3-RAT-PRORATE — proration service, ENDORSEMENT/RENEWAL modes, tax lines on credits (S0)

**Runs on Sonnet 5.5 (model: sonnet), D-USR-16.** Batch S0. Review: **deep (money)**. Estimate: 3 h.

- **WP / PRD:** SL3-RAT-PRORATE.
  - Requirements: REQ-RAT-004, -155, -156 (TERM_RATIO and ACT/365F only), -165 (refuse an undeclared convention,
    `RAT-ERR-CONVENTION`); REQ-RAT-009 (subset: tax lines after premium through `TaxCalculator`); REQ-POL-093
    (ENDORSEMENT mode under the term-pinned artefact); REQ-POL-124 (taxes only through RAT, including the credit
    treatment); REQ-POL-249 (subset: RENEWAL mode).
  - Sources: PRD-03 §5.7 (CAP-RAT-07); digest `orchestration/digests/PRD-03.md`; PRD-17 §7.5.
  - Decisions: D-SL3-04, D-SL3-05, D-SLC-11.
- **Scope:**
  1. `rat.Proration.prorate`:
     - **in:** annual rates per element × charge type × segment, the term period, the convention and the configuration
       hash;
     - **out:** amount per line with days, fraction and rounding residual;
     - days are whole Europe/Athens calendar dates (D-SL3-04);
     - rounding through MKT's rule for premium, explicit;
     - the exact reversal of a prorated amount is its exact negative;
     - refuse a convention the product artefact does not declare (`RAT-ERR-CONVENTION`).
  2. `rat.Rate.rate` modes:
     - **ENDORSEMENT:** rate under `pinnedRatingArtefactHash` (the term's artefact), never the currently active one. A
       mismatch or unknown hash → `RAT-ERR-INPUT`.
     - **RENEWAL:** rate under the artefact active at the new term start, as given by the caller's resolution.
     - The existing NEW_BUSINESS behaviour and the E2E-01 amounts stay unchanged.
  3. **Tax lines for servicing deltas.** For a list of premium deltas with a `transactionKind` (and `cancellationSource`
     where relevant), compute the IPT line per delta:
     - `APPLY` → the IPT rate on the delta;
     - `KEEP_NOT_REDUCED` → an IPT line of 0.00 carrying the treatment;
     - `RULE_MISSING` → fail the whole request (no partial output).
     - Every line carries `ruleId`/`ruleVersion` of both the calculation and the treatment, plus `legalStatus` and
       `provisional` (weakest status wins, as SL-RAT-UW's fix did).
     - Expose this as an in-process service that POL calls (the `ITaxLinesForDeltas` port of SL3-POL-ENGINE): through
       the generated RAT contract if typed, otherwise as an additive typed op of your own module.
- **Depends on / provides:**
  - **Depends on:** contracts only. Code `TaxCalculator.treatment` against the generated fake until SL3-MKT-TREATMENT
    merges, then run your integration test against the real one.
  - **Provides:** proration and tax lines for POL CHANGE/CANCEL/RENEW.
- **Files you own:** `src/CoreIns.Modules.Rating/Services/Proration*`, `Services/Modes*`, `Services/ServicingTax*`,
  `Api/Proration*`, `tests/CoreIns.IntegrationTests/Rating/Proration/**`, the RAT permission file (append).
- **Tests:**
  - PRD GWTs: 365.0000 TERM_RATIO, 100-day segment → 100.00, reversal −100.00; 120 + 245 days; a leap-year full term
    TERM_RATIO = rate, ACT/365F = 366/365 × rate; a 182-day half-year (REQ-RAT-276 golden) → 182.50;
  - the undeclared convention is refused;
  - ENDORSEMENT under an old pinned artefact after a newer one is active;
  - the tax lines: debit +70.00 → IPT per rate with `APPLY`; credit −288.63 → IPT 0.00 `KEEP_NOT_REDUCED`
    provisional; a levy line → `RULE_MISSING` fails.
- **PITFALLS to self-check (likely):** 10, 11, 12 (every field POL relies on is set), 14.
- **Workflow:** `briefs/sl3/_COMMON.md`: merge main, quick gate, push,
  `gh pr create --base main --title "SL3-RAT-PRORATE: proration, ENDORSEMENT/RENEWAL modes, servicing tax lines"`, do
  not merge.
