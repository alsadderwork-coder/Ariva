# ADR-0022: Observability with Serilog and OpenTelemetry to SigNoz or Loki

- Status: Accepted
- Date: 2026-10-01
- Source: Product owner decision, 2026-10-01 (AMAN parity; replaces D5's Prometheus, Grafana and Loki reuse)

## Context

D5 said observability reuses AMAN's Prometheus, Grafana and Loki stack. AMAN's current pattern is Serilog plus OpenTelemetry, exported to SigNoz or Loki.

## Decision

Ariva logs with Serilog and emits traces and metrics with OpenTelemetry, exported to SigNoz or Loki as the site provides. Product-level alarms: consumer lag, bin maturity backlog, stale AODB and AMAN feeds, sensor heartbeat and frame rate, clock drift, outbox backlog.

## Consequences

- Same dashboards and skills as AMAN.
- Trace context must be propagated through Kafka headers.

## Alternatives considered

- Prometheus, Grafana and Loki (D5). Superseded for AMAN parity.
