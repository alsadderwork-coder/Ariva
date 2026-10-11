using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.InputModels;

namespace Ariva.Core.Services.Topology;

/// <summary>
/// The request rules of a bundle's parts (ARV-122a, Fx.Specification), checked before any entity is touched: the same
/// limits the topology, desk code mapping, zone profile and floor plan requests carry (codes, names of at most 200
/// characters, kinds by name, polygons, coordinates), the bounds of <see cref="SiteConfigurationLimits"/>, no code or name
/// twice where the endpoints refuse it, and every reference between parts resolved through the bundle's own codes and
/// names. The entities then apply their own rules as they do for an administrator's request. Each rule names the part it
/// refuses with a fixed text and repeats no value (CWE-79, CWE-501).
/// </summary>
public static class SiteConfigurationRules
{
    /// <summary>The parts' rules; the parts that refer to the topology are judged only when the topology itself is valid.</summary>
    public static ISpecification<SiteConfigurationContent> Content() =>
        Fx.Specification<SiteConfigurationContent>()
            .And(c => c is not null && IsTopology(c.Topology), SiteConfigurationErrors.InvalidTopology)
            .And(c => c is null || !IsTopology(c.Topology) || AreMappings(c.DeskCodeMappings, c.Topology), SiteConfigurationErrors.InvalidDeskCodeMappings)
            .And(c => c is null || !IsTopology(c.Topology) || IsProfilePart(c.ZoneProfiles, c.Topology), SiteConfigurationErrors.InvalidZoneProfile)
            .And(c => c is null || !IsTopology(c.Topology) || AreFloorPlans(c.FloorPlans, c.FloorPlanFiles, c.Topology), SiteConfigurationErrors.InvalidFloorPlans);

    #region Topology

    public static bool IsTopology(TopologyPart topology)
    {
        if (topology?.Terminals is null || topology.Levels is null || topology.Checkpoints is null || topology.Desks is null)
            return false;
        if (topology.Terminals.Count > SiteConfigurationLimits.MaxTerminals || topology.Levels.Count > SiteConfigurationLimits.MaxLevels ||
            topology.Checkpoints.Count > SiteConfigurationLimits.MaxCheckpoints || topology.Desks.Count > SiteConfigurationLimits.MaxDesks)
            return false;

        if (topology.Terminals.Any(t => t is null || !TopologyCodes.IsIata(t.Airport) || !TopologyCodes.IsValid(t.Code) || !IsName(t.Name)) ||
            !Unique(topology.Terminals.Select(t => t.Code)))
            return false;

        var terminals = topology.Terminals.Select(t => t.Code).ToHashSet(StringComparer.Ordinal);
        if (topology.Levels.Any(l => l is null || !terminals.Contains(l.Terminal ?? string.Empty) || !TopologyCodes.IsValid(l.Code) || !IsName(l.Name) ||
                                     l.FloorNumber is < Level.LowestFloor or > Level.HighestFloor || !IsExtent(l.WidthMetres) || !IsExtent(l.DepthMetres)) ||
            !Unique(topology.Levels.Select(l => LevelKey(l.Terminal, l.Code))) ||
            !Unique(topology.Levels.Select(l => $"{l.Terminal}|{l.FloorNumber}")))
            return false;

        var levels = topology.Levels.Select(l => LevelKey(l.Terminal, l.Code)).ToHashSet(StringComparer.Ordinal);
        if (topology.Checkpoints.Any(c => c is null || !levels.Contains(LevelKey(c.Terminal, c.Level)) || !TopologyCodes.IsValid(c.Code) || !IsName(c.Name) ||
                                          !IsKind<CheckpointKind>(c.Kind)) ||
            !Unique(topology.Checkpoints.Select(c => c.Code)))
            return false;

        var checkpoints = topology.Checkpoints.Select(c => c.Code).ToHashSet(StringComparer.Ordinal);
        return !topology.Desks.Any(d => d is null || !checkpoints.Contains(d.Checkpoint ?? string.Empty) || !TopologyCodes.IsValid(d.Code) ||
                                        !(d.Name is null || IsName(d.Name)) || !IsKind<DeskKind>(d.Kind) || !AreLanes(d.LaneCategories)) &&
               Unique(topology.Desks.Select(d => DeskKey(d.Checkpoint, d.Code)));
    }

