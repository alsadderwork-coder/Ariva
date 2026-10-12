using System.Globalization;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Services.Topology;
using FluentAssertions;

namespace Ariva.UnitTests.Domain.Topology;

/// <summary>
/// ARV-122a: the request rules of a bundle's parts, run before any entity is touched: the limits the existing endpoints'
/// requests carry, the bounds of each kind at the bound and one beyond, no duplicate the endpoints refuse, every reference
/// resolved through the bundle's own codes and names; each refusal is the part's fixed text. ARV-122b: the same for
/// devices, alert rules and displays.
/// </summary>
public sealed class SiteConfigurationRulesTests
{
    private const string Hash = "a32ba7a122c54c0fc9a5e8deabb146b21be26383ed766dab6eb34ce5ca4fb527";

    private static TopologyPart Topology(int terminals = 1) => new(
        [.. Enumerable.Range(1, terminals).Select(n => new TerminalPartRow("AAA", $"T{n}", $"Terminal {n}"))],
        [new LevelPartRow("T1", "L0", "Arrivals", 0, 60, 40), new LevelPartRow("T1", "L1", "Departures", 1, 60, 40)],
        [new CheckpointPartRow("T1", "L0", "IMM", "Immigration", "Immigration"), new CheckpointPartRow("T1", "L1", "CHK", "Check-in", "CheckIn")],
        [new DeskPartRow("IMM", "D01", null, "Desk", ["CIT"], true), new DeskPartRow("CHK", "C01", "Counter 1", "Counter", [], true)]);

    private static ZoneProfilePartRow Profile(int zones = 1, int lines = 2) => new(
        "Arrivals", 1, null, Hash,
        [.. Enumerable.Range(0, zones).Select(n => new ZonePartRow(n == 0 ? "Snake A" : $"Zone {n}", "Queue", "T1", "L0", "10 10,34 10,34 22,10 22", null, null, null, null, null))],
        [.. Enumerable.Range(0, lines).Select(n => new LinePartRow($"Line {n}", "Count", null, "T1", "L0", 1, 1, 1, 9))]);

    private static DevicePartRow Device(string code = "S01") =>
        new(code, "StereoVision", "Xovis PC2", "HttpsPush", "Xovis", "Ntp", null, "T1", "L0", 20, 16, 5, 0, "Snake A", null, null, null, false);

    private static AlertRulePartRow Rule(string code = "R-001") =>
        new(code, "Long wait", ["Snake A"], "Nowcast", "GreaterThan", 20, null, null, 5, 5, "Warning", "BorderShiftSupervisor", 15, null, true, false, true, null);

    private static DisplayPartRow Board(string code = "DSP1") =>
        new(code, "Hall board", "Gate 3", "Landscape", ["en", "ar"], 5, 1, 300,
            [new DisplayEntryPartRow("Snake A", new Dictionary<string, string> { ["en"] = "Citizens", ["ar"] = "Citizens" })],
            new Dictionary<string, string> { ["en"] = "Ask staff", ["ar"] = "Ask staff" }, true);

    private static SiteConfigurationContent Content(
        TopologyPart topology = null,
        IReadOnlyList<DeskCodeMappingPartRow> mappings = null,
        IReadOnlyList<ZoneProfilePartRow> profiles = null,
        IReadOnlyList<FloorPlanPartRow> plans = null,
        int? files = null,
        IReadOnlyList<DevicePartRow> devices = null,
        IReadOnlyList<AlertRulePartRow> rules = null,
        IReadOnlyList<DisplayPartRow> displays = null)
    {
        plans ??= [new FloorPlanPartRow("T1", "L0", "arrivals.svg", 0.05, 0, 0)];
        return new SiteConfigurationContent(topology ?? Topology(), mappings ?? [new DeskCodeMappingPartRow("Aman", "IMM-01", "IMM", "D01")],
            profiles ?? [Profile()], plans, Enumerable.Range(1, files ?? plans.Count).ToDictionary(n => n, _ => new byte[] { 1 }),
            devices ?? [Device()], rules ?? [Rule()], displays ?? [Board()]);
    }

    private static async Task<IReadOnlyList<string>> Errors(SiteConfigurationContent content) =>
        (await SiteConfigurationRules.Content().ValidateAllAsync(content)).ErrorMessages?.ToList() ?? [];

