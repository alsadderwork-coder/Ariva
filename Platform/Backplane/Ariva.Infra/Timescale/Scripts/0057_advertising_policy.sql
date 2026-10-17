-- 0057 Advertising policy per site (ARV-145): whether a site's passenger displays may show advertising in their second
-- region, and the queue zones where it stays off even where the site allows it. A site without a row has advertising off.
-- Only a system administrator changes it, with a second factor, and the audit trail keeps every change with its reason;
-- the runtime role cannot delete a row (turning advertising off is a change, audited like any other).
--
--   advertising_policy.allowed          whether the site's displays may show advertising
--   advertising_policy.excluded_zones   the queue zone names, as a JSON array, where it stays off
--   advertising_policy.reason           why the last change was made: who authorised it and their reference
--   advertising_policy.confirmed_profile_id   the site's published zone profile when the policy was last saved; the
--                                       exclusions were judged against its zones, so once another profile is published
--                                       (zones renamed, moved, dropped or added by the roles that edit profiles) no display
--                                       of the site shows advertising until an administrator saves the policy again
--
-- ck_display_second_region (script 0055) now allows Advertising, and a trigger refuses a display set to advertising
-- (inserted, changed to advertising, given other zones or restored) unless its site's policy allows it: allowed, saved
-- against the site's published zone profile of now, every excluded zone a queue zone of that profile, every zone the
-- display shows exactly a queue zone of that profile (every key named zone in any case, and at least one per entry), and
-- none of them excluded. The
-- rule of the display service, for every path that writes a display. It reads the policy row FOR SHARE, so a change of the
-- policy in progress commits first. A display set to advertising before its policy or profile changed keeps the setting;
-- its board shows instructions instead.

CREATE TABLE advertising_policy (
    id              uuid           PRIMARY KEY,
    site_code       varchar(17)    NOT NULL REFERENCES site (code),
    allowed         boolean        NOT NULL,
    excluded_zones  varchar(8000)  NOT NULL CHECK (left(excluded_zones, 1) = '['),
    reason          varchar(500)   NOT NULL CHECK (length(trim(reason)) >= 10),
    confirmed_profile_id  uuid     REFERENCES zone_profile (id),
    created_by      varchar(200),
    created_on      timestamptz,
    created_by_id   uuid,
    modified_by     varchar(200),
    modified_on     timestamptz,
    modified_by_id  uuid
);
CREATE UNIQUE INDEX ux_advertising_policy_site ON advertising_policy (site_code);

REVOKE DELETE, TRUNCATE ON advertising_policy FROM ariva_runtime;

ALTER TABLE display DROP CONSTRAINT ck_display_second_region;
ALTER TABLE display ADD CONSTRAINT ck_display_second_region CHECK (second_region IN ('Instructions', 'Advertising'));

CREATE FUNCTION display_advertising_allowed() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    policy public.advertising_policy%ROWTYPE;
    published uuid;
BEGIN
    IF NEW.second_region <> 'Advertising' OR NEW.deleted_on IS NOT NULL THEN
        RETURN NEW;
    END IF;
    IF TG_OP = 'UPDATE' AND OLD.second_region = NEW.second_region AND OLD.entries = NEW.entries AND OLD.site_code = NEW.site_code
        AND OLD.deleted_on IS NULL THEN
        RETURN NEW;
    END IF;
    SELECT * INTO policy FROM public.advertising_policy WHERE site_code = NEW.site_code FOR SHARE;
    -- One published profile per site (a unique index); none leaves published NULL. A missing policy leaves policy.id NULL.
    SELECT id INTO published FROM public.zone_profile WHERE site_code = NEW.site_code AND status = 'Published';
    IF policy.id IS NULL OR NOT policy.allowed OR published IS NULL OR policy.confirmed_profile_id IS DISTINCT FROM published
        OR EXISTS (
            SELECT 1 FROM jsonb_array_elements_text(policy.excluded_zones::jsonb) excluded
            WHERE NOT EXISTS (SELECT 1 FROM public.zone z WHERE z.profile_id = published AND z.kind = 'Queue' AND z.name = excluded))
        OR EXISTS (
            SELECT 1 FROM jsonb_array_elements(NEW.entries::jsonb) entry
            WHERE NOT EXISTS (SELECT 1 FROM jsonb_each_text(entry) field WHERE lower(field.key) = 'zone')
               OR EXISTS (
                   SELECT 1 FROM jsonb_each_text(entry) field
                   WHERE lower(field.key) = 'zone'
                     AND NOT EXISTS (SELECT 1 FROM public.zone z WHERE z.profile_id = published AND z.kind = 'Queue' AND z.name = field.value)))
        OR EXISTS (
            SELECT 1 FROM jsonb_array_elements(NEW.entries::jsonb) entry, jsonb_each_text(entry) field
            WHERE lower(field.key) = 'zone' AND field.value IN (SELECT jsonb_array_elements_text(policy.excluded_zones::jsonb))) THEN
        RAISE EXCEPTION 'advertising is off for the site of display % or for one of its zones', NEW.code USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
ALTER FUNCTION display_advertising_allowed() SET search_path = pg_catalog, public, pg_temp;

CREATE TRIGGER display_advertising_allowed BEFORE INSERT OR UPDATE ON display
    FOR EACH ROW EXECUTE FUNCTION display_advertising_allowed();
