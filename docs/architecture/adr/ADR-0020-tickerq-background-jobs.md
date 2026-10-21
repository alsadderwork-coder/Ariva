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

## Implementation notes

- ARV-060, 2026-10-04: the first job (the scheduled daily reports, every five minutes) runs on TickerQ 10.0.2 in memory, without the EF Core operational store. The job keeps its own state in PostgreSQL (`report_delivery`, one row per schedule, day and recipient, written under a transaction advisory lock), which makes it idempotent across restarts and replicas; adding EF Core and its migrations next to NHibernate and the SQL scripts for TickerQ's own bookkeeping was not worth it for a cron job. Revisit when a job needs TickerQ's persisted per-entity timers (escalation timeouts).
- The dashboard has no Ariva user session (a browser cannot send the bearer token on navigation, and TickerQ's host mode reads the identity from the request only), so it takes an operator key, compared as a SHA-256 digest in constant time on every API and hub request, and serves its page under its own content security policy: only the two inline scripts of TickerQ's page by hash, no `unsafe-eval` (its bundled chart library probes with `new Function` and falls back when refused), no framing. The API reads the key from the Authorization header. TickerQ's browser app sends nothing to its notification hub in host mode, so every keyed request returns a ticket cookie (an expiry and its HMAC under a key the process draws at random; HttpOnly, Secure, SameSite=Strict, scoped to the hub path, 30 minutes) and the hub accepts only that ticket, from its own origin; nothing secret travels in a URL. TickerQ's hub also insists on some Authorization value before host mode reads the user, so an admitted hub request gets a fixed marker there (`Ariva-Jobs-Ticket`, not a credential); the e2e test holds the connection ten seconds to prove TickerQ keeps it. Paths are matched ignoring case against an explicit list of what may load without the key (the page, its static assets, `/api/auth/info`). It is off unless configured.
- ARV-124a, 2026-10-21 (lab measurement, TickerQ 10.0.2 in memory, a test application outside the repository): TickerQ starts a cron function's next occurrence while the previous run is still going (a 5-second function every 2 seconds ran three to five at once), skips the occurrences it missed while the process was stopped (a 10-second pause fired none of the five it covered), and now and then fires one occurrence twice, 1 to 10 ms apart, whether or not runs overlap (13 of 118 occurrences, with a 5-second function and with a 100 ms one). It has no option Ariva sets to prevent either. So every job must tolerate a concurrent copy of itself on the same replica, as it already must across replicas; the five jobs do (advisory locks skipped when held, inserts `ON CONFLICT DO NOTHING`, `FOR UPDATE SKIP LOCKED`, compare-and-swap updates), and `JobConcurrencyTests` fails for a new job until it is reviewed and listed with its guard. The staffing job, every minute, warns when a run takes its whole minute or more (wiki 10 section 4.8l). Repeat the measurement after a TickerQ upgrade (wiki 05, sizing notes).

## Alternatives considered

- Rebus deferred messages. Not available (Rebus dropped).
- Kubernetes CronJobs. Rejected for fine-grained, per-entity timers.
