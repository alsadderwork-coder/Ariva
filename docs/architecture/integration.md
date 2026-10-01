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
| `POST /api/v1/flights/batch` | `flights:write` | Array of `FlightLeg`: `flightKey`, `carrier`, `number`, `suffix`, `direction` (Arrival, Departure), `scheduledUtc`, `estimatedUtc`, `actualUtc`, `onBlockUtc`, `offBlockUtc`, `origin`, `destination`, `terminal`, `stand`, `gate`, `aircraftType`, `seats`, `paxEstimate`, `status`, `codeshares[]` |
| `POST /api/v1/flights/events` | `flights:write` | Array of `FlightEvent`: `flightKey`, `eventType` (Estimated, Landed, OnBlock, GateOpen, BoardingStart, OffBlock, Cancelled, Diverted), `timeUtc` |
| `POST /api/v1/allocations/batch` | `allocations:write` | Array of `CounterAllocation`: `flightKey`, `checkpointCode`, `counterCodes[]`, `openUtc`, `closeUtc`, `handlerCode` |
| `POST /api/v1/immigration/desk-sessions` | `immigration:write` | Array of `DeskSessionChanged` (contract V1) |
| `POST /api/v1/immigration/desk-interval-stats` | `immigration:write` | Array of `DeskIntervalStats` (contract V1) |
| `POST /api/v1/immigration/egate-interval-stats` | `immigration:write` | Array of `EGateIntervalStats` (contract V1) |
| `POST /api/v1/immigration/inbound-lane-demand` | `immigration:write` | Array of `InboundFlightLaneDemand` (contract V1) |
| `POST /api/v1/aodb/aidx` | `flights:write` | AIDX 22.1 `IATA_AIDX_FlightLegNotifRQ` XML (XXE disabled, schema validated, 5 MB limit) |
| `GET /api/v1/queues/current?checkpoint=` | `queues:read` | Returns nowcast, realised wait, state and profile version per zone |
| `GET /api/v1/displays/{displayCode}/content` | `displays:read` | Display board bands, already hysteresis-filtered |

The immigration contracts are the same records AMAN publishes on Kafka (`Ariva.Business.Contracts.Aman.V1`), so a non-AMAN immigration system integrates by sending the same JSON. The contracts carry no person, document or officer identifiers, and the API rejects unknown fields.

Responses use the AMAN `Result<T>` shape: `{ hasErrors, errorMessages, warningMessages, infoMessages, data }`, with per-item results for batches.

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
