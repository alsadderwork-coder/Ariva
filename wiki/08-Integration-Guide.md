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

using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/integration/sites/DMO/flights/events")
{
    Content = JsonContent.Create(new
    {
        items = new[] { new { flightKey = "DM214-20261001-A", eventType = "OnBlock", timeUtc = DateTime.UtcNow } } // serialised with a Z suffix
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

events = {"items": [{"flightKey": "DM214-20261001-A", "eventType": "OnBlock", "timeUtc": "2026-10-01T14:21:00Z"}]}
r = requests.post(f"{base}/api/v1/integration/sites/DMO/flights/events", json=events, timeout=10, headers={
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

curl -sS -X POST "$BASE/api/v1/integration/sites/DMO/flights/events" \
  -H "Authorization: Bearer $TOKEN" \
  -H "X-TOTP-Code: $(oathtool --totp -b "$ARIVA_TOTP_SEED")" \
  -H "Idempotency-Key: $(uuidgen)" \
  -H 'Content-Type: application/json' \
  -d '{"items":[{"flightKey":"DM214-20261001-A","eventType":"OnBlock","timeUtc":"2026-10-01T14:21:00Z"}]}'
```

## 5. Headers, idempotency and limits

| Header | When | Notes |
|---|---|---|
| `Authorization: Bearer <token>` | Every call | Token from `/api/v1/auth` |
| `X-TOTP-Code: <current code>` | Every call when the client's per-request TOTP policy is on | On by default for immigration clients (AMAN parity) |
| `Idempotency-Key: <key>` | Every batch write (required) | 8 to 64 letters, digits or `. _ : -` (a UUID fits). Ariva keeps keys for 24 hours per client. A retry with the same key and the same body to the same endpoint and site gets the original answer with `Idempotent-Replayed: true`, and nothing is applied twice, also when retries overlap. The same key with a different body, endpoint or site answers 422. Use a new key for new data. |
| `Content-Type` | Every write | `application/json`; AIDX uses XML |

| Limit | Value |
|---|---|
| Body size, JSON APIs | 1 MB (413 beyond) |
| Body size, AIDX | 5 MB |
| Items per batch | 1 to 500 |
| Calls per client | 120 a minute per Ariva.Api.Integration replica, every Integration API call counted (429 with `Retry-After` beyond) |
| Batches at once | 8 per replica, 16 waiting (429 beyond, before the body is read); AIDX messages 2, with 4 waiting |
| JSON nesting depth, batches | 8 |
| Unknown or repeated members | Rejected (400); member names are exact camelCase |
| Comments, trailing commas, numbers as strings | Rejected (400) |
| Timestamps | UTC, ISO 8601 ending in `Z` (for example `2026-10-01T14:21:00Z`); a time with an offset is refused |

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

Ariva answers like the rest of its API: the data on success and RFC 9457 problem details (`application/problem+json`) on failure. A batch answers 200 with one result per item, in order, also when some or all items were refused:

```json
{
  "received": 2,
  "applied": 1,
  "unchanged": 0,
  "refused": 1,
  "items": [
    { "index": 0, "flightKey": "DM214-20261001-A", "applied": true, "errors": [], "warnings": [] },
    { "index": 1, "flightKey": "QL118-20261001-A", "applied": false, "errors": ["scheduledUtc is required."], "warnings": [] }
  ]
}
```

`applied` is true when the item changed something; an item that carries nothing newer than what Ariva knows is `unchanged`, with a warning. Item errors name the field and the rule, never your value. A whole batch that is not valid (bad JSON, unknown member, no or too many items, a bad `Idempotency-Key`) is one 400 problem and nothing is applied.

## 8. Endpoints

Every endpoint names the site in its path: `/api/v1/integration/sites/{siteCode}/...`, where `siteCode` is one of the sites your client is bound to (403 otherwise). Your batches are your client's own feed: Ariva tracks each client's freshness and raises the stale-feed alarm per client. Two connectivity checks, `GET .../flights/check` and `GET .../immigration/check`, answer 200 with who you are when your token, scope and site are right.

### POST /api/v1/integration/sites/{siteCode}/flights/batch

Scope `flights:write`. A batch of `FlightLeg`. The `flightKey` is a stable key you choose, unique per flight leg at the site (1 to 64 letters, digits or `. _ : -`; examples use carrier, number, date and direction); a key keeps its direction. `messageTimeUtc` is when your system produced the batch (optional; at most 5 minutes ahead of Ariva's clock and 30 days behind): Ariva applies each field by the time of the message that set it, so late and out-of-order batches are safe. Without it, Ariva's receive time is used. `status` is a flight status name; only `Cancelled` and `Diverted` carry meaning of their own, the rest follows from the times.

```http
POST /api/v1/integration/sites/DMO/flights/batch
Authorization: Bearer <token>
Idempotency-Key: 0f8fad5b-d9cb-469f-a165-70867728950e
Content-Type: application/json
```

```json
{
  "messageTimeUtc": "2026-10-01T13:58:00Z",
  "items": [
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
      "status": null,
      "codeshares": ["XR1214"]
    }
  ]
}
```

| Field | Rule |
|---|---|
| `carrier` | Two-character IATA or three-letter ICAO designator |
| `number`, `suffix` | 1 to 4 digits; one letter |
| `direction` | `Arrival` or `Departure` |
| `scheduledUtc` | Required; at most 3 days ago and 400 days ahead |
| `estimatedUtc`, `actualUtc`, `onBlockUtc`, `offBlockUtc` | At most a day before and 3 days after `scheduledUtc`; on-block for arrivals, off-block for departures |
| `origin`, `destination` | IATA or ICAO airport codes |
| `terminal`, `stand`, `gate` | 1 to 16 letters, digits or `. _ / -` |
| `aircraftType` | 2 to 4 letters or digits |
| `seats`, `paxEstimate` | 0 to 1,000 |
| `codeshares` | At most 20, each a designator and number such as `XR1214` |

### POST /api/v1/integration/sites/{siteCode}/flights/events

Scope `flights:write`. A batch of `FlightEvent` for legs already sent (an event for an unknown key is refused). `eventType` is exactly one of `Estimated`, `Landed`, `OnBlock`, `GateOpen`, `BoardingStart`, `OffBlock`, `Cancelled`, `Diverted`.

```json
{
  "items": [
    { "flightKey": "DM214-20261001-A", "eventType": "Landed", "timeUtc": "2026-10-01T14:14:00Z" },
    { "flightKey": "DM214-20261001-A", "eventType": "OnBlock", "timeUtc": "2026-10-01T14:21:00Z" }
  ]
}
```

Send each event with the time it happened; Ariva keeps the most recent report of each milestone, so late or out-of-order delivery is safe.

### POST /api/v1/integration/sites/{siteCode}/allocations/batch

Scope `allocations:write` (separate from `flights:write`). A batch of `CounterAllocation` for departing legs already sent, at a check-in checkpoint of the site. Counter codes are your AODB's; Ariva resolves them through the desk code mappings of the checkpoint (system `Aodb`) and keeps unmapped codes apart with a warning, never guessing.

```json
{
  "items": [
    {
      "flightKey": "XR331-20261001-D",
      "checkpointCode": "CI",
      "counterCodes": ["C01", "C02", "C03", "C04"],
      "openUtc": "2026-10-01T15:30:00Z",
      "closeUtc": "2026-10-01T17:45:00Z",
      "handlerCode": "HB"
    }
  ]
}
```

Counters stay open at most 24 hours; 1 to 100 counter codes per allocation.

### POST /api/v1/integration/sites/{siteCode}/immigration/desk-sessions

Scope `immigration:write` (ARV-048). A batch of `DeskSessionChanged` (contract V1) in `items`. `state` is `Opened`, `Closed` or `Paused`, by its exact name: a number, another case (`opened`), padding or comma-joined names are refused, and the same holds for the keys of `rejectsByCategory`. `laneCategory` is empty when closed. No officer identity is accepted.

```json
{
  "items": [
    {
      "siteCode": "DMO",
      "deskCode": "IN07",
      "state": "Opened",
      "laneCategory": "VIS",
      "occurredAtUtc": "2026-10-01T14:20:00Z",
      "sourceEventId": "imm-7f3c2a"
    }
  ]
}
```

### POST /api/v1/integration/sites/{siteCode}/immigration/desk-interval-stats

Scope `immigration:write`. A batch of `DeskIntervalStats`. Intervals start on a whole minute. Intervals are one minute: `intervalSeconds` must be 60. `transactionsProcessed` counts approaches (a family of four is one); `documentsProcessed` counts documents (the same family is four).

```json
{
  "items": [
    {
      "siteCode": "DMO",
      "deskCode": "IN07",
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
}
```

### POST /api/v1/integration/sites/{siteCode}/immigration/egate-interval-stats

Scope `immigration:write`. A batch of `EGateIntervalStats`; the categories are member names. `rejectsByCategory` uses the coarse categories `Other`, `DocumentRead`, `BiometricCapture`, `Eligibility`, `ReferredToOfficer`, `Technical`; its values sum to `rejected`. Any category with fewer than 3 rejects in the interval must be folded into `Other`, so no individual outcome can be inferred. In this example two rejects of different kinds both go to `Other`.

```json
{
  "items": [
    {
      "siteCode": "DMO",
      "gateCode": "EGIN3",
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
}
```

### POST /api/v1/integration/sites/{siteCode}/immigration/inbound-lane-demand

Scope `immigration:write`. A batch of `InboundFlightLaneDemand`, computed from API data. `passengersByLane` covers the manual lanes (`CIT`, `RES`, `VIS`, `CRW`); `eGateEligible` is separate. `boardedTotal` need not equal the sum (transfers and crew). The lanes and `eGateEligible` add up to at most `boardedTotal`. The latest `computedAtUtc` wins; an older computation is unchanged. The example is the reference split for 200 passengers.

```json
{
  "items": [
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
}
```

The four immigration contracts are the same records AMAN publishes on Kafka (`Ariva.Business.Contracts.Aman.V1`), so a non-AMAN immigration system integrates by sending the same JSON. They carry no person, document or officer identifiers, and the API rejects unknown fields.

The immigration endpoints work like the flight batches above (ARV-048): `{ "messageTimeUtc"?, "items": [ 1 to 500 records ] }`, at most 1 MB, `Idempotency-Key` required, read strictly, and 200 with `{ received, applied, unchanged, refused, items: [{ index, key, applied, errors, warnings }] }` (`key` is the desk, gate or flight; null when the code does not have the desk code mapping shape, so a refused value is never echoed). Each record names the site of the path (another site is refused); its desk or gate code is the system's own and is resolved through the site's AMAN desk code mappings, counting only a mapping whose desk is at the same site (an unmapped code is kept apart with a warning, never guessed). A record is kept once per site by its `sourceEventId`, so a retry, a redelivery or the same record over Kafka and REST is `unchanged`. The contracts' times are `DateTimeOffset`, so each carries its offset (`Z`, or `+00:00` as .NET writes it); a time without one is refused rather than read in the server's zone. Times lie between 7 days ago and a few minutes after Ariva's clock (`Border:Feed:AheadMinutes`, 5 by default); intervals are 60 seconds on a whole minute; counts add up (accepted and rejected to attempts, categories to rejected, transactions at most documents); a category other than `Other` holds 0 or at least 3. JSON property casing follows the OpenAPI document (camelCase shown here).

### POST /api/v1/integration/sites/{siteCode}/aodb/aidx

Scope `flights:write`. Body: one IATA AIDX `IATA_AIDX_FlightLegNotifRQ` message in the AIDX namespace `http://www.iata.org/IATA/2007/00`, `Content-Type: application/xml` or `text/xml`, up to 5 MB and 500 `FlightLeg` elements. The root `TimeStamp` is required (UTC, ending in `Z`): it is the message time, and a message sent again carries its original `TimeStamp`, so it is no newer than what it would overwrite and changes nothing. `Idempotency-Key` is therefore optional here: send one when your system can, and a retry gets the first answer.

How Ariva reads it:

- No DOCTYPE is accepted (so no external entity, no entity expansion and no external DTD), no resolver is used, processing instructions and comments are ignored, and nesting deeper than 32 levels is refused.
- The message is validated against Ariva's AIDX 22.1 profile: every element Ariva reads is typed (codes of at most 16 letters, digits or `. / _ -`, flight numbers of 1 to 4 digits, `OriginDate` a date, `OperationTime` a date and time with `OperationQualifier` and `TimeType`, `Resource` with `DepartureOrArrival`), and everything else (including `TPA_Extension`) is let through unread. A message that breaks the profile is refused whole with its line and position. Validating against IATA's own XSDs as well, for a customer licensed for them: To confirm per deployment.
- The root `TimeStamp` (required; UTC, ending in `Z`; at most 5 minutes ahead of Ariva's clock and 30 days behind it) is the message time.

How a `FlightLeg` maps (a leg Ariva cannot place is refused on its own; the rest go through the rules above):

| AIDX | Ariva |
|---|---|
| `LegIdentifier/ArrivalAirport` is an airport of the site (IATA or ICAO code of an airport with a terminal in the site) | `direction: Arrival` |
| `LegIdentifier/DepartureAirport` is an airport of the site | `direction: Departure`; a leg touching none or both of the site's airports is refused |
| `Airline`, `FlightNumber`, `OperationalSuffix`, `OriginDate` | `carrier`, `number`, `suffix`; the flight key is `RJ111-20261003-A` (airline, number, suffix, origin date, direction), the same key the JSON examples use, so the same flight from both routes is one leg |
| `DepartureAirport`, `ArrivalAirport` | `origin`, `destination` |
| `OperationTime` `ONB`/`SCT` (arrival) or `OFB`/`SCT` (departure); `TDN` or `TKO` when there is no block time | `scheduledUtc` |
| `ONB`/`EST` (arrival) or `OFB`/`EST` (departure); `TDN` or `TKO` when there is no block time | `estimatedUtc` |
| `TDN`/`ACT` (arrival), `TKO`/`ACT` (departure) | `actualUtc` (landed, airborne) |
| `ONB`/`ACT` (arrival), `OFB`/`ACT` (departure) | `onBlockUtc`, `offBlockUtc` |
| `AirportResources/Resource` of the site's side: `AircraftTerminal`, `AircraftParkingPosition`, `PassengerGate` (`Usage="Actual"` wins over `Planned`) | `terminal`, `stand`, `gate` |
| `AircraftInfo/AircraftType` | `aircraftType` |
| `CodeShareInfo/Airline` and `FlightNumber` | `codeshares` |
| `OperationalStatus` `DX` (cancelled) or `DV` (diverted), PADIS code set 2005 | `status: Cancelled` or `Diverted`; other codes leave the status to the times |

When an `OperationTime` of the same qualifier and type appears more than once, the last one counts. A time without its zone, or with an offset other than `Z`, is refused for its field (the leg is refused with the reason). Check-in counters are not read from AIDX; send them to `allocations/batch`.

The answer is an `IATA_AIDX_FlightLegRS` acknowledgement in the OTA pattern, `application/xml`:

```xml
<IATA_AIDX_FlightLegRS xmlns="http://www.iata.org/IATA/2007/00" Version="22.1" TimeStamp="2026-10-03T09:00:01Z" TransactionIdentifier="T-42">
  <Success/>
  <Warnings>
    <Warning RecordID="1" Status="Refused" ShortText="The leg must arrive at or depart from exactly one airport of this site."/>
    <Warning RecordID="2" Status="Unchanged" ShortText="Nothing newer than what is known."/>
  </Warnings>
</IATA_AIDX_FlightLegRS>
```

`RecordID` is the leg's position in the message, from 0; legs without a warning were applied. Your `TransactionIdentifier` is echoed when it is a plain identifier (letters, digits, `. _ : -`). Whether your AODB needs another acknowledgement shape: To confirm during onboarding. A message Ariva cannot read at all is a 400 problem (`application/problem+json`), 413 beyond 5 MB, 415 for anything but XML, 429 beyond the client's allowance or the AIDX messages a replica takes at once (2, with 4 waiting). Note: D5 records that SITA's AIDX API supports the 21.2 schema; the version each AODB sends is To confirm during onboarding (the elements Ariva reads are the same in 21.2).

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
| AIDX 22.1 inbound | Phase 0 (ARV-044); the AODB emulator pushes it (ARV-029) | Primary AODB adapter; flight legs, times, resources and status (section 8) |
| ACRIS flight API pull | Phase 0 (ARV-045); the AODB emulator serves it (ARV-029) | Ariva polls an `AcrisFlights` outbound endpoint every 30 s or more with `If-Modified-Since`; see ACRIS below. ACRIS Passenger Wait Times API v1.6.0 is the reference for Ariva's outbound wait-times publishing (v1) |
| SSIM chapter 7 import | Phase 0 (ARV-046) | File upload through Ariva.Api.Main at `api/v1/admin/sites/{siteCode}/flight-schedules` (preview, then import; up to 20 MB, streamed). Seasonal schedule fallback and day-one pilots without a live feed; see SSIM below |
| AMAN Kafka feed | Phase 0 against the simulator | Same contracts as the REST immigration endpoints |
| Vendor AODB REST (SITA, Amadeus, others) | Per project | A mapping onto `FlightLeg` once API access and documentation are granted; mocked until then |

Flight data rules: the layer keeps a canonical flight id map (diversions, renumbering, codeshares), applies messages by their own timestamps, and raises a stale-feed alarm when a heartbeat or expected update is missing. Agree the expected update cadence with each AODB.

ACRIS flights (ARV-045): your pull path answers `200` with a JSON array of flights (or `{ "flights": [...] }`, at most 5,000), or `304` when nothing changed since Ariva's `If-Modified-Since`; send `Last-Modified`. Ariva reads these members of each flight and ignores the others: `flightNumber { airlineCode, trackNumber, suffix }`, `originDate` (date), `departureAirport`, `arrivalAirport`, `departure` and `arrival` (each `{ scheduled, estimated, actual, block, terminal, gate, stand }`, times in UTC ending in `Z`; `actual` is take-off or touchdown, `block` off-block or on-block), `aircraftType`, `flightStatus` (`Cancelled` or `Diverted` carry meaning; others leave the status to the times) and `codeShares` (`[{ airlineCode, trackNumber }]`). The side of the site, the key (`EK501-20261003-A`, leading zeros of the number dropped) and the checks are as for AIDX. These member names follow the ACRIS Semantic Model flight resource; the profile your AODB serves: To confirm during onboarding.

SSIM (ARV-046): an administrator uploads the airline's chapter 7 file for a site (multipart field `file`, at most 20 MB; `horizonDays` 1 to 200, default 60) to `.../flight-schedules/preview`, checks the answer (leg records read and touching the site, the legs they expand to, a sample, and the lines Ariva could not read by number), then sends the same file and horizon with the preview's `previewToken` to `.../import`. The token binds the file's SHA-256, the site, the horizon, the window and the user, and lasts two hours; any difference is refused (409) and nothing is imported. Ariva reads record 2's time mode (U or L with the record's UTC variations) and these fields of each record 3: suffix (2), airline (3-5), number (6-9), period of operation (15-21 and 22-28, `ddMMMyy`, `00XXX00` open), days of operation (29-35), frequency rate (36; 2 is every other week), departure station and passenger time (37-39, 40-43), departure variation (48-52), departure terminal (53-54), arrival station and passenger time (55-57, 62-65), arrival variation (66-70), arrival terminal (71-72) and aircraft type (73-75). The arrival is on the first day after the departure that makes it later (the date variation field is not read). Legs touching none of the site's airports are skipped. Lines must be printable ASCII of at most 200 characters (a CR before the LF is allowed); a file has at most 200,000 lines and expands to at most 100,000 legs for the site. Every expanded leg must pass the flight intake's own rules (designators, numbers, terminals, aircraft types, times): a record that breaks them is a line error with Ariva's reason, never in the preview's counts or sample. The schedule is a fallback feed (`ssim`, which never goes stale): it creates legs and updates only legs whose schedule a schedule file set (`flight_leg.schedule_fallback`); a leg any live feed has reported is counted `unchanged` and never touched, whatever the message times, and a live feed always replaces a schedule's values, even with an older message. Legs are applied in units of 500, each in its own transaction; a unit that fails is rolled back whole, and once the first unit is committed the import runs to its end even if the caller disconnects. Every import, completed or failed, is audited (`FlightSchedule.Imported`) in a transaction of its own with the outcome, the units committed and the counts.

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
- Ariva.Api.Integration consumes the four topics (ARV-048), each with its own consumer group (`ariva-integration.aman-desk-sessions` and so on) and the Ariva consume pipe (retries, inbox, dead letter). Values are read strictly: a value that is not the contract (an unknown member, an enum that is not an exact member name, a time without an offset, malformed JSON) goes to `ariva.<topic>.dlq.v1` with its raw bytes. A record the rules refuse (the same rules as the REST endpoints) is logged with Ariva's reasons, counted and acknowledged. Records are stored as the feed `aman-kafka`, for the site the record names, which must be an Ariva site (AMAN site codes are Ariva site codes; To confirm per deployment).
- Consumption is idempotent by `SourceEventId` per site, across both transports; interval records with any length other than 60 seconds are rejected.
- AMAN codes are resolved against the site's AMAN desk code mappings; unknown codes are kept apart (stored with no desk, logged once per code and site, at most 1,000 codes per site, counted in `ariva.border.feed.unmapped`), never guessed. The demo airport's seed maps AMAN's IN, OUT, EGIN and EGOUT codes to its desks and e-gates.
- The feed's heartbeat is watched: when it stops, desk state falls back to sensor zones and service rates to their last known values, flagged.
- In a shared cluster, Ariva's consumer has read ACLs on `aman.feed.` and write ACLs only on `ariva.`.
- Where AMAN's Kafka is not shared, Ariva pulls the same four contracts from AMAN's Integration API instead (ARV-050): an administrator registers an `AmanFeed` outbound endpoint for the site with AMAN's client id, secret and TOTP seed (Administration guide); Ariva exchanges them for a token (one exchange per TOTP step, the token kept until a minute before it expires), sends `X-TOTP-Code` on every call (always, for an AMAN pull), asks `GET {feed path}{contract}?after={position}&limit=500` for `desk-sessions`, `desk-interval-stats`, `egate-interval-stats` and `inbound-lane-demand`, and keeps its position per contract. The records go through the same checks and mapping as the Kafka values (feed `aman-` and the endpoint's code); a record received over both is kept once. AMAN's paging is To confirm against AMAN's Integration API (the mock AMAN of the simulator serves it this way).
- Versioning: additive changes within V1 bump `ContractVersion.Current` (currently `1.0`); breaking changes go to `Aman/V2` and run in parallel until AMAN moves over. A data-boundary test fails the build if a contract property looks like a person or officer identifier.

### Contract tests with AMAN (Pact)

Ariva is the consumer of AMAN's feed, so it states what it needs as a Pact message contract (ARV-068) and AMAN verifies its producers against it before it releases.

- The pact: `Ariva-AMAN.json`, Pact specification V4, consumer `Ariva`, provider `AMAN`, one message interaction per V1 contract (`a desk session change`, `a desk's one-minute statistics`, `an e-gate's one-minute statistics`, `an inbound flight's lane demand`), each with the `kafkaTopic` and `contentType` it travels with. The unit tests write it to `.verify/pacts/` and every CI run publishes it as the `aman-pact` artifact. Each example is first read by Ariva's strict reader and intake rules, so the pact never asks for a record Ariva would refuse.
- What it matches: codes, site codes and source event ids by Ariva's patterns; times as ISO 8601 with an offset (interval starts on a whole minute); `state` as `Opened`, `Closed` or `Paused` by name; `laneCategory` as `CIT`, `RES`, `VIS` or `CRW` (empty when a desk closes); counts as integers, seconds as numbers; `intervalSeconds` exactly 60; `rejectsByCategory` and `passengersByLane` as maps with any of their keys, each value an integer.
- What Pact cannot say: it lets a provider add members, and it matches a pattern against the text of a number or a boolean (a `deskCode` of `101` passes the code pattern). Ariva does neither (such a record is dead-lettered) and the data boundary forbids identifiers, so the pact's metadata states the rule (`arivaDataBoundary`, with `arivaSmallCellSuppression` and `arivaContract` for the version) and the harness below adds a shape check: each member a producer sends must be one the pact's example names, with the example's JSON type. Ariva's own tests also check that no member of the pact is named like a person, officer, traveller or document identifier.
- What the pact leaves to Ariva's intake rules (a record that breaks them is logged, counted and dropped, not stored): ranges (counts 0 to 10,000, passengers 0 to 1,000, seconds 0 to 3,600), sums (accepted and rejected make attempts, the categories make rejected, transactions at most documents, lanes and e-gate eligible at most boarded), small-cell suppression, times within 7 days, and a closed desk's empty lane. AMAN's own tests cover those; the reasons are in `ImmigrationRules`.

**Provider harness for AMAN's pipeline** (.NET, PactNet 5.0.1; AMAN serialises as it publishes: camelCase, enums by name):

```csharp
using var verifier = new PactVerifier("AMAN", new PactVerifierConfig { LogLevel = PactLogLevel.Warn });
verifier
    // PactNet 5.0.1 needs an HTTP endpoint declared first for message-only verification; nothing is sent to it.
    .WithHttpEndpoint(new Uri("http://localhost:9"))
    .WithMessages(scenarios =>
    {
        scenarios.Add("a desk session change", b => b
            .WithMetadata(new { contentType = "application/json", kafkaTopic = "aman.feed.desk-session-changed.v1" })
            .WithContent(() => producer.BuildDeskSessionChanged(sampleSession)));
        // the same for the other three contracts, from AMAN's real producers
    }, amanWireJsonOptions)
    .WithFileSource(new FileInfo("Ariva-AMAN.json"))
    .Verify(); // throws PactFailureException with the mismatches

// Shape (the data boundary and the JSON types): only the members the pact's example names, each with the example's type.
using var pact = JsonDocument.Parse(File.ReadAllText("Ariva-AMAN.json"));
foreach (var (description, message) in producedSamples)
{
    var example = pact.RootElement.GetProperty("interactions").EnumerateArray()
        .Single(i => i.GetProperty("description").GetString() == description).GetProperty("contents").GetProperty("content");
    foreach (var member in JsonSerializer.SerializeToElement(message, amanWireJsonOptions).EnumerateObject())
    {
        if (!example.TryGetProperty(member.Name, out var expected))
            throw new InvalidOperationException($"{description}: {member.Name} is not a member of the contract");
        if (member.Value.ValueKind != expected.ValueKind)
            throw new InvalidOperationException($"{description}: {member.Name} is {member.Value.ValueKind}, the contract has {expected.ValueKind}");
    }
}
```

- Run it with `PACT_DO_NOT_TRACK=true` in the process environment: the Pact FFI otherwise reports usage to Pact's analytics (Ariva sets it in `Ariva.UnitTests.runsettings` and `ci.yml`, and its pact tests refuse to run without it).
- Verify with samples that cover each case: open, paused and closed desks, an e-gate with rejects and one without (an empty map), several lane mixes.
- Ariva runs this same harness against the simulator's AMAN in every build (`AmanFeedPactTests`), together with broken messages that must fail it (a wrong enum spelling, a time without an offset or with Arabic-Indic digits, a count as text, another interval length, a missing member, an unknown reject category) and messages Pact accepts that the shape check refuses (an added identifier, a code as a number, an id as a boolean); every sample that passes is also read by Ariva's strict reader.
- Dispose the verifier only when PactNet's messaging thread cannot be between two requests. PactNet 5.0.1 serves the messages from a thread of its own that loops on `while (server.IsListening) server.GetContext()` and catches only `HttpListenerException`; disposing the verifier just after that thread has logged a message as sent can make `GetContext` throw `InvalidOperationException` or `ObjectDisposedException` there, which ends the whole process (a test run aborts with "Test host process crashed" and no failed test). The thread also writes to the configured outputters. `AmanFeedPactTests` uses an outputter that locks every write and holds that thread in its "Successfully simulated message" line until the verifier is disposed; a harness that runs many verifications in one process should do the same.
- A change to a V1 contract changes the pact in the same pull request; AMAN verifies the new pact before either side releases. Until a Pact Broker is agreed with AMAN, the pact travels as the CI artifact or from the repository's build.

### All Kafka topics (AsyncAPI)

Every topic Ariva produces or consumes, with AMAN's feed, is described in AsyncAPI 3 in [`docs/architecture/asyncapi.yaml`](../docs/architecture/asyncapi.yaml) (ARV-067): channels with their partitions, cleanup policy and retention, each message's JSON Schema, its Kafka key and headers, producers and consumer groups, and the dead-letter topics. It is generated from the code (topic constants, retention catalog and message types) and checked in CI by a unit test and the AsyncAPI CLI validator; browse it with the AsyncAPI extension for VS Code or a local AsyncAPI Studio (it holds no secret, but there is no need to paste it into a third-party site). Reserved topics are declared and provisioned with nothing on them yet. After changing a topic or an event, regenerate both with `ARIVA_UPDATE_ASYNCAPI=1 dotnet test Platform/Backplane/Ariva.UnitTests --filter AsyncApiTests` and check the result with `CI=true npx --yes @asyncapi/cli@6.2.0 validate docs/architecture/asyncapi.yaml --fail-severity warn` (`CI=true` keeps the CLI's usage metrics off; `npm_config_ignore_scripts=true` as in CI skips install scripts). The table below is generated from the same model.

<!-- asyncapi-topics:start (generated by AsyncApiTests, do not edit) -->
| Topic | Payload | Key | Producer | Consumer groups | Retention |
|---|---|---|---|---|---|
| `ariva.device.track-sample.v1` | `TrackSampleBatch` | &lt;site&gt;/&lt;zone&gt; | api-ingest | `ariva-stream.queue-engine`, `ariva-stream.sensing-archive-tracks` | 3 days |
| `ariva.device.vendor-line-crossing.v1` | `VendorLineCrossingBatch` | &lt;site&gt;/&lt;zone&gt; | api-ingest | `ariva-stream.queue-engine`, `ariva-stream.sensing-archive-crossings` | 3 days |
| `ariva.device.zone-occupancy.v1` | `ZoneOccupancyBatch` | &lt;site&gt;/&lt;zone&gt; | api-ingest | `ariva-stream.queue-engine`, `ariva-stream.sensing-archive-occupancy` | 3 days |
| `ariva.device.interval-count.v1` | `IntervalCountBatch` | &lt;site&gt;/&lt;zone&gt; | api-ingest | `ariva-stream.queue-engine`, `ariva-stream.sensing-archive-intervals` | 3 days |
| `ariva.device.health.v1` | `DeviceHealthReported` | &lt;site&gt;/&lt;zone&gt; | api-ingest | `ariva-stream.queue-engine`, `ariva-stream.health-archive`, `ariva-main.device-health` | 3 days |
| `ariva.device.registry-changed.v1` | `DeviceRegistryChanged` | device id, compacted | api-main | none yet | compacted |
| `ariva.device.zone-health.v1` | `ZoneHealthChanged` | &lt;site&gt;/&lt;zone&gt;, compacted | api-main | none yet | compacted |
| `ariva.topology.zone-profile-activated.v1` | `ZoneProfilePublished` | site code, compacted | api-main | none yet | compacted |
| `ariva.topology.desk-changed.v1` |  |  |  | reserved | compacted |
| `ariva.flow.zone-crossing.v1` |  |  |  | reserved | 3 days |
| `ariva.flow.queue-interval.v1` |  |  |  | reserved | 30 days |
| `ariva.flow.nowcast.v1` |  |  |  | reserved | compacted |
| `ariva.flow.overflow-detected.v1` | `OverflowDetected` | &lt;site&gt;/&lt;zone&gt; | api-stream | none yet | 14 days |
| `ariva.desk.signal.v1` |  |  |  | reserved | 3 days |
| `ariva.desk.state-changed.v1` |  |  |  | reserved | compacted |
| `ariva.desk.interval-closed.v1` |  |  |  | reserved | 30 days |
| `ariva.border.service-rate-updated.v1` |  |  |  | reserved | 14 days |
| `ariva.border.egate-outcome-rate-updated.v1` |  |  |  | reserved | 14 days |
| `ariva.border.lane-demand-updated.v1` |  |  |  | reserved | 14 days |
| `ariva.flight.flight-changed.v1` | `FlightChanged` | &lt;site&gt;/&lt;flight key&gt; | api-integration, api-main | none yet | 14 days |
| `ariva.forecast.published.v1` |  |  |  | reserved | 30 days |
| `ariva.forecast.arrival-wave.v1` |  |  |  | reserved | 30 days |
| `ariva.forecast.staffing-recommendation-issued.v1` |  |  |  | reserved | 30 days |
| `ariva.alert.state-changed.v1` |  |  |  | reserved | 30 days |
| `ariva.sla.breach-detected.v1` |  |  |  | reserved | 30 days |
| `ariva.sla.evaluation-finalised.v1` |  |  |  | reserved | 30 days |
| `ariva.sla.dispute-changed.v1` |  |  |  | reserved | 30 days |
| `ariva.feed.border-lane-kpi.v1` |  |  |  | reserved | 14 days |
| `aman.feed.desk-session-changed.v1` | `DeskSessionChanged` | desk code | AMAN | `ariva-integration.aman-desk-sessions` | 14 days |
| `aman.feed.desk-interval-stats.v1` | `DeskIntervalStats` | desk code | AMAN | `ariva-integration.aman-desk-intervals` | 14 days |
| `aman.feed.egate-interval-stats.v1` | `EGateIntervalStats` | gate code | AMAN | `ariva-integration.aman-egate-intervals` | 14 days |
| `aman.feed.inbound-flight-lane-demand.v1` | `InboundFlightLaneDemand` | flight key | AMAN | `ariva-integration.aman-lane-demand` | 14 days |
<!-- asyncapi-topics:end -->

## 11. Outbound connections

Ariva calls other systems only through `OutboundEndpoint` records created by administrators with step-up MFA: base URL, allowed CIDRs, TLS settings (optional client certificate, optional pinned CA), timeout, retry and circuit breaker policy, and one authentication handler.

| Handler | Behaviour |
|---|---|
| `TotpClientCredentials` | AMAN style: posts client id, secret and a fresh TOTP code to the remote auth endpoint (at most once per TOTP step), caches the token until 60 seconds before expiry, sends `X-TOTP-Code` per request when required. This is the AMAN connector (the `AmanFeed` pull, ARV-050) |
| `OAuth2ClientCredentials` | Standard token endpoint, scopes, cached token |
| `ApiKeyHeader` | Named header with a secret value |
| `HmacSignature` | Signs method, path, timestamp and body SHA-256 with a shared key |
| `MutualTls` | Client certificate only |

URLs are never taken from a caller at request time, redirects are not followed, HTTPS is required (HTTP only for allowlisted lab hosts), and the resolved IP must fall inside the endpoint's allowed CIDRs.

What this means for the system Ariva calls (ARV-045):

- Give the administrator the networks your service's addresses are in (CIDR, at most 16). Every address your host name resolves to must be inside them, or Ariva refuses to connect; a redirect is never followed, so serve the final URL.
- Present a certificate the system roots trust, or give the administrator your CA (PEM) to pin; the host name must match the certificate.
- API key: Ariva sends the key in the header you name (not `Host`, `Cookie` or another transport header). HMAC: verify `X-Ariva-Signature` as base64 of HMAC-SHA256 over `METHOD\npath?query\ntimestamp\nsha256-hex-of-body` with the shared key, and reject a `X-Ariva-Timestamp` more than a few minutes off. OAuth 2.0: Ariva uses client credentials with HTTP Basic client authentication at the token path you give (on the same host). AMAN style: Ariva posts `{ clientId, clientSecret, totpCode }` and expects `{ accessToken, expiresAt }`.
- Ariva retries GET calls on 408, 429, 5xx and timeouts (with backoff, honouring a short Retry-After), stops calling for a while after repeated failures, and reads at most 10 MB of an answer.

## 12. Errors and rate limits

| Status | When | What to do |
|---|---|---|
| 200 | Batch processed; check `refused` and each item's `errors` | Fix and resend the refused items as a new batch with a new `Idempotency-Key` |
| 200 with `Idempotent-Replayed: true` | A retry of a batch already processed | Nothing to do; the answer is the original one |
| 400 | Not a batch: malformed JSON, unknown or repeated member, wrong type, JSON deeper than 8 levels, no or more than 500 items, a bad or missing `Idempotency-Key`, a `messageTimeUtc` out of range; malformed AIDX | Fix the payload; do not retry unchanged |
| 415 | Not `application/json` | Send JSON |
| 422 | The `Idempotency-Key` was used in the last 24 hours for another request | Use a new key for new data |
| 429 | Too many batches from your client in a minute, or too many batches arriving at once | Wait (`Retry-After` when given), then retry with the same `Idempotency-Key`; nothing was applied |
| 401 `invalid_client` | Any token exchange failure, including rate limiting and lockout | Check credentials, clock and source IP; back off |
| 401 | Missing or expired token | Re-authenticate |
| 403 | Scope not granted, or site not bound to the client | Ask the administrator; do not retry |
| 413 | Body larger than the limit | Split the batch |
| 5xx | Server or dependency failure | Retry with the same `Idempotency-Key` and exponential backoff |

A missing or wrong `X-TOTP-Code` on a data call, for a client whose policy needs one, is 401. Batch endpoints have the per-client and at-once limits above, on top of the per-address limit of every Ariva API.

## 13. Test environment

`Ariva.Simulation.Api` provides:

- A sensor emulator (ARV-028) that plays the reference day to Ingest as registered devices, at up to 60 times real time, with start, pause, speed and jump controls. Its demo clock drives the emulators below, so they all describe the same minute.
- A mock AODB (ARV-029): AIDX 22.1 notifications pushed to Ariva with its own integration client (every leg when a run starts, then the legs whose estimate or block time changed), and an ACRIS flight API (`aodb/acris/flights`, API key, `If-Modified-Since`) for Ariva's ACRIS pull.
- A mock AMAN (ARV-029): desk sessions, desk and e-gate intervals and inbound lane demand from the reference day, with AMAN's own desk and e-gate codes, on the `aman.feed.*.v1` Kafka topics and through the immigration REST endpoints with its own TOTP client; and a mock AMAN Integration API (`aman/api/v1/auth`, `aman/api/v1/feed/{contract}`) so Ariva's outbound `TotpClientCredentials` handler is exercised.
- A mock immigration system (ARV-029) that sends the same contracts through the generic REST endpoints with its own client (departure immigration by default).
- Validation observers (ARV-104i): from the scenario's truth, manual counts, tracer runs and desk logs sent through Ariva.Api.Main's capture API as Validation observer accounts (the normal sign-in, a TOTP code for an account with an authenticator), with optional injected error, to rehearse a validation campaign before any site work (`api/v1/simulation/validation`; the runbook is in [Commissioning and calibration](07-Commissioning-and-Calibration.md) section 8).

`api/v1/simulation/feeds` shows the emulators' status, lists AMAN's codes with the Ariva desks they stand for, plays one minute at once and loads the integration clients Ariva issued (operator key). `Platform/Simulation/Ariva.Simulation.Api/Emulators/README.md` describes what each sends.

The reference scenario is the seeded day at the fictional Demo International Airport (site code `DMO`, seed 9303). The simulator also plays the illustrative AUH Terminal A arrivals evening (site `AUH-TA`, seed 9304, ARV-139b) on the same clock: its emulated AODB publishes airport `AUH`, terminal `A`, to site `AUH-TA` with a client of its own (`aodbSites` in `PUT api/v1/simulation/feeds/clients`, ACRIS at `aodb/sites/AUH-TA/acris/flights`), and AMAN does not play it (the site has no AMAN codes; its counters are sensor-derived). Dalil issues test clients on the dev or demo environment (`https://api-integration-dev-ariva.dalilhub.tech`, simulator at `https://simulation-dev-ariva.dalilhub.tech`). Ariva's end-to-end tests (`Platform/Testing/Ariva.E2E`) cover the token exchange, TOTP replay rejection, scope and site enforcement, idempotency and every endpoint above; use them as the reference behaviour.

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
