-- 0022 Alert lifecycle (ARV-039): an alert is acknowledged by someone responsible, escalated (by hand, or by the
-- evaluation when it stays unacknowledged for the rule's escalation minutes) and resolved by hand with a note or by
-- itself when it clears. Each acknowledgement and escalation happens once and is never rewritten (entering the state
-- is the move that writes it), a manual resolution carries its note, and the states move only forward: Raised to Acknowledged, Escalated or Resolved; Escalated to Acknowledged or Resolved; Acknowledged to
-- Escalated or Resolved; Resolved stays.
ALTER TABLE alert
    ADD COLUMN acknowledged_utc   timestamptz,
    ADD COLUMN acknowledged_by    varchar(200),
    ADD COLUMN acknowledged_note  varchar(500),
    ADD COLUMN escalated_utc      timestamptz,
    ADD COLUMN escalated_by       varchar(200),
    ADD COLUMN escalated_note     varchar(500),
    ADD COLUMN resolution_note    varchar(500);

ALTER TABLE alert ADD CONSTRAINT alert_acknowledged_once
    CHECK ((acknowledged_utc IS NULL) = (acknowledged_by IS NULL) AND (acknowledged_utc IS NULL OR acknowledged_utc >= raised_utc)
           AND (acknowledged_note IS NULL OR acknowledged_utc IS NOT NULL));
ALTER TABLE alert ADD CONSTRAINT alert_escalated_once
    CHECK ((escalated_utc IS NULL) = (escalated_by IS NULL) AND (escalated_utc IS NULL OR escalated_utc >= raised_utc)
           AND (escalated_note IS NULL OR escalated_utc IS NOT NULL));
ALTER TABLE alert ADD CONSTRAINT alert_state_recorded
    CHECK ((state <> 'Acknowledged' OR acknowledged_utc IS NOT NULL) AND (state <> 'Escalated' OR escalated_utc IS NOT NULL)
           AND (resolution_note IS NULL OR resolution = 'Manual'));
ALTER TABLE alert ADD CONSTRAINT alert_note_text
    CHECK (acknowledged_note !~ '[\u0000-\u0009\u000B\u000C\u000E-\u001F\u007F‪-‮⁦-⁩]'
           AND escalated_note !~ '[\u0000-\u0009\u000B\u000C\u000E-\u001F\u007F‪-‮⁦-⁩]'
           AND resolution_note !~ '[\u0000-\u0009\u000B\u000C\u000E-\u001F\u007F‪-‮⁦-⁩]');

-- The evaluation looks for alerts due for escalation every minute.
CREATE INDEX ix_alert_escalation_due ON alert (raised_utc) WHERE state = 'Raised' AND escalate_after_minutes IS NOT NULL AND escalated_utc IS NULL;
CREATE INDEX ix_alert_open_site ON alert (site_code, raised_utc DESC) WHERE state <> 'Resolved';

-- 0021's trigger, with the lifecycle: what was raised never changes, a resolved alert stays resolved, an
-- acknowledgement or escalation is written once, and the state only moves forward.
CREATE OR REPLACE FUNCTION alert_keep_raise() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF (NEW.id, NEW.site_code, NEW.rule_id, NEW.rule_code, NEW.rule_name, NEW.zone_name, NEW.device_code, NEW.metric, NEW.severity, NEW.owner_role,
        NEW.escalate_after_minutes, NEW.escalate_to_role, NEW.escalation_contact, NEW.raised_utc, NEW.raised_value, NEW.bin_start_utc, NEW.predicted_for_utc)
       IS DISTINCT FROM
       (OLD.id, OLD.site_code, OLD.rule_id, OLD.rule_code, OLD.rule_name, OLD.zone_name, OLD.device_code, OLD.metric, OLD.severity, OLD.owner_role,
        OLD.escalate_after_minutes, OLD.escalate_to_role, OLD.escalation_contact, OLD.raised_utc, OLD.raised_value, OLD.bin_start_utc, OLD.predicted_for_utc)
    THEN
        RAISE EXCEPTION 'an alert keeps what was raised' USING ERRCODE = 'check_violation';
    END IF;
    IF OLD.state = 'Resolved' AND (NEW.state, NEW.cleared_utc, NEW.resolved_utc, NEW.resolution, NEW.resolved_by, NEW.resolution_note,
                                   NEW.acknowledged_utc, NEW.acknowledged_by, NEW.acknowledged_note, NEW.escalated_utc, NEW.escalated_by, NEW.escalated_note)
                                  IS DISTINCT FROM (OLD.state, OLD.cleared_utc, OLD.resolved_utc, OLD.resolution, OLD.resolved_by, OLD.resolution_note,
                                   OLD.acknowledged_utc, OLD.acknowledged_by, OLD.acknowledged_note, OLD.escalated_utc, OLD.escalated_by, OLD.escalated_note) THEN
        RAISE EXCEPTION 'a resolved alert stays resolved' USING ERRCODE = 'check_violation';
    END IF;
    IF OLD.acknowledged_utc IS NOT NULL AND (NEW.acknowledged_utc, NEW.acknowledged_by, NEW.acknowledged_note)
                                            IS DISTINCT FROM (OLD.acknowledged_utc, OLD.acknowledged_by, OLD.acknowledged_note) THEN
        RAISE EXCEPTION 'an acknowledgement is written once' USING ERRCODE = 'check_violation';
    END IF;
    IF OLD.escalated_utc IS NOT NULL AND (NEW.escalated_utc, NEW.escalated_by, NEW.escalated_note)
                                         IS DISTINCT FROM (OLD.escalated_utc, OLD.escalated_by, OLD.escalated_note) THEN
        RAISE EXCEPTION 'an escalation is written once' USING ERRCODE = 'check_violation';
    END IF;
    -- Entering Acknowledged or Escalated is the move that writes it, once; a manual resolution carries its note.
    IF NEW.state <> OLD.state AND NEW.state = 'Acknowledged' AND NOT (OLD.acknowledged_utc IS NULL AND NEW.acknowledged_utc IS NOT NULL) THEN
        RAISE EXCEPTION 'an alert is acknowledged once' USING ERRCODE = 'check_violation';
    END IF;
    IF NEW.state <> OLD.state AND NEW.state = 'Escalated' AND NOT (OLD.escalated_utc IS NULL AND NEW.escalated_utc IS NOT NULL) THEN
        RAISE EXCEPTION 'an alert is escalated once' USING ERRCODE = 'check_violation';
    END IF;
    -- ...and an acknowledgement or escalation is written only by entering its state.
    IF OLD.acknowledged_utc IS NULL AND NEW.acknowledged_utc IS NOT NULL AND NEW.state <> 'Acknowledged' THEN
        RAISE EXCEPTION 'an acknowledgement is written by acknowledging' USING ERRCODE = 'check_violation';
    END IF;
    IF OLD.escalated_utc IS NULL AND NEW.escalated_utc IS NOT NULL AND NEW.state <> 'Escalated' THEN
        RAISE EXCEPTION 'an escalation is written by escalating' USING ERRCODE = 'check_violation';
    END IF;
    IF OLD.state <> 'Resolved' AND NEW.resolution = 'Manual' AND NEW.resolution_note IS NULL THEN
        RAISE EXCEPTION 'a manual resolution needs a note' USING ERRCODE = 'check_violation';
    END IF;
    IF NEW.state <> OLD.state AND NOT (
           (OLD.state = 'Raised' AND NEW.state IN ('Acknowledged', 'Escalated', 'Resolved'))
        OR (OLD.state = 'Escalated' AND NEW.state IN ('Acknowledged', 'Resolved'))
        OR (OLD.state = 'Acknowledged' AND NEW.state IN ('Escalated', 'Resolved'))) THEN
        RAISE EXCEPTION 'an alert moves from % only forward, not to %', OLD.state, NEW.state USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END
$$;

ALTER FUNCTION alert_keep_raise() SET search_path = pg_catalog, public, pg_temp;
