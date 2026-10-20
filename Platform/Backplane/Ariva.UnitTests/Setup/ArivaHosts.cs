using System.Net;
using Ariva.Api.Common.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

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

    /// <summary>The ingress controller's pod network a host started here in k8s-prd trusts for forwarded headers (ARV-085).</summary>
    public const string ProductionPodNetwork = "10.244.0.0/16";

    /// <summary>
    /// The collector a host started here in k8s-prd ships its logs to (ARV-091a): an https address on loopback port 9 (discard),
    /// a privileged port that the tests never bind (their listeners take free ports) and a test machine does not serve, so every
    /// batch is refused at once and no line leaves the machine. .NET never sends loopback through a proxy.
    /// </summary>
    public const string ProductionCollector = "https://127.0.0.1:9";

    /// <summary>The log pseudonym key a host started here in k8s-prd hashes with (ARV-091a): base64 of the bytes 1 to 32, the tests' own.</summary>
    public const string ProductionPseudonymKey = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=";

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

    /// <summary>
    /// As <see cref="Create"/>, but on a real Kestrel bound to a free loopback port instead of the in-memory TestServer, for what
    /// only Kestrel does: the minimum request body data rate, which TestServer has no feature for (ARV-096). The host starts on
    /// first use; <see cref="Address"/> then gives where it listens. Only Ariva.Api.Main is offered: no other test needs one.
    /// </summary>
    /// <param name="configure">Extra web host configuration, for example test services or settings.</param>
    /// <param name="environment">The Ariva environment, vm-local by default.</param>
    /// <returns>The host; dispose it at the end of the test.</returns>
    public static IArivaHost CreateMainOnKestrel(Action<IWebHostBuilder> configure = null, string environment = ArivaEnvironment.VmLocal) =>
        new ArivaHost<Ariva.Api.Main._IAssemblyMark>(Main, environment, builder =>
        {
            configure?.Invoke(builder);
            builder.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        }, kestrel: true);

    /// <summary>Where a host made by <see cref="CreateMainOnKestrel"/> listens (it starts the host).</summary>
    /// <param name="host">The host.</param>
    /// <returns>The http address on loopback.</returns>
    public static Uri Address(IArivaHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var addresses = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        return new Uri(addresses?.Addresses.FirstOrDefault() ?? throw new InvalidOperationException("The host listens nowhere: was it made with CreateMainOnKestrel?"));
    }

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
/// <param name="kestrel">True for a real Kestrel (the configuration then says where it listens), false for TestServer.</param>
internal sealed class ArivaHost<TEntryPoint>(string name, string environment, Action<IWebHostBuilder> configure, bool kestrel = false) : IArivaHost
    where TEntryPoint : class
{
    #region Fields

    private readonly ArivaWebApplicationFactory<TEntryPoint> _factory = Factory(environment, configure, kestrel);

    #endregion

    #region Helpers

    private static ArivaWebApplicationFactory<TEntryPoint> Factory(string environment, Action<IWebHostBuilder> configure, bool kestrel)
    {
        var factory = new ArivaWebApplicationFactory<TEntryPoint>(environment, configure);
        if (kestrel)
            factory.UseKestrel();
        return factory;
    }

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
    private readonly TaskCompletionSource _startGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private string DevelopmentKeyDirectory { get; } = Path.Combine(Path.GetTempPath(), "ariva-unit-tests", Guid.NewGuid().ToString("N"));

    /// <summary>A CA certificate (PEM) in the host's own temporary folder, for the pinned database CA no in-process host connects with.</summary>
    private string TestDatabaseCa()
    {
        Directory.CreateDirectory(DevelopmentKeyDirectory);
        var path = Path.Combine(DevelopmentKeyDirectory, "database-ca.crt");
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=Ariva test database CA", key,
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension(true, false, 0, true));
        using var ca = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        File.WriteAllText(path, ca.ExportCertificatePem());
        return path;
    }

    /// <summary>
    /// A pod certificate and its key (PEM) in the host's own temporary folder, for the k8s-prd https endpoint (ARV-082b) that an
    /// in-process host never binds: the host checks at startup that both files are there.
    /// </summary>
    private (string Certificate, string Key) TestPodCertificate()
    {
        Directory.CreateDirectory(DevelopmentKeyDirectory);
        var certificatePath = Path.Combine(DevelopmentKeyDirectory, "pod-tls.crt");
        var keyPath = Path.Combine(DevelopmentKeyDirectory, "pod-tls.key");
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=api-main-service", key,
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        File.WriteAllText(certificatePath, certificate.ExportCertificatePem());
        File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        return (certificatePath, keyPath);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);

        // In-process hosts have no database: no development migration at startup (vm-local) and no schema check
        // (cluster environments). Persistence is covered by Ariva.IntegrationTests against a real PostgreSQL.
        builder.UseSetting("Database:AllowSchemaUpdate", "false");
        builder.UseSetting("Database:VerifySchemaOnStartup", "false");
        // The cluster environments pin the database's internal CA (ARV-082a) and a k8s-prd host refuses to start without the
        // file; nothing connects here, so a test CA of the host's own stands in for the mounted secret.
        builder.UseSetting("Database:RootCertificate", TestDatabaseCa());
        // k8s-prd serves https from the mounted pod TLS secret (ARV-082b) and a host refuses to start without its files; the
        // in-process host binds no port, so a certificate of its own stands in. Other environments declare no endpoint.
        if (environment == ArivaEnvironment.K8sPrd)
        {
            var (certificate, key) = TestPodCertificate();
            builder.UseSetting("Kestrel:Endpoints:Https:Certificate:Path", certificate);
            builder.UseSetting("Kestrel:Endpoints:Https:Certificate:KeyPath", key);
            // k8s-prd trusts forwarded headers from the ingress controller's pod network only, which the chart passes from a Helm
            // value (ARV-085), and a host refuses to start without one: a pod network of the test's own stands in.
            builder.UseSetting($"{Ariva.Api.Common.Settings.ForwardedHeadersSettings.SectionName}:KnownNetworks:0", ArivaHosts.ProductionPodNetwork);
            // k8s-prd ships its logs over TLS to a collector and hashes with the deployment's pseudonym key from the environment
            // (ARV-091a), and a host refuses to start without either: a Loki nothing answers on, and a key of the tests' own, as
            // the chart's Loki__Enabled and Logging__PseudonymKey would give them. A test of these guards sets them otherwise.
            builder.UseSetting(Ariva.Api.Common.Logging.ArivaLogging.LokiEnabledSetting, "true");
            builder.UseSetting(Ariva.Api.Common.Logging.ArivaLogging.LokiUriSetting, ArivaHosts.ProductionCollector);
            builder.UseSetting(Ariva.Api.Common.Logging.LogProtection.KeySetting, ArivaHosts.ProductionPseudonymKey);
        }
        // Nor a database readiness check, which would answer 503 here: the probes' authorization is what these hosts
        // test; the check itself is covered by ServiceDefaultsTests (ARV-066).
        builder.ConfigureServices(services => services.PostConfigure<HealthCheckServiceOptions>(options =>
        {
            foreach (var database in options.Registrations.Where(r => r.Name == "database").ToList())
                options.Registrations.Remove(database);
        }));

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

        // Last, so it wraps whatever lifetime the host and the test leave registered (see CreateHost).
        builder.ConfigureServices(services => GatedHostLifetime.Hold(services, _startGate.Task));
    }

    /// <summary>
    /// ARV-156: the host's own start waits until the factory is listening for it. The host's entry point builds the host,
    /// hands it to the factory and goes straight on into <c>RunAsync</c>, while the factory's start (DeferredHost.StartAsync)
    /// resolves <see cref="IHostApplicationLifetime"/> from the host's services before it waits for that start. A start that
    /// fails (a host refusing its configuration) disposes those services in <c>RunAsync</c>; on a loaded machine that came
    /// first, and the test saw "Cannot access a disposed object. Object name: 'IServiceProvider'" instead of the start's own
    /// exception. Here the factory's start subscribes first (it returns at its first wait), then the gate opens, so a failed
    /// start always surfaces as its own exception. The base method's Kestrel port step has nothing to do: a host on TestServer
    /// binds no port, and one on Kestrel (<see cref="ArivaHosts.CreateMainOnKestrel"/>) names its own loopback endpoint in its
    /// configuration.
    /// </summary>
    protected override IHost CreateHost(IHostBuilder builder)
    {
        try
        {
            var host = builder.Build();
            var started = host.StartAsync();
            _startGate.TrySetResult();
            started.GetAwaiter().GetResult();
            return host;
        }
        finally
        {
            // Whatever happened, even a build that failed, nothing is left waiting at the gate.
            _startGate.TrySetResult();
        }
    }
}

