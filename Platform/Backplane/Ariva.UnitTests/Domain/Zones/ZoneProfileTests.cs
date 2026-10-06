using Ariva.Core.Domain.Components;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using FluentAssertions;

namespace Ariva.UnitTests.Domain.Zones;

/// <summary>
/// ARV-016: the zone profile aggregate. Zones are simple polygons inside their level with unique names; entry and exit
/// lines lie on a queue zone's edge; a queue needs entry lines and one exit; drafts change, published versions never do;
/// the geometry hash depends on geometry only.
/// </summary>
public sealed class ZoneProfileTests
{
    private static readonly DateTime Now = new(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc);

    private static Level NewLevel(string site = "DMO-T1", double width = 60, double depth = 40)
    {
        var level = new Airport("DMO", null, "Demo", "Asia/Amman").AddTerminal("T1", "T1", site).AddLevel("L0", "Arrivals", 0, width, depth);
        level.Id = Guid.Parse("0199a000-0000-7000-8000-00000000c0de");
        return level;
    }

    private static IReadOnlyList<FloorPoint> Rect(double x, double y, double w, double h) =>
        [new(x, y), new(x + w, y), new(x + w, y + h), new(x, y + h)];

    private static Dictionary<Guid, Level> Levels(Level level) => new() { [level.Id!.Value] = level };

    /// <summary>A publishable profile: snake 24 x 12 m with an entry and an exit line, an overflow band and a count line.</summary>
    private static (ZoneProfile Profile, Level Level, Zone Queue) Publishable(bool reverseOrder = false)
    {
        var level = NewLevel();
        var profile = new ZoneProfile("DMO-T1", "Arrivals immigration");
        var queue = profile.AddZone("Snake A", ZoneKind.Queue, level, Rect(10, 10, 24, 12));
        Zone overflow = null;
        void AddLines()
        {
            profile.AddLine("Entry A", LineRole.Entry, level, new FloorPoint(10, 12), new FloorPoint(10, 16), queue);
            profile.AddLine("Exit A", LineRole.Exit, level, new FloorPoint(30, 22), new FloorPoint(34, 22), queue);
        }

        void AddOverflow()
        {
            overflow = profile.AddZone("Overflow A", ZoneKind.Overflow, level, Rect(10, 4, 24, 6), queue);
            profile.AddLine("Overflow entry A", LineRole.OverflowEntry, level, new FloorPoint(10, 5), new FloorPoint(10, 9), overflow);
            profile.AddLine("Count hall", LineRole.Count, level, new FloorPoint(40, 0), new FloorPoint(40, 40));
        }

        if (reverseOrder) { AddOverflow(); AddLines(); }
        else { AddLines(); AddOverflow(); }
        return (profile, level, queue);
    }

    #region Zones

    [Fact]
    public void AddZone_Should_RoundAndStoreThePolygon_When_Valid()
    {
        var zone = new ZoneProfile("DMO-T1", "Draft").AddZone("Snake", ZoneKind.Queue, NewLevel(), [new(1.00049, 1), new(5, 1), new(5, 4)]);

        zone.Polygon.Should().Be("1.000 1.000,5.000 1.000,5.000 4.000");
        zone.Points.Should().HaveCount(3);
        zone.AreaSquareMetres.Should().BeApproximately(6, 1e-9);
    }

    public static TheoryData<string, string> InvalidPolygons => new()
    {
        { "0 0,10 0", "fewer than three points" },
        { "0 0,10 10,10 0,0 10", "self-intersecting" },
        { "50 30,70 30,70 35,50 35", "outside the level (width 60)" },
        { "-1 0,5 0,5 5", "negative coordinates" },
        { "0 0,10 0,20 0", "no area" }
    };

    [Theory]
    [MemberData(nameof(InvalidPolygons))]
    public void AddZone_Should_RefuseInvalidPolygons_When_Added(string ring, string because)
    {
        var add = () => new ZoneProfile("DMO-T1", "Draft").AddZone("Snake", ZoneKind.Queue, NewLevel(), Geometry.ParseRing(ring, 200));

        add.Should().Throw<ArgumentException>(because);
    }

