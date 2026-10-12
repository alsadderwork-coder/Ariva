using System.Buffers;
using System.Globalization;
using System.Text;

namespace Ariva.Core.Domain.Components;

/// <summary>
/// Free text that people read (names, models, notes): trimmed, bounded, and without control characters, invisible
/// format characters of any plane (bidirectional overrides, zero-width joiners, tag characters) or split surrogate pairs,
/// which can disguise what a reviewer or an audit reader sees (CWE-501, CWE-79).
/// </summary>
public static class DisplayText
{
    /// <summary>The trimmed text, or an <see cref="ArgumentException"/> naming the rule it breaks.</summary>
    public static string Require(string text, int maxLength, string paramName, bool allowLineBreaks = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text, paramName);
        var trimmed = text.Trim();
        if (trimmed.Length > maxLength)
            throw new ArgumentException($"At most {maxLength} characters.", paramName);
        if (!IsClean(trimmed, allowLineBreaks))
            throw new ArgumentException("Control characters, invisible format characters and broken characters are not allowed.", paramName);
        return trimmed;
    }

    /// <summary>Like <see cref="Require"/>, but null or blank gives null.</summary>
    public static string Optional(string text, int maxLength, string paramName, bool allowLineBreaks = false) =>
        string.IsNullOrWhiteSpace(text) ? null : Require(text, maxLength, paramName, allowLineBreaks);

    /// <summary>
    /// True when the text holds no lone surrogate, no control character (line feeds and carriage returns allowed when
    /// <paramref name="allowLineBreaks"/>) and no invisible format character, each judged per code point (ARV-158): so
    /// the format characters outside the basic plane are refused like those inside it, the tag characters (U+E0001,
    /// U+E0020 to U+E007F), the shorthand format controls (U+1BCA0 to U+1BCA3) and the musical formatting characters
    /// (U+1D173 to U+1D17A), which a check by UTF-16 unit saw only as surrogate halves.
    /// </summary>
    public static bool IsClean(string text, bool allowLineBreaks = false)
    {
        if (text is null)
            return true;
        var rest = text.AsSpan();
        while (!rest.IsEmpty)
        {
            // A lone surrogate is not a code point: InvalidData, or NeedMoreData for a high surrogate at the end.
            if (Rune.DecodeFromUtf16(rest, out var rune, out var used) != OperationStatus.Done)
                return false;
            rest = rest[used..];
            if (allowLineBreaks && rune.Value is '\n' or '\r')
                continue;
            if (Rune.IsControl(rune) || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format)
                return false;
        }

        return true;
    }
}
