# Task: build an offline HTML site that presents the Airport QMS business plan and a clickable prototype of the product

You are building two things in one static site:

- **Part 1, the business plan:** a multi-page site presenting Dalil Tech's business plan for a new product, an Airport Queue Management System (QMS).
- **Part 2, the product prototype:** a clickable, live-simulated demo of what the QMS application will look like, running on a fictional airport sized like Beirut's.

The audience is Dalil Tech management first, then prospective clients (border authorities and airport operators). Every business number below comes from the approved plan. Do not invent business numbers. If something you need is missing, show "to confirm" instead of guessing. Prototype data is synthetic and must be labelled as such everywhere it appears.

## Output

Create a folder `qms-business-plan/` with:

- Part 1 pages: `index.html` (executive summary), `market.html`, `positioning.html`, `segments.html`, `pricing.html`, `boq.html`, `costs.html`, `breakeven.html`, `gtm.html`, `risks.html`, `sources.html`
- Part 2 pages in `app/`: `index.html` (live operations), `rules.html` (alert rules), `immigration.html`, `checkin.html`, `sla.html`, `forecast.html`, `zones.html`, `devices.html`, `display.html`, `reports.html`, `access.html`
- Shared assets: `assets/styles.css`, `assets/data.js`, `assets/charts.js`, `assets/app.js`
- Prototype assets: `app/assets/app.css`, `app/assets/sim.js`, `app/assets/ui.js`

The business site's navigation includes a "Product demo" link to `app/index.html`; the prototype has a "Back to business plan" link.

## Technical rules

1. Must open from `file://` with no server and no internet. No frameworks, no CDNs, no web fonts, no `fetch`. Use a system font stack.
2. Business data lives in `assets/data.js` as one global object. Every page and calculator reads from it, so a number changed there changes everywhere.
3. Every business number carries a provenance marker: a small "Sourced" tag linking to its source, or an "Assumed" tag. Use the flags given below.
4. Charts are inline SVG drawn by small vanilla JS helpers in `assets/charts.js`, shared by both parts: line chart with optional band, stacked bars, horizontal bars, layered market map, 2x2 matrix, phase roadmap, sparkline. Axes carry units; line ends carry labels; no legend when labels fit.
5. Light and dark themes (follow the OS setting, with a manual toggle). The prototype defaults to dark, as control-room screens usually are. Responsive down to 360 px with no horizontal page scroll (the prototype may scroll inside its floor-plan panel).
6. Use logical CSS properties throughout so Arabic RTL works. The passenger display page is bilingual Arabic and English.
7. Print stylesheet for Part 1: each page prints cleanly to A4 (navigation hidden, charts kept), so it can be exported to PDF from the browser.
8. Footer on Part 1 pages: "Dalil Tech, confidential. Figures as of 28 September 2026." Brand colours as clearly named CSS custom properties with neutral placeholder values that can be swapped later.
9. Accessible: semantic headings, tables with header cells, SVG charts with a title and aria-label, sufficient contrast, keyboard-usable controls, status colours always paired with text or an icon.

## Writing rules for all visible copy

- Lead each page with its answer in one or two sentences, then the supporting tables and charts.
- Plain, direct English. No marketing filler.
- Never use em dashes or double hyphens in visible text. Use commas, colons, semicolons, periods or parentheses.
- Currency is USD unless stated. Format numbers with thousands separators.

# Part 1: business plan pages

### index.html: executive summary

Answer: "It is a business on narrow terms: sell the Border module into AMAN clients first, bundle the QMS into every AMAN bid, and reach airport operators with a penalty-grade, on-prem product through partners."

