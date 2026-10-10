# NBJ terminal BC1: the development-only seed site (NBJ-BC1)

Status: seeded only on the owner's machine, in a run in which the owner turns it on (see "Turning it on": `run-ariva.ps1 -NbjSite`, or Ariva.Api.Main by hand with `Seed__NbjSite=true`). No committed setting turns it on; CI, the E2E suite, the scripted demo (`scripts/demo-local.mjs`) and Codespaces never seed it, and a Codespace or a cloud agent session refuses it. The pins that keep it off stop a run from seeding the site; they do not remove a site already seeded: a database that holds it keeps it until it is reset, and the E2E suite refuses such a database. The seed's code is compiled into Debug builds only; Release builds and images carry none of it. Since ARV-139c (owner request 2026-10-09; scope narrowed by the owner's decisions of 2026-10-09 after the security review). The site models the border control halls of terminal BC1 of Dr. António Agostinho Neto International Airport (NBJ), Luanda, from the counts and measures of the terminal's 2018 design drawings. It is for local development and Angola-facing work only; the owner will remove it from the repository later.

## The drawings stay out of the repository

The source drawings are a third party's confidential work: each sheet carries the designer's copyright and confidentiality notice (no copying or disclosure to third parties without written permission). They are also security-sensitive for the border authority. So:

- The PDFs live only on the owner's machine, in `.private/angola/` at the repository root. `.private/` is in `.gitignore` (and in the owner's local `.git/info/exclude`), so `git add -A` cannot pick them up.
- Nothing rendered from them (crops, screenshots, traced images) is committed or uploaded as a floor plan. The seed draws its own schematic from the numbers below (`NbjBc1Plan.cs`).
- What is derived from them (the seed's counts and measures) is development-only: `Ariva.Infra.csproj` compiles `NbjBc1Layout.cs`, `NbjBc1Plan.cs` and `NbjBc1Seed.cs` only in Debug builds, so no Release build or image carries it (`NbjSiteScopeTests` proves it on the Release Ariva.Infra.dll in CI's backend job), and a host with `Seed:NbjSite` on refuses to start unless it is a Debug build whose host environment and `Application:Environment` are both vm-local, outside a Codespace or a cloud agent session.
- Show this site only to the Angolan stakeholders unless they agree otherwise in writing. For anyone else, use DMO or AUH-TA.

## What was read from the drawings

| Sheet | Floor | Read |
|---|---|---|
| ETP-ARQ-008 | Ground floor, international arrivals | 8 health counters, then arrivals immigration: one row of 13 double booths (2 desks each, 26 desks; owner-confirmed), 5 e-gate channels at the row's left end, a staff channel at its right end; about 22 m of open hall from the health counters to the booths; booth pitch about 3.5 m, booth about 2.6 m wide |
| ETP-ARQ-010 | Departures floor | Departures emigration: one row of 13 double booths (26 desks), 5 e-gate channels at the row's right end, after the security lanes; about 16 m of hall from the security exits; booth pitch about 3.7 m, booth about 2.8 m wide |

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
| Floor plans | Ariva's schematic SVG per level at 20 pixels per metre, labelled "Illustrative, not surveyed" and "Schematic from 2018 design drawings": arrivals draws the 8 health counters upstream of the hall; departures draws one unlabelled band there, with no lanes and no count (assumption); stored unchanged by the floor plan inspection |

No AMAN desk codes, alert rules, users, roles, site grants or credentials (integration tests check the database), and no person, document or organisation name: a unit test checks that every seeded text (site, terminal, level, checkpoint, desk, zone, line and sensor names and codes, plan labels and file names) uses only an allowlisted vocabulary of Ariva's own words, the codes above and the airport's name and time zone, and that no sheet id of the drawings appears.

## Assumptions

- Each level's modelled extent (the hall, not the whole terminal) and the departures floor number (2).
- The queue and overflow depths, the overflow bands behind the shared queues, and the service and staff zones' sizes.
- Capacities: one person per 1.2 m2 of queue or band.
- Sensors: overhead stereo at 5 m with the BOQ's assumed 10 by 10 m footprint.
- Departures schematic: the area upstream of the hall is one unlabelled band, with no lanes and no count. Security checkpoints are out of scope, so nothing of them is drawn.
- Every code is Ariva's own, never the airport's or a border system's.

## Turning it on (owner's machine only)

Nothing committed turns it on: the committed settings files, the Helm values, the AppHost, the E2E environment, the scripts, CI, the agents' settings and hooks and the dev container leave it off (`NbjSiteScopeTests` fails otherwise, and allows only the switch's own lines in `run-ariva.ps1`, `AppHost.cs` and `AppHostSettings.cs`). The Aspire AppHost and the E2E suite give api-main `Seed__NbjSite=false` in every run but the switched one, after any host variables file. Both ways below are for one run, in a Debug build, on your own machine; a Codespace or a cloud agent session refuses it.

### run-ariva.ps1 -NbjSite

```
.\run-ariva.ps1 -NbjSite
```

