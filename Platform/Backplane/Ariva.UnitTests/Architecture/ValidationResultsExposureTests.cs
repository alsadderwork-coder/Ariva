using System.Reflection;
using System.Text.RegularExpressions;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Validation;
using Ariva.Core.Validation.Comparison;
using Ariva.UnitTests.Setup;
using FluentAssertions;

namespace Ariva.UnitTests.Architecture;

/// <summary>
/// ARV-104g (CWE-862, CWE-863, CWE-120): the frozen validation results. The stored documents (script 0050) hold every section,
/// the desk-state results, the observer-level results and the shadow's zone figures, so only the validation results service reads
/// or writes their table, and serves them only through the per-caller projection; the service's interface exposes only the
/// site-scoped read and the recomputation (the computation and the comparison, which skip the caller's site check, stay
/// internal: L7 of the ARV-104g2 review); and no type reachable from the results lists minutes (M3 of the same review).
/// </summary>
public sealed class ValidationResultsExposureTests
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly string[] Skip =
    [
        $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", $"{Path.DirectorySeparatorChar}.svelte-kit{Path.DirectorySeparatorChar}",
        "Ariva.UnitTests", "Ariva.IntegrationTests", "Ariva.E2E", "Ariva.LoadTests"
    ];

    private static readonly string[] Kinds = [".cs", ".ts", ".svelte", ".js", ".mjs", ".sql", ".json"];

    [Fact]
    public void Sources_Should_NameTheFrozenResultsTableOnlyInTheResultsServiceAndItsScript_When_Scanned()
    {
        // Case-insensitive with optional underscores: the snake_case table and any PascalCase name a convention mapper would turn
        // into it (an entity ValidationResultRevision would be mapped to the table without the table's name in the source).
        var table = new Regex(@"validation_?result_?revision", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

        var sources = Directory.EnumerateFiles(RepositoryPaths.Platform, "*", SearchOption.AllDirectories)
            .Where(f => Kinds.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase) && !Skip.Any(s => f.Contains(s, StringComparison.Ordinal)))
            .Where(f => table.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(RepositoryPaths.Platform, f).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

        sources.Should().Equal(["Backplane/Ariva.Infra/Services/Validation/SvcValidationResults.Revisions.cs",
            "Backplane/Ariva.Infra/Timescale/Scripts/0050_validation_result_revision.sql"],
            "only the validation results service stores and reads the frozen documents, and serves them through the projection");
    }

    [Fact]
    public void Interface_Should_ExposeOnlyTheSiteScopedReadAndTheRecomputation_When_Reflected()
    {
        var methods = typeof(ISvcValidationResults).GetMethods().OrderBy(m => m.Name, StringComparer.Ordinal).ToList();

        methods.Select(m => m.Name).Should().Equal("GetAsync", "RecomputeAsync");
        methods.Should().OnlyContain(m => m.GetParameters()[0].Name == "siteCode" && m.GetParameters()[0].ParameterType == typeof(string),
            "every exposed call names the site first, so the caller's sites are checked before anything else");
        methods.Should().OnlyContain(m => m.ReturnType == typeof(Task<Fluentx.Result<ValidationResultsJson>>),
            "the results leave the service only as the projection's JSON, never as the shared results");
        var service = typeof(Ariva.Infra.Services.Validation.SvcValidationResults);
        service.IsNotPublic.Should().BeTrue();
        service.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly).Select(m => m.Name).Order(StringComparer.Ordinal)
            .Should().Equal("GetAsync", "RecomputeAsync");
    }

    [Fact]
    public void Controller_Should_LimitRateTimeAndBodyAndAskForASecondFactor_When_ItsEndpointsAreReflected()
    {
        // CWE-400, CWE-770, CWE-306 (L7 and M3 of the ARV-104g2 review): a few requests at once per host with a few waiting for a
        // turn, a request timeout, a body limit on the recomputation and a second factor before it.
        var controller = typeof(Ariva.Api.Main.Controllers.OpsArea.Validation.ValidationResultsController);
        controller.GetCustomAttribute<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>()!.PolicyName
            .Should().Be(Ariva.Api.Common.Extensions.RateLimitingExtensions.ValidationResultsPolicy);
        controller.GetCustomAttribute<Microsoft.AspNetCore.Http.Timeouts.RequestTimeoutAttribute>()!.PolicyName
            .Should().Be(Ariva.Infra.Settings.ValidationResultsSettings.RequestTimeoutPolicy);
        controller.GetCustomAttribute<Ariva.Api.Common.Security.SiteScopedAttribute>().Should().NotBeNull();
        var recompute = controller.GetMethod("Recompute")!;
        recompute.GetCustomAttribute<Ariva.Api.Common.Security.RequiresRecentMfaAttribute>().Should().NotBeNull();
        recompute.GetCustomAttribute<Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute>().Should().NotBeNull();
        controller.GetConstructors().Single().GetParameters().Select(p => p.ParameterType).Should().Equal(typeof(ISvcValidationResults));
        new Ariva.Api.Common.Settings.RateLimitingSettings().ValidationResults.Should().Match<Ariva.Api.Common.Settings.ConcurrencySettings>(
            s => s.PermitLimit == 4 && s.QueueLimit == 8, "callers may wait for a turn, a few at most");
    }

    [Fact]
    public void Results_Should_ReachNoListOfMinutes_When_TheirTypesAreWalked()
    {
        var seen = new HashSet<Type>();
        var queue = new Queue<Type>([typeof(ValidationResultsViewModel)]);
        while (queue.Count > 0)
        {
            var type = queue.Dequeue();
            if (!seen.Add(type) || type.Assembly != typeof(ValidationResultsViewModel).Assembly)
                continue;
            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                var mentioned = property.PropertyType.IsGenericType ? property.PropertyType.GetGenericArguments().Append(property.PropertyType) : [property.PropertyType];
                foreach (var t in mentioned)
                    queue.Enqueue(Nullable.GetUnderlyingType(t) ?? t);
            }
        }

        seen.Should().Contain(typeof(NowcastZoneErrors)).And.Contain(typeof(DeskAgreementSummary));
        seen.Should().NotContain(typeof(NowcastMinuteError), "the compared nowcast minutes are counted, never listed (M3)");
        seen.Should().NotContain(typeof(DeskMinuteAgreement), "the observed desk minutes are counted, never listed (M3)");
        seen.Should().NotContain(typeof(ComparisonResult)).And.NotContain(typeof(ComparisonInput));
    }
}
