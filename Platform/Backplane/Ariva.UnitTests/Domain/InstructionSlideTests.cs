using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using FluentAssertions;

namespace Ariva.UnitTests.Domain;

/// <summary>
/// ARV-141: an instruction slide's settings are checked values (a scope with its targets, a built-in icon, 5 to 60 seconds,
/// a title and an optional body in at least one of the board's languages, plain text without control or invisible
/// characters); a display shows the slides of its site that apply to it, in the site's order, in its own languages, and pins
/// the first pinned one under the waits.
/// </summary>
public sealed class InstructionSlideTests
{
    private static Dictionary<string, InstructionText> Words(params (string Language, string Title, string Body)[] values) =>
        values.ToDictionary(v => v.Language, v => new InstructionText(v.Title, v.Body));

    private static InstructionSlideValues Values(
        InstructionScope scope = InstructionScope.Site,
        string[] targets = null,
        InstructionIcon icon = InstructionIcon.Passport,
        int duration = 10,
        bool pinned = false,
        Dictionary<string, InstructionText> texts = null) =>
        new(scope, targets ?? [], icon, duration, pinned,
            texts ?? Words(("en", "Have your passport open at the photo page", null), ("ar", "افتح جواز سفرك على صفحة الصورة", "عند الكاونتر")));

    private static Display Board(string code = "ARR-A", string site = "DMO", string[] languages = null, params string[] zones) =>
        new(site, code, new DisplayValues("Arrivals", null, DisplayOrientation.Landscape, languages ?? ["ar", "en"], 5, 1, 150,
            [.. (zones.Length == 0 ? ["A-CIT"] : zones).Select(z => new DisplayEntry(z, (languages ?? ["ar", "en"]).ToDictionary(l => l, _ => z)))],
            (languages ?? ["ar", "en"]).ToDictionary(l => l, _ => "Please follow the signs"), true));

    [Fact]
    public void Values_Should_BeKeptAndReadBack_When_TheyAreValid()
    {
        Values().Problems().Should().BeEmpty();
        var slide = new InstructionSlide("DMO", 1, Values(InstructionScope.Zone, ["A-CIT", " A-RES "]));
        slide.TargetList.Should().Equal("A-CIT", "A-RES");
        slide.TextMap["ar"].Should().Be(new InstructionText("افتح جواز سفرك على صفحة الصورة", "عند الكاونتر"));
        slide.TextMap["en"].Body.Should().BeNull("a blank body is no body");
        slide.Status.Should().Be(InstructionStatus.Active);
        slide.Values().Problems().Should().BeEmpty("what is stored reads back as valid values");
        slide.AuditSummary().Should().Contain("A-RES").And.Contain("Passport").And.Contain("افتح جواز سفرك");
    }

    public static TheoryData<string, InstructionSlideValues> Refused => new()
    {
        { "an undefined scope", Values(scope: (InstructionScope)7) },
        { "a site slide naming a zone", Values(targets: ["A-CIT"]) },
        { "a zone slide naming nothing", Values(InstructionScope.Zone) },
        { "a display slide naming nothing", Values(InstructionScope.Display) },
        { "21 zones", Values(InstructionScope.Zone, [.. Enumerable.Range(1, 21).Select(i => $"Z{i}")]) },
        { "a zone twice", Values(InstructionScope.Zone, ["A-CIT", "A-CIT"]) },
        { "a blank zone", Values(InstructionScope.Zone, [" "]) },
        { "a display code in lower case", Values(InstructionScope.Display, ["arr-a"]) },
        { "an undefined icon", Values(icon: (InstructionIcon)99) },
        { "4 seconds", Values(duration: 4) },
        { "61 seconds", Values(duration: 61) },
        { "no words", Values(texts: []) },
        { "an unknown language", Values(texts: Words(("fr", "Passeport", null))) },
        { "a blank title", Values(texts: Words(("en", " ", "Body"))) },
        { "a title too long", Values(texts: Words(("en", new string('x', 81), null))) },
        { "a body too long", Values(texts: Words(("en", "Title", new string('x', 241)))) },
        { "a line break in a title", Values(texts: Words(("en", "Two\nlines", null))) },
        { "a line break in a body", Values(texts: Words(("en", "Title", "Two\r\nlines"))) },
        { "a control character", Values(texts: Words(("en", "Bell\u0007", null))) },
        { "a bidirectional override", Values(texts: Words(("ar", "\u202Eجواز", null))) },
        { "a zero width joiner", Values(texts: Words(("en", "Pass\u200Dport", null))) }
    };

    [Fact]
    public void Values_Should_BeRefused_When_TheAuditTrailCouldNotKeepThemWhole()
    {
        // Characters outside the basic plane are kept escaped (12 for 2): words at their limits in emoji fit the stored room
        // but not one audit entry, so they are refused rather than audited in part (security review of ARV-141).
        var values = Values(texts: Words(("en", Emoji(80), Emoji(240)), ("ar", Emoji(80), Emoji(240)), ("pt", Emoji(80), Emoji(240)), ("sw", Emoji(80), Emoji(240))));
        values.Problems().Should().Equal(InstructionSlideValues.TooLongToAudit);
    }

