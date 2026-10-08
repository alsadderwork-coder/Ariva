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
| Close a validation campaign (ARV-104a) | Border shift supervisor, Terminal duty manager or System administrator of the site |

## 2. Users and roles

| Field | Rule |
|---|---|
| Display name | Free text |
| Organisation | Border authority, airport operator, a handler (for example Handler B), or a security contractor |
| Role | Only roles valid for that organisation and deployment: Border shift supervisor in a border deployment; Terminal duty manager and Handler station manager in an airport deployment; System administrator in either; Validation observer in either, for the people who count during a validation campaign (section 2c) |
| Deployment | Derived and enforced: a border deployment user cannot hold an airport role, and the reverse |
| Sign-in method | Ariva's own sign-in: PBKDF2 password hashing, TOTP for every account and step-up for critical actions (ADR-0026, security guide section 3). Sign-in through the customer's identity provider (OIDC, D5's option) is not built |
| Expiry | Optional end date for temporary accounts |

Role grant rules:

- Grants only through the role assignment service: the granter's own role must rank at least as high as the role (System administrator above the three operational roles and the validation observer), nobody changes their own roles, and the last active System administrator keeps the role.
- Every grant and revoke needs step-up MFA and is audited; a revoke ends the user's sessions.
- Role codes (`BorderShiftSupervisor`, `TerminalDutyManager`, `HandlerStationManager`, `SystemAdministrator`, `ValidationObserver`) never change once shipped.
- A validation observer (ARV-104a) holds one permission, `Validation.Capture`: it records manual counts for the running validation campaigns of its sites and corrects its own counts, and nothing else (no operational screen, topology, report or campaign management). Give the account the campaign's site only: Ariva refuses every site for an account whose only role is Validation observer, when you create it, change its sites, grant the role or revoke its other role (400). An account that also holds another role follows that role. The person who plans or starts a campaign never counts for it, even with the observer role as well (403). Observers may be border officers seconded to the campaign: Ariva stores only their account id with each count, and the account itself holds the name you give it, so use a neutral user name and display name (for example `obs.amm.07`) where the data boundary requires it.

What each role sees and creates is in [Product overview](01-Product-Overview.md) (Roles). The authorisation matrix (`security/permission-matrix.json`, role by endpoint by expected status) is the reference; it drives the authorisation tests.

Phase 0 API (ARV-011), under `api/v1/admin`: `users` (search with text, role and disabled filters and an allowlisted sort; view; create; update display name and email; `reset-password`; `reset-totp`; `roles/{role}` PUT to grant and DELETE to revoke; `unlock`; `disable`; `enable`), `roles` (the five roles, their rank and permissions) and `audit-entries` (search and view). Sites (ARV-012): `api/v1/admin/sites` creates and renames sites (codes never change, sites are never deleted) and `users/{id}/sites` sets a user's access to every site or a list (critical, only within your own sites; narrowing ends the user's sessions). Everyone reads only their sites through `api/v1/sites`. An administrator limited to some sites manages only accounts inside them and cannot create sites; the break-glass account and the administrators that existed before site scoping reach every site. A new account and a password reset get a temporary password of four groups of five characters, shown once; the user must change it at the next sign-in. Accounts are disabled, never deleted. The break-glass account does not appear here.

Sessions: access tokens last 15 minutes; a new session id is minted at every sign-in; refresh tokens rotate on every use, are stored hashed, and reuse of an old refresh token revokes the whole family. The refresh cookie is HttpOnly, Secure, SameSite Strict.

## 2a. Site topology

In Ariva.Web, Topology (ARV-052) shows the tree as five columns, airports to desks: pick the site, then an airport, terminal, level and checkpoint; the details of the item last picked show below. An administrator adds items with Add on each column (Add a range for desks), edits names, floors, sizes, lane categories and in service in the details, deletes with a confirmation on the page, and maps a desk's AMAN or AODB code in the desk's details. Airports are added or changed only by an administrator of all sites. Every other role sees its sites read only; terminal duty managers also see the desk codes, border shift supervisors and handler station managers do not. The screen calls the API below, which checks every change.

ARV-014 adds `api/v1/admin/airports`, `terminals`, `levels`, `checkpoints` and `desks`, each with search (text, parent, site, an allowlisted sort, pages of at most 500), view, create, update and delete.

| Entity | Rules |
|---|---|
| Airport | IATA code (three letters, never changed), optional ICAO code, name, IANA time zone (picked from the list, grouped by region with the current UTC offset; the server checks it). Deployment-wide: only an administrator with every site creates, changes or deletes airports; every topology reader can read them |
| Terminal | Code unique in the airport; belongs to one site, fixed at creation, which every level, checkpoint and desk under it inherits |
| Level | Code and floor number unique in the terminal; width and depth in metres (up to 2,000) define the floor coordinate system for zones |
| Checkpoint | Kind `CheckIn`, `Security`, `Emigration` or `Immigration`, fixed at creation |
| Desk | Kind by checkpoint: `Counter` at check-in, `SecurityLane` at security, `Desk` and `EGate` at emigration and immigration; border desks serve at least one lane category and e-gates include `EG`; can be taken out of service |