    [Fact]
    public async Task Content_Should_Pass_When_EveryPartKeepsTheRules()
    {
        (await Errors(Content())).Should().BeEmpty();
        (await Errors(Content(mappings: [], profiles: [], plans: [], devices: [], rules: [], displays: []))).Should().BeEmpty("every part may be an empty list");
    }

    public static TheoryData<string, string> Broken => new()
    {
        { "a lower-case terminal code", SiteConfigurationErrors.InvalidTopology },
        { "an airport that is not an IATA code", SiteConfigurationErrors.InvalidTopology },
        { "a name of 201 characters", SiteConfigurationErrors.InvalidTopology },
        { "a name with a NUL", SiteConfigurationErrors.InvalidTopology },
        { "a blank name", SiteConfigurationErrors.InvalidTopology },
        { "a level of an unknown terminal", SiteConfigurationErrors.InvalidTopology },
        { "a level code twice in a terminal", SiteConfigurationErrors.InvalidTopology },
        { "a floor twice in a terminal", SiteConfigurationErrors.InvalidTopology },
        { "a floor out of range", SiteConfigurationErrors.InvalidTopology },
        { "an extent beyond 2000 m", SiteConfigurationErrors.InvalidTopology },
        { "a checkpoint kind by number", SiteConfigurationErrors.InvalidTopology },
        { "a checkpoint code twice in the site", SiteConfigurationErrors.InvalidTopology },
        { "a desk of an unknown checkpoint", SiteConfigurationErrors.InvalidTopology },
        { "nine lane categories", SiteConfigurationErrors.InvalidTopology },
        { "no topology lists", SiteConfigurationErrors.InvalidTopology },
        { "a mapping system in lower case", SiteConfigurationErrors.InvalidDeskCodeMappings },
        { "an external code of 33 characters", SiteConfigurationErrors.InvalidDeskCodeMappings },
        { "a code twice for a system", SiteConfigurationErrors.InvalidDeskCodeMappings },
        { "a desk twice for a system", SiteConfigurationErrors.InvalidDeskCodeMappings },
        { "a mapping of an unknown desk", SiteConfigurationErrors.InvalidDeskCodeMappings },
        { "two profiles", SiteConfigurationErrors.InvalidZoneProfile },
        { "no portable hash", SiteConfigurationErrors.InvalidZoneProfile },
        { "a zone of an unknown level", SiteConfigurationErrors.InvalidZoneProfile },
        { "a polygon that does not parse", SiteConfigurationErrors.InvalidZoneProfile },
        { "a polygon longer than a zone holds", SiteConfigurationErrors.InvalidZoneProfile },
        { "a queue zone that is not in the profile", SiteConfigurationErrors.InvalidZoneProfile },
        { "a zone name twice ignoring case", SiteConfigurationErrors.InvalidZoneProfile },
        { "a zone's desk that is not in the topology", SiteConfigurationErrors.InvalidZoneProfile },
        { "a capacity of 5001", SiteConfigurationErrors.InvalidZoneProfile },
        { "a line role by number", SiteConfigurationErrors.InvalidZoneProfile },
        { "a line end that is not finite", SiteConfigurationErrors.InvalidZoneProfile },
        { "a line name twice", SiteConfigurationErrors.InvalidZoneProfile },
        { "more plans than files", SiteConfigurationErrors.InvalidFloorPlans },
        { "two plans for a level", SiteConfigurationErrors.InvalidFloorPlans },
        { "a plan of an unknown level", SiteConfigurationErrors.InvalidFloorPlans },
        { "a file name of 201 characters", SiteConfigurationErrors.InvalidFloorPlans },
        { "a file name of 201 characters outside the basic plane", SiteConfigurationErrors.InvalidFloorPlans },
        { "a scale that is not finite", SiteConfigurationErrors.InvalidFloorPlans },
        { "no devices list", SiteConfigurationErrors.InvalidDevices },
        { "a device code in lower case", SiteConfigurationErrors.InvalidDevices },
        { "a device code twice", SiteConfigurationErrors.InvalidDevices },
        { "a device family in lower case", SiteConfigurationErrors.InvalidDevices },
        { "a transport by number", SiteConfigurationErrors.InvalidDevices },
        { "a dialect that does not exist", SiteConfigurationErrors.InvalidDevices },
        { "a clock source as a combination", SiteConfigurationErrors.InvalidDevices },
        { "a model of 101 characters", SiteConfigurationErrors.InvalidDevices },
        { "a mapping name of 65 characters", SiteConfigurationErrors.InvalidDevices },
        { "a device on an unknown level", SiteConfigurationErrors.InvalidDevices },
        { "a device position that is not finite", SiteConfigurationErrors.InvalidDevices },
        { "a device position beyond 2001 m", SiteConfigurationErrors.InvalidDevices },
        { "a mounting height beyond 100 m", SiteConfigurationErrors.InvalidDevices },
        { "an orientation beyond 360 degrees", SiteConfigurationErrors.InvalidDevices },
        { "a device without a queue zone", SiteConfigurationErrors.InvalidDevices },
        { "a footprint beyond 1000 m", SiteConfigurationErrors.InvalidDevices },
        { "a device model with a NUL", SiteConfigurationErrors.InvalidDevices },
        { "no alert rules list", SiteConfigurationErrors.InvalidAlertRules },
        { "a rule code not of the R-nnn form", SiteConfigurationErrors.InvalidAlertRules },
        { "a rule code with a leading zero too many", SiteConfigurationErrors.InvalidAlertRules },
        { "a rule code of number 0", SiteConfigurationErrors.InvalidAlertRules },
        { "a rule code beyond the last number", SiteConfigurationErrors.InvalidAlertRules },
        { "a rule code twice", SiteConfigurationErrors.InvalidAlertRules },
        { "a rule name of 201 characters", SiteConfigurationErrors.InvalidAlertRules },
        { "a rule without zones", SiteConfigurationErrors.InvalidAlertRules },
        { "a rule with 65 zones", SiteConfigurationErrors.InvalidAlertRules },
        { "a rule zone with a NUL", SiteConfigurationErrors.InvalidAlertRules },
        { "a metric in lower case", SiteConfigurationErrors.InvalidAlertRules },
        { "a threshold that is not finite", SiteConfigurationErrors.InvalidAlertRules },
        { "sustain of 121 minutes", SiteConfigurationErrors.InvalidAlertRules },
        { "clear of 0 minutes", SiteConfigurationErrors.InvalidAlertRules },
        { "escalation after 1441 minutes", SiteConfigurationErrors.InvalidAlertRules },
        { "a lead of 61 minutes", SiteConfigurationErrors.InvalidAlertRules },
        { "an owner role of 33 characters", SiteConfigurationErrors.InvalidAlertRules },
        { "no displays list", SiteConfigurationErrors.InvalidDisplays },
        { "a display code in lower case", SiteConfigurationErrors.InvalidDisplays },
        { "a display code twice", SiteConfigurationErrors.InvalidDisplays },
        { "a display name of 201 characters", SiteConfigurationErrors.InvalidDisplays },
        { "an orientation in lower case", SiteConfigurationErrors.InvalidDisplays },
        { "five languages", SiteConfigurationErrors.InvalidDisplays },
        { "a band of 31 minutes", SiteConfigurationErrors.InvalidDisplays },
        { "a hysteresis that is not finite", SiteConfigurationErrors.InvalidDisplays },
        { "stale after 59 seconds", SiteConfigurationErrors.InvalidDisplays },
        { "thirteen entries", SiteConfigurationErrors.InvalidDisplays },
        { "a label of 81 characters", SiteConfigurationErrors.InvalidDisplays },
        { "a fallback of 201 characters", SiteConfigurationErrors.InvalidDisplays },
        { "no fallback", SiteConfigurationErrors.InvalidDisplays }
    };

