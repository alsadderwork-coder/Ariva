-- 0034 Zone lane (ARV-057): a queue zone may say which lane category it is the queue of (CIT, RES, VIS, CRW, EG and
-- any other code a site configures), so the immigration screen shows a hall's waits per lane. Null for queues that
-- serve no lane (check-in, security). Not part of the geometry hash.

ALTER TABLE zone ADD COLUMN lane_category varchar(4) CHECK (lane_category ~ '^[A-Z]{2,4}$');
ALTER TABLE zone ADD CONSTRAINT zone_lane_queue_check CHECK (lane_category IS NULL OR kind = 'Queue');
