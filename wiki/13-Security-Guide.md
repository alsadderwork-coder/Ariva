# Security guide

For Dalil engineers, DevOps, and the customer's security reviewers. It summarises how Ariva is protected, what the customer's cluster must provide, and how security is checked on every change. The engineering reference is `../docs/security/cwe-controls.md`; it is checked on every story, pull request and release. `../docs/security/asvs-l2.md` maps every OWASP ASVS 5.0 Level 2 requirement to its control and test, or to the gap and the story that closes it (ARV-074), for tenders that cite ASVS.

## 1. Security model in brief

| Principle | How |
|---|---|
| No inbound internet | Users through the ingress on the site network; sensors on their own VLAN behind the gateway; displays on their own VLAN reading a read-only endpoint |
| Default deny | Every endpoint needs a user permission, an integration scope or device authentication; a test fails the build if one does not |
| Least data | No images, no biometrics, no officer or passenger identity; AMAN sends aggregates only; only lane-level aggregates leave a border deployment |
| Separate principals | Users and integration clients have separate signing keys and token audiences; devices have a scheme of their own (a per-device credential, never a token), accepted only on device endpoints and nowhere else |
| Integrity | Zone profiles and contracts are immutable versions; the runtime database role cannot change raw hypertables; evidence packs are sealed with SHA-256; a golden replay of the archive hash-chains its inputs and outputs and is recorded append-only in `replay_run` (time, login and a chain link set by the database), so a change to an exported replay is detected; the archive and that record are as trustworthy as the runtime database login (ARV-036); everything is audited |
| On premises, in country | Every deployment runs in the customer's environment; no telemetry leaves without consent. Library telemetry is off in code: MassTransit's usage report (versions, OS, time zone, topic names) is disabled where every host registers the bus, a host refuses to start if `MASSTRANSIT_USAGE_TELEMETRY` turns it back on, and the E2E log scan fails a run that reports it (ARV-097) |

## 2. Control summary (14 CWEs)

| CWE | Threat in Ariva | Main control |
|---|---|---|
| CWE-78 OS command injection | A device name, file name or AODB field reaches a shell | Ariva never starts processes; a test checks no Ariva assembly references `System.Diagnostics.Process` |
| CWE-77 Command injection | Kafka or Helm commands assembled from input | No runtime command assembly; topics only from a fixed list; Helm only in pipelines |
| CWE-94 Code injection | Rule expressions, templates or dynamic queries executing input | Alert rules are typed data; no scripting, dynamic LINQ or polymorphic JSON; report templates are embedded resources; CSP `script-src 'self'` |
| CWE-918 SSRF | Pull URLs pointed at internal services or cloud metadata | All outbound calls through registered endpoints: administrator-created with step-up MFA, HTTPS, allowed CIDRs checked at call time, no redirects, relative paths only |
| CWE-862 Missing authorisation | A new endpoint ships without a check | Default-deny fallback policy; endpoint inventory test boots every host and fails on any endpoint without authorisation metadata unless allowlisted |
| CWE-863 Incorrect authorisation | Cross-site reads; a duty manager seeing per-desk border data; one handler seeing another | Permission policies; site scope on every query; field-level projection per role; integration clients bound to scopes and sites; alerts visible and actionable only by the roles responsible for them, also on the live hub (ARV-039); authorisation matrix tests and IDOR tests |
| CWE-306 Missing authentication for critical functions | Publishing a profile, signing a contract, creating clients, changing roles without fresh authentication | Authentication everywhere except the health probes; step-up MFA (TOTP within 15 minutes) on critical functions |
| CWE-287 Improper authentication | Weak storage, TOTP replay, token confusion | PBKDF2-SHA256 with 600,000 iterations for passwords and client secrets; constant-time comparison; TOTP replay guard; lockout and rate limits; strict JWT validation (issuer, audience, lifetime, key, algorithm allowlist, 30 s skew); one generic error for every login failure |
| CWE-93 CRLF injection (email headers) | A rule name, zone or address adding a header or a recipient to an alert email | Recipients are one plain address, checked when an administrator sets them, again when an email is written and again when it is sent, with a database check; template values are made one line of printable text (control, line-break and format characters removed, clipped); the subject is one line; MimeKit encodes every header; one recipient per message (ARV-040) |
| CWE-501 Trust boundary violation | Unvalidated vendor, AODB or request data reaching the domain | Request models validated before mapping; entities never bound from requests; vendor and AMAN payloads validated, size-limited and mapped at the adapter edge |
| CWE-269 Improper privilege management | Self-elevation, over-privileged services | Grants only by a higher-ranked granter, no self-grants, System administrator grants need a second administrator; non-root containers; separate database roles for runtime and migrations, and a third login of its own for the validation service, the only one that reads the shadow nowcast (ARV-104g1: every host refuses to start if its runtime login can read a value of it: the reader role or `pg_read_all_data` held in any way, a superuser, or a grant on a value column of the table or a chunk to it, a role it holds or PUBLIC); per-service Kafka ACLs; the GitHub dev release runs on `main` only, in the `k8s-dev` environment, with a namespace-scoped kubeconfig and read-only package access |
| CWE-384 Session fixation | A pre-login session surviving sign-in | Stateless 15-minute access tokens; new session id at every sign-in; rotating, hashed refresh tokens with family revocation; HttpOnly, Secure, SameSite Strict cookie on `/auth` |
| CWE-89 SQL injection | Filters, sort fields, Timescale queries built from strings | Parameterised queries only; binary COPY with typed writers; Timescale DDL only from versioned scripts; allowlisted sort and filter fields |
| CWE-120 Buffer overflow | Oversized or deeply nested payloads | Managed code only, unsafe blocks disabled; body limits (APIs 1 MB, sensor pushes 256 KB, file imports 20 MB streamed); JSON depth 32; bounds-checked binary parsing; Kafka messages up to 1 MB |
| CWE-79 Cross-site scripting | Names, notes, flight data or messages rendered as HTML | Svelte text interpolation only (`{@html}` banned); strict CSP; `X-Content-Type-Options: nosniff`; CSV exports neutralise formula prefixes |

