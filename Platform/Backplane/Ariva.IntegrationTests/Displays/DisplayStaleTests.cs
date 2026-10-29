using System.Collections.Concurrent;
using Ariva.Di.Extensions;
using Ariva.Infra.Caching;
using Ariva.Infra.Live;
using Ariva.Infra.Services.Displays;
using Ariva.IntegrationTests.Caching;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Ariva.IntegrationTests.Displays;

/// <summary>
/// ARV-259d against PostgreSQL with the scripts applied, as the runtime login, and a real Redis: the display stale sweep counts,
/// per site, the enabled live displays whose board would be stale now (a queue zone's live snapshot missing or older than the
/// display's stale threshold, the player's rule), reads each zone's snapshot once, judges at most
/// <see cref="SvcDisplayStaleness.MaxDisplays"/> displays by site and code, counts every display not judged yet as stale when
/// Redis does not answer, and runs on one replica a round (lock class <see cref="SvcDisplayStaleness.LockClass"/>).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DisplayStaleTests(PostgresFixture fixture, RedisFixture redis) : IClassFixture<RedisFixture>, IAsyncDisposable
{
    private readonly string _runtimePassword = "rt-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));

    private AccountsHost _host;
    private RedisConnection _redis;
    private CountingSnapshots _snapshots;
    private readonly RecordingLogs _logs = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
            await _host.DisposeAsync();
        if (_redis is not null)
            await _redis.DisposeAsync();
    }

    /// <summary>The host's services on the test database as the runtime login, with Redis's live snapshots behind a counter.</summary>
    private async Task<AccountsHost> HostAsync()
    {
        _redis = new RedisConnection(new RedisSettings { Enabled = true, ConnectionString = redis.ConnectionString, InstanceName = "it-display-stale:" });
        _snapshots = new CountingSnapshots(new RedisLiveSnapshots(_redis));
        _host = new AccountsHost(fixture, database: TestDatabase.DisplayStale, configure: services =>
        {
            services.AddSingleton<ILiveSnapshotStore>(_snapshots);
            services.AddSingleton<ILoggerProvider>(_logs);
            services.AddArivaDisplayStaleMonitor();
        });
        var database = await _host.DatabaseAsync();
        const string login = "it_runtime_display_stale";
        await using (var admin = new NpgsqlConnection(fixture.ConnectionString(database)))
        {
            await admin.OpenAsync(Ct);
            await using var ensure = new NpgsqlCommand("SELECT ariva_ensure_runtime_login(@login, @password)", admin);
            ensure.Parameters.AddWithValue("login", login);
            ensure.Parameters.AddWithValue("password", _runtimePassword);
            await ensure.ExecuteNonQueryAsync(Ct);
        }

        // The tests share the database: the displays an earlier test left are taken out (soft deleted, as the services do).
        await ExecuteAsync("UPDATE display SET deleted_on = now() WHERE deleted_on IS NULL");
        _host.Settings["Database:Username"] = login;
        _host.Settings["Database:Password"] = _runtimePassword;
        // Builds the services (as the runtime login) and proves the login writes as the hosts' does.
        await _host.CreateUserAsync("it.display.stale." + Guid.NewGuid().ToString("N")[..8]);
        return _host;
    }

    private DisplayStaleMonitor Monitor => _host.Provider.GetServices<IHostedService>().OfType<DisplayStaleMonitor>().Single();

    private DisplayMetrics Metrics => _host.Provider.GetRequiredService<DisplayMetrics>();

    private DateTime Now => _host.Clock.GetUtcNow().UtcDateTime;

    private async Task ExecuteAsync([System.Diagnostics.CodeAnalysis.ConstantExpected] string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // every caller passes a literal (ConstantExpected)
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>A display as the services store it: its site, code, stale threshold and queue zones; enabled and live unless said.</summary>
    private Task DisplayAsync(string site, string code, int staleSeconds, string[] zones, bool enabled = true, bool deleted = false) =>
        ExecuteAsync("""
            INSERT INTO display (id, site_code, code, name, orientation, languages, band_minutes, hysteresis_minutes, stale_seconds, entries,
                fallback_messages, enabled, deleted_on)
            VALUES (gen_random_uuid(), @site, @code, 'Arrivals hall', 'Landscape', 'en', 5, 1, @stale, @entries, '{"en":"Please follow the signs"}',
                @enabled, CASE WHEN @deleted THEN now() END)
            """, ("site", site), ("code", code), ("stale", staleSeconds),
            ("entries", "[" + string.Join(',', zones.Select(z => $$$"""{"zone":"{{{z}}}","labels":{"en":"{{{z}}}"}}""")) + "]"), ("enabled", enabled),
            ("deleted", deleted));

    private Task SnapshotAsync(string zoneKey, double ageSeconds, double? nowcastMinutes = 7.5, string noService = null) =>
        _snapshots.PublishAsync([new LiveZoneSnapshot(zoneKey, Now.AddMinutes(-1), 12, true, false, nowcastMinutes, 3.0, noService, false, Now.AddSeconds(-ageSeconds))], Ct);

    /// <summary>Another replica's try for the sweep's lock (released at once when taken: the statement is its own transaction).</summary>
    private async Task<bool> TryLockAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(@class, 0)", connection);
        command.Parameters.AddWithValue("class", SvcDisplayStaleness.LockClass);
        return await command.ExecuteScalarAsync(Ct) is true;
    }

    private static Dictionary<string, (int Displays, int Stale)> BySite(Ariva.Core.Services.Displays.DisplayStaleSweep sweep) =>
        sweep.Sites.ToDictionary(s => s.SiteCode, s => (s.Displays, s.Stale), StringComparer.Ordinal);

    [Fact]
    public async Task Sweep_Should_CountTheDisplaysWhoseBoardIsStale_When_ASnapshotIsMissingOrOlderThanTheirThreshold()
    {
        await HostAsync();
        await ExecuteAsync("INSERT INTO site (id, code, name) VALUES (gen_random_uuid(), 'DSA', 'Stale A'), (gen_random_uuid(), 'DSB', 'Stale B')");
        await SnapshotAsync("DSA/Z1", 30);
        await SnapshotAsync("DSA/Z2", 90);
        await SnapshotAsync("DSA/Z4", 10, nowcastMinutes: null, noService: "NothingOpen");
        await DisplayAsync("DSA", "A1", 150, ["Z1"]);
        await DisplayAsync("DSA", "A2", 60, ["Z1", "Z2"]);
        await DisplayAsync("DSA", "A3", 150, ["Z2"]);
        await DisplayAsync("DSA", "A4", 60, ["Z3"]);
        await DisplayAsync("DSA", "A5", 60, ["Z3"], enabled: false);
        await DisplayAsync("DSA", "A6", 60, ["Z3"], deleted: true);
        await DisplayAsync("DSA", "A7", 90, ["Z2"]);
        await DisplayAsync("DSA", "A8", 60, ["Z4"]);
        await DisplayAsync("DSB", "B1", 60, ["Z1"]);
        using var reader = new InstrumentReader(Metrics.Meter);
        // While the sweep reads Redis, another replica's try for the lock fails: the sweep holds it to its end.
        bool? freeWhileReading = null;
        _snapshots.OnFirstRead = async () => freeWhileReading = await TryLockAsync();

        await Monitor.RunOnceAsync(Ct);

        freeWhileReading.Should().BeFalse("the sweep's transaction holds lock class 54 while it reads the snapshots");
        (await TryLockAsync()).Should().BeTrue("and lets it go when it ends");
        var sweep = Monitor.Last;
        sweep.Ran.Should().BeTrue();
        sweep.Truncated.Should().BeFalse();
        sweep.SnapshotsUnreadable.Should().BeFalse();
        BySite(sweep).Should().BeEquivalentTo(new Dictionary<string, (int, int)> { ["DSA"] = (6, 2), ["DSB"] = (1, 1) },
            "A2 shows Z2, 90 s old past its 60; A4 shows Z3, which has no snapshot; B1 shows its own site's Z1, which has none. A1 and A3 are within " +
            "their thresholds, A7's is exactly the age (the player shows a band up to it), A8's zone is out of service (neutral by design, not stale), " +
            "and A5 (disabled) and A6 (deleted) are not judged");
        _snapshots.Reads.Should().BeEquivalentTo(["DSA/Z1", "DSA/Z2", "DSA/Z3", "DSA/Z4", "DSB/Z1"], "each zone's snapshot once a sweep");
        reader.Observe(DisplayMetrics.StaleName).ToDictionary(m => m.Tags["site"], m => m.Value, StringComparer.Ordinal)
            .Should().BeEquivalentTo(new Dictionary<string, double> { ["DSA"] = 2, ["DSB"] = 1 });

        // Every zone fresh again: each site with an enabled display is still reported, with 0.
        await SnapshotAsync("DSA/Z2", 0);
        await SnapshotAsync("DSA/Z3", 0);
        await SnapshotAsync("DSB/Z1", 0);
        await Monitor.RunOnceAsync(Ct);
        reader.Observe(DisplayMetrics.StaleName).ToDictionary(m => m.Tags["site"], m => m.Value, StringComparer.Ordinal)
            .Should().BeEquivalentTo(new Dictionary<string, double> { ["DSA"] = 0, ["DSB"] = 0 });

        // A minute and a second on, nothing new published: the 60-second displays go stale, the others hold.
        _host.Clock.Advance(TimeSpan.FromSeconds(61));
        await Monitor.RunOnceAsync(Ct);
        BySite(Monitor.Last).Should().BeEquivalentTo(new Dictionary<string, (int, int)> { ["DSA"] = (6, 3), ["DSB"] = (1, 1) },
            "A2 (Z1 now 91 s old), A4 (Z3, 61 s) and A8 (Z4, 71 s: out of service, but old) past 60 s; A1, A3 and A7 within theirs; B1 past 60 s");

        // The copy lapses two sweep intervals after the last sweep this replica ran.
        _host.Clock.Advance(2 * Monitor.Interval + TimeSpan.FromSeconds(1));
        reader.Observe(DisplayMetrics.StaleName).Should().BeEmpty();
    }

    [Fact]
    public async Task Sweep_Should_SkipTheRoundAndBoundItsRead_When_AnotherReplicaHoldsTheLockOrDisplaysAreMany()
    {
        await HostAsync();
        await ExecuteAsync("INSERT INTO site (id, code, name) VALUES (gen_random_uuid(), 'DSC', 'Stale C'), (gen_random_uuid(), 'DSD', 'Stale D')");
        await DisplayAsync("DSC", "C1", 60, ["Z1"]);
        await SnapshotAsync("DSC/Z1", 5);

        // Another replica's sweep holds the lock: this one skips the round without waiting and reports nothing new.
        var database = await _host.DatabaseAsync();
        await using (var other = new NpgsqlConnection(fixture.ConnectionString(database)))
        {
            await other.OpenAsync(Ct);
            await using var transaction = await other.BeginTransactionAsync(Ct);
            await using (var hold = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@class, 0)", other, transaction))
            {
                hold.Parameters.AddWithValue("class", SvcDisplayStaleness.LockClass);
                await hold.ExecuteNonQueryAsync(Ct);
            }

            using var reader = new InstrumentReader(Metrics.Meter);
            await Monitor.RunOnceAsync(Ct);
            Monitor.Last.Should().Be(Ariva.Core.Services.Displays.DisplayStaleSweep.Skipped);
            reader.Observe(DisplayMetrics.StaleName).Should().BeEmpty("only the replica that swept reports");
            _snapshots.Reads.Should().BeEmpty();
            await transaction.CommitAsync(Ct);
        }

        // The lock released at the other's commit, and the sweep's own released at its end: two sweeps in a row both run.
        await Monitor.RunOnceAsync(Ct);
        Monitor.Last.Ran.Should().BeTrue();
        await Monitor.RunOnceAsync(Ct);
        Monitor.Last.Ran.Should().BeTrue();

        // More enabled displays than one sweep judges: the first by site and code, the rest left out.
        await ExecuteAsync("""
            INSERT INTO display (id, site_code, code, name, orientation, languages, band_minutes, hysteresis_minutes, stale_seconds, entries,
                fallback_messages, enabled)
            SELECT gen_random_uuid(), 'DSD', 'D' || lpad(n::text, 4, '0'), 'Gate', 'Landscape', 'en', 5, 1, 60,
                '[{"zone":"Z1","labels":{"en":"Z1"}}]', '{"en":"Please follow the signs"}', true
              FROM generate_series(1, @count) n
            """, ("count", SvcDisplayStaleness.MaxDisplays));
        _snapshots.Clear();
        await Monitor.RunOnceAsync(Ct);
        Monitor.Last.Truncated.Should().BeTrue();
        BySite(Monitor.Last).Should().BeEquivalentTo(new Dictionary<string, (int, int)> { ["DSC"] = (1, 0), ["DSD"] = (SvcDisplayStaleness.MaxDisplays - 1, SvcDisplayStaleness.MaxDisplays - 1) },
            "DSC's display first, then DSD's in code order up to the bound; DSD's Z1 has no snapshot");
        _snapshots.Reads.Should().BeEquivalentTo(["DSC/Z1", "DSD/Z1"], "one read per zone, however many displays show it");
    }

    [Theory]
    [InlineData("DSE", false)]
    [InlineData("DSF", true)]
    public async Task Sweep_Should_CountEveryDisplayNotJudgedYetAsStale_When_RedisDoesNotAnswer(string site, bool timesOut)
    {
        await HostAsync();
        await ExecuteAsync("INSERT INTO site (id, code, name) VALUES (gen_random_uuid(), @site, 'Stale')", ("site", site));
        await SnapshotAsync($"{site}/Z1", 5);
        await DisplayAsync(site, $"{site}-1", 60, ["Z1"]);
        await DisplayAsync(site, $"{site}-2", 60, ["Z2"]);
        await DisplayAsync(site, $"{site}-3", 60, ["Z1"]);
        // Redis refusing the connection, or not answering within RedisLiveSnapshots.OperationTimeout.
        _snapshots.FailFrom = 1;
        _snapshots.Failure = timesOut
            ? () => new TimeoutException("The operation has timed out.")
            : () => new RedisConnectionException(ConnectionFailureType.UnableToConnect, "It was not possible to connect to the redis server(s).");

        await Monitor.RunOnceAsync(Ct);

        Monitor.Last.SnapshotsUnreadable.Should().BeTrue();
        BySite(Monitor.Last).Should().BeEquivalentTo(new Dictionary<string, (int, int)> { [site] = (3, 2) },
            "the first display judged fresh before Redis stopped answering; the other two counted stale, as their boards answer 503");
        _snapshots.Reads.Should().BeEquivalentTo([$"{site}/Z1", $"{site}/Z2"], "Redis is not asked again that sweep once it failed");
        // Wiki 10 section 4.8 sends the operator to this Warning: once a sweep, the exception its only detail (CWE-532).
        var warning = _logs.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning && e.Category == typeof(SvcDisplayStaleness).FullName).Which;
        warning.Message.Should().Be("Display stale sweep could not read the live snapshots; every display not judged yet counts as stale");
        warning.Exception.Should().BeOfType(timesOut ? typeof(TimeoutException) : typeof(RedisConnectionException));
        warning.Properties.Should().Equal("{OriginalFormat}");
    }

    /// <summary>Redis's live snapshots, recording each zone read and failing with <see cref="Failure"/> once <see cref="FailFrom"/> reads have passed.</summary>
    private sealed class CountingSnapshots(ILiveSnapshotStore inner) : ILiveSnapshotStore
    {
        private readonly ConcurrentQueue<string> _reads = new();

        public int? FailFrom { get; set; }

        public Func<Exception> Failure { get; set; }

        /// <summary>Runs before the first read reaches Redis (a test's probe while the sweep is under way).</summary>
        public Func<Task> OnFirstRead { get; set; }

        public IReadOnlyList<string> Reads => [.. _reads];

        public void Clear() => _reads.Clear();

        public Task PublishAsync(IReadOnlyCollection<LiveZoneSnapshot> snapshots, CancellationToken ct) => inner.PublishAsync(snapshots, ct);

        public async Task<LiveZoneSnapshot> GetAsync(string zoneKey, CancellationToken ct)
        {
            _reads.Enqueue(zoneKey);
            if (_reads.Count == 1 && OnFirstRead is { } probe)
                await probe();
            return FailFrom is { } from && _reads.Count > from ? throw Failure() : await inner.GetAsync(zoneKey, ct);
        }

        public Task SubscribeAsync(Func<LiveZoneSnapshot, Task> handler, CancellationToken ct) => inner.SubscribeAsync(handler, ct);
    }

    /// <summary>The host's log entries: category, level, message, exception and the names of the entry's properties.</summary>
    private sealed class RecordingLogs : ILoggerProvider
    {
        public ConcurrentQueue<(string Category, LogLevel Level, string Message, Exception Exception, IReadOnlyList<string> Properties)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger(RecordingLogs logs, string category) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter) =>
                logs.Entries.Enqueue((category, logLevel, formatter(state, exception), exception,
                    state is IReadOnlyList<KeyValuePair<string, object>> properties ? [.. properties.Select(p => p.Key)] : []));
        }
    }
}
