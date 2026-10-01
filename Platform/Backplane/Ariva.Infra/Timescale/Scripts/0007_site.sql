-- 0007 Sites and site access (ARV-012).
-- A site is the unit of data access: users (and later integration clients) are bound to site codes, and every
-- site-bound table carries site_code. Codes never change; sites are not deleted.
CREATE TABLE site (
    id              uuid          PRIMARY KEY,
    code            varchar(17)   NOT NULL CHECK (code ~ '^[A-Z0-9]{2,8}(-[A-Z0-9]{1,8})?$'),
    name            varchar(200)  NOT NULL,
    created_by      varchar(200),
    created_on      timestamptz,
    created_by_id   uuid,
    modified_by     varchar(200),
    modified_on     timestamptz,
    modified_by_id  uuid
);

CREATE UNIQUE INDEX ux_site_code ON site (code);

REVOKE DELETE, TRUNCATE ON site FROM ariva_runtime;

-- Deployment-wide access (administrators, national views); otherwise the rows in user_site.
ALTER TABLE "user" ADD COLUMN all_sites boolean NOT NULL DEFAULT false;

CREATE TABLE user_site (
    id             uuid         PRIMARY KEY,
    user_id        uuid         NOT NULL REFERENCES "user" (id),
    site_code      varchar(17)  NOT NULL REFERENCES site (code),
    granted_by_id  uuid,
    granted_on     timestamptz
);

CREATE UNIQUE INDEX ux_user_site_user_site ON user_site (user_id, site_code);
