-- 0014 Device access (ARV-022): where a device may push from and the client certificate it must present. Sources are
-- CIDR blocks separated by commas (empty: any address); the certificate is pinned by its SHA-256.
ALTER TABLE device
    ADD COLUMN allowed_sources            varchar(1000),
    ADD COLUMN client_certificate_sha256  varchar(64) CHECK (client_certificate_sha256 ~ '^[0-9a-f]{64}$');
