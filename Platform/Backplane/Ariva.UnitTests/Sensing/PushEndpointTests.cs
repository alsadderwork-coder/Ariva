using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Ariva.Core.Domain.Contracts;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Sensing;
using Ariva.Core.Services.Sensing;
using Ariva.Infra.Security;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ariva.UnitTests.Sensing;

/// <summary>
/// ARV-023 on the Ingest host in process: pushes for the device's own zone in its own dialect are 202 with the counts;
/// no key 401, another zone 403, another dialect 400, malformed 400, not JSON 415, over 256 KB 413 (with and without a
/// length), and 503 when the events cannot be stored.
/// </summary>
public sealed class PushEndpointTests
{
    private static readonly DeviceCredentials.Issued Canonical = DeviceCredentials.New();
    private static readonly DeviceCredentials.Issued Xovis = DeviceCredentials.New();
    private static readonly DeviceCredentials.Issued Ouster = DeviceCredentials.New();
    private static readonly DeviceCredentials.Issued OnMqtt = DeviceCredentials.New();

    private sealed class Gateway : ISvcDeviceGateway
    {
        public Task<DeviceCredentialRecord> FindByPrefixAsync(string prefix, CancellationToken ct = default) => Task.FromResult(
            prefix == Canonical.Prefix ? new DeviceCredentialRecord(Guid.NewGuid(), "S-17", "DMO", "Snake A", "Online", Canonical.Hash, [], null, "Canonical", 20, 16, 0)
            : prefix == Xovis.Prefix ? new DeviceCredentialRecord(Guid.NewGuid(), "S-18", "DMO", "Snake A", "Online", Xovis.Hash, [], null, "Xovis", 20, 16, 0)
            : prefix == OnMqtt.Prefix ? new DeviceCredentialRecord(Guid.NewGuid(), "M-30", "DMO", "Snake A", "Online", OnMqtt.Hash, [], null, "Canonical", 20, 16, 0, null, "Mqtt")
            : prefix == Ouster.Prefix ? new DeviceCredentialRecord(Guid.NewGuid(), "L-24", "DMO", "Snake A", "Online", Ouster.Hash, [], null, "Declarative", 20, 16, 90, "ouster-detect-v1")
            : null);

        public Task<Fluentx.Result<DeviceZoneViewModel>> PublishedZoneAsync(string siteCode, string queueZoneName, CancellationToken ct = default) =>
            Task.FromResult(new Fluentx.Result<DeviceZoneViewModel>(new DeviceZoneViewModel(siteCode, queueZoneName, 1, new string('c', 64),
                [new(Guid.NewGuid(), "Snake A", "Queue", Guid.NewGuid(), null, null, "10 10,34 10,34 22,10 22", 288)],
                [new(Guid.NewGuid(), "Entry A", "Entry", null, Guid.NewGuid(), 10, 12, 10, 16, 4), new(Guid.NewGuid(), "Exit A", "Exit", null, Guid.NewGuid(), 30, 22, 34, 22, 4)])));
    }

    private sealed class Sink : ISensingSink
    {
        public static bool Fail { get; set; }
        public static List<IEvent> Published { get; } = [];

        public Task PublishAsync(IReadOnlyList<IEvent> events, CancellationToken ct = default)
        {
            if (Fail)
                throw new InvalidOperationException("broker down");
            lock (Published)
                Published.AddRange(events);
            return Task.CompletedTask;
        }
    }

    private static IArivaHost Ingest() => ArivaHosts.Create(ArivaHosts.Ingest, configure: builder => builder.ConfigureTestServices(services =>
    {
        services.Replace(ServiceDescriptor.Scoped<ISvcDeviceGateway, Gateway>());
        services.Replace(ServiceDescriptor.Singleton<ISensingSink, Sink>());
        // The samples were sent at 14:05 UTC on 2 October 2026; Ariva receives them a second later.
        services.Replace(ServiceDescriptor.Singleton<TimeProvider>(new ManualClock(new DateTimeOffset(2026, 10, 2, 14, 5, 1, TimeSpan.Zero))));
    }));

    private static async Task<(HttpStatusCode Status, string Body)> PostAsync(HttpClient client, string path, string key, HttpContent content)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        if (key is not null)
            request.Headers.Add("X-Ariva-Device-Key", key);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    private const string Push = """
        { "sentUtc": "2026-10-02T14:05:00.900Z", "packageId": 1,
          "intervals": [{ "lineName": "Entry A", "in": 3, "out": 0, "fromUtc": "2026-10-02T14:00:00Z", "toUtc": "2026-10-02T14:05:00Z" }] }
        """;

