# Business flow

This page follows Ariva end to end: how a site goes from survey to live service, how a sensor reading becomes a number on a screen, how an arrival wave turns into a staffing alert, how handler penalties are evaluated (v1), and how an external system authenticates. Each flow has a diagram and a step table saying who does what.

## A. Site lifecycle: from survey to go-live

```mermaid
graph LR
  S1["1 Site survey"] --> S2["2 Sensor design and BOQ"]
  S2 --> S3["3 Procure and install"]
  S3 --> S4["4 Commission"]
  S4 --> S5["5 Calibrate and validate"]
  S5 --> S6["6 Publish zone profile"]
  S6 --> S7["7 Go-live"]
  S7 --> S8["8 Monitor"]
  S8 -->|"Moved sensor, layout or firmware change"| S5
```
| # | Step | Who | What happens | Output | Duration (assumption from the roadmap) |
|---|---|---|---|---|---|
| 1 | Site survey | Local integration partner with the Dalil field engineer | Measure ceilings and halls, photograph snake layouts, record obstructions, network rooms, power, time sources and access constraints | Survey report, photographs, floor plan | 1 to 3 weeks |
| 2 | Sensor design and BOQ | Dalil architect and field engineer | Choose the device family per ceiling height, plan coverage and overlap (F1 to F3), size servers and network, write the bill of quantities | Sensor layout, BOQ, network design | Part of the proposal |
| 3 | Procure and install | Procurement, local partner | Order sensors (lead time and customs), mount, cable, power over Ethernet, label, signage | As-built documentation, device list | 4 to 10 weeks order and delivery, 1 to 3 weeks installation |
| 4 | Commission | Dalil field engineer, site administrator | Register devices, issue credentials, configure output to the gateway, check time sync, calibrate the floor plan, draw zones and lines as a draft profile | Devices in `Commissioning`, draft zone profile | Calibration and commissioning 1 to 2 weeks |
| 5 | Calibrate and validate | Field engineer, client staff | Per device: counting accuracy against a manual count sample. Per site: validation campaign with manual counts, timed tracers and observer logs over at least five operating days including two peaks | Calibration records, validation report | 2 to 3 weeks |
| 6 | Publish zone profile | Site administrator (step-up MFA) | Publish the validated profile as an immutable version and activate it | Active zone profile version | Hours |
| 7 | Go-live | Client operations, Dalil | Devices `Online`, alert rules enabled, displays switched on, burn-in | Live service | Two weeks of burn-in in the pilot scenario |
| 8 | Monitor | Client operations, Dalil support, local partner | Watch data quality, device health and feeds; re-calibrate on triggers | Health and incident records | Continuous |

Notes:

- Zones are drafted during commissioning; validation runs on that draft profile version; the validated version is the one activated for production and, in v1, signed into handler contracts. Any later change is a new version, never an edit.
- A device moves to `Online` only after a passed calibration (counting accuracy of at least 95 percent by default). See [Commissioning and calibration](07-Commissioning-and-Calibration.md).
- The calendar is set by hardware, the client's change control for AMAN changes and validation fieldwork, not by software. These chains run in parallel after the contract and meet at commissioning.

## B. Live operations loop

```mermaid
sequenceDiagram
  participant S as Sensor
  participant I as Ingest gateway
  participant K as Kafka
  participant T as Stream
  participant D as TimescaleDB
  participant M as Main
  participant W as Dashboard and displays
  participant N as Integration
  S->>I: Track samples, 2 to 5 Hz, MQTT or HTTPS
  I->>K: ariva.device.track-sample.v1, keyed by zone
  K->>T: Consume per partition
  T->>T: Crossings, queue length, nowcast, desk state
  T->>D: Samples, zone events, interval results
  T->>K: ariva.flow.nowcast.v1
  K->>M: Nowcast and desk state
  M->>W: SignalR push every few seconds
  T->>T: Evaluate alert rules on the nowcast
  T->>K: ariva.alert.state-changed.v1
  K->>M: Alert shown in the alert list
  K->>N: Alert to notification channels
  N->>N: Email in the MVP, SMS and webhook in v1
```
| # | Step | Component | Detail |
|---|---|---|---|
| 1 | Sense | Sensor | Each sensor tracks people in its footprint and timestamps samples with its own synchronised clock (NTP or PTP). Health (frame rate, temperature, clock offset) every 10 to 30 seconds |
| 2 | Ingest | Ariva.Api.Ingest (device gateway, one per terminal on the sensor VLAN) | Authenticates the device, validates and size-limits the payload, maps the vendor dialect to canonical events, checks the clock offset, buffers to disk if Kafka is unreachable |
| 3 | Publish | Kafka | `ariva.device.track-sample.v1` and `ariva.device.health.v1`, keyed by the owning queue zone id so a whole process stays on one partition |
| 4 | Compute | Ariva.Api.Stream | Applies the active zone profile: line crossings (F4), occupancy and queue length, realised waits by entry bin (F5, F6), nowcast (F8), desk state (F10), overflow, data-quality flags (F11). Bins close on a watermark; late events reopen a bin as a new revision |
| 5 | Store | PostgreSQL with TimescaleDB | Raw samples and zone events by binary COPY (kept for the dispute window, 90 days by default); interval results upserted by (zone, bin start, revision) |
| 6 | Push | Ariva.Api.Main | Consumes compacted nowcast and desk-state topics and pushes them over SignalR to dashboards, filtered by the user's tenant and data scope; serves the read-only display endpoint |
| 7 | Display | Ariva.Web | Dashboards show nowcast and realised waits with their flags; passenger boards show the nowcast in 5-minute bands with hysteresis, never a realised number, and a neutral message when data is stale |
| 8 | Alert | Stream (rules), Main (list), Cronz (escalation timers), Integration (channels) | Rules evaluate nowcasts and forecasts, never realised waits. One open alert per rule and target; escalation if not acknowledged in time |

