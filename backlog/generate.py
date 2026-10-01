"""Generates backlog/prd-phase0.json (ralph-tui JSON tracker) and backlog/PRD-phase0.md from one source.
Run: python backlog/generate.py   (keeps "passes" and "notes" already recorded in prd-phase0.json)
"""
import json, os

HERE = os.path.dirname(os.path.abspath(__file__))

GATES = {
    'B': 'node scripts/verify.mjs backend passes (build with security analyzers, unit and security tests, security gate)',
    'I': 'node scripts/verify.mjs integration passes (Testcontainers)',
    'W': 'node scripts/verify.mjs web passes (svelte-check, lint, build)',
    'E': 'node scripts/verify.mjs e2e passes, including the new API end-to-end and Playwright functional tests for this story',
    'S': 'node scripts/security/scan.mjs passes with 0 errors',
}
ALWAYS = [
    'security-reviewer verdict PASS recorded in backlog/progress.md for the CWEs listed in this story',
    'docs and wiki pages affected by the change are updated (node scripts/verify.mjs docs passes)',
]

E = {}  # epic titles
def epic(code, title): E[code] = title

epic('E0', 'Foundation and security baseline')
epic('E1', 'Site topology and zone profiles')
epic('E2', 'Messaging, sensing and ingest')
epic('E3', 'Simulation')
epic('E4', 'Queue state engine and stream')
epic('E5', 'Alerting')
epic('E6', 'Flights and AODB integration')
epic('E7', 'AMAN and immigration integration')
epic('E8', 'Web application')
epic('E9', 'Reports')
epic('E10', 'Deployment and demo')
epic('E11', 'Engineering quality')

S = []
def story(id, ep, title, desc, ac, gates, cwe, deps=()):
    S.append(dict(id=id, epic=ep, title=title, description=desc, ac=ac, gates=gates, cwe=cwe, deps=list(deps)))

# E0
story('ARV-001', 'E0', 'Restore, build and test the scaffold on a developer machine',
      'The scaffold was verified offline (no NuGet access). Restore it where NuGet is reachable and make every existing gate green.',
      ['dotnet restore and dotnet build Ariva.slnx succeed with 0 errors',
       'All existing Ariva.UnitTests pass, including the Security tests (endpoint inventory, default deny, headers, limits, forbidden dependencies, entity binding, unsafe code, allowlist)',
       'npx playwright install chromium has been run and node scripts/verify.mjs e2e passes',
       'Any package version that fails to restore is replaced with a version confirmed through the nuget MCP and recorded in backlog/progress.md'],
      'BES', ['all (baseline)'])
story('ARV-002', 'E0', 'Finish container and chart hardening',
      'The scaffold already runs images as numeric non-root user 10001 and sets runAsNonRoot, runAsUser, seccomp RuntimeDefault, allowPrivilegeEscalation false and drop ALL on every Deployment. Finish Platform/Cloud/CLAUDE.md.',
      ['Docker builds of every image succeed and each container starts under the chart security context on the dev cluster (verified by the human release, recorded in progress)',
       '.NET pods use readOnlyRootFilesystem true with an emptyDir at /tmp; the web pod gets emptyDirs for nginx cache and run directories',
       'Base images pinned by version and digest (aspnet and runtime-deps 10.0.x, nginx 1.30.x stable on Alpine); every Ingress has a tls section bound to a configurable certificate secret',
       'helmfile-k8s.yaml defines environments dev, demo, prd, localk8s selecting the matching values file; prd values require an explicit buildNumber (no trunk)',
       'A chart test (node script under Platform/Cloud/Ariva.K8s/tests, run by verify security) renders the templates and fails if any Deployment lacks the security context or any Ingress lacks tls'],
      'S', ['CWE-269', 'CWE-79'], ['ARV-001'])
story('ARV-003', 'E0', 'Local development environment with Docker Compose',
      'One command brings up the dependencies agents and tests need locally.',
      ['docker-compose.dev.yml starts TimescaleDB (pinned timescale/timescaledb-ha image), Kafka in KRaft mode, Redis and smtp4dev with healthchecks',
       'Credentials come from .env.example copied to an ignored .env; no secret is committed',
       'scripts/dev-up.mjs and scripts/dev-down.mjs wrap compose cross-platform; README and wiki/04-Deployment-Guide.md local section updated',
       'The read-only database role used by the postgres-dev MCP is created by the dev bootstrap'],
      'S', ['CWE-269', 'CWE-287'], ['ARV-001'])
story('ARV-004', 'E0', 'Port the domain foundations from AMAN',
      'Port EntityBase, BaseAuditableEntity, BaseSoftDeletableEntity, IDomain, IHasDomainEvents, EventBase, PagedData, Search<TCriteria> and the Result and Fx.Specification usage (Fluentx) from ../Aman, renamed and trimmed.',
      ['Types live in Ariva.Core/Domain/Common and Ariva.Core/Services with no reference to Aman assemblies',
       'Fluentx and Mapster added through Directory.Packages.props at AMAN versions',
       'Unit tests cover id generation, audit fields, domain event collection and specification validation'],
      'BS', ['CWE-501'], ['ARV-001'])
story('ARV-005', 'E0', 'Port the NHibernate storage provider and unit of work',
      'Port IStorageProvider, IUnitOfWork, SvcDb, SvcBase, the NHibernate provider, convention mapper and mapping rules. NHibernate only.',
      ['SchemaUpdate runs only when Database:AllowSchemaUpdate is true; only appsettings vm-local sets it',
       'ExecuteSqlAsync accepts parameters only (no overload taking an interpolated string)',
       'Testcontainers.PostgreSql added with a version confirmed by the nuget MCP; an integration test round-trips a sample entity with audit fields and a domain event',
       'The ported SvcBase keeps AMAN signatures where practical (ValidateRequestAsync, ExecutePagedQueryAsync)'],
      'BIS', ['CWE-89', 'CWE-269'], ['ARV-003', 'ARV-004'])
