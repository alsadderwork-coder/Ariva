# ADR-0025: Simulation host and reference scenario

- Status: Accepted
- Date: 2026-10-01
- Source: Product owner decision, 2026-10-01; D6 Phase 0; product decision 11

## Context

Phase 0 must run end to end without a site (D6). AODB, AMAN and sensor access come later. The prototype (D8, the business plan site and simulated prototype) already defines a deterministic demo day.

## Decision

Ariva.Simulation.Api (port 51020) hosts sensor, AODB and AMAN emulators. The reference scenario is the prototype's seeded day: seed 9303, with scripted events at 18:05 (a visitor-heavy arrival wave pushes the arrivals Visitors nowcast above 15 minutes and an alert fires), 18:20 to 18:30 (sensor S-17 over the arrivals hall offline; the Visitors zone is degraded and shows a band) and 19:10 (Handler B's check-in island C breaches its SLA after a shift change leaves 5 of 12 counters open). Same seed, same output. AODB emulation covers AIDX 22.1, ACRIS and SSIM import.

## Consequences

- Demos, tests and documentation share one reproducible day; golden-file tests can lock behaviour.
- Risk of tuning to synthetic data. Mitigated by recording two to four lab sensors in Phase 0 and real feeds at the pilot.

## Alternatives considered

- Wait for real feeds. Rejected (decision 11).
- Random scenarios without a fixed seed. Rejected: not reproducible.
