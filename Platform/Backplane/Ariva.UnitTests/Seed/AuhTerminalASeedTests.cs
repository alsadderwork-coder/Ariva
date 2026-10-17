using System.Text;
using System.Text.RegularExpressions;
using Ariva.Core.Domain.Components;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.InputModels;
using Ariva.Infra.Services.Seed;
using Ariva.Infra.Storage;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using static Ariva.Infra.Services.Seed.AuhTerminalALayout;

namespace Ariva.UnitTests.Seed;

/// <summary>
/// ARV-139a: the illustrative AUH Terminal A arrivals seed. Its zone profile v1 validates as built (a queue per lane
/// with entry and exit lines, two overflow bands, a staff and a service zone per counter linked to its desk), its counts
/// and geometry stay within bounds (CWE-120), every sensor reaches its zone, the plan passes the floor plan inspection,
/// nothing in it looks like a person, a document or a real staff or system name (data boundary), and only the demo
/// seeds can flag a site illustrative (CWE-269).
/// </summary>
public sealed partial class AuhTerminalASeedTests
{
    #region Arrange

    private static Level NewLevel()
    {
        var terminal = new Airport(AirportIata, null, AirportName, TimeZoneId).AddTerminal(TerminalCode, TerminalName, SiteCode);
        var level = terminal.AddLevel(LevelCode, LevelName, FloorNumber, WidthMetres, DepthMetres);
        level.Id = Guid.Parse("0199a000-0000-7000-8000-000000139a01");
        return level;
    }

    private static Dictionary<int, Guid> CounterIds() =>
        Enumerable.Range(1, Counters).ToDictionary(n => n, n => Guid.Parse($"0199a000-0000-7000-8000-0000001390{n:00}"));

    private static (ZoneProfile Profile, Level Level, Dictionary<int, Guid> Ids) Build()
    {
        var level = NewLevel();
        var ids = CounterIds();
        return (AuhTerminalASeed.BuildProfile(level, ids), level, ids);
    }

    #endregion

    #region Profile

    [Fact]
    public void BuildProfile_Should_Validate_When_BuiltFromTheLayout()
    {
        var (profile, level, _) = Build();

        profile.Validate(new Dictionary<Guid, Level> { [level.Id!.Value] = level }).Should().BeEmpty();
        profile.SiteCode.Should().Be("AUH-TA");
        profile.Zones.Where(z => z.Kind == ZoneKind.Queue).Select(z => z.Name).Should()
            .BeEquivalentTo("A-CRW", "A-DIP", "A-CIT", "A-RES", "A-GCC", "A-VIS", "A-TRF", "A-EG");
        profile.Zones.Where(z => z.Kind == ZoneKind.Overflow).Select(z => (z.Name, Feeds: z.QueueZone.Name)).Should()
            .BeEquivalentTo([("A-VIS-OV", "A-VIS"), ("A-EG-OV", "A-EG")]);
        profile.Zones.Count(z => z.Kind == ZoneKind.Staff).Should().Be(38);
        profile.Zones.Count(z => z.Kind == ZoneKind.Service).Should().Be(38);
        profile.Lines.Should().HaveCount(8 * 2 + 2, "an entry and an exit per queue and an overflow entry per band");
    }

    [Fact]
    public void BuildProfile_Should_GiveEveryQueueItsLaneAndAnAssumedCapacity()
    {
        var (profile, _, _) = Build();

        profile.Zones.Where(z => z.Kind == ZoneKind.Queue).Should().OnlyContain(z => z.Name == $"A-{z.LaneCategory}");
        profile.Zones.Where(z => z.Kind is ZoneKind.Queue or ZoneKind.Overflow).Should()
            .OnlyContain(z => z.PhysicalCapacity >= 1 && z.PhysicalCapacity <= Zone.MaxPhysicalCapacity);
        profile.Zones.Where(z => z.Kind is ZoneKind.Staff or ZoneKind.Service).Should().OnlyContain(z => z.PhysicalCapacity == null);
    }

