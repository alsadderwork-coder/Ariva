using Ariva.Core;
using Ariva.Core.Domain.Components;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Integration;
using Ariva.Core.Security;
using Ariva.Core.Sensing;
using Ariva.Infra.Integration;
using Ariva.Infra.Security;
using Ariva.Infra.Services.Administration;
using FluentAssertions;

namespace Ariva.UnitTests.Domain;

/// <summary>
/// ARV-158 (CWE-79, CWE-501): display texts are judged per code point, so the invisible format characters outside the
/// basic plane (tag characters, shorthand format controls, musical formatting) are refused like those inside it, while an
/// emoji is still taken; a lone surrogate is refused; line breaks only where a text allows them. Each kind of entity is
/// shown to refuse a tag character through its own entry point, and so are device events and alert notes (ARV-159). Every
/// invisible character here is an escape.
/// </summary>
public sealed class DisplayTextTests
{
    private const string Tag = "\U000E0041";

    public static TheoryData<string, string, bool> Texts => new()
    {
        { "a tag character inside a text (U+E0041)", "Gate\U000E0041A", false },
        { "the language tag (U+E0001)", "\U000E0001Gate A", false },
        { "a musical formatting character (U+1D173)", "Gate\U0001D173A", false },
        { "a shorthand format control (U+1BCA0)", "Gate\U0001BCA0A", false },
        { "an emoji", "Gate \U0001F6EB A", true },
        { "Arabic and Latin", "Gate A \x0628\x0648\x0627\x0628\x0629", true },
        { "a right-to-left override (U+202E)", "Gate\x202EA", false },
        { "a zero-width space (U+200B)", "Gate\x200BA", false },
        { "a bell", "Gate\x0007A", false },
        { "a delete", "Gate\x007FA", false },
        { "a tab", "Gate\tA", false },
        { "a line feed", "Gate\nA", false },
        { "plain text", "Gate A", true }
    };

    [Theory]
    [MemberData(nameof(Texts))]
    public void IsClean_Should_JudgeEveryCodePoint_When_TextIsRead(string because, string text, bool clean) =>
        DisplayText.IsClean(text).Should().Be(clean, because);

    #region Subdivision flags (ARV-169)

    // The three RGI subdivision flags: U+1F3F4, the tags for gb and the subdivision, the cancel tag U+E007F.
    private const string England = "\U0001F3F4\U000E0067\U000E0062\U000E0065\U000E006E\U000E0067\U000E007F";
    private const string Scotland = "\U0001F3F4\U000E0067\U000E0062\U000E0073\U000E0063\U000E0074\U000E007F";
    private const string Wales = "\U0001F3F4\U000E0067\U000E0062\U000E0077\U000E006C\U000E0073\U000E007F";

    public static TheoryData<string, string, bool> Flags => new()
    {
        { "England alone", England, true },
        { "Scotland inside a text", "Team " + Scotland + " A", true },
        { "Wales at the end", "Cardiff " + Wales, true },
        { "the three together", England + Scotland + Wales, true },
        { "the waving black flag alone", "\U0001F3F4 Gate", true },
        { "a subdivision outside the three (US California)", "\U0001F3F4\U000E0075\U000E0073\U000E0063\U000E0061\U000E007F", false },
        { "a malformed subdivision (gbxyz)", "\U0001F3F4\U000E0067\U000E0062\U000E0078\U000E0079\U000E007A\U000E007F", false },
        { "upper case tags", "\U0001F3F4\U000E0047\U000E0042\U000E0045\U000E004E\U000E0047\U000E007F", false },
        { "a sequence without its cancel tag", England[..^2], false },
        { "a truncated sequence", England[..6] + "\U000E007F", false },
        { "a tag after a flag", England + Tag, false },
        { "a cancel tag after a flag", England + "\U000E007F", false },
        { "the flag's tags without the waving flag", "Gate" + England[2..], false },
        { "a cancel tag alone", "Gate\U000E007F", false },
        { "a flag broken by a space", "\U0001F3F4 \U000E0067\U000E0062\U000E0065\U000E006E\U000E0067\U000E007F", false },
        { "a flag broken by a variation selector", "\U0001F3F4\uFE0F" + England[2..], false },
        { "a flag broken by a zero-width joiner", "\U0001F3F4\u200D" + England[2..], false },
        { "an unterminated sequence before a whole flag", England[..^2] + England, false },
        { "the waving flag before a whole flag", "\U0001F3F4" + England, true }
    };

