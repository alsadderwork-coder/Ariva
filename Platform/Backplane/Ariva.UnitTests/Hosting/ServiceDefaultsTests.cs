using System.Net;
using Ariva.Di.Extensions;
using Ariva.Infra.Caching;
using Ariva.Infra.Settings;
using Ariva.ServiceDefaults;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace Ariva.UnitTests.Hosting;

/// <summary>
/// ARV-066: the service defaults every host shares. Telemetry only with an OTLP endpoint, one service instance across
/// signals, resilience that never repeats an unsafe request, and readiness that reflects the database (Unhealthy) and
/// Redis (Degraded only).
/// </summary>
public sealed class ServiceDefaultsTests
{
    private static HostApplicationBuilder Builder(Dictionary<string, string> settings = null)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "vm-local" });
        builder.Configuration.AddInMemoryCollection(settings ?? []);
        return builder;
    }

    [Fact]
    public void Telemetry_Should_BeOff_When_NoOtlpEndpointIsConfigured()
    {
        var builder = Builder();
        builder.AddArivaServiceDefaults(options => options.ServiceName = "api-test");
        using var host = builder.Build();

        host.Services.GetService<TracerProvider>().Should().BeNull("no collector, no exporter, nothing on the startup path");
        host.Services.GetService<HealthCheckService>().Should().NotBeNull("the probes always have a health check service");
    }

    [Fact]
    public void Telemetry_Should_KeepTheOrchestratorsServiceInstance_When_OtlpIsConfigured()
    {
        var builder = Builder(new()
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://localhost:4317",
            ["OTEL_RESOURCE_ATTRIBUTES"] = "service.instance.id=apphost-7"
        });
        builder.AddArivaServiceDefaults(options =>
        {
            options.ServiceName = "api-test";
            options.Environment = "vm-local";
        });
        using var host = builder.Build();

        var tracing = host.Services.GetRequiredService<TracerProvider>();
        var attributes = tracing.GetResource().Attributes.ToDictionary(a => a.Key, a => a.Value);
        attributes["service.name"].Should().Be("api-test");
        attributes["service.namespace"].Should().Be("ariva");
        attributes["deployment.environment"].Should().Be("vm-local");
        attributes["service.instance.id"].Should().Be("apphost-7", "the instance Serilog's sink reports too, so the dashboard shows one resource");
    }

    [Fact]
    public void Defaults_Should_AddNoMiddlewareOrEndpoint()
    {
        var builder = Builder(new() { ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://localhost:4317" });
        var before = builder.Services.Select(d => d.ServiceType).ToHashSet();
        builder.AddArivaServiceDefaults();

        var added = builder.Services.Select(d => d.ServiceType).Where(t => !before.Contains(t)).ToList();
        added.Should().NotContain(typeof(Microsoft.AspNetCore.Hosting.IStartupFilter), "a startup filter would put middleware ahead of the security baseline");
        added.Should().NotContain(t => typeof(Microsoft.AspNetCore.Routing.EndpointDataSource).IsAssignableFrom(t), "the probes are the host's, mapped and allowlisted there");
    }

    [Theory]
    [InlineData("GET", 2)]
    [InlineData("POST", 1)]
    [InlineData("PUT", 1)]
    public async Task Resilience_Should_RetryOnlySafeMethods(string method, int expectedCalls)
    {
        var handler = new FailOnce();
        var services = new ServiceCollection();
        services.AddHttpClient("emulator")
            .ConfigurePrimaryHttpMessageHandler(() => handler)
            .AddStandardResilienceHandler(options =>
            {
                ServiceDefaultsExtensions.ConfigureResilience(options);
                options.Retry.Delay = TimeSpan.Zero;
            });
        await using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("emulator");

        using var request = new HttpRequestMessage(new HttpMethod(method), "http://ingest.test/api/v1/events");
        using var _ = await client.SendAsync(request, TestContext.Current.CancellationToken);

        handler.Calls.Should().Be(expectedCalls, "a sensor event or feed message is never sent twice");
    }

    [Fact]
    public void Resilience_Should_AllowTheLongestClientTimeout()
    {
        var options = new Microsoft.Extensions.Http.Resilience.HttpStandardResilienceOptions();
        ServiceDefaultsExtensions.ConfigureResilience(options);

        options.AttemptTimeout.Timeout.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(60), "emulator clients allow up to 60 seconds");
        options.TotalRequestTimeout.Timeout.Should().BeGreaterThan(options.AttemptTimeout.Timeout);
        options.CircuitBreaker.SamplingDuration.Should().BeGreaterThanOrEqualTo(options.AttemptTimeout.Timeout * 2);
    }

    [Fact]
    public async Task Readiness_Should_BeUnhealthyWithoutTheDatabase_And_OnlyDegradedWithoutRedis()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Database:Host"] = "127.0.0.1",
            ["Database:Port"] = "1",
            ["Database:Password"] = "unused",
            ["Redis:Enabled"] = "true",
            ["Redis:ConnectionString"] = "127.0.0.1:1,connectTimeout=500"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(DatabaseSettings.FromConfiguration(configuration));
        services.AddSingleton(RedisSettings.From(configuration));
        services.AddSingleton<RedisConnection>();
        services.AddArivaDependencyChecks(configuration);
        await using var provider = services.BuildServiceProvider();

        var report = await provider.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(check => check.Tags.Contains(HealthExtensions.ReadyTag), TestContext.Current.CancellationToken);

        report.Entries["database"].Status.Should().Be(HealthStatus.Unhealthy);
        report.Entries["database"].Description.Should().BeNull("no host or login in a probe answer");
        report.Entries["redis"].Status.Should().Be(HealthStatus.Degraded, "the cache serves from memory while Redis is away");
        report.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public void Readiness_Should_NotCheckRedis_When_ItIsOff()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["Redis:Enabled"] = "false" }).Build();
        var services = new ServiceCollection();
        services.AddArivaDependencyChecks(configuration);
        using var provider = services.BuildServiceProvider();

        var registrations = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<HealthCheckServiceOptions>>().Value.Registrations;
        registrations.Select(r => r.Name).Should().Equal("database");
        registrations.Single().Tags.Should().Contain(HealthExtensions.ReadyTag);
    }

    private sealed class FailOnce : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(Calls == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
        }
    }
}
