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
