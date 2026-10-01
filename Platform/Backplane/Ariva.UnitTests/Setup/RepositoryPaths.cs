namespace Ariva.UnitTests.Setup;

/// <summary>
/// Locates files in the repository from the test binaries by walking up to the folder that holds Ariva.slnx.
/// </summary>
public static class RepositoryPaths
{
    #region Fields

    private const string SolutionFile = "Ariva.slnx";

    private static readonly Lazy<string> RootPath = new(FindRoot);

    #endregion

    #region Paths

    /// <summary>The repository root (the folder that contains Ariva.slnx).</summary>
    public static string Root => RootPath.Value;

    /// <summary>The Platform folder.</summary>
    public static string Platform => Path.Combine(Root, "Platform");

    /// <summary>The security exception list.</summary>
    public static string Allowlist => Path.Combine(Root, "security", "allowlist.json");

    /// <summary>Resolves a repository relative path written with forward slashes.</summary>
    /// <param name="relativePath">The path relative to the repository root.</param>
    /// <returns>The absolute path.</returns>
    public static string Resolve(string relativePath) =>
        Path.Combine([Root, .. relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries)]);

    #endregion

    #region Helpers

    /// <summary>
    /// Walks up from the test binaries (the normal case: bin folder inside the checkout), then from the current
    /// directory (test binaries copied elsewhere and run from the checkout).
    /// </summary>
    private static string FindRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, SolutionFile)))
                {
                    return directory.FullName;
                }
            }
        }

        throw new InvalidOperationException($"{SolutionFile} was not found above {AppContext.BaseDirectory} or {Directory.GetCurrentDirectory()}.");
    }

    #endregion
}
