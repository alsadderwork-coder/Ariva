# Sensor support and adapters

Ariva is vendor-agnostic. It supports any people-sensing device used for queue management by combining a small set of **transports** with **dialect mappers** and a **capability profile** per device family, the same way AMAN abstracts e-gate vendors. Adding a family is configuration plus a mapper and a conformance test, not a new subsystem.

What "all sensors" means in practice: any device or perception platform that emits counts, occupancy, line crossings or tracks over a standard transport. Ariva does not consume raw LiDAR point clouds; those go through a perception platform first (see below). That boundary is deliberate: perception is a product category of its own, and every serious LiDAR deployment already runs one.

## Capability tiers

| Tier | Data the device provides | What Ariva computes | Typical devices |
|---|---|---|---|
| T1 Counts | Interval counts per counting line (in and out) | Throughput; realised wait from cumulative arrival and departure curves (FIFO assumption, formulas F5); nowcast | Basic overhead counters, camera analytics with line counting, thermal counters |
| T2 Occupancy | T1 plus zone occupancy (people in a polygon) | Queue length directly; overflow detection; better nowcast | Xovis zone logics, camera 3D counters, LiDAR perception zones |
| T3 Tracks | Anonymous track ids with entry and exit crossings or positions | Per-person realised wait (exit minus entry), dwell, desk approach, desk occupancy | Xovis multi-sensor tracking, LiDAR perception platforms |
| T4 On-device KPIs | Device-computed queue KPIs (waiting time, queue length) | Used as a cross-check against Ariva's own calculation; never the evidence source for penalties | Xovis queue logics, some LiDAR platforms |

Penalty-grade evaluation (v1) requires T3 data or T1 data validated against manual counts for that zone profile version.

## Transports

| Transport | Direction | Notes |
|---|---|---|
| HTTPS push (webhook) | Device to `Ariva.Api.Ingest` | Per-device credential (bearer key or Basic), TLS, IP allowlist, 256 KB body limit |
| MQTT(S) | Device to the broker inside `Ariva.Api.Ingest` (ARV-024) | TLS on its own port (8883), device code and credential as user name and password, optional pinned client certificate, topic ACL per device, publish only |
| REST pull | `Ariva.Api.Ingest` polls the device or controller | Only registered device addresses (SSRF control); `If-Modified-Since` or cursor |
| WebSocket stream | Perception platform to `Ariva.Api.Ingest` client | JSON or protobuf frames; reconnect with backoff |
| TCP or UDP stream | Device to listener | Length-prefixed frames, bounds-checked parsing, source address allowlist |
| (S)FTP or FTPS file drop | Device uploads interval files | Polled from a dedicated drop directory; size and name validation |
| ONVIF Profile M metadata | Camera analytics events | For IP cameras with counting or occupancy analytics |
| Canonical push | Sensor gateway to the Integration API (`sensing:write`) | For sites that aggregate sensors behind their own gateway |

## Dialect mappers

A dialect maps a vendor payload to canonical events: `LineCrossing` (lineId, direction, trackId optional, timeUtc), `ZoneOccupancy` (zoneId, count, timeUtc), `TrackPosition` (trackId, x, y, timeUtc), `IntervalCount` (lineId, in, out, fromUtc, toUtc), `DeviceStatus` (online, temperature, frame rate, clock offset).

Two kinds:
- **Coded mappers** for first-class families (Xovis, the main LiDAR perception platforms), with typed parsing and full conformance tests.
- **Declarative mappers** (ARV-024) for the long tail: a JSON mapping document per vendor format, shipped with Ariva and reviewed like code, read with a restricted path syntax. Expressions are paths, not code (CWE-94): nothing in a mapping is evaluated, compiled or executed. JSON only; XPath is not implemented.

