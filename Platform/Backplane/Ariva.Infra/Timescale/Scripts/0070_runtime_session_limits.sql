-- ARV-292 (CWE-400): the server's own limits on a runtime login session that its host stopped using. A host whose connection is
-- lost on the network (a client timeout whose cancel cannot reach the server, a middlebox that keeps the server side open)
-- leaves its session open, idle in its transaction with its locks (a report delivery's row, the deliveries' report slot, a
-- lock unit's advisory lock), until the server's TCP notices the client gone. idle_in_transaction_session_timeout ends a
-- session idle in a transaction for longer than the limit, so its locks go and nothing of its transaction commits; TCP
-- keepalives end a session whose client is gone sooner, idle or not. The limit is above every transaction a host holds open
-- while it waits on other work (the inventory in docs/security/costly-operations.md; a host refuses to start below it). As for
-- the statement timeout (script 0063), a setting of the ariva_runtime group role would not reach its members and no script knows
-- the login's name, so the migration job sets these on the login itself, after ariva_ensure_runtime_login, with
-- Database:IdleInTransactionTimeoutSeconds and the keepalive values DatabaseSettings fixes. All four are user settings: the
-- migration login sets them as it sets statement_timeout. The migration login and the validation reader keep their own (none
-- set here). Identifier and values are quoted by format (%I, %L), never concatenated.
CREATE OR REPLACE FUNCTION ariva_set_runtime_session_limits(
    login_name text, idle_in_transaction_seconds integer, keepalive_idle_seconds integer, keepalive_interval_seconds integer,
    keepalive_count integer)
RETURNS void
LANGUAGE plpgsql
AS $$
BEGIN
    IF login_name IS NULL OR login_name !~ '^[a-z_][a-z0-9_]{0,62}$' THEN
        RAISE EXCEPTION 'invalid runtime login name';
    END IF;
    IF idle_in_transaction_seconds IS NULL OR idle_in_transaction_seconds < 1 OR idle_in_transaction_seconds > 3600 THEN
        RAISE EXCEPTION 'runtime idle in transaction limit must be 1 to 3600 seconds';
    END IF;
    IF keepalive_idle_seconds IS NULL OR keepalive_idle_seconds < 1 OR keepalive_idle_seconds > 600
       OR keepalive_interval_seconds IS NULL OR keepalive_interval_seconds < 1 OR keepalive_interval_seconds > 60
       OR keepalive_count IS NULL OR keepalive_count < 1 OR keepalive_count > 20 THEN
        RAISE EXCEPTION 'runtime keepalives must be 1 to 600 seconds idle, 1 to 60 seconds apart and 1 to 20 probes';
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

    EXECUTE format('ALTER ROLE %I IN DATABASE %I SET idle_in_transaction_session_timeout = %L', login_name, current_database(), idle_in_transaction_seconds || 's');
    EXECUTE format('ALTER ROLE %I IN DATABASE %I SET tcp_keepalives_idle = %L', login_name, current_database(), keepalive_idle_seconds::text);
    EXECUTE format('ALTER ROLE %I IN DATABASE %I SET tcp_keepalives_interval = %L', login_name, current_database(), keepalive_interval_seconds::text);
    EXECUTE format('ALTER ROLE %I IN DATABASE %I SET tcp_keepalives_count = %L', login_name, current_database(), keepalive_count::text);
END
$$;

REVOKE ALL ON FUNCTION ariva_set_runtime_session_limits(text, integer, integer, integer, integer) FROM PUBLIC;