    [Fact]
    public void BuildProfile_Should_LinkEachCountersStaffAndServiceZoneToItsDesk_When_Built()
    {
        // ARV-116: "<desk> staff" and "<desk> service", naming the desk, hanging off the queue of the counter's lane.
        var (profile, _, ids) = Build();

        for (var n = 1; n <= Counters; n++)
        {
            var lane = LaneOfCounter(n).Code;
            foreach (var (suffix, kind) in new[] { ("staff", ZoneKind.Staff), ("service", ZoneKind.Service) })
            {
                var zone = profile.Zones.Single(z => z.Name == $"IC-{n:00} {suffix}");
                zone.Kind.Should().Be(kind);
                zone.DeskId.Should().Be(ids[n]);
                zone.QueueZone.Name.Should().Be($"A-{lane}");
            }
        }
    }

    [Fact]
    public void BuildProfile_Should_GiveEveryQueueZoneAKeyThatFitsAMessageKey_When_Built()
    {
        // ARV-114c: <site>/<queue zone name> is at most 200 characters.
        var (profile, _, _) = Build();

        profile.Zones.Where(z => z.Kind == ZoneKind.Queue).Should().OnlyContain(z => Ariva.Core.Sensing.ZoneKeys.Fits(profile.SiteCode, z.Name));
        profile.Zones.Max(z => Ariva.Core.Sensing.ZoneKeys.For(profile.SiteCode, z.Name).Length).Should().BeLessThan(40);
    }

    [Fact]
    public void BuildProfile_Should_HashTheSame_When_BuiltTwice()
    {
        var (first, _, _) = Build();
        var (second, _, _) = Build();

        first.ComputeGeometryHash().Should().Be(second.ComputeGeometryHash());
    }

    [Fact]
    public void BuildProfile_Should_Refuse_When_ACounterHasNoDeskId()
    {
        var ids = CounterIds();
        ids.Remove(20);

        var act = () => AuhTerminalASeed.BuildProfile(NewLevel(), ids);

        act.Should().Throw<ArgumentException>().WithMessage("Counter 20*");
    }

    #endregion

    #region Counts and bounds (CWE-120)

    [Fact]
    public void Layout_Should_HaveTheReportedCountsAndEveryCounterInExactlyOneLane()
    {
        Counters.Should().Be(38);
        SmartGates.Should().Be(34);
        CounterLanes.Sum(l => l.LastCounter - l.FirstCounter + 1).Should().Be(Counters);
        Enumerable.Range(1, Counters).Select(n => LaneOfCounter(n).Code).Distinct().Should()
            .BeEquivalentTo("CRW", "DIP", "CIT", "RES", "GCC", "VIS", "TRF");
        Lanes.Select(l => l.Code).Should().OnlyContain(code => LaneCategory.IsValid(code));
        Lanes.Select(l => l.Code).Should().Contain(["GCC", "DIP", "TRF"], "the new lane codes fit the two to four letter rule");
    }

    [Fact]
    public void Layout_Should_StayWithinTheLevelAndTheProfileLimits()
    {
        var (profile, level, _) = Build();

        profile.Zones.Count.Should().BeLessThanOrEqualTo(ZoneProfile.MaxZones);
        profile.Lines.Count.Should().BeLessThanOrEqualTo(ZoneProfile.MaxLines);
        profile.Zones.SelectMany(z => z.Points).Should().OnlyContain(p => level.Contains(p.X, p.Y));
        profile.Zones.Should().OnlyContain(z => z.Points.Count == 4 && z.Polygon.Length <= Zone.PolygonLength);
        WidthMetres.Should().BeLessThanOrEqualTo(Level.MaxExtentMetres);
        DepthMetres.Should().BeLessThanOrEqualTo(Level.MaxExtentMetres);
        (SmartGateCode(SmartGates).Length, CounterCode(Counters).Length).Should().Be((5, 5));
        CounterBand(Counters).Bottom.Should().BeLessThan(DepthMetres);
        GateBand.Bottom.Should().BeLessThan(CounterStartY);
    }

