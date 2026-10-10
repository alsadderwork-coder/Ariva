-- 0043 The shadow nowcast in its own table (ARV-117a, owner decision 2026-10-07; formulas F8). Script 0041 stored the
-- shadow nowcast without AMAN inputs as three columns of queue_minute, which the runtime role reads for reports, alert
-- inputs and screens, so the rule "written by the stream, read by nothing but the validation comparison" rested on
-- source scans. It now has a table of its own that the runtime role can write but not read: the database enforces it.
--
--   queue_minute_shadow   one row per queue zone and minute that has a shadow nowcast, keyed like queue_minute and
--                         written by Ariva.Api.Stream in the same checkpoint transaction as the published row:
--     nowcast_minutes     the shadow W_now in minutes, null when there is no service
--     no_service          why there is none (NoServiceReason names), null when there is a number
--     nowcast_degraded    the shadow's F11 flag (true when it rests on the exit term alone or an Unknown desk)
--
-- Exactly one of a number and a reason, and always the flag: a minute without a shadow has no row. The rows 0041 wrote
-- are copied here, then its columns and their checks are dropped (0041 itself is history and stays as merged).
--
-- Privileges (CWE-862, CWE-863; verified on PostgreSQL 17 with TimescaleDB 2.30, QueueStreamTests): the runtime role
-- has INSERT, UPDATE of the value columns and SELECT of the two key columns only, and no DELETE or TRUNCATE. The stream's
-- upsert needs SELECT on the conflict target's columns (zone_key, minute_utc); a reference to EXCLUDED.<column> in
-- ON CONFLICT DO UPDATE needs SELECT on that column of the table, so the stream's upsert takes the new values from its
-- staging table by key instead (a correlated subquery over the key columns) and never reads a value column. Any read
-- of a value column by the runtime role (SELECT, RETURNING, a WHERE or SET over a value) fails with 42501. Chunks
-- copy the hypertable's table-level privileges, so a chunk read directly fails too.
-- Residual (accepted, docs/product/decisions.md): the runtime role can learn one bit per minute, whether the shadow holds
-- a number or a reason, by a deliberate probe: an UPDATE by key that sets only no_service (or only nowcast_minutes)
-- trips ck_queue_minute_shadow_one (23514) exactly when the other one is held, and the prober rolls back. The number,
-- the reason and the flag are not revealed (the error's row detail shows only the key columns and the columns the
-- statement itself set), the shadow can be recomputed from inputs the runtime role reads anyway, and the purpose of the
-- control is that the shadow never reaches what Ariva publishes.
-- Watch item: never enable TimescaleDB chunk skipping (enable_chunk_skipping) on a value column of this table; its
-- per-chunk min and max ranges would be readable through the catalog and the planner, a read path around the grants.
--
--   ariva_validation_reader   NOLOGIN role that may read the shadow (SELECT on queue_minute_shadow), for the
--                             validation comparison (ARV-104f), which grants it to its own login. No host login has it.
--                             An existing role with a wider attribute is corrected below, one attribute at a time (a
--                             migration login without SUPERUSER may not name SUPERUSER or BYPASSRLS at all, so they are
--                             named only when set, and then only a superuser can correct them: the script fails closed);
--                             a reader that is a member of another role fails the script.
--
-- Retention as queue_minute (evidence, no retention policy). Aggregates only: no desk code, officer, traveller or
-- document identity. TimescaleDB is required, as in 0018.
CREATE TABLE queue_minute_shadow (
    zone_key          varchar(220)      NOT NULL,
    minute_utc        timestamptz       NOT NULL,
    nowcast_minutes   double precision  CHECK (nowcast_minutes >= 0),
    no_service        varchar(20)       CHECK (no_service IN ('NothingOpen', 'ThroughputTooLow', 'NoThroughputData', 'NoQueueLength', 'Implausible')),
    nowcast_degraded  boolean           NOT NULL,
    updated_on        timestamptz       NOT NULL,
    PRIMARY KEY (zone_key, minute_utc),
    -- Nowcast.Compute returns a number or a reason on every path, never both and never neither.
    CONSTRAINT ck_queue_minute_shadow_one CHECK ((nowcast_minutes IS NULL) <> (no_service IS NULL))
);