    [Theory]
    [MemberData(nameof(Broken))]
    public async Task Content_Should_RefuseWithThePartsFixedText_When_ARowBreaksARule(string change, string expected)
    {
        var topology = Topology();
        var profile = Profile();
        var content = change switch
        {
            "a lower-case terminal code" => Content(topology with { Terminals = [new TerminalPartRow("AAA", "t1", "T")] }),
            "an airport that is not an IATA code" => Content(topology with { Terminals = [new TerminalPartRow("<script>", "T1", "T")] }),
            "a name of 201 characters" => Content(topology with { Terminals = [new TerminalPartRow("AAA", "T1", new string('n', 201))] }),
            "a name with a NUL" => Content(topology with { Terminals = [new TerminalPartRow("AAA", "T1", "T\0")] }),
            "a blank name" => Content(topology with { Terminals = [new TerminalPartRow("AAA", "T1", "  ")] }),
            "a level of an unknown terminal" => Content(topology with { Levels = [.. topology.Levels, new LevelPartRow("T9", "L2", "L", 2, 10, 10)] }),
            "a level code twice in a terminal" => Content(topology with { Levels = [.. topology.Levels, new LevelPartRow("T1", "L0", "L", 5, 10, 10)] }),
            "a floor twice in a terminal" => Content(topology with { Levels = [.. topology.Levels, new LevelPartRow("T1", "L5", "L", 0, 10, 10)] }),
            "a floor out of range" => Content(topology with { Levels = [.. topology.Levels, new LevelPartRow("T1", "L5", "L", 51, 10, 10)] }),
            "an extent beyond 2000 m" => Content(topology with { Levels = [.. topology.Levels, new LevelPartRow("T1", "L5", "L", 5, 2000.5, 10)] }),
            "a checkpoint kind by number" => Content(topology with { Checkpoints = [.. topology.Checkpoints, new CheckpointPartRow("T1", "L0", "SEC", "S", "1")] }),
            "a checkpoint code twice in the site" => Content(topology with { Checkpoints = [.. topology.Checkpoints, new CheckpointPartRow("T1", "L1", "IMM", "S", "Security")] }),
            "a desk of an unknown checkpoint" => Content(topology with { Desks = [.. topology.Desks, new DeskPartRow("NONE", "D02", null, "Desk", ["CIT"], true)] }),
            "nine lane categories" => Content(topology with { Desks = [new DeskPartRow("IMM", "D01", null, "Desk", ["AA", "BB", "CC", "DD", "EE", "FF", "GG", "HH", "II"], true)] }),
            "no topology lists" => Content(topology with { Desks = null }),
            "a mapping system in lower case" => Content(mappings: [new DeskCodeMappingPartRow("aman", "IMM-01", "IMM", "D01")]),
            "an external code of 33 characters" => Content(mappings: [new DeskCodeMappingPartRow("Aman", new string('X', 33), "IMM", "D01")]),
            "a code twice for a system" => Content(topology with { Desks = [.. topology.Desks, new DeskPartRow("IMM", "D02", null, "Desk", ["CIT"], true)] },
                [new DeskCodeMappingPartRow("Aman", "X1", "IMM", "D01"), new DeskCodeMappingPartRow("Aman", "x1", "IMM", "D02")]),
            "a desk twice for a system" => Content(mappings: [new DeskCodeMappingPartRow("Aman", "X1", "IMM", "D01"), new DeskCodeMappingPartRow("Aman", "X2", "IMM", "D01")]),
            "a mapping of an unknown desk" => Content(mappings: [new DeskCodeMappingPartRow("Aman", "X1", "IMM", "D99")]),
            "two profiles" => Content(profiles: [profile, profile]),
            "no portable hash" => Content(profiles: [profile with { PortableGeometryHash = null }]),
            "a zone of an unknown level" => Content(profiles: [profile with { Zones = [profile.Zones[0] with { Level = "L9" }] }]),
            "a polygon that does not parse" => Content(profiles: [profile with { Zones = [profile.Zones[0] with { Polygon = "10 10,<script>" }] }]),
            "a polygon longer than a zone holds" => Content(profiles: [profile with { Zones = [profile.Zones[0] with { Polygon = new string(' ', 4001) + "1 1,2 2,3 1" }] }]),
            "a queue zone that is not in the profile" => Content(profiles: [profile with { Zones = [.. profile.Zones, profile.Zones[0] with { Name = "Band", Kind = "Overflow", QueueZone = "Nowhere" }] }]),
            "a zone name twice ignoring case" => Content(profiles: [profile with { Zones = [.. profile.Zones, profile.Zones[0] with { Name = "SNAKE A" }] }]),
            "a zone's desk that is not in the topology" => Content(profiles: [profile with { Zones = [profile.Zones[0] with { Checkpoint = "IMM", Desk = "D99" }] }]),
            "a capacity of 5001" => Content(profiles: [profile with { Zones = [profile.Zones[0] with { PhysicalCapacity = 5001 }] }]),
            "a line role by number" => Content(profiles: [profile with { Lines = [profile.Lines[0] with { Role = "2" }] }]),
            "a line end that is not finite" => Content(profiles: [profile with { Lines = [profile.Lines[0] with { EndX = double.NaN }] }]),
            "a line name twice" => Content(profiles: [profile with { Lines = [profile.Lines[0], profile.Lines[0] with { StartY = 2 }] }]),
            "more plans than files" => Content(files: 0),
            "two plans for a level" => Content(plans: [new FloorPlanPartRow("T1", "L0", "a.svg", 0.05, 0, 0), new FloorPlanPartRow("T1", "L0", "b.svg", 0.05, 0, 0)]),
            "a plan of an unknown level" => Content(plans: [new FloorPlanPartRow("T1", "L9", "a.svg", 0.05, 0, 0)]),
            "a file name of 201 characters" => Content(plans: [new FloorPlanPartRow("T1", "L0", new string('f', 201), 0.05, 0, 0)]),
            "a file name of 201 characters outside the basic plane" => Content(plans: [new FloorPlanPartRow("T1", "L0", string.Concat(Enumerable.Repeat("\U0001F6EB", 201)), 0.05, 0, 0)]),
            "a scale that is not finite" => Content(plans: [new FloorPlanPartRow("T1", "L0", "a.svg", double.PositiveInfinity, 0, 0)]),
            "no devices list" => Content() with { Devices = null },
            "a device code in lower case" => Content(devices: [Device("s01")]),
            "a device code twice" => Content(devices: [Device(), Device() with { X = 21 }]),
            "a device family in lower case" => Content(devices: [Device() with { Family = "stereovision" }]),
            "a transport by number" => Content(devices: [Device() with { Transport = "0" }]),
            "a dialect that does not exist" => Content(devices: [Device() with { Dialect = "<script>" }]),
            "a clock source as a combination" => Content(devices: [Device() with { ClockSource = "Ntp, Ptp" }]),
            "a model of 101 characters" => Content(devices: [Device() with { Model = new string('m', 101) }]),
            "a mapping name of 65 characters" => Content(devices: [Device() with { Dialect = "Declarative", MappingName = new string('m', 65) }]),
            "a device on an unknown level" => Content(devices: [Device() with { Level = "L9" }]),
            "a device position that is not finite" => Content(devices: [Device() with { X = double.NaN }]),
            "a device position beyond 2001 m" => Content(devices: [Device() with { Y = 2001.5 }]),
            "a mounting height beyond 100 m" => Content(devices: [Device() with { MountingHeightMetres = 100.5 }]),
            "an orientation beyond 360 degrees" => Content(devices: [Device() with { OrientationDegrees = 360.5 }]),
            "a device without a queue zone" => Content(devices: [Device() with { QueueZone = " " }]),
            "a footprint beyond 1000 m" => Content(devices: [Device() with { FootprintRadiusMetres = 1000.5 }]),
            "a device model with a NUL" => Content(devices: [Device() with { Model = "Xovis\0" }]),
            "no alert rules list" => Content() with { AlertRules = null },
            "a rule code not of the R-nnn form" => Content(rules: [Rule("RULE-1")]),
            "a rule code with a leading zero too many" => Content(rules: [Rule("R-0001")]),
            "a rule code of number 0" => Content(rules: [Rule("R-000")]),
            "a rule code beyond the last number" => Content(rules: [Rule("R-1000000")]),
            "a rule code twice" => Content(rules: [Rule(), Rule() with { Name = "Other" }]),
            "a rule name of 201 characters" => Content(rules: [Rule() with { Name = new string('n', 201) }]),
            "a rule without zones" => Content(rules: [Rule() with { Zones = [] }]),
            "a rule with 65 zones" => Content(rules: [Rule() with { Zones = [.. Enumerable.Range(1, 65).Select(n => $"Z{n}")] }]),
            "a rule zone with a NUL" => Content(rules: [Rule() with { Zones = ["Snake\0A"] }]),
            "a metric in lower case" => Content(rules: [Rule() with { Metric = "nowcast" }]),
            "a threshold that is not finite" => Content(rules: [Rule() with { Threshold = double.PositiveInfinity }]),
            "sustain of 121 minutes" => Content(rules: [Rule() with { SustainMinutes = 121 }]),
            "clear of 0 minutes" => Content(rules: [Rule() with { ClearAfterMinutes = 0 }]),
            "escalation after 1441 minutes" => Content(rules: [Rule() with { EscalateAfterMinutes = 1441 }]),
            "a lead of 61 minutes" => Content(rules: [Rule() with { Metric = "PredictedNowcast", LeadMinutes = 61 }]),
            "an owner role of 33 characters" => Content(rules: [Rule() with { OwnerRole = new string('r', 33) }]),
            "no displays list" => Content() with { Displays = null },
            "a display code in lower case" => Content(displays: [Board("dsp1")]),
            "a display code twice" => Content(displays: [Board(), Board() with { Name = "Other" }]),
            "a display name of 201 characters" => Content(displays: [Board() with { Name = new string('n', 201) }]),
            "an orientation in lower case" => Content(displays: [Board() with { Orientation = "landscape" }]),
            "five languages" => Content(displays: [Board() with { Languages = ["en", "ar", "pt", "sw", "fr"] }]),
            "a band of 31 minutes" => Content(displays: [Board() with { BandMinutes = 31 }]),
            "a hysteresis that is not finite" => Content(displays: [Board() with { HysteresisMinutes = double.NaN }]),
            "stale after 59 seconds" => Content(displays: [Board() with { StaleSeconds = 59 }]),
            "thirteen entries" => Content(displays: [Board() with { Entries = [.. Enumerable.Range(1, 13).Select(n => Board().Entries[0] with { Zone = $"Z{n}" })] }]),
            "a label of 81 characters" => Content(displays: [Board() with { Entries = [new DisplayEntryPartRow("Snake A", new Dictionary<string, string> { ["en"] = new string('l', 81) })] }]),
            "a fallback of 201 characters" => Content(displays: [Board() with { Fallback = new Dictionary<string, string> { ["en"] = new string('f', 201) } }]),
            "no fallback" => Content(displays: [Board() with { Fallback = null }]),
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };

        (await Errors(content)).Should().Equal([expected], change);
    }

