-- 0040 Staff and service zone readings for the desk engine (ARV-116, formulas F10 ranks 3 and 4). The stream's zone
-- processor no longer drops the occupancy of a staff or service zone that names its desk in the published zone profile:
-- it passes the desk's key, the zone's role, the reading's time, the count and the sensing pipeline's flag on, and
-- Ariva.Api.Stream writes them here in the checkpoint transaction of queue_minute (binary COPY into a staging table, then
-- an insert that keeps the first row of a key, so a replay after a restart adds nothing). The desk feed (ARV-049) reads
-- each site's new rows by the time they were written, with the same overlap and memory of rows taken as for AMAN's
-- records (0031), and offers them to the site's desk engine, whose minutes go to desk_minute (0018).
--
--   desk_zone_reading   one row per desk, zone role and reading time: counts only. No track, officer, traveller or
--                       document identity (data boundary); desk_code is the desk's key (site, checkpoint and desk code),
--                       as in desk_minute.
--
-- The readings are an input, not evidence: desk_minute keeps what they produced, and the raw events stay in
-- sensing_event (0017) for recomputation. Retention 7 days (Proposed, To confirm), chunks of 1 day. The runtime role
-- inserts and reads; it never updates, deletes or truncates (only retention removes rows). TimescaleDB is required, as in 0018.
CREATE TABLE desk_zone_reading (
    id               uuid          NOT NULL,
    site_code        varchar(17)   NOT NULL CHECK (length(site_code) > 0),
    desk_code        varchar(64)   NOT NULL,
    source           varchar(12)   NOT NULL CHECK (source IN ('StaffZone', 'ServiceZone')),
    reading_utc      timestamptz   NOT NULL,
    occupancy        smallint      NOT NULL CHECK (occupancy BETWEEN 0 AND 50),
    degraded         boolean       NOT NULL,
    zone_key         varchar(220)  NOT NULL,
    profile_version  integer       NOT NULL CHECK (profile_version >= 0),
    written_utc      timestamptz   NOT NULL,
    PRIMARY KEY (desk_code, source, reading_utc),
    -- A desk's key starts with its site's code, so a site's feed reads its own desks only.
    CHECK (starts_with(desk_code, site_code || '/') AND length(desk_code) > length(site_code) + 1)
);

-- The desk feed reads each site's rows from a position in write time.
CREATE INDEX ix_desk_zone_reading_written ON desk_zone_reading (site_code, written_utc);

REVOKE UPDATE, DELETE, TRUNCATE ON desk_zone_reading FROM ariva_runtime;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'timescaledb') THEN
        IF coalesce(current_setting('ariva.allow_plain_postgres', true), '') <> 'on' THEN
            RAISE EXCEPTION 'TimescaleDB is required: desk_zone_reading is a hypertable with a retention policy';
        END IF;
        RAISE NOTICE 'TimescaleDB is not available (ariva.allow_plain_postgres): desk_zone_reading stays an ordinary table without retention';
        RETURN;
    END IF;
    CREATE EXTENSION IF NOT EXISTS timescaledb;
    PERFORM create_hypertable('desk_zone_reading', 'reading_utc', chunk_time_interval => INTERVAL '1 day');
    PERFORM add_retention_policy('desk_zone_reading', INTERVAL '7 days');
END
$$;
