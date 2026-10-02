-- 0019 Golden replay (ARV-036). The queue stream now reads device health with the sensing batches, so the zone's device
-- outages follow from its own records and a replay reproduces them:
--   device_health_event  every device health report as received (the archive's fifth kind), 90 days like sensing_event;
--   zone_outage          the device outages the stream found (from the minute a device was last heard in to the minute it
--                        was heard again), one row per zone, device and start;
--   replay_run           one row per replay command: the range, the profile version, the settings and the heads of the
--                        hash chains of its inputs and outputs, so an exported replay can be checked against the record
--                        kept here. Append-only for the runtime role; the database sets each row's time and login and
--                        chains it to the row before (row_hash), so a row cannot be backdated or slipped in unnoticed.
CREATE TABLE device_health_event (
    received_utc     timestamptz   NOT NULL,
    site_code        varchar(17)   NOT NULL,
    queue_zone_name  varchar(200)  NOT NULL,
    device_id        uuid          NOT NULL,
    device_code      varchar(16)   NOT NULL,
    event_id         uuid          NOT NULL,
    online           boolean       NOT NULL,
    commissioned     boolean       NOT NULL,
    status_utc       timestamptz,
    clock_state      varchar(16)   CHECK (clock_state IN ('Ok', 'Corrected', 'Unreliable'))
);

CREATE UNIQUE INDEX ux_device_health_event ON device_health_event (event_id, received_utc);
CREATE INDEX ix_device_health_event_zone ON device_health_event (site_code, queue_zone_name, received_utc);

CREATE TABLE zone_outage (
    zone_key      varchar(220)  NOT NULL,
    device_code   varchar(16)   NOT NULL,
    from_utc      timestamptz   NOT NULL,
    to_utc        timestamptz   NOT NULL,
    closed        boolean       NOT NULL,
    recorded_on   timestamptz   NOT NULL,
    PRIMARY KEY (zone_key, device_code, from_utc),
    CHECK (to_utc > from_utc)
);

CREATE TABLE replay_run (
    id               uuid          PRIMARY KEY,
    format           varchar(32)   NOT NULL,
    site_code        varchar(17)   NOT NULL,
    zones            text          NOT NULL CHECK (length(zones) <= 20000),
    from_utc         timestamptz   NOT NULL,
    to_utc           timestamptz   NOT NULL,
    profile_version  integer       NOT NULL CHECK (profile_version >= 0),
    settings_hash    char(64)      NOT NULL,
    input_records    bigint        NOT NULL CHECK (input_records >= 0),
    output_records   bigint        NOT NULL CHECK (output_records >= 0),
    input_head       char(64)      NOT NULL,
    output_head      char(64)      NOT NULL,
    replay_hash      char(64)      NOT NULL,
    requested_by     varchar(100)  NOT NULL,
    created_on       timestamptz   NOT NULL,
    recorded_by      varchar(100)  NOT NULL,
    previous_hash    char(64)      NOT NULL,
    row_hash         char(64)      NOT NULL,
    CHECK (to_utc > from_utc)
);

CREATE INDEX ix_replay_run_hash ON replay_run (replay_hash);
CREATE INDEX ix_replay_run_order ON replay_run (created_on, id);

-- Whatever the client sends, the time is the database's, the login is the session's, and the row hash chains the row to
-- the latest one (one writer at a time). Only the table's owner could disable this trigger. The function names its table
-- with its schema and pins its search path, temporary schema last, so a session's own temporary table cannot stand in
-- for replay_run (the runtime role keeps TEMPORARY: the stream store stages its rows in temporary tables).
CREATE FUNCTION replay_run_anchor() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    previous char(64);
BEGIN
    PERFORM pg_advisory_xact_lock(36, 1);
    SELECT r.row_hash INTO previous FROM public.replay_run r ORDER BY r.created_on DESC, r.id DESC LIMIT 1;
    NEW.created_on := clock_timestamp();
    NEW.recorded_by := left(session_user, 100);
    NEW.previous_hash := coalesce(previous, repeat('0', 64));
    NEW.row_hash := encode(sha256(convert_to(concat_ws('|', NEW.previous_hash, NEW.id::text, NEW.format, NEW.site_code, NEW.zones,
        to_char(NEW.from_utc AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US'), to_char(NEW.to_utc AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US'),
        NEW.profile_version::text, NEW.settings_hash, NEW.input_records::text, NEW.output_records::text, NEW.input_head, NEW.output_head,
        NEW.replay_hash, NEW.requested_by, to_char(NEW.created_on AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US'), NEW.recorded_by), 'UTF8')), 'hex');
    RETURN NEW;
END
$$;

ALTER FUNCTION replay_run_anchor() SET search_path = pg_catalog, public, pg_temp;

CREATE TRIGGER replay_run_anchor BEFORE INSERT ON replay_run FOR EACH ROW EXECUTE FUNCTION replay_run_anchor();

REVOKE UPDATE, DELETE, TRUNCATE ON device_health_event FROM ariva_runtime;
REVOKE DELETE, TRUNCATE ON zone_outage FROM ariva_runtime;
REVOKE UPDATE, DELETE, TRUNCATE ON replay_run FROM ariva_runtime;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'timescaledb') THEN
        IF coalesce(current_setting('ariva.allow_plain_postgres', true), '') <> 'on' THEN
            RAISE EXCEPTION 'TimescaleDB is required: device health events would never be dropped without its retention policy';
        END IF;
        RAISE NOTICE 'TimescaleDB is not available (ariva.allow_plain_postgres): device_health_event stays an ordinary table without retention';
        RETURN;
    END IF;
    CREATE EXTENSION IF NOT EXISTS timescaledb;
    PERFORM create_hypertable('device_health_event', 'received_utc', chunk_time_interval => INTERVAL '1 day');
    EXECUTE 'ALTER TABLE device_health_event SET (timescaledb.compress, timescaledb.compress_segmentby = ''site_code, queue_zone_name'', timescaledb.compress_orderby = ''received_utc'')';
    PERFORM add_compression_policy('device_health_event', INTERVAL '1 day');
    PERFORM add_retention_policy('device_health_event', INTERVAL '90 days');
END
$$;
