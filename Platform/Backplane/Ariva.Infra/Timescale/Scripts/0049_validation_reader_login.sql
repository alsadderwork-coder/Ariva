-- 0049 The validation reader login (ARV-104g1; CWE-269, CWE-863, CWE-287). Script 0043 created ariva_validation_reader, the
-- NOLOGIN role that alone may read the shadow nowcast (SELECT on that one table, USAGE on schema public), and no login held
-- it. The validation service (the campaign results of ARV-104g2 and ARV-104g, hosted by Ariva.Api.Main) gets a login of its
-- own, created and updated by the migration job through the function below from Database:ValidationReader, as 0001's
-- ariva_ensure_runtime_login does for the hosts' runtime login. Its name and password come from configuration (in the
-- clusters, the secret ariva-validation-reader), never from a script.
--
-- What the login gets is exactly what the validation reads through it need: membership of ariva_validation_reader and
-- CONNECT on this database. Nothing else: the validation service reads its other inputs (campaigns, counts, tracer runs,
-- desk observations, queue and desk minutes) through the hosts' runtime login like every other service, so the reader
-- password reaches the shadow nowcast and nothing more, and the login is never a member of ariva_runtime or ariva_migration.
--
-- The password never reaches the server: the migration job sends a SCRAM-SHA-256 verifier it computed itself (random 16-byte
-- salt, at least 4096 iterations, RFC 5802 and RFC 7677), which PostgreSQL stores as given; this function refuses anything
-- else, so a plain password can never be stored or logged through it. The verifier itself can still appear in a server log
-- (log_statement = 'all', or log_min_duration_statement at 0 or more) or in pg_stat_statements (track = 'all'); it does not
-- sign anyone in, and recovering a random password of 16 characters or more from it is not practical. The length rule for the
-- password is applied by the migration job before it computes the verifier (ValidationReaderSettings). 0001's runtime login
-- is still created from its plain password, as a bind parameter that such a server log would record (a known residual,
-- docs/security/cwe-controls.md, CWE-269).
--
-- Refused, so the migration job fails and changes nothing:
--   a name that is not a plain lower-case identifier, starts with pg_, is one of Ariva's roles, or is the login running the
--   migration; anything but a SCRAM-SHA-256 verifier of at least 4096 iterations;
--   an existing role that is a superuser or has CREATEROLE, CREATEDB, BYPASSRLS or REPLICATION, that is a member of any role
--   but ariva_validation_reader, that is itself granted to a role other than the migration login (PostgreSQL 16 and later
--   make the role that creates a login its administrator), that owns anything or holds any privilege recorded for it in
--   pg_shdepend, in any database of the cluster or a shared catalog (an object it owns, a table, column, schema, sequence or
--   function grant, a default privilege, a policy, a tablespace) except its privileges on databases, or whose privileges on
--   any database of the cluster are anything but CONNECT without grant option. So an existing role such as the development
--   read-only role (SELECT on every table through default privileges) can never become the reader, and the reader's password
--   opens no other database with rights of its own; one reader shared by several Ariva databases still passes, since in each
--   it holds only CONNECT and the cluster-wide reader role.
-- Identifier and verifier are quoted by format (%I, %L), never concatenated; an error while creating or altering the login is
-- raised again without the statement. search_path is pinned to pg_catalog, so no object of another schema can stand in for a
-- catalog the function reads.
-- The hosts check at start-up that their runtime login can read no value of the shadow nowcast and that the reader login is
-- neither the runtime nor the migration login (ValidationReaderGuard); the migration job checks the first as well.

CREATE OR REPLACE FUNCTION ariva_ensure_validation_reader_login(login_name text, login_verifier text)
RETURNS void
LANGUAGE plpgsql
SET search_path = pg_catalog, pg_temp
AS $function$
DECLARE
    login_oid oid;
    found text;
