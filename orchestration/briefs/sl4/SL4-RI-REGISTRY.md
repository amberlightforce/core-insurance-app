# Brief SL4-RI-REGISTRY — reinsurance module foundation and XoL treaty registry (wave 1)

Runs on Sonnet 5.5 (model: sonnet), D-USR-16.

Wave 1, **own PR**. Review: **deep (security + temporal)**. Estimate: 4 h.

- **WP / PRD:** SL4-RI-REGISTRY. Requirements (subset for a per-risk XoL treaty):
  - REQ-RI-001 (registry API);
  - REQ-RI-030 / -031 / -032 (programme-less contract, XoL type only);
  - REQ-RI-037 / -038 (validation);
  - REQ-RI-046 / -047 (participations, Σ signed lines = placed %);
  - REQ-RI-056 / -057 / -058 (lifecycle, maker-checker on the content hash, activation at the period start, Athens);
  - REQ-RI-065 (no deletion of referenced rows);
  - REQ-RI-231 (stable treaty id + contract year).

  Sources: `orchestration/digests/PRD-08.md` §3 (RIContract, RIContractVersion, RISection, RILayer, ClauseSet,
  RIParticipation, state machine) and §11 (authority, SoD). Decisions: **D-SL4-04, D-SL4-16**, D-SL2-05/06 patterns.
- **Scope:**
  1. **Module foundation.**
     - `ri` schema, `ReinsuranceDbContext` on the shared `DbSession`, and the first RI migration (you are the **RI
       migration owner for wave 1**).
     - Register the context in the migrate job's list (`src/CoreIns.Host/Database/**`, append one line).
     - Write the permission file `src/CoreIns.Host/permissions/ri.json` (new).
     - Follow `docs/module-pattern.md` exactly (commands through the platform pipeline, outbox, audit).
  2. **Data model (minimal, ready for versioning):**
     - **contract:** number from `INumberingService`, series `RI_CONTRACT`, gapless per legal entity; stable treaty id;
       contract year; type; status; legal entity; jurisdiction; `record_version`.
     - **contract_version:** v1 only; valid period + `known_from/known_to`; content hash; approval request id. The
       exclusion constraint on the valid period per contract is present now. It is immutable once Approved: a trigger
       refuses UPDATE of content columns.
     - **section**, with the scope (product codes, coverage codes).
     - **layer:** attachment, limit, aad, aal; NUMERIC.
     - **clause:** ALAE included, interest included, recoveries inure = REALISED_ONLY.
     - **participation:** reinsurer party id, an organisation in PTY (validated through the PTY contract); broker
       party?; signed line %; lead flag. Exactly one lead per layer, and Σ signed = placed % (`RI-ERR-SIGNED-LINES`).
     - EUR only (D-SL4-04): any other currency → `RI-ERR-VALIDATION`.
  3. **Lifecycle:**
     - Draft → Submitted → Approved → Active → Expired, and Submitted → Draft (returned with a reason).
     - Approval:
       - goes through a **PLT approval request created in-process** by RI (`RI.CONTRACT_APPROVE`, register the
         authority type in `Modules.Reinsurance/Authority/`);
       - is bound to contract + type + content hash;
       - a checker must not be the enterer, an editor or the submitter (PITFALLS 3–5), else `RI-ERR-SOD`;
       - is verified with `verifyForExecution`.
     - **Activation:**
       - when approved with a period start ≤ now (Athens, `IClock`), activate in the same transaction;
       - otherwise a Hangfire scanner activates at the start, idempotently;
       - publish `RIContractActivated` through the outbox.
     - **Expiry** at the period end by the same scanner.
     - A racing approve or update → `RI-ERR-STALE` (409), never 500 (PITFALLS 15).
  4. **API:**
     - `ri.Contract.create/update/submit/approve/get/list/applicable`, as typed by SL4-CONTRACTS;
     - `applicable(lossAt, productCode, coverageCode)` returns the Active contracts whose period contains the loss
       instant (half-open, Athens dates) and whose scope matches. SL4-RI-RECOVERY uses it.
     - Permissions:
       - Staff.ReinsuranceAccountant creates and submits;
       - Staff.ReinsuranceManager approves;
       - claims roles may read `applicable` and `get`.
     - Roles arrive with SL4-PLT. Until that merges, test with role strings; the permission file is yours.
- **Depends on / provides:**
  - **Depends on:** contracts (start on persistence before they merge).
  - **Provides:** storage and `applicable` for SL4-RI-RECOVERY; the API for SL4-UI-RI.
- **Files you own:**
  - `src/CoreIns.Modules.Reinsurance/Persistence/**`, `Registry/**`, `Authority/**`, `Api/Contracts*`,
    `ReinsuranceModule.cs`;
  - `src/CoreIns.Host/permissions/ri.json`;
  - `tests/CoreIns.IntegrationTests/Reinsurance/Registry/**`.

  Do **not** create `Domain/Recovery/**` (SL4-RI-ENGINE owns it).
- **Shared (append only):** the migrate-job DbContext list.
- **Tests:**
  - create → submit → approve by another user → Active; approve by the enterer → `RI-ERR-SOD`;
  - approval content changed after submit → stale;
  - signed lines ≠ placed % → refused; two leads → refused;
  - a non-EUR contract → refused;
  - activation in the future happens when the scanner runs after a clock move (use `IClock` test double);
  - `applicable` respects the half-open period and the scope;
  - an app-role test that the approved version cannot be updated;
  - concurrent approve → one wins, the other gets 409;
  - no personal data in `RIContractActivated`.
- **PITFALLS to self-check (likely):**
  - 3, 4, 5 (approvals in-process, bound, SoD);
  - 8 (seal/immutability trigger, app-role test);
  - 13, 14 (period boundaries, Athens);
  - 15;
  - 17.
- **Workflow:** follow `briefs/sl4/_COMMON.md`. Merge main, run the quick gate, push, then run
  `gh pr create --base main --title "SL4-RI-REGISTRY: RI module and XoL treaty registry"`. Do not merge.
