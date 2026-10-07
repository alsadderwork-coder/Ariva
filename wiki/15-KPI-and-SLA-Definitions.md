# KPI and SLA definitions

Every number Ariva reports, how it is computed, and how it is used in service-level evaluation. Written for technical committees, contract owners and handlers who must be able to check the numbers by hand. Exact formulas, edge cases and test cases are in `../docs/domain/formulas.md` (cited as F1 to F21); that document is authoritative.

Values marked "reference" are defaults of the simulator's reference scenario (seed 9303); they are not contractual and not field-validated. Acceptance targets are proposals to agree contractually, not industry standards.

## 1. Conventions

| Quantity | Unit |
|---|---|
| Time instants | UTC, from the sensor or source clock (event time) |
| Waits | Minutes |
| Service and cycle times | Seconds in AMAN contracts, minutes in formulas |
| Rates (throughput, arrivals) | Passengers per minute |
| Bin size | 15 minutes unless stated; per-minute bins for charts and cohorts |
| Percentiles | Arrival-weighted: every passenger counts once |

Every KPI value carries a data-quality flag (`Good`, `Degraded`, `Unknown`; the worst input wins, F11) and the zone profile version it was computed with.

## 2. KPI definitions

### Realised wait (F5)

The time one passenger waited: exit-line crossing time minus first entry-line crossing time, including time in the overflow band. Exact but known only after exit. Used for reports and penalties.

- Primary method: per track, `w = t_exit - t_entry`.
- Fallback (fragmented tracks, T1 count data, vendor crossings only): first in, first out matching of sorted entry and exit times, `w_n = x_n - e_n`, re-anchored whenever the queue is observed empty.
- Excluded: tracks with no exit (abandoned, fragmented, censored) and tracks originating behind the desk line or from staff doors. A negative wait means a clock or geometry error: the track is rejected and a data-quality event raised.
- Example: entry at 18:00:00, exit at 18:12:30 gives 12.5 minutes.

### Attribution to the entry bin (F6)

Each realised wait belongs to the bin containing its entry time. A wait that starts at 18:14:30 and ends at 18:40:00 belongs to the 18:00 bin.

### Bin statistics: mean, median, P90 and share within target (F7)

Per bin: count, mean, median, P90 (or P95), and the share of passengers who waited no more than a target T.

- Percentile rule: nearest rank. For n waits, P90 is the ceil(0.9 x n)-th smallest. Waits 1, 2, ..., 10 minutes give P90 = 9, median = 5, mean = 5.5.
- With weighted samples (per-minute cohorts, forecasts): the smallest wait at which cumulative weight reaches p of the total.
- A bin with fewer passengers than the contract minimum has no percentile (null), not zero.
- Longer windows are never averages of bin percentiles. Hourly and daily percentiles merge the underlying samples (Proposed: a fixed-width wait histogram per bin, 30-second buckets). Example: 10 passengers at 5 minutes and 2 at 20 minutes give P90 = 20 minutes; averaging per-minute P90s would wrongly give 12.5.

### P90 per bin

The arrival-weighted 90th percentile of realised waits of passengers who joined in the bin. The default SLA KPI (reference threshold: at most 15 minutes per 15-minute bin).

### Nowcast (F8)

The predicted wait for someone joining now: `W_now = (Q + 1) / mu`.

| Symbol | Meaning |
|---|---|
| Q | Queue length now (occupancy of the queue zone including the overflow band) |
| mu | Throughput: a blend of staffed-desk throughput `n_open / c` (desks idle or serving, divided by recent mean cycle time) and the measured exit rate over the last m minutes (Proposed m = 5, blend 0.5) |

- Example: 29 queuing, 6 desks open, cycle time 1.5 minutes: mu = 4.0 per minute, nowcast 7.5 minutes, displayed as "5 to 10 min".
- Fast-track lanes sharing desks use their share of the desks (merge ratio). E-gates use `(gates in service / cycle time) x (1 - reject rate)`.
- No staffed desk: the nowcast is null ("no service"), never zero or infinite.
- Displays: 5-minute bands with hysteresis (the band changes only when the nowcast moves 5 minutes from the reference value); "under 5 min" below 5; a widened band when degraded; a neutral message when stale or no service. Never a realised number.
- Accuracy target for the pilot: median error against the later realised wait within 2 minutes for waits under 20 minutes.

