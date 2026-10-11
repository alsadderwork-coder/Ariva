using System.Text;
using System.Text.RegularExpressions;
using Ariva.Core.Domain.Components;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Sensing;
using Ariva.Core.Services.Topology;
using FluentAssertions;

namespace Ariva.UnitTests.Domain.Topology;

/// <summary>
/// ARV-122a: export names every reference by codes and names and carries no id; import creates everything anew at the
/// target site through the entities' methods, remaps every reference through the bundle's codes and names, rebases it to
/// the target's site code, and reproduces the portable geometry hash; refusals are fixed texts.
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

    /// <summary>A source site: two levels, an immigration checkpoint with a desk and an e-gate out of service, a check-in counter, two mappings, a published profile and a plan.</summary>
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
        profile.AddLine("Entry A", LineRole.Entry, arrivals, new FloorPoint(10, 12), new FloorPoint(10, 16), queue);
        profile.AddLine("Exit A", LineRole.Exit, arrivals, new FloorPoint(30, 22), new FloorPoint(34, 22), queue);
        profile.AddLine("Count hall", LineRole.Count, departures, new FloorPoint(1, 1), new FloorPoint(1, 9));
        var levels = new Dictionary<Guid, Level> { [arrivals.Id!.Value] = arrivals, [departures.Id!.Value] = departures };
        profile.Publish(3, levels, "admin", Now).Should().BeEmpty();

        var plan = WithId(new FloorPlan(arrivals, "0199a0000000700080000000000000aa.svg", "image/svg+xml", 10, new string('a', 64), "arrivals.svg", 100, 50, 0.05, 1, 2));
        return new SiteConfigurationSource([terminal], [arrivals, departures], [immigration, checkIn], [desk, gate, counter], mappings, profile, [plan]);
    }

    /// <summary>The content an import reads: the export's parts through the bundle, as the service passes them.</summary>
    private static SiteConfigurationContent RoundTrip(ExportedSiteConfiguration exported, byte[] file = null)
    {
        var parts = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [SiteConfigurationBundles.Topology] = SiteConfigurationParts.Write(exported.Topology),
            [SiteConfigurationBundles.DeskCodeMappings] = SiteConfigurationParts.Write(exported.DeskCodeMappings),
            [SiteConfigurationBundles.ZoneProfile] = SiteConfigurationParts.Write(exported.ZoneProfiles),
            [SiteConfigurationBundles.FloorPlans] = SiteConfigurationParts.Write(exported.FloorPlans)
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
        data.Counts.Should().Be(new Ariva.Core.Domain.ViewModels.SiteConfigurationCountsViewModel(1, 2, 2, 3, 2, 2, 3, 1));

        var json = Encoding.UTF8.GetString(SiteConfigurationParts.Write(data.Topology)) + Encoding.UTF8.GetString(SiteConfigurationParts.Write(data.ZoneProfiles)) +
                   Encoding.UTF8.GetString(SiteConfigurationParts.Write(data.DeskCodeMappings)) + Encoding.UTF8.GetString(SiteConfigurationParts.Write(data.FloorPlans));
        Regex.IsMatch(json, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-").Should().BeFalse("no id travels in a bundle");
        json.Should().NotContain("SRC", "no part names the source site");
    }

    [Fact]
    public void Export_Should_CarryAnEmptyProfileList_When_TheSiteHasNoPublishedProfile()
    {
        var source = Source() with { PublishedProfile = null };

        var data = SiteConfigurationTransfer.Export(source).Data;

        data.ZoneProfiles.Should().BeEmpty();
        data.PortableGeometryHash.Should().BeNull();
        data.Counts.Zones.Should().Be(0);
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
        var source = Source() with { PublishedProfile = null, FloorPlans = [] };
        var airport = WithId(new Airport("CCC", null, "Many", "Asia/Dubai"));
        var atBound = Enumerable.Range(1, SiteConfigurationLimits.MaxTerminals).Select(n => WithId(airport.AddTerminal($"T{n}", $"T{n}", "SRC"))).ToList();

        SiteConfigurationTransfer.Export(source with { Terminals = atBound, Levels = [], Checkpoints = [], Desks = [], DeskCodeMappings = [] })
            .Data.Counts.Terminals.Should().Be(SiteConfigurationLimits.MaxTerminals);
        SiteConfigurationTransfer.Export(source with { Terminals = [.. atBound, WithId(airport.AddTerminal("T99", "T99", "SRC"))] })
            .ErrorMessages.Should().Equal(SiteConfigurationErrors.TooManyRecords);
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
        var content = RoundTrip(SiteConfigurationTransfer.Export(Source() with { PublishedProfile = null, FloorPlans = [], DeskCodeMappings = [] }).Data);
        var airport = WithId(new Airport("ZZZ", null, "Target", "Asia/Dubai"));

        var plan = SiteConfigurationTransfer.Plan("DST", content, Airports(airport), "ZZZ").Data;

        plan.Draft.Should().BeNull();
        plan.PortableGeometryHash.Should().BeNull();
        plan.FloorPlans.Should().BeEmpty();
        plan.Counts.Desks.Should().Be(3);
    }

    #endregion
}
