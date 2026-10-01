# Phase 1 and v1 outline (to be expanded into prd-phase1.json after the pilot contract)

Phase 1, pilot MVP (gate: pilot contract signed; target Apr to Sep or Dec 2027; developer plus field engineer):
- Certify one stereo family (Xovis PC2 or PC3 at the pilot hall) on hardware: conformance kit from real payloads, firmware range, MQTT transport.
- Validation campaign tooling: manual count sessions on a tablet, accuracy computation per zone profile version, calibration records, acceptance report.
- Pilot site configuration: real topology, floor plans, zone profile, devices, display boards.
- Security hardening for accreditation: penetration test fixes, ZAP findings closed, secrets rotation runbook, backup and restore drills, OIDC option (Keycloak or Entra ID) if the authority requires it.
- Operations: SigNoz dashboards and alerts on the system itself, runbook rehearsals, on-call contacts.
- Evidence pack v0 (signed interval data, zone profile version, calibration record, exclusions, content hash).
- Pilot KPI report comparing forecast error with and without AMAN inputs (the commercial proof).

v1, first commercial sale (Oct 2027 to Jun 2028; two developers):
- Forecast and staffing: show-up curves, lane-mix forecast from API counts, Monte Carlo P50 and P90, recommendations against roster, roster overrides.
- Check-in and handlers: counter allocation, handler views, SLA per bin.
- SLA and penalty engine: contracts, exclusions, evaluations, disputes, evidence packs, penalty statements.
- Multi-terminal, second sensor family (a LiDAR perception platform), scheduled reports for all roles.

v2 (from H2 2028): what-if simulation, multi-airport tenancy, offline bundle and licensing, HA for the stream engine across sites.