    #endregion

    #region Desk code mappings

    public static bool AreMappings(IReadOnlyList<DeskCodeMappingPartRow> mappings, TopologyPart topology)
    {
        if (mappings is null || mappings.Count > SiteConfigurationLimits.MaxDeskCodeMappings)
            return false;
        var desks = topology.Desks.Select(d => DeskKey(d.Checkpoint, d.Code)).ToHashSet(StringComparer.Ordinal);
        if (mappings.Any(m => m is null || !IsKind<ExternalSystem>(m.System) || m.ExternalCode is not { Length: <= 32 } ||
                              DeskCodeMapping.NormalizeCode(m.ExternalCode) is null || !desks.Contains(DeskKey(m.Checkpoint, m.Desk))))
            return false;
        // One live mapping per system and code at a site, and one per system and desk (the endpoint's 409, script 0009).
        return Unique(mappings.Select(m => $"{m.System}|{DeskCodeMapping.NormalizeCode(m.ExternalCode)}")) &&
               Unique(mappings.Select(m => $"{m.System}|{DeskKey(m.Checkpoint, m.Desk)}"));
    }

    #endregion

    #region Zone profile

    public static bool IsProfilePart(IReadOnlyList<ZoneProfilePartRow> profiles, TopologyPart topology) =>
        profiles is { Count: <= 1 } && profiles.All(profile => IsProfile(profile, topology));

    public static bool IsProfile(ZoneProfilePartRow profile, TopologyPart topology)
    {
        if (profile?.Zones is null || profile.Lines is null || !IsName(profile.Name) || profile.Version is < 1 ||
            !(profile.GeometryHash is null || SiteConfigurationBundles.IsSha256(profile.GeometryHash)) ||
            !SiteConfigurationBundles.IsSha256(profile.PortableGeometryHash))
            return false;
        if (profile.Zones.Count > SiteConfigurationLimits.MaxZones || profile.Lines.Count > SiteConfigurationLimits.MaxLines)
            return false;

        var levels = topology.Levels.Select(l => LevelKey(l.Terminal, l.Code)).ToHashSet(StringComparer.Ordinal);
        var desks = topology.Desks.Select(d => DeskKey(d.Checkpoint, d.Code)).ToHashSet(StringComparer.Ordinal);
        if (profile.Zones.Any(z => z is null || !IsName(z.Name) || !IsKind<ZoneKind>(z.Kind) || !levels.Contains(LevelKey(z.Terminal, z.Level)) ||
                                   z.Polygon is not { Length: <= Zone.PolygonLength } || Geometry.ParseRing(z.Polygon, Zone.MaxVertices) is null ||
                                   !(z.QueueZone is null || IsName(z.QueueZone)) ||
                                   !((z.Checkpoint is null && z.Desk is null) || desks.Contains(DeskKey(z.Checkpoint, z.Desk))) ||
                                   !(z.LaneCategory is null || (z.LaneCategory.Length <= 4 && !z.LaneCategory.Contains('\0'))) ||
                                   z.PhysicalCapacity is < 1 or > Zone.MaxPhysicalCapacity))
            return false;

        // Names are unique ignoring case (the aggregate's rule); a queue zone named must be a zone of the part.
        var zones = new HashSet<string>(profile.Zones.Select(z => z.Name.Trim()), StringComparer.OrdinalIgnoreCase);
        if (zones.Count != profile.Zones.Count || profile.Zones.Any(z => z.QueueZone is not null && !zones.Contains(z.QueueZone.Trim())))
            return false;

        if (profile.Lines.Any(l => l is null || !IsName(l.Name) || !IsKind<LineRole>(l.Role) || !levels.Contains(LevelKey(l.Terminal, l.Level)) ||
                                   !(l.Zone is null || zones.Contains(l.Zone.Trim())) ||
                                   !IsCoordinate(l.StartX) || !IsCoordinate(l.StartY) || !IsCoordinate(l.EndX) || !IsCoordinate(l.EndY)))
            return false;
        return new HashSet<string>(profile.Lines.Select(l => l.Name.Trim()), StringComparer.OrdinalIgnoreCase).Count == profile.Lines.Count;
    }

