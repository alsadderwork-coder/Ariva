using System.Text.RegularExpressions;
using Ariva.UnitTests.Setup;
using FluentAssertions;

namespace Ariva.UnitTests.Architecture;

/// <summary>
/// ARV-195: Kestrel takes a per-request minimum body data rate on HTTP/1.x only; on HTTP/2 and HTTP/3 reading or setting it
/// throws (Kestrel 10.0.12), and the response rate's feature is not there at all, so an endpoint that touched either on any
/// protocol would answer every request over those 500 the day TLS ends on Kestrel. Every rate goes through
/// <c>SteadyUploadAttribute.RequireRate</c>, which sets the request rate for HTTP/1.0 and HTTP/1.1 requests alone (the sensor
/// pushes, the steady uploads and the integration bodies; its unit tests in <c>HostRequestPathTests</c>, whose fake throws as
/// Kestrel's does). The scan reads the Platform's production code, and the build files that could alias a type, for either
/// feature and for any assignment of a rate: only that method may hold them, as one statement behind the protocol check.
/// </summary>
public sealed partial class MinimumBodyRateTests
{
    #region Fields

    /// <summary>The one file allowed to set the rate.</summary>
    private const string Allowed = "Backplane/Ariva.Api.Common/Filters/SteadyUploadAttribute.cs";

    #endregion

    #region Tests

    [Fact]
    public void Platform_Should_SetAMinimumBodyRateOnlyOverHttp1_When_ItsProductionCodeIsRead()
    {
        var root = RepositoryPaths.Platform;
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Select(f => (File: Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'), Path: f))
            .Where(f => !IsBuildOutput(f.File) && !f.File.Contains("/Ariva.UnitTests/", StringComparison.Ordinal) &&
                !f.File.Contains("/Ariva.IntegrationTests/", StringComparison.Ordinal))
            .Select(f => (f.File, Text: File.ReadAllText(f.Path)))
            .ToList();

        files.Where(f => f.File != Allowed && RateSetting().IsMatch(f.Text)).Select(f => f.File).Should().BeEmpty(
            "a per-request body rate is set through SteadyUploadAttribute.RequireRate, over HTTP/1.x only");
        var allowed = files.Should().ContainSingle(f => f.File == Allowed, "the allowed file exists").Subject.Text;
        Feature().Matches(allowed).Should().ContainSingle("RequireRate takes the feature once");
        RateAssignment().Matches(allowed).Should().ContainSingle("and sets the rate once");
        GuardedSetting().IsMatch(allowed).Should().BeTrue("in one statement behind the HTTP/1.0 and HTTP/1.1 check");
    }

    [Fact]
    public void BuildFiles_Should_NameNoRateFeature_When_TheyAreRead()
    {
        // An MSBuild alias (<Using Include="..." Alias="..." />) lands under obj, which the source scan skips.
        var builds = Directory.EnumerateFiles(RepositoryPaths.Platform, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".csproj", StringComparison.Ordinal) || f.EndsWith(".props", StringComparison.Ordinal) ||
                f.EndsWith(".targets", StringComparison.Ordinal))
            .Where(f => !IsBuildOutput(Path.GetRelativePath(RepositoryPaths.Platform, f).Replace(Path.DirectorySeparatorChar, '/')))
            .Concat(Directory.EnumerateFiles(RepositoryPaths.Root, "*.props", SearchOption.TopDirectoryOnly))
            .Concat(Directory.EnumerateFiles(RepositoryPaths.Root, "*.targets", SearchOption.TopDirectoryOnly))
            .ToList();

        builds.Should().Contain(f => Path.GetFileName(f) == "Directory.Build.props", "the root's build properties are read too");
        builds.Where(f => Feature().IsMatch(File.ReadAllText(f))).Should().BeEmpty("no build file aliases a rate feature");
    }

    [Theory]
    [InlineData("feature.MinDataRate = rate;")]
    [InlineData("feature.MinDataRate=null;")]
    [InlineData("feature.MinDataRate ??= rate;")]
    [InlineData("HttpContext.Features.Get<IHttpMinRequestBodyDataRateFeature>().MinDataRate = new MinDataRate(1024, TimeSpan.FromSeconds(5));")]
    [InlineData("if (http.Features.Get<IHttpMinRequestBodyDataRateFeature>() is { } body)")]
    [InlineData("http.Features.Get<IHttpMinResponseDataRateFeature>()?.MinDataRate = rate;")]
    [InlineData("var response = http.Features.Get<IHttpMinResponseDataRateFeature>();")]
    public void Scan_Should_FindTheRateSetting_When_TheCodeHoldsOne(string line) =>
        RateSetting().IsMatch(line).Should().BeTrue(line);

    [Theory]
    [InlineData("options.Limits.MinRequestBodyDataRate = new MinDataRate(240, TimeSpan.FromSeconds(5));")]
    [InlineData("options.Limits.MinResponseDataRate = null;")]
    [InlineData("rate.Stored.Should().Be(null);")]
    [InlineData("public static MinDataRate PushRate { get; } = new(1024, TimeSpan.FromSeconds(5));")]
    public void Scan_Should_PassTheCode_When_ItSetsNoPerRequestRate(string line) =>
        RateSetting().IsMatch(line).Should().BeFalse(line);

    #endregion

    #region Methods

    private static bool IsBuildOutput(string relativeFile) =>
        relativeFile.Contains("/bin/", StringComparison.Ordinal) || relativeFile.Contains("/obj/", StringComparison.Ordinal) ||
        relativeFile.Contains("/node_modules/", StringComparison.Ordinal);

    /// <summary>
    /// Either per-request rate feature, or an assignment of a rate (plain or <c>??=</c>). A read of the rate (which also throws on
    /// HTTP/2 and HTTP/3) needs the feature, so the feature's name finds it.
    /// </summary>
    [GeneratedRegex(@"IHttpMin(?:RequestBody|Response)DataRateFeature|\.MinDataRate\s*(?:\?\?)?=(?!=)")]
    private static partial Regex RateSetting();

    /// <summary>Either per-request rate feature.</summary>
    [GeneratedRegex(@"IHttpMin(?:RequestBody|Response)DataRateFeature")]
    private static partial Regex Feature();

    /// <summary>An assignment of a per-request rate, plain or <c>??=</c>.</summary>
    [GeneratedRegex(@"\.MinDataRate\s*(?:\?\?)?=(?!=)")]
    private static partial Regex RateAssignment();

    /// <summary>The protocol check, the feature and the assignment as the one statement of <c>RequireRate</c>.</summary>
    [GeneratedRegex(@"if \(\(HttpProtocol\.IsHttp11\(http\.Request\.Protocol\) \|\| HttpProtocol\.IsHttp10\(http\.Request\.Protocol\)\) &&\s*http\.Features\.Get<IHttpMinRequestBodyDataRateFeature>\(\) is \{ \} feature\)\s*feature\.MinDataRate = rate;")]
    private static partial Regex GuardedSetting();

    #endregion
}
