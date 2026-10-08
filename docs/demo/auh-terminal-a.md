# AUH Terminal A arrivals: the illustrative demo site (AUH-TA)

Status: seeded in development and demo deployments since ARV-139a (owner request 2026-10-06). The site is modelled on a real airport, Zayed International Airport (AUH) in Abu Dhabi, Terminal A, from public information only. There is no real plan and no survey: an illustrative sample is accepted (owner decision 2026-10-06). The site carries the flag "Illustrative, not surveyed", and every screen of the site and every passenger display of the site shows it as a banner, in English and Arabic.

Naming a real airport in demo data is for internal demos and pitches to that airport only, with care; the banner stays on.

## What the seed creates

Source: `Platform/Backplane/Ariva.Infra/Services/Seed/AuhTerminalASeed.cs`, with the layout in `AuhTerminalALayout.cs` and the drawn plan in `AuhTerminalAPlan.cs`. Every number below that is not in the table of public facts is an assumption, and the source says so next to it.

| Record | Value | Fact or assumption |
|---|---|---|
| Site | `AUH-TA`, "Zayed International Airport, Terminal A arrivals (illustrative)", flagged illustrative | Name from the public facts; code is Ariva's |
| Airport | IATA `AUH`, "Zayed International Airport (Abu Dhabi)", time zone `Asia/Dubai` | Public (IATA code and city); no ICAO code is seeded |
| Terminal | `A`, "Terminal A" | Public |
| Level | `ARR`, "Arrivals, lower level", floor 0, 200 by 150 metres | Lower level is public; floor number and extent are assumptions (the modelled part only, not the whole terminal) |
| Checkpoint | `IMM`, "Arrivals immigration", kind Immigration | Public (arrivals immigration on the lower level) |
| Counters | 38 desks `IC-01` to `IC-38` | Count reported (unverified); codes, pitch (2.4 m) and positions are assumptions |
| Smart gates | 34 e-gates `SG-01` to `SG-34`, lane `EG` | Count reported (unverified); codes, pitch (1.2 m) and positions are assumptions; every gate on lane `EG` only is an assumption accepted by the owner for the demo, 2026-10-07 |
| Lanes by counter | `CRW` IC-01 and IC-02, `DIP` IC-03, `CIT` IC-04 to IC-07, `RES` IC-08 to IC-13, `GCC` IC-14 to IC-19, `VIS` IC-20 to IC-36, `TRF` IC-37 and IC-38 | Assumption (the split is not public); accepted by the owner for the demo, 2026-10-07 |
| Queue zones | `A-CRW`, `A-DIP`, `A-CIT`, `A-RES`, `A-GCC`, `A-VIS`, `A-TRF`, `A-EG`, each with its lane, an entry line on the hall side and an exit line at the counters or gates | Lanes are from the public facts where cited (smart gates for residents and eligible nationals, most first-time visitors at a staffed counter); geometry is an assumption |
| Transfer lane | `A-TRF` | Assumption: transfer flows normally bypass arrivals immigration; kept so the demo shows a lane of that kind |
| Overflow bands | `A-VIS-OV` (feeds A-VIS) and `A-EG-OV` (feeds A-EG), each with an overflow entry line | Assumption |
| Desk zones (ARV-116) | `IC-01 staff` and `IC-01 service` to `IC-38 staff` and `IC-38 service`: a staff zone behind and a service zone in front of every counter, linked to its desk and hanging off its lane's queue | Assumption (positions); smart gates have none, as the stream leaves e-gates' desk zones out |
| Capacities | Every queue and band: its area at one person per 1.2 m2 | Assumption |
| Sensors | 84 devices in Commissioning, without a credential: a grid of 10 by 10 m footprints over every queue and band (`Q-<lane>-nn`, `O-<lane>-nn`) and one desk sensor over every run of up to four counters of a lane (`D-<lane>-nn`); overhead stereo at 5 m with the BOQ's assumed footprint; model "Overhead stereo counter (illustrative)" | Assumption |
| Zone profile | Version 1, "AUH Terminal A arrivals v1 (illustrative)", published by `demo-seed`: 86 zones (8 queues, 2 bands, 38 staff, 38 service) and 18 lines | Assumption |
| Floor plan | A schematic SVG at 10 pixels per metre (0.1 m per pixel, origin at the top left), 2000 by 1500 pixels, labelled "Illustrative, not surveyed" | Drawn by the seed; the straight line from immigration to baggage claim, customs and landside transport is public, everything else is an assumption |

