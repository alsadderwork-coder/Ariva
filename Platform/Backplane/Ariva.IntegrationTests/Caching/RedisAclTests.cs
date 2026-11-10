using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ariva.Api.Main.Hubs;
using Ariva.Di.Extensions;
using Ariva.Infra.Caching;
using Ariva.Infra.Live;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;
using ZiggyCreatures.Caching.Fusion;

namespace Ariva.IntegrationTests.Caching;

/// <summary>
/// ARV-083b (CWE-269, CWE-285; ASVS 5.0 V13.2.2) on a real Redis 8 with the users <see cref="RedisAccess"/> declares, created with
/// the very ACL SETUSER lines the deployment guide lists: each host signed in as its own user runs every Redis path it has (the
/// connection's handshake and readiness ping, the FusionCache distributed level and backplane, and its live keys, channels, lease
/// and hub backplane) and the server's ACL log records no refusal of that user; each user is refused what another host's is given
/// (a live key, a live channel, a command) and what no host is given; and the cache's keys all sit under the prefix the users are
/// granted.
/// </summary>
public sealed class RedisAclTests(RedisFixture redis) : IClassFixture<RedisFixture>
{
    #region Fields

    private const string Environment = "k8s-dev";
    private static readonly LiveZoneSnapshot Visitors = new("DMO/A-VIS", new DateTime(2026, 9, 28, 18, 5, 0, DateTimeKind.Utc), 61, true, false, 15.4, 4.0, null,
        false, new DateTime(2026, 9, 28, 18, 5, 2, DateTimeKind.Utc));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    #endregion

    #region Tests

