-- 0028 Outbound endpoints (ARV-045, docs/architecture/integration.md, Outbound connections): the systems Ariva calls,
-- registered by administrators, with the networks their addresses must be in, the authentication kind and its
-- non-secret settings, the secret material protected by the Data Protection key ring, resilience settings and, for the
-- ACRIS flight pull, the path, interval and pull state. Disabled, never deleted.
CREATE TABLE outbound_endpoint (
    id                    uuid           PRIMARY KEY,
    code                  varchar(24)    NOT NULL CHECK (code ~ '^[a-z0-9][a-z0-9-]{1,23}$'),
    name                  varchar(100)   NOT NULL CHECK (length(btrim(name)) > 0 AND name !~ '[[:cntrl:]]'),
    purpose               varchar(16)    NOT NULL CHECK (purpose IN ('Generic', 'AcrisFlights')),
    status                varchar(16)    NOT NULL CHECK (status IN ('Active', 'Disabled')),
    site_codes            varchar(600)   NOT NULL CHECK (site_codes ~ '^[A-Z0-9]{2,8}(-[A-Z0-9]{1,8})?( [A-Z0-9]{2,8}(-[A-Z0-9]{1,8})?){0,31}$'),
    base_url              varchar(300)   NOT NULL CHECK (base_url ~ '^https?://[^/?#@[:space:]]+/[^?#[:space:]]*$'),
    allowed_networks      varchar(1000)  NOT NULL CHECK (allowed_networks ~ '^[0-9a-fA-F:.]+/[0-9]{1,3}( [0-9a-fA-F:.]+/[0-9]{1,3}){0,15}$'),
    auth_kind             varchar(32)    NOT NULL CHECK (auth_kind IN ('TotpClientCredentials', 'OAuth2ClientCredentials', 'ApiKeyHeader', 'HmacSignature', 'MutualTls')),
    token_path            varchar(200)   CHECK (token_path ~ '^/[A-Za-z0-9._~/-]{0,199}$'),
    client_id             varchar(128)   CHECK (client_id !~ '[[:space:][:cntrl:]]'),
    scope                 varchar(200)   CHECK (scope !~ '[[:cntrl:]]'),
    header_name           varchar(64)    CHECK (header_name ~ '^[A-Za-z0-9-]{1,64}$'),
    key_id                varchar(64)    CHECK (key_id ~ '^[A-Za-z0-9._:-]{1,64}$'),
    totp_per_request      boolean        NOT NULL,
    pinned_ca_pem         varchar(16384),
    timeout_seconds       integer        NOT NULL CHECK (timeout_seconds BETWEEN 1 AND 60),
    retry_count           integer        NOT NULL CHECK (retry_count BETWEEN 0 AND 5),
    breaker_failures      integer        NOT NULL CHECK (breaker_failures BETWEEN 1 AND 50),
    break_seconds         integer        NOT NULL CHECK (break_seconds BETWEEN 5 AND 600),
    pull_path             varchar(300)   CHECK (pull_path ~ '^/[A-Za-z0-9._~/-]{0,199}(\?[A-Za-z0-9._~=&-]{1,99})?$'),
    poll_seconds          integer        NOT NULL CHECK (poll_seconds = 0 OR poll_seconds BETWEEN 30 AND 3600),
    secret_protected      varchar(200000) NOT NULL,
    has_client_certificate boolean       NOT NULL,
    secret_changed_utc    timestamptz    NOT NULL,
    client_version        integer        NOT NULL CHECK (client_version >= 0),
    last_poll_utc         timestamptz,
    last_modified         varchar(64)    CHECK (last_modified !~ '[[:cntrl:]]'),
    last_status           varchar(200)   CHECK (last_status !~ '[[:cntrl:]]'),
    consecutive_failures  integer        NOT NULL DEFAULT 0 CHECK (consecutive_failures >= 0),
    created_by            varchar(200),
    created_on            timestamptz,
    created_by_id         uuid,
    modified_by           varchar(200),
    modified_on           timestamptz,
    modified_by_id        uuid,
    CHECK ((purpose = 'AcrisFlights') = (pull_path IS NOT NULL)),
    CHECK (purpose <> 'AcrisFlights' OR site_codes !~ ' '),
    CHECK ((auth_kind IN ('TotpClientCredentials', 'OAuth2ClientCredentials')) = (token_path IS NOT NULL AND client_id IS NOT NULL)),
    CHECK ((auth_kind = 'ApiKeyHeader') = (header_name IS NOT NULL)),
    CHECK ((auth_kind = 'HmacSignature') = (key_id IS NOT NULL))
);

CREATE UNIQUE INDEX ux_outbound_endpoint_code ON outbound_endpoint (code);
CREATE INDEX ix_outbound_endpoint_pull ON outbound_endpoint (last_poll_utc) WHERE purpose = 'AcrisFlights' AND status = 'Active';

-- Endpoints are disabled, never deleted (their calls and audit entries stay attributable).
REVOKE DELETE, TRUNCATE ON outbound_endpoint FROM ariva_runtime;