    [Theory]
    [MemberData(nameof(Flags))]
    public void IsClean_Should_TakeOnlyTheThreeSubdivisionFlagsWhole_When_TagsAppear(string because, string text, bool clean) =>
        DisplayText.IsClean(text).Should().Be(clean, because);

    [Fact]
    public void Offenders_Should_ReportNothingForTheFlags_And_TheTagsAroundThem_When_Read()
    {
        DisplayText.Offenders(England + " " + Scotland + " " + Wales).Should().BeEmpty();
        DisplayText.Offenders(England + Tag).Should().Equal((8, 0xE0041));
        DisplayText.Offenders(England + Scotland + Tag).Should().Equal((15, 0xE0041));
        DisplayText.Offenders(Tag + England + Tag).Should().Equal((1, 0xE0041), (9, 0xE0041));
        DisplayText.Offenders("A" + England[2..]).Should().HaveCount(6, "every tag of the sequence, and its cancel tag, without the waving flag");
        DisplayText.Require(" " + Wales + " Cardiff ", 50, "name").Should().Be(Wales + " Cardiff");
    }

    [Fact]
    public void IsClean_Should_RefuseALoneSurrogateInsideAFlag_When_Read()
    {
        // Built here: theory data is serialised, which would turn a lone surrogate into a replacement character.
        DisplayText.IsClean(England[..4] + '\xDB40' + England[4..]).Should().BeFalse("a high surrogate alone inside the sequence");
        DisplayText.IsClean(England + '\xDB40').Should().BeFalse("a high surrogate alone right after a flag");
        DisplayText.IsClean('\xD83C' + England).Should().BeFalse("a high surrogate alone before a flag");
    }

    [Fact]
    public void Names_Should_TakeTheSubdivisionFlags_When_ASiteOrTopologyIsNamed()
    {
        TopologyCodes.IsName("Gate A " + England).Should().BeTrue();
        TopologyCodes.IsName("Gate A " + England + Tag).Should().BeFalse("a tag beyond the flag");
        FluentActions.Invoking(() => Site.CreateIllustrative("DMO", "Cardiff " + Wales)).Should().NotThrow();
    }

    #endregion

    [Fact]
    public void IsClean_Should_RefuseALoneSurrogate_When_ItIsAnywhere()
    {
        // Built here: theory data is serialised, which would turn a lone surrogate into a replacement character.
        DisplayText.IsClean("Gate" + '\xD83D').Should().BeFalse("a high surrogate at the end");
        DisplayText.IsClean("Gate" + '\xD83D' + "A").Should().BeFalse("a high surrogate before a letter");
        DisplayText.IsClean("Gate" + '\xDE00' + "A").Should().BeFalse("a low surrogate alone");
        DisplayText.IsClean("Gate" + '\xD83D' + '\xDE00').Should().BeTrue("the pair is one code point");
    }

    [Fact]
    public void IsClean_Should_TakeLineBreaksOnlyWhereAllowed_When_TheTextIsANote()
    {
        DisplayText.IsClean("line one\nline two\r\nthree").Should().BeFalse();
        DisplayText.IsClean("line one\nline two\r\nthree", allowLineBreaks: true).Should().BeTrue();
        DisplayText.IsClean("line one\n\U000E0041", allowLineBreaks: true).Should().BeFalse("a line break allows nothing else");
        DisplayText.IsClean("line one\ttwo", allowLineBreaks: true).Should().BeFalse("a tab is no line break");
        DisplayText.IsClean(null).Should().BeTrue();
    }

