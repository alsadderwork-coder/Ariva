# ADR-0008: Nowcast from queue length and staffed-desk throughput

- Status: Accepted
- Date: 2026-09-28
- Source: D5 key decision 8; D4 nowcast

## Context

Screens and alerts need the wait for someone joining now. Realised waits are late and understate waits while a queue is building.

## Decision

Nowcast = (queue length + 1) / estimated throughput, where throughput is staffed desks divided by recent cycle time, blended with the measured exit rate over the last few minutes (formulas F8). At AMAN sites cycle times come from real transactions.

## Consequences

- Explainable and checkable by hand by a technical committee.
- Some accuracy is lost in unusual regimes (fast-track merges, sudden desk changes); handled by the merge ratio and blending rules.

## Alternatives considered

- A learned nowcast model. Rejected for now: less explainable; can be revisited with pilot data.
