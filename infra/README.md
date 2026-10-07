# Infrastructure

| Path | Contents |
|---|---|
| `database/bootstrap.sql` | The one idempotent database bootstrap for every environment: roles `migrator` and `app`, database privileges, the five extensions |
| `database/greek-search.sql` | Idempotent Greek search objects (F-1e): `coreins_search_key()` (twin of the GR pack `GreekSearchNormalizer`, parity-tested), ICU collation `el_gr_ci_ai`, text-search configuration `greek_unaccent`. Not yet run by the migrate job (wiring is a follow-up) |
| `local/` | Docker Compose stack (INFRASTRUCTURE §3.2); `pg-init/` creates `coreins` and runs `database/bootstrap.sql` |
| `azure/` | Bicep for one stamp (INFRASTRUCTURE §6); deployed by `.github/workflows/deploy.yml` |

## Database lifecycle (same steps everywhere)

1. **Bootstrap** (server administrator; idempotent, re-applies role passwords):
   - local and Testcontainers: `local/pg-init/01-init.sh` on first start of the data volume;
   - Azure: Container Apps job `bootstrap` = the application image with `APP_ROLE=migrate` and `Migrate__Bootstrap=true`,
     running as its own identity `bootstrap` and reading `ConnectionStrings__Admin`, `Database__AppPassword` and
     `Database__MigratorPassword` from Key Vault. The Host turns each password into a SCRAM-SHA-256 verifier
     (`CoreIns.Host.Database.ScramVerifier`, RFC 5802/7677) before anything is sent, so no plaintext role password
     reaches the server. Role passwords must be printable ASCII.
   - On a managed server whose `postgres` database the administrator does not own, the CONNECT revoke on `postgres`
     is skipped with a NOTICE; everything else is applied.
2. **Migrate** (role `migrator`): job `migrate` / Compose service `migrate`. It first checks that all five extensions
   exist and stops with a clear error otherwise, then creates the module schemas and installs Hangfire's storage.
3. **Run** (role `app`): `api` and `worker`, started only after the migration succeeded.

## Key Vault access (least privilege)

"Key Vault Secrets User" is assigned per secret, never on the vault (`azure/modules/keyvault.bicep`). Each workload has
its own user-assigned identity:

| Secret | Readable by |
|---|---|
| `connectionstrings-core` | `api`, `worker` |
| `entra-client-secret` | `api` (Container Apps built-in authentication) |
| `connectionstrings-migrator` | `migrate` |
| `connectionstrings-admin`, `db-app-password`, `db-migrator-password` | `bootstrap` |

`scripts/check-keyvault-rbac.py` enforces this on the compiled template in CI (job `infra`): it fails on any
vault-scope assignment, any Key Vault role outside the Key Vault module, any reader outside the table above, and any
app or job that references another workload's secret or identity.

## Statement logging must stay off

`log_statement` must remain `none` and `log_min_duration_statement` `-1` on every stamp (set explicitly in
`azure/modules/postgres.bicep`). With statement logging on, the bootstrap's SCRAM verifiers and application data
(personal data) would be written to server logs. Query performance is observed through `pg_stat_statements`, which
records normalised statements without parameter values. Locally, `pg-init` passes the synthetic `.env` passwords
inside the container; that path is development-only.

## Deltas to the infrastructure specification

These are the differences between this repository and `core-insurance-infra` (ARCHITECTURE-DECISIONS.md,
INFRASTRUCTURE.md) found while building the scaffold. The spec files themselves are owned elsewhere and are not
edited here.

| Spec section | Delta in this repository | Why |
|---|---|---|
| §5 Configuration | Added `ConnectionStrings__Migrator` (migrate job connects as `migrator`; falls back to `ConnectionStrings__Core`), `Database__AppRole`, `Database__Name`, and for the bootstrap job `Migrate__Bootstrap`, `ConnectionStrings__Admin`, `Database__AppPassword`, `Database__MigratorPassword`. Added `Email__Endpoint` / `Email__SenderDomain` in Azure | Least privilege: DDL and runtime use different roles; the administrator credential is used only by the bootstrap job |
| §3.2 Local services | Images pinned to exact tags (`postgres:17.11`, `wiremock/wiremock:3.13.2`, ...); PostgreSQL runs with `shared_preload_libraries=pg_stat_statements`; `APP_DB_PASSWORD` / `MIGRATOR_DB_PASSWORD` in `.env`; `depends_on` uses `service_healthy` / `service_completed_successfully`; `infra/database` is mounted for the shared bootstrap | The spec sample uses floating tags and does not start `migrate` reliably before `api` |
| §6.2 / §6.5 Azure | Nothing in the spec created the `app`/`migrator` roles or the extensions on Azure (`azure.extensions` only allow-lists them). Added the `bootstrap` job and a staged deployment: infrastructure, image, jobs, bootstrap, migrate, then apps | Parity rule §4.3 and "migrations run as a job, never on start-up" |
| §6.2 Managed identities | Five identities (`api`, `worker`, `migrate`, `gotenberg`, `bootstrap`) and per-secret Key Vault roles instead of vault-wide access; the Entra client secret is a deployment secret (`ENTRA_CLIENT_SECRET`) stored in Key Vault by Bicep | Least privilege: no app can read the administrator or migrator credential |
| §6.4 Security controls | PostgreSQL `log_statement=none` and `log_min_duration_statement=-1` set explicitly; role passwords applied as SCRAM verifiers | Credentials and personal data never in server logs |
| §6.1 Security | Built-in authentication redirects browser routes but excludes `/api/*`; the application returns 401 for unauthenticated API calls. `entraClientId` is required, so authentication can never be silently disabled | §6.1 requires 401 (not a redirect) for API calls |
| §7 Repository layout | Added `tools/` (Roslyn analyser COREINS001), `infra/database/`, `scripts/` | Analyser and shared bootstrap have no place in the spec layout |
| ADR §2 rule 2 | "Analyser rule" is implemented by COREINS001 (`tools/CoreIns.Analyzers`), not `Microsoft.CodeAnalysis.BannedApiAnalyzers` | RS0030 does not report `double`/`float` keywords, literals or conversions |

Known open items:
- The worker sends email through a custom role "CoreIns Email Sender" on the Communication Services resource only,
  with `Microsoft.Communication/CommunicationServices/Read`, `.../CommunicationServices/Write` and
  `Microsoft.Communication/EmailServices/write`, the set Microsoft documents for Entra ID email sending. The actions
  exist (`az provider operation show --namespace Microsoft.Communication`; there is no email data action), but whether
  they are sufficient is confirmed only by the first real send on a stamp.
- The `/api/*` exclusion of built-in authentication is verified by the deploy smoke test (expects 401).
