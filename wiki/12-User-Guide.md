# User guide

For supervisors, duty managers and handler staff who use Ariva during operations. It explains how to read the numbers, then each screen with what it shows and the common tasks, and the phase that delivers it. Screen descriptions follow the clickable prototype; the product may differ in layout.

Status: in Phase 0 only a minimal live operations dashboard and the passenger display page exist (on simulated data). The MVP (pilot) adds the full border screens; v1 adds the airport and SLA screens.

## Signing in

Ariva.Web opens on the sign-in page when you have no session (ARV-051). Enter your username and password; if your account has an authenticator app, Ariva then asks for the 6-digit code it shows, or one of your recovery codes instead. One message covers every refusal (wrong username, password or code), and after 10 attempts a minute from one address Ariva asks you to wait a minute. A link to a screen that needed sign-in brings you back to that screen afterwards; Ariva only returns to its own pages.

First sign-in, with the temporary password your administrator gave you:

1. Choose your own password: 12 to 128 characters, not a common or breached password, without your username or the product name.
2. Set up an authenticator app: scan the QR code (or type the key shown under it), then enter the code the app shows. Deployments that require MFA (all production deployments) do not let you skip this step.
3. Save the 10 recovery codes. They are shown only this once; Copy the codes puts them on the clipboard. Each signs you in once if you lose the phone. Then continue to Ariva.

While signed in:

- The sidebar shows only the screens of your role, and the card at its foot shows your name, roles and sites, with Account security and Sign out. Hidden screens are a convenience: Ariva's server checks every request.
- Your session lasts across tabs and reloads of the same browser. Signing out in one tab signs out every tab. When the session ends on the server (signed out elsewhere, your account changed by an administrator, or the refresh period over), Ariva returns to the sign-in page and then to the screen you were on.
- Critical actions (publishing a zone profile, changing users, devices, integrations or alert rules, new recovery codes) need a code from the authenticator within the last 15 minutes. If yours is older, a dialog asks for a fresh code (or a recovery code) and then completes the action; Cancel leaves it undone.
- Account security: change your password (your other sessions are signed out), set up an authenticator if you have none, and generate new recovery codes (the old ones stop working).

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

ARV-055, the home screen. Live over the live hub (WebSockets): a chip in the header says Live, Reconnecting or Not live (figures may be old). For the chosen site it shows:

- KPI tiles: passengers waiting and the longest nowcast wait over the zones reporting, open alerts, and how many queue zones are reporting.
- The floor plan of a level with the queue zones of the published profile coloured by nowcast (within target, near target from 10 minutes, over target above 15 minutes, the reference rule R-001; grey when degraded or stale) and the wait written on each zone.
- The queue zones table: people in queue, the nowcast wait (or why there is none, for example no desk open), a status in words, and the minute it is for. A zone not heard from for more than 150 seconds is marked Stale; one the hub does not let the user see is marked Not available.
- A wait chart of the selected zone: its nowcast minute by minute since the screen opened, against the 15-minute line. (v1 adds realised waits and the forecast band.)
- Alerts the user's role is responsible for, with severity, rule, zone and time; Acknowledge takes one on, with an optional note. New alerts and changes arrive live.
- The arrival wave (border shift supervisors, terminal duty managers, administrators): predicted hall arrivals per minute for the next 60 minutes, with the sum for now + 5 to now + 25 minutes.
- Desk states: each desk's latest minute (Serving, Idle, Paused, Closed, Unknown) with the people served. Immigration and emigration desks and e-gates are border data and show only to border shift supervisors and administrators; check-in counters and security lanes show to terminal duty managers and administrators. A handler station manager sees only its own counters, which needs handler tenancy, so until then the panel is not shown to handlers.

Common tasks:

1. Acknowledge an alert you own before its escalation timer runs out; then act (open desks, redirect passengers).
2. When a zone shows Stale or Data degraded, check the Devices screen and tell the site administrator.
3. Use the arrival wave to open desks before a wave reaches the hall.

