namespace Ariva.Core.Validation.Comparison;

/// <summary>
/// The F18 comparison engine (ARV-104e, ARV-104f): a campaign's ground truth (manual counts, tracer runs, desk observations)
/// against Ariva's stored outputs (line counts per bin, queue minutes with their published nowcast, the shadow nowcast, queue
/// bins, zone health bins, desk minutes, data-quality intervals) under the campaign's profile version. It computes count
/// accuracy per line and bin (<see cref="CountAccuracy"/>), tracer wait error and bias per zone with their sensitivity to the
/// clock offsets (<see cref="TracerWaits"/>), the observers' offset spread and outlier batches (<see cref="ClockOffsets"/>),
/// track completion per zone (<see cref="TrackCompletion"/>), the desk-state agreement per desk and over every desk
/// (<see cref="DeskStateAgreement"/>) and the nowcast error per zone with the ground-truth proof, the published and the
/// shadow nowcast side by side (<see cref="NowcastErrors"/>). Degraded and Unknown items are reported apart with their
/// counts, never dropped (the desk-state agreement counts its Unknown minutes as disagreement); every result carries the
/// profile version; unusable rows are left out and counted, and no earlier revision stands in for an unusable latest one;
/// empty sets give no value (no data), never a division by zero; every list returned is read-only. The per-line, per-zone and
/// per-desk verdicts judge only the items they hold: they are not acceptance verdicts on their own (the campaign's verdict,
/// ARV-104g, needs the campaign's targets of judged items and shows the excluded share). Pure: no I/O, no persistence, no
/// clock; the same inputs in any order give the same result. What ARV-104g must restrict is on <see cref="ComparisonResult"/>.
/// </summary>
public static class ValidationComparison
{
    public static ComparisonResult Compare(ComparisonInput input, ComparisonSettings settings = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        settings ??= new ComparisonSettings();
        var problems = settings.Problems().ToList();
        if (problems.Count > 0)
            throw new ArgumentException(string.Join(" ", problems), nameof(settings));

        var data = ComparisonData.Take(input, settings, out var problem);
        return data is null ? Nothing(input.Scope?.ProfileVersion ?? 0, problem) : Compare(data);
    }

    /// <summary>The result of a comparison that compared nothing: every list empty, the overall summaries null.</summary>
    internal static ComparisonResult Nothing(int profileVersion, ComparisonProblem? problem) =>
        new(profileVersion, problem, [], [], [], [], null, [], [], [], [], [], null, [], [], null, LeftOutInputs.None);

    /// <summary>The comparison of inputs already checked and indexed (<see cref="ComparisonData.Take"/>).</summary>
    internal static ComparisonResult Compare(ComparisonData data)
    {
        var (countBins, lines) = CountAccuracy.Compare(data);
        var (observers, batches) = ClockOffsets.Compare(data);
        var tracers = TracerWaits.Compare(data, batches.Where(b => b.Outlier).Select(b => b.BatchId).ToHashSet());
        var byZone = tracers.GroupBy(t => t.QueueZone, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var zones = data.Zones.Select(z => TracerWaits.Summarise(data, z.Name, byZone.GetValueOrDefault(z.Name) ?? [])).ToList().AsReadOnly();
        var (deskMinutes, desks, deskOverall) = DeskStateAgreement.Compare(data);
        var (nowcastMinutes, nowcastZones, nowcastOverall) = NowcastErrors.Compare(data);
        return new ComparisonResult(data.Version, null, countBins, lines, tracers, zones, TracerWaits.Summarise(data, null, tracers),
            TrackCompletion.Compare(data), observers, batches, deskMinutes, desks, deskOverall, nowcastMinutes, nowcastZones, nowcastOverall, data.LeftOut);
    }
}
