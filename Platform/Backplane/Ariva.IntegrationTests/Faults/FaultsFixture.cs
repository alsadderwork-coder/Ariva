using System.Globalization;
using Apizr;
using Apizr.Logging;
using Ariva.Infra.Timescale;
using Ariva.IntegrationTests.Caching;
using Ariva.IntegrationTests.Setup;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Networks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Refit;
using Testcontainers.Kafka;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using Testcontainers.Toxiproxy;

namespace Ariva.IntegrationTests.Faults;

/// <summary>
/// ARV-072: PostgreSQL, Redis and Kafka on one Docker network, each reached from the tests only through Toxiproxy, so a
/// test can cut a dependency off (<see cref="CutAsync"/>), stall it (<see cref="StallAsync"/>) and bring it back
/// (<see cref="RestoreAsync"/>) while the code under test keeps running. Kafka advertises the proxy's address as its
/// listener, so the clients' broker connections after bootstrap go through the proxy too. Toxiproxy is driven through
/// its HTTP API with an Apizr client (<see cref="IToxiproxyApi"/>). The schema is applied over a direct connection, not
/// the proxy.
/// </summary>
public sealed class FaultsFixture : IAsyncLifetime
{
    /// <summary>Toxiproxy by tag and digest (ARV-096; scripts/base-images.mjs checks the pin and proposes new digests).</summary>
    public const string ToxiproxyImage = "ghcr.io/shopify/toxiproxy:2.12.0@sha256:9378ed52a28bc50edc1350f936f518f31fa95f0d15917d6eb40b8e376d1a214e";

    /// <summary>
    /// With this variable set to true the fixture takes <see cref="ToxiproxyImage"/> by its tag alone. Only for machines that
    /// cannot pull ghcr.io's blobs and run a local build of the same release instead (the cloud agent sessions: built from the
    /// Go module <c>github.com/Shopify/toxiproxy/v2@v2.12.0</c>, checked against the Go checksum database, backlog/progress.md
    /// 2026-10-06), which has no registry digest. Developer machines leave it unset and pull the pinned digest; in a pipeline
    /// (<c>GITHUB_ACTIONS</c> true, as GitHub Actions sets it, or <c>TF_BUILD</c> True, as Azure DevOps sets it) the fixture refuses it
    /// with an <see cref="InvalidOperationException"/> before starting anything, so CI never runs Toxiproxy off its digest. The
    /// generic <c>CI</c> variable is not the test: local tooling sets it too (the agent sessions' gate does, for Playwright)
    /// (<see cref="ImageToStart"/>).
    /// </summary>
    public const string TagOnlyVariable = "ARIVA_TOXIPROXY_TAG_ONLY";
    private const int ApiPort = 8474;
    private const int PostgresProxyPort = 8666;
    private const int RedisProxyPort = 8667;
    private const int KafkaProxyPort = 8668;
    private const string Password = "it-faults-pass-0f1e2d";
    public const string Database = "ariva_faults";

    private INetwork _network;
    private ToxiproxyContainer _toxiproxy;
    private PostgreSqlContainer _postgres;
    private RedisContainer _redis;
    private KafkaContainer _kafka;
    private IToxiproxyApi _api;

    public string Host => _toxiproxy.Hostname;

    /// <summary>PostgreSQL through the proxy.</summary>
    public int PostgresPort => _toxiproxy.GetMappedPublicPort(PostgresProxyPort);

    public string PostgresUsername => "postgres";

    public string PostgresPassword => Password;

