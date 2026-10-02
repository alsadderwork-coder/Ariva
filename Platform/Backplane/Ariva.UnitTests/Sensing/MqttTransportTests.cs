using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Ariva.Api.Common.Settings;
using Ariva.Api.Ingest.Mqtt;
using Ariva.Core.Domain.Contracts;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Sensing;
using Ariva.Infra.Security;
using Ariva.Infra.Sensing;
using Ariva.Infra.Sensing.Declarative;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using MQTTnet.Server;
using ZiggyCreatures.Caching.Fusion;

namespace Ariva.UnitTests.Sensing;

/// <summary>
/// ARV-024, the MQTT transport: the packet size limit reads fixed headers as bytes arrive; the topic ACL takes only a
/// device's own topic; settings refuse clear text outside vm-local; the broker authenticates like an HTTPS push, only
/// for MQTT devices, with the device code as client id, rechecks the credential on every message, limits the rate and
/// closes on a storage failure; and end to end over TLS on loopback with the MQTTnet client.
/// </summary>
public sealed class MqttTransportTests
{
    private static readonly DeviceCredentials.Issued Lidar = DeviceCredentials.New();
    private static readonly DeviceCredentials.Issued Pusher = DeviceCredentials.New();
    private static readonly Guid LidarId = Guid.Parse("0199a000-0000-7000-8000-0000000d0024");

    private static DeviceCredentialRecord LidarRecord(string hash = null, IReadOnlyList<string> sources = null, string pin = null) =>
        new(LidarId, "L-24", "DMO", "Snake A", "Online", hash ?? Lidar.Hash, sources ?? [], pin, "Canonical", 20, 16, 0, null, "Mqtt");

    private sealed class Gateway : ISvcDeviceGateway
    {
        public static Func<string, DeviceCredentialRecord> Find { get; set; } = Default;

        public static DeviceCredentialRecord Default(string prefix) =>
            prefix == Lidar.Prefix ? LidarRecord()
            : prefix == Pusher.Prefix ? new DeviceCredentialRecord(Guid.NewGuid(), "H-25", "DMO", "Snake A", "Online", Pusher.Hash, [], null, "Canonical", 20, 16, 0)
            : null;

        public Task<DeviceCredentialRecord> FindByPrefixAsync(string prefix, CancellationToken ct = default) => Task.FromResult(Find(prefix));

        public Task<Fluentx.Result<DeviceZoneViewModel>> PublishedZoneAsync(string siteCode, string queueZoneName, CancellationToken ct = default) =>
            Task.FromResult(new Fluentx.Result<DeviceZoneViewModel>(new DeviceZoneViewModel(siteCode, queueZoneName, 1, new string('c', 64),
                [new(Guid.NewGuid(), "Snake A", "Queue", Guid.NewGuid(), null, null, "10 10,34 10,34 22,10 22", 288)],
                [new(Guid.NewGuid(), "Entry A", "Entry", null, Guid.NewGuid(), 10, 12, 10, 16, 4)])));
    }

    private sealed class Sink : ISensingSink
    {
        public bool Fail { get; set; }
        public List<IEvent> Published { get; } = [];

        public Task PublishAsync(IReadOnlyList<IEvent> events, CancellationToken ct = default)
        {
            if (Fail)
                throw new InvalidOperationException("broker down");
            lock (Published)
                Published.AddRange(events);
            return Task.CompletedTask;
        }
    }

    private static void AddIngest(IServiceCollection services, Sink sink)
    {
        services.AddScoped<ISvcDeviceGateway, Gateway>();
        services.AddSingleton<ISensingSink>(sink);
        services.AddSingleton<DeviceClockStore>();
        services.AddSingleton<IFusionCache>(new FusionCache(new FusionCacheOptions(), new MemoryCache(new MemoryCacheOptions())));
        services.AddSingleton(Options.Create(new IngestSettings()));
        services.AddSingleton(DeclarativeMappingCatalog.Embedded);
        services.AddScoped<SensingIngest>();
        services.AddSingleton(TimeProvider.System);
    }

