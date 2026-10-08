Runs on Sonnet 5.5 (model: sonnet), D-USR-16.

# Brief SL5-UI-UW-WB — the «Ανάληψη κινδύνου» referral workbench screen (wave 1)

Wave 1, **PARALLEL-NOW**. Review: light, plus a **visual review** against the mockup (D-USR-11/13). Estimate: 4 h.
Meaningful PR.

- **WP / PRD:** SL5-UI-UW-WB.
  - Requirements: PRD-04 SCR-UW-01 underwriter workbench (subset: sidebar work views with counts, queue, split pane
    with brief / fact sheet / decision panel, keyboard list navigation IB-02, sidebar counts IB-03, split pane IB-05)
    and SCR-UW-03 issue decision (subset: required authority vs yours, outcome, approve/reject with mandatory reason).
  - Sources: digest `orchestration/digests/PRD-04.md` (screens, IB patterns); design digests `DESIGN-A.md`/`DESIGN-B.md`.
  - Decisions: D-USR-11, D-USR-13, D-SL5-03, D-SL5-04, D-SL5-05, D-UW-01, D-SLC-21 (c) (single-click row open).
- **Visual reference (binding):** `core-insurance-prds/design-guide/mockups/aegean-screens-v3.html`, screen `#s-uw`
  (around lines 689–724 for the markup and 912–1011 for the behaviour). Match it:
  - a three-column layout: views rail (`nav.views`), the queue sheet, and the detail sheet;
  - the queue header with chips;
  - the grid rows (name in bold, id · product);
  - the detail head (overline, h2, mono id, the «Σε παραπομπή» pill, facts row);
  - issue cards with the compare row and «Γιατί;»;
  - the kv card;
  - the decision bar with the authority meter, the buttons and the receipt;
  - the empty-state illustration;
  - the motion on approve (row leaving, pill morph). Respect `prefers-reduced-motion`.
- **What to build and what to leave out (D-SL5-03; never fake a feature):**

  | Mockup | Build |
  |---|---|
  | «Οι παραπομπές μου» / «Ομάδα» / «Ολοκληρωμένες σήμερα» | Views MINE / OPEN / DECIDED_BY_ME_TODAY with counts, plus «Απορρίφθηκαν» (REJECTED) in the same style |
  | «Λήγουν σήμερα», «Με όρους σε εκκρεμότητα» | **Omit** (no SLA, no conditions) |
  | «Πρόοδος ημέρας» | decided-by-me-today ÷ (that + mine) from the counts |
  | Columns Πελάτης / Προτεραιότητα / SLA / Ασφάλιστρο | Πελάτης / **Αιτία** (first reason label + "+n") / **Σε αναμονή από** (relative, from `raisedAt`) / Ασφάλιστρο; checkbox column **omitted** |
  | AI summary card, sanctions card, «Ο παραγωγός βλέπει» | **Omit** |
  | Issue card value vs «όριο κανόνα» | `observed` vs `limit`; when `limit` is null, show "—" with the reason tooltip; rule id in mono |
  | kv card | product/version, claims last 5 years, vehicle age · usage · cc, youngest driver age band, start date, producer code |
  | Decision bar | Authority line from `decidability.authority`, meter full/green on ALLOW, red on DENY/REFER; **Απόρριψη…** and **Έγκριση…** open an inline reason field (required); **Ctrl+Enter** submits only with a reason; when `canDecide` is false both are disabled and the bar shows the server's reason in plain Greek/English (e.g. «Δημιουργήσατε εσείς αυτή την προσφορά») |
  | «Έγκριση με όρους…» | **Omit** |
  | Receipt + toast with undo | Receipt «Απόφαση: <name> · <time>» from the decision; toast **without** undo |

- **Data:**
  - `uw.Referral.list?queue=…` (cursor paging; the counts come with every page) and `uw.Referral.get`.
  - Decide with the existing `uw.Issue.decide`, through an idempotency key (`useIdempotencyKey`), one issue at a
    time or all open issues of the referral in one call, as the API allows.
  - After a decision, invalidate both queries.
  - Problem details: SoD/authority errors from the server are explained (FIX-403 banner pattern); codes go only under
    "technical details" (PITFALLS 27).
- **Routing:**
  - Replace the element of the existing `policies/referrals` route in `web/src/routes.tsx` with the new workbench page.
    Change that one line only. Leave `modules/policy/ReferralsPage.tsx` in place; the orchestrator removes it later.
  - Add the «Ανάληψη κινδύνου» entry with the mockup's scales icon to the staff navigation rail (append). Show it only
    with `uw.Referral.list` permission.
  - Users without the permission get the no-permission state.
- **Depends on / provides:**
  - **Depends on:** contracts only.
    - Start at once on the WIP branch's generated `web/src/api/generated/uw.d.ts`
      (`git show worktree-agent-ac9c66400c2402b05:web/src/api/generated/uw.d.ts`).
    - Rebase on SL5-UW-WB-API's first contract commit (queue MINE, `decidability`, `observed`/`limit`) when the
      orchestrator says it is pushed.
    - Build against MSW handlers seeded from the generated samples (the claims module test pattern).
    - Do a **live walk** against the real stack after SL5-UW-WB-API merges. Use `uwsenior` on a quote created by
      `underwriter` with a 1991 vehicle, and `superuser` on its own quote (must be disabled with the SoD reason).
- **Files you own:** `web/src/modules/underwriting/**` (new: `workbench/` components, `api.ts`, hooks, tests),
  `web/src/modules/underwriting/i18n/*` (own namespace).
- **Shared:** `web/src/routes.tsx` (the one line), the staff navigation rail (append one entry).
- **Do not touch:** `web/src/modules/policy/**` (slice 3 owns `file/`, `servicing/`), `quote/**`, `claims/**`, the
  design system. If a design-system component is missing, compose it locally and report it.
- **Tests (vitest + axe, `--maxWorkers=2`):**
  - each view with its count and the empty state;
  - row selection by click and keyboard (↑/↓/Enter);
  - decision with a reason (Ctrl+Enter blocked without one);
  - disabled decision with the SoD reason;
  - DENY authority;
  - a server 403/409 explained;
  - **no render loop**: memoised columns, rows and query keys, and no fresh array literal into DataTable. In a real
    browser, check that the page answers within 2 s after each navigation (PITFALLS 26);
  - no personal data in URLs or browser storage. Only opaque job ids may appear in the URL (PITFALLS 18–19).
- **Visual review:** screenshots of mockup vs app — the views rail, queue, detail and decision bar, the empty state,
  light/dark, desktop and about 840 px — go into `orchestration/ux/sl5-ui-uw-wb/` and the PR body. List every
  deliberate difference (the omitted features above).
- **PITFALLS to self-check (likely):** 18, 19, 25, 26, 27, 28, 30.
- **Workflow:** `briefs/sl5/_COMMON.md`. PR title: `SL5-UI-UW-WB: Ανάληψη κινδύνου referral workbench`. Do not merge.