    [Fact]
    public async Task EveryHost_Should_RunItsRedisPaths_When_SignedInAsItsOwnUser()
    {
        var instance = "acl-ok-" + Guid.CreateVersion7().ToString("N")[..8] + ":";
        await using var admin = await Admin();
        var users = await CreateUsersAsync(admin, instance, "ok");
        await admin.GetServers()[0].ExecuteAsync("ACL", "LOG", "RESET");

        // Ariva.Api.Stream: the snapshots and the alert notices it announces, while Main listens.
        await using var main = Host(instance, users["main"]);
        await using var stream = Host(instance, users["stream"]);
        var zones = new ConcurrentQueue<LiveZoneSnapshot>();
        var alerts = new ConcurrentQueue<AlertNotice>();
        using var stop = new CancellationTokenSource();
        await main.GetRequiredService<ILiveSnapshotStore>().SubscribeAsync(s =>
        {
            zones.Enqueue(s);
            return Task.CompletedTask;
        }, stop.Token);
        await main.GetRequiredService<IAlertNotices>().SubscribeAsync(n =>
        {
            alerts.Enqueue(n);
            return Task.CompletedTask;
        }, stop.Token);
        await stream.GetRequiredService<ILiveSnapshotStore>().PublishAsync([Visitors], Ct);
        await stream.GetRequiredService<IAlertNotices>().PublishAsync([Notice("R-001")], Ct);
        await main.GetRequiredService<IAlertNotices>().PublishAsync([Notice("R-002")], Ct);
        await Until(() => zones.Count >= 1 && alerts.Count >= 2);
        zones.Should().ContainSingle().Which.ZoneKey.Should().Be(Visitors.ZoneKey, "Main hears Stream's snapshot");
        alerts.Select(a => a.RuleCode).Should().BeEquivalentTo(["R-001", "R-002"], "Main hears Stream's notice and its own");

        // Ariva.Api.Main: the snapshot read back (the hub, the displays), the relay's lease, the hub's SignalR backplane.
        (await main.GetRequiredService<ILiveSnapshotStore>().GetAsync(Visitors.ZoneKey, Ct)).Should().Be(Visitors);
        var connection = main.GetRequiredService<RedisConnection>();
        (await LiveRelayLease.HoldAsync(connection, "replica-a", TimeSpan.FromSeconds(15))).Should().BeTrue("a free lease is taken");
        (await LiveRelayLease.HoldAsync(connection, "replica-a", TimeSpan.FromSeconds(15))).Should().BeTrue("its own lease is extended");
        (await LiveRelayLease.HoldAsync(connection, "replica-b", TimeSpan.FromSeconds(15))).Should().BeFalse("another replica's lease is left");
        await using (var hub = HubBackplane(instance, users["main"]))
        {
            var manager = hub.GetRequiredService<HubLifetimeManager<LiveHub>>();
            await manager.SendAllAsync("zone", [Visitors], Ct);
            await manager.SendGroupAsync(LiveHub.Group(Visitors.ZoneKey), "zone", [Visitors], Ct);
        }

        // Ariva.Api.Cronz: the availability ledger reads the snapshot.
        await using (var cronz = Host(instance, users["cronz"]))
            (await cronz.GetRequiredService<ILiveSnapshotStore>().GetAsync(Visitors.ZoneKey, Ct)).Should().Be(Visitors);

        // Every host: the readiness ping and the cache's two levels, written by one replica, read by another, evicted by tag
        // through the backplane.
        var cached = new Dictionary<string, (HealthStatus Ready, int Read, bool Evicted)>(StringComparer.Ordinal);
        foreach (var host in RedisAccess.Hosts)
        {
            await using var first = Host(instance, users[host]);
            await using var second = Host(instance, users[host]);
            var ready = await Ready(first);
            var cacheA = first.GetRequiredService<IFusionCache>();
            var cacheB = second.GetRequiredService<IFusionCache>();
            var key = $"{host}:value:" + Guid.CreateVersion7().ToString("N");
            await cacheA.SetAsync(key, 42, tags: [$"zone:{host}"], token: Ct);
            var read = await cacheB.GetOrDefaultAsync<int>(key, token: Ct);
            await cacheA.RemoveByTagAsync($"zone:{host}", token: Ct);
            await Until(async () => !(await cacheB.TryGetAsync<int>(key, token: Ct)).HasValue);
            var evicted = !(await cacheB.TryGetAsync<int>(key, token: Ct)).HasValue;
            await cacheA.RemoveAsync(key, token: Ct);
            cached[host] = (ready, read, evicted);
        }

        // The backplane's channel is the one its users are granted: FusionCache's {prefix}.Backplane:{version}, and no client
        // listens on StackExchange.Redis's configuration channel (it is turned off, ARV-083b review).
        await using (var listening = Host(instance, users["ingest"]))
        {
            await listening.GetRequiredService<IFusionCache>().SetAsync("ingest:listen", 1, token: Ct);
            var channels = ((RedisResult[])await admin.GetServers()[0].ExecuteAsync("PUBSUB", "CHANNELS", "*")).Select(c => c.ToString()).ToList();
            channels.Should().Contain(c => c.StartsWith(RedisNames.BackplaneChannelPrefix(instance, Environment) + ".Backplane:", StringComparison.Ordinal),
                "FusionCache names its channel as RedisNames.BackplaneChannelPattern expects");
            channels.Should().NotContain("__Booksleeve_MasterChanged", "the client subscribes to no configuration channel");
        }

        await stop.CancelAsync();
        await Task.Delay(500, Ct);
        var refusals = await RefusalsAsync(admin, users.Values.Select(u => u.User));
        refusals.Should().BeEmpty("each host's user is granted every command, key and channel its paths use (RedisAccess)");
        foreach (var (host, (ready, read, evicted)) in cached)
        {
            ready.Should().Be(HealthStatus.Healthy, "{0}'s user may ping", host);
            read.Should().Be(42, "{0}'s second replica reads the first one's value through Redis", host);
            evicted.Should().BeTrue("{0}'s eviction reaches its other replica through the backplane", host);
        }

        var keys = await KeysAsync(admin, instance + "*");
        keys.Should().Contain(k => k.StartsWith(RedisNames.CacheKeyPrefix(instance), StringComparison.Ordinal), "the cache wrote its entries and tags");
        keys.Should().OnlyContain(k => k.StartsWith(RedisNames.CacheKeyPrefix(instance), StringComparison.Ordinal) ||
                                       k.StartsWith(instance + RedisNames.LiveSegment, StringComparison.Ordinal),
            "the cache keeps its keys under its own prefix, apart from the live keys");
    }

