# Operations runbook

For Dalil support, site IT and the local partner. It lists what to watch, which alarms Ariva raises about itself, and step-by-step procedures for the incidents that matter. The guiding rule: degrade and flag, never guess silently. A failure must leave affected numbers marked `Degraded` or `Unknown` and screens honest, not frozen.

Status: metric names, dashboards and system alarms are built in Phase 0 epic Skeleton and platform and Phase 1 epic Hardening. Until then, use pod health, logs and the dependency tools shown below. Commands use `$NAMESPACE` for the deployment's namespace.

## 1. Monitoring stack

| Piece | What it does |
|---|---|
| Serilog | Structured logs from every .NET host |
| OpenTelemetry | Traces and metrics from every host, exported over OTLP to the endpoint in `otel.endpoint` (SigNoz, or Loki and its stack, as the site provides). Trace context is propagated through Kafka headers |
| Resource attributes | `service.namespace=ariva`, `deployment.environment`, `k8s.cluster.name`, `service.version`, plus node, pod and namespace names |
| Kubernetes probes | `/health/startup`, `/health/readiness`, `/health/liveness` on every .NET host; `/healthz` on the web pods. Readiness will include PostgreSQL, Kafka and Redis checks (target) |
| TickerQ dashboard | Job runs and failures in Ariva.Api.Cronz (management network only) |

## 2. Key metrics

Thresholds are To confirm per site unless a value is given.

| Metric | What it tells you | Alarm (proposal) |
|---|---|---|
| Kafka consumer lag, per group (`ariva-stream`, `ariva-integration`) and partition | Whether Stream and Integration keep up | Lag growing for several minutes |
| Ingest rate per device (messages per second) and frame rate | A sensor going quiet or degrading | Rate or frame rate below the device's normal band |
| Device heartbeat age | Sensor offline | No `DeviceHealth` for 2 x the expected interval (T_stale, Proposed) |
| Clock offset per device | Time sync problems | Above 500 ms (assumption to tune) |
| Stream processing latency (sensor time to nowcast published) | End-to-end freshness of screens | Above a few seconds (D5 expects seconds) |
| Bin maturity backlog (bins provisional for too long) | Censored tracks, stuck watermarks, missing data | Bins older than the censoring timeout still provisional |
| Zones `Degraded` or `Unknown` | Measurement quality across the site | Any zone `Unknown`; degraded count above normal |
| AODB feed freshness (time since last message while flights are due) | Stale flight data | No message within the agreed cadence |
| AMAN feed heartbeat (`aman.feed` topics) | Border desk state and service rates going stale | Heartbeat gap above T_stale |
| Outbox backlog (unpublished rows) | Kafka problems or a stuck relay | Growing backlog |
| API error rate (5xx) and authentication failures (401) per client | Faults; misconfigured or attacking integrators | 5xx above baseline; lockouts |
| Display staleness | Boards showing the neutral message | Any board stale during operating hours |
| Database disk usage, hypertable sizes, compression and retention job status | Running out of space; jobs not running | Disk above 75 percent; a failed job |
| Redis availability | SignalR and caching | Unavailable |
| Certificate expiry | Upcoming TLS failures | 30 days before expiry |

## 3. Alerts on the system itself

| Alarm | Severity (proposal) | Goes to |
|---|---|---|
| Sensor offline, sensor degraded | Warning | Site administrator, local partner (ticket) |
| Zone `Unknown` (entry or exit line without coverage) | Critical | Site administrator, Dalil support |
| Clock drift unstable | Warning | Local partner, site IT |
| Stale AODB feed | Warning | Airport IT |
| AMAN feed stopped | Critical (border) | Border authority IT, Dalil support |
| Unknown AMAN desk or gate code | Warning | Site administrator |
| Kafka consumer lag growing, outbox backlog | Critical | Dalil support |
| Bin maturity backlog | Warning | Dalil support |
| Database disk filling, retention job failed | Critical | Site IT, Dalil support |
| Display stale | Warning | Terminal or border operations |
| Certificate expiring | Warning at 30 days, critical at 7 | Site IT |
| Integration client locked out | Warning | Site administrator |

Operational alerts for queues (for example R-001 "Nowcast above 15 min") are configured by users; see [Administration guide](11-Administration-Guide.md).

## 4. Procedures

Each procedure lists symptoms, the effect on numbers, checks and actions. Record every incident with times, so SLA evaluation can apply the right exclusions (sensor outages over 5 minutes in a bin are an allowed exclusion type in the reference contract).

