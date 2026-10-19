# Light builder workflow (D-USR-20, 2026-10-19)

The user wants a **working system first**, cheaply. Supersedes the deep-review / PR gating in older briefs
(BUILDER-TEMPLATE, _COMMON review sections). Older briefs still define *what* to build; this file defines *how*.

## Principles
- Build the feature end to end so it actually works (API + persistence + wiring; UI if the WP is UI).
- Write a reasonable number of unit/integration tests for the main paths. Don't write exhaustive adversarial suites.
- Don't chase every edge case or finance-grade hardening. Fail closed on obviously dangerous input, leave a
  `// TODO(hardening):` note for the rest, and list them in your final report.
- Keep scope to your WP. Don't refactor unrelated code.
- No reviewer agents. You self-check.

## Setup
Repo: `C:/Users/Karl/Projects/coreinsurance/core-insurance-app`. **Never** touch the main checkout (it is on
branch `training-portal`, used by another session) and never touch `.claude/worktrees/orchestrator`.
Create your own worktree from origin/main:

```bash
cd C:/Users/Karl/Projects/coreinsurance/core-insurance-app
git fetch origin
git worktree add .claude/worktrees/b-<wp> -b wp/<wp> origin/main     # or from a salvage branch, see your task
cd .claude/worktrees/b-<wp>
```
If you need `infra/local/.env`, copy it from the orchestrator worktree.

## Quick checks (run before pushing)
- `dotnet build CoreIns.sln -c Release` (warnings are errors — fix them).
- If contracts changed: `dotnet run --project tools/CoreIns.ContractGen -c Release --no-build` then
  `python3 tools/CoreIns.ContractGen/validate_samples.py`; otherwise `... --no-build -- --check`.
- Your own tests: `dotnet test --project tests/<Project> -c Release --filter <YourClass>`.
  `tests/CoreIns.IntegrationTests` needs Docker (Testcontainers) — run only your filtered classes.
- `dotnet test --project tests/CoreIns.ArchitectureTests -c Release` if you added projects/references/modules.
- Web (if touched, in `web/`): `npm ci --no-audit` (once), `npm run lint`, `npm run typecheck`,
  `npx vitest run <your files>`, `npm run format` before commit.
- Do NOT run the e2e Docker stacks locally (`tests/e2e/run-*.sh`); CI does that.

## Integrate (you push straight to main — there are no PRs)
```bash
git add -A && git commit -m "<wp>: <summary>"      # end message with: Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
git fetch origin && git merge origin/main          # resolve conflicts keeping BOTH sides' intent
# re-run build (+ ContractGen if contracts/ changed, + web typecheck if web/ touched) after the merge
git push origin HEAD:main                          # if rejected (someone pushed first): fetch, merge, rebuild, push again
```
Hot shared files (`routes.tsx`, `ClaimsModule.cs`, module registration, `ci.yml`, i18n json, generated contracts,
migrations snapshots) WILL conflict with parallel builders — merge additively; for EF migrations, if two
migrations collide on the model snapshot, regenerate yours after merging.
Push working increments early (e.g. backend first, then extras) rather than one giant push at the end.
Never force-push. Never push a red build to main.

## Final report (keep it short, <30 lines)
What works, commit SHA(s) pushed to main, tests added, known gaps / TODO(hardening) list, anything the next WP needs.
Then remove your worktree: `git worktree remove .claude/worktrees/b-<wp>` (from the repo root).
