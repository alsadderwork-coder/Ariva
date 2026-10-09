using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Ariva.Core;
using Ariva.Core.Domain.Constants;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Security;
using Ariva.Core.Services;
using Ariva.Infra.Services.Reports;
using Ariva.Core.Services.Validation;
using Ariva.Core.Validation;
using Ariva.Core.Validation.Comparison;
using Ariva.Infra.Services.Validation;
using Ariva.Infra.Settings;
using Ariva.Infra.Timescale;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ariva.IntegrationTests.Validation;

/// <summary>
/// ARV-104g2 against PostgreSQL 17 with TimescaleDB, under a real runtime login and the validation reader login: a campaign's
/// results from its own stored rows. Each test plants two sites in Dubai with the same zone, line and desk names (so a read by a
/// name, a prefix or a suffix would mix them), a campaign planned for 1 October 2026 with its ground truth captured through the
/// services (counts, tracer runs, desk states), Ariva's stored outputs written as the stream writes them, the availability
/// ledger and device calibrations. The other site's rows, and a second campaign of the same site, hold values that would fail
/// every criterion if read: the results read only the campaign's site by exact keys and only the campaign's own ground truth
/// (CWE-863). Also: the comparison in slices equals the whole comparison on this campaign; a caller outside the site, or naming
/// another site, gets NotFound; the other site's campaign reads nothing of this one; the shadow read counts before it reads
/// and turns a full pool into a result (ARV-104g1 review, L7); without the reader login the results say the proof was not read.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ValidationResultsTests(PostgresFixture fixture) : IAsyncDisposable
{
    #region Setup

    private const string RuntimeLogin = "it_vres_runtime";
    private const string ReaderLogin = "it_vres_reader";

    // One password per login for the class, so its tests share one pool per login (cleared after each test).
    private static readonly string RuntimePassword = "rt-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
    private static readonly string ReaderPassword = "vr-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));

    private AccountsHost _admin;
    private AccountsHost _runtime;
    private AccountsHost _withoutReader;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTime Utc(int hour, int minute, int second = 0) => new(2026, 10, 1, hour, minute, second, DateTimeKind.Utc);

    public async ValueTask DisposeAsync()
    {
        // Each host's runtime pool and the reader's pool are cleared, so the shared server keeps its connections (ValidationTests' lesson).
        var pools = new List<string>();
        foreach (var host in new[] { _runtime, _withoutReader })
        {
            if (host is null)
                continue;
            if (host.ProviderReady)
            {
                var database = host.Provider.GetRequiredService<DatabaseSettings>();
                pools.Add(database.BuildConnectionString());
                var reader = host.Provider.GetRequiredService<ValidationReaderSettings>();
                if (reader.IsConfigured)
                    pools.Add(new NpgsqlConnectionStringBuilder(reader.BuildReaderConnectionString(database)) { CommandTimeout = 120 }.ConnectionString);
            }

            await host.DisposeAsync();
        }

        foreach (var pool in pools)
        {
            await using var connection = new NpgsqlConnection(pool);
            NpgsqlConnection.ClearPool(connection);
        }

        if (_admin is not null)
            await _admin.DisposeAsync();
    }

    /// <summary>The validation results service as Ariva.Api.Main will register it (ARV-104g): scoped, with its single-flight singleton.</summary>
    private static void Register(IServiceCollection services)
    {
        services.AddSingleton(new ValidationResultsSettings());
        services.AddSingleton(new SingleFlight<ValidationResultsViewModel>(2, TimeSpan.FromMinutes(2), TimeProvider.System, ValidationResultsErrors.TimedOut));
        services.AddScoped<SvcValidationResults>();
        services.AddScoped<ISvcValidationResults>(provider => provider.GetRequiredService<SvcValidationResults>());
    }

    private async Task<AccountsHost> RuntimeAsync(bool withReader = true)
    {
        if (_admin is null)
        {
            _admin = new AccountsHost(fixture, database: TestDatabase.ValidationResults);
            var database = await _admin.DatabaseAsync();
            await using var admin = new NpgsqlConnection(fixture.ConnectionString(database));
            await admin.OpenAsync(Ct);
            await using (var ensure = new NpgsqlCommand("SELECT ariva_ensure_runtime_login(@login, @password)", admin))
            {
                ensure.Parameters.AddWithValue("login", RuntimeLogin);
                ensure.Parameters.AddWithValue("password", RuntimePassword);
                await ensure.ExecuteNonQueryAsync(Ct);
            }

            await using (var reader = new NpgsqlCommand("SELECT ariva_ensure_validation_reader_login(@login, @verifier)", admin))
            {
                reader.Parameters.AddWithValue("login", ReaderLogin);
                reader.Parameters.AddWithValue("verifier", ScramVerifier.For(ReaderPassword));
                await reader.ExecuteNonQueryAsync(Ct);
            }
        }

        var settings = new Dictionary<string, string> { ["Database:Username"] = RuntimeLogin, ["Database:Password"] = RuntimePassword };
        if (withReader)
        {
            settings["Database:ValidationReader:Username"] = ReaderLogin;
            settings["Database:ValidationReader:Password"] = ReaderPassword;
            return _runtime ??= new AccountsHost(fixture, settings, TestDatabase.ValidationResults, Register);
        }

        if (_withoutReader is null)
        {
            _withoutReader = new AccountsHost(fixture, settings, TestDatabase.ValidationResults, Register);
            // The host builds its services with its first account.
            await _withoutReader.CreateUserAsync($"it.vres.none.{Guid.NewGuid():N}"[..32]);
        }

        return _withoutReader;
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

    private sealed record Planted(string Site, Guid Profile, Guid QueueA, Guid QueueB, Guid EntryA, Guid D01, Guid Level, Guid Manager, Guid Observer);

    /// <summary>
    /// A site in Dubai with a published profile version 1 (queue zones Q-A with the line Entry A, and Q-B), an immigration
    /// checkpoint with desk D01, a manager and an observer of the site.
    /// </summary>
    private async Task<Planted> PlantSiteAsync(string site, string iata)
    {
        var host = await RuntimeAsync();
        var p = new Planted(site, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            Guid.Empty, Guid.Empty);
        await ExecuteAsync("""
            INSERT INTO site (id, code, name) VALUES (gen_random_uuid(), @site, @site);
            INSERT INTO airport (id, iata_code, name, time_zone_id) VALUES (gen_random_uuid(), @iata, @iata, 'Asia/Dubai');
            INSERT INTO terminal (id, airport_id, code, name, site_code) SELECT gen_random_uuid(), a.id, 'T1', 'T1', @site FROM airport a WHERE a.iata_code = @iata;
            INSERT INTO level (id, terminal_id, site_code, code, name, floor_number, width_metres, depth_metres)
            SELECT @level, t.id, @site, 'L1', 'L1', 0, 100, 100 FROM terminal t JOIN airport a ON a.id = t.airport_id WHERE a.iata_code = @iata;
            INSERT INTO checkpoint (id, level_id, site_code, code, name, kind) VALUES (@imm, @level, @site, 'IMM', 'Immigration', 'Immigration');
            INSERT INTO desk (id, checkpoint_id, site_code, code, name, kind, lane_category_codes) VALUES (@d01, @imm, @site, 'D01', 'D01', 'Desk', 'CIT');
            INSERT INTO zone_profile (id, site_code, name, status) VALUES (@profile, @site, 'Profile', 'Draft');
            INSERT INTO zone (id, profile_id, name, kind, level_id, polygon, queue_zone_id) VALUES
                (@qa, @profile, 'Q-A', 'Queue', @level, '[]', NULL),
                (@qb, @profile, 'Q-B', 'Queue', @level, '[]', NULL);
            INSERT INTO line (id, profile_id, name, role, zone_id, level_id, start_x, start_y, end_x, end_y) VALUES
                (@entryA, @profile, 'Entry A', 'Entry', @qa, @level, 0, 0, 0, 1);
            UPDATE zone_profile SET status = 'Published', version = 1, geometry_hash = repeat('c', 64), published_on = '2026-09-01T00:00:00Z', published_by = 'it'
             WHERE id = @profile;
            """, ("site", site), ("iata", iata), ("level", p.Level), ("imm", Guid.CreateVersion7()), ("d01", p.D01), ("profile", p.Profile), ("qa", p.QueueA),
            ("qb", p.QueueB), ("entryA", p.EntryA));

        var manager = await host.CreateUserAsync($"it.vres.mgr.{Guid.NewGuid():N}"[..32], roles: [RoleCodes.BorderShiftSupervisor]);
        var observer = await host.CreateUserAsync($"it.vres.obs.{Guid.NewGuid():N}"[..32], roles: [RoleCodes.ValidationObserver]);
        foreach (var user in new[] { manager, observer })
            await ExecuteAsync("INSERT INTO user_site (id, user_id, site_code) VALUES (gen_random_uuid(), @user, @site)", ("user", user), ("site", site));
        return p with { Manager = manager, Observer = observer };
    }

    /// <summary>
    /// Ariva's stored outputs at a site from 06:00 to 07:00 UTC on 1 October, as the stream writes them: queue minutes of Q-A
    /// (each entrant's mean wait <paramref name="wait"/>, the published nowcast <paramref name="nowcast"/>) with the minute after
    /// them, final Good bins of both zones, four health bins of each zone (both count tracks), Entry A's crossings (<paramref name="perMinute"/> a minute,
    /// one minute in fifteen without), D01's minutes in <paramref name="deskState"/>, the shadow nowcast <paramref name="shadow"/>,
    /// the availability ledger (<paramref name="available"/> of 101 operating minutes) and a calibrated device of Q-A.
    /// </summary>
    private async Task PlantOutputsAsync(Planted p, double wait, double nowcast, int perMinute, string deskState, double shadow, double trackRate, int available,
        string deviceCode)
    {
        await ExecuteAsync("""
            INSERT INTO queue_minute (zone_key, minute_utc, profile_version, status, entries, exits, waits, mean_wait_minutes, nowcast_minutes, nowcast_degraded, updated_on)
            SELECT @site || '/Q-A', m, 1, 'Final', 10, 10, 10, @wait, CASE WHEN m < @end THEN @nowcast END, CASE WHEN m < @end THEN false END, now()
              FROM generate_series(@start, @end, interval '1 minute') AS m;
            INSERT INTO queue_bin (zone_key, start_utc, revision, length_minutes, status, quality, entries, exits, waits, abandoned, fragmented, censored, reanchored,
                                   rejected, open_people, late_events, profile_version, updated_on)
            SELECT @site || z, b, 1, 15, 'Final', 'Good', 150, 150, 150, 0, 0, 0, 0, 0, 0, 0, 1, now()
              FROM generate_series(@start - interval '15 minutes', @end + interval '45 minutes', interval '15 minutes') AS b, unnest(ARRAY['/Q-A', '/Q-B']) AS z;
            INSERT INTO zone_health_bin (zone_key, start_utc, revision, length_minutes, status, profile_version, entries, exits, tracks_entered, tracks_exited,
                                         tracks_abandoned, tracks_fragmented, tracks_censored, tracks_rejected, tracks_open, track_completion_rate,
                                         occupancy_minutes, capacity_minutes, minutes_outside_capacity, updated_on)
            SELECT @site || z, b, 1, 15, 'Final', 1, 100, 100, 100, round(100 * @rate), 0, 0, 0, 0, 0, round(100 * @rate) / 100.0, 15, 15, 0, now()
              FROM generate_series(@start, @end - interval '15 minutes', interval '15 minutes') AS b, unnest(ARRAY['/Q-A', '/Q-B']) AS z;
            INSERT INTO line_minute (zone_key, line_name, line_role, source, minute_utc, profile_version, crossings_in, crossings_out, updated_on)
            SELECT @site || '/Q-A', 'Entry A', 'Entry', 'Ariva', m, 1, CASE WHEN extract(minute FROM m)::int % 15 = 14 THEN 0 ELSE @perMinute END, 0, now()
              FROM generate_series(@start, @end - interval '1 minute', interval '1 minute') AS m;
            INSERT INTO desk_minute (desk_code, lane, minute_utc, closed_seconds, idle_seconds, serving_seconds, paused_seconds, unknown_seconds, transactions,
                                     sensor_derived_seconds, present_seconds, degraded, updated_on)
            SELECT @site || '/IMM/D01', 'CIT', m, 0, CASE WHEN @desk = 'Idle' THEN 60 ELSE 0 END, CASE WHEN @desk = 'Serving' THEN 60 ELSE 0 END, 0, 0, 0, 0, 0, false, now()
              FROM generate_series(@start, @end - interval '1 minute', interval '1 minute') AS m;
            INSERT INTO queue_minute_shadow (zone_key, minute_utc, nowcast_minutes, no_service, nowcast_degraded, updated_on)
            SELECT @site || '/Q-A', m, @shadow, NULL, false, now() FROM generate_series(@start, @end - interval '1 minute', interval '1 minute') AS m;
            INSERT INTO availability_minute (site_code, minute_utc, local_date, calendar, state, reasons, zones_expected, zones_stale, zones_missing, zones_lagging,
                                             profile_version, rule_version, decided_utc)
            SELECT @site, m, DATE '2026-10-01', 'Operating', CASE WHEN n <= @available THEN 'Available' ELSE 'Unavailable' END,
                   CASE WHEN n <= @available THEN '{}'::text[] ELSE ARRAY['StaleZone'] END, 2, CASE WHEN n <= @available THEN 0 ELSE 1 END, 0, 0, 1, 1, m + interval '2 minutes'
              FROM generate_series(1, 101) AS n, LATERAL (SELECT @start + (n - 1) * interval '1 minute' AS m) AS t;
            INSERT INTO device (id, code, site_code, family, model, transport, dialect, clock_source, state, level_id, x, y, mounting_height_metres, orientation_degrees,
                                footprint_radius_metres, footprint_source, queue_zone_name)
            VALUES (@device, @code, @site, 'Simulator', 'Sim', 'HttpsPush', 'Canonical', 'Ntp', 'Online', @level, 1, 1, 3, 0, 2, 'Vendor', 'Q-A');
            INSERT INTO device_calibration (id, device_id, site_code, method, sample_size, counting_accuracy_percent, wait_time_error_minutes, threshold_percent, passed,
                                            notes, performed_on)
            VALUES (gen_random_uuid(), @device, @site, 'ManualCountTally', 120, 97.5, 0.4, 95, true, 'Counted by Omar at the north door', '2026-09-30T10:00:00Z'),
                   (gen_random_uuid(), @device, @site, 'ManualCountTally', 120, 99, 0.2, 95, true, NULL, '2026-10-02T10:00:00Z');
            """, ("site", p.Site), ("start", Utc(6, 0)), ("end", Utc(7, 0)), ("wait", wait), ("nowcast", nowcast), ("rate", trackRate), ("perMinute", (long)perMinute),
            ("desk", deskState), ("shadow", shadow), ("available", available), ("device", Guid.CreateVersion7()), ("code", deviceCode), ("level", p.Level));
    }

    private Task<T> As<T>(Guid caller, Func<IServiceProvider, Task<T>> work, bool withReader = true) =>
        (withReader ? _runtime : _withoutReader).AsCallerAsync(caller, work);

    private Task<Fluentx.Result<ValidationResultsViewModel>> ResultsAsync(Guid caller, string site, Guid campaign, bool withReader = true) =>
        As(caller, s => s.GetRequiredService<ISvcValidationResults>().GetAsync(site, campaign, Ct), withReader);

    /// <summary>
    /// A running campaign over Q-A and Q-B, Entry A and desk D01 on 1 October (targets: 1 bin per line, 2 tracer runs), its
    /// observer's counts of Entry A for 06:00 and 06:15 (<paramref name="count"/> in), two tracer runs of 8 minutes in Q-A, and
    /// D01's 15 minutes from <paramref name="deskFrom"/> logged <paramref name="deskState"/>.
    /// </summary>
    private async Task<Guid> CampaignAsync(Planted p, string name, int count, string deskFrom, string deskState)
    {
        var created = await As(p.Manager, s => s.GetRequiredService<ISvcValidationCampaigns>().CreateAsync(p.Site,
            new CreateValidationCampaignRequest(name, 1, [p.QueueA, p.QueueB], [p.EntryA], ["2026-10-01"], 1, 2, [p.D01]), Ct));
        created.HasErrors.Should().BeFalse(string.Join(" ", created.ErrorMessages ?? []));
        var id = created.Data.Id;
        (await As(p.Manager, s => s.GetRequiredService<ISvcValidationCampaigns>().StartAsync(p.Site, id, Ct))).HasErrors.Should().BeFalse();

        foreach (var bin in new[] { "2026-10-01T06:00:00Z", "2026-10-01T06:15:00Z" })
        {
            var counted = await As(p.Observer, s => s.GetRequiredService<ISvcValidationCapture>().CaptureAsync(p.Site, id,
                new CaptureManualCountRequest(p.EntryA, bin, count, 0), $"count:{Tag(name)}:{bin[11..13]}{bin[14..16]}", Ct));
            counted.HasErrors.Should().BeFalse(string.Join(" ", counted.ErrorMessages ?? []));
        }

        var runs = await As(p.Observer, s => s.GetRequiredService<ISvcTracerRuns>().CaptureAsync(p.Site, id, new CaptureTracerRunsRequest("2026-10-01T08:00:00.000Z",
        [
            new TracerRunRequest(p.QueueA, "T-01", "2026-10-01T06:10:30.000Z", "2026-10-01T06:18:30.000Z", false),
            new TracerRunRequest(p.QueueA, "T-02", "2026-10-01T06:20:00.000Z", "2026-10-01T06:28:00.000Z", false)
        ]), $"tracers:{Tag(name)}", Ct));
        runs.HasErrors.Should().BeFalse(string.Join(" ", runs.ErrorMessages ?? []));

        var states = Enumerable.Repeat(deskState, 15).ToArray();
        var desks = await As(p.Observer, s => s.GetRequiredService<ISvcDeskObservations>().CaptureAsync(p.Site, id,
            new CaptureDeskObservationsRequest(deskFrom, [new DeskMinutesRequest(p.D01, states)]), $"desks:{Tag(name)}", Ct));
        desks.HasErrors.Should().BeFalse(string.Join(" ", desks.ErrorMessages ?? []));
        return id;
    }

    /// <summary>A campaign's name as a part of an Idempotency-Key (letters and digits only).</summary>
    private static string Tag(string name) => new([.. name.Where(char.IsAsciiLetterOrDigit)]);

    private static CampaignCriterionVerdict Criterion(ValidationResultsViewModel results, CampaignCriterion criterion) =>
        results.Criteria.Single(c => c.Criterion == criterion);

    #endregion

    [Fact]
    public async Task Results_Should_JudgeTheCampaignOnItsOwnRowsAtItsSite_When_AnotherSiteAndAnotherCampaignHoldRowsThatWouldFailIt()
    {
        var p = await PlantSiteAsync("VRA1", "VQA");
        var other = await PlantSiteAsync("VRA2", "VQB");
        // The campaign's site: waits of 8.2 against tracers of 8 (error 0.2, bias 2.5 percent), nowcasts of 8 (error 0.2),
        // 98 of 100 crossings a bin, D01 Serving as observed, track completion 0.95, 100 of 101 minutes available.
        await PlantOutputsAsync(p, 8.2, 8, 7, "Serving", 9, 0.95, 100, "CAM-A1");
        // The other site, with the same zone, line and desk names: every value would fail a criterion if read.
        await PlantOutputsAsync(other, 50, 1, 70, "Idle", 99, 0.10, 0, "CAM-B1");
        await ExecuteAsync("INSERT INTO zone_outage (zone_key, device_code, from_utc, to_utc, closed, recorded_on) VALUES ('VRA2/Q-A', 'CAM-B1', @from, @to, true, now())",
            ("from", Utc(5, 0)), ("to", Utc(8, 0)));

        var campaign = await CampaignAsync(p, "Pilot week", 100, "2026-10-01T06:00:00Z", "Serving");
        // A second campaign of the same site: other counts of the same line and bins, desk states at other minutes that disagree.
        await CampaignAsync(p, "Rehearsal", 50, "2026-10-01T06:30:00Z", "Idle");
        await CampaignAsync(other, "Other site", 10, "2026-10-01T06:00:00Z", "Closed");

        var read = await ResultsAsync(p.Manager, "VRA1", campaign);

        read.HasErrors.Should().BeFalse(string.Join(" ", read.ErrorMessages ?? []));
        var results = read.Data;
        (results.CampaignId, results.SiteCode, results.ProfileVersion, results.GeometryHash, results.TimeZoneId, results.Status, results.Problem)
            .Should().Be((campaign, "VRA1", 1, new string('c', 64), "Asia/Dubai", "Running", (ComparisonProblem?)null));
        results.PlannedDays.Should().Equal("2026-10-01");
        (results.Targets.BinsPerLine, results.Targets.TracerRuns, results.Targets.DeskMinutes, results.Targets.NowcastMinutes).Should().Be((1, 2, 15, 15));

        // Every criterion passes on this campaign's rows alone.
        results.Criteria.Select(c => (c.Criterion, c.Verdict)).Should().Equal(
            (CampaignCriterion.CountAccuracy, CriterionVerdict.Pass), (CampaignCriterion.WaitError, CriterionVerdict.Pass),
            (CampaignCriterion.WaitBias, CriterionVerdict.Pass), (CampaignCriterion.TrackCompletion, CriterionVerdict.Pass),
            (CampaignCriterion.NowcastError, CriterionVerdict.Pass), (CampaignCriterion.Availability, CriterionVerdict.Pass));
        Criterion(results, CampaignCriterion.CountAccuracy).Value.Should().BeApproximately(0.98, 1e-12);
        results.Counts.Bins.Should().HaveCount(2).And.OnlyContain(b => b.ManualCount == 100 && b.SystemCount == 98 && b.Observers == 1,
            "only this campaign's counts, only this site's crossings");
        Criterion(results, CampaignCriterion.WaitBias).Value.Should().BeApproximately(0.025, 1e-9);
        results.Observers.Runs.Should().HaveCount(2).And.OnlyContain(r => r.SystemWaitMinutes == 8.2 && r.ObserverId == p.Observer);
        Criterion(results, CampaignCriterion.TrackCompletion).Value.Should().BeApproximately(0.95, 1e-12);
        Criterion(results, CampaignCriterion.NowcastError).Should().Match<CampaignCriterionVerdict>(c => c.Judged == 60 && c.Excluded == 0);
        Criterion(results, CampaignCriterion.NowcastError).Value.Should().BeApproximately(0.2, 1e-9);
        Criterion(results, CampaignCriterion.Availability).Value.Should().BeApproximately(100 / 101.0, 1e-12);
        (results.Availability.Total.OperatingMinutes, results.Availability.Total.AvailableMinutes, results.Availability.Days.Single().Date)
            .Should().Be((101, 100, new DateOnly(2026, 10, 1)));

        // The shadow (the proof) read through the reader login, this site's only; the nowcast's coverage of the planned day.
        results.Nowcast.ShadowRead.Should().BeTrue();
        results.Nowcast.Overall.Both.Minutes.Should().Be(60);
        results.Nowcast.Overall.Both.Shadow.MeanErrorMinutes.Should().BeApproximately(0.8, 1e-9, "9 against 8.2, never the other site's 99");
        var qa = results.Nowcast.Coverage.Single(c => c.QueueZone == "Q-A");
        // 05:59 to 06:59 have a next minute with a realised wait; 06:00 to 06:59 published a nowcast, 05:59 has no row.
        (qa.PlannedMinutes, qa.WithRealisedWait, qa.Published, qa.Missing).Should().Be((1440, 61, 60, 1));
        results.Nowcast.CoverageOverall.PlannedMinutes.Should().Be(2880);

        // Desks: this campaign's 15 observed minutes only, all agreeing (the rehearsal's Idle minutes are not read).
        results.Desks.Minutes.Should().HaveCount(15).And.OnlyContain(m => m.Agrees == true && m.MinuteUtc < Utc(6, 15));
        results.Desks.Verdict.Verdict.Should().Be(CriterionVerdict.Pass);
        results.Desks.Verdict.Value.Should().Be(1.0);

        // Calibration records of this site's device of Q-A performed before the planned day ended; never the notes, never the other site's.
        results.Calibrations.Should().ContainSingle().Which.Should().Be(new ValidationResultsViewModel.CalibrationView("CAM-A1", "Q-A", "ManualCountTally", 120, 97.5, 0.4, 95,
            true, new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc)));
        JsonSerializer.Serialize(results).Should().NotContain("Omar").And.NotContain("CAM-B1");

        // Nothing left out; to review: the running campaign and the planned minute without a nowcast.
        results.LeftOut.Should().Match<ValidationResultsViewModel.LeftOutView>(l => l.Keys.Count == 0 && l.QualityIntervals == 0 && l.QueueMinutes == 0);
        // 05:59 is a planned minute whose next minute has a realised wait but which has no row: flagged (L5).
        results.Review.Should().Equal(CampaignReview.CampaignNotClosed, CampaignReview.NowcastMissing);
    }

    [Fact]
    public async Task Results_Should_EqualTheWholeComparison_When_TheCampaignIsComparedInSlices()
    {
        var p = await PlantSiteAsync("VRB1", "VQC");
        await PlantOutputsAsync(p, 8.2, 8, 7, "Serving", 9, 0.95, 100, "CAM-C1");
        // More for the comparison to keep: Q-B's minutes without a shadow, a closed outage in Q-B and one still open in Q-A.
        await ExecuteAsync("""
            INSERT INTO queue_minute (zone_key, minute_utc, profile_version, status, entries, exits, waits, mean_wait_minutes, nowcast_minutes, nowcast_degraded, updated_on)
            SELECT 'VRB1/Q-B', m, 1, 'Final', 5, 5, 5, 4, 3, false, now() FROM generate_series(@from, @to, interval '1 minute') AS m;
            INSERT INTO zone_outage (zone_key, device_code, from_utc, to_utc, closed, recorded_on) VALUES ('VRB1/Q-B', 'CAM-X', @outFrom, @outTo, true, now());
            INSERT INTO zone_outage (zone_key, device_code, from_utc, to_utc, closed, recorded_on) VALUES ('VRB1/Q-A', 'CAM-Y', @open, @open + interval '1 minute', false, now());
            """, ("from", Utc(6, 0)), ("to", Utc(6, 40)), ("outFrom", Utc(6, 20)), ("outTo", Utc(6, 25)), ("open", Utc(6, 50)));
        var campaign = await CampaignAsync(p, "Slices", 100, "2026-10-01T06:00:00Z", "Serving");

        var (sliced, whole) = await As(p.Manager, async s =>
        {
            var service = s.GetRequiredService<SvcValidationResults>();
            return (await service.CompareAsync("VRB1", campaign, sliced: true, Ct), await service.CompareAsync("VRB1", campaign, sliced: false, Ct));
        });

        sliced.HasErrors.Should().BeFalse(string.Join(" ", sliced.ErrorMessages ?? []));
        whole.Data.Result.Problem.Should().BeNull("the campaign is within the engine's bounds");
        whole.Data.Result.NowcastZones.Should().HaveCount(2);
        whole.Data.Result.Tracers.Should().HaveCount(2);
        whole.Data.Result.NowcastMinutes.Should().Contain(m => m.Standing == ComparisonStanding.Degraded);
        JsonSerializer.Serialize(sliced.Data.Result).Should().Be(JsonSerializer.Serialize(whole.Data.Result));
        JsonSerializer.Serialize(sliced.Data.Coverage).Should().Be(JsonSerializer.Serialize(whole.Data.Coverage));
        sliced.Data.ShadowRead.Should().BeTrue();
    }

    [Fact]
    public async Task Results_Should_AnswerNotFoundAcrossSitesAndReadNothingOfTheOtherSite_When_TheCampaignIsOfAnotherSite()
    {
        var p = await PlantSiteAsync("VRC1", "VQD");
        var other = await PlantSiteAsync("VRC2", "VQE");
        await PlantOutputsAsync(p, 8.2, 8, 7, "Serving", 9, 0.95, 100, "CAM-D1");
        var mine = await CampaignAsync(p, "Mine", 100, "2026-10-01T06:00:00Z", "Serving");
        // The other site's campaign: its site has no stored output at all.
        var theirs = await CampaignAsync(other, "Theirs", 100, "2026-10-01T06:00:00Z", "Serving");

        // A manager of the other site: NotFound for this site's campaign, by either site code (CWE-863, CWE-204).
        (await ResultsAsync(other.Manager, "VRC1", mine)).ErrorMessages.Should().Equal(ValidationErrors.NotFound);
        (await ResultsAsync(other.Manager, "VRC2", mine)).ErrorMessages.Should().Equal(ValidationErrors.NotFound);
        (await ResultsAsync(p.Manager, "VRC1", theirs)).ErrorMessages.Should().Equal(ValidationErrors.NotFound);
        (await ResultsAsync(p.Manager, "VRC1", Guid.Empty)).ErrorMessages.Should().Equal(ValidationErrors.NotFound);
        (await ResultsAsync(p.Manager, "vrc1' OR '1'='1", mine)).ErrorMessages.Should().Equal(ValidationErrors.NotFound);

        // The other site's campaign reads nothing of this site, though its zones, line and desk carry the same names.
        var read = await ResultsAsync(other.Manager, "VRC2", theirs);
        read.HasErrors.Should().BeFalse(string.Join(" ", read.ErrorMessages ?? []));
        var results = read.Data;
        results.Counts.Bins.Should().NotBeEmpty().And.OnlyContain(b => b.Standing != ComparisonStanding.Good, "no stored bin of VRC2 covers them");
        results.Nowcast.Minutes.Should().BeEmpty();
        results.Nowcast.Overall.Both.Minutes.Should().Be(0);
        results.Observers.Runs.Should().OnlyContain(r => r.SystemWaitMinutes == null);
        results.Desks.Minutes.Should().OnlyContain(m => !m.SystemStored);
        results.TrackCompletion.Should().OnlyContain(z => z.Good.Bins == 0);
        results.Calibrations.Should().BeEmpty();
        results.Availability.Total.RecordedMinutes.Should().Be(0);
        results.Criteria.Should().OnlyContain(c => c.Verdict != CriterionVerdict.Pass, "nothing of VRC1 lets it pass");

        // Two callers at once share one computation's result.
        var both = await Task.WhenAll(ResultsAsync(p.Manager, "VRC1", mine), ResultsAsync(p.Manager, "VRC1", mine));
        both.Should().OnlyContain(r => !r.HasErrors);
        JsonSerializer.Serialize(both[0].Data with { ComputedUtc = default }).Should().Be(JsonSerializer.Serialize(both[1].Data with { ComputedUtc = default }));
    }

    [Fact]
    public async Task Results_Should_SayTheProofWasNotRead_When_NoValidationReaderLoginIsConfigured()
    {
        var p = await PlantSiteAsync("VRD1", "VQF");
        await PlantOutputsAsync(p, 8.2, 8, 7, "Serving", 9, 0.95, 100, "CAM-E1");
        var campaign = await CampaignAsync(p, "No reader", 100, "2026-10-01T06:00:00Z", "Serving");
        await RuntimeAsync(withReader: false);

        var read = await ResultsAsync(p.Manager, "VRD1", campaign, withReader: false);

        read.HasErrors.Should().BeFalse(string.Join(" ", read.ErrorMessages ?? []));
        read.Data.Nowcast.ShadowRead.Should().BeFalse();
        read.Data.Nowcast.Overall.Both.Minutes.Should().Be(0);
        read.Data.Review.Should().Contain(CampaignReview.ProofNotRead);
        Criterion(read.Data, CampaignCriterion.NowcastError).Verdict.Should().Be(CriterionVerdict.Pass, "the published nowcast is judged without the proof");
    }

    [Fact]
    public async Task Reads_Should_BeRefusedRatherThanCutShort_When_TheyPassTheRowLimit()
    {
        // M1 of the ARV-104g2 review (CWE-120, CWE-400): a ground-truth read, a per-day read and a per-zone read each return
        // nothing (refused) one row beyond their limit and every row at it, and the computation answers TooLarge, never a
        // comparison over a list cut short. The campaign holds 15 desk states, 4 bins of Entry A's crossings on its day and 61
        // queue minutes of Q-A.
        var p = await PlantSiteAsync("VRF1", "VQH");
        await PlantOutputsAsync(p, 8.2, 8, 7, "Serving", 9, 0.95, 100, "CAM-H1");
        var campaign = await CampaignAsync(p, "Bounds", 100, "2026-10-01T06:00:00Z", "Serving");
        var day = new UtcWindow(new DateTime(2026, 9, 30, 20, 0, 0, DateTimeKind.Utc), Utc(20, 0));
        var zones = new Dictionary<string, string>(StringComparer.Ordinal) { ["VRF1/Q-A"] = "Q-A", ["VRF1/Q-B"] = "Q-B" };

        await As(p.Manager, async s =>
        {
            SvcValidationResults Limited(int limit) => new(s.GetRequiredService<IUnitOfWork>(), s.GetRequiredService<ICurrentUser>(), TimeProvider.System,
                s.GetRequiredService<ISiteScope>(), s.GetRequiredService<ReportReader>(), s.GetRequiredService<IServiceScopeFactory>(),
                s.GetRequiredService<SingleFlight<ValidationResultsViewModel>>(), new ValidationResultsSettings(), null, s.GetRequiredService<DatabaseSettings>(),
                s.GetRequiredService<ValidationReaderSettings>()) { RowLimit = limit };
            var entity = await Limited(1_000).CampaignByIdAsync("VRF1", campaign, Ct);

            (await Limited(14).DeskObservationsAsync(entity, Ct)).Should().BeNull("a ground-truth read beyond its limit is refused");
            (await Limited(15).DeskObservationsAsync(entity, Ct)).Should().HaveCount(15);
            (await Limited(3).LineBinsAsync(zones, ["Entry A"], day, Ct)).Should().BeNull("a per-day read beyond its limit is refused");
            (await Limited(4).LineBinsAsync(zones, ["Entry A"], day, Ct)).Should().HaveCount(4);
            (await Limited(60).QueueMinutesAsync("VRF1", "Q-A", day, Ct)).Should().BeNull("a per-zone read beyond its limit is refused");
            (await Limited(61).QueueMinutesAsync("VRF1", "Q-A", day, Ct)).Should().HaveCount(61);
            (await Limited(18).QueueBinsAsync(zones, day, Ct)).Should().HaveCount(18);
            (await Limited(17).QueueBinsAsync(zones, day, Ct)).Should().BeNull();

            (await Limited(14).CompareAsync("VRF1", campaign, sliced: true, Ct)).ErrorMessages.Should().Equal(ValidationResultsErrors.TooLarge);
            (await Limited(60).CompareAsync("VRF1", campaign, sliced: true, Ct)).ErrorMessages.Should().Equal(ValidationResultsErrors.TooLarge);
            (await Limited(61).CompareAsync("VRF1", campaign, sliced: true, Ct)).HasErrors.Should().BeFalse("at the limit every row is read");
            return 0;
        });
    }

    [Fact]
    public async Task ShadowRead_Should_RefuseBeforeReadingAndAnswerAFullPoolWithAResult_When_ReadsExceedTheirBounds()
    {
        var p = await PlantSiteAsync("VRE1", "VQG");
        await PlantOutputsAsync(p, 8.2, 8, 7, "Serving", 9, 0.95, 100, "CAM-F1");
        var host = await RuntimeAsync();
        var database = host.Provider.GetRequiredService<DatabaseSettings>();
        var reader = host.Provider.GetRequiredService<ValidationReaderSettings>();

        // L7: the 60 shadow minutes are counted through a LIMITed query and refused at a limit of 59, before any row is read.
        var limited = await ValidationReaderLoginTests.Results(database, reader, rowLimit: 59).ReadShadowAsync("VRE1", ["Q-A"], Utc(0, 0), Utc(23, 0), Ct);
        limited.ErrorMessages.Should().ContainSingle().Which.Should().Contain("read per zone or per day");
        (await ValidationReaderLoginTests.Results(database, reader, rowLimit: 60).ReadShadowAsync("VRE1", ["Q-A"], Utc(0, 0), Utc(23, 0), Ct)).Data.Should().HaveCount(60);

        // L7: the reader's pool of four held by others: the read waits for its connect timeout, then answers with an error result.
        var pooled = new NpgsqlConnectionStringBuilder(reader.BuildReaderConnectionString(database)) { CommandTimeout = 120 }.ConnectionString;
        var held = new List<NpgsqlConnection>();
        try
        {
            for (var i = 0; i < ValidationReaderSettings.MaxPoolSize; i++)
            {
                var connection = new NpgsqlConnection(pooled);
                await connection.OpenAsync(Ct);
                held.Add(connection);
            }

            var busy = await ValidationReaderLoginTests.Results(database, reader).ReadShadowAsync("VRE1", ["Q-A"], Utc(0, 0), Utc(23, 0), Ct);

            busy.HasErrors.Should().BeTrue();
            busy.ErrorMessages.Should().Equal(ValidationResultsErrors.Busy);
        }
        finally
        {
            foreach (var connection in held)
                await connection.DisposeAsync();
        }

        (await ValidationReaderLoginTests.Results(database, reader).ReadShadowAsync("VRE1", ["Q-A"], Utc(0, 0), Utc(23, 0), Ct)).HasErrors.Should().BeFalse();
    }
}
