# ADR-0007: Realised wait attributed to the entry interval, provisional until final

- Status: Accepted
- Date: 2026-09-28
- Source: D5 key decision 7; D4 snake-queue wait time

## Context

A wait target describes the experience of people who joined the queue in a period. Waits are only known once people exit. Penalties need numbers that do not move after they are used.

## Decision

Each realised wait is attributed to the bin in which the passenger crossed the first entry line. A bin is provisional until everyone who entered in it has exited or been resolved (see formulas F6), then final. Reports mark provisional bins. SLA evaluation uses final bins only. Recomputation creates a new revision and keeps the original.

## Consequences

- The latest numbers are always provisional; revisions add complexity to storage, APIs and UI.
- A bin maturity backlog metric is needed (bins provisional for too long).

## Alternatives considered

- Attribution to exit time. Rejected: bins become final sooner, but during a build-up they describe service, not the experience of the people waiting.
