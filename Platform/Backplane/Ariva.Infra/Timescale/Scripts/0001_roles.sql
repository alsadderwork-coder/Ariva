-- 0001 Database roles (ARV-006, CWE-269).
--
-- ariva_migration: DDL. The migration job's login is made a member; it creates and owns the schema objects.
-- ariva_runtime:   DML only (SELECT, INSERT, UPDATE, DELETE, sequence use). Every host connects with a login that
--                  is a member of this role and nothing else, so a compromised host cannot change the schema.
-- The runtime login itself (name and password from configuration) is created or updated by the migration job
-- through ariva_ensure_runtime_login, so its password never appears in a script.

DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ariva_migration') THEN
        CREATE ROLE ariva_migration NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE;
    END IF;
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ariva_runtime') THEN
        CREATE ROLE ariva_runtime NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE;
    END IF;
END
$$;

GRANT ariva_migration TO CURRENT_USER;

-- Nobody but the migration role may create objects in public.
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE, CREATE ON SCHEMA public TO ariva_migration;
GRANT USAGE ON SCHEMA public TO ariva_runtime;

-- Existing objects, then everything the migration login creates from now on.
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO ariva_runtime;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO ariva_runtime;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ariva_runtime;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO ariva_runtime;

-- The runtime role reads schema_version (host startup check) but never writes it.
REVOKE INSERT, UPDATE, DELETE ON schema_version FROM ariva_runtime;
GRANT SELECT ON schema_version TO ariva_runtime;

-- Creates or updates a runtime login. Identifier and password are quoted by format (%I, %L), never concatenated.
CREATE OR REPLACE FUNCTION ariva_ensure_runtime_login(login_name text, login_password text)
RETURNS void
LANGUAGE plpgsql
AS $$
BEGIN
    IF login_name IS NULL OR login_name !~ '^[a-z_][a-z0-9_]{0,62}$' THEN
        RAISE EXCEPTION 'invalid runtime login name';
    END IF;
    IF login_password IS NULL OR length(login_password) < 16 THEN
        RAISE EXCEPTION 'runtime login password must have at least 16 characters';
    END IF;
    IF EXISTS (SELECT FROM pg_roles WHERE rolname = login_name AND (rolsuper OR rolcreaterole OR rolcreatedb)) THEN
        RAISE EXCEPTION 'runtime login % is privileged; use a dedicated login', login_name;
    END IF;

    IF EXISTS (SELECT FROM pg_roles WHERE rolname = login_name) THEN
        EXECUTE format('ALTER ROLE %I WITH LOGIN INHERIT PASSWORD %L', login_name, login_password);
    ELSE
        EXECUTE format('CREATE ROLE %I WITH LOGIN INHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD %L', login_name, login_password);
    END IF;
    EXECUTE format('GRANT ariva_runtime TO %I', login_name);
    EXECUTE format('GRANT CONNECT ON DATABASE %I TO %I', current_database(), login_name);
END
$$;

REVOKE ALL ON FUNCTION ariva_ensure_runtime_login(text, text) FROM PUBLIC;
