using System.Net;
using System.Text.Json;
using Ariva.Api.Common.Extensions;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-063: the OpenAPI document the dynamic scan reads. Off by default on every host; with OpenApi:Enabled it is
/// served to system administrators only (SystemInfo.View): anonymous callers get 401 and other roles 403; and a k8s-prd
/// host refuses to start with it (ASVS V13.4.5).
/// </summary>
[Collection(HostCollection.Name)]
public sealed class OpenApiTests
{
    public static TheoryData<string> DocumentedHosts => [ArivaHosts.Main, ArivaHosts.Ingest, ArivaHosts.Integration];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IArivaHost Host(string name, bool enabled, string environment = Ariva.Api.Common.Hosting.ArivaEnvironment.VmLocal) =>
        ArivaHosts.Create(name, environment, builder =>
        {
            builder.UseSetting(OpenApiExtensions.SectionKey, enabled ? "true" : "false");
            builder.ConfigureTestServices(TestAuthenticationHandler.Register);
        });

    private static HttpRequestMessage Request(string role)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, OpenApiExtensions.DocumentPath);
        if (role is not null)
        {
            request.Headers.Add(TestAuthenticationHandler.UserHeader, "openapi-" + role);
            request.Headers.Add(TestAuthenticationHandler.RolesHeader, role);
        }

        return request;
    }

    [Theory]
    [MemberData(nameof(ArivaHosts.BackplaneHosts), MemberType = typeof(ArivaHosts))]
    public async Task Document_Should_NotExist_When_OpenApiIsNotEnabled(string host)
    {
        await using var app = Host(host, enabled: false);
        using var client = app.CreateClient();

        using var response = await client.SendAsync(Request(Ariva.Core.RoleCodes.SystemAdministrator), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "the document is off unless a deployment turns it on");
    }

    [Theory]
    [MemberData(nameof(DocumentedHosts))]
    public async Task Document_Should_BeServedToSystemAdministratorsOnly_When_Enabled(string host)
    {
        await using var app = Host(host, enabled: true);
        using var client = app.CreateClient();

        using var anonymous = await client.SendAsync(Request(null), Ct);
        using var supervisor = await client.SendAsync(Request(Ariva.Core.RoleCodes.BorderShiftSupervisor), Ct);
        using var administrator = await client.SendAsync(Request(Ariva.Core.RoleCodes.SystemAdministrator), Ct);

        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        supervisor.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        administrator.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await administrator.Content.ReadAsStringAsync(Ct));
        document.RootElement.GetProperty("openapi").GetString().Should().StartWith("3.");
        document.RootElement.GetProperty("paths").EnumerateObject().Should().NotBeEmpty("the scan needs the host's endpoints");
    }

    [Fact]
    public async Task Host_Should_RefuseToStart_When_TheApplicationSettingSaysProduction()
    {
        // ARV-098: Application:Environment must equal the host's environment, so production is set on both.
        await using var app = ArivaHosts.Create(ArivaHosts.Main, Ariva.Api.Common.Hosting.ArivaEnvironment.K8sPrd, builder =>
            builder.UseSetting(OpenApiExtensions.SectionKey, "true"));

        var start = () => app.CreateClient();

        start.Should().Throw<InvalidOperationException>().WithMessage("*not allowed in k8s-prd*");
    }

    [Fact]
    public async Task Host_Should_RefuseToStart_When_OpenApiIsEnabledInProduction()
    {
        await using var app = Host(ArivaHosts.Main, enabled: true, Ariva.Api.Common.Hosting.ArivaEnvironment.K8sPrd);

        var start = () => app.CreateClient();

        start.Should().Throw<InvalidOperationException>().WithMessage("*not allowed in k8s-prd*");
    }
}