    /// <summary>PostgreSQL over the container's own port, for checking what was stored while the proxy is down.</summary>
    public string DirectConnectionString => new Npgsql.NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Pooling = false }.ConnectionString;

    /// <summary>Redis through the proxy, as a StackExchange.Redis configuration string.</summary>
    public string RedisConnectionString => string.Create(CultureInfo.InvariantCulture, $"{Host}:{_toxiproxy.GetMappedPublicPort(RedisProxyPort)},abortConnect=false");

    /// <summary>Kafka through the proxy (bootstrap and the advertised listener).</summary>
    public string KafkaBootstrap => string.Create(CultureInfo.InvariantCulture, $"{Host}:{_toxiproxy.GetMappedPublicPort(KafkaProxyPort)}");

    /// <summary>Kafka over the container's own listener, not the proxy, for reading the broker's view while the proxy is down.</summary>
    public string KafkaDirectBootstrap => _kafka.GetBootstrapAddress();

    /// <summary>
    /// The Toxiproxy image to start: <see cref="ToxiproxyImage"/> by its digest, or by its tag alone when <see cref="TagOnlyVariable"/>
    /// is true on a machine that is not a pipeline run.
    /// </summary>
    /// <param name="variable">Reads an environment variable by name (<see cref="Environment.GetEnvironmentVariable(string)"/>).</param>
    /// <returns>The image reference.</returns>
    /// <exception cref="InvalidOperationException">The variable is true in a pipeline (<c>GITHUB_ACTIONS</c> or <c>TF_BUILD</c> true, in any case).</exception>
    public static string ImageToStart(Func<string, string> variable)
    {
        ArgumentNullException.ThrowIfNull(variable);
        if (variable(TagOnlyVariable) != "true")
            return ToxiproxyImage;
        if (string.Equals(variable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase) || string.Equals(variable("TF_BUILD"), "true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"{TagOnlyVariable} is set in a pipeline (GITHUB_ACTIONS or TF_BUILD is true), where Toxiproxy is pulled by its pinned digest only. Unset {TagOnlyVariable} in the pipeline.");
        return ToxiproxyImage[..ToxiproxyImage.IndexOf('@', StringComparison.Ordinal)];
    }

    public async ValueTask InitializeAsync()
    {
        // First, so a refused image leaves nothing started.
        var image = ImageToStart(Environment.GetEnvironmentVariable);
        _network = new NetworkBuilder().WithName("ariva-faults-" + Guid.NewGuid().ToString("N")[..8]).Build();
        await _network.CreateAsync();

        _toxiproxy = new ToxiproxyBuilder(image)
            .WithNetwork(_network)
            .WithNetworkAliases("toxiproxy")
            .WithPortBinding(ApiPort, true)
            .WithPortBinding(PostgresProxyPort, true)
            .WithPortBinding(RedisProxyPort, true)
            .WithPortBinding(KafkaProxyPort, true)
            .Build();
        await _toxiproxy.StartAsync();
        // The address of the container this fixture started (Testcontainers' host and mapped port), nothing else.
        _api = ApizrBuilder.Current.CreateManagerFor<IToxiproxyApi>(options => options
            .WithBaseAddress(new UriBuilder(Uri.UriSchemeHttp, Host, _toxiproxy.GetMappedPublicPort(ApiPort)).Uri)
            .WithLogging(HttpTracerMode.ExceptionsOnly, HttpMessageParts.None, LogLevel.None)).Api;

        // Kafka listens inside its container on the proxy's host port and advertises it at the proxy's host address: a
        // client bootstraps through the proxy and is told to stay on it.
        var kafkaListener = _toxiproxy.GetMappedPublicPort(KafkaProxyPort);
        _postgres = new PostgreSqlBuilder()
            .WithImage(PostgresFixture.Image)
            .WithDatabase(Database)
            .WithUsername("postgres")
            .WithPassword(Password)
            .WithEnvironment("TIMESCALEDB_TELEMETRY", "off")
            .WithNetwork(_network)
            .WithNetworkAliases("postgres")
            .Build();
        _redis = new RedisBuilder().WithImage(RedisFixture.Image).WithNetwork(_network).WithNetworkAliases("redis").Build();
        _kafka = KafkaContainers.Builder()
            .WithNetwork(_network)
            .WithNetworkAliases("kafka")
            .WithListener(string.Create(CultureInfo.InvariantCulture, $"{Host}:{kafkaListener}"))
            // WithListener binds the listener to the advertised host (loopback here); it must accept the proxy's
            // connections from the network, so it binds every address and keeps advertising the proxy.
            .WithEnvironment("KAFKA_LISTENERS", string.Create(CultureInfo.InvariantCulture,
                $"PLAINTEXT://:9092,BROKER://:9093,CONTROLLER://:9094,TC-0://0.0.0.0:{kafkaListener}"))
            .Build();
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync(), _kafka.StartAsync());

        await ProxyAsync("postgres", PostgresProxyPort, "postgres:5432");
        await ProxyAsync("redis", RedisProxyPort, "redis:6379");
        await ProxyAsync("kafka", KafkaProxyPort, string.Create(CultureInfo.InvariantCulture, $"kafka:{kafkaListener}"));

        // The schema over the container's own port, so the proxy's state never matters to it.
        await new SqlScriptRunner(DirectConnectionString, NullLogger<SqlScriptRunner>.Instance).ApplyAsync(SqlScriptCatalog.Embedded());
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var container in new DotNet.Testcontainers.Containers.IContainer[] { _kafka, _redis, _postgres, _toxiproxy })
        {
            if (container is not null)
                await container.DisposeAsync();
        }

        if (_network is not null)
            await _network.DisposeAsync();
    }

    private Task ProxyAsync(string name, int listen, string upstream) =>
        _api.CreateProxyAsync(new ProxyDefinition(name, string.Create(CultureInfo.InvariantCulture, $"0.0.0.0:{listen}"), upstream, true));

    /// <summary>The dependency is unreachable: open connections are closed and new ones refused.</summary>
    public Task CutAsync(string proxy) => _api.UpdateProxyAsync(proxy, new ProxyState(false));

    /// <summary>The dependency stalls: connections stay open and nothing comes back (Toxiproxy's timeout toxic with no limit).</summary>
    public Task StallAsync(string proxy) =>
        _api.AddToxicAsync(proxy, new ToxicDefinition("stall", "timeout", "upstream", 1.0, new Dictionary<string, int> { ["timeout"] = 0 }));

    /// <summary>The dependency answers again: the proxy enabled and every toxic removed.</summary>
    public async Task RestoreAsync(string proxy)
    {
        foreach (var toxic in await _api.ToxicsAsync(proxy))
            await _api.RemoveToxicAsync(proxy, toxic.Name);
        await _api.UpdateProxyAsync(proxy, new ProxyState(true));
    }
}

