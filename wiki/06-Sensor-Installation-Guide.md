# Sensor installation guide

For the local integration partner and the Dalil field engineer. It covers the site survey, choosing a device family and model, coverage planning, mounting, power and cabling, privacy signage, labelling, as-built documentation, safety, and the handover to commissioning.

Ariva is software and is vendor-agnostic. The sensor vendor's installation manual is always authoritative for mounting, clearances, tilt, power and firmware. Vendor-specific figures on this page cite the vendor documents listed in `../docs/architecture/sensor-adapters.md`; anything else is marked "check the vendor datasheet".

## 1. Safety first

| Hazard | Minimum precautions |
|---|---|
| Working at height | Use mobile elevating work platforms or scaffolding suited to the ceiling height, operated by trained staff. Follow the airport's permit-to-work rules and local regulations for fall protection. Barrier an exclusion zone below the work area and tether tools |
| Live terminal | Work in agreed night or low-traffic windows. Keep passengers out of the work area with barriers and a spotter. Never block emergency exits, signage or fire equipment |
| Airside and restricted areas | Hold the required passes and escorts before the visit; tools and materials follow the airport's screening rules |
| Electrical | PoE is low voltage, but switch cabinets and ceiling services are not. Only qualified electricians work on mains circuits |
| Fire systems | Do not obstruct sprinklers, detectors or voice alarm speakers. Fire-stop every ceiling or wall penetration to local code |
| LiDAR laser emission | Check the laser class (IEC 60825-1 classification) on the vendor datasheet for the exact model, keep the label visible, and follow the vendor's instructions. Do not install a device whose class requires access controls in a public area without the authority's safety approval |
| Dropped objects | Secure brackets to structure, not to ceiling tiles, unless the vendor and the ceiling manufacturer allow it; use the vendor's safety wire where provided |

## 2. Site survey checklist

Survey before the BOQ is final. The roadmap allows 1 to 3 weeks.

| Area | Record |
|---|---|
| Halls and processes | Every queue to measure: immigration (arrivals and departures), e-gates, check-in islands, security lanes. Queue entrances, snake layout and stanchion positions, overflow areas, desk rows, e-gate banks, staff doors |
| Dimensions | Zone length and width; ceiling height at each planned sensor position; height of beams, ducts and light fittings |
| Ceiling | Construction (slab, grid, open structure), access hatches, fixing points, materials, the ceiling owner's rules |
| Obstructions | Signs, light fittings, sprinklers, HVAC outlets, columns, hanging displays, anything inside the sensor's field of view |
| Light and environment | Daylight and glare on the floor, very low light areas, outdoor or semi-outdoor areas, temperature and dust. These steer the choice between stereo and LiDAR |
| Network | Comms rooms, cable routes and distances (Ethernet channel limit 100 m), free switch ports, PoE budget, VLAN availability, 802.1X support |
| Power | Power for PoE switches; UPS availability |
| Time | Site NTP server or PTP grandmaster reachable from the sensor VLAN |
| Floor plan | CAD export or a scaled plan, and two fixed reference points visible on site for floor-plan calibration |
| Access | Work windows, permits, escorts, lift and platform availability, storage for equipment |
| Legal and privacy | Where privacy notices go; whether the jurisdiction needs an authorisation for camera-based counters (Angola: confirm whether stereo counters fall under the video-surveillance law before choosing the sensor) |
| Photographs | Ceilings and snake layouts from several angles, with a scale reference |

Pilot hall criteria from the roadmap: AMAN live; ceiling under 6 m so one wide-footprint stereo sensor covers about 100 m2; a hall small enough to cover completely (roughly up to 20 desks plus e-gates); a supervisor who wants it; a local partner able to install.

## 3. Choosing a device family and model

Rule from D4: overhead 3D stereo vision for ceilings from 2 to about 20 m; LiDAR above that, outdoors, in poor light, or where cameras need legal authorisation. One product family of each is to be certified before the pilot. Check the certification status in the [sensor catalogue](09-Sensor-Catalogue-and-Adapters.md) before ordering.

### Overhead stereo vision (Xovis)

One Ariva adapter covers the Xovis family; the model changes the mounting height and coverage. Heights from the Xovis selection guide (metric, V1.8) and the PC3 technical datasheet:

