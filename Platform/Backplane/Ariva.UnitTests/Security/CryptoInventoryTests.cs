using System.Text.RegularExpressions;
using Ariva.UnitTests.Setup;
using FluentAssertions;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-094 (ASVS 5.0 V11.1.2): docs/security/crypto-inventory.md lists every key, secret, certificate and algorithm Ariva
/// uses, with the code that creates, reads or checks each. Every C# file of the hosts, the libraries and the simulator that
/// uses a cryptographic API (System.Security.Cryptography, ASP.NET Data Protection, Otp.NET or Microsoft.IdentityModel.Tokens)
/// or configures TLS (System.Net.Security, System.Security.Authentication, <c>SslProtocols</c>, Npgsql's <c>SslMode</c>,
/// Confluent's <c>SecurityProtocol</c>) must be named in a row of the inventory's tables, so new cryptography cannot arrive
/// without its row; and every C# file the inventory names must still exist. A file is named by its path in backticks, from
/// the repository root or any shorter tail of it (<c>Ariva.Api.Common/Security/RefreshCookie.cs</c>) that matches exactly one
/// file.
/// </summary>
public sealed partial class CryptoInventoryTests
{
    #region Fields

    private const string Inventory = "docs/security/crypto-inventory.md";

    /// <summary>The folders whose C# code ships: every Backplane project but the test projects, and the simulator.</summary>
    private static readonly string[] SourceFolders = ["Platform/Backplane", "Platform/Simulation"];

    private static readonly string[] SkippedFolders = ["bin", "obj", "Ariva.UnitTests", "Ariva.IntegrationTests"];

    #endregion

    #region Parsers

    [GeneratedRegex(@"\b(?:System\.Security\.Cryptography|Microsoft\.AspNetCore\.DataProtection|OtpNet|Microsoft\.IdentityModel\.Tokens|System\.Net\.Security|System\.Security\.Authentication|SslProtocols|SslMode|SecurityProtocol\.)\b",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, Timeout.Infinite)]
    private static partial Regex CryptographicApi();

    /// <summary>A C# file named in backticks: <c>`Some/Folder/File.cs`</c>.</summary>
    [GeneratedRegex(@"`([A-Za-z0-9_./-]+\.cs)`", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, Timeout.Infinite)]
    private static partial Regex NamedFile();

    #endregion

    #region Tests

    [Fact]
    public void Inventory_Should_NameEveryFileThatUsesCryptography_When_TheSourceIsScanned()
    {
        var sources = SourceFiles().ToList();
        var named = NamedFiles();

        var users = sources.Where(file => CryptographicApi().IsMatch(File.ReadAllText(RepositoryPaths.Resolve(file)))).ToList();
        var missing = users.Where(file => !named.Any(name => Names(name, file))).Order(StringComparer.Ordinal).ToList();

        users.Should().NotBeEmpty("the hosts sign tokens, hash passwords and protect keys");
        missing.Should().BeEmpty($"every file that uses a cryptographic API needs its key or algorithm row in {Inventory} (Code column); missing: {string.Join(", ", missing)}");
    }

    [Fact]
    public void Inventory_Should_NameOnlyFilesThatExist_When_Read()
    {
        var all = Directory.EnumerateFiles(RepositoryPaths.Platform, "*.cs", SearchOption.AllDirectories)
            .Select(Relative)
            .Where(file => !Skipped(file))
            .ToList();

        var stale = NamedFiles()
            .Where(name => all.Count(file => Names(name, file)) != 1)
            .Order(StringComparer.Ordinal)
            .ToList();

        stale.Should().BeEmpty($"each C# file {Inventory} names must match exactly one file; not found or ambiguous: {string.Join(", ", stale)}");
    }

    #endregion

    #region Helpers

    private static IEnumerable<string> SourceFiles() =>
        SourceFolders
            .SelectMany(folder => Directory.EnumerateFiles(RepositoryPaths.Resolve(folder), "*.cs", SearchOption.AllDirectories))
            .Select(Relative)
            .Where(file => !Skipped(file));

    /// <summary>The C# files named in the inventory's table rows (a line that starts with <c>|</c>), not in its prose.</summary>
    private static List<string> NamedFiles() =>
        File.ReadAllLines(RepositoryPaths.Resolve(Inventory))
            .Where(line => line.StartsWith('|'))
            .SelectMany(line => NamedFile().Matches(line).Select(match => match.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>True when the inventory's name is the file's repository path or a tail of it that starts at a folder.</summary>
    private static bool Names(string name, string file) =>
        file == name || file.EndsWith("/" + name, StringComparison.Ordinal);

    private static string Relative(string path) =>
        Path.GetRelativePath(RepositoryPaths.Root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static bool Skipped(string file) =>
        file.Split('/').Any(part => SkippedFolders.Contains(part, StringComparer.Ordinal));

    #endregion
}
