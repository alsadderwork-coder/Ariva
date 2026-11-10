using System.Collections.Concurrent;
using Ariva.Core.Services.Storage;
using Ariva.Infra.Storage;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Ariva.IntegrationTests.Setup;

/// <summary>
/// ARV-294: local disk storage (as the hosts' own) that runs <see cref="Probe"/> before each write and each read, so a test can look at
/// the database while a service does its file work (<see cref="TransactionsAsync"/> says whether any session of it is then idle in a
/// transaction, a transaction left open across the disk, with its locks) or hold writes together, and keeps the keys it wrote.
/// </summary>
public sealed class ProbingFileStorage(IConfiguration configuration) : IFileStorage
{
    private readonly LocalDiskFileStorage _inner = new(configuration);

    /// <summary>Runs with "save" or "read" before the file work; none by default.</summary>
    public Func<string, Task> Probe { get; set; }

    /// <summary>Every key written, in the order the writes ended (uploads at once write at once).</summary>
    public ConcurrentQueue<string> Saved { get; } = new();

    public async Task SaveAsync(string key, Stream content, CancellationToken ct = default)
    {
        if (Probe is { } probe)
            await probe("save");
        await _inner.SaveAsync(key, content, ct);
        Saved.Enqueue(key);
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken ct = default)
    {
        if (Probe is { } probe)
            await probe("read");
        return await _inner.OpenReadAsync(key, ct);
    }

    public Task DeleteAsync(string key, CancellationToken ct = default) => _inner.DeleteAsync(key, ct);

    /// <summary>"save: 0 in transaction" or the like: the sessions of the database (but this one) idle in a transaction now.</summary>
    public static async Task<string> TransactionsAsync(string connectionString, string work)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var count = new NpgsqlCommand("""
            SELECT count(*)::int FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid() AND state LIKE 'idle in transaction%'
            """, connection);
        return $"{work}: {(int)await count.ExecuteScalarAsync(TestContext.Current.CancellationToken)} in transaction";
    }
}
