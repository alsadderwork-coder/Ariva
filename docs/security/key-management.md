# Key management policy

How every cryptographic key, secret and certificate of an Ariva deployment is owned, made, given out, held, used, rotated, recovered, revoked and destroyed, following the key lifecycle of NIST SP 800-57 Part 1 (ASVS 5.0 V11.1.1). For a site's security officer, the operator team that holds the keys, and Dalil.

Related pages: what each key is, its algorithm, its permitted use and its code are in the [cryptographic inventory](crypto-inventory.md); this page points at its rows (K1 and so on) rather than repeating them. The protection levels L1 to L4 are those of the [data classification](data-classification.md) (L4: credentials and keys). Commands to create the secrets: [wiki 04](../../wiki/04-Deployment-Guide.md) section 6.2; backups: wiki 04 section 10; operations procedures: [wiki 10](../../wiki/10-Operations-Runbook.md); the security guide and its go-live checklist: [wiki 13](../../wiki/13-Security-Guide.md).

Status: as of 2026-10-19 (ARV-094); updated 2026-11-10 by ARV-207 (the key ring's retention and destruction, the destruction rule for a previous Data Protection certificate, key-age metrics and alerts) and ARV-211 (a signing key of the key ring's own), then by the ARV-211 and ARV-207 review (each key ring row signed, the upgrade check, the previous certificates mounted by the chart, a failed key lifecycle run alerted). A rule that restates what the code, the charts or a written procedure already do cites them. A rule marked **Proposed** is not built or agreed yet; every **To confirm** is collected in section 6.

## 1. Roles

| Role | Who | Holds and does | Never |
|---|---|---|---|
| Key owner | The authority that operates the deployment: the border authority for a border deployment, the airport operator for an airport deployment (one deployment per namespace, wiki 04 section 6.1) | Owns every key of the deployment. Approves cryptoperiods, custody and who may read each Secret; receives every compromise report; decides recovery (for example a mass re-enrolment) | Shares a key with another deployment, above all between a border and an airport deployment (data boundary) |
| Custodians | The site's operator team (site IT and operations), who provide and operate the cluster, its TLS and its secrets (wiki 19 section 6) | Create the keys and Secrets with the commands of wiki 04, keep the copies in the site's secret store, run rotations, keep the go-live record and the operations log | Give a key to someone outside the custody rules of section 3.3 |
| Ariva administrators | The deployment's system administrators | Issue and rotate the credentials made inside Ariva (users' resets, devices, displays, integration clients, outbound endpoint secrets), each a critical action with a fresh second factor, audited | See a credential after it was shown once |
| Local partner | The installer of the sensors (wiki 19 section 6: device credentials and switch security) | Puts device credentials and certificates on the devices | Keeps a copy of a device credential after installing it (Proposed) |
| Partners | AODB, AMAN, ACRIS and other systems Ariva calls | Issue the outbound credentials (K17 to K20) and their CAs; revoke and reissue them | |
| Dalil Tech | The product team and its release pipeline | Owns the product's cryptography (code, defaults, this policy and the inventory), the release signing identity (K43) and the repository settings it rests on, and the keys of the dev and demo environments it runs | Holds a site's keys or credentials. Dalil reaches a site only as the customer allows (wiki 19 section 1); its support tooling collects health telemetry only |

## 2. Lifecycle states

NIST SP 800-57 Part 1 names six states. In Ariva:

| State | Meaning | Ariva examples |
|---|---|---|
| Pre-activation | Made, not yet used to protect anything | A Secret created before the release that names it (the pods wait for it); a new token public key installed as `public.pem` before the matching signing key replaces the old one (wiki 04 section 6.2, rotation step 1); a new Data Protection certificate in the secret store before `ariva-dataprotection` is replaced; a new key ring signing certificate before `ariva-dataprotection-signing` is replaced |
| Active | Used to protect and to process | The signing key in `ariva-token-signing`; the default Data Protection key; the current certificate; the current key ring signing certificate; a device's live credential |
| Suspended | Not used for a while, can return | A disabled integration client (its tokens stop at once) or outbound endpoint; a disabled or locked user account; a locked integration client |
| Deactivated | No longer used to protect; may still process what it protected | `previous.pem` during a token key rotation (verifies, signs nothing); a certificate under `DataProtection:Previous` (unwraps, wraps nothing); a key ring signing certificate under `DataProtection:Signing:Previous` (its certificate alone: verifies, signs nothing, and the keys it signed only unprotect; ARV-211); an expired Data Protection key (unprotects, protects nothing); a backup identity kept for older backups; an allowed-signers line kept for older backups |
| Compromised | Known or suspected to be in the wrong hands | Any of the above; handled by section 3.7 and the key's own section |
| Destroyed | Gone from every place it was kept | A device credential's hash cleared when the device is retired; an archive day key deleted by the first archive write after its three days; an expired Data Protection key deleted by the key lifecycle job once its retention has passed and no stored value needs it (ARV-207; a backup keeps its own copy, section 3.8); a Secret deleted and its files shredded |

```mermaid
stateDiagram-v2
  state "Pre-activation" as Pre
  state "Active" as Act
  state "Suspended" as Sus
  state "Deactivated" as Deact
  state "Compromised" as Comp
  state "Destroyed" as Dest
  [*] --> Pre: generated
  Pre --> Act: activated
  Pre --> Dest: never used
  Pre --> Comp
  Act --> Sus: suspended
  Sus --> Act: restored
  Act --> Deact: replaced or expired
  Sus --> Deact
  Act --> Comp
  Sus --> Comp
  Deact --> Comp
  Deact --> Dest
  Comp --> Dest
  Dest --> [*]
```

## 3. Policy

### 3.1 Generation

1. Every key, secret, salt and nonce comes from a cryptographically secure generator: `RandomNumberGenerator` in the hosts, OpenSSL, `age-keygen` or `ssh-keygen` for the operators (inventory A12). Reason: a predictable key protects nothing. The build fails on `System.Random` (analyzer CA5394).
2. Every key gives at least 128 bits of security: P-256 for the token keys and the key ring signing certificate, RSA 3072 or more for the Data Protection certificate, 256 random bits for every bearer credential, 160 bits for TOTP seeds, at least 32 bytes for HMAC keys and the log pseudonym key. The hosts refuse smaller where they can (`TokenKeys.RequireP256`, `DataProtectionCertificates.RequireStrong`, `KeyRingSigning.RequireSigningCertificate`, `OutboundRules.IsHmacKey`, `LogProtection.MinimumKeyBytes`). Reason: ASVS V11.2.3. Known exceptions: recovery codes and temporary passwords (inventory section 5, items 1 and 2).
3. No key is derived from a password, except where the code does so on purpose: PBKDF2 only verifies passwords and client secrets (A2); the SCRAM verifier is PostgreSQL's protocol (A3); the log day keys are derived from a random key, not a password (A6). Reason: a password has too little entropy to be a key (V11.4.4).
4. Operators generate a key into a file with `umask 077`, never type or paste a value on a command line, and shred the file once the copy is in the secret store, as the commands of wiki 04 section 6.2 do. Reason: shell history, process lists and stray files leak keys.
5. Every site key is made for one deployment and never reused in another: the token key pairs, the Data Protection certificate, the key ring signing certificate, the break-glass account, the log pseudonym key (wiki 04 section 6.2). Reason: one leak must not open two deployments, and a key shared between border and airport would cross the data boundary.
6. A key the operator chooses (the Cronz dashboard key K22, the simulator keys K44) is 32 random bytes, `openssl rand -base64 32` (wiki 04 section 6.4). Reason: the hosts check no entropy (ARV-088).

