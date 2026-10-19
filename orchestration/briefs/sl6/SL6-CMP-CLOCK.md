Runs on Sonnet 5.5 (model: sonnet). Process: orchestration/briefs/LIGHT-WORKFLOW.md (push straight to main, no PR).

# Brief SL6-CMP-CLOCK (wave 1, CMP, est. 4h)

**Depends on:** -

**Goal:** Clock engine: start/pause/resume/stop/cancel/extend/get/query + clock definitions, elapsing by a hosted job on the platform clock (DevClock), with events.

**Scope**
- Ops: cmp.Clock.start|pause|resume|stop|cancel|extend|get|query, cmp.ClockDef.list|get (propose optional).
- Types WAITING_PERIOD (elapse -> ClockElapsed), DEADLINE (warn then ClockBreached), pause/resume extends due date.
- Seeded definitions: BIL_NONPAY_NOTICE (WAITING_PERIOD, P1M, Law 2496/1997 Art 6 par 2, legalStatus SECONDARY_SOURCE, D-SL6-04), DSAR_DEADLINE (DEADLINE, P1M), CLM_FS_COUNTERPARTY_REPLY (placeholder 30d).
- Due date computed in Europe/Athens end of day, calendar months with clamp; never in UTC.
- Events (outbox): ClockStarted, ClockWarned, ClockElapsed, ClockBreached, ClockMet, ClockPaused/Resumed/Cancelled (schemas exist).
- In-process SPI `IComplianceClockService` (start/stop/get) in Compliance.Contracts for BIL/CLM/DSAR; idempotent on (definition, subjectRef, key). Push this interface as your first commit.
- Tables cmp.clock, cmp.clock_event; hosted job `ClockSweeper` every 10 s (configurable), at-least-once safe.

**Acceptance (main-path tests only)**
- Unit: month arithmetic incl. 31 Jan, DST day, pause/resume.
- Integration: start -> advance DevClock -> exactly one ClockElapsed; stop before elapse -> ClockMet, no elapse; double start is idempotent.

**Files owned:** src/CoreIns.Modules.Compliance/{Clocks/**,Api/Clock*,Persistence/** (CMP migration owner, W1)}; contracts/openapi/cmp.yaml + events/cmp (clock part only); permissions/cmp.json (append)

**Rules:** defaults D-SL6-01..14 in orchestration/SLICE-PLAN-6.md section 4 are binding; do not wait on open questions. Touch only your files plus append-only shared files (module registration, permissions json, routes, i18n, ci.yml, seed-demo.py). Edit only your module's contracts yaml and regenerate with ContractGen; if regeneration conflicts after merging origin/main, regenerate again. If an EF migration collides with another WP's, regenerate yours after merging. Leave `// TODO(hardening):` notes for edge cases and list them in the report. Regulatory values only with a legalStatus tag and PRD source; otherwise mark illustrative.
Final report under 30 lines, then remove your worktree.
