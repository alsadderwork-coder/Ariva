using System.Text.RegularExpressions;
using Ariva.Core.Reports;
using Ariva.Core.Services.Reports;
using Ariva.Infra.Services.Reports;
using Ariva.UnitTests.Setup;
using FluentAssertions;

namespace Ariva.UnitTests.Architecture;

/// <summary>
/// ARV-119 (CWE-863): the weekly summary's reader takes the roles it projects for as an argument and checks no site access, so
/// only the weekly summary service (which checks the caller's sites and roles first) and the composition root may name it;
/// tests aside. The service's interface exposes one site-scoped read, and the reader stays internal to Ariva.Infra.
/// </summary>
public sealed class WeeklySummaryExposureTests
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly string[] Skip =
    [
        $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", $"{Path.DirectorySeparatorChar}.svelte-kit{Path.DirectorySeparatorChar}",
        "Ariva.UnitTests", "Ariva.IntegrationTests", "Ariva.E2E", "Ariva.LoadTests"
    ];

    private static readonly string[] Kinds = [".cs", ".ts", ".svelte", ".js", ".mjs"];

    [Fact]
    public void Sources_Should_NameTheReaderOnlyInTheServiceAndTheCompositionRoot_When_Scanned()
    {
        var reader = new Regex(@"\bWeeklySummaryReader\b", RegexOptions.CultureInvariant, RegexTimeout);

        var sources = Directory.EnumerateFiles(RepositoryPaths.Platform, "*", SearchOption.AllDirectories)
            .Where(f => Kinds.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase) && !Skip.Any(s => f.Contains(s, StringComparison.Ordinal)))
            .Where(f => reader.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(RepositoryPaths.Platform, f).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

        sources.Should().Equal(["Backplane/Ariva.Di/Extensions/AccountExtensions.cs", "Backplane/Ariva.Infra/Services/Reports/SvcWeeklySummary.cs",
            "Backplane/Ariva.Infra/Services/Reports/WeeklySummaryReader.cs"],
            "the reader projects for the roles it is given and checks no site access; only the service, which checks both, calls it");
    }

    [Fact]
    public void Interface_Should_ExposeOnlyTheSiteScopedRead_When_Reflected()
    {
        var methods = typeof(ISvcWeeklySummary).GetMethods();

        methods.Select(m => m.Name).Should().Equal("GetAsync");
        methods[0].GetParameters()[0].Should().Match<System.Reflection.ParameterInfo>(p => p.Name == "siteCode" && p.ParameterType == typeof(string));
        methods[0].ReturnType.Should().Be(typeof(Task<Fluentx.Result<WeeklySummary>>));
        typeof(WeeklySummaryReader).IsPublic.Should().BeFalse("the reader stays inside Ariva.Infra");
    }
}