## 3. Authentication and MFA

| Principal | Method | Token |
|---|---|---|
| Users | Ariva's own sign-in (ADR-0026): PBKDF2 password hashing, TOTP required for every account (an account without it gets only the pending scope until it enrols), step-up with a fresh TOTP code for critical actions, lockout and per-address limits. Sign-in through an external identity provider (OIDC, D5's option) is not built | Access token 15 minutes, in memory in the browser; rotating refresh cookie (`__Secure-ariva_rt`) |
| Integration clients | Client id, client secret and TOTP (AMAN-compatible flow, hardened) at `POST /api/v1/auth`; optional `X-TOTP-Code` on every call | JWT, audience `ariva-integration`, 15 minutes, no refresh token |
| Devices | Per-device credential over HTTPS (`Authorization: Bearer ardk_...`, `X-Ariva-Device-Key`, or Basic with the device code as user name), optionally limited to source networks and bound to a pinned client certificate; client certificate or username and key over MQTT with TLS | Device authentication on Ingest endpoints (ARV-022): 401 for a missing, wrong, revoked or out-of-network credential and for a user token; 403 for the zone of another device; limited per device |
| Display players | The display's code (query `code`) and its own credential in `X-Ariva-Display-Key` (ARV-058), compared as SHA-256 in constant time; only the board endpoint accepts it | None; limited per credential |
| Cronz job dashboard | An operator key checked against `Cronz:Dashboard:KeySha256`; off unless enabled; restrict its ingress to the administration network (wiki 04). Not tied to a user or MFA (ASVS V6.3.3, story ARV-087) | None |
| Simulator operators (non-production only) | A simulation key checked against configured SHA-256 digests (`SimulationKeyHandler`, scheme `Ariva.SimulationKey`, in `Ariva.Simulation.Api/Security/SimulationKeyAuthentication.cs`); the simulator refuses to run in k8s-prd | None |
| Services | Database and Redis passwords, Kafka SASL/SCRAM over TLS in production (shared logins per deployment today). Database logins: the runtime login every host uses (DML only), the migration login the migration job uses (DDL), and the validation reader login (ARV-104g1), which only the validation service in api-main uses, to read the shadow nowcast and nothing else. Mutual TLS, per-service identities and network policies between services are not built yet (ASVS V12.3, V13.2, stories ARV-082, ARV-083 and ARV-085) | |

