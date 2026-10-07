#!/bin/bash
# Runs once, on first start of an empty data directory (docker-entrypoint-initdb.d).
# Creates the `coreins` database, then applies the shared, idempotent bootstrap (infra/database/bootstrap.sql,
# mounted at /coreins-db): roles `migrator` and `app`, database privileges and the extension set.
# The same script runs on Azure through the `bootstrap` job and in the Testcontainers integration tests.
set -e

: "${APP_DB_PASSWORD:?APP_DB_PASSWORD must be set}"
: "${MIGRATOR_DB_PASSWORD:?MIGRATOR_DB_PASSWORD must be set}"
BOOTSTRAP_SQL="${COREINS_BOOTSTRAP_SQL:-/coreins-db/bootstrap.sql}"

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres <<'SQL'
SELECT 'CREATE DATABASE coreins ENCODING ''UTF8'' TEMPLATE template0'
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'coreins')\gexec
SQL

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname coreins \
  --set=app_password="$APP_DB_PASSWORD" \
  --set=migrator_password="$MIGRATOR_DB_PASSWORD" \
  --set=bootstrap_sql="$BOOTSTRAP_SQL" <<'SQL'
SELECT set_config('coreins.app_password', :'app_password', false),
       set_config('coreins.migrator_password', :'migrator_password', false) \g /dev/null
\i :bootstrap_sql
SQL
