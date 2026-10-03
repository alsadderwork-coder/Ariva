using System.Reflection;
using FluentAssertions;

namespace Ariva.UnitTests.Architecture;

/// <summary>
/// Architecture rules checked with reflection only. They read the assembly references the compiler emitted,
/// which appear only when a type from the referenced assembly is actually used, so they catch real coupling
/// between layers rather than unused project references.
/// </summary>
public sealed class LayeringTests
{
    private const string ArivaPrefix = "Ariva.";
    private const string ArivaApiPrefix = "Ariva.Api.";
    private const string AmanPrefix = "Aman.";

    private static readonly Assembly UtilitiesAssembly = typeof(Ariva.Utilities._IAssemblyMark).Assembly;
    private static readonly Assembly CoreAssembly = typeof(Ariva.Core._IAssemblyMark).Assembly;
    private static readonly Assembly InfraAssembly = typeof(Ariva.Infra._IAssemblyMark).Assembly;
    private static readonly Assembly DiAssembly = typeof(Ariva.Di._IAssemblyMark).Assembly;
    private static readonly Assembly ApiCommonAssembly = typeof(Ariva.Api.Common._IAssemblyMark).Assembly;
    private static readonly Assembly BusinessContractsAssembly = typeof(Ariva.Business.Contracts._IAssemblyMark).Assembly;

    private static readonly Assembly[] ArivaAssemblies =
    [
        UtilitiesAssembly,
        CoreAssembly,
        InfraAssembly,
        DiAssembly,
        ApiCommonAssembly,
        BusinessContractsAssembly
    ];

    // Property name fragments that indicate an officer or person identifier. "Nationality" is deliberately
    // absent: it is allowed as an aggregate count key (passengers per nationality), never as a per person field.
    private static readonly string[] IdentifierFragments =
    [
        "Officer",
        "Badge",
        "PersonId",
        "Passport",
        "DocumentNumber",
        "FirstName",
        "LastName",
        "FullName"
    ];

    [Fact]
    public void GetReferencedAssemblies_Should_ContainNoArivaAssemblyExceptUtilitiesAndContracts_When_AssemblyIsCore()
    {
        // Ariva.Business.Contracts is a dependency-free package of feed records (tested below to stand alone); the
        // immigration intake checks those records as they are (ARV-048), so the domain may name them.
        var allowed = new[] { UtilitiesAssembly.GetName().Name, BusinessContractsAssembly.GetName().Name };

        var violations = ArivaReferencesOf(CoreAssembly)
            .Where(name => !allowed.Contains(name))
            .ToList();

        violations.Should().BeEmpty("Ariva.Core is the domain layer and may depend on Ariva.Utilities and the standalone feed contracts only");
    }

    [Fact]
    public void GetReferencedAssemblies_Should_ContainNoArivaAssembly_When_AssemblyIsUtilities()
    {
        var violations = ArivaReferencesOf(UtilitiesAssembly).ToList();

        violations.Should().BeEmpty("Ariva.Utilities is the bottom layer and depends on no other Ariva assembly");
    }

    [Fact]
    public void GetReferencedAssemblies_Should_ContainNoApiAssembly_When_AssemblyIsInfra()
    {
        var violations = ArivaReferencesOf(InfraAssembly)
            .Where(name => name.StartsWith(ArivaApiPrefix, StringComparison.Ordinal))
            .ToList();

        violations.Should().BeEmpty("Ariva.Infra implements Core services and must not depend on any API host or Ariva.Api.Common");
    }

    [Fact]
    public void GetReferencedAssemblies_Should_ContainNoArivaAssembly_When_AssemblyIsBusinessContracts()
    {
        var violations = ArivaReferencesOf(BusinessContractsAssembly).ToList();

        violations.Should().BeEmpty("Ariva.Business.Contracts is shipped to AMAN as a package and must stand alone");
    }

    [Fact]
    public void GetReferencedAssemblies_Should_ContainNoAmanAssembly_When_AssemblyIsAnyArivaAssembly()
    {
        var violations = ArivaAssemblies
            .SelectMany(assembly => assembly.GetReferencedAssemblies()
                .Select(reference => reference.Name)
                .Where(name => name is not null && name.StartsWith(AmanPrefix, StringComparison.Ordinal))
                .Select(name => $"{assembly.GetName().Name} -> {name}"))
            .ToList();

        violations.Should().BeEmpty("Ariva is a separate product and integrates with AMAN only through Ariva.Business.Contracts");
    }

    [Fact]
    public void GetProperties_Should_ExposeNoOfficerOrPersonIdentifier_When_TypeIsPublicBusinessContract()
    {
        var contractTypes = BusinessContractsAssembly.GetExportedTypes();

        var violations = contractTypes
            .SelectMany(type => type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Where(property => IsIdentifierName(property.Name))
                .Select(property => $"{type.FullName}.{property.Name}"))
            .Distinct()
            .ToList();

        contractTypes.Should().NotBeEmpty("the data boundary rule needs the contract types to inspect");
        violations.Should().BeEmpty("AMAN feed contracts are aggregate only; officer and passenger identities never cross into Ariva");
    }

    private static IEnumerable<string> ArivaReferencesOf(Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name is not null && name.StartsWith(ArivaPrefix, StringComparison.Ordinal));

    private static bool IsIdentifierName(string propertyName) =>
        IdentifierFragments.Any(fragment => propertyName.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}
