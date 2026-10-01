using System.Security.Cryptography;

namespace Ariva.Infra.Security;

/// <summary>
/// Temporary passwords an administrator hands over (ARV-011): 20 random characters (about 115 bits) from an alphabet
/// without look-alikes, in four groups of five for reading aloud. Shown once; the account must change it at the next
/// sign-in (pending scope). A draw that the password policy would refuse (it spells the site code, the product name or
/// the user name, about one in a thousand draws for a three-letter site code) is drawn again, so a temporary password
/// is always one the account could have chosen itself.
/// </summary>
public static class TemporaryPasswords
{
    private const string Alphabet = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string New(PasswordPolicy policy, string userName)
    {
        ArgumentNullException.ThrowIfNull(policy);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var password = Draw();
            if (policy.Validate(password, userName).Count == 0)
                return password;
        }

        throw new InvalidOperationException("No temporary password passed the password policy in 100 draws; check the context words.");
    }

    private static string Draw()
    {
        var raw = RandomNumberGenerator.GetString(Alphabet, 20);
        return string.Join('-', Enumerable.Range(0, 4).Select(i => raw.Substring(i * 5, 5)));
    }
}
