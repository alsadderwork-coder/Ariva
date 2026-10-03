-- 0030 AMAN feed and immigration endpoints (ARV-048): the four V1 contracts as Ariva received them, from AMAN's Kafka
-- topics or the immigration REST endpoints. Codes are the border system's own; desk_id is the Ariva desk its AMAN desk
-- code mapping resolves to, or null while the code is unmapped (kept apart, never guessed). Each record is unique per site
-- by its source event id, so a redelivery or the same record over both transports is stored once. No person, document or
-- officer identifier exists in any of these tables. Intervals are one minute; times are UTC.

CREATE TABLE border_desk_session (
    id                uuid          PRIMARY KEY,
    site_code         varchar(17)   NOT NULL REFERENCES site (code),
    desk_code         varchar(32)   NOT NULL CHECK (desk_code ~ '^[A-Z0-9][A-Z0-9._/-]{0,31}$'),
    desk_id           uuid          REFERENCES desk (id),
    state             varchar(8)    NOT NULL CHECK (state IN ('Opened', 'Closed', 'Paused')),
    lane_category     varchar(3)    CHECK (lane_category IN ('CIT', 'RES', 'VIS', 'CRW')),
    occurred_utc      timestamptz   NOT NULL,
    feed              varchar(32)   NOT NULL CHECK (feed ~ '^[a-z0-9][a-z0-9-]{1,31}$'),
    source_event_id   varchar(64)   NOT NULL CHECK (source_event_id ~ '^[A-Za-z0-9][A-Za-z0-9._:-]{0,63}$'),
    received_utc      timestamptz   NOT NULL,
    UNIQUE (site_code, source_event_id),
    CHECK ((state = 'Closed') = (lane_category IS NULL))
);
CREATE INDEX ix_border_desk_session_desk ON border_desk_session (site_code, desk_code, occurred_utc DESC);

CREATE TABLE border_desk_interval (
    id                      uuid           PRIMARY KEY,
    site_code               varchar(17)    NOT NULL REFERENCES site (code),
    desk_code               varchar(32)    NOT NULL CHECK (desk_code ~ '^[A-Z0-9][A-Z0-9._/-]{0,31}$'),
    desk_id                 uuid           REFERENCES desk (id),
    interval_start_utc      timestamptz    NOT NULL CHECK (date_trunc('minute', interval_start_utc) = interval_start_utc),
    transactions_processed  integer        NOT NULL CHECK (transactions_processed BETWEEN 0 AND 10000),
    documents_processed     integer        NOT NULL CHECK (documents_processed BETWEEN 0 AND 10000),
    mean_service_seconds    double precision NOT NULL CHECK (mean_service_seconds BETWEEN 0 AND 3600),
    p90_service_seconds     double precision NOT NULL CHECK (p90_service_seconds BETWEEN 0 AND 3600),
    mean_cycle_seconds      double precision NOT NULL CHECK (mean_cycle_seconds BETWEEN 0 AND 3600),
    lane_category           varchar(3)     NOT NULL CHECK (lane_category IN ('CIT', 'RES', 'VIS', 'CRW')),
    feed                    varchar(32)    NOT NULL CHECK (feed ~ '^[a-z0-9][a-z0-9-]{1,31}$'),
    source_event_id         varchar(64)    NOT NULL CHECK (source_event_id ~ '^[A-Za-z0-9][A-Za-z0-9._:-]{0,63}$'),
    received_utc            timestamptz    NOT NULL,
    UNIQUE (site_code, source_event_id),
    CHECK (transactions_processed <= documents_processed AND (documents_processed = 0) = (transactions_processed = 0))
);
CREATE INDEX ix_border_desk_interval_desk ON border_desk_interval (site_code, desk_code, interval_start_utc DESC);

