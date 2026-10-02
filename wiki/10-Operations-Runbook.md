# Operations runbook

For Dalil support, site IT and the local partner. It lists what to watch, which alarms Ariva raises about itself, and step-by-step procedures for the incidents that matter. The guiding rule: degrade and flag, never guess silently. A failure must leave affected numbers marked `Degraded` or `Unknown` and screens honest, not frozen.

Status: metric names, dashboards and system alarms are built in Phase 0 epic Skeleton and platform and Phase 1 epic Hardening. Until then, use pod health, logs and the dependency tools shown below. Commands use `$NAMESPACE` for the deployment's namespace.

## 1. Monitoring stack

| Piece | What it does |
|---|---|
| Serilog | Structured JSON logs on the console of every API host; also to Loki (`Loki:Enabled`, `Loki:Uri`) and to the OTLP endpoint when telemetry is on. Credentials are removed before any sink sees an event: Authorization, Cookie, Set-Cookie, X-TOTP-Code, access_token and password-like values, bearer and basic credentials and JWTs inside text. `Microsoft.AspNetCore.Hosting` stays at Warning in code (its request line carries the SignalR access_token); raise other categories under `Serilog:MinimumLevel:Override` |
| OpenTelemetry | Traces and metrics from every API host, exported over OTLP to the endpoint in `otel.endpoint` (the chart sets `OTEL_EXPORTER_OTLP_ENDPOINT`; `Otlp:Enabled` and `Otlp:Endpoint` do the same outside Kubernetes). Off when no endpoint is set. Query string values are redacted in spans (OpenTelemetry .NET default; never set `OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION`). Trace context is propagated through Kafka headers |
| Resource attributes | `service.namespace=ariva`, `deployment.environment`, `k8s.cluster.name`, `service.version`, plus node, pod and namespace names |
| Kubernetes probes | `/health/startup`, `/health/readiness`, `/health/liveness` on every .NET host; `/healthz` on the web pods. Readiness runs the checks tagged `ready`: the MassTransit bus and its Kafka consumers (ARV-020), so a consuming host leaves the service while it cannot reach Kafka; a host that only publishes stays ready and keeps events in the outbox. PostgreSQL and Redis checks are target |
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

### 4.5a Outbox backlog and dead letters (ARV-020)

Outbox backlog, oldest unsent event and the last error (read only, safe on production):

```sql
SELECT count(*) AS unsent, min(created_on) AS oldest, max(attempts) AS most_attempts
  FROM outbox_message WHERE sent_on IS NULL;
SELECT topic, message_key, message_type, attempts, next_attempt_on, last_error
  FROM outbox_message WHERE sent_on IS NULL AND attempts > 0 ORDER BY seq LIMIT 20;
```

- A growing backlog with `last_error` naming the broker (timeouts, transport failure): Kafka is unreachable; the relay backs off (2 seconds doubling to 5 minutes per row) and drains on recovery. Check the brokers, then the relay's logs (`Ariva.Infra.Messaging.Outbox.OutboxRelay`).
- `Unknown event type` in `last_error` with `attempts` 0: a pod of another build leads the relay (during a rolling deploy) or a rollback crossed an event rename. The row and the rows behind it with the same key wait without backoff and go as soon as a pod that knows the type leads; roll forward if it persists. Rows are never dropped.
- `Outbox row ... stuck after N attempts` in the error log (from attempt 10): one key has been held back for a long time. Read its `last_error`; an event refused for size cannot happen (the commit that wrote it would have failed), so it is the broker, the topic (missing, or ACLs) or the principal.
- Dead letters: a consumer that fails after its retries writes the message to `<topic>.dlq.v1` (AMAN feed topics: `ariva.aman.feed.<contract>.v1.dlq.v1`) with the source partition, offset, key, consumer group and error, and moves on. Read them with any Kafka client from the beginning of the dead-letter topic; the body is base64 of the original value, cut at 512 KB (`bodyTruncated`, `bodyBytes`), in which case read the original from the source topic by partition and offset while its retention lasts. Replay to the source topic is an admin operation (target, audited; not yet built). Never delete a dead-letter topic: it is the record of what was not applied.

