# Brief SL4-UI-RI — reinsurance treaty registry screens (wave 1)

Runs on Sonnet 5.5 (model: sonnet), D-USR-16.

Wave 1 (starts when SL4-CONTRACTS has merged), **own PR**. Review: light + visual (side-by-side with the mockup).
Estimate: 3.5 h.

- **WP / PRD:**
  - SCR-RI-02 (programme designer with layer diagram), SCR-RI-03 (contract editor), SCR-RI-04 (participation panel):
    subsets for a per-risk XoL treaty;
  - REQ-RI-001, -037, -046, -047, -057 (UI side).

  Sources: `orchestration/digests/PRD-08.md` §8 and the full PRD-08 SCR-RI-02/03/04 field tables (Greek labels given
  there, † = working translation). Decisions: D-SL4-04, D-SL4-16.
- **Scope** (`web/src/modules/reinsurance/**`, new):
  1. **Treaties list** («Συμβάσεις αντασφάλισης»):
     - number, treaty year, type, period, layer summary ("500.000 xs 250.000"), status pill, placed %;
     - filter by status;
     - landing PageHeader.
  2. **Treaty editor** (Staff.ReinsuranceAccountant), for Draft only:
     - period, year, scope (product, coverages from PFC catalogue reads), clause flags;
     - layers (attachment, limit, AAD, AAL);
     - participations (reinsurer organisation picker via party search, signed line %, lead), with a live Σ signed
       lines vs placed % check.
     - A **layer diagram**: a simple stacked bar of attachment/limit in design-system tokens; no chart library unless
       the design system has one.
     - Submit with a confirmation.
     - Money uses BigInt minor units, no float maths.
  3. **Treaty detail** (record PageHeader, Section cards in the «Φάκελος ζημίας» pattern):
     - a stage strip Draft → Submitted → Approved → Active → Expired;
     - terms, layers, participations, approval history;
     - for Staff.ReinsuranceManager: approve / return with a reason. Hide the actions for the enterer and explain the
       SoD refusal in plain Greek/English if the server returns `RI-ERR-SOD` (PITFALLS 27).
  4. Routes and nav entry «Αντασφάλιση» (append to `routes.tsx` and the staff nav list), permission-aware.
  5. MSW fakes built from the generated samples. Live walk against the real API after SL4-RI-REGISTRY merges.
- **Design:**
  - Aegean mockup v3 (`../core-insurance-prds/design-guide/mockups/aegean-screens-v3.html`). There is no RI screen in
    the mockup, so use its record pattern (the claim file «Φάκελος ζημίας»: header facts, stage strip, right-column
    money card → "Layers" card) and the approvals inbox pattern for the decision bar.
  - Greek-first, EN switch; empty, loading, error and no-permission states.
  - Side-by-side screenshots (light/dark, desktop/narrow) in `orchestration/ux/sl4-ui-ri/`.
- **Files you own:**
  - `web/src/modules/reinsurance/**` (incl. its own i18n namespace files and `api.ts`).
  - **Shared (append only):** `web/src/routes.tsx`, the staff nav list.
- **PITFALLS to self-check (likely):**
  - 25 (mockup);
  - 26 (memoise rows/columns; no fresh array literals into DataTable; page answers within 2 s);
  - 27 (required inputs; server errors explained with "go to field");
  - 28;
  - 19 (no party names in browser storage).
- **Workflow:** follow `briefs/sl4/_COMMON.md`. Merge main, run the web quick gate, push, then run
  `gh pr create --base main --title "SL4-UI-RI: reinsurance treaty registry screens"`. Do not merge.
