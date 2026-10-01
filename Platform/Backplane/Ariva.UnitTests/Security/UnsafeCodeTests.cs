using System.Security;
using System.Xml.Linq;
using Ariva.UnitTests.Setup;
using FluentAssertions;

namespace Ariva.UnitTests.Security;

/// <summary>
/// CWE-120: Ariva is managed code only. No project may enable unsafe blocks, Directory.Build.props switches them off
/// for every project, and no compiled Ariva assembly carries the module attribute the compiler adds for unsafe code.
/// </summary>
public sealed class UnsafeCodeTests
{
    #region Fields

    private const string AllowUnsafeBlocks = "AllowUnsafeBlocks";

    private static readonly string[] SkippedFolders = ["bin", "obj", "node_modules", ".svelte-kit", "build"];

    #endregion

    #region Tests

    [Fact]
    public void ReadProjectFiles_Should_NotEnableUnsafeBlocks_When_ProjectIsUnderPlatform()
    {
        var projects = MsBuildFilesUnder(RepositoryPaths.Platform).ToList();

        var violations = projects
            .Where(project => ReadValues(project, AllowUnsafeBlocks).Any(IsTrue))
            .Select(project => Path.GetRelativePath(RepositoryPaths.Root, project))
            .ToList();

        projects.Should().NotBeEmpty("the Platform folder holds the Ariva projects");
        violations.Should().BeEmpty("unsafe code is banned (CWE-120)");
    }

    [Fact]
    public void ReadDirectoryBuildProps_Should_DisableUnsafeBlocks_When_Read()
    {
        var values = ReadValues(Path.Combine(RepositoryPaths.Root, "Directory.Build.props"), AllowUnsafeBlocks).ToList();

        values.Should().ContainSingle().Which.Trim().Should().Be("false");
    }

    [Fact]
    public void GetCustomAttributes_Should_FindNoUnverifiableCodeAttribute_When_AssemblyIsAriva()
    {
        var violations = ArivaAssemblies.All
            .Where(assembly => assembly.ManifestModule.IsDefined(typeof(UnverifiableCodeAttribute), inherit: false))
            .Select(assembly => assembly.GetName().Name)
            .ToList();

        violations.Should().BeEmpty("the compiler marks modules built with AllowUnsafeBlocks as unverifiable");
    }

    #endregion

    #region Helpers

    private static IEnumerable<string> MsBuildFilesUnder(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            if (file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".targets", StringComparison.OrdinalIgnoreCase))
            {
                yield return file;
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(folder))
        {
            if (SkippedFolders.Contains(Path.GetFileName(directory), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var file in MsBuildFilesUnder(directory))
            {
                yield return file;
            }
        }
    }

    private static IEnumerable<string> ReadValues(string msBuildFile, string property) =>
        XDocument.Load(msBuildFile)
            .Descendants()
            .Where(element => string.Equals(element.Name.LocalName, property, StringComparison.OrdinalIgnoreCase))
            .Select(element => element.Value);

    private static bool IsTrue(string value) => string.Equals(value.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    #endregion
}
