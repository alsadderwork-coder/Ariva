-- 0023 Email notifications (ARV-040): the emails Ariva sends, written in the transaction of the alert change they tell
-- of and sent by Ariva.Api.Integration. One email per alert, kind and recipient; held back (Suppressed) past the rate limits;
-- given up (Failed) after its attempts. The runtime role marks them but never rewrites or deletes them: they are the
-- record of who was told what, and when.
CREATE TABLE email_message (
    id                uuid           PRIMARY KEY,
    kind              varchar(32)    NOT NULL CHECK (kind IN ('AlertRaised', 'AlertEscalated')),
    alert_id          uuid           NOT NULL REFERENCES alert (id),
    site_code         varchar(17)    NOT NULL REFERENCES site (code),
    recipient         varchar(254)   NOT NULL CHECK (recipient ~ '^[^@\s<>()",;:\\]+@[^@\s<>()",;:\\]+$'),
    subject           varchar(200)   NOT NULL CHECK (subject !~ '[\u0000-\u001F\u007F]'),
    body              varchar(8000)  NOT NULL CHECK (length(body) > 0),
    status            varchar(16)    NOT NULL CHECK (status IN ('Pending', 'Sent', 'Suppressed', 'Failed')),
    attempts          integer        NOT NULL CHECK (attempts BETWEEN 0 AND 100),
    created_utc       timestamptz    NOT NULL,
    next_attempt_utc  timestamptz    NOT NULL,
    sent_utc          timestamptz,
    reason            varchar(500),
    CHECK ((status = 'Sent') = (sent_utc IS NOT NULL))
);

-- Addresses compare without case, as the hosts compare them.
CREATE UNIQUE INDEX ux_email_message_once ON email_message (alert_id, kind, lower(recipient));
CREATE INDEX ix_email_message_due ON email_message (created_utc) WHERE status = 'Pending';
CREATE INDEX ix_email_message_recipient ON email_message (lower(recipient), created_utc);

-- What an email says and to whom is fixed when it is written: the runtime role inserts a row and afterwards changes
-- only how sending went (CWE-501: a row rewritten through another host could otherwise send any text to any address
-- from Ariva's sender). It never deletes one.
REVOKE UPDATE, DELETE, TRUNCATE ON email_message FROM ariva_runtime;
GRANT UPDATE (status, attempts, next_attempt_utc, sent_utc, reason) ON email_message TO ariva_runtime;

-- Sent, Suppressed and Failed are final; attempts only grow.
CREATE OR REPLACE FUNCTION email_message_keep() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF (NEW.id, NEW.kind, NEW.alert_id, NEW.site_code, NEW.recipient, NEW.subject, NEW.body, NEW.created_utc)
       IS DISTINCT FROM (OLD.id, OLD.kind, OLD.alert_id, OLD.site_code, OLD.recipient, OLD.subject, OLD.body, OLD.created_utc) THEN
        RAISE EXCEPTION 'an email keeps what it says and to whom' USING ERRCODE = 'check_violation';
    END IF;
    IF OLD.status <> 'Pending' AND (NEW.status, NEW.attempts, NEW.sent_utc) IS DISTINCT FROM (OLD.status, OLD.attempts, OLD.sent_utc) THEN
        RAISE EXCEPTION 'an email that was % stays so', OLD.status USING ERRCODE = 'check_violation';
    END IF;
    IF NEW.attempts < OLD.attempts THEN
        RAISE EXCEPTION 'attempts only grow' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END
$$;

ALTER FUNCTION email_message_keep() SET search_path = pg_catalog, public, pg_temp;

CREATE TRIGGER email_message_keep BEFORE UPDATE ON email_message FOR EACH ROW EXECUTE FUNCTION email_message_keep();
