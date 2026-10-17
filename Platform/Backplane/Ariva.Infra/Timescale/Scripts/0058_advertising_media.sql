-- 0058 Advertisement media library (ARV-142a): images of a site, decoded and written anew on upload, kept in file storage
-- under a generated key, with who advertises, the campaign and the site's local days they may play. An item plays only once
-- approved, and approved only by a person other than its uploader and the last person who changed it (two people). The
-- service holds these rules; the checks and the trigger below hold them again for every path that writes a row:
--
--   advertising_media.storage_key       the generated key: 32 hex characters, a dot and the extension of content_type
--   advertising_media.starts_on, ends_on   the first and last local day of the site, "yyyy-MM-dd", at most 731 days apart
--   advertising_media.uploaded_by_id    the account that uploaded the item, which never approves it
--   advertising_media.changed_by_id     the account that last set its details, which never approves it either
--   advertising_media.approved_by_id    the approver, set exactly while the item is approved (and kept once retired)
--
-- The trigger refuses a new row that is not a draft its uploader last changed, and an update that changes the file, the
-- uploader or the creation stamps, changes a retired item, changes an approved item's details or approval while it stays
-- approved (a change sends it back to draft), approves an item and changes its details in the same write, or retires an
-- item and changes its details or approval in the same write. No approver is ever the uploader or the last changer, in any
-- state. The runtime role cannot delete a row: an item is retired, and its history stays.

