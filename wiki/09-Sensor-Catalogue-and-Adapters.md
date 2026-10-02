# Sensor catalogue and adapters

Ariva supports any people-sensing device or perception platform that emits counts, occupancy, line crossings or tracks over a standard transport. It does this the way AMAN abstracts e-gate vendors: a small set of transports, a dialect mapper per device family, and a capability profile. Adding a family is configuration plus a mapper and a conformance test, not a new subsystem. The reference is `../docs/architecture/sensor-adapters.md`.

Ariva does not consume raw LiDAR point clouds; they go through a perception platform first. That boundary is deliberate: perception is a product category of its own, and every serious LiDAR deployment already runs one.

## Canonical events

Every adapter emits the same events, whatever the vendor sends.

| Event | Fields | Tier |
|---|---|---|
| `TrackPosition` (stored as `TrackSample`) | Track id (ephemeral, namespaced by device), floor x and y, height where available, sensor time | T3 |
| `LineCrossing` | Line id, direction, optional track id, time | T1 to T3 |
| `ZoneOccupancy` | Zone id, count, time | T2 |
| `IntervalCount` | Line id, in, out, from, to | T1 |
| `DeviceStatus` (`DeviceHealth`) | Online, temperature, frame rate, clock offset; every 10 to 30 seconds | All |
| `VendorLineCrossing` | Vendor-computed crossing, kept as a cross-check and fallback | Optional |

Bounds checked on every event before use (`CanonicalEventRules`, ARV-021): times in UTC; coordinates finite and within 2,100 m; heights 0 to 3 m; track ids 1 to 64 letters, digits, dots, hyphens or underscores, namespaced as `<device code>/<track id>`; line and zone names 1 to 200 printable characters as in the zone profile; occupancy 0 to 10,000; interval counts 0 to 100,000 over at most a day; temperature -60 to 150 degrees Celsius; frame rate 0 to 1,000; clock offset at most a day either way.

Pushes (ARV-023): `POST /api/v1/ingest/zones/<zone>/events` for the canonical dialect and `POST /api/v1/ingest/zones/<zone>/xovis` for Xovis firmware 5 JSON, at most 256 KB and 2,000 events each. The answer is 202 with what was accepted, refused, flagged and ignored, the device clock estimate and the first problems; 400 for a malformed message or a device of another dialect; 413 too large; 415 not JSON; 503 when Ariva could not store it (send again). Line and zone names must be those of the published zone profile. Formats, mapping rules and what is verified about Xovis: `../docs/architecture/sensor-adapters.md`.

Track samples arrive at 2 to 5 Hz per track and are downsampled to 1 Hz for heat maps. Ariva computes its own crossings from tracks against its versioned lines (ADR-0003); vendor crossings never replace them where tracks exist.

## Adapter matrix

Status: **Phase 0** means built and tested against recorded or emulated payloads in Phase 0; **Pilot** means certified on hardware before or during the pilot; **Planned** means designed for, built on demand. "Verify" means the protocol must be confirmed against the vendor's current API documentation or SDK before certification.

