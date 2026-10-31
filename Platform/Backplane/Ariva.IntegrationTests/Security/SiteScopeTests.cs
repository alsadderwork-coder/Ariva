using Ariva.Core;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Security;
using Ariva.Core.Services;
using Ariva.Core.Services.Administration;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using static Ariva.IntegrationTests.Security.AccountsHost;
using static Ariva.IntegrationTests.Security.AuthenticatorTests;

namespace Ariva.IntegrationTests.Security;

/// <summary>
/// ARV-012 against PostgreSQL: sites are created once and audited; site access is granted only within the granter's own
/// access and never to oneself; a caller sees only its sites, a change applies on the next request, and narrowing the
/// access ends the user's sessions.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SiteScopeTests(PostgresFixture fixture) : IAsyncDisposable
{
    private readonly AccountsHost _host = new(fixture, database: TestDatabase.Administration);

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ISvcSites Sites(IServiceProvider s) => s.GetRequiredService<ISvcSites>();

    private static ISvcUsers Users(IServiceProvider s) => s.GetRequiredService<ISvcUsers>();

    private Task<SiteAccess> AccessOf(Guid user) => _host.AsCallerAsync(user, s => s.GetRequiredService<ISiteScope>().GetAsync(Ct));

    private async Task<Guid> AllSitesAdminAsync(string userName)
    {
        var admin = await _host.CreateUserAsync(userName, roles: [RoleCodes.SystemAdministrator]);
        await _host.ReadAsync<int>("UPDATE \"user\" SET all_sites = true WHERE id = @id RETURNING 1", admin);
        return admin;
    }

    [Fact]
    public async Task Sites_Should_BeCreatedOnceAndAudited_When_AdministratorCreatesThem()
    {
        var admin = await AllSitesAdminAsync("it.site.creator");

        var created = await _host.AsCallerAsync(admin, s => Sites(s).CreateAsync(new CreateSiteRequest("ITA", "Integration A"), Ct));
        var again = await _host.AsCallerAsync(admin, s => Sites(s).CreateAsync(new CreateSiteRequest("ITA", "Again"), Ct));
        var renamed = await _host.AsCallerAsync(admin, s => Sites(s).UpdateAsync("ITA", new UpdateSiteRequest("Integration A, renamed"), Ct));

        created.Data.Code.Should().Be("ITA");
        again.ErrorMessages.Should().Equal(AdministrationErrors.SiteTaken);
        renamed.Data.Name.Should().Be("Integration A, renamed");
        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE target_name = 'ITA' AND action IN ('Site.Created', 'Site.Updated')")).Should().Be(2);
    }

    /// <summary>
    /// ARV-278: two creates of one code at the same moment. The first has sent its row and holds its transaction open; the
    /// second passes the existence check (it cannot see a row not yet committed), and its insert waits on the first one's
    /// entry in the code index. When the first commits, the second answers SiteTaken (409 at the API) and commits nothing.
    /// Before ARV-278 neither create sent its row before the commit, so the second never waited and one of them failed there.
    /// </summary>
    [Fact]
    public async Task Create_Should_AnswerSiteTakenToTheSecond_When_TwoCreatesOfOneCodeMeet()
    {
        var admin = await AllSitesAdminAsync("it.site.racer");

        Fluentx.Result<SiteViewModel> first;
        Task<Fluentx.Result<SiteViewModel>> second;
        await using (var scope = _host.Provider.CreateAsyncScope())
        {
            var caller = (TestCurrentUser)scope.ServiceProvider.GetRequiredService<ICurrentUser>();
            caller.Id = admin;
            caller.UserName = "it-admin";
            first = await Sites(scope.ServiceProvider).CreateAsync(new CreateSiteRequest("ITR", "Race first"), Ct);
            second = _host.AsCallerAsync(admin, s => Sites(s).CreateAsync(new CreateSiteRequest("ITR", "Race second"), Ct));
            (await BlocksAsync(second)).Should().BeTrue("the second insert waits on the first one's index entry, so the first has sent its row");
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().EndAsync(Ct);
        }

        first.Data.Code.Should().Be("ITR");
        (await second).ErrorMessages.Should().Equal(AdministrationErrors.SiteTaken);
        (await _host.ReadAsync<string>("SELECT string_agg(name, ',') FROM site WHERE code = 'ITR'")).Should().Be("Race first");
        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE target_name = 'ITR' AND action = 'Site.Created'")).Should().Be(1);
    }

    /// <summary>
    /// ARV-278: only the code index's refusal means the code is taken. A unique violation of another index, or any other
    /// database error, is not turned into SiteTaken: the create fails (500 at the API, without its text outside vm-local;
    /// <c>ErrorHandlingTests.Get_Should_Return500WithoutExceptionDetails_When_EnvironmentIsNotVmLocal</c>) and keeps nothing.
    /// A trigger on this test's database raises each error for the codes it names; it is dropped before it is created and
    /// afterwards, so one row's failure cannot leave it for the next.
    /// </summary>
    [Theory]
    [InlineData("ITXU", "Raise unique", "23505")]
    [InlineData("ITXO", "Raise other", "P0001")]
    public async Task Create_Should_FailAndKeepNothing_When_TheDatabaseRefusesTheSiteForAnotherReason(string code, string name, string sqlState)
    {
        var admin = await AllSitesAdminAsync($"it.site.refused.{code.ToLowerInvariant()}");
        await _host.ReadAsync<int>("""
            DROP TRIGGER IF EXISTS it_refuse_site ON site;
            CREATE OR REPLACE FUNCTION it_refuse_site() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.name = 'Raise unique' THEN
                    RAISE EXCEPTION 'refused by the test' USING ERRCODE = 'unique_violation', CONSTRAINT = 'ux_site_elsewhere';
                END IF;
                RAISE EXCEPTION 'refused by the test';
            END $$;
            CREATE TRIGGER it_refuse_site BEFORE INSERT ON site FOR EACH ROW WHEN (NEW.code LIKE 'ITX%') EXECUTE FUNCTION it_refuse_site();
            SELECT 1
            """);
        try
        {
            var create = () => _host.AsCallerAsync(admin, s => Sites(s).CreateAsync(new CreateSiteRequest(code, name), Ct));

            var thrown = await create.Should().ThrowAsync<global::NHibernate.Exceptions.GenericADOException>();
            thrown.Which.InnerException.Should().BeOfType<Npgsql.PostgresException>().Which.SqlState.Should().Be(sqlState);
        }
        finally
        {
            await _host.ReadAsync<int>("DROP TRIGGER IF EXISTS it_refuse_site ON site; DROP FUNCTION IF EXISTS it_refuse_site(); SELECT 1");
        }

        (await _host.ReadAsync<long>("SELECT count(*) FROM site WHERE code LIKE 'ITX%'")).Should().Be(0);
        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE target_name LIKE 'ITX%'")).Should().Be(0);
    }

    /// <summary>True once <paramref name="work"/> waits for a lock in this test's database; false when it ends first, or after 30 seconds.</summary>
    private async Task<bool> BlocksAsync(Task work)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until && !work.IsCompleted)
        {
            if (await _host.ReadAsync<long>("SELECT count(*) FROM pg_locks l JOIN pg_stat_activity a ON a.pid = l.pid WHERE NOT l.granted AND a.datname = current_database()") > 0)
                return true;
            await Task.Delay(25, Ct);
        }

        return false;
    }

    [Fact]
    public async Task SiteAccess_Should_LimitWhatAUserSees_When_BoundToSites()
    {
        var admin = await AllSitesAdminAsync("it.site.admin");
        foreach (var code in new[] { "ITB", "ITC" })
            await _host.AsCallerAsync(admin, s => Sites(s).CreateAsync(new CreateSiteRequest(code, code), Ct));
        var officer = await _host.CreateUserAsync("it.site.officer", roles: [RoleCodes.BorderShiftSupervisor]);

        var none = await AccessOf(officer);
        none.AllSites.Should().BeFalse();
        none.SiteCodes.Should().BeEmpty("a new account has no sites");
        var set = await _host.AsCallerAsync(admin, s => Users(s).SetSitesAsync(officer, new SiteAccessRequest(false, ["ITB"]), Ct));
        set.Data.Sites.Should().Equal("ITB");

        var access = await AccessOf(officer);
        access.Allows("ITB").Should().BeTrue("the change applies on the next request");
        access.Allows("ITC").Should().BeFalse();
        (await _host.AsCallerAsync(officer, s => Sites(s).ListAsync(Ct))).Data.Select(v => v.Code).Should().Equal("ITB");
        (await _host.AsCallerAsync(officer, s => Sites(s).GetAsync("ITC", Ct))).ErrorMessages.Should().Equal(AdministrationErrors.NotFound);
        (await _host.AsCallerAsync(officer, s => Sites(s).GetAsync("NOPE", Ct))).ErrorMessages.Should().Equal(new[] { AdministrationErrors.NotFound }, "an unknown site answers the same");
        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE target_id = @id AND action = 'User.SitesChanged' AND after_summary LIKE '%sites=ITB;%'", officer)).Should().Be(1);
    }

    [Fact]
    public async Task SetSites_Should_RefuseUnknownSitesSelfChangesAndWiderAccess_When_RulesAreBroken()
    {
        var admin = await AllSitesAdminAsync("it.site.granter");
        await _host.AsCallerAsync(admin, s => Sites(s).CreateAsync(new CreateSiteRequest("ITD", "ITD"), Ct));
        await _host.AsCallerAsync(admin, s => Sites(s).CreateAsync(new CreateSiteRequest("ITE", "ITE"), Ct));
        var limited = await _host.CreateUserAsync("it.site.limited", roles: [RoleCodes.SystemAdministrator]);
        await _host.AsCallerAsync(admin, s => Users(s).SetSitesAsync(limited, new SiteAccessRequest(false, ["ITD"]), Ct));
        var target = await _host.CreateUserAsync("it.site.target");

        (await _host.AsCallerAsync(admin, s => Users(s).SetSitesAsync(target, new SiteAccessRequest(false, ["NOPE"]), Ct))).ErrorMessages.Should().Equal(AdministrationErrors.UnknownSite);
        // Without sites the target is beyond the limited administrator (ARV-059); inside ITD it is its to administer.
        (await _host.AsCallerAsync(limited, s => Users(s).SetSitesAsync(target, new SiteAccessRequest(false, ["ITD"]), Ct))).ErrorMessages.Should().Equal(AdministrationErrors.NotFound);
        (await _host.AsCallerAsync(admin, s => Users(s).SetSitesAsync(target, new SiteAccessRequest(false, ["ITD"]), Ct))).HasErrors.Should().BeFalse();
        (await _host.AsCallerAsync(admin, s => Users(s).SetSitesAsync(target, new SiteAccessRequest(false, ["itd"]), Ct))).ErrorMessages.Should().Equal(AdministrationErrors.UnknownSite);
        (await _host.AsCallerAsync(admin, s => Users(s).SetSitesAsync(admin, new SiteAccessRequest(false, []), Ct))).ErrorMessages.Should().Equal(AdministrationErrors.OwnAccount);
        (await _host.AsCallerAsync(limited, s => Users(s).SetSitesAsync(target, new SiteAccessRequest(false, ["ITE"]), Ct))).ErrorMessages.Should().Equal(AdministrationErrors.BeyondOwnSites);
        (await _host.AsCallerAsync(limited, s => Users(s).SetSitesAsync(target, new SiteAccessRequest(true, []), Ct))).ErrorMessages.Should().Equal(AdministrationErrors.BeyondOwnSites);
        (await _host.AsCallerAsync(limited, s => Users(s).SetSitesAsync(target, new SiteAccessRequest(false, ["ITD"]), Ct))).HasErrors.Should().BeFalse("within its own sites");
    }

    [Fact]
    public async Task SiteAccess_Should_NeverBeEverySite_When_TheAccountsOnlyRoleIsValidationObserver()
    {
        // ARV-104a, first security review (2026-10-08): an observer counts at the sites of its campaigns, so an account whose only role is
        // Validation observer is bound to named sites. Creation, a site change, a grant and a revoke that would leave such an
        // account with every site are refused (400); an account that also holds another role follows that role.
        var admin = await AllSitesAdminAsync("it.obs.admin");
        await _host.AsCallerAsync(admin, s => Sites(s).CreateAsync(new CreateSiteRequest("ITO", "Integration observers"), Ct));
        var roles = (IServiceProvider s) => s.GetRequiredService<ISvcRoleAssignment>();

        (await _host.AsCallerAsync(admin, s => Users(s).CreateAsync(new CreateUserRequest("it.obs.every", Roles: [RoleCodes.ValidationObserver], AllSites: true), Ct)))
            .ErrorMessages.Should().Equal(AdministrationErrors.ObserverNeedsNamedSites);
        (await _host.ReadAsync<long>("SELECT count(*) FROM \"user\" WHERE user_name = 'it.obs.every'")).Should().Be(0, "nothing is created");
        var made = await _host.AsCallerAsync(admin, s => Users(s).CreateAsync(new CreateUserRequest("it.obs.named", Roles: [RoleCodes.ValidationObserver], SiteCodes: ["ITO"]), Ct));
        made.HasErrors.Should().BeFalse(string.Join(", ", made.ErrorMessages ?? []));
        var observer = made.Data.User.Id;

        (await _host.AsCallerAsync(admin, s => Users(s).SetSitesAsync(observer, new SiteAccessRequest(true, []), Ct)))
            .ErrorMessages.Should().Equal(AdministrationErrors.ObserverNeedsNamedSites);
        var access = await AccessOf(observer);
        access.AllSites.Should().BeFalse("the refused change wrote nothing");
        access.SiteCodes.Should().BeEquivalentTo(["ITO"]);

        // With a manager role as well, every site follows that role; revoking the manager role would leave an all-sites
        // observer, so it is refused until the sites are named again.
        (await _host.AsCallerAsync(admin, s => roles(s).GrantAsync(observer, RoleCodes.TerminalDutyManager, Ct))).HasErrors.Should().BeFalse();
        (await _host.AsCallerAsync(admin, s => Users(s).SetSitesAsync(observer, new SiteAccessRequest(true, []), Ct))).HasErrors.Should().BeFalse();
        (await _host.AsCallerAsync(admin, s => roles(s).RevokeAsync(observer, RoleCodes.TerminalDutyManager, Ct)))
            .ErrorMessages.Should().Equal(AdministrationErrors.ObserverNeedsNamedSites);
        (await _host.AsCallerAsync(admin, s => Users(s).SetSitesAsync(observer, new SiteAccessRequest(false, ["ITO"]), Ct))).HasErrors.Should().BeFalse();
        (await _host.AsCallerAsync(admin, s => roles(s).RevokeAsync(observer, RoleCodes.TerminalDutyManager, Ct))).Data.Roles.Should().Equal(RoleCodes.ValidationObserver);

        // An all-sites account without roles cannot be made an observer alone; with another role it can.
        var later = (await _host.AsCallerAsync(admin, s => Users(s).CreateAsync(new CreateUserRequest("it.obs.later", AllSites: true), Ct))).Data.User.Id;
        (await _host.AsCallerAsync(admin, s => roles(s).GrantAsync(later, RoleCodes.ValidationObserver, Ct)))
            .ErrorMessages.Should().Equal(AdministrationErrors.ObserverNeedsNamedSites);
        var dual = await _host.AsCallerAsync(admin, s => Users(s).CreateAsync(
            new CreateUserRequest("it.obs.dual", Roles: [RoleCodes.BorderShiftSupervisor, RoleCodes.ValidationObserver], AllSites: true), Ct));
        dual.HasErrors.Should().BeFalse(string.Join(", ", dual.ErrorMessages ?? []));
        (await _host.ReadAsync<long>("SELECT count(*) FROM user_role WHERE user_id = @id", later)).Should().Be(0);
        AdministrationErrors.Forbidden.Should().NotContain(AdministrationErrors.ObserverNeedsNamedSites, "a request the rule refuses is not valid (400), not a privilege refusal");
    }

    [Fact]
    public async Task SetSites_Should_EndSessionsOnlyWhenAccessNarrows_When_Changed()
    {
        var admin = await AllSitesAdminAsync("it.site.narrower");
        await _host.AsCallerAsync(admin, s => Sites(s).CreateAsync(new CreateSiteRequest("ITF", "ITF"), Ct));
        await _host.AsCallerAsync(admin, s => Sites(s).CreateAsync(new CreateSiteRequest("ITG", "ITG"), Ct));
        var officer = await _host.CreateUserAsync("it.site.narrowed");
        await _host.AsCallerAsync(admin, s => Users(s).SetSitesAsync(officer, new SiteAccessRequest(false, ["ITF"]), Ct));
        var session = SessionOf((await _host.LoginAsync("it.site.narrowed", Password)).Data.Token.AccessToken);

        await _host.AsCallerAsync(admin, s => Users(s).SetSitesAsync(officer, new SiteAccessRequest(false, ["ITF", "ITG"]), Ct));
        (await _host.SessionStateAsync(session)).Should().Be(SessionState.Active, "widening keeps the session");

        await _host.AsCallerAsync(admin, s => Users(s).SetSitesAsync(officer, new SiteAccessRequest(false, ["ITG"]), Ct));
        (await _host.SessionStateAsync(session)).Should().Be(SessionState.Revoked, "narrowing ends it");
    }

    [Fact]
    public async Task SiteLimitedAdministrator_Should_NotSeeOrTakeOverWiderAccounts_When_ActingOnUsers()
    {
        var wide = await AllSitesAdminAsync("it.site.wide");
        await _host.AsCallerAsync(wide, s => Sites(s).CreateAsync(new CreateSiteRequest("ITH", "ITH"), Ct));
        await _host.AsCallerAsync(wide, s => Sites(s).CreateAsync(new CreateSiteRequest("ITI", "ITI"), Ct));
        var limited = await _host.CreateUserAsync("it.site.lim.admin", roles: [RoleCodes.SystemAdministrator]);
        await _host.AsCallerAsync(wide, s => Users(s).SetSitesAsync(limited, new SiteAccessRequest(false, ["ITH"]), Ct));
        var inside = await _host.CreateUserAsync("it.site.inside");
        await _host.AsCallerAsync(wide, s => Users(s).SetSitesAsync(inside, new SiteAccessRequest(false, ["ITH"]), Ct));
        var outside = await _host.CreateUserAsync("it.site.outside");
        await _host.AsCallerAsync(wide, s => Users(s).SetSitesAsync(outside, new SiteAccessRequest(false, ["ITH", "ITI"]), Ct));
        var roles = (IServiceProvider s) => s.GetRequiredService<ISvcRoleAssignment>();
        var auth = (IServiceProvider s) => s.GetRequiredService<Ariva.Core.Services.Security.ISvcAuthenticator>();

        (await _host.AsCallerAsync(limited, s => Users(s).GetAsync(inside, Ct))).HasErrors.Should().BeFalse("inside its sites");
        foreach (var target in new[] { wide, outside })
        {
            (await _host.AsCallerAsync(limited, s => Users(s).GetAsync(target, Ct))).ErrorMessages.Should().Equal(AdministrationErrors.NotFound);
            (await _host.AsCallerAsync(limited, s => Users(s).ResetTotpAsync(target, Ct))).ErrorMessages.Should().Equal(AdministrationErrors.NotFound);
            (await _host.AsCallerAsync(limited, s => Users(s).ResetPasswordAsync(target, Ct))).ErrorMessages.Should().Equal(AdministrationErrors.NotFound);
            (await _host.AsCallerAsync(limited, s => roles(s).GrantAsync(target, RoleCodes.BorderShiftSupervisor, Ct))).ErrorMessages.Should().Equal(AdministrationErrors.NotFound);
            (await _host.AsCallerAsync(limited, s => auth(s).DisableAsync(target, Ct))).ErrorMessages.Should().Equal(Ariva.Core.Services.Security.ISvcAuthenticator.UserNotFound);
        }

        var fresh = await _host.CreateUserAsync("it.site.fresh.admin", roles: [RoleCodes.SystemAdministrator]);
        (await _host.AsCallerAsync(limited, s => Users(s).ResetPasswordAsync(fresh, Ct)))
            .ErrorMessages.Should().Equal(new[] { AdministrationErrors.NotFound }, "an administrator without sites yet belongs to the all-sites administrators");

        // ARV-059: any account without sites, not only an administrator, is beyond a site-limited administrator: it may
        // be about to get any site, and resetting it now would let the site administrator sign in as it later.
        var unsited = await _host.CreateUserAsync("it.site.unsited", roles: [RoleCodes.TerminalDutyManager]);
        (await _host.AsCallerAsync(limited, s => Users(s).GetAsync(unsited, Ct))).ErrorMessages.Should().Equal(AdministrationErrors.NotFound);
        (await _host.AsCallerAsync(limited, s => Users(s).ResetPasswordAsync(unsited, Ct))).ErrorMessages.Should().Equal(AdministrationErrors.NotFound);
        (await _host.AsCallerAsync(limited, s => Users(s).ResetTotpAsync(unsited, Ct))).ErrorMessages.Should().Equal(AdministrationErrors.NotFound);
        (await _host.AsCallerAsync(limited, s => Users(s).SetSitesAsync(unsited, new SiteAccessRequest(false, ["ITH"]), Ct))).ErrorMessages.Should().Equal(AdministrationErrors.NotFound);
        (await _host.AsCallerAsync(limited, s => roles(s).GrantAsync(unsited, RoleCodes.BorderShiftSupervisor, Ct))).ErrorMessages.Should().Equal(AdministrationErrors.NotFound);
        (await _host.AsCallerAsync(limited, s => auth(s).DisableAsync(unsited, Ct))).ErrorMessages.Should().Equal(Ariva.Core.Services.Security.ISvcAuthenticator.UserNotFound);
        (await _host.AsCallerAsync(limited, s => auth(s).UnlockAsync(unsited, Ct))).ErrorMessages.Should().Equal(Ariva.Core.Services.Security.ISvcAuthenticator.UserNotFound);

        // An account is created with its sites in the same request: a site administrator gives one of its own, never none,
        // another site or every site; what it creates stays inside its sites and administrable by it.
        (await _host.AsCallerAsync(limited, s => Users(s).CreateAsync(new CreateUserRequest("it.site.made.none", Roles: [RoleCodes.TerminalDutyManager]), Ct)))
            .ErrorMessages.Should().Equal(AdministrationErrors.SitesRequired);
        (await _host.AsCallerAsync(limited, s => Users(s).CreateAsync(new CreateUserRequest("it.site.made.iti", SiteCodes: ["ITI"]), Ct)))
            .ErrorMessages.Should().Equal(AdministrationErrors.BeyondOwnSites);
        (await _host.AsCallerAsync(limited, s => Users(s).CreateAsync(new CreateUserRequest("it.site.made.all", AllSites: true), Ct)))
            .ErrorMessages.Should().Equal(AdministrationErrors.BeyondOwnSites);
        (await _host.AsCallerAsync(limited, s => Users(s).CreateAsync(new CreateUserRequest("it.site.made.nope", SiteCodes: ["NOPE"]), Ct)))
            .ErrorMessages.Should().Equal(new[] { AdministrationErrors.BeyondOwnSites }, "an unknown site answers like another site's");
        var made = await _host.AsCallerAsync(limited, s => Users(s).CreateAsync(
            new CreateUserRequest("it.site.made.admin", Roles: [RoleCodes.SystemAdministrator], SiteCodes: ["ITH"]), Ct));
        made.HasErrors.Should().BeFalse(string.Join(", ", made.ErrorMessages ?? []));
        made.Data.User.Should().Match<UserViewModel>(u => !u.AllSites && u.Sites.SequenceEqual(new[] { "ITH" }));
        (await _host.AsCallerAsync(limited, s => Users(s).GetAsync(made.Data.User.Id, Ct))).HasErrors.Should().BeFalse("its own creation stays its to administer");
        (await _host.ReadAsync<string>("SELECT after_summary FROM audit_entry WHERE target_id = @id AND action = 'User.Created'", made.Data.User.Id))
            .Should().Contain("sites=ITH;", "the sites are part of the creation");

        // An all-sites administrator may still create an account for every site, or one without sites for later.
        var everywhere = await _host.AsCallerAsync(wide, s => Users(s).CreateAsync(new CreateUserRequest("it.site.made.every", AllSites: true), Ct));
        everywhere.Data.User.AllSites.Should().BeTrue();
        (await _host.AsCallerAsync(wide, s => Users(s).CreateAsync(new CreateUserRequest("it.site.made.later"), Ct))).HasErrors.Should().BeFalse();

        var listed = await _host.AsCallerAsync(limited, s => Users(s).SearchAsync(new Ariva.Core.Domain.Criteria.UserCriteria { Text = "it.site.", PageSize = 500 }, Ct));
        listed.Data.Data.Select(u => u.UserName).Should().Contain("it.site.inside").And.NotContain(["it.site.wide", "it.site.outside"]);
        var audit = await _host.AsCallerAsync(limited, s => s.GetRequiredService<ISvcAuditEntries>().SearchAsync(new Ariva.Core.Domain.Criteria.AuditEntryCriteria { PageSize = 500 }, Ct));
        audit.Data.Data.Should().NotContain(e => e.TargetId == outside || e.TargetId == wide || e.TargetName == "ITI");
        audit.Data.Data.Should().Contain(e => e.TargetId == inside);

        (await _host.AsCallerAsync(limited, s => Users(s).SetSitesAsync(inside, new SiteAccessRequest(false, ["ITI"]), Ct))).ErrorMessages.Should().Equal(AdministrationErrors.BeyondOwnSites);
        (await _host.AsCallerAsync(limited, s => Users(s).SetSitesAsync(inside, new SiteAccessRequest(false, ["NOPE"]), Ct)))
            .ErrorMessages.Should().Equal(new[] { AdministrationErrors.BeyondOwnSites }, "an unknown site answers like another site's");
        (await _host.AsCallerAsync(limited, s => Sites(s).CreateAsync(new CreateSiteRequest("ITH", "x"), Ct)))
            .ErrorMessages.Should().Equal(new[] { AdministrationErrors.BeyondOwnSites }, "creating sites is deployment-wide, so no 409 oracle");
    }

    [Fact]
    public async Task LastAdministrator_Should_SurviveTwoAdministratorsRemovingEachOther_When_TheyRace()
    {
        await _host.ReadAsync<int>("UPDATE \"user\" SET is_disabled = true WHERE NOT is_break_glass AND id IN (SELECT user_id FROM user_role WHERE role_code = 'SystemAdministrator') RETURNING 1");
        var a = await AllSitesAdminAsync("it.site.race.a");
        var b = await AllSitesAdminAsync("it.site.race.b");
        var roles = (IServiceProvider s) => s.GetRequiredService<ISvcRoleAssignment>();

        var results = await Task.WhenAll(
            _host.AsCallerAsync(a, s => roles(s).RevokeAsync(b, RoleCodes.SystemAdministrator, Ct)),
            _host.AsCallerAsync(b, s => roles(s).RevokeAsync(a, RoleCodes.SystemAdministrator, Ct)));

        results.Count(r => !r.HasErrors).Should().Be(1, "the advisory lock lets only one of the two revokes through");
        // The loser either waited on the lock and found itself the last administrator, or started after the winner had
        // committed and no longer holds the role at all; both keep one administrator.
        results.Single(r => r.HasErrors).ErrorMessages.Should().ContainSingle()
            .Which.Should().BeOneOf(AdministrationErrors.LastAdministrator, AdministrationErrors.AboveOwnRole);
        (await _host.ReadAsync<long>("SELECT count(*) FROM user_role r JOIN \"user\" u ON u.id = r.user_id WHERE r.role_code = 'SystemAdministrator' AND NOT u.is_disabled AND NOT u.is_break_glass"))
            .Should().Be(1);

        var survivor = results[0].HasErrors ? b : a;
        var other = await _host.CreateUserAsync("it.site.race.c", roles: [RoleCodes.BorderShiftSupervisor]);
        (await _host.AsCallerAsync(other, s => s.GetRequiredService<Ariva.Core.Services.Security.ISvcAuthenticator>().DisableAsync(survivor, Ct)))
            .HasErrors.Should().BeTrue("a non-administrator never gets here through the API, and the guard refuses anyway");
        var breakGlassExists = await _host.ReadAsync<long>("SELECT count(*) FROM \"user\" WHERE is_break_glass") > 0;
        await _host.IssueBreakGlassAsync(rotate: breakGlassExists);
        var breakGlass = await _host.ReadAsync<Guid>("SELECT id FROM \"user\" WHERE is_break_glass");
        (await _host.AsCallerAsync(breakGlass, s => s.GetRequiredService<Ariva.Core.Services.Security.ISvcAuthenticator>().DisableAsync(survivor, Ct)))
            .ErrorMessages.Should().Equal(Ariva.Core.Services.Security.ISvcAuthenticator.LastAdministrator);
        (await _host.AsCallerAsync(survivor, s => s.GetRequiredService<Ariva.Core.Services.Security.ISvcAuthenticator>().DisableAsync(breakGlass, Ct)))
            .ErrorMessages.Should().Equal(new[] { Ariva.Core.Services.Security.ISvcAuthenticator.UserNotFound }, "the break-glass account is not administered through the API");
        (await AccessOf(breakGlass)).AllSites.Should().BeTrue("the break-glass account reaches every site, so it can bootstrap site access");
    }
}

