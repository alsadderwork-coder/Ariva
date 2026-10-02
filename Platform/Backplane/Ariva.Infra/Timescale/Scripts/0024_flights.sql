-- 0024 Flights and allocations (ARV-041, Flight Demand context): flight legs keyed by the feed's stable flight key per
-- site, the milestones feeds report (append-only), check-in counter allocations of departing legs, and the freshness of
-- each flight feed (the stale-feed alarm, runbook 4.3). Feeds arrive out of order: every value keeps the time of the
-- message that set it. Flight data only; no passenger or crew data (docs/domain/data-boundary.md).
CREATE TABLE flight_leg (
    id                         uuid          PRIMARY KEY,
    site_code                  varchar(17)   NOT NULL REFERENCES site (code),
    flight_key                 varchar(64)   NOT NULL CHECK (flight_key ~ '^[A-Za-z0-9][A-Za-z0-9._:-]{0,63}$'),
    direction                  varchar(16)   NOT NULL CHECK (direction IN ('Arrival', 'Departure')),
    carrier                    varchar(3)    NOT NULL CHECK (carrier ~ '^([A-Z0-9]{2}|[A-Z]{3})$'),
    number                     varchar(4)    NOT NULL CHECK (number ~ '^[0-9]{1,4}$'),
    suffix                     varchar(1)    CHECK (suffix ~ '^[A-Z]$'),
    scheduled_utc              timestamptz   NOT NULL,
    origin                     varchar(3)    CHECK (origin ~ '^[A-Z]{3}$'),
    destination                varchar(3)    CHECK (destination ~ '^[A-Z]{3}$'),
    terminal                   varchar(16)   CHECK (terminal ~ '^[A-Z0-9][A-Z0-9._/-]{0,15}$'),
    stand                      varchar(16)   CHECK (stand ~ '^[A-Z0-9][A-Z0-9._/-]{0,15}$'),
    gate                       varchar(16)   CHECK (gate ~ '^[A-Z0-9][A-Z0-9._/-]{0,15}$'),
    aircraft_type              varchar(4)    CHECK (aircraft_type ~ '^[A-Z0-9]{2,4}$'),
    seats                      integer       CHECK (seats BETWEEN 0 AND 1000),
    pax_estimate               integer       CHECK (pax_estimate BETWEEN 0 AND 1000),
    codeshare_codes            varchar(200)  CHECK (codeshare_codes ~ '^(([A-Z0-9]{2}|[A-Z]{3})[0-9]{1,4}[A-Z]?)( ([A-Z0-9]{2}|[A-Z]{3})[0-9]{1,4}[A-Z]?){0,19}$'),
    schedule_source_utc        timestamptz   NOT NULL,
    estimated_utc              timestamptz,
    estimated_source_utc       timestamptz,
    actual_utc                 timestamptz,
    actual_source_utc          timestamptz,
    on_block_utc               timestamptz,
    on_block_source_utc        timestamptz,
    gate_open_utc              timestamptz,
    gate_open_source_utc       timestamptz,
    boarding_start_utc         timestamptz,
    boarding_start_source_utc  timestamptz,
    off_block_utc              timestamptz,
    off_block_source_utc       timestamptz,
    cancelled                  boolean       NOT NULL,
    cancelled_source_utc       timestamptz,
    diverted                   boolean       NOT NULL,
    diverted_source_utc        timestamptz,
    status                     varchar(16)   NOT NULL CHECK (status IN ('Scheduled', 'Estimated', 'Landed', 'OnBlock', 'GateOpen', 'Boarding', 'OffBlock', 'Cancelled', 'Diverted')),
    feed                       varchar(32)   NOT NULL CHECK (feed ~ '^[a-z0-9][a-z0-9-]{1,31}$'),
    updated_utc                timestamptz   NOT NULL,
    -- A milestone has a value exactly when it has the time of the message that set it.
    CHECK ((estimated_utc IS NULL) = (estimated_source_utc IS NULL) AND (actual_utc IS NULL) = (actual_source_utc IS NULL)
       AND (on_block_utc IS NULL) = (on_block_source_utc IS NULL) AND (gate_open_utc IS NULL) = (gate_open_source_utc IS NULL)
       AND (boarding_start_utc IS NULL) = (boarding_start_source_utc IS NULL) AND (off_block_utc IS NULL) = (off_block_source_utc IS NULL)),
    -- Milestones of the other direction never appear.
    CHECK (direction = 'Arrival' OR on_block_utc IS NULL),
    CHECK (direction = 'Departure' OR (gate_open_utc IS NULL AND boarding_start_utc IS NULL AND off_block_utc IS NULL)),
    CHECK (NOT cancelled OR cancelled_source_utc IS NOT NULL),
    CHECK (NOT diverted OR diverted_source_utc IS NOT NULL)
);

