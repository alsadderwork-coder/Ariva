# Administration guide

For each deployment's System administrator and for the duty managers who own operational configuration. It covers users and roles, permissions, TOTP, integration clients and outbound endpoints, devices, zone profiles, alert rules, displays, the audit log and data retention.

Status: administration screens arrive with Phase 1 (epics Authentication, roles and audit; Zone editor; Alerting) and v1 (contracts). Everything below is the target behaviour, taken from the design, the security controls and the prototype. In Phase 0, zones come from a configuration file; user, role and audit administration exist as an API (ARV-011, section 2) without screens yet.

## 1. Principles

- Default deny. Every endpoint requires a permission (users), a scope (integration clients) or device authentication (sensors); anything else fails the build's endpoint inventory test.
- Permission policies, not role strings. Permission codes have the form `Area.Entity.Action` (for example `Ops.Zones.View`, `Admin.Zones.Edit`) and are declared in `Ariva.Core/Global.cs`.
- Site scope on every query and command: a user of one site never reads another site's data.
- The data boundary is enforced by field-level projection per role: a Terminal duty manager never receives per-desk border data, and nobody receives officer identity.
- Critical functions require step-up MFA: a TOTP check within the last 15 minutes.
- Everything that changes configuration, decides an SLA question or exports data is audited.

Critical functions (step-up MFA required):

| Function | Who |
|---|---|
| Publish or activate a zone profile | Site administrator or the duty manager who owns the zones |
| Sign a handler contract (v1) | Terminal duty manager |
| Create, change or rotate an integration client | System administrator |
| Create or change an outbound endpoint | System administrator |
| Grant or revoke roles, create users, reset a password or TOTP | System administrator (a second approving administrator for System administrator grants is a Phase 1 candidate) |

## 2. Users and roles

| Field | Rule |
|---|---|
| Display name | Free text |
| Organisation | Border authority, airport operator, a handler (for example Handler B), or a security contractor |
| Role | Only roles valid for that organisation and deployment: Border shift supervisor in a border deployment; Terminal duty manager and Handler station manager in an airport deployment; System administrator in either |
| Deployment | Derived and enforced: a border deployment user cannot hold an airport role, and the reverse |
| Sign-in method | OIDC through the bundled Keycloak or the customer's identity provider (D5), or Ariva's own sign-in with PBKDF2 password hashing and TOTP (security controls). Which applies per site: To confirm |
| Expiry | Optional end date for temporary accounts |

Role grant rules:

- Grants only through the role assignment service: the granter's own role must rank at least as high as the role (System administrator above the three operational roles), nobody changes their own roles, and the last active System administrator keeps the role.
- Every grant and revoke needs step-up MFA and is audited; a revoke ends the user's sessions.
- Role codes (`BorderShiftSupervisor`, `TerminalDutyManager`, `HandlerStationManager`, `SystemAdministrator`) never change once shipped.

What each role sees and creates is in [Product overview](01-Product-Overview.md) (Roles). The authorisation matrix (`security/permission-matrix.json`, role by endpoint by expected status) is the reference; it drives the authorisation tests.

Phase 0 API (ARV-011), under `api/v1/admin`: `users` (search with text, role and disabled filters and an allowlisted sort; view; create; update display name and email; `reset-password`; `reset-totp`; `roles/{role}` PUT to grant and DELETE to revoke; `unlock`; `disable`; `enable`), `roles` (the four roles, their rank and permissions) and `audit-entries` (search and view). Sites (ARV-012): `api/v1/admin/sites` creates and renames sites (codes never change, sites are never deleted) and `users/{id}/sites` sets a user's access to every site or a list (critical, only within your own sites; narrowing ends the user's sessions). Everyone reads only their sites through `api/v1/sites`. An administrator limited to some sites manages only accounts inside them and cannot create sites; the break-glass account and the administrators that existed before site scoping reach every site. A new account and a password reset get a temporary password of four groups of five characters, shown once; the user must change it at the next sign-in. Accounts are disabled, never deleted. The break-glass account does not appear here.

Sessions: access tokens last 15 minutes; a new session id is minted at every sign-in; refresh tokens rotate on every use, are stored hashed, and reuse of an old refresh token revokes the whole family. The refresh cookie is HttpOnly, Secure, SameSite Strict.

## 2a. Site topology (Phase 0 API)

ARV-014 adds `api/v1/admin/airports`, `terminals`, `levels`, `checkpoints` and `desks`, each with search (text, parent, site, an allowlisted sort, pages of at most 500), view, create, update and delete.

| Entity | Rules |
|---|---|
| Airport | IATA code (three letters, never changed), optional ICAO code, name, IANA time zone. Deployment-wide: only an administrator with every site creates, changes or deletes airports; every topology reader can read them |
| Terminal | Code unique in the airport; belongs to one site, fixed at creation, which every level, checkpoint and desk under it inherits |
| Level | Code and floor number unique in the terminal; width and depth in metres (up to 2,000) define the floor coordinate system for zones |
| Checkpoint | Kind `CheckIn`, `Security`, `Emigration` or `Immigration`, fixed at creation |
| Desk | Kind by checkpoint: `Counter` at check-in, `SecurityLane` at security, `Desk` and `EGate` at emigration and immigration; border desks serve at least one lane category and e-gates include `EG`; can be taken out of service |