### 3.2 Distribution

1. Keys and secrets reach the pods only from Kubernetes Secrets, as read-only mounted files (token keys, the Data Protection certificate, TLS keys) or as `secretKeyRef` environment variables (the log pseudonym key, the validation reader login, the Kafka password, the log store's sender credentials). Never chart values, a ConfigMap, a committed appsettings file or an image. Reason: values files, ConfigMaps and images are read by more people and systems than a Secret. What enforces it:
   - the chart test fails a render with a literal credential in a container's `env`, a signing key mounted by any pod but its host, or the pseudonym key in a ConfigMap or as a literal (`Platform/Cloud/Ariva.K8s/tests/checks.mjs`), and the chart refuses a `logging.pseudonymKeySecret.key` that looks like a key;
   - at install and upgrade, when it can see the cluster, the chart reads the pseudonym key's Secret and refuses one that is missing or holds a key the hosts would refuse, and refuses a cluster CA Secret without `ca.crt` or with `tls.key` (`templates/validate.yaml`);
   - outside vm-local a host refuses the pseudonym key, Loki's sender credentials and the OTLP headers from a configuration file (`LogProtection.From`, `ArivaLogging`, `OtlpSettings.EnsureHeadersAreProtected`), and any host but api-main refuses the validation reader login;
   - the security scanner fails a secret value committed in configuration outside the vm-local files (SEC-120), and the committed `k8s-*` files carry empty passwords.
2. Exception, as built: the service passwords (database, Redis, SMTP) travel inside the appsettings Secrets (`base-appsettings-secret` and `<service>-appsettings-secret`), Kubernetes Secrets mounted as files, so one Secret reaches every host (ARV-083b splits them per host). The Azure DevOps release gives the registry password to Helm as a value (inventory section 5, item 19).
3. Each Secret goes to the pods that use it and no other: the user signing key to api-main only, the integration signing key to api-integration only, the validation reader to api-main and the migration Job only, the pseudonym key to the five API hosts only (never the Jobs, the web pod or the simulator), each pod certificate to its own pod. Reason: least privilege; a pod that holds no key cannot leak it.
4. Credentials made inside Ariva are shown once, with `Cache-Control: no-store`, and never logged: device and display credentials, integration client secrets and seeds, temporary passwords, TOTP enrolment, the break-glass credential. They are handed over at once: a device credential into the device or the vendor tool (wiki 11 section 6); an integration client's secret and seed on two separate channels (wiki 11 section 4). Reason: a value that is stored nowhere in clear cannot be read back.
5. Public halves and CA certificates may travel openly, but come from a source an attacker cannot change: the allowed-signers file from the operations repository or the secret store, never the backup store (wiki 04 section 10.2); CA certificates from the issuing PKI. Reason: whoever can replace a trust anchor can impersonate what it vouches for.

### 3.3 Storage and custody

1. The custodians keep a copy of every site key and secret in the site's secret store (D5), apart from the database backups and the log store: the token signing keys, the Data Protection certificate, the pseudonym key, the service passwords (wiki 04 sections 6.2 and 10.1). Kubernetes Secrets are encrypted at rest on the cluster (wiki 13 checklist item 2). Reason: a Secret lost with its namespace must be recoverable, and the copy must not share the backups' or the logs' exposure.
2. Separation of duties:
   - Nobody who reads the logs may read the log pseudonym key's Secret (K24): anyone allowed to get it, to list or watch Secrets, to create pods or to exec into the API pods can (wiki 13 checklist item 15, entry 5, and the read-only checks below it). Reason: with the key, a log reader can recompute every desk code's pseudonym.
   - The Data Protection certificate (K3) is kept apart from the database backups, which carry the key ring wrapped under it (wiki 04 section 10.1).
   - The backup identity (K41) is kept offline, in a custody apart from the certificate's (Proposed: sealed, two people, wiki 04 section 10.1). Reason: one person or one theft must not get both the backup and what unwraps its secrets.
   - Each backup signing key (K42) stays with its operator, apart from the backup store, the backup identity and the certificate; the allowed-signers file where the backup store's writers cannot change it (wiki 04 section 10.2).
   - The break-glass credential (K11) is sealed offline; two-person custody is recommended (wiki 04 section 6.2).
3. Who can read each Secret is recorded: for the pseudonym key and the log store's sender credentials it is part of the go-live record (wiki 13 checklist item 15). Proposed: the same record for the token signing keys, the Data Protection certificate and the backup identity. The deployer needs `get` on Secrets for the chart's checks (wiki 13 checklist item 3); keep that right to the release identity.
4. In the database there are only hashes (A2, A5) and Data Protection payloads (K4) of credentials, never a credential in clear, with one exception: the sensing archive day keys (K26; inventory section 5, item 12). Keys that live in memory only: K5, K23, K25.
5. Files in the pods are read-only; the signing keys mode 0400, the pod certificates 0440 through the pod's group (wiki 04 section 5).

### 3.4 Use

1. One purpose per key. The permitted use is the inventory's column; a key is never used for anything else. Built in: separate key rings for user and integration tokens, so neither verifies as the other (K1, K2); one Data Protection purpose per kind of value (inventory section 3); labelled HMAC inputs (`ariva-log-pseudonym:`, `ariva-jobs-hub`) so a key serves one thing (A6, A8); the Data Protection certificate is never a TLS certificate (K3); since ARV-211 the Data Protection certificate only wraps the key ring (RSA-OAEP, key transport) and a certificate of the key ring's own signs it (K49, ECDSA P-256), so neither key serves two purposes. Reason: a key used for two things can be turned from one against the other.
   - The former exception, closed by ARV-211: until then the Data Protection certificate's RSA key pair (K3) both wrapped each key ring element and signed it (RSA-PSS), which NIST SP 800-57 Part 1 section 5.2 names as a pairing to avoid: a key transport key is kept for recovery while a signing key should be retired, and while an old certificate was configured under `DataProtection:Previous` its private key could still sign a key the hosts admitted. Now the hosts admit a key only under the key ring signing certificate (`OaepGcmXml.Check`); the signing certificate is held to ECDSA P-256 with the key usage digital signature alone, so it can never be a wrapping certificate (`KeyRingSigning.RequireSigningCertificate`). Keys an installation wrote before ARV-211 (signed with the certificate's key) are read only inside `DataProtection:Signing:CertificateSignedUntil`, an upgrade window the operator sets (wiki 04 section 6.2), and then only to unprotect.
