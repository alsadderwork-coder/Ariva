# Pilot "to confirm" list

Dated 2026-10-06. Deliverable of ARV-065 (Phase 0 exit review).

This page lists the open questions that the pilot contract, the site survey or the client must answer before or during Phase 1. It is generated from the "To confirm" markers in `docs/` and `wiki/`. Each row states the question once, even when several pages raise it, and links every page that raises it. When an item is answered, update the source pages first, then remove the row here.

Columns:

- **Why it matters**: what the answer changes in the pilot.
- **Blocks**: `Contract` (needed to sign the pilot contract), `Survey` (needed for or settled by the site survey), `Go-live` (needed before the pilot site goes live), blank (can wait until after go-live or v1).

Page labels: `wiki/NN` is `wiki/NN-*.md`; section numbers are the page's own headings.

## 1. Border authority (client)

| ID | Question | Why it matters | Blocks | Sources |
|---|---|---|---|---|
| TC-01 | Which manual lane do e-gate rejects join at each pilot site (reference: the visitors lane, VIS)? | Manual lane demand and breach alerts | Go-live | [formulas](../domain/formulas.md) F12 (two places); [decisions](decisions.md) ARV-049; [wiki/04](../../wiki/04-Deployment-Guide.md) section 5 (`Border:EgateCoupling`) |
| TC-02 | What passenger lane mix (CRW, CIT, RES, VIS, EG shares) applies per site when AMAN sends no lane demand? | Arrival wave per lane | Go-live | [formulas](../domain/formulas.md) F14; [decisions](decisions.md) ARV-047 |
| TC-03 | What threshold should the arrival-wave alert use (fixed count or staffed capacity)? | When staffing alerts fire | Go-live | [formulas](../domain/formulas.md) F14; [wiki/02](../../wiki/02-Business-Flow.md) section C; [wiki/15](../../wiki/15-KPI-and-SLA-Definitions.md) section 2 (Arrival wave) |
| TC-04 | What sample size per line and how many tracers does the validation campaign use, as written in the pilot's KPI annex? | Accuracy acceptance criteria | Contract | [wiki/07](../../wiki/07-Commissioning-and-Calibration.md) section 8 |
| TC-05 | How does the site verify a user's identity before an administrator resets that user's MFA? | ASVS V6.4.4 control; account takeover risk | Go-live | [asvs-l2](../security/asvs-l2.md) V6.4.4; [wiki/11](../../wiki/11-Administration-Guide.md) section 3 |
| TC-06 | Is MFA mandatory for roles other than administrators at each site? | Sign-in policy | Go-live | [wiki/11](../../wiki/11-Administration-Guide.md) section 3 |
| TC-07 | How much operational data may a System administrator see beyond what configuration needs? | Role grants and data boundary | Go-live | [wiki/01](../../wiki/01-Product-Overview.md) Roles |
| TC-08 | Which local time zone do screens show per site, and is the operating day for track pseudonyms the UTC day or the site's local day? | Screen times; track id rotation rule (D4) | | [wiki/Home](../../wiki/Home.md) conventions (Time); [sensor-adapters](../architecture/sensor-adapters.md) HTTPS push ingest |
| TC-09 | After how long is a nowcast stale, so screens and boards show the fallback message? | What passengers and staff see during outages | Go-live | [formulas](../domain/formulas.md) F8; [wiki/11](../../wiki/11-Administration-Guide.md) section 9 |
| TC-10 | What transport and exact field list does the border-to-airport feed use (Proposed: lane-level waits and KPIs over HTTPS with mutual TLS)? | Only needed when both a border and an airport deployment exist | | [overview](../architecture/overview.md) section 7 (Border-to-airport feed); [data-boundary](../domain/data-boundary.md) What never crosses; [wiki/14](../../wiki/14-Privacy-and-Data-Protection.md) section 2; [wiki/05](../../wiki/05-Network-and-Ports.md) firewall matrix row 11 |
| TC-83 | For the pilot criterion "availability 99 percent of operating hours" in the KPI annex: what are each site's operating hours (weekly hours, closed days, how maintenance is announced and how far ahead), and is an available minute one where every published queue zone has a live snapshot younger than the staleness threshold and a stored minute (Proposed), with minutes Ariva could not observe live (after downtime of its job host) counted as not available? Announced maintenance: Ariva reports the ratio both with maintenance minutes left out (not operating) and counted as unavailable; the KPI annex should either cap the maintenance minutes a site may declare per month or use the stricter ratio, so that pre-declared maintenance cannot inflate the result. Which does the client choose, and with what cap? (The owner accepted the definition as built as Ariva's Proposed annex wording on 2026-10-07.) | Whether the pilot meets its availability criterion; what the site administrator must record in the operating calendar | Contract | [formulas](../domain/formulas.md) F18 (Availability during the pilot); [decisions](decisions.md) ARV-118; [wiki/15](../../wiki/15-KPI-and-SLA-Definitions.md) section 8; [wiki/11](../../wiki/11-Administration-Guide.md) section 2b |