story('ARV-006', 'E0', 'Versioned SQL script runner and database roles',
      'Production schema and every TimescaleDB object come from Ariva.Infra/Timescale/Scripts/NNNN_*.sql.',
      ['The runner applies scripts in order, records name and SHA-256 checksum in schema_version, and fails startup when an applied script changed',
       '0001_roles.sql creates a migration role (DDL) and a runtime role (DML only); runtime connection strings use the runtime role',
       'The runner runs as a one-shot job (command-line flag), not in every pod',
       'Integration tests prove ordering, idempotent re-run and tamper detection against a TimescaleDB container'],
      'BIS', ['CWE-89', 'CWE-269'], ['ARV-005'])
story('ARV-007', 'E0', 'Logging and telemetry with secret redaction',
      'Serilog and OpenTelemetry wiring with AMAN parity, plus redaction.',
      ['Structured JSON console logging; Loki and OTLP exporters enabled by configuration',
       'Authorization, Cookie, Set-Cookie, X-TOTP-Code headers and the access_token query parameter are redacted; Microsoft.AspNetCore.Hosting logs at Warning (Microsoft SignalR security guidance)',
       'Unit tests prove redaction of each item; no log line in the E2E run contains a bearer token'],
      'BES', ['CWE-287', 'CWE-384'], ['ARV-001'])
story('ARV-008', 'E0', 'Caching and Data Protection key ring',
      'FusionCache with Redis backplane; Data Protection keys persisted in PostgreSQL and protected at rest.',
      ['FusionCache registered with tags and the Redis backplane (AMAN versions)',
       'Data Protection uses a custom IXmlRepository on PostgreSQL (parameterised SQL) and ProtectKeysWithCertificate with a certificate from a Kubernetes secret; dev generates a local certificate',
       'Integration tests prove two hosts share the key ring and that keys are not readable without the certificate'],
      'BIS', ['CWE-287'], ['ARV-005'])
story('ARV-009', 'E0', 'Permission model and permission matrix',
      'Port AMAN permissions and make authorization data-driven and testable.',
      ['Global.Defaults.Permissions with View, Create, Edit, Search, Delete per entity; Permission attribute and policy provider (no role strings)',
       'Role to permission seed for BorderShiftSupervisor, TerminalDutyManager, HandlerStationManager, SystemAdministrator',
       'security/permission-matrix.json lists every endpoint with the expected status per role; a unit test checks the seed against it and the E2E helper reads it'],
      'BES', ['CWE-862', 'CWE-863'], ['ARV-004'])
story('ARV-010a', 'E0', 'Password login, lockout and token issuance',
      'Local accounts with username and password (ADR-0026). Replace the placeholder deny scheme with real authentication while keeping default deny. Split from ARV-010 after grilling on 2026-10-01.',
      ['User has a unique username (case-insensitive, NFKC-normalised, 3 to 64 characters), optional email, PBKDF2-SHA256 hash with 600,000 iterations, 16-byte salt and the algorithm and iteration count stored for upgrades; passwords are never trimmed or altered',
       'Password policy: 12 to 128 characters, any Unicode, no composition rules, no periodic expiry; rejected when it appears in the bundled blocklist (top breached and common passwords, shipped with the image because sites may have no internet) or contains the username, "ariva" or the site code',
       'POST /api/auth/login returns one generic error for unknown user, wrong password, disabled or locked account; unknown usernames are verified against a dummy hash so timing is equal (CWE-208)',
       'Brute force: per-IP limit of 10 login attempts per minute (429); per-account lock after 10 consecutive failures for 15 minutes, auto-unlocking, with an admin unlock endpoint stubbed for ARV-011 and a security event on every lock',
       'Access tokens: JWT signed with ES256 by Ariva.Api.Main only, header kid, issuer ariva, audience ariva-users, 15-minute lifetime, claims sub, sid, family, auth_time, amr; every host validates issuer, audience, lifetime, algorithm allowlist (ES256 only) and a 30-second skew; keys come from a Kubernetes secret, rotated every 90 days with the previous public key kept for overlap',
       'Admin-created users get a one-time temporary password; until they change it and enrol TOTP (ARV-010c), their token carries scope pending and every endpoint except change-password, TOTP enrolment and logout returns 403',
       'Nothing secret is logged: no argument logging on auth methods; a test drives a login and asserts the password, codes and tokens appear in no log event (CWE-532)',
       'E2E: login success, wrong password, unknown user (same status and body), lockout and auto-unlock, rate limit 429, pending scope blocked, token with alg none or HS256 rejected, token for another audience rejected, token in a query string rejected on API routes'],
      'BES', ['CWE-287', 'CWE-306', 'CWE-307', 'CWE-208', 'CWE-521', 'CWE-532'], ['ARV-007', 'ARV-008', 'ARV-009'])
story('ARV-010b', 'E0', 'Server-side sessions, refresh rotation and revocation',
      'Sessions are server-side so disabling a user or logging out takes effect at once (ADR-0026).',
      ['A UserSession row (sid, user, family id, created, last seen, idle and absolute expiry, ip, user agent, revoked) in PostgreSQL, cached through FusionCache (Redis plus a 5-second memory layer with backplane invalidation); every authenticated request checks that its sid is active, so revocation applies within 5 seconds',
       'Lifetimes: absolute 12 hours for every role; idle 30 minutes for SystemAdministrator and 4 hours for operational roles, extended by refresh; expired sessions return 401 with error session_expired',
       'Refresh tokens: 256-bit random, opaque, stored as SHA-256 hashes, rotated on every use within one family; presenting a rotated token within a 30-second grace window returns the already-issued successor once (two tabs refreshing together), after it revokes the whole family and raises a security event',
       'Refresh cookie __Secure-ariva_rt: HttpOnly, Secure, SameSite=Strict, Path=/api/auth, Max-Age equal to the remaining absolute lifetime; never in a response body; /api/auth/refresh also requires the X-Ariva-Csrf: 1 header and an Origin in the allowlist (CWE-352)',
       'Login always issues a new sid, family and cookie and revokes any refresh cookie presented with the login request (CWE-384); logout revokes the session and family and clears the cookie; disabling a user or changing their roles revokes all their sessions',
       'Web and API share one host: /api and /hubs behind the ingress (ADR-0026); the SignalR hub validates the sid on connect and closes connections of revoked sessions within 5 seconds',
       'E2E: refresh rotation, reuse within grace returns the same successor, reuse after grace revokes the family, refresh without the CSRF header 403, foreign Origin 403, logout then old access token 401 within 5 seconds, disabled user loses access within 5 seconds, sid differs before and after login, idle and absolute expiry with a fake clock'],
      'BIES', ['CWE-384', 'CWE-613', 'CWE-352', 'CWE-287'], ['ARV-010a'])
