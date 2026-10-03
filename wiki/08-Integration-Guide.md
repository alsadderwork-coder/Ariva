# Integration guide

For developers who connect an AODB, an immigration system or a sensor gateway to Ariva, and for those who consume Ariva's wait times. The normative specification is `../docs/architecture/integration.md`; this page explains it with examples. Where they differ, the specification wins.

Status: the Integration API v1 is specified; its implementation is a Target procedure, implemented in Phase 0 epic Integration API and mocks (AIDX, ACRIS pull, SSIM import and the AMAN Kafka feed are built against mocks in Phase 0). Today the Integration host serves health probes only. Field names and enumerations not listed in the specification are marked illustrative until the OpenAPI document is published.

## 1. Choose a route

| Direction | Transport | Use it when | Host |
|---|---|---|---|
| Inbound push, generic | HTTPS JSON, Integration API v1 | Your system can call a REST API (any AODB or immigration system) | Ariva.Api.Integration |
| Inbound push, standards | HTTPS, AIDX 22.1 XML on `/api/v1/aodb/aidx` | Your AODB emits AIDX | Ariva.Api.Integration |
| Inbound stream | Kafka topics `aman.feed.*.v1` | AMAN shares or bridges a Kafka cluster with Ariva | Ariva.Api.Integration |
| Outbound pull | HTTPS to a registered `OutboundEndpoint` | Ariva polls an ACRIS flight API, the AMAN Integration API or a vendor AODB REST API | Ariva.Api.Integration, scheduled by Ariva.Api.Cronz |
| File import | SSIM file upload | No live feed exists yet | Ariva.Api.Main |

Every route normalises into the same canonical commands; the rest of Ariva never knows which system the data came from.

Base URL: the Integration host's ingress, `https://<api-integration subdomain>.<site domain>` (in Dalil's dev environment `https://api-integration-dev-ariva.dalilhub.tech`).

## 2. Onboarding

| # | Step | Who |
|---|---|---|
| 1 | Request a client: system name, kind (`Aodb`, `Immigration`, `SensorGateway`, `Other`), scopes, site codes, source IP ranges (CIDR), whether per-request TOTP is needed | Integrator, to the site's Ariva administrator |
| 2 | Create the `IntegrationClient` (requires step-up MFA). Ariva generates a public `ClientId`, a 256-bit `ClientSecret` (shown once, stored only as a PBKDF2-SHA256 hash) and a 160-bit Base32 TOTP seed (stored encrypted; the `otpauth://` URI and QR code are shown once) | Ariva System administrator |
| 3 | Hand over the secret and the seed out of band, by two separate channels (recommended) | Administrator |
| 4 | Load the seed into an RFC 6238 library (Otp.NET, pyotp, otplib, aerogear-otp) | Integrator |
| 5 | Develop and test against the test environment (section 13) | Integrator |
| 6 | Pass the certification checklist (section 14) | Integrator with Dalil |
| 7 | Receive separate production credentials | Administrator |

## 3. TOTP parameters

| Parameter | Value |
|---|---|
| Standard | RFC 6238 |
| Algorithm | SHA1 |
| Digits | 6 |
| Step | 30 seconds |
| Accepted window | One step either side of the server's current step |
| Replay | Each time step is accepted once per client for the token exchange; a code already used cannot be used again |

Consequences for your client:

- Keep its clock on NTP.
- To authenticate twice within one 30-second step, wait for the next step.
- `X-TOTP-Code` on data calls (clients whose per-request policy is on) is checked against the same window but without the replay guard, so a client can make many calls in one step (decided in ARV-042). Generate it from the current time on every call; it proves the caller still holds the seed, and it never works without a valid token.

## 4. Token exchange

`POST /api/v1/auth`

```json
{ "clientId": "<client id>", "clientSecret": "<client secret>", "totpCode": "123456" }
```

Success:

```json
{ "accessToken": "<JWT>", "expiresAt": "2026-10-01T10:15:00Z", "sessionId": "<session id>" }
```

