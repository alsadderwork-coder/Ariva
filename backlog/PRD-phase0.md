# Phase 0 PRD: demo core

Generated from `backlog/generate.py`; edit the generator, not this file. The JSON tracker for ralph-tui is `backlog/prd-phase0.json`.

Phase 0 (Oct 2026 to Apr 2027): an end-to-end demo on simulated sensors, AODB and AMAN feeds, secure by default, deployable to the dev cluster. Gate: pilot contract signed.

## Quality gates appended to every story

- security-reviewer verdict PASS recorded in backlog/progress.md for the CWEs listed in this story
- docs and wiki pages affected by the change are updated (node scripts/verify.mjs docs passes)
- Plus the scope gates named per story: B = node scripts/verify.mjs backend passes (build with security analyzers, unit and security tests, security gate); I = node scripts/verify.mjs integration passes (Testcontainers); W = node scripts/verify.mjs web passes (svelte-check, lint, build); E = node scripts/verify.mjs e2e passes, including the new API end-to-end and Playwright functional tests for this story; S = node scripts/security/scan.mjs passes with 0 errors

## E0: Foundation and security baseline

### ARV-001: Restore, build and test the scaffold on a developer machine

The scaffold was verified offline (no NuGet access). Restore it where NuGet is reachable and make every existing gate green.

Depends on: nothing. CWEs: all (baseline). Gates: B, E, S.

- dotnet restore and dotnet build Ariva.slnx succeed with 0 errors
- All existing Ariva.UnitTests pass, including the Security tests (endpoint inventory, default deny, headers, limits, forbidden dependencies, entity binding, unsafe code, allowlist)
- npx playwright install chromium has been run and node scripts/verify.mjs e2e passes
- Any package version that fails to restore is replaced with a version confirmed through the nuget MCP and recorded in backlog/progress.md

### ARV-002: Finish container and chart hardening

The scaffold already runs images as numeric non-root user 10001 and sets runAsNonRoot, runAsUser, seccomp RuntimeDefault, allowPrivilegeEscalation false and drop ALL on every Deployment. Finish Platform/Cloud/CLAUDE.md.

Depends on: ARV-001. CWEs: CWE-269, CWE-79. Gates: S.

- Docker builds of every image succeed and each container starts under the chart security context on the dev cluster (verified by the human release, recorded in progress)
- .NET pods use readOnlyRootFilesystem true with an emptyDir at /tmp; the web pod gets emptyDirs for nginx cache and run directories
- Base images pinned by version and digest (aspnet and runtime-deps 10.0.x, nginx 1.28.x); every Ingress has a tls section bound to a configurable certificate secret
- helmfile-k8s.yaml defines environments dev, demo, prd, localk8s selecting the matching values file; prd values require an explicit buildNumber (no trunk)
- A chart test (node script under Platform/Cloud/Ariva.K8s/tests, run by verify security) renders the templates and fails if any Deployment lacks the security context or any Ingress lacks tls

### ARV-003: Local development environment with Docker Compose

One command brings up the dependencies agents and tests need locally.

Depends on: ARV-001. CWEs: CWE-269, CWE-287. Gates: S.

- docker-compose.dev.yml starts TimescaleDB (pinned timescale/timescaledb-ha image), Kafka in KRaft mode, Redis and smtp4dev with healthchecks
- Credentials come from .env.example copied to an ignored .env; no secret is committed
- scripts/dev-up.mjs and scripts/dev-down.mjs wrap compose cross-platform; README and wiki/04-Deployment-Guide.md local section updated
- The read-only database role used by the postgres-dev MCP is created by the dev bootstrap

### ARV-004: Port the domain foundations from AMAN

Port EntityBase, BaseAuditableEntity, BaseSoftDeletableEntity, IDomain, IHasDomainEvents, EventBase, PagedData, Search<TCriteria> and the Result and Fx.Specification usage (Fluentx) from ../Aman, renamed and trimmed.

Depends on: ARV-001. CWEs: CWE-501. Gates: B, S.

