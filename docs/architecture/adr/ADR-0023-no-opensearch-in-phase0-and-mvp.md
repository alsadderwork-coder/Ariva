# ADR-0023: No OpenSearch in Phase 0 or the MVP

- Status: Accepted
- Date: 2026-10-01
- Source: Product owner decision, 2026-10-01; D5 listed OpenSearch as optional for event and audit search

## Context

AMAN runs OpenSearch. D5 rejected it as a system of record for revisable bins and kept it optional for event and audit search.

## Decision

OpenSearch is not deployed in Phase 0 or the MVP. There is no free-text search need. Audit and event queries are served from PostgreSQL. Revisit at v1 if audit search or log analytics require it.

## Consequences

- Smaller footprint and one less component to secure at pilot sites.

## Alternatives considered

- Deploy OpenSearch for audit and event search from the start. Rejected: no requirement yet.
