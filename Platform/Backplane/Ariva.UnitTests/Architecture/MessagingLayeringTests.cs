using System.Reflection;
using Ariva.UnitTests.Setup;
using FluentAssertions;

namespace Ariva.UnitTests.Architecture;

/// <summary>
/// ADR-0018: MassTransit and Confluent.Kafka are implementation details of Ariva.Infra and Ariva.Di, so the bus can
/// be swapped (v9 licence or raw Confluent client) without touching the domain, the contracts or the hosts' logic.
/// The compiler records an assembly reference only when a type from it is used.
/// </summary>
public sealed class MessagingLayeringTests
{
    #region Fields

    private static readonly string[] MessagingAssemblyPrefixes = ["MassTransit", "Confluent.Kafka"];

    private static readonly IReadOnlyList<Assembly> MessagingFreeAssemblies =
    [
        typeof(Ariva.Utilities._IAssemblyMark).Assembly,
        typeof(Ariva.Core._IAssemblyMark).Assembly,
        typeof(Ariva.Resources._IAssemblyMark).Assembly,
        typeof(Ariva.Business.Contracts._IAssemblyMark).Assembly,
        typeof(Ariva.Api.Common._IAssemblyMark).Assembly
    ];

    #endregion

    #region Tests

    [Fact]
    public void GetReferencedAssemblies_Should_ContainNoMessagingLibrary_When_AssemblyIsDomainOrContracts()
    {
        var violations = MessagingFreeAssemblies
            .SelectMany(assembly => assembly.GetReferencedAssemblies()
                .Where(reference => MessagingAssemblyPrefixes.Any(prefix =>
                    reference.Name is not null && reference.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                .Select(reference => $"{assembly.GetName().Name} -> {reference.Name}"))
            .ToList();

        violations.Should().BeEmpty("MassTransit and Confluent.Kafka belong in Ariva.Infra and Ariva.Di only (ADR-0018)");
    }

    [Fact]
    public void AddMassTransit_Should_BeCalledOnlyByAddArivaMessaging_When_AnyProductionCodeRegistersABus()
    {
        // ARV-097: AddArivaMessaging turns MassTransit's usage telemetry off (MessagingRegistrationTests); a bus
        // registered anywhere else, including a second bus (AddMassTransit<TBus>), would report to MassTransit again.
        var platform = Path.Combine(RepositoryPaths.Root, "Platform");
        var call = new System.Text.RegularExpressions.Regex(@"\bAddMassTransit(?!TestHarness)\s*[<(]");
        var callers = Directory.EnumerateFiles(platform, "*.cs", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(platform, file).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(file => !file.Contains("/bin/", StringComparison.Ordinal) && !file.Contains("/obj/", StringComparison.Ordinal) &&
                           !file.Contains("/node_modules/", StringComparison.Ordinal) &&
                           !file.Split('/')[1].EndsWith("Tests", StringComparison.Ordinal))
            .Where(file => call.IsMatch(File.ReadAllText(Path.Combine(platform, file))))
            .ToList();

        callers.Should().Equal(["Backplane/Ariva.Di/Extensions/MessagingExtensions.cs"], "every host gets its bus, with usage telemetry off, from AddArivaMessaging");
    }

    [Fact]
    public void MessagingFreeAssemblies_Should_BeSubsetOfAll_When_ListIsMaintained()
    {
        ArivaAssemblies.All.Should().Contain(MessagingFreeAssemblies);
    }

    #endregion
}
