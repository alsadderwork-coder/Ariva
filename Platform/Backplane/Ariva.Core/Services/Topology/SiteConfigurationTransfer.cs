using Ariva.Core.Domain.Components;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Sensing;
using Ariva.Core.Services.Alerting;

namespace Ariva.Core.Services.Topology;

/// <summary>What an export reads of a site: its live records and its published zone profile, if any.</summary>
public sealed record SiteConfigurationSource(
    IReadOnlyList<Terminal> Terminals,
    IReadOnlyList<Level> Levels,
    IReadOnlyList<Checkpoint> Checkpoints,
    IReadOnlyList<Desk> Desks,
    IReadOnlyList<DeskCodeMapping> DeskCodeMappings,
    ZoneProfile PublishedProfile,
    IReadOnlyList<FloorPlan> FloorPlans,
    IReadOnlyList<Device> Devices,
    IReadOnlyList<AlertRule> AlertRules,
    IReadOnlyList<Display> Displays);

/// <summary>An export's parts as rows; floor plan file N is the plan at <c>FloorPlanFiles[N - 1]</c>.</summary>
public sealed record ExportedSiteConfiguration(
    TopologyPart Topology,
    IReadOnlyList<DeskCodeMappingPartRow> DeskCodeMappings,
    IReadOnlyList<ZoneProfilePartRow> ZoneProfiles,
    IReadOnlyList<FloorPlanPartRow> FloorPlans,
    IReadOnlyList<FloorPlan> FloorPlanFiles,
    IReadOnlyList<DevicePartRow> Devices,
    IReadOnlyList<AlertRulePartRow> AlertRules,
    IReadOnlyList<DisplayPartRow> Displays,
    SiteConfigurationCountsViewModel Counts,
    int? ProfileVersion,
    string GeometryHash,
    string PortableGeometryHash);

/// <summary>A floor plan to create: its new level, its metadata row and its file's bytes (inspected again before it is stored).</summary>
public sealed record PlannedFloorPlan(Level Level, FloorPlanPartRow Row, byte[] File);

