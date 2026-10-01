using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Ariva.Api.Common.HealthChecks;

/// <summary>
/// Kubernetes probe endpoints shared by every Ariva host. The paths match the startup, readiness and
/// liveness probes in the Helm chart (Platform/Cloud/Ariva.K8s/Helm/Charts/platform/templates).
/// Dependency checks (PostgreSQL, Kafka, Redis) are added behind readiness by the backlog.
/// </summary>
public static class HealthEndpoints
{
    /// <summary>Startup probe path.</summary>
    public const string StartupPath = "/health/startup";

    /// <summary>Readiness probe path.</summary>
    public const string ReadinessPath = "/health/readiness";

    /// <summary>Liveness probe path.</summary>
    public const string LivenessPath = "/health/liveness";

    /// <summary>
    /// Maps the three probe endpoints. Each returns 200 with a small JSON body while the process is up.
    /// </summary>
    /// <param name="app">The host application.</param>
    /// <returns>The same application, for chaining.</returns>
    public static WebApplication MapArivaHealthChecks(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(StartupPath, () => Healthy("startup")).AllowAnonymous().ExcludeFromDescription();
        app.MapGet(ReadinessPath, () => Healthy("readiness")).AllowAnonymous().ExcludeFromDescription();
        app.MapGet(LivenessPath, () => Healthy("liveness")).AllowAnonymous().ExcludeFromDescription();

        return app;
    }

    private static IResult Healthy(string probe) => Results.Ok(new { status = "Healthy", probe });
}