    [Fact]
    public async Task Content_Should_ReportOnlyTheTopology_When_TheTopologyItselfIsBroken()
    {
        // The parts that refer to the topology are judged only once it is valid, so the answer names the first real problem.
        (await Errors(Content(Topology() with { Levels = null }, mappings: [new DeskCodeMappingPartRow("aman", "", "X", "Y")])))
            .Should().Equal(SiteConfigurationErrors.InvalidTopology);
    }

    [Fact]
    public async Task Content_Should_TakeEachKindAtItsBoundAndRefuseOneMore_When_Counted()
    {
        (await Errors(Content(Topology(SiteConfigurationLimits.MaxTerminals)))).Should().BeEmpty("50 terminals are the bound");
        (await Errors(Content(Topology(SiteConfigurationLimits.MaxTerminals + 1)))).Should().Equal(SiteConfigurationErrors.InvalidTopology);

        var desks = Enumerable.Range(1, SiteConfigurationLimits.MaxDesks).Select(n => new DeskPartRow("CHK", string.Create(CultureInfo.InvariantCulture, $"C{n}"), null, "Counter", [], true)).ToList();
        (await Errors(Content(Topology() with { Desks = desks }, mappings: []))).Should().BeEmpty("5,000 desks are the bound");
        (await Errors(Content(Topology() with { Desks = [.. desks, new DeskPartRow("CHK", "C0", null, "Counter", [], true)] }, mappings: [])))
            .Should().Equal(SiteConfigurationErrors.InvalidTopology);

        (await Errors(Content(profiles: [Profile(SiteConfigurationLimits.MaxZones)]))).Should().BeEmpty("a profile holds 500 zones (ZoneProfile.MaxZones)");
        (await Errors(Content(profiles: [Profile(SiteConfigurationLimits.MaxZones + 1)]))).Should().Equal(SiteConfigurationErrors.InvalidZoneProfile);
        (await Errors(Content(profiles: [Profile(lines: SiteConfigurationLimits.MaxLines)]))).Should().BeEmpty("and 1,000 lines");
        (await Errors(Content(profiles: [Profile(lines: SiteConfigurationLimits.MaxLines + 1)]))).Should().Equal(SiteConfigurationErrors.InvalidZoneProfile);

        (await Errors(Content(Topology() with { Terminals = [new TerminalPartRow("AAA", "T1", new string('n', SiteConfigurationLimits.MaxNameLength))] })))
            .Should().BeEmpty("a name of 200 characters is the bound");
        (await Errors(Content(mappings: [new DeskCodeMappingPartRow("Aman", new string('X', 32), "IMM", "D01")]))).Should().BeEmpty("an external code of 32 characters");
        (await Errors(Content(plans: [new FloorPlanPartRow("T1", "L0", new string('f', SiteConfigurationLimits.MaxFileNameLength), 0.05, 0, 0)])))
            .Should().BeEmpty("a file name of 200 characters");
        (await Errors(Content(plans: [new FloorPlanPartRow("T1", "L0", string.Concat(Enumerable.Repeat("\U0001F6EB", SiteConfigurationLimits.MaxFileNameLength)), 0.05, 0, 0)])))
            .Should().BeEmpty("200 characters outside the basic plane, as PostgreSQL counts them");
    }

