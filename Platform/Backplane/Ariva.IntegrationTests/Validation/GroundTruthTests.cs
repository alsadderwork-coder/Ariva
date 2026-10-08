using System.Security.Cryptography;
using Ariva.Core;
using Ariva.Core.Domain.Constants;
using Ariva.Core.Domain.Criteria;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Services.Validation;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ariva.IntegrationTests.Validation;

/// <summary>
/// ARV-104b against PostgreSQL with script 0048, under a real runtime login as the hosts run: tracer batches corrected by the
/// device's measured clock offset (raw and corrected times stored, a batch beyond 5 minutes refused), desk batches of 15
/// minutes with corrections as revisions, Idempotency-Keys per observer, the border-only read of desk states, the campaign's
/// creator and starter refused, the close racing a batch (two locks in a known order), NotFound across sites, the keys and
/// triggers against statements that bypass the services, no change or delete by the runtime login, and a schema that holds
/// no person beyond an Ariva user id and no tracer beyond a label. After the first security review: no desk path for an
/// account that holds an airport role without BorderDesks.View (even with the observer role), device times far from the
/// device's clock refused without an overflow, 500 runs per observer and campaign, duplicates by exact pairs. Each test plants
/// a site of its own (database it_validation, shared with <see cref="ValidationTests"/> under another runtime login).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class GroundTruthTests(PostgresFixture fixture) : IAsyncDisposable
{
    private const string RuntimeLogin = "it_validation_truth_runtime";

    // One password for the class, so its tests share one connection pool of the runtime login (cleared after each test).
    private static readonly string RuntimePassword = "rt-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
    private AccountsHost _admin;
    private AccountsHost _runtime;
    private bool _usersCreated;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The hosts' clock: Thursday 1 October 2026 08:00 UTC, 12:00 in Dubai. The planned local days are 30 September and 1 October.
    private static readonly DateTime Now = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

    public async ValueTask DisposeAsync()
    {
        if (_runtime is not null)
        {
            string pooled = null;
            if (_usersCreated)
                pooled = _runtime.Provider.GetRequiredService<Ariva.Infra.Settings.DatabaseSettings>().BuildConnectionString();
            await _runtime.DisposeAsync();
            if (pooled is not null)
            {
                await using var connection = new NpgsqlConnection(pooled);
                NpgsqlConnection.ClearPool(connection);
            }
        }

        if (_admin is not null)
            await _admin.DisposeAsync();
    }

    #region Setup

    private sealed record Planted(string Site, Guid Profile, Guid QueueA, Guid QueueB, Guid EntryA, Guid D01, Guid D02, Guid EGate, Guid Counter);

    private async Task<AccountsHost> RuntimeAsync()
    {
        if (_runtime is not null)
            return _runtime;
        _admin = new AccountsHost(fixture, database: TestDatabase.Validation);
        var database = await _admin.DatabaseAsync();
        await using (var admin = new NpgsqlConnection(fixture.ConnectionString(database)))
        {
            await admin.OpenAsync(Ct);
            await using var ensure = new NpgsqlCommand("SELECT ariva_ensure_runtime_login(@login, @password)", admin);
            ensure.Parameters.AddWithValue("login", RuntimeLogin);
            ensure.Parameters.AddWithValue("password", RuntimePassword);
            await ensure.ExecuteNonQueryAsync(Ct);
        }

        _runtime = new AccountsHost(fixture, new Dictionary<string, string> { ["Database:Username"] = RuntimeLogin, ["Database:Password"] = RuntimePassword },
            TestDatabase.Validation);
        return _runtime;
    }

    private async Task ExecuteAsync([System.Diagnostics.CodeAnalysis.ConstantExpected] string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await _admin.DatabaseAsync()));
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // test helper: every caller passes a literal
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<List<string>> TextsAsync([System.Diagnostics.CodeAnalysis.ConstantExpected] string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await _admin.DatabaseAsync()));
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // test helper: every caller passes a literal
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        var texts = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
            texts.Add(reader.IsDBNull(0) ? null : reader.GetValue(0).ToString());
        return texts;
    }

    /// <summary>
    /// A site in Dubai with a published profile version 1 (queue zones Q-A with an entry line and Q-B), an immigration
    /// checkpoint with two staffed desks and an e-gate, and a check-in checkpoint with a counter.
    /// </summary>
    private async Task<Planted> PlantAsync(string site, string iata)
    {
        await RuntimeAsync();
        var p = new Planted(site, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            Guid.CreateVersion7(), Guid.CreateVersion7());
        await ExecuteAsync("""
            INSERT INTO site (id, code, name) VALUES (gen_random_uuid(), @site, @site);
            INSERT INTO airport (id, iata_code, name, time_zone_id) VALUES (gen_random_uuid(), @iata, @iata, 'Asia/Dubai');
            INSERT INTO terminal (id, airport_id, code, name, site_code) SELECT gen_random_uuid(), a.id, 'T1', 'T1', @site FROM airport a WHERE a.iata_code = @iata;
            INSERT INTO level (id, terminal_id, site_code, code, name, floor_number, width_metres, depth_metres)
            SELECT @level, t.id, @site, 'L1', 'L1', 0, 100, 100 FROM terminal t JOIN airport a ON a.id = t.airport_id WHERE a.iata_code = @iata;
            INSERT INTO checkpoint (id, level_id, site_code, code, name, kind) VALUES
                (@imm, @level, @site, 'IMM', 'Immigration', 'Immigration'),
                (@chk, @level, @site, 'CHK', 'Check-in', 'CheckIn');
            INSERT INTO desk (id, checkpoint_id, site_code, code, name, kind, lane_category_codes) VALUES
                (@d01, @imm, @site, 'D01', 'D01', 'Desk', 'CIT'),
                (@d02, @imm, @site, 'D02', 'D02', 'Desk', 'VIS'),
                (@eg, @imm, @site, 'EG01', 'EG01', 'EGate', 'EG'),
                (@c01, @chk, @site, 'C01', 'C01', 'Counter', NULL);
            INSERT INTO zone_profile (id, site_code, name, status) VALUES (@profile, @site, 'Profile', 'Draft');
            INSERT INTO zone (id, profile_id, name, kind, level_id, polygon, queue_zone_id) VALUES
                (@qa, @profile, 'Q-A', 'Queue', @level, '[]', NULL),
                (@qb, @profile, 'Q-B', 'Queue', @level, '[]', NULL);
            INSERT INTO line (id, profile_id, name, role, zone_id, level_id, start_x, start_y, end_x, end_y) VALUES
                (@entryA, @profile, 'Entry A', 'Entry', @qa, @level, 0, 0, 0, 1);
            UPDATE zone_profile SET status = 'Published', version = 1, geometry_hash = repeat('c', 64), published_on = '2026-09-01T00:00:00Z', published_by = 'it'
             WHERE id = @profile;
            """, ("site", site), ("iata", iata), ("level", Guid.CreateVersion7()), ("imm", Guid.CreateVersion7()), ("chk", Guid.CreateVersion7()),
            ("d01", p.D01), ("d02", p.D02), ("eg", p.EGate), ("c01", p.Counter), ("profile", p.Profile), ("qa", p.QueueA), ("qb", p.QueueB), ("entryA", p.EntryA));
        return p;
    }

    private Task<Guid> UserAsync(string role, params string[] sites) => UserWithRolesAsync([role], sites);

    private async Task<Guid> UserWithRolesAsync(string[] roles, params string[] sites)
    {
        var host = await RuntimeAsync();
        _usersCreated = true;
        // Three letters per role keep the user name unique within its 40 characters.
        var id = await host.CreateUserAsync($"it.gt.{string.Concat(roles.Select(r => r[..3])).ToLowerInvariant()}.{Guid.NewGuid():N}"[..40], roles: roles,
            allSites: sites.Length == 0);
        foreach (var site in sites)
            await ExecuteAsync("INSERT INTO user_site (id, user_id, site_code) VALUES (gen_random_uuid(), @user, @site)", ("user", id), ("site", site));
        return id;
    }

    private async Task<NpgsqlConnection> RuntimeConnectionAsync()
    {
        var builder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString(await _admin.DatabaseAsync(), RuntimeLogin, RuntimePassword)) { Pooling = false };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(Ct);
        return connection;
    }

    /// <summary>Waits until <paramref name="sessions"/> sessions wait on a lock <paramref name="holder"/> holds, or <paramref name="waiter"/> finished, within 30 seconds.</summary>
    private async Task WaitUntilBlockedAsync(int holder, Task waiter, int sessions = 1)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && !waiter.IsCompleted)
        {
            var waiting = await TextsAsync("SELECT count(*) FROM pg_stat_activity WHERE @holder = ANY(pg_blocking_pids(pid))", ("holder", holder));
            if (int.Parse(waiting[0], System.Globalization.CultureInfo.InvariantCulture) >= sessions)
                return;
            await Task.Delay(25, Ct);
        }

        waiter.IsCompleted.Should().BeFalse("the second statement waits on the first one's lock");
        throw new TimeoutException("No session waited on the lock within 30 seconds.");
    }

    private static CreateValidationCampaignRequest Request(Planted p, string name = "Ground truth", IReadOnlyList<Guid> desks = null) =>
        new(name, 1, [p.QueueA], [p.EntryA], ["2026-10-01", "2026-09-30"], null, 12, desks ?? [p.D01, p.D02]);

    private Task<T> Campaigns<T>(Guid caller, Func<ISvcValidationCampaigns, Task<T>> work) =>
        _runtime.AsCallerAsync(caller, s => work(s.GetRequiredService<ISvcValidationCampaigns>()));

    private Task<T> Tracers<T>(Guid caller, Func<ISvcTracerRuns, Task<T>> work) =>
        _runtime.AsCallerAsync(caller, s => work(s.GetRequiredService<ISvcTracerRuns>()));

    private Task<T> Desks<T>(Guid caller, Func<ISvcDeskObservations, Task<T>> work) =>
        _runtime.AsCallerAsync(caller, s => work(s.GetRequiredService<ISvcDeskObservations>()));

    private Task<T> Capture<T>(Guid caller, Func<ISvcValidationCapture, Task<T>> work) =>
        _runtime.AsCallerAsync(caller, s => work(s.GetRequiredService<ISvcValidationCapture>()));

    /// <summary>A batch from a device <paramref name="aheadMs"/> ahead of the server, with runs on that device's clock.</summary>
    private static CaptureTracerRunsRequest Batch(int aheadMs, params TracerRunRequest[] runs) =>
        new(Iso(Now.AddMilliseconds(aheadMs)), runs);

    private static TracerRunRequest Run(Guid zone, string code, DateTime joined, DateTime exited, bool abandoned = false) =>
        new(zone, code, Iso(joined), Iso(exited), abandoned);

    private static string Iso(DateTime value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A UTC instant around the hosts' clock: days 28 to 30 are in September 2026, the others in October.</summary>
    private static DateTime Utc(int day, int hour, int minute, int second = 0, int millisecond = 0) =>
        new(2026, day >= 28 ? 9 : 10, day, hour, minute, second, millisecond, DateTimeKind.Utc);

    /// <summary>A desk batch for the bin starting at <paramref name="bin"/> (UTC): per desk, its states by minute (null for minutes not observed).</summary>
    private static CaptureDeskObservationsRequest DeskBatch(string bin, params (Guid Desk, (int Minute, string State)[] States)[] desks) =>
        new(bin, [.. desks.Select(d =>
        {
            var states = new string[15];
            foreach (var (minute, state) in d.States)
                states[minute] = state;
            return new DeskMinutesRequest(d.Desk, states);
        })]);

    #endregion

    #region Tracer runs

    [Fact]
    public async Task TracerBatch_Should_StoreRawAndCorrectedTimes_When_TheDeviceClockIsOff()
    {
        var p = await PlantAsync("GT1", "GTA");
        var manager = await UserAsync(RoleCodes.BorderShiftSupervisor, "GT1");
        var observer = await UserAsync(RoleCodes.ValidationObserver, "GT1");
        var second = await UserAsync(RoleCodes.ValidationObserver, "GT1");
        var campaign = (await Campaigns(manager, s => s.CreateAsync("GT1", Request(p), Ct))).Data;
        await Campaigns(manager, s => s.StartAsync("GT1", campaign.Id, Ct));
        var running = (await _runtime.AsCallerAsync(observer, s => s.GetRequiredService<ISvcValidationCapture>().RunningAsync("GT1", Ct))).Data.Should().ContainSingle().Subject;
        running.Zones.Select(z => z.Name).Should().Equal("Q-A");
        running.Desks.Select(d => (d.Checkpoint, d.Code)).Should().Equal(("IMM", "D01"), ("IMM", "D02"));
        running.MaxClockOffsetSeconds.Should().Be(300);

        // The device is 42.5 s ahead: T-07 joined at its 07:10:42.500 (07:10:00 on the server) and left at its 07:28:12.500
        // (07:27:30); T-08 left without being served.
        const string key = "tablet-07:GT1-tracers-1";
        var batch = Batch(42_500, Run(p.QueueA, "T-07", Utc(1, 7, 10, 42, 500), Utc(1, 7, 28, 12, 500)), Run(p.QueueA, "T-08", Utc(1, 7, 20, 42, 500), Utc(1, 7, 31, 42, 500), true));
        var sent = await Tracers(observer, s => s.CaptureAsync("GT1", campaign.Id, batch, key, Ct));
        sent.HasErrors.Should().BeFalse(string.Join(" ", sent.ErrorMessages ?? []));
        (sent.Data.Replayed, sent.Data.Batch.ClockOffsetMs, sent.Data.Batch.ReceivedUtc, sent.Data.Batch.ObserverId).Should().Be((false, 42_500, Now, observer));
        var t07 = sent.Data.Batch.Runs.Single(r => r.TracerCode == "T-07");
        (t07.JoinedUtc, t07.ExitedUtc, t07.WaitSeconds, t07.Abandoned, t07.ZoneName).Should().Be((Utc(1, 7, 10), Utc(1, 7, 27, 30), 1050d, false, "Q-A"));
        (t07.JoinedRawUtc, t07.ExitedRawUtc, t07.ClockOffsetMs).Should().Be((Utc(1, 7, 10, 42, 500), Utc(1, 7, 28, 12, 500), 42_500));
        sent.Data.Batch.Runs.Single(r => r.TracerCode == "T-08").Abandoned.Should().BeTrue();
        (await TextsAsync("""
            SELECT tracer_code || ' ' || to_char(joined_raw_utc AT TIME ZONE 'UTC', 'HH24:MI:SS.MS') || ' ' || to_char(joined_utc AT TIME ZONE 'UTC', 'HH24:MI:SS.MS')
                   || ' ' || clock_offset_ms || ' ' || abandoned
            FROM tracer_run WHERE campaign_id = @id ORDER BY tracer_code
            """, ("id", campaign.Id))).Should().Equal("T-07 07:10:42.500 07:10:00.000 42500 false", "T-08 07:20:42.500 07:20:00.000 42500 true");

        // Resent with the same key (the device re-reads its clock): the stored batch, with the offset measured the first time.
        var resent = await Tracers(observer, s => s.CaptureAsync("GT1", campaign.Id, batch with { DeviceClockUtc = Iso(Now.AddSeconds(50)) }, key, Ct));
        (resent.Data.Replayed, resent.Data.Batch.Id, resent.Data.Batch.ClockOffsetMs, resent.Data.Batch.Runs.Count).Should().Be((true, sent.Data.Batch.Id, 42_500, 2));
        (await Tracers(observer, s => s.CaptureAsync("GT1", campaign.Id, Batch(0, Run(p.QueueA, "T-09", Utc(1, 7, 0), Utc(1, 7, 5))), key, Ct)))
            .ErrorMessages.Should().Equal(ValidationErrors.KeyReused);
        (await Tracers(observer, s => s.CaptureAsync("GT1", campaign.Id, batch, null, Ct))).ErrorMessages.Should().Equal(ValidationErrors.IdempotencyKeyRequired);
        (await Tracers(observer, s => s.CaptureAsync("GT1", campaign.Id, batch, "tablet-07:GT1-tracers-2", Ct))).ErrorMessages.Should().Equal(ValidationErrors.RunAlreadyRecorded);
        // The same key from another observer is that observer's own batch (a key belongs to its observer, CWE-863).
        var other = await Tracers(second, s => s.CaptureAsync("GT1", campaign.Id, batch, key, Ct));
        (other.Data.Replayed, other.Data.Batch.ObserverId).Should().Be((false, second));
        other.Data.Batch.Id.Should().NotBe(sent.Data.Batch.Id);

        // Refused (400, nothing stored): a device more than 5 minutes off, a join on an unplanned day, an exit in the future, a
        // zone out of scope, a name as the tracer code.
        var refusals = new (CaptureTracerRunsRequest Batch, string Error)[]
        {
            (Batch(300_001, Run(p.QueueA, "T-10", Utc(1, 7, 0), Utc(1, 7, 5))), ValidationErrors.ClockOffsetTooLarge),
            (Batch(-3_600_000, Run(p.QueueA, "T-10", Utc(1, 7, 0), Utc(1, 7, 5))), ValidationErrors.ClockOffsetTooLarge),
            (Batch(0, Run(p.QueueA, "T-10", Utc(29, 19, 50), Utc(29, 20, 5))), ValidationErrors.RunOutsideCampaign),
            (Batch(0, Run(p.QueueA, "T-10", Utc(1, 7, 50), Utc(1, 8, 1, 1))), ValidationErrors.TimeInFuture),
            (Batch(0, Run(p.QueueB, "T-10", Utc(1, 7, 0), Utc(1, 7, 5))), ValidationErrors.ZoneNotInScope),
            (Batch(0, Run(p.QueueA, "Omar", Utc(1, 7, 0), Utc(1, 7, 5))), ValidationErrors.InvalidTracerCode)
        };
        for (var i = 0; i < refusals.Length; i++)
        {
            var (refused, error) = refusals[i];
            (await Tracers(observer, s => s.CaptureAsync("GT1", campaign.Id, refused, $"tablet-07:GT1-refused-{i}", Ct))).ErrorMessages.Should().Equal([error], error);
        }

        // The manager reads every observer's runs; an observer only its own; the campaign shows the runs per zone.
        var all = await Tracers(manager, s => s.SearchAsync("GT1", campaign.Id, new TracerRunCriteria { SortBy = "recordedUtc" }, Ct));
        all.Data.TotalCount.Should().Be(4);
        var own = await Tracers(second, s => s.OwnRunsAsync("GT1", campaign.Id, new TracerRunCriteria { ObserverId = observer }, Ct));
        own.Data.Data.Should().HaveCount(2).And.OnlyContain(r => r.ObserverId == second, "an observer reads its own runs whatever observer the query names");
        (await Tracers(manager, s => s.SearchAsync("GT1", campaign.Id, new TracerRunCriteria { TracerCode = "T-08" }, Ct))).Data.TotalCount.Should().Be(2);
        (await Tracers(manager, s => s.SearchAsync("GT1", campaign.Id, new TracerRunCriteria { TracerCode = "' OR '1'='1" }, Ct))).ErrorMessages.Should().Equal(ValidationErrors.InvalidTracerCode);
        (await Tracers(manager, s => s.SearchAsync("GT1", campaign.Id, new TracerRunCriteria { SortBy = "joined_utc; DROP TABLE tracer_run" }, Ct))).ErrorMessages.Should().Equal(ValidationErrors.InvalidSort);
        (await Campaigns(manager, s => s.GetAsync("GT1", campaign.Id, Ct))).Data.Zones.Single().TracerRuns.Should().Be(4);
        (await TextsAsync("SELECT count(*) FROM tracer_batch WHERE campaign_id = @id", ("id", campaign.Id))).Should().Equal("2");
    }

    [Fact]
    public async Task TracerBatch_Should_RefuseFarDeviceTimesAndStopAtFiveHundredRuns_When_AnObserverKeepsSending()
    {
        // First security review of ARV-104b: (CWE-501) a well-formed but absurd device time is refused before the correction,
        // which used to overflow (500); (CWE-770) at most 500 runs per observer and campaign; a duplicate is the exact tracer
        // code and device join time pair, compared by the database.
        var p = await PlantAsync("GT10", "GTJ");
        var manager = await UserAsync(RoleCodes.BorderShiftSupervisor, "GT10");
        var observer = await UserAsync(RoleCodes.ValidationObserver, "GT10");
        var second = await UserAsync(RoleCodes.ValidationObserver, "GT10");
        var campaign = (await Campaigns(manager, s => s.CreateAsync("GT10", Request(p), Ct))).Data;
        await Campaigns(manager, s => s.StartAsync("GT10", campaign.Id, Ct));

        var far = new (CaptureTracerRunsRequest Batch, string Error, string Why)[]
        {
            (Batch(2_000, Run(p.QueueA, "T-01", new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(1, 1, 1, 1, 0, 0, DateTimeKind.Utc))),
                ValidationErrors.RunOutsideCampaign, "year 1, device ahead"),
            (Batch(-2_000, Run(p.QueueA, "T-01", new DateTime(9999, 12, 31, 23, 0, 0, DateTimeKind.Utc), new DateTime(9999, 12, 31, 23, 59, 59, 999, DateTimeKind.Utc))),
                ValidationErrors.TimeInFuture, "year 9999, device behind"),
            (Batch(0, Run(p.QueueA, "T-01", Now.AddDays(-32).AddMinutes(-1), Now.AddDays(-32).AddMinutes(9))), ValidationErrors.RunOutsideCampaign,
                "joined more than 32 days before the device's clock"),
            (Batch(0, Run(p.QueueA, "T-01", Now.AddMinutes(-9), Now.AddMinutes(1).AddMilliseconds(1))), ValidationErrors.TimeInFuture,
                "exited more than a minute after the device's clock")
        };
        for (var i = 0; i < far.Length; i++)
        {
            var (batch, error, why) = far[i];
            var refused = () => Tracers(observer, s => s.CaptureAsync("GT10", campaign.Id, batch, $"far-{i:D2}:GT10-run", Ct));
            (await refused.Should().NotThrowAsync(why)).Which.ErrorMessages.Should().Equal([error], why);
        }

        // Exact pairs: T-07 and T-08 with their join times swapped are two new runs; the same pair again is 409.
        (await Tracers(second, s => s.CaptureAsync("GT10", campaign.Id,
            Batch(0, Run(p.QueueA, "T-07", Utc(1, 7, 0), Utc(1, 7, 5)), Run(p.QueueA, "T-08", Utc(1, 7, 1), Utc(1, 7, 6))), "pairs-01:GT10-run", Ct))).HasErrors.Should().BeFalse();
        (await Tracers(second, s => s.CaptureAsync("GT10", campaign.Id,
            Batch(0, Run(p.QueueA, "T-07", Utc(1, 7, 1), Utc(1, 7, 6)), Run(p.QueueA, "T-08", Utc(1, 7, 0), Utc(1, 7, 5))), "pairs-02:GT10-run", Ct)))
            .HasErrors.Should().BeFalse("a code and a join time of different runs are no duplicate");
        (await Tracers(second, s => s.CaptureAsync("GT10", campaign.Id,
            Batch(0, Run(p.QueueA, "T-09", Utc(1, 7, 2), Utc(1, 7, 7)), Run(p.QueueA, "T-07", Utc(1, 7, 0), Utc(1, 7, 5))), "pairs-03:GT10-run", Ct)))
            .ErrorMessages.Should().Equal(ValidationErrors.RunAlreadyRecorded);

        // 499 runs of the observer planted directly, then one more through the service (500), then none.
        var seeded = Guid.CreateVersion7();
        await ExecuteAsync("""
            INSERT INTO tracer_batch (id, campaign_id, site_code, observer_id, idempotency_key, request_hash, device_clock_utc, received_utc, clock_offset_ms, runs)
            VALUES (@batch, @campaign, 'GT10', @observer, 'seeded-01:GT10-runs', repeat('a', 64), '2026-10-01T07:59:00Z', '2026-10-01T07:59:00Z', 0, 20);
            INSERT INTO tracer_run (id, batch_id, campaign_id, site_code, zone_id, tracer_code, observer_id, joined_raw_utc, exited_raw_utc, clock_offset_ms, joined_utc,
                                    exited_utc, abandoned, recorded_utc)
            SELECT gen_random_uuid(), @batch, @campaign, 'GT10', @zone, 'T-99', @observer,
                   timestamptz '2026-10-01T05:00:00Z' + n * interval '1 second', timestamptz '2026-10-01T05:10:00Z' + n * interval '1 second', 0,
                   timestamptz '2026-10-01T05:00:00Z' + n * interval '1 second', timestamptz '2026-10-01T05:10:00Z' + n * interval '1 second', false,
                   '2026-10-01T07:59:00Z'
            FROM generate_series(1, 499) AS n
            """, ("batch", seeded), ("campaign", campaign.Id), ("observer", observer), ("zone", p.QueueA));
        (await Tracers(observer, s => s.CaptureAsync("GT10", campaign.Id, Batch(0, Run(p.QueueA, "T-01", Utc(1, 7, 0), Utc(1, 7, 5))), "cap-01:GT10-run", Ct)))
            .HasErrors.Should().BeFalse("the 500th run");
        (await Tracers(observer, s => s.CaptureAsync("GT10", campaign.Id, Batch(0, Run(p.QueueA, "T-02", Utc(1, 7, 0), Utc(1, 7, 5))), "cap-02:GT10-run", Ct)))
            .ErrorMessages.Should().Equal(ValidationErrors.TooManyRuns);
        (await Tracers(second, s => s.CaptureAsync("GT10", campaign.Id, Batch(0, Run(p.QueueA, "T-02", Utc(1, 7, 0), Utc(1, 7, 5))), "cap-03:GT10-run", Ct)))
            .HasErrors.Should().BeFalse("the bound is per observer");
        (await TextsAsync("SELECT count(*) FROM tracer_run WHERE campaign_id = @id AND observer_id = @observer", ("id", campaign.Id), ("observer", observer)))
            .Should().Equal("500");
    }

    #endregion

    #region Desk observations

    [Fact]
    public async Task DeskBatch_Should_RecordCorrectAndShowStatesToBorderRolesOnly_When_AnObserverLogsDesks()
    {
        var p = await PlantAsync("GT2", "GTB");
        var supervisor = await UserAsync(RoleCodes.BorderShiftSupervisor, "GT2");
        var dutyManager = await UserAsync(RoleCodes.TerminalDutyManager, "GT2");
        var observer = await UserAsync(RoleCodes.ValidationObserver, "GT2");
        var second = await UserAsync(RoleCodes.ValidationObserver, "GT2");

        // Desks are border data: a duty manager may not put them in scope (403), nor an airport counter or an e-gate be there.
        (await Campaigns(dutyManager, s => s.CreateAsync("GT2", Request(p), Ct))).ErrorMessages.Should().Equal(ValidationErrors.DesksNeedBorderRole);
        (await Campaigns(supervisor, s => s.CreateAsync("GT2", Request(p, desks: [p.D01, p.Counter]), Ct))).ErrorMessages.Should().Equal(ValidationErrors.InvalidDesks);
        (await Campaigns(supervisor, s => s.CreateAsync("GT2", Request(p, desks: [p.EGate]), Ct))).ErrorMessages.Should().Equal(ValidationErrors.InvalidDesks);
        var withoutDesks = await Campaigns(dutyManager, s => s.CreateAsync("GT2", Request(p, name: "No desks", desks: []), Ct));
        withoutDesks.HasErrors.Should().BeFalse("a campaign without desks is anyone's to plan");
        var created = await Campaigns(supervisor, s => s.CreateAsync("GT2", Request(p), Ct));
        created.HasErrors.Should().BeFalse(string.Join(" ", created.ErrorMessages ?? []));
        var campaign = created.Data;
        campaign.DesksIncluded.Should().BeTrue();
        campaign.Desks.Select(d => (d.Checkpoint, d.Code, d.MinutesObserved)).Should().Equal(("IMM", "D01", 0), ("IMM", "D02", 0));
        var asDutyManager = (await Campaigns(dutyManager, s => s.GetAsync("GT2", campaign.Id, Ct))).Data;
        (asDutyManager.DesksIncluded, asDutyManager.Desks.Count).Should().Be((false, 0));
        await Campaigns(dutyManager, s => s.StartAsync("GT2", campaign.Id, Ct));

        // The 07:00 bin (11:00 in Dubai): D01 serving then idle, D02 paused for a minute.
        const string key = "tablet-07:GT2-desks-0700";
        var batch = DeskBatch("2026-10-01T07:00:00Z", (p.D01, [(0, "Serving"), (1, "Serving"), (14, "Idle")]), (p.D02, [(3, "Paused")]));
        var sent = await Desks(observer, s => s.CaptureAsync("GT2", campaign.Id, batch, key, Ct));
        sent.HasErrors.Should().BeFalse(string.Join(" ", sent.ErrorMessages ?? []));
        sent.Data.Batch.Observations.Select(o => (o.DeskCode, o.MinuteUtc, o.State, o.Revision)).Should().Equal(
            ("D01", Utc(1, 7, 0), "Serving", 1), ("D01", Utc(1, 7, 1), "Serving", 1), ("D01", Utc(1, 7, 14), "Idle", 1), ("D02", Utc(1, 7, 3), "Paused", 1));
        var resent = await Desks(observer, s => s.CaptureAsync("GT2", campaign.Id, batch, key, Ct));
        (resent.Data.Replayed, resent.Data.Batch.Id, resent.Data.Batch.Observations.Count).Should().Be((true, sent.Data.Batch.Id, 4));
        (await Desks(observer, s => s.CaptureAsync("GT2", campaign.Id, DeskBatch("2026-10-01T07:00:00Z", (p.D01, [(0, "Idle")])), key, Ct)))
            .ErrorMessages.Should().Equal(ValidationErrors.KeyReused);
        (await Desks(observer, s => s.CaptureAsync("GT2", campaign.Id, DeskBatch("2026-10-01T07:00:00Z", (p.D01, [(1, "Idle")])), "tablet-07:GT2-again", Ct)))
            .ErrorMessages.Should().Equal(ValidationErrors.AlreadyObserved);
        (await Desks(observer, s => s.CaptureAsync("GT2", campaign.Id, DeskBatch("2026-10-01T07:00:00Z", (p.D01, [(2, "Idle")])), "tablet-07:GT2-rest", Ct)))
            .HasErrors.Should().BeFalse("another minute of the same bin");
        (await Desks(second, s => s.CaptureAsync("GT2", campaign.Id, batch, key, Ct))).Data.Replayed.Should().BeFalse("a key belongs to its observer");
        (await Desks(observer, s => s.CaptureAsync("GT2", campaign.Id, batch, null, Ct))).ErrorMessages.Should().Equal(ValidationErrors.IdempotencyKeyRequired);

        // Refused (400): a desk out of scope, a minute on an unplanned day, a minute in the future, a state that is not one of four.
        (await Desks(observer, s => s.CaptureAsync("GT2", campaign.Id, DeskBatch("2026-10-01T06:45:00Z", (p.EGate, [(0, "Idle")])), "tablet-07:GT2-r1", Ct)))
            .ErrorMessages.Should().Equal(ValidationErrors.DeskNotInScope);
        (await Desks(observer, s => s.CaptureAsync("GT2", campaign.Id, DeskBatch("2026-09-29T19:45:00Z", (p.D01, [(0, "Idle")])), "tablet-07:GT2-r2", Ct)))
            .ErrorMessages.Should().Equal(ValidationErrors.BinOutsideCampaign);
        (await Desks(observer, s => s.CaptureAsync("GT2", campaign.Id, DeskBatch("2026-10-01T08:00:00Z", (p.D01, [(1, "Idle")])), "tablet-07:GT2-r3", Ct)))
            .ErrorMessages.Should().Equal(ValidationErrors.TimeInFuture);
        (await Desks(observer, s => s.CaptureAsync("GT2", campaign.Id, DeskBatch("2026-10-01T06:45:00Z", (p.D01, [(0, "Unknown")])), "tablet-07:GT2-r4", Ct)))
            .ErrorMessages.Should().Equal(ValidationErrors.InvalidState);

        // A correction is the next revision with a reason, audited; only one's own latest.
        var first = sent.Data.Batch.Observations[0];
        var corrected = await Desks(observer, s => s.CorrectAsync("GT2", campaign.Id, first.Id, new CorrectDeskObservationRequest("Idle", "Tapped the wrong row"), "tablet-07:GT2-fix", Ct));
        corrected.HasErrors.Should().BeFalse(string.Join(" ", corrected.ErrorMessages ?? []));
        (corrected.Data.Observation.Revision, corrected.Data.Observation.State, corrected.Data.Observation.CorrectsId, corrected.Data.Observation.BatchId).Should()
            .Be((2, "Idle", first.Id, null));
        (await Desks(observer, s => s.CorrectAsync("GT2", campaign.Id, first.Id, new CorrectDeskObservationRequest("Idle", "Tapped the wrong row"), "tablet-07:GT2-fix", Ct)))
            .Data.Replayed.Should().BeTrue();
        (await Desks(observer, s => s.CorrectAsync("GT2", campaign.Id, first.Id, new CorrectDeskObservationRequest("Closed", "Again"), null, Ct))).ErrorMessages.Should().Equal(ValidationErrors.NotLatest);
        (await Desks(second, s => s.CorrectAsync("GT2", campaign.Id, corrected.Data.Observation.Id, new CorrectDeskObservationRequest("Closed", "Not mine"), null, Ct)))
            .ErrorMessages.Should().Equal(ValidationErrors.NotFound);
        (await TextsAsync("SELECT after_summary FROM audit_entry WHERE action = 'DeskObservation.Corrected' AND target_id = @id", ("id", corrected.Data.Observation.Id)))
            .Should().ContainSingle().Which.Should().Contain($"observer={observer}").And.Contain("desk=IMM/D01").And.Contain("state=Idle").And.Contain("\"Tapped the wrong row\"");

        // Border callers read every observer's states; a duty manager gets nothing (desk-level data is border data).
        var border = await Desks(supervisor, s => s.SearchAsync("GT2", campaign.Id, new DeskObservationCriteria { DeskId = p.D01, ObserverId = observer }, Ct));
        (border.Data.DesksIncluded, border.Data.TotalCount).Should().Be((true, 4));
        border.Data.Data.Single(o => o.MinuteUtc == Utc(1, 7, 0)).Should().Match<Core.Domain.ViewModels.DeskObservationViewModel>(o => o.Revision == 2 && o.Current);
        var history = await Desks(supervisor, s => s.SearchAsync("GT2", campaign.Id, new DeskObservationCriteria { CurrentOnly = false, DeskId = p.D01, ObserverId = observer }, Ct));
        history.Data.TotalCount.Should().Be(5);
        var airport = await Desks(dutyManager, s => s.SearchAsync("GT2", campaign.Id, new DeskObservationCriteria(), Ct));
        (airport.Data.DesksIncluded, airport.Data.TotalCount, airport.Data.Data.Count).Should().Be((false, 0, 0));
        var own = await Desks(second, s => s.OwnObservationsAsync("GT2", campaign.Id, new DeskObservationCriteria { ObserverId = observer }, Ct));
        own.Data.Data.Should().OnlyContain(o => o.ObserverId == second);
        (await Campaigns(supervisor, s => s.GetAsync("GT2", campaign.Id, Ct))).Data.Desks.Select(d => d.MinutesObserved).Should().Equal(4, 1);

        // Closed: nothing is captured or corrected any more.
        await Campaigns(supervisor, s => s.CloseAsync("GT2", campaign.Id, Ct));
        (await Desks(observer, s => s.CaptureAsync("GT2", campaign.Id, DeskBatch("2026-10-01T06:45:00Z", (p.D01, [(0, "Idle")])), "tablet-07:GT2-late", Ct)))
            .ErrorMessages.Should().Equal(ValidationErrors.Closed);
        (await Desks(observer, s => s.CorrectAsync("GT2", campaign.Id, corrected.Data.Observation.Id, new CorrectDeskObservationRequest("Closed", "Late"), null, Ct)))
            .ErrorMessages.Should().Equal(ValidationErrors.Closed);
        (await Tracers(observer, s => s.CaptureAsync("GT2", campaign.Id, Batch(0, Run(p.QueueA, "T-01", Utc(1, 7, 0), Utc(1, 7, 5))), "tablet-07:GT2-run", Ct)))
            .ErrorMessages.Should().Equal(ValidationErrors.Closed);
    }

    [Fact]
    public async Task DeskPaths_Should_RefuseAnAirportRoleWithoutBorderDesks_When_ItAlsoHoldsTheObserverRole()
    {
        // First security review of ARV-104b (CWE-863, data boundary, option (b)): an account that holds an airport role and the
        // Validation observer role gets no desk in the capture view and 403 on desk batches, corrections and its own desk
        // reads; a pure observer (the border's own) and a border role with the observer role keep them.
        var p = await PlantAsync("GT9", "GTI");
        var supervisor = await UserAsync(RoleCodes.BorderShiftSupervisor, "GT9");
        var observer = await UserAsync(RoleCodes.ValidationObserver, "GT9");
        var dutyObserver = await UserWithRolesAsync([RoleCodes.TerminalDutyManager, RoleCodes.ValidationObserver], "GT9");
        var handlerObserver = await UserWithRolesAsync([RoleCodes.HandlerStationManager, RoleCodes.ValidationObserver], "GT9");
        var borderObserver = await UserWithRolesAsync([RoleCodes.BorderShiftSupervisor, RoleCodes.ValidationObserver], "GT9");
        var campaign = (await Campaigns(supervisor, s => s.CreateAsync("GT9", Request(p), Ct))).Data;
        await Campaigns(supervisor, s => s.StartAsync("GT9", campaign.Id, Ct));

        // The pure observer sees the desks to log, logs, corrects and reads back.
        var listed = (await Capture(observer, s => s.RunningAsync("GT9", Ct))).Data.Should().ContainSingle().Subject;
        listed.DesksIncluded.Should().BeTrue();
        listed.Desks.Select(d => d.Code).Should().Equal("D01", "D02");
        var sent = await Desks(observer, s => s.CaptureAsync("GT9", campaign.Id, DeskBatch("2026-10-01T07:00:00Z", (p.D01, [(0, "Serving")])), "obs-01:GT9-desk", Ct));
        sent.HasErrors.Should().BeFalse(string.Join(" ", sent.ErrorMessages ?? []));
        var observation = sent.Data.Batch.Observations[0];
        (await Desks(observer, s => s.CorrectAsync("GT9", campaign.Id, observation.Id, new CorrectDeskObservationRequest("Idle", "Tapped the wrong row"), null, Ct)))
            .HasErrors.Should().BeFalse();
        (await Desks(observer, s => s.OwnObservationsAsync("GT9", campaign.Id, new DeskObservationCriteria { CurrentOnly = false }, Ct))).Data.TotalCount.Should().Be(2);

        // An airport role with the observer role: the lines and zones but no desk, 403 on every desk path (also for a campaign
        // that does not exist: decided before any row is read), and its tracer runs, zone data, still count.
        foreach (var airport in new[] { dutyObserver, handlerObserver })
        {
            var view = (await Capture(airport, s => s.RunningAsync("GT9", Ct))).Data.Should().ContainSingle().Subject;
            (view.DesksIncluded, view.Desks.Count, view.Zones.Count, view.Lines.Count).Should().Be((false, 0, 1, 1));
            (await Desks(airport, s => s.CaptureAsync("GT9", campaign.Id, DeskBatch("2026-10-01T07:00:00Z", (p.D02, [(1, "Idle")])), $"air:{airport:N}", Ct)))
                .ErrorMessages.Should().Equal(ValidationErrors.DeskObservationsNeedBorderRole);
            (await Desks(airport, s => s.CorrectAsync("GT9", campaign.Id, observation.Id, new CorrectDeskObservationRequest("Closed", "Not mine"), null, Ct)))
                .ErrorMessages.Should().Equal(ValidationErrors.DeskObservationsNeedBorderRole);
            (await Desks(airport, s => s.OwnObservationsAsync("GT9", campaign.Id, null, Ct))).ErrorMessages.Should().Equal(ValidationErrors.DeskObservationsNeedBorderRole);
            (await Desks(airport, s => s.OwnObservationsAsync("GT9", Guid.CreateVersion7(), null, Ct))).ErrorMessages.Should().Equal(ValidationErrors.DeskObservationsNeedBorderRole);
            (await Tracers(airport, s => s.CaptureAsync("GT9", campaign.Id, Batch(0, Run(p.QueueA, "T-01", Utc(1, 7, 0), Utc(1, 7, 5))), $"run:{airport:N}", Ct)))
                .HasErrors.Should().BeFalse("tracer runs are zone data");
        }

        var managerRead = await Desks(dutyObserver, s => s.SearchAsync("GT9", campaign.Id, new DeskObservationCriteria(), Ct));
        (managerRead.Data.DesksIncluded, managerRead.Data.TotalCount).Should().Be((false, 0));

        // A border role with the observer role sees and logs desks.
        var border = (await Capture(borderObserver, s => s.RunningAsync("GT9", Ct))).Data.Should().ContainSingle().Subject;
        (border.DesksIncluded, border.Desks.Count).Should().Be((true, 2));
        (await Desks(borderObserver, s => s.CaptureAsync("GT9", campaign.Id, DeskBatch("2026-10-01T07:00:00Z", (p.D02, [(2, "Paused")])), "bso-01:GT9-desk", Ct)))
            .HasErrors.Should().BeFalse();

        (await TextsAsync("SELECT DISTINCT observer_id::text FROM desk_observation WHERE campaign_id = @id", ("id", campaign.Id)))
            .Should().BeEquivalentTo(observer.ToString(), borderObserver.ToString());
        (await TextsAsync("SELECT count(*) FROM desk_observation WHERE campaign_id = @id AND revision = 1 AND state = 'Serving'", ("id", campaign.Id))).Should().Equal("1");
    }

    #endregion

    #region Separation of duties, races and sites

    [Fact]
    public async Task Capture_Should_RefuseTheCampaignsCreatorAndStarter_When_TheyAlsoHoldTheObserverRole()
    {
        var p = await PlantAsync("GT3", "GTC");
        var planner = await UserWithRolesAsync([RoleCodes.BorderShiftSupervisor, RoleCodes.ValidationObserver], "GT3");
        var starter = await UserWithRolesAsync([RoleCodes.BorderShiftSupervisor, RoleCodes.ValidationObserver], "GT3");
        var observer = await UserAsync(RoleCodes.ValidationObserver, "GT3");
        var campaign = (await Campaigns(planner, s => s.CreateAsync("GT3", Request(p), Ct))).Data;
        await Campaigns(starter, s => s.StartAsync("GT3", campaign.Id, Ct));
        var run = Batch(0, Run(p.QueueA, "T-01", Utc(1, 7, 0), Utc(1, 7, 5)));
        var desks = DeskBatch("2026-10-01T07:00:00Z", (p.D01, [(0, "Idle")]));

        foreach (var excluded in new[] { planner, starter })
        {
            (await Tracers(excluded, s => s.CaptureAsync("GT3", campaign.Id, run, "dual-01:GT3-run", Ct))).ErrorMessages.Should().Equal(ValidationErrors.OwnCampaign);
            (await Desks(excluded, s => s.CaptureAsync("GT3", campaign.Id, desks, "dual-01:GT3-desk", Ct))).ErrorMessages.Should().Equal(ValidationErrors.OwnCampaign);
        }

        (await Tracers(observer, s => s.CaptureAsync("GT3", campaign.Id, run, "obs-01:GT3-run", Ct))).HasErrors.Should().BeFalse();
        (await Desks(observer, s => s.CaptureAsync("GT3", campaign.Id, desks, "obs-01:GT3-desk", Ct))).HasErrors.Should().BeFalse();

        // Bypassing the services, the insert triggers refuse the creator's and the starter's rows as well (23514).
        foreach (var excluded in new[] { planner, starter })
        {
            var insert = () => ExecuteAsync("""
                INSERT INTO desk_observation_batch (id, campaign_id, site_code, observer_id, bin_start_utc, idempotency_key, request_hash, received_utc, observations)
                VALUES (gen_random_uuid(), @campaign, 'GT3', @observer, '2026-10-01T06:45:00Z', 'bypass-key-01', repeat('a', 64), now(), 1)
                """, ("campaign", campaign.Id), ("observer", excluded));
            (await insert.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
        }

        (await TextsAsync("SELECT DISTINCT observer_id::text FROM tracer_run WHERE campaign_id = @id", ("id", campaign.Id))).Should().Equal(observer.ToString());
        (await TextsAsync("SELECT DISTINCT observer_id::text FROM desk_observation WHERE campaign_id = @id", ("id", campaign.Id))).Should().Equal(observer.ToString());
    }

    [Fact]
    public async Task CloseAndCapture_Should_NeverLeaveARunOrStateAfterTheClose_When_TheyRace()
    {
        var p = await PlantAsync("GT4", "GTD");
        var manager = await UserAsync(RoleCodes.BorderShiftSupervisor, "GT4");
        var observer = await UserAsync(RoleCodes.ValidationObserver, "GT4");
        var first = (await Campaigns(manager, s => s.CreateAsync("GT4", Request(p, name: "Close first"), Ct))).Data;
        var second = (await Campaigns(manager, s => s.CreateAsync("GT4", Request(p, name: "Capture first"), Ct))).Data;
        await Campaigns(manager, s => s.StartAsync("GT4", first.Id, Ct));
        await Campaigns(manager, s => s.StartAsync("GT4", second.Id, Ct));

        // The close takes the lock first: a runtime connection holds the campaign FOR UPDATE and closes it, uncommitted. The
        // tracer batch and the desk batch pass their rules (the campaign still reads Running) and their inserts wait on the
        // triggers' share lock; once the close commits they see Closed: 409, nothing stored.
        await using (var closer = await RuntimeConnectionAsync())
        {
            await using var transaction = await closer.BeginTransactionAsync(Ct);
            await using (var close = new NpgsqlCommand("""
                SELECT id FROM validation_campaign WHERE id = @id FOR UPDATE;
                UPDATE validation_campaign SET status = 'Closed', closed_by_id = @by, closed_utc = now() WHERE id = @id
                """, closer, transaction))
            {
                close.Parameters.AddWithValue("id", first.Id);
                close.Parameters.AddWithValue("by", manager);
                await close.ExecuteNonQueryAsync(Ct);
            }

            var runs = Tracers(observer, s => s.CaptureAsync("GT4", first.Id, Batch(0, Run(p.QueueA, "T-01", Utc(1, 7, 0), Utc(1, 7, 5))), "race-01:GT4-run", Ct));
            await WaitUntilBlockedAsync(closer.ProcessID, runs);
            var states = Desks(observer, s => s.CaptureAsync("GT4", first.Id, DeskBatch("2026-10-01T07:00:00Z", (p.D01, [(0, "Idle")])), "race-01:GT4-desk", Ct));
            await WaitUntilBlockedAsync(closer.ProcessID, states, sessions: 2);
            await transaction.CommitAsync(Ct);

            (await runs).ErrorMessages.Should().Equal(ValidationErrors.Closed);
            (await states).ErrorMessages.Should().Equal(ValidationErrors.Closed);
        }

        (await TextsAsync("SELECT (SELECT count(*) FROM tracer_batch WHERE campaign_id = @id) + (SELECT count(*) FROM desk_observation_batch WHERE campaign_id = @id)",
            ("id", first.Id))).Should().Equal("0");

        // The batch takes the lock first: a runtime connection inserts a desk batch (the trigger share-locks the campaign),
        // uncommitted. The close's FOR UPDATE waits; once the batch commits the close goes ahead: the batch stays, nothing after.
        await using (var counter = await RuntimeConnectionAsync())
        {
            await using var transaction = await counter.BeginTransactionAsync(Ct);
            await using (var insert = new NpgsqlCommand("""
                INSERT INTO desk_observation_batch (id, campaign_id, site_code, observer_id, bin_start_utc, idempotency_key, request_hash, received_utc, observations)
                VALUES (gen_random_uuid(), @campaign, 'GT4', @observer, '2026-10-01T06:45:00Z', 'race-02:GT4-desk', repeat('b', 64), now(), 1)
                """, counter, transaction))
            {
                insert.Parameters.AddWithValue("campaign", second.Id);
                insert.Parameters.AddWithValue("observer", observer);
                await insert.ExecuteNonQueryAsync(Ct);
            }

            var close = Campaigns(manager, s => s.CloseAsync("GT4", second.Id, Ct));
            await WaitUntilBlockedAsync(counter.ProcessID, close);
            await transaction.CommitAsync(Ct);

            (await close).Data.Status.Should().Be("Closed");
        }

        (await TextsAsync("SELECT count(*) FROM desk_observation_batch WHERE campaign_id = @id", ("id", second.Id))).Should().Equal("1");
        (await Tracers(observer, s => s.CaptureAsync("GT4", second.Id, Batch(0, Run(p.QueueA, "T-02", Utc(1, 7, 0), Utc(1, 7, 5))), "race-03:GT4-run", Ct)))
            .ErrorMessages.Should().Equal(ValidationErrors.Closed);
    }

    [Fact]
    public async Task Calls_Should_AnswerNotFound_When_TheCallerIsBoundToAnotherSite()
    {
        var a = await PlantAsync("GT5", "GTE");
        await PlantAsync("GT6", "GTF");
        var manager = await UserAsync(RoleCodes.BorderShiftSupervisor, "GT5");
        var observer = await UserAsync(RoleCodes.ValidationObserver, "GT5");
        var elsewhere = await UserAsync(RoleCodes.BorderShiftSupervisor, "GT6");
        var observerElsewhere = await UserAsync(RoleCodes.ValidationObserver, "GT6");
        var campaign = (await Campaigns(manager, s => s.CreateAsync("GT5", Request(a), Ct))).Data;
        await Campaigns(manager, s => s.StartAsync("GT5", campaign.Id, Ct));
        var observed = (await Desks(observer, s => s.CaptureAsync("GT5", campaign.Id, DeskBatch("2026-10-01T07:00:00Z", (a.D01, [(0, "Idle")])), "obs-01:GT5-desk", Ct)))
            .Data.Batch.Observations[0];
        var run = Batch(0, Run(a.QueueA, "T-01", Utc(1, 7, 0), Utc(1, 7, 5)));

        foreach (var site in new[] { "GT5", "GT6" })
        {
            (await Tracers(elsewhere, s => s.SearchAsync(site, campaign.Id, null, Ct))).ErrorMessages.Should().Equal(ValidationErrors.NotFound);
            (await Desks(elsewhere, s => s.SearchAsync(site, campaign.Id, null, Ct))).ErrorMessages.Should().Equal(ValidationErrors.NotFound);
            (await Tracers(observerElsewhere, s => s.CaptureAsync(site, campaign.Id, run, "obs-02:GT6-run", Ct))).ErrorMessages.Should().Equal(ValidationErrors.NotFound);
            (await Tracers(observerElsewhere, s => s.OwnRunsAsync(site, campaign.Id, null, Ct))).ErrorMessages.Should().Equal(ValidationErrors.NotFound);
            (await Desks(observerElsewhere, s => s.CaptureAsync(site, campaign.Id, DeskBatch("2026-10-01T07:15:00Z", (a.D01, [(0, "Idle")])), "obs-02:GT6-desk", Ct)))
                .ErrorMessages.Should().Equal(ValidationErrors.NotFound);
            (await Desks(observerElsewhere, s => s.CorrectAsync(site, campaign.Id, observed.Id, new CorrectDeskObservationRequest("Closed", "x"), null, Ct)))
                .ErrorMessages.Should().Equal(ValidationErrors.NotFound);
            (await Desks(observerElsewhere, s => s.OwnObservationsAsync(site, campaign.Id, null, Ct))).ErrorMessages.Should().Equal(ValidationErrors.NotFound);
        }

        (await TextsAsync("SELECT count(*) FROM tracer_run WHERE campaign_id = @id", ("id", campaign.Id))).Should().Equal("0");
        (await TextsAsync("SELECT count(*) FROM desk_observation WHERE campaign_id = @id", ("id", campaign.Id))).Should().Equal("1");
    }

    #endregion

    #region Schema, keys and privileges

    [Fact]
    public async Task Schema_Should_HoldNoNameOrDocumentColumnOfAPerson_When_TheScriptsHaveRun()
    {
        await RuntimeAsync();
        await _admin.DatabaseAsync();
        var columns = await TextsAsync("""
            SELECT table_name || '.' || column_name FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name IN ('validation_campaign_desk', 'tracer_batch', 'tracer_run', 'desk_observation_batch', 'desk_observation')
            ORDER BY 1
            """);

        // Data boundary: observers are Ariva user ids (uuid), tracers are campaign labels, desks are codes of counters.
        columns.Should().BeEquivalentTo(
            "validation_campaign_desk.id", "validation_campaign_desk.campaign_id", "validation_campaign_desk.site_code", "validation_campaign_desk.desk_id",
            "validation_campaign_desk.checkpoint_code", "validation_campaign_desk.desk_code",
            "tracer_batch.id", "tracer_batch.campaign_id", "tracer_batch.site_code", "tracer_batch.observer_id", "tracer_batch.idempotency_key", "tracer_batch.request_hash",
            "tracer_batch.device_clock_utc", "tracer_batch.received_utc", "tracer_batch.clock_offset_ms", "tracer_batch.runs",
            "tracer_run.id", "tracer_run.batch_id", "tracer_run.campaign_id", "tracer_run.site_code", "tracer_run.zone_id", "tracer_run.tracer_code", "tracer_run.observer_id",
            "tracer_run.joined_raw_utc", "tracer_run.exited_raw_utc", "tracer_run.clock_offset_ms", "tracer_run.joined_utc", "tracer_run.exited_utc", "tracer_run.abandoned",
            "tracer_run.recorded_utc",
            "desk_observation_batch.id", "desk_observation_batch.campaign_id", "desk_observation_batch.site_code", "desk_observation_batch.observer_id",
            "desk_observation_batch.bin_start_utc", "desk_observation_batch.idempotency_key", "desk_observation_batch.request_hash", "desk_observation_batch.received_utc",
            "desk_observation_batch.observations",
            "desk_observation.id", "desk_observation.batch_id", "desk_observation.campaign_id", "desk_observation.site_code", "desk_observation.desk_id",
            "desk_observation.minute_utc", "desk_observation.observer_id", "desk_observation.revision", "desk_observation.state", "desk_observation.reason",
            "desk_observation.corrects_id", "desk_observation.recorded_utc", "desk_observation.idempotency_key");
        string[] personal = ["name", "user", "email", "phone", "document", "passport", "nationality", "birth", "badge", "staff", "officer", "traveller", "passenger", "mrz", "pnr",
            "created_by", "modified_by"];
        columns.Select(c => c.Split('.')[1]).Where(c => personal.Any(word => c.Contains(word, StringComparison.Ordinal))).Should()
            .BeEmpty("no column holds a person's name, account name, contact, staff number or document");
        (await TextsAsync("""
            SELECT table_name || '.' || column_name || ':' || data_type FROM information_schema.columns
            WHERE table_schema = 'public' AND column_name = 'observer_id' AND table_name IN ('tracer_batch', 'tracer_run', 'desk_observation_batch', 'desk_observation')
            """)).Should().HaveCount(4).And.OnlyContain(c => c.EndsWith(":uuid", StringComparison.Ordinal));
        (await TextsAsync("""
            SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid = 'tracer_run'::regclass AND pg_get_constraintdef(oid) LIKE '%tracer_code%'
            """)).Should().ContainSingle().Which.Should().Contain("^T-[0-9]{2,3}$", "the database checks the tracer label as the code does");
    }

    [Fact]
    public async Task Keys_Should_KeepRunsAndStatesToTheCampaign_When_AStatementBypassesTheService()
    {
        var p = await PlantAsync("GT7", "GTG");
        var manager = await UserAsync(RoleCodes.BorderShiftSupervisor, "GT7");
        var observer = await UserAsync(RoleCodes.ValidationObserver, "GT7");
        var planned = (await Campaigns(manager, s => s.CreateAsync("GT7", Request(p, name: "Planned", desks: [p.D01]), Ct))).Data;
        var campaign = (await Campaigns(manager, s => s.CreateAsync("GT7", Request(p, desks: [p.D01]), Ct))).Data;
        await Campaigns(manager, s => s.StartAsync("GT7", campaign.Id, Ct));
        var batch = (await Tracers(observer, s => s.CaptureAsync("GT7", campaign.Id, Batch(1_500, Run(p.QueueA, "T-01", Utc(1, 7, 0), Utc(1, 7, 5))), "obs-01:GT7-run", Ct)))
            .Data.Batch;
        var desks = (await Desks(observer, s => s.CaptureAsync("GT7", campaign.Id, DeskBatch("2026-10-01T07:00:00Z", (p.D01, [(0, "Idle")])), "obs-01:GT7-desk", Ct)))
            .Data.Batch;

        async Task<string> RefusedAsync([System.Diagnostics.CodeAnalysis.ConstantExpected] string sql, params (string Name, object Value)[] parameters)
        {
            var run = () => ExecuteAsync(sql, parameters);
            return (await run.Should().ThrowAsync<PostgresException>(sql)).Which.SqlState;
        }

        // Scope: an airport counter and an e-gate are no campaign desks; a desk joins only a planned campaign.
        (await RefusedAsync("INSERT INTO validation_campaign_desk (id, campaign_id, site_code, desk_id, checkpoint_code, desk_code) VALUES (gen_random_uuid(), @c, 'GT7', @d, 'CHK', 'C01')",
            ("c", planned.Id), ("d", p.Counter))).Should().Be("23514");
        (await RefusedAsync("INSERT INTO validation_campaign_desk (id, campaign_id, site_code, desk_id, checkpoint_code, desk_code) VALUES (gen_random_uuid(), @c, 'GT7', @d, 'IMM', 'EG01')",
            ("c", planned.Id), ("d", p.EGate))).Should().Be("23514");
        (await RefusedAsync("INSERT INTO validation_campaign_desk (id, campaign_id, site_code, desk_id, checkpoint_code, desk_code) VALUES (gen_random_uuid(), @c, 'GT7', @d, 'IMM', 'D02')",
            ("c", campaign.Id), ("d", p.D02))).Should().Be("23001", "the scope is set while the campaign is planned");

        // Runs: a name as the code, an offset other than the batch's, a correction other than raw minus offset, a zone out of scope.
        (await RefusedAsync("""
            INSERT INTO tracer_run (id, batch_id, campaign_id, site_code, zone_id, tracer_code, observer_id, joined_raw_utc, exited_raw_utc, clock_offset_ms, joined_utc, exited_utc, abandoned, recorded_utc)
            VALUES (gen_random_uuid(), @b, @c, 'GT7', @z, 'Omar', @o, '2026-10-01T07:10:01.5Z', '2026-10-01T07:20:01.5Z', 1500, '2026-10-01T07:10:00Z', '2026-10-01T07:20:00Z', false, '2026-10-01T08:00:00Z')
            """, ("b", batch.Id), ("c", campaign.Id), ("z", p.QueueA), ("o", observer))).Should().Be("23514", "a tracer is a label, never a name");
        (await RefusedAsync("""
            INSERT INTO tracer_run (id, batch_id, campaign_id, site_code, zone_id, tracer_code, observer_id, joined_raw_utc, exited_raw_utc, clock_offset_ms, joined_utc, exited_utc, abandoned, recorded_utc)
            VALUES (gen_random_uuid(), @b, @c, 'GT7', @z, 'T-02', @o, '2026-10-01T07:10:00Z', '2026-10-01T07:20:00Z', 0, '2026-10-01T07:10:00Z', '2026-10-01T07:20:00Z', false, '2026-10-01T08:00:00Z')
            """, ("b", batch.Id), ("c", campaign.Id), ("z", p.QueueA), ("o", observer))).Should().Be("23503", "the run carries its batch's offset");
        (await RefusedAsync("""
            INSERT INTO tracer_run (id, batch_id, campaign_id, site_code, zone_id, tracer_code, observer_id, joined_raw_utc, exited_raw_utc, clock_offset_ms, joined_utc, exited_utc, abandoned, recorded_utc)
            VALUES (gen_random_uuid(), @b, @c, 'GT7', @z, 'T-02', @o, '2026-10-01T07:10:00Z', '2026-10-01T07:20:00Z', 1500, '2026-10-01T07:10:00Z', '2026-10-01T07:20:00Z', false, '2026-10-01T08:00:00Z')
            """, ("b", batch.Id), ("c", campaign.Id), ("z", p.QueueA), ("o", observer))).Should().Be("23514", "corrected is raw minus the offset");
        (await RefusedAsync("""
            INSERT INTO tracer_run (id, batch_id, campaign_id, site_code, zone_id, tracer_code, observer_id, joined_raw_utc, exited_raw_utc, clock_offset_ms, joined_utc, exited_utc, abandoned, recorded_utc)
            VALUES (gen_random_uuid(), @b, @c, 'GT7', @z, 'T-02', @o, '2026-10-01T07:10:01.5Z', '2026-10-01T07:20:01.5Z', 1500, '2026-10-01T07:10:00Z', '2026-10-01T07:20:00Z', false, '2026-10-01T08:00:00Z')
            """, ("b", batch.Id), ("c", campaign.Id), ("z", p.QueueB), ("o", observer))).Should().Be("23503", "Q-B is not in scope");
        (await RefusedAsync("""
            INSERT INTO tracer_batch (id, campaign_id, site_code, observer_id, idempotency_key, request_hash, device_clock_utc, received_utc, clock_offset_ms, runs)
            VALUES (gen_random_uuid(), @c, 'GT7', @o, 'bypass-key-02', repeat('a', 64), '2026-10-01T08:10:00Z', '2026-10-01T08:00:00Z', 600000, 1)
            """, ("c", campaign.Id), ("o", observer))).Should().Be("23514", "an offset beyond five minutes");

        // States: a desk out of scope, a minute outside its batch's bin, a correction that skips a revision.
        (await RefusedAsync("""
            INSERT INTO desk_observation (id, batch_id, campaign_id, site_code, desk_id, minute_utc, observer_id, revision, state, recorded_utc)
            VALUES (gen_random_uuid(), @b, @c, 'GT7', @d, '2026-10-01T07:05:00Z', @o, 1, 'Idle', '2026-10-01T08:00:00Z')
            """, ("b", desks.Id), ("c", campaign.Id), ("d", p.D02), ("o", observer))).Should().Be("23503", "D02 is not in scope");
        (await RefusedAsync("""
            INSERT INTO desk_observation (id, batch_id, campaign_id, site_code, desk_id, minute_utc, observer_id, revision, state, recorded_utc)
            VALUES (gen_random_uuid(), @b, @c, 'GT7', @d, '2026-10-01T07:15:00Z', @o, 1, 'Idle', '2026-10-01T08:00:00Z')
            """, ("b", desks.Id), ("c", campaign.Id), ("d", p.D01), ("o", observer))).Should().Be("23514", "07:15 is not in the 07:00 bin");
        (await RefusedAsync("""
            INSERT INTO desk_observation (id, campaign_id, site_code, desk_id, minute_utc, observer_id, revision, state, reason, corrects_id, recorded_utc)
            VALUES (gen_random_uuid(), @c, 'GT7', @d, '2026-10-01T07:00:00Z', @o, 3, 'Serving', 'skip', @f, '2026-10-01T08:00:00Z')
            """, ("c", campaign.Id), ("d", p.D01), ("o", observer), ("f", desks.Observations[0].Id))).Should().Be("23514");
        (await RefusedAsync("""
            INSERT INTO desk_observation (id, batch_id, campaign_id, site_code, desk_id, minute_utc, observer_id, revision, state, recorded_utc)
            VALUES (gen_random_uuid(), @b, @c, 'GT7', @d, '2026-10-01T07:06:00Z', @o, 1, 'Unknown', '2026-10-01T08:00:00Z')
            """, ("b", desks.Id), ("c", campaign.Id), ("d", p.D01), ("o", observer))).Should().Be("23514", "Unknown is the engine's answer, never an observation");

        // Evidence never changes, not even for the migration login.
        (await RefusedAsync("UPDATE tracer_run SET joined_utc = joined_utc - interval '1 minute' WHERE batch_id = @b", ("b", batch.Id))).Should().Be("23001");
        (await RefusedAsync("UPDATE desk_observation SET state = 'Serving' WHERE batch_id = @b", ("b", desks.Id))).Should().Be("23001");
        (await RefusedAsync("DELETE FROM validation_campaign_desk WHERE campaign_id = @c", ("c", campaign.Id))).Should().Be("23001");
        (await TextsAsync("SELECT count(*) FROM tracer_run WHERE campaign_id = @id", ("id", campaign.Id))).Should().Equal("1");
        (await TextsAsync("SELECT count(*) FROM desk_observation WHERE campaign_id = @id", ("id", campaign.Id))).Should().Equal("1");
    }

    [Fact]
    public async Task RuntimeLogin_Should_NeitherChangeNorDeleteGroundTruth_When_ConnectedAsTheHostsAre()
    {
        var p = await PlantAsync("GT8", "GTH");
        var manager = await UserAsync(RoleCodes.BorderShiftSupervisor, "GT8");
        var observer = await UserAsync(RoleCodes.ValidationObserver, "GT8");
        var campaign = (await Campaigns(manager, s => s.CreateAsync("GT8", Request(p), Ct))).Data;
        await Campaigns(manager, s => s.StartAsync("GT8", campaign.Id, Ct));
        await Tracers(observer, s => s.CaptureAsync("GT8", campaign.Id, Batch(0, Run(p.QueueA, "T-01", Utc(1, 7, 0), Utc(1, 7, 5))), "obs-01:GT8-run", Ct));
        await Desks(observer, s => s.CaptureAsync("GT8", campaign.Id, DeskBatch("2026-10-01T07:00:00Z", (p.D01, [(0, "Idle")])), "obs-01:GT8-desk", Ct));

        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await _admin.DatabaseAsync(), RuntimeLogin, RuntimePassword));
        await connection.OpenAsync(Ct);
        foreach (var sql in new[]
                 {
                     "UPDATE tracer_run SET abandoned = true", "DELETE FROM tracer_run", "TRUNCATE tracer_run",
                     "UPDATE tracer_batch SET clock_offset_ms = 0", "DELETE FROM tracer_batch", "TRUNCATE tracer_batch",
                     "UPDATE desk_observation SET state = 'Closed'", "DELETE FROM desk_observation", "TRUNCATE desk_observation",
                     "UPDATE desk_observation_batch SET observations = 2", "DELETE FROM desk_observation_batch",
                     "UPDATE validation_campaign_desk SET desk_code = 'X'", "DELETE FROM validation_campaign_desk", "TRUNCATE validation_campaign_desk"
                 })
        {
            async Task RunAsync()
            {
                await using var transaction = await connection.BeginTransactionAsync(Ct);
#pragma warning disable CA2100 // literal statements from this test
                await using var command = new NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100
                await command.ExecuteNonQueryAsync(Ct);
                await transaction.RollbackAsync(Ct);
            }

            var change = RunAsync;
            (await change.Should().ThrowAsync<PostgresException>(sql)).Which.SqlState.Should().Be("42501", $"permission denied: {sql}");
        }

        (await TextsAsync("SELECT count(*) FROM tracer_run WHERE campaign_id = @id", ("id", campaign.Id))).Should().Equal("1");
        (await TextsAsync("SELECT count(*) FROM desk_observation WHERE campaign_id = @id", ("id", campaign.Id))).Should().Equal("1");
    }

    #endregion
}
