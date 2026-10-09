# SL4-CLM-MONEY2 resumed checkpoint — 9 October 2026

Branch `codex/sl4-claims-money`, checkout `agent-ad6062491a131f6ec`.
This is a **partially validated review checkpoint**, not a completed package. Database migration/runtime gates and independent money review remain outstanding.

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

## Validated bounded gates

The coordinator granted one serialized native slot, with a 1.5 GB disk reserve.

- Claims Release build: passed, zero warnings/errors (`.logs/money2-claims-build2.log`).
- IntegrationTests Release compilation: passed, zero warnings/errors (`.logs/money2-integration-build.log`).
- MONEY2 pure domain tests: 2 passed, none failed/skipped (`.logs/money2-pure-tests.log`).
- ContractGen build/run/check: passed; 2,852 generated files current. Submit authority enum and
  PaymentIssued insurer counterparty were regenerated (`.logs/money2-contractgen*.log`).
- API sample validation: 2,423 checked, OK (`.logs/money2-sample-validation.log`).
- OpenAPI validation: 1,142 operations, zero errors, 18 pre-existing warnings. Optional full OpenAPI
  schema validator was unavailable (`.logs/money2-openapi-validate.log`).
- EF generated `20261009171616_RecoveryMoney` and snapshot. SQL Up/Down guard installation is attached.
  Generation and compilation establish source consistency; the migration has not been applied to a database.
- `git diff --check` passed. Disk after gates: approximately 2.15 GB free. Native slot released.

## Remaining acceptance work

1. Docker is unavailable. Eight MONEY2 API/database test sources compile, but have not run. Run these
   with the actual PLT50 implementation and apply/rollback the migration against PostgreSQL before acceptance.
   Cover authority referrals, real sibling withdrawal, typed close errors, public recovery refusal,
   evidence immutability, missing authoritative evidence and CLEARING approval without BIL disbursement.
2. Extend database coverage for concurrent submit/withdrawal races, cross-entity ledger protections,
   recovery reserve decreases and as-of balances. Review database guard/index naming and existing-trigger interaction.
3. Actual authoritative evidence readers belong to the recovery/FS/payment consumers and remain unavailable
   here. The module-owned engine intentionally fails closed. The scripted test reader proves engine wiring,
   replay and approval behavior only; it does not prove real BIL/FS evidence acceptance.
4. Full required Release/architecture gates, E2E-02a, PITFALLS review and independent deep money review
   remain outstanding. Treat any PR as draft pending these gates.
5. Later RECOVERY-OPS, FS-CASE, FS-STATEMENT and PAYOPS packages remain unimplemented in this lane.

Sources read: restart handover, CODEX-TAKEOVER, full MONEY2/common briefs, slice4 plan, decisions,
PITFALLS, module pattern, PRD07 digest and relevant financial/recovery/authority sections of the full
`core-insurance-prds/PRD-07-claims-management.md`. Full PRD confirms aggregate recovery balances and
the exception allowing recovery reserves to remain on closed exposures.
