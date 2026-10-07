# Local stack (Docker Compose)

Synthetic data only. Every port is bound to 127.0.0.1.

```bash
cp infra/local/.env.example infra/local/.env     # set the three passwords; repeat them in the connection strings
docker compose -f infra/local/compose.yaml up -d --build
infra/local/smoke.sh                             # optional: proves the stack over HTTP with curl
docker compose -f infra/local/compose.yaml down  # add -v to drop the database volume
```

## What starts, in order

| Service | Role |
|---|---|
| `postgres` | PostgreSQL 17.11. On a fresh volume `pg-init/01-init.sh` creates `coreins` and runs `infra/database/bootstrap.sql` and `greek-search.sql` |
| `bootstrap` | The Host with `APP_ROLE=migrate`, `Migrate__Bootstrap=true` and the superuser credential: re-applies roles, privileges, extensions and the Greek search objects (idempotent; brings an existing volume up to date). Same code path as the Azure `bootstrap` job |
| `migrate` | The Host with `APP_ROLE=migrate` as role `migrator`: creates the module schemas, installs Hangfire storage, applies every module's EF Core migrations (`ModuleCatalog.Databases`: `plt`, `pty`, …) and grants role `app` its privileges. Exits 0 |
| `api` | REST API, OpenAPI (`/openapi/v1.json` in Development), the built React app, on http://localhost:5000 |
| `worker` | Outbox dispatcher and Hangfire server |
| `azurite`, `gotenberg`, `mailpit`, `wiremock`, `otel` | Blob storage, PDF rendering, mail catcher (http://localhost:8025), external-service doubles, Aspire dashboard (http://localhost:18888) |

`api` and `worker` start only after `migrate` completed successfully. Migrations never run at api/worker start-up.

Ports already used on your machine: every host port is overridable in `.env` (`PG_HOST_PORT`, `API_HOST_PORT`,
`AZURITE_HOST_PORT`, `GOTENBERG_HOST_PORT`, `MAILPIT_UI_HOST_PORT`, `MAILPIT_SMTP_HOST_PORT`, `WIREMOCK_HOST_PORT`,
`OTEL_UI_HOST_PORT`, `OTEL_HOST_PORT`; see `.env.example`).

## Development sign-in (D-SLC-03)

`.env` sets `ASPNETCORE_ENVIRONMENT=Development` and `DevAuthentication__Enabled=true`. Then the api offers:

- `GET /api/plt/v1/dev/users`: the configured synthetic users (`src/CoreIns.Host/appsettings.Development.json`):
  `underwriter` (role `Staff.Underwriter`), `billing` (`Staff.Billing`), `finance` (`Staff.Finance`), `admin` (`Platform.Admin`).
- `POST /api/plt/v1/dev/sign-in` with `{"userId":"underwriter"}`: a bearer token (8 h) signed with a key generated at
  api start (a restart invalidates tokens; no signing secret exists in configuration).

The web app has a page at `/dev/sign-in` (`npm run dev`, http://localhost:5173/dev/sign-in) that does the same and
keeps the token for the browser tab. If `DevAuthentication__Enabled=true` reaches any environment other than
Development, the Host refuses to start. Production uses Entra ID only.

What each role may do is configuration (`Platform:Permissions:Grants` in `src/CoreIns.Host/appsettings.json`:
permission → roles). The Entra app roles must use the same role names.

## Calling the API with curl

```bash
TOKEN=$(curl -s -X POST localhost:5000/api/plt/v1/dev/sign-in -H 'Content-Type: application/json' \
  -d '{"userId":"underwriter"}' | python3 -c 'import sys,json;print(json.load(sys.stdin)["accessToken"])')

# Commands need an Idempotency-Key (a fresh UUID); a retry with the same key returns the first result.
curl -s -X POST localhost:5000/api/pty/v1/parties -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' -H "Idempotency-Key: $(uuidgen)" --data-binary @party.json

# Names, identifiers and the single search box go in a POST body, never in a URL (D-SLC-05); GET takes only partyNumber.
curl -s -X POST localhost:5000/api/pty/v1/parties/search -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"name":"efthymiou"}'
curl -s -X POST localhost:5000/api/pty/v1/parties/search -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"identifierScheme":"AFM","identifierValue":"123456783"}'
curl -s "localhost:5000/api/pty/v1/parties/<partyId>" -H "Authorization: Bearer $TOKEN"                     # P2 masked
curl -s "localhost:5000/api/pty/v1/parties/<partyId>?revealPurpose=RATING" -H "Authorization: Bearer $TOKEN"  # P2 shown, audited
```

`smoke.sh` contains a complete `party.json`. On Windows, pass non-ASCII text in files or percent-encoded URLs (the
shell's code page can mangle Greek command-line arguments).

## Field-level encryption locally

Identifier values and birth dates are encrypted (AES-256-GCM) with data keys stored wrapped in `plt.data_key`. The
local key provider derives the key-encryption keys from `DataProtection__LocalMasterKey` (a synthetic placeholder in
`.env.example`; generate your own with `openssl rand -base64 32`). Changing it makes existing encrypted values
unreadable: run `docker compose down -v` to start over. Outside Development the Host refuses the local provider.

## Automated E2E-01 (fresh, isolated stack)

`tests/e2e/run-e2e01.sh` builds the image (tag `coreins-host:e2e`), starts a separate compose project `coreins-e2e` with its own
volume and host ports in the 26000 range (`tests/e2e/stack/e2e.env`, API on http://127.0.0.1:26000), runs the API-level E2E-01
happy path (`tests/e2e/tests/e2e01.spec.ts`: product import, party, submission, quote, ANNUAL bind, invoice with stub MARK,
exact payment, FIN journals, as-of policy read) and tears everything down with `down -v`. It never touches the `coreins`
project. `KEEP_STACK=1` leaves the stack up; `SKIP_BUILD=1` reuses the image. CI runs it in the `e2e01` job.