/// <summary>
/// ARV-156: the host's lifetime, holding the start (<see cref="IHostLifetime.WaitForStartAsync"/>, the first step of a
/// host's start, before the start-up validators and the hosted services) until the gate opens.
/// </summary>
/// <param name="inner">The lifetime the host registered (ConsoleLifetime).</param>
/// <param name="ownsInner">Whether this wrapper created the inner lifetime, and so disposes it.</param>
/// <param name="gate">Completes when the factory is listening for the start.</param>
internal sealed class GatedHostLifetime(IHostLifetime inner, bool ownsInner, Task gate) : IHostLifetime, IDisposable
{
    /// <summary>Replaces the registered <see cref="IHostLifetime"/> with itself around it.</summary>
    /// <param name="services">The host's services.</param>
    /// <param name="gate">Completes when the factory is listening for the start.</param>
    public static void Hold(IServiceCollection services, Task gate)
    {
        var registered = services.Where(d => d.ServiceType == typeof(IHostLifetime) && !d.IsKeyedService).ToList();
        if (registered.Count == 0)
            throw new InvalidOperationException("The host registers no IHostLifetime to hold.");
        foreach (var descriptor in registered)
            services.Remove(descriptor);

        var lifetime = registered[^1];
        services.AddSingleton<IHostLifetime>(provider => lifetime.ImplementationInstance is IHostLifetime instance
            ? new GatedHostLifetime(instance, false, gate)
            : new GatedHostLifetime((IHostLifetime)(lifetime.ImplementationFactory?.Invoke(provider) ??
                                                    ActivatorUtilities.CreateInstance(provider, lifetime.ImplementationType!)), true, gate));
    }

    public async Task WaitForStartAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        await inner.WaitForStartAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => inner.StopAsync(cancellationToken);

    public void Dispose()
    {
        if (ownsInner)
            (inner as IDisposable)?.Dispose();
    }
}
