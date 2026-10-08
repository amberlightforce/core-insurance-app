Runs on Sonnet 5.5 (model: sonnet), D-USR-16.

# Brief SL5-UI-PACKS — pack registry with rollback, and the POL rollback-exception queue (wave 2)

Wave 2, **PARALLEL-NOW** on the typed contracts. The live walk comes after SL5-MKT-ROLLBACK and SL5-POL-ROLLBACK merge.
Review: light, plus a **visual review** (D-USR-11). Estimate: 3 h. Meaningful PR.

- **WP / PRD:** SL5-UI-PACKS.
  - Requirements:
    - PRD-17 SCR-MKT-03 pack registry (subset), REQ-MKT-295 (subset): versions, active version per entity,
      activation history with rolled-back versions in `warning` and the `PackRolledBack` window; Roll back opens a
      request with preview and needs a checker;
    - PRD-05 REQ-POL-354 (UI subset): the exception queue and review.
  - Sources: `../core-insurance-prds/PRD-17-multi-market-country-packs.md` SCR-MKT-03 (around lines 899–915); digests
    `PRD-17.md` §screens, `PRD-05.md`.
  - Decisions: D-SL5-08, D-SL5-10, D-SL5-12, D-USR-11/13.
- **Visual reference:** the Aegean mockup v3 has **no pack or exception screen**. Follow the **claims approvals
  inbox** pattern as built in `web/src/modules/claims/` (list + detail + decision bar), with the record PageHeader and
  Section cards. Take side-by-side screenshots of the approvals inbox vs your screens (light/dark, desktop and about
  840 px) into `orchestration/ux/sl5-ui-packs/`.
- **Scope:**
  1. **Pack registry** (`/admin/packs`, nav entry for users with `mkt.Pack.list`):
     - a pack list (GR, CY stub) with the active version per entity;
     - a version timeline (Published, Active, Superseded; a **rolled-back** version in the warning style with its
       window and the hashes issued, mono, copyable);
     - activation history (kind, from → to, requested by, decided by, time, resulting hash).
  2. **Roll back…** (Platform.ReleaseManager):
     - choose the target version and type a reason (≥ 20 chars, counter shown);
     - **Preview** calls the dry-run and shows from/to, window, hashes issued and the key diff (added/removed/changed,
       with the 8 `tax.treatment.rule.*` keys in the E2E case);
     - **Submit** creates the request (PendingApproval).
     - The same dialog serves "Activate version…" (`scheduleActivation`).
  3. **Approve/Reject** (Platform.DesignAuthority) on a pending activation:
     - the reason is required;
     - the maker sees the request read-only with "waiting for a checker";
     - a self-approval attempt shows the server's SoD error in plain language.
  4. **Exception queue** (`/policies/pack-rollback`, nav entry for users with `pol.PackRollbackException.list`):
     - a list filtered Open/Reviewed, with policy number, transaction kind, product version, configuration hash
       (short), pack from → to, identified at;
     - detail with "WRK activity: not created (WRK not built)";
     - **Review…** with outcome NO_ACTION / CORRECTION_REQUIRED and a required reason;
     - an explanatory note that corrections (re-rating) are not available yet (D-SL3-02). No "re-rate" button.
  5. Greek first and English complete. Loading, empty, error and no-permission states.
- **Depends on / provides:**
  - **Depends on:** SL5-CONTRACTS-PACKS (generated types and samples → MSW handlers).
  - Live walk with `releasemgr`, `designauth` and `uwsenior` after SL5-MKT-ROLLBACK and SL5-POL-ROLLBACK merge (the
    orchestrator tells you); report what could and could not be verified.
- **Files you own:** `web/src/modules/market/packs/**` (new), `web/src/modules/policy/packRollback/**` (new), their own
  i18n namespaces and `api.ts` files.
- **Shared:** `web/src/routes.tsx` (append two routes), the staff navigation rail (append).
- **Do not touch:** `web/src/modules/policy/file/**` and `policy/servicing/**` (slice 3),
  `web/src/modules/underwriting/**` (SL5-UI-UW-WB), the design system.
- **Tests (vitest + axe, `--maxWorkers=2`):**
  - each state;
  - the preview is required before submit;
  - the reason length rule;
  - maker vs checker views;
  - a rolled-back version marked with its window;
  - exception review;
  - no render loop (PITFALLS 26);
  - no personal data or hashes of personal data in URLs or storage. Only ids and version strings may appear in the URL.
- **PITFALLS to self-check (likely):** 18, 19, 25, 26, 27, 28.
- **Workflow:** `briefs/sl5/_COMMON.md`. PR title:
  `SL5-UI-PACKS: pack registry with rollback, pack-rollback exception queue`. Do not merge.
