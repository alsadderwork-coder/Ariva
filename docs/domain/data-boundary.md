# AMAN to Ariva data boundary

What crosses from AMAN into Ariva, what never crosses, how AMAN codes are mapped, how long data is kept, and which privacy laws are in scope. Sources: D5 (Integrations; Multi-tenancy, hosting and security), D4 (tracking rules, privacy profile), D6 (DPIA and Angola actions), and the decisions up to 2026-10-01. Anything the sources do not cover is marked To confirm.

## Principles

1. Ariva receives aggregate-only contracts from AMAN (decision 9, [ADR-0010](../architecture/adr/ADR-0010-aggregate-only-aman-contracts.md)).
2. Officer-level analytics stay in AMAN and never cross into Ariva. The Border dashboard links to AMAN's own reports (resolution of decisions 8 and 9 in D5).
3. The AMAN feed exists only in border deployments. AMAN publishes it through its existing outbox to `aman.feed.<contract>.v1` topics inside the border deployment.
4. Timestamps are coarsened to the interval, which blocks re-identification by timestamp alignment (D5, citing D1).
5. Only lane-level aggregates leave a border deployment; desk-level data, tracks and desk events never reach the airport side ([ADR-0012](../architecture/adr/ADR-0012-deployment-per-authority-one-way-feed.md)).
6. Data never leaves the country; national views receive aggregates only.

## The four V1 contracts

The contracts live in `Platform/Business/Ariva.Business.Contracts` (namespace `Ariva.Business.Contracts.Aman.V1`). The tables give D5's specified content, the field in the skeleton records as drafted on 2026-10-01, and gaps to reconcile. D5 is the reference for content; the skeleton is the reference for names once reconciled.

### DeskSessionChanged

Topic `aman.feed.desk-session-changed.v1`, key AMAN desk code. Used as the rank 2 (login) signal in the desk state machine (formulas F10). Reconciled with D5 on 2026-10-01.

| Field | Type | Notes |
|---|---|---|
| `SiteCode`, `DeskCode` | string | Mapped to an Ariva desk (below) |
| `State` | `DeskSessionState { Opened = 1, Closed = 2, Paused = 3 }` | On break maps to `Paused` |
| `LaneCategory` | string | AMAN lane code mapped to an Ariva lane category; empty when closed |
| `OccurredAtUtc` | `DateTimeOffset` | Coarsened by AMAN to its publishing interval |
| `SourceEventId` | string | Idempotent consumption |
| Deliberately absent | | Officer identity |

### DeskIntervalStats

Topic `aman.feed.desk-interval-stats.v1`, key AMAN desk code. Feeds service and cycle times for the nowcast and forecasts (F8, F9, F10). Reconciled with D5 on 2026-10-01.

| Field | Type | Notes |
|---|---|---|
| `SiteCode`, `DeskCode` | string | |
| `IntervalStartUtc`, `IntervalSeconds` | `DateTimeOffset`, int | One-minute intervals; V1 consumers reject any other length |
| `TransactionsProcessed` | int | Approaches (a family of four is one transaction) |
| `DocumentsProcessed` | int | Documents (the same family is four) |
| `MeanServiceSeconds`, `P90ServiceSeconds` | double | Transaction start to end |
| `MeanCycleSeconds` | double | Start to next start while open; sets throughput (F8) |
| `LaneCategory` | string | AMAN code mapped to an Ariva lane category |
| `SourceEventId` | string | Idempotent consumption |
| Deliberately absent | | Traveller, document and officer identifiers |

### EGateIntervalStats

Topic `aman.feed.egate-interval-stats.v1`, key AMAN gate code. Feeds e-gate throughput and reject coupling (F12). Reconciled with D5 on 2026-10-01.

