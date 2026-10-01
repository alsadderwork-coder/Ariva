# Ariva wiki

Ariva is Dalil Tech's airport queue management system. Overhead sensors measure people anonymously from above; Ariva turns their tracks into queue lengths, waits and desk states, predicts the wait for someone joining now, alerts supervisors before a queue breaks its target, and (from v1) evaluates handler service levels and penalties from versioned, signed definitions. It runs on the customer's premises and never needs a WAN for live measurement.

This wiki is the operating manual: how Ariva works, how it is installed, integrated, run and supported. The reviewed design lives in `docs/` in the same repository; where a wiki page and `docs/` disagree, `docs/` wins and the wiki page is corrected.

## Modules

| Module | Buyer | Covers | First phase |
|---|---|---|---|
| Border module | Border authorities | Arrival and departure immigration halls, manual desks, e-gates, the aggregate feed from AMAN where AMAN runs, arrival-wave alerts | MVP (pilot at one AMAN arrivals hall) |
| Airport Operations module | Airport operators (ground handlers are users, not buyers) | Check-in islands, security lanes, handler tenancy, AODB integration, handler SLA and penalty engine | v1 |

Both modules run on the same core (measurement, queue engine, alerting, displays, reports). Each is enabled per deployment by a signed, offline-verifiable licence file. A border authority and an airport operator at the same airport each get their own deployment; the border side may push lane-level aggregates one way to the airport side. See [Product overview](01-Product-Overview.md).

## Who should read what

| Reader | Start with |
|---|---|
| Dalil engineers | [Architecture](03-Architecture.md), [Testing strategy](16-Testing-Strategy.md), [Release notes and versioning](18-Release-Notes-and-Versioning.md) |
| Dalil DevOps | [Deployment guide](04-Deployment-Guide.md), [Network and ports](05-Network-and-Ports.md), [Operations runbook](10-Operations-Runbook.md), [Security guide](13-Security-Guide.md) |
| Local integration partner (sensor installer) | [Sensor installation guide](06-Sensor-Installation-Guide.md), [Commissioning and calibration](07-Commissioning-and-Calibration.md), [Network and ports](05-Network-and-Ports.md), [Support and maintenance](19-Support-and-Maintenance.md) |
| AODB and immigration system integrators | [Integration guide](08-Integration-Guide.md) |
| Airport and border authority technical committees | [Product overview](01-Product-Overview.md), [Business flow](02-Business-Flow.md), [KPI and SLA definitions](15-KPI-and-SLA-Definitions.md), [Privacy and data protection](14-Privacy-and-Data-Protection.md), [Security guide](13-Security-Guide.md) |
| Support staff | [Operations runbook](10-Operations-Runbook.md), [Troubleshooting and FAQ](17-Troubleshooting-and-FAQ.md), [Administration guide](11-Administration-Guide.md) |
| Supervisors and duty managers | [User guide](12-User-Guide.md) |

## Pages

