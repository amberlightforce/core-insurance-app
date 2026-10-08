# Brief SL3-CONTRACTS — type every slice-3 contract (S0)

**Runs on Sonnet 5.5 (model: sonnet), D-USR-16.** Batch S0. Review: light (completeness and additivity), plus an
orchestrator check. Estimate: 2.5 h.

- **WP / PRD:** SL3-CONTRACTS. `SLICE-PLAN-3.md` §6 is the whole scope: type the request, response, error and event
  payloads of every operation and event slice 3 uses, then regenerate. Requirement sources:
  - PRD-05 §8.1/§9.1 (POL);
  - PRD-06 §8.1/§9.1 (BIL);
  - PRD-11 §9.1 (CMP fiscal);
  - PRD-07 REQ-CLM-002/057/058 (CLM);
  - PRD-03 REQ-RAT-004/155 (RAT);
  - PRD-17 §7.5 (MKT `TaxCalculator.treatment`).
- **Depends on / provides:**
  - **Depends on:** nothing. Start at once.
  - **Provides:** every other slice-3 WP codes against your generated interfaces and fakes. Merge fast: you unblock 12
    WPs.
- **What to type (all additive; operations already exist with `x-status` minimal or full):**
  - **POL** (`contracts/openapi/pol.yaml`, `contracts/events/pol/`):
    - `pol.PolicyChange.create`.
    - `pol.Cancellation.create`: `source` from the code list, `reasonCode`, `effectiveAt`, `kind` (Standard | Flat).
    - `pol.Renewal.create/offer/accept`: accept carries `channel` and `acceptedAt`.
    - `pol.Job.quote/bind` responses gain `servicingPreview`. It holds the annual premium before and after; prorated
      lines per element × charge type (days, fraction, amount); tax lines with `treatmentAction`, `ruleId`,
      `ruleVersion`, `legalStatus` and `provisional`; total change; refund due or additional due.
    - `pol.Term.timeline`: transactions with kind, effective, recorded, premium change, sequence.
    - `pol.Snapshot.get` gains `effectiveKnownAt` and `supersession { superseded, successorRef?, supersededAt? }`,
      outside `content`. `pol.Policy.get` gains `effectiveKnownAt`.
    - New error code `POL-ERR-OUT-OF-SEQUENCE` (422). Make sure `POL-ERR-PREEMPTED`, `-REBASE-REQUIRED`,
      `-AFTER-CANCELLATION`, `-EFFDATE-LIMIT` and `-STALE` (409) are listed on the right operations.
    - Events:
      - `PolicyChanged`: effective date, changed locators, vehicle facts (added/removed);
      - `PolicyCancelled`: source, refund method, effective date, kind;
      - `RenewalCreated/Offered/Bound`: new term id and number, predecessor term;
      - `ChargeDeltaEmitted` gains `transactionKind` (the MKT enum), `cancellationSource?`, `treatmentRuleId?`,
        `treatmentRuleVersion?`, `legalStatus?` and `provisional?`.
  - **RAT:**
    - `rat.Proration.prorate`: annual rates per element × charge type × segment, term period, convention and
      configuration hash in; amounts, fractions and residuals out; errors `RAT-ERR-CONVENTION` and `-PERIOD`.
    - `rat.Rate.rate`: `mode` ENDORSEMENT | RENEWAL plus `pinnedRatingArtefactHash`.
  - **MKT:** check `ITaxCalculator.TreatmentAsync` (`src/CoreIns.Modules.Market.Contracts/Spi/ITaxCalculator.cs`)
    against PRD-17 §7.5: the transaction kinds, cancellation source, action, the three detail fields, and
    `ruleId/ruleVersion/legalStatus/legalSourceRef`. Fix only what is missing, additively. Type the cancellation-source
    code list (REQ-POL-205).
  - **BIL:**
    - invoice `kind` CREDIT_NOTE plus `originalInvoiceId`;
    - `bil.Refund.propose/get/list/decide/resubmit`: breakdown per charge type, netting, payee (masked), state,
      approval state;
    - `bil.PayeeAccount.create` purpose REFUND;
    - `BillingEntryPosted` gains entry types `CREDIT_WRITTEN`, `CREDIT_BILLED` and `REFUND_APPROVED`, and the
      disbursement entry types gain source `BIL_REFUND`;
    - line dimensions `transactionKind`, `cancellationSource` and `treatmentRuleId`;
    - `RefundApproved`, `RefundDisbursed` and `RefundRejected` typed.
  - **CMP:** `cmp.FiscalDocument.request` gains role `CREDIT` and `correlatedDocumentId`.
  - **CLM:** `clm.Coverage.reverify` (decision KEEP | ADOPT, reason code, expected new ref); claim view `snapshotStatus`;
    the `ReverificationRequired` payload (claim id, old ref, new ref, cause event id).
- **Must use:** `tools/CoreIns.ContractGen` and `python tools/CoreIns.ContractGen/validate_samples.py`. Do not edit
  module implementation code. If an implementation stops compiling because a type changed, use named arguments or
  report it; do not change behaviour.
- **Read first:** `contracts/README.md`, D-API-06/06a, D-CON-*, D-SL2-12b, D-SLC-14 in `DECISIONS.md`, and the digests
  PRD-05, PRD-06, PRD-11, PRD-17 §7.5 and PRD-07.
- **Do not add payload fields carrying personal data:** no names, IBANs, plates or AFMs in any event (PITFALLS 18).
- **PITFALLS to self-check (likely):** 12 (fields consumers depend on: say in the schema description that they are
  always set), 18, 21 (samples satisfy their schema; `validate_samples.py` passes), 22, 23.
- **Files you own:** `contracts/**`, `src/*.Contracts/Generated/**`, `contracts/openapi/INDEX.md` (regenerated). The
  count tests are self-maintaining after D-PRG-21. If they are not, update only the counts.
- **Workflow:** follow `briefs/sl3/_COMMON.md`: merge main, quick gate (build + Contracts tests + ContractGen `--check`
  + validate_samples), push, `gh pr create --base main --title "SL3-CONTRACTS: type slice-3 contracts"`, do not merge.
- **Done when:** every row of `SLICE-PLAN-3.md` §6 is typed, generated code compiles, the samples validate, CI on the PR
  is green, and the report lists each new or changed operation and event.