The seed creates no AMAN desk codes (the counters' states come from their staff and service zones alone, ARV-116), no alert rules, no users, roles, site grants or credentials, and no officer, traveller or document identity or real staff or system name (a unit test checks the seeded text).

Live traffic for AUH-TA comes from the simulator's AUH-TA scenario (ARV-139b, next section).

## The AUH-TA scenario (ARV-139b)

`Ariva.Simulation.Api` plays an arrivals evening for AUH-TA beside DMO's reference evening, on the same demo clock: its own default seed 9304 (`Simulation:Sites:AUH-TA:Seed`), the seed's lanes, counters, smart gates and 84 sensors, and flight waves, a lane mix and a smart-gate share that are all ASSUMPTIONS of the scenario, labelled as such in `Platform/Simulation/Ariva.Simulation.Api/Scenarios/Engine/AuhTerminalAScenarioSite.cs` and listed in the simulator's `Scenarios/README.md` (fictional carriers HC, RG and LV; seven flight waves through the day; a visitor-heavy long-haul wave and a resident-heavy hub wave in the evening; smart-gate shares of 75 % of citizens, 55 % of residents, 40 % of GCC nationals and 5 % of visitors; service times per lane; an evening shift handover; snake capacities of 300 people in front of A-VIS-OV and 250 in front of A-EG-OV). None of it is the airport's data.

The scripted evening (seed 9304; the scenario's tests and Ariva's own replay of it reproduce every event):

| Time | Event | What the screens show |
|---|---|---|
| 18:12 | Nowcast breach: the visitor-heavy wave lands while 11 of the 17 visitors' counters are open (shift handover) | A-VIS above 15 minutes (Ariva's live nowcast, from the exits, and the scenario's both from 18:12), peak about 29 minutes at 18:20, back under target by 18:34 |
| 18:25 to 18:35 | Sensor outage: Q-RES-04 over the residents' queue sends nothing | Q-RES-04 Offline, A-RES degraded (its wait as a band) until it is heard again at 18:36 |
| 19:12 to 19:25 | Overflow: a smart gate fault (SG-05 to SG-34 out of service from 18:40 to 19:30) meets the resident-heavy hub wave | A-EG above 15 minutes from 19:08; its band A-EG-OV occupied from 19:13 to 19:26 (peak 308 people against a 250-person snake) |

AUH-TA has no AMAN feed, so the waits on its screens are estimates from the exit rate, shown with the "≈" marker (F8; with sensor-derived desks there is no cycle time, ARV-117), and they read a little higher than the scenario's own nowcast at the peak (in real-time runs of the pipeline A-VIS peaked at 40 to 45 minutes against the scenario's 29).

How the demo runs it, without weakening device authentication (CWE-287, CWE-306): the seed leaves the 84 sensors in Commissioning without a credential. `node scripts/demo-local.mjs start` (and `run-ariva.ps1 -Demo`) signs in as `demo.admin` with its second factor, issues each sensor the credential it plays with through the devices API (`POST api/v1/admin/devices/{id}/credential`, a critical action that needs the recent second factor; Ariva keeps only its hash and shows it once), records its calibration (`POST api/v1/admin/devices/{id}/calibrations`, which moves it out of Commissioning) and loads it into the simulator with its site; it also gives AUH-TA the scenario's three alert rules (nowcast above 15 minutes, overflow band occupied, sensor offline) through the alert rules API, once, while the site has none. These are the same paths an installer and an administrator take, the same the DMO demo uses for its own devices. The seed itself is unchanged: still no credential, calibration or rule. `--sites DMO` or `--sites AUH-TA` plays one site only.

The emulated AODB plays AUH-TA with a client of its own (airport AUH, terminal A, site AUH-TA; an integration client registered for AUH-TA with `flights:write`, loaded with `PUT api/v1/simulation/feeds/clients` as `aodbSites`). The AMAN emulator does not play AUH-TA: the site has no AMAN desk codes, its counters' states come from their staff and service zones alone (the owner's 2026-10-07 decision to place the ARV-116 desk zones here), an AMAN desk session would take precedence over those sensors, and AMAN contract V1's lanes have no GCC, DIP or TRF (ARV-130). This is a decision to confirm with the owner.

## Owner decisions (2026-10-07)

Ahmad Al-Sadder decided, for the demo (docs/product/decisions.md):