| Field | Type | Notes |
|---|---|---|
| `SiteCode`, `GateCode` | string | Mapped to an Ariva desk of kind `EGate` |
| `IntervalStartUtc`, `IntervalSeconds` | `DateTimeOffset`, int | One-minute intervals |
| `Attempts`, `Accepted`, `Rejected` | int | |
| `RejectsByCategory` | dictionary of `EGateRejectCategory` to count | Coarse categories only (Other, DocumentRead, BiometricCapture, Eligibility, ReferredToOfficer, Technical). AMAN folds any category with fewer than 3 rejects in the interval into Other, so no individual outcome can be inferred |
| `MeanCycleSeconds` | double | |
| `SourceEventId` | string | Idempotent consumption |
| Deliberately absent | | Traveller identifiers, detailed security outcome codes |

### InboundFlightLaneDemand

Topic `aman.feed.inbound-flight-lane-demand.v1`, key AMAN flight key. Feeds the arrival lane split (F14). Computed by AMAN from API data. Reconciled with D5 on 2026-10-01.

| Field | Type | Notes |
|---|---|---|
| `SiteCode`, `FlightKey`, `ScheduledArrivalUtc` | string, string, `DateTimeOffset` | Flight key mapped to Ariva's canonical flight id |
| `BoardedTotal` | int | Not always the sum of the lanes (transfers, crew) |
| `PassengersByLane` | dictionary of lane code to count | Manual lanes: CIT, RES, VIS, CRW |
| `EGateEligible` | int | Separate field; Ariva applies its own uptake and reject rates |
| `ComputedAtUtc` | `DateTimeOffset` | Latest wins |
| `SourceEventId` | string | Idempotent consumption |
| Deliberately absent | | Names, document numbers, nationality lists below the lane level |

Versioning (skeleton README, consistent with D5): additive changes within V1 bump `ContractVersion.Current`; breaking changes go to `Aman/V2`, run in parallel until AMAN moves over. A data boundary test in `Ariva.UnitTests` fails the build if a contract property looks like a person or officer identifier.

## What never crosses into Ariva

| Category | Examples | Where it stays |
|---|---|---|
| Officer identity | Names, staff numbers, usernames, badge ids, officer-to-desk assignments, shift rosters | AMAN |
| Person identity | Traveller names, passenger ids, PNR locators, API and PNR records, per-person nationality | AMAN |
| Document data | Document numbers, MRZ, dates of birth, issuing state per person, visa data | AMAN |
| Biometrics | Face and fingerprint images, templates, match scores | AMAN, ABIS |
| Images | Any camera frame, including stereo sensor images (processed on the sensor; only coordinates leave) | Sensor |
| Exact event timestamps from AMAN | Per-transaction times | AMAN (Ariva receives interval-coarsened times) |

And from a border deployment to an airport deployment, additionally never: track ids, track samples, zone events, desk-level states or statistics, AMAN feed messages. Only lane-level wait times and KPIs cross (Proposed field list: lane, bin start, realised wait P50 and P90, nowcast, queue length, bin status, data quality; To confirm).

## Code mapping

| AMAN identifier | Ariva concept | Mapping (Proposed) |
|---|---|---|
| `SiteCode` | `Site` | Configured per site |
| `SiteCode` + `DeskCode` | `Desk` (`DeskKind.ImmigrationDesk`) | `DeskCodeMapping { SiteId, ExternalSiteCode, ExternalCode, DeskId, ValidFromProfileVersion }` in Site Topology, published with the zone profile |
| `SiteCode` + `GateCode` | `Desk` (`DeskKind.EGate`) | Same table |
| Lane category code | `LaneCategory` | Per-site lane category mapping; AMAN's code list To confirm |
| `FlightKey` | Canonical `Flight` | Flight id map in Flight Demand |

Rules (Proposed):

- The Border Integration translator resolves codes at event time against the active zone profile.
- An unknown code is never guessed: the message is parked, a data-quality alert goes to the site administrator, and the affected desk counts as `Unknown` until mapped.
- A desk code identifies a physical counter, not a person. Ariva holds no roster and no officer-to-desk mapping. Because desk plus minute could be joined with AMAN's own records to identify an officer, desk-level data stays inside the border deployment.

## Retention

