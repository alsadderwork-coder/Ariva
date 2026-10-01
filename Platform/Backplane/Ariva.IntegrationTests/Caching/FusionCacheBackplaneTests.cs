using Ariva.Di.Extensions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.Redis;
using ZiggyCreatures.Caching.Fusion;

namespace Ariva.IntegrationTests.Caching;

/// <summary>Redis container for the cache tests; Redis 8 like docker-compose.dev.yml.</summary>
public sealed class RedisFixture : IAsyncLifetime
{
    public const string Image = "redis:8.2-alpine";

    private RedisContainer _container;

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        _container = new RedisBuilder().WithImage(Image).Build();
        await _container.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }
}

/// <summary>
/// ARV-008: FusionCache as AddArivaCaching registers it, two hosts on one Redis: a value written by one host is read by
/// the other through the distributed level, and an eviction by tag on one host reaches the other's memory level
/// through the backplane.
/// </summary>
public sealed class FusionCacheBackplaneTests(RedisFixture redis) : IClassFixture<RedisFixture>
{
    [Fact]
    public async Task RemoveByTagAsync_Should_EvictOtherHostsMemoryLevel_When_BackplaneIsOn()
    {
        await using var hostA = Host();
        await using var hostB = Host();
        var cacheA = hostA.GetRequiredService<IFusionCache>();
        var cacheB = hostB.GetRequiredService<IFusionCache>();
        var key = "zone-wait:" + Guid.CreateVersion7().ToString("N");
        var ct = TestContext.Current.CancellationToken;

        await cacheA.SetAsync(key, 42, tags: ["zone:T1-ARR-A"], token: ct);
        (await cacheB.GetOrDefaultAsync<int>(key, token: ct)).Should().Be(42, "host B reads host A's value through Redis");

        await cacheA.RemoveByTagAsync("zone:T1-ARR-A", token: ct);

        var evicted = false;
        for (var attempt = 0; attempt < 50 && !evicted; attempt++)
        {
            evicted = !(await cacheB.TryGetAsync<int>(key, token: ct)).HasValue;
            if (!evicted)
                await Task.Delay(100, ct);
        }

        evicted.Should().BeTrue("the tag eviction travels to host B over the Redis backplane");
    }

    [Fact]
    public async Task AddArivaCaching_Should_ServeFromMemory_When_RedisIsDisabled()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddArivaCaching(new ConfigurationBuilder().Build());
        await using var host = services.BuildServiceProvider();
        var cache = host.GetRequiredService<IFusionCache>();
        var ct = TestContext.Current.CancellationToken;

        var value = await cache.GetOrSetAsync<int>("memory-only", _ => Task.FromResult(7), token: ct);

        value.Should().Be(7);
        cache.HasDistributedCache.Should().BeFalse();
        cache.HasBackplane.Should().BeFalse();
    }

    private ServiceProvider Host()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["Redis:Enabled"] = "true",
                ["Redis:ConnectionString"] = redis.ConnectionString,
                ["Application:Environment"] = "it"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddArivaCaching(configuration);
        return services.BuildServiceProvider();
    }
}