- The lane split of the counters (CRW 2, DIP 1, CIT 4, RES 6, GCC 6, VIS 17, TRF 2) and every smart gate on lane `EG` only are kept. They remain assumptions, labelled as such here and in the seed, but are accepted for the demo.
- The display note "Illustrative, not surveyed" stays in English and Arabic only.

The other points stay as recorded on this page: each source's URL is to confirm, the operational demo users stay on DMO, and the flag can never be cleared.

## Public facts and their sources

Only these are facts; everything else is an assumption. The network the seed was written on blocked the publishers' sites, so no address was verified: each source is cited by its publication and subject, and every URL is to confirm before the page is shown outside Dalil.

| Fact | Source, as cited in the story | URL |
|---|---|---|
| Terminal A: 742,000 m2 built-up area; 45 million passengers a year; 11,000 passengers an hour; 79 aircraft stands at a time; opened November 2023; X-shaped design by KPF | Gulf News, WAM (Emirates News Agency) and KPF (the architect) | URL to confirm |
| Arrivals on the lower level: immigration, baggage claim and customs in a straight line, then landside transport | The National, BDC Network | URL to confirm |
| 38 immigration counters and 34 smart gates (reported, unverified; labelled "reported" wherever they appear) | Secondary travel guides: propertyfinder.ae, morafiq.ae, the airport's terminal guides | URL to confirm |
| Smart gates for UAE residents and eligible nationals; most first-time visitors at a staffed counter; biometric face and iris corridors since May 2026 | VisaHQ news, 2026-05-07 | URL to confirm |

The terminal's size, capacity, stands, opening and architect are context for the demo; the seed uses only the arrivals sequence, the lower level, the two reported counts and the lane facts. The biometric corridors are not modelled (no lane or gate kind for them; the smart gates stand for automated processing as a whole).

## The illustrative flag (CWE-269)

- `site.is_illustrative` (script `0046_site_illustrative.sql`) is set only when the seed inserts the site (`Site.CreateIllustrative`, which a unit test allows only in `Ariva.Infra/Services/Seed`).
- No request model carries it: the admin sites API ignores any `illustrative` member in a body on create and on update (E2E `tests/api/illustrative-site.spec.ts`), and no entity method changes it.
- The runtime database role cannot update it: script 0046 revokes UPDATE on `site` and grants it back on the columns a rename writes only (`name` and the `modified_*` stamps), so no statement of a running host can set or clear the flag (integration test `IllustrativeSeedTests`).
- The seed refuses to write into a site `AUH-TA` that exists without the flag (a real deployment's site of that code), and into terminal A of airport AUH when it belongs to another site.
- The web shows the banner from the sites API (`illustrative`) on every site screen, and the board API (`illustrative`) on the site's passenger displays; the text comes from the dictionaries (`site.illustrative.*`, board `illustrative`) by text interpolation only (CWE-79).

## Same guard as the DMO seed

The seed runs with the DMO seed (`Seed:DemoTopology`, vm-local in appsettings, the Helm value `demoSeed` in demo) after it, in its own unit of work and with its own retries. Api.Main refuses the setting unless both `DOTNET_ENVIRONMENT` and `Application:Environment` are `vm-local`, `k8s-dev` or `k8s-demo` (`DemoSeedExtensions`). It is idempotent: records are found by their codes and created only when missing; the zone profile only while the site has none (a draft someone started is left alone and v1 is skipped with a note); the plan only while the level never had one; the sensors only while the site never had any device and the published profile has their queue zones. A re-run creates, updates and audits nothing, and both seeds coexist (integration test `IllustrativeSeedTests`). Counts and geometry are bounded (CWE-120): at most 200 sensors, the profile's 500 zones and 1,000 lines, every vertex inside the level, every capacity within 1 to 5,000.

## Replacing the plan with a real one

The illustrative plan is an ordinary floor plan of level `ARR`. To show a real plan, for example one shared under NDA, an administrator or duty manager of AUH-TA uploads it at runtime on the Zones screen (or `PUT api/v1/admin/levels/{levelId}/floor-plan`, ARV-018) and calibrates it to two reference points (ARV-053). The upload replaces the illustrative plan (the old file is removed after commit), and the seed never puts its plan back: it adds one only while the level has never had a plan. The zones stay where they are in metres; move them to the real plan in a new draft and publish it.

A real plan is never committed to the repository, never added to the seed and never attached to a story, test or document: it lives only in the deployment's file storage. The banner stays while the site is AUH-TA, whatever plan it shows: the flag cannot be cleared. Real, surveyed geometry for a pilot belongs to a new site created through the admin API (which never carries the flag), not to AUH-TA.
