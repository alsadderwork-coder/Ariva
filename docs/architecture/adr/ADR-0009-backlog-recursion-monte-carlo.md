# ADR-0009: Backlog recursion with Monte Carlo for waits and staffing

- Status: Accepted
- Date: 2026-09-28
- Source: D5 key decision 9; D4 forecasting

## Context

Airport arrivals come in waves, and queues carry over from one interval to the next. Per-interval Erlang formulas assume steady state and ignore carry-over.

## Decision

Forecast waits with the backlog recursion Q(k+1) = max(0, Q(k) + A(k) - C(k)) per interval, run many times with sampled show-ups, on-blocks, loads and service times (Monte Carlo) to obtain P50 and P90. Staffing is the smallest desk count per interval meeting the P90 target under constraints (formulas F9, F15, F16).

## Consequences

- Whiteboard-explainable; handles carry-over.
- Layout-level detail (walking paths, lane switching) is not modelled; discrete-event simulation stays a later feature (v2 what-if).

## Alternatives considered

- Erlang C. Rejected: ignores carry-over between intervals.
- Full discrete-event simulation. Deferred to v2.
