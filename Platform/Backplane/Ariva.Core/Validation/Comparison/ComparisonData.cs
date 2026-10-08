using Ariva.Core.Domain.Enums;
using Ariva.Core.Queueing;

namespace Ariva.Core.Validation.Comparison;

/// <summary>
/// The comparison engine's inputs once checked and indexed (ARV-104e, ARV-104f). Rows are read key by key, as their tables key
/// them, and a key is placed by its instant alone (ticks, as DateTime equality and timestamptz compare them). A row whose key
/// cannot be placed (null, a time not on its minute or bin or outside the years 2000 to 2999, a zone, line or desk outside the
/// scope, a count's bin or a desk state's minute outside the planned days) is left out and counted per kind
/// (<see cref="LeftOut"/>). Of the rows of one key, the highest revision decides before any value is checked (security
/// review, CWE-501): when any row of it holds a value that cannot be (a time that is not UTC, a count below 0 or above the
/// bounds, a wait or nowcast that is not a number, a state or reason that is not one, a desk minute holding more than a
/// minute) or its rows disagree, the key is unusable, reported in <see cref="LeftOutInputs.UnusableKeys"/>, and no earlier
/// revision takes its place. Tracer runs are grouped by run, join and batch before any is checked, and a batch with a run that
/// cannot be is not used at all. A quality interval is clamped to the years 2000 to 2999; one that cannot be placed makes its
/// zone Unknown throughout. Bounded (CWE-120, CWE-400): every list is counted as it is read, never by its own <c>Count</c>,
/// and beyond <see cref="MaxRows"/> rows of a kind, or the scope's limits (100 desks among them), nothing is compared. Pure:
/// no I/O, no clock.
/// </summary>
internal sealed class ComparisonData
{
    #region Constants

    /// <summary>Rows of one kind, at most.</summary>
    public const int MaxRows = 1_000_000;

    /// <summary>People, crossings or tracks of one stored bin, at most (sums over every row stay far inside 64 bits).</summary>
    public const long MaxPerBin = 1_000_000;

    /// <summary>A mean wait above a day is not a wait (the wait histogram stops at 24 hours, F7).</summary>
    public const double MaxWaitMinutes = 24 * 60;

    /// <summary>Windows of a scope, at most (a campaign plans 31 days).</summary>
    public const int MaxWindows = 62;

    /// <summary>15-minute bins of the windows, at most (31 days of 25 hours are 3,100).</summary>
    public const int MaxWindowBins = 3_200;

    public static readonly TimeSpan BinLength = ValidationCampaign.BinLength;

