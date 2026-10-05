using System.Net;
using System.Reflection;
using Ariva.Api.Common.Security;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Security;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-012: every action that takes a site, airport or terminal reference is [SiteScoped]; the filter answers 404 for a
/// site outside the caller's access; access checks and query filters behave for all-sites and listed-sites callers.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class SiteScopeTests
{
    #region Architecture

    [Fact]
    public void Actions_Should_BeSiteScoped_When_TheyTakeASiteReference()
    {
        var violations = Unscoped(ArivaAssemblies.Hosts.SelectMany(assembly => assembly.GetTypes()));

        violations.Should().BeEmpty("an action with a site, airport or terminal reference must carry [SiteScoped] (CWE-863)");
    }

    [Fact]
    public void Unscoped_Should_ReportEveryUnscopedSiteReference_When_AControllerForgetsTheAttribute()
    {
        var violations = Unscoped([typeof(FixtureUnscopedController), typeof(FixtureScopedController)]);

        violations.Should().BeEquivalentTo(
            "FixtureUnscopedController.ByCode(siteCode)",
            "FixtureUnscopedController.ByAirport(airportId)",
            "FixtureUnscopedController.ByRoute(terminalId)",
            "FixtureUnscopedController.Create(SiteCode)");
    }

    [Fact]
    public void Unscoped_Should_ReportSiteReferencesBeyondTheRouteSite_When_AnIntegrationActionTakesThem()
    {
        var violations = Unscoped([typeof(FixtureIntegrationController), typeof(FixtureIntegrationFlatController)]);

        violations.Should().BeEquivalentTo(
            "FixtureIntegrationController.Body(SiteCode)",
            "FixtureIntegrationController.Query(airportCode)",
            "FixtureIntegrationController.Terminal(terminalId)",
            "FixtureIntegrationController.Header(siteCode)",
            "FixtureIntegrationFlatController.QuerySite(siteCode)");
    }

    /// <summary>
    /// Actions with a site reference in a parameter or route template and no [SiteScoped] on the action or controller.
    /// An Integration API action ([IntegrationScope], ARV-042) is checked against the client's sites by the scope handler,
    /// which reads only the <c>{siteCode}</c> route token: it may name a site there and nowhere else.
    /// </summary>
    public static List<string> Unscoped(IEnumerable<Type> types)
    {
        var violations = new List<string>();
        foreach (var controller in types.Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract))
        {
            var controllerScoped = controller.GetCustomAttribute<SiteScopedAttribute>(inherit: true) is not null;
            var controllerRoute = controller.GetCustomAttributes<RouteAttribute>().SelectMany(r => RouteParameters(r.Template)).ToList();
            foreach (var action in controller.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                if (controllerScoped || action.GetCustomAttribute<SiteScopedAttribute>(inherit: true) is not null)
                    continue;

                var route = action.GetCustomAttributes<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>().SelectMany(RouteParameters).Concat(controllerRoute).ToList();
                var integration = action.GetCustomAttribute<IntegrationScopeAttribute>(inherit: true) is not null ||
                                  controller.GetCustomAttribute<IntegrationScopeAttribute>(inherit: true) is not null;
                var routeSite = integration && route.Contains(IntegrationRouteSite, StringComparer.Ordinal);
                var references = action.GetParameters().Where(p => !(routeSite && p.Name == IntegrationRouteSite && FromRouteOnly(p))).Select(p => p.Name)
                    .Concat(action.GetParameters().Where(p => IsRequestModel(p.ParameterType)).SelectMany(p => p.ParameterType.GetProperties().Select(property => property.Name)))
                    .Concat(route.Where(name => !(integration && name == IntegrationRouteSite)))
                    .Where(name => SiteScopedAttribute.SiteReferences.Contains(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (references.Count > 0)
                    violations.Add($"{controller.Name}.{action.Name}({references[0]})");
            }
        }

        return violations;
    }

    /// <summary>A parameter bound from the route (no attribute, or [FromRoute]): one with [FromQuery], [FromHeader] or [FromBody] could differ from the route value the handler checks.</summary>
    private static bool FromRouteOnly(ParameterInfo parameter) =>
        parameter.GetCustomAttributes(inherit: true).OfType<Microsoft.AspNetCore.Mvc.ModelBinding.IBindingSourceMetadata>()
            .All(b => b.BindingSource == Microsoft.AspNetCore.Mvc.ModelBinding.BindingSource.Path);

    /// <summary>The one route token through which an Integration API action names its site.</summary>
    private const string IntegrationRouteSite = "siteCode";

    /// <summary>Request models (Ariva or fixture types bound from the body or query) whose properties may name a site.</summary>
    private static bool IsRequestModel(Type type) =>
        type.IsClass && type != typeof(string) && type.Namespace?.StartsWith("Ariva", StringComparison.Ordinal) == true;

    private static IEnumerable<string> RouteParameters(Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute attribute) => RouteParameters(attribute.Template);

    private static IEnumerable<string> RouteParameters(string template) =>
        template is null
            ? []
            : System.Text.RegularExpressions.Regex.Matches(template, @"\{\*{0,2}(?<name>[A-Za-z]+)", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1))
                .Select(m => m.Groups["name"].Value);

    #endregion

    #region Access

    [Fact]
    public void SiteAccess_Should_AllowOnlyListedSites_When_NotAllSites()
    {
        var access = new SiteAccess(false, new HashSet<string> { "AMM", "AUH-T1" });

        access.Allows("AMM").Should().BeTrue();
        access.Allows("DXB").Should().BeFalse();
        access.Allows(null).Should().BeFalse();
        SiteAccess.Everything.Allows("DXB").Should().BeTrue();
        SiteAccess.None.Allows("AMM").Should().BeFalse();
    }

    [Fact]
    public void Covers_Should_RefuseWiderAccess_When_GrantingSites()
    {
        var amm = new SiteAccess(false, new HashSet<string> { "AMM" });
        var both = new SiteAccess(false, new HashSet<string> { "AMM", "DXB" });

        SiteAccess.Everything.Covers(both).Should().BeTrue();
        SiteAccess.Everything.Covers(SiteAccess.Everything).Should().BeTrue();
        both.Covers(amm).Should().BeTrue();
        amm.Covers(both).Should().BeFalse();
        both.Covers(SiteAccess.Everything).Should().BeFalse("only an all-sites caller grants all sites");
        amm.Covers(SiteAccess.None).Should().BeTrue();
    }

    [Fact]
    public void WithinSites_Should_FilterRecords_When_AccessIsListed()
    {
        var records = new[] { new Bound("AMM"), new Bound("DXB"), new Bound("AUH-T1") }.AsQueryable();

        records.WithinSites(new SiteAccess(false, new HashSet<string> { "AMM", "AUH-T1" })).Select(r => r.SiteCode).ToList().Should().Equal("AMM", "AUH-T1");
        records.WithinSites(SiteAccess.Everything).ToList().Should().HaveCount(3);
        records.WithinSites(SiteAccess.None).ToList().Should().HaveCount(0);
    }

    [Theory]
    [InlineData("AMM", true)]
    [InlineData("AUH-T1", true)]
    [InlineData("A", false)]
    [InlineData("amm", false)]
    [InlineData("AMM-", false)]
    [InlineData("ABCDEFGHI", false)]
    [InlineData("AMM/../DXB", false)]
    [InlineData("AMM'--", false)]
    public void IsValidCode_Should_AcceptOnlyUpperCaseCodes_When_Checked(string code, bool valid)
    {
        Site.IsValidCode(code).Should().Be(valid);
    }

    [Fact]
    public void SetSites_Should_ReturnTheBindingsToDeleteAndSave_When_AccessChanges()
    {
        var user = new User("ops.sites", null, null);
        var at = new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc);

        var first = user.SetSites(false, ["AMM", "DXB"], null, at);
        var second = user.SetSites(true, ["DXB", "AUH-T1"], null, at);

        first.Added.Select(s => s.SiteCode).Should().Equal("AMM", "DXB");
        second.Removed.Select(s => s.SiteCode).Should().Equal("AMM");
        second.Added.Select(s => s.SiteCode).Should().Equal("AUH-T1");
        user.AllSites.Should().BeTrue();
        user.Sites.Select(s => s.SiteCode).Should().BeEquivalentTo("DXB", "AUH-T1");
    }

    #endregion

    #region Filter over HTTP

    [Theory]
    [InlineData("AMM", HttpStatusCode.OK, "inside the caller's sites")]
    [InlineData("DXB", HttpStatusCode.NotFound, "outside the caller's sites: the same 404 as an unknown site")]
    public async Task SiteScoped_Should_AnswerNotFound_When_SiteIsOutsideTheCallersAccess(string siteCode, HttpStatusCode expected, string because)
    {
        var scope = new FixedSiteScope(new SiteAccess(false, new HashSet<string> { "AMM" }));
        await using var app = ArivaHosts.Create(ArivaHosts.Main, configure: builder => builder.ConfigureTestServices(services =>
        {
            TestAuthenticationHandler.Register(services);
            FakeAdministration.Register(services);
            services.Replace(ServiceDescriptor.Scoped<ISiteScope>(_ => scope));
            services.Replace(ServiceDescriptor.Scoped<Ariva.Core.Services.Administration.ISvcSites, EverySite>());
        }));
        using var client = app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/sites/{siteCode}");
        request.Headers.Add(TestAuthenticationHandler.UserHeader, "site-user");
        request.Headers.Add(TestAuthenticationHandler.RolesHeader, "BorderShiftSupervisor");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(expected, because);
        scope.Calls.Should().Be(1);
    }

    [Theory]
    [InlineData("/api/v1/sites/AMM%27--")]
    [InlineData("/api/v1/sites/AMMANAIRPORT1")]
    [InlineData("/api/v1/admin/displays?siteCode=DM%22")]
    public async Task SiteScoped_Should_AnswerNotFoundWithoutLookingUp_When_TheValueCannotBeASiteCode(string path)
    {
        // ARV-063: ZAP sent siteCode=%00, which reached PostgreSQL and answered 500 (a NUL now answers 400 before any filter,
        // NulCharacterTests). Any other value that cannot be a site code is a site that does not exist, even for a caller
        // with every site.
        var scope = new FixedSiteScope(new SiteAccess(true, new HashSet<string>()));
        await using var app = ArivaHosts.Create(ArivaHosts.Main, configure: builder => builder.ConfigureTestServices(services =>
        {
            TestAuthenticationHandler.Register(services);
            FakeAdministration.Register(services);
            FakeDisplays.Register(services);
            services.Replace(ServiceDescriptor.Scoped<ISiteScope>(_ => scope));
            services.Replace(ServiceDescriptor.Scoped<Ariva.Core.Services.Administration.ISvcSites, EverySite>());
        }));
        using var client = app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(TestAuthenticationHandler.UserHeader, "site-admin");
        request.Headers.Add(TestAuthenticationHandler.RolesHeader, Ariva.Core.RoleCodes.SystemAdministrator);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        scope.Calls.Should().Be(0, "the value never reaches the site lookup");
    }

    #endregion

    /// <summary>Finds any site, so only the filter can answer 404.</summary>
    private sealed class EverySite : Ariva.Core.Services.Administration.ISvcSites
    {
        public Task<Fluentx.Result<IReadOnlyList<Ariva.Core.Domain.ViewModels.SiteViewModel>>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult(new Fluentx.Result<IReadOnlyList<Ariva.Core.Domain.ViewModels.SiteViewModel>>(Array.Empty<Ariva.Core.Domain.ViewModels.SiteViewModel>()));

        public Task<Fluentx.Result<Ariva.Core.Domain.ViewModels.SiteViewModel>> GetAsync(string code, CancellationToken ct = default) =>
            Task.FromResult(new Fluentx.Result<Ariva.Core.Domain.ViewModels.SiteViewModel>(new Ariva.Core.Domain.ViewModels.SiteViewModel(code, code, null)));

        public Task<Fluentx.Result<Ariva.Core.Domain.ViewModels.SiteViewModel>> CreateAsync(Ariva.Core.Domain.InputModels.CreateSiteRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Fluentx.Result<Ariva.Core.Domain.ViewModels.SiteViewModel>> UpdateAsync(string code, Ariva.Core.Domain.InputModels.UpdateSiteRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed record Bound(string SiteCode) : Core.Domain.Contracts.ISiteBound;

    private sealed class FixedSiteScope(SiteAccess access) : ISiteScope
    {
        public int Calls { get; private set; }

        public Task<SiteAccess> GetAsync(CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(access);
        }
    }
}
