-- 0071 Advertising policy changes held again by the database (ARV-273a, CWE-863, CWE-269). Since ARV-273 a change of a
-- site's advertising policy names the version it was made from, its last change to the microsecond, and the service
-- refuses one made from another version under the site's lock; every change of a row is stamped after its last stamp
-- (AuditInterceptor.After). The runtime role keeps INSERT and UPDATE on advertising_policy (the service writes the policy
-- through it; script 0057 revokes DELETE and TRUNCATE only), so a direct write through the runtime login could store a
-- change under the version it replaced, or without who made it. This trigger refuses, for every writer:
--   - a new policy without when and by whom it was created (its version would read as "no policy", and a form opened on a
--     site without one would save over it unseen), or with a change stamp before its creation or without who made it;
--   - an update whose modified_on is not after the row's last stamp (the later of its last change and its creation,
--     compared to the microsecond PostgreSQL keeps), whose modified_by_id is missing, or that changes when or by whom the
--     policy was created;
--   - a stamp that is not finite, or more than a day ahead of the database's clock (the pods' clock skew it tolerates): every
--     later change must be stamped after it, so one such write would hold the policy as it is for good.
-- A refused write raises check_violation and changes nothing. A restore creates the trigger after the rows (pg_restore
-- restores data before triggers).

CREATE FUNCTION advertising_policy_stamped() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    latest constant timestamptz := clock_timestamp() + interval '1 day';
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.created_on IS NULL OR NEW.created_by_id IS NULL OR NOT isfinite(NEW.created_on) OR NEW.created_on > latest
            OR (NEW.modified_on IS NOT NULL AND (NOT isfinite(NEW.modified_on) OR NEW.modified_on > latest
                                                 OR NEW.modified_on < NEW.created_on OR NEW.modified_by_id IS NULL)) THEN
            RAISE EXCEPTION 'a new advertising policy of site % is stamped with when and by whom it was created, at most a day ahead of the database clock', NEW.site_code
                USING ERRCODE = 'check_violation';
        END IF;
    ELSIF NEW.modified_on IS NULL OR NEW.modified_by_id IS NULL OR NOT isfinite(NEW.modified_on) OR NEW.modified_on > latest
        OR NEW.modified_on <= greatest(OLD.modified_on, OLD.created_on)
        OR NEW.created_on IS DISTINCT FROM OLD.created_on OR NEW.created_by_id IS DISTINCT FROM OLD.created_by_id THEN
        RAISE EXCEPTION 'a change of the advertising policy of site % is stamped after its last change, with who made it, at most a day ahead of the database clock', OLD.site_code
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
ALTER FUNCTION advertising_policy_stamped() SET search_path = pg_catalog, public, pg_temp;

CREATE TRIGGER advertising_policy_stamped BEFORE INSERT OR UPDATE ON advertising_policy
    FOR EACH ROW EXECUTE FUNCTION advertising_policy_stamped();
