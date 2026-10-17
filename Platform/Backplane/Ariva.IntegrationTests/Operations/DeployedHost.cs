using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ariva.IntegrationTests.Operations;

/// <summary>
/// An Ariva host started in process as a cluster runs it (ARV-126): environment k8s-dev, its database through the runtime
/// login, the schema check at start (<c>Database:VerifySchemaOnStartup</c>, on outside vm-local) and the Data Protection
/// certificate from PEM files, as the chart mounts the ariva-dataprotection secret. Only what a test machine lacks is
/// replaced: Kafka and Redis are off (domain events stay in the outbox) and the token rings use development keys of the
/// host's own, as in the unit tests' hosts.
/// </summary>
/// <typeparam name="TEntryPoint">A type in the host assembly (its <c>_IAssemblyMark</c>).</typeparam>
internal sealed class DeployedHost<TEntryPoint>(IReadOnlyDictionary<string, string> settings) : WebApplicationFactory<TEntryPoint>
    where TEntryPoint : class
{
    public const string Environment = "k8s-dev";

    private readonly TaskCompletionSource _startGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _keyDirectory = Path.Combine(Path.GetTempPath(), "ariva-it-deployed", Guid.NewGuid().ToString("N"));

    /// <summary>A client that does not follow redirects; starts the host.</summary>
    public HttpClient Client() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environment);
        builder.UseSetting("Database:AllowSchemaUpdate", "false");
        builder.UseSetting("Database:VerifySchemaOnStartup", "true");
        builder.UseSetting("Redis:Enabled", "false");
        builder.UseSetting("Kafka:Enabled", "false");
        builder.UseSetting("Seed:DemoTopology", "false");
        builder.UseSetting("Email:Smtp:Host", "localhost");
        builder.UseSetting("Integration:Outbound:PollAcris", "false");
        builder.UseSetting("Integration:Outbound:PollAman", "false");
        foreach (var ring in new[] { "Auth:Tokens", "Auth:IntegrationTokens" })
        {
            builder.UseSetting($"{ring}:UseDevelopmentKeys", "true");
            builder.UseSetting($"{ring}:DevelopmentKeyDirectory", _keyDirectory);
            builder.UseSetting($"{ring}:SigningKeyPath", string.Empty);
            builder.UseSetting($"{ring}:PublicKeyPaths:0", string.Empty);
            builder.UseSetting($"{ring}:PublicKeyPaths:1", string.Empty);
        }

        foreach (var (key, value) in settings)
            builder.UseSetting(key, value);

        builder.ConfigureServices(services => StartGate.Hold(services, _startGate.Task));
    }

    /// <summary>
    /// As in the unit tests' hosts (ARV-156): the host's own start waits until the factory listens for it, so a start that
    /// fails (a schema check, a refused setting) surfaces as its own exception, never as a disposed service provider.
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
            _startGate.TrySetResult();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(_keyDirectory))
            Directory.Delete(_keyDirectory, recursive: true);
    }

    /// <summary>Holds <see cref="IHostLifetime.WaitForStartAsync"/> until the gate opens.</summary>
    private sealed class StartGate(IHostLifetime inner, bool ownsInner, Task gate) : IHostLifetime, IDisposable
    {
        public static void Hold(IServiceCollection services, Task gate)
        {
            var registered = services.Last(d => d.ServiceType == typeof(IHostLifetime) && !d.IsKeyedService);
            services.Remove(registered);
            services.AddSingleton<IHostLifetime>(provider => registered.ImplementationInstance is IHostLifetime instance
                ? new StartGate(instance, false, gate)
                : new StartGate((IHostLifetime)(registered.ImplementationFactory?.Invoke(provider)
                                                ?? ActivatorUtilities.CreateInstance(provider, registered.ImplementationType!)), true, gate));
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
}
