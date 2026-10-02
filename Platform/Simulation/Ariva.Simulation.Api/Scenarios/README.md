# Scenarios

The reference scenario is the prototype's seeded demo day: seed 9303 at the fictional Demo International Airport (DMO), with scripted events at 18:05, 18:20 to 18:30, and 19:10. It is ported from `docs/design/prototype/app/assets/sim.js` (ARV-027).

Same seed, same output: the emulators in `Emulators/` (sensors, AODB, AMAN) replay this day deterministically, so demos, tests and documentation share one reproducible day. See ADR-0025 in `docs/architecture/adr/`.

## The engine (`Engine/`)

| File | What it holds |
|---|---|
| `ScenarioMath.cs` | mulberry32, mix32, h3, FNV-1a for flight IDs, JavaScript rounding (halves up) and a non-negative modulo, all bit for bit |
| `ScenarioModel.cs` | Synthetic parameters, the 16 queues and their servers, the five staffing areas, the 59 sensors, today's outage, the schedule banks and the scripted evening |
| `ScenarioSchedule.cs` | The seeded flights (two streams) plus the scripted ones; ad-hoc flights draw from their own stream |
| `ScenarioDay.cs` | One run: roster, server behaviour, the per-minute backlog recursion and the cumulative curves |
| `ScenarioDay.Views.cs` | Live state and nowcast, servers, 15-minute bins and P90s, wait series, e-gates |
| `ScenarioDay.Forecast.cs` | Expected and day-ahead runs, the Monte Carlo forecast, staffing recommendations, demand and hall curves |
| `ScenarioDay.Rules.cs` | Alert rules R-001 to R-005 and their evaluation |

Parity with the prototype is exact, not approximate. `scripts/simulation/reference-golden.mjs` runs sim.js for six cases (the reference day, other seeds, the extremes of the seed range, and a day with ad-hoc flights, overrides and an accepted recommendation) and writes SHA-256 fingerprints of every output family to `Platform/Backplane/Ariva.UnitTests/Simulation/reference-golden.json`; `ScenarioParityTests` requires the port to match all of them. `node scripts/verify.mjs unit` fails if the golden file no longer matches sim.js. Change sim.js, then regenerate the golden file and make the port match.

Two details keep the numbers identical on every platform. The check-in show-up curve uses `Math.pow`, which differs in the last bit between V8 and the platform's libm for some inputs, so `ScenarioModel.Show` stores the prototype's own float64 bits (`ShowShape()` recomputes the curve, and a test checks the two agree within a few ULP). Sorting is stable wherever the prototype relies on it.

## Endpoints

All under `api/v1/simulation/scenario`, all behind an operator key. Minutes are clock minutes of the demo day, 0 (00:00) to 1439 (23:59).

| Method and path | Scope | Answer |
|---|---|---|
| `GET` | read | Seed, date, site, counts, alerts, when and by whom the day was last run |
| `GET queues?minute=` | read | Every queue: length, throughput, open and paused servers, nowcast (a band when the zone is degraded) |
| `GET queues/{queue}?minute=` | read | One queue (404 for an unknown queue) |
| `GET sensors?minute=` | read | Every sensor and whether it is offline |
| `GET alerts` | read | Every alert of the day under the seeded rules |
| `PUT` `{ "seed": n }` | control | Re-runs the reference scenario with seed n (0 to 4294967295); six re-runs a minute per key by default (429 beyond), one at a time; a body without a seed is refused |

Operator keys live in `Simulation:Control:Keys` in the simulation appsettings secret: a name (for the audit log), the SHA-256 of the key as lower-case hex (never the key itself) and scopes `read` and or `control`. A request sends `Authorization: Bearer <key>`; the key is hashed and compared in constant time with every configured digest. With no key configured nothing authenticates. The day lives in one replica (`simulationHpaMax` stays 1), and the simulator still refuses to start in k8s-prd.
