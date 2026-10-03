using System.Net;
using System.Net.Http.Headers;
using Ariva.Api.Common.Security;
using Ariva.Core.Services.Displays;
using Ariva.Core.Services.Sensing;
using Ariva.Infra.Security;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-058 on the Main host in process: a display player authenticates with its display's code and credential, in the
/// X-Ariva-Display-Key header only, on the board only; a missing, wrong, duplicated or device credential is 401, a user
/// is 401 there, and a display credential opens no user or device endpoint; requests are limited per credential.
/// </summary>
public sealed class DisplayAuthenticationTests
{
    private static readonly DeviceCredentials.Issued Gate4 = DisplayCredentials.New();
    private static readonly DeviceCredentials.Issued Hall2 = DisplayCredentials.New();
    private static readonly DeviceCredentials.Issued Device = DeviceCredentials.New();

    /// <summary>Two displays: GATE-4 and HALL-2, each with its own credential; the board of either is a fixed answer.</summary>
    private sealed class FakeBoard : ISvcDisplayBoard
    {
        private static readonly Dictionary<string, (Guid Id, DeviceCredentials.Issued Credential)> Displays = new(StringComparer.Ordinal)
        {
            ["GATE-4"] = (Guid.NewGuid(), Gate4),
            ["HALL-2"] = (Guid.NewGuid(), Hall2)
        };

        public Task<DisplayPlayer> FindAsync(string code, string credential, CancellationToken ct = default) =>
            Task.FromResult(code is not null && Displays.TryGetValue(code, out var display) && DisplayCredentials.Matches(credential, display.Credential.Hash)
                ? new DisplayPlayer(display.Id, code, "DMO", display.Credential.Prefix)
                : null);

        public Task<Fluentx.Result<DisplayBoardViewModel>> GetAsync(Guid displayId, string credentialPrefix, CancellationToken ct = default)
        {
            var code = Displays.Single(d => d.Value.Id == displayId).Key;
            return Task.FromResult(new Fluentx.Result<DisplayBoardViewModel>(new DisplayBoardViewModel(code, "Board", "Landscape", ["en"], 5, 1, 150,
                new Dictionary<string, string> { ["en"] = "Please follow the signs" }, [], DateTime.UtcNow)));
        }
    }

    private static IArivaHost Main(int permitLimit = 1000) => ArivaHosts.Create(ArivaHosts.Main, configure: builder => builder
        .UseSetting("Security:RateLimiting:Device:PermitLimit", permitLimit.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .ConfigureTestServices(services =>
        {
            TestAuthenticationHandler.Register(services);
            services.Replace(ServiceDescriptor.Scoped<ISvcDisplayBoard, FakeBoard>());
            FakeDisplays.RegisterAdmin(services);
            FakeDevices.Register(services);
        }));

    private static HttpRequestMessage Board(string code, string key = null, Action<HttpRequestMessage> more = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, code is null ? "/api/v1/display/board" : $"/api/v1/display/board?code={Uri.EscapeDataString(code)}");
        if (key is not null)
            request.Headers.Add(DisplayAuthentication.KeyHeader, key);
        more?.Invoke(request);
        return request;
    }

    private static async Task<HttpStatusCode> SendAsync(HttpClient client, HttpRequestMessage request)
    {
        using (request)
        using (var response = await client.SendAsync(request, TestContext.Current.CancellationToken))
            return response.StatusCode;
    }

