using Ariva.Api.Common.Hosting;
using Ariva.Api.Common.Security;
using Ariva.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.Api.Common.Extensions;

/// <summary>
/// OpenAPI documents of a host (ARV-063), for the dynamic security scan: OWASP ZAP's API scan reads them to reach every
/// endpoint. Off unless <c>OpenApi:Enabled</c>; the E2E harness turns it on for the scan, and a dev or demo cluster
/// may. Refused in k8s-prd (ASVS V13.4.5: no documentation endpoints in production). The document is served at
/// <see cref="DocumentPath"/> only to callers with SystemInfo.View (system administrators), never anonymously, and
/// there is no browsing UI.
/// </summary>
public static class OpenApiExtensions
{
    public const string SectionKey = "OpenApi:Enabled";

    /// <summary>The one document each host serves.</summary>
    public const string DocumentPath = "/openapi/v1.json";

    public static bool Enabled(IConfiguration configuration) => configuration?.GetValue<bool>(SectionKey) == true;

    public static IServiceCollection AddArivaOpenApi(this IServiceCollection services, IConfiguration configuration, string environmentName)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!Enabled(configuration))
            return services;
        if (string.Equals(environmentName, ArivaEnvironment.K8sPrd, StringComparison.Ordinal) ||
            string.Equals(configuration["Application:Environment"], ArivaEnvironment.K8sPrd, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("OpenApi:Enabled is not allowed in k8s-prd: production serves no API documentation.");
        }

        services.AddOpenApi();
        return services;
    }

    public static IEndpointRouteBuilder MapArivaOpenApi(this IEndpointRouteBuilder endpoints, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (Enabled(configuration))
            endpoints.MapOpenApi().RequireAuthorization(PermissionPolicyProvider.PolicyFor([nameof(Global.Defaults.Permissions.ViewSystemInfo)]));
        return endpoints;
    }
}