Who acts: nobody in the loop; it runs continuously. Supervisors act on what it shows (open desks, acknowledge alerts). If a sensor drops out, its zone turns `Degraded` and screens show a band instead of a number; if an entry or exit line loses coverage, the wait becomes `Unknown` and screens show a neutral message.

## C. Arrival wave: from on-block to staffing alert

Border module. In the MVP only if an AODB feed is already available; standard from v1.

```mermaid
sequenceDiagram
  participant A as AODB
  participant AM as AMAN
  participant N as Integration
  participant K as Kafka
  participant T as Stream
  participant M as Main
  participant U as Shift supervisor
  A->>N: Estimated or actual in-block
  AM->>K: aman.feed.inbound-flight-lane-demand.v1
  K->>N: Lane demand per inbound flight
  N->>K: ariva.flight.flight-changed.v1
  N->>K: ariva.border.lane-demand-updated.v1
  K->>T: Flights and lane demand
  T->>T: Predicted hall arrivals per lane
  T->>K: ariva.forecast.arrival-wave.v1
  T->>T: Sum now plus 5 to 25 min against threshold or capacity
  T->>K: ariva.alert.state-changed.v1
  K->>M: Alert and arrival-wave strip
  M->>U: Push alert, open desks before the wave
```
| # | Step | Who or what | Detail |
|---|---|---|---|
| 1 | Flight times | AODB through Ariva.Api.Integration | Estimated in-block before landing, actual in-block after. Messages are applied by their own timestamps; identity changes (diversions, renumbering, codeshares) go through the canonical flight id map |
| 2 | Lane demand | AMAN (border deployments) | `InboundFlightLaneDemand`: boarded total, passengers per manual lane (CIT, RES, VIS, CRW) and e-gate eligible, computed by AMAN from API data. Without AMAN, Ariva uses a configured lane mix (reference: CIT 0.35, RES 0.20, VIS 0.35, CRW 0.02, transfers 0.08, e-gate share 0.40 of citizens and residents) |
| 3 | Hall arrival curve | Ariva.Api.Stream | Passengers reach the hall 8 to 15 minutes after on-block (reference midpoint 11 minutes when unknown), spread over about 12 minutes with fixed reference weights (F14). E-gate rejects (reference rate 0.07) are added to the receiving manual lane (F12) |
| 4 | Wave alert | Alert rules in Stream | The sum of predicted arrivals over the window now plus 5 to now plus 25 minutes, per hall or lane, compared with a threshold or with the capacity of staffed desks over the same window (threshold To confirm) |
| 5 | Act | Border shift supervisor | Sees the alert and the wave strip (flights landing in the next 30 minutes, passengers by lane, predicted hall arrival curve) and opens desks before the wave reaches the hall |

If the AODB feed is stale, the wave alert uses the last estimates and says so, and forecasts are marked as built on stale inputs. If AMAN lane demand is missing, the configured lane mix is used and the result is flagged. A sensor surge at immigration with no matching on-block raises a data-quality alert; the wave is still visible in live measurement.

The reference scenario shows this at 18:05: a visitor-heavy arrival wave pushes the arrivals Visitors nowcast above 15 minutes and rule R-001 fires.

## D. SLA and penalties for handlers (planned for v1)

Airport Operations module; which module licenses the engine is To confirm. Exact rules are in [KPI and SLA definitions](15-KPI-and-SLA-Definitions.md) and F17.