BEGIN
    IF login_name IS NULL OR login_name !~ '^[a-z_][a-z0-9_]{0,62}$' OR left(login_name, 3) = 'pg_' THEN
        RAISE EXCEPTION 'invalid validation reader login name';
    END IF;
    IF login_name IN ('ariva_migration', 'ariva_runtime', 'ariva_validation_reader') OR login_name = current_user OR login_name = session_user THEN
        RAISE EXCEPTION 'the validation reader login must be a login of its own, not % (CWE-269)', login_name;
    END IF;
    IF login_verifier IS NULL OR login_verifier !~ '^SCRAM-SHA-256\$[0-9]{4,7}:[A-Za-z0-9+/]{22}==\$[A-Za-z0-9+/]{43}=:[A-Za-z0-9+/]{43}=$' THEN
        RAISE EXCEPTION 'the validation reader login takes a SCRAM-SHA-256 verifier of at least 4096 iterations, never a password';
    END IF;
    -- Checked apart, after the pattern, so the cast only ever sees digits.
    IF split_part(split_part(login_verifier, '$', 2), ':', 1)::integer < 4096 THEN
        RAISE EXCEPTION 'the validation reader login takes a SCRAM-SHA-256 verifier of at least 4096 iterations, never a password';
    END IF;

    SELECT oid INTO login_oid FROM pg_roles WHERE rolname = login_name;
    IF login_oid IS NOT NULL THEN
        IF EXISTS (SELECT FROM pg_roles WHERE oid = login_oid AND (rolsuper OR rolcreaterole OR rolcreatedb OR rolbypassrls OR rolreplication)) THEN
            RAISE EXCEPTION 'validation reader login % is privileged; use a dedicated login', login_name;
        END IF;
        SELECT string_agg(g.rolname, ', ' ORDER BY g.rolname) INTO found
          FROM pg_auth_members m JOIN pg_roles g ON g.oid = m.roleid
         WHERE m.member = login_oid AND g.rolname <> 'ariva_validation_reader';
        IF found IS NOT NULL THEN
            RAISE EXCEPTION 'validation reader login % is a member of %; revoke it first (CWE-269)', login_name, found;
        END IF;
        SELECT string_agg(r.rolname, ', ' ORDER BY r.rolname) INTO found
          FROM pg_auth_members m JOIN pg_roles r ON r.oid = m.member
         WHERE m.roleid = login_oid AND r.rolname <> current_user;
        IF found IS NOT NULL THEN
            RAISE EXCEPTION 'validation reader login % is granted to %; revoke it first (CWE-269)', login_name, found;
        END IF;
        -- Every database of the cluster, not only this one: the password would open them too.
        IF EXISTS (SELECT FROM pg_shdepend d
                    WHERE d.refclassid = 'pg_authid'::regclass AND d.refobjid = login_oid
                      AND NOT (d.classid = 'pg_database'::regclass AND d.deptype = 'a')) THEN
            RAISE EXCEPTION 'validation reader login % owns objects or holds privileges; use a dedicated login (CWE-269)', login_name;
        END IF;
        IF EXISTS (SELECT FROM pg_database db, aclexplode(db.datacl) a
                    WHERE a.grantee = login_oid AND (a.privilege_type <> 'CONNECT' OR a.is_grantable)) THEN
            RAISE EXCEPTION 'validation reader login % holds privileges on a database beyond CONNECT; use a dedicated login (CWE-269)', login_name;
        END IF;
    END IF;

    BEGIN
        IF login_oid IS NOT NULL THEN
            EXECUTE format('ALTER ROLE %I WITH LOGIN INHERIT PASSWORD %L', login_name, login_verifier);
        ELSE
            EXECUTE format('CREATE ROLE %I WITH LOGIN INHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD %L', login_name, login_verifier);
        END IF;
    EXCEPTION WHEN OTHERS THEN
        RAISE EXCEPTION 'could not create or update validation reader login %: %', login_name, SQLERRM USING ERRCODE = SQLSTATE;
    END;
    EXECUTE format('GRANT ariva_validation_reader TO %I', login_name);
    EXECUTE format('GRANT CONNECT ON DATABASE %I TO %I', current_database(), login_name);
END
$function$;

REVOKE ALL ON FUNCTION ariva_ensure_validation_reader_login(text, text) FROM PUBLIC;
