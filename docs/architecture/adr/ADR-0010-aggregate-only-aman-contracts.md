# ADR-0010: Aggregate-only AMAN contracts; officer analytics stay in AMAN

- Status: Accepted
- Date: 2026-09-28
- Source: D5 key decision 10; product decisions 8 and 9

## Context

Decision 8 said officer-level data is for border supervisors only. Decision 9 said AMAN shares aggregate-only contracts. Officer-level views need officer identity, which aggregate-only contracts exclude, so the two conflicted.

## Decision

Ariva receives four aggregate-only contracts V1 from AMAN (`DeskSessionChanged`, `DeskIntervalStats`, `EGateIntervalStats`, `InboundFlightLaneDemand`), published by AMAN through its outbox to `aman.feed.<contract>.v1` topics inside the border deployment, with timestamps coarsened to the interval. Officer-level analytics stay in AMAN's own reporting; the Border dashboard links to them. Ariva never holds officer identity. Details in [../../domain/data-boundary.md](../../domain/data-boundary.md).

## Consequences

- No officer drill-down inside Ariva dashboards; a link to AMAN instead.
- AMAN-side outbox changes are needed and go through the client's change control.
- Coarsened timestamps block linkage by timestamp alignment.

## Alternatives considered

- Officer-level data inside Ariva. Rejected: puts identified data in a second system and weakens the privacy case.