| Page | What it covers |
|---|---|
| [01 Product overview](01-Product-Overview.md) | Modules, roles, screens, capability tiers, what Ariva does not do |
| [02 Business flow](02-Business-Flow.md) | Site lifecycle, live operations loop, arrival wave, SLA and penalties, integration token exchange |
| [03 Architecture](03-Architecture.md) | Hosts, ports, data stores, Kafka topics, ADR index |
| [04 Deployment guide](04-Deployment-Guide.md) | On-premises Kubernetes install, sizing, configuration, bootstrap, upgrades, backup, local development |
| [05 Network and ports](05-Network-and-Ports.md) | Firewall matrix, time synchronisation, TLS, segmentation, bandwidth |
| [06 Sensor installation guide](06-Sensor-Installation-Guide.md) | Site survey, device choice, coverage, mounting, cabling, signage, safety, handover |
| [07 Commissioning and calibration](07-Commissioning-and-Calibration.md) | Registering devices, zones and lines, profile versions, validation campaign, re-calibration |
| [08 Integration guide](08-Integration-Guide.md) | Integration API v1, TOTP, examples, AIDX, ACRIS, SSIM, AMAN feed, certification |
| [09 Sensor catalogue and adapters](09-Sensor-Catalogue-and-Adapters.md) | Device families, adapter matrix, adding a family, certification levels |
| [10 Operations runbook](10-Operations-Runbook.md) | Monitoring, system alerts, incident procedures |
| [11 Administration guide](11-Administration-Guide.md) | Users, roles, TOTP, integration clients, devices, profiles, rules, displays, audit, retention |
| [12 User guide](12-User-Guide.md) | Screens per role and common tasks |
| [13 Security guide](13-Security-Guide.md) | Controls, authentication, authorisation, secrets, hardening, security gates |
| [14 Privacy and data protection](14-Privacy-and-Data-Protection.md) | Anonymity by design, retention, DPIA outline, signage, laws in scope |
| [15 KPI and SLA definitions](15-KPI-and-SLA-Definitions.md) | Every KPI, how it is computed, provisional and final, exclusions, evidence packs |
| [16 Testing strategy](16-Testing-Strategy.md) | Test layers and how to run them |
| [17 Troubleshooting and FAQ](17-Troubleshooting-and-FAQ.md) | Symptom, cause, check, fix |
| [18 Release notes and versioning](18-Release-Notes-and-Versioning.md) | Version rules and release history |
| [19 Support and maintenance](19-Support-and-Maintenance.md) | Support tiers, maintenance windows, firmware policy, responsibilities |
| [20 Glossary](20-Glossary.md) | The ubiquitous language in short |

## Product status (1 October 2026)

Ariva is in Phase 0 (demo core), which runs from October 2026 to about April 2027 on simulated and lab data. Phase 0 builds only what needs no site. The pilot (Phase 1) starts when a pilot contract is signed.

| Area | Exists in the repository today | Planned |
|---|---|---|
| Solution | .NET 10 solution mirroring AMAN's layout; every host builds and answers the three health probes | Domain, persistence and messaging stories in Phase 0 |
| Hosts | Ariva.Api.Main, Ingest, Stream, Cronz, Integration and Ariva.Simulation.Api start and serve `/health/startup`, `/health/readiness`, `/health/liveness` only | Controllers, workers, adapters and emulators |
| Web | SvelteKit 2 skeleton with Arabic and English language files; nginx image serving `/healthz` | Dashboard, display page (Phase 0), all other screens (Phase 1 and v1) |
| Contracts | AMAN feed contracts V1 (`DeskSessionChanged`, `DeskIntervalStats`, `EGateIntervalStats`, `InboundFlightLaneDemand`) | AMAN-side outbox changes (Phase 1) |
| Kubernetes | Helm chart `ariva-platform` (seven deployments, services, HPAs, ingresses), `helmfile-k8s.yaml`, TimescaleDB chart placeholder (`installed: false`) | TimescaleDB templates, Kafka and Redis releases or reuse of AMAN's |
| Pipelines | PR validation, one image build per service, a fan-out build, release to `k8s-dev` | Demo and production release pipelines, security pipelines |
| Security gates | Repository scanner (39 rules over 14 CWEs), .NET security analyzers as errors, layering and data-boundary tests, `scripts/verify.mjs` | Behaviour tests, end-to-end suite `Platform/Testing/Ariva.E2E` (not yet in the repository), ZAP scan |
| Prototype | A clickable prototype on synthetic data (fictional Demo International Airport, DMO, seed 9303) shows the target screens. It is not the product | |

Phases and gates (dates are a scenario that moves with the pilot contract):

| Phase or gate | Dates (scenario) | Scope headline |
|---|---|---|
| Phase 0: demo core | Oct 2026 to Apr 2027 | Queue engine, zones from a configuration file, simulator and lab sensors, minimal dashboard, display page |
| Gate 1: pilot contract signed | Apr 2027 | One AMAN arrivals hall |
| Phase 1: pilot MVP | Apr to Sep or Dec 2027 | AMAN desk and gate data, zone editor, alerting, reports, authentication, validation campaign |
| Gate 2: pilot accepted | Sep to Dec 2027 | Acceptance criteria met |
| v1: first sale | Oct 2027 to Jun 2028 | Airport Operations module, SLA and penalty engine, forecasting and staffing, AODB on a real feed |
| v2: scale | From H2 2028 | What-if simulation, national views, queue balancing |

