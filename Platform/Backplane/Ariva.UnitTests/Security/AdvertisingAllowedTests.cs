using System.Reflection;
using Ariva.Api.Common.Security;
using Ariva.Core.Security;
using Ariva.Core.Services.Displays;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-145: an advertising endpoint ([AdvertisingAllowed]) acts only for a site of the caller whose advertising policy allows
/// advertising: 404 for a site outside the caller's sites, a site that does not exist or a value that cannot be a site code
/// (nothing revealed), 403 for
/// a site whose policy is off or missing and for an action without a site. Every advertising endpoint of the booted hosts
/// carries it (the media library and playlists, ARV-142a and ARV-143: a route naming advertising, or a controller in an
/// <c>Advertising</c> namespace), except the policy itself and the display fetch of ARV-142a, which is display-authenticated
/// and applies the advertising rule to the display instead (it must carry <see cref="DisplayAuthenticatedAttribute"/>).
/// </summary>
[Collection(HostCollection.Name)]
public sealed class AdvertisingAllowedTests
{
    [Theory]
    [InlineData("OPEN", 0, "the caller's site, advertising allowed")]
    [InlineData("SHUT", 403, "the caller's site, advertising off")]
    [InlineData("NONE", 403, "the caller's site, never given a policy")]
    [InlineData("AWAY", 404, "a site outside the caller's sites, even with advertising allowed")]
    [InlineData("GONE", 404, "a site the caller's scope allows that does not exist")]
    [InlineData("open", 404, "a value that is not a site code")]
    [InlineData("<script>", 404, "markup")]
    [InlineData(null, 403, "no site")]
    public async Task Filter_Should_LetTheActionRunOnlyForAnAllowedSiteOfTheCaller(string siteCode, int expected, string reason)
    {
        var (result, ran) = await RunAsync(siteCode is null ? [] : new Dictionary<string, object> { ["siteCode"] = siteCode });

        if (expected == 0)
        {
            ran.Should().BeTrue(reason);
            result.Should().BeNull(reason);
            return;
        }

        ran.Should().BeFalse(reason);
        var answer = result.Should().BeOfType<ObjectResult>().Subject;
        answer.StatusCode.Should().Be(expected, reason);
        var problem = answer.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Detail.Should().Be(expected == 403 ? AdvertisingPolicyErrors.Off : null, "a fixed text, never the value sent");
        problem.Title.Should().Be(expected == 403 ? "Not allowed" : "Not found", "every 404 answers alike, so a site outside the caller's sites looks like one that does not exist");
    }

    [Fact]
    public async Task AdvertisingEndpoints_Should_CarryTheFilter_When_AHostIsBooted()
    {
        var violations = new List<string>();
        foreach (var host in ArivaHosts.Backplane)
        {
            await using var app = ArivaHosts.Create(host);
            violations.AddRange(app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
                .Where(endpoint => IsAdvertising(endpoint) && endpoint.Metadata.GetMetadata<AdvertisingAllowedAttribute>() is null)
                .Select(endpoint => $"{host} {endpoint.RoutePattern.RawText}"));
        }

        violations.Should().BeEmpty("an advertising endpoint acts only for a site whose policy allows advertising (ARV-145, CWE-862)");
    }