Ariva.Web (ARV-051, ADR-0026) keeps the access token in memory only, never in web storage or a readable cookie (CWE-384). A reload or a new tab gets a token from the refresh cookie (`__Secure-ariva_rt`: HttpOnly, Secure, SameSite=Strict, Path=/api/auth), which only Ariva.Api.Main on the web origin receives: the ingress routes `/api` and `/hubs` on the web host to Main, so the web app makes no cross-origin call and `connect-src 'self'` covers it. One tab refreshes at a time (Web Locks API) and shares the new token and sign-out with the other tabs over a BroadcastChannel; the server's refresh grace period covers a tab that refreshes with a token another tab has just rotated. A non-secret `ariva-signed-in` flag in local storage only spares a signed-out visit the refused refresh. A 401 `mfa_required` (RFC 9470) opens the step-up dialog and sends the action once more; a 401 `session_expired` returns to sign-in; the page to return to is a path on the web origin only (no scheme, no `//`, no backslash), so a crafted link cannot redirect off the site. The sidebar hides screens by the permissions of `GET /api/auth/me`; the server checks every call. Text from the server and the user is rendered by interpolation only, and the QR code is an SVG path built in the browser (no `{@html}`, no innerHTML). Floor plans (ARV-053) are fetched with the token and drawn by an SVG `<image>` from an object URL, so `img-src` adds `blob:`; an image never runs a plan's script, and the server strips scripts from SVG plans when they are uploaded.

Ariva's integration authentication follows AMAN's flow but adds hashed secrets, a TOTP replay guard, one generic error, rate limits with lockout, UTC-only time handling and separate keys per principal type.

## 4. Authorisation and the data boundary

- Permissions are declared as constants (`Area.Entity.Action`) and attached to every controller action with the `Permission` attribute; integration endpoints carry `[IntegrationScope]`; sensor endpoints carry device authentication; SignalR hubs check the permission on every group join.
- The live hub (`/hubs/live`, ARV-035) needs `LiveQueue.View` to connect and checks every `JoinZone` against the caller's stored permissions and sites, and the site's published zone profile, answering the same `forbidden` for another site's zone and an unknown one; a connection holds at most 64 zones and makes at most 120 joins a minute, a session at most 8 connections per replica, messages are limited to 8 KB and errors carry no detail. The access token may come as the `access_token` query value only on `/hubs`, only when no Authorization header is present, once and at most 4 KB; application logs redact it and the `/hubs` ingress (`tokenInQuery` in the chart, checked by the chart security test) writes no access or audit log and only critical errors. Snapshots taken from Redis are size-limited and checked before they are sent.
- Every site-bound query and command goes through a site scope; organisation-level tenancy uses NHibernate filters with PostgreSQL row-level security on contract, SLA and alert tables as a second line.
- The data boundary is physical where it matters most: border and airport are separate deployments, and only lane-level aggregates cross, one way. Inside an airport deployment, a handler sees only its own desks, contracts and alerts.
- AMAN contracts carry no person, document or officer identifiers; a unit test fails the build if a contract property name looks like one.

## 5. Secrets management

