namespace Ariva.Core.Validation.Comparison;

/// <summary>
/// The published nowcast's coverage of one queue zone over the planned days, or of every zone (<see cref="QueueZone"/> null),
/// ARV-104g2: the minutes starting in the planned days, those whose next minute holds a final realised wait of the campaign's
/// version (the minutes the nowcast error could judge), and of those the minutes whose own row published a number, a no-service
/// reason, or nothing at all (no row, no live part, or a row that cannot be used); the share is published over minutes with a
/// realised wait (null with none). A queue-level aggregate with no desk and no person.
/// </summary>
public sealed record NowcastCoverage(string QueueZone, int PlannedMinutes, int WithRealisedWait, int Published, int NoService, int Missing, double? Share)
{
    /// <summary>The coverage over several zones: the counts added, the share recomputed (never a mean of shares).</summary>
    public static NowcastCoverage Sum(IEnumerable<NowcastCoverage> zones)
    {
        ArgumentNullException.ThrowIfNull(zones);
        int planned = 0, withWait = 0, published = 0, noService = 0, missing = 0;
        foreach (var zone in zones)
        {
            planned += zone.PlannedMinutes;
            withWait += zone.WithRealisedWait;
            published += zone.Published;
            noService += zone.NoService;
            missing += zone.Missing;
        }

        return new NowcastCoverage(null, planned, withWait, published, noService, missing, withWait > 0 ? published / (double)withWait : null);
    }
}

/// <summary>One queue zone's comparison (its nowcast part is what the pooled result keeps) and its nowcast coverage (null when nothing was compared).</summary>
public sealed record ZoneSlice(ComparisonResult Result, NowcastCoverage Coverage);

/// <summary>A campaign's inputs as the slices read them: the base comparison and one input per queue zone in scope (ordinal order).</summary>
public sealed record SlicedInput(ComparisonInput Base, IReadOnlyList<ComparisonInput> Zones);

/// <summary>
/// The comparison of a campaign in slices that stay within the engine's bounds (ARV-104g2; security reviews of ARV-104e and
/// ARV-104f: the nowcast error needs every queue minute of the planned days, about 2.2 million for 50 zones over 31 days, beyond
/// the 1,000,000 rows of a kind <see cref="ComparisonData"/> takes). Only queue minutes and shadow nowcasts grow with every
/// minute, and only the nowcast error reads every one of them, so:
/// <list type="bullet">
/// <item>the base comparison takes the campaign's full scope (<see cref="ComparisonScope.Of"/>: a subset scope drops whole
/// batches that span zones) with every input except those two: of the queue minutes only the ones the tracer matching rule
/// can read (<see cref="TracerMinutes"/>: the join minute and two minutes either side, which covers the neighbour and the
/// sensitivity shift of at most a minute), and no shadow nowcast. Its counts, tracers, offsets, track completion and desks are
/// the whole comparison's;</item>
/// <item>one comparison per queue zone (<see cref="ZoneScope"/>: that zone alone, the planned days, no lines, no desks) takes
/// the zone's queue minutes, shadow nowcasts, queue bins and quality intervals. Its nowcast minutes and its zone summary are
/// the whole comparison's for that zone, because the nowcast error reads nothing of another zone;</item>
/// <item><see cref="Pool"/> keeps the base and replaces its nowcast part with the zones' minutes and summaries in zone order,
/// the overall summary computed again over those minutes in the same order (sums and medians as the whole gives them), and the
/// left-out queue minutes and shadow nowcasts (counts and unusable keys) with the zones'.</item>
/// </list>
/// Pooled, the result is the one <see cref="ValidationComparison.Compare"/> gives over every row at once, value for value,
/// whenever that comparison is within the bounds (ComparisonSlicesTests and the generated campaigns of ComparisonPropertyTests);
/// beyond them only the slices can compare. A scope without zones is not sliced (the base is the whole). Pure: no I/O.
/// </summary>
public static class ComparisonSlices
{
    #region Constants

    /// <summary>
    /// How far, in whole minutes either side of a tracer's join minute, the matching rule reads: the neighbour minute (1) and the
    /// join shifted by the sensitivity shift (at most a minute, <see cref="ComparisonSettings.Problems"/>) and its neighbour (2).
    /// </summary>
    public const int TracerMinuteReach = 2;

    #endregion

    #region Slices

