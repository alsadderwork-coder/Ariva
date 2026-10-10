-- 0047 Validation campaigns and manual counts (ARV-104a; formulas F18; wiki 07 section 8). A campaign binds a site, one
-- published zone profile version, the queue zones and lines in scope and the planned local days; observers record manual
-- counts per line and 15-minute bin while it runs; a correction is a new revision with a reason, never an edit; a closed
-- campaign never changes. People appear only as Ariva user ids (who created, started and closed a campaign, and the
-- observer of each count), never by name or document (data boundary).
--
--   validation_campaign       the campaign (Planned, Running, Closed), its profile version and geometry hash, its local
--                             days and its targets (placeholders until TC-04 is answered, targets_placeholder)
--   validation_campaign_zone  the queue zones in scope: zones of the campaign's profile version
--   validation_campaign_line  the lines in scope: lines of the campaign's profile version on a queue zone in scope or on
--                             one of its overflow bands, with the queue zone's name (line_minute's key, ARV-104e)
--   manual_count              one row per revision of an observer's count of a line and bin; a row is current while no
--                             later revision corrects it
--
-- The keys below keep, in the database itself, every zone, line and count to the campaign's profile version and site.
-- Retention: indefinite (evidence of the pilot's acceptance criteria); the runtime role never deletes or truncates.

ALTER TABLE zone_profile ADD CONSTRAINT uq_zone_profile_id_site_version UNIQUE (id, site_code, version);
ALTER TABLE line ADD CONSTRAINT uq_line_profile_id UNIQUE (profile_id, id);

CREATE TABLE validation_campaign (
    id                    uuid          PRIMARY KEY,
    site_code             varchar(17)   NOT NULL REFERENCES site (code),
    name                  varchar(200)  NOT NULL CHECK (length(btrim(name)) BETWEEN 1 AND 200),
    status                varchar(16)   NOT NULL CHECK (status IN ('Planned', 'Running', 'Closed')),
    profile_id            uuid          NOT NULL,
    profile_version       integer       NOT NULL CHECK (profile_version >= 1),
    geometry_hash         varchar(64)   NOT NULL CHECK (geometry_hash ~ '^[0-9a-f]{64}$'),
    planned_days          varchar(340)  NOT NULL CHECK (planned_days ~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}(,[0-9]{4}-[0-9]{2}-[0-9]{2}){0,30}$'),
    target_bins_per_line  integer       NOT NULL CHECK (target_bins_per_line BETWEEN 1 AND 2976),
    target_tracer_runs    integer       NOT NULL CHECK (target_tracer_runs BETWEEN 0 AND 1000),
    targets_placeholder   boolean       NOT NULL,
    created_by_id         uuid          NOT NULL REFERENCES "user" (id),
    created_utc           timestamptz   NOT NULL,
    started_by_id         uuid          REFERENCES "user" (id),
    started_utc           timestamptz,
    closed_by_id          uuid          REFERENCES "user" (id),
    closed_utc            timestamptz,
    FOREIGN KEY (profile_id, site_code, profile_version) REFERENCES zone_profile (id, site_code, version),
    UNIQUE (id, profile_id),
    UNIQUE (id, site_code),
    CHECK ((started_by_id IS NULL) = (started_utc IS NULL)),
    CHECK ((closed_by_id IS NULL) = (closed_utc IS NULL)),
    CHECK (CASE status
               WHEN 'Planned' THEN started_utc IS NULL AND closed_utc IS NULL
               WHEN 'Running' THEN started_utc IS NOT NULL AND closed_utc IS NULL
               ELSE closed_utc IS NOT NULL
           END)
);

CREATE INDEX ix_validation_campaign_site ON validation_campaign (site_code, created_utc DESC);

CREATE TABLE validation_campaign_zone (
    id           uuid          PRIMARY KEY,
    campaign_id  uuid          NOT NULL,
    profile_id   uuid          NOT NULL,
    zone_id      uuid          NOT NULL,
    zone_name    varchar(200)  NOT NULL,
    FOREIGN KEY (campaign_id, profile_id) REFERENCES validation_campaign (id, profile_id),
    FOREIGN KEY (profile_id, zone_id) REFERENCES zone (profile_id, id),
    UNIQUE (campaign_id, zone_id)
);

CREATE TABLE validation_campaign_line (
    id               uuid          PRIMARY KEY,
    campaign_id      uuid          NOT NULL,
    profile_id       uuid          NOT NULL,
    line_id          uuid          NOT NULL,
    line_name        varchar(200)  NOT NULL,
    line_role        varchar(100)  NOT NULL CHECK (line_role IN ('Entry', 'Exit', 'Count', 'OverflowEntry')),
    queue_zone_name  varchar(200)  NOT NULL,
    FOREIGN KEY (campaign_id, profile_id) REFERENCES validation_campaign (id, profile_id),
    FOREIGN KEY (profile_id, line_id) REFERENCES line (profile_id, id),
    UNIQUE (campaign_id, line_id)
);

CREATE TABLE manual_count (
    id               uuid          PRIMARY KEY,
    campaign_id      uuid          NOT NULL,
    site_code        varchar(17)   NOT NULL,
    line_id          uuid          NOT NULL,
    bin_start_utc    timestamptz   NOT NULL CHECK (extract(epoch FROM bin_start_utc) % 900 = 0),
    observer_id      uuid          NOT NULL REFERENCES "user" (id),
    revision         integer       NOT NULL CHECK (revision BETWEEN 1 AND 100),
    crossings_in     integer       NOT NULL CHECK (crossings_in BETWEEN 0 AND 10000),
    crossings_out    integer       NOT NULL CHECK (crossings_out BETWEEN 0 AND 10000),
    reason           varchar(200)  CHECK (reason IS NULL OR length(btrim(reason)) BETWEEN 1 AND 200),
    corrects_id      uuid          REFERENCES manual_count (id),
    recorded_utc     timestamptz   NOT NULL,
    idempotency_key  varchar(64)   CHECK (idempotency_key ~ '^[A-Za-z0-9][A-Za-z0-9._:-]{7,63}$'),
    FOREIGN KEY (campaign_id, site_code) REFERENCES validation_campaign (id, site_code),
    FOREIGN KEY (campaign_id, line_id) REFERENCES validation_campaign_line (campaign_id, line_id),
    CHECK ((revision = 1) = (reason IS NULL)),
    CHECK ((revision = 1) = (corrects_id IS NULL))
);

-- One entry per line, bin and observer (revision 1), and one row per later revision; a revision is corrected at most once,
-- so the revisions of a count form one chain and its current row is the one nothing corrects.
CREATE UNIQUE INDEX ux_manual_count_revision ON manual_count (campaign_id, line_id, bin_start_utc, observer_id, revision);
CREATE UNIQUE INDEX ux_manual_count_corrects ON manual_count (corrects_id) WHERE corrects_id IS NOT NULL;
-- A tablet's resent request (Idempotency-Key) finds the count it already made.
CREATE UNIQUE INDEX ux_manual_count_idempotency ON manual_count (observer_id, idempotency_key) WHERE idempotency_key IS NOT NULL;
CREATE INDEX ix_manual_count_campaign_bin ON manual_count (campaign_id, bin_start_utc);

-- A campaign is planned over the published version of its site's profile; FOR SHARE waits for a publish in progress, so
-- no campaign is bound to a version that a concurrent publish is retiring.
CREATE FUNCTION validation_campaign_insert_check() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.status <> 'Planned' OR NEW.started_utc IS NOT NULL OR NEW.closed_utc IS NOT NULL THEN
        RAISE EXCEPTION 'a validation campaign starts planned' USING ERRCODE = 'check_violation';
    END IF;
    IF (SELECT status FROM zone_profile WHERE id = NEW.profile_id FOR SHARE) IS DISTINCT FROM 'Published' THEN
        RAISE EXCEPTION 'a validation campaign is planned over a published zone profile version' USING ERRCODE = 'restrict_violation';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER validation_campaign_insert_check BEFORE INSERT ON validation_campaign
    FOR EACH ROW EXECUTE FUNCTION validation_campaign_insert_check();

-- A campaign changes only by its lifecycle: Planned to Running (while its profile version is still the published one),
-- Planned or Running to Closed; everything else, and a closed campaign, never changes; nothing is deleted.
CREATE FUNCTION validation_campaign_transition() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'validation campaign % is evidence and is never deleted', OLD.id USING ERRCODE = 'restrict_violation';
    END IF;
    IF NEW.id IS DISTINCT FROM OLD.id OR NEW.site_code IS DISTINCT FROM OLD.site_code OR NEW.name IS DISTINCT FROM OLD.name
       OR NEW.profile_id IS DISTINCT FROM OLD.profile_id OR NEW.profile_version IS DISTINCT FROM OLD.profile_version
       OR NEW.geometry_hash IS DISTINCT FROM OLD.geometry_hash OR NEW.planned_days IS DISTINCT FROM OLD.planned_days
       OR NEW.target_bins_per_line IS DISTINCT FROM OLD.target_bins_per_line OR NEW.target_tracer_runs IS DISTINCT FROM OLD.target_tracer_runs
       OR NEW.targets_placeholder IS DISTINCT FROM OLD.targets_placeholder OR NEW.created_by_id IS DISTINCT FROM OLD.created_by_id
       OR NEW.created_utc IS DISTINCT FROM OLD.created_utc THEN
        RAISE EXCEPTION 'validation campaign % keeps its site, name, profile version, days, targets and creation', OLD.id
            USING ERRCODE = 'restrict_violation';
    END IF;
    IF NOT (
           (NEW.status = OLD.status AND NEW.started_by_id IS NOT DISTINCT FROM OLD.started_by_id AND NEW.started_utc IS NOT DISTINCT FROM OLD.started_utc
            AND NEW.closed_by_id IS NOT DISTINCT FROM OLD.closed_by_id AND NEW.closed_utc IS NOT DISTINCT FROM OLD.closed_utc)
        OR (OLD.status = 'Planned' AND NEW.status = 'Running' AND NEW.closed_utc IS NULL)
        OR (OLD.status IN ('Planned', 'Running') AND NEW.status = 'Closed'
            AND NEW.started_by_id IS NOT DISTINCT FROM OLD.started_by_id AND NEW.started_utc IS NOT DISTINCT FROM OLD.started_utc)) THEN
        RAISE EXCEPTION 'validation campaign % goes Planned, Running, Closed and a closed campaign never changes', OLD.id
            USING ERRCODE = 'restrict_violation';
    END IF;
    IF OLD.status = 'Planned' AND NEW.status = 'Running'
       AND (SELECT status FROM zone_profile WHERE id = NEW.profile_id FOR SHARE) IS DISTINCT FROM 'Published' THEN
        RAISE EXCEPTION 'validation campaign % starts only while its profile version is published', OLD.id USING ERRCODE = 'restrict_violation';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER validation_campaign_transition BEFORE UPDATE OR DELETE ON validation_campaign
    FOR EACH ROW EXECUTE FUNCTION validation_campaign_transition();

-- The scope is written with the planned campaign: a queue zone of its profile version, and a line on a queue zone in scope
-- or on an overflow band of one (the zones go in first, in the same transaction).
CREATE FUNCTION validation_campaign_zone_check() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF (SELECT status FROM validation_campaign WHERE id = NEW.campaign_id) IS DISTINCT FROM 'Planned' THEN
        RAISE EXCEPTION 'the scope of a validation campaign is set when it is planned' USING ERRCODE = 'restrict_violation';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM zone WHERE id = NEW.zone_id AND profile_id = NEW.profile_id AND kind = 'Queue' AND name = NEW.zone_name) THEN
        RAISE EXCEPTION 'a campaign zone is a queue zone of the campaign''s profile version' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;

