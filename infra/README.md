# Infrastructure

| Path | Contents |
|---|---|
| `database/bootstrap.sql` | The one idempotent database bootstrap for every environment: roles `migrator` and `app`, database privileges, the five extensions |
| `local/` | Docker Compose stack (INFRASTRUCTURE §3.2); `pg-init/` creates `coreins` and runs `database/bootstrap.sql` |
| `azure/` | Bicep for one stamp (INFRASTRUCTURE §6); deployed by `.github/workflows/deploy.yml` |

## Database lifecycle (same steps everywhere)

1. **Bootstrap** (server administrator; idempotent, re-applies role passwords):
   - local and Testcontainers: `local/pg-init/01-init.sh` on first start of the data volume;
   - Azure: Container Apps job `bootstrap` = the application image with `APP_ROLE=migrate` and `Migrate__Bootstrap=true`,
     reading `ConnectionStrings__Admin`, `Database__AppPassword` and `Database__MigratorPassword` from Key Vault.
2. **Migrate** (role `migrator`): job `migrate` / Compose service `migrate`. It first checks that all five extensions
   exist and stops with a clear error otherwise, then creates the module schemas and installs Hangfire's storage.
3. **Run** (role `app`): `api` and `worker`, started only after the migration succeeded.

## Deltas to the infrastructure specification

These are the differences between this repository and `core-insurance-infra` (ARCHITECTURE-DECISIONS.md,
INFRASTRUCTURE.md) found while building the scaffold. The spec files themselves are owned elsewhere and are not
edited here.

| Spec section | Delta in this repository | Why |
|---|---|---|
| §5 Configuration | Added `ConnectionStrings__Migrator` (migrate job connects as `migrator`; falls back to `ConnectionStrings__Core`), `Database__AppRole`, `Database__Name`, and for the bootstrap job `Migrate__Bootstrap`, `ConnectionStrings__Admin`, `Database__AppPassword`, `Database__MigratorPassword`. Added `Email__Endpoint` / `Email__SenderDomain` in Azure | Least privilege: DDL and runtime use different roles; the administrator credential is used only by the bootstrap job |
| §3.2 Local services | Images pinned to exact tags (`postgres:17.11`, `wiremock/wiremock:3.13.2`, ...); PostgreSQL runs with `shared_preload_libraries=pg_stat_statements`; `APP_DB_PASSWORD` / `MIGRATOR_DB_PASSWORD` in `.env`; `depends_on` uses `service_healthy` / `service_completed_successfully`; `infra/database` is mounted for the shared bootstrap | The spec sample uses floating tags and does not start `migrate` reliably before `api` |
| §6.2 / §6.5 Azure | Nothing in the spec created the `app`/`migrator` roles or the extensions on Azure (`azure.extensions` only allow-lists them). Added the `bootstrap` job and a staged deployment: infrastructure, image, jobs, bootstrap, migrate, then apps | Parity rule §4.3 and "migrations run as a job, never on start-up" |
| §6.1 Security | Built-in authentication redirects browser routes but excludes `/api/*`; the application returns 401 for unauthenticated API calls. `entraClientId` is required, so authentication can never be silently disabled | §6.1 requires 401 (not a redirect) for API calls |
| §7 Repository layout | Added `tools/` (Roslyn analyser COREINS001), `infra/database/`, `scripts/` | Analyser and shared bootstrap have no place in the spec layout |
| ADR §2 rule 2 | "Analyser rule" is implemented by COREINS001 (`tools/CoreIns.Analyzers`), not `Microsoft.CodeAnalysis.BannedApiAnalyzers` | RS0030 does not report `double`/`float` keywords, literals or conversions |

Known open items: the email sender role is `Contributor` on the Communication Services resource only; the
`/api/*` exclusion of built-in authentication is verified by the deploy smoke test (expects 401).
