-- 0050 Validation results frozen at campaign close (ARV-104g; formulas F18 "Campaign verdicts"; wiki 07 section 8 and wiki 15
-- section 8). When a campaign closes, its results (each pilot criterion against its target with the comparison behind it,
-- availability, calibration records, the profile version and the geometry hash) are frozen as revision 1 of a JSON document with
-- a SHA-256 content hash; a later recomputation adds the next revision with a reason and who asked for it. Nothing is ever
-- edited or deleted: a revision is evidence of the pilot's acceptance criteria, kept indefinitely like the campaign.
--
--   validation_result_revision  one row per revision of a campaign's results: the document's exact bytes and their hash
--
-- The document holds every section of the results: desk-state results (border per-desk data), observer-level results
-- (observers as Ariva user ids only, never a name or document) and the shadow nowcast's zone-level figures (validation data:
-- error statistics per zone and over every zone, no minute and no stored value of the shadow itself). Only the validation
-- results service reads this table (a source scan, ValidationResultsExposureTests) and it serves a document only through the
-- per-caller projection: desks to border roles of the campaign's site, observer-level results to Validation.View or
-- Validation.Manage holders, the shadow's figures to Validation.View holders (data boundary). Never to AMAN or the feed.

CREATE TABLE validation_result_revision (
    id              uuid          PRIMARY KEY,
    campaign_id     uuid          NOT NULL,
    site_code       varchar(17)   NOT NULL,
    revision        integer       NOT NULL CHECK (revision BETWEEN 1 AND 1000),
    reason          varchar(500)  CHECK (reason IS NULL OR length(btrim(reason)) BETWEEN 1 AND 500),
    document        bytea         NOT NULL CHECK (octet_length(document) BETWEEN 2 AND 16777216),
    content_sha256  char(64)      NOT NULL CHECK (content_sha256 ~ '^[0-9a-f]{64}$'),
    computed_utc    timestamptz   NOT NULL,
    frozen_utc      timestamptz   NOT NULL,
    frozen_by_id    uuid          REFERENCES "user" (id),
    FOREIGN KEY (campaign_id, site_code) REFERENCES validation_campaign (id, site_code),
    UNIQUE (campaign_id, revision),
    -- The hash is of the stored bytes, checked here so that no writer can store a hash of anything else.
    CHECK (content_sha256 = encode(sha256(document), 'hex')),
    -- Revision 1 is the freeze at close, by the system and without a reason; every later one is a recomputation a manager asked
    -- for, with a reason.
    CHECK ((revision = 1) = (reason IS NULL)),
    CHECK ((revision = 1) = (frozen_by_id IS NULL))
);

-- Revisions come only for a closed campaign (FOR SHARE: a close in progress commits first), and none skips a number: a revision
-- beyond the campaign's latest plus one is refused. A number already taken is left to the unique key, so that the freeze's
-- ON CONFLICT DO NOTHING keeps a revision 1 another replica committed meanwhile, and two recomputations at once meet 23505.
CREATE FUNCTION validation_result_revision_insert_check() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF (SELECT status FROM validation_campaign WHERE id = NEW.campaign_id AND site_code = NEW.site_code FOR SHARE) IS DISTINCT FROM 'Closed' THEN
        RAISE EXCEPTION 'the results of validation campaign % are frozen once it is closed', NEW.campaign_id USING ERRCODE = 'restrict_violation';
    END IF;
    IF NEW.revision > COALESCE((SELECT max(revision) FROM validation_result_revision WHERE campaign_id = NEW.campaign_id), 0) + 1 THEN
        RAISE EXCEPTION 'a revision of validation results is the next after the campaign''s latest' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER validation_result_revision_insert_check BEFORE INSERT ON validation_result_revision
    FOR EACH ROW EXECUTE FUNCTION validation_result_revision_insert_check();

-- A revision never changes and is never deleted, even by a bug or a hand-written statement (the function of script 0047).
CREATE TRIGGER validation_result_revision_immutable BEFORE UPDATE OR DELETE ON validation_result_revision
    FOR EACH ROW EXECUTE FUNCTION validation_row_immutable();

-- The runtime role (default privileges of 0001) inserts and reads; it never updates, deletes or truncates a revision (CWE-269).
REVOKE UPDATE, DELETE, TRUNCATE ON validation_result_revision FROM ariva_runtime;
