-- 0036 Daily report and scheduled delivery (ARV-060).
-- queue_minute keeps each minute's wait histogram (F7: 30-second buckets, as bucket indexes and counts), so an hour's
-- or a day's P50 and P90 come from merged histograms, never from averaged percentiles. Minutes written before this
-- script have none; the report marks those hours.
ALTER TABLE queue_minute ADD COLUMN wait_buckets integer[];
ALTER TABLE queue_minute ADD COLUMN wait_counts integer[];
ALTER TABLE queue_minute ADD CONSTRAINT ck_queue_minute_histogram
    CHECK ((wait_buckets IS NULL) = (wait_counts IS NULL)
       AND (wait_buckets IS NULL OR cardinality(wait_buckets) = cardinality(wait_counts)));

-- A schedule sends one site's daily report for the previous local day at a local time, to Ariva accounts only (each
-- recipient gets the report its own roles and sites allow, checked again at every delivery).
CREATE TABLE report_schedule (
    id              uuid          PRIMARY KEY,
    site_code       varchar(17)   NOT NULL REFERENCES site (code),
    name            varchar(120)  NOT NULL CHECK (length(trim(name)) > 0),
    template        varchar(20)   NOT NULL CHECK (template IN ('DailyPeaks')),
    send_at         varchar(5)    NOT NULL CHECK (send_at ~ '^([01][0-9]|2[0-3]):[0-5][0-9]$'),
    enabled         boolean       NOT NULL,
    owner_id        uuid          NOT NULL REFERENCES "user" (id),
    created_by      varchar(200),
    created_on      timestamptz,
    created_by_id   uuid,
    modified_by     varchar(200),
    modified_on     timestamptz,
    modified_by_id  uuid,
    deleted_by      varchar(200),
    deleted_on      timestamptz
);
CREATE INDEX ix_report_schedule_site ON report_schedule (site_code) WHERE deleted_on IS NULL;

CREATE TABLE report_schedule_recipient (
    id           uuid  PRIMARY KEY,
    schedule_id  uuid  NOT NULL REFERENCES report_schedule (id),
    user_id      uuid  NOT NULL REFERENCES "user" (id)
);
CREATE UNIQUE INDEX ux_report_schedule_recipient ON report_schedule_recipient (schedule_id, user_id);

-- One row per schedule, local day and recipient: a run that finds the row already sent does nothing, so the job is
-- idempotent and safe to re-run (ADR-0020).
CREATE TABLE report_delivery (
    id            uuid          PRIMARY KEY,
    schedule_id   uuid          NOT NULL REFERENCES report_schedule (id),
    report_date   date          NOT NULL,
    recipient_id  uuid          NOT NULL REFERENCES "user" (id),
    status        varchar(12)   NOT NULL CHECK (status IN ('Pending', 'Sent', 'Failed', 'Skipped')),
    attempts      integer       NOT NULL DEFAULT 0 CHECK (attempts >= 0),
    reason        varchar(200),
    created_utc   timestamptz   NOT NULL,
    sent_utc      timestamptz
);
CREATE UNIQUE INDEX ux_report_delivery ON report_delivery (schedule_id, report_date, recipient_id);

REVOKE DELETE, TRUNCATE ON report_schedule FROM ariva_runtime;
REVOKE TRUNCATE ON report_schedule_recipient FROM ariva_runtime;
REVOKE DELETE, TRUNCATE ON report_delivery FROM ariva_runtime;
