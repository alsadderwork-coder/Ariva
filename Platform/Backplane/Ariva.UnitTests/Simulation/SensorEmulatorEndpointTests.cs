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
/// ARV-028, CWE-287: the sensor emulator's controls need an operator key with the control scope (a read key sees the
/// status only), every control is validated, device credentials go in and never come out, and an Ingest address in
/// clear text, a malformed credential or a sensor that does not exist stops the host at start.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class SensorEmulatorEndpointTests
{
    private const string Route = "/api/v1/simulation/sensors";
    private const string ReadKey = "sim-read-3f0a9c2e8b7d4e61a5c0f9b2d8e7a6c1";
    private const string ControlKey = "sim-ctl-7b1e4d9a2c6f8e03b5a7d1c9e4f2a8b6";
    private const string Credential = "ardk_BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB1";

    private static string Digest(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private static IArivaHost Host(Action<IWebHostBuilder> extra = null) =>
        ArivaHosts.Create(ArivaHosts.Simulation, configure: builder =>
        {
            builder.UseSetting("Simulation:Control:Keys:0:Name", "demo-reader");
            builder.UseSetting("Simulation:Control:Keys:0:Sha256", Digest(ReadKey));
            builder.UseSetting("Simulation:Control:Keys:0:Scopes:0", "read");
            builder.UseSetting("Simulation:Control:Keys:1:Name", "demo-operator");
            builder.UseSetting("Simulation:Control:Keys:1:Sha256", Digest(ControlKey));
            builder.UseSetting("Simulation:Control:Keys:1:Scopes:0", "control");
            extra?.Invoke(builder);
        });

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string key, string json = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (key is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<string> TextAsync(HttpResponseMessage response) => await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("POST", "/start", "{}")]
    [InlineData("POST", "/pause", null)]
    [InlineData("PUT", "/speed", "{\"speed\":2}")]
    [InlineData("POST", "/jump", "{\"minute\":10}")]
    [InlineData("PUT", "/devices", "{\"devices\":[]}")]
    public async Task Controls_Should_Return401WithoutAKeyAnd403ForAReadKey_When_Called(string method, string path, string json)
    {
        await using var app = Host();
        using var client = app.CreateClient();

        using var anonymous = await SendAsync(client, new HttpMethod(method), Route + path, null, json);
        using var reader = await SendAsync(client, new HttpMethod(method), Route + path, ReadKey, json);
        using var wrong = await SendAsync(client, new HttpMethod(method), Route + path, Credential, json);

        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        reader.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a device credential is not an operator key");
    }

    [Fact]
    public async Task Status_Should_BeReadable_When_TheKeyCanRead()
    {
        await using var app = Host();
        using var client = app.CreateClient();

        using var response = await SendAsync(client, HttpMethod.Get, Route, ReadKey);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonDocument.Parse(await TextAsync(response)).RootElement;
        body.GetProperty("running").GetBoolean().Should().BeFalse("the emulator waits for a start");
        body.GetProperty("ingestConfigured").GetBoolean().Should().BeTrue("vm-local points the emulator at the local Ingest");
    }

    [Fact]
    public async Task Devices_Should_NeverEchoACredential_When_Replaced()
    {
        await using var app = Host();
        using var client = app.CreateClient();

        using var put = await SendAsync(client, HttpMethod.Put, Route + "/devices", ControlKey,
            $"{{\"devices\":[{{\"sensor\":\"S-15\",\"dialect\":\"Canonical\",\"credential\":\"{Credential}\"}}]}}");
        using var get = await SendAsync(client, HttpMethod.Get, Route, ReadKey);

        put.StatusCode.Should().Be(HttpStatusCode.OK);
        (await TextAsync(put)).Should().NotContain("ardk_").And.Contain("S-15");
        var text = await TextAsync(get);
        text.Should().NotContain("ardk_");
        JsonDocument.Parse(text).RootElement.GetProperty("devices")[0].GetProperty("queueZone").GetString().Should().Be("A-VIS");
    }

    [Theory]
    [InlineData("/start", "POST", "{\"minute\":1440}")]
    [InlineData("/start", "POST", "{\"minute\":-1}")]
    [InlineData("/start", "POST", "{\"speed\":0.1}")]
    [InlineData("/start", "POST", "{\"speed\":61}")]
    [InlineData("/start", "POST", "{\"minute\":600,\"untilMinute\":600}")]
    [InlineData("/start", "POST", "{\"untilMinute\":1441}")]
    [InlineData("/speed", "PUT", "{}")]
    [InlineData("/speed", "PUT", "{\"speed\":1e308}")]
    [InlineData("/jump", "POST", "{}")]
    [InlineData("/jump", "POST", "{\"minute\":5000}")]
    [InlineData("/devices", "PUT", "{}")]
    [InlineData("/devices", "PUT", "{\"devices\":[{\"sensor\":\"S-99\",\"credential\":\"ardk_BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB1\"}]}")]
    [InlineData("/devices", "PUT", "{\"devices\":[{\"sensor\":\"S-15\",\"credential\":\"not-a-credential\"}]}")]
    [InlineData("/devices", "PUT", "{\"devices\":[{\"sensor\":\"S-15\",\"dialect\":\"Bogus\",\"credential\":\"ardk_BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB1\"}]}")]
    public async Task Controls_Should_Return400_When_TheRequestIsInvalid(string path, string method, string json)
    {
        await using var app = Host();
        using var client = app.CreateClient();

        using var response = await SendAsync(client, new HttpMethod(method), Route + path, ControlKey, json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TextAsync(response)).Should().NotContain("ardk_B", "a refused credential is never quoted back");
    }

    [Fact]
    public async Task Controls_Should_StartPauseSpeedAndJump_When_TheKeyCanControl()
    {
        await using var app = Host();
        using var client = app.CreateClient();

        using var start = await SendAsync(client, HttpMethod.Post, Route + "/start", ControlKey, "{\"minute\":1080,\"speed\":10,\"untilMinute\":1090}");
        using var speed = await SendAsync(client, HttpMethod.Put, Route + "/speed", ControlKey, "{\"speed\":20}");
        using var jump = await SendAsync(client, HttpMethod.Post, Route + "/jump", ControlKey, "{\"minute\":1085}");
        using var pause = await SendAsync(client, HttpMethod.Post, Route + "/pause", ControlKey);

        start.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonDocument.Parse(await TextAsync(start)).RootElement.GetProperty("running").GetBoolean().Should().BeTrue();
        JsonDocument.Parse(await TextAsync(speed)).RootElement.GetProperty("speed").GetDouble().Should().Be(20);
        JsonDocument.Parse(await TextAsync(jump)).RootElement.GetProperty("nextMinute").GetInt32().Should().Be(1085);
        JsonDocument.Parse(await TextAsync(pause)).RootElement.GetProperty("running").GetBoolean().Should().BeFalse();
    }

    [Theory]
    [InlineData("Simulation:Sensors:IngestUrl", "http://ingest.example")]
    [InlineData("Simulation:Sensors:IngestUrl", "http://localhost:51002")]
    [InlineData("Simulation:Sensors:IngestUrl", "not a url")]
    [InlineData("Simulation:Sensors:Devices:0:Sensor", "S-99")]
    [InlineData("Simulation:Sensors:MaxSpeed", "500")]
    public async Task Start_Should_Fail_When_TheEmulatorConfigurationIsInvalid(string setting, string value)
    {
        await using var app = Host(builder =>
        {
            builder.UseSetting("Simulation:Sensors:Devices:0:Sensor", "S-15");
            builder.UseSetting("Simulation:Sensors:Devices:0:Credential", Credential);
            builder.UseSetting("Simulation:Sensors:AllowInsecureTransport", "false");
            builder.UseSetting(setting, value);
        });

        Action start = () => app.CreateClient();

        start.Should().Throw<Exception>("an invalid emulator configuration stops the simulator at start");
    }
}
