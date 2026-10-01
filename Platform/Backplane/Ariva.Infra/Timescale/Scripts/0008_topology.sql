-- 0008 Site topology: airport, terminal, level, checkpoint, desk (ARV-013, ARV-014).
-- Records are soft deleted (deleted_on) so history and zone profiles keep their references; codes are unique among
-- the live children of one parent. Every table below the airport carries site_code from its terminal (ARV-012).
CREATE TABLE airport (
    id              uuid          PRIMARY KEY,
    iata_code       varchar(3)    NOT NULL CHECK (iata_code ~ '^[A-Z]{3}$'),
    icao_code       varchar(4)    CHECK (icao_code ~ '^[A-Z]{4}$'),
    name            varchar(200)  NOT NULL,
    time_zone_id    varchar(64)   NOT NULL,
    created_by      varchar(200),
    created_on      timestamptz,
    created_by_id   uuid,
    modified_by     varchar(200),
    modified_on     timestamptz,
    modified_by_id  uuid,
    deleted_by      varchar(200),
    deleted_on      timestamptz
);
CREATE UNIQUE INDEX ux_airport_iata_code ON airport (iata_code) WHERE deleted_on IS NULL;

CREATE TABLE terminal (
    id              uuid          PRIMARY KEY,
    airport_id      uuid          NOT NULL REFERENCES airport (id),
    code            varchar(16)   NOT NULL CHECK (code ~ '^[A-Z0-9]+(-[A-Z0-9]+)*$'),
    name            varchar(200)  NOT NULL,
    site_code       varchar(17)   NOT NULL REFERENCES site (code),
    created_by      varchar(200),
    created_on      timestamptz,
    created_by_id   uuid,
    modified_by     varchar(200),
    modified_on     timestamptz,
    modified_by_id  uuid,
    deleted_by      varchar(200),
    deleted_on      timestamptz
);
CREATE UNIQUE INDEX ux_terminal_airport_code ON terminal (airport_id, code) WHERE deleted_on IS NULL;
CREATE INDEX ix_terminal_site_code ON terminal (site_code);

CREATE TABLE level (
    id              uuid              PRIMARY KEY,
    terminal_id     uuid              NOT NULL REFERENCES terminal (id),
    site_code       varchar(17)       NOT NULL REFERENCES site (code),
    code            varchar(16)       NOT NULL CHECK (code ~ '^[A-Z0-9]+(-[A-Z0-9]+)*$'),
    name            varchar(200)      NOT NULL,
    floor_number    integer           NOT NULL CHECK (floor_number BETWEEN -10 AND 50),
    width_metres    double precision  NOT NULL CHECK (width_metres > 0 AND width_metres <= 2000),
    depth_metres    double precision  NOT NULL CHECK (depth_metres > 0 AND depth_metres <= 2000),
    created_by      varchar(200),
    created_on      timestamptz,
    created_by_id   uuid,
    modified_by     varchar(200),
    modified_on     timestamptz,
    modified_by_id  uuid,
    deleted_by      varchar(200),
    deleted_on      timestamptz
);
CREATE UNIQUE INDEX ux_level_terminal_code ON level (terminal_id, code) WHERE deleted_on IS NULL;
CREATE UNIQUE INDEX ux_level_terminal_floor ON level (terminal_id, floor_number) WHERE deleted_on IS NULL;
CREATE INDEX ix_level_site_code ON level (site_code);

CREATE TABLE checkpoint (
    id              uuid          PRIMARY KEY,
    level_id        uuid          NOT NULL REFERENCES level (id),
    site_code       varchar(17)   NOT NULL REFERENCES site (code),
    code            varchar(16)   NOT NULL CHECK (code ~ '^[A-Z0-9]+(-[A-Z0-9]+)*$'),
    name            varchar(200)  NOT NULL,
    kind            varchar(100)  NOT NULL CHECK (kind IN ('CheckIn', 'Security', 'Emigration', 'Immigration')),
    created_by      varchar(200),
    created_on      timestamptz,
    created_by_id   uuid,
    modified_by     varchar(200),
    modified_on     timestamptz,
    modified_by_id  uuid,
    deleted_by      varchar(200),
    deleted_on      timestamptz
);
CREATE UNIQUE INDEX ux_checkpoint_level_code ON checkpoint (level_id, code) WHERE deleted_on IS NULL;
CREATE INDEX ix_checkpoint_site_code ON checkpoint (site_code);

CREATE TABLE desk (
    id                   uuid          PRIMARY KEY,
    checkpoint_id        uuid          NOT NULL REFERENCES checkpoint (id),
    site_code            varchar(17)   NOT NULL REFERENCES site (code),
    code                 varchar(16)   NOT NULL CHECK (code ~ '^[A-Z0-9]+(-[A-Z0-9]+)*$'),
    name                 varchar(200)  NOT NULL,
    kind                 varchar(100)  NOT NULL CHECK (kind IN ('Counter', 'SecurityLane', 'Desk', 'EGate')),
    lane_category_codes  varchar(64),
    in_service           boolean       NOT NULL DEFAULT true,
    created_by           varchar(200),
    created_on           timestamptz,
    created_by_id        uuid,
    modified_by          varchar(200),
    modified_on          timestamptz,
    modified_by_id       uuid,
    deleted_by           varchar(200),
    deleted_on           timestamptz
);
CREATE UNIQUE INDEX ux_desk_checkpoint_code ON desk (checkpoint_id, code) WHERE deleted_on IS NULL;
CREATE INDEX ix_desk_site_code ON desk (site_code);

-- Soft delete only: the application never removes topology rows.
REVOKE DELETE, TRUNCATE ON airport, terminal, level, checkpoint, desk FROM ariva_runtime;
