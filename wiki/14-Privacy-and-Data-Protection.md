# Privacy and data protection

For authority technical committees, data protection officers, counsel and Dalil staff preparing a DPIA. Ariva is designed so that it never needs to know who anyone is: it measures anonymous movement and aggregates it. This page explains how, what is kept and for how long, and what remains to confirm per jurisdiction. The reference is `../docs/domain/data-boundary.md`.

This page is not legal advice. Items marked To confirm need counsel's answer for each market.

## 1. Anonymity by design

| Measure | Detail |
|---|---|
| No images | No image leaves a stereo sensor: images are processed on the sensor and only coordinates leave. LiDAR captures no images. Camera analytics integrations deliver events only; Ariva never receives video |
| No biometrics | Nothing biometric enters Ariva. No face, fingerprint, template or match score |
| No re-identification | Continuity only by overlapping coverage inside one process. No appearance re-identification (height, shape, gait) and no device re-identification (Wi-Fi or BLE). Between processes, flows are linked only statistically or not at all |
| Ephemeral track ids | A track id is short-lived, rotated at zone exit, never persists past the operating day, and has no link to identity |
| No probing | Wi-Fi or BLE probing and CCTV analytics are not used for queue KPIs (CCTV only as a coarse overflow fallback). Wi-Fi and BLE identifiers are pseudonymous and still personal data, which is why they are excluded |
| Aggregate-only AMAN feed | AMAN sends four aggregate contracts: desk sessions, desk interval statistics, e-gate interval statistics, inbound lane demand per flight. No person, document or officer identifiers, with timestamps coarsened to the interval to block re-identification by timestamp alignment |
| Small-cell suppression | E-gate reject categories with fewer than 3 rejects in an interval are folded into `Other`, so no individual outcome can be inferred |
| Officer data stays in AMAN | Officer identity, rosters and officer-to-desk assignments never enter Ariva; the Border dashboard links to AMAN's own reports |
| Desk-level data stays on the border side | A desk code plus a minute could be joined with AMAN's records to identify an officer, so desk-level data never leaves the border deployment. Only lane-level aggregates cross to an airport deployment |
| In country | Each deployment runs in-country; national views receive aggregates only |
| Build-time guard | A test fails the build if an AMAN contract property name looks like a person or officer identifier |

## 2. What never crosses into Ariva

| Category | Examples | Where it stays |
|---|---|---|
| Officer identity | Names, staff numbers, usernames, badge ids, officer-to-desk assignments, shift rosters | AMAN |
| Person identity | Traveller names, passenger ids, PNR locators, API and PNR records, per-person nationality | AMAN |
| Document data | Document numbers, MRZ, dates of birth, issuing state per person, visa data | AMAN |
| Biometrics | Face and fingerprint images, templates, match scores | AMAN, ABIS |
| Images | Any camera frame, including stereo sensor images | Sensor |
| Exact AMAN event timestamps | Per-transaction times | AMAN (Ariva receives interval-coarsened times) |

From a border deployment to an airport deployment, additionally never: track ids, track samples, zone events, desk-level states or statistics, AMAN feed messages. Only lane-level wait times and KPIs cross (Proposed field list: lane, bin start, realised wait P50 and P90, nowcast, queue length, bin status, data quality; To confirm).

## 3. Retention

