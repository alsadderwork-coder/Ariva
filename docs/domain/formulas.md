# Ariva formulas

Every computation Ariva performs, with symbols, units, inputs, outputs, edge cases and test cases. Each formula should live as a pure function in Ariva.Core (or the Python worker for v1 forecasting) with unit tests that include the test cases below.

Sources: D4 (How It Works) and D5 (Technical Architecture), 2026-09-28. Values marked "reference" come from the prototype's simulator (the reference scenario, seed 9303) and are defaults for Ariva.Simulation.Api, not contractual or field-validated numbers. Items marked Proposed are design proposals made here where the sources state the rule but not its exact form; items marked To confirm need a decision.

## Conventions

| Quantity | Unit | Type |
|---|---|---|
| Time instants | UTC, sensor or source clock (event time) | `DateTimeOffset` |
| Waits | minutes in formulas; `TimeSpan` in code | `TimeSpan` |
| Service and cycle times | seconds in AMAN contracts; minutes in formulas | `TimeSpan` |
| Rates (throughput, arrivals) | passengers per minute | `double` |
| Counts per interval | passengers | `double` in forecasts (fluid model), `int` in measurement |
| Geometry | metres, degrees | `double` |
| Bin size delta | 15 minutes unless stated | `TimeSpan` |

Rounding: never call a ceiling on a floating-point ratio directly. Ratios that are exact integers in decimal can land one ulp above or below (for example (24 - 0.3) / (4.25 - 0.3) evaluates to 5.999999999999999). Use `Math.Ceiling(Math.Round(x, 9))` or `decimal` arithmetic. Compare floating results in tests with a tolerance of 1e-9.

## F1. Sensor footprint from mounting height and field of view

Source: D4.

```
W = 2 * (h - z) * tan(alpha / 2)
```

| Symbol | Meaning | Unit |
|---|---|---|
| h | Mounting height of the sensor | m |
| z | Height of the tracking plane: head height for counting, 0 (floor) for coverage drawings | m |
| alpha | Field of view along one axis | degrees |
| W | Footprint width along that axis at plane z | m |

Apply once per axis to get the footprint L x W. Vendor footprint tables are authoritative because they include tracking margins; this formula is for sanity checks and committee discussion.

Edge cases: h <= z is invalid (sensor below the plane); alpha must be in (0, 180). Tilt is not modelled (Xovis allows up to 15 degrees on one axis and 5 on the other).

Tests: h = 4, z = 0, alpha = 90 gives W = 8.0. h = 4, z = 1.7, alpha = 90 gives W = 4.6.

## F2. Sensor count for a rectangular zone with overlap

Source: D4.

```
N = ceil((L - o) / (a - o)) * ceil((B - o) / (b - o))
N_min = min(N(a, b), N(b, a))
```

| Symbol | Meaning | Unit |
|---|---|---|
| L, B | Zone length and width | m |
| a, b | Sensor footprint length and width (vendor table) | m |
| o | Required overlap between neighbours; at least 0.3 for Xovis multisensor setups | m |
| N_min | Sensor count; try both footprint orientations and keep the smaller | count |

Edge cases: a <= o or b <= o is invalid. If L <= a the first factor is 1. Apply the rounding rule above. For LiDAR the binding constraint is occlusion, not range: plan so every point of a dense queue is seen by at least two sensors from different angles; the perception vendor's simulation sets the count (planning heuristic 2 to 4 per zone).

Tests (D4 worked example: 24 x 12 m snake plus 4 m overflow band = 24 x 16 m zone, o = 0.3):

| Ceiling | Footprint a x b | Expected N_min |
|---|---|---|
| 3 m | 9.6 x 9.6 (wide footprint) | 6 |
| 3 m | 3.75 x 2.13 (standard) | 63 (other orientation 65) |
| 4 m | 10 x 10 | 6 |
| 4 m | 7 x 4.25 | 16 (other orientation 18) |
| 12 m | 12 x 9 (high mount) | 6 |
| 18 m | 10.5 x 8 (ultra high) | 8 (other orientation 9) |

## F3. BOQ planning footprints and the area helper

Source: the BOQ in the business plan and its prototype calculator (D7, D8); given in the build brief. Used for bills of quantities before a site survey, not for installation design.

Planning footprints by ceiling height:

| Ceiling | Planning footprint L x W |
|---|---|
| 4 to 6 m | 10 x 10 m |
| 10 to 14 m | 12 x 9 m |
| Other heights | Use the vendor table (F2 tests give 3 m and 18 m examples) |

Area helper:

```
N = ceil( A / ((L - 0.3) * (W - 0.3)) * 1.3 )
```

| Symbol | Meaning | Unit |
|---|---|---|
| A | Zone area | m2 |
| L, W | Planning footprint | m |
| 0.3 | Overlap deducted from each footprint dimension | m |
| 1.3 | Planning margin for layout shape, obstructions and edges (named "overlap" in the prototype data) | factor |

The prototype computes `Math.ceil(area / cell * 1.3 - 1e-9)`; the 1.3 is inside the ceiling.

Edge cases: A = 0 gives 0; the helper ignores zone shape, so it can disagree with F2 by one or two sensors; the BOQ may override it by judgement.

Tests:

| Zone | A | Footprint | Expected N |
|---|---|---|---|
| Check-in | 1500 | 12 x 9 | 20 |
| Departure immigration | 930 | 10 x 10 | 13 |
| Arrival immigration | 1080 | 10 x 10 | 15 |
| Security | 800 | 10 x 10 | 12 (the reference BOQ uses 11 by judgement) |
| D4 worked example | 384 | 10 x 10 | 6 (matches F2) |

## F4. Line crossing (Proposed implementation)

Source: D5 requires Ariva to compute crossings centrally from tracks on versioned lines; the method below is Proposed.

For consecutive samples p1 at t1 and p2 at t2 of one track, and a line segment from q1 to q2, a crossing occurs when p1 and p2 are on opposite sides of the line and the segment p1 p2 intersects the segment q1 q2. The side of a point is the sign of the 2D cross product of (q2 - q1) and (p - q1); a point exactly on the line keeps the side of the previous sample. Each line stores which side is inside: moving from the outside to the inside is `In`, the reverse is `Out`. The crossing time is interpolated:

```
s = |p1 - x| / |p1 - p2|          (x = intersection point)
t_cross = t1 + s * (t2 - t1)
```

Rules:

- A track touching the line without passing it is not a crossing.
- Jitter: when a track crosses a line and back within a debounce window, keep only the net crossing (debounce window To confirm, Proposed 2 s).
- Lines must be at least 1 m inside sensor coverage (D4 site-survey rule).

Tests: line from (-1, 0) to (1, 0). Samples (0, -1) at 0 s and (0, 1) at 2 s give one crossing at 1.0 s. Samples (0, -1), (0, 0), (0, -1) give no crossing (touch and return). Samples (2, -1) and (2, 1) give no crossing (outside the segment).

## F5. Realised wait

Source: D4.

