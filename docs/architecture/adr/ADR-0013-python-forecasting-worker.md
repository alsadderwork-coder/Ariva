# ADR-0013: Python worker for model training and batch forecasts

- Status: Accepted
- Date: 2026-09-28
- Source: D5 key decision 13; D4 model quality

## Context

Show-up and load-factor models, quantile outputs, backtesting and Monte Carlo simulation have a mature ecosystem in Python. The nowcast and arrival-wave alert need seconds of latency and are deterministic arithmetic.

## Decision

A Python worker runs on a schedule (every 15 minutes for the day, nightly for the season): show-up and load-factor models (gradient-boosted trees with quantile outputs as the default), Monte Carlo waits, walk-forward backtesting. It talks to the rest of Ariva only through Kafka topics and one container. The arrival-wave alert and nowcast stay in .NET in Ariva.Api.Stream; the staffing optimiser stays in .NET using an operations-research library such as Google OR-Tools. Scope: v1. Repository location: To confirm.

## Consequences

- A second language in the stack, contained by a Kafka contract and one container.
- Every forecast run records its model version and an input snapshot.

## Alternatives considered

- ML.NET only. Rejected: weaker ecosystem for quantile models and simulation.