-- Before the hypertable and the copy, so that every chunk takes these privileges (default privileges in 0001 would
-- otherwise give the runtime role SELECT and DELETE).
REVOKE ALL ON queue_minute_shadow FROM ariva_runtime;
GRANT INSERT ON queue_minute_shadow TO ariva_runtime;
GRANT UPDATE (nowcast_minutes, no_service, nowcast_degraded, updated_on) ON queue_minute_shadow TO ariva_runtime;
GRANT SELECT (zone_key, minute_utc) ON queue_minute_shadow TO ariva_runtime;

DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ariva_validation_reader') THEN
        CREATE ROLE ariva_validation_reader NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOREPLICATION;
    END IF;
END
$$;

-- Equivalent to ALTER ROLE ariva_validation_reader NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOREPLICATION
-- for a role created before with other attributes, without needing SUPERUSER when nothing is to be corrected.
DO $$
DECLARE
    r record;
BEGIN
    SELECT rolcanlogin, rolsuper, rolcreatedb, rolcreaterole, rolbypassrls, rolreplication INTO r
      FROM pg_roles WHERE rolname = 'ariva_validation_reader';
    IF r.rolsuper THEN
        ALTER ROLE ariva_validation_reader NOSUPERUSER;
    END IF;
    IF r.rolbypassrls THEN
        ALTER ROLE ariva_validation_reader NOBYPASSRLS;
    END IF;
    IF r.rolreplication THEN
        ALTER ROLE ariva_validation_reader NOREPLICATION;
    END IF;
    IF r.rolcanlogin THEN
        ALTER ROLE ariva_validation_reader NOLOGIN;
    END IF;
    IF r.rolcreatedb THEN
        ALTER ROLE ariva_validation_reader NOCREATEDB;
    END IF;
    IF r.rolcreaterole THEN
        ALTER ROLE ariva_validation_reader NOCREATEROLE;
    END IF;
    IF EXISTS (SELECT FROM pg_auth_members m JOIN pg_roles g ON g.oid = m.roleid
                WHERE m.member = (SELECT oid FROM pg_roles WHERE rolname = 'ariva_validation_reader')) THEN
        RAISE EXCEPTION 'ariva_validation_reader is a member of another role; revoke it before this script (CWE-269)';
    END IF;
END
$$;

GRANT USAGE ON SCHEMA public TO ariva_validation_reader;
GRANT SELECT ON queue_minute_shadow TO ariva_validation_reader;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'timescaledb') THEN
        IF coalesce(current_setting('ariva.allow_plain_postgres', true), '') <> 'on' THEN
            RAISE EXCEPTION 'TimescaleDB is required: queue_minute_shadow is a hypertable like queue_minute';
        END IF;
        RAISE NOTICE 'TimescaleDB is not available (ariva.allow_plain_postgres): queue_minute_shadow stays an ordinary table';
        RETURN;
    END IF;
    CREATE EXTENSION IF NOT EXISTS timescaledb;
    PERFORM create_hypertable('queue_minute_shadow', 'minute_utc', chunk_time_interval => INTERVAL '7 days');
END
$$;

-- The shadows 0041 stored (its checks guarantee exactly one of a number and a reason, with the flag).
INSERT INTO queue_minute_shadow (zone_key, minute_utc, nowcast_minutes, no_service, nowcast_degraded, updated_on)
SELECT zone_key, minute_utc, shadow_nowcast_minutes, shadow_no_service, shadow_nowcast_degraded, updated_on
  FROM queue_minute
 WHERE shadow_nowcast_degraded IS NOT NULL;

ALTER TABLE queue_minute DROP CONSTRAINT ck_queue_minute_shadow_flag;
ALTER TABLE queue_minute DROP CONSTRAINT ck_queue_minute_shadow;
ALTER TABLE queue_minute DROP COLUMN shadow_nowcast_minutes;
ALTER TABLE queue_minute DROP COLUMN shadow_no_service;
ALTER TABLE queue_minute DROP COLUMN shadow_nowcast_degraded;
