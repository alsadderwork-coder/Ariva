using System.Security.Cryptography;

namespace Ariva.Infra.Security;

/// <summary>
/// Refresh token values (ARV-010b): 256 random bits, base64url, opaque to the client; stored only as the lower-case hex
/// SHA-256 of the value. A database reader cannot use a stored hash as a token.
/// </summary>
public static class RefreshTokens
{
    public const string CookieName = "__Secure-ariva_rt";
    public const string CookiePath = "/api/auth";

    /// <summary>Data Protection purpose for the successor kept during the grace window.</summary>
    public const string SuccessorPurpose = "Ariva.RefreshToken.Successor.v1";

    public static string New() => Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token ?? string.Empty)));

    /// <summary>Only well-formed values reach the database lookup: 43 base64url characters.</summary>
    public static bool IsWellFormed(string token) =>
        token is { Length: 43 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
