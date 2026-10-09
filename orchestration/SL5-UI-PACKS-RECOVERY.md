# SL5-UI-PACKS recovery checkpoint

Draft PR #51, branch `wp/sl5-ui-packs`; local worktree `agent-a45575c6160f065bc`. Interrupted work preserved first in d6a705b; no resets or lost edits. Latest main merged before publishing. Unrelated generated BIL/CLM/PFC/PLT churn was restored from main after preserving the original snapshot; only consumed MKT/POL typing remains in this frontend change.

This is a reviewable frontend checkpoint, not slice-5 completion. It registers guarded registry/detail/decision and exception queue/detail routes, uses shared Aegean PageHeader/Section/pattern components, supplies Greek and English, and adds cursor pagination. Request reasons require 20–128 characters; preview must match the exact current input before a real request can be submitted. Maker checks compare the server-derived actorKey; absent/unverified identity leaves decisions read-only. Native POL transaction-kind labels are used. Review records an outcome/reason without re-rating, and states that WRK is not built.

| Requirement | Evidence |
|---|---|
| Registry/loading/empty/error/no permission | Pack focused tests exercise loading, retry, empty and unrelated-role guard; actual fixture routes render |
| Version lifecycle/rolled-back window/hash | Focused test verifies old version's warning state, window and hash; fixture detail and history captures |
| Preview before submit; reason 20+; edited input invalidates preview | Focused dialog regression verifies dryRun, all 8 diff keys, distinct idempotency key and explicit real request |
| Maker/checker; unknown identity read-only | Native-shaped USER:dev:actorKey fixtures plus own maker/malformed/missing key cases; required decision reason/POST test |
| Exception queue and review | Required outcome/reason, exactly one review POST, no re-rate button and no extra write; native ISSUANCE translated |
| Cursor lists | Explicit load-more controls; next-page exception regression retains first page and OPEN filter |
| Visual/Greek/English | Thirty fixture PNGs and side-by-side claims inbox comparisons in ux/sl5-ui-packs; producer checks only |

Local validation: ESLint, CSS and i18n checks pass; typecheck and full format check pass; both focused Vitest files pass 14 tests with maxWorkers=2. Final wording-only Greek warning correction passed i18n and was recaptured. GitHub runs the full suite on the draft PR; root owns review/merge. No native pack acceptance is claimed.

Applicable PITFALLS: 3–6 (only owning-module endpoints; no client authority; server remains authoritative for SoD),18–19 (ids/version strings in URLs; reasons in request bodies; no domain data in browser storage),21–24 (generated consumed contracts, no normative interface change),25–28 (Aegean components, stable infinite-query selectors, explicit required inputs and translated errors),33–35/45 (two workers, early commits, isolated stopped Vite and verified explicit push). Other ledger/database/temporal guards are backend-owned; this UI does not implement them.

Dependencies/open review: SL5-MKT-ROLLBACK and SL5-POL-ROLLBACK must provide native operations and be accepted with release-manager/design-authority/underwriting-manager users. MKT activation history currently exposes only initial resultingHash; dry-run preview alone supplies all hashesIssued in its window. An additive history window/hash contract is needed to show every historical issued hash. Maker actorKey serialization must match the native PLT/RI convention. Independent visual acceptance and full CI completion remain distinct from these fixture checks. No other programme package was started.