    /// <summary>
    /// The earliest instant a row or window may carry: Ariva wrote nothing before 2000. With <see cref="LatestUtc"/> it keeps
    /// every step of a bin, a minute or the sensitivity shift inside DateTime's range, whatever a row says.
    /// </summary>
    public static readonly DateTime EarliestUtc = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The instant every row and window must lie before (the year 3000).</summary>
    public static readonly DateTime LatestUtc = new(3000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The least sensor cycle time a shadow nowcast may carry (script 0045's check), minutes.</summary>
    public const double MinSensorCycleMinutes = 0.05;

    /// <summary>The largest sensor cycle time a shadow nowcast may carry (script 0045's check), minutes.</summary>
    public const double MaxSensorCycleMinutes = 60;

    #endregion

    #region Fields

    private readonly Dictionary<(string Zone, string Line, DateTime Bin), LineBinState> _lineBins = [];
    private readonly Dictionary<(string Zone, DateTime Minute), QueueMinuteRow> _minutes = [];
    private readonly HashSet<(string Zone, DateTime Minute)> _unusableMinutes = [];
    private readonly Dictionary<(string Zone, DateTime Start), QueueBinRow> _bins = [];
    private readonly Dictionary<(string Zone, DateTime Start), ZoneHealthBin> _health = [];
    // Per zone and quality, the union of the quality intervals as disjoint spans in time order, so whether one overlaps a
    // range is a binary search (CWE-400: never every interval for every item). The planned days likewise.
    private readonly Dictionary<(string Zone, BinQuality Quality), List<(DateTime From, DateTime To)>> _intervals = [];
    private readonly List<(DateTime From, DateTime To)> _planned;
    // Zones with a quality interval that cannot be placed: every stored result of the zone is Unknown.
    private readonly HashSet<string> _unplaced = new(StringComparer.Ordinal);
    private readonly List<UnusableKey> _unusable = [];
    // ARV-104f: desk minutes and shadow nowcasts by their tables' keys, and the keys whose rows cannot be used.
    private readonly Dictionary<(string Checkpoint, string Desk, DateTime Minute), DeskMinuteRow> _deskMinutes = [];
    private readonly HashSet<(string Checkpoint, string Desk, DateTime Minute)> _unusableDeskMinutes = [];
    private readonly Dictionary<(string Zone, DateTime Minute), ShadowMinuteRow> _shadows = [];
    private readonly HashSet<(string Zone, DateTime Minute)> _unusableShadows = [];
    private readonly Dictionary<(string Checkpoint, string Desk), ScopeDesk> _deskByCodes;

    #endregion

    #region Constructors

    private ComparisonData(ComparisonScope scope, ComparisonSettings settings, IReadOnlyList<DateTime> windowBins)
    {
        Version = scope.ProfileVersion;
        Settings = settings;
        Zones = [.. scope.Zones.OrderBy(z => z.Name, StringComparer.Ordinal)];
        Lines = [.. scope.Lines.OrderBy(l => l.QueueZone, StringComparer.Ordinal).ThenBy(l => l.Name, StringComparer.Ordinal)];
        Desks = [.. scope.Desks.OrderBy(d => d.CheckpointCode, StringComparer.Ordinal).ThenBy(d => d.DeskCode, StringComparer.Ordinal)];
        ZoneById = scope.Zones.ToDictionary(z => z.ZoneId);
        LineById = scope.Lines.ToDictionary(l => l.LineId);
        DeskById = scope.Desks.ToDictionary(d => d.DeskId);
        _deskByCodes = scope.Desks.ToDictionary(d => (d.CheckpointCode, d.DeskCode));
        ZoneNames = scope.Zones.Select(z => z.Name).ToHashSet(StringComparer.Ordinal);
        LineNames = scope.Lines.Select(l => (l.QueueZone, l.Name)).ToHashSet();
        WindowBins = windowBins;
        _planned = Merge(scope.Windows.Select(w => (w.FromUtc, w.ToUtc)));
    }

    #endregion

    #region Properties

    public int Version { get; }
    public ComparisonSettings Settings { get; }

    /// <summary>The zones in scope by name (ordinal).</summary>
    public IReadOnlyList<ScopeZone> Zones { get; }

    /// <summary>The lines in scope by queue zone and name (ordinal).</summary>
    public IReadOnlyList<ScopeLine> Lines { get; }

    /// <summary>The border desks in scope by checkpoint and desk code (ordinal).</summary>
    public IReadOnlyList<ScopeDesk> Desks { get; }

    public IReadOnlyDictionary<Guid, ScopeZone> ZoneById { get; }
    public IReadOnlyDictionary<Guid, ScopeLine> LineById { get; }
    public IReadOnlyDictionary<Guid, ScopeDesk> DeskById { get; }
    private HashSet<string> ZoneNames { get; }
    private HashSet<(string Zone, string Line)> LineNames { get; }

    /// <summary>The 15-minute bins of the planned days, ascending.</summary>
    public IReadOnlyList<DateTime> WindowBins { get; }

    /// <summary>The highest revision of each line, bin and observer's count, for the lines and bins whose every count can be used.</summary>
    public IReadOnlyList<ManualCountRow> Counts { get; private set; } = [];

    /// <summary>Per line, the bins not judged because an observer's count of them is unusable (<see cref="UnusableKeyKind.ManualCount"/>).</summary>
    public IReadOnlyDictionary<Guid, int> UnusableCountBins { get; private set; } = new Dictionary<Guid, int>();

    /// <summary>The usable tracer runs.</summary>
    public IReadOnlyList<TracerRunRow> Runs { get; private set; } = [];

    /// <summary>
    /// The highest revision of each desk, minute and observer's state, for the desk minutes whose every observer's state can be
    /// used (ARV-104f).
    /// </summary>
    public IReadOnlyList<DeskObservationRow> DeskObservations { get; private set; } = [];

    /// <summary>Per desk, the observed minutes not judged because an observer's state of them is unusable (<see cref="UnusableKeyKind.DeskObservation"/>).</summary>
    public IReadOnlyDictionary<Guid, int> UnusableObservationMinutes { get; private set; } = new Dictionary<Guid, int>();

    public LeftOutInputs LeftOut { get; private set; } = LeftOutInputs.None;

    #endregion

    #region Intake

    /// <summary>The checked inputs, or null with the reason nothing can be compared.</summary>
    public static ComparisonData Take(ComparisonInput input, ComparisonSettings settings, out ComparisonProblem? problem)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(settings);
        var scope = Checked(input.Scope, out problem);
        if (scope is null)
            return null;

        // Each list as it is read, at most MaxRows rows: its own Count may say something else (CWE-400, CWE-501).
        var counts = Bounded(input.ManualCounts, MaxRows);
        var runs = Bounded(input.TracerRuns, MaxRows);
        var lineBins = Bounded(input.LineBins, MaxRows);
        var minutes = Bounded(input.QueueMinutes, MaxRows);
        var queueBins = Bounded(input.QueueBins, MaxRows);
        var health = Bounded(input.HealthBins, MaxRows);
        var intervals = Bounded(input.QualityIntervals, MaxRows);
        var observations = Bounded(input.DeskObservations, MaxRows);
        var deskMinutes = Bounded(input.DeskMinutes, MaxRows);
        var shadows = Bounded(input.ShadowMinutes, MaxRows);
        var windowBins = WindowBinsOf(scope.Windows);
        if (counts is null || runs is null || lineBins is null || minutes is null || queueBins is null || health is null || intervals is null ||
            observations is null || deskMinutes is null || shadows is null || windowBins is null)
        {
            problem = ComparisonProblem.InputTooLarge;
            return null;
        }

        var data = new ComparisonData(scope, settings, windowBins);
        var leftOut = new LeftOutInputs(data.TakeCounts(counts), data.TakeRuns(runs), data.TakeLineBins(lineBins), data.TakeMinutes(minutes),
            data.TakeBins(queueBins), data.TakeHealth(health), data.TakeIntervals(intervals));
        var leftOutObservations = data.TakeDeskObservations(observations);
        var leftOutDeskMinutes = data.TakeDeskMinutes(deskMinutes);
        var leftOutShadows = data.TakeShadows(shadows);
        data.LeftOut = leftOut with
        {
            DeskObservations = leftOutObservations,
            DeskMinutes = leftOutDeskMinutes,
            ShadowMinutes = leftOutShadows,
            UnusableKeys = data._unusable.OrderBy(k => k.Kind).ThenBy(k => k.QueueZone, StringComparer.Ordinal).ThenBy(k => k.LineName, StringComparer.Ordinal)
                .ThenBy(k => k.DeskId).ThenBy(k => k.StartUtc).ThenBy(k => k.ObserverId).ToList()
        };
        return data;
    }

    /// <summary>The scope with its lists as read (at most their bounds), or null with the reason it cannot be compared.</summary>
    private static ComparisonScope Checked(ComparisonScope scope, out ComparisonProblem? problem)
    {
        problem = ComparisonProblem.InvalidScope;
        if (scope?.Zones is null || scope.Lines is null || scope.Windows is null || scope.ProfileVersion < 0)
            return null;
        var zones = Bounded(scope.Zones, ValidationCampaign.MaxZones);
        var lines = Bounded(scope.Lines, ValidationCampaign.MaxLines);
        var windows = Bounded(scope.Windows, MaxWindows);
        // A null list of desks is none (a campaign planned without desks).
        var desks = Bounded(scope.Desks, ValidationCampaign.MaxDesks);
        if (zones is null || lines is null || windows is null || desks is null)
        {
            problem = ComparisonProblem.InputTooLarge;
            return null;
        }

        if (!IsValid(zones, lines, windows) || !AreDesks(desks))
            return null;
        problem = null;
        return scope with { Zones = zones, Lines = lines, Windows = windows, Desks = desks };
    }

    /// <summary>Desks with an id and both codes, none twice by id or by codes (a desk_minute key names one desk).</summary>
    private static bool AreDesks(List<ScopeDesk> desks) =>
        desks.All(d => d is not null && d.DeskId != Guid.Empty && !string.IsNullOrEmpty(d.CheckpointCode) && !string.IsNullOrEmpty(d.DeskCode)) &&
        desks.Select(d => d.DeskId).Distinct().Count() == desks.Count &&
        desks.Select(d => (d.CheckpointCode, d.DeskCode)).Distinct().Count() == desks.Count;

    private static bool IsValid(List<ScopeZone> zones, List<ScopeLine> lines, List<UtcWindow> windows)
    {
        if (zones.Any(z => z is null || z.ZoneId == Guid.Empty || string.IsNullOrEmpty(z.Name)) ||
            zones.Select(z => z.ZoneId).Distinct().Count() != zones.Count ||
            zones.Select(z => z.Name).Distinct(StringComparer.Ordinal).Count() != zones.Count)
            return false;
        var names = zones.Select(z => z.Name).ToHashSet(StringComparer.Ordinal);
        if (lines.Any(l => l is null || l.LineId == Guid.Empty || string.IsNullOrEmpty(l.Name) || !Enum.IsDefined(l.Role) || l.QueueZone is null ||
                           !names.Contains(l.QueueZone)) ||
            lines.Select(l => l.LineId).Distinct().Count() != lines.Count ||
            lines.Select(l => (l.QueueZone, l.Name)).Distinct().Count() != lines.Count)
            return false;
        return windows.All(w => w is not null && AreTimes(w.FromUtc, w.ToUtc) && w.ToUtc > w.FromUtc);
    }

    /// <summary>
    /// The rows a list yields, read one by one; null when it yields more than <paramref name="max"/> (reading stops at the first
    /// row beyond). A list's own <c>Count</c> is never used: one that understates its rows would pass the bound and make the
    /// left-out counts negative. A null list yields none.
    /// </summary>
    private static List<T> Bounded<T>(IEnumerable<T> rows, int max)
    {
        var taken = new List<T>();
        if (rows is null)
            return taken;
        foreach (var row in rows)
        {
            if (taken.Count == max)
                return null;
            taken.Add(row);
        }

        return taken;
    }

    /// <summary>The distinct 15-minute UTC bins starting inside the windows, ascending; null beyond <see cref="MaxWindowBins"/>.</summary>
    private static List<DateTime> WindowBinsOf(IReadOnlyList<UtcWindow> windows)
    {
        long total = 0;
        foreach (var w in windows)
        {
            total += (w.ToUtc.Ticks - FirstBinAtOrAfter(w.FromUtc) + BinLength.Ticks - 1) / BinLength.Ticks;
            if (total > MaxWindowBins)
                return null;
        }

        var bins = new SortedSet<DateTime>();
        foreach (var w in windows)
        {
            for (var b = FirstBinAtOrAfter(w.FromUtc); b < w.ToUtc.Ticks; b += BinLength.Ticks)
                bins.Add(new DateTime(b, DateTimeKind.Utc));
        }

        return [.. bins];
    }

    private static long FirstBinAtOrAfter(DateTime value) => (value.Ticks + BinLength.Ticks - 1) / BinLength.Ticks * BinLength.Ticks;

    private int TakeCounts(List<ManualCountRow> rows)
    {
        // As captured (ARV-104a): a bin of a line in scope that starts on a planned day, counted by an observer, revision 1 up.
        var keyed = rows.Where(r => r is not null && LineById.ContainsKey(r.LineId) && IsBinTicks(r.BinStartUtc) && IsPlanned(r.BinStartUtc) &&
                                    r.ObserverId != Guid.Empty && r.Revision >= 1).ToList();
        var leftOut = rows.Count - keyed.Count;
        var latest = new List<ManualCountRow>();
        var unusable = new HashSet<(Guid Line, DateTime Bin)>();
        foreach (var key in keyed.GroupBy(r => (r.LineId, r.BinStartUtc, r.ObserverId)))
        {
            var (row, reason) = Decide(key, r => r.Revision, r => IsUtc(r.BinStartUtc) && ManualCount.AreCrossings(r.CrossingsIn, r.CrossingsOut), ref leftOut);
            if (row is not null)
            {
                latest.Add(row);
                continue;
            }

            var line = LineById[key.Key.LineId];
            unusable.Add((key.Key.LineId, key.Key.BinStartUtc));
            _unusable.Add(new UnusableKey(UnusableKeyKind.ManualCount, line.QueueZone, line.Name, AsUtc(key.Key.BinStartUtc), key.Key.ObserverId, reason));
        }

        // A line and bin with an observer's unusable count is not judged: N_manual would silently lose that observer, so the
        // other observers' counts of it are left out with it (and counted).
        Counts = [.. latest.Where(r => !unusable.Contains((r.LineId, r.BinStartUtc)))];
        UnusableCountBins = unusable.GroupBy(b => b.Line).ToDictionary(g => g.Key, g => g.Count());
        return leftOut + latest.Count - Counts.Count;
    }

    private int TakeRuns(List<TracerRunRow> rows)
    {
        // Only the ids place a run (its own, its batch's, its observer's); nothing else is checked before the conflicts are, so a
        // run that cannot be still counts against its twins and its batch (security review: a refused twin let the other be used).
        var present = rows.Where(r => r is not null).ToList();
        var keyed = present.Where(r => r.RunId != Guid.Empty && r.BatchId != Guid.Empty && r.ObserverId != Guid.Empty).ToList();
        var leftOut = rows.Count - keyed.Count;
        // A batch is captured whole (ARV-104b refuses a batch with any run that cannot be) under one observer and one offset: a
        // batch whose rows disagree on either, or hold a run that cannot be (as script 0048 checks), is not used at all. Every row
        // naming the batch counts, one with an empty run or observer id included (second re-check).
        var unusableBatches = present.Where(r => r.BatchId != Guid.Empty).GroupBy(r => r.BatchId)
            .Where(b => b.Select(r => (r.ObserverId, r.ClockOffsetMs)).Distinct().Count() > 1 || !b.All(IsUsableRun))
            .Select(b => b.Key)
            .ToHashSet();
        // One run per observer, tracer code and device join time (script 0048's ux_tracer_run_join, the join by its instant): a key
        // held by more than one run id, over every row (an empty id counts as one), is not one run measured twice but a conflict,
        // so none of its runs is used, even when one id's own rows disagree and leave nothing below (second re-check).
        var conflictingJoins = present.GroupBy(JoinKey).Where(j => j.Select(r => r.RunId).Distinct().Count() > 1).Select(j => j.Key).ToHashSet();
        // One row per run id: copies equal in every value count once, rows that disagree are a conflict (none used).
        var runs = Unique(keyed, r => r.RunId, ref leftOut);
        Runs = [.. runs.Where(r => !conflictingJoins.Contains(JoinKey(r)) && !unusableBatches.Contains(r.BatchId))];
        return leftOut + runs.Count - Runs.Count;
    }

    private static (Guid Observer, string TracerCode, long JoinedRawTicks) JoinKey(TracerRunRow r) => (r.ObserverId, r.TracerCode, r.JoinedRawUtc.Ticks);

    private bool IsUsableRun(TracerRunRow r) =>
        r is not null && r.RunId != Guid.Empty && r.BatchId != Guid.Empty && r.ObserverId != Guid.Empty && ZoneById.ContainsKey(r.ZoneId) &&
        TracerRun.IsTracerCode(r.TracerCode) && AreTimes(r.JoinedRawUtc, r.ExitedRawUtc, r.JoinedUtc, r.ExitedUtc) && IsPlanned(r.JoinedUtc) &&
        Math.Abs((long)r.ClockOffsetMs) <= TracerBatch.MaxClockOffsetMs &&
        r.JoinedRawUtc.Ticks - r.JoinedUtc.Ticks == r.ClockOffsetMs * TimeSpan.TicksPerMillisecond &&
        r.ExitedRawUtc.Ticks - r.ExitedUtc.Ticks == r.ClockOffsetMs * TimeSpan.TicksPerMillisecond &&
        TracerRun.IsDuration(r.JoinedUtc, r.ExitedUtc);

    private int TakeLineBins(List<LineBinCount> rows)
    {
        // Only Ariva's own crossings are N_system; a vendor's are a cross-check, never compared (ARV-113). The key is
        // line_minute_15m's: zone, line, bin and version.
        var keyed = rows.Where(r => r is not null && r.Source == LineCountSource.Ariva && LineNames.Contains((r.QueueZone, r.LineName)) &&
                                    IsBinTicks(r.BinStartUtc) && r.ProfileVersion >= 0).ToList();
        var leftOut = rows.Count - keyed.Count;
        foreach (var key in keyed.GroupBy(r => (r.QueueZone, r.LineName, r.BinStartUtc, r.ProfileVersion)))
        {
            var (row, reason) = Decide(key, _ => 0, r => IsUtc(r.BinStartUtc) && IsCount(r.CrossingsIn) && IsCount(r.CrossingsOut), ref leftOut);
            var bin = (key.Key.QueueZone, key.Key.LineName, key.Key.BinStartUtc);
            var state = _lineBins.GetValueOrDefault(bin);
            if (key.Key.ProfileVersion != Version)
            {
                // Another version counted the line in this bin too, whatever its values: the campaign version's count is partial.
                _lineBins[bin] = state with { OtherVersion = true };
            }
            else if (row is not null)
            {
                _lineBins[bin] = state with { In = row.CrossingsIn, Out = row.CrossingsOut };
            }
            else
            {
                _lineBins[bin] = state with { Unusable = true };
                _unusable.Add(new UnusableKey(UnusableKeyKind.LineBin, bin.QueueZone, bin.LineName, AsUtc(bin.BinStartUtc), null, reason));
            }
        }

        return leftOut;
    }

    private int TakeMinutes(List<QueueMinuteRow> rows)
    {
        var keyed = rows.Where(r => r is not null && ZoneNames.Contains(r.QueueZone) && IsInRange(r.MinuteUtc) && r.MinuteUtc.Ticks % TimeSpan.TicksPerMinute == 0)
            .ToList();
        var leftOut = rows.Count - keyed.Count;
        foreach (var key in keyed.GroupBy(r => (r.QueueZone, r.MinuteUtc)))
        {
            var (row, reason) = Decide(key, _ => 0, IsMinute, ref leftOut);
            if (row is not null)
            {
                _minutes[key.Key] = row;
                continue;
            }

            _unusableMinutes.Add(key.Key);
            _unusable.Add(new UnusableKey(UnusableKeyKind.QueueMinute, key.Key.QueueZone, null, AsUtc(key.Key.MinuteUtc), null, reason));
        }

        return leftOut;
    }

    private static bool IsMinute(QueueMinuteRow r) =>
        IsUtc(r.MinuteUtc) && r.ProfileVersion >= 0 && (r.Status is null || Enum.IsDefined(r.Status.Value)) && IsCount(r.Waits) && (r.Waits == 0) == (r.MeanWaitMinutes is null) &&
        (r.MeanWaitMinutes is null || (double.IsFinite(r.MeanWaitMinutes.Value) && r.MeanWaitMinutes >= 0 && r.MeanWaitMinutes <= MaxWaitMinutes)) &&
        (r.NowcastDegraded is null ? r.NowcastMinutes is null && r.NoService is null : IsNowcast(r.NowcastMinutes, r.NoService));

    /// <summary>
    /// A nowcast as <see cref="Nowcast.Compute"/> gives one (ARV-104f): exactly one of a number (finite, at least 0) and a
    /// reason (a <see cref="NoServiceReason"/> name, exactly). No upper bound: a published nowcast of hours is a real error of
    /// Ariva's and must count as one, never be refused out of the comparison; its error's size is capped at
    /// <see cref="NowcastErrors.MaxErrorMinutes"/>, so a value near <see cref="double.MaxValue"/> still fails and the statistics
    /// stay finite (security review of ARV-104f).
    /// </summary>
    private static bool IsNowcast(double? minutes, string noService) =>
        minutes is { } m ? noService is null && double.IsFinite(m) && m >= 0 : NowcastErrors.ReasonOf(noService) is not null;

    private int TakeShadows(List<ShadowMinuteRow> rows)
    {
        // Keyed like queue_minute: zone and minute, nothing else places the row.
        var keyed = rows.Where(r => r is not null && ZoneNames.Contains(r.QueueZone) && IsMinuteTicks(r.MinuteUtc)).ToList();
        var leftOut = rows.Count - keyed.Count;
        foreach (var key in keyed.GroupBy(r => (r.QueueZone, r.MinuteUtc)))
        {
            var (row, reason) = Decide(key, _ => 0, IsShadow, ref leftOut);
            if (row is not null)
            {
                _shadows[key.Key] = row;
                continue;
            }

            _unusableShadows.Add(key.Key);
            _unusable.Add(new UnusableKey(UnusableKeyKind.ShadowMinute, key.Key.QueueZone, null, AsUtc(key.Key.MinuteUtc), null, reason));
        }

        return leftOut;
    }

    /// <summary>As script 0043 and 0045 check it: a nowcast (<see cref="IsNowcast"/>) and a sensor cycle time of 0.05 to 60 minutes or none.</summary>
    private static bool IsShadow(ShadowMinuteRow r) =>
        IsUtc(r.MinuteUtc) && IsNowcast(r.NowcastMinutes, r.NoService) &&
        (r.SensorCycleMinutes is not { } c || (double.IsFinite(c) && c >= MinSensorCycleMinutes && c <= MaxSensorCycleMinutes));

    private int TakeDeskObservations(List<DeskObservationRow> rows)
    {
        // As captured (ARV-104b): a whole minute of a desk in scope on a planned day. The observer and the revision do not place
        // the row: a row of the desk minute without an observer id is a refused key of its own, so it spoils the desk minute
        // instead of being dropped before the others are judged (ARV-104e second re-check), and of an observer's rows the highest
        // revision decides, one outside 1 to 100 being a value refused there (never dropped before the grouping).
        var keyed = rows.Where(r => r is not null && DeskById.ContainsKey(r.DeskId) && IsMinuteTicks(r.MinuteUtc) && IsPlanned(r.MinuteUtc)).ToList();
        var leftOut = rows.Count - keyed.Count;
        var latest = new List<DeskObservationRow>();
        var unusable = new HashSet<(Guid Desk, DateTime Minute)>();
        foreach (var key in keyed.GroupBy(r => (r.DeskId, r.MinuteUtc, r.ObserverId)))
        {
            var (row, reason) = Decide(key, r => r.Revision, IsDeskObservation, ref leftOut);
            if (row is not null)
            {
                latest.Add(row);
                continue;
            }

            unusable.Add((key.Key.DeskId, key.Key.MinuteUtc));
            _unusable.Add(new UnusableKey(UnusableKeyKind.DeskObservation, null, null, AsUtc(key.Key.MinuteUtc), key.Key.ObserverId, reason, key.Key.DeskId));
        }

        // A desk minute with an observer's unusable state is not judged: the observed state would silently lose that observer,
        // so the other observers' states of it are left out with it (and counted), as for manual counts.
        DeskObservations = [.. latest.Where(r => !unusable.Contains((r.DeskId, r.MinuteUtc)))];
        UnusableObservationMinutes = unusable.GroupBy(m => m.Desk).ToDictionary(g => g.Key, g => g.Count());
        return leftOut + latest.Count - DeskObservations.Count;
    }

    private static bool IsDeskObservation(DeskObservationRow r) =>
        r.ObserverId != Guid.Empty && IsUtc(r.MinuteUtc) && r.Revision is >= 1 and <= DeskObservation.MaxRevisions && Enum.IsDefined(r.State);

    private int TakeDeskMinutes(List<DeskMinuteRow> rows)
    {
        // desk_minute's key: the desk (site/checkpoint/desk, here its codes) and the minute.
        var keyed = rows.Where(r => r is not null && r.CheckpointCode is not null && r.DeskCode is not null && _deskByCodes.ContainsKey((r.CheckpointCode, r.DeskCode)) &&
                                    IsMinuteTicks(r.MinuteUtc)).ToList();
        var leftOut = rows.Count - keyed.Count;
        foreach (var key in keyed.GroupBy(r => (r.CheckpointCode, r.DeskCode, r.MinuteUtc)))
        {
            var (row, reason) = Decide(key, _ => 0, IsDeskMinute, ref leftOut);
            if (row is not null)
            {
                _deskMinutes[key.Key] = row;
                continue;
            }

            _unusableDeskMinutes.Add(key.Key);
            _unusable.Add(new UnusableKey(UnusableKeyKind.DeskMinute, null, null, AsUtc(key.Key.MinuteUtc), null, reason,
                _deskByCodes[(key.Key.CheckpointCode, key.Key.DeskCode)].DeskId));
        }

        return leftOut;
    }

    /// <summary>As script 0018 checks it (each state 0 to 60 seconds) and a minute holds (at most 60 seconds in all).</summary>
    private static bool IsDeskMinute(DeskMinuteRow r) =>
        IsUtc(r.MinuteUtc) && DeskStateAgreement.Dominant(r.ClosedSeconds, r.IdleSeconds, r.ServingSeconds, r.PausedSeconds, r.UnknownSeconds) is not null;

    private int TakeBins(List<QueueBinRow> rows)
    {
        var keyed = rows.Where(r => r is not null && ZoneNames.Contains(r.QueueZone) && IsBinTicks(r.StartUtc) && r.Revision >= 0).ToList();
        var leftOut = rows.Count - keyed.Count;
        foreach (var key in keyed.GroupBy(r => (r.QueueZone, r.StartUtc)))
        {
            var (row, reason) = Decide(key, r => r.Revision, IsBin, ref leftOut);
            if (row is not null)
                _bins[key.Key] = row;
            else
                _unusable.Add(new UnusableKey(UnusableKeyKind.QueueBin, key.Key.QueueZone, null, AsUtc(key.Key.StartUtc), null, reason));
        }

        return leftOut;
    }

    private static bool IsBin(QueueBinRow r) =>
        IsUtc(r.StartUtc) && r.Length == BinLength && Enum.IsDefined(r.Status) && Enum.IsDefined(r.Quality) && r.ProfileVersion >= 0 && IsCount(r.Entries) && r.Abandoned >= 0 &&
        r.Abandoned <= r.Entries;

    private int TakeHealth(List<ZoneHealthBin> rows)
    {
        var keyed = rows.Where(r => r is not null && ZoneNames.Contains(r.QueueZone) && IsBinTicks(r.StartUtc) && r.Revision >= 0).ToList();
        var leftOut = rows.Count - keyed.Count;
        foreach (var key in keyed.GroupBy(r => (r.QueueZone, r.StartUtc)))
        {
            var (row, reason) = Decide(key, r => r.Revision, IsHealth, ref leftOut);
            if (row is not null)
                _health[key.Key] = row;
            else
                _unusable.Add(new UnusableKey(UnusableKeyKind.HealthBin, key.Key.QueueZone, null, AsUtc(key.Key.StartUtc), null, reason));
        }

        return leftOut;
    }

    private static bool IsHealth(ZoneHealthBin r) =>
        IsUtc(r.StartUtc) && r.Length == BinLength && Enum.IsDefined(r.Status) && r.ZoneProfileVersion >= 0 && IsCount(r.TracksEntered) && IsOutcome(r.TracksExited, r) &&
        IsOutcome(r.TracksAbandoned, r) && IsOutcome(r.TracksFragmented, r) && IsOutcome(r.TracksCensored, r);

    private int TakeIntervals(List<QualityInterval> rows)
    {
        // A Good interval says nothing; one of a zone out of scope touches nothing compared: both left out and counted.
        var inScope = rows.Where(r => r is not null && ZoneNames.Contains(r.QueueZone) && r.Quality != BinQuality.Good).ToList();
        var leftOut = rows.Count - inScope.Count;
        var placed = new List<(string Zone, BinQuality Quality, DateTime From, DateTime To)>();
        foreach (var r in inScope)
        {
            // Ends clamped to the years 2000 to 2999 (an outage still open may end at DateTime.MaxValue, AlertInputs does). One
            // that cannot be placed (a time not UTC, an end not after its start, an undefined quality) must never leave the
            // zone's results Good, so the zone is Unknown over the whole comparison (security review, CWE-501).
            if (r.Quality is (BinQuality.Degraded or BinQuality.Unknown) && IsIntervalEnd(r.FromUtc, DateTime.MinValue) &&
                IsIntervalEnd(r.ToUtc, DateTime.MaxValue) && r.ToUtc.Ticks > r.FromUtc.Ticks)
            {
                var (from, to) = (Clamp(r.FromUtc), Clamp(r.ToUtc));
                // Wholly before 2000 or from 3000 on: it overlaps nothing compared (left out and counted, the zone unchanged).
                if (to > from)
                    placed.Add((r.QueueZone, r.Quality, from, to));
                else
                    leftOut++;
                continue;
            }

            leftOut++;
            if (_unplaced.Add(r.QueueZone))
                _unusable.Add(new UnusableKey(UnusableKeyKind.QualityInterval, r.QueueZone, null, EarliestUtc, null, UnusableKeyReason.Refused));
        }

        foreach (var group in placed.GroupBy(i => (i.Zone, i.Quality)))
            _intervals[group.Key] = Merge(group.Select(i => (i.From, i.To)));
        return leftOut;
    }

    /// <summary>A UTC time, or the open end <paramref name="open"/> (DateTime.MinValue or MaxValue) whatever its kind.</summary>
    private static bool IsIntervalEnd(DateTime value, DateTime open) => IsUtc(value) || value.Ticks == open.Ticks;

    /// <summary>A time moved into the engine's range [<see cref="EarliestUtc"/>, <see cref="LatestUtc"/>], as UTC.</summary>
    private static DateTime Clamp(DateTime value) =>
        value.Ticks < EarliestUtc.Ticks ? EarliestUtc : value.Ticks > LatestUtc.Ticks ? LatestUtc : AsUtc(value);

    private static bool IsCount(long value) => value is >= 0 and <= MaxPerBin;

    private static bool IsOutcome(long value, ZoneHealthBin bin) => value >= 0 && value <= bin.TracksEntered;

    /// <summary>UTC instants from <see cref="EarliestUtc"/> to before <see cref="LatestUtc"/>.</summary>
    private static bool AreTimes(params DateTime[] values) => values.All(v => v.Kind == DateTimeKind.Utc && v >= EarliestUtc && v < LatestUtc);

    /// <summary>An instant from <see cref="EarliestUtc"/> to before <see cref="LatestUtc"/>, by its ticks alone (a key's place).</summary>
    private static bool IsInRange(DateTime value) => value.Ticks >= EarliestUtc.Ticks && value.Ticks < LatestUtc.Ticks;

    /// <summary>The start of a 15-minute bin on the quarter hour within the plausible times, by its ticks alone (a key's place).</summary>
    private static bool IsBinTicks(DateTime value) => IsInRange(value) && value.Ticks % BinLength.Ticks == 0;

    /// <summary>The start of a whole minute within the plausible times, by its ticks alone (a key's place).</summary>
    private static bool IsMinuteTicks(DateTime value) => IsInRange(value) && value.Ticks % TimeSpan.TicksPerMinute == 0;

    /// <summary>Whether a time says it is UTC: a value of a row, checked with its other values once the key is placed.</summary>
    private static bool IsUtc(DateTime value) => value.Kind == DateTimeKind.Utc;

    private static DateTime AsUtc(DateTime value) => new(value.Ticks, DateTimeKind.Utc);

    /// <summary>Whether an instant lies inside a planned day's window [from, to).</summary>
    private bool IsPlanned(DateTime value)
    {
        var first = FirstEndingAfter(_planned, value);
        return first < _planned.Count && _planned[first].From <= value;
    }

    /// <summary>
    /// The row that decides a key, from its rows of every revision: the highest revision, held by one row or by copies equal in
    /// every value (the copies counted), each of whose values can be. Otherwise the key is unusable (no row, and the reason) and
    /// every row of that revision is counted; no earlier revision takes its place. Every row of the revision is checked, not one
    /// of them: copies equal but for a time's kind (DateTime equality ignores it) are refused whatever their order. Earlier
    /// revisions are superseded and never used; of them, as of the highest, the copies and the rows refused or disagreeing are
    /// counted.
    /// </summary>
    private static (T Row, UnusableKeyReason Reason) Decide<T>(IEnumerable<T> rows, Func<T, int> revision, Func<T, bool> isValid, ref int leftOut)
        where T : class
    {
        (T Row, UnusableKeyReason Reason) decided = default;
        var highest = true;
        foreach (var group in rows.GroupBy(revision).OrderByDescending(g => g.Key))
        {
            var first = group.First();
            var count = group.Count();
            UnusableKeyReason? problem = !group.All(r => EqualityComparer<T>.Default.Equals(r, first)) ? UnusableKeyReason.Conflicting
                : !group.All(isValid) ? UnusableKeyReason.Refused
                : null;
            leftOut += problem is null ? count - 1 : count;
            if (highest)
                decided = problem is { } reason ? (null, reason) : (first, default);
            highest = false;
        }

        return decided;
    }

    /// <summary>
    /// The rows whose key is held once, or by copies equal in every value (one kept, the copies counted); a key held by rows that
    /// disagree is not used at all (every one counted): which of them is true cannot be told.
    /// </summary>
    private static List<T> Unique<TKey, T>(List<T> rows, Func<T, TKey> key, ref int leftOut)
    {
        var kept = new List<T>();
        foreach (var group in rows.GroupBy(key))
        {
            var first = group.First();
            var count = group.Count();
            if (group.All(r => EqualityComparer<T>.Default.Equals(r, first)))
            {
                kept.Add(first);
                leftOut += count - 1;
            }
            else
            {
                leftOut += count;
            }
        }

        return kept;
    }

    /// <summary>The union of spans [from, to) as disjoint spans in time order (spans that overlap or touch are merged).</summary>
    private static List<(DateTime From, DateTime To)> Merge(IEnumerable<(DateTime From, DateTime To)> spans)
    {
        var merged = new List<(DateTime From, DateTime To)>();
        foreach (var (from, to) in spans.OrderBy(s => s.From))
        {
            if (merged.Count > 0 && from <= merged[^1].To)
                merged[^1] = (merged[^1].From, to > merged[^1].To ? to : merged[^1].To);
            else
                merged.Add((from, to));
        }

        return merged;
    }

    /// <summary>The index of the first of disjoint spans in time order that ends after <paramref name="value"/> (the count when none does).</summary>
    private static int FirstEndingAfter(List<(DateTime From, DateTime To)> spans, DateTime value)
    {
        int low = 0, high = spans.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (spans[middle].To > value)
                high = middle;
            else
                low = middle + 1;
        }

        return low;
    }

