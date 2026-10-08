Runs on Sonnet 5.5 (model: sonnet), D-USR-16.

# Brief SL5-PFC-FALLBACK — product fall-back version (REQ-PFC-213): MOTOR-GR 1.1 → 1.2 = copy of 1.0 (wave 1)

Wave 1, **PARALLEL-NOW** (SL3-PFC-MOTOR11 has merged; no other slice-3/4 PFC WP is planned). Review: **deep
(config/temporal + security: maker-checker)**. Estimate: 3 h. Meaningful PR.

- **WP / PRD:** SL5-PFC-FALLBACK.
  - Requirements:
    - REQ-PFC-213: fall-back for a defective Locked version, by closing it to new business and re-opening the
      predecessor's windows through a minor version of the predecessor, without altering terms already written;
    - REQ-PFC-033: no overlapping new-business windows; publishing sets the predecessor's NB end in the same
      transaction;
    - REQ-PFC-166 and REQ-PFC-178: windows and close to new business;
    - PRD-02 §12.2 authority `PFC.EmergencyChange` and §12.3 maker-checker ("fall-back: second release manager or
      head of product").
  - Sources: `../core-insurance-prds/PRD-02-product-factory-configuration.md` (REQ rows above, §7 ProductVersion
    states); digest `orchestration/digests/PRD-02.md`.
  - Decisions: D-SL5-08 (roles), D-SL5-09, D-SL3-04, D-SL3-15, D-SLC-04 (illustrative values).
- **Scope:**
  1. `pfc.ProductVersion.fallback {productCode, defectiveVersion, reason ≥ 20 chars}`, with dry-run, for the maker role
     **Platform.ReleaseManager**.
     - Checks: the defective version is Locked and has a predecessor in the same product, jurisdiction, entity and
       channel scope.
     - The new version number is the **next free minor** (for MOTOR-GR: 1.2; D-SL5-09).
     - The dry-run returns the preview.
     - A real call creates a fall-back request (PendingApproval) and an **in-process** PLT approval request of type
       `PFC.Fallback`. The content hash covers {product, defective, source, newVersion, fallbackDate, reason}.
  2. `pfc.ProductVersion.decideFallback {fallbackId, decision, reason}`.
     - The checker must be a different human, not the maker's principal (PITFALLS 5), with authority
       `PFC.EmergencyChange`.
     - Register the authority type and an **illustrative** grant to Platform.DesignAuthority in the PFC authority file.
     - `verifyForExecution` binds subject + type + content hash (PITFALLS 3).
  3. **On approval, in one transaction:**
     - publish the new version as a **write-once copy of the predecessor's content** (1.0: ACT/365F, no refund
       methods), Locked, with `fallbackOf = <predecessor>` and `replaces = <defective>`;
     - the new-business window starts on the fall-back business date (Europe/Athens, the approval instant's date);
     - the defective version's **new-business** window ends on that date (half-open; no overlap, BR-PFC-001);
     - the renewal window of the defective version is **not** touched (REQ-PFC-166/167);
     - neither the defective version nor the predecessor is edited in content (write-once);
     - publish `ProductVersionPublished` with `fallbackOf` and `replaces` through the outbox, and audit every step.
  4. **Resolution:** `pfc.ProductVersion.resolve` by date gives the new version for term starts on or after the
     fall-back date. Artefacts of 1.0 and 1.1 stay retrievable by hash (REQ-PFC-181/227). Terms already pinned to 1.1
     are never touched.
  5. Idempotency on the request (`Idempotency-Key`). A second fall-back of the same defective version while one is
     pending → `PFC-ERR-FALLBACK-STATE`.
- **Depends on / provides:**
  - **Depends on:** SL5-CONTRACTS-PACKS (types; start at once on the brief's shapes and merge main when it lands), and
    the existing PLT approval service. The roles come from SL5-PLT-ROLES; until it merges, tests use role strings
    directly.
  - **Provides:** `ProductVersionPublished{fallbackOf}`, which SL5-RAT-FALLBACK consumes. The fall-back version has
    **no rating** until RAT activates it; quoting it before that fails closed, as today for a version without an
    artefact. Say so in the report.
- **Files you own:** `src/CoreIns.Modules.Product/**` except `Seed/motor-gr*.json` (**PFC migration owner**: one
  migration for the fall-back request rows and `fallback_of`/`replaces` columns), `src/CoreIns.Host/permissions/pfc.json`,
  the PFC authority file, `tests/CoreIns.IntegrationTests/Product/Fallback/**`.
- **Do not touch:** the 1.0 and 1.1 seed files, RAT, POL.
- **Tests (integration):**
  - Happy path: request → other user approves → 1.2 Locked, a copy of 1.0 (content hash equal to 1.0's content apart
    from version and windows), 1.1 NB end = fall-back date, 1.0 unchanged; `ProductVersionPublished` once.
  - Resolve by date before and after the fall-back date. 1.1 remains resolvable by hash.
  - **Maker approves own → refused.** Maker's principal → refused. A checker without the grant → refused. Approving a
    tampered request (content hash changed) → refused.
  - Dry-run writes nothing.
  - Concurrent approvals → one wins and the other gets 409 (PITFALLS 15).
  - **Window property:** no two Locked versions of the product have overlapping NB windows, before or after.
  - Half-open boundary on the fall-back date, including a DST day (PITFALLS 14).
- **Reviewer focus (deep):**
  - Can a client choose the copy source, the new version number or the window dates? It must not: the server derives
    them (PITFALLS 4, 7).
  - Can the fall-back edit history: change 1.0 or 1.1 rows, or move existing windows other than the NB end?
  - Are the SoD and approval bindings sound?
  - Is the date arithmetic in Athens time?
- **PITFALLS to self-check (likely):** 3, 4, 5, 10, 14, 15, 17, 21, 24.
- **Workflow:** `briefs/sl5/_COMMON.md`. PR title:
  `SL5-PFC-FALLBACK: product fall-back version (REQ-PFC-213) with maker-checker`. Do not merge.
