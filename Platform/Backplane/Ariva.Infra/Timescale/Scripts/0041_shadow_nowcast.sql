-- 0041 Shadow nowcast without AMAN inputs (ARV-117, formulas F8). The pilot's ground-truth proof compares the nowcast
-- error with and without AMAN inputs. The desk term is live only and never replayed, so the comparison cannot be rebuilt
-- later: Ariva.Api.Stream computes, for every live queue minute, a second nowcast with Nowcast.Compute from the same queue
-- length and exits and the sensor-only desk term (n_open from the desks' staff and service zones alone, no cycle time;
-- no AMAN session, AMAN cycle time or AMAN reject rate), and writes it in the same queue_minute row as the published
-- nowcast, in the same checkpoint transaction.
--
--   shadow_nowcast_minutes    the shadow W_now in minutes, null when there is no service (as nowcast_minutes)
--   shadow_no_service         why there is none (NoServiceReason names), null when there is a number
--   shadow_nowcast_degraded   the shadow's F11 flag (true when it rests on the exit term alone or an Unknown desk)
--
-- The shadow is never shown and never an input: the live snapshot, the hub, displays, alert inputs and reports do not
-- read these columns (architecture tests in Ariva.UnitTests). Only the validation comparison (ARV-104f) will read them.
-- Minutes written before this script, and rows written only by the minute results, have none. Retention as
-- queue_minute (evidence, no retention policy). Aggregates only: no officer, traveller or document identity.
ALTER TABLE queue_minute ADD COLUMN shadow_nowcast_minutes double precision CHECK (shadow_nowcast_minutes >= 0);
ALTER TABLE queue_minute ADD COLUMN shadow_no_service varchar(20)
    CHECK (shadow_no_service IN ('NothingOpen', 'ThroughputTooLow', 'NoThroughputData', 'NoQueueLength', 'Implausible'));
ALTER TABLE queue_minute ADD COLUMN shadow_nowcast_degraded boolean;
ALTER TABLE queue_minute ADD CONSTRAINT ck_queue_minute_shadow
    CHECK (shadow_nowcast_minutes IS NULL OR shadow_no_service IS NULL);
-- A shadow (a number or a reason) always carries its F11 flag, and a flag never stands alone: a row without a shadow has
-- none of the three. Nowcast.Compute always returns a number or a reason (never neither), so the stream writes either all
-- of a shadow or none of it.
ALTER TABLE queue_minute ADD CONSTRAINT ck_queue_minute_shadow_flag
    CHECK ((shadow_nowcast_minutes IS NULL AND shadow_no_service IS NULL) = (shadow_nowcast_degraded IS NULL));