### Throughput

Passengers served per minute by a lane or zone: exits over the exit line per minute, reported per bin. Capacity for planning is `C = n x delta / c` for n desks staffed all interval with cycle time c (F9); 4 desks at 1.5 minutes over 15 minutes give 40 passengers.

### Desk utilisation

Share of time a desk is staffed and in use, from the desk state machine (F10). States in strict signal precedence: transaction (AMAN or common-use check-in), login and logout, staff zone occupancy, service zone occupancy; a passenger walking up is not a signal.

| State | Counts as open for throughput |
|---|---|
| `Serving` | Yes |
| `Idle` | Yes |
| `Paused` (no activity for T1, example 3 minutes) | No |
| `Closed` (logout, or staff absent beyond T2, example 10 minutes) | No |
| `Unknown` (all signals stale) | No; bins flagged `Degraded` |

Proposed reporting: staffed share = (idle + serving + paused minutes) / interval; serving share = serving minutes / staffed minutes. The exact utilisation definition used in reports is To confirm with the product owner.

Service and cycle times (from AMAN `DeskIntervalStats` at AMAN sites, sensors elsewhere): service time is transaction start to end; cycle time is start to next start and includes the walk-up gap; cycle time sets throughput. AMAN separates approaches (a family of four is one) from documents (four).

### E-gate reject rate (F12)

`r = rejects / attempts` over a window, from AMAN `EGateIntervalStats`. Each reject becomes an arrival in a manual lane: manual demand = own arrivals + r x e-gate flow (after a walk lag, reference 1 minute). Example: 100 e-gate passengers at r = 0.07 add 7 to the receiving manual lane. Which lane receives rejects is a site rule (reference: visitors). Reference reject rate 0.07.

### Overflow minutes

Minutes in a bin during which an overflow band of the queue is occupied (occupancy above zero raises `OverflowDetected`). A contract KPI option in the prototype. Ariva counts a minute when any band of the queue held anyone in it (the highest reading of the minute above zero); a minute without a band reading is not observed. This rule (TC-19) was accepted by the product owner on 2026-10-06 and is implemented since ARV-115: the stream stores each band's occupancy per minute (`overflow_minute`) and the overflow minutes per 15-minute bin (`overflow_bin_15m`). A band whose sensor stays silent beyond the 2-minute freshness window is Unknown (neither occupied nor empty) and adds no minute until it reports again. A contract may still choose another KPI.

### Abandonment and track completion (F6, F18)

- Abandonment rate: tracks that left the queue without crossing the exit line / tracks that entered.
- Track completion rate: tracks that exited / tracks that entered; target at least 90 percent. Fragmentation biases waits low.
- Conservation residual: (entries minus exits) minus the change in occupancy over an interval; should be 0.

### Forecast P50 and P90 (F15, v1)

Monte Carlo runs of the backlog recursion `Q(k+1) = max(0, Q(k) + A(k) - C(k))` with sampled show-ups, on-blocks, loads and service times (reference: 40 runs, 120-minute horizon). P50 and P90 are taken across runs per future step, weighted by arrivals for 15-minute bins. Every run records its model version and an input snapshot.

### Arrival wave (F14)

Predicted arrivals per hall or lane over now plus 5 to now plus 25 minutes, from on-blocks (8 to 15 minutes delay, spread over about 12 minutes) and lane demand. Compared with a threshold or with staffed capacity (threshold To confirm).

## 3. Provisional and final

A bin is **final** when all of these hold, and **provisional** otherwise (F6):

1. The bin has ended and the watermark has passed its end plus the lateness allowance (seconds, tuned per site).
2. Every track that entered in the bin has an outcome: exited, abandoned, fragmented, or censored (still unresolved after T_censor, Proposed 120 minutes).
3. The zone's data quality for the bin is known (an outage over an entry or exit line makes the bin `Unknown`; it still becomes final, with that flag).