### 4.1 Sensor offline

- **Symptoms**: device status `Offline`; rule R-003 "Sensor offline" fires; its zone turns `Degraded`; the floor plan shows a gap.
- **Effect**: bins for the zone flagged `Degraded`; the nowcast falls back to desk-event throughput and neighbouring sensors and is shown as a band; if an entry or exit line loses coverage, the wait becomes `Unknown` and screens show a neutral message.
- **Checks**:
  1. Is it one sensor or a whole switch? Look at neighbouring devices on the same switch.
  2. Ping the device from the gateway's sensor interface; check the switch port and PoE status.
  3. Check Ingest logs for authentication or payload errors from that device: `kubectl -n "$NAMESPACE" logs deploy/api-ingest-deployment --since=30m`.
- **Actions**: raise a ticket to the local partner for power, cabling or hardware. If the device was replaced or moved, it goes back to `Commissioning` and must be re-calibrated.
- **Recovery**: health checks confirm the heartbeat; the device returns to `Online`. Stereo sensors that stored counts on board can backfill interval counts; tracks for the gap are lost and those bins stay `Degraded`.

The reference scenario rehearses this: sensor S-17 over the arrivals hall is offline from 18:20 to 18:30, and the Visitors zone shows a band.

### 4.2 Zone degraded or unknown

- **Symptoms**: zone flagged `Degraded` or `Unknown` without a sensor alarm.
- **Likely causes**: a desk in `Unknown` state (all its signals stale); AMAN feed stale with last-known values in use; corrected clock drift; censored tracks above 5 percent of a bin's entries; conservation residual above tolerance; a line outside coverage after a profile change.
- **Checks**: the zone's data-quality reason in the dashboard (once available) or Stream logs; the active profile version and its activation time; desk states for the lane; device clock offsets.
- **Actions**: fix the input (sensor, feed, mapping, clock). If a profile change caused it, publish a corrected version and recompute the affected period (the original revision is kept).

### 4.3 Stale AODB feed

- **Symptoms**: stale-feed alarm; no flight messages while flights are due; the arrival-wave strip says it uses last estimates.
- **Effect**: forecasts marked as built on stale inputs with widened bands; fall back to the schedule; the arrival-wave alert uses the last estimates and says so. Live measurement is unaffected.
- **Checks**: Integration logs for authentication failures (`401 invalid_client`, lockouts) or validation errors from the AODB client; the client's status in administration; network path from the AODB to the ingress; for outbound pulls, the `OutboundEndpoint` circuit breaker state and the remote API.
- **Actions**: contact airport IT; unlock the client after confirming the cause; if the AODB sent wrong data (for example a missing on-block), operators correct it or the next message does. Ad-hoc flights can be added manually while the feed is down (v1).
- **Recovery**: automatic when messages resume; messages are applied by their own timestamps.

### 4.4 AMAN feed stopped (border deployments)

- **Symptoms**: AMAN feed heartbeat alarm; desk states switch to sensor-derived; service rates frozen.
- **Effect**: desk state from sensor zones; service rate from the last known values; affected bins flagged `Degraded`.
- **Checks**:
  1. Is AMAN publishing? Ask the AMAN operations team to check its outbox.
  2. Is Ariva consuming? `kafka-consumer-groups.sh --bootstrap-server <broker:port> --describe --group ariva-integration`.
  3. ACLs on the `aman.feed.` prefix; unknown desk codes being parked (look for the data-quality alert).
- **Actions**: restore the AMAN side or the consumer; map any unknown desk or gate code in a new profile version.
- **Recovery**: automatic on resume; consumption is idempotent by `SourceEventId`.

### 4.5 Clock drift detected

- **Symptoms**: clock-offset alarm for one or more devices.
- **Effect**: a stable offset is subtracted and the data flagged `Degraded`; an unstable offset marks the sensor `Degraded` and waits across its boundaries unreliable.
- **Checks**: is the offset on one device (device NTP or PTP settings) or many (site time source, network path)? Check the site NTP server's own synchronisation and the servers' `chronyc tracking` or equivalent.
- **Actions**: fix the device or the time source; resynchronise. Avoid firmware or configuration changes during peaks.
- **Recovery**: offset back within 500 ms and stable; the device returns to `Online`.

### 4.6 Kafka consumer lag growing

