# Support and maintenance

How Ariva is supported once live: support tiers and response targets, maintenance windows, the firmware policy for sensors, and the split of responsibilities between the customer, the local integration partner and Dalil Tech.

The tiers and targets below are a template. Every commercial figure is To agree in each customer's support contract; nothing here is a promise.

## 1. Support model

| Line | Who | Handles |
|---|---|---|
| First line | Customer operations and IT (supervisors, duty managers, site IT, site administrator) | User questions, password and TOTP resets, display players, network and time source on the customer side, first triage with the [Troubleshooting and FAQ](17-Troubleshooting-and-FAQ.md) |
| Second line, field | Local integration partner | Sensor hardware, mounting, cabling, PoE switches, device-side configuration, on-site replacement, firmware updates under this policy |
| Second line, software | Dalil support | Ariva software, configuration, data-quality investigations, integrations, Kubernetes deployment, upgrades |
| Third line | Dalil engineering | Defects, fixes, releases, recomputation of disputed periods, security incidents |

Remote access for Dalil is only as the customer allows (for example a customer-approved VPN), and Dalil's support tooling collects health telemetry only (device status, lag, error rates), never operational data.

## 2. Severity levels and response targets (template)

| Severity | Definition | Response target | Restoration target |
|---|---|---|---|
| 1 Critical | Live monitoring unavailable for a whole site or hall; data loss risk; security incident | To agree | To agree |
| 2 High | A process (for example arrivals immigration) degraded or unknown; AMAN or AODB feed down; displays stale across a terminal | To agree | To agree |
| 3 Medium | A single sensor or display down with the zone still measured; a report failing; one integration client failing | To agree | To agree |
| 4 Low | Questions, cosmetic issues, change requests | To agree | Next release or as agreed |

| Support tier (template) | Coverage | Typical customer |
|---|---|---|
| Standard | Business hours of the customer's time zone, remote | Small sites |
| Extended | Extended hours, remote, with on-call for severity 1 | Standard sites |
| 24x7 | Round the clock for severity 1 and 2, with on-site partner response | Hubs and penalty-grade sites |

Service targets proposed in the architecture (to agree per contract, not promises): live monitoring available 99.5 percent of each month at a standard three-node site; no loss of committed interval data; recovery from a single node failure within 15 minutes. A small site runs with reduced availability, accepted in writing.

Commercial basis in the business plan (assumptions, to agree per contract): annual support and updates at 18 percent of the licence from year 2; hardware maintenance at 5 percent of hardware; an optional penalty-grade assurance service.

## 3. Raising a ticket

Include: site and deployment (border or airport), severity, time in UTC and local time, affected zones, desks, devices or integration clients, what users see (screenshot if allowed), and what has been checked. For anything affecting SLA bins, record the start and end times: they decide exclusions and disputes.

## 4. Maintenance windows

| Activity | Window | Notes |
|---|---|---|
| Ariva upgrades | Agreed low-traffic window (for example overnight outside the night bank) | Every apply restarts all pods; live screens may show the neutral message briefly |
| Kubernetes and node patching | Agreed window, one node at a time on a three-node site | Kafka keeps two in-sync replicas; Stream reloads checkpoints |
| Database maintenance and restore tests | Agreed window | On a copy wherever possible |
| Sensor work (mounting, cabling, firmware) | Night or low-traffic windows set by the airport | Coordinate with operations: the zone will be degraded; log the period for SLA exclusions |
| AMAN-side changes | The border client's change-control calendar | Ask for the calendar early; it sits on the critical path |

Announce windows to the customer's operations, the local partner and, where handlers are evaluated, to the handlers, so planned outages are excluded rather than disputed. Suppression windows on alert rules silence planned events.

## 5. Sensor firmware policy

1. Ariva certifies each sensor family for a firmware range ([Sensor catalogue and adapters](09-Sensor-Catalogue-and-Adapters.md)). Only firmware inside the certified range goes to production devices.
2. Vendor security advisories are reviewed by Dalil and the local partner when published. A critical security fix may be applied outside the range only with Dalil's agreement and a re-validation plan.
3. New firmware is first replayed through the family's conformance kit with recorded payloads, then tested on one device on site, then rolled out.
4. Roll out per zone in a maintenance window, never across a whole process during operating hours.
5. After an update, check health, clock offset and counts; a firmware update is a re-calibration trigger, and penalty-grade zones are re-validated.
6. Record the firmware version per device in the asset register and in Ariva's device record.
7. Vendor cloud connectivity stays disabled at government sites whatever the firmware enables by default.

## 6. Responsibilities

| Area | Customer | Local integration partner | Dalil Tech |
|---|---|---|---|
| Site survey | Access, plans, escorts | Performs with the field engineer | Field engineer, design review |
| Sensor design and BOQ | Approves | Input on installability | Designs coverage and BOQ |
| Sensor supply | Procurement and customs (per contract) | Import, customs, delivery (per contract) | Recommends families; partner agreements with vendors |
| Installation, cabling, PoE, labelling, signage | Permits, work windows, power | Performs; delivers the as-built pack | Reviews the as-built pack |
| Commissioning and calibration | Staff for manual counts and observer logs | Device-side fixes | Owns commissioning and the validation campaign |
| Kubernetes cluster, storage, network, DNS, TLS, NTP | Provides and operates (unless contracted otherwise) | | Specifies requirements; deploys Ariva |
| Ariva software, upgrades, configuration support | Approves changes and windows | | Delivers and supports |
| Sensor hardware maintenance and replacement | Spares policy (per contract) | Performs | Re-calibration support |
| Sensor firmware | Approves windows | Applies within the certified range | Certifies ranges; reviews advisories |
| AMAN feed | Border client's change control | | AMAN-side changes (separate AMAN work items) |
| AODB feed | Airport IT provides access and data | | Builds and supports the adapter |
| Backups and restore tests | Operates per the agreed model | | Defines the procedure; supports restores |
| Security | Cluster hardening, network policies, identity provider, penetration test approval | Device credentials and switch security (802.1X) | Product security, fixes, SBOM, penetration test before go-live |
| Privacy | Controller duties, DPIA, notices, authorisations | Signage installation | DPIA template, privacy-by-design evidence |
| SLA disputes (v1) | Terminal duty manager decides | | Evidence packs, recomputation on request |
| Monitoring | Watches dashboards and alarms during operations | | Monitors health telemetry where allowed |

## 7. Contacts

| Contact | Details |
|---|---|
| Dalil support desk | To be added |
| Dalil escalation (product owner) | To be added |
| Local integration partner | To be added per site |
| Customer operations | To be added per site |
| Customer IT | To be added per site |
