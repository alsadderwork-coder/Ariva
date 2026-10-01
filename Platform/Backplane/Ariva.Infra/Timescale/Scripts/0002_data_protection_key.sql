-- 0002 ASP.NET Core Data Protection key ring (ARV-008).
-- Every host reads the same keys, so a cookie or token protected by one pod is readable by the others. The key XML is
-- encrypted with the certificate from the ariva-dataprotection secret before it reaches this table: a database dump
-- or a read-only login does not reveal the keys.
CREATE TABLE data_protection_key (
    friendly_name text        PRIMARY KEY,
    xml           text        NOT NULL,
    created_on    timestamptz NOT NULL DEFAULT now()
);

-- Keys are added, never changed or removed by the application (revocation is an added element).
REVOKE UPDATE, DELETE, TRUNCATE ON data_protection_key FROM ariva_runtime;