Any failure: `401` with `{ "error": "invalid_client" }`. The checks, in order: client exists, is active and the caller's IP is in an allowed CIDR; secret hash matches (constant time); TOTP code valid; time step newer than the last accepted step; rate limits. One generic error for every case.

The token is a JWT with audience `ariva-integration`, lifetime 15 minutes, claims `sub` (client id), `sid` (new session id), `scope` and `site`. There is no refresh token: re-authenticate with a fresh TOTP code before `expiresAt` (Ariva's own outbound connector refreshes 60 seconds before expiry; do the same).

Rate limits and lockout on `/api/v1/auth`: 5 attempts per minute per client, 20 per minute per IP; 10 consecutive failures lock the client for 15 minutes and alert Ariva's administrators. Rate-limited attempts also return `401 invalid_client` (a per-client attempt over the limit does not count toward the lockout, so nobody can lock a client out by hammering its id).

Formats (ARV-042): the client id is `ic_` and 26 lower-case letters or digits 2 to 7; the secret is `ics_` and 43 base64url characters. Any change an administrator makes to the client (scopes, sites, networks, the per-request policy, a new secret or seed, disabling it) stops the tokens it already holds; exchange again. Each call is also checked against the client's allowed networks.

Connectivity check: `GET /api/v1/integration/sites/{siteCode}/flights/check` (scope `flights:write`) and `GET /api/v1/integration/sites/{siteCode}/immigration/check` (scope `immigration:write`) answer 200 with your client id, scopes and sites when your token holds that scope for that site, 403 when it does not and 401 without a valid token (or without a fresh `X-TOTP-Code` when your policy needs one). They change nothing; use them to test your set-up.

### C# (Otp.NET)

```csharp
using System.Net.Http.Headers;
using System.Net.Http.Json;
using OtpNet;

var baseUrl = new Uri("https://api-integration-dev-ariva.dalilhub.tech");
var totp = new Totp(Base32Encoding.ToBytes(seedBase32)); // SHA1, 6 digits, 30 s by default
using var http = new HttpClient { BaseAddress = baseUrl };

var auth = await http.PostAsJsonAsync("/api/v1/auth", new
{
    clientId,
    clientSecret,
    totpCode = totp.ComputeTotp(DateTime.UtcNow)
});
auth.EnsureSuccessStatusCode();
var token = await auth.Content.ReadFromJsonAsync<AuthResponse>();

using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/flights/events")
{
    Content = JsonContent.Create(new[]
    {
        new { flightKey = "DM214-20261001-A", eventType = "OnBlock", timeUtc = DateTime.UtcNow } // serialised with a Z suffix
    })
};
request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);
request.Headers.Add("X-TOTP-Code", totp.ComputeTotp(DateTime.UtcNow));
request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

var response = await http.SendAsync(request);

public sealed record AuthResponse(string AccessToken, DateTimeOffset ExpiresAt, string SessionId);
```

### Python (pyotp)

```python
import uuid
import pyotp
import requests

base = "https://api-integration-dev-ariva.dalilhub.tech"
totp = pyotp.TOTP(seed_base32)  # SHA1, 6 digits, 30 s by default

auth = requests.post(f"{base}/api/v1/auth", json={
    "clientId": client_id,
    "clientSecret": client_secret,
    "totpCode": totp.now(),
}, timeout=10)
auth.raise_for_status()
token = auth.json()["accessToken"]

events = [{"flightKey": "DM214-20261001-A", "eventType": "OnBlock", "timeUtc": "2026-10-01T14:21:00Z"}]
r = requests.post(f"{base}/api/v1/flights/events", json=events, timeout=10, headers={
    "Authorization": f"Bearer {token}",
    "X-TOTP-Code": totp.now(),
    "Idempotency-Key": str(uuid.uuid4()),
})
print(r.status_code, r.json())
```

### curl (with oathtool)

For testing only: secrets on a command line are visible to other users of the machine.

```bash
BASE=https://api-integration-dev-ariva.dalilhub.tech

TOKEN=$(curl -sS -X POST "$BASE/api/v1/auth" \
  -H 'Content-Type: application/json' \
  -d "{\"clientId\":\"$ARIVA_CLIENT_ID\",\"clientSecret\":\"$ARIVA_CLIENT_SECRET\",\"totpCode\":\"$(oathtool --totp -b "$ARIVA_TOTP_SEED")\"}" \
  | jq -r .accessToken)

curl -sS -X POST "$BASE/api/v1/flights/events" \
  -H "Authorization: Bearer $TOKEN" \
  -H "X-TOTP-Code: $(oathtool --totp -b "$ARIVA_TOTP_SEED")" \
  -H "Idempotency-Key: $(uuidgen)" \
  -H 'Content-Type: application/json' \
  -d '[{"flightKey":"DM214-20261001-A","eventType":"OnBlock","timeUtc":"2026-10-01T14:21:00Z"}]'
```

## 5. Headers, idempotency and limits

| Header | When | Notes |
|---|---|---|
| `Authorization: Bearer <token>` | Every call | Token from `/api/v1/auth` |
| `X-TOTP-Code: <current code>` | Every call when the client's per-request TOTP policy is on | On by default for immigration clients (AMAN parity) |
| `Idempotency-Key: <uuid>` | Every write | Ariva stores keys for 24 hours and returns the original result for a repeat. Reuse the same key when retrying the same request; use a new key for new data. Behaviour when the same key arrives with a different body: To confirm |
| `Content-Type` | Every write | `application/json`; AIDX uses XML |

| Limit | Value |
|---|---|
| Body size, JSON APIs | 1 MB |
| Body size, AIDX | 5 MB |
| Items per batch | 500 |
| JSON nesting depth | 32 |
| Unknown fields | Rejected |
| Timestamps | UTC, ISO 8601 (for example `2026-10-01T14:21:00Z`) |

Authorisation is by scope (`[IntegrationScope("flights:write")]` on each endpoint) and by site binding: a client bound to `DMO` cannot write `BEY`. Every call is audited with client, scope, endpoint, site, result and the payload's SHA-256.

## 6. Scopes

| Scope | Allows |
|---|---|
| `flights:write` | Flight schedule and flight status events, AIDX push |
| `allocations:write` | Check-in counter and stand allocations |
| `immigration:write` | Desk sessions, desk interval statistics, e-gate interval statistics, inbound lane demand |
| `sensing:write` | Canonical sensing events from a sensor gateway, when sensors cannot push to Ariva.Api.Ingest directly |
| `queues:read` | Current waits, nowcasts and interval statistics (airport app, FIDS) |
| `displays:read` | Display board content |

## 7. Response shape

Responses use AMAN's `Result<T>` shape, with per-item results for batches:

```json
{
  "hasErrors": false,
  "errorMessages": [],
  "warningMessages": [],
  "infoMessages": [],
  "data": [ { "index": 0, "hasErrors": false, "errorMessages": [] } ]
}
```

The per-item object above is illustrative; its exact fields come with the OpenAPI document. Whether a batch with some failed items returns 200 or an error status is To confirm.

## 8. Endpoints

### POST /api/v1/flights/batch

Scope `flights:write`. An array of `FlightLeg`. The `flightKey` is a stable key you choose, unique per flight leg (format To confirm; examples use carrier, number, date and direction). `status` values are illustrative.

```json
[
  {
    "flightKey": "DM214-20261001-A",
    "carrier": "DM",
    "number": "214",
    "suffix": null,
    "direction": "Arrival",
    "scheduledUtc": "2026-10-01T14:05:00Z",
    "estimatedUtc": "2026-10-01T14:12:00Z",
    "actualUtc": null,
    "onBlockUtc": null,
    "offBlockUtc": null,
    "origin": "BEY",
    "destination": "DMO",
    "terminal": "T1",
    "stand": "B12",
    "gate": "B12",
    "aircraftType": "A320",
    "seats": 180,
    "paxEstimate": 162,
    "status": "Scheduled",
    "codeshares": ["XR1214"]
  }
]
```

Illustrative response for a batch of two legs where the second failed validation (per-item field names follow the OpenAPI document once published):

```json
{
  "hasErrors": true,
  "errorMessages": [],
  "warningMessages": [],
  "infoMessages": [],
  "data": [
    { "index": 0, "flightKey": "DM214-20261001-A", "hasErrors": false, "errorMessages": [] },
    { "index": 1, "flightKey": "QL118-20261001-A", "hasErrors": true, "errorMessages": ["scheduledUtc is required"] }
  ]
}
```

The other write endpoints answer with the same shape, one item per element of the request array.

### POST /api/v1/flights/events

Scope `flights:write`. An array of `FlightEvent`. `eventType` is one of `Estimated`, `Landed`, `OnBlock`, `GateOpen`, `BoardingStart`, `OffBlock`, `Cancelled`, `Diverted`.

```json
[
  { "flightKey": "DM214-20261001-A", "eventType": "Landed", "timeUtc": "2026-10-01T14:14:00Z" },
  { "flightKey": "DM214-20261001-A", "eventType": "OnBlock", "timeUtc": "2026-10-01T14:21:00Z" }
]
```

Send each event with the time it happened; Ariva applies messages by their own timestamps, so late or out-of-order delivery is safe.

### POST /api/v1/allocations/batch

Scope `allocations:write`. An array of `CounterAllocation`.

```json
[
  {
    "flightKey": "XR331-20261001-D",
    "checkpointCode": "CI-C",
    "counterCodes": ["C01", "C02", "C03", "C04"],
    "openUtc": "2026-10-01T15:30:00Z",
    "closeUtc": "2026-10-01T17:45:00Z",
    "handlerCode": "HB"
  }
]
```

### POST /api/v1/immigration/desk-sessions

Scope `immigration:write`. An array of `DeskSessionChanged` (contract V1). `state` is `Opened`, `Closed` or `Paused` (numeric values 1, 2, 3 are fixed by the contract; the JSON representation is To confirm). `laneCategory` is empty when closed. No officer identity is accepted.

```json
[
  {
    "siteCode": "DMO",
    "deskCode": "A-07",
    "state": "Opened",
    "laneCategory": "VIS",
    "occurredAtUtc": "2026-10-01T14:20:00Z",
    "sourceEventId": "imm-7f3c2a"
  }
]
```

### POST /api/v1/immigration/desk-interval-stats

Scope `immigration:write`. An array of `DeskIntervalStats`. Intervals are one minute: `intervalSeconds` must be 60. `transactionsProcessed` counts approaches (a family of four is one); `documentsProcessed` counts documents (the same family is four).

```json
[
  {
    "siteCode": "DMO",
    "deskCode": "A-07",
    "intervalStartUtc": "2026-10-01T14:31:00Z",
    "intervalSeconds": 60,
    "transactionsProcessed": 1,
    "documentsProcessed": 3,
    "meanServiceSeconds": 41.0,
    "p90ServiceSeconds": 41.0,
    "meanCycleSeconds": 58.0,
    "laneCategory": "VIS",
    "sourceEventId": "imm-7f3c2b"
  }
]
```

### POST /api/v1/immigration/egate-interval-stats

Scope `immigration:write`. An array of `EGateIntervalStats`. `rejectsByCategory` uses the coarse categories `Other`, `DocumentRead`, `BiometricCapture`, `Eligibility`, `ReferredToOfficer`, `Technical`; its values sum to `rejected`. Any category with fewer than 3 rejects in the interval must be folded into `Other`, so no individual outcome can be inferred. In this example two rejects of different kinds both go to `Other`.

```json
[
  {
    "siteCode": "DMO",
    "gateCode": "EG-03",
    "intervalStartUtc": "2026-10-01T14:31:00Z",
    "intervalSeconds": 60,
    "attempts": 4,
    "accepted": 2,
    "rejected": 2,
    "rejectsByCategory": { "Other": 2 },
    "meanCycleSeconds": 18.0,
    "sourceEventId": "imm-7f3c2c"
  }
]
```

### POST /api/v1/immigration/inbound-lane-demand

Scope `immigration:write`. An array of `InboundFlightLaneDemand`, computed from API data. `passengersByLane` covers the manual lanes (`CIT`, `RES`, `VIS`, `CRW`); `eGateEligible` is separate. `boardedTotal` need not equal the sum (transfers and crew). The latest `computedAtUtc` wins. The example is the reference split for 200 passengers.

```json
[
  {
    "siteCode": "DMO",
    "flightKey": "DM214-20261001-A",
    "scheduledArrivalUtc": "2026-10-01T14:05:00Z",
    "boardedTotal": 200,
    "passengersByLane": { "CIT": 42, "RES": 24, "VIS": 70, "CRW": 4 },
    "eGateEligible": 44,
    "computedAtUtc": "2026-10-01T12:40:00Z",
    "sourceEventId": "imm-7f3c2d"
  }
]
```

The four immigration contracts are the same records AMAN publishes on Kafka (`Ariva.Business.Contracts.Aman.V1`), so a non-AMAN immigration system integrates by sending the same JSON. They carry no person, document or officer identifiers, and the API rejects unknown fields. JSON property casing follows the OpenAPI document (camelCase shown here).

### POST /api/v1/aodb/aidx

Scope `flights:write`. Body: an IATA AIDX 22.1 `IATA_AIDX_FlightLegNotifRQ` message, `Content-Type` XML, up to 5 MB. Ariva disables external entities (XXE), validates against the 22.1 schema and maps flight legs and status to `FlightLeg` and `FlightEvent`. Site-specific `TPA_Extensions` are mapped per site. Note: D5 records that SITA's AIDX API supports the 21.2 schema; the version used by each AODB is To confirm during onboarding.

### GET /api/v1/queues/current?checkpoint=

Scope `queues:read`. Returns, per zone of the checkpoint, the nowcast, the realised wait, the state and the zone profile version. Illustrative response:

```json
{
  "hasErrors": false,
  "errorMessages": [],
  "warningMessages": [],
  "infoMessages": [],
  "data": [
    {
      "zoneCode": "A-VIS",
      "nowcastMinutes": 12.5,
      "realisedWaitMinutes": 10.0,
      "dataQuality": "Good",
      "zoneProfileVersion": 12,
      "computedAtUtc": "2026-10-01T14:35:10Z"
    }
  ]
}
```

Consumers must handle a null nowcast (no service: no staffed desk) and `Degraded` or `Unknown` quality.

### GET /api/v1/displays/{displayCode}/content

Scope `displays:read`. Returns the display board's bands, already hysteresis-filtered (5-minute bands; a band changes only when the nowcast moves a full band; a neutral message on stale data). Signage systems should show what they receive and never compute their own bands. Illustrative response:

```json
{
  "hasErrors": false,
  "errorMessages": [],
  "warningMessages": [],
  "infoMessages": [],
  "data": {
    "displayCode": "DSP-01",
    "items": [
      { "checkpointCode": "ARR-PASSPORT", "bandFromMinutes": 10, "bandToMinutes": 15, "stale": false },
      { "checkpointCode": "ARR-EGATES", "bandFromMinutes": 0, "bandToMinutes": 5, "stale": false }
    ]
  }
}
```

## 9. Standards notes

| Adapter | Status | Notes |
|---|---|---|
| AIDX 22.1 inbound | Phase 0 against a mock | Primary AODB adapter; flight legs and status |
| ACRIS flight API pull | Phase 0 against a mock | Ariva polls with `If-Modified-Since` through a registered `OutboundEndpoint`; maps to `FlightLeg`. ACRIS Passenger Wait Times API v1.6.0 is the reference for Ariva's outbound wait-times publishing (v1) |
| SSIM chapter 7 import | Phase 0 | File upload through Ariva.Api.Main (file imports up to 20 MB, streamed). Seasonal schedule fallback and day-one pilots without a live feed. Endpoint path To confirm |
| AMAN Kafka feed | Phase 0 against the simulator | Same contracts as the REST immigration endpoints |
| Vendor AODB REST (SITA, Amadeus, others) | Per project | A mapping onto `FlightLeg` once API access and documentation are granted; mocked until then |

Flight data rules: the layer keeps a canonical flight id map (diversions, renumbering, codeshares), applies messages by their own timestamps, and raises a stale-feed alarm when a heartbeat or expected update is missing. Agree the expected update cadence with each AODB.

How the model applies them (ARV-041), whatever the adapter:

- A leg is identified by the site and its `flightKey`; the direction never changes (the same key with the other direction is refused). Send the leg before its events or counters; events and allocations for a key the site does not know are refused.
- Each item is checked on its own and a bad one never stops the others: airline designators (two-character IATA or three-letter ICAO), flight numbers of 1 to 4 digits, IATA airport codes, stand, gate and terminal codes, aircraft types, seats and passengers up to 1,000, at most 20 codeshares, enum names exactly as documented (`Arrival`, not `arrival`), every time in UTC, a scheduled time at most 3 days ago and 400 days ahead, estimates and actuals at most a day early and 3 days late.
- Out of order is safe: the schedule fields are replaced only by a message at least as recent as the one that set them, and each milestone keeps the value of the most recent message that reported it; an older message can still fill a milestone nobody has reported. A status of `Cancelled` or `Diverted` holds until a more recent message states another status. A message that repeats what Ariva knows changes nothing. The message time is the feed's timestamp where the format has one, otherwise when Ariva received it; it may be at most 5 minutes ahead of Ariva's clock.
- Counter allocations are for departures at check-in checkpoints of the site; counter codes resolve to Ariva desks through the AODB desk code mappings (Administration guide), and codes without a mapping are kept apart and reported as a warning, never guessed. Counters stay open at most 24 hours.
- A call with at least one item Ariva could check counts as a message from its feed for the stale-feed alarm (Operations runbook 4.3); an empty batch or one whose items are all refused does not. A feed name and site come from the adapter and the client's binding, never from the message.

## 10. AMAN Kafka feed (border deployments)

| Topic | Key | Contract | Ariva use |
|---|---|---|---|
| `aman.feed.desk-session-changed.v1` | AMAN desk code | `DeskSessionChanged` | Login signal (rank 2) in the desk state machine |
| `aman.feed.desk-interval-stats.v1` | AMAN desk code | `DeskIntervalStats` | Service and cycle times for nowcast and forecasts |
| `aman.feed.egate-interval-stats.v1` | AMAN gate code | `EGateIntervalStats` | E-gate throughput and reject coupling |
| `aman.feed.inbound-flight-lane-demand.v1` | AMAN flight key | `InboundFlightLaneDemand` | Arrival lane split |

- AMAN publishes through its existing outbox inside the border deployment; Ariva consumes only. Ariva never writes back to AMAN through these types.
- Consumption is idempotent by `SourceEventId`; interval records with any length other than 60 seconds are rejected.
- AMAN codes are resolved against the active zone profile's desk code mappings; unknown codes are parked and alerted, never guessed.
- The feed's heartbeat is watched: when it stops, desk state falls back to sensor zones and service rates to their last known values, flagged.
- In a shared cluster, Ariva's consumer has read ACLs on `aman.feed.` and write ACLs only on `ariva.`.
- Versioning: additive changes within V1 bump `ContractVersion.Current` (currently `1.0`); breaking changes go to `Aman/V2` and run in parallel until AMAN moves over. A data-boundary test fails the build if a contract property looks like a person or officer identifier.

## 11. Outbound connections

Ariva calls other systems only through `OutboundEndpoint` records created by administrators with step-up MFA: base URL, allowed CIDRs, TLS settings (optional client certificate, optional pinned CA), timeout, retry and circuit breaker policy, and one authentication handler.

| Handler | Behaviour |
|---|---|
| `TotpClientCredentials` | AMAN style: posts client id, secret and a fresh TOTP code to the remote auth endpoint, caches the token until 60 seconds before expiry, sends `X-TOTP-Code` per request when required. This is the AMAN connector |
| `OAuth2ClientCredentials` | Standard token endpoint, scopes, cached token |
| `ApiKeyHeader` | Named header with a secret value |
| `HmacSignature` | Signs method, path, timestamp and body SHA-256 with a shared key |
| `MutualTls` | Client certificate only |

URLs are never taken from a caller at request time, redirects are not followed, HTTPS is required (HTTP only for allowlisted lab hosts), and the resolved IP must fall inside the endpoint's allowed CIDRs.

## 12. Errors and rate limits

| Status | When | What to do |
|---|---|---|
| 200 with `hasErrors: false` | Accepted | |
| 200 or error status with per-item errors | Some items rejected (status To confirm) | Fix and resend those items with new idempotency keys |
| 400 | Validation failure, unknown field, JSON deeper than 32 levels, malformed AIDX | Fix the payload; do not retry unchanged |
| 401 `invalid_client` | Any token exchange failure, including rate limiting and lockout | Check credentials, clock and source IP; back off |
| 401 | Missing or expired token | Re-authenticate |
| 403 | Scope not granted, or site not bound to the client | Ask the administrator; do not retry |
| 413 | Body larger than the limit | Split the batch |
| 5xx | Server or dependency failure | Retry with the same `Idempotency-Key` and exponential backoff |

Status for a missing or wrong `X-TOTP-Code` on a data call: To confirm. Rate limits on data endpoints: To confirm (the token exchange limits are fixed above).

## 13. Test environment

`Ariva.Simulation.Api` provides:

- A mock AODB: AIDX push and ACRIS pull.
- A mock AMAN: Kafka and REST, with its own TOTP client so Ariva's outbound `TotpClientCredentials` handler is exercised.
- A mock immigration system that uses the generic REST endpoints.
- A sensor emulator (ARV-028) that plays the reference day to Ingest as registered devices, at up to 60 times real time, with start, pause, speed and jump controls.

The reference scenario is the seeded day at the fictional Demo International Airport (site code `DMO`, seed 9303). Dalil issues test clients on the dev or demo environment (`https://api-integration-dev-ariva.dalilhub.tech`, simulator at `https://simulation-dev-ariva.dalilhub.tech`). Ariva's end-to-end tests (`Platform/Testing/Ariva.E2E`) cover the token exchange, TOTP replay rejection, scope and site enforcement, idempotency and every endpoint above; use them as the reference behaviour.

## 14. Certification checklist for a new integration

| # | Check | Evidence |
|---|---|---|
| 1 | Token exchange succeeds; the client re-authenticates before `expiresAt` | Log of a 2-hour run |
| 2 | No TOTP code is reused; the client waits for the next step when needed | Log |
| 3 | `X-TOTP-Code` sent on every call when the policy is on | Request capture |
| 4 | Every write carries an `Idempotency-Key`; retries reuse it | Request capture, retry test |
| 5 | Batches at most 500 items and 1 MB | Test with a full day's volume |
| 6 | All timestamps in UTC; event times are when things happened, not when sent | Payload review |
| 7 | Flight identity changes (diversion, renumbering, codeshare) handled; out-of-order delivery tested | Replay of a recorded day |
| 8 | Expected update cadence agreed and met, so the stale-feed alarm is meaningful | Agreement record |
| 9 | No unknown fields; no personal, document or officer data in any payload | Payload review, data-boundary check |
| 10 | Site codes and desk, gate and counter codes match the site's configuration | Mapping table |
| 11 | Errors handled per section 12; backoff on 5xx; no retry on 4xx | Fault-injection test |
| 12 | Client clock on NTP | Configuration evidence |
| 13 | Secrets and the TOTP seed stored in a secret store, not in code or logs | Security review |
| 14 | Calls come only from the registered CIDRs | Network test |
| 15 | A recorded production-like day replays cleanly in the test environment | Test report signed by Dalil |

Recorded feeds from each pilot airport are added to Ariva's replay suite so later releases are tested against them.