    private static string Utc(DateTime time) => time.ToString("yyyy-MM-dd'T'HH':'mm':'ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static string Push()
    {
        var now = DateTime.UtcNow;
        return $$"""
            { "sentUtc": "{{Utc(now)}}",
              "intervals": [{ "lineName": "Entry A", "in": 3, "out": 0, "fromUtc": "{{Utc(now.AddMinutes(-5))}}", "toUtc": "{{Utc(now)}}" }] }
            """;
    }

    #region Packet limit and topics

    private static ReadOnlySequence<byte> Bytes(params byte[] bytes) => new(bytes);

    /// <summary>A CONNECT for MQTT 3.1.1 with a clean session and no will (remaining length 10, no payload).</summary>
    private static readonly byte[] Connect = [0x10, 10, 0, 4, (byte)'M', (byte)'Q', (byte)'T', (byte)'T', 4, 0x02, 0, 60];

    private static MqttPacketLimit Accepted(int max)
    {
        var limit = new MqttPacketLimit(max);
        limit.Feed(Bytes(Connect)).Should().BeTrue();
        limit.Accept();
        return limit;
    }

    [Fact]
    public void Limit_Should_FollowPacketsAcrossAnyChunking()
    {
        // CONNECT, PINGREQ (length 0), and a PUBLISH of 200 bytes (two-byte length 0xC8 0x01).
        var stream = new List<byte>(Connect);
        stream.AddRange([0xC0, 0x00, 0x30, 0xC8, 0x01]);
        stream.AddRange(new byte[200]);
        var whole = stream.ToArray();

        foreach (var chunk in new[] { 1, 2, 3, 7, 64, whole.Length })
        {
            var limit = new MqttPacketLimit(1_000);
            for (var i = 0; i < whole.Length; i += chunk)
                limit.Feed(Bytes(whole[i..Math.Min(whole.Length, i + chunk)])).Should().BeTrue($"chunks of {chunk}");
        }
    }

    [Fact]
    public void Limit_Should_RefuseALargePacketFromItsHeaderAlone()
    {
        Accepted(1_000).Feed(Bytes(0x30, 0xE8, 0x07)).Should().BeTrue("exactly 1000 is allowed");
        Accepted(1_000).Feed(Bytes(0x30, 0xE9, 0x07)).Should().BeFalse("1001 declared, nothing of the body sent yet");
        Accepted(MqttPacketLimit.MaxPacketBytes).Feed(Bytes(0x30, 0xFF, 0xFF, 0xFF, 0x7F)).Should().BeFalse("256 MB declared");
        Accepted(1_000).Feed(Bytes(0x30, 0x80, 0x80, 0x80, 0x80)).Should().BeFalse("a fifth length byte is malformed");

        var split = Accepted(1_000);
        split.Feed(Bytes(0x30, 0xE9)).Should().BeTrue();
        split.Feed(Bytes(0x07)).Should().BeFalse("the length is checked when its last byte arrives");
    }

    [Fact]
    public void Limit_Should_HoldAConnectionToSmallPacketsUntilTheBrokerAcceptsIt()
    {
        var nine = new byte[] { 0x30, 0xA8, 0x46 }; // a PUBLISH declaring 9,000 bytes
        var before = new MqttPacketLimit(MqttPacketLimit.MaxPacketBytes);
        before.Feed(Bytes(Connect)).Should().BeTrue();
        before.Feed(Bytes(nine)).Should().BeFalse("before the CONNECT is accepted packets are at most 8 KB");

        Accepted(MqttPacketLimit.MaxPacketBytes).Feed(Bytes(nine)).Should().BeTrue("after it, up to a full push");
    }

    [Fact]
    public void Limit_Should_RefuseAFirstPacketThatIsNotAPlainConnect()
    {
        new MqttPacketLimit(1_000).Feed(Bytes(0x30, 0x02, 0, 0)).Should().BeFalse("a PUBLISH before CONNECT");
        new MqttPacketLimit(1_000).Feed(Bytes(0xC0, 0x00)).Should().BeFalse("a PINGREQ before CONNECT");
        new MqttPacketLimit(1_000).Feed(Bytes(0x10, 0x00)).Should().BeFalse("an empty CONNECT");
        new MqttPacketLimit(1_000).Feed(Bytes(0x10, 0xC1, 0x40)).Should().BeFalse("a CONNECT over 8 KB");
        new MqttPacketLimit(1_000).Feed(Bytes(0x10, 10, 0, 9, 0, 0, 0, 0, 0, 0, 0, 0)).Should().BeFalse("not an MQTT protocol name");

        var will = (byte[])Connect.Clone();
        will[9] = 0x06; // clean session and will
        new MqttPacketLimit(1_000).Feed(Bytes(will)).Should().BeFalse("a will would be ingested after an unclean disconnect");
        var byteByByte = new MqttPacketLimit(1_000);
        will.Take(9).ToList().ForEach(b => byteByByte.Feed(Bytes(b)).Should().BeTrue());
        byteByByte.Feed(Bytes(will[9])).Should().BeFalse("the flags are checked however the bytes arrive");

        var v31 = new byte[] { 0x10, 12, 0, 6, (byte)'M', (byte)'Q', (byte)'I', (byte)'s', (byte)'d', (byte)'p', 3, 0x02, 0, 60 };
        new MqttPacketLimit(1_000).Feed(Bytes(v31)).Should().BeTrue("MQTT 3.1's protocol name");
    }

    [Theory]
    [InlineData("ariva/v1/devices/L-24/canonical", true, DeviceDialect.Canonical)]
    [InlineData("ariva/v1/devices/L-24/xovis", true, DeviceDialect.Xovis)]
    [InlineData("ariva/v1/devices/L-24/declarative", true, DeviceDialect.Declarative)]
    [InlineData("ariva/v1/devices/L-25/canonical", false, default(DeviceDialect))]
    [InlineData("ariva/v1/devices/l-24/canonical", false, default(DeviceDialect))]
    [InlineData("ariva/v1/devices/L-24/Canonical", false, default(DeviceDialect))]
    [InlineData("ariva/v1/devices/L-24/canonical/extra", false, default(DeviceDialect))]
    [InlineData("ariva/v1/devices/L-24", false, default(DeviceDialect))]
    [InlineData("ariva/v1/devices/+/canonical", false, default(DeviceDialect))]
    [InlineData("ariva/v1/devices/#", false, default(DeviceDialect))]
    [InlineData("/ariva/v1/devices/L-24/canonical", false, default(DeviceDialect))]
    [InlineData("ariva/v2/devices/L-24/canonical", false, default(DeviceDialect))]
    [InlineData("$SYS/broker", false, default(DeviceDialect))]
    [InlineData(null, false, default(DeviceDialect))]
    public void Topic_Should_BeTheDevicesOwn(string topic, bool ok, DeviceDialect dialect)
    {
        MqttDeviceBroker.TryDialect(topic, "L-24", out var parsed).Should().Be(ok);
        if (ok)
            parsed.Should().Be(dialect);
    }

    [Fact]
    public void Settings_Should_RefuseClearTextOutsideLocalDevelopment()
    {
        new MqttSettings { Enabled = true, RequireTls = false }.Problems("k8s-prd").Should().ContainSingle().Which.Should().Contain("only in vm-local");
        new MqttSettings { Enabled = true, RequireTls = false }.Problems("vm-local").Should().BeEmpty();
        new MqttSettings { Enabled = true }.Problems("k8s-dev").Should().ContainSingle().Which.Should().Contain("CertificatePath");
        new MqttSettings { Enabled = true, Port = 0, CertificatePath = "a", CertificateKeyPath = "b" }.Problems("k8s-dev").Should().ContainSingle().Which.Should().Contain("Port");
        new MqttSettings { Enabled = false, RequireTls = false }.Problems("k8s-prd").Should().BeEmpty("nothing is checked while off");
    }

    [Fact]
    public void HttpEndpoints_Should_KeepWhatTheHostWouldHaveBound()
    {
        static IConfiguration Config(params (string Key, string Value)[] values) =>
            new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string>(v.Key, v.Value))).Build();

