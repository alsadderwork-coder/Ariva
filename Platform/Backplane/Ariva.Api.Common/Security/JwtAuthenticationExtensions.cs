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
/// query string is ignored on API routes (SignalR hubs read it from access_token when they arrive). Failures do not
/// explain themselves: the challenge stays the Ariva.Deny problem response.
/// </summary>
public static class JwtAuthenticationExtensions
{
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
            });
        builder.Services.PostConfigure<AuthenticationOptions>(options => options.DefaultAuthenticateScheme ??= Scheme);
        return builder;
    }
}