CREATE TABLE advertising_media (
    id                  uuid           PRIMARY KEY,
    site_code           varchar(17)    NOT NULL REFERENCES site (code),
    storage_key         varchar(40)    NOT NULL UNIQUE CHECK (storage_key ~ '^[0-9a-f]{32}\.(png|jpg|webp)$'),
    content_type        varchar(32)    NOT NULL CHECK (content_type IN ('image/png', 'image/jpeg', 'image/webp')),
    size_bytes          bigint         NOT NULL CHECK (size_bytes BETWEEN 1 AND 10485760),
    sha256              varchar(64)    NOT NULL CHECK (sha256 ~ '^[0-9a-f]{64}$'),
    width_pixels        integer        NOT NULL CHECK (width_pixels BETWEEN 1 AND 3840),
    height_pixels       integer        NOT NULL CHECK (height_pixels BETWEEN 1 AND 3840),
    original_file_name  varchar(200),
    advertiser          varchar(120)   NOT NULL CHECK (length(trim(advertiser)) >= 1),
    campaign            varchar(120)   NOT NULL CHECK (length(trim(campaign)) >= 1),
    starts_on           varchar(10)    NOT NULL CHECK (starts_on ~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$'),
    ends_on             varchar(10)    NOT NULL CHECK (ends_on ~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$'),
    status              varchar(16)    NOT NULL CHECK (status IN ('Draft', 'Approved', 'Retired')),
    uploaded_by_id      uuid           NOT NULL,
    changed_by_id       uuid           NOT NULL,
    approved_by_id      uuid,
    approved_by         varchar(200),
    approved_on         timestamptz,
    retired_on          timestamptz,
    created_by          varchar(200),
    created_on          timestamptz,
    created_by_id       uuid,
    modified_by         varchar(200),
    modified_on         timestamptz,
    modified_by_id      uuid,
    CONSTRAINT ck_advertising_media_pixels CHECK (width_pixels::bigint * height_pixels <= 8294400),
    CONSTRAINT ck_advertising_media_extension CHECK (
        right(storage_key, length(storage_key) - 33) = CASE content_type WHEN 'image/png' THEN 'png' WHEN 'image/jpeg' THEN 'jpg' ELSE 'webp' END),
    CONSTRAINT ck_advertising_media_dates CHECK (
        ends_on >= starts_on AND to_date(ends_on, 'YYYY-MM-DD') - to_date(starts_on, 'YYYY-MM-DD') < 731),
    CONSTRAINT ck_advertising_media_draft CHECK (
        status <> 'Draft' OR (approved_by_id IS NULL AND approved_by IS NULL AND approved_on IS NULL AND retired_on IS NULL)),
    CONSTRAINT ck_advertising_media_two_people CHECK (
        status <> 'Approved' OR (approved_by_id IS NOT NULL AND approved_by IS NOT NULL AND approved_on IS NOT NULL AND retired_on IS NULL)),
    CONSTRAINT ck_advertising_media_approver CHECK (
        approved_by_id IS NULL OR (approved_by_id <> uploaded_by_id AND approved_by_id <> changed_by_id)),
    CONSTRAINT ck_advertising_media_retired CHECK (status <> 'Retired' OR retired_on IS NOT NULL)
);
CREATE INDEX ix_advertising_media_site ON advertising_media (site_code, created_on);

REVOKE DELETE, TRUNCATE ON advertising_media FROM ariva_runtime;

CREATE FUNCTION advertising_media_guard() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    details_changed boolean;
    approval_changed boolean;
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.status <> 'Draft' OR NEW.changed_by_id IS DISTINCT FROM NEW.uploaded_by_id THEN
            RAISE EXCEPTION 'advertisement % starts as a draft its uploader last changed', NEW.id USING ERRCODE = 'check_violation';
        END IF;
        RETURN NEW;
    END IF;
    details_changed := NEW.advertiser IS DISTINCT FROM OLD.advertiser OR NEW.campaign IS DISTINCT FROM OLD.campaign
        OR NEW.starts_on IS DISTINCT FROM OLD.starts_on OR NEW.ends_on IS DISTINCT FROM OLD.ends_on
        OR NEW.changed_by_id IS DISTINCT FROM OLD.changed_by_id;
    approval_changed := NEW.approved_by_id IS DISTINCT FROM OLD.approved_by_id OR NEW.approved_by IS DISTINCT FROM OLD.approved_by
        OR NEW.approved_on IS DISTINCT FROM OLD.approved_on;
    IF NEW.id IS DISTINCT FROM OLD.id OR NEW.site_code IS DISTINCT FROM OLD.site_code OR NEW.storage_key IS DISTINCT FROM OLD.storage_key
        OR NEW.content_type IS DISTINCT FROM OLD.content_type OR NEW.size_bytes IS DISTINCT FROM OLD.size_bytes
        OR NEW.sha256 IS DISTINCT FROM OLD.sha256 OR NEW.width_pixels IS DISTINCT FROM OLD.width_pixels
        OR NEW.height_pixels IS DISTINCT FROM OLD.height_pixels OR NEW.original_file_name IS DISTINCT FROM OLD.original_file_name
        OR NEW.uploaded_by_id IS DISTINCT FROM OLD.uploaded_by_id OR NEW.created_by IS DISTINCT FROM OLD.created_by
        OR NEW.created_by_id IS DISTINCT FROM OLD.created_by_id OR NEW.created_on IS DISTINCT FROM OLD.created_on THEN
        RAISE EXCEPTION 'advertisement % keeps its file, its uploader and its creation stamps', OLD.id USING ERRCODE = 'check_violation';
    END IF;
    IF OLD.status = 'Retired' AND (NEW.status <> 'Retired' OR details_changed OR approval_changed OR NEW.retired_on IS DISTINCT FROM OLD.retired_on) THEN
        RAISE EXCEPTION 'advertisement % is retired', OLD.id USING ERRCODE = 'check_violation';
    END IF;
    IF OLD.status = 'Approved' AND NEW.status = 'Approved' AND (details_changed OR approval_changed) THEN
        RAISE EXCEPTION 'advertisement % is approved: a change sends it back to draft', OLD.id USING ERRCODE = 'check_violation';
    END IF;
    IF OLD.status <> 'Approved' AND NEW.status = 'Approved' AND details_changed THEN
        RAISE EXCEPTION 'advertisement % is approved as it was seen, never changed in the same write', OLD.id USING ERRCODE = 'check_violation';
    END IF;
    IF OLD.status <> 'Retired' AND NEW.status = 'Retired' AND (details_changed OR approval_changed) THEN
        RAISE EXCEPTION 'advertisement % is retired as it was, never changed in the same write', OLD.id USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
ALTER FUNCTION advertising_media_guard() SET search_path = pg_catalog, public, pg_temp;

CREATE TRIGGER advertising_media_guard BEFORE INSERT OR UPDATE ON advertising_media
    FOR EACH ROW EXECUTE FUNCTION advertising_media_guard();
