Runs on Sonnet 5.5 (model: sonnet). Process: orchestration/briefs/LIGHT-WORKFLOW.md (push straight to main, no PR).

# Brief SL6-AI-FEATURES (wave 2, CLM, PTY, BIL, CHN, UW, est. 4.5h)

**Depends on:** PLT-AI (IAiGate); CHN-WITHDRAW merged (CHN migration)

**Goal:** One minimal AI-assisted feature per module behind the gate, with deterministic fallback, so the kill switch has something to switch.

**Scope**
- CLM: POST /api/clm/v1/fnol/assist drafts an FNOL description from free text. PTY: duplicate-party explainer. BIL: suspense match suggestion. UW: referral decision-support summary. CHN: chn.Agent.mcp tool endpoint and chn.Ticket.get|approve|reject (a pending ticket stays approvable by a human when AI is off). Fake provider: deterministic template.
- Each calls IAiGate.Check(feature) first; Disabled -> 503 AI-ERR-DISABLED with a neutral message, non-AI path unchanged; Allowed -> plt.Ai.invoke, return suggestion + interactionId.
- AI output is a suggestion only; no state change without an existing manual action.
- Consume AiKillSwitchActivated only for cache invalidation; the gate is the authority.

**Acceptance (main-path tests only)**
- Each endpoint: Allowed returns a suggestion; Disabled returns 503 and the manual flow still works; pending CHN ticket approvable by a human.

**Files owned:** small file sets: Claims/Services/Ai/**, Party/Services/Ai/**, Billing/Services/Ai/**, Channels/Ai/**, Underwriting/Ai/**; permissions append only

**Rules:** defaults D-SL6-01..14 in orchestration/SLICE-PLAN-6.md section 4 are binding; do not wait on open questions. Touch only your files plus append-only shared files (module registration, permissions json, routes, i18n, ci.yml, seed-demo.py). Edit only your module's contracts yaml and regenerate with ContractGen; if regeneration conflicts after merging origin/main, regenerate again. If an EF migration collides with another WP's, regenerate yours after merging. Leave `// TODO(hardening):` notes for edge cases and list them in the report. Regulatory values only with a legalStatus tag and PRD source; otherwise mark illustrative.
Final report under 30 lines, then remove your worktree.
