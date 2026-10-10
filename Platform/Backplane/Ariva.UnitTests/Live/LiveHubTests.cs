using System.Security.Claims;
using Ariva.Api.Main.Hubs;
using Ariva.Core;
using Ariva.Core.Queueing;
using Ariva.Core.Security;
using Ariva.Infra.Live;
using Ariva.Infra.Security;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Ariva.Api.Common.Hubs;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ariva.UnitTests.Live;

/// <summary>
/// ARV-035: the live hub's joins (a zone of a site the caller holds, with LiveQueue.View, at most 64 zones), the
/// snapshot checks on what comes back from Redis, and the access token read from the query string on the hubs only.
/// </summary>
public sealed class LiveHubTests
{
    private static readonly Guid UserId = Guid.Parse("01a0f000-0000-7000-8000-000000000001");

    private sealed class Caller(ClaimsPrincipal user) : HubCallerContext
    {
        public override string ConnectionId { get; } = "c-1";
        public override string UserIdentifier => user.FindFirst(ArivaClaims.Subject)?.Value;
        public override ClaimsPrincipal User { get; } = user;
        public override IDictionary<object, object> Items { get; } = new Dictionary<object, object>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;

        public override void Abort()
        {
        }
    }

    private sealed class Groups : IGroupManager
    {
        public List<string> Joined { get; } = [];

        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            Joined.Add(groupName);
            return Task.CompletedTask;
        }

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            Joined.Remove(groupName);
            return Task.CompletedTask;
        }
    }

    private sealed class Grants(params Permission[] granted) : IPermissionResolver
    {
        public Task<IReadOnlySet<Permission>> GetPermissionsAsync(ClaimsPrincipal user, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<Permission>>(new HashSet<Permission>(granted));
    }

    private sealed class Sites(SiteAccess access) : SiteAccessResolver(null!, null!)
    {
        public override Task<SiteAccess> ForUserAsync(Guid userId, CancellationToken ct = default) => Task.FromResult(access);
    }

    private sealed class Held(params string[] roles) : Ariva.Infra.Security.UserRoles(null!)
    {
        public override Task<IReadOnlyList<string>> ForUserAsync(Guid userId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>(roles);
    }

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class Directory(params string[] zones) : LiveZoneDirectory(null!, null!)
    {
        public override Task<bool> ContainsAsync(string zoneKey, CancellationToken ct) => Task.FromResult(zones.Length == 0 || zones.Contains(zoneKey));
    }

    private sealed class Store : ILiveSnapshotStore
    {
        public Dictionary<string, LiveZoneSnapshot> Kept { get; } = [];

        public Task PublishAsync(IReadOnlyCollection<LiveZoneSnapshot> snapshots, CancellationToken ct) => Task.CompletedTask;

        public Task<LiveZoneSnapshot> GetAsync(string zoneKey, CancellationToken ct) => Task.FromResult(Kept.GetValueOrDefault(zoneKey));

        public Task SubscribeAsync(Func<LiveZoneSnapshot, Task> handler, CancellationToken ct) => Task.CompletedTask;
    }

    private static readonly LiveZoneSnapshot Visitors = new("DMO/A-VIS", new DateTime(2026, 9, 28, 18, 5, 0, DateTimeKind.Utc), 61, true, false, 15.4, 4.0, null,
        false, new DateTime(2026, 9, 28, 18, 5, 2, DateTimeKind.Utc));

    private static (LiveHub Hub, Groups Groups) Hub(Permission[] granted, SiteAccess access, Store store = null, Directory zones = null,
        TimeProvider clock = null, HubCallerContext caller = null, string[] roles = null)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ArivaClaims.Subject, UserId.ToString())], "test"));
        var groups = new Groups();
        var hub = new LiveHub(store ?? new Store(), new Grants(granted), new Sites(access), zones ?? new Directory(), new Held(roles ?? [RoleCodes.BorderShiftSupervisor]),
            clock ?? TimeProvider.System, NullLogger<LiveHub>.Instance)
        {
            Context = caller ?? new Caller(user),
            Groups = groups
        };
        return (hub, groups);
    }

    private static readonly Permission[] Live = [Global.Defaults.Permissions.ViewLiveQueue];
    private static readonly SiteAccess Dmo = new(false, new HashSet<string> { "DMO" });

    private static readonly Permission[] AlertsSeen = [Global.Defaults.Permissions.ViewLiveQueue, Global.Defaults.Permissions.ViewAlert];

    [Fact]
    public async Task JoinAlerts_Should_JoinTheGroupsOfTheCallersRoles_When_TheCallerSeesTheSitesAlerts()
    {
        var (hub, groups) = Hub(AlertsSeen, Dmo, roles: [RoleCodes.BorderShiftSupervisor, RoleCodes.TerminalDutyManager]);

        (await hub.JoinAlerts("DMO")).Should().Be(2);

        groups.Joined.Should().BeEquivalentTo("alerts:DMO:BorderShiftSupervisor", "alerts:DMO:TerminalDutyManager");
        await hub.LeaveAlerts("DMO");
        groups.Joined.Should().BeEmpty();
    }

    [Fact]
    public async Task JoinAlerts_Should_RefuseTheSameWay_When_TheGrantSiteOrRolesAreMissing()
    {
        foreach (var (hub, groups, site) in new[]
                 {
                     Add(Hub(Live, Dmo), "DMO"), Add(Hub(AlertsSeen, Dmo), "AUH"), Add(Hub(AlertsSeen, SiteAccess.None), "DMO"),
                     Add(Hub(AlertsSeen, Dmo, roles: []), "DMO")
                 })
        {
            var join = () => hub.JoinAlerts(site);
            (await join.Should().ThrowAsync<HubException>()).WithMessage("forbidden");
            groups.Joined.Should().BeEmpty();
        }

        var (any, _) = Hub(AlertsSeen, Dmo);
        foreach (var bad in new[] { "dmo", "DMO/A-VIS", "", "DMO\n" })
            (await ((Func<Task>)(() => any.JoinAlerts(bad))).Should().ThrowAsync<HubException>()).WithMessage("invalid_site");

        static (LiveHub, Groups, string) Add((LiveHub Hub, Groups Groups) h, string site) => (h.Hub, h.Groups, site);
    }

    [Fact]
    public void Notices_Should_ReachOnlyTheResponsibleRoles_When_Routed()
    {
        var notice = new AlertNotice(Guid.NewGuid(), "DMO", "R-001", "Nowcast above 15 min", "A-VIS", null, "Nowcast", "Critical", "Raised",
            new DateTime(2026, 9, 28, 18, 5, 0, DateTimeKind.Utc), 16.3, RoleCodes.BorderShiftSupervisor, RoleCodes.TerminalDutyManager, false, null,
            new DateTime(2026, 9, 28, 18, 5, 1, DateTimeKind.Utc));

        notice.Audience().Should().BeEquivalentTo(RoleCodes.BorderShiftSupervisor, RoleCodes.SystemAdministrator);
        (notice with { Escalated = true }).Audience().Should().BeEquivalentTo(RoleCodes.BorderShiftSupervisor, RoleCodes.TerminalDutyManager, RoleCodes.SystemAdministrator);
        // No owner: every alert role of the site, named here so a change to RoleCodes.AlertRoles cannot widen it unnoticed; the
        // validation observer holds no alert permission and never receives one (ARV-104a, CWE-269, CWE-863).
        var unowned = (notice with { OwnerRole = null }).Audience();
        unowned.Should().BeEquivalentTo(
            [RoleCodes.BorderShiftSupervisor, RoleCodes.TerminalDutyManager, RoleCodes.HandlerStationManager, RoleCodes.SystemAdministrator]);
        unowned.Should().NotContain(RoleCodes.ValidationObserver);
        (notice with { Escalated = true }).Audience().Should().NotContain(RoleCodes.ValidationObserver);
        AlertNotice.Plausible(notice).Should().BeTrue();
        foreach (var bad in new[]
                 {
                     notice with { SiteCode = "dmo" }, notice with { RuleName = "x\u202E" }, notice with { OwnerRole = "Root" }, notice with { State = "Raised,Resolved" },
                     notice with { OwnerRole = RoleCodes.ValidationObserver }, notice with { EscalateToRole = RoleCodes.ValidationObserver },
                     notice with { RaisedValue = double.NaN }, notice with { RaisedValue = 2e9 }, notice with { DeviceCode = "s 17" }, notice with { AlertId = Guid.Empty },
                     notice with { ZoneName = new string('z', 201) }, notice with { RuleCode = "X-1" }
                 })
            AlertNotice.Plausible(bad).Should().BeFalse(bad.ToString());
    }

    [Fact]
    public async Task JoinZone_Should_JoinAndReturnTheLatestSnapshot_When_TheCallerHoldsTheSite()
    {
        var store = new Store();
        store.Kept["DMO/A-VIS"] = Visitors;
        var (hub, groups) = Hub(Live, Dmo, store);

        var snapshot = await hub.JoinZone("DMO/A-VIS");

        snapshot.Should().Be(Visitors);
        groups.Joined.Should().Equal("zone:DMO/A-VIS");
        await hub.LeaveZone("DMO/A-VIS");
        groups.Joined.Should().BeEmpty();
    }

    [Fact]
    public async Task JoinZone_Should_RefuseTheSameWay_When_TheSiteIsNotTheCallersOrTheGrantIsMissing()
    {
        var (otherSite, otherGroups) = Hub(Live, Dmo);
        var (noGrant, noGrantGroups) = Hub([Global.Defaults.Permissions.ViewDevice], Dmo);
        var (noSites, _) = Hub(Live, SiteAccess.None);

        foreach (var (hub, zone) in new[] { (otherSite, "AUH/T1-ARR"), (noGrant, "DMO/A-VIS"), (noSites, "DMO/A-VIS") })
        {
            Func<Task> join = () => hub.JoinZone(zone);
            (await join.Should().ThrowAsync<HubException>()).Which.Message.Should().Be("forbidden", "another site's zone and an unknown zone look alike");
        }

        otherGroups.Joined.Should().BeEmpty();
        noGrantGroups.Joined.Should().BeEmpty();
        var (everything, everywhere) = Hub(Live, SiteAccess.Everything);
        await everything.JoinZone("AUH/T1-ARR");
        everywhere.Joined.Should().Equal("zone:AUH/T1-ARR");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("DMO")]
    [InlineData("dmo/A-VIS")]
    [InlineData("DMO/")]
    [InlineData("DMO/A\u0000VIS")]
    [InlineData("DMO//A-VIS")]
    [InlineData("TOO-LONG-SITE-CODE/A-VIS")]
    [InlineData("DMO/A-VIS\n")]
    [InlineData("DMO/A-VIS\r\n")]
    public async Task JoinZone_Should_RefuseAKey_When_ItIsNotAZoneKey(string zoneKey)
    {
        var (hub, groups) = Hub(Live, SiteAccess.Everything);

        Func<Task> join = () => hub.JoinZone(zoneKey);

        (await join.Should().ThrowAsync<HubException>()).Which.Message.Should().Be("invalid_zone");
        groups.Joined.Should().BeEmpty();
    }

    [Fact]
    public async Task JoinZone_Should_StopAtSixtyFourZones_When_AConnectionJoinsMore()
    {
        var (hub, groups) = Hub(Live, Dmo);
        for (var k = 0; k < LiveHub.MaxGroups; k++)
            await hub.JoinZone($"DMO/Z{k}");
        await hub.JoinZone("DMO/Z0");

        Func<Task> more = () => hub.JoinZone("DMO/Z64");

        (await more.Should().ThrowAsync<HubException>()).Which.Message.Should().Be("too_many_zones");
        groups.Joined.Distinct().Should().HaveCount(LiveHub.MaxGroups);
    }

    [Fact]
    public async Task JoinZone_Should_RefuseAnUnknownZone_When_ItIsNotInTheSitesPublishedProfile()
    {
        var (hub, groups) = Hub(Live, Dmo, zones: new Directory("DMO/A-VIS"));

        Func<Task> unknown = () => hub.JoinZone("DMO/INVENTED");

        (await unknown.Should().ThrowAsync<HubException>()).Which.Message.Should().Be("forbidden", "an unknown zone looks like another site's");
        groups.Joined.Should().BeEmpty();
        await hub.JoinZone("DMO/A-VIS");
        groups.Joined.Should().Equal("zone:DMO/A-VIS");
    }

    [Fact]
    public async Task JoinZone_Should_LimitJoinsPerMinute_When_AConnectionChurnsJoinsAndLeaves()
    {
        var clock = new Clock(new DateTimeOffset(2026, 9, 28, 18, 0, 0, TimeSpan.Zero));
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ArivaClaims.Subject, UserId.ToString())], "test"));
        var caller = new Caller(user);
        for (var k = 0; k < LiveHub.MaxJoinsPerMinute; k++)
        {
            var (each, _) = Hub(Live, Dmo, clock: clock, caller: caller);
            await each.JoinZone("DMO/A-VIS");
            await each.LeaveZone("DMO/A-VIS");
        }

        var (hub, groups) = Hub(Live, Dmo, clock: clock, caller: caller);
        Func<Task> more = () => hub.JoinZone("DMO/A-VIS");

        (await more.Should().ThrowAsync<HubException>()).Which.Message.Should().Be("too_many_joins");
        groups.Joined.Should().BeEmpty();
        clock.Advance(TimeSpan.FromMinutes(1));
        await hub.JoinZone("DMO/A-VIS");
        groups.Joined.Should().Equal("zone:DMO/A-VIS");
    }

    [Fact]
    public void Snapshot_Should_BeDropped_When_WhatComesBackFromRedisIsNotPlausible()
    {
        RedisLiveSnapshots.Read(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(Visitors, Ariva.Infra.Messaging.EventCatalog.Json)).Should().Be(Visitors);
        foreach (var bad in new[]
                 {
                     Visitors with { ZoneKey = "not a key" }, Visitors with { QueueLength = -1 }, Visitors with { NowcastMinutes = 1e9 },
                     Visitors with { NowcastMinutes = -2 }, Visitors with { NoService = "Whatever" }, Visitors with { MinuteUtc = DateTime.MinValue }
                 })
            RedisLiveSnapshots.Read(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(bad, Ariva.Infra.Messaging.EventCatalog.Json)).Should().BeNull();

        RedisLiveSnapshots.Read("{not json").Should().BeNull();
        RedisLiveSnapshots.Read(new string('x', RedisLiveSnapshots.MaxBytes + 1)).Should().BeNull();
        Ariva.Infra.Streaming.LiveMinuteSnapshots.From(new QueueLiveMinute("DMO/A-VIS", Visitors.MinuteUtc, 61, true, false, 15.4, 4.0, NoServiceReason.NothingOpen, false), Visitors.PublishedUtc)
            .NoService.Should().Be("NothingOpen");
    }

    private static IArivaHost Main(bool granted)
    {
        var app = ArivaHosts.Create(ArivaHosts.Main, configure: builder => builder.ConfigureTestServices(services =>
        {
            // No database in-process: sessions are active and the stored grants are the test's.
            services.Replace(ServiceDescriptor.Singleton<ISessionValidator>(new FakeSessionValidator()));
            services.Replace(ServiceDescriptor.Scoped<IPermissionResolver>(_ =>
                new Grants(granted ? Live : [Global.Defaults.Permissions.ViewDevice])));
        }));
        return app;
    }

    private static string Token(IServiceProvider services) => services.GetRequiredService<AccessTokenIssuer>()
        .Issue(Guid.CreateVersion7(), "officer.one", Guid.CreateVersion7(), Guid.CreateVersion7(), DateTime.UtcNow, ["pwd"], false);

    [Fact]
    public async Task Hub_Should_AdmitOnlyHoldersOfLiveQueueView_When_TheyNegotiateOrOpenTheWebSocket()
    {
        var ct = TestContext.Current.CancellationToken;
        foreach (var granted in new[] { true, false })
        {
            await using var app = Main(granted);
            using var client = app.CreateClient();
            var token = Token(app.Services);
            var expected = granted ? 200 : 403;

            using var byQuery = await client.PostAsync($"{LiveHub.Path}/negotiate?negotiateVersion=1&access_token={token}", null, ct);
            using var byHeader = new HttpRequestMessage(HttpMethod.Post, $"{LiveHub.Path}/negotiate?negotiateVersion=1");
            byHeader.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var header = await client.SendAsync(byHeader, ct);
            using var socket = await client.GetAsync($"{LiveHub.Path}?access_token={token}", ct);

            ((int)byQuery.StatusCode).Should().Be(expected, $"negotiate with the query token, granted {granted}");
            ((int)header.StatusCode).Should().Be(expected, $"negotiate with the header, granted {granted}");
            if (granted)
                ((int)socket.StatusCode).Should().NotBe(401).And.NotBe(403, "the connection request passes authorisation (then needs a WebSocket)");
            else
                ((int)socket.StatusCode).Should().Be(403, "the connection request itself is refused without the grant");
        }
    }

    [Fact]
    public async Task Hub_Should_IgnoreTheQueryToken_When_ItIsRepeatedOrOutsideTheHubs()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = Main(granted: true);
        using var client = app.CreateClient();
        var token = Token(app.Services);

        using var none = await client.PostAsync($"{LiveHub.Path}/negotiate?negotiateVersion=1", null, ct);
        using var twice = await client.PostAsync($"{LiveHub.Path}/negotiate?negotiateVersion=1&access_token={token}&access_token={token}", null, ct);
        using var api = await client.GetAsync($"/api/v1/system/info?access_token={token}", ct);

        ((int)none.StatusCode).Should().Be(401);
        ((int)twice.StatusCode).Should().Be(401, "a repeated access_token is refused rather than guessed");
        ((int)api.StatusCode).Should().Be(401, "elsewhere a query token is ignored");
    }

    [Fact]
    public async Task Main_Should_CloseHubConnectionsOfEndedSessions_When_TheLiveHubIsMapped()
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Main);

        // AddFilter<T> stores a factory that names the filter type in a private field.
        static Type FilterType(IHubFilter filter) => filter as object is SessionHubFilter ? typeof(SessionHubFilter) :
            filter.GetType().GetField("_filterType", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(filter) as Type;
        // HubFilters is internal; the hub's options copy the global filters (HubOptionsSetup<THub>).
        var property = typeof(HubOptions).GetProperty("HubFilters", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var filters = new[] { (HubOptions)app.Services.GetRequiredService<IOptions<HubOptions<LiveHub>>>().Value, app.Services.GetRequiredService<IOptions<HubOptions>>().Value }
            .SelectMany(o => property.GetValue(o) as IEnumerable<IHubFilter> ?? []).ToList();
        filters.Should().NotBeEmpty();
        filters.Select(FilterType).Should().Contain(typeof(SessionHubFilter), "a revoked session must lose its live connections within 5 seconds (ADR-0026)");
        app.Services.GetServices<IHostedService>().Should().Contain(s => s is HubSessionSweeper);
        app.Services.GetServices<IHostedService>().Should().Contain(s => s is LiveRelay);
    }
}