A bin with censored tracks above 5 percent of its entries is flagged `Degraded` (Proposed). Reports show provisional bins marked as such. SLA evaluation uses final bins only. Late events and recomputations create a new revision of a bin; earlier revisions are kept and each records its profile version.

## 4. SLA evaluation (F17, v1)

Per contract, per zone in scope, per final bin:

```
evaluated = final and passengers >= minPax and value is not null
breach    = value > threshold            (percentile wait, overflow minutes)
          = value < target share         (share of passengers under the threshold)
excluded  = an allowed exclusion overlaps the bin, or the bin's dispute was upheld
held      = the bin's dispute is Raised or UnderReview
```

Per evaluation window (calendar month or week):

```
breaches  = evaluated bins in breach
counted   = breaches - excluded breaches - held breaches (not excluded)
penalised = max(0, counted - allowance)
penalty   = min(cap, penalised x rate per bin)
```

Examples:

| Case | Result |
|---|---|
| 10 breaches, 2 excluded, 1 held, allowance 6, rate USD 350 | Counted 7, penalised 1, penalty USD 350 |
| 40 counted, allowance 6, rate USD 350, cap USD 10,000 | Penalised 34, 34 x 350 = 11,900, penalty capped at USD 10,000 |
| A sensor outage of 4 minutes in a breached bin | Does not exclude the bin |
| A sensor outage of 6 minutes in a breached bin | Excludes the bin |

Reference contract (prototype, not contractual): P90 per 15-minute bin at most 15 minutes; minimum 1 passenger per bin; allowance 6 breached bins a month; USD 350 per further breached bin; cap USD 10,000 a month; dispute window 5 working days.

Rules: provisional breaches are shown but never counted; disputes apply to final breached bins only; when a dispute resolves, the evaluation is recomputed as a new revision; every evaluation records the contract version, the zone profile version per bin and the evidence pack hash. Penalty-grade data requires T3 tracks, or T1 counts validated against manual counts for that zone profile version.

### Overflow minutes as a penalty KPI (Proposed)

Proposed on 2026-10-07; each point is To confirm per contract (pilot list TC-78 to TC-82). Full rule: F17.

- **Who is penalised**: the ground handler operating the zone's counters, not the airline. Reports list the flights checking in during each breached bin, so the handler can settle with its airlines.
- **Which zones**: those the contract names. Immigration, emigration and e-gate zones are never in a penalty contract; they are reported and alerted only.
- **Attribution**: from the AODB counter allocations overlapping the bin. One handler on the zone's counters: the bin is theirs. Two or more handlers, an unresolved counter code, or no allocation: manual review, never split automatically.
- **Observation floor**: a bin needs at least the contract's minimum of observed minutes (Proposed 12 of 15). Below it the bin is a sensor outage, excluded and reported, never a clean bin.
- **Staffed to plan**: a breached bin is excluded when every counter of the agreed staffing plan was open.
- **A breach** is a 15-minute bin whose overflow minutes exceed the threshold; a long overflow is several breached bins.

| Case | Result |
|---|---|
| 9 overflow minutes, 15 observed, threshold 5, one handler | Breach, attributed to that handler |
| Same, 11 minutes observed, floor 12 | Excluded (sensor outage) |
| Same, counters of two handlers allocated | Manual review, not counted |
| Same, every planned counter open | Excluded (staffed to plan) |

## 5. Exclusions

Types from the prototype; the allowed set is defined per contract (To confirm):

| Type | Applies when |
|---|---|
| `SecurityDirective` | A security directive affected the process |
| `SensorOutage` | A sensor outage over the zone lasted more than 5 minutes in the bin |
| `ClosedOnAirportInstruction` | Counters were closed on the airport's instruction |
| `FlightDisruption` | A flight disruption outside the handler's control |
| `StaffedToPlan` | Every counter of the agreed staffing plan was open (Proposed, overflow minutes) |
| `UpheldDispute` | The handler's dispute on the bin was upheld |

