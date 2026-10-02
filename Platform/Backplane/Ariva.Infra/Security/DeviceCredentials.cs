using System.Security.Cryptography;

namespace Ariva.Infra.Security;

/// <summary>
/// Device credentials (ARV-021): <c>ardk_</c> followed by 256 random bits in base64url (48 characters in all). Shown
/// once, at registration or rotation; Ariva stores the first 13 characters (to find the device) and the lower-case hex
/// SHA-256 of the whole value. A fast hash is enough because the value is random and long (no dictionary to try); a
/// database reader cannot use a stored hash as a credential. ARV-022 compares hashes in constant time.
/// </summary>
public static class DeviceCredentials
{
    public const string Marker = "ardk_";
    public const int PrefixLength = 13;
    public const int Length = 48;

    public sealed record Issued(string Credential, string Prefix, string Hash);

    public static Issued New()
    {
        var credential = Marker + Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        return new Issued(credential, credential[..PrefixLength], Hash(credential));
    }

    public static string Hash(string credential) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(credential ?? string.Empty)));

    /// <summary>Only well-formed values reach a lookup: the marker and 43 base64url characters.</summary>
    public static bool IsWellFormed(string credential) =>
        credential is { Length: Length } && credential.StartsWith(Marker, StringComparison.Ordinal) &&
        credential.AsSpan(Marker.Length).IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_") < 0;

    /// <summary>True when the presented value hashes to <paramref name="storedHash"/>; constant time in the hash.</summary>
    public static bool Matches(string presented, string storedHash) =>
        storedHash is { Length: 64 } && IsWellFormed(presented) &&
        CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(Hash(presented)), System.Text.Encoding.ASCII.GetBytes(storedHash));
}
