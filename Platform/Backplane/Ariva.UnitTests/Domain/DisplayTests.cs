using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using Ariva.Infra.Security;
using FluentAssertions;

namespace Ariva.UnitTests.Domain;

/// <summary>
/// ARV-058: a passenger display's settings are checked values (languages from the board's resource files, a label and a
/// neutral message in each, band, hysteresis below the band, stale threshold, at most 12 entries, each zone once); its
/// player credential is random, shown once and kept as a prefix and a hash, never in the audit summary.
/// </summary>
public sealed class DisplayTests
{
    private static Dictionary<string, string> Text(params (string Language, string Value)[] values) => values.ToDictionary(v => v.Language, v => v.Value);

    private static DisplayValues Values(
        string[] languages = null,
        int band = 5,
        double hysteresis = 1,
        int stale = 150,
        DisplayEntry[] entries = null,
        Dictionary<string, string> fallback = null) =>
        new("Arrivals hall A", "Above the passport control entrance", DisplayOrientation.Landscape, languages ?? ["ar", "en"], band, hysteresis, stale,
            entries ?? [new DisplayEntry("A-CIT", Text(("ar", "المواطنون"), ("en", "Citizens")))],
            fallback ?? Text(("ar", "يرجى اتباع اللافتات"), ("en", "Please follow the signs")), true);

    [Fact]
    public void Values_Should_BeAccepted_When_EveryLanguageHasItsWords()
    {
        Values().Problems().Should().BeEmpty();
        var display = new Display("DMO", "ARR-A", Values());
        display.LanguageList.Should().Equal("ar", "en");
        display.EntryList.Should().ContainSingle().Which.Labels["ar"].Should().Be("المواطنون");
        display.FallbackMap["en"].Should().Be("Please follow the signs");
        display.Values().Problems().Should().BeEmpty("what is stored reads back as valid values");
    }

    public static TheoryData<string, DisplayValues> Refused => new()
    {
        { "no language", Values(languages: []) },
        { "an unknown language", Values(languages: ["ar", "fr"]) },
        { "a language twice", Values(languages: ["en", "en"]) },
        { "a missing label", Values(languages: ["ar", "en", "pt"]) },
        { "a band of zero", Values(band: 0) },
        { "a band over 30", Values(band: 31) },
        { "hysteresis as wide as the band", Values(hysteresis: 5) },
        { "negative hysteresis", Values(hysteresis: -1) },
        { "stale too soon", Values(stale: 30) },
        { "no entry", Values(entries: []) },
        { "13 entries", Values(entries: [.. Enumerable.Range(1, 13).Select(i => new DisplayEntry($"Z{i}", Text(("ar", "س"), ("en", "x"))))]) },
        { "a zone twice", Values(entries: [new DisplayEntry("A-CIT", Text(("ar", "س"), ("en", "x"))), new DisplayEntry("A-CIT", Text(("ar", "س"), ("en", "y")))]) },
        { "a label in a language the display does not show", Values(entries: [new DisplayEntry("A-CIT", Text(("ar", "س"), ("en", "x"), ("pt", "y")))]) },
        { "an invisible character", Values(entries: [new DisplayEntry("A-CIT", Text(("ar", "س\u200F"), ("en", "x")))]) },
        { "a label too long", Values(entries: [new DisplayEntry("A-CIT", Text(("ar", "س"), ("en", new string('x', 81))))]) },
        { "a missing message", Values(fallback: Text(("en", "Please follow the signs"))) },
        { "labels in emoji past the stored room", Values(languages: Four, entries: [.. Enumerable.Range(1, 12).Select(i => new DisplayEntry($"Z{i}", All(Emoji(40))))], fallback: All("x")) },
        { "messages in emoji past the stored room", Values(languages: Four, entries: [new DisplayEntry("A-CIT", All("x"))], fallback: All(Emoji(100))) },
        { "a queue share under half the screen", Values() with { Layout = DisplayLayout.Split, QueueShare = 49 } },
        { "a queue share over three quarters", Values() with { Layout = DisplayLayout.Split, QueueShare = 76 } },
        { "a queue share out of range in a full layout too", Values() with { QueueShare = 0 } },
        { "an undefined layout", Values() with { Layout = (DisplayLayout)7 } },
        { "an undefined second region", Values() with { SecondRegion = (DisplaySecondRegion)7 } }
    };

    [Fact]
    public void Layout_Should_StayTheFullQueueView_When_ADisplayIsGivenNoLayout()
    {
        var display = new Display("DMO", "ARR-A", Values());
        display.Layout.Should().Be(DisplayLayout.Full, "displays made before ARV-140 keep today's view");
        display.QueueShare.Should().Be(DisplayValues.DefaultQueueShare);
        display.SecondRegion.Should().Be(DisplaySecondRegion.Instructions);
    }

