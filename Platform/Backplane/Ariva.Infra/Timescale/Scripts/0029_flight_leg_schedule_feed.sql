-- 0029 Schedule ownership (ARV-046): the feed whose message set a leg's schedule fields, and whether that feed is a
-- fallback (an SSIM schedule file). A fallback creates legs and updates only the legs whose schedule a fallback set; a
-- live feed always replaces a fallback's schedule, whatever the message times. Existing legs were all set by live feeds.
ALTER TABLE flight_leg ADD COLUMN schedule_feed varchar(32);
UPDATE flight_leg SET schedule_feed = feed;
ALTER TABLE flight_leg ALTER COLUMN schedule_feed SET NOT NULL;
ALTER TABLE flight_leg ADD CONSTRAINT flight_leg_schedule_feed_check CHECK (schedule_feed ~ '^[a-z0-9][a-z0-9-]{1,31}$');
ALTER TABLE flight_leg ADD COLUMN schedule_fallback boolean NOT NULL DEFAULT false;
