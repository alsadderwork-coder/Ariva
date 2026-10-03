using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Flights;
using Ariva.Core.Integration;
using Ariva.Infra.Flights.Acris;
using Ariva.Infra.Integration;
using Ariva.Infra.Integration.Outbound;
using Ariva.Infra.Security;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Ariva.Di.Extensions;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-045 (CWE-918, CWE-306): outbound endpoints are reached only at registered URLs, only at addresses inside their
/// networks and never at this host, link-local or cloud metadata, multicast or reserved ranges, checked for the address
/// actually dialled (DNS rebinding); redirects are refused; retries, timeouts and the circuit breaker behave; each
/// authentication handler sends what it should; ACRIS flights are read strictly and mapped to the site's side.
/// </summary>
public sealed class OutboundTests
{
    private static readonly IReadOnlyList<string> Ten = ["10.0.0.0/8"];
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    #region Addresses

    [Theory]
    [InlineData("169.254.169.254", "cloud metadata (link-local)")]
    [InlineData("::ffff:169.254.169.254", "metadata as an IPv4-mapped IPv6 address")]
    [InlineData("64:ff9b::a9fe:a9fe", "metadata through NAT64")]
    [InlineData("2002:a9fe:a9fe::1", "metadata through 6to4")]
    [InlineData("fe80::1", "IPv6 link-local")]
    [InlineData("0.0.0.0", "this network")]
    [InlineData("255.255.255.255", "broadcast")]
    [InlineData("224.0.0.1", "multicast")]
    [InlineData("::", "unspecified")]
    public void AddressRefusal_Should_RefuseRangesNoEndpointMayReach_When_EvenTheNetworksListThem(string address, string why)
    {
        OutboundRules.AddressRefusal(IPAddress.Parse(address), ["169.254.0.0/16", "0.0.0.0/1", "128.0.0.0/1", "::/1", "8000::/1"], allowLoopback: true)
            .Should().NotBeNull(why);
    }

    [Fact]
    public void AddressRefusal_Should_AllowOnlyTheEndpointsNetworks_When_Checked()
    {
        OutboundRules.AddressRefusal(IPAddress.Parse("10.20.0.5"), Ten, false).Should().BeNull();
        OutboundRules.AddressRefusal(IPAddress.Parse("::ffff:10.20.0.5"), Ten, false).Should().BeNull("an IPv4-mapped address counts as IPv4");
        OutboundRules.AddressRefusal(IPAddress.Parse("192.168.1.5"), Ten, false).Should().Contain("outside");
        OutboundRules.AddressRefusal(IPAddress.Parse("127.0.0.1"), ["127.0.0.0/8"], false).Should().Contain("loopback");
        OutboundRules.AddressRefusal(IPAddress.Parse("::1"), ["::1/128"], false).Should().Contain("loopback");
        OutboundRules.AddressRefusal(IPAddress.Parse("127.0.0.1"), ["127.0.0.0/8"], true).Should().BeNull("a lab setting");
    }

    #endregion

    #region Connection rules

    private static OutboundConnectionRequest Connection(string url = "https://aodb.example.test/api", string kind = "ApiKeyHeader", string header = "X-Api-Key",
        string tokenPath = null, IReadOnlyList<string> networks = null, string pullPath = null) =>
        new(url, networks ?? Ten, kind, TokenPath: tokenPath, ClientId: tokenPath is null ? null : "ariva", HeaderName: header, PullPath: pullPath);

    private static IReadOnlyList<string> Errors(OutboundConnectionRequest request, OutboundEndpointPurpose purpose = OutboundEndpointPurpose.Generic, bool lab = false) =>
        OutboundRules.Check(request, purpose, lab ? new HashSet<string>(["127.0.0.1"]) : new HashSet<string>(), allowLoopback: lab).Errors;

    [Theory]
    [InlineData("http://aodb.example.test/", "plain HTTP")]
    [InlineData("https://user:pass@aodb.example.test/", "user information")]
    [InlineData("https://aodb.example.test/?next=http://169.254.169.254/", "a query")]
    [InlineData("https://aodb.example.test/#frag", "a fragment")]
    [InlineData("https://169.254.169.254/latest/meta-data/", "the metadata address as a literal")]
    [InlineData("https://10.0.0.1@169.254.169.254/", "metadata behind user information")]
    [InlineData("https://192.168.0.1/", "a literal outside the networks")]
    [InlineData("file:///etc/passwd", "another scheme")]
    [InlineData("gopher://aodb.example.test/", "another scheme")]
    [InlineData("/relative", "not absolute")]
    public void Check_Should_RefuseTheBaseUrl_When_ItIsNotARegistrableHttpsUrl(string url, string why)
    {
        Errors(Connection(url)).Should().NotBeEmpty(why);
    }

    [Theory]
    [InlineData("https://evil.example/token", "an absolute URL")]
    [InlineData("//evil.example/token", "a scheme-relative URL")]
    [InlineData("/api/../../token", "a path that climbs")]
    [InlineData("/api/./token", "a dot segment")]
    [InlineData("api/token", "not rooted")]
    [InlineData("/token?x=1", "a query on a token path")]
    public void Check_Should_KeepTheTokenRequestOnTheSameOrigin_When_ATokenPathIsGiven(string tokenPath, string why)
    {
        Errors(Connection(kind: "OAuth2ClientCredentials", header: null, tokenPath: tokenPath)).Should().NotBeEmpty(why);
    }

