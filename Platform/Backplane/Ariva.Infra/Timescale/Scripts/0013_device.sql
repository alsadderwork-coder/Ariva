-- 0013 Devices and calibrations (ARV-021). A device belongs to the site of its level; its code is unique among the
-- site's live devices. Only the credential's prefix and SHA-256 are stored; a retired or removed device keeps no hash,
-- so it can never authenticate again. Calibrations are evidence (a pass is what lets a device count): never changed,
-- never deleted.
CREATE TABLE device (
    id                       uuid              PRIMARY KEY,
    code                     varchar(16)       NOT NULL CHECK (code ~ '^[A-Z0-9]+(-[A-Z0-9]+)*$'),
    site_code                varchar(17)       NOT NULL REFERENCES site (code),
    family                   varchar(100)      NOT NULL CHECK (family IN ('StereoVision', 'Lidar', 'CameraAnalytics', 'ThermalOrTimeOfFlight', 'Simulator')),
    model                    varchar(100)      NOT NULL,
    transport                varchar(100)      NOT NULL CHECK (transport IN ('HttpsPush', 'Mqtt', 'RestPull', 'WebSocket', 'TcpOrUdp', 'FileDrop', 'OnvifProfileM')),
    dialect                  varchar(100)      NOT NULL CHECK (dialect IN ('Canonical', 'Xovis', 'Declarative')),
    clock_source             varchar(100)      NOT NULL CHECK (clock_source IN ('Ntp', 'Ptp')),
    state                    varchar(100)      NOT NULL CHECK (state IN ('Commissioning', 'Online', 'Degraded', 'Offline', 'Retired')),
    level_id                 uuid              NOT NULL REFERENCES level (id),
    x                        double precision  NOT NULL,
    y                        double precision  NOT NULL,
    mounting_height_metres   double precision  NOT NULL CHECK (mounting_height_metres >= 2 AND mounting_height_metres <= 20),
    orientation_degrees      double precision  NOT NULL CHECK (orientation_degrees >= 0 AND orientation_degrees < 360),
    footprint_length_metres  double precision  CHECK (footprint_length_metres >= 0.1 AND footprint_length_metres <= 60),
    footprint_width_metres   double precision  CHECK (footprint_width_metres >= 0.1 AND footprint_width_metres <= 60),
    footprint_radius_metres  double precision  CHECK (footprint_radius_metres >= 0.1 AND footprint_radius_metres <= 60),
    footprint_source         varchar(100)      NOT NULL CHECK (footprint_source IN ('Vendor', 'AssumedFromBoq')),
    queue_zone_name          varchar(200)      NOT NULL,
    credential_prefix        varchar(13)       CHECK (credential_prefix ~ '^ardk_[A-Za-z0-9_-]{8}$'),
    credential_hash          varchar(64)       CHECK (credential_hash ~ '^[0-9a-f]{64}$'),
    credential_issued_on     timestamptz,
    retired_on               timestamptz,
    created_by               varchar(200),
    created_on               timestamptz,
    created_by_id            uuid,
    modified_by              varchar(200),
    modified_on              timestamptz,
    modified_by_id           uuid,
    deleted_by               varchar(200),
    deleted_on               timestamptz,
    -- A rectangle or a circle, never both or neither.
    CHECK ((footprint_radius_metres IS NULL) = (footprint_length_metres IS NOT NULL AND footprint_width_metres IS NOT NULL)),
    -- Retired and removed devices hold no credential hash.
    CHECK ((state <> 'Retired' AND deleted_on IS NULL) OR credential_hash IS NULL),
    CHECK ((state = 'Retired') = (retired_on IS NOT NULL))
);

CREATE UNIQUE INDEX ux_device_site_code ON device (site_code, code) WHERE deleted_on IS NULL;
CREATE UNIQUE INDEX ux_device_credential_prefix ON device (credential_prefix) WHERE credential_hash IS NOT NULL;
CREATE INDEX ix_device_level ON device (level_id);
CREATE INDEX ix_device_site_zone ON device (site_code, queue_zone_name);

CREATE TABLE device_calibration (
    id                          uuid              PRIMARY KEY,
    device_id                   uuid              NOT NULL REFERENCES device (id),
    site_code                   varchar(17)       NOT NULL REFERENCES site (code),
    method                      varchar(100)      NOT NULL CHECK (method IN ('ManualCountTally', 'ManualCountTwoObservers')),
    sample_size                 integer           NOT NULL CHECK (sample_size BETWEEN 50 AND 5000),
    counting_accuracy_percent   double precision  NOT NULL CHECK (counting_accuracy_percent BETWEEN 0 AND 100),
    wait_time_error_minutes     double precision  NOT NULL CHECK (wait_time_error_minutes BETWEEN 0 AND 30),
    threshold_percent           double precision  NOT NULL CHECK (threshold_percent BETWEEN 50 AND 100),
    passed                      boolean           NOT NULL,
    notes                       varchar(1000),
    performed_on                timestamptz       NOT NULL,
    created_by                  varchar(200),
    created_on                  timestamptz,
    created_by_id               uuid,
    modified_by                 varchar(200),
    modified_on                 timestamptz,
    modified_by_id              uuid,
    -- The verdict follows from the numbers; a pass cannot be written for a failing accuracy.
    CHECK (passed = (counting_accuracy_percent >= threshold_percent))
);

CREATE INDEX ix_device_calibration_device ON device_calibration (device_id, performed_on DESC);

CREATE FUNCTION device_calibration_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'device calibration % is evidence and never changes', OLD.id USING ERRCODE = 'restrict_violation';
END;
$$;

CREATE TRIGGER device_calibration_immutable BEFORE UPDATE OR DELETE ON device_calibration
    FOR EACH ROW EXECUTE FUNCTION device_calibration_immutable();

REVOKE DELETE, TRUNCATE ON device FROM ariva_runtime;
REVOKE UPDATE, DELETE, TRUNCATE ON device_calibration FROM ariva_runtime;
