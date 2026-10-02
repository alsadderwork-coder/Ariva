-- 0018 Stream outputs (ARV-034). What Ariva.Api.Stream computes from the sensing topics, written once per checkpoint
-- by binary COPY into a staging table and an upsert, so that replaying records after a restart rewrites the same rows
-- (idempotent): the key of every row is its zone or desk and its minute or bin.
--
--   queue_minute   one row per queue zone and minute: entries and exits counted in the minute, the waits of the
--                  people who entered in it (F6, F7; provisional until their bin is final), and the queue length and
--                  nowcast (F8) at the end of the minute.
--   queue_bin      every revision of a zone's 15-minute bin result (ADR-0007): final results never change in place,
--                  a recomputation adds a revision.
--   desk_minute    per-desk minute aggregates of the desk state (F10): time in each state and transactions, no
--                  identity of any officer, traveller or document.
--   egate_minute   per-gate minute aggregates of e-gate outcomes (F12), filled from the AMAN feed (ARV-049).
--   stream_zone_state   the latest snapshot of each zone's stream state, written in the same transaction as its rows.
--   stream_offset       the next offset per consumer group, topic and partition, in the same transaction: on
--                       assignment the consumer starts there, so a record is never applied twice to a saved state.
--   stream_partition_count   every partition count the sensing topics have had (records keep the partition their
--                       key hashed to when they were produced).
--
-- queue_minute_15m is the continuous aggregate of queue_minute per 15 minutes (counts, the longest queue and nowcast)
-- for screens and reports; the authoritative bin results are queue_bin. TimescaleDB is required, as in 0017.
CREATE TABLE queue_minute (
    zone_key              varchar(220)      NOT NULL,
    minute_utc            timestamptz       NOT NULL,
    profile_version       integer           NOT NULL CHECK (profile_version >= 0),
    status                varchar(12)       CHECK (status IN ('Provisional', 'Final')),
    entries               bigint            NOT NULL DEFAULT 0 CHECK (entries >= 0),
    exits                 bigint            NOT NULL DEFAULT 0 CHECK (exits >= 0),
    waits                 bigint            NOT NULL DEFAULT 0 CHECK (waits >= 0),
    mean_wait_minutes     double precision,
    p50_wait_minutes      double precision,
    p90_wait_minutes      double precision,
    p95_wait_minutes      double precision,
    share_within_target   double precision  CHECK (share_within_target BETWEEN 0 AND 1),
    queue_length          integer           CHECK (queue_length >= 0),
    length_from_sensors   boolean,
    length_degraded       boolean,
    nowcast_minutes       double precision  CHECK (nowcast_minutes >= 0),
    throughput_per_minute double precision  CHECK (throughput_per_minute >= 0),
    no_service            varchar(20),
    nowcast_degraded      boolean,
    updated_on            timestamptz       NOT NULL,
    PRIMARY KEY (zone_key, minute_utc)
);

CREATE TABLE queue_bin (
    zone_key              varchar(220)      NOT NULL,
    start_utc             timestamptz       NOT NULL,
    revision              integer           NOT NULL CHECK (revision >= 0),
    length_minutes        integer           NOT NULL CHECK (length_minutes BETWEEN 1 AND 1440),
    status                varchar(12)       NOT NULL CHECK (status IN ('Provisional', 'Final')),
    quality               varchar(10)       NOT NULL CHECK (quality IN ('Good', 'Degraded', 'Unknown')),
    entries               bigint            NOT NULL CHECK (entries >= 0),
    exits                 bigint            NOT NULL CHECK (exits >= 0),
    waits                 bigint            NOT NULL CHECK (waits >= 0),
    mean_wait_minutes     double precision,
    p50_wait_minutes      double precision,
    p90_wait_minutes      double precision,
    p95_wait_minutes      double precision,
    share_within_target   double precision  CHECK (share_within_target BETWEEN 0 AND 1),
    abandoned             bigint            NOT NULL CHECK (abandoned >= 0),
    fragmented            bigint            NOT NULL CHECK (fragmented >= 0),
    censored              bigint            NOT NULL CHECK (censored >= 0),
    reanchored            bigint            NOT NULL CHECK (reanchored >= 0),
    rejected              bigint            NOT NULL CHECK (rejected >= 0),
    open_people           bigint            NOT NULL CHECK (open_people >= 0),
    late_events           bigint            NOT NULL CHECK (late_events >= 0),
    profile_version       integer           NOT NULL CHECK (profile_version >= 0),
    revision_reason       varchar(200),
    updated_on            timestamptz       NOT NULL,
    PRIMARY KEY (zone_key, start_utc, revision)
);

CREATE TABLE desk_minute (
    desk_code             varchar(64)       NOT NULL,
    lane                  varchar(64)       NOT NULL,
    minute_utc            timestamptz       NOT NULL,
    closed_seconds        double precision  NOT NULL CHECK (closed_seconds BETWEEN 0 AND 60),
    idle_seconds          double precision  NOT NULL CHECK (idle_seconds BETWEEN 0 AND 60),
    serving_seconds       double precision  NOT NULL CHECK (serving_seconds BETWEEN 0 AND 60),
    paused_seconds        double precision  NOT NULL CHECK (paused_seconds BETWEEN 0 AND 60),
    unknown_seconds       double precision  NOT NULL CHECK (unknown_seconds BETWEEN 0 AND 60),
    transactions          integer           NOT NULL CHECK (transactions >= 0),
    sensor_derived_seconds double precision NOT NULL CHECK (sensor_derived_seconds BETWEEN 0 AND 60),
    present_seconds       double precision  NOT NULL CHECK (present_seconds BETWEEN 0 AND 60),
    degraded              boolean           NOT NULL,
    updated_on            timestamptz       NOT NULL,
    PRIMARY KEY (desk_code, minute_utc)
);