    [Fact]
    public async Task EachUser_Should_BeRefused_When_ItUsesWhatItsHostIsNotGranted()
    {
        var instance = "acl-no-" + Guid.CreateVersion7().ToString("N")[..8] + ":";
        await using var admin = await Admin();
        var users = await CreateUsersAsync(admin, instance, "no");
        var liveZone = RedisNames.LiveZoneKey(instance, Visitors.ZoneKey);
        var cacheKey = RedisNames.CacheKeyPrefix(instance) + "probe";
        await admin.GetDatabase().StringSetAsync(liveZone, "{}");

        // What no host is given: the whole key space, the server's configuration and data, the ACLs, another prefix.
        foreach (var host in RedisAccess.Hosts)
        {
            await using var connection = await Connect(users[host]);
            var database = connection.GetDatabase();
            foreach (var (command, args) in new (string, object[])[]
            {
                ("KEYS", ["*"]), ("SCAN", ["0"]), ("FLUSHALL", []), ("FLUSHDB", []), ("CONFIG", ["SET", "maxmemory", "1"]), ("CONFIG", ["GET", "requirepass"]),
                ("CONFIG", ["GET", "*pass*"]), ("ACL", ["LIST"]), ("GET", ["__Booksleeve_TieBreak"]), ("PUBLISH", ["__Booksleeve_MasterChanged", "x"]),
                ("PUBLISH", [RedisNames.BackplaneChannelPrefix(instance, Environment) + "2.Backplane:v2", "x"]),
                ("SCRIPT", ["FLUSH"]), ("CLIENT", ["KILL", "TYPE", "normal"]), ("HMGET", ["other:cache:probe", "data"]), ("PUBLISH", ["other:live:zones", "x"])
            })
            {
                await Refused(database, host, command, args);
            }

            (await database.ExecuteAsync("HMSET", cacheKey, "data", "1")).IsNull.Should().BeFalse("{0} writes the cache", host);

            // The hosts' own client never sends CONFIG at all (the ARV-083b review).
            await using var own = await ConnectionMultiplexer.ConnectAsync(new RedisSettings
            {
                Enabled = true, ConnectionString = redis.ConnectionString, Username = users[host].User, Password = users[host].Password
            }.Options());
            var config = () => own.GetDatabase().ExecuteAsync("CONFIG", "GET", "requirepass");
            await config.Should().ThrowAsync<RedisCommandException>("CONFIG is out of the hosts' client's command map");
        }

        // What another host is given.
        await RefusedTo("ingest", users, "GET", liveZone);
        await RefusedTo("ingest", users, "HMGET", liveZone, "data");
        await RefusedTo("ingest", users, "PUBLISH", RedisNames.LiveZonesChannel(instance), "forged");
        await RefusedTo("ingest", users, "PUBLISH", RedisNames.LiveAlertsChannel(instance), "forged");
        await RefusedTo("integration", users, "GET", liveZone);
        await RefusedTo("integration", users, "PUBLISH", RedisNames.LiveAlertsChannel(instance), "forged");
        await RefusedTo("integration", users, "PUBLISH", RedisNames.SignalRChannelPrefix(instance) + "Ariva.Api.Main.Hubs.LiveHub:all", "forged");
        await RefusedTo("cronz", users, "SET", liveZone, "forged");
        await RefusedTo("cronz", users, "HMSET", liveZone, "data", "forged");
        await RefusedTo("cronz", users, "PUBLISH", RedisNames.LiveZonesChannel(instance), "forged");
        await RefusedTo("stream", users, "GET", liveZone);
        await RefusedTo("stream", users, "HMGET", liveZone, "data");
        await RefusedTo("stream", users, "SET", RedisNames.LiveRelayKey(instance), "stream");
        await RefusedTo("stream", users, "PUBLISH", RedisNames.SignalRChannelPrefix(instance) + "Ariva.Api.Main.Hubs.LiveHub:all", "forged");
        await RefusedTo("main", users, "SET", liveZone, "forged");
        await RefusedTo("main", users, "HMSET", liveZone, "data", "forged");
    }

    #endregion

    #region Helpers

    private sealed record HostUser(string Host, string User, string Password);

