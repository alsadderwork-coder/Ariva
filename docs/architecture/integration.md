# Integration with AODBs and immigration systems

Ariva integrates with any airport operational database (AODB) and any immigration system through one versioned Integration API, authenticated the same way AMAN authenticates its integration clients (client id, client secret and TOTP), hardened as described in `docs/security/cwe-controls.md`. AMAN is the reference immigration integration; SITA, Amadeus and other AODBs are reached through standards adapters (AIDX, ACRIS, SSIM) or through the same generic API.

## Directions and transports

| Direction | Transport | Used by | Host |
|---|---|---|---|
| Inbound push, generic | HTTPS JSON, Integration API v1 | Any AODB or immigration system that can call a REST API | `Ariva.Api.Integration` |
| Inbound push, standards | HTTPS AIDX 22.1 XML on `/api/v1/aodb/aidx` | AODBs that emit AIDX | `Ariva.Api.Integration` |
| Inbound stream | Kafka topics `aman.feed.*.v1` | AMAN when Ariva and AMAN share a cluster or a bridged cluster | `Ariva.Api.Integration` |
| Outbound pull | HTTPS to a registered `OutboundEndpoint` | ACRIS flight APIs, AMAN Integration API, vendor AODB REST APIs | `Ariva.Api.Integration` (scheduled by `Ariva.Api.Cronz`) |
| File import | SSIM file upload (schedule fallback) | Any airport, when no live feed exists | `Ariva.Api.Main` |

Every inbound route normalises into the same canonical commands, so the rest of Ariva never knows which system the data came from.

## Inbound authentication (AMAN-compatible, hardened)

### Client registration (administrator, step-up MFA required)

1. An administrator creates an `IntegrationClient`: name, kind (`Aodb`, `Immigration`, `SensorGateway`, `Other`), scopes, bound site codes, allowed source CIDRs, per-request TOTP policy, status.
2. Ariva generates a `ClientId` (public), a `ClientSecret` (256-bit, shown once, stored as a PBKDF2-SHA256 hash) and a TOTP seed (160-bit, Base32, stored encrypted with the Data Protection key ring). The `otpauth://` provisioning URI and QR code are shown once.
3. The client's operator loads the seed into their system (any RFC 6238 library: Otp.NET in .NET, pyotp, otplib, Java's `aerogear-otp`).

### Token exchange

`POST /api/v1/auth` with body `{ "clientId": "...", "clientSecret": "...", "totpCode": "123456" }` (same field names as AMAN).

Checks, in order, all failing with the same `401 { "error": "invalid_client" }`:

1. Client exists, is active, and the caller's IP is inside an allowed CIDR.
2. Secret hash matches (constant-time).
3. TOTP code valid: SHA1, 6 digits, 30-second step, window of one step each side.
4. The matched time step is newer than the last accepted step for this client (replay guard in Redis, with a database fallback).
5. Rate limits: 5 attempts per minute per client, 20 per minute per IP; 10 consecutive failures lock the client for 15 minutes and raise an administrator alert.

On success: a JWT access token, audience `ariva-integration`, signed with the integration key ring (separate from user and device keys), lifetime 15 minutes, claims `sub` (client id), `sid` (new session id), `scope`, `site`. No refresh token: the client re-authenticates with a fresh TOTP code. Response: `{ "accessToken", "expiresAt", "sessionId" }` (AMAN shape).

### Calling the API

- `Authorization: Bearer <token>` on every call.
- `X-TOTP-Code: <current code>` on every call when the client's per-request TOTP policy is on (AMAN parity; on by default for immigration clients).
- `Idempotency-Key: <uuid>` on every write; Ariva stores keys for 24 hours and returns the original result for a repeat.
- Body limit 1 MB; batches up to 500 items.
- Authorization: endpoint scope (`[IntegrationScope("flights:write")]`) and site binding (a client bound to `DMO` cannot write `BEY`).
- Every call is audited: client, scope, endpoint, site, result, payload SHA-256.

### Implementation (ARV-042)