| Secret | Where it lives |
|---|---|
| Database, Kafka, Redis, SMTP credentials | `appsettings.*.<env>.json` written into Kubernetes secrets by the release pipeline from a secret variable group (or by the customer's process); never in the repository |
| Validation reader login (ARV-104g1) | Its own Kubernetes secret, `ariva-validation-reader` (keys `username` and `password`), given only to api-main and the migration job as environment variables from the secret (never an appsettings file, which the base files would spread to every host); required in k8s-prd. The chart test fails if another workload gets it, a unit test if a committed appsettings file carries it, and outside vm-local any host but api-main refuses to start with it. The migration job sends PostgreSQL only a SCRAM-SHA-256 verifier of the password, never the password itself (the runtime login, older, is still set from its plain password: keep statement logging off on the database) ([Deployment guide](04-Deployment-Guide.md), sections 6.2 and 7.1) |
| Registry credentials | `dalilacr-secret`, rendered from pipeline variables |
| Integration client secrets | Stored only as PBKDF2 hashes |
| TOTP seeds, outbound endpoint secrets | Encrypted with the Data Protection key ring (AES-256-GCM for keys since ARV-080; the ring itself wrapped with RSA-OAEP-SHA256 and AES-256-GCM under an RSA 3072 certificate) and re-protected under the current key by the daily `SecretReprotection` job |
| Token signing keys | Separate keys for users and integration clients; devices use per-device credentials stored as hashes, not tokens |
| TLS keys and certificates | Customer PKI; ingress or controller secrets |

The key ring table (`data_protection_key`) admits only signed keys in Ariva's format (ARV-080), so a key planted there is ignored. A planted revocation is still possible for anyone who can write to the database: values under the revoked keys stop working (users re-enrol their authenticator, outbound secrets must be entered again, everyone signs in again). If that happens, delete the planted `revocation` row with the migration (administrator) login, since the runtime login cannot delete from that table, and restart the hosts, or restore the database from backup; and treat it as a database compromise.

At rest, secrets stay in Kubernetes secrets or a vault (D5). Enable encryption at rest for Kubernetes secrets on the customer's cluster. Back up the Data Protection key ring and signing keys separately and encrypted (see [Deployment guide](04-Deployment-Guide.md), backup section).

## 6. TLS

TLS on every external hop: users, displays and integrators to the ingress; sensors to the gateway (HTTPS, MQTT over TLS with client certificates); outbound integrations (HTTPS, optional client certificate and pinned CA); the border-to-airport feed (mutual TLS, Proposed). Inside the cluster the current chart uses plain HTTP between the ingress and pods and plain connections to Kafka, PostgreSQL and Redis; production hardening adds encrypted connections and mutual TLS or network policies. Details per hop in [Network and ports](05-Network-and-Ports.md).

## 7. Hardening checklist for the customer's cluster

| # | Item | Notes |
|---|---|---|
| 1 | Supported Kubernetes release, patched | |
| 2 | Encryption at rest for Kubernetes secrets enabled | |
| 3 | RBAC: deployment credentials limited to the Ariva namespace | The dev pipeline uses a kubeconfig from a secret variable |
| 4 | Network policies: default deny in the Ariva namespace; allow only the flows in the firewall matrix | |
| 5 | Ingress controller: ModSecurity on, snippet annotations allowed only for Ariva's reviewed annotations, host check active (421 on wrong host) | Ingress-nginx retirement status to review |
| 6 | Pods run as non-root; add `runAsNonRoot`, read-only root file system where possible, dropped capabilities | Images run as a non-root user today; chart `securityContext` is a Target procedure, implemented in Phase 1 epic Hardening |
| 7 | Images pulled from the Dalil registry or the site mirror only, pinned by build number | Never deploy a branch tag such as `trunk` in production |
| 8 | Base images patched; pin the web image's nginx version | The web Dockerfile uses `nginx:latest` today |
| 9 | Separate database roles: migrations with DDL, runtime without DDL and without update or delete on raw hypertables, and the validation reader login with the reader role only (ARV-104g1) | Check with `\du`: the runtime login holds `ariva_runtime` only, never `ariva_validation_reader` or `pg_read_all_data` (hosts refuse to start otherwise); the reader login holds `ariva_validation_reader` only. Statement logging (`log_statement = 'all'`) stays off: the runtime login's password is set by the migration in plain text |
| 10 | Kafka: TLS listeners, per-service ACLs, `ariva.` prefix ACLs in a shared cluster | |
| 11 | PostgreSQL and Redis: encrypted connections, strong passwords from the secret store | |
| 12 | TickerQ dashboard and Kubernetes API reachable only from the management network | |
| 13 | Vendor cloud connectivity disabled on sensors and perception platforms at government sites; 802.1X on sensor switch ports | |
| 14 | NTP on all nodes and devices | |
| 15 | Central log collection and audit log retention agreed | |
| 16 | Backups encrypted, restore tested | |

## 8. Security gates in the pipeline

| Layer | Tool | When | Fails the build |
|---|---|---|---|
| 1. Edit-time scan | Post-edit hook runs the scanner on each file an agent writes | Every edit by an AI agent | Feeds findings back immediately |
| 2. Compiler analyzers | .NET security analyzers (CA2100, CA3001 to CA3012, CA2300 to CA2330, CA5350 to CA5403) as errors | Every `dotnet build` | Yes |
| 3. Repository scan | `node scripts/security/scan.mjs` (39 rules mapped to the 14 CWEs, self-tested against good and bad fixtures) | `verify.mjs security`, PR pipeline | Yes, on any error |
| 4. Architecture tests | Reflection tests in `Ariva.UnitTests` (forbidden references, endpoint inventory, entity binding, unsafe code, layering, data boundary) | `dotnet test` | Yes |
| 5. Behaviour tests | Unit tests per control, API and Playwright tests with attack payloads in `Platform/Testing/Ariva.E2E/tests/security` | `verify.mjs e2e`, PR pipeline | Yes |
| 6. Dependency audit | `dotnet list package --vulnerable --include-transitive`, `npm audit --audit-level=high` | `verify.mjs security`, nightly pipeline | Yes for high and critical |
| 7. Dynamic scan | OWASP ZAP 2.17 (pinned by digest): API scans of the Main, Ingest and Integration OpenAPI documents with an administrator's token without a second factor (Ingest's device endpoints and Integration's client endpoints take their own credentials, so there the scan runs as an unauthenticated caller), and baseline scans of every host and of the web app under its production nginx configuration, against the E2E stack; a passive baseline of the dev deployment when `ARIVA_DEV_WEB_URL` is set (ARV-063) | `security-zap.yml` weekly and on demand, `node scripts/verify.mjs zap` | Yes for high (accepted alerts in `security/zap-triage.json`) |
| 8. Penetration test | External, before the pilot goes live | Once per major release | Release gate |

Exceptions go to `security/allowlist.json` with a reason, a proposer, an approver and a date. An entry whose approver starts with `PENDING` stays visible as a warning in every report; only a human approver replaces it. Today one exception is pending: the anonymous health probes (`SEC-052`), which return only a status word and are excluded from OpenAPI.

Current state (1 October 2026): the analyzers (layer 2), the repository scan (layer 3), the layering and data-boundary tests (part of layer 4) and the dependency audits (layer 6) run through `node scripts/verify.mjs security` and `dotnet test`. The PR validation pipeline (`Analyze-solution.yaml`) runs the build, the unit and integration test projects and the web checks, but not yet the repository scan or the audits; adding them is a Target procedure, implemented in Phase 0 epic Skeleton and platform. The behaviour test project, the nightly pipeline and the ZAP pipeline are planned. Update (5 October 2026): the dynamic scan (layer 7) runs in `security-zap.yml` and through `node scripts/verify.mjs zap` (ARV-063); a High risk alert fails it unless a person approved an entry in `security/zap-triage.json`, and lower risks are triaged in `backlog/progress.md`.

## 9. Vulnerability management

| Activity | Frequency | Owner |
|---|---|---|
| Dependency audit (NuGet and npm) | Every security verification and nightly | Dalil |
| SBOM and vulnerability scan per release | Each release | Dalil |
| Base image updates | Each release, and out of band for critical issues | Dalil |
| Security fixes for supported versions | Per the support agreement ([Support and maintenance](19-Support-and-Maintenance.md)) | Dalil |
| Sensor and perception platform firmware advisories | When the vendor publishes | Local partner with Dalil |
| Mapping to the customer's national standard | Confirmed per bid | Dalil product owner |

## 10. Penetration testing

An external penetration test runs before the pilot goes live and once per major release, with a client-approved scope. The test is a release gate. Security accreditation with the client is part of the pilot's critical path (2 to 8 weeks in the roadmap assumption).

## 11. Incident response

| Contact | Details |
|---|---|
| Dalil security contact | To be added |
| Dalil support (24x7 line, if contracted) | To be added |
| Customer security operations | To be added per site |
| Local integration partner | To be added per site |

Steps: contain (suspend the affected integration client or user, isolate the node or network segment), preserve evidence (audit log, logs, the affected database state), notify the customer and Dalil within the agreed time, eradicate and recover, and review. Data protection notification duties depend on the jurisdiction (see [Privacy and data protection](14-Privacy-and-Data-Protection.md)).
