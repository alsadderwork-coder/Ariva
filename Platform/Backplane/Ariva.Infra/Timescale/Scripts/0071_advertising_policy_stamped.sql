-- 0071 Advertising policy changes held again by the database (ARV-273a, CWE-863, CWE-269). Since ARV-273 a change of a
-- site's advertising policy names the version it was made from, its last change to the microsecond, and the service
-- refuses one made from another version under the site's lock; every change of a row is stamped after its last stamp
-- (AuditInterceptor.After). The runtime role keeps UPDATE on advertising_policy (the service changes the policy through it;
-- script 0057 revokes DELETE and TRUNCATE only), so a direct write through the runtime login could store a change under the
-- version it replaced, or without who made it. This trigger refuses, for every writer, an update of a policy whose
-- modified_on is not after the row's last stamp (the later of its last change and its creation, compared to the
-- microsecond PostgreSQL keeps), whose modified_by_id is missing, or that changes when or by whom the policy was created.
-- A refused update raises check_violation and changes nothing.

CREATE FUNCTION advertising_policy_stamped() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.modified_on IS NULL OR NEW.modified_by_id IS NULL
        OR NEW.modified_on <= greatest(OLD.modified_on, OLD.created_on)
        OR NEW.created_on IS DISTINCT FROM OLD.created_on OR NEW.created_by_id IS DISTINCT FROM OLD.created_by_id THEN
        RAISE EXCEPTION 'a change of the advertising policy of site % is stamped after its last change, with who made it', OLD.site_code
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
ALTER FUNCTION advertising_policy_stamped() SET search_path = pg_catalog, public, pg_temp;

CREATE TRIGGER advertising_policy_stamped BEFORE UPDATE ON advertising_policy
    FOR EACH ROW EXECUTE FUNCTION advertising_policy_stamped();