### 4.5b Queue stream worker (ARV-034)

Ariva.Api.Stream's queue engine worker (group `ariva-stream.queue-engine`) writes `queue_minute`, `queue_bin`, the zone snapshots (`stream_zone_state`) and its own positions (`stream_offset`) in one transaction per checkpoint. To check it is keeping up, compare `stream_offset.next_offset` with the end of each sensing topic partition and look at `stream_zone_state.updated_on`.

- After a crash or a redeploy nothing needs doing: the worker restores each zone from its snapshot and replays the records after the saved positions, rewriting the same rows.
- The positions in `stream_offset` are authoritative. Resetting the Kafka consumer group's offsets has no effect on a partition that has a saved position.
- To recompute a zone from scratch (for example after a geometry fix), stop the worker, delete the zone's `stream_zone_state` row and the group's `stream_offset` rows as the migration login, reset the group to the earliest offsets, and start it again. The topics keep 3 days; older periods come from the archive (4.10).
- A log line "the saved state of zone ... cannot be restored" means the snapshot failed its checks and the zone started afresh; recompute the affected period from the archive (4.10).
- `stream_partition_count` remembers every partition count the sensing topics have had, so a recompute still accepts records produced before partitions were added. Keep it when restoring or moving the database; a database restored from before a scale-out needs the earlier count inserted again (as the migration login) before it replays those records.
- Records of a zone that is not in its site's published profile, or beyond `Stream:MaxZones` zones on one instance, are counted and skipped (warning in the log); publish the profile or add instances, then recompute the period from the archive.
- "Queue stream checkpoint failed" means the database refused the write; the worker keeps its outputs, retries every checkpoint interval and pauses reading while a zone is at its bound. Nothing is lost unless the instance itself stops, in which case its successor replays from the last checkpoint.
- Dead letters of the sensing topics (`ariva.device.*.dlq.v1`) are records that are not a batch of the zone their key names. Investigate the producer (Ingest) before replaying them.
- Upgrading to ARV-036: Ingest keys device health reports by zone from then on. Upgrade Ingest before Stream if you can; reports Ingest published before (keyed by device id) are skipped by the queue worker and counted in a warning ("keyed by device id"), not dead-lettered. Add partitions to `ariva.device.health.v1` first if its count differs from the sensing topics'.
- Device outages (ARV-036): `zone_outage` lists, per zone, each device silent beyond 180 seconds or reporting itself offline, from the minute it was last heard in to the minute it was heard again. A zone degraded on the live screens with no outage there is degraded by its inputs (skewed or unreliable clocks, F11). A device removed from a zone stops counting 24 hours after it was last heard.

A host that logs "Waiting for Kafka topics before starting the bus" is holding its start until Api.Main (or the platform team) has created its topics; after `Kafka:Consumers:StartWaitSeconds` (300) it starts anyway and its readiness check reports the bus.

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

- **Likely causes**: the retention or compression job failing (raw samples in `sensing_event` and `sensing_batch` must be dropped after the dispute window and compressed after one day; `SELECT * FROM timescaledb_information.job_stats` shows the last runs); a dispute window set longer than the volume was sized for; WAL retained by a broken replica or backup archive.
- **Actions**: fix and re-run the failing job; expand the volume (volumes can grow, never shrink); check replication slots and WAL archiving. Never delete hypertable data by hand: only the retention job may, and evidence for open disputes must be kept.

### 4.8 Display board stale

- **Symptoms**: a board shows the neutral message; operations report a blank or frozen board.
- **Effect**: by design, a board never freezes on an old number; it shows the neutral message when the latest nowcast is older than the staleness threshold.
- **Checks**: is the zone's nowcast itself stale (see 4.1, 4.6) or only the board? From the display VLAN, can the player reach the display page and the read-only endpoint? Is the player's browser in kiosk mode and online?
- **Actions**: fix the upstream cause or the player's network; reload the kiosk page.

### 4.8a Live dashboards not updating (ARV-035)

