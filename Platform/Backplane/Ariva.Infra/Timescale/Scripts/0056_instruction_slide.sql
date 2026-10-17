-- 0056 Instruction slides of the passenger displays (ARV-141): short multilingual plain-text slides per site, with a
-- built-in icon, a time on screen, a scope (the whole site, queue zones or displays, named in a JSON array) and an order.
-- Their words are JSON text, never markup; the service checks them and the players show them as text. A retired slide is
-- kept and shown nowhere; the runtime role cannot delete a row.

CREATE TABLE instruction_slide (
    id                uuid           PRIMARY KEY,
    site_code         varchar(17)    NOT NULL REFERENCES site (code),
    position          integer        NOT NULL CHECK (position BETWEEN 1 AND 1000),
    scope             varchar(16)    NOT NULL CHECK (scope IN ('Site', 'Zone', 'Display')),
    targets           varchar(4000)  NOT NULL CHECK (left(targets, 1) = '['),
    icon              varchar(32)    NOT NULL CHECK (icon IN ('Information', 'Passport', 'EGate', 'HatAndGlasses', 'Fingerprint', 'Family',
                                                              'Accessibility', 'Baggage', 'NoPhone', 'FollowSigns')),
    duration_seconds  integer        NOT NULL CHECK (duration_seconds BETWEEN 5 AND 60),
    pinned            boolean        NOT NULL,
    status            varchar(16)    NOT NULL CHECK (status IN ('Active', 'Retired')),
    texts             varchar(8000)  NOT NULL CHECK (left(texts, 1) = '{' AND texts <> '{}'),
    created_by        varchar(200),
    created_on        timestamptz,
    created_by_id     uuid,
    modified_by       varchar(200),
    modified_on       timestamptz,
    modified_by_id    uuid,
    CHECK ((scope = 'Site') = (targets = '[]'))
);
CREATE INDEX ix_instruction_slide_site ON instruction_slide (site_code, position);

REVOKE DELETE, TRUNCATE ON instruction_slide FROM ariva_runtime;
