using System.Net;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;

namespace Ariva.UnitTests.Security;

/// <summary>
/// Rate limiting (CWE-307, CWE-770) and the CORS allow-list from <c>Security:RateLimiting</c> and
/// <c>Security:Cors</c>.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class RateLimitingAndCorsTests
{
    #region Fields

    private const string Liveness = "/health/liveness";

    private const string WebOrigin = "http://localhost:51011";

    #endregion

    #region Rate limiting

    [Fact]
    public async Task Get_Should_Return429ProblemDetails_When_GlobalLimitIsExceeded()
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Main, configure: builder => builder
            .UseSetting("Security:RateLimiting:Global:PermitLimit", "3")
            .UseSetting("Security:RateLimiting:Global:WindowSeconds", "60"));
        using var client = app.CreateClient();

        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage last = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            last?.Dispose();
            last = await client.GetAsync(Liveness, TestContext.Current.CancellationToken);
            statuses.Add(last.StatusCode);
        }

        using var rejected = last;
        statuses.Should().Equal(HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests);
        rejected.Header("Retry-After").Should().NotBeNullOrWhiteSpace();
        rejected.Header("Content-Type").Should().StartWith(HttpResponses.ProblemJson);
        rejected.Header("X-Content-Type-Options").Should().Be("nosniff");
    }

    [Fact]
    public async Task CreateClient_Should_FailAtStartup_When_RateLimitIsNotPositive()
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Main, configure: builder => builder.UseSetting("Security:RateLimiting:Auth:PermitLimit", "0"));

        Action start = () => app.CreateClient();

        start.Should().Throw<Exception>().WithMessage("*Security:RateLimiting*");
    }

    #endregion

    #region CORS

    [Theory]
    [MemberData(nameof(ArivaHosts.BackplaneHosts), MemberType = typeof(ArivaHosts))]
    public async Task Get_Should_OmitAllowOrigin_When_OriginIsNotAllowlisted(string host)
    {
        await using var app = ArivaHosts.Create(host);
        using var client = app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, Liveness);
        request.Headers.Add("Origin", "https://attacker.example");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Header("Access-Control-Allow-Origin").Should().BeNull();
        response.Header("Access-Control-Allow-Credentials").Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(ArivaHosts.BackplaneHosts), MemberType = typeof(ArivaHosts))]
    public async Task Get_Should_EchoExactOrigin_When_OriginIsAllowlisted(string host)
    {
        await using var app = ArivaHosts.Create(host);
        using var client = app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, Liveness);
        request.Headers.Add("Origin", WebOrigin);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.Header("Access-Control-Allow-Origin").Should().Be(WebOrigin);
        response.Header("Access-Control-Allow-Credentials").Should().Be("true");
    }

    [Theory]
    [InlineData("*")]
    [InlineData("https://*.dalilhub.tech")]
    [InlineData("https://ariva.dalilhub.tech/")]
    [InlineData("ariva.dalilhub.tech")]
    public async Task CreateClient_Should_FailAtStartup_When_OriginIsNotExact(string origin)
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Main, configure: builder => builder.UseSetting("Security:Cors:AllowedOrigins:0", origin));

        Action start = () => app.CreateClient();

        start.Should().Throw<Exception>().WithMessage("*Security:Cors*");
    }

    #endregion
}
