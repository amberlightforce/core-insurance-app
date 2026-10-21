# Dev sign-in and the `superuser` policy

Source: `src/CoreIns.Host/Hosting/DevelopmentAuthentication.cs`, `src/CoreIns.Host/dev-users.Development.json`,
`scripts/check-no-dev-config.py` (D-SLC-03, NX-OT-02).

## What it is
A local sign-in so the stack runs without an Entra tenant. With `DevAuthentication:Enabled=true` and
`ASPNETCORE_ENVIRONMENT=Development`, the api offers `GET /api/plt/v1/dev/users` and `POST /api/plt/v1/dev/sign-in`, which mint a
short-lived bearer token (default 480 minutes) signed with a random key generated at process start: no secret anywhere, and
tokens die with the process. Users are synthetic and listed in `dev-users.Development.json`; `superuser` carries every staff and
platform role (`Platform.Admin`, `Platform.ReleaseManager`, `Staff.*`, and so on).

## Rules
1. Local and E2E stacks only (`infra/local`, `tests/e2e/stack/*.env`, `launchSettings.json`).
2. Never enable it in a `dev` or `prod` Azure stamp. Two guards enforce this: the Host refuses to start if the flag is set and the
   environment is not Development (`DevelopmentAuthentication.Guard`), and CI runs `python3 scripts/check-no-dev-config.py`, which
   fails when `infra/azure`, the Dockerfile, `.github/workflows` or non-Development `appsettings*.json` enable `DevAuthentication`
   or set the environment to Development.
3. Keep `dev-users.Development.json` synthetic: no real names, e-mails or person-to-role mappings.
4. Do not use `superuser` to demonstrate role-restricted behaviour; use the narrow dev users (for example `underwriter`,
   `billing`). Use `superuser` only for setup and tests that need every permission.
5. Real staff access in Azure is Entra ID (Container Apps built-in authentication, `vars.ENTRA_CLIENT_ID`) with app roles, granted by group, never a shared account.
6. If the check script fails in CI, remove the offending setting. Do not add an exemption.
