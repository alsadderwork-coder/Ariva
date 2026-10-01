using System.Security.Cryptography;
using System.Text;
using Ariva.Core.Security;

namespace Ariva.Infra.Security;

/// <summary>
/// PBKDF2-SHA256 with 600,000 iterations and a 16-byte random salt (ADR-0026; OWASP Password Storage guidance). The
/// password is hashed exactly as typed: never trimmed or normalised (AMAN trimmed it). Verification is constant time
/// and also reports when a stored hash used fewer iterations, so the next successful sign-in can upgrade it.
/// </summary>
public static class PasswordHasher
{
    public const string Algorithm = "pbkdf2-sha256";
    public const int Iterations = 600_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static PasswordHashValue Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(password, salt, Iterations);
        return new PasswordHashValue(Algorithm, Iterations, Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    /// <summary>True when <paramref name="password"/> matches. Unknown algorithms never match.</summary>
    public static bool Verify(string password, PasswordHashValue stored, out bool needsRehash)
    {
        needsRehash = false;
        if (password is null || stored is null || !string.Equals(stored.Algorithm, Algorithm, StringComparison.Ordinal) || stored.Iterations <= 0)
            return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(stored.Salt);
            expected = Convert.FromBase64String(stored.Hash);
        }
        catch (FormatException)
        {
            return false;
        }

        // A damaged row (empty or short hash) never matches; an empty expected value would compare equal to nothing.
        if (salt.Length < SaltBytes || expected.Length != HashBytes)
            return false;

        var actual = Derive(password, salt, stored.Iterations, expected.Length);
        var matches = CryptographicOperations.FixedTimeEquals(actual, expected);
        needsRehash = matches && stored.Iterations < Iterations;
        return matches;
    }

    private static byte[] Derive(string password, byte[] salt, int iterations, int length = HashBytes) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, length);
}
