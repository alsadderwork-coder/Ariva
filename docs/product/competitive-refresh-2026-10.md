# Competitive refresh, October 2026

A refresh of D2 (Market and Competitors) focused on current product capabilities, from public web sources only. Items older than 2024 are marked [pre-2024]; "?" means not verified. Veovo's Passenger Predictability page and Copenhagen Optimization's Better Border page could not be fetched, so a few cells rest on press coverage. Mapping to the backlog: [backlog/phase1-candidates.md](../../backlog/phase1-candidates.md).

## What changed since D2

- Xovis launched the PF-Series sensor (February 2026, 7.9 TOPS on the sensor) and argues publicly against LiDAR (occlusion, seven-year life). Claims "up to 98%" counting, 120+ airports and 600,000+ sensors. AUH runs 455 Xovis sensors across 19 sites including immigration, with forecasting by DTP (2018) [pre-2024].
- Veovo, with DTP and SAMI, is rolling out passenger flow at 27 Saudi airports (GACA and MATARAT, announced December 2024 and January 2025): check-in, immigration, security and transfer, with prediction and alerts. Its Capacity Optimizer plans border lanes, and Queue Balancer moves transfer passengers ahead when their predicted wait would breach the SLA. Forecast accuracy claimed above 99%.
- SITA signed an MoU with Abu Dhabi Airports (December 2025) to co-develop an AI "Intelligent Total Airport Management" platform. This puts SITA inside Ariva's first target account on the airport-operator side.
- Amadeus completed the Vision-Box acquisition (April 2024): e-gates and border control, and "Free Flow" corridors. No standalone sensor-based queue product found.
- Copenhagen Optimization (InfraVia majority since early 2025) sells cloud SaaS modules (Forecast, Security, Shift, Deployment, Border, Virtual Queuing) to 40+ airports; no Middle East or Africa customer found.
- Outsight leads LiDAR software (multi-brand, Hesai and RoboSense partnerships, 15 to 30 minute forecasts, no images stored); NEC partners with it for TSA wait displays (October 2025). Quanergy launched Q-Insights for airports (December 2025).
- Camera AI on existing CCTV (Zensors with Axis at Toronto, including the CBSA hall [pre-2024]) and portable sensors with playback (Laminar) lower the hardware bill.
- Ticketing queue systems still win African immigration halls: Wavetec at Douala and Yaoundé immigration with routing by visa type (2023).
- The EU Entry/Exit System showed hour-plus waits at Copenhagen six months after launch (April 2026): process changes at the border create queues nobody models.

## Feature matrix

Y = yes, P = partial or through a partner, N = not found, ? = not verified.

| Capability | Xovis | Veovo | SITA | Amadeus/Vision-Box | Copenhagen Opt. | Outsight | Zensors | Wavetec/Qmatic |
|---|---|---|---|---|---|---|---|---|
| Measured live wait time | Y | Y | P | N | P ? | Y | Y | P (tickets) |
| Short-horizon predicted wait | Y | Y | P | P | P | Y (15 to 30 min) | Y | P |
| Per-flight forecast, D-60 to the day | ? | Y | P | P | Y | N | N | N |
| Staffing or lane-plan recommendation | P | Y | P | ? | Y | P | N | N |
| Border lanes modelled | Y | Y | P | P (e-gates) | P | N | Y | Y |
| Overflow routing via signage | ? | Y | N | N | N | P | N | P |
| Passenger displays, web, API | Y | Y | ? | N | Y | Y | Y | Y |
| Configurable alerts | Y | Y | Y | ? | ? | Y | ? | ? |
| Event replay or playback | Y | ? | N | N | N | P | N | N |
| Desk or counter attribution | Y | P | ? | N | ? | ? | N | P |
| Virtual queuing (booked slots) | N | N | N | N | Y | N | N | Y |
| Anonymised by design | Y | Y | ? | n/a | n/a | Y | P | n/a |
| Sensor-agnostic ingestion | N (own sensors) | Y | Y (partners) | ? | ? | Y (LiDAR brands) | N (CCTV) | N |
| On-premises or sovereign option | ? | ? (cloud) | ? | ? | N (cloud) | ? | ? | Y |
| Middle East or Africa reference | AUH, BLR | 27 Saudi airports | AUH MoU | e-gates | none found | none found | none | Cameroon, Emirates |