## 2. Airport operator or AODB owner

| ID | Question | Why it matters | Blocks | Sources |
|---|---|---|---|---|
| TC-11 | Which AIDX version (21.2 or 22.1) does the AODB send, and does it need an acknowledgement shape other than `IATA_AIDX_FlightLegRS`? | Flight feed onboarding | Survey | [wiki/08](../../wiki/08-Integration-Guide.md) section 8 (AIDX, two items); [decisions](decisions.md) ARV-044 |
| TC-12 | Is the customer licensed for IATA's AIDX XSDs, and should Ariva validate against them as well? | Stricter message validation | | [decisions](decisions.md) ARV-044; [wiki/08](../../wiki/08-Integration-Guide.md) section 8 (AIDX) |
| TC-13 | Which ACRIS flight profile does the AODB serve, and do its member names match the ACRIS Semantic Model names Ariva reads? | ACRIS pull onboarding | Survey | [decisions](decisions.md) ARV-045; [wiki/08](../../wiki/08-Integration-Guide.md) section 9 |
| TC-14 | Does deriving the SSIM arrival date from the times (not the date variation field) match the airlines' real files? | Correct flight legs from schedule imports | Go-live | [decisions](decisions.md) ARV-046 |
| TC-15 | Does the AODB send security lane codes, or only check-in counter codes? | Security lane desk mapping | Survey | [wiki/11](../../wiki/11-Administration-Guide.md) section 2a |
| TC-16 | Can the existing signage estate (for example Samsung MagicInfo) show web content? | Passenger display hardware | Survey | [overview](../architecture/overview.md) section 7 (Displays); [wiki/11](../../wiki/11-Administration-Guide.md) section 9 |
| TC-17 | Which SLA exclusion types does each handler contract allow? | SLA engine rules (v1) | | [glossary](../domain/glossary.md) Exclusion; [formulas](../domain/formulas.md) F17; [wiki/02](../../wiki/02-Business-Flow.md) section D; [wiki/15](../../wiki/15-KPI-and-SLA-Definitions.md) section 5 |
| TC-18 | Who may run a recomputation of a disputed period? | Dispute process (v1) | | [wiki/02](../../wiki/02-Business-Flow.md) section D |
| TC-19 | How are overflow minutes counted (per minute with any occupancy)? Ariva implements the rule since ARV-115, accepted by the product owner on 2026-10-06: a minute counts when any band held anyone (highest reading above zero); a minute without a reading is not observed, and a band silent beyond the freshness window is Unknown. Kept here for the client's contract KPI choice | Contract KPI option | | [wiki/15](../../wiki/15-KPI-and-SLA-Definitions.md) section 2 (Overflow minutes); [formulas](../domain/formulas.md) F17; [decisions](decisions.md) ARV-115 |
| TC-78 | Is the ground handler (not the airline) the penalised party for overflow minutes, with reports listing the flights checking in during each breached bin? | Who the SLA contract is signed with and who can dispute | Contract | [formulas](../domain/formulas.md) F17; [wiki/15](../../wiki/15-KPI-and-SLA-Definitions.md) section 4 |
| TC-79 | Which queue zones are penalty-grade per contract? Border zones (immigration, emigration, e-gates) are Proposed as report and alert only. | Contract scope; zones shared by several handlers need separate snakes and overflow bands to be penalty-grade | Contract | [formulas](../domain/formulas.md) F17; [wiki/15](../../wiki/15-KPI-and-SLA-Definitions.md) section 4 |
| TC-80 | What is the overflow-minutes threshold per 15-minute bin, and the observation floor (Proposed 12 of 15 minutes) below which a bin counts as a sensor outage? | Breach rule and protection against a silent band sensor | Contract | [formulas](../domain/formulas.md) F17; [wiki/15](../../wiki/15-KPI-and-SLA-Definitions.md) section 4 |
| TC-81 | Who reviews a breached bin that cannot be attributed (two or more handlers, an unresolved counter code, no allocation), within what time, and what is the outcome when no decision is made? | Bins the engine refuses to attribute automatically | Contract | [formulas](../domain/formulas.md) F17; [wiki/15](../../wiki/15-KPI-and-SLA-Definitions.md) section 4 |
| TC-82 | Does the contract allow a staffed-to-plan exclusion, and where does the agreed staffing plan per zone and bin come from? | Flight schedule peaks not charged to a handler that staffed as agreed | Contract | [formulas](../domain/formulas.md) F17; [wiki/15](../../wiki/15-KPI-and-SLA-Definitions.md) section 4; [wiki/15](../../wiki/15-KPI-and-SLA-Definitions.md) section 5 |

