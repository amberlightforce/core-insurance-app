# Builder brief template

Fill in every field. Never send a vague task. All builders run on **Sonnet 5.5** (D-USR-16).

- **WP / PRD:** <WP id + PRD path(s)>. That is the scope, the whole scope, and nothing outside it. For slice WPs:
  only the requirements the slice needs (see the current `orchestration/SLICE-PLAN-*.md`).
- **Depends on / provides:** <contracts this module consumes and exposes: OpenAPI files, event schemas, generated interfaces>
- **Must use:** the shared domain model (`CoreIns.SharedKernel`), the API/event contracts (`contracts/`, generated
  `*.Contracts`), the platform (`CoreIns.Platform*`, `CoreIns.Rules`) and the design-system components (`web/src/design-system`).
  Do not modify them. If a change is needed, report back instead of making it (small additive contract typing of your
  own module's operations is allowed per D-API-06/06a).
- **Design guide sections (UI WPs):** the binding visual reference is
  `core-insurance-prds/design-guide/mockups/aegean-screens-v3.html` (D-USR-11/13); use the shared PageHeader
  (`landing` / record variants) and Section cards; take side-by-side screenshots into `orchestration/ux/<wp>/`.
- **Infra constraints:** <list> plus: build and test **natively on Windows** (never WSL, D-ARC-30). Docker Desktop runs,
  so integration tests use Testcontainers; warnings are errors; no float/double; time only via IClock.
- **Read first:** `orchestration/HANDOVER.md` §4, **`orchestration/briefs/PITFALLS.md` (self-check against every item
  before reporting)**, `orchestration/DECISIONS.md` (search for your module), the digest(s) in `orchestration/digests/`,
  and the PRD sections listed.
- **Do not spawn sub-agents** (D-PRG-15). Commit early and often to your own branch.
- **Pull-request workflow (D-PRG-19):**
  1. Work on your worktree branch; `git merge main` before you finish.
  2. Quick local gate only: `dotnet build CoreIns.sln -c Release`; the test classes you added or changed
     (`dotnet test --project tests/<X> -c Release --filter <Class>`, output redirected to a file);
     `dotnet run --project tools/CoreIns.ContractGen -- --check`; for web, `npm run lint && npm run typecheck` and the
     touched vitest files with `--maxWorkers=2`.
  3. Push your branch (`git push -u origin <branch>`) and open a PR to `main`
     (`gh pr create --base main --title "<WP id>: <summary>" --body "<report summary>"`). GitHub runs the full suite.
  4. Do **not** merge. Report the PR URL. If CI on your PR is red, fix it on the same branch and push again.
- **Definition of done:**
  - Every requirement in scope is implemented, each mapped to code and to a test.
  - The PITFALLS checklist is satisfied (say which items applied).
  - Quick local gate green; PR open; CI on the PR green (or failing only for a reason you report).
  - Every screen uses design-system components, in Greek and English, with empty, loading and error states.
  - No TODOs or stubs unless listed in the report.
- **Report back with:** branch, PR URL, CI status; files changed; a requirement→test traceability table; pitfalls that
  applied and how they are handled; deviations, assumptions and open questions; contract changes. Keep it under 150 lines.