| Family | Products | Transport into Ariva | Tier | Status |
|---|---|---|---|---|
| Overhead stereo vision | Xovis PC2, PC2 extended coverage, PC3 series (PF series: verify) | `Xovis` dialect over HTTPS push | T1 to T4 | Phase 0 (recorded payloads) |
| | | MQTT over TLS | | Pilot |
| | | REST pull for configuration and health | | Pilot |
| LiDAR perception platforms | Ouster Gemini, Outsight SHIFT, Seoul Robotics SENSR, Blickfeld Percept and Qb2 | REST, WebSocket or MQTT (verify per platform); one coded adapter per platform | T2, T3 (T4 on some platforms) | One platform certified for the pilot, others Planned. Outsight's interface is unverified in the sources |
| Camera 3D and AI counters | Axis 3D People Counter, AXIS Object Analytics | ONVIF Profile M events or Axis HTTP API (verify) | T1, T2 | Planned |
| | Hikvision and Hanwha people counting cameras | ONVIF Profile M or vendor HTTP push (verify) | T1, T2 | Planned |
| | Existing CCTV with an analytics server | Analytics server events over MQTT or HTTPS; Ariva never receives video | T1, T2 | Planned |
| Thermal or time-of-flight counters | Irisys and other ToF overhead counters | Declarative mapper | T1 or T2 | Planned |
| Wi-Fi or BLE flow time | Probe-based travel-time systems | Declarative mapper | Segment travel times only, no queue length | Planned, supplementary only |
| Desk occupancy | AMAN desk session signal; desk presence sensors | AMAN feed; declarative mapper | Desk state input | AMAN signal in Phase 0 |
| Manual counts | Observer tablet app | Capture form in the validation tooling | Validation ground truth | Phase 1 (validation campaign) |
| Simulator | Ariva.Simulation.Api replaying recorded tracks and generating synthetic crowds | Simulator adapter (transport To confirm) | T1 to T3 | Phase 0 |

The roadmap plans one sensor family for the MVP, matched to the pilot hall's ceiling, and a second family (LiDAR perception) in v1; D4 asks for one family of each to be certified before the pilot. The pilot plan will settle which applies.

LiDAR hardware behind the platforms (Ouster OS0, OS1, OSDome and the Velodyne lines, Hesai XT and JT, RoboSense, Livox, Seyond, Blickfeld) is not consumed directly; it is supported if the chosen platform supports it. Until a platform is certified, BOQs assume a 10 m effective tracking radius.

## Transports

| Transport | Direction | Controls |
|---|---|---|
| HTTPS push (webhook) | Device to Ariva.Api.Ingest | Per-device credential (bearer key or Basic), TLS, IP allowlist, 256 KB body limit |
| MQTT(S) | Device to broker; Ingest subscribes | TLS, per-device client certificate or username and key, topic ACL per device |
| REST pull | Ingest polls the device or controller | Only registered device addresses (SSRF control); `If-Modified-Since` or cursor |
| WebSocket stream | Perception platform to an Ingest client | JSON or protobuf frames; reconnect with backoff |
| TCP or UDP stream | Device to listener | Length-prefixed frames, bounds-checked parsing, source address allowlist |
| (S)FTP or FTPS file drop | Device uploads interval files | Polled from a dedicated drop directory; size and name validation |
| ONVIF Profile M metadata | Camera analytics events | For IP cameras with counting or occupancy analytics |
| Canonical push | Sensor gateway to the Integration API (`sensing:write`) | For sites that aggregate sensors behind their own gateway |

Every payload is untrusted: schema validated, size limited, timestamps checked against the device clock offset, ids namespaced by device.

Device authentication (ARV-022): every device push and every device call to Ariva.Api.Ingest carries the credential of the device, as `Authorization: Bearer ardk_...`, as the `X-Ariva-Device-Key` header, or as HTTP Basic with the device code as user name and the credential as password. Nothing else is accepted on those endpoints (a user token is 401), and the credential opens nothing else. A device may be limited to source networks and bound to a client certificate; a device acts only on its own queue zone (403 otherwise). `GET /api/v1/ingest/device` answers who the device is with the UTC clock of the server; `GET /api/v1/ingest/zones/<zone>` answers the published zones and lines of the zone of the device, the names its events must use. Where a vendor parent, gateway or perception server pushes for several sensors, that endpoint is the device that holds the credential (a refinement for gateways is planned with the push endpoints).

## Dialect mappers

| Kind | For | How |
|---|---|---|
| Coded mapper | First-class families (Xovis, the main LiDAR perception platforms) | Typed parsing in Ariva.Infra with full conformance tests |
| Declarative mapper | The long tail | JSONPath or XPath field maps stored with the device family. Expressions are paths, not code: the mapper evaluates a restricted path syntax and never executes scripts (CWE-94 control) |

