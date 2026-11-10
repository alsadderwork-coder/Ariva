-- 0072 Retention of expired Data Protection keys (ARV-207, docs/security/key-management.md section 3.8 rule 3, crypto
-- inventory section 5 item 7). The key ring only grew: script 0002 gives the runtime role no DELETE on data_protection_key,
-- so that the hosts' runtime login can never destroy a key a stored value needs (a TOTP seed, an integration client seed, an
-- outbound secret: lost for good, short of a restore). This script keeps that, and adds one narrow path for the Cronz key
-- lifecycle job (KeyLifecycle):
--
--   ariva_data_protection_key_purge(after days, batch size)
--                               deletes at most one batch (1 to 1,000) of the keys that expired more than the given days
--                               ago (30 to 3,650; the floor is here, so the runtime login cannot shorten it) and that no
--                               stored value still needs, oldest first, and returns how many it deleted and how many keys
--                               it found active (unexpired), kept (expired within the period) and held (past the period
--                               and still in the table: a stored value names them, no unexpired key is left, or the batch
--                               was full). It deletes nothing while no unexpired key is in the table, so the key ring is
--                               never left without a key Data Protection could still fall back on.
--
-- What "needed" means. Every long-lived Data Protection payload is in one of three columns: "user".totp_secret_protected,
-- integration_client.totp_secret_protected and outbound_endpoint.secret_protected (the values the re-protection job
-- SecretReprotection moves to the current key every day). A payload is the base64url of a 4-byte magic header (09 F0 C9 F0)
-- and the 16 bytes of its key's id (in .NET's byte order) before the ciphertext, so the key a value needs is read from its
-- first 28 characters without unprotecting anything. A key is needed while any row of the three names it, whatever the row's
-- status (a disabled client or endpoint may be enabled again); a value that cannot be read (left by the job as unreadable)
-- keeps its key too. The short-lived payloads are covered by the period instead: a refresh token's successor is read only
-- within Auth:Sessions:RefreshGraceSeconds (30 s) of its use, a preview token within 2 hours, and a session (whose refresh
-- cookie holds no Data Protection payload) lasts 12 hours, all far inside the 30 days at the least.
--
-- What "a key" is. Only a row in the exact shape Data Protection writes is ever deleted: a key element whose text starts
-- with its id, version 1, its creation, activation and expiration dates (the expiration in UTC, Z or +00:00) and its
-- descriptor, matched with one bounded regular expression and compared as text (never parsed or cast, so a planted row
-- cannot make the function fail). Every other row stays: revocations among them (a revocation is never deleted, so a copy of
-- a revoked key can never become usable again), and any row a writer planted in another shape (the hosts skip those). Each
-- row is judged by its own dates, so deleting a planted copy of a key leaves the genuine row alone.
--
-- Runs as its owner (SECURITY DEFINER), with its search path pinned to pg_catalog and pg_temp (no public: every object in
-- the body is named with its schema) and its time zone to UTC; the period's floor and the shape are in the body, so the
-- runtime login can only remove what this rule already lets go. No dynamic SQL. EXECUTE goes to ariva_runtime only. DELETE
-- and TRUNCATE on data_protection_key stay revoked from the runtime role (0002). The keys of a database backup travel with it,
-- so deleting a key here never touches what a restore brings back (wiki 04 section 10).
CREATE FUNCTION ariva_data_protection_key_purge(p_after_days integer, p_limit integer)
    RETURNS TABLE (deleted integer, active integer, kept integer, held integer)
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
    SET TimeZone = 'UTC'
AS $$
DECLARE
    shape    constant text := '^<key id="([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})" version="1">'
                              || '<creationDate>[0-9T:.Z+-]{19,40}</creationDate><activationDate>[0-9T:.Z+-]{19,40}</activationDate>'
                              || '<expirationDate>([0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2})(?:\.[0-9]{1,7})?(?:Z|\+00:00)</expirationDate>'
                              || '<descriptor ';
    now_text constant text := pg_catalog.to_char(pg_catalog.now(), 'YYYY-MM-DD"T"HH24:MI:SS');
    cutoff   text;
