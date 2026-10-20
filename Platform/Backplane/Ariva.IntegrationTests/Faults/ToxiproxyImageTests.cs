using FluentAssertions;

namespace Ariva.IntegrationTests.Faults;

/// <summary>
/// ARV-096, second security review (L7): the faults fixture pulls Toxiproxy by its pinned digest, takes it by its tag alone only when
/// <see cref="FaultsFixture.TagOnlyVariable"/> is true outside a pipeline, and refuses that variable in one (GitHub Actions sets
/// <c>GITHUB_ACTIONS</c> to true, Azure DevOps sets <c>TF_BUILD</c> to True). The generic <c>CI</c> variable alone, which local tooling
/// sets too, does not refuse it. No container is started here.
/// </summary>
public sealed class ToxiproxyImageTests
{
    private const string Tag = "ghcr.io/shopify/toxiproxy:2.12.0";

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("false", "true", null)]
    [InlineData("TRUE", null, "True")]
    public void ImageToStart_Should_TakeThePinnedDigest_When_TheTagOnlyVariableIsNotTrue(string tagOnly, string githubActions, string tfBuild)
    {
        var image = FaultsFixture.ImageToStart(Variables(tagOnly, githubActions, tfBuild));

        image.Should().Be(FaultsFixture.ToxiproxyImage).And.StartWith(Tag + "@sha256:");
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("false", "False", null)]
    [InlineData(null, null, "true")]
    public void ImageToStart_Should_TakeTheTagAlone_When_TheVariableIsTrueOutsideAPipeline(string githubActions, string tfBuild, string genericCi)
    {
        FaultsFixture.ImageToStart(Variables("true", githubActions, tfBuild, genericCi)).Should().Be(Tag);
    }

    [Theory]
    [InlineData("true", null)]
    [InlineData("True", null)]
    [InlineData(null, "True")]
    [InlineData("false", "true")]
    public void ImageToStart_Should_Throw_When_TheVariableIsTrueInAPipeline(string githubActions, string tfBuild)
    {
        var start = () => FaultsFixture.ImageToStart(Variables("true", githubActions, tfBuild, "true"));

        start.Should().Throw<InvalidOperationException>().WithMessage($"*{FaultsFixture.TagOnlyVariable}*pinned digest*");
    }

    private static Func<string, string> Variables(string tagOnly, string githubActions, string tfBuild, string genericCi = null) => name => name switch
    {
        FaultsFixture.TagOnlyVariable => tagOnly,
        "GITHUB_ACTIONS" => githubActions,
        "TF_BUILD" => tfBuild,
        "CI" => genericCi,
        _ => null
    };
}
