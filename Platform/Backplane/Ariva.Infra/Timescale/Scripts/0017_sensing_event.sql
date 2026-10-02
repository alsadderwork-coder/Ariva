-- 0017 Raw sensing event archive (ARV-026). Every canonical event Ingest accepted, one row each, for replay and for
-- recomputing a disputed period from raw data (docs/architecture/overview.md section 6). Written by binary COPY;
-- the runtime role can insert and read but never update, delete or truncate (integrity: only retention removes rows).
-- sensing_batch records each archived batch once, so a batch delivered twice (a consumer restart, a resent body) is
-- archived once.
--
-- Track ids never persist past the operating day (D4, ADR-0011): an archived track id is a keyed pseudonym of the day
-- (HMAC-SHA256 with a random key per UTC day in sensing_day_key), so tracks cannot be linked across days. A day's key is
-- destroyed once no event of that day can still arrive (3 days, the topics' retention and the ingest's maximum age).
--
-- TimescaleDB is required: both tables become hypertables with 1-day chunks, sensing_event compressed after 1 day,
-- both dropped after 90 days (values To confirm per site: the contract's dispute window). Without TimescaleDB the
-- script fails, unless the database sets ariva.allow_plain_postgres to on (local tests on plain PostgreSQL only), in
-- which case the tables stay ordinary and nothing is dropped.
CREATE TABLE sensing_batch (
    id               uuid          NOT NULL,
    received_on      timestamptz   NOT NULL,
    site_code        varchar(17)   NOT NULL,
    queue_zone_name  varchar(200)  NOT NULL,
    kind             varchar(16)   NOT NULL CHECK (kind IN ('Track', 'Crossing', 'Occupancy', 'Interval')),
    device_id        uuid          NOT NULL,
    events           integer       NOT NULL CHECK (events BETWEEN 1 AND 3000),
    archived_on      timestamptz   NOT NULL
);

CREATE INDEX ix_sensing_batch_id ON sensing_batch (id, received_on);

CREATE TABLE sensing_event (
    time_utc         timestamptz       NOT NULL,
    site_code        varchar(17)       NOT NULL,
    queue_zone_name  varchar(200)      NOT NULL,
    kind             varchar(16)       NOT NULL CHECK (kind IN ('Track', 'Crossing', 'Occupancy', 'Interval')),
    batch_id         uuid              NOT NULL,
    ordinal          integer           NOT NULL CHECK (ordinal >= 0 AND ordinal < 3000),
    device_id        uuid              NOT NULL,
    device_code      varchar(16)       NOT NULL,
    received_utc     timestamptz       NOT NULL,
    flags            smallint          NOT NULL CHECK (flags >= 0),
    commissioned     boolean           NOT NULL,
    track_id         varchar(81),
    x                double precision  CHECK (x >= -2100 AND x <= 2100),
    y                double precision  CHECK (y >= -2100 AND y <= 2100),
    height_metres    double precision  CHECK (height_metres >= 0 AND height_metres <= 3),
    name             varchar(200),
    direction        varchar(3)        CHECK (direction IN ('In', 'Out')),
    count_value      integer           CHECK (count_value >= 0),
    in_count         integer           CHECK (in_count >= 0),
    out_count        integer           CHECK (out_count >= 0),
    from_utc         timestamptz,
    CHECK ((kind = 'Track') = (x IS NOT NULL AND y IS NOT NULL AND track_id IS NOT NULL)),
    CHECK ((kind IN ('Crossing', 'Occupancy', 'Interval')) = (name IS NOT NULL)),
    CHECK ((kind = 'Crossing') = (direction IS NOT NULL)),
    CHECK ((kind = 'Occupancy') = (count_value IS NOT NULL)),
    CHECK ((kind = 'Interval') = (in_count IS NOT NULL AND out_count IS NOT NULL AND from_utc IS NOT NULL))
);

CREATE INDEX ix_sensing_event_zone_time ON sensing_event (site_code, queue_zone_name, time_utc);

CREATE TABLE sensing_day_key (
    day         date         PRIMARY KEY,
    key         bytea        NOT NULL CHECK (length(key) = 32),
    created_on  timestamptz  NOT NULL
);

REVOKE UPDATE, DELETE, TRUNCATE ON sensing_event, sensing_batch FROM ariva_runtime;
REVOKE UPDATE, TRUNCATE ON sensing_day_key FROM ariva_runtime;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'timescaledb') THEN
        IF coalesce(current_setting('ariva.allow_plain_postgres', true), '') <> 'on' THEN
            RAISE EXCEPTION 'TimescaleDB is required: raw sensing events would never be dropped without its retention policy';
        END IF;
        RAISE NOTICE 'TimescaleDB is not available (ariva.allow_plain_postgres): sensing_event and sensing_batch stay ordinary tables without retention';
        RETURN;
    END IF;
    CREATE EXTENSION IF NOT EXISTS timescaledb;
    PERFORM create_hypertable('sensing_event', 'time_utc', chunk_time_interval => INTERVAL '1 day');
    PERFORM create_hypertable('sensing_batch', 'received_on', chunk_time_interval => INTERVAL '1 day');
    EXECUTE 'ALTER TABLE sensing_event SET (timescaledb.compress, timescaledb.compress_segmentby = ''site_code, queue_zone_name, kind'', timescaledb.compress_orderby = ''time_utc'')';
    PERFORM add_compression_policy('sensing_event', INTERVAL '1 day');
    PERFORM add_retention_policy('sensing_event', INTERVAL '90 days');
    PERFORM add_retention_policy('sensing_batch', INTERVAL '90 days');
END
$$;