CREATE FUNCTION validation_campaign_line_check() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF (SELECT status FROM validation_campaign WHERE id = NEW.campaign_id) IS DISTINCT FROM 'Planned' THEN
        RAISE EXCEPTION 'the scope of a validation campaign is set when it is planned' USING ERRCODE = 'restrict_violation';
    END IF;
    IF NOT EXISTS (
        SELECT 1
        FROM line l
        JOIN zone z ON z.id = l.zone_id AND z.profile_id = l.profile_id
        JOIN zone q ON q.id = CASE WHEN z.kind = 'Overflow' THEN z.queue_zone_id ELSE z.id END AND q.kind = 'Queue'
        JOIN validation_campaign_zone s ON s.campaign_id = NEW.campaign_id AND s.zone_id = q.id
        WHERE l.id = NEW.line_id AND l.profile_id = NEW.profile_id AND z.kind IN ('Queue', 'Overflow')
          AND l.name = NEW.line_name AND l.role = NEW.line_role AND q.name = NEW.queue_zone_name) THEN
        RAISE EXCEPTION 'a campaign line is on a queue zone in scope or on one of its overflow bands' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER validation_campaign_zone_check BEFORE INSERT ON validation_campaign_zone
    FOR EACH ROW EXECUTE FUNCTION validation_campaign_zone_check();