## 3. AMAN team and client change control

| ID | Question | Why it matters | Blocks | Sources |
|---|---|---|---|---|
| TC-20 | Are AMAN site codes the same as Ariva site codes in each deployment? | AMAN records reach the right site | Go-live | [decisions](decisions.md) ARV-048; [wiki/08](../../wiki/08-Integration-Guide.md) section 10 |
| TC-21 | What is AMAN's lane category code list, and how does it map onto Ariva's codes? | Lane waits and lane demand | Go-live | [glossary](../domain/glossary.md) Lane category; [data-boundary](../domain/data-boundary.md) Code mapping |
| TC-22 | Does AMAN's Integration API page as the simulator's mock does (`after`, `limit`, `next`, envelope)? | AMAN pull where Kafka is not shared | Go-live | [decisions](decisions.md) ARV-050; [integration](../architecture/integration.md) Outbound connections, Implementation; [wiki/08](../../wiki/08-Integration-Guide.md) section 10 |
| TC-23 | What retention does AMAN set on the `aman.feed` topics? | Replay window after an Ariva outage | | [data-boundary](../domain/data-boundary.md) Retention; [wiki/14](../../wiki/14-Privacy-and-Data-Protection.md) section 3 |
| TC-24 | Which port and TLS settings does AMAN's Kafka expose for the `aman.feed.*` topics? | Firewall rules and certificates | Survey | [wiki/05](../../wiki/05-Network-and-Ports.md) firewall matrix row 10 |
| TC-25 | Is Kafka shared with AMAN; if so, which chart and version (Strimzi as D5 recommends, or AMAN's chart)? | Cluster design and ownership | Survey | [overview](../architecture/overview.md) section 5; [wiki/03](../../wiki/03-Architecture.md) Data stores; [wiki/04](../../wiki/04-Deployment-Guide.md) section 4 (Kafka) |
| TC-26 | Which PostgreSQL HA method (operator or Patroni) does AMAN run, so Ariva matches it and its backup procedure? | HA and backup procedure | Go-live | [overview](../architecture/overview.md) section 9; [wiki/04](../../wiki/04-Deployment-Guide.md) section 4 (PostgreSQL) and section 10 |
| TC-27 | Which Redis version is used, and is it dedicated or AMAN's? | Platform prerequisites | Go-live | [wiki/03](../../wiki/03-Architecture.md) Data stores; [wiki/04](../../wiki/04-Deployment-Guide.md) section 2 |
| TC-28 | What is the minimum Kubernetes version of the cluster baseline Ariva will run on? | Chart compatibility | Survey | [wiki/04](../../wiki/04-Deployment-Guide.md) section 2 |
| TC-29 | How are ingress certificates served: a default wildcard certificate as AMAN does, or a `tls` block per site? | HTTPS on every ingress host | Go-live | [wiki/04](../../wiki/04-Deployment-Guide.md) section 2 |

## 4. Client IT (cluster, network and identity owner)

| ID | Question | Why it matters | Blocks | Sources |
|---|---|---|---|---|
| TC-30 | Does the site use the bundled Keycloak or its own OIDC provider? | Sign-in integration work | Survey | [wiki/04](../../wiki/04-Deployment-Guide.md) section 2 |
| TC-31 | Which mail relay, port and TLS mode does the site provide for alert email? | Alert and report email | Survey | [wiki/05](../../wiki/05-Network-and-Ports.md) firewall matrix row 15 |
| TC-32 | How may Dalil support receive health telemetry (for example a customer-approved VPN)? | Support terms and remote diagnosis | Contract | [wiki/05](../../wiki/05-Network-and-Ports.md) firewall matrix row 23 |
| TC-33 | Do the lab sizing estimates hold on the site's hardware, and what node size does each profile need after the Phase 1 load test? | Hardware order | Survey | [wiki/04](../../wiki/04-Deployment-Guide.md) section 3; [wiki/05](../../wiki/05-Network-and-Ports.md) Sizing notes |
| TC-34 | What recovery point and recovery time objectives apply, and is there a secondary or cold standby site? | Service levels and cost | Contract | [wiki/04](../../wiki/04-Deployment-Guide.md) section 11 (three rows) |
| TC-35 | Does the image signature admission policy (Kyverno, or the Sigstore policy-controller) run on the customer's cluster? | Supply chain control | Go-live | [wiki/04](../../wiki/04-Deployment-Guide.md) section 6.5 |
| TC-36 | Which thresholds should the system monitoring alerts use at each site? | Operations alerts | Go-live | [wiki/10](../../wiki/10-Operations-Runbook.md) section 2 |

## 5. Sensor vendor and local partner

| ID | Question | Why it matters | Blocks | Sources |
|---|---|---|---|---|
| TC-37 | Over MQTT, is the Xovis payload the same as the push JSON, and does the gateway use an embedded or a separate broker? | Xovis MQTT adapter and network design | Survey | [sensor-adapters](../architecture/sensor-adapters.md) Overhead stereo vision; [wiki/09](../../wiki/09-Sensor-Catalogue-and-Adapters.md) Adapter matrix; [overview](../architecture/overview.md) section 7 (Sensors) |
| TC-38 | Can Xovis or the reseller supply a real live data capture and confirm units, axes, multi-sensor and HUB payloads, the push schema per firmware, counter names, success codes, retry ids and TLS details? | Xovis certification | Go-live | [sensor-adapters](../architecture/sensor-adapters.md) Xovis push format (two places) |
| TC-39 | Do the Ouster Detect key names match a recorded payload? | LiDAR certification | Go-live | [sensor-adapters](../architecture/sensor-adapters.md) Declarative mappings; [wiki/09](../../wiki/09-Sensor-Catalogue-and-Adapters.md) Adapter matrix |
| TC-40 | What device id convention does each site use (it must match the id registered in Ariva)? | Installation labels and registration | Survey | [wiki/06](../../wiki/06-Sensor-Installation-Guide.md) section 9 |
| TC-41 | Can a native reviewer per site check the Portuguese and Swahili board texts? | Passenger display wording | Go-live | [decisions](decisions.md) ARV-058 |

## 6. Dalil Tech internal (commercial or product decision)

| ID | Question | Why it matters | Blocks | Sources |
|---|---|---|---|---|
| TC-42 | Which module licenses the SLA and penalty engine? | Commercial packaging | Contract | [overview](../architecture/overview.md) section 4; [wiki/01](../../wiki/01-Product-Overview.md) Modules and Screens; [wiki/02](../../wiki/02-Business-Flow.md) section D |
| TC-43 | How is the signed licence file loaded and renewed? | Licence delivery to the site | Go-live | [wiki/04](../../wiki/04-Deployment-Guide.md) section 2; [wiki/11](../../wiki/11-Administration-Guide.md) section 12 |
| TC-44 | Which queue engine values go into the pilot: crossing debounce (Proposed 2 s), hand-over window (30 s), censoring timeout (120 min), T_stale? | Wait accuracy | Go-live | [formulas](../domain/formulas.md) F4, F6 (two places), F10; [wiki/07](../../wiki/07-Commissioning-and-Calibration.md) section 5 |
| TC-45 | Which desk utilisation definition do reports use? | Report content | | [wiki/15](../../wiki/15-KPI-and-SLA-Definitions.md) section 2 (Desk utilisation) |
| TC-46 | How should the SSIM key (UTC departure date) and the AIDX key (local date) agree, so a flight near midnight does not become two legs? | Duplicate flights | Go-live | [decisions](decisions.md) ARV-046 |
| TC-47 | How many partitions does each Kafka topic get? | Stream scaling | Go-live | [overview](../architecture/overview.md) section 5; [wiki/03](../../wiki/03-Architecture.md) Kafka topics; [wiki/04](../../wiki/04-Deployment-Guide.md) section 7.2 |
| TC-48 | How are TLS and per-service ACLs configured for Kafka (including the Ingest listener and port) and for Redis? | CWE-269 control in production | Go-live | [wiki/05](../../wiki/05-Network-and-Ports.md) firewall matrix row 7 and TLS everywhere (two rows) |
| TC-49 | How is the first System administrator created without a standing default password? | Secure bootstrap | Go-live | [wiki/04](../../wiki/04-Deployment-Guide.md) section 7.3 |
| TC-50 | Where is the Data Protection key ring persisted? | Backup of encrypted secrets | Go-live | [wiki/04](../../wiki/04-Deployment-Guide.md) section 10 |
| TC-51 | What is the procedure to republish compacted topics from the database after a Kafka loss? | Recovery procedure | | [wiki/04](../../wiki/04-Deployment-Guide.md) section 10 |
| TC-52 | Does each site get its own web build with its API URLs, or runtime configuration? | Web deployment per site | Go-live | [wiki/04](../../wiki/04-Deployment-Guide.md) section 5 |
| TC-53 | Can Cronz run more than one replica (TickerQ multi-instance behaviour)? | Cronz availability | | [wiki/04](../../wiki/04-Deployment-Guide.md) section 6.4 |
| TC-54 | Are the `v*` tag ruleset and the `main` protection in place before the first signed release? | Trust in signed images | Go-live | [wiki/04](../../wiki/04-Deployment-Guide.md) section 6.5 |
| TC-55 | Should Ariva move off ingress-nginx (retirement announced November 2025) to another controller or the Gateway API? | Long-term ingress support | | [wiki/04](../../wiki/04-Deployment-Guide.md) section 2 |
| TC-56 | How is the end-to-end suite pointed at a deployed environment? | Smoke test at go-live | Go-live | [wiki/04](../../wiki/04-Deployment-Guide.md) section 8; [wiki/16](../../wiki/16-Testing-Strategy.md) section 4 |
| TC-57 | Where does the Python forecasting worker's repository live? | v1 planning | | [overview](../architecture/overview.md) section 3; [ADR-0013](../architecture/adr/ADR-0013-python-forecasting-worker.md); [wiki/03](../../wiki/03-Architecture.md) Hosts |
| TC-58 | Confirm that Debezium is not used and the ported outbox relay stays. | Architecture record | | [overview](../architecture/overview.md) section 5 |
| TC-59 | Which folder holds relational SQL scripts (the ADR proposes `Ariva.Infra/Database/Scripts`; the repository uses `Ariva.Infra/Timescale/Scripts`)? Probably resolved; update the ADR. | Architecture record | | [ADR-0017](../architecture/adr/ADR-0017-nhibernate-and-timescale-sql-scripts.md) |
| TC-60 | When an integration client's secret or TOTP seed is rotated, can the old and new values overlap? | Integrator switch-over | | [wiki/11](../../wiki/11-Administration-Guide.md) section 4 |
| TC-61 | Should e-gates under maintenance show an `OutOfService` desk state? | Desk state model | | [glossary](../domain/glossary.md) Desk state values; [wiki/12](../../wiki/12-User-Guide.md) Reading the numbers |

## 7. Legal and privacy (counsel and the client's data protection officer)

| ID | Question | Why it matters | Blocks | Sources |
|---|---|---|---|---|
| TC-62 | How long is the contract's dispute window per site, which sets raw sensing retention (default 90 days)? | Raw data retention and disputes | Contract | [overview](../architecture/overview.md) section 6; [sensor-adapters](../architecture/sensor-adapters.md) HTTPS push ingest; [wiki/04](../../wiki/04-Deployment-Guide.md) section 5; [wiki/14](../../wiki/14-Privacy-and-Data-Protection.md) section 3 |
| TC-63 | How long are evidence packs kept (at least the dispute and audit periods)? | SLA evidence (v1) | | [data-boundary](../domain/data-boundary.md) Retention; [wiki/11](../../wiki/11-Administration-Guide.md) section 11; [wiki/14](../../wiki/14-Privacy-and-Data-Protection.md) section 3; [wiki/15](../../wiki/15-KPI-and-SLA-Definitions.md) section 7 |
| TC-64 | How long is flight data kept? | Retention schedule | | [overview](../architecture/overview.md) section 6; [data-boundary](../domain/data-boundary.md) Retention; [wiki/11](../../wiki/11-Administration-Guide.md) section 11; [wiki/14](../../wiki/14-Privacy-and-Data-Protection.md) section 3 |
| TC-65 | How long are logs and traces kept, and who may read them? | ASVS V16.1.1 logging inventory | Go-live | [data-boundary](../domain/data-boundary.md) Retention; [wiki/11](../../wiki/11-Administration-Guide.md) section 11; [wiki/14](../../wiki/14-Privacy-and-Data-Protection.md) section 3; [asvs-l2](../security/asvs-l2.md) V16.1.1 |
| TC-66 | How long are report schedules and delivery rows kept (Proposed 400 days for deliveries)? | Retention schedule | | [data-boundary](../domain/data-boundary.md) Retention; [wiki/14](../../wiki/14-Privacy-and-Data-Protection.md) section 3 |
| TC-67 | How long are staff email addresses kept in sent alert emails? | Staff personal data | | [wiki/14](../../wiki/14-Privacy-and-Data-Protection.md) section 3 |
| TC-68 | How long is the Integration API call record kept? | Audit retention | | [wiki/14](../../wiki/14-Privacy-and-Data-Protection.md) section 3 |
| TC-69 | How long are Phase 0 lab recordings kept, and what notice do recorded people get? | Lab data handling | | [data-boundary](../domain/data-boundary.md) Retention; [wiki/14](../../wiki/14-Privacy-and-Data-Protection.md) section 3 |
| TC-70 | For a UAE pilot: does the PDPL apply to the border authority and airport operators, what DPIA is required, and what is the lawful basis? | Lawful processing | Contract | [data-boundary](../domain/data-boundary.md) Privacy laws; [wiki/14](../../wiki/14-Privacy-and-Data-Protection.md) sections 4 and 5 |
| TC-71 | For an Angola pilot: which law and authority apply, and do stereo counters need the video-surveillance authorisation (LiDAR may avoid it)? | Sensor family choice | Survey | [data-boundary](../domain/data-boundary.md) Privacy laws; [wiki/14](../../wiki/14-Privacy-and-Data-Protection.md) sections 4 and 5 |
| TC-72 | For a Tanzania pilot: which act applies, and what registration or permit duties come with in-country processing? | Lawful processing | Contract | [data-boundary](../domain/data-boundary.md) Privacy laws; [wiki/14](../../wiki/14-Privacy-and-Data-Protection.md) sections 4 and 5 |
| TC-73 | When does GDPR apply (EU-based client, processor or support access), and which transfer rules follow? | Support and hosting model | | [data-boundary](../domain/data-boundary.md) Privacy laws; [wiki/14](../../wiki/14-Privacy-and-Data-Protection.md) section 4 |
| TC-74 | Does Lebanon's Law 81/2018 apply if a Lebanese deployment is pursued? | Future market | | [wiki/14](../../wiki/14-Privacy-and-Data-Protection.md) section 4 |
| TC-75 | Do ephemeral, anonymous track coordinates count as personal data in the pilot's jurisdiction, and what passenger notice and sign wording does the law require? | DPIA conclusion and signage | Contract | [data-boundary](../domain/data-boundary.md) Privacy laws; [wiki/14](../../wiki/14-Privacy-and-Data-Protection.md) sections 4, 5 and 6; [wiki/06](../../wiki/06-Sensor-Installation-Guide.md) section 8 |
| TC-76 | Are the "Ariva" trademark and domain available in the target markets? | Product name in the contract | Contract | [ADR-0015](../architecture/adr/ADR-0015-product-name-ariva.md) |
| TC-77 | If Ariva is ever hosted as SaaS, does the TimescaleDB Community licence still allow it? | Only for a SaaS offer | | [ADR-0006](../architecture/adr/ADR-0006-postgresql-timescaledb-community.md) |

## Counts

The search found 153 lines with a "To confirm" marker across 30 files under `docs/` and `wiki/` (as of 2026-10-06, before this page). 13 are not open questions and were dropped: definitions of the label (glossary, formulas, data boundary and overview introductions, wiki Home, wiki/05 legend, two wiki/14 introduction lines), prototype instructions (two), release note headings (two in wiki/18) and one ASVS row that only points to the wiki/14 retention table. The other 140 merge into 77 distinct items: 10 border authority, 9 airport operator or AODB owner, 10 AMAN team and change control, 7 client IT, 5 sensor vendor and local partner, 20 Dalil Tech internal, 16 legal and privacy. Blocks: 9 Contract, 13 Survey, 30 Go-live, 25 blank.

Added 2026-10-07 outside the search: TC-78 to TC-82 (overflow minutes as a penalty KPI, product owner request), all blocking the contract, in section 2; TC-83 (operating hours and the availability definition for the KPI annex, ARV-118), blocking the contract, in section 1.
