-- 0044 Sensor-only desk minutes (ARV-117a, formulas F8, F10 and F11). While AMAN's session is live the site's desk
-- engine takes AMAN's rank, so desk_minute records no sensor-derived time for a desk with an AMAN code, and at a site
-- where every desk has one the shadow nowcast (0043) had no sensor desk term whenever AMAN was live. The desk feed of
-- Ariva.Api.Stream now runs a second, sensor-only desk engine per site beside the published one, fed only by the
-- desks' staff and service zone readings (desk_zone_reading, 0040), never by AMAN's sessions, statistics or
-- transactions, and writes its closed minutes here in the same transaction as the published desk minutes.
--
--   desk_sensor_minute   one row per desk with a staff or service zone and minute: the seconds in each state as the
--                        zones alone say (closed, idle, serving, paused, unknown) and the F11 flag. desk_code is the
--                        desk's key (site, checkpoint and desk code), as in desk_minute. No transactions, no session,
--                        no officer, traveller or document identity.
--
-- Read only by the stream's desk term (DeskTermSource, for the shadow nowcast's sensor-only n_open) and, later, the
-- validation comparison (ARV-104f); not by the live snapshot, the hub, displays, alert inputs, reports or the AMAN
-- contracts (ShadowNowcastExposureTests, and the dependents test in QueueStreamTests). Border desk data: it stays in
-- the border deployment like desk_minute. The runtime role writes and upserts, reads (the desk term), and never
-- deletes or truncates. Retention as desk_minute (no retention policy, To confirm with desk_minute's). TimescaleDB is
-- required, as in 0018.
CREATE TABLE desk_sensor_minute (
    desk_code        varchar(64)       NOT NULL CHECK (position('/' IN desk_code) > 1),
    minute_utc       timestamptz       NOT NULL,
    closed_seconds   double precision  NOT NULL CHECK (closed_seconds BETWEEN 0 AND 60),
    idle_seconds     double precision  NOT NULL CHECK (idle_seconds BETWEEN 0 AND 60),
    serving_seconds  double precision  NOT NULL CHECK (serving_seconds BETWEEN 0 AND 60),
    paused_seconds   double precision  NOT NULL CHECK (paused_seconds BETWEEN 0 AND 60),
    unknown_seconds  double precision  NOT NULL CHECK (unknown_seconds BETWEEN 0 AND 60),
    degraded         boolean           NOT NULL,
    updated_on       timestamptz       NOT NULL,
    PRIMARY KEY (desk_code, minute_utc),
    -- One minute holds 60 seconds; a little room for the binary rounding of the five parts.
    CONSTRAINT ck_desk_sensor_minute_total CHECK (closed_seconds + idle_seconds + serving_seconds + paused_seconds + unknown_seconds <= 60.001)
);

REVOKE DELETE, TRUNCATE ON desk_sensor_minute FROM ariva_runtime;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'timescaledb') THEN
        IF coalesce(current_setting('ariva.allow_plain_postgres', true), '') <> 'on' THEN
            RAISE EXCEPTION 'TimescaleDB is required: desk_sensor_minute is a hypertable like desk_minute';
        END IF;
        RAISE NOTICE 'TimescaleDB is not available (ariva.allow_plain_postgres): desk_sensor_minute stays an ordinary table';
        RETURN;
    END IF;
    CREATE EXTENSION IF NOT EXISTS timescaledb;
    PERFORM create_hypertable('desk_sensor_minute', 'minute_utc', chunk_time_interval => INTERVAL '7 days');
END
$$;