- `IntegrationClient` (script 0025): client id `ic_` and 26 base32 characters; secret `ics_` and 256 random bits in base64url, PBKDF2-SHA256 with 600,000 iterations; TOTP seed protected with the Data Protection purpose `Ariva.Totp.v1.IntegrationClient` (separate from the users' seeds); scopes, 1 to 32 bound sites, 1 to 16 CIDR blocks (required; no `/0`, no host bits, no IPv4-mapped IPv6 blocks), the per-request policy (on by default for immigration clients), status, the replay guard step, the failure count and lockout, and the token version. Clients are disabled, never deleted. Administration is in Ariva.Api.Main (`api/v1/admin/integration-clients`, every change a critical action); the exchange and the integration scheme are in Ariva.Api.Integration only, which holds the integration key ring.
- Exchange order: a malformed client id is refused at once (no client can have it); otherwise the secret is always hashed (a dummy hash for an unknown id), so an unknown and a known client take the same time. An address outside the client's networks is refused before anything is counted. The per-client attempt limit is counted in the database (atomic, across replicas); an attempt over it is refused without counting toward the lockout. Then disabled, locked, the secret and the TOTP code (with the replay guard); a wrong secret or code counts toward the lockout (10), and the count starts again at the lock. The accepting update is conditional on the step, the token version, the status and the lock, so a change made in the meantime wins. An administrator's change locks the client's row first, so concurrent changes run in turn and each moves the token version on. Every failure answers the same 401 `invalid_client`.
- A token carries the client's token version (`ver`), moved by every change of scopes, sites, networks, policy, secret, seed or status. Each call re-checks the client's record: known, active, the same token version, the caller inside the networks, and `X-TOTP-Code` within the window (no replay guard on data calls) when the policy is on. A lock is not checked per call: it stops new exchanges only, so an attacker on the client's network who fails on purpose cannot cut a feed that is running.
- `[IntegrationScope("...")]` endpoints accept integration tokens only; the scope handler reads the client's scopes and sites from the database (not from the token) and needs the route value `siteCode` to be one of them (403; an action without `{siteCode}` in its route is refused). Such an action may name a site only through that route token (an architecture test reports any other site reference). Every call to them is recorded in `integration_call` (client, session, scope, route, site, status, payload SHA-256 and size, address) after authentication, refused ones included (a token refused at the per-call check is recorded under its client with 401); the body is hashed only for authenticated calls and within the endpoint's size limit; the runtime role cannot change or delete the record.

### Scopes

| Scope | Allows |
|---|---|
| `flights:write` | Flight schedule and flight status events |
| `allocations:write` | Check-in counter and stand allocations |
| `immigration:write` | Desk sessions, desk interval statistics, e-gate interval statistics, inbound lane demand |
| `sensing:write` | Canonical sensing events from a sensor gateway (when sensors cannot push to `Ariva.Api.Ingest` directly) |
| `queues:read` | Current waits, nowcasts and interval statistics (for an airport app or FIDS) |
| `displays:read` | Display board content |

## Integration API v1 (canonical)

| Method and path | Scope | Body |
|---|---|---|
| `POST /api/v1/integration/sites/{siteCode}/flights/batch` (ARV-043) | `flights:write` | Batch of `FlightLeg`: `flightKey`, `carrier`, `number`, `suffix`, `direction` (Arrival, Departure), `scheduledUtc`, `estimatedUtc`, `actualUtc`, `onBlockUtc`, `offBlockUtc`, `origin`, `destination`, `terminal`, `stand`, `gate`, `aircraftType`, `seats`, `paxEstimate`, `status`, `codeshares[]` |
| `POST /api/v1/integration/sites/{siteCode}/flights/events` (ARV-043) | `flights:write` | Batch of `FlightEvent`: `flightKey`, `eventType` (Estimated, Landed, OnBlock, GateOpen, BoardingStart, OffBlock, Cancelled, Diverted), `timeUtc` |
| `POST /api/v1/integration/sites/{siteCode}/allocations/batch` (ARV-043) | `allocations:write` | Batch of `CounterAllocation`: `flightKey`, `checkpointCode`, `counterCodes[]`, `openUtc`, `closeUtc`, `handlerCode` |
| `POST /api/v1/immigration/desk-sessions` | `immigration:write` | Array of `DeskSessionChanged` (contract V1) |
| `POST /api/v1/immigration/desk-interval-stats` | `immigration:write` | Array of `DeskIntervalStats` (contract V1) |
| `POST /api/v1/immigration/egate-interval-stats` | `immigration:write` | Array of `EGateIntervalStats` (contract V1) |
| `POST /api/v1/immigration/inbound-lane-demand` | `immigration:write` | Array of `InboundFlightLaneDemand` (contract V1) |
| `POST /api/v1/aodb/aidx` | `flights:write` | AIDX 22.1 `IATA_AIDX_FlightLegNotifRQ` XML (XXE disabled, schema validated, 5 MB limit) |
| `GET /api/v1/queues/current?checkpoint=` | `queues:read` | Returns nowcast, realised wait, state and profile version per zone |
| `GET /api/v1/displays/{displayCode}/content` | `displays:read` | Display board bands, already hysteresis-filtered |

The immigration contracts are the same records AMAN publishes on Kafka (`Ariva.Business.Contracts.Aman.V1`), so a non-AMAN immigration system integrates by sending the same JSON. The contracts carry no person, document or officer identifiers, and the API rejects unknown fields.

Every Integration API path names the site (`/api/v1/integration/sites/{siteCode}/...`), which must be one of the client's bound sites; the site never comes from the body. The paths of the endpoints not yet built follow the same pattern when their stories land.

Responses follow Ariva's API convention, not AMAN's `Result<T>` envelope: the data on success and RFC 9457 problem details on failure. A batch answers 200 with `{ received, applied, unchanged, refused, items: [{ index, flightKey, applied, errors, warnings }] }`, also when some or all items were refused.

### Batches (ARV-043)

- Body: `{ "messageTimeUtc": optional UTC time the AODB produced the batch, "items": [ 1 to 500 items ] }`, `application/json`, at most 1 MB (413 beyond, answered by the call audit before the action and recorded). Read strictly: exact camelCase member names, no unknown or repeated member, no comments or trailing commas, numbers as numbers, at most 8 levels deep; a refusal names where the body went wrong only through members Ariva knows, so the client's text is never echoed (CWE-20, CWE-117).
- `Idempotency-Key` is required (one header, 8 to 64 letters, digits or `. _ : -`). The key is claimed in the transaction that applies the batch (script 0026, `integration_idempotency`, primary key client and key): a retry with the same key, operation, site and body SHA-256 gets the stored answer with `Idempotent-Replayed: true` and nothing is applied again; a concurrent retry waits on the uncommitted claim and then gets the same answer; the same key with another request is 422; a batch that fails (5xx) leaves no claim. Keys are kept 24 hours and swept every 10 minutes by Ariva.Api.Integration; the runtime role cannot rewrite an answer or delete a key before it expires.
- Limits (CWE-400): the `integration-batch` concurrency policy (`Security:RateLimiting:IntegrationBatch`, 8 at once and 16 waiting per replica) runs before authentication and before the call audit buffers a body, so excess batches get 429 without being read; each client may send `Security:RateLimiting:IntegrationClient` batches a minute per replica (120), counted in the action after the token and the client's record were checked (`IntegrationClientRateLimiter`), so a forged token cannot spend another client's allowance; the call audit buffers a body within the endpoint's limit in memory only; the ingress refuses bodies over 1 MB. A batch refused by the at-once limit is refused before authentication, so it appears in the rate limiter's metrics, not in `integration_call`; one refused by the client's allowance is recorded.
- The feed is the client's own (`api-` and the client id's 26 characters), so each client's freshness and stale-feed alarm are its own and the body cannot name another feed. Each item goes through `ISvcFlightIntake` (ARV-041): checked by `FlightRules`, applied on its own, never stopping the others.


