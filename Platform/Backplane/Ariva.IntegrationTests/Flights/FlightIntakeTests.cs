using System.Diagnostics;
using Ariva.Core;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Flights;
using Ariva.Core.Services;
using Ariva.Core.Services.Administration;
using Ariva.Core.Services.Flights;
using Ariva.Core.Services.Topology;
using Ariva.Di.Extensions;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Seed;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Ariva.IntegrationTests.Flights;

/// <summary>
/// ARV-041 against TimescaleDB with script 0024: legs, milestones and counter allocations enter through the intake one
/// item at a time (a bad item never stops the others), in any order with the same result, under a lock per leg (two
/// feeds creating one leg at once make one row); allocations resolve AODB counter codes and keep the rest apart; every
/// call refreshes its feed, and the sweep raises the stale-feed alarm only while flights are due; the database repeats
/// the rules and the runtime role cannot rewrite the milestone record.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class FlightIntakeTests(PostgresFixture fixture) : IAsyncDisposable
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Guid? _admin;

    private readonly AccountsHost _host = new(fixture, database: TestDatabase.Flights,
        configure: services =>
        {
            services.AddArivaFlights(new ConfigurationBuilder().Build(), watchFeeds: false);
            services.AddArivaFlightSchedules(new ConfigurationBuilder().Build());
            // The intake, with a schedule unit that can be made to fail after applying its legs (before its commit).
            services.AddScoped<Ariva.Infra.Services.Flights.SvcFlightIntake>();
            services.Replace(ServiceDescriptor.Scoped<ISvcFlightIntake>(s => new FailingIntake(s.GetRequiredService<Ariva.Infra.Services.Flights.SvcFlightIntake>())));
        });

    /// <summary>The real intake; while <see cref="FailScheduleCall"/> is set, that call of the schedule path throws once its legs are applied.</summary>
    private sealed class FailingIntake(ISvcFlightIntake inner) : ISvcFlightIntake
    {
        public static int FailScheduleCall;
        private static int _calls;

        public static void Arm(int call) => (FailScheduleCall, _calls) = (call, 0);

        public Task<IReadOnlyList<FlightItemResult>> ApplyLegsAsync(string siteCode, string feed, IReadOnlyList<FlightLegData> legs, DateTime? sourceUtc,
            CancellationToken ct = default) => inner.ApplyLegsAsync(siteCode, feed, legs, sourceUtc, ct);

        public async Task<IReadOnlyList<FlightItemResult>> ApplyScheduleLegsAsync(string siteCode, string feed, IReadOnlyList<FlightLegData> legs, DateTime sourceUtc,
            CancellationToken ct = default)
        {
            var results = await inner.ApplyScheduleLegsAsync(siteCode, feed, legs, sourceUtc, ct);
            if (FailScheduleCall > 0 && Interlocked.Increment(ref _calls) == FailScheduleCall)
                throw new InvalidOperationException("A unit failed after applying its legs (test).");
            return results;
        }

        public Task<IReadOnlyList<FlightItemResult>> ApplyEventsAsync(string siteCode, string feed, IReadOnlyList<FlightEventData> events, DateTime? sourceUtc,
            CancellationToken ct = default) => inner.ApplyEventsAsync(siteCode, feed, events, sourceUtc, ct);

        public Task<IReadOnlyList<FlightItemResult>> ApplyAllocationsAsync(string siteCode, string feed, IReadOnlyList<CounterAllocationData> allocations,
            DateTime? sourceUtc, CancellationToken ct = default) => inner.ApplyAllocationsAsync(siteCode, feed, allocations, sourceUtc, ct);
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DateTime Now => _host.Clock.GetUtcNow().UtcDateTime;

    private async Task<Guid> AdminAsync()
    {
        await Gate.WaitAsync(Ct);
        try
        {
            if (_admin is { } known)
            {
                await _host.CreateUserAsync("it.flight.probe." + Guid.NewGuid().ToString("N")[..8]);
                return known;
            }

            var admin = await _host.CreateUserAsync("it.flight.admin", roles: [RoleCodes.SystemAdministrator]);
            await _host.ReadAsync<int>("UPDATE \"user\" SET all_sites = true WHERE id = @id RETURNING 1", admin);
            await _host.AsCallerAsync(null, s =>
                new DemoTopologySeed(s.GetRequiredService<IUnitOfWork>(), s.GetRequiredService<ICurrentUser>(), _host.Clock, s.GetRequiredService<AuditTrail>()).RunAsync(Ct));
            await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSites>().CreateAsync(new CreateSiteRequest("FRS", "Freshness airport"), Ct));
            await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSites>().CreateAsync(new CreateSiteRequest("IDL", "Idle airport"), Ct));
            foreach (var (code, desk) in new[] { ("CNT-1", "A01"), ("CNT-2", "A02") })
            {
                var deskId = await _host.ReadAsync<Guid>("SELECT d.id FROM desk d JOIN checkpoint c ON c.id = d.checkpoint_id WHERE c.site_code = 'DMO' AND c.code = 'CI' AND d.code = @secret",
                    secret: desk);
                var mapped = await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcDeskCodeMappings>().CreateAsync(new CreateDeskCodeMappingRequest("Aodb", code, deskId), Ct));
                mapped.HasErrors.Should().BeFalse(string.Join(", ", mapped.ErrorMessages ?? []));
            }

            _admin = admin;
            return admin;
        }
        finally
        {
            Gate.Release();
        }
    }

    private Task<IReadOnlyList<FlightItemResult>> LegsAsync(string site, string feed, DateTime? source, params FlightLegData[] legs) =>
        _host.AsCallerAsync(null, s => s.GetRequiredService<ISvcFlightIntake>().ApplyLegsAsync(site, feed, legs, source, Ct));

    private Task<IReadOnlyList<FlightItemResult>> EventsAsync(string site, DateTime? source, params FlightEventData[] events) =>
        _host.AsCallerAsync(null, s => s.GetRequiredService<ISvcFlightIntake>().ApplyEventsAsync(site, "aidx", events, source, Ct));

    private Task<IReadOnlyList<FlightItemResult>> AllocationsAsync(string site, DateTime? source, params CounterAllocationData[] allocations) =>
        _host.AsCallerAsync(null, s => s.GetRequiredService<ISvcFlightIntake>().ApplyAllocationsAsync(site, "api", allocations, source, Ct));

    private Task<FeedFreshnessSweep> SweepAsync() => _host.AsCallerAsync(null, s => s.GetRequiredService<ISvcFeedFreshness>().SweepAsync(Ct));

    private FlightLegData Arrival(string key, int inMinutes = 120, string carrier = "DM") =>
        new(key, carrier, "214", null, "Arrival", Now.AddMinutes(inMinutes), Origin: "BEY", Destination: "DMO", Stand: "B12", Seats: 180, PaxEstimate: 160);

    private FlightLegData Departure(string key, int inMinutes = 180) =>
        new(key, "XR", "331", null, "Departure", Now.AddMinutes(inMinutes), Origin: "DMO", Destination: "DXB", Seats: 180);

    [Fact]
    public async Task Legs_Should_BeCheckedOneByOneAndAppliedByMessageTime_When_AFeedSendsThem()
    {
        await AdminAsync();
        var key = "DM214-LEGS-A";
        var results = await LegsAsync("DMO", "aidx", Now.AddMinutes(-10), Arrival(key), Arrival("DM9-BAD-A", carrier: "D'X"), Departure("XR331-LEGS-D"));

        results.Select(r => (r.Index, r.Applied, r.HasErrors)).Should().Equal((0, true, false), (1, false, true), (2, true, false));
        results[1].Errors.Should().ContainSingle().Which.Should().Contain("carrier");
        (await _host.ReadAsync<long>("SELECT count(*) FROM flight_leg WHERE site_code = 'DMO' AND flight_key LIKE '%-LEGS-%'")).Should().Be(2);
        (await _host.ReadAsync<long>("SELECT count(*) FROM outbox_message WHERE topic = 'ariva.flight.flight-changed.v1' AND message_key = @secret", secret: "DMO/" + key))
            .Should().Be(1, "FlightChanged goes through the outbox, keyed by site and flight key");

        // An older snapshot changes nothing; a newer one does; repeating it changes nothing again.
        (await LegsAsync("DMO", "aidx", Now.AddMinutes(-20), Arrival(key) with { Stand = "C1" }))[0].Should().Match<FlightItemResult>(r => !r.Applied && !r.HasErrors);
        (await LegsAsync("DMO", "acris", Now.AddMinutes(-5), Arrival(key) with { Stand = "B14" }))[0].Applied.Should().BeTrue();
        (await LegsAsync("DMO", "aidx", Now.AddMinutes(-4), Arrival(key) with { Stand = "B14" }))[0].Applied.Should().BeFalse();
        (await _host.ReadAsync<string>("SELECT stand || ' ' || feed FROM flight_leg WHERE flight_key = @secret", secret: key)).Should().Be("B14 acris");

        // The same key for the other direction is another flight.
        (await LegsAsync("DMO", "aidx", null, Departure(key)))[0].Errors.Should().ContainSingle().Which.Should().Contain("other direction");
    }

    private AidxLeg AidxLeg(string number, string from, string to, int inMinutes) =>
        new("RJ", number, null, from, to, DateOnly.FromDateTime(Now), null,
            [new AidxTime(to == "DMO" ? "ONB" : "OFB", "SCT", Now.AddMinutes(inMinutes))], AidxResources.None, new AidxResources("T1", "B7", "G7"), "320", ["XR1214"]);

    [Fact]
    public async Task Aidx_Should_ApplyTheSitesLegsAtTheirOwnIndex_When_AMessageArrives()
    {
        await AdminAsync();
        var message = new AidxMessage(Now.AddMinutes(-1), "T-1", [
            AidxLeg("701", "AMM", "DMO", 90),
            AidxLeg("702", "AMM", "CAI", 90),
            null,
            AidxLeg("703", "DMO", "CAI", 150)
        ]);

        var results = await _host.AsCallerAsync(null, s => s.GetRequiredService<ISvcAidxIntake>().ApplyAsync("DMO", "api-aidx", message, Ct));

        results.Select(r => (r.Index, r.Applied, r.HasErrors)).Should().Equal((0, true, false), (1, false, true), (2, false, true), (3, true, false));
        var day = Now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        results[0].FlightKey.Should().Be($"RJ701-{day}-A");
        results[3].FlightKey.Should().Be($"RJ703-{day}-D");
        (await _host.ReadAsync<string>("SELECT direction || ' ' || coalesce(stand, '-') || ' ' || feed FROM flight_leg WHERE site_code = 'DMO' AND flight_key = @secret",
            secret: $"RJ701-{day}-A")).Should().Be("Arrival B7 api-aidx");

        var none = await _host.AsCallerAsync(null, s => s.GetRequiredService<ISvcAidxIntake>().ApplyAsync("FRS", "api-aidx", message, Ct));
        none.Should().OnlyContain(r => r.HasErrors, "a site without airports has no side of any leg");
    }

    /// <summary>An SSIM file (time mode U) with one daily leg record per (number, from, to).</summary>
    private string Ssim(params (string Number, string From, string To)[] legs)
    {
        string Date(DateTime d) => d.ToString("ddMMMyy", System.Globalization.CultureInfo.InvariantCulture).ToUpperInvariant();
        var lines = new List<string> { "1AIRLINE STANDARD SCHEDULE DATA SET".PadRight(200), "2UQR ".PadRight(200) };
        foreach (var (number, from, to) in legs)
        {
            var line = new System.Text.StringBuilder(new string(' ', 200));
            void Put(int start, string value) => line.Remove(start - 1, value.Length).Insert(start - 1, value);
            Put(1, "3"); Put(3, "QR "); Put(6, number.PadLeft(4, '0')); Put(10, "0101J");
            Put(15, Date(Now.AddDays(-5))); Put(22, Date(Now.AddDays(30))); Put(29, "1234567");
            Put(37, from); Put(40, "0800"); Put(44, "0800"); Put(48, "+0000");
            Put(55, to); Put(58, "1200"); Put(62, "1200"); Put(66, "+0000"); Put(71, "T2"); Put(73, "359");
            lines.Add(line.ToString());
        }

        return string.Join("\n", lines) + "\n";
    }

    private Task<Fluentx.Result<Ariva.Core.Domain.ViewModels.SsimPreviewViewModel>> PreviewAsync(Guid? caller, string text, int horizon, string site = "DMO") =>
        _host.AsCallerAsync(caller, s => s.GetRequiredService<ISvcFlightSchedules>().PreviewAsync(site, new MemoryStream(System.Text.Encoding.ASCII.GetBytes(text)), horizon, Ct));

    private Task<Fluentx.Result<Ariva.Core.Domain.ViewModels.SsimImportViewModel>> ImportAsync(Guid? caller, string text, int horizon, string token, CancellationToken? ct = null,
        string site = "DMO") =>
        _host.AsCallerAsync(caller, s => s.GetRequiredService<ISvcFlightSchedules>().ImportAsync(site, new MemoryStream(System.Text.Encoding.ASCII.GetBytes(text)), horizon, token,
            ct ?? Ct));

    // The audit entry of an import of the file with this outcome.
    private Task<string> ImportAuditAsync(string sha256, string outcome) =>
        _host.ReadAsync<string>("SELECT after_summary FROM audit_entry WHERE action = 'FlightSchedule.Imported' AND target_name = 'DMO' AND after_summary LIKE @secret " +
            "ORDER BY occurred_on DESC LIMIT 1", secret: $"%\"sha256\":\"{sha256}\"%\"outcome\":\"{outcome}\"%");

    [Fact]
    public async Task Ssim_Should_ImportOnlyWhatWasPreviewedAndNeverChangeALiveLeg_When_Uploaded()
    {
        var admin = await AdminAsync();
        var text = Ssim(("0901", "DOH", "DMO"), ("902", "DMO", "DOH"), ("903", "DOH", "KWI"));
        var day = Now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);

        var preview = await PreviewAsync(admin, text, 7);
        preview.HasErrors.Should().BeFalse(string.Join(", ", preview.ErrorMessages ?? []));
        preview.Data.Should().Match<Ariva.Core.Domain.ViewModels.SsimPreviewViewModel>(p => p.LegRecords == 3 && p.LegRecordsOfSite == 2 && p.Arrivals == 9 && p.Departures == 9);
        (await _host.ReadAsync<long>("SELECT count(*) FROM flight_leg WHERE flight_key LIKE 'QR90%'")).Should().Be(0, "a preview changes nothing");

        // A live feed reported the arrival long ago (40 days before the import): its schedule and stand are still never changed by a schedule file.
        (await LegsAsync("DMO", "aidx", Now.AddMinutes(-1),
            new FlightLegData($"QR901-{day}-A", "QR", "901", null, "Arrival", Now.Date.AddHours(12).AddMinutes(25), Origin: "DOH", Destination: "DMO", Stand: "B12")))[0]
            .Applied.Should().BeTrue();
        await _host.ReadAsync<int>("UPDATE flight_leg SET schedule_source_utc = schedule_source_utc - interval '40 days' WHERE flight_key = @secret RETURNING 1",
            secret: $"QR901-{day}-A");

        // The preview binds the file, the horizon and the user.
        var other = await _host.CreateUserAsync("it.flight.admin2." + Guid.NewGuid().ToString("N")[..6], roles: [RoleCodes.SystemAdministrator]);
        await _host.ReadAsync<int>("UPDATE \"user\" SET all_sites = true WHERE id = @id RETURNING 1", other);
        (await ImportAsync(admin, text, 7, "not-a-token")).ErrorMessages.Should().Equal([FlightScheduleErrors.NotThePreview]);
        (await ImportAsync(admin, text.Replace("T2", "T3", StringComparison.Ordinal), 7, preview.Data.PreviewToken)).ErrorMessages.Should()
            .Equal([FlightScheduleErrors.NotThePreview], "another file");
        (await ImportAsync(admin, text, 200, preview.Data.PreviewToken)).ErrorMessages.Should().Equal([FlightScheduleErrors.NotThePreview], "another horizon");
        (await ImportAsync(other, text, 7, preview.Data.PreviewToken)).ErrorMessages.Should().Equal([FlightScheduleErrors.NotThePreview], "another user");
        (await _host.ReadAsync<long>("SELECT count(*) FROM flight_leg WHERE flight_key LIKE 'QR90%' AND feed = 'ssim'")).Should().Be(0);

        var imported = await ImportAsync(admin, text, 7, preview.Data.PreviewToken);
        imported.HasErrors.Should().BeFalse(string.Join(", ", imported.ErrorMessages ?? []));
        imported.Data.Should().Match<Ariva.Core.Domain.ViewModels.SsimImportViewModel>(i => i.Legs == 18 && i.Refused == 0 && i.Applied == 17 && i.Unchanged == 1);

        (await _host.ReadAsync<long>("SELECT count(*) FROM flight_leg WHERE flight_key LIKE 'QR90%' AND schedule_feed = 'ssim' AND schedule_fallback")).Should().Be(17);
        (await _host.ReadAsync<string>("SELECT to_char(scheduled_utc, 'HH24:MI') || ' ' || coalesce(stand, '-') || ' ' || schedule_feed FROM flight_leg WHERE flight_key = @secret",
            secret: $"QR901-{day}-A")).Should().Be("12:25 B12 aidx", "the live feed's schedule and stand stay, however old its message");
        (await ImportAuditAsync(preview.Data.Sha256, "completed")).Should().Contain("\"unitsCommitted\":1");
        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE action = 'FlightSchedule.Imported' AND actor_id = @id", admin)).Should()
            .BeGreaterThan(0, "the importing administrator is the actor");

        // A live feed then takes over a leg the schedule created, even with an older message; a later import leaves it alone.
        var taken = $"QR902-{day}-D";
        (await LegsAsync("DMO", "aidx", Now.AddDays(-2),
            new FlightLegData(taken, "QR", "902", null, "Departure", Now.Date.AddHours(8).AddMinutes(10), Origin: "DMO", Destination: "DOH", Gate: "A3")))[0]
            .Applied.Should().BeTrue();
        var again = await ImportAsync(admin, text, 7, (await PreviewAsync(admin, text, 7)).Data.PreviewToken);
        again.Data.Applied.Should().Be(0, "nothing in the file changed");
        (await _host.ReadAsync<string>("SELECT to_char(scheduled_utc, 'HH24:MI') || ' ' || coalesce(gate, '-') || ' ' || schedule_feed FROM flight_leg WHERE flight_key = @secret",
            secret: taken)).Should().Be("08:10 A3 aidx");

        (await PreviewAsync(admin, text, 7, "FRS")).ErrorMessages.Should().ContainSingle().Which.Should().Contain("no airport");
    }

    [Fact]
    public async Task Ssim_Should_RefuseAPreviewToken_When_UsedAtAnotherSiteOrAfterTwoHours()
    {
        var admin = await AdminAsync();
        var text = Ssim(("0951", "DOH", "DMO"));

        // A second site with a terminal at the demo airport reads the same file the same way; only the token's site differs.
        var airport = await _host.ReadAsync<Guid>("SELECT a.id FROM airport a JOIN terminal t ON t.airport_id = a.id WHERE t.site_code = 'DMO' LIMIT 1");
        (await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSites>().CreateAsync(new CreateSiteRequest("SSB", "Second schedule site"), Ct))).HasErrors.Should().BeFalse();
        (await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcTopology>().CreateTerminalAsync(new CreateTerminalRequest(airport, "TS", "Schedule terminal", "SSB"), Ct)))
            .HasErrors.Should().BeFalse();
        var atDmo = await PreviewAsync(admin, text, 7);
        (await PreviewAsync(admin, text, 7, "SSB")).Data.Sha256.Should().Be(atDmo.Data.Sha256, "the same file reads the same at the second site");
        (await ImportAsync(admin, text, 7, atDmo.Data.PreviewToken, site: "SSB")).ErrorMessages.Should().Equal([FlightScheduleErrors.NotThePreview], "another site");

        _host.Clock.Advance(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(1));
        (await ImportAsync(admin, text, 7, atDmo.Data.PreviewToken)).ErrorMessages.Should().Equal([FlightScheduleErrors.NotThePreview], "older than two hours");
        (await _host.ReadAsync<long>("SELECT count(*) FROM flight_leg WHERE flight_key LIKE 'QR951-%'")).Should().Be(0);
        (await ImportAsync(admin, text, 7, (await PreviewAsync(admin, text, 7)).Data.PreviewToken)).HasErrors.Should().BeFalse("a fresh preview imports");
    }

    [Fact]
    public async Task Ssim_Should_RollBackAFailedUnitAndAuditWhatWasCommitted_When_TheImportFailsPartWay()
    {
        var admin = await AdminAsync();
        var many = Ssim([.. Enumerable.Range(1000, 40).Select(n => (n.ToString(System.Globalization.CultureInfo.InvariantCulture), "DOH", "DMO"))]);
        var preview = await PreviewAsync(admin, many, 14);
        preview.Data.Legs.Should().BeInRange(FlightRules.MaxBatch + 1, 2 * FlightRules.MaxBatch, "two units");

        // The second unit fails after applying its legs: it is rolled back whole (its scope's disposal would otherwise commit
        // it), the first stays, and the audit says the import failed after one unit.
        FailingIntake.Arm(2);
        try
        {
            var failing = () => ImportAsync(admin, many, 14, preview.Data.PreviewToken);
            await failing.Should().ThrowAsync<InvalidOperationException>();
        }
        finally
        {
            FailingIntake.Arm(0);
        }

        (await _host.ReadAsync<long>("SELECT count(*) FROM flight_leg WHERE flight_key LIKE 'QR10%'")).Should().Be(FlightRules.MaxBatch);
        (await ImportAuditAsync(preview.Data.Sha256, "failed")).Should().Contain("\"unitsCommitted\":1").And.Contain($"\"applied\":{FlightRules.MaxBatch}");

        // The first unit fails: nothing is committed, and the failure is still audited.
        var other = Ssim([.. Enumerable.Range(1200, 2).Select(n => (n.ToString(System.Globalization.CultureInfo.InvariantCulture), "DOH", "DMO"))]);
        var small = await PreviewAsync(admin, other, 7);
        FailingIntake.Arm(1);
        try
        {
            var failing = () => ImportAsync(admin, other, 7, small.Data.PreviewToken);
            await failing.Should().ThrowAsync<InvalidOperationException>();
        }
        finally
        {
            FailingIntake.Arm(0);
        }

        (await _host.ReadAsync<long>("SELECT count(*) FROM flight_leg WHERE flight_key LIKE 'QR120%'")).Should().Be(0);
        (await ImportAuditAsync(small.Data.Sha256, "failed")).Should().Contain("\"unitsCommitted\":0").And.Contain("\"applied\":0");
    }

    [Fact]
    public async Task Ssim_Should_RunToItsEnd_When_TheCallerGoesAwayAfterTheFirstUnit()
    {
        var admin = await AdminAsync();
        var many = Ssim([.. Enumerable.Range(1100, 40).Select(n => (n.ToString(System.Globalization.CultureInfo.InvariantCulture), "DOH", "DMO"))]);
        var preview = await PreviewAsync(admin, many, 14);

        using var leaving = new CancellationTokenSource();
        var import = ImportAsync(admin, many, 14, preview.Data.PreviewToken, leaving.Token);
        var waited = Stopwatch.StartNew();
        while (!import.IsCompleted && waited.Elapsed < TimeSpan.FromSeconds(60)
               && await _host.ReadAsync<long>("SELECT count(*) FROM flight_leg WHERE flight_key LIKE 'QR11%'") == 0)
            await Task.Delay(20, Ct);
        await leaving.CancelAsync();

        var done = await import;
        done.Data.Applied.Should().Be(preview.Data.Legs);
        (await _host.ReadAsync<long>("SELECT count(*) FROM flight_leg WHERE flight_key LIKE 'QR11%'")).Should().Be(preview.Data.Legs);
        (await ImportAuditAsync(preview.Data.Sha256, "completed")).Should().Contain($"\"applied\":{preview.Data.Legs}");
    }

    [Fact]
    public async Task Call_Should_BeRefusedAsAWhole_When_TheSiteFeedOrMessageTimeCannotBeRight()
    {
        await AdminAsync();
        var unknownSite = () => LegsAsync("XXX", "aidx", null, Arrival("DM1-SITE-A"));
        await unknownSite.Should().ThrowAsync<ArgumentException>().WithMessage("Unknown site*");
        var badFeed = () => LegsAsync("DMO", "AIDX feed", null, Arrival("DM1-SITE-A"));
        await badFeed.Should().ThrowAsync<ArgumentException>();
        var future = () => LegsAsync("DMO", "aidx", Now.AddHours(1), Arrival("DM1-SITE-A"));
        await future.Should().ThrowAsync<ArgumentException>();
        var tooMany = () => LegsAsync("DMO", "aidx", null, [.. Enumerable.Range(0, 501).Select(i => Arrival($"DM1-MANY-{i}"))]);
        await tooMany.Should().ThrowAsync<ArgumentException>();
        (await _host.ReadAsync<long>("SELECT count(*) FROM flight_leg WHERE flight_key = 'DM1-SITE-A' OR flight_key LIKE 'DM1-MANY-%'")).Should().Be(0);
    }

    [Fact]
    public async Task Events_Should_ApplyToKnownLegsOfTheirDirectionAndAllBeRecorded_When_Sent()
    {
        await AdminAsync();
        var key = "DM214-EVT-A";
        await LegsAsync("DMO", "aidx", Now.AddMinutes(-30), Arrival(key, 30));
        var scheduled = Now.AddMinutes(30);

        var results = await EventsAsync("DMO", Now.AddMinutes(-2),
            new FlightEventData(key, "OnBlock", scheduled.AddMinutes(9)),
            new FlightEventData(key, "Landed", scheduled.AddMinutes(2)),
            new FlightEventData(key, "BoardingStart", scheduled),
            new FlightEventData("NOPE-EVT-A", "Landed", scheduled),
            new FlightEventData(key, "landed", scheduled),
            new FlightEventData(key, "Landed", scheduled.AddDays(5)));

        results.Select(r => r.Applied).Should().Equal(true, true, false, false, false, false);
        results.Skip(2).Should().OnlyContain(r => r.HasErrors);
        (await EventsAsync("DMO", Now.AddMinutes(-20), new FlightEventData(key, "OnBlock", scheduled.AddMinutes(5))))[0].Applied.Should().BeFalse("older than the on-block known");
        (await _host.ReadAsync<string>("SELECT status FROM flight_leg WHERE flight_key = @secret", secret: key)).Should().Be("OnBlock");
        (await _host.ReadAsync<long>("SELECT count(*) FROM flight_event WHERE flight_key = @secret", secret: key)).Should().Be(3, "the record keeps the older one too");
        (await _host.ReadAsync<long>("SELECT count(*) FROM flight_event WHERE flight_key = @secret AND NOT applied", secret: key)).Should().Be(1);
    }

    [Fact]
    public async Task Allocations_Should_ResolveMappedCountersAndKeepTheRestApart_When_SentForDepartures()
    {
        await AdminAsync();
        var key = "XR331-ALC-D";
        await LegsAsync("DMO", "api", Now.AddMinutes(-30), Departure(key), Arrival("DM214-ALC-A"));
        var scheduled = Now.AddMinutes(180);
        CounterAllocationData At(string checkpoint, string flight = "XR331-ALC-D") =>
            new(flight, checkpoint, ["cnt-1", "CNT-2", "CNT-9"], scheduled.AddHours(-3), scheduled.AddMinutes(-45), "HB");

        var results = await AllocationsAsync("DMO", Now.AddMinutes(-5), At("CI"), At("SEC-N"), At("NOPE"), At("CI", "DM214-ALC-A"), At("CI", "NOPE-ALC-D"));

        results.Select(r => r.Applied).Should().Equal(true, false, false, false, false);
        results[0].Warnings.Should().ContainSingle().Which.Should().Contain("1 counter codes have no AODB desk code mapping");
        results.Skip(1).Should().OnlyContain(r => r.HasErrors);
        (await _host.ReadAsync<string>("SELECT desk_codes || '|' || unresolved_codes FROM counter_allocation WHERE flight_key = @secret", secret: key)).Should().Be("A01 A02|CNT-9");

        (await AllocationsAsync("DMO", Now.AddMinutes(-10), At("CI") with { CounterCodes = ["CNT-1"] }))[0].Applied.Should().BeFalse("older");
        (await AllocationsAsync("DMO", Now.AddMinutes(-1), At("CI") with { CounterCodes = ["CNT-2"] }))[0].Should().Match<FlightItemResult>(r => r.Applied && r.Warnings.Count == 0);
        (await _host.ReadAsync<string>("SELECT desk_codes || '|' || COALESCE(unresolved_codes, '-') FROM counter_allocation WHERE flight_key = @secret", secret: key))
            .Should().Be("A02|-");
    }

    [Fact]
    public async Task Leg_Should_BeCreatedOnce_When_TwoFeedsSendItAtTheSameTime()
    {
        await AdminAsync();
        var key = "DM777-RACE-A";
        var calls = Enumerable.Range(0, 6).Select(i => LegsAsync("DMO", i % 2 == 0 ? "aidx" : "acris", Now.AddMinutes(-i), Arrival(key) with { Stand = $"S{i}" }));

        var results = await Task.WhenAll(calls);

        results.Should().OnlyContain(r => !r[0].HasErrors);
        (await _host.ReadAsync<long>("SELECT count(*) FROM flight_leg WHERE flight_key = @secret", secret: key)).Should().Be(1);
        (await _host.ReadAsync<string>("SELECT stand FROM flight_leg WHERE flight_key = @secret", secret: key)).Should().Be("S0", "the newest message wins, whatever order they ran in");
    }

    [Fact]
    public async Task Feed_Should_GoStaleOnlyWhileFlightsAreDueAndRecover_When_ItFallsSilentAndSpeaksAgain()
    {
        await AdminAsync();
        await LegsAsync("FRS", "aidx", null, Arrival("FR1-FRESH-A", 30));
        await LegsAsync("IDL", "aidx", null, Arrival("ID1-IDLE-A", 600));
        await LegsAsync("FRS", "ssim", null, Arrival("FR2-SSIM-A", 50));
        string State(string site, string feed) => $"{site}/{feed}";
        async Task<string> StateOf(string site, string feed) =>
            await _host.ReadAsync<string>("SELECT state || ' ' || flights_due FROM feed_freshness WHERE site_code || '/' || feed = @secret", secret: State(site, feed));

        (await SweepAsync()).Ran.Should().BeTrue();
        (await StateOf("FRS", "aidx")).Should().Be("Fresh 2");

        _host.Clock.Advance(TimeSpan.FromMinutes(25));
        var sweep = await SweepAsync();
        sweep.Stale.Should().BeGreaterThanOrEqualTo(1);
        (await StateOf("FRS", "aidx")).Should().Be("Stale 2", "silent for 25 min while two flights are due");
        (await StateOf("IDL", "aidx")).Should().Be("Idle 0", "silent, but nothing due before ten hours");
        (await StateOf("FRS", "ssim")).Should().Be("Idle 2", "a schedule import is not a live feed");

        await EventsAsync("FRS", null, new FlightEventData("FR1-FRESH-A", "Estimated", Now.AddMinutes(10)));
        await SweepAsync();
        (await StateOf("FRS", "aidx")).Should().Be("Fresh 2");

        // One sweep at a time: another replica holding the sweep lock makes this one skip.
        await using var other = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await other.OpenAsync(Ct);
        await using var transaction = await other.BeginTransactionAsync(Ct);
        await using (var leg = new NpgsqlCommand("SELECT pg_advisory_xact_lock(41, 0)", other, transaction))
            await leg.ExecuteNonQueryAsync(Ct);
        (await SweepAsync()).Ran.Should().BeTrue("a leg's lock, whatever its key, is not the sweep's");
        await using (var take = new NpgsqlCommand("SELECT pg_advisory_xact_lock(42, 0)", other, transaction))
            await take.ExecuteNonQueryAsync(Ct);
        (await SweepAsync()).Ran.Should().BeFalse();
        await transaction.RollbackAsync(Ct);
    }

    [Fact]
    public async Task Batch_Should_ApplyItsGoodItems_When_AnItemCarriesTimesAtTheEdgesOfTheCalendar()
    {
        await AdminAsync();
        var results = await LegsAsync("DMO", "aidx", null,
            Arrival("DM1-EDGE-A"),
            Arrival("DM2-EDGE-A") with { ScheduledUtc = DateTime.MinValue.ToUniversalTime(), EstimatedUtc = Now },
            Arrival("DM3-EDGE-A") with { ScheduledUtc = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc), EstimatedUtc = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc) },
            Arrival("DM4-EDGE-A") with { EstimatedUtc = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc), ActualUtc = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc) },
            Arrival("DM5-EDGE-A"));

        results.Select(r => r.Applied).Should().Equal(true, false, false, false, true);
        results.Skip(1).Take(3).Should().OnlyContain(r => r.HasErrors);
        (await EventsAsync("DMO", null, new FlightEventData("DM1-EDGE-A", "Landed", DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc))))[0].HasErrors.Should().BeTrue();
    }

    [Fact]
    public async Task Sites_Should_KeepTheirOwnLegsAndCheckpoints_When_TwoSitesUseTheSameFlightKey()
    {
        await AdminAsync();
        var key = "XR500-SITES-D";
        (await LegsAsync("DMO", "aidx", null, Departure(key)))[0].Applied.Should().BeTrue();
        (await LegsAsync("FRS", "aidx", null, Departure(key) with { Gate = "F9" }))[0].Applied.Should().BeTrue("the same key at another site is another leg");
        (await _host.ReadAsync<long>("SELECT count(*) FROM flight_leg WHERE flight_key = @secret", secret: key)).Should().Be(2);
        (await _host.ReadAsync<string>("SELECT COALESCE(gate, '-') FROM flight_leg WHERE site_code = 'DMO' AND flight_key = @secret", secret: key)).Should().Be("-");

        // A key only DMO knows is unknown at FRS; DMO's check-in checkpoint does not exist at FRS.
        (await LegsAsync("DMO", "aidx", null, Departure("XR600-SITES-D")))[0].Applied.Should().BeTrue();
        (await EventsAsync("FRS", null, new FlightEventData("XR600-SITES-D", "GateOpen", Now.AddMinutes(150))))[0].Errors.Should().ContainSingle().Which.Should().Contain("Unknown flight");
        var allocation = new CounterAllocationData(key, "CI", ["CNT-1"], Now.AddMinutes(60), Now.AddMinutes(150));
        (await AllocationsAsync("FRS", null, allocation))[0].Errors.Should().ContainSingle().Which.Should().Contain("not a check-in checkpoint of the site");
        (await AllocationsAsync("DMO", null, allocation))[0].Applied.Should().BeTrue();
        (await _host.ReadAsync<long>("SELECT count(*) FROM counter_allocation a JOIN flight_leg l ON l.id = a.flight_leg_id WHERE l.site_code = 'FRS'")).Should().Be(0);
    }

    [Fact]
    public async Task Feeds_Should_NotDeadlock_When_TheySendTheSameLegsInOppositeOrders()
    {
        await AdminAsync();
        var keys = Enumerable.Range(0, 40).Select(i => $"DM{i}-ORDER-A").ToList();
        for (var round = 0; round < 3; round++)
        {
            var forward = LegsAsync("DMO", "aidx", Now.AddMinutes(-round), [.. keys.Select(k => Arrival(k) with { Stand = $"F{round}" })]);
            var backward = LegsAsync("DMO", "acris", Now.AddMinutes(-round), [.. Enumerable.Reverse(keys).Select(k => Arrival(k) with { Stand = $"B{round}" })]);
            var both = await Task.WhenAll(forward, backward);
            both.Should().OnlyContain(r => r.All(i => !i.HasErrors), "neither call is aborted as a deadlock victim");
        }

        (await _host.ReadAsync<long>("SELECT count(*) FROM flight_leg WHERE flight_key LIKE '%-ORDER-A'")).Should().Be(40);
    }

    [Fact]
    public async Task Feed_Should_NotKeepItselfFresh_When_ItSendsOnlyEmptyOrRefusedBatches()
    {
        await AdminAsync();
        await LegsAsync("IDL", "junk", null);
        await LegsAsync("IDL", "junk", null, Arrival("DM1-JUNK-A", carrier: "D'X"));
        await _host.AsCallerAsync(null, s => s.GetRequiredService<ISvcFlightIntake>().ApplyEventsAsync("IDL", "junk", [new FlightEventData("NOPE-JUNK-A", "Landed", Now)], null, Ct));
        await _host.AsCallerAsync(null, s => s.GetRequiredService<ISvcFlightIntake>().ApplyAllocationsAsync("IDL", "junk",
            [new CounterAllocationData("NOPE-JUNK-D", "CI", ["CNT-1"], Now, Now.AddHours(1))], null, Ct));
        (await _host.ReadAsync<long>("SELECT count(*) FROM feed_freshness WHERE site_code = 'IDL' AND feed = 'junk'")).Should().Be(0);

        await LegsAsync("IDL", "junk", null, Arrival("DM1-JUNK-A"));
        (await _host.ReadAsync<long>("SELECT count(*) FROM feed_freshness WHERE site_code = 'IDL' AND feed = 'junk'")).Should().Be(1);
    }

    [Theory]
    [InlineData("DELETE FROM flight_event", "42501")]
    [InlineData("UPDATE flight_event SET applied = NOT applied", "42501")]
    [InlineData("DELETE FROM counter_allocation", "42501")]
    [InlineData("DELETE FROM feed_freshness", "42501")]
    [InlineData("DELETE FROM flight_leg", "42501")]
    [InlineData("UPDATE flight_leg SET carrier = 'D''X'", "23514")]
    [InlineData("UPDATE flight_leg SET flight_key = E'K\\r\\nX'", "23514")]
    [InlineData("UPDATE flight_leg SET gate_open_utc = now(), gate_open_source_utc = now() WHERE direction = 'Arrival'", "23514")]
    [InlineData("UPDATE flight_leg SET estimated_utc = now(), estimated_source_utc = NULL", "23514")]
    [InlineData("UPDATE flight_leg SET status = 'Delayed'", "23514")]
    [InlineData("UPDATE counter_allocation SET close_utc = open_utc", "23514")]
    public async Task Database_Should_RepeatTheRules_When_TheRuntimeRoleWritesDirectly(string sql, string state)
    {
        await AdminAsync();
        if (await _host.ReadAsync<long>("SELECT count(*) FROM counter_allocation") == 0 || await _host.ReadAsync<long>("SELECT count(*) FROM flight_event") == 0)
        {
            await LegsAsync("DMO", "api", null, Departure("XR9-DB-D"), Arrival("DM9-DB-A", 60));
            await EventsAsync("DMO", null, new FlightEventData("DM9-DB-A", "Landed", Now.AddMinutes(55)));
            await AllocationsAsync("DMO", null, new CounterAllocationData("XR9-DB-D", "CI", ["CNT-1"], Now.AddMinutes(60), Now.AddMinutes(150)));
        }

        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE ariva_runtime", connection, transaction))
            await role.ExecuteNonQueryAsync(Ct);
#pragma warning disable CA2100 // literal statements from the inline data above
        await using var command = new NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100

        var change = () => command.ExecuteNonQueryAsync(Ct);

        (await change.Should().ThrowAsync<PostgresException>(sql)).Which.SqlState.Should().Be(state);
        await transaction.RollbackAsync(Ct);
    }
}
