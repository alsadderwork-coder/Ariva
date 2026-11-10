using System.Text.RegularExpressions;

namespace Ariva.UnitTests.Setup;

/// <summary>
/// ARV-083a: the grant manifest and its script name every table, each in a grant to the host role that may use it, so the
/// source scans that keep a table to its readers allow these two files. They read and write no row; <see cref="NonGrantLines"/>
/// proves that they name a scanned table in grant lines only (a manifest entry <c>"relation=PRIVILEGE,..."</c> or one
/// <c>GRANT ... ON relation TO role;</c> statement), so the allowance covers the grants and nothing else.
/// </summary>
public static class GrantManifestSources
{
    #region Fields

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly Regex GrantLine = new(
        @"^\s*(""[a-z_]+=[A-Za-z_(), ]+"",?|GRANT\s[^;]+\sON\s[^;]+\sTO\s[a-z_, ]+;)\s*$", RegexOptions.CultureInvariant, RegexTimeout);

    #endregion

    #region Paths

    /// <summary>The grant manifest (Ariva.Infra), relative to the Platform folder.</summary>
    public const string Manifest = "Backplane/Ariva.Infra/Timescale/HostDatabaseAccess.cs";

    /// <summary>The script that creates the host roles and their grants, relative to the Platform folder.</summary>
    public const string Script = "Backplane/Ariva.Infra/Timescale/Scripts/0074_host_database_roles.sql";

    #endregion

    #region Helpers

    /// <summary>The lines of the manifest and its script that match <paramref name="pattern"/> but are no grant, each with its file.</summary>
    /// <param name="pattern">The scan's pattern for the table or column.</param>
    /// <returns>The offending lines; a scan asserts there are none.</returns>
    public static List<string> NonGrantLines(Regex pattern) =>
        [.. new[] { Manifest, Script }
            .SelectMany(file => File.ReadLines(Path.Combine([RepositoryPaths.Platform, .. file.Split('/')]))
                .Where(line => pattern.IsMatch(line) && !GrantLine.IsMatch(line))
                .Select(line => $"{file}: {line.Trim()}"))];

    #endregion
}
