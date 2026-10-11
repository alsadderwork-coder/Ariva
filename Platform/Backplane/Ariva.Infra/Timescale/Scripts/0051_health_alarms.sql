-- 0051 Health-check alarms and degraded propagation (ARV-114b, formulas F11, F18 and F20; wiki 11 section 8).
--
-- queue_bin gains degraded_by_completion_only: the bin is Degraded for its own track completion alone (F11: final, its track
-- completion below the threshold, Good without it). The validation comparison judges such a bin like a Good one (F18)
-- rather than setting apart exactly the bins whose tracking failed. Rows written before this script read false (none could
-- be: the rule did not exist); the flag is only ever true beside a Final, Degraded bin.
ALTER TABLE queue_bin ADD COLUMN degraded_by_completion_only boolean NOT NULL DEFAULT false;
ALTER TABLE queue_bin ADD CONSTRAINT queue_bin_completion_only_check
    CHECK (NOT degraded_by_completion_only OR (quality = 'Degraded' AND status = 'Final'));

-- Alert rules gain three typed metrics, never expressions, judged per queue zone on its stored continuous checks
-- (zone_health_bin, 0038):
--
--   ConservationResidual   the size of the latest ended bin's conservation residual, in people (threshold up to 100,000)
--   TrackCompletion        the latest final bin's track completion, in percent (threshold up to 100)
--   OccupancyOutOfRange    the latest ended bin's minutes outside 0 to the physical capacity, in minutes (up to 1,440)
--
-- The checks that list the metrics are replaced by the same checks with the three new names and their units, so no
-- statement, the runtime role's included, can store a rule the evaluator cannot apply or an alert of an unknown metric.
-- alert_rule's checks are named (0021); alert's list of metrics was left to PostgreSQL in 0021, so it is found by its
-- definition. The three metrics are numbers: they take a comparator and a threshold, never IsTrue, a minimum queue or a
-- lead time (the existing checks already say so for every metric that is not a condition, the nowcast or the prediction).
ALTER TABLE alert_rule DROP CONSTRAINT alert_rule_metric_names;
ALTER TABLE alert_rule ADD CONSTRAINT alert_rule_metric_names
    CHECK (metric IN ('Nowcast', 'BinP90', 'QueueLength', 'OverflowOccupied', 'SensorOffline', 'DesksBelowPlan', 'PredictedNowcast',
                      'ConservationResidual', 'TrackCompletion', 'OccupancyOutOfRange'));

ALTER TABLE alert_rule DROP CONSTRAINT alert_rule_threshold_unit;
ALTER TABLE alert_rule ADD CONSTRAINT alert_rule_threshold_unit
    CHECK (threshold IS NULL OR threshold <= CASE metric WHEN 'Nowcast' THEN 600 WHEN 'BinP90' THEN 600 WHEN 'PredictedNowcast' THEN 600
                                                         WHEN 'QueueLength' THEN 100000 WHEN 'ConservationResidual' THEN 100000
                                                         WHEN 'DesksBelowPlan' THEN 1000 WHEN 'TrackCompletion' THEN 100
                                                         WHEN 'OccupancyOutOfRange' THEN 1440 ELSE 0 END);

ALTER TABLE alert_rule DROP CONSTRAINT alert_rule_clear_side;
ALTER TABLE alert_rule ADD CONSTRAINT alert_rule_clear_side
    CHECK (clear_threshold IS NULL
           OR (comparator IN ('GreaterThan', 'GreaterOrEqual') AND clear_threshold < threshold)
           OR (comparator IN ('LessThan', 'LessOrEqual') AND clear_threshold > threshold
               AND clear_threshold <= CASE metric WHEN 'Nowcast' THEN 600 WHEN 'BinP90' THEN 600 WHEN 'PredictedNowcast' THEN 600
                                                  WHEN 'QueueLength' THEN 100000 WHEN 'ConservationResidual' THEN 100000
                                                  WHEN 'DesksBelowPlan' THEN 1000 WHEN 'TrackCompletion' THEN 100
                                                  WHEN 'OccupancyOutOfRange' THEN 1440 ELSE 0 END));

DO $$
DECLARE
    c record;
BEGIN
    FOR c IN SELECT conname FROM pg_constraint
             WHERE conrelid = 'public.alert'::regclass AND contype = 'c' AND pg_get_constraintdef(oid) LIKE '%PredictedNowcast%'
    LOOP
        EXECUTE format('ALTER TABLE public.alert DROP CONSTRAINT %I', c.conname);
    END LOOP;
END
$$;

ALTER TABLE alert ADD CONSTRAINT alert_metric_names
    CHECK (metric IN ('Nowcast', 'BinP90', 'QueueLength', 'OverflowOccupied', 'SensorOffline', 'DesksBelowPlan', 'PredictedNowcast',
                      'ConservationResidual', 'TrackCompletion', 'OccupancyOutOfRange'));
