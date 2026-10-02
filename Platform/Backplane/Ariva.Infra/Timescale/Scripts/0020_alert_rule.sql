-- 0020 Alert rules (ARV-037): typed rules per site, never expressions. A code (R-001, R-002, ...) is given once per site
-- and not reused after a delete; deletes are soft, like the topology. The checks repeat the entity's (AlertRuleValues),
-- so no statement, the runtime role's included, can store a rule the evaluator cannot apply; a rule's site and code
-- never change. Names, zones and contacts refuse control characters and the usual invisible and bidirectional ones (the
-- entity refuses every Unicode format character).
CREATE TABLE alert_rule (
    id                      uuid              PRIMARY KEY,
    site_code               varchar(17)       NOT NULL REFERENCES site (code),
    code                    varchar(8)        NOT NULL CHECK (code ~ '^R-[0-9]{3,6}$' AND CAST(substring(code FROM 3) AS integer) >= 1),
    name                    varchar(200)      NOT NULL CHECK (length(trim(name)) > 0
                                                  AND name !~ '[[:cntrl:]­؜᠎​-‏‪-‮⁠-⁤⁦-⁯﻿￹-￻]'),
    scope_zones             varchar(12900)    NOT NULL CHECK (length(scope_zones) > 0
                                                  AND scope_zones !~ '(^|\n)[ \t]*(\n|$)'
                                                  AND scope_zones !~ '[\u0001-\u0009\u000B-\u001F\u007F-\u009F­؜᠎​-‏‪-‮⁠-⁤⁦-⁯﻿￹-￻]'
                                                  AND cardinality(string_to_array(scope_zones, E'\n')) <= 64),
    metric                  varchar(32)       NOT NULL CHECK (metric IN ('Nowcast', 'BinP90', 'QueueLength', 'OverflowOccupied', 'SensorOffline', 'DesksBelowPlan')),
    comparator              varchar(32)       NOT NULL CHECK (comparator IN ('GreaterThan', 'GreaterOrEqual', 'LessThan', 'LessOrEqual', 'IsTrue')),
    threshold               double precision  CHECK (threshold >= 0),
    min_queue_length        integer           CHECK (min_queue_length >= 0 AND min_queue_length <= 100000),
    clear_threshold         double precision  CHECK (clear_threshold >= 0),
    sustain_minutes         integer           NOT NULL CHECK (sustain_minutes BETWEEN 1 AND 120),
    clear_after_minutes     integer           NOT NULL CHECK (clear_after_minutes BETWEEN 1 AND 120),
    severity                varchar(16)       NOT NULL CHECK (severity IN ('Info', 'Warning', 'Critical')),
    owner_role              varchar(32)       CHECK (owner_role IN ('BorderShiftSupervisor', 'TerminalDutyManager', 'HandlerStationManager', 'SystemAdministrator')),
    escalate_after_minutes  integer           CHECK (escalate_after_minutes BETWEEN 1 AND 1440),
    escalate_to_role        varchar(32)       CHECK (escalate_to_role IN ('BorderShiftSupervisor', 'TerminalDutyManager', 'HandlerStationManager', 'SystemAdministrator')),
    escalation_contact      varchar(100)      CHECK (length(trim(escalation_contact)) > 0
                                                  AND escalation_contact !~ '[[:cntrl:]­؜᠎​-‏‪-‮⁠-⁤⁦-⁯﻿￹-￻]'),
    notify_by_email         boolean           NOT NULL,
    enabled                 boolean           NOT NULL,
    created_by              varchar(200),
    created_on              timestamptz,
    created_by_id           uuid,
    modified_by             varchar(200),
    modified_on             timestamptz,
    modified_by_id          uuid,
    deleted_by              varchar(200),
    deleted_on              timestamptz,
    -- Conditions are true or false: IsTrue, no threshold, clear threshold or minimum queue; numbers take a comparator
    -- and a threshold in their unit (minutes up to 600, people up to 100,000, desks up to 1,000).
    CHECK ((metric IN ('OverflowOccupied', 'SensorOffline')) = (comparator = 'IsTrue')),
    CHECK ((comparator = 'IsTrue') = (threshold IS NULL)),
    CHECK (comparator <> 'IsTrue' OR (clear_threshold IS NULL AND min_queue_length IS NULL)),
    CHECK (threshold IS NULL OR threshold <= CASE metric WHEN 'Nowcast' THEN 600 WHEN 'BinP90' THEN 600 WHEN 'QueueLength' THEN 100000 WHEN 'DesksBelowPlan' THEN 1000 ELSE 0 END),
    CHECK (min_queue_length IS NULL OR metric = 'Nowcast'),
    -- The clear threshold lies strictly on the clearing side of the threshold, within the unit.
    CHECK (clear_threshold IS NULL
           OR (comparator IN ('GreaterThan', 'GreaterOrEqual') AND clear_threshold < threshold)
           OR (comparator IN ('LessThan', 'LessOrEqual') AND clear_threshold > threshold
               AND clear_threshold <= CASE metric WHEN 'Nowcast' THEN 600 WHEN 'BinP90' THEN 600 WHEN 'QueueLength' THEN 100000 WHEN 'DesksBelowPlan' THEN 1000 ELSE 0 END)),
    CHECK (escalate_after_minutes IS NOT NULL OR (escalate_to_role IS NULL AND escalation_contact IS NULL))
);

CREATE UNIQUE INDEX ux_alert_rule_code ON alert_rule (site_code, code);
CREATE INDEX ix_alert_rule_site ON alert_rule (site_code) WHERE deleted_on IS NULL;

-- A rule's id, site and code are given once. Only the table's owner could disable this trigger.
CREATE FUNCTION alert_rule_identity() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.site_code IS DISTINCT FROM OLD.site_code OR NEW.code IS DISTINCT FROM OLD.code OR NEW.id IS DISTINCT FROM OLD.id THEN
        RAISE EXCEPTION 'an alert rule keeps its id, site and code' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END
$$;

ALTER FUNCTION alert_rule_identity() SET search_path = pg_catalog, public, pg_temp;

CREATE TRIGGER alert_rule_identity BEFORE UPDATE ON alert_rule FOR EACH ROW EXECUTE FUNCTION alert_rule_identity();

REVOKE DELETE, TRUNCATE ON alert_rule FROM ariva_runtime;
