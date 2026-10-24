using System.Buffers;
using System.Globalization;
using System.Text;

namespace Ariva.Core.Domain.Components;

/// <summary>
/// Free text that people read (names, models, notes): trimmed, bounded, and without control characters, invisible
/// format characters of any plane (bidirectional overrides, zero-width joiners, tag characters) or split surrogate pairs,
/// which can disguise what a reviewer or an audit reader sees (CWE-501, CWE-79). The one exception (owner decision
/// 2026-10-13, ARV-169): the England, Scotland and Wales flag emoji, each taken whole as its well-formed RGI sequence, so
/// no other tag character, and no tag content beyond those three, can be carried.
/// </summary>
public static class DisplayText
{
    /// <summary>
    /// The three RGI subdivision flags (ARV-169): the waving black flag U+1F3F4, the tags for gbeng, gbsct or gbwls, and the
    /// cancel tag U+E007F. Taken only as these exact sequences; any other use of a tag character is refused.
    /// </summary>
    private static readonly string[] SubdivisionFlags =
    [
        "\U0001F3F4\U000E0067\U000E0062\U000E0065\U000E006E\U000E0067\U000E007F",
        "\U0001F3F4\U000E0067\U000E0062\U000E0073\U000E0063\U000E0074\U000E007F",
        "\U0001F3F4\U000E0067\U000E0062\U000E0077\U000E006C\U000E0073\U000E007F"
    ];

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
    /// (U+1D173 to U+1D17A), which a check by UTF-16 unit saw only as surrogate halves. The three subdivision flag emoji are
    /// clean as whole sequences (ARV-169); their tag characters anywhere else are not.
    /// </summary>
    /// <param name="allowJoiners">
    /// True for a person's name (ARV-169): the zero-width non-joiner and joiner (U+200C, U+200D), which Persian and some Indic
    /// names need, are taken where they stand, so the text judged is the text stored.
    /// </param>
    public static bool IsClean(string text, bool allowLineBreaks = false, bool allowJoiners = false) =>
        text is null || NextOffender(text, 0, allowLineBreaks, allowJoiners, out _, out _) < 0;

    /// <summary>
    /// The code points that break <see cref="IsClean"/>, each with its position counted in code points from 1 (ARV-162): a
    /// lone surrogate as its own UTF-16 unit, every other offender as its code point. For saying what is wrong with a stored
    /// text without repeating it, where the text may be personal (a note, a reason, a person's name). Empty when clean.
    /// </summary>
    public static IReadOnlyList<(int Position, int CodePoint)> Offenders(string text, bool allowLineBreaks = false, bool allowJoiners = false)
    {
        var found = new List<(int Position, int CodePoint)>();
        if (text is null)
            return found;
        var index = 0;
        var position = 1;
        var counted = 0;
        while ((index = NextOffender(text, index, allowLineBreaks, allowJoiners, out var codePoint, out var length)) >= 0)
        {
            position += CodePoints(text.AsSpan(counted, index - counted));
            counted = index;
            found.Add((position, codePoint));
            index += length;
        }

        return found;
    }

    /// <summary>
    /// The UTF-16 index of the first code point at or after <paramref name="start"/> that breaks the rule, or -1: a lone
    /// surrogate (InvalidData, or NeedMoreData for a high surrogate at the end), else a control or format character judged
    /// per code point (ARV-158), line feeds and carriage returns allowed when <paramref name="allowLineBreaks"/>. A subdivision
    /// flag emoji is stepped over whole (ARV-169), so a tag character is an offender unless it is part of one.
    /// </summary>
    private static int NextOffender(string text, int start, bool allowLineBreaks, bool allowJoiners, out int codePoint, out int length)
    {
        var rest = text.AsSpan(start);
        var index = start;
        while (!rest.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(rest, out var rune, out var used) != OperationStatus.Done)
            {
                (codePoint, length) = (rest[0], 1);
                return index;
            }

            if (rune.Value == 0x1F3F4 && SubdivisionFlagLength(rest) is > 0 and var flag)
            {
                rest = rest[flag..];
                index += flag;
                continue;
            }

            if (!(allowLineBreaks && rune.Value is '\n' or '\r') && !(allowJoiners && rune.Value is 0x200C or 0x200D) &&
                (Rune.IsControl(rune) || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format))
            {
                (codePoint, length) = (rune.Value, used);
                return index;
            }

            rest = rest[used..];
            index += used;
        }

        (codePoint, length) = (0, 0);
        return -1;
    }

    /// <summary>The UTF-16 length of the subdivision flag <paramref name="text"/> starts with, or 0.</summary>
    private static int SubdivisionFlagLength(ReadOnlySpan<char> text)
    {
        foreach (var flag in SubdivisionFlags)
            if (text.StartsWith(flag, StringComparison.Ordinal))
                return flag.Length;
        return 0;
    }

    /// <summary>Code points in a span, a lone surrogate counted as one.</summary>
    private static int CodePoints(ReadOnlySpan<char> text)
    {
        var count = 0;
        while (!text.IsEmpty)
        {
            Rune.DecodeFromUtf16(text, out _, out var used);
            text = text[Math.Max(used, 1)..];
            count++;
        }

        return count;
    }

    /// <summary>
    /// The text as printable ASCII (ARV-162): printable ASCII kept, a backslash doubled, every other code point written as
    /// <c>\uXXXX</c> or <c>\UXXXXXXXX</c> and a lone surrogate as its own <c>\uXXXX</c>. For naming a stored text that
    /// breaks the rule (a validation problem, the pre-pilot check) without letting an override or an invisible character
    /// reach the reader: what is printed is what is stored.
    /// </summary>
    public static string Escape(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is >= ' ' and <= '~')
            {
                builder.Append(c == '\\' ? "\\\\" : c.ToString());
            }
            else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                builder.Append(CultureInfo.InvariantCulture, $"\\U{char.ConvertToUtf32(c, text[i + 1]):X8}");
                i++;
            }
            else
            {
                builder.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:X4}");
            }
        }

        return builder.ToString();
    }
}
