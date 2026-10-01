# ADR-0020: Background jobs with TickerQ in Ariva.Api.Cronz

- Status: Accepted
- Date: 2026-10-01
- Source: Product owner decision, 2026-10-01 (AMAN parity)

## Context

Ariva needs scheduled and delayed work: escalation timers, SLA evaluation windows, scheduled reports, evidence packs, forecast triggers and housekeeping. With Rebus dropped ([ADR-0004](ADR-0004-kafka-for-facts-workflows-without-second-broker.md)), deferred messages are no longer available.

## Decision

TickerQ runs all scheduled and delayed jobs in Ariva.Api.Cronz (port 51004), as in AMAN: alert escalation timeouts, SLA evaluation runs and window closes, scheduled report and evidence-pack generation (PuppeteerSharp and Handlebars, the AMAN pattern), staffing optimiser runs (v1), forecast scheduling triggers (v1), retention and health sweeps.

## Consequences

- One scheduler, same as AMAN; job state is persisted.
- Jobs must be idempotent and safe to re-run after a restart.

## Alternatives considered

- Rebus deferred messages. Not available (Rebus dropped).
- Kubernetes CronJobs. Rejected for fine-grained, per-entity timers.
