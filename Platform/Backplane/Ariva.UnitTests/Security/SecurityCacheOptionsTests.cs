using System.Text.RegularExpressions;
using Ariva.Infra.Security;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using ZiggyCreatures.Caching.Fusion;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-081 (ASVS V16.5.3): a cache that feeds a security decision never answers from an expired copy when the database
/// fails or is slow; the hosts' default entry options (fail-safe, 500 ms soft timeout) would, for up to an hour.
/// </summary>
public sealed class SecurityCacheOptionsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The hosts' defaults (Ariva.Di CachingExtensions).</summary>
    private static FusionCache HostCache() => new(new FusionCacheOptions
    {
        DefaultEntryOptions = new FusionCacheEntryOptions
        {
            Duration = TimeSpan.FromMinutes(5),
            IsFailSafeEnabled = true,
            FailSafeMaxDuration = TimeSpan.FromHours(1),
            FactorySoftTimeout = TimeSpan.FromMilliseconds(500),
            FactoryHardTimeout = TimeSpan.FromSeconds(30),
            AllowTimedOutFactoryBackgroundCompletion = true
        }
    });

    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task Lookup_Should_FailRatherThanServeTheExpiredCopy_When_TheDatabaseFails()
    {
        using var cache = HostCache();
        await cache.GetOrSetAsync<string>("session:a", _ => Task.FromResult("active"), o => SecurityCacheOptions.Apply(o, Short), token: Ct);
        await Task.Delay(Short * 3, Ct);

        var lookup = async () => await cache.GetOrSetAsync<string>("session:a", _ => throw new TimeoutException("database"), o => SecurityCacheOptions.Apply(o, Short), token: Ct);

        await lookup.Should().ThrowAsync<TimeoutException>("a revoked session must not pass on an expired copy");
    }

    [Fact]
    public async Task Lookup_Should_WaitForTheDatabase_When_ItIsSlowerThanTheDefaultSoftTimeout()
    {
        using var cache = HostCache();
        await cache.GetOrSetAsync<string>("roles:a", _ => Task.FromResult("old"), o => SecurityCacheOptions.Apply(o, Short), token: Ct);
        await Task.Delay(Short * 3, Ct);

        var roles = await cache.GetOrSetAsync<string>("roles:a", async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1), token);
            return "new";
        }, o => SecurityCacheOptions.Apply(o, Short), token: Ct);

        roles.Should().Be("new", "a slow answer is waited for, never replaced by the stale one");
    }

    [Fact]
    public async Task HostDefaults_Should_ServeTheExpiredCopy_When_TheDatabaseFails()
    {
        // Why the security caches opt out: with the defaults the failure is hidden behind the old value.
        using var cache = HostCache();
        await cache.GetOrSetAsync<string>("topology:a", _ => Task.FromResult("old"), o => o.SetDuration(Short), token: Ct);
        await Task.Delay(Short * 3, Ct);

        var value = await cache.GetOrSetAsync<string>("topology:a", _ => throw new TimeoutException("database"), o => o.SetDuration(Short), token: Ct);

        value.Should().Be("old");
    }

    [Fact]
    public void Apply_Should_TurnFailSafeAndSoftTimeoutOffAndBoundTheWait()
    {
        var options = SecurityCacheOptions.Apply(new FusionCacheEntryOptions { IsFailSafeEnabled = true, FactorySoftTimeout = TimeSpan.FromMilliseconds(500) }, TimeSpan.FromMinutes(1));

        options.IsFailSafeEnabled.Should().BeFalse();
        options.FactorySoftTimeout.Should().Be(Timeout.InfiniteTimeSpan);
        options.FactoryHardTimeout.Should().Be(SecurityCacheOptions.LookupTimeout);
        options.AllowTimedOutFactoryBackgroundCompletion.Should().BeFalse();
        options.Duration.Should().Be(TimeSpan.FromMinutes(1));
    }

    /// <summary>The caches whose entries decide who may do what: sessions, roles, sites, device credentials, live zone joins.</summary>
    public static TheoryData<string> DecisionCaches => new()
    {
        "Platform/Backplane/Ariva.Infra/Security/SessionValidator.cs",
        "Platform/Backplane/Ariva.Infra/Security/StoredPermissionResolver.cs",
        "Platform/Backplane/Ariva.Infra/Security/SiteScope.cs",
        "Platform/Backplane/Ariva.Infra/Services/Sensing/SvcDeviceGateway.cs",
        "Platform/Backplane/Ariva.Infra/Live/LiveZoneDirectory.cs"
    };

    [Theory]
    [MemberData(nameof(DecisionCaches))]
    public void DecisionCache_Should_UseSecurityCacheOptions_When_ItCachesALookup(string file)
    {
        var source = File.ReadAllText(RepositoryPaths.Resolve(file));
        var lookups = Regex.Count(source, @"\.GetOrSetAsync\s*<");
        lookups.Should().BeGreaterThan(0, $"{file} caches a decision");
        Regex.Count(source, @"SecurityCacheOptions\.Apply\(").Should().Be(lookups, $"every cached lookup in {file} opts out of fail-safe (ARV-081)");
    }

    [Fact]
    public void SecurityNamespace_Should_HaveNoCachedLookupWithoutSecurityCacheOptions()
    {
        // A new cache in Ariva.Infra/Security is a decision cache until shown otherwise.
        foreach (var file in Directory.EnumerateFiles(RepositoryPaths.Resolve("Platform/Backplane/Ariva.Infra/Security"), "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            Regex.Count(source, @"SecurityCacheOptions\.Apply\(").Should().BeGreaterThanOrEqualTo(Regex.Count(source, @"\.GetOrSetAsync\s*<"), Path.GetFileName(file));
        }
    }
}
