# ADR-0012: One deployment per authority per airport, with a one-way border-to-airport feed

- Status: Accepted
- Date: 2026-09-28
- Source: D5 key decision 12; product decision 15

## Context

At a combined site the border authority and the airport operator are different data controllers with different rules. Officer-level data must physically stay on the border side.

## Decision

Each authority gets its own deployment per airport, always in-country. At a combined site, the border deployment pushes lane-level wait times and KPIs one way to the airport deployment. The airport side never gets a route into the border network.

## Consequences

- The officer-data boundary is physical, not a UI setting.
- No single pane of glass across the boundary; more deployments to operate.
- The feed needs buffering and stale marking on the airport side.

## Alternatives considered

- A single shared deployment with role-based separation. Rejected: the boundary would be a configuration setting.
