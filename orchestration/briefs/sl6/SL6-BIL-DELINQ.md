Runs on Sonnet 5.5 (model: sonnet). Process: orchestration/briefs/LIGHT-WORKFLOW.md (push straight to main, no PR).

# Brief SL6-BIL-DELINQ (wave 1, BIL, est. 5h)

**Depends on:** CMP-CLOCK first commit (IComplianceClockService), DOC-CORE first commit (IDocumentRequestService); until then use local fakes

**Goal:** Delinquency case from a returned or unpaid instalment through notice, waiting clock and cancellation request.

**Scope**
- Ops: bil.Delinquency.list|get|recordContact|arrange|releaseHold (generated contracts exist; implement IBillingDelinquencyService).
- Open a case when a payment return (SEPA AM04/MS03) leaves arrears and re-presentation failed, or an invoice is overdue past `bil.delinq.grace` (default 5 days, illustrative). Event DelinquencyStarted.
- Request DT-NONPAY-NOTICE from DOC (registered post). On DocumentDelivered{notificationDate} -> cmp clock BIL_NONPAY_NOTICE from notificationDate. On DeliveryFailed: case NOTICE_RETURNED, no clock (D-SL6-03), emit a WorkRequested record (WRK not built, D-SL6-11).
- Cure: receipt clearing arrears before elapse -> clock stop (Met), case RESOLVED, DelinquencyResolved.
- On ClockElapsed: if arrears > tolerance (`bil.delinq.tolerance`, default 5.00 EUR illustrative, INV-11) emit CancellationForNonPaymentRequested{policyId, termId, asOf, arrears, clockId}; otherwise resolve. Idempotent on clockId. Push the event schema first.
- releaseHold (vulnerable customer) and arrange (promise to pay) pause the clock.
- Tables bil.delinquency_case, bil.delinquency_contact.

**Acceptance (main-path tests only)**
- Integration with fake DOC/CMP SPI: flow to CancellationForNonPaymentRequested; cure path; returned letter starts no clock; below tolerance resolves.

**Files owned:** src/CoreIns.Modules.Billing/{Commands/Delinquency*,Api/Delinquency*,Domain/Delinquency*,Services/Nonpay*,Persistence/** (BIL migration owner W1)}; contracts bil.yaml (Delinquency ops) + events/bil; permissions/bil.json (append)

**Rules:** defaults D-SL6-01..14 in orchestration/SLICE-PLAN-6.md section 4 are binding; do not wait on open questions. Touch only your files plus append-only shared files (module registration, permissions json, routes, i18n, ci.yml, seed-demo.py). Edit only your module's contracts yaml and regenerate with ContractGen; if regeneration conflicts after merging origin/main, regenerate again. If an EF migration collides with another WP's, regenerate yours after merging. Leave `// TODO(hardening):` notes for edge cases and list them in the report. Regulatory values only with a legalStatus tag and PRD source; otherwise mark illustrative.
Final report under 30 lines, then remove your worktree.