`POST desks/range` creates a numbered range in one go (for example prefix `D`, 1 to 22, width 2 gives D01 to D22): at most 200, every code valid and free, otherwise nothing is created. `desk-code-mappings` maps another system's code to a desk: AMAN desk and e-gate codes to border desks and e-gates, AODB counter codes to check-in counters; a code is unique per system and site, a desk has one code per system, and the feeds resolve codes through these mappings and park what they cannot resolve. Whether AODB also sends security lane codes is To confirm per airport.

Each level can carry one floor plan (ARV-018) at `api/v1/admin/levels/{levelId}/floor-plan`: upload a PNG, JPEG or SVG of at most 20 MB as multipart field `file` with `metresPerPixel`, `originX` and `originY` (the floor coordinates, in metres, of the image's top left corner). The file type is read from its content, so a renamed file is typed by what it is; raster images may be at most 16,384 pixels a side and 128 megapixels (an A0 sheet at 250 dpi fits). SVG drawings keep shapes, paths, text, gradients, patterns, clip paths, masks and markers with inline presentation styles; scripts, links, style sheets, embedded HTML or images, filters and anything pointing outside the file are removed before storing, so export plans as plain vector drawings (at most 200,000 elements). A host takes one upload at a time; a busy host answers 429 and the upload can be retried. A new upload replaces the previous plan, and a level cannot be deleted while it has a plan. `PUT .../calibration` changes the scale and origin, `GET .../content` returns the image, `DELETE` removes the plan. Uploads and deletes need an administrator; every topology reader can view plans of their sites.

Development and demo deployments start with the fictional Demo International Airport (ARV-019, site `DMO`, terminal `T1`, IANA zone Asia/Dubai): level `ARR` (arrivals, floor 0) with arrival immigration `IMM` (desks AR-01 to AR-22: AR-01 crew, AR-02 to AR-04 citizens, AR-05 to AR-07 residents, AR-08 to AR-22 visitors; e-gates AG-1 to AG-6), and level `DEP` (departures, floor 1) with check-in `CI` (islands A to D, counters A01 to D12), security `SEC-N` and `SEC-S` (lanes N1 to N5 and S1 to S5) and departure immigration `EMI` (DP-01 to DP-22 by the same lanes, e-gates DG-1 to DG-4). Both levels are 100 by 60 metres. Zone profile version 12, published by `demo-seed`, has the prototype's 16 queues and 7 overflow bands with their entry, exit and overflow entry lines. The seed only adds what is missing: it never changes or deletes a record, leaves a draft someone started alone (and then skips v12), stops without writing if existing DMO records belong to another site or have another kind, and creates no users or site grants; give people access to `DMO` like any site. Turn it on with `Seed:DemoTopology` (vm-local) or the Helm value `demoSeed` (demo); production refuses it.

The same deployments also get a second, illustrative site after DMO (ARV-139a, [docs/demo/auh-terminal-a.md](../docs/demo/auh-terminal-a.md)): `AUH-TA`, the arrivals immigration hall of Zayed International Airport (AUH) Terminal A, modelled from public information only. Airport `AUH`, terminal `A`, level `ARR` (lower arrivals level, 200 by 150 metres, an assumption) with arrival immigration `IMM`: counters IC-01 to IC-38 and smart gates SG-01 to SG-34 (both counts reported, unverified), queue zones `A-CRW`, `A-DIP`, `A-CIT`, `A-RES`, `A-GCC`, `A-VIS`, `A-TRF` and `A-EG` by lane, overflow bands `A-VIS-OV` and `A-EG-OV`, a staff and a service zone per counter (`IC-01 staff`, `IC-01 service`, linked to the desk, so the counters' states come from sensors alone, ARV-116), 84 sensors in Commissioning without credentials, an illustrative floor plan and zone profile version 1, published by `demo-seed`. Everything but the cited facts is an assumption. The site is flagged "Illustrative, not surveyed": every screen of the site and its passenger displays show a banner in English and Arabic. Only the seed sets the flag; the sites API ignores it in a body and the runtime database role cannot change it. The seed never writes into a site `AUH-TA` without the flag, so a real deployment of that code is never touched. Replace the illustrative plan with a real one by uploading it on the Zones screen; a real plan is never committed. Give people access to `AUH-TA` like any site.

Codes are 1 to 16 upper case letters or digits with single inner hyphens. Deletes are soft (history and zone profiles keep their references), refused while live records exist underneath, and free the code for reuse. Everyone sees only the records of their sites; another site's record answers like one that does not exist. Every change is audited.

## 2b. Site operating calendar (ARV-118)

The operating calendar decides which minutes count in the pilot's availability criterion (99 percent of operating hours; [KPI definitions](15-KPI-and-SLA-Definitions.md) section 8). There is no screen yet (deferred); administrators use the API `api/v1/admin/sites/{siteCode}/calendar`. Every role reads the calendar of its sites (`GET`, the site's view permission); only administrators of the site change it (the site's edit permission). Every change is in the audit log (`SiteCalendar.*`). Times are the site's local time: the IANA zone of the site's airport (UTC for a site without one); the answer names the zone and today's local date.

| Entry | Request | Rules |
|---|---|---|
| Weekly hours | `PUT .../calendar/weeks` with `effectiveFrom` (a local date `yyyy-MM-dd` after today, at most 366 days ahead) and `hours`, a list of `{ "day": "Monday", "opens": "06:00", "closes": "22:00" }` | Up to 4 intervals a day, none overlapping (Sunday's overnight interval runs into Monday). A closing time at or before the opening time is on the next day (`18:00` to `02:00`); `00:00` to `24:00` is the whole day; a day without intervals is closed. The version is in force from its day until a later version's day. Setting the same day again replaces the version; `DELETE .../weeks/{id}` removes it |
| Dated exception | `POST .../calendar/exceptions` with `date`, `closed` (true with no hours, or false with 1 to 4 intervals) and a `reason` (1 to 200 characters) | Replaces the intervals that open on that local day (the overnight part of the previous day still counts). One exception per day; `DELETE .../exceptions/{id}` removes it |
| Maintenance window | `POST .../calendar/maintenance-windows` with `startsUtc`, `endsUtc` (UTC, whole minutes, ISO 8601 ending in Z) and a `reason` | Starts after now, at most 7 days long and 366 days ahead. Operating minutes inside it count in neither side of the ratio. `DELETE .../maintenance-windows/{id}` cancels it |

Record entries before they take effect: a week or an exception before its local day starts, a maintenance window before it starts (server time). An entry that has taken effect can no longer be changed or removed (409): it is part of the record. Two administrators recording the same day's exception, or a new weekly version of the same day, at the same moment: one succeeds and the other gets 409 (read the calendar and try again). A removed entry disappears from the calendar and stays in the database as deleted, with a `SiteCalendar.WeekRemoved`, `ExceptionRemoved` or `MaintenanceCancelled` audit entry holding what it was. A maintenance window that has already started cannot be recorded, so an unplanned outage stays in the figures; this is the Proposed rule (TC-83). A site without any weekly version in force is open around the clock: every minute counts. A site administrator of one site gets 404 for another site, and an entry of another site through its own site's route answers 404 too.

Example (Dubai, open 06:00 to 22:00 every day from next Monday):

```
PUT /api/v1/admin/sites/DMO/calendar/weeks
{ "effectiveFrom": "2026-10-12", "hours": [ { "day": "Monday", "opens": "06:00", "closes": "22:00" }, ... ] }
```

## 2c. Validation campaigns (ARV-104a, ARV-104b)

A validation campaign proves a site's numbers before anyone relies on them ([Commissioning and calibration](07-Commissioning-and-Calibration.md) section 8; formulas F18). There is no screen yet: the observer tablet is ARV-104c and ARV-104d, the campaign and report screens ARV-104h. Until then use the API under `api/v1/sites/{siteCode}/validation`.

| Who | Permission | Roles holding it |
|---|---|---|
| Reads campaigns, every observer's counts and tracer runs | `Validation.View` | Border shift supervisor, Terminal duty manager, System administrator |
| Reads the border desks of a campaign and every observer's desk states (border data) | `Validation.View` and `BorderDesks.View` | Border shift supervisor, System administrator |
| Plans, starts and closes campaigns (closing needs a second factor within 15 minutes); puts border desks in scope only with `BorderDesks.View` | `Validation.Manage` | Border shift supervisor, Terminal duty manager, System administrator |
| Records and corrects their own counts and desk states, records tracer runs | `Validation.Capture` | Validation observer only |
| Logs, corrects and reads back desk states, and sees the desks to log (border data) | `Validation.Capture`, and either `BorderDesks.View` or no role but Validation observer | Validation observer alone, or with Border shift supervisor or System administrator |

Managers and administrators do not capture (separation of duties, decided by the owner on 2026-10-08): the ground truth stays independent of whoever configures the system or runs the campaign. If a manager or an administrator must count (a paper sheet, for instance), give that account the Validation observer role as well; it still never counts for a campaign it planned or started (403). Handler station managers hold none of the three.

Desk states are border data (first security review of ARV-104b, 2026-10-08): an account that holds an airport role (Terminal duty manager or Handler station manager) and the Validation observer role counts lines and times tracers, but sees no desk in the capture list and is refused (403) on desk logs, desk corrections and its own desk reads. Desk logs come from a Validation observer account with no other role (the border's own observer) or from a border shift supervisor or administrator who also holds the Validation observer role. Do not name people in a correction's reason: it is kept beside a desk and a minute.

| Step | Request | Rules |
|---|---|---|
| Plan | `POST .../validation/campaigns` with `name` (1 to 200 characters), `profileVersion` (the site's published zone profile version you reviewed), `zoneIds` (1 to 50 queue zones of that version), `lineIds` (up to 200 lines of those zones or of their overflow bands), `days` (1 to 31 local dates `yyyy-MM-dd` in the site's time zone, from 31 days back to 366 ahead), optional `targetBinsPerLine` (1 to 2,976) and `targetTracerRuns` (0 to 1,000), and optional `deskIds` (up to 100 staffed immigration or emigration desks of the site, in service; e-gates and airport counters are refused; a terminal duty manager naming desks gets 403) | A newer published version answers 409 (read the profile again). A target left empty takes its placeholder (20 bins per line, 30 tracer runs) until the pilot's KPI annex answers TC-04; the campaign shows `placeholder: true`. A count line standing alone (on no zone) is counted by no queue zone and cannot be in scope |
| Start | `POST .../campaigns/{id}/start` | Planned only, and only while its profile version is still the published one (409 otherwise) |
| Capture | `POST .../validation/capture/campaigns/{id}/counts` with `lineId`, `binStartUtc` (UTC on the quarter hour, ending in Z), `crossingsIn` and `crossingsOut` (0 to 10,000); optional `Idempotency-Key` header | The campaign runs, the line is in scope, the bin starts on a planned local day, has ended (a minute of clock tolerance) and ended before the profile version was retired. One count per line, bin and observer (a second is 409; two observers may count the same line and bin). Resending with the same key returns the stored count (200); the same key with another count is 409. A key belongs to the observer who sent it: another observer's identical key makes that observer's own count (201). The account that planned or started the campaign is refused (403) |
| Correct | `POST .../capture/campaigns/{id}/counts/{countId}/corrections` with the crossings and a `reason` (1 to 200 characters) | Only your own latest revision (409 for an older one, 404 for another observer's); it becomes the next revision, the corrected one stays. Audited (`ManualCount.Corrected`) |
| Read | `GET .../campaigns` (text, status, an allowlisted sort, pages of at most 500), `GET .../campaigns/{id}` (scope, targets, the version's state, bins captured per line), `GET .../campaigns/{id}/counts` (current revisions, or `currentOnly=false` for every revision; by line, observer and bin range); observers `GET .../capture/campaigns` (the running campaigns with their lines and the site's time zone) and `GET .../capture/campaigns/{id}/counts` (their own) | A site you cannot see, a campaign of another site and another observer's count answer 404 |
| Tracer runs | `POST .../capture/campaigns/{id}/tracer-runs` with `deviceClockUtc` (the tablet's own clock when it sends, read again at every retry) and 1 to 20 `runs`, each with `zoneId` (a queue zone in scope), `tracerCode` (`T-` and 2 or 3 digits, never a name), `joinedUtc` and `exitedUtc` (the tablet's times, UTC ending in Z) and `abandoned`; `Idempotency-Key` header required | Ariva measures the tablet's offset (its clock minus Ariva's at receipt) and stores the tablet's times, the offset and the corrected times; more than 5 minutes off is 400 (set the tablet's time automatically). Each run lasts at most 3 hours, joined on a planned local day, exited not in the future beyond a minute (400 otherwise, the value never repeated). Resending with the same key returns the stored batch (200); the same key with other runs is 409; a run already sent in another batch is 409, and so is a batch past 500 runs of yours in the campaign. Tablet times more than 32 days before or more than a minute after the tablet's own clock reading are refused (400) before any correction. Read with `GET .../campaigns/{id}/tracer-runs` (by zone, observer, tracer code, join range) and `GET .../capture/campaigns/{id}/tracer-runs` (your own) |
| Desk logs | `POST .../capture/campaigns/{id}/desk-observations` with `binStartUtc` (UTC on the quarter hour) and 1 to 20 `desks`, each a `deskId` in scope and exactly 15 `states` (`Closed`, `Idle`, `Serving`, `Paused`, or null for a minute not observed); `Idempotency-Key` header required | Only minutes that have ended (a minute of tolerance), on a planned local day; one state per desk, minute and observer (a minute already sent is 409; resending with the same key returns the stored batch). Correct with `POST .../capture/campaigns/{id}/desk-observations/{observationId}/corrections` (`state` and a `reason`; your own latest only; audited `DeskObservation.Corrected`). Read with `GET .../campaigns/{id}/desk-observations` (border roles only: a duty manager gets an empty page with `desksIncluded` false) and `GET .../capture/campaigns/{id}/desk-observations` (your own). An account with an airport role gets 403 on these three observer calls and `desksIncluded` false with no desk in `GET .../capture/campaigns`, even with the Validation observer role |
| Close | `POST .../campaigns/{id}/close` | Critical (a second factor within 15 minutes, otherwise 401 `mfa_required`); from Planned or Running; nothing is captured or corrected afterwards (409). Audited with plan and start (`ValidationCampaign.*`) |

A campaign, its scope, its counts, tracer runs and desk logs are evidence: nothing is deleted, a closed campaign never changes, and the database refuses an edited count, run or desk state, or one in a campaign that is not running, even from a hand-written statement. Desk logs are border desk data: they stay in the border deployment and only border roles read them. If a new zone profile version is published during a campaign, counts of bins that ended before it stay valid and later bins are refused; plan a new campaign over the new version (re-validate after any layout change, F18).

## 3. TOTP enrolment and reset

Enrolment:

1. Administrators enrol TOTP at first sign-in, before any other action. Other roles: whether MFA is mandatory is To confirm per site (D5 requires MFA for administrators).
2. Ariva shows a QR code and the key once (first sign-in in Ariva.Web, or Account security later); the user scans it into an authenticator app and confirms with a current code. Ariva then shows 10 recovery codes once, with a copy action (ARV-051).
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
| Status | Active or Disabled |

On creation Ariva shows the client secret and the TOTP provisioning URI once. Hand them over out of band by two separate channels.

API (ARV-042), under `api/v1/admin/integration-clients`, System administrator only: list (optionally `?siteCode=`), view, create, `PUT` to change name, scopes, sites, networks and the per-request policy, `secret` to rotate the secret, `totp` to reset the seed (each shown once), `disable`, `enable` and `unlock`. Every change needs a second factor in the last 15 minutes and is audited without secrets. You can bind a client only to sites you hold, and you see only clients whose sites are all yours. Every client needs at least one allowed source network (CIDR, at most 16, no `/0`, no host bits): token exchanges from anywhere else are refused and not counted. Any change stops the client's current tokens on their next call. A client locked after 10 failed token exchanges unlocks by itself after 15 minutes or when you unlock it; find the cause first (Operations runbook, integration client locked out). A lock stops new tokens only; tokens the client already holds run until they expire, so to stop a client at once, disable it. Enabling a disabled client does not clear a lock: unlock it separately. Clients are never deleted: disable them, so their recorded calls stay attributable.

SSIM schedules (ARV-046), under `api/v1/admin/sites/{siteCode}/flight-schedules` (permission `CreateFlightSchedule`, System administrator, your own sites): `preview` reads a file and shows what it would do without changing anything; `import` applies exactly what was previewed (send the same file and horizon with the preview's `previewToken`, yourself, within two hours). Use it when a site has no live AODB feed yet, or to give a new season's baseline; a schedule creates legs and never changes one a live feed has reported. Every import is audited with its outcome and counts, even one that fails part way.

Outbound endpoints (ARV-045), under `api/v1/admin/outbound-endpoints`, with the same integration permissions (System administrator): list (optionally `?siteCode=`), view, register (`POST` with `code`, `name`, `purpose` `Generic`, `AcrisFlights` or `AmanFeed`, `siteCodes`, `connection` and `secret`), `PUT` to change the name, sites and connection, `PUT {id}/secret` to replace the secret, `disable` and `enable`. Every change needs a second factor in the last 15 minutes and is audited without secrets; the secret is never shown again. You can bind an endpoint only to your own sites and you see only endpoints whose sites are all yours. An ACRIS or AMAN endpoint feeds exactly one site. An `AmanFeed` endpoint (ARV-050) pulls AMAN's feed from AMAN's Integration API where AMAN's Kafka is not shared: `TotpClientCredentials` only (AMAN's client id, secret and TOTP seed, with per-request TOTP: an AMAN pull always sends `X-TOTP-Code`), `pullPath` the feed path ending in a slash (for example `/feed/`, the contract names follow it), and a poll interval of 30 to 3,600 s; its status shows the records pulled, applied, unchanged (already received over another transport), refused and unreadable. The authentication kind of an endpoint never changes: register a new endpoint and disable the old one. Changing the base URL, networks, pinned CA or token path needs the secret again in the same request (the partner gives you the secret for the new place); a rename or a change of timeouts does not. Give the networks the partner's service is really in, never a broad range: Ariva refuses any address outside them, and never calls this host, link-local or cloud metadata addresses whatever they say. Endpoints are never deleted.

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
| Register | Code (the physical label, for example `S-17`), family, model, transport, dialect, clock source (NTP or PTP), level, position, mounting height (2 to 20 m), orientation, owning queue zone, and the footprint from the vendor's table if you have it. New devices start in `Commissioning`. Registering is a critical action (a second factor within 15 minutes); the answer carries the device credential once |
| Record calibration | Method (tally counters or two observers), sample size (50 to 5,000 passengers), counting accuracy, wait-time error, pass threshold (95 percent by default), notes. A pass sets the device `Online`; a failure keeps it, or puts it back, in `Commissioning`. Calibrations are never changed or deleted |
| Replace or move | Change the placement: anything that changes what the sensor sees or which zone owns it sends it back to `Commissioning` until a calibration passes; re-validate penalty-grade zones. So does a new model, transport or dialect (the calibration was measured with the old ones); a clock source change does not |
| Rotate the credential | A new credential, shown once; the previous one stops working at once (within a minute on hosts without the cache backplane). Critical action |
| Restrict access | Source networks in CIDR form (for example `10.20.0.0/24`; at most 16, no `/0`) and the SHA-256 fingerprint of a client certificate the device must present. Empty lists allow any address and no certificate. Critical action (`PUT {id}/access`) |
| Retire | Status `Retired`; the credential stops working, history is kept. Administrators only, and a critical action. A device registered by mistake and never calibrated can be removed instead. A level cannot be deleted while a device that is not retired hangs on it |

In Ariva.Web the Devices screen (ARV-054) does each task on the registry and the coverage plan; see the [User guide](12-User-Guide.md), Devices. The API is `api/v1/admin/devices` (ARV-021): search by text, site, level, state and owning zone; view; register; `PUT {id}` for details; `PUT {id}/placement`; `POST {id}/credential`; `GET` and `POST {id}/calibrations`; `POST {id}/retire`; `DELETE {id}`; and `GET assumed-footprint?family=&mountingHeightMetres=` for the form's preview.

The owning queue zone is named as in the site's zone profiles: it must be a queue zone in the published profile or in the draft, on the device's level, and the device's footprint must reach it or a zone that hangs off it (overflow, service, staff). A device can be registered while its zone is still in the draft, but it goes `Online` only once the zone is published, on the device's level and within its footprint, as checked when the passing calibration is recorded. The name, not a zone id, ties a device to its zone, because every profile version copies its zones with new ids and keeps their names; sensor events stay keyed by the same zone across versions.

Without a vendor footprint, the BOQ's planning assumption for the mounting height is used and labelled as an estimate everywhere it shows: 10 x 10 m from 4 to 6 m, 12 x 9 m from 10 to 14 m, interpolated between 6 and 10 m, scaled below 4 m and above 14 m; a 10 m radius for LiDAR until a perception platform is certified. Enter the vendor's footprint for the model and height as soon as you have it.

The credential (`ardk_` followed by 43 characters) is shown when the device is registered and when it is rotated, never again: copy it into the device or the vendor tool at once. Ariva keeps only its first 13 characters and a hash, so a lost credential is replaced, not recovered.

Health (ARV-025): `GET api/v1/admin/devices/health` (optionally `?siteCode=`) shows every device of your sites with its state, when it was last heard from and what it last reported (online, frame rate, temperature, clock offset), every queue zone as `Healthy`, `Degraded` or `Unmonitored`, and the heartbeat timeout; `GET {id}/health` shows one device. A commissioned device not heard from for the timeout (`Devices:Health:HeartbeatTimeoutSeconds`, 180 seconds by default) becomes `Offline` and its zone `Degraded`; a device that reports itself unwell becomes `Degraded`; both return to `Online` by themselves when the device reports again. Nothing in the health view can be changed by hand: fix the device, and its state follows. A device in `Commissioning` keeps that state whatever it reports.

See [Commissioning and calibration](07-Commissioning-and-Calibration.md).

## 7. Zone profiles

| Task | Notes |
|---|---|
| New draft | Copies the active profile; one draft per site at a time |
| Edit the draft | Zones (queue, service, staff, overflow), entry, exit, count and overflow-entry lines, desks, lanes, lane categories, desk code mappings, e-gate reject target lane, site parameters |
| Publish | Creates the next immutable version; step-up MFA; audited |
| Activate | From a time, or immediately by a supervisor (for example when stanchions move) |
| Correct | Always a new version; affected periods can be recomputed as new revisions |

Every result records the profile version it was computed with.

In Ariva.Web the Zones screen (ARV-053) does all of this on the floor plan: New draft, zones and lines drawn by dragging or typed in a corner table, Check the draft, Publish (step-up), Discard the draft (administrators), and for administrators Upload a floor plan for the chosen level with its scale and origin. See the [User guide](12-User-Guide.md), Zones.

Phase 0 API (ARV-017), `api/v1/admin/zone-profiles`:

| Call | What it does | Who |
|---|---|---|
| `GET ?siteCode=` | The site's versions and draft, newest first, with who created and published each and when | Supervisors, duty managers, handler station managers of the site, administrators |
| `GET {id}`, `GET {id}/validation` | A version with its zones and lines; what stops a draft from being published, and the hash of its geometry | Same |
| `POST drafts` `{siteCode, name}` | A draft copied from the published version (empty for the first); 409 while a draft exists | Supervisors, duty managers, administrators |
| `PUT {id}`, `POST {id}/zones`, `PUT {id}/zones/{zoneId}`, `DELETE {id}/zones/{zoneId}`, `POST {id}/lines`, `DELETE {id}/lines/{lineId}` | Edit the draft. Polygons are `"x y,x y,..."` in metres of the level, 3 to 200 points, simple and inside the level; entry and exit lines lie on an edge of their queue zone. Names are up to 200 characters, and a queue zone's name with the site code and a slash at most 200 (its events are keyed by them; ARV-114c): at most 196 characters at a three-letter site, refused with 400 otherwise | Same |
| `POST {id}/publish` `{geometryHash}` | Publishes the draft as the next version if its geometry still has the hash from the validation the publisher reviewed (409 when someone edited it since), retires the version it replaces and announces it to Stream and Cronz (`ZoneProfilePublished`). Refused with the list of problems when the draft is incomplete | Same, with a second factor in the last 15 minutes (otherwise 401 asking for step-up) |
| `DELETE {id}` | Discards a draft | Administrators |

Published and retired versions cannot be changed or deleted (409), not even by a database statement (triggers in script 0012). Publishing takes effect immediately; activation from a later time is a target feature.

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

Phase 0 API (ARV-037), `api/v1/admin/alert-rules`. A rule is the typed fields below and nothing else; there is no expression, formula or script anywhere, and enum fields take their names exactly (`Nowcast`; not `nowcast`, `0`, `Nowcast ` with a space or `Nowcast,BinP90`).

| Call | What it does | Who |
|---|---|---|
| `GET ?siteCode=&metric=&enabled=&text=` | The rules of your sites, by site and code; `text` matches the code or the name | Supervisors, duty managers, handler station managers of the site, administrators |
| `GET {id}` | One rule | Same |
| `POST` | Creates a rule in one of your sites; the server gives the next code (`R-006` after the seeded five); 400 with the problems when a value is out of range | Supervisors, duty managers, administrators |
| `PUT {id}` | Changes every field but the site and the code | Same |
| `DELETE {id}` | Deletes the rule; its code is never given again in that site | Same, with a second factor in the last 15 minutes (otherwise 401 asking for step-up) |
| `POST backtest` `{rule, fromUtc, toUtc}` | How often the rule (as it would be created, saved or not) would have fired on the stored minutes of an ended range of up to 24 hours in the last 90 days, times in UTC ending in Z: the count, the first raise and the alerts (at most 200) with when each cleared, and how many of its targets had data. It shows past queue values, so the caller must also see live queues; two run at a time per host (otherwise 429) | Supervisors, duty managers, administrators |

| Field | Accepted values |
|---|---|
| `siteCode`, `name`, `zones` | A site you hold; a name of up to 200 characters without control or invisible characters (stored and shown as plain text); 1 to 64 zones, each a queue or overflow zone of the site's published zone profile (desk service and staff zones cannot be watched) |
| `metric` | `Nowcast`, `BinP90` and `PredictedNowcast` (minutes, 0 to 600), `QueueLength` (people, 0 to 100,000), `DesksBelowPlan` (desks, 0 to 1,000), `OverflowOccupied` and `SensorOffline` (true or false) |
| `comparator`, `threshold` | `GreaterThan`, `GreaterOrEqual`, `LessThan` or `LessOrEqual` with a threshold for numbers; `IsTrue` without a threshold for the two conditions |
| `minQueueLength` | Nowcast only: raise only with at least this many queuing |
| `clearThreshold` | Optional; strictly on the clearing side of the threshold (below it for a rule that fires above) |
| `sustainMinutes`, `clearAfterMinutes` | 1 to 120 |
| `severity` | `Info`, `Warning`, `Critical` |
| `ownerRole`, `escalateToRole` | Optional role codes. Empty owner means the zone's owner. Unless you are an administrator you can set only roles you hold yourself, and you cannot change or clear a role you do not hold that is already on the rule (editing other fields with it left as it is works) |
| `escalateAfterMinutes`, `escalationContact` | 1 to 1,440 minutes, required when there is an escalation role or contact; the contact (up to 100 characters) names a function outside Ariva's roles, such as the border operations duty officer, never a person by name |
| `notifyByEmail`, `enabled` | Email is the MVP's second channel; disabled rules are kept but never evaluated |
| `leadMinutes` | For `PredictedNowcast` only, 15 to 60: how far ahead the rule looks |

At most 500 rules per site, and codes stop at R-999999. Every create, change and delete is in the audit log with the rule's values before and after as JSON (fixed fields first, then the zones' count and SHA-256, then the zone list). Reading, changing or deleting a rule of another site answers 404, like one that does not exist; creating one in a site you do not hold answers 400, like an unknown site. The database repeats the checks above and keeps a rule's site and code. The demo seed (ARV-019) adds R-001 to R-005 above to the demo airport once, each audited, and only when the published profile has the zones they watch; a site where they were deleted does not get them back. Alerts move from Raised to Acknowledged, Escalated and Resolved (User guide, Live operations, ARV-039); a rule with `notifyByEmail` emails the people of its owner role (every operational role when it has none) when an alert is raised and the people of its escalation role when it is escalated (ARV-040): only enabled accounts that hold the role and the alert's site and have an email address, never the break-glass account. A user's email must be one plain address (no display name, comma, spaces or line breaks), or the users API answers 400. Emails name the escalation role, never the rule's free-text escalation contact. One alert email per alert, kind and address; past the hourly limit per address the next ones are held back and recorded as such; an alert still Raised after the rule's escalation minutes is escalated by the evaluation to the escalation role or contact and audited. Duplication and suppression windows are not built yet.

Evaluation (ARV-038). Ariva.Api.Stream evaluates every enabled rule once a minute (`Alerts:Evaluation`) on what the stream stored, one target at a time: each queue zone in scope, or each commissioned device of those zones for `SensorOffline`. A minute without a value (no row yet, or the queue length degraded by a sensor outage) is skipped: it neither counts towards nor breaks a run. The condition must hold for the sustain minutes in a row to raise an alert; while it is open no second alert is raised for that rule and target; it resolves by itself when the clear condition (beyond the clear threshold, or the condition no longer holding) has held for the clear minutes. A new target (a new or re-enabled rule, a zone added, a device commissioned) starts at the present: earlier minutes are for the backtest, so no stale alerts are raised. Editing a rule restarts its counts (an open alert stays and clears under the new values, unless the metric changed, which resolves it as `RuleChanged`); a target that leaves the rule has its open alert resolved as `TargetWithdrawn`; disabling or deleting a rule resolves its open alerts as `RuleWithdrawn`. Each rule is evaluated in its own transaction, so one that fails is logged and retried without holding up the others. An alert keeps what the rule said when it was raised. The backtest is the same evaluation over the same stored minutes from the range's start, so it shows exactly the alerts the live evaluation raised over a range it evaluated from that start (proved on the reference evening: R-001 at 18:05). A value beyond a billion is recorded on the alert at that bound. What each metric reads:

| Metric | Value per minute |
|---|---|
| `Nowcast` | The minute's nowcast; skipped while the queue length is degraded. A nowcast flagged only because its throughput comes from exits (the desk state of ARV-049 is not yet joined to the nowcast) is judged |
| `QueueLength` | People in the queue zone and its overflow bands |
| `BinP90` | The largest P90 among the zone's 15-minute bins that ended in the last 150 minutes (their latest revision, so a backtest after bins became final can differ from what was judged live) |
| `SensorOffline` | 1 while the device is in an outage the stream recorded, or offline in the registry since it was last heard; otherwise 0 |
| `PredictedNowcast` | The highest nowcast projected within the lead time: the queue now, stepped a minute at a time with the arrival-wave projection and what the desks clear at the current throughput. The projection (ARV-047, formulas F14) gives a queue zone the arrivals of the lane categories its desks serve (through the service zones linked to desks in the published profile), shared among the queue zones serving the same lane; a zone serving no lane has nothing to judge, and a backtest finds none (projections are not stored) |
| `OverflowOccupied` | Since ARV-115: 1 for a minute in which an overflow band held anyone (its highest reading above zero), 0 when the bands that reported held no one; a minute without a band reading is skipped. A band whose sensor is silent beyond 2 minutes is Unknown (neither occupied nor empty) and counts in neither until it reports again, so a rule whose only band is Unknown neither raises nor clears. A queue zone watches all its bands; an overflow zone in scope watches that band only. The demo seed's R-002 raises after 3 occupied minutes and clears after 3 empty ones |
| `DesksBelowPlan` | Nothing yet: there is no staffing plan yet; a new rule on it is refused |

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
