Runs on Sonnet 5.5 (model: sonnet). Process: orchestration/briefs/LIGHT-WORKFLOW.md (push straight to main, no PR).

# Brief SL6-POL-NONPAY (wave 2, POL, est. 4h)

**Depends on:** BIL-DELINQ event schema; POL-WITHDRAW merged (migration)

**Goal:** Cancel for non-payment when BIL asks; exceptions to a staff queue; rescind on cure.

**Scope**
- Consume CancellationForNonPaymentRequested; create the cancellation through CancelPolicy with reason NON_PAYMENT, effective = start of the day after the clock due date (Europe/Athens), refund/credit by the existing pro-rata path (E2E-03 steps 4-9).
- Exactly once: unique (policyId, termId, clockId); a second delivery returns the existing job (POL X-12).
- Exceptions: a vulnerable-customer flag on PTY (read via the party service; flag absent = not vulnerable) or an existing hold -> row in pol.psr_exception, status PENDING, no cancellation; staff resolve via pol.Psr.decide (proceed|hold). An open claim is not an exception (D-SL6-04).
- Consume DelinquencyResolved: rescind a scheduled non-payment cancellation not yet effective (REQ-POL-211).
- Emit PolicyCancelled as for E2E-03 so CMP/FIN/DOC consumers act; request DT-CANCELLATION-NOTICE.

**Acceptance (main-path tests only)**
- Event twice -> one cancellation. Vulnerable party -> exception only. Cure after scheduling -> rescinded.

**Files owned:** src/CoreIns.Modules.Policy/Commands/Cancellation/NonPayment/** (new), consumer file, migration (regenerate after POL-WITHDRAW), pol.yaml (pol.Psr ops), events/pol (append)

**Rules:** defaults D-SL6-01..14 in orchestration/SLICE-PLAN-6.md section 4 are binding; do not wait on open questions. Touch only your files plus append-only shared files (module registration, permissions json, routes, i18n, ci.yml, seed-demo.py). Edit only your module's contracts yaml and regenerate with ContractGen; if regeneration conflicts after merging origin/main, regenerate again. If an EF migration collides with another WP's, regenerate yours after merging. Leave `// TODO(hardening):` notes for edge cases and list them in the report. Regulatory values only with a legalStatus tag and PRD source; otherwise mark illustrative.
Final report under 30 lines, then remove your worktree.
