using System.Text.Json;
using System.Text.Json.Serialization;
using Ariva.Infra.Caching;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Backplane.StackExchangeRedis;
using ZiggyCreatures.Caching.Fusion.Serialization.SystemTextJson;

namespace Ariva.Di.Extensions;

/// <summary>
/// FusionCache (ARV-008), AMAN's entry options: fail-safe, soft and hard factory timeouts. With Redis enabled it adds
/// the distributed level and the Redis backplane, so an eviction by tag (RemoveByTagAsync) on one pod reaches every
/// pod's memory level. Without Redis (vm-local without dev-up, E2E) it is a memory-only cache, never a null cache:
/// code paths behave the same.
/// </summary>
public static class CachingExtensions
{
    public static IServiceCollection AddArivaCaching(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var redis = RedisSettings.From(configuration);
        var environment = configuration["Application:Environment"] ?? "vm-local";
        var minutes = int.TryParse(configuration["Cache:FusionCacheInMinutes"], out var configured) && configured > 0 ? configured : 5;

        var builder = services
            .AddFusionCache()
            .WithOptions(options =>
            {
                options.CacheKeyPrefix = redis.InstanceName;
                options.BackplaneChannelPrefix = $"{redis.InstanceName}{environment}";
                options.DistributedCacheCircuitBreakerDuration = TimeSpan.FromSeconds(2);
                options.BackplaneCircuitBreakerDuration = TimeSpan.FromSeconds(2);
            })
            .WithDefaultEntryOptions(new FusionCacheEntryOptions
            {
                Duration = TimeSpan.FromMinutes(minutes),
                Priority = CacheItemPriority.High,
                IsFailSafeEnabled = true,
                FailSafeMaxDuration = TimeSpan.FromHours(1),
                FactorySoftTimeout = TimeSpan.FromMilliseconds(500),
                FactoryHardTimeout = TimeSpan.FromSeconds(30),
                AllowTimedOutFactoryBackgroundCompletion = true
            })
            .WithSerializer(new FusionCacheSystemTextJsonSerializer(new JsonSerializerOptions
            {
                ReferenceHandler = ReferenceHandler.IgnoreCycles,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            }));

        if (!redis.Enabled)
            return services;

        services.AddSingleton(redis);
        services.AddSingleton<RedisConnection>();
        builder
            .WithDistributedCache(provider =>
            {
                var connection = provider.GetRequiredService<RedisConnection>();
                return new RedisCache(new RedisCacheOptions
                {
                    InstanceName = redis.InstanceName,
                    ConnectionMultiplexerFactory = connection.GetAsync
                });
            })
            .WithBackplane(provider =>
            {
                var connection = provider.GetRequiredService<RedisConnection>();
                return new RedisBackplane(new RedisBackplaneOptions { ConnectionMultiplexerFactory = connection.GetAsync });
            });

        return services;
    }
}
