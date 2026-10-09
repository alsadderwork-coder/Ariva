# NBJ terminal BC1: the development-only seed site (NBJ-BC1)

Status: seeded only where `Seed:NbjSite` is true (on in `appsettings.service.vm-local.json`, off everywhere else) since ARV-139c (owner request 2026-10-09). The site models the border control halls of terminal BC1 of Dr. António Agostinho Neto International Airport (NBJ), Luanda, from the counts and measures of the terminal's 2018 design drawings. It is for local development and Angola-facing work only; the owner will remove it from the repository later.

## The drawings stay out of the repository

The source drawings are a third party's confidential work: each sheet carries the designer's copyright and confidentiality notice (no copying or disclosure to third parties without written permission). They are also security-sensitive for the border authority. So:

- The PDFs live only on the owner's machine, in `.private/angola/` at the repository root. `.private/` is in `.gitignore` (and in the owner's local `.git/info/exclude`), so `git add -A` cannot pick them up.
- Nothing rendered from them (crops, screenshots, traced images) is committed or uploaded as a floor plan. The seed draws its own schematic from the numbers below (`NbjBc1Plan.cs`).
- Show this site only to the Angolan stakeholders unless they agree otherwise in writing. For anyone else, use DMO or AUH-TA.

## What was read from the drawings

| Sheet | Floor | Read |
|---|---|---|
| ETP-ARQ-008 | Ground floor, international arrivals | 8 health counters, then arrivals immigration: one row of 13 double booths (2 desks each, 26 desks; owner-confirmed), 5 e-gate channels at the row's left end, a staff channel at its right end; about 22 m of open hall from the health counters to the booths; booth pitch about 3.5 m, booth about 2.6 m wide |
| ETP-ARQ-010 | Departures floor | Departures emigration: one row of 13 double booths (26 desks), 5 e-gate channels at the row's right end, after the security lanes; about 16 m of hall from the security exits; booth pitch about 3.7 m, booth about 2.8 m wide. A separate row of 7 positions further east (purpose not identified) is not modelled |
| ETP-ARQ-009 | Mezzanine | Arrival corridors with travelators of 33 to 88 m (rated 9,000 people an hour); not modelled, kept for a later scenario's walking times |

Measures are taken against the drawings' 15,000 mm structural grid, so they are approximate (to about half a metre). The drawings predate construction: the as-built halls may differ, which is why the site carries the "Illustrative, not surveyed" flag and banner.

Owner statements (2026-10-09): every booth holds two immigration desks; the narrow channels at the ends of the rows are e-gates; the halls have no lane segregation.

## What the seed creates

Source: `Platform/Backplane/Ariva.Infra/Services/Seed/NbjBc1Seed.cs`, with the layout in `NbjBc1Layout.cs` and the schematics in `NbjBc1Plan.cs`.

| Record | Value |
|---|---|
| Site | `NBJ-BC1`, flagged illustrative |
| Airport | IATA `NBJ`, time zone `Africa/Luanda` (no ICAO code seeded) |
| Terminal | `BC1` |
| Level ARR | "Ground floor, international arrivals", floor 0, 64 by 40 m modelled (the hall, not the terminal) |
| Checkpoint IMM | Immigration: desks `IM-01` to `IM-26` (desk 2b-1 the left half of booth b, desk 2b its right half), e-gates `EGA-01` to `EGA-05` |
| Level DEP | "Departures floor, international departures", floor 2 (assumption: ground, mezzanine, departures), 64 by 34 m modelled |
| Checkpoint EMI | Emigration: desks `EM-01` to `EM-26`, e-gates `EGD-01` to `EGD-05` |
| Lanes | Every desk on `ALL` (no segregation), every e-gate on `EG` |
| Queue zones | `A-ALL`, `A-EG`, `D-ALL`, `D-EG`, each with an entry line on the hall side and an exit line at the booths or gates |
| Overflow bands | `A-ALL-OV` and `D-ALL-OV`, behind the shared queues (assumption) |
| Desk zones (ARV-116) | `IM-01 staff` and `IM-01 service` to `EM-26 staff` and `EM-26 service`: the officer's half of the booth and the window in front of it, linked to the desk and hanging off the hall's shared queue |
| Capacities | Every queue and band: its area at one person per 1.2 m2 (assumption) |
| Sensors | Devices in Commissioning without a credential: a 10 by 10 m grid over every queue and band (`Q-A-nn`, `O-A-nn`, `G-A-nn` and the same with `D`) and one desk sensor over every two booths (`K-A-nn`, `K-D-nn`); overhead stereo at 5 m with the BOQ's assumed footprint (assumption) |
| Zone profile | Version 1, "NBJ BC1 border control v1 (illustrative)", over both levels, published by `demo-seed` |
| Floor plans | Ariva's schematic SVG per level at 20 pixels per metre, labelled "Illustrative, not surveyed" and "Schematic from 2018 design drawings" |

No AMAN desk codes, alert rules, users, roles, site grants or credentials, and no person, document or real organisation or system name (a unit test checks the seeded text).

## The lane gap

Ariva's border screens assume segregated lanes: the immigration view, the arrival wave split and AMAN contract V1 know `CIT`, `RES`, `VIS` and `CRW` (and `EG`). NBJ's halls are unsegregated, so the seed uses its own code `ALL`. The zones, desks, live waits per zone and the desk engine work with it; the per-lane parts of the immigration screen and the flight-driven forecasts do not know `ALL`, and how they show it has not been checked yet. A first-class "unsegregated hall" in the lane model is a backlog item for the owner, not part of ARV-139c.

## Not yet

- Live traffic: the simulator has no NBJ-BC1 scenario, so the live screens of this site stay empty until one is written (as ARV-139b did for AUH-TA).
- Check-in islands, security lanes and the health counters are drawn on the schematics only, not seeded as checkpoints.

## Turning it off or removing it

Set `Seed:NbjSite` to false (or delete the line) and the seed stops running; data already seeded stays in the local database until it is reset. To remove the site from the repository: delete `NbjBc1Layout.cs`, `NbjBc1Plan.cs`, `NbjBc1Seed.cs`, `NbjBc1SeedTests.cs` and this page, the `NbjSettingName` lines in `DemoSeedExtensions.cs`, the `NbjSite` line of `appsettings.service.vm-local.json`, and the ARV-139c story.
