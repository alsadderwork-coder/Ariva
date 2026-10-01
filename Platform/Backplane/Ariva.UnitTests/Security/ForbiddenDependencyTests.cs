using System.Diagnostics;
using Ariva.UnitTests.Setup;
using FluentAssertions;

namespace Ariva.UnitTests.Security;

/// <summary>
/// CWE-78, CWE-77 and CWE-94: Ariva never starts processes, never evaluates code and never builds LINQ from
/// strings. The compiler records an assembly reference only when a type from it is used, so a reference to one of
/// these assemblies means the capability is actually in use.
/// </summary>
public sealed class ForbiddenDependencyTests
{
    #region Fields

    private static readonly string[] ForbiddenAssemblies =
    [
        "System.Diagnostics.Process",
        "System.Linq.Dynamic.Core",
        "Microsoft.CodeAnalysis.CSharp.Scripting",
        "Microsoft.CodeAnalysis.Scripting"
    ];

    #endregion

    #region Tests

    [Fact]
    public void GetReferencedAssemblies_Should_ContainNoForbiddenAssembly_When_AssemblyIsAriva()
    {
        var violations = ArivaAssemblies.All
            .SelectMany(assembly => assembly.GetReferencedAssemblies()
                .Where(reference => ForbiddenAssemblies.Contains(reference.Name, StringComparer.OrdinalIgnoreCase))
                .Select(reference => $"{assembly.GetName().Name} -> {reference.Name}"))
            .ToList();

        violations.Should().BeEmpty("Ariva starts no processes (CWE-78, CWE-77) and evaluates no code or dynamic LINQ (CWE-94)");
    }

    [Fact]
    public void GetName_Should_MatchForbiddenName_When_AssemblyDefinesProcess()
    {
        // Guards the rule above: if the runtime moved Process to another assembly, the check would pass silently.
        var processAssembly = typeof(Process).Assembly.GetName().Name;

        ForbiddenAssemblies.Should().Contain(processAssembly);
    }

    [Fact]
    public void GetReferencedAssemblies_Should_FindProcessReference_When_AssemblyUsesProcess()
    {
        // The test assembly uses Process (in the test above), so the same detection must report it here.
        var references = typeof(ForbiddenDependencyTests).Assembly.GetReferencedAssemblies().Select(reference => reference.Name);

        references.Should().Contain("System.Diagnostics.Process");
    }

    #endregion
}