    /// <summary>
    /// The queue minutes (zone and minute, UTC) the tracer matching rule can read for these runs: for every run of a zone in
    /// scope whose corrected join lies in the engine's years, its join minute and <see cref="TracerMinuteReach"/> minutes either
    /// side. Runs are not checked here (the engine does that); a run it leaves out only adds minutes nobody reads.
    /// </summary>
    public static IReadOnlySet<(string Zone, DateTime Minute)> TracerMinutes(ComparisonScope scope, IEnumerable<TracerRunRow> runs)
    {
        var minutes = new HashSet<(string Zone, DateTime Minute)>();
        if (scope?.Zones is null || runs is null)
            return minutes;
        var zones = new Dictionary<Guid, string>();
        foreach (var zone in scope.Zones)
        {
            if (zone?.Name is not null)
                zones.TryAdd(zone.ZoneId, zone.Name);
        }

        var reach = TracerMinuteReach * TimeSpan.TicksPerMinute;
        foreach (var run in runs)
        {
            if (run is null || !zones.TryGetValue(run.ZoneId, out var name))
                continue;
            var ticks = run.JoinedUtc.Ticks;
            if (ticks < ComparisonData.EarliestUtc.Ticks + reach || ticks >= ComparisonData.LatestUtc.Ticks - reach)
                continue;
            var minute = ticks - (ticks % TimeSpan.TicksPerMinute);
            for (var m = minute - reach; m <= minute + reach; m += TimeSpan.TicksPerMinute)
                minutes.Add((name, new DateTime(m, DateTimeKind.Utc)));
        }

        return minutes;
    }

