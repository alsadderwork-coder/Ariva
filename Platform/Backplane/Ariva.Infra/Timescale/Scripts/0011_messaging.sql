-- 0011 Messaging (ARV-020, ADR-0018): the transactional outbox and the consumer inbox.
--
-- outbox_message: domain events written in the same transaction as the aggregate change. The relay (single leader
-- through an advisory lock) claims unsent rows in seq order, produces them keyed, and stops for a key on failure, so
-- per-key order holds. seq is the insertion order; id is the event id.
CREATE TABLE outbox_message (
    seq              bigint        GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    id               uuid          NOT NULL,
    topic            varchar(120)  NOT NULL CHECK (topic ~ '^(ariva|aman)\.[a-z0-9.-]+$'),
    message_key      varchar(200)  NOT NULL CHECK (length(message_key) > 0),
    message_type     varchar(200)  NOT NULL,
    payload          jsonb         NOT NULL,
    headers          jsonb         NOT NULL DEFAULT '{}'::jsonb,
    created_on       timestamptz   NOT NULL,
    sent_on          timestamptz,
    attempts         integer       NOT NULL DEFAULT 0,
    next_attempt_on  timestamptz,
    last_error       varchar(2000)
);

CREATE UNIQUE INDEX ux_outbox_message_id ON outbox_message (id);
-- The relay's claim: unsent rows in order.
CREATE INDEX ix_outbox_message_unsent ON outbox_message (seq) WHERE sent_on IS NULL;
-- Holding back a key behind an earlier row that is waiting to be retried.
CREATE INDEX ix_outbox_message_unsent_key ON outbox_message (topic, message_key, seq) WHERE sent_on IS NULL;
-- Housekeeping of sent rows.
CREATE INDEX ix_outbox_message_sent ON outbox_message (sent_on) WHERE sent_on IS NOT NULL;

-- processed_event: the inbox. A consumer inserts (consumer, event_id) in the transaction that applies the event; a
-- conflict means the event was applied before and the delivery is skipped. A failed consumer rolls the row back with
-- its changes, so an unapplied event is never marked done.
CREATE TABLE processed_event (
    consumer      varchar(200)  NOT NULL,
    event_id      uuid          NOT NULL,
    processed_on  timestamptz   NOT NULL,
    PRIMARY KEY (consumer, event_id)
);

CREATE INDEX ix_processed_event_processed_on ON processed_event (processed_on);
