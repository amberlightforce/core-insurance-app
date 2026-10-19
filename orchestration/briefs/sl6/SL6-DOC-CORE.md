Runs on Sonnet 5.5 (model: sonnet). Process: orchestration/briefs/LIGHT-WORKFLOW.md (push straight to main, no PR).

# Brief SL6-DOC-CORE (wave 1, DOC, est. 4.5h)

**Depends on:** -

**Goal:** Minimal document service: request and render a letter from a template, deliver by a stub channel, keep delivery evidence, and report statutory delivery proof.

**Scope**
- Ops: doc.Document.request|get|listForObject|reprint, doc.Delivery.status|evidenceFor, doc.EvidencePack.get, doc.ProviderEvent.receive (dev/stub provider callbacks), doc.Archive.store|get.
- Templates (HTML, PDF only if a library is already in the repo, D-SL6-12): DT-NONPAY-NOTICE, DT-WITHDRAWAL-ACK, DT-CANCELLATION-NOTICE, DT-REFUND-ADVICE; el/en; placeholders from request payload only.
- StatutoryDeliveryRule: NONPAY notice medium = REGISTERED_POST only (D-SL6-03). Stub post provider; delivery proof = ProviderEvent DELIVERED with proof date -> DocumentDelivered{notificationDate}; RETURNED -> DeliveryFailed. No proof, no notificationDate.
- Consume DocumentRequested from the outbox and expose sync SPI `IDocumentRequestService` (push the interface as your first commit).
- De-dup acknowledgement on (templateKey, requestNo) so one e-mail is sent (REQ-DOC RT-11).
- Tables doc.document, doc.delivery, doc.evidence; content stored in DB bytea (blob store out of scope).

**Acceptance (main-path tests only)**
- Request NONPAY notice -> rendered doc + DocumentRendered; provider DELIVERED callback -> DocumentDelivered with notificationDate; RETURNED -> DeliveryFailed.
- Duplicate request with same key returns same doc.

**Files owned:** src/CoreIns.Modules.Documents/** (all new; DOC migration owner), Documents.Contracts, contracts/openapi/doc.yaml + events/doc, permissions/doc.json (new), module registration line in Host

**Rules:** defaults D-SL6-01..14 in orchestration/SLICE-PLAN-6.md section 4 are binding; do not wait on open questions. Touch only your files plus append-only shared files (module registration, permissions json, routes, i18n, ci.yml, seed-demo.py). Edit only your module's contracts yaml and regenerate with ContractGen; if regeneration conflicts after merging origin/main, regenerate again. If an EF migration collides with another WP's, regenerate yours after merging. Leave `// TODO(hardening):` notes for edge cases and list them in the report. Regulatory values only with a legalStatus tag and PRD source; otherwise mark illustrative.
Final report under 30 lines, then remove your worktree.