`POST desks/range` creates a numbered range in one go (for example prefix `D`, 1 to 22, width 2 gives D01 to D22): at most 200, every code valid and free, otherwise nothing is created. `desk-code-mappings` maps another system's code to a desk: AMAN desk and e-gate codes to border desks and e-gates, AODB counter codes to check-in counters; a code is unique per system and site, a desk has one code per system, and the feeds resolve codes through these mappings and park what they cannot resolve. Whether AODB also sends security lane codes is To confirm per airport.

Codes are 1 to 16 upper case letters or digits with single inner hyphens. Deletes are soft (history and zone profiles keep their references), refused while live records exist underneath, and free the code for reuse. Everyone sees only the records of their sites; another site's record answers like one that does not exist. Every change is audited.

## 3. TOTP enrolment and reset

Enrolment:

1. Administrators enrol TOTP at first sign-in, before any other action. Other roles: whether MFA is mandatory is To confirm per site (D5 requires MFA for administrators).
2. Ariva shows a QR code and the `otpauth://` URI once; the user scans it into an authenticator app and confirms with a current code.
3. TOTP follows RFC 6238 with a replay guard: a code cannot be used twice.

Reset (lost or replaced phone):

1. The user asks another System administrator; nobody resets their own TOTP.
2. The administrator verifies the user's identity by the site's procedure (To confirm per customer).
3. The administrator resets the enrolment with step-up MFA; the user's sessions and refresh tokens are revoked.
4. The user enrols again at next sign-in. The reset is audited with both identities.

If the last System administrator loses access, recovery follows the bootstrap procedure in the [Deployment guide](04-Deployment-Guide.md) (section 7.3) and needs Dalil support and the customer's change control.

## 4. Integration clients

Created for each AODB, immigration system, sensor gateway or consumer of the wait-times API.

| Field | Notes |
|---|---|
| Name | The external system |
| Kind | `Aodb`, `Immigration`, `SensorGateway`, `Other` |
| Scopes | `flights:write`, `allocations:write`, `immigration:write`, `sensing:write`, `queues:read`, `displays:read`; grant the minimum |
| Bound site codes | A client bound to one site cannot write another |
| Allowed source CIDRs | Calls from elsewhere fail |
| Per-request TOTP policy | Requires `X-TOTP-Code` on every call; on by default for immigration clients |
| Status | Active or suspended |

On creation Ariva shows the client secret and the TOTP provisioning URI once. Hand them over out of band by two separate channels.

Operations:

| Task | How |
|---|---|
| Rotate the secret or the TOTP seed | Generate new values (shown once); coordinate the switch time with the integrator. Whether old and new can overlap is To confirm |
| Suspend | Set status to suspended; tokens already issued expire within 15 minutes |
| Unlock after lockout | Ten consecutive failures lock a client for 15 minutes and alert administrators. Find the cause (wrong clock, wrong secret, attack) before the integrator retries |
| Review | Every call is audited with client, scope, endpoint, site, result and payload hash |

Details for integrators: [Integration guide](08-Integration-Guide.md).

## 5. Outbound endpoints

Every system Ariva calls is an `OutboundEndpoint` record:

| Field | Notes |
|---|---|
| Base URL | HTTPS only (HTTP only for allowlisted lab hosts). Request paths are relative to it |
| Allowed CIDRs | The resolved IP must be inside them at call time (blocks DNS rebinding) |
| TLS | Optional client certificate, optional pinned CA |
| Timeout, retry, circuit breaker | Per endpoint |
| Authentication handler | `TotpClientCredentials` (the AMAN connector), `OAuth2ClientCredentials`, `ApiKeyHeader`, `HmacSignature`, `MutualTls` |

Secrets are encrypted at rest. Redirects are never followed. Creating an endpoint that points at a link-local, loopback or metadata address is rejected.

## 6. Devices

| Task | Notes |
|---|---|
| Register | Family, model, transport, credential, mounting height, position, coverage footprint, owning queue zone, clock source (NTP or PTP). New devices start in `Commissioning` |
| Record calibration | Method, sample size, counting accuracy, wait-time error, pass threshold (95 percent by default), notes. A pass sets the device `Online` |
| Replace or move | Set back to `Commissioning`, re-calibrate; re-validate penalty-grade zones |
| Retire | Status `Retired`; history is kept |

See [Commissioning and calibration](07-Commissioning-and-Calibration.md).

## 7. Zone profiles