    /// <summary>The rows of the queue minutes the matching rule reads (every row of such a key, so its conflicts are seen as in the whole).</summary>
    public static IReadOnlyList<QueueMinuteRow> ForTracers(IEnumerable<QueueMinuteRow> rows, IReadOnlySet<(string Zone, DateTime Minute)> minutes)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(minutes);
        return [.. rows.Where(r => r?.QueueZone is not null && minutes.Contains((r.QueueZone, new DateTime(r.MinuteUtc.Ticks, DateTimeKind.Utc))))];
    }

    /// <summary>The scope of one queue zone's slice: the campaign's version and planned days, that zone alone, no lines and no desks.</summary>
    public static ComparisonScope ZoneScope(ComparisonScope scope, ScopeZone zone)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(zone);
        return new ComparisonScope(scope.ProfileVersion, [zone], [], scope.Windows) { Desks = [] };
    }

    /// <summary>The queue zones a scope is sliced by: each zone once by name, in ordinal order (the order of the whole's results).</summary>
    public static IReadOnlyList<ScopeZone> ZonesOf(ComparisonScope scope) =>
        scope?.Zones is null
            ? []
            : [.. scope.Zones.Where(z => z?.Name is not null).GroupBy(z => z.Name, StringComparer.Ordinal).Select(g => g.First()).OrderBy(z => z.Name, StringComparer.Ordinal)];

    /// <summary>
    /// A whole input cut as the service reads it (<see cref="SlicedInput"/>): the base and one input per zone. Queue minutes,
    /// shadow nowcasts, queue bins and quality intervals go to their zone's slice; rows of no zone in scope (and null rows) go
    /// to the first zone's slice, where they are left out and counted as the whole would. A scope without zones is not sliced.
    /// </summary>
    public static SlicedInput Slice(ComparisonInput whole)
    {
        ArgumentNullException.ThrowIfNull(whole);
        var zones = ZonesOf(whole.Scope);
        if (zones.Count == 0)
            return new SlicedInput(whole, []);

        var tracerMinutes = TracerMinutes(whole.Scope, whole.TracerRuns ?? []);
        var names = zones.Select(z => z.Name).ToHashSet(StringComparer.Ordinal);
        var first = zones[0].Name;
        var inputs = zones.Select(zone => new ComparisonInput
        {
            Scope = ZoneScope(whole.Scope, zone),
            QueueMinutes = Of(whole.QueueMinutes, names, first, zone.Name, r => r?.QueueZone),
            ShadowMinutes = Of(whole.ShadowMinutes, names, first, zone.Name, r => r?.QueueZone),
            QueueBins = [.. (whole.QueueBins ?? []).Where(r => r?.QueueZone == zone.Name)],
            QualityIntervals = [.. (whole.QualityIntervals ?? []).Where(r => r?.QueueZone == zone.Name)]
        }).ToList();
        var baseInput = whole with { QueueMinutes = ForTracers(whole.QueueMinutes ?? [], tracerMinutes), ShadowMinutes = [] };
        return new SlicedInput(baseInput, inputs.AsReadOnly());
    }

    // A zone's rows, and for the first zone also every row of no zone in scope.
    private static List<T> Of<T>(IReadOnlyList<T> rows, HashSet<string> zones, string first, string zone, Func<T, string> zoneOf) =>
        [.. (rows ?? []).Where(r => zoneOf(r) is { } z && zones.Contains(z) ? z == zone : zone == first)];

    #endregion

    #region Comparison

    /// <summary>One zone's slice compared (<see cref="ValidationComparison.Compare"/>) with its nowcast coverage.</summary>
    public static ZoneSlice CompareZone(ComparisonInput zoneInput, ComparisonSettings settings = null)
    {
        ArgumentNullException.ThrowIfNull(zoneInput);
        settings ??= new ComparisonSettings();
        var problems = settings.Problems().ToList();
        if (problems.Count > 0)
            throw new ArgumentException(string.Join(" ", problems), nameof(settings));

        var data = ComparisonData.Take(zoneInput, settings, out var problem);
        if (data is null)
            return new ZoneSlice(ValidationComparison.Nothing(zoneInput.Scope?.ProfileVersion ?? 0, problem), null);
        var coverage = data.Zones.Count == 1 ? NowcastErrors.Coverage(data, data.Zones[0].Name) : null;
        return new ZoneSlice(ValidationComparison.Compare(data), coverage);
    }

    /// <summary>The nowcast coverage of every zone in scope from one input (the whole), as each zone's slice gives it; empty when nothing can be compared.</summary>
    public static IReadOnlyList<NowcastCoverage> Coverage(ComparisonInput input, ComparisonSettings settings = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        var data = ComparisonData.Take(input, settings ?? new ComparisonSettings(), out _);
        return data is null ? [] : [.. data.Zones.Select(z => NowcastErrors.Coverage(data, z.Name))];
    }

    /// <summary>
    /// The base comparison with the zones' nowcast parts (see the class). The zones' results may come in any order; each holds
    /// exactly one zone. A problem of the base, or of any zone, is the result's (nothing compared). Without zone results the
    /// base is returned as it is (a scope without zones is not sliced). Zone results that are not exactly one per zone of the base's
    /// scope (one twice, one missing, one of another zone) are refused.
    /// </summary>
    public static ComparisonResult Pool(ComparisonResult baseResult, IReadOnlyList<ComparisonResult> zoneResults, ComparisonSettings settings = null)
    {
        ArgumentNullException.ThrowIfNull(baseResult);
        ArgumentNullException.ThrowIfNull(zoneResults);
        settings ??= new ComparisonSettings();
        if (baseResult.Problem is not null || zoneResults.Count == 0)
            return baseResult;
        if (zoneResults.FirstOrDefault(z => z?.Problem is not null) is { } refused)
            return ValidationComparison.Nothing(baseResult.ProfileVersion, refused.Problem);
        if (zoneResults.Any(z => z is null || z.NowcastZones.Count != 1 || z.ProfileVersion != baseResult.ProfileVersion))
            throw new ArgumentException("Each zone's result holds exactly one zone, compared under the base's profile version.", nameof(zoneResults));
        // One result per zone in the base's scope, none twice and none missing (the base lists every zone in scope, by name).
        var expected = baseResult.NowcastZones.Select(z => z.QueueZone).Order(StringComparer.Ordinal).ToList();
        if (!zoneResults.Select(z => z.NowcastZones[0].QueueZone).Order(StringComparer.Ordinal).SequenceEqual(expected, StringComparer.Ordinal))
            throw new ArgumentException("The zones' results are exactly one for each zone in the base's scope.", nameof(zoneResults));

        var zones = zoneResults.OrderBy(z => z.NowcastZones[0].QueueZone, StringComparer.Ordinal).ToList();
        var minutes = zones.SelectMany(z => z.NowcastMinutes).ToList();
        var overall = NowcastErrors.Summarise(baseResult.ProfileVersion, settings, null, minutes);
        var keys = baseResult.LeftOut.UnusableKeys.Where(k => !IsSliced(k.Kind))
            .Concat(zones.SelectMany(z => z.LeftOut.UnusableKeys.Where(k => IsSliced(k.Kind))))
            .OrderBy(k => k.Kind).ThenBy(k => k.QueueZone, StringComparer.Ordinal).ThenBy(k => k.LineName, StringComparer.Ordinal)
            .ThenBy(k => k.DeskId).ThenBy(k => k.StartUtc).ThenBy(k => k.ObserverId)
            .ToList();
        var leftOut = baseResult.LeftOut with
        {
            QueueMinutes = zones.Sum(z => z.LeftOut.QueueMinutes),
            ShadowMinutes = zones.Sum(z => z.LeftOut.ShadowMinutes),
            UnusableKeys = keys
        };
        return baseResult with
        {
            NowcastMinutes = minutes.AsReadOnly(),
            NowcastZones = zones.Select(z => z.NowcastZones[0]).ToList().AsReadOnly(),
            NowcastOverall = overall,
            LeftOut = leftOut
        };
    }

    /// <summary>
    /// The whole input compared in slices (<see cref="Slice"/>, <see cref="CompareZone"/>, <see cref="Pool"/>) with every zone's
    /// coverage, as the service does it reading from the database; for tests and inputs already in memory.
    /// </summary>
    public static (ComparisonResult Result, IReadOnlyList<NowcastCoverage> Coverage) CompareSliced(ComparisonInput whole, ComparisonSettings settings = null)
    {
        var sliced = Slice(whole);
        var zones = sliced.Zones.Select(z => CompareZone(z, settings)).ToList();
        var result = Pool(ValidationComparison.Compare(sliced.Base, settings), [.. zones.Select(z => z.Result)], settings);
        return (result, result.Problem is null ? [.. zones.Where(z => z.Coverage is not null).Select(z => z.Coverage)] : []);
    }

    // The kinds only the zones' slices read in full.
    private static bool IsSliced(UnusableKeyKind kind) => kind is UnusableKeyKind.QueueMinute or UnusableKeyKind.ShadowMinute;

    #endregion
}
