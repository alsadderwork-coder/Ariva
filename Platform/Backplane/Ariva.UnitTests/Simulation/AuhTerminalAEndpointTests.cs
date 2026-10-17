using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;

namespace Ariva.UnitTests.Simulation;

/// <summary>
/// ARV-139b, CWE-306, CWE-287: the simulator plays the AUH-TA scenario beside the reference one. The scenario endpoints
/// take a site (DMO when none is named) behind the same operator key scopes: a read key reads either site, only a control
/// key re-runs one, and a site the simulator does not play is 404 without being echoed. The sensor emulator takes AUH-TA
/// devices only as that site's sensors with an Ariva-issued credential (never weakened: the same credential rule as DMO's);
/// AUH-TA has its own AODB with its own integration client and ACRIS answer behind the AODB key; the seeds are configured
/// per site and an invalid one stops the host.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class AuhTerminalAEndpointTests
{
    private const string Route = "/api/v1/simulation/scenario";
    private const string ReadKey = "sim-read-3f0a9c2e8b7d4e61a5c0f9b2d8e7a6c1";
    private const string ControlKey = "sim-ctl-7b1e4d9a2c6f8e03b5a7d1c9e4f2a8b6";
    private const string AcrisKey = "acris-key-5f1e0c9b8a7d6e5f4a3b2c1d";
    private const string ClientId = "ic_ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string ClientSecret = "ics_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string TotpSeed = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";
    private const string Credential = "ardk_CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Digest(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private static IArivaHost Host(Action<IWebHostBuilder> extra = null) =>
        ArivaHosts.Create(ArivaHosts.Simulation, configure: builder =>
        {
            builder.UseSetting("Simulation:Control:Keys:0:Name", "demo-reader");
            builder.UseSetting("Simulation:Control:Keys:0:Sha256", Digest(ReadKey));
            builder.UseSetting("Simulation:Control:Keys:0:Scopes:0", "read");
            builder.UseSetting("Simulation:Control:Keys:1:Name", "demo-operator");
            builder.UseSetting("Simulation:Control:Keys:1:Sha256", Digest(ControlKey));
            builder.UseSetting("Simulation:Control:Keys:1:Scopes:0", "read");
            builder.UseSetting("Simulation:Control:Keys:1:Scopes:1", "control");
            builder.UseSetting("Simulation:Aman:Kafka:BootstrapServers", string.Empty);
            builder.UseSetting("Simulation:Aodb:AcrisKeySha256", Digest(AcrisKey));
            extra?.Invoke(builder);
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

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();

    #region Scenario endpoints

    [Fact]
    public async Task Get_Should_DescribeBothSites_When_TheKeyCanRead()
    {
        await using var app = Host();
        using var client = app.CreateClient();

        using var auh = await SendAsync(client, HttpMethod.Get, Route + "?site=AUH-TA", ReadKey);
        using var reference = await SendAsync(client, HttpMethod.Get, Route, ReadKey);
        using var sites = await SendAsync(client, HttpMethod.Get, Route + "/sites", ReadKey);

        auh.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await JsonAsync(auh);
        body.GetProperty("siteCode").GetString().Should().Be("AUH-TA");
        body.GetProperty("seed").GetUInt32().Should().Be(9304u);
        body.GetProperty("queues").GetInt32().Should().Be(8);
        body.GetProperty("sensors").GetInt32().Should().Be(84);
        body.GetProperty("departures").GetInt32().Should().Be(0);
        (await JsonAsync(reference)).GetProperty("siteCode").GetString().Should().Be("DMO", "no site means the reference site");
        (await JsonAsync(sites)).EnumerateArray().Select(s => s.GetProperty("siteCode").GetString()).Should().Equal("DMO", "AUH-TA");
    }

    [Fact]
    public async Task Get_Should_ReplayTheAuhEvening_When_AskedAtItsMinutes()
    {
        await using var app = Host();
        using var client = app.CreateClient();

        using var before = await SendAsync(client, HttpMethod.Get, Route + "/queues/A-VIS?minute=1091&site=AUH-TA", ReadKey);
        using var breach = await SendAsync(client, HttpMethod.Get, Route + "/queues/A-VIS?minute=1092&site=AUH-TA", ReadKey);
        using var sensors = await SendAsync(client, HttpMethod.Get, Route + "/sensors?minute=1110&site=AUH-TA", ReadKey);
        using var alerts = await SendAsync(client, HttpMethod.Get, Route + "/alerts?site=AUH-TA", ReadKey);
        using var queues = await SendAsync(client, HttpMethod.Get, Route + "/queues?minute=1158&site=AUH-TA", ReadKey);
        using var dmoQueue = await SendAsync(client, HttpMethod.Get, Route + "/queues/A-OV?minute=10&site=AUH-TA", ReadKey);

        (await JsonAsync(before)).GetProperty("nowcastMinutes").GetDouble().Should().BeLessThanOrEqualTo(15);
        (await JsonAsync(breach)).GetProperty("nowcastMinutes").GetDouble().Should().BeGreaterThan(15, "the AUH-TA Visitors nowcast passes 15 minutes at 18:12");
        var q = (await JsonAsync(sensors)).EnumerateArray().Single(s => s.GetProperty("sensor").GetString() == "Q-RES-04");
        q.GetProperty("offline").GetBoolean().Should().BeTrue("Q-RES-04 is offline from 18:25 to 18:35");
        q.GetProperty("queueZone").GetString().Should().Be("A-RES");
        (await JsonAsync(alerts)).EnumerateArray().Select(a => a.GetProperty("ruleId").GetString()).Should().Contain("R-002");
        var all = await JsonAsync(queues);
        all.GetArrayLength().Should().Be(8);
        all.EnumerateArray().Single(x => x.GetProperty("queue").GetString() == "A-EG").GetProperty("length").GetDouble().Should().BeGreaterThan(250);
        dmoQueue.StatusCode.Should().Be(HttpStatusCode.NotFound, "A-OV is a DMO band");
    }

    [Theory]
    [InlineData("AUH-TB")]
    [InlineData("auh-ta")]
    [InlineData("%27%20OR%201%3D1%2D%2D")]
    [InlineData("%3Cscript%3Ealert(1)%3C%2Fscript%3E")]
    [InlineData("AUH-TA-AUH-TA-AUH-TA-AUH-TA")]
    public async Task Get_Should_Return404WithoutEchoingIt_When_TheSiteIsNotPlayed(string site)
    {
        await using var app = Host();
        using var client = app.CreateClient();

        foreach (var path in new[] { $"?site={site}", $"/queues?minute=10&site={site}", $"/queues/A-VIS?minute=10&site={site}", $"/sensors?minute=10&site={site}", $"/alerts?site={site}" })
        {
            using var response = await SendAsync(client, HttpMethod.Get, Route + path, ReadKey);
            response.StatusCode.Should().Be(HttpStatusCode.NotFound, path);
            var text = await response.Content.ReadAsStringAsync(Ct);
            text.Should().NotContain(Uri.UnescapeDataString(site)).And.NotContain("script");
        }
    }

    [Fact]
    public async Task Put_Should_RerunOnlyTheNamedSite_When_TheKeyCanControl()
    {
        await using var app = Host();
        using var client = app.CreateClient();

        using var anonymous = await SendAsync(client, HttpMethod.Put, Route, null, "{\"seed\":42,\"site\":\"AUH-TA\"}");
        using var reader = await SendAsync(client, HttpMethod.Put, Route, ReadKey, "{\"seed\":42,\"site\":\"AUH-TA\"}");
        using var unknown = await SendAsync(client, HttpMethod.Put, Route, ControlKey, "{\"seed\":42,\"site\":\"XYZ\"}");
        using var oversized = await SendAsync(client, HttpMethod.Put, Route, ControlKey, "{\"seed\":42,\"site\":\"" + new string('A', 40) + "\"}");
        using var put = await SendAsync(client, HttpMethod.Put, Route, ControlKey, "{\"seed\":42,\"site\":\"AUH-TA\"}");
        using var auh = await SendAsync(client, HttpMethod.Get, Route + "?site=AUH-TA", ReadKey);
        using var reference = await SendAsync(client, HttpMethod.Get, Route, ReadKey);

        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        reader.StatusCode.Should().Be(HttpStatusCode.Forbidden, "re-running needs the control scope");
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await unknown.Content.ReadAsStringAsync(Ct)).Should().NotContain("XYZ");
        oversized.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await JsonAsync(auh);
        body.GetProperty("seed").GetUInt32().Should().Be(42u);
        body.GetProperty("runBy").GetString().Should().Be("demo-operator");
        (await JsonAsync(reference)).GetProperty("seed").GetUInt32().Should().Be(9303u, "a re-run of AUH-TA leaves DMO alone");
    }

    [Fact]
    public async Task Start_Should_TakeEachSitesSeed_When_Configured()
    {
        await using var app = Host(builder =>
        {
            builder.UseSetting("Simulation:Sites:AUH-TA:Seed", "7");
            builder.UseSetting("Simulation:Seed", "11");
        });
        using var client = app.CreateClient();

        using var auh = await SendAsync(client, HttpMethod.Get, Route + "?site=AUH-TA", ReadKey);
        using var reference = await SendAsync(client, HttpMethod.Get, Route, ReadKey);

        (await JsonAsync(auh)).GetProperty("seed").GetUInt32().Should().Be(7u);
        (await JsonAsync(reference)).GetProperty("seed").GetUInt32().Should().Be(11u, "Simulation:Seed stays the reference site's seed");
    }

    [Theory]
    [InlineData("Simulation:Sites:AUH-TA:Seed", "-1")]
    [InlineData("Simulation:Sites:AUH-TA:Seed", "4294967296")]
    [InlineData("Simulation:Sites:DMO:Seed", "-1")]
    public async Task Start_Should_Fail_When_ASitesSeedIsOutOfRange(string setting, string value)
    {
        await using var app = Host(builder => builder.UseSetting(setting, value));

        Action start = () => app.CreateClient();

        start.Should().Throw<Exception>("an invalid seed must stop the simulator at start");
    }

    #endregion

    #region Devices

    [Fact]
    public async Task Devices_Should_TakeAuhSensors_When_NamedWithTheirSite()
    {
        await using var app = Host();
        using var client = app.CreateClient();

        using var loaded = await SendAsync(client, HttpMethod.Put, "/api/v1/simulation/sensors/devices", ControlKey,
            $"{{\"devices\":[{{\"site\":\"AUH-TA\",\"sensor\":\"Q-VIS-01\",\"dialect\":\"Canonical\",\"credential\":\"{Credential}\"}},"
            + "{\"sensor\":\"S-15\",\"dialect\":\"Canonical\",\"credential\":\"ardk_CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC2\"}]}");

        loaded.StatusCode.Should().Be(HttpStatusCode.OK);
        var text = await loaded.Content.ReadAsStringAsync(Ct);
        text.Should().NotContain("ardk_", "credentials never come back out");
        var devices = JsonDocument.Parse(text).RootElement.GetProperty("devices").EnumerateArray().ToList();
        devices.Select(d => (d.GetProperty("site").GetString(), d.GetProperty("sensor").GetString(), d.GetProperty("queueZone").GetString())).Should()
            .Equal(("AUH-TA", "Q-VIS-01", "A-VIS"), ("DMO", "S-15", "A-VIS"));
    }

    [Theory]
    [InlineData("{\"site\":\"AUH-TA\",\"sensor\":\"S-15\"", "a DMO sensor is not an AUH-TA sensor")]
    [InlineData("{\"site\":\"DMO\",\"sensor\":\"Q-VIS-01\"", "an AUH-TA sensor is not a DMO sensor")]
    [InlineData("{\"sensor\":\"Q-VIS-01\"", "no site means DMO")]
    [InlineData("{\"site\":\"<b>X</b>\",\"sensor\":\"Q-VIS-01\"", "an unknown site")]
    [InlineData("{\"site\":\"AUH-TA\",\"sensor\":\"Q-VIS-99\"", "an unknown sensor")]
    public async Task Devices_Should_Return400WithoutEchoing_When_TheSiteOrSensorIsWrong(string device, string because)
    {
        await using var app = Host();
        using var client = app.CreateClient();

        using var response = await SendAsync(client, HttpMethod.Put, "/api/v1/simulation/sensors/devices", ControlKey,
            $"{{\"devices\":[{device},\"dialect\":\"Canonical\",\"credential\":\"{Credential}\"}}]}}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, because);
        var text = await response.Content.ReadAsStringAsync(Ct);
        text.Should().NotContain("<b>").And.NotContain("ardk_");
    }

    [Fact]
    public async Task Devices_Should_Return400_When_ACredentialIsNotOneAriva()
    {
        await using var app = Host();
        using var client = app.CreateClient();

        using var response = await SendAsync(client, HttpMethod.Put, "/api/v1/simulation/sensors/devices", ControlKey,
            "{\"devices\":[{\"site\":\"AUH-TA\",\"sensor\":\"Q-VIS-01\",\"dialect\":\"Canonical\",\"credential\":\"letmein\"}]}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a seeded AUH-TA sensor runs only with a credential Ariva issued (CWE-287)");
        (await response.Content.ReadAsStringAsync(Ct)).Should().NotContain("letmein");
    }

    [Fact]
    public async Task Devices_Should_Return400_When_MoreThanTheCapAreLoaded()
    {
        // CWE-120/400: the cap was raised to 150 for both sites' sensors (59 DMO and 84 AUH-TA); one more is refused.
        await using var app = Host();
        using var client = app.CreateClient();
        var device = $"{{\"site\":\"AUH-TA\",\"sensor\":\"Q-VIS-01\",\"dialect\":\"Canonical\",\"credential\":\"{Credential}\"}}";
        var body = "{\"devices\":[" + string.Join(',', Enumerable.Repeat(device, Ariva.Simulation.Api.Emulators.Sensors.SensorEmulatorSettings.MaxDevices + 1)) + "]}";

        using var response = await SendAsync(client, HttpMethod.Put, "/api/v1/simulation/sensors/devices", ControlKey, body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var text = await response.Content.ReadAsStringAsync(Ct);
        text.Should().Contain("At most 150").And.NotContain("ardk_");
        Ariva.Simulation.Api.Emulators.Sensors.SensorEmulatorSettings.MaxDevices.Should().BeGreaterThanOrEqualTo(59 + 84, "the cap fits every sensor of both sites");
    }

    #endregion

    #region The AUH-TA AODB

    [Fact]
    public async Task Clients_Should_TakeTheAuhAodbClientWithoutEchoingIt_When_Replaced()
    {
        await using var app = Host();
        using var client = app.CreateClient();
        var credentials = $"{{\"clientId\":\"{ClientId}\",\"clientSecret\":\"{ClientSecret}\",\"totpSecret\":\"{TotpSeed}\",\"perRequestTotp\":false}}";

        using var reader = await SendAsync(client, HttpMethod.Put, "/api/v1/simulation/feeds/clients", ReadKey, $"{{\"aodbSites\":{{\"AUH-TA\":{credentials}}}}}");
        using var unknown = await SendAsync(client, HttpMethod.Put, "/api/v1/simulation/feeds/clients", ControlKey, $"{{\"aodbSites\":{{\"<i>ZZ</i>\":{credentials}}}}}");
        using var reference = await SendAsync(client, HttpMethod.Put, "/api/v1/simulation/feeds/clients", ControlKey, $"{{\"aodbSites\":{{\"DMO\":{credentials}}}}}");
        using var bad = await SendAsync(client, HttpMethod.Put, "/api/v1/simulation/feeds/clients", ControlKey,
            "{\"aodbSites\":{\"AUH-TA\":{\"clientId\":\"ic_short\",\"clientSecret\":\"ics_leaked\",\"totpSecret\":\"x\"}}}");
        using var good = await SendAsync(client, HttpMethod.Put, "/api/v1/simulation/feeds/clients", ControlKey, $"{{\"aodbSites\":{{\"AUH-TA\":{credentials}}}}}");

        reader.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await unknown.Content.ReadAsStringAsync(Ct)).Should().NotContain("<i>");
        reference.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the reference site's AODB client is aodb");
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await bad.Content.ReadAsStringAsync(Ct)).Should().NotContain("leaked").And.NotContain("ic_short");
        good.StatusCode.Should().Be(HttpStatusCode.OK);
        var text = await good.Content.ReadAsStringAsync(Ct);
        text.Should().NotContain("ics_").And.NotContain(TotpSeed);
        var body = JsonDocument.Parse(text).RootElement;
        body.GetProperty("aodb").GetProperty("aidxConfigured").GetBoolean().Should().BeFalse("the reference AODB's client is left as it was");
        var auh = body.GetProperty("aodbSites").EnumerateArray().Single();
        auh.GetProperty("scenarioSite").GetString().Should().Be("AUH-TA");
        auh.GetProperty("arivaSite").GetString().Should().Be("AUH-TA");
        auh.GetProperty("airport").GetString().Should().Be("AUH");
        auh.GetProperty("aidxConfigured").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Acris_Should_AnswerTheAuhSchedule_When_PulledWithTheKey()
    {
        await using var app = Host();
        using var client = app.CreateClient();
        var key = new Dictionary<string, string> { ["X-Api-Key"] = AcrisKey };

        using var played = await SendAsync(client, HttpMethod.Post, "/api/v1/simulation/feeds/play", ControlKey, "{\"minute\":1100}");
        using var none = await SendAsync(client, HttpMethod.Get, "/aodb/sites/AUH-TA/acris/flights");
        using var unknown = await SendAsync(client, HttpMethod.Get, "/aodb/sites/XYZ/acris/flights", headers: key);
        using var auh = await SendAsync(client, HttpMethod.Get, "/aodb/sites/AUH-TA/acris/flights", headers: key);
        using var dmo = await SendAsync(client, HttpMethod.Get, "/aodb/sites/DMO/acris/flights", headers: key);
        using var legacy = await SendAsync(client, HttpMethod.Get, "/aodb/acris/flights", headers: key);

        played.StatusCode.Should().Be(HttpStatusCode.OK);
        none.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        auh.StatusCode.Should().Be(HttpStatusCode.OK);
        var flights = (await JsonAsync(auh)).EnumerateArray().ToList();
        flights.Should().NotBeEmpty().And.OnlyContain(f => f.GetProperty("arrivalAirport").GetString() == "AUH");
        flights.Should().OnlyContain(f => f.GetProperty("arrival").GetProperty("terminal").GetString() == "A");
        flights.Select(f => f.GetProperty("flightNumber").GetProperty("airlineCode").GetString()).Distinct().Should().BeSubsetOf(["HC", "RG", "LV"]);
        (await dmo.Content.ReadAsStringAsync(Ct)).Should().Be(await legacy.Content.ReadAsStringAsync(Ct), "the reference site's ACRIS is the same on both routes");
        var status = (await JsonAsync(played)).GetProperty("aodbSites").EnumerateArray().Single();
        status.GetProperty("lastMinute").GetInt32().Should().Be(1100);
        status.GetProperty("legs").GetInt32().Should().BePositive();
    }

    #endregion
}
