using System.Text;
using System.Text.RegularExpressions;
using Ariva.Core;
using Ariva.Core.Domain.Components;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Sensing;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Security;
using FluentAssertions;

namespace Ariva.UnitTests.Domain.Topology;

/// <summary>
/// ARV-122a: export names every reference by codes and names and carries no id; import creates everything anew at the
/// target site through the entities' methods, remaps every reference through the bundle's codes and names, rebases it to
/// the target's site code, and reproduces the portable geometry hash; refusals are fixed texts. ARV-122b: devices, alert
/// rules and displays travel without any credential, calibration, access setting or escalation contact; their zones are
/// checked against the imported draft.
/// </summary>
public sealed class SiteConfigurationTransferTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 8, 0, 0, DateTimeKind.Utc);

    private static T WithId<T>(T entity) where T : Ariva.Core.Domain.Common.EntityBase<T>
    {
        entity.Id = Guid.CreateVersion7();
        return entity;
    }

    private static IReadOnlyList<FloorPoint> Rect(double x, double y, double w, double h) =>
        [new(x, y), new(x + w, y), new(x + w, y + h), new(x, y + h)];

    /// <summary>
    /// A source site: two levels, an immigration checkpoint with a desk and an e-gate out of service, a check-in counter, two
    /// mappings, a published profile with a queue zone and its overflow band, a plan; two devices with credentials and access
    /// settings, two alert rules (one with an escalation contact) and a paired display.
    /// </summary>
    private static SiteConfigurationSource Source(string siteCode = "SRC", string iata = "AAA", string queueName = "Snake A")
    {
        var airport = WithId(new Airport(iata, null, "Source airport", "Asia/Dubai"));
        var terminal = WithId(airport.AddTerminal("T1", "Terminal <b>1</b>", siteCode));
        var arrivals = WithId(terminal.AddLevel("L0", "Arrivals", 0, 60, 40));
        var departures = WithId(terminal.AddLevel("L1", "Departures", 1, 80, 50));
        var immigration = WithId(arrivals.AddCheckpoint("IMM", "Immigration", CheckpointKind.Immigration));
        var desk = WithId(immigration.AddDesk("D01", null, DeskKind.Desk, ["RES", "CIT"]));
        var gate = WithId(immigration.AddDesk("EG01", "E-gate 1", DeskKind.EGate, ["EG"]));
        gate.SetInService(false);
        var checkIn = WithId(departures.AddCheckpoint("CHK", "Check-in", CheckpointKind.CheckIn));
        var counter = WithId(checkIn.AddDesk("C01", "' OR '1'='1", DeskKind.Counter, []));
        var mappings = new List<DeskCodeMapping> { WithId(new DeskCodeMapping(ExternalSystem.Aman, "imm-01", desk)), WithId(new DeskCodeMapping(ExternalSystem.Aodb, "C/01", counter)) };

        var profile = WithId(new ZoneProfile(siteCode, "Arrivals"));
        var queue = profile.AddZone(queueName, ZoneKind.Queue, arrivals, Rect(10, 10, 24, 12));
        profile.SetZoneLane(queue, "CIT");
        profile.SetZoneCapacity(queue, 190);
        profile.AddZone("Service A", ZoneKind.Service, arrivals, Rect(40, 10, 4, 4), queue, desk.Id);
        var overflow = profile.AddZone("Overflow A", ZoneKind.Overflow, arrivals, Rect(10, 24, 24, 4), queue);
        profile.AddLine("Entry A", LineRole.Entry, arrivals, new FloorPoint(10, 12), new FloorPoint(10, 16), queue);
        profile.AddLine("Exit A", LineRole.Exit, arrivals, new FloorPoint(30, 22), new FloorPoint(34, 22), queue);
        profile.AddLine("Overflow entry A", LineRole.OverflowEntry, arrivals, new FloorPoint(10, 25), new FloorPoint(10, 27), overflow);
        profile.AddLine("Count hall", LineRole.Count, departures, new FloorPoint(1, 1), new FloorPoint(1, 9));
        var levels = new Dictionary<Guid, Level> { [arrivals.Id!.Value] = arrivals, [departures.Id!.Value] = departures };
        profile.Publish(3, levels, "admin", Now).Should().BeEmpty();

        var plan = WithId(new FloorPlan(arrivals, "0199a0000000700080000000000000aa.svg", "image/svg+xml", 10, new string('a', 64), "arrivals.svg", 100, 50, 0.05, 1, 2));
        return new SiteConfigurationSource([terminal], [arrivals, departures], [immigration, checkIn], [desk, gate, counter], mappings, profile, [plan],
            Devices(arrivals, queueName), Rules(siteCode, queueName), [Board(siteCode, "DSP1", queueName)]);
    }

    /// <summary>A stereo vision sensor on the assumed footprint with a credential and access settings; a LiDAR with a vendor radius.</summary>
    private static List<Device> Devices(Level level, string queueName)
    {
        var stereo = new Device("S01", DeviceFamily.StereoVision, "Xovis <PC2>", DeviceTransport.HttpsPush, DeviceDialect.Xovis, ClockSource.Ntp, level, 20, 16, 5, 0,
            CoverageFootprint.Assumed(DeviceFamily.StereoVision, 5), queueName, Now);
        var issued = DeviceCredentials.New();
        stereo.IssueCredential(issued.Prefix, issued.Hash, Now);
        stereo.SetAccess(["10.20.0.0/24"], new string('c', 64), Now);
        var lidar = new Device("S02", DeviceFamily.Lidar, "Ouster", DeviceTransport.HttpsPush, DeviceDialect.Canonical, ClockSource.Ptp, level, 22, 16, 6, 90,
            CoverageFootprint.FromVendor(null, null, 8), queueName, Now);
        var second = DeviceCredentials.New();
        lidar.IssueCredential(second.Prefix, second.Hash, Now);
        return [lidar, stereo];
    }

    private static AlertRuleValues Rule(string name, IReadOnlyList<string> zones, AlertMetric metric = AlertMetric.Nowcast, string contact = null) =>
        AlertRuleValues.IsCondition(metric)
            ? new AlertRuleValues(name, zones, metric, AlertComparator.IsTrue, null, null, null, 5, 5, AlertSeverity.Info, null, null, null, contact, false, true)
            : new AlertRuleValues(name, zones, metric, AlertComparator.GreaterThan, 20, null, null, 5, 5, AlertSeverity.Warning, RoleCodes.BorderShiftSupervisor,
                contact is null ? null : 15, null, contact, true, true);

    /// <summary>R-001 with an escalation contact, R-003 on the overflow band (numbers skip, as after a deletion).</summary>
    private static List<AlertRule> Rules(string siteCode, string queueName) =>
    [
        WithId(new AlertRule(siteCode, 3, Rule("Overflow in use", ["Overflow A"], AlertMetric.OverflowOccupied))),
        WithId(new AlertRule(siteCode, 1, Rule("Long wait <script>alert(1)</script>", [queueName], contact: "Duty officer +971 50 000 0000")))
    ];

    private static DisplayValues BoardValues(IReadOnlyList<string> zones) =>
        new("Hall board <img src=x onerror=alert(1)>", "Gate 3", DisplayOrientation.Landscape, ["en", "ar"], 5, 1, 300,
            [.. zones.Select(z => new DisplayEntry(z, new Dictionary<string, string> { ["en"] = "Citizens", ["ar"] = "\u0645\u0648\u0627\u0637\u0646\u0648\u0646" }))],
            new Dictionary<string, string> { ["en"] = "Ask a member of staff", ["ar"] = "\u0627\u0633\u0623\u0644" }, true);

    /// <summary>A display paired with a player credential.</summary>
    private static Display Board(string siteCode, string code, params string[] zones)
    {
        var display = WithId(new Display(siteCode, code, BoardValues(zones)));
        var issued = DisplayCredentials.New();
        display.SetCredential(issued.Prefix, issued.Hash, Now);
        return display;
    }

    /// <summary>The source without devices, alert rules and displays.</summary>
    private static SiteConfigurationSource Bare(SiteConfigurationSource source) => source with { Devices = [], AlertRules = [], Displays = [] };

    /// <summary>Every exported part as the bundle carries it.</summary>
    private static string AllParts(ExportedSiteConfiguration data) =>
        string.Concat(new[]
        {
            SiteConfigurationParts.Write(data.Topology), SiteConfigurationParts.Write(data.ZoneProfiles), SiteConfigurationParts.Write(data.DeskCodeMappings),
            SiteConfigurationParts.Write(data.FloorPlans), SiteConfigurationParts.Write(data.Devices), SiteConfigurationParts.Write(data.AlertRules),
            SiteConfigurationParts.Write(data.Displays)
        }.Select(Encoding.UTF8.GetString));

    /// <summary>The content an import reads: the export's parts through the bundle, as the service passes them.</summary>
    private static SiteConfigurationContent RoundTrip(ExportedSiteConfiguration exported, byte[] file = null)
    {
        var parts = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [SiteConfigurationBundles.Topology] = SiteConfigurationParts.Write(exported.Topology),
            [SiteConfigurationBundles.DeskCodeMappings] = SiteConfigurationParts.Write(exported.DeskCodeMappings),
            [SiteConfigurationBundles.ZoneProfile] = SiteConfigurationParts.Write(exported.ZoneProfiles),
            [SiteConfigurationBundles.FloorPlans] = SiteConfigurationParts.Write(exported.FloorPlans),
            [SiteConfigurationBundles.Devices] = SiteConfigurationParts.Write(exported.Devices),
            [SiteConfigurationBundles.AlertRules] = SiteConfigurationParts.Write(exported.AlertRules),
            [SiteConfigurationBundles.Displays] = SiteConfigurationParts.Write(exported.Displays)
        };
        for (var number = 1; number <= exported.FloorPlanFiles.Count; number++)
            parts[SiteConfigurationBundles.FloorPlanFile(number)] = file ?? "<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8.ToArray();
        var bundle = SiteConfigurationBundles.Build("SRC", Now, parts);
        var opened = SiteConfigurationBundles.Open(new ImportSiteConfigurationRequest(bundle.FormatVersion, bundle.SourceSite, bundle.ExportedAtUtc,
            new Dictionary<string, string>(bundle.Manifest), new Dictionary<string, string>(bundle.Parts)));
        opened.HasErrors.Should().BeFalse(string.Join(", ", opened.ErrorMessages ?? []));
        var content = SiteConfigurationParts.Read(opened.Data);
        content.HasErrors.Should().BeFalse(string.Join(", ", content.ErrorMessages ?? []));
        return content.Data;
    }

    private static Dictionary<string, Airport> Airports(params Airport[] airports) => airports.ToDictionary(a => a.IataCode, StringComparer.Ordinal);

    /// <summary>A fresh target airport: a plan adds its terminals to the airport it is given.</summary>
    private static Dictionary<string, Airport> Target(string iata = "ZZZ") => Airports(WithId(new Airport(iata, null, "Target", "Asia/Dubai")));

    #region Export

    [Fact]
    public void Export_Should_NameEveryReferenceByCodesAndCarryNoId_When_TheSiteIsExported()
    {
        var source = Source();

        var exported = SiteConfigurationTransfer.Export(source);

        exported.HasErrors.Should().BeFalse(string.Join(", ", exported.ErrorMessages ?? []));
        var data = exported.Data;
        data.Topology.Terminals.Should().Equal(new TerminalPartRow("AAA", "T1", "Terminal <b>1</b>"));
        data.Topology.Levels.Select(l => (l.Terminal, l.Code, l.FloorNumber)).Should().Equal(("T1", "L0", 0), ("T1", "L1", 1));
        data.Topology.Checkpoints.Select(c => (c.Terminal, c.Level, c.Code, c.Kind)).Should().Equal(("T1", "L1", "CHK", "CheckIn"), ("T1", "L0", "IMM", "Immigration"));
        data.Topology.Desks.Select(d => (d.Checkpoint, d.Code, d.InService)).Should().Equal(("CHK", "C01", true), ("IMM", "D01", true), ("IMM", "EG01", false));
        data.Topology.Desks.Single(d => d.Code == "D01").LaneCategories.Should().Equal("CIT", "RES");
        data.DeskCodeMappings.Should().Equal(new DeskCodeMappingPartRow("Aman", "IMM-01", "IMM", "D01"), new DeskCodeMappingPartRow("Aodb", "C/01", "CHK", "C01"));
        var profile = data.ZoneProfiles.Should().ContainSingle().Subject;
        profile.Version.Should().Be(3);
        profile.GeometryHash.Should().Be(source.PublishedProfile.GeometryHash);
        profile.PortableGeometryHash.Should().MatchRegex("^[0-9a-f]{64}$").And.Be(data.PortableGeometryHash);
        profile.Zones.Single(z => z.Name == "Service A").Should().Match<ZonePartRow>(z => z.Checkpoint == "IMM" && z.Desk == "D01" && z.QueueZone == "Snake A" && z.Level == "L0");
        profile.Zones.Single(z => z.Name == "Snake A").Should().Match<ZonePartRow>(z => z.LaneCategory == "CIT" && z.PhysicalCapacity == 190);
        profile.Lines.Single(l => l.Name == "Count hall").Should().Match<LinePartRow>(l => l.Zone == null && l.Level == "L1");
        data.FloorPlans.Should().Equal(new FloorPlanPartRow("T1", "L0", "arrivals.svg", 0.05, 1, 2));
        data.Counts.Should().Be(new Ariva.Core.Domain.ViewModels.SiteConfigurationCountsViewModel(1, 2, 2, 3, 2, 3, 4, 1, 2, 2, 1));

        var json = AllParts(data);
        Regex.IsMatch(json, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-").Should().BeFalse("no id travels in a bundle");
        json.Should().NotContain("SRC", "no part names the source site");
    }

    [Fact]
    public void Export_Should_CarryAnEmptyProfileList_When_TheSiteHasNoPublishedProfile()
    {
        var source = Bare(Source()) with { PublishedProfile = null };

        var data = SiteConfigurationTransfer.Export(source).Data;

        data.ZoneProfiles.Should().BeEmpty();
        data.PortableGeometryHash.Should().BeNull();
        data.Counts.Zones.Should().Be(0);
        data.Devices.Should().BeEmpty();
        data.AlertRules.Should().BeEmpty();
        data.Displays.Should().BeEmpty();
    }

    [Fact]
    public void Export_Should_RefuseTheSite_When_TwoTerminalsShareACodeAtTwoAirports()
    {
        var source = Source();
        var other = WithId(WithId(new Airport("BBB", null, "Other", "Asia/Dubai")).AddTerminal("T1", "Other T1", "SRC"));

        SiteConfigurationTransfer.Export(source with { Terminals = [.. source.Terminals, other] }).ErrorMessages
            .Should().Equal(SiteConfigurationErrors.AmbiguousTerminals);
    }

    [Fact]
    public void Export_Should_RefuseTheProfile_When_ItNamesALevelThatIsNoLongerLive()
    {
        var source = Source();

        SiteConfigurationTransfer.Export(source with { Levels = [source.Levels[1]] }).ErrorMessages.Should().Equal(SiteConfigurationErrors.ProfileOnRemovedTopology);
        SiteConfigurationTransfer.Export(source with { Desks = [source.Desks[1], source.Desks[2]] }).ErrorMessages
            .Should().Equal([SiteConfigurationErrors.ProfileOnRemovedTopology], "the service zone's desk is gone");
    }

    [Fact]
    public void Export_Should_RefuseRatherThanCut_When_AKindPassesItsBound()
    {
        var source = Bare(Source()) with { PublishedProfile = null, FloorPlans = [] };
        var airport = WithId(new Airport("CCC", null, "Many", "Asia/Dubai"));
        var atBound = Enumerable.Range(1, SiteConfigurationLimits.MaxTerminals).Select(n => WithId(airport.AddTerminal($"T{n}", $"T{n}", "SRC"))).ToList();

        SiteConfigurationTransfer.Export(source with { Terminals = atBound, Levels = [], Checkpoints = [], Desks = [], DeskCodeMappings = [] })
            .Data.Counts.Terminals.Should().Be(SiteConfigurationLimits.MaxTerminals);
        SiteConfigurationTransfer.Export(source with { Terminals = [.. atBound, WithId(airport.AddTerminal("T99", "T99", "SRC"))] })
            .ErrorMessages.Should().Equal(SiteConfigurationErrors.TooManyRecords);
    }

    [Fact]
    public void Export_Should_CarryDevicesRulesAndDisplaysWithoutCredentialsContactsOrAccessSettings_When_TheSiteHasThem()
    {
        var source = Source();

        var data = SiteConfigurationTransfer.Export(source).Data;

        data.Devices.Should().Equal(
            new DevicePartRow("S01", "StereoVision", "Xovis <PC2>", "HttpsPush", "Xovis", "Ntp", null, "T1", "L0", 20, 16, 5, 0, "Snake A", null, null, null, true),
            new DevicePartRow("S02", "Lidar", "Ouster", "HttpsPush", "Canonical", "Ptp", null, "T1", "L0", 22, 16, 6, 90, "Snake A", null, null, 8, false));
        data.AlertRules.Select(r => (r.Code, r.Zones.Single(), r.EscalationContactWithheld)).Should().Equal(("R-001", "Snake A", true), ("R-003", "Overflow A", false));
        var withContact = data.AlertRules[0];
        withContact.Should().Match<AlertRulePartRow>(r => r.Metric == "Nowcast" && r.Comparator == "GreaterThan" && r.Threshold == 20 &&
                                                         r.OwnerRole == RoleCodes.BorderShiftSupervisor && r.EscalateAfterMinutes == 15 && r.NotifyByEmail);
        var display = data.Displays.Should().ContainSingle().Subject;
        (display.Code, display.Orientation, display.BandMinutes, display.StaleSeconds).Should().Be(("DSP1", "Landscape", 5, 300));
        display.Languages.Should().Equal("en", "ar");
        display.Entries.Should().ContainSingle().Which.Zone.Should().Be("Snake A");
        display.Fallback.Keys.Should().BeEquivalentTo("en", "ar");

        // Booleans only, so a failure prints no credential material.
        var json = AllParts(data);
        Regex.IsMatch(json, "ard[kp]_").Should().BeFalse("no device credential and no pairing secret is exported");
        source.Devices.Any(d => json.Contains(d.CredentialHash, StringComparison.Ordinal) || json.Contains(d.CredentialPrefix, StringComparison.Ordinal))
            .Should().BeFalse("no device credential hash or prefix is exported");
        (json.Contains(source.Displays[0].CredentialHash, StringComparison.Ordinal) || json.Contains(source.Displays[0].CredentialPrefix, StringComparison.Ordinal))
            .Should().BeFalse("no display credential hash or prefix is exported");
        // Only the matching fixed words are printed on a failure, never the parts.
        new[] { "Duty officer", "+971" }.Where(w => json.Contains(w, StringComparison.Ordinal)).Should().BeEmpty("an escalation contact is never exported");
        new[] { "10.20.0.0", new string('c', 64), "allowedSources" }.Where(w => json.Contains(w, StringComparison.Ordinal))
            .Should().BeEmpty("access settings are not exported, only that they were withheld");
        new[] { "Calibration", "credential", "Credential", "issued", "Issued", "State", "Commissioning" }.Where(w => json.Contains(w, StringComparison.Ordinal))
            .Should().BeEmpty("no calibration, credential or state member");
    }

    [Fact]
    public void Export_Should_RefuseTheSite_When_ADeviceRuleOrDisplayNamesAZoneThePublishedProfileLacks()
    {
        var source = Source();
        var arrivals = source.Levels[0];
        var stray = new Device("S09", DeviceFamily.StereoVision, "Xovis", DeviceTransport.HttpsPush, DeviceDialect.Xovis, ClockSource.Ntp, arrivals, 20, 16, 5, 0,
            CoverageFootprint.Assumed(DeviceFamily.StereoVision, 5), "Snake B", Now);
        var strayRule = WithId(new AlertRule("SRC", 9, Rule("Stray", ["Snake B"])));
        var healthOnOverflow = WithId(new AlertRule("SRC", 10, new AlertRuleValues("Health", ["Overflow A"], AlertMetric.TrackCompletion, AlertComparator.LessThan, 80,
            null, null, 5, 5, AlertSeverity.Warning, null, null, null, null, false, true)));
        var serviceRule = WithId(new AlertRule("SRC", 11, Rule("Service", ["Service A"])));

        SiteConfigurationTransfer.Export(source with { Devices = [.. source.Devices, stray] }).ErrorMessages.Should().Equal(SiteConfigurationErrors.ReferencesOutsideProfile);
        SiteConfigurationTransfer.Export(source with { AlertRules = [strayRule] }).ErrorMessages.Should().Equal(SiteConfigurationErrors.ReferencesOutsideProfile);
        SiteConfigurationTransfer.Export(source with { AlertRules = [healthOnOverflow] }).ErrorMessages
            .Should().Equal([SiteConfigurationErrors.ReferencesOutsideProfile], "a health check watches queue zones only");
        SiteConfigurationTransfer.Export(source with { AlertRules = [serviceRule] }).ErrorMessages.Should().Equal(SiteConfigurationErrors.ReferencesOutsideProfile);
        SiteConfigurationTransfer.Export(source with { Displays = [Board("SRC", "DSP2", "Overflow A")] }).ErrorMessages
            .Should().Equal([SiteConfigurationErrors.ReferencesOutsideProfile], "a display shows queue zones only");
        SiteConfigurationTransfer.Export(source with { PublishedProfile = null }).ErrorMessages
            .Should().Equal([SiteConfigurationErrors.ReferencesOutsideProfile], "without a published profile no zone is known");
    }

    [Fact]
    public void Export_Should_RefuseTheSite_When_ADeviceHangsOnALevelThatIsNoLongerLive()
    {
        var source = Source() with { PublishedProfile = null, AlertRules = [], Displays = [] };

        SiteConfigurationTransfer.Export(source with { Levels = [source.Levels[1]] }).ErrorMessages.Should().Equal(SiteConfigurationErrors.DeviceOnRemovedLevel);
    }

    [Fact]
    public void Export_Should_RefuseRatherThanCut_When_DevicesRulesOrDisplaysPassTheirBounds()
    {
        var source = Source();
        var arrivals = source.Levels[0];
        var devices = Enumerable.Range(1, SiteConfigurationLimits.MaxDevices + 1).Select(n => new Device($"D{n}", DeviceFamily.StereoVision, "Xovis",
            DeviceTransport.HttpsPush, DeviceDialect.Xovis, ClockSource.Ntp, arrivals, 20, 16, 5, 0, CoverageFootprint.Assumed(DeviceFamily.StereoVision, 5), "Snake A", Now)).ToList();
        var rules = Enumerable.Range(1, SiteConfigurationLimits.MaxAlertRules + 1).Select(n => WithId(new AlertRule("SRC", n, Rule($"Rule {n}", ["Snake A"])))).ToList();
        var displays = Enumerable.Range(1, SiteConfigurationLimits.MaxDisplays + 1).Select(n => WithId(new Display("SRC", $"B{n}", BoardValues(["Snake A"])))).ToList();

        SiteConfigurationTransfer.Export(source with { Devices = devices[..^1] }).Data.Counts.Devices.Should().Be(SiteConfigurationLimits.MaxDevices);
        SiteConfigurationTransfer.Export(source with { Devices = devices }).ErrorMessages.Should().Equal(SiteConfigurationErrors.TooManyRecords);
        SiteConfigurationTransfer.Export(source with { AlertRules = rules[..^1] }).Data.Counts.AlertRules.Should().Be(SiteConfigurationLimits.MaxAlertRules);
        SiteConfigurationTransfer.Export(source with { AlertRules = rules }).ErrorMessages.Should().Equal(SiteConfigurationErrors.TooManyRecords);
        SiteConfigurationTransfer.Export(source with { Displays = displays[..^1] }).Data.Counts.Displays.Should().Be(SiteConfigurationLimits.MaxDisplays);
        SiteConfigurationTransfer.Export(source with { Displays = displays }).ErrorMessages.Should().Equal(SiteConfigurationErrors.TooManyRecords);
    }

    [Fact]
    public void Export_Should_RefuseTheSite_When_ARuleCodeIsAboveWhatAnImportTakes()
    {
        var source = Source();
        var atBound = WithId(new AlertRule("SRC", SiteConfigurationLimits.MaxImportedRuleNumber, Rule("Last importable", ["Snake A"])));
        var beyond = WithId(new AlertRule("SRC", SiteConfigurationLimits.MaxImportedRuleNumber + 1, Rule("Beyond", ["Snake A"])));

        SiteConfigurationTransfer.Export(source with { AlertRules = [.. source.AlertRules, atBound] }).Data.AlertRules.Select(r => r.Code)
            .Should().Equal("R-001", "R-003", "R-100000");
        SiteConfigurationTransfer.Export(source with { AlertRules = [.. source.AlertRules, beyond] }).ErrorMessages
            .Should().Equal([SiteConfigurationErrors.RuleCodesBeyondImportBound], "every export can be imported");
        SiteConfigurationErrors.Conflicts.Should().Contain(SiteConfigurationErrors.RuleCodesBeyondImportBound);
    }

    #endregion

    #region Import: remapping and rebasing

    [Fact]
    public async Task Plan_Should_RecreateTheSiteWithNewIdsAtTheTargetSite_When_TheExportIsImported()
    {
        var source = Source();
        var exported = SiteConfigurationTransfer.Export(source).Data;
        var content = RoundTrip(exported);
        (await SiteConfigurationRules.Content().ValidateAllAsync(content)).HasErrors.Should().BeFalse();
        var target = WithId(new Airport("ZZZ", null, "Target airport", "Asia/Dubai"));

        var planned = SiteConfigurationTransfer.Plan("DST-COPY", content, Airports(target), "ZZZ");

        planned.HasErrors.Should().BeFalse(string.Join(", ", planned.ErrorMessages ?? []));
        var plan = planned.Data;
        plan.Counts.Should().Be(exported.Counts);
        var sourceIds = source.Terminals.Select(t => t.Id).Concat(source.Levels.Select(l => l.Id)).Concat(source.Checkpoints.Select(c => c.Id))
            .Concat(source.Desks.Select(d => d.Id)).Append(source.PublishedProfile.Id).ToHashSet();
        plan.Terminals.Select(t => t.Id).Concat(plan.Levels.Select(l => l.Id)).Concat(plan.Checkpoints.Select(c => c.Id)).Concat(plan.Desks.Select(d => d.Id))
            .Append(plan.Draft.Id).Should().OnlyContain(id => id != null && !sourceIds.Contains(id), "every record gets a new id");
        plan.Terminals.Should().ContainSingle().Which.Airport.Should().BeSameAs(target);
        new IEnumerable<string>[] { plan.Terminals.Select(t => t.SiteCode), plan.Levels.Select(l => l.SiteCode), plan.Checkpoints.Select(c => c.SiteCode),
            plan.Desks.Select(d => d.SiteCode), plan.DeskCodeMappings.Select(m => m.SiteCode), [plan.Draft.SiteCode] }
            .SelectMany(codes => codes).Should().OnlyContain(code => code == "DST-COPY", "rebased to the target site");

        plan.Desks.Single(d => d.Code == "EG01").InService.Should().BeFalse();
        plan.Desks.Single(d => d.Code == "C01").Name.Should().Be("' OR '1'='1", "a hostile name is kept as data");
        plan.DeskCodeMappings.Single(m => m.System == ExternalSystem.Aman).Desk.Should().BeSameAs(plan.Desks.Single(d => d.Code == "D01"));
        plan.Draft.Status.Should().Be(ZoneProfileStatus.Draft, "the profile arrives as a draft");
        plan.Draft.Version.Should().BeNull();
        var service = plan.Draft.Zones.Single(z => z.Name == "Service A");
        service.DeskId.Should().Be(plan.Desks.Single(d => d.Code == "D01").Id);
        service.QueueZone.Should().BeSameAs(plan.Draft.Zones.Single(z => z.Name == "Snake A"));
        service.LevelId.Should().Be(plan.Levels.Single(l => l.Code == "L0").Id!.Value);
        plan.Draft.Zones.Single(z => z.Name == "Snake A").PhysicalCapacity.Should().Be(190);
        plan.Draft.Lines.Single(l => l.Name == "Count hall").LevelId.Should().Be(plan.Levels.Single(l => l.Code == "L1").Id!.Value);

        plan.PortableGeometryHash.Should().Be(exported.PortableGeometryHash, "the round trip keeps the portable geometry hash");
        plan.Draft.ComputeGeometryHash().Should().NotBe(source.PublishedProfile.GeometryHash, "F22 v1 names levels and desks by their new ids");
        var levels = plan.Levels.ToDictionary(l => l.Id!.Value);
        plan.Draft.Validate(levels).Should().BeEmpty("the draft is publishable as the source was");
        plan.FloorPlans.Should().ContainSingle().Which.Level.Should().BeSameAs(plan.Levels.Single(l => l.Code == "L0"));
    }

    [Fact]
    public void Plan_Should_KeepEachTerminalAtItsOwnAirport_When_NoAirportIsNamed()
    {
        var content = RoundTrip(SiteConfigurationTransfer.Export(Source(iata: "AAA")).Data);
        var sameCode = WithId(new Airport("AAA", null, "Same code elsewhere", "Asia/Dubai"));

        SiteConfigurationTransfer.Plan("DST", content, Airports(sameCode)).Data.Terminals.Single().Airport.Should().BeSameAs(sameCode);
        SiteConfigurationTransfer.Plan("DST", content, Airports()).ErrorMessages.Should().Equal(SiteConfigurationErrors.UnknownAirport);
        SiteConfigurationTransfer.Plan("DST", content, Airports(sameCode), "QQQ").ErrorMessages.Should().Equal(SiteConfigurationErrors.UnknownAirport);
    }

    [Fact]
    public void Plan_Should_RefuseATerminalCodeTakenAtItsAirport_When_AnotherSiteHoldsItLive()
    {
        var content = RoundTrip(SiteConfigurationTransfer.Export(Source()).Data);
        var taken = WithId(new Airport("ZZZ", null, "Target", "Asia/Dubai"));
        WithId(taken.AddTerminal("T1", "Another site's T1", "OTHER"));
        var freed = WithId(new Airport("YYY", null, "Target", "Asia/Dubai"));
        WithId(freed.AddTerminal("T1", "Deleted T1", "OTHER")).SoftDelete("admin", Now);

        SiteConfigurationTransfer.Plan("DST", content, Airports(taken), "ZZZ").ErrorMessages.Should().Equal(SiteConfigurationErrors.TerminalTaken);
        SiteConfigurationTransfer.Plan("DST", content, Airports(freed), "YYY").HasErrors.Should().BeFalse("a deleted terminal frees its code");
    }

    [Fact]
    public void Plan_Should_RefuseAQueueZoneNameTooLongForTheTargetSite_When_TheTargetCodeIsLonger()
    {
        // ARV-114c: a queue zone's events are keyed by the site code, a slash and its name (200 characters).
        var longest = ZoneKeys.MaxQueueZoneNameLength("DST-COPY");
        var fits = new string('Q', longest);
        var tooLong = new string('Q', longest + 1);

        var atBound = RoundTrip(SiteConfigurationTransfer.Export(Source(siteCode: "AB", queueName: fits)).Data);
        var beyond = RoundTrip(SiteConfigurationTransfer.Export(Source(siteCode: "AB", queueName: tooLong)).Data);

        SiteConfigurationTransfer.Plan("DST-COPY", atBound, Target(), "ZZZ").HasErrors.Should().BeFalse();
        SiteConfigurationTransfer.Plan("DST-COPY", beyond, Target(), "ZZZ").ErrorMessages.Should().Equal(SiteConfigurationErrors.QueueZoneNameTooLong);
        SiteConfigurationTransfer.Plan("AB", beyond, Target(), "ZZZ").HasErrors.Should().BeFalse("it fits the shorter source code");
    }

    [Fact]
    public void Plan_Should_RefuseTheProfile_When_ItDoesNotReproduceItsPortableHash()
    {
        var content = RoundTrip(SiteConfigurationTransfer.Export(Source()).Data);
        var profile = content.ZoneProfile with { PortableGeometryHash = new string('0', 64) };
        var airport = WithId(new Airport("ZZZ", null, "Target", "Asia/Dubai"));

        SiteConfigurationTransfer.Plan("DST", content with { ZoneProfiles = [profile] }, Airports(airport), "ZZZ").ErrorMessages
            .Should().Equal(SiteConfigurationErrors.ProfileNotReproduced);
    }

    [Fact]
    public void Plan_Should_AnswerThePartsFixedText_When_AnEntityRefusesARow()
    {
        var content = RoundTrip(SiteConfigurationTransfer.Export(Source()).Data);
        var topology = content.Topology;
        // A counter at an immigration checkpoint, an AMAN code for a check-in counter, a zone outside its level: the
        // entities refuse them with messages that name the row; the answer is the part's fixed text.
        var counterAtImmigration = content with
        {
            Topology = topology with { Desks = [.. topology.Desks, new DeskPartRow("IMM", "C99", null, "Counter", [], true)] }
        };
        var amanCounter = content with { DeskCodeMappings = [new DeskCodeMappingPartRow("Aman", "X1", "CHK", "C01")] };
        var outside = content with
        {
            ZoneProfiles = [content.ZoneProfile with { Zones = [.. content.ZoneProfile.Zones.Select(z => z.Name == "Service A" ? z with { Polygon = "100 100,101 100,101 101" } : z)] }]
        };

        SiteConfigurationTransfer.Plan("DST", counterAtImmigration, Target(), "ZZZ").ErrorMessages.Should().Equal(SiteConfigurationErrors.InvalidTopology);
        SiteConfigurationTransfer.Plan("DST", amanCounter, Target(), "ZZZ").ErrorMessages.Should().Equal(SiteConfigurationErrors.InvalidDeskCodeMappings);
        SiteConfigurationTransfer.Plan("DST", outside, Target(), "ZZZ").ErrorMessages.Should().Equal(SiteConfigurationErrors.InvalidZoneProfile);
    }

    [Fact]
    public void Plan_Should_CreateNothingButTheTopology_When_TheBundleCarriesOnlyThat()
    {
        var content = RoundTrip(SiteConfigurationTransfer.Export(Bare(Source()) with { PublishedProfile = null, FloorPlans = [], DeskCodeMappings = [] }).Data);
        var airport = WithId(new Airport("ZZZ", null, "Target", "Asia/Dubai"));

        var plan = SiteConfigurationTransfer.Plan("DST", content, Airports(airport), "ZZZ").Data;

        plan.Draft.Should().BeNull();
        plan.PortableGeometryHash.Should().BeNull();
        plan.FloorPlans.Should().BeEmpty();
        plan.Counts.Desks.Should().Be(3);
        plan.Devices.Should().BeEmpty();
        plan.AlertRules.Should().BeEmpty();
        plan.Displays.Should().BeEmpty();
        plan.RulesNeedingEscalationContact.Should().BeEmpty();
        plan.DevicesNeedingAccessSettings.Should().BeEmpty();
    }

    #endregion

    #region Import: devices, alert rules and displays (ARV-122b)

    [Fact]
    public void Plan_Should_RegisterEachDeviceOnItsNewLevel_When_TheBundleCarriesDevices()
    {
        var content = RoundTrip(SiteConfigurationTransfer.Export(Source()).Data);

        var plan = SiteConfigurationTransfer.Plan("DST-COPY", content, Target(), "ZZZ").Data;

        var arrivals = plan.Levels.Single(l => l.Code == "L0");
        plan.Devices.Should().Equal(
            new RegisterDeviceRequest("S01", "StereoVision", "Xovis <PC2>", "HttpsPush", "Xovis", "Ntp", new DevicePlacement(arrivals.Id, 20, 16, 5, 0, "Snake A")),
            new RegisterDeviceRequest("S02", "Lidar", "Ouster", "HttpsPush", "Canonical", "Ptp", new DevicePlacement(arrivals.Id, 22, 16, 6, 90, "Snake A", null, null, 8)));
        plan.Counts.Devices.Should().Be(2);
        plan.DevicesNeedingAccessSettings.Should().Equal(["S01"], "only S01 had source networks or a certificate pin");
    }

    [Fact]
    public void Plan_Should_KeepRuleCodesAndListTheRulesWhoseContactWasWithheld_When_RulesAreImported()
    {
        var content = RoundTrip(SiteConfigurationTransfer.Export(Source()).Data);

        var plan = SiteConfigurationTransfer.Plan("DST-COPY", content, Target(), "ZZZ").Data;

        plan.AlertRules.Select(r => (r.Code, r.SiteCode, r.EscalationContact)).Should().Equal(("R-001", "DST-COPY", null), ("R-003", "DST-COPY", null));
        plan.AlertRules[0].Name.Should().Be("Long wait <script>alert(1)</script>", "a hostile name is kept as data");
        plan.AlertRules[0].Should().Match<AlertRule>(r => r.OwnerRole == RoleCodes.BorderShiftSupervisor && r.EscalateAfterMinutes == 15 && r.Id == null);
        plan.RulesNeedingEscalationContact.Should().Equal("R-001");
    }

    [Fact]
    public void Plan_Should_ImportDisplaysUnpairedAtTheTargetSite_When_TheBundleCarriesDisplays()
    {
        var content = RoundTrip(SiteConfigurationTransfer.Export(Source()).Data);

        var display = SiteConfigurationTransfer.Plan("DST-COPY", content, Target(), "ZZZ").Data.Displays.Should().ContainSingle().Subject;

        display.Should().Match<Display>(d => d.Code == "DSP1" && d.SiteCode == "DST-COPY" && d.CredentialPrefix == null && d.CredentialHash == null &&
                                             d.CredentialIssuedOn == null);
        display.Values().Should().BeEquivalentTo(BoardValues(["Snake A"]));
    }

    [Fact]
    public void Plan_Should_RefuseRulesAndDisplays_When_TheirZonesAreNotInTheImportedDraft()
    {
        var content = RoundTrip(SiteConfigurationTransfer.Export(Source()).Data);
        var rule = content.AlertRules[0];
        var display = content.Displays[0];
        AlertRulePartRow Watching(string zone, string metric = "Nowcast") => rule with { Zones = [zone], Metric = metric };

        SiteConfigurationTransfer.Plan("DST", content with { AlertRules = [Watching("Snake B")] }, Target(), "ZZZ").ErrorMessages
            .Should().Equal(SiteConfigurationErrors.AlertRuleZonesNotInProfile);
        SiteConfigurationTransfer.Plan("DST", content with { AlertRules = [Watching("Service A")] }, Target(), "ZZZ").ErrorMessages
            .Should().Equal([SiteConfigurationErrors.AlertRuleZonesNotInProfile], "a service zone is no queue zone or overflow band");
        SiteConfigurationTransfer.Plan("DST", content with { AlertRules = [Watching("Overflow A", "TrackCompletion") with { Comparator = "LessThan" }] }, Target(), "ZZZ")
            .ErrorMessages.Should().Equal([SiteConfigurationErrors.AlertRuleZonesNotInProfile], "a health check watches queue zones only");
        SiteConfigurationTransfer.Plan("DST", content with { AlertRules = [Watching("Overflow A")] }, Target(), "ZZZ").HasErrors
            .Should().BeFalse("a wait rule may watch an overflow band");
        SiteConfigurationTransfer.Plan("DST", content with { AlertRules = [Watching("snake a")] }, Target(), "ZZZ").ErrorMessages
            .Should().Equal([SiteConfigurationErrors.AlertRuleZonesNotInProfile], "zone names match exactly, as the rule service matches them");
        SiteConfigurationTransfer.Plan("DST", content with { Displays = [display with { Entries = [display.Entries[0] with { Zone = "Overflow A" }] }] }, Target(), "ZZZ")
            .ErrorMessages.Should().Equal(SiteConfigurationErrors.DisplayZonesNotInProfile);
        SiteConfigurationTransfer.Plan("DST", content with { ZoneProfiles = [], AlertRules = [] }, Target(), "ZZZ").ErrorMessages
            .Should().Equal([SiteConfigurationErrors.DisplayZonesNotInProfile], "without a profile in the bundle no zone is known");
        SiteConfigurationTransfer.Plan("DST", content with { ZoneProfiles = [], Displays = [] }, Target(), "ZZZ").ErrorMessages
            .Should().Equal(SiteConfigurationErrors.AlertRuleZonesNotInProfile);
    }

    [Fact]
    public void Plan_Should_AnswerThePartsFixedText_When_TheRuleOrDisplayEntityRefusesARow()
    {
        var content = RoundTrip(SiteConfigurationTransfer.Export(Source()).Data);
        var rule = content.AlertRules[0];
        var display = content.Displays[0];

        foreach (var refused in new[]
                 {
                     rule with { OwnerRole = "Root" }, rule with { OwnerRole = RoleCodes.ValidationObserver },
                     rule with { EscalateToRole = "Root" }, rule with { Comparator = "IsTrue" }, rule with { Code = "R-000" },
                     rule with { EscalateAfterMinutes = null, EscalateToRole = RoleCodes.TerminalDutyManager }, rule with { Name = "Bell\u0007" },
                     // ARV-181b2: open desks below recommendation belongs to a border role, escalates to one or none, and fires above.
                     rule with { Metric = "DesksBelowPlan", OwnerRole = RoleCodes.TerminalDutyManager }, rule with { Metric = "DesksBelowPlan", OwnerRole = null },
                     rule with { Metric = "DesksBelowPlan", OwnerRole = RoleCodes.HandlerStationManager },
                     rule with { Metric = "DesksBelowPlan", EscalateToRole = RoleCodes.TerminalDutyManager },
                     rule with { Metric = "DesksBelowPlan", Comparator = "LessThan" }
                 })
            SiteConfigurationTransfer.Plan("DST", content with { AlertRules = [refused] }, Target(), "ZZZ").ErrorMessages
                .Should().Equal([SiteConfigurationErrors.InvalidAlertRules], refused.ToString());

        foreach (var refused in new[]
                 {
                     display with { Languages = ["fr"] }, display with { Languages = ["en"] }, display with { HysteresisMinutes = 5 },
                     display with { Fallback = new Dictionary<string, string> { ["en"] = "Ask" } }, display with { Name = "Bell\u0007" }
                 })
            SiteConfigurationTransfer.Plan("DST", content with { Displays = [refused] }, Target(), "ZZZ").ErrorMessages
                .Should().Equal([SiteConfigurationErrors.InvalidDisplays], refused.Code);
    }

    /// <summary>
    /// ARV-181b2: open desks below recommendation is evaluated, so a bundle may carry a rule on it when the rule belongs to a
    /// border role (the entity's checks) and watches queue zones only (as the health checks).
    /// </summary>
    [Fact]
    public void Plan_Should_ImportOpenDesksBelowRecommendation_When_TheRuleBelongsToABorderRoleOnAQueueZone()
    {
        var content = RoundTrip(SiteConfigurationTransfer.Export(Source()).Data);
        var rule = content.AlertRules[0] with { Metric = "DesksBelowPlan", Comparator = "GreaterOrEqual", Threshold = 2 };

        var plan = SiteConfigurationTransfer.Plan("DST", content with { AlertRules = [rule] }, Target(), "ZZZ");

        plan.HasErrors.Should().BeFalse(string.Join(", ", plan.ErrorMessages ?? []));
        plan.Data.AlertRules.Single().Should().Match<AlertRule>(r => r.Metric == AlertMetric.DesksBelowPlan && r.OwnerRole == RoleCodes.BorderShiftSupervisor);
        SiteConfigurationTransfer.Plan("DST", content with { AlertRules = [rule with { OwnerRole = RoleCodes.SystemAdministrator }] }, Target(), "ZZZ").HasErrors
            .Should().BeFalse("the administrator is a border role");
        SiteConfigurationTransfer.Plan("DST", content with { AlertRules = [rule with { Zones = ["Overflow A"] }] }, Target(), "ZZZ").ErrorMessages
            .Should().Equal([SiteConfigurationErrors.AlertRuleZonesNotInProfile], "the recommendation is per lane, a queue zone");
        SiteConfigurationTransfer.ZonesOf(["Overflow A"], AlertMetric.DesksBelowPlan, new HashSet<string> { "Snake A" }, new HashSet<string> { "Overflow A" })
            .Should().BeFalse();
        SiteConfigurationTransfer.ZonesOf(["Overflow A"], AlertMetric.Nowcast, new HashSet<string> { "Snake A" }, new HashSet<string> { "Overflow A" })
            .Should().BeTrue();
    }

    [Fact]
    public void Plan_Should_ReadEmptyContactsAsNoneAndBlankRolesAsNoRole_When_ARuleCarriesThem()
    {
        var content = RoundTrip(SiteConfigurationTransfer.Export(Source()).Data);
        var rule = content.AlertRules[1] with { OwnerRole = " ", EscalateToRole = "" };

        var plan = SiteConfigurationTransfer.Plan("DST", content with { AlertRules = [rule] }, Target(), "ZZZ").Data;

        plan.AlertRules.Single().Should().Match<AlertRule>(r => r.OwnerRole == null && r.EscalateToRole == null);
        plan.RulesNeedingEscalationContact.Should().BeEmpty();
    }

    #endregion

    #region ARV-162: an export never yields a bundle its import refuses

    public static TheoryData<string, string> NotImportable => new()
    {
        { "a terminal name with a right-to-left override", SiteConfigurationErrors.TopologyNamesNotImportable },
        { "a desk name with a tag character", SiteConfigurationErrors.TopologyNamesNotImportable },
        { "a level name with a zero-width space", SiteConfigurationErrors.TopologyNamesNotImportable },
        { "a checkpoint name with a bell", SiteConfigurationErrors.TopologyNamesNotImportable },
        { "a floor plan file name with a tag character", SiteConfigurationErrors.FloorPlanNamesNotImportable },
        { "a floor plan file name with a path", SiteConfigurationErrors.FloorPlanNamesNotImportable },
        { "a display name with a right-to-left override", SiteConfigurationErrors.TextsNotImportable },
        { "a display location with a zero-width space", SiteConfigurationErrors.TextsNotImportable },
        { "a zone name with a zero-width space", SiteConfigurationErrors.ProfileNotImportable },
        { "a line name with a bell", SiteConfigurationErrors.ProfileNotImportable },
        { "the profile's name with a right-to-left override", SiteConfigurationErrors.ProfileNotImportable },
        { "a zone outline that touches itself", SiteConfigurationErrors.ProfileNotImportable },
        { "a device model with a right-to-left override", SiteConfigurationErrors.TextsNotImportable },
        { "an alert rule name with a tag character", SiteConfigurationErrors.TextsNotImportable },
        { "a display label with a right-to-left override", SiteConfigurationErrors.TextsNotImportable },
        { "a display message with a tag character", SiteConfigurationErrors.TextsNotImportable }
    };

    [Theory]
    [MemberData(nameof(NotImportable))]
    public void Export_Should_RefuseWithTheKindsFixedText_When_AStoredValueWouldBeRefusedByTheImport(string change, string expected)
    {
        // Values saved before ARV-158, ARV-159 or ARV-161, written past the entities as an older database holds them.
        static void Store<T>(T entity, string property, string value) => typeof(T).GetProperty(property)!.SetValue(entity, value);
        var source = Source();
        var profile = source.PublishedProfile;
        switch (change)
        {
            case "a terminal name with a right-to-left override": Store(source.Terminals[0], nameof(Terminal.Name), "Terminal \x202E1"); break;
            case "a desk name with a tag character": Store(source.Desks[0], nameof(Desk.Name), "Desk\U000E0041"); break;
            case "a level name with a zero-width space": Store(source.Levels[0], nameof(Level.Name), "Arr\x200Bivals"); break;
            case "a checkpoint name with a bell": Store(source.Checkpoints[0], nameof(Checkpoint.Name), "Immigration\x0007"); break;
            case "a floor plan file name with a tag character": Store(source.FloorPlans[0], nameof(FloorPlan.OriginalFileName), "arrivals\U000E0041.svg"); break;
            case "a floor plan file name with a path": Store(source.FloorPlans[0], nameof(FloorPlan.OriginalFileName), "../../etc/arrivals.svg"); break;
            case "a display name with a right-to-left override": Store(source.Displays[0], nameof(Display.Name), "Hall\x202E board"); break;
            case "a display location with a zero-width space": Store(source.Displays[0], nameof(Display.Location), "Gate\x200B 3"); break;
            case "a zone name with a zero-width space": Store(profile.Zones.Single(z => z.Name == "Service A"), nameof(Zone.Name), "Service\x200BA"); break;
            case "a line name with a bell": Store(profile.Lines.Single(l => l.Name == "Count hall"), nameof(Line.Name), "Count hall\x0007"); break;
            case "the profile's name with a right-to-left override": Store(profile, nameof(ZoneProfile.Name), "Arr\x202Eivals"); break;
            case "a zone outline that touches itself":
                Store(profile.Zones.Single(z => z.Name == "Overflow A"), nameof(Zone.Polygon), "10.000 24.000,34.000 24.000,34.000 28.000,22.000 24.005,10.000 28.000");
                break;
            case "a device model with a right-to-left override": Store(source.Devices[0], nameof(Device.Model), "Ouster\x202E"); break;
            case "an alert rule name with a tag character": Store(source.AlertRules[0], nameof(AlertRule.Name), "Overflow in use\U000E0041"); break;
            case "a display label with a right-to-left override":
                Store(source.Displays[0], nameof(Display.Entries), "[{\"zone\":\"Snake A\",\"labels\":{\"en\":\"Cit\\u202Ezens\"}}]");
                break;
            case "a display message with a tag character":
                Store(source.Displays[0], nameof(Display.FallbackMessages), "{\"en\":\"Ask staff\\uDB40\\uDC41\"}");
                break;
            default: throw new ArgumentOutOfRangeException(nameof(change));
        }

        SiteConfigurationTransfer.Export(source).ErrorMessages.Should().Equal([expected], change);
        SiteConfigurationErrors.Conflicts.Should().Contain(expected, "the site's state does not allow the export (409)");
    }

    #endregion
}
