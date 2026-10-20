# Secret rotation

Source: `.github/workflows/deploy.yml` (header comment), `infra/azure/modules/keyvault.bicep`, `scripts/check-keyvault-rbac.py`.

Secrets live in Key Vault (RBAC, purge protection) and Container Apps read them as Key Vault references. Access is per
secret and per identity; CI (`check-keyvault-rbac.py`) fails any widening.

| Secret | Read by | GitHub environment secret |
| --- | --- | --- |
| `connectionstrings-core` | api, worker | `APP_DB_PASSWORD` |
| `db-app-password` | bootstrap | `APP_DB_PASSWORD` |
| `connectionstrings-migrator` | migrate | `MIGRATOR_DB_PASSWORD` |
| `db-migrator-password` | bootstrap | `MIGRATOR_DB_PASSWORD` |
| `connectionstrings-admin` | bootstrap | `PG_ADMIN_PASSWORD` |
| `entra-client-secret` | api | `ENTRA_CLIENT_SECRET` |

## Rotate a database role password (`app` or `migrator`)
1. Choose a printable-ASCII password (bootstrap computes a SCRAM-SHA-256 verifier from it).
2. Update the GitHub environment secret (`APP_DB_PASSWORD` or `MIGRATOR_DB_PASSWORD`).
3. Run **Deploy** for the stamp with `provision` true. Bicep rewrites the Key Vault secrets, `bootstrap` (idempotent)
   applies the new password to the role, and the apps get a new revision with the new reference.
4. Verify: `/health/ready` is OK, `az containerapp logs show -n api -g $RG` shows no authentication errors, `migrate` ran green.
5. Existing connections keep the old password briefly; expect transient errors while the last app restarts. Do it off-peak.

## Rotate the PostgreSQL administrator password
Update `PG_ADMIN_PASSWORD` and deploy with `provision` true. Only `bootstrap` reads it.

## Rotate the Entra client secret
1. Entra admin centre, staff app registration (`vars.ENTRA_CLIENT_ID`): create a new client secret, keeping the old one valid.
2. Update `ENTRA_CLIENT_SECRET` and deploy with `provision` true.
3. Verify a staff sign-in through the api FQDN, then delete the old secret in Entra.

## If a secret leaked
Rotate at once as above, then review Key Vault access logs and the audit trail for use of the credential.
Never paste secrets into issues, logs or the repo.
