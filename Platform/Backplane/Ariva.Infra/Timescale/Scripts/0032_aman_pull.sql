-- 0032 AMAN pull (ARV-050): an outbound endpoint may pull AMAN's feed from its Integration API for one site where AMAN's
-- Kafka is not shared (purpose AmanFeed, TOTP client credentials with a code on every call only), and the pull keeps its position in each of the
-- four contracts (AMAN's feed sequence) in aman_pull_cursor, written in the same transaction as the records it applied.

ALTER TABLE outbound_endpoint DROP CONSTRAINT outbound_endpoint_purpose_check;
ALTER TABLE outbound_endpoint ADD CONSTRAINT outbound_endpoint_purpose_check CHECK (purpose IN ('Generic', 'AcrisFlights', 'AmanFeed'));

-- A pull path exactly for the pulls, and a pull feeds one site.
ALTER TABLE outbound_endpoint DROP CONSTRAINT outbound_endpoint_check;
ALTER TABLE outbound_endpoint ADD CONSTRAINT outbound_endpoint_pull_path_purpose_check CHECK ((purpose IN ('AcrisFlights', 'AmanFeed')) = (pull_path IS NOT NULL));
ALTER TABLE outbound_endpoint DROP CONSTRAINT outbound_endpoint_check1;
ALTER TABLE outbound_endpoint ADD CONSTRAINT outbound_endpoint_pull_site_check CHECK (purpose NOT IN ('AcrisFlights', 'AmanFeed') OR site_codes !~ ' ');

-- AMAN's Integration API authenticates with TOTP client credentials.
ALTER TABLE outbound_endpoint ADD CONSTRAINT outbound_endpoint_aman_auth_check CHECK (purpose <> 'AmanFeed' OR (auth_kind = 'TotpClientCredentials' AND totp_per_request));
-- AMAN's feed path: below the endpoint's base, ending in a slash (the contract names follow it), no query.
ALTER TABLE outbound_endpoint ADD CONSTRAINT outbound_endpoint_aman_path_check CHECK (purpose <> 'AmanFeed' OR (pull_path ~ '/$' AND pull_path !~ '[?]'));

DROP INDEX ix_outbound_endpoint_pull;
CREATE INDEX ix_outbound_endpoint_pull ON outbound_endpoint (purpose, last_poll_utc) WHERE status = 'Active' AND pull_path IS NOT NULL;

CREATE TABLE aman_pull_cursor (
    endpoint_id     uuid         NOT NULL REFERENCES outbound_endpoint (id),
    contract        varchar(32)  NOT NULL CHECK (contract IN ('desk-sessions', 'desk-interval-stats', 'egate-interval-stats', 'inbound-lane-demand')),
    after_sequence  bigint       NOT NULL CHECK (after_sequence >= 0),
    updated_on      timestamptz  NOT NULL,
    PRIMARY KEY (endpoint_id, contract)
);

REVOKE DELETE, TRUNCATE ON aman_pull_cursor FROM ariva_runtime;