- Types live in Ariva.Core/Domain/Common and Ariva.Core/Services with no reference to Aman assemblies
- Fluentx and Mapster added through Directory.Packages.props at AMAN versions
- Unit tests cover id generation, audit fields, domain event collection and specification validation

### ARV-005: Port the NHibernate storage provider and unit of work

Port IStorageProvider, IUnitOfWork, SvcDb, SvcBase, the NHibernate provider, convention mapper and mapping rules. NHibernate only.

Depends on: ARV-003, ARV-004. CWEs: CWE-89, CWE-269. Gates: B, I, S.

- SchemaUpdate runs only when Database:AllowSchemaUpdate is true; only appsettings vm-local sets it
- ExecuteSqlAsync accepts parameters only (no overload taking an interpolated string)
- Testcontainers.PostgreSql added with a version confirmed by the nuget MCP; an integration test round-trips a sample entity with audit fields and a domain event
- The ported SvcBase keeps AMAN signatures where practical (ValidateRequestAsync, ExecutePagedQueryAsync)

### ARV-006: Versioned SQL script runner and database roles

Production schema and every TimescaleDB object come from Ariva.Infra/Timescale/Scripts/NNNN_*.sql.

Depends on: ARV-005. CWEs: CWE-89, CWE-269. Gates: B, I, S.

- The runner applies scripts in order, records name and SHA-256 checksum in schema_version, and fails startup when an applied script changed
- 0001_roles.sql creates a migration role (DDL) and a runtime role (DML only); runtime connection strings use the runtime role
- The runner runs as a one-shot job (command-line flag), not in every pod
- Integration tests prove ordering, idempotent re-run and tamper detection against a TimescaleDB container

### ARV-007: Logging and telemetry with secret redaction

Serilog and OpenTelemetry wiring with AMAN parity, plus redaction.

Depends on: ARV-001. CWEs: CWE-287, CWE-384. Gates: B, E, S.

- Structured JSON console logging; Loki and OTLP exporters enabled by configuration
- Authorization, Cookie, Set-Cookie, X-TOTP-Code headers and the access_token query parameter are redacted; Microsoft.AspNetCore.Hosting logs at Warning (Microsoft SignalR security guidance)
- Unit tests prove redaction of each item; no log line in the E2E run contains a bearer token

### ARV-008: Caching and Data Protection key ring

FusionCache with Redis backplane; Data Protection keys persisted in PostgreSQL and protected at rest.

Depends on: ARV-005. CWEs: CWE-287. Gates: B, I, S.

- FusionCache registered with tags and the Redis backplane (AMAN versions)
- Data Protection uses a custom IXmlRepository on PostgreSQL (parameterised SQL) and ProtectKeysWithCertificate with a certificate from a Kubernetes secret; dev generates a local certificate
- Integration tests prove two hosts share the key ring and that keys are not readable without the certificate

### ARV-009: Permission model and permission matrix

Port AMAN permissions and make authorization data-driven and testable.

Depends on: ARV-004. CWEs: CWE-862, CWE-863. Gates: B, E, S.

- Global.Defaults.Permissions with View, Create, Edit, Search, Delete per entity; Permission attribute and policy provider (no role strings)
- Role to permission seed for BorderShiftSupervisor, TerminalDutyManager, HandlerStationManager, SystemAdministrator
- security/permission-matrix.json lists every endpoint with the expected status per role; a unit test checks the seed against it and the E2E helper reads it

### ARV-010: User authentication with TOTP, rotation and step-up MFA

Replace the placeholder deny scheme with real authentication while keeping default deny.

Depends on: ARV-007, ARV-008, ARV-009. CWEs: CWE-287, CWE-306, CWE-384, CWE-269. Gates: B, E, S.

