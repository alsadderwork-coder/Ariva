using Ariva.Api.Common.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Ariva.UnitTests.Setup;

/// <summary>
/// Boots the Ariva hosts in memory with <see cref="WebApplicationFactory{TEntryPoint}"/>. Each host is identified by
/// its <c>_IAssemblyMark</c> interface rather than <c>Program</c>, because every host has its own top level
/// <c>Program</c> and one test project references them all. The hosts also copy their appsettings.service files into
/// the test output, where the last one wins; the security settings under test all come from the shared
/// appsettings.base files.
/// </summary>
public static class ArivaHosts
{
    #region Names

    /// <summary>Ariva.Api.Main.</summary>
    public const string Main = "Main";

    /// <summary>Ariva.Api.Ingest.</summary>
    public const string Ingest = "Ingest";

    /// <summary>Ariva.Api.Stream.</summary>
    public const string Stream = "Stream";

    /// <summary>Ariva.Api.Cronz.</summary>
    public const string Cronz = "Cronz";

    /// <summary>Ariva.Api.Integration.</summary>
    public const string Integration = "Integration";

    /// <summary>Ariva.Simulation.Api.</summary>
    public const string Simulation = "Simulation";

    /// <summary>The Backplane hosts that use the Ariva.Api.Common security baseline.</summary>
    public static readonly IReadOnlyList<string> Backplane = [Main, Ingest, Stream, Cronz, Integration];

    /// <summary>Every host, including the simulator.</summary>
    public static readonly IReadOnlyList<string> All = [.. Backplane, Simulation];

    #endregion

    #region Theory data

    /// <summary>The Backplane hosts, as theory data.</summary>
    public static TheoryData<string> BackplaneHosts => ToTheoryData(Backplane);

    /// <summary>Every host, as theory data.</summary>
    public static TheoryData<string> AllHosts => ToTheoryData(All);

    #endregion

    #region Factory

    /// <summary>
    /// Creates a host in the given Ariva environment. The host starts on first use of
    /// <see cref="IArivaHost.Services"/> or <see cref="IArivaHost.CreateClient"/>.
    /// </summary>
    /// <param name="name">One of the host names above.</param>
    /// <param name="environment">The Ariva environment, vm-local by default.</param>
    /// <param name="configure">Extra web host configuration, for example test services or settings.</param>
    /// <returns>The host; dispose it at the end of the test.</returns>
    public static IArivaHost Create(string name, string environment = ArivaEnvironment.VmLocal, Action<IWebHostBuilder> configure = null) =>
        name switch
        {
            Main => new ArivaHost<Ariva.Api.Main._IAssemblyMark>(name, environment, configure),
            Ingest => new ArivaHost<Ariva.Api.Ingest._IAssemblyMark>(name, environment, configure),
            Stream => new ArivaHost<Ariva.Api.Stream._IAssemblyMark>(name, environment, configure),
            Cronz => new ArivaHost<Ariva.Api.Cronz._IAssemblyMark>(name, environment, configure),
            Integration => new ArivaHost<Ariva.Api.Integration._IAssemblyMark>(name, environment, configure),
            Simulation => new ArivaHost<Ariva.Simulation.Api._IAssemblyMark>(name, environment, configure),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown Ariva host.")
        };

    #endregion

    #region Helpers

    private static TheoryData<string> ToTheoryData(IEnumerable<string> names)
    {
        var data = new TheoryData<string>();
        foreach (var name in names)
        {
            data.Add(name);
        }

        return data;
    }

    #endregion
}

/// <summary>
/// A host booted for a test.
/// </summary>
public interface IArivaHost : IAsyncDisposable
{
    /// <summary>The host name.</summary>
    string Name { get; }

    /// <summary>The host's services; starts the host.</summary>
    IServiceProvider Services { get; }

    /// <summary>Creates a client for the host that does not follow redirects; starts the host.</summary>
    /// <returns>The client.</returns>
    HttpClient CreateClient();
}

