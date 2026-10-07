-- Database bootstrap shared by every environment (INFRASTRUCTURE §4 rule 3). Idempotent: safe to run on every deployment.
--
-- Run connected to the application database (`coreins`) as a role that can create roles and extensions:
--   local / CI : the postgres superuser, from infra/local/pg-init/01-init.sh (also used by the Testcontainers tests)
--   Azure      : the Flexible Server administrator, from the `bootstrap` Container Apps job
--                (CoreIns.Host, APP_ROLE=migrate with Migrate__Bootstrap=true; this file is embedded in the Host)
-- The database itself must already exist (Bicep creates it on Azure; the runners create it locally).
--
-- Inputs are session settings, set by the runner before this script, so no password appears in this file:
--   coreins.app_password       password of the runtime role `app`      (api, worker)
--   coreins.migrator_password  password of the DDL role `migrator`     (migrate job)

DO $bootstrap$
DECLARE
  app_password      text := current_setting('coreins.app_password', true);
  migrator_password text := current_setting('coreins.migrator_password', true);
BEGIN
  IF coalesce(app_password, '') = '' OR coalesce(migrator_password, '') = '' THEN
    RAISE EXCEPTION 'coreins bootstrap: set coreins.app_password and coreins.migrator_password before running bootstrap.sql';
  END IF;

  -- Roles: created once; the password is re-applied on every run so rotation is a redeploy.
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'migrator') THEN
    EXECUTE format('CREATE ROLE migrator LOGIN PASSWORD %L', migrator_password);
  ELSE
    EXECUTE format('ALTER ROLE migrator LOGIN PASSWORD %L', migrator_password);
  END IF;

  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'app') THEN
    EXECUTE format('CREATE ROLE app LOGIN PASSWORD %L', app_password);
  ELSE
    EXECUTE format('ALTER ROLE app LOGIN PASSWORD %L', app_password);
  END IF;

  -- Database privileges: nobody but these roles connects; only `migrator` creates schemas (the migrate job).
  EXECUTE format('REVOKE ALL ON DATABASE %I FROM PUBLIC', current_database());
  EXECUTE format('GRANT CONNECT, TEMPORARY ON DATABASE %I TO app', current_database());
  EXECUTE format('GRANT CONNECT, TEMPORARY, CREATE ON DATABASE %I TO migrator', current_database());
END
$bootstrap$;

-- Extension set, identical everywhere (on Azure these must also be allow-listed in `azure.extensions`).
CREATE EXTENSION IF NOT EXISTS pg_trgm;            -- Greek name search (trigram similarity)
CREATE EXTENSION IF NOT EXISTS unaccent;           -- accent-insensitive search
CREATE EXTENSION IF NOT EXISTS pgcrypto;           -- identifier encryption and digests
CREATE EXTENSION IF NOT EXISTS btree_gist;         -- exclusion constraints for non-overlapping effective dates
CREATE EXTENSION IF NOT EXISTS pg_stat_statements; -- query statistics (needs shared_preload_libraries)
