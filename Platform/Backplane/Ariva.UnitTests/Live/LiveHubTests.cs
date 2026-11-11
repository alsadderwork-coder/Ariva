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
using Microsoft.AspNetCore.Hosting;
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

        // Every removal the hub passes on to the group manager (the Redis backplane in a cluster), in order.
        public List<string> Removed { get; } = [];

        // Every add ("+group") and removal ("-group") in the order the hub made them.
        public List<string> Log { get; } = [];

        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            Log.Add("+" + groupName);
            Joined.Add(groupName);
            return Task.CompletedTask;
        }

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            Log.Add("-" + groupName);
            Removed.Add(groupName);
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

    /// <summary>The border-only metrics by their stored names (ARV-181b2, ARV-187), pinned by <c>BorderAlertsTests</c>.</summary>
    public static TheoryData<string> BorderMetrics => [.. Ariva.Core.Security.BorderAlerts.MetricNames];

    /// <summary>
    /// ARV-181b2 and ARV-187 (CWE-863, CWE-200, data boundary): a notice of open desks below recommendation or of the
    /// predicted nowcast reaches the alert groups of border roles only (the border shift supervisor and the administrator),
    /// whatever its owner and escalation roles say: without an owner (a rule from before the story, or a row changed in the
    /// database) it is not every alert role, owned by the terminal duty manager it reaches the administrator alone, and
    /// escalated to an airport-side role it leaves that role out.
    /// </summary>
    [Theory]
    [MemberData(nameof(BorderMetrics))]
    public void Notices_Should_ReachBorderRolesOnly_When_TheMetricIsBorderOnly(string borderMetric)
    {
        var notice = new AlertNotice(Guid.NewGuid(), "DMO", "R-006", "Border alert", "A-VIS", null, borderMetric, "Warning", "Raised",
            new DateTime(2026, 10, 15, 10, 9, 0, DateTimeKind.Utc), 3, RoleCodes.BorderShiftSupervisor, null, false, null, new DateTime(2026, 10, 15, 10, 9, 1, DateTimeKind.Utc));

        AlertNotice.Plausible(notice).Should().BeTrue();
        notice.Audience().Should().Equal(RoleCodes.BorderShiftSupervisor, RoleCodes.SystemAdministrator);
        (notice with { OwnerRole = null }).Audience().Should().Equal([RoleCodes.BorderShiftSupervisor, RoleCodes.SystemAdministrator], "no owner is not every alert role");
        (notice with { OwnerRole = RoleCodes.TerminalDutyManager }).Audience().Should().Equal([RoleCodes.SystemAdministrator], "a changed row's airport-side owner is left out");
        (notice with { OwnerRole = RoleCodes.HandlerStationManager, EscalateToRole = RoleCodes.TerminalDutyManager, Escalated = true }).Audience()
            .Should().Equal([RoleCodes.SystemAdministrator], "an airport-side escalation role is left out");
        (notice with { EscalateToRole = RoleCodes.TerminalDutyManager, Escalated = true }).Audience()
            .Should().Equal([RoleCodes.BorderShiftSupervisor, RoleCodes.SystemAdministrator]);
        foreach (var metric in Enum.GetNames<Ariva.Core.Domain.Enums.AlertMetric>().Where(m => !Ariva.Core.Security.BorderAlerts.IsBorderOnly(m)))
            (notice with { Metric = metric, OwnerRole = null }).Audience().Should().Contain(RoleCodes.TerminalDutyManager, metric + " stays every alert role's without an owner");
        Enum.GetNames<Ariva.Core.Domain.Enums.AlertMetric>().Count(m => !Ariva.Core.Security.BorderAlerts.IsBorderOnly(m)).Should().Be(8, "two border metrics of ten");
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
    public async Task LeaveZone_Should_RemoveOnlyAGroupTheConnectionJoined_When_TheKeyDiffersInLetterCaseOrWasNeverJoined()
    {
        // ARV-297 (GHSA-wr8v-9x5q-ggcm): SignalR's Redis backplane up to 10.0.14 mishandled a group removal whose name differs
        // in letter case from the joined group's, leaving subscriptions that grow; a zone key's name part takes any letter
        // case, so the hub passes on only the removal of a group this connection joined, by its exact key.
        var (hub, groups) = Hub(Live, Dmo);
        await hub.JoinZone("DMO/A-VIS");

        await hub.LeaveZone("DMO/a-vis");
        await hub.LeaveZone("DMO/A-Vis");
        await hub.LeaveZone("DMO/B-VIS");

        groups.Removed.Should().BeEmpty("a key the connection did not join, in any letter case, never reaches the group manager");
        groups.Joined.Should().Equal("zone:DMO/A-VIS");
        await hub.LeaveZone("DMO/A-VIS");
        groups.Removed.Should().Equal("zone:DMO/A-VIS");
        groups.Joined.Should().BeEmpty();
        await hub.LeaveZone("DMO/A-VIS");
        groups.Removed.Should().Equal(["zone:DMO/A-VIS"], "a zone left once is not removed again");
        await hub.JoinZone("DMO/A-VIS");
        await hub.LeaveZone("DMO/A-VIS");
        groups.Removed.Should().Equal(["zone:DMO/A-VIS", "zone:DMO/A-VIS"], "a zone joined again is left again");
    }

    [Fact]
    public async Task JoinZone_Should_LeaveTheEarlierKeyFirst_When_TheZoneWasRenamedInLetterCaseOnly()
    {
        // ARV-297: a zone renamed in letter case alone and published again; the connection joined the old key. Holding both
        // keys would let a later leave name one group while the backplane matched it to the other without regard to case.
        var (hub, groups) = Hub(Live, Dmo);
        await hub.JoinZone("DMO/A-VIS");

        await hub.JoinZone("DMO/a-vis");

        groups.Removed.Should().Equal(["zone:DMO/A-VIS"], "the earlier key's group is left by its exact name");
        groups.Log.Should().Equal(["+zone:DMO/A-VIS", "-zone:DMO/A-VIS", "+zone:DMO/a-vis"],
            "the earlier group is left before the new one is joined: a backplane that matches groups without regard to case would take the add as a repeat and the later removal as the end of both");
        groups.Joined.Should().Equal("zone:DMO/a-vis");
        await hub.LeaveZone("DMO/A-VIS");
        groups.Removed.Should().Equal(["zone:DMO/A-VIS"], "the earlier key is no longer the connection's");
        await hub.LeaveZone("DMO/a-vis");
        groups.Removed.Should().Equal(["zone:DMO/A-VIS", "zone:DMO/a-vis"]);
        groups.Joined.Should().BeEmpty();
    }

    [Fact]
    public async Task JoinZone_Should_CountTheRenamedZoneOnce_When_TheConnectionHoldsItsMostZones()
    {
        // ARV-297: the earlier key's place is the renamed key's, so a connection at the limit can follow the rename.
        var (hub, groups) = Hub(Live, Dmo);
        for (var k = 0; k < LiveHub.MaxGroups; k++)
            await hub.JoinZone($"DMO/Z{k}");

        await hub.JoinZone("DMO/z0");

        groups.Joined.Should().HaveCount(LiveHub.MaxGroups).And.Contain("zone:DMO/z0").And.NotContain("zone:DMO/Z0");
        Func<Task> more = () => hub.JoinZone("DMO/Z64");
        (await more.Should().ThrowAsync<HubException>()).Which.Message.Should().Be("too_many_zones");
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

    [Fact]
    public void Snapshot_Should_CarryUnknownWithNothingMeasured_When_TheMinuteHadNoMeasurement()
    {
        // ARV-259e2 (formulas F11): a minute in which no live device covered the zone's lines reaches the screens as Unknown, with no
        // nowcast, throughput or no-service reason, and both flags set even if the engine's minute left one unset.
        static byte[] Json(object value) => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value, Ariva.Infra.Messaging.EventCatalog.Json);
        var minute = new QueueLiveMinute("DMO/A-VIS", Visitors.MinuteUtc, 61, true, false, 15.4, 4.0, NoServiceReason.NothingOpen, false) { Unknown = true };

        var unknown = Ariva.Infra.Streaming.LiveMinuteSnapshots.From(minute, Visitors.PublishedUtc);

        unknown.Should().Be(new LiveZoneSnapshot("DMO/A-VIS", Visitors.MinuteUtc, 61, true, true, null, null, null, true, Visitors.PublishedUtc, Unknown: true));
        LiveZones.Plausible(unknown).Should().BeTrue();
        RedisLiveSnapshots.Read(Json(unknown)).Should().Be(unknown, "Unknown survives the round trip through Redis");
        Ariva.Infra.Streaming.LiveMinuteSnapshots.From(minute with { Unknown = false }, Visitors.PublishedUtc).Unknown.Should().BeFalse();

        foreach (var (bad, because) in new (LiveZoneSnapshot, string)[]
                 {
                     (unknown with { NowcastMinutes = 3 }, "a nowcast"), (unknown with { ThroughputPerMinute = 1 }, "a throughput"),
                     (unknown with { NoService = "NothingOpen" }, "a no-service reason"), (unknown with { LengthDegraded = false }, "a length not flagged"),
                     (unknown with { NowcastDegraded = false }, "a nowcast not flagged")
                 })
        {
            LiveZones.Plausible(bad).Should().BeFalse("an Unknown minute has no measurement, so {0} comes with nothing", because);
            RedisLiveSnapshots.Read(Json(bad)).Should().BeNull(because);
        }

        // A snapshot written before ARV-259e2 has no unknown member: it reads as a measured one.
        var older = System.Text.Json.JsonSerializer.SerializeToNode(Visitors, Ariva.Infra.Messaging.EventCatalog.Json)!.AsObject();
        older.Remove("unknown").Should().BeTrue();
        RedisLiveSnapshots.Read(older.ToJsonString()).Should().Be(Visitors);
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

    private static string Token(IServiceProvider services) => Token(services, Guid.CreateVersion7());

    private static string Token(IServiceProvider services, Guid userId) => services.GetRequiredService<AccessTokenIssuer>()
        .Issue(userId, "officer.one", Guid.CreateVersion7(), Guid.CreateVersion7(), DateTime.UtcNow, ["pwd"], false);

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
    public async Task Negotiate_Should_Answer429WithRetryAfter_When_TheReplicaHoldsItsMostConnections()
    {
        // ARV-096b: the replica's total (Security:Hubs:MaxConnections, here the least, 10), after authorization: an anonymous caller
        // is still told 401, a holder of the grant 429 with Retry-After until a connection closes.
        var ct = TestContext.Current.CancellationToken;
        await using var app = ArivaHosts.Create(ArivaHosts.Main, configure: builder =>
        {
            builder.UseSetting("Security:Hubs:MaxConnections", "10");
            builder.ConfigureTestServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton<ISessionValidator>(new FakeSessionValidator()));
                services.Replace(ServiceDescriptor.Scoped<IPermissionResolver>(_ => new Grants(Live)));
            });
        });
        using var client = app.CreateClient();
        var token = Token(app.Services);
        var registry = app.Services.GetRequiredService<HubSessionRegistry>();
        var held = Enumerable.Range(0, 10).Select(_ => new HeldConnection()).ToList();
        held.ForEach(connection => registry.Admit(null, connection).Should().Be(HubAdmission.Admitted));

        using var anonymous = await client.PostAsync($"{LiveHub.Path}/negotiate?negotiateVersion=1", null, ct);
        using var full = new HttpRequestMessage(HttpMethod.Post, $"{LiveHub.Path}/negotiate?negotiateVersion=1");
        full.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var refused = await client.SendAsync(full, ct);
        registry.Remove(held[0].ConnectionId);
        using var room = new HttpRequestMessage(HttpMethod.Post, $"{LiveHub.Path}/negotiate?negotiateVersion=1");
        room.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var admitted = await client.SendAsync(room, ct);

        registry.MaxConnections.Should().Be(10, "the setting reaches the registry");
        ((int)anonymous.StatusCode).Should().Be(401);
        ((int)refused.StatusCode).Should().Be(429);
        refused.Headers.RetryAfter.Should().NotBeNull();
        ((int)admitted.StatusCode).Should().Be(200, "a closed connection frees its place");
    }

    [Theory]
    [InlineData("9")]
    [InlineData("20001")]
    public async Task Main_Should_RefuseToStart_When_TheHubsTotalIsOutOfBounds(string maxConnections)
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Main, configure: builder => builder.UseSetting("Security:Hubs:MaxConnections", maxConnections));

        var start = () => app.CreateClient();

        start.Should().Throw<OptionsValidationException>().WithMessage("*Security:Hubs:MaxConnections is 10 to 20,000*");
    }

    [Fact]
    public async Task Negotiate_Should_Answer429WithRetryAfter_When_TheCallersAccountHoldsItsMostConnections()
    {
        // ARV-229a: one account's connections (Security:Hubs:MaxConnectionsPerAccount, here the least, 8), every sign-in of its subject
        // together; another account negotiates while the replica has room, and a closed connection of the account frees a place.
        var ct = TestContext.Current.CancellationToken;
        await using var app = ArivaHosts.Create(ArivaHosts.Main, configure: builder =>
        {
            builder.UseSetting("Security:Hubs:MaxConnectionsPerAccount", "8");
            builder.ConfigureTestServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton<ISessionValidator>(new FakeSessionValidator()));
                services.Replace(ServiceDescriptor.Scoped<IPermissionResolver>(_ => new Grants(Live)));
            });
        });
        using var client = app.CreateClient();
        var account = Guid.CreateVersion7();
        var token = Token(app.Services, account);
        var otherToken = Token(app.Services, Guid.CreateVersion7());
        var registry = app.Services.GetRequiredService<HubSessionRegistry>();
        var held = Enumerable.Range(0, 8).Select(_ => new HeldConnection(account.ToString())).ToList();
        held.ForEach(connection => registry.Admit(Guid.CreateVersion7(), connection).Should().Be(HubAdmission.Admitted));

        using var refused = await client.SendAsync(Negotiate(token), ct);
        using var other = await client.SendAsync(Negotiate(otherToken), ct);
        registry.Remove(held[0].ConnectionId);
        using var admitted = await client.SendAsync(Negotiate(token), ct);

        registry.MaxConnectionsPerAccount.Should().Be(8, "the setting reaches the registry");
        registry.MaxConnections.Should().Be(Ariva.Api.Common.Settings.HubSettings.DefaultMaxConnections);
        ((int)refused.StatusCode).Should().Be(429, "a new sign-in of the account has no allowance past the account's");
        refused.Headers.RetryAfter.Should().NotBeNull();
        ((int)other.StatusCode).Should().Be(200, "another account has its own allowance");
        ((int)admitted.StatusCode).Should().Be(200, "a closed connection of the account frees its place");
    }

    [Theory]
    [InlineData("7")]
    [InlineData("20001")]
    public async Task Main_Should_RefuseToStart_When_TheAccountsMostIsOutOfBounds(string perAccount)
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Main, configure: builder => builder.UseSetting("Security:Hubs:MaxConnectionsPerAccount", perAccount));

        var start = () => app.CreateClient();

        start.Should().Throw<OptionsValidationException>().WithMessage("*Security:Hubs:MaxConnectionsPerAccount 8 to 20,000*");
    }

    [Fact]
    public async Task Main_Should_HoldTheDefaults_When_TheHubsSettingsAreNotSet()
    {
        await using var app = Main(granted: true);
        app.CreateClient().Dispose();

        var registry = app.Services.GetRequiredService<HubSessionRegistry>();

        (registry.MaxConnections, registry.MaxConnectionsPerAccount).Should().Be((1000, 200), "appsettings.base.json and the code agree");
    }

    private static HttpRequestMessage Negotiate(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{LiveHub.Path}/negotiate?negotiateVersion=1");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    /// <summary>An open hub connection the test holds in the registry, of no account or of <paramref name="subject"/>'s.</summary>
    private sealed class HeldConnection(string subject = null) : HubCallerContext
    {
        public override string ConnectionId { get; } = Guid.NewGuid().ToString("N");

        public override string UserIdentifier => null;

        public override ClaimsPrincipal User { get; } = subject is null
            ? new(new ClaimsIdentity())
            : new(new ClaimsIdentity([new Claim(ArivaClaims.Subject, subject)], "Bearer"));

        public override IDictionary<object, object> Items { get; } = new Dictionary<object, object>();

        public override IFeatureCollection Features { get; } = new FeatureCollection();

        public override CancellationToken ConnectionAborted => CancellationToken.None;

        public override void Abort()
        {
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
