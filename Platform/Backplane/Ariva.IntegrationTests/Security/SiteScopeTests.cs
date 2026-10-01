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
        (await _host.AsCallerAsync(officer, s => Sites(s).GetAsync("NOPE", Ct))).ErrorMessages.Should().Equal(AdministrationErrors.NotFound, "an unknown site answers the same");
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
}
