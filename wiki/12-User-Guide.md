# User guide

For supervisors, duty managers and handler staff who use Ariva during operations. It explains how to read the numbers, then each screen with what it shows and the common tasks, and the phase that delivers it. Screen descriptions follow the clickable prototype; the product may differ in layout.

Status: in Phase 0 only a minimal live operations dashboard and the passenger display page exist (on simulated data). The MVP (pilot) adds the full border screens; v1 adds the airport and SLA screens.

## Reading the numbers

| You see | It means |
|---|---|
| Nowcast | The predicted wait for someone joining the queue now: (people queuing + 1) divided by current throughput. Immediate but modelled. Used on screens and in alerts, never for penalties |
| Realised wait | The measured wait of people who have already left the queue (exit time minus entry time), attributed to the 15-minute bin in which they joined. Exact but late. Used in reports and SLA evaluation |
| Provisional | The bin's result may still change: someone who joined in it is still queuing, or late data may arrive |
| Final | Everyone who joined in the bin has left or been resolved; only final bins count for SLAs |
| `Good` | All sensors healthy, all desks known, clocks within limits |
| `Degraded` | A sensor degraded, a desk state unknown, a feed stale, or similar. Waits are shown as a band, not a single number |
| `Unknown` | No usable measurement (for example an entry or exit line without coverage). Screens show a neutral message |
| No service | No staffed desk: the nowcast is undefined and screens show a neutral message, never zero or infinity |
| Profile version (for example v12) | The zone configuration the number was computed with |
| Desk states | `Closed` (not staffed), `Idle` (staffed, not serving), `Serving`, `Paused` (staffed but inactive for a while; does not count as open), `Unknown` (all signals silent). E-gates may also show out of service (To confirm) |

Words to avoid: "open" for a desk (say idle, serving, paused or closed) and "wait time" without saying nowcast or realised.

## Screens by role

| Screen | Border shift supervisor | Terminal duty manager | Handler station manager | Phase |
|---|---|---|---|---|
| Live operations | Immigration zones with desk and e-gate states, border alerts, arrival wave by lane | Check-in, security and reclaim; each immigration hall as one zone with its longest lane wait (no desks); arrival wave with flight totals only | Own islands with counter states and own alerts; no arrival wave | Minimal in Phase 0, full in MVP |
| Alert rules | Rules on immigration queues and sensors; creates and changes them | Rules on airport-side queues and sensors; creates and changes them | Rules covering own islands, read only | MVP |
| Immigration | Lane waits, desk grid with per-desk interval aggregates, e-gates | Lane waits and queue lengths only (aggregates) | Not available | MVP |
| Check-in and handlers | Not available | All handlers and counters | Own islands only | v1 |
| SLA and penalties | Not available | Contracts, evaluation, exclusions, dispute decisions, evidence pack | Own contract, evaluation, raises disputes, evidence pack | v1 |
| Forecast and staffing | Immigration demand by lane and desk recommendation | Check-in and security | Own check-in only | v1 |
| Zones | Edits immigration zones | Edits check-in and security zones | Not available | Phase 0 from a file, MVP editor |
| Devices | Sensors over the immigration halls | Sensors over check-in and security | Not available | MVP |
| Passenger display | The public board | The public board; manages displays | The public board | Phase 0 page, MVP languages and stale handling |
| Reports | Border report and CSV of immigration intervals | Airport report with border lane waits as aggregates; CSV without desk data | Own section and CSV of own islands | MVP basic, v1 full |
| Access and data boundary | Own rights, border users, border audit log | Own rights, airport and handler users, airport audit log | Own rights, own organisation's users and audit entries | MVP |

No screen ever shows officer identities.

## Live operations

Phase 0 (minimal), MVP (complete).

Shows:

- KPI tiles: the longest current wait and its lane, people queuing, desks staffed of total, e-gates in use and reject rate.
- The floor plan with zones coloured by nowcast and desks by state; hover for details.
- A wait chart: the last two hours of realised wait, the current nowcast, and (v1) the next two hours of forecast as a P50 line with a P90 band, against a dashed target line (15 minutes in the reference setup).
- The alert list: severity, rule, owner role, time, an acknowledge button and the escalation countdown.
- The arrival-wave strip (border): flights landing in the next 30 minutes with passengers by lane and the predicted hall arrival curve.

Common tasks:

1. Acknowledge an alert you own before its escalation timer runs out; then act (open desks, redirect passengers).
2. When a zone shows a band or a neutral message, check the Devices screen and tell the site administrator.
3. Use the arrival-wave strip to open desks before a wave reaches the hall.

### Alerts (ARV-039)

You see the alerts of your sites that your role is responsible for: the rule's owner role, the escalation role once an alert has been escalated, and every role for a rule without an owner (administrators see all). An alert moves forward only:

| From | Action | To | Who |
|---|---|---|---|
| Raised | Acknowledge (optional note) | Acknowledged | The responsible role, once per alert |
| Raised or Acknowledged | Escalate (optional note) | Escalated | The responsible role, once per alert; also automatic when an alert stays Raised for the rule's escalation minutes |
| Escalated | Acknowledge | Acknowledged | The owner or the escalation role, if no one acknowledged it before the escalation |
| Any open state | Resolve (note required) | Resolved | The responsible role |
| Any open state | Clears by itself | Resolved | When the rule's clear condition has held for its clear minutes |

An action on an alert that has already moved on answers that it is not in a state for that action. Notes are up to 500 characters and are shown as plain text. A screen that has joined the site's alerts on the live hub is told of each change, for the alerts its role is responsible for. Every acknowledgement, escalation and manual resolution is in the audit log.

