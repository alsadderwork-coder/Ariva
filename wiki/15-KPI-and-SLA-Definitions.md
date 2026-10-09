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
| mu | Throughput: a blend of staffed-desk throughput `n_open / c` (desks idle or serving, divided by c, the recent time per person at a desk) and the measured exit rate over the last m minutes (Proposed m = 5, blend 0.5) |

- Example: 29 queuing, 6 desks open, cycle time 1.5 minutes: mu = 4.0 per minute, nowcast 7.5 minutes, displayed as "5 to 10 min".
- Fast-track lanes sharing desks use their share of the desks (merge ratio). E-gates use `(gates in service / cycle time) x (1 - reject rate)`.
- No staffed desk: the nowcast is null ("no service"), never zero or infinite.
- Displays: 5-minute bands with hysteresis (the band changes only when the nowcast moves 5 minutes from the reference value); "under 5 min" below 5; a widened band when degraded; a neutral message when stale or no service. Never a realised number.
- Accuracy target for the pilot: median error against the later realised wait within 2 minutes for waits under 20 minutes.
- Cycle time at AMAN sites (ARV-117d, Proposed; the method accepted by the owner on 2026-10-07, the walk-up gap to re-measure on pilot data): c is the time a desk spends per person, from AMAN's one-minute desk statistics: the officers' mean service time per transaction times the transactions, over the travel documents (people) processed. Before this change Ariva took AMAN's start-to-start cycle per transaction: a family processed together is one transaction, so the throughput read about 20 percent low (1.25 people per transaction on the reference day), and after a quiet spell the time a desk waited for the next passenger counted as service. What operators will see: **published waits at AMAN sites drop**, most after a lull and where families travel together; on the reference evening (Ariva's own simulator, not field data) the published wait's average overstatement fell from 1.85 minutes to 0.31 minutes. Where AMAN's statistics carry no documents the nowcast uses the exit rate alone and is shown as an estimate; statistics outside the contract's bounds are left out and the wait is shown as a band. The walk-up time between one passenger leaving and the next arriving at the desk is no longer in c, so in the field the published wait may read slightly low where that gap is long; the pilot measures it (to confirm with AMAN, [pilot-to-confirm](../docs/product/pilot-to-confirm.md) TC-84). Sites without AMAN are unchanged.

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

Service and cycle times (from AMAN `DeskIntervalStats` at AMAN sites, sensors elsewhere): service time is transaction start to end; cycle time is start to next start and includes the walk-up gap; cycle time sets throughput. AMAN separates approaches (a family of four is one) from documents (four). The published nowcast at AMAN sites takes the mean service time per document (ARV-117d, Nowcast above), since AMAN's cycle also counts the time a desk waits after a quiet spell.

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

### How the comparison is computed (F18, ARV-104e, Proposed)

Ariva compares a campaign's ground truth with what it stored, under the campaign's zone profile version only. Every result carries that version. The rules below are proposals pending the product owner and the client's KPI annex (`../docs/product/decisions.md`).

