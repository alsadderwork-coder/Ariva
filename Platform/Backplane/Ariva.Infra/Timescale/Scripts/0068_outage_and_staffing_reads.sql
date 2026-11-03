-- 0068 Outage and staffing reads at their bounds (ARV-228a, docs/security/costly-operations.md section 15 item 14).
--
-- lane_staffing (script 0053): the open desks below recommendation alert, live and in a backtest, reads interval 0 of each
-- computed minute only (AlertInputs.StaffingAsync and LatestStaffingMinutesAsync), but interval 0 sits among intervals 1 to 12
-- of the same minute, in the primary key and on the heap pages alike (the job writes a minute's 13 rows together), so a read
-- of a range touched the pages of every interval. A partial index of interval 0 alone, on the zone and the minute, that
-- carries every column those reads take (INCLUDE) keeps interval 0 apart: the reads are index-only scans of a range of
-- interval 0 entries per zone, and touch no row of another interval. It holds about a thirteenth of the table. The staffing
-- API, which reads a minute's whole plan, keeps the primary key.
--
-- zone_outage (script 0019): the device outages the stream found had no time index and no retention, so every read of a
-- range (the sensor rule's live tick and backtest, the daily and weekly reports, the validation results) took the primary
-- key on the zone with a time condition that does not lead it, and grew with the table's history rather than the range.
--   * It becomes a hypertable on its start (from_utc, 7-day chunks; the primary key already holds the column), so a read of
--     a range skips every chunk that starts after the range ends.
--   * An index on its end (to_utc): an outage overlaps a range when it starts before the range ends and ends after the range
--     starts, so the end bounds every read of a range from below.
--   * A partial index of the outages still open (closed false), which the reports and the validation results take whatever
--     their end, so that condition is an index range too.
--   * Its retention is the raw sensing period (Database:Retention:RawSensingDays, 90 days by default; the migration job sets
--     it as it sets device_health_event's): an outage is drawn from the device health archive, so a replay of a period
--     finds its events, the devices' health and their outages together, and an outage is kept as long as the archive that
--     can reproduce it (the contract's dispute window, TC-62). A chunk is dropped once every outage in it started more than
--     the period ago. The stream writes an outage when it ends (DeviceLiveness): when the device is heard again, or when it
--     is forgotten after the forget limit (24 hours by default, 72 at most), so a live outage is closed within that limit of
--     its device's last push; rows still
--     open come only from a replay's end (ZoneProcessor.Finish). The sensor rule reads a device offline now from the
--     registry as well.
--   * The runtime role's UPDATE narrows to the columns the stream's upsert sets (to_utc, closed, recorded_on): it cannot move
--     an outage's start, the column the retention reads, so it cannot age a row into a chunk the policy drops (CWE-269, as
--     0054 does for the purged tables). It keeps insert and select and, as 0019 has it, never delete or truncate.
CREATE INDEX ix_lane_staffing_interval0 ON lane_staffing (zone_key, minute_utc)
    INCLUDE (interval_index, lane, start_utc, minutes, arrivals, recommended, bound, at_limit, shortfall, wait_recommended, wait_open,
             no_service_open, gap, lane_gap, queue_now, open_now, open_now_degraded, desks, target_minutes, busy_cap, throughput_per_desk,
             throughput_source)
    WHERE interval_index = 0;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'timescaledb') THEN
        IF coalesce(current_setting('ariva.allow_plain_postgres', true), '') <> 'on' THEN
            RAISE EXCEPTION 'TimescaleDB is required: zone_outage becomes a hypertable with a retention policy';
        END IF;
        RAISE NOTICE 'TimescaleDB is not available (ariva.allow_plain_postgres): zone_outage stays an ordinary table without retention';
        RETURN;
    END IF;
    CREATE EXTENSION IF NOT EXISTS timescaledb;
    PERFORM create_hypertable('zone_outage', 'from_utc', chunk_time_interval => INTERVAL '7 days', migrate_data => true);
    PERFORM add_retention_policy('zone_outage', INTERVAL '90 days');
END
$$;

CREATE INDEX ix_zone_outage_to ON zone_outage (to_utc);
CREATE INDEX ix_zone_outage_open ON zone_outage (zone_key, from_utc) WHERE NOT closed;

REVOKE UPDATE ON zone_outage FROM ariva_runtime;
GRANT UPDATE (to_utc, closed, recorded_on) ON zone_outage TO ariva_runtime;