/// <summary>
/// <see cref="IArivaHost"/> over a <see cref="WebApplicationFactory{TEntryPoint}"/>.
/// </summary>
/// <typeparam name="TEntryPoint">A type in the host assembly.</typeparam>
/// <param name="name">The host name.</param>
/// <param name="environment">The Ariva environment passed to the host as <c>--environment</c>.</param>
/// <param name="configure">Extra web host configuration.</param>
internal sealed class ArivaHost<TEntryPoint>(string name, string environment, Action<IWebHostBuilder> configure) : IArivaHost
    where TEntryPoint : class
{
    #region Fields

    private readonly ArivaWebApplicationFactory<TEntryPoint> _factory = new(environment, configure);

    #endregion

    #region IArivaHost

    public string Name => name;

    public IServiceProvider Services => _factory.Services;

    public HttpClient CreateClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    #endregion
}

/// <summary>
/// Sets the Ariva environment and applies the test's configuration.
/// </summary>
/// <typeparam name="TEntryPoint">A type in the host assembly.</typeparam>
/// <param name="environment">The Ariva environment.</param>
/// <param name="configure">Extra web host configuration.</param>
internal sealed class ArivaWebApplicationFactory<TEntryPoint>(string environment, Action<IWebHostBuilder> configure)
    : WebApplicationFactory<TEntryPoint>
    where TEntryPoint : class
{
    private string DevelopmentKeyDirectory { get; } = Path.Combine(Path.GetTempPath(), "ariva-unit-tests", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);

        // In-process hosts have no database: no development migration at startup (vm-local) and no schema check
        // (cluster environments). Persistence is covered by Ariva.IntegrationTests against a real PostgreSQL.
        builder.UseSetting("Database:AllowSchemaUpdate", "false");
        builder.UseSetting("Database:VerifySchemaOnStartup", "false");

        // No mounted certificate and no Redis in-process: a development Data Protection certificate and a memory-only
        // cache, whatever the environment under test.
        builder.UseSetting("DataProtection:CertificatePath", string.Empty);
        builder.UseSetting("DataProtection:UseDevelopmentCertificate", "true");
        builder.UseSetting("Redis:Enabled", "false");

        // No broker in-process: the bus is off and domain events stay in the outbox (ARV-020). The consume pipe is
        // covered by ConsumePipelineTests and the broker by Ariva.IntegrationTests (Testcontainers.Kafka).
        builder.UseSetting("Kafka:Enabled", "false");
        builder.UseSetting("Seed:DemoTopology", "false");

        // The mail relay is Integration's own setting (appsettings.service.<env>.json), which the shared test output
        // cannot hold for every host: give the in-process hosts vm-local's smtp4dev. Nothing connects to it here.
        builder.UseSetting("Email:Smtp:Host", "localhost");
        // The integration key ring (ARV-042): Integration's own development key, whatever the environment under test.
        builder.UseSetting("Integration:Outbound:PollAcris", "false");
        builder.UseSetting("Integration:Outbound:PollAman", "false");
        builder.UseSetting("Auth:IntegrationTokens:UseDevelopmentKeys", "true");
        builder.UseSetting("Auth:IntegrationTokens:DevelopmentKeyDirectory", DevelopmentKeyDirectory);
        builder.UseSetting("Auth:IntegrationTokens:SigningKeyPath", string.Empty);
        builder.UseSetting("Auth:IntegrationTokens:PublicKeyPaths:0", string.Empty);
        builder.UseSetting("Auth:IntegrationTokens:PublicKeyPaths:1", string.Empty);

        // No mounted token keys in-process: a development key of this host's own, whatever the environment under test
        // (cluster files name /app/secrets paths). Tests sign tokens with the host's TokenKeys.
        builder.UseSetting("Auth:Tokens:UseDevelopmentKeys", "true");
        builder.UseSetting("Auth:Tokens:DevelopmentKeyDirectory", DevelopmentKeyDirectory);
        builder.UseSetting("Auth:Tokens:SigningKeyPath", string.Empty);
        builder.UseSetting("Auth:Tokens:PublicKeyPaths:0", string.Empty);
        builder.UseSetting("Auth:Tokens:PublicKeyPaths:1", string.Empty);

        configure?.Invoke(builder);
    }
}
