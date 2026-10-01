---
name: ariva-domain
description: Ariva's ubiquitous language, bounded contexts, invariants and formulas (zones, profiles, crossings, realised wait, nowcast, desk state, SLA bins, data boundary). Load for any domain, stream or reporting work.
---
# Ariva domain essentials

Normative sources: docs/domain/glossary.md (names), docs/domain/formulas.md (F1 to F21 with test cases), docs/domain/data-boundary.md, docs/architecture/overview.md (contexts and events). This skill is the short form; when in doubt, the docs win.

## Bounded contexts
Site topology (airport, terminal, level, checkpoint, desk, e-gate), Zone profiles (versioned geometry: snake queues, service areas, overflow bands, count lines, entry and exit lines), Sensing (devices, calibrations, canonical events), Queue measurement (state engine, minute and bin aggregates), Flights and demand (AODB flights, allocations, AMAN lane demand, show-up curves), Alerting (rules, alerts, escalation), Service levels (contracts, exclusions, evaluations, disputes, evidence packs), Displays, Reporting, Access (users, roles, permissions, audit).

## Invariants that bite
- A published zone profile version is immutable; every derived metric stores the profile version that produced it.
- Realised wait belongs to the entry interval; it is provisional until every passenger who entered in that interval has exited or timed out (censored), then final. Finals change only through a recorded recomputation run.
- Nowcast = (queue length + 1) / throughput; zero throughput means "no service" with a reason, never a number.
- Desk state precedence: integration signal (AMAN desk session) over sensor desk occupancy over approach heuristic; stale signals become Unknown.
- E-gate rejects become demand on the manual lanes (reject coupling).
- Degraded zones (sensor offline or uncalibrated) show bands, never numbers, and are excluded from penalty evaluation for the affected bins.
- Data boundary: no officer, traveller or document identifiers anywhere in Ariva; AMAN contracts are aggregate-only with small-cell suppression for e-gate rejects.

## Reference scenario
Seed 9303 on the fictional DMO airport: the arrivals Visitors nowcast passes 15 minutes at 18:05; sensor S-17 is offline 18:20 to 18:30; Handler B breaches its SLA from 19:10 for three consecutive 15-minute bins (provisional, then final). The simulator and the golden replay test reproduce it exactly.