Key figures as stat tiles:
- Build cost to v1: 624,750 (cash 522,000; the product owner's time 102,750). Assumed.
- Run-rate after v1: about 400,000 a year. Assumed.
- Base case: 9 deals from 2028 to 2030, pays back during 2030. Assumed model.
- Beirut (BEY) reference deal: year one 666,325; five-year cost of ownership 880,795; Dalil keeps about 270,000 in year one. Assumed.
- Market benchmark: London Luton's 2025 queue measurement tender, estimated GBP 635,000 over five years. Sourced: https://www.find-tender.service.gov.uk/Notice/048206-2025

Four recommendations:
1. Price software per airport and module, never per sensor.
2. Stay vendor-agnostic on hardware: certify one stereo and one LiDAR family, sell through local partners.
3. Make the QMS a priced option in every AMAN proposal from now on.
4. Decide go or no-go at the pilot gate: if no pilot contract by mid-2027, stop at Phase 0; loss capped near 80,000.

Context line: AMAN is Dalil Tech's border control platform, deployed in the UAE, Angola and Tanzania. It records every immigration transaction (desk, officer login, processing time) and receives API passenger counts for inbound flights.

Add a prominent card: "See the product" linking to the prototype.

### market.html

Answer: "The product in the original brief already exists three times over. What nobody sells is queue analytics built on the border system's own data, with penalty-grade contracts and on-prem delivery."

Market map chart, five layers top to bottom, plus one adjacent market. Shade layers 2 and 3.
1. Airport operations suites (AODB, RMS, FIDS): SITA, Amadeus, Veovo RMS, DTP (integrator)
2. Forecasting and staff planning: Copenhagen Optimization, Veovo, DTP Desk Planner
3. Queue and flow analytics: Xovis AERO, Veovo, Outsight, Beonic, SITA PFM
4. Perception software: Xovis (on-sensor), Outsight SHIFT, Ouster Gemini, Seoul Robotics SENSR
5. Sensors: Xovis, Hesai, Ouster, RoboSense, Seyond
Adjacent (dashed): ticket-based queuing: Qmatic, Wavetec, Qwaiting

Competitor cards (what they sell | scale and references | strengths | weaknesses):
- Xovis (Switzerland): own 3D stereo sensors plus airport software; AERO managed cloud service since Nov 2023 | 120+ airports, about 700 touchpoints; AUH 455 sensors installed with DTP in 2018; DXB and DOH | purpose-built sensors, check-in SLA use cases, AODB/FIDS/RMS API | software tied to own sensors, no forecasting module found, cloud-first. Sources: https://www.xovis.com/solutions/airport , https://www.cbnme.com/logistics-news/dubai-technology-partners-xovis-to-refine-operations-at-auh-airport/
- Veovo (UK): software only, sensor-agnostic; queues, flow, forecasting, lane plans incl. border control, RMS | 140+ airports; Schiphol, Sydney, Oslo, Gatwick; Aeropuertos Argentina 2000 at EZE and AEP incl. immigration | measure, predict and plan on one platform | needs integrators, no Gulf or African reference found. Sources: https://veovo.com/platform/queue-management , https://www.airportsinternational.com/article/argentinas-busiest-airports-choose-veovo
- Copenhagen Optimization (Denmark): Better Airport, nine cloud modules incl. Better Border | Hannover, London Stansted, Manchester | rostering and planning depth | no sensing. Source: https://copenhagenoptimization.com/better-airport/better-border
- SITA: passenger flow management, queue analytics, AODB, FIDS | 1,000+ airports; AUH iTAM platform MoU, Nov 2025 | incumbent platform owner | queue analytics is one module among many. Source: https://simpleflying.com/ai-controlled-airport-abu-dhabi-sita-smart-hub/
- Amadeus: airport operations, common use, Airport Insights | no sensor-based queue product found | owns check-in data | would need to partner.
- Outsight (France): LiDAR perception and analytics, 210+ LiDAR models | DFW deployment of USD 17.2m; 280m annual passengers measured (Sep 2026); partnered with NEC | no images, multi-sensor fusion | NEC is a border competitor of ours. Sources: https://www.futuretravelexperience.com/2025/06/dallas-fort-worth-international-airport-selects-outsight-for-worlds-largest-3d-lidar-deployment/ , https://lidarmag.com/2026/09/21/outsight-crosses-the-milestone-of-280-million-annual-passengers-under-real-time-measurement-at-airports-worldwide/
- Qmatic and Wavetec: ticket-based queuing for service counters; not terminal queue measurement. Wavetec is based in Dubai with offices in Riyadh and Nairobi.

Gaps table (gap | our position):
- Border-system ground truth (officer login, per-document time, e-gate rejects, API counts) | strong at AMAN sites only
- E-gate and corridor analytics joined to queue forecasts | strong
- Contract-aware SLA and penalty engine | open, nobody has it
- On-prem delivery for government buyers | strong
- Border and airport views with an enforced data boundary | designed in
- Delivery in sub-Saharan Africa | strong

### positioning.html

Answer: "We win at the border and lose in the terminal, unless we change the question."
Positioning line (large): "Border-grade queue intelligence, built on the border system's own data, penalty-grade by design, on your premises."

Advantages tested (advantage | border authority cares | airport operator cares):
- Officer login as desk-open signal | yes | only as an aggregate
- Real processing time per desk | yes | indirectly, via better accuracy
- API counts per inbound flight | yes, lane mix hours ahead | partly; load messages give counts but not nationality mix
- E-gate outcomes | yes | weakly
- Government relationships in three countries | yes | barely
Note: all advantages exist only where AMAN runs; they must be proven by a pilot that compares forecast error with and without AMAN inputs.

Differentiators (name | value | effort S/M/L | competitors offer it):
Arrival-wave alert | border supervisors staff before the wave | S | not found
Lane-mix forecast from API | staffing per lane | M | not found
E-gate analytics joined to queue | e-gate ROI evidence | S | gate vendors show own gates only
Departure show-up forecasting | airport and emigration | M | yes, table stakes
Staffing recommendation | the decide layer | M | yes
Service-standard reporting | border reporting upward | S | partly
Handler SLA and penalty engine | airport operator | L | not found
Shared view with enforced data boundary | sell both modules at one site | M | claimed, boundary unstated
Queue balancing signage | both | S to M | yes (Veovo)
What-if simulation | planning cases | L | partly
On-prem delivery | government | part of architecture | rarely led with
Effort key: S up to 3 developer-weeks, M 4 to 8, L over 8.

Each differentiator row links to the prototype screen that shows it (for example, arrival-wave alert links to `app/index.html#wave`).

Walk away from: replacing a working Xovis or Veovo dashboard on dashboards alone; cloud-first operators wanting 50+ references; retail heat maps; wayfinding; penalties against airlines for queue time.

### segments.html

Answer: "Sell to border authorities where AMAN runs first, then state-run mid-size airports, then border authorities without AMAN."

Segments (priority | segment | module):
1 | Border authorities at AMAN sites | Border
2 | State-run airports of 5 to 15 million passengers in the Middle East and Africa | Airport Operations plus border feed
3 | Border authorities without AMAN (for example General Security at BEY) | Border, sensor-only
4 | Private concessionaires (AIAAN's concessionaire, dnata at ZNZ, Abu Dhabi Airports) | as a component via a prime
Not a target | hub airports such as DXB and DOH | none
Note: handlers are not buyers; they are what the penalty engine is aimed at, so they must be able to trust the numbers.

Personas (persona | cares about | buys on | objection):
Border operations director | visible queues, staffing evidence | sovereignty, relationship, proven accuracy | "AMAN already reports processing times"
Border shift supervisor | when to open which desk | simple, correct alerts | fear of officer monitoring
Airport COO | service scores, handler performance | evidence handlers accept; references | "how many airports use you?"
Airport CIO | integration risk, security | standards (AIDX, ACRIS), on-prem | "another system to run"
Handler station manager (not a buyer) | not being penalised unfairly | transparent definitions, dispute process | contests every number
Government procurement | capex, tender rules, payment terms | clear BOQ | budget timing

### pricing.html

Answer: "Perpetual licence per airport and module with 18% annual support; term licence for private operators; never per sensor."

Models table: per sensor (rejected), per zone (add-on only), perpetual plus support (default), term licence (offered to private operators), hosted SaaS (not now).

Price list for 5 to 15 million passengers (all Assumed):
Core platform licence 90,000
Airport Operations module 70,000
Border module, sensor-only 50,000
Border module with AMAN integration 80,000
Annual support and updates 18% of licence, from year 2
Term licence 35% of perpetual per year, support included, three-year minimum
Penalty-grade assurance 15,000 per year
Hardware at cost plus 12 to 18%
Pilot at cost, credited to the licence if bought within six months

Interactive tier selector: under 5 million passengers = 60% of licence prices; 5 to 15 million = 100%; 15 to 30 million = 160%. Module checkboxes. Output: licence total, annual support, and the equivalent term licence per year.

### boq.html: Beirut reference airport, interactive

Context (with tags):
- Operator: Directorate General of Civil Aviation, Ministry of Public Works and Transport. Sourced: https://en.wikipedia.org/wiki/Beirut%E2%80%93Rafic_Hariri_International_Airport
- About 7.0 million passengers in 2025 (same source); 8 million used as the design year. Assumed.
- Counters: 48 check-in, 22 departure immigration, 22 arrival immigration. Given by client brief.
- Security: two checkpoints of five lanes. Assumed.
- Handlers: MEA subsidiaries and Lebanese Air Transport; MEA's parent is Banque du Liban. Sourced: https://en.wikipedia.org/wiki/Middle_East_Airlines
- Data law: Law 81/2018, Ministry of Economy and Trade oversight, minimal enforcement. Sourced: https://www.consentstack.io/regulations/lb-law81

Coverage defaults (area m2 | ceiling | sensor footprint | sensors):
Check-in 1,500 | 10 to 14 m | 12 x 9 m | 20
Departure immigration 930 | 4 to 6 m | 10 x 10 m | 13
Arrival immigration 1,080 | 4 to 6 m | 10 x 10 m | 15
Security 800 | 4 to 6 m | 10 x 10 m | 11
Total 59 (range 50 to 75)
Provide an "estimate from area" helper per row: ceil(area / ((footprintLength minus 0.3) x (footprintWidth minus 0.3)) x 1.3). Show its result beside the editable sensor count, but keep the defaults above as the starting values (the plan used judgement on rounding).

BOQ lines (qty | unit | default):
Stereo sensors | total sensors | 3,200 (reseller list about EUR 2,830 to 2,890 at an assumed 1.12 USD per EUR; source https://shop.vemcogroup.com/collections/xovis)
Mounts and brackets | total sensors | 250
Multisensor processing units | 4 | 3,000
PoE switches | 4 | 4,000
Servers | 5 | 12,000
Wait-time displays (optional toggle, default on) | 10 | 2,000
Supervisor tablets (optional toggle, default on) | 6 | 800
Software: Core 90,000; Airport Operations 70,000; Border sensor-only 50,000 (each a toggle)
Site survey 12,000; installation labour 450 per sensor; cabling 300 per sensor; commissioning 20,000; integrations 25,000; validation campaign 18,000; training 8,000; project management 10% of services
Recurring from year 2: support 18% of licences; hardware maintenance 5% of hardware
Margins for "what Dalil keeps": sensor cost 2,750 each (so 450 margin per sensor), 10% on other hardware, 90% of software, 30% of services.

Editable inputs: every quantity, unit price, the FX rate, toggles, margins. Outputs: hardware, software and services subtotals, year-one total, annual recurring, five-year cost of ownership (year one plus four years recurring), Dalil contribution. Also a stacked bar showing year one split by hardware, software, services.

### costs.html

Answer: "About 625,000 to reach v1 in mid-2028, 522,000 of it cash."

Inputs (Assumed): developer 8,000 per month loaded; field engineer 6,000; product owner time valued at 15,000; toggle to include or exclude the owner's time.
Phase 0 (Oct 2026 to Apr 2027, 7 months): developer half-time 28,000; owner 25% 26,250; lab sensors 12,000; vendor talks and travel 8,000; tooling 3,000; total 77,250.
Phase 1 (May to Oct 2027, 6 months): developer 48,000; field engineer 36,000; owner 40% 36,000; penetration test 20,000; legal 15,000; travel 25,000; pilot subsidy 50,000; tooling 3,000; total 233,000.
v1 (Nov 2027 to Jul 2028, 9 months): two developers 144,000; field engineer half-time 27,000; owner 30% 40,500; second sensor lab 8,000; tests and certification 20,000; marketing and events 40,000; travel 30,000; tooling 5,000; total 314,500.
Run-rate after v1: 192,000 developers; 72,000 field engineer; 54,000 owner; 60,000 events and travel; 25,000 tooling and legal; about 400,000 a year.
Salary lines recompute from the monthly inputs, months and allocation percentages.

### breakeven.html

Answer: "The base case pays back in 2030; two deals a year only covers running costs."

Model (all Assumed, all editable):
- Deal contribution: border deal 150,000; airport deal 230,000.
- Support contribution per deal already sold, each following year: border 17,000; airport 16,000.
- Costs by year from the phase totals: 2026 = Phase0 x 3/7; 2027 = Phase0 x 4/7 + Phase1 + v1 x 2/9; 2028 = v1 x 7/9 + runRate x 5/12; 2029 = runRate; 2030 = runRate. Defaults come from data.js (no cross-page state needed).
- Deal mix (border, airport) by year:
  Conservative: 2028 (1,0); 2029 (1,1); 2030 (1,1)
  Base: 2028 (1,1); 2029 (2,1); 2030 (2,2)
  Optimistic: 2028 (2,1); 2029 (3,2); 2030 (3,3)
- Pilot contributes nothing (subsidised, inside 2027 costs).
Chart: cumulative cash in USD thousands, one line per scenario, 2026 to 2030, zero line labelled "Break-even".
Expected default results (thousands, rounded): Conservative -33, -380, -641, -644, -614; Base -33, -380, -411, -248, 195; Optimistic -33, -380, -261, 299, 1,172.

### gtm.html

Answer: "Land with the Border module at an AMAN client, expand across its sites, then sell the Airport Operations module to the operator at the same airports."

Steps (step | when | proof needed):
1 Paid pilot at one AMAN arrivals hall | contract by Q2 2027 | demo and KPI annex
2 Validation report and case study | Q4 2027 | pilot result
3 Expand within the same authority | 2028 | client's own reference
4 Border module in the other AMAN countries | 2028 | first case study
5 Airport Operations module at the same airports | 2028 onward | v1 and the border feed
6 New markets such as BEY | 2028 onward | two references
7 AUH and concessionaire groups, as a component inside a prime such as SITA iTAM | after v1 | v1 reference and partner agreement
Callout: from now on every AMAN proposal carries a priced QMS option.

Roadmap chart (four phases, three gates, not to scale):
Phase 0 demo core, Oct 2026 to Apr 2027, developer half-time | gate: pilot contract signed | Phase 1 pilot MVP, Apr to Sep or Dec 2027, developer plus field engineer (shade this phase) | gate: pilot accepted | v1 first sale, Oct 2027 to Jun 2028, two developers | gate: first commercial sale | v2 scale, from H2 2028.
Under the roadmap, a small table mapping each prototype screen to the phase that delivers it: live operations, alert rules, immigration, passenger display, devices, zones and reports in the MVP; forecast and staffing, check-in and handlers, SLA and penalties in v1; what-if simulation in v2.

### risks.html

Answer: "The two risks that decide the product are commercial: the pilot slipping and the team being pulled back onto AMAN."
2x2 matrix (likelihood x impact) plus a table (risk | likelihood | impact | mitigation | early signal):
Pilot contract slips | High | High | small paid pilot, two candidates, QMS in every AMAN bid | no procurement route by Q1 2027
Team pulled back onto AMAN | High | High | fund developer full-time, name field engineer | velocity below half plan for two sprints
AMAN advantage not measurable | Medium | High | define comparison before pilot | pilot result
Stereo vendor refuses supply | Medium | High | certify LiDAR in parallel | talks stall by end 2026
Incumbent discounting or bundling | High | Medium | target where incumbents are absent | losses on price
Country and payment risk | Medium | High | milestones, hardware advance, USD, letters of credit | terms beyond 90 days
Legal (Angola camera law, Lebanon licence, Tanzania registration) | Medium | Medium | local counsel, LiDAR option | counsel answers
Penalty disputes | Medium | Medium | penalty tier only after validation | first disputed penalty
AODB access delays | High | Medium | schedule-file import | no feed by v1
Price expectations below list | Medium | Medium | test in first three bids | discount demands above 30%
Customs and permits | Medium | Medium | local partners, order at signature | lead times above 10 weeks

### sources.html

List every source URL used above, grouped by page, each with a one-line description.

# Part 2: product prototype (app/)

## Purpose and honesty

Show clients and management what the QMS will look like and how it behaves. It is a front-end prototype on synthetic data, not the product. Every prototype page shows a persistent "Prototype, synthetic data" badge in the top bar. Do not use real airline, handler or authority names in the prototype; use the fictional ones below.

## The fictional airport

"Demo International Airport (DMO)", sized like the business plan's reference airport:
- Departures: 48 check-in counters in 4 islands of 12 (A, B, C, D); 2 security checkpoints (North, South) of 5 lanes each; departure immigration with 22 desks and 4 e-gates.
- Arrivals: arrival immigration with 22 desks and 6 e-gates; baggage reclaim shown only as a zone.
- Immigration lane categories: Citizens, Residents, Visitors, Crew and diplomats, E-gate eligible.
- Two fictional ground handlers: "Handler A" (desks in islands A and B) and "Handler B" (islands C and D).
- About 8 million passengers a year: roughly 11,000 arriving and 11,000 departing per day, in waves (a morning bank 06:00 to 09:00, an evening bank 18:00 to 22:00, a night bank 01:00 to 04:00).
- Fictional flight codes (for example "DM 214", "XR 331", "QL 118"), 120 to 300 seats, load factors 70 to 95%.

Draw an invented floor plan as SVG (not a real airport's plan): two levels shown as tabs (Departures, Arrivals), with snake queues drawn as polygons, entry and exit lines, desk rows, e-gates, and overflow bands.

## Simulation engine (app/assets/sim.js)

- Deterministic: seeded pseudo-random generator (for example mulberry32) with the seed shown in the UI, so every demo run looks the same unless the seed changes.
- Simulated clock with controls: play, pause, speed (real time, 60x, 600x), and "jump to" buttons for the morning peak, the evening peak and the night bank. Start at 17:40 so the evening wave arrives within the first minute of a demo at 60x.
- Every simulated minute:
  - Arrivals: each landing flight's passengers reach the immigration hall starting 8 to 15 minutes after on-block, spread over about 12 minutes, split by lane category (synthetic mix: Citizens 35%, Residents 20%, Visitors 35%, Crew and diplomats 2%, E-gate eligible taken as 40% of Citizens and Residents).
  - Departures: passengers reach check-in from 3 hours to 45 minutes before departure (a smooth show-up curve), then security, then emigration.
  - Service times (synthetic, show them on an "About this demo" panel): Citizens 30 s, Residents 45 s, Visitors 95 s, Crew and diplomats 20 s, e-gate cycle 18 s with a 7% reject rate (rejects join the Visitors lane), check-in 150 s per passenger, security about 180 passengers per lane per hour.
  - Queues use the backlog recursion: next backlog = max(0, backlog + arrivals minus capacity), with capacity from staffed desks divided by cycle time.
  - Desks follow a staffing plan with random short pauses; desk states are Closed, Idle, Serving, Paused, Unknown.
  - Realised wait is computed for passengers who have exited; the nowcast is (queue length + 1) divided by current throughput.
- Scripted events so every demo shows the key behaviours:
  - At 18:05 an arrival wave pushes the Visitors nowcast above 15 minutes, firing an alert.
  - At 18:20 sensor S-17 over the arrivals hall goes offline for 10 minutes: its zone turns "degraded", the wait for that zone shows a band instead of a number, and a device alarm appears.
  - At 19:10 Handler B's check-in queue breaches its SLA for three consecutive 15-minute bins (provisional first, final once the queue clears).

## App shell

- Left sidebar navigation with icons and labels; top bar with airport and terminal selector (only DMO), simulated clock and speed controls, role switcher, theme toggle, "Prototype, synthetic data" badge, and "Back to business plan".
- Dense, calm operations styling: dark by default, one accent colour, status colours (good, warning, critical) always paired with text. Numbers in tabular figures.
- Every screen leads with its most important number in large type.

## Screens

1. **Live operations (`app/index.html`)**: KPI tiles (longest current wait and its lane, people queuing, desks staffed of total, e-gates in use and reject rate); the floor plan with zones coloured by nowcast and desks by state, hover tooltips; a chart of the last two hours of realised wait, the current nowcast, and the next two hours of forecast as a P50 line with a P90 band, plus a dashed target line at 15 minutes; an alert list (severity, rule, owner role, time, acknowledge button, escalation countdown); and an arrival-wave strip (anchor `#wave`): flights landing in the next 30 minutes with passengers by lane and the predicted hall arrival curve.
2. **Immigration (`app/immigration.html`, Border module)**: arrivals and departures tabs; desk grid with state and per-desk service time as interval aggregates (no officer names or IDs anywhere); lane-category waits; e-gate utilisation, reject reasons by category, and the predicted extra load on manual desks; a disabled button "Officer analytics open in AMAN" with a tooltip explaining that officer-level data stays in the border system.
3. **Check-in and handlers (`app/checkin.html`, Airport Operations module)**: island view by handler; per-counter state; waits per island; SLA compliance per 15-minute bin with provisional and final markers.
4. **SLA and penalties (`app/sla.html`)**: a contract card for Handler B (KPI definition, threshold of 15 minutes at P90 per 15-minute bin, evaluation window monthly, exclusions such as sensor outages and security directives, penalty schedule, signed zone profile version); an evaluation table for the month so far with breaches, exclusions applied and the resulting penalty; a dispute workflow (Raised, Under review, Upheld, Rejected) with a sample dispute; and an "Evidence pack" button that downloads a JSON file generated in the browser (interval data, zone profile version, calibration record, exclusions, and a content hash).
5. **Forecast and staffing (`app/forecast.html`)**: next 24 hours of demand per lane as stacked areas; recommended desks per 15 minutes against the planned roster, with gaps highlighted; predicted P90 wait under the plan and under the recommendation; "Accept recommendation" updates the simulation's staffing plan.
6. **Zones (`app/zones.html`)**: floor-plan editor where polygon vertices and entry and exit lines can be dragged; a profile list (v12 active, v13 draft); "Publish" creates v14 in memory and switches the live view to it; each result elsewhere shows which profile produced it.
7. **Devices (`app/devices.html`)**: sensor registry table (ID, type: stereo or LiDAR, zone, status, frame rate, clock offset, last calibration), the same sensors plotted on the floor plan, and S-17's outage history.
8. **Passenger display (`app/display.html`)**: a 16:9 full-screen board, bilingual Arabic and English, showing wait bands per checkpoint ("Passport control: 10 to 15 min"), updated with hysteresis (only when a full band changes), and a toggle that simulates stale data to show the neutral fallback message.
9. **Reports (`app/reports.html`)**: a daily report preview built around peaks (peak-hour waits by lane, forecast against actual, staffing against recommendation, e-gate performance); CSV export that really downloads; PDF via the browser's print.
10. **Access and data boundary (`app/access.html`)**: explains and demonstrates the role switcher. Roles: Border shift supervisor (sees immigration, no check-in handler data), Terminal duty manager (sees everything airport-side plus border waits as aggregates only), Handler B station manager (sees only Handler B's counters, SLA and disputes). Switching roles in the top bar changes what every screen shows; this page lists, per role, what is visible and what is not, and states that no screen shows officer identities.

## Create functionality

The prototype must let users create the objects a real deployment needs, not only watch. Every create flow follows one pattern.

Shared pattern:
- A "New ..." button opens a side drawer (full width on phones) with `role="dialog"`, `aria-modal="true"`, a focus trap, Esc to close and focus returned to the button. Labelled fields, sensible defaults, inline validation messages (required fields, ranges, formats, uniqueness, conflicts), a primary "Create" button and "Cancel". No browser dialogs.
- On success: a short in-page toast ("Created R-006") with Undo, and the new row highlighted in its list.
- Readable, deterministic IDs per object type (R-006, S-60, C-002, EX-003, OV-001, FL-X01, AL-004, RP-003, DSP-05, U-012).
- Created objects persist with the rest of the demo state and survive reloads and page changes. "Reset demo" clears them after an in-page confirmation.
- Every create, sign, publish, calibration and acknowledgement writes an audit entry: simulated time, role, action, object ID, summary. The audit log is shown on the access screen.
- Role gating: the button is visible but disabled with a one-line reason when the current role may not create that object ("Only the Terminal duty manager signs handler contracts"). The access screen lists create rights per role.
- Objects that should change the numbers feed the simulation, which recomputes the day deterministically. Added flights use their own sub-seeded random stream so existing flights' draws do not change. With no created objects and the default seed, the scripted events stay exactly as specified.

Create flows by screen:
1. **Alert rules (new screen, `app/rules.html`, MVP):** the rules that drive every alert. Seed the existing behaviour as rules (for example R-001 "Nowcast above 15 min" on immigration lanes, R-002 "Overflow band occupied", R-003 "Sensor offline over 5 min", R-004 "Check-in P90 above SLA threshold"), so the 18:05 alert comes from R-001. "New rule": name; scope (a queue or a group of queues visible to the role); metric (nowcast wait, realised P90 per 15-minute bin, people queuing, overflow band occupied, sensor offline, desks open below plan); condition and threshold with units; sustained for N minutes; severity (Info, Warning, Critical); owner role; escalate after N minutes to a role; channels (on-screen always; email addresses as plain text, marked "not sent in the demo"). Before creating, a backtest preview evaluates the rule against today's synthetic data: "Would have fired 3 times today, first at 18:05". Rules can be enabled, disabled and duplicated. Created rules fire live on the operations screen with their rule name.
2. **Zones:** "New draft profile" copies the active profile. "New zone" in the draft: type (snake queue, service area, overflow band, count line), name, level, linked queue; draw the polygon by clicking points on the plan (a vertex table offers a keyboard alternative), then place entry and exit lines for queue types. Validation: at least three points, no self-intersection, inside the level, entry and exit lines on the polygon edge, unique name. Publishing increments the version (v14, v15 and so on). A published new zone shows "Not measured: no calibrated sensor" until a calibrated sensor covers it.
3. **Devices:** "Register sensor": auto ID, type (stereo or LiDAR, from generic certified families, no brand names), zone, mounting height (2.5 to 16 m), position by clicking on the plan (or x and y fields), network address, clock source (NTP or PTP). The coverage footprint is drawn on the plan from the height, using the BOQ's assumed footprints (4 to 6 m: 10 x 10 m; 10 to 14 m: 12 x 9 m; other heights interpolated and labelled estimate; LiDAR as an assumed radius). A new sensor is "Commissioning" until "Record calibration": method (manual count comparison), sample size, counting accuracy, wait-time error, pass threshold (95% counting accuracy by default), notes. A pass sets it Online and covers its zone; the record appears in the evidence pack.
4. **SLA and penalties:** "New contract": party (Handler A or Handler B), scope (that handler's islands), KPI (P90 wait per bin, share of passengers under threshold, or overflow minutes), threshold, percentile, bin size, evaluation window, minimum passengers per bin, exclusions, monthly allowance, penalty per breached bin, monthly cap, dispute window, signed zone profile version (a published version), effective date. Draft, then "Sign" with an in-page confirmation (signed terms are locked; changes need a new contract version). Signed contracts are evaluated on today's live bins. "Add exclusion" (for example a security directive): type, zones, time window, reason, reference; evaluations recompute and mark excluded bins. Raising disputes stays as built.
5. **Forecast and staffing:** "Add roster override": queue, from and to (15-minute aligned), planned desks, reason; the plan, simulation and forecast update. A "Flight schedule (AODB, synthetic)" panel lists flights with "Add ad-hoc flight" for diversions, extra sections, or when the AODB feed is stale: code (validated, for example "DM 902"), arrival or departure, on-block time or STD, seats (120 to 400), expected load, lane mix (default or custom), handler for departures. It appears in the wave strip and changes demand.
6. **Check-in and handlers:** "Allocate counters": departing flight, island, counter range, open and close times (default STD minus 3 hours to STD minus 45 minutes). Overlapping allocations on a counter are rejected. Allocated counters show the flight code, and staffed counters in that window become at least the allocated count.
7. **Reports:** "New scheduled report": name, template (daily peaks, weekly service standard, monthly SLA and penalties, device health), scope limited to the role's view, schedule (daily at a time, weekly on a day, monthly on a date), format (CSV or PDF), recipients as plain text. The list shows the next run in simulated time; "Run now" opens the preview.
8. **Passenger display:** "Add display": name, level and location, orientation (landscape or portrait), checkpoints shown, language order, band size (5 or 10 minutes), hysteresis, stale threshold, fallback message in English and Arabic. A display selector renders the chosen board.
9. **Access and data boundary:** "Add user": fictional display name, organisation (border authority, airport operator, Handler B), role (only roles valid for that organisation), deployment (border or airport, derived and enforced: a border deployment user cannot hold an airport role, and the form says why), sign-in method, optional expiry. The user list offers "View as this user" (switches role). The audit log panel lives here.

Create rights (the demo presenter has all of them):
- Border shift supervisor: alert rules and roster overrides for immigration queues; immigration zones and sensors; scheduled reports within its view.
- Terminal duty manager: alert rules, roster overrides, zones and sensors airport-side; SLA contracts and exclusions; ad-hoc flights; counter allocations; displays; scheduled reports; decides disputes.
- Handler B station manager: roster overrides and counter allocations for islands C and D; disputes; scheduled reports within its view.
- Users: the demo presenter only (in a real deployment, each deployment's administrator).

## Links between the parts

- Business plan pages link to the matching prototype screens (differentiators, roadmap table, positioning).
- Each prototype screen has a small "Why this matters" link back to the relevant business plan page.

# Verification before you finish

1. Open every page in both parts (headless browser or Node with jsdom) and confirm there are no console errors.
2. Part 1 with defaults: sensors 59; hardware 316,350 (291,550 without optional items); software 210,000; services 139,975; year one 666,325; annual recurring 53,618 (rounded); five-year cost 880,795; Dalil contribution about 270,298; build cost 624,750; break-even arrays exactly as listed.
3. Part 2: with the default seed, the three scripted events fire at the stated simulated times; the same seed gives the same numbers on reload; role switching hides exactly what `access.html` says it hides; the evidence pack and CSV downloads produce valid files; the stale-data toggle on the passenger display shows the fallback message.
4. Create flows: each one creates, validates bad input with a visible message, persists across reload and page changes, respects role gating, writes an audit entry, and changes the simulation where specified (a new rule's backtest time matches when it fires live; a roster override, ad-hoc flight or counter allocation changes the forecast; a signed contract and an exclusion change the evaluation; a passed calibration sets the sensor Online). With no created objects, the scripted events are unchanged. "Reset demo" clears everything created.
5. Search all HTML and JS string literals for the em dash character and for double hyphens in visible text; there must be none.
6. Check pages at 360 px and 1280 px widths, and the passenger display at 1920 x 1080; check light and dark themes, and Part 1 in print preview.
7. Report the results of these checks, then list any figure you had to mark "to confirm".
