Runs on Sonnet 5.5 (model: sonnet), D-USR-16.

# Brief SL5-PLT-ROLES — release-manager and design-authority roles and dev users (wave 1, gated, batched)

Wave 1, **GATED on the merge of SL3-PLT-SUPPORT**, which owns the dev-users file in slice 3. Do not start before the
orchestrator confirms the gate. Review: light, with the security checklist. Estimate: 1 h. **Batched** (D-USR-18): push
your branch and report; do not open a PR. The orchestrator folds it into a `batch/<name>` PR, normally with
SL5-CONTRACTS-PACKS.

- **WP / PRD:** SL5-PLT-ROLES.
  - Requirements: PRD-17 ROLE-40 release manager (maker for pack activation and rollback), with the design authority
    or country product owner as checker (PRD-17 §12 authority table `MKT_PACK_ACTIVATION`); REQ-MKT-137 (maker-checker
    for every activation and rollback); PRD-02 §12.3 (fall-back checker).
  - Decisions: D-SL5-08, D-SL5-09, D-USR-13 (dev superuser keeps SoD), D-PRG-21 (dev users in their own file).
- **Scope:**
  1. Add the roles **Platform.ReleaseManager** and **Platform.DesignAuthority** to the role catalogue, in the same
     place and format as the existing roles (find where `Staff.BillingManager` from SL3-PLT-SUPPORT was added).
  2. Add the dev users in `src/CoreIns.Host/dev-users.Development.json` (append):
     - `releasemgr`, "Dev Release Manager (synthetic)", [Platform.ReleaseManager];
     - `designauth`, "Dev Design Authority (synthetic)", [Platform.DesignAuthority].
     - Display names must be unique, and no name may contain another (Playwright selects by exact name, PITFALLS 30).
  3. `superuser` gains both roles. SoD is unchanged: superuser still cannot check its own request.
  4. Development only, like every dev user. They are never available in Production; the existing startup test covers
     it, so extend it if it lists names.
  5. Do **not** register approval types or authority types. MKT and PFC register their own in their module files
     (SL5-MKT-ROLLBACK, SL5-PFC-FALLBACK).
- **Files you own:** `src/CoreIns.Host/dev-users.Development.json` (append only), the role catalogue file (append only),
  the dev sign-in test file (new cases).
- **Do not touch:** `Program.cs`, `appsettings*.json`, any module.
- **Tests:**
  - dev sign-in lists the two new users with the right roles;
  - superuser has both roles;
  - in a Production-environment host the dev users are not registered.
- **Security checklist (reviewer):**
  - no Production exposure;
  - no role grants another module's permission by accident: the new roles are in no `permissions/*.json` yet, and the
    module WPs add them;
  - superuser SoD is unchanged.
- **PITFALLS to self-check (likely):** 4, 5, 30.
- **Workflow:** `briefs/sl5/_COMMON.md` (batched variant): merge `origin/main`, quick gate, push the branch, report.
  Do not open a PR.