API (`api/v1/alerts`): `GET ?siteCode=&state=&open=&zoneName=&ruleCode=&fromUtc=&toUtc=` (newest first), `GET {id}` (with `escalationDueUtc` while it can still escalate by itself), `POST {id}/acknowledge`, `POST {id}/escalate` and `POST {id}/resolve` with `{note}`. Live hub: `JoinAlerts(siteCode)` and `LeaveAlerts(siteCode)`; notices arrive on `alert` (what changed and its state, no names of people).

## Alert rules

MVP. Shows the rules that drive every alert, with their status.

Common tasks:

1. Create a rule: name, scope, metric, condition and threshold, sustain time, severity, owner role, escalation, channels. Check the backtest preview ("Would have fired 3 times today, first at 18:05") before saving: it is the same evaluation the live alerts come from, on the stored minutes of the range you choose (up to a day).
2. For an early warning, choose the predicted nowcast and a lead time of 15 to 60 minutes: the rule fires when the queue is projected to pass the threshold within that time, from the arrival wave of landing flights (available once the flight feed is connected).
3. Enable, disable or duplicate a rule.

You can only create rules for queues your role can see.

## Immigration (Border module)

MVP. Arrivals and departures tabs.

Shows:

- The desk grid with each desk's state and per-desk service time as interval aggregates (from AMAN at AMAN sites). No officer names or ids anywhere.
- Waits per lane category (citizens, residents, visitors, crew and diplomats, e-gate eligible, as configured per site).
- E-gate utilisation, rejects by coarse category, and the predicted extra load that rejects put on manual desks.
- A disabled link "Officer analytics open in AMAN": officer-level data stays in the border system.

Common tasks: compare staffed desks with the lane waits; watch e-gate rejects that will load a manual lane.

## Check-in and handlers (Airport Operations module)

v1. Shows the islands by handler, each counter's state, waits per island, and SLA compliance per 15-minute bin with provisional and final markers.

Common tasks:

1. Allocate counters to a departing flight: island, counter range, open and close times (default STD minus 3 hours to STD minus 45 minutes). Overlapping allocations on a counter are rejected.
2. Watch bins turning from provisional to final during and after a peak.

## SLA and penalties

v1. Shows the contract card (KPI definition, threshold, evaluation window, exclusions, penalty schedule, signed zone profile version), the evaluation table for the window so far (breaches, exclusions applied, held bins, resulting penalty), disputes, and the evidence pack.

Common tasks:

| Task | Who |
|---|---|
| Draft and sign a contract (signed terms are locked; changes need a new version) | Terminal duty manager |
| Add an exclusion (type, zones, time window, reason, reference); evaluations recompute | Terminal duty manager |
| Raise a dispute on a final breached bin within the dispute window | Handler station manager |
| Review and decide a dispute (Upheld or Rejected) | Terminal duty manager |
| Download the evidence pack (interval data, zone profile version, calibration record, exclusions, content hash) | Both parties |

Provisional breaches are shown but never counted. See [KPI and SLA definitions](15-KPI-and-SLA-Definitions.md).

## Forecast and staffing

v1. Shows the next 24 hours of demand per lane, recommended desks per 15 minutes against the planned roster with gaps highlighted, and the predicted P90 wait under the plan and under the recommendation.

Common tasks:

1. Accept a recommendation to update the staffing plan.
2. Add a roster override: queue, from and to (15-minute aligned), planned desks, reason.
3. Add an ad-hoc flight when the AODB feed is stale or for diversions and extra sections: code, arrival or departure, on-block time or STD, seats, expected load, lane mix.

## Zones

Phase 0 from a configuration file; MVP editor. Shows the floor plan per level with zones, entry and exit lines, and the list of profile versions (for example v12 active, v13 draft).

Common tasks (editor): create a draft from the active profile; add or change zones and lines; publish (creates the next version; needs step-up MFA). A newly published zone shows "Not measured: no calibrated sensor" until a calibrated sensor covers it. Details in [Commissioning and calibration](07-Commissioning-and-Calibration.md).

## Devices

MVP. Shows the sensor registry (id, type, zone, status, frame rate, clock offset, last calibration), the sensors on the floor plan with their coverage, and outage history.

Common tasks: check why a zone is degraded; register a sensor and record its calibration (administrators and field engineers).

## Passenger display

Phase 0 (page), MVP (languages and stale handling). A 16:9 full-screen board per display channel, showing the nowcast per checkpoint in 5-minute bands (for example "Passport control: 10 to 15 min") in the site's languages. The band changes only when the nowcast moves a full band (hysteresis). A degraded zone shows a wider band. When data is stale the board shows a neutral message instead of an old number. Boards never show a realised wait.

Common tasks (Terminal duty manager): add a display (location, orientation, checkpoints, language order, band size, hysteresis, stale threshold, fallback messages).

## Reports

MVP (daily and weekly basics, CSV), v1 (full set, PDF, evidence packs). The daily report is built around peaks: peak-hour waits by lane, forecast against actual, staffing against recommendation, e-gate performance.

Common tasks: run a report now; schedule a report (template, scope within your view, schedule, CSV or PDF, recipients); export CSV. Provisional bins are marked as such. CSV exports neutralise spreadsheet formula prefixes for safety.

## Access and data boundary

MVP. Lists, per role, what is visible and what is not, the create rights of each role, the users of the deployment (administrators), and the audit log. It states that no screen shows officer identities.
