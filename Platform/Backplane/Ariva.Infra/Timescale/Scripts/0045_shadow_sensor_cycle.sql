-- 0045 The sensor cycle time of the shadow nowcast (ARV-117b, owner decision 2026-10-07; formulas F8). Until now the
-- shadow's sensor-only desk term (ARV-117a) had n_open from the desks' staff and service zones but no cycle time, so its
-- number was the exit term alone whenever a desk was open. The stream now derives a cycle time for the shadow only: the
-- lane's sensor-only Serving desk minutes (the sensor-only desk engine's minutes, 0044) over the queue's exits in the same window, with
-- bounds and a minimum of exits (Proposed values, docs/product/decisions.md), and stores the value it took beside the
-- shadow it produced, so that the validation comparison (ARV-104f) can tell the minutes that used it from those that
-- fell back:
--
--   queue_minute_shadow.sensor_cycle_minutes   minutes per passenger, null when the shadow took no sensor cycle time
--                                              (too few exits, outside the bounds, Unknown desk time, no sensor desks)
--
-- The published nowcast (queue_minute) never takes it; it is never shown to staff, passengers or rules.
--
-- Privileges (CWE-862, CWE-863), as 0043 for the other value columns: the runtime role may write it (INSERT is granted on
-- the table; UPDATE of the column below, for the stream's upsert, which takes the value from its staging table by key)
-- and never read it (no SELECT on the column, so a SELECT, RETURNING, WHERE or SET over it fails with 42501). The check
-- names this column alone, so it adds no probe beyond 0043's accepted one bit: an UPDATE that sets the column trips the
-- check for its own value only, never for the stored one. ariva_validation_reader reads it through its SELECT on the
-- table. Aggregates only: no desk code, officer, traveller or document identity.
ALTER TABLE queue_minute_shadow ADD COLUMN sensor_cycle_minutes double precision
    CONSTRAINT ck_queue_minute_shadow_cycle CHECK (sensor_cycle_minutes BETWEEN 0.05 AND 60);

GRANT UPDATE (sensor_cycle_minutes) ON queue_minute_shadow TO ariva_runtime;
