using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;

namespace Ariva.UnitTests.Simulation;

/// <summary>
/// ARV-027, CWE-306: the simulator's scenario endpoints need an operator key. No key, a wrong key, an oversized key or
/// a host with no keys configured answers 401; a read key reads the day and gets 403 on a re-run; a control key re-runs
/// it (rate limited per key); minutes, seeds and queues are validated; an invalid key configuration stops the host.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class ScenarioEndpointTests
{
    #region Fields

    private const string Route = "/api/v1/simulation/scenario";
    private const string ReadKey = "sim-read-3f0a9c2e8b7d4e61a5c0f9b2d8e7a6c1";
    private const string ControlKey = "sim-ctl-7b1e4d9a2c6f8e03b5a7d1c9e4f2a8b6";

    #endregion

    #region Helpers

    private static string Digest(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private static IArivaHost Host(int reruns = 6, bool keys = true, Action<IWebHostBuilder> extra = null) =>
        ArivaHosts.Create(ArivaHosts.Simulation, configure: builder =>
        {
            if (keys)
            {
                builder.UseSetting("Simulation:Control:Keys:0:Name", "demo-reader");
                builder.UseSetting("Simulation:Control:Keys:0:Sha256", Digest(ReadKey));
                builder.UseSetting("Simulation:Control:Keys:0:Scopes:0", "read");
                builder.UseSetting("Simulation:Control:Keys:1:Name", "demo-operator");
                builder.UseSetting("Simulation:Control:Keys:1:Sha256", Digest(ControlKey));
                builder.UseSetting("Simulation:Control:Keys:1:Scopes:0", "read");
                builder.UseSetting("Simulation:Control:Keys:1:Scopes:1", "control");
            }

            builder.UseSetting("Simulation:Control:RerunsPerMinute", reruns.ToString(System.Globalization.CultureInfo.InvariantCulture));
            extra?.Invoke(builder);
        });

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string key, object body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (key is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.Clone();

    #endregion

    #region Authentication

    [Theory]
    [InlineData(null)]
    [InlineData("sim-wrong-0000000000000000000000000000")]
    [InlineData("")]
    public async Task Get_Should_Return401_When_TheKeyIsMissingOrWrong(string key)
    {
        await using var app = Host();
        using var client = app.CreateClient();

        using var response = await SendAsync(client, HttpMethod.Get, Route, key);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().Should().Be("Bearer");
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().NotContain("sim-");
    }

    [Fact]
    public async Task Get_Should_Return401_When_TheKeyIsOversized()
    {
        await using var app = Host();
        using var client = app.CreateClient();

        using var response = await SendAsync(client, HttpMethod.Get, Route, ReadKey + new string('x', 300));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Get_Should_Return401_When_NoKeysAreConfigured()
    {
        await using var app = Host(keys: false);
        using var client = app.CreateClient();

        using var response = await SendAsync(client, HttpMethod.Get, Route, ReadKey);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "without provisioned keys nothing authenticates");
    }

    [Fact]
    public async Task Put_Should_Return403_When_TheKeyCanOnlyRead()
    {
        await using var app = Host();
        using var client = app.CreateClient();

        using var response = await SendAsync(client, HttpMethod.Put, Route, ReadKey, new { seed = 42 });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("Simulation:Control:Keys:0:Sha256", "not-a-digest")]
    [InlineData("Simulation:Control:Keys:0:Name", "bad name with spaces")]
    [InlineData("Simulation:Control:Keys:0:Scopes:0", "admin")]
    [InlineData("Simulation:Control:RerunsPerMinute", "0")]
    [InlineData("Simulation:Control:Keys:1:Sha256", "DUPLICATE")]
    [InlineData("Simulation:Seed", "-5")]
    [InlineData("Simulation:Seed", "4294967296")]
    public async Task Start_Should_Fail_When_TheKeyConfigurationIsInvalid(string setting, string value)
    {
        // DUPLICATE: the control key reuses the read key's digest, so the audit log could not tell them apart.
        var setValue = value == "DUPLICATE" ? Digest(ReadKey) : value;
        await using var app = Host(extra: builder => builder.UseSetting(setting, setValue));

        Action start = () => app.CreateClient();

        start.Should().Throw<Exception>("an invalid operator key or seed configuration must stop the simulator at start");
    }

    #endregion

    #region Reading the day

    [Fact]
    public async Task Get_Should_DescribeTheReferenceDay_When_TheKeyCanRead()
    {
        await using var app = Host();
        using var client = app.CreateClient();

        using var response = await SendAsync(client, HttpMethod.Get, Route, ReadKey);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl?.NoStore.Should().BeTrue("answers to an authenticated request are not cached");
        var body = await JsonAsync(response);
        body.GetProperty("seed").GetUInt32().Should().Be(9303u);
        body.GetProperty("date").GetString().Should().Be("2026-09-28");
        body.GetProperty("siteCode").GetString().Should().Be("DMO");
        body.GetProperty("sensors").GetInt32().Should().Be(59);
        body.GetProperty("alerts").GetInt32().Should().Be(7);
    }

    [Fact]
    public async Task Get_Should_ReplayTheScriptedEvening_When_AskedAtItsMinutes()
    {
        await using var app = Host();
        using var client = app.CreateClient();

        using var nowcast = await SendAsync(client, HttpMethod.Get, Route + "/queues/A-VIS?minute=1085", ReadKey);
        var visitors = await JsonAsync(nowcast);
        visitors.GetProperty("nowcastMinutes").GetDouble().Should().BeGreaterThan(15, "the Visitors nowcast passes 15 minutes at 18:05");

        using var sensors = await SendAsync(client, HttpMethod.Get, Route + "/sensors?minute=1105", ReadKey);
        var s17 = (await JsonAsync(sensors)).EnumerateArray().Single(s => s.GetProperty("sensor").GetString() == "S-17");
        s17.GetProperty("offline").GetBoolean().Should().BeTrue("S-17 is offline from 18:20 to 18:30");

        using var degraded = await SendAsync(client, HttpMethod.Get, Route + "/queues/A-VIS?minute=1105", ReadKey);
        (await JsonAsync(degraded)).GetProperty("degraded").GetBoolean().Should().BeTrue();

        using var alerts = await SendAsync(client, HttpMethod.Get, Route + "/alerts", ReadKey);
        var breach = (await JsonAsync(alerts)).EnumerateArray().Single(a => a.GetProperty("ruleId").GetString() == "R-004");
        breach.GetProperty("raisedAt").GetInt32().Should().Be(1150, "Handler B breaches from 19:10");

        using var all = await SendAsync(client, HttpMethod.Get, Route + "/queues?minute=1085", ReadKey);
        (await JsonAsync(all)).GetArrayLength().Should().Be(16, "16 queues: five per immigration side, two security checkpoints and four check-in islands");
    }

    [Theory]
    [InlineData(Route + "/queues?minute=1440")]
    [InlineData(Route + "/queues?minute=-1")]
    [InlineData(Route + "/queues")]
    [InlineData(Route + "/sensors?minute=99999")]
    [InlineData(Route + "/queues/A-VIS?minute=abc")]
    public async Task Get_Should_Return400_When_TheMinuteIsMissingOrOutsideTheDay(string path)
    {
        await using var app = Host();
        using var client = app.CreateClient();

        using var response = await SendAsync(client, HttpMethod.Get, path, ReadKey);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Get_Should_Return404_When_TheQueueDoesNotExist()
    {
        await using var app = Host();
        using var client = app.CreateClient();

        using var response = await SendAsync(client, HttpMethod.Get, Route + "/queues/NOPE?minute=10", ReadKey);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    #endregion

    #region Re-running the day

    [Fact]
    public async Task Put_Should_RerunTheDay_When_TheKeyCanControl()
    {
        await using var app = Host();
        using var client = app.CreateClient();

        using var put = await SendAsync(client, HttpMethod.Put, Route, ControlKey, new { seed = 42 });
        using var get = await SendAsync(client, HttpMethod.Get, Route, ReadKey);

        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await JsonAsync(get);
        body.GetProperty("seed").GetUInt32().Should().Be(42u);
        body.GetProperty("runBy").GetString().Should().Be("demo-operator");
    }

    [Theory]
    [InlineData("{\"seed\":-1}")]
    [InlineData("{\"seed\":4294967296}")]
    [InlineData("{}")]
    [InlineData("{\"Seed \":1}")]
    [InlineData("{\"seed\":1.5}")]
    [InlineData("{\"seed\":null}")]
    public async Task Put_Should_Return400_When_TheSeedIsMissingOrOutOfRange(string json)
    {
        await using var app = Host();
        using var client = app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, Route) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ControlKey);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        using var summary = await SendAsync(client, HttpMethod.Get, Route, ReadKey);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().NotContain("Ariva.Simulation", "binding errors do not name internal types");
        (await JsonAsync(summary)).GetProperty("seed").GetUInt32().Should().Be(9303u, "a refused re-run leaves the day alone");
    }

    [Fact]
    public async Task Put_Should_Return429_When_TheKeyRerunsTooOften()
    {
        await using var app = Host(reruns: 1);
        using var client = app.CreateClient();

        using var first = await SendAsync(client, HttpMethod.Put, Route, ControlKey, new { seed = 1 });
        using var second = await SendAsync(client, HttpMethod.Put, Route, ControlKey, new { seed = 2 });

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    #endregion
}