- Local users with PBKDF2-SHA256 (600,000 iterations) password hashes, constant-time comparison, lockout after 10 failures for 15 minutes, rate limit on login
- TOTP enrolment mandatory for SystemAdministrator, BorderShiftSupervisor and TerminalDutyManager; TOTP verified with a per-user time-step replay guard
- JWT access tokens (15 minutes, audience ariva-users, strict validation, 30 s skew); refresh tokens rotate on every use, are stored hashed, reuse revokes the family; refresh cookie HttpOnly, Secure, SameSite=Strict, path /auth
- A new session id is minted at every login; logout revokes the family
- [RequiresRecentMfa] attribute enforces a TOTP check within the last 15 minutes for critical functions
- E2E: login, wrong password, replayed TOTP, lockout, refresh rotation, refresh reuse revokes family, sid changes on login, token in query rejected on API routes

### ARV-011: User, role and audit administration

Administrators manage users and roles safely; every security-relevant action is audited.

Depends on: ARV-010. CWEs: CWE-269, CWE-863, CWE-306. Gates: B, E, S.

- User CRUD with role assignment through SvcRoleAssignment: no self-grant, cannot grant above own role, SystemAdministrator grants need step-up MFA and are audited
- TOTP reset flow requires step-up MFA and forces re-enrolment
- AuditEntry entity and search API (who, what, when, before and after summaries); audit rows cannot be edited or deleted through any API
- E2E: privilege escalation attempts return 403; audit rows appear for every create, grant, reset

### ARV-012: Site scoping

Every site-bound read and write is limited to the caller's sites.

Depends on: ARV-009, ARV-011. CWEs: CWE-863. Gates: B, E, S.

- ISiteScope service and [SiteScoped] filter; users and integration clients are bound to site codes
- An architecture test fails when a controller action takes a site, airport or terminal id without the scope
- E2E IDOR tests: requesting another site's ids returns 403 or 404 without data

## E1: Site topology and zone profiles

### ARV-013: Site topology entities

Airport, Terminal, Level, Checkpoint (CheckIn, Security, Emigration, Immigration), Desk (Counter, Desk, EGate, lane category) per docs/domain/glossary.md.

Depends on: ARV-004. CWEs: CWE-501. Gates: B, S.

- Rich entities with invariants (unique codes per parent, valid lane categories, desk kind rules)
- Unit tests for every invariant

### ARV-014: Topology persistence and AdminArea APIs

CRUD for airport, terminal, level, checkpoint and desk following the AMAN controller pattern.

Depends on: ARV-005, ARV-012, ARV-013. CWEs: CWE-862, CWE-863, CWE-89, CWE-501. Gates: B, I, E, S.

- Mappings, services (Result<T>, Fx.Specification, FusionCache tags), controllers with [Permission] and site scope, .http files
- Search endpoints use sort and filter allowlists
- E2E: CRUD per entity, permission matrix rows, cross-site 403, SQL injection and XSS payloads return 400 or are stored as inert text

### ARV-015: Desk ranges and external desk code mapping

Bulk create desks (for example D01 to D22) and map external codes (AMAN desk codes, AODB counter codes) to Ariva desks.

Depends on: ARV-014. CWEs: CWE-120, CWE-863. Gates: B, E, S.

- Range creation validated (max 200 per request, pattern checked)
- DeskCodeMapping entity unique per source system and site; used later by the AMAN feed consumer
- Unit and E2E tests

### ARV-016: Zone profile aggregate

Versioned zone geometry: Draft, Published, Retired; zones of type SnakeQueue, ServiceArea, OverflowBand, CountLine; entry and exit lines.

Depends on: ARV-013. CWEs: CWE-501. Gates: B, S.

- Invariants: at least 3 points, simple polygon (no self-intersection), entry and exit lines on the polygon edge, unique names, coordinates inside the level bounds
- Published versions are immutable; publishing produces a geometry hash stable across runs
- Table-driven unit tests for every invariant and the hash

### ARV-017: Zone profile API with publish workflow

Create a draft from the active version, edit zones, validate, publish.

Depends on: ARV-014, ARV-016, ARV-010, ARV-020. CWEs: CWE-306, CWE-862, CWE-863. Gates: B, E, S.