    private static async Task<Dictionary<string, HostUser>> CreateUsersAsync(ConnectionMultiplexer admin, string instance, string suffix)
    {
        var server = admin.GetServers()[0];
        var users = new Dictionary<string, HostUser>(StringComparer.Ordinal);
        foreach (var grant in RedisAccess.All(instance, Environment))
        {
            var user = new HostUser(grant.Service, $"ariva-{grant.Service}-{suffix}-{instance.TrimEnd(':')}", "redis-" + grant.Service + "-" + Guid.CreateVersion7().ToString("N"));
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(user.Password)));
            // The line the deployment guide gives the site's Redis administrator, word for word.
            var words = RedisAccess.SetUser(grant, user.User, hash).Split(' ');
            words[0].Should().Be("ACL");
            await server.ExecuteAsync(words[0], [.. words.Skip(1)]);
            users[grant.Service] = user;
        }

        return users;
    }

    /// <summary>The default user's connection, without a configuration channel, so no listener of that channel is the test's own.</summary>
    private async Task<ConnectionMultiplexer> Admin()
    {
        var options = ConfigurationOptions.Parse(redis.ConnectionString);
        options.ConfigurationChannel = string.Empty;
        return await ConnectionMultiplexer.ConnectAsync(options);
    }

    private ServiceProvider Host(string instance, HostUser user)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddArivaCaching(Configuration(instance, user));
        return services.BuildServiceProvider();
    }

    /// <summary>The live hub's SignalR backplane as Ariva.Api.Main registers it: the cache's options, the hub's own channel prefix.</summary>
    private ServiceProvider HubBackplane(string instance, HostUser user)
    {
        var settings = RedisSettings.From(Configuration(instance, user));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignalR().AddStackExchangeRedis(options =>
        {
            options.Configuration = settings.Options();
            options.Configuration.ChannelPrefix = RedisChannel.Literal(RedisNames.SignalRChannelPrefix(settings.InstanceName));
        });
        return services.BuildServiceProvider();
    }

    private IConfiguration Configuration(string instance, HostUser user) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Redis:Enabled"] = "true",
            ["Redis:ConnectionString"] = redis.ConnectionString,
            ["Redis:InstanceName"] = instance,
            ["Redis:Username"] = user.User,
            ["Redis:Password"] = user.Password,
            ["Application:Environment"] = Environment
        })
        .Build();

    /// <summary>
    /// A client of the user's that sends whatever it is asked to, CONFIG included, as an attacker holding the host's credentials
    /// would: the server's ACL must refuse it (the hosts' own client, <see cref="RedisSettings.Options"/>, never sends CONFIG).
    /// </summary>
    private Task<ConnectionMultiplexer> Connect(HostUser user)
    {
        var options = ConfigurationOptions.Parse(redis.ConnectionString);
        options.User = user.User;
        options.Password = user.Password;
        options.ConfigurationChannel = string.Empty;
        options.TieBreaker = string.Empty;
        return ConnectionMultiplexer.ConnectAsync(options);
    }

    private static async Task<HealthStatus> Ready(ServiceProvider host)
    {
        var check = new RedisReadiness(host.GetRequiredService<RedisConnection>());
        var context = new HealthCheckContext { Registration = new HealthCheckRegistration("redis", check, HealthStatus.Unhealthy, null) };
        return (await check.CheckHealthAsync(context, Ct)).Status;
    }

    private async Task RefusedTo(string host, Dictionary<string, HostUser> users, string command, params object[] args)
    {
        await using var connection = await Connect(users[host]);
        await Refused(connection.GetDatabase(), host, command, args);
    }

    private static async Task Refused(IDatabase database, string host, string command, object[] args)
    {
        var run = () => database.ExecuteAsync(command, args);
        (await run.Should().ThrowAsync<RedisServerException>("{0}'s user may not run {1} {2}", host, command, string.Join(' ', args)))
            .Which.Message.Should().StartWith("NOPERM", "the ACL refuses it, not the command's own error");
    }

    /// <summary>The ACL log's refusals of these users: what each was refused, as "user reason object (context)".</summary>
    private static async Task<IReadOnlyList<string>> RefusalsAsync(ConnectionMultiplexer admin, IEnumerable<string> users)
    {
        var names = users.ToHashSet(StringComparer.Ordinal);
        var log = (RedisResult[])await admin.GetServers()[0].ExecuteAsync("ACL", "LOG", "128");
        var found = new List<string>();
        foreach (var entry in log ?? [])
        {
            var fields = (RedisResult[])entry;
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i + 1 < fields.Length; i += 2)
                values[fields[i].ToString()] = fields[i + 1].ToString();
            if (values.TryGetValue("username", out var user) && names.Contains(user))
                found.Add(string.Create(CultureInfo.InvariantCulture, $"{user} {values.GetValueOrDefault("reason")} {values.GetValueOrDefault("object")} ({values.GetValueOrDefault("context")})"));
        }

        return found;
    }

    private static async Task<IReadOnlyList<string>> KeysAsync(ConnectionMultiplexer admin, string pattern)
    {
        var keys = new List<string>();
        await foreach (var key in admin.GetServers()[0].KeysAsync(pattern: pattern))
            keys.Add(key.ToString());
        return keys;
    }

    private static AlertNotice Notice(string rule) => new(Guid.NewGuid(), "DMO", rule, "Nowcast above 15 min", "A-VIS", null, "Nowcast", "Critical", "Raised",
        new DateTime(2026, 9, 28, 18, 5, 0, DateTimeKind.Utc), 16.3, "BorderShiftSupervisor", "TerminalDutyManager", false, null, DateTime.UtcNow);

    private static Task Until(Func<bool> condition) => Until(() => Task.FromResult(condition()));

    private static async Task Until(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 100 && !await condition(); attempt++)
            await Task.Delay(100, Ct);
    }

    #endregion
}
