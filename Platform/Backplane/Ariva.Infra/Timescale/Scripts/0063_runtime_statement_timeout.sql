-- ARV-096d (CWE-400, ASVS 5.0 V15.2.2): the server's own limit on one statement of the runtime login.
-- A setting on the ariva_runtime group role does not reach its members (a login takes the settings of its own role only),
-- and no script knows the login's name, so the migration job sets it on the login itself, after
-- ariva_ensure_runtime_login, with the value of Database:StatementTimeoutSeconds. It is a backstop above every client
-- timeout the runtime login uses: it ends a statement whose client stopped waiting without the server knowing. The
-- migration login and the validation reader keep their own (none set here), so long migrations are not affected.
-- Identifier and value are quoted by format (%I, %L), never concatenated.
CREATE OR REPLACE FUNCTION ariva_set_runtime_statement_timeout(login_name text, timeout_seconds integer)
RETURNS void
LANGUAGE plpgsql
AS $$
BEGIN
    IF login_name IS NULL OR login_name !~ '^[a-z_][a-z0-9_]{0,62}$' THEN
        RAISE EXCEPTION 'invalid runtime login name';
    END IF;
    IF timeout_seconds IS NULL OR timeout_seconds < 1 OR timeout_seconds > 3600 THEN
        RAISE EXCEPTION 'runtime statement timeout must be 1 to 3600 seconds';
    END IF;
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = login_name AND rolcanlogin) THEN
        RAISE EXCEPTION 'runtime login % does not exist', login_name;
    END IF;
    IF EXISTS (SELECT FROM pg_roles WHERE rolname = login_name AND (rolsuper OR rolcreaterole OR rolcreatedb)) THEN
        RAISE EXCEPTION 'runtime login % is privileged; use a dedicated login', login_name;
    END IF;
    IF NOT EXISTS (
        SELECT FROM pg_auth_members m
        JOIN pg_roles g ON g.oid = m.roleid
        JOIN pg_roles l ON l.oid = m.member
        WHERE g.rolname = 'ariva_runtime' AND l.rolname = login_name) THEN
        RAISE EXCEPTION 'login % is not a member of ariva_runtime', login_name;
    END IF;

    EXECUTE format('ALTER ROLE %I IN DATABASE %I SET statement_timeout = %L', login_name, current_database(), timeout_seconds || 's');
END
$$;

REVOKE ALL ON FUNCTION ariva_set_runtime_statement_timeout(text, integer) FROM PUBLIC;