/// <summary>Toxiproxy's HTTP API (https://github.com/Shopify/toxiproxy#http-api), the calls the fault tests use.</summary>
public interface IToxiproxyApi
{
    [Post("/proxies")]
    Task CreateProxyAsync([Body] ProxyDefinition proxy);

    [Post("/proxies/{name}")]
    Task UpdateProxyAsync(string name, [Body] ProxyState state);

    [Get("/proxies/{name}/toxics")]
    Task<List<ToxicDefinition>> ToxicsAsync(string name);

    [Post("/proxies/{name}/toxics")]
    Task AddToxicAsync(string name, [Body] ToxicDefinition toxic);

    [Delete("/proxies/{name}/toxics/{toxic}")]
    Task RemoveToxicAsync(string name, string toxic);
}

public sealed record ProxyDefinition(
    [property: System.Text.Json.Serialization.JsonPropertyName("name")] string Name,
    [property: System.Text.Json.Serialization.JsonPropertyName("listen")] string Listen,
    [property: System.Text.Json.Serialization.JsonPropertyName("upstream")] string Upstream,
    [property: System.Text.Json.Serialization.JsonPropertyName("enabled")] bool Enabled);

public sealed record ProxyState([property: System.Text.Json.Serialization.JsonPropertyName("enabled")] bool Enabled);

public sealed record ToxicDefinition(
    [property: System.Text.Json.Serialization.JsonPropertyName("name")] string Name,
    [property: System.Text.Json.Serialization.JsonPropertyName("type")] string Type,
    [property: System.Text.Json.Serialization.JsonPropertyName("stream")] string Stream,
    [property: System.Text.Json.Serialization.JsonPropertyName("toxicity")] double Toxicity,
    [property: System.Text.Json.Serialization.JsonPropertyName("attributes")] Dictionary<string, int> Attributes);

[CollectionDefinition(Name)]
public sealed class FaultsCollection : ICollectionFixture<FaultsFixture>
{
    public const string Name = "faults";
}