- **Symptoms**: dashboards connect but their queue figures stop changing; or the browser cannot connect to `/hubs/live`.
- **Checks**: is the stream itself writing (4.5b, `queue_minute` for the last minutes)? In Redis, does `ariva:live:zone:<site>/<zone>` change after each checkpoint, and does `ariva:live:relay` exist (one Main replica holds it, renewed every 5 seconds)? Do Api.Main and Api.Stream use the same `Redis:ConnectionString` and `InstanceName`? Does the ingress pass WebSocket upgrades on `/hubs` (a 400 or a connection closed at once)?
- **Log lines**: Stream logs "Live snapshots not published" when Redis refuses the write (the database checkpoint is not affected); Main logs "Live relay could not subscribe" or "Live relay lease not renewed" while Redis is unreachable and retries every 5 seconds.
- **Access**: a user who cannot join a zone gets `forbidden` (no `LiveQueue.View`, the zone's site is not among the user's sites, or the zone is not a queue zone of the site's published profile; a newly published zone is joinable within a minute), `too_many_zones` (more than 64 zones on one connection), `too_many_joins` (more than 120 joins a minute) or, on connecting, `too_many_connections` (more than 8 hub connections of one sign-in on one replica).
- **Logs**: the `/hubs` ingress writes no access log by design (its URLs carry tokens); use Main's logs, which name the connection id only.
- **Actions**: restore Redis or fix the settings; the next checkpoint rewrites every zone's snapshot and clients receive it without reconnecting. Restart Main only if no replica holds the relay lease for more than a minute.

### 4.8b Alerts not raised or not clearing (ARV-038)

- **Symptoms**: a queue is clearly over a rule's threshold but no alert appears, or an alert stays open after the queue is back.
- **Checks**: is the rule enabled, and are its zones queue or overflow zones of the published profile? Is the stream writing the zone's minutes (`queue_minute` for the last minutes, 4.5b)? The evaluation only judges minutes the stream stored and that have ended. While the queue length is degraded (a sensor out, 4.1) a nowcast rule skips those minutes, so the sustain or clear count waits. Run the rule's backtest over the last hour: it is the same evaluation and shows whether, and when, it should have fired. In the database, `alert_rule_state` shows each target's counts and the last minute taken.
- **Log lines**: Stream logs "Alert evaluation: n raised, n cleared, n withdrawn, n failed" when a tick changes something, "Alert evaluation of rule {id} failed" with the error when one rule fails (the others go on; it is tried again next tick), and "Alert evaluation tick failed" when a whole tick fails.
- **Escalation**: an alert still Raised after its rule's escalation minutes is escalated at the next tick (audited as `Alert.Escalated` by `alert-evaluation`); if escalations are late, check that the Stream host is evaluating (the log line above) and that its clock is right.
- **Screens not told of alerts**: the screen must have joined the site's alerts (`JoinAlerts`), and its user must hold a role responsible for the alert; Main and Stream must share the Redis of 4.8a.
- **Known limits**: `OverflowOccupied` and `DesksBelowPlan` rules have nothing to judge yet; predicted-nowcast rules need the arrival-wave projection (ARV-047). A new or re-enabled rule starts at the present (use the backtest for the past). After the Stream host was down for more than three hours (`Alerts:Evaluation:MaxCatchUpMinutes`), evaluation continues from the last three hours.
- **Actions**: fix the data source; an edit of the rule restarts its counts. Do not delete `alert_rule_state` rows by hand.

### 4.8c Alert emails not arriving (ARV-040)

- **Symptoms**: an alert was raised or escalated but its people got no email.
- **Checks**: is `Email:Enabled` on in the base settings, and does the rule notify by email (`notifyByEmail`)? Who is told: when raised, the people of the rule's owner role (every operational role for a rule without owner); when escalated, the people of the escalation role; each enabled, holding the role and the alert's site, with an email address that is one plain address. A rule that escalates to a named contact rather than a role sends no escalation email. In the database, `email_message` has one row per alert, kind and address: `Pending` (waiting or to be retried at `next_attempt_utc`), `Sent`, `Suppressed` (held back by the hourly limit per address, `Email:MaxPerRecipientPerHour`) or `Failed` (given up after `Email:MaxAttempts`). `reason` names the error type of the last failure, never the server's reply.
- **Held back at sending**: an email is sent only while its recipient is still an enabled account with that address, holding the alert's site and the role it was written for; otherwise the round marks it Suppressed ("The recipient no longer holds ..."). One Integration replica sends at a time; the others skip the round.
- **Log lines**: Integration logs "Email round: n sent, n to retry, n given up, n held back" when a round has any of those and "Email round failed" with the error when a whole round fails; the writing host logs "recipients skipped for an address that is not valid".
- **Actions**: fix the relay or its credentials; pending emails go out on the next rounds. Fix an address in the user's profile (Administration guide); emails already written keep the old address. Do not edit or delete `email_message` rows: the runtime role can change only how sending went (never the recipient, subject or body), Sent, Suppressed and Failed are final, and the rows are the record of who was told what.
- **Known limits**: delivery is at least once: if Integration stops between the relay accepting a message and the round's commit, that message is sent again. No digest or quiet hours yet; a burst over the per-minute limit waits for the next minutes.

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
| Evidence for a period, or a regression check of an engine change | Golden replay of the archive (ARV-036): `Ariva.Api.Stream --replay`, below | Built |

Golden replay (ARV-036). Run the Stream image as a one-off job (or the host locally) with the runtime database login, as for the migration job:

```
dotnet Ariva.Api.Stream.dll --replay --replay-site=DMO --replay-zone=A-VIS --replay-zone=CI-C \
  --replay-from=2026-09-28T17:00:00Z --replay-to=2026-09-28T20:30:00Z --replay-profile-version=12 \
  --replay-output=/evidence/dmo-2026-09-28.jsonl
```

- Without `--replay-zone` every queue zone of the profile version is replayed; without `--replay-profile-version` the published one is used. Times are UTC and end in `Z`; a range is at most 31 days and a zone at most a million archived events and a million health reports (about 400 MB each in memory; split longer ranges, and give the job the Stream pod's memory). The archive keeps 90 days.
- The command prints the manifest (site, zones, range, profile version, engine version, settings hash), the hashes (`outputHead` is the stable output hash: the same archive, version and settings always give it), each zone's counters and the run's `rowHash`, and records the run in `replay_run`: the database sets the row's time and login and chains it to the run before, so a run cannot be backdated or slipped in later unnoticed. The export file is created new (never over an existing file or a link) with owner-only permissions; it holds one JSON line per archived record and per output row, each with its chain value. Hand it over with the printed `replayHash`, and keep the `rowHash` outside the database (in the evidence pack or the ticket) as the anchor.
- To check an export: `dotnet Ariva.Api.Stream.dll --verify-replay=/evidence/dmo-2026-09-28.jsonl`. Exit code 0 means every line is exactly as written, every chain value holds and the run is on record in `replay_run` (the output lists the recorded runs with their time, login and row hash); 3 means a line was changed, dropped, added, moved or given an extra property, the file is incomplete, or no such run was recorded (a recomputed chain shows here).
- What the chains prove, and what they do not: anyone who changes the export after the run is caught. The archive and `replay_run` are only as trustworthy as the runtime database login: whoever holds it can add archive rows or record runs (with the database's time, chained after the real ones). Keep that login to the Stream pods and the replay job, and compare a run's `rowHash` with the one kept outside.
- Expect the replay to equal what the stream wrote for the period except where records share a receive time (ordered by kind and id rather than Kafka offsets) and for tracks that cross UTC midnight (the archive's pseudonyms change daily); the zones start empty at `--replay-from`, so start an hour or two before the period of interest.

Do not reset consumer offsets by hand to "replay" a period until the procedure has been verified in Phase 0 epic Queue engine core; Kafka keeps track samples for only a few days, and raw samples in TimescaleDB are the source for anything older.

## 5. Routine checks

| Frequency | Check |
|---|---|
| Daily | Zones `Degraded` or `Unknown` in the last 24 hours and why; device offline minutes; feed freshness; failed TickerQ jobs |
| Weekly | Disk growth against forecast; certificate expiries in the next 60 days; integration client lockouts; conservation residual and track completion trends |
| Monthly | Restore test on a copy (at least before go-live and after major releases); review of audit log for configuration changes |
| Quarterly | Re-validation of penalty-grade zones |