    [Fact]
    public void Sensors_Should_BeBoundedUniqueAndReachTheirZones()
    {
        var (profile, level, _) = Build();
        var sensors = Sensors();
        var now = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

        sensors.Count.Should().BeInRange(1, MaxSensors);
        sensors.Select(s => s.Code).Should().OnlyHaveUniqueItems().And.OnlyContain(c => TopologyCodes.IsValid(c));
        foreach (var sensor in sensors)
        {
            var device = AuhTerminalASeed.NewSensor(sensor, level, now);
            device.State.Should().Be(DeviceState.Commissioning);
            device.CredentialHash.Should().BeNull("the seed issues no credential");
            var process = profile.Zones.Where(z => z.Name == sensor.QueueZoneName || z.QueueZone?.Name == sensor.QueueZoneName).ToList();
            process.Should().NotBeEmpty();
            process.Should().Contain(z => Geometry.Overlaps(device.FootprintRing, z.Points), $"{sensor.Code} reaches its zone (the devices API's coverage rule)");
        }
    }

    [Fact]
    public void Sensors_Should_CoverEveryCountersDeskZonesAndEveryQueueAndBand()
    {
        var (profile, level, _) = Build();
        var now = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
        var rings = Sensors().Select(s => AuhTerminalASeed.NewSensor(s, level, now).FootprintRing).ToList();

        profile.Zones.Should().OnlyContain(z => rings.Any(r => Geometry.Overlaps(r, z.Points)), "every zone is reached by a sensor");
        // A queue or band's every vertex lies under a footprint: the grid covers it.
        profile.Zones.Where(z => z.Kind is ZoneKind.Queue or ZoneKind.Overflow).SelectMany(z => z.Points)
            .Should().OnlyContain(p => rings.Any(r => Geometry.ContainsPoint(r, p)));
    }

    #endregion

    #region Floor plan

    [Fact]
    public void Plan_Should_PassTheFloorPlanInspectionAtTheLayoutsScale()
    {
        var bytes = AuhTerminalAPlan.Svg();

        var file = FloorPlanFiles.Inspect(bytes);

        file.Should().NotBeNull();
        file!.ContentType.Should().Be("image/svg+xml");
        (file.WidthPixels, file.HeightPixels).Should().Be(((int?)2000, (int?)1500));
        (file.WidthPixels * MetresPerPixel).Should().Be(WidthMetres);
        Encoding.UTF8.GetString(file.Bytes).Should().Contain("Illustrative, not surveyed").And.Contain("38, reported").And.Contain("34, reported");
        AuhTerminalAPlan.Svg().Should().Equal(bytes, "the same bytes every run");
        bytes.Length.Should().BeLessThan(200_000);
    }

    #endregion

    #region Data boundary

    /// <summary>Real staff or system names that must never appear in the illustrative seed (names of the airport's operator, border and airline systems and vendors).</summary>
    private static readonly string[] RealNames =
    [
        "Etihad", "AD Airports", "Abu Dhabi Airports", "ICP", "GDRFA", "UAE PASS", "UAEPASS", "Emirates ID", "SITA", "Amadeus", "IDEMIA", "Vision-Box",
        "Thales", "Xovis", "Collins", "Dnata", "Aviation Security", "Police", "KPF"
    ];