    #endregion

    #region Floor plans

    public static bool AreFloorPlans(IReadOnlyList<FloorPlanPartRow> plans, IReadOnlyDictionary<int, byte[]> files, TopologyPart topology)
    {
        if (plans is null || files is null || plans.Count > SiteConfigurationLimits.MaxFloorPlans || plans.Count != files.Count ||
            Enumerable.Range(1, plans.Count).Any(number => !files.ContainsKey(number)))
            return false;
        var levels = topology.Levels.Select(l => LevelKey(l.Terminal, l.Code)).ToHashSet(StringComparer.Ordinal);
        return !plans.Any(p => p is null || !levels.Contains(LevelKey(p.Terminal, p.Level)) ||
                               !IsFileName(p.FileName) ||
                               !double.IsFinite(p.MetresPerPixel) || !double.IsFinite(p.OriginX) || !double.IsFinite(p.OriginY)) &&
               Unique(plans.Select(p => LevelKey(p.Terminal, p.Level)));
    }

    #endregion

    #region Helpers

    /// <summary>A name as the endpoints take it: 1 to 200 characters after trimming, no NUL (PostgreSQL refuses it in text).</summary>
    public static bool IsName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= SiteConfigurationLimits.MaxNameLength && !name.Contains('\0');

    /// <summary>
    /// A floor plan's file name: none, or at most 200 characters as PostgreSQL counts them (code points: the column is
    /// varchar(200)) without a NUL; the import then applies the upload's own name check.
    /// </summary>
    public static bool IsFileName(string name) =>
        name is null || (name.Length <= 2 * SiteConfigurationLimits.MaxFileNameLength && !name.Contains('\0') &&
                         name.EnumerateRunes().Count() <= SiteConfigurationLimits.MaxFileNameLength);

    /// <summary>A kind by its name only (numbers and unknown names refused), as the endpoints parse kinds.</summary>
    public static bool IsKind<TEnum>(string value) where TEnum : struct, Enum => ParseKind<TEnum>(value) is not null;

    /// <summary>
    /// A kind by its exact name. Stricter than <c>Enum.TryParse</c>, which reads "Immigration, CheckIn" as a combination
    /// of flags; an export writes exact names, so nothing else need be read.
    /// </summary>
    public static TEnum? ParseKind<TEnum>(string value) where TEnum : struct, Enum =>
        value is { Length: > 0 and <= 32 } && Enum.GetNames<TEnum>().Any(name => string.Equals(name, value, StringComparison.Ordinal)) ? Enum.Parse<TEnum>(value) : null;

    /// <summary>The key of a level in a bundle: its terminal and level codes.</summary>
    public static string LevelKey(string terminal, string level) => $"{terminal}|{level}";

    /// <summary>The key of a desk in a bundle: its checkpoint (unique in the site) and desk codes.</summary>
    public static string DeskKey(string checkpoint, string desk) => $"{checkpoint}|{desk}";

    private static bool AreLanes(IReadOnlyList<string> lanes) =>
        lanes is null || (lanes.Count <= Desk.MaxLaneCategories && lanes.All(lane => lane is { Length: <= 4 } && !lane.Contains('\0')));

    private static bool IsExtent(double metres) => double.IsFinite(metres) && metres is > 0 and <= Level.MaxExtentMetres;

    /// <summary>A line end as the line endpoint takes it (-1 to 2,001 m; the aggregate then keeps it inside the level).</summary>
    private static bool IsCoordinate(double metres) => double.IsFinite(metres) && metres is >= -1 and <= 2001;

    private static bool Unique(IEnumerable<string> keys)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return keys.All(seen.Add);
    }

    #endregion
}
