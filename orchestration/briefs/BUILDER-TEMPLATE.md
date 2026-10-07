# Builder brief template

Fill in every field. Never send a vague task.

- **WP / PRD:** <WP id + PRD path(s)>. That is the scope, the whole scope, and nothing outside it. For slice WPs:
  only the requirements the slice needs (see `orchestration/SLICE-PLAN.md`).
- **Depends on / provides:** <contracts this module consumes and exposes: OpenAPI files, event schemas, generated interfaces>
- **Must use:** the shared domain model (`CoreIns.SharedKernel`), the API/event contracts (`contracts/`, generated
  `*.Contracts`), the platform (`CoreIns.Platform*`, `CoreIns.Rules`) and the design-system components (`web/src/design-system`).
  Do not modify them. If a change is needed, report back instead of making it (small additive contract typing of your
  own module's operations is allowed per D-API-06/06a).
- **Design guide sections:** <list>
- **Infra constraints:** <list> plus: build and test **natively on Windows** from your worktree (`dotnet build CoreIns.sln -c Release`,
  `dotnet test --project tests/<X>`, `npm ci && npm test` in `web/`); never use WSL (D-ARC-30). Docker Desktop runs, so
  integration tests use Testcontainers; warnings are errors; no float/double; time only via IClock.
- **Read first:** `orchestration/HANDOVER.md` §4, `orchestration/DECISIONS.md` (search for your module), the digest(s) in
  `orchestration/digests/`, and the PRD sections listed.
- **Do not spawn sub-agents** (D-PRG-15). Commit to your worktree branch; do not merge or push.
- **Definition of done:**
  - Every requirement in scope is implemented, each mapped to code and to a test.
  - Unit and integration tests pass; contract tests pass against the shared schemas; `dotnet build -c Release` clean.
  - Every screen uses design-system components, in Greek and English, with empty, loading and error states.
  - No TODOs or stubs unless listed in the report.
- **Report back with:** files changed; a requirement→test traceability table; deviations, assumptions and open
  questions; contract changes requested. Keep the report under 150 lines.