CREATE TABLE egate_minute (
    gate_code             varchar(64)       NOT NULL,
    lane                  varchar(64)       NOT NULL,
    minute_utc            timestamptz       NOT NULL,
    in_service_seconds    double precision  NOT NULL CHECK (in_service_seconds BETWEEN 0 AND 60),
    processed             integer           NOT NULL CHECK (processed >= 0),
    rejected              integer           NOT NULL CHECK (rejected >= 0),
    mean_cycle_seconds    double precision  CHECK (mean_cycle_seconds > 0),
    degraded              boolean           NOT NULL,
    updated_on            timestamptz       NOT NULL,
    PRIMARY KEY (gate_code, minute_utc)
);

CREATE TABLE stream_zone_state (
    zone_key              varchar(220)      PRIMARY KEY,
    profile_version       integer           NOT NULL,
    state                 jsonb             NOT NULL,
    state_bytes           integer           NOT NULL CHECK (state_bytes > 0),
    reference_utc         timestamptz       NOT NULL,
    updated_on            timestamptz       NOT NULL
);

-- Every partition count the sensing topics have had: records produced before partitions were added hash over the
-- earlier count, so a worker started later (a restart, a recompute from the earliest offsets) must still accept them.
CREATE TABLE stream_partition_count (
    topic_set             varchar(100)      NOT NULL,
    partitions            integer           NOT NULL CHECK (partitions BETWEEN 1 AND 10000),
    first_seen            timestamptz       NOT NULL,
    PRIMARY KEY (topic_set, partitions)
);

CREATE TABLE stream_offset (
    consumer_group        varchar(200)      NOT NULL,
    topic                 varchar(249)      NOT NULL,
    partition_no          integer           NOT NULL CHECK (partition_no >= 0),
    next_offset           bigint            NOT NULL CHECK (next_offset >= 0),
    updated_on            timestamptz       NOT NULL,
    PRIMARY KEY (consumer_group, topic, partition_no)
);

-- Results are evidence: the runtime role writes and upserts, never deletes or truncates (only retention removes rows),
-- and a Final bin revision never changes (a recomputation adds a revision). Resetting a zone's stream state is an
-- operator action with the migration login (runbook 4.5b).
REVOKE DELETE, TRUNCATE ON queue_minute, queue_bin, desk_minute, egate_minute FROM ariva_runtime;
REVOKE DELETE, TRUNCATE ON stream_zone_state, stream_offset FROM ariva_runtime;
REVOKE UPDATE, DELETE, TRUNCATE ON stream_partition_count FROM ariva_runtime;

CREATE FUNCTION queue_bin_final_is_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.status = 'Final' THEN
        RAISE EXCEPTION 'queue_bin % % revision % is final and cannot change; add a revision instead', OLD.zone_key, OLD.start_utc, OLD.revision
            USING ERRCODE = 'restrict_violation';
    END IF;
    RETURN NEW;
END
$$;

CREATE TRIGGER trg_queue_bin_final_is_immutable BEFORE UPDATE ON queue_bin FOR EACH ROW EXECUTE FUNCTION queue_bin_final_is_immutable();

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'timescaledb') THEN
        IF coalesce(current_setting('ariva.allow_plain_postgres', true), '') <> 'on' THEN
            RAISE EXCEPTION 'TimescaleDB is required: the stream outputs are hypertables with a continuous aggregate';
        END IF;
        RAISE NOTICE 'TimescaleDB is not available (ariva.allow_plain_postgres): the stream outputs stay ordinary tables';
        RETURN;
    END IF;
    CREATE EXTENSION IF NOT EXISTS timescaledb;
    PERFORM create_hypertable('queue_minute', 'minute_utc', chunk_time_interval => INTERVAL '7 days');
    PERFORM create_hypertable('queue_bin', 'start_utc', chunk_time_interval => INTERVAL '30 days');
    PERFORM create_hypertable('desk_minute', 'minute_utc', chunk_time_interval => INTERVAL '7 days');
    PERFORM create_hypertable('egate_minute', 'minute_utc', chunk_time_interval => INTERVAL '7 days');
    EXECUTE $view$
        CREATE MATERIALIZED VIEW queue_minute_15m WITH (timescaledb.continuous) AS
        SELECT zone_key,
               time_bucket(INTERVAL '15 minutes', minute_utc) AS bucket_utc,
               sum(entries) AS entries,
               sum(exits) AS exits,
               max(queue_length) AS max_queue_length,
               avg(queue_length) AS mean_queue_length,
               max(nowcast_minutes) AS max_nowcast_minutes,
               bool_or(coalesce(length_degraded, false) OR coalesce(nowcast_degraded, false)) AS degraded,
               count(*) AS minutes
        FROM queue_minute
        GROUP BY zone_key, time_bucket(INTERVAL '15 minutes', minute_utc)
        WITH NO DATA
    $view$;
    PERFORM add_continuous_aggregate_policy('queue_minute_15m',
        start_offset => INTERVAL '3 days', end_offset => INTERVAL '1 minute', schedule_interval => INTERVAL '1 minute');
    -- Read only for the runtime role: the view and the hypertable behind it (default privileges would allow writes).
    EXECUTE 'REVOKE ALL ON queue_minute_15m FROM ariva_runtime';
    EXECUTE 'GRANT SELECT ON queue_minute_15m TO ariva_runtime';
    EXECUTE (SELECT format('REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON %I.%I FROM ariva_runtime', materialization_hypertable_schema, materialization_hypertable_name)
             FROM timescaledb_information.continuous_aggregates WHERE view_name = 'queue_minute_15m');
END
$$;