- The switch reaches Ariva.AppHost only as its own argument, `--AppHost:NbjSite=true`; nothing is set in the PowerShell session, and a `Seed__NbjSite` variable in the session or in `appsettings.local.json` changes nothing (the AppHost's explicit value wins).
- The AppHost then refuses `AppHost:HostEnvironmentFile` (the E2E suite's and the scripted demo's settings) and `AppHost:Persistent=false` (an E2E run). The script gives it the demo accounts as `AppHost:AccountsFile` instead, of which only api-main's sign-in variables are applied (`Auth__TotpRequired`, `Auth__DevelopmentUsers__n__*`): the accounts in the script's table sign in as usual, the scripted demo's simulator key is not applied, and `-Demo` is refused with the switch.
- The run uses a separate database volume, `ariva-apphost-timescaledb-nbj`: Aspire recreates the persistent database container on it, so the run starts from its own database, where DMO, AUH-TA and NBJ-BC1 are seeded. Your usual data, in `ariva-apphost-timescaledb` (the volume the scripted demo presents), is neither shown nor changed: what you set up there (zone edits, sensors, alert rules, validation campaigns) is not in the switched run, and what you do in the switched run stays in its own volume for the next switched run. The next run without the switch puts the usual volume back. To discard the switched run's data: `docker volume rm ariva-apphost-timescaledb-nbj`, with the container stopped.
- Persistent containers outlive the AppHost: after a switched run the database container keeps running on the separate volume, on the usual port 5433. So that no later run shows the site by attaching to it, `run-ariva.ps1` refuses to start, in every mode and with or without the switch, while a container uses `ariva-apphost-timescaledb-nbj` (`docker ps -q --filter volume=ariva-apphost-timescaledb-nbj`): close the switched run's AppHost window and `docker stop` the container it names, then run the script again. Stop it as well before `node scripts/dev-up.mjs`, a by-hand run or a local E2E run.

### By hand

```
Seed__NbjSite=true dotnet run --project Platform/Backplane/Ariva.Api.Main
```

In PowerShell, set `$env:Seed__NbjSite = 'true'` in a window used only for that run, then `Remove-Item Env:Seed__NbjSite`. `Seed:DemoTopology` must be on too (the committed vm-local settings have it on); with it off, nothing is seeded. Never export the variable from a shell profile. This route has no separate volume: it writes into the database `appsettings.local.json` names, the dev-up database on localhost:5433, which local E2E runs also use. From then on the E2E global setup refuses that database until it is reset: `node scripts/dev-down.mjs --volumes`, then `node scripts/dev-up.mjs`. The E2E check (`tests/support/development-site.ts`) fails closed: it reads the same `Database__*` variables the hosts of the run get and stops without `Database__Port` and `Database__Name`, and it stops when the database it reached has no site table, since it runs after the hosts migrated theirs (`tests/api/development-site.spec.ts` covers it).

The repository's git-ignored local settings file, `Platform/Backplane/Ariva.Api.Common/appsettings.local.json`, would also take `"Seed": { "NbjSite": true }`, but it is not the way to do it: `node scripts/dev-up.mjs` rewrites the whole file on every run, and it is copied into every host's build output, so every host started by hand from that build would read it (the AppHost and the E2E suite still override it to false).

A host with the setting on refuses to start anywhere else: outside vm-local (host environment or `Application:Environment`), in a Release build, or in a Codespace or a cloud agent session (`CODESPACES` or `CLAUDE_CODE_REMOTE` true), with a message that names the setting but no site.

## The lane gap

Ariva's border screens assume segregated lanes: the immigration view, the arrival wave split and AMAN contract V1 know `CIT`, `RES`, `VIS` and `CRW` (and `EG`). NBJ's halls are unsegregated, so the seed uses its own code `ALL`. The zones, desks, live waits per zone and the desk engine work with it; the per-lane parts of the immigration screen and the flight-driven forecasts do not know `ALL`, and how they show it has not been checked yet. A first-class "unsegregated hall" in the lane model is a backlog item for the owner, not part of ARV-139c.

## Not yet

- Live traffic: the simulator has no NBJ-BC1 scenario, so the live screens of this site stay empty until one is written (as ARV-139b did for AUH-TA).
- The health counters are drawn on the arrivals schematic only, not seeded as a checkpoint. Check-in and security checkpoints are neither drawn nor seeded.

## Turning it off or removing it

Run without the switch or the variable and the seed stops running. Data already seeded stays where it was written until that database is reset: the separate volume of the switched AppHost run, or the dev-up database after a by-hand run (see above). To remove the site from the repository: delete `NbjBc1Layout.cs`, `NbjBc1Plan.cs`, `NbjBc1Seed.cs`, `NbjBc1SeedTests.cs`, `NbjSiteScopeTests.cs` and this page; the `NbjSettingName`, `NbjEnvironment`, `HostedMachineMarkers` and `NbjSeedCompiled` parts of `DemoSeedExtensions.cs`; the NBJ tests in `IllustrativeSeedTests.cs` and their test databases in `PostgresFixture.cs`; the "Development-only seed" blocks of `Ariva.Infra.csproj`, `Ariva.Di.csproj`, `Ariva.UnitTests.csproj` and `Ariva.IntegrationTests.csproj` (the `ARIVA_DEV_SEED` symbol); the switch in `run-ariva.ps1`, its part of `AppHostSettings.cs` (the development-only site region) and `AppHost.cs`, and its cases in `AppHostModelTests.cs`; the `Seed__NbjSite` pin in `playwright.config.ts` and the check in `tests/support/global-setup.ts`; and the ARV-139c story.
