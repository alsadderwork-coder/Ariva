# ADR-0021: Live push with SignalR, Redis backplane and MessagePack

- Status: Accepted
- Date: 2026-10-01
- Source: Product owner decision, 2026-10-01 (AMAN parity); D5 read API and SignalR hub

## Context

Supervisor dashboards show a live floor plan with zones coloured by nowcast and desks by state. The audience is dozens of users per site, so scale is not a concern; reliability across replicas is.

## Decision

Ariva.Api.Main hosts a SignalR hub with a Redis backplane and the MessagePack protocol (AMAN parity). It pushes compacted nowcast and desk-state updates every few seconds, grouped by site and zone, fed by consumers of the compacted `ariva.flow.nowcast.v1` and `ariva.desk.state-changed.v1` topics. Group membership is filtered by the user's tenant and data scope.

## Consequences

- Same client and server libraries as AMAN.
- Redis becomes required for multi-replica Main deployments.

## Alternatives considered

- Server-sent events or polling. Rejected for parity with AMAN.
