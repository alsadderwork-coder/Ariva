using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Ariva.Infra.Timescale;

/// <summary>
/// One versioned script from <c>Timescale/Scripts</c>. The checksum is SHA-256 over the text with line endings
/// normalised to LF and any byte order mark removed, so a Windows checkout (core.autocrlf) does not look like tampering.
/// </summary>
public sealed partial record SqlScript(int Number, string Name, string Sql, string Checksum, bool RunInTransaction)
{
    /// <summary>First-line marker for statements PostgreSQL refuses inside a transaction (CREATE INDEX CONCURRENTLY).</summary>
    public const string NoTransactionMarker = "-- ariva:no-transaction";

    public static SqlScript From(string fileName, string text)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(text);

        var match = NamePattern().Match(fileName);
        if (!match.Success)
            throw new FormatException($"Script name '{fileName}' must be NNNN_lower_snake_case.sql.");

        var normalized = Normalize(text);
        var firstLine = normalized.Split('\n', 2)[0].Trim();
        return new SqlScript(
            int.Parse(match.Groups["number"].Value, System.Globalization.CultureInfo.InvariantCulture),
            fileName,
            normalized,
            ComputeChecksum(normalized),
            !string.Equals(firstLine, NoTransactionMarker, StringComparison.Ordinal));
    }

    public static string Normalize(string text) => text.TrimStart('﻿').Replace("\r\n", "\n", StringComparison.Ordinal);

    public static string ComputeChecksum(string normalizedText) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedText)));

    [GeneratedRegex(@"^(?<number>\d{4})_[a-z0-9]+(_[a-z0-9]+)*\.sql$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
