-- 0012 Zone profiles (ARV-016 aggregate, ARV-017 workflow): versions of a site's zones and lines. At most one draft
-- and one published version per site; version numbers are unique per site. A published or retired version never
-- changes: the triggers below refuse any change to its geometry, so the evidence behind an SLA period cannot be edited
-- even by a bug or a hand-written statement (the only change allowed is Published to Retired).
CREATE TABLE zone_profile (
    id                uuid          PRIMARY KEY,
    site_code         varchar(17)   NOT NULL REFERENCES site (code),
    name              varchar(200)  NOT NULL,
    status            varchar(100)  NOT NULL CHECK (status IN ('Draft', 'Published', 'Retired')),
    version           integer       CHECK (version >= 1),
    based_on_version  integer,
    geometry_hash     varchar(64)   CHECK (geometry_hash ~ '^[0-9a-f]{64}$'),
    published_on      timestamptz,
    published_by      varchar(200),
    retired_on        timestamptz,
    created_by        varchar(200),
    created_on        timestamptz,
    created_by_id     uuid,
    modified_by       varchar(200),
    modified_on       timestamptz,
    modified_by_id    uuid,
    CHECK ((status = 'Draft') = (version IS NULL)),
    CHECK (status = 'Draft' OR (geometry_hash IS NOT NULL AND published_on IS NOT NULL))
);

CREATE UNIQUE INDEX ux_zone_profile_site_version ON zone_profile (site_code, version) WHERE version IS NOT NULL;
CREATE UNIQUE INDEX ux_zone_profile_site_draft ON zone_profile (site_code) WHERE status = 'Draft';
CREATE UNIQUE INDEX ux_zone_profile_site_published ON zone_profile (site_code) WHERE status = 'Published';

CREATE TABLE zone (
    id              uuid          PRIMARY KEY,
    profile_id      uuid          NOT NULL REFERENCES zone_profile (id),
    name            varchar(200)  NOT NULL,
    kind            varchar(100)  NOT NULL CHECK (kind IN ('Queue', 'Service', 'Staff', 'Overflow')),
    level_id        uuid          NOT NULL REFERENCES level (id),
    queue_zone_id   uuid,
    desk_id         uuid          REFERENCES desk (id),
    polygon         varchar(4000) NOT NULL,
    UNIQUE (profile_id, id),
    -- A zone hangs off a queue zone of the same profile.
    FOREIGN KEY (profile_id, queue_zone_id) REFERENCES zone (profile_id, id)
);

CREATE INDEX ix_zone_profile ON zone (profile_id);
CREATE UNIQUE INDEX ux_zone_profile_name ON zone (profile_id, lower(name));

CREATE TABLE line (
    id          uuid              PRIMARY KEY,
    profile_id  uuid              NOT NULL REFERENCES zone_profile (id),
    name        varchar(200)      NOT NULL,
    role        varchar(100)      NOT NULL CHECK (role IN ('Entry', 'Exit', 'Count', 'OverflowEntry')),
    zone_id     uuid,
    level_id    uuid              NOT NULL REFERENCES level (id),
    start_x     double precision  NOT NULL,
    start_y     double precision  NOT NULL,
    end_x       double precision  NOT NULL,
    end_y       double precision  NOT NULL,
    -- A line belongs to a zone of the same profile.
    FOREIGN KEY (profile_id, zone_id) REFERENCES zone (profile_id, id)
);

CREATE INDEX ix_line_profile ON line (profile_id);
CREATE UNIQUE INDEX ux_line_profile_name ON line (profile_id, lower(name));

-- A published version: only Published to Retired (setting retired_on once) and the audit columns may change; nothing is
-- deleted. A raw update can still move a draft to Published with any hash; the service is the only writer that hashes.
CREATE FUNCTION zone_profile_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        IF OLD.status <> 'Draft' THEN
            RAISE EXCEPTION 'zone profile % version % is published or retired and cannot be deleted', OLD.id, OLD.version
                USING ERRCODE = 'restrict_violation';
        END IF;
        RETURN OLD;
    END IF;

    IF OLD.status <> 'Draft' AND (
           NEW.site_code IS DISTINCT FROM OLD.site_code OR NEW.name IS DISTINCT FROM OLD.name
        OR NEW.version IS DISTINCT FROM OLD.version OR NEW.based_on_version IS DISTINCT FROM OLD.based_on_version
        OR NEW.geometry_hash IS DISTINCT FROM OLD.geometry_hash OR NEW.published_on IS DISTINCT FROM OLD.published_on
        OR NEW.published_by IS DISTINCT FROM OLD.published_by
        OR NOT (NEW.status = OLD.status OR (OLD.status = 'Published' AND NEW.status = 'Retired'))
        OR (NEW.retired_on IS DISTINCT FROM OLD.retired_on
            AND NOT (OLD.status = 'Published' AND NEW.status = 'Retired' AND OLD.retired_on IS NULL AND NEW.retired_on IS NOT NULL))) THEN
        RAISE EXCEPTION 'zone profile % version % is published or retired and never changes', OLD.id, OLD.version
            USING ERRCODE = 'restrict_violation';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER zone_profile_immutable BEFORE UPDATE OR DELETE ON zone_profile
    FOR EACH ROW EXECUTE FUNCTION zone_profile_immutable();

-- Zones and lines change only while their profile is a draft.
CREATE FUNCTION zone_profile_geometry_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    profile uuid := CASE WHEN TG_OP = 'DELETE' THEN OLD.profile_id ELSE NEW.profile_id END;
BEGIN
    IF TG_OP = 'UPDATE' AND NEW.profile_id IS DISTINCT FROM OLD.profile_id THEN
        RAISE EXCEPTION 'a zone or line stays in its profile' USING ERRCODE = 'restrict_violation';
    END IF;
    -- FOR SHARE waits for a publish in progress on the profile, so no zone or line slips in after its validation.
    IF (SELECT status FROM zone_profile WHERE id = profile FOR SHARE) <> 'Draft' THEN
        RAISE EXCEPTION 'zone profile % is published or retired; its zones and lines never change', profile
            USING ERRCODE = 'restrict_violation';
    END IF;
    RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
END;
$$;

CREATE TRIGGER zone_immutable BEFORE INSERT OR UPDATE OR DELETE ON zone
    FOR EACH ROW EXECUTE FUNCTION zone_profile_geometry_immutable();
CREATE TRIGGER line_immutable BEFORE INSERT OR UPDATE OR DELETE ON line
    FOR EACH ROW EXECUTE FUNCTION zone_profile_geometry_immutable();

REVOKE TRUNCATE ON zone_profile, zone, line FROM ariva_runtime;