story('ARV-010c', 'E0', 'TOTP enrolment, verification, recovery codes and break-glass access',
      'Second factor for every human user (all four roles enrol; ADR-0026).',
      ['TOTP per RFC 6238: SHA1, 6 digits, 30-second step, window of one step either side; secret of 160 bits from a CSPRNG, encrypted at rest with Data Protection (purpose Ariva.Totp.v1), shown once as a QR code and text at enrolment',
       'Enrolment activates only after the user submits a valid first code; until then the account stays in pending scope',
       'Replay guard: the last accepted time step is stored on the user row (durable across a Redis loss) and a code for that step or an earlier one is rejected (CWE-294)',
       'Ten recovery codes (10 characters, Crockford base32) generated at enrolment, shown once, stored as SHA-256 hashes, single use; using one raises a security event and prompts re-generation; regeneration needs step-up (ARV-010d)',
       'Failed second-factor attempts count towards the same account lockout as passwords',
       'Break-glass: one account per deployment, created only by the installer command-line job with a printed sealed credential and recovery codes, excluded from the lockout, every login raises a high-severity security event and alert, and it cannot be created or re-enabled through any API',
       'E2E and unit tests: enrolment confirm, wrong code, replayed code, code from the next step accepted, recovery code single use, break-glass login emits the alert event, a TOTP secret is never returned after enrolment'],
      'BES', ['CWE-287', 'CWE-294', 'CWE-308', 'CWE-312'], ['ARV-010b'])
story('ARV-010d', 'E0', 'Step-up MFA for critical actions',
      'Critical actions need a TOTP check within the last 15 minutes (ADR-0026).',
      ['[RequiresRecentMfa(minutes: 15)] attribute and policy read auth_time and amr from the token; failure returns 401 with WWW-Authenticate: Bearer error="insufficient_user_authentication", max_age=900 (RFC 9470) and a ProblemDetails code mfa_required',
       'POST /api/auth/step-up verifies a TOTP or recovery code for the current session and returns a new access token with a fresh auth_time and amr pwd plus otp; it shares the replay guard and lockout',
       'Critical actions, enforced by an architecture test that reads the list: grant or revoke roles, create or reset users and TOTP, create or regenerate integration clients and device credentials, publish or retire a zone profile, create, edit or delete SLA contracts, delete alert rules, change outbound endpoints, regenerate recovery codes',
       'E2E: each critical endpoint returns 401 mfa_required with an old auth_time and succeeds after step-up; a non-critical endpoint never asks'],
      'BES', ['CWE-306', 'CWE-287', 'CWE-269'], ['ARV-010c'])
story('ARV-011', 'E0', 'User, role and audit administration',
      'Administrators manage users and roles safely; every security-relevant action is audited.',
      ['User CRUD with role assignment through SvcRoleAssignment: no self-grant, cannot grant above own role, SystemAdministrator grants need step-up MFA and are audited',
       'TOTP reset flow requires step-up MFA and forces re-enrolment',
       'AuditEntry entity and search API (who, what, when, before and after summaries); audit rows cannot be edited or deleted through any API',
       'E2E: privilege escalation attempts return 403; audit rows appear for every create, grant, reset'],
      'BES', ['CWE-269', 'CWE-863', 'CWE-306'], ['ARV-010d'])
story('ARV-012', 'E0', 'Site scoping',
      'Every site-bound read and write is limited to the caller\'s sites.',
      ['ISiteScope service and [SiteScoped] filter; users and integration clients are bound to site codes',
       'An architecture test fails when a controller action takes a site, airport or terminal id without the scope',
       'E2E IDOR tests: requesting another site\'s ids returns 403 or 404 without data'],
      'BES', ['CWE-863'], ['ARV-009', 'ARV-011'])

# E1
story('ARV-013', 'E1', 'Site topology entities',
      'Airport, Terminal, Level, Checkpoint (CheckIn, Security, Emigration, Immigration), Desk (Counter, Desk, EGate, lane category) per docs/domain/glossary.md.',
      ['Rich entities with invariants (unique codes per parent, valid lane categories, desk kind rules)',
       'Unit tests for every invariant'], 'BS', ['CWE-501'], ['ARV-004'])
story('ARV-014', 'E1', 'Topology persistence and AdminArea APIs',
      'CRUD for airport, terminal, level, checkpoint and desk following the AMAN controller pattern.',
      ['Mappings, services (Result<T>, Fx.Specification, FusionCache tags), controllers with [Permission] and site scope, .http files',
       'Search endpoints use sort and filter allowlists',
       'E2E: CRUD per entity, permission matrix rows, cross-site 403, SQL injection and XSS payloads return 400 or are stored as inert text'],
      'BIES', ['CWE-862', 'CWE-863', 'CWE-89', 'CWE-501'], ['ARV-005', 'ARV-012', 'ARV-013'])
story('ARV-015', 'E1', 'Desk ranges and external desk code mapping',
      'Bulk create desks (for example D01 to D22) and map external codes (AMAN desk codes, AODB counter codes) to Ariva desks.',
      ['Range creation validated (max 200 per request, pattern checked)',
       'DeskCodeMapping entity unique per source system and site; used later by the AMAN feed consumer',
       'Unit and E2E tests'], 'BES', ['CWE-120', 'CWE-863'], ['ARV-014'])
story('ARV-016', 'E1', 'Zone profile aggregate',
      'Versioned zone geometry: Draft, Published, Retired; zones of type SnakeQueue, ServiceArea, OverflowBand, CountLine; entry and exit lines.',
      ['Invariants: at least 3 points, simple polygon (no self-intersection), entry and exit lines on the polygon edge, unique names, coordinates inside the level bounds',
       'Published versions are immutable; publishing produces a geometry hash stable across runs',
       'Table-driven unit tests for every invariant and the hash'], 'BS', ['CWE-501'], ['ARV-013'])
