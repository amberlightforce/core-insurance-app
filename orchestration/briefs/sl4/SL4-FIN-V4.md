# Brief SL4-FIN-V4 — posting rules v4: recoveries, Friendly Settlement clearing, RI recoverables, payment voids (wave 2)

Runs on Sonnet 5.5 (model: sonnet), D-USR-16.

Wave 2, **own PR**. Review: **deep (ledger)**. Estimate: 4 h.

**Gate:** start only after **SL3-FIN-RULES has merged**. It owns `Posting/**`, `Domain/PostingRules.cs` and rule set v3
in slice 3. Your rule set is **v4**; build it on top of v3 and never edit the v1–v3 seeds.

- **WP / PRD:**
  - REQ-FIN-037 (claim facts only from CLM; cash only from BIL; clearing nets per payment; FS nets per statement);
  - REQ-FIN-159 (payment voids by reversal);
  - REQ-FIN-160 (recoveries by type with the counterparty dimension; FS recoveries against FS clearing);
  - REQ-FIN-164 (RI recoveries on paid and outstanding separately);
  - REQ-FIN-299 + BR-FIN-097 (GL-2515 nets to zero per counterparty and statement; `CLEARING` excluded from
    BR-FIN-096);
  - GF-05 / GF-10 (subsets).

  Sources: `orchestration/digests/PRD-09.md` (chart §4, events table, Greek FS line 484), and the full PRD-09 rows
  REQ-FIN-037/159/160/164/299. Decisions: **D-SL4-02, D-SL4-10**, D-SL2-08, D-SL2-12 (the fail-closed rules stay),
  D-SLC-12.
- **Scope (rule set `gr-test.finance.v4.json`; accounts PRD-09 *Illustrative*; placeholders flagged `PLACEHOLDER`):**
  1. **Recovery reserve:** `ReserveChanged` kind `RECOVERY_RESERVE`:
     - Dr recovery reserve asset / Cr GL-5110 (incurred) by the delta, with the claim dimensions plus `recoveryId`.
     - The slice-2 note says this had no rule; now it has one.
  2. **Recovery recorded (CLM):** `RecoveryRecorded` by type:
     - **Subrogation / Salvage:** Dr **claim recovery clearing** (PLACEHOLDER) / Cr recovery income per type
       (PLACEHOLDER), with the counterparty party dimension. Release the matching recovery reserve if CLM's event
       says so: CLM publishes the reserve release as its own `ReserveChanged`; do not infer it.
     - **FriendlySettlement:** Dr GL-2515 / Cr recovery income FS (PLACEHOLDER), with `fsStatementId` + counterparty.
  3. **Recovery cash (BIL):** `BillingEntryPosted` `RECEIVABLE_COLLECTED` for source `CLM_CLAIM_PAYMENT`:
     - Dr GL-1110 cash / Cr claim recovery clearing.
     - The clearing account must **net to zero per recovery id**. Add an invariant query plus a test, like GL-2510 per
       payment.
  4. **Friendly Settlement:**
     - `PaymentIssued` with method `CLEARING`: Dr GL-5110 / Cr **GL-2515**, with `fsStatementId` + counterparty.
       **Replace** the slice-2 NO_RULE suspense for `CLEARING` (D-SL2-12a) with this rule. Keep suspending a
       `CLEARING` payment **without** `fsStatementId` or counterparty (fail closed).
     - BIL `FS_CLEARING` entries (`FS_CLEARING_RELEASED/_CLEARED` for a net payable; `RECEIVABLE_REGISTERED/
       _COLLECTED` with source `FS_CLEARING` for a net receivable): GL-2515 against cash/in-transit.
     - **GL-2515 nets to zero per (counterparty, statement)** once BIL has posted the net. Provide the invariant query
       and a test.
     - `CLEARING` is excluded from the per-payment GL-2510 check.
  5. **RI:** `RecoveryCalculated`:
     - for each delta, Dr **GL-1320** RI held asset for incurred claims / Cr **GL-4210** amounts recovered from
       reinsurers;
     - **two lines per delta**: one for the paid part and one for the outstanding part, with dimension
       `recoverableBasis` PAID/OUTSTANDING, the participant party, the contract and contract year, and
       `ri_held_group = UNASSIGNED`;
     - negative deltas post reversed sides;
     - the source event id + delta index make the journal unique.
  6. **Payment void:** `PaymentVoided` → reversal of the payment journal (link to the original journal, never edit
     it). BIL's `DISBURSEMENT_REVERSED` entry reverses the cash side, and GL-2510 returns to its previous balance per
     payment.
  7. **Fail closed** (PITFALLS 10): unknown recovery type, missing counterparty, missing `fsStatementId` for FS,
     missing `recoverableBasis` split, currency ≠ EUR, or functional/group ≠ transaction amount → suspended with a
     reason, never a default.
  8. Every journal balanced, append-only and sealed (existing D-ARC-34 triggers); gapless journal numbers as today.
- **Depends on / provides:**
  - **Depends on:** the merged SL3-FIN-RULES; contracts for the new event fields. Use the generated fakes and
    hand-built events in tests. Real-module tests (CLM recovery, BIL receivable, RI) come in SL4-E2E.
  - **Provides:** E2E-02 steps 5, 9, 12, 14 and E2E-06 step 8.
- **Files you own:**
  - `src/CoreIns.Modules.Finance/Seed/gr-test.finance.v4.json` (new);
  - `Posting/Recovery*`, `Posting/Fs*`, `Posting/RiFacts.cs` (new);
  - `Posting/ClaimFacts.cs` (edit: `CLEARING` and the recovery reserve);
  - `Domain/PostingRules.cs`;
  - `tests/CoreIns.IntegrationTests/Finance/V4/**`.
  - A FIN migration only if a new invariant view needs one (you are the FIN migration owner in slice 4).
- **Shared (append only):** the FIN permission file.
- **Tests (each journal asserted balanced and sealed):**
  - subrogation 200,000: reserve, recovery, BIL cash → clearing 0 per recovery;
  - salvage 1,500 reserve / 1,700 recovery;
  - FS:
    - payables 3,000 and FS recovery 1,200 on statement S;
    - BIL net payable 1,800 → GL-2515 = 0 for (Y, S); no GL-2510 break;
    - the variant net receivable;
  - RI:
    - deltas +50,000 then +450,000 then the payment split (paid 150,000 / outstanding 350,000) then −50,000: the
      GL-1320 balance by basis equals RI's totals;
  - payment void reversal;
  - each fail-closed case suspends;
  - a duplicate event → one journal;
  - the rule set v4 activates without changing v1–v3 journals.
- **PITFALLS to self-check (likely):** 8, 9, 10, 11, 12, 29 (complete sets in tests).
- **Workflow:** follow `briefs/sl4/_COMMON.md`. Merge main, run the quick gate, push, then run
  `gh pr create --base main --title "SL4-FIN-V4: recoveries, FS clearing, RI recoverables, payment voids"`. Do not
  merge.
