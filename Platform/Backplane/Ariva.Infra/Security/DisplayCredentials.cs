namespace Ariva.Infra.Security;

/// <summary>
/// Display player credentials (ARV-058): <c>ardp_</c> followed by 256 random bits in base64url (48 characters in all),
/// stored and compared exactly like device credentials (<see cref="DeviceCredentials"/>: the first 13 characters to find
/// the display, the SHA-256 of the whole value, a constant-time comparison). Shown once.
/// </summary>
public static class DisplayCredentials
{
    public const string Marker = "ardp_";
    public const int PrefixLength = DeviceCredentials.PrefixLength;
    public const int Length = DeviceCredentials.Length;

    public static DeviceCredentials.Issued New()
    {
        var credential = Marker + Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        return new DeviceCredentials.Issued(credential, credential[..PrefixLength], DeviceCredentials.Hash(credential));
    }

    /// <summary>Only well-formed values reach a lookup: the marker and 43 base64url characters.</summary>
    public static bool IsWellFormed(string credential) =>
        credential is { Length: Length } && credential.StartsWith(Marker, StringComparison.Ordinal) &&
        credential.AsSpan(Marker.Length).IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_") < 0;

    /// <summary>True when the presented value hashes to <paramref name="storedHash"/>; constant time in the hash.</summary>
    public static bool Matches(string presented, string storedHash) =>
        storedHash is { Length: 64 } && IsWellFormed(presented) &&
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(DeviceCredentials.Hash(presented)), System.Text.Encoding.ASCII.GetBytes(storedHash));
}
