using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using FluentAssertions;

namespace Ariva.UnitTests.Domain.Topology;

/// <summary>
/// ARV-013: the topology invariants. Codes have one shape and are unique among the live children of a parent; levels
/// have a bounded extent and unique floor numbers; checkpoint kinds decide the desk kinds; lane categories follow the
/// desk kind; the site code flows down from the terminal.
/// </summary>
public sealed class TopologyTests
{
    private static readonly DateTime Now = new(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc);

    private static Airport Airport() => new("DMO", "ODMO", "Demo International", "Asia/Amman");

    private static Checkpoint Immigration() =>
        Airport().AddTerminal("T1", "Terminal 1", "DMO-T1").AddLevel("L0", "Arrivals", 0, 300, 120).AddCheckpoint("IMM", "Immigration", CheckpointKind.Immigration);

    #region Codes

    [Theory]
    [InlineData("T1", true)]
    [InlineData("D01", true)]
    [InlineData("EG-03", true)]
    [InlineData("A-B-C", true)]
    [InlineData("ABCDEFGHIJKLMNOP", true)]
    [InlineData("ABCDEFGHIJKLMNOPQ", false)]
    [InlineData("t1", false)]
    [InlineData("-T1", false)]
    [InlineData("T1-", false)]
    [InlineData("T--1", false)]
    [InlineData("T 1", false)]
    [InlineData("T1<script>", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValid_Should_AcceptOnlyUpperCaseCodes_When_Checked(string code, bool valid)
    {
        TopologyCodes.IsValid(code).Should().Be(valid);
    }

    [Theory]
    [InlineData("DMO", null, "Asia/Amman")]
    [InlineData("AUH", "OMAA", "Asia/Dubai")]
    public void Airport_Should_BeCreated_When_CodesAndTimeZoneAreValid(string iata, string icao, string zone)
    {
        var airport = new Airport(iata, icao, " Name ", zone);

        airport.IataCode.Should().Be(iata);
        airport.Name.Should().Be("Name");
    }

    [Theory]
    [InlineData("DM", null, "Asia/Amman")]
    [InlineData("dmo", null, "Asia/Amman")]
    [InlineData("DMO", "ODM", "Asia/Amman")]
    [InlineData("DMO", null, "Mars/Olympus")]
    [InlineData("DMO", null, "")]
    public void Airport_Should_Refuse_When_ACodeOrTheTimeZoneIsInvalid(string iata, string icao, string zone)
    {
        var create = () => new Airport(iata, icao, "Name", zone);

        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Names_Should_BeRequiredAndBounded_When_Set()
    {
        var blank = () => new Airport("DMO", null, "  ", "Asia/Amman");
        var tooLong = () => new Airport("DMO", null, new string('n', 201), "Asia/Amman");

        blank.Should().Throw<ArgumentException>();
        tooLong.Should().Throw<ArgumentException>();
    }

    #endregion

    #region Uniqueness and site

    [Fact]
    public void AddTerminal_Should_RefuseADuplicateCodeAndCarryTheSite_When_Added()
    {
        var airport = Airport();
        var terminal = airport.AddTerminal("T1", "Terminal 1", "DMO-T1");

        var duplicate = () => airport.AddTerminal("T1", "Again", "DMO-T1");
        var badSite = () => airport.AddTerminal("T2", "Terminal 2", "dmo");

        duplicate.Should().Throw<InvalidOperationException>();
        badSite.Should().Throw<ArgumentException>();
        terminal.SiteCode.Should().Be("DMO-T1");
        terminal.Airport.Should().BeSameAs(airport);
    }

    [Fact]
    public void AddTerminal_Should_AllowACodeAgain_When_TheEarlierOneIsDeleted()
    {
        var airport = Airport();
        airport.AddTerminal("T1", "Terminal 1", "DMO-T1").SoftDelete("admin", Now);

        var again = airport.AddTerminal("T1", "Terminal 1 rebuilt", "DMO-T1");

        again.Name.Should().Be("Terminal 1 rebuilt");
    }

    [Fact]
    public void Children_Should_InheritTheTerminalsSite_When_Created()
    {
        var desk = Immigration().AddDesk("D01", null, DeskKind.Desk, ["CIT"]);

        desk.SiteCode.Should().Be("DMO-T1");
        desk.Checkpoint.SiteCode.Should().Be("DMO-T1");
        desk.Checkpoint.Level.SiteCode.Should().Be("DMO-T1");
        desk.Name.Should().Be("D01", "a desk without a name shows its code");
    }

    [Fact]
    public void AddLevel_Should_RefuseDuplicateCodesAndFloors_When_Added()
    {
        var terminal = Airport().AddTerminal("T1", "Terminal 1", "DMO-T1");
        terminal.AddLevel("L0", "Ground", 0, 100, 100);

        var code = () => terminal.AddLevel("L0", "Again", 1, 100, 100);
        var floor = () => terminal.AddLevel("L1", "Same floor", 0, 100, 100);

        code.Should().Throw<InvalidOperationException>();
        floor.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(-11, 100, 100)]
    [InlineData(51, 100, 100)]
    [InlineData(0, 0, 100)]
    [InlineData(0, 100, -1)]
    [InlineData(0, 2000.5, 100)]
    [InlineData(0, double.NaN, 100)]
    public void AddLevel_Should_RefuseFloorsAndExtentsOutOfRange_When_Added(int floor, double width, double depth)
    {
        var add = () => Airport().AddTerminal("T1", "T", "DMO-T1").AddLevel("L0", "Level", floor, width, depth);

        add.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Contains_Should_IncludeTheEdges_When_PointsAreChecked()
    {
        var level = Airport().AddTerminal("T1", "T", "DMO-T1").AddLevel("L0", "Level", 0, 30, 20);

        level.Contains(0, 0).Should().BeTrue();
        level.Contains(30, 20).Should().BeTrue();
        level.Contains(30.01, 5).Should().BeFalse();
        level.Contains(-0.01, 5).Should().BeFalse();
    }

    [Fact]
    public void AddCheckpoint_Should_RefuseDuplicatesAndUnknownKinds_When_Added()
    {
        var level = Airport().AddTerminal("T1", "T", "DMO-T1").AddLevel("L0", "Level", 0, 30, 20);
        level.AddCheckpoint("SEC-N", "Security North", CheckpointKind.Security);

        var duplicate = () => level.AddCheckpoint("SEC-N", "Again", CheckpointKind.Security);
        var unknown = () => level.AddCheckpoint("X", "Unknown", (CheckpointKind)42);

        duplicate.Should().Throw<InvalidOperationException>();
        unknown.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Add_Should_Refuse_When_TheParentIsDeleted()
    {
        var checkpoint = Immigration();
        checkpoint.SoftDelete("admin", Now);

        var add = () => checkpoint.AddDesk("D01", null, DeskKind.Desk, ["CIT"]);

        add.Should().Throw<InvalidOperationException>();
    }

    #endregion

    #region Desk kinds and lane categories

    [Theory]
    [InlineData(CheckpointKind.CheckIn, DeskKind.Counter, true)]
    [InlineData(CheckpointKind.CheckIn, DeskKind.Desk, false)]
    [InlineData(CheckpointKind.Security, DeskKind.SecurityLane, true)]
    [InlineData(CheckpointKind.Security, DeskKind.EGate, false)]
    [InlineData(CheckpointKind.Immigration, DeskKind.Desk, true)]
    [InlineData(CheckpointKind.Immigration, DeskKind.EGate, true)]
    [InlineData(CheckpointKind.Emigration, DeskKind.EGate, true)]
    [InlineData(CheckpointKind.Emigration, DeskKind.Counter, false)]
    public void AllowedDeskKinds_Should_FollowTheCheckpointKind_When_DesksAreAdded(CheckpointKind checkpointKind, DeskKind deskKind, bool allowed)
    {
        var checkpoint = Airport().AddTerminal("T1", "T", "DMO-T1").AddLevel("L0", "Level", 0, 30, 20).AddCheckpoint("CP", "Checkpoint", checkpointKind);
        string[] categories = deskKind switch { DeskKind.Desk => ["CIT"], DeskKind.EGate => ["EG"], _ => [] };

        var add = () => checkpoint.AddDesk("D01", null, deskKind, categories);

        if (allowed)
            add.Should().NotThrow();
        else
            add.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void LaneCategories_Should_BeNormalisedSortedAndDistinct_When_Set()
    {
        var desk = Immigration().AddDesk("D01", "Desk 1", DeskKind.Desk, ["res", "CIT", " RES "]);

        desk.LaneCategories.Should().Equal("CIT", "RES");
        desk.LaneCategoryCodes.Should().Be("CIT,RES");
    }

    [Theory]
    [InlineData(DeskKind.Desk, new string[0])]
    [InlineData(DeskKind.EGate, new[] { "CIT" })]
    public void LaneCategories_Should_FollowTheBorderDeskRules_When_Set(DeskKind kind, string[] categories)
    {
        var add = () => Immigration().AddDesk("D01", null, kind, categories);

        add.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void LaneCategories_Should_BeRefused_When_ACounterOrSecurityLaneHasThem()
    {
        var level = Airport().AddTerminal("T1", "T", "DMO-T1").AddLevel("L0", "Level", 0, 30, 20);
        var counter = () => level.AddCheckpoint("CI-A", "Island A", CheckpointKind.CheckIn).AddDesk("C01", null, DeskKind.Counter, ["CIT"]);
        var lane = () => level.AddCheckpoint("SEC", "Security", CheckpointKind.Security).AddDesk("S01", null, DeskKind.SecurityLane, ["VIS"]);

        counter.Should().Throw<InvalidOperationException>();
        lane.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("C")]
    [InlineData("CITIZ")]
    [InlineData("C1T")]
    [InlineData("CI,T")]
    [InlineData(null)]
    public void LaneCategories_Should_RefuseMalformedCodes_When_Set(string code)
    {
        var add = () => Immigration().AddDesk("D01", null, DeskKind.Desk, [code]);

        add.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void LaneCategories_Should_BeBounded_When_Set()
    {
        var add = () => Immigration().AddDesk("D01", null, DeskKind.Desk, ["AA", "BB", "CC", "DD", "EE", "FF", "GG", "HH", "II"]);

        add.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EGate_Should_ServeEGateEligibleWithOthersAllowed_When_Set()
    {
        var gate = Immigration().AddDesk("EG-01", null, DeskKind.EGate, ["EG", "CIT"]);

        gate.LaneCategories.Should().Equal("CIT", "EG");
        gate.InService.Should().BeTrue();
        gate.SetInService(false);
        gate.InService.Should().BeFalse();
    }

    [Fact]
    public void LaneCategoryReference_Should_AllBeValid_When_Read()
    {
        LaneCategory.Reference.Should().OnlyContain(code => LaneCategory.IsValid(code));
    }

    #endregion

    #region Ranges and code mappings (ARV-015)

    [Fact]
    public void AddDeskRange_Should_CreatePaddedCodes_When_RangeIsValid()
    {
        var desks = Immigration().AddDeskRange("D", 1, 22, 2, DeskKind.Desk, ["CIT"]);

        desks.Should().HaveCount(22);
        desks[0].Code.Should().Be("D01");
        desks[^1].Code.Should().Be("D22");
        desks.Should().OnlyContain(d => d.LaneCategories.Count == 1);
    }

    [Theory]
    [InlineData("D", 5, 4, 2)]
    [InlineData("D", -1, 4, 2)]
    [InlineData("D", 1, 201, 3)]
    [InlineData("D", 1, 100, 2)]
    [InlineData("D", 1, 5, 5)]
    public void AddDeskRange_Should_RefuseBadBounds_When_Checked(string prefix, int from, int to, int width)
    {
        var add = () => Immigration().AddDeskRange(prefix, from, to, width, DeskKind.Desk, ["CIT"]);

        add.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void AddDeskRange_Should_AddNothing_When_ACodeIsTakenOrInvalid()
    {
        var checkpoint = Immigration();
        checkpoint.AddDesk("D03", null, DeskKind.Desk, ["CIT"]);

        var taken = () => checkpoint.AddDeskRange("D", 1, 5, 2, DeskKind.Desk, ["CIT"]);
        var invalid = () => checkpoint.AddDeskRange("d-", 1, 5, 2, DeskKind.Desk, ["CIT"]);
        var tooLong = () => checkpoint.AddDeskRange("ABCDEFGHIJKLMN", 1, 5, 4, DeskKind.Desk, ["CIT"]);

        taken.Should().Throw<InvalidOperationException>();
        invalid.Should().Throw<ArgumentException>();
        tooLong.Should().Throw<ArgumentException>();
        checkpoint.Desks.Should().HaveCount(1, "a refused range adds nothing");
    }

    [Theory]
    [InlineData(ExternalSystem.Aman, DeskKind.Desk, true)]
    [InlineData(ExternalSystem.Aman, DeskKind.EGate, true)]
    [InlineData(ExternalSystem.Aman, DeskKind.Counter, false)]
    [InlineData(ExternalSystem.Aodb, DeskKind.Counter, true)]
    [InlineData(ExternalSystem.Aodb, DeskKind.Desk, false)]
    [InlineData(ExternalSystem.Aodb, DeskKind.SecurityLane, false)]
    public void DeskCodeMapping_Should_FitTheDeskKind_When_Created(ExternalSystem system, DeskKind kind, bool allowed)
    {
        var level = Airport().AddTerminal("T1", "T", "DMO-T1").AddLevel("L0", "Level", 0, 30, 20);
        var checkpointKind = kind switch { DeskKind.Counter => CheckpointKind.CheckIn, DeskKind.SecurityLane => CheckpointKind.Security, _ => CheckpointKind.Immigration };
        string[] categories = kind switch { DeskKind.Desk => ["CIT"], DeskKind.EGate => ["EG"], _ => [] };
        var desk = level.AddCheckpoint("CP", "Checkpoint", checkpointKind).AddDesk("X1", null, kind, categories);

        var map = () => new DeskCodeMapping(system, "a-07", desk);

        if (allowed)
            map().Should().Match<DeskCodeMapping>(m => m.ExternalCode == "A-07" && m.SiteCode == "DMO-T1");
        else
            map.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(" a-07 ", "A-07")]
    [InlineData("DSK/12.3_x", "DSK/12.3_X")]
    [InlineData("-A", null)]
    [InlineData("A B", null)]
    [InlineData("A<1>", null)]
    [InlineData("", null)]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456", null)]
    public void NormalizeCode_Should_UpperCaseAndValidate_When_Read(string code, string expected)
    {
        DeskCodeMapping.NormalizeCode(code).Should().Be(expected);
    }

    [Fact]
    public void Assign_Should_StayInTheSite_When_TheDeskChanges()
    {
        var checkpoint = Immigration();
        var mapping = new DeskCodeMapping(ExternalSystem.Aman, "A-01", checkpoint.AddDesk("D01", null, DeskKind.Desk, ["CIT"]));
        var other = Airport().AddTerminal("T9", "Other", "DMO-T9").AddLevel("L0", "L", 0, 10, 10).AddCheckpoint("IMM", "I", CheckpointKind.Immigration)
            .AddDesk("D01", null, DeskKind.Desk, ["CIT"]);

        mapping.Assign(checkpoint.AddDesk("D02", null, DeskKind.Desk, ["CIT"]));
        var move = () => mapping.Assign(other);

        mapping.Desk.Code.Should().Be("D02");
        move.Should().Throw<InvalidOperationException>();
    }

    #endregion
}
