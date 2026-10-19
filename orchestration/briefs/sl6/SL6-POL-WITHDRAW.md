Runs on Sonnet 5.5 (model: sonnet). Process: orchestration/briefs/LIGHT-WORKFLOW.md (push straight to main, no PR).

# Brief SL6-POL-WITHDRAW (wave 1, POL, est. 4h)

**Depends on:** -

**Goal:** pol.Withdrawal.submit: void the policy ab initio with a full refund, exactly once.

**Scope**
- Op pol.Withdrawal.submit (policyId, right WITHDRAWAL|OBJECTION, receivedAt, channelRef, dry-run) + SPI for in-process use by CHN.
- Reuse the cancellation machinery in Commands/Cancellation: reason DISTANCE_WITHDRAWAL, effective = cover start, treatment source `DistanceWithdrawal`, refund = total premium paid incl. IPT and levies, cost-of-cover deduction 0.00 (D-SL6-02). CY pack: pro rata (stub).
- Right window checked from receivedAt (not now): POL-ERR-RIGHT-EXPIRED 422.
- Exactly-once on (policyId, channelRef): unique index; replay returns the first result.
- Emit PolicyVoided / POL_REFUND_DUE (variant DISTANCE_WITHDRAWAL); request DT-WITHDRAWAL-ACK via DocumentRequested (no-op while DOC absent); refund runs through the existing BIL refund path (E2E-03).
- GR treatment rows: add `DistanceWithdrawal` source with legalStatus SECONDARY_SOURCE and tax_position TAX_OPINION_PENDING (F-401).

**Acceptance (main-path tests only)**
- Integration: bound paid policy -> submit -> void done, refund equals paid, deduction 0.00; second submit same result, one refund.
- Late receivedAt -> 422.

**Files owned:** src/CoreIns.Modules.Policy/Commands/Withdrawal/** (new), Api/WithdrawalController.cs, Policy.Contracts SPI IPolicyWithdrawalService, Persistence migration (POL migration owner W1), contracts/openapi/pol.yaml (Withdrawal ops) + events/pol (append), permissions/pol.json (append), CountryPacks.GR treatment rows (append)

**Rules:** defaults D-SL6-01..14 in orchestration/SLICE-PLAN-6.md section 4 are binding; do not wait on open questions. Touch only your files plus append-only shared files (module registration, permissions json, routes, i18n, ci.yml, seed-demo.py). Edit only your module's contracts yaml and regenerate with ContractGen; if regeneration conflicts after merging origin/main, regenerate again. If an EF migration collides with another WP's, regenerate yours after merging. Leave `// TODO(hardening):` notes for edge cases and list them in the report. Regulatory values only with a legalStatus tag and PRD source; otherwise mark illustrative.
Final report under 30 lines, then remove your worktree.