    [Fact]
    public void AddZone_Should_RefuseMoreThan200Vertices_When_Added()
    {
        var circle = Enumerable.Range(0, 201).Select(i => new FloorPoint(30 + (10 * Math.Cos(i * 2 * Math.PI / 201)), 20 + (10 * Math.Sin(i * 2 * Math.PI / 201)))).ToList();

        var add = () => new ZoneProfile("DMO-T1", "Draft").AddZone("Round", ZoneKind.Queue, NewLevel(), circle);

        add.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AddZone_Should_RefuseDuplicateNamesIgnoringCase_When_Added()
    {
        var level = NewLevel();
        var profile = new ZoneProfile("DMO-T1", "Draft");
        profile.AddZone("Snake A", ZoneKind.Queue, level, Rect(0, 0, 5, 5));

        var add = () => profile.AddZone("snake a", ZoneKind.Queue, level, Rect(10, 10, 5, 5));

        add.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AddZone_Should_RequireAQueueZone_When_TheZoneHangsOffOne()
    {
        var level = NewLevel();
        var profile = new ZoneProfile("DMO-T1", "Draft");
        var queue = profile.AddZone("Snake", ZoneKind.Queue, level, Rect(0, 0, 5, 5));
        var service = profile.AddZone("Desk 1 service", ZoneKind.Service, level, Rect(6, 0, 2, 2), queue, Guid.NewGuid());

        var orphan = () => profile.AddZone("Orphan", ZoneKind.Staff, level, Rect(10, 0, 2, 2));
        var offService = () => profile.AddZone("Nested", ZoneKind.Staff, level, Rect(10, 0, 2, 2), service);
        var queueOffQueue = () => profile.AddZone("Queue 2", ZoneKind.Queue, level, Rect(20, 0, 2, 2), queue);
        var deskOnOverflow = () => profile.AddZone("Overflow", ZoneKind.Overflow, level, Rect(30, 0, 2, 2), queue, Guid.NewGuid());

        orphan.Should().Throw<InvalidOperationException>();
        offService.Should().Throw<InvalidOperationException>();
        queueOffQueue.Should().Throw<InvalidOperationException>();
        deskOnOverflow.Should().Throw<InvalidOperationException>();
        service.QueueZone.Should().BeSameAs(queue);
    }

    [Fact]
    public void AddZone_Should_RefuseALevelOfAnotherSite_When_Added()
    {
        var add = () => new ZoneProfile("DMO-T2", "Draft").AddZone("Snake", ZoneKind.Queue, NewLevel("DMO-T1"), Rect(0, 0, 5, 5));

        add.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MoveZone_Should_KeepLinesOnTheEdge_When_VerticesMove()
    {
        var (profile, level, queue) = Publishable();

        var stranding = () => profile.MoveZone(queue, level, Rect(11, 10, 23, 12));
        stranding.Should().Throw<InvalidOperationException>("the entry line at x = 10 would leave the edge");

        profile.MoveZone(queue, level, Rect(10, 10, 26, 12));
        queue.Points[2].Should().Be(new FloorPoint(36, 22));
    }

    [Fact]
    public void RemoveZone_Should_RemoveItsLinesAndRefuseWhileZonesHangOffIt_When_Removed()
    {
        var (profile, _, queue) = Publishable();
        var overflow = profile.Zones.Single(z => z.Kind == ZoneKind.Overflow);

        var blocked = () => profile.RemoveZone(queue);
        blocked.Should().Throw<InvalidOperationException>();

        var removed = profile.RemoveZone(overflow);
        removed.Select(l => l.Name).Should().Equal("Overflow entry A");
        profile.Lines.Should().NotContain(l => l.Name == "Overflow entry A");
    }

    #endregion

    #region Zone keys (ARV-114c)

    // Site DMO-T1 has 6 characters: with the slash, a queue zone name of 193 characters makes a key of exactly 200.
    private const string Site = "DMO-T1";

    /// <summary>A character outside the basic plane: two UTF-16 units, one character to PostgreSQL.</summary>
    private const string Astral = "\U0001F6EB";

    private static string Name(int asciiCharacters, int astralCharacters = 0) =>
        string.Concat(Enumerable.Repeat(Astral, astralCharacters)) + new string('q', asciiCharacters);

    [Fact]
    public void AddZone_Should_AcceptAQueueZone_When_ItsZoneKeyIsExactly200Characters()
    {
        var name = Name(193);

        var zone = new ZoneProfile(Site, "Draft").AddZone(name, ZoneKind.Queue, NewLevel(), Rect(1, 1, 4, 4));

        zone.Name.Should().Be(name);
        Ariva.Core.Sensing.ZoneKeys.For(Site, zone.Name).Should().HaveLength(200);
        Ariva.Core.Sensing.ZoneKeys.MaxQueueZoneNameLength(Site).Should().Be(193);
    }

    [Fact]
    public void AddZone_Should_RefuseAQueueZoneWithoutRepeatingItsName_When_ItsZoneKeyIs201Characters()
    {
        var profile = new ZoneProfile(Site, "Draft");
        var name = Name(194);

        var add = () => profile.AddZone(name, ZoneKind.Queue, NewLevel(), Rect(1, 1, 4, 4));

        add.Should().Throw<ArgumentException>()
            .Which.Message.Should().StartWith(ZoneProfile.QueueZoneNameTooLong(Site)).And.Contain("at most 193 characters").And.NotContain(name);
        profile.Zones.Should().BeEmpty();
    }

    [Fact]
    public void AddZone_Should_CountCharactersAsPostgresDoes_When_TheNameHasCharactersOutsideTheBasicPlane()
    {
        // 7 astral characters and 186 letters: 200 UTF-16 units (the name rule's limit) but 193 characters, a key of 200.
        var fits = Name(186, astralCharacters: 7);
        // 6 astral characters and 188 letters: 200 UTF-16 units again, but 194 characters, a key of 201.
        var over = Name(188, astralCharacters: 6);
        fits.Length.Should().Be(200);
        over.Length.Should().Be(200);
        var profile = new ZoneProfile(Site, "Draft");

        profile.AddZone(fits, ZoneKind.Queue, NewLevel(), Rect(1, 1, 4, 4)).Name.Should().Be(fits);
        var add = () => profile.AddZone(over, ZoneKind.Queue, NewLevel(), Rect(10, 1, 4, 4));

        add.Should().Throw<ArgumentException>().WithMessage(ZoneProfile.QueueZoneNameTooLong(Site) + "*");
        Ariva.Core.Messaging.MessageKeys.Fits(Ariva.Core.Sensing.ZoneKeys.For(Site, fits)).Should().BeTrue();
    }

    [Fact]
    public void RenameZone_Should_RefuseAQueueZoneNameAndKeepTheOldOne_When_ItsZoneKeyWouldExceed200Characters()
    {
        var (profile, _, queue) = Publishable();

        var rename = () => profile.RenameZone(queue, Name(194));

        rename.Should().Throw<ArgumentException>().WithMessage(ZoneProfile.QueueZoneNameTooLong(Site) + "*");
        queue.Name.Should().Be("Snake A");
        profile.RenameZone(queue, Name(193));
        queue.Name.Should().HaveLength(193);
    }

    [Fact]
    public void AddZone_Should_AcceptLongOverflowServiceAndStaffNames_When_OnlyTheQueueZoneNameIsInTheKey()
    {
        // Hanging zones share their queue zone's key (OverflowDetected carries the band's name in its payload, not its key).
        var (profile, level, queue) = Publishable();

        foreach (var (kind, x) in new[] { (ZoneKind.Overflow, 40.0), (ZoneKind.Service, 46.0), (ZoneKind.Staff, 52.0) })
        {
            var name = $"{kind} " + Name(200 - kind.ToString().Length - 1);
            var zone = profile.AddZone(name, kind, level, Rect(x, 30, 4, 4), queue);
            profile.RenameZone(zone, name.Replace('q', 'r'));
            zone.Name.Should().HaveLength(200);
        }
    }

    [Fact]
    public void Validate_Should_NameAQueueZoneWhoseKeyIsTooLong_When_ADraftCopiesItFromAnEarlierVersion()
    {
        // A version published before ARV-114c may hold such a name; its next draft cannot be published until it is renamed.
        var (profile, level, queue) = Publishable();
        typeof(Zone).GetProperty(nameof(Zone.Name))!.SetValue(queue, Name(194));

        var problems = profile.Validate(Levels(level));

        problems.Should().ContainSingle().Which.Should().EndWith(ZoneProfile.QueueZoneNameTooLong(Site));
        profile.Publish(1, Levels(level), "e2e.admin", Now).Should().HaveCount(1);
        profile.Status.Should().Be(ZoneProfileStatus.Draft);
    }

    #endregion


    #region Lines

    [Theory]
    [InlineData(LineRole.Entry, 10, 12, 10, 16, true, "on the left edge")]
    [InlineData(LineRole.Entry, 12, 12, 12, 16, false, "inside the zone, not on an edge")]
    [InlineData(LineRole.Exit, 30, 22, 34, 22, true, "on the top edge")]
    [InlineData(LineRole.Exit, 30, 22.5, 34, 22.5, false, "half a metre off the edge")]
    [InlineData(LineRole.Entry, 10, 20, 14, 22, false, "across a corner, on two edges")]
    public void AddLine_Should_LieOnAnEdgeOfItsQueueZone_When_EntryOrExit(LineRole role, double ax, double ay, double bx, double by, bool valid, string because)
    {
        var level = NewLevel();
        var profile = new ZoneProfile("DMO-T1", "Draft");
        var queue = profile.AddZone("Snake A", ZoneKind.Queue, level, Rect(10, 10, 24, 12));

        var add = () => profile.AddLine("Line", role, level, new FloorPoint(ax, ay), new FloorPoint(bx, by), queue);

        if (valid)
            add.Should().NotThrow(because);
        else
            add.Should().Throw<InvalidOperationException>(because);
    }

    [Fact]
    public void AddLine_Should_EnforceRolesZonesAndOneExit_When_Added()
    {
        var (profile, level, queue) = Publishable();
        var overflow = profile.Zones.Single(z => z.Kind == ZoneKind.Overflow);

        var secondExit = () => profile.AddLine("Exit B", LineRole.Exit, level, new FloorPoint(10, 18), new FloorPoint(10, 20), queue);
        var entryWithoutZone = () => profile.AddLine("Entry X", LineRole.Entry, level, new FloorPoint(1, 1), new FloorPoint(1, 3));
        var entryOnOverflow = () => profile.AddLine("Entry Y", LineRole.Entry, level, new FloorPoint(10, 5), new FloorPoint(10, 9), overflow);
        var duplicateName = () => profile.AddLine("entry a", LineRole.Count, level, new FloorPoint(1, 1), new FloorPoint(1, 3));
        var pointLine = () => profile.AddLine("Dot", LineRole.Count, level, new FloorPoint(1, 1), new FloorPoint(1, 1.004));
        var outside = () => profile.AddLine("Far", LineRole.Count, level, new FloorPoint(1, 1), new FloorPoint(61, 1));

        secondExit.Should().Throw<InvalidOperationException>();
        entryWithoutZone.Should().Throw<InvalidOperationException>();
        entryOnOverflow.Should().Throw<InvalidOperationException>();
        duplicateName.Should().Throw<InvalidOperationException>();
        pointLine.Should().Throw<ArgumentException>();
        outside.Should().Throw<ArgumentException>();
    }

    #endregion

    #region Validation, publishing and the hash

    [Fact]
    public void Validate_Should_ListWhatAQueueAndOverflowLack_When_Incomplete()
    {
        var level = NewLevel();
        var profile = new ZoneProfile("DMO-T1", "Draft");
        var queue = profile.AddZone("Snake A", ZoneKind.Queue, level, Rect(10, 10, 24, 12));
        profile.AddZone("Overflow A", ZoneKind.Overflow, level, Rect(10, 4, 24, 6), queue);

        var problems = profile.Validate(Levels(level));

        problems.Should().BeEquivalentTo(
            "Zone Overflow A: an overflow zone needs an overflow entry line.",
            "Zone Snake A: a queue zone needs at least one entry line.",
            "Zone Snake A: a queue zone needs exactly one exit line.");
        new ZoneProfile("DMO-T1", "Empty").Validate(Levels(level)).Should().Equal("The profile has no zones.");
    }

    [Fact]
    public void Validate_Should_CatchALevelThatShrankOrWasDeleted_When_Publishing()
    {
        var (profile, level, _) = Publishable();
        level.Update("Arrivals", 0, 30, 40);

        profile.Validate(Levels(level)).Should().Contain(p => p.Contains("outside level", StringComparison.Ordinal));
        profile.Validate(new Dictionary<Guid, Level>()).Should().Contain(p => p.Contains("no longer exists", StringComparison.Ordinal));
    }

    [Fact]
    public void Publish_Should_NumberHashAndFreezeTheProfile_When_Valid()
    {
        var (profile, level, queue) = Publishable();

        var problems = profile.Publish(12, Levels(level), "e2e.admin", Now);

        problems.Should().BeEmpty();
        profile.Status.Should().Be(ZoneProfileStatus.Published);
        profile.Version.Should().Be(12);
        profile.GeometryHash.Should().MatchRegex("^[0-9a-f]{64}$");
        profile.PublishedOn.Should().Be(Now);

        var changes = new Action[]
        {
            () => profile.AddZone("Late", ZoneKind.Queue, level, Rect(0, 0, 2, 2)),
            () => profile.AddLine("Late", LineRole.Count, level, new FloorPoint(1, 1), new FloorPoint(1, 2)),
            () => profile.MoveZone(queue, level, Rect(10, 10, 24, 14)),
            () => profile.RenameZone(queue, "Renamed"),
            () => profile.RemoveLine(profile.Lines[0]),
            () => profile.RemoveZone(profile.Zones.Single(z => z.Kind == ZoneKind.Overflow)),
            () => profile.Rename("Renamed"),
            () => profile.Publish(13, Levels(level), "x", Now)
        };
        foreach (var change in changes)
            change.Should().Throw<InvalidOperationException>("a published version is immutable");
    }

    [Fact]
    public void Publish_Should_RaiseZoneProfilePublishedKeyedBySite_When_Valid()
    {
        var (profile, level, _) = Publishable();
        profile.Id = Guid.CreateVersion7();

        profile.Publish(3, Levels(level), "e2e.zonemanager", Now, replacesVersion: 2);

        var published = profile.DomainEvents.Should().ContainSingle().Which.Should().BeOfType<Ariva.Core.Domain.Events.ZoneProfilePublished>().Subject;
        published.ProfileId.Should().Be(profile.Id.Value);
        published.SiteCode.Should().Be("DMO-T1");
        published.GetPartitionKey().Should().Be("DMO-T1", "the activated-profile topic is compacted per site");
        published.ProfileVersion.Should().Be(3);
        published.Version.Should().Be(1, "the event's schema version, as on every event");
        published.ReplacesVersion.Should().Be(2);
        published.GeometryHash.Should().Be(profile.GeometryHash);
        published.PublishedBy.Should().Be("e2e.zonemanager");
        (published.ZoneCount, published.LineCount).Should().Be((profile.Zones.Count, profile.Lines.Count));
        published.OccurredOn.Should().Be(Now);
    }

    [Fact]
    public void Publish_Should_RaiseNothing_When_Invalid()
    {
        var level = NewLevel();
        var profile = new ZoneProfile("DMO-T1", "Draft");
        profile.AddZone("Snake A", ZoneKind.Queue, level, Rect(10, 10, 24, 12));

        profile.Publish(1, Levels(level), "admin", Now);

        profile.DomainEvents.Should().BeEmpty();
    }

    [Fact]
    public void Publish_Should_ReturnTheProblems_When_Invalid()
    {
        var level = NewLevel();
        var profile = new ZoneProfile("DMO-T1", "Draft");
        profile.AddZone("Snake A", ZoneKind.Queue, level, Rect(10, 10, 24, 12));

        var problems = profile.Publish(1, Levels(level), "admin", Now);

        problems.Should().NotBeEmpty();
        profile.Status.Should().Be(ZoneProfileStatus.Draft);
        profile.Version.Should().BeNull();
    }

    [Fact]
    public void GeometryHash_Should_DependOnGeometryOnly_When_BuiltTwice()
    {
        var first = Publishable().Profile;
        var second = Publishable(reverseOrder: true).Profile;

        first.ComputeGeometryHash().Should().Be(second.ComputeGeometryHash(), "the same zones and lines in another order and with other ids");
    }

    [Fact]
    public void GeometryHash_Should_Change_When_AVertexMovesByAMillimetre()
    {
        var (a, levelA, queueA) = Publishable();
        var (b, levelB, queueB) = Publishable();
        var (c, levelC, queueC) = Publishable();
        b.MoveZone(queueB, levelB, [new(10, 10), new(34.001, 10), new(34, 22), new(10, 22)]);
        c.MoveZone(queueC, levelC, [new(10, 10), new(34.0004, 10), new(34, 22), new(10, 22)]);

        b.ComputeGeometryHash().Should().NotBe(a.ComputeGeometryHash());
        c.ComputeGeometryHash().Should().Be(a.ComputeGeometryHash(), "below a millimetre is rounding");
        queueA.Should().NotBeNull();
        levelA.Should().NotBeNull();
    }

    [Fact]
    public void GeometryHash_Should_StayTheSameAcrossReleases_When_TheReferenceProfileIsHashed()
    {
        // The canonical form is a contract: evidence packs and signed contracts quote this hash. Change it only with a new
        // canonical prefix (ariva-zone-profile-v2) and keep v1 verifiable.
        Publishable().Profile.ComputeGeometryHash().Should().Be(ReferenceHash);
    }

    private const string ReferenceHash = "fd3d7d1585070bd347de877d87aa3bb4e28d2b571081986650ec934471d9a1da";

    [Fact]
    public void CreateDraft_Should_CopyGeometryWithNewIdsAndTheSameHash_When_Copied()
    {
        var (profile, level, _) = Publishable();
        profile.Publish(12, Levels(level), "admin", Now);

        var draft = profile.CreateDraft("v13 draft");

        draft.Status.Should().Be(ZoneProfileStatus.Draft);
        draft.BasedOnVersion.Should().Be(12);
        draft.Zones.Should().HaveCount(profile.Zones.Count);
        draft.Lines.Should().HaveCount(profile.Lines.Count);
        draft.Zones.Single(z => z.Kind == ZoneKind.Overflow).QueueZone.Should().BeSameAs(draft.Zones.Single(z => z.Kind == ZoneKind.Queue));
        draft.ComputeGeometryHash().Should().Be(profile.GeometryHash);
        draft.Validate(Levels(level)).Should().BeEmpty();
    }

    [Fact]
    public void SetZoneLane_Should_MarkAQueueAsALanesQueueWithoutChangingTheHash_When_Set()
    {
        // ARV-057: the immigration screen's waits per lane come from this link; it is no geometry.
        var (profile, level, queue) = Publishable();
        var hash = profile.ComputeGeometryHash();
        profile.SetZoneLane(queue, "CIT");
        queue.LaneCategory.Should().Be("CIT");
        profile.ComputeGeometryHash().Should().Be(hash);
        profile.Invoking(p => p.SetZoneLane(queue, "cit")).Should().Throw<ArgumentException>();
        profile.Invoking(p => p.SetZoneLane(queue, "CITIZENS")).Should().Throw<ArgumentException>();
        profile.Invoking(p => p.SetZoneLane(profile.Zones.Single(z => z.Kind == ZoneKind.Overflow), "CIT")).Should().Throw<InvalidOperationException>("only a queue zone is a lane's queue");

        profile.Publish(12, Levels(level), "admin", Now);
        profile.Invoking(p => p.SetZoneLane(queue, null)).Should().Throw<InvalidOperationException>("a published version never changes");
        var draft = profile.CreateDraft("v13");
        var copy = draft.Zones.Single(z => z.Kind == ZoneKind.Queue);
        copy.LaneCategory.Should().Be("CIT", "a draft keeps the lanes");
        draft.SetZoneLane(copy, null);
        copy.LaneCategory.Should().BeNull();
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(190, true)]
    [InlineData(5_000, true)]
    [InlineData(0, false)]
    [InlineData(-5, false)]
    [InlineData(5_001, false)]
    [InlineData(int.MaxValue, false)]
    public void SetZoneCapacity_Should_AcceptOneToFiveThousandPeople_When_Set(int capacity, bool valid)
    {
        // ARV-114a: the occupancy sanity check of F18 needs the zone's physical capacity.
        var (profile, _, queue) = Publishable();

        var set = () => profile.SetZoneCapacity(queue, capacity);

        if (valid)
        {
            set.Should().NotThrow();
            queue.PhysicalCapacity.Should().Be(capacity);
        }
        else
        {
            set.Should().Throw<ArgumentOutOfRangeException>().Which.Message.Should().StartWith("A physical capacity is 1 to 5,000 people.");
            queue.PhysicalCapacity.Should().BeNull();
        }
    }

    [Fact]
    public void SetZoneCapacity_Should_KeepTheHashAndThePublishedVersion_When_SetOnQueuesAndBands()
    {
        var (profile, level, queue) = Publishable();
        var hash = profile.ComputeGeometryHash();
        var band = profile.Zones.Single(z => z.Kind == ZoneKind.Overflow);
        profile.SetZoneCapacity(queue, 190);
        profile.SetZoneCapacity(band, 60);

        profile.ComputeGeometryHash().Should().Be(hash, "a capacity changes no count or wait, so F22's hash stays ariva-zone-profile-v1");
        profile.ComputeGeometryHash().Should().Be(ReferenceHash);
        var service = profile.AddZone("Desk 1", ZoneKind.Service, level, Rect(40, 10, 2, 2), queue);
        profile.Invoking(p => p.SetZoneCapacity(service, 5)).Should().Throw<InvalidOperationException>("only queue zones and overflow bands hold a queue");
        profile.RemoveZone(service);

        profile.Publish(12, Levels(level), "admin", Now);
        profile.GeometryHash.Should().Be(ReferenceHash);
        profile.Invoking(p => p.SetZoneCapacity(queue, 200)).Should().Throw<InvalidOperationException>("a published version never changes");
        profile.Invoking(p => p.SetZoneCapacity(queue, null)).Should().Throw<InvalidOperationException>();
        queue.PhysicalCapacity.Should().Be(190);

        var draft = profile.CreateDraft("v13");
        var copy = draft.Zones.Single(z => z.Kind == ZoneKind.Queue);
        copy.PhysicalCapacity.Should().Be(190, "a draft keeps the capacities");
        draft.Zones.Single(z => z.Kind == ZoneKind.Overflow).PhysicalCapacity.Should().Be(60);
        draft.SetZoneCapacity(copy, null);
        copy.PhysicalCapacity.Should().BeNull();
        queue.PhysicalCapacity.Should().Be(190, "the published version is untouched");
    }

    [Fact]
    public void Retire_Should_OnlyRetireAPublishedVersion_When_Called()
    {
        var (profile, level, _) = Publishable();
        var early = () => profile.Retire(Now);
        profile.Publish(1, Levels(level), "admin", Now);

        profile.Retire(Now.AddDays(1));

        early.Should().Throw<InvalidOperationException>();
        profile.Status.Should().Be(ZoneProfileStatus.Retired);
        var again = () => profile.Retire(Now.AddDays(2));
        again.Should().Throw<InvalidOperationException>();
    }

    #endregion
}
