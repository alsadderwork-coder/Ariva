using System.Security.Cryptography;

namespace Ariva.Infra.Security;

/// <summary>
/// Temporary passwords an administrator hands over (ARV-011): 20 random characters (about 115 bits) from an alphabet
/// without look-alikes, in four groups of five for reading aloud. Shown once; the account must change it at the next
/// sign-in (pending scope).
/// </summary>
public static class TemporaryPasswords
{
    private const string Alphabet = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string New()
    {
        var raw = RandomNumberGenerator.GetString(Alphabet, 20);
        return string.Join('-', Enumerable.Range(0, 4).Select(i => raw.Substring(i * 5, 5)));
    }
}
