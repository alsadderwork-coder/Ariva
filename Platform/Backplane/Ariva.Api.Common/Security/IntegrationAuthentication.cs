using System.Security.Claims;
using System.Security.Cryptography;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Integration;
using Ariva.Core.Services;
using Ariva.Core.Services.Integration;
using Ariva.Infra.Integration;
using Ariva.Infra.Security;
using Ariva.Infra.Settings;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ariva.Api.Common.Security;

/// <summary>
/// Integration client authentication on Ariva.Api.Integration (ARV-042): a JWT bearer scheme of its own (the integration
/// key ring, audience ariva-integration), so a user token answers 401 on the Integration API and an integration token
/// answers 401 everywhere else. Each call is checked again against the client's record (active, token issued after its
/// last change, caller inside its networks, a fresh code in <see cref="TotpHeader"/> when its policy says so). Endpoints
/// opt in with <see cref="IntegrationScopeAttribute"/>: the token's scopes must include the endpoint's, and a route value
/// <c>siteCode</c> must be one of the client's sites (403 otherwise).
/// </summary>
public static class IntegrationAuthentication
{
    public const string Scheme = "Ariva.Integration";
    public const string AnyClientPolicy = "Ariva.Integration";
    public const string TotpHeader = "X-TOTP-Code";
    public const string SiteRouteValue = "siteCode";

    /// <summary>HttpContext.Items key of the checked caller.</summary>
    public const string CallerItem = "ariva:integration-caller";

    /// <summary>HttpContext.Items key of a validly signed token the per-call check refused (client id, session, reason), for the audit record.</summary>
    public const string RefusedItem = "ariva:integration-refused";

    public static string PolicyFor(string scope) => AnyClientPolicy + ":" + scope;

    /// <summary>The checked caller of an Integration API request, or null.</summary>
    public static IntegrationCallerViewModel CallerOf(HttpContext context) =>
        context?.Items.TryGetValue(CallerItem, out var caller) == true ? caller as IntegrationCallerViewModel : null;

    /// <summary>Registers the integration key ring and issuer, the scheme and one policy per scope.</summary>
    public static AuthenticationBuilder AddArivaIntegrationAuthentication(this AuthenticationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);
        var settings = IntegrationTokenSettings.From(configuration, AuthSettings.From(configuration).Tokens);
        builder.Services.TryAddSingleton(_ => new IntegrationTokenKeys(TokenKeys.Load(settings, requireSigningKey: true), settings));
        builder.Services.TryAddSingleton<IntegrationTokenIssuer>();

        builder.AddJwtBearer(Scheme, _ => { });
        builder.Services
            .AddOptions<JwtBearerOptions>(Scheme)
            .Configure<IntegrationTokenKeys>((options, ring) =>
            {
                options.MapInboundClaims = false;
                options.SaveToken = false;
                options.IncludeErrorDetails = false;
                options.RequireHttpsMetadata = false;
                options.TokenValidationParameters = IntegrationTokenIssuer.ValidationParameters(ring);
                options.Events = new JwtBearerEvents
                {
                    // Only the Authorization header; never a device credential or a query string.
                    OnMessageReceived = context =>
                    {
                        var authorization = context.Request.Headers.Authorization.ToString();
                        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
                            authorization.StartsWith("Bearer " + DeviceCredentials.Marker, StringComparison.OrdinalIgnoreCase))
                            context.NoResult();
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = async context =>
                    {
                        var principal = context.Principal;
                        var clientId = principal?.FindFirstValue(IntegrationClaims.ClientId);
                        var version = int.TryParse(principal?.FindFirstValue(IntegrationClaims.Version), System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : -1;
                        var headers = context.Request.Headers[TotpHeader];
                        var code = headers.Count == 1 ? headers[0] : null;
                        var auth = context.HttpContext.RequestServices.GetRequiredService<ISvcIntegrationAuth>();
                        var check = await auth.CheckCallAsync(clientId, version, context.HttpContext.Connection.RemoteIpAddress, code, context.HttpContext.RequestAborted);
                        if (check.Caller is null)
                        {
                            context.HttpContext.Items[RefusedItem] = (clientId, principal?.FindFirstValue(IntegrationClaims.SessionId), check.Reason);
                            context.Fail("The client may not call now.");
                            return;
                        }

                        context.HttpContext.Items[CallerItem] = check.Caller;
                    }
                };
            });

        builder.Services.AddSingleton<IAuthorizationHandler, IntegrationScopeHandler>();
        var authorization = builder.Services.AddAuthorizationBuilder()
            .AddPolicy(AnyClientPolicy, policy => policy.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser().RequireClaim(IntegrationClaims.ClientId));
        foreach (var scope in IntegrationScopes.All)
        {
            authorization.AddPolicy(PolicyFor(scope), policy => policy.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser().RequireClaim(IntegrationClaims.ClientId)
                .AddRequirements(new IntegrationScopeRequirement(scope)));
        }

        return builder;
    }

