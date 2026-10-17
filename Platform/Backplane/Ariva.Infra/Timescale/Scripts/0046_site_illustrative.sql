-- 0046 The "Illustrative, not surveyed" flag of a site (ARV-139a). A demo site modelled on a real airport from public
-- information only (AUH-TA) carries it; the web shell and the site's passenger displays show a banner while it is set.
--
--   site.is_illustrative   true for a demo site whose geometry, counts and positions are assumptions
--
-- Privileges (CWE-269): only the demo seeds set the flag, when they insert the site. No request model carries it and no
-- entity method changes it; here the runtime role loses UPDATE on the table and gets it back on the columns a rename
-- writes (NHibernate updates only the changed columns), so no runtime statement can set or clear the flag of an existing
-- site. SELECT ... FOR UPDATE on site (the zone profile API and the seeds lock the row) needs UPDATE on one column only,
-- which the grant below keeps. DELETE and TRUNCATE stay revoked (0007).
ALTER TABLE site ADD COLUMN is_illustrative boolean NOT NULL DEFAULT false;

REVOKE UPDATE ON site FROM ariva_runtime;
GRANT UPDATE (name, modified_by, modified_on, modified_by_id) ON site TO ariva_runtime;
