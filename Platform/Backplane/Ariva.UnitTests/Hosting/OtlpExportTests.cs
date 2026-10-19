using System.Diagnostics;
using System.Diagnostics.Metrics;
using Ariva.ServiceDefaults;
using Ariva.UnitTests.Logging.Fixtures;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Ariva.UnitTests.Hosting;

/// <summary>
/// ARV-091a (ASVS 5.0 V16.4.3): the OTLP exporters of the service defaults reach the collector where OTLP says each signal
/// goes. Over http/protobuf the endpoint is a base address and each signal has its own path (<c>/v1/traces</c>,
/// <c>/v1/metrics</c>, <c>/v1/logs</c>), whether the endpoint comes from <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> or from
/// <c>Otlp:Endpoint</c>; over gRPC the endpoint is used as given (the gRPC method names the signal). Read back from a
/// loopback collector, with the collector's sender header on every request. The OTLP headers carry that credential, so outside
/// vm-local they come from the environment, never from a configuration file, and go over https only (as the Loki credentials);
/// no message names a value.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class OtlpExportTests : IDisposable
{
    private const string Source = "Ariva.UnitTests.OtlpProbe";
    private const string Header = "canary-otlp-header-77c2";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TimeSpan Patience => TimeSpan.FromSeconds(30);

    private readonly string _folder = Directory.CreateTempSubdirectory("ariva-otlp-").FullName;

    public static TheoryData<string> EndpointSettings => new() { "OTEL_EXPORTER_OTLP_ENDPOINT", "Otlp:Endpoint" };

    public static TheoryData<string> HeaderVariables => [.. OtlpSettings.HeaderVariables];

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    #region Signal paths

    [Theory]
    [MemberData(nameof(EndpointSettings))]
    public async Task Traces_Should_ReachTheTracesPath_When_TheProtocolIsHttpProtobuf(string endpointSetting)
    {
        await using var collector = new LoopbackCollector();
        await using var provider = Telemetry(collector, endpointSetting);
        var tracing = provider.GetRequiredService<TracerProvider>();

        using (var source = new ActivitySource(Source))
        using (source.StartActivity("probe"))
        {
        }

        tracing.ForceFlush(10_000);

        var request = await collector.FirstRequest.WaitAsync(Patience, Ct);
        request.RequestLine.Should().StartWith("POST /v1/traces ", "OTLP over HTTP takes the signal's path after the base endpoint");
        request.Headers.Should().ContainKey("x-collector-key").WhoseValue.Should().Be(Header);
    }

    [Theory]
    [MemberData(nameof(EndpointSettings))]
    public async Task Metrics_Should_ReachTheMetricsPath_When_TheProtocolIsHttpProtobuf(string endpointSetting)
    {
        await using var collector = new LoopbackCollector();
        await using var provider = Telemetry(collector, endpointSetting);
        var metrics = provider.GetRequiredService<MeterProvider>();

        using (var meter = new Meter(Source))
            meter.CreateCounter<long>("ariva.probe.count").Add(1);

        metrics.ForceFlush(10_000);

        var request = await collector.FirstRequest.WaitAsync(Patience, Ct);
        request.RequestLine.Should().StartWith("POST /v1/metrics ", "OTLP over HTTP takes the signal's path after the base endpoint");
        request.Headers.Should().ContainKey("x-collector-key").WhoseValue.Should().Be(Header);
    }

    [Theory]
    [MemberData(nameof(EndpointSettings))]
    public async Task Logs_Should_ReachTheLogsPath_When_AHostWithoutSerilogExportsThemOverHttpProtobuf(string endpointSetting)
    {
        await using var collector = new LoopbackCollector();
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "vm-local" });
        builder.Configuration.AddInMemoryCollection(Settings(collector, endpointSetting));
        builder.AddArivaServiceDefaults(options =>
        {
            options.ServiceName = "ariva-probe";
            options.Environment = "vm-local";
            options.ExportLogs = true;
        });

        using var host = builder.Build();
        await host.StartAsync(Ct);
        host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Probe").LogWarning("Probe line for the OTLP collector");
        host.Services.GetRequiredService<LoggerProvider>().ForceFlush(10_000);

        // The host's traces and metrics are exported at its shutdown too, so the logs' request is the one on the logs path.
        var logs = collector.RequestMatching(r => r.RequestLine.StartsWith("POST /v1/logs ", StringComparison.Ordinal));
        var arrived = await Task.WhenAny(logs, Task.Delay(Patience, Ct));

        arrived.Should().BeSameAs(logs, $"the simulator's OTLP logs take the logs path too (received: {string.Join("; ", collector.RequestLines)})");
        (await logs).Headers.Should().ContainKey("x-collector-key").WhoseValue.Should().Be(Header);
    }

    [Theory]
    [InlineData("traces")]
    [InlineData("metrics")]
    public async Task Exporter_Should_SpeakGrpcToTheEndpointAsGiven_When_TheProtocolIsGrpc(string signal)
    {
        await using var collector = new LoopbackCollector();
        var configuration = Configuration(new()
        {
            ["Application:Environment"] = "vm-local",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = collector.Address,
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "grpc"
        });
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        var options = new ArivaServiceDefaultsOptions { ServiceName = "ariva-probe", Environment = "vm-local" };
        options.ActivitySources.Add(Source);
        options.Meters.Add(Source);
        services.AddArivaTelemetry(configuration, options);
        await using var provider = services.BuildServiceProvider();

        if (signal == "traces")
        {
            var tracing = provider.GetRequiredService<TracerProvider>();
            using (var source = new ActivitySource(Source))
            using (source.StartActivity("probe"))
            {
            }

            tracing.ForceFlush(10_000);
        }
        else
        {
            var metrics = provider.GetRequiredService<MeterProvider>();
            using (var meter = new Meter(Source))
                meter.CreateCounter<long>("ariva.probe.count").Add(1);
            metrics.ForceFlush(10_000);
        }

        var request = await collector.FirstRequest.WaitAsync(Patience, Ct);
        request.RequestLine.Should().Be("PRI * HTTP/2.0", "gRPC opens HTTP/2 at the endpoint as given; the method, not a path Ariva adds, names the signal");
    }

    [Theory]
    [InlineData("grpc", "https://collector:4317", OtlpSettings.TracesPath, "https://collector:4317/")]
    [InlineData("grpc", "https://collector:4317/otlp", OtlpSettings.MetricsPath, "https://collector:4317/otlp")]
    [InlineData("http/protobuf", "https://collector:4318", OtlpSettings.TracesPath, "https://collector:4318/v1/traces")]
    [InlineData("http/protobuf", "https://collector:4318/", OtlpSettings.MetricsPath, "https://collector:4318/v1/metrics")]
    [InlineData("http/protobuf", "https://collector:4318/otlp/", OtlpSettings.LogsPath, "https://collector:4318/otlp/v1/logs")]
    public void EndpointFor_Should_AppendTheSignalsPathOverHttpOnly_When_TheEndpointIsABase(string protocol, string endpoint, string signal, string expected)
    {
        var otlp = OtlpSettings.From(Configuration(new() { ["OTEL_EXPORTER_OTLP_ENDPOINT"] = endpoint, ["OTEL_EXPORTER_OTLP_PROTOCOL"] = protocol }));

        otlp.EndpointFor(signal).Should().Be(new Uri(expected));
    }

    /// <summary>
    /// The header form the docs give (the OpenTelemetry specification's: comma-separated <c>key=value</c> pairs, values
    /// percent-encoded): every pair reaches the collector, each value decoded.
    /// </summary>
    [Theory]
    [InlineData("traces")]
    [InlineData("metrics")]
    [InlineData("Serilog logs")]
    public async Task Exporter_Should_DeliverEachHeaderDecoded_When_TheVariableIsPercentEncodedAsTheSpecificationSays(string exporter)
    {
        await using var collector = new LoopbackCollector();
        var settings = new Dictionary<string, string>
        {
            ["Application:Environment"] = "vm-local",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = collector.Address,
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
            ["OTEL_EXPORTER_OTLP_HEADERS"] = "Authorization=Bearer%20test-token,x-collector-key=second-header"
        };
        var configuration = Configuration(settings);

        if (exporter == "Serilog logs")
        {
            using var logger = Ariva.Api.Common.Logging.ArivaLogging.Configure(new Serilog.LoggerConfiguration(), configuration, "vm-local").CreateLogger();
            logger.Information("Probe line for the OTLP collector");
        }
        else
        {
            var services = new ServiceCollection();
            services.AddSingleton(configuration);
            var options = new ArivaServiceDefaultsOptions { ServiceName = "ariva-probe", Environment = "vm-local" };
            options.ActivitySources.Add(Source);
            options.Meters.Add(Source);
            services.AddArivaTelemetry(configuration, options);
            await using var provider = services.BuildServiceProvider();
            if (exporter == "traces")
            {
                var tracing = provider.GetRequiredService<TracerProvider>();
                using (var source = new ActivitySource(Source))
                using (source.StartActivity("probe"))
                {
                }

                tracing.ForceFlush(10_000);
            }
            else
            {
                var metrics = provider.GetRequiredService<MeterProvider>();
                using (var meter = new Meter(Source))
                    meter.CreateCounter<long>("ariva.probe.count").Add(1);
                metrics.ForceFlush(10_000);
            }
        }

        var request = await collector.FirstRequest.WaitAsync(Patience, Ct);
        request.Headers.Should().ContainKey("Authorization").WhoseValue.Should().Be("Bearer test-token", $"the {exporter} exporter decodes %20");
        request.Headers.Should().ContainKey("x-collector-key").WhoseValue.Should().Be("second-header", "a second pair after the comma arrives too");
    }

    #endregion

    #region The OTLP headers

    [Theory]
    [MemberData(nameof(HeaderVariables))]
    public void EnsureHeadersAreProtected_Should_RefuseTheHeaders_When_TheyWouldGoInClear(string variable)
    {
        var cases = new[]
        {
            ("k8s-dev", new Dictionary<string, string> { ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:9" }),
            ("k8s-demo", new Dictionary<string, string> { ["Otlp:Enabled"] = "true", ["Otlp:Endpoint"] = "http://127.0.0.1:9" }),
            ("k8s-dev", new Dictionary<string, string> { ["Otlp:Enabled"] = "true", ["Otlp:Endpoint"] = "https://127.0.0.1:9", ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:9" }),
            (null, new Dictionary<string, string> { ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:9" })
        };

        foreach (var (environment, settings) in cases)
        {
            settings[variable] = $"x-collector-key={Header}";
            var check = () => OtlpSettings.EnsureHeadersAreProtected(Configuration(settings), environment);

            check.Should().Throw<InvalidOperationException>($"{environment ?? "no environment"} sends {variable} in clear")
                .WithMessage($"{variable} is sent over https only*").Which.Message.Should().NotContain(Header);
        }
    }

    [Theory]
    [InlineData("k8s-dev", "https://127.0.0.1:9")]
    [InlineData("k8s-demo", "https://127.0.0.1:9")]
    [InlineData("k8s-prd", "https://127.0.0.1:9")]
    [InlineData("vm-local", "http://127.0.0.1:9")]
    public void EnsureHeadersAreProtected_Should_AcceptTheHeaders_When_TheEnvironmentGivesThemForHttps(string environment, string endpoint)
    {
        var configuration = Configuration(new() { ["OTEL_EXPORTER_OTLP_ENDPOINT"] = endpoint, ["OTEL_EXPORTER_OTLP_HEADERS"] = $"x-collector-key={Header}" });

        var check = () => OtlpSettings.EnsureHeadersAreProtected(configuration, environment);

        check.Should().NotThrow(environment == "vm-local" ? "the AppHost gives its dashboard's key over http on the machine" : "https, from the environment");
    }

    [Fact]
    public void EnsureHeadersAreProtected_Should_IgnoreTheHeaders_When_OtlpIsOff()
    {
        var configuration = Configuration(new() { ["OTEL_EXPORTER_OTLP_HEADERS"] = $"x-collector-key={Header}", ["Otlp:Endpoint"] = "http://127.0.0.1:9" });

        var check = () => OtlpSettings.EnsureHeadersAreProtected(configuration, "k8s-dev");

        check.Should().NotThrow("nothing is exported, so nothing is sent");
    }

    [Theory]
    [MemberData(nameof(HeaderVariables))]
    public void EnsureHeadersAreProtected_Should_RefuseTheHeaders_When_TheyComeFromAFileOutsideVmLocal(string variable)
    {
        var file = Path.Combine(_folder, $"{variable}.json");
        File.WriteAllText(file, $$"""{ "{{variable}}": "x-collector-key={{Header}}", "OTEL_EXPORTER_OTLP_ENDPOINT": "https://127.0.0.1:9" }""");
        IConfiguration FromFile(Dictionary<string, string> more) => new ConfigurationBuilder().AddJsonFile(file).AddInMemoryCollection(more).Build();

        var dev = () => OtlpSettings.EnsureHeadersAreProtected(FromFile(new()), "k8s-dev");
        var production = () => OtlpSettings.EnsureHeadersAreProtected(FromFile(new()), "k8s-prd");
        var local = () => OtlpSettings.EnsureHeadersAreProtected(FromFile(new()), "vm-local");
        var overridden = () => OtlpSettings.EnsureHeadersAreProtected(FromFile(new() { [variable] = $"x-collector-key={Header}" }), "k8s-prd");

        dev.Should().Throw<InvalidOperationException>().WithMessage($"{variable} comes from a configuration file*").Which.Message.Should().NotContain(Header);
        production.Should().Throw<InvalidOperationException>().WithMessage($"{variable} comes from a configuration file*");
        local.Should().NotThrow("a developer machine may keep them in a file");
        overridden.Should().NotThrow("the value the environment gives wins, and it is not a file's");
    }

    [Fact]
    public void AddArivaTelemetry_Should_RefuseTheHeaders_When_TheyWouldGoInClear()
    {
        var configuration = Configuration(new()
        {
            ["Application:Environment"] = "k8s-dev",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:9",
            ["OTEL_EXPORTER_OTLP_HEADERS"] = $"x-collector-key={Header}"
        });

        var add = () => new ServiceCollection().AddArivaTelemetry(configuration, new ArivaServiceDefaultsOptions { ServiceName = "ariva-probe" });

        add.Should().Throw<InvalidOperationException>().WithMessage("OTEL_EXPORTER_OTLP_HEADERS is sent over https only*")
            .Which.Message.Should().NotContain(Header);
    }

    [Fact]
    public void ArivaLogging_Should_RefuseTheHeaders_When_TheyWouldGoInClear()
    {
        var configuration = Configuration(new()
        {
            ["Application:Environment"] = "k8s-demo",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:9",
            ["OTEL_EXPORTER_OTLP_HEADERS"] = $"x-collector-key={Header}"
        });

        var configure = () => Ariva.Api.Common.Logging.ArivaLogging.Configure(new Serilog.LoggerConfiguration(), configuration, "k8s-demo").CreateLogger().Dispose();

        configure.Should().Throw<InvalidOperationException>().WithMessage("OTEL_EXPORTER_OTLP_HEADERS is sent over https only*");
    }

    [Theory]
    [MemberData(nameof(ArivaHosts.BackplaneHosts), MemberType = typeof(ArivaHosts))]
    public async Task Host_Should_RefuseToStart_When_TheOtlpHeadersWouldGoInClear(string host)
    {
        await using var app = ArivaHosts.Create(host, Ariva.Api.Common.Hosting.ArivaEnvironment.K8sDev, builder => builder
            .UseSetting("OTEL_EXPORTER_OTLP_ENDPOINT", "http://127.0.0.1:9")
            .UseSetting("OTEL_EXPORTER_OTLP_HEADERS", $"x-collector-key={Header}"));

        Action start = () => app.CreateClient();

        start.Should().Throw<Exception>().WithMessage("OTEL_EXPORTER_OTLP_HEADERS is sent over https only*").Which.Message.Should().NotContain(Header);
    }

    [Theory]
    [MemberData(nameof(ArivaHosts.BackplaneHosts), MemberType = typeof(ArivaHosts))]
    public async Task Host_Should_Start_When_ProductionSendsTheOtlpHeadersOverHttps(string host)
    {
        await using var app = ArivaHosts.Create(host, Ariva.Api.Common.Hosting.ArivaEnvironment.K8sPrd, builder => builder
            .UseSetting("OTEL_EXPORTER_OTLP_ENDPOINT", ArivaHosts.ProductionCollector)
            .UseSetting("OTEL_EXPORTER_OTLP_HEADERS", $"x-collector-key={Header}"));

        Action start = () => app.CreateClient().Dispose();

        start.Should().NotThrow("https, and from outside any file");
    }

    #endregion

    #region Helpers

    private static IConfiguration Configuration(Dictionary<string, string> settings) => new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static Dictionary<string, string> Settings(LoopbackCollector collector, string endpointSetting)
    {
        var settings = new Dictionary<string, string>
        {
            ["Application:Environment"] = "vm-local",
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
            ["OTEL_EXPORTER_OTLP_HEADERS"] = $"x-collector-key={Header}",
            [endpointSetting] = collector.Address
        };
        if (endpointSetting == "Otlp:Endpoint")
            settings["Otlp:Enabled"] = "true";
        return settings;
    }

    /// <summary>The service defaults' traces and metrics, with the configuration in the services as a host registers it.</summary>
    private static ServiceProvider Telemetry(LoopbackCollector collector, string endpointSetting)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(Settings(collector, endpointSetting)).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        var options = new ArivaServiceDefaultsOptions { ServiceName = "ariva-probe", Environment = "vm-local" };
        options.ActivitySources.Add(Source);
        options.Meters.Add(Source);
        services.AddArivaTelemetry(configuration, options);
        return services.BuildServiceProvider();
    }

    #endregion
}
