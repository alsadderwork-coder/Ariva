-- 0048 Tracer runs and desk observer logs (ARV-104b; formulas F10, F18, F19; wiki 07 section 8). Ground truth for the
-- realised wait (timed tracers) and for desk states (an observer's state of each border desk per minute), both bound to a
-- validation campaign of script 0047 and captured only while it runs, by an observer that neither created nor started it.
--
--   validation_campaign_desk  the border desks in a campaign's scope: staffed immigration or emigration desks of its site,
--                             with the checkpoint and desk codes (desk_minute's key site/checkpoint/desk, ARV-104f); set
--                             when the campaign is planned. A campaign planned before this script has none.
--   tracer_batch              one batch of runs from a capturing device: its Idempotency-Key and request fingerprint, the
--                             device's own clock reading, the server's receipt and the measured offset (device minus server,
--                             milliseconds, at most 5 minutes either way)
--   tracer_run                one tracer: the queue zone in scope, a campaign label (T-07, never a name), the join and exit
--                             times as the device read them and corrected by the batch's offset, and the abandoned flag
--   desk_observation_batch    one observer's 15-minute batch of desk states: its Idempotency-Key and request fingerprint
--   desk_observation          one row per revision of an observer's state (Closed, Idle, Serving, Paused) of a desk for a
--                             minute; a correction is a new revision with a reason, never an edit
--
-- People appear only as Ariva user ids (observer_id); tracers only as campaign labels; no column holds a name, staff number,
-- contact or document (data boundary). Desk observations are desk-level border data: they stay in the border deployment and
-- only border roles read them. Retention: indefinite (evidence of the pilot's acceptance criteria); the runtime role never
-- changes, deletes or truncates any row here.

ALTER TABLE desk ADD CONSTRAINT uq_desk_id_site UNIQUE (id, site_code);

CREATE TABLE validation_campaign_desk (
    id               uuid          PRIMARY KEY,
    campaign_id      uuid          NOT NULL,
    site_code        varchar(17)   NOT NULL,
    desk_id          uuid          NOT NULL,
    checkpoint_code  varchar(16)   NOT NULL,
    desk_code        varchar(16)   NOT NULL,
    FOREIGN KEY (campaign_id, site_code) REFERENCES validation_campaign (id, site_code),
    FOREIGN KEY (desk_id, site_code) REFERENCES desk (id, site_code),
    UNIQUE (campaign_id, desk_id)
);

CREATE TABLE tracer_batch (
    id                uuid          PRIMARY KEY,
    campaign_id       uuid          NOT NULL,
    site_code         varchar(17)   NOT NULL,
    observer_id       uuid          NOT NULL REFERENCES "user" (id),
    idempotency_key   varchar(64)   NOT NULL CHECK (idempotency_key ~ '^[A-Za-z0-9][A-Za-z0-9._:-]{7,63}$'),
    request_hash      varchar(64)   NOT NULL CHECK (request_hash ~ '^[0-9a-f]{64}$'),
    device_clock_utc  timestamptz   NOT NULL,
    received_utc      timestamptz   NOT NULL,
    clock_offset_ms   integer       NOT NULL CHECK (clock_offset_ms BETWEEN -300000 AND 300000),
    runs              integer       NOT NULL CHECK (runs BETWEEN 1 AND 20),
    FOREIGN KEY (campaign_id, site_code) REFERENCES validation_campaign (id, site_code),
    UNIQUE (id, campaign_id, observer_id, clock_offset_ms),
    -- The offset is the device's reading minus the receipt, to the millisecond (F19's sign: positive when the device runs ahead).
    CHECK (device_clock_utc - received_utc = clock_offset_ms * interval '1 millisecond')
);

-- A device's resent batch (Idempotency-Key) finds the batch it already made; a key belongs to its observer.
CREATE UNIQUE INDEX ux_tracer_batch_idempotency ON tracer_batch (observer_id, idempotency_key);

CREATE TABLE tracer_run (
    id               uuid          PRIMARY KEY,
    batch_id         uuid          NOT NULL,
    campaign_id      uuid          NOT NULL,
    site_code        varchar(17)   NOT NULL,
    zone_id          uuid          NOT NULL,
    tracer_code      varchar(5)    NOT NULL CHECK (tracer_code ~ '^T-[0-9]{2,3}$'),
    observer_id      uuid          NOT NULL,
    joined_raw_utc   timestamptz   NOT NULL,
    exited_raw_utc   timestamptz   NOT NULL,
    clock_offset_ms  integer       NOT NULL,
    joined_utc       timestamptz   NOT NULL,
    exited_utc       timestamptz   NOT NULL,
    abandoned        boolean       NOT NULL,
    recorded_utc     timestamptz   NOT NULL,
    -- The run carries its batch's campaign, observer and offset, and corrects its device times by that offset.
    FOREIGN KEY (batch_id, campaign_id, observer_id, clock_offset_ms) REFERENCES tracer_batch (id, campaign_id, observer_id, clock_offset_ms),
    FOREIGN KEY (campaign_id, site_code) REFERENCES validation_campaign (id, site_code),
    FOREIGN KEY (campaign_id, zone_id) REFERENCES validation_campaign_zone (campaign_id, zone_id),
    CHECK (joined_utc = joined_raw_utc - clock_offset_ms * interval '1 millisecond'),
    CHECK (exited_utc = exited_raw_utc - clock_offset_ms * interval '1 millisecond'),
    CHECK (exited_utc > joined_utc AND exited_utc - joined_utc <= interval '3 hours'),
    -- Not in the future beyond the one-minute clock tolerance when recorded.
    CHECK (exited_utc <= recorded_utc + interval '1 minute')
);

-- One run per observer, tracer code and join time on the device's clock (a run sent again in another batch).
CREATE UNIQUE INDEX ux_tracer_run_join ON tracer_run (campaign_id, observer_id, tracer_code, joined_raw_utc);
CREATE INDEX ix_tracer_run_campaign_joined ON tracer_run (campaign_id, joined_utc);

CREATE TABLE desk_observation_batch (
    id               uuid          PRIMARY KEY,
    campaign_id      uuid          NOT NULL,
    site_code        varchar(17)   NOT NULL,
    observer_id      uuid          NOT NULL REFERENCES "user" (id),
    bin_start_utc    timestamptz   NOT NULL CHECK (extract(epoch FROM bin_start_utc) % 900 = 0),
    idempotency_key  varchar(64)   NOT NULL CHECK (idempotency_key ~ '^[A-Za-z0-9][A-Za-z0-9._:-]{7,63}$'),
    request_hash     varchar(64)   NOT NULL CHECK (request_hash ~ '^[0-9a-f]{64}$'),
    received_utc     timestamptz   NOT NULL,
    observations     integer       NOT NULL CHECK (observations BETWEEN 1 AND 300),
    FOREIGN KEY (campaign_id, site_code) REFERENCES validation_campaign (id, site_code)
);

CREATE UNIQUE INDEX ux_desk_observation_batch_idempotency ON desk_observation_batch (observer_id, idempotency_key);

CREATE TABLE desk_observation (
    id               uuid          PRIMARY KEY,
    batch_id         uuid          REFERENCES desk_observation_batch (id),
    campaign_id      uuid          NOT NULL,
    site_code        varchar(17)   NOT NULL,
    desk_id          uuid          NOT NULL,
    minute_utc       timestamptz   NOT NULL CHECK (extract(epoch FROM minute_utc) % 60 = 0),
    observer_id      uuid          NOT NULL REFERENCES "user" (id),
    revision         integer       NOT NULL CHECK (revision BETWEEN 1 AND 100),
    state            varchar(16)   NOT NULL CHECK (state IN ('Closed', 'Idle', 'Serving', 'Paused')),
    reason           varchar(200)  CHECK (reason IS NULL OR length(btrim(reason)) BETWEEN 1 AND 200),
    corrects_id      uuid          REFERENCES desk_observation (id),
    recorded_utc     timestamptz   NOT NULL,
    idempotency_key  varchar(64)   CHECK (idempotency_key ~ '^[A-Za-z0-9][A-Za-z0-9._:-]{7,63}$'),
    FOREIGN KEY (campaign_id, site_code) REFERENCES validation_campaign (id, site_code),
    FOREIGN KEY (campaign_id, desk_id) REFERENCES validation_campaign_desk (campaign_id, desk_id),
    -- Revision 1 comes in a batch (which holds the key); a later revision has a reason, corrects one and may hold its own key.
    CHECK ((revision = 1) = (reason IS NULL)),
    CHECK ((revision = 1) = (corrects_id IS NULL)),
    CHECK ((revision = 1) = (batch_id IS NOT NULL)),
    CHECK (batch_id IS NULL OR idempotency_key IS NULL),
    -- The minute has ended (within the one-minute clock tolerance) when recorded.
    CHECK (minute_utc <= recorded_utc)
);

-- One state per desk, minute and observer (revision 1), and one row per later revision; a revision is corrected at most once,
-- so the revisions of a state form one chain and its current row is the one nothing corrects.
CREATE UNIQUE INDEX ux_desk_observation_revision ON desk_observation (campaign_id, desk_id, minute_utc, observer_id, revision);
CREATE UNIQUE INDEX ux_desk_observation_corrects ON desk_observation (corrects_id) WHERE corrects_id IS NOT NULL;
CREATE UNIQUE INDEX ux_desk_observation_idempotency ON desk_observation (observer_id, idempotency_key) WHERE idempotency_key IS NOT NULL;
CREATE INDEX ix_desk_observation_campaign_minute ON desk_observation (campaign_id, minute_utc);

-- A desk joins the scope while the campaign is planned, and only a live, in-service, staffed desk of an immigration or
-- emigration checkpoint of the campaign's site, with its own codes (the desk row is share-locked against a concurrent delete).
-- The campaign's status is read FOR SHARE, as the capture triggers read it: a start in progress (FOR UPDATE) is waited for,
-- so a desk never joins a campaign that has just started.
CREATE FUNCTION validation_campaign_desk_check() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    campaign_status  varchar(16);
BEGIN
    SELECT status INTO campaign_status FROM validation_campaign WHERE id = NEW.campaign_id FOR SHARE;
    IF campaign_status IS DISTINCT FROM 'Planned' THEN
        RAISE EXCEPTION 'the scope of a validation campaign is set when it is planned' USING ERRCODE = 'restrict_violation';
    END IF;
    IF NOT EXISTS (
        SELECT 1
        FROM desk d
        JOIN checkpoint c ON c.id = d.checkpoint_id
        WHERE d.id = NEW.desk_id AND d.site_code = NEW.site_code AND d.kind = 'Desk' AND d.in_service AND d.deleted_on IS NULL
          AND c.kind IN ('Immigration', 'Emigration') AND c.deleted_on IS NULL
          AND d.code = NEW.desk_code AND c.code = NEW.checkpoint_code
        FOR SHARE OF d) THEN
        RAISE EXCEPTION 'a campaign desk is a staffed immigration or emigration desk of the campaign''s site' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER validation_campaign_desk_check BEFORE INSERT ON validation_campaign_desk
    FOR EACH ROW EXECUTE FUNCTION validation_campaign_desk_check();

-- Ground truth goes only into a running campaign (FOR SHARE waits for a close in progress, so nothing lands after the close
-- commits), never from the account that created or started the campaign (separation of duties, as for manual counts).
CREATE FUNCTION validation_capture_insert_check() RETURNS trigger LANGUAGE plpgsql AS $$
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
        RAISE EXCEPTION 'the account that created or started validation campaign % does not capture for it', NEW.campaign_id
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER tracer_batch_capture_check BEFORE INSERT ON tracer_batch
    FOR EACH ROW EXECUTE FUNCTION validation_capture_insert_check();
CREATE TRIGGER tracer_run_capture_check BEFORE INSERT ON tracer_run
    FOR EACH ROW EXECUTE FUNCTION validation_capture_insert_check();
CREATE TRIGGER desk_observation_batch_capture_check BEFORE INSERT ON desk_observation_batch
    FOR EACH ROW EXECUTE FUNCTION validation_capture_insert_check();
CREATE TRIGGER desk_observation_capture_check BEFORE INSERT ON desk_observation
    FOR EACH ROW EXECUTE FUNCTION validation_capture_insert_check();

-- A batch's state is for a minute of its bin, of its campaign and observer; a correction is the next revision of the same
-- desk, minute and observer.
CREATE FUNCTION desk_observation_insert_check() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.batch_id IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM desk_observation_batch b
        WHERE b.id = NEW.batch_id AND b.campaign_id = NEW.campaign_id AND b.observer_id = NEW.observer_id
          AND NEW.minute_utc >= b.bin_start_utc AND NEW.minute_utc < b.bin_start_utc + interval '15 minutes') THEN
        RAISE EXCEPTION 'a batch''s desk state is for a minute of its bin, campaign and observer' USING ERRCODE = 'check_violation';
    END IF;
    IF NEW.corrects_id IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM desk_observation o
        WHERE o.id = NEW.corrects_id AND o.campaign_id = NEW.campaign_id AND o.desk_id = NEW.desk_id AND o.minute_utc = NEW.minute_utc
          AND o.observer_id = NEW.observer_id AND o.revision = NEW.revision - 1) THEN
        RAISE EXCEPTION 'a correction is the next revision of the same desk, minute and observer' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER desk_observation_insert_check BEFORE INSERT ON desk_observation
    FOR EACH ROW EXECUTE FUNCTION desk_observation_insert_check();

