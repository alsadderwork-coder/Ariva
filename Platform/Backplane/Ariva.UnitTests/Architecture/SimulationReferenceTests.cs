using System.Reflection;
using System.Xml.Linq;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.ApplicationParts;

namespace Ariva.UnitTests.Architecture;

/// <summary>
/// ARV-149a (from the ARV-151 security review, CWE-501, ASVS V15.2.3): the simulator (Ariva.Simulation.Api: the scenario's truth,
/// the sensor, AODB and AMAN emulators and the validation observers) is test and demo tooling. No Ariva host, no layer below them
/// (Ariva.Core, Ariva.Infra, Ariva.Di, Ariva.Api.Common, Ariva.ServiceDefaults, Ariva.Utilities, Ariva.Resources) and not the
/// AMAN feed contracts may carry it, so production code can never read the simulator's truth or call its emulators, and no host
/// can serve its controllers: what the simulator produces reaches Ariva only through Ariva's own APIs (device pushes through
/// Ingest, the integration API, the capture API). Three checks, because each alone has a gap:
/// <list type="bullet">
/// <item>the references the compiler emitted, directly or through another Ariva assembly (they appear only when a type is used,
/// so this alone would miss a project reference no code uses, whose dll would still ship);</item>
/// <item>the <see cref="ApplicationPartAttribute"/>s of every production assembly (the Web SDK writes one for each referenced
/// assembly that uses MVC, and a host registers that assembly's controllers), none naming the simulator;</item>
/// <item>the production projects' <c>ProjectReference</c> graph from their .csproj files, which never reaches
/// <c>Ariva.Simulation.Api.csproj</c>, whatever the compiler kept.</item>
/// </list>
/// </summary>
public sealed class SimulationReferenceTests
{
    private static readonly Assembly Simulator = typeof(Ariva.Simulation.Api._IAssemblyMark).Assembly;

    private static readonly Assembly[] Production =
    [
        typeof(Ariva.Utilities._IAssemblyMark).Assembly,
        typeof(Ariva.Core._IAssemblyMark).Assembly,
        typeof(Ariva.Infra._IAssemblyMark).Assembly,
        typeof(Ariva.Di._IAssemblyMark).Assembly,
        typeof(Ariva.Resources._IAssemblyMark).Assembly,
        typeof(Ariva.Api.Common._IAssemblyMark).Assembly,
        // Ariva.ServiceDefaults has no assembly mark; its OTLP settings name it.
        typeof(Ariva.ServiceDefaults.OtlpSettings).Assembly,
        typeof(Ariva.Business.Contracts._IAssemblyMark).Assembly,
        typeof(Ariva.Api.Main._IAssemblyMark).Assembly,
        typeof(Ariva.Api.Ingest._IAssemblyMark).Assembly,
        typeof(Ariva.Api.Stream._IAssemblyMark).Assembly,
        typeof(Ariva.Api.Cronz._IAssemblyMark).Assembly,
        typeof(Ariva.Api.Integration._IAssemblyMark).Assembly
    ];

    /// <summary>The production projects, from the repository root: the hosts, every layer below them and the feed contracts.</summary>
    private static readonly string[] ProductionProjects =
    [
        "Platform/Backplane/Ariva.Utilities/Ariva.Utilities.csproj",
        "Platform/Backplane/Ariva.Core/Ariva.Core.csproj",
        "Platform/Backplane/Ariva.Resources/Ariva.Resources.csproj",
        "Platform/Backplane/Ariva.Infra/Ariva.Infra.csproj",
        "Platform/Backplane/Ariva.Di/Ariva.Di.csproj",
        "Platform/Backplane/Ariva.Api.Common/Ariva.Api.Common.csproj",
        "Platform/Backplane/Ariva.ServiceDefaults/Ariva.ServiceDefaults.csproj",
        "Platform/Backplane/Ariva.Api.Main/Ariva.Api.Main.csproj",
        "Platform/Backplane/Ariva.Api.Ingest/Ariva.Api.Ingest.csproj",
        "Platform/Backplane/Ariva.Api.Stream/Ariva.Api.Stream.csproj",
        "Platform/Backplane/Ariva.Api.Cronz/Ariva.Api.Cronz.csproj",
        "Platform/Backplane/Ariva.Api.Integration/Ariva.Api.Integration.csproj",
        "Platform/Business/Ariva.Business.Contracts/Ariva.Business.Contracts.csproj"
    ];

    private const string SimulatorProject = "Ariva.Simulation.Api.csproj";

    [Fact]
    public void GetReferencedAssemblies_Should_NeverReachTheSimulator_When_AssemblyIsAnArivaHostOrLayer()
    {
        var simulator = Simulator.GetName().Name;
        var violations = new List<string>();
        foreach (var assembly in Production)
        {
            foreach (var path in ArivaReferencePaths(assembly))
            {
                if (string.Equals(path[^1], simulator, StringComparison.Ordinal))
                    violations.Add(string.Join(" -> ", path));
            }
        }

        violations.Should().BeEmpty("the simulator is test and demo tooling that no Ariva host or layer may reference");
    }

