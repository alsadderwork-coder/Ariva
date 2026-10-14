using System.Net;
using System.Text.Json;
using Ariva.Api.Common.HealthChecks;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ariva.UnitTests.Hosting;

/// <summary>
/// ARV-125a: readiness answers 200 with "Degraded" while a check is Degraded (the owner's ARV-125 decision: an alert, not a
/// readiness failure; before, the body said "Healthy"), 503 with "Unhealthy" while one is Unhealthy, and the anonymous body
/// is a status word and the probe's name only: no check, dependency or detail (CWE-200).
/// </summary>
public sealed class ReadinessTests
{
    private const string CheckName = "probe-dependency-db-host-1";
    private const string Detail = "connection to db-host-1 as ariva_app refused";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(HealthStatus.Healthy, HttpStatusCode.OK, "Healthy")]
    [InlineData(HealthStatus.Degraded, HttpStatusCode.OK, "Degraded")]
    [InlineData(HealthStatus.Unhealthy, HttpStatusCode.ServiceUnavailable, "Unhealthy")]
    public async Task Readiness_Should_SayTheStatusAndNothingElse_When_AReadyCheckReports(HealthStatus status, HttpStatusCode code, string word)
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Main, configure: builder => builder.ConfigureServices(services => services.AddHealthChecks()
            .AddCheck(CheckName, () => new HealthCheckResult(status, Detail, new InvalidOperationException(Detail)), [HealthEndpoints.ReadyTag])));
        using var client = app.CreateClient();

        using var response = await client.GetAsync(HealthEndpoints.ReadinessPath, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.Should().Be(code);
        using var json = JsonDocument.Parse(body);
        json.RootElement.EnumerateObject().Select(p => (p.Name, p.Value.GetString())).Should().Equal([("status", word), ("probe", "readiness")]);
        body.Should().NotContain(CheckName).And.NotContain("db-host-1").And.NotContain("ariva_app");
    }

    [Theory]
    [InlineData("/health/startup", "startup")]
    [InlineData("/health/liveness", "liveness")]
    public async Task OtherProbes_Should_StayHealthy_When_AReadyCheckIsDegraded(string path, string probe)
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Main, configure: builder => builder.ConfigureServices(services => services.AddHealthChecks()
            .AddCheck(CheckName, () => HealthCheckResult.Degraded(Detail), [HealthEndpoints.ReadyTag])));
        using var client = app.CreateClient();

        using var response = await client.GetAsync(path, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        json.RootElement.EnumerateObject().Select(p => (p.Name, p.Value.GetString())).Should().Equal([("status", "Healthy"), ("probe", probe)],
            "startup and liveness never run the checks, so a dependency never restarts a pod");
    }
}