    #endregion

    #region Lookups

    /// <summary>The bin a time falls in (15-minute UTC bins, as line_minute_15m buckets them).</summary>
    public static DateTime BinOf(DateTime value) => new(value.Ticks - value.Ticks % BinLength.Ticks, DateTimeKind.Utc);

    /// <summary>The later of two standings in <see cref="ComparisonStanding"/>'s order.</summary>
    public static ComparisonStanding Worst(ComparisonStanding a, ComparisonStanding b) => (ComparisonStanding)Math.Max((int)a, (int)b);

    /// <summary>A zone's stored bin; null when none was stored or its latest revision is unusable.</summary>
    public QueueBinRow BinAt(string zone, DateTime start) => _bins.GetValueOrDefault((zone, start));

    /// <summary>A zone's stored minute; null when none was stored or its row is unusable (<see cref="IsUnusableMinute"/>).</summary>
    public QueueMinuteRow MinuteAt(string zone, DateTime minute) => _minutes.GetValueOrDefault((zone, minute));

    /// <summary>Whether a zone's minute was stored but its row is refused or conflicting.</summary>
    public bool IsUnusableMinute(string zone, DateTime minute) => _unusableMinutes.Contains((zone, minute));

    /// <summary>A zone's stored health bin; null when none was stored or its latest revision is unusable.</summary>
    public ZoneHealthBin HealthAt(string zone, DateTime start) => _health.GetValueOrDefault((zone, start));

