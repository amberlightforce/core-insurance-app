Runs on Sonnet 5.5 (model: sonnet), D-USR-16.

# Common section of every slice-5 brief (builders and reviewers)

Every `sl5/<WP>.md` brief includes this section by reference, and the orchestrator pastes it under the brief when
sending. It is filled from `briefs/BUILDER-TEMPLATE.md`. **Reviewers also run on Sonnet 5.5** (`model: "sonnet"`). A
deep review keeps its depth: adversarial, with probe tests in a scratch copy (D-USR-16, `briefs/REVIEWER-TEMPLATE.md`).

- **Read first:**
  - `orchestration/HANDOVER.md` §4.
  - **`orchestration/briefs/PITFALLS.md`** (all items, including any added during slices 3–5): self-check against every item before you report. Your brief
    names the likely ones, but they are not the only ones.
  - `orchestration/SLICE-PLAN-5.md`:
    - §3/§3a (workbench) or §4 (E2E-12 design);
    - §6 contracts;
    - **§7 conflict map**. Stay inside the files you own; shared files are append-only.
  - `orchestration/DECISIONS.md`: D-UW-01, D-SL3-01..15, D-SL5-01..14 (proposed in SLICE-PLAN-5 §11 until the
    orchestrator appends them), and the rows for your module.
  - The digests your brief names, and `docs/module-pattern.md`.
- **Three slices run at the same time.** Slice 3 (servicing) and slice 4 (claims + RI) are building in parallel with
  you.
  - Never edit a file that §7 gives to another WP.
  - If your WP is **GATED**, do not start before the orchestrator confirms that the gate merged.
  - Merge `origin/main` often (`git fetch origin && git merge origin/main`), and always just before your PR.
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
  - Slice 5 adds **no** regulatory value: GR 0.1.0/0.2.0 are existing content, and MOTOR-GR 1.2 is a copy of 1.0.
  - Roles, grants and limits are **illustrative** and say so.
  - An open value stays absent and fails closed.
- **Separation of duties and approvals:**
  - Approval requests are created **in-process** by the owning module only.
  - `verifyForExecution` binds subject + type + content hash.
  - Maker ≠ checker includes the maker's principal (PITFALLS 3–6).
- **Do not spawn sub-agents** (D-PRG-15). Commit early and often (WIP commits are fine): worktrees have been lost before
  (PITFALLS 34).
- **Pull-request workflow (D-PRG-19, D-USR-18):**
  1. Run `git merge origin/main` before you finish, and whenever the orchestrator says a dependency has merged.
  2. Quick local gate:
     - `dotnet build CoreIns.sln -c Release`;
     - the test classes you added or changed: `dotnet test --project tests/<X> -c Release --filter <Class>`, with the
       output redirected to a file (PITFALLS 33);
     - `dotnet run --project tools/CoreIns.ContractGen -- --check` and
       `python tools/CoreIns.ContractGen/validate_samples.py`;
     - for web: `npm run lint && npm run typecheck && npm run format:check`, plus the touched vitest files with
       `--maxWorkers=2`.
  3. Push and open the PR:
     - `git push -u origin <your branch>`;
     - a **meaningful** WP (behaviour, contracts, migrations, security, temporal) opens its own PR with
       `gh pr create --base main --title "<WP id>: <summary>" --body "<report summary>"`;
     - a WP that your brief marks **batched** only pushes its branch and reports it; the orchestrator folds it into a
       `batch/<name>` PR (D-PRG-22, D-USR-18).
  4. **Do not merge.** Report the PR URL or the branch. If CI on your PR is red, fix it on the same branch and push
     again.
- **Never touch** the user's `coreins` compose project or other projects' containers. An isolated stack has its own
  project name and is torn down with `down -v` (PITFALLS 35).
- **UI WPs:**
  - The binding visual reference is `core-insurance-prds/design-guide/mockups/aegean-screens-v3.html` (D-USR-11/13).
  - Use the shared PageHeader, Section, DataTable and SplitView (`web/src/design-system/patterns/Workbench`).
  - Greek first, English complete, with loading, empty, error and no-permission states.
  - Take side-by-side screenshots (mockup vs app; light/dark; desktop and about 840 px) into `orchestration/ux/<wp>/`
    and put them in the PR body.
- **Definition of done:**
  - Every requirement in scope is implemented and mapped to a test.
  - The PITFALLS checklist is satisfied, and your report says which items applied.
  - The quick gate is green, and the PR is open (or the branch is pushed, for batched WPs).
  - CI is green, or failing only for a reason you report.
  - There are no TODOs or stubs that your report does not list.
- **Report back (under 150 lines):**
  - branch, PR URL and CI status;
  - files changed;
  - a requirement → test traceability table;
  - the pitfalls that applied and how you handled them;
  - deviations, assumptions and open questions, including any decision the orchestrator should record;
  - contract changes.
