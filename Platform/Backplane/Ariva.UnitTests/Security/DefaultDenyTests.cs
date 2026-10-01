using System.Net;
using System.Net.Http.Headers;
using Ariva.Core.Domain.ViewModels;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;

namespace Ariva.UnitTests.Security;

/// <summary>
/// CWE-862 and CWE-306: default deny. Without a valid access token (ARV-010a) everything except the allowlisted
/// anonymous endpoints answers 401 with a ProblemDetails body; valid tokens are covered in SignInHostTests.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class DefaultDenyTests
{
    #region Fields

    private const string SystemInfo = "/api/v1/system/info";

    private static readonly string[] Probes = ["/health/startup", "/health/readiness", "/health/liveness"];

    #endregion

    #region Theory data

    public static TheoryData<string, string> HostProbes
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var host in ArivaHosts.All)
            {
                foreach (var probe in Probes)
                {
                    data.Add(host, probe);
                }
            }

            return data;
        }
    }

    #endregion

    #region Tests

    [Fact]
    public async Task GetSystemInfo_Should_Return401_When_NoTokenIsPresented()
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Main);
        using var client = app.CreateClient();

        using var response = await client.GetAsync(SystemInfo, TestContext.Current.CancellationToken);

        await ShouldBeUnauthorizedProblemAsync(response);
        response.Header("WWW-Authenticate").Should().Be("Bearer");
    }

    [Fact]
    public async Task GetSystemInfo_Should_Return401_When_BearerTokenIsMalformed()
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Main);
        using var client = app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, SystemInfo);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-jwt");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        await ShouldBeUnauthorizedProblemAsync(response);
    }

    [Fact]
    public async Task GetSystemInfo_Should_Return401_When_TokenIsInQueryString()
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Main);
        using var client = app.CreateClient();

        using var response = await client.GetAsync($"{SystemInfo}?access_token=eyJhbGciOiJIUzI1NiJ9.e30.c2ln", TestContext.Current.CancellationToken);

        await ShouldBeUnauthorizedProblemAsync(response);
    }

    [Fact]
    public async Task GetSystemInfo_Should_ReturnProductAndVersion_When_CallerIsAuthenticated()
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Main, configure: builder => builder.ConfigureTestServices(TestAuthenticationHandler.Register));
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.UserHeader, "administrator");
        // System info needs SystemInfo.View, which only system administrators hold (ARV-009).
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.RolesHeader, Ariva.Core.RoleCodes.SystemAdministrator);

        using var response = await client.GetAsync(SystemInfo, TestContext.Current.CancellationToken);
        var body = await response.BodyAsync();
        var info = System.Text.Json.JsonSerializer.Deserialize<SystemInfoViewModel>(body, System.Text.Json.JsonSerializerOptions.Web);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the 401 answers come from default deny, not from a broken endpoint");
        info.Product.Should().Be("Ariva");
        info.Version.Should().NotBeNullOrWhiteSpace();
        info.Version.Should().NotContain("+", "build metadata such as the commit id is not shown to clients");
    }

    [Theory]
    [MemberData(nameof(HostProbes))]
    public async Task GetProbe_Should_Return200_When_CallerIsAnonymous(string host, string probe)
    {
        await using var app = ArivaHosts.Create(host);
        using var client = app.CreateClient();

        using var response = await client.GetAsync(probe, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.BodyAsync()).Should().Contain("Healthy");
    }

    [Theory]
    [MemberData(nameof(ArivaHosts.AllHosts), MemberType = typeof(ArivaHosts))]
    public async Task GetUnknownRoute_Should_Return401_When_CallerIsAnonymous(string host)
    {
        await using var app = ArivaHosts.Create(host);
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/api/v1/zones/42", TestContext.Current.CancellationToken);

        // The fallback policy also covers requests that match no endpoint, so callers cannot enumerate routes.
        await ShouldBeUnauthorizedProblemAsync(response);
    }

    #endregion

    #region Helpers

    private static async Task ShouldBeUnauthorizedProblemAsync(HttpResponseMessage response)
    {
        var body = await response.BodyAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Header("Content-Type").Should().StartWith(HttpResponses.ProblemJson);
        HttpResponses.ParseProblem(body).GetProperty("status").GetInt32().Should().Be(401);
        foreach (var marker in HttpResponses.LeakMarkers)
        {
            body.Should().NotContain(marker);
        }
    }

    #endregion
}
