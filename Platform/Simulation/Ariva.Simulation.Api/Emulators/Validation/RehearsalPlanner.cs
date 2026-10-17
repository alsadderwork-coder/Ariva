using System.Globalization;

namespace Ariva.Simulation.Api.Emulators.Validation;

/// <summary>A line of a running campaign as Ariva shows it to an observer (<c>GET .../validation/capture/campaigns</c>).</summary>
public sealed record CaptureLine(Guid Id, string Name, string Role, string QueueZone);

/// <summary>A queue zone tracers may run in.</summary>
public sealed record CaptureZone(Guid Id, string Name);

/// <summary>A border desk an observer logs.</summary>
public sealed record CaptureDesk(Guid Id, string Checkpoint, string Code);

/// <summary>
/// A running campaign as Ariva shows it to one observer (ARV-104a, ARV-104b): what to count and when, the zones tracers run in,
/// and the desks to log (none, with <see cref="DesksIncluded"/> false, for an account that sees no border desk). Read from
/// Ariva's answer; every value is Ariva's, never the rehearsal request's.
/// </summary>
public sealed record CaptureCampaign(
    Guid Id,
    string SiteCode,
    string Name,
    string TimeZoneId,
    IReadOnlyList<string> Days,
    int BinMinutes,
    IReadOnlyList<CaptureLine> Lines,
    IReadOnlyList<CaptureZone> Zones,
    bool DesksIncluded,
    IReadOnlyList<CaptureDesk> Desks,
    int MaxClockOffsetSeconds);

/// <summary>The part of the scenario day a rehearsal plays: clock minutes [<see cref="FromMinute"/>, <see cref="ToMinute"/>).</summary>
public sealed record RehearsalWindow(int FromMinute, int ToMinute);

/// <summary>One manual count to send: the observer (index into the rehearsal's observers), the line, the UTC bin and the crossings.</summary>
internal sealed record PlannedCount(int Observer, Guid LineId, DateTime BinStartUtc, int CrossingsIn, int CrossingsOut, int TrueIn, int TrueOut);

/// <summary>One tracer run to send: its observer, zone, label (T-01 to T-999, never a name) and its times on the true clock.</summary>
internal sealed record PlannedRun(int Observer, Guid ZoneId, string TracerCode, DateTime JoinedUtc, DateTime ExitedUtc, double TrueWaitMinutes);

/// <summary>One desk to log in a batch: its 15 states (null for a minute not observed).</summary>
internal sealed record PlannedDesk(Guid DeskId, IReadOnlyList<string> States);

/// <summary>One desk batch to send: its observer, the UTC bin and at most 20 desks.</summary>
internal sealed record PlannedDeskBatch(int Observer, DateTime BinStartUtc, IReadOnlyList<PlannedDesk> Desks);

/// <summary>
/// What a rehearsal plans and leaves out: the lines, zones and desks in scope and those the scenario has no truth for (a line
/// that is not an entry or exit line of a scenario queue, a zone or desk the scenario does not have), the desks no observer
/// may log (no account sees them), the lines the count error applies to, and what the observers miss by the slips.
/// </summary>
public sealed record PlanSummary(
    int Lines,
    int LinesWithoutTruth,
    int Zones,
    int ZonesWithoutTruth,
    int Desks,
    int DesksWithoutTruth,
    int DesksNotShown,
    int CountErrorLines,
    int CountBinsMissed,
    int DeskBatchesMissed,
    int DeskMinutesMissed);

/// <summary>Everything a rehearsal sends, in the order it sends it.</summary>
internal sealed record RehearsalPlan(
    IReadOnlyList<PlannedCount> Counts,
    IReadOnlyList<PlannedRun> Runs,
    IReadOnlyList<PlannedDeskBatch> DeskBatches,
    PlanSummary Summary);

/// <summary>
/// Turns a scenario day's truth into what the rehearsal's observers report (ARV-104i), deterministically:
/// <list type="bullet">
/// <item>Counts: one per line in scope and 15-minute UTC bin that lies wholly in the window, by the observer the line is given to
/// (lines in order, round robin): an entry line's people in, an exit line's people out, with the count error where it applies;
/// an observer's slip misses the bin.</item>
/// <item>Tracers: per queue zone, one tracer every <c>tracerEveryMinutes</c> from the window's second minute (zones staggered by a
/// minute): the tracer joins at the middle of the first minute from there whose entrants have a realised wait and waits their
/// mean wait (what Ariva compares a tracer with, F18), with the tracer error; labels T-01 upwards across the rehearsal, given to
/// the observers in turn.</item>
/// <item>Desk logs: the desks shown to observers who see border desks, given to them in turn; per bin and observer one batch of at
/// most 20 desks with each minute's state, a slipped minute left null and a slipped bin not sent.</item>
/// </list>
/// Lines, zones and desks the scenario does not know are counted, not guessed. Pure: no I/O, no clock.
/// </summary>
internal static class RehearsalPlanner
{
    /// <summary>Desks per desk batch (Ariva's DeskObservationBatch.MaxDesks).</summary>
    public const int MaxDesksPerBatch = 20;

