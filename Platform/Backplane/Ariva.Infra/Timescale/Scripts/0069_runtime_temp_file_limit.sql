-- ARV-290 (CWE-400): the server's own cap on the temporary files one session of the runtime login may hold at once. A sort or
-- hash that outgrows work_mem spills to temporary files in the data directory's disk, and without a cap one statement could fill
-- it; past the cap PostgreSQL ends the statement (SQLSTATE 53400) instead. As for the statement timeout (script 0063), a setting
-- of the ariva_runtime group role would not reach its members and no script knows the login's name, so the migration job sets it
-- on the login itself, after ariva_ensure_runtime_login, with Database:TempFileLimitMegabytes. temp_file_limit is a superuser
-- setting: the migration login (never a superuser) may set it on another role only once a superuser has granted it SET on the
-- parameter (the database chart's first start does; on an existing server, ariva-db.sh grant-settings or GRANT SET ON PARAMETER
-- temp_file_limit TO the migration login, wiki 04); the migration job checks that before it calls this function. The migration
-- login and the validation reader keep their own (none set here), so long migrations are not affected.
-- Identifier and value are quoted by format (%I, %L), never concatenated.
CREATE OR REPLACE FUNCTION ariva_set_runtime_temp_file_limit(login_name text, limit_megabytes integer)
RETURNS void
LANGUAGE plpgsql
AS $$
BEGIN
    IF login_name IS NULL OR login_name !~ '^[a-z_][a-z0-9_]{0,62}$' THEN
        RAISE EXCEPTION 'invalid runtime login name';
    END IF;
    IF limit_megabytes IS NULL OR limit_megabytes < 1 OR limit_megabytes > 1048576 THEN
        RAISE EXCEPTION 'runtime temporary file limit must be 1 to 1048576 megabytes';
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

    EXECUTE format('ALTER ROLE %I IN DATABASE %I SET temp_file_limit = %L', login_name, current_database(), limit_megabytes || 'MB');
END
$$;

REVOKE ALL ON FUNCTION ariva_set_runtime_temp_file_limit(text, integer) FROM PUBLIC;
