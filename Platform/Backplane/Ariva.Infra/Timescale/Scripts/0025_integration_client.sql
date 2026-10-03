-- 0025 Integration clients (ARV-042, docs/architecture/integration.md): the systems that call the Integration API, with
-- a PBKDF2 hash of their secret, their TOTP seed protected by the Data Protection key ring, their scopes, bound sites and
-- allowed networks, the TOTP replay guard and the lockout; and the record of every Integration API call.
CREATE TABLE integration_client (
    id                        uuid          PRIMARY KEY,
    client_id                 varchar(29)   NOT NULL CHECK (client_id ~ '^ic_[a-z2-7]{26}$'),
    name                      varchar(100)  NOT NULL CHECK (length(btrim(name)) > 0 AND name !~ '[[:cntrl:]]'),
    kind                      varchar(16)   NOT NULL CHECK (kind IN ('Aodb', 'Immigration', 'SensorGateway', 'Other')),
    status                    varchar(16)   NOT NULL CHECK (status IN ('Active', 'Disabled')),
    scope_names               varchar(200)  NOT NULL CHECK (scope_names ~ '^(flights:write|allocations:write|immigration:write|sensing:write|queues:read|displays:read)( (flights:write|allocations:write|immigration:write|sensing:write|queues:read|displays:read)){0,5}$'),
    site_codes                varchar(600)  NOT NULL CHECK (site_codes ~ '^[A-Z0-9]{2,8}(-[A-Z0-9]{1,8})?( [A-Z0-9]{2,8}(-[A-Z0-9]{1,8})?){0,31}$'),
    allowed_networks          varchar(1000) NOT NULL CHECK (allowed_networks ~ '^[0-9a-fA-F:.]+/[0-9]{1,3}( [0-9a-fA-F:.]+/[0-9]{1,3}){0,15}$'),
    require_totp_per_request  boolean       NOT NULL,
    secret_algorithm          varchar(32)   NOT NULL,
    secret_iterations         integer       NOT NULL CHECK (secret_iterations >= 600000),
    secret_salt               varchar(64)   NOT NULL,
    secret_hash               varchar(64)   NOT NULL,
    secret_changed_utc        timestamptz   NOT NULL,
    totp_secret_protected     varchar(2000) NOT NULL,
    totp_last_step            bigint,
    totp_changed_utc          timestamptz   NOT NULL,
    failed_attempts           integer       NOT NULL DEFAULT 0 CHECK (failed_attempts >= 0),
    locked_until_utc          timestamptz,
    attempt_window_utc        timestamptz,
    attempts_in_window        integer       NOT NULL DEFAULT 0 CHECK (attempts_in_window >= 0),
    tokens_valid_from_utc     timestamptz   NOT NULL,
    token_version             integer       NOT NULL CHECK (token_version >= 0),
    last_token_utc            timestamptz,
    created_by                varchar(200),
    created_on                timestamptz,
    created_by_id             uuid,
    modified_by               varchar(200),
    modified_on               timestamptz,
    modified_by_id            uuid
);

CREATE UNIQUE INDEX ux_integration_client_client_id ON integration_client (client_id);
-- Clients are disabled, never deleted (their calls stay attributable).
REVOKE DELETE, TRUNCATE ON integration_client FROM ariva_runtime;

-- Every Integration API call: client, session, scope, endpoint, site, answer, payload SHA-256. Written once.
CREATE TABLE integration_call (
    id              uuid          PRIMARY KEY,
    client_id       varchar(29)   NOT NULL,
    session_id      uuid,
    scope           varchar(32),
    method          varchar(8)    NOT NULL,
    route           varchar(200)  NOT NULL,
    site_code       varchar(17),
    status          integer       NOT NULL CHECK (status BETWEEN 100 AND 599),
    payload_sha256  char(64)      CHECK (payload_sha256 ~ '^[0-9a-f]{64}$'),
    payload_bytes   bigint        NOT NULL CHECK (payload_bytes >= 0),
    remote_address  varchar(64),
    at_utc          timestamptz   NOT NULL
);

CREATE INDEX ix_integration_call_client ON integration_call (client_id, at_utc DESC);
REVOKE UPDATE, DELETE, TRUNCATE ON integration_call FROM ariva_runtime;
