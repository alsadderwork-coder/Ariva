using FluentAssertions;
using Npgsql;

namespace Ariva.IntegrationTests.CsvImports;

/// <summary>
/// ARV-155: takes a site's row for update in a transaction of its own and holds it until disposed (rolled back), as another
/// import, registration or publication at the site would.
/// </summary>
internal sealed class SiteHolder : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;

    private SiteHolder(NpgsqlConnection connection, NpgsqlTransaction transaction) => (_connection, _transaction) = (connection, transaction);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<SiteHolder> HoldAsync(string connectionString, string site)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        var transaction = await connection.BeginTransactionAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT 1 FROM site WHERE code = @code FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue("code", site);
        (await command.ExecuteScalarAsync(Ct)).Should().Be(1, "the site's row is held");
        return new SiteHolder(connection, transaction);
    }

    public async ValueTask DisposeAsync()
    {
        await _transaction.RollbackAsync();
        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
