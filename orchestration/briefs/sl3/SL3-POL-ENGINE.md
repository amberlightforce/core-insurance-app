# Brief SL3-POL-ENGINE — pure servicing engine: segments and NET charge deltas (S0)

**Runs on Sonnet 5.5 (model: sonnet), D-USR-16.** Batch S0. Review: **deep (money)**, adversarial, with property
probes. Estimate: 3.5 h.

- **WP / PRD:** SL3-POL-ENGINE. Requirements:
  - REQ-POL-115 (annual rate × days ÷ basis), -116 (flat charges), -119 (deltas per element × charge type × period,
    never netted across), -121 (NET mode), -122 (Σ deltas = cumulative written per charge type), -123 (rounding and
    residuals), -214 (cancellation deltas), -118 (earned + unearned = written, as an invariant only);
  - REQ-RAT-004 (one shared proration).
  - PRD-05 §5.4 notes, §7.1 ChargeDelta; PRD-03 §5.7 (CAP-RAT-07). Decisions D-SL3-04 (day count, TERM_RATIO,
    rounding, residuals) and D-SL3-02 (in-sequence only).
- **Scope:** a **pure** library in `src/CoreIns.Modules.Policy/Domain/Servicing/`. No DB, no DI registration, no I/O.
  - **Input:**
    - the term (period, day-count convention, currency);
    - the current segments as of the head (valid period plus annual rates per element locator × charge type × coverage,
      with charge category and flat/proratable flags);
    - an intent, one of:
      - `Change(effectiveAt, newAnnualRates for the remainder)`;
      - `EndCover(effectiveAt, refundMethod)` with ProRata or FullRefund. Flat cancel = effectiveAt at term start.
        Anything else → typed refusal;
      - `NewTerm(period, annualRates)` for renewal.
  - **Output:** the new segment list, and NET deltas per element × charge type × valid period, each with amount (4
    decimal places, rounded per the MKT rule given as a delegate), days, fraction, the `transactionKind` (the MKT enum:
    ENDORSEMENT_DEBIT/ENDORSEMENT_CREDIT/CANCELLATION/NEW_BUSINESS) and the correlation and set fields.
  - **Day count:** whole Europe/Athens calendar dates, half-open, `days = AthensDate(to) − AthensDate(from)`.
  - **Conventions:** TERM_RATIO (term days 365/366) and ACT/365F, through an `IProration` port that SL3-RAT-PRORATE
    implements. Ship an in-library reference implementation for tests only, and have a test prove it agrees with the
    RAT contract sample.
  - **In-sequence guard:** an intent whose effectiveAt is earlier than the latest segment boundary created by a bound
    transaction → typed `OutOfSequence` result (D-SL3-02).
  - **Tax lines are not computed here.** Premium deltas go out; SL3-POL-CHANGE/-CANCEL call RAT for tax lines on them.
    Define a small port `ITaxLinesForDeltas` that those WPs implement.
  - **Exactness rules:**
    - a flat or same-day `EndCover` credits **exactly** −written per element × charge type;
    - a `Change` with the same rates produces zero deltas;
    - residuals stay with the elapsed part;
    - flat charge types are never prorated: they get no cancellation credit unless the charge type says so.
- **Depends on / provides:**
  - **Depends on:** contracts only (`rat.Proration.prorate` shape from SL3-CONTRACTS). Start at once and use your own
    records until CONTRACTS lands.
  - **Provides:** the engine that SL3-POL-CHANGE, -CANCEL and -RENEW call.
- **Tests** (`tests/CoreIns.IntegrationTests/Policy/Servicing/`, pure, no Testcontainers):
  - The worked cases: 430.00 annual, day 120 of 365 → credit −288.63, earned 141.37; flat cancel → −430.00; a change at
    day 200 from 430.00 to 500.00 → +31.64 (70 × 165/365 = 31.6438…, rounded per the MKT rule; assert the rule); a
    leap-year term (366 days).
  - A DST-day effective time (last Sunday of March/October, Athens).
  - Property tests (hand-rolled generators are fine): for random sequences of in-sequence changes and an optional final
    cancellation,
    - Σ deltas per element × charge type = Σ current segment amounts (P7);
    - every delta's period is inside the term;
    - no delta nets two charge types;
    - reversing the sequence's amounts gives exactly 0.
- **Files you own:** `src/CoreIns.Modules.Policy/Domain/Servicing/**` and
  `tests/CoreIns.IntegrationTests/Policy/Servicing/**`. **Do not touch** `Persistence/`, `Queries/`, `Commands/` or
  `Domain/Codes.cs` (SL3-POL-TEMPORAL owns them in parallel). If you need a code constant, define it locally and say so
  in the report.
- **PITFALLS to self-check (likely):** 10 (fail closed on an unknown method or convention; never guess), 11 (decimal
  only, explicit rounding from the given rule), 14 (Athens dates, half-open, DST), 17.
- **Workflow:** `briefs/sl3/_COMMON.md`: merge main, quick gate (build, `--filter` your Servicing classes), push,
  `gh pr create --base main --title "SL3-POL-ENGINE: pure servicing engine (segments, NET deltas, day count)"`, do not
  merge.
