# ADR-0014: Canonical flight model with AIDX and ACRIS adapters, mocks and a replay harness

- Status: Accepted
- Date: 2026-09-28
- Source: D5 key decision 14; product decision 11 (mock AODB feeds during development)

## Context

AODB access takes months and every airport's feed differs. Development cannot wait for it.

## Decision

Flight Demand is an anti-corruption layer with a canonical flight model and a canonical flight id map. Adapters: IATA AIDX (implementation guide v22.1) as primary, ACI ACRIS, SSIM schedule import, and one adapter per vendor AODB once access is granted. All AODB feeds are mocked during development (AIDX 22.1, ACRIS, SSIM import) in Ariva.Simulation.Api, and a replay harness plays recorded feeds.

## Consequences

- Risk that mocks diverge from real feeds. Closed by recording feeds from each pilot airport and adding them to the replay suite.
- The domain never sees a vendor's vocabulary.

## Alternatives considered

- Coding directly against one vendor's AODB. Rejected: lock-in and blocked development.