A declarative mapper maps each canonical field to a path in the vendor payload. Illustrative only; the mapper format is defined in Phase 0 epic Device gateway and simulator:

| Canonical field | Example vendor path |
|---|---|
| `IntervalCount.lineId` | `$.counts[*].line` |
| `IntervalCount.in` | `$.counts[*].fw` |
| `IntervalCount.out` | `$.counts[*].bw` |
| `IntervalCount.fromUtc` | `$.from` |
| `IntervalCount.toUtc` | `$.to` |

## Adding a new device family

1. **Collect the vendor's material**: API documentation or SDK, sample payloads, firmware versions, the transport options, the coverage table, and whether the vendor allows data access by a third-party software vendor.
2. **Classify the tier** (T1 to T4) from what the device can output.
3. **Choose the transport** from the table above. Prefer TLS transports with per-device credentials.
4. **Write the mapper**: declarative for simple count or occupancy payloads; coded for tracks or binary formats.
5. **Record payloads** from the device (or vendor samples) into `Platform/Simulation/Ariva.Simulation.Api/Emulators/Sensors/<Family>/samples/`.
6. **Write the expected canonical events** next to the samples.
7. **Run the conformance test**: it replays the samples through the adapter and compares the output, including malformed and oversized payloads (CWE-120, CWE-501).
8. **Validate in the field** against manual counts on site; the accuracy figure goes into the calibration record.
9. **Certify the family for a firmware range** and update the catalogue (this page and `sensor-adapters.md`).
10. **Add the family to the device registry** so administrators can register its devices.

## Certification levels

The catalogue statuses map to the levels below. The level names are Proposed; the content of each level comes from the conformance kit and the calibration rules.

| Level | Catalogue status | Meaning | Evidence |
|---|---|---|---|
| 0 Designed for | Planned | Transport and dialect identified; no adapter yet | Vendor documentation |
| 1 Conformant | Phase 0 | Adapter passes the conformance kit on recorded or emulated payloads | Conformance test results in CI |
| 2 Field certified | Pilot | Hardware tested on site against manual counts, certified for a firmware range | Field validation report, calibration records |
| 3 Penalty grade, per zone | (per zone profile version) | A zone's data is accepted as evidence for SLA penalties: T3 data, or T1 data validated against manual counts for that zone profile version | Validation report for that profile version |

T4 on-device KPIs are a cross-check only at every level; they are never the evidence source for penalties.

## Device lifecycle

| Status | Meaning | Next |
|---|---|---|
| `Commissioning` | Registered, not yet calibrated | `Online` after a passed calibration (at least 95 percent counting accuracy by default) |
| `Online` | Calibrated and healthy | `Degraded` or `Offline` on health problems; `Commissioning` after a move or replacement |
| `Degraded` | Running with a quality problem (frame rate drop, unstable clock) | `Online` when resolved |
| `Offline` | Heartbeat lost; its zones are `Degraded` and waits are shown as bands | `Online` when health checks confirm recovery |
| `Retired` | Removed from service | |

## Vendor sources

- Xovis selection guide (metric, V1.8): https://api.xovis.com/fileadmin/user_upload/file/ressources/Brochures/Xovis-SelectionGuide-Metric-EN-V1.8.pdf
- Xovis model list (third-party copy of the selection guide): https://www.stromquist.com/customer/docs/Misc/Xovis-selection-guide.pdf
- Xovis PC3 technical datasheet: https://api.xovis.com/fileadmin/user_upload/file/ressources/PC3_Technical_Datasheet.pdf
- Example of Xovis data push to a third-party platform: https://docs.evolution.cloud.mrisoftware.com/IoTHubHelp/Content/2ConnectivityIntegrationLayer/IntegrationVendors/Xovis/Xovis.htm
- Ouster Gemini: https://ouster.com/products/software/gemini
- Outsight people counting: https://www.outsight.ai/solutions/people-counting-technologies
- Blickfeld LiDAR software: https://www.blickfeld.com/lidar-software/
