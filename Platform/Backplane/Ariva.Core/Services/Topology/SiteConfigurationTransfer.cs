using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Sensing;

namespace Ariva.Core.Services.Topology;

/// <summary>What an export reads of a site: its live records and its published zone profile, if any.</summary>
public sealed record SiteConfigurationSource(
    IReadOnlyList<Terminal> Terminals,
    IReadOnlyList<Level> Levels,
    IReadOnlyList<Checkpoint> Checkpoints,
    IReadOnlyList<Desk> Desks,
    IReadOnlyList<DeskCodeMapping> DeskCodeMappings,
    ZoneProfile PublishedProfile,
    IReadOnlyList<FloorPlan> FloorPlans);

/// <summary>An export's parts as rows; floor plan file N is the plan at <c>FloorPlanFiles[N - 1]</c>.</summary>
public sealed record ExportedSiteConfiguration(
    TopologyPart Topology,
    IReadOnlyList<DeskCodeMappingPartRow> DeskCodeMappings,
    IReadOnlyList<ZoneProfilePartRow> ZoneProfiles,
    IReadOnlyList<FloorPlanPartRow> FloorPlans,
    IReadOnlyList<FloorPlan> FloorPlanFiles,
    SiteConfigurationCountsViewModel Counts,
    int? ProfileVersion,
    string GeometryHash,
    string PortableGeometryHash);

/// <summary>A floor plan to create: its new level, its metadata row and its file's bytes (inspected again before it is stored).</summary>
public sealed record PlannedFloorPlan(Level Level, FloorPlanPartRow Row, byte[] File);

/// <summary>
/// What an import creates, every record new and bound to the target site: the topology, the desk code mappings, the zone
/// profile as a draft (null when the bundle carries none) with its portable geometry hash, and the floor plans.
/// </summary>
public sealed record SiteConfigurationPlan(
    IReadOnlyList<Terminal> Terminals,
    IReadOnlyList<Level> Levels,
    IReadOnlyList<Checkpoint> Checkpoints,
    IReadOnlyList<Desk> Desks,
    IReadOnlyList<DeskCodeMapping> DeskCodeMappings,
    ZoneProfile Draft,
    string PortableGeometryHash,
    IReadOnlyList<PlannedFloorPlan> FloorPlans,
    SiteConfigurationCountsViewModel Counts);

/// <summary>
/// The two directions of a site configuration bundle (ARV-122a), pure: no database, no files. An export names every
/// reference by codes (a level by terminal and level, a desk by checkpoint and desk) and names (a queue zone, a line's
/// zone); an import trusts no id from a bundle: it creates every record with a new id through the entities' own methods
/// (the rules an administrator's requests meet), resolves each reference through the bundle's codes and names, and binds
/// everything to the target site, so the site codes of the source never reach the target ("rebased").
/// </summary>
public static class SiteConfigurationTransfer
{
    #region Export

    public static Result<ExportedSiteConfiguration> Export(SiteConfigurationSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Terminals.Count > SiteConfigurationLimits.MaxTerminals || source.Levels.Count > SiteConfigurationLimits.MaxLevels ||
            source.Checkpoints.Count > SiteConfigurationLimits.MaxCheckpoints || source.Desks.Count > SiteConfigurationLimits.MaxDesks ||
            source.DeskCodeMappings.Count > SiteConfigurationLimits.MaxDeskCodeMappings || source.FloorPlans.Count > SiteConfigurationLimits.MaxFloorPlans)
            return Result.Error<ExportedSiteConfiguration>(SiteConfigurationErrors.TooManyRecords);
        // Terminal codes are unique per airport; a site with terminals at two airports could hold one code twice.
        if (source.Terminals.Select(t => t.Code).Distinct(StringComparer.Ordinal).Count() != source.Terminals.Count)
            return Result.Error<ExportedSiteConfiguration>(SiteConfigurationErrors.AmbiguousTerminals);