CREATE TABLE border_egate_interval (
    id                          uuid           PRIMARY KEY,
    site_code                   varchar(17)    NOT NULL REFERENCES site (code),
    gate_code                   varchar(32)    NOT NULL CHECK (gate_code ~ '^[A-Z0-9][A-Z0-9._/-]{0,31}$'),
    desk_id                     uuid           REFERENCES desk (id),
    interval_start_utc          timestamptz    NOT NULL CHECK (date_trunc('minute', interval_start_utc) = interval_start_utc),
    attempts                    integer        NOT NULL CHECK (attempts BETWEEN 0 AND 10000),
    accepted                    integer        NOT NULL CHECK (accepted >= 0),
    rejected                    integer        NOT NULL CHECK (rejected >= 0),
    rejects_other               integer        NOT NULL CHECK (rejects_other >= 0),
    rejects_document_read       integer        NOT NULL CHECK (rejects_document_read = 0 OR rejects_document_read >= 3),
    rejects_biometric_capture   integer        NOT NULL CHECK (rejects_biometric_capture = 0 OR rejects_biometric_capture >= 3),
    rejects_eligibility         integer        NOT NULL CHECK (rejects_eligibility = 0 OR rejects_eligibility >= 3),
    rejects_referred_to_officer integer        NOT NULL CHECK (rejects_referred_to_officer = 0 OR rejects_referred_to_officer >= 3),
    rejects_technical           integer        NOT NULL CHECK (rejects_technical = 0 OR rejects_technical >= 3),
    mean_cycle_seconds          double precision NOT NULL CHECK (mean_cycle_seconds BETWEEN 0 AND 3600),
    feed                        varchar(32)    NOT NULL CHECK (feed ~ '^[a-z0-9][a-z0-9-]{1,31}$'),
    source_event_id             varchar(64)    NOT NULL CHECK (source_event_id ~ '^[A-Za-z0-9][A-Za-z0-9._:-]{0,63}$'),
    received_utc                timestamptz    NOT NULL,
    UNIQUE (site_code, source_event_id),
    CHECK (accepted + rejected = attempts),
    CHECK (rejects_other + rejects_document_read + rejects_biometric_capture + rejects_eligibility + rejects_referred_to_officer + rejects_technical = rejected)
);
CREATE INDEX ix_border_egate_interval_gate ON border_egate_interval (site_code, gate_code, interval_start_utc DESC);

-- The latest computation of a flight's lane demand; an older one never replaces a newer one.
CREATE TABLE inbound_lane_demand (
    id                     uuid          PRIMARY KEY,
    site_code              varchar(17)   NOT NULL REFERENCES site (code),
    flight_key             varchar(64)   NOT NULL CHECK (flight_key ~ '^[A-Za-z0-9][A-Za-z0-9._:-]{0,63}$'),
    scheduled_arrival_utc  timestamptz   NOT NULL,
    boarded_total          integer       NOT NULL CHECK (boarded_total BETWEEN 0 AND 1000),
    cit                    integer       NOT NULL CHECK (cit BETWEEN 0 AND 1000),
    res                    integer       NOT NULL CHECK (res BETWEEN 0 AND 1000),
    vis                    integer       NOT NULL CHECK (vis BETWEEN 0 AND 1000),
    crw                    integer       NOT NULL CHECK (crw BETWEEN 0 AND 1000),
    egate_eligible         integer       NOT NULL CHECK (egate_eligible BETWEEN 0 AND 1000),
    computed_utc           timestamptz   NOT NULL,
    feed                   varchar(32)   NOT NULL CHECK (feed ~ '^[a-z0-9][a-z0-9-]{1,31}$'),
    source_event_id        varchar(64)   NOT NULL CHECK (source_event_id ~ '^[A-Za-z0-9][A-Za-z0-9._:-]{0,63}$'),
    received_utc           timestamptz   NOT NULL,
    UNIQUE (site_code, flight_key),
    CHECK (cit + res + vis + crw + egate_eligible <= boarded_total)
);

-- Records are written once (lane demand is replaced by a newer computation); the runtime role never deletes them.
REVOKE UPDATE, DELETE, TRUNCATE ON border_desk_session FROM ariva_runtime;
REVOKE UPDATE, DELETE, TRUNCATE ON border_desk_interval FROM ariva_runtime;
REVOKE UPDATE, DELETE, TRUNCATE ON border_egate_interval FROM ariva_runtime;
REVOKE DELETE, TRUNCATE ON inbound_lane_demand FROM ariva_runtime;

-- The immigration batches are claimed like the other Integration API batches (scripts 0026, 0027).
ALTER TABLE integration_idempotency DROP CONSTRAINT integration_idempotency_operation_check;
ALTER TABLE integration_idempotency ADD CONSTRAINT integration_idempotency_operation_check
    CHECK (operation IN ('flights.legs', 'flights.events', 'allocations', 'aodb.aidx', 'immigration.desk-sessions', 'immigration.desk-interval-stats',
                         'immigration.egate-interval-stats', 'immigration.inbound-lane-demand'));
