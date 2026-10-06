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
- **Alarm (ARV-041)**: every minute Integration judges each feed of each site: Stale when nothing came for longer than its cadence (`Flights:Feeds:DefaultStaleAfterMinutes`, 20, or per feed in `Flights:Feeds:StaleAfterMinutes`) while flights are due at the site (not cancelled or diverted, not yet on block or off block, expected from 120 minutes ago to 60 minutes ahead); Idle when silent with no flight due; Fresh otherwise. An SSIM import (`ssim`, cadence 0) is never stale. Going stale logs the warning "Flight feed {feed} of {site} is stale: nothing for n min while n flights are due" and counts `ariva.feeds.went_stale`; the gauge `ariva.feeds.stale` (meter `Ariva.Flights`) is the number stale now; recovery logs "is Fresh again" and counts `ariva.feeds.recovered`. Only a call with at least one item Ariva could check counts as a message: an empty batch, or one whose items are all refused, does not, so a feed that sends only refused data goes Stale (look for `ariva.flights.items` with outcome `refused`). In the database, `feed_freshness` shows each feed's last message, state, since when and the flights due at the last sweep.
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

Ariva.Api.Stream's queue engine worker (group `ariva-stream.queue-engine`) writes `queue_minute`, `queue_bin`, `line_minute` (per-line crossings per minute, ARV-113), `zone_health_bin` (the F18 health checks of each bin, ARV-114a), `overflow_minute` (each overflow band's occupancy per minute, ARV-115) with its `OverflowDetected` events in `outbox_message`, the zone snapshots (`stream_zone_state`) and its own positions (`stream_offset`) in one transaction per checkpoint. To check it is keeping up, compare `stream_offset.next_offset` with the end of each sensing topic partition and look at `stream_zone_state.updated_on`.

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
- **Known limits**: `DesksBelowPlan` rules have nothing to judge yet; `OverflowOccupied` rules (since ARV-115) judge only zones whose overflow band has a commissioned sensor reporting occupancy (a band without one shows nothing, and is not counted as empty; a band whose sensor falls silent for more than 2 minutes becomes Unknown, an `OverflowDetected` with state Unknown, and the rule neither raises nor clears on it until the sensor reports again; check the band's sensor under Devices); predicted-nowcast rules read the arrival-wave projection (ARV-047) and have nothing to judge for a queue zone none of whose service zones stands at a desk with lane categories (link the zones to their desks in the zone profile). A new or re-enabled rule starts at the present (use the backtest for the past). After the Stream host was down for more than three hours (`Alerts:Evaluation:MaxCatchUpMinutes`), evaluation continues from the last three hours.
- **Actions**: fix the data source; an edit of the rule restarts its counts. Do not delete `alert_rule_state` rows by hand.

### 4.8c Alert emails not arriving (ARV-040)

- **Symptoms**: an alert was raised or escalated but its people got no email.
- **Checks**: is `Email:Enabled` on in the base settings, and does the rule notify by email (`notifyByEmail`)? Who is told: when raised, the people of the rule's owner role (every operational role for a rule without owner); when escalated, the people of the escalation role; each enabled, holding the role and the alert's site, with an email address that is one plain address. A rule that escalates to a named contact rather than a role sends no escalation email. In the database, `email_message` has one row per alert, kind and address: `Pending` (waiting or to be retried at `next_attempt_utc`), `Sent`, `Suppressed` (held back by the hourly limit per address, `Email:MaxPerRecipientPerHour`) or `Failed` (given up after `Email:MaxAttempts`). `reason` names the error type of the last failure, never the server's reply.
- **Held back at sending**: an email is sent only while its recipient is still an enabled account with that address, holding the alert's site and the role it was written for; otherwise the round marks it Suppressed ("The recipient no longer holds ..."). One Integration replica sends at a time; the others skip the round.
- **Log lines**: Integration logs "Email round: n sent, n to retry, n given up, n held back" when a round has any of those and "Email round failed" with the error when a whole round fails; the writing host logs "recipients skipped for an address that is not valid".
- **Actions**: fix the relay or its credentials; pending emails go out on the next rounds. Fix an address in the user's profile (Administration guide); emails already written keep the old address. Do not edit or delete `email_message` rows: the runtime role can change only how sending went (never the recipient, subject or body), Sent, Suppressed and Failed are final, and the rows are the record of who was told what.
- **Known limits**: delivery is at least once: if Integration stops between the relay accepting a message and the round's commit, that message is sent again. No digest or quiet hours yet; a burst over the per-minute limit waits for the next minutes.

### 4.8d Integration client locked out (ARV-042)

- **Symptoms**: an AODB or immigration system stops sending; its token exchanges answer 401 `invalid_client`.
- **Checks**: Integration logs "Integration token refused for {client}: {reason}" for each failure (wrong secret, wrong or reused TOTP code, address outside the allowed networks, disabled, locked, too many attempts in a minute) and "Integration client {client} locked until ..." when ten consecutive failures lock it (also the audit entry `IntegrationClient.LockedOut` and the counter `ariva.integration.lockouts`, meter `Ariva.Integration`). The client's view in administration shows `locked`; the failure count starts again at 0 when the lock is set, so a locked client with a count of 0 is normal. Common causes: the system's clock drifted (TOTP), the secret was rotated without the system being updated, a new source address, or a code used twice within one 30-second step.
- **What a lock does and does not do**: a lock stops new token exchanges only. Tokens the client already holds keep working until they expire (15 minutes), so a feed that is running does not stop at the moment of the lock; it stops when its token expires and the next exchange is refused. Exchanges from addresses outside the client's allowed networks are refused without counting, so a lockout always comes from inside those networks: either the integrator's own system or something on that network. Calls beyond 5 exchanges a minute for one client are refused without counting either.
- **Actions**: fix the cause with the integrator, then unlock the client (it also unlocks by itself after 15 minutes). Enabling a disabled client does not clear a lock; unlock it separately. Repeated lockouts with no cause on the integrator's side are an attack from inside the client's networks: narrow the allowed networks, disable the client (its tokens stop at once), rotate its secret and reset its seed. `integration_call` records every accepted or refused API call of the client with its payload's SHA-256 (the source address too), which shows where the failures came from.

### 4.8e Integration batches and AIDX messages refused or replayed (ARV-043, ARV-044)

- **Symptoms**: an integrator reports 422 on a batch, or that a batch "did nothing", or that its feed went stale while it was sending.
- **Checks**: `integration_call` shows each call of the client with its status and payload SHA-256 (route `.../flights/batch`, `.../flights/events`, `.../allocations/batch`). A 422 means the integrator reused an `Idempotency-Key` for a different body, endpoint or site within 24 hours: their retry logic is generating keys per attempt group rather than per batch. A batch "doing nothing" is usually a replay (the answer carries `Idempotent-Replayed: true`) or items that were `unchanged` (nothing newer than what Ariva knows) or refused (each item's `errors`). `integration_idempotency` holds each key's claim and answer for 24 hours (read only for the runtime role apart from storing the answer once); Ariva.Api.Integration deletes expired keys every 10 minutes ("Deleted {Count} expired idempotency keys"). A feed is the client's own (`api-` and the client id without `ic_`), so the stale-feed alarm of runbook 4.3 names the client. A 429 is either the client's allowance (120 batches a minute per replica, `Retry-After` given; the call is in `integration_call`) or the replica's at-once limit (8 handled, 16 waiting; not in `integration_call`, since it is refused before authentication). Sustained 429s for one client mean its system sends tiny batches too often: ask for fuller batches; raise `Security:RateLimiting:IntegrationClient` only with a reason.
- **Actions**: tell the integrator to use one new key per new batch and the same key only when retrying that batch unchanged. For AIDX (route `.../aodb/aidx`), a 400 names only the line and position where the message broke Ariva's AIDX profile or was not well-formed (a DOCTYPE is always refused); ask the integrator for that message and compare it with wiki 08 section 8. Legs refused in a 200 answer are `Warning` elements with `Status="Refused"` and the reason; "exactly one airport of this site" means the site's airports (topology) do not include the leg's airport, or the AODB sends ICAO codes the airport record lacks. Never delete keys by hand to "unblock" a client: a deleted key lets a retry apply the batch again. If the sweep logs failures, the keys simply live longer; the table grows by one row per batch.

### 4.8f Outbound endpoint failing (ARV-045)

- **Symptoms**: an ACRIS flight feed goes stale (runbook 4.3 names the feed `acris-` and the endpoint's code); the endpoint's view in administration shows a growing `consecutiveFailures` and its `lastStatus`.
- **Checks**: Integration logs "ACRIS pull from {endpoint} refused: {reason}" (warning) or "... failed". `lastStatus` says which: "Refused to connect: the address is outside the endpoint's allowed networks" (the partner moved or its DNS changed: confirm with the partner before widening the networks, and never to a broad range), "...link-local or metadata..." or "...loopback..." (the name now points at a forbidden address: treat as an attack until explained), "...redirect, which Ariva does not follow" (ask for the final URL), "The circuit to {endpoint} is open after N failures in a row" (it retries after the break), "The endpoint answered 401" (the key or credentials changed: replace the secret), "Not modified." (normal), "Pulled N flights: a applied, r refused" (refused flights touch none or both of the site's airports, or break the flight rules). "The endpoint's secret cannot be read with this key ring; set the secret again." (logged as an error "its secret cannot be unprotected") means the Data Protection key that protected the secret is gone (a ring restored from an older backup, or another environment's database): only that endpoint stops; set its secret again.
- **Actions**: fix the cause with the partner; change the connection or replace the secret (both critical actions); the next due pull uses it. Disable an endpoint that must stop at once.

### 4.8g SSIM import refused or partial (ARV-046)

- **Symptoms**: a preview or import answers 400, 409 or 413, or the preview lists line errors.
- **Checks**: 400 "no flight leg records" means the file is not chapter 7 (or is another encoding); "The site has no airport" means the site has no terminal under an airport in the topology; 409 means the import is not what was previewed (the file was re-exported or edited, the horizon or site differs, another administrator sent it, the day changed, or the preview is older than two hours): preview again; 413 is a file over 20 MB (split it by carrier). Line errors give the line number and the field that broke (period, days, times, variations, frequency, length, characters). Legs of other airports are skipped, not errors. An import that reports many `unchanged` legs met legs a live feed has reported (`flight_leg.schedule_fallback = false`) or an earlier import that said the same: expected, since a schedule never changes a leg a live feed has reported. The audit entry `FlightSchedule.Imported` holds each import's SHA-256, outcome (`completed` or `failed`), units committed and counts; a failed import keeps the units it committed (500 legs each) and can be imported again, since repeated legs are `unchanged`.
- **Actions**: fix the file with the airline or the AODB team, preview again, import with the new preview token.

### 4.8h AMAN feed or immigration records refused or kept apart (ARV-048)

- **Symptoms**: the immigration endpoints answer items with `errors` or warnings, Integration logs "AMAN {contract} record refused" or "Border feed code {code} of site {site} has no AMAN desk code mapping", `ariva.border.feed.records{outcome="refused"}` or `ariva.border.feed.unmapped` rises, or records appear on `ariva.aman.feed.*.v1.dlq.v1`.
- **Checks**: a refusal names the rule: `intervalSeconds is 60`, `starts on a whole minute`, `at most 7 days ago and not later than Ariva's clock allows` (clock skew, or a simulator run faster than real time: `Border:Feed:AheadMinutes`), sums that do not add up, `small-cell suppression` (AMAN must fold categories under 3 into `Other`), lanes, a site other than the call's or one Ariva does not know. Dead letters are values that are not the contract (an unknown member, an enum that is not an exact member name, a time without an offset): check AMAN's contract version against `ContractVersion.Current`. An unmapped code is stored with no desk (`desk_id` null in `border_desk_*`, `border_egate_interval`): add the AMAN desk code mapping (Administration guide, desk code mappings); records received before the mapping keep no desk. `unchanged` means the record was already received (the same `sourceEventId` at the site, over either transport) or, for lane demand, a newer computation is known.
- **Actions**: map the code; ask AMAN to correct the contract or the suppression; never edit the tables (the runtime role cannot).

### 4.8i Desk states Unknown or e-gate minutes missing (ARV-049)

- **Symptoms**: AMAN desks show `Unknown` in `desk_minute` (`unknown_seconds` 60), `egate_minute` has no rows for a site, or Stream logs "Desk feed read failed".
- **Checks**: Stream's desk feed reads the records the immigration intake stored (4.8h first: no stored records, no desk states). AMAN desks turn `Unknown` two minutes after AMAN's last record of the site (the feed is their heartbeat); a desk AMAN never reports a session for is `Closed`. Only desks with an AMAN desk code mapping take part, keyed site/checkpoint/desk in the minute tables. `desk_feed_state` holds each site's read position and engine (`updated_on` moves every `Border:DeskFeed:PollSeconds` while Stream runs; a stale one means the worker stops or another replica holds the site's lock). Records more than 15 minutes late, or more than 5 minutes ahead of Stream's clock, are not applied (a simulator run faster than real time). A burst larger than `MaxRead` is read over several polls; a record whose intake transaction committed more than two minutes after it was stamped is not read (the overlap), and a record dated ahead of Stream's clock never extends the feed's heartbeat. Two desks whose checkpoints share a code on different levels share a key and are left out (logged): rename one checkpoint.
- **Actions**: restore AMAN's feed or the mapping; restart Stream if `updated_on` is stale. Deleting a site's `desk_feed_state` row (migration role) restarts its desks from now; never edit the minute tables.

### 4.8j AMAN pull failing (ARV-050)

- **Symptoms**: an `AmanFeed` outbound endpoint shows `consecutive_failures` rising, or its `last_status` reports refused or unreadable records; Integration logs "AMAN pull from {endpoint} failed".
- **Checks**: `AMAN answered 401.` is a refused exchange (client id, secret or TOTP seed wrong, or the host's clock more than 30 s off) or a refused call (the code on each call is wrong: the clock or the seed); "already requested in this TOTP step" means a retry within the step and clears itself; "not its feed envelope", "did not move forward" or "more than 1000 records" mean AMAN's answer is not the feed Ariva expects (check AMAN's version and the feed path); unreadable records are not the V1 contracts (4.8h); a refused or unreachable address is the SSRF control (4.8f, the endpoint's networks). The position per contract is in `aman_pull_cursor`.
- **Actions**: set the secret again (`PUT {id}/secret`), correct the connection, or ask AMAN to fix its feed. If AMAN restarted its sequence, a position beyond it finds nothing new: register a new endpoint (a new position) and disable the old one.

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
| Reproduce a day for tests or a demo | Ariva.Simulation.Api plays the reference scenario through the whole pipeline in real time (section 4.11); recorded customer feeds are not replayed yet | Built for the reference scenario (ARV-064); recorded feeds Target |
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

### 4.11 Scripted demo (ARV-064)

The reference evening at Demo International Airport (DMO, seed 9303) played in real time through the whole pipeline: the simulator's sensors push to Ingest as registered, calibrated devices, AMAN's feed goes through Kafka to Integration and the desk engine, Stream computes the queues, the nowcast (now with the AMAN desk term) and the alert rules, and Main relays both to the screens. Nothing is planted. Three scripted events, each seen by the role that owns it:

| Demo time | What happens | Who sees it | Account (local) |
|---|---|---|---|
| 18:05 | A visitor-heavy arrival wave: the arrivals Visitors (A-VIS) nowcast passes 15 minutes and R-001 fires | Border shift supervisor | `demo.border` |
| 18:20 to 18:30 | Sensor S-17 over the arrivals hall goes silent: R-003 names it, Devices shows it Offline, then it recovers | Border shift supervisor, system administrator | `demo.border`, `demo.admin` |
| 19:10 | Handler B's check-in island C after a shift change: the 15-minute bin breaches the 15-minute P90 target and R-004 fires once the bin has ended (19:15) | Handler station manager | `demo.handler` |

Two runs:

- Full rehearsal, 17:40 to 19:40 (about two hours): all three events, the bins and the reports. Run it the day before any demo.
- Short live demo, 17:50 to 18:35 (about 45 minutes): the arrival wave and the sensor outage. Start at 17:50 rather than 17:55: the sensors' first ten minutes are their warm-up (step 3 below), and R-001 should fire on screen, not during the warm-up.

On a laptop (Docker and the .NET SDK, Node 22):

On Windows, `run-ariva.cmd` in the repository root does steps 1 and 2 in one go: it checks the tools (.NET, Node and a running Docker engine, such as Rancher Desktop), runs `npm ci` and `prepare` the first time, starts the AppHost in its own window, waits until Ariva answers, opens http://localhost:51010 and keeps a table of the four demo accounts with their passwords and the administrator's current authenticator code (press 1 to 4 to copy a password, C to copy the code). `run-ariva.cmd -Demo` also runs step 3. The passwords are generated on the laptop in `.demo/` and never committed; the script is for a developer machine only.

1. Once: `npm ci` in `Platform/Frontplane/Ariva.Web`, then `node scripts/demo-local.mjs prepare`. It writes `.demo/accounts.json` (the four demo accounts with random passwords; the administrator also has a TOTP secret) and `.demo/apphost-environment.json` (git-ignored, owner-only) and prints the AppHost command.
2. Start Ariva with that command: `dotnet run --project Platform/Cloud/Ariva.AppHost -- "--AppHost:HostEnvironmentFile=<repository>/.demo/apphost-environment.json"`. Wait until the Aspire dashboard (http://localhost:15880) shows every resource Running. The accounts sign in without a second factor (`Auth:TotpRequired` false): Ariva.Api.Main refuses to start with that setting, or with development accounts, unless both its host environment and `Application:Environment` are vm-local. `.demo/` holds passwords: it is owner-only on Linux and macOS, while Windows ignores those modes, so keep the repository under your own user profile.
3. `node scripts/demo-local.mjs start` for the full rehearsal, or `node scripts/demo-local.mjs start --at 17:50 --until 18:35` for the short demo. It registers and calibrates the sensors (one counting sensor per queue and overflow band, plus S-17; `--sensors events` keeps only the seven the events need), loads them into the simulator and starts on the next whole minute, up to 14 demo minutes before `--at`, so that the 15-minute bins fall on the wall clock's quarter hours and the sensors settle before the demo proper. It prints the accounts; `node scripts/demo-local.mjs code` gives the administrator's current code.
4. Open http://localhost:51010 and sign in. Live operations shows every queue; the immigration screen shows desks, lanes and waits; Alerts shows the events as they fire. `node scripts/demo-local.mjs status` shows the demo clock.
5. `node scripts/demo-local.mjs stop` pauses the simulator and unloads the sensors. The devices stay registered; the next `start` reuses them with new credentials.

What to expect on screen:

- During the first minutes after a start, sensors report but their queues may show "Data degraded" until Ariva has learned each sensor's clock (F19: ten readings). The pre-roll in step 3 covers this.
- A wait shown with "≈" is an estimate from the exit rate alone (no desk state for that queue, as for check-in, which has no AMAN desks): it keeps its colour. "Data degraded" is kept for doubtful data: a stale zone, or a queue length during a counting sensor's outage. S-17 only reports its health, so its outage raises R-003 without degrading A-VIS.
- R-003 alerts for every sensor at the start mean the sensors were calibrated long before the simulator started (more than three minutes): run `stop` and `start` again.

As an automated check (the cloud workspace and CI, not a demo): `ARIVA_E2E_DEMO=1 npx playwright test --project=demo` in `Platform/Testing/Ariva.E2E` runs the same setup and asserts each event on screen (`ARIVA_DEMO_START` and `ARIVA_DEMO_END` in demo minutes, 1060 is 17:40); `node scripts/verify.mjs demo` wraps it.

If an event does not show:

| Symptom | Check |
|---|---|
| A-VIS shows a wait but no R-001 | The A-VIS row's status: "Data degraded" means its length is not a full sensor reading (the overflow band's sensor S-25 not loaded) or still in warm-up; R-001 does not judge degraded lengths |
| No queues at all | `node scripts/demo-local.mjs status`: the clock running and the sensors listed; Ingest and Stream healthy in the dashboard |
| Every desk Unknown on the immigration screen | The AMAN feed: the simulator plays it with the demo clock, so it is Unknown until the clock runs; then section 4.8i |
| R-004 not by 19:16 | The 19:00 to 19:15 bin is judged once it has ended; the 15-minute bins need the alignment of step 3 (do not use `--no-align` for the rehearsal) |

## 5. Routine checks

| Frequency | Check |
|---|---|
| Daily | Zones `Degraded` or `Unknown` in the last 24 hours and why; device offline minutes; feed freshness; failed TickerQ jobs |
| Weekly | Disk growth against forecast; certificate expiries in the next 60 days; integration client lockouts; conservation residual and track completion trends |
| Monthly | Restore test on a copy (at least before go-live and after major releases); review of audit log for configuration changes |
| Quarterly | Re-validation of penalty-grade zones |
