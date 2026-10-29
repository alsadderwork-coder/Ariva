using Ariva.Core;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Services;
using Ariva.Core.Services.Administration;
using Ariva.Core.Services.Displays;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Seed;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ariva.IntegrationTests.Displays;

/// <summary>
/// ARV-145 against PostgreSQL with script 0057: a site without an advertising policy has advertising off; a change is kept
/// with its reason and audited with who made it; excluded zones must be published queue zones of the site; a display is set
/// to advertising only where the policy allows every zone it shows, by the service and by the database for the runtime role;
/// the board falls back to instructions on its next refresh once the policy no longer allows it; the runtime role cannot
/// delete a policy; two first changes of a site at the same moment make one policy; a display set to advertising while the
/// policy is being turned off is refused, by the service (the site's lock) and by the database (the policy row FOR SHARE);
/// renaming the excluded hall's zone in a new profile keeps advertising off at the site (security review). Its own
/// database, so the displays and slides tests keep advertising off at DMO.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AdvertisingPolicyServiceTests(PostgresFixture fixture) : IAsyncDisposable
{
    private static readonly SemaphoreSlim SeedGate = new(1, 1);
    private static bool _seeded;

    private readonly AccountsHost _host = new(fixture, database: TestDatabase.Advertising);

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ISvcAdvertisingPolicies Policies(IServiceProvider s) => s.GetRequiredService<ISvcAdvertisingPolicies>();

    private static ISvcDisplays Displays(IServiceProvider s) => s.GetRequiredService<ISvcDisplays>();

    private static ISvcDisplayBoard Board(IServiceProvider s) => s.GetRequiredService<ISvcDisplayBoard>();

    private static AdvertisingPolicyRequest Allow(params string[] excluded) =>
        new(true, excluded, "Airport operator letter AO-2026-114, signed by the terminal manager");

    private static AdvertisingPolicyRequest Off(string reason = "Border authority asked for no advertising, ref BA-77") => new(false, [], reason);

    private static DisplayRequest Request(string code, string zone = "A-CIT", string secondRegion = "Advertising", string name = "Arrivals hall") =>
        new("DMO", code, name, null, "Landscape", ["ar", "en"], 5, 1, 150,
            [new DisplayEntryRequest(zone, new Dictionary<string, string> { ["ar"] = "المواطنون", ["en"] = "Citizens" })],
            new Dictionary<string, string> { ["ar"] = "يرجى اتباع اللافتات", ["en"] = "Please follow the signs" }, true, "Split", 60, secondRegion);

    private async Task<Guid> AdminAsync(string userName)
    {
        var admin = await _host.CreateUserAsync(userName, roles: [RoleCodes.SystemAdministrator]);
        await _host.ReadAsync<int>("UPDATE \"user\" SET all_sites = true WHERE id = @id RETURNING 1", admin);
        await SeedGate.WaitAsync(Ct);
        try
        {
            if (!_seeded)
            {
                await _host.AsCallerAsync(null, s =>
                    new DemoTopologySeed(s.GetRequiredService<IUnitOfWork>(), s.GetRequiredService<ICurrentUser>(), _host.Clock, s.GetRequiredService<AuditTrail>()).RunAsync(Ct));
                foreach (var site in new[] { "ALX", "ADV1", "ADV2", "ADV3", "ADV4", "ADV5" })
                    await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSites>().CreateAsync(new CreateSiteRequest(site, "Advertising test site"), Ct));
                _seeded = true;
            }
        }
        finally
        {
            SeedGate.Release();
        }

        return admin;
    }

    private async Task<Guid> ManagerAsync(Guid admin, string userName, string site = "DMO")
    {
        var user = await _host.CreateUserAsync(userName, roles: [RoleCodes.TerminalDutyManager]);
        await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcUsers>().SetSitesAsync(user, new SiteAccessRequest(false, [site]), Ct));
        return user;
    }

    [Fact]
    public async Task Policy_Should_BeOffWithoutAReason_When_ASiteNeverHadOne()
    {
        var admin = await AdminAsync("it.ad.none.admin");
        var policy = await _host.AsCallerAsync(admin, s => Policies(s).GetAsync("ADV1", Ct));
        policy.HasErrors.Should().BeFalse();
        policy.Data.Should().Match<AdvertisingPolicyViewModel>(p =>
            p.SiteCode == "ADV1" && !p.Allowed && p.ExcludedZones.Count == 0 && p.Reason == null && p.ChangedOn == null && p.DisplaysFallingBack.Count == 0);
        (await _host.AsCallerAsync(admin, s => Policies(s).IsAllowedAsync("ADV1", Ct))).Should().BeFalse();
        (await _host.AsCallerAsync(admin, s => Policies(s).AccessAsync("ADV1", Ct))).Should().Be(AdvertisingAccess.Off, "a site of the caller without a policy");
        (await _host.AsCallerAsync(admin, s => Policies(s).GetAsync("ZZ9", Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(admin, s => Policies(s).AccessAsync("ZZ9", Ct))).Should().Be(AdvertisingAccess.NotFound, "a site that does not exist");
    }

    [Fact]
    public async Task Change_Should_BeKeptAndAuditedWithWhoAndWhy_When_AnAdministratorAllowsThenTurnsAdvertisingOff()
    {
        var admin = await AdminAsync("it.ad.change.admin");
        var allowed = await AllowAsync(admin, "ADV2");
        allowed.HasErrors.Should().BeFalse(string.Join(", ", allowed.ErrorMessages ?? []));
        allowed.Data.Should().Match<AdvertisingPolicyViewModel>(p => p.Allowed && p.Reason == Allow().Reason && p.ChangedOn != null);
        (await _host.AsCallerAsync(admin, s => Policies(s).IsAllowedAsync("ADV2", Ct))).Should().BeTrue();
        (await _host.AsCallerAsync(admin, s => Policies(s).AccessAsync("ADV2", Ct))).Should().Be(AdvertisingAccess.Allowed);

        var off = await _host.AsCallerAsync(admin, s => Policies(s).ChangeAsync("ADV2", Off("  Border authority asked for no advertising, ref BA-77  "), Ct));
        off.Data.Should().Match<AdvertisingPolicyViewModel>(p => !p.Allowed && p.Reason == "Border authority asked for no advertising, ref BA-77", "kept trimmed");
        (await _host.AsCallerAsync(admin, s => Policies(s).IsAllowedAsync("ADV2", Ct))).Should().BeFalse();
        (await _host.ReadAsync<long>("SELECT count(*) FROM advertising_policy WHERE site_code = 'ADV2'")).Should().Be(1, "one policy per site, changed in place");

        // Who (the actor), when (occurred_on) and why (the reason in the summary), before and after.
        var audit = await _host.ReadAsync<string>(
            """
            SELECT string_agg(coalesce(actor_id::text, '') || ' ' || target_name || ' ' || coalesce(before_summary, '-') || ' => ' || after_summary, ' | ' ORDER BY occurred_on, id)
            FROM audit_entry WHERE action = 'AdvertisingPolicy.Changed' AND target_name = 'ADV2' AND actor_id = @id AND occurred_on IS NOT NULL
            """, admin);
        audit.Should().Contain(admin.ToString()).And.Contain("\"allowed\":true").And.Contain("Airport operator letter AO-2026-114")
            .And.Contain("\"allowed\":false").And.Contain("ref BA-77");
        var entries = audit.Split(" | ");
        entries.Should().HaveCount(2);
        entries[0].Should().Contain(" - => ", "the first change makes the policy");
        entries[1].Should().Contain("AO-2026-114").And.Contain("BA-77", "a change keeps the reason before it and its own");
    }

    public static TheoryData<string, AdvertisingPolicyRequest> Refused => new()
    {
        { "no reason", Allow() with { Reason = null } },
        { "a blank reason", Allow() with { Reason = "          " } },
        { "a reason of 9 characters", Allow() with { Reason = "Approved." } },
        { "a reason of 501 characters", Allow() with { Reason = new string('x', 501) } },
        { "a line break in the reason", Allow() with { Reason = "Approved by the operator\nref 12" } },
        { "a bidirectional override in the reason", Allow() with { Reason = "Approved \u202Eby the operator" } },
        { "21 zones", Allow([.. Enumerable.Range(1, 21).Select(i => $"Z{i}")]) },
        { "a zone twice", Allow("A-RES", "A-RES") },
        { "a blank zone", Allow(" ") },
        { "a zone that is not published", Allow("NOT-A-ZONE") },
        { "an overflow zone", Allow("A-OV") },
        { "a zone in lower case", Allow("a-res") }
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task Change_Should_BeRefusedAndLeaveThePolicy_When_ItBreaksARule(string reason, AdvertisingPolicyRequest request)
    {
        var admin = await AdminAsync($"it.ad.refused.{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(reason)))[..12]}");
        var reviewed = (await _host.AsCallerAsync(admin, s => Policies(s).GetAsync("DMO", Ct))).Data.PublishedProfileId;
        var result = await _host.AsCallerAsync(admin, s => Policies(s).ChangeAsync("DMO", request with { ReviewedProfileId = reviewed }, Ct));
        result.HasErrors.Should().BeTrue(reason);
        string.Concat(result.ErrorMessages).Should().NotContain("NOT-A-ZONE", "an answer never repeats what was sent");
        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE action = 'AdvertisingPolicy.Changed' AND target_name = 'DMO' AND after_summary LIKE '%NOT-A-ZONE%'"))
            .Should().Be(0);
    }

    [Fact]
    public async Task Change_Should_AnswerLikeASiteThatDoesNotExist_When_TheSiteIsOutsideTheCallersSites()
    {
        var admin = await AdminAsync("it.ad.scope.admin");
        var local = await _host.CreateUserAsync("it.ad.scope.local", roles: [RoleCodes.SystemAdministrator]);
        await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcUsers>().SetSitesAsync(local, new SiteAccessRequest(false, ["ALX"]), Ct));

        // ADV1 never gets a policy in this class.
        (await AllowAsync(local, "ADV1")).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(local, s => Policies(s).GetAsync("ADV1", Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(local, s => Policies(s).IsAllowedAsync("ADV1", Ct))).Should().BeFalse();
        (await _host.AsCallerAsync(local, s => Policies(s).AccessAsync("ADV1", Ct))).Should().Be(AdvertisingAccess.NotFound, "outside the caller's sites");
        (await _host.ReadAsync<long>("SELECT count(*) FROM advertising_policy WHERE site_code = 'ADV1'")).Should().Be(0);
        (await AllowAsync(local, "ALX")).HasErrors.Should().BeFalse("its own site");
    }

    [Fact]
    public async Task Display_Should_ShowAdvertisingOnlyWhereThePolicyAllowsEveryZone_And_FallBackOnTheNextBoard()
    {
        var admin = await AdminAsync("it.ad.display.admin");
        var manager = await ManagerAsync(admin, "it.ad.display.manager");

        // Off (other tests of this class allow it at DMO): a display cannot be set to advertising.
        (await _host.AsCallerAsync(admin, s => Policies(s).ChangeAsync("DMO", Off(), Ct))).HasErrors.Should().BeFalse();
        (await _host.AsCallerAsync(manager, s => Displays(s).CreateAsync(Request("ADV-OFF"), Ct))).ErrorMessages.Should().Equal(DisplayErrors.AdvertisingOff);
        var instructions = await _host.AsCallerAsync(manager, s => Displays(s).CreateAsync(Request("ADV-A", secondRegion: "Instructions"), Ct));
        instructions.HasErrors.Should().BeFalse(string.Join(", ", instructions.ErrorMessages ?? []));

        // Allowed except in A-RES (the immigration hall of this test).
        (await AllowAsync(admin, "DMO", "A-RES")).HasErrors.Should().BeFalse();
        var id = instructions.Data.Display.Id;
        var advertising = await _host.AsCallerAsync(manager, s => Displays(s).UpdateAsync(id, Request("ADV-A"), Ct));
        advertising.HasErrors.Should().BeFalse(string.Join(", ", advertising.ErrorMessages ?? []));
        advertising.Data.SecondRegion.Should().Be("Advertising");
        (await _host.AsCallerAsync(manager, s => Displays(s).CreateAsync(Request("ADV-RES", "A-RES"), Ct))).ErrorMessages.Should().Equal(
            [DisplayErrors.AdvertisingOff], "a display showing an excluded zone shows instructions");
        (await _host.AsCallerAsync(manager, s => Displays(s).UpdateAsync(id, Request("ADV-A", "A-RES"), Ct))).ErrorMessages.Should().Equal(
            [DisplayErrors.AdvertisingOff], "nor can an advertising display take an excluded zone");

        var credential = (await _host.AsCallerAsync(manager, s => Displays(s).NewCredentialAsync(id, Ct))).Data.Credential;
        var player = await _host.AsCallerAsync(null, s => Board(s).FindAsync("ADV-A", credential, Ct));
        async Task<string> BoardRegionAsync() => (await _host.AsCallerAsync(null, s => Board(s).GetAsync(player.Id, player.CredentialPrefix, Ct))).Data.SecondRegion;
        (await BoardRegionAsync()).Should().Be("Advertising");

        // Excluding its zone, then turning the policy off: the very next board shows instructions, and the display keeps its
        // setting. Other tests of this class leave displays of their own at DMO, so only this one's place in the list is checked.
        (await AllowAsync(admin, "DMO", "A-RES", "A-CIT")).Data.DisplaysFallingBack.Should().Contain("ADV-A");
        (await BoardRegionAsync()).Should().Be("Instructions", "an excluded zone falls back within one refresh");
        (await AllowAsync(admin, "DMO", "A-RES")).Data.DisplaysFallingBack.Should().NotContain("ADV-A");
        (await BoardRegionAsync()).Should().Be("Advertising");
        var off = await _host.AsCallerAsync(admin, s => Policies(s).ChangeAsync("DMO", Off(), Ct));
        off.Data.DisplaysFallingBack.Should().Contain("ADV-A");
        (await BoardRegionAsync()).Should().Be("Instructions", "advertising off falls back within one refresh");
        (await _host.ReadAsync<string>("SELECT second_region FROM display WHERE code = 'ADV-A' AND deleted_on IS NULL")).Should().Be("Advertising");

        // While off, the display can be saved only with instructions; a new credential and a deletion still work.
        (await _host.AsCallerAsync(manager, s => Displays(s).UpdateAsync(id, Request("ADV-A", name: "Renamed"), Ct))).ErrorMessages.Should().Equal(DisplayErrors.AdvertisingOff);
        (await _host.AsCallerAsync(manager, s => Displays(s).NewCredentialAsync(id, Ct))).HasErrors.Should().BeFalse();
        (await _host.AsCallerAsync(manager, s => Displays(s).UpdateAsync(id, Request("ADV-A", secondRegion: "Instructions"), Ct))).HasErrors.Should().BeFalse();
        (await _host.AsCallerAsync(manager, s => Displays(s).DeleteAsync(id, Ct))).HasErrors.Should().BeFalse();
    }

    [Fact]
    public async Task Guard_Should_RefuseADisplayOfANullZone_When_TheBoardAsksWithOne()
    {
        // ARV-193: the board's check passes a null zone to the rule, which refuses it, as the policy's view and script 0057's
        // trigger do; it used to drop it first (not reachable: a display never stores a null zone).
        var admin = await AdminAsync("it.ad.null.admin");
        // Two queue zones of DMO's profile as published now (other tests of this class publish new versions): one shown, one excluded.
        var published = (await _host.AsCallerAsync(admin, s => Policies(s).GetAsync("DMO", Ct))).Data.PublishedQueueZones;
        published.Should().HaveCountGreaterThanOrEqualTo(2);
        var (shown, excluded) = (published[0], published[1]);
        (await AllowAsync(admin, "DMO", excluded)).HasErrors.Should().BeFalse();
        async Task<bool> AllowsAsync(params string[] zones) =>
            await _host.AsCallerAsync(null, s => s.GetRequiredService<Ariva.Infra.Services.Displays.AdvertisingPolicyGuard>().AllowsAsync("DMO", zones, Ct));

        (await AllowsAsync(shown)).Should().BeTrue();
        (await AllowsAsync($" {shown} ")).Should().BeTrue("a zone is trimmed as the display's entries are");
        (await AllowsAsync(shown, null)).Should().BeFalse("a null zone is no queue zone of the published profile");
        (await AllowsAsync(new string[] { null })).Should().BeFalse();
        (await AllowsAsync(excluded)).Should().BeFalse();
    }

    [Theory]
    [InlineData("an excluded zone", "RT-AD-RES", "UPDATE display SET second_region = 'Advertising' WHERE code = 'RT-AD-RES'")]
    [InlineData("a display moved into an excluded zone", "RT-AD-CIT", "UPDATE display SET entries = replace(entries, 'A-CIT', 'A-RES') WHERE code = 'RT-AD-CIT'")]
    [InlineData("a site without a policy", "RT-AD-ALX", "UPDATE display SET second_region = 'Advertising' WHERE code = 'RT-AD-ALX'")]
    [InlineData("an allowed policy saved before the site had a zone profile", "RT-AD-NOP",
        "UPDATE display SET site_code = 'ADV4', entries = '[]', second_region = 'Advertising' WHERE code = 'RT-AD-NOP'")]
    public async Task RuntimeRole_Should_NotStoreAdvertisingThePolicyRefuses_When_ItWritesADisplayDirectly(string change, string code, string sql)
    {
        var admin = await AdminAsync($"it.ad.sql.admin.{code}");
        var manager = await ManagerAsync(admin, $"it.ad.sql.manager.{code}");
        (await AllowAsync(admin, "DMO", "A-RES")).HasErrors.Should().BeFalse();
        var created = await _host.AsCallerAsync(manager, s => Displays(s).CreateAsync(Request(code, code == "RT-AD-RES" ? "A-RES" : "A-CIT",
            code == "RT-AD-CIT" ? "Advertising" : "Instructions"), Ct));
        created.HasErrors.Should().BeFalse(string.Join(", ", created.ErrorMessages ?? []));
        // ADV5 never gets a policy in this class; ADV4 gets an allowing one but has no zone profile, so the only refusal is
        // that the policy confirms no published profile.
        if (code == "RT-AD-ALX")
            await _host.ReadAsync<int>("UPDATE display SET site_code = 'ADV5' WHERE code = 'RT-AD-ALX' RETURNING 1");
        if (code == "RT-AD-NOP")
            (await AllowAsync(admin, "ADV4")).HasErrors.Should().BeFalse();

        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE ariva_runtime", connection, transaction))
            await role.ExecuteNonQueryAsync(Ct);
#pragma warning disable CA2100 // the statements are the theory's literals
        await using var command = new NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100
        var update = () => command.ExecuteNonQueryAsync(Ct);
        (await update.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514", $"script 0057 refuses {change}");
        await transaction.RollbackAsync(Ct);
    }

    [Fact]
    public async Task Exclusion_Should_StayClosed_When_AManagerRenamesTheExcludedZoneInANewProfile()
    {
        // Security review of ARV-145: a duty manager holds EditZoneProfile and PublishZoneProfile, so it can rename the
        // excluded hall's zone in a new profile. The exclusion then names no published queue zone, and advertising stays off
        // on every display of the site, by the service, the board and the database, until an administrator updates it.
        var admin = await AdminAsync("it.ad.rename.admin");
        var manager = await ManagerAsync(admin, "it.ad.rename.manager");
        (await AllowAsync(admin, "DMO", "A-RES")).HasErrors.Should().BeFalse();
        var created = await _host.AsCallerAsync(manager, s => Displays(s).CreateAsync(Request("ADV-REN", "A-VIS"), Ct));
        created.HasErrors.Should().BeFalse(string.Join(", ", created.ErrorMessages ?? []));
        var player = await _host.AsCallerAsync(null, s => Board(s).FindAsync("ADV-REN", created.Data.Credential, Ct));
        async Task<string> BoardRegionAsync() => (await _host.AsCallerAsync(null, s => Board(s).GetAsync(player.Id, player.CredentialPrefix, Ct))).Data.SecondRegion;
        (await BoardRegionAsync()).Should().Be("Advertising");

        await RenameQueueZoneAsync(manager, "A-RES", "A-IMM");
        (await BoardRegionAsync()).Should().Be("Instructions", "the excluded zone is no longer published: advertising is off everywhere at the site");
        var view = (await _host.AsCallerAsync(manager, s => Policies(s).GetAsync("DMO", Ct))).Data;
        view.UnpublishedZones.Should().Equal("A-RES");
        view.ProfileChanged.Should().BeTrue("a new profile is published since the policy was saved");
        view.PublishedQueueZones.Should().Contain("A-IMM").And.NotContain("A-RES", "the screen offers the zones of the profile it confirms");
        view.DisplaysFallingBack.Should().Contain("ADV-REN");
        foreach (var zone in new[] { "A-IMM", "A-VIS" })
            (await _host.AsCallerAsync(manager, s => Displays(s).CreateAsync(Request($"ADV-R{zone[^1]}", zone), Ct))).ErrorMessages.Should().Equal(
                [DisplayErrors.AdvertisingOff], $"a new display showing {zone}");
        await using (var runtime = await RuntimeAsync())
        {
            await using var insert = new NpgsqlCommand(InsertCopy, runtime.Connection, runtime.Transaction);
            insert.Parameters.AddWithValue("code", "RT-AD-REN");
            insert.Parameters.AddWithValue("source", "ADV-REN");
            insert.Parameters.AddWithValue("from", "A-VIS");
            insert.Parameters.AddWithValue("to", "A-VIS");
            var inserting = () => insert.ExecuteNonQueryAsync(Ct);
            (await inserting.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514", "script 0057 fails closed on an unpublished exclusion");
        }

        // The administrator excludes the hall under its new name: advertising comes back where it is allowed.
        var confirmed = (await AllowAsync(admin, "DMO", "A-IMM")).Data;
        confirmed.UnpublishedZones.Should().BeEmpty();
        confirmed.ProfileChanged.Should().BeFalse();
        (await BoardRegionAsync()).Should().Be("Advertising");

        // The other tests of this class keep A-RES: the hall gets its name back (advertising off again until the policy is
        // saved), and the policy is turned off.
        await RenameQueueZoneAsync(manager, "A-IMM", "A-RES");
        (await BoardRegionAsync()).Should().Be("Instructions");
        (await _host.AsCallerAsync(admin, s => Policies(s).ChangeAsync("DMO", Off(), Ct))).HasErrors.Should().BeFalse();
        (await _host.AsCallerAsync(manager, s => Displays(s).DeleteAsync(created.Data.Display.Id, Ct))).HasErrors.Should().BeFalse();
    }

    [Fact]
    public async Task Exclusion_Should_WaitForTheAdministrator_When_AnyNewZoneProfileIsPublished()
    {
        // Round 2 of the security review: a new profile can give the excluded name to a decoy zone, move the hall's outline
        // or add a zone over it, none of which the names show. So any new profile, even one with nothing changed, keeps
        // advertising off at the site until an administrator saves the policy against it.
        var admin = await AdminAsync("it.ad.republish.admin");
        var manager = await ManagerAsync(admin, "it.ad.republish.manager");
        (await AllowAsync(admin, "DMO", "A-RES")).HasErrors.Should().BeFalse();
        var created = await _host.AsCallerAsync(manager, s => Displays(s).CreateAsync(Request("ADV-PUB", "A-VIS"), Ct));
        created.HasErrors.Should().BeFalse(string.Join(", ", created.ErrorMessages ?? []));
        var other = await _host.AsCallerAsync(manager, s => Displays(s).CreateAsync(Request("ADV-PUB2", "A-VIS", "Instructions"), Ct));
        other.HasErrors.Should().BeFalse();
        var player = await _host.AsCallerAsync(null, s => Board(s).FindAsync("ADV-PUB", created.Data.Credential, Ct));
        async Task<string> BoardRegionAsync() => (await _host.AsCallerAsync(null, s => Board(s).GetAsync(player.Id, player.CredentialPrefix, Ct))).Data.SecondRegion;
        (await BoardRegionAsync()).Should().Be("Advertising");

        await RenameQueueZoneAsync(manager, null, null);
        (await BoardRegionAsync()).Should().Be("Instructions", "a new profile is published");
        var view = (await _host.AsCallerAsync(manager, s => Policies(s).GetAsync("DMO", Ct))).Data;
        view.Should().Match<AdvertisingPolicyViewModel>(v => v.Allowed && v.ProfileChanged && v.UnpublishedZones.Count == 0);
        view.DisplaysFallingBack.Should().Contain("ADV-PUB");
        (await _host.AsCallerAsync(manager, s => Displays(s).UpdateAsync(other.Data.Display.Id, Request("ADV-PUB2", "A-VIS"), Ct))).ErrorMessages.Should().Equal(
            [DisplayErrors.AdvertisingOff], "the service waits for the administrator too");
        await using (var runtime = await RuntimeAsync())
        {
            await using var update = new NpgsqlCommand("UPDATE display SET second_region = 'Advertising' WHERE code = 'ADV-PUB2'", runtime.Connection,
                runtime.Transaction);
            var updating = () => update.ExecuteNonQueryAsync(Ct);
            (await updating.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514", "and so does script 0057");
        }

        var saved = (await AllowAsync(admin, "DMO", "A-RES")).Data;
        saved.ProfileChanged.Should().BeFalse("saved against the new profile");
        (await BoardRegionAsync()).Should().Be("Advertising");
        (await _host.AsCallerAsync(admin, s => Policies(s).ChangeAsync("DMO", Off(), Ct))).HasErrors.Should().BeFalse();
        foreach (var id in new[] { created.Data.Display.Id, other.Data.Display.Id })
            (await _host.AsCallerAsync(manager, s => Displays(s).DeleteAsync(id, Ct))).HasErrors.Should().BeFalse();
    }

    [Theory]
    [InlineData("an excluded zone", "A-RES")]
    [InlineData("an excluded zone with spaces", " A-RES ")]
    [InlineData("an excluded zone with a tab", "A-RES\\t")]
    [InlineData("an excluded zone with a no-break space", "A-RES\\u00A0")]
    [InlineData("a zone that is not a published queue zone", "A-OV")]
    [InlineData("an excluded zone under a key in capitals", "\"Zone\":\"A-RES\"")]
    public async Task RuntimeRole_Should_NotInsertAnAdvertisingDisplayThePolicyRefuses_When_ItWritesOneDirectly(string change, string zone)
    {
        var admin = await AdminAsync($"it.ad.insert.admin.{change.Length}");
        var manager = await ManagerAsync(admin, $"it.ad.insert.manager.{change.Length}");
        (await AllowAsync(admin, "DMO", "A-RES")).HasErrors.Should().BeFalse();
        var source = $"RT-AD-SRC{change.Length}";
        (await _host.AsCallerAsync(manager, s => Displays(s).CreateAsync(Request(source, "A-CIT", "Instructions"), Ct))).HasErrors.Should().BeFalse();

        await using var runtime = await RuntimeAsync();
        await using var insert = new NpgsqlCommand(InsertCopy, runtime.Connection, runtime.Transaction);
        insert.Parameters.AddWithValue("code", $"RT-AD-INS{change.Length}");
        insert.Parameters.AddWithValue("source", source);
        insert.Parameters.AddWithValue("from", zone.StartsWith('"') ? "\"zone\":\"A-CIT\"" : "A-CIT");
        insert.Parameters.AddWithValue("to", zone);
        var inserting = () => insert.ExecuteNonQueryAsync(Ct);
        (await inserting.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514", $"script 0057 refuses {change} on insert");
    }

    [Fact]
    public async Task RuntimeRole_Should_NotRestoreAnAdvertisingDisplay_When_ItsZoneIsExcludedSinceItsDeletion()
    {
        var admin = await AdminAsync("it.ad.restore.admin");
        var manager = await ManagerAsync(admin, "it.ad.restore.manager");
        (await AllowAsync(admin, "DMO", "A-RES")).HasErrors.Should().BeFalse();
        var created = await _host.AsCallerAsync(manager, s => Displays(s).CreateAsync(Request("RT-AD-DEL", "A-CIT"), Ct));
        created.HasErrors.Should().BeFalse(string.Join(", ", created.ErrorMessages ?? []));
        (await _host.AsCallerAsync(manager, s => Displays(s).DeleteAsync(created.Data.Display.Id, Ct))).HasErrors.Should().BeFalse();
        (await AllowAsync(admin, "DMO", "A-RES", "A-CIT")).HasErrors.Should().BeFalse();

        await using var runtime = await RuntimeAsync();
        await using var restore = new NpgsqlCommand("UPDATE display SET deleted_on = NULL, deleted_by = NULL WHERE code = 'RT-AD-DEL'", runtime.Connection,
            runtime.Transaction);
        var restoring = () => restore.ExecuteNonQueryAsync(Ct);
        (await restoring.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514", "script 0057 checks a restored display too");
    }

    [Fact]
    public async Task Display_Should_NotBeSetToAdvertising_When_ThePolicyIsTurnedOffAtTheSameMoment()
    {
        var admin = await AdminAsync("it.ad.race.display.admin");
        var manager = await ManagerAsync(admin, "it.ad.race.display.manager");
        (await AllowAsync(admin, "DMO", "A-RES")).HasErrors.Should().BeFalse();
        var created = await _host.AsCallerAsync(manager, s => Displays(s).CreateAsync(Request("ADV-RACE", "A-CIT", "Instructions"), Ct));
        created.HasErrors.Should().BeFalse(string.Join(", ", created.ErrorMessages ?? []));

        // A change of the policy in progress, as the service makes it: the site's lock (class 145), then the row, not yet committed.
        await using (var change = await RuntimeAsync())
        {
            await using (var command = new NpgsqlCommand(
                             "SELECT pg_advisory_xact_lock(145, hashtext('DMO')); UPDATE advertising_policy SET allowed = false WHERE site_code = 'DMO'",
                             change.Connection, change.Transaction))
                await command.ExecuteNonQueryAsync(Ct);
            var setting = _host.AsCallerAsync(manager, s => Displays(s).UpdateAsync(created.Data.Display.Id, Request("ADV-RACE", "A-CIT"), Ct));
            await WaitForAsync("SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND classid = 145 AND NOT granted");
            await change.Transaction.CommitAsync(Ct);
            (await setting).ErrorMessages.Should().Equal([DisplayErrors.AdvertisingOff], "the display waits for the change and reads the policy it left");
        }

        (await _host.ReadAsync<string>("SELECT second_region FROM display WHERE code = 'ADV-RACE' AND deleted_on IS NULL")).Should().Be("Instructions");

        // A direct write that skips the site's lock waits on the policy row (FOR SHARE) and is refused once the change commits.
        (await AllowAsync(admin, "DMO", "A-RES")).HasErrors.Should().BeFalse();
        await using (var change = await RuntimeAsync())
        await using (var write = await RuntimeAsync())
        {
            await using (var off = new NpgsqlCommand("UPDATE advertising_policy SET allowed = false WHERE site_code = 'DMO'", change.Connection, change.Transaction))
                await off.ExecuteNonQueryAsync(Ct);
            await using var advertise = new NpgsqlCommand("UPDATE display SET second_region = 'Advertising' WHERE code = 'ADV-RACE'", write.Connection,
                write.Transaction);
            var writing = advertise.ExecuteNonQueryAsync(Ct);
            await WaitForAsync("SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND query LIKE 'UPDATE display SET second_region%'");
            await change.Transaction.CommitAsync(Ct);
            var waited = () => writing;
            (await waited.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514", "the trigger reads the policy as committed");
        }

        (await _host.AsCallerAsync(manager, s => Displays(s).DeleteAsync(created.Data.Display.Id, Ct))).HasErrors.Should().BeFalse();
    }

    [Fact]
    public async Task Change_Should_BeRefusedAndKeepThePolicy_When_AnotherProfileIsPublishedSinceTheReview()
    {
        // Round 3 of the security review: an administrator reviews the policy against the published profile, then a duty
        // manager publishes another (it could hand the excluded name to a decoy) before the save. The save names the profile
        // it reviewed and is refused, so it never confirms geometry nobody looked at.
        var admin = await AdminAsync("it.ad.review.admin");
        var manager = await ManagerAsync(admin, "it.ad.review.manager");
        (await AllowAsync(admin, "DMO", "A-RES")).HasErrors.Should().BeFalse();
        var reviewed = (await _host.AsCallerAsync(admin, s => Policies(s).GetAsync("DMO", Ct))).Data;
        reviewed.PublishedProfileId.Should().NotBeNull();
        reviewed.ConfirmedProfileVersion.Should().Be(reviewed.PublishedProfileVersion);
        reviewed.PublishedQueueZones.Should().Contain(["A-CIT", "A-RES", "A-VIS"]).And.NotContain("A-OV", "overflow bands are not queue zones");

        await RenameQueueZoneAsync(manager, null, null);
        var audits = await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE action = 'AdvertisingPolicy.Changed' AND target_name = 'DMO'");
        var stale = await _host.AsCallerAsync(admin, s => Policies(s).ChangeAsync("DMO", Allow("A-RES") with { ReviewedProfileId = reviewed.PublishedProfileId }, Ct));
        stale.ErrorMessages.Should().Equal(AdvertisingPolicyErrors.ProfileChangedSinceReview);
        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE action = 'AdvertisingPolicy.Changed' AND target_name = 'DMO'"))
            .Should().Be(audits, "nothing is saved or audited");
        var now = (await _host.AsCallerAsync(admin, s => Policies(s).GetAsync("DMO", Ct))).Data;
        now.Should().Match<AdvertisingPolicyViewModel>(v => v.ProfileChanged && v.ConfirmedProfileVersion == reviewed.PublishedProfileVersion &&
                                                            v.PublishedProfileVersion == reviewed.PublishedProfileVersion + 1);
        (await _host.AsCallerAsync(admin, s => Policies(s).ChangeAsync("DMO", Allow("A-RES") with { ReviewedProfileId = null }, Ct))).ErrorMessages
            .Should().Equal([AdvertisingPolicyErrors.ProfileChangedSinceReview], "an allowing change names the profile it reviewed");

        // Turning advertising off needs no review; allowing it again reviews the new profile.
        (await _host.AsCallerAsync(admin, s => Policies(s).ChangeAsync("DMO", Off(), Ct))).HasErrors.Should().BeFalse();
        (await AllowAsync(admin, "DMO", "A-RES")).Data.ProfileChanged.Should().BeFalse();
        (await _host.AsCallerAsync(admin, s => Policies(s).ChangeAsync("DMO", Off(), Ct))).HasErrors.Should().BeFalse();
    }

    /// <summary>Allows advertising at a site as an administrator does: naming the published profile the view showed it.</summary>
    private async Task<Fluentx.Result<AdvertisingPolicyViewModel>> AllowAsync(Guid caller, string site, params string[] excluded)
    {
        var reviewed = (await _host.AsCallerAsync(caller, s => Policies(s).GetAsync(site, Ct))).Data?.PublishedProfileId;
        return await _host.AsCallerAsync(caller, s => Policies(s).ChangeAsync(site, Allow(excluded) with { ReviewedProfileId = reviewed }, Ct));
    }

    /// <summary>A copy of a display under another code, its zone replaced in the entries' JSON text, written by the runtime role.</summary>
    private const string InsertCopy =
        """
        INSERT INTO display (id, site_code, code, name, location, orientation, languages, band_minutes, hysteresis_minutes, stale_seconds, entries,
                             fallback_messages, enabled, layout, queue_share, second_region)
        SELECT gen_random_uuid(), site_code, @code, name, location, orientation, languages, band_minutes, hysteresis_minutes, stale_seconds,
               replace(entries, @from, @to), fallback_messages, enabled, 'Split', queue_share, 'Advertising'
        FROM display WHERE code = @source AND deleted_on IS NULL
        """;

    private sealed class Runtime(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public NpgsqlConnection Connection { get; } = connection;
        public NpgsqlTransaction Transaction { get; } = transaction;

        public async ValueTask DisposeAsync()
        {
            await Transaction.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    /// <summary>A connection with an open transaction as the runtime role (rolled back when disposed unless committed).</summary>
    private async Task<Runtime> RuntimeAsync()
    {
        var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE ariva_runtime", connection, transaction))
            await role.ExecuteNonQueryAsync(Ct);
        return new Runtime(connection, transaction);
    }

    /// <summary>Waits until the count is above zero (another session is waiting on a lock), for at most 20 seconds.</summary>
    private async Task WaitForAsync([System.Diagnostics.CodeAnalysis.ConstantExpected] string countSql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        for (var attempt = 0; attempt < 200; attempt++)
        {
#pragma warning disable CA2100 // the callers pass literals (ConstantExpected)
            await using var command = new NpgsqlCommand(countSql, connection);
#pragma warning restore CA2100
            if ((long)(await command.ExecuteScalarAsync(Ct))! > 0)
                return;
            await Task.Delay(100, Ct);
        }

        throw new TimeoutException("Nothing waited on the lock.");
    }

    /// <summary>
    /// Publishes a new profile of DMO, as the duty manager can: with a queue zone renamed, or (both names null) unchanged.
    /// </summary>
    private async Task RenameQueueZoneAsync(Guid manager, string from, string to)
    {
        var draft = await _host.AsCallerAsync(manager, s => s.GetRequiredService<ISvcZoneProfiles>().CreateDraftAsync(new CreateZoneProfileDraftRequest("DMO", null), Ct));
        draft.HasErrors.Should().BeFalse(string.Join(", ", draft.ErrorMessages ?? []));
        if (from is not null)
        {
            var zone = draft.Data.Zones.Single(z => z.Name == from);
            var renamed = await _host.AsCallerAsync(manager, s => s.GetRequiredService<ISvcZoneProfiles>().UpdateZoneAsync(draft.Data.Profile.Id, zone.Id,
                new UpdateZoneRequest(to, zone.Polygon, zone.LaneCategory, zone.PhysicalCapacity), Ct));
            renamed.HasErrors.Should().BeFalse(string.Join(", ", renamed.ErrorMessages ?? []));
        }

        var reviewed = await _host.AsCallerAsync(manager, s => s.GetRequiredService<ISvcZoneProfiles>().ValidateAsync(draft.Data.Profile.Id, Ct));
        var published = await _host.AsCallerAsync(manager, s => s.GetRequiredService<ISvcZoneProfiles>().PublishAsync(draft.Data.Profile.Id,
            new PublishZoneProfileRequest(reviewed.Data.GeometryHash), Ct));
        published.HasErrors.Should().BeFalse(string.Join(", ", published.ErrorMessages ?? []));
    }

    [Fact]
    public async Task RuntimeRole_Should_KeepAnAllowedAdvertisingDisplay_And_NeverDeleteAPolicy()
    {
        var admin = await AdminAsync("it.ad.runtime.admin");
        var manager = await ManagerAsync(admin, "it.ad.runtime.manager");
        (await AllowAsync(admin, "DMO", "A-RES")).HasErrors.Should().BeFalse();
        (await _host.AsCallerAsync(manager, s => Displays(s).CreateAsync(Request("RT-AD-OK", "A-VIS", "Instructions"), Ct))).HasErrors.Should().BeFalse();

        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        await using (var transaction = await connection.BeginTransactionAsync(Ct))
        {
            await using (var role = new NpgsqlCommand("SET LOCAL ROLE ariva_runtime", connection, transaction))
                await role.ExecuteNonQueryAsync(Ct);
            await using (var allowed = new NpgsqlCommand("UPDATE display SET second_region = 'Advertising' WHERE code = 'RT-AD-OK'", connection, transaction))
                (await allowed.ExecuteNonQueryAsync(Ct)).Should().Be(1, "A-VIS is not excluded");
            await using var delete = new NpgsqlCommand("DELETE FROM advertising_policy WHERE site_code = 'DMO'", connection, transaction);
            var deleting = () => delete.ExecuteNonQueryAsync(Ct);
            (await deleting.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501", "turning advertising off is a change, audited");
            await transaction.RollbackAsync(Ct);
        }
    }

    [Fact]
    public async Task Change_Should_MakeOnePolicy_When_TwoAdministratorsChangeANewSiteAtOnce()
    {
        var admin = await AdminAsync("it.ad.race.admin");
        var other = await _host.CreateUserAsync("it.ad.race.other", roles: [RoleCodes.SystemAdministrator]);
        await _host.ReadAsync<int>("UPDATE \"user\" SET all_sites = true WHERE id = @id RETURNING 1", other);

        var results = await Task.WhenAll(
            AllowAsync(admin, "ADV3"),
            _host.AsCallerAsync(other, s => Policies(s).ChangeAsync("ADV3", Off(), Ct)));

        results.Should().OnlyContain(r => !r.HasErrors, "the site lock makes the second change an update of the first");
        (await _host.ReadAsync<long>("SELECT count(*) FROM advertising_policy WHERE site_code = 'ADV3'")).Should().Be(1);
        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE action = 'AdvertisingPolicy.Changed' AND target_name = 'ADV3'")).Should().Be(2);
    }
}
