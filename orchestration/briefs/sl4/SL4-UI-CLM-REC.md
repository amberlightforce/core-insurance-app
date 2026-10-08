# Brief SL4-UI-CLM-REC — claim file: recoveries, RI recoverable, payment corrections, follow-ups (wave 2)

Runs on Sonnet 5.5 (model: sonnet), D-USR-16.

Wave 2, **own PR** (after the live walk; earlier rounds may be **batched**). Review: light + visual (side by side with
the mockup). Estimate: 4 h.

**Gate:**
- start only after **SL3-UI-CLM has merged**: it owns the re-verification banner slot in `ClaimViewPage.tsx` and
  `claims/reverify/**`;
- code against MSW fakes from the SL4 samples;
- live walks after SL4-CLM-RECOVERY-OPS (recoveries), SL4-RI-RECOVERY (RI card) and SL4-CLM-PAYOPS (corrections)
  merge.

You are the **only slice-4 owner of `ClaimViewPage.tsx`**. SL4-UI-CLM-FS gives you its slot components through
`web/src/modules/claims/fs/index.ts`. Mount them, and render nothing if that index exports placeholders.

- **WP / PRD:**
  - SCR-CLM-02 (claim workspace: hero metric, money card), SCR-CLM-06 (financials incl. recovery reserves),
    SCR-CLM-07 (payments: void/stop/reissue), SCR-CLM-08 (recoveries), all subsets;
  - REQ-CLM-096 (net incurred), REQ-CLM-143…-151, REQ-CLM-125…-127;
  - REQ-CLM-229 (RI recoverable, read-only, via `ri.Recovery.listByClaim`, F-407);
  - the slice-2 follow-ups in the UI (set list, authority preview, close-guard detail).

  Sources: `orchestration/digests/PRD-07.md` §8 screens; the full PRD-07 SCR-CLM-06/07/08 field tables. Decisions:
  D-SL4-06, -07, -08, -09, -13, -14, -17.
- **Scope:**
  1. **Money card** (right column, mockup «Οικονομικά ζημίας»):
     - incurred = paid + reserve;
     - the lines include recoveries in the success colour: «Ανάκτηση · Υποκατάσταση», «Ανάκτηση · Διάσωση» (amount
       received), and expected ones «(αναμενόμενη)» from open recovery reserves;
     - a **net incurred** line;
     - real data only.
  2. **«Αντασφαλιστική ανάκτηση» card** (right column, below the money card):
     - incurred / paid / outstanding recoverable per treaty and layer from `ri.Recovery.listByClaim`;
     - a link to the trace (opens a side panel with the trace steps);
     - hidden when there are no rows; shows "δεν υπάρχει ενεργή σύμβαση" if RI answers with none.
  3. **Recoveries tab** (`claims/recoveries/**`):
     - case list (type, counterparty, expected, received, status, milestones);
     - create subrogation/salvage (with the counterparty organisation picker and salvage fields);
     - set the recovery reserve through the existing transaction builder (kind `RECOVERY_RESERVE`, reason required);
     - record a milestone;
     - "Απαίτηση" (demand) → shows the BIL payment reference to give the counterparty;
     - write-off (reason, authority preview).
     - The subrogation proposal (REQ-CLM-144) appears as a suggestion banner when the server says so.
  4. **Liability facts editor** on the claim (fault %, source, counterparty insurer, joint report, vehicles,
     accident in Greece) (D-SL4-17).
  5. **Transaction sets list** on the Financials tab (`clm.TransactionSet.list`), with status pills.
  6. **Authority preview** in the builder from the dry-run (`authorityPreview[]`): "Θα χρειαστεί έγκριση από Υπεύθυνο
     ζημιών" etc., before submit.
  7. **Close-guard detail:** list `errors[]` with links to the exposures.
  8. **Payment corrections** (`claims/payments/**`):
     - Void / Stop / Reissue actions on a payment with a reason;
     - `CLM-ERR-NOT-STOPPABLE` explained plainly;
     - reissue to a new payee account (masked IBAN capture via the existing `PayeeAccountForm`);
     - the "linked to payment X" chips and the fiscal MARK when present.
  9. **MTPL exposures** show «Νόμιμες προθεσμίες: δεν παρακολουθούνται (προσωρινό)» when `statutoryClocks =
     NOT_TRACKED` (D-SL4-09). The mockup's offer-clock pill stays absent until clocks exist.
- **Design:**
  - The claim file is the mockup's «Φάκελος ζημίας» (`../core-insurance-prds/design-guide/mockups/aegean-screens-v3.html`,
    around lines 725–815: header facts, money card list, CTA buttons).
  - No AI summary or assessor lines (D-USR-13).
  - Greek-first; all states.
  - Screenshots light/dark, desktop/narrow in `orchestration/ux/sl4-ui-clm-rec/`.
- **Files you own:**
  - `web/src/modules/claims/recoveries/**`, `claims/payments/**` (new);
  - `ClaimViewPage.tsx`, `FinancialsTab.tsx`, `ClaimMoney.tsx`, `TransactionBuilder.tsx` (preview only), `closeGuard.ts`;
  - your i18n namespace files.
  - **Not yours:** `claims/reverify/**` (slice 3), `claims/fs/**` and `claims/fs-statements/**` (SL4-UI-CLM-FS).
- **Shared (append only):** `web/src/routes.tsx`.
- **PITFALLS to self-check (likely):**
  - 25;
  - 26 (memoise; the claim file has many cards — check render loops in a real browser);
  - 27;
  - 28;
  - 18–20 (masked IBAN, no personal data in URLs/storage).
- **Workflow:** follow `briefs/sl4/_COMMON.md`. Round 1 on fakes may be pushed batched. After the live walks, merge
  main, run the web quick gate, push, then run
  `gh pr create --base main --title "SL4-UI-CLM-REC: recoveries, RI recoverable, payment corrections"`. Do not merge.