| Data | Retention | Source |
|---|---|---|
| Track ids | Rotated at zone exit; never persist past the operating day | D4 |
| track_samples, zone_events | Contract dispute window, default 90 days; compressed after one day | D5 |
| device_health | Dispute window (evidence for sensor-outage exclusions) | Proposed |
| `aman.feed` Kafka topics | Medium class; AMAN owns the topic configuration | D5; values To confirm |
| Ariva Kafka topics | Short, medium, long or compacted classes (see the overview) | D5; values Proposed |
| Desk staff and service zone readings (`desk_zone_reading`: desk key, zone role, time, count, flag; no identities). Desk-level border data: stays in the border deployment and no API reads it; only the desk feed turns it into desk states (ARV-116) | 7 days, dropped by TimescaleDB retention | Proposed, To confirm |
| Shadow nowcast (`queue_minute_shadow`: zone, minute, minutes or no-service reason, F11 flag, and since ARV-117b, script 0045, the lane's sensor cycle time the shadow took; ARV-117, in its own table since ARV-117a, script 0043): the queue's nowcast without AMAN inputs, a lane aggregate with no desk code and no identities. Never shown to staff or passengers, never an alert input or in a report; the runtime login can write it but not read its values (database privileges); read only by the pilot's validation comparison (ARV-104f) through the `ariva_validation_reader` role | As queue_minute: no retention policy (evidence) | Proposed |
| Sensor-only desk minutes (`desk_sensor_minute`: desk key, minute, seconds per state and flag from the desk's staff and service zones alone; ARV-117a, script 0044; no transactions, no identities). Desk-level border data: stays in the border deployment and no API reads it; only the stream's desk term (the shadow nowcast's sensor-only n_open) and the validation comparison read it | As desk_minute: no retention policy | Proposed, To confirm |
| queue_intervals, desk_intervals, forecast_values, border_lane_kpis | Indefinite | D5 |
| Hourly and daily report aggregates | Indefinite | D5 |
| Report schedules and deliveries (schedule, local day, recipient account, status; ARV-060). Report emails carry lane aggregates, alerts and device uptime only, to Ariva accounts allowed the site. The recipient picker lists the user names and display names of the accounts allowed the site that hold a report role, all-sites administrators included, to whoever may manage that site's schedules | To confirm; Proposed: 400 days for delivery rows, schedules until deleted (audited) | Proposed |
| Configuration, contracts, SLA decisions, alerts | Indefinite, audited | D5 |
| Evidence packs | To confirm (at least the contract's dispute and audit periods) | |
| Flight data | To confirm | |
| Logs and traces | To confirm; Proposed: no track ids or desk codes in log messages beyond the operating day | |
| Phase 0 lab recordings (office entrance or corridor) | To confirm, including notice to people recorded | |

## Privacy laws in scope

D4 to D6 do not analyse the laws themselves; the privacy analysis is in D1 (Challenging the Brief), which was not a source for this document. The table records only what D4 to D6 say.

| Jurisdiction | What the sources say | To confirm |
|---|---|---|
| UAE (PDPL) | Not covered in D4 to D6 | Applicability to the border authority and to airport operators; DPIA requirements; lawful basis for anonymous tracking; any sector or emirate rules. Confirm with counsel and D1 |
| Angola | Confirm whether stereo counters fall under the video-surveillance law before choosing the sensor; LiDAR may avoid the authorisation route (D4, D6). Ask local counsel; prepare the DPIA template (D6 "start this month") | The data protection law and authority; whether anonymous coordinates are personal data; authorisation route for stereo sensors |
| Tanzania | In-country deployment also avoids Tanzania's cross-border permit (D5, citing D1) | The act and any registration or permit duties for in-country processing |
| GDPR (where EU-linked) | Not covered in D4 to D6 | When it applies (for example an EU-based client, processor or support access); transfer rules |
| All markets | A DPIA template per market (D4, D6). Wi-Fi and BLE identifiers are pseudonymous and still personal data (D4). Appearance re-identification drifts toward biometric data (D4, citing D1). No images leave stereo sensors; LiDAR captures none | Whether ephemeral, anonymous track coordinates count as personal data under each law; notice requirements for passengers |
