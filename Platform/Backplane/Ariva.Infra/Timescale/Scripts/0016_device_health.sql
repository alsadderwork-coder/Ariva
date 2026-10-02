-- 0016 Device health (ARV-025). A device's heartbeat is the last health report Ariva received for it (one row per
-- device, replaced by newer reports only). A queue zone's health follows the states of its commissioned devices:
-- Healthy, Degraded while any is offline or degraded, Unmonitored when it has none.
CREATE TABLE device_heartbeat (
    device_id                  uuid              PRIMARY KEY REFERENCES device (id),
    site_code                  varchar(17)       NOT NULL REFERENCES site (code),
    last_seen_on               timestamptz       NOT NULL,
    reported_online            boolean           NOT NULL,
    frame_rate                 double precision  CHECK (frame_rate >= 0 AND frame_rate <= 1000),
    temperature_celsius        double precision  CHECK (temperature_celsius >= -100 AND temperature_celsius <= 200),
    clock_offset_milliseconds  double precision  CHECK (clock_offset_milliseconds >= -86400000 AND clock_offset_milliseconds <= 86400000),
    clock_state                varchar(16)       CHECK (clock_state IN ('Ok', 'Corrected', 'Unreliable'))
);

CREATE INDEX ix_device_heartbeat_last_seen ON device_heartbeat (last_seen_on);

CREATE TABLE zone_health (
    id                uuid          PRIMARY KEY,
    site_code         varchar(17)   NOT NULL REFERENCES site (code),
    queue_zone_name   varchar(200)  NOT NULL,
    state             varchar(100)  NOT NULL CHECK (state IN ('Healthy', 'Degraded', 'Unmonitored')),
    devices           integer       NOT NULL CHECK (devices >= 0),
    devices_offline   integer       NOT NULL CHECK (devices_offline >= 0),
    devices_degraded  integer       NOT NULL CHECK (devices_degraded >= 0),
    changed_on        timestamptz   NOT NULL,
    CHECK (devices_offline + devices_degraded <= devices),
    CHECK ((state = 'Unmonitored') = (devices = 0)),
    CHECK ((state = 'Degraded') = (devices_offline + devices_degraded > 0))
);

CREATE UNIQUE INDEX ux_zone_health_zone ON zone_health (site_code, queue_zone_name);