    /// <summary>Tracer labels T-01 to T-999.</summary>
    public const int MaxTracers = 999;

    private static readonly TimeSpan Bin = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The plan of a rehearsal. <paramref name="observers"/> counts the observers who may capture for the campaign (indexes 0 to
    /// n - 1); <paramref name="deskObservers"/> lists those of them shown the campaign's desks.
    /// </summary>
    public static RehearsalPlan Plan(ValidationTruth truth, CaptureCampaign campaign, int observers, IReadOnlyList<int> deskObservers, RehearsalWindow window,
        ObserverErrors errors, uint seed, int tracerEveryMinutes)
    {
        ArgumentNullException.ThrowIfNull(truth);
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentOutOfRangeException.ThrowIfLessThan(observers, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(tracerEveryMinutes, 1);
        errors ??= ObserverErrors.None;
        deskObservers ??= [];

        var fromUtc = truth.At(window.FromMinute);
        var toUtc = truth.At(window.ToMinute);
        var bins = BinsWithin(fromUtc, toUtc);

        var (counts, lines, linesWithoutTruth, errorLines, binsMissed) = Counts(truth, campaign, observers, bins, errors, seed);
        var (runs, zones, zonesWithoutTruth) = Runs(truth, campaign, observers, fromUtc, toUtc, errors, tracerEveryMinutes);
        var (batches, desks, desksWithoutTruth, desksNotShown, batchesMissed, minutesMissed) = DeskBatches(truth, campaign, deskObservers, bins, errors, seed);

        return new RehearsalPlan(counts, runs, batches,
            new PlanSummary(lines, linesWithoutTruth, zones, zonesWithoutTruth, desks, desksWithoutTruth, desksNotShown, errorLines, binsMissed, batchesMissed, minutesMissed));
    }

    /// <summary>The 15-minute UTC bins (on the quarter hour) that lie wholly in [<paramref name="fromUtc"/>, <paramref name="toUtc"/>).</summary>
    public static IReadOnlyList<DateTime> BinsWithin(DateTime fromUtc, DateTime toUtc)
    {
        var first = fromUtc.Ticks % Bin.Ticks == 0 ? fromUtc.Ticks : fromUtc.Ticks - (fromUtc.Ticks % Bin.Ticks) + Bin.Ticks;
        var bins = new List<DateTime>();
        for (var start = first; start + Bin.Ticks <= toUtc.Ticks; start += Bin.Ticks)
            bins.Add(new DateTime(start, DateTimeKind.Utc));
        return bins;
    }

    #region Counts

    private static (List<PlannedCount> Counts, int Lines, int WithoutTruth, int ErrorLines, int Missed) Counts(ValidationTruth truth, CaptureCampaign campaign,
        int observers, IReadOnlyList<DateTime> bins, ObserverErrors errors, uint seed)
    {
        var counts = new List<PlannedCount>();
        var lines = (campaign.Lines ?? []).Where(l => l is not null)
            .OrderBy(l => l.QueueZone, StringComparer.Ordinal).ThenBy(l => l.Name, StringComparer.Ordinal).ThenBy(l => l.Id).ToList();
        int withoutTruth = 0, errorLines = 0, missed = 0, given = 0;
        foreach (var line in lines)
        {
            var q = truth.QueueOf(line.QueueZone);
            var entry = string.Equals(line.Role, "Entry", StringComparison.Ordinal);
            var exit = string.Equals(line.Role, "Exit", StringComparison.Ordinal);
            if (q is null || !(entry || exit))
            {
                withoutTruth++;
                continue;
            }

            var observer = given++ % observers;
            var withError = errors.CountErrorApplies(line.Name);
            if (withError)
                errorLines++;
            foreach (var bin in bins)
            {
                if (ObserverSlips.Missed(seed, "count", Key(line.Id, bin), errors.MissedBinsPercent))
                {
                    missed++;
                    continue;
                }

                var people = entry ? truth.Entries(q.Value, bin, bin + Bin).Count : truth.Exits(q.Value, bin, bin + Bin).Count;
                var reported = withError ? ObserverSlips.Count(people, errors.CountErrorPercent) : people;
                counts.Add(entry
                    ? new PlannedCount(observer, line.Id, bin, reported, 0, people, 0)
                    : new PlannedCount(observer, line.Id, bin, 0, reported, 0, people));
            }
        }

        return (counts, lines.Count, withoutTruth, errorLines, missed);
    }

    #endregion

    #region Tracers

    private static (List<PlannedRun> Runs, int Zones, int WithoutTruth) Runs(ValidationTruth truth, CaptureCampaign campaign, int observers, DateTime fromUtc,
        DateTime toUtc, ObserverErrors errors, int every)
    {
        var runs = new List<PlannedRun>();
        var zones = (campaign.Zones ?? []).Where(z => z is not null).OrderBy(z => z.Name, StringComparer.Ordinal).ThenBy(z => z.Id).ToList();
        var firstMinute = CeilingMinute(fromUtc);
        var withoutTruth = 0;
        var known = 0;
        foreach (var zone in zones)
        {
            var q = truth.QueueOf(zone.Name);
            if (q is null)
            {
                withoutTruth++;
                continue;
            }

            // The window's second minute, each zone a minute after the one before, so the zones' tracers do not all join at once.
            var start = firstMinute.AddMinutes(1 + (known++ % every));
            for (var slot = start; slot.AddMinutes(1) <= toUtc && runs.Count < MaxTracers; slot = slot.AddMinutes(every))
            {
                // The first minute of the slot whose entrants have a realised wait (a minute nobody entered has none).
                for (var minute = slot; minute < slot.AddMinutes(every) && minute.AddMinutes(1) <= toUtc; minute = minute.AddMinutes(1))
                {
                    var waits = truth.EntrantWaits(q.Value, minute);
                    if (waits.MeanWaitMinutes is not { } mean || mean <= 0)
                        continue;
                    var joined = minute.AddSeconds(30);
                    var wait = ObserverSlips.TracerWait(TimeSpan.FromMilliseconds(Math.Round(mean * 60_000, MidpointRounding.AwayFromZero)),
                        errors.TracerErrorMinutes, errors.TracerErrorPercent);
                    var code = TracerCode(runs.Count + 1);
                    runs.Add(new PlannedRun(runs.Count % observers, zone.Id, code, joined, joined + wait, mean));
                    break;
                }
            }
        }

        return (runs, zones.Count, withoutTruth);
    }

    /// <summary>The tracer label of the n-th tracer (1 to 999): T-01 to T-99, then T-100 to T-999.</summary>
    public static string TracerCode(int n) =>
        n is < 1 or > MaxTracers
            ? throw new ArgumentOutOfRangeException(nameof(n))
            : string.Create(CultureInfo.InvariantCulture, $"T-{n:00}");

    #endregion

    #region Desks

    private static (List<PlannedDeskBatch> Batches, int Desks, int WithoutTruth, int NotShown, int BatchesMissed, int MinutesMissed) DeskBatches(ValidationTruth truth,
        CaptureCampaign campaign, IReadOnlyList<int> deskObservers, IReadOnlyList<DateTime> bins, ObserverErrors errors, uint seed)
    {
        var batches = new List<PlannedDeskBatch>();
        var shown = campaign.DesksIncluded ? (campaign.Desks ?? []).Where(d => d is not null).ToList() : [];
        var desks = shown.OrderBy(d => d.Checkpoint, StringComparer.Ordinal).ThenBy(d => d.Code, StringComparer.Ordinal).ThenBy(d => d.Id)
            .Select(d => (Desk: d, Server: truth.DeskOf(d.Code))).ToList();
        var known = desks.Where(d => d.Server is not null).ToList();
        var withoutTruth = desks.Count - known.Count;
        if (deskObservers.Count == 0)
            return (batches, desks.Count, withoutTruth, known.Count, 0, 0);

        int batchesMissed = 0, minutesMissed = 0;
        foreach (var bin in bins)
        {
            for (var o = 0; o < deskObservers.Count; o++)
            {
                var observer = deskObservers[o];
                var mine = known.Where((_, i) => i % deskObservers.Count == o).ToList();
                if (mine.Count == 0)
                    continue;
                if (ObserverSlips.Missed(seed, "desk-bin", string.Create(CultureInfo.InvariantCulture, $"{observer}|{Stamp(bin)}"), errors.MissedBinsPercent))
                {
                    batchesMissed++;
                    continue;
                }

                var logged = new List<PlannedDesk>();
                foreach (var (desk, server) in mine)
                {
                    var states = new string[15];
                    for (var m = 0; m < 15; m++)
                    {
                        var minute = bin.AddMinutes(m);
                        if (ObserverSlips.Missed(seed, "desk-minute", Key(desk.Id, minute), errors.MissedMinutesPercent))
                        {
                            minutesMissed++;
                            continue;
                        }

                        states[m] = truth.DeskState(server.GetValueOrDefault().Q, server.GetValueOrDefault().K, minute)?.ToString();
                    }

                    if (states.Any(s => s is not null))
                        logged.Add(new PlannedDesk(desk.Id, states));
                }

                foreach (var chunk in logged.Chunk(MaxDesksPerBatch))
                    batches.Add(new PlannedDeskBatch(observer, bin, chunk));
            }
        }

        return (batches, desks.Count, withoutTruth, 0, batchesMissed, minutesMissed);
    }

    #endregion

    private static DateTime CeilingMinute(DateTime utc) =>
        utc.Ticks % TimeSpan.TicksPerMinute == 0 ? utc : new DateTime(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMinute) + TimeSpan.TicksPerMinute, DateTimeKind.Utc);

    private static string Key(Guid id, DateTime utc) => string.Create(CultureInfo.InvariantCulture, $"{id:N}|{Stamp(utc)}");

    /// <summary>A UTC instant as the capture API takes it: ISO 8601 to the millisecond, ending in Z.</summary>
    public static string Stamp(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