        MqttExtensions.HttpEndpoints(Config(("urls", "http://127.0.0.1:51901;https://0.0.0.0:443"))).Should().Equal(new IPEndPoint(IPAddress.Loopback, 51901));
        MqttExtensions.HttpEndpoints(Config(("urls", "http://+:8080"))).Should().Equal(new IPEndPoint(IPAddress.Any, 8080));
        MqttExtensions.HttpEndpoints(Config(("http_ports", "8080;8081"))).Should().Equal(new IPEndPoint(IPAddress.Any, 8080), new IPEndPoint(IPAddress.Any, 8081));
        MqttExtensions.HttpEndpoints(Config(("Application:BindingPort", "51002"))).Should().Equal(new IPEndPoint(IPAddress.Any, 51002));
        MqttExtensions.HttpEndpoints(Config()).Should().Equal(new IPEndPoint(IPAddress.Any, 8080));
    }

    #endregion

    #region Broker logic

    private static (MqttDeviceBroker Broker, Sink Sink) Broker(int devicePermits = 600, int connectsPerMinute = 30, bool requireTls = true)
    {
        var sink = new Sink();
        var services = new ServiceCollection();
        services.AddLogging();
        AddIngest(services, sink);
        var provider = services.BuildServiceProvider();
        var server = new MqttServerFactory().CreateMqttServer(new MqttServerOptionsBuilder().Build());
        var broker = new MqttDeviceBroker(server, provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new MqttSettings { Enabled = true, RequireTls = requireTls, ConnectsPerAddressPerMinute = connectsPerMinute }),
            Options.Create(new RateLimitingSettings { Device = new FixedWindowSettings { PermitLimit = devicePermits, WindowSeconds = 60 } }),
            TimeProvider.System, provider.GetRequiredService<ILogger<MqttDeviceBroker>>());
        return (broker, sink);
    }

    private static readonly IPAddress Address = IPAddress.Parse("10.20.0.7");
    private static readonly MqttDeviceBroker.Origin Here = MqttDeviceBroker.Of(Address, null);

    [Fact]
    public async Task Connect_Should_AuthenticateLikeAPush()
    {
        Gateway.Find = Gateway.Default;
        var (broker, _) = Broker();
        var ct = TestContext.Current.CancellationToken;

        var ok = await broker.AuthenticateAsync("", "L-24", Lidar.Credential, Address, null, true, ct);
        ok.Code.Should().Be(MqttConnectReasonCode.Success);
        ok.AssignedClientId.Should().Be("L-24", "an empty client id becomes the device code");
        (await broker.AuthenticateAsync("L-24", "L-24", Lidar.Credential, Address, null, true, ct)).AssignedClientId.Should().BeNull();

        (await broker.AuthenticateAsync("L-25", "L-24", Lidar.Credential, Address, null, true, ct)).Code.Should().Be(MqttConnectReasonCode.ClientIdentifierNotValid,
            "a device cannot take over another device's session");
        (await broker.AuthenticateAsync("", "L-25", Lidar.Credential, Address, null, true, ct)).Code.Should().Be(MqttConnectReasonCode.BadUserNameOrPassword);
        (await broker.AuthenticateAsync("", "L-24", Pusher.Credential, Address, null, true, ct)).Code.Should().Be(MqttConnectReasonCode.BadUserNameOrPassword);
        (await broker.AuthenticateAsync("", "L-24", Lidar.Credential[..^1] + (Lidar.Credential[^1] == 'A' ? "B" : "A"), Address, null, true, ct)).Code.Should().Be(MqttConnectReasonCode.BadUserNameOrPassword);
        (await broker.AuthenticateAsync("", "L-24", "", Address, null, true, ct)).Code.Should().Be(MqttConnectReasonCode.BadUserNameOrPassword);
        (await broker.AuthenticateAsync("", "H-25", Pusher.Credential, Address, null, true, ct)).Code.Should().Be(MqttConnectReasonCode.NotAuthorized,
            "a device registered for HTTPS push cannot use MQTT");
        (await broker.AuthenticateAsync("", "L-24", Lidar.Credential, Address, null, false, ct)).Code.Should().Be(MqttConnectReasonCode.NotAuthorized, "TLS is required");
        (await broker.AuthenticateAsync("", "L-24", Lidar.Credential, Address, null, true, ct, assignable: false)).Code.Should().Be(MqttConnectReasonCode.ClientIdentifierNotValid,
            "MQTT 3.1.1 cannot be assigned a client id, so it must send the device code");
    }

    [Fact]
    public async Task Connect_Should_HonourNetworksAndThePinnedCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var certificate = new CertificateRequest("CN=L-24", key, HashAlgorithmName.SHA256).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var pin = Convert.ToHexStringLower(SHA256.HashData(certificate.RawData));
        Gateway.Find = p => p == Lidar.Prefix ? LidarRecord(sources: ["10.20.0.0/24"], pin: pin) : null;
        try
        {
            var (broker, _) = Broker();
            var ct = TestContext.Current.CancellationToken;

            (await broker.AuthenticateAsync("", "L-24", Lidar.Credential, Address, certificate, true, ct)).Code.Should().Be(MqttConnectReasonCode.Success);
            (await broker.AuthenticateAsync("", "L-24", Lidar.Credential, IPAddress.Parse("10.20.1.7"), certificate, true, ct)).Code.Should().Be(MqttConnectReasonCode.BadUserNameOrPassword);
            (await broker.AuthenticateAsync("", "L-24", Lidar.Credential, Address, null, true, ct)).Code.Should().Be(MqttConnectReasonCode.BadUserNameOrPassword);
        }
        finally
        {
            Gateway.Find = Gateway.Default;
        }
    }

    [Fact]
    public async Task Connect_Should_LimitAttemptsPerAddress()
    {
        var (broker, _) = Broker(connectsPerMinute: 3);
        var ct = TestContext.Current.CancellationToken;

        for (var i = 0; i < 3; i++)
            (await broker.AuthenticateAsync("", "L-24", "ardk_wrong", Address, null, true, ct)).Code.Should().Be(MqttConnectReasonCode.BadUserNameOrPassword);
        (await broker.AuthenticateAsync("", "L-24", Lidar.Credential, Address, null, true, ct)).Code.Should().Be(MqttConnectReasonCode.ConnectionRateExceeded,
            "the limit applies before any credential is looked at");
        (await broker.AuthenticateAsync("", "L-24", Lidar.Credential, IPAddress.Parse("10.20.0.8"), null, true, ct)).Code.Should().Be(MqttConnectReasonCode.Success);
    }

    [Fact]
    public async Task Publish_Should_IngestOnlyTheDevicesOwnTopic()
    {
        Gateway.Find = Gateway.Default;
        var (broker, sink) = Broker(devicePermits: 3);
        var ct = TestContext.Current.CancellationToken;
        var device = LidarRecord();
        ReadOnlySequence<byte> Body(string text) => new(Encoding.UTF8.GetBytes(text));

        var ok = await broker.PublishAsync(device, Lidar.Prefix, "ariva/v1/devices/L-24/canonical", Body(Push()), ct, Here);
        (ok.Code, ok.Close, ok.Device.Code).Should().Be((MqttPubAckReasonCode.Success, false, "L-24"));
        sink.Published.Should().NotBeEmpty();

        var malformed = await broker.PublishAsync(device, Lidar.Prefix, "ariva/v1/devices/L-24/canonical", Body("{\"tracks\":"), ct, Here);
        (malformed.Code, malformed.Close).Should().Be((MqttPubAckReasonCode.PayloadFormatInvalid, false));
        malformed.Reason.Should().Be("The body is not well-formed JSON.");
        var dialect = await broker.PublishAsync(device, Lidar.Prefix, "ariva/v1/devices/L-24/xovis", Body(Push()), ct, Here);
        dialect.Reason.Should().Contain("Canonical dialect");

        var limited = await broker.PublishAsync(device, Lidar.Prefix, "ariva/v1/devices/L-24/canonical", Body(Push()), ct, Here);
        (limited.Code, limited.Close).Should().Be((MqttPubAckReasonCode.QuotaExceeded, false), "the fourth message in the window is over the device limit");

        (await broker.PublishAsync(device, Lidar.Prefix, "ariva/v1/devices/H-25/canonical", Body(Push()), ct, Here)).Close.Should().BeTrue("another device's topic closes the connection");
        (await broker.PublishAsync(null, null, "ariva/v1/devices/L-24/canonical", Body(Push()), ct)).Close.Should().BeTrue("no authenticated session");
    }

    [Fact]
    public async Task Publish_Should_Close_When_TheCredentialChangedOrTheEventsCannotBeStored()
    {
        Gateway.Find = Gateway.Default;
        var (broker, sink) = Broker();
        var ct = TestContext.Current.CancellationToken;
        var payload = new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(Push()));

        var stale = await broker.PublishAsync(LidarRecord(hash: new string('f', 64)), Lidar.Prefix, "ariva/v1/devices/L-24/canonical", payload, ct, Here);
        stale.Close.Should().BeTrue("the credential was rotated since the connection was made");

        Gateway.Find = _ => null;
        try
        {
            (await broker.PublishAsync(LidarRecord(), Lidar.Prefix, "ariva/v1/devices/L-24/canonical", payload, ct, Here)).Close.Should().BeTrue("the device was retired");
        }
        finally
        {
            Gateway.Find = Gateway.Default;
        }

        Gateway.Find = p => p == Lidar.Prefix ? LidarRecord(sources: ["10.30.0.0/16"]) : null;
        try
        {
            (await broker.PublishAsync(LidarRecord(), Lidar.Prefix, "ariva/v1/devices/L-24/canonical", payload, ct, Here)).Close.Should().BeTrue(
                "the device's networks were narrowed and no longer hold the connection's address");
            Gateway.Find = p => p == Lidar.Prefix ? LidarRecord(pin: new string('a', 64)) : null;
            (await broker.PublishAsync(LidarRecord(), Lidar.Prefix, "ariva/v1/devices/L-24/canonical", payload, ct, Here)).Close.Should().BeTrue(
                "a certificate was pinned that the connection did not present");
            Gateway.Find = p => p == Lidar.Prefix ? LidarRecord(sources: ["10.20.0.0/24"]) : null;
            (await broker.PublishAsync(LidarRecord(), Lidar.Prefix, "ariva/v1/devices/L-24/canonical", payload, ct, Here)).Close.Should().BeFalse();
            (await broker.PublishAsync(LidarRecord(), Lidar.Prefix, "ariva/v1/devices/L-24/canonical", payload, ct)).Close.Should().BeTrue("no recorded origin");
        }
        finally
        {
            Gateway.Find = Gateway.Default;
        }

        sink.Fail = true;
        var down = await broker.PublishAsync(LidarRecord(), Lidar.Prefix, "ariva/v1/devices/L-24/canonical", payload, ct, Here);
        (down.Code, down.Close).Should().Be((MqttPubAckReasonCode.ImplementationSpecificError, true), "no acknowledgement, so the device sends it again");
    }

    #endregion

    #region End to end over TLS

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<(WebApplication App, Sink Sink, int Port)> StartAsync()
    {
        var directory = Directory.CreateTempSubdirectory("ariva-mqtt-");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var certPath = Path.Combine(directory.FullName, "tls.crt");
        var keyPath = Path.Combine(directory.FullName, "tls.key");
        await File.WriteAllTextAsync(certPath, certificate.ExportCertificatePem());
        await File.WriteAllTextAsync(keyPath, key.ExportPkcs8PrivateKeyPem());

        var port = FreePort();
        var sink = new Sink();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "k8s-dev", ContentRootPath = directory.FullName });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string>
        {
            ["urls"] = "http://127.0.0.1:" + FreePort(),
            ["Ingest:Mqtt:Enabled"] = "true",
            ["Ingest:Mqtt:Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Ingest:Mqtt:CertificatePath"] = certPath,
            ["Ingest:Mqtt:CertificateKeyPath"] = keyPath,
            ["Ingest:Mqtt:ConnectTimeoutSeconds"] = "2",
            ["Ingest:Mqtt:MaxConnectionsPerAddress"] = "4"
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        AddIngest(builder.Services, sink);
        builder.Services.AddOptions<RateLimitingSettings>();
        builder.Services.AddArivaMqttTransport(builder.Configuration, "k8s-dev");
        builder.WebHost.UseArivaMqttListener();
        var app = builder.Build();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return (app, sink, port);
    }

    private static MqttClientOptions Client(int port, string user, string password, string clientId = "", MqttProtocolVersion version = MqttProtocolVersion.V500, bool tls = true)
    {
        var options = new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", port).WithCredentials(user, password).WithClientId(clientId)
            .WithProtocolVersion(version).WithTimeout(TimeSpan.FromSeconds(5)).WithCleanSession();
        if (tls)
            options.WithTlsOptions(o => o.UseTls().WithCertificateValidationHandler(_ => true));
        return options.Build();
    }

    [Fact]
    public async Task Broker_Should_TakeDeviceDataOverTlsAndNothingElse()
    {
        Gateway.Find = Gateway.Default;
        var (app, sink, port) = await StartAsync();
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;
        var factory = new MqttClientFactory();

        using var client = factory.CreateMqttClient();
        var connected = await client.ConnectAsync(Client(port, "L-24", Lidar.Credential), ct);
        connected.ResultCode.Should().Be(MqttClientConnectResultCode.Success);
        connected.AssignedClientIdentifier.Should().Be("L-24");

        var published = await client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic("ariva/v1/devices/L-24/canonical").WithPayload(Push())
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(), ct);
        published.ReasonCode.Should().Be(MqttClientPublishReasonCode.Success);
        sink.Published.OfType<Ariva.Core.Sensing.IntervalCountBatch>().Should().ContainSingle().Which.DeviceCode.Should().Be("L-24");

        var malformed = await client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic("ariva/v1/devices/L-24/canonical").WithPayload("{")
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(), ct);
        malformed.ReasonCode.Should().Be(MqttClientPublishReasonCode.PayloadFormatInvalid);

        var subscribed = await client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter("#").Build(), ct);
        subscribed.Items.Should().ContainSingle().Which.ResultCode.Should().Be(MqttClientSubscribeResultCode.NotAuthorized);

        await client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic("ariva/v1/devices/H-25/canonical").WithPayload(Push()).Build(), ct);
        await WaitUntilAsync(() => !client.IsConnected, ct);
        client.IsConnected.Should().BeFalse("publishing to another device's topic closes the connection");

        using var v311 = factory.CreateMqttClient();
        (await v311.ConnectAsync(Client(port, "L-24", Lidar.Credential, "L-24", MqttProtocolVersion.V311), ct)).ResultCode.Should().Be(MqttClientConnectResultCode.Success);
        (await v311.PublishAsync(new MqttApplicationMessageBuilder().WithTopic("ariva/v1/devices/L-24/canonical").WithPayload(Push())
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(), ct)).ReasonCode.Should().Be(MqttClientPublishReasonCode.Success);

        using var wrong = factory.CreateMqttClient();
        (await wrong.ConnectAsync(Client(port, "L-24", Pusher.Credential), ct)).ResultCode.Should().Be(MqttClientConnectResultCode.BadUserNameOrPassword);
        using var other = factory.CreateMqttClient();
        (await other.ConnectAsync(Client(port, "L-24", Lidar.Credential, "someone-else"), ct)).ResultCode.Should().Be(MqttClientConnectResultCode.ClientIdentifierNotValid);

        using var clear = factory.CreateMqttClient();
        var clearText = async () => await clear.ConnectAsync(Client(port, "L-24", Lidar.Credential, tls: false), ct);
        await clearText.Should().ThrowAsync<Exception>("the listener speaks TLS only");
    }

    [Fact]
    public async Task Broker_Should_DropAConnection_When_APacketIsOverTheLimit()
    {
        Gateway.Find = Gateway.Default;
        var (app, sink, port) = await StartAsync();
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;
        using var client = new MqttClientFactory().CreateMqttClient();
        (await client.ConnectAsync(Client(port, "L-24", Lidar.Credential), ct)).ResultCode.Should().Be(MqttClientConnectResultCode.Success);

        try
        {
            await client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic("ariva/v1/devices/L-24/canonical").WithPayload(new byte[MqttPacketLimit.MaxPacketBytes + 1])
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(), ct);
        }
        catch (Exception e) when (e is MQTTnet.Exceptions.MqttCommunicationException or OperationCanceledException or IOException)
        {
            // The connection may close while the packet is still being written.
        }

        await WaitUntilAsync(() => !client.IsConnected, ct);
        client.IsConnected.Should().BeFalse();
        sink.Published.Should().BeEmpty();
    }

    /// <summary>A TLS connection that sends no MQTT at all.</summary>
    private static async Task<(TcpClient Tcp, System.Net.Security.SslStream Tls)> SilentAsync(int port, CancellationToken ct)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port, ct);
        var tls = new System.Net.Security.SslStream(tcp.GetStream(), false, (_, certificate, _, _) => certificate?.Subject == "CN=localhost");
        await tls.AuthenticateAsClientAsync(new System.Net.Security.SslClientAuthenticationOptions { TargetHost = "localhost" }, ct);
        return (tcp, tls);
    }

    /// <summary>True when the server closes the connection within the time (a read returns nothing or fails).</summary>
    private static async Task<bool> ClosedByServerAsync(Stream stream, TimeSpan within)
    {
        using var timeout = new CancellationTokenSource(within);
        try
        {
            return await stream.ReadAsync(new byte[16], timeout.Token) == 0;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    [Fact]
    public async Task Broker_Should_CloseSilentAndRefusedConnections_And_CapThemPerAddress()
    {
        Gateway.Find = Gateway.Default;
        var (app, _, port) = await StartAsync();
        await using var __ = app;
        var ct = TestContext.Current.CancellationToken;

        var silent = new List<(TcpClient Tcp, System.Net.Security.SslStream Tls)>();
        for (var i = 0; i < 4; i++)
            silent.Add(await SilentAsync(port, ct));
        var fifth = async () =>
        {
            var (tcp, tls) = await SilentAsync(port, ct);
            using (tcp)
            await using (tls)
                (await ClosedByServerAsync(tls, TimeSpan.FromSeconds(1))).Should().BeTrue();
        };
        await fifth.Should().ThrowAsync<Exception>("a fifth connection from the address is dropped before TLS");

        foreach (var (tcp, tls) in silent)
        {
            (await ClosedByServerAsync(tls, TimeSpan.FromSeconds(6))).Should().BeTrue("a connection that sends no CONNECT is closed after the timeout");
            await tls.DisposeAsync();
            tcp.Dispose();
        }

        using var refused = new MqttClientFactory().CreateMqttClient();
        (await refused.ConnectAsync(Client(port, "L-24", Pusher.Credential), ct)).ResultCode.Should().Be(MqttClientConnectResultCode.BadUserNameOrPassword);
        using var will = new MqttClientFactory().CreateMqttClient();
        var willOptions = Client(port, "L-24", Lidar.Credential);
        willOptions.WillTopic = "ariva/v1/devices/L-24/canonical";
        willOptions.WillPayload = Encoding.UTF8.GetBytes(Push());
        var withWill = async () => await will.ConnectAsync(willOptions, ct);
        await withWill.Should().ThrowAsync<Exception>("a CONNECT with a will is closed before it reaches the broker");

        // Every slot was given back: four new connections from the address are accepted.
        var clients = new List<IMqttClient>();
        try
        {
            for (var i = 0; i < 4; i++)
            {
                var client = new MqttClientFactory().CreateMqttClient();
                clients.Add(client);
                (await client.ConnectAsync(Client(port, "L-24", Lidar.Credential, "L-24"), ct)).ResultCode.Should().Be(MqttClientConnectResultCode.Success);
            }
        }
        finally
        {
            clients.ForEach(c => c.Dispose());
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        for (var i = 0; i < 50 && !condition(); i++)
            await Task.Delay(100, ct);
    }

    #endregion
}
