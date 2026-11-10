-- 0074 One database login per host, each with the grants its code needs (ARV-083a; CWE-269, CWE-285; ASVS 5.0 V13.2.1,
-- V13.2.2, V13.3.2).
--
-- Until this script every host connected with one runtime login, a member of ariva_runtime, which held SELECT, INSERT,
-- UPDATE and DELETE on nearly every table (script 0001's default privileges, narrowed table by table since). A compromised
-- host could write what only another host writes: Ingest the configuration, Stream the accounts, any host the availability
-- ledger's minutes and cursors (ARV-118, the ARV-150 review). From here each host connects with a login of its own, which the
-- migration job creates or updates through ariva_ensure_host_login below, a member of two roles and nothing else:
--
--   ariva_runtime        the base every host needs: the schema check (schema_version), the vm-local environment guard
--                        (database_environment), the session, permission and site reads of every authenticated request
--                        (user_role, user_session, user_site, and every column of "user" but its credentials: the password
--                        hash, salt, algorithm and iterations and the protected TOTP seed, which only ariva_main reads, and
--                        ariva_cronz the seed it re-protects), the Data Protection key ring (data_protection_key, granted
--                        by table, so the row signature of script 0073 is read and written with each row), the outbox and
--                        its relay (outbox_message) and the relay's purge of the inbox (processed_event). The functions of
--                        scripts 0063, 0069 and 0070 set their limits on a member of this role.
--   one host role        what that host's code reads and writes beyond the base, derived from the services each host registers
--                        (Ariva.Infra's HostDatabaseAccess, which an integration test holds this database to exactly):
--                        ariva_main, ariva_ingest, ariva_stream, ariva_cronz and ariva_integration, and ariva_availability for
--                        the availability ledger, which Ariva.Api.Cronz runs under a login of its own (Database:Availability).
--                        ariva_availability alone may record availability minutes or move a site's cursor.
--
-- The host roles are NOLOGIN, hold no role and are held by one login each, so no host reaches another's grants. Nothing
-- here grants a sequence: outbox_message's identity column needs none, and no code calls nextval. No role is given the
-- continuous aggregates of scripts 0018, 0037 and 0039, which no code reads. Temporary tables (the COPY writers' staging
-- tables) come from PUBLIC's TEMP on the database, as before.
--
-- Script 0001's default privileges go: a table or sequence a later script creates gives no host anything until that script
-- grants it to the host that reads or writes it (and HostDatabaseAccess says so). Every existing grant to ariva_runtime is
-- revoked first, its column grants with its table grants, so this script leaves ariva_runtime exactly the base whatever
-- an earlier script or a hand left it; TimescaleDB carries each hypertable's grants and revokes to its chunks.
--
-- ariva_ensure_runtime_login (script 0001) took the runtime login's password itself, as a bind parameter that a server log
-- records (log_statement = 'all', or a duration threshold) and in a statement a failing EXECUTE prints (the ARV-091 log
-- inventory). It is removed: ariva_ensure_host_login takes a SCRAM-SHA-256 verifier the migration job computed, as the
-- validation reader's function does (script 0049), and refuses anything else. Script 0001 itself is unchanged, so no
-- applied checksum moves. ariva_ensure_development_login gives one login every host role, for a vm-local database (or one
-- that records no environment: the integration tests) whose hosts share a login; any other database refuses it.

DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ariva_main') THEN
        CREATE ROLE ariva_main NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOREPLICATION;
    END IF;
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ariva_ingest') THEN
        CREATE ROLE ariva_ingest NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOREPLICATION;
    END IF;
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ariva_stream') THEN
        CREATE ROLE ariva_stream NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOREPLICATION;
    END IF;
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ariva_cronz') THEN
        CREATE ROLE ariva_cronz NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOREPLICATION;
    END IF;
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ariva_integration') THEN
        CREATE ROLE ariva_integration NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOREPLICATION;
    END IF;
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ariva_availability') THEN
        CREATE ROLE ariva_availability NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOREPLICATION;
    END IF;
END
$$;

DO $$
DECLARE
    host_role text;
    r record;
BEGIN
    FOREACH host_role IN ARRAY ARRAY['ariva_main', 'ariva_ingest', 'ariva_stream', 'ariva_cronz', 'ariva_integration', 'ariva_availability'] LOOP
        -- A role created before with a wider attribute is corrected one attribute at a time, as script 0043 does: a migration
        -- login without SUPERUSER may not name SUPERUSER or BYPASSRLS, so they are named only when set (the script then fails
        -- closed unless a superuser runs it).
        SELECT rolcanlogin, rolsuper, rolcreatedb, rolcreaterole, rolbypassrls, rolreplication INTO r FROM pg_roles WHERE rolname = host_role;
        IF r.rolsuper THEN
            EXECUTE format('ALTER ROLE %I NOSUPERUSER', host_role);
        END IF;
        IF r.rolbypassrls THEN
            EXECUTE format('ALTER ROLE %I NOBYPASSRLS', host_role);
        END IF;
        IF r.rolreplication THEN
            EXECUTE format('ALTER ROLE %I NOREPLICATION', host_role);
        END IF;
        IF r.rolcanlogin THEN
            EXECUTE format('ALTER ROLE %I NOLOGIN', host_role);
        END IF;
        IF r.rolcreatedb THEN
            EXECUTE format('ALTER ROLE %I NOCREATEDB', host_role);
        END IF;
        IF r.rolcreaterole THEN
            EXECUTE format('ALTER ROLE %I NOCREATEROLE', host_role);
        END IF;
        IF EXISTS (SELECT FROM pg_auth_members m WHERE m.member = (SELECT oid FROM pg_roles WHERE rolname = host_role)) THEN
            RAISE EXCEPTION '% is a member of another role; revoke it before this script (CWE-269)', host_role;
        END IF;
    END LOOP;
END
$$;

-- The base: everything else ariva_runtime held goes, table, column and function grants alike (the two purges of scripts
-- 0054 and 0072 go to ariva_cronz below, the role of the Cronz jobs that call them), and so do the default privileges script
-- 0001 gave it.
REVOKE ALL ON ALL TABLES IN SCHEMA public FROM ariva_runtime;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM ariva_runtime;
REVOKE ALL ON FUNCTION ariva_retention_purge(text, integer) FROM ariva_runtime;
REVOKE ALL ON FUNCTION ariva_data_protection_key_purge(integer, integer) FROM ariva_runtime;
ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE ALL ON TABLES FROM ariva_runtime;
ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE ALL ON SEQUENCES FROM ariva_runtime;

GRANT SELECT, INSERT ON data_protection_key TO ariva_runtime;
GRANT SELECT ON database_environment TO ariva_runtime;
GRANT SELECT, INSERT, UPDATE, DELETE ON outbox_message TO ariva_runtime;
GRANT SELECT, DELETE ON processed_event TO ariva_runtime;
GRANT SELECT ON schema_version TO ariva_runtime;
GRANT SELECT (all_sites, created_by, created_by_id, created_on, display_name, email, failed_login_count, id, is_break_glass, is_disabled, last_login_on, locked_until, modified_by, modified_by_id, modified_on, must_change_password, totp_enrolled, totp_last_step, user_name) ON "user" TO ariva_runtime;
GRANT SELECT ON user_role TO ariva_runtime;
GRANT SELECT ON user_session TO ariva_runtime;
GRANT SELECT ON user_site TO ariva_runtime;

GRANT USAGE ON SCHEMA public TO ariva_main, ariva_ingest, ariva_stream, ariva_cronz, ariva_integration, ariva_availability;

-- Ariva.Api.Main: the administration and operations API, the live hub, the device health consumer and sweep, the display
-- staleness sweep, the validation results (the shadow nowcast is read through the validation reader's login, never this one),
-- the flight schedule uploads, the demo seeds and the stored name check.
GRANT SELECT, INSERT, UPDATE ON advertising_media TO ariva_main;
GRANT SELECT, INSERT ON advertising_play TO ariva_main;
GRANT SELECT, INSERT, UPDATE ON advertising_playlist TO ariva_main;
GRANT SELECT, INSERT, UPDATE ON advertising_policy TO ariva_main;
GRANT SELECT, INSERT, UPDATE ON advertising_schedule TO ariva_main;
GRANT SELECT, INSERT, UPDATE ON airport TO ariva_main;
GRANT SELECT, UPDATE ON alert TO ariva_main;
GRANT SELECT, INSERT, UPDATE ON alert_rule TO ariva_main;
GRANT SELECT, INSERT ON audit_entry TO ariva_main;
GRANT SELECT ON availability_minute TO ariva_main;
GRANT SELECT ON border_desk_interval TO ariva_main;
GRANT SELECT ON border_desk_session TO ariva_main;
GRANT SELECT ON border_egate_interval TO ariva_main;
GRANT SELECT, INSERT, UPDATE ON checkpoint TO ariva_main;
GRANT SELECT ON counter_allocation TO ariva_main;
GRANT SELECT, INSERT, UPDATE ON desk TO ariva_main;
GRANT SELECT, INSERT, UPDATE ON desk_code_mapping TO ariva_main;
GRANT SELECT ON desk_minute TO ariva_main;
GRANT SELECT, INSERT ON desk_observation TO ariva_main;
GRANT SELECT, INSERT ON desk_observation_batch TO ariva_main;
GRANT SELECT, INSERT, UPDATE ON device TO ariva_main;
GRANT SELECT, INSERT ON device_calibration TO ariva_main;
GRANT SELECT, INSERT, UPDATE ON device_heartbeat TO ariva_main;
GRANT SELECT, INSERT, UPDATE ON display TO ariva_main;
GRANT SELECT, INSERT ON email_message TO ariva_main;
GRANT SELECT, INSERT, UPDATE ON feed_freshness TO ariva_main;
GRANT SELECT, INSERT, UPDATE ON flight_leg TO ariva_main;
GRANT SELECT, INSERT, UPDATE ON floor_plan TO ariva_main;
GRANT SELECT ON inbound_lane_demand TO ariva_main;
GRANT SELECT, INSERT, UPDATE ON instruction_slide TO ariva_main;
GRANT SELECT, INSERT, UPDATE ON integration_client TO ariva_main;
GRANT SELECT ON lane_staffing TO ariva_main;
GRANT SELECT, INSERT, UPDATE ON level TO ariva_main;
GRANT SELECT, INSERT, UPDATE, DELETE ON line TO ariva_main;
GRANT SELECT ON line_minute TO ariva_main;
GRANT SELECT, INSERT, UPDATE ON maintenance_window TO ariva_main;
GRANT SELECT, INSERT ON manual_count TO ariva_main;
GRANT SELECT, INSERT, UPDATE ON operating_day_exception TO ariva_main;
GRANT SELECT, INSERT, UPDATE ON operating_week TO ariva_main;
GRANT SELECT, INSERT, UPDATE ON outbound_endpoint TO ariva_main;
GRANT SELECT ON overflow_minute TO ariva_main;
GRANT INSERT ON processed_event TO ariva_main;
GRANT SELECT ON queue_bin TO ariva_main;
GRANT SELECT ON queue_minute TO ariva_main;
GRANT SELECT, INSERT, UPDATE ON recovery_code TO ariva_main;
GRANT SELECT, INSERT, UPDATE ON refresh_token TO ariva_main;
GRANT SELECT ON report_delivery TO ariva_main;
GRANT SELECT, INSERT, UPDATE ON report_schedule TO ariva_main;
GRANT SELECT, INSERT, DELETE ON report_schedule_recipient TO ariva_main;
GRANT SELECT, INSERT ON site TO ariva_main;
GRANT UPDATE (modified_by, modified_by_id, modified_on, name) ON site TO ariva_main;
GRANT SELECT, INSERT, UPDATE ON terminal TO ariva_main;
GRANT SELECT, INSERT ON tracer_batch TO ariva_main;
GRANT SELECT, INSERT ON tracer_run TO ariva_main;
GRANT SELECT, INSERT, UPDATE ON "user" TO ariva_main;
GRANT INSERT, DELETE ON user_role TO ariva_main;
GRANT INSERT, UPDATE ON user_session TO ariva_main;
GRANT INSERT, DELETE ON user_site TO ariva_main;
GRANT SELECT, INSERT ON validation_campaign TO ariva_main;
GRANT UPDATE (closed_by_id, closed_utc, started_by_id, started_utc, status) ON validation_campaign TO ariva_main;
GRANT SELECT, INSERT ON validation_campaign_desk TO ariva_main;
GRANT SELECT, INSERT ON validation_campaign_line TO ariva_main;
GRANT SELECT, INSERT ON validation_campaign_zone TO ariva_main;
GRANT SELECT, INSERT ON validation_result_revision TO ariva_main;
GRANT SELECT, INSERT, UPDATE, DELETE ON zone TO ariva_main;
GRANT SELECT, INSERT, UPDATE ON zone_health TO ariva_main;
GRANT SELECT ON zone_health_bin TO ariva_main;
GRANT SELECT ON zone_outage TO ariva_main;
GRANT SELECT, INSERT, UPDATE, DELETE ON zone_profile TO ariva_main;

-- Ariva.Api.Ingest: a device's credential and its published zone (SvcDeviceGateway; a device loads its level, terminal and
-- airport with it). It writes nothing but the base.
GRANT SELECT ON airport TO ariva_ingest;
GRANT SELECT ON device TO ariva_ingest;
GRANT SELECT ON level TO ariva_ingest;
GRANT SELECT ON line TO ariva_ingest;
GRANT SELECT ON terminal TO ariva_ingest;
GRANT SELECT ON zone TO ariva_ingest;
GRANT SELECT ON zone_profile TO ariva_ingest;

-- Ariva.Api.Stream: the queue engine's outputs and checkpoints (StreamStore), the sensing and device health archives, the
-- desk feed, the alert evaluation with its emails and audit entries, and replays. It never writes an account.
GRANT SELECT, INSERT, UPDATE ON alert TO ariva_stream;
GRANT SELECT ON alert_rule TO ariva_stream;
GRANT SELECT, INSERT, UPDATE, DELETE ON alert_rule_state TO ariva_stream;
GRANT INSERT ON audit_entry TO ariva_stream;
GRANT SELECT ON border_desk_interval TO ariva_stream;
GRANT SELECT ON border_desk_session TO ariva_stream;
GRANT SELECT ON border_egate_interval TO ariva_stream;
GRANT SELECT ON checkpoint TO ariva_stream;
GRANT SELECT ON desk TO ariva_stream;
GRANT SELECT ON desk_code_mapping TO ariva_stream;
GRANT SELECT, INSERT, UPDATE ON desk_feed_state TO ariva_stream;
GRANT SELECT, INSERT, UPDATE ON desk_minute TO ariva_stream;
GRANT SELECT, INSERT, UPDATE ON desk_sensor_minute TO ariva_stream;
GRANT SELECT, INSERT ON desk_zone_reading TO ariva_stream;
GRANT SELECT ON device TO ariva_stream;
GRANT SELECT, INSERT ON device_health_event TO ariva_stream;
GRANT SELECT ON device_heartbeat TO ariva_stream;
GRANT SELECT, INSERT, UPDATE ON egate_minute TO ariva_stream;
GRANT SELECT, INSERT ON email_message TO ariva_stream;
GRANT SELECT ON flight_leg TO ariva_stream;
GRANT SELECT ON inbound_lane_demand TO ariva_stream;
GRANT SELECT ON lane_staffing TO ariva_stream;
GRANT SELECT ON line TO ariva_stream;
GRANT SELECT, INSERT, UPDATE ON line_minute TO ariva_stream;
GRANT SELECT, INSERT, UPDATE ON overflow_minute TO ariva_stream;
GRANT INSERT ON processed_event TO ariva_stream;
GRANT SELECT, INSERT, UPDATE ON queue_bin TO ariva_stream;
GRANT SELECT, INSERT, UPDATE ON queue_minute TO ariva_stream;
GRANT INSERT ON queue_minute_shadow TO ariva_stream;
GRANT SELECT (minute_utc, zone_key) ON queue_minute_shadow TO ariva_stream;
GRANT UPDATE (no_service, nowcast_degraded, nowcast_minutes, sensor_cycle_minutes, updated_on) ON queue_minute_shadow TO ariva_stream;
GRANT SELECT, INSERT ON replay_run TO ariva_stream;
GRANT SELECT, INSERT ON sensing_batch TO ariva_stream;
GRANT SELECT, INSERT, DELETE ON sensing_day_key TO ariva_stream;
GRANT SELECT, INSERT ON sensing_event TO ariva_stream;
GRANT SELECT, INSERT, UPDATE ON stream_offset TO ariva_stream;
GRANT SELECT, INSERT ON stream_partition_count TO ariva_stream;
GRANT SELECT, INSERT, UPDATE ON stream_zone_state TO ariva_stream;
GRANT SELECT ON zone TO ariva_stream;
GRANT SELECT, INSERT, UPDATE ON zone_health_bin TO ariva_stream;
GRANT SELECT, INSERT ON zone_outage TO ariva_stream;
GRANT UPDATE (closed, recorded_on, to_utc) ON zone_outage TO ariva_stream;
GRANT SELECT ON zone_profile TO ariva_stream;

-- Ariva.Api.Cronz: the report deliveries, the staffing recommendation, the retention purge (script 0054's function), the
-- re-protection of stored secrets (one column of each of three tables), the key ring upgrade check at start (those three
-- columns), and the key lifecycle job: the key ring purge (script 0072's function) and the dates of the credentials in use
-- (an integration client's secret and seed, a device's and a display's credential, an outbound endpoint's secret: the job
-- reads the dates, the status and whether a credential is set, never a secret; a column grant cannot tell "is set" from the
-- value, so a display's credential hash is readable here, as a device's already was for the reports). The availability
-- ledger runs under its own login.
GRANT SELECT ON airport TO ariva_cronz;
GRANT SELECT ON alert TO ariva_cronz;
GRANT SELECT ON border_egate_interval TO ariva_cronz;
GRANT SELECT ON checkpoint TO ariva_cronz;
GRANT SELECT ON desk TO ariva_cronz;
GRANT SELECT ON desk_minute TO ariva_cronz;
GRANT SELECT ON device TO ariva_cronz;
GRANT SELECT ON device_heartbeat TO ariva_cronz;
GRANT SELECT (credential_hash, credential_issued_on, deleted_on) ON display TO ariva_cronz;
GRANT SELECT ON flight_leg TO ariva_cronz;
GRANT SELECT ON inbound_lane_demand TO ariva_cronz;
GRANT SELECT (id, secret_changed_utc, status, totp_changed_utc, totp_secret_protected) ON integration_client TO ariva_cronz;
GRANT UPDATE (totp_secret_protected) ON integration_client TO ariva_cronz;
GRANT SELECT, INSERT ON lane_staffing TO ariva_cronz;
GRANT SELECT (id, secret_changed_utc, secret_protected, status) ON outbound_endpoint TO ariva_cronz;
GRANT UPDATE (secret_protected) ON outbound_endpoint TO ariva_cronz;
GRANT SELECT ON queue_minute TO ariva_cronz;
GRANT SELECT, INSERT ON report_delivery TO ariva_cronz;
GRANT UPDATE (attempts, reason, sent_utc, status) ON report_delivery TO ariva_cronz;
GRANT SELECT ON report_schedule TO ariva_cronz;
GRANT SELECT ON report_schedule_recipient TO ariva_cronz;
GRANT SELECT ON retention_period TO ariva_cronz;
GRANT SELECT ON site TO ariva_cronz;
GRANT SELECT ON terminal TO ariva_cronz;
GRANT SELECT (totp_secret_protected) ON "user" TO ariva_cronz;
GRANT UPDATE (totp_secret_protected) ON "user" TO ariva_cronz;
GRANT SELECT ON zone TO ariva_cronz;
GRANT SELECT ON zone_outage TO ariva_cronz;
GRANT SELECT ON zone_profile TO ariva_cronz;
GRANT EXECUTE ON FUNCTION ariva_retention_purge(text, integer) TO ariva_cronz;
GRANT EXECUTE ON FUNCTION ariva_data_protection_key_purge(integer, integer) TO ariva_cronz;

-- Ariva.Api.Integration: the AODB and immigration intake, the AMAN feed consumers and pull, the ACRIS pull, the Integration
-- API's client sign-in (its attempt, lockout and replay columns only), call record and idempotency keys, and the alert email
-- sender (an email's delivery columns only).
GRANT SELECT ON airport TO ariva_integration;
GRANT SELECT ON alert TO ariva_integration;
GRANT SELECT, INSERT, UPDATE ON aman_pull_cursor TO ariva_integration;
GRANT INSERT ON audit_entry TO ariva_integration;
GRANT SELECT, INSERT ON border_desk_interval TO ariva_integration;
GRANT SELECT, INSERT ON border_desk_session TO ariva_integration;
GRANT SELECT, INSERT ON border_egate_interval TO ariva_integration;
GRANT SELECT ON checkpoint TO ariva_integration;
GRANT SELECT, INSERT, UPDATE ON counter_allocation TO ariva_integration;
GRANT SELECT ON desk TO ariva_integration;
GRANT SELECT ON desk_code_mapping TO ariva_integration;
GRANT SELECT ON email_message TO ariva_integration;
GRANT UPDATE (attempts, next_attempt_utc, reason, sent_utc, status) ON email_message TO ariva_integration;
GRANT SELECT, INSERT, UPDATE ON feed_freshness TO ariva_integration;
GRANT INSERT ON flight_event TO ariva_integration;
GRANT SELECT, INSERT, UPDATE ON flight_leg TO ariva_integration;
GRANT SELECT, INSERT, UPDATE ON inbound_lane_demand TO ariva_integration;
GRANT INSERT ON integration_call TO ariva_integration;
GRANT SELECT ON integration_client TO ariva_integration;
GRANT UPDATE (attempt_window_utc, attempts_in_window, failed_attempts, last_token_utc, locked_until_utc, totp_last_step) ON integration_client TO ariva_integration;
GRANT SELECT, INSERT, DELETE ON integration_idempotency TO ariva_integration;
GRANT UPDATE (response_body, status_code) ON integration_idempotency TO ariva_integration;
GRANT SELECT ON outbound_endpoint TO ariva_integration;
GRANT UPDATE (consecutive_failures, last_modified, last_poll_utc, last_status) ON outbound_endpoint TO ariva_integration;
GRANT INSERT ON processed_event TO ariva_integration;
GRANT SELECT ON site TO ariva_integration;
GRANT SELECT ON terminal TO ariva_integration;

-- The availability ledger (Ariva.Api.Cronz's AvailabilityLedger, ARV-118): what it reads to decide a site's minutes, and the
-- only INSERT on availability_minute and INSERT and UPDATE on availability_cursor (owner decision 2026-10-10).
GRANT SELECT ON airport TO ariva_availability;
GRANT SELECT, INSERT, UPDATE ON availability_cursor TO ariva_availability;
GRANT SELECT, INSERT ON availability_minute TO ariva_availability;
GRANT SELECT ON maintenance_window TO ariva_availability;
GRANT SELECT ON operating_day_exception TO ariva_availability;
GRANT SELECT ON operating_week TO ariva_availability;
GRANT SELECT ON queue_minute TO ariva_availability;
GRANT SELECT ON terminal TO ariva_availability;
GRANT SELECT ON zone TO ariva_availability;
GRANT SELECT ON zone_profile TO ariva_availability;

-- Creates or updates a host's login from a SCRAM-SHA-256 verifier the migration job computed (random 16-byte salt, at least
-- 4096 iterations, RFC 5802 and RFC 7677), which PostgreSQL stores as given, so the password never reaches the server; anything
-- else is refused, so a plain password can never be stored or logged through it. The login gets ariva_runtime, the one host
-- role named and CONNECT on this database. The migration job then sets the runtime limits on it (scripts 0063, 0069 and 0070),
-- since a setting of a group role does not reach its members.
--
-- Refused, so the migration job fails and changes nothing: a host role that is not one of the six; a name that is not a plain
-- lower-case identifier, starts with pg_, is one of Ariva's roles, or is the login running the migration; anything but a
-- SCRAM-SHA-256 verifier of at least 4096 iterations; an existing role that is a superuser or has CREATEROLE, CREATEDB,
-- BYPASSRLS or REPLICATION, that is a member of any role but ariva_runtime and the role named (another host's role, the
-- migration or the validation reader role, a predefined role), that is granted to a role other than the migration login
-- (PostgreSQL 16 and later make the role that creates a login its administrator), that owns anything or holds any privilege
-- recorded for it in pg_shdepend in any database of the cluster or a shared catalog except its privileges on databases, or
-- whose privileges on any database are anything but CONNECT without grant option. So a host login holds exactly what its
-- two roles hold, which HostDatabaseAccess declares, and one login never serves two hosts.
-- Identifier and verifier are quoted by format (%I, %L), never concatenated; an error while creating or altering the login is
-- raised again without the statement. search_path is pinned to pg_catalog, so no object of another schema can stand in for a
-- catalog the function reads.
CREATE OR REPLACE FUNCTION ariva_ensure_host_login(login_name text, login_verifier text, host_role text)
RETURNS void
LANGUAGE plpgsql
SET search_path = pg_catalog, pg_temp
AS $function$
DECLARE
    host_roles constant text[] := ARRAY['ariva_main', 'ariva_ingest', 'ariva_stream', 'ariva_cronz', 'ariva_integration', 'ariva_availability'];
    login_oid oid;
    found text;
BEGIN
    IF host_role IS NULL OR NOT host_role = ANY (host_roles) THEN
        RAISE EXCEPTION 'invalid host role';
    END IF;
    IF login_name IS NULL OR login_name !~ '^[a-z_][a-z0-9_]{0,62}$' OR left(login_name, 3) = 'pg_' THEN
        RAISE EXCEPTION 'invalid host login name';
    END IF;
    IF login_name = ANY (host_roles || ARRAY['ariva_migration', 'ariva_runtime', 'ariva_validation_reader'])
       OR login_name = current_user OR login_name = session_user THEN
        RAISE EXCEPTION 'a host login must be a login of its own, not % (CWE-269)', login_name;
    END IF;
    IF login_verifier IS NULL OR login_verifier !~ '^SCRAM-SHA-256\$[0-9]{4,7}:[A-Za-z0-9+/]{22}==\$[A-Za-z0-9+/]{43}=:[A-Za-z0-9+/]{43}=$' THEN
        RAISE EXCEPTION 'a host login takes a SCRAM-SHA-256 verifier of at least 4096 iterations, never a password';
    END IF;
    -- Checked apart, after the pattern, so the cast only ever sees digits.
    IF split_part(split_part(login_verifier, '$', 2), ':', 1)::integer < 4096 THEN
        RAISE EXCEPTION 'a host login takes a SCRAM-SHA-256 verifier of at least 4096 iterations, never a password';
    END IF;

    SELECT oid INTO login_oid FROM pg_roles WHERE rolname = login_name;
    IF login_oid IS NOT NULL THEN
        IF EXISTS (SELECT FROM pg_roles WHERE oid = login_oid AND (rolsuper OR rolcreaterole OR rolcreatedb OR rolbypassrls OR rolreplication)) THEN
            RAISE EXCEPTION 'host login % is privileged; use a dedicated login', login_name;
        END IF;
        SELECT string_agg(g.rolname, ', ' ORDER BY g.rolname) INTO found
          FROM pg_auth_members m JOIN pg_roles g ON g.oid = m.roleid
         WHERE m.member = login_oid AND g.rolname <> 'ariva_runtime' AND g.rolname <> host_role;
        IF found IS NOT NULL THEN
            RAISE EXCEPTION 'host login % is a member of %; revoke it first (CWE-269)', login_name, found;
        END IF;
        SELECT string_agg(r.rolname, ', ' ORDER BY r.rolname) INTO found
          FROM pg_auth_members m JOIN pg_roles r ON r.oid = m.member
         WHERE m.roleid = login_oid AND r.rolname <> current_user;
        IF found IS NOT NULL THEN
            RAISE EXCEPTION 'host login % is granted to %; revoke it first (CWE-269)', login_name, found;
        END IF;
        -- Every database of the cluster, not only this one: the password would open them too.
        IF EXISTS (SELECT FROM pg_shdepend d
                    WHERE d.refclassid = 'pg_authid'::regclass AND d.refobjid = login_oid
                      AND NOT (d.classid = 'pg_database'::regclass AND d.deptype = 'a')) THEN
            RAISE EXCEPTION 'host login % owns objects or holds privileges; use a dedicated login (CWE-269)', login_name;
        END IF;
        IF EXISTS (SELECT FROM pg_database db, aclexplode(db.datacl) a
                    WHERE a.grantee = login_oid AND (a.privilege_type <> 'CONNECT' OR a.is_grantable)) THEN
            RAISE EXCEPTION 'host login % holds privileges on a database beyond CONNECT; use a dedicated login (CWE-269)', login_name;
        END IF;
    END IF;

    BEGIN
        IF login_oid IS NOT NULL THEN
            EXECUTE format('ALTER ROLE %I WITH LOGIN INHERIT PASSWORD %L', login_name, login_verifier);
        ELSE
            EXECUTE format('CREATE ROLE %I WITH LOGIN INHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD %L', login_name, login_verifier);
        END IF;
    EXCEPTION WHEN OTHERS THEN
        RAISE EXCEPTION 'could not create or update host login %: %', login_name, SQLERRM USING ERRCODE = SQLSTATE;
    END;
    EXECUTE format('GRANT ariva_runtime TO %I', login_name);
    EXECUTE format('GRANT %I TO %I', host_role, login_name);
    EXECUTE format('GRANT CONNECT ON DATABASE %I TO %I', current_database(), login_name);
END
$function$;

REVOKE ALL ON FUNCTION ariva_ensure_host_login(text, text, text) FROM PUBLIC;

-- The vm-local login every host of a developer machine, the E2E suites and the integration tests share (Database:Username when
-- no Database:HostLogins are configured): ariva_runtime and every host role, from a SCRAM-SHA-256 verifier as above, with the
-- same refusals but membership (it may already hold ariva_runtime and the host roles, nothing else). Refused, before any
-- change, on a database that records an environment other than vm-local (script 0064): a cluster's database never gets a
-- login with every host's grants. The migration job refuses to call it outside vm-local and processes without an environment.
CREATE OR REPLACE FUNCTION ariva_ensure_development_login(login_name text, login_verifier text)
RETURNS void
LANGUAGE plpgsql
SET search_path = pg_catalog, pg_temp
AS $function$
DECLARE
    host_roles constant text[] := ARRAY['ariva_main', 'ariva_ingest', 'ariva_stream', 'ariva_cronz', 'ariva_integration', 'ariva_availability'];
    host_role text;
    recorded text;
    login_oid oid;
    found text;
BEGIN
    IF to_regclass('public.database_environment') IS NOT NULL THEN
        SELECT environment INTO recorded FROM public.database_environment WHERE id = 1;
    END IF;
    IF recorded IS NOT NULL AND recorded <> 'vm-local' THEN
        RAISE EXCEPTION 'this database records the environment %: only a vm-local database has a development login with every host''s grants (CWE-269)', recorded;
    END IF;
    IF login_name IS NULL OR login_name !~ '^[a-z_][a-z0-9_]{0,62}$' OR left(login_name, 3) = 'pg_' THEN
        RAISE EXCEPTION 'invalid development login name';
    END IF;
    IF login_name = ANY (host_roles || ARRAY['ariva_migration', 'ariva_runtime', 'ariva_validation_reader'])
       OR login_name = current_user OR login_name = session_user THEN
        RAISE EXCEPTION 'the development login must be a login of its own, not % (CWE-269)', login_name;
    END IF;
    IF login_verifier IS NULL OR login_verifier !~ '^SCRAM-SHA-256\$[0-9]{4,7}:[A-Za-z0-9+/]{22}==\$[A-Za-z0-9+/]{43}=:[A-Za-z0-9+/]{43}=$' THEN
        RAISE EXCEPTION 'the development login takes a SCRAM-SHA-256 verifier of at least 4096 iterations, never a password';
    END IF;
    IF split_part(split_part(login_verifier, '$', 2), ':', 1)::integer < 4096 THEN
        RAISE EXCEPTION 'the development login takes a SCRAM-SHA-256 verifier of at least 4096 iterations, never a password';
    END IF;

    SELECT oid INTO login_oid FROM pg_roles WHERE rolname = login_name;
    IF login_oid IS NOT NULL THEN
        IF EXISTS (SELECT FROM pg_roles WHERE oid = login_oid AND (rolsuper OR rolcreaterole OR rolcreatedb OR rolbypassrls OR rolreplication)) THEN
            RAISE EXCEPTION 'development login % is privileged; use a dedicated login', login_name;
        END IF;
        SELECT string_agg(g.rolname, ', ' ORDER BY g.rolname) INTO found
          FROM pg_auth_members m JOIN pg_roles g ON g.oid = m.roleid
         WHERE m.member = login_oid AND g.rolname <> 'ariva_runtime' AND NOT g.rolname = ANY (host_roles);
        IF found IS NOT NULL THEN
            RAISE EXCEPTION 'development login % is a member of %; revoke it first (CWE-269)', login_name, found;
        END IF;
        SELECT string_agg(r.rolname, ', ' ORDER BY r.rolname) INTO found
          FROM pg_auth_members m JOIN pg_roles r ON r.oid = m.member
         WHERE m.roleid = login_oid AND r.rolname <> current_user;
        IF found IS NOT NULL THEN
            RAISE EXCEPTION 'development login % is granted to %; revoke it first (CWE-269)', login_name, found;
        END IF;
        IF EXISTS (SELECT FROM pg_shdepend d
                    WHERE d.refclassid = 'pg_authid'::regclass AND d.refobjid = login_oid
                      AND NOT (d.classid = 'pg_database'::regclass AND d.deptype = 'a')) THEN
            RAISE EXCEPTION 'development login % owns objects or holds privileges; use a dedicated login (CWE-269)', login_name;
        END IF;
        IF EXISTS (SELECT FROM pg_database db, aclexplode(db.datacl) a
                    WHERE a.grantee = login_oid AND (a.privilege_type <> 'CONNECT' OR a.is_grantable)) THEN
            RAISE EXCEPTION 'development login % holds privileges on a database beyond CONNECT; use a dedicated login (CWE-269)', login_name;
        END IF;
    END IF;

    BEGIN
        IF login_oid IS NOT NULL THEN
            EXECUTE format('ALTER ROLE %I WITH LOGIN INHERIT PASSWORD %L', login_name, login_verifier);
        ELSE
            EXECUTE format('CREATE ROLE %I WITH LOGIN INHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD %L', login_name, login_verifier);
        END IF;
    EXCEPTION WHEN OTHERS THEN
        RAISE EXCEPTION 'could not create or update development login %: %', login_name, SQLERRM USING ERRCODE = SQLSTATE;
    END;
    EXECUTE format('GRANT ariva_runtime TO %I', login_name);
    FOREACH host_role IN ARRAY host_roles LOOP
        EXECUTE format('GRANT %I TO %I', host_role, login_name);
    END LOOP;
    EXECUTE format('GRANT CONNECT ON DATABASE %I TO %I', current_database(), login_name);
END
$function$;

REVOKE ALL ON FUNCTION ariva_ensure_development_login(text, text) FROM PUBLIC;

-- The plain password path of script 0001, no longer called by the migration job.
DROP FUNCTION ariva_ensure_runtime_login(text, text);
