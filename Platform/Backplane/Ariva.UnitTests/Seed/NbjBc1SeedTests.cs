// ARV-139c (CWE-200): the NBJ-BC1 seed is compiled only in Debug builds (Ariva.Infra.csproj), and so are its tests;
// NbjSiteScopeTests runs in both and proves a Release build carries none of it.
#if ARIVA_DEV_SEED
using System.Text;
using System.Text.RegularExpressions;
using Ariva.Core.Domain.Components;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using Ariva.Infra.Services.Seed;
using Ariva.Infra.Storage;
using FluentAssertions;
using static Ariva.Infra.Services.Seed.NbjBc1Layout;

namespace Ariva.UnitTests.Seed;

/// <summary>
/// ARV-139c: the NBJ terminal BC1 seed, from the 2018 design drawings. Its zone profile v1 validates over both levels
/// as built (per hall a shared queue, an overflow band, an e-gates' queue, a staff and a service zone per desk linked
/// to its desk), its counts match the drawings (13 double booths, 26 desks and 5 e-gates per row), its geometry and
/// sensors stay within bounds (CWE-120), every sensor reaches its zone, each schematic passes the floor plan inspection
/// unchanged, and every seeded text is built only from an allowlisted vocabulary (Ariva's own words, the codes ARV-139c
/// defines, the airport's name and time zone), so nothing else read from the drawings can reach it (CWE-200). The opt-in
/// and the Release exclusion are in NbjSiteScopeTests.
/// </summary>
public sealed partial class NbjBc1SeedTests
{
    #region Arrange

    private static Dictionary<string, Level> NewLevels()
    {
        var terminal = new Airport(AirportIata, null, AirportName, TimeZoneId).AddTerminal(TerminalCode, TerminalName, SiteCode);
        var levels = new Dictionary<string, Level>(StringComparer.Ordinal);
        var i = 0;
        foreach (var hall in Halls)
        {
            var level = terminal.AddLevel(hall.LevelCode, hall.LevelName, hall.FloorNumber, hall.WidthMetres, hall.DepthMetres);
            level.Id = Guid.Parse($"0199a000-0000-7000-8000-000000139c0{++i}");
            levels[hall.LevelCode] = level;
        }

        return levels;
    }

    private static Dictionary<string, IReadOnlyDictionary<int, Guid>> DeskIds() =>
        Halls.Select((hall, h) => (hall.LevelCode, Ids: (IReadOnlyDictionary<int, Guid>)Enumerable.Range(1, DesksPerRow)
                .ToDictionary(n => n, n => Guid.Parse($"0199a000-0000-7000-8000-0000013{h}c0{n:00}"))))
            .ToDictionary(x => x.LevelCode, x => x.Ids, StringComparer.Ordinal);

    private static (ZoneProfile Profile, Dictionary<string, Level> Levels) Build()
    {
        var levels = NewLevels();
        return (NbjBc1Seed.BuildProfile(levels, DeskIds()), levels);
    }

    private static Dictionary<Guid, Level> ById(Dictionary<string, Level> levels) => levels.Values.ToDictionary(l => l.Id!.Value);

    #endregion

    #region Profile

    [Fact]
    public void BuildProfile_Should_Validate_When_BuiltFromTheLayout()
    {
        var (profile, levels) = Build();

        profile.Validate(ById(levels)).Should().BeEmpty();
        profile.SiteCode.Should().Be("NBJ-BC1");
        profile.Zones.Where(z => z.Kind == ZoneKind.Queue).Select(z => (z.Name, z.LaneCategory)).Should()
            .BeEquivalentTo([("A-ALL", "ALL"), ("A-EG", "EG"), ("D-ALL", "ALL"), ("D-EG", "EG")]);
        profile.Zones.Where(z => z.Kind == ZoneKind.Overflow).Select(z => (z.Name, Feeds: z.QueueZone.Name)).Should()
            .BeEquivalentTo([("A-ALL-OV", "A-ALL"), ("D-ALL-OV", "D-ALL")]);
        profile.Zones.Count(z => z.Kind == ZoneKind.Staff).Should().Be(52);
        profile.Zones.Count(z => z.Kind == ZoneKind.Service).Should().Be(52);
        profile.Lines.Should().HaveCount((4 * 2) + 2, "an entry and an exit per queue and an overflow entry per band");
    }

