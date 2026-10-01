# Product overview

Ariva measures queues at border control, e-gates, check-in and security with anonymous overhead sensors, combines the measurements with flight data from the airport operational database (AODB) and, at border sites, aggregate feeds from AMAN, and gives control rooms live waits, desk states, alerts, forecasts and, from v1, contract-grade SLA evaluation. This page explains what the product does, for whom, and where its boundaries are.

## What Ariva does

| Step | What happens | Main output |
|---|---|---|
| Measure | Sensors report anonymous track samples or counts. Ariva applies its own versioned zones and lines to them, computes crossings, queue lengths and desk states | Queue length, crossings, desk state, data-quality flag |
| Compute waits | Realised wait per passenger (exit time minus entry time) attributed to the 15-minute bin of entry; nowcast for someone joining now from queue length and staffed-desk throughput | Realised wait per bin (provisional, then final); nowcast |
| Predict | Arrival-wave forecast from on-blocks and lane demand; from v1, show-up forecasts and Monte Carlo waits | Predicted hall arrivals, P50 and P90 waits |
| Decide | Alert rules on nowcasts and forecasts; from v1, staffing recommendations | Alerts with owner role and escalation; recommended desks per interval |
| Inform | Live dashboards, passenger display boards, reports, outbound APIs | Banded nowcast on screens, CSV and (v1) PDF reports |
| Evaluate (v1) | Contracts applied to final bins with exclusions, disputes and evidence packs | Evaluations, penalty notices, sealed evidence packs |

Design rules that shape every feature (Decided):

1. Ariva owns zone geometry. Tracks come in; zones and lines are Ariva's versioned data, so vendors stay swappable and disputed days can be recomputed.
2. Continuity by overlapping coverage, never by re-identification. Nothing biometric enters the system.
3. Two wait numbers: realised wait for reports and penalties, nowcast for screens and alerts.
4. Degrade and flag, never guess silently. Every number carries a data-quality flag (`Good`, `Degraded`, `Unknown`) and the zone profile version it was computed with.
5. Separate border and airport deployments, with a one-way aggregate feed from border to airport. Officer-level data stays in AMAN.
6. On-premises first. Real-time measurement never depends on a WAN.

## Modules

| Capability | Border module | Airport Operations module |
|---|---|---|
| Buyer | Border authority | Airport operator |
| Processes | Arrival and departure immigration, manual desks, e-gates | Check-in islands, security lanes |
| Desk state signals | AMAN desk session and interval statistics, sensor staff and service zones | Common-use check-in (CUPPS) logins and transactions (v1), sensor zones |
| Demand inputs | AODB on-blocks, AMAN lane demand per inbound flight (from API data) | AODB schedules and estimates, SSIM files, counter allocations |
| Tenancy | Single tenant: the border authority | Airport operator plus handler tenants; a handler sees only its own desks, contracts and alerts |
| SLA and penalty engine | Which module licenses it is To confirm | Planned in v1 against handler contracts |
| First phase | MVP | v1 |

Common to both: zones and versioned profiles, realised wait, nowcast, overflow detection, data-quality checks, alerting, supervisor dashboard, passenger display page, reports, Integration API, simulator.

## Deployments

| Deployment kind | Where | Modules | What crosses its boundary |
|---|---|---|---|
| Border | Own namespace and database, in AMAN's cluster or a separate one | Core plus Border | In: AMAN aggregate events. Out: lane-level wait times and KPIs only |
| Airport | The airport operator's environment, on premises or their private cloud | Core plus Airport Operations | In: AODB, signage acknowledgements, the border feed where one exists |
| Combined site | Both of the above, separately | Both | One-way aggregate feed, border to airport, pushed from the border side |
| Small site | Single node, single Kafka broker, single database | Core plus one module | Same rules; reduced availability, accepted in writing |

Hosted SaaS by Dalil is out of scope.

## Roles

Role codes are fixed in `Ariva.Core/RoleCodes.cs` and never renamed once shipped. Visibility below follows the prototype's access model and the data boundary; the authorisation matrix in `security/permission-matrix.json` will be the reference once written (Phase 1 epic Authentication, roles and audit).

| Role (code) | Module and deployment | Sees | Does not see | Creates or decides |
|---|---|---|---|---|
| Border shift supervisor (`BorderShiftSupervisor`) | Border, border deployment | Immigration lanes and desks (interval aggregates), e-gates, arrival-wave strip, border alerts, devices and zones of the immigration halls | Check-in and handler data; any officer identity (officer analytics open in AMAN) | Alert rules and roster overrides for immigration queues; immigration zones and sensors; scheduled reports within its view |
| Terminal duty manager (`TerminalDutyManager`) | Airport Operations, airport deployment | Everything airport-side; border waits as lane-level aggregates only | Border desk-level data, track data, officer identity | Alert rules, roster overrides, zones and sensors airport-side; SLA contracts and exclusions; ad-hoc flights; counter allocations; displays; scheduled reports; decides disputes |
| Handler station manager (`HandlerStationManager`) | Airport Operations, airport deployment (handler tenant) | Its own counters, waits, SLA evaluations and disputes | Other handlers, security, immigration | Roster overrides and counter allocations for its own islands; raises disputes; scheduled reports within its view |
| System administrator (`SystemAdministrator`) | Any deployment | Configuration, users, integrations, audit log | Operational data beyond what configuration needs (To confirm per site) | Users and role grants, integration clients, outbound endpoints, devices, licence. Critical functions require step-up MFA |