- **What is compared**: only items whose stored results are final, of the campaign's version and of `Good` quality, with no sensor outage over their time. Items in `Degraded` or `Unknown` bins (a bin with no stored result is `Unknown`), provisional bins, bins of another version and tracers whose entry minute holds no realised wait are listed apart with their counts, never dropped; their values are shown where Ariva has them.
- **Latest revision only**: a stored bin, a stored minute or a count is read at its latest revision. When that revision holds a value that cannot be (a negative count, more people leaving than entering) or two rows of it disagree, the result is listed as unusable and treated as missing: an earlier revision never takes its place. A line and bin whose counts include an observer's unusable latest correction is not judged (the other observers' counts of it are set aside with it), and a line whose stored crossings cannot be used has no system count rather than 0. A time recorded without its UTC marker counts as a value that cannot be. Counts of bins and tracers who joined outside the campaign's planned days are left out, two tracer runs from one observer with the same label and join time are both left out, and a tablet batch holding a run that cannot be is left out whole.
- **Sensor outages**: an outage still open counts to the end of the comparison. An outage record whose times cannot be read makes the whole queue unknown for the campaign (rather than being ignored), and the other queues keep their results.
- **Count accuracy, per line and 15-minute bin**: `1 - |N_system - N_manual| / N_manual`, never below 0 (a system count above twice the manual count shows 0), with the absolute error always shown. An entry or overflow entry line is compared on people crossing in, an exit line on people crossing out, a count line on both. Two observers of the same line and bin count as the mean of their latest counts, and their difference is shown. A manual count of 0 has no accuracy: the absolute error stands instead, without a verdict. A line passes when every compared bin reaches 95 percent; its lowest and pooled accuracy are shown. Example: 96 counted by Ariva against 100 counted by hand is 96 percent.
- **Which system wait a tracer is compared with**: the final realised wait of the people who entered the same queue at the tracer's join time (the tablet's time corrected by its clock offset), read from the stored minutes: each minute's mean wait is placed at the middle of the minute and the value at the join time lies on the straight line between the two minutes around it. Example: minutes with mean waits of 8 and 10 minutes, and a tracer who joined a quarter of the way from the first minute's middle to the second's: 8.5 minutes. The neighbouring minute is used only when its own stored results are final and `Good` (for example not in a degraded bin or under a sensor outage); otherwise the join minute's own mean is used alone. A sensor outage in the join minute, even before the tracer joined, sets the tracer apart.
- **Wait error and bias, per queue**: error = system wait minus tracer wait, within tolerance when its size is at most the larger of 1 minute and 10 percent of the tracer's wait (8 minutes against 8.9 passes; 20 against 22.5 fails). Bias = the sum of the errors over the sum of the tracers' waits, positive when Ariva overstates; target within plus or minus 5 percent. The wait criterion passes when every compared tracer is within tolerance (the annex may set a share instead). A tracer who left without being served (abandoned) has no error and no part in the bias: abandoned tracers are counted apart, beside the abandonment Ariva recorded in the same bins.
- **Clock offsets**: for each observer (an account id, never a name), the lowest, median and highest offset of its tablet's batches; a batch more than 5 seconds from its observer's median is flagged (a stale clock reading or a tablet whose time was changed). The report also shows the wait error and bias with every offset 2 seconds lower and higher, so a reader sees how much the clock correction matters.
- **Track completion, per queue**: tracks that exited over tracks that entered, summed over the campaign days' `Good` bins (never an average of bin rates); target at least 90 percent. Only queues measured by sensors that track people (T3) have tracks; elsewhere there is no value.
- **Pass and fail are per line and per queue, not the campaign's acceptance**: each line's and queue's pass or fail judges only the bins and tracers that could be compared, so one compared bin beside nine degraded ones can pass a line. They are not acceptance verdicts on their own: the campaign's verdict per criterion (ARV-104g) requires the campaign's targets of compared items (bins per line, tracer runs), otherwise it shows no data, and it shows the share of items excluded. Every line and queue result gives its compared and excluded counts for this.

### Desk-state agreement and nowcast error (F18, ARV-104f, Proposed)

The same comparison adds the last two pilot criteria and the ground-truth proof. As above, every result carries the zone profile version, results that cannot be used are listed and never replaced by an earlier revision, and the rules are proposals pending the product owner and the KPI annex (`../docs/product/decisions.md`, ARV-104f entry).

