# SL4-BIL-RECV source checkpoint — 9 October 2026

Branch `codex/sl4-billing-receivables`, worktree `agent-a5f811b9f42a2e657`.

This is **unvalidated WIP**, not a completed package. Native build, ContractGen, EF migration generation and tests were expressly withheld by the coordinator: disk C was approximately 20 MB free. No native gates have run and no PR has been opened.

Implemented source:
- CLM-command-only registration via a narrow read-only `RequestContext.ExecutingCommandModule` accessor. The service captures registered producer identity before its nested Billing command starts. Public register uses null authority; actor strings cannot establish module identity.
- Receivable rows, immutable allocation rows, dedicated CLAIM_RECOVERY/CLEARING billing account types, compound duplicate key, entity-scoped reads and cursor paging.
- ISO 11649 RF checksum from an opaque UUID hash, 21-character body and 25-character total; DB unique reference is authoritative for the improbable hash collision.
- Payment matching by id/RF reference and FS statement reference on the counterparty account; bounded partial allocation; surplus remains unapplied. Receipt reads include receivable allocation sums.
- Data-driven ledger rules and security SQL, typed `CashAllocated` with all recovery/statement evidence. New LA-28 non-premium receivable asset avoids repurposing LA-23 (reinsurer receivable).
- Future FSOUT columns modeled now: statement_ref, lines jsonb, release_approval_request_id, release_approved_by. Existing FSOUT request remains refused by existing disbursement source guard.
- Real-host integration test source with registered CLM caller probe, cash allocation, partial/overpayment, FS partial receipt, duplicate/replay, actor spoof and HTTP origin refusal. RF pure tests check size and mod97.

Authoritative semantics:
- Full `C:/Users/Karl/Projects/coreinsurance/core-insurance-prds/PRD-06-billing-collections.md` REQ-BIL-130 bounds receipt/item allocation; -133 leaves overpayment credit; -356 explicitly specifies EUR8150 FS receivable plus EUR8100 cash leaves EUR50 open. Exact-only receivable wording in the pre-release OpenAPI contradicts those rows and SL4-BIL-RECV. Owner contract documentation now states bounded partial allocation/surplus; regenerate all generated files before validation.
- Extended duplicate tuple comes from SL4-BIL-RECV and PITFALLS9: sourceType/sourceId/purpose/counterparty/amount, entity-scoped. Same idempotency key replay is handled by native command pipeline; new key with same tuple is DUPLICATE.
- D-SL4-06 forbids fiscal request and refuses claim receivable registration in Production. FS stub registration also fails closed in Production.

Accounting proposal for FIN owner/review:
- RECEIVABLE_REGISTERED CLM: DrLA28 CrLA17; FS: DrLA28 CrLA24.
- Cash intake remains DrLA10 CrLA11; RECEIVABLE_COLLECTED: DrLA11 CrLA28.
- Combined FS receipt therefore DrLA10 CrLA24 as PRD356 specifies. BillingEntryPosted dimensions always carry claim/recovery/statement/counterparty when applicable.
- These are illustrative slice chart extension rules; FIN must confirm journal-rule mapping and matching claim movements before financial acceptance.

Required next steps:
1. Fetch/merge latest origin/main (source checkpoint incorporated PLT50 and RI48 at36e7e75; latest main advanced thereafter).
2. With disk space and exclusive native slot: build Release -m:2, fix compilation/analyzers, generate Billing EF migration and snapshot, append `ReceivableDatabaseSql.Guards` to Up with matching Down cleanup. Current source models are not deployed until migration exists.
3. Migration must include account_type index change, receivable/allocation tables, recovery/statement/counterparty ledger dimensions and FSOUT columns. Validate grant setup for receivable tables.
4. Run ContractGen generate/check and sample validation; compile focused tests. Add production refusal, ledger seal/update and allocation-race DB tests. Existing test source has not been executed.
5. Check counterparty existence and FS binding evidence against upstream authoritative producer; tighten current inputs if review requires it. Add meaningful BIL/other-module command-origin rejection (existing tests cover public and fake actor only).
6. Build focused Billing/architecture and changed Platform tests, independent money/security review, open RECV PR and attach it. Do not merge as builder.
7. Implement FSOUT as separate package after RECV gates: CLM approval verification, canonical line sums/hash, CLEARING-only source, screening/VoP, distinct BillingManager release approval and LA24 posting. Then DISBOPS remains a separate required package.

Known limits: no claim-party lookup yet; full schema migration absent; FSOUT absent; native gates absent; regression tests and accounting review pending. No live money acceptance claim.
