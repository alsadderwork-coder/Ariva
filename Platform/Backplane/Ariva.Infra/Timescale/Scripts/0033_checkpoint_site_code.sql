-- 0033 Checkpoint codes unique per site (ARV-055): a desk is known across Ariva by site/checkpoint/desk (the key of
-- desk_minute, written by Ariva.Api.Stream), so two live checkpoints of one site on different levels must not share a
-- code; otherwise one desk's minutes could be read as another's, a border desk's as a check-in counter's.

DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM checkpoint WHERE deleted_on IS NULL GROUP BY site_code, code HAVING count(*) > 1
    ) THEN
        RAISE EXCEPTION 'Two live checkpoints of one site share a code. Rename or delete one before this script runs.';
    END IF;
END
$$;

CREATE UNIQUE INDEX ux_checkpoint_site_code ON checkpoint (site_code, code) WHERE deleted_on IS NULL;