BEGIN
    IF p_after_days IS NULL OR p_after_days < 30 OR p_after_days > 3650 THEN
        RAISE EXCEPTION 'an expired Data Protection key is kept 30 to 3650 days' USING ERRCODE = 'invalid_parameter_value';
    END IF;
    IF p_limit IS NULL OR p_limit < 1 OR p_limit > 1000 THEN
        RAISE EXCEPTION 'a key ring purge deletes 1 to 1000 keys' USING ERRCODE = 'invalid_parameter_value';
    END IF;
    cutoff := pg_catalog.to_char(pg_catalog.now() - pg_catalog.make_interval(days => p_after_days), 'YYYY-MM-DD"T"HH24:MI:SS');

    WITH keys AS (
        SELECT d.friendly_name, m[2] AS expires,
               -- The key id in the byte order a payload carries it (the first three groups reversed byte by byte).
               pg_catalog.substr(g, 7, 2) || pg_catalog.substr(g, 5, 2) || pg_catalog.substr(g, 3, 2) || pg_catalog.substr(g, 1, 2)
               || pg_catalog.substr(g, 11, 2) || pg_catalog.substr(g, 9, 2) || pg_catalog.substr(g, 15, 2) || pg_catalog.substr(g, 13, 2)
               || pg_catalog.substr(g, 17, 16) AS wire
          FROM public.data_protection_key d
         CROSS JOIN LATERAL pg_catalog.regexp_match(d.xml, shape) AS m
         CROSS JOIN LATERAL pg_catalog.replace(m[1], '-', '') AS g
         WHERE m IS NOT NULL),
    stored AS (
        SELECT pg_catalog.decode(pg_catalog.translate(pg_catalog.left(v.value, 28), '-_', '+/'), 'base64') AS head
          FROM (SELECT u.totp_secret_protected AS value FROM public."user" u
                UNION ALL SELECT c.totp_secret_protected FROM public.integration_client c
                UNION ALL SELECT o.secret_protected FROM public.outbound_endpoint o) v
         WHERE v.value ~ '^[A-Za-z0-9_-]{28}'),
    named AS (
        SELECT DISTINCT pg_catalog.encode(pg_catalog.substring(s.head, 5, 16), 'hex') AS wire
          FROM stored s
         WHERE pg_catalog.substring(s.head, 1, 4) = pg_catalog.decode('09f0c9f0', 'hex'))
    DELETE FROM public.data_protection_key d
     WHERE d.friendly_name IN (
           SELECT k.friendly_name FROM keys k
            WHERE k.expires < cutoff COLLATE "C"
              AND NOT EXISTS (SELECT 1 FROM named n WHERE n.wire = k.wire)
              AND EXISTS (SELECT 1 FROM keys a WHERE a.expires > now_text COLLATE "C")
            ORDER BY k.expires COLLATE "C", k.friendly_name COLLATE "C"
            LIMIT p_limit);
    GET DIAGNOSTICS deleted = ROW_COUNT;

    SELECT pg_catalog.count(*) FILTER (WHERE m[2] > now_text COLLATE "C"),
           pg_catalog.count(*) FILTER (WHERE m[2] <= now_text COLLATE "C" AND m[2] >= cutoff COLLATE "C"),
           pg_catalog.count(*) FILTER (WHERE m[2] < cutoff COLLATE "C")
      INTO active, kept, held
      FROM public.data_protection_key d
     CROSS JOIN LATERAL pg_catalog.regexp_match(d.xml, shape) AS m
     WHERE m IS NOT NULL;
    RETURN NEXT;
END
$$;

REVOKE ALL ON FUNCTION ariva_data_protection_key_purge(integer, integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION ariva_data_protection_key_purge(integer, integer) TO ariva_runtime;