| Data | Retention | Source |
|---|---|---|
| Track ids | Rotated at zone exit; never persist past the operating day. The raw archive keeps only a pseudonym per UTC day (HMAC-SHA256 with a random daily key destroyed after three days), so archived tracks cannot be linked across days (ARV-026) | D4 |
| Raw sensing events (track samples, vendor crossings, occupancy and interval counts, table `sensing_event`) | Contract dispute window, default 90 days, dropped by TimescaleDB's retention policy; compressed after one day (ARV-026; values To confirm per site) | D5 |
| Device health | Dispute window (evidence for sensor-outage exclusions) | Proposed |
| `aman.feed` Kafka topics | Medium class; AMAN owns the configuration | D5; values To confirm |
| Ariva Kafka topics | Short, medium, long or compacted classes (3, 14, 30 days) | D5; values Proposed |
| Desk staff and service zone readings (`desk_zone_reading`: desk key, zone role, time, count, flag; no identities). Desk-level border data: stays in the border deployment and no API reads it; only the desk feed turns it into desk states (ARV-116) | 7 days, dropped by TimescaleDB retention | Proposed, To confirm |
| Shadow nowcast (`queue_minute_shadow`: zone, minute, minutes or no-service reason, F11 flag, and since ARV-117b, script 0045, the lane's sensor cycle time the shadow took; ARV-117, in its own table since ARV-117a, script 0043): the queue's nowcast without AMAN inputs, a lane aggregate with no desk code and no identities. Never shown to staff or passengers, never an alert input or in a report; the runtime login can write it but not read its values (database privileges); read only for the pilot's validation comparison (compared by the engine of ARV-104f, read by the validation results service through the validation reader login, the only login that holds the `ariva_validation_reader` role (ARV-104g1, script 0049); its figures appear only in the validation results) | As queue_minute: no retention policy (evidence) | Proposed |
| Sensor-only desk minutes (`desk_sensor_minute`: desk key, minute, seconds per state and flag from the desk's staff and service zones alone; ARV-117a, script 0044; no transactions, no identities). Desk-level border data: stays in the border deployment and no API reads it; only the stream's desk term (the shadow nowcast's sensor-only n_open) reads it. The validation comparison (ARV-104f) takes no sensor-only desk minutes; whether its read path (ARV-104g) reads them is decided there | As desk_minute: no retention policy | Proposed, To confirm |
| Validation campaigns, manual counts and tracer runs (`validation_campaign` and its scope, `manual_count`, `tracer_batch`, `tracer_run`; ARV-104a and ARV-104b): the campaign's site, profile version, zones, lines and days; per line and 15-minute bin the crossings an observer counted; per tracer run the zone, a campaign label (`T-07`, never a name), the join and exit times on the capturing device's clock and corrected to Ariva's, and whether the tracer left without being served. Observers appear only as Ariva user ids; who carries which tracer label stays on the campaign's paper roster outside Ariva. Line and zone data, no desk data; read by the site's supervisors, duty managers and administrators, each observer only its own | Indefinite (pilot acceptance evidence; nothing is deleted or changed) | Proposed |
| Desk observations (`validation_campaign_desk`, `desk_observation_batch`, `desk_observation`; ARV-104b): the staffed border desks in a campaign's scope and, per desk and minute, the state an observer saw (Closed, Idle, Serving, Paused), corrections kept as revisions with a reason; no officer identity, the observer an Ariva user id. Desk-level border data: stays in the border deployment, never crosses to an airport deployment, and no airport-side account reaches it. Managers' reads (the desks in scope, every observer's states) need `BorderDesks.View` (border shift supervisors and administrators); a terminal duty manager sees no desk and no state. Logging, correcting and reading back desk states is for a Validation observer whose only role is that one, or for an account with `BorderDesks.View`: an account that holds an airport role (terminal duty manager, handler station manager) and also the Validation observer role sees no desk in the capture list and is refused (403) on desk batches, corrections and its own desk reads; it can still count lines and time tracers. Each observer reads back only what it recorded. The desk-state agreement computed from these states and the stored desk minutes (ARV-104f: per desk and minute, per desk and over the campaign's desks) is border per-desk data too: it is served only to border roles of the campaign's site (`BorderDeskAccess.Sees`, `BorderDesks.View`), never to airport roles, the border-to-airport feed or AMAN. Correction reasons are free text beside a desk and a minute: the observer screen asks observers never to name people in them (Ariva cannot detect a name in text) | Indefinite (pilot acceptance evidence); a shorter period after the campaign's report is accepted is To confirm with the border authority | Proposed, To confirm |
| Queue intervals, desk intervals, forecasts, border lane KPIs | Indefinite (aggregates) | D5 |
| Hourly and daily report aggregates | Indefinite | D5 |
| Report schedules and deliveries (schedule, local day, recipient account, status; ARV-060). Report emails carry lane aggregates, alerts and device uptime only, to Ariva accounts allowed the site | To confirm; Proposed: 400 days for delivery rows, schedules until deleted (audited) | Proposed |
| Configuration, contracts, SLA decisions, alerts | Indefinite, audited | D5 |
| Alert emails (`email_message`: recipient address, subject, body, status) | Kept with the alert as the record of who was told what; the body holds no traveller, officer or document data and no notes, only the staff address. A retention period for staff addresses here is To confirm | ARV-040 |
| Evidence packs | At least the contract's dispute and audit periods | To confirm |
| Flight data (`flight_leg`, `flight_event`, `counter_allocation`: flight identity, times, places, aircraft, seat and passenger counts; no passenger or crew data) | | To confirm |
| Integration call record (`integration_call`: client, route, site, status, payload SHA-256, source address) | Kept as the audit of every Integration API call; a retention period is To confirm | ARV-042 |
| Integration idempotency keys (`integration_idempotency`: key, body SHA-256 and the answer sent, which holds flight keys and item errors) | 24 hours, then swept | ARV-043 |
| Logs and traces | No track ids or desk codes in log messages beyond the operating day | Proposed, To confirm |
| Backups | No longer than the dispute window plus the backup cycle, so backups do not extend sample retention | Proposed |
| Phase 0 lab recordings (office entrance or corridor) | Including notice to the people recorded | To confirm |

Only the retention job can delete raw hypertable data; the application role cannot update or delete it.

## 4. Laws in scope

The design documents D4 to D6 do not analyse the laws themselves; the legal analysis sits in an earlier document (D1) that was not a source for the data boundary. The table records only what is known and what must be confirmed.

| Jurisdiction | What the sources say | To confirm |
|---|---|---|
| UAE (PDPL) | Not covered in D4 to D6 | Applicability to the border authority and to airport operators; DPIA requirements; lawful basis for anonymous tracking; any sector or emirate rules. Confirm with counsel and D1 |
| Angola | Confirm whether stereo counters fall under the video-surveillance law before choosing the sensor; LiDAR may avoid the authorisation route. Ask local counsel; prepare the DPIA template | The data protection law and authority; whether anonymous coordinates are personal data; the authorisation route for stereo sensors |
| Tanzania | In-country deployment also avoids Tanzania's cross-border permit (D5, citing D1) | The act and any registration or permit duties for in-country processing |
| GDPR (where EU-linked) | Not covered in D4 to D6 | When it applies (for example an EU-based client, processor or support access); transfer rules |
| Lebanon (reference airport in the business plan) | Law 81/2018, Ministry of Economy and Trade oversight (business plan source) | Applicability if a Lebanese deployment is pursued |
| All markets | A DPIA template per market; appearance re-identification drifts toward biometric data | Whether ephemeral, anonymous track coordinates count as personal data under each law; notice requirements for passengers |

## 5. DPIA outline

A DPIA template is prepared per market (D4, D6). Proposed outline:

1. **Description of processing**: purpose (queue measurement, staffing, service levels), processes covered, sensor families, data flows (sensor to gateway to Ariva; AMAN aggregates; AODB flight data; border-to-airport aggregates), deployment and hosting (on premises, in country).
2. **Data inventory**: track samples (anonymous coordinates and ephemeral ids), counts, desk states, flight data, user accounts and audit logs of Ariva's own users; explicit list of what is never processed (section 2).
3. **Necessity and proportionality**: why counts and tracks are needed (realised wait per person requires a track from entry to exit line); why images, biometrics and re-identification are not; retention justified by the dispute window.
4. **Lawful basis and notice**: per jurisdiction (To confirm); signage text and placement.
5. **Data subject considerations**: passengers (anonymous tracks), staff crossing measured areas (excluded by zone rules; residual bias measured in validation), officers (no officer data), Ariva users (accounts, audit).
6. **Risks and mitigations**: re-identification by joining datasets (coarsened timestamps, desk data kept on the border side, small-cell suppression); function creep toward surveillance (no images, aggregate-only contracts, build-time identifier test); unauthorised access (security controls in [Security guide](13-Security-Guide.md)); retention beyond need (retention job, backup retention rule).
7. **Processors and transfers**: Dalil support access (health telemetry only, where allowed), the local integration partner, sensor vendors (cloud connectivity disabled at government sites); no transfer out of country.
8. **Authorisations**: camera-related authorisations (for example Angola), registrations (for example Tanzania), to confirm per market.
9. **Sign-off and review**: owner, review after any change of sensor family, purpose or data flow.

## 6. Signage

Passengers are told that overhead sensors count people anonymously to measure queue times, that no images are stored or transmitted, that no one is identified, who operates the system and whom to contact. Signs are in the site's languages and placed at every queue entrance in the measured area. Exact wording and legal notice requirements are To confirm per jurisdiction. See [Sensor installation guide](06-Sensor-Installation-Guide.md) for placement.

## 7. Requests from authorities and individuals

Because Ariva holds no identity, it cannot answer a request about a specific person's movements, and it is designed so that it never could. Requests about officer activity are for AMAN, where officer data is held. Requests about Ariva's own users (accounts, audit entries) are handled by the deployment's administrator under the customer's procedures.

## 8. Material derived from third parties' confidential drawings

Customers, prospects and their designers sometimes share design drawings that carry a copyright and confidentiality notice. Dalil may use the counts and measures read from such drawings to build a development seed site, on these terms:

- The drawings themselves, and anything rendered from them (crops, screenshots, traces), are kept outside the repository, on the product owner's machine only, and are never committed, uploaded or drawn into the product. The seed draws its own schematic from the numbers.
- What is derived from them is development-only: its code is compiled into Debug builds only, so Release builds and container images never carry it, and it is switched on only on the product owner's machine (a host with it on refuses to start outside a developer machine or in a Release build).
- No names of organisations, systems, people or drawing sheets appear in the seeded data; a unit test allows only Ariva's own vocabulary, the site's own codes and the airport's public name.
- Such a site is shown only to the parties the drawings concern, unless they agree otherwise in writing.
