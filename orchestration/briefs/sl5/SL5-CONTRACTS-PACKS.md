Runs on Sonnet 5.5 (model: sonnet), D-USR-16.

# Brief SL5-CONTRACTS-PACKS — type the pack-rollback contracts: MKT, PFC, POL, events (wave 1)

Wave 1, **PARALLEL-NOW** (land it in a quiet window; see the conflicts below). Review: light (orchestrator + reviewer:
completeness and additivity). Estimate: 2 h. Meaningful PR; the orchestrator may batch SL5-PLT-ROLES onto it
(D-PRG-22).

- **WP / PRD:** SL5-CONTRACTS-PACKS. Type every slice-5 operation and event of SLICE-PLAN-5 §6 except UW, which
  SL5-UW-WB-API types itself.
  - Sources:
    - PRD-17 §9 (`mkt.Pack.*`, REQ-MKT-144) and §8 (`PackActivated`, `PackRolledBack`, REQ-MKT-149);
    - PRD-02 REQ-PFC-213 and the `ProductVersionPublished` event;
    - PRD-05 REQ-POL-354 and the §8.2 `PackRolledBack` consumer row;
    - digests `PRD-17.md` §5–6, `PRD-02.md`, `PRD-05.md`.
  - Decisions: D-API-06/06a, D-SL5-06..10.
- **Scope (all additive; every field a consumer depends on is always set, PITFALLS 12):**
  1. **MKT (`contracts/openapi/mkt.yaml`):**
     - `mkt.Pack.list` and `mkt.Pack.get`: packs → versions {version, status, contentDigest, publishedAt}, the active
       version per legal entity, activation history {activationId, kind, from, to, status, requestedBy, decidedBy,
       activatedAt, resultingHash};
     - `mkt.Pack.rollback` and `mkt.Pack.scheduleActivation`: commands with an `Idempotency-Key` and **dry-run**.
       - Request: {pack, legalEntity, toVersion | version, reason (min 20 chars)}.
       - Dry-run preview: {fromVersion, toVersion, window {from, to}, hashesIssued[], keyDiff[] {key, change
         ADDED/REMOVED/CHANGED}}.
       - Real response: {activationId, status PendingApproval, approvalRequestId};
     - `mkt.PackActivation.decide` {activationId, decision APPROVE/REJECT, reason} and `mkt.PackActivation.list/get`;
     - `mkt.Configuration.resolve`: document that `configurationHash` accepts any state recorded since slice 5 (no
       schema change if none is needed);
     - errors: `MKT-ERR-PACK-VERSION-UNKNOWN`, `MKT-ERR-PACK-NOT-PUBLISHED`, `MKT-ERR-PACK-ALREADY-ACTIVE`,
       `MKT-ERR-PACK-ROLLBACK-INSTANT` (REQ-MKT-139), `MKT-ERR-PACK-ACTIVATION-STATE`, plus the existing
       `PLT-ERR-SELF-APPROVAL` and `SOD-VIOLATION` on decide.
  2. **Events (`contracts/events/`, new schema files):**
     - `PackActivated` {pack, legalEntity, version, previousVersion, activatedAt, resultingHash, activationId};
     - `PackRolledBack` {pack, legalEntity, fromVersion, toVersion, window {from, to}, hashesIssued[], reason,
       activationId, resultingHash}.
     - Envelope as contract §3.4.1, with no personal data.
  3. **PFC (`contracts/openapi/pfc.yaml`):**
     - `pfc.ProductVersion.fallback` (dry-run): {productCode, defectiveVersion, reason} → preview {newVersion, source,
       newBusinessWindow, closedWindow} | {fallbackId, status PendingApproval, approvalRequestId};
     - `pfc.ProductVersion.decideFallback` {fallbackId, decision, reason};
     - `ProductVersionPublished` gains `fallbackOf` and `replaces` (null when not a fall-back, always present);
     - errors `PFC-ERR-FALLBACK-SOURCE`, `PFC-ERR-FALLBACK-STATE`.
  4. **POL (`contracts/openapi/pol.yaml`, new paths and schemas appended only, no edit of an existing schema except
     the one additive field below):**
     - `pol.PackRollbackException.list` (filters: status OPEN/REVIEWED, activationId, cursor/limit);
     - `pol.PackRollbackException.get`;
     - `pol.PackRollbackException.review` {exceptionId, outcome NO_ACTION | CORRECTION_REQUIRED, reason};
     - the exception view: {exceptionId, activationId, pack, fromVersion, toVersion, policyId, policyNumber, termId,
       transactionId, transactionKind, configurationHash, productVersion, identifiedAt, status, wrkActivity
       (`NOT_CREATED_WRK_NOT_BUILT`), review {outcome, reason, reviewedBy, reviewedAt}};
     - the quote version view gains `staleReason` (`PACK_ROLLBACK` or null).
     - Permissions: `x-permission` names `pol.PackRollbackException.list/get/review`.
  5. Run ContractGen; samples valid (`validate_samples.py`); `--check` green.
- **Depends on / provides:** depends on nothing. It provides the fakes that SL5-PFC-FALLBACK, SL5-MKT-STATE,
  SL5-MKT-ROLLBACK, SL5-POL-ROLLBACK, SL5-RAT-FALLBACK and SL5-UI-PACKS code against.
- **Files you own:** `contracts/openapi/mkt.yaml`, `contracts/openapi/pfc.yaml`, `contracts/events/pack-*.schema.json`
  (new), and the generated `src/CoreIns.Modules.Market.Contracts/Generated/**` and
  `src/CoreIns.Modules.Product.Contracts/Generated/**`.
- **Shared:**
  - `contracts/openapi/pol.yaml`: appends only;
  - the generated POL contracts;
  - `contracts/openapi/INDEX.md`, generated samples and fakes under `tests/`: resolved by regeneration only.
- **Conflicts to expect:**
  - Slice-3 POL WPs may add fields to `pol.yaml`, and slice 4 edits `clm.yaml`/`ri.yaml`.
  - Merge `origin/main` immediately before the PR. On a conflict, keep both sides and regenerate.
  - Never hand-edit generated files.
- **Do not touch:** `uw.yaml` (SL5-UW-WB-API), any module implementation.
- **PITFALLS to self-check (likely):** 12, 21, 22 (named arguments where generated signatures change).
- **Workflow:** `briefs/sl5/_COMMON.md`. PR title:
  `SL5-CONTRACTS-PACKS: type pack activation/rollback, PFC fall-back, POL rollback exceptions`. Do not merge.
