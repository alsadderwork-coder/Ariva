using Ariva.Core.Domain.Entities;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Services.Topology;
using FluentAssertions;

namespace Ariva.UnitTests.Storage;

/// <summary>
/// ARV-153 (CWE-120, CWE-79): a floor plan's file name is bounded at 200 code points, as PostgreSQL counts the
/// varchar(200) column, and refused beyond it with a fixed text, never cut and never left to fail the insert. Combining
/// marks count one each, a surrogate pair once; the path, control and lone surrogate checks stand as they were, and
/// control and format characters are now judged by code point, so invisible ones outside the basic plane (tag
/// characters, shorthand format controls, musical formatting) are refused like a right-to-left override.
/// </summary>
public sealed class FloorPlanFileNameTests
{
    private const string Acute = "\u0301";
    private const string Plane = "\U0001F6EB";

    private static string Repeat(string text, int times) => string.Concat(Enumerable.Repeat(text, times));

    public static TheoryData<string, string, string> Names => new()
    {
        { "200 letters", new string('a', 200), null },
        { "201 letters", new string('a', 201), TopologyErrors.FileNameTooLong },
        { "a letter and 199 combining marks (200 code points, one text element)", "a" + Repeat(Acute, 199), null },
        { "a letter and 200 combining marks (201 code points)", "a" + Repeat(Acute, 200), TopologyErrors.FileNameTooLong },
        { "a letter and 300 combining marks", "a" + Repeat(Acute, 300), TopologyErrors.FileNameTooLong },
        { "200 text elements, 201 code points", new string('a', 199) + "e" + Acute, TopologyErrors.FileNameTooLong },
        { "199 letters and an emoji at the bound (200 code points, 201 UTF-16 units)", new string('a', 199) + Plane, null },
        { "200 letters and an emoji (201 code points)", new string('a', 200) + Plane, TopologyErrors.FileNameTooLong },
        { "200 emoji (400 UTF-16 units)", Repeat(Plane, 200), null },
        { "201 emoji", Repeat(Plane, 201), TopologyErrors.FileNameTooLong },
        { "a right-to-left override", "plan\u202Egvs.png", TopologyErrors.UnsafeFileName },
        { "a right-to-left override in a name over the bound", new string('a', 250) + "\u202E", TopologyErrors.UnsafeFileName },
        { "a path", "../../etc/passwd.png", TopologyErrors.UnsafeFileName },
        { "a tag character inside a name (U+E0041)", "plan\U000E0041.png", TopologyErrors.UnsafeFileName },
        { "the language tag (U+E0001)", "\U000E0001plan.png", TopologyErrors.UnsafeFileName },
        { "a musical formatting character (U+1D173)", "plan\U0001D173.png", TopologyErrors.UnsafeFileName },
        { "a shorthand format control (U+1BCA0)", "plan\U0001BCA0.png", TopologyErrors.UnsafeFileName },
        { "an emoji inside a name", "plan\U0001F6EB.png", null },
        { "a control character", "plan\u0007.png", TopologyErrors.UnsafeFileName },
        { "markup", "<img src=x onerror=alert(1)>.png", null }
    };

    [Theory]
    [MemberData(nameof(Names))]
    public void CheckFileName_Should_RefuseWhatTheColumnCannotHoldOrIsUnsafe_When_ANameIsUploaded(string because, string name, string expected)
    {
        var (kept, error) = SvcFloorPlans.CheckFileName(name);

        error.Should().Be(expected, because);
        if (expected is null)
        {
            kept.Should().Be(name, "a name that fits is kept whole, never cut");
            kept.EnumerateRunes().Count().Should().BeLessThanOrEqualTo(FloorPlan.MaxFileNameLength);
        }
        else
        {
            kept.Should().BeNull();
            SvcFloorPlans.SafeFileName(name).Should().BeNull("the site configuration import refuses it too");
        }
    }

    [Fact]
    public void CheckFileName_Should_RefuseALoneSurrogate_When_ItIsAnywhereInTheName()
    {
        // Built here: theory data is serialised, which would turn a lone surrogate into a replacement character.
        SvcFloorPlans.CheckFileName("plan" + '\uD83D' + ".png").Error.Should().Be(TopologyErrors.UnsafeFileName);
        SvcFloorPlans.CheckFileName(new string('a', 199) + '\uDE00').Error.Should().Be(TopologyErrors.UnsafeFileName, "a low surrogate alone at the bound");
    }

    [Fact]
    public void CheckFileName_Should_CountTheTrimmedNameAndNameABlankOne_When_Spaces()
    {
        SvcFloorPlans.CheckFileName("  " + new string('a', 200) + "  ").Should().Be((new string('a', 200), null));
        SvcFloorPlans.CheckFileName("   ").Should().Be(("floor-plan", null));
        SvcFloorPlans.CheckFileName(null).Should().Be(("floor-plan", null));
    }

    [Fact]
    public void FloorPlan_Should_RefuseANameTheColumnCannotHold_When_Created()
    {
        var level = new Airport("AAA", null, "Airport", "Asia/Dubai").AddTerminal("T1", "T1", "SITE").AddLevel("L0", "Arrivals", 0, 100, 50);
        FloorPlan Plan(string name) => new(level, "0199a0000000700080000000000000aa.png", "image/png", 10, new string('a', 64), name, 2, 2, 0.05, 0, 0);

        Plan("a" + Repeat(Acute, 199)).OriginalFileName.Should().HaveLength(200);
        var refused = () => Plan("a" + Repeat(Acute, 300));
        refused.Should().Throw<ArgumentException>().Which.Message.Should().StartWith(TopologyErrors.FileNameTooLong, "the last line before the column");
    }

    [Theory]
    [InlineData(200, true)]
    [InlineData(201, false)]
    public void IsFileName_Should_FollowTheSameBound_When_TheSiteConfigurationImportReadsAName(int marks, bool fits) =>
        SiteConfigurationRules.IsFileName("a" + Repeat(Acute, marks - 1)).Should().Be(fits);

    [Fact]
    public void IsSafeFileName_Should_BeTheUploadsRule_When_ANameIsStoredUploadedImportedOrExported()
    {
        // ARV-162 (review R1): one rule in Ariva.Core; the upload refuses exactly what it refuses, and a blank name passes
        // (the upload stores "floor-plan").
        foreach (var name in new[]
                 {
                     "arrivals.svg", "plan\U000E0041.svg", "plan\x202E.svg", "../etc/x.svg", "a\\b.svg", "c:x.svg", "a..b.svg", "plan\n.svg",
                     "\x0628\x0648\x0627\x0628\x0629.png", "a" + Repeat(Acute, 199), "a" + Repeat(Acute, 200), "  ", "plan" + '\xD83D' + ".png"
                 })
        {
            FloorPlan.IsSafeFileName(name).Should().Be(SvcFloorPlans.CheckFileName(name).Error is null, name.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        FloorPlan.IsSafeFileName(null).Should().BeTrue();
    }
}
