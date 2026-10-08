# Brief SL4-UI-BIL — receivables and FS_CLEARING release approval screens (wave 2)

Runs on Sonnet 5.5 (model: sonnet), D-USR-16.

Wave 2, **own PR** after the live walk (round 1 on fakes may be **batched**). Review: light + visual. Estimate: 2.5 h.

**Gate:**
- start when SL4-CONTRACTS has merged (MSW fakes from samples);
- live walk after SL4-BIL-RECV (receivables) and SL4-BIL-FSOUT (release approval) merge;
- never edit `web/src/modules/billing/refunds/**` or `InvoicePage.tsx`, which belong to slice 3 (SL3-UI-BIL).

- **WP / PRD:**
  - REQ-BIL-346 / -356 (receivables visible and payable by reference);
  - REQ-BIL-355 / -357 (FS_CLEARING net payable with method CLEARING; release approval, D-SL4-12);
  - REQ-BIL-213 (disbursement status with masked payee).

  Sources: `orchestration/digests/PRD-06.md` screens section; PRD-06 field tables for disbursements (Greek labels).
  Decisions: D-SL4-06, D-SL4-12, D-SL3-14 (Staff.BillingManager).
- **Scope** (`web/src/modules/billing/receivables/**`, new):
  1. **Receivables list** «Απαιτήσεις»:
     - source (Ανάκτηση ζημίας / Φιλικός Διακανονισμός), purpose, counterparty (party id → display via the existing
       party read, masked as elsewhere), claim number link, payment reference, amount, open amount, due date, status
       pill;
     - filters by status and source.
  2. **Receivable detail:** facts, allocation history, and "Καταχώριση είσπραξης" (record a receipt): amount, value
     date and payment reference prefilled. It calls `bil.Payment.take` with the reference. The Idempotency-Key is
     reused on retry. Money uses BigInt minor units.
  3. **FS_CLEARING release approval** for Staff.BillingManager:
     - an inbox list «Εγκρίσεις αποδέσμευσης» following the claims approvals-inbox pattern;
     - the detail shows statement ref, counterparty/clearing office, net amount, claim-level lines (sum shown and
       checked against the net), method `CLEARING`, screening/VoP stub results, and requester;
     - approve / reject with a reason;
     - the requester sees the actions disabled with the SoD reason (PITFALLS 5, 27).
  4. **Disbursement detail** (if a page exists outside `refunds/**`; otherwise a section in your detail page): method
     CLEARING and statement ref.
  5. Routes and nav (append), permission-aware: Staff.Billing reads and records receipts; Staff.BillingManager
     approves releases.
- **Design:**
  - Aegean mockup v3: the record pattern of «Φάκελος ζημίας» for details; the approvals inbox for releases.
  - Greek-first; all states.
  - Screenshots in `orchestration/ux/sl4-ui-bil/`.
- **Files you own:**
  - `web/src/modules/billing/receivables/**` (own i18n namespace, own `api.ts`).
  - **Shared (append only):** `web/src/routes.tsx`, the staff nav list.
- **PITFALLS to self-check (likely):**
  - 18 (no IBAN or AFM in URLs);
  - 19 (opaque ids only in storage);
  - 20 (masked IBAN);
  - 25–28.
- **Workflow:** follow `briefs/sl4/_COMMON.md`. Round 1 (fakes) may be pushed as batched. After the live walk, merge
  main, run the web quick gate, push, then run
  `gh pr create --base main --title "SL4-UI-BIL: receivables and FS release approval"`. Do not merge.