    [GeneratedRegex(@"\b[A-Z]{1,2}\d{6,9}\b|784-?\d{4}-?\d{7}-?\d|[\w.+-]+@[\w-]+\.\w+|\+?\d[\d ]{8,}\d", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex IdentifierLike();

    private static IEnumerable<string> SeededText()
    {
        var (profile, level, _) = Build();
        var now = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
        yield return SiteName;
        yield return AirportName;
        yield return TerminalName;
        yield return LevelName;
        yield return CheckpointName;
        yield return profile.Name;
        foreach (var zone in profile.Zones)
            yield return zone.Name;
        foreach (var line in profile.Lines)
            yield return line.Name;
        for (var n = 1; n <= Counters; n++)
            yield return CounterCode(n);
        for (var n = 1; n <= SmartGates; n++)
            yield return SmartGateCode(n);
        foreach (var sensor in Sensors())
        {
            var device = AuhTerminalASeed.NewSensor(sensor, level, now);
            yield return device.Code;
            yield return device.Model;
        }

        // The plan's words (its numbers are coordinates, not identifiers).
        foreach (Match text in SvgText().Matches(Encoding.UTF8.GetString(AuhTerminalAPlan.Svg())))
            yield return text.Groups[1].Value;
    }

    [GeneratedRegex("<(?:text|title)[^>]*>([^<]*)</", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SvgText();

    [Fact]
    public void Seed_Should_HoldNoPersonDocumentOrRealStaffOrSystemName()
    {
        foreach (var text in SeededText())
        {
            IdentifierLike().IsMatch(text).Should().BeFalse($"'{text[..Math.Min(text.Length, 60)]}' looks like an identifier");
            RealNames.Should().NotContain(name => text.Contains(name, StringComparison.OrdinalIgnoreCase), $"'{text[..Math.Min(text.Length, 60)]}'");
            foreach (var fragment in new[] { "officer", "passport", "traveller", "traveler", "badge", "employee" })
                text.Should().NotContainEquivalentOf(fragment);
        }
    }

    #endregion

    #region The illustrative flag (CWE-269)

    [Fact]
    public void CreateIllustrative_Should_FlagTheSite_When_CalledAndANewSiteShouldNot()
    {
        Site.CreateIllustrative("AUH-TA", "x").IsIllustrative.Should().BeTrue();
        new Site("AUH-TA", "x").IsIllustrative.Should().BeFalse();
        typeof(Site).GetProperty(nameof(Site.IsIllustrative))!.SetMethod!.IsPublic.Should().BeFalse();
        typeof(Site).GetMethods().Where(m => m.DeclaringType == typeof(Site) && !m.IsStatic && m.Name != "get_IsIllustrative")
            .Should().NotContain(m => m.Name.Contains("Illustrative", StringComparison.Ordinal), "no method changes the flag of an existing site");
    }

    [Fact]
    public void SiteRequests_Should_CarryNoIllustrativeFlag()
    {
        // The admin API binds these; with no such member, an "illustrative" in a body is ignored (E2E sites.spec.ts proves it).
        foreach (var type in new[] { typeof(CreateSiteRequest), typeof(UpdateSiteRequest) })
            type.GetProperties().Should().NotContain(p => p.Name.Contains("Illustrative", StringComparison.OrdinalIgnoreCase), type.Name);
    }

    [Fact]
    public void CreateIllustrative_Should_BeCalledOnlyByTheDemoSeeds()
    {
        var seedFolder = Path.Combine("Ariva.Infra", "Services", "Seed") + Path.DirectorySeparatorChar;
        var callers = Directory.EnumerateFiles(RepositoryPaths.Platform, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                        !f.Contains($"{Path.DirectorySeparatorChar}Ariva.UnitTests{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                        !f.Contains($"{Path.DirectorySeparatorChar}Ariva.IntegrationTests{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("CreateIllustrative(", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(RepositoryPaths.Platform, f))
            .ToList();

        callers.Should().NotBeEmpty();
        callers.Should().OnlyContain(f => f.EndsWith(Path.Combine("Ariva.Core", "Domain", "Entities", "Site.cs"), StringComparison.Ordinal) || f.Contains(seedFolder, StringComparison.Ordinal),
            "only the demo seeds flag a site illustrative");
    }

    #endregion
}