    [Theory]
    [InlineData(DisplayValues.MinQueueShare)]
    [InlineData(DisplayValues.DefaultQueueShare)]
    [InlineData(DisplayValues.MaxQueueShare)]
    public void Layout_Should_BeKeptAndAudited_When_ADisplayIsSplit(int share)
    {
        var display = new Display("DMO", "ARR-A", Values());
        var before = display.AuditSummary();
        display.Set(Values() with { Orientation = DisplayOrientation.Portrait, Layout = DisplayLayout.Split, QueueShare = share });

        display.Layout.Should().Be(DisplayLayout.Split);
        display.QueueShare.Should().Be(share);
        display.Values().Should().Match<DisplayValues>(v => v.Layout == DisplayLayout.Split && v.QueueShare == share && v.SecondRegion == DisplaySecondRegion.Instructions,
            "the stored layout reads back");
        before.Should().Contain("\"layout\":\"Full\"");
        display.AuditSummary().Should().Contain("\"layout\":\"Split\"").And.Contain($"\"queueShare\":{share}").And.Contain("\"secondRegion\":\"Instructions\"",
            "the audit trail shows a layout change before and after");
    }

    [Fact]
    public void Advertising_Should_BeAValidSetting_When_OnlyTheSitesPolicyCanTellWhetherItIsShown()
    {
        // ARV-145: whether a display may advertise is its site's advertising policy (the display service, script 0057 and
        // the board check it); the value alone is a valid setting and reads back.
        var advertising = Values() with { Layout = DisplayLayout.Split, SecondRegion = DisplaySecondRegion.Advertising };
        advertising.Problems().Should().BeEmpty();
        new Display("DMO", "ARR-A", advertising).AuditSummary().Should().Contain("\"secondRegion\":\"Advertising\"");
    }

    private static readonly string[] Four = ["ar", "en", "pt", "sw"];

    /// <summary>Characters outside the basic plane: two UTF-16 units each, stored escaped as twelve.</summary>
    private static string Emoji(int count) => string.Concat(Enumerable.Repeat("\U0001F6C2", count));

    private static Dictionary<string, string> All(string text) => Four.ToDictionary(l => l, _ => text, StringComparer.Ordinal);

    [Fact]
    public void Values_Should_BeAccepted_When_AFullBoardIsWrittenInArabicAndLatinScripts()
    {
        var label = new string('س', Display.MaxLabelLength);
        var values = Values(languages: Four, entries: [.. Enumerable.Range(1, Display.MaxEntries).Select(i => new DisplayEntry($"Z{i}", All(label)))],
            fallback: All(new string('ل', Display.MaxMessageLength)));
        values.Problems().Should().BeEmpty("the stored room holds every label and message at full length in these scripts");
        new Display("DMO", "ARR-A", values).EntryList.Should().HaveCount(Display.MaxEntries);
    }

    [Fact]
    public void Values_Should_BeRefusedBeforeTheDatabase_When_EmojiOutgrowTheStoredRoom()
    {
        var withinEachLimit = Values(languages: Four, entries: [new DisplayEntry("A-CIT", All("x"))], fallback: All(Emoji(Display.MaxMessageLength / 2)));
        withinEachLimit.Problems().Should().ContainSingle().Which.Should().Contain("too long to store");
        Values(languages: Four, entries: [new DisplayEntry("A-CIT", All("x"))], fallback: All(Emoji(10))).Problems().Should().BeEmpty("a few symbols fit");
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public void Values_Should_BeRefused_When_TheyBreakARule(string why, DisplayValues values)
    {
        values.Problems().Should().NotBeEmpty(why);
        var create = () => new Display("DMO", "ARR-A", values);
        create.Should().Throw<ArgumentException>(why);
    }

    [Fact]
    public void Credential_Should_BeRandomAndKeptAsPrefixAndHash_When_Issued()
    {
        var first = DisplayCredentials.New();
        var second = DisplayCredentials.New();
        (first.Credential.StartsWith("ardp_", StringComparison.Ordinal) && first.Credential.Length == 48).Should().BeTrue("a display credential's shape");
        (first.Credential != second.Credential).Should().BeTrue("every credential is random");
        (first.Prefix == first.Credential[..13]).Should().BeTrue("the prefix is the credential's first 13 characters");
        DisplayCredentials.Matches(first.Credential, first.Hash).Should().BeTrue();
        DisplayCredentials.Matches(second.Credential, first.Hash).Should().BeFalse();
        DisplayCredentials.Matches(DeviceCredentials.New().Credential, first.Hash).Should().BeFalse("a device credential is not a display credential");
        DisplayCredentials.IsWellFormed(first.Credential[..47] + "!").Should().BeFalse();

        var display = new Display("DMO", "ARR-A", Values());
        display.SetCredential(first.Prefix, first.Hash, new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc));
        var summary = display.AuditSummary();
        summary.Contains(first.Credential, StringComparison.Ordinal).Should().BeFalse("the audit summary holds the prefix, never the credential");
        summary.Should().Contain(first.Prefix).And.NotContain(first.Hash);
    }
}
