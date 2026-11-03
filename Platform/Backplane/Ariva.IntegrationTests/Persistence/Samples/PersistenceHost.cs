using Ariva.Core.Services;
using Ariva.Di.Extensions;
using Ariva.Infra.NHibernate;
using Ariva.Infra.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.IntegrationTests.Persistence.Samples;

/// <summary>
/// The production persistence registration (AddArivaPersistence) pointed at a test database, with the sample entities
/// mapped and the recording outbox in place of the ARV-020 one. The schema is created with SchemaUpdate, which the
/// test settings allow.
/// </summary>
public sealed class PersistenceHost : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    /// <param name="settings">More settings on top (ARV-228b: a small pool and a short connection wait), or none.</param>
    public PersistenceHost(string host, int port, string database, string username, string password, IReadOnlyDictionary<string, string> settings = null)
    {
        var values = new Dictionary<string, string>
        {
            ["Database:Host"] = host,
            ["Database:Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Database:Name"] = database,
            ["Database:Username"] = username,
            ["Database:Password"] = password,
            ["Database:AllowSchemaUpdate"] = "true"
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string>())
            values[key] = value;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Outbox);
        services.AddSingleton<IDomainEventOutbox>(Outbox);
        services.AddArivaPersistence(configuration);

        // Map the sample entities instead of Ariva.Core's (registered last, so it wins).
        services.AddSingleton(provider => new NHibernateSessionFactoryProvider(
            provider.GetRequiredService<DatabaseSettings>(), [typeof(SampleZone).Assembly]));

        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public RecordingOutbox Outbox { get; } = new();

    /// <summary>The connection string the host's sessions use, so a test can take connections from the same pool.</summary>
    public string ConnectionString => _provider.GetRequiredService<DatabaseSettings>().BuildConnectionString();

    public string UpdateSchema() => _provider.GetRequiredService<NHibernateSessionFactoryProvider>().UpdateSchema();

    /// <summary>Runs <paramref name="work"/> in a fresh scope (one unit of work) and ends the unit afterwards.</summary>
    public async Task<T> InScopeAsync<T>(Func<IUnitOfWork, ICurrentUser, Task<T>> work)
    {
        await using var scope = _provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var currentUser = scope.ServiceProvider.GetRequiredService<ICurrentUser>();
        var result = await work(unitOfWork, currentUser);
        await unitOfWork.EndAsync();
        return result;
    }

    public ValueTask DisposeAsync() => _provider.DisposeAsync();
}