Desk states API (ARV-055, `LiveQueue.View`): `GET api/v1/sites/{siteCode}/desk-states` answers each desk's latest minute within the last 15 minutes from Ariva.Api.Stream's desk engine, with the state that held most of the minute; check-in counters and security lanes only with `AirportDesks.View` (terminal duty managers, administrators), immigration and emigration desks and e-gates only with `BorderDesks.View` (border shift supervisors, administrators); a caller with neither gets an empty list. A desk is known by site/checkpoint/desk, so checkpoint codes are unique across a site (script 0033), and a minute whose key ever named a desk the caller may not see (a deleted checkpoint included) is left out. `GET api/v1/admin/floor-plans?siteCode=` lists the site's current floor plans, so screens fetch only the plans that exist; a site the caller cannot see and a site that does not exist both answer 404.

Arrival-wave API (ARV-047, `ArrivalWave.View`: border shift supervisors, terminal duty managers and administrators of the site): `GET api/v1/sites/{siteCode}/arrival-wave?minutes=30` (5 to 120) answers the flights landing within that many minutes and those landed whose passengers are still reaching the hall, each with its in-block time and where it comes from (`OnBlock`, `Landed` plus taxi-in, `Estimated`, `Scheduled`), its passengers (`Aman`, `PaxEstimate` or `Seats`; none when the feeds give none) and lanes (`Aman` or `DefaultMix`), then the predicted hall arrivals per minute and lane (`cit`, `res`, `vis`, `crw`, `eGate`), and the sum over the next 5 to 25 minutes. The split by lane is border data (`ArrivalWaveLanes.View`): a terminal duty manager gets the same answer with totals only (the lane counts and `laneSource` null). `truncated` is true when the site had more arriving legs than Ariva reads at once (2,000; the latest scheduled are left out). Passengers reach the hall 8 to 15 minutes after on-block (11 by default) over about 12 minutes (formulas F14). Cancelled and diverted flights are left out.

### Alerts (ARV-039)

You see the alerts of your sites that your role is responsible for: the rule's owner role, the escalation role once an alert has been escalated, and every role for a rule without an owner (administrators see all). An alert moves forward only:

| From | Action | To | Who |
|---|---|---|---|
| Raised | Acknowledge (optional note) | Acknowledged | The responsible role, once per alert |
| Raised or Acknowledged | Escalate (optional note) | Escalated | The responsible role, once per alert; also automatic when an alert stays Raised for the rule's escalation minutes |
| Escalated | Acknowledge | Acknowledged | The owner or the escalation role, if no one acknowledged it before the escalation |
| Any open state | Resolve (note required) | Resolved | The responsible role |
| Any open state | Clears by itself | Resolved | When the rule's clear condition has held for its clear minutes |

When the rule notifies by email (ARV-040), you also get an email when an alert you are responsible for is raised, and when one is escalated to your role: from Ariva's address, to you alone, naming the rule, zone, site, metric, value and times (UTC). It never holds traveller, officer or document data, nor anyone's notes; open Ariva to act on it. You need an email address in your profile (your administrator sets it), and at most 20 alert emails an hour go to one address by default; more are held back. An action on an alert that has already moved on answers that it is not in a state for that action. Notes are up to 500 characters and are shown as plain text. A screen that has joined the site's alerts on the live hub is told of each change, for the alerts its role is responsible for. Every acknowledgement, escalation and manual resolution is in the audit log.

API (`api/v1/alerts`): `GET ?siteCode=&state=&open=&zoneName=&ruleCode=&fromUtc=&toUtc=` (newest first), `GET {id}` (with `escalationDueUtc` while it can still escalate by itself), `POST {id}/acknowledge`, `POST {id}/escalate` and `POST {id}/resolve` with `{note}`. Live hub: `JoinAlerts(siteCode)` and `LeaveAlerts(siteCode)`; notices arrive on `alert` (what changed and its state, no names of people).

## Alert rules

MVP. Shows the rules that drive every alert, with their status. Delivered in ARV-056 at `/alert-rules` (`AlertRule.Search`): the site's rules with their code, name, condition in words, zones, severity, owner role and whether they are enabled. Border shift supervisors, terminal duty managers and administrators create, change, enable or disable, duplicate and delete rules (deleting asks for a fresh second factor); handler station managers read them.

Common tasks:

1. Create a rule: name, scope, metric, condition and threshold, sustain time, severity, owner role, escalation, channels. Check the backtest preview ("Would have fired 3 times, first at 2026-10-02 18:05 UTC") before saving: it is the same evaluation the live alerts come from, on the stored minutes of the range you choose (in UTC, up to a day that has ended, within the last 90 days). The preview judges the form as it is, saved or not, and lists the first 20 alerts it would have raised. It needs the live queue view as well as creating rules, because it shows past queue values.
2. For an early warning, choose the predicted nowcast and a lead time of 15 to 60 minutes: the rule fires when the queue is projected to pass the threshold within that time, from the arrival wave of landing flights (available once the flight feed is connected).
3. Enable, disable or duplicate a rule. A duplicate opens as a new rule named "Copy of" the original.