    [Fact]
    public void BuildProfile_Should_PutEachHallOnItsOwnLevel()
    {
        var (profile, levels) = Build();

        foreach (var hall in Halls)
        {
            var levelId = levels[hall.LevelCode].Id!.Value;
            profile.Zones.Where(z => z.Name.StartsWith(hall.Prefix + "-", StringComparison.Ordinal) || z.Name.StartsWith(hall.DeskPrefix, StringComparison.Ordinal))
                .Should().HaveCount(3 + (2 * DesksPerRow)).And.OnlyContain(z => z.LevelId == levelId);
        }
    }

    [Fact]
    public void BuildProfile_Should_LinkEachDesksStaffAndServiceZoneToItsDeskAndTheSharedQueue()
    {
        var (profile, _) = Build();
        var ids = DeskIds();

        foreach (var hall in Halls)
        {
            for (var n = 1; n <= DesksPerRow; n++)
            {
                foreach (var (suffix, kind) in new[] { ("staff", ZoneKind.Staff), ("service", ZoneKind.Service) })
                {
                    var zone = profile.Zones.Single(z => z.Name == $"{hall.DeskCode(n)} {suffix}");
                    zone.Kind.Should().Be(kind);
                    zone.DeskId.Should().Be(ids[hall.LevelCode][n]);
                    zone.QueueZone.Name.Should().Be(hall.QueueName, "no lane segregation: every desk serves the shared queue");
                }
            }
        }
    }

    [Fact]
    public void BuildProfile_Should_GiveQueuesAndBandsAnAssumedCapacityAndKeysThatFitAMessageKey()
    {
        var (profile, _) = Build();

        profile.Zones.Where(z => z.Kind is ZoneKind.Queue or ZoneKind.Overflow).Should()
            .OnlyContain(z => z.PhysicalCapacity >= 1 && z.PhysicalCapacity <= Zone.MaxPhysicalCapacity);
        profile.Zones.Where(z => z.Kind is ZoneKind.Staff or ZoneKind.Service).Should().OnlyContain(z => z.PhysicalCapacity == null);
        profile.Zones.Where(z => z.Kind == ZoneKind.Queue).Should().OnlyContain(z => Ariva.Core.Sensing.ZoneKeys.Fits(profile.SiteCode, z.Name));
    }

    [Fact]
    public void BuildProfile_Should_HashTheSame_When_BuiltTwice()
    {
        Build().Profile.ComputeGeometryHash().Should().Be(Build().Profile.ComputeGeometryHash());
    }

    [Fact]
    public void BuildProfile_Should_Refuse_When_ADeskHasNoIdOrALevelIsMissing()
    {
        var ids = DeskIds();
        ids["ARR"] = ids["ARR"].Where(p => p.Key != 7).ToDictionary(p => p.Key, p => p.Value);
        var levels = NewLevels();

        ((Action)(() => NbjBc1Seed.BuildProfile(levels, ids))).Should().Throw<ArgumentException>().WithMessage("Desk IM-07*");
        levels.Remove("DEP");
        ((Action)(() => NbjBc1Seed.BuildProfile(levels, DeskIds()))).Should().Throw<ArgumentException>().WithMessage("Level DEP*");
    }

    #endregion

    #region Counts and bounds (CWE-120)

