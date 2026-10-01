using Ariva.Core;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Security;
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
        (await _host.AsCallerAsync(admin, s => Users(s).SetSitesAsync(target, new SiteAccessRequest(false, ["itd"]), Ct))).ErrorMessages.Should().Equal(AdministrationErrors.UnknownSite);
        (await _host.AsCallerAsync(admin, s => Users(s).SetSitesAsync(admin, new SiteAccessRequest(false, []), Ct))).ErrorMessages.Should().Equal(AdministrationErrors.OwnAccount);
        (await _host.AsCallerAsync(limited, s => Users(s).SetSitesAsync(target, new SiteAccessRequest(false, ["ITE"]), Ct))).ErrorMessages.Should().Equal(AdministrationErrors.BeyondOwnSites);
        (await _host.AsCallerAsync(limited, s => Users(s).SetSitesAsync(target, new SiteAccessRequest(true, []), Ct))).ErrorMessages.Should().Equal(AdministrationErrors.BeyondOwnSites);
        (await _host.AsCallerAsync(limited, s => Users(s).SetSitesAsync(target, new SiteAccessRequest(false, ["ITD"]), Ct))).HasErrors.Should().BeFalse("within its own sites");
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

