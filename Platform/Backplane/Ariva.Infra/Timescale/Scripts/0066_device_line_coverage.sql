-- 0066 Device line coverage (ARV-259e4, ADR-0027): which entry and exit lines of its queue zone a device counts.
--
--   line_coverage   All (every entry and exit line of the device's queue zone; every device registered before this script),
--                   Listed (the lines in covered_lines) or None (a desk zone or band sensor that counts no queue line).
--   covered_lines   the lines a Listed device counts: 1 to 16 line names in ordinal order, separated by line feeds (a name
--                   holds no control character, ARV-159, so the separator is never part of one); null for All and None. The
--                   registry checks that each is an entry or exit line of the device's zone when it is declared; a later
--                   publish that drops one leaves it here, and the devices screen and the publish answer warn.
ALTER TABLE device
    ADD COLUMN line_coverage  varchar(100)   NOT NULL DEFAULT 'All',
    ADD COLUMN covered_lines  varchar(3216);

-- Written so that a restored dump prints them as the migration did (the backup check compares the two): the kinds compared as
-- text, and the bounds as one flat AND (BETWEEN and a varchar IN list print differently once read back).
ALTER TABLE device
    ADD CONSTRAINT device_line_coverage_check CHECK (line_coverage::text IN ('All', 'Listed', 'None')),
    ADD CONSTRAINT device_covered_lines_listed CHECK (
        (line_coverage = 'Listed') = (covered_lines IS NOT NULL)
        AND (covered_lines IS NULL
             OR (cardinality(string_to_array(covered_lines, E'\n')) >= 1
                 AND cardinality(string_to_array(covered_lines, E'\n')) <= 16
                 AND NOT ('' = ANY (string_to_array(covered_lines, E'\n'))))));
