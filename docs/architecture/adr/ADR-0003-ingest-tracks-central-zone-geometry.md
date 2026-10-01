# ADR-0003: Ingest tracks and apply Ariva's own versioned zone geometry centrally

- Status: Accepted
- Date: 2026-09-28
- Source: D5 key decision 3

## Context

Sensor vendors can compute line crossings on the sensor. That geometry then lives in each vendor's tool, is unversioned, and a disputed day cannot be recomputed with corrected lines. Penalties require recomputation under a named, signed zone configuration.

## Decision

Ariva ingests anonymous track samples (`TrackSample`), not vendor counts. Zones and lines are Ariva data, published as immutable `ZoneProfile` versions and activated by time or by a supervisor. Crossings are computed centrally in Ariva.Api.Stream. Vendor-computed crossings are kept as a cross-check and as the fallback for sites with constrained links.

## Consequences

- More traffic: about 15,000 messages and 1.5 MB per second for 100 sensors at 30 people each and 5 Hz (sizing assumption). Trivial for a local Kafka and LAN.
- Ariva owns crossing logic and must test it thoroughly (property tests on geometry).
- Vendors are swappable; one zone model covers all vendors; disputed periods can be recomputed; every result records its profile version.

## Alternatives considered

- Vendor lines on the sensor. Rejected as the primary path: unversioned, vendor-specific, not recomputable. Kept as fallback.
