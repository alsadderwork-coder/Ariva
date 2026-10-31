using System.Diagnostics.CodeAnalysis;
using Ariva.Infra.Streaming;
using Ariva.IntegrationTests.Messaging;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ariva.IntegrationTests.Streaming;

/// <summary>
/// ARV-274's restores on an assignment, on a broker of their own that no record is ever sent to, so the zones a worker holds are only
/// the ones it restores. Which saved zones are this instance's is decided under the topics' partition count read on the assignment, not
/// a larger one saved before; a key that is not a zone key and a snapshot that names another zone (CWE-501) are passed over,
/// Stream:MaxZones bounds the restores and the listing is held to its maximum, and one summary says what became of them. A database that
/// does not answer ends the pass: the restores wait a minute, try again, and give up after three attempts (CWE-400).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class QueueStreamRestoreTests(PostgresFixture postgres, KafkaFixture kafka) : IClassFixture<KafkaFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Dictionary<string, object> None = [];
    private readonly StreamAssignmentHarness _h = new(postgres, kafka);

    private const string Failed = "Zones of the assigned partitions not restored now";
    private const string GaveUp = "Zones of the assigned partitions not restored after 3 attempts";
    private const string Summary = "Queue stream worker restored";

    private Task<List<string>> RowsAsync(string database, [ConstantExpected] string sql, IReadOnlyDictionary<string, object> parameters) =>
        _h.RowsAsync(database, sql, parameters, Ct);

    [Fact]
    public async Task Worker_Should_RestoreOnlyWellFormedZonesOfItsPartitionsUpToMaxZones_When_ALargerPartitionCountWasSavedBefore()
    {
        // The sensing topics have 3 partitions; a count of 5 saved before (topics made again with fewer, or a stale row) still decides where
        // older records are, but not which zones are this instance's now: under 5 the four good zones below would go to partitions 3 and 4,
        // which no instance has, so none would be restored. Saved, in key order: Q9a; a hostile copy of Q9a's state under Q9b (its snapshot
        // names another zone, CWE-501); Q9e, Q9f and Q9h; and a key that is not a zone key. Stream:MaxZones is 2.
        await _h.CreateTopicsAsync();
        var name = await _h.DatabaseAsync(TestDatabase.StreamAssignedBounds, Ct);
        string[] good = ["Q9a", "Q9e", "Q9f", "Q9h"];
        await _h.SeedAsync(name, good, Ct);
        const string Copy = """
            INSERT INTO stream_zone_state (zone_key, profile_version, state, state_bytes, reference_utc, updated_on)
            SELECT @key, profile_version, state, state_bytes, reference_utc, updated_on FROM stream_zone_state WHERE zone_key = 'DMO/Q9a'
            """;
        await RowsAsync(name, Copy, new Dictionary<string, object> { ["key"] = "DMO/Q9b" });
        await RowsAsync(name, Copy, new Dictionary<string, object> { ["key"] = "Q9 without a site" });
        // A key longer than a message key is never a zone key: not even read.
        await RowsAsync(name, Copy, new Dictionary<string, object> { ["key"] = "DMO/" + new string('Q', 210) });
        await RowsAsync(name, "INSERT INTO stream_partition_count (topic_set, partitions, first_seen) VALUES ('sensing', 5, now())", None);
        good.Select(z => QueueStreamWorker.ExpectedPartition("DMO/" + z, 5)).Should().OnlyContain(p => p >= 3, "under the larger count none of them would be this instance's");

        // The listing is held to its maximum and one more (so a cut is seen), in key order.
        var store = new StreamStore(_h.Settings(name), TimeProvider.System, NullLogger<StreamStore>.Instance);
        (await store.LoadZoneKeysAsync(1, Ct)).Should().Equal([("DMO/Q9a", 7), ("DMO/Q9b", 7)]);
        (await store.LoadZoneKeysAsync(100, Ct)).Should().HaveCount(6, "the key longer than a message key is not read");

        const string Updated = "SELECT zone_key, updated_on <> '2026-10-01T00:00:00Z' FROM stream_zone_state WHERE char_length(zone_key) <= 200 ORDER BY zone_key";
        string[] expected = ["DMO/Q9a|True", "DMO/Q9b|False", "DMO/Q9e|True", "DMO/Q9f|False", "DMO/Q9h|False", "Q9 without a site|False"];
        var log = new WorkerLog();
        using (var host = _h.Host(name, new Dictionary<string, string> { ["Stream:MaxZones"] = "2" }, TimeProvider.System, log))
        {
            await host.StartAsync(Ct);

            // A restored zone is held, so each checkpoint saves its state again; one not restored is not saved.
            var rows = await RowsAsync(name, Updated, None);
            var until = DateTime.UtcNow.AddSeconds(120);
            while (!rows.SequenceEqual(expected) && DateTime.UtcNow < until)
            {
                await Task.Delay(250, Ct);
                rows = await RowsAsync(name, Updated, None);
            }

            rows.Should().Equal(expected, "Q9a and Q9e restored in key order, the hostile copy passed over, and Q9f and Q9h beyond Stream:MaxZones");

            // The worker carries on after the hostile snapshot: its checkpoints keep saving the restored zones.
            const string Saved = "SELECT updated_on FROM stream_zone_state WHERE zone_key = 'DMO/Q9a'";
            var first = await RowsAsync(name, Saved, None);
            until = DateTime.UtcNow.AddSeconds(30);
            while ((await RowsAsync(name, Saved, None)).SequenceEqual(first) && DateTime.UtcNow < until)
                await Task.Delay(250, Ct);
            (await RowsAsync(name, Saved, None)).Should().NotEqual(first, "a checkpoint each second");
            await host.StopAsync(Ct);
        }

        (await RowsAsync(name, "SELECT state->>'zoneKey' FROM stream_zone_state WHERE zone_key = 'DMO/Q9b'", None)).Should().Equal(["DMO/Q9a"],
            "the hostile row is left as it was");
        log.Lines.Should().ContainSingle(l => l.Message.StartsWith(Summary, StringComparison.Ordinal)).Which.Should().Be((LogLevel.Error,
            "Queue stream worker restored 2 zones of its assigned partitions from their saved state; not restored: 0 held already, " +
            "0 not in their site's published zone profile, 0 saved under another profile version, 0 no longer saved, 1 keys that are not zone keys, " +
            "1 saved states that fail their checks, 2 beyond Stream:MaxZones, 0 the database did not answer for"), "one summary, an error since zones were left out");
        log.Lines.Should().NotContain(l => l.Message.Contains("not all below", StringComparison.Ordinal), "the assigned partitions are below the count read now");
    }

    [Fact]
    public async Task Worker_Should_WaitAMinuteBetweenAttemptsAndGiveUpAfterThree_When_TheDatabaseDoesNotAnswerTheRestores()
    {
        // A lock held on stream_zone_state makes the listing wait to its command timeout (10 s) while the database answers everything
        // else: each attempt ends the pass, the next waits a minute of the host's clock, and the third failure in a row gives up, leaving
        // the zone to be restored with its first record. Nothing else the worker does touches the table while it holds no zone.
        await _h.CreateTopicsAsync();
        var name = await _h.DatabaseAsync(TestDatabase.StreamRestoreGiveUp, Ct);
        await _h.SeedAsync(name, ["Q9a"], Ct);
        var clock = new MovableClock();
        var log = new WorkerLog();
        var (holder, hold) = await _h.LockZoneStatesAsync(name, Ct);
        await using (holder)
        await using (hold)
        {
            using var host = _h.Host(name, new Dictionary<string, string>(), clock, log);
            await host.StartAsync(Ct);

            (await log.WaitForAsync(Failed, 1, 90, Ct)).Should().Be(1);
            log.Lines.Should().Contain(l => l.Level == LogLevel.Warning && l.Message.EndsWith("attempt 1 of 3, the next in 60 seconds", StringComparison.Ordinal));
            (await log.WaitForAsync(Failed, 2, 15, Ct)).Should().Be(1, "no second attempt before a minute of the host's clock has passed");

            clock.MoveOn(TimeSpan.FromSeconds(61));
            (await log.WaitForAsync(Failed, 2, 60, Ct)).Should().Be(2);
            log.Lines.Should().Contain(l => l.Message.EndsWith("attempt 2 of 3, the next in 60 seconds", StringComparison.Ordinal));

            clock.MoveOn(TimeSpan.FromSeconds(61));
            (await log.WaitForAsync(GaveUp, 1, 60, Ct)).Should().Be(1, "the third failure in a row gives up");

            clock.MoveOn(TimeSpan.FromSeconds(61));
            await hold.RollbackAsync(Ct);
            await Task.Delay(TimeSpan.FromSeconds(5), Ct);
            log.Count(Failed).Should().Be(2, "nothing is tried once the restores gave up, even with the database back");
            log.Count(GaveUp).Should().Be(1);
            log.Count(Summary).Should().Be(0, "nothing was listed, so there is nothing to sum up");
            await host.StopAsync(Ct);
        }

        (await RowsAsync(name, "SELECT updated_on = '2026-10-01T00:00:00Z' FROM stream_zone_state WHERE zone_key = 'DMO/Q9a'", None)).Should().Equal(["True"],
            "the zone was never held, so its state was not saved again");
    }

    [Fact]
    public async Task Worker_Should_RestoreTheZones_When_TheDatabaseAnswersTheNextAttempt()
    {
        await _h.CreateTopicsAsync();
        var name = await _h.DatabaseAsync(TestDatabase.StreamRestoreRetry, Ct);
        await _h.SeedAsync(name, ["Q9a"], Ct);
        var clock = new MovableClock();
        var log = new WorkerLog();
        var (holder, hold) = await _h.LockZoneStatesAsync(name, Ct);
        await using (holder)
        await using (hold)
        {
            using var host = _h.Host(name, new Dictionary<string, string>(), clock, log);
            await host.StartAsync(Ct);
            (await log.WaitForAsync(Failed, 1, 90, Ct)).Should().Be(1);

            // The database answers again: the next attempt, a minute on, lists and restores the zone.
            await hold.RollbackAsync(Ct);
            clock.MoveOn(TimeSpan.FromSeconds(61));
            (await log.WaitForAsync(Summary, 1, 60, Ct)).Should().Be(1);
            log.Lines.Should().ContainSingle(l => l.Message.StartsWith(Summary, StringComparison.Ordinal)).Which.Should().Be((LogLevel.Information,
                "Queue stream worker restored 1 zones of its assigned partitions from their saved state; not restored: 0 held already, " +
                "0 not in their site's published zone profile, 0 saved under another profile version, 0 no longer saved, 0 keys that are not zone keys, " +
                "0 saved states that fail their checks, 0 beyond Stream:MaxZones, 0 the database did not answer for"));

            const string Saved = "SELECT updated_on <> '2026-10-01T00:00:00Z' FROM stream_zone_state WHERE zone_key = 'DMO/Q9a'";
            var until = DateTime.UtcNow.AddSeconds(30);
            while (!(await RowsAsync(name, Saved, None)).SequenceEqual(["True"]) && DateTime.UtcNow < until)
                await Task.Delay(250, Ct);
            (await RowsAsync(name, Saved, None)).Should().Equal(["True"], "the restored zone is held, so its checkpoints save it");
            log.Count(Failed).Should().Be(1, "one failure, then the attempt that the database answered");
            log.Count(GaveUp).Should().Be(0);
            await host.StopAsync(Ct);
        }
    }
}