story('ARV-017', 'E1', 'Zone profile API with publish workflow',
      'Create a draft from the active version, edit zones, validate, publish.',
      ['Publish requires step-up MFA and raises ZoneProfilePublished through the outbox',
       'History endpoint lists versions with who and when',
       'E2E: full workflow, editing a published version fails, publish without recent MFA returns 403'],
      'BES', ['CWE-306', 'CWE-862', 'CWE-863'], ['ARV-014', 'ARV-016', 'ARV-010d', 'ARV-020'])
story('ARV-018', 'E1', 'Floor plan assets',
      'Upload a floor plan per level with scale and origin.',
      ['SVG uploads are sanitised (scripts, event attributes, foreignObject and external references removed) or rasterised; PNG and JPEG accepted; 20 MB limit; content type sniffed',
       'Stored through a storage abstraction (local disk in dev, S3-compatible later) with generated file names only (no user-supplied paths)',
       'E2E: SVG with script is neutralised, oversized upload 413, path traversal names rejected'],
      'BES', ['CWE-79', 'CWE-120', 'CWE-501'], ['ARV-014'])
story('ARV-019', 'E1', 'Seed the DMO demo topology',
      'The fictional Demo International Airport from the prototype (48 counters in islands A to D, security North and South, 22 plus 22 desks, 4 plus 6 e-gates) and its zone profile v12.',
      ['Idempotent seed job usable in dev and demo, disabled in production',
       'Integration test proves re-running the seed changes nothing'], 'BIS', ['CWE-269'], ['ARV-017'])

# E2
story('ARV-020', 'E2', 'Messaging over Kafka with outbox and dead letters',
      'ISvcMessageBus implemented on MassTransit 8.5.11 with the Kafka Rider (ADR-0018), with Ariva-owned outbox, inbox and dead-letter filters that AMAN lacks.',
      ['KafkaTopics constants and startup provisioning (partitions, replication, retention); keyed producers AddProducer<string,T> with acks all, idempotence, 1 MB limit; one consumer group per service and endpoint',
       'NHibernate outbox_message table and single-leader relay (advisory lock, FOR UPDATE SKIP LOCKED, in-order per key); InboxFilter on processed_event in the consumer transaction; DeadLetterFilter outermost producing to <topic>.dlq.v1 with topic, partition, offset and error',
       'Endpoint defaults: AutoOffsetReset earliest, ConcurrentDeliveryLimit 1, CheckpointInterval 5 s, CheckpointMessageCount 500; AddSource and AddMeter MassTransit; bus health check in readiness',
       'Partition-aware raw Confluent consumer abstraction for Ariva.Api.Stream (assigned and revoked callbacks, StoreOffset after persist)',
       'Architecture test: no MassTransit or Confluent types referenced from Ariva.Core',
       'Testcontainers.Kafka integration tests: at-least-once with a crash between write and checkpoint, duplicate suppression, poison message reaches the DLQ, filter order (dead letter outside retry) proven'],
      'BIS', ['CWE-77', 'CWE-120', 'CWE-501'], ['ARV-003', 'ARV-005'])
story('ARV-021', 'E2', 'Devices, calibrations and the canonical sensing model',
      'Canonical events (LineCrossing, ZoneOccupancy, TrackPosition, IntervalCount, DeviceStatus), Device and Calibration entities, registry API.',
      ['Device credentials generated once and stored hashed; coverage footprint from mounting height (BOQ assumed table, labelled)',
       'Lifecycle Commissioning to Online only after a passed calibration (default 95 percent counting accuracy)',
       'Unit and E2E tests including permission matrix rows'],
      'BES', ['CWE-287', 'CWE-501', 'CWE-863'], ['ARV-014', 'ARV-020'])
story('ARV-022', 'E2', 'Device authentication scheme',
      '[DeviceAuthenticated] for sensor pushes and gateways.',
      ['Device key header (hash compared in constant time) with optional client certificate; IP allowlist per device; rate limit per device',
       'Audience and scheme separate from users and integration clients',
       'E2E: missing key 401, wrong key 401, other device\'s zone 403, user token on device endpoint 401'],
      'BES', ['CWE-287', 'CWE-306', 'CWE-863'], ['ARV-010a', 'ARV-021'])
story('ARV-023', 'E2', 'HTTPS push ingest with the Xovis dialect',
      'First transport and first coded dialect.',
      ['Ingest endpoints for Xovis push and canonical push; 256 KB limit; schema validation; max events per message',
       'Clock offset estimated per device (EWMA); events outside tolerance flagged',
       'Events produced to ariva.sensing.events.v1 keyed by zone id',
       'Conformance tests from recorded or vendor sample payloads plus malformed, oversized and skewed cases; vendor details verified and sources recorded'],
      'BES', ['CWE-501', 'CWE-120'], ['ARV-020', 'ARV-022'])
story('ARV-024', 'E2', 'Declarative mapper and MQTT transport',
      'Long-tail device support without code.',
      ['Restricted JSONPath subset (no expressions, no scripts) with tests proving hostile paths are rejected',
       'MQTT transport (TLS, per-device credentials, topic ACL) with MQTTnet at a version confirmed by the nuget MCP',
       'A generic LiDAR perception dialect defined declaratively from a documented sample, with conformance tests'],
      'BIS', ['CWE-94', 'CWE-501'], ['ARV-023'])
story('ARV-025', 'E2', 'Device heartbeat and degraded zones',
      'Offline detection and zone degradation.',
      ['Heartbeat timeout marks a device Offline and its zones Degraded; recovery restores them; events published',
       'Device health API and metrics', 'Unit and E2E tests'], 'BES', ['CWE-862'], ['ARV-023'])
story('ARV-026', 'E2', 'Raw sensing event archive',
      'Archive canonical events for replay.',
      ['0002 script creates the sensing_events hypertable with retention and compression policies (values marked to confirm)',
       'Binary COPY writer with batching; replay reader by time range and zone',
       'Integration tests for write, read and retention'], 'BIS', ['CWE-89'], ['ARV-006', 'ARV-023'])

# E3
story('ARV-027', 'E3', 'Port the reference scenario engine',
      'Port docs/design/prototype/app/assets/sim.js (seeded day model) to C# in Ariva.Simulation.Api.',
      ['Same seed gives identical outputs; default seed 9303',
       'Unit test reproduces the scripted events: arrivals Visitors nowcast passes 15 minutes at 18:05; S-17 offline 18:20 to 18:30; Handler B breach from 19:10 for three bins',
       'The simulator refuses to start in k8s-prd (already enforced) and its control endpoints require authorization'],
      'BS', ['CWE-306'], ['ARV-004'])