- **Symptoms**: lag alarm; screens slow to update; nowcasts stale.
- **Checks**:
  1. `kafka-consumer-groups.sh --bootstrap-server <broker:port> --describe --group ariva-stream` (lag per partition).
  2. Stream pods: `kubectl -n "$NAMESPACE" get pods -l app=api-stream` and `kubectl -n "$NAMESPACE" top pods -l app=api-stream` (CPU at the limit, restarts).
  3. Broker health and disk; under-replicated partitions.
  4. A single hot partition (one busy queue zone) versus all partitions.
- **Actions**: if CPU-bound, raise the HPA maximum or resources (`apiStreamHpaMax`, `apiStreamCpuLimit`), but replicas beyond the partition count do nothing; if a broker is down, restore it (three brokers, replication factor 3, minimum two in-sync replicas survive the loss of one).
- **Recovery**: lag drains; late events reopen bins as new revisions, which is expected.

If Kafka is unavailable entirely: Ingest buffers to disk, services keep unpublished events in the PostgreSQL outbox, and screens switch to the neutral message after the staleness threshold. On recovery the relay drains the outbox and consumers resume from committed offsets.

### 4.7 Database disk filling

- **Symptoms**: disk alarm; slow queries; write errors in Stream logs.
- **Checks**:

```sql
SELECT pg_size_pretty(pg_database_size('ariva'));
SELECT hypertable_name, pg_size_pretty(hypertable_size(format('%I.%I', hypertable_schema, hypertable_name)::regclass))
  FROM timescaledb_information.hypertables;
SELECT job_id, hypertable_name, last_run_status, last_successful_finish, next_start, total_failures
  FROM timescaledb_information.job_stats;
```

- **Likely causes**: the retention or compression job failing (raw samples must be dropped after the dispute window and compressed after one day); a dispute window set longer than the volume was sized for; WAL retained by a broken replica or backup archive.
- **Actions**: fix and re-run the failing job; expand the volume (volumes can grow, never shrink); check replication slots and WAL archiving. Never delete hypertable data by hand: only the retention job may, and evidence for open disputes must be kept.

### 4.8 Display board stale

- **Symptoms**: a board shows the neutral message; operations report a blank or frozen board.
- **Effect**: by design, a board never freezes on an old number; it shows the neutral message when the latest nowcast is older than the staleness threshold.
- **Checks**: is the zone's nowcast itself stale (see 4.1, 4.6) or only the board? From the display VLAN, can the player reach the display page and the read-only endpoint? Is the player's browser in kiosk mode and online?
- **Actions**: fix the upstream cause or the player's network; reload the kiosk page.

### 4.9 Certificate expiry

- **Checks**:

```bash
echo | openssl s_client -connect <host>:443 -servername <host> 2>/dev/null | openssl x509 -noout -enddate
```

Also check device client certificates (MQTT), outbound endpoint client certificates, the border-to-airport mutual TLS certificates, and integration partners' pinned CAs.

- **Actions**: renew through the customer's PKI; update the ingress certificate secret or the controller's default certificate; for device certificates, schedule rotation with the local partner outside peaks.

### 4.10 Replaying a time range

Three kinds of replay exist; pick the right one.

| Need | Mechanism | Status |
|---|---|---|
| Recover after a crash, rebalance or Kafka outage | Automatic: consumers resume from committed offsets; idempotent upserts by (zone, bin start, revision) and event-id deduplication make replay safe | Built into the stream design |
| Recompute a disputed or misconfigured period | Recompute from raw samples in TimescaleDB (not from Kafka) under a named zone profile version; the result is a new revision and the original is kept | Target procedure, implemented in v1 epic SLA and penalty engine (recomputation and revision tooling) |
| Reproduce a day for tests or a demo | The AODB replay harness and Ariva.Simulation.Api replay recorded feeds and the reference scenario deterministically | Target procedure, implemented in Phase 0 epic Device gateway and simulator |

Do not reset consumer offsets by hand to "replay" a period until the procedure has been verified in Phase 0 epic Queue engine core; Kafka keeps track samples for only a few days, and raw samples in TimescaleDB are the source for anything older.

## 5. Routine checks

| Frequency | Check |
|---|---|
| Daily | Zones `Degraded` or `Unknown` in the last 24 hours and why; device offline minutes; feed freshness; failed TickerQ jobs |
| Weekly | Disk growth against forecast; certificate expiries in the next 60 days; integration client lockouts; conservation residual and track completion trends |
| Monthly | Restore test on a copy (at least before go-live and after major releases); review of audit log for configuration changes |
| Quarterly | Re-validation of penalty-grade zones |
