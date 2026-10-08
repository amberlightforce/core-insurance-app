# Brief SL4-BIL-RECV — receivables for claim recoveries and FS net receivables (wave 2)

Runs on Sonnet 5.5 (model: sonnet), D-USR-16.

Wave 2, **own PR**. Review: **deep (money)**. Estimate: 3.5 h.

**Gate:** start only after **SL3-BIL-REFUND has merged**. It is slice 3's last BIL WP and the BIL migration owner in
slice-3 S2. Run `git merge origin/main` and confirm that its migration is in.

You are the **BIL migration owner for slice-4 wave 2**. SL4-BIL-FSOUT, which follows you, adds no migration, so cover
its needs too: the source-register rows and the release-approval columns on disbursements.

- **WP / PRD:**
  - REQ-BIL-346 (receivables from CLM for non-premium amounts; salvage, and subrogation per D-SL4-06);
  - REQ-BIL-354 (source register rows: `CLM_CLAIM_PAYMENT` gains direction "in"; `FS_CLEARING` in/out with method
    `CLEARING`);
  - REQ-BIL-356 (FS net receivable on a CLEARING account, matched by statement reference, `CashAllocated` with the
    reference);
  - REQ-BIL-125/-127 (subset: matching a receipt by payment reference);
  - REQ-BIL-286 (ledger rules as data).

  Sources: `orchestration/digests/PRD-06.md` (disbursement/receivable rows, LA accounts incl. LA-24) and the full
  PRD-06 rows 346, 354–357. Decisions: **D-SL4-06, D-SL4-02**, D-SL2-10, D-ARC-34.
- **Scope:**
  1. **`bil.Receivable.register`** (typed by SL4-CONTRACTS):
     - **Sources:** `CLM_CLAIM_PAYMENT` with purpose SALVAGE | SUBROGATION, or `FS_CLEARING` with purpose FS_NET. Any
       other source or purpose → `BIL-ERR-SOURCE`.
     - **Caller check:** only CLM may register these sources (in-process caller module), never a client.
     - **Billing account:** find or create the counterparty's account, with type `CLAIM_RECOVERY` for a CLM source or
       `CLEARING` for FS. The payer is the counterparty party (organisation or person).
     - **Open item:** create one with a **payment reference** (the existing RF generator) and a due date, with no
       charge deltas and **no fiscal request** (D-SL4-06).
     - **Production:** refuse `CLM_CLAIM_PAYMENT` receivables (`BIL-ERR-FISCAL-TREATMENT-OPEN`). `FS_CLEARING` follows
       the FS rule (the stub is unbound, so CLM never gets that far).
     - **Duplicates:** the key is source type + source id + purpose + counterparty + amount (PITFALLS 9). A replay with
       the same Idempotency-Key returns the same receivable.
     - **Entries:** sealed `RECEIVABLE_REGISTERED` entries plus `BillingEntryPosted` with claim id, recovery id,
       statement ref and counterparty dims (for FS through LA-24).
  2. **Receipt matching:**
     - `bil.Payment.take` with a `paymentReference` (or `receivableId`) allocates to the receivable item;
     - an exact amount closes it; less leaves the rest open; more → the surplus goes to the account's unapplied credit
       (existing behaviour; report what it does).
     - Publish **`CashAllocated` with receivable id, claim id, recovery id, statement ref and source type**, always set
       when present (PITFALLS 12).
     - Sealed `RECEIVABLE_COLLECTED` entries.
  3. **`bil.Receivable.get/list`:** by id; by claim id; by counterparty account. Masked, with no personal data beyond
     the party id.
  4. **Source register rows** (REQ-BIL-354) as configuration:
     - `CLM_CLAIM_PAYMENT` direction both;
     - `FS_CLEARING` direction both, methods [`CLEARING`], evidence type "CLM FS net approval", ledger rows via LA-24.

     Add the `FS_CLEARING` **out** row now, but leave its request path to SL4-BIL-FSOUT. Until then, an
     `FS_CLEARING` disbursement request must still be refused (`BIL-ERR-SOURCE`) by a guard that FSOUT removes.
  5. **Migration:** receivable table/columns, the account type values, the register rows (if stored), and the
     disbursement columns FSOUT needs (`statement_ref`, `lines` jsonb, `release_approval_request_id`,
     `release_approved_by`).
- **Depends on / provides:**
  - **Depends on:** the merged SL3-BIL-REFUND; contracts.
  - **Provides:** receivables for SL4-CLM-RECOVERY-OPS and SL4-CLM-FS-STATEMENT; `CashAllocated` for both; the schema
    for SL4-BIL-FSOUT.
- **Files you own:**
  - `src/CoreIns.Modules.Billing/Commands/Receivables*` (new), `Services/ReceivableMatching.cs` (new),
    `Queries/Receivable*` (new), `Api/Receivable*` (new);
  - `Persistence/**` (migration owner, wave 2);
  - the source-register configuration;
  - `tests/CoreIns.IntegrationTests/Billing/Receivables/**`.
- **Shared (append only / small hooks):**
  - `Events/IntakeHandlers.cs` (no change expected);
  - `Commands/Payments.cs` (one matching hook; keep it minimal and list it);
  - the BIL permission file.
- **Tests:**
  - register salvage 1,700 → pay by reference → `CashAllocated` with all refs, item Paid, entries sealed and balanced
    in the sub-ledger;
  - subrogation 200,000 partial payment 150,000 → 50,000 open;
  - FS net receivable registered on a CLEARING account and matched by statement ref;
  - a client calling register directly → 403 (only CLM, in-process);
  - a second identical register with a new key → `BIL-ERR-DUPLICATE`;
  - Production refuses CLM receivables;
  - an `FS_CLEARING` disbursement is still refused (the FSOUT guard);
  - app role cannot UPDATE entries;
  - no IBAN or name in events (PITFALLS 18, 20).
- **PITFALLS to self-check (likely):** 4 (in-process caller only), 8, 9, 10, 11, 12, 18, 20.
- **Workflow:** follow `briefs/sl4/_COMMON.md`. Merge main, run the quick gate, push, then run
  `gh pr create --base main --title "SL4-BIL-RECV: claim and FS receivables, matching, CashAllocated refs"`. Do not
  merge.
