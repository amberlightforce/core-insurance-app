# Deploy and rollback

Source of truth: `.github/workflows/deploy.yml`, `scripts/run-job.sh`, `infra/azure/main.bicep`,
`infra/azure/{dev,prod}.bicepparam`.

## Deploy
1. GitHub, Actions, **Deploy**, Run workflow: pick `environment` (`dev` or `prod`); leave `provision` true unless infra is unchanged.
2. The run is serialised per environment (`concurrency: deploy-<env>`, not cancelled). Order, fixed by design:
   infrastructure (`deployJobs=false deployApps=false`) -> build and push `coreins:<github.sha>` to ACR ->
   jobs deployed on the new image -> `scripts/run-job.sh bootstrap` -> `scripts/run-job.sh migrate` ->
   apps (`gotenberg`, `worker`, `api`) -> smoke test.
3. Smoke test: `GET https://<api fqdn>/health/live` must succeed and `GET /api/plt/v1/health` must return **401**
   (never a sign-in redirect). After a prod deploy also check `/health/ready` by hand.
4. Apps update only after `migrate` succeeds, so a failed migration leaves the previous api/worker running
   (see [migration-failure.md](migration-failure.md)).
5. Prerequisites for `prod`: vars `STAMP_LEGAL_ENTITY` and `IMMUTABILITY_RETENTION_DAYS`, and the go-live gates in
   [regulatory-go-live-gates.md](regulatory-go-live-gates.md). `scripts/check-no-dev-config.py` and
   `scripts/check-keyvault-rbac.py` must be green on the commit being deployed.

## Rollback
Images are tagged with the commit SHA, so rollback means deploying an older SHA.
1. Find the last good SHA (previous green Deploy run, or `az containerapp revision list -n api -g $RG -o table`).
2. Preferred: re-run **Deploy** with `provision` false, selecting the older ref under "Use workflow from"
   (the workflow builds from the ref it runs on).
3. Fast path for a bad app revision only (no migration in the bad release):
   `az containerapp update -n api -g $RG --image <acr>/coreins:<good-sha>`, and the same for `worker`.
   If the app is in multiple-revision mode, shift traffic instead with `az containerapp ingress traffic set`.
4. **Schema:** migrations are forward-only. Do not roll the image back across a release that changed the schema unless the
   old code is compatible with the new schema (expand/contract). If it is not, fix forward, or restore per
   [backup-restore.md](backup-restore.md). Never edit an applied migration.
5. After rollback repeat the smoke test and record the incident.
