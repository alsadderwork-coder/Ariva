-- 0067 The lines a device declared it counts, on the device health archive (ARV-259e5, ADR-0027).
-- Ingest stamps each device health report with the entry and exit lines of its queue zone the device declares it counts
-- (device.line_coverage and device.covered_lines, script 0066): NULL when it counts every line (All, and every report
-- archived before this script), an empty array when it counts none (None), else the names (Listed). A replay reads them
-- back with the reports, so live processing, replay and recomputation see the same declarations. At most 64 names (the
-- registry keeps 16), none NULL or empty, in one dimension; the archive's writer also holds them to the registry's shape.
-- TimescaleDB takes the column and the constraint on a hypertable with compressed chunks: rows already compressed read as
-- NULL (All).
ALTER TABLE device_health_event ADD COLUMN covered_lines text[];
ALTER TABLE device_health_event ADD CONSTRAINT ck_device_health_event_covered_lines CHECK (
    covered_lines IS NULL
    OR (cardinality(covered_lines) <= 64
        AND coalesce(array_ndims(covered_lines), 1) = 1
        AND array_position(covered_lines, NULL) IS NULL
        AND NOT ('' = ANY (covered_lines))
        AND length(array_to_string(covered_lines, ',')) <= 12863));
