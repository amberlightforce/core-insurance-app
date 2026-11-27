# Index and query review (NX-OT-11)

Date 2026-11-26. Scope: foreign-key and lookup columns without a usable index, per module schema.

## Method
- Fresh throwaway PostgreSQL 17 with all module migrations applied (105 tables, no RLS policies).
- Catalog query: every `uuid` column named `*_id` that is not the first (or second, after `legal_entity_id`) column of any valid index.
  PostgreSQL does not index FK columns automatically, so each hit is a sequential scan on lookup or on parent delete/update.
- Not done: `pg_stat_statements` / `EXPLAIN` at volume. The seed (`infra/local/seed-demo.py`) creates a handful of rows, so planner output
  there says nothing. Re-run the catalog query and `EXPLAIN (ANALYZE, BUFFERS)` on the hot queries once a volume load exists
  (the local postgres already preloads `pg_stat_statements`).

## Findings
- **Greek unaccent search (pty.party_search_key)** is already well served: GIN `gin_trgm_ops` for `%` / similarity, and a partial
  `(legal_entity_id, search_key text_pattern_ops) WHERE recorded_to IS NULL` for prefix/exact `LIKE`. No change.
- **Tenant (legal entity) indexes:** most tables already lead composite indexes with `legal_entity_id` (policy number, policyholder, ...).
  A standalone `legal_entity_id` index was deliberately NOT added: a stamp serves one legal entity, so the column has one value and the
  index would never be chosen. Revisit if a multi-entity stamp is ever deployed.
- **Missing FK/lookup indexes added** (one migration `AddFkLookupIndexes` per module; names `ix_<table>_<column>`):
  - Billing: invoice.term_id, invoice_item.term_id, allocation.invoice_id, charge.policy_id, receivable.claim_id, refund.payee_party_id,
    disbursement.payee_party_id, delinquency_case.policy_id, ledger_line.(policy_id, invoice_id, claim_id), credit_application.target_invoice_id,
    respread.billing_account_id, refund_credit.policy_id
  - Claims: claim.policy_term_id, claim_payment.payee_party_id, financial_transaction.(exposure_id, claim_payment_id)
  - Policy: policy_transaction.term_id, segment.policy_id, pack_rollback_exception.policy_id, psr_exception.policy_id, charge_line.policy_id,
    policy.account_id, oos_correction.term_id, withdrawal.term_id
  - Finance: business_event.(policy_id, billing_account_id), journal_line.(policy_id, invoice_id, billing_account_id)
  - Compliance: complaint.complainant_party_id, disclosure_log.dsar_id, bureau_handover.policy_id, fiscal_document.counterparty_party_id
  - Documents: document.(recipient_party_id, archive_id), delivery.recipient_party_id, evidence.delivery_id
  - Reinsurance: claim_view.policy_id, premium_delta.policy_id, recovery.layer_id, recovery_batch.claim_id, participation.broker_party_id
  - Channels: ticket.interaction_id, withdrawal_request.policy_id; Migration: xref.batch_id
- **Left unindexed on purpose** (write-heavy append-only or rarely queried): `plt.outbox_message/event_archive/audit_event` causation, set and AI
  interaction ids; approval/authority-check ids; `*_event_id` provenance columns; `ledger_line` secondary refs (charge, receipt, recovery, term,
  transaction, allocation, disbursement); user-id columns on approvals. Add per concrete query if `pg_stat_statements` shows one.
- Ledger-sealed tables (`bil.ledger_line`) take plain btree indexes only; the seal trigger is unaffected.

## Follow-up
Run a volume seed (e.g. 100k policies) then: `SELECT query, calls, mean_exec_time FROM pg_stat_statements ORDER BY total_exec_time DESC LIMIT 30;`
and `EXPLAIN (ANALYZE, BUFFERS)` on policy / invoice / claim lookups by party and by policy.