    [Fact]
    public async Task DisplayFetch_Should_BeDisplayAuthenticatedOnly_When_ItIsExemptFromTheFilter()
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Main);
        var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<ControllerActionDescriptor>()?.ControllerTypeInfo.AsType() == typeof(Ariva.Api.Main.Controllers.OpsArea.Displays.DisplayAdvertisingController))
            .ToList();

        endpoints.Should().NotBeEmpty();
        endpoints.Should().AllSatisfy(e =>
        {
            e.Metadata.GetMetadata<DisplayAuthenticatedAttribute>().Should().NotBeNull("only a display player fetches advertising media (ARV-142a)");
            e.Metadata.GetOrderedMetadata<PermissionAttribute>().Should().BeEmpty("no user permission reaches the display fetch");
        });
    }

    public static TheoryData<string, Type, bool> Classified => new()
    {
        { "api/v1/admin/sites/{siteCode}/media", typeof(Fixtures.Advertising.AdvertisingNamespaceMarker), true },
        { "api/v1/admin/[controller]", typeof(Fixtures.Advertising.AdvertisingNamespaceMarker), true },
        { "api/v1/admin/sites/{siteCode}/advertising/playlists", typeof(AdvertisingAllowedTests), true },
        { "api/v1/admin/sites/{siteCode}/Advertising-Media", typeof(AdvertisingAllowedTests), true },
        { "api/v1/admin/sites/{siteCode}/advertising-policy/media", typeof(AdvertisingAllowedTests), true },
        { "api/v1/admin/sites/{siteCode}/advertising-policy", typeof(Ariva.Api.Main.Controllers.AdminArea.Sites.SiteAdvertisingPolicyController), false },
        { "api/v1/admin/sites/{siteCode}/advertising/media", typeof(Ariva.Api.Main.Controllers.AdminArea.Advertising.AdvertisingMediaController), true },
        { "api/v1/display/advertising/{id:guid}", typeof(Ariva.Api.Main.Controllers.OpsArea.Displays.DisplayAdvertisingController), false },
        { "api/v1/display/advertising/{id:guid}", typeof(AdvertisingAllowedTests), true },
        { "api/v1/admin/display-slides", typeof(Ariva.Api.Main.Controllers.AdminArea.DisplaySlides.DisplaySlidesController), false }
    };

    [Theory]
    [MemberData(nameof(Classified))]
    public void IsAdvertising_Should_RecogniseAnEndpointByItsRouteOrItsNamespace_When_OnlyThePolicyItselfIsExempt(string route, Type controller, bool expected)
    {
        var endpoint = new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse(route), 0,
            new EndpointMetadataCollection(new ControllerActionDescriptor { ControllerTypeInfo = controller.GetTypeInfo() }), route);

        IsAdvertising(endpoint).Should().Be(expected, route);
    }

    /// <summary>
    /// An advertising endpoint: its route names advertising, or its controller lives in an <c>Advertising</c> namespace (the
    /// convention for ARV-142a and ARV-143, whatever their routes say). Exempt by their types only: the policy itself, and the
    /// display fetch (display-authenticated, <see cref="DisplayFetch_Should_BeDisplayAuthenticatedOnly_When_ItIsExemptFromTheFilter"/>).
    /// </summary>
    private static bool IsAdvertising(RouteEndpoint endpoint)
    {
        var controller = endpoint.Metadata.GetMetadata<ControllerActionDescriptor>()?.ControllerTypeInfo;
        if (controller?.AsType() == typeof(Ariva.Api.Main.Controllers.AdminArea.Sites.SiteAdvertisingPolicyController) ||
            controller?.AsType() == typeof(Ariva.Api.Main.Controllers.OpsArea.Displays.DisplayAdvertisingController))
            return false;
        return (endpoint.RoutePattern.RawText ?? string.Empty).Contains("advertising", StringComparison.OrdinalIgnoreCase) ||
               (controller?.Namespace ?? string.Empty).Split('.').Contains("Advertising", StringComparer.Ordinal);
    }

    private static async Task<(IActionResult Result, bool Ran)> RunAsync(IDictionary<string, object> arguments)
    {
        var services = new ServiceCollection()
            .AddSingleton<ISiteScope>(new Scope())
            .AddSingleton<ISvcAdvertisingPolicies>(new Policies())
            .BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var context = new ActionExecutingContext(action, [], arguments, controller: null);
        var ran = false;
        await new AdvertisingAllowedAttribute().OnActionExecutionAsync(context, () =>
        {
            ran = true;
            return Task.FromResult(new ActionExecutedContext(action, [], controller: null));
        });
        return (context.Result, ran);
    }

    /// <summary>The caller's sites: OPEN, SHUT, NONE and GONE (which does not exist); AWAY is another site's.</summary>
    private sealed class Scope : ISiteScope
    {
        public Task<SiteAccess> GetAsync(CancellationToken ct = default) =>
            Task.FromResult(new SiteAccess(false, new HashSet<string>(["OPEN", "SHUT", "NONE", "GONE"], StringComparer.Ordinal)));
    }

    /// <summary>OPEN and AWAY allow advertising; SHUT turned it off; NONE never had a policy; GONE does not exist.</summary>
    private sealed class Policies : ISvcAdvertisingPolicies
    {
        public Task<Fluentx.Result<AdvertisingPolicyViewModel>> GetAsync(string siteCode, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<Fluentx.Result<AdvertisingPolicyViewModel>> ChangeAsync(string siteCode, AdvertisingPolicyRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> IsAllowedAsync(string siteCode, CancellationToken ct = default) => Task.FromResult(siteCode is "OPEN" or "AWAY");

        public Task<AdvertisingAccess> AccessAsync(string siteCode, CancellationToken ct = default) => Task.FromResult(siteCode switch
        {
            "OPEN" or "AWAY" => AdvertisingAccess.Allowed,
            "SHUT" or "NONE" => AdvertisingAccess.Off,
            _ => AdvertisingAccess.NotFound
        });
    }
}