CREATE TRIGGER validation_campaign_line_check BEFORE INSERT ON validation_campaign_line
    FOR EACH ROW EXECUTE FUNCTION validation_campaign_line_check();

-- A count goes only into a running campaign (FOR SHARE waits for a close in progress, so no count lands after the close
-- commits), never from the account that created or started the campaign (separation of duties: the ground truth stays
-- independent of whoever runs the campaign), and a correction is the next revision of the same line, bin and observer.
CREATE FUNCTION manual_count_insert_check() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    campaign_status  varchar(16);
    creator          uuid;
    starter          uuid;
BEGIN
    SELECT status, created_by_id, started_by_id INTO campaign_status, creator, starter
      FROM validation_campaign WHERE id = NEW.campaign_id FOR SHARE;
    IF campaign_status IS DISTINCT FROM 'Running' THEN
        RAISE EXCEPTION 'validation campaign % is not running', NEW.campaign_id USING ERRCODE = 'restrict_violation';
    END IF;
    IF NEW.observer_id = creator OR NEW.observer_id = starter THEN
        RAISE EXCEPTION 'the account that created or started validation campaign % does not count for it', NEW.campaign_id
            USING ERRCODE = 'check_violation';
    END IF;
    IF NEW.corrects_id IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM manual_count c
        WHERE c.id = NEW.corrects_id AND c.campaign_id = NEW.campaign_id AND c.line_id = NEW.line_id AND c.bin_start_utc = NEW.bin_start_utc
          AND c.observer_id = NEW.observer_id AND c.revision = NEW.revision - 1) THEN
        RAISE EXCEPTION 'a correction is the next revision of the same line, bin and observer' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER manual_count_insert_check BEFORE INSERT ON manual_count
    FOR EACH ROW EXECUTE FUNCTION manual_count_insert_check();

