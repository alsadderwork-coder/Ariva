-- 0060 Advertising playlists and schedules (ARV-143a). A playlist is an ordered list of items of its site, each an
-- advertisement of the site's library or an instruction slide of the site, with its seconds on screen and its weight; a
-- schedule assigns a playlist of the same site to displays of the site on days of the week within a window of the site's
-- local time. The service holds these rules; the checks, the key and the trigger below hold them again for every writer:
--
--   advertising_playlist.items           JSON: 1 to 40 items, each {"kind":"Media"|"Slide","id":"<uuid>","seconds":1..120,
--                                        "weight":1..10}, every id an item of the playlist's own site (an advertisement not
--                                        retired, an active slide) when the items are written
--   advertising_schedule.playlist_id     a playlist of the schedule's own site (a key over both columns)
--   advertising_schedule.targets         JSON: a list of 0 to 20 different names (queue zones or display codes) of 1 to
--                                        200 characters, empty for the whole site and only then
--   advertising_schedule.days            English day names separated by commas
--   advertising_schedule.opens_minute    the minute of the local day the window opens; length_minutes how long it lasts
--
-- Neither table's rows change site or id, and the runtime role cannot delete them: a playlist or a schedule is retired.

CREATE TABLE advertising_playlist (
    id              uuid           PRIMARY KEY,
    site_code       varchar(17)    NOT NULL REFERENCES site (code),
    name            varchar(120)   NOT NULL CHECK (length(trim(name)) >= 1),
    items           varchar(8000)  NOT NULL,
    status          varchar(16)    NOT NULL CHECK (status IN ('Active', 'Retired')),
    created_by      varchar(200),
    created_on      timestamptz,
    created_by_id   uuid,
    modified_by     varchar(200),
    modified_on     timestamptz,
    modified_by_id  uuid,
    CONSTRAINT uq_advertising_playlist_site UNIQUE (id, site_code)
);
CREATE INDEX ix_advertising_playlist_site ON advertising_playlist (site_code, created_on);

CREATE TABLE advertising_schedule (
    id              uuid           PRIMARY KEY,
    site_code       varchar(17)    NOT NULL REFERENCES site (code),
    playlist_id     uuid           NOT NULL,
    scope           varchar(16)    NOT NULL CHECK (scope IN ('Site', 'Zone', 'Display')),
    targets         varchar(4000)  NOT NULL,
    days            varchar(64)    NOT NULL CHECK (
        days ~ '^(Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday)(,(Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday)){0,6}$'),
    opens_minute    integer        NOT NULL CHECK (opens_minute BETWEEN 0 AND 1439),
    length_minutes  integer        NOT NULL CHECK (length_minutes BETWEEN 1 AND 1440),
    status          varchar(16)    NOT NULL CHECK (status IN ('Active', 'Retired')),
    created_by      varchar(200),
    created_on      timestamptz,
    created_by_id   uuid,
    modified_by     varchar(200),
    modified_on     timestamptz,
    modified_by_id  uuid,
    CONSTRAINT fk_advertising_schedule_playlist FOREIGN KEY (playlist_id, site_code) REFERENCES advertising_playlist (id, site_code),
    CONSTRAINT ck_advertising_schedule_targets CHECK ((scope = 'Site') = (targets = '[]'))
);
CREATE INDEX ix_advertising_schedule_site ON advertising_schedule (site_code, created_on);

REVOKE DELETE, TRUNCATE ON advertising_playlist, advertising_schedule FROM ariva_runtime;

CREATE FUNCTION advertising_playlist_guard() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    items jsonb;
    item jsonb;