    [Fact]
    public void AuditSummary_Should_KeepEveryWord_When_ASlideIsAtItsLimitsInPlainText()
    {
        // Every language at its longest, with quotes (escaped in JSON), and 20 zones: the whole slide fits one audit entry.
        var title = new string('"', 10) + new string('x', 70);
        var body = new string('y', 240);
        var values = Values(InstructionScope.Zone, [.. Enumerable.Range(1, 20).Select(i => $"A-ZONE-{i:00}")], InstructionIcon.HatAndGlasses, 60, true,
            Words(("en", title, body), ("ar", new string('ب', 80), new string('ج', 240)), ("pt", title, body), ("sw", title, body)));
        values.Problems().Should().BeEmpty();
        var slide = new InstructionSlide("AUH-TA", 999, values);
        slide.Retire();
        slide.AuditSummary().Length.Should().BeLessThanOrEqualTo(AuditEntry.SummaryLength);
        slide.AuditSummary().Should().Contain(new string('ج', 240), "the last language's words are kept whole");
    }

    [Fact]
    public void Problems_Should_NeverRepeatALanguageKeyTheCallerChose_When_ItIsRefused()
    {
        var problems = Values(texts: Words(("<script>alert(1)</script>", "Bell\u0007", null), ("en", "Fine", null))).Problems();
        problems.Should().NotBeEmpty();
        string.Concat(problems).Should().NotContain("<script>").And.NotContain("alert(1)");
    }

    private static string Emoji(int count) => string.Concat(Enumerable.Repeat("\U0001F6C2", count / 2));

    [Theory]
    [MemberData(nameof(Refused))]
    public void Values_Should_BeRefused_When_TheyBreakARule(string reason, InstructionSlideValues values)
    {
        values.Problems().Should().NotBeEmpty(reason);
        var create = () => new InstructionSlide("DMO", 1, values);
        create.Should().Throw<ArgumentException>(reason);
    }

    [Fact]
    public void Markup_Should_BeKeptAsText_When_ItIsInTheWords()
    {
        // Markup is ordinary text here: the players show it as text, never parse it (CWE-79, the player's tests prove it).
        var markup = "<script>alert(1)</script>";
        var slide = new InstructionSlide("DMO", 1, Values(texts: Words(("en", markup, "\"><img src=x onerror=alert(1)>"))));
        slide.TextMap["en"].Title.Should().Be(markup);
        slide.Texts.Should().Contain("<script>", "stored as JSON text, not escaped into something else");
    }

    [Fact]
    public void Status_Should_FollowRetireAndRestore()
    {
        var slide = new InstructionSlide("DMO", 1, Values());
        slide.Retire();
        slide.Status.Should().Be(InstructionStatus.Retired);
        slide.AppliesTo("DMO", "ARR-A", ["A-CIT"]).Should().BeFalse("a retired slide is shown nowhere");
        slide.Restore();
        slide.AppliesTo("DMO", "ARR-A", ["A-CIT"]).Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1001)]
    public void Position_Should_BeRefused_When_ItIsOutOfRange(int position)
    {
        var create = () => new InstructionSlide("DMO", position, Values());
        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ForDisplay_Should_ShowTheSlidesThatApply_When_ScopesDiffer()
    {
        var site = new InstructionSlide("DMO", 1, Values());
        var zone = new InstructionSlide("DMO", 2, Values(InstructionScope.Zone, ["A-VIS", "A-RES"]));
        var otherZone = new InstructionSlide("DMO", 3, Values(InstructionScope.Zone, ["D-CIT"]));
        var display = new InstructionSlide("DMO", 4, Values(InstructionScope.Display, ["ARR-A"]));
        var otherDisplay = new InstructionSlide("DMO", 5, Values(InstructionScope.Display, ["ARR-B"]));
        var otherSite = new InstructionSlide("AUH-TA", 1, Values());
        var retired = new InstructionSlide("DMO", 6, Values());
        retired.Retire();

        var (pinned, rotation) = InstructionSlides.ForDisplay([display, otherSite, zone, retired, otherZone, site, otherDisplay], Board("ARR-A", "DMO", null, "A-CIT", "A-RES"));

        pinned.Should().BeNull();
        rotation.Select(s => s.Slide).Should().Equal([site, zone, display], "the site's order, only what applies to this display");
    }

    [Fact]
    public void ForDisplay_Should_UseOnlyTheDisplaysLanguagesInItsOrder_When_ASlideHasMore()
    {
        var all = new InstructionSlide("DMO", 1, Values(texts: Words(("en", "Passport", null), ("ar", "جواز", null), ("pt", "Passaporte", null))));
        var swahiliOnly = new InstructionSlide("DMO", 2, Values(texts: Words(("sw", "Pasipoti", null))));

        var (_, rotation) = InstructionSlides.ForDisplay([all, swahiliOnly], Board("ARR-A", "DMO", ["pt", "ar"]));

        rotation.Should().ContainSingle("a slide with none of the display's languages is not shown").Which.Texts.Select(t => t.Language).Should().Equal("pt", "ar");
    }

    [Fact]
    public void ForDisplay_Should_PinTheFirstPinnedSlide_When_SeveralApply()
    {
        var first = new InstructionSlide("DMO", 1, Values());
        var pinnedFirst = new InstructionSlide("DMO", 2, Values(pinned: true));
        var pinnedSecond = new InstructionSlide("DMO", 3, Values(pinned: true));

        var (pinned, rotation) = InstructionSlides.ForDisplay([pinnedSecond, first, pinnedFirst], Board());

        pinned.Slide.Should().BeSameAs(pinnedFirst);
        rotation.Select(s => s.Slide).Should().Equal([first, pinnedSecond], "any other pinned slide rotates with the rest");
    }
}
