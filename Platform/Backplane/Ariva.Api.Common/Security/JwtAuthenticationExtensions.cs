using Ariva.Infra.Security;
using Ariva.Infra.Settings;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ariva.Api.Common.Security;

/// <summary>
/// Access token validation on every host (ADR-0026): issuer, audience, lifetime, ES256 only, typ at+jwt, 30 seconds of
/// clock skew, keys from <see cref="TokenKeys"/>. Tokens are read from the Authorization header only; a token in the
/// query string is ignored on API routes; on <see cref="HubsPath"/> only, a request without an Authorization header may
/// carry it as access_token (a browser cannot set headers on a WebSocket; ARV-035). Failures do not
/// explain themselves: the challenge stays the Ariva.Deny problem response.
/// </summary>
public static class JwtAuthenticationExtensions
{
    /// <summary>Where SignalR hubs are mapped; only there may an access token arrive in the query string.</summary>
    public static readonly Microsoft.AspNetCore.Http.PathString HubsPath = "/hubs";

    public const string Scheme = JwtBearerDefaults.AuthenticationScheme;

    public static AuthenticationBuilder AddArivaJwtBearer(this AuthenticationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddJwtBearer(Scheme, _ => { });
        builder.Services
            .AddOptions<JwtBearerOptions>(Scheme)
            .Configure<TokenKeys, AuthSettings>((options, keys, settings) =>
            {
                options.MapInboundClaims = false;
                options.SaveToken = false;
                options.IncludeErrorDetails = false;
                options.RequireHttpsMetadata = false; // no metadata endpoint is used; keys are local
                options.TokenValidationParameters = AccessTokenIssuer.ValidationParameters(keys, settings.Tokens);
                // A device credential sent as a bearer (ARV-022) is not a token: skip it quietly instead of failing a JWT
                // parse on every sensor push. It never authenticates here; only [DeviceAuthenticated] endpoints accept it.
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var authorization = context.Request.Headers.Authorization.ToString();
                        if (authorization.StartsWith("Bearer " + DeviceCredentials.Marker, StringComparison.OrdinalIgnoreCase))
                        {
                            context.NoResult();
                            return Task.CompletedTask;
                        }

                        // A browser cannot set headers on a WebSocket: SignalR sends the token as access_token, which
                        // is read on the hubs only (CWE-598 elsewhere) and redacted from every log (RedactionEnricher).
                        if (authorization.Length == 0 && context.Request.Path.StartsWithSegments(HubsPath, StringComparison.OrdinalIgnoreCase) &&
                            context.Request.Query.TryGetValue("access_token", out var token) && token.Count == 1 && token[0] is { Length: > 0 and <= 4096 } value)
                            context.Token = value;
                        return Task.CompletedTask;
                    }
                };
            });
        builder.Services.PostConfigure<AuthenticationOptions>(options => options.DefaultAuthenticateScheme ??= Scheme);
        return builder;
    }
}