    [Fact]
    public async Task Content_Should_TakeDevicesRulesAndDisplaysAtTheirBoundsAndRefuseOneMore_When_Counted()
    {
        string Code(string prefix, int n) => string.Create(CultureInfo.InvariantCulture, $"{prefix}{n}");
        var devices = Enumerable.Range(1, SiteConfigurationLimits.MaxDevices + 1).Select(n => Device(Code("S", n))).ToList();
        var rules = Enumerable.Range(1, SiteConfigurationLimits.MaxAlertRules + 1).Select(n => Rule(Ariva.Core.Domain.Entities.AlertRule.CodeOf(n))).ToList();
        var displays = Enumerable.Range(1, SiteConfigurationLimits.MaxDisplays + 1).Select(n => Board(Code("B", n))).ToList();

        (await Errors(Content(devices: devices[..^1]))).Should().BeEmpty("2,000 devices are the bound");
        (await Errors(Content(devices: devices))).Should().Equal(SiteConfigurationErrors.InvalidDevices);
        (await Errors(Content(rules: rules[..^1]))).Should().BeEmpty("500 alert rules are the bound");
        (await Errors(Content(rules: rules))).Should().Equal(SiteConfigurationErrors.InvalidAlertRules);
        (await Errors(Content(displays: displays[..^1]))).Should().BeEmpty("200 displays are the bound");
        (await Errors(Content(displays: displays))).Should().Equal(SiteConfigurationErrors.InvalidDisplays);

        (await Errors(Content(rules: [Rule("R-100000"), Rule("R-1000")]))).Should().BeEmpty("an import takes codes up to R-100000; past R-999 they grow");
        (await Errors(Content(rules: [Rule("R-100001")]))).Should().Equal([SiteConfigurationErrors.RuleCodeTooHigh], "one beyond the import's bound");
        (await Errors(Content(rules: [Rule("R-999999"), Rule("R-002")]))).Should().Equal([SiteConfigurationErrors.RuleCodeTooHigh], "a rule code the entity takes, but not an import");
        (await Errors(Content(rules: [Rule("R-100001"), Rule("RULE-1")]))).Should().Equal([SiteConfigurationErrors.InvalidAlertRules], "a malformed part answers its own text only");
        (await Errors(Content(rules: [Rule() with { Name = new string('n', 200), SustainMinutes = 120, ClearAfterMinutes = 1, EscalateAfterMinutes = 1440,
            Zones = [.. Enumerable.Range(1, 64).Select(n => Code("Z", n))] }]))).Should().BeEmpty("the rule request's limits");
        (await Errors(Content(devices: [Device() with { Model = new string('m', 100), X = 2001, Y = -1, MountingHeightMetres = 100, OrientationDegrees = 360,
            FootprintLengthMetres = 1000, FootprintWidthMetres = 0 }]))).Should().BeEmpty("the registration request's limits; the registration then applies its own");
        (await Errors(Content(displays: [Board() with { BandMinutes = 30, StaleSeconds = 1800, Name = new string('n', 200),
            Entries = [.. Enumerable.Range(1, 12).Select(n => Board().Entries[0] with { Zone = Code("Z", n) })] }]))).Should().BeEmpty("the display request's limits");
    }

