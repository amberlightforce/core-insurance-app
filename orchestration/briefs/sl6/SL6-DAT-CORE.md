Runs on Sonnet 5.5 (model: sonnet). Process: orchestration/briefs/LIGHT-WORKFLOW.md (push straight to main, no PR).

# Brief SL6-DAT-CORE (wave 1, DAT, est. 3.5h)

**Depends on:** -

**Goal:** Minimal data module: subject export/erasure with crypto-shred keys, and the AI bias test that auto-disables a feature.

**Scope**
- Ops: dat.Subject.export, dat.Erasure.execute (idempotent on requestId, destroys the subject key once, returns synchronously, emits LakehouseErasureCompleted as evidence only), dat.Restriction.apply, dat.BiasTest.run, dat.Model.register|get.
- No lakehouse: tables dat.subject_key (subject, key id, destroyed_at) and dat.subject_fact keyed by subject for export; erasure destroys the key and blanks the facts.
- dat.BiasTest.run(feature, metric, ratio, band 0.8..1.25): outside the band -> BiasThresholdBreached event (PLT-AI consumes) and model status SUSPENDED. AI-CLM-04 ratio 0.74 breaches.
- Do not build marts, accumulation, analytics or SCR.

**Acceptance (main-path tests only)**
- Erasure twice with the same requestId destroys keys once and returns the same result.
- BiasTest 0.74 emits one breach event; 0.9 none.

**Files owned:** src/CoreIns.Modules.Data/** (new; DAT migration owner), Data.Contracts, contracts/openapi/dat.yaml (Erasure, Subject, Restriction, BiasTest, Model ops only) + events/dat, permissions/dat.json (new)

**Rules:** defaults D-SL6-01..14 in orchestration/SLICE-PLAN-6.md section 4 are binding; do not wait on open questions. Touch only your files plus append-only shared files (module registration, permissions json, routes, i18n, ci.yml, seed-demo.py). Edit only your module's contracts yaml and regenerate with ContractGen; if regeneration conflicts after merging origin/main, regenerate again. If an EF migration collides with another WP's, regenerate yours after merging. Leave `// TODO(hardening):` notes for edge cases and list them in the report. Regulatory values only with a legalStatus tag and PRD source; otherwise mark illustrative.
Final report under 30 lines, then remove your worktree.
