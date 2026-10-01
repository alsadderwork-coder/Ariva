---
name: sensor-adapters
description: Sensor support model (capability tiers, transports, dialect mappers, device registry, conformance kit) for Xovis, LiDAR perception platforms, camera counters and generic devices. Load for any Ingest work.
---
# Sensor adapters

Normative spec: docs/architecture/sensor-adapters.md (catalogue, tiers, transports, sources).

- Canonical events: LineCrossing, ZoneOccupancy, TrackPosition, IntervalCount, DeviceStatus. Everything downstream depends only on these.
- Tiers: T1 counts (wait from cumulative curves), T2 occupancy, T3 tracks (per-person wait), T4 on-device KPIs (cross-check only, never penalty evidence).
- Transports are generic and reusable; dialects are per family. Xovis PC2, PC3 and PF series share one dialect family. LiDAR enters through perception platforms (Ouster Gemini, Outsight, Seoul Robotics SENSR, Blickfeld Percept), never raw point clouds.
- Declarative mappers: restricted JSONPath or XPath subset; no expressions, no scripts (CWE-94).
- Untrusted input (CWE-501, CWE-120): schema validation, 256 KB push limit, bounded frames for TCP and UDP, max events per message, timestamp sanity against the device clock offset (EWMA of received minus event time), device-namespaced ids.
- Auth: per-device keys (hashed), optional mTLS, IP allowlist; pull adapters only call registered device addresses (CWE-918).
- Lifecycle: Commissioning until a calibration passes (default 95 percent counting accuracy), then Online; heartbeat loss sets Offline and zones Degraded.
- Conformance kit per family: samples, expected canonical events, malformed, oversized and skewed cases. Verify vendor protocol details against current vendor documentation before certifying; label anything unverified.