-- Scope rows and counts are never changed or deleted, even by a bug or a hand-written statement.
CREATE FUNCTION validation_row_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION '% rows are evidence and never change', TG_TABLE_NAME USING ERRCODE = 'restrict_violation';
END;
$$;

CREATE TRIGGER validation_campaign_zone_immutable BEFORE UPDATE OR DELETE ON validation_campaign_zone
    FOR EACH ROW EXECUTE FUNCTION validation_row_immutable();
CREATE TRIGGER validation_campaign_line_immutable BEFORE UPDATE OR DELETE ON validation_campaign_line
    FOR EACH ROW EXECUTE FUNCTION validation_row_immutable();
CREATE TRIGGER manual_count_immutable BEFORE UPDATE OR DELETE ON manual_count
    FOR EACH ROW EXECUTE FUNCTION validation_row_immutable();

-- The runtime role (default privileges of 0001) inserts and reads; it updates only a campaign's lifecycle columns and never
-- deletes or truncates anything here (CWE-269).
REVOKE UPDATE, DELETE, TRUNCATE ON validation_campaign FROM ariva_runtime;
GRANT UPDATE (status, started_by_id, started_utc, closed_by_id, closed_utc) ON validation_campaign TO ariva_runtime;
REVOKE UPDATE, DELETE, TRUNCATE ON validation_campaign_zone, validation_campaign_line, manual_count FROM ariva_runtime;