    [Fact]
    public void ArivaReferencePaths_Should_FindTheSimulator_When_AnAssemblyReferencesIt()
    {
        // The walk itself is checked on an assembly known to reference the simulator: this test project.
        var simulator = Simulator.GetName().Name;
        ArivaReferencePaths(typeof(SimulationReferenceTests).Assembly).Select(p => p[^1])
            .Should().Contain(simulator, "the unit tests use the simulator's scenario engine");
        simulator.Should().Be("Ariva.Simulation.Api");
    }

    [Fact]
    public void ApplicationParts_Should_NeverNameTheSimulator_When_AssemblyIsAnArivaHostOrLayer()
    {
        var parts = Production.SelectMany(a => a.GetCustomAttributes<ApplicationPartAttribute>().Select(p => (Assembly: a.GetName().Name, Part: p.AssemblyName))).ToList();

        parts.Where(p => p.Part is not null && p.Part.StartsWith("Ariva.Simulation", StringComparison.Ordinal)).Should()
            .BeEmpty("a host with the simulator as an application part would serve its controllers");
        // The attributes are read: Ariva.Api.Main has Ariva.Api.Common's controllers as an application part.
        parts.Should().Contain(("Ariva.Api.Main", "Ariva.Api.Common"));
    }

    [Fact]
    public void ProjectReferences_Should_NeverReachTheSimulator_When_ProjectIsAnArivaHostOrLayer()
    {
        var violations = new List<string>();
        foreach (var project in ProductionProjects)
        {
            foreach (var path in ProjectReferencePaths(RepositoryPaths.Resolve(project)))
            {
                if (string.Equals(Path.GetFileName(path[^1]), SimulatorProject, StringComparison.OrdinalIgnoreCase))
                    violations.Add(string.Join(" -> ", path.Select(Path.GetFileName)));
            }
        }

        violations.Should().BeEmpty("no production project may reference the simulator, even with no code using it (its dll would ship)");
        // The repository-wide build files add no project reference to any project.
        foreach (var shared in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props" })
        {
            var file = RepositoryPaths.Resolve(shared);
            if (File.Exists(file))
                XDocument.Load(file).Descendants().Where(e => e.Name.LocalName == "ProjectReference").Should().BeEmpty(shared);
        }
    }

    [Fact]
    public void ProjectReferencePaths_Should_FindTheSimulator_When_AProjectReferencesIt()
    {
        // The walk itself is checked on a project known to reference the simulator: this test project.
        ProjectReferencePaths(RepositoryPaths.Resolve("Platform/Backplane/Ariva.UnitTests/Ariva.UnitTests.csproj")).Select(p => Path.GetFileName(p[^1]))
            .Should().Contain(SimulatorProject);
    }

    // Every Ariva assembly reachable from the given one through emitted references, each with the path that reaches it.
    private static List<string[]> ArivaReferencePaths(Assembly start)
    {
        var paths = new List<string[]>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { start.GetName().Name };
        var queue = new Queue<(Assembly Assembly, string[] Path)>();
        queue.Enqueue((start, [start.GetName().Name]));
        while (queue.Count > 0)
        {
            var (assembly, path) = queue.Dequeue();
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                var name = reference.Name;
                if (name is null || !name.StartsWith("Ariva.", StringComparison.Ordinal) || !seen.Add(name))
                    continue;
                string[] next = [.. path, name];
                paths.Add(next);
                queue.Enqueue((Assembly.Load(reference), next));
            }
        }

        return paths;
    }

    // Every project reachable from the given .csproj through its ProjectReference items (paths resolved from each project's own
    // folder), each with the path of full file names that reaches it.
    private static List<string[]> ProjectReferencePaths(string start)
    {
        var paths = new List<string[]>();
        var root = Path.GetFullPath(start);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root };
        var queue = new Queue<string[]>();
        queue.Enqueue([root]);
        while (queue.Count > 0)
        {
            var path = queue.Dequeue();
            var project = path[^1];
            var folder = Path.GetDirectoryName(project) ?? string.Empty;
            foreach (var item in XDocument.Load(project).Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
            {
                var include = (string)item.Attribute("Include");
                if (string.IsNullOrWhiteSpace(include))
                    continue;
                var target = Path.GetFullPath(Path.Combine(folder, include.Replace('\\', Path.DirectorySeparatorChar)));
                if (!seen.Add(target))
                    continue;
                string[] next = [.. path, target];
                paths.Add(next);
                if (File.Exists(target))
                    queue.Enqueue(next);
            }
        }

        return paths;
    }
}