/// <summary>
/// What an import creates, every record new and bound to the target site: the topology, the desk code mappings, the zone
/// profile as a draft (null when the bundle carries none) with its portable geometry hash, the floor plans, the devices as
/// registration requests (registered, with their new credentials, by the registration path itself; the codes of those whose
/// access settings were withheld listed), the alert rules (without escalation contacts; their codes listed) and the displays
/// (unpaired).
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
    IReadOnlyList<RegisterDeviceRequest> Devices,
    IReadOnlyList<AlertRule> AlertRules,
    IReadOnlyList<Display> Displays,
    IReadOnlyList<string> DevicesNeedingAccessSettings,
    IReadOnlyList<string> RulesNeedingEscalationContact,
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
            source.DeskCodeMappings.Count > SiteConfigurationLimits.MaxDeskCodeMappings || source.FloorPlans.Count > SiteConfigurationLimits.MaxFloorPlans ||
            source.Devices.Count > SiteConfigurationLimits.MaxDevices || source.AlertRules.Count > SiteConfigurationLimits.MaxAlertRules ||
            source.Displays.Count > SiteConfigurationLimits.MaxDisplays)
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
        // ARV-162: every export can be imported, so a name the import would refuse (ARV-159, saved before) refuses the export.
        if (terminals.Any(t => !TopologyCodes.IsName(t.Name)) || levels.Any(l => !TopologyCodes.IsName(l.Name)) ||
            checkpoints.Any(c => !TopologyCodes.IsName(c.Name)) || desks.Any(d => !TopologyCodes.IsName(d.Name)))
            return Result.Error<ExportedSiteConfiguration>(SiteConfigurationErrors.TopologyNamesNotImportable);

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
        // ARV-162 (review R1): a file name stored before ARV-153 may hold a tag character, which the import refuses.
        if (plans.Any(p => !FloorPlan.IsSafeFileName(p.OriginalFileName)))
            return Result.Error<ExportedSiteConfiguration>(SiteConfigurationErrors.FloorPlanNamesNotImportable);
        var planRows = plans.Select(p => new FloorPlanPartRow(p.Level.Terminal.Code, p.Level.Code, p.OriginalFileName, p.MetresPerPixel, p.OriginX, p.OriginY)).ToList();

        List<ZoneProfilePartRow> profiles = [];
        string portable = null;
        var profile = source.PublishedProfile;
        if (profile is not null)
        {
            if (profile.Zones.Any(z => !levelNames.ContainsKey(z.LevelId) || (z.DeskId is { } desk && !deskNames.ContainsKey(desk))) ||
                profile.Lines.Any(l => !levelNames.ContainsKey(l.LevelId)))
                return Result.Error<ExportedSiteConfiguration>(SiteConfigurationErrors.ProfileOnRemovedTopology);
            // ARV-162: a version published before ARV-159 or ARV-161 may hold a name or an outline the import refuses.
            if (!TopologyCodes.IsName(profile.Name) || profile.Zones.Any(z => !TopologyCodes.IsName(z.Name) || !Geometry.IsSimplePolygon(z.Points)) ||
                profile.Lines.Any(l => !TopologyCodes.IsName(l.Name)))
                return Result.Error<ExportedSiteConfiguration>(SiteConfigurationErrors.ProfileNotImportable);
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

        // ARV-122b: devices, alert rules and displays name the published profile's zones; one that names another zone could
        // not be imported (the import checks them against the imported draft), so the export refuses it now.
        var queues = profile?.Zones.Where(z => z.Kind == ZoneKind.Queue).Select(z => z.Name).ToHashSet(StringComparer.Ordinal) ?? [];
        var overflows = profile?.Zones.Where(z => z.Kind == ZoneKind.Overflow).Select(z => z.Name).ToHashSet(StringComparer.Ordinal) ?? [];
        var devices = source.Devices.OrderBy(d => d.Code, StringComparer.Ordinal).ToList();
        if (devices.Any(d => !levelNames.ContainsKey(d.Level.Id.GetValueOrDefault())))
            return Result.Error<ExportedSiteConfiguration>(SiteConfigurationErrors.DeviceOnRemovedLevel);
        var rules = source.AlertRules.OrderBy(r => SiteConfigurationRules.RuleNumber(r.Code) ?? int.MaxValue).ThenBy(r => r.Code, StringComparer.Ordinal).ToList();
        // Every export can be imported: a rule code the import would refuse (above R-100000) refuses the export instead.
        if (rules.Any(r => SiteConfigurationRules.RuleNumber(r.Code) is not { } number || number > SiteConfigurationLimits.MaxImportedRuleNumber))
            return Result.Error<ExportedSiteConfiguration>(SiteConfigurationErrors.RuleCodesBeyondImportBound);
        var displays = source.Displays.OrderBy(d => d.Code, StringComparer.Ordinal).ToList();
        if (devices.Any(d => !queues.Contains(d.QueueZoneName)) ||
            rules.Any(r => !ZonesOf(r.Zones, r.Metric, queues, overflows)) ||
            displays.Any(d => d.EntryList.Any(e => !queues.Contains(e.Zone))))
            return Result.Error<ExportedSiteConfiguration>(SiteConfigurationErrors.ReferencesOutsideProfile);
        // ARV-162: texts saved before ARV-158 judged them per code point travel only when the import would take them.
        if (devices.Any(d => !DisplayText.IsClean(d.Model)) || rules.Any(r => !DisplayText.IsClean(r.Name)) ||
            displays.Any(d => !DisplayText.IsClean(d.Name) || !DisplayText.IsClean(d.Location) ||
                              d.EntryList.Any(e => e.Labels.Values.Any(label => !DisplayText.IsClean(label))) || d.FallbackMap.Values.Any(m => !DisplayText.IsClean(m))))
            return Result.Error<ExportedSiteConfiguration>(SiteConfigurationErrors.TextsNotImportable);
        // ARV-187 (lead decision P5): a rule the import's entity checks would refuse (Plan builds each rule without its
        // escalation contact, which is never exported), such as a predicted nowcast rule from before ARV-187 that does not
        // belong to a border role, refuses the export instead, so every export still imports.
        if (rules.Any(r => (r.Values() with { EscalationContact = null }).Problems().Count > 0))
            return Result.Error<ExportedSiteConfiguration>(SiteConfigurationErrors.RulesNotImportable);

        var levelRowsById = levels.ToDictionary(l => l.Id.GetValueOrDefault());
        var deviceRows = devices.Select(d =>
        {
            var level = levelRowsById[d.Level.Id.GetValueOrDefault()];
            var vendor = d.FootprintSource == FootprintSource.Vendor;
            return new DevicePartRow(d.Code, d.Family.ToString(), d.Model, d.Transport.ToString(), d.Dialect.ToString(), d.ClockSource.ToString(), d.MappingName,
                level.Terminal.Code, level.Code, d.X, d.Y, d.MountingHeightMetres, d.OrientationDegrees, d.QueueZoneName,
                vendor ? d.FootprintLengthMetres : null, vendor ? d.FootprintWidthMetres : null, vendor ? d.FootprintRadiusMetres : null,
                !string.IsNullOrEmpty(d.AllowedSources) || !string.IsNullOrEmpty(d.ClientCertificateSha256));
        }).ToList();
        var ruleRows = rules.Select(r => new AlertRulePartRow(r.Code, r.Name, [.. r.Zones], r.Metric.ToString(), r.Comparator.ToString(), r.Threshold, r.MinQueueLength,
            r.ClearThreshold, r.SustainMinutes, r.ClearAfterMinutes, r.Severity.ToString(), r.OwnerRole, r.EscalateAfterMinutes, r.EscalateToRole,
            !string.IsNullOrWhiteSpace(r.EscalationContact), r.NotifyByEmail, r.Enabled, r.LeadMinutes)).ToList();
        var displayRows = displays.Select(d => new DisplayPartRow(d.Code, d.Name, d.Location, d.Orientation.ToString(), [.. d.LanguageList], d.BandMinutes,
            d.HysteresisMinutes, d.StaleSeconds, [.. d.EntryList.Select(e => new DisplayEntryPartRow(e.Zone, e.Labels))], d.FallbackMap, d.Enabled)).ToList();

        var counts = new SiteConfigurationCountsViewModel(topology.Terminals.Count, topology.Levels.Count, topology.Checkpoints.Count, topology.Desks.Count,
            mappings.Count, profile?.Zones.Count ?? 0, profile?.Lines.Count ?? 0, planRows.Count, deviceRows.Count, ruleRows.Count, displayRows.Count);
        return new Result<ExportedSiteConfiguration>(new ExportedSiteConfiguration(topology, mappings, profiles, planRows, plans, deviceRows, ruleRows, displayRows,
            counts, profile?.Version, profile?.GeometryHash, portable));
    }

    /// <summary>
    /// The zone rule of alert rules (the rule service's, owner decision 2026-10-11 for imports): every zone a queue zone or
    /// an overflow band of the profile; a health check and open desks below recommendation watch queue zones only
    /// (<see cref="AlertRuleValues.WatchesQueueZonesOnly"/>).
    /// </summary>
    public static bool ZonesOf(IReadOnlyList<string> zones, AlertMetric metric, IReadOnlySet<string> queues, IReadOnlySet<string> overflows) =>
        zones.All(z => queues.Contains(z) || (!AlertRuleValues.WatchesQueueZonesOnly(metric) && overflows.Contains(z)));

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

        // ARV-122b. Devices become registration requests on their new levels; the registration path checks and registers them.
        var devices = content.Devices.Select(d => new RegisterDeviceRequest(d.Code, d.Family, d.Model, d.Transport, d.Dialect, d.ClockSource,
            new DevicePlacement(levels[SiteConfigurationRules.LevelKey(d.Terminal, d.Level)].Id, d.X, d.Y, d.MountingHeightMetres, d.OrientationDegrees, d.QueueZone,
                d.FootprintLengthMetres, d.FootprintWidthMetres, d.FootprintRadiusMetres), d.MappingName)).ToList();

        // Alert rules and displays: the entities' own checks, then their zones against the imported draft (owner decision
        // 2026-10-11): queue zones and overflow bands for rules (queue zones only for health checks and open desks below recommendation), queue
        // zones for displays.
        var queues = draft?.Zones.Where(z => z.Kind == ZoneKind.Queue).Select(z => z.Name).ToHashSet(StringComparer.Ordinal) ?? [];
        var overflows = draft?.Zones.Where(z => z.Kind == ZoneKind.Overflow).Select(z => z.Name).ToHashSet(StringComparer.Ordinal) ?? [];
        var rules = new List<AlertRule>();
        foreach (var row in content.AlertRules)
        {
            var metric = SiteConfigurationRules.ParseKind<AlertMetric>(row.Metric).GetValueOrDefault();
            if (AlertRuleErrors.NotEvaluatedMetrics.Contains(metric))
                return Result.Error<SiteConfigurationPlan>(SiteConfigurationErrors.InvalidAlertRules);
            // ARV-181b2, ARV-187: a rule on a border-only metric (open desks below recommendation, the predicted nowcast)
            // passes the entity's border checks (a border owner, never none; a border escalation role or none; for open desks
            // below recommendation, fires above) like any other value, so the import refuses it otherwise.
            AlertRule rule = null;
            built = Guarded(SiteConfigurationErrors.InvalidAlertRules, () => rule = new AlertRule(siteCode, SiteConfigurationRules.RuleNumber(row.Code).GetValueOrDefault(),
                new AlertRuleValues(row.Name, row.Zones, metric, SiteConfigurationRules.ParseKind<AlertComparator>(row.Comparator).GetValueOrDefault(), row.Threshold,
                    row.MinQueueLength, row.ClearThreshold, row.SustainMinutes, row.ClearAfterMinutes, SiteConfigurationRules.ParseKind<AlertSeverity>(row.Severity).GetValueOrDefault(),
                    Blank(row.OwnerRole), row.EscalateAfterMinutes, Blank(row.EscalateToRole), EscalationContact: null, row.NotifyByEmail, row.Enabled, row.LeadMinutes)));
            if (built is not null)
                return Result.Error<SiteConfigurationPlan>(built);
            if (!ZonesOf(rule.Zones, rule.Metric, queues, overflows))
                return Result.Error<SiteConfigurationPlan>(SiteConfigurationErrors.AlertRuleZonesNotInProfile);
            rules.Add(rule);
        }

        var displays = new List<Display>();
        foreach (var row in content.Displays)
        {
            Display display = null;
            built = Guarded(SiteConfigurationErrors.InvalidDisplays, () => display = new Display(siteCode, row.Code, new DisplayValues(row.Name, row.Location,
                SiteConfigurationRules.ParseKind<DisplayOrientation>(row.Orientation).GetValueOrDefault(), row.Languages, row.BandMinutes, row.HysteresisMinutes,
                row.StaleSeconds, [.. row.Entries.Select(e => new DisplayEntry(e.Zone, e.Labels))], row.Fallback, row.Enabled)));
            if (built is not null)
                return Result.Error<SiteConfigurationPlan>(built);
            if (display.EntryList.Any(e => !queues.Contains(e.Zone)))
                return Result.Error<SiteConfigurationPlan>(SiteConfigurationErrors.DisplayZonesNotInProfile);
            displays.Add(display);
        }

        var needAccess = content.Devices.Where(d => d.AccessSettingsWithheld).Select(d => d.Code).ToList();
        var needContact = content.AlertRules.Where(r => r.EscalationContactWithheld).Select(r => r.Code).ToList();
        var counts = new SiteConfigurationCountsViewModel(terminals.Count, levels.Count, checkpoints.Count, desks.Count, mappings.Count,
            draft?.Zones.Count ?? 0, draft?.Lines.Count ?? 0, plans.Count, devices.Count, rules.Count, displays.Count);
        return new Result<SiteConfigurationPlan>(new SiteConfigurationPlan([.. terminals.Values], [.. levels.Values], [.. checkpoints.Values], [.. desks.Values],
            mappings, draft, portable, plans, devices, rules, displays, needAccess, needContact, counts));
    }

    /// <summary>An empty role is no role, as the rule service reads one.</summary>
    private static string Blank(string role) => string.IsNullOrWhiteSpace(role) ? null : role.Trim();

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
