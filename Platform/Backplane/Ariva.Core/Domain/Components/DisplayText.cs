using System.Globalization;

namespace Ariva.Core.Domain.Components;

/// <summary>
/// Free text that people read (names, models, notes): trimmed, bounded, and without control characters, invisible
/// format characters (bidirectional overrides, zero-width joiners) or split surrogate pairs, which can disguise what a
/// reviewer or an audit reader sees (CWE-501).
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

    public static bool IsClean(string text, bool allowLineBreaks = false)
    {
        if (text is null)
            return true;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                    return false;
                i++;
                continue;
            }

            if (char.IsLowSurrogate(c))
                return false;
            if (allowLineBreaks && c is '\n' or '\r')
                continue;
            if (char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format)
                return false;
        }

        return true;
    }
}
