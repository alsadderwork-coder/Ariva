using System.Text.RegularExpressions;
using Ariva.Core.Domain.Constants;
using Ariva.UnitTests.Setup;
using FluentAssertions;

namespace Ariva.UnitTests.Domain.Validation;

/// <summary>
/// ARV-104c (security review L2): the observer tablet recognises refusals of the capture API by a fragment of their text, since
/// the API sends no error code for them: a device clock too far off (<see cref="ValidationErrors.ClockOffsetTooLarge"/>, shown as
/// "set the tablet's time to automatic"), a key sent again with another body (<see cref="ValidationErrors.KeyReused"/>, a
/// correction that was not saved) and, since ARV-104d, desk states refused to an account with an airport role
/// (<see cref="ValidationErrors.DeskObservationsNeedBorderRole"/>) and the campaign's own creator or starter
/// (<see cref="ValidationErrors.OwnCampaign"/>); any other 403 is shown with the server's text (security review L1). The
/// fragments live in Ariva.Web's <c>src/lib/core/validation.ts</c>; this test reads them from that file and fails when a constant
/// is reworded so that the tablet would no longer recognise it.
/// </summary>
public sealed partial class ValidationErrorTextTests
{
    #region Fixtures

    private const string WebFile = "Platform/Frontplane/Ariva.Web/src/lib/core/validation.ts";

    [GeneratedRegex("""export const (?<name>clockRefusalText|keyReusedText|deskRoleText|ownCampaignText) = (?<quote>['"])(?<text>.+?)\k<quote>;""")]
    private static partial Regex Fragment();

    private static IReadOnlyDictionary<string, string> Fragments() =>
        Fragment().Matches(File.ReadAllText(RepositoryPaths.Resolve(WebFile)))
            .ToDictionary(m => m.Groups["name"].Value, m => m.Groups["text"].Value);

    #endregion

    #region Tests

    [Fact]
    public void WebFragments_Should_BeContainedInTheirConstants_When_TheTabletMatchesTheServerText()
    {
        var fragments = Fragments();

        fragments.Should().ContainKeys("clockRefusalText", "keyReusedText", "deskRoleText", "ownCampaignText");
        ValidationErrors.ClockOffsetTooLarge.Should().Contain(fragments["clockRefusalText"]);
        ValidationErrors.KeyReused.Should().Contain(fragments["keyReusedText"]);
        ValidationErrors.DeskObservationsNeedBorderRole.Should().Contain(fragments["deskRoleText"]);
        ValidationErrors.OwnCampaign.Should().Contain(fragments["ownCampaignText"]);
        ValidationErrors.Forbidden.Should().Contain(ValidationErrors.DeskObservationsNeedBorderRole, "the tablet tells it apart among the 403 answers");
        ValidationErrors.Forbidden.Should().Contain(ValidationErrors.OwnCampaign, "the tablet tells it apart among the 403 answers");
    }

    [Fact]
    public void WebFragments_Should_MatchNoOtherValidationText_When_TheTabletTellsTheRefusalsApart()
    {
        var fragments = Fragments();
        var others = typeof(ValidationErrors).GetFields()
            .Where(f => f is { IsLiteral: true, FieldType.Name: nameof(String) })
            .Where(f => f.Name is not nameof(ValidationErrors.ClockOffsetTooLarge) and not nameof(ValidationErrors.KeyReused)
                and not nameof(ValidationErrors.DeskObservationsNeedBorderRole) and not nameof(ValidationErrors.OwnCampaign))
            .Select(f => (string)f.GetRawConstantValue())
            .ToList();

        others.Should().NotBeEmpty();
        others.Should().NotContain(text => text.Contains(fragments["clockRefusalText"]) || text.Contains(fragments["keyReusedText"]) ||
            text.Contains(fragments["deskRoleText"]) || text.Contains(fragments["ownCampaignText"]));
    }

    #endregion
}