    /// <summary>
    /// Records every call to an Integration API endpoint (docs/architecture/integration.md): client, session, scope, route,
    /// site, answer and the SHA-256 of the body, also when a validly signed token is refused by the per-call check (401)
    /// or holds the wrong scope or site (403). The body is hashed only after the token authenticated, within the endpoint's
    /// size limit, so an anonymous caller cannot make the host buffer bodies. Put it after authentication and before
    /// authorization.
    /// </summary>
    public static IApplicationBuilder UseIntegrationCallAudit(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var endpoint = context.GetEndpoint();
            var scope = endpoint?.Metadata.GetMetadata<IntegrationScopeAttribute>();
            if (scope is null)
            {
                await next(context);
                return;
            }

            string sha = null;
            long bytes = 0;
            var tooLarge = false;
            TimeSpan? limited = null;
            var authenticated = await context.AuthenticateAsync(Scheme);
            // The client's allowance is spent before its body is read, on every Integration API call (refused ones too),
            // so a client cannot hold the at-once permits with calls it is not allowed to make (ARV-043, ARV-044).
            if (authenticated.Succeeded && CallerOf(context) is { } allowanceOf &&
                context.RequestServices.GetRequiredService<IntegrationClientRateLimiter>().TryAcquire(allowanceOf.ClientId) is { Allowed: false } refusedAllowance)
                limited = refusedAllowance.RetryAfter ?? TimeSpan.FromSeconds(60);
            if (authenticated.Succeeded && limited is null)
            {
                if (endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata>()?.MaxRequestBodySize is { } limit &&
                    context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
                    size.MaxRequestBodySize = limit;
                try
                {
                    (sha, bytes) = await HashBodyAsync(context.Request,
                        endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata>()?.MaxRequestBodySize);
                }
                catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
                {
                    // Beyond the endpoint's limit: answered here, and still recorded (with no payload hash).
                    sha = null;
                    bytes = 0;
                    tooLarge = true;
                }
            }

            if (limited is { } wait)
            {
                context.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                await Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Too many requests",
                    detail: "This client sent more calls than it may in a minute; retry later (with the same Idempotency-Key for a batch).").ExecuteAsync(context);
            }
            else if (tooLarge)
                await Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Too large", detail: "The body is larger than this endpoint takes.")
                    .ExecuteAsync(context);
            else
                await next(context);

            var caller = CallerOf(context);
            var refused = context.Items.TryGetValue(RefusedItem, out var item) && item is ValueTuple<string, string, string> r ? r : default;
            var clientId = caller?.ClientId ?? (Ariva.Core.Domain.Entities.IntegrationClient.IsClientId(refused.Item1) ? refused.Item1 : null);
            if (clientId is null)
                return;
            var principal = authenticated.Principal;
            try
            {
                var site = context.GetRouteValue(SiteRouteValue) as string;
                var sessionText = caller is not null ? principal?.FindFirstValue(IntegrationClaims.SessionId) : refused.Item2;
                Guid? session = Guid.TryParse(sessionText, out var sid) ? sid : null;
                var route = (endpoint as RouteEndpoint)?.RoutePattern.RawText ?? context.Request.Path.Value;
                var at = context.RequestServices.GetService<TimeProvider>()?.GetUtcNow().UtcDateTime ?? DateTime.UtcNow;
                await using var auditScope = context.RequestServices.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
                var unitOfWork = auditScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                await auditScope.ServiceProvider.GetRequiredService<ISvcIntegrationAuth>().RecordCallAsync(new IntegrationCallRecord(clientId, session, scope.Scope,
                    context.Request.Method, route is { Length: > 200 } ? route[..200] : route, site is { Length: <= 17 } ? site : null, context.Response.StatusCode, sha, bytes,
                    context.Connection.RemoteIpAddress, at), CancellationToken.None);
                await unitOfWork.EndAsync(CancellationToken.None);
            }
#pragma warning disable CA1031 // the answer is already written; a failed audit record is logged, never thrown at the client
            catch (Exception e)
#pragma warning restore CA1031
            {
                context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(IntegrationAuthentication))
                    .LogError(e, "Integration call of {ClientId} could not be recorded", clientId);
            }
        });

    // The body's SHA-256 (empty for none); the body stays readable for the endpoint. Its size is bounded by the endpoint's
    // limit, and a body within it is buffered in memory, never in a temporary file (the pod's /tmp is small).
    private static async Task<(string Sha, long Bytes)> HashBodyAsync(HttpRequest request, long? limit)
    {
        if (request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>() is { CanHaveBody: false } || request.ContentLength is 0)
            return (null, 0);
        if (limit is { } max and > 0 and <= int.MaxValue)
            request.EnableBuffering((int)max);
        else
            request.EnableBuffering();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[16 * 1024];
        long total = 0;
        int read;
        while ((read = await request.Body.ReadAsync(buffer, request.HttpContext.RequestAborted)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            total += read;
        }

        request.Body.Position = 0;
        return (Convert.ToHexStringLower(hash.GetHashAndReset()), total);
    }
}