story('ARV-028', 'E3', 'Sensor emulator',
      'Emit the scenario as device traffic.',
      ['Per-passenger crossings and zone occupancy in the Xovis and generic dialects over HTTPS to Ingest, authenticated as registered devices',
       'Controls: start, pause, speed, jump, seed; all authorised',
       'E2E: a 10-minute accelerated run produces the expected event counts in Kafka'],
      'BES', ['CWE-287'], ['ARV-027', 'ARV-023'])
story('ARV-029', 'E3', 'AODB, AMAN and immigration emulators',
      'Integration partners for tests and demos.',
      ['AIDX push and ACRIS pull endpoints for the scenario schedule',
       'AMAN feed on Kafka and REST, with its own TOTP client exercising Ariva\'s Integration API, and a mock AMAN auth endpoint for Ariva\'s outbound TotpClientCredentials handler',
       'A mock immigration system that uses the generic REST endpoints'],
      'BS', ['CWE-287'], ['ARV-027', 'ARV-020'])

# E4
story('ARV-030', 'E4', 'Pure queue state engine',
      'Ariva.Core engine: entries and exits from crossings, occupancy, FIFO wait per track (T3) and cumulative-curve wait (T1), censoring, watermark for late and out-of-order events.',
      ['No I/O, time passed in; bounded state per zone',
       'Table-driven tests from docs/domain/formulas.md including missing exits, late events and clock skew'],
      'BS', ['CWE-120'], ['ARV-016', 'ARV-021'])
story('ARV-031', 'E4', 'Realised wait attribution and bins',
      'Attribution to the entry interval, provisional to final, minute aggregates and arrival-weighted P90 per 15-minute bin.',
      ['Rules exactly as formulas.md; finals change only through a recorded recomputation',
       'Tests reproduce the documented numeric cases'], 'BS', [], ['ARV-030'])
story('ARV-032', 'E4', 'Nowcast and throughput',
      'Nowcast (Q + 1) / throughput with the documented estimator.',
      ['Zero throughput returns no service with a reason; degraded zones return bands',
       'Tests from formulas.md'], 'BS', [], ['ARV-030'])
story('ARV-033', 'E4', 'Desk state engine',
      'Desk state from integration, sensor and approach signals with precedence and staleness.',
      ['States Closed, Idle, Serving, Paused, Unknown; per-desk minute aggregates only (no identities)',
       'Tests for precedence and staleness'], 'BS', [], ['ARV-030'])
story('ARV-034', 'E4', 'Stream host',
      'Ariva.Api.Stream workers run the engines on Kafka input and persist outputs.',
      ['Consumer group, per-zone state snapshot and restore on rebalance, StoreOffset after COPY commit',
       '0003 script creates queue_minute, desk_minute, egate_minute and the 15-minute continuous aggregate',
       'Integration tests: restart mid-stream yields identical minute rows (idempotent upserts)'],
      'BIS', ['CWE-120', 'CWE-501'], ['ARV-031', 'ARV-032', 'ARV-033', 'ARV-026', 'ARV-020'])
story('ARV-035', 'E4', 'Live snapshots and the SignalR hub',
      'Push live state to the web.',
      ['Snapshots per zone in Redis; LiveHub in Ariva.Api.Main with [Authorize], group joins authorised per permission and site, MessagePack, Redis backplane',
       'access_token query accepted only on /hubs and redacted from logs',
       'E2E: unauthorised join rejected, cross-site join rejected, authorised client receives updates'],
      'BES', ['CWE-862', 'CWE-863', 'CWE-384'], ['ARV-034', 'ARV-010b', 'ARV-012'])
story('ARV-036', 'E4', 'Golden replay',
      'Deterministic replay for evidence and regression.',
      ['Replay command over archived events for a range and profile version produces a stable output hash',
       'CI test: seed 9303 reproduces 18:05, 18:20 to 18:30 and 19:10 exactly'],
      'BIS', [], ['ARV-034', 'ARV-028'])

# E5
story('ARV-037', 'E5', 'Alert rules',
      'Typed rules (metric, comparator, threshold, sustain, severity, owner role, escalation, channels); no expression strings.',
      ['Seeded rules R-001 to R-005 reproduce the prototype behaviour', 'CRUD with permissions and site scope', 'Unit and E2E tests including hostile strings in names'],
      'BES', ['CWE-94', 'CWE-862', 'CWE-863'], ['ARV-014', 'ARV-009'])
story('ARV-038', 'E5', 'Rule evaluation and backtest',
      'Evaluate rules every minute; backtest against stored minutes.',
      ['Sustain windows, dedupe, auto-resolve', 'Backtest returns first fire time and count; a test proves backtest equals live firing for seed 9303 (R-001 at 18:05)'],
      'BIS', [], ['ARV-037', 'ARV-034'])
story('ARV-039', 'E5', 'Alert lifecycle',
      'Acknowledge, escalate, resolve; live push; audit.', ['Escalation after N minutes to the configured role', 'E2E for each transition and its authorization'],
      'BES', ['CWE-862', 'CWE-863'], ['ARV-038', 'ARV-035'])
story('ARV-040', 'E5', 'Email notifications',
      'MailKit channel with templates.', ['Templates from Ariva.Resources; recipients validated; header injection impossible (CRLF stripped); rate limited', 'smtp4dev used in dev and E2E'],
      'BES', ['CWE-501'], ['ARV-039'])

# E6
story('ARV-041', 'E6', 'Flights and allocations model',
      'FlightLeg, FlightEvent, CounterAllocation and feed freshness.', ['Stale feed detection raises an alert condition', 'Unit tests'], 'BS', ['CWE-501'], ['ARV-014'])
story('ARV-042', 'E6', 'Integration clients and TOTP authentication',
      'docs/architecture/integration.md inbound authentication, AMAN-compatible and hardened.',
      ['IntegrationClient with hashed secret, encrypted TOTP seed, scopes, site codes, CIDRs; created with step-up MFA; secret and provisioning URI shown once',
       'POST /api/v1/auth with replay guard, generic invalid_client, rate limits and lockout; integration signing key and audience separate',
       '[IntegrationScope] with site binding; per-request X-TOTP-Code policy; audit of every call',
       'E2E: success, wrong secret, wrong code, replayed code, lockout, wrong scope, other site, integration token on a user endpoint'],
      'BES', ['CWE-287', 'CWE-306', 'CWE-863', 'CWE-269'], ['ARV-010a', 'ARV-008', 'ARV-012'])
