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
        { "an invisible character", Values(entries: [new DisplayEntry("A-CIT", Text(("ar", "س‏"), ("en", "x")))]) },
        { "a label too long", Values(entries: [new DisplayEntry("A-CIT", Text(("ar", "س"), ("en", new string('x', 81))))]) },
        { "a missing message", Values(fallback: Text(("en", "Please follow the signs"))) },
        { "labels in emoji past the stored room", Values(languages: Four, entries: [.. Enumerable.Range(1, 12).Select(i => new DisplayEntry($"Z{i}", All(Emoji(40))))], fallback: All("x")) },
        { "messages in emoji past the stored room", Values(languages: Four, entries: [new DisplayEntry("A-CIT", All("x"))], fallback: All(Emoji(100))) }
    };

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
        first.Credential.Should().StartWith("ardp_").And.HaveLength(48).And.NotBe(second.Credential);
        first.Prefix.Should().Be(first.Credential[..13]);
        DisplayCredentials.Matches(first.Credential, first.Hash).Should().BeTrue();
        DisplayCredentials.Matches(second.Credential, first.Hash).Should().BeFalse();
        DisplayCredentials.Matches(DeviceCredentials.New().Credential, first.Hash).Should().BeFalse("a device credential is not a display credential");
        DisplayCredentials.IsWellFormed(first.Credential[..47] + "!").Should().BeFalse();

        var display = new Display("DMO", "ARR-A", Values());
        display.SetCredential(first.Prefix, first.Hash, new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc));
        display.AuditSummary().Should().Contain(first.Prefix).And.NotContain(first.Credential).And.NotContain(first.Hash);
    }
}
