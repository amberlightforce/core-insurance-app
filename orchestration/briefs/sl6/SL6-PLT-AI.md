Runs on Sonnet 5.5 (model: sonnet). Process: orchestration/briefs/LIGHT-WORKFLOW.md (push straight to main, no PR).

# Brief SL6-PLT-AI (wave 1, PLT, est. 4h)

**Depends on:** -

**Goal:** AI control plane: feature registry, per-feature toggles, global kill switch, deterministic gate used by every module.

**Scope**
- Ops: plt.AiToggle.resolve|set, plt.Ai.killSwitch (activate/clear), plt.Ai.invoke (pluggable provider; default deterministic fake), plt.Ai.recordOutcome, plt.FeatureFlag.evaluate.
- `IAiGate.Check(feature)` -> Allowed | Disabled(reason); modules call it before any AI use and fall back deterministically. Push the interface first.
- Enablement: `set(enabled=true)` refused unless the feature's register status is APPROVED, looked up via `IAiRegisterLookup` (Platform interface; CMP-AIREG supplies the impl; default impl = deny, fail closed, D-SL6-08).
- Kill switch: row flag + event AiKillSwitchActivated; gate reads DB state with <=5 s cache plus invalidation on event, so full effect < 60 s across api and worker. State unreadable -> Disabled.
- Consume AiSystemStatusChanged (CMP) and BiasThresholdBreached (DAT): non-APPROVED or breach -> disable that feature only, audited.
- In-flight: plt.Ai.invoke checks the gate before and after the provider call; if disabled meanwhile it discards output and returns 503 AI-ERR-DISABLED; inputs kept in the interaction row.
- Tables plt.ai_feature, plt.ai_toggle, plt.ai_interaction; Platform.Admin sets toggles/kill switch.

**Acceptance (main-path tests only)**
- Kill switch on -> all gates Disabled within the cache TTL; enable of unapproved feature refused.
- Event consumption disables one feature only.

**Files owned:** src/CoreIns.Platform/Ai/** (new), Platform persistence migration (PLT migration owner W1), contracts/openapi/plt.yaml (Ai/AiToggle/FeatureFlag ops) + events/plt, permissions/plt.json (append), SharedKernel/Ai/IAiGate.cs

**Rules:** defaults D-SL6-01..14 in orchestration/SLICE-PLAN-6.md section 4 are binding; do not wait on open questions. Touch only your files plus append-only shared files (module registration, permissions json, routes, i18n, ci.yml, seed-demo.py). Edit only your module's contracts yaml and regenerate with ContractGen; if regeneration conflicts after merging origin/main, regenerate again. If an EF migration collides with another WP's, regenerate yours after merging. Leave `// TODO(hardening):` notes for edge cases and list them in the report. Regulatory values only with a legalStatus tag and PRD source; otherwise mark illustrative.
Final report under 30 lines, then remove your worktree.
