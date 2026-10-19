Runs on Sonnet 5.5 (model: sonnet). Process: orchestration/briefs/LIGHT-WORKFLOW.md (push straight to main, no PR).

# Brief SL6-DSAR-SPI (wave 1, SHARED, est. 2h)

**Depends on:** -

**Goal:** Shared contract by which every module takes part in a data-subject request.

**Scope**
- `IDsarParticipant { string Module; ExportAsync(SubjectRef, DsarContext); RestrictAsync(SubjectRef, reason, legalBasis, retentionEnd?); EraseAsync(SubjectRef, DsarContext) }`.
- Outcome per record class: ERASED | ANONYMISED | RESTRICTED(legalBasis, retentionEnd or UNSET_F404) | NOT_HELD | NOT_BUILT. Never a silent skip.
- `DsarParticipantRegistry` built from DI; `DsarRetentionPolicy` reads a config table of record classes (ledger, invoice, claim file, policy document, fiscal document, bureau fact) that are restrict-only (D-SL6-07).
- Subject identity = partyId + known identifiers; no PII in logs.
- Architecture test: participants only in module projects.

**Acceptance (main-path tests only)**
- Registry resolves participants; retention policy marks LEDGER/INVOICE/CLAIM_FILE restrict-only.

**Files owned:** src/CoreIns.SharedKernel/Dsar/** (new), one-line Host registration helper, tests/CoreIns.SharedKernel.Tests/Dsar*

**Rules:** defaults D-SL6-01..14 in orchestration/SLICE-PLAN-6.md section 4 are binding; do not wait on open questions. Touch only your files plus append-only shared files (module registration, permissions json, routes, i18n, ci.yml, seed-demo.py). Edit only your module's contracts yaml and regenerate with ContractGen; if regeneration conflicts after merging origin/main, regenerate again. If an EF migration collides with another WP's, regenerate yours after merging. Leave `// TODO(hardening):` notes for edge cases and list them in the report. Regulatory values only with a legalStatus tag and PRD source; otherwise mark illustrative.
Final report under 30 lines, then remove your worktree.
