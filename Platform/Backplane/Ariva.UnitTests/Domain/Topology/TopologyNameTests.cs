using Ariva.Core.Domain.Components;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using FluentAssertions;

namespace Ariva.UnitTests.Domain.Topology;

/// <summary>
/// ARV-159 (CWE-79, CWE-501): site and topology names follow the display text rule per code point
/// (<see cref="TopologyCodes.IsName"/>): no control character, no invisible format character of any plane (a right-to-left
/// override, a zero-width space, an Arabic letter mark, a tag character), no lone surrogate. Names in Arabic (with their
/// vowel marks), other scripts and emoji are still names. Every entry point refuses with one fixed text that never repeats
/// the name: airport, terminal, level, checkpoint, desk, zone profile, zone, line and site. Every invisible character here
/// is an escape.
/// </summary>
public sealed class TopologyNameTests
{
    private const string Override = "\x202E";
    private const string Tag = "\U000E0041";

    /// <summary>"Arrivals hall" in Arabic, and "airport" with its vowel marks (combining marks, category Mn).</summary>
    private const string Arabic = "\x0635\x0627\x0644\x0629 \x0627\x0644\x0648\x0635\x0648\x0644";
    private const string ArabicWithMarks = "\x0645\x064E\x0637\x0627\x0631";

    private static Airport Airport(string name = "Demo International") => new("DMO", "ODMO", name, "Asia/Amman");

    private static Terminal Terminal() => Airport().AddTerminal("T1", "Terminal 1", "DMO-T1");

    private static Level Level()
    {
        var level = Terminal().AddLevel("L0", "Arrivals", 0, 300, 120);
        level.Id = Guid.Parse("0199a000-0000-7000-8000-00000000c159");
        return level;
    }

    private static Checkpoint Immigration() => Level().AddCheckpoint("IMM", "Immigration", CheckpointKind.Immigration);

    private static IReadOnlyList<FloorPoint> Rect(double x, double y, double w, double h) =>
        [new(x, y), new(x + w, y), new(x + w, y + h), new(x, y + h)];

    #region The rule

    public static TheoryData<string, string, bool> Names => new()
    {
        { "plain text", "Arrivals hall A (north)", true },
        { "Arabic", Arabic, true },
        { "Arabic with vowel marks", ArabicWithMarks, true },
        { "Chinese", "\x5230\x8FBE\x5927\x5385", true },
        { "Latin with a combining accent", "Cafe\x0301 hall", true },
        { "an emoji", "Gate \U0001F6EB A", true },
        { "a right-to-left override (U+202E)", "Hall" + Override + "A", false },
        { "a right-to-left isolate (U+2067)", "Hall\x2067A", false },
        { "an Arabic letter mark (U+061C)", Arabic + "\x061C", false },
        { "a zero-width space (U+200B)", "Hall\x200BA", false },
        { "a zero-width non-joiner (U+200C)", "Hall\x200CA", false },
        { "a byte order mark (U+FEFF)", "\xFEFFHall", false },
        { "a tag character (U+E0041)", "Hall" + Tag + "A", false },
        { "the language tag (U+E0001)", "\U000E0001Hall", false },
        { "a musical formatting character (U+1D173)", "Hall\U0001D173A", false },
        { "a shorthand format control (U+1BCA0)", "Hall\U0001BCA0A", false },
        { "a bell", "Hall\x0007A", false },
        { "a tab", "Hall\tA", false },
        { "a line feed", "Hall\nA", false },
        { "a NUL", "Hall\0A", false },
        { "a blank name", "   ", false },
        { "201 characters", new string('n', 201), false },
        { "200 characters", new string('n', 200), true }
    };

    [Theory]
    [MemberData(nameof(Names))]
    public void IsName_Should_JudgeEveryCodePoint_When_ANameIsRead(string because, string name, bool valid) =>
        TopologyCodes.IsName(name).Should().Be(valid, because);

    [Fact]
    public void IsName_Should_RefuseALoneSurrogate_When_ItIsAnywhere()
    {
        // Built here: theory data is serialised, which would turn a lone surrogate into a replacement character.
        TopologyCodes.IsName("Hall" + '\xD83D').Should().BeFalse("a high surrogate at the end");
        TopologyCodes.IsName("Hall" + '\xDE00' + "A").Should().BeFalse("a low surrogate alone");
        TopologyCodes.IsName("Hall" + '\xD83D' + '\xDE00').Should().BeTrue("the pair is one code point");
        TopologyCodes.IsName(null).Should().BeFalse();
    }