    [Fact]
    public async Task Player_Should_Authenticate_Only_With_ItsOwnCodeAndCredential()
    {
        await using var host = Main();
        using var client = host.CreateClient();

        (await SendAsync(client, Board("GATE-4", Gate4.Credential))).Should().Be(HttpStatusCode.OK);
        (await SendAsync(client, Board("GATE-4"))).Should().Be(HttpStatusCode.Unauthorized, "no key");
        (await SendAsync(client, Board(null, Gate4.Credential))).Should().Be(HttpStatusCode.Unauthorized, "no code");
        (await SendAsync(client, Board("HALL-2", Gate4.Credential))).Should().Be(HttpStatusCode.Unauthorized, "another display's code");
        (await SendAsync(client, Board("GATE-4", Gate4.Credential[..47] + (Gate4.Credential[47] == 'A' ? 'B' : 'A')))).Should().Be(HttpStatusCode.Unauthorized, "wrong key");
        (await SendAsync(client, Board("GATE-4", Device.Credential))).Should().Be(HttpStatusCode.Unauthorized, "a device credential is not a display credential");
        (await SendAsync(client, Board("GATE-4", more: r => r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Gate4.Credential)))).Should()
            .Be(HttpStatusCode.Unauthorized, "the header only, never the Authorization header");
        (await SendAsync(client, Board("GATE-4", more: r => r.Headers.Add(TestAuthenticationHandler.UserHeader, "admin")))).Should()
            .Be(HttpStatusCode.Unauthorized, "a user is not a display");
        (await SendAsync(client, Board("GATE-4", more: r => r.Headers.TryAddWithoutValidation(DisplayAuthentication.KeyHeader, [Gate4.Credential, Gate4.Credential])))).Should()
            .Be(HttpStatusCode.Unauthorized, "a key header given twice");
        using var twice = new HttpRequestMessage(HttpMethod.Get, "/api/v1/display/board?code=GATE-4&code=HALL-2");
        twice.Headers.Add(DisplayAuthentication.KeyHeader, Gate4.Credential);
        (await SendAsync(client, twice)).Should().Be(HttpStatusCode.Unauthorized, "a code given twice");
    }

    [Fact]
    public async Task DisplayCredential_Should_NotOpenUserOrDeviceEndpoints()
    {
        await using var host = Main();
        using var client = host.CreateClient();

        using var admin = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/displays?code=GATE-4");
        admin.Headers.Add(DisplayAuthentication.KeyHeader, Gate4.Credential);
        (await SendAsync(client, admin)).Should().Be(HttpStatusCode.Unauthorized, "a [Permission] endpoint");
        using var bearer = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/displays");
        bearer.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Gate4.Credential);
        (await SendAsync(client, bearer)).Should().Be(HttpStatusCode.Unauthorized);

        await using var ingest = ArivaHosts.Create(ArivaHosts.Ingest, configure: builder => builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Scoped<ISvcDeviceGateway, NoDevices>())));
        using var ingestClient = ingest.CreateClient();
        using var device = new HttpRequestMessage(HttpMethod.Get, "/api/v1/ingest/device");
        device.Headers.Add(DeviceAuthentication.KeyHeader, Gate4.Credential);
        (await SendAsync(ingestClient, device)).Should().Be(HttpStatusCode.Unauthorized, "a display is not a device");
    }

    [Fact]
    public async Task Requests_Should_BeLimitedPerCredential()
    {
        await using var host = Main(permitLimit: 3);
        using var client = host.CreateClient();

        // Someone who knows the prefix (administrators see it) cannot use up the real player's window.
        for (var i = 0; i < 5; i++)
            await SendAsync(client, Board("GATE-4", Gate4.Prefix + new string((char)('a' + i), 35)));

        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
            codes.Add(await SendAsync(client, Board("GATE-4", Gate4.Credential)));

        codes.Should().Equal(HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests);
        (await SendAsync(client, Board("HALL-2", Hall2.Credential))).Should().Be(HttpStatusCode.OK, "another display has its own window");
    }

    [Fact]
    public void RateLimitPartition_Should_UseTheWholeCredentialOrTheAddress()
    {
        var player = new DefaultHttpContext();
        player.Request.Headers[DisplayAuthentication.KeyHeader] = Gate4.Credential;
        var anonymous = new DefaultHttpContext { Connection = { RemoteIpAddress = IPAddress.Parse("::ffff:10.0.0.9") } };

        DisplayAuthentication.RateLimitPartition(player).Should().Be("display:" + Gate4.Hash[..32], "a hash of the whole credential, not its public prefix");
        DisplayAuthentication.RateLimitPartition(anonymous).Should().Be("address:10.0.0.9");
    }

    private sealed class NoDevices : ISvcDeviceGateway
    {
        public Task<Ariva.Core.Domain.ViewModels.DeviceCredentialRecord> FindByPrefixAsync(string prefix, CancellationToken ct = default) =>
            Task.FromResult<Ariva.Core.Domain.ViewModels.DeviceCredentialRecord>(null);

        public Task<Fluentx.Result<Ariva.Core.Domain.ViewModels.DeviceZoneViewModel>> PublishedZoneAsync(string siteCode, string queueZoneName, CancellationToken ct = default) =>
            Task.FromResult(Fluentx.Result.Error<Ariva.Core.Domain.ViewModels.DeviceZoneViewModel>("none"));
    }
}