- Publish requires step-up MFA and raises ZoneProfilePublished through the outbox
- History endpoint lists versions with who and when
- E2E: full workflow, editing a published version fails, publish without recent MFA returns 403

### ARV-018: Floor plan assets

Upload a floor plan per level with scale and origin.

Depends on: ARV-014. CWEs: CWE-79, CWE-120, CWE-501. Gates: B, E, S.

- SVG uploads are sanitised (scripts, event attributes, foreignObject and external references removed) or rasterised; PNG and JPEG accepted; 20 MB limit; content type sniffed
- Stored through a storage abstraction (local disk in dev, S3-compatible later) with generated file names only (no user-supplied paths)
- E2E: SVG with script is neutralised, oversized upload 413, path traversal names rejected

### ARV-019: Seed the DMO demo topology

The fictional Demo International Airport from the prototype (48 counters in islands A to D, security North and South, 22 plus 22 desks, 4 plus 6 e-gates) and its zone profile v12.

Depends on: ARV-017. CWEs: CWE-269. Gates: B, I, S.

- Idempotent seed job usable in dev and demo, disabled in production
- Integration test proves re-running the seed changes nothing

## E2: Messaging, sensing and ingest

### ARV-020: Messaging over Kafka with outbox and dead letters

ISvcMessageBus implemented on MassTransit 8.5.11 with the Kafka Rider (ADR-0018), with Ariva-owned outbox, inbox and dead-letter filters that AMAN lacks.

Depends on: ARV-003, ARV-005. CWEs: CWE-77, CWE-120, CWE-501. Gates: B, I, S.

- KafkaTopics constants and startup provisioning (partitions, replication, retention); keyed producers AddProducer<string,T> with acks all, idempotence, 1 MB limit; one consumer group per service and endpoint
- NHibernate outbox_message table and single-leader relay (advisory lock, FOR UPDATE SKIP LOCKED, in-order per key); InboxFilter on processed_event in the consumer transaction; DeadLetterFilter outermost producing to <topic>.dlq.v1 with topic, partition, offset and error
- Endpoint defaults: AutoOffsetReset earliest, ConcurrentDeliveryLimit 1, CheckpointInterval 5 s, CheckpointMessageCount 500; AddSource and AddMeter MassTransit; bus health check in readiness
- Partition-aware raw Confluent consumer abstraction for Ariva.Api.Stream (assigned and revoked callbacks, StoreOffset after persist)
- Architecture test: no MassTransit or Confluent types referenced from Ariva.Core
- Testcontainers.Kafka integration tests: at-least-once with a crash between write and checkpoint, duplicate suppression, poison message reaches the DLQ, filter order (dead letter outside retry) proven

### ARV-021: Devices, calibrations and the canonical sensing model

Canonical events (LineCrossing, ZoneOccupancy, TrackPosition, IntervalCount, DeviceStatus), Device and Calibration entities, registry API.

Depends on: ARV-014, ARV-020. CWEs: CWE-287, CWE-501, CWE-863. Gates: B, E, S.

- Device credentials generated once and stored hashed; coverage footprint from mounting height (BOQ assumed table, labelled)
- Lifecycle Commissioning to Online only after a passed calibration (default 95 percent counting accuracy)
- Unit and E2E tests including permission matrix rows

### ARV-022: Device authentication scheme

[DeviceAuthenticated] for sensor pushes and gateways.

Depends on: ARV-010, ARV-021. CWEs: CWE-287, CWE-306, CWE-863. Gates: B, E, S.

- Device key header (hash compared in constant time) with optional client certificate; IP allowlist per device; rate limit per device
- Audience and scheme separate from users and integration clients
- E2E: missing key 401, wrong key 401, other device's zone 403, user token on device endpoint 401

### ARV-023: HTTPS push ingest with the Xovis dialect

First transport and first coded dialect.

Depends on: ARV-020, ARV-022. CWEs: CWE-501, CWE-120. Gates: B, E, S.

