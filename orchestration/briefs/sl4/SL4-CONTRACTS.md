# Brief SL4-CONTRACTS — type every slice-4 contract (wave 1)

Runs on Sonnet 5.5 (model: sonnet), D-USR-16.

Wave 1, **own PR**. Review: light (completeness and additivity), plus an orchestrator check. Estimate: 3 h.

- **WP / PRD:** SL4-CONTRACTS. `SLICE-PLAN-4.md` §6 is the whole scope: type the request, response, error and event
  payloads of every operation, event and SPI that slice 4 uses, then regenerate. Requirement sources:
  - PRD-07 §8 and §9.1 (CLM recoveries, Friendly Settlement, payments, transaction sets);
  - PRD-08 §8.1 and §9.1 (RI contract, recovery);
  - PRD-06 §8.1 and §9.1 (BIL receivables, `FS_CLEARING`, disbursement stop/void/events);
  - PRD-17 §9.4.23/§9.4.43 (`FriendlySettlementClearing`, R-42/R-87);
  - PRD-11 (CMP fiscal source `CLM_CLAIM_PAYMENT`);
  - PRD-14 (PLT approval withdrawal).
- **Depends on / provides:**
  - **Depends on:** nothing. Start at once.
  - **Provides:** every other slice-4 WP codes against your generated interfaces and fakes. Merge fast.
- **Timing rule:**
  - Slice-3 WPs may add small additive typing to their own operations in `clm.yaml`, `bil.yaml` and `cmp.yaml` while
    you work. Edit only **new** operations, schemas and enum values in those files.
  - On a conflict, merge textually, then regenerate. Never hand-edit generated files.
  - SL5-CONTRACTS-PACKS (slice 5) types `mkt.yaml`, `pfc.yaml` and new `pol.yaml` paths. Never edit those files. The
    orchestrator will not merge both contracts PRs at the same time; whichever lands second merges main and
    regenerates.