Every payload is untrusted (CWE-501): schema validated, size limited, timestamps checked against the device clock offset, ids namespaced by device. The canonical records and their bounds live in `Ariva.Core.Sensing` (`CanonicalEventRules`, ARV-021). A device is tied to its owning queue zone by the zone's name in the site's profiles, which survives new profile versions (zone ids do not), so the zone key of a device's events is stable.

## Device family catalogue

Status column: **Phase 0** means built and tested against recorded or emulated payloads in Phase 0; **Pilot** means certified on hardware before or during the pilot; **Planned** means designed for, built on demand. Protocol details marked "verify" must be confirmed against the vendor's current API documentation or SDK before certification.

### Overhead stereo vision (Xovis)

Xovis models share one data interface family, so one adapter covers them; the model only changes the mounting height and coverage.

| Series | Models | Mounting height | Typical use in Ariva |
|---|---|---|---|
| PC2 series | PC2SE, PC2R, PC2R-O (outdoor) | About 2.2 to 6 m | Immigration halls, security lanes, low ceilings |
| PC2 extended coverage | PC2SE-UL, PC2SE-L, PC2R-UL, PC2R-L and outdoor variants | Extended range variants | Wider halls at low heights |
| PC3 series | PC3 (6 to 14 m), PC3-L (6 to 9 m), PC3-M1, PC3-M2, PC3-H, PC3-UH (16 to 20 m), outdoor -O variants | 6 to 20 m | Check-in halls and high ceilings |
| PF series | Listed in the 2025 selection guide | verify | verify |

Data output: data push over HTTP(S), MQTT(S), FTP(S), SFTP, TCP and UDP, and a REST API with Swagger documentation (PC3 datasheet); JSON payloads with counting-line and zone logics, intervals and track data in multi-sensor setups; four privacy modes, text-only output. Ariva adapter: `Xovis` dialect over HTTPS push (Phase 0, against recorded payloads) and over the MQTT transport (Phase 0 transport; the Xovis MQTT payload is assumed to be the push JSON, to confirm), REST pull for configuration and health (Pilot). Multi-sensor stitching is done by the Xovis multi-sensor setup, not by Ariva.

### LiDAR (through perception platforms)

| Layer | Products | Ariva integration |
|---|---|---|
| Perception platforms (what Ariva connects to) | Ouster Gemini, Outsight SHIFT (Augmented LiDAR), Seoul Robotics SENSR, Blickfeld Percept and Qb2 smart LiDAR | Ouster Detect through the declarative mapping `ouster-detect-v1` over HTTPS push or MQTT (Phase 0, from documentation); coded adapters for binary outputs (Outsight OSEF, SENSR protobuf); outputs are tracks, zone occupancy and line counts. One platform certified for the pilot, others Planned |
| LiDAR hardware (behind the platform) | Ouster (OS0, OS1, OSDome, and Velodyne lines after the merger), Hesai (XT and JT series), RoboSense, Livox, Seyond, Blickfeld | Not consumed directly; supported if the chosen perception platform supports it |

Coverage for LiDAR is radius-based and depends on the platform's tracking range; the BOQ uses an assumed 10 m effective radius until a platform is certified.

### Camera-based 3D and AI counters

| Products | Transport | Status |
|---|---|---|
| Axis 3D People Counter and AXIS Object Analytics | ONVIF Profile M events or Axis HTTP API (verify) | Planned |
| Hikvision and Hanwha people counting cameras | ONVIF Profile M or vendor HTTP push (verify) | Planned |
| Existing CCTV with an analytics server | Analytics server events over MQTT or HTTPS; Ariva never receives video | Planned |

### Other sensing

| Kind | Examples | Tier | Status |
|---|---|---|---|
| Thermal or time-of-flight counters | Irisys, other ToF overhead counters | T1 or T2 | Planned via declarative mapper |
| Wi-Fi or BLE flow time | Probe-based travel-time systems | Segment travel times, no queue length | Planned, supplementary only (accuracy and privacy limits documented in D4) |
| Desk occupancy | Desk presence sensors, the AMAN desk session signal | Desk state input | AMAN signal in Phase 0 |
| Manual counts | Observer tablet app | Validation ground truth | Phase 1 (validation campaign) |