2. A deactivated key only processes: `previous.pem` verifies, a previous Data Protection certificate unwraps, an expired Data Protection key unprotects; none of them protects anything new. Reason: NIST SP 800-57's deactivated state.
3. No API returns a key or a credential after it was issued, and the logs omit them (`RedactionEnricher`, log inventory section 10.6). Reason: what is not shown cannot leak.
4. Cryptoperiods (the time a key may protect): as built, or Proposed where nothing sets one yet. A Proposed period is this policy's default until the owner confirms or changes it (section 6), and every key is also replaced at once on a compromise (section 3.7) and when a person who knew it leaves (section 3.5, rule 1). The Proposed periods stay within the originator usage periods NIST SP 800-57 Part 1 section 5.3 suggests (up to two years for symmetric authentication keys and private key transport keys). Where a period is shorter, the reason is among the risk factors of its section 5.3.1, chiefly personnel turnover and the number of copies of a key: a key that people type, hold or keep in a password manager has more copies and more people who knew it.

| Key | Cryptoperiod |
|---|---|
| K1, K2 token signing keys | 90 days (ADR-0026 point 5); the previous public key kept for the overlap only (section 4.1) |
| K3 Data Protection certificate | Proposed: 2 years, the previous one kept as `DataProtection:Previous` while keys it wraps are unexpired (the wiki 04 command makes it valid for 1825 days, so it is replaced well before it expires) |
| K49 key ring signing certificate | Proposed: 2 years (ARV-211; the wiki 04 command makes it valid for 1825 days); its predecessor configured by its certificate alone for the overlap of a rotation |
| K4 Data Protection keys | 90 days, then a new default key (code); since ARV-207 deleted once expired for `Cronz:KeyLifecycle:ExpiredKeyRetentionDays` (Proposed 30, at least 30) and no stored value names them (section 3.8, rule 3) |
| K5 element key | One element |
| K6 passwords | The user's; no forced expiry |
| K7 temporary passwords | Until the first sign-in; an expiry is To confirm (ARV-088 proposes one) |
| K8, K13 TOTP seeds | Until reset or re-enrolment (as built). Proposed: K8 2 years (a symmetric authentication key, section 5.3), and re-enrolled at once on a lost or replaced device; K13 reset together with its client's secret (K12, 1 year). Nothing enforces either today |
| K9 recovery codes, K10 refresh tokens | One use each; a refresh token at most the session's 12 hours |
| K11 break-glass | Until its codes run low, an incident or a change of custody (wiki 04 section 6.2) |
| K12 integration client secrets, K14 device and K16 display credentials | Proposed: 1 year, and at once when a partner system, a device or a screen changes hands |
| K15 device certificates | The certificate's own validity, which Ariva does not check (inventory section 5, item 13) |
| K17 to K21 outbound credentials | The partner's |
| K22 Cronz dashboard key | Proposed: 90 days, and when someone who knew it leaves |
| K23 hub ticket key | One api-cronz process; each ticket 30 minutes |
| K24 log pseudonym key | Proposed: 1 year (a new key only breaks the link between pseudonyms written before and after it) |
| K25 log day keys, K26 archive day keys | One UTC day; K26 deleted by the first archive write after its three days (section 4.10) |
| K27 to K33, K47 and K48 certificates and CAs | The issuer's; for pod certificates the wiki 04 example is 90 days, renewed 30 days before the end |
| K34 to K40 service credentials | Proposed: 1 year, and when an administrator who knew one leaves |
| K41 backup identity, K42 backup signing keys | Proposed: 2 years; a retired backup identity is kept, sealed, while backups made under it are kept |
| K43 release signing | One signing run |