## Outbound connections (Ariva calls the other system)

Created by administrators with step-up MFA as `OutboundEndpoint` records: base URL, allowed CIDRs, TLS settings (optional client certificate, optional pinned CA), timeout, retry and circuit breaker policy, and one authentication handler:

| Handler | Behaviour |
|---|---|
| `TotpClientCredentials` | AMAN-style: posts client id, secret and a freshly generated TOTP code to the remote auth endpoint, caches the access token until 60 seconds before expiry, sends `X-TOTP-Code` per request when the remote requires it. This is the AMAN connector. |
| `OAuth2ClientCredentials` | Standard token endpoint, scopes, cached token |
| `ApiKeyHeader` | Named header with a secret value |
| `HmacSignature` | Signs method, path, timestamp and body SHA-256 with a shared key; for systems that prefer request signing |
| `MutualTls` | Client certificate only |

Secrets are encrypted at rest. Calls use `IHttpClientFactory` named clients created from the registry; URLs are never taken from a caller at request time, redirects are not followed, and the resolved IP must fall inside the endpoint's allowed CIDRs (SSRF control, CWE-918).

## Standards adapters

| Adapter | Status | Notes |
|---|---|---|
| AIDX 22.1 inbound | Phase 0 against a mock | Flight legs and status; maps to `FlightLeg` and `FlightEvent` |
| ACRIS flight API pull | Phase 0 against a mock | Polls with `If-Modified-Since`; maps to `FlightLeg` |
| SSIM chapter 7 import | Phase 0 | Seasonal schedule fallback when no live feed exists |
| AMAN Kafka feed | Phase 0 against the simulator | Same contracts as the REST immigration endpoints |
| Vendor AODB REST (SITA, Amadeus, others) | Per project | Built as a mapping onto `FlightLeg` once API access is granted; mocked until then |

## Testing integrations

`Ariva.Simulation.Api` exposes mock AODB (AIDX push and ACRIS pull), a mock AMAN (Kafka and REST, with its own TOTP client so Ariva's outbound `TotpClientCredentials` handler is exercised), and a mock immigration system that uses the generic REST endpoints. End-to-end tests in `Platform/Testing/Ariva.E2E` cover the token exchange, TOTP replay rejection, scope and site enforcement, idempotency, and every endpoint above.
