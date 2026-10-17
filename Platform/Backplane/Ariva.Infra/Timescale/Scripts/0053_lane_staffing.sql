-- 0053 Indicative staffing recommendation per lane and minute (ARV-181a, formulas F16 pilot version).
--
--   lane_staffing  per arrivals-hall lane (its queue zone's key, site/zone) and computed minute, the 13 intervals of the
--                  plan (interval_index 0 is the current quarter hour from that minute, 1 to 12 the following ones): the
--                  predicted arrivals, the recommended open desks and what set them (the target wait, the busy cap, both),
--                  whether the lane is at its desk limit and by how many desks it falls short, the predicted waits with
--                  the recommendation and with the desks open now, the backlog left, and why an interval has no
--                  recommendation (gap). Each row repeats the lane's values of the minute: the reason the whole lane has
--                  none (lane_gap: no desks, no throughput measured or configured, no queue length now), the queue now and its minute,
--                  the desks open now (the desk-state count, F8 n_open) with its flag, the desks in service, the target
--                  wait and busy cap used, and the throughput per open desk with its source.
--
-- Written once a minute by Ariva.Api.Cronz (binary COPY into a staging table, then INSERT ... ON CONFLICT DO NOTHING
-- under a per-site advisory lock, class 51): the first computation of a minute stands, so the immigration screen and the
-- DesksBelowPlan alert (ARV-181b) read one computation, and a backtest reads what the live evaluation read. The runtime
-- role inserts and reads; it cannot update, delete or truncate a row. Lane aggregates only: no desk code, no officer,
-- traveller or document identity. Border data (the lane split of the arrival wave): read only by callers with
-- ArrivalWaveLanes.View. Kept 90 days (the alert backtest's history), decision of 2026-10-15; ARV-111a makes hypertable
-- retention a migration parameter per environment.
CREATE TABLE lane_staffing (
    zone_key             varchar(220)      NOT NULL,
    minute_utc           timestamptz       NOT NULL CHECK (date_trunc('minute', minute_utc) = minute_utc),
    interval_index       smallint          NOT NULL CHECK (interval_index BETWEEN 0 AND 12),
    lane                 varchar(4)        NOT NULL CHECK (lane ~ '^[A-Z]{2,4}$'),
    start_utc            timestamptz       NOT NULL CHECK (date_trunc('minute', start_utc) = start_utc),
    minutes              smallint          NOT NULL CHECK (minutes BETWEEN 1 AND 15),
    arrivals             double precision  CHECK (arrivals >= 0 AND arrivals <= 1000000),
    recommended          smallint          CHECK (recommended BETWEEN 0 AND 1000),
    bound                varchar(4)        NOT NULL CHECK (bound IN ('None', 'Wait', 'Busy', 'Both')),
    at_limit             boolean           NOT NULL,
    shortfall            integer           CHECK (shortfall BETWEEN 1 AND 1000000),
    wait_recommended     double precision  CHECK (wait_recommended >= 0 AND wait_recommended <= 120),
    wait_open            double precision  CHECK (wait_open >= 0 AND wait_open <= 120),
    no_service_open      boolean           NOT NULL,
    backlog_end          double precision  CHECK (backlog_end >= 0 AND backlog_end <= 20000000),
    gap                  varchar(24)       NOT NULL CHECK (gap IN ('None', 'NoQueueLength', 'NoThroughput', 'FlightWithoutPassengers', 'BacklogUnknown',
                                                               'ArrivalsTruncated', 'NoDesks', 'NoLaneDemand', 'ArrivalsOutOfRange')),
    lane_gap             varchar(24)       NOT NULL CHECK (lane_gap IN ('None', 'NoQueueLength', 'NoThroughput', 'NoDesks')),
    queue_minute_utc     timestamptz,
    queue_now            double precision  CHECK (queue_now >= 0 AND queue_now <= 1000000),
    open_now             smallint          CHECK (open_now BETWEEN 0 AND 1000),
    open_now_degraded    boolean           NOT NULL,
    desks                smallint          NOT NULL CHECK (desks BETWEEN 0 AND 1000),
    target_minutes       double precision  NOT NULL CHECK (target_minutes >= 1 AND target_minutes <= 120),
    busy_cap             double precision  NOT NULL CHECK (busy_cap >= 0.5 AND busy_cap <= 1),
    throughput_per_desk  double precision  CHECK (throughput_per_desk > 0 AND throughput_per_desk <= 1000),
    throughput_source    varchar(8)        CHECK (throughput_source IN ('Measured', 'Fallback')),
    computed_utc         timestamptz       NOT NULL,
    PRIMARY KEY (zone_key, minute_utc, interval_index),
    CHECK (start_utc >= minute_utc AND start_utc < minute_utc + INTERVAL '195 minutes'),
    CHECK ((gap = 'None') = (recommended IS NOT NULL)),
    CHECK (lane_gap = 'None' OR recommended IS NULL),
    CHECK (recommended IS NULL OR recommended <= desks),
    CHECK (NOT at_limit OR recommended = desks),
    CHECK (shortfall IS NULL OR at_limit),
    CHECK ((throughput_per_desk IS NULL) = (throughput_source IS NULL)),
    CHECK (computed_utc >= minute_utc)
);

REVOKE UPDATE, DELETE, TRUNCATE ON lane_staffing FROM ariva_runtime;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'timescaledb') THEN
        IF coalesce(current_setting('ariva.allow_plain_postgres', true), '') <> 'on' THEN
            RAISE EXCEPTION 'TimescaleDB is required: lane_staffing is a hypertable with a retention policy';
        END IF;
        RAISE NOTICE 'TimescaleDB is not available (ariva.allow_plain_postgres): lane_staffing stays an ordinary table without retention';
        RETURN;
    END IF;
    CREATE EXTENSION IF NOT EXISTS timescaledb;
    PERFORM create_hypertable('lane_staffing', 'minute_utc', chunk_time_interval => INTERVAL '1 day');
    PERFORM add_retention_policy('lane_staffing', INTERVAL '90 days');
END
$$;