    [Fact]
    public void Offenders_Should_ListEveryCodePointThatBreaksTheRuleWithItsPosition_When_ATextIsJudged()
    {
        // ARV-162: positions in code points from 1, so an emoji or a tag character before an offender counts once.
        DisplayText.Offenders("Gate A").Should().BeEmpty();
        DisplayText.Offenders("\U0001F6EB Gate\x202EA\U000E0041").Should().Equal((7, 0x202E), (9, 0xE0041));
        DisplayText.Offenders("line\none\x0007", allowLineBreaks: true).Should().Equal((9, 0x0007));
        DisplayText.Offenders("line\none").Should().Equal((5, 0x000A));
        DisplayText.Offenders("A" + '\xDE00' + "B" + '\xD83D').Should().Equal((2, 0xDE00), (4, 0xD83D));
        DisplayText.Offenders(null).Should().BeEmpty();
        foreach (var text in new[] { "Gate A", "Gate\x202EA", "A\U000E0041", "a\nb" })
            DisplayText.IsClean(text).Should().Be(DisplayText.Offenders(text).Count == 0, "one walk decides both");
    }

    #region One consumer per kind of entity

    [Fact]
    public void AlertRule_Should_RefuseATagCharacter_When_InItsNameZoneOrContact()
    {
        AlertRuleValues Rule(string name = "Long wait", string zone = "Snake A", string contact = "Duty officer") =>
            new(name, [zone], AlertMetric.Nowcast, AlertComparator.GreaterThan, 20, null, null, 5, 5, AlertSeverity.Warning, RoleCodes.BorderShiftSupervisor, 15, null,
                contact, false, true);

        Rule().Problems().Should().BeEmpty();
        Rule(name: "Long" + Tag).Problems().Should().NotBeEmpty("the name");
        Rule(zone: "Snake" + Tag).Problems().Should().NotBeEmpty("a zone");
        Rule(contact: "Duty" + Tag).Problems().Should().NotBeEmpty("the escalation contact");
        var create = () => new AlertRule("DMO", 1, Rule(name: "Long" + Tag));
        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Display_Should_RefuseATagCharacter_When_InItsNameLabelOrMessage()
    {
        DisplayValues Board(string name = "Hall", string label = "Citizens", string message = "Ask staff") =>
            new(name, null, DisplayOrientation.Landscape, ["en"], 5, 1, 300, [new DisplayEntry("Snake A", new Dictionary<string, string> { ["en"] = label })],
                new Dictionary<string, string> { ["en"] = message }, true);

        Board().Problems().Should().BeEmpty();
        Board(name: "Hall" + Tag).Problems().Should().NotBeEmpty("the name");
        Board(label: "Citizens" + Tag).Problems().Should().NotBeEmpty("a label");
        Board(message: "Ask" + Tag).Problems().Should().NotBeEmpty("the stale message");
        var create = () => new Display("DMO", "ARR-A", Board(name: "Hall" + Tag));
        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Device_Should_RefuseATagCharacter_When_InItsModelOrQueueZoneName()
    {
        var level = new Airport("AAA", null, "Airport", "Asia/Dubai").AddTerminal("T1", "T1", "DMO").AddLevel("L0", "Arrivals", 0, 100, 50);
        Device Register(string model = "PC2SE", string zone = "Snake A") => new("S-01", DeviceFamily.StereoVision, model, DeviceTransport.HttpsPush, DeviceDialect.Xovis,
            ClockSource.Ntp, level, 20, 16, 5, 0, Ariva.Core.Domain.Components.CoverageFootprint.Assumed(DeviceFamily.StereoVision, 5), zone, new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc));

        Register().Model.Should().Be("PC2SE");
        var model = () => Register(model: "PC2" + Tag);
        var zone = () => Register(zone: "Snake" + Tag);
        model.Should().Throw<ArgumentException>("the model");
        zone.Should().Throw<ArgumentException>("the queue zone name");
    }

    [Fact]
    public void User_Should_RefuseATagCharacter_When_InTheDisplayName()
    {
        SvcUsers.IsUsableDisplayName("Amal Haddad").Should().BeTrue();
        SvcUsers.IsUsableDisplayName("Amal" + Tag + " Haddad").Should().BeFalse();
        SvcUsers.IsUsableDisplayName("Amal \U0001F6EB").Should().BeTrue("an emoji is no format character");
    }

    [Fact]
    public void User_Should_TakeTheSubdivisionFlags_And_JudgeTheJoinersWhereTheyStand_When_InTheDisplayName()
    {
        // ARV-169: the name is judged as stored. Removing the joiners first let a joiner split a flag's tags through.
        SvcUsers.IsUsableDisplayName("Amal " + England).Should().BeTrue();
        SvcUsers.IsUsableDisplayName("Amal \U0001F3F4\u200D" + England[2..]).Should().BeFalse("a joiner between the waving flag and its tags");
        SvcUsers.IsUsableDisplayName("Amal " + England[..6] + "\u200C" + England[6..]).Should().BeFalse("a non-joiner between the tags");
        SvcUsers.IsUsableDisplayName("\u0645\u06CC\u200C\u062E\u0648\u0627\u0647\u0645").Should().BeTrue("a Persian name keeps its non-joiner");
        SvcUsers.IsUsableDisplayName("Amal " + '\xD83C' + "\u200D" + '\xDFF4').Should().BeFalse("a surrogate pair split by a joiner is two lone surrogates");
        DisplayText.IsClean("A\u200DB").Should().BeFalse("only a person's name takes the joiners");
        DisplayText.IsClean("A\u200DB", allowJoiners: true).Should().BeTrue();
    }

    [Fact]
    public void IntegrationClient_Should_RefuseATagCharacter_When_Named()
    {
        var (access, errors) = IntegrationClientAccess.Check([IntegrationScopes.FlightsWrite], ["DMO"], ["10.20.0.0/24"], false);
        errors.Should().BeEmpty();
        IntegrationClient Client(string name) => new(IntegrationCredentials.NewClientId(), name, IntegrationClientKind.Aodb, access,
            new PasswordHashValue("pbkdf2-sha256", 600_000, "c2FsdA==", "aGFzaA=="), "protected-seed", new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc));

        Client("AODB of DMO").Name.Should().Be("AODB of DMO");
        var tagged = () => Client("AODB" + Tag);
        tagged.Should().Throw<ArgumentException>();
        var renamed = () => Client("AODB of DMO").Rename("AODB" + Tag);
        renamed.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ReportSchedule_Should_RefuseATagCharacter_When_Named()
    {
        ReportSchedule Schedule(string name) => new("DMO", Guid.CreateVersion7(), name, ReportTemplate.DailyPeaks, "06:00", true);

        Schedule("Morning peaks").Name.Should().Be("Morning peaks");
        var tagged = () => Schedule("Morning" + Tag);
        tagged.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CanonicalEventRules_Should_RefuseATagCharacter_When_InALineOrZoneNameADeviceSends()
    {
        // ARV-159 (from the ARV-158 review): device input is the real trust boundary; Ingest refuses the event before Kafka.
        var at = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);

        CanonicalEventRules.Validate(new LineCrossing("A-VIS entry", CrossingDirection.In, "7", at)).Should().BeEmpty();
        CanonicalEventRules.Validate(new LineCrossing("A-VIS entry" + Tag, CrossingDirection.In, "7", at)).Should().ContainSingle().Which.Should().Contain("line name");
        CanonicalEventRules.Validate(new IntervalCount("A-VIS" + Tag + " entry", 30, 2, at.AddMinutes(-5), at)).Should().ContainSingle().Which.Should().Contain("line name");
        CanonicalEventRules.Validate(new ZoneOccupancy("A-VIS" + Tag, 41, at)).Should().ContainSingle().Which.Should().Contain("zone name");
    }

    [Fact]
    public void Alert_Should_RefuseATagCharacter_When_InANoteThatAllowsLineBreaks()
    {
        // ARV-159 (from the ARV-158 review): notes take line breaks, and nothing invisible with them.
        Alert.IsValidNote("Desks 4 and 5 opened.\nQueue back under 20 minutes.", required: true).Should().BeTrue();
        Alert.IsValidNote("Desks opened.\n" + Tag + "Queue back.", required: true).Should().BeFalse();
        Alert.IsValidNote("Desks opened." + Tag, required: false).Should().BeFalse();
        Alert.IsValidNote("Desks opened.\x202E\nQueue back.", required: true).Should().BeFalse();
    }

    #endregion
}
