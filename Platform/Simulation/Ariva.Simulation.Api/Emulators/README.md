# Emulators

The emulated partners of the demo day (ADR-0025). Every emulator plays the shared demo clock: the sensor emulator's (`api/v1/simulation/sensors`: start, pause, speed, jump, stop minute). Each time a demo minute completes, the sensors push it to Ingest and every feed emulator (`IDemoMinuteSink`) plays the same minute, so sensors, the AODB and AMAN describe one moment. The feeds lay the demo day on real minutes (`FeedTime`): the minute a run starts or jumps to is the current wall minute and later minutes follow one minute apart, so at speed 1 the feeds are on real time and faster runs move ahead of it. Each feed emulator plays its minutes in order on its own queue, so a slow partner never holds the sensors back; a feed more than 120 minutes behind loses its oldest queued minutes (counted as `clockMinutesDropped` in the feeds status and logged once per run). A manual `play` re-anchors the feeds when it does not follow the last minute played, so mixing it with a running clock gives the same flights on other dates.

| Folder or file | What it holds |
|---|---|
| `Sensors/` | The sensor emulator and the demo clock (ARV-028) |
| `Aman/AmanFeed.cs` | The AMAN feed of a demo minute (ARV-029): desk sessions, one-minute desk and e-gate intervals, inbound lane demand, from the scenario, deterministic for a seed; AMAN's own desk and e-gate codes (`AmanCodes`) |
| `Aman/AmanTransports.cs` | The four contracts' topics and paths, the AMAN Kafka producer and the buffer behind the mock AMAN feed API |
| `Aman/ImmigrationFeedEmulator.cs` | An emulated border system: AMAN (Kafka, REST, buffer) or the mock immigration system (REST only) |
| `Aodb/AodbSchedule.cs` | The site's schedule as an AODB knows it at a demo minute, as AIDX 22.1 notifications and ACRIS flights |
| `Aodb/AodbEmulator.cs` | Pushes AIDX to Ariva and keeps the ACRIS snapshot Ariva pulls |
| `Integration/` | RFC 6238 TOTP and the emulated systems' client of Ariva's Integration API (token exchange, `X-TOTP-Code`, `Idempotency-Key`) |
| `MockPartners.cs` | The mock AMAN's token service and the authentication of the mock partners (AMAN token, AODB API key) |
| `FeedSettings.cs`, `FeedEmulatorExtensions.cs` | `Simulation:Ariva`, `Simulation:Aman`, `Simulation:Immigration`, `Simulation:Aodb`, validated at start; registration |

## What each emulator sends

- **AMAN** (`Simulation:Aman`, both border sides by default): a desk session change when a scenario desk opens, pauses or closes (the first minute of a run states every desk with a session); each desk with a session reports its one-minute interval (documents are people, transactions are approaches of 1.25 on average); each e-gate in service reports attempts, accepted and rejected with rejects by coarse category, any category under 3 folded into `Other`; each inbound flight's lane demand two hours out (booked) and 30 minutes out (boarded), split by the carrier's mix, 40 % of citizens and residents e-gate eligible, transfers excluded. Codes are AMAN's own (`IN01` to `IN22`, `EGIN1` to `EGIN6`, `OUT01` to `OUT22`, `EGOUT1` to `EGOUT4`; `GET api/v1/simulation/feeds/aman-codes` lists the Ariva desk each stands for), so Ariva's AMAN desk code mappings are exercised. Records go to Kafka (`aman.feed.*.v1`, keyed by desk, gate or flight, JSON as Ariva reads it, acks from all replicas) when `Simulation:Aman:Kafka:BootstrapServers` is set (SASL credentials only with `SaslSsl`, and `Plaintext` or `SaslPlaintext` only to loopback or in-cluster brokers), and to Ariva's immigration endpoints with AMAN's integration client when it is set.
- **Mock immigration system** (`Simulation:Immigration`, departures by default): the same contracts through the generic REST endpoints with its own client and no Kafka, as a non-AMAN system integrates.
- **AODB** (`Simulation:Aodb`): the scenario's arrivals and departures at the site's airport (other airport, stand, gate and aircraft fixed per flight code), each leg's estimate published an hour before and its block time when it happens. AIDX notifications to `api/v1/integration/sites/{site}/aodb/aidx`: every leg at a run's first minute, then the legs that changed. ACRIS: `GET aodb/acris/flights` with the API key Ariva's outbound endpoint presents, `Last-Modified` and 304 on `If-Modified-Since`.

REST calls go to `Simulation:Ariva:IntegrationUrl` for the Ariva site `Simulation:Ariva:SiteCode`: an immigration contract as `{ "items": [...] }` (at most 500) to `api/v1/integration/sites/{site}/immigration/{desk-sessions|desk-interval-stats|egate-interval-stats|inbound-lane-demand}` with an `Idempotency-Key` of the seed, date, minute and contract, so a replayed minute is a replay for Ariva. The clients exchange their credentials at `api/v1/auth` with a TOTP code from a newer step each time (Ariva's replay guard), keep the token until 60 seconds before it expires and send `X-TOTP-Code` on every call when their policy asks.

## Mock AMAN Integration API

For Ariva's outbound AMAN connector (`TotpClientCredentials`, ARV-050):

- `POST aman/api/v1/auth` with `{ clientId, clientSecret, totpCode }` answers `{ accessToken, expiresAt, sessionId }` (AMAN's shape, no-store). Every failure is the same 401 `{"error":"invalid_client"}`; a TOTP step is accepted once per client; `Simulation:Aman:Mock:AuthPerMinute` exchanges per address per minute (429 beyond). Clients are configured in `Simulation:Aman:Mock:Clients`: `ClientId`, `SecretSha256` (never the secret), `TotpSecret` (Base32) and `PerRequestTotp`.
- `GET aman/api/v1/feed/{contract}?after=&limit=` with the token (and a current `X-TOTP-Code` when the client's policy asks) answers what the emulated AMAN published after a sequence number, oldest first, at most 500, in AMAN's `Result` envelope `{ data: { items, next }, hasErrors, errorMessages }`. The real AMAN API's paths and paging are To confirm.

## Endpoints

Under `api/v1/simulation/feeds`, behind an operator key: `GET` (read) for each emulator's status; `GET aman-codes` (read); `POST play` with `minute` (control, 60 a minute per key) plays one minute on the feeds at once, for tests and demos that need a given moment; `PUT clients` (control) with `aman`, `immigration` and `aodb` credentials (`clientId`, `clientSecret`, `totpSecret`, `perRequestTotp`) as Ariva issued them. Secrets go in and never come back out.