- **What to type (all additive; most operations already exist with `x-status` minimal):**
  - **RI** (`contracts/openapi/ri.yaml`, `contracts/events/ri/`):
    - `ri.Contract.create`:
      - type enum: `XOL_PER_RISK` only for now; keep the PRD's other values in the enum, but document that the slice
        refuses them;
      - contract year;
      - `period` with start and end as Athens dates, half-open;
      - currency;
      - `scope { productCodes[], coverageCodes[] }`;
      - `clause { alaeIncluded, statutoryInterestIncluded, recoveriesInure (REALISED_ONLY) }`;
      - `layers[] { layerNo, attachment, limit, aad, aal? }`;
      - `participations[] { reinsurerPartyId, brokerPartyId?, signedLinePct, lead }`;
      - `placedPct`.
    - `ri.Contract.update` (Draft only, `expectedRecordVersion`), `submit`, `approve` (decision APPROVE | RETURN,
      reason), `get`, `list`, and `applicable(lossAt, productCode, coverageCode)`.
    - `ri.Recovery.listByClaim(claimId, knownAt?)`, `listByContract(contractId, knownAt?)`, `trace(batchId)`, returning
      recoverable incurred, paid and outstanding per contract, layer and participant, and the trace steps.
    - Errors `RI-ERR-VALIDATION`, `-SIGNED-LINES`, `-STATE`, `-SOD`, `-STALE`.
    - `RIContractActivated` payload: contract, year, type, period, currency, layers, participants.
    - `RecoveryCalculated` payload, as `SLICE-PLAN-4.md` §6 (four amounts, posting key, `ifrs17GroupRef`,
      `sourceCorrelationKey`).
  - **CLM** (`clm.yaml`, `events/clm/`):
    - add `clm.Recovery.create/get/list/recordMilestone/demand/writeOff` (REQ-CLM-143…-151), with salvage fields
      (estimate, buyer party, sale price);
    - `clm.Claim.update` liability facts (D-SL4-17);
    - transaction-set build lines of kind `RECOVERY_RESERVE` with `recoveryId`;
    - dry-run response `authorityPreview[]`;
    - `clm.TransactionSet.list(claimId)`;
    - a typed approval `diff` (before/after per line);
    - close-guard `errors[]` items `{ code, exposureIds[] }`;
    - add `clm.FriendlySettlement.evaluate/openOwnSettlement/submit/get/list/importStatement/getStatement/approveNet`;
    - type `clm.Payment.void/stop/reissue`;
    - the payment view gains `method` (incl. `CLEARING`), `reissueOf`, `fiscalMark?`;
    - errors `CLM-ERR-FS-DISABLED`, `-ALLOCATION`, `-NOT-STOPPABLE`, `-RECOVERY-STATE`, `-MTPL-CLOCKS-REQUIRED`;
    - events:
      - `RecoveryRecorded` fully typed;
      - `ReserveChanged` kind `RECOVERY_RESERVE` + `recoveryId`;
      - `PaymentIssued` method `CLEARING` + `fsStatementId?`;
      - `PaymentVoided` typed;
      - `FriendlySettlementSubmitted` typed.
  - **BIL:**
    - type `bil.Receivable.register` (source type `CLM_CLAIM_PAYMENT` | `FS_CLEARING`, source id, counterparty party,
      claim id?, recovery id?, statement ref?, purpose, amount, due date → receivable id, billing account id, payment
      reference);
    - add `bil.Receivable.get/list`;
    - `bil.Payment.take` gains optional `paymentReference`/`receivableId`;
    - `bil.Disbursement.request` gains the `FS_CLEARING` fields (statement ref, lines, method `CLEARING`);
    - add `bil.Disbursement.approveRelease` (decision, reason);
    - type `bil.Disbursement.stop/void`;
    - errors `BIL-ERR-SOURCE`, `-DUPLICATE`, `-FISCAL-TREATMENT-OPEN`, `-LINES-MISMATCH`, `-NOT-STOPPABLE`, `-SOD`;
    - events: `CashAllocated` refs; `DisbursementStopped/Voided/Returned/Rejected` typed; `BillingEntryPosted` new
      entry types and dimensions (§6).
  - **MKT:** hand-written SPI `src/CoreIns.Modules.Market.Contracts/Spi/IFriendlySettlementClearing.cs`, in the style
    of `ITaxCalculator.cs`. It has the four operations of §6, with typed records; `SubmitDisputeAsync` and
    `RecordReplyAsync` are typed but documented as not implemented in slice 4. The capability key constant is
    `cap.clm.friendly_settlement`.
  - **CMP:** document that `sourceType` accepts `CLM_CLAIM_PAYMENT` with category `CLAIM_SETTLEMENT_RECEIPT`.
  - **PLT:**
    - in-process `IPlatformApprovalService.WithdrawAsync(approvalRequestId, reason)`: the interface only, no REST
      path;
    - `ApprovalDecided` outcome `WITHDRAWN` if it is missing.
- **Must use:** `tools/CoreIns.ContractGen` and `python tools/CoreIns.ContractGen/validate_samples.py`. Do not edit
  module implementation code. If an implementation stops compiling because a type changed, use named arguments or
  report it; do not change behaviour.
- **Read first:**
  - `contracts/README.md`;
  - D-API-06/06a, D-CON-*, D-SL2-12b and D-SL3-* in `DECISIONS.md`;
  - the digests PRD-07, PRD-08, PRD-06, PRD-17 and PRD-18-B (§4.3 E2E-02 and E2E-06).
- **Do not add payload fields carrying personal data:** no names, IBANs, plates or AFMs in any event (PITFALLS 18).
- **PITFALLS to self-check (likely):**
  - 12: fields consumers depend on — say in the schema description that they are always set;
  - 18;
  - 21: samples satisfy their schema; `validate_samples.py` passes;
  - 22;
  - 23.
- **Files you own:**
  - `contracts/**`;
  - `src/*.Contracts/Generated/**`;
  - `Market.Contracts/Spi/IFriendlySettlementClearing.cs`;
  - `contracts/openapi/INDEX.md` (regenerated).
- **Workflow:** follow `briefs/sl4/_COMMON.md`. Run the quick gate (build + Contracts tests + ContractGen `--check` +
  validate_samples), push, then run `gh pr create --base main --title "SL4-CONTRACTS: type slice-4 contracts"`. Do not
  merge. SL4-E2E-HARNESS may be folded into your PR by the orchestrator.
- **Done when:**
  - every row of `SLICE-PLAN-4.md` §6 is typed;
  - the generated code compiles and the samples validate;
  - CI on the PR is green;
  - the report lists each new or changed operation, event and SPI member.
