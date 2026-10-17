-- 0054 Retention of relational records (ARV-111a, docs/privacy/data-inventory.md; TC-66 to TC-68).
--
-- Three relational tables grow without end and hold records with a retention question: alert emails (email_message: staff
-- email addresses, TC-67), the Integration API call record (integration_call: TC-68) and report deliveries (report_delivery:
-- TC-66). The runtime role may not delete any of them (0023, 0025, 0036): they are records, and the hosts' runtime login must
-- not be able to erase them. This script keeps that, and adds one narrow path for the Cronz retention job:
--
--   retention_period            the period of each data set in days, written by the migration job from Database:Retention
--                               (the Helm values migration.retention of each environment) with the migration login. The
--                               runtime role reads it and can never write it.
--   ariva_retention_purge(data set, batch size)
--                               deletes at most one batch (1 to 10,000 rows) of the data set's rows older than its period,
--                               oldest first, and returns how many it deleted. It runs as its owner (SECURITY DEFINER), with
--                               its search path pinned to pg_catalog and pg_temp (no public: every object in the body is
--                               named with its schema, so no operator or function of public can stand in for the catalog's)
--                               and its time zone to UTC (a caller's daylight saving time cannot move the cutoff); the
--                               period is read here, never passed in, so the runtime login can only remove what the
--                               configured period already lets go (it cannot move the time column the purge reads, below),
--                               and nothing at all for a data set without a period. Emails still Pending are never deleted.
--                               No dynamic SQL: one constant statement per data set. EXECUTE goes to ariva_runtime only.
--
-- The periods are at least 30 days (the floor here and in RetentionSettings): report deliveries are owed for 3 days at most
-- (ReportDeliveryRound), so a deleted delivery is never owed and sent again. Each table gets an index on its time column so
-- that a batch finds its oldest rows without reading the table. TRUNCATE and direct DELETE stay revoked from the runtime role.
--
-- The runtime role cannot move the time column the purge reads on any purged table, or it could age a row into the purge:
-- email_message.created_utc (0023: UPDATE of the sending columns only, and a trigger that keeps the rest),
-- integration_call.at_utc (0025: no UPDATE at all) and report_delivery.created_utc (below: 0036 revoked only DELETE and
-- TRUNCATE, so 0001's default privileges still gave table-wide UPDATE; it keeps UPDATE of the four columns the delivery round
-- marks, which also lets the round lock a delivery with SELECT ... FOR UPDATE).
CREATE TABLE retention_period (
    data_set  varchar(40)   PRIMARY KEY CHECK (data_set IN ('email_message', 'integration_call', 'report_delivery')),
    days      integer       NOT NULL CHECK (days BETWEEN 30 AND 3650),
    set_on    timestamptz   NOT NULL,
    set_by    varchar(100)  NOT NULL
);

REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON retention_period FROM ariva_runtime;
GRANT SELECT ON retention_period TO ariva_runtime;

REVOKE UPDATE ON report_delivery FROM ariva_runtime;
GRANT UPDATE (status, attempts, reason, sent_utc) ON report_delivery TO ariva_runtime;

CREATE INDEX ix_email_message_created ON email_message (created_utc);
CREATE INDEX ix_integration_call_at ON integration_call (at_utc);
CREATE INDEX ix_report_delivery_created ON report_delivery (created_utc);

CREATE FUNCTION ariva_retention_purge(p_data_set text, p_limit integer) RETURNS integer
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
    SET TimeZone = 'UTC'
AS $$
DECLARE
    period   integer;
    cutoff   timestamptz;
    deleted  integer := 0;
BEGIN
    IF p_limit IS NULL OR p_limit < 1 OR p_limit > 10000 THEN
        RAISE EXCEPTION 'a retention batch holds 1 to 10000 rows' USING ERRCODE = 'invalid_parameter_value';
    END IF;
    SELECT r.days INTO period FROM public.retention_period r WHERE r.data_set = p_data_set;
    IF period IS NULL THEN
        RAISE EXCEPTION 'no retention period is set for this data set' USING ERRCODE = 'no_data_found';
    END IF;
    IF period < 30 THEN
        RAISE EXCEPTION 'a retention period is at least 30 days' USING ERRCODE = 'check_violation';
    END IF;
    cutoff := pg_catalog.now() - pg_catalog.make_interval(days => period);

    IF p_data_set = 'email_message' THEN
        DELETE FROM public.email_message e WHERE e.id IN (
            SELECT m.id FROM public.email_message m
             WHERE m.created_utc < cutoff AND m.status <> 'Pending'
             ORDER BY m.created_utc LIMIT p_limit);
        GET DIAGNOSTICS deleted = ROW_COUNT;
    ELSIF p_data_set = 'integration_call' THEN
        DELETE FROM public.integration_call c WHERE c.id IN (
            SELECT x.id FROM public.integration_call x
             WHERE x.at_utc < cutoff
             ORDER BY x.at_utc LIMIT p_limit);
        GET DIAGNOSTICS deleted = ROW_COUNT;
    ELSIF p_data_set = 'report_delivery' THEN
        DELETE FROM public.report_delivery d WHERE d.id IN (
            SELECT x.id FROM public.report_delivery x
             WHERE x.created_utc < cutoff
             ORDER BY x.created_utc LIMIT p_limit);
        GET DIAGNOSTICS deleted = ROW_COUNT;
    END IF;

    RETURN deleted;
END
$$;

REVOKE ALL ON FUNCTION ariva_retention_purge(text, integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION ariva_retention_purge(text, integer) TO ariva_runtime;
