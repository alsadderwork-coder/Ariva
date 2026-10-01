-- 0010 Floor plans (ARV-018): one live plan per level; the file lives in file storage under storage_key, which Ariva
-- generates. Soft deleted when replaced, so earlier calibrations stay readable.
CREATE TABLE floor_plan (
    id                  uuid              PRIMARY KEY,
    level_id            uuid              NOT NULL REFERENCES level (id),
    site_code           varchar(17)       NOT NULL REFERENCES site (code),
    storage_key         varchar(40)       NOT NULL CHECK (storage_key ~ '^[0-9a-f]{32}\.(png|jpg|svg)$'),
    content_type        varchar(32)       NOT NULL CHECK (content_type IN ('image/png', 'image/jpeg', 'image/svg+xml')),
    size_bytes          bigint            NOT NULL CHECK (size_bytes > 0 AND size_bytes <= 20971520),
    sha256              varchar(64)       NOT NULL,
    original_file_name  varchar(200),
    width_pixels        integer,
    height_pixels       integer,
    metres_per_pixel    double precision  NOT NULL CHECK (metres_per_pixel > 0 AND metres_per_pixel <= 10),
    origin_x            double precision  NOT NULL,
    origin_y            double precision  NOT NULL,
    created_by          varchar(200),
    created_on          timestamptz,
    created_by_id       uuid,
    modified_by         varchar(200),
    modified_on         timestamptz,
    modified_by_id      uuid,
    deleted_by          varchar(200),
    deleted_on          timestamptz
);

CREATE UNIQUE INDEX ux_floor_plan_level ON floor_plan (level_id) WHERE deleted_on IS NULL;
CREATE UNIQUE INDEX ux_floor_plan_storage_key ON floor_plan (storage_key);

REVOKE DELETE, TRUNCATE ON floor_plan FROM ariva_runtime;