| Task | Notes |
|---|---|
| New draft | Copies the active profile |
| Edit the draft | Zones (queue, service, staff, overflow), entry, exit, count and overflow-entry lines, desks, lanes, lane categories, desk code mappings, e-gate reject target lane, site parameters |
| Publish | Creates the next immutable version; step-up MFA; audited |
| Activate | From a time, or immediately by a supervisor (for example when stanchions move) |
| Correct | Always a new version; affected periods can be recomputed as new revisions |

Every result records the profile version it was computed with.

## 8. Alert rules

Rules are typed data (metric, comparator, threshold), never expression strings.

| Field | Values |
|---|---|
| Name and scope | A queue or group of queues visible to the creator's role |
| Metric | Nowcast wait, realised P90 per 15-minute bin, people queuing, overflow band occupied, sensor offline, desks open below plan (and the arrival-wave forecast) |
| Condition | Comparator and threshold with units; optional minimum queue length |
| Sustain | Raise when the condition holds for N consecutive minutes |
| Clear | Below a clear threshold (or condition false) for N minutes |
| Severity | Info, Warning, Critical |
| Owner role | Who must act |
| Escalation | After N minutes unacknowledged, to a role |
| Channels | On-screen always; email in the MVP; SMS and operations-centre webhook in v1 |
| Suppression windows | Silence planned events |

Before creating, a backtest preview shows how often the rule would have fired on recent data (for example "Would have fired 3 times today, first at 18:05"). Rules can be enabled, disabled and duplicated. One open alert per rule and target.

Seed rules from the prototype (reference values):

| Rule | Condition | Owner | Escalation |
|---|---|---|---|
| R-001 Nowcast above 15 min | Immigration lanes; nowcast above 15 min with at least 10 queuing, sustained 1 min; clears below 12 min | Border shift supervisor | After 10 min to the border operations duty officer |
| R-002 Overflow band occupied | Every queue with an overflow band; sustained 3 min | Zone owner | After 15 min |
| R-003 Sensor offline | All sensors; sustained 1 min | Zone owner | After 15 min to systems |
| R-004 Check-in P90 above SLA threshold | Handler B's islands (contract C-001); P90 above 15 min | Handler B station manager | After 15 min to the Terminal duty manager |
| R-005 Nowcast above 15 min, airport side | Check-in and security; as R-001 | Zone owner | After 10 min |

Rules evaluate nowcasts and forecasts, never realised waits (except the SLA-oriented realised P90 per bin metric, which is informative and not a penalty decision).

## 9. Displays

| Field | Notes |
|---|---|
| Name, level and location | |
| Orientation | Landscape or portrait |
| Checkpoints shown | One or more queues |
| Language order | Per site: Arabic and English (UAE), Portuguese (Angola), Swahili and English (Tanzania) |
| Band size | 5 minutes (or 10) |
| Hysteresis | The band changes only when the nowcast moves a full band from the reference value |
| Stale threshold | After this age the board shows the fallback message (value To confirm per site) |
| Fallback message | In each language |

Each display is a kiosk URL on the display VLAN. Boards never show a realised wait. Whether an existing signage estate (for example Samsung MagicInfo) can show web content is To confirm on site.

## 10. Audit log

Phase 0 (ARV-011) records every user administration action (create, update, grant, revoke, password and TOTP reset, unlock, disable, enable) with time, actor, address, trace id, action code and before and after summaries without credentials, in the same transaction as the change; the API can only read it and the database refuses updates and deletes from the application login. The target scope: recorded for every configuration change, sign, publish, calibration, acknowledgement, SLA decision, export, role change, integration call and administrator action: time, user or client, role, action, object id, summary and, for integration calls, the payload SHA-256. The audit log is kept indefinitely and is served from PostgreSQL (no OpenSearch in the MVP). Exports of the log are themselves audited.

## 11. Data retention settings

| Data | Default | Configurable by | Notes |
|---|---|---|---|
| Track ids | Rotated at zone exit; never past the operating day | Not configurable | Privacy rule |
| Raw track samples, zone events, device health | Contract dispute window, default 90 days; compressed after one day | System administrator, per deployment (dispute window) | Storage scales with the window |
| Interval results, desk intervals, forecasts, border lane KPIs, report aggregates | Indefinite | | |
| Configuration, contracts, SLA decisions, alerts, audit | Indefinite, audited | | |
| Evidence packs | To confirm (at least the contract's dispute and audit periods) | | |
| Flight data | To confirm | | |
| Ariva Kafka topics | Short 3 days, medium 14, long 30, or compacted (Proposed) | DevOps, per topic | |
| `aman.feed` topics | AMAN owns | AMAN | |
| Logs and traces | To confirm; Proposed: no track ids or desk codes in log messages beyond the operating day | DevOps | |
| Backups | Proposed: no longer than the dispute window plus the backup cycle | DevOps | Backups contain raw samples |

See [Privacy and data protection](14-Privacy-and-Data-Protection.md).

## 12. Licence

Modules are enabled by a signed, offline-verifiable licence file per deployment. Loading and renewing it is a System administrator task (mechanism To confirm; complete in v1).
