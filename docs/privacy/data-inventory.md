# Data inventory

Every table, hypertable, continuous aggregate and view in which Ariva stores data, for the pilot's data protection impact assessment (DPIA, [wiki 14](../../wiki/14-Privacy-and-Data-Protection.md) section 5, item 2) and for operators (ARV-111a). Each row gives the object's kind, its category, whether it holds personal data, how long it is kept, where that retention is set, and its owner.

A unit test keeps this page complete: `DataInventoryTests` (Ariva.UnitTests) reads the shipped scripts (`Ariva.Infra/Timescale/Scripts`: every `CREATE TABLE`, `create_hypertable`, continuous aggregate and view), the NHibernate mappings and the script runner's own table, and fails when an object has no row here, when a row names an object that no longer exists, when a row's kind differs from the scripts, or when a configurable retention does not name its parameter. Add the row in the change that adds the table.

## How to read it

- **Personal data.** Ariva never receives a traveller's, an officer's or a document's identity ([data boundary](../domain/data-boundary.md)). The personal data it holds is about its own users, who are staff: account data (user name, display name, email address, password hash, sign-in sessions with their client address), and references to accounts (an account id or user name beside an action). "Aggregates only" means counts, waits and states per zone, line, lane or desk with no identity. "Audit columns" means the row carries `created_by`, `modified_by` (and `deleted_by`) with the user name and id of the account that changed it. Free text that a person types (a note, a reason, an escalation contact) may hold a name although the screens ask users not to type one; those rows say so.
- **Desk-level border data.** A desk code with a minute could be joined with AMAN's own records to identify an officer, so such rows stay in the border deployment and only border roles read them (data boundary, principle 5). They hold no identity.
- **Owner.** The context that writes the table and answers for its content, with the host that runs it. The controller of everything in a deployment is the authority that operates it (the border authority, or the airport operator).
- **Retention.** "Indefinite" means nothing deletes the rows today; the source of that decision is in the data boundary's retention table. "Overwritten" means one row per key holds the latest state and nothing accumulates.

## Retention parameters per environment

These periods are migration-job parameters (`Database:Retention`; in the clusters the Helm values `migration.retention` of each environment, wiki 04 sections 5 and 6). The migration job (`Ariva.Api.Main --migrate`, a Helm hook before every release) applies them with the migration login after the versioned scripts: it sets each hypertable's TimescaleDB retention policy to its period, replacing the value the creating script gave only when it differs, and writes each relational period into `retention_period`. TimescaleDB's own background job then drops whole chunks older than the period once a day; the Cronz retention job (`RelationalRetention`, daily at 03:15 UTC, wiki 10 section 4.8m) deletes the relational rows in batches. The hosts' runtime login can change neither (CWE-269).

