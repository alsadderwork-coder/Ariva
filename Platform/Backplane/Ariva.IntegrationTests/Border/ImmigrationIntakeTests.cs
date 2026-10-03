using Ariva.Business.Contracts.Aman.V1;
using Ariva.Core;
using Ariva.Core.Border;
using Ariva.Core.Services;
using Ariva.Core.Flights;
using Ariva.Core.Services.Border;
using Ariva.Core.Services.Flights;
using Ariva.Di.Extensions;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Seed;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using Ariva.Simulation.Api.Emulators.Aman;
using Ariva.Simulation.Api.Scenarios.Engine;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ariva.IntegrationTests.Border;

/// <summary>
/// ARV-048 on PostgreSQL: a minute of the AMAN emulator's feed is stored whole, its AMAN codes resolved to the demo
/// airport's desks and e-gates through the seeded AMAN desk code mappings; the same records again (another transport, a
/// redelivery) change nothing; an unmapped code is kept apart with a warning; a record of another site or an unknown site
/// is refused; a lane demand is replaced only by a later computation; the database repeats the rules (sums, small cells)
/// and the runtime role cannot change or delete a record.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ImmigrationIntakeTests(PostgresFixture fixture) : IAsyncDisposable
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _seeded;

    private readonly AccountsHost _host = new(fixture, database: TestDatabase.Border,
        configure: services =>
        {
            services.AddArivaBorderFeed(new ConfigurationBuilder().Build());
            services.AddArivaFlights(new ConfigurationBuilder().Build(), watchFeeds: false);
            services.AddArivaArrivalWave(new ConfigurationBuilder().Build());
        });

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DateTime Now => _host.Clock.GetUtcNow().UtcDateTime;

    private async Task SeedAsync()
    {
        await Gate.WaitAsync(Ct);
        try
        {
            await _host.CreateUserAsync("it.border.probe." + Guid.NewGuid().ToString("N")[..8]);
            if (_seeded)
                return;
            await _host.AsCallerAsync(null, s =>
                new DemoTopologySeed(s.GetRequiredService<IUnitOfWork>(), s.GetRequiredService<ICurrentUser>(), _host.Clock, s.GetRequiredService<AuditTrail>()).RunAsync(Ct));
            _seeded = true;
        }
        finally
        {
            Gate.Release();
        }
    }

    private AmanMinute Minute(int minute, bool first = true)
    {
        var day = ScenarioDay.Run(ScenarioConfig.Reference(9303));
        var start = new DateTime(Now.Ticks - Now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc).AddMinutes(-minute);
        return AmanFeed.Build(day, minute, m => start.AddMinutes(m), "DMO", BorderSides.Both, first);
    }

    private Task<IReadOnlyList<Ariva.Core.Border.ImmigrationItemResult>> Apply<T>(Func<ISvcImmigrationIntake, Task<IReadOnlyList<Ariva.Core.Border.ImmigrationItemResult>>> call) =>
        _host.AsCallerAsync(null, s => call(s.GetRequiredService<ISvcImmigrationIntake>()));

    [Fact]
    public async Task Intake_Should_StoreAMinuteOnceWithItsDesksResolved_When_ItArrivesTwice()
    {
        await SeedAsync();
        var minute = Minute(1110);

        foreach (var feed in new[] { "aman-kafka", "api-aman-rest" })
        {
            var sessions = await Apply<DeskSessionChanged>(i => i.ApplyDeskSessionsAsync(feed == "aman-kafka" ? ImmigrationScope.RecordSite : ImmigrationScope.ForSite("DMO"), feed, minute.Sessions, Ct));
            var desks = await Apply<DeskIntervalStats>(i => i.ApplyDeskIntervalsAsync(ImmigrationScope.RecordSite, feed, minute.Desks, Ct));
            var gates = await Apply<EGateIntervalStats>(i => i.ApplyEgateIntervalsAsync(ImmigrationScope.RecordSite, feed, minute.Gates, Ct));
            var demand = await Apply<InboundFlightLaneDemand>(i => i.ApplyLaneDemandAsync(ImmigrationScope.ForSite("DMO"), feed, minute.Demand, Ct));
            var all = sessions.Concat(desks).Concat(gates).Concat(demand).ToList();
            all.Should().HaveCount(minute.Count).And.OnlyContain(r => !r.HasErrors);
            if (feed == "aman-kafka")
                all.Should().OnlyContain(r => r.Applied && r.Warnings.Count == 0, "every AMAN code of the demo airport is mapped");
            else
                all.Should().OnlyContain(r => !r.Applied && r.Warnings.Count == 1, "the same records over the other transport change nothing");
        }

        (await _host.ReadAsync<long>("SELECT count(*) FROM border_desk_interval WHERE desk_id IS NULL AND feed = 'aman-kafka' AND source_event_id LIKE 'aman-%'")).Should().Be(0);
        (await _host.ReadAsync<string>("SELECT d.code FROM border_desk_interval b JOIN desk d ON d.id = b.desk_id WHERE b.desk_code = 'IN09' LIMIT 1")).Should().Be("AR-09");
        (await _host.ReadAsync<string>("SELECT d.code || ' ' || d.kind FROM border_egate_interval b JOIN desk d ON d.id = b.desk_id WHERE b.gate_code = 'EGOUT2' LIMIT 1"))
            .Should().Be("DG-2 EGate");
        (await _host.ReadAsync<long>("SELECT count(*) FROM border_desk_session WHERE feed = 'aman-kafka'")).Should().Be(minute.Sessions.Count);
    }

    [Fact]
    public async Task Intake_Should_KeepUnmappedCodesApartAndRefuseOtherSites_When_Received()
    {
        await SeedAsync();
        var at = new DateTimeOffset(new DateTime(Now.Ticks - Now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc));
        DeskIntervalStats Desk(string site, string code, string id) => new(site, code, at, 60, 1, 2, 30, 45, 60, "CIT", id);

        var results = await Apply<DeskIntervalStats>(i => i.ApplyDeskIntervalsAsync(ImmigrationScope.ForSite("DMO"), "api-imm-test", [
            Desk("DMO", "NOSUCH1", "imm-unmapped-1"), Desk("DMO", "EGIN1", "imm-wrongkind-1"), Desk("BEY", "IN01", "imm-othersite-1")], Ct));
        var kafka = await Apply<DeskIntervalStats>(i => i.ApplyDeskIntervalsAsync(ImmigrationScope.RecordSite, "aman-kafka", [Desk("ZZ9", "IN01", "imm-unknownsite-1")], Ct));

        results[0].Should().Match<Ariva.Core.Border.ImmigrationItemResult>(r => r.Applied && r.Warnings.Single().Contains("no AMAN desk code mapping"));
        results[1].Should().Match<Ariva.Core.Border.ImmigrationItemResult>(r => r.Applied && r.Warnings.Single().Contains("maps to a EGate"));
        results[2].Errors.Should().Equal("siteCode is the site of the call.");
        kafka[0].Errors.Should().Equal("siteCode is not a site Ariva knows.");
        (await _host.ReadAsync<long>("SELECT count(*) FROM border_desk_interval WHERE source_event_id IN ('imm-unmapped-1', 'imm-wrongkind-1') AND desk_id IS NULL"))
            .Should().Be(2);
    }

    [Fact]
    public async Task Intake_Should_ResolveOnlyMappingsWhoseDeskIsAtTheSameSite_When_AMappingCrossesSites()
    {
        await SeedAsync();
        // A second site whose AMAN mapping (written by the migration role, past the entity's own check) points at a desk
        // of DMO: it resolves neither for DMO (another site's mapping) nor for XS1 (the desk is not at XS1). CWE-863.
        await ExecuteAsMigrationAsync("""
            INSERT INTO site (id, code, name) SELECT gen_random_uuid(), 'XS1', 'Cross-site probe' WHERE NOT EXISTS (SELECT 1 FROM site WHERE code = 'XS1');
            INSERT INTO desk (id, checkpoint_id, site_code, code, name, kind)
                 SELECT gen_random_uuid(), d.checkpoint_id, 'DMO', 'XT-1', 'Cross-site probe desk', 'Desk' FROM desk d
                  WHERE d.site_code = 'DMO' AND d.code = 'AR-09' AND d.deleted_on IS NULL
                    AND NOT EXISTS (SELECT 1 FROM desk WHERE site_code = 'DMO' AND code = 'XT-1');
            INSERT INTO desk_code_mapping (id, system, external_code, desk_id, site_code)
                 SELECT gen_random_uuid(), 'Aman', 'XSITE1', d.id, 'XS1' FROM desk d WHERE d.site_code = 'DMO' AND d.code = 'XT-1'
                    AND NOT EXISTS (SELECT 1 FROM desk_code_mapping WHERE system = 'Aman' AND site_code = 'XS1' AND external_code = 'XSITE1');
            """);
        var at = new DateTimeOffset(new DateTime(Now.Ticks - Now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc));
        DeskIntervalStats Desk(string site, string id) => new(site, "XSITE1", at, 60, 1, 2, 30, 45, 60, "CIT", id);

        var dmo = await Apply<DeskIntervalStats>(i => i.ApplyDeskIntervalsAsync(ImmigrationScope.ForSite("DMO"), "api-imm-test", [Desk("DMO", "imm-cross-dmo")], Ct));
        var xs1 = await Apply<DeskIntervalStats>(i => i.ApplyDeskIntervalsAsync(ImmigrationScope.RecordSite, "aman-kafka", [Desk("XS1", "imm-cross-xs1")], Ct));

        foreach (var result in dmo.Concat(xs1))
            result.Should().Match<Ariva.Core.Border.ImmigrationItemResult>(r => r.Applied && r.Warnings.Single().Contains("no AMAN desk code mapping"));
        (await _host.ReadAsync<long>("SELECT count(*) FROM border_desk_interval WHERE source_event_id IN ('imm-cross-dmo', 'imm-cross-xs1') AND desk_id IS NULL"))
            .Should().Be(2);
    }

    [Fact]
    public async Task Intake_Should_NeverEchoACodeOutsideTheMappingShape_When_ARecordIsRefused()
    {
        await SeedAsync();
        var at = new DateTimeOffset(new DateTime(Now.Ticks - Now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc));
        var results = await Apply<DeskIntervalStats>(i => i.ApplyDeskIntervalsAsync(ImmigrationScope.ForSite("DMO"), "api-imm-test",
            [new("DMO", "<script>", at, 60, 1, 2, 30, 45, 60, "CIT", "imm-echo-1"), new("DMO", "IN01", at, 60, 1, 2, 30, 45, 60, "EG", "imm-echo-2")], Ct));

        results[0].Key.Should().BeNull("a code outside the mapping shape is never echoed");
        results[1].Key.Should().Be("IN01");
        System.Text.Json.JsonSerializer.Serialize(results).Should().NotContain("script");
    }

    [Fact]
    public async Task Intake_Should_RefuseTheCall_When_NoScopeWasChosen()
    {
        await SeedAsync();
        var call = () => Apply<DeskIntervalStats>(i => i.ApplyDeskIntervalsAsync(default, "aman-kafka", [], Ct));
        await call.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task LaneDemand_Should_KeepTheLatestComputation_When_AnOlderOneArrivesLate()
    {
        await SeedAsync();
        var at = new DateTimeOffset(Now);
        InboundFlightLaneDemand Demand(int boarded, DateTimeOffset computed, string id) =>
            new("DMO", "QR900-20261003-A", at.AddHours(2), boarded, new Dictionary<string, int> { ["VIS"] = boarded / 2 }, 0, computed, id);

        (await Apply<InboundFlightLaneDemand>(i => i.ApplyLaneDemandAsync(ImmigrationScope.ForSite("DMO"), "aman-kafka", [Demand(180, at.AddMinutes(-30), "ld-new")], Ct)))[0].Applied.Should().BeTrue();
        var older = await Apply<InboundFlightLaneDemand>(i => i.ApplyLaneDemandAsync(ImmigrationScope.ForSite("DMO"), "aman-kafka", [Demand(150, at.AddMinutes(-90), "ld-old")], Ct));
        older[0].Applied.Should().BeFalse();
        older[0].Warnings.Single().Should().Contain("Not newer");
        (await Apply<InboundFlightLaneDemand>(i => i.ApplyLaneDemandAsync(ImmigrationScope.ForSite("DMO"), "aman-kafka", [Demand(190, at.AddMinutes(-5), "ld-latest")], Ct)))[0].Applied.Should().BeTrue();
        (await _host.ReadAsync<string>("SELECT boarded_total || ' ' || vis || ' ' || source_event_id FROM inbound_lane_demand WHERE flight_key = 'QR900-20261003-A'"))
            .Should().Be("190 95 ld-latest");
    }

    [Fact]
    public async Task ArrivalWave_Should_ProjectTheSitesArrivalsWithAmansLaneDemandWherePresent_When_Asked()
    {
        await SeedAsync();
        FlightLegData Leg(string key, DateTime? estimated = null, DateTime? onBlock = null, int? seats = null, int? pax = null, string direction = "Arrival") =>
            new(key, "AW", key[2..5], null, direction, Now.AddMinutes(15), estimated, null, onBlock, null, Origin: direction == "Arrival" ? "BEY" : "DMO",
                Destination: direction == "Arrival" ? "DMO" : "BEY", Stand: "B12", Seats: seats, PaxEstimate: pax);
        var legs = await _host.AsCallerAsync(null, s => s.GetRequiredService<ISvcFlightIntake>().ApplyLegsAsync("DMO", "it-wave", [
            Leg("AW101-WAVE-A", onBlock: Now.AddMinutes(-5), pax: 160),
            Leg("AW102-WAVE-A", estimated: Now.AddMinutes(20), pax: 100),
            Leg("AW103-WAVE-A", estimated: Now.AddMinutes(20), pax: 100),
            Leg("AW104-WAVE-A", estimated: Now.AddMinutes(45), pax: 100),
            Leg("AW105-WAVE-A", estimated: Now.AddMinutes(10), seats: 180),
            Leg("AW106-WAVE-A", estimated: Now.AddMinutes(25)),
            Leg("AW107-WAVE-D", estimated: Now.AddMinutes(10), pax: 100, direction: "Departure")], Now.AddMinutes(-1), Ct));
        legs.Should().OnlyContain(r => !r.HasErrors);
        (await _host.AsCallerAsync(null, s => s.GetRequiredService<ISvcFlightIntake>().ApplyEventsAsync("DMO", "it-wave",
            [new FlightEventData("AW103-WAVE-A", "Cancelled", Now.AddMinutes(-1))], Now.AddMinutes(-1), Ct))).Should().OnlyContain(r => !r.HasErrors);
        var lanes = new Dictionary<string, int> { ["CIT"] = 40, ["RES"] = 20, ["VIS"] = 90, ["CRW"] = 4 };
        (await Apply<InboundFlightLaneDemand>(i => i.ApplyLaneDemandAsync(ImmigrationScope.ForSite("DMO"), "aman-kafka",
            [new("DMO", "AW101-WAVE-A", new DateTimeOffset(Now.AddMinutes(15)), 200, lanes, 30, new DateTimeOffset(Now.AddMinutes(-10)), "aw-ld-1")], Ct)))
            .Should().OnlyContain(r => r.Applied);

        _waveAdmin = await WaveUserAsync(RoleCodes.SystemAdministrator);
        var wave = await Wave("DMO", 30);

        wave.HasErrors.Should().BeFalse();
        var view = wave.Data;
        view.Flights.Select(f => f.FlightKey).Should().Equal("AW101-WAVE-A", "AW105-WAVE-A", "AW102-WAVE-A", "AW106-WAVE-A");
        view.Flights[0].Should().Match<ArrivalWaveFlightViewModel>(f => f.PassengerSource == "Aman" && f.LaneSource == "Aman" && f.InBlockSource == "OnBlock" && f.Landed &&
                                                                         f.Lanes.Vis == 90 && f.Lanes.EGate == 30 && f.Passengers == 200);
        view.Flights[1].Should().Match<ArrivalWaveFlightViewModel>(f => f.PassengerSource == "Seats" && f.Passengers == 144 && f.LaneSource == "DefaultMix");
        view.Flights[2].Should().Match<ArrivalWaveFlightViewModel>(f => f.PassengerSource == "PaxEstimate" && f.Lanes.Vis == 35 && f.InBlockSource == "Estimated" && !f.Landed);
        view.Flights[3].Passengers.Should().BeNull();
        view.FlightsWithoutPassengers.Should().Be(1);
        // Every flight's passengers lie inside the horizon: the curve holds them all (AMAN's 184 of 200 boarded, the mix's 92 percent of the rest).
        view.Minutes.Sum(m => m.Lanes.Total).Should().BeApproximately(184 + 144 * 0.92 + 100 * 0.92, 0.1);
        view.Minutes.Should().HaveCount(30 + 11 + 12);

        (await Wave("DMO", 60)).Data.Flights.Select(f => f.FlightKey).Should().Contain("AW104-WAVE-A");
        (await Wave("ZZ9", 30)).ErrorMessages.Should().Equal(Ariva.Core.Services.Topology.TopologyErrors.NotFound);
        (await Wave("DMO", 4)).ErrorMessages.Should().ContainSingle().Which.Should().Contain("minutes is 5 to 120");

        // A site the caller cannot see is not found, whatever the endpoint checked (CWE-863); no caller sees nothing.
        await ProbeSiteAsync();
        (await Wave("DMO", 30, await WaveUserAsync(RoleCodes.BorderShiftSupervisor, "XS2"))).ErrorMessages.Should().Equal(Ariva.Core.Services.Topology.TopologyErrors.NotFound);
        (await _host.AsCallerAsync(null, s => s.GetRequiredService<ISvcArrivalWave>().GetAsync("DMO", 30, Ct))).HasErrors.Should().BeTrue();

        // An airport role sees flight and minute totals only: the lane split is border data (wiki 01).
        var totals = (await Wave("DMO", 30, await WaveUserAsync(RoleCodes.TerminalDutyManager, "DMO"))).Data;
        var aman = totals.Flights.Single(f => f.FlightKey == "AW101-WAVE-A");
        aman.Should().Match<ArrivalWaveFlightViewModel>(f => f.Lanes.Vis == null && f.Lanes.Cit == null && f.Lanes.EGate == null && f.Lanes.Total == 184 && f.LaneSource == null);
        totals.Minutes.Should().OnlyContain(m => m.Lanes.Vis == null && m.Lanes.Cit == null);
        totals.Minutes.Sum(m => m.Lanes.Total).Should().BeApproximately(view.Minutes.Sum(m => m.Lanes.Total), 0.1);
        totals.AlertWindow.Vis.Should().BeNull();
        (await Wave("DMO", 30, await WaveUserAsync(RoleCodes.BorderShiftSupervisor, "DMO"))).Data.Flights.Single(f => f.FlightKey == "AW101-WAVE-A").Lanes.Vis
            .Should().Be(90);
    }

    [Fact]
    public async Task ArrivalWave_Should_KeepEachSitesFlightsAndLaneDemandApart_When_TwoSitesShareAFlightKey()
    {
        await SeedAsync();
        await ProbeSiteAsync();
        _waveAdmin = await WaveUserAsync(RoleCodes.SystemAdministrator);
        FlightLegData Leg(int pax) => new("XK1-CROSS-A", "XK", "1", null, "Arrival", Now.AddMinutes(15), Now.AddMinutes(5), null, null, null, Origin: "BEY",
            Destination: "DMO", PaxEstimate: pax);
        foreach (var (site, pax) in new[] { ("DMO", 100), ("XS2", 300) })
            (await _host.AsCallerAsync(null, s => s.GetRequiredService<ISvcFlightIntake>().ApplyLegsAsync(site, "it-cross", [Leg(pax)], Now.AddMinutes(-1), Ct)))
                .Should().OnlyContain(r => !r.HasErrors);
        (await Apply<InboundFlightLaneDemand>(i => i.ApplyLaneDemandAsync(ImmigrationScope.ForSite("XS2"), "aman-kafka",
            [new("XS2", "XK1-CROSS-A", new DateTimeOffset(Now.AddMinutes(15)), 250, new Dictionary<string, int> { ["VIS"] = 200 }, 0,
                new DateTimeOffset(Now.AddMinutes(-5)), "xk1-ld")], Ct))).Should().OnlyContain(r => r.Applied);

        var dmo = (await Wave("DMO", 30)).Data.Flights.Single(f => f.FlightKey == "XK1-CROSS-A");
        dmo.Should().Match<ArrivalWaveFlightViewModel>(f => f.PassengerSource == "PaxEstimate" && f.LaneSource == "DefaultMix" && f.Passengers == 100,
            "XS2's lane demand for the same key never reaches DMO");
        var xs2 = (await Wave("XS2", 30)).Data;
        xs2.Flights.Single(f => f.FlightKey == "XK1-CROSS-A").Should().Match<ArrivalWaveFlightViewModel>(f => f.PassengerSource == "Aman" && f.Passengers == 250);
        xs2.Flights.Select(f => f.FlightKey).Should().NotContain(k => k.StartsWith("AW1", StringComparison.Ordinal), "DMO's legs never reach XS2");
        (await Wave("DMO", 30)).Data.Flights.Select(f => f.FlightKey).Should().NotContain(k => k.StartsWith("XP1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProjectedArrivalWave_Should_GiveEachQueueZoneTheLanesItsDesksServe_When_ARuleReadsIt()
    {
        await SeedAsync();
        // A second site at the demo airport whose published profile links queue zones to desks through service zones:
        // two queues for visitors, one for citizens and residents, an overflow band of the first, and a queue with no desk.
        await ProbeSiteAsync();
        var minute = new DateTime(Now.Ticks - Now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        var leg = new FlightLegData("XP1-WAVE-A", "XP", "1", null, "Arrival", minute.AddMinutes(10), null, null, minute.AddMinutes(-11), null, Origin: "BEY",
            Destination: "DMO", PaxEstimate: 300);
        (await _host.AsCallerAsync(null, s => s.GetRequiredService<ISvcFlightIntake>().ApplyLegsAsync("XS2", "it-wave", [leg], Now.AddMinutes(-1), Ct)))
            .Should().OnlyContain(r => !r.HasErrors);
        (await Apply<InboundFlightLaneDemand>(i => i.ApplyLaneDemandAsync(ImmigrationScope.ForSite("XS2"), "aman-kafka",
            [new("XS2", "XP1-WAVE-A", new DateTimeOffset(minute.AddMinutes(10)), 200, new Dictionary<string, int> { ["VIS"] = 100, ["CIT"] = 40, ["RES"] = 10 }, 0,
                new DateTimeOffset(Now.AddMinutes(-5)), "xp1-ld")], Ct))).Should().OnlyContain(r => r.Applied);

        async Task<IReadOnlyDictionary<DateTime, double>> Arrivals(string zone) =>
            await _host.AsCallerAsync(null, s => s.GetRequiredService<Ariva.Core.Alerting.IArrivalWaveSource>().ArrivalsAsync("XS2", zone, minute.AddMinutes(-1), minute.AddMinutes(60), Ct));

        // On-block 11 minutes ago: the 12 hall minutes start now, so the whole flight lies ahead; visitors are shared by two queues.
        (await Arrivals("Q-VIS-1")).Values.Sum().Should().BeApproximately(50, 1e-6);
        (await Arrivals("Q-VIS-2")).Values.Sum().Should().BeApproximately(50, 1e-6);
        (await Arrivals("OV-1")).Values.Sum().Should().BeApproximately(50, 1e-6, "an overflow band takes its queue's arrivals");
        (await Arrivals("Q-CIT")).Values.Sum().Should().BeApproximately(50, 1e-6);
        (await Arrivals("Q-CIT"))[minute].Should().BeApproximately(50 * 0.03, 1e-6);
        (await Arrivals("Q-NONE")).Should().BeNull("a queue serving no lane has no projection");
        (await _host.AsCallerAsync(null, s => s.GetRequiredService<Ariva.Core.Alerting.IArrivalWaveSource>()
            .ArrivalsAsync("XS2", "Q-VIS-1", minute.AddMinutes(-120), minute.AddMinutes(-60), Ct))).Should().BeEmpty("the past has no projection");
    }

    /// <summary>
    /// A second site, XS2, at the demo airport (once per database): a terminal, an immigration checkpoint with desks for
    /// visitors (two) and citizens and residents and an e-gate, all mapped to AMAN codes (XV1, XV2, XC1, XG1), and a
    /// published profile linking queue zones to the desks through service zones: two visitor queues, one for citizens and
    /// residents, an overflow band of the first and a queue with no desk.
    /// </summary>
    private Task ProbeSiteAsync() => ExecuteAsMigrationAsync("""
        DO $probe$
        BEGIN
            IF EXISTS (SELECT 1 FROM zone_profile WHERE id = 'a0470000-0000-0000-0000-000000000020') THEN
                RETURN;
            END IF;
                INSERT INTO site (id, code, name) SELECT gen_random_uuid(), 'XS2', 'Arrival wave probe' WHERE NOT EXISTS (SELECT 1 FROM site WHERE code = 'XS2');
                INSERT INTO terminal (id, airport_id, code, name, site_code)
                     SELECT 'a0470000-0000-0000-0000-000000000001', id, 'XT2', 'Probe terminal', 'XS2' FROM airport WHERE iata_code = 'DMO' AND deleted_on IS NULL;
                INSERT INTO level (id, terminal_id, site_code, code, name, floor_number, width_metres, depth_metres)
                     VALUES ('a0470000-0000-0000-0000-000000000002', 'a0470000-0000-0000-0000-000000000001', 'XS2', 'L0', 'Arrivals', 0, 100, 100);
                INSERT INTO checkpoint (id, level_id, site_code, code, name, kind)
                     VALUES ('a0470000-0000-0000-0000-000000000003', 'a0470000-0000-0000-0000-000000000002', 'XS2', 'IMM', 'Immigration', 'Immigration');
                INSERT INTO desk (id, checkpoint_id, site_code, code, name, kind, lane_category_codes) VALUES
                    ('a0470000-0000-0000-0000-000000000011', 'a0470000-0000-0000-0000-000000000003', 'XS2', 'V1', 'Visitors 1', 'Desk', 'VIS'),
                    ('a0470000-0000-0000-0000-000000000012', 'a0470000-0000-0000-0000-000000000003', 'XS2', 'V2', 'Visitors 2', 'Desk', 'VIS'),
                    ('a0470000-0000-0000-0000-000000000013', 'a0470000-0000-0000-0000-000000000003', 'XS2', 'C1', 'Citizens 1', 'Desk', 'CIT,RES'),
                    ('a0470000-0000-0000-0000-000000000014', 'a0470000-0000-0000-0000-000000000003', 'XS2', 'G1', 'E-gate 1', 'EGate', 'EG');
                INSERT INTO desk_code_mapping (id, system, external_code, desk_id, site_code) VALUES
                    (gen_random_uuid(), 'Aman', 'XV1', 'a0470000-0000-0000-0000-000000000011', 'XS2'),
                    (gen_random_uuid(), 'Aman', 'XV2', 'a0470000-0000-0000-0000-000000000012', 'XS2'),
                    (gen_random_uuid(), 'Aman', 'XC1', 'a0470000-0000-0000-0000-000000000013', 'XS2'),
                    (gen_random_uuid(), 'Aman', 'XG1', 'a0470000-0000-0000-0000-000000000014', 'XS2');
                INSERT INTO zone_profile (id, site_code, name, status) VALUES ('a0470000-0000-0000-0000-000000000020', 'XS2', 'Probe', 'Draft');
                INSERT INTO zone (id, profile_id, name, kind, level_id, queue_zone_id, desk_id, polygon) VALUES
                    ('a0470000-0000-0000-0000-000000000021', 'a0470000-0000-0000-0000-000000000020', 'Q-VIS-1', 'Queue', 'a0470000-0000-0000-0000-000000000002', NULL, NULL, 'probe'),
                    ('a0470000-0000-0000-0000-000000000022', 'a0470000-0000-0000-0000-000000000020', 'Q-VIS-2', 'Queue', 'a0470000-0000-0000-0000-000000000002', NULL, NULL, 'probe'),
                    ('a0470000-0000-0000-0000-000000000023', 'a0470000-0000-0000-0000-000000000020', 'Q-CIT', 'Queue', 'a0470000-0000-0000-0000-000000000002', NULL, NULL, 'probe'),
                    ('a0470000-0000-0000-0000-000000000024', 'a0470000-0000-0000-0000-000000000020', 'Q-NONE', 'Queue', 'a0470000-0000-0000-0000-000000000002', NULL, NULL, 'probe');
                INSERT INTO zone (id, profile_id, name, kind, level_id, queue_zone_id, desk_id, polygon) VALUES
                    ('a0470000-0000-0000-0000-000000000031', 'a0470000-0000-0000-0000-000000000020', 'S-V1', 'Service', 'a0470000-0000-0000-0000-000000000002', 'a0470000-0000-0000-0000-000000000021', 'a0470000-0000-0000-0000-000000000011', 'probe'),
                    ('a0470000-0000-0000-0000-000000000032', 'a0470000-0000-0000-0000-000000000020', 'S-V2', 'Service', 'a0470000-0000-0000-0000-000000000002', 'a0470000-0000-0000-0000-000000000022', 'a0470000-0000-0000-0000-000000000012', 'probe'),
                    ('a0470000-0000-0000-0000-000000000033', 'a0470000-0000-0000-0000-000000000020', 'S-C1', 'Service', 'a0470000-0000-0000-0000-000000000002', 'a0470000-0000-0000-0000-000000000023', 'a0470000-0000-0000-0000-000000000013', 'probe'),
                    ('a0470000-0000-0000-0000-000000000034', 'a0470000-0000-0000-0000-000000000020', 'OV-1', 'Overflow', 'a0470000-0000-0000-0000-000000000002', 'a0470000-0000-0000-0000-000000000021', NULL, 'probe');
                UPDATE zone_profile SET status = 'Published', version = 1, geometry_hash = repeat('a', 64), published_on = now() WHERE id = 'a0470000-0000-0000-0000-000000000020';
        END
        $probe$;
        """);

    [Fact]
    public async Task DeskFeed_Should_DriveTheDeskEngineFromAmanAndFillTheEgateMinutes_When_AmanRecordsAreStored()
    {
        await SeedAsync();
        await ProbeSiteAsync();
        var feed = new Ariva.Infra.Border.DeskFeed(_host.Provider.GetRequiredService<Ariva.Infra.Settings.DatabaseSettings>(), _host.Clock,
            new Ariva.Infra.Border.DeskFeedSettings(), Microsoft.Extensions.Logging.Abstractions.NullLogger<Ariva.Infra.Border.DeskFeed>.Instance);
        var t0 = new DateTime(Now.Ticks - Now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc).AddMinutes(1);
        _host.Clock.Advance(t0 - Now);
        (await feed.SiteAsync("XS2", Ct)).Should().BeTrue("the engine starts now");
        (await feed.SiteAsync("DMO", Ct)).Should().BeTrue();
        var id = 0;
        string Id() => $"df-{++id}";
        async Task Minute(int m, params object[] records)
        {
            _host.Clock.Advance(t0.AddMinutes(m + 1).AddSeconds(5) - Now);
            foreach (var record in records)
            {
                var results = record switch
                {
                    DeskSessionChanged session => await Apply<DeskSessionChanged>(i => i.ApplyDeskSessionsAsync(ImmigrationScope.ForSite("XS2"), "aman-kafka", [session], Ct)),
                    DeskIntervalStats desk => await Apply<DeskIntervalStats>(i => i.ApplyDeskIntervalsAsync(ImmigrationScope.ForSite("XS2"), "aman-kafka", [desk], Ct)),
                    EGateIntervalStats gate => await Apply<EGateIntervalStats>(i => i.ApplyEgateIntervalsAsync(ImmigrationScope.ForSite("XS2"), "aman-kafka", [gate], Ct)),
                    _ => throw new ArgumentException("record", nameof(records))
                };
                results.Should().OnlyContain(r => r.Applied && r.Warnings.Count == 0);
            }

            (await feed.SiteAsync("XS2", Ct)).Should().BeTrue();
            (await feed.SiteAsync("DMO", Ct)).Should().BeTrue();
        }

        DateTimeOffset At(int m, int s = 0) => new(t0.AddMinutes(m).AddSeconds(s));
        EGateIntervalStats Gate(int m) => new("XS2", "XG1", At(m), 60, 10, 7, 3, new Dictionary<EGateRejectCategory, int> { [EGateRejectCategory.DocumentRead] = 3 }, 18, Id());
        DeskIntervalStats Desk(int m, int transactions) => new("XS2", "XV1", At(m), 60, transactions, transactions, 40, 60, 30, "VIS", Id());

        await Minute(0, new DeskSessionChanged("XS2", "XV1", DeskSessionState.Opened, "VIS", At(0, 20), Id()),
            new DeskSessionChanged("XS2", "XV2", DeskSessionState.Closed, "", At(0, 20), Id()), Desk(0, 2), Gate(0));
        // The same desk code shapes at DMO in the same minutes: they reach DMO's desks only (CWE-863).
        (await Apply<DeskIntervalStats>(i => i.ApplyDeskIntervalsAsync(ImmigrationScope.ForSite("DMO"), "aman-kafka",
            [new("DMO", "IN09", At(0), 60, 50, 50, 40, 60, 30, "VIS", Id())], Ct))).Should().OnlyContain(r => r.Applied);
        for (var m = 1; m < 8; m++)
            await Minute(m, Desk(m, 2), Gate(m));
        // A replay of the same read changes nothing: each record is taken once.
        (await feed.SiteAsync("XS2", Ct)).Should().BeTrue();

        async Task<Dictionary<string, (double Idle, double Closed, double Unknown, long Transactions)>> Desks(int fromMinute, int toMinute) =>
            await RowsAsync("""
                SELECT desk_code, sum(idle_seconds + serving_seconds), sum(closed_seconds), sum(unknown_seconds), sum(transactions) FROM desk_minute
                 WHERE desk_code LIKE 'XS2/IMM/%' AND minute_utc >= @from AND minute_utc < @to GROUP BY desk_code
                """, r => (r.GetString(0), (r.GetDouble(1), r.GetDouble(2), r.GetDouble(3), r.GetInt64(4))), t0.AddMinutes(fromMinute), t0.AddMinutes(toMinute));

        var settled = await Desks(1, 6);
        settled["XS2/IMM/V1"].Idle.Should().Be(5 * 60, "logged in with transactions every minute: open");
        settled["XS2/IMM/V2"].Closed.Should().Be(5 * 60, "logged out: closed, and kept so by the feed's heartbeat");
        settled["XS2/IMM/C1"].Closed.Should().Be(5 * 60, "a desk AMAN reports no session for, while its feed is alive, is not logged in (F10 row 9)");
        (await _host.ReadAsync<long>("SELECT count(*) FROM egate_minute WHERE gate_code = 'XS2/IMM/G1' AND processed = 10 AND rejected = 3")).Should().Be(8);

        // AMAN stops: its desks turn unknown after T_stale.
        _host.Clock.Advance(t0.AddMinutes(20) - Now);
        (await feed.SiteAsync("XS2", Ct)).Should().BeTrue();
        (await Desks(14, 18))["XS2/IMM/V2"].Unknown.Should().Be(4 * 60);
        (await Desks(0, 18))["XS2/IMM/V1"].Transactions.Should().Be(2 * 8, "each interval's transactions counted once, and none of DMO's");
        (await feed.SiteAsync("DMO", Ct)).Should().BeTrue();
        var dmo = await RowsAsync("SELECT desk_code, sum(transactions) FROM desk_minute WHERE desk_code LIKE 'DMO/%' AND minute_utc >= @from AND minute_utc < @to GROUP BY desk_code",
            r => (r.GetString(0), r.GetInt64(1)), t0, t0.AddMinutes(18));
        dmo["DMO/IMM/AR-09"].Should().BeGreaterThanOrEqualTo(50, "DMO's interval reached DMO's desk (other tests feed DMO too)");
        (await _host.ReadAsync<long>("SELECT count(*) FROM desk_minute WHERE desk_code NOT LIKE 'DMO/%' AND desk_code NOT LIKE 'XS2/%'")).Should().Be(0);


        // Another replica holding the site: this one skips it.
        await using (var other = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync())))
        {
            await other.OpenAsync(Ct);
            await using (var hold = new NpgsqlCommand("SELECT pg_advisory_lock(49, hashtext('XS2'))", other))
                await hold.ExecuteNonQueryAsync(Ct);
            (await feed.SiteAsync("XS2", Ct)).Should().BeFalse();
        }

        // The rejects join the visitors' demand (F12): r measured from AMAN (30 percent here) times the e-gate eligible a minute earlier.
        async Task<double> Visitors() => (await _host.AsCallerAsync(null, s => s.GetRequiredService<Ariva.Core.Alerting.IArrivalWaveSource>()
            .ArrivalsAsync("XS2", "Q-VIS-1", Now.AddMinutes(-1), Now.AddMinutes(60), Ct))).Values.Sum();
        var before = await Visitors();
        var leg = new FlightLegData("XG1-WAVE-A", "XG", "1", null, "Arrival", Now.AddMinutes(10), null, null, Now.AddMinutes(-11), null, Origin: "BEY",
            Destination: "DMO", PaxEstimate: 100);
        (await _host.AsCallerAsync(null, s => s.GetRequiredService<ISvcFlightIntake>().ApplyLegsAsync("XS2", "it-wave", [leg], Now.AddMinutes(-1), Ct)))
            .Should().OnlyContain(r => !r.HasErrors);
        (await Apply<InboundFlightLaneDemand>(i => i.ApplyLaneDemandAsync(ImmigrationScope.ForSite("XS2"), "aman-kafka",
            [new("XS2", "XG1-WAVE-A", new DateTimeOffset(Now.AddMinutes(10)), 100, new Dictionary<string, int>(), 100, new DateTimeOffset(Now.AddMinutes(-1)), "xg1-ld")], Ct)))
            .Should().OnlyContain(r => r.Applied);
        (await Visitors() - before).Should().BeApproximately(0.3 * 100 / 2, 1e-6,
            "the flight brings only e-gate passengers; 30 of its 100 are rejected and shared by the two visitor queues");

        // A burst larger than one read (250 records in one batch, received at one instant, reads of 100) is read over a few steps, each once.
        var burst = new Ariva.Infra.Border.DeskFeed(_host.Provider.GetRequiredService<Ariva.Infra.Settings.DatabaseSettings>(), _host.Clock,
            new Ariva.Infra.Border.DeskFeedSettings { MaxRead = 100 }, Microsoft.Extensions.Logging.Abstractions.NullLogger<Ariva.Infra.Border.DeskFeed>.Instance);
        (await Apply<EGateIntervalStats>(i => i.ApplyEgateIntervalsAsync(ImmigrationScope.ForSite("XS2"), "aman-kafka",
            [.. Enumerable.Range(1, 250).Select(k => new EGateIntervalStats("XS2", "XG1", At(-k), 60, 4, 4, 0, null, 18, Id()))], Ct))).Should().OnlyContain(r => r.Applied);
        for (var k = 0; k < 4; k++)
            (await burst.SiteAsync("XS2", Ct)).Should().BeTrue();
        (await _host.ReadAsync<long>("SELECT count(*) FROM egate_minute WHERE gate_code = 'XS2/IMM/G1' AND processed = 4")).Should().Be(250);
    }

    private async Task<Dictionary<string, T>> RowsAsync<T>([System.Diagnostics.CodeAnalysis.ConstantExpected] string sql, Func<NpgsqlDataReader, (string, T)> map,
        DateTime from, DateTime to)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // literal statements of this class
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        command.Parameters.AddWithValue("from", from);
        command.Parameters.AddWithValue("to", to);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new Dictionary<string, T>(StringComparer.Ordinal);
        while (await reader.ReadAsync(Ct))
        {
            var (key, value) = map(reader);
            rows[key] = value;
        }

        return rows;
    }

    private Task<Fluentx.Result<ArrivalWaveViewModel>> Wave(string site, int minutes, Guid? caller = null) =>
        _host.AsCallerAsync(caller ?? _waveAdmin, s => s.GetRequiredService<ISvcArrivalWave>().GetAsync(site, minutes, Ct));

    private Guid? _waveAdmin;

    /// <summary>An administrator of every site (the lane split included), and a user with one role at the given sites.</summary>
    private async Task<Guid> WaveUserAsync(string role, params string[] sites)
    {
        var user = await _host.CreateUserAsync("it.wave." + Guid.NewGuid().ToString("N")[..10], roles: [role]);
        if (sites.Length == 0)
            await _host.ReadAsync<int>("UPDATE \"user\" SET all_sites = true WHERE id = @id RETURNING 1", user);
        foreach (var site in sites)
            await ExecuteAsMigrationAsync($"INSERT INTO user_site (id, user_id, site_code) VALUES (gen_random_uuid(), '{user}', '{site}')");
        return user;
    }

    [Theory]
    [InlineData("UPDATE border_desk_interval SET documents_processed = 0", "42501")]
    [InlineData("DELETE FROM border_desk_session", "42501")]
    [InlineData("DELETE FROM inbound_lane_demand", "42501")]
    [InlineData("UPDATE border_egate_interval SET attempts = 0", "42501")]
    [InlineData("DELETE FROM border_egate_interval", "42501")]
    [InlineData("UPDATE border_desk_session SET state = 'Closed'", "42501")]
    [InlineData("DELETE FROM border_desk_interval", "42501")]
    [InlineData("DELETE FROM desk_feed_state", "42501")]
    [InlineData("TRUNCATE desk_feed_state", "42501")]
    [InlineData("INSERT INTO border_egate_interval (id, site_code, gate_code, interval_start_utc, attempts, accepted, rejected, rejects_other, rejects_document_read, rejects_biometric_capture, rejects_eligibility, rejects_referred_to_officer, rejects_technical, mean_cycle_seconds, feed, source_event_id, received_utc) VALUES (gen_random_uuid(), 'DMO', 'EGIN1', date_trunc('minute', now()), 4, 2, 2, 0, 2, 0, 0, 0, 0, 18, 'aman-kafka', 'db-small-cell', now())", "23514")]
    [InlineData("INSERT INTO border_desk_interval (id, site_code, desk_code, interval_start_utc, transactions_processed, documents_processed, mean_service_seconds, p90_service_seconds, mean_cycle_seconds, lane_category, feed, source_event_id, received_utc) VALUES (gen_random_uuid(), 'DMO', 'IN01', date_trunc('minute', now()), 5, 3, 30, 40, 12, 'CIT', 'aman-kafka', 'db-sums', now())", "23514")]
    public async Task Database_Should_RefuseChangesAndBrokenRecords_When_TheRuntimeRoleTries(string sql, string state)
    {
        await SeedAsync();
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE ariva_runtime", connection, transaction))
            await role.ExecuteNonQueryAsync(Ct);
#pragma warning disable CA2100 // literal statements from the inline data above
        await using var command = new NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100

        var change = () => command.ExecuteNonQueryAsync(Ct);

        (await change.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(state);
    }

    private async Task ExecuteAsMigrationAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // literal statements of this class
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync(Ct);
    }
}
