using Ariva.Core.Domain.Enums;

namespace Ariva.Core.Sensing;

/// <summary>
/// What devices' line declarations (ARV-259e4, ADR-0027) mean against a queue zone's entry and exit lines: the lines a
/// declaration covers, the names it lists that the zone does not hold (a published profile dropped or renamed them), and
/// the zone's lines no declaration covers. Pure; names compare ordinally, as the zone profile stores them.
/// </summary>
public static class LineDeclarations
{
    /// <summary>The zone's lines the declaration covers: every one for All, those listed for Listed, none for None.</summary>
    public static IEnumerable<string> Covered(DeviceLineCoverage coverage, IReadOnlyList<string> listed, IReadOnlySet<string> zoneLines)
    {
        ArgumentNullException.ThrowIfNull(zoneLines);
        return coverage switch
        {
            DeviceLineCoverage.All => zoneLines,
            DeviceLineCoverage.Listed => (listed ?? []).Where(zoneLines.Contains),
            _ => []
        };
    }

    /// <summary>The names a Listed declaration holds that are not entry or exit lines of the zone, in their order; none for the other kinds.</summary>
    public static IReadOnlyList<string> NotInZone(DeviceLineCoverage coverage, IReadOnlyList<string> listed, IReadOnlySet<string> zoneLines)
    {
        ArgumentNullException.ThrowIfNull(zoneLines);
        return coverage == DeviceLineCoverage.Listed ? [.. (listed ?? []).Where(name => !zoneLines.Contains(name))] : [];
    }

    /// <summary>
    /// A declaration as a health report or a snapshot carries it (ARV-259e5): null for every line (All), empty for none (None), else
    /// the names (Listed). From the registry's kind and its stored names.
    /// </summary>
    public static IReadOnlyList<string> Carried(DeviceLineCoverage coverage, IReadOnlyList<string> listed) => coverage switch
    {
        DeviceLineCoverage.All => null,
        DeviceLineCoverage.Listed => [.. listed ?? []],
        _ => []
    };

    /// <summary>
    /// Whether a carried declaration has the registry's shape (ARV-259e5, CWE-501: a health report and a snapshot are data from beyond a
    /// trust boundary): null, or at most <see cref="Domain.Entities.Device.MaxCoveredLines"/> distinct names (ordinal) of 1 to
    /// <see cref="Domain.Entities.TopologyCodes.MaxNameLength"/> characters, each a name by the registry's rule
    /// (<see cref="Domain.Entities.TopologyCodes.IsName"/>) with no control or invisible character anywhere, not even at its ends (no line
    /// feed, so the health archive's line feed join of the names is lossless, and what a replay reads back is what the stream saw).
    /// Whether the names are lines of the zone is not its question.
    /// </summary>
    public static bool IsWellFormed(IReadOnlyList<string> carried)
    {
        if (carried is null)
            return true;
        if (carried.Count > Domain.Entities.Device.MaxCoveredLines)
            return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in carried)
        {
            if (string.IsNullOrEmpty(name) || name.Length > Domain.Entities.TopologyCodes.MaxNameLength || !Domain.Entities.TopologyCodes.IsName(name) ||
                !Domain.Components.DisplayText.IsClean(name) || !seen.Add(name))
                return false;
        }

        return true;
    }

    /// <summary>The zone's entry and exit lines none of the declarations covers, in ordinal order.</summary>
    public static IReadOnlyList<string> Uncovered(IReadOnlySet<string> zoneLines, IEnumerable<(DeviceLineCoverage Coverage, IReadOnlyList<string> Listed)> declarations)
    {
        ArgumentNullException.ThrowIfNull(zoneLines);
        ArgumentNullException.ThrowIfNull(declarations);
        var uncovered = new HashSet<string>(zoneLines, StringComparer.Ordinal);
        foreach (var (coverage, listed) in declarations)
            uncovered.ExceptWith(Covered(coverage, listed, zoneLines));
        return [.. uncovered.Order(StringComparer.Ordinal)];
    }
}
