using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Ariva.Api.Common.Middlewares;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ariva.UnitTests.Security;

/// <summary>
/// CWE-79 and CWE-200: every API response, including challenges and errors, carries the security headers and no
/// Server header; responses to requests that carry credentials are never cached. ARV-092 (ASVS 5.0 V4.1.1): a text answer
/// that names no charset gets charset=utf-8. ARV-233: an answer to a request whose declared body is larger than its endpoint
/// takes closes the connection, so a client never reuses one Kestrel closes after the answer.
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
        ["Content-Security-Policy"] = "default-src 'none'; base-uri 'none'; object-src 'none'; frame-ancestors 'none'"
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
    [InlineData("default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; sandbox; frame-ancestors 'none'", true)]
    [InlineData("default-src 'none'; base-uri 'none'; sandbox; frame-ancestors 'none'", true)]
    [InlineData("default-src 'none'; sandbox", false)]
    [InlineData("default-src 'none'; sandbox; frame-ancestors 'none'", false)]
    [InlineData("default-src 'none'; base-uri 'self'; frame-ancestors 'none'", false)]
    [InlineData("default-src 'none'; base-uri 'none' https://cdn.example; frame-ancestors 'none'", false)]
    [InlineData("default-src 'none' https://cdn.example; base-uri 'none'; frame-ancestors 'none'", false)]
    [InlineData("default-src 'none';base-uri 'none';frame-ancestors 'none'", true)]
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

        response.Header("Content-Security-Policy").Should().Be(kept ? policy : SecurityHeadersMiddleware.ContentSecurityPolicy);
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

    [Theory]
    [InlineData("HTTP/1.1", 300 * 1024L, 256 * 1024L, true)]
    [InlineData("HTTP/1.0", 300 * 1024L, 256 * 1024L, true)]
    [InlineData("HTTP/1.1", 256 * 1024L, 256 * 1024L, false)]
    [InlineData("HTTP/1.1", 1L, 256 * 1024L, false)]
    [InlineData("HTTP/1.1", null, 256 * 1024L, false)]
    [InlineData("HTTP/1.1", 300 * 1024L, null, false)]
    [InlineData("HTTP/2", 300 * 1024L, 256 * 1024L, false)]
    [InlineData("HTTP/3", 300 * 1024L, 256 * 1024L, false)]
    public void CannotBeDrained_Should_HoldOnlyForADeclaredBodyPastTheLimitOverHttp1_When_Answered(string protocol, long? declared, long? most, bool close)
    {
        var context = new DefaultHttpContext();
        context.Request.Protocol = protocol;
        context.Request.ContentLength = declared;
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(new BodyLimit(most));

        SecurityHeadersMiddleware.CannotBeDrained(context).Should().Be(close);
        SecurityHeadersMiddleware.CannotBeDrained(new DefaultHttpContext { Request = { Protocol = "HTTP/1.1", ContentLength = declared } })
            .Should().BeFalse("without a body size limit every body can be read");
    }

    [Fact]
    public async Task Post_Should_AnswerEveryPushOnTheClientsConnections_When_OversizedPushesAreRefusedUnread()
    {
        // ARV-233, as Ingest answers an oversized push (413 before reading, the action's limit 256 KB) on Kestrel, to a client
        // whose pushes share one connection pool as the pilot run's did. Before the fix about one push in ten after a 413 ended in
        // a connection reset (ResponseEnded, or an IOException Shutdown) on this machine, idle as under load.
        const int Limit = 256 * 1024;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.Run(async context =>
        {
            context.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = Limit;
            if (context.Request.ContentLength > Limit)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }

            await context.Request.Body.CopyToAsync(Stream.Null, context.RequestAborted);
            context.Response.StatusCode = StatusCodes.Status202Accepted;
        });
        await app.StartAsync(TestContext.Current.CancellationToken);
        // One client and its connection pool for every push, through IHttpClientFactory.
        await using var clients = new ServiceCollection().AddHttpClient(nameof(SecurityHeadersTests)).Services.BuildServiceProvider();
        using var client = clients.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(SecurityHeadersTests));
        client.BaseAddress = new Uri(app.Urls.First());
        var outcomes = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);

        async Task Push(int bytes)
        {
            string outcome;
            try
            {
                using var content = new ByteArrayContent(new byte[bytes]);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                using var response = await client.PostAsync("/push", content, TestContext.Current.CancellationToken);
                outcome = $"{(int)response.StatusCode}{(response.Headers.ConnectionClose == true ? " close" : "")}";
            }
            catch (HttpRequestException e)
            {
                outcome = $"reset {e.HttpRequestError}";
            }

            outcomes.AddOrUpdate(outcome, 1, (_, n) => n + 1);
        }

        await Parallel.ForAsync(0, 8, TestContext.Current.CancellationToken, async (_, _) =>
        {
            for (var round = 0; round < 50; round++)
            {
                await Push(Limit + (10 * 1024));
                await Push(15 * 1024);
                await Push(15 * 1024);
            }
        });

        outcomes.Should().BeEquivalentTo(new Dictionary<string, int> { ["413 close"] = 400, ["202"] = 800 },
            "every oversized push is refused with Connection: close, and no push on a reused connection is reset");
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

    /// <summary>A request's maximum body size, as Kestrel's feature gives it.</summary>
    private sealed class BodyLimit(long? most) : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;

        public long? MaxRequestBodySize { get; set; } = most;
    }

    #endregion
}