- **Which state Ariva showed for a desk minute**: the state the desk spent most of the minute in. Seconds Ariva did not account for count as `Unknown`; when `Unknown` ties with another state the minute is `Unknown`; a tie between two known states goes to the more active one (serving, idle, paused, closed). Example: 40 seconds serving and 20 idle is serving; 30 serving and 30 unknown is unknown.
- **Desk-state agreement**: observed minutes in which Ariva's state equals the observer's, over observed minutes; target at least 95 percent, per desk and over all desks of the campaign. A minute Ariva shows as `Unknown`, or for which it stored nothing usable, counts as a disagreement. Minutes from a degraded desk feed count like the others and are shown apart. Example: 19 agreeing minutes of 20 is 95 percent, a pass; 18 of 20 is a fail.
- **Observers and corrections**: each observer's state is their latest entry (the last correction, if they corrected it). Two observers of the same desk and minute count once; if they recorded different states the minute is set aside (the observation itself is uncertain) and counted. Because setting minutes aside could hide Ariva's mistakes (a second observer contradicting only the minutes Ariva got wrong would lift 16 of 20 to 16 of 16), Ariva also shows the strict agreement, with every minute set aside counted as a disagreement, and the campaign result (ARV-104g) uses it or caps the share set aside. A correction that cannot be read leaves that minute out for every observer. Beside the agreement Ariva shows whether it got "counted for throughput" right (idle or serving against paused or closed), which is what the wait estimate needs, and a table of which state was taken for which.
- **Nowcast error**: the wait Ariva predicted at the end of each minute is compared with the average wait the people who joined in the next minute actually had (final values only). The criterion is the median of the absolute errors over minutes whose actual wait was under 20 minutes; target within 2 minutes. Example: predictions of 7.5, 8, 9, 10 and 12 minutes against actual waits of 8, 8, 10, 13 and 12 give errors of 0.5, 0, 1, 3 and 0 minutes, a median of 0.5, a pass. Minutes with actual waits of 20 minutes or more, minutes without service (no prediction, with the reason), and minutes whose data was degraded, unknown, provisional or of another profile version are shown apart with their counts. Minutes where Ariva said there was no service while people still joined and waited under 20 minutes are counted separately, because the campaign result (ARV-104g) counts them against the prediction or caps their share. A prediction so large it is not a real wait counts as an error of a billion minutes, a fail. A prediction Ariva itself marked as an estimate (for example from exit counts alone) is judged like the others and also shown apart.
- **Ground-truth proof**: the same error for the sensor-only prediction Ariva computes beside the published one without AMAN's inputs (never shown elsewhere), side by side, with the minutes each covers and both on exactly the minutes both could be judged; the sensor-only minutes that used the sensor cycle time are shown apart. No target.
- **Who sees what**: desk-state results are border data (a desk and a minute could be matched with AMAN's records): only border roles of the site see them, never airport roles or the border-to-airport feed. The sensor-only prediction's figures appear only in the validation results.

### Availability (F18, ARV-118, Proposed)

Availability = available operating minutes / operating minutes, per local day, per week (Monday to Sunday) and over a range; no value when there are no operating minutes. Proposed pending the owner and the client's KPI annex (TC-83).

- **Operating minute**: inside the site's operating hours (the site operating calendar, [Administration guide](11-Administration-Guide.md) section 2b) and outside an announced maintenance window. A window counts only when it was recorded before it started; a site without a calendar is open around the clock.
- **Available minute**: every queue zone of the site's published zone profile had a live state younger than 150 seconds that had reached the minute (trailing real time by at most 180 seconds), and its minute is stored.
- **Not available**: the ledger records why: `StaleZone` (no fresh live state), `MissingMinute` (no stored minute), `StreamLag` (the live state behind), `NoPublishedZones`, and `NotObservedLive` for minutes decided after downtime of the job host (counted as not available: what cannot be proven live is not claimed).
- **Where**: `GET api/v1/sites/{siteCode}/availability?from=yyyy-MM-dd&to=yyyy-MM-dd` (local dates, at most 92 days; border shift supervisors, terminal duty managers and administrators). Each day, week and the total give the minutes recorded, operating, available, unavailable, unobserved, in maintenance and closed, the minutes per reason and two ratios; the answer repeats the pilot target (0.99).
- **Two ratios**: `availability` leaves maintenance minutes out of both sides (maintenance is not operating time); `availabilityMaintenanceAsUnavailable` counts them as operating and not available. A site could otherwise raise the first figure by declaring maintenance in advance over hours it expects to be weak, so both are reported side by side. Until the client decides (TC-83), read the pilot result against both; the KPI annex should either cap the maintenance minutes a site may declare per month or use the stricter ratio.

Example: a day open 06:00 to 22:00 (960 operating minutes) with a 60-minute announced maintenance window has 900 operating minutes; 891 available minutes give 0.99, and 891 of 960 (0.928) as the stricter ratio.

### Campaign verdicts (F18, ARV-104g2, Proposed)

A campaign's results give one verdict per pilot criterion: pass, fail or no data. The per-line, per-queue and per-desk verdicts above judge only the items that could be compared; the campaign verdict also needs enough of them. The rules are proposals pending the product owner and the client's KPI annex (`../docs/product/decisions.md`, ARV-104g2 entry).

- **Enough compared items, otherwise no data**: the campaign's own targets set how many (planned with the campaign, 20 bins per line and 30 tracer runs as placeholders until the annex agrees them). Count accuracy needs that many compared bins on every line in the campaign; the wait error and the bias need that many compared tracer runs over all queues; track completion needs that many `Good` bins on every queue of the campaign (a queue that recorded no track at all leaves the result at no data while another queue tracks people, since Ariva cannot tell a queue without tracking sensors from a tracker that failed; plan track completion on tracking queues); the desk-state agreement and the nowcast error need as many compared minutes as the bins per line hold (20 bins, 300 minutes). A line or queue below its count shows no data even when its compared items failed (its own fail stays visible beside it). Availability needs at least one operating minute.
- **Pass and fail**: a criterion judged per line or queue fails when any line or queue with enough compared items fails, shows no data when any lacks them, and passes otherwise. The others are judged once over the campaign.
- **Items set aside count against Ariva (strict, the default)**: a desk minute whose observers disagree, or whose observer entry cannot be read, counts as a disagreement (the strict agreement: 16 agreeing of 20 observed is 0.80, a fail, even when a second observer contradicts only the 4 wrong minutes); a minute whose published prediction gave no number while the next minute's entrants waited under 20 minutes counts as an error beyond the target in the median. The alternative the owner may choose is a cap: the figure over compared items, failing when more than 5 percent were set aside. Either way the share set aside is shown for every criterion.
- **Needs review**: the results flag what a person should check before relying on them: sensor outage records the comparison could not use (they could hide degraded bins), stored results that were refused or contradicted each other, placeholder targets, a campaign still running, results not final yet, a sensor-only prediction that could not be read, queues that recorded no track, and minutes after which people's waits are known but with no published prediction at all.
- **Beside the verdicts**: availability over the campaign's planned days (the ledger above), the calibration records of the queues' devices (method, sample, measured accuracy and wait error, verdict and date; never the notes or who recorded them), the zone profile version and its geometry hash, and the published prediction's coverage: the planned minutes after which people's waits are known, and how many of them had a published prediction.
- **Size**: a campaign of 50 queues over 31 days holds about 2.2 million stored minutes, more than the comparison takes at once; it is compared one queue at a time and pooled, which gives exactly the result of comparing everything at once (proved by tests). One computation per campaign runs at a time, within a timeout. Since ARV-104g a campaign holds at most 400 zone-days (queues times planned days, for example 12 queues for 31 days or 50 for 8) and one computation runs at a time on the API host, so that a computation (about 200 MB at that bound) fits beside sign-in and the live screens; the results list no minute, only counts per queue and desk.
- **Not in scope**: track completion is "not in scope" when no queue counted tracks (not tracked, or every tracker silent); the results then also raise the review flag for queues without tracks.
- **Frozen at close (ARV-104g)**: when the campaign closes its results are frozen as revision 1 with a SHA-256 hash of the stored document; later reads return exactly that document, and a recomputation (a validation manager with a second factor and a reason) adds revision 2, 3 and so on without changing the earlier ones. Each reader gets the sections its role allows: desk-state results only border roles, every observer's runs and offsets the site's validation managers, the nowcast without AMAN inputs only Validation.View holders.