- Ingest endpoints for Xovis push and canonical push; 256 KB limit; schema validation; max events per message
- Clock offset estimated per device (EWMA); events outside tolerance flagged
- Events produced to ariva.sensing.events.v1 keyed by zone id
- Conformance tests from recorded or vendor sample payloads plus malformed, oversized and skewed cases; vendor details verified and sources recorded

### ARV-024: Declarative mapper and MQTT transport

Long-tail device support without code.

Depends on: ARV-023. CWEs: CWE-94, CWE-501. Gates: B, I, S.

- Restricted JSONPath subset (no expressions, no scripts) with tests proving hostile paths are rejected
- MQTT transport (TLS, per-device credentials, topic ACL) with MQTTnet at a version confirmed by the nuget MCP
- A generic LiDAR perception dialect defined declaratively from a documented sample, with conformance tests

### ARV-025: Device heartbeat and degraded zones

Offline detection and zone degradation.

Depends on: ARV-023. CWEs: CWE-862. Gates: B, E, S.

- Heartbeat timeout marks a device Offline and its zones Degraded; recovery restores them; events published
- Device health API and metrics
- Unit and E2E tests

### ARV-026: Raw sensing event archive

Archive canonical events for replay.

Depends on: ARV-006, ARV-023. CWEs: CWE-89. Gates: B, I, S.

- 0002 script creates the sensing_events hypertable with retention and compression policies (values marked to confirm)
- Binary COPY writer with batching; replay reader by time range and zone
- Integration tests for write, read and retention

## E3: Simulation

### ARV-027: Port the reference scenario engine

Port docs/design/prototype/app/assets/sim.js (seeded day model) to C# in Ariva.Simulation.Api.

Depends on: ARV-004. CWEs: CWE-306. Gates: B, S.

- Same seed gives identical outputs; default seed 9303
- Unit test reproduces the scripted events: arrivals Visitors nowcast passes 15 minutes at 18:05; S-17 offline 18:20 to 18:30; Handler B breach from 19:10 for three bins
- The simulator refuses to start in k8s-prd (already enforced) and its control endpoints require authorization

### ARV-028: Sensor emulator

Emit the scenario as device traffic.

Depends on: ARV-027, ARV-023. CWEs: CWE-287. Gates: B, E, S.

- Per-passenger crossings and zone occupancy in the Xovis and generic dialects over HTTPS to Ingest, authenticated as registered devices
- Controls: start, pause, speed, jump, seed; all authorised
- E2E: a 10-minute accelerated run produces the expected event counts in Kafka

### ARV-029: AODB, AMAN and immigration emulators

Integration partners for tests and demos.

Depends on: ARV-027, ARV-020. CWEs: CWE-287. Gates: B, S.

- AIDX push and ACRIS pull endpoints for the scenario schedule
- AMAN feed on Kafka and REST, with its own TOTP client exercising Ariva's Integration API, and a mock AMAN auth endpoint for Ariva's outbound TotpClientCredentials handler
- A mock immigration system that uses the generic REST endpoints

## E4: Queue state engine and stream

### ARV-030: Pure queue state engine

Ariva.Core engine: entries and exits from crossings, occupancy, FIFO wait per track (T3) and cumulative-curve wait (T1), censoring, watermark for late and out-of-order events.

Depends on: ARV-016, ARV-021. CWEs: CWE-120. Gates: B, S.

- No I/O, time passed in; bounded state per zone
- Table-driven tests from docs/domain/formulas.md including missing exits, late events and clock skew

### ARV-031: Realised wait attribution and bins

Attribution to the entry interval, provisional to final, minute aggregates and arrival-weighted P90 per 15-minute bin.

Depends on: ARV-030. CWEs: baseline. Gates: B, S.

- Rules exactly as formulas.md; finals change only through a recorded recomputation
- Tests reproduce the documented numeric cases

### ARV-032: Nowcast and throughput

Nowcast (Q + 1) / throughput with the documented estimator.

Depends on: ARV-030. CWEs: baseline. Gates: B, S.

- Zero throughput returns no service with a reason; degraded zones return bands
- Tests from formulas.md

