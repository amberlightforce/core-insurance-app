# Brief SL3-POL-CANCEL — policyholder cancellation "now" and flat cancellation (S1)

**Runs on Sonnet 5.5 (model: sonnet), D-USR-16.** Batch S1. Review: **deep (temporal + money)**. Estimate: 3.5 h.

- **WP / PRD:** SL3-POL-CANCEL.
  - Requirements:
    - REQ-POL-205 (cancellation source from the MKT code list);
    - REQ-POL-206 (refund method from the product by source);
    - REQ-POL-207 (refund per element × charge type, tax via treatment);
    - REQ-POL-208 (default effective = request receipt time for Policyholder);
    - REQ-POL-209 (**Cancel now only**; future dates refused with `POL-ERR-EFFDATE-LIMIT` and the reason "scheduled
      cancellation not available in this release");
    - REQ-POL-214 (cancellation deltas), REQ-POL-215 (GR tax treatment from RAT/MKT), REQ-POL-216 (`PolicyCancelled`),
      REQ-POL-217 (flat cancellation of a Scheduled term);
    - REQ-POL-212 (notices: record "not sent — DOC not built" on the job; no DOC call);
    - REQ-POL-004/-134.
  - E2E-03 steps 1–3 (`SLICE-PLAN-3.md` §3.1). PRD-05 §5.6, §6 SCR-POL-14.
  - Decisions: D-SL3-02, D-SL3-03, D-SL3-04, D-SL3-05, D-SL3-06, D-SL3-08, D-SL3-11.
- **Scope:**
  1. `pol.Cancellation.create` (source, reasonCode, effectiveAt optional, kind Standard | Flat).
     - Only source `Policyholder` is accepted; other sources → `POL-ERR-VALIDATION` with "not available in this
       release" (fail closed).
     - Refund method from the term's pinned artefact (SL3-PFC-MOTOR11: ProRata, illustrative). Absent → refuse.
       FullRefund for DistanceWithdrawal is not used by this WP.
     - Effective time: default the request time (`IClock.Now`). Backdating is not allowed (0 days, fail closed).
     - Flat = term start, allowed only while the term is Scheduled (REQ-POL-217).
     - One open cancellation per term (partial unique index → 409).
  2. **Quote** (dry run equals real): SL3-POL-ENGINE `EndCover` → premium credits per element × charge type → RAT
     servicing tax lines (CANCELLATION + source Policyholder → IPT 0.00 `KEEP_NOT_REDUCED`, `provisional: true`).
     `RULE_MISSING` (e.g. a levy line) fails the quote with a plain error. The `servicingPreview` shows the refund due
     and "IPT not refunded (provisional)".
  3. **Bind:**
     - under the policy lock: base = head check, in-sequence check;
     - close the current segments at the effective time; write a new term version with state **Cancelled** and
       `cancelled_at`;
     - transaction kind Cancellation; charge lines with the treatment fields;
     - `PolicyCancelled` (source, refund method, effective date, kind) and `ChargeDeltaEmitted` in the same
       transaction;
     - afterwards, policy changes on the term are refused (G1, SL3-POL-CHANGE checks) and a second cancellation →
       `POL-ERR-ILLEGAL-TRANSITION`.
  4. `pol.Policy.get` and the term status show Cancelled from the effective time (D-SLC-13 semantics unchanged).
     Register in `PolicyModule.cs` (append) and add `Api/CancellationController.cs`. Add permissions `pol.cancel` and
     `pol.Cancellation.create` for Staff.Underwriter (append).
- **Depends on / provides:**
  - **Depends on:** **merged** SL3-POL-TEMPORAL and SL3-POL-ENGINE; contracts for RAT and MKT (fakes until they merge).
  - **Provides:** `PolicyCancelled` and the credit deltas for SL3-BIL-CREDIT, SL3-CLM-REVERIFY and SL3-FIN-RULES.
- **Files you own:** `src/CoreIns.Modules.Policy/Commands/Cancellation/**`, `Api/CancellationController.cs`,
  `tests/CoreIns.IntegrationTests/Policy/Cancellation/**`.
- **Shared (append only):** `PolicyModule.cs` and the POL permission file.
- **Do not touch:** `Persistence/**` (no migrations), `Queries/**`, `Domain/Servicing/**`, or the Change/Renewal
  folders.
- **Tests:**
  - **E2E-03 numbers:** a 430.00 premium bound at day 0, cancel at day 120 (fake IClock) → premium credit −288.63;
    IPT delta 0.00 with `KEEP_NOT_REDUCED`/PendingOpinion/provisional; Σ deltas = written − earned; the term is
    Cancelled; `PolicyCancelled` carries source and method.
  - A flat cancel of a Scheduled term credits exactly −written.
  - Refusals: other sources, a future date, backdating, a second cancellation, cancel after expiry.
  - A concurrent cancel and change → one wins, the other 409.
  - The dry run produces nothing.
  - Production: the provisional tax treatment is refused (MKT gate) → the cancellation fails closed with a plain error.
- **PITFALLS to self-check (likely):** 10, 11, 12, 13, 14, 15, 17, 24.
- **Workflow:** `briefs/sl3/_COMMON.md`: merge main, quick gate, push,
  `gh pr create --base main --title "SL3-POL-CANCEL: policyholder cancellation now and flat"`, do not merge.
