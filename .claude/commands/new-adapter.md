---
description: Add a sensor device family adapter with its conformance kit
argument-hint: <family name> <transport> <tier T1-T4>
---
Add the device family $ARGUMENTS following docs/architecture/sensor-adapters.md and the sensor-adapters skill:
1. Verify the vendor's current data interface (use WebFetch on the vendor's documentation; record sources; label anything unverified).
2. Dialect mapper (coded or declarative) to canonical events; capability tier; registry entry.
3. Conformance kit under Platform/Simulation/Ariva.Simulation.Api/Emulators/Sensors/<Family>/samples with expected canonical events, plus malformed, oversized and clock-skewed cases.
4. Emulator support so the simulator can stream this dialect.
5. Tests: conformance (unit), ingest endpoint E2E (device auth, 401 without key, 413 oversize, 400 malformed).
6. Update wiki/09-Sensor-Catalogue-and-Adapters.md.
