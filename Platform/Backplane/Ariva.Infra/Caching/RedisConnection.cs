using Microsoft.Extensions.Configuration;
using StackExchange.Redis;

namespace Ariva.Infra.Caching;

/// <summary>
/// One Redis connection per process, shared by the FusionCache distributed level and its backplane (ARV-008).
/// Redis:ConnectionString uses the StackExchange.Redis format ("host:port,password=..."), parsed rather than split
/// (AMAN split on ':' and lost the password). The connection never aborts on a failed first connect: FusionCache keeps
/// serving from memory with fail-safe while Redis is away, and reconnects.
/// </summary>
public sealed class RedisConnection : IAsyncDisposable
{
    private readonly Lazy<Task<IConnectionMultiplexer>> _connection;

    public RedisConnection(RedisSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Settings = settings;
        _connection = new Lazy<Task<IConnectionMultiplexer>>(ConnectAsync, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public RedisSettings Settings { get; }

    public Task<IConnectionMultiplexer> GetAsync() => _connection.Value;

    private async Task<IConnectionMultiplexer> ConnectAsync()
    {
        var options = ConfigurationOptions.Parse(Settings.ConnectionString);
        options.AbortOnConnectFail = false;
        options.ClientName ??= "ariva";
        return await ConnectionMultiplexer.ConnectAsync(options);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection.IsValueCreated)
            await (await _connection.Value).DisposeAsync();
    }
}

/// <summary>The "Redis" section. Off in the base file (vm-local without dev-up, E2E); on in the cluster files.</summary>
public sealed class RedisSettings
{
    public bool Enabled { get; init; }
    public string ConnectionString { get; init; } = "localhost:6379";

    /// <summary>Key and channel prefix, so Ariva never collides with AMAN on a shared Redis.</summary>
    public string InstanceName { get; init; } = "ariva:";

    public static RedisSettings From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.GetSection("Redis").Get<RedisSettings>() ?? new RedisSettings();
    }
}
