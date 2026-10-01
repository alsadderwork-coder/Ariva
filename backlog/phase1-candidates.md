# Phase 1 and v1 candidates from the October 2026 competitive refresh

Source: [docs/product/competitive-refresh-2026-10.md](../docs/product/competitive-refresh-2026-10.md). Phase 0 stories stay as they are; these are candidates for the PO to rank at the Phase 1 planning, not commitments. "Covered" means an existing story already delivers the capability; "extend" means a Phase 0 story gets an extra acceptance criterion; "new" is a candidate story.

| Capability | Status | Where | Module |
|---|---|---|---|
| Border-system fusion (desk sessions, e-gate decisions, document categories with sensed queues) | Covered | ARV-048, ARV-049 (aggregate-only AMAN contracts; officer analytics stay in AMAN) | Border |
| Sovereign on-premises and air-gapped deployment | Covered | ARV-062; add an offline install bundle in ARV-064 | Both |
| Sensor-agnostic ingestion | Covered | ARV-021, ARV-023, ARV-024 | Both |
| Two-way AODB and A-CDM integration | Covered | ARV-043 to ARV-047 | Airport |
| Audit-grade golden replay | Extend | ARV-036: hash-chain the replay inputs and outputs so an export is tamper-evident | Both |
| Multilingual passenger displays | Extend | ARV-058: Arabic (right to left), English, Portuguese and Swahili resource files | Both |
| Predicted SLA breach 15 to 60 minutes ahead | Extend | ARV-038 and ARV-047: rule type on the arrival-wave projection | Both |
| Wait time by traveller category | New ARV-077 | Queue segments per zone (citizen, GCC, visa on arrival, e-gate eligible, crew, diplomatic) fed by AMAN categories and lane signage | Border |
| E-gate and manual desk balancing | New ARV-078 | Recommend moving eligible travellers between e-gates and desks from both queues and gate availability | Border |
| Overflow orchestration | New ARV-079 | One action that opens lanes, proposes officer moves and updates signage, with an audit entry | Both |
| What-if simulation of process changes | New ARV-080 | Scenario engine runs against a changed process (enrolment time, gate count, visa rule) and compares SLAs | Border |
| Signed SLA evidence pack | New ARV-081 | Per-counter attribution with timestamps exported as a signed bundle for airline and handler penalties | Airport |
| Per-flight show-up forecast with published accuracy | New ARV-082 | D-60 to the day of operations, accuracy tracked and shown | Airport |
| Officer roster and break recommendations | New ARV-083 | Lane-opening plan from the forecast under border staffing rules | Border |
| Accuracy assurance | New ARV-084 | Ground-truth sampling tool and published error bands per zone (pilot validation) | Both |
| National multi-site command view | New ARV-085 | Cross-site dashboard for border authorities and airport groups, built on site scoping (ARV-012) | Both |
| Hybrid mode without sensors | New ARV-086 | Wait estimates from border-system logs and kiosk or ticket timing for halls without sensors | Border |
| Edge store-and-forward | New ARV-087 | Ingest keeps working on a poor link and forwards when it returns | Both |
| Public wait-time API with confidence bands | New ARV-088 | Anonymous, cached, rate-limited feed for apps and websites | Airport |
| Transfer connection risk | New ARV-089 | Flag transfer passengers whose predicted wait threatens the connection | Airport |
| Mobile supervisor app with push alerts | New ARV-090 | PWA with push for alerts and the live view | Both |
| Privacy compliance pack | New ARV-091 | Data inventory, retention controls and mapping to UAE and Saudi PDPL, Kenya, Nigeria, Angola and Tanzania law | Both |
| Virtual queuing (booked slots) | New ARV-092, low priority | Booked security slots; not a border feature | Airport |
