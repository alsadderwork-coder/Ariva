-- 0009 Desk code mappings (ARV-015): another system's code for an Ariva desk. Unique per system and site among live
-- mappings, one live mapping per desk and system. Soft deleted, like the topology.
CREATE TABLE desk_code_mapping (
    id              uuid          PRIMARY KEY,
    system          varchar(100)  NOT NULL CHECK (system IN ('Aman', 'Aodb')),
    external_code   varchar(32)   NOT NULL CHECK (external_code ~ '^[A-Z0-9][A-Z0-9._/-]{0,31}$'),
    desk_id         uuid          NOT NULL REFERENCES desk (id),
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

CREATE UNIQUE INDEX ux_desk_code_mapping_code ON desk_code_mapping (system, site_code, external_code) WHERE deleted_on IS NULL;
CREATE UNIQUE INDEX ux_desk_code_mapping_desk ON desk_code_mapping (system, desk_id) WHERE deleted_on IS NULL;

REVOKE DELETE, TRUNCATE ON desk_code_mapping FROM ariva_runtime;
