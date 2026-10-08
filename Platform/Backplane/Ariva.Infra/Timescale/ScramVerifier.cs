using System.Security.Cryptography;
using System.Text;

namespace Ariva.Infra.Timescale;

/// <summary>
/// A PostgreSQL SCRAM-SHA-256 password verifier (RFC 5802, RFC 7677), computed by the migration job so that a password it sets
/// never reaches the server (ARV-104g1, CWE-532, CWE-287): PostgreSQL stores a verifier given as a role's password as it is,
/// and a session signs in with the password it was derived from. Format, as PostgreSQL writes it:
/// <c>SCRAM-SHA-256$&lt;iterations&gt;:&lt;salt&gt;$&lt;StoredKey&gt;:&lt;ServerKey&gt;</c>, in standard Base64.
/// <para>
/// SaltedPassword = PBKDF2-HMAC-SHA-256(password, salt, iterations, 32 bytes); ClientKey = HMAC(SaltedPassword, "Client Key");
/// StoredKey = SHA-256(ClientKey); ServerKey = HMAC(SaltedPassword, "Server Key"). PostgreSQL applies SASLprep to the password
/// first; for printable ASCII (the only passwords the settings accept for this login) SASLprep changes nothing, so the UTF-8
/// bytes are the prepared password.
/// </para>
/// </summary>
internal static class ScramVerifier
{
    #region Constants

    /// <summary>PostgreSQL's default (scram_iterations) and the least the reader login function accepts.</summary>
    public const int Iterations = 4096;

    public const int SaltBytes = 16;

    #endregion

    #region Methods

    /// <summary>The verifier of <paramref name="password"/> with a new random salt.</summary>
    public static string For(string password) => For(password, RandomNumberGenerator.GetBytes(SaltBytes), Iterations);

    /// <summary>The verifier of <paramref name="password"/> with the given salt and iterations (tests use a known salt).</summary>
    public static string For(string password, byte[] salt, int iterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        ArgumentNullException.ThrowIfNull(salt);
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, Iterations);

        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var salted = Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, iterations, HashAlgorithmName.SHA256, 32);
        var clientKey = HMACSHA256.HashData(salted, "Client Key"u8);
        try
        {
            var storedKey = SHA256.HashData(clientKey);
            var serverKey = HMACSHA256.HashData(salted, "Server Key"u8);
            return $"SCRAM-SHA-256${iterations}:{Convert.ToBase64String(salt)}${Convert.ToBase64String(storedKey)}:{Convert.ToBase64String(serverKey)}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            CryptographicOperations.ZeroMemory(salted);
            CryptographicOperations.ZeroMemory(clientKey);
        }
    }

    #endregion
}
