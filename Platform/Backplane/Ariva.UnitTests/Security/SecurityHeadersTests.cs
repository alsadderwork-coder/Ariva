using System.Net.Http.Headers;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Ariva.Api.Common.Middlewares;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

namespace Ariva.UnitTests.Security;

/// <summary>
/// CWE-79 and CWE-200: every API response, including challenges and errors, carries the security headers and no
/// Server header; responses to requests that carry credentials are never cached.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class SecurityHeadersTests
{
    #region Fields

    private const string Liveness = "/health/liveness";

    private const string SystemInfo = "/api/v1/system/info";

    private static readonly IReadOnlyDictionary<string, string> ExpectedHeaders = new Dictionary<string, string>
    {
        ["X-Content-Type-Options"] = "nosniff",
        ["X-Frame-Options"] = "DENY",
        ["Referrer-Policy"] = "no-referrer",
        ["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'"
    };

    #endregion

    #region Tests

    [Theory]
    [MemberData(nameof(ArivaHosts.AllHosts), MemberType = typeof(ArivaHosts))]
    public async Task Get_Should_SendSecurityHeaders_When_ProbeAnswers(string host)
    {
        await using var app = ArivaHosts.Create(host);
        using var client = app.CreateClient();

        using var response = await client.GetAsync(Liveness, TestContext.Current.CancellationToken);

        ShouldCarrySecurityHeaders(response);
        response.Header("Cache-Control").Should().BeNull("anonymous probe answers carry no credentials");
    }

    [Theory]
    [MemberData(nameof(ArivaHosts.AllHosts), MemberType = typeof(ArivaHosts))]
    public async Task Get_Should_SendSecurityHeaders_When_RequestIsRefused(string host)
    {
        await using var app = ArivaHosts.Create(host);
        using var client = app.CreateClient();

        using var response = await client.GetAsync(SystemInfo, TestContext.Current.CancellationToken);

        ((int)response.StatusCode).Should().Be(401);
        ShouldCarrySecurityHeaders(response);
    }

    [Theory]
    [MemberData(nameof(ArivaHosts.AllHosts), MemberType = typeof(ArivaHosts))]
    public async Task Get_Should_SendNoStore_When_RequestCarriesABearerToken(string host)
    {
        await using var app = ArivaHosts.Create(host);
        using var client = app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, SystemInfo);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "eyJhbGciOiJIUzI1NiJ9.e30.c2ln");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.Header("Cache-Control").Should().Be("no-store");
        ShouldCarrySecurityHeaders(response);
    }

    [Fact]
    public async Task Get_Should_SendNoStore_When_CallerIsAuthenticated()
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Main, configure: builder => builder.ConfigureTestServices(TestAuthenticationHandler.Register));
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.UserHeader, "administrator");
        // System info needs SystemInfo.View, which only system administrators hold (ARV-009).
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.RolesHeader, Ariva.Core.RoleCodes.SystemAdministrator);

        using var response = await client.GetAsync(SystemInfo, TestContext.Current.CancellationToken);

        ((int)response.StatusCode).Should().Be(200);
        response.Header("Cache-Control").Should().Be("no-store");
        ShouldCarrySecurityHeaders(response);
    }

    [Theory]
    [InlineData("default-src 'none'; style-src 'unsafe-inline'; sandbox; frame-ancestors 'none'", true)]
    [InlineData("default-src 'none'; sandbox", false)]
    [InlineData("default-src *; frame-ancestors 'none'", false)]
    [InlineData("script-src 'unsafe-inline'", false)]
    public async Task Get_Should_KeepOnlyALockedDownEndpointPolicy_When_EndpointSetsItsOwn(string policy, bool kept)
    {
        // ARV-018: the floor plan image sets a sandboxing policy; anything looser than the baseline is replaced.
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web.UseTestServer().Configure(app =>
            {
                app.UseMiddleware<SecurityHeadersMiddleware>();
                app.Run(context =>
                {
                    context.Response.Headers.ContentSecurityPolicy = policy;
                    return context.Response.WriteAsync("image", context.RequestAborted);
                });
            }))
            .StartAsync(TestContext.Current.CancellationToken);
        using var client = host.GetTestClient();

        using var response = await client.GetAsync("/image", TestContext.Current.CancellationToken);

        response.Header("Content-Security-Policy").Should().Be(kept ? policy : "default-src 'none'; frame-ancestors 'none'");
        response.Header("X-Content-Type-Options").Should().Be("nosniff");
    }

    #endregion

    #region Helpers

    private static void ShouldCarrySecurityHeaders(HttpResponseMessage response)
    {
        foreach (var (name, value) in ExpectedHeaders)
        {
            response.Header(name).Should().Be(value, $"{name} is part of the API security baseline");
        }

        response.Header("Server").Should().BeNull("the Server header reveals the web server and its version");
    }

    #endregion
}