## Device registry and lifecycle

`Device` records carry family, model, transport, credential reference, mounting height, position on the level, coverage footprint, linked zones, clock source (NTP or PTP) and status (`Commissioning`, `Online`, `Degraded`, `Offline`, `Retired`). A device moves to `Online` only after a passed calibration (counting accuracy at or above 95 percent by default against a manual count sample). Heartbeat loss marks it `Offline` and its zones `Degraded`; waits for degraded zones are shown as bands, never as numbers (see below).

### Heartbeats and degraded zones (ARV-025)

Every health report Ingest publishes (`ariva.device.health.v1`: each status a device sends, or Ariva's own report at most every 10 seconds while it sends data) is consumed by Main as the device's heartbeat (`device_heartbeat`: when it was last heard from, its online flag, frame rate, temperature and Ariva's clock estimate; newer reports only, reports out of the canonical bounds ignored). Every `Devices:Health:SweepSeconds` (15) one replica of Main sweeps: a commissioned device (`Online` or `Degraded`) not heard from for `Devices:Health:HeartbeatTimeoutSeconds` (180; a device must push or report at least every half of it) goes `Offline`. A report brings an `Offline` or `Degraded` device back `Online`; a report with `online` false makes an online device `Degraded`; a report older than the timeout (a backlog) changes no state. A queue zone is `Healthy` when all its commissioned devices are online, `Degraded` while any is offline or degraded, and `Unmonitored` with none; every sweep re-assesses all zones, so calibrations, moves and retirements show within one sweep. Device changes publish `DeviceRegistryChanged` (`HealthOffline`, `HealthOnline`, `HealthDegraded`) and zone changes `ZoneHealthChanged` on `ariva.device.zone-health.v1` (keyed by `<site>/<queue zone name>`, compacted), both through the outbox. `GET api/v1/admin/devices/health[?siteCode=]` lists the devices (state, last seen, seconds since, last report) and zones of the caller's sites with the timeout, offline and degraded first, at most 2,000 of each (`truncated` says when there were more), and `GET api/v1/admin/devices/{id}/health` one device. Metrics (meter `Ariva.Devices`): `ariva.devices.heartbeat_lost`, `ariva.devices.recovered`, `ariva.zones.degraded` (counters per site) and, from the last sweep, `ariva.devices.offline`, `ariva.devices.degraded`, `ariva.zones.degraded_now`.

## HTTPS push ingest (ARV-023)

`Ariva.Api.Ingest` takes pushes at `POST api/v1/ingest/zones/{zone}/events` (canonical dialect) and `POST api/v1/ingest/zones/{zone}/xovis` (Xovis dialect), for the device's own queue zone only (403 otherwise), with the device credential (ARV-022). A body is JSON (`application/json`, otherwise 415) of at most 256 KB (413) and 2,000 events (`Ingest:MaxEventsPerMessage`, 400). The answer is 202 with the events accepted, refused, flagged and deliberately ignored, the clock estimate and the first problems; 400 when the message is malformed or the device is registered for another dialect; 503 only when the broker could not store the events, so the device sends again. Problems quote vendor names in a safe form (letters, digits and a few separators, at most 40 characters) and never echo the payload; a body slower than 1 KB a second is cut off. `Ingest:MaxEventsPerMessage` is at most 3,000, which keeps a full batch under Kafka's 1 MB message limit. A push that meets an outage (Kafka not acknowledging, or the database not answering a cold credential or zone lookup within 5 seconds) is answered 503 with Retry-After 5 (ARV-072, `DependencyOutageHandler`): the device keeps the batch and sends it again; Ingest has no buffer of its own.

Every event is checked against `CanonicalEventRules`; lines and zones must be named as in the published geometry of the device's zone (the queue zone and the zones that hang off it), so a misconfigured logic is refused rather than counted. The device's clock offset is the exponentially weighted average (weight 0.2) of its send time against Ariva's receive time, with the last ten readings for F19's stability test: within 500 ms the clock is Ok, beyond it Corrected (times shifted by the estimate) when stable and Unreliable otherwise. A reading more than a day off, or a send time not newer than the device's last one (a resend), does not move the estimate, and corrections use the mean of the stable readings. Events more than 500 ms ahead of receipt after correction, and events more than 10 minutes late, are flagged `Skewed`; events more than 5 minutes ahead or older than the 3-day topic retention are refused. Track ids become `<device code>/<track id>`. The batches go to `ariva.device.track-sample.v1`, `ariva.device.vendor-line-crossing.v1`, `ariva.device.zone-occupancy.v1` and `ariva.device.interval-count.v1`, keyed by `<site>/<queue zone name>`, with a batch id derived from the device, the kind and a SHA-256 of the body, so a resent body repeats its ids and consumers drop it, while a vendor counter that restarts never hides new data; a health report goes to `ariva.device.health.v1` (keyed by device id) with every status the device sends, or at most every 10 seconds otherwise. Batches from a device still in commissioning say so (`Commissioned` false) and never feed KPIs.

Every accepted batch is also archived (ARV-026, script 0017; device health reports in `device_health_event` since ARV-036, script 0019): the Stream host consumes the four sensing topics (one transaction per Kafka message; multi-batch writes are for replay and backfill tools) and writes each event to `sensing_event` by binary COPY, one row per event with its zone, device, batch and position in it, flags, receipt time and the fields of its kind; `sensing_batch` records each batch once, so a redelivered or resent batch is not archived twice (checked over the three days the topics keep). Batches and events are checked again on the way in (they crossed Kafka): a batch without a usable site, zone or device, or received outside a sane window, is skipped whole, and an event out of the canonical bounds, with unknown flags or far from its receipt time is skipped alone; both are logged and counted, so one bad event never costs its batch. Track ids are not archived as such: each becomes a pseudonym of its UTC day (HMAC-SHA256 with a random key per day, `sensing_day_key`), so a track can be followed within the day but not linked across days (D4: track ids never persist past the operating day); a day's key is destroyed after three days, when no event of that day can still arrive. The operating day is approximated by the UTC day (To confirm: the site's local day). A database outage is waited out for about a minute per attempt (about four minutes with the consume pipe's retries, under Kafka's five-minute poll interval) before the message fails, so a failover does not send evidence to the dead-letter topic. Replay and recomputation read one queue zone of a site over at most 31 days in time order, one day per statement (`ISensingArchive.ReadAsync`), and turn each row back into its canonical event. The runtime role can insert and read but never update, delete or truncate; TimescaleDB is required: both tables are hypertables with 1-day chunks, `sensing_event` is compressed after one day and both drop chunks after 90 days (values To confirm per site, the contract's dispute window). Without TimescaleDB the migration fails, unless the database sets `ariva.allow_plain_postgres` to `on` (local tests on plain PostgreSQL), and then nothing is dropped.

The PRD named one topic, `ariva.sensing.events.v1`; the topics follow ADR-0019 and the topic table in the architecture overview instead, plus two new ones for counting and occupancy devices, which D5 had not foreseen.

### Canonical dialect

```json
{ "sentUtc": "2026-10-02T18:05:00.250Z", "packageId": 4711,
  "tracks":    [{ "trackId": "7", "x": 12.5, "y": 8.25, "heightMetres": 1.72, "timeUtc": "2026-10-02T18:04:59.800Z" }],
  "crossings": [{ "lineName": "Entry A", "direction": "In", "trackId": "7", "timeUtc": "2026-10-02T18:04:59.900Z" }],
  "occupancy": [{ "zoneName": "Snake A", "count": 41, "timeUtc": "2026-10-02T18:05:00Z" }],
  "intervals": [{ "lineName": "Entry A", "in": 30, "out": 2, "fromUtc": "2026-10-02T18:00:00Z", "toUtc": "2026-10-02T18:05:00Z" }],
  "status":    { "online": true, "temperatureCelsius": 41.5, "frameRate": 12.5, "clockOffsetMilliseconds": -18, "timeUtc": "2026-10-02T18:05:00Z" } }
```

Strict: unknown members, numbers for enums and times without `Z` or `+00:00` refuse the message. Positions are floor coordinates of the device's level in metres.

### Xovis push format

Firmware 5 data push in JSON (`package_info.version` "5.0"), one envelope or an array of envelopes per POST. Configure the Xovis push agent with the URL `https://<ingest host>/api/v1/ingest/zones/<queue zone>/xovis`, JSON format, RFC 3339 or Unix milliseconds time, and authentication `BEARER_TOKEN` with the device credential (or a custom header `X-Ariva-Device-Key`, or Basic with the device code). Name every Xovis logic after the Ariva line or zone it measures.

| Envelope | Mapping | Status |
|---|---|---|
| `logics_data` | Each record of a logic: counts named `fw`, `forward`, `in`, `enter` or `entry` are crossings in, `bw`, `backward`, `reverse`, `out`, `exit` or `leave` crossings out, giving an interval count on the line named like the logic from `from` to `to`; a logic whose only count is `balance` or `occupancy` gives the occupancy of the zone named like the logic at `to`. Other counts (dwell time, queue statistics) are ignored and reported | Field names verified on a firmware 5.8.2 capture (MRI) and the V5 API (sc-bos); count names are what the integrator configures (Forward and Reverse in the capture, fw, bw and balance as template defaults) |
| `live_data` | Each frame's tracked PERSON, WHEELCHAIR and GROUP objects become track positions; positions are read as metres in the sensor's frame and placed on the floor with the device's registered position and orientation; tagged objects (staff tags), other types and scene events (which name geometry by id only) are ignored and reported | Envelope verified from parser code (xovis-sdk); frame fields, metres and the sensor frame inferred from the V5 API schema; to confirm with Xovis |
| `connection_test` | Accepted, nothing stored | Verified from parser code |
| Firmware 3 and 4 formats (`LineCrossing` arrays, `sensor-time` count documents) | Refused (400) | Verified (IoTnxt); not supported |

`package_info.id` is the package id (duplicates repeat their batch id); `sensor_info.time` is the device's send time for the clock estimate. Conformance samples and their expected canonical events are in `Platform/Backplane/Ariva.UnitTests/Sensing/Samples`. To confirm with Xovis or the reseller before certification: a real live data capture (units, origin and axes, what normalization level 1 changes), multi-sensor and HUB payloads (identity, coordinate frame), the official push schema per firmware, queue logic counter names and units, when bodies are arrays, which status codes count as success and whether package ids repeat on retry, and TLS details.

## Declarative mappings (ARV-024)

A device on the `Declarative` dialect names a mapping (`mappingName`, for example `ouster-detect-v1`), and pushes to `POST api/v1/ingest/zones/{zone}/declarative` or publishes over MQTT. Mappings are the JSON files in `Platform/Backplane/Ariva.Infra/Sensing/Mappings`, embedded in the assembly and checked when a host starts (a broken mapping stops it); `GET api/v1/admin/devices/mappings` lists them. There is no way to add or change a mapping at run time: a new vendor format is a reviewed pull request with a documented sample and its expected events.

```json
{ "name": "ouster-detect-v1", "title": "...", "source": "https://docs.ouster.com/...",
  "positions": { "frame": "device", "scale": 1 },
  "package": "$.object_list[0].frame_count",
  "tracks": { "groups": "$.object_list[*]", "items": "^.objects[*]", "where": [{ "path": "@.classification", "equals": ["PERSON"] }],
              "trackId": "@.id", "x": "@.position.x", "y": "@.position.y", "height": "@.dimensions.height", "time": { "path": "^.timestamp", "unit": "us" } },
  "occupancy": { "items": "$.occupations[*]", "zone": "@.name", "count": "@.num_objects", "time": { "received": true } } }
```

| Part | Rule |
|---|---|
| Paths | Start at `$` (the payload), `^` (the current group) or `@` (the current item); then `.name`, `['name with spaces']` or `[index]`; one `[*]` only in `groups` and `items`. No filters, recursive descent, slices, unions, negative indexes, functions or expressions; at most 200 characters and 16 steps. Evaluating a path only walks JSON values |
| Sections | `tracks`, `crossings`, `occupancy`, `intervals`; each enumerates `items` from the payload or, with `groups`, from each group; up to 4 `where` filters keep items whose value equals one of the listed strings, numbers or booleans (others are ignored and counted) |
| Values | Ids are strings or integers; names are strings; positions finite numbers in metres times `scale`, on the floor (`floor`) or in the device's frame placed with its registered position and orientation (`device`); counts integers; a missing or mistyped required value refuses the message, naming the mapping path and never the payload |
| Times | RFC 3339 with an offset, or Unix time in `s`, `ms`, `us` or `ns`, between 2000 and 2100; `received` stamps events with Ariva's receipt time for payloads that carry none (no clock correction for those); `sentTime` and `package` feed the clock estimate and duplicate detection as for the coded dialects |
| Limits | A mapping document is at most 32 KB and strict (unknown members, comments and wrong types refuse it); per message at most four times the event limit of items are looked at and at most the event limit become events |

Shipped mappings:

| Mapping | Vendor format | Status |
|---|---|---|
| `ouster-detect-v1` | Ouster Detect (Gemini) perception output: `object_list` frames (`frame_count`, `timestamp` in microseconds, objects with `id`, `classification`, `position` in metres, `dimensions`) become tracks of PERSON objects in the device's frame; `occupations` (`name`, `num_objects`) become zone occupancy at receipt, since that stream carries no time | Field names from Ouster's documentation (connecting to output, appendix); positions are in the Gemini world frame, so register the device at the world origin with its rotation; key spelling to confirm against a recorded payload before certification |

Samples and expected events are in `Platform/Backplane/Ariva.UnitTests/Sensing/Samples/declarative/<mapping>`; every shipped mapping must have them. Other LiDAR platforms do not fit a JSON mapping today: Outsight streams the binary OSEF format and Seoul Robotics SENSR protobuf, so they need coded adapters; Blickfeld Percept's JSON nesting is not documented precisely enough to write one without a capture.

## MQTT transport (ARV-024)

`Ariva.Api.Ingest` runs an MQTT 3.1.1 and 5 broker (MQTTnet 5.2.0.1603) on its own Kestrel listener when `Ingest:Mqtt:Enabled` is true. It only takes device data in:

- TLS on port 8883 with the certificate in `Ingest:Mqtt:CertificatePath` and `CertificateKeyPath` (PEM, a mounted `kubernetes.io/tls` secret); clear text only in vm-local. Client certificates are optional and checked against a device's pin, not a CA. The certificate is read at start-up, so a renewed secret needs a rollout.
- CONNECT: user name the device code, password the device credential, checked exactly like an HTTPS push (prefix lookup, constant-time hash, allowed networks, pinned certificate), for devices registered on the `Mqtt` transport only (and HTTPS push takes only devices registered on `HttpsPush`, 403 otherwise, so a device has one way in and one rate limit). The client id is the device code; with MQTT 5 it may be empty and is then assigned, with MQTT 3.1.1 it must be the code. One device cannot take over another's session. A CONNECT asking for a will message is refused (a will would be ingested as data after any unclean disconnect).
- Limits before authentication: at most `MaxConnections` (2,000) connections and `MaxConnectionsPerAddress` (50) per client address, dropped before TLS; `ConnectTimeoutSeconds` (10) to finish TLS and CONNECT, after which the server closes the connection; `ConnectsPerAddressPerMinute` (30) CONNECT checks per address before any lookup. The first packet must be a CONNECT of at most 8 KB, and every packet stays under 8 KB until the CONNECT is accepted. Where sensors reach the broker through one NAT, firewall or source-NATing load balancer address, that address is what the per-address limits and a device's allowed networks see; raise `MaxConnectionsPerAddress` for such a site.
- PUBLISH to `ariva/v1/devices/<code>/<dialect>` with `canonical`, `xovis` or `declarative`, for the device's own queue zone. The payload goes through the same ingest as an HTTPS push, under a per-device rate limit of the same size (`Security:RateLimiting:Device`). MQTT 5 acknowledgements carry PayloadFormatInvalid with the reason when the ingest refuses a message, QuotaExceeded when over the rate limit; when the events cannot be stored the connection closes without an acknowledgement, so a QoS 1 message is sent again. Anything published elsewhere closes the connection; messages are never routed to subscribers or retained.
- The device's access is checked again on every message (a cached lookup evicted on every registry change): rotating or retiring the credential, moving the device off MQTT, narrowing its networks so they no longer hold the connection's address, or pinning a certificate the connection did not present closes a live connection at its next message. A reconnect that takes over a live session with `CleanSession` false keeps that session's identity, which fails closed after a rotation: the connection is closed at its first message and the next reconnect works.
- SUBSCRIBE is refused. No persistent sessions.
- After the CONNECT is accepted, a packet declaring more than 260 KB (a 256 KB push and its topic and properties) or a malformed length closes the connection before its body is buffered; MQTTnet alone would buffer up to 256 MB per packet. Size `MaxConnections` with the pod's memory: an accepted device can have one packet and the transport's read buffer (up to 1 MB) waiting.

In Kubernetes, `mqtt.enabled` adds the port, mounts `mqtt.tlsSecretName` and adds `api-ingest-mqtt-service` (`mqtt.serviceType`, with `externalTrafficPolicy: Local` outside ClusterIP so the device address is kept for allowed networks); the chart refuses MQTT without its TLS secret. Declaring the MQTT listener in code replaces the URLs Kestrel would otherwise bind, so the HTTP endpoints are declared again from `urls`, `http_ports` or `Application:BindingPort` (8080 by default); Kestrel logs that it overrides the configured addresses.

## Sensor emulator (ARV-028)

`Ariva.Simulation.Api` plays the reference day (ARV-027) and, since ARV-139b, the illustrative AUH-TA arrivals evening to Ingest as device traffic, over HTTPS push, authenticated with the credentials of devices registered in Ariva (an operator loads them with `PUT api/v1/simulation/sensors/devices` or the simulation appsettings secret; they are never returned or logged). Each device names its scenario site (DMO when not given) and sensor: DMO's S-01 to S-59, or AUH-TA's 84 seeded sensors (`Q-`, `O-` and `D-` codes of the ARV-139a seed, which are in Commissioning without a credential until an administrator issues one and records a calibration through the devices API; the emulator never relaxes device authentication). Each speaks the canonical or the Xovis dialect:

- The first sensor of a queue zone counts it: per-passenger crossings of `<zone> entry` (In) and `<zone> exit` (Out), with one track id per passenger from entry to exit, and the zone's occupancy at the end of each minute (canonical); or one-minute interval counts on the two line logics and a balance logic for the zone (Xovis).
- The first sensor of an overflow band (A-OV, D-OV, SEC-OV; AUH-TA's O-VIS-01 and O-EG-01 for A-VIS-OV and A-EG-OV) reports the band's occupancy beyond the snake capacity, as part of the queue zone it feeds.
- S-18, S-19 and S-20 (the fourth to sixth sensors over the arrivals Visitors hall, ARV-116) report the staff and service zones of the Visitors desks AR-08 to AR-22, five desks each: `<desk> staff` holds 1 while the scenario has the desk serving or idle, `<desk> service` holds 1 while serving, both 0 while paused, closed or out of service, read at the minute's start (the state holds through the scenario minute); a desk the scenario marks unknown is not reported. Ingest takes them only under a profile with those zones (the demo's v12 has none, so the demo does not register these sensors); the stream passes them to the desk engine (formulas F10, ARV-116). At AUH-TA every counter has these zones (`IC-01 staff`, `IC-01 service` and so on), and its `D-` sensors report them, up to four counters each, the same way.
- Every other sensor sends a sign of life each minute (a canonical status, or a Xovis envelope without logics).
- A sensor offline in the scenario (S-17 from 18:20 to 18:30; AUH-TA's Q-RES-04 from 18:25 to 18:35) sends nothing.

Passengers are the whole people of the fluid model (passenger n enters when cumulative arrivals reach n and leaves when cumulative departures reach n), so occupancy is always entries minus exits. A demo clock runs at 0.5 to 60 demo minutes per wall minute (`Simulation:Sensors:MaxSpeed`); demo times map onto wall times so Ingest's clock checks see live traffic. Controls (control scope): start (at a minute, at a speed, until a minute), pause, speed, jump, and the seed through `PUT api/v1/simulation/scenario`. The status shows, per device, pushes, the events the emulator expected and the events Ingest accepted. At high speed a sensor's silence is shorter than the heartbeat timeout, so a degraded zone shows only at speeds near 1.

## Conformance kit (how a family gets certified)

1. Record real payloads from the device (or vendor samples) into `Platform/Simulation/Ariva.Simulation.Api/Emulators/Sensors/<Family>/samples/`.
2. Write the expected canonical events next to them.
3. The conformance test replays samples through the adapter and compares output, including malformed and oversized payload cases (CWE-120, CWE-501).
4. A field validation against manual counts on site sets the accuracy figure in the calibration record.
5. The family is marked certified for a firmware range in the catalogue.

## Sources

- Xovis selection guide (metric, V1.8): https://api.xovis.com/fileadmin/user_upload/file/ressources/Brochures/Xovis-SelectionGuide-Metric-EN-V1.8.pdf
- Xovis model list (third-party copy of the selection guide): https://www.stromquist.com/customer/docs/Misc/Xovis-selection-guide.pdf
- Xovis PC3 technical datasheet: https://api.xovis.com/fileadmin/user_upload/file/ressources/PC3_Technical_Datasheet.pdf
- Example of Xovis data push to a third-party platform: https://docs.evolution.cloud.mrisoftware.com/IoTHubHelp/Content/2ConnectivityIntegrationLayer/IntegrationVendors/Xovis/Xovis.htm
- Xovis Sensor V5 API (release 5.3) and a firmware 5 webhook parser, in vanti-dev/sc-bos: https://github.com/vanti-dev/sc-bos/tree/main/pkg/driver/xovis
- xovis-sdk (unofficial, models generated from the firmware 5.9 API): https://pypi.org/project/xovis-sdk/
- Legacy (firmware 3 and 4) payloads, IoTnxt Raptor: https://community.iotnxt.com/docs/raptor/supported-devices/v-raptor/xovis-integration/
- Ouster Gemini: https://ouster.com/products/software/gemini
- Ouster Detect output (object_list and occupations, transports and ports): https://docs.ouster.com/ouster-detect/connecting_to_output/connecting-to-output.html and https://docs.ouster.com/ouster-detect/appendix/appendix.html
- MQTTnet 5.2.0.1603 on nuget.org: https://www.nuget.org/packages/MQTTnet.AspNetCore/
- MQTT 3.1.1, fixed header and remaining length (2.2.3): https://docs.oasis-open.org/mqtt/mqtt/v3.1.1/os/mqtt-v3.1.1-os.html
- Outsight people counting: https://www.outsight.ai/solutions/people-counting-technologies
- Blickfeld LiDAR software: https://www.blickfeld.com/lidar-software/