story('ARV-043', 'E6', 'Integration API: flights and allocations',
      'Generic endpoints for any AODB.', ['Batch endpoints with Idempotency-Key, unknown-field rejection, 500 items and 1 MB limits, per-item results', 'E2E including duplicates, oversized, malformed, injection payloads'],
      'BES', ['CWE-501', 'CWE-120', 'CWE-89'], ['ARV-042', 'ARV-041'])
story('ARV-044', 'E6', 'AIDX 22.1 inbound',
      'Hardened XML adapter.', ['DtdProcessing.Prohibit, XmlResolver null, schema validation, 5 MB limit', 'Maps to FlightLeg and FlightEvent; E2E with the emulator and an XXE payload that must be rejected'],
      'BES', ['CWE-501', 'CWE-120'], ['ARV-043', 'ARV-029'])
story('ARV-045', 'E6', 'Outbound endpoints and the ACRIS connector',
      'Registry and auth handlers with SSRF controls.',
      ['OutboundEndpoint registry (step-up MFA), handlers TotpClientCredentials, OAuth2ClientCredentials, ApiKeyHeader, HmacSignature, MutualTls',
       'Named clients, resolved-IP CIDR check, no redirects, resilience policies',
       'ACRIS pull against the emulator; tests for metadata IP, loopback, DNS rebinding and redirect refusal'],
      'BES', ['CWE-918', 'CWE-306'], ['ARV-042', 'ARV-029'])
story('ARV-046', 'E6', 'SSIM import',
      'Schedule fallback.', ['Streaming parse with line and size limits; preview before commit; E2E with malformed and oversized files'], 'BES', ['CWE-120', 'CWE-501'], ['ARV-041'])
story('ARV-047', 'E6', 'Arrival-wave projection',
      'Flights landing in the next 30 minutes with lane split and predicted hall arrival curve (8 to 15 minutes after on-block over about 12 minutes).',
      ['Uses AMAN lane demand when present, the default mix otherwise; API with permissions', 'Tests from formulas.md'], 'BES', ['CWE-863'], ['ARV-041', 'ARV-048'])

# E7
story('ARV-048', 'E7', 'AMAN feed and immigration endpoints',
      'Consume the four V1 contracts from Kafka and REST.',
      ['Validation: 60-second intervals, consistent sums, small-cell suppression respected, unknown fields rejected', 'Desk code mapping applied; idempotent by SourceEventId', 'E2E with the emulator over both transports'],
      'BIES', ['CWE-501', 'CWE-863'], ['ARV-042', 'ARV-020', 'ARV-015', 'ARV-029'])
story('ARV-049', 'E7', 'Desk sessions and e-gate coupling',
      'Feed AMAN signals into the engines.', ['Desk sessions at their precedence rank; e-gate rejects added to manual lane demand', 'Unit and integration tests'], 'BIS', [], ['ARV-048', 'ARV-033'])
story('ARV-050', 'E7', 'Outbound AMAN connector',
      'Pull from AMAN\'s Integration API when Kafka is not shared, using TotpClientCredentials.', ['Tested against the mock AMAN; token cached and refreshed; X-TOTP-Code per request'], 'BES', ['CWE-287', 'CWE-918'], ['ARV-045', 'ARV-048'])

# E8
story('ARV-051', 'E8', 'Web shell, login and design tokens',
      'Login on the shell that already follows Aman.Web\'s design system (tokens, sidebar, header, light and dark modes, RTL; done 2026-10-01). Remaining: authentication and the (modules) and (public) route groups.',
      ['Login with username, password and TOTP; access token in memory only; one tab refreshes for all through the Web Locks API and shares the new token with BroadcastChannel; logout', 'First-login flow: change the temporary password, enrol TOTP with a QR code, confirm the first code, show the recovery codes once with a copy action', 'Step-up dialog opens on 401 mfa_required and retries the action once; session_expired returns to login with the page remembered', 'Authenticated routes under (modules), login under (public); sidebar items filtered by permission (server stays the authority); user card and logout in the sidebar footer',
       'Playwright: login, RTL, no console errors, no CSP violations, XSS probes on the login form'],
      'WE', ['CWE-384', 'CWE-79', 'CWE-287'], ['ARV-010d'])
story('ARV-052', 'E8', 'Topology admin screens', 'Airport to desk administration.', ['Per-role visibility', 'Playwright per screen and role, XSS probes on names'], 'WE', ['CWE-79', 'CWE-863'], ['ARV-051', 'ARV-014'])
story('ARV-053', 'E8', 'Zones screen and editor', 'Floor plan, profiles, draft editor, publish.', ['Drag vertices and lines with a keyboard vertex table alternative; validation messages; publish with step-up MFA', 'Playwright for editing, publishing and role hiding'], 'WE', ['CWE-79', 'CWE-306'], ['ARV-051', 'ARV-017', 'ARV-018'])
story('ARV-054', 'E8', 'Devices screen', 'Registry, coverage on plan, register sensor, record calibration, health.', ['Device secret shown once with copy; never re-displayed', 'Playwright per role'], 'WE', ['CWE-79', 'CWE-287'], ['ARV-051', 'ARV-021', 'ARV-025'])
story('ARV-055', 'E8', 'Live operations screen', 'KPI tiles, floor plan by nowcast, desk states, wait chart, alerts, arrival-wave strip.', ['Live updates through SignalR; stale indicator', 'Playwright with the simulator: the 18:05 alert appears and can be acknowledged'], 'WE', ['CWE-79', 'CWE-863'], ['ARV-051', 'ARV-035', 'ARV-039', 'ARV-047'])
story('ARV-056', 'E8', 'Alert rules screen', 'Rules list, create with backtest preview.', ['Playwright: create a rule, preview first fire time, rule fires live at the same time'], 'WE', ['CWE-79', 'CWE-94'], ['ARV-051', 'ARV-038'])
story('ARV-057', 'E8', 'Immigration screen', 'Border module view.', ['Lane waits, desk grid aggregates for border roles only, e-gate utilisation and rejects', 'Playwright: Terminal duty manager sees aggregates only; no identifiers anywhere'], 'WE', ['CWE-863', 'CWE-79'], ['ARV-051', 'ARV-049'])
story('ARV-058', 'E8', 'Passenger display', 'Display configuration and the kiosk route.', ['Bilingual bands with hysteresis and stale fallback; display players authenticate with device credentials', 'Playwright at 1920x1080 including stale data'], 'WE', ['CWE-79', 'CWE-306'], ['ARV-051', 'ARV-035'])
story('ARV-059', 'E8', 'Users and access screen', 'Users, roles, TOTP reset, audit log.', ['Playwright: escalation attempts blocked in the UI and by the API'], 'WE', ['CWE-269', 'CWE-79'], ['ARV-051', 'ARV-011'])

