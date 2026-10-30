# ADR-0027: Device line coverage declared per device

- Status: Proposed
- Date: 2026-10-30
- Source: Product owner decisions of 2026-10-29 (docs/product/decisions.md, the ARV-259e split); ARV-259e4, ARV-259e5

## Context

Formulas F11 makes a queue zone's wait Unknown when an entry or exit line has no coverage. ARV-259e1 gave the queue engine line coverage, but nothing says which device covers which line, so it takes every commissioned device of a zone to cover every entry and exit line of it: a zone is Unknown only when all its devices are out. A zone whose exit is watched by one sensor and whose snake by another stays Degraded, with numbers on the screens, while its exit sensor is down. The registry knows each device's queue zone, its position and its coverage footprint, and the zone profile knows each line's geometry; neither says what a device's counts stand for.

## Decision

1. **Per device, declared.** Each device declares the entry and exit lines of its queue zone it counts: `All` (every entry and exit line of the zone), `Listed` (1 to 16 named lines) or `None` (a desk zone or band sensor that counts no queue line). A device that declares nothing counts `All`, so every device registered before the declaration existed keeps today's meaning.
2. **Checked against the profile when declared.** A listed name must be an entry or exit line of the device's queue zone in the site's published zone profile, else in the draft it was registered against; anything else is refused without repeating the name. Names are kept in ordinal order, separated by line feeds in `device.covered_lines` (script 0066), which a name can never hold (ARV-159's name rule).
3. **Geometry warns, never refuses.** A listed line the device's footprint does not reach (`Geometry.SegmentReaches`) is a warning in the answer: a vendor's footprint can be conservative, and the installer may know better.
4. **A publish warns, never refuses.** A published version that drops or renames a listed line leaves the declaration as it is: the publish answer names the devices, and the device list shows the lines the zone no longer has. A device assigned to another queue zone that listed its lines counts every line again (the names were the old zone's).
5. **Not a recommissioning.** A declaration changes which of the zone's lines a device's counts stand for, not what it measures, so it never sends the device back to commissioning. It is audited and raises `DeviceRegistryChanged` with the change `CoverageChanged`.
6. **The engine reads it later, without the registry.** ARV-259e5 stamps the declaration on each device health report in Ingest and archives it with the report, so live processing, replay and recomputation see the same declarations; the queue engine never reads the registry.

## Consequences

- The device health answer lists, per published queue zone, the entry and exit lines no commissioned device declares it counts, so a gap in the declarations shows before the engine relies on them.
- Since ARV-259e5 the engine reads them (formulas F11): Ingest stamps each health report with the registry's declaration, the archive keeps it (script 0067), and the zone snapshot carries each device's (version 15). A declared name the zone does not hold is ignored and counted (`DeclaredLineNotInZone`), never refused in Ingest; naming, in the push answer, events on lines a device does not declare is left for ARV-277 (the owner's optional item).
- Site configuration export and import do not carry declarations yet (a follow-up story).
- A wrong declaration is a configuration error that makes a zone Unknown sooner (or never); the warnings and the uncovered lines are the guard, with the audit trail to trace who declared what.

## Alternatives considered

- **Coverage derived from geometry** (a device covers the lines its footprint reaches): no configuration, but vendor footprints are approximations, a footprint over a line does not mean the device's counting logic watches it, and a geometry change would silently change coverage. Kept as the warning.
- **Coverage on the line** (each line lists its devices): the zone profile is versioned and published, and a device swap would need a new profile version. Rejected.
- **Refuse a declaration the footprint does not reach, or a publish that drops a listed line:** blocks an installer who knows better, or a profile change, on a configuration detail. Rejected by the owner (warnings).