        var terminals = source.Terminals.OrderBy(t => t.Code, StringComparer.Ordinal).ToList();
        var terminalIds = terminals.Select(t => t.Id.GetValueOrDefault()).ToHashSet();
        var levels = source.Levels.Where(l => terminalIds.Contains(l.Terminal.Id.GetValueOrDefault()))
            .OrderBy(l => l.Terminal.Code, StringComparer.Ordinal).ThenBy(l => l.FloorNumber).ThenBy(l => l.Code, StringComparer.Ordinal).ToList();
        var levelNames = levels.ToDictionary(l => l.Id.GetValueOrDefault(), l => ZoneProfile.PortableLevelName(l.Terminal.Code, l.Code));
        var checkpoints = source.Checkpoints.Where(c => levelNames.ContainsKey(c.Level.Id.GetValueOrDefault()))
            .OrderBy(c => c.Code, StringComparer.Ordinal).ToList();
        var checkpointIds = checkpoints.Select(c => c.Id.GetValueOrDefault()).ToHashSet();
        var desks = source.Desks.Where(d => checkpointIds.Contains(d.Checkpoint.Id.GetValueOrDefault()))
            .OrderBy(d => d.Checkpoint.Code, StringComparer.Ordinal).ThenBy(d => d.Code, StringComparer.Ordinal).ToList();
        var deskNames = desks.ToDictionary(d => d.Id.GetValueOrDefault(), d => ZoneProfile.PortableDeskName(d.Checkpoint.Code, d.Code));
        var deskRows = desks.ToDictionary(d => d.Id.GetValueOrDefault());

        var topology = new TopologyPart(
            [.. terminals.Select(t => new TerminalPartRow(t.Airport.IataCode, t.Code, t.Name))],
            [.. levels.Select(l => new LevelPartRow(l.Terminal.Code, l.Code, l.Name, l.FloorNumber, l.WidthMetres, l.DepthMetres))],
            [.. checkpoints.Select(c => new CheckpointPartRow(c.Level.Terminal.Code, c.Level.Code, c.Code, c.Name, c.Kind.ToString()))],
            [.. desks.Select(d => new DeskPartRow(d.Checkpoint.Code, d.Code, d.Name, d.Kind.ToString(), [.. d.LaneCategories], d.InService))]);

        var mappings = source.DeskCodeMappings.Where(m => deskRows.ContainsKey(m.Desk.Id.GetValueOrDefault()))
            .OrderBy(m => m.System).ThenBy(m => m.ExternalCode, StringComparer.Ordinal)
            .Select(m => new DeskCodeMappingPartRow(m.System.ToString(), m.ExternalCode, m.Desk.Checkpoint.Code, m.Desk.Code))
            .ToList();

        var plans = source.FloorPlans.Where(p => levelNames.ContainsKey(p.Level.Id.GetValueOrDefault()))
            .OrderBy(p => p.Level.Terminal.Code, StringComparer.Ordinal).ThenBy(p => p.Level.Code, StringComparer.Ordinal).ToList();
        var planRows = plans.Select(p => new FloorPlanPartRow(p.Level.Terminal.Code, p.Level.Code, p.OriginalFileName, p.MetresPerPixel, p.OriginX, p.OriginY)).ToList();

        List<ZoneProfilePartRow> profiles = [];
        string portable = null;
        var profile = source.PublishedProfile;
        if (profile is not null)
        {
            if (profile.Zones.Any(z => !levelNames.ContainsKey(z.LevelId) || (z.DeskId is { } desk && !deskNames.ContainsKey(desk))) ||
                profile.Lines.Any(l => !levelNames.ContainsKey(l.LevelId)))
                return Result.Error<ExportedSiteConfiguration>(SiteConfigurationErrors.ProfileOnRemovedTopology);
            portable = profile.ComputePortableGeometryHash(levelNames, deskNames);
            var levelRows = levels.ToDictionary(l => l.Id.GetValueOrDefault());
            var zones = profile.Zones.OrderBy(z => z.Name, StringComparer.Ordinal).Select(z =>
            {
                var level = levelRows[z.LevelId];
                var desk = z.DeskId is { } id ? deskRows[id] : null;
                return new ZonePartRow(z.Name, z.Kind.ToString(), level.Terminal.Code, level.Code, z.Polygon, z.QueueZone?.Name, desk?.Checkpoint.Code, desk?.Code,
                    z.LaneCategory, z.PhysicalCapacity);
            }).ToList();
            var lines = profile.Lines.OrderBy(l => l.Name, StringComparer.Ordinal).Select(l =>
            {
                var level = levelRows[l.LevelId];
                return new LinePartRow(l.Name, l.Role.ToString(), l.Zone?.Name, level.Terminal.Code, level.Code, l.StartX, l.StartY, l.EndX, l.EndY);
            }).ToList();
            profiles.Add(new ZoneProfilePartRow(profile.Name, profile.Version, profile.GeometryHash, portable, zones, lines));
        }

