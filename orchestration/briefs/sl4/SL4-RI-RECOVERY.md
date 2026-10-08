# Brief SL4-RI-RECOVERY — claim intake, recovery booking and RI recovery API (wave 2)

Runs on Sonnet 5.5 (model: sonnet), D-USR-16.

Wave 2, **own PR**. Review: **deep (money)**. Estimate: 4.5 h.

**Gate:** start after **SL4-RI-REGISTRY and SL4-RI-ENGINE have merged**. This WP is not gated by slice 3, because it
consumes CLM events through the generated contracts only.

- **WP / PRD:**
  - REQ-RI-003 (recoveries on every claim movement), REQ-RI-004 (events to FIN, book-neutral);
  - REQ-RI-113 (ClaimView; idempotent on event id; per-claim order);
  - REQ-RI-114 (claim attributes via `clm.Claim.get`);
  - REQ-RI-115 (per-risk occurrence only, D-SL4-04);
  - REQ-RI-122 (closed/reopened);
  - REQ-RI-127, -128 (triggers), -129;
  - REQ-RI-131 (state Calculated; Posted on `JournalPosted` only if FIN publishes it, otherwise leave it Calculated
    and report);
  - REQ-RI-132 (trace stored), REQ-RI-136 (`listByClaim`, `listByContract`, `trace`, as of record time).

  Sources: `orchestration/digests/PRD-08.md` §3–§6 and §15 (hardest part 1), and PRD-08 J-04. Decisions:
  **D-SL4-04, D-SL4-05, D-SL4-19**, D-ARC-26, D-ARC-34, D-SL2-06.
- **Scope:**
  1. **ClaimView intake** (`Modules.Reinsurance/Intake/**`), as outbox consumers of these CLM events:
     - `ClaimReported`: read loss instant, product and policy via `clm.Claim.get`;
     - `ExposureCreated`: coverage code, exposure kind;
     - `ReserveChanged`: incl. kind `RECOVERY_RESERVE`, stored but not inuring (realised only);
     - `PaymentIssued`: method `CLEARING` counts as paid; FS payables are claims cost;
     - `PaymentVoided`: reverses the paid amount;
     - `RecoveryRecorded`: a realised recovery;
     - `ClaimClosed` / `ClaimReopened`.

     Intake rules:
     - idempotent on event id;
     - store the last applied `aggregateSequence` per claim, and treat a lower or equal one as a no-op (D-SL4-19);
     - keep per claim × exposure × cost type: indemnity paid, open reserve, ALAE, interest, realised recoveries,
       closed flag;
     - never store personal data (party ids only).
  2. **Applicability:**
     - per exposure, ask `ri.Contract.applicable(lossAt, productCode, coverageCode)` (in-process, your own registry
       query);
     - an exposure is in scope only if an Active contract matches;
     - several matching contracts → apply each (no inuring order needed for one XoL; if two overlap, refuse with a
       `CessionException`-style log row and report it; do not invent an order);
     - occurrence = claim (per risk).
  3. **Recalculation:**
     - on every relevant movement, take a **per contract-year advisory lock** (one batch in flight, D-SL4-19);
     - load the year's occurrences and the booked cumulative rows, then call the SL4-RI-ENGINE;
     - persist the non-zero deltas as **append-only, sealed** recovery rows (occurrence × layer × participant,
       incurred/paid/outstanding deltas, four amounts equal in EUR, batch id, calc hash). Use a BEFORE INSERT seal
       trigger plus an app-role test (PITFALLS 8, D-ARC-34);
     - store the trace per batch;
     - publish **one `RecoveryCalculated` per batch**, with every delta and the claim-level totals, through the outbox
       in the same transaction;
     - the posting key and SII LoB (from the claim/exposure; `UNMAPPED` if CLM has none, as D-SL2-11e);
       `ifrs17GroupRef = UNASSIGNED`; `sourceCorrelationKey` = claim id + source event id.
  4. **API:** `ri.Recovery.listByClaim(claimId, knownAt?)`, `listByContract(contractId, knownAt?)`, `trace(batchId)`:
     - as of record time: the sum of rows with `recorded_at ≤ knownAt`;
     - refuse a future `knownAt` (PITFALLS 13);
     - permission: claims roles and RI roles read.
  5. **RI migration for wave 2:** you are the RI migration owner now. RI-REGISTRY has merged.
- **Depends on / provides:**
  - **Depends on:** merged RI-REGISTRY + RI-ENGINE; contracts for the CLM events. Unit and integration tests publish
    CLM events through the outbox test harness or the generated fakes. A **real-CLM integration test** (reserve →
    `RecoveryCalculated`) runs once SL4-CLM-MONEY2 has merged; mark it and run it after merging main.
  - **Provides:** `RecoveryCalculated` for SL4-FIN-V4, `ri.Recovery.listByClaim` for SL4-UI-CLM-REC, and E2E-02
    steps 13–14.
- **Files you own:**
  - `src/CoreIns.Modules.Reinsurance/Intake/**`, `Recovery/**` (application layer, not `Domain/Recovery/**`),
    `Api/Recovery*`, `Persistence/**` (migration owner, wave 2);
  - `tests/CoreIns.IntegrationTests/Reinsurance/Recovery/**`.
- **Shared (append only):** `ReinsuranceModule.cs` (DI), `ri.json`.
- **Tests:**
  - the §3.1 table of `SLICE-PLAN-4.md` end to end with events, rows a–d (deltas per participant, cents);
  - a duplicate event → no new rows;
  - replaying the whole event stream into an empty RI gives the same booked totals;
  - recalculating twice → zero deltas;
  - an exposure out of scope (own damage) → no recovery;
  - no Active contract at the loss date → nothing booked;
  - `ClaimClosed` with an open reserve released → outstanding 0, paid unchanged;
  - `PaymentVoided` → negative paid delta;
  - concurrent movements on two claims of the same contract year → serialised, totals equal a single full
    recalculation, no 500;
  - app role cannot UPDATE/DELETE booked rows;
  - no personal data in `RecoveryCalculated`;
  - `listByClaim` as of an earlier `knownAt`.
- **PITFALLS to self-check (likely):** 8, 9, 10, 11, 12, 13, 15, 22.
- **Workflow:** follow `briefs/sl4/_COMMON.md`. Merge main, run the quick gate, push, then run
  `gh pr create --base main --title "SL4-RI-RECOVERY: ClaimView, recovery booking and RecoveryCalculated"`. Do not
  merge.
