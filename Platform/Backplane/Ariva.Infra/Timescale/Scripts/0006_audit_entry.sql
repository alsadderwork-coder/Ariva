-- 0006 Audit trail and role grant provenance (ARV-011).
-- audit_entry is append-only for the runtime login: no UPDATE, DELETE or TRUNCATE, so neither the API nor a bug in
-- it can rewrite history. Summaries never hold secrets.
CREATE TABLE audit_entry (
    id              uuid          PRIMARY KEY,
    occurred_on     timestamptz   NOT NULL,
    actor_id        uuid,
    actor_name      varchar(200),
    action          varchar(64)   NOT NULL,
    target_type     varchar(64)   NOT NULL,
    target_id       uuid,
    target_name     varchar(200),
    before_summary  varchar(2000),
    after_summary   varchar(2000),
    ip_address      varchar(64),
    trace_id        varchar(64)
);

CREATE INDEX ix_audit_entry_occurred_on ON audit_entry (occurred_on DESC, id DESC);
CREATE INDEX ix_audit_entry_target ON audit_entry (target_id, occurred_on DESC);
CREATE INDEX ix_audit_entry_actor ON audit_entry (actor_id, occurred_on DESC);

REVOKE UPDATE, DELETE, TRUNCATE ON audit_entry FROM ariva_runtime;

-- Who granted a role and when; null for grants made by the development seed and the installer command.
ALTER TABLE user_role ADD COLUMN granted_by_id uuid, ADD COLUMN granted_on timestamptz;
