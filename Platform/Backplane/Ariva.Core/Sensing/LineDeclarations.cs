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