    [Fact]
    public void Check_Should_NormaliseAndKeepOnlyWhatTheKindUses_When_Valid()
    {
        var (connection, errors) = OutboundRules.Check(Connection("https://aodb.example.test/api") with { TokenPath = "/ignored", ClientId = "ignored" },
            OutboundEndpointPurpose.Generic, new HashSet<string>(), false);

        errors.Should().BeEmpty();
        connection.BaseUrl.AbsoluteUri.Should().Be("https://aodb.example.test/api/");
        connection.TokenPath.Should().BeNull("an API key endpoint has no token path");
        connection.HeaderName.Should().Be("X-Api-Key");
        Errors(Connection("http://127.0.0.1:5999/", networks: ["127.0.0.0/8"]), lab: true).Should().BeEmpty("a lab host over HTTP on loopback, where the deployment allows it");
    }

    [Theory]
    [InlineData("Host")]
    [InlineData("Authorization ")]
    [InlineData("Cookie")]
    [InlineData("Transfer-Encoding")]
    [InlineData("X-TOTP-Code")]
    [InlineData("X-Api Key")]
    public void Check_Should_RefuseTheHeader_When_ItIsReservedOrMalformed(string header)
    {
        Errors(Connection(header: header)).Should().NotBeEmpty();
    }

    [Fact]
    public void Check_Should_RefuseOtherInputs_When_TheyBreakTheRules()
    {
        Errors(Connection(networks: [])).Should().NotBeEmpty("networks are required");
        Errors(Connection(networks: ["0.0.0.0/0"])).Should().NotBeEmpty("no /0");
        Errors(Connection(kind: "apikeyheader")).Should().NotBeEmpty("exact names");
        Errors(Connection(kind: "4")).Should().NotBeEmpty("no numbers for names");
        Errors(Connection() with { TimeoutSeconds = 0 }).Should().NotBeEmpty();
        Errors(Connection() with { RetryCount = 9 }).Should().NotBeEmpty();
        Errors(Connection() with { PinnedCaPem = "not a pem" }).Should().NotBeEmpty();
        Errors(Connection(pullPath: "/flights")).Should().NotBeEmpty("a pull path is for ACRIS endpoints only");
        Errors(Connection(), OutboundEndpointPurpose.AcrisFlights).Should().NotBeEmpty("an ACRIS endpoint needs its pull path");
        Errors(Connection(pullPath: "/acris/flights?airport=DMO") with { PollSeconds = 10 }, OutboundEndpointPurpose.AcrisFlights).Should().NotBeEmpty("at least 30 seconds");
        Errors(Connection(pullPath: "/acris/flights?airport=DMO"), OutboundEndpointPurpose.AcrisFlights).Should().BeEmpty();
    }

    [Theory]
    [InlineData(OutboundAuthKind.ApiKeyHeader, true)]
    [InlineData(OutboundAuthKind.HmacSignature, false)]
    [InlineData(OutboundAuthKind.OAuth2ClientCredentials, false)]
    public void CheckSecret_Should_TakeOnlyTheKindsFields_When_Checked(OutboundAuthKind kind, bool valid)
    {
        OutboundRules.CheckSecret(new OutboundSecretRequest(ApiKey: "an-api-key-1"), kind).Should().HaveCount(valid ? 0 : kind == OutboundAuthKind.HmacSignature ? 2 : 2);
        OutboundRules.CheckSecret(new OutboundSecretRequest(HmacKey: Convert.ToBase64String(new byte[32])), OutboundAuthKind.HmacSignature).Should().BeEmpty();
        OutboundRules.CheckSecret(new OutboundSecretRequest(HmacKey: Convert.ToBase64String(new byte[16])), OutboundAuthKind.HmacSignature).Should().NotBeEmpty("at least 32 bytes");
        OutboundRules.CheckSecret(new OutboundSecretRequest(ClientSecret: "secret-1234", TotpSeed: "JBSWY3DPEHPK3PXP"), OutboundAuthKind.TotpClientCredentials).Should().BeEmpty();
        OutboundRules.CheckSecret(new OutboundSecretRequest(ClientSecret: "secret-1234", TotpSeed: "not base32!"), OutboundAuthKind.TotpClientCredentials).Should().NotBeEmpty();
        OutboundRules.CheckSecret(null, kind).Should().NotBeEmpty();
    }

    #endregion

    #region Transport

    private sealed class Resolver(params IPAddress[][] answers) : IOutboundResolver
    {
        private int _calls;

        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Task.FromResult(answers[Math.Min(_calls++, answers.Length - 1)]);
    }

    [Fact]
    public async Task Connect_Should_RefuseTheCall_When_AnyAddressTheHostResolvesToIsNotAllowed()
    {
        var both = new Resolver([IPAddress.Parse("10.0.0.5"), IPAddress.Parse("169.254.169.254")]);
        var connect = () => OutboundTransport.ConnectAsync(new DnsEndPoint("aodb.example.test", 443), Ten, both, false, Ct).AsTask();
        (await connect.Should().ThrowAsync<OutboundRefusedException>()).Which.Message.Should().Contain("link-local");

        var none = () => OutboundTransport.ConnectAsync(new DnsEndPoint("aodb.example.test", 443), Ten, new Resolver(Array.Empty<IPAddress>()), false, Ct).AsTask();
        await none.Should().ThrowAsync<OutboundRefusedException>();

        var loopback = () => OutboundTransport.ConnectAsync(new DnsEndPoint("localhost", 443), ["127.0.0.0/8"], new Resolver([IPAddress.Loopback]), false, Ct).AsTask();
        await loopback.Should().ThrowAsync<OutboundRefusedException>();

        var literal = () => OutboundTransport.ConnectAsync(new DnsEndPoint("169.254.169.254", 80), ["169.254.0.0/16"], new Resolver([IPAddress.Parse("10.0.0.5")]), false, Ct).AsTask();
        await literal.Should().ThrowAsync<OutboundRefusedException>("a literal is checked, never resolved");
    }

