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
| MQTT(S) | Device to broker; `Ariva.Api.Ingest` subscribes | TLS, per-device client certificate or username and key; topic ACL per device |
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
- **Declarative mappers** (JSONPath or XPath field maps stored with the device family) for the long tail. Expressions are paths, not code (CWE-94): the mapper evaluates a restricted path syntax and never executes scripts.

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

Data output: data push over HTTP(S), MQTT(S), FTP(S), SFTP, TCP and UDP, and a REST API with Swagger documentation (PC3 datasheet); JSON payloads with counting-line and zone logics, intervals and track data in multi-sensor setups; four privacy modes, text-only output. Ariva adapter: `Xovis` dialect over HTTPS push (Phase 0, against recorded payloads), MQTT (Pilot), REST pull for configuration and health (Pilot). Multi-sensor stitching is done by the Xovis multi-sensor setup, not by Ariva.

### LiDAR (through perception platforms)

| Layer | Products | Ariva integration |
|---|---|---|
| Perception platforms (what Ariva connects to) | Ouster Gemini, Outsight SHIFT (Augmented LiDAR), Seoul Robotics SENSR, Blickfeld Percept and Qb2 smart LiDAR | Coded adapters per platform; outputs are tracks, zone occupancy and line counts over REST, WebSocket or MQTT (verify per platform). One platform certified for the pilot, others Planned |
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

`Device` records carry family, model, transport, credential reference, mounting height, position on the level, coverage footprint, linked zones, clock source (NTP or PTP) and status (`Commissioning`, `Online`, `Degraded`, `Offline`, `Retired`). A device moves to `Online` only after a passed calibration (counting accuracy at or above 95 percent by default against a manual count sample). Heartbeat loss marks it `Offline` and its zones `Degraded`; waits for degraded zones are shown as bands, never as numbers.

## HTTPS push ingest (ARV-023)

`Ariva.Api.Ingest` takes pushes at `POST api/v1/ingest/zones/{zone}/events` (canonical dialect) and `POST api/v1/ingest/zones/{zone}/xovis` (Xovis dialect), for the device's own queue zone only (403 otherwise), with the device credential (ARV-022). A body is JSON (`application/json`, otherwise 415) of at most 256 KB (413) and 2,000 events (`Ingest:MaxEventsPerMessage`, 400). The answer is 202 with the events accepted, refused, flagged and deliberately ignored, the clock estimate and the first problems; 400 when the message is malformed or the device is registered for another dialect; 503 only when the broker could not store the events, so the device sends again. Problems quote vendor names in a safe form (letters, digits and a few separators, at most 40 characters) and never echo the payload; a body slower than 1 KB a second is cut off. `Ingest:MaxEventsPerMessage` is at most 3,000, which keeps a full batch under Kafka's 1 MB message limit.

Every event is checked against `CanonicalEventRules`; lines and zones must be named as in the published geometry of the device's zone (the queue zone and the zones that hang off it), so a misconfigured logic is refused rather than counted. The device's clock offset is the exponentially weighted average (weight 0.2) of its send time against Ariva's receive time, with the last ten readings for F19's stability test: within 500 ms the clock is Ok, beyond it Corrected (times shifted by the estimate) when stable and Unreliable otherwise. A reading more than a day off, or a send time not newer than the device's last one (a resend), does not move the estimate, and corrections use the mean of the stable readings. Events more than 500 ms ahead of receipt after correction, and events more than 10 minutes late, are flagged `Skewed`; events more than 5 minutes ahead or older than the 3-day topic retention are refused. Track ids become `<device code>/<track id>`. The batches go to `ariva.device.track-sample.v1`, `ariva.device.vendor-line-crossing.v1`, `ariva.device.zone-occupancy.v1` and `ariva.device.interval-count.v1`, keyed by `<site>/<queue zone name>`, with a batch id derived from the device, the kind and a SHA-256 of the body, so a resent body repeats its ids and consumers drop it, while a vendor counter that restarts never hides new data; a health report goes to `ariva.device.health.v1` (keyed by device id) with every status the device sends, or at most every 10 seconds otherwise. Batches from a device still in commissioning say so (`Commissioned` false) and never feed KPIs.

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
- Outsight people counting: https://www.outsight.ai/solutions/people-counting-technologies
- Blickfeld LiDAR software: https://www.blickfeld.com/lidar-software/
