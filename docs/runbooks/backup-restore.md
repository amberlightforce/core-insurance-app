# Runbook: backup and restore

Scope: the production stamp (`infra/azure/prod.bicepparam`). Names below use the stamp's resource group `<rg>`, server `psql-<prefix>` and storage account `st<prefix>`.

## What is protected, and how

| Data | Protection | Recovery objective |
|---|---|---|
| PostgreSQL `coreins` | Flexible Server automated backups, 35 days retention (point-in-time restore, PITR), geo-redundant backup copy in the paired region, zone-redundant HA | RPO about 5 min (WAL) in-region; geo-restore RPO up to about 1 h. RTO: hours, depends on database size |
| Blob `documents`, `audit-archive` | GZRS replication, time-based immutability (retention from `IMMUTABILITY_RETENTION_DAYS`, policy unlocked), versioning, change feed, 30-day soft delete (blobs and containers) | Immutable blobs cannot be deleted or overwritten inside the retention window; accidental overwrite or delete is recovered from a version or soft delete |
| Blob `inbound`, `exports` | GZRS, versioning, soft delete | Transient data; re-ingest or regenerate |
| Key Vault secrets | Soft delete and purge protection (see `modules/keyvault.bicep`) | Recover deleted secret within retention |
| Server and storage account | `CanNotDelete` management locks (`lockCriticalResources`) | Prevents accidental resource deletion |

Notes: blob point-in-time restore is deliberately not enabled; it is not supported together with immutability policies, and versions plus soft delete cover the same accidents without it. The immutability policy is unlocked. Locking is irreversible and a regulatory decision (PLT retention catalogue); do it only on instruction from compliance.

## Before you start

1. Declare an incident and record the correlation id and time of the bad event (UTC).
2. Decide the target time `T` (just before the damage). Stop writers if corruption is ongoing: scale `ca-*-api` and `ca-*-worker` to zero replicas.
3. Never restore over the live server. Restore to a new server, validate, then cut over.

## A. PostgreSQL point-in-time restore (same region)

```
az postgres flexible-server restore \
  --resource-group <rg> --name psql-<prefix>-restore \
  --source-server psql-<prefix> --restore-time "<T, ISO 8601 UTC>" \
  --vnet <vnet> --subnet snet-db --private-dns-zone <postgres private dns zone id>
```

The restored server keeps the source's networking only if you pass the VNet arguments; it comes up without HA and with default server parameters. Then:

1. Re-apply server parameters from `modules/postgres.bicep` (`azure.extensions`, `shared_preload_libraries`, `require_secure_transport`, `timezone`, `log_statement`). Restart.
2. Validate: connect as `pgadmin` via a Container Apps job or bastion (the server has no public access); check `select max(created_at)` on the outbox/audit tables, row counts of `policy`, `claim`, `payment`, and that `coreins` roles `app` and `migrator` exist.
3. Cut over (choose one):
   - Rename: delete the damaged server only after unlocking (see Locks), then restore under the original name. Preferred when the connection strings in Key Vault must stay valid.
   - Repoint: update Key Vault secrets `core`, `migrator` and `admin` connection strings to the new FQDN and restart the apps. Then reconcile `infra/azure` (server name is derived from `namePrefix`; redeploy would recreate the old name, so prefer the rename path).
4. Run the migrate job against the restored database; confirm it reports no pending migrations.
5. Scale the api and worker back up; watch `/health/ready`, outbox lag and dead letters. Replay dead letters per `dead-letter-replay.md` if present.
6. Reconcile business data written between `T` and the incident from upstream sources (payments from the bank, documents from blob).

## B. PostgreSQL geo-restore (regional loss)

Available because geo-redundant backup is enabled. Use only when the primary region is unavailable.

```
az postgres flexible-server geo-restore \
  --resource-group <rg-dr> --name psql-<prefix>-dr \
  --source-server <resource id of psql-<prefix>> --location <paired region>
```

The rest of the stamp (VNet, Key Vault, Container Apps environment, storage) must exist in the target region: deploy `infra/azure/main.bicep` there with a different `regionCode` first (secrets and images from `acr` geo-replication or a rebuild). Follow steps A.1 to A.6 afterwards. Expect to lose the tail of WAL not yet replicated.

## C. Blob recovery

Always list first and restore the specific blob; do not bulk-restore.

Accidentally deleted blob (soft delete, 30 days):
```
az storage blob list --account-name st<prefix> --container-name documents --include d --auth-mode login
az storage blob undelete --account-name st<prefix> --container-name documents --name <blob> --auth-mode login
```

Overwritten or corrupted blob (version):
```
az storage blob list --account-name st<prefix> --container-name documents --prefix <blob> --include v --auth-mode login
az storage blob copy start --account-name st<prefix> --destination-container documents --destination-blob <blob> \
  --source-container documents --source-blob <blob> --source-blob-version-id <version-id> --auth-mode login
```
Blobs under an active immutability period cannot be overwritten; if a legitimate correction is required, write a new blob name and update the document reference rather than attempting to defeat the policy.

Deleted container (30 days): `az storage container restore --account-name st<prefix> --name <container> --deleted-version <version> --auth-mode login`.

Regional loss: fail over the storage account (`az storage account failover --name st<prefix>`). Failover is a one-way conversion to LRS in the secondary region; re-enable GZRS afterwards and expect loss of writes not yet replicated (check `lastSyncTime` with `az storage account show --expand geoReplicationStats`). Not supported while versioning/immutability constraints block it for your account state; confirm in the portal before relying on it.

## D. Locks

`lock-no-delete` exists on the server and the storage account. To delete or replace one deliberately: `az lock delete --name lock-no-delete --resource-group <rg> --resource-name <name> --resource-type <type>`, perform the change, redeploy `main.bicep` to restore the lock.

## E. Restore drill (do quarterly and after any change to backup settings)

1. Run section A into a throwaway server `psql-<prefix>-drill` at `T = now - 1 day`.
2. Run the validation queries; time the restore and record it below.
3. Restore one document and one audit-archive blob from soft delete and from a version.
4. Delete the drill server. Record the result in the incident log.

| Date | Operator | PITR duration | Blob drill | Result |
|---|---|---|---|---|
| (not yet performed) | | | | |

## Known gaps

- TODO(hardening): drill results, measured RTO and a documented regional-failover rehearsal; the second region stamp is not pre-provisioned.
- TODO(hardening): private endpoints and customer-managed keys (Stage 2 modules) are not in place; the storage account uses a VNet firewall rule.
- TODO(hardening): alerts on backup failure and on geo-replication lag (`modules/monitoring.bicep`).
