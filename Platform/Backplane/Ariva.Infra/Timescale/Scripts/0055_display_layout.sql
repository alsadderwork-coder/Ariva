-- 0055 A passenger display's layout (ARV-140): Full (the queue view fills the screen, as every display so far) or Split,
-- with the queue region's share of the screen and what the second region shows. Every existing display stays Full.
--
--   display.layout          Full or Split
--   display.queue_share     the queue region's share of a split screen, 50 to 75 percent (kept, unused, by a full display)
--   display.second_region   what the second region shows: Instructions. Advertising waits for the site's advertising
--                           policy (ARV-145, off by default), whose script widens ck_display_second_region; until then the
--                           database refuses it as the entity does.
ALTER TABLE display ADD COLUMN layout varchar(16) NOT NULL DEFAULT 'Full'
    CONSTRAINT ck_display_layout CHECK (layout IN ('Full', 'Split'));
ALTER TABLE display ADD COLUMN queue_share integer NOT NULL DEFAULT 60
    CONSTRAINT ck_display_queue_share CHECK (queue_share BETWEEN 50 AND 75);
ALTER TABLE display ADD COLUMN second_region varchar(16) NOT NULL DEFAULT 'Instructions'
    CONSTRAINT ck_display_second_region CHECK (second_region IN ('Instructions'));