Per track (primary method):

```
w_i = t_exit_i - t_entry_i
```

| Symbol | Meaning |
|---|---|
| t_entry_i | Time of the first inward crossing of any entry line of the queue, including the overflow entry line (overflow time counts as wait) |
| t_exit_i | Time of the outward crossing of the exit line after t_entry_i |
| w_i | Realised wait of track i, minutes |

Excluded from realised wait: tracks with no exit (abandoned, fragmented, censored; see F6), tracks originating behind the desk line or from staff doors (staff exclusion rule).

FIFO method from crossings (fallback when tracks fragment, when only vendor line crossings are available, and in the fluid simulation): in a first-in, first-out snake the n-th entrant is the n-th to exit.

```
e_1 <= e_2 <= ...   entry crossing times, sorted
x_1 <= x_2 <= ...   exit crossing times, sorted
w_n = x_n - e_n
```

Equivalently, with cumulative entries A(t) and exits D(t), the wait of the n-th entrant is D^-1(n) - A^-1(n) (the simulator uses this form on fluid per-minute curves, interpolating within the minute).

Edge cases:

- w_i < 0 means a clock or geometry error: reject the track and raise a data-quality event.
- FIFO alignment breaks with abandonment, lane switching and count errors. Re-anchor both sequences whenever the queue is observed empty (occupancy 0); flag waits computed after a conservation residual above tolerance as `Degraded`.
- Fragmentation shortens waits (biases low); it is measured by the track completion rate (F18).

Tests: track entering at 18:00:00 and exiting at 18:12:30 gives 12.5 min. FIFO: entries at 0, 1, 2 min and exits at 5, 6, 8 min give waits 5, 5, 6.

Implementation (ARV-030, `Ariva.Core/Queueing/QueueStateEngine`): one engine per queue zone, pure (no I/O, time passed in) and bounded in state, work and output whatever a device sends: at most 20,000 people held, 100,000 events buffered (a tenth of it for events ahead of the clock), 20,000 resolved tracks remembered, 64 devices tracked apart and 100,000 records per step by default (a full step ends early and the next continues); an interval reading counts at most 5,000 people, and each event costs the number of devices or a logarithm, never the number of people. Events are held in a reorder buffer and processed in event-time order when the watermark (reference time minus the lateness allowance, default 30 s) passes them; the watermark never runs ahead of that, whatever the buffer holds. An event arriving behind the watermark is late, counted with the earliest late time, and not applied (a recomputation decides, F6). Events more than 5 minutes ahead of Ariva's clock are refused. An interval's counts are spread over the part of it after the last processed event, so they never reach into minutes already processed. Movements are reported as entries and exits per minute. A tracked exit closes its own track (method Track); any other exit pairs with the earliest person held (method Fifo; with interval counts spread evenly over their interval this is the cumulative-curve method, Cumulative, for T1 devices), except that a tracked exit never takes a person the same device is tracking (track keys are namespaced by device), so tracks that entered before the engine started do not displace others. Anonymous crossings of an overflow band's entry line are not counted, because the same person crosses the queue's entry line later; overflow time is counted only for tracked people. Re-anchoring happens when every zone of the queue (the queue zone and its overflow bands) reads zero; a residual of more than 2 people flags the FIFO waits that follow as `Degraded` until the next clean anchor. Inputs flagged by the sensing pipeline (a skewed or corrected clock) make the waits they touch `Degraded` (F11). On the reference evening, cumulative-curve waits from Xovis minute counts are within a minute of the tracked waits (QueueEngineScenarioTests).

## F6. Attribution to the entry bin; provisional and final

Source: D4, D5; timeout and censoring rules Proposed.

Attribution: a wait belongs to the bin containing its entry time.

```
bin(i) = floor((t_entry_i - t_day_start) / delta)    bins are [b, b + delta)
```

Track outcomes for a track that crossed an entry line:

| Outcome | Condition | In realised wait? |
|---|---|---|
| `Exited` | Crossed the exit line | Yes |
| `Abandoned` | Left the queue zone without crossing the exit line (back over an entry line or across a non-exit boundary) | No; counts in the abandonment rate |
| `Fragmented` | Track lost inside the zone and not recovered within the hand-over window | No; lowers the track completion rate |
| `Censored` | Still unresolved T_censor after entry | No; counted as censored, bin flagged |

A bin b is final when all of these hold, and provisional otherwise:

1. The bin has ended and the watermark has passed b + delta + lateness allowance (lateness in seconds, tuned per site; D5).
2. Every track that entered in b has an outcome (`Exited`, `Abandoned`, `Fragmented` or `Censored`). This is D4's "everyone who entered has exited", made explicit for tracks that never exit.
3. The zone's data quality for the bin is known (a sensor outage covering an entry or exit line makes the bin `Unknown`; it still becomes final, with that flag).

Timeout (Proposed, To confirm): T_censor = 120 min by default, configurable per zone profile. A bin finalised with censored tracks is flagged `Degraded` if censored tracks exceed 5% of its entries (threshold Proposed).

Revisions: a late event, a correction or a recomputation produces revision r + 1 of the bin; results are upserted by (zone, bin start, revision) and earlier revisions are kept. Each revision records `ZoneProfileVersion`.

Use: reports show provisional bins marked as such; SLA evaluation uses final bins only.

Edge cases: a bin with no entries is final once condition 1 holds, with count 0 and no wait statistics. A bin that a recomputation changes after it was used in a finalised evaluation produces a new evaluation revision, never an in-place change.

Tests: a passenger entering at 18:14:30 and exiting at 18:40:00 belongs to bin 18:00; that bin cannot be final before 18:40:00 plus the lateness allowance. A track entering at 18:05 and lost at 18:10 inside the zone is `Fragmented`; the bin can still become final.