    /// <summary>A line's crossings in a bin under the campaign's version (0 when none were stored) and what else holds the bin.</summary>
    public LineBinState LineBinAt(string zone, string line, DateTime bin) => _lineBins.GetValueOrDefault((zone, line, bin));

    /// <summary>A desk's stored minute; null when none was stored or its rows are unusable (<see cref="IsUnusableDeskMinute"/>).</summary>
    public DeskMinuteRow DeskMinuteAt(ScopeDesk desk, DateTime minute) => _deskMinutes.GetValueOrDefault((desk.CheckpointCode, desk.DeskCode, minute));

    /// <summary>Whether a desk's minute was stored but its rows are refused or conflicting.</summary>
    public bool IsUnusableDeskMinute(ScopeDesk desk, DateTime minute) => _unusableDeskMinutes.Contains((desk.CheckpointCode, desk.DeskCode, minute));

    /// <summary>A zone's stored shadow nowcast of a minute; null when none was stored or its rows are unusable (<see cref="IsUnusableShadow"/>).</summary>
    public ShadowMinuteRow ShadowAt(string zone, DateTime minute) => _shadows.GetValueOrDefault((zone, minute));

    /// <summary>Whether a zone's shadow nowcast of a minute was stored but its rows are refused or conflicting.</summary>
    public bool IsUnusableShadow(string zone, DateTime minute) => _unusableShadows.Contains((zone, minute));