    [Fact]
    public void Layout_Should_MatchTheDrawingsCounts()
    {
        (BoothsPerRow, DesksPerBooth, DesksPerRow, EGatesPerRow).Should().Be((13, 2, 26, 5));
        Halls.Select(h => (h.LevelCode, h.CheckpointCode, h.Arrivals)).Should().Equal(("ARR", "IMM", true), ("DEP", "EMI", false));
        LaneCategory.IsValid(SharedLane).Should().BeTrue();
        Halls.Should().OnlyContain(h => h.EGatesLeft == h.Arrivals, "arrivals e-gates at the row's left end, departures at its right (drawings)");
    }

    [Fact]
    public void Layout_Should_KeepDesksApartAndInsideTheirBooths()
    {
        foreach (var hall in Halls)
        {
            var bands = Enumerable.Range(1, DesksPerRow).Select(hall.DeskBand).ToList();
            bands.Zip(bands.Skip(1)).Should().OnlyContain(p => p.First.Right <= p.Second.Left + 1e-9);
            bands[0].Left.Should().Be(hall.FirstBoothX);
            bands[^1].Right.Should().BeApproximately(hall.LastBoothEndX, 1e-9);
            var (gateLeft, gateRight) = hall.EGateBand;
            (gateRight <= hall.FirstBoothX || gateLeft >= hall.LastBoothEndX).Should().BeTrue("the e-gates stand beside the booths");
            gateLeft.Should().BeGreaterThan(0);
            gateRight.Should().BeLessThan(hall.WidthMetres);
            hall.BoothEndY.Should().BeLessThan(hall.DepthMetres);
            hall.DeskCode(DesksPerRow).Length.Should().BeLessThanOrEqualTo(16);
            ((Action)(() => hall.DeskBand(0))).Should().Throw<ArgumentOutOfRangeException>();
            ((Action)(() => hall.DeskBand(DesksPerRow + 1))).Should().Throw<ArgumentOutOfRangeException>();
        }
    }

    [Fact]
    public void Layout_Should_StayWithinTheLevelsAndTheProfileLimits()
    {
        var (profile, levels) = Build();
        var byId = ById(levels);

        profile.Zones.Count.Should().BeLessThanOrEqualTo(ZoneProfile.MaxZones);
        profile.Lines.Count.Should().BeLessThanOrEqualTo(ZoneProfile.MaxLines);
        profile.Zones.Should().OnlyContain(z => z.Points.All(p => byId[z.LevelId].Contains(p.X, p.Y)));
        profile.Zones.Should().OnlyContain(z => z.Points.Count == 4 && z.Polygon.Length <= Zone.PolygonLength);
        Halls.Should().OnlyContain(h => h.WidthMetres <= Level.MaxExtentMetres && h.DepthMetres <= Level.MaxExtentMetres);
    }

    [Fact]
    public void Sensors_Should_BeBoundedUniqueAndCoverEveryZone()
    {
        var (profile, levels) = Build();
        var now = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
        var sensors = Sensors();

        sensors.Count.Should().BeInRange(1, MaxSensors);
        sensors.Select(s => s.Code).Should().OnlyHaveUniqueItems().And.OnlyContain(c => TopologyCodes.IsValid(c));
        var devices = sensors.Select(s => (Sensor: s, Device: NbjBc1Seed.NewSensor(s, levels[s.LevelCode], now))).ToList();
        foreach (var (sensor, device) in devices)
        {
            device.State.Should().Be(DeviceState.Commissioning);
            device.CredentialHash.Should().BeNull("the seed issues no credential");
            var process = profile.Zones.Where(z => z.Name == sensor.QueueZoneName || z.QueueZone?.Name == sensor.QueueZoneName).ToList();
            process.Should().Contain(z => z.LevelId == levels[sensor.LevelCode].Id && Geometry.Overlaps(device.FootprintRing, z.Points),
                $"{sensor.Code} reaches its zone (the devices API's coverage rule)");
        }

        foreach (var zone in profile.Zones)
        {
            var rings = devices.Where(d => levels[d.Sensor.LevelCode].Id == zone.LevelId).Select(d => d.Device.FootprintRing).ToList();
            rings.Should().Contain(r => Geometry.Overlaps(r, zone.Points), $"{zone.Name} is reached by a sensor");
            if (zone.Kind is ZoneKind.Queue or ZoneKind.Overflow)
                zone.Points.Should().OnlyContain(p => rings.Any(r => Geometry.ContainsPoint(r, p)), $"the grid covers {zone.Name}");
        }
    }

