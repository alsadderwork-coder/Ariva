using System.Text.RegularExpressions;
using Ariva.UnitTests.Setup;
using FluentAssertions;

namespace Ariva.UnitTests.Architecture;

/// <summary>
/// ARV-242 (CWE-400): no Ariva code opens a database connection or runs a command synchronously. Npgsql bounds a synchronous read by
/// a socket receive timeout set from the connection timeout left, and less than a millisecond left becomes no timeout at all
/// (Npgsql 10.0.0), so a database that accepts and never answers held a run for two hours; its asynchronous calls end at the
/// connection timeout. The scan reads the Platform's production code for Npgsql's and ADO.NET's synchronous open and commands,
/// NHibernate's synchronous transaction begin and its List and UniqueResult. Not in its scope: synchronous LINQ terminals on a
/// session's query, ISession's Get, Load and Flush (none in the code when this was written), reads and commits on a connection
/// already open (each bounded by the command timeout), and NHibernate's lazy loads, which open synchronously when they run
/// outside a transaction: code a bounded run reaches loads its collections asynchronously (the validation results' campaign).
/// </summary>
public sealed partial class SynchronousDatabaseCallTests
{
    #region Fields

    /// <summary>
    /// The matches allowed, each file with the one call it may hold and the reason: anything else in the same file is refused.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, (string Call, string Reason)> Allowed = new Dictionary<string, (string, string)>
    {
        ["Backplane/Ariva.Infra/NHibernate/NHibernateStorageProvider.cs"] = (".BeginTransaction(",
            "BeginTransactionAsync opens the session's connection asynchronously first; NHibernate's Begin then takes the open connection")
    };

    #endregion

    #region Tests

    [Fact]
    public void Platform_Should_CallNoSynchronousDatabaseApi_When_ItsProductionCodeIsRead()
    {
        var root = RepositoryPaths.Platform;
        var offenders = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsBuildOutput(f) && !f.Contains("Ariva.UnitTests", StringComparison.Ordinal) && !f.Contains("Ariva.IntegrationTests", StringComparison.Ordinal))
            .Select(f => (File: Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'), Matches: SynchronousCall().Matches(File.ReadAllText(f))))
            .Select(f => (f.File, Calls: f.Matches.Select(m => m.Value).Where((call, index) =>
                !(Allowed.TryGetValue(f.File, out var allowed) && call == allowed.Call && index == 0)).ToList()))
            .Where(f => f.Calls.Count > 0)
            .Select(f => $"{f.File}: {string.Join(", ", f.Calls.Distinct())}")
            .ToList();

        offenders.Should().BeEmpty("a synchronous open or read is bounded by no run's timeout and, below a millisecond left, by no timeout at all");
        foreach (var allowed in Allowed.Keys)
            File.Exists(Path.Combine(root, allowed)).Should().BeTrue($"{allowed} is allowed a match, so it exists");
    }

    [Theory]
    [InlineData("connection.Open();")]
    [InlineData("dataSource.OpenConnection();")]
    [InlineData("using var reader = command.ExecuteReader();")]
    [InlineData("command.ExecuteNonQuery();")]
    [InlineData("var value = command.ExecuteScalar();")]
    [InlineData("session.BeginTransaction(IsolationLevel.ReadCommitted);")]
    [InlineData("var rows = query.List<Row>();")]
    [InlineData("var row = query.UniqueResult<Row>();")]
    public void Scan_Should_FindTheSynchronousCall_When_TheCodeHoldsOne(string line) =>
        SynchronousCall().IsMatch(line).Should().BeTrue(line);

    [Theory]
    [InlineData("await connection.OpenAsync(ct);")]
    [InlineData("await using var reader = await command.ExecuteReaderAsync(ct);")]
    [InlineData("await storage.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);")]
    [InlineData("var rows = await query.ListAsync<Row>(ct);")]
    [InlineData("using var stream = File.Open(path, FileMode.Open);")]
    public void Scan_Should_PassTheAsynchronousCall_When_TheCodeHoldsOne(string line) =>
        SynchronousCall().IsMatch(line).Should().BeFalse(line);

    #endregion

    #region Methods

    private static bool IsBuildOutput(string file) =>
        file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
        file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    /// <summary>A synchronous open, command, transaction begin or NHibernate query.</summary>
    [GeneratedRegex(@"\.Open\(\)|\.OpenConnection\(\)|\.ExecuteReader\(|\.ExecuteNonQuery\(|\.ExecuteScalar\(|\.BeginTransaction\(|\.List(<[^>]+>)?\(\)|\.UniqueResult(<[^>]+>)?\(\)")]
    private static partial Regex SynchronousCall();

    #endregion
}
