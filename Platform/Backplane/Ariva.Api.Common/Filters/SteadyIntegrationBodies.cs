using Ariva.Api.Common.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;

namespace Ariva.Api.Common.Filters;

/// <summary>
/// ARV-096b (CWE-400, ASVS 5.0 V15.2.2): the Integration API's batches and AIDX messages are read before MVC, by the call audit
/// (<c>UseIntegrationCallAudit</c>), while each holds its concurrency permit (<see cref="RateLimitingExtensions.IntegrationBatchPolicy"/>,
/// <see cref="RateLimitingExtensions.IntegrationAidxPolicy"/>). Run before the audit, this asks their bodies to arrive at the uploads'
/// minimum rate (<see cref="SteadyUploadAttribute.MinimumRate"/>, over HTTP/1.x); over HTTP/2 the host's request timeout ends a slow
/// read, since the audit reads with the request's token.
/// </summary>
public static class SteadyIntegrationBodies
{
    /// <summary>The concurrency policies whose bodies are read before MVC.</summary>
    public static IReadOnlySet<string> Policies { get; } =
        new HashSet<string>(StringComparer.Ordinal) { RateLimitingExtensions.IntegrationBatchPolicy, RateLimitingExtensions.IntegrationAidxPolicy };

    /// <summary>Sets the minimum body rate of every request to an endpoint under <see cref="Policies"/>; call it after routing, before any body is read.</summary>
    /// <param name="app">The Integration host's application builder.</param>
    /// <returns>The same application builder, for chaining.</returns>
    public static IApplicationBuilder UseSteadyIntegrationBodies(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use((context, next) =>
        {
            if (context.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName is { } policy && Policies.Contains(policy))
                SteadyUploadAttribute.RequireSteadyBody(context);
            return next(context);
        });
    }
}