### ARV-033: Desk state engine

Desk state from integration, sensor and approach signals with precedence and staleness.

Depends on: ARV-030. CWEs: baseline. Gates: B, S.

- States Closed, Idle, Serving, Paused, Unknown; per-desk minute aggregates only (no identities)
- Tests for precedence and staleness

### ARV-034: Stream host

Ariva.Api.Stream workers run the engines on Kafka input and persist outputs.

Depends on: ARV-031, ARV-032, ARV-033, ARV-026, ARV-020. CWEs: CWE-120, CWE-501. Gates: B, I, S.

- Consumer group, per-zone state snapshot and restore on rebalance, StoreOffset after COPY commit
- 0003 script creates queue_minute, desk_minute, egate_minute and the 15-minute continuous aggregate
- Integration tests: restart mid-stream yields identical minute rows (idempotent upserts)

### ARV-035: Live snapshots and the SignalR hub

Push live state to the web.

Depends on: ARV-034, ARV-010, ARV-012. CWEs: CWE-862, CWE-863, CWE-384. Gates: B, E, S.

- Snapshots per zone in Redis; LiveHub in Ariva.Api.Main with [Authorize], group joins authorised per permission and site, MessagePack, Redis backplane
- access_token query accepted only on /hubs and redacted from logs
- E2E: unauthorised join rejected, cross-site join rejected, authorised client receives updates

### ARV-036: Golden replay

Deterministic replay for evidence and regression.

Depends on: ARV-034, ARV-028. CWEs: baseline. Gates: B, I, S.

- Replay command over archived events for a range and profile version produces a stable output hash
- CI test: seed 9303 reproduces 18:05, 18:20 to 18:30 and 19:10 exactly

## E5: Alerting

### ARV-037: Alert rules

Typed rules (metric, comparator, threshold, sustain, severity, owner role, escalation, channels); no expression strings.

Depends on: ARV-014, ARV-009. CWEs: CWE-94, CWE-862, CWE-863. Gates: B, E, S.

- Seeded rules R-001 to R-005 reproduce the prototype behaviour
- CRUD with permissions and site scope
- Unit and E2E tests including hostile strings in names

### ARV-038: Rule evaluation and backtest

Evaluate rules every minute; backtest against stored minutes.

Depends on: ARV-037, ARV-034. CWEs: baseline. Gates: B, I, S.

- Sustain windows, dedupe, auto-resolve
- Backtest returns first fire time and count; a test proves backtest equals live firing for seed 9303 (R-001 at 18:05)

### ARV-039: Alert lifecycle

Acknowledge, escalate, resolve; live push; audit.

Depends on: ARV-038, ARV-035. CWEs: CWE-862, CWE-863. Gates: B, E, S.

- Escalation after N minutes to the configured role
- E2E for each transition and its authorization

### ARV-040: Email notifications

MailKit channel with templates.

Depends on: ARV-039. CWEs: CWE-501. Gates: B, E, S.

- Templates from Ariva.Resources; recipients validated; header injection impossible (CRLF stripped); rate limited
- smtp4dev used in dev and E2E

## E6: Flights and AODB integration

### ARV-041: Flights and allocations model

FlightLeg, FlightEvent, CounterAllocation and feed freshness.

Depends on: ARV-014. CWEs: CWE-501. Gates: B, S.

- Stale feed detection raises an alert condition
- Unit tests

### ARV-042: Integration clients and TOTP authentication

docs/architecture/integration.md inbound authentication, AMAN-compatible and hardened.

Depends on: ARV-010, ARV-008, ARV-012. CWEs: CWE-287, CWE-306, CWE-863, CWE-269. Gates: B, E, S.

- IntegrationClient with hashed secret, encrypted TOTP seed, scopes, site codes, CIDRs; created with step-up MFA; secret and provisioning URI shown once
- POST /api/v1/auth with replay guard, generic invalid_client, rate limits and lockout; integration signing key and audience separate
- [IntegrationScope] with site binding; per-request X-TOTP-Code policy; audit of every call
- E2E: success, wrong secret, wrong code, replayed code, lockout, wrong scope, other site, integration token on a user endpoint

