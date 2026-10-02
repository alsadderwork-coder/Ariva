using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Ariva.Api.Common.Security;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Sensing;
using Ariva.Infra.Security;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-022 on the Ingest host in process: a device authenticates with its credential (Bearer, header or Basic with its
/// code), only on device endpoints; a missing or wrong key is 401, a user token on a device endpoint is 401, another
/// device's zone is 403; the allowed networks and the pinned client certificate are enforced; requests are limited per
/// device.
/// </summary>
public sealed class DeviceAuthenticationTests
{
    private const string TestRemoteHeader = "X-Test-Remote";
    private const string TestCertificateHeader = "X-Test-Certificate";

    private static readonly DeviceCredentials.Issued S17 = DeviceCredentials.New();
    private static readonly DeviceCredentials.Issued S18 = DeviceCredentials.New();
    private static readonly X509Certificate2 Pinned = Certificate("CN=S-18");
    private static readonly X509Certificate2 Other = Certificate("CN=intruder");

    private static X509Certificate2 Certificate(string subject)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    /// <summary>S-17 owns "Snake A" and may push from anywhere; S-18 owns "Snake B", only from 10.20.0.0/24, with a pinned certificate.</summary>
    private sealed class FakeGateway : ISvcDeviceGateway
    {
        public int Lookups { get; private set; }

        public Task<DeviceCredentialRecord> FindByPrefixAsync(string prefix, CancellationToken ct = default)
        {
            Lookups++;
            DeviceCredentialRecord record = prefix == S17.Prefix
                ? new(Guid.NewGuid(), "S-17", "DMO", "Snake A", "Online", S17.Hash, [], null)
                : prefix == S18.Prefix
                    ? new(Guid.NewGuid(), "S-18", "DMO", "Snake B", "Commissioning", S18.Hash, ["10.20.0.0/24"], Convert.ToHexStringLower(SHA256.HashData(Pinned.RawData)))
                    : null;
            return Task.FromResult(record);
        }

        public Task<Fluentx.Result<DeviceZoneViewModel>> PublishedZoneAsync(string siteCode, string queueZoneName, CancellationToken ct = default) =>
            Task.FromResult(new Fluentx.Result<DeviceZoneViewModel>(new DeviceZoneViewModel(siteCode, queueZoneName, 12, new string('a', 64), [], [])));
    }

    /// <summary>Lets a test set the client address and certificate that TestServer cannot.</summary>
    private sealed class ConnectionStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue(TestRemoteHeader, out var remote))
                    context.Connection.RemoteIpAddress = IPAddress.Parse(remote.ToString());
                if (context.Request.Headers.TryGetValue(TestCertificateHeader, out var certificate))
                    context.Connection.ClientCertificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(certificate.ToString()));
                return nextMiddleware();
            });
            next(app);
        };
    }

    private static IArivaHost Ingest(int devicePermitLimit = 1000, bool forwardedCertificates = false) => ArivaHosts.Create(ArivaHosts.Ingest, configure: builder => builder
        .UseSetting("Security:RateLimiting:Device:PermitLimit", devicePermitLimit.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .UseSetting(Ariva.Api.Common.Extensions.ClientCertificateExtensions.ForwardedHeaderSetting, forwardedCertificates ? "ssl-client-cert" : "")
        .UseSetting("Security:ForwardedHeaders:KnownProxies:0", "10.9.9.9")
        .ConfigureTestServices(services =>
        {
            TestAuthenticationHandler.Register(services);
            services.Replace(ServiceDescriptor.Scoped<ISvcDeviceGateway, FakeGateway>());
            services.AddSingleton<IStartupFilter, ConnectionStartupFilter>();
        }));

    private static HttpRequestMessage Get(string path, string bearer = null, Action<HttpRequestMessage> more = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
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
    public async Task Device_Should_Authenticate_Only_With_ItsOwnCredential()
    {
        await using var host = Ingest();
        using var client = host.CreateClient();

        (await SendAsync(client, Get("/api/v1/ingest/device"))).Should().Be(HttpStatusCode.Unauthorized, "no key");
        (await SendAsync(client, Get("/api/v1/ingest/device", S17.Credential[..47] + (S17.Credential[47] == 'A' ? 'B' : 'A')))).Should().Be(HttpStatusCode.Unauthorized, "wrong key");
        (await SendAsync(client, Get("/api/v1/ingest/device", "ardk_" + new string('A', 43)))).Should().Be(HttpStatusCode.Unauthorized, "unknown device");
        (await SendAsync(client, Get("/api/v1/ingest/device", "ardk_short"))).Should().Be(HttpStatusCode.Unauthorized, "malformed key");
        (await SendAsync(client, Get("/api/v1/ingest/device", more: r => r.Headers.Add(TestAuthenticationHandler.UserHeader, "admin")))).Should()
            .Be(HttpStatusCode.Unauthorized, "a user is not a device");
        (await SendAsync(client, Get("/api/v1/ingest/device", "eyJhbGciOiJFUzI1NiJ9.e30.sig"))).Should().Be(HttpStatusCode.Unauthorized, "a user token is not a device credential");

        (await SendAsync(client, Get("/api/v1/ingest/device", S17.Credential))).Should().Be(HttpStatusCode.OK, "Bearer");
        (await SendAsync(client, Get("/api/v1/ingest/device", more: r => r.Headers.Add(DeviceAuthentication.KeyHeader, S17.Credential)))).Should().Be(HttpStatusCode.OK, "header");
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes("S-17:" + S17.Credential));
        (await SendAsync(client, Get("/api/v1/ingest/device", more: r => r.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic)))).Should().Be(HttpStatusCode.OK, "Basic with the code");
        var wrongUser = Convert.ToBase64String(Encoding.UTF8.GetBytes("S-99:" + S17.Credential));
        (await SendAsync(client, Get("/api/v1/ingest/device", more: r => r.Headers.Authorization = new AuthenticationHeaderValue("Basic", wrongUser)))).Should().Be(HttpStatusCode.Unauthorized, "Basic with another code");
    }

    [Fact]
    public async Task Self_Should_NameTheDeviceAndTheServerClock()
    {
        await using var host = Ingest();
        using var client = host.CreateClient();
        using var request = Get("/api/v1/ingest/device", S17.Credential);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        body.Should().Contain("\"code\":\"S-17\"").And.Contain("\"queueZoneName\":\"Snake A\"").And.Contain("serverTimeUtc").And.NotContain(S17.Hash);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task Zone_Should_BeForbidden_When_ItIsAnotherDevicesZone()
    {
        await using var host = Ingest();
        using var client = host.CreateClient();

        (await SendAsync(client, Get("/api/v1/ingest/zones/Snake%20A", S17.Credential))).Should().Be(HttpStatusCode.OK);
        (await SendAsync(client, Get("/api/v1/ingest/zones/Snake%20B", S17.Credential))).Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(client, Get("/api/v1/ingest/zones/snake%20a", S17.Credential))).Should().Be(HttpStatusCode.Forbidden, "zone names are exact");
        (await SendAsync(client, Get("/api/v1/ingest/zones/Snake%20A"))).Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Device_Should_BeRefused_When_OutsideItsNetworksOrWithoutItsCertificate()
    {
        await using var host = Ingest();
        using var client = host.CreateClient();
        void From(HttpRequestMessage r, string address, X509Certificate2 certificate)
        {
            r.Headers.Add(TestRemoteHeader, address);
            if (certificate is not null)
                r.Headers.Add(TestCertificateHeader, Convert.ToBase64String(certificate.RawData));
        }

        (await SendAsync(client, Get("/api/v1/ingest/device", S18.Credential, r => From(r, "10.20.0.17", Pinned)))).Should().Be(HttpStatusCode.OK);
        (await SendAsync(client, Get("/api/v1/ingest/device", S18.Credential, r => From(r, "::ffff:10.20.0.17", Pinned)))).Should().Be(HttpStatusCode.OK, "an IPv4-mapped address");
        (await SendAsync(client, Get("/api/v1/ingest/device", S18.Credential, r => From(r, "10.20.1.17", Pinned)))).Should().Be(HttpStatusCode.Unauthorized, "outside 10.20.0.0/24");
        (await SendAsync(client, Get("/api/v1/ingest/device", S18.Credential, r => From(r, "10.20.0.17", null)))).Should().Be(HttpStatusCode.Unauthorized, "no certificate");
        (await SendAsync(client, Get("/api/v1/ingest/device", S18.Credential, r => From(r, "10.20.0.17", Other)))).Should().Be(HttpStatusCode.Unauthorized, "another certificate");
        (await SendAsync(client, Get("/api/v1/ingest/device", S18.Credential))).Should().Be(HttpStatusCode.Unauthorized, "no address");
    }

    [Fact]
    public async Task Requests_Should_BeLimitedPerDevice()
    {
        await using var host = Ingest(devicePermitLimit: 3);
        using var client = host.CreateClient();

        // Someone who knows the prefix (it is shown to administrators) cannot use up the real device's window.
        for (var i = 0; i < 5; i++)
            await SendAsync(client, Get("/api/v1/ingest/device", S17.Prefix + new string((char)('a' + i), 35)));

        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
            codes.Add(await SendAsync(client, Get("/api/v1/ingest/device", S17.Credential)));

        codes.Should().Equal(HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests);
        (await SendAsync(client, Get("/api/v1/ingest/device", S18.Credential, r => r.Headers.Add(TestRemoteHeader, "10.20.0.17")))).Should()
            .Be(HttpStatusCode.Unauthorized, "another device has its own window (and fails here only for its missing certificate)");
    }

    [Fact]
    public async Task ForwardedCertificate_Should_CountOnlyFromATrustedProxy()
    {
        await using var host = Ingest(forwardedCertificates: true);
        using var client = host.CreateClient();
        var pem = Uri.EscapeDataString(Pinned.ExportCertificatePem());
        void Via(HttpRequestMessage r, string connection, string client)
        {
            r.Headers.Add(TestRemoteHeader, connection);
            if (client is not null)
                r.Headers.Add("X-Forwarded-For", client);
            r.Headers.Add("ssl-client-cert", pem);
        }

        (await SendAsync(client, Get("/api/v1/ingest/device", S18.Credential, r => Via(r, "10.9.9.9", "10.20.0.17")))).Should()
            .Be(HttpStatusCode.OK, "the ingress verified the certificate and forwarded it with the client's address");
        (await SendAsync(client, Get("/api/v1/ingest/device", S18.Credential, r => Via(r, "10.20.0.17", null)))).Should()
            .Be(HttpStatusCode.Unauthorized, "a caller that reaches the host directly cannot name a certificate");
        (await SendAsync(client, Get("/api/v1/ingest/device", S18.Credential, r => Via(r, "10.20.0.99", "10.20.0.17")))).Should()
            .Be(HttpStatusCode.Unauthorized, "nor can it pretend to be the proxy");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a certificate")]
    [InlineData("-----BEGIN%20CERTIFICATE-----%0AAAAA%0A-----END%20CERTIFICATE-----")]
    public void ForwardedCertificate_Should_BeIgnored_When_Unreadable(string header) =>
        Ariva.Api.Common.Extensions.ClientCertificateExtensions.Parse(header).Should().BeNull();

    [Fact]
    public async Task DeviceCredential_Should_NotOpenUserEndpoints()
    {
        await using var host = ArivaHosts.Create(ArivaHosts.Main, configure: builder => builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Scoped<ISvcDeviceGateway, FakeGateway>())));
        using var client = host.CreateClient();

        (await SendAsync(client, Get("/api/v1/admin/devices", S17.Credential))).Should().Be(HttpStatusCode.Unauthorized);
        (await SendAsync(client, Get("/api/v1/admin/devices", more: r => r.Headers.Add(DeviceAuthentication.KeyHeader, S17.Credential)))).Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("Bearer ardk_x", "ardk_x", null)]
    [InlineData("Bearer eyJ.a.b", null, null)]
    [InlineData("Basic !!!", null, null)]
    [InlineData("Basic UzE3", null, null)]
    [InlineData("Digest abc", null, null)]
    public void Presented_Should_ReadTheCredential(string authorization, string key, string user)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = authorization;

        DeviceAuthentication.Presented(context.Request).Should().Be((key, user));
    }

    [Fact]
    public void RateLimitPartition_Should_UseTheWholeCredentialOrTheAddress()
    {
        var device = new DefaultHttpContext();
        device.Request.Headers[DeviceAuthentication.KeyHeader] = S17.Credential;
        var anonymous = new DefaultHttpContext { Connection = { RemoteIpAddress = IPAddress.Parse("::ffff:10.0.0.9") } };

        DeviceAuthentication.RateLimitPartition(device).Should().Be("device:" + S17.Hash[..32], "a hash of the whole credential, not its public prefix");
        DeviceAuthentication.RateLimitPartition(anonymous).Should().Be("address:10.0.0.9");
    }
}