    #endregion

    #region Floor plans

    [Fact]
    public void Plans_Should_PassTheFloorPlanInspectionAtTheLayoutsScale()
    {
        foreach (var hall in Halls)
        {
            var bytes = NbjBc1Plan.Svg(hall);

            var file = FloorPlanFiles.Inspect(bytes);

            file.Should().NotBeNull(hall.LevelCode);
            file.ContentType.Should().Be("image/svg+xml");
            file.Bytes.Should().Equal(bytes, "the sanitiser has nothing to drop or rewrite in Ariva's own schematic ({0})", hall.LevelCode);
            (file.WidthPixels * MetresPerPixel).Should().Be(hall.WidthMetres);
            (file.HeightPixels * MetresPerPixel).Should().Be(hall.DepthMetres);
            Encoding.UTF8.GetString(file.Bytes).Should().Contain("Illustrative, not surveyed").And.Contain("2018 design drawings").And.Contain("13 double booths");
            NbjBc1Plan.Svg(hall).Should().Equal(bytes, "the same bytes every run");
            bytes.Length.Should().BeLessThan(200_000);
        }
    }

    #endregion

    #region Data boundary (CWE-200)

    /// <summary>
    /// Ariva's own words: the generic vocabulary of a border control hall and of Ariva's seeds and schematics. A seeded text
    /// may use nothing else besides <see cref="StoryCodes"/>, <see cref="StoryPlaceNames"/> and numbers, so no name of an
    /// organisation, system, person, document or sheet from the drawings can reach the seed without a deliberate change here.
    /// </summary>
    private static readonly HashSet<string> ArivaWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "airside", "arrivals", "as", "baggage", "booths", "border", "built", "control", "counter", "counters", "demo", "departure",
        "departures", "design", "desks", "differ", "double", "drawings", "e", "emigration", "entry", "exit", "floor", "from", "gates",
        "ground", "hall", "health", "illustrative", "immigration", "international", "lane", "lounge", "may", "no", "not", "one",
        "overflow", "overhead", "queue", "reclaim", "schematic", "seed", "segregation", "service", "shared", "staff", "stereo",
        "surveyed", "svg", "terminal", "the", "to", "v1"
    };

    /// <summary>
    /// The codes ARV-139c defines: the site and terminal (NBJ-BC1, BC1), the levels (ARR, DEP), the checkpoints (IMM, EMI),
    /// the lanes (ALL, EG), the desk and e-gate prefixes (IM, EM, EGA, EGD), the halls' letters in zone and sensor codes (A,
    /// D), the overflow band's suffix (OV) and the sensor kinds (Q, O, G, K).
    /// </summary>
    private static readonly HashSet<string> StoryCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "NBJ", "BC1", "ARR", "DEP", "IMM", "EMI", "ALL", "EG", "IM", "EM", "EGA", "EGD", "A", "D", "OV", "Q", "O", "G", "K"
    };

    /// <summary>The airport's name and time zone as the story names them: Dr. António Agostinho Neto International Airport, Luanda, Africa/Luanda.</summary>
    private static readonly HashSet<string> StoryPlaceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Dr", "António", "Agostinho", "Neto", "Airport", "Luanda", "Africa"
    };

    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Word();

    /// <summary>A sheet id of the drawings' set (ETP-ARQ-nnn), in any spelling.</summary>
    [GeneratedRegex(@"ETP\W*ARQ", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SheetId();

    [GeneratedRegex(@"\b[A-Z]{1,2}\d{6,9}\b|[\w.+-]+@[\w-]+\.\w+|\+?\d[\d ]{8,}\d", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex IdentifierLike();

    [GeneratedRegex("<(?:text|title)[^>]*>([^<]*)</", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SvgText();

    /// <summary>The words of a text that are neither numbers nor in the allowlisted vocabulary.</summary>
    private static IEnumerable<string> UnknownWords(string text) =>
        Word().Matches(text).Select(m => m.Value)
            .Where(w => !w.All(char.IsAsciiDigit) && !ArivaWords.Contains(w) && !StoryCodes.Contains(w) && !StoryPlaceNames.Contains(w));

    /// <summary>
    /// Every text the seed writes: the site, airport, terminal, levels, checkpoints, desks and e-gates (codes and names), the
    /// lanes, the zone profile, zones and lines, the sensors (codes and model), the publisher's name, and the floor plans'
    /// file names and every label of the schematics.
    /// </summary>
    private static IEnumerable<string> SeededText()
    {
        var (profile, levels) = Build();
        var now = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
        yield return SiteCode;
        yield return SiteName;
        yield return AirportIata;
        yield return AirportName;
        yield return TimeZoneId;
        yield return TerminalCode;
        yield return TerminalName;
        yield return SharedLane;
        yield return LaneCategory.EGateEligible;
        yield return NbjBc1Seed.SystemUserName;
        yield return profile.Name;
        foreach (var hall in Halls)
        {
            yield return hall.LevelCode;
            yield return hall.LevelName;
            yield return hall.CheckpointCode;
            yield return hall.CheckpointName;
            yield return hall.UpstreamLabel;
            yield return hall.DownstreamLabel;
            for (var n = 1; n <= DesksPerRow; n++)
                yield return hall.DeskCode(n);
            for (var n = 1; n <= EGatesPerRow; n++)
                yield return hall.EGateCode(n);
            yield return NbjBc1Plan.FileName(hall);
            foreach (Match text in SvgText().Matches(Encoding.UTF8.GetString(NbjBc1Plan.Svg(hall))))
                yield return text.Groups[1].Value;
        }

        foreach (var zone in profile.Zones)
            yield return zone.Name;
        foreach (var line in profile.Lines)
            yield return line.Name;
        foreach (var sensor in Sensors())
        {
            var device = NbjBc1Seed.NewSensor(sensor, levels[sensor.LevelCode], now);
            yield return device.Code;
            yield return device.Model;
        }
    }

    [Fact]
    public void Seed_Should_BuildEveryTextFromTheAllowlistedVocabulary()
    {
        var texts = SeededText().ToList();

        texts.Should().HaveCountGreaterThan(300, "every desk, zone, line, sensor and label is checked");
        texts.SelectMany(t => UnknownWords(t).Select(w => $"'{w}' in '{t[..Math.Min(t.Length, 60)]}'")).Distinct().Should()
            .BeEmpty("a seeded text uses only Ariva's own words, the codes ARV-139c defines and the airport's name and time zone");
        texts.Should().NotContain(t => SheetId().IsMatch(t), "no sheet id of the drawings is seeded");
    }

    [Fact]
    public void Seed_Should_HoldNoIdentifierOrPersonalDataTerm()
    {
        foreach (var text in SeededText())
        {
            IdentifierLike().IsMatch(text).Should().BeFalse($"'{text[..Math.Min(text.Length, 60)]}' looks like an identifier");
            foreach (var fragment in new[] { "officer", "passport", "traveller", "traveler", "badge", "employee" })
                text.Should().NotContainEquivalentOf(fragment);
        }
    }

    [Theory]
    [InlineData("Sheet ETP-ARQ-000", "a sheet id")]
    [InlineData("Operated by Example Holdings", "a name outside the vocabulary")]
    [InlineData("Booth 3 of the Mezzanine", "a word outside the vocabulary")]
    public void Vocabulary_Should_RefuseText_When_ItHasAWordOutsideTheAllowlist(string text, string reason)
    {
        UnknownWords(text).Should().NotBeEmpty(reason);
    }

    #endregion
}
#endif
