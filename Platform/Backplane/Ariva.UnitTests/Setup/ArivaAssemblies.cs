using System.Reflection;

namespace Ariva.UnitTests.Setup;

/// <summary>
/// The Ariva assemblies inspected by the architecture and security tests, loaded through their
/// <c>_IAssemblyMark</c> anchors.
/// </summary>
public static class ArivaAssemblies
{
    /// <summary>The host assemblies (every process Ariva runs).</summary>
    public static readonly IReadOnlyList<Assembly> Hosts =
    [
        typeof(Ariva.Api.Main._IAssemblyMark).Assembly,
        typeof(Ariva.Api.Ingest._IAssemblyMark).Assembly,
        typeof(Ariva.Api.Stream._IAssemblyMark).Assembly,
        typeof(Ariva.Api.Cronz._IAssemblyMark).Assembly,
        typeof(Ariva.Api.Integration._IAssemblyMark).Assembly,
        typeof(Ariva.Simulation.Api._IAssemblyMark).Assembly
    ];

    /// <summary>Every Ariva assembly: libraries and hosts.</summary>
    public static readonly IReadOnlyList<Assembly> All =
    [
        typeof(Ariva.Utilities._IAssemblyMark).Assembly,
        typeof(Ariva.Core._IAssemblyMark).Assembly,
        typeof(Ariva.Resources._IAssemblyMark).Assembly,
        typeof(Ariva.Infra._IAssemblyMark).Assembly,
        typeof(Ariva.Di._IAssemblyMark).Assembly,
        typeof(Ariva.Api.Common._IAssemblyMark).Assembly,
        typeof(Ariva.Business.Contracts._IAssemblyMark).Assembly,
        .. Hosts
    ];
}