/// <summary>
/// Marks an Integration API endpoint (ARV-042): integration tokens only, with <see cref="Scope"/> among the client's
/// scopes and the route value <c>siteCode</c> (required: the route names the site) among the client's sites. Every call is recorded.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class IntegrationScopeAttribute : AuthorizeAttribute
{
    public IntegrationScopeAttribute(string scope)
    {
        if (!IntegrationScopes.IsKnown(scope))
            throw new ArgumentException($"Unknown integration scope {scope}.", nameof(scope));
        Scope = scope;
        Policy = IntegrationAuthentication.PolicyFor(scope);
        AuthenticationSchemes = IntegrationAuthentication.Scheme;
    }

    public string Scope { get; }
}

public sealed class IntegrationScopeRequirement(string scope) : IAuthorizationRequirement
{
    public string Scope { get; } = scope;
}

/// <summary>The client holds the endpoint's scope and the route's site, as its record says now.</summary>
public sealed class IntegrationScopeHandler : AuthorizationHandler<IntegrationScopeRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, IntegrationScopeRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);
        // The scopes and sites the database gives the client now (checked by the scheme for this request), never the
        // token's copy: a narrowing takes effect at once. Every Integration API endpoint names its site in the route
        // ({siteCode}); one without it is refused (fail closed, CWE-863).
        var http = context.Resource as HttpContext;
        var caller = IntegrationAuthentication.CallerOf(http);
        if (caller is null || !System.Linq.Enumerable.Contains(caller.Scopes, requirement.Scope, StringComparer.Ordinal))
            return Task.CompletedTask;
        if (http.GetRouteValue(IntegrationAuthentication.SiteRouteValue) is not string site || !System.Linq.Enumerable.Contains(caller.SiteCodes, site, StringComparer.Ordinal))
            return Task.CompletedTask;
        context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
