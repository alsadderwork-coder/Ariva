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
/// ARV-104a against PostgreSQL with script 0047. The services run under a real runtime login, as the hosts do, so the
/// column grants and the share locks of the triggers are exercised: a manager plans, starts and closes a campaign (audited),
/// an observer captures and corrects counts, the site scope answers NotFound across sites, an Idempotency-Key returns the
/// stored count while the same key from another observer is a new count, two captures of one line, bin and observer keep
/// one, the campaign's creator and starter never count for it, and a count racing a close either commits before it or
/// answers 409 (two locks held in a known order). Statements
/// that bypass the services meet the keys and triggers; the runtime login can neither change nor delete evidence; and the
/// schema holds no person beyond an Ariva user id. Each test plants a site of its own.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ValidationTests(PostgresFixture fixture) : IAsyncDisposable
{
    private const string RuntimeLogin = "it_validation_runtime";

    // One password for the class, so its tests share one connection pool of the runtime login (cleared after each test):
    // the collection shares one PostgreSQL server and its connection limit.
    private static readonly string RuntimePassword = "rt-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
    private AccountsHost _admin;
    private AccountsHost _runtime;
    private bool _usersCreated;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The hosts' clock: Thursday 1 October 2026 08:00 UTC, 12:00 in Dubai; the 07:00 bin (11:00 local) has ended.
    private static readonly DateTime Bin = new(2026, 10, 1, 7, 0, 0, DateTimeKind.Utc);

    public async ValueTask DisposeAsync()
    {
        if (_runtime is not null)
        {
            // The runtime host's pool (its provider exists once a user was created through it).
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

    private sealed record Planted(string Site, Guid Profile, Guid QueueA, Guid QueueB, Guid BandA, Guid EntryA, Guid ExitA, Guid BandEntryA, Guid EntryB, Guid HallCount);

    /// <summary>The migration login's host (plants rows) and a host on the runtime login (runs the services).</summary>
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
    /// A site in Dubai with a published profile version 1: queue zones Q-A (entry, exit, an overflow band with its entry line)
    /// and Q-B (entry), and a count line standing alone. Zones and lines go in while the profile is a draft (a published
    /// profile's geometry never changes), then it is published.
    /// </summary>
    private async Task<Planted> PlantAsync(string site, string iata)
    {
        await RuntimeAsync();
        var p = new Planted(site, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await ExecuteAsync("""
            INSERT INTO site (id, code, name) VALUES (gen_random_uuid(), @site, @site);
            INSERT INTO airport (id, iata_code, name, time_zone_id) VALUES (gen_random_uuid(), @iata, @iata, 'Asia/Dubai');
            INSERT INTO terminal (id, airport_id, code, name, site_code) SELECT gen_random_uuid(), a.id, 'T1', 'T1', @site FROM airport a WHERE a.iata_code = @iata;
            INSERT INTO level (id, terminal_id, site_code, code, name, floor_number, width_metres, depth_metres)
            SELECT @level, t.id, @site, 'L1', 'L1', 0, 100, 100 FROM terminal t JOIN airport a ON a.id = t.airport_id WHERE a.iata_code = @iata;
            INSERT INTO zone_profile (id, site_code, name, status) VALUES (@profile, @site, 'Profile', 'Draft');
            INSERT INTO zone (id, profile_id, name, kind, level_id, polygon, queue_zone_id) VALUES
                (@qa, @profile, 'Q-A', 'Queue', @level, '[]', NULL),
                (@qb, @profile, 'Q-B', 'Queue', @level, '[]', NULL),
                (@band, @profile, 'Q-A band', 'Overflow', @level, '[]', @qa);
            INSERT INTO line (id, profile_id, name, role, zone_id, level_id, start_x, start_y, end_x, end_y) VALUES
                (@entryA, @profile, 'Entry A', 'Entry', @qa, @level, 0, 0, 0, 1),
                (@exitA, @profile, 'Exit A', 'Exit', @qa, @level, 1, 0, 1, 1),
                (@bandEntry, @profile, 'Band entry A', 'OverflowEntry', @band, @level, 2, 0, 2, 1),
                (@entryB, @profile, 'Entry B', 'Entry', @qb, @level, 3, 0, 3, 1),
                (@hall, @profile, 'Hall count', 'Count', NULL, @level, 4, 0, 4, 1);
            UPDATE zone_profile SET status = 'Published', version = 1, geometry_hash = repeat('b', 64), published_on = '2026-09-01T00:00:00Z', published_by = 'it'
             WHERE id = @profile;
            """, ("site", site), ("iata", iata), ("level", Guid.CreateVersion7()), ("profile", p.Profile), ("qa", p.QueueA), ("qb", p.QueueB), ("band", p.BandA),
            ("entryA", p.EntryA), ("exitA", p.ExitA), ("bandEntry", p.BandEntryA), ("entryB", p.EntryB), ("hall", p.HallCount));
        return p;
    }

    /// <summary>A user with the role, bound to the given sites (or every site).</summary>
    private Task<Guid> UserAsync(string role, params string[] sites) => UserWithRolesAsync([role], sites);

    /// <summary>A user with these roles (a manager who is also an observer, for instance), bound to the given sites (or every site).</summary>
    private async Task<Guid> UserWithRolesAsync(string[] roles, params string[] sites)
    {
        var host = await RuntimeAsync();
        _usersCreated = true;
        var id = await host.CreateUserAsync($"it.val.{string.Concat(roles).ToLowerInvariant()}.{Guid.NewGuid():N}"[..40], roles: roles, allSites: sites.Length == 0);
        foreach (var site in sites)
            await ExecuteAsync("INSERT INTO user_site (id, user_id, site_code) VALUES (gen_random_uuid(), @user, @site)", ("user", id), ("site", site));
        return id;
    }

    /// <summary>A connection of the runtime login outside any pool (closed, never reused), as a host's would be.</summary>
    private async Task<NpgsqlConnection> RuntimeConnectionAsync()
    {
        var builder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString(await _admin.DatabaseAsync(), RuntimeLogin, RuntimePassword)) { Pooling = false };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(Ct);
        return connection;
    }

    /// <summary>
    /// Waits until a session waits on a lock that <paramref name="holder"/> (a backend process id) holds, or until
    /// <paramref name="waiter"/> finished without waiting; either way within 30 seconds.
    /// </summary>
    private async Task WaitUntilBlockedAsync(int holder, Task waiter)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && !waiter.IsCompleted)
        {
            var waiting = await TextsAsync("SELECT count(*) FROM pg_stat_activity WHERE @holder = ANY(pg_blocking_pids(pid))", ("holder", holder));
            if (waiting[0] != "0")
                return;
            await Task.Delay(25, Ct);
        }

        waiter.IsCompleted.Should().BeFalse("the second statement waits on the first one's lock");
        throw new TimeoutException("No session waited on the lock within 30 seconds.");
    }

    private static CreateValidationCampaignRequest Request(Planted p, string name = "Pilot week 1", int? version = 1, IReadOnlyList<Guid> lines = null) =>
        new(name, version, [p.QueueA], lines ?? [p.EntryA, p.ExitA, p.BandEntryA], ["2026-10-01", "2026-09-30"], null, 12);

    private Task<T> Campaigns<T>(Guid caller, Func<ISvcValidationCampaigns, Task<T>> work) =>
        _runtime.AsCallerAsync(caller, s => work(s.GetRequiredService<ISvcValidationCampaigns>()));

    private Task<T> Capture<T>(Guid caller, Func<ISvcValidationCapture, Task<T>> work) =>
        _runtime.AsCallerAsync(caller, s => work(s.GetRequiredService<ISvcValidationCapture>()));

    private static CaptureManualCountRequest Count(Guid line, int crossingsIn = 42, int crossingsOut = 3, string bin = "2026-10-01T07:00:00Z") =>
        new(line, bin, crossingsIn, crossingsOut);

    #endregion

    #region Lifecycle

    [Fact]
    public async Task Campaign_Should_RunFromPlanToClose_When_AManagerPlansAndAnObserverCounts()
    {
        var p = await PlantAsync("VAL1", "VLA");
        var manager = await UserAsync(RoleCodes.BorderShiftSupervisor, "VAL1");
        var observer = await UserAsync(RoleCodes.ValidationObserver, "VAL1");
        var second = await UserAsync(RoleCodes.ValidationObserver, "VAL1");

        var created = await Campaigns(manager, s => s.CreateAsync("VAL1", Request(p), Ct));
        created.HasErrors.Should().BeFalse(string.Join(" ", created.ErrorMessages ?? []));
        var campaign = created.Data;
        (campaign.Status, campaign.ProfileVersion, campaign.ProfileStatus, campaign.TimeZoneId).Should().Be(("Planned", 1, "Published", "Asia/Dubai"));
        campaign.Days.Should().Equal("2026-09-30", "2026-10-01");
        campaign.Targets.Should().Be(new Core.Domain.ViewModels.ValidationTargetsViewModel(20, 12, true));
        campaign.Lines.Select(l => (l.Name, l.Role, l.QueueZone, l.BinsCaptured)).Should().Equal(
            ("Band entry A", "OverflowEntry", "Q-A", 0), ("Entry A", "Entry", "Q-A", 0), ("Exit A", "Exit", "Q-A", 0));
        campaign.CreatedById.Should().Be(manager);

        // Planned: nothing is captured yet, and the observer sees no running campaign.
        (await Capture(observer, s => s.CaptureAsync("VAL1", campaign.Id, Count(p.EntryA), null, Ct))).ErrorMessages.Should().Equal(ValidationErrors.NotStarted);
        (await Capture(observer, s => s.RunningAsync("VAL1", Ct))).Data.Should().BeEmpty();

        var started = await Campaigns(manager, s => s.StartAsync("VAL1", campaign.Id, Ct));
        (started.Data.Status, started.Data.StartedById).Should().Be(("Running", manager));
        (await Campaigns(manager, s => s.StartAsync("VAL1", campaign.Id, Ct))).ErrorMessages.Should().Equal(ValidationErrors.NotPlanned);

        var running = (await Capture(observer, s => s.RunningAsync("VAL1", Ct))).Data.Should().ContainSingle().Subject;
        (running.Id, running.TimeZoneId, running.BinMinutes).Should().Be((campaign.Id, "Asia/Dubai", 15));
        running.Lines.Select(l => l.Name).Should().Equal("Band entry A", "Entry A", "Exit A");

        // A count, the same line and bin again (409), the same by a second observer (allowed), a line out of scope, a bin
        // that has not ended and a day outside the campaign.
        var first = await Capture(observer, s => s.CaptureAsync("VAL1", campaign.Id, Count(p.EntryA), null, Ct));
        first.HasErrors.Should().BeFalse(string.Join(" ", first.ErrorMessages ?? []));
        (first.Data.Replayed, first.Data.Count.Revision, first.Data.Count.ObserverId, first.Data.Count.LineName).Should().Be((false, 1, observer, "Entry A"));
        (await Capture(observer, s => s.CaptureAsync("VAL1", campaign.Id, Count(p.EntryA, 50), null, Ct))).ErrorMessages.Should().Equal(ValidationErrors.AlreadyCaptured);
        (await Capture(second, s => s.CaptureAsync("VAL1", campaign.Id, Count(p.EntryA, 41), null, Ct))).HasErrors.Should().BeFalse();
        (await Capture(observer, s => s.CaptureAsync("VAL1", campaign.Id, Count(p.EntryB), null, Ct))).ErrorMessages.Should().Equal(ValidationErrors.LineNotInScope);
        (await Capture(observer, s => s.CaptureAsync("VAL1", campaign.Id, Count(p.HallCount), null, Ct))).ErrorMessages.Should().Equal(ValidationErrors.LineNotInScope);
        (await Capture(observer, s => s.CaptureAsync("VAL1", campaign.Id, Count(p.ExitA, bin: "2026-10-01T08:00:00Z"), null, Ct))).ErrorMessages.Should().Equal(ValidationErrors.BinNotEnded);
        (await Capture(observer, s => s.CaptureAsync("VAL1", campaign.Id, Count(p.ExitA, bin: "2026-09-29T19:45:00Z"), null, Ct))).ErrorMessages.Should().Equal(ValidationErrors.BinOutsideCampaign);
        (await Capture(observer, s => s.CaptureAsync("VAL1", campaign.Id, Count(p.ExitA, bin: "2026-10-01T07:05:00Z"), null, Ct))).ErrorMessages.Should().Equal(ValidationErrors.InvalidBin);

        // A correction is the next revision with a reason; the corrected revision stays; only the latest can be corrected,
        // and only by its observer.
        var corrected = await Capture(observer, s => s.CorrectAsync("VAL1", campaign.Id, first.Data.Count.Id, new CorrectManualCountRequest(44, 3, "Two missed at the start"), null, Ct));
        corrected.HasErrors.Should().BeFalse(string.Join(" ", corrected.ErrorMessages ?? []));
        (corrected.Data.Count.Revision, corrected.Data.Count.CrossingsIn, corrected.Data.Count.Reason, corrected.Data.Count.CorrectsId).Should()
            .Be((2, 44, "Two missed at the start", first.Data.Count.Id));
        (await Capture(observer, s => s.CorrectAsync("VAL1", campaign.Id, first.Data.Count.Id, new CorrectManualCountRequest(45, 3, "Again"), null, Ct)))
            .ErrorMessages.Should().Equal(ValidationErrors.NotLatest);
        (await Capture(second, s => s.CorrectAsync("VAL1", campaign.Id, corrected.Data.Count.Id, new CorrectManualCountRequest(1, 1, "Not mine"), null, Ct)))
            .ErrorMessages.Should().Equal(ValidationErrors.NotFound);

        // The manager sees every observer's current counts, or every revision; an observer sees only its own.
        var current = await Campaigns(manager, s => s.SearchCountsAsync("VAL1", campaign.Id, new ManualCountCriteria(), Ct));
        current.Data.Data.Select(c => (c.ObserverId, c.Revision, c.CrossingsIn, c.Current)).Should().BeEquivalentTo(new[] { (observer, 2, 44, true), (second, 1, 41, true) });
        var history = await Campaigns(manager, s => s.SearchCountsAsync("VAL1", campaign.Id, new ManualCountCriteria { CurrentOnly = false, ObserverId = observer }, Ct));
        history.Data.Data.Select(c => (c.Revision, c.Current)).Should().Equal((1, false), (2, true));
        var own = await Capture(second, s => s.OwnCountsAsync("VAL1", campaign.Id, new ManualCountCriteria { ObserverId = observer, CurrentOnly = false }, Ct));
        own.Data.Data.Should().ContainSingle().Which.ObserverId.Should().Be(second, "an observer reads its own counts whatever observer the query names");
        (await Campaigns(manager, s => s.GetAsync("VAL1", campaign.Id, Ct))).Data.Lines.Single(l => l.Name == "Entry A").BinsCaptured.Should().Be(1);

        // Closed: nothing is captured or corrected any more; closing again is refused.
        var closed = await Campaigns(manager, s => s.CloseAsync("VAL1", campaign.Id, Ct));
        (closed.Data.Status, closed.Data.ClosedById).Should().Be(("Closed", manager));
        (await Capture(observer, s => s.CaptureAsync("VAL1", campaign.Id, Count(p.ExitA), null, Ct))).ErrorMessages.Should().Equal(ValidationErrors.Closed);
        (await Capture(observer, s => s.CorrectAsync("VAL1", campaign.Id, corrected.Data.Count.Id, new CorrectManualCountRequest(1, 1, "Late"), null, Ct)))
            .ErrorMessages.Should().Equal(ValidationErrors.Closed);
        (await Campaigns(manager, s => s.CloseAsync("VAL1", campaign.Id, Ct))).ErrorMessages.Should().Equal(ValidationErrors.Closed);
        (await Campaigns(manager, s => s.StartAsync("VAL1", campaign.Id, Ct))).ErrorMessages.Should().Equal(ValidationErrors.Closed);

        // Audited in the same unit of work: created, started, closed and the correction; no audit row names an observer by name.
        (await TextsAsync("SELECT action FROM audit_entry WHERE target_id = @id", ("id", campaign.Id)))
            .Should().BeEquivalentTo("ValidationCampaign.Created", "ValidationCampaign.Started", "ValidationCampaign.Closed");
        (await TextsAsync("SELECT after_summary FROM audit_entry WHERE action = 'ManualCount.Corrected' AND target_id = @id", ("id", corrected.Data.Count.Id)))
            .Should().ContainSingle().Which.Should().Contain($"observer={observer}").And.Contain("revision=2").And.Contain("\"Two missed at the start\"");
    }

    [Fact]
    public async Task Capture_Should_ReturnTheStoredCount_When_TheSameIdempotencyKeyIsSentAgain()
    {
        var p = await PlantAsync("VAL2", "VLB");
        var manager = await UserAsync(RoleCodes.TerminalDutyManager, "VAL2");
        var observer = await UserAsync(RoleCodes.ValidationObserver, "VAL2");
        var campaign = (await Campaigns(manager, s => s.CreateAsync("VAL2", Request(p), Ct))).Data;
        await Campaigns(manager, s => s.StartAsync("VAL2", campaign.Id, Ct));

        var sent = await Capture(observer, s => s.CaptureAsync("VAL2", campaign.Id, Count(p.ExitA, 7, 1), "tablet-07:VAL2-0700", Ct));
        var resent = await Capture(observer, s => s.CaptureAsync("VAL2", campaign.Id, Count(p.ExitA, 7, 1), "tablet-07:VAL2-0700", Ct));
        var reused = await Capture(observer, s => s.CaptureAsync("VAL2", campaign.Id, Count(p.EntryA, 7, 1), "tablet-07:VAL2-0700", Ct));
        var bad = await Capture(observer, s => s.CaptureAsync("VAL2", campaign.Id, Count(p.EntryA), "' OR '1'='1", Ct));

        sent.Data.Replayed.Should().BeFalse();
        (resent.Data.Replayed, resent.Data.Count.Id).Should().Be((true, sent.Data.Count.Id));
        reused.ErrorMessages.Should().Equal(ValidationErrors.KeyReused);
        bad.ErrorMessages.Should().Equal(ValidationErrors.InvalidIdempotencyKey);

        var correction = new CorrectManualCountRequest(8, 1, "Recount");
        var fixedOnce = await Capture(observer, s => s.CorrectAsync("VAL2", campaign.Id, sent.Data.Count.Id, correction, "tablet-07:VAL2-0700-fix", Ct));
        var fixedAgain = await Capture(observer, s => s.CorrectAsync("VAL2", campaign.Id, sent.Data.Count.Id, correction, "tablet-07:VAL2-0700-fix", Ct));
        (fixedOnce.Data.Replayed, fixedAgain.Data.Replayed, fixedAgain.Data.Count.Id).Should().Be((false, true, fixedOnce.Data.Count.Id));
        (await TextsAsync("SELECT count(*) FROM manual_count WHERE campaign_id = @id", ("id", campaign.Id))).Should().Equal("2");
    }

    [Fact]
    public async Task IdempotencyKey_Should_BelongToOneObserver_When_AnotherObserverSendsTheSameKey()
    {
        // CWE-863: the replay looks among the caller's own counts only and ux_manual_count_idempotency is unique per observer,
        // so a key another observer already used never returns, refuses or reveals that observer's count.
        var p = await PlantAsync("VAL13", "VLM");
        var manager = await UserAsync(RoleCodes.TerminalDutyManager, "VAL13");
        var first = await UserAsync(RoleCodes.ValidationObserver, "VAL13");
        var second = await UserAsync(RoleCodes.ValidationObserver, "VAL13");
        var campaign = (await Campaigns(manager, s => s.CreateAsync("VAL13", Request(p), Ct))).Data;
        await Campaigns(manager, s => s.StartAsync("VAL13", campaign.Id, Ct));
        const string exitKey = "tablet-01:VAL13-exit-0700";
        const string entryKey = "tablet-01:VAL13-entry-0700";

        var exit = (await Capture(first, s => s.CaptureAsync("VAL13", campaign.Id, Count(p.ExitA, 7, 1), exitKey, Ct))).Data.Count;
        var entry = (await Capture(first, s => s.CaptureAsync("VAL13", campaign.Id, Count(p.EntryA, 5, 0), entryKey, Ct))).Data.Count;

        // The second observer sends the first one's key with the same body: a new count of its own (201), not a replay.
        var same = await Capture(second, s => s.CaptureAsync("VAL13", campaign.Id, Count(p.ExitA, 7, 1), exitKey, Ct));
        same.HasErrors.Should().BeFalse(string.Join(" ", same.ErrorMessages ?? []));
        (same.Data.Replayed, same.Data.Count.ObserverId, same.Data.Count.Revision).Should().Be((false, second, 1));
        same.Data.Count.Id.Should().NotBe(exit.Id);

        // The first one's other key with another body: also a new count (201), not 409 KeyReused.
        var other = await Capture(second, s => s.CaptureAsync("VAL13", campaign.Id, Count(p.EntryA, 9, 2), entryKey, Ct));
        other.HasErrors.Should().BeFalse(string.Join(" ", other.ErrorMessages ?? []));
        (other.Data.Replayed, other.Data.Count.ObserverId, other.Data.Count.CrossingsIn, other.Data.Count.CrossingsOut).Should().Be((false, second, 9, 2));

        // Each observer's resend still finds its own count, and its own key with another body is still refused.
        var firstAgain = await Capture(first, s => s.CaptureAsync("VAL13", campaign.Id, Count(p.ExitA, 7, 1), exitKey, Ct));
        (firstAgain.Data.Replayed, firstAgain.Data.Count.Id, firstAgain.Data.Count.ObserverId).Should().Be((true, exit.Id, first));
        var secondAgain = await Capture(second, s => s.CaptureAsync("VAL13", campaign.Id, Count(p.ExitA, 7, 1), exitKey, Ct));
        (secondAgain.Data.Replayed, secondAgain.Data.Count.Id, secondAgain.Data.Count.ObserverId).Should().Be((true, same.Data.Count.Id, second));
        (await Capture(second, s => s.CaptureAsync("VAL13", campaign.Id, Count(p.EntryA, 5, 0), entryKey, Ct))).ErrorMessages.Should().Equal(ValidationErrors.KeyReused);

        // The same two cases for a correction key.
        const string exitFixKey = "tablet-01:VAL13-exit-0700-fix";
        const string entryFixKey = "tablet-01:VAL13-entry-0700-fix";
        var recount = new CorrectManualCountRequest(8, 1, "Recount");
        var exitFixed = (await Capture(first, s => s.CorrectAsync("VAL13", campaign.Id, exit.Id, recount, exitFixKey, Ct))).Data.Count;
        (await Capture(first, s => s.CorrectAsync("VAL13", campaign.Id, entry.Id, new CorrectManualCountRequest(6, 0, "Missed one"), entryFixKey, Ct))).HasErrors.Should().BeFalse();

        var sameFix = await Capture(second, s => s.CorrectAsync("VAL13", campaign.Id, same.Data.Count.Id, recount, exitFixKey, Ct));
        sameFix.HasErrors.Should().BeFalse(string.Join(" ", sameFix.ErrorMessages ?? []));
        (sameFix.Data.Replayed, sameFix.Data.Count.ObserverId, sameFix.Data.Count.Revision, sameFix.Data.Count.CorrectsId).Should().Be((false, second, 2, same.Data.Count.Id));
        sameFix.Data.Count.Id.Should().NotBe(exitFixed.Id);
        var otherFix = await Capture(second, s => s.CorrectAsync("VAL13", campaign.Id, other.Data.Count.Id, new CorrectManualCountRequest(10, 2, "Two more"), entryFixKey, Ct));
        otherFix.HasErrors.Should().BeFalse(string.Join(" ", otherFix.ErrorMessages ?? []));
        (otherFix.Data.Replayed, otherFix.Data.Count.ObserverId, otherFix.Data.Count.CrossingsIn, otherFix.Data.Count.CorrectsId).Should().Be((false, second, 10, other.Data.Count.Id));
        var firstFixAgain = await Capture(first, s => s.CorrectAsync("VAL13", campaign.Id, exit.Id, recount, exitFixKey, Ct));
        (firstFixAgain.Data.Replayed, firstFixAgain.Data.Count.Id).Should().Be((true, exitFixed.Id));

        // Stored: every key once per observer; the first observer's counts are as it sent them.
        (await TextsAsync("""
            SELECT idempotency_key || '=' || count(DISTINCT observer_id) || '/' || count(*) FROM manual_count
            WHERE campaign_id = @id GROUP BY idempotency_key ORDER BY idempotency_key COLLATE "C"
            """, ("id", campaign.Id))).Should().Equal(
            $"{entryKey}=2/2", $"{entryKey}-fix=2/2", $"{exitKey}=2/2", $"{exitKey}-fix=2/2");
        (await TextsAsync("SELECT crossings_in || '/' || crossings_out FROM manual_count WHERE observer_id = @o AND revision = 1 ORDER BY line_id = @l",
            ("o", first), ("l", p.ExitA))).Should().Equal("5/0", "7/1");
    }

    [Fact]
    public async Task Capture_Should_RefuseTheCampaignsCreatorAndStarter_When_TheyAlsoHoldTheObserverRole()
    {
        // Owner decision 2026-10-08 (separation of duties): an account holding a manager role and the observer role never
        // counts for a campaign it created or started, with or without a key; it may count for another manager's campaign.
        var p = await PlantAsync("VAL14", "VLN");
        var planner = await UserWithRolesAsync([RoleCodes.TerminalDutyManager, RoleCodes.ValidationObserver], "VAL14");
        var starter = await UserWithRolesAsync([RoleCodes.BorderShiftSupervisor, RoleCodes.ValidationObserver], "VAL14");
        var manager = await UserAsync(RoleCodes.BorderShiftSupervisor, "VAL14");
        var observer = await UserAsync(RoleCodes.ValidationObserver, "VAL14");
        var theirs = (await Campaigns(planner, s => s.CreateAsync("VAL14", Request(p), Ct))).Data;
        await Campaigns(starter, s => s.StartAsync("VAL14", theirs.Id, Ct));
        var another = (await Campaigns(manager, s => s.CreateAsync("VAL14", Request(p, name: "Another"), Ct))).Data;
        await Campaigns(manager, s => s.StartAsync("VAL14", another.Id, Ct));

        (await Capture(planner, s => s.CaptureAsync("VAL14", theirs.Id, Count(p.EntryA), null, Ct))).ErrorMessages.Should().Equal(ValidationErrors.OwnCampaign);
        (await Capture(planner, s => s.CaptureAsync("VAL14", theirs.Id, Count(p.EntryA), "dual-01:VAL14-0700", Ct))).ErrorMessages.Should().Equal(ValidationErrors.OwnCampaign);
        (await Capture(starter, s => s.CaptureAsync("VAL14", theirs.Id, Count(p.ExitA), null, Ct))).ErrorMessages.Should().Equal(ValidationErrors.OwnCampaign);
        var counted = await Capture(observer, s => s.CaptureAsync("VAL14", theirs.Id, Count(p.EntryA), null, Ct));
        counted.HasErrors.Should().BeFalse(string.Join(" ", counted.ErrorMessages ?? []));
        (await Capture(planner, s => s.CorrectAsync("VAL14", theirs.Id, counted.Data.Count.Id, new CorrectManualCountRequest(1, 1, "Mine now"), null, Ct)))
            .ErrorMessages.Should().Equal(new[] { ValidationErrors.NotFound }, "another observer's count answers like a missing one");
        (await Capture(planner, s => s.CaptureAsync("VAL14", another.Id, Count(p.EntryA), null, Ct))).HasErrors.Should().BeFalse("another manager's campaign");
        (await Capture(starter, s => s.CaptureAsync("VAL14", another.Id, Count(p.EntryA), null, Ct))).HasErrors.Should().BeFalse("another manager's campaign");

        // Bypassing the service, the insert trigger refuses the creator's and the starter's counts as well (23514).
        foreach (var excluded in new[] { planner, starter })
        {
            var insert = () => ExecuteAsync("""
                INSERT INTO manual_count (id, campaign_id, site_code, line_id, bin_start_utc, observer_id, revision, crossings_in, crossings_out, recorded_utc)
                VALUES (gen_random_uuid(), @campaign, 'VAL14', @line, '2026-10-01T06:45:00Z', @observer, 1, 1, 1, now())
                """, ("campaign", theirs.Id), ("line", p.ExitA), ("observer", excluded));
            (await insert.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
        }

        (await TextsAsync("SELECT observer_id::text FROM manual_count WHERE campaign_id = @id", ("id", theirs.Id))).Should().Equal(observer.ToString());
    }

    [Fact]
    public async Task Captures_Should_KeepOneCount_When_AnObserverSendsTheSameLineAndBinTwiceAtOnce()
    {
        var p = await PlantAsync("VAL3", "VLC");
        var manager = await UserAsync(RoleCodes.SystemAdministrator);
        var observer = await UserAsync(RoleCodes.ValidationObserver, "VAL3");
        var campaign = (await Campaigns(manager, s => s.CreateAsync("VAL3", Request(p), Ct))).Data;
        await Campaigns(manager, s => s.StartAsync("VAL3", campaign.Id, Ct));

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            Capture(observer, s => s.CaptureAsync("VAL3", campaign.Id, Count(p.EntryA, 10 + i), null, Ct))));

        results.Count(r => !r.HasErrors).Should().Be(1);
        results.Where(r => r.HasErrors).SelectMany(r => r.ErrorMessages).Should().OnlyContain(e => e == ValidationErrors.AlreadyCaptured);
        (await TextsAsync("SELECT count(*) FROM manual_count WHERE campaign_id = @id", ("id", campaign.Id))).Should().Equal("1");
    }

    [Fact]
    public async Task Close_Should_LeaveNoCountAfterIt_When_ACountArrivesAfterTheClose()
    {
        var p = await PlantAsync("VAL4", "VLD");
        var manager = await UserAsync(RoleCodes.BorderShiftSupervisor, "VAL4");
        var observer = await UserAsync(RoleCodes.ValidationObserver, "VAL4");
        var campaign = (await Campaigns(manager, s => s.CreateAsync("VAL4", Request(p), Ct))).Data;
        await Campaigns(manager, s => s.StartAsync("VAL4", campaign.Id, Ct));
        var count = (await Capture(observer, s => s.CaptureAsync("VAL4", campaign.Id, Count(p.EntryA), null, Ct))).Data.Count;
        await Campaigns(manager, s => s.CloseAsync("VAL4", campaign.Id, Ct));

        // Bypassing the service, the insert trigger still refuses a count in a closed campaign (23001), which the service maps to 409.
        var insert = () => ExecuteAsync("""
            INSERT INTO manual_count (id, campaign_id, site_code, line_id, bin_start_utc, observer_id, revision, crossings_in, crossings_out, recorded_utc)
            VALUES (gen_random_uuid(), @campaign, 'VAL4', @line, '2026-10-01T06:45:00Z', @observer, 1, 1, 1, now())
            """, ("campaign", campaign.Id), ("line", p.ExitA), ("observer", observer));
        (await insert.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23001");
        var reopen = () => ExecuteAsync("UPDATE validation_campaign SET status = 'Running', closed_by_id = NULL, closed_utc = NULL WHERE id = @id", ("id", campaign.Id));
        (await reopen.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23001", "a closed campaign never changes, not even for the migration login");
        var edit = () => ExecuteAsync("UPDATE manual_count SET crossings_in = 99 WHERE id = @id", ("id", count.Id));
        (await edit.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23001", "a count is never edited");
    }

    [Fact]
    public async Task CloseAndCapture_Should_NeverLeaveACountAfterTheClose_When_TheyRace()
    {
        var p = await PlantAsync("VAL15", "VLO");
        var manager = await UserAsync(RoleCodes.BorderShiftSupervisor, "VAL15");
        var observer = await UserAsync(RoleCodes.ValidationObserver, "VAL15");
        var first = (await Campaigns(manager, s => s.CreateAsync("VAL15", Request(p, name: "Close first"), Ct))).Data;
        var second = (await Campaigns(manager, s => s.CreateAsync("VAL15", Request(p, name: "Count first"), Ct))).Data;
        await Campaigns(manager, s => s.StartAsync("VAL15", first.Id, Ct));
        await Campaigns(manager, s => s.StartAsync("VAL15", second.Id, Ct));

        // The close takes the lock first: one runtime connection holds the campaign FOR UPDATE and closes it, as
        // SvcValidationCampaigns.CloseAsync does, without committing yet. The capture reads the campaign as still running,
        // passes its rules, and its insert waits on the trigger's share lock; once the close commits it sees Closed: 409
        // (23001), nothing stored.
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

            var capture = Capture(observer, s => s.CaptureAsync("VAL15", first.Id, Count(p.EntryA), null, Ct));
            await WaitUntilBlockedAsync(closer.ProcessID, capture);
            await transaction.CommitAsync(Ct);

            (await capture).ErrorMessages.Should().Equal(ValidationErrors.Closed);
        }

        (await TextsAsync("SELECT count(*) FROM manual_count WHERE campaign_id = @id", ("id", first.Id))).Should().Equal("0");
        (await TextsAsync("SELECT status FROM validation_campaign WHERE id = @id", ("id", first.Id))).Should().Equal("Closed");

        // The count takes the lock first: one runtime connection inserts a count (the trigger share-locks the campaign) without
        // committing yet. The close's FOR UPDATE waits; once the count commits the close goes ahead: the count was committed
        // before the close and stays, and nothing is counted after it.
        await using (var counter = await RuntimeConnectionAsync())
        {
            await using var transaction = await counter.BeginTransactionAsync(Ct);
            await using (var insert = new NpgsqlCommand("""
                INSERT INTO manual_count (id, campaign_id, site_code, line_id, bin_start_utc, observer_id, revision, crossings_in, crossings_out, recorded_utc)
                VALUES (gen_random_uuid(), @campaign, 'VAL15', @line, '2026-10-01T06:45:00Z', @observer, 1, 4, 0, now())
                """, counter, transaction))
            {
                insert.Parameters.AddWithValue("campaign", second.Id);
                insert.Parameters.AddWithValue("line", p.EntryA);
                insert.Parameters.AddWithValue("observer", observer);
                await insert.ExecuteNonQueryAsync(Ct);
            }

            var close = Campaigns(manager, s => s.CloseAsync("VAL15", second.Id, Ct));
            await WaitUntilBlockedAsync(counter.ProcessID, close);
            await transaction.CommitAsync(Ct);

            (await close).Data.Status.Should().Be("Closed");
        }

        (await TextsAsync("SELECT count(*) FROM manual_count WHERE campaign_id = @id", ("id", second.Id))).Should().Equal("1");
        (await Capture(observer, s => s.CaptureAsync("VAL15", second.Id, Count(p.ExitA), null, Ct))).ErrorMessages.Should().Equal(ValidationErrors.Closed);
        (await TextsAsync("SELECT count(*) FROM manual_count WHERE campaign_id = @id", ("id", second.Id))).Should().Equal("1");
    }

    #endregion

    #region Scope and refusals

    [Fact]
    public async Task Calls_Should_AnswerNotFound_When_TheCallerIsBoundToAnotherSite()
    {
        var a = await PlantAsync("VAL5", "VLE");
        await PlantAsync("VAL6", "VLF");
        var manager = await UserAsync(RoleCodes.BorderShiftSupervisor, "VAL5");
        var observer = await UserAsync(RoleCodes.ValidationObserver, "VAL5");
        var elsewhere = await UserAsync(RoleCodes.BorderShiftSupervisor, "VAL6");
        var observerElsewhere = await UserAsync(RoleCodes.ValidationObserver, "VAL6");
        var campaign = (await Campaigns(manager, s => s.CreateAsync("VAL5", Request(a), Ct))).Data;
        await Campaigns(manager, s => s.StartAsync("VAL5", campaign.Id, Ct));
        var count = (await Capture(observer, s => s.CaptureAsync("VAL5", campaign.Id, Count(a.EntryA), null, Ct))).Data.Count;

        // The other site's people, through the campaign's site and through their own site's route: NotFound every time.
        foreach (var site in new[] { "VAL5", "VAL6" })
        {
            (await Campaigns(elsewhere, s => s.GetAsync(site, campaign.Id, Ct))).ErrorMessages.Should().Equal(ValidationErrors.NotFound);
            (await Campaigns(elsewhere, s => s.CloseAsync(site, campaign.Id, Ct))).ErrorMessages.Should().Equal(ValidationErrors.NotFound);
            (await Campaigns(elsewhere, s => s.SearchCountsAsync(site, campaign.Id, null, Ct))).ErrorMessages.Should().Equal(ValidationErrors.NotFound);
            (await Capture(observerElsewhere, s => s.CaptureAsync(site, campaign.Id, Count(a.ExitA), null, Ct))).ErrorMessages.Should().Equal(ValidationErrors.NotFound);
            (await Capture(observerElsewhere, s => s.CorrectAsync(site, campaign.Id, count.Id, new CorrectManualCountRequest(1, 1, "x"), null, Ct)))
                .ErrorMessages.Should().Equal(ValidationErrors.NotFound);
        }

        (await Campaigns(elsewhere, s => s.CreateAsync("VAL5", Request(a), Ct))).ErrorMessages.Should().Equal(ValidationErrors.NotFound);
        (await Campaigns(elsewhere, s => s.SearchAsync("VAL6", null, Ct))).Data.Data.Should().BeEmpty();
        (await Capture(observerElsewhere, s => s.RunningAsync("VAL5", Ct))).ErrorMessages.Should().Equal(ValidationErrors.NotFound);
        (await Campaigns(manager, s => s.GetAsync("NOPE", campaign.Id, Ct))).ErrorMessages.Should().Equal(new[] { ValidationErrors.NotFound }, "an unknown site answers the same");
        (await TextsAsync("SELECT count(*) FROM manual_count WHERE campaign_id = @id", ("id", campaign.Id))).Should().Equal("1");
    }

    [Fact]
    public async Task Create_Should_Refuse_When_TheVersionOrScopeIsNotTheSitesPublishedOne()
    {
        var p = await PlantAsync("VAL7", "VLG");
        var other = await PlantAsync("VAL8", "VLH");
        var manager = await UserAsync(RoleCodes.SystemAdministrator);

        (await Campaigns(manager, s => s.CreateAsync("VAL7", Request(p, version: 2), Ct))).ErrorMessages.Should().Equal(ValidationErrors.NotPublished);
        (await Campaigns(manager, s => s.CreateAsync("VAL7", Request(p, lines: [p.EntryB]), Ct))).ErrorMessages.Should().Equal(ValidationErrors.InvalidScope);
        (await Campaigns(manager, s => s.CreateAsync("VAL7", Request(p, lines: [p.HallCount]), Ct))).ErrorMessages.Should().Equal(ValidationErrors.InvalidScope);
        (await Campaigns(manager, s => s.CreateAsync("VAL7", Request(p, lines: [other.EntryA]), Ct))).ErrorMessages.Should().Equal(ValidationErrors.InvalidScope);
        (await Campaigns(manager, s => s.CreateAsync("VAL7", Request(p, name: "bidi \u202e override"), Ct))).ErrorMessages.Should().Equal(ValidationErrors.InvalidName);
        (await Campaigns(manager, s => s.CreateAsync("VAL7", Request(p) with { Days = ["2026-10-01'; DROP TABLE manual_count;"] }, Ct)))
            .ErrorMessages.Should().Equal(ValidationErrors.InvalidDays);
        (await Campaigns(manager, s => s.CreateAsync("VAL7", null, Ct))).HasErrors.Should().BeTrue();
        (await TextsAsync("SELECT count(*) FROM validation_campaign WHERE site_code = 'VAL7'")).Should().Equal("0");

        // Markup and SQL in a name are text: stored as given, parameterised, returned as a value.
        var markup = await Campaigns(manager, s => s.CreateAsync("VAL7", Request(p, name: "<script>alert(1)</script>' OR '1'='1"), Ct));
        markup.Data.Name.Should().Be("<script>alert(1)</script>' OR '1'='1");
        var search = await Campaigns(manager, s => s.SearchAsync("VAL7", new ValidationCampaignCriteria { Text = "' OR '1'='1", SortBy = "name" }, Ct));
        search.Data.Data.Should().ContainSingle().Which.Id.Should().Be(markup.Data.Id);
        (await Campaigns(manager, s => s.SearchAsync("VAL7", new ValidationCampaignCriteria { SortBy = "name; DROP TABLE site" }, Ct))).ErrorMessages.Should().Equal(ValidationErrors.InvalidSort);
        (await Campaigns(manager, s => s.SearchAsync("VAL7", new ValidationCampaignCriteria { Status = "Running' OR 1=1" }, Ct))).ErrorMessages.Should().Equal(ValidationErrors.InvalidStatus);
    }

    [Fact]
    public async Task Retirement_Should_StopStartAndLaterBins_When_ANewVersionIsPublished()
    {
        var p = await PlantAsync("VAL9", "VLI");
        var manager = await UserAsync(RoleCodes.BorderShiftSupervisor, "VAL9");
        var observer = await UserAsync(RoleCodes.ValidationObserver, "VAL9");
        var planned = (await Campaigns(manager, s => s.CreateAsync("VAL9", Request(p), Ct))).Data;
        var running = (await Campaigns(manager, s => s.CreateAsync("VAL9", Request(p, name: "Second"), Ct))).Data;
        await Campaigns(manager, s => s.StartAsync("VAL9", running.Id, Ct));

        // A new version is published at 07:10: version 1 is retired.
        await ExecuteAsync("UPDATE zone_profile SET status = 'Retired', retired_on = '2026-10-01T07:10:00Z' WHERE id = @id", ("id", p.Profile));

        (await Campaigns(manager, s => s.StartAsync("VAL9", planned.Id, Ct))).ErrorMessages.Should().Equal(ValidationErrors.ProfileRetired);
        (await Capture(observer, s => s.CaptureAsync("VAL9", running.Id, Count(p.EntryA, bin: "2026-10-01T06:45:00Z"), null, Ct))).HasErrors.Should().BeFalse("it ended before the retirement");
        (await Capture(observer, s => s.CaptureAsync("VAL9", running.Id, Count(p.EntryA, bin: "2026-10-01T07:00:00Z"), null, Ct))).ErrorMessages.Should().Equal(ValidationErrors.BinAfterRetirement);
        var view = (await Campaigns(manager, s => s.GetAsync("VAL9", running.Id, Ct))).Data;
        (view.ProfileStatus, view.ProfileRetiredUtc).Should().Be(("Retired", new DateTime(2026, 10, 1, 7, 10, 0, DateTimeKind.Utc)));
        (await Campaigns(manager, s => s.CloseAsync("VAL9", planned.Id, Ct))).Data.Status.Should().Be("Closed", "a planned campaign of a retired version can still be closed");
        (await TextsAsync("SELECT count(*) FROM validation_campaign WHERE site_code = 'VAL9' AND status = 'Running'")).Should().Equal("1");
    }

    #endregion

    #region Schema and privileges

    [Fact]
    public async Task Schema_Should_HoldNoNameOrDocumentColumnOfAPerson_When_TheScriptsHaveRun()
    {
        await RuntimeAsync();
        await _admin.DatabaseAsync();
        var columns = await TextsAsync("""
            SELECT table_name || '.' || column_name FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name IN ('validation_campaign', 'validation_campaign_zone', 'validation_campaign_line', 'manual_count')
            ORDER BY 1
            """);

        // Data boundary: people are Ariva user ids (uuid); the only names are of the campaign, zones and lines.
        columns.Should().BeEquivalentTo(
            "manual_count.bin_start_utc", "manual_count.campaign_id", "manual_count.corrects_id", "manual_count.crossings_in", "manual_count.crossings_out",
            "manual_count.id", "manual_count.idempotency_key", "manual_count.line_id", "manual_count.observer_id", "manual_count.reason", "manual_count.recorded_utc",
            "manual_count.revision", "manual_count.site_code",
            "validation_campaign.closed_by_id", "validation_campaign.closed_utc", "validation_campaign.created_by_id", "validation_campaign.created_utc",
            "validation_campaign.geometry_hash", "validation_campaign.id", "validation_campaign.name", "validation_campaign.planned_days",
            "validation_campaign.profile_id", "validation_campaign.profile_version", "validation_campaign.site_code", "validation_campaign.started_by_id",
            "validation_campaign.started_utc", "validation_campaign.status", "validation_campaign.target_bins_per_line", "validation_campaign.target_tracer_runs",
            "validation_campaign.targets_placeholder",
            "validation_campaign_line.campaign_id", "validation_campaign_line.id", "validation_campaign_line.line_id", "validation_campaign_line.line_name",
            "validation_campaign_line.line_role", "validation_campaign_line.profile_id", "validation_campaign_line.queue_zone_name",
            "validation_campaign_zone.campaign_id", "validation_campaign_zone.id", "validation_campaign_zone.profile_id", "validation_campaign_zone.zone_id",
            "validation_campaign_zone.zone_name");
        string[] personal = ["user_name", "display_name", "first_name", "last_name", "full_name", "observer_name", "created_by", "modified_by", "email", "phone",
            "document", "passport", "nationality", "birth", "badge", "staff", "officer", "traveller", "passenger", "mrz", "pnr"];
        var values = columns.Select(c => c.Split('.')[1]).Where(c => c != "id" && !c.EndsWith("_id", StringComparison.Ordinal)).ToList();
        values.Where(c => personal.Any(word => c.Contains(word, StringComparison.Ordinal))).Should().BeEmpty("no column holds a person's name, contact or document");
        columns.Where(c => c.Contains("name", StringComparison.Ordinal)).Should().BeEquivalentTo(
            ["validation_campaign.name", "validation_campaign_zone.zone_name", "validation_campaign_line.line_name", "validation_campaign_line.queue_zone_name"],
            "the only names are of the campaign, its zones and lines");
        (await TextsAsync("""
            SELECT table_name || '.' || column_name || ':' || data_type FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name IN ('validation_campaign', 'manual_count') AND (column_name LIKE '%by_id' OR column_name = 'observer_id')
            ORDER BY 1
            """)).Should().OnlyContain(c => c.EndsWith(":uuid", StringComparison.Ordinal)).And.HaveCount(4);
    }

    [Fact]
    public async Task RuntimeLogin_Should_NeitherChangeNorDeleteEvidence_When_ConnectedAsTheHostsAre()
    {
        var p = await PlantAsync("VAL10", "VLJ");
        var manager = await UserAsync(RoleCodes.BorderShiftSupervisor, "VAL10");
        var observer = await UserAsync(RoleCodes.ValidationObserver, "VAL10");
        var campaign = (await Campaigns(manager, s => s.CreateAsync("VAL10", Request(p), Ct))).Data;
        await Campaigns(manager, s => s.StartAsync("VAL10", campaign.Id, Ct));
        await Capture(observer, s => s.CaptureAsync("VAL10", campaign.Id, Count(p.EntryA), null, Ct));

        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await _admin.DatabaseAsync(), RuntimeLogin, RuntimePassword));
        await connection.OpenAsync(Ct);
        foreach (var sql in new[]
                 {
                     "UPDATE manual_count SET crossings_in = 0",
                     "DELETE FROM manual_count",
                     "TRUNCATE manual_count",
                     "UPDATE validation_campaign SET name = 'renamed'",
                     "UPDATE validation_campaign SET planned_days = '2026-10-02'",
                     "DELETE FROM validation_campaign",
                     "UPDATE validation_campaign_line SET line_name = 'x'",
                     "DELETE FROM validation_campaign_zone",
                     "TRUNCATE validation_campaign_line"
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

        (await TextsAsync("SELECT count(*) FROM manual_count WHERE campaign_id = @id", ("id", campaign.Id))).Should().Equal("1");
    }

    [Fact]
    public async Task Keys_Should_KeepScopeAndCountsToTheCampaignsVersion_When_AStatementBypassesTheService()
    {
        var p = await PlantAsync("VAL11", "VLK");
        var other = await PlantAsync("VAL12", "VLL");
        var manager = await UserAsync(RoleCodes.SystemAdministrator);
        var observer = await UserAsync(RoleCodes.ValidationObserver, "VAL11");
        var campaign = (await Campaigns(manager, s => s.CreateAsync("VAL11", Request(p), Ct))).Data;
        await Campaigns(manager, s => s.StartAsync("VAL11", campaign.Id, Ct));
        var first = (await Capture(observer, s => s.CaptureAsync("VAL11", campaign.Id, Count(p.EntryA), null, Ct))).Data.Count;

        async Task<string> RefusedAsync([System.Diagnostics.CodeAnalysis.ConstantExpected] string sql, params (string Name, object Value)[] parameters)
        {
            var run = () => ExecuteAsync(sql, parameters);
            return (await run.Should().ThrowAsync<PostgresException>(sql)).Which.SqlState;
        }

        // In a planned campaign: a line of another site's profile, a line of a zone out of scope, a line standing alone, an
        // overflow band as a zone. In a running one: any scope row. Then counts: a line out of scope, another site's code, a
        // correction that skips a revision, a bin off the quarter hour.
        var planned = (await Campaigns(manager, s => s.CreateAsync("VAL11", Request(p, name: "Planned"), Ct))).Data;
        (await RefusedAsync("INSERT INTO validation_campaign_line (id, campaign_id, profile_id, line_id, line_name, line_role, queue_zone_name) VALUES (gen_random_uuid(), @c, @p, @l, 'Entry A', 'Entry', 'Q-A')",
            ("c", planned.Id), ("p", p.Profile), ("l", other.EntryA))).Should().Be("23514", "a line of another profile is not on a zone in scope");
        (await RefusedAsync("INSERT INTO validation_campaign_line (id, campaign_id, profile_id, line_id, line_name, line_role, queue_zone_name) VALUES (gen_random_uuid(), @c, @p, @l, 'Entry A', 'Entry', 'Q-A')",
            ("c", planned.Id), ("p", other.Profile), ("l", other.EntryA))).Should().BeOneOf(["23514", "23503"], "the campaign is bound to its own profile version (the trigger, then the key)");
        (await RefusedAsync("INSERT INTO validation_campaign_line (id, campaign_id, profile_id, line_id, line_name, line_role, queue_zone_name) VALUES (gen_random_uuid(), @c, @p, @l, 'Entry B', 'Entry', 'Q-B')",
            ("c", planned.Id), ("p", p.Profile), ("l", p.EntryB))).Should().Be("23514", "Q-B is not in scope");
        (await RefusedAsync("INSERT INTO validation_campaign_line (id, campaign_id, profile_id, line_id, line_name, line_role, queue_zone_name) VALUES (gen_random_uuid(), @c, @p, @l, 'Hall count', 'Count', 'Q-A')",
            ("c", planned.Id), ("p", p.Profile), ("l", p.HallCount))).Should().Be("23514", "a line standing alone is counted by no queue zone");
        (await RefusedAsync("INSERT INTO validation_campaign_zone (id, campaign_id, profile_id, zone_id, zone_name) VALUES (gen_random_uuid(), @c, @p, @z, 'Q-A band')",
            ("c", planned.Id), ("p", p.Profile), ("z", p.BandA))).Should().Be("23514", "an overflow band is not a queue zone");
        (await RefusedAsync("INSERT INTO validation_campaign_zone (id, campaign_id, profile_id, zone_id, zone_name) VALUES (gen_random_uuid(), @c, @p, @z, 'Q-B')",
            ("c", campaign.Id), ("p", p.Profile), ("z", p.QueueB))).Should().Be("23001", "the scope is set while the campaign is planned");
        (await RefusedAsync("""
            INSERT INTO manual_count (id, campaign_id, site_code, line_id, bin_start_utc, observer_id, revision, crossings_in, crossings_out, recorded_utc)
            VALUES (gen_random_uuid(), @c, 'VAL11', @l, '2026-10-01T06:45:00Z', @o, 1, 1, 1, now())
            """, ("c", campaign.Id), ("l", p.EntryB), ("o", observer))).Should().Be("23503");
        (await RefusedAsync("""
            INSERT INTO manual_count (id, campaign_id, site_code, line_id, bin_start_utc, observer_id, revision, crossings_in, crossings_out, recorded_utc)
            VALUES (gen_random_uuid(), @c, 'VAL12', @l, '2026-10-01T06:45:00Z', @o, 1, 1, 1, now())
            """, ("c", campaign.Id), ("l", p.EntryA), ("o", observer))).Should().Be("23503");
        (await RefusedAsync("""
            INSERT INTO manual_count (id, campaign_id, site_code, line_id, bin_start_utc, observer_id, revision, crossings_in, crossings_out, reason, corrects_id, recorded_utc)
            VALUES (gen_random_uuid(), @c, 'VAL11', @l, '2026-10-01T07:00:00Z', @o, 3, 1, 1, 'skip', @f, now())
            """, ("c", campaign.Id), ("l", p.EntryA), ("o", observer), ("f", first.Id))).Should().Be("23514");
        (await RefusedAsync("""
            INSERT INTO manual_count (id, campaign_id, site_code, line_id, bin_start_utc, observer_id, revision, crossings_in, crossings_out, recorded_utc)
            VALUES (gen_random_uuid(), @c, 'VAL11', @l, '2026-10-01T07:07:00Z', @o, 1, 1, 1, now())
            """, ("c", campaign.Id), ("l", p.ExitA), ("o", observer))).Should().Be("23514", "a bin starts on the quarter hour");
        (await TextsAsync("SELECT count(*) FROM manual_count WHERE campaign_id = @id", ("id", campaign.Id))).Should().Equal("1");
    }

    #endregion
}
