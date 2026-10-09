# SL4-CLM-MONEY2 resumed checkpoint — 9 October 2026

Branch `codex/sl4-claims-money`, checkout `agent-ad6062491a131f6ec`.
This is **unvalidated source work**, not a completed package or an executable deployment.

Interrupted modified and untracked files were copied into
`C:/Users/Karl/Projects/coreinsurance/recovery/2026-10-09-resume/claims-money`,
including a patch, before editing. Commit42162c9 preserves that exact WIP. Actual PLT50
was merged via origin/main1be0657; withdrawal uses its real owner-only implementation.

## Source checkpoint

- Recovery and recovery-reserve transaction kinds, binding in canonical content/basis hashes,
  positive cash validation and refusal of client-built RECOVERY transactions.
- Recovery reserve build validates the recovery's entity, claim, exposure and open state;
  mandatory reason; closed exposures can retain recovery reserves (REQ-CLM-072).
- EF models cover recovery cases, liability facts, FS cases/statements/lines, evidence uniqueness,
  payment correction columns, statutory-clock flag and one reissue per original.
- Derived recovery balances and net incurred in Financials; recovery reserve event provenance;
  RecoveryRecorded with line allocation and settlement evidence; CLEARING bypasses individual BIL requests.
- Aggregate recovery reserve authority; illustrative manager limit1,000,000; four-eyes above50,000;
  write-off and FS net authority registration/grants; typed approval diff; dry-run authority preview.
- Transaction-set list with entity filtering and keyset pagination; open-recovery close guard with field errors;
  rejection withdraws still-open sibling PLT approvals.
- Internal financial engine accepts evidence kind/reference only and resolves amounts through a module-owned
  evidence reader. Default reader refuses unavailable evidence. No HTTP path exposes this engine.
  System payments/reserves require a human approver; received recoveries require no authority.
- Added two domain regression test sources and updated older manager-limit denial test amounts.

## Mandatory next work before build/PR readiness

1. Native Release compile, resolve compiler/analyzer diagnostics, then generate the **one** Claims EF migration
   and snapshot. Models are ahead of migrations right now; existing database schemas cannot run this checkpoint.
2. Migration SQL must freeze evidence kind/ref and reissue/reversal binding, enforce same-entity/claim/exposure
   recovery links and preserved append-only financial protections, and support negative payment reversals when
   PAYOPS is added. Review existing claim-payment/set triggers before extending them.
3. Run ContractGen after additive submit authority basis `EXPOSURE_TOTAL_RECOVERY_RESERVE`; existing generated
   submit enum is not yet regenerated, so recovery reserve submit would fail during response mapping until then.
4. Integrate actual authoritative evidence providers in the owning recovery/FS/payment consumers. They are
   deliberately unavailable at this checkpoint. System engine needs replay/missing evidence, transaction/audit
   atomicity, human authority and context-restoration integration coverage. Do not replace refusal with success.
5. Add native integration tests for recovery-bound reserve increases/decreases, mixed reserve approvals,
   300,000 referral /1,000,000.01 denial /60,000 manager maker-checker, real sibling withdrawal (including decision
   races), CLEARING no disbursement, close guard errors, cross-entity refusal, ledger app-role protections,
   concurrent submits409, dry-run preview equality and as-of balances. Two domain tests are currently unrun.
6. Validate authority-preview handling for system sets against submit's synthetic eligibility/human referral
   logic; ensure the source resolver can preserve negative correction transactions without public acceptance.
7. Complete ContractGen check/sample validation, architecture and native focused/full required gates,
   E2E-02a, PITFALLS review and independent deep money review. Then open the package PR.

`git diff --check` passed. No native build, EF generation or tests were attempted in this resumed turn:
coordinator withheld the heavy slot, then reported disk20MB and paused native gates. No PR opened yet.
Later RECOVERY-OPS, FS-CASE, FS-STATEMENT and PAYOPS packages remain unimplemented in this lane.

Sources read: restart handover, CODEX-TAKEOVER, full MONEY2/common briefs, slice4 plan, decisions,
PITFALLS, module pattern, PRD07 digest and relevant financial/recovery/authority sections of the full
`core-insurance-prds/PRD-07-claims-management.md`. Full PRD confirms aggregate recovery balances and
the exception allowing recovery reserves to remain on closed exposures.