        var counts = new SiteConfigurationCountsViewModel(topology.Terminals.Count, topology.Levels.Count, topology.Checkpoints.Count, topology.Desks.Count,
            mappings.Count, profile?.Zones.Count ?? 0, profile?.Lines.Count ?? 0, planRows.Count);
        return new Result<ExportedSiteConfiguration>(new ExportedSiteConfiguration(topology, mappings, profiles, planRows, plans, counts,
            profile?.Version, profile?.GeometryHash, portable));
    }

    #endregion

    #region Import

    /// <summary>
    /// The records an import creates at <paramref name="siteCode"/> from content the rules accepted. Terminals go to the
    /// airport of each row, or to <paramref name="airportOverride"/> when one is given; <paramref name="airports"/> holds
    /// the live airports by IATA code (none is ever created). Any refusal is one fixed text for its part.
    /// </summary>
    public static Result<SiteConfigurationPlan> Plan(string siteCode, SiteConfigurationContent content, IReadOnlyDictionary<string, Airport> airports, string airportOverride = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(airports);
        if (!Site.IsValidCode(siteCode))
            throw new ArgumentException("Unknown site code shape.", nameof(siteCode));

        var topology = content.Topology;
        var terminals = new Dictionary<string, Terminal>(StringComparer.Ordinal);
        var levels = new Dictionary<string, Level>(StringComparer.Ordinal);
        var checkpoints = new Dictionary<string, Checkpoint>(StringComparer.Ordinal);
        var desks = new Dictionary<string, Desk>(StringComparer.Ordinal);
        foreach (var row in topology.Terminals)
        {
            if (!airports.TryGetValue(airportOverride ?? row.Airport, out var airport) || airport.IsDeleted)
                return Result.Error<SiteConfigurationPlan>(SiteConfigurationErrors.UnknownAirport);
            // Terminal codes are unique among an airport's live terminals, whichever site holds them.
            if (airport.Terminals.Any(t => !t.IsDeleted && string.Equals(t.Code, row.Code, StringComparison.Ordinal)))
                return Result.Error<SiteConfigurationPlan>(SiteConfigurationErrors.TerminalTaken);
        }

        var built = Guarded(SiteConfigurationErrors.InvalidTopology, () =>
        {
            foreach (var row in topology.Terminals)
                terminals[row.Code] = WithId(airports[airportOverride ?? row.Airport].AddTerminal(row.Code, row.Name, siteCode));
            foreach (var row in topology.Levels)
                levels[SiteConfigurationRules.LevelKey(row.Terminal, row.Code)] =
                    WithId(terminals[row.Terminal].AddLevel(row.Code, row.Name, row.FloorNumber, row.WidthMetres, row.DepthMetres));
            foreach (var row in topology.Checkpoints)
                checkpoints[row.Code] = WithId(levels[SiteConfigurationRules.LevelKey(row.Terminal, row.Level)]
                    .AddCheckpoint(row.Code, row.Name, SiteConfigurationRules.ParseKind<CheckpointKind>(row.Kind).GetValueOrDefault()));
            foreach (var row in topology.Desks)
            {
                var desk = WithId(checkpoints[row.Checkpoint].AddDesk(row.Code, row.Name, SiteConfigurationRules.ParseKind<DeskKind>(row.Kind).GetValueOrDefault(), row.LaneCategories));
                desk.SetInService(row.InService);
                desks[SiteConfigurationRules.DeskKey(row.Checkpoint, row.Code)] = desk;
            }
        });
        if (built is not null)
            return Result.Error<SiteConfigurationPlan>(built);

        var mappings = new List<DeskCodeMapping>();
        built = Guarded(SiteConfigurationErrors.InvalidDeskCodeMappings, () =>
        {
            foreach (var row in content.DeskCodeMappings)
                mappings.Add(new DeskCodeMapping(SiteConfigurationRules.ParseKind<ExternalSystem>(row.System).GetValueOrDefault(), row.ExternalCode,
                    desks[SiteConfigurationRules.DeskKey(row.Checkpoint, row.Desk)]));
        });
        if (built is not null)
            return Result.Error<SiteConfigurationPlan>(built);

        ZoneProfile draft = null;
        string portable = null;
        if (content.ZoneProfile is { } profile)
        {
            // Rebased: a queue zone's events are keyed by the target site's code and its name (ARV-114c).
            if (profile.Zones.Any(z => SiteConfigurationRules.ParseKind<ZoneKind>(z.Kind) == ZoneKind.Queue && !ZoneKeys.Fits(siteCode, z.Name.Trim())))
                return Result.Error<SiteConfigurationPlan>(SiteConfigurationErrors.QueueZoneNameTooLong);
            built = Guarded(SiteConfigurationErrors.InvalidZoneProfile, () => draft = Draft(siteCode, profile, levels, desks));
            if (built is not null)
                return Result.Error<SiteConfigurationPlan>(built);
            portable = draft.ComputePortableGeometryHash(
                levels.Values.ToDictionary(l => l.Id.GetValueOrDefault(), l => ZoneProfile.PortableLevelName(l.Terminal.Code, l.Code)),
                desks.Values.ToDictionary(d => d.Id.GetValueOrDefault(), d => ZoneProfile.PortableDeskName(d.Checkpoint.Code, d.Code)));
            if (!string.Equals(portable, profile.PortableGeometryHash, StringComparison.Ordinal))
                return Result.Error<SiteConfigurationPlan>(SiteConfigurationErrors.ProfileNotReproduced);
        }

        var plans = content.FloorPlans
            .Select((row, index) => new PlannedFloorPlan(levels[SiteConfigurationRules.LevelKey(row.Terminal, row.Level)], row, content.FloorPlanFiles[index + 1]))
            .ToList();
        var counts = new SiteConfigurationCountsViewModel(terminals.Count, levels.Count, checkpoints.Count, desks.Count, mappings.Count,
            draft?.Zones.Count ?? 0, draft?.Lines.Count ?? 0, plans.Count);
        return new Result<SiteConfigurationPlan>(new SiteConfigurationPlan([.. terminals.Values], [.. levels.Values], [.. checkpoints.Values], [.. desks.Values],
            mappings, draft, portable, plans, counts));
    }

    /// <summary>The draft: queue zones first (hanging zones name them), then the rest, then the lines, through the aggregate's methods.</summary>
    private static ZoneProfile Draft(string siteCode, ZoneProfilePartRow profile, Dictionary<string, Level> levels, Dictionary<string, Desk> desks)
    {
        var draft = new ZoneProfile(siteCode, profile.Name) { Id = Guid.CreateVersion7() };
        var zones = new Dictionary<string, Zone>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in profile.Zones.OrderBy(z => SiteConfigurationRules.ParseKind<ZoneKind>(z.Kind) == ZoneKind.Queue ? 0 : 1))
        {
            var kind = SiteConfigurationRules.ParseKind<ZoneKind>(row.Kind).GetValueOrDefault();
            var level = levels[SiteConfigurationRules.LevelKey(row.Terminal, row.Level)];
            var queue = row.QueueZone is null ? null : zones.GetValueOrDefault(row.QueueZone.Trim()) ?? throw new InvalidOperationException("The queue zone is not in the profile.");
            var desk = row.Checkpoint is null && row.Desk is null ? null : desks[SiteConfigurationRules.DeskKey(row.Checkpoint, row.Desk)].Id;
            var zone = draft.AddZone(row.Name, kind, level, Geometry.ParseRing(row.Polygon, Zone.MaxVertices), queue, desk);
            draft.SetZoneLane(zone, string.IsNullOrWhiteSpace(row.LaneCategory) ? null : row.LaneCategory.Trim());
            draft.SetZoneCapacity(zone, row.PhysicalCapacity);
            zones[zone.Name] = zone;
        }

        foreach (var row in profile.Lines)
        {
            var zone = row.Zone is null ? null : zones[row.Zone.Trim()];
            draft.AddLine(row.Name, SiteConfigurationRules.ParseKind<LineRole>(row.Role).GetValueOrDefault(), levels[SiteConfigurationRules.LevelKey(row.Terminal, row.Level)],
                new FloorPoint(row.StartX, row.StartY), new FloorPoint(row.EndX, row.EndY), zone);
        }

        return draft;
    }

    /// <summary>
    /// Runs one part's creation; an entity's refusal (its rule message names a value from the bundle) becomes the part's
    /// fixed text, so nothing from the bundle is echoed.
    /// </summary>
    private static string Guarded(string error, Action work)
    {
        try
        {
            work();
            return null;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return error;
        }
    }

    private static T WithId<T>(T entity) where T : EntityBase<T>
    {
        entity.Id = Guid.CreateVersion7();
        return entity;
    }

    #endregion
}
