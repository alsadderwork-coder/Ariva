-- 0062 Proof of play (ARV-144): one row per advertisement a passenger display played.
--
--   advertising_play  when the play started (UTC), the display's site, the display, the advertisement and the playlist it
--                     was scheduled from, how long it was on screen in milliseconds (1,000 to 125,000: at least a second,
--                     at most a slot's ceiling of 120 seconds and the player's timing), whether it played its whole slot
--                     (completed) or was cut short, and when the server took it.
--
-- Written by Ariva.Api.Main from the display players' batches (binary COPY into a staging table, then INSERT ... ON CONFLICT
-- DO NOTHING): a play is keyed by its display, advertisement and start, so a batch sent twice (the player retries when it
-- hears no answer) counts once. The server takes only plays of advertisements on the playlist that applied to the display
-- when the play started, that kept their slot and overlap no other play of the display. The runtime role inserts and reads; it cannot update, delete or truncate a row. No passenger data
-- of any kind, and nothing about who was near the screen. Kept 400 days by default (a year's campaigns and their
-- comparison), a migration parameter per environment (Database:Retention:AdvertisingPlayDays, ARV-111a).
CREATE TABLE advertising_play (
    started_utc   timestamptz  NOT NULL,
    site_code     varchar(17)  NOT NULL,
    display_id    uuid         NOT NULL,
    media_id      uuid         NOT NULL,
    playlist_id   uuid         NOT NULL,
    duration_ms   integer      NOT NULL CHECK (duration_ms BETWEEN 1000 AND 125000),
    completed     boolean      NOT NULL,
    received_utc  timestamptz  NOT NULL,
    PRIMARY KEY (display_id, media_id, started_utc),
    CHECK (received_utc >= started_utc - INTERVAL '5 seconds'),
    CHECK (received_utc <= started_utc + INTERVAL '1 day')
);
CREATE INDEX ix_advertising_play_site ON advertising_play (site_code, started_utc);
-- A display's plays by time: what a new batch is checked against for overlaps.
CREATE INDEX ix_advertising_play_display ON advertising_play (display_id, started_utc);

REVOKE UPDATE, DELETE, TRUNCATE ON advertising_play FROM ariva_runtime;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'timescaledb') THEN
        IF coalesce(current_setting('ariva.allow_plain_postgres', true), '') <> 'on' THEN
            RAISE EXCEPTION 'TimescaleDB is required: advertising_play is a hypertable with a retention policy';
        END IF;
        RAISE NOTICE 'TimescaleDB is not available (ariva.allow_plain_postgres): advertising_play stays an ordinary table without retention';
        RETURN;
    END IF;
    CREATE EXTENSION IF NOT EXISTS timescaledb;
    PERFORM create_hypertable('advertising_play', 'started_utc', chunk_time_interval => INTERVAL '7 days');
    PERFORM add_retention_policy('advertising_play', INTERVAL '400 days');
END
$$;
