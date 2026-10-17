-- 0042 Availability ledger per operating minute (ARV-118, formulas F18). The pilot criterion "availability 99 percent of
-- operating hours" (D6) needs the operating hours of each site and a record, per site and minute, of whether Ariva was
-- measuring. Five tables:
--
--   operating_week           a version of a site's weekly hours (site local time), in force from a local day; the
--                            hours are canonical JSON ([{"day":"Monday","opens":"06:00","closes":"22:00"}, ...]).
--   operating_day_exception  a dated exception: the hours that open on one local day instead of the week's ([] closed).
--   maintenance_window       an announced maintenance window [starts_utc, ends_utc) in whole minutes.
--   availability_minute      the ledger: one row per site and UTC minute, written once by Ariva.Api.Cronz.
--   availability_cursor      per site, the next minute the ledger's catch-up fills from (minutes still in their live
--                            window are written first; the cursor never moves backwards).
--
-- Calendar entries are configuration (NHibernate, audited, soft deleted). Each carries recorded_utc, the server time it
-- was recorded: an entry counts only when recorded before it takes effect (a week or an exception before its local day
-- starts, a maintenance window before it starts; a window recorded at or after its start never counts), so a minute's
-- calendar state is fixed before the minute and a catch-up computes what the live run would have.
--
-- availability_minute: calendar is the minute's calendar state (Operating, Closed, Maintenance), local_date the site's
-- local date of the minute, state Available, Unavailable or Unobserved, reasons the names of what made it not available
-- (StaleZone, MissingMinute, StreamLag, NoPublishedZones, NotObservedLive) with the number of zones behind each, the
-- profile version whose queue zones were expected, the rule version and when the minute was decided. Written by binary
-- COPY into a staging table and INSERT ... ON CONFLICT DO NOTHING under a per-site advisory lock (class 50): the first
-- decision of a minute stands; the runtime role cannot update, delete or truncate a row (the ledger is evidence, kept
-- with no retention policy). Aggregates only: no officer, traveller or document identity anywhere here.
CREATE TABLE operating_week (
    id              uuid           PRIMARY KEY,
    site_code       varchar(17)    NOT NULL REFERENCES site (code),
    effective_from  varchar(10)    NOT NULL CHECK (effective_from ~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$'),
    hours           varchar(4000)  NOT NULL CHECK (left(hours, 1) = '['),
    recorded_utc    timestamptz    NOT NULL,
    created_by      varchar(200),
    created_on      timestamptz,
    created_by_id   uuid,
    modified_by     varchar(200),
    modified_on     timestamptz,
    modified_by_id  uuid,
    deleted_by      varchar(200),
    deleted_on      timestamptz
);
CREATE UNIQUE INDEX ux_operating_week_site_from ON operating_week (site_code, effective_from) WHERE deleted_on IS NULL;

CREATE TABLE operating_day_exception (
    id              uuid           PRIMARY KEY,
    site_code       varchar(17)    NOT NULL REFERENCES site (code),
    date            varchar(10)    NOT NULL CHECK (date ~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$'),
    hours           varchar(4000)  NOT NULL CHECK (left(hours, 1) = '['),
    reason          varchar(200)   NOT NULL CHECK (length(trim(reason)) > 0),
    recorded_utc    timestamptz    NOT NULL,
    created_by      varchar(200),
    created_on      timestamptz,
    created_by_id   uuid,
    modified_by     varchar(200),
    modified_on     timestamptz,
    modified_by_id  uuid,
    deleted_by      varchar(200),
    deleted_on      timestamptz
);
CREATE UNIQUE INDEX ux_operating_day_exception_site_date ON operating_day_exception (site_code, date) WHERE deleted_on IS NULL;

CREATE TABLE maintenance_window (
    id              uuid           PRIMARY KEY,
    site_code       varchar(17)    NOT NULL REFERENCES site (code),
    starts_utc      timestamptz    NOT NULL,
    ends_utc        timestamptz    NOT NULL,
    reason          varchar(200)   NOT NULL CHECK (length(trim(reason)) > 0),
    recorded_utc    timestamptz    NOT NULL,
    created_by      varchar(200),
    created_on      timestamptz,
    created_by_id   uuid,
    modified_by     varchar(200),
    modified_on     timestamptz,
    modified_by_id  uuid,
    deleted_by      varchar(200),
    deleted_on      timestamptz,
    CHECK (ends_utc > starts_utc AND ends_utc - starts_utc <= INTERVAL '7 days'),
    CHECK (date_trunc('minute', starts_utc) = starts_utc AND date_trunc('minute', ends_utc) = ends_utc),
    -- Recorded before it starts, whoever writes the row: a window cannot excuse an outage once it has begun.
    CHECK (recorded_utc < starts_utc)
);
CREATE INDEX ix_maintenance_window_site_starts ON maintenance_window (site_code, starts_utc);

CREATE TABLE availability_minute (
    site_code              varchar(17)   NOT NULL,
    minute_utc             timestamptz   NOT NULL CHECK (date_trunc('minute', minute_utc) = minute_utc),
    local_date             date          NOT NULL,
    calendar               varchar(12)   NOT NULL CHECK (calendar IN ('Operating', 'Closed', 'Maintenance')),
    state                  varchar(12)   NOT NULL CHECK (state IN ('Available', 'Unavailable', 'Unobserved')),
    reasons                text[]        NOT NULL DEFAULT '{}'
        CHECK (reasons <@ ARRAY['StaleZone', 'MissingMinute', 'StreamLag', 'NoPublishedZones', 'NotObservedLive']::text[]),
    zones_expected         smallint      NOT NULL CHECK (zones_expected >= 0),
    zones_stale            smallint      NOT NULL CHECK (zones_stale >= 0),
    zones_missing          smallint      NOT NULL CHECK (zones_missing >= 0),
    zones_lagging          smallint      NOT NULL CHECK (zones_lagging >= 0),
    profile_version        integer       CHECK (profile_version >= 1),
    rule_version           smallint      NOT NULL CHECK (rule_version >= 1),
    decided_utc            timestamptz   NOT NULL,
    PRIMARY KEY (site_code, minute_utc),
    CHECK ((state = 'Available') = (cardinality(reasons) = 0)),
    CHECK (zones_stale <= zones_expected AND zones_missing <= zones_expected AND zones_lagging <= zones_expected),
    CHECK (decided_utc > minute_utc)
);

CREATE TABLE availability_cursor (
    site_code        varchar(17)   PRIMARY KEY REFERENCES site (code),
    next_minute_utc  timestamptz   NOT NULL CHECK (date_trunc('minute', next_minute_utc) = next_minute_utc),
    updated_on       timestamptz   NOT NULL
);

-- Calendar entries are soft deleted (the runtime role sets deleted_on); the ledger is written once. Accepted residual
-- risk: the runtime role keeps UPDATE on the calendar tables (soft delete needs it), so a compromised runtime could alter
-- an entry's columns; the ledger has fixed every minute already written, so this reaches only minutes not yet written.
REVOKE DELETE, TRUNCATE ON operating_week, operating_day_exception, maintenance_window FROM ariva_runtime;
REVOKE UPDATE, DELETE, TRUNCATE ON availability_minute FROM ariva_runtime;
REVOKE DELETE, TRUNCATE ON availability_cursor FROM ariva_runtime;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'timescaledb') THEN
        IF coalesce(current_setting('ariva.allow_plain_postgres', true), '') <> 'on' THEN
            RAISE EXCEPTION 'TimescaleDB is required: availability_minute is a hypertable';
        END IF;
        RAISE NOTICE 'TimescaleDB is not available (ariva.allow_plain_postgres): availability_minute stays an ordinary table';
        RETURN;
    END IF;
    CREATE EXTENSION IF NOT EXISTS timescaledb;
    PERFORM create_hypertable('availability_minute', 'minute_utc', chunk_time_interval => INTERVAL '30 days');
END
$$;