| Series | Models | Mounting height | Typical use in Ariva |
|---|---|---|---|
| PC2 series | PC2SE, PC2R, PC2R-O (outdoor) | About 2.2 to 6 m | Immigration halls, security lanes, low ceilings |
| PC2 extended coverage | PC2SE-UL, PC2SE-L, PC2R-UL, PC2R-L and outdoor variants | Extended-range variants: check the vendor datasheet | Wider halls at low heights |
| PC3 series | PC3 (6 to 14 m), PC3-L (6 to 9 m), PC3-M1, PC3-M2, PC3-H, PC3-UH (16 to 20 m), outdoor -O variants | 6 to 20 m | Check-in halls and high ceilings |
| PF series | Listed in the 2025 selection guide | Check the vendor datasheet | Check the vendor datasheet |

Xovis outputs (PC3 datasheet): data push over HTTP(S), MQTT(S), FTP(S), SFTP, TCP and UDP; a REST API; JSON with counting-line and zone logics, intervals and track data in multi-sensor setups; four privacy modes with text-only output. Multi-sensor stitching is done by the Xovis multi-sensor setup, not by Ariva. Tilt: D4 records up to 15 degrees on one axis and 5 on the other; check the vendor manual for the exact model.

### LiDAR through a perception platform

Ariva connects to a perception platform, never to raw point clouds.

| Layer | Products |
|---|---|
| Perception platform (what Ariva connects to) | Ouster Gemini, Outsight SHIFT, Seoul Robotics SENSR, Blickfeld Percept and Qb2 smart LiDAR |
| LiDAR hardware (behind the platform) | Ouster (OS0, OS1, OSDome, and Velodyne lines after the merger), Hesai (XT and JT series), RoboSense, Livox, Seyond, Blickfeld; supported if the chosen platform supports it |

Coverage for LiDAR is radius-based and depends on the platform's tracking range. The BOQ uses an assumed 10 m effective radius until a platform is certified. Mounting heights, field of view, laser class and environmental ratings: check the vendor datasheet for the chosen sensor and the platform vendor's design rules.

### Tier needed

Penalty-grade evaluation needs T3 (tracks) or T1 counts validated against manual counts. Plan queue zones that will carry SLA contracts for T3 output. See [Product overview](01-Product-Overview.md) for the tiers.

## 4. Coverage planning

### Rules

1. Continuous coverage inside a process. A snake and the desks it feeds must have no coverage gaps; tracks are handed between sensors by position, time and velocity. Ariva never re-identifies people across gaps.
2. Overlap of at least 0.3 m between neighbouring stereo sensors in Xovis multi-sensor setups (F2).
3. Every entry and exit line at least 1 m inside coverage.
4. Cover the staff zone behind each desk and the service zone in front of it when desk state comes from sensors (staff presence proves a desk is staffed; service-zone occupancy is a weak serving signal).
5. Cover the overflow band outside the snake; overflow time counts as wait.
6. LiDAR: the binding constraint is occlusion, not range. Every point of a dense queue should be seen by at least two sensors from different angles; the perception vendor's simulation sets the count (planning heuristic 2 to 4 per zone).

### Formulas

Footprint along one axis (F1), for sanity checks only; the vendor's footprint table is authoritative because it includes tracking margins:

```
W = 2 * (h - z) * tan(alpha / 2)
h = mounting height, z = tracking plane (head height for counting, 0 for floor drawings), alpha = field of view
```

Example: h = 4 m, z = 1.7 m, alpha = 90 degrees gives W = 4.6 m; at the floor (z = 0) W = 8.0 m.

Sensor count for a rectangular zone with overlap (F2); try both footprint orientations and keep the smaller:

```
N = ceil((L - o) / (a - o)) * ceil((B - o) / (b - o))
L, B = zone length and width; a, b = footprint length and width; o = overlap (at least 0.3 m)
```

D4 worked example, a 24 x 12 m snake plus a 4 m overflow band (24 x 16 m), o = 0.3 m. Footprints are D4's examples; confirm against the vendor table for the chosen model.

| Ceiling | Footprint a x b | Sensors |
|---|---|---|
| 3 m | 9.6 x 9.6 m (wide footprint) | 6 |
| 3 m | 3.75 x 2.13 m (standard) | 63 |
| 4 m | 10 x 10 m | 6 |
| 4 m | 7 x 4.25 m | 16 |
| 12 m | 12 x 9 m (high mount) | 6 |
| 18 m | 10.5 x 8 m (ultra high) | 8 |