-- Scope rows, batches, runs and observations are never changed or deleted, even by a bug or a hand-written statement
-- (validation_row_immutable is script 0047's).
CREATE TRIGGER validation_campaign_desk_immutable BEFORE UPDATE OR DELETE ON validation_campaign_desk
    FOR EACH ROW EXECUTE FUNCTION validation_row_immutable();
CREATE TRIGGER tracer_batch_immutable BEFORE UPDATE OR DELETE ON tracer_batch
    FOR EACH ROW EXECUTE FUNCTION validation_row_immutable();
CREATE TRIGGER tracer_run_immutable BEFORE UPDATE OR DELETE ON tracer_run
    FOR EACH ROW EXECUTE FUNCTION validation_row_immutable();
CREATE TRIGGER desk_observation_batch_immutable BEFORE UPDATE OR DELETE ON desk_observation_batch
    FOR EACH ROW EXECUTE FUNCTION validation_row_immutable();
CREATE TRIGGER desk_observation_immutable BEFORE UPDATE OR DELETE ON desk_observation
    FOR EACH ROW EXECUTE FUNCTION validation_row_immutable();

-- The runtime role (default privileges of 0001) inserts and reads; it never updates, deletes or truncates anything here (CWE-269).
REVOKE UPDATE, DELETE, TRUNCATE ON validation_campaign_desk, tracer_batch, tracer_run, desk_observation_batch, desk_observation FROM ariva_runtime;
