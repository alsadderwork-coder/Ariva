using System.Security.Claims;
using Ariva.Infra.Settings;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Ariva.Infra.Security;

/// <summary>Claim names and values shared by the issuer and the validators.</summary>
public static class ArivaClaims
{
    public const string Subject = "sub";
    public const string SessionId = "sid";
    public const string Family = "family";
    public const string AuthTime = "auth_time";
    public const string Methods = "amr";
    public const string Name = "name";
    public const string Scope = "scope";

    /// <summary>A temporary password or unfinished TOTP enrolment: only change-password, enrolment and logout work.</summary>
    public const string PendingScope = "pending";

    /// <summary>Access token type (RFC 9068), so an ID token or another JWT cannot be replayed as an access token.</summary>
    public const string TokenType = "at+jwt";
}

/// <summary>
/// Issues ES256 access tokens (ADR-0026): issuer ariva, audience ariva-users, 15 minutes, header kid and typ at+jwt;
/// claims sub, sid, family, auth_time, amr, name and, for accounts that are not fully set up, scope pending.
/// Registered only in Ariva.Api.Main.
/// </summary>
public sealed class AccessTokenIssuer(TokenKeys keys, AuthSettings settings, TimeProvider timeProvider)
{
    private readonly JsonWebTokenHandler _handler = new();

    public int LifetimeSeconds => settings.Tokens.LifetimeMinutes * 60;

    public string Issue(Guid userId, string userName, Guid sessionId, Guid family, DateTime authenticatedAt, IReadOnlyList<string> methods, bool pending)
    {
        if (keys.SigningKey is null)
            throw new InvalidOperationException("This host has no signing key; only Ariva.Api.Main issues tokens.");

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var claims = new Dictionary<string, object>
        {
            [ArivaClaims.Subject] = userId.ToString(),
            [ArivaClaims.SessionId] = sessionId.ToString(),
            [ArivaClaims.Family] = family.ToString(),
            [ArivaClaims.AuthTime] = new DateTimeOffset(authenticatedAt).ToUnixTimeSeconds(),
            [ArivaClaims.Methods] = methods.ToArray(),
            [ArivaClaims.Name] = userName
        };
        if (pending)
            claims[ArivaClaims.Scope] = ArivaClaims.PendingScope;

        return _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = settings.Tokens.Issuer,
            Audience = settings.Tokens.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(settings.Tokens.LifetimeMinutes),
            TokenType = ArivaClaims.TokenType,
            Claims = claims,
            SigningCredentials = new SigningCredentials(keys.SigningKey, SecurityAlgorithms.EcdsaSha256)
        });
    }

    /// <summary>The validation every host applies; also used by tests.</summary>
    public static TokenValidationParameters ValidationParameters(TokenKeys keys, TokenSettings settings) => new()
    {
        ValidIssuer = settings.Issuer,
        ValidAudience = settings.Audience,
        IssuerSigningKeys = keys.ValidationKeys,
        ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
        ValidTypes = [ArivaClaims.TokenType],
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        RequireSignedTokens = true,
        RequireExpirationTime = true,
        ClockSkew = TimeSpan.FromSeconds(settings.ClockSkewSeconds),
        NameClaimType = ArivaClaims.Name,
        RoleClaimType = ClaimTypes.Role
    };
}