Since ARV-207 the hosts record the age of every dated key and credential and count down to the cryptoperiod of those rotated by hand: `ariva.keys.age` and `ariva.keys.rotation_due_in` by kind (K3, K3 previous, K49, K49 previous, K4, K12, K13, K14, K16, K17 to K20; wiki 10 section 2a), with the Proposed periods above as `Monitoring:Cryptoperiods` (K3 and K49 730 days; K12, K13, K14 and K16 365 days), which the owner confirms or changes in configuration; the SigNoz rule `keyRotationDue` warns 30 days before a period ends and is critical 7 days before (wiki 10 section 3.1). Keys that carry no date Ariva can read are not in it: the token signing keys (K1, K2), the log pseudonym key (K24), the Cronz dashboard key (K22), the service credentials (K34 to K40), the user TOTP seeds (K8, no date stored) and the backup keys (K41, K42, outside the hosts); their dates are the go-live record's and the operations log's (section 3.9, rules 2 and 3). The certificates a host loads are in `ariva.certificates.expires_in` (their issuer's validity is their cryptoperiod), the CA files included since ARV-207 (K31, K32, K33, K47).

### 3.5 Rotation

1. Rotate on the cryptoperiod, at once on compromise or suspicion (section 3.7), and when a person who had access to a key leaves (wiki 04 section 6.4 says so for the Cronz key; Proposed for every key a person could read).
2. Rotate with overlap where the key protects something that lasts: install the new key as pre-activation, activate it, keep the old one deactivated for the overlap, then destroy it (the token key steps of wiki 04 section 6.2, the certificate's `DataProtection:Previous`). Reason: nobody is signed out and nothing becomes unreadable.
3. In-app credentials rotate through their critical action, which audits it and stops the old value at once: device credentials (wiki 11 section 6), display credentials (`POST api/v1/admin/displays/{id}/credential`, wiki 11 section 2d), integration client secrets and seeds and outbound secrets (`PUT {id}/secret`; wiki 11 section 4), users' passwords and TOTP (wiki 11 sections 2 and 3), the break-glass account (`--rotate-break-glass`).
4. After a rotation, prove it took: the smoke tests (wiki 04 section 8), the TLS check after a certificate change (wiki 10 section 4.14), `ariva-ops.mjs keyring` after a Data Protection or key ring signing certificate change (it names, per key, the certificate that wraps it and the signer that signed it; wiki 04 section 10.2), a startup line in the log store after a log credential change (wiki 04 section 8, check 10), and `ariva.keys.age` for the kind after a rotation of a dated key (wiki 10 section 2a).
5. Record each rotation (section 3.9).

### 3.6 Backup and recovery

1. Database backups (ARV-126) hold the key ring only wrapped, credentials only as hashes or Data Protection payloads, and no role or password (wiki 04 section 10.1); they also hold the archive day keys (K26) still in the table when they were taken (normally the last three days). Every backup file is encrypted with age (K41) and its manifest signed (K42).
2. Never in a backup: the Data Protection certificate, the token signing keys, the backup identity, the backup signing keys, the appsettings secrets and the TLS keys; each has a custody of its own (wiki 04 section 10.1).
3. Recovery of the protected values needs the Data Protection certificate in use when the backup was taken and, since ARV-211, the key ring signing certificate that signed its keys (its certificate alone, configured under `DataProtection:Signing:Previous` for the restore when it has been replaced since; for a backup from before ARV-211, the upgrade window `DataProtection:Signing:CertificateSignedUntil` instead). Keep every previous certificate as long as a kept backup needs it (`ariva-ops.mjs keyring` lists, per key, the certificate it needs; wiki 04 section 10.4, step 6). Losing it with no copy cannot be undone: users enrol their authenticator again, integration clients get new seeds, outbound secrets are entered again (wiki 04 section 11; wiki 10 section 4.13).
4. Check every month that the certificate in the secret store is the one in use (`ariva-ops.mjs keyring`, wiki 10 section 5), and restore into a scratch namespace with the production certificate before go-live and after every major release (wiki 04 section 10.6).
5. A restore brings back the credentials of the backup's time: rotate again whatever was rotated since (users, integration clients, devices, displays; wiki 04 section 10.4, step 8).
6. Keys that are never backed up and are simply made again: the token signing keys (a new pair; users get new tokens), the memory-only keys (K5, K23, K25), the pod and server certificates (reissued by the site's PKI).

### 3.7 Compromise response

On a known or suspected compromise, follow the incident steps of wiki 13 section 11: contain, keep the evidence (audit trail, logs, the affected database state), tell the key owner and Dalil within the agreed time, rotate, recover, and review. For each key:
- **What to do**: the key's section in section 4 (rotate it at once, without the overlap where the key itself leaked).
- **What it invalidates**: the tokens, sessions or values the key protects, as each section says.
- **Who to tell**: always the key owner and the site's security operations; Dalil's security contact (wiki 13 section 11, contacts To be added); and whoever relies on the key: the integrator for an integration client, the partner for an outbound credential, the local partner for a device, the log store's administrators for a log credential. Data protection notification duties depend on the jurisdiction (wiki 14).
- **Evidence**: never copy the compromised value into a ticket or a log; name the key by its inventory id and its identifier (key id, certificate SHA-256 fingerprint, credential prefix).

### 3.8 Deactivation and destruction

1. A replaced key is deactivated for its overlap and then destroyed: removed from the Kubernetes Secret (or the Secret deleted), from the secret store, and any file shredded (wiki 04 section 6.2 uses `shred -u`). Reason: a key kept after its use is only a risk.
2. Keys and credentials the code destroys itself: a retired device's credential hash (a database check holds it, script `0013_device.sql`); a spent recovery code (marked used); a refresh token's successor once taken; the archive day keys, by the first archive write after their three days (`SensingArchive.Window`, `SensingArchive.DayKeysAsync`); the memory-only keys at process end.
3. Data Protection keys and the certificates that wrap them (ARV-207):
   - **Expired keys** are deleted by the Cronz job `KeyLifecycle` (daily, 03:10 UTC, half an hour after `SecretReprotection` has moved the stored secrets to the current key) through script 0072's `ariva_data_protection_key_purge`, the only way the runtime login can delete a key (it holds no DELETE on `data_protection_key`). A key goes only when all of these hold, each checked by the function itself: it expired more than `Cronz:KeyLifecycle:ExpiredKeyRetentionDays` ago (Proposed 30; the function refuses less than 30); no stored value names it (every user TOTP seed, integration client seed and outbound secret carries its key's id in its first bytes, whatever the row's status, an unreadable value included); and an unexpired key remains in the ring. The short-lived values are covered by the period: a refresh token's successor (`refresh_token.successor_protected`) is read only within `Auth:Sessions:RefreshGraceSeconds` of its use (30 seconds; no check bounds that setting yet, section 6, item 27), a preview token within 2 hours, and a session (whose refresh cookie holds no Data Protection payload) lasts 12 hours. A unit test holds the list: every `*_protected` column the scripts create is one the purge holds keys for, the same three the re-protection job moves and the ARV-211 upgrade check reads, or the refresh token's successor (`KeyLifecycleJobsTests.Purge_Should_HoldTheKeyOfEveryLongLivedProtectedColumn_When_TheScriptsCreateOne`). Revocations are never deleted, so a copy of a revoked key cannot become usable again. A key past its retention that is still needed is kept and reported (`ariva.keyring.keys`, kind `held`; the `keyRingHeld` alert): the values the re-protection job could not move are named in its log, and setting them again releases the key. A run that fails as a whole (the database out of reach, the purge refused, its run timeout) is measured (`ariva.keyring.problems`) and alerted (`keyLifecycleFailed`), so a job that stopped purging is seen. Proposed for the owner: the 30 days.
   - **A key in a backup** is not touched: every backup carries the key ring as it was (wrapped), and a restore brings it back. The keys in the live table are therefore destroyed by the job; their copies in backups are destroyed with those backups (rule 4), and in effect when every certificate that wraps them is destroyed.
   - **A previous Data Protection certificate** is destroyed (removed from `DataProtection:Previous`, its Secret deleted, the secret store's copy and every file shredded) only when all of these hold, and the destruction is recorded (section 3.9, rule 3): (a) no key it wraps is left in the live key ring: `ariva-ops.mjs keyring` with the current certificate alone reports nothing missing, so every key it wrapped has been deleted by the job (or is past its retention and named by no value); (b) the last `SecretReprotection` run reported no unreadable value, and `ariva.keyring.keys` reports no held key; (c) no kept backup needs it: `ariva-ops.mjs keyring` against a restore of the oldest kept backup (a scratch namespace, wiki 04 section 10.6) reports nothing that only this certificate covers. Until (c) holds it stays in the secret store only, never configured: a restore that needs it configures it again for that restore (wiki 04 section 10.4). With 90-day keys, 30 days of retention and the Proposed 14 days of backups (TC-34), a previous certificate can go about four to five months after it was replaced. Proposed for the owner: this rule.
   - **A previous key ring signing certificate** (K49) has no private key in any host: its private key is destroyed at the rotation (wiki 04 section 6.2), and only its certificate is configured, for the overlap. The certificate is kept in the secret store while a kept backup holds keys it signed (a restore of such a backup configures it under `DataProtection:Signing:Previous`), then deleted; a public certificate needs no shredding.
4. Backups are destroyed with their retention (Proposed 14 days, TC-34); the backup identity that decrypts them, and the allowed-signers lines of the keys that signed them, only after the last such backup.
5. Kubernetes keeps deleted Secrets in its etcd backups and on node disks as long as the cluster does; that is the site's (To confirm in the go-live record).

### 3.9 Records

1. The go-live record already holds (wiki 13 section 7): who can read the pseudonym key's Secret and the log store's sender credentials (item 15), the cluster CA check (item 17), the CA each ingress host chains to (item 18), the pseudonym key in place (item 21), and the tested restore (item 16).
2. Proposed: the go-live record also lists, for every key of the inventory that the site holds: its id, owner and custodians, where its copy is kept, its identifier (token key id, certificate SHA-256 fingerprint, age recipient, SSH key fingerprint; never the value), the date it was made, its cryptoperiod and next rotation date, and who can read its Secret.
3. Proposed: each rotation and each destruction is recorded in the operations log (where wiki 04 section 10.3 already puts the backup hashes): the key's id, the old and new identifiers, the time in UTC, who did it (both people where custody is dual), why (scheduled, a person leaving, a compromise), the checks run after it (section 3.5, rule 4), and when the old key was destroyed. Never a value.
4. Rotations inside Ariva (devices, displays, integration clients, outbound secrets, users) are recorded by the application itself in `audit_entry`, without secrets.

## 4. Keys and key families

Algorithms, sizes and code are in the inventory rows named in each section.

### 4.1 Access token signing keys (K1 user ring, K2 integration ring)

- **Owner**: the key owner. **Custody**: the custodians; private keys in `ariva-token-signing` (api-main) and `ariva-integration-token-signing` (api-integration), public keys in `ariva-token-public` and `ariva-integration-token-public`, copies in the site's secret store.
- **Cryptoperiod**: 90 days (ADR-0026). The overlap of the previous public key: the access token lifetime, 15 minutes (wiki 04 section 6.2): only access tokens are signed, and a host accepts `previous.pem` for as long as it is mounted, so the operator removes it then. The `TokenKeys` comment's 90 days is stale (inventory section 5, item 10).
- **Rotation**: wiki 04 section 6.2, three steps (new `public.pem` with the current key as `previous.pem` and a restart of every API Deployment; the new `signing.key` and a restart of api-main; `previous.pem` removed and the Deployments restarted). The integration ring the same way with its own Secrets and api-integration (not yet written in wiki 04, To confirm).
- **Compromise**: do the same without waiting, and remove the leaked key from the public Secret at once: every token it signed is refused (wiki 04 section 6.2). Then review the audit trail and the security events of the exposure window and undo what a forged token did (accounts, grants, integration clients, outbound endpoints created or changed). A forged token can carry any claim, a fresh second factor (`amr`, `auth_time`) included, but every host still checks that a user token's session is active (`SessionValidationMiddleware`; session ids appear in the logs as `CallerSessionId`), and every integration call is checked against the client's record: active, its current token version, the caller inside its allowed networks (wiki 11 section 4).
- **Destruction**: delete the old private key from the secret store and shred any file once the rotation is done; remove `previous.pem` at step 3.

### 4.2 Data Protection certificate (K3)

- **Owner**: the key owner. **Custody**: the custodians; `ariva-dataprotection` in every API host; the copy in the site's secret store, apart from the backups.
- **Cryptoperiod**: Proposed, 2 years (section 3.4, item 4; the certificate the wiki 04 command makes is valid for 1825 days; `ariva.certificates.expires_in` warns before its end).
- **Two purposes (closed by ARV-211)**: until ARV-211 the one RSA key pair wrapped every key ring element with RSA-OAEP and signed it with RSA-PSS, against the one purpose per key of NIST SP 800-57 Part 1 section 5.2 (section 3.4, rule 1), and while an old certificate was configured under `DataProtection:Previous` an element signed with its private key was admitted. Now the key ring signing certificate (K49, section 4.2a) signs every element and every row, and this certificate only wraps: a previous one kept for recovery unwraps the keys it wrapped and signs nothing the hosts admit.
- **Rotation**: wiki 04 section 6.2: a new pair as `ariva-dataprotection`, the old one from a second Secret named by the chart value `dataProtectionPreviousSecretName` (mounted read-only and owner-only at `/app/secrets/dataprotection-previous` in every API host, as `DataProtection:Previous:0`); new keys are wrapped by the new certificate; let `SecretReprotection` run (or run it from the jobs dashboard); the previous entry can go once the old keys have expired; keep the old certificate itself while a backup needs it (section 3.6). The age of the current certificate and of the oldest previous one are in `ariva.keys.age` (kinds `data-protection-certificate` and `data-protection-previous-certificate`), and `keyRotationDue` warns before the current one's 2 years end (ARV-207).
- **Compromise**: the certificate's private key unwraps every key ring element, so with a copy of the database it discloses every TOTP seed and outbound secret. Since ARV-211 it can no longer make a key the hosts admit: a key must be signed by the key ring signing certificate (K49), which is apart from it (section 4.2a). Proposed procedure (no tool for steps 2 and 3 yet, inventory section 5, item 8): (1) create a new certificate and replace `ariva-dataprotection`, keeping the compromised one under `DataProtection:Previous` only while it still unwraps keys the hosts need; (1a) rotate the key ring signing certificate too (section 4.2a): both are mounted in the same API pods, so whatever exposed the one may have exposed the other; once `SecretReprotection` has run, remove the previous signer, so that every key and row the old signing key signed, a planted one included, is refused; (2) make the hosts stop using keys wrapped by the old certificate (a revocation of those keys); (3) if the database may also have been read, reset every user's TOTP and every integration client's seed, have the partners reissue the outbound secrets and enter them again, and let everyone sign in again (the same recovery as a lost certificate, wiki 10 section 4.13). Tell the key owner, the integrators and the partners.
- **Destruction**: section 3.8, rule 3.

### 4.2a Data Protection key ring signing certificate (K49, ARV-211)

- **Owner**: the key owner. **Custody**: the custodians; `ariva-dataprotection-signing` (type `kubernetes.io/tls`, an ECDSA P-256 key, key usage digital signature alone) in every API host, read-only; the copy in the site's secret store, apart from the backups and apart from the Data Protection certificate's copy where the store allows it.
- **Cryptoperiod**: Proposed, 2 years (section 3.4, item 4); `ariva.keys.age` and `keyRotationDue` (kind `data-protection-signing-certificate`) and `ariva.certificates.expires_in` (`data-protection-signing`) report it.
- **What it does**: signs every key ring element the hosts write (ES256 over the element's wrapped key, nonce, tag, ciphertext, its algorithm label, its wrapping certificate and its own name), and nothing else. A host admits a key only when its signature verifies under the current signing certificate (admitted as it is) or under a previous one configured by its certificate alone (admitted as an expired copy: it unprotects what it protected and never becomes the default key, so the hosts make a new key signed by the current certificate at once). A key signed by a signing certificate that is not configured, a retired one, is refused (`TrustedKeyRingRepository`, `OaepGcmXml.Check`). A key wrapped under a previous Data Protection certificate still unwraps, whoever wraps it: who signed it decides whether it is admitted. Since the ARV-211 review (finding M1) it also signs each row the hosts store, over the row's friendly name and its exact text (script 0073, `KeyRingSigning.SignRow`), checked before the row is parsed: a key's id, dates and descriptor and every revocation are the ones a host wrote, so a genuine element copied under another id, other dates or another descriptor, or a revocation a writer adds, is refused; a row without a row signature (written before ARV-211) is read only inside the upgrade window, a key in it only as an expired copy, and never one whose element names a signer (a copy of a signed row's element). The descriptor is held to the one Data Protection writes with Ariva's settings (`AuthenticatedEncryptorDescriptorDeserializer`; AES-256-GCM, or AES-256-CBC with HMAC-SHA256 for keys from before ARV-080), and two rows of one key id enter the ring once (the signed row's).
- **Rotation**: wiki 04 section 6.2: a new certificate as `ariva-dataprotection-signing`, the old one from a second Secret named by the chart value `dataProtectionSigningPreviousSecretName`, of which the chart mounts `tls.crt` alone (read-only and owner-only at `/app/secrets/dataprotection-signing-previous`, as `DataProtection:Signing:Previous:0:CertificatePath`; a host refuses a file there that holds a private key), a rollout of the API hosts (the hosts make a new default key signed by the new certificate), `SecretReprotection` run from the jobs dashboard (every stored value moves to the new key), then, 2 hours later at the least (the longest-lived value it does not move, a preview token), the value emptied and the API hosts rolled out again. The old private key is shredded at the first step: no host holds it again. `ariva-ops.mjs keyring` names the signer of every key.
- **Compromise**: whoever holds the private key and can write to the database can plant a key the hosts admit as current, become its default and read every value protected or re-protected under it. Do the rotation at once, keeping the compromised certificate under `Previous` only for the re-protection run (while it is configured, a key it signed is read, a planted one too, though never as the default), and treat every value protected since the suspected compromise as disclosed: the recovery of section 4.2, step 3; then remove the previous signer (an empty `dataProtectionSigningPreviousSecretName`). The same rotation is part of the Data Protection certificate's compromise procedure (section 4.2, step 1a) and of any revocation of the key ring after a compromise (section 4.3). Tell the key owner.
- **Upgrade from the shared key**: an installation's keys from before ARV-211 carry no signer; they are read only while `DataProtection:Signing:CertificateSignedUntil` is in the future, as expired copies, so the re-protection job moves their values; the window is the operator's, set for the upgrade and removed after it, and no committed cluster file sets it (wiki 04 section 6.2; test `ClusterSettings_Should_MountTheSigningCertificateAndKeepTheWindowClosed_When_Committed`). A restore of a backup taken before the upgrade needs the window again for that restore. The review's findings H1 and N1: the migration job (after its scripts) and every host at start outside vm-local refuse, pointing at wiki 04 section 6.2, only in the never-upgraded case: stored values need keys no configured signer signed, none is yet under a signed key, and the window is not open (`KeyRingUpgradeCheck`; the job exits with code 2). So an upgrade or a restore without the window stops before any pod rolls, while values left under old keys in an installation already upgraded (the re-protection job could not move them, or a database writer planted a row) stop nothing: they are logged and measured (`ariva.keyring.problems`, outcome `unsigned`, by every API host at start and by the key lifecycle job each day) and alerted (`keyRingUnsigned`; wiki 10 section 4.25, which gives the hints that tell a real leftover from a row a writer added, and what to do when unsure). The hosts count apart the values under keys a signing certificate they do not hold signed (outcome `retired-signer`: a rotation whose previous signer was removed before `SecretReprotection` moved them): the window does not admit those keys, so it neither excuses them nor fixes them, and the message, the alert and the runbook name the previous signer (`dataProtectionSigningPreviousSecretName`) instead; when no value is under a configured signer's key yet, the hosts refuse to start for them too. The hosts verify each row's signature for this; the migration job, which holds no signing certificate, counts a row as signed when it carries a row signature. A window more than 90 days ahead is refused, and the hosts log a warning while it is open (finding L1).
- **Destruction**: section 3.8, rule 3.

### 4.3 Data Protection keys (K4, K5) and the values they protect (K8, K13, K17 to K20)

- **Owner**: the key owner. **Custody**: the database (`data_protection_key`, each element wrapped and signed, each row signed since ARV-211, script 0073); the values in their columns as payloads.
- **Cryptoperiod**: 90 days per key (code); `SecretReprotection` moves the long-lived values to the current key every day at 02:40 UTC; the key lifecycle job deletes an expired key past its retention once no stored value names it (section 3.8, rule 3), so the ring holds two or three keys. `ariva.keys.age` (kind `data-protection-key`) is the oldest key's age and `ariva.keyring.keys` its states (ARV-207).
- **Rotation**: automatic. To move every value at once, run `SecretReprotection` from the jobs dashboard, then `KeyLifecycle`.
- **Compromise**: a key ring key is only usable with the certificate (section 4.2). A revocation is admitted only with the row signature of a signing certificate the hosts accept (since the ARV-211 review), so one a database writer adds is refused; inside the upgrade window (`DataProtection:Signing:CertificateSignedUntil`) a revocation without a row signature is still read, as one written before ARV-211 is, and whoever holds K49's private key can sign one. A planted revocation that is read makes the values under the revoked keys unreadable: delete the planted `revocation` row with the migration login and restart the hosts, or restore the database, and treat it as a database compromise (wiki 13 section 5). A value that cannot be unprotected stops only its own use: an outbound endpoint says to set its secret again (wiki 10 section 4.8f), a user re-enrols. A revocation of the key ring after a compromise (inventory section 5, item 8) includes a rotation of the key ring signing certificate (section 4.2a), its previous signer removed once `SecretReprotection` has run.
- **Destruction**: deleted by the key lifecycle job (ARV-207; section 3.8, rule 3); copies in backups go with the backups. K5 lives in memory only and is zeroed after use.

### 4.4 People's credentials (K6 passwords, K7 temporary passwords, K8 TOTP seeds, K9 recovery codes, K10 refresh tokens)

- **Owner**: the user, within the key owner's rules. **Custody**: the user; Ariva keeps hashes (K6, K7, K9, K10) or payloads (K8).
- **Cryptoperiod**: section 3.4.
- **Rotation**: the user changes the password; an administrator resets a password or TOTP (wiki 11 sections 2 and 3); recovery codes are regenerated with a TOTP code and a recent second factor; refresh tokens rotate at every use.
- **Compromise**: reset the password, the TOTP or both; each reset ends every session of the user (`security/README.md`, administration). A reused refresh token revokes its family by itself and logs `SecurityEvent.RefreshTokenReused`. Disable the account to stop it at once. Tell the user and the key owner.
- **Destruction**: a replaced hash is overwritten; a spent recovery code stays marked used; a refresh token's successor is cleared once taken. Accounts are disabled, never deleted (wiki 11 section 2).

### 4.5 Break-glass credential (K11)

- **Owner**: the key owner. **Custody**: sealed offline, two people recommended (wiki 04 section 6.2).
- **Cryptoperiod**: until its codes run low, after an incident, or when custody changes.
- **Rotation**: `Ariva.Api.Main --rotate-break-glass` in an api-main pod: a new password and ten codes, the old codes spent, the account's sessions ended.
- **Compromise**: rotate at once; review every `SecurityEvent.BreakGlassSignIn` (9110, logged at Critical) and the audit trail since the last known good state; tell the key owner.
- **Destruction**: destroy the old sealed copy after the rotation; the old codes are already spent in the database.

### 4.6 Integration client credentials (K12 secrets, K13 TOTP seeds)

- **Owner**: the key owner; the integrator holds them. **Custody**: the integrator's system, in a secret store (wiki 08 section 14, item 13); Ariva keeps the hash and the payload.
- **Cryptoperiod**: Proposed, 1 year for K12, and at once when the partner system changes hands; K13 reset together with K12 (section 3.4, item 4).
- **Rotation**: an administrator rotates the secret or resets the seed (critical action, wiki 11 section 4); the new value is shown once; the client's current tokens stop at their next call. Whether old and new may overlap is To confirm (wiki 11 section 4); as built they do not.
- **Compromise**: disable the client (its tokens stop at once), rotate the secret and reset the seed, hand the new values over on two channels, enable it; narrow its allowed networks if the attack came from inside them (wiki 10 section 4.8d); read `integration_call` for what the client did. Tell the integrator.
- **Destruction**: the old hash and payload are overwritten; clients are disabled, never deleted, so their calls stay attributable.

### 4.7 Device and display credentials (K14, K15, K16)

- **Owner**: the key owner. **Custody**: the device or the display player; the local partner installs device credentials (wiki 19 section 6). Ariva keeps the prefix and hash, and a device's certificate pin.
- **Cryptoperiod**: Proposed, 1 year for K14 and K16, and at once when a device or a screen changes hands; K15 the certificate's own validity (section 3.4, item 4; no cadence is enforced today).
- **Rotation**: a new credential by an administrator (critical action): for a device, the old one stops at once, within a minute without the cache backplane (wiki 11 section 6); for a display, `POST api/v1/admin/displays/{id}/credential` (wiki 11 section 2d) and a new player address carrying it. A changed device certificate is pinned again (critical action).
- **Compromise**: rotate; for a device also read its data in the window (it may have been forged) and re-pin a certificate whose key may have leaked; for a display, read its proof of play in the window (ARV-144). Tell the local partner or the display's owner.
- **Destruction**: retiring a device clears its hash (a database check holds it); remove the old credential from the device or the player.

### 4.8 Outbound endpoint credentials (K17 to K21)

- **Owner**: the partner, who issues them; the key owner decides which partners Ariva calls. **Custody**: Ariva holds them only as K4 payloads and in memory in the calling host.
- **Cryptoperiod**: the partner's.
- **Rotation**: `PUT {id}/secret` when the partner reissues; a change of base URL, networks, pinned CA or token path needs the secret again (wiki 11 section 4).
- **Compromise**: disable the endpoint (it stops at once), ask the partner to revoke and reissue, set the new secret. Tell the partner.
- **Destruction**: the old payload is overwritten; endpoints are never deleted, only disabled.

### 4.9 Log pseudonym key (K24, K25)

- **Owner**: the key owner (for a border deployment, the border authority: the pseudonyms are border-only data). **Custody**: the custodians; `ariva-log-pseudonym-key`; a copy in the site's secret store, apart from the log store and its readers.
- **Cryptoperiod**: Proposed, 1 year (section 3.4, item 4). Each day key (K25) lives one UTC day.
- **Rotation**: write a new key into the Secret as wiki 04 section 6.2 creates it, then restart the five API Deployments; pseudonyms change from then on, lines already written keep theirs, and for a few minutes old and new pods disagree (wiki 10 section 4.16).
- **Compromise**: rotate. Whoever holds the old key can still recompute the pseudonyms of every line written under it, so treat the log store's desk code and track id pseudonyms up to the rotation as readable; check who could read the Secret (wiki 13 checklist item 15). Tell the key owner.
- **Destruction**: remove the old key from the secret store once rotated; nothing in Ariva keeps it.

### 4.10 Sensing archive day keys (K26)

- **Owner**: the key owner. **Custody**: the database, in clear (inventory section 5, item 12).
- **Cryptoperiod**: one UTC day of use. A key is kept until the first archive write after its day is more than three days old (`SensingArchive.Window`).
- **Rotation**: automatic, one key per day.
- **Compromise**: a database reader has the keys still in the table: normally the last three days, more when the deployment has archived nothing since (no sensing data, or api-stream stopped), since the DELETE runs only at the next write. Treat as part of a database compromise. Backups keep them as long as the backup is kept.
- **Destruction**: deleted by the archive writer at its next write (`SensingArchive.DayKeysAsync`), not on a schedule; in backups, with the backup.

### 4.11 Cronz dashboard key and hub ticket key (K22, K23)

- **Owner**: the key owner. **Custody**: the operators who use the dashboard (the customer's password manager); only its SHA-256 is configured.
- **Cryptoperiod**: K22 Proposed, 90 days and when someone who knew it leaves (section 3.4, item 4); K23 one process.
- **Rotation**: a new key, its SHA-256 in `api-cronz-appsettings-secret`, a restart of api-cronz (wiki 04 section 6.4), which also draws a new ticket key.
- **Compromise**: rotate; read the refused dashboard requests in the log (event 9601) and the job runs; the dashboard answers only the management network in its `sourceRanges` (wiki 13 section 6a). ARV-087 would replace the key by user sign-in with a second factor.
- **Destruction**: remove the old key from the password manager and from the browsers that kept it in local storage.

### 4.12 TLS certificates and CAs (K27 to K33, K47, K48)

- **Owner**: the key owner; issued by the site's PKI or a public CA. **Custody**: the custodians and the issuers; each in its Secret (inventory rows).
- **Cryptoperiod**: the issuer's; the wiki 04 example for pod certificates is 90 days, renewed 30 days before the end.
- **Rotation**: wiki 04 section 6.2 and wiki 10 section 4.9. The API hosts take a renewed pod certificate without a restart; the web pods, the MQTT listener (a rollout) and a replaced database, Redis, Kafka or collector CA (a restart of the hosts) do not; for a collector CA (K47), keep both CAs in its `ca.crt` until the collector serves the new certificate. When the cluster CA is replaced, keep both CA certificates in `ariva-cluster-ca` until every pod serves a new certificate, then run wiki 13 checklist item 17 again. After an ingress certificate changes, run the TLS check.
- **Compromise**: have the issuer revoke the certificate and issue a new one with a new key; replace the Secret; restart where the host does not reload. A compromised CA: a new CA, every certificate it issued reissued, the old CA removed from every trust Secret.
- **Destruction**: the old key leaves its Secret with the renewal (cert-manager's `rotationPolicy: Always` makes a new key each time); delete any copy.

### 4.13 Service credentials (K34 to K40)

- **Owner**: the key owner; issued by whoever runs the service (the site's database administrators, the brokers' operator, the mail and log store administrators, the registry). **Custody**: the custodians; the Secrets of the inventory rows.
- **Cryptoperiod**: Proposed, 1 year, and when an administrator who knew one leaves (section 3.4, item 4).
- **Rotation**: database logins: `ALTER ROLE` and both places updated (wiki 04 section 6.2); the validation reader: update its Secret and release, or run the migration by hand (wiki 04 section 6.2); Kafka, Redis, SMTP, the log store and the registry: To confirm (no procedure written), Proposed: change at the service, update the Secret, restart the hosts that read it.
- **Compromise**: rotate at once. The validation reader can be withdrawn (`ALTER ROLE ariva_validation NOLOGIN;`, wiki 04 section 6.2). Tell the service's administrators.
- **Destruction**: the old value removed from the Secret and the secret store.

### 4.14 Backup identity, backup signing keys and allowed signers (K41, K42)

- **Owner**: the key owner. **Custody**: K41 offline, apart from K3 (Proposed: sealed, two people); each K42 with its operator; `allowed_signers` where the backup store's writers cannot change it (wiki 04 section 10.2).
- **Cryptoperiod**: Proposed, 2 years (section 3.4, item 4); both kept while backups that need them are kept.
- **Rotation**: wiki 04 section 10.2: a new age pair and the new recipient given out, the old identity kept for older backups; a new signing key's line added, the old line kept while backups it signed are kept.
- **Compromise**: K41: make a new pair at once; the backups encrypted to the old one are readable by whoever holds it (their secrets still need K3, but hashes and operational data are exposed), so treat them as disclosed and tell the key owner (data protection duties, wiki 14). K42: remove its line from `allowed_signers` at once; backups it signed are then restored with `--sha256` from the operations log (wiki 04 section 10.2); review the restores made since.
- **Destruction**: section 3.8, rule 4.

### 4.15 Release signing (K43)

- **Owner**: Dalil Tech. **Custody**: no long-lived key exists: each run of `images.yml` or `offline-bundle.yml` gets a short-lived Fulcio certificate for its GitHub OIDC identity; trust rests on the repository's tag ruleset and branch protection (To confirm, wiki 04 section 6.5).
- **Cryptoperiod**: one signing run.
- **Rotation**: none needed.
- **Compromise**: a signature made by a misused workflow identity is recorded in the public Rekor log. Proposed: on suspicion, compare the log's entries for the workflow identity with the released digests, tell every site which digests to refuse, and fix the repository settings that allowed it.
- **Destruction**: nothing to destroy.

### 4.16 Simulator and development keys (K44 to K46)

- **Owner**: Dalil Tech for the dev and demo environments it runs; each developer for vm-local. **Custody**: the team's secret store (K44, K45); the developer's machine (K46).
- **Cryptoperiod and rotation**: To confirm for K44 and K45 (rotate a copied credential when it is rotated in Ariva); K46: delete and start again.
- **Compromise**: rotate; none of them opens a production deployment (the simulator refuses k8s-prd; the development users are refused outside vm-local).
- **Destruction**: delete with the environment.

## 5. Not yet built

Target procedures this policy will cover when they exist: the licence file's signing key and its offline check (wiki 11 section 12, wiki 04 section 12.5); mutual TLS for the border-to-airport feed (Proposed, wiki 13 section 6); secrets created, rotated and destroyed through a secret store (ARV-083c); per-host Kafka principals, Redis users and settings secrets (ARV-083b).

## 6. Open decisions

Every To confirm and Proposed item of this page and of the inventory's section 5:

1. The Proposed cryptoperiods (section 3.4, item 4): the Data Protection certificate (2 years), the log pseudonym key (1 year), the user TOTP seeds (2 years, sooner on a lost or replaced device) and integration client seeds (reset with the client's secret), integration client secrets and device and display credentials (1 year), the Cronz dashboard key (90 days), the service credentials (database, Kafka, Redis, SMTP, log store, registry; 1 year) and the backup identity and signing keys (2 years). The simulator keys (K44, K45, dev and demo only) have none.
2. Correcting the stale `TokenKeys` comment (90 days) to the 15 minute overlap wiki 04 gives, when the login freeze lifts.
3. The integration ring's rotation steps, to be written in wiki 04 section 6.2.
4. Whether integration client secrets and seeds may overlap during a rotation (wiki 11 section 4).
5. An expiry for temporary passwords (ARV-088), and whether their 115 bits meet ASVS V11.5.1.
6. Rotation procedures for the Kafka, Redis, SMTP, log store and registry credentials.
7. Proposed: custody of the backup identity sealed with two people (wiki 04 section 10.1).
8. Proposed: the go-live record lists every site key with its identifier, custodians, readers and next rotation date (section 3.9, rule 2), and every rotation and destruction is recorded in the operations log (rule 3).
9. Built by ARV-207, values Proposed: expired key ring entries are deleted 30 days after they expire once no stored value names them (`Cronz:KeyLifecycle:ExpiredKeyRetentionDays`, at least 30), and the destruction rule for a previous Data Protection certificate (section 3.8, rule 3: no key it wraps left in the ring, no unreadable value, no kept backup needs it). To confirm by the owner: the 30 days and the rule.
10. Proposed: a tool to revoke the Data Protection keys wrapped by a compromised certificate and to end every session of a deployment (section 4.2; inventory section 5, item 8).
11. Built by ARV-207: the age of every dated key and credential (`ariva.keys.age`), the countdown to the Proposed cryptoperiods of those rotated by hand (`ariva.keys.rotation_due_in`, `Monitoring:Cryptoperiods`), the CA files' ends, and the SigNoz rules `keyRotationDue` and `keyRingHeld` (inventory section 5, item 9). To confirm by the owner: the periods (item 1) and the alert days (30 and 7). Keys that carry no date (K1, K2, K8, K22, K24, K34 to K40) stay with the records of section 3.9.
12. Proposed: rotate every key a person could read when that person leaves (section 3.5, rule 1); the local partner keeps no copy of a device credential (section 1).
13. How the rule that Dalil staff never hold a site's keys applies where a contract has Dalil deploy Ariva (wiki 19 section 6: Dalil deploys, the customer operates).
14. Who issues device client certificates per site (K15), and whether Ariva should check their validity period (inventory section 5, item 13).
15. Whether the sensing archive day keys should be protected or kept out of the backups (inventory section 5, item 12).
16. Whether api-ingest and api-stream need the Data Protection certificate (inventory section 5, item 17).
17. Whether the Azure DevOps release should write `dalilacr-secret` itself, as the GitHub release does (inventory section 5, item 19).
18. Whether the runtime database login should be set from a SCRAM verifier, as the validation reader is (inventory section 5, item 21).
19. Outbound TLS versions and suites; `Kafka:SslCaLocation` required in k8s-prd (inventory section 5, items 14 and 15; item 16, the collectors on the site's PKI, was closed by ARV-200).
20. The repository settings that release signing rests on: the `v*` tag ruleset and the protection of `main` (TC-54, wiki 04 section 6.5).
21. Proposed: the compromise check for release signing against the transparency log (section 4.15).
22. Who keeps the etcd backups and node disks where deleted Secrets may remain (section 3.8, rule 5).
23. Closed by ARV-211: the key ring has a signing certificate of its own (K49, section 4.2a), so the Data Protection certificate only wraps. To confirm by the owner: K49's Proposed cryptoperiod (2 years), the upgrade window procedure (`DataProtection:Signing:CertificateSignedUntil`, wiki 04 section 6.2) and that a previous signing certificate is configured by its certificate alone (inventory section 5, item 22).
24. The partner-side checks: whether each `HmacSignature` partner checks the timestamp (replay), and who makes the HMAC key (inventory K19, A7).
25. Dalil's security contact and the site contacts of wiki 13 section 11 (To be added there).
26. Closed by the ARV-211 review (finding M1): each key ring row carries a row signature by K49 over its friendly name and exact text (script 0073), checked before the row is parsed, so a genuine element copied into a key with another id, other dates or another descriptor is refused, and two rows of one key id enter the ring once. Residual: inside the upgrade window `DataProtection:Signing:CertificateSignedUntil` the rows written before ARV-211 carry no row signature and are read, so a database writer could add an unsigned copy of such a key (read only as an expired copy) or an unsigned revocation (an availability risk, as before); the window is at most 90 days and the hosts warn while it is open.
27. To confirm, after the owner's sign-in freeze: a bound on `Auth:Sessions:RefreshGraceSeconds` (30 seconds by default, no check), so a refresh token's successor stays short-lived next to the 30 days of section 3.8, rule 3 (a sign-in setting, left as it is under the freeze). And two residuals of the ARV-211 upgrade check (`KeyRingUpgradeCheck`): the migration job counts a key as signed when a row of its id carries any row signature in the shape script 0073 holds, without verifying it (it holds no signing certificate), so a database writer who plants such a row and points a value at it could keep the job from refusing an upgrade without the window; the hosts verify, and would still refuse at start. And a writer who holds the runtime login could point every stored value at keys from before ARV-211 (or at planted stubs), which the hosts then take for the never-upgraded case and refuse to start: a writer who can rewrite every stored secret has already made them unusable; recovery is the database backup.
28. Accepted gap, To confirm by the owner: the per-endpoint outbound client certificates (K20) and pinned CAs (K21) have no age or end in the metrics (a tag per endpoint is not allowed); their ends are checked by hand (wiki 10 section 4.9; inventory section 5, item 9).
