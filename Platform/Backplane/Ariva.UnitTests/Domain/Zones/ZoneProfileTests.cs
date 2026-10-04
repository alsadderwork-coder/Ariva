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
