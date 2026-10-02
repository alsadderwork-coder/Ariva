# Glossary

A short version of Ariva's ubiquitous language. The authoritative glossary, with canonical C# names, is `../docs/domain/glossary.md`: one concept, one name, used in code, APIs, screens, tickets and conversation. Database columns use the snake_case form of the same name and Kafka topics the kebab-case form. Spelling is British (realised, finalised, organisation, licence).

## Core terms

| Term | Meaning |
|---|---|
| Zone | A polygon in floor coordinates (metres) with one role: queue, service, staff or overflow |
| Queue zone | Where people wait, bounded by entry and exit lines. It owns a process; its overflow, service and staff zones share its Kafka key |
| Service zone | The area in front of a desk where a passenger is served; occupancy is a weak serving signal |
| Staff zone | The area behind a desk; occupancy proves someone is present |
| Overflow band | The area outside the snake where the queue spills over; overflow time counts as wait |
| Snake queue | A first-in, first-out serpentine queue formed by stanchions, feeding a bank of desks or gates |
| Entry line | Its inward crossing starts a passenger's wait; the first entry line crossed counts |
| Exit line | Its outward crossing ends the wait, at the head of the queue; at least 1 m inside coverage |
| Count line | Counts flow only, with no wait attached |
| Zone profile | The complete set of zones, lines, desks and lanes for a site as one immutable, signed version |
| Zone profile version | The increasing number of a published profile; every result records it |
| Zone profile activation | The rule or action that makes a version current from a given time |
| Track | The anonymous trajectory of one person inside continuous coverage; its id is rotated at zone exit and never outlives the operating day |
| Track sample | One position of one track at one sensor time |
| Crossing | The moment a track crosses a line in a direction, interpolated between samples |
| Occupancy | The number of tracks inside a zone; for a queue zone, the queue length |
| Realised wait | Exit crossing time minus first entry crossing time. Exact, late; used for reports and penalties |
| Nowcast | (Queue length + 1) / throughput: the wait for someone joining now. Immediate, modelled; used for screens and alerts |
| Bin | A fixed interval (15 minutes by default) to which waits are attributed by entry time |
| Provisional | A bin that may still change |
| Final | A bin whose entrants have all exited or been resolved after the watermark; only final bins feed SLA evaluation |
| Revision | A version of a bin's result; late events and recomputations create new revisions and keep the old ones |
| Data quality | `Good`, `Degraded` or `Unknown` on every number |
| Desk | A service point: immigration desk, e-gate, check-in counter or security lane |
| Desk state | `Closed`, `Idle`, `Serving`, `Paused` or `Unknown`, from signals in strict precedence (transaction, login, staff zone, service zone) |
| Lane | A set of desks serving one or more lane categories from one queue |
| Lane category | The class of traveller a lane serves; reference codes `CRW` (crew and diplomats), `CIT` (citizens), `RES` (residents), `VIS` (visitors), `EG` (e-gate eligible) |
| E-gate reject coupling | Every e-gate reject becomes an arrival in a manual lane |
| Show-up curve | How long before departure passengers reach a departure process, learned per segment |
| Arrival wave | The burst of arriving passengers reaching the immigration hall after flights go on-block |
| Backtest | A rule evaluated on stored minutes of a past range, to see how often and when it would have fired; the same evaluation as the live alerts |
| Predicted breach | An alert raised before a queue passes its threshold, from the nowcast projected over the next 15 to 60 minutes with the arrival wave |
| Calibration | Fixing a sensor's position and orientation and proving its counting accuracy; recorded as a calibration record |
| Coverage footprint | The rectangle a downward-facing sensor tracks at the tracking plane; vendor tables are authoritative |
| Evidence pack | The sealed bundle behind an SLA evaluation, with a SHA-256 content hash |
| Exclusion | An event that removes bins from SLA evaluation, of a type the contract allows |
| Dispute | A challenge to a final breached bin: `Raised`, `UnderReview`, `Upheld` or `Rejected` |

## Supporting terms

