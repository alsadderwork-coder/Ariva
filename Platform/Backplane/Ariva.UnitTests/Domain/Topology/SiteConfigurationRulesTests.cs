using System.Globalization;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Services.Topology;
using FluentAssertions;

namespace Ariva.UnitTests.Domain.Topology;

/// <summary>
/// ARV-122a: the request rules of a bundle's parts, run before any entity is touched: the limits the existing endpoints'
/// requests carry, the bounds of each kind at the bound and one beyond, no duplicate the endpoints refuse, every reference
/// resolved through the bundle's own codes and names; each refusal is the part's fixed text.
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

    private static SiteConfigurationContent Content(
        TopologyPart topology = null,
        IReadOnlyList<DeskCodeMappingPartRow> mappings = null,
        IReadOnlyList<ZoneProfilePartRow> profiles = null,
        IReadOnlyList<FloorPlanPartRow> plans = null,
        int? files = null)
    {
        plans ??= [new FloorPlanPartRow("T1", "L0", "arrivals.svg", 0.05, 0, 0)];
        return new SiteConfigurationContent(topology ?? Topology(), mappings ?? [new DeskCodeMappingPartRow("Aman", "IMM-01", "IMM", "D01")],
            profiles ?? [Profile()], plans, Enumerable.Range(1, files ?? plans.Count).ToDictionary(n => n, _ => new byte[] { 1 }));
    }

    private static async Task<IReadOnlyList<string>> Errors(SiteConfigurationContent content) =>
        (await SiteConfigurationRules.Content().ValidateAllAsync(content)).ErrorMessages?.ToList() ?? [];

    [Fact]
    public async Task Content_Should_Pass_When_EveryPartKeepsTheRules()
    {
        (await Errors(Content())).Should().BeEmpty();
        (await Errors(Content(mappings: [], profiles: [], plans: []))).Should().BeEmpty("every part may be an empty list");
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
        { "a scale that is not finite", SiteConfigurationErrors.InvalidFloorPlans }
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
}
