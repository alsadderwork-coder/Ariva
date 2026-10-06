-- 0037 Per-line minute counts (ARV-113). Count accuracy (F18) is judged per line and per 15-minute bin, so the stream
-- keeps, beside queue_minute, the crossings counted on every line of a queue zone (its entry and exit lines, the entry
-- lines of its overflow bands and the count lines of the queue zone and its bands) per minute:
--
--   line_minute       one row per queue zone, line, source and minute: crossings in and out, the line's role in the
--                     zone profile version that counted them. Written by Ariva.Api.Stream in the checkpoint transaction
--                     of queue_minute (binary COPY into a staging table, then an upsert by key), once the engine's
--                     watermark has passed the minute, so a replay after a restart rewrites the same rows (idempotent).
--                     source 'Ariva' is what Ariva's queue engine counted; 'Vendor' is kept apart for crossings a vendor
--                     computed on its own lines, a cross-check that is never added to Ariva's counts.
--   line_minute_15m   the continuous aggregate of line_minute per 15 minutes, the bins F18 compares with manual counts
--                     (one row per profile version, so a bin that spans a profile change shows both).
--
-- Retention as queue_minute: results are evidence, kept with no retention or compression policy (only an operator with
-- the migration login removes rows). TimescaleDB is required, as in 0018.
CREATE TABLE line_minute (
    zone_key         varchar(220)  NOT NULL,
    line_name        varchar(200)  NOT NULL CHECK (length(line_name) > 0),
    line_role        varchar(16)   NOT NULL CHECK (line_role IN ('Entry', 'Exit', 'Count', 'OverflowEntry')),
    source           varchar(8)    NOT NULL CHECK (source IN ('Ariva', 'Vendor')),
    minute_utc       timestamptz   NOT NULL,
    profile_version  integer       NOT NULL CHECK (profile_version >= 0),
    crossings_in     bigint        NOT NULL DEFAULT 0 CHECK (crossings_in >= 0),
    crossings_out    bigint        NOT NULL DEFAULT 0 CHECK (crossings_out >= 0),
    updated_on       timestamptz   NOT NULL,
    PRIMARY KEY (zone_key, line_name, source, minute_utc)
);

-- The runtime role has SELECT, INSERT and UPDATE from the default privileges of 0001; it writes and upserts, never
-- deletes or truncates (as for queue_minute in 0018).
REVOKE DELETE, TRUNCATE ON line_minute FROM ariva_runtime;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'timescaledb') THEN
        IF coalesce(current_setting('ariva.allow_plain_postgres', true), '') <> 'on' THEN
            RAISE EXCEPTION 'TimescaleDB is required: line_minute is a hypertable with a continuous aggregate';
        END IF;
        RAISE NOTICE 'TimescaleDB is not available (ariva.allow_plain_postgres): line_minute stays an ordinary table';
        RETURN;
    END IF;
    CREATE EXTENSION IF NOT EXISTS timescaledb;
    PERFORM create_hypertable('line_minute', 'minute_utc', chunk_time_interval => INTERVAL '7 days');
    EXECUTE $view$
        CREATE MATERIALIZED VIEW line_minute_15m WITH (timescaledb.continuous) AS
        SELECT zone_key,
               line_name,
               line_role,
               source,
               profile_version,
               time_bucket(INTERVAL '15 minutes', minute_utc) AS bucket_utc,
               sum(crossings_in) AS crossings_in,
               sum(crossings_out) AS crossings_out,
               count(*) AS minutes
        FROM line_minute
        GROUP BY zone_key, line_name, line_role, source, profile_version, time_bucket(INTERVAL '15 minutes', minute_utc)
        WITH NO DATA
    $view$;
    PERFORM add_continuous_aggregate_policy('line_minute_15m',
        start_offset => INTERVAL '3 days', end_offset => INTERVAL '1 minute', schedule_interval => INTERVAL '1 minute');
    -- Read only for the runtime role: the view and the hypertable behind it (default privileges would allow writes).
    EXECUTE 'REVOKE ALL ON line_minute_15m FROM ariva_runtime';
    EXECUTE 'GRANT SELECT ON line_minute_15m TO ariva_runtime';
    EXECUTE (SELECT format('REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON %I.%I FROM ariva_runtime', materialization_hypertable_schema, materialization_hypertable_name)
             FROM timescaledb_information.continuous_aggregates WHERE view_name = 'line_minute_15m');
END
$$;
