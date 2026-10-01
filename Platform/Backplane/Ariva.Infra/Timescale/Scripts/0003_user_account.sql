-- 0003 Local user accounts and role grants (ADR-0026, ARV-010a).
-- Column names follow the NHibernate conventions (snake_case; "user" is quoted because it is a reserved word).
CREATE TABLE "user" (
    id                    uuid         PRIMARY KEY,
    user_name             varchar(64)  NOT NULL,
    display_name          varchar(200),
    email                 varchar(320),
    password_hash         varchar(100) NOT NULL,
    password_salt         varchar(100) NOT NULL,
    password_algorithm    varchar(30)  NOT NULL,
    password_iterations   integer      NOT NULL CHECK (password_iterations > 0),
    must_change_password  boolean      NOT NULL DEFAULT false,
    totp_enrolled         boolean      NOT NULL DEFAULT false,
    is_disabled           boolean      NOT NULL DEFAULT false,
    failed_login_count    integer      NOT NULL DEFAULT 0,
    locked_until          timestamptz,
    last_login_on         timestamptz,
    created_by            varchar(200),
    created_on            timestamptz,
    created_by_id         uuid,
    modified_by           varchar(200),
    modified_on           timestamptz,
    modified_by_id        uuid
);

-- Usernames are stored normalised (NFKC, lower case), so a plain unique index enforces one account per name.
CREATE UNIQUE INDEX ux_user_user_name ON "user" (user_name);

CREATE TABLE user_role (
    id         uuid        PRIMARY KEY,
    user_id    uuid        NOT NULL REFERENCES "user" (id),
    role_code  varchar(64) NOT NULL
);

CREATE UNIQUE INDEX ux_user_role_user_role ON user_role (user_id, role_code);

-- Accounts are disabled, never deleted (audit history keeps pointing at them).
REVOKE DELETE, TRUNCATE ON "user" FROM ariva_runtime;
