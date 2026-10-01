-- 0004 Server-side sessions and refresh tokens (ADR-0026, ARV-010b).
-- A session is one sign-in and one refresh token family; the access token's sid is user_session.id.
CREATE TABLE user_session (
    id                      uuid         PRIMARY KEY,
    user_id                 uuid         NOT NULL REFERENCES "user" (id),
    family_id               uuid         NOT NULL,
    started_on              timestamptz  NOT NULL,
    authenticated_on        timestamptz  NOT NULL,
    authentication_methods  varchar(100) NOT NULL,
    last_seen_on            timestamptz  NOT NULL,
    idle_timeout_seconds    integer      NOT NULL CHECK (idle_timeout_seconds > 0),
    idle_expires_on         timestamptz  NOT NULL,
    absolute_expires_on     timestamptz  NOT NULL,
    ip_address              varchar(64),
    user_agent              varchar(512),
    revoked_on              timestamptz,
    revoked_reason          varchar(40)
);

CREATE UNIQUE INDEX ux_user_session_family ON user_session (family_id);
-- Revoking every session of a user (disable, password change, role change) reads only the active ones.
CREATE INDEX ix_user_session_active_user ON user_session (user_id) WHERE revoked_on IS NULL;

-- Tokens are stored as the hex SHA-256 of the value; the value itself never reaches the database.
CREATE TABLE refresh_token (
    id                   uuid        PRIMARY KEY,
    session_id           uuid        NOT NULL REFERENCES user_session (id),
    token_hash           char(64)    NOT NULL,
    issued_on            timestamptz NOT NULL,
    expires_on           timestamptz NOT NULL,
    used_on              timestamptz,
    successor_id         uuid,
    successor_protected  text
);

CREATE UNIQUE INDEX ux_refresh_token_hash ON refresh_token (token_hash);
CREATE INDEX ix_refresh_token_session ON refresh_token (session_id);
CREATE INDEX ix_refresh_token_successor ON refresh_token (successor_id) WHERE successor_protected IS NOT NULL;

-- Sessions are revoked, never deleted by the hosts (they are the sign-in history); retention is a later Cronz job.
REVOKE DELETE, TRUNCATE ON user_session FROM ariva_runtime;
REVOKE DELETE, TRUNCATE ON refresh_token FROM ariva_runtime;
