# Deploy and rollback

Source of truth: `.github/workflows/deploy.yml`, `scripts/run-job.sh`, `infra/azure/main.bicep`,
`infra/azure/{dev,prod}.bicepparam`.

## Deploy
1. GitHub, Actions, **Deploy**, Run workflow: pick `environment` (`dev` or `prod`); leave `provision` true unless infra is unchanged.
2. The run is serialised per environment (`concurrency: deploy-<env>`, not cancelled). Order, fixed by design:
   infrastructure (`deployJobs=false deployApps=false`) -> build and push `coreins:<github.sha>` to ACR ->
   jobs deployed on the new image -> `scripts/run-job.sh bootstrap` -> `scripts/run-job.sh migrate` ->
   apps (`gotenberg`, `worker`, `api`) -> smoke test.
3. Smoke test (`scripts/smoke.sh`, run by the workflow; also runnable by hand with the api FQDN): `/health/live` and
   `/health/ready` must return 200 (retried while the new revision takes traffic; the ready body must not report an
   Unhealthy check), anonymous calls to several `/api/<module>/v1/...` reads must return **401** (never a sign-in
   redirect), and, if a bearer token is available (`SMOKE_TOKEN`; the workflow tries
   `az account get-access-token --resource api://$ENTRA_CLIENT_ID`), authenticated reads must return 2xx.
4. Apps update only after `migrate` succeeds, so a failed migration leaves the previous api/worker running
   (see [migration-failure.md](migration-failure.md)).
5. Prerequisites for `prod`: vars `STAMP_LEGAL_ENTITY` and `IMMUTABILITY_RETENTION_DAYS`, and the go-live gates in
   [regulatory-go-live-gates.md](regulatory-go-live-gates.md). `scripts/check-no-dev-config.py` and
   `scripts/check-keyvault-rbac.py` must be green on the commit being deployed.

## Prod approval gate (one-time GitHub setup, done by a repo admin)
The workflow cannot enforce reviewers itself; the deploy job pauses until the environment's protection rule is satisfied.
1. Repo **Settings > Environments > prod** (create it if missing).
2. Tick **Required reviewers**, add at least one person or team other than whoever triggers deploys, and tick
   **Prevent self-review** so the triggering user cannot approve their own run.
3. Under **Deployment branches and tags** choose **Selected branches** and add `main` (the workflow also refuses prod
   from any other ref).
4. Optionally add a **Wait timer**. Save.
5. Verify: run **Deploy** with `prod`; the run must show "Waiting for review" before any step executes.

Schema changes must follow [migration-policy.md](migration-policy.md) (expand/contract).

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
