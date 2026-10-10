# Ariva glossary (ubiquitous language)

Use these words in code, APIs, UI text, tickets and conversation. One concept, one name. C# names are canonical for Ariva.Core; database columns use the snake_case form of the same name; Kafka topics use the kebab-case form.

Spelling follows the design documents (British English): `Realised`, `Finalised`, `Organisation`, `Licence`. Times are `DateTimeOffset` in UTC taken from the sensor or source clock (`OccurredAt`), never ingestion time (`ReceivedAt`) unless stated. Durations are `TimeSpan`. Rates are passengers per minute (`double`).

Items marked (Proposed) are naming proposals made in this repository where the sources give the concept but no name; items marked (To confirm) need a product owner decision.

## Core terms

| Term | Definition | Canonical C# name |
|---|---|---|
| Zone | A polygon in floor coordinates (metres, local metric system of a floor plan) with one role in a process. | `Zone`, `ZoneId`, `ZoneKind { Queue, Service, Staff, Overflow }` |
| Queue zone | The zone where people wait, bounded by entry and exit lines. It owns a process: its overflow, service and staff zones hang off it and share its Kafka key. | `ZoneKind.Queue`; owning id `QueueZoneId` |
| Service zone | The area in front of a desk where a passenger is served. Occupancy is a weak serving signal (rank 4). | `ZoneKind.Service` |
| Staff zone | The area behind a desk where the officer or agent sits. Occupancy proves presence (rank 3). | `ZoneKind.Staff` |
| Zone profile | The complete, signed set of zones, lines, desks and lanes for a site, as one immutable version. Switched by time or by a supervisor (stanchions move). Every result records the profile version it was computed with. Drafts change; publishing validates (formulas F22), numbers the version and fixes the geometry hash; published versions never change. | `ZoneProfile`, `ZoneProfileId`, `ZoneProfileStatus { Draft, Published, Retired }`, `ZoneProfile.GeometryHash` |
| Zone profile version | The monotonically increasing version number of a published zone profile. A change is a new version, never an edit. | `ZoneProfile.Version` (int); on results `ZoneProfileVersion` |
| Zone profile activation | The rule or action that makes a profile version current from a given time. | `ZoneProfileActivation`; events `ZoneProfilePublished`, `ZoneProfileActivated` |
| Snake queue | A first-in, first-out serpentine queue formed by stanchions, feeding a bank of desks or gates. Modelled as one queue zone with entry lines and an exit line. | `ZoneKind.Queue` with `QueueDiscipline.Fifo` (Proposed) |
| Entry line | The line whose inward crossing starts a passenger's wait. A queue may have several (one per entrance); the first one crossed counts. It lies on an edge of the queue zone. | `Line`, `LineRole.Entry` |
| Exit line | The line whose outward crossing ends a passenger's wait, at the head of the queue before the desks. Must be at least 1 m inside coverage. | `LineRole.Exit` |
| Count line | A line that only counts crossings (flow), with no wait attached. Used for flows between processes and for validation. | `LineRole.Count` |
| Overflow band | The area outside the snake where the queue spills over when the snake is full. It has its own entry line; overflow time counts as wait. Occupancy above zero raises `OverflowDetected` (decided per closed minute, once when the band becomes occupied and once when it empties; and once when its sensor stays silent beyond the 2-minute occupancy freshness window, when the band becomes Unknown until it reports again; ARV-115). | `ZoneKind.Overflow`, `LineRole.OverflowEntry`; event `OverflowDetected` |
| Track | The anonymous trajectory of one person within continuous coverage, from a sensor or fused across overlapping sensors. Its id is short-lived, rotated at zone exit, never persists past the operating day, and has no link to identity. | `TrackId` (value object); samples `TrackSample` |
| Track sample | One position of one track at one sensor time: floor x and y, height where available. | `TrackSample { SiteId, DeviceId, TrackId, X, Y, Height?, OccurredAt }` |
| Crossing | The moment a track crosses a line in a direction, at sensor time, interpolated between samples. | `LineCrossing { ZoneId, LineId, TrackId, Direction, OccurredAt }`, `CrossingDirection { In, Out }`; events `PassengerEnteredZone`, `PassengerExitedZone` |
| Occupancy | The number of tracks inside a zone at an instant. For a queue zone it is the queue length. | `Occupancy` (int); queue zones `QueueLength` |
| Realised wait | For one passenger: exit-line crossing time minus first entry-line crossing time. Exact but known only after exit. Used for reports and penalties. | `RealisedWait` (TimeSpan) |
| Nowcast | The predicted wait for someone joining now: (queue length + 1) / throughput. Immediate but modelled. Used for screens and alerts, never for penalties. | `NowcastWait` (TimeSpan?); event `NowcastUpdated` |
| Provisional | Status of a bin that may still change: the bin is open, or someone who entered in it has not exited or been resolved, or a late event may arrive. | `BinStatus.Provisional` |
| Final | Status of a bin once everyone who entered in it has exited or been resolved and the watermark has passed. Only final bins feed SLA evaluation. | `BinStatus.Final` |
| Bin | A fixed time interval to which waits are attributed by entry time. Default size 15 minutes; per-minute bins are used for cohorts and charts. | `QueueInterval` (aggregate), `BinStart`, `BinSize`; event `QueueIntervalClosed` |
| Revision | The version of a bin's result. Late events and recomputations create a new revision; earlier revisions are kept. | `Revision` (int) |
| Data quality | Flag on every measured number. | `DataQuality { Good, Degraded, Unknown }` |
| Desk | A service point: immigration or emigration desk, e-gate, check-in counter or security lane. E-gates are desks with a reject path. A checkpoint's kind decides its desk kinds (check-in: counters; security: lanes; emigration and immigration: desks and e-gates). | `Desk`, `DeskId`, `DeskKind { Counter, SecurityLane, Desk, EGate }` (ARV-013) |
| Desk state | The state machine of one desk, fed by signals in strict precedence (transaction, login, staff zone, service zone). | `DeskState` (aggregate); event `DeskStateChanged` |
| Desk state values | `Closed`: not staffed (logout, or staff absent beyond T2). `Idle`: staffed, not serving. `Serving`: a transaction or passenger at the desk. `Paused`: staffed but no activity for T1 (counts as not open for throughput). `Unknown`: all signals silent; excluded from throughput, bins flagged. | `DeskStatus { Closed, Idle, Serving, Paused, Unknown }`. The prototype also shows `OutOfService` for e-gates under maintenance (To confirm) |
| Lane | A set of desks serving one or more lane categories from one queue. | `Lane`, `LaneId` |
| Lane category | The class of traveller a lane serves, set by eligibility rules. Site-configurable codes of 2 to 4 upper case letters; a border desk serves at least one, an e-gate always includes EG, counters and security lanes have none. Reference codes in the simulation: `CRW` crew and diplomats, `CIT` citizens, `RES` residents, `VIS` visitors, `EG` e-gate eligible; fast track or priority where present. AMAN's own lane categories map onto these (To confirm). | `LaneCategory` (codes), `Desk.LaneCategories`; event `LaneCategoryChanged` |
| E-gate reject coupling | Every e-gate reject becomes an arrival in a manual lane, so manual-lane demand = its own arrivals + reject rate x e-gate flow. Effective e-gate throughput is gates / cycle time x (1 - reject rate). | `EGateRejectRate` (double, 0 to 1); `RejectTargetLaneId` on the e-gate lane (Proposed) |
| Show-up curve | The distribution of how long before departure (STD) a passenger reaches a departure process. Learned per segment (destination region, time of day, weekday, season, airline). | `ShowUpCurve`, keyed by `ShowUpSegment` (Proposed) |
| Arrival wave | The burst of arriving passengers reaching the immigration hall after flights go on-block. The sum over the next 5 to 25 minutes is the arrival-wave alert. | `ArrivalWave`; event `ArrivalWaveForecast` |
| Backtest | An alert rule evaluated on the stored minutes of a past range: the alerts it would have raised. The same fold as the live evaluation, so it equals it from the same start | `AlertEvaluator.Run`; `POST api/v1/admin/alert-rules/backtest` |
| Predicted breach | An alert from the nowcast projected 15 to 60 minutes ahead (the highest within the lead time) from the queue now, the arrival wave and the current throughput | `AlertMetric.PredictedNowcast`, `PredictedWait.Peak` |
| Calibration | Fixing a sensor's position and orientation in the floor coordinate system, recorded with date, method and residual error; triggers re-validation for penalty-grade zones. | `CalibrationRecord`; event `DeviceCalibrated` |
| Coverage footprint | The rectangle a downward-facing sensor tracks at the tracking plane, length by width in metres. Vendor tables are authoritative. | `CoverageFootprint { LengthM, WidthM }` |
| Evidence pack | The sealed bundle behind an SLA evaluation: interval data, zone profile version, calibration record, exclusions, disputes and quality flags, with a SHA-256 content hash. | `EvidencePack`, `EvidencePack.ContentHash` |
| Exclusion | An event that removes bins from SLA evaluation, of a type the contract allows. | `Exclusion`, `ExclusionType { SecurityDirective, SensorOutage, ClosedOnAirportInstruction, FlightDisruption, UpheldDispute }` (types from the prototype; per contract, To confirm) |
| Dispute | A challenge by the contract party against a final breached bin, within the dispute window. While open the bin is held (not counted); upheld excludes it, rejected counts it. | `Dispute`, `DisputeStatus { Raised, UnderReview, Upheld, Rejected }`; events `DisputeRaised`, `DisputeResolved` |