### ARV-043: Integration API: flights and allocations

Generic endpoints for any AODB.

Depends on: ARV-042, ARV-041. CWEs: CWE-501, CWE-120, CWE-89. Gates: B, E, S.

- Batch endpoints with Idempotency-Key, unknown-field rejection, 500 items and 1 MB limits, per-item results
- E2E including duplicates, oversized, malformed, injection payloads

### ARV-044: AIDX 22.1 inbound

Hardened XML adapter.

Depends on: ARV-043, ARV-029. CWEs: CWE-501, CWE-120. Gates: B, E, S.

- DtdProcessing.Prohibit, XmlResolver null, schema validation, 5 MB limit
- Maps to FlightLeg and FlightEvent; E2E with the emulator and an XXE payload that must be rejected

### ARV-045: Outbound endpoints and the ACRIS connector

Registry and auth handlers with SSRF controls.

Depends on: ARV-042, ARV-029. CWEs: CWE-918, CWE-306. Gates: B, E, S.

- OutboundEndpoint registry (step-up MFA), handlers TotpClientCredentials, OAuth2ClientCredentials, ApiKeyHeader, HmacSignature, MutualTls
- Named clients, resolved-IP CIDR check, no redirects, resilience policies
- ACRIS pull against the emulator; tests for metadata IP, loopback, DNS rebinding and redirect refusal

### ARV-046: SSIM import

Schedule fallback.

Depends on: ARV-041. CWEs: CWE-120, CWE-501. Gates: B, E, S.

- Streaming parse with line and size limits; preview before commit; E2E with malformed and oversized files

### ARV-047: Arrival-wave projection

Flights landing in the next 30 minutes with lane split and predicted hall arrival curve (8 to 15 minutes after on-block over about 12 minutes).

Depends on: ARV-041, ARV-048. CWEs: CWE-863. Gates: B, E, S.

- Uses AMAN lane demand when present, the default mix otherwise; API with permissions
- Tests from formulas.md

## E7: AMAN and immigration integration

### ARV-048: AMAN feed and immigration endpoints

Consume the four V1 contracts from Kafka and REST.

Depends on: ARV-042, ARV-020, ARV-015, ARV-029. CWEs: CWE-501, CWE-863. Gates: B, I, E, S.

- Validation: 60-second intervals, consistent sums, small-cell suppression respected, unknown fields rejected
- Desk code mapping applied; idempotent by SourceEventId
- E2E with the emulator over both transports

### ARV-049: Desk sessions and e-gate coupling

Feed AMAN signals into the engines.

Depends on: ARV-048, ARV-033. CWEs: baseline. Gates: B, I, S.

- Desk sessions at their precedence rank; e-gate rejects added to manual lane demand
- Unit and integration tests

### ARV-050: Outbound AMAN connector

Pull from AMAN's Integration API when Kafka is not shared, using TotpClientCredentials.

Depends on: ARV-045, ARV-048. CWEs: CWE-287, CWE-918. Gates: B, E, S.

- Tested against the mock AMAN; token cached and refreshed; X-TOTP-Code per request

## E8: Web application

### ARV-051: Web shell, login and design tokens

SvelteKit shell per the prototype with shadcn-svelte.

Depends on: ARV-010. CWEs: CWE-384, CWE-79, CWE-287. Gates: W, E.

- Login with TOTP; access token in memory; refresh through the cookie; logout
- Sidebar and top bar per prototype; English and Arabic with RTL; dark default with light theme
- Playwright: login, RTL, no console errors, no CSP violations, XSS probes on the login form

### ARV-052: Topology admin screens

Airport to desk administration.

Depends on: ARV-051, ARV-014. CWEs: CWE-79, CWE-863. Gates: W, E.

- Per-role visibility
- Playwright per screen and role, XSS probes on names

### ARV-053: Zones screen and editor

