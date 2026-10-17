-- 0039 Overflow band occupancy (ARV-115). The queue state engine already counts an overflow band's occupancy in the
-- queue length; the stream now keeps each band's own occupancy per minute, so that overflow minutes can be counted per
-- bin (an SLA KPI option, F17) and rule R-002 (OverflowOccupied) has data:
--
--   overflow_minute   one row per queue zone, overflow band and minute with a reading: the lowest and highest occupancy
--                     the band's sensors reported in the minute, under the zone profile version that produced it.
--                     Written by Ariva.Api.Stream in the checkpoint transaction of queue_minute (binary COPY into a
--                     staging table, then an upsert by key) once the engine's watermark has passed the minute, so a
--                     replay after a restart rewrites the same rows (idempotent). A minute without a reading has no row:
--                     the band's occupancy is unknown then, never zero. Only if more band minutes are open than the
--                     engine's bound is a minute released early; its later parts merge into the row (lowest of the lows,
--                     highest of the highs), which gives the same row whatever the order.
--   overflow_bin_15m  overflow minutes per queue zone and 15-minute bin (bins from UTC midnight, the default bin length):
--                     a minute counts when any band of the zone held anyone in it (TC-19, decided 2026-10-06: a minute with any
--                     occupancy), with the minutes observed and the most people one band held. A plain view over
--                     overflow_minute, so it is always as current as the rows the checkpoint wrote.
--
-- OverflowDetected (ariva.flow.overflow-detected.v1) goes through outbox_message (0011) in the same transaction; no table
-- of its own. Retention as queue_minute: results are evidence, kept with no retention or compression policy (only an
-- operator with the migration login removes rows). TimescaleDB is required, as in 0018.
CREATE TABLE overflow_minute (
    zone_key         varchar(220)  NOT NULL,
    band_name        varchar(200)  NOT NULL CHECK (length(band_name) > 0),
    minute_utc       timestamptz   NOT NULL,
    profile_version  integer       NOT NULL CHECK (profile_version >= 0),
    min_occupancy    integer       NOT NULL CHECK (min_occupancy >= 0),
    max_occupancy    integer       NOT NULL CHECK (max_occupancy BETWEEN 0 AND 10000),
    updated_on       timestamptz   NOT NULL,
    PRIMARY KEY (zone_key, band_name, minute_utc),
    CHECK (min_occupancy <= max_occupancy)
);

-- The runtime role has SELECT, INSERT and UPDATE from the default privileges of 0001; it writes and upserts, never
-- deletes or truncates (as for queue_minute in 0018).
REVOKE DELETE, TRUNCATE ON overflow_minute FROM ariva_runtime;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'timescaledb') THEN
        IF coalesce(current_setting('ariva.allow_plain_postgres', true), '') <> 'on' THEN
            RAISE EXCEPTION 'TimescaleDB is required: overflow_minute is a hypertable';
        END IF;
        RAISE NOTICE 'TimescaleDB is not available (ariva.allow_plain_postgres): overflow_minute stays an ordinary table';
        RETURN;
    END IF;
    CREATE EXTENSION IF NOT EXISTS timescaledb;
    PERFORM create_hypertable('overflow_minute', 'minute_utc', chunk_time_interval => INTERVAL '7 days');
END
$$;

CREATE VIEW overflow_bin_15m AS
SELECT zone_key,
       date_bin(INTERVAL '15 minutes', minute_utc, TIMESTAMPTZ '2000-01-01 00:00:00+00') AS start_utc,
       count(DISTINCT minute_utc) FILTER (WHERE max_occupancy > 0) AS overflow_minutes,
       count(DISTINCT minute_utc) AS observed_minutes,
       max(max_occupancy) AS peak_band_occupancy,
       min(profile_version) AS profile_version_from,
       max(profile_version) AS profile_version_to
FROM overflow_minute
GROUP BY zone_key, date_bin(INTERVAL '15 minutes', minute_utc, TIMESTAMPTZ '2000-01-01 00:00:00+00');

-- Read only for the runtime role (default privileges would allow writes through the view).
REVOKE ALL ON overflow_bin_15m FROM ariva_runtime;
GRANT SELECT ON overflow_bin_15m TO ariva_runtime;
