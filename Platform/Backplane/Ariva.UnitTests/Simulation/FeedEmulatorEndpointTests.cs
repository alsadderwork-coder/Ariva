using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ariva.Simulation.Api.Emulators;
using Ariva.Simulation.Api.Emulators.Aodb;
using Ariva.Simulation.Api.Emulators.Integration;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ariva.UnitTests.Simulation;

/// <summary>
/// ARV-029, CWE-287: the feed emulators' controls need an operator key with the control scope; client secrets go in and
/// never come out. The mock AMAN exchanges client id, secret and a TOTP code for a token (once per step, the same 401
/// for every failure, limited per address) and its feed API needs the token and a current X-TOTP-Code. The AODB's ACRIS
/// answer needs its API key and honours If-Modified-Since. The emulators' Ariva client exchanges its credentials, keeps
/// the token, sends X-TOTP-Code and Idempotency-Key, and never reuses a TOTP step.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class FeedEmulatorEndpointTests
{
    private const string Route = "/api/v1/simulation/feeds";
    private const string ReadKey = "sim-read-3f0a9c2e8b7d4e61a5c0f9b2d8e7a6c1";
    private const string ControlKey = "sim-ctl-7b1e4d9a2c6f8e03b5a7d1c9e4f2a8b6";
    private const string AmanSecret = "mock-aman-secret-0123456789abcdef";
    private const string AmanSeed = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";
    private const string AcrisKey = "acris-key-5f1e0c9b8a7d6e5f4a3b2c1d";
    private const string ClientId = "ic_ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string ClientSecret = "ics_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    private static string Digest(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IArivaHost Host(int authPerMinute = 30) =>
        ArivaHosts.Create(ArivaHosts.Simulation, configure: builder =>
        {
            builder.UseSetting("Simulation:Control:Keys:0:Name", "demo-reader");
            builder.UseSetting("Simulation:Control:Keys:0:Sha256", Digest(ReadKey));
            builder.UseSetting("Simulation:Control:Keys:0:Scopes:0", "read");
            builder.UseSetting("Simulation:Control:Keys:1:Name", "demo-operator");
            builder.UseSetting("Simulation:Control:Keys:1:Sha256", Digest(ControlKey));
            builder.UseSetting("Simulation:Control:Keys:1:Scopes:0", "control");
            builder.UseSetting("Simulation:Aman:Mock:Clients:0:ClientId", "ariva-connector");
            builder.UseSetting("Simulation:Aman:Mock:Clients:0:SecretSha256", Digest(AmanSecret));
            builder.UseSetting("Simulation:Aman:Mock:Clients:0:TotpSecret", AmanSeed);
            builder.UseSetting("Simulation:Aman:Mock:AuthPerMinute", authPerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.UseSetting("Simulation:Aman:Kafka:BootstrapServers", string.Empty);
            builder.UseSetting("Simulation:Aodb:AcrisKeySha256", Digest(AcrisKey));
        });

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string bearer = null, string json = null,
        IDictionary<string, string> headers = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
            request.Headers.TryAddWithoutValidation(name, value);
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await client.SendAsync(request, Ct);
    }

    private static string Code(DateTimeOffset? at = null) => Totp.Code(Totp.FromBase32(AmanSeed), at ?? DateTimeOffset.UtcNow);

    [Theory]
    [InlineData("POST", "/play", "{\"minute\":600}")]
    [InlineData("PUT", "/clients", "{}")]
    public async Task Controls_Should_Return401WithoutAKeyAnd403ForAReadKey_When_Called(string method, string path, string json)
    {
        await using var app = Host();
        using var client = app.CreateClient();

        using var anonymous = await SendAsync(client, new HttpMethod(method), Route + path, null, json);
        using var reader = await SendAsync(client, new HttpMethod(method), Route + path, ReadKey, json);
        using var status = await SendAsync(client, HttpMethod.Get, Route, ReadKey);
        using var codes = await SendAsync(client, HttpMethod.Get, Route + "/aman-codes", ReadKey);

        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        reader.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        status.StatusCode.Should().Be(HttpStatusCode.OK);
        (await codes.Content.ReadAsStringAsync(Ct)).Should().Contain("\"amanCode\":\"IN07\",\"arivaCode\":\"AR-07\"");
    }

    [Fact]
    public async Task Clients_Should_NeverEchoASecretAndRefuseMalformedOnes_When_Replaced()
    {
        await using var app = Host();
        using var client = app.CreateClient();

        using var good = await SendAsync(client, HttpMethod.Put, Route + "/clients", ControlKey,
            $"{{\"aman\":{{\"clientId\":\"{ClientId}\",\"clientSecret\":\"{ClientSecret}\",\"totpSecret\":\"{AmanSeed}\",\"perRequestTotp\":true}}}}");
        using var bad = await SendAsync(client, HttpMethod.Put, Route + "/clients", ControlKey,
            "{\"aodb\":{\"clientId\":\"ic_short\",\"clientSecret\":\"ics_leaked-secret\",\"totpSecret\":\"not base32!\"}}");

        good.StatusCode.Should().Be(HttpStatusCode.OK);
        var text = await good.Content.ReadAsStringAsync(Ct);
        text.Should().NotContain("ics_").And.NotContain(AmanSeed);
        JsonDocument.Parse(text).RootElement.GetProperty("aman").GetProperty("transports")[0].GetProperty("configured").GetBoolean().Should().BeTrue();
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await bad.Content.ReadAsStringAsync(Ct)).Should().NotContain("leaked").And.NotContain("ic_short");
    }

    [Fact]
    public async Task MockAmanAuth_Should_IssueATokenOncePerStepAndRefuseEverythingElseAlike_When_Exchanged()
    {
        await using var app = Host();
        using var client = app.CreateClient();
        string Body(string id, string secret, string code) => JsonSerializer.Serialize(new { clientId = id, clientSecret = secret, totpCode = code });

        using var wrongSecret = await SendAsync(client, HttpMethod.Post, "/aman/api/v1/auth", json: Body("ariva-connector", "nope", Code()));
        using var unknown = await SendAsync(client, HttpMethod.Post, "/aman/api/v1/auth", json: Body("someone-else", AmanSecret, Code()));
        using var wrongCode = await SendAsync(client, HttpMethod.Post, "/aman/api/v1/auth", json: Body("ariva-connector", AmanSecret, "000000"));
        var code = Code();
        using var ok = await SendAsync(client, HttpMethod.Post, "/aman/api/v1/auth", json: Body("ariva-connector", AmanSecret, code));
        using var replay = await SendAsync(client, HttpMethod.Post, "/aman/api/v1/auth", json: Body("ariva-connector", AmanSecret, code));

        foreach (var refused in new[] { wrongSecret, unknown, replay })
        {
            refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await refused.Content.ReadAsStringAsync(Ct)).Should().Be("{\"error\":\"invalid_client\"}");
        }

        wrongCode.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "unless 000000 happens to be the code, once in a million runs");
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        ok.Headers.CacheControl!.NoStore.Should().BeTrue();
        var token = JsonDocument.Parse(await ok.Content.ReadAsStringAsync(Ct)).RootElement;
        token.GetProperty("expiresAt").GetDateTimeOffset().Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(15), TimeSpan.FromMinutes(1));
        var accessToken = token.GetProperty("accessToken").GetString();

        // The feed API needs the token and a current code; it answers what AMAN published, in AMAN's Result envelope.
        using var play = await SendAsync(client, HttpMethod.Post, Route + "/play", ControlKey, "{\"minute\":1110}");
        play.StatusCode.Should().Be(HttpStatusCode.OK);
        using var noCode = await SendAsync(client, HttpMethod.Get, "/aman/api/v1/feed/desk-sessions", accessToken);
        using var noToken = await SendAsync(client, HttpMethod.Get, "/aman/api/v1/feed/desk-sessions", headers: new Dictionary<string, string> { ["X-TOTP-Code"] = Code() });
        using var operatorKey = await SendAsync(client, HttpMethod.Get, "/aman/api/v1/feed/desk-sessions", ControlKey, headers: new Dictionary<string, string> { ["X-TOTP-Code"] = Code() });
        using var feed = await SendAsync(client, HttpMethod.Get, "/aman/api/v1/feed/desk-sessions?after=0&limit=5", accessToken,
            headers: new Dictionary<string, string> { ["X-TOTP-Code"] = Code() });
        using var unknownContract = await SendAsync(client, HttpMethod.Get, "/aman/api/v1/feed/officers", accessToken, headers: new Dictionary<string, string> { ["X-TOTP-Code"] = Code() });

        noCode.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        noToken.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        operatorKey.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "an operator key is not an AMAN token");
        unknownContract.StatusCode.Should().Be(HttpStatusCode.NotFound);
        feed.StatusCode.Should().Be(HttpStatusCode.OK);
        var envelope = JsonDocument.Parse(await feed.Content.ReadAsStringAsync(Ct)).RootElement;
        envelope.GetProperty("hasErrors").GetBoolean().Should().BeFalse();
        var items = envelope.GetProperty("data").GetProperty("items");
        items.GetArrayLength().Should().Be(5);
        items[0].GetProperty("state").GetString().Should().BeOneOf("Opened", "Paused");
        envelope.GetProperty("data").GetProperty("next").GetInt64().Should().Be(5);
    }

    [Fact]
    public async Task MockAmanAuth_Should_Return429_When_AnAddressExchangesTooOften()
    {
        await using var app = Host(authPerMinute: 2);
        using var client = app.CreateClient();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            using var response = await SendAsync(client, HttpMethod.Post, "/aman/api/v1/auth", json: "{\"clientId\":\"x-client\",\"clientSecret\":\"x\",\"totpCode\":\"123456\"}");
            statuses.Add(response.StatusCode);
        }

        statuses.Should().Equal(HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Acris_Should_NeedTheKeyAndHonourIfModifiedSince_When_Pulled()
    {
        await using var app = Host();
        using var client = app.CreateClient();
        using var play = await SendAsync(client, HttpMethod.Post, Route + "/play", ControlKey, "{\"minute\":1110}");

        using var none = await SendAsync(client, HttpMethod.Get, "/aodb/acris/flights");
        using var wrong = await SendAsync(client, HttpMethod.Get, "/aodb/acris/flights", headers: new Dictionary<string, string> { ["X-Api-Key"] = "guess" });
        using var right = await SendAsync(client, HttpMethod.Get, "/aodb/acris/flights", headers: new Dictionary<string, string> { ["X-Api-Key"] = AcrisKey });
        none.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        right.StatusCode.Should().Be(HttpStatusCode.OK);
        var flights = JsonDocument.Parse(await right.Content.ReadAsStringAsync(Ct)).RootElement;
        flights.GetArrayLength().Should().BeGreaterThan(50);
        var modified = right.Content.Headers.LastModified!.Value;

        using var unchanged = await SendAsync(client, HttpMethod.Get, "/aodb/acris/flights",
            headers: new Dictionary<string, string> { ["X-Api-Key"] = AcrisKey, ["If-Modified-Since"] = modified.ToString("R") });
        unchanged.StatusCode.Should().Be(HttpStatusCode.NotModified);
    }

    #region The emulators' Ariva client

    /// <summary>Ariva stand-in: the token exchange and the calls, recorded.</summary>
    private sealed class FakeAriva : HttpMessageHandler
    {
        public List<HttpRequestMessage> Calls { get; } = [];
        public List<string> Bodies { get; } = [];
        public HttpStatusCode DataStatus { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add(request);
            Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(ct));
            if (request.RequestUri!.AbsolutePath == "/api/v1/auth")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{{\"accessToken\":\"tok-{Calls.Count}\",\"expiresAt\":\"{DateTimeOffset.UtcNow.AddMinutes(15):O}\",\"sessionId\":\"s\"}}",
                        Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(DataStatus) { Content = new StringContent("{}") };
        }
    }

    /// <summary>A client without a base address, as when Simulation:Ariva:IntegrationUrl is empty.</summary>
    private sealed class FakeHttp : HttpClient;

    private static HttpClient Http(HttpMessageHandler handler) => new(handler, disposeHandler: false) { BaseAddress = new Uri("https://integration.test/") };

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task IntegrationClient_Should_ExchangeOnceKeepTheTokenAndSendTheCodeAndKey_When_Calling()
    {
        var ariva = new FakeAriva();
        var clock = new Clock();
        var client = new ArivaIntegrationClient("aman", () => Http(ariva), clock,
            NullLogger.Instance);
        (await client.SendAsync(HttpMethod.Post, "x", null, null, Ct)).Error.Should().Be("no client configured");
        var nowhere = new ArivaIntegrationClient("aodb", () => new FakeHttp(), clock, NullLogger.Instance);
        nowhere.Use(new IntegrationClientSettings { ClientId = ClientId, ClientSecret = ClientSecret, TotpSecret = AmanSeed });
        (await nowhere.SendAsync(HttpMethod.Post, "x", null, null, Ct)).Error.Should().StartWith("no Ariva address configured");
        client.Use(new IntegrationClientSettings { ClientId = ClientId, ClientSecret = ClientSecret, TotpSecret = AmanSeed, PerRequestTotp = true });

        var first = await client.SendAsync(HttpMethod.Post, "api/v1/integration/sites/DMO/immigration/desk-sessions", new StringContent("{}"), "key-1", Ct);
        var second = await client.SendAsync(HttpMethod.Post, "api/v1/integration/sites/DMO/immigration/desk-sessions", new StringContent("{}"), "key-2", Ct);

        first.Succeeded.Should().BeTrue();
        second.Succeeded.Should().BeTrue();
        ariva.Calls.Select(c => c.RequestUri!.AbsolutePath).Should().Equal("/api/v1/auth", "/api/v1/integration/sites/DMO/immigration/desk-sessions",
            "/api/v1/integration/sites/DMO/immigration/desk-sessions");
        JsonDocument.Parse(ariva.Bodies[0]).RootElement.GetProperty("totpCode").GetString().Should().Be(Totp.Code(Totp.FromBase32(AmanSeed), clock.Now));
        ariva.Calls[1].Headers.Authorization!.Parameter.Should().Be("tok-1");
        ariva.Calls[1].Headers.GetValues("X-TOTP-Code").Single().Should().MatchRegex("^[0-9]{6}$");
        ariva.Calls[2].Headers.GetValues("Idempotency-Key").Single().Should().Be("key-2");

        // A refused call drops the token; a new exchange waits for a newer TOTP step, as Ariva's replay guard requires.
        ariva.DataStatus = HttpStatusCode.Unauthorized;
        (await client.SendAsync(HttpMethod.Post, "y", new StringContent("{}"), null, Ct)).Status.Should().Be(401);
        (await client.SendAsync(HttpMethod.Post, "y", new StringContent("{}"), null, Ct)).Error.Should().Be("waiting for a new TOTP step");
        clock.Now = clock.Now.AddSeconds(Totp.StepSeconds);
        ariva.DataStatus = HttpStatusCode.OK;
        (await client.SendAsync(HttpMethod.Post, "y", new StringContent("{}"), null, Ct)).Succeeded.Should().BeTrue();
        ariva.Calls.Count(c => c.RequestUri!.AbsolutePath == "/api/v1/auth").Should().Be(2);
    }

    [Fact]
    public async Task BorderFeed_Should_SendEachContractAsABatchToTheImmigrationEndpoints_When_AMinutePlays()
    {
        var ariva = new FakeAriva();
        var clock = new Clock();
        var client = new ArivaIntegrationClient("aman", () => Http(ariva), clock,
            NullLogger.Instance);
        client.Use(new IntegrationClientSettings { ClientId = ClientId, ClientSecret = ClientSecret, TotpSecret = AmanSeed, PerRequestTotp = true });
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string> { ["Simulation:Seed"] = "9303" }).Build();
        var engine = new Ariva.Simulation.Api.Scenarios.ScenarioEngine(configuration, NullLogger<Ariva.Simulation.Api.Scenarios.ScenarioEngine>.Instance, clock);
        var buffer = new Ariva.Simulation.Api.Emulators.Aman.AmanFeedBuffer();
        var feed = new Ariva.Simulation.Api.Emulators.Aman.ImmigrationFeedEmulator("aman", engine, new FeedTime(),
            () => ("DMO", Ariva.Simulation.Api.Emulators.Aman.BorderSides.Both, 180), () => "DMO", client, kafka: null, buffer, NullLogger.Instance);
        var wall = new DateTime(2026, 10, 3, 18, 30, 12, DateTimeKind.Utc);

        await feed.PlayAsync(1110, _ => wall, Ct);
        await feed.PlayAsync(1110, _ => wall, Ct);

        var batches = ariva.Calls.Zip(ariva.Bodies).Where(c => c.First.RequestUri!.AbsolutePath != "/api/v1/auth").ToList();
        batches.Select(b => b.First.RequestUri!.AbsolutePath).Distinct().Should().BeEquivalentTo(
            Ariva.Simulation.Api.Emulators.Aman.AmanContracts.All.Select(k => "/api/v1/integration/sites/DMO/immigration/" + k));
        foreach (var (call, body) in batches)
        {
            call.Headers.GetValues("Idempotency-Key").Single().Should().MatchRegex("^sim-9303-20261003-1110-[a-z-]+$");
            call.Headers.Contains("X-TOTP-Code").Should().BeTrue();
            JsonDocument.Parse(body).RootElement.GetProperty("items").GetArrayLength().Should().BeGreaterThan(0);
        }

        batches.GroupBy(b => b.First.RequestUri!.AbsolutePath).Should().OnlyContain(g => g.Select(b => b.First.Headers.GetValues("Idempotency-Key").Single()).Distinct().Count() == 1 &&
            g.Select(b => b.Second).Distinct().Count() == 1, "a replayed minute is the same batch under the same key");
        var status = feed.Status();
        status.LastMinute.Should().Be(1110);
        status.Transports.Single(t => t.Transport == "rest").Failures.Should().Be(0);
        buffer.After(Ariva.Simulation.Api.Emulators.Aman.AmanContracts.DeskIntervalStats, 0, 500).Items.Should().NotBeEmpty();
    }

    [Fact]
    public void Totp_Should_MatchArivasOwnCodes_When_GivenTheSameSeed()
    {
        var seed = Ariva.Infra.Security.Totp.NewSecret();
        var base32 = Ariva.Infra.Security.Base32.Encode(seed);
        Totp.FromBase32(base32).Should().Equal(seed);
        Totp.FromBase32(base32.ToLowerInvariant()).Should().Equal(seed);
        for (var step = 59_000_000L; step < 59_000_010L; step++)
            Totp.Code(seed, step).Should().Be(Ariva.Infra.Security.Totp.Code(seed, step));
        Totp.Verify(seed, Ariva.Infra.Security.Totp.Code(seed, Totp.Step(DateTimeOffset.UtcNow) - 1), DateTimeOffset.UtcNow, out _).Should().BeTrue("one step either side");
        Totp.Verify(seed, Ariva.Infra.Security.Totp.Code(seed, Totp.Step(DateTimeOffset.UtcNow) - 3), DateTimeOffset.UtcNow, out _).Should().BeFalse();
        Totp.FromBase32("too-short!").Should().BeNull();
    }

    [Fact]
    public async Task Emulators_Should_PlayOnTheSensorClock_When_RegisteredAsSinks()
    {
        await using var app = Host();
        var sinks = app.Services.GetServices<IDemoMinuteSink>().ToList();
        sinks.Should().HaveCount(3);
        var sensor = app.Services.GetRequiredService<Ariva.Simulation.Api.Emulators.Sensors.SensorEmulator>();
        var aodb = app.Services.GetRequiredService<AodbEmulator>();

        sensor.Start(1110, 60, 1112, "test");
        await Task.Delay(TimeSpan.FromSeconds(3), Ct);

        aodb.Status().LastMinute.Should().Be(1111, "the sensor clock played 1110 and 1111 and stopped at 1112");
        app.Services.GetRequiredService<BorderFeeds>().Aman.Status().Records.Should().BeGreaterThan(0);
    }

    #endregion

    #region Review follow-ups (CWE-287, CWE-400, CWE-532)

    private sealed class Capture : Microsoft.Extensions.Logging.ILoggerProvider
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Lines { get; } = new();

        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger(Capture capture) : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull
            {
                capture.Lines.Enqueue("scope: " + state);
                return null;
            }

            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception exception,
                Func<TState, Exception, string> formatter) =>
                capture.Lines.Enqueue(formatter(state, exception) + " " + state + " " + exception);
        }
    }

    [Fact]
    public async Task Logs_Should_NeverHoldASecretKeyOrToken_When_TheEmulatorsAndMockPartnersAreUsed()
    {
        var capture = new Capture();
        await using var app = ArivaHosts.Create(ArivaHosts.Simulation, configure: builder =>
        {
            builder.UseSetting("Simulation:Control:Keys:0:Name", "demo-operator");
            builder.UseSetting("Simulation:Control:Keys:0:Sha256", Digest(ControlKey));
            builder.UseSetting("Simulation:Control:Keys:0:Scopes:0", "control");
            builder.UseSetting("Simulation:Aman:Mock:Clients:0:ClientId", "ariva-connector");
            builder.UseSetting("Simulation:Aman:Mock:Clients:0:SecretSha256", Digest(AmanSecret));
            builder.UseSetting("Simulation:Aman:Mock:Clients:0:TotpSecret", AmanSeed);
            builder.UseSetting("Simulation:Aman:Kafka:BootstrapServers", string.Empty);
            builder.UseSetting("Simulation:Aodb:AcrisKeySha256", Digest(AcrisKey));
            builder.ConfigureServices(services => services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(capture));
        });
        using var client = app.CreateClient();
        var credentials = $"{{\"clientId\":\"{ClientId}\",\"clientSecret\":\"{ClientSecret}\",\"totpSecret\":\"{AmanSeed}\",\"perRequestTotp\":true}}";

        using var put = await SendAsync(client, HttpMethod.Put, Route + "/clients", ControlKey, $"{{\"aman\":{credentials},\"aodb\":{credentials}}}");
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        using var play = await SendAsync(client, HttpMethod.Post, Route + "/play", ControlKey, "{\"minute\":1110}"); // Ariva is not running: refused calls
        using var failed = await SendAsync(client, HttpMethod.Post, "/aman/api/v1/auth", json: JsonSerializer.Serialize(new { clientId = "ariva-connector", clientSecret = "wrong", totpCode = Code() }));
        using var issued = await SendAsync(client, HttpMethod.Post, "/aman/api/v1/auth", json: JsonSerializer.Serialize(new { clientId = "ariva-connector", clientSecret = AmanSecret, totpCode = Code() }));
        var token = JsonDocument.Parse(await issued.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("accessToken").GetString();
        using var feed = await SendAsync(client, HttpMethod.Get, "/aman/api/v1/feed/desk-sessions", token, headers: new Dictionary<string, string> { ["X-TOTP-Code"] = Code() });
        using var wrongKey = await SendAsync(client, HttpMethod.Get, "/aodb/acris/flights", headers: new Dictionary<string, string> { ["X-Api-Key"] = "wrong-" + AcrisKey });
        using var rightKey = await SendAsync(client, HttpMethod.Get, "/aodb/acris/flights", headers: new Dictionary<string, string> { ["X-Api-Key"] = AcrisKey });
        using var tokenOnFeeds = await SendAsync(client, HttpMethod.Get, Route, token);

        // The outbound exchange refused with 401, logged by the emulators' client.
        var logger = capture.CreateLogger("outbound");
        var outbound = new ArivaIntegrationClient("aman", () => Http(new RefuseExchange()), TimeProvider.System, logger);
        outbound.Use(new IntegrationClientSettings { ClientId = ClientId, ClientSecret = ClientSecret, TotpSecret = AmanSeed, PerRequestTotp = true });
        (await outbound.SendAsync(HttpMethod.Post, "x", new StringContent("{}"), "k", Ct)).Status.Should().Be(401);

        feed.StatusCode.Should().Be(HttpStatusCode.OK);
        rightKey.StatusCode.Should().Be(HttpStatusCode.OK);
        tokenOnFeeds.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a mock AMAN token is not an operator key");
        capture.Lines.Should().NotBeEmpty();
        var everything = string.Join("\n", capture.Lines);
        foreach (var secret in new[] { ClientSecret, AmanSeed, AmanSecret, token, AcrisKey, ControlKey, Code() })
            everything.Should().NotContain(secret);
    }

    private sealed class RefuseExchange : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"error\":\"invalid_client\"}") });
    }

    private sealed class Monitor<T>(T value) : Microsoft.Extensions.Options.IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string name) => value;
        public IDisposable OnChange(Action<T, string> listener) => null;
    }

    [Fact]
    public void MockAmanToken_Should_BeRefused_When_ItHasExpired()
    {
        var clock = new Clock();
        var settings = new AmanEmulatorSettings { Mock = new MockAmanSettings { TokenMinutes = 15, Clients = [new MockAmanClient { ClientId = "ariva-connector",
            SecretSha256 = Digest(AmanSecret), TotpSecret = AmanSeed }] } };
        var tokens = new MockAmanTokens(new Monitor<AmanEmulatorSettings>(settings), clock);

        var issued = tokens.Exchange("ariva-connector", AmanSecret, Code(clock.Now));
        issued.Should().NotBeNull();
        clock.Now = clock.Now.AddMinutes(15).AddSeconds(-1);
        tokens.Validate(issued!.Value.Token).Should().NotBeNull();
        clock.Now = clock.Now.AddSeconds(1);
        tokens.Validate(issued.Value.Token).Should().BeNull("a token lives TokenMinutes");
        tokens.Validate("another-" + issued.Value.Token).Should().BeNull();
        tokens.Exchange("ariva-connector", AmanSecret, Code(clock.Now)).Should().NotBeNull("a newer step exchanges again");
    }

    [Fact]
    public async Task Acris_Should_AuthenticateNobody_When_NoKeyIsConfigured()
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Simulation, configure: builder => builder.UseSetting("Simulation:Aman:Kafka:BootstrapServers", string.Empty));
        using var client = app.CreateClient();
        foreach (var key in new[] { AcrisKey, "x", " ", Digest(string.Empty) })
        {
            using var response = await SendAsync(client, HttpMethod.Get, "/aodb/acris/flights", headers: new Dictionary<string, string> { ["X-Api-Key"] = key });
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, key);
        }
    }

    private sealed class BlockingSink : IDemoMinuteSink
    {
        public Task PlayAsync(int minute, Func<double, DateTime> wallOf, CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);
    }

    [Fact]
    public async Task FeedQueue_Should_DropTheOldestMinutes_When_AFeedFallsMoreThanItsCapacityBehind()
    {
        var clock = new Clock { Now = new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero) };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["Simulation:Seed"] = "9303" }).Build();
        var engine = new Ariva.Simulation.Api.Scenarios.ScenarioEngine(configuration, NullLogger<Ariva.Simulation.Api.Scenarios.ScenarioEngine>.Instance, clock);
        var emulator = new Ariva.Simulation.Api.Emulators.Sensors.SensorEmulator(engine, new NoHttp(),
            new Monitor<Ariva.Simulation.Api.Emulators.Sensors.SensorEmulatorSettings>(new Ariva.Simulation.Api.Emulators.Sensors.SensorEmulatorSettings()), clock,
            NullLogger<Ariva.Simulation.Api.Emulators.Sensors.SensorEmulator>.Instance, [new BlockingSink()]);

        emulator.Start(0, 60, 200, "test");
        clock.Now = clock.Now.AddMinutes(5); // 300 demo minutes at speed 60; the run stops at 200
        for (var i = 0; i < 5; i++)
            await emulator.TickAsync(Ct);

        emulator.Status().NextMinute.Should().Be(200);
        emulator.FeedMinutesDropped.Should().Be(200 - Ariva.Simulation.Api.Emulators.Sensors.SensorEmulator.FeedQueueCapacity,
            "the pump never ran, so all but the newest 120 minutes were dropped");
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => Http(new RefuseExchange());
    }

    #endregion
}
