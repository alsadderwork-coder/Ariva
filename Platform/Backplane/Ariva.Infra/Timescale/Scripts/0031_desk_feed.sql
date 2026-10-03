-- 0031 Desk feed (ARV-049): AMAN's stored records (0030) into the desk state engine (F10) and the e-gate minutes (F12).
-- Ariva.Api.Stream reads each site's new records by the time Ariva received them and keeps, per site, the desk engine's
-- snapshot, its read position and the feed heartbeat in desk_feed_state, written in the same transaction as the
-- desk_minute and egate_minute rows it produced (0018), so a restart continues where the last commit left off.

CREATE TABLE desk_feed_state (
    site_code   varchar(17)  PRIMARY KEY REFERENCES site (code),
    state       jsonb        NOT NULL,
    updated_on  timestamptz  NOT NULL
);

REVOKE DELETE, TRUNCATE ON desk_feed_state FROM ariva_runtime;

-- The feed reads each site's records from a position in receipt time.
CREATE INDEX ix_border_desk_session_received ON border_desk_session (site_code, received_utc);
CREATE INDEX ix_border_desk_interval_received ON border_desk_interval (site_code, received_utc);
CREATE INDEX ix_border_egate_interval_received ON border_egate_interval (site_code, received_utc);

-- The e-gate reject rate (F12) reads a site's recent e-gate intervals by their start.
CREATE INDEX ix_border_egate_interval_start ON border_egate_interval (site_code, interval_start_utc);