An exclusion overlaps a bin when it starts before the bin ends and ends after the bin starts. Sensor outage exclusions are evidenced by the device health history, which is kept for the dispute window.

## 6. Disputes

| Status | Effect on the bin |
|---|---|
| `Raised` | Held: not counted |
| `UnderReview` | Held: not counted |
| `Upheld` | Excluded |
| `Rejected` | Counted |

A handler station manager raises a dispute on a final breached bin within the dispute window; the terminal duty manager decides. Recomputation of a disputed period uses raw samples under a named zone profile version and creates a new revision; the original is kept.

## 7. Evidence pack

The sealed bundle behind an evaluation, generated by Ariva.Api.Cronz:

| Content | Purpose |
|---|---|
| Interval data for the bins in scope (counts, wait statistics, status, revision, quality flags) | The measured values |
| Zone profile version (and the signed geometry) | Which lines and zones produced the numbers |
| Calibration records of the devices involved | Proof the sensors were calibrated |
| Exclusions applied | Why bins were removed |
| Disputes and their decisions | Held, upheld and rejected bins |
| Data-quality flags | Where data was degraded or unknown |
| Contract version | The terms applied |
| SHA-256 content hash | Detects any change after sealing; recorded with the evaluation |

Retention of evidence packs: at least the contract's dispute and audit periods (To confirm).

## 8. Validation metrics (F18)

| Criterion | Target (proposed) |
|---|---|
| Count accuracy per 15-minute bin, each line | At least 95 percent |
| Realised-wait absolute error | Within the larger of 1 minute or 10 percent of the true wait |
| Realised-wait bias | Within plus or minus 5 percent |
| Track completion | At least 90 percent |
| Desk-state agreement with observer log | At least 95 percent of observed minutes |
| Nowcast error against later realised wait | Median within 2 minutes for waits under 20 minutes |
| Availability during the pilot | 99 percent of operating hours |
| Ground-truth proof | Nowcast error with and without AMAN inputs, side by side; no target |

See [Commissioning and calibration](07-Commissioning-and-Calibration.md) for the campaign.

### Availability (F18, ARV-118, Proposed)

Availability = available operating minutes / operating minutes, per local day, per week (Monday to Sunday) and over a range; no value when there are no operating minutes. Proposed pending the owner and the client's KPI annex (TC-83).

- **Operating minute**: inside the site's operating hours (the site operating calendar, [Administration guide](11-Administration-Guide.md) section 2b) and outside an announced maintenance window. A window counts only when it was recorded before it started; a site without a calendar is open around the clock.
- **Available minute**: every queue zone of the site's published zone profile had a live state younger than 150 seconds that had reached the minute (trailing real time by at most 180 seconds), and its minute is stored.
- **Not available**: the ledger records why: `StaleZone` (no fresh live state), `MissingMinute` (no stored minute), `StreamLag` (the live state behind), `NoPublishedZones`, and `NotObservedLive` for minutes decided after downtime of the job host (counted as not available: what cannot be proven live is not claimed).
- **Where**: `GET api/v1/sites/{siteCode}/availability?from=yyyy-MM-dd&to=yyyy-MM-dd` (local dates, at most 92 days; border shift supervisors, terminal duty managers and administrators). Each day, week and the total give the minutes recorded, operating, available, unavailable, unobserved, in maintenance and closed, the minutes per reason and two ratios; the answer repeats the pilot target (0.99).
- **Two ratios**: `availability` leaves maintenance minutes out of both sides (maintenance is not operating time); `availabilityMaintenanceAsUnavailable` counts them as operating and not available. A site could otherwise raise the first figure by declaring maintenance in advance over hours it expects to be weak, so both are reported side by side. Until the client decides (TC-83), read the pilot result against both; the KPI annex should either cap the maintenance minutes a site may declare per month or use the stricter ratio.

Example: a day open 06:00 to 22:00 (960 operating minutes) with a 60-minute announced maintenance window has 900 operating minutes; 891 available minutes give 0.99, and 891 of 960 (0.928) as the stricter ratio.
