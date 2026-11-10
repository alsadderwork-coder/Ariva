-- 0073 A signature on every row of the Data Protection key ring (ARV-211, the review's finding M1; docs/security/
-- key-management.md section 4.2a). The element signature (ES256 over the wrapped key, nonce, tag and ciphertext) proves
-- who wrapped a key's master key, but not the row around it: a writer with INSERT could copy a genuine encrypted secret
-- under another key id, other dates or another descriptor, or plant a revocation. Since ARV-211 the hosts sign the exact
-- text of each row they store, with its friendly name, using the key ring signing certificate (K49), and check that
-- signature before they parse the row (TrustedKeyRingRepository):
--
--   signature   "ES256:" then the signer's SHA-256 thumbprint (64 upper-case hex digits), ":" and the base64 of the
--               64-byte ECDSA P-256 signature (r and s). NULL on a row written before ARV-211, which the hosts read only
--               inside DataProtection:Signing:CertificateSignedUntil (wiki 04 section 6.2) and only to unprotect.
--
-- The runtime role keeps the grants of script 0002 (SELECT and INSERT; no UPDATE, so no writer can add a signature to an
-- existing row, and no DELETE). The shape is held here so a planted value is refused at INSERT and never reaches a log line.
ALTER TABLE data_protection_key ADD COLUMN signature text;

ALTER TABLE data_protection_key ADD CONSTRAINT data_protection_key_signature_shape
    CHECK (signature IS NULL OR signature ~ '^ES256:[0-9A-F]{64}:[A-Za-z0-9+/]{86}==$');
