-- 0035 Passenger displays (ARV-058): a board per display, its entries (queue zones with a label per language) and the
-- neutral messages for stale data as JSON, its band, hysteresis and stale threshold, and the player's credential as a
-- prefix and a SHA-256 (never the credential). Soft deleted; the runtime role cannot delete a row.

CREATE TABLE display (
    id                    uuid              PRIMARY KEY,
    site_code             varchar(17)       NOT NULL REFERENCES site (code),
    code                  varchar(16)       NOT NULL CHECK (code ~ '^[A-Z0-9]+(-[A-Z0-9]+)*$'),
    name                  varchar(200)      NOT NULL CHECK (length(trim(name)) > 0),
    location              varchar(200),
    orientation           varchar(16)       NOT NULL CHECK (orientation IN ('Landscape', 'Portrait')),
    languages             varchar(16)       NOT NULL CHECK (languages ~ '^(en|ar|pt|sw)(,(en|ar|pt|sw)){0,3}$'),
    band_minutes          integer           NOT NULL CHECK (band_minutes BETWEEN 1 AND 30),
    hysteresis_minutes    double precision  NOT NULL CHECK (hysteresis_minutes >= 0 AND hysteresis_minutes < band_minutes),
    stale_seconds         integer           NOT NULL CHECK (stale_seconds BETWEEN 60 AND 1800),
    entries               varchar(16000)    NOT NULL CHECK (left(entries, 1) = '['),
    fallback_messages     varchar(4000)     NOT NULL CHECK (left(fallback_messages, 1) = '{'),
    enabled               boolean           NOT NULL,
    credential_prefix     varchar(13)       CHECK (credential_prefix ~ '^ardp_[A-Za-z0-9_-]{8}$'),
    credential_hash       char(64)          CHECK (credential_hash ~ '^[0-9a-f]{64}$'),
    credential_issued_on  timestamptz,
    created_by            varchar(200),
    created_on            timestamptz,
    created_by_id         uuid,
    modified_by           varchar(200),
    modified_on           timestamptz,
    modified_by_id        uuid,
    deleted_by            varchar(200),
    deleted_on            timestamptz,
    CHECK ((credential_prefix IS NULL) = (credential_hash IS NULL))
);
CREATE UNIQUE INDEX ux_display_code ON display (code) WHERE deleted_on IS NULL;
CREATE INDEX ix_display_site ON display (site_code);
CREATE INDEX ix_display_credential_prefix ON display (credential_prefix) WHERE deleted_on IS NULL;

REVOKE DELETE, TRUNCATE ON display FROM ariva_runtime;
