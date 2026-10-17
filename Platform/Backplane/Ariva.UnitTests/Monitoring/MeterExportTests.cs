using System.Reflection;
using System.Text.RegularExpressions;
using Ariva.Api.Common.Extensions;
using Ariva.UnitTests.Setup;
using FluentAssertions;

namespace Ariva.UnitTests.Monitoring;

/// <summary>
/// ARV-125a: every meter Ariva creates is exported. Each type that creates a meter names it in a <c>MeterName</c> constant,
/// every such name is in <see cref="TelemetryExtensions.Meters"/> (the list the hosts give OpenTelemetry), and no meter is
/// created anywhere else. Before ARV-125a, Ariva.Border, Ariva.Flights and Ariva.Integration were created and never exported.
/// </summary>
public sealed partial class MeterExportTests
{
    /// <summary>Every Ariva assembly the hosts and the simulator load, from their entry assemblies down.</summary>
    internal static IReadOnlyList<Assembly> ArivaAssemblies()
    {
        var roots = new[]
        {
            typeof(Ariva.Api.Main._IAssemblyMark), typeof(Ariva.Api.Ingest._IAssemblyMark), typeof(Ariva.Api.Stream._IAssemblyMark),
            typeof(Ariva.Api.Cronz._IAssemblyMark), typeof(Ariva.Api.Integration._IAssemblyMark), typeof(Ariva.Simulation.Api._IAssemblyMark)
        }.Select(t => t.Assembly);
        var seen = new Dictionary<string, Assembly>(StringComparer.Ordinal);
        var queue = new Queue<Assembly>(roots);
        while (queue.TryDequeue(out var assembly))
        {
            if (!seen.TryAdd(assembly.GetName().Name!, assembly))
                continue;
            foreach (var reference in assembly.GetReferencedAssemblies().Where(r => r.Name!.StartsWith("Ariva.", StringComparison.Ordinal)))
                queue.Enqueue(Assembly.Load(reference));
        }

        return [.. seen.Values.OrderBy(a => a.GetName().Name, StringComparer.Ordinal)];
    }

    /// <summary>Every type of an Ariva assembly that names a meter in a public <c>MeterName</c> constant, with that name.</summary>
    internal static IReadOnlyList<(Type Type, string Meter)> MeterTypes() =>
    [
        .. from assembly in ArivaAssemblies()
           from type in TypesOf(assembly)
           let field = type.GetField("MeterName", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
           where field is { IsLiteral: true } && field.FieldType == typeof(string)
           orderby type.FullName
           select (type, (string)field.GetRawConstantValue()!)
    ];

    private static IEnumerable<Type> TypesOf(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.Where(t => t is not null)!;
        }
    }

    [Fact]
    public void EveryArivaMeter_Should_BeExported_When_AHostSetsUpItsTelemetry()
    {
        var meters = MeterTypes();

        meters.Select(m => m.Meter).Should().Contain(["Ariva.Border", "Ariva.Flights", "Ariva.Integration"], "the three meters the ARV-159 review found unexported are Ariva's");
        foreach (var (type, meter) in meters)
            TelemetryExtensions.Meters.Should().Contain(meter, $"{type.FullName} creates the meter {meter}, so the hosts must export it");
    }

    [Fact]
    public void ExportedMeters_Should_EachBeNamedByAType_When_TheyAreNotMassTransits()
    {
        var named = MeterTypes().Select(m => m.Meter).ToHashSet(StringComparer.Ordinal);

        TelemetryExtensions.Meters.Should().OnlyHaveUniqueItems();
        TelemetryExtensions.Meters.Where(m => m != "MassTransit").Should().OnlyContain(m => named.Contains(m), "no stale entry: every exported Ariva meter is a type's MeterName");
    }

    [Fact]
    public void EveryMeterCreation_Should_BeInATypeThatNamesItsMeter_When_TheSourcesAreScanned()
    {
        // A meter created anywhere else (a literal name, another field, a method's local) would escape the export test above.
        var findings = new List<string>();
        var files = new[] { "Platform/Backplane", "Platform/Simulation" }
            .SelectMany(root => Directory.EnumerateFiles(RepositoryPaths.Resolve(root), "*.cs", SearchOption.AllDirectories))
            .Where(path => !IsExcluded(path))
            .ToList();
        files.Should().NotBeEmpty();

        foreach (var path in files)
        {
            var text = File.ReadAllText(path);
            var creations = MeterCreations().Matches(text).Cast<Match>().ToList();
            if (creations.Count == 0)
                continue;
            var relative = Path.GetRelativePath(RepositoryPaths.Root, path).Replace('\\', '/');
            if (!NamesItsMeter().IsMatch(text))
                findings.Add($"{relative}: creates a meter but declares no public const string MeterName");
            foreach (var creation in creations.Where(c => c.Groups["argument"].Value.Trim() != "MeterName"))
                findings.Add($"{relative}: '{creation.Value}' creates a meter that is not named by its type's MeterName");
        }

        findings.Should().BeEmpty();
        files.Count(path => MeterCreations().IsMatch(File.ReadAllText(path))).Should().BeGreaterThanOrEqualTo(MeterTypes().Count,
            "the scan sees every meter the export test sees");
    }

    [Theory]
    [InlineData("_meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);", true)]
    [InlineData("private readonly Meter _meter = new(MeterName);", true)]
    [InlineData("var meter = new Meter(\"Ariva.Other\");", false)]
    [InlineData("private readonly Meter _meter = new(\"Ariva.Other\");", false)]
    [InlineData("var meter = meterFactory.Create(\"Ariva.Other\");", false)]
    [InlineData("var meter = new System.Diagnostics.Metrics.Meter(Name, \"1.0\");", false)]
    public void Scan_Should_TellAMeterNamedByItsTypeFromAnyOther_When_ItMeetsACreation(string line, bool named)
    {
        var creation = MeterCreations().Match(line);

        creation.Success.Should().BeTrue("every form of creating a meter is seen");
        (creation.Groups["argument"].Value.Trim() == "MeterName").Should().Be(named);
    }

    private static bool IsExcluded(string path)
    {
        var parts = path.Replace('\\', '/').Split('/');
        return parts.Any(p => p is "bin" or "obj" or "Ariva.UnitTests" or "Ariva.IntegrationTests" or "node_modules");
    }

    // new Meter(...), new System.Diagnostics.Metrics.Meter(...), "Meter x = new(...)" and a meter factory's Create(...).
    [GeneratedRegex(@"new\s+(?:System\.Diagnostics\.Metrics\.)?Meter\s*\((?<argument>[^)]*)\)|\bMeter\s+\w+\s*=\s*new\s*\((?<argument>[^)]*)\)|\b\w*[mM]eterFactory\??\s*\.\s*Create\s*\((?<argument>[^)]*)\)")]
    private static partial Regex MeterCreations();

    [GeneratedRegex(@"public\s+const\s+string\s+MeterName\s*=")]
    private static partial Regex NamesItsMeter();
}