Floor plan, profiles, draft editor, publish.

Depends on: ARV-051, ARV-017, ARV-018. CWEs: CWE-79, CWE-306. Gates: W, E.

- Drag vertices and lines with a keyboard vertex table alternative; validation messages; publish with step-up MFA
- Playwright for editing, publishing and role hiding

### ARV-054: Devices screen

Registry, coverage on plan, register sensor, record calibration, health.

Depends on: ARV-051, ARV-021, ARV-025. CWEs: CWE-79, CWE-287. Gates: W, E.

- Device secret shown once with copy; never re-displayed
- Playwright per role

### ARV-055: Live operations screen

KPI tiles, floor plan by nowcast, desk states, wait chart, alerts, arrival-wave strip.

Depends on: ARV-051, ARV-035, ARV-039, ARV-047. CWEs: CWE-79, CWE-863. Gates: W, E.

- Live updates through SignalR; stale indicator
- Playwright with the simulator: the 18:05 alert appears and can be acknowledged

### ARV-056: Alert rules screen

Rules list, create with backtest preview.

Depends on: ARV-051, ARV-038. CWEs: CWE-79, CWE-94. Gates: W, E.

- Playwright: create a rule, preview first fire time, rule fires live at the same time

### ARV-057: Immigration screen

Border module view.

Depends on: ARV-051, ARV-049. CWEs: CWE-863, CWE-79. Gates: W, E.

- Lane waits, desk grid aggregates for border roles only, e-gate utilisation and rejects
- Playwright: Terminal duty manager sees aggregates only; no identifiers anywhere

### ARV-058: Passenger display

Display configuration and the kiosk route.

Depends on: ARV-051, ARV-035. CWEs: CWE-79, CWE-306. Gates: W, E.

- Bilingual bands with hysteresis and stale fallback; display players authenticate with device credentials
- Playwright at 1920x1080 including stale data

### ARV-059: Users and access screen

Users, roles, TOTP reset, audit log.

Depends on: ARV-051, ARV-011. CWEs: CWE-269, CWE-79. Gates: W, E.

- Playwright: escalation attempts blocked in the UI and by the API

## E9: Reports

### ARV-060: Daily report and scheduled delivery

Peaks per lane, P50 and P90 per hour, alerts, device uptime; CSV export; scheduled email.

Depends on: ARV-034, ARV-040. CWEs: CWE-79, CWE-306. Gates: B, I, E, S.

- CSV neutralises formula prefixes (=, +, -, @)
- TickerQ in Ariva.Api.Cronz with its dashboard behind authorization
- Integration and E2E tests

### ARV-061: Reports screen

Report preview and export.

Depends on: ARV-060, ARV-051. CWEs: CWE-79. Gates: W, E.

- Playwright per role

## E10: Deployment and demo

### ARV-062: Complete Helm deployment

All hosts, TimescaleDB, topic and migration jobs, environments.

Depends on: ARV-002, ARV-006, ARV-020. CWEs: CWE-269. Gates: S.

- Chart tests pass; helmfile environments; pipelines updated; wiki deployment guide matches

### ARV-063: Dynamic security scanning pipeline

OWASP ZAP baseline and OpenAPI scans against the dev deployment.

Depends on: ARV-062. CWEs: all. Gates: S.

- Security-zap.yaml fails on high findings and publishes the report
- Findings triaged in backlog/progress.md

### ARV-064: Scripted demo and runbook

A repeatable demo on the dev cluster.

Depends on: ARV-055, ARV-057, ARV-058, ARV-036, ARV-062. CWEs: baseline. Gates: E.

- Simulator starts at 17:40 seed 9303; the three events show on the live screens
- A Playwright demo test walks the three events
- wiki runbook page updated

### ARV-065: Phase 0 exit review

Gate before the pilot.

Depends on: ARV-064, ARV-063. CWEs: all. Gates: B, I, W, E, S.

- All gates green; full-repository security review PASS; coverage report attached
- List of open "to confirm" items for the pilot contract
