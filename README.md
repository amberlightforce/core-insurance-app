# Core Insurance App

Greek-first, EU-ready P&C core insurance system (modular monolith, .NET 10 + React 19 + PostgreSQL 17 on Azure).

- Specifications: `../core-insurance-prds` (PRDs, design guide) and `../core-insurance-infra` (stack and infrastructure).
- Build orchestration: [`orchestration/`](orchestration/): PLAN.md, DECISIONS.md, STATUS.md, backlog.

## Repository layout

| Path | Contents |
|---|---|
| `src/CoreIns.Host` | The single deployable. `APP_ROLE` = `api` (REST + OpenAPI + React app + Hangfire dashboard), `worker` (Hangfire server) or `migrate` (schemas + Hangfire storage, then exit) |
| `src/CoreIns.Modules.<Name>` / `.Contracts` | One project pair per business module; other modules may reference only `.Contracts` |
| `src/CoreIns.Platform`, `src/CoreIns.SharedKernel` | Platform services (outbox, audit, config) and shared value types |
| `src/CoreIns.CountryPacks.GR` / `.CY` | Country packs; core code never references them |
| `tools/CoreIns.Analyzers` | Roslyn analyser COREINS001: no `double`/`float` in `src/` |
| `tests/` | Architecture, analyser, unit, integration (Testcontainers) and Playwright end-to-end tests |
| `web/` | React + TypeScript + Vite front end (see [web/README.md](web/README.md)) |
| `infra/local` | Docker Compose stack for local development |
| `infra/azure` | Bicep templates for one Azure stamp |

## Prerequisites

.NET 10 SDK (see `global.json`), Node.js 24 (`.nvmrc`), Docker Desktop (or Podman/Rancher), Azure CLI for `infra/azure`.

## Run locally

```bash
# 1. Local services: PostgreSQL 17, Azurite, Gotenberg, Mailpit, WireMock, Aspire Dashboard (all on 127.0.0.1)
cp infra/local/.env.example infra/local/.env        # set the three passwords (and repeat them in the connection strings)
docker compose -f infra/local/compose.yaml up -d --build
#    api: http://localhost:5000  ·  telemetry: http://localhost:18888  ·  mail: http://localhost:8025

# 2. Or run the Host from source against the Compose database
docker compose -f infra/local/compose.yaml up -d postgres
export ConnectionStrings__Core="Host=localhost;Database=coreins;Username=app;Password=<APP_DB_PASSWORD>"
export ConnectionStrings__Migrator="Host=localhost;Database=coreins;Username=migrator;Password=<MIGRATOR_DB_PASSWORD>"
dotnet run --project src/CoreIns.Host --launch-profile migrate   # once per schema change
dotnet run --project src/CoreIns.Host --launch-profile api       # http://localhost:5000
dotnet run --project src/CoreIns.Host --launch-profile worker    # optional

# 3. Front end with hot reload (proxies /api to :5000)
cd web && npm ci && npm run dev                                   # http://localhost:5173
```

Health probes on every container: `/health/live` (process up) and `/health/ready` (PostgreSQL reachable).
Migrations never run at application start-up; only `APP_ROLE=migrate` changes the schema.

## Test

```bash
scripts/ci-local.sh                     # everything CI runs (bash)
.\scripts\ci-local.ps1                  # same, PowerShell
SKIP_INTEGRATION=1 scripts/ci-local.sh  # without Docker; or .\scripts\ci-local.ps1 -SkipIntegration

dotnet test --project tests/CoreIns.ArchitectureTests     # module boundaries
dotnet test --project tests/CoreIns.IntegrationTests      # real PostgreSQL 17 via Testcontainers (Docker)
cd web && npm test                                        # Vitest
cd tests/e2e && npm ci && npx playwright test             # against a running stack (E2E_BASE_URL, default :5000)
```

`tests/e2e` depends only on `@playwright/test` (end-to-end tests, ADR §1), `typescript` and `@types/node`.
`dotnet test` runs on Microsoft.Testing.Platform (opted in by `global.json`). Mutation testing: `dotnet tool restore`
then `dotnet stryker` in a test project folder (configured in `tests/CoreIns.SharedKernel.Tests/stryker-config.json`).

Build rules: warnings are errors, latest .NET analysers, central package management with pinned versions
(`Directory.Packages.props`, each package with its reason), COREINS001 forbids floating point in production code.
CI (`.github/workflows/ci.yml`) also produces CycloneDX SBOMs for .NET and npm and builds the container image.
