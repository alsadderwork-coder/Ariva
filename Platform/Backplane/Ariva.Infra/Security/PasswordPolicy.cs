using System.Globalization;
using System.IO.Compression;
using System.Text;
using Ariva.Core.Security;

namespace Ariva.Infra.Security;

/// <summary>
/// Password rules (ADR-0026, NIST SP 800-63B): 12 to 128 characters counted as Unicode code points, any characters, no
/// composition rules, no expiry. Rejected when the password is a known breached or common password (bundled list,
/// because sites may be offline) or contains the username, "ariva" or a configured context word such as the site code.
/// The list holds the 12+ character entries of SecLists' 1 million most common passwords (MIT licence), NFKC
/// normalised and lower-cased; comparisons use the same normalisation.
/// </summary>
public sealed class PasswordPolicy
{
    public const int MinLength = 12;
    public const int MaxLength = 128;

    private static readonly Lazy<HashSet<string>> Blocklist = new(LoadBlocklist, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly string[] _contextWords;

    public PasswordPolicy(IEnumerable<string> contextWords)
    {
        _contextWords = (contextWords ?? [])
            .Append("ariva")
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .Select(Fold)
            .Where(w => w.Length >= 3)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public static int BlocklistSize => Blocklist.Value.Count;

    /// <summary>Error messages; empty when the password is acceptable.</summary>
    public IReadOnlyList<string> Validate(string password, string userName)
    {
        var errors = new List<string>();
        if (string.IsNullOrEmpty(password))
        {
            errors.Add($"Choose a password of {MinLength} to {MaxLength} characters.");
            return errors;
        }

        var length = password.EnumerateRunes().Count();
        if (length < MinLength || length > MaxLength)
            errors.Add($"Choose a password of {MinLength} to {MaxLength} characters.");

        var folded = Fold(password);
        if (Blocklist.Value.Contains(folded))
            errors.Add("This password appears in lists of breached or common passwords; choose another.");

        var name = UserNames.Normalize(userName);
        foreach (var word in _contextWords.Append(name is { Length: >= 3 } ? Fold(name) : null).Where(w => w is not null))
        {
            if (folded.Contains(word, StringComparison.Ordinal))
            {
                errors.Add("The password may not contain your username, the product name or the site code.");
                break;
            }
        }

        return errors;
    }

    private static string Fold(string text) => text.Normalize(NormalizationForm.FormKC).ToLower(CultureInfo.InvariantCulture);

    private static HashSet<string> LoadBlocklist()
    {
        using var stream = typeof(PasswordPolicy).Assembly.GetManifestResourceStream("Ariva.Infra.Security.password-blocklist.txt.gz")
            ?? throw new InvalidOperationException("The password blocklist resource is missing from Ariva.Infra.");
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);

        var set = new HashSet<string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0)
                set.Add(line);
        }

        return set;
    }
}
