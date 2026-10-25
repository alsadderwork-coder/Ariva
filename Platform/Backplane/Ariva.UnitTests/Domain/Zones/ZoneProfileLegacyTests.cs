using Ariva.Core.Domain.Components;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using FluentAssertions;

namespace Ariva.UnitTests.Domain.Zones;

/// <summary>
/// ARV-162 (CWE-79, CWE-501): what a draft copied from an older version may carry. A zone or line name the name rule now
/// refuses (ARV-159) or a zone outline that touches itself by ARV-161's rule is copied as stored, so it can be fixed in the
/// draft, and blocks publication until it is: the problem names it escaped, never as stored. Messages of the other edits
/// name a legacy zone or line escaped too. Values written past the entities, as an older database would hold them, are set
/// by reflection. Every invisible character here is an escape.
/// </summary>
public sealed class ZoneProfileLegacyTests
{
    private const string Override = "\x202E";
    private const string Tag = "\U000E0041";

    /// <summary>A rectangle 24 x 12 m at (10, 10) with a fifth vertex 5 mm from its bottom edge: it touches itself (ARV-161).</summary>
    private const string Touching = "10.000 10.000,34.000 10.000,34.000 22.000,22.000 10.005,10.000 22.000";

    private static readonly DateTime Now = new(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);

    private static Level NewLevel()
    {
        var level = new Airport("DMO", null, "Demo", "Asia/Amman").AddTerminal("T1", "T1", "DMO-T1").AddLevel("L0", "Arrivals", 0, 60, 40);
        level.Id = Guid.Parse("0199a000-0000-7000-8000-00000000c162");
        return level;
    }

    private static IReadOnlyList<FloorPoint> Rect(double x, double y, double w, double h) =>
        [new(x, y), new(x + w, y), new(x + w, y + h), new(x, y + h)];

    private static Dictionary<Guid, Level> Levels(Level level) => new() { [level.Id!.Value] = level };

    /// <summary>A publishable draft: queue Snake A with entry and exit lines, and a count line.</summary>
    private static (ZoneProfile Profile, Level Level, Zone Queue, Line Entry, Line Count) Draft(string name = "Arrivals")
    {
        var level = NewLevel();
        var profile = new ZoneProfile("DMO-T1", name);
        var queue = profile.AddZone("Snake A", ZoneKind.Queue, level, Rect(10, 10, 24, 12));
        var entry = profile.AddLine("Entry A", LineRole.Entry, level, new FloorPoint(10, 12), new FloorPoint(10, 16), queue);
        profile.AddLine("Exit A", LineRole.Exit, level, new FloorPoint(30, 22), new FloorPoint(34, 22), queue);
        var count = profile.AddLine("Count hall", LineRole.Count, level, new FloorPoint(40, 0), new FloorPoint(40, 40));
        return (profile, level, queue, entry, count);
    }

    /// <summary>Writes a stored value past the entity's rules, as a database from before ARV-158, ARV-159 or ARV-161 holds it.</summary>
    private static void Store<T>(T entity, string property, string value) => typeof(T).GetProperty(property)!.SetValue(entity, value);

    private static bool Printable(string text) => text.All(c => c is >= ' ' and <= '~');

    [Fact]
    public void Validate_Should_NameLegacyNamesEscapedAndBlockPublication_When_TheDraftCarriesThem()
    {
        var (profile, level, queue, entry, count) = Draft();
        profile.Validate(Levels(level)).Should().BeEmpty("the draft is publishable as built");
        Store(queue, nameof(Zone.Name), "Snake" + Override + "A");
        Store(entry, nameof(Line.Name), "Entry" + Tag);
        Store(count, nameof(Line.Name), "Count\x200Bhall");
        Store(profile, nameof(ZoneProfile.Name), "Arr\x0007ivals");

        var problems = profile.Validate(Levels(level));

        problems.Should().BeEquivalentTo(
        [
            "The profile's name Arr\\u0007ivals " + ZoneProfile.UnsafeNameProblem + " rename the draft.",
            "Zone Snake\\u202EA: its name " + ZoneProfile.UnsafeNameProblem + " rename the zone.",
            "Line Count\\u200Bhall: its name " + ZoneProfile.UnsafeNameProblem + " remove the line and add it again with a clean name.",
            "Line Entry\\U000E0041: its name " + ZoneProfile.UnsafeNameProblem + " remove the line and add it again with a clean name."
        ]);
        problems.Should().OnlyContain(p => Printable(p), "a legacy name is never shown as stored");
        profile.Publish(1, Levels(level), "admin", Now).Should().BeEquivalentTo(problems, "publication is blocked");
        profile.Status.Should().Be(ZoneProfileStatus.Draft);
    }

    [Fact]
    public void Validate_Should_LetTheDraftPublish_When_TheZoneIsRenamedAndTheLinesAddedAgain()
    {
        var (profile, level, queue, entry, count) = Draft();
        Store(queue, nameof(Zone.Name), "Snake" + Override + "A");
        Store(entry, nameof(Line.Name), "Entry" + Tag);
        Store(profile, nameof(ZoneProfile.Name), "Arr\x0007ivals");

        profile.RenameZone(queue, "Snake A");
        profile.RemoveLine(entry);
        profile.AddLine("Entry A", LineRole.Entry, level, new FloorPoint(10, 12), new FloorPoint(10, 16), queue);
        profile.Rename("Arrivals");

        profile.Validate(Levels(level)).Should().BeEmpty();
        profile.Publish(1, Levels(level), "admin", Now).Should().BeEmpty();
        profile.Status.Should().Be(ZoneProfileStatus.Published);
    }

