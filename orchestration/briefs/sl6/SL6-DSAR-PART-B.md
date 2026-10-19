Runs on Sonnet 5.5 (model: sonnet). Process: orchestration/briefs/LIGHT-WORKFLOW.md (push straight to main, no PR).

# Brief SL6-DSAR-PART-B (wave 2, BIL, FIN, CLM, RI, est. 3h)

**Depends on:** DSAR-SPI

**Goal:** Implement IDsarParticipant for BIL, FIN, CLM, RI (BIL-335/336, FIN-290, CLM-233).

**Scope**
- Export: all records for the subject as JSON (ids, dates, amounts, document refs); seeded-party completeness test: found == seeded.
- Erase/anonymise only data outside mandatory retention classes (drafts, unbound quotes, contact preferences, consent rows). Everything else RESTRICTED with legal-basis text and retentionEnd UNSET_F404 (D-SL6-07). Ledger, invoices, claim files, policy and fiscal documents are restrict-only.
- Restriction = a row per (module, subject, class) in the module's own schema, honoured by reads feeding marketing/notification.
- Where an op exists in generated contracts (bil.Dsar.*, clm.Dsar.*, chn.Dsar.*, doc.Subject.*, fin.Dsar.*, pol.Dsar.export) route it to the participant.
- MIG: archive export and restrict only; claim file under retention restricted.

**Acceptance (main-path tests only)**
- Per module: export finds the seeded subject; erase returns a per-class outcome list with no silent skip; second erase idempotent.

**Files owned:** one participant file per module under its Services/Dsar/** (new), a registration line in each Module.cs, tests per module; no migrations unless a restriction column is unavoidable (then own migration + TODO)

**Rules:** defaults D-SL6-01..14 in orchestration/SLICE-PLAN-6.md section 4 are binding; do not wait on open questions. Touch only your files plus append-only shared files (module registration, permissions json, routes, i18n, ci.yml, seed-demo.py). Edit only your module's contracts yaml and regenerate with ContractGen; if regeneration conflicts after merging origin/main, regenerate again. If an EF migration collides with another WP's, regenerate yours after merging. Leave `// TODO(hardening):` notes for edge cases and list them in the report. Regulatory values only with a legalStatus tag and PRD source; otherwise mark illustrative.
Final report under 30 lines, then remove your worktree.
