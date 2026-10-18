-- 0059 Advertisement videos (ARV-142b): the library of script 0058 takes MP4 and WebM videos as well as images. A video's
-- container is checked and stripped of its metadata in place by the service, which holds it to the size and length the
-- installation sets (Advertising:Videos); the checks below hold the ceilings every installation keeps, for every path
-- that writes a row:
--
--   advertising_media.duration_milliseconds   a video's length, 1 ms to 120 s; null for an image, never null for a video
--   advertising_media.size_bytes              an image at most 10 MB, a video at most 100 MB
--   advertising_media.storage_key             the generated key: 32 hex characters, a dot and the extension of content_type
--
-- The guard trigger of script 0058 is replaced by the same function with the length among what an item keeps: like its
-- file, a video's length never changes once uploaded.

ALTER TABLE advertising_media ADD COLUMN duration_milliseconds integer CHECK (duration_milliseconds BETWEEN 1 AND 120000);

ALTER TABLE advertising_media
    DROP CONSTRAINT advertising_media_storage_key_check,
    DROP CONSTRAINT advertising_media_content_type_check,
    DROP CONSTRAINT advertising_media_size_bytes_check,
    DROP CONSTRAINT ck_advertising_media_extension,
    ADD CONSTRAINT advertising_media_storage_key_check CHECK (storage_key ~ '^[0-9a-f]{32}\.(png|jpg|webp|mp4|webm)$'),
    ADD CONSTRAINT advertising_media_content_type_check CHECK (
        content_type IN ('image/png', 'image/jpeg', 'image/webp', 'video/mp4', 'video/webm')),
    ADD CONSTRAINT advertising_media_size_bytes_check CHECK (
        size_bytes BETWEEN 1 AND CASE WHEN content_type IN ('video/mp4', 'video/webm') THEN 104857600 ELSE 10485760 END),
    ADD CONSTRAINT ck_advertising_media_extension CHECK (
        right(storage_key, length(storage_key) - 33) = CASE content_type
            WHEN 'image/png' THEN 'png' WHEN 'image/jpeg' THEN 'jpg' WHEN 'image/webp' THEN 'webp'
            WHEN 'video/mp4' THEN 'mp4' WHEN 'video/webm' THEN 'webm' ELSE '' END),
    ADD CONSTRAINT ck_advertising_media_duration CHECK (
        (content_type IN ('video/mp4', 'video/webm')) = (duration_milliseconds IS NOT NULL));

CREATE OR REPLACE FUNCTION advertising_media_guard() RETURNS trigger LANGUAGE plpgsql AS $$
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
        OR NEW.height_pixels IS DISTINCT FROM OLD.height_pixels OR NEW.duration_milliseconds IS DISTINCT FROM OLD.duration_milliseconds
        OR NEW.original_file_name IS DISTINCT FROM OLD.original_file_name
        OR NEW.uploaded_by_id IS DISTINCT FROM OLD.uploaded_by_id OR NEW.created_by IS DISTINCT FROM OLD.created_by
        OR NEW.created_by_id IS DISTINCT FROM OLD.created_by_id OR NEW.created_on IS DISTINCT FROM OLD.created_on THEN
        RAISE EXCEPTION 'advertisement % keeps its file, its length, its uploader and its creation stamps', OLD.id USING ERRCODE = 'check_violation';
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
