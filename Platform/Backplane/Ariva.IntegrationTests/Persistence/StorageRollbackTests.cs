using System.Data;
using System.Reflection;
using Ariva.Core.Services;
using Ariva.IntegrationTests.Persistence.Samples;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using NHibernate;
using NHibernate.Linq;
using Npgsql;

namespace Ariva.IntegrationTests.Persistence;

/// <summary>
/// ARV-291 (CWE-400, ASVS 5.0 V16.5.3) against PostgreSQL 17 with TimescaleDB: a unit of work whose database connection broke (here
/// its session terminated by the server) ends as rolled back instead of throwing from its rollback and again from its disposal:
/// nothing of its transaction commits, no transaction is left active, and a write later in the same scope runs on a session and
/// transaction of its own and commits alone. A transaction disposed for any other reason (no broken connection under it) still makes
/// the rollback throw, so a real fault is never taken for a lost connection.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class StorageRollbackTests(PostgresFixture fixture)
{
    private PersistenceHost Host => fixture.Host;

    private static string NewCode() => "R-" + Guid.CreateVersion7().ToString("N")[^12..];

    [Fact]
    public async Task Rollback_Should_EndTheUnitOfWorkAsRolledBack_When_TheServerTerminatedItsSession()
    {
        var lost = NewCode();
        var kept = NewCode();
        int before = 0, after = 0;
        var activeAfterRollback = true;

        await Host.InScopeAsync(async (uow, _) =>
        {
            await uow.StorageProvider.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            await uow.StorageProvider.SaveAsync(new SampleZone(lost, "Lost with its connection", SampleZoneKind.SnakeQueue));
            await uow.StorageProvider.FlushAsync();
            before = await BackendAsync(uow);
            await TerminateAsync(before);

            var broken = () => BackendAsync(uow);
            await broken.Should().ThrowAsync<Exception>("the server ended the session");
            var rollback = () => uow.RollbackAsync();
            await rollback.Should().NotThrowAsync("nothing is left to roll back once the connection broke");
            activeAfterRollback = uow.StorageProvider.IsTransactionActive();

            // A later write in the same scope: a session of its own, which commits alone.
            await uow.StorageProvider.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            await uow.StorageProvider.SaveAsync(new SampleZone(kept, "Written after", SampleZoneKind.SnakeQueue));
            after = await BackendAsync(uow);
            uow.PromiseToCommit();
            return true;
        });

        activeAfterRollback.Should().BeFalse("the broken transaction ended with its session");
        after.Should().NotBe(before, "the later write ran on another connection");
        (await CountAsync(lost)).Should().Be(0, "nothing of the broken transaction committed");
        (await CountAsync(kept)).Should().Be(1, "the later write committed");
    }

    [Fact]
    public async Task EndAsync_Should_NotThrow_When_TheServerTerminatedTheSessionOfAUnitNotPromisedToCommit()
    {
        var lost = NewCode();

        // The unit ends (and its scope is disposed) without an explicit rollback, as a job's unit that ends by rollback does.
        var unit = () => Host.InScopeAsync(async (uow, _) =>
        {
            await uow.StorageProvider.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            await uow.StorageProvider.SaveAsync(new SampleZone(lost, "Lost with its connection", SampleZoneKind.SnakeQueue));
            await uow.StorageProvider.FlushAsync();
            await TerminateAsync(await BackendAsync(uow));
            var broken = () => BackendAsync(uow);
            await broken.Should().ThrowAsync<Exception>();
            return true;
        });

        await unit.Should().NotThrowAsync("its end and its disposal roll back what the server already ended");
        (await CountAsync(lost)).Should().Be(0);
    }

    [Fact]
    public async Task Rollback_Should_Throw_When_TheTransactionWasDisposedWithoutABrokenConnection()
    {
        var code = NewCode();

        // Npgsql's transaction disposed by code on a live connection: its ObjectDisposedException carries no database cause.
        var unit = () => Host.InScopeAsync(async (uow, _) =>
        {
            await uow.StorageProvider.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            await uow.StorageProvider.SaveAsync(new SampleZone(code, "Disposed by code", SampleZoneKind.SnakeQueue));
            await uow.StorageProvider.FlushAsync();
            var session = SessionOf(uow.StorageProvider);
            await using var command = session.Connection.CreateCommand();
            session.GetCurrentTransaction().Enlist(command);
            command.Transaction.Should().NotBeNull();
            await command.Transaction.DisposeAsync();
            await uow.RollbackAsync();
            return true;
        });

        (await unit.Should().ThrowAsync<ObjectDisposedException>("a transaction disposed without a broken connection is a fault, not a lost connection"))
            .Which.InnerException.Should().BeNull();
        (await CountAsync(code)).Should().Be(0);
    }

    #region Helpers

    private static async Task<int> BackendAsync(IUnitOfWork uow) =>
        (await uow.StorageProvider.ExecuteSqlAsync<BackendRow>("SELECT pg_backend_pid() AS \"Pid\"")).Single().Pid;

    /// <summary>Terminates the session from a connection of the test's own, and waits until the server has ended it.</summary>
    private async Task TerminateAsync(int pid)
    {
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(Host.ConnectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using (var terminate = new NpgsqlCommand("SELECT pg_terminate_backend(@pid)", connection))
        {
            terminate.Parameters.AddWithValue("pid", pid);
            ((bool)await terminate.ExecuteScalarAsync(TestContext.Current.CancellationToken)).Should().BeTrue();
        }

        for (var wait = 0; wait < 100; wait++)
        {
            await using var gone = new NpgsqlCommand("SELECT count(*)::int FROM pg_stat_activity WHERE pid = @pid", connection);
            gone.Parameters.AddWithValue("pid", pid);
            if ((int)await gone.ExecuteScalarAsync(TestContext.Current.CancellationToken) == 0)
                return;
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    private Task<int> CountAsync(string code) =>
        Host.InScopeAsync(async (uow, _) => await uow.StorageProvider.Query<SampleZone>().CountAsync(zone => zone.Code == code));

    /// <summary>The storage provider's NHibernate session (its private field): the test reaches Npgsql's transaction through it.</summary>
    private static ISession SessionOf(IStorageProvider storage)
    {
        var field = storage.GetType().GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull("the provider keeps its session in _session");
        return (ISession)field.GetValue(storage);
    }

    public sealed class BackendRow
    {
        public int Pid { get; set; }
    }

    #endregion
}