A border deployment user cannot hold an airport role, and the reverse; the deployment kind enforces it.

## Screens

The prototype defines the target screens. Phase per screen comes from the roadmap; the Phase 0 demo core delivers a minimal dashboard and the display page only.

| Screen | Shows | Module | Delivered in |
|---|---|---|---|
| Live operations | KPI tiles (longest current wait and its lane, people queuing, desks staffed, e-gates in use and reject rate), live floor plan, wait chart (realised, nowcast, forecast band, target), alert list, arrival-wave strip | Core | Minimal in Phase 0, complete in MVP |
| Alert rules | Rules that drive every alert, with a backtest preview | Core | MVP |
| Immigration | Desk grid with state and per-desk interval aggregates, lane waits, e-gate utilisation and reject categories, extra load on manual desks; a disabled "Officer analytics open in AMAN" link | Border | MVP |
| Passenger display | 16:9 board, banded nowcast per checkpoint, site languages, neutral message on stale data | Core | Phase 0 (page), MVP (languages, stale handling) |
| Devices | Sensor registry, health, clock offset, calibration, outage history | Core | MVP |
| Zones | Floor-plan editor, profile versions, publish | Core | Phase 0 from a configuration file, MVP editor |
| Reports | Daily and weekly reports built around peaks, CSV export; PDF and evidence packs in v1 | Core | MVP (basic), v1 (full) |
| Access and data boundary | Users, roles, what each role sees, audit log | Core | MVP (Phase 1 epic Authentication, roles and audit) |
| Check-in and handlers | Islands by handler, counter state, waits per island, SLA compliance per bin | Airport Operations | v1 |
| SLA and penalties | Contract card, evaluation table, exclusions, disputes, evidence pack | Airport Operations (licensing To confirm) | v1 |
| Forecast and staffing | Demand per lane, recommended desks against the roster, predicted P90 under plan and recommendation | Core | v1 |
| What-if simulation | Planning scenarios | Core | v2 |

Screen details per role are in [User guide](12-User-Guide.md).

## Capability tiers

What Ariva can compute depends on what the device family provides. A family's tier is recorded in the [sensor catalogue](09-Sensor-Catalogue-and-Adapters.md).

| Tier | Data the device provides | What Ariva computes | Typical devices |
|---|---|---|---|
| T1 Counts | Interval counts per counting line (in and out) | Throughput; realised wait from cumulative arrival and departure curves (FIFO assumption, F5); nowcast | Basic overhead counters, camera analytics with line counting, thermal counters |
| T2 Occupancy | T1 plus zone occupancy | Queue length directly; overflow detection; better nowcast | Xovis zone logics, camera 3D counters, LiDAR perception zones |
| T3 Tracks | Anonymous track ids with entry and exit crossings or positions | Per-person realised wait, dwell, desk approach, desk occupancy | Xovis multi-sensor tracking, LiDAR perception platforms |
| T4 On-device KPIs | Device-computed waiting time and queue length | Cross-check against Ariva's own calculation only; never the evidence source for penalties | Xovis queue logics, some LiDAR platforms |

Penalty-grade evaluation (v1) requires T3 data, or T1 data validated against manual counts for that zone profile version.

## What Ariva does not do

| Not in Ariva | Why, and what to use instead |
|---|---|
| Wayfinding | Not a queue KPI; outside the product's scope |
| Retail heat maps and dwell marketing analytics | Not a target use; samples are downsampled to 1 Hz for operational heat maps only |
| Officer analytics (per-officer throughput, rosters, officer-to-desk assignment) | Officer identity never enters Ariva. Officer-level analytics stay in AMAN; the Border dashboard links to AMAN's own reports |
| Raw LiDAR point clouds | Ariva connects to a perception platform (for example Ouster Gemini, Outsight SHIFT, Seoul Robotics SENSR, Blickfeld Percept), never to raw point clouds |
| Images or video | No image leaves a stereo sensor; LiDAR captures none; camera analytics integrations receive events only |
| Re-identification or journeys across processes (curb to gate) | Continuity only by overlapping coverage inside one process; between processes, flows are linked statistically or not at all |
| Wi-Fi or BLE probing, CCTV analytics for queue KPIs | Not used for queue KPIs (CCTV only as a coarse overflow fallback); probe-based travel time is a planned supplementary source only |
| Multi-sensor stitching inside a vendor's system | Done by the vendor's multi-sensor setup (for example Xovis), not by Ariva |
| Ticket-based queuing for service counters | An adjacent market, not terminal queue measurement |
| Penalties against airlines for queue time | Not a product goal |
| Hosted SaaS operated by Dalil | Out of scope; every deployment is in-country, on the customer's infrastructure |

## Related pages

- [Business flow](02-Business-Flow.md) for how these pieces work end to end.
- [KPI and SLA definitions](15-KPI-and-SLA-Definitions.md) for exact computations.
- `../docs/architecture/overview.md` for the full architecture.