| Parameter | Objects | Default | Allowed | Decision |
|---|---|---|---|---|
| `RawSensingDays` | `sensing_event`, `sensing_batch`, `device_health_event` | 90 days | 7 to 3,650 | The contract's dispute window, TC-62 (90 days until answered) |
| `DeskZoneReadingDays` | `desk_zone_reading` | 7 days | 2 to 90 | Proposed, ARV-116 |
| `LaneStaffingDays` | `lane_staffing` | 90 days | 90 to 3,650 | Decision of 2026-10-15 (the alert backtest's history) |
| `AlertEmailDays` | `email_message` (never a Pending email) | 180 days | 30 to 3,650 | Proposed, TC-67 |
| `IntegrationCallDays` | `integration_call` | 400 days | 30 to 3,650 | Proposed, TC-68 |
| `ReportDeliveryDays` | `report_delivery` | 400 days | 30 to 3,650 | Proposed, TC-66 |

## Objects

### Accounts, security and audit

| Object | Kind | Category | Personal data | Retention | Where set | Owner |
|---|---|---|---|---|---|---|
| `user` | Table | Accounts | Yes, account data: user name, display name, email address, PBKDF2 password hash and salt, the TOTP seed protected by the key ring, lockout counters, last sign-in; audit columns | Indefinite: accounts are disabled, never deleted (audit entries keep pointing at them) | Script 0003 (the runtime role cannot delete) | Accounts (Ariva.Api.Main) |
| `user_role` | Table | Accounts | Yes, references: the roles an account holds, who granted each and when | As the account | Scripts 0003 and 0006 | Accounts (Ariva.Api.Main) |
| `user_site` | Table | Accounts | Yes, references: the sites an account may reach, who granted each and when | As the account | Script 0007 | Accounts (Ariva.Api.Main) |
| `user_session` | Table | Accounts | Yes, account data: each sign-in with its client address, user agent, authentication methods, times and revocation | Indefinite today (the sign-in history; revoked, never deleted by the hosts). A period is To confirm (TC-65, open point of ARV-111a) | Script 0004 | Accounts (Ariva.Api.Main) |
| `refresh_token` | Table | Accounts | Yes, references: SHA-256 hashes of a session's refresh tokens (never a token) | As `user_session` | Script 0004 | Accounts (Ariva.Api.Main) |
| `recovery_code` | Table | Accounts | Yes, references: SHA-256 hashes of an account's recovery codes and when each was used | As the account (used codes are marked, never deleted) | Script 0005 | Accounts (Ariva.Api.Main) |
| `data_protection_key` | Table | Security | None: the Data Protection key ring, encrypted with the `ariva-dataprotection` certificate | Indefinite (retired keys stay so that older protected values stay readable) | ASP.NET Core Data Protection; script 0002 | Platform (every host) |
| `audit_entry` | Table | Audit | Yes: the acting account's id and user name, its client address, the trace id, and the target's name with short summaries of the change (never a secret) | Indefinite, append-only | D5 (configuration and audit); script 0006 (the runtime role cannot update or delete) | Platform (every host that changes configuration or accounts) |

### Sites, topology and configuration

| Object | Kind | Category | Personal data | Retention | Where set | Owner |
|---|---|---|---|---|---|---|
| `site` | Table | Configuration | None (codes and names); audit columns | Indefinite, audited | D5; script 0007 (sites are never deleted) | Site Topology (Ariva.Api.Main) |
| `airport` | Table | Configuration | None; audit columns | Indefinite, soft deleted | D5; script 0008 | Site Topology (Ariva.Api.Main) |
| `terminal` | Table | Configuration | None; audit columns | Indefinite, soft deleted | D5; script 0008 | Site Topology (Ariva.Api.Main) |
| `level` | Table | Configuration | None; audit columns | Indefinite, soft deleted | D5; script 0008 | Site Topology (Ariva.Api.Main) |
| `checkpoint` | Table | Configuration | None; audit columns | Indefinite, soft deleted | D5; script 0008 | Site Topology (Ariva.Api.Main) |
| `desk` | Table | Configuration | None: physical counters and gates, their lanes and service state; audit columns | Indefinite, soft deleted | D5; script 0008 | Site Topology (Ariva.Api.Main) |
| `desk_code_mapping` | Table | Configuration | None: another system's code for a physical counter (no roster, no officer); audit columns | Indefinite, soft deleted | D5; script 0009 | Site Topology (Ariva.Api.Main) |
| `floor_plan` | Table | Configuration | None: a plan's storage key, type, size, original file name and scale (the image itself is in file storage); audit columns | Indefinite, soft deleted when replaced | D5; script 0010 | Site Topology (Ariva.Api.Main) |
| `zone_profile` | Table | Configuration | None; the publishing user's name (`published_by`) and audit columns | Indefinite (published versions are immutable) | D5; script 0012 | Site Topology (Ariva.Api.Main) |
| `zone` | Table | Configuration | None: zone outlines of a profile version | As its profile version | D5; script 0012 | Site Topology (Ariva.Api.Main) |
| `line` | Table | Configuration | None: counting lines of a profile version | As its profile version | D5; script 0012 | Site Topology (Ariva.Api.Main) |
| `display` | Table | Configuration | None: a board's settings and labels, among them the wait at which advertising gives way to instructions (ARV-143b, `advertising_pause_minutes`: a number of minutes, 1 to 240 or none), its player credential as a prefix and a SHA-256 (never the credential); audit columns | Indefinite, soft deleted | D5; scripts 0035 and 0061 | Displays (Ariva.Api.Main) |
| `instruction_slide` | Table | Configuration | None: a slide's words per language (plain text a person types, which the screen asks not to hold a name), its icon, time on screen, scope and order; audit columns | Indefinite: retired, never deleted | Script 0056 (the runtime role cannot delete) | Displays (Ariva.Api.Main) |
| `advertising_policy` | Table | Configuration | Possibly: the reason of the last change is free text (who authorised it and their reference; the screen asks for the organisation and a document reference, not a person's name); besides it, per site whether its displays may show advertising, the queue zones excluded and the zone profile they were judged against; audit columns | Indefinite: changed in place, never deleted; every change is in the audit log with the administrator | Script 0057 (the runtime role cannot delete) | Displays (Ariva.Api.Main) |
| `advertising_media` | Table | Configuration | Possibly: the advertiser and campaign names and the original file name are free text (the screen asks for an organisation and a campaign, not a person); besides them, per item its storage key, type, size, hash, pixel size, a video's length (ARV-142b, script 0059; none for an image), the site's days it may play, its state, and the account ids of who uploaded it, last changed it and approved it, with the approver's user name; audit columns | Indefinite: retired, never deleted; every upload, change, approval and retirement is in the audit log | Script 0058 (the runtime role cannot delete) | Displays (Ariva.Api.Main) |
| `advertising_playlist` | Table | Configuration | None: a playlist's name (plain text a person types, which the screen asks to name the bank, not a person), its items as advertisement and slide ids with their seconds and weights, and its state; audit columns | Indefinite: retired, never deleted; every change is in the audit log | Script 0060 (the runtime role cannot delete) | Displays (Ariva.Api.Main) |
| `advertising_schedule` | Table | Configuration | None: which playlist plays on which displays of the site (the whole site, queue zone names or display codes), on which days and in which local window, and its state; audit columns | Indefinite: retired, never deleted; every change is in the audit log | Script 0060 (the runtime role cannot delete) | Displays (Ariva.Api.Main) |
| `operating_week` | Table | Configuration | None: a site's weekly operating hours; audit columns | Indefinite (availability evidence) | Script 0042 | Availability (Ariva.Api.Main) |
| `operating_day_exception` | Table | Configuration | Possibly: a reason in free text; audit columns | Indefinite (availability evidence) | Script 0042 | Availability (Ariva.Api.Main) |
| `maintenance_window` | Table | Configuration | Possibly: a reason in free text; audit columns | Indefinite (availability evidence) | Script 0042 | Availability (Ariva.Api.Main) |
| `alert_rule` | Table | Configuration | Possibly: the escalation contact is free text and may name a person; audit columns | Indefinite, soft deleted, audited | D5; script 0020 | Alerting (Ariva.Api.Main) |
| `report_schedule` | Table | Configuration | Yes, references: the owner's account id; audit columns | Until deleted (soft delete, audited) | Proposed (data boundary, report schedules); script 0036 | Reports (Ariva.Api.Main) |
| `report_schedule_recipient` | Table | Configuration | Yes, references: the recipients' account ids | As the schedule | Script 0036 | Reports (Ariva.Api.Main) |
| `integration_client` | Table | Configuration | None: a calling system (name, scopes, sites, networks), its secret as a PBKDF2 hash and its TOTP seed protected by the key ring; audit columns | Indefinite: clients are disabled, never deleted (their calls stay attributable) | Script 0025 | Integration (Ariva.Api.Main; used by Ariva.Api.Integration) |
| `outbound_endpoint` | Table | Configuration | None: a system Ariva calls, its settings and its secret protected by the key ring; audit columns | Indefinite: disabled, never deleted | Script 0028 | Integration (Ariva.Api.Main; used by Ariva.Api.Integration) |
| `retention_period` | Table | Configuration | None: each relational data set's period with the time and the login that set it | One row per data set, replaced by every migration | Migration job (`Database:Retention`); script 0054 | Platform (migration job) |
| `schema_version` | Table | Platform | None: the applied scripts with their checksum, time and login | Indefinite | The script runner (`SqlScriptRunner`) | Platform (migration job) |

### Devices, sensing and queue measurement

| Object | Kind | Category | Personal data | Retention | Where set | Owner |
|---|---|---|---|---|---|---|
| `device` | Table | Configuration | None: a sensor's registry entry, position and footprint, its credential as a prefix and a SHA-256; audit columns | Indefinite, soft deleted | D5; script 0013 | Device Management (Ariva.Api.Main) |
| `device_calibration` | Table | Evidence | Possibly: notes in free text; who recorded it (audit columns) | Indefinite: evidence, never changed or deleted | D5; script 0013 | Device Management (Ariva.Api.Main) |
| `device_heartbeat` | Table | Operational state | None: a device's latest health report | Overwritten (one row per device) | Script 0016 | Device Management (Ariva.Api.Main) |
| `zone_health` | Table | Operational state | None: a queue zone's latest health from its devices | Overwritten (one row per zone) | Script 0016 | Device Management (Ariva.Api.Main) |
| `sensing_event` | Hypertable | Raw sensing | Pseudonymous: anonymous track coordinates and heights with a track id that is a pseudonym of the UTC day (HMAC with a daily key destroyed after 3 days), line crossings and counts; no image and no identity. Whether such tracks are personal data is To confirm per jurisdiction (TC-75) | `Database:Retention:RawSensingDays`, default 90 days (TC-62); compressed after one day | `Database:Retention` (migration job, TimescaleDB retention policy); script 0017 created it with 90 days | Sensing (Ariva.Api.Stream) |
| `sensing_batch` | Hypertable | Raw sensing | None: each archived batch once (zone, device, kind, event count) | `Database:Retention:RawSensingDays`, default 90 days (TC-62) | `Database:Retention` (migration job, TimescaleDB retention policy); script 0017 created it with 90 days | Sensing (Ariva.Api.Stream) |
| `sensing_day_key` | Table | Key material | None directly: the key of each UTC day's track pseudonyms; destroying it makes that day's pseudonyms unlinkable | 3 days, then deleted | Code: `SensingArchive.Window` | Sensing (Ariva.Api.Stream) |
| `device_health_event` | Hypertable | Raw sensing | None: every device health report as received (device, zone, online, clock state) | `Database:Retention:RawSensingDays`, default 90 days (TC-62): kept as long as the raw events, so a replay reproduces the outages | `Database:Retention` (migration job, TimescaleDB retention policy); script 0019 created it with 90 days | Sensing (Ariva.Api.Stream) |
| `zone_outage` | Table | Evidence | None: device outages per queue zone | Indefinite (dispute evidence) | Script 0019 (no deletion) | Queue State (Ariva.Api.Stream) |
| `replay_run` | Table | Evidence | Yes, references: the operator's name and the database login of each replay command; hash chained | Indefinite, append-only | Script 0019 | Replay (the `--replay` command of Ariva.Api.Stream) |
| `queue_minute` | Hypertable | Queue measurement | None, aggregates only: per queue zone and minute, entries, exits, waits, queue length, nowcast | Indefinite (no retention policy; the pilot's evidence) | D5; script 0018 (no policy) | Queue State (Ariva.Api.Stream) |
| `queue_minute_15m` | Continuous aggregate | Queue measurement | None, aggregates only | As `queue_minute` (a refresh policy only) | Script 0018 | Queue State (Ariva.Api.Stream) |
| `queue_bin` | Hypertable | Queue measurement | None, aggregates only: 15-minute bins with wait statistics and quality | Indefinite (no retention policy) | D5; script 0018 | Queue State (Ariva.Api.Stream) |
| `line_minute` | Hypertable | Queue measurement | None, aggregates only: crossings per line and minute | Indefinite (no retention policy) | Script 0037 | Queue State (Ariva.Api.Stream) |
| `line_minute_15m` | Continuous aggregate | Queue measurement | None, aggregates only | As `line_minute` (a refresh policy only) | Script 0037 | Queue State (Ariva.Api.Stream) |
| `overflow_minute` | Hypertable | Queue measurement | None, aggregates only: occupancy per overflow band and minute | Indefinite (no retention policy) | Script 0039 | Queue State (Ariva.Api.Stream) |
| `overflow_bin_15m` | View | Queue measurement | None: computed from `overflow_minute`, holds nothing of its own | Not stored | Script 0039 | Queue State (Ariva.Api.Stream) |
| `zone_health_bin` | Hypertable | Queue measurement | None, aggregates only: conservation residual, track completion and occupancy checks per bin | Indefinite (no retention policy) | Script 0038 | Queue State (Ariva.Api.Stream) |
| `queue_minute_shadow` | Hypertable | Validation evidence | None, aggregates only: the nowcast without AMAN inputs, readable only by the validation reader login | Indefinite (no retention policy; evidence) | Script 0043 | Queue State (Ariva.Api.Stream) |
| `stream_zone_state` | Table | Operational state | None lasting: the queue engine's checkpoint per zone (open tracks of the moment with their ephemeral ids, counters) | Overwritten at every checkpoint | Script 0018 | Queue State (Ariva.Api.Stream) |
| `stream_partition_count` | Table | Operational state | None: partition counts per topic set | Overwritten | Script 0018 | Queue State (Ariva.Api.Stream) |
| `stream_offset` | Table | Operational state | None: the stream consumers' offsets | Overwritten | Script 0018 | Queue State (Ariva.Api.Stream) |

### Border data (AMAN feed and desks)

| Object | Kind | Category | Personal data | Retention | Where set | Owner |
|---|---|---|---|---|---|---|
| `border_desk_session` | Table | Border feed | None: AMAN desk session changes (desk code, state, lane, interval-coarsened time; no officer). Desk-level border data | Indefinite (the desk feed's input); a period is To confirm with the border authority | Script 0030 (the runtime role cannot delete) | Border Integration (Ariva.Api.Integration and the AMAN feed consumers) |
| `border_desk_interval` | Table | Border feed | None: transactions, documents and service times per desk and minute (no officer, traveller or document). Desk-level border data | Indefinite; To confirm with the border authority | Script 0030 | Border Integration (Ariva.Api.Integration and the AMAN feed consumers) |
| `border_egate_interval` | Table | Border feed | None: attempts, accepts and rejects by coarse category per gate and minute (small categories folded by AMAN). Desk-level border data | Indefinite; To confirm with the border authority | Script 0030 | Border Integration (Ariva.Api.Integration and the AMAN feed consumers) |
| `inbound_lane_demand` | Table | Border feed | None, aggregates only: passengers per lane of an arriving flight | Indefinite; To confirm with the border authority | Script 0030 | Border Integration (Ariva.Api.Integration and the AMAN feed consumers) |
| `aman_pull_cursor` | Table | Operational state | None: the AMAN pull's position per endpoint and contract | Overwritten | Script 0032 | Border Integration (Ariva.Api.Integration) |
| `desk_feed_state` | Table | Operational state | None: per site the desk engine's snapshot and read position (lane service times and people waiting) | Overwritten | Script 0031 | Border (Ariva.Api.Stream) |
| `desk_minute` | Hypertable | Desk measurement | None: seconds per desk state and transactions per desk and minute. Desk-level border data | Indefinite (no retention policy) | D5; script 0018 | Border (Ariva.Api.Stream) |
| `egate_minute` | Hypertable | Desk measurement | None: e-gate service, processed and rejected per gate and minute. Desk-level border data | Indefinite (no retention policy) | D5; script 0018 | Border (Ariva.Api.Stream) |
| `desk_zone_reading` | Hypertable | Desk measurement | None: staff and service zone counts per desk. Desk-level border data, no API reads it | `Database:Retention:DeskZoneReadingDays`, default 7 days (Proposed) | `Database:Retention` (migration job, TimescaleDB retention policy); script 0040 created it with 7 days | Border (Ariva.Api.Stream) |
| `desk_sensor_minute` | Hypertable | Desk measurement | None: seconds per desk state from the desk's zones alone. Desk-level border data | Indefinite (as `desk_minute`; To confirm) | Script 0044 | Border (Ariva.Api.Stream) |
| `lane_staffing` | Hypertable | Staffing | None, aggregates only: per arrivals lane and minute the 13 intervals of the next 3 hours (zone keys, lane codes, counts, recommended desks, waits). Border data (the lane split of the arrival wave) | `Database:Retention:LaneStaffingDays`, default 90 days (the alert backtest's history) | `Database:Retention` (migration job, TimescaleDB retention policy); script 0053 created it with 90 days | Staffing (Ariva.Api.Cronz) |

### Flights

| Object | Kind | Category | Personal data | Retention | Where set | Owner |
|---|---|---|---|---|---|---|
| `flight_leg` | Table | Flights | None: flight identity, times, places, aircraft, seats and passenger counts; no passenger or crew data | Indefinite today; To confirm (TC-64) | Script 0024 | Flight Demand (Ariva.Api.Integration) |
| `flight_event` | Table | Flights | None: flight milestones as feeds reported them | Indefinite today; To confirm (TC-64) | Script 0024 | Flight Demand (Ariva.Api.Integration) |
| `counter_allocation` | Table | Flights | None: check-in counters of departing legs and their handler code | Indefinite today; To confirm (TC-64) | Script 0024 | Flight Demand (Ariva.Api.Integration) |
| `feed_freshness` | Table | Operational state | None: each flight feed's latest message time and state | Overwritten | Script 0024 | Flight Demand (Ariva.Api.Integration) |

### Alerts, notifications and reports

| Object | Kind | Category | Personal data | Retention | Where set | Owner |
|---|---|---|---|---|---|---|
| `alert` | Table | Alerts | Possibly: the escalation contact copied from its rule (free text) and the user names of who acknowledged, escalated and resolved it, with their notes (free text) | Indefinite, audited | D5; script 0021 (the runtime role cannot delete) | Alerting (Ariva.Api.Stream and Ariva.Api.Main) |
| `alert_rule_state` | Table | Operational state | None: a rule's evaluation state per target | Overwritten | Script 0021 | Alerting (Ariva.Api.Stream) |
| `email_message` | Table | Notifications | Yes: the recipient's email address (staff), with the subject and body (alert facts and the escalation role; no traveller, officer or document data and no notes) | `Database:Retention:AlertEmailDays`, default 180 days (Proposed, TC-67); Pending emails are never deleted | `Database:Retention` (migration job, `retention_period`); deleted by the Cronz retention job | Notifications (written by Ariva.Api.Main and Ariva.Api.Stream, sent by Ariva.Api.Integration) |
| `report_delivery` | Table | Reports | Yes, references: the recipient's account id, the local day, status and attempts | `Database:Retention:ReportDeliveryDays`, default 400 days (Proposed, TC-66) | `Database:Retention` (migration job, `retention_period`); deleted by the Cronz retention job | Reports (Ariva.Api.Cronz) |

### Integration and messaging

| Object | Kind | Category | Personal data | Retention | Where set | Owner |
|---|---|---|---|---|---|---|
| `integration_call` | Table | Audit | None about people: each Integration API call's client id, session, scope, route, site, status, payload SHA-256 and size, and the calling system's source address (never the payload) | `Database:Retention:IntegrationCallDays`, default 400 days (Proposed, TC-68) | `Database:Retention` (migration job, `retention_period`); deleted by the Cronz retention job | Integration (Ariva.Api.Integration) |
| `integration_idempotency` | Table | Operational state | None: a batch's key, operation, request SHA-256 and the answer sent (flight keys and item errors) | 24 hours, then swept every 10 minutes | Code: `IdempotencySweeper` (script 0026) | Integration (Ariva.Api.Integration) |
| `outbox_message` | Table | Messaging | Possibly: domain events as sent to Kafka (aggregates and configuration; the zone profile published event names the publishing user) | Sent rows deleted after `Kafka:Outbox:KeepSentDays` (7 days), except the newest row of each key on a compacted topic, kept until a newer one | `Kafka:Outbox` settings (the relay's housekeeping); script 0052 | Messaging (every host that writes domain events) |
| `processed_event` | Table | Messaging | None: consumer and event id | `Kafka:Outbox:KeepProcessedDays` (35 days, longer than any deleting topic) | `Kafka:Outbox` settings (the relay's housekeeping) | Messaging (every consuming host) |

### Validation and availability (pilot evidence)

| Object | Kind | Category | Personal data | Retention | Where set | Owner |
|---|---|---|---|---|---|---|
| `validation_campaign` | Table | Validation evidence | Yes, references: who planned, started and closed it, as account ids | Indefinite (pilot acceptance evidence) | Data boundary, Proposed; script 0047 | Validation (Ariva.Api.Main) |
| `validation_campaign_zone` | Table | Validation evidence | None | Indefinite (as its campaign) | Script 0047 | Validation (Ariva.Api.Main) |
| `validation_campaign_line` | Table | Validation evidence | None | Indefinite (as its campaign) | Script 0047 | Validation (Ariva.Api.Main) |
| `manual_count` | Table | Validation evidence | Yes, references: the observer's account id; a correction's reason in free text | Indefinite (pilot acceptance evidence) | Script 0047 | Validation (Ariva.Api.Main) |
| `validation_campaign_desk` | Table | Validation evidence | None: the staffed border desks in a campaign's scope. Desk-level border data | Indefinite; To confirm with the border authority | Script 0048 | Validation (Ariva.Api.Main) |
| `tracer_batch` | Table | Validation evidence | Yes, references: the observer's account id and the capturing device's clock | Indefinite (pilot acceptance evidence) | Script 0048 | Validation (Ariva.Api.Main) |
| `tracer_run` | Table | Validation evidence | Yes, references: the observer's account id; the tracer is a campaign label (`T-07`), never a name | Indefinite (pilot acceptance evidence) | Script 0048 | Validation (Ariva.Api.Main) |
| `desk_observation_batch` | Table | Validation evidence | Yes, references: the observer's account id. Desk-level border data | Indefinite; To confirm with the border authority | Script 0048 | Validation (Ariva.Api.Main) |
| `desk_observation` | Table | Validation evidence | Yes, references: the observer's account id; a correction's reason in free text. Desk-level border data | Indefinite; To confirm with the border authority | Script 0048 | Validation (Ariva.Api.Main) |
| `validation_result_revision` | Table | Validation evidence | Yes, references: who asked for a recomputation and, in the observer-level section, observers as account ids | Indefinite (frozen results); To confirm with the border authority | Script 0050 | Validation (Ariva.Api.Main) |
| `availability_minute` | Hypertable | Availability evidence | None: per site and minute whether Ariva was measuring, and why not | Indefinite (no retention policy; the pilot criterion's evidence) | Script 0042 | Availability (Ariva.Api.Cronz) |
| `availability_cursor` | Table | Operational state | None: the ledger's next minute per site | Overwritten | Script 0042 | Availability (Ariva.Api.Cronz) |

## Outside the database

| Data | Personal data | Retention | Where set |
|---|---|---|---|
| Logs and traces (Serilog to the console, Loki or OTLP; OpenTelemetry traces) | Possibly: user names in some messages, client addresses at the ingress; never passwords, tokens, keys or TOTP codes (redacted) | Proposed: 30 days in the log store, readable by the operations team only (TC-65) | The log store's own retention (Loki or SigNoz), outside Ariva |
| Kafka topics | As the events: aggregates and configuration | 3, 14 or 30 days by class, or compacted ([overview](../architecture/overview.md) section 5) | `Kafka:Topics`, the topics job |
| Floor plan images (file storage) | None | As their `floor_plan` row | `IFileStorage` |
| Advertisement images and videos (file storage) | Possibly: an image or video an advertiser supplied may show a person (a model in an advertisement). An image is written anew on upload, so no camera or location metadata is kept. A video (ARV-142b) keeps its media as uploaded, but its metadata (titles, dates, tags, chapters, attachments, track and handler names, the compressor name and creation and change times) is zeroed before it is stored; the muxer's name (WebM `MuxingApp` and `WritingApp`, for example "Lavf60.16.100") is kept, as Matroska requires | As their `advertising_media` row | `IFileStorage` |
| Redis (cache, live snapshots, SignalR backplane) | Yes, references: an account's roles and its session's state, cached by account and session id (no name, address or password) | Seconds to minutes; never a system of record | Cache settings (`Cache`, `Auth:Sessions:CacheSeconds`) |
| Backups | As the database | No longer than the dispute window plus the backup cycle (Proposed) | Wiki 04 section 10 |
