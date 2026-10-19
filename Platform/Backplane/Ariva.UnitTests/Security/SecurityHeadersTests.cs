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
/// Server header; responses to requests that carry credentials are never cached. ARV-092 (ASVS 5.0 V4.1.1): a text answer
/// that names no charset gets charset=utf-8.
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

    [Theory]
    [MemberData(nameof(ArivaHosts.BackplaneHosts), MemberType = typeof(ArivaHosts))]
    public async Task Get_Should_NameUtf8_When_TheStatusCodePageAnswersInPlainText(string host)
    {
        // A client that accepts no JSON gets the framework's plain-text status page instead of a ProblemDetails; the
        // authenticated test user passes default deny, so the unknown route answers the genuine 404.
        await using var app = ArivaHosts.Create(host, configure: builder => builder.ConfigureTestServices(TestAuthenticationHandler.Register));
        using var client = app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/does-not-exist");
        request.Headers.Add(TestAuthenticationHandler.UserHeader, "plain-text-reader");
        request.Headers.Accept.ParseAdd("text/plain");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        ((int)response.StatusCode).Should().Be(404);
        response.Header("Content-Type").Should().Be("text/plain; charset=utf-8");
        ShouldCarrySecurityHeaders(response);
    }

    [Theory]
    [InlineData("text/plain", "text/plain; charset=utf-8")]
    [InlineData("text/html", "text/html; charset=utf-8")]
    [InlineData("text/css", "text/css; charset=utf-8")]
    [InlineData("text/javascript", "text/javascript; charset=utf-8")]
    [InlineData("text/csv", "text/csv; charset=utf-8")]
    [InlineData("text/xml", "text/xml; charset=utf-8")]
    [InlineData("text/event-stream", "text/event-stream; charset=utf-8")]
    [InlineData("TEXT/PLAIN", "TEXT/PLAIN; charset=utf-8")]
    [InlineData("application/javascript", "application/javascript; charset=utf-8")]
    [InlineData("application/xml", "application/xml; charset=utf-8")]
    [InlineData("image/svg+xml", "image/svg+xml; charset=utf-8")]
    [InlineData("application/problem+xml", "application/problem+xml; charset=utf-8")]
    [InlineData("text/plain; version=0.0.4", "text/plain; version=0.0.4; charset=utf-8")]
    public void WithUtf8Charset_Should_NameUtf8_When_ATextTypeNamesNoCharset(string contentType, string expected)
    {
        SecurityHeadersMiddleware.WithUtf8Charset(contentType).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("text/csv; charset=utf-8")]
    [InlineData("application/xml; charset=utf-8")]
    [InlineData("text/plain; charset=us-ascii")]
    [InlineData("application/json")]
    [InlineData("application/problem+json")]
    [InlineData("image/png")]
    [InlineData("video/webm")]
    [InlineData("application/octet-stream")]
    [InlineData("not a media type")]
    public void WithUtf8Charset_Should_LeaveTheType_When_ItNamesACharsetOrIsNotText(string contentType)
    {
        SecurityHeadersMiddleware.WithUtf8Charset(contentType).Should().BeNull("only a text type without a charset is changed");
    }

    [Theory]
    [InlineData("text/javascript", "text/javascript; charset=utf-8")]
    [InlineData("image/svg+xml", "image/svg+xml; charset=utf-8")]
    [InlineData("application/json; charset=utf-8", "application/json; charset=utf-8")]
    [InlineData("image/png", "image/png")]
    public async Task Get_Should_NameUtf8OnTheWire_When_AnEndpointWritesTextWithoutACharset(string written, string sent)
    {
        // ARV-092: what a component Ariva does not write itself sends (the TickerQ dashboard's files, for example).
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web.UseTestServer().Configure(app =>
            {
                app.UseMiddleware<SecurityHeadersMiddleware>();
                app.Run(context =>
                {
                    context.Response.ContentType = written;
                    return context.Response.WriteAsync("x", context.RequestAborted);
                });
            }))
            .StartAsync(TestContext.Current.CancellationToken);
        using var client = host.GetTestClient();

        using var response = await client.GetAsync("/file", TestContext.Current.CancellationToken);

        response.Header("Content-Type").Should().Be(sent);
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