# E9
story('ARV-060', 'E9', 'Daily report and scheduled delivery',
      'Peaks per lane, P50 and P90 per hour, alerts, device uptime; CSV export; scheduled email.',
      ['CSV neutralises formula prefixes (=, +, -, @)', 'TickerQ in Ariva.Api.Cronz with its dashboard behind authorization', 'Integration and E2E tests'],
      'BIES', ['CWE-79', 'CWE-306'], ['ARV-034', 'ARV-040'])
story('ARV-061', 'E9', 'Reports screen', 'Report preview and export.', ['Playwright per role'], 'WE', ['CWE-79'], ['ARV-060', 'ARV-051'])

# E10
story('ARV-062', 'E10', 'Complete Helm deployment',
      'All hosts, TimescaleDB, topic and migration jobs, environments.', ['Chart tests pass; helmfile environments; pipelines updated; wiki deployment guide matches'], 'S', ['CWE-269'], ['ARV-002', 'ARV-006', 'ARV-020'])
story('ARV-063', 'E10', 'Dynamic security scanning pipeline',
      'OWASP ZAP baseline and OpenAPI scans against the dev deployment.', ['Security-zap.yaml fails on high findings and publishes the report', 'Findings triaged in backlog/progress.md'], 'S', ['all'], ['ARV-062'])
story('ARV-064', 'E10', 'Scripted demo and runbook',
      'A repeatable demo on the dev cluster.', ['Simulator starts at 17:40 seed 9303; the three events show on the live screens', 'A Playwright demo test walks the three events', 'wiki runbook page updated'], 'E', [], ['ARV-055', 'ARV-057', 'ARV-058', 'ARV-036', 'ARV-062'])
story('ARV-065', 'E10', 'Phase 0 exit review',
      'Gate before the pilot.', ['All gates green; full-repository security review PASS; coverage report attached', 'List of open "to confirm" items for the pilot contract'], 'BIWES', ['all'], ['ARV-064', 'ARV-063'])

# ---------------------------------------------------------------------------------------------
# E11: engineering quality (suggested 2026-10-01; each story is independent and can be reordered)
story('ARV-066', 'E11', '.NET Aspire AppHost for local runs, with the Aspire MCP for agents',
      'One F5 starts every host, TimescaleDB, Kafka, Redis and the simulator with the dashboard, traces and logs; agents read resources, logs and traces through the Aspire MCP instead of guessing.',
      ['Ariva.AppHost (Platform/Cloud or Platform/Backplane, mirroring the AMAN layout) wires the five APIs, the simulator, the web dev server and the containers from ARV-003 with health checks and references',
       'Ariva.ServiceDefaults adds OpenTelemetry, health checks and resilience defaults without changing the security baseline (default deny, headers, limits)',
       'The aspire MCP server is added to .mcp.json and documented in docs/harness/README.md; Playwright and integration tests can target the AppHost',
       'Docker Compose stays for CI and for machines without the Aspire workload'],
      'BS', ['CWE-269'], ['ARV-003', 'ARV-007'])
story('ARV-067', 'E11', 'AsyncAPI 3 document for every Kafka topic, checked in CI',
      'docs/architecture/asyncapi.yaml describes every topic in KafkaTopics (key, schema, producer, consumers, retention class) and the AMAN feed; CI fails when a topic constant has no entry or a contract record and its schema drift apart.',
      ['AsyncAPI 3.0 document generated or validated from KafkaTopics and the event records (unit test compares them)',
       'The AMAN feed topics and the four V1 contracts are described with the aggregate-only rule and small-cell suppression',
       'The CLI validator runs in ci.yml; the document renders in the wiki integration guide'],
      'BS', ['CWE-501'], ['ARV-020'])
story('ARV-068', 'E11', 'Consumer-driven contract tests for the AMAN feed (Pact)',
      'Ariva is the consumer of aman.feed.*; a Pact contract per V1 message lets AMAN verify it never breaks Ariva and never adds identifiers.',
      ['PactNet message pacts for DeskSessionChanged, DeskIntervalStats, EGateIntervalStats and InboundFlightLaneDemand, published as CI artifacts',
       'A provider verification harness that AMAN can run in its pipeline is documented in wiki/08-Integration-Guide.md',
       'The pacts assert absence of officer, traveller and document identifiers (data boundary)'],
      'BIS', ['CWE-501'], ['ARV-048'])
story('ARV-069', 'E11', 'Mutation testing on the pure engines (Stryker.NET)',
      'Agents write many tests; mutation testing proves they catch real faults. Applies to the formulas, the queue state engine, realised wait attribution and the nowcast.',
      ['Stryker.NET configured for Ariva.Core formula and engine namespaces with a mutation score threshold of 80 percent (break below 70)',
       'Runs weekly and on demand (workflow_dispatch), report uploaded as an artifact; surviving mutants triaged in backlog/progress.md'],
      'B', [], ['ARV-030', 'ARV-031', 'ARV-032'])
story('ARV-070', 'E11', 'Property-based tests for formulas and vendor parsers',
      'Generated inputs for the queue formulas (F1 to F21) and every vendor dialect parser: invariants such as non-negative waits, conservation of passengers across zones, and parsers that never throw or over-read on arbitrary bytes (CWE-120).',
      ['CsCheck (or FsCheck) properties for each formula in docs/domain/formulas.md with shrunk counterexamples recorded as regression tests',
       'Every parser in Ariva.Infra adapters gets a property test with random and truncated payloads; no exception escapes the adapter boundary'],
      'BS', ['CWE-120', 'CWE-501'], ['ARV-023', 'ARV-030'])