CREATE UNIQUE INDEX ux_flight_leg_key ON flight_leg (site_code, flight_key);
-- Flights due at a site (the stale-feed sweep) and the arrival wave (ARV-047) read by expected time.
CREATE INDEX ix_flight_leg_expected ON flight_leg (site_code, (COALESCE(estimated_utc, scheduled_utc)));
-- Legs are updated, never removed by the runtime role (their events and allocations refer to them).
REVOKE DELETE, TRUNCATE ON flight_leg FROM ariva_runtime;

-- The record of the milestones feeds reported, applied or not: written once.
CREATE TABLE flight_event (
    id             uuid          PRIMARY KEY,
    flight_leg_id  uuid          NOT NULL REFERENCES flight_leg (id),
    site_code      varchar(17)   NOT NULL REFERENCES site (code),
    flight_key     varchar(64)   NOT NULL,
    type           varchar(16)   NOT NULL CHECK (type IN ('Estimated', 'Landed', 'OnBlock', 'GateOpen', 'BoardingStart', 'OffBlock', 'Cancelled', 'Diverted')),
    time_utc       timestamptz   NOT NULL,
    feed           varchar(32)   NOT NULL CHECK (feed ~ '^[a-z0-9][a-z0-9-]{1,31}$'),
    source_utc     timestamptz   NOT NULL,
    received_utc   timestamptz   NOT NULL,
    applied        boolean       NOT NULL
);

CREATE INDEX ix_flight_event_leg ON flight_event (flight_leg_id, received_utc);
REVOKE UPDATE, DELETE, TRUNCATE ON flight_event FROM ariva_runtime;

-- Check-in counters of a departing leg at one checkpoint; codes no AODB mapping resolves are kept apart.
CREATE TABLE counter_allocation (
    id                uuid          PRIMARY KEY,
    flight_leg_id     uuid          NOT NULL REFERENCES flight_leg (id),
    site_code         varchar(17)   NOT NULL REFERENCES site (code),
    flight_key        varchar(64)   NOT NULL,
    checkpoint_code   varchar(16)   NOT NULL CHECK (checkpoint_code ~ '^[A-Z0-9]+(-[A-Z0-9]+)*$'),
    desk_codes        varchar(1700) CHECK (desk_codes ~ '^[A-Z0-9]+(-[A-Z0-9]+)*( [A-Z0-9]+(-[A-Z0-9]+)*)*$'),
    unresolved_codes  varchar(3300) CHECK (unresolved_codes ~ '^[A-Z0-9][A-Z0-9._/-]{0,31}( [A-Z0-9][A-Z0-9._/-]{0,31})*$'),
    open_utc          timestamptz   NOT NULL,
    close_utc         timestamptz   NOT NULL,
    handler_code      varchar(8)    CHECK (handler_code ~ '^[A-Z0-9]{2,8}$'),
    feed              varchar(32)   NOT NULL CHECK (feed ~ '^[a-z0-9][a-z0-9-]{1,31}$'),
    source_utc        timestamptz   NOT NULL,
    updated_utc       timestamptz   NOT NULL,
    CHECK (close_utc > open_utc AND close_utc - open_utc <= interval '24 hours'),
    CHECK (desk_codes IS NOT NULL OR unresolved_codes IS NOT NULL)
);

CREATE UNIQUE INDEX ux_counter_allocation_leg ON counter_allocation (flight_leg_id, checkpoint_code);
-- Allocations are replaced, never removed by the runtime role.
REVOKE DELETE, TRUNCATE ON counter_allocation FROM ariva_runtime;

-- When each feed of a site was last heard from, and the stale-feed rule's verdict at the last sweep.
CREATE TABLE feed_freshness (
    id                uuid          PRIMARY KEY,
    site_code         varchar(17)   NOT NULL REFERENCES site (code),
    feed              varchar(32)   NOT NULL CHECK (feed ~ '^[a-z0-9][a-z0-9-]{1,31}$'),
    last_message_utc  timestamptz   NOT NULL,
    state             varchar(16)   NOT NULL CHECK (state IN ('Fresh', 'Idle', 'Stale')),
    state_since_utc   timestamptz   NOT NULL,
    flights_due       integer       NOT NULL CHECK (flights_due >= 0),
    swept_utc         timestamptz
);

CREATE UNIQUE INDEX ux_feed_freshness_feed ON feed_freshness (site_code, feed);
REVOKE DELETE, TRUNCATE ON feed_freshness FROM ariva_runtime;
