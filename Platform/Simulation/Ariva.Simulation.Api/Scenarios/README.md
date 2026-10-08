# Scenarios

The simulator plays one simulated day per scenario site (ARV-139b: the scenario engine takes the site as a parameter), all on the same demo clock:

| Site | Scenario | Default seed | Scripted evening |
|---|---|---|---|
| `DMO` | The reference scenario: the prototype's seeded demo day at the fictional Demo International Airport, ported from `docs/design/prototype/app/assets/sim.js` (ARV-027) | 9303 | 18:05 the arrivals Visitors nowcast (A-VIS) passes 15 minutes; S-17 offline 18:20 to 18:30; 19:10 Handler B's check-in island C breaches its SLA |
| `AUH-TA` | The illustrative AUH Terminal A arrivals hall of the ARV-139a seed (`docs/demo/auh-terminal-a.md`), with an arrivals evening built from assumed flight waves (`Engine/AuhTerminalAScenarioSite.cs`) | 9304 | 18:12 the arrivals Visitors nowcast (A-VIS) passes 15 minutes; Q-RES-04 offline 18:25 to 18:35; from 19:12 the smart gates' queue (A-EG) spills into its band A-EG-OV |

Same seed, same output: the emulators in `Emulators/` (sensors, AODB, AMAN) replay these days deterministically, so demos, tests and documentation share reproducible days. See ADR-0025 in `docs/architecture/adr/`.

Scenario cases: `ScenarioConfig.OverflowEvening` (ARV-115) is the reference day with the arrivals Visitors snake (A-VIS) holding 60 people instead of 190, so its queue spills into the A-OV overflow band in both evening waves; the scripted events are unchanged. The reference day itself never fills a band. The sensor emulator splits a queue with a band at the run's snake capacity (`ScenarioConfig.Caps`, the site's defaults otherwise): the queue's lead sensor reports up to it and the band's lead sensor the rest. The golden replay tests use the case through `ReferenceReplay.RunOverflow` (locked in `Platform/Backplane/Ariva.UnitTests/Replay/replay-overflow.json`); the running simulator still plays the reference day.

## The engine (`Engine/`)

