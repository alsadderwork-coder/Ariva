namespace Ariva.IntegrationTests.Setup;

/// <summary>
/// ARV-234: a collection of its own, on a PostgreSQL container of its own, that xUnit runs alone after every collection that runs
/// in parallel (<c>DisableParallelization</c>): for a measurement of the test process itself (its managed heap), which tests running
/// beside it would add to, and for seeds too large for the suite's shared database. Its tests use database kinds of their own
/// (<see cref="TestDatabase.ReportHeap"/>), since <see cref="Security.AccountsHost"/> keeps one database a kind for the process.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgresAloneCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres-alone";
}
