-- 0005 TOTP, recovery codes and the break-glass account (ADR-0026, ARV-010c).
ALTER TABLE "user"
    ADD COLUMN totp_secret_protected text,
    ADD COLUMN totp_last_step        bigint,
    ADD COLUMN is_break_glass        boolean NOT NULL DEFAULT false;

-- One break-glass account per deployment.
CREATE UNIQUE INDEX ux_user_break_glass ON "user" (is_break_glass) WHERE is_break_glass;

CREATE TABLE recovery_code (
    id         uuid        PRIMARY KEY,
    user_id    uuid        NOT NULL REFERENCES "user" (id),
    code_hash  char(64)    NOT NULL,
    issued_on  timestamptz NOT NULL,
    used_on    timestamptz
);

CREATE UNIQUE INDEX ux_recovery_code_user_hash ON recovery_code (user_id, code_hash);

-- Regeneration marks a user's remaining codes used; the hosts never delete them.
REVOKE DELETE, TRUNCATE ON recovery_code FROM ariva_runtime;
