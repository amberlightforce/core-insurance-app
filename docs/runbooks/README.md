# Runbooks

Short, practical procedures for operating a stamp. Each is based on the real scripts and code it cites.

| Runbook | Use when |
| --- | --- |
| [deploy-rollback.md](deploy-rollback.md) | Shipping a build to `dev` or `prod`, or backing one out |
| [migration-failure.md](migration-failure.md) | The `bootstrap` or `migrate` job fails during a deploy |
| [secret-rotation.md](secret-rotation.md) | Rotating database role passwords or the Entra client secret |
| [dead-letter-replay.md](dead-letter-replay.md) | An event handler parked a message in `plt.outbox_dead_letter` |
| [incident-triage.md](incident-triage.md) | A user or alert reports an error; trace it by correlation id |
| [dev-superuser-policy.md](dev-superuser-policy.md) | Rules for the Development-only sign-in and its `superuser` |
| [regulatory-go-live-gates.md](regulatory-go-live-gates.md) | Before activating a country pack in Production |
| [backup-restore.md](backup-restore.md) | Backup and restore of PostgreSQL and blobs (written by NX-OT-04; may land after this index) |

Conventions: stamps are `dev` and `prod` (GitHub environments); the resource group is `vars.AZURE_RESOURCE_GROUP`
(`rg-coreins-<env>-<region>`, shown as `$RG` below); the container apps are `api`, `worker`, `gotenberg`; the jobs are
`bootstrap` and `migrate`.
