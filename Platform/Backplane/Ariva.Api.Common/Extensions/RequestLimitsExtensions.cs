using Ariva.Api.Common.Settings;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ariva.Api.Common.Extensions;

/// <summary>
/// Input size limits for Kestrel, forms and JSON (CWE-120), read from <c>Security:Limits</c>.
/// </summary>
public static class RequestLimitsExtensions
{
    #region Services

    /// <summary>
    /// Applies <see cref="RequestLimitsSettings"/> to Kestrel (body, request line, headers, header timeout and no
    /// <c>Server</c> header; every host runs on Kestrel in a Linux container), form parsing and the System.Text.Json
    /// options used by controllers and minimal APIs (<c>MaxDepth</c>). The options are bound lazily, so configuration
    /// added by tests or by later configuration sources is honoured. Rejecting unknown JSON properties
    /// (<c>JsonUnmappedMemberHandling.Disallow</c>) is a later story, together with the request models.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="configuration">The host's layered configuration.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddAppRequestLimits(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<RequestLimitsSettings>()
            .Bind(configuration.GetSection(RequestLimitsSettings.SectionName))
            .Validate(IsValid, $"{RequestLimitsSettings.SectionName} values must all be positive.")
            .ValidateOnStart();

        services
            .AddOptions<KestrelServerOptions>()
            .Configure<IOptions<RequestLimitsSettings>>((kestrel, settings) =>
            {
                var limits = settings.Value;
                kestrel.AddServerHeader = false;
                kestrel.Limits.MaxRequestBodySize = limits.MaxRequestBodyBytes;
                kestrel.Limits.MaxRequestLineSize = limits.MaxRequestLineBytes;
                kestrel.Limits.MaxRequestHeadersTotalSize = limits.MaxRequestHeadersTotalBytes;
                kestrel.Limits.MaxRequestHeaderCount = limits.MaxRequestHeaderCount;
                kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(limits.RequestHeadersTimeoutSeconds);
            });

        services
            .AddOptions<FormOptions>()
            .Configure<IOptions<RequestLimitsSettings>>((form, settings) =>
            {
                var limits = settings.Value;
                form.MultipartBodyLengthLimit = limits.MaxRequestBodyBytes;
                form.BufferBodyLengthLimit = limits.MaxRequestBodyBytes;
                form.ValueCountLimit = limits.MaxFormValueCount;
                form.ValueLengthLimit = limits.MaxFormValueLengthBytes;
                form.KeyLengthLimit = limits.MaxFormKeyLengthBytes;
            });

        services
            .AddOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>()
            .Configure<IOptions<RequestLimitsSettings>>((json, settings) => json.SerializerOptions.MaxDepth = settings.Value.MaxJsonDepth);

        services
            .AddOptions<Microsoft.AspNetCore.Mvc.JsonOptions>()
            .Configure<IOptions<RequestLimitsSettings>>((json, settings) => json.JsonSerializerOptions.MaxDepth = settings.Value.MaxJsonDepth);

        return services;
    }

    #endregion

    #region Helpers

    private static bool IsValid(RequestLimitsSettings limits) =>
        limits.MaxRequestBodyBytes > 0
        && limits.MaxRequestLineBytes > 0
        && limits.MaxRequestHeadersTotalBytes > 0
        && limits.MaxRequestHeaderCount > 0
        && limits.MaxFormValueCount > 0
        && limits.MaxFormValueLengthBytes > 0
        && limits.MaxFormKeyLengthBytes > 0
        && limits.MaxJsonDepth > 0
        && limits.RequestHeadersTimeoutSeconds > 0;

    #endregion
}