You can only create rules for queues your role can see, and give a rule (or escalate it) only to a role you hold; an administrator can give it to any role or to every role of the site. Rules are typed fields only, never an expression.

## Immigration (Border module)

MVP, delivered in ARV-057 at `/immigration` (`Immigration.View`: border shift supervisors, terminal duty managers, administrators; handler station managers have no immigration screen). Arrivals and departures tabs (the halls of the site's Immigration and Emigration checkpoints).

Shows:

- The desk grid with each desk's state and per-desk service time as interval aggregates (from AMAN at AMAN sites). No officer names or ids anywhere.
- Waits per lane category (citizens, residents, visitors, crew and diplomats, e-gate eligible, as configured per site).
- E-gate utilisation, rejects by coarse category, and the predicted extra load that rejects put on manual desks.
- A disabled link "Officer analytics open in AMAN": officer-level data stays in the border system.

Where the figures come from: the wait per lane is the live nowcast of the lane's queue zone (the zone profile says which queue zone is which lane's queue, see Zones), on the live hub; desks open and paused are each desk's latest AMAN session event; people served, mean and P90 service and cycle times are AMAN's interval aggregates over the last 15 minutes, weighted by the people served; e-gate utilisation is the used gates' busy time (attempts times mean cycle) over the configured gates' time; the extra manual load is each rejected traveller at the manual desks' mean service time, in desk-minutes, and spread over the desks open now as minutes of extra wait (an estimate). Desk and e-gate codes are Ariva's (AMAN's codes are mapped, ARV-015). Terminal duty managers see the lane and e-gate totals only: the server leaves every desk and gate row out of their answer (`BorderDesks.View`), and a lane's service and cycle times when fewer than three desks served in the window (one or two desks' times would be those desks' own).

API (ARV-057, `Immigration.View`): `GET api/v1/sites/{siteCode}/immigration` answers both halls with their lane queues, lane aggregates and e-gate totals, plus desk and gate rows for callers with `BorderDesks.View`.

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

## Topology

ARV-052. The airports, terminals, levels, checkpoints and desks of your sites in five columns, with each desk's kind, lane categories and whether it is in service, and (terminal duty managers) the codes AMAN and the AODB use for it. Read only for the operational roles; administrators change it (see the [Administration guide](11-Administration-Guide.md), section 2a).

## Zones

ARV-053. Shows each level's floor plan with its zones (queue, service, staff, overflow, each in its own colour and named on the plan) and lines (entry, exit, count, overflow entry), and the site's versions (for example v12 published, a draft from v12). Handler station managers see them read only; supervisors and duty managers edit; only a draft changes.

Editing a draft: Add a zone starts a small square in the middle of the level, Add a line starts a line on an edge of its queue zone (an exit across from the entry). Select a zone or line on the plan or in the lists; drag a corner or a line end and let go to save, or use the keyboard: Tab to a handle, arrow keys move it by 0.1 m (1 m with Shift), Enter saves. The corner table beside the plan takes exact coordinates in metres, adds a corner after any other or removes one (at least three), and Save the zone saves the name and the shape. The server checks every shape (inside the level, no crossing edges, entry and exit lines on their queue zone's edge) and a refused move puts the shape back. Check the draft lists what still stops it from being published; Publish then publishes exactly what was checked and asks for a fresh authenticator code if the last one is older than 15 minutes.

A queue zone can say which lane category it is the queue of (CIT, RES, VIS, CRW, EG; ARV-057): choose it under Lane and save the zone. The immigration screen shows the wait per lane from it. It goes with the published version like the shapes, and does not change the geometry hash. Through the API, a zone change without `laneCategory` keeps the lane and an empty one clears it.

Common tasks (editor): create a draft from the active profile; add or change zones and lines; publish (creates the next version; needs step-up MFA). A newly published zone shows "Not measured: no calibrated sensor" until a calibrated sensor covers it. Details in [Commissioning and calibration](07-Commissioning-and-Calibration.md).

## Devices

ARV-054, for border shift supervisors, terminal duty managers and administrators (handler station managers have no devices screen). Shows the site's sensor registry (code, family, owning queue zone, state, when Ariva last heard from it, last calibration), each queue zone's sensing health (Healthy, Degraded, Unmonitored, with the heartbeat timeout), and a level's floor plan with the queue zones and each device's coverage (a dashed outline when the footprint is the BOQ's assumption).

Common tasks: register a sensor (code, family, model, transport, dialect, clock, level, position, height, orientation, queue zone, footprint if the vendor gives one); store its credential, which is shown once with a copy action and never again (a lost one is replaced, not recovered); record a calibration (a pass at the threshold takes it Online); move it (back to Commissioning until a calibration passes); restrict the networks it may push from or pin its client certificate; issue a new credential; retire it (administrators). Registering, a new credential, network access and retiring ask for a fresh authenticator code if the last one is older than 15 minutes.

## Passenger display

Phase 0 (page), MVP (languages and stale handling). A 16:9 full-screen board per display channel, showing the nowcast per checkpoint in 5-minute bands (for example "Passport control: 10 to 15 min") in the site's languages. The band changes only when the nowcast moves a full band (hysteresis). A degraded zone shows a wider band. When data is stale the board shows a neutral message instead of an old number. Boards never show a realised wait.

Delivered in ARV-058. The settings screen is `/displays` (`Display.*`: terminal duty managers airport-side, border shift supervisors for the immigration halls, administrators; handler station managers have none). A display has a code, a name, a location, an orientation, its languages in order (English, Arabic, Portuguese and Swahili have board resource files; Arabic reads right to left), the band width (1 to 30 minutes, 5 by default), the hysteresis (less than the band; the band changes only when the nowcast leaves it by that much), the stale threshold (60 to 1,800 seconds), up to 12 queue zones of the published profile with a label in each language, and the neutral message in each language.

The player is a kiosk browser on the screen (full screen, 1920 by 1080) opened at the address shown once when the display is created or gets a new credential: `/display?code=CODE#key=CREDENTIAL`. The credential sits in the fragment, so it is never sent to a server or logged; the player keeps it for its tab and removes it from the address bar. The player asks for its board every 10 seconds with the credential in the `X-Ariva-Display-Key` header (`GET api/v1/display/board?code=`), never with a user's account; a wrong or replaced credential, or a deleted or disabled display, shows "This display is not set up". Creating a display and issuing a new credential ask for a fresh authenticator code. The board's type shrinks to fit every row on the screen.

Common tasks (terminal duty manager, border shift supervisor): add a display; change its queues, labels or bands; issue a new credential when a player is replaced or its address may have leaked (the old one stops at once); disable or delete it.

## Reports

MVP (daily and weekly basics, CSV), v1 (full set, PDF, evidence packs). The daily report is built around peaks: peak-hour waits by lane, forecast against actual, staffing against recommendation, e-gate performance.

Common tasks: run a report now; schedule a report (template, scope within your view, schedule, CSV or PDF, recipients); export CSV. Provisional bins are marked as such. CSV exports neutralise spreadsheet formula prefixes for safety.

Delivered in ARV-060 (the API and the scheduled email) and ARV-061 (the screen). The daily report covers one site and one local day (the site's airport time zone, so a daylight-saving day has 23 or 25 hours), for border shift supervisors, terminal duty managers and administrators (`Report.View`; the handler's own-counter report comes with its module). Per queue zone (lane): passengers, waits, the day's P50 and P90, the peak hour (the hour with the highest P90 among hours with at least 20 waits), the longest queue, and every local hour with its own P50 and P90. Percentiles are merged from each minute's 30-second wait histogram, never averaged (formulas F7), so they are exact to 30 seconds; an hour with provisional minutes says Provisional, and an hour stored before histograms were kept says the histogram is missing. The day's alerts are listed with severity, state and how long they were open, only those the reader's roles see (as on the alert screens). Each device gets its uptime: the share of the day with no outage. A report can be read up to 400 days back.

Exports: `GET api/v1/sites/{site}/reports/daily?date=YYYY-MM-DD` (JSON) and `daily.csv?date=...&section=hours|alerts|devices` (UTF-8 CSV with a byte order mark so spreadsheets read Arabic names). A text cell that starts with `=`, `+`, `-`, `@`, a tab or a carriage return is written with a leading apostrophe, so opening the file never runs a formula; a cell holding a comma, semicolon, tab, quote or line break is quoted, so spreadsheets set to a semicolon separator (common with Arabic and many European locales) do not split it. `section` takes only those three names.

Scheduled delivery: a schedule (`ReportSchedule.*`, same roles) names a site, a local send time (HH:MM) and 1 to 20 recipients, who must be enabled Ariva accounts that reach the site and may read reports, past their first sign-in (no temporary password, an authenticator where one is required) and never the break-glass account; free email addresses are not possible. Every five minutes Ariva.Api.Cronz sends each due schedule's report for the previous local day to each recipient's account address, once: the report that recipient's roles see, with the three CSV files attached and the headline figures (worst peak hour, zone-hours above 15 minutes at P90, alerts, lowest uptime) in the body. A recipient who has since lost the site, the role or an address is skipped and recorded; a failed send is retried up to five times. Schedules are audited.

The Reports screen (`/reports`, sidebar item Reports under Oversight, shown with `Report.View`) opens on yesterday at the site, in the site's time zone; a date field (up to 400 days back) and Yesterday and Today so far buttons change the day, and today's partial report says so. Four headline figures (worst peak hour at P90 with its lane and hour, zone-hours above 15 minutes at P90, alerts with the critical count, lowest device uptime), then every queue zone with passengers, waits, P50, P90, peak hour, longest queue and a strip of its local hours coloured by P90 (green under 10 minutes, amber from 10, red above 15, grey without waits). A zone opens to its hours, each marked Final or Provisional, and says when an hour has no histogram. The day's alerts (only those the reader's roles see, as the server decides) and device uptime follow. The three CSV buttons save the server's files as they are. With `ReportSchedule.Search` a Schedules tab lists the site's schedules (name, send time, recipients, last delivery, enabled) and, by permission, adds, edits and deletes them; recipients are picked from the server's list of accounts that may receive the site's report, and an account already on a schedule that has since lost the right is shown so it can be removed. A role without `Report.View` (a handler station manager) has no item and sees the no-access panel at `/reports`. English and Arabic.

## Access and data boundary

MVP. Lists, per role, what is visible and what is not, the create rights of each role, the users of the deployment (administrators), and the audit log. It states that no screen shows officer identities.

## Users and access

Delivered in ARV-059, for system administrators (`User.*` and `AuditEntry.Search`; other roles have no item in the sidebar, the address shows "No access", and the API answers 403). The screen at `/users` has two tabs.

Accounts lists the deployment's accounts with their roles, sites and state (active or disabled, locked, still on a temporary password, no authenticator yet), searchable by username or name, role and state. Picking an account opens its panel: the name and email; the roles, granted or revoked as soon as a box changes; the sites (every site, or a list); and the actions reset password, reset authenticator, unlock, disable and enable. Adding an account takes a username (3 to 64 letters, digits or `. _ - @`), an optional name and email, roles and sites, all in one step; its temporary password is shown once with a copy action (switching tabs or leaving the page drops it), and the person changes it at the first sign-in and sets up an authenticator then. An administrator limited to some sites must give a new account at least one of its own sites; only an administrator of every site may create an account without sites (it reaches nothing until it gets some) or for every site. An account without sites is administered only by administrators of every site. A name is one line of visible text in any script.

The audit log shows every change, newest first, in UTC: who, what action, about which account or site, the before and after summaries (shown on demand, always as text; names and emails are quoted so they cannot read as other fields), the address and the trace id. Filter by action and UTC day, or open it from an account's panel for the changes to that account or by that account. Nobody can change or delete an entry.

What the screen will not do, and the server refuses even when asked directly: change your own roles, sites, password, authenticator or status (your own account's panel is read only) (another administrator does; your own password and authenticator are under Account security); grant a role above your own; give a site you cannot reach yourself, or every site when you have only some; show or touch an account beyond your sites. An administrator limited to some sites sees only the accounts inside them and only their audit entries. Creating an account, resetting a password or an authenticator, granting or revoking a role, changing sites and enabling a disabled account ask for a fresh authenticator code if the last one is older than 15 minutes.

Common tasks (system administrator): add an account for a new supervisor and give it its site; reset the password or authenticator of someone who lost theirs; disable a leaver (their sessions end within seconds); find who changed an account and when.