    [Fact]
    public async Task Content_Should_JudgeDevicesOnlyAgainstAValidTopology_When_TheTopologyIsBroken()
    {
        (await Errors(Content(Topology() with { Levels = null }, devices: [Device() with { Level = "L9" }])))
            .Should().Equal(SiteConfigurationErrors.InvalidTopology);
    }

    [Theory]
    [InlineData("R-001", 1)]
    [InlineData("R-999", 999)]
    [InlineData("R-1000", 1000)]
    [InlineData("R-999999", 999_999)]
    [InlineData("R-000", null)]
    [InlineData("R-01", null)]
    [InlineData("R-0001", null)]
    [InlineData("R-1000000", null)]
    [InlineData("R- 12", null)]
    [InlineData("R-+12", null)]
    [InlineData("R-\u0661\u0662\u0663", null)]
    [InlineData("r-001", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void RuleNumber_Should_ReadOnlyTheCanonicalCode_When_Parsed(string code, int? number) =>
        SiteConfigurationRules.RuleNumber(code).Should().Be(number);

    [Theory]
    [InlineData("Immigration", true)]
    [InlineData("CheckIn", true)]
    [InlineData("immigration", false)]
    [InlineData("1", false)]
    [InlineData("Immigration, CheckIn", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsKind_Should_TakeKindsByTheirNamesOnly_When_Parsed(string value, bool valid) =>
        SiteConfigurationRules.IsKind<Ariva.Core.Domain.Enums.CheckpointKind>(value).Should().Be(valid);

    #region ARV-159: names judged per code point

    public static TheoryData<string, string> UnsafeNames => new()
    {
        { "a terminal name with a right-to-left override", SiteConfigurationErrors.InvalidTopology },
        { "a level name with a tag character", SiteConfigurationErrors.InvalidTopology },
        { "a checkpoint name with a zero-width space", SiteConfigurationErrors.InvalidTopology },
        { "a desk name with a bell", SiteConfigurationErrors.InvalidTopology },
        { "a profile name with a right-to-left override", SiteConfigurationErrors.InvalidZoneProfile },
        { "a zone name with a tag character", SiteConfigurationErrors.InvalidZoneProfile },
        { "a line name with a zero-width space", SiteConfigurationErrors.InvalidZoneProfile }
    };

    [Theory]
    [MemberData(nameof(UnsafeNames))]
    public async Task Content_Should_RefuseANameWithAnInvisibleOrControlCharacter_When_AnyPartCarriesIt(string change, string expected)
    {
        // ARV-159: the import takes the names the topology and zone profile endpoints take, and no other (escapes only).
        const string Override = "\x202E";
        const string Tag = "\U000E0041";
        var topology = Topology();
        var profile = Profile();
        var content = change switch
        {
            "a terminal name with a right-to-left override" => Content(topology with { Terminals = [new TerminalPartRow("AAA", "T1", "Terminal" + Override + "1")] }),
            "a level name with a tag character" => Content(topology with { Levels = [topology.Levels[0] with { Name = "Arrivals" + Tag }, topology.Levels[1]] }),
            "a checkpoint name with a zero-width space" => Content(topology with { Checkpoints = [topology.Checkpoints[0] with { Name = "Immi\x200Bgration" }, topology.Checkpoints[1]] }),
            "a desk name with a bell" => Content(topology with { Desks = [topology.Desks[0], topology.Desks[1] with { Name = "Counter\x0007" }] }),
            "a profile name with a right-to-left override" => Content(profiles: [profile with { Name = "Arrivals" + Override }]),
            "a zone name with a tag character" => Content(profiles: [profile with { Zones = [.. profile.Zones, profile.Zones[0] with { Name = "Band" + Tag, Kind = "Overflow", QueueZone = "Snake A" }] }]),
            "a line name with a zero-width space" => Content(profiles: [profile with { Lines = [profile.Lines[0] with { Name = "Line\x200B 0" }, profile.Lines[1]] }]),
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };

        (await Errors(content)).Should().Equal([expected], change);
    }

    [Fact]
    public async Task Content_Should_TakeNamesInArabic_When_EveryPartCarriesThem()
    {
        // "Arrivals hall", "Immigration", "Counter" and "Entry line" in Arabic; the zone keeps its name, which other parts reference.
        const string Hall = "\x0635\x0627\x0644\x0629 \x0627\x0644\x0648\x0635\x0648\x0644";
        const string Immigration = "\x0627\x0644\x062C\x0648\x0627\x0632\x0627\x062A";
        const string Counter = "\x0645\x0646\x0636\x062F \x0661";
        const string Entry = "\x062E\x0637 \x0627\x0644\x062F\x062E\x0648\x0644";
        var topology = Topology();
        var profile = Profile();
        var content = Content(
            topology with
            {
                Terminals = [new TerminalPartRow("AAA", "T1", Hall)],
                Levels = [topology.Levels[0] with { Name = Hall }, topology.Levels[1]],
                Checkpoints = [topology.Checkpoints[0] with { Name = Immigration }, topology.Checkpoints[1]],
                Desks = [topology.Desks[0], topology.Desks[1] with { Name = Counter }]
            },
            profiles: [profile with { Name = Hall, Lines = [profile.Lines[0] with { Name = Entry }, profile.Lines[1]] }]);

        (await Errors(content)).Should().BeEmpty();
        SiteConfigurationRules.IsName(Hall).Should().BeTrue();
        SiteConfigurationRules.IsName("Hall" + "\x202E").Should().BeFalse();
    }

    #endregion
}