    [Fact]
    public async Task Connect_Should_CheckTheAddressOfEachConnection_When_DnsChangesItsAnswer()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        // Rebinding: the first answer is allowed, the next one points at the metadata service.
        var rebinding = new Resolver([IPAddress.Loopback], [IPAddress.Parse("169.254.169.254")]);

        await using (var first = await OutboundTransport.ConnectAsync(new DnsEndPoint("partner.example.test", port), ["127.0.0.0/8"], rebinding, true, Ct))
            first.Should().NotBeNull();
        var second = () => OutboundTransport.ConnectAsync(new DnsEndPoint("partner.example.test", port), ["127.0.0.0/8"], rebinding, true, Ct).AsTask();

        await second.Should().ThrowAsync<OutboundRefusedException>();
    }

    #endregion

    #region Handlers

    private sealed class Stub(Func<HttpRequestMessage, int, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
            return Task.FromResult(answer(request, Requests.Count));
        }
    }

    private static OutboundTarget Target(OutboundAuthKind kind = OutboundAuthKind.ApiKeyHeader, int retries = 2, int breaker = 3, bool totpPerRequest = false) =>
        new(Guid.NewGuid(), "aodb", 1, new Uri("https://aodb.example.test/api/"), Ten, kind, "/auth/token", "ariva", "flights.read", "X-Api-Key", "k1", totpPerRequest, null, 5,
            retries, breaker, 30, "unused");

    [Fact]
    public async Task RedirectGuard_Should_RefuseRedirects_When_TheEndpointAnswersWithOne()
    {
        foreach (var status in new[] { HttpStatusCode.Found, HttpStatusCode.MovedPermanently, HttpStatusCode.TemporaryRedirect, HttpStatusCode.PermanentRedirect })
        {
            using var invoker = new HttpMessageInvoker(new OutboundRedirectGuard { InnerHandler = new Stub((_, _) => new HttpResponseMessage(status)) });
            var call = () => invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://aodb.example.test/"), Ct);
            await call.Should().ThrowAsync<OutboundRefusedException>(status.ToString());
        }

        using var notModified = new HttpMessageInvoker(new OutboundRedirectGuard { InnerHandler = new Stub((_, _) => new HttpResponseMessage(HttpStatusCode.NotModified)) });
        (await notModified.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://aodb.example.test/"), Ct)).StatusCode.Should().Be(HttpStatusCode.NotModified);
    }

    [Fact]
    public async Task Resilience_Should_RetryOnlyIdempotentTransientFailures_When_Calls()
    {
        var stub = new Stub((_, n) => new HttpResponseMessage(n < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
        using var invoker = new HttpMessageInvoker(new OutboundResilienceHandler(Target(retries: 2, breaker: 10), TimeProvider.System) { InnerHandler = stub });

        (await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://aodb.example.test/"), Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        stub.Requests.Should().HaveCount(3, "two retries");

        var post = new Stub((_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var posting = new HttpMessageInvoker(new OutboundResilienceHandler(Target(retries: 2, breaker: 10), TimeProvider.System) { InnerHandler = post });
        (await posting.SendAsync(new HttpRequestMessage(HttpMethod.Post, "https://aodb.example.test/"), Ct)).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        post.Requests.Should().HaveCount(1, "a POST is never retried");

        var refused = new Stub((_, _) => throw new OutboundRefusedException("refused"));
        using var refusing = new HttpMessageInvoker(new OutboundResilienceHandler(Target(retries: 2, breaker: 10), TimeProvider.System) { InnerHandler = refused });
        var call = () => refusing.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://aodb.example.test/"), Ct);
        await call.Should().ThrowAsync<OutboundRefusedException>();
        refused.Requests.Should().HaveCount(1, "a refused call is never retried");
    }

    [Fact]
    public async Task Resilience_Should_OpenTheCircuitAndTryOnceAfterTheBreak_When_FailuresRepeat()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero));
        var healthy = false;
        var stub = new Stub((_, _) => new HttpResponseMessage(healthy ? HttpStatusCode.OK : HttpStatusCode.BadGateway));
        var handler = new OutboundResilienceHandler(Target(retries: 0, breaker: 3), clock) { InnerHandler = stub };
        using var invoker = new HttpMessageInvoker(handler);
        Task<HttpResponseMessage> Get() => invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://aodb.example.test/"), Ct);

        for (var i = 0; i < 3; i++)
            (await Get()).StatusCode.Should().Be(HttpStatusCode.BadGateway);
        handler.IsOpen.Should().BeTrue();
        await ((Func<Task>)Get).Should().ThrowAsync<OutboundUnavailableException>("open: no call is made");
        stub.Requests.Should().HaveCount(3);

        clock.Advance(TimeSpan.FromSeconds(31));
        healthy = true;
        (await Get()).StatusCode.Should().Be(HttpStatusCode.OK, "one trial after the break, and it closes the circuit");
        handler.IsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task ApiKeyAndHmac_Should_SignEachRequest_When_Configured()
    {
        var stub = new Stub((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        using (var invoker = new HttpMessageInvoker(new ApiKeyHeaderHandler("X-Api-Key", "the-key-123") { InnerHandler = stub }))
            await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://aodb.example.test/x"), Ct);
        stub.Requests[0].Headers.GetValues("X-Api-Key").Should().Equal("the-key-123");

        var key = RandomNumberGenerator.GetBytes(32);
        var clock = new ManualClock(DateTimeOffset.FromUnixTimeSeconds(1_790_000_000));
        var signed = new Stub((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        using (var invoker = new HttpMessageInvoker(new HmacSignatureHandler("k1", key, clock) { InnerHandler = signed }))
            await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Post, "https://aodb.example.test/x?y=1") { Content = new StringContent("{\"a\":1}") }, Ct);
        var headers = signed.Requests[0].Headers;
        headers.GetValues("X-Ariva-Key-Id").Should().Equal("k1");
        headers.GetValues("X-Ariva-Timestamp").Should().Equal("1790000000");
        var expected = Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("POST\n/x?y=1\n1790000000\n" + Convert.ToHexStringLower(SHA256.HashData("{\"a\":1}"u8.ToArray())))));
        headers.GetValues("X-Ariva-Signature").Should().Equal(expected);
    }

    [Fact]
    public async Task TokenHandler_Should_CacheTheTokenAndDropItOn401_When_UsingTotpClientCredentials()
    {
        var seed = Base32.Encode(Totp.NewSecret());
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero));
        var tokens = 0;
        var stub = new Stub((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/auth/token")
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { accessToken = $"t{++tokens}", expiresAt = clock.GetUtcNow().AddMinutes(15) }), Encoding.UTF8, "application/json")
                };
            return new HttpResponseMessage(request.Headers.Authorization?.Parameter == "t1" && tokens == 1 && request.RequestUri.AbsolutePath == "/api/stale" ? HttpStatusCode.Unauthorized : HttpStatusCode.OK);
        });
        var secret = new OutboundSecret("client-secret-1", seed, null, null, null, null);
        using var invoker = new HttpMessageInvoker(new TokenHandler(Target(OutboundAuthKind.TotpClientCredentials, totpPerRequest: true), secret, clock) { InnerHandler = stub });

        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://aodb.example.test/api/a"), Ct);
        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://aodb.example.test/api/b"), Ct);
        tokens.Should().Be(1, "the token is cached");
        var exchange = stub.Requests[0];
        exchange.Method.Should().Be(HttpMethod.Post);
        var body = JsonDocument.Parse(stub.Bodies[0]).RootElement;
        body.GetProperty("clientId").GetString().Should().Be("ariva");
        body.GetProperty("clientSecret").GetString().Should().Be("client-secret-1");
        Totp.Match(Base32.Decode(seed), body.GetProperty("totpCode").GetString(), clock.GetUtcNow(), null).Should().NotBeNull();
        stub.Requests[1].Headers.Authorization!.Parameter.Should().Be("t1");
        stub.Requests[1].Headers.GetValues("X-TOTP-Code").Single().Should().HaveLength(6);

        (await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://aodb.example.test/api/stale"), Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        // ARV-050: one exchange per TOTP step (AMAN's replay guard): within the step of the first, no second exchange is sent.
        var again = () => invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://aodb.example.test/api/c"), Ct);
        await again.Should().ThrowAsync<OutboundUnavailableException>();
        stub.Requests.Count(r => r.Method == HttpMethod.Post).Should().Be(1);
        clock.Advance(TimeSpan.FromSeconds(30));
        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://aodb.example.test/api/c"), Ct);
        tokens.Should().Be(2, "a 401 drops the token; the next step exchanges again");

        clock.Advance(TimeSpan.FromMinutes(15));
        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://aodb.example.test/api/d"), Ct);
        tokens.Should().Be(3, "renewed 60 seconds before it expires");
    }

    [Fact]
    public async Task TokenHandler_Should_UseClientCredentialsWithBasicAuthentication_When_OAuth2()
    {
        var stub = new Stub((request, _) => request.RequestUri!.AbsolutePath == "/api/auth/token"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"access_token":"o1","token_type":"Bearer","expires_in":300}""", Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.OK));
        var secret = new OutboundSecret("s3cr3t:with/colon", null, null, null, null, null);
        using var invoker = new HttpMessageInvoker(new TokenHandler(Target(OutboundAuthKind.OAuth2ClientCredentials), secret, TimeProvider.System) { InnerHandler = stub });

        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://aodb.example.test/api/a"), Ct);

        var exchange = stub.Requests[0];
        exchange.RequestUri!.AbsoluteUri.Should().Be("https://aodb.example.test/api/auth/token", "the same origin as the endpoint");
        stub.Bodies[0].Should().Be("grant_type=client_credentials&scope=flights.read");
        Encoding.UTF8.GetString(Convert.FromBase64String(exchange.Headers.Authorization!.Parameter!)).Should().Be("ariva:s3cr3t%3Awith%2Fcolon");
        stub.Requests[1].Headers.Authorization!.ToString().Should().Be("Bearer o1");
    }

    [Fact]
    public async Task TokenHandler_Should_RefuseTheCall_When_TheTokenAnswerIsUnusable()
    {
        foreach (var body in new[] { "not json", """{"access_token":"has space"}""", """{"token":"x"}""" })
        {
            var stub = new Stub((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
            using var invoker = new HttpMessageInvoker(new TokenHandler(Target(OutboundAuthKind.OAuth2ClientCredentials), new OutboundSecret("s", null, null, null, null, null),
                TimeProvider.System) { InnerHandler = stub });
            var call = () => invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://aodb.example.test/api/a"), Ct);
            await call.Should().ThrowAsync<HttpRequestException>(body);
        }
    }

    #endregion

    #region The whole chain against a real server

    [Fact]
    public async Task Clients_Should_CallTheRegisteredEndpointAndRefuseItsRedirects_When_Built()
    {
        await using var server = await LoopbackServer.StartAsync(async c =>
        {
            if (c.Request.Path == "/api/redirect")
                c.Response.Redirect("http://169.254.169.254/latest/meta-data/");
            else if (c.Request.Path == "/api/flights" && c.Request.Headers["X-Api-Key"] == "the-key-123")
                await c.Response.WriteAsync("[]", Ct);
            else
                c.Response.StatusCode = StatusCodes.Status401Unauthorized;
        });
        var protector = new EphemeralDataProtectionProvider();
        var secrets = new OutboundSecrets(protector);
        var (protectedSecret, _, _) = secrets.Protect(new OutboundSecretRequest(ApiKey: "the-key-123"), OutboundAuthKind.ApiKeyHeader);
        var target = new OutboundTarget(Guid.NewGuid(), "lab", 1, new Uri($"http://127.0.0.1:{server.Port}/api/"), ["127.0.0.0/8"], OutboundAuthKind.ApiKeyHeader, null, null, null,
            "X-Api-Key", null, false, null, 5, 0, 5, 30, protectedSecret);
        using var clients = new OutboundClients(secrets, new OutboundSettings { AllowLoopback = true, LabHosts = ["127.0.0.1"] }, new DnsOutboundResolver(), TimeProvider.System);

        (await clients.For(target).GetAsync("flights", Ct)).StatusCode.Should().Be(HttpStatusCode.OK, "the API key reached the endpoint");
        var redirect = () => clients.For(target).GetAsync("redirect", Ct);
        await redirect.Should().ThrowAsync<OutboundRefusedException>("redirects are refused, never followed");

        using var strict = new OutboundClients(secrets, new OutboundSettings { AllowLoopback = false, LabHosts = ["127.0.0.1"] }, new DnsOutboundResolver(), TimeProvider.System);
        var loopback = () => strict.For(target).GetAsync("flights", Ct);
        await loopback.Should().ThrowAsync<OutboundRefusedException>("loopback is refused outside a lab");
    }

    private sealed class LoopbackServer : IAsyncDisposable
    {
        private WebApplication _app;
        public int Port { get; private set; }

        // A plain request delegate: the stand-in partner has no endpoints of Ariva's own.
        public static async Task<LoopbackServer> StartAsync(RequestDelegate handle, Microsoft.AspNetCore.Server.Kestrel.Https.HttpsConnectionAdapterOptions https = null,
            X509Certificate2 offlineCertificate = null)
        {
            var builder = WebApplication.CreateSlimBuilder();
            if (offlineCertificate is not null)
                // The server's own chain built offline: Kestrel would otherwise download the missing issuer itself.
                builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(new Microsoft.AspNetCore.Server.Kestrel.Https.TlsHandshakeCallbackOptions
                {
                    OnConnection = _ => ValueTask.FromResult(new System.Net.Security.SslServerAuthenticationOptions
                    {
                        ServerCertificateContext = System.Net.Security.SslStreamCertificateContext.Create(offlineCertificate, null, offline: true)
                    })
                })));
            else if (https is null)
                builder.WebHost.UseUrls("http://127.0.0.1:0");
            else
                builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(https)));
            var app = builder.Build();
            app.Run(handle);
            await app.StartAsync(TestContext.Current.CancellationToken);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            return new LoopbackServer { _app = app, Port = new Uri(address).Port };
        }

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }

    #endregion

    #region TLS and client certificates

    private static readonly DateTimeOffset Today = DateTimeOffset.UtcNow;

    private static X509Certificate2 Authority(string name, X509Certificate2 issuer = null)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        if (issuer is null)
            return request.CreateSelfSigned(Today.AddDays(-30), Today.AddYears(2));
        using var issued = request.Create(issuer, Today.AddDays(-20), Today.AddYears(1), RandomNumberGenerator.GetBytes(8));
        return issued.CopyWithPrivateKey(key);
    }

    private static X509Certificate2 Leaf(X509Certificate2 issuer, string dns, DateTimeOffset? notAfter = null, string aia = null, bool serverAuth = true)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={dns}", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(dns);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(serverAuth ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2")], false));
        if (aia is not null)
            request.CertificateExtensions.Add(new X509AuthorityInformationAccessExtension(null, [aia]));
        var until = notAfter ?? Today.AddDays(30);
        using var issued = issuer is null
            ? request.CreateSelfSigned(Today.AddDays(-10), until)
            : request.Create(issuer, Today.AddDays(-10), until, RandomNumberGenerator.GetBytes(8));
        // Through PKCS#12 so the key is usable by the TLS stack on every platform.
        using var withKey = issued.HasPrivateKey ? null : issued.CopyWithPrivateKey(key);
        return X509CertificateLoader.LoadPkcs12((withKey ?? issued).Export(X509ContentType.Pkcs12), null);
    }

    private static Microsoft.AspNetCore.Server.Kestrel.Https.HttpsConnectionAdapterOptions Https(X509Certificate2 leaf, X509Certificate2 intermediate = null,
        bool requireClientCertificate = false) => new()
    {
        ServerCertificate = leaf,
        ServerCertificateChain = intermediate is null ? null : [intermediate],
        ClientCertificateMode = requireClientCertificate
            ? Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.RequireCertificate
            : Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.NoCertificate,
        ClientCertificateValidation = (_, _, _) => true
    };

    private static (OutboundClients Clients, OutboundTarget Target) Tls(int port, X509Certificate2 pinned, OutboundAuthKind kind = OutboundAuthKind.ApiKeyHeader,
        OutboundSecretRequest secret = null)
    {
        var secrets = new OutboundSecrets(new EphemeralDataProtectionProvider());
        var (protectedSecret, _, error) = secrets.Protect(secret ?? new OutboundSecretRequest(ApiKey: "the-key-123"), kind);
        error.Should().BeNull();
        var target = new OutboundTarget(Guid.NewGuid(), "tls", 1, new Uri($"https://aodb.test:{port}/"), ["127.0.0.0/8"], kind, null, null, null,
            kind == OutboundAuthKind.ApiKeyHeader ? "X-Api-Key" : null, null, false, pinned?.ExportCertificatePem(), 5, 0, 5, 30, protectedSecret);
        return (new OutboundClients(secrets, new OutboundSettings { AllowLoopback = true }, new Resolver([IPAddress.Loopback]), TimeProvider.System), target);
    }

    [Fact]
    public async Task Tls_Should_TrustOnlyThePinnedCa_When_TheEndpointPinsOne()
    {
        var root = Authority("Ariva test root");
        var intermediate = Authority("Ariva test intermediate", root);
        await using var server = await LoopbackServer.StartAsync(c => c.Response.WriteAsync("ok", Ct), Https(Leaf(intermediate, "aodb.test"), intermediate));

        var (clients, target) = Tls(server.Port, root);
        using (clients)
            (await clients.For(target).GetAsync("x", Ct)).StatusCode.Should().Be(HttpStatusCode.OK, "a leaf under the pinned root, with the intermediate the server sent");

        var (others, otherTarget) = Tls(server.Port, Authority("Another root"));
        using (others)
        {
            var call = () => others.For(otherTarget).GetAsync("x", Ct);
            await call.Should().ThrowAsync<HttpRequestException>("a chain to another CA");
        }

        var (system, systemTarget) = Tls(server.Port, null);
        using (system)
        {
            var call = () => system.For(systemTarget).GetAsync("x", Ct);
            await call.Should().ThrowAsync<HttpRequestException>("without a pin, only the system roots");
        }
    }

    [Theory]
    [InlineData("name", "a certificate for another name")]
    [InlineData("expired", "an expired certificate")]
    [InlineData("self", "a self-signed certificate")]
    public async Task Tls_Should_RefuseTheServer_When_ItsCertificateIsWrong(string kind, string why)
    {
        var root = Authority("Ariva test root");
        var leaf = kind switch
        {
            "name" => Leaf(root, "other.test"),
            "expired" => Leaf(root, "aodb.test", notAfter: Today.AddDays(-1)),
            _ => Leaf(null, "aodb.test")
        };
        await using var server = await LoopbackServer.StartAsync(c => c.Response.WriteAsync("ok", Ct), Https(leaf));
        var (clients, target) = Tls(server.Port, root);
        using (clients)
        {
            var call = () => clients.For(target).GetAsync("x", Ct);
            await call.Should().ThrowAsync<HttpRequestException>(why);
        }
    }

    [Fact]
    public async Task Tls_Should_NeverFetchAMissingIssuer_When_TheCertificateNamesOne()
    {
        // The leaf's issuer is neither sent nor trusted, and its AIA points at a listener: a download would be a request
        // Ariva makes outside the guarded transport (CWE-918).
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var aiaPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var root = Authority("Ariva test root");
        var intermediate = Authority("Ariva hidden intermediate", root);
        await using var server = await LoopbackServer.StartAsync(c => c.Response.WriteAsync("ok", Ct),
            offlineCertificate: Leaf(intermediate, "aodb.test", aia: $"http://127.0.0.1:{aiaPort}/intermediate.cer"));
        var (clients, target) = Tls(server.Port, root);

        using (clients)
        {
            var call = () => clients.For(target).GetAsync("x", Ct);
            await call.Should().ThrowAsync<HttpRequestException>();
        }

        listener.Pending().Should().BeFalse("the missing issuer was not downloaded");
    }

    [Fact]
    public async Task MutualTls_Should_PresentTheClientCertificate_When_TheEndpointUsesIt()
    {
        var root = Authority("Ariva test root");
        string presented = null;
        await using var server = await LoopbackServer.StartAsync(c =>
        {
            presented = c.Connection.ClientCertificate?.Thumbprint;
            return c.Response.WriteAsync("ok", Ct);
        }, Https(Leaf(root, "aodb.test"), requireClientCertificate: true));
        var client = Leaf(root, "ariva-client", serverAuth: false);
        var pfx = Convert.ToBase64String(client.Export(X509ContentType.Pkcs12, "pfx-password"));
        var (clients, target) = Tls(server.Port, root, OutboundAuthKind.MutualTls, new OutboundSecretRequest(CertificatePfxBase64: pfx, CertificatePassword: "pfx-password"));

        using (clients)
            (await clients.For(target).GetAsync("x", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        presented.Should().Be(client.Thumbprint);
    }

    [Fact]
    public void MutualTls_Should_RefuseTheCertificate_When_ItCannotBeUsed()
    {
        var secrets = new OutboundSecrets(new EphemeralDataProtectionProvider());
        var root = Authority("Ariva test root");
        var good = Leaf(root, "ariva-client", serverAuth: false);
        string Pfx(X509Certificate2 certificate, string password) => Convert.ToBase64String(certificate.Export(X509ContentType.Pkcs12, password));
        using var publicOnly = X509CertificateLoader.LoadCertificate(good.Export(X509ContentType.Cert));
        var expired = Leaf(root, "ariva-client", notAfter: Today.AddDays(-1), serverAuth: false);

        secrets.Protect(new OutboundSecretRequest(CertificatePfxBase64: Pfx(good, "pw"), CertificatePassword: "pw"), OutboundAuthKind.MutualTls).Error.Should().BeNull();
        secrets.Protect(new OutboundSecretRequest(CertificatePfxBase64: Pfx(publicOnly, "pw"), CertificatePassword: "pw"), OutboundAuthKind.MutualTls).Error.Should().Contain("private key");
        secrets.Protect(new OutboundSecretRequest(CertificatePfxBase64: Pfx(expired, "pw"), CertificatePassword: "pw"), OutboundAuthKind.MutualTls).Error.Should().Contain("expired");
        secrets.Protect(new OutboundSecretRequest(CertificatePfxBase64: Pfx(good, "pw"), CertificatePassword: "wrong"), OutboundAuthKind.MutualTls).Error.Should().Contain("password");
        secrets.Protect(new OutboundSecretRequest(CertificatePfxBase64: "not base64!", CertificatePassword: "pw"), OutboundAuthKind.MutualTls).Error.Should().NotBeNull();
        OutboundRules.CheckSecret(new OutboundSecretRequest(CertificatePfxBase64: "abc", CertificatePassword: "pw"), OutboundAuthKind.MutualTls).Should().BeEmpty();
        OutboundRules.CheckSecret(new OutboundSecretRequest(CertificatePassword: "pw"), OutboundAuthKind.MutualTls).Should().NotBeEmpty("the certificate is required");
        OutboundRules.CheckSecret(new OutboundSecretRequest(CertificatePfxBase64: "abc", ApiKey: "an-api-key-1"), OutboundAuthKind.MutualTls).Should().NotBeEmpty("no other kind's field");
    }

    #endregion

    #region Settings, token answers and the breaker's trial

    private static IConfiguration Config(string applicationEnvironment, bool loopback = true, int maxResponseBytes = 1024 * 1024) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Application:Environment"] = applicationEnvironment,
            ["Integration:Outbound:AllowLoopback"] = loopback ? "true" : "false",
            ["Integration:Outbound:MaxResponseBytes"] = maxResponseBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }).Build();

    [Theory]
    [InlineData("k8s-prd", "vm-local", "a cluster host whose environment file is missing (Application:Environment defaults to vm-local)")]
    [InlineData("vm-local", "k8s-prd", "a cluster's application environment")]
    [InlineData("k8s-dev", "k8s-dev", "the dev cluster")]
    public void Settings_Should_RefuseLabSettings_When_EitherEnvironmentIsNotALab(string host, string application, string why)
    {
        var calls = () => new ServiceCollection().AddArivaOutboundCalls(Config(application), host);
        var endpoints = () => new ServiceCollection().AddArivaOutboundEndpoints(Config(application), host);

        calls.Should().Throw<InvalidOperationException>(why).WithMessage("*vm-local*");
        endpoints.Should().Throw<InvalidOperationException>(why);
    }

    [Fact]
    public void Settings_Should_AllowLabSettingsInVmLocalOnlyAndBoundTheAnswerSize_When_Registered()
    {
        new ServiceCollection().AddArivaOutboundCalls(Config("vm-local"), "vm-local").Should().NotBeNull();
        new ServiceCollection().AddArivaOutboundCalls(Config("k8s-prd", loopback: false), "k8s-prd").Should().NotBeNull("no lab settings");
        var tooLarge = () => new ServiceCollection().AddArivaOutboundCalls(Config("k8s-prd", loopback: false, maxResponseBytes: 100 * 1024 * 1024), "k8s-prd");
        tooLarge.Should().Throw<InvalidOperationException>().WithMessage("*MaxResponseBytes*");
    }

    private sealed class UnknownLength(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext context) => stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    [Fact]
    public async Task TokenHandler_Should_RefuseALargeTokenAnswer_When_ItHasNoLength()
    {
        var large = Encoding.UTF8.GetBytes("{\"access_token\":\"" + new string('a', 70_000) + "\"}");
        var stub = new Stub((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLength(large) });
        using var invoker = new HttpMessageInvoker(new TokenHandler(Target(OutboundAuthKind.OAuth2ClientCredentials), new OutboundSecret("s", null, null, null, null, null),
            TimeProvider.System) { InnerHandler = stub });

        var call = () => invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://aodb.example.test/api/a"), Ct);

        (await call.Should().ThrowAsync<HttpRequestException>()).Which.Message.Should().Contain("too large");
    }

    [Fact]
    public async Task Resilience_Should_LetTheCircuitCloseAgain_When_ATrialWasRefusedOrCancelled()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero));
        var mode = "fail";
        var stub = new Stub((_, _) => mode switch
        {
            "refuse" => throw new OutboundRefusedException("refused"),
            "cancel" => throw new OperationCanceledException(),
            "fail" => new HttpResponseMessage(HttpStatusCode.BadGateway),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
        });
        var handler = new OutboundResilienceHandler(Target(retries: 0, breaker: 2), clock) { InnerHandler = stub };
        using var invoker = new HttpMessageInvoker(handler);
        Task<HttpResponseMessage> Get(CancellationToken ct) => invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://aodb.example.test/"), ct);

        await Get(Ct);
        await Get(Ct);
        handler.IsOpen.Should().BeTrue();

        clock.Advance(TimeSpan.FromSeconds(31));
        mode = "refuse";
        await ((Func<Task>)(() => Get(Ct))).Should().ThrowAsync<OutboundRefusedException>("the trial is refused");
        handler.IsOpen.Should().BeTrue("a refused trial counts as a failure and opens the circuit again");

        clock.Advance(TimeSpan.FromSeconds(31));
        mode = "cancel";
        using (var cancelled = new CancellationTokenSource())
        {
            await cancelled.CancelAsync();
            await ((Func<Task>)(() => Get(cancelled.Token))).Should().ThrowAsync<OperationCanceledException>("the caller gave up during the trial");
        }

        mode = "ok";
        (await Get(Ct)).StatusCode.Should().Be(HttpStatusCode.OK, "the next call is a trial again, and closes the circuit");
        handler.IsOpen.Should().BeFalse();
    }

    #endregion

    #region ACRIS

    private static readonly IReadOnlySet<string> Dmo = new HashSet<string>(["DMO"]);

    private const string Flights = """
        {"flights":[
          {"flightNumber":{"airlineCode":"RJ","trackNumber":"0111"},"originDate":"2026-10-03","departureAirport":"AMM","arrivalAirport":"DMO",
           "arrival":{"scheduled":"2026-10-03T10:00:00Z","estimated":"2026-10-03T10:10:00Z","block":"2026-10-03T10:14:00Z","gate":"G4","stand":"B7"},
           "aircraftType":"320","flightStatus":"Landed","codeShares":[{"airlineCode":"XR","trackNumber":"01214"}],"extra":{"anything":[1,2,3]}},
          {"flightNumber":{"airlineCode":"RJ","trackNumber":"112"},"originDate":"2026-10-03","departureAirport":"DMO","arrivalAirport":"CAI",
           "departure":{"scheduled":"2026-10-03T12:00:00Z"},"flightStatus":"cancelled"},
          {"flightNumber":{"airlineCode":"RJ","trackNumber":"113"},"originDate":"2026-10-03","departureAirport":"AMM","arrivalAirport":"CAI"}
        ]}
        """;

    [Fact]
    public void Acris_Should_ReadAndMapTheSitesFlights_When_TheAnswerIsValid()
    {
        var (flights, error) = AcrisReader.Read(Encoding.UTF8.GetBytes(Flights));

        error.Should().BeNull();
        flights.Should().HaveCount(3);
        var arrival = AcrisMapping.ToLeg(flights[0], Dmo).Leg;
        arrival.Should().Match<FlightLegData>(l => l.FlightKey == "RJ111-20261003-A" && l.Number == "111" && l.Direction == "Arrival" && l.Gate == "G4" && l.Stand == "B7" &&
                                                   l.OnBlockUtc == new DateTime(2026, 10, 3, 10, 14, 0, DateTimeKind.Utc) && l.Status == null);
        arrival.Codeshares.Should().Equal("XR1214");
        FlightRules.Check(arrival, new DateTime(2026, 10, 3, 9, 0, 0, DateTimeKind.Utc)).Errors.Should().BeEmpty();
        AcrisMapping.ToLeg(flights[1], Dmo).Leg.Should().Match<FlightLegData>(l => l.FlightKey == "RJ112-20261003-D" && l.Status == "Cancelled");
        AcrisMapping.ToLeg(flights[2], Dmo).Error.Should().Be(FlightKeys.NotThisSite);
    }

    [Theory]
    [InlineData("""{"flights":{}}""")]
    [InlineData("""{"flights":[{"flightNumber":{"airlineCode":"RJ","trackNumber":111}}]}""")]
    [InlineData("""[{"originDate":"2026-10-03","originDate":"2026-10-04"}]""")]
    [InlineData("""[/* c */]""")]
    [InlineData("""not json""")]
    [InlineData("""{"other":[]}""")]
    public void Acris_Should_RefuseTheAnswer_When_ItIsNotAListOfFlights(string json)
    {
        var (flights, error) = AcrisReader.Read(Encoding.UTF8.GetBytes(json));

        flights.Should().BeNull();
        error.Should().NotBeNullOrEmpty().And.NotContain("111");
    }

    [Fact]
    public void Acris_Should_RefuseTheAnswer_When_ItHasTooManyFlights()
    {
        var many = "[" + string.Join(',', Enumerable.Repeat("{}", AcrisReader.MaxFlights + 1)) + "]";

        AcrisReader.Read(Encoding.UTF8.GetBytes(many)).Error.Should().Contain("5000");
    }

    [Fact]
    public void FlightKeys_Should_BeTheSameForEveryAdapter_When_NumbersArePadded()
    {
        FlightKeys.Of("rj", "0111", null, new DateOnly(2026, 10, 3), true).Should().Be("RJ111-20261003-A");
        FlightKeys.Number("0000").Should().Be("0");
        FlightKeys.SiteSide("dmo", "cai", Dmo).Should().BeFalse();
    }

    #endregion
}