    [Fact]
    public async Task Push_Should_AnswerByTheRules()
    {
        await using var host = Ingest();
        using var client = host.CreateClient();
        const string own = "/api/v1/ingest/zones/Snake%20A/events";

        var accepted = await PostAsync(client, own, Canonical.Credential, JsonBody(Push));
        accepted.Status.Should().Be(HttpStatusCode.Accepted, accepted.Body);
        accepted.Body.Should().Contain("\"accepted\":1");

        (await PostAsync(client, own, null, JsonBody(Push))).Status.Should().Be(HttpStatusCode.Unauthorized);
        (await PostAsync(client, "/api/v1/ingest/zones/Snake%20B/events", Canonical.Credential, JsonBody(Push))).Status.Should().Be(HttpStatusCode.Forbidden);
        var dialect = await PostAsync(client, "/api/v1/ingest/zones/Snake%20A/xovis", Canonical.Credential, JsonBody(Push));
        dialect.Status.Should().Be(HttpStatusCode.BadRequest);
        dialect.Body.Should().Contain("Canonical dialect");
        (await PostAsync(client, own, Canonical.Credential, JsonBody("{\"tracks\":"))).Status.Should().Be(HttpStatusCode.BadRequest);
        (await PostAsync(client, own, Canonical.Credential, new StringContent(Push, Encoding.UTF8, "text/plain"))).Status.Should().Be(HttpStatusCode.UnsupportedMediaType);

        var large = "{\"tracks\":[" + string.Join(',', Enumerable.Repeat("{\"trackId\":\"7\",\"x\":1,\"y\":1,\"timeUtc\":\"2026-10-02T14:05:00Z\"}", 5_000)) + "]}";
        (await PostAsync(client, own, Canonical.Credential, JsonBody(large))).Status.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        var chunked = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(large)));
        chunked.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        (await PostAsync(client, own, Canonical.Credential, chunked)).Status.Should().Be(HttpStatusCode.RequestEntityTooLarge, "a body without a declared length is limited too");
    }

    [Fact]
    public async Task Push_Should_AcceptAXovisSample()
    {
        await using var host = Ingest();
        using var client = host.CreateClient();
        var sample = File.ReadAllText(RepositoryPaths.Resolve("Platform/Backplane/Ariva.UnitTests/Sensing/Samples/xovis-logics-fw5.json"));

        var result = await PostAsync(client, "/api/v1/ingest/zones/Snake%20A/xovis", Xovis.Credential, JsonBody(sample));

        result.Status.Should().Be(HttpStatusCode.Accepted, result.Body);
        result.Body.Should().Contain("\"accepted\":3").And.Contain("\"ignored\":1");
        var test = await PostAsync(client, "/api/v1/ingest/zones/Snake%20A/xovis", Xovis.Credential,
            JsonBody(File.ReadAllText(RepositoryPaths.Resolve("Platform/Backplane/Ariva.UnitTests/Sensing/Samples/xovis-connection-test.json"))));
        test.Status.Should().Be(HttpStatusCode.Accepted, "a connection test is answered so the sensor marks the agent healthy");
    }

    [Fact]
    public async Task Push_Should_Answer503_When_TheEventsCannotBeStored()
    {
        await using var host = Ingest();
        using var client = host.CreateClient();
        Sink.Fail = true;
        try
        {
            var result = await PostAsync(client, "/api/v1/ingest/zones/Snake%20A/events", Canonical.Credential, JsonBody(Push.Replace("\"packageId\": 1", "\"packageId\": 2", StringComparison.Ordinal)));

            result.Status.Should().Be(HttpStatusCode.ServiceUnavailable);
            result.Body.Should().NotContain("broker down", "the cause is logged, not returned");
        }
        finally
        {
            Sink.Fail = false;
        }
    }

    [Fact]
    public async Task Push_Should_AcceptAnOusterSampleThroughTheDeclarativeEndpoint()
    {
        await using var host = Ingest();
        using var client = host.CreateClient();
        var sample = File.ReadAllText(RepositoryPaths.Resolve("Platform/Backplane/Ariva.UnitTests/Sensing/Samples/declarative/ouster-detect-v1/occupations.json"));

        var result = await PostAsync(client, "/api/v1/ingest/zones/Snake%20A/declarative", Ouster.Credential, JsonBody(sample));
        var wrong = await PostAsync(client, "/api/v1/ingest/zones/Snake%20A/declarative", Canonical.Credential, JsonBody(Push));

        result.Status.Should().Be(HttpStatusCode.Accepted, result.Body);
        result.Body.Should().Contain("\"accepted\":1").And.Contain("\"rejected\":1", "Overflow A is not in this zone's published geometry");
        wrong.Status.Should().Be(HttpStatusCode.BadRequest);
        wrong.Body.Should().Contain("Canonical dialect");

        var mqtt = await PostAsync(client, "/api/v1/ingest/zones/Snake%20A/events", OnMqtt.Credential, JsonBody(Push));
        mqtt.Status.Should().Be(HttpStatusCode.Forbidden, "a device registered on MQTT does not push over HTTPS");
        mqtt.Body.Should().Contain("Mqtt transport");
    }
}
