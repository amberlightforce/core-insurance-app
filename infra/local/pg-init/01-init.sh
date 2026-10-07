#!/bin/bash
# Runs once, on first start of an empty data directory (docker-entrypoint-initdb.d).
# Creates the `coreins` database, the `migrator` (DDL owner) and `app` (runtime) roles, and the extension set
# that must be identical in every environment (INFRASTRUCTURE §4 rule 3).
# Also used by tests/CoreIns.IntegrationTests (Testcontainers), so local and CI databases match.
set -e

: "${APP_DB_PASSWORD:?APP_DB_PASSWORD must be set}"
: "${MIGRATOR_DB_PASSWORD:?MIGRATOR_DB_PASSWORD must be set}"

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres \
  --set=app_password="$APP_DB_PASSWORD" \
  --set=migrator_password="$MIGRATOR_DB_PASSWORD" <<'SQL'
CREATE ROLE migrator LOGIN PASSWORD :'migrator_password';
CREATE ROLE app LOGIN PASSWORD :'app_password';
CREATE DATABASE coreins OWNER migrator ENCODING 'UTF8' TEMPLATE template0;
REVOKE ALL ON DATABASE coreins FROM PUBLIC;
GRANT CONNECT, TEMPORARY ON DATABASE coreins TO app;
SQL

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname coreins <<'SQL'
-- Greek name search (accent-insensitive, trigram similarity)
CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE EXTENSION IF NOT EXISTS unaccent;
-- Identifier encryption and digests
CREATE EXTENSION IF NOT EXISTS pgcrypto;
-- Exclusion constraints for non-overlapping effective dates
CREATE EXTENSION IF NOT EXISTS btree_gist;
-- Query statistics (requires shared_preload_libraries=pg_stat_statements)
CREATE EXTENSION IF NOT EXISTS pg_stat_statements;

-- Nobody creates objects in public; module schemas are created by the migrate job.
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
SQL