| File | What it holds |
|---|---|
| `ScenarioMath.cs` | mulberry32, mix32, h3, FNV-1a for flight IDs, JavaScript rounding (halves up) and a non-negative modulo, all bit for bit |
| `ScenarioSite.cs` | `ScenarioSite`, what the engine knows of a site (queues and servers, staffing areas, arrival lanes and how a flight's passengers split between them, minimum staffing, sensors and their roles, overflow bands, outages, roster lines, servers out of service, seeded rules and snake capacities, the flights a seed draws); `DmoScenarioSite`; the `ScenarioSites` registry |
| `ScenarioModel.cs` | The reference site's data: synthetic parameters, the 16 queues and their servers, the five staffing areas, the 59 sensors, today's outage, the schedule banks and the scripted evening; the clock constants every site shares |
| `AuhTerminalAScenarioSite.cs` | The AUH-TA site: the seed's lanes, counters, smart gates and 84 sensors, and the scenario's assumptions (below) |
| `ScenarioSchedule.cs` | The reference site's seeded flights (two streams) plus the scripted ones; ad-hoc flights draw from their own stream |
| `ScenarioDay.cs` | One run of a site: roster, server behaviour, the per-minute backlog recursion and the cumulative curves |
| `ScenarioDay.Views.cs` | Live state and nowcast, servers, 15-minute bins and P90s, wait series, e-gates |
| `ScenarioDay.Forecast.cs` | Expected and day-ahead runs, the Monte Carlo forecast, staffing recommendations, demand and hall curves |
| `ScenarioDay.Rules.cs` | Alert rules (the reference site's R-001 to R-005, AUH-TA's R-001 to R-003) and their evaluation |

Parity with the prototype is exact, not approximate. `scripts/simulation/reference-golden.mjs` runs sim.js for six cases (the reference day, other seeds, the extremes of the seed range, and a day with ad-hoc flights, overrides and an accepted recommendation) and writes SHA-256 fingerprints of every output family to `Platform/Backplane/Ariva.UnitTests/Simulation/reference-golden.json`; `ScenarioParityTests` requires the port to match all of them. `node scripts/verify.mjs unit` fails if the golden file no longer matches sim.js. Change sim.js, then regenerate the golden file and make the port match. Taking the site as a parameter changed none of the reference site's outputs: the parity fingerprints, the golden replay (3cce63edfb17660f53a3fb7eead6c8ae92b20c491135b406a7df8cb032199a6f over 3,202 outputs) and the overflow case (d971a3170539758f5248bfcc01e181e3bfd9ab1aa51dfba638d8908be9948b9a over 1,692 outputs) are unchanged.

Two details keep the numbers identical on every platform. The check-in show-up curve uses `Math.pow`, which differs in the last bit between V8 and the platform's libm for some inputs, so `ScenarioModel.Show` stores the prototype's own float64 bits (`ShowShape()` recomputes the curve, and a test checks the two agree within a few ULP). Sorting is stable wherever the prototype relies on it. The AUH-TA day uses neither (it has no check-in), and its fingerprints are locked in `Platform/Backplane/Ariva.UnitTests/Simulation/auh-terminal-a-golden.json` (`AuhTerminalAScenarioTests`).

## The AUH-TA scenario (ARV-139b)

From the ARV-139a seed (owner-accepted assumptions, 2026-10-07): counters IC-01 to IC-38 split CRW 2, DIP 1, CIT 4, RES 6, GCC 6, VIS 17 and TRF 2; smart gates SG-01 to SG-34 on lane EG only; queue zones `A-CRW`, `A-DIP`, `A-CIT`, `A-RES`, `A-GCC`, `A-VIS`, `A-TRF`, `A-EG`; bands `A-VIS-OV` and `A-EG-OV`; the seed's 84 sensors, each on its lane's queue zone: `Q-<lane>-nn` over a queue (the first counts it), `O-<lane>-nn` over a band (the first reports it), `D-<lane>-nn` over up to four counters (reports their staff and service zones, `IC-nn staff` and `IC-nn service`), the rest heartbeats. A unit test checks the codes and zones against `AuhTerminalALayout`.

Everything else is an ASSUMPTION of this scenario, labelled in the source, none of it the airport's data:

| Assumption | Value |
|---|---|
| Arrivals only | No departures, check-in or security |
| Carriers (fictional codes; any match with a real airline is coincidental) | `HC` hub carrier, mixed network (citizens 15 %, residents 45 %, GCC 5 %, visitors 30 %, crew 1.5 %, diplomats 0.5 %, transfer counters 3 %); `RG` regional, GCC routes (16, 31, 30, 19, 2, 1, 1 %); `LV` long-haul leisure (4, 20, 2, 70, 1.5, 0.5, 2 %). The loads are the passengers who reach the arrivals hall: transfers who stay airside are not counted, and the TRF counters serve the few who pass immigration to connect |
| Flight waves (on-block) | 10 flights 00:00 to 02:30, 12 from 05:00 to 08:00, 9 from 09:00 to 14:00, 7 from 15:00 to 17:30, 4 through the evening, 6 from 20:00 to 22:00, 10 from 22:00 to 23:50; carrier 50 % HC, 25 % RG, 25 % LV; 174 to 380 seats; booked and actual loads 70 to 95 %; delays up to 20 minutes; walks of 10 to 18 minutes (14 in the forecasts) |
| The scripted evening's flights | Visitor-heavy wave: `LV 401` (on-block 17:51), `LV 518` (17:59). Resident-heavy hub wave: `HC 205` (18:35), `HC 311` (18:40), `RG 129` (18:44), `HC 427` (18:48), `RG 216` (18:53), `RG 340` (18:57) |
| Smart-gate share | Citizens 75 %, residents 55 %, GCC nationals 40 %, visitors 5 % (eligible nationals); rejected attempts (7 % on average) join the visitors' counters |
| Service time per person | Crew 25 s, diplomatic 45 s, citizens 30 s, residents 40 s, GCC 35 s, visitors 80 s, transfer 50 s, smart gates 20 s |
| Staffing | A roster from the day-ahead demand with at least 4 visitors' counters, 2 each for citizens, residents and GCC nationals, 1 elsewhere, and every smart gate; the evening shift handover leaves 11 of 17 visitors' counters open from 17:30 to 18:30 and 15 until 19:00; residents' and GCC counters fully open from 18:30 to 19:45 for the hub wave |
| The smart gate fault | SG-05 to SG-34 out of service from 18:40 to 19:30 (4 gates in service) |
| The sensor outage | `Q-RES-04` (a heartbeat sensor over the residents' queue) offline from 18:25 to 18:35 |
| Snake capacities | The seed's area capacities (one person per 1.2 m2), except the snakes in front of the bands: `A-VIS` 300 and `A-EG` 250 people (the seed's zones hold 947 and 994, which no plausible evening fills) |

The scripted events, seed 9304 (`AuhTerminalAScenarioTests` reproduces them; the scenario's alert rules R-001 nowcast above 15, R-002 overflow band occupied, R-003 sensor offline are the reference site's first three, on AUH-TA's zones):

| Time | Event | In the scenario | As Ariva measures it (`AuhTerminalAReplayTests`, the emulator's pushes through Ingest and the zone engine) |
|---|---|---|---|
| 18:12 | Nowcast breach: the visitor wave meets the shift handover | A-VIS nowcast above 15 minutes from 18:12 (R-001 18:12 to 18:34; peak about 29 minutes at 18:20) | Above 15 minutes from 18:12, the scenario's minute: Ariva's nowcast divides the measured queue by the exits |
| 18:25 to 18:35 | Sensor outage | Q-RES-04 sends nothing; A-RES degraded, its wait shown as a band (R-003 18:25 to 18:35) | Device outage from 18:25 until it is heard again at 18:36; A-RES degraded meanwhile |
| 19:12 to 19:24 | Overflow | The smart gate fault meets the hub wave: A-EG above its 250-person snake (R-001 on A-EG 19:08 to 19:30, R-002 19:14 to 19:27; peak 308 people) | Band A-EG-OV occupied from 19:13, emptied at 19:26 |

The same evening plays with any seed: other seeds move the background flights and shift the breach and the overflow by a few minutes; the outage is fixed. The day's other alerts with seed 9304 are two short visitors' breaches in the background waves (06:25 and 15:49).

The AMAN emulator does not play AUH-TA (decision of ARV-139b, to confirm with the owner): AUH-TA has no AMAN desk codes, its counters' states come from their staff and service zones alone by the owner's decision (ARV-116 desk zones placed in the AUH-TA seed), an AMAN desk session would take precedence over those sensors (desk state precedence), and contract V1's lanes (CIT, RES, VIS, CRW) have no GCC, DIP or TRF (ARV-130's concern). The AODB emulator does play AUH-TA, with a client of its own.

## Endpoints

All under `api/v1/simulation/scenario`, all behind an operator key. Minutes are clock minutes of the demo day, 0 (00:00) to 1439 (23:59). `site` is a scenario site, `DMO` when not given; a site the simulator does not play is 404, never echoed.

| Method and path | Scope | Answer |
|---|---|---|
| `GET ?site=` | read | Seed, date, site, counts, alerts, when and by whom the site's day was last run |
| `GET sites` | read | Every scenario site's summary (DMO, AUH-TA) |
| `GET queues?minute=&site=` | read | Every queue of the site: length, throughput, open and paused servers, nowcast (a band when the zone is degraded) |
| `GET queues/{queue}?minute=&site=` | read | One queue (404 for a queue the site does not have) |
| `GET sensors?minute=&site=` | read | Every sensor of the site, whether it is offline, the queue zone of its device and its role |
| `GET alerts?site=` | read | Every alert of the site's day under its seeded rules |
| `PUT` `{ "seed": n, "site": "AUH-TA" }` | control | Re-runs a site's scenario (DMO without `site`) with seed n (0 to 4294967295); six re-runs a minute per key by default (429 beyond), one at a time; a body without a seed is refused |

Seeds: `Simulation:Sites:{site}:Seed` (`Simulation:Sites:AUH-TA:Seed`, default 9304); the reference site also reads `Simulation:Seed` (default 9303). An out-of-range seed stops the host at start.

Operator keys live in `Simulation:Control:Keys` in the simulation appsettings secret: a name (for the audit log), the SHA-256 of the key as lower-case hex (never the key itself) and scopes `read` and or `control`. A request sends `Authorization: Bearer <key>`; the key is hashed and compared in constant time with every configured digest. With no key configured nothing authenticates. The days live in one replica (`simulationHpaMax` stays 1), and the simulator still refuses to start in k8s-prd.
