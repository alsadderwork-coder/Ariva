using System.Text.Json;
using Ariva.Core;
using Ariva.Core.Reports;
using Ariva.Core.Services;
using Ariva.Core.Services.Reports;
using Ariva.Infra.Notifications;
using Ariva.Infra.Services.Reports;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Ariva.IntegrationTests.Reports;

/// <summary>
/// ARV-234 (CWE-400, CWE-120) against PostgreSQL 17 with TimescaleDB: the daily report's hours statement runs in one of the
/// report database slots (transaction advisory locks of class <see cref="ReportReader.SlotLockClass"/>, shared by every replica)
/// or reads nothing. Every slot held answers busy without a scan of the minutes, and the service answers and logs it; either slot
/// serves a report on demand; a scheduled delivery takes the last slot only and holds it to its commit, so deliveries at once hold
/// one between them and reports on demand keep the first; a delivery that finds the last in use is left pending for a later round;
/// and with every slot free a delivery another transaction holds is passed over at once (FOR UPDATE SKIP LOCKED), never sent
/// twice. Slots are held from connections of the test's own (without pooling, so a held lock ends with its connection). A database of
/// its own: advisory locks are per database.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DailyReportSlotsTests(PostgresFixture fixture) : IAsyncDisposable
{
    /// <summary>The host's clock is 2026-10-01 08:00 UTC; the sites have no airport, so their day is UTC's.</summary>
    private static readonly DateOnly Day = new(2026, 9, 30);

    private readonly HoldingTransport _transport = new();
    private readonly CapturedLogs _logs = new();
    private AccountsHost _host;

    private AccountsHost Host => _host ??= new AccountsHost(fixture, new Dictionary<string, string>
    {
        ["Email:Enabled"] = "true",
        ["Email:FromAddress"] = "no-reply@ariva.test"
    }, TestDatabase.ReportSlots, services =>
    {
        services.Replace(ServiceDescriptor.Singleton<IEmailTransport>(_transport));
        services.AddSingleton<ILoggerProvider>(_logs);
        services.TryAddScoped<ISvcReportDeliveries, SvcReportDeliveries>();
        services.TryAddSingleton<ReportDeliveryRound>();
    });

    public async ValueTask DisposeAsync()
    {
        _transport.Release();
        if (_host is not null)
            await _host.DisposeAsync();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Read_Should_AnswerBusyWithoutReadingTheMinutes_When_EverySlotIsHeld()
    {
        var admin = await SiteAsync("SLB", "it.slots.busy");
        Fluentx.Result<DailyReport> busy, service, served;
        int loops;
        await using (await HoldAsync(1, 2))
        {
            busy = await Host.AsCallerAsync(null, s => s.GetRequiredService<ReportReader>().ReadAsync("SLB", Day, [RoleCodes.SystemAdministrator], DateTime.UtcNow, Ct));
            loops = await PlanScanLoopsAsync("SLB");
            service = await Host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcReports>().DailyAsync("SLB", Day, Ct));
        }

        served = await Host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcReports>().DailyAsync("SLB", Day, Ct));

        busy.ErrorMessages.Should().Equal([ReportErrors.Busy], "every slot is held by another session");
        loops.Should().Be(0, "a statement without a slot scans none of the minutes");
        service.ErrorMessages.Should().Equal([ReportErrors.Busy]);
        _logs.Entries.Should().Contain(e => e.Contains("Daily report of site SLB refused: every report database slot is in use", StringComparison.Ordinal));
        served.HasErrors.Should().BeFalse(string.Join(" ", served.ErrorMessages ?? []));
        served.Data.Lanes.Should().ContainSingle().Which.Passengers.Should().Be(30, "the day is served once a slot frees");
        (await HeldSlotsAsync()).Should().BeEmpty("a report on demand holds its slot for its statement only");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Read_Should_ServeAReportOnDemand_When_TheOtherSlotIsFree(int held)
    {
        var admin = await SiteAsync("SL" + held, "it.slots.other" + held);
        Fluentx.Result<DailyReport> report;
        await using (await HoldAsync(held))
            report = await Host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcReports>().DailyAsync("SL" + held, Day, Ct));

        report.HasErrors.Should().BeFalse(string.Join(" ", report.ErrorMessages ?? []));
        report.Data.Lanes.Should().ContainSingle().Which.Passengers.Should().Be(30);
    }

    [Fact]
    public async Task Delivery_Should_HoldTheLastSlotUntilItsCommit_When_ItReadsItsReport()
    {
        var admin = await SiteAsync("SLD", "it.slots.delivery", "slots.delivery@ariva.test");
        var schedule = await ScheduleAsync(admin, "SLD", "Held to its commit");
        _transport.Hold();

        var round = Task.Run(RunAsync, Ct);
        await _transport.Entered.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        var during = await HeldSlotsAsync();
        var onDemand = await Host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcReports>().DailyAsync("SLD", Day, Ct));
        _transport.Release();
        var run = await round;

        during.Should().Equal([2], "the delivery's transaction holds the last slot while it sends");
        onDemand.HasErrors.Should().BeFalse("a report on demand takes the first slot meanwhile");
        run.Sent.Should().Be(1);
        (await HeldSlotsAsync()).Should().BeEmpty("the slot ends with the delivery's commit");
        (await StatusAsync(schedule)).Should().Be("Sent:1");
    }

    [Fact]
    public async Task Deliveries_Should_HoldOneSlotBetweenThem_When_TwoRoundsRunAtOnce()
    {
        var admin = await SiteAsync("SLT", "it.slots.two", "slots.two@ariva.test");
        var other = await UserAsync("it.slots.two.other", "slots.two.other@ariva.test");
        var schedule = await ScheduleAsync(admin, "SLT", "Two at once", other);
        _transport.Hold();

        var first = Task.Run(RunAsync, Ct);
        await _transport.Entered.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        var second = await RunAsync();
        var during = await HeldSlotsAsync();
        var onDemand = await Host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcReports>().DailyAsync("SLT", Day, Ct));
        _transport.Release();
        var firstRun = await first;

        (second.Sent + second.Failed).Should().Be(0, "the second round finds the deliveries' slot held and leaves its deliveries");
        during.Should().Equal([2], "rounds at once hold the last slot between them");
        onDemand.HasErrors.Should().BeFalse("a report on demand is served from the first slot");
        firstRun.Sent.Should().Be(2, "the first round sends both");
        (await StatusAsync(schedule)).Should().Be("Sent:1,Sent:1");
        _logs.Entries.Should().Contain(e => e.Contains("the deliveries' report database slot is in use, left for a later round", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Delivery_Should_StayPendingWithNoAttempt_When_TheDeliveriesSlotIsInUse()
    {
        var admin = await SiteAsync("SLP", "it.slots.pending", "slots.pending@ariva.test");
        var schedule = await ScheduleAsync(admin, "SLP", "Left for later");
        ReportDeliveryRun busy;
        Fluentx.Result<DailyReport> onDemand;
        await using (await HoldAsync(2))
        {
            busy = await RunAsync();
            onDemand = await Host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcReports>().DailyAsync("SLP", Day, Ct));
        }

        var pending = await StatusAsync(schedule);
        var later = await RunAsync();

        (busy.Sent + busy.Failed + busy.Skipped).Should().Be(0);
        pending.Should().Be("Pending:0", "nothing written and no attempt counted while the slot is in use");
        _logs.Entries.Should().Contain(e => e.Contains("the deliveries' report database slot is in use, left for a later round", StringComparison.Ordinal));
        onDemand.HasErrors.Should().BeFalse("reports on demand keep the first slot");
        later.Sent.Should().Be(1, "a later round sends it");
        (await StatusAsync(schedule)).Should().Be("Sent:1");
        _transport.SentTo("slots.pending@ariva.test").Should().Be(1);
    }

    [Fact]
    public async Task Claim_Should_PassOverADeliveryAnotherTransactionHolds_When_EverySlotIsFree()
    {
        var admin = await SiteAsync("SLC", "it.slots.claim", "slots.claim@ariva.test");
        var schedule = await ScheduleAsync(admin, "SLC", "Held elsewhere");
        // The delivery prepared without a send, so the row lock below is taken before any round claims it.
        await using (var scope = Host.Provider.CreateAsyncScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            (await scope.ServiceProvider.GetRequiredService<ISvcReportDeliveries>().PrepareAsync(Ct)).Should().ContainSingle();
            await unitOfWork.EndAsync(Ct);
        }

        await using (var row = new NpgsqlConnection(Unpooled(await Host.DatabaseAsync())))
        {
            await row.OpenAsync(Ct);
            await using var transaction = await row.BeginTransactionAsync(Ct);
            await using (var lockRow = new NpgsqlCommand("WITH held AS (SELECT id FROM report_delivery WHERE schedule_id = @id FOR UPDATE) SELECT count(*)::int FROM held", row, transaction))
            {
                lockRow.Parameters.AddWithValue("id", schedule);
                ((int)await lockRow.ExecuteScalarAsync(Ct)).Should().Be(1);
            }

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var passed = await RunAsync();
            clock.Stop();

            (passed.Sent + passed.Failed + passed.Skipped).Should().Be(0, "the held delivery is passed over, its report neither read nor sent");
            clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "SKIP LOCKED passes it over without waiting for the other transaction");
            _transport.Calls.Should().Be(0);
            await transaction.RollbackAsync(Ct);
        }

        var sent = await RunAsync();
        var again = await RunAsync();

        sent.Sent.Should().Be(1, "sent once the other transaction lets it go");
        (again.Sent + again.Failed + again.Skipped).Should().Be(0, "a sent delivery is never claimed again");
        _transport.SentTo("slots.claim@ariva.test").Should().Be(1);
        (await StatusAsync(schedule)).Should().Be("Sent:1");
    }

    #region Helpers

    private Task<ReportDeliveryRun> RunAsync() => Host.Provider.GetRequiredService<ReportDeliveryRound>().RunAsync(Ct);

    /// <summary>A site with one queue zone's ten minutes of the day (three entries a minute), and an administrator of all sites.</summary>
    private async Task<Guid> SiteAsync(string site, string userName, string email = null)
    {
        var admin = await Host.CreateUserAsync(userName, roles: [RoleCodes.SystemAdministrator], allSites: true);
        if (email is not null)
            (await Host.ReadAsync<int>("UPDATE \"user\" SET email = @secret WHERE id = @id RETURNING 1", admin, email)).Should().Be(1);
        await using var connection = new NpgsqlConnection(Unpooled(await Host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        await using var seed = new NpgsqlCommand("""
            WITH site AS (INSERT INTO site (id, code, name) VALUES (gen_random_uuid(), @site, 'Slots') RETURNING code)
            INSERT INTO queue_minute (zone_key, minute_utc, profile_version, status, entries, exits, waits, queue_length, wait_buckets, wait_counts, updated_on)
            SELECT @site || '/Q1', timestamptz '2026-09-30T10:00:00Z' + m * interval '1 minute', 1, 'Final', 3, 3, 3, 4, ARRAY[2, 6], ARRAY[2, 1], now()
            FROM generate_series(0, 9) m
            """, connection);
        seed.Parameters.AddWithValue("site", site);
        (await seed.ExecuteNonQueryAsync(Ct)).Should().Be(10);
        return admin;
    }

    private async Task<Guid> UserAsync(string userName, string email)
    {
        var user = await Host.CreateUserAsync(userName, roles: [RoleCodes.SystemAdministrator], allSites: true);
        (await Host.ReadAsync<int>("UPDATE \"user\" SET email = @secret WHERE id = @id RETURNING 1", user, email)).Should().Be(1);
        return user;
    }

    private async Task<Guid> ScheduleAsync(Guid admin, string site, string name, params Guid[] others) =>
        (await Host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcReportSchedules>()
            .CreateAsync(new ReportScheduleRequest(site, name, null, "06:00", [admin, .. others]), Ct))).Data.Id;

    private Task<string> StatusAsync(Guid schedule) =>
        Host.ReadAsync<string>("SELECT string_agg(status || ':' || attempts, ',' ORDER BY status, attempts) FROM report_delivery WHERE schedule_id = @id", schedule);

    private string Unpooled(string database) => new NpgsqlConnectionStringBuilder(fixture.ConnectionString(database)) { Pooling = false }.ConnectionString;

    /// <summary>The report slots another session holds, as session advisory locks on a connection of the test's own (closed to free them).</summary>
    private async Task<NpgsqlConnection> HoldAsync(params int[] slots)
    {
        var connection = new NpgsqlConnection(Unpooled(await Host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        foreach (var slot in slots)
        {
            await using var hold = new NpgsqlCommand("SELECT pg_advisory_lock(@class, @slot)", connection);
            hold.Parameters.AddWithValue("class", ReportReader.SlotLockClass);
            hold.Parameters.AddWithValue("slot", slot);
            await hold.ExecuteNonQueryAsync(Ct);
        }

        return connection;
    }

    /// <summary>The report slots held in this database by sessions other than the test's reads (the two-key form: classid, objid, objsubid 2).</summary>
    private async Task<List<int>> HeldSlotsAsync()
    {
        var held = await Host.ReadAsync<string>("""
            SELECT COALESCE(string_agg(objid::text, ',' ORDER BY objid), '') FROM pg_locks
            WHERE locktype = 'advisory' AND classid = 60 AND objsubid = 2 AND granted
              AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
            """);
        return [.. held.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse)];
    }

    /// <summary>The loops of every relation scan in the hours statement's plan as it runs now (EXPLAIN ANALYZE).</summary>
    private async Task<int> PlanScanLoopsAsync(string site)
    {
        await using var connection = new NpgsqlConnection(Unpooled(await Host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, FORMAT JSON) " + ReportReader.HoursSql, connection);
        var (from, to) = DailyReports.DayBounds(Day, TimeZoneInfo.Utc);
        explain.Parameters.AddWithValue("first", 1);
        explain.Parameters.AddWithValue("slots", DailyReportLimits.Default.DatabaseSlots);
        explain.Parameters.AddWithValue("lockClass", ReportReader.SlotLockClass);
        explain.Parameters.AddWithValue("prefix", site + "/%");
        explain.Parameters.AddWithValue("from", from);
        explain.Parameters.AddWithValue("to", to);
        explain.Parameters.AddWithValue("buckets", Ariva.Core.Queueing.WaitStatistics.Buckets);
        explain.Parameters.AddWithValue("cap", DailyReportLimits.Default.MaxZoneHours + 1);
        using var plan = JsonDocument.Parse((string)await explain.ExecuteScalarAsync(Ct));
        var scans = new List<int>();
        Scans(plan.RootElement[0].GetProperty("Plan"), scans);
        scans.Should().NotBeEmpty("the plan scans the minutes when it runs");
        return scans.Sum();
    }

    private static void Scans(JsonElement node, List<int> loops)
    {
        if (node.TryGetProperty("Relation Name", out var relation) && relation.GetString() is { } name && (name == "queue_minute" || name.Contains("chunk", StringComparison.Ordinal)))
            loops.Add(node.GetProperty("Actual Loops").GetInt32());
        if (node.TryGetProperty("Plans", out var children))
            foreach (var child in children.EnumerateArray())
                Scans(child, loops);
    }

    /// <summary>Records what the relay would send; after <see cref="Hold"/> the next send waits (at most 60 s) for <see cref="Release"/>.</summary>
    private sealed class HoldingTransport : IEmailTransport
    {
        private readonly Lock _gate = new();
        private readonly List<string> _sent = [];
        private TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _holding;
        private int _calls;

        public Task Entered => _entered.Task;

        public int Calls => Volatile.Read(ref _calls);

        public void Hold()
        {
            _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _holding, 1);
        }

        public void Release() => _release.TrySetResult();

        public int SentTo(string recipient)
        {
            lock (_gate)
                return _sent.Count(r => string.Equals(r, recipient, StringComparison.OrdinalIgnoreCase));
        }

        public async Task<EmailBatch> SendAsync(IReadOnlyList<OutgoingEmail> emails, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            if (Interlocked.CompareExchange(ref _holding, 0, 1) == 1)
            {
                _entered.TrySetResult();
                await _release.Task.WaitAsync(TimeSpan.FromSeconds(60), ct).ContinueWith(_ => { }, TaskScheduler.Default);
            }

            lock (_gate)
                _sent.AddRange(emails.Select(e => e.Recipient));
            return EmailBatch.Tried(new Dictionary<Guid, string>());
        }
    }

    private sealed class CapturedLogs : ILoggerProvider, ILogger
    {
        private readonly List<string> _entries = [];

        public IReadOnlyList<string> Entries
        {
            get
            {
                lock (_entries)
                    return [.. _entries];
            }
        }

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            lock (_entries)
                _entries.Add(formatter(state, exception));
        }

        public void Dispose()
        {
        }
    }

    #endregion
}
