using System.Security.Claims;
using System.Security.Cryptography;
using Ariva.Infra.Security;
using Ariva.Infra.Settings;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Ariva.Infra.Integration;

/// <summary>
/// Integration client credentials (ARV-042): a public client id (<c>ic_</c> and 26 lower-case base32 characters, 130
/// random bits) and a secret (<c>ics_</c> and 43 base64url characters, 256 random bits), shown once and stored as a
/// PBKDF2-SHA256 hash (<see cref="PasswordHasher"/>), as docs/architecture/integration.md requires.
/// </summary>
public static class IntegrationCredentials
{
    public const string SecretMarker = "ics_";

    /// <summary>The Data Protection purpose of client TOTP seeds, apart from user seeds, so a protected seed cannot be moved between the two.</summary>
    public const string SeedProtectionPurpose = "Ariva.Totp.v1.IntegrationClient";
    public const int SecretLength = 47;

    public static string NewClientId() => "ic_" + Base32.Encode(RandomNumberGenerator.GetBytes(17))[..26].ToLowerInvariant();

    public static string NewSecret() => SecretMarker + Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    /// <summary>Whether a presented secret has the shape of one; a token exchange still spends the same PBKDF2 work on one that does not.</summary>
    public static bool IsWellFormed(string secret) =>
        secret is { Length: SecretLength } && secret.StartsWith(SecretMarker, StringComparison.Ordinal) &&
        secret.AsSpan(SecretMarker.Length).IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_") < 0;
}

/// <summary>The integration key ring (ARV-042): apart from the user ring, so neither kind of token verifies as the other.</summary>
public sealed class IntegrationTokenKeys(TokenKeys keys, TokenSettings settings)
{
    public TokenKeys Keys { get; } = keys;
    public TokenSettings Settings { get; } = settings;
}

/// <summary>Claim names of integration tokens.</summary>
public static class IntegrationClaims
{
    public const string ClientId = "sub";
    public const string SessionId = "sid";

    /// <summary>The client's scopes, space separated (RFC 8693 style).</summary>
    public const string Scope = "scope";

    /// <summary>One claim per bound site code.</summary>
    public const string Site = "site";

    public const string IssuedAt = "iat";

    /// <summary>The client's token version when the token was issued.</summary>
    public const string Version = "ver";
}

/// <summary>
/// Issues integration access tokens (ARV-042): ES256 with the integration ring, issuer ariva, audience
/// ariva-integration, typ at+jwt, 15 minutes, claims sub (client id), sid (a new session id), scope and site. No
/// refresh token: a client exchanges its credentials again with a fresh TOTP code.
/// </summary>
public sealed class IntegrationTokenIssuer(IntegrationTokenKeys ring, TimeProvider timeProvider)
{
    private readonly JsonWebTokenHandler _handler = new();

    public (string Token, DateTime ExpiresAt) Issue(string clientId, Guid sessionId, int version, IReadOnlyList<string> scopes, IReadOnlyList<string> sites)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(sites);
        if (ring.Keys.SigningKey is null)
            throw new InvalidOperationException("Ariva.Api.Integration issues integration tokens and needs Auth:IntegrationTokens:SigningKeyPath.");
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var expires = now.AddMinutes(ring.Settings.LifetimeMinutes);
        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = ring.Settings.Issuer,
            Audience = ring.Settings.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            TokenType = ArivaClaims.TokenType,
            Claims = new Dictionary<string, object>
            {
                [IntegrationClaims.ClientId] = clientId,
                [IntegrationClaims.SessionId] = sessionId.ToString(),
                [IntegrationClaims.Version] = version,
                [IntegrationClaims.Scope] = string.Join(' ', scopes),
                [IntegrationClaims.Site] = sites.ToArray()
            },
            SigningCredentials = new SigningCredentials(ring.Keys.SigningKey, SecurityAlgorithms.EcdsaSha256)
        });
        return (token, expires);
    }

    public static TokenValidationParameters ValidationParameters(IntegrationTokenKeys ring)
    {
        ArgumentNullException.ThrowIfNull(ring);
        var parameters = AccessTokenIssuer.ValidationParameters(ring.Keys, ring.Settings);
        parameters.NameClaimType = IntegrationClaims.ClientId;
        parameters.RoleClaimType = ClaimTypes.Role;
        return parameters;
    }
}
