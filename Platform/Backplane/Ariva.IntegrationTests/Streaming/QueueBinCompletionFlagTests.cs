using Ariva.Core.Queueing;
using Ariva.Infra.Settings;
using Ariva.Infra.Streaming;
using Ariva.Infra.Timescale;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Ariva.IntegrationTests.Streaming;

/// <summary>
/// ARV-114b re-check against TimescaleDB with script 0051: <c>queue_bin.degraded_by_completion_only</c>, the reason F11 made a
/// bin Degraded (its own track completion alone), as the stream writes it and as the tables guard it. A Final completion-only
/// bin saved through <see cref="StreamStore"/> reads back true; a Provisional row upserted to Final takes the flag; a Final
/// row is never rewritten. As the runtime login, the flag beside a Good, Unknown or Provisional bin is refused by
/// <c>queue_bin_completion_only_check</c> (23514), and changing it on a Final row by 0018's trigger (23001).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class QueueBinCompletionFlagTests(PostgresFixture postgres)
{
    private static readonly DateTime Start = new(2026, 9, 28, 18, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly WaitSummary NoWaits = new(0, null, null, null, null, null, []);

    private static BinResult Bin(int binIndex, BinStatus status, BinQuality quality, bool completionOnly, long entries = 20) =>
        new("Q1", Start.AddMinutes(15 * binIndex), TimeSpan.FromMinutes(15), 1, status, quality, entries, entries, NoWaits, 5, 0, 0, 0, 0, 0, 0, 7, null)
        {
            DegradedByCompletionOnly = completionOnly
        };

    private static ZoneProcessorState State() =>
        new ZoneProcessor("DMO/Q1", new QueueZoneGeometry("Q1", new HashSet<string> { "Q1 entry" }, new HashSet<string> { "Q1 exit" },
            new HashSet<string>(), new HashSet<string>()), 7).Capture();

    private async Task<string> DatabaseAsync()
    {
        var name = await postgres.CreateDatabaseAsync(TestDatabase.StreamCompletionFlag);
        await new SqlScriptRunner(postgres.ConnectionString(name), NullLogger<SqlScriptRunner>.Instance).ApplyAsync(SqlScriptCatalog.Embedded());
        return name;
    }

    private StreamStore Store(string database) => new(new DatabaseSettings
    {
        Host = postgres.Hostname, Port = postgres.Port, Name = database, Username = postgres.AdminUsername, Password = postgres.AdminPassword
    }, TimeProvider.System, NullLogger<StreamStore>.Instance);

    private async Task<List<string>> FlagsAsync(string database)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString(database));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            SELECT to_char(start_utc AT TIME ZONE 'UTC', 'HH24:MI') || '|' || status || '|' || quality || '|' || degraded_by_completion_only || '|' || entries
            FROM queue_bin WHERE zone_key = 'DMO/Q1' ORDER BY start_utc
            """, connection);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
            rows.Add(reader.GetString(0));
        return rows;
    }

    /// <summary>A statement as the runtime role in a transaction rolled back: the SQLSTATE it is refused with, or null.</summary>
    private async Task<string> AsRuntimeAsync(string database, [System.Diagnostics.CodeAnalysis.ConstantExpected] string sql)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString(database));
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE ariva_runtime", connection, transaction))
            await role.ExecuteNonQueryAsync(Ct);
#pragma warning disable CA2100 // literal statements of the test below
        await using var command = new NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100
        try
        {
            await command.ExecuteNonQueryAsync(Ct);
            return null;
        }
        catch (PostgresException e)
        {
            return e.SqlState;
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Store_Should_WriteTheCompletionFlagAndTheTablesGuardIt_When_BinsAreSavedProvisionalThenFinal()
    {
        var database = await DatabaseAsync();
        var store = Store(database);
        var state = State();
        Task Save(params BinResult[] bins) => store.SaveAsync(new StreamCheckpoint("g-flag", [new ZoneOutputs("DMO/Q1", [], bins, [], [])], [state], [], []), Ct);

        // 18:00 final and Degraded for its completion alone; 18:15 still provisional; 18:30 final and Good; 18:45 provisional, Degraded by an outage.
        await Save(Bin(0, BinStatus.Final, BinQuality.Degraded, true), Bin(1, BinStatus.Provisional, BinQuality.Good, false),
            Bin(2, BinStatus.Final, BinQuality.Good, false), Bin(3, BinStatus.Provisional, BinQuality.Degraded, false));
        (await FlagsAsync(database)).Should().Equal("18:00|Final|Degraded|true|20", "18:15|Provisional|Good|false|20", "18:30|Final|Good|false|20",
            "18:45|Provisional|Degraded|false|20");

        // 18:15 becomes final, Degraded for its completion alone: the upsert takes the flag. 18:00 sent again with other values
        // (as a buggy replay might): a Final row is never rewritten.
        await Save(Bin(1, BinStatus.Final, BinQuality.Degraded, true, entries: 22), Bin(0, BinStatus.Final, BinQuality.Degraded, false, entries: 21));
        (await FlagsAsync(database)).Should().Equal("18:00|Final|Degraded|true|20", "18:15|Final|Degraded|true|22", "18:30|Final|Good|false|20",
            "18:45|Provisional|Degraded|false|20");

        // As the runtime login: the flag beside anything but a Final, Degraded bin is refused by the check; a Final row by the trigger.
        (await AsRuntimeAsync(database, """
            INSERT INTO queue_bin (zone_key, start_utc, revision, length_minutes, status, quality, entries, exits, waits, abandoned, fragmented, censored, reanchored,
                                   rejected, open_people, late_events, profile_version, updated_on, degraded_by_completion_only)
            VALUES ('DMO/Q1', '2026-09-28T19:00:00Z', 1, 15, 'Final', 'Good', 20, 20, 0, 0, 0, 0, 0, 0, 0, 0, 7, now(), true)
            """)).Should().Be("23514", "a Good bin is not Degraded for anything");
        (await AsRuntimeAsync(database, """
            INSERT INTO queue_bin (zone_key, start_utc, revision, length_minutes, status, quality, entries, exits, waits, abandoned, fragmented, censored, reanchored,
                                   rejected, open_people, late_events, profile_version, updated_on, degraded_by_completion_only)
            VALUES ('DMO/Q1', '2026-09-28T19:00:00Z', 1, 15, 'Final', 'Unknown', 20, 20, 0, 0, 0, 0, 0, 0, 0, 0, 7, now(), true)
            """)).Should().Be("23514", "an Unknown bin is set apart whatever its completion");
        (await AsRuntimeAsync(database, "UPDATE queue_bin SET degraded_by_completion_only = true WHERE zone_key = 'DMO/Q1' AND start_utc = '2026-09-28T18:45:00Z'"))
            .Should().Be("23514", "a provisional bin's completion is not judged");
        (await AsRuntimeAsync(database, "UPDATE queue_bin SET degraded_by_completion_only = false WHERE zone_key = 'DMO/Q1' AND start_utc = '2026-09-28T18:00:00Z'"))
            .Should().Be("23001", "a Final row never changes: 0018's trigger");
        (await AsRuntimeAsync(database, "UPDATE queue_bin SET degraded_by_completion_only = true WHERE zone_key = 'DMO/Q1' AND start_utc = '2026-09-28T18:30:00Z'"))
            .Should().Be("23001");
        (await AsRuntimeAsync(database, """
            INSERT INTO queue_bin (zone_key, start_utc, revision, length_minutes, status, quality, entries, exits, waits, abandoned, fragmented, censored, reanchored,
                                   rejected, open_people, late_events, profile_version, updated_on, degraded_by_completion_only)
            VALUES ('DMO/Q1', '2026-09-28T19:00:00Z', 1, 15, 'Final', 'Degraded', 20, 20, 0, 0, 0, 0, 0, 0, 0, 0, 7, now(), true)
            """)).Should().BeNull("beside a Final, Degraded bin the flag is allowed");
        (await FlagsAsync(database)).Should().HaveCount(4, "every statement above was rolled back");
    }
}