## Labels used in this wiki

| Label | Meaning |
|---|---|
| Decided | Fixed in an approved design document or a later decision |
| Proposed | A design proposal in the repository, not yet confirmed |
| To confirm | Open; needs an answer from the client, a vendor, counsel or the product owner |
| Estimate | A planning figure derived from design assumptions, not a measurement |
| Reference value | A default from the simulator's reference scenario (seed 9303); not contractual and not field-validated |
| Target procedure, implemented in (phase) epic (name) | A procedure for software, files or commands that do not exist yet. It describes the intended behaviour; the epic delivers it |

## Epic names used in this wiki

The Azure DevOps backlog will hold one epic per roadmap work package. Until it exists, this wiki names epics as below; rename the references when the backlog is created.

| Phase | Epic | Roadmap work package |
|---|---|---|
| Phase 0 | Skeleton and platform | WP1: skeleton, pipelines, Helm, Kafka, PostgreSQL with TimescaleDB, observability |
| Phase 0 | Zones from configuration | WP2, Phase 0 part |
| Phase 0 | Device gateway and simulator | WP3, Phase 0 part |
| Phase 0 | Queue engine core | WP4, Phase 0 part |
| Phase 0 | Minimal dashboard | WP6, Phase 0 part |
| Phase 0 | Passenger display page | WP8, Phase 0 part |
| Phase 0 | Integration API and mocks | Standards adapters marked Phase 0 in `docs/architecture/integration.md` |
| Phase 1 | Zone editor | WP2, Phase 1 part |
| Phase 1 | Pilot sensor adapter | WP3, Phase 1 part |
| Phase 1 | Quality checks and revisions | WP4, Phase 1 part |
| Phase 1 | Desk state and AMAN contracts | WP5 |
| Phase 1 | Alerting | WP7 |
| Phase 1 | Reports and export | WP9 |
| Phase 1 | Authentication, roles and audit | WP10 |
| Phase 1 | Validation tooling | WP11 |
| Phase 1 | Hardening | WP12 |
| v1 | Offline bundle, licensing and HA | Offline install bundle, licensing, high-availability hardening |
| v1 | SLA and penalty engine | Contracts, evaluation, exclusions, evidence packs, disputes, recomputation tooling |
| v1 | Forecasting and staffing | Python worker, show-up models, Monte Carlo, staffing optimiser |
| v1 | AODB on a real feed | AIDX adapter, one vendor AODB, canonical flight id map |
| v1 | Border-to-airport feed | Lane-level aggregate feed |

## Conventions

- **Time**: every stored and exchanged instant is UTC (`DateTimeOffset`), taken from the sensor or source clock (event time), never from the time Ariva received it, unless a field says so. The local time zone shown on screens per site is To confirm.
- **Units**: waits in minutes on screens and in formulas (`TimeSpan` in code); service and cycle times in seconds in AMAN contracts; rates in passengers per minute; geometry in metres in the floor plan's local metric system; the default bin is 15 minutes.
- **Two wait numbers**: realised wait (exact, late, used for reports and penalties) and nowcast (immediate, modelled, used for screens and alerts). Never write "wait time" without saying which. See [KPI and SLA definitions](15-KPI-and-SLA-Definitions.md).
- **Terminology**: one concept, one name, as in [Glossary](20-Glossary.md) and `docs/domain/glossary.md`. British spelling follows the design documents (realised, finalised, organisation, licence).
- **Links into `docs/`**: links to design documents are repository-relative (for example `../docs/architecture/overview.md`). They open in the repository view; in the published wiki, open the same path under Repos, Files.
- **Diagrams**: Mermaid diagrams use the Azure DevOps wiki block syntax (`::: mermaid`). They render in the published wiki, not in the plain file view.
- **Source of truth**: formulas in `docs/domain/formulas.md` (cited here as F1 to F21), the AMAN boundary in `docs/domain/data-boundary.md`, decisions in `docs/architecture/adr/README.md` and `docs/product/decisions.md`.
