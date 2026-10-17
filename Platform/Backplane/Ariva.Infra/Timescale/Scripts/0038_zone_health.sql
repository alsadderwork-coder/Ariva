-- 0038 Continuous health checks (ARV-114a, formulas F18). Two changes:
--
--   zone.physical_capacity   the people a queue zone or overflow band holds at most (1 to 5,000), optional, set on a
--                            draft and kept by its published version (not part of the geometry hash, F22 unchanged).
--   zone_health_bin          one row per queue zone, bin and revision, beside queue_bin: the conservation residual
--                            (entries - exits) - (Occ(end) - Occ(start)) from the queue's sensor occupancy at the bin's
--                            two boundaries (null when either has none), the tracks that entered in the bin and what
--                            became of them (exited, abandoned, fragmented, censored, rejected, still open) with the
--                            track completion rate (null when no track entered), and the minutes with occupancy
--                            readings, those checked against a physical capacity and those outside 0 to it. Written by
--                            Ariva.Api.Stream with every bin result in the checkpoint transaction (binary COPY into a
--                            staging table, then an upsert by key), so a replay after a restart rewrites the same rows; a
--                            Final row never changes (a recomputation adds a revision), as for queue_bin.
--
-- Retention as queue_bin: results are evidence, kept with no retention or compression policy (only an operator with the
-- migration login removes rows). Thresholds and alarms are not stored here (Proposed, ARV-114b). TimescaleDB is
-- required, as in 0018.
ALTER TABLE zone ADD COLUMN physical_capacity integer CHECK (physical_capacity BETWEEN 1 AND 5000);
ALTER TABLE zone ADD CONSTRAINT zone_capacity_kind_check CHECK (physical_capacity IS NULL OR kind IN ('Queue', 'Overflow'));

CREATE TABLE zone_health_bin (
    zone_key                  varchar(220)      NOT NULL,
    start_utc                 timestamptz       NOT NULL,
    revision                  integer           NOT NULL CHECK (revision >= 0),
    length_minutes            integer           NOT NULL CHECK (length_minutes BETWEEN 1 AND 1440),
    status                    varchar(12)       NOT NULL CHECK (status IN ('Provisional', 'Final')),
    profile_version           integer           NOT NULL CHECK (profile_version >= 0),
    entries                   bigint            NOT NULL CHECK (entries >= 0),
    exits                     bigint            NOT NULL CHECK (exits >= 0),
    occupancy_start           integer           CHECK (occupancy_start >= 0),
    occupancy_end             integer           CHECK (occupancy_end >= 0),
    conservation_residual     bigint,
    tracks_entered            bigint            NOT NULL CHECK (tracks_entered >= 0),
    tracks_exited             bigint            NOT NULL CHECK (tracks_exited >= 0),
    tracks_abandoned          bigint            NOT NULL CHECK (tracks_abandoned >= 0),
    tracks_fragmented         bigint            NOT NULL CHECK (tracks_fragmented >= 0),
    tracks_censored           bigint            NOT NULL CHECK (tracks_censored >= 0),
    tracks_rejected           bigint            NOT NULL CHECK (tracks_rejected >= 0),
    tracks_open               bigint            NOT NULL CHECK (tracks_open >= 0),
    track_completion_rate     double precision  CHECK (track_completion_rate BETWEEN 0 AND 1),
    occupancy_minutes         integer           NOT NULL CHECK (occupancy_minutes BETWEEN 0 AND 1440),
    capacity_minutes          integer           NOT NULL CHECK (capacity_minutes >= 0),
    minutes_outside_capacity  integer           NOT NULL CHECK (minutes_outside_capacity >= 0),
    updated_on                timestamptz       NOT NULL,
    PRIMARY KEY (zone_key, start_utc, revision),
    CHECK (capacity_minutes <= occupancy_minutes AND minutes_outside_capacity <= occupancy_minutes),
    CHECK ((conservation_residual IS NULL) = (occupancy_start IS NULL OR occupancy_end IS NULL)),
    CHECK ((track_completion_rate IS NULL) = (tracks_entered = 0))
);

-- The runtime role has SELECT, INSERT and UPDATE from the default privileges of 0001; it writes and upserts, never
-- deletes or truncates (as for queue_bin in 0018), and a Final row never changes.
REVOKE DELETE, TRUNCATE ON zone_health_bin FROM ariva_runtime;

CREATE FUNCTION zone_health_bin_final_is_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.status = 'Final' THEN
        RAISE EXCEPTION 'zone_health_bin % % revision % is final and cannot change; add a revision instead', OLD.zone_key, OLD.start_utc, OLD.revision
            USING ERRCODE = 'restrict_violation';
    END IF;
    RETURN NEW;
END
$$;

CREATE TRIGGER trg_zone_health_bin_final_is_immutable BEFORE UPDATE ON zone_health_bin FOR EACH ROW EXECUTE FUNCTION zone_health_bin_final_is_immutable();

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'timescaledb') THEN
        IF coalesce(current_setting('ariva.allow_plain_postgres', true), '') <> 'on' THEN
            RAISE EXCEPTION 'TimescaleDB is required: zone_health_bin is a hypertable';
        END IF;
        RAISE NOTICE 'TimescaleDB is not available (ariva.allow_plain_postgres): zone_health_bin stays an ordinary table';
        RETURN;
    END IF;
    CREATE EXTENSION IF NOT EXISTS timescaledb;
    PERFORM create_hypertable('zone_health_bin', 'start_utc', chunk_time_interval => INTERVAL '30 days');
END
$$;
