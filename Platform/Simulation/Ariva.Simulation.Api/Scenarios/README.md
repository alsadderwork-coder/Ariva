# Scenarios

The reference scenario is the prototype's seeded demo day: seed 9303 at the fictional Demo International Airport (DMO), with scripted events at 18:05, 18:20 to 18:30, and 19:10. It is ported from `docs/design/prototype/app/assets/sim.js`.

Same seed, same output: the emulators in `Emulators/` (sensors, AODB, AMAN) replay this day deterministically, so demos, tests and documentation share one reproducible day. See ADR-0025 in `docs/architecture/adr/`.
