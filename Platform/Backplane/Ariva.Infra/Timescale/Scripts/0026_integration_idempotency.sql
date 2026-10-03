-- 0026 Idempotency of Integration API batches (ARV-043): a client names each batch with an Idempotency-Key; the first
-- call with a key claims it in the transaction that applies the batch and stores the answer, so a retry with the same key
-- and the same request gets the same answer without applying anything twice, and a retry with another request under the
-- same key is refused. A concurrent retry waits on the claim (the primary key) until the first call commits or rolls
-- back. Keys are kept for a day, then swept.
CREATE TABLE integration_idempotency (
    client_id        varchar(29)   NOT NULL CHECK (client_id ~ '^ic_[a-z2-7]{26}$'),
    idempotency_key  varchar(64)   NOT NULL CHECK (idempotency_key ~ '^[A-Za-z0-9][A-Za-z0-9._:-]{7,63}$'),
    operation        varchar(32)   NOT NULL CHECK (operation IN ('flights.legs', 'flights.events', 'allocations')),
    site_code        varchar(17)   NOT NULL REFERENCES site (code),
    request_sha256   char(64)      NOT NULL CHECK (request_sha256 ~ '^[0-9a-f]{64}$'),
    created_utc      timestamptz   NOT NULL,
    expires_utc      timestamptz   NOT NULL,
    status_code      integer       CHECK (status_code BETWEEN 200 AND 599),
    response_body    text          CHECK (length(response_body) <= 4194304),
    PRIMARY KEY (client_id, idempotency_key),
    CHECK (expires_utc > created_utc),
    CHECK ((status_code IS NULL) = (response_body IS NULL))
);

CREATE INDEX ix_integration_idempotency_expires ON integration_idempotency (expires_utc);

-- The runtime role claims a key, stores its answer once and deletes expired keys; it never rewrites a claim or an
-- answer (a rewritten answer would be replayed to the client as what Ariva did) and never deletes a live key (that
-- would let a retry apply the batch again).
REVOKE UPDATE, TRUNCATE ON integration_idempotency FROM ariva_runtime;
GRANT UPDATE (status_code, response_body) ON integration_idempotency TO ariva_runtime;

CREATE OR REPLACE FUNCTION integration_idempotency_keep() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        -- Five minutes of grace for the hosts' clocks, which decide when a key expired.
        IF OLD.expires_utc > now() + interval '5 minutes' THEN
            RAISE EXCEPTION 'an idempotency key is kept until it expires' USING ERRCODE = 'check_violation';
        END IF;
        RETURN OLD;
    END IF;
    IF OLD.status_code IS NOT NULL THEN
        RAISE EXCEPTION 'an idempotency key keeps its answer' USING ERRCODE = 'check_violation';
    END IF;
    IF (NEW.client_id, NEW.idempotency_key, NEW.operation, NEW.site_code, NEW.request_sha256, NEW.created_utc, NEW.expires_utc)
       IS DISTINCT FROM (OLD.client_id, OLD.idempotency_key, OLD.operation, OLD.site_code, OLD.request_sha256, OLD.created_utc, OLD.expires_utc) THEN
        RAISE EXCEPTION 'an idempotency key keeps its claim' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END
$$;

ALTER FUNCTION integration_idempotency_keep() SET search_path = pg_catalog, public, pg_temp;

CREATE TRIGGER integration_idempotency_keep BEFORE UPDATE OR DELETE ON integration_idempotency FOR EACH ROW EXECUTE FUNCTION integration_idempotency_keep();