| Term | Meaning |
|---|---|
| Site | A terminal or group of terminals within a deployment, with its own configuration |
| Floor plan | The plan image of a level (PNG, JPEG or cleaned SVG), placed on the floor by a scale and the position of its top left corner |
| Device | A sensor or other measuring device, registered like an AMAN e-gate |
| Adapter | The code that connects one sensor family or perception platform behind the common interface |
| Device gateway | Ariva.Api.Ingest deployed per terminal on the sensor VLAN |
| Heartbeat | Periodic health from a device or feed; its absence is the most common failure signal |
| Watermark | The event time up to which a partition is considered complete |
| Censored track | A track still unresolved after the censoring timeout; excluded from realised wait |
| Track outcome | `Exited`, `Abandoned`, `Fragmented` or `Censored` |
| Conservation residual | Entries minus exits minus the change in occupancy; should be 0 |
| Service time | Per passenger: transaction start to end, or service-zone entry to exit |
| Cycle time | Per desk: time between successive service starts, including the walk-up gap; sets throughput |
| Approach | One group arriving at a desk together (a family of four is one approach and four documents) |
| Throughput | Passengers served per minute by a lane or zone |
| Merge ratio | The standard lane's share of desks shared with a fast-track lane |
| Capacity | Passengers a lane can serve in an interval |
| Backlog | Passengers still waiting at the end of an interval in the forecast recursion |
| Forecast run | One execution of the forecast with its model version and input snapshot |
| Staffing plan | Desks per lane per interval, recommended and accepted |
| Contract | The agreement evaluated for SLA and penalties: party, KPIs, thresholds, windows, exclusions, penalty schedule |
| Allowance | Breached bins per evaluation window that carry no penalty |
| Evaluation | The result of applying a contract to final bins in a window |
| Alert | A raised condition with an owner role and an escalation path: `Raised`, `Acknowledged`, `Escalated`, `Resolved` |
| Alert rule | Typed data that says when to raise an alert: metric, comparator, threshold, sustain and clear minutes, severity, owner role and escalation; coded R-001, R-002 and so on per site, never an expression |
| Module | A separately licensed part of Ariva: Border or Airport Operations |
| Deployment kind | Border, Airport or Small; a combined site is two deployments |
| Organisation | A party within a deployment: airport operator, handler, security contractor, border authority |
| Desk code mapping | Maps an AMAN desk or gate code to an Ariva desk at a site |
| AMAN feed | The four aggregate-only contracts V1 from AMAN |
| Border-to-airport feed | Lane-level wait times and KPIs pushed one way from a border deployment to an airport deployment |
| Display channel | One passenger screen or group of screens showing one or more queues |

## Words to avoid

| Avoid | Use instead | Why |
|---|---|---|
| "Open" for a desk | Idle, serving, paused or closed | "Open" collapses four states that drive different numbers |
| "Wait time" without a qualifier | Realised wait or nowcast | They differ in timing, method and use |
| "Passenger id", "person id" | Track id | Tracks are anonymous and ephemeral |
| "Officer" anywhere in Ariva data | Desk, lane | Officer identity never enters Ariva |
| "QMS" in code | Ariva | "Airport QMS" was the working name in the design documents |

## Abbreviations

| Abbreviation | Meaning |
|---|---|
| ACRIS | ACI Aviation Community Recommended Information Services (API standards) |
| AIDX | IATA Aviation Information Data Exchange (flight data XML standard) |
| AMAN | Dalil Tech's e-border and border control platform |
| AODB | Airport operational database |
| API (passenger data) | Advance passenger information |
| BOQ | Bill of quantities |
| CUPPS | Common-use passenger processing systems (shared check-in) |
| DPIA | Data protection impact assessment |
| FIDS | Flight information display system |
| KRaft | Kafka's built-in consensus mode (no ZooKeeper) |
| PoE | Power over Ethernet |
| PTP | Precision Time Protocol (IEEE 1588) |
| SSIM | IATA Standard Schedules Information Manual (schedule file format) |
| STD | Scheduled time of departure |
| TOTP | Time-based one-time password (RFC 6238) |