    [Fact]
    public void Validate_Should_RefuseAnOutlineThatTouchesItselfUntilItIsMoved_When_ItWasStoredBeforeTheRule()
    {
        var (profile, level, queue, _, _) = Draft();
        Store(queue, nameof(Zone.Polygon), Touching);

        var problems = profile.Validate(Levels(level));

        problems.Should().Equal("Zone Snake A: " + ZoneProfile.OutlineNotSimple);
        profile.Publish(1, Levels(level), "admin", Now).Should().Equal(problems);

        profile.MoveZone(queue, level, Rect(10, 10, 24, 12));
        profile.Validate(Levels(level)).Should().BeEmpty();
    }

    [Fact]
    public void Validate_Should_RefuseASliverUntilItIsMoved_When_ItWasStoredBeforeTheMinimumWidth()
    {
        // ARV-170: a triangle 10 m long and 1 mm wide, simple by every rule before the minimum width of 1 cm.
        var (profile, level, queue, _, _) = Draft();
        Store(queue, nameof(Zone.Polygon), "10.000 10.000,20.000 10.000,15.000 10.001");

        var problems = profile.Validate(Levels(level));

        problems.Should().Equal("Zone Snake A: " + ZoneProfile.OutlineNotSimple);
        profile.Publish(1, Levels(level), "admin", Now).Should().Equal(problems);
        var add = () => profile.AddZone("Sliver", ZoneKind.Queue, level, [new(10, 30), new(20, 30), new(15, 30.001)]);
        add.Should().Throw<ArgumentException>("a new zone is refused the same way").WithMessage("*at least 1 cm wide*");
        var moveIntoASliver = () => profile.MoveZone(queue, level, [new(10, 20), new(20, 20), new(15, 20.009)]);
        moveIntoASliver.Should().Throw<ArgumentException>("a move into a sliver is refused too").WithMessage("*at least 1 cm wide*");

        profile.MoveZone(queue, level, Rect(10, 10, 24, 12));
        profile.Validate(Levels(level)).Should().BeEmpty();
    }

    [Fact]
    public void CreateDraft_Should_CopyLegacyNamesAndOutlinesAsStored_When_AnOlderVersionHoldsThem()
    {
        var (published, level, queue, entry, _) = Draft();
        published.Publish(1, Levels(level), "admin", Now).Should().BeEmpty();
        Store(queue, nameof(Zone.Name), "Snake" + Override + "A");
        Store(queue, nameof(Zone.Polygon), Touching);
        Store(entry, nameof(Line.Name), "Entry" + Tag);
        Store(published, nameof(ZoneProfile.Name), "Arrivals" + Tag);

        var copy = () => published.CreateDraft(null);
        copy.Should().Throw<ArgumentException>("the draft's own name keeps the rule: give it a new one")
            .Which.Message.Should().StartWith(TopologyCodes.UnsafeNameMessage);
        var draft = published.CreateDraft("Next");

        draft.Zones.Should().ContainSingle(z => z.Name == "Snake" + Override + "A").Which.Polygon.Should().Be(Touching, "copied as stored, to be fixed in the draft");
        draft.Lines.Should().Contain(l => l.Name == "Entry" + Tag);
        draft.Validate(Levels(level)).Should().HaveCount(3).And.OnlyContain(p => Printable(p));
    }

    [Fact]
    public void Edits_Should_NameALegacyZoneOrLineEscaped_When_TheyAreRefused()
    {
        var (profile, level, queue, entry, _) = Draft();
        Store(queue, nameof(Zone.Name), "Snake" + Override + "A");
        Store(entry, nameof(Line.Name), "Entry" + Tag);
        profile.AddZone("Band", ZoneKind.Overflow, level, Rect(10, 30, 24, 6), queue);

        var exitAgain = () => profile.AddLine("Exit B", LineRole.Exit, level, new FloorPoint(30, 22), new FloorPoint(34, 22), queue);
        var offEdge = () => profile.AddLine("Entry B", LineRole.Entry, level, new FloorPoint(20, 15), new FloorPoint(20, 16), queue);
        var stranding = () => profile.MoveZone(queue, level, Rect(11, 10, 23, 12));
        var withBand = () => profile.RemoveZone(queue);

        exitAgain.Should().Throw<InvalidOperationException>().WithMessage("Zone Snake\\u202EA already has its exit line.");
        offEdge.Should().Throw<InvalidOperationException>().WithMessage("Line Entry B must lie on an edge of zone Snake\\u202EA.");
        stranding.Should().Throw<InvalidOperationException>().Which.Message.Should().StartWith("Line Entry\\U000E0041 would no longer lie");
        withBand.Should().Throw<InvalidOperationException>().WithMessage("Zones hang off Snake\\u202EA; remove them first.");
        ZoneProfile.Label("Snake A").Should().Be("Snake A", "a clean name is shown as it is");
    }

    [Fact]
    public void Label_Should_EscapeACleanNameWithABackslash_When_ItSpellsAnEscape()
    {
        // Review recommendation 2: "Snake \u202EA" typed as ASCII is a clean name, but shown as stored it would read like
        // the escaped form of a legacy one; its backslash is doubled, so the two can never be confused.
        const string spelled = "Snake \\u202EA";
        TopologyCodes.IsName(spelled).Should().BeTrue();
        ZoneProfile.Label(spelled).Should().Be("Snake \\\\u202EA");
        ZoneProfile.Label("Snake" + Override + "A").Should().Be("Snake\\u202EA");
        ZoneProfile.Label(spelled).Should().NotBe(ZoneProfile.Label("Snake " + Override + "A"));
    }
}