For BOQs before a survey only, the area helper (F3): `N = ceil(A / ((L - 0.3) * (W - 0.3)) * 1.3)` with planning footprints of 10 x 10 m for 4 to 6 m ceilings and 12 x 9 m for 10 to 14 m ceilings. Reference results: check-in 1,500 m2 needs 20; departure immigration 930 m2 needs 13; arrival immigration 1,080 m2 needs 15; security 800 m2 needs 12 (the reference BOQ uses 11 by judgement).

Use the vendor's planning tool for the final layout and record the design in the as-built pack.

## 5. Mounting

1. Mount to the vendor's instructions: bracket, orientation marks, maximum tilt, clearances from lights and ducts.
2. Measure and record the actual mounting height to the floor at each sensor.
3. Align each sensor's axes with the floor plan where the vendor allows it; record the orientation.
4. Record the position in the floor plan's coordinates (metres) against the two reference points, so commissioning can place the device on the plan.
5. Keep the field of view clear; move or ask the airport to move hanging signs that intrude.
6. Leave service access: note whether a platform is needed to reach each sensor.

## 6. Power over Ethernet and cabling

| Item | Guidance |
|---|---|
| PoE class and power per sensor | Check the vendor datasheet; size switch PoE budgets with margin for every port |
| Cable | Use the cable category the vendor specifies; keep each channel within 100 m including patch cords |
| Switches | Dedicated to the sensor VLAN; 802.1X on sensor ports; spare ports for replacements |
| Power resilience | Put PoE switches on UPS where available, so sensors keep counting on board through short outages |
| Routing | Follow the airport's containment rules; segregate from mains cabling; fire-stop penetrations |
| Testing | Certify or at least continuity-test every run; record results in the cable schedule |

## 7. Device network configuration

1. Address each sensor statically or by DHCP reservation, on the sensor VLAN.
2. Point it at the site NTP server or PTP grandmaster; record the clock source.
3. Change default credentials; use HTTPS for configuration.
4. Disable vendor cloud connectivity at government sites.
5. Record the firmware version and serial number. Ariva certifies a family for a firmware range; do not update firmware outside the agreed process (see [Support and maintenance](19-Support-and-Maintenance.md)).
6. Leave the data output to Ariva for commissioning: the field engineer supplies the gateway endpoint and the per-device credential.

## 8. Privacy signage

No image leaves a stereo sensor, LiDAR captures none, and Ariva never identifies anyone. Passengers should still be told. Notice requirements per law are To confirm with counsel (see [Privacy and data protection](14-Privacy-and-Data-Protection.md)).

Proposed sign content, in the site's languages (Arabic and English in the UAE, Portuguese in Angola, Swahili and English in Tanzania):

- Overhead sensors in this area count people anonymously to measure queue times.
- No images are stored or transmitted. No one is identified.
- Operator name and a contact for questions.

Place signs at every queue entrance in the measured area, readable before people enter the measured zone. Record the sign locations in the as-built pack.

## 9. Labelling

| What | Label |
|---|---|
| Sensor | Ariva device id (for example `S-17`), zone, and the network address if site rules allow it on a visible label |
| Cable | Same id at both ends, plus the switch and port |
| Switch port | Device id |
| Asset register | Device id, model, serial number, MAC address, firmware, location |

The device id convention per site is To confirm with the site; it must match the id registered in Ariva.

## 10. As-built documentation

Deliver one pack per site before commissioning:

| Item | Content |
|---|---|
| As-built plan | Floor plan with every sensor's id, position, mounting height and orientation, the two reference points, and coverage footprints |
| Device list | Id, family, model, serial number, MAC, IP address, switch and port, VLAN, firmware version, clock source (CSV, used for registration in Ariva) |
| Cable schedule | Run ids, lengths, test results |
| Power | PoE budget per switch, UPS details |
| Photographs | Each installed sensor and its view of the floor |
| Signage | Sign locations and photos |
| Deviations | Every difference from the design and the reason |
| Open issues | Anything outstanding, with an owner and date |

## 11. Handover to commissioning

| Check | Done by |
|---|---|
| Every sensor powered, reachable from the gateway's sensor interface, and synchronised to the site time source | Local partner |
| Firmware recorded and inside the certified range | Local partner |
| Vendor multi-sensor setup completed where the vendor requires one (for example Xovis multi-sensor) | Local partner with the vendor's tool |
| As-built pack delivered | Local partner |
| Walk-through of the installation with the field engineer | Both |
| Handover signed with the open issues list | Both |

Commissioning then registers the devices in Ariva, issues credentials, draws zones and runs calibration. See [Commissioning and calibration](07-Commissioning-and-Calibration.md).
