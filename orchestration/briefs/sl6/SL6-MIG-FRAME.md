Runs on Sonnet 5.5 (model: sonnet). Process: orchestration/briefs/LIGHT-WORKFLOW.md (push straight to main, no PR).

# Brief SL6-MIG-FRAME (wave 1, MIG, est. 5h)

**Depends on:** -

**Goal:** Migration framework: xref registry, batches with dry-run, idempotent import, coexistence routing, baseline configuration reference, reverse SPI.

**Scope**
- Ops: mig.Xref.register|resolve|list, mig.Batch.start (dryRun)|get|outcomes|reverse|pause|resume, mig.Routing.resolve|override. Push the SPI interfaces first.
- Batch = ordered list of (module, objectType, legacyId, payload) dispatched to `IMigrationImporter` registered per module; an already-imported legacyId is a no-op reported SKIPPED_EXISTING.
- All imported objects carry origin=MIGRATION and batchId. Non-dry-run needs approval by a different user than the starter (MIG-005; reuse the Platform approval type mechanism).
- Baseline configuration H0: record the current MKT configuration-state hash as the batch's `baselineHash` (SL5 MKT state history); migrated transactions reference it.
- Routing per (policy, date): NONE|TRANSITIONING|CORE; resolve is authoritative; each change emits CoexistenceMasterChanged; overlapping periods rejected so there is exactly one master per policy and date.
- Reverse (D-SL6-05): calls `IMigrationReverser.Reverse(batchId)` of each module; a module refuses objects with post-migration activity (MIG-ERR-REVERSAL-BLOCKED, listed per object); a module with no reverser makes the batch NOT_REVERSIBLE.
- Tables mig.batch, mig.batch_item, mig.xref, mig.routing.

**Acceptance (main-path tests only)**
- Party import through a stub importer: dry-run writes nothing; real run twice -> second all SKIPPED_EXISTING.
- Overlapping routing periods rejected; resolve returns one master.

**Files owned:** src/CoreIns.Modules.Migration/** (new; MIG migration owner), Migration.Contracts (SPI IMigrationImporter / IMigrationReverser), contracts/openapi/mig.yaml + events/mig, permissions/mig.json (new)

**Rules:** defaults D-SL6-01..14 in orchestration/SLICE-PLAN-6.md section 4 are binding; do not wait on open questions. Touch only your files plus append-only shared files (module registration, permissions json, routes, i18n, ci.yml, seed-demo.py). Edit only your module's contracts yaml and regenerate with ContractGen; if regeneration conflicts after merging origin/main, regenerate again. If an EF migration collides with another WP's, regenerate yours after merging. Leave `// TODO(hardening):` notes for edge cases and list them in the report. Regulatory values only with a legalStatus tag and PRD source; otherwise mark illustrative.
Final report under 30 lines, then remove your worktree.
