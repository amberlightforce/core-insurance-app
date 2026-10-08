# Common section of every slice-4 builder brief

Runs on Sonnet 5.5 (model: sonnet), D-USR-16.

Every `sl4/<WP>.md` brief includes this section by reference, and the orchestrator pastes it under the brief when
sending. It is filled from `briefs/BUILDER-TEMPLATE.md`.

- **Read first:**
  - `orchestration/HANDOVER.md` §4.
  - **`orchestration/briefs/PITFALLS.md`** (items 1–39 and any added since). Self-check against every item before you
    report. The items your brief names are the likely ones, but not the only ones.
  - `orchestration/SLICE-PLAN-4.md`:
    - §3 journeys and amounts;
    - §4 the Friendly Settlement design;
    - §6 contracts;
    - §7 conflict map. Stay inside the files you own; shared files are append-only.
  - `orchestration/DECISIONS.md`: D-SL2-03..13, D-SL3-*, and the D-SL4-01..20 rows (in `SLICE-PLAN-4.md` §11 until
    the orchestrator appends them).
  - The digests your brief names, and `docs/module-pattern.md`.
- **Slice 3 is still building in parallel.**
  - Never edit a file that a slice-3 WP owns (`SLICE-PLAN-3.md` §7) unless your brief's gate says that WP has merged.
  - If you find you need such a file, stop and report instead of editing it.
  - Run `git merge origin/main` when you start and whenever the orchestrator tells you a dependency has merged.
- **Must use:**
  - `CoreIns.SharedKernel`, the generated `*.Contracts`, `CoreIns.Platform*`, `CoreIns.Rules` and the design-system
    components.
  - Do not modify them. If you need a change, report it instead of making it. The one exception is small additive
    typing of your **own** module's operations (D-API-06/06a); run ContractGen afterwards and keep `--check` green.
- **Infra constraints:**
  - Build and test natively on Windows (D-ARC-30). Integration tests use Testcontainers.
  - Warnings are errors.
  - `decimal`/NUMERIC only (COREINS001). Time only through `IClock` (COREINS002).
  - No MediatR, AutoMapper, MassTransit or FluentAssertions.
- **Money rules for this slice:**
  - CLM never moves or posts cash. BIL is the only cash executor (D1). FIN posts only from module events.
  - Authority is checked on aggregates, never per line (PITFALLS 1).
  - System-recorded sets carry an evidence reference (BIL allocation, FS notification or statement line) and never
    take amounts from a client (PITFALLS 7).
- **Regulatory values:**
  - Only from the PRDs, each with its `legalStatus`.
  - A value that is not Settled is flagged `provisional` outside Production and refused in Production. The gate is
    `== Settled` (PITFALLS 36).
  - An open value stays absent and fails closed.
  - Commercial values (treaty, authority grants, thresholds, GL placeholders) are marked **illustrative**.
- **RAM and logs (PITFALLS 38, 39):** before every `dotnet` command set `MSBUILDDISABLENODEREUSE=1` and
  `DOTNET_CLI_USE_MSBUILD_SERVER=0`, and pass `-m:2`. Write logs to unique paths in your own worktree (e.g.
  `<worktree>/.logs/<class>.log`).
- **Do not spawn sub-agents** (D-PRG-15). Commit early and often (WIP commits are fine): worktrees have been lost before.
- **Pull-request workflow (D-PRG-19, D-USR-18):**
  1. Run `git merge origin/main` before you finish.
  2. Quick local gate:
     - `dotnet build CoreIns.sln -c Release`;
     - the test classes you added or changed: `dotnet test --project tests/<X> -c Release --filter <Class>`, with the
       output redirected to a file;
     - `dotnet run --project tools/CoreIns.ContractGen -- --check`;
     - for web: `npm run lint && npm run typecheck`, plus the touched vitest files with `--maxWorkers=2`.
  3. Push your branch with `git push -u origin <your branch>`.
     - If your brief says **"own PR"**, run `gh pr create --base main --title "<WP id>: <summary>" --body "<report
       summary>"`.
     - If it says **"batched"**, do not open a PR; the orchestrator folds your branch into a `batch/<name>` PR.
  4. **Do not merge.** Report the branch (and PR URL). If CI on your PR is red, fix it on the same branch and push
     again.
- **Never touch** the user's `coreins` compose project or other projects' containers. An isolated stack has its own
  project name and is torn down with `down -v`.
- **Definition of done:**
  - Every requirement in scope is implemented and mapped to a test.
  - The PITFALLS checklist is satisfied, and your report says which items applied.
  - The quick gate is green and the branch is pushed (and the PR is open if your brief says "own PR").
  - Every screen (UI WPs) uses design-system components, in Greek and English, with empty, loading, error and
    no-permission states. Side-by-side screenshots against the Aegean mockup v3 go in `orchestration/ux/<wp>/`.
  - There are no TODOs or stubs that your report does not list.
- **Report back (under 150 lines):**
  - branch, PR URL and CI status;
  - files changed;
  - a requirement → test traceability table;
  - the pitfalls that applied and how you handled them;
  - deviations, assumptions and open questions, including any decision the orchestrator should record;
  - contract changes.
