Runs on Sonnet 5.5 (model: sonnet). Process: orchestration/briefs/LIGHT-WORKFLOW.md (push straight to main, no PR).

# Brief SL6-CHN-WITHDRAW (wave 1, CHN, est. 3.5h)

**Depends on:** -

**Goal:** Customer withdrawal function: declaration, confirmation step, confirmedAt from platform time, handoff to POL.

**Scope**
- Ops: chn.Withdrawal.declare (two-step: declare -> confirm token -> confirmedAt), chn.Withdrawal.get|list.
- Deadline check: window length from pack config `withdrawal.days` (default 14, D-SL6-06) from conclusion date; the last day ends 23:59:59 Europe/Athens (DST-safe). Late -> declined with CHN-ERR-RIGHT-EXPIRED, still recorded.
- On confirm: call `IPolicyWithdrawalService.Submit(policyId, right=WITHDRAWAL, receivedAt=confirmedAt, channelRef=requestNo)` (POL SPI from POL-WITHDRAW; use a local stub interface until it merges) and always emit WithdrawalRequestReceived (backup path) in the same transaction as the request row.
- Close on POL outcome: WithdrawalRequestClosed.
- Customer-facing auth: reuse the existing portal customer principal; staff may declare on behalf (permission chn.Withdrawal.declare).
- Table chn.withdrawal_request (requestNo unique, channelRef).

**Acceptance (main-path tests only)**
- confirmedAt 23:59:59 on the last day accepted (also on a DST-change day), 00:00:00 next day rejected.
- Re-confirm yields one request and one POL call (idempotent).

**Files owned:** src/CoreIns.Modules.Channels/** (new; CHN migration owner W1), Channels.Contracts, contracts/openapi/chn.yaml (Withdrawal ops only) + events/chn, permissions/chn.json (new)

**Rules:** defaults D-SL6-01..14 in orchestration/SLICE-PLAN-6.md section 4 are binding; do not wait on open questions. Touch only your files plus append-only shared files (module registration, permissions json, routes, i18n, ci.yml, seed-demo.py). Edit only your module's contracts yaml and regenerate with ContractGen; if regeneration conflicts after merging origin/main, regenerate again. If an EF migration collides with another WP's, regenerate yours after merging. Leave `// TODO(hardening):` notes for edge cases and list them in the report. Regulatory values only with a legalStatus tag and PRD source; otherwise mark illustrative.
Final report under 30 lines, then remove your worktree.
