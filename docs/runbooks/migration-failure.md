# Migration failure

Source: `scripts/run-job.sh`, `src/CoreIns.Host/Database/DatabaseMigrator.cs`, `src/CoreIns.Host/Program.cs`
(`APP_ROLE` `bootstrap` | `migrate`), the Container Apps jobs in `infra/azure/main.bicep`.

## Symptoms
Deploy stops at "Run the database bootstrap" or "Run the database migrations" (`<job>: Failed`, or "did not finish within
1200s"; override with `JOB_TIMEOUT_SECONDS`). Stage 3 (apps) never ran, so api/worker still serve the previous image.

## Steps
1. Get the logs of the failed execution:
   `az containerapp job execution list -n migrate -g $RG -o table`, then
   `az containerapp job logs show -n migrate -g $RG --execution <name> --container migrate`
   (or Log Analytics: `ContainerAppConsoleLogs_CL | where ContainerJobName_s == "migrate"`).
2. The migrator exits 1 on `NpgsqlException` or `InvalidOperationException` and logs the cause. Common ones:
   - Missing extension or Greek search objects (`VerifyExtensionsAsync`, `VerifyGreekSearchAsync`): run `bootstrap` first;
     the admin role must be allowed to create the extensions.
   - Invalid identifier: the schema and `Database__AppRole` names must be plain PostgreSQL identifiers. `bootstrap` creates the `app` and `migrator` roles.
   - Authentication failure: the `migrator` password in Key Vault (`connectionstrings-migrator`) differs from the role password.
     See [secret-rotation.md](secret-rotation.md).
   - A module migration error (constraint, data conflict): fix forward in code; never edit an applied migration.
3. Both jobs are idempotent (`CREATE SCHEMA IF NOT EXISTS`, per-module EF migrations). After the fix, re-run only the
   failed job: `RESOURCE_GROUP=$RG scripts/run-job.sh migrate`, or re-run the Deploy workflow.
4. If a migration ran partly, inspect as admin with `psql` (`SELECT * FROM <schema>."__EFMigrationsHistory"`) before re-running.
5. If data is damaged, stop and restore per [backup-restore.md](backup-restore.md); do not retry blindly.
6. Never force the apps onto an image whose migration failed.