## Supporting terms

| Term | Definition | Canonical C# name |
|---|---|---|
| Airport | An airport of the deployment, identified by its IATA code, with an IANA time zone for local days and schedules. | `Airport { IataCode, IcaoCode, Name, TimeZoneId }` |
| Terminal | A terminal of an airport; it belongs to one site, which every level, checkpoint and desk under it carries. | `Terminal { Code, SiteCode }` |
| Level | A floor of a terminal with its extent in metres; the floor coordinate system runs from (0, 0) to (width, depth). | `Level { Code, FloorNumber, WidthMetres, DepthMetres }` |
| Checkpoint | A process point on a level. | `Checkpoint`, `CheckpointKind { CheckIn, Security, Emigration, Immigration }` |
| Topology code | Codes of terminals, levels, checkpoints and desks: 1 to 16 upper case letters or digits with single inner hyphens, unique among the live children of one parent, never changed. | `TopologyCodes` |
| Site | A terminal (or group of terminals) within an airport deployment, with its own configuration, and the unit of data access: users are bound to sites (ARV-012). | `Site { Code, Name }`, `SiteCode` (upper case, for example `AMM`, `AUH-T1`), `UserSite`, `ISiteBound` |
| Floor plan | The plan image (PNG, JPEG or sanitised SVG) of a level, placed in the floor coordinate system by a scale in metres per pixel and the floor position of the image's top left corner; image axes are taken as parallel to the floor axes (a rotated drawing is straightened before upload). Two-point alignment with rotation is a later option. | `FloorPlan { StorageKey, ContentType, Sha256, MetresPerPixel, OriginX, OriginY }` |
| Device | A sensor or other measuring device, registered like an AMAN e-gate. | `Device`, `DeviceKind { StereoSensor, LidarSensor, Simulator }` (Proposed) |
| Adapter | The code that connects one sensor family or perception platform (not one LiDAR brand) behind the common interface: connect, subscribe, health, normalise. | `ISensorAdapter` (Proposed), `AdapterConfig` |
| Device gateway | Ariva.Api.Ingest deployed per terminal on the sensor VLAN: adapters, MQTT, clock checks, disk buffer. | Host `Ariva.Api.Ingest` |
| Heartbeat | Periodic health from a device or feed; its absence is the most common failure signal. | `DeviceHealth`; events `DeviceHeartbeatLost`, `DeviceRecovered` |
| Watermark | The event time up to which a partition is considered complete; a bin closes when the watermark passes its end plus the lateness allowance. | `Watermark`, `LatenessAllowance` |
| Censored track | A track that entered but has neither exited nor left another way by the censoring timeout. Excluded from realised wait, counted separately. | `TrackOutcome.Censored` (Proposed) |
| Track outcome | How a track that entered a queue ended. | `TrackOutcome { Exited, Abandoned, Fragmented, Censored }` (Proposed) |
| Abandonment | A track that leaves the queue without crossing the exit line; reported as an abandonment rate. | `TrackOutcome.Abandoned` |
| Fragmentation | A track lost and restarted; biases waits low. Measured by the track completion rate. | `TrackOutcome.Fragmented`, `TrackCompletionRate` |
| Conservation residual | Entries minus exits minus change in occupancy over an interval; a live estimate of count error. | `ConservationResidual` |
| Shadow nowcast | The nowcast without AMAN inputs, computed beside the published one every live minute for the ground-truth proof; never shown, alerted on or reported (ARV-117, F8). | `ShadowNowcast`; as a stored row `ShadowMinuteRow` (Proposed) |
| Dominant desk state | The state a desk spent most of a stored minute in; seconds not accounted for count as Unknown, and Unknown wins any tie (F18). | `DeskStateAgreement.Dominant` (Proposed) |
| Desk-state agreement | Observed minutes in which the dominant desk state equals the state the observer recorded, over observed minutes; Unknown counts as disagreement (F18). | `DeskStateAgreement`, `DeskMinuteAgreement`, `DeskAgreementSummary` (Proposed) |
| Nowcast error | A minute's nowcast minus the final mean realised wait of the people who entered in the next minute; judged as the median absolute error under a 20-minute cut (F18). | `NowcastErrors`, `NowcastMinuteError`, `NowcastZoneErrors` (Proposed) |
| Service time | Per passenger: transaction start to end (AMAN) or service-zone entry to exit (sensors). | `ServiceTime` |
| Cycle time | Per desk: time between successive service starts, including the walk-up gap. Sets throughput. | `CycleTime` |
| Approach | One group arriving at a desk together (a family of four is one approach and four documents). | `Approach`; counts `Approaches`, `Documents` |
| Throughput | Passengers served per minute by a lane or zone. | `ThroughputPerMinute` |
| Merge ratio | The standard lane's share of desks shared with a fast-track lane. | `MergeRatio` |
| Capacity | Passengers a lane can serve in an interval from staffed desks and cycle times. | `Capacity` |
| Backlog | Passengers still waiting at the end of an interval in the forecast recursion. | `Backlog` |
| Forecast run | One execution of the forecast with its model version and input snapshot. | `ForecastRun`; event `ForecastPublished` |
| Staffing plan | Desks per lane per interval, recommended and accepted. | `StaffingPlan`; events `StaffingRecommendationIssued`, `StaffingPlanAccepted` |
| Contract | The agreement evaluated for SLA and penalties: party, KPI definitions, thresholds, windows, exclusions, penalty schedule. | `Contract`, `KpiDefinition`, `PenaltySchedule` |
| Allowance | Breached bins per evaluation window that carry no penalty. | `PenaltySchedule.Allowance` |
| Evaluation | The result of applying a contract to final bins in a window. | `Evaluation`; events `SlaBreachDetected`, `EvaluationFinalised`, `PenaltyAssessed` |
| Alert | A raised condition with an owner role and an escalation path. | `Alert`, `AlertStatus { Raised, Acknowledged, Escalated, Resolved }`, `AlertRule`, `EscalationPolicy` |
| Module | A separately licensed part of Ariva. | `ModuleCode { Border, AirportOperations }` |
| Deployment kind | Where an Ariva instance runs and what may cross its boundary. | `DeploymentKind { Border, Airport, Small }`; a combined site is two deployments |
| Organisation | A party within a deployment: airport operator, handler, security contractor, border authority. | `Organisation`, `OrganisationId`, `TenantId` |
| Flight leg | One flight at a site in one direction, keyed by the feed's stable flight key; its schedule and each milestone keep the time of the message that set them, so feeds can arrive out of order. | `FlightLeg { SiteCode, FlightKey, Direction, Status }`, `FlightDirection { Arrival, Departure }`, `FlightStatus` (ARV-041) |
| Flight event | A milestone a feed reports for a known leg (estimate, landed, on-block, gate open, boarding, off-block, cancelled, diverted), kept as a record whether or not it changed the leg. | `FlightEvent`, `FlightEventType` (ARV-041) |
| Counter allocation | The check-in counters a departing leg has at one check-in checkpoint; AODB counter codes resolve through desk code mappings, the rest are kept apart. | `CounterAllocation { DeskCodes, UnresolvedCodes }` (ARV-041) |
| Feed freshness | When a flight feed of a site was last heard from, and whether it is Fresh, Idle (silent with no flight due) or Stale (silent beyond its cadence while flights are due: the stale-feed alarm). | `FeedFreshness`, `FeedState`, `FeedFreshnessRule` (ARV-041) |
| Desk code mapping | The mapping from an AMAN desk or gate code, or an AODB counter code, to an Ariva desk at a site; unique per system and site. | `DeskCodeMapping { System, SiteCode, ExternalCode, Desk }`, `ExternalSystem { Aman, Aodb }` (ARV-015) |
| AMAN feed | The four aggregate-only contracts V1 from AMAN. | `DeskSessionChanged`, `DeskIntervalStats`, `EGateIntervalStats`, `InboundFlightLaneDemand` in `Ariva.Business.Contracts` |
| Border-to-airport feed | Lane-level wait times and KPIs pushed one way from a border deployment to an airport deployment. | `BorderLaneKpi` (Proposed) |
| Display channel | One passenger screen or group of screens showing one or more queues. | `DisplayChannel` |
| Outbox | The table where an event raised by an aggregate is written in the same transaction as the change; a relay produces it to Kafka afterwards, in order per key (ADR-0018). | `outbox_message`, `OutboxRelay` |
| Inbox | The record that a consumer applied an event, written in the consumer's transaction; a redelivered event finds it and is skipped. | `processed_event`, `InboxFilter` |
| Dead letter | A message a consumer could not apply after its retries, kept on `<topic>.dlq.v1` with where it came from and the error, so the partition moves on and nothing is lost. | `DeadLetter`, `KafkaTopics.DeadLetter` |

## Words to avoid

| Avoid | Use instead | Why |
|---|---|---|
| "Open" for a desk | `Idle`, `Serving`, `Paused` or `Closed` | "Open" collapses four states that drive different numbers |
| "Wait time" without qualifier | `RealisedWait` or `NowcastWait` | They differ in timing, method and use |
| "Passenger id", "person id" | `TrackId` | Tracks are anonymous and ephemeral |
| "Officer" anywhere in Ariva data | Desk, lane | Officer identity never enters Ariva |
| "QMS" in code | Ariva | Working name only (ADR-0015) |