BEGIN
    IF TG_OP = 'UPDATE' AND (NEW.id IS DISTINCT FROM OLD.id OR NEW.site_code IS DISTINCT FROM OLD.site_code) THEN
        RAISE EXCEPTION 'playlist % keeps its id and its site', OLD.id USING ERRCODE = 'check_violation';
    END IF;
    -- The items are judged when they are written: a later retirement of one of them leaves the playlist as it was.
    IF TG_OP = 'UPDATE' AND NEW.items IS NOT DISTINCT FROM OLD.items THEN
        RETURN NEW;
    END IF;
    items := NEW.items::jsonb;
    IF jsonb_typeof(items) <> 'array' OR jsonb_array_length(items) NOT BETWEEN 1 AND 40 THEN
        RAISE EXCEPTION 'playlist % has 1 to 40 items', NEW.id USING ERRCODE = 'check_violation';
    END IF;
    FOR item IN SELECT value FROM jsonb_array_elements(items) LOOP
        IF jsonb_typeof(item) <> 'object' OR jsonb_typeof(item -> 'kind') <> 'string' OR item ->> 'kind' NOT IN ('Media', 'Slide')
            OR jsonb_typeof(item -> 'id') <> 'string' OR (item ->> 'id') !~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
            OR jsonb_typeof(item -> 'seconds') <> 'number' OR (item -> 'seconds')::numeric NOT BETWEEN 1 AND 120
            OR (item -> 'seconds')::numeric <> trunc((item -> 'seconds')::numeric)
            OR jsonb_typeof(item -> 'weight') <> 'number' OR (item -> 'weight')::numeric NOT BETWEEN 1 AND 10
            OR (item -> 'weight')::numeric <> trunc((item -> 'weight')::numeric) THEN
            RAISE EXCEPTION 'playlist % has an item that is not an advertisement or a slide with its seconds and weight', NEW.id
                USING ERRCODE = 'check_violation';
        END IF;
        IF item ->> 'kind' = 'Media' AND NOT EXISTS (
            SELECT 1 FROM advertising_media m WHERE m.id = (item ->> 'id')::uuid AND m.site_code = NEW.site_code AND m.status <> 'Retired') THEN
            RAISE EXCEPTION 'playlist % names an advertisement that is not of its site''s library', NEW.id USING ERRCODE = 'check_violation';
        END IF;
        IF item ->> 'kind' = 'Slide' AND NOT EXISTS (
            SELECT 1 FROM instruction_slide s WHERE s.id = (item ->> 'id')::uuid AND s.site_code = NEW.site_code AND s.status = 'Active') THEN
            RAISE EXCEPTION 'playlist % names a slide that is not an active slide of its site', NEW.id USING ERRCODE = 'check_violation';
        END IF;
    END LOOP;
    RETURN NEW;
END;
$$;
ALTER FUNCTION advertising_playlist_guard() SET search_path = pg_catalog, public, pg_temp;

CREATE TRIGGER advertising_playlist_guard BEFORE INSERT OR UPDATE ON advertising_playlist
    FOR EACH ROW EXECUTE FUNCTION advertising_playlist_guard();

CREATE FUNCTION advertising_schedule_guard() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    targets jsonb;
BEGIN
    IF TG_OP = 'UPDATE' AND (NEW.id IS DISTINCT FROM OLD.id OR NEW.site_code IS DISTINCT FROM OLD.site_code) THEN
        RAISE EXCEPTION 'schedule % keeps its id and its site', OLD.id USING ERRCODE = 'check_violation';
    END IF;
    -- Every reader takes the targets as a list of names: anything else would fail the site's schedules and timelines.
    targets := NEW.targets::jsonb;
    IF jsonb_typeof(targets) <> 'array' OR jsonb_array_length(targets) > 20
        OR EXISTS (SELECT 1 FROM jsonb_array_elements(targets) t WHERE jsonb_typeof(t.value) <> 'string'
                   OR length(t.value #>> '{}') NOT BETWEEN 1 AND 200)
        OR (SELECT count(DISTINCT t.value) FROM jsonb_array_elements(targets) t) <> jsonb_array_length(targets) THEN
        RAISE EXCEPTION 'schedule % names 0 to 20 different zones or displays', NEW.id USING ERRCODE = 'check_violation';
    END IF;
    -- The list as parsed, not as written: "[ ]" is empty too.
    IF (NEW.scope = 'Site') <> (jsonb_array_length(targets) = 0) THEN
        RAISE EXCEPTION 'schedule % names targets for zones or displays and only then', NEW.id USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
ALTER FUNCTION advertising_schedule_guard() SET search_path = pg_catalog, public, pg_temp;

CREATE TRIGGER advertising_schedule_guard BEFORE INSERT OR UPDATE ON advertising_schedule
    FOR EACH ROW EXECUTE FUNCTION advertising_schedule_guard();
