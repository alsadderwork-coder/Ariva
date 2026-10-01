using System.Globalization;
using System.Text;

namespace Ariva.Core.Security;

/// <summary>
/// Username rules (ARV-010a): Unicode NFKC normalisation, then lower case (invariant), surrounding white space removed;
/// 3 to 64 characters of letters, digits, '.', '_', '-' and '@'. Normalising before lookup makes "Ahmad", "ahmad" and
/// the full-width "ａｈｍａｄ" the same account, so two look-alike accounts cannot exist.
/// </summary>
public static class UserNames
{
    public const int MinLength = 3;
    public const int MaxLength = 64;

    public static string Normalize(string userName) =>
        userName is null ? null : userName.Trim().Normalize(NormalizationForm.FormKC).ToLowerInvariant();

    public static bool IsValid(string userName)
    {
        var normalized = Normalize(userName);
        if (normalized is null || normalized.Length < MinLength || normalized.Length > MaxLength)
            return false;

        foreach (var rune in normalized.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            var allowed = Rune.IsLetterOrDigit(rune) || rune.Value is '.' or '_' or '-' or '@';
            if (!allowed || category == UnicodeCategory.Format)
                return false;
        }

        return true;
    }
}

/// <summary>A stored password hash with what is needed to verify and upgrade it.</summary>
public sealed record PasswordHashValue(string Algorithm, int Iterations, string Salt, string Hash);
