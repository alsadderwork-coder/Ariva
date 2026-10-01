using System.Reflection;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.UnitTests.Security;

/// <summary>
/// CWE-862 and CWE-306: every endpoint of every host makes an explicit authorization decision. The fallback policy
/// already denies endpoints without metadata, so this inventory catches the omission before it ships: an endpoint
/// either carries authorization data (<c>[Authorize]</c>, later <c>[Permission]</c>) or is explicitly anonymous,
/// and every anonymous route is listed in security/allowlist.json.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class EndpointInventoryTests
{
    #region Fields

    private static readonly IReadOnlyList<AllowlistEntry> AnonymousEntries = SecurityAllowlist.Load()
        .Where(entry => entry.Rule == SecurityAllowlist.AnonymousRule)
        .ToList();

    #endregion

    #region Tests

    [Theory]
    [MemberData(nameof(ArivaHosts.AllHosts), MemberType = typeof(ArivaHosts))]
    public async Task GetEndpoints_Should_CarryAnAuthorizationDecision_When_HostIsBooted(string host)
    {
        await using var app = ArivaHosts.Create(host);

        var endpoints = EndpointsOf(app);
        var violations = endpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<IAuthorizeData>() is null
                               && endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null)
            .Select(Describe)
            .ToList();

        endpoints.Should().NotBeEmpty("every host maps at least the health probes");
        violations.Should().BeEmpty("every endpoint needs [Authorize], [Permission] or an allowlisted AllowAnonymous()");
    }

    [Theory]
    [MemberData(nameof(ArivaHosts.AllHosts), MemberType = typeof(ArivaHosts))]
    public async Task GetEndpoints_Should_ListEveryAnonymousRouteInTheAllowlist_When_HostIsBooted(string host)
    {
        await using var app = ArivaHosts.Create(host);

        var anonymous = EndpointsOf(app)
            .Where(endpoint => endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .ToList();
        var violations = anonymous
            .Where(endpoint => !IsAllowlisted(endpoint))
            .Select(Describe)
            .ToList();

        anonymous.Should().NotBeEmpty("the health probes are anonymous on every host");
        violations.Should().BeEmpty("anonymous endpoints need a SEC-052 entry that lists the route in security/allowlist.json");
    }

    [Fact]
    public async Task GetEndpoints_Should_RequireAuthorization_When_EndpointIsSystemInfo()
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Main);

        var systemInfo = EndpointsOf(app).Single(endpoint => RouteOf(endpoint) == "api/v1/system/info");

        systemInfo.Metadata.GetMetadata<IAuthorizeData>().Should().NotBeNull();
        systemInfo.Metadata.GetMetadata<IAllowAnonymous>().Should().BeNull();
    }

    #endregion

    #region Helpers

    private static List<Endpoint> EndpointsOf(IArivaHost app) =>
        app.Services.GetRequiredService<EndpointDataSource>().Endpoints.ToList();

    private static string RouteOf(Endpoint endpoint) =>
        (endpoint as RouteEndpoint)?.RoutePattern.RawText?.TrimStart('/') ?? endpoint.DisplayName;

    private static bool IsAllowlisted(Endpoint endpoint)
    {
        var route = "/" + RouteOf(endpoint);
        var source = SourceAssemblyOf(endpoint);

        return AnonymousEntries.Any(entry =>
            entry.Routes.Contains(route, StringComparer.Ordinal)
            && (source is null || entry.Path.Contains($"/{source}/", StringComparison.Ordinal)));
    }

    /// <summary>The assembly that declares the endpoint's handler, which tells which allowlist entry covers it.</summary>
    private static string SourceAssemblyOf(Endpoint endpoint)
    {
        var handler = endpoint.Metadata.GetMetadata<MethodInfo>();
        if (handler?.DeclaringType is not null)
        {
            return handler.DeclaringType.Assembly.GetName().Name;
        }

        return endpoint.Metadata.GetMetadata<ControllerActionDescriptor>()?.ControllerTypeInfo.Assembly.GetName().Name;
    }

    private static string Describe(Endpoint endpoint)
    {
        var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [];
        return $"{string.Join(",", methods)} /{RouteOf(endpoint)} ({endpoint.DisplayName})";
    }

    #endregion
}
