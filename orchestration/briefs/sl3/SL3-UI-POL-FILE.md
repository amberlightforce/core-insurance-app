# Brief SL3-UI-POL-FILE — policy file: terms, transaction history, charges, actions (S1)

**Runs on Sonnet 5.5 (model: sonnet), D-USR-16.** Batch S1. Review: light, plus a **visual review** against the
mockup pattern (D-USR-11). Estimate: 3 h.

- **WP / PRD:** SL3-UI-POL-FILE.
  - Requirements: PRD-05 §6 SCR-POL-10 (policy file summary, subset), SCR-POL-11 (contract sections as-of, with the
    Charges section, subset), SCR-POL-12 (transaction history grouped by term, subset); REQ-POL-128 (charge history per
    transaction), REQ-POL-132 (display status from terms, incl. a pending renewal flag), REQ-POL-002 (as-of display with
    `effectiveKnownAt`).
  - Digest `orchestration/digests/PRD-05.md` §8; design digests `DESIGN-A.md` / `DESIGN-B.md`.
  - Decisions: D-USR-11, D-USR-13, D-SL3-03 (show "as known at"), D-SLC-13, D-SLC-21 (c) (single-click row open).
- **Visual reference:** `core-insurance-prds/design-guide/mockups/aegean-screens-v3.html`. It has **no policy screen**,
  so follow the **«Φάκελος ζημίας» (claim file) pattern** as built in `web/src/modules/claims/ClaimViewPage.tsx`:
  - the record PageHeader;
  - the claim's stage strip → a **term timeline** (term 1 … n, states Scheduled/InForce/Cancelled/Expired, with the
    renewal term);
  - the money card → a **term premium card** (written, credits, billed/paid link to billing);
  - the history list → a **transaction history** (kind badge: New business / Change / Cancellation / Renewal;
    effective; recorded; premium change; expandable charges per element × charge type with the `provisional` tax
    badge).
  - Take side-by-side screenshots (claim file vs policy file, light/dark, desktop/≈840 px) into
    `orchestration/ux/sl3-ui-pol-file/`.
- **Scope:**
  - Rework `PolicyViewPage.tsx` into the policy file layout above.
  - An as-of control: valid date, and "as known at" shown read-only from `effectiveKnownAt`.
  - An **action bar**: Change / Cancel / Renew now. Each is shown only when the user has the permission and the term
    state allows it; otherwise the button is hidden or disabled with a reason. The actions link to routes that
    SL3-UI-POL-JOBS builds (`/policies/:id/change`, `/cancel`, `/renew`); until then, add the routes with a placeholder
    page. **Only you add these routes.**
  - A supersession badge when a viewed snapshot is superseded.
  - Greek-first, EN complete; loading, empty, error and no-permission states.
  - Money in BigInt minor units.
- **Depends on / provides:**
  - **Depends on:** contracts only. Build against MSW handlers seeded from the generated contract samples (pattern of
    the claims module tests). Do a live walk against the real stack after POL S1 merges (the orchestrator tells you);
    report what you could and could not verify.
  - **Provides:** the entry points that SL3-UI-POL-JOBS fills.
- **Files you own:** `web/src/modules/policy/file/**` (new: components, `api.ts`, tests), `PolicyViewPage.tsx`,
  `web/src/modules/policy/i18n/file.*` (own namespace).
- **Shared (append only):** `web/src/routes.tsx`.
- **Do not touch:** `policy/servicing/**` (SL3-UI-POL-JOBS), `quote/**`, `policy/api.ts` (create `file/api.ts`).
- **Tests:**
  - vitest + axe for each state;
  - history ordering and grouping by term;
  - permission-aware actions;
  - **no render loop**: memoised rows and columns, and the page answers within 2 s in a real browser;
  - no personal data in URLs or storage.
- **PITFALLS to self-check (likely):** 18, 19, 25, 26, 27, 28.
- **Workflow:** `briefs/sl3/_COMMON.md`: merge main, quick gate (`npm run lint && npm run typecheck`, touched vitest
  files with `--maxWorkers=2`, `npm run format:check`), push,
  `gh pr create --base main --title "SL3-UI-POL-FILE: policy file (terms, history, charges, actions)"`, do not merge.
  Put the screenshots in the PR body.