    /// <summary>
    /// The minutes of the planned days whose nowcast is compared (ARV-104f), by zone (ordinal) and minute, in UTC: every stored
    /// queue minute with a live part, and, failing closed, every queue minute whose rows cannot be used (whether it held a
    /// nowcast cannot be told) and every minute with a stored shadow nowcast, usable or not.
    /// </summary>
    public IReadOnlyList<(string Zone, DateTime Minute)> NowcastKeys()
    {
        var keys = new HashSet<(string Zone, DateTime Minute)>();
        keys.UnionWith(_minutes.Where(m => m.Value.NowcastDegraded is not null).Select(m => m.Key));
        keys.UnionWith(_unusableMinutes);
        keys.UnionWith(_shadows.Keys);
        keys.UnionWith(_unusableShadows);
        return [.. keys.Where(k => IsPlanned(k.Minute)).Select(k => (k.Zone, AsUtc(k.Minute))).OrderBy(k => k.Zone, StringComparer.Ordinal).ThenBy(k => k.Item2)];
    }

    /// <summary>
    /// How the stored results of a zone stand over [<paramref name="fromUtc"/>, <paramref name="toUtc"/>): every 15-minute bin
    /// that overlaps it must have a stored bin (Unknown otherwise) of the campaign's version (OtherVersion otherwise), final when
    /// <paramref name="requireFinal"/> (Provisional otherwise), and its quality counts (Degraded, Unknown); so does every quality
    /// interval that overlaps the range, and a zone with an interval that cannot be placed is Unknown throughout. The latest
    /// standing in <see cref="ComparisonStanding"/>'s order wins.
    /// </summary>
    public ComparisonStanding Over(string zone, DateTime fromUtc, DateTime toUtc, bool requireFinal)
    {
        var standing = ComparisonStanding.Good;
        for (var start = BinOf(fromUtc); start < toUtc; start += BinLength)
        {
            standing = Worst(standing, BinAt(zone, start) switch
            {
                null => ComparisonStanding.Unknown,
                { ProfileVersion: var v } when v != Version => ComparisonStanding.OtherVersion,
                { Status: not BinStatus.Final } when requireFinal => ComparisonStanding.Provisional,
                var bin => Of(bin.Quality)
            });
        }

        if (Overlaps(zone, BinQuality.Degraded, fromUtc, toUtc))
            standing = Worst(standing, ComparisonStanding.Degraded);
        if (Overlaps(zone, BinQuality.Unknown, fromUtc, toUtc) || _unplaced.Contains(zone))
            standing = Worst(standing, ComparisonStanding.Unknown);
        return standing;
    }

    /// <summary>Whether a span of the zone's intervals of that quality overlaps [<paramref name="fromUtc"/>, <paramref name="toUtc"/>).</summary>
    private bool Overlaps(string zone, BinQuality quality, DateTime fromUtc, DateTime toUtc)
    {
        if (!_intervals.TryGetValue((zone, quality), out var spans))
            return false;
        // The first span ending after the range starts; the spans are disjoint and in time order, so only it can overlap first.
        var first = FirstEndingAfter(spans, fromUtc);
        return first < spans.Count && spans[first].From < toUtc;
    }

    private static ComparisonStanding Of(BinQuality quality) =>
        quality switch
        {
            BinQuality.Degraded => ComparisonStanding.Degraded,
            BinQuality.Unknown => ComparisonStanding.Unknown,
            _ => ComparisonStanding.Good
        };

    #endregion
}

/// <summary>
/// A line's Ariva crossings in one bin under the campaign's version (0 when no row was stored), whether another version also
/// counted the line in that bin, and whether the campaign version's row is unusable (refused or conflicting: no crossings known).
/// </summary>
internal readonly record struct LineBinState(long In, long Out, bool OtherVersion, bool Unusable);