story('ARV-071', 'E11', 'Load tests for ingest and live fan-out (NBomber)',
      'Sizing evidence for the BOQ: sensor events per second through Ingest and Stream, and SignalR fan-out to dashboards and displays.',
      ['NBomber scenarios: 40 sensors at vendor push rates, 3x peak burst, 200 concurrent dashboard and display connections',
       'Results (p50, p95, p99 latency, throughput, CPU and memory per pod) recorded in wiki/05-Network-and-Ports.md sizing notes, labelled as lab measurements',
       'Rate limits and body limits hold under load (429 and 413 rather than failures)'],
      'BES', ['CWE-120', 'CWE-400'], ['ARV-034', 'ARV-035'])
story('ARV-072', 'E11', 'Failure-injection tests with Toxiproxy',
      'Prove degraded modes: Kafka, Redis and PostgreSQL latency, resets and outages while the simulator runs.',
      ['Testcontainers.Toxiproxy scenarios: broker outage (outbox holds, relay resumes in order), Redis loss (live view marked stale, no crash), database stall (ingest backpressure, no data loss)',
       'Each scenario asserts the data-quality flag and the stale indicators the UI shows'],
      'BIS', ['CWE-400'], ['ARV-020', 'ARV-034', 'ARV-035'])
story('ARV-073', 'E11', 'Signed images with SBOM and provenance',
      'Customers in border security ask for supply-chain evidence; sign every image and attach its SBOM.',
      ['images.yml signs each image with cosign keyless (GitHub OIDC) and attaches the CycloneDX SBOM as an attestation',
       'The Helm deployment guide shows how to verify signatures (cosign verify) and, optionally, enforce them with an admission policy',
       'Release notes record image digests'],
      'S', ['CWE-494'], ['ARV-062'])
story('ARV-074', 'E11', 'OWASP ASVS 5.0 Level 2 mapping',
      'The 14 CWEs are the floor; tenders increasingly cite ASVS. Map every ASVS 5.0 Level 2 requirement to a control, a test or a gap.',
      ['docs/security/asvs-l2.md lists each requirement with status (met, partly, gap, not applicable) and evidence links',
       'Gaps become stories; the security-reviewer agent checks the mapping for the areas a story touches'],
      'S', ['all'], ['ARV-010d', 'ARV-011'])
story('ARV-075', 'E11', 'Visual regression baselines for the key screens',
      'Catch unintended layout and token changes (Aman design system parity) on live operations, immigration, displays and the shell in both languages and modes.',
      ['Playwright toHaveScreenshot baselines generated in the CI Linux image only (fonts pinned), with a masked area for live values',
       'An update flow (workflow_dispatch) regenerates baselines on request; diffs uploaded as artifacts'],
      'WE', ['CWE-79'], ['ARV-055', 'ARV-057', 'ARV-058'])
story('ARV-076', 'E11', 'Dev container for cloud agent sessions and Codespaces',
      'A .devcontainer with the .NET 10 SDK, Node 22, Playwright Chromium, uv and the Compose services, so Claude Code cloud sessions and Codespaces start with every gate runnable.',
      ['devcontainer.json and a Dockerfile pinned by digest; postCreate runs npm ci and dotnet restore',
       'node scripts/verify.mjs all passes inside the container; documented in docs/harness/README.md'],
      'BWES', ['CWE-269'], ['ARV-001', 'ARV-003'])

prd_path = os.path.join(HERE, 'prd-phase0.json')
prev = {}
if os.path.exists(prd_path):
    try:
        for s in json.load(open(prd_path, encoding='utf-8'))['userStories']:
            prev[s['id']] = s
    except Exception:
        pass

stories = []
for i, s in enumerate(S, start=1):
    crit = list(s['ac']) + [GATES[g] for g in s['gates']] + ALWAYS
    cwe = ', '.join(s['cwe']) if s['cwe'] else 'none specific beyond the baseline'
    p = prev.get(s['id'], {})
    stories.append({
        'id': s['id'],
        'title': f"[{s['epic']}] {s['title']}",
        'description': f"{s['description']} Epic: {E[s['epic']]}. CWEs to review: {cwe}.",
        'acceptanceCriteria': crit,
        'priority': i,
        'passes': p.get('passes', False),
        'notes': p.get('notes', ''),
        'dependsOn': s['deps'],
    })

ids = {s['id'] for s in stories}
for s in stories:
    for d in s['dependsOn']:
        assert d in ids, (s['id'], d)
        assert d != s['id'], (s['id'], d)

prd = {
    'name': 'Ariva Phase 0: demo core',
    'branchName': 'ralph/phase0',
    'description': 'Phase 0 (Oct 2026 to Apr 2027): an end-to-end demo on simulated sensors, AODB and AMAN feeds, secure by default, deployable to the dev cluster. Gate: pilot contract signed.',
    'userStories': stories,
}
with open(prd_path, 'w', encoding='utf-8') as f:
    json.dump(prd, f, indent=2, ensure_ascii=False)
    f.write('\n')

md = ['# Phase 0 PRD: demo core', '',
      'Generated from `backlog/generate.py`; edit the generator, not this file. The JSON tracker for ralph-tui is `backlog/prd-phase0.json`.', '',
      prd['description'], '', '## Quality gates appended to every story', '']
md += [f'- {g}' for g in ALWAYS]
md += ['- Plus the scope gates named per story: ' + '; '.join(f'{k} = {v}' for k, v in GATES.items()), '']
for code, title in E.items():
    md += [f'## {code}: {title}', '']
    for s in S:
        if s['epic'] != code:
            continue
        md += [f"### {s['id']}: {s['title']}", '', s['description'], '',
               f"Depends on: {', '.join(s['deps']) or 'nothing'}. CWEs: {', '.join(s['cwe']) or 'baseline'}. Gates: {', '.join(s['gates'])}.", '']
        md += [f'- {a}' for a in s['ac']]
        md += ['']
with open(os.path.join(HERE, 'PRD-phase0.md'), 'w', encoding='utf-8') as f:
    f.write('\n'.join(md))
print(f'{len(stories)} stories written')
