-- 0061 The wait at which a display's advertising gives way to instructions (ARV-143b):
--
--   display.advertising_pause_minutes   1 to 240 minutes, or null for no such rule. When the nowcast of any queue zone the
--                                       display shows reaches it, the player shows instructions in the second region until
--                                       every one is below it by the display's hysteresis. Kept, unused, while the display
--                                       shows no advertising.

ALTER TABLE display ADD COLUMN advertising_pause_minutes integer
    CONSTRAINT ck_display_advertising_pause CHECK (advertising_pause_minutes BETWEEN 1 AND 240);
