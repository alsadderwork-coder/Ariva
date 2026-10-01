using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ariva.Api.Common.HealthChecks;

/// <summary>
/// Kubernetes probe endpoints shared by every Ariva host. The paths match the startup, readiness and
/// liveness probes in the Helm chart (Platform/Cloud/Ariva.K8s/Helm/Charts/platform/templates).
/// Readiness runs the health checks tagged <see cref="ReadyTag"/> (the MassTransit bus and Kafka rider, ARV-020; more
/// dependencies as the backlog adds them) and answers 503 while any is unhealthy, without details (CWE-200).
/// </summary>
public static class HealthEndpoints
{
    /// <summary>Startup probe path.</summary>
    public const string StartupPath = "/health/startup";

    /// <summary>Readiness probe path.</summary>
    public const string ReadinessPath = "/health/readiness";

    /// <summary>Liveness probe path.</summary>
    public const string LivenessPath = "/health/liveness";

    /// <summary>The tag of checks that gate readiness; MassTransit tags its bus check with it.</summary>
    public const string ReadyTag = "ready";

    /// <summary>
    /// Maps the three probe endpoints. Each returns 200 with a small JSON body while the process is up.
    /// </summary>
    /// <param name="app">The host application.</param>
    /// <returns>The same application, for chaining.</returns>
    public static WebApplication MapArivaHealthChecks(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(StartupPath, () => Healthy("startup")).AllowAnonymous().ExcludeFromDescription();
        app.MapGet(ReadinessPath, ReadinessAsync).AllowAnonymous().ExcludeFromDescription();
        app.MapGet(LivenessPath, () => Healthy("liveness")).AllowAnonymous().ExcludeFromDescription();

        return app;
    }

    // Two parameters, so the method is not taken for a RequestDelegate and its IResult is written.
    private static async Task<IResult> ReadinessAsync(HttpContext context, CancellationToken ct)
    {
        var checks = context.RequestServices.GetService<HealthCheckService>();
        if (checks is null)
            return Healthy("readiness");
        var report = await checks.CheckHealthAsync(check => check.Tags.Contains(ReadyTag), ct);
        return report.Status == HealthStatus.Unhealthy
            ? Results.Json(new { status = "Unhealthy", probe = "readiness" }, statusCode: StatusCodes.Status503ServiceUnavailable)
            : Healthy("readiness");
    }

    private static IResult Healthy(string probe) => Results.Ok(new { status = "Healthy", probe });
}