```mermaid
graph TD
  B0["Bin result for a zone in scope"] --> B1{"Final?"}
  B1 -->|No| P1["Shown as provisional, never counted"]
  B1 -->|Yes| B2{"Minimum passengers and a value?"}
  B2 -->|No| P2["Not evaluated"]
  B2 -->|Yes| B3{"Breach of the KPI threshold?"}
  B3 -->|No| P3["Compliant bin"]
  B3 -->|Yes| B4{"Allowed exclusion overlaps the bin?"}
  B4 -->|Yes| P4["Excluded breach"]
  B4 -->|No| B5{"Dispute raised or under review?"}
  B5 -->|Yes| P5["Held, not counted"]
  B5 -->|No| P6["Counted breach"]
  P5 --> B6{"Dispute decision"}
  B6 -->|Upheld| P4
  B6 -->|Rejected| P6
  P6 --> P7["Penalty: counted minus allowance, times rate, capped"]
  P7 --> P8["Evaluation finalised, evidence pack sealed with SHA-256"]
```
| # | Step | Who | Detail |
|---|---|---|---|
| 1 | Contract | Terminal duty manager drafts and signs (step-up MFA) | Party (the handler), scope (its islands), KPI (P90 wait per bin, share under threshold, or overflow minutes), threshold, bin size, evaluation window, minimum passengers per bin, allowed exclusion types, monthly allowance, rate per breached bin, monthly cap, dispute window, signed zone profile version, effective date. Signed terms are immutable; a change is a new contract version |
| 2 | Bins | Ariva.Api.Stream | Realised waits attributed to the entry bin. A bin is provisional while anyone who entered in it is unresolved or the watermark has not passed; then final. Only final bins are evaluated |
| 3 | Exclusions | Terminal duty manager adds them; sensor outages are detected | Types from the prototype (per contract, To confirm): security directive, sensor outage over the zone for more than 5 minutes in the bin, counters closed on airport instruction, flight disruption outside the handler's control, upheld dispute |
| 4 | Evaluation | Ariva.Api.Cronz (TickerQ job) | Per window: breaches, minus excluded, minus held; allowance; penalty capped. Provisional breaches are shown but never counted |
| 5 | Dispute | Handler station manager raises; terminal duty manager decides | Within the dispute window (reference: 5 working days), on final breached bins only. Raised and Under review hold the bin; Upheld excludes it; Rejected counts it. A resolution recomputes the evaluation as a new revision |
| 6 | Recompute | Recomputation tooling (v1); who may run it is To confirm | A disputed period can be recomputed from raw samples under a named zone profile version; the result is a new revision and the original is kept |
| 7 | Evidence pack | Ariva.Api.Cronz | Interval data, zone profile version, calibration record, exclusions, disputes and quality flags, sealed with a SHA-256 content hash and recorded with the evaluation |

Reference contract values from the prototype (not contractual): P90 per 15-minute bin at most 15 minutes, minimum 1 passenger per bin, allowance 6 breached bins a month, USD 350 per further breached bin, cap USD 10,000 a month, dispute window 5 working days. The reference scenario shows Handler B's check-in island C breaching at 19:10 after a shift change leaves 5 of 12 counters open.

Commercial safeguard from the risk register: offer the penalty tier only after the zones have been validated.

## E. Integration token exchange

How an AODB, an immigration system or a sensor gateway authenticates to the Integration API. The normative specification is `../docs/architecture/integration.md`; the developer view with examples is in [Integration guide](08-Integration-Guide.md).

```mermaid
sequenceDiagram
  participant C as Integration client
  participant N as Ariva Integration API
  participant R as Replay guard
  C->>C: Compute TOTP code from the seed
  C->>N: POST /api/v1/auth with clientId, clientSecret, totpCode
  N->>N: Client active and caller IP in an allowed CIDR
  N->>N: Secret hash and TOTP code valid
  N->>R: Time step newer than the last accepted step?
  R-->>N: Yes, step recorded
  N-->>C: accessToken, expiresAt, sessionId
  C->>N: POST /api/v1/integration/sites/{siteCode}/flights/batch
  Note over C,N: Authorization Bearer token, X-TOTP-Code, Idempotency-Key
  N->>N: Scope, site binding and idempotency checks
  N-->>C: Result with per-item results
  Note over C,N: Any failed auth check returns 401 invalid_client
```
| # | Step | Who | Detail |
|---|---|---|---|
| 1 | Register | Ariva system administrator (step-up MFA) | Creates an `IntegrationClient`: name, kind (`Aodb`, `Immigration`, `SensorGateway`, `Other`), scopes, bound site codes, allowed source CIDRs, per-request TOTP policy. Ariva shows the client secret and the TOTP provisioning URI once |
| 2 | Load the seed | The integrator's operator | Loads the Base32 seed into any RFC 6238 library (SHA1, 6 digits, 30-second step) |
| 3 | Exchange | Integrator's system | `POST /api/v1/auth` with client id, secret and current code. Checks in order: client active and IP allowed; secret hash; TOTP valid within one step either side; time step newer than the last accepted one; rate limits. Every failure returns the same `401 { "error": "invalid_client" }` |
| 4 | Token | Ariva | JWT with audience `ariva-integration`, lifetime 15 minutes, claims `sub`, `sid`, `scope`, `site`. No refresh token: re-authenticate with a fresh code |
| 5 | Call | Integrator's system | `Authorization: Bearer`, `X-TOTP-Code` on every call when the client's per-request policy is on (default for immigration clients), `Idempotency-Key` on every write |
| 6 | Audit | Ariva | Every call is audited: client, scope, endpoint, site, result, payload SHA-256 |

Lockout: 5 attempts per minute per client and 20 per minute per IP; 10 consecutive failures lock the client for 15 minutes and alert the administrators.
