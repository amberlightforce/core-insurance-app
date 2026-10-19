Runs on Sonnet 5.5 (model: sonnet). Process: orchestration/briefs/LIGHT-WORKFLOW.md (push straight to main, no PR).

# Brief SL6-CMP-DSAR (wave 2, CMP, est. 5h)

**Depends on:** DSAR-SPI; CMP-CLOCK merged

**Goal:** DSAR case orchestration across every registered participant.

**Scope**
- Ops: cmp.Dsar.create|verifyIdentity|startFanOut|retryTask|decideItem|approve|extend|refuse|get|query|export|restrict|erase, cmp.DisclosureLog.record|query.
- create starts DSAR_DEADLINE clock (one month) from receipt.
- startFanOut: one task per entry of the configured map (15 rows: PTY POL BIL CLM UW RI DOC CHN WRK PLT FIN RAT(export only) DAT MIG CMP; PFC and MKT hold none). Participants run through IDsarParticipant; a module without one gets task NOT_BUILT (WRK) and the case cannot auto-complete (D-SL6-07).
- Export compiles a package (JSON + index); DPO decideItem sets third-party redaction; erase/restrict only after approve by a DPO who is not the creator; retention classes force restrict with basis; legal hold (flag) blocks purge.
- DAT task calls dat.Erasure.execute with requestId = caseId+taskId; the sync response completes the task.
- Events DSARReceived, DSARExtended, DSARCompleted. Tables cmp.dsar, cmp.dsar_task, cmp.disclosure_log.

**Acceptance (main-path tests only)**
- Fan-out with 3 fake participants: results aggregated, erase waits for approve, deadline clock set, completion event once.

**Files owned:** src/CoreIns.Modules.Compliance/Dsar/** (new), migration (after CMP-CLOCK), cmp.yaml (Dsar + DisclosureLog ops), events/cmp (DSAR*), permissions/cmp.json

**Rules:** defaults D-SL6-01..14 in orchestration/SLICE-PLAN-6.md section 4 are binding; do not wait on open questions. Touch only your files plus append-only shared files (module registration, permissions json, routes, i18n, ci.yml, seed-demo.py). Edit only your module's contracts yaml and regenerate with ContractGen; if regeneration conflicts after merging origin/main, regenerate again. If an EF migration collides with another WP's, regenerate yours after merging. Leave `// TODO(hardening):` notes for edge cases and list them in the report. Regulatory values only with a legalStatus tag and PRD source; otherwise mark illustrative.
Final report under 30 lines, then remove your worktree.
