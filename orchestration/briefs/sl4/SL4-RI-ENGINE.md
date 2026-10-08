# Brief SL4-RI-ENGINE — pure XoL recovery engine (wave 1)

Runs on Sonnet 5.5 (model: sonnet), D-USR-16.

Wave 1, **own PR**. Review: **deep (money)**. Estimate: 3.5 h.

- **WP / PRD:** SL4-RI-ENGINE. Requirements:
  - REQ-RI-116 (UNL per clause);
  - REQ-RI-117 (realised recoveries only, the default clause);
  - REQ-RI-119 (paid/outstanding split);
  - REQ-RI-122 (closed claim → outstanding 0);
  - REQ-RI-123 (layer loss);
  - REQ-RI-125 / -126 (annual aggregate R(Cₖ) − R(Cₖ₋₁), full contract-year restatement, ordered by occurrence date
    then id);
  - REQ-RI-127 (target − booked deltas per occurrence × layer × participant);
  - REQ-RI-129 (idempotent, order-independent);
  - REQ-RI-132 (trace content);
  - BR-RI-006/007/027/028/029.

  Sources: `orchestration/digests/PRD-08.md` §3 and §15, and the full PRD-08 §5.8–5.9 (lines ~579–612 of
  `../core-insurance-prds/PRD-08-ceded-reinsurance.md`). Decision: **D-SL4-05** (the binding formulas).
- **Scope: a pure engine, no database, no DI registration, no I/O.**
  - It lives in `src/CoreIns.Modules.Reinsurance/Domain/Recovery/**`.
  - **Inputs (immutable records):**
    - contract terms: layers, aad, aal, clause flags, participations with signed lines and lead, placed %, MKT
      rounding rule passed in as a delegate/value;
    - a contract year's occurrences, each with a date, an id, and per-claim totals of in-scope cost types:
      - indemnity paid;
      - indemnity open reserve;
      - ALAE paid / open;
      - statutory interest paid / open;
      - realised recoveries;
      - a closed flag;
    - the booked cumulative recoverable per occurrence × layer × participant (incurred, paid, outstanding).
  - **Formulas (D-SL4-05):**
    - UNL incurred = indemnity (paid + open) [+ ALAE if included] [+ interest if included] − realised recoveries.
      ExpenseUnallocated is never an input.
    - UNL paid = paid parts − realised recoveries.
    - Both are floored at 0.
    - Layer loss = min(max(UNL − A, 0), L), layer by layer in ascending attachment.
    - Annual aggregate per layer: Cₖ = cumulative layer loss up to occurrence k; R(C) = min(max(C − AAD, 0), AAL)
      (no AAL = unlimited); recovery_k = R(Cₖ) − R(Cₖ₋₁). Recompute every occurrence of the year on any change.
    - Do the same separately on the paid basis to get the paid recoverable. Outstanding = incurred recoverable − paid
      recoverable, and is 0 when the claim is closed with no open reserve.
    - **Participant split:** amount × signed line %, rounded by the MKT rule, with the residual to the lead
      participant, so that Σ participants = layer recoverable × placed % exactly.
    - **Deltas** = target − booked, per occurrence × layer × participant, for incurred, paid and outstanding. A rerun
      with the same inputs yields all-zero deltas.
  - **Trace:** for each occurrence and layer, record the inputs, UNL components, attachment, limit, aggregate position
    before/after, outputs and an engine version string. SL4-RI-RECOVERY stores it.
  - `decimal` only. Never throw on a valid zero; validate inputs and return typed errors for impossible ones
    (negative limits, etc.).
- **Tests (xUnit, no containers):**
  - **Goldens:**
    - GT-05: UNL path 300k → 900k → 700k on 500k xs 250k gives incurred recoverables 50k → 500k → 450k and deltas
      +50k, +450k, −50k;
    - REQ-RI-119: 900k incurred / 400k paid → 500k / 150k / 350k;
    - REQ-RI-123: two layers, UNL 1.2m;
    - REQ-RI-125: AAD 300k with occurrences 200k then 250k → 0 and 150k;
    - REQ-RI-126: restatement when an earlier occurrence grows;
    - REQ-RI-116: indemnity 900k + ALAE 60k − salvage 40k = 920k.
  - **Property tests** (FsCheck if the repo already uses it, otherwise a hand-rolled seeded generator):
    - idempotence;
    - order independence over shuffled occurrence input;
    - Σ participants = layer × placed %;
    - recovery ≤ limit;
    - deltas summed over runs = final target;
    - paid recoverable ≤ incurred recoverable.
  - §3.1 of `SLICE-PLAN-4.md` row by row (60/40 split, cents).
- **Depends on / provides:**
  - **Depends on:** contracts only (and not even those, if you keep the engine's own records).
  - **Provides:** the engine for SL4-RI-RECOVERY.
- **Files you own:**
  - `src/CoreIns.Modules.Reinsurance/Domain/Recovery/**`;
  - `tests/CoreIns.IntegrationTests/Reinsurance/Engine/**`, or the unit-test project the repo uses for pure domain
    code.

  Nothing else: no DbContext, no `ReinsuranceModule.cs` change.
- **PITFALLS to self-check (likely):**
  - 10 (fail closed on missing inputs);
  - 11 (decimal, explicit MKT rounding);
  - 1 by analogy (aggregates are computed on the year, never per movement).
- **Workflow:** follow `briefs/sl4/_COMMON.md`. Merge main, run the quick gate, push, then run
  `gh pr create --base main --title "SL4-RI-ENGINE: XoL recovery engine"`. Do not merge.