## Where Ariva can win

1. Fusing border-system records (desk sessions, e-gate decisions, document categories) with sensed queues. No competitor shows it; AMAN's aggregate contracts give Ariva the data, with officer-level analytics kept in AMAN.
2. Sovereign on-premises and air-gapped deployment. The strongest rivals are cloud SaaS; interior ministries in the UAE, Angola and Tanzania expect in-country hosting.
3. Wait times by traveller category (citizen, GCC, visa on arrival, e-gate eligible, crew, diplomatic), because border SLAs are set by category.
4. Balancing between e-gates and manual desks, with routing by eligibility.
5. Predicted SLA breaches 15 to 60 minutes ahead, anchored to arrival waves from the AODB.
6. Overflow orchestration that opens lanes, reassigns officers and updates signage in one action.
7. What-if simulation of process changes (enrolment, new e-gates, visa rules), the EES lesson.
8. Signed evidence packs for airline and handler SLA penalties, with per-counter attribution.
9. Audit-grade golden replay: deterministic and tamper-evident.
10. A hybrid mode for halls without sensors (border-system logs plus kiosk or ticket timing), where Wavetec wins today.
11. Edge operation with store-and-forward for low-bandwidth regional ports.
12. Arabic, Portuguese, Swahili and English passenger displays, right to left where needed.
13. A privacy compliance pack mapped to national law (UAE and Saudi PDPL, Kenya, Nigeria, Angola, Tanzania), not only GDPR.

## Table stakes for a credible bid

Measured live wait time with a stated accuracy; predicted wait refreshed at least every minute; wait, queue length, throughput, process time, occupancy and percentage within SLA by area and hour; configurable alerts on web and mobile; history with drill-down and export; passenger displays and a public feed; AODB schedules and per-flight show-up forecasting; staffing or lane-opening recommendations; anonymised sensing with role-based access and an audit trail; Xovis support with LiDAR increasingly expected; open REST and streaming APIs; high availability; credible regional references.

## Strategic notes

- DTP (UAE) integrated both the AUH Xovis project and the Saudi Veovo project: a likely partner or competitor in the Gulf.
- SITA's iTAM work with Abu Dhabi Airports may absorb the airport-operator queue scope at AUH, which strengthens the decision to lead there with the Border module.
- Buyers will compare Ariva's accuracy with Xovis's 98% counting claim and Veovo's 99% forecast claim, so the pilot validation campaign must publish error bands in the same terms.

## Sources

Xovis: xovis.com/solutions/airport; xovis.com press releases on the PF-Series (PTE 2026) and AI overcrowding tools (PTE 2025); International Airport Review, "Why Xovis doesn't use LiDAR"; DTP case study on Abu Dhabi Airport; Memoori on 120 airport installs. Veovo: veovo.com/platform/forecasting and /capacity-optimizer; Future Travel Experience on Keflavik Queue Balancer (2023); International Airport Review and dtp.ae on the Saudi rollout; International Airport Review on the Amorph LiDAR partnership and the Gatwick control room. SITA: sita.aero Passenger Intelligent Flow and Passenger Flow Management brochure; Intelligent CIO on the Abu Dhabi Airports MoU (December 2025); Business Wire on Honolulu biometric arrivals (October 2025); Aviation Pros on the Materna IPS acquisition. Amadeus: Aviation Pros on the Vision-Box acquisition; amadeus.com Free Flow release. Copenhagen Optimization: copenhagenoptimization.com news; InfraVia portfolio page; Aviation Pros on Montevideo virtual queuing. Outsight: outsight.ai insights pages; Access Newswire on the NEC partnership; Bordeaux trial coverage. Others: Securityworldmarket on Quanergy Q-Insights; Business Wire on Zensors at Toronto; airportlabs.com Laminar; wavetec.com case studies; Airport Technology on BA digital queuing and Hamad; Dubai Airports media on DXB queue times; Rustourismnews on EES waits at Copenhagen (April 2026). Full URLs are in the research hand-back of 2 October 2026 stored with the project.
