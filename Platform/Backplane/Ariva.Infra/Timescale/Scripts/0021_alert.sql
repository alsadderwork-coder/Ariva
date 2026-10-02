-- 0021 Alert evaluation (ARV-038).
--   alert_rule        gains the predicted nowcast (a breach projected 15 to 60 minutes ahead from the arrival wave):
--                     lead_minutes, set for that metric only; its threshold is in minutes like the nowcast's.
--   alert             one row per alert a rule raised on a target (a queue zone, or a device of it for a sensor rule),
--                     with what the rule said at the time; one open alert per rule and target. Alerts are records:
--                     the runtime role resolves them but never deletes them.
--   alert_rule_state  where the live evaluation stands for each rule and target between ticks (armed or not, the
--                     minutes the condition or the clear condition has held, the last minute taken, and the hash of the
--                     rule's values it was kept under), so a restart continues the same fold a backtest makes.
ALTER TABLE alert_rule ADD COLUMN lead_minutes integer CHECK (lead_minutes BETWEEN 15 AND 60);

-- The checks of 0020 that list the metrics are replaced by the same checks with the predicted nowcast. They are found by
-- their definition, since 0020 left their names to PostgreSQL.
DO $$
DECLARE
    c record;
BEGIN
    FOR c IN SELECT conname FROM pg_constraint
             WHERE conrelid = 'public.alert_rule'::regclass AND contype = 'c' AND pg_get_constraintdef(oid) LIKE '%DesksBelowPlan%'
    LOOP
        EXECUTE format('ALTER TABLE public.alert_rule DROP CONSTRAINT %I', c.conname);
    END LOOP;
END
$$;

ALTER TABLE alert_rule ADD CONSTRAINT alert_rule_metric_names
    CHECK (metric IN ('Nowcast', 'BinP90', 'QueueLength', 'OverflowOccupied', 'SensorOffline', 'DesksBelowPlan', 'PredictedNowcast'));
ALTER TABLE alert_rule ADD CONSTRAINT alert_rule_threshold_unit
    CHECK (threshold IS NULL OR threshold <= CASE metric WHEN 'Nowcast' THEN 600 WHEN 'BinP90' THEN 600 WHEN 'PredictedNowcast' THEN 600
                                                         WHEN 'QueueLength' THEN 100000 WHEN 'DesksBelowPlan' THEN 1000 ELSE 0 END);
ALTER TABLE alert_rule ADD CONSTRAINT alert_rule_clear_side
    CHECK (clear_threshold IS NULL
           OR (comparator IN ('GreaterThan', 'GreaterOrEqual') AND clear_threshold < threshold)
           OR (comparator IN ('LessThan', 'LessOrEqual') AND clear_threshold > threshold
               AND clear_threshold <= CASE metric WHEN 'Nowcast' THEN 600 WHEN 'BinP90' THEN 600 WHEN 'PredictedNowcast' THEN 600
                                                  WHEN 'QueueLength' THEN 100000 WHEN 'DesksBelowPlan' THEN 1000 ELSE 0 END));
ALTER TABLE alert_rule ADD CONSTRAINT alert_rule_lead_for_prediction
    CHECK ((metric = 'PredictedNowcast') = (lead_minutes IS NOT NULL));

CREATE TABLE alert (
    id                      uuid              PRIMARY KEY,
    site_code               varchar(17)       NOT NULL REFERENCES site (code),
    rule_id                 uuid              NOT NULL REFERENCES alert_rule (id),
    rule_code               varchar(8)        NOT NULL,
    rule_name               varchar(200)      NOT NULL,
    zone_name               varchar(200)      NOT NULL,
    device_code             varchar(16),
    metric                  varchar(32)       NOT NULL CHECK (metric IN ('Nowcast', 'BinP90', 'QueueLength', 'OverflowOccupied', 'SensorOffline', 'DesksBelowPlan', 'PredictedNowcast')),
    severity                varchar(16)       NOT NULL CHECK (severity IN ('Info', 'Warning', 'Critical')),
    owner_role              varchar(32),
    escalate_after_minutes  integer           CHECK (escalate_after_minutes BETWEEN 1 AND 1440),
    escalate_to_role        varchar(32),
    escalation_contact      varchar(100),
    raised_utc              timestamptz       NOT NULL,
    raised_value            double precision  NOT NULL CHECK (raised_value = raised_value AND raised_value BETWEEN -1e9 AND 1e9),
    bin_start_utc           timestamptz,
    predicted_for_utc       timestamptz,
    state                   varchar(16)       NOT NULL CHECK (state IN ('Raised', 'Acknowledged', 'Escalated', 'Resolved')),
    cleared_utc             timestamptz,
    resolved_utc            timestamptz,
    resolution              varchar(16)       CHECK (resolution IN ('Cleared', 'RuleWithdrawn', 'TargetWithdrawn', 'RuleChanged', 'Manual')),
    resolved_by             varchar(200),
    CHECK ((state = 'Resolved') = (resolved_utc IS NOT NULL AND resolution IS NOT NULL AND resolved_by IS NOT NULL)),
    CHECK (cleared_utc IS NULL OR (resolution = 'Cleared' AND cleared_utc >= raised_utc)),
    CHECK (resolved_utc IS NULL OR resolved_utc >= raised_utc),
    CHECK ((device_code IS NOT NULL) = (metric = 'SensorOffline'))
);

-- One open alert per rule and target (dedupe), also against two evaluators racing.
CREATE UNIQUE INDEX ux_alert_open ON alert (rule_id, zone_name, coalesce(device_code, '')) WHERE state <> 'Resolved';
CREATE INDEX ix_alert_site_raised ON alert (site_code, raised_utc DESC);
CREATE INDEX ix_alert_rule ON alert (rule_id, raised_utc);

-- A raised alert keeps what was raised; only its state, clear and resolution change, and a resolved alert stays resolved.
CREATE FUNCTION alert_keep_raise() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF (NEW.id, NEW.site_code, NEW.rule_id, NEW.rule_code, NEW.rule_name, NEW.zone_name, NEW.device_code, NEW.metric, NEW.severity, NEW.owner_role,
        NEW.escalate_after_minutes, NEW.escalate_to_role, NEW.escalation_contact, NEW.raised_utc, NEW.raised_value, NEW.bin_start_utc, NEW.predicted_for_utc)
       IS DISTINCT FROM
       (OLD.id, OLD.site_code, OLD.rule_id, OLD.rule_code, OLD.rule_name, OLD.zone_name, OLD.device_code, OLD.metric, OLD.severity, OLD.owner_role,
        OLD.escalate_after_minutes, OLD.escalate_to_role, OLD.escalation_contact, OLD.raised_utc, OLD.raised_value, OLD.bin_start_utc, OLD.predicted_for_utc)
    THEN
        RAISE EXCEPTION 'an alert keeps what was raised' USING ERRCODE = 'check_violation';
    END IF;
    IF OLD.state = 'Resolved' AND (NEW.state, NEW.cleared_utc, NEW.resolved_utc, NEW.resolution, NEW.resolved_by)
                                  IS DISTINCT FROM (OLD.state, OLD.cleared_utc, OLD.resolved_utc, OLD.resolution, OLD.resolved_by) THEN
        RAISE EXCEPTION 'a resolved alert stays resolved' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END
$$;

ALTER FUNCTION alert_keep_raise() SET search_path = pg_catalog, public, pg_temp;

CREATE TRIGGER alert_keep_raise BEFORE UPDATE ON alert FOR EACH ROW EXECUTE FUNCTION alert_keep_raise();

CREATE TABLE alert_rule_state (
    id                 uuid          PRIMARY KEY,
    rule_id            uuid          NOT NULL REFERENCES alert_rule (id),
    zone_name          varchar(200)  NOT NULL,
    device_code        varchar(16)   NOT NULL DEFAULT '',
    armed              boolean       NOT NULL,
    sustained          integer       NOT NULL CHECK (sustained BETWEEN 0 AND 1440),
    clearing           integer       NOT NULL CHECK (clearing BETWEEN 0 AND 1440),
    last_minute_utc    timestamptz,
    open_alert_id      uuid          REFERENCES alert (id),
    rule_values        char(64)      NOT NULL CHECK (rule_values ~ '^[0-9a-f]{64}$'),
    updated_on         timestamptz   NOT NULL,
    CHECK (armed = (open_alert_id IS NULL))
);

CREATE UNIQUE INDEX ux_alert_rule_state_target ON alert_rule_state (rule_id, zone_name, device_code);

REVOKE DELETE, TRUNCATE ON alert FROM ariva_runtime;
REVOKE TRUNCATE ON alert_rule_state FROM ariva_runtime;