Implementation (ARV-030): the engine resolves each entrant as exited (a wait), `Abandoned` (back over an entry line: the tracked person, or the latest anonymous entrant), `Fragmented` (a track with position samples not seen within the hand-over window, default 30 s, To confirm; or a tracked person still held when the queue is observed empty), `Censored` (T_censor after entry, default 120 min; or the earliest held when the zone's bound is reached) or `Reanchored` (an anonymous FIFO residual when the queue is observed empty). Bins, attribution and finality are ARV-031: `Ariva.Core/Queueing/BinAccumulator` takes the engine's steps in order and attributes each wait and resolution to the bin and minute of its entry, entries and exits to the minute they happened. Bins align to the site's day start (`DayStartOffset`; delta divides a day). A bin is final when the engine's watermark (which already allows for lateness) has passed its end and nobody who entered in it is unresolved; every bin the watermark passes is published, an empty one final with count 0 and no statistics. Quality is the worst of degraded inputs, a re-anchor residual or a rejected pair, censored above 5 percent of entries, approximate percentiles and the outages the caller marks (F11). Final results never change: late events go to the bin of their minute (the engine reports them per minute, within a 3-day horizon matching the archive; older ones are only counted), a provisional bin carries them and asks for a recomputation once final, a final bin asks at once, and so does an outage marked late for a final bin (`RecomputationRequest`, one per run of contiguous bins); `BinRevisions.Next` turns a recomputation over the archive into revision r + 1 for the final bins whose values changed, with its reason. Provisional percentiles come from the histogram (cheap to refresh, exact to 30 seconds); final ones from the exact waits. Bounds: 2,000 open bins (beyond, the earliest is published final with its open people, Degraded; a watermark jump beyond them is reported as a gap), 50,000 exact waits per bin including its minutes (beyond, final percentiles come from the histogram, Degraded), 1,000 quality marks; counts are 64-bit. The day start offset is a whole number of minutes. On the reference day, Handler B's island C measured this way breaches in the 19:00, 19:15 and 19:30 bins, as the scenario does (BinScenarioTests).

## F7. Percentiles per minute and per 15-minute bin (arrival-weighted)

Source: D4 (mean, median, P90 or P95, share within target per bin); D5; prototype for the weighting rule.

Arrival-weighted means every passenger counts once, whichever minute they entered in. With individual waits, take the percentile over all waits attributed to the bin. With weighted samples (per-minute cohorts, fluid simulation, forecasts), use the weighted nearest-rank rule:

```
sort samples by wait ascending: (w_1, omega_1), (w_2, omega_2), ...
Omega = sum of omega_k
P_p = smallest w_k such that sum_{j <= k} omega_j >= p * Omega
```

For n unweighted samples this is the nearest-rank percentile: the ceil(p * n)-th smallest value.

Other bin statistics:

```
mean   = sum(omega_k * w_k) / Omega
share(T) = sum(omega_k where w_k <= T) / Omega       share within target T
```

Rules:

- A bin with fewer passengers than the contract's minimum (default 1; prototype treats less than 0.5 fluid passengers as empty) has no percentile (null), not zero.
- Never average per-minute or per-bin percentiles to get a longer window. For hourly and daily views, merge the underlying samples. Proposed: store a fixed-width wait histogram per bin (30-second buckets up to T_censor) in queue_intervals so continuous aggregates can merge histograms and percentiles stay exact to the bucket width.

Implementation (ARV-031, `WaitStatistics` and `BinAccumulator`): percentiles are the weighted nearest rank over the exact waits of a bin or minute (unweighted: the ceil(p x n)-th), with mean, P50, P90, P95 and the share within the target (15 minutes by default); fewer waits than the minimum (default 1) give null. Each bin and minute also carries a sparse histogram of 30-second buckets (up to 24 hours, the last open-ended); `WaitStatistics.Merge` adds histograms and `Percentile` reads one at the upper edge of the bucket that reaches the rank, so longer windows are exact to 30 seconds and never averaged.

Tests:

- Waits 1, 2, ..., 10 min (unweighted): P90 = 9, median (P50) = 5, mean = 5.5.
- Weighted: (w = 5 min, omega = 10), (w = 20 min, omega = 2). Omega = 12, 0.9 x 12 = 10.8; cumulative weight is 10 at w = 5 and 12 at w = 20, so P90 = 20. The average of per-minute P90s would wrongly give 12.5.
- share(15) for the weighted case = 10 / 12 = 0.833.

## F8. Nowcast

Source: D4, D5. Blending weights Proposed.

```
W_now = (Q(t) + 1) / mu(t)

mu_desk(t) = n_open(t) / c(t)
mu_exit(t) = exits in the last m minutes / m
mu(t)      = beta * mu_desk(t) + (1 - beta) * mu_exit(t)
```

| Symbol | Meaning | Unit |
|---|---|---|
| Q(t) | Queue length now (occupancy of the queue zone, including the overflow band) | passengers |
| n_open(t) | Desks in `Idle` or `Serving` (staffed and not paused) for this lane | desks |
| c(t) | Recent mean cycle time per desk: AMAN `DeskIntervalStats` at AMAN sites, sensor-derived desk occupancy elsewhere | min |
| m | Exit-rate window ("last few minutes" in D4; Proposed 5 min) | min |
| beta | Blend weight (Proposed 0.5; tune in the pilot) | 0 to 1 |
| W_now | Nowcast wait for someone joining now | min |

D4 writes the throughput as n_open / s with s the mean service time, and also states that cycle time (service time plus walk-up gap) sets throughput and that using pure service time overstates throughput. Ariva therefore uses cycle time c. The desk term reacts the moment a desk opens or closes; the exit term damps noise. This is Little's law applied to the person at the back of the queue.

Variants:

- Fast-track merge: when priority and standard lanes share desks, the standard lane uses mu_std = r_merge * mu, where r_merge is the standard share of the shared desks.
- E-gates: mu_gate = (n_gates_in_service / c_gate) * (1 - r_reject) (see F12).
- Sensor outage on the zone: fall back to entry counts plus desk-event throughput; the result is `Degraded` and shown as a band (below).

Edge cases:

- No service: n_open = 0, or mu below 0.01 passengers per minute (reference). The nowcast is undefined (null), never infinite or zero. Screens show a neutral message; the "queue long with too few staffed desks" alert rule applies.
- Empty or near-empty queue: Q = 0 or W_now < 5 min shows the band "under 5 min".
- Unknown desks do not count in n_open, and the bin is flagged.

Display rules (D4: passenger screens show the nowcast rounded to 5-minute bands and change only when it moves a full band; never a realised number):

```
band(W) = [5 * floor(W / 5), 5 * floor(W / 5) + 5)
```

Hysteresis (Proposed exact rule): keep a reference value W_ref, set when the displayed band last changed. Change the displayed band to band(W_now) only when |W_now - W_ref| >= 5, then set W_ref = W_now.

Degraded band (reference): when the zone is degraded show [5 * floor(0.75 * W_now / 5), 5 * ceil(1.25 * W_now / 5)], widened so that it spans at least 10 min.

Stale data: when the latest nowcast is older than the staleness threshold (To confirm per site), screens switch to a neutral message instead of freezing on an old number.

Display precedence (first match wins): stale data or no service shows the neutral message; W_now at or above 120 min shows "over 120 min" (Proposed, ARV-032); a degraded zone shows the degraded band; W_now below 5 min shows "under 5 min"; otherwise the 5-minute band with hysteresis (extended in ARV-032, Proposed, to the "under 5 min" message, the degraded band and "over 120 min").

Tests:

- Q = 29, n_open = 6, c = 1.5 min: mu_desk = 4.0, W_now = 30 / 4 = 7.5 min, band "5 to 10".
- Same, with 18 exits in the last 5 min (mu_exit = 3.6) and beta = 0.5: mu = 3.8, W_now = 7.89 min.
- Fast track: mu = 4.0, r_merge = 0.5, Q = 29: W_now = 15 min.
- n_open = 0: null (no service).
- Hysteresis: W_ref = 12 (band 10 to 15); W_now = 15.5 keeps "10 to 15"; W_now = 17.2 switches to "15 to 20".
- Degraded: W_now = 12 gives "5 to 15"; W_now = 4 gives "0 to 10".

Implementation (ARV-032, `Ariva.Core/Queueing/Nowcast`): `Nowcast.Compute` blends the desk and exit terms when both are known, uses either alone otherwise (the exit term alone is flagged `Degraded`, since it lags desk changes), applies the fast-track share and the e-gate reject rate (`Nowcast.EgateRate`, F12), and returns no number with a reason (`NothingOpen`, `ThroughputTooLow` below 0.01 a minute, `NoThroughputData`, `NoQueueLength`, `Implausible` when no finite wait results). Cycle times under 0.05 minutes (3 seconds, Proposed) count as unknown, so a bad desk feed cannot produce an infinite throughput; an unknown fast-track share or reject rate is not assumed to be 1 or 0 silently but flags the result `Degraded`. `NowcastDisplays.Next` applies the display precedence and the hysteresis above, keeping W_ref between updates. Two display rules beyond the reference (Proposed): a wait at or above 120 minutes (the reference cap) shows "over 120 min", degraded or not, since a band there would exclude the estimate, and the degraded band is clamped to 120 while keeping its 10-minute width; a throughput above 1,000 passengers a minute (Proposed) is not real and gives `Implausible`, so a corrupt exit count cannot show "under 5 min"; the hysteresis also holds the "under 5 min" message and the degraded band, so a wait hovering at 5 minutes does not flap, while a change between degraded and live always shows at once. `ExitRate` sums the engine's per-minute exits over the last m whole minutes (one hour kept). Measured on the reference evening through the emulator and the queue engine with the exit term, the Visitors nowcast is within 2 minutes of the scenario's from 18:04 to 18:10 and passes 15 minutes at 18:05 to 18:06; the desk term arrives with the desk state engine (ARV-033).

## F9. Backlog recursion and capacity

Source: D4, D5. Per interval k (forecast step; 15 minutes in D4, 1 minute in the reference simulator):

```
D_k     = min(Q_k + A_k, C_k)                served
Q_{k+1} = max(0, Q_k + A_k - C_k)             backlog carried forward
mu_k    = C_k / delta
W_k     ~ (Q_k + A_k / 2) / mu_k              expected wait of an arrival in interval k
```

Capacity from staffed desks and cycle times:

```
C_k = sum over desks d of tau_{d,k} / c_{d,k}
    = n_k * delta / c_k                      (homogeneous desks staffed all interval)
```

| Symbol | Meaning | Unit |
|---|---|---|
| Q_k | Backlog at the start of interval k (Q_0 = measured queue length now) | passengers |
| A_k | Arrivals in interval k (forecast demand, F13, F14, plus reject inflow, F12) | passengers |
| C_k | Capacity in interval k | passengers |
| tau_{d,k} | Minutes desk d is staffed and not paused in interval k | min |
| c_{d,k} | Cycle time of desk d (lane category specific) | min |

The term A_k / 2 assumes arrivals spread evenly within the interval (Proposed refinement of D4's "roughly the backlog ahead of it divided by capacity"). The reference simulator steps per minute and reports the joining wait as (Q_{k+1} + 0.5) / mu_k, capped at 120 min. It also scales capacity by (1 - pause fraction) per process (reference: 0.035 immigration, 0.03 check-in, 0.02 security, 0 e-gates).

Edge cases: C_k = 0 makes W_k undefined; report the cap (reference 120 min) with a "no service" flag. Arrivals are fluid (non-integer) in forecasts.

Tests:

- C = 30 per 15 min, Q_0 = 0, A = 60, 60, 0: Q = 30, 60, 30; D = 30, 30, 30.
- C = 30 per 15 min, A = 30, 30, 30: Q stays 0.
- Q_k = 30, A_k = 60, C_k = 30 per 15 min (mu = 2 per min): W_k = (30 + 30) / 2 = 30 min.
- Capacity: 4 desks all interval, c = 1.5 min, delta = 15: C = 40. One desk opening 5 minutes into the interval contributes 10 / 1.5 = 6.67.

## F10. Desk state: precedence and staleness

Source: D4 (states, transitions, precedence, T1 and T2); staleness rule Proposed.

States: `Closed`, `Idle`, `Serving`, `Paused`, `Unknown`.

| Rank | Signal | Source | Proves |
|---|---|---|---|
| 1 | Transaction start and end | AMAN; common-use check-in transactions | `Serving`, exactly |
| 2 | Login and logout | AMAN desk session (`DeskSessionChanged`); common-use agent sign-in | Staffed candidate; logout proves `Closed` |
| 3 | Staff zone occupied behind the desk | Sensor | Someone is present |
| 4 | Service zone occupied in front of the desk | Sensor | Probably serving; weak on its own |
| none | A passenger walking up | Sensor | Nothing (people approach closed desks) |

Evaluation at time t (first matching row wins):

| # | Condition | Status |
|---|---|---|
| 1 | Every signal source for the desk is stale (no heartbeat or update within T_stale) | `Unknown` |
| 2 | Login source available and the latest session event is a logout or Closed | `Closed` |
| 3 | A transaction is in progress (start without end) | `Serving` |
| 4 | Logged in (or, without a login source, staff zone occupied) and the staff zone has been empty for at least T2 | `Closed` |
| 5 | Logged in (or staff present) and the staff zone has been empty for at least T1 with no transaction in that time. Where no staff-zone sensor exists, activity means transactions only | `Paused` |
| 6 | No login source, staff present, and the service zone occupied | `Serving` (weak; flagged as sensor-derived) |
| 7 | Logged in, or no login source and staff present | `Idle` |
| 8 | AMAN site, staff present, not logged in | `Closed` for throughput (present, not processing: a supervisor or training); record presence |
| 9 | Otherwise | `Closed` |

Parameters: T1 and T2 per site (examples 3 and 10 min; assumptions to tune in the pilot). AMAN's own "on break" session status maps to `Paused` (Proposed). T_stale is To confirm (Proposed: 2 x the source's expected heartbeat interval, and for AMAN the feed heartbeat on the `aman.feed` topics).

Throughput: n_open counts `Idle` and `Serving` only. `Paused`, `Closed` and `Unknown` do not count. Any `Unknown` desk flags the lane's bins `Degraded`.

Processing time measures (D4):

| Measure | Definition | Source |
|---|---|---|
| Service time per passenger | Transaction start to end; elsewhere service-zone entry to exit | AMAN `DeskIntervalStats` (mean and P90), sensors |
| Cycle time per desk | Time between successive service starts; includes walk-up gap; sets throughput | AMAN `DeskIntervalStats`, sensors |
| Service time per approach | A family of four is one approach and four documents | AMAN separates approaches and documents; sensors cannot reliably |

Lane cycle time from AMAN stats over a window (Proposed): c_lane = sum over desks and minutes of (cycle_seconds x transactions) / sum of transactions, divided by 60.

Tests (T1 = 3 min, T2 = 10 min):

- Logged in, staff zone occupied, last transaction ended 4 min ago: `Idle` (staff presence is activity).
- Logged in, staff zone empty for 4 min, no transaction: `Paused`.
- Logged in, staff zone empty for 11 min: `Closed`.
- Logout received: `Closed`, whatever the sensors show.
- Transaction started and not ended: `Serving`.
- All sources silent beyond T_stale: `Unknown`.

Implementation (ARV-033, `Ariva.Core/Desks`): `DeskRule.Evaluate` applies the table above to what is known of each source at a moment; a configured source silent beyond T_stale counts as absent, so the state falls back to the sources that remain (the AMAN feed down leaves the staff and service zones) and is flagged `Degraded`. Choices beyond the table (Proposed): the session's "on break" gives `Paused` after row 3, since a transaction in progress is exact; transaction activity within T2 (or T1) keeps row 4 (or 5) from firing, since rank 1 outranks a missed staff zone reading; the start of a session counts as activity, so a fresh login is not `Paused` at once; without a live login source a desk counts as staffed while staff are present, within T2 of the staff leaving (an occupied reading followed by an empty one) or within T2 of its last transaction, so rows 4 and 5 give their grace and a sensor dropout of a few seconds does not close the desk; a logout with staff still present records presence, as row 8 does; a sensor-derived state on healthy sensors is counted in the lane but does not make it `Degraded` (F11); a transaction open longer than 30 minutes is a lost end, not `Serving`; emptiness of the staff zone is counted from the first empty reading after an occupied, unknown or stale stretch. `DeskStateEngine` runs many desks: signals in event time reordered within a lateness allowance (30 s), the state re-evaluated at each signal and at each moment a threshold passes (`DeskRule.NextChange`), so the time in each state is exact; it emits `DeskTransition` (the `DeskStateChanged` event) and per-desk `DeskMinute` aggregates (time per state, transactions completed, sensor-derived and present-not-processing time, degraded), with no officer, traveller or document identity. Late signals apply at the desk's current time without revising emitted minutes; a signal older than the latest of its kind is superseded, so a late logout cannot undo a newer login; signals more than 15 minutes behind the watermark are refused (a replay belongs in an engine started at its own time), and a desk more than a day behind skips the gap, counted, instead of emitting every minute of it. Buffering is fair: each desk holds at most 1,000 signals, a tenth of them ahead of the clock, and past half the engine's buffer no more than an equal share, so a flood on one desk cannot make the others `Unknown`. The stream host's desk feed (ARV-049, `DeskFeed`, `AmanDeskFeed`) takes AMAN's records as Ariva stored them (ARV-048, once per site and `SourceEventId`), each once (a read position in receipt time with a two-minute overlap), and maps them to session signals (Opened, Closed, Paused) and `DeskTransactionsCompleted` only (exact transaction times stay in AMAN; starts and ends are for common-use check-in). AMAN publishes sessions only on change and intervals only while a desk is in session, so the feed is the heartbeat: every whole minute the site's AMAN feed is seen alive, each AMAN desk gets a heartbeat for its session and transaction sources, and a desk AMAN reports no session for stays `Closed` (row 9) while the feed lives and turns `Unknown` after T_stale once it stops. One engine per site, AMAN's lateness 90 seconds (AMAN publishes an interval when it closes), the reference time from the host's clock, minutes to `desk_minute`, keys site/checkpoint/desk. Staff and service zone readings are not yet fed to it (the stream drops non-queue zones), and the nowcast's desk term is not yet joined (open, see decisions). `LaneDesks` gives n_open (Idle and Serving) and the lane's Degraded flag for F8, and `LaneCycle` the transaction-weighted lane cycle time above. Replaying the reference day's Visitors desk staffing as session signals, the engine's open desks match the scenario's every minute, silent desks turn `Unknown` after T_stale, and the nowcast from the desk term matches the scenario's.

## F11. Data-quality propagation

Source: D5 invariants and failure modes.

| Condition in the bin | Flag |
|---|---|
| All sensors healthy, all desks known, clocks within threshold | `Good` |
| A sensor degraded, a desk `Unknown`, clock drift corrected, AMAN feed stale with last-known values used, censored tracks above threshold | `Degraded` |
| An entry or exit line without coverage for the bin, or no measurement at all | `Unknown` |

The worst flag of any input wins. Every interval result, nowcast and forecast carries its flag.

Implementation of "a sensor degraded" in the stream (ARV-036, `Ariva.Core/Queueing/DeviceLiveness`, Proposed): the zone processor follows its commissioned devices from its own records, so a replay finds the same outages as the live run. Every sensing batch and every device health report (`ariva.device.health.v1`, keyed by zone since ARV-036) means the device was heard at its receive time; a health report may say it is offline. A device heard before and silent for longer than 180 seconds (`DeviceSilenceSeconds`, the same as `Devices:Health:HeartbeatTimeoutSeconds`), or reporting itself offline, is out from the minute it was last heard in until the minute it is heard again online (`DeviceOutage`, kept in `zone_outage`). While a device is out the zone's live minutes are `Degraded` (from the moment the silence passes the limit) and its bins are marked `Degraded` for the outage, as it grows and once more when it ends; a bin already final asks for a recomputation. A device silent for 24 hours (`DeviceForgetHours`) is treated as removed: its outage ends there and it no longer counts. On the reference evening S-17 is last heard at 18:20 and again at 18:31, so the Visitors zone's outage is 18:20 to 18:31 and its 18:15 and 18:30 bins are `Degraded`.

## F12. E-gate reject coupling

Source: D4; lag and live form from the reference simulator.

```
mu_gate        = (g / c_gate) * (1 - r)
A_manual(t)    = A_own(t) + r * F_gate(t - lag)
r              = rejects / attempts             (AMAN EGateIntervalStats over a window)
```

| Symbol | Meaning |
|---|---|
| g | E-gates in service (not closed, not out of service) |
| c_gate | Mean e-gate cycle time, min |
| r | Reject rate, 0 to 1 |
| F_gate | Live: passengers processed by the gates; planning: e-gate arrivals (D4's form) |
| lag | Walk from the gates to the manual queue (reference 1 min) |

Which manual lane receives rejects is a site rule (reference: the visitors lane), To confirm per site. Reference reject rate 0.07.

Tests: g = 6, c_gate = 0.3 min (18 s), r = 0.07: mu_gate = 18.6 per min. E-gate flow 100 in an interval with r = 0.07 adds 7 to the manual lane's arrivals.

Implementation (ARV-049, `Ariva.Core/Border/EgateCoupling`): r is measured from AMAN's e-gate intervals over the last `Border:EgateCoupling:RateWindowMinutes` (30) when they hold at least `MinAttempts` (20) attempts, else the reference 0.07; the rejects join `RejectLane` (VIS, one per deployment, To confirm per site) after `LagMinutes` (1). In the arrival projection that predicted-breach rules read (ARV-047), the reject lane's demand per minute is its own plus r times the e-gate eligible arrivals a lag earlier (planning form), and the first lag minutes take the rejects AMAN reported just before (live form); the e-gate lane keeps its arrivals, since rejected passengers queue at the gates first. AMAN's e-gate intervals also fill `egate_minute` (processed = attempts, rejected, mean cycle). The arrival-wave API shows r and the reject lane to callers with the lane split.

## F13. Departure demand and show-up curves

Source: D4.

```
A_p(t) = sum over flights f of  N_f * Pr(t <= STD_f - S_f < t + delta) * sigma_{p,f}
Pr(t <= STD_f - S_f < t + delta) = F_S(STD_f - t) - F_S(STD_f - t - delta)
```

| Symbol | Meaning |
|---|---|
| A_p(t) | Passengers reaching process p (check-in, security, emigration) in [t, t + delta) |
| N_f | Booked passengers, or seats x forecast load factor |
| S_f | Minutes before departure a passenger reaches process p; its distribution F_S is the show-up curve, learned per segment (destination region, time of day, weekday, season, airline) |
| sigma_{p,f} | Process share: online check-in skips desks; e-gate eligibility splits emigration lanes |

(F_S here is the cumulative distribution; take the endpoints so that each passenger is counted in exactly one interval.)

Learning show-up curves: anonymous sensors cannot tell which flight a passenger is on. Curves are learned from check-in or boarding-pass timestamps per flight where the airport shares them, or by fitting curves so that the summed flight demand reproduces measured entry counts. New or seasonal routes borrow curves from similar routes until they have history.

Reference show-up curve (simulator): arrivals between STD - 180 and STD - 45 min, minute weights proportional to x^1.6 * (1 - x)^1.3 with x = (k + 0.5) / 136 for k = 0 to 135, normalised; online check-in share 0.40 goes straight to security (3 min walk).

Edge cases: a flight with N_f = 0 contributes nothing; flights departing after the horizon still contribute to early intervals.

Test: N = 200, sigma = 0.6, S uniform on [45, 180] min, STD = 12:00, t = 10:00, delta = 15: S in (105, 120], Pr = 15 / 135 = 0.111, A = 13.33.

## F14. Arrival hall curve and arrival-wave alert

Source: D4 (on-block plus deplaning plus walking or bus time, spread over a distribution learned per stand type); the 8 to 15 minute delay and 12-minute spread are the reference values given in the build brief and used in the simulator.

```
d_f      in [8, 15] min           delay from on-block to first arrival at the hall (deplane plus walk or bus)
A_l(t)   = sum over flights f of  P_f * share_{l,f} * omega_{t - (T_f + d_f)}
omega_j  = (3, 6, 9, 11, 12, 12, 11, 10, 8, 7, 6, 5) / 100   for j = 0 to 11, else 0
T_f      = estimated in-block before landing, actual in-block after
```

| Symbol | Meaning |
|---|---|
| P_f | Arriving passengers: API boarded count or the airline load message, transfers removed |
| share_{l,f} | Share for lane category l from API nationality and document type against eligibility rules (`InboundFlightLaneDemand` at AMAN sites) |
| omega | Reference 12-minute spread, weights sum to 1 |

Reference lane split (simulator, without API): with mix shares for CRW, CIT, RES, VIS, TRF and e-gate share e of eligible citizens and residents:

```
CRW = P * mix_CRW
CIT = P * mix_CIT * (1 - e)
RES = P * mix_RES * (1 - e)
VIS = P * mix_VIS
EG  = P * (mix_CIT + mix_RES) * e
transfers (mix_TRF) are removed
```

Reference values: mix CIT 0.35, RES 0.20, VIS 0.35, CRW 0.02, TRF 0.08; e = 0.40; expected delay 11 min (midpoint) when unknown.

Arrival-wave alert: the sum of A_l(t) over [now + 5, now + 25] min per hall or lane, compared with a threshold or with the capacity of staffed desks over the same window (threshold To confirm).

Predicted breach (ARV-038, rule metric `PredictedNowcast`, lead time L from 15 to 60 minutes): from the queue Q(t) and the throughput mu(t) of the minute (F8),

```
Q(t + k) = max(0, Q(t + k - 1) + A(t + k) - mu(t))     for k = 1 to L, with Q(t + 0) = Q(t)
W(t + k) = (Q(t + k) + 1) / mu(t)
predicted(t) = max over k of W(t + k), for the first k that reaches it
```

The rule judges predicted(t) against its threshold like a nowcast, and its alert names t + k as the minute the breach is projected for. Taking the highest W over the horizon, not only W(t + L), keeps a wave that the desks would have cleared by t + L. No value without mu(t) > 0 or without A for every minute of the horizon, and none while the queue length is degraded.

Tests (ARV-038):

- Q = 20, mu = 5, A = 10 per minute for L = 15: W peaks at k = 15, (20 + 75 + 1) / 5 = 19.2.
- Q = 0, mu = 10, a single minute of 100 arrivals at k = 3 and none after: W peaks at k = 3, (90 + 1) / 10 = 9.1.

Tests:

- P = 200, one lane, T = 18:00, d = 10: arrivals per minute from 18:10 to 18:21 are 6, 12, 18, 22, 24, 24, 22, 20, 16, 14, 12, 10 (sum 200).
- Lane split, P = 200 with the reference mix: CRW 4, CIT 42, RES 24, VIS 70, EG 44 (total 184; 16 transfers removed).

Implementation (ARV-047, `Ariva.Core/Flights/ArrivalWave`): T_f is the actual on-block; after a landing without one, the later of landing plus taxi-in (`Flights:ArrivalWave:TaxiInMinutes`, 5, Proposed) and the estimate; else the estimate (the AODB's estimated on-block); else the schedule; it is taken to the whole minute, so each passenger counts in exactly one minute. P_f and the split are AMAN's `InboundFlightLaneDemand` for the same site and flight key when received (P_f its boarded total, the lanes and e-gate eligible as sent, the rest taken as transfers), else the feed's passenger estimate, else seats times a load factor (0.8, Proposed), each with the default mix (`Flights:ArrivalWave:Mix`, the reference values above, To confirm per site); a flight with none of these is listed without passengers and counted. d_f is one value per deployment (`DelayMinutes`, 11, from 8 to 15) until stand types are known. The projection keeps arrivals neither cancelled nor diverted that land within the window (5 to 120 minutes, default 30) or whose hall minutes are not all past, and gives hall arrivals per minute and lane from the current minute for window + d + 12 minutes. The alert window sum covers the 20 minutes from now + 5 to now + 25. For predicted-breach rules (`ProjectedArrivalWave`, ARV-038's arrival source), a queue zone receives the lanes of the desks its service zones stand at in the site's published profile, each lane shared equally among the queue zones that serve it (an overflow band takes its queue zone's); a zone serving no lane has no projection, and minutes before the current one have none, so a backtest finds none (at most its last minute when it ends now). The lane split is border data: airport roles get flight and minute totals only (`ArrivalWaveLanes.View` for the split).

Tests (ARV-047, `ArrivalWaveTests`): the two tests above; AMAN's split preferred to the mix; the in-block and passenger precedence; window edges, a flight still arriving and a flight fully arrived; floored in-block and the alert window.

## F15. Monte Carlo P50 and P90

Source: D4, D5; run count and perturbations from the reference simulator.

```
for r = 1..R:
    sample inputs (show-ups, on-blocks, loads, service times) with a deterministic seed
    run the backlog recursion (F9) from the current state
    record W_r(h) for each future step h, weighted by arrivals A_r(h)
P50(h) = v[floor(0.5 * (R - 1))]      v = W_1(h)..W_R(h) sorted ascending (0-based)
P90(h) = v[ceil(0.9 * (R - 1))]
```

Reference perturbations (simulator): R = 40 runs; horizon 120 min; flights not yet landed: on-block = estimated in-block + uniform(-6, +6) min; delay d = clamp(11 + round(uniform(-3, +3)), 8, 15); load x (1 + uniform(-0.04, +0.04)); service rate per queue divided by (1 + uniform(-0.08, +0.08)); joining wait capped at 120 min. Seed derived from (scenario seed, now, run index) so a run is reproducible.

For a 15-minute bin, combine per-step values across runs with arrival weights (F7), not by averaging step percentiles. Every run records the model version and an input snapshot.

Edge cases: R < 10 gives unstable P90 (Proposed minimum 20 in production). Zero capacity gives the cap.

Test: R = 40 with values 1, 2, ..., 40: P50 = v[19] = 20, P90 = v[36] = 37.

## F16. Staffing recommendation

Source: D4.

For each interval k choose the smallest n_k such that the predicted P90 wait (F15) stays at or below target, subject to maximum desks, minimum open durations, shift boundaries and break rules. Intervals are coupled through the backlog, so decide forward in time: fixing n_k changes Q_{k+1}. A single lane is solved greedily; a hall (shared officers across lanes) exactly with an integer optimiser (OR-Tools; v1).

Reference targets (simulator): P90 target 10 min for immigration and check-in, 8 min for security.

Test: with C per desk = 10 per 15 min, Q_0 = 0, A = 25, target "Q_{k+1} = 0": n = 3 (2 desks leave a backlog of 5).

## F17. SLA evaluation per bin, exclusions and allowance

Source: D5 (contracts, final bins only, profile version, evidence packs); exact rules and reference values from the prototype's SLA screen.

Per contract, per zone in scope, per final bin b of the contract's bin size:

```
evaluated_b = status_b == Final and pax_b >= minPax and value_b != null
breach_b    = value_b > threshold        (percentile wait, overflow minutes)
            = value_b < target_share     (share of passengers under the threshold)
excluded_b  = an exclusion of a type the contract allows overlaps b
              (exclusion.from < b + delta and exclusion.to > b),
              or the bin's dispute is Upheld
held_b      = the bin's dispute is Raised or UnderReview
```

Per evaluation window (calendar month or week):

```
breaches  = count(evaluated_b and breach_b)
counted   = breaches - count(breach_b and excluded_b) - count(breach_b and held_b and not excluded_b)
penalised = max(0, counted - allowance)
penalty   = min(cap, penalised * rate_per_bin)
```

KPI types (prototype): percentile wait per bin (default P90, arrival-weighted, F7), share of passengers under the threshold, overflow minutes per bin.

Exclusion types (prototype; per contract, To confirm): security directive; sensor outage over the zone for more than 5 minutes in the bin; counters closed on airport instruction; flight disruption outside the handler's control; upheld dispute.

Reference contract values (prototype, not contractual): P90 per 15-minute bin at most 15 min; minimum 1 passenger per bin; allowance 6 breached bins a month; USD 350 per further breached bin; cap USD 10,000 a month; dispute window 5 working days.

Rules: provisional breaches are shown but never counted. Disputes apply to final breached bins only. When a dispute resolves, the evaluation is recomputed as a new revision. Every evaluation records contract version, zone profile version per bin and the evidence pack hash.

Tests:

- 10 breaches, 2 excluded, 1 held, allowance 6, rate 350: counted 7, penalised 1, penalty USD 350.
- 40 counted, allowance 6, rate 350, cap 10,000: penalised 34, 34 x 350 = 11,900, penalty USD 10,000.
- A sensor outage of 4 minutes within a breached bin does not exclude it; 6 minutes does.

## F18. Health checks and validation metrics

Source: D4 (checks, protocol, proposed acceptance targets); D6 (additional pilot criteria). Acceptance targets are proposals to agree contractually, not industry standards.

Continuous health checks:

```
conservation residual  r = (entries - exits) - (Occ(t + delta) - Occ(t))        should be 0
track completion rate  = tracks with outcome Exited / tracks that entered
occupancy sanity       0 <= Occ(t) <= zone physical capacity
```

Validation metrics per 15-minute bin:

```
count accuracy   = 1 - |N_system - N_manual| / N_manual                 per line
wait error       = w_system - w_tracer                                  per tracer
wait tolerance   |wait error| <= max(1 min, 0.10 * w_tracer)
bias             = sum(w_system - w_tracer) / sum(w_tracer)
```

| Criterion | Target |
|---|---|
| Count accuracy per 15-minute bin, each line | at least 95% |
| Realised-wait absolute error | within max(1 min, 10% of the true wait) |
| Realised-wait bias | within plus or minus 5% |
| Track completion | at least 90% |
| Desk-state agreement with observer log (D6) | at least 95% of observed minutes |
| Nowcast error against later realised wait (D6) | median within 2 min for waits under 20 min |
| Availability during the pilot (D6) | 99% of operating hours |
| Ground-truth proof (D6) | nowcast error with and without AMAN inputs, side by side; no target |

Re-validate after any layout change, sensor replacement or health-check alarm, and at least quarterly for penalty-grade zones. Validation runs at least five operating days including two peak days (assumption).

Edge cases: N_manual = 0 makes count accuracy undefined; report absolute error instead.

Tests: N_system = 96, N_manual = 100 gives 96%. Tracer 8 min, system 8.9 min: tolerance 1 min, pass. Tracer 20 min, system 22.5 min: tolerance 2 min, fail.

## F19. Clock drift

Source: D5. Threshold is an assumption to tune; stability rule Proposed.

```
offset_s(t) = t_sensor - t_reference        reference = site NTP or PTP source
alarm when |offset_s| > 500 ms
stable when the standard deviation of offset_s over the last 10 readings < 50 ms   (Proposed)
```

Above the threshold: if stable, subtract the offset from the sensor's timestamps and flag `Degraded`; otherwise mark the sensor `Degraded` and its zone's waits across sensor boundaries unreliable.

Test: offsets 620, 610, 630 ms (stable): corrected. Offsets 100, 900, 300 ms: not stable, sensor degraded.

Implementation (ARV-023, `ClockOffset`): each push gives one reading, the device's send time minus Ariva's receive time (network delay included, a few milliseconds on a site LAN). The estimate is an exponentially weighted moving average with weight 0.2 for the newest reading; the stability test uses the last 10 raw readings and needs at least 3. The clock is Ok while the estimate and every recent reading are within 500 ms; otherwise Corrected (event times shifted by the estimate) when stable, and Unreliable when not.

## F20. Alert rule evaluation

Source: D5 (hysteresis, hold times, deduplication, suppression); parameters from the prototype's seed rules.

```
raise  when condition holds for `sustain` consecutive minutes (and queue length >= minQueue if set)
clear  when value < clearBelow (or the condition is false) for `clearAfter` consecutive minutes
escalate when not acknowledged within escalateAfter minutes
```

Rules evaluate nowcasts and forecasts, never realised waits. One open alert per rule and target (deduplication); suppression windows silence planned events.

Reference rule R-001: nowcast above 15 min with at least 10 in the queue, sustain 1 min, clear below 12 min, escalate after 10 min to the border operations duty officer. Measured outcome KPI (D5): time from alert to the next desk opening.

Test (minutes numbered from 1, sustain 1, clearAfter 1): nowcast series 14, 16, 16, 13, 11 min with queue 12, threshold 15, clearBelow 12: raised at minute 2, still open at minute 4 (13 is not below 12), cleared at minute 5.

## F21. Sizing arithmetic

Source: D5 assumptions.

```
messages per second = sensors * people per sensor * Hz          100 * 30 * 5 = 15,000
bandwidth           = messages per second * bytes per message   15,000 * 100 B = 1.5 MB/s
stored rows per day = rows per second * 86,400                  1,000 * 86,400 = 86.4 million
```

About 9 GB a day uncompressed, 1 to 2 GB a day compressed, 80 to 160 GB for a 90-day dispute window; provision 200 GB.

## F22. Zone profile validity and geometry hash

ARV-016. A zone profile version is publishable when:

- every zone is a simple polygon of 3 to 200 vertices with non-zero area, every vertex inside its level (0 to width, 0 to depth metres), and no two vertices closer than 1 cm;
- zone names are unique in the profile, ignoring case; line names likewise;
- every queue zone has at least one entry line and exactly one exit line, each lying on one edge of that zone (both ends within 1 cm of the same edge);
- every overflow zone has an overflow entry line on one of its edges; service, staff and overflow zones hang off a queue zone on the same level; only service and staff zones name a desk;
- count lines have length (at least 1 cm) and both ends inside their level.

Coordinates are rounded to the millimetre (half away from zero, negative zero written as zero) when stored.

Geometry hash: SHA-256, lower-case hex, of the UTF-8 text below, fixed when the version is published and quoted by evidence packs and signed contracts. It contains no ids of the profile itself, so equal geometry gives an equal hash whatever the order of editing.

```
ariva-zone-profile-v1
zone|<name>|<kind>|<level id, 32 hex>|<queue zone name or empty>|<desk id, 32 hex, or empty>|<x y,x y,...>
...                                   (one line per zone, ordered by name, ordinal)
line|<name>|<role>|<zone name or empty>|<level id>|<x y>|<x y>
...                                   (one line per line, ordered by name, ordinal)
```

Numbers are written with three decimals in invariant culture. Changing this form needs a new prefix (`ariva-zone-profile-v2`) while v1 stays verifiable.

Tests: the reference profile in `ZoneProfileTests` (snake 24 x 12 m at (10, 10) with an entry and an exit line, an overflow band with its entry line, one count line) hashes to `fd3d7d1585070bd347de877d87aa3bb4e28d2b571081986650ec934471d9a1da`, recomputed independently in Python; moving a vertex by 1 mm changes the hash, by 0.4 mm does not.

## Property tests (ARV-070)

Beyond the fixed cases above, `Ariva.UnitTests/Properties/FormulaPropertyTests.cs` checks invariants over generated inputs with CsCheck (2,000 inputs per property in every build; `ARIVA_FUZZ_ITERATIONS=10000` runs 50,000, done once for ARV-070). A failure prints the shrunk input and its seed; the case then joins the formula's own tests.

| Formula | Property | Status |
|---|---|---|
| F1 | Footprints are positive; length never shrinks as the sensor goes higher, nor the area beyond the 0.1 m rounding | Property |
| F2, F3, F21 | Sensor counts, BOQ footprints and sizing arithmetic | Not in Ariva.Core (planning and BOQ work); no property |
| F4 | Segment intersection does not depend on order or direction; a ring keeps its area (sign flips when reversed) and its contents when rotated or reversed; a convex ring is simple and holds the mean of its corners | Property |
| F5, F6 | Realised waits and their attribution | Covered by the reference scenario, the queue engine tests and mutation testing (ARV-069); the engine's state machine has no independent model to generate against |
| F7 | A percentile is one of the waits, grows with p, and does not change when every weight is scaled; the share within target is a fraction that grows with the target; the mean lies within the waits; a histogram percentile is the exact one to the bucket, and merging two halves gives what the whole would | Property |
| F8 | The nowcast is finite and positive, equals (Q + 1) / mu, and never falls as the queue grows or rejects rise; a displayed band holds the estimate (live bands 5 minutes wide, degraded at least 10, within the ceiling) | Property |
| F9 | A predicted peak is at least the first minute's nowcast and never falls when arrivals rise | Property |
| F10 | Every second of each desk minute is in exactly one state; transitions chain (each leaves the state the previous one entered) in time order; a lane's counts add up | Property |
| F11 | Data-quality propagation | Covered by the device liveness and zone processor tests; no property |
| F12 | Coupling adds exactly rate x the e-gate demand lag minutes earlier to the reject lane and nothing else; the reject rate is a share, the reference value below the minimum attempts | Property |
| F13, F15, F16, F17, F18 | Departure demand, Monte Carlo, staffing, SLA evaluation, validation metrics | Not implemented yet; properties come with them |
| F14 | The lane split keeps every passenger but transfers; the projection spreads each flight's passengers over the hall without losing or adding any | Property |
| F19 | The clock estimate stays within its readings, keeps at most 10, ignores readings beyond a day, and a steady offset is corrected exactly | Property |
| F20 | Alerts alternate raised and cleared, a run equals its steps, and minutes already seen change nothing | Property |
| F22 | The ring text the geometry hash covers reads back to itself, each coordinate within half a millimetre | Property |

The parsers at Ariva's edge have their own properties (`ParserPropertyTests`, CWE-120): random bytes and valid samples cut short, mutated or with a value retyped give a result or Ariva's reason, never an exception, and a sensing push never more events than its limit. They found four faults and the security review a fifth, each fixed with a regression test: a JSON string holding invalid UTF-8 (parsed, then threw when read) in the sensing ingest and AMAN's pull pages; a null item in a canonical push list; AMAN's page position given as something other than a number; a token answer that is not an object or gives its expiry as text; and a member name or string escaping a lone surrogate (`\uDC00`, valid UTF-8 that parses and throws when read) in the sensing ingest, AMAN's pull pages, the token answer and the ACRIS reader. The generators now insert such escapes.
