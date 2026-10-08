# Common section of every slice-3 builder brief

Every `sl3/<WP>.md` brief includes this section by reference, and the orchestrator pastes it under the brief when
sending. It is filled from `briefs/BUILDER-TEMPLATE.md`. **Runs on Sonnet 5.5 (model: sonnet), D-USR-16.**

- **Read first:**
  - `orchestration/HANDOVER.md` §4.
  - **`orchestration/briefs/PITFALLS.md`**: self-check against every item before you report. The items your brief names
    are the likely ones, but not the only ones.
  - `orchestration/SLICE-PLAN-3.md`:
    - §4 temporal design;
    - §6 contracts;
    - §7 conflict map. Stay inside the files you own; shared files are append-only.
  - `orchestration/DECISIONS.md`: D-SL3-01..14 and the rows for your module.
  - The digests your brief names, and `docs/module-pattern.md`.
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
- **Regulatory values:**
  - Only from the PRDs, each with its `legalStatus`.
  - A value that is not Settled is flagged `provisional` outside Production and refused in Production.
  - An open value stays absent and fails closed.
  - Commercial values are marked **illustrative**.
- **Do not spawn sub-agents** (D-PRG-15). Commit early and often (WIP commits are fine): worktrees have been lost before.
- **Pull-request workflow (D-PRG-19):**
  1. Run `git merge main` before you finish, and whenever the orchestrator tells you a dependency has merged.
  2. Quick local gate:
     - `dotnet build CoreIns.sln -c Release`;
     - the test classes you added or changed: `dotnet test --project tests/<X> -c Release --filter <Class>`, with the
       output redirected to a file;
     - `dotnet run --project tools/CoreIns.ContractGen -- --check`;
     - for web: `npm run lint && npm run typecheck`, plus the touched vitest files with `--maxWorkers=2`.
  3. Push and open the PR:
     - `git push -u origin <your branch>`;
     - `gh pr create --base main --title "<WP id>: <summary>" --body "<report summary>"`.
  4. **Do not merge.** Report the PR URL. If CI on your PR is red, fix it on the same branch and push again.
- **Never touch** the user's `coreins` compose project or other projects' containers. An isolated stack has its own
  project name and is torn down with `down -v`.
- **Definition of done:**
  - Every requirement in scope is implemented and mapped to a test.
  - The PITFALLS checklist is satisfied, and your report says which items applied.
  - The quick gate is green and the PR is open.
  - CI on the PR is green, or failing only for a reason you report.
  - Every screen (UI WPs) uses design-system components, in Greek and English, with empty, loading, error and
    no-permission states.
  - There are no TODOs or stubs that your report does not list.
- **Report back (under 150 lines):**
  - branch, PR URL and CI status;
  - files changed;
  - a requirement → test traceability table;
  - the pitfalls that applied and how you handled them;
  - deviations, assumptions and open questions, including any decision the orchestrator should record;
  - contract changes.