    [Fact]
    public void IsName_Should_BeTheDisplayTextRule_When_TheNameFitsTheBound()
    {
        // One rule, not a copy: within the bound, a name is clean exactly when it is a clean display text.
        foreach (var name in new[] { "Hall", Arabic, "Hall" + Override, "Hall" + Tag, "Hall\x200DA", "Line one\nLine two" })
            TopologyCodes.IsName(name).Should().Be(DisplayText.IsClean(name), name.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    #endregion

    #region Every entry point

    public static TheoryData<string> Unsafe => new() { "Hall" + Override + "A", "Hall" + Tag, "Hall\x200BA", "Hall\x0007" };

    [Theory]
    [MemberData(nameof(Unsafe))]
    public void Topology_Should_RefuseTheNameWithTheFixedText_When_AnyEntryPointIsGivenIt(string name)
    {
        var level = Level();
        var checkpoint = Immigration();
        var desk = checkpoint.AddDesk("D01", null, DeskKind.Desk, ["CIT"]);
        var profile = new ZoneProfile("DMO-T1", "Arrivals");
        var queue = profile.AddZone("Snake A", ZoneKind.Queue, level, Rect(10, 10, 24, 12));

        var attempts = new (string EntryPoint, Action Act)[]
        {
            ("airport", () => Airport(name)),
            ("airport update", () => Airport().Update("ODMO", name, "Asia/Amman")),
            ("terminal", () => Airport().AddTerminal("T2", name, "DMO-T2")),
            ("terminal rename", () => Terminal().Rename(name)),
            ("level", () => Terminal().AddLevel("L1", name, 1, 300, 120)),
            ("level update", () => level.Update(name, 0, 300, 120)),
            ("checkpoint", () => Level().AddCheckpoint("SEC", name, CheckpointKind.Security)),
            ("checkpoint rename", () => checkpoint.Rename(name)),
            ("desk", () => Immigration().AddDesk("D02", name, DeskKind.Desk, ["CIT"])),
            ("desk rename", () => desk.Rename(name)),
            ("zone profile", () => _ = new ZoneProfile("DMO-T1", name)),
            ("zone profile rename", () => profile.Rename(name)),
            ("zone", () => profile.AddZone(name, ZoneKind.Queue, level, Rect(40, 10, 24, 12))),
            ("zone rename", () => profile.RenameZone(queue, name)),
            ("line", () => profile.AddLine(name, LineRole.Entry, level, new FloorPoint(10, 12), new FloorPoint(10, 16), queue)),
            ("site", () => _ = new Site("DMO", name)),
            ("site rename", () => new Site("DMO", "Demo").Rename(name)),
            ("illustrative site", () => Site.CreateIllustrative("DMO", name))
        };

        foreach (var (entryPoint, act) in attempts)
            act.Should().Throw<ArgumentException>(entryPoint).Which.Message.Should().StartWith(TopologyCodes.UnsafeNameMessage, entryPoint);
        profile.Name.Should().Be("Arrivals", "a refused rename changes nothing");
        queue.Name.Should().Be("Snake A");
    }

    [Theory]
    [InlineData(Arabic)]
    [InlineData(ArabicWithMarks)]
    [InlineData("Gate \U0001F6EB A")]
    public void Topology_Should_TakeTheName_When_ItIsInAnotherScriptOrHasAnEmoji(string name)
    {
        var level = Level();
        var profile = new ZoneProfile("DMO-T1", name);
        var queue = profile.AddZone(name, ZoneKind.Queue, level, Rect(10, 10, 24, 12));

        Airport(name).Name.Should().Be(name);
        Airport().AddTerminal("T2", name, "DMO-T2").Name.Should().Be(name);
        Terminal().AddLevel("L1", name, 1, 300, 120).Name.Should().Be(name);
        Level().AddCheckpoint("SEC", name, CheckpointKind.Security).Name.Should().Be(name);
        Immigration().AddDesk("D02", name, DeskKind.Desk, ["CIT"]).Name.Should().Be(name);
        profile.Name.Should().Be(name);
        queue.Name.Should().Be(name);
        profile.AddLine(name, LineRole.Entry, level, new FloorPoint(10, 12), new FloorPoint(10, 16), queue).Name.Should().Be(name);
        new Site("DMO", " " + name + " ").Name.Should().Be(name, "trimmed as before");
    }

    #endregion
}
