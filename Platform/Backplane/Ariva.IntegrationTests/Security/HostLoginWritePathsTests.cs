using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using Ariva.Core;
using Ariva.Core.Border;
using Ariva.Core.Desks;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Flights;
using Ariva.Core.Queueing;
using Ariva.Core.Sensing;
using Ariva.Core.Services;
using Ariva.Core.Services.Administration;
using Ariva.Core.Services.Border;
using Ariva.Core.Services.Flights;
using Ariva.Core.Services.Reports;
using Ariva.Di;
using Ariva.Di.Extensions;
using Ariva.Infra.Border;
using Ariva.Infra.DataProtection;
using Ariva.Infra.Integration;
using Ariva.Infra.Notifications;
using Ariva.Infra.Security;
using Ariva.Infra.Sensing;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Reports;
using Ariva.Infra.Services.Seed;
using Ariva.Infra.Settings;
using Ariva.Infra.Streaming;
using Ariva.IntegrationTests.Setup;
using Ariva.IntegrationTests.Streaming;
using Ariva.Simulation.Api.Emulators.Aman;
using Ariva.Simulation.Api.Scenarios.Engine;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Ariva.IntegrationTests.Security;

/// <summary>
/// ARV-083d (CWE-269; ASVS 5.0 V13.2.2): the hosts' write paths other than authentication and the key ring (those are
/// <see cref="HostLoginServicesTests"/>) run as the production code, each under the narrowed login of the host that runs it, on a
/// database a cluster's migration job migrated (k8s-dev, every Database:HostLogins and Database:Availability). A grant one of
/// them needs and its host role lacks fails here with 42501, not first in a cluster: the other suites run these services as the
/// fixture's superuser, and the E2E suites on vm-local's development login, which holds every role.
/// <list type="bullet">
/// <item>Ariva.Api.Cronz's login: the secret re-protection round (an account's TOTP seed, an integration client's seed and an
/// outbound endpoint's secret, each rewritten under the new Data Protection key) and the scheduled report deliveries (the round
/// prepares the delivery rows, claims each, reads its report and records it sent), and the staffing recommendation for the demo
/// airport (its inputs read, its rows written by binary COPY).</item>
/// <item>Ariva.Api.Stream's login: each binary COPY writer it runs: a checkpoint of the queue engine with every kind of output
/// (queue minutes and live minutes, bins and their health, line and overflow minutes with their events, desk zone readings, desk
/// minutes, outages, states and offsets), written twice (the upserts), the sensing archive, the desk feed's desk and sensor-only
/// minutes, and a round of the desk feed itself.</item>
/// <item>Ariva.Api.Integration's login: the AODB intake (legs, milestones and counter allocations, an AIDX message of the
/// emulated AODB, the feed freshness sweep) and the immigration intake (a minute of the emulated AMAN feed: sessions, desk and
/// e-gate intervals and lane demand).</item>
/// </list>
/// The setup (the demo topology, the accounts, the report schedule) is Ariva.Api.Main's own, under its login.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class HostLoginWritePathsTests(PostgresFixture fixture)
{
    #region Fields

    private const string Prefix = "it_hlw_";

    private const string Seed = "JBSWY3DPEHPK3PXP";

    private const string RecipientEmail = "host.login.report@ariva.test";

    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static readonly DateTime At = new(2026, 10, 2, 14, 5, 0, DateTimeKind.Utc);

    private static string _database;

    private static Dictionary<string, string> _logins;

    private static (Guid Admin, Guid Recipient, Guid Schedule)? _seeded;

    #endregion

    #region Properties

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    #endregion

    #region Ariva.Api.Cronz

    [Fact]
    public async Task CronzLogin_Should_ReprotectTheStoredSecrets_When_TheDataProtectionKeyHasRolled()
    {
        var (database, logins) = await MigratedAsync();
        await using var cronz = Host(database, logins, "Cronz", configure: services =>
        {
            services.RemoveAll<IDataProtectionProvider>();
            services.AddArivaDataProtection(new DatabaseSettings(), DataProtectionCertificates.From(Wrapping()), KeyRingSigning.From(Signer()));
            var repository = new MemoryRepository();
            services.Configure<KeyManagementOptions>(options => options.XmlRepository = repository);
        });
        await cronz.StartAsync();
        await AssertRunsAsAsync(cronz, logins, "Cronz");
        var provider = cronz.Provider.GetRequiredService<IDataProtectionProvider>();
        var accounts = (IPersistedDataProtector)provider.CreateProtector(Totp.DataProtectionPurpose);
        var clients = (IPersistedDataProtector)provider.CreateProtector(IntegrationCredentials.SeedProtectionPurpose);
        var outbound = (IPersistedDataProtector)provider.CreateProtector(OutboundSecrets.Purpose);
        var (userId, clientId, endpointId) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var values = new Dictionary<string, object>
        {
            ["user"] = userId,
            ["client"] = clientId,
            ["endpoint"] = endpointId,
            ["seed"] = accounts.Protect(Seed),
            ["clientSeed"] = clients.Protect(Seed),
            ["secret"] = outbound.Protect("{\"ApiKey\":\"host-login-partner\"}")
        };
        await ExecuteAsync(database, Statement.InsertProtectedSecrets, values);

        // The default key is the most recently activated one: a new key, active a second from now, makes the stored values old.
        cronz.Provider.GetRequiredService<IKeyManager>().CreateNewKey(DateTimeOffset.UtcNow.AddSeconds(1), DateTimeOffset.UtcNow.AddDays(90));
        for (var attempt = 0; attempt < 100 && !RequiresMigration(accounts, (string)values["seed"]); attempt++)
            await Task.Delay(TimeSpan.FromMilliseconds(200), Ct);

        var run = await cronz.Provider.GetRequiredService<SecretReprotectionRound>().RunAsync(Ct);

        run.Failed.Should().Be(0);
        run.Reprotected.Should().BeGreaterThanOrEqualTo(3, "Cronz's login reads and rewrites the three protected columns");
        var after = await RowAsync(database, Statement.ProtectedSecrets, values);
        after[0].Should().NotBe((string)values["seed"], "the account's seed is protected again under the new key");
        after[1].Should().NotBe((string)values["clientSeed"]);
        after[2].Should().NotBe((string)values["secret"]);
        (accounts.Unprotect(after[0]) == Seed).Should().BeTrue("the seed reads back unchanged");
        (clients.Unprotect(after[1]) == Seed).Should().BeTrue();
        RequiresMigration(outbound, after[2]).Should().BeFalse("the outbound secret is under the default key now");
    }

    [Fact]
    public async Task CronzLogin_Should_PrepareClaimAndRecordTheScheduledReports_When_TheDeliveryRoundRuns()
    {
        var (database, logins) = await MigratedAsync();
        var (_, _, schedule) = await SeededAsync();
        var transport = new RecordingTransport();
        await using var cronz = Host(database, logins, "Cronz", new Dictionary<string, string>
        {
            ["Email:Enabled"] = "true",
            ["Email:FromAddress"] = "no-reply@ariva.test"
        }, services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IEmailTransport>(transport));
            services.TryAddScoped<ReportReader>();
            services.TryAddScoped<ISvcReportDeliveries, SvcReportDeliveries>();
            services.TryAddSingleton<ReportDeliveryRound>();
        });
        await cronz.StartAsync();
        await AssertRunsAsAsync(cronz, logins, "Cronz");
        var round = cronz.Provider.GetRequiredService<ReportDeliveryRound>();

        // The host's clock is 2026-10-01 08:00 UTC, 12:00 in Dubai: the 06:00 schedule owes the 30th.
        (await round.RunAsync(Ct)).Should().Be(new ReportDeliveryRun(1, 1, 0, 0), "Cronz's login prepares the delivery, claims it, reads its report and records it");
        transport.Sent.Should().ContainSingle().Which.Recipient.Should().Be(RecipientEmail);
        (await RowAsync(database, Statement.Deliveries, new Dictionary<string, object> { ["schedule"] = schedule }))[0].Should().Be("Sent:1");
        (await round.RunAsync(Ct)).Should().Be(new ReportDeliveryRun(0, 0, 0, 0), "a sent report is never owed again");
    }

    [Fact]
    public async Task CronzLogin_Should_WriteTheStaffingRecommendations_When_TheJobRunsForTheDemoAirport()
    {
        var (database, logins) = await MigratedAsync();
        await SeededAsync();
        await using var cronz = Host(database, logins, "Cronz", configure: services => services.AddArivaStaffingJob(new ConfigurationBuilder().Build()));
        await cronz.StartAsync();
        await AssertRunsAsAsync(cronz, logins, "Cronz");
        var staffing = cronz.Provider.GetRequiredService<Ariva.Infra.Staffing.StaffingRecommendations>();

        var pass = await staffing.SiteAsync("DMO", Ct);

        pass.Refused.Should().BeNull("Cronz's login reads the site's lanes, desks, queues and flights");
        pass.Rows.Should().BeGreaterThan(0, "and writes the recommendations by binary COPY");
        (await RowAsync(database, Statement.StaffingRows))[0].Should().Be($"{pass.Rows}");
    }

    #endregion

    #region Ariva.Api.Stream

    [Fact]
    public async Task StreamLogin_Should_RunEveryBinaryCopyWriter_When_TheEngineCheckpointsArchivesAndFeedsTheDesks()
    {
        var (database, logins) = await MigratedAsync();
        await SeededAsync();
        var stream = LoginSettings(database, logins, "Stream");
        (await CurrentUserAsync(stream)).Should().Be(logins["Database:HostLogins:Stream:Username"], "the writers below connect as Stream's own login");

        // The queue engine's checkpoint: what the engine produced for an evening, with every other kind of output beside it.
        var geometry = new QueueZoneGeometry("IT-HLW", new HashSet<string> { "IT-HLW entry" }, new HashSet<string> { "IT-HLW exit" }, new HashSet<string>(), new HashSet<string>());
        var zone = new ZoneProcessor("DMO/IT-HLW", geometry, 7);
        foreach (var batch in QueueStreamTests.Evening("IT-HLW", 0))
            zone.Offer(batch, DateTime.MaxValue);
        var minute = new DateTime(2026, 9, 28, 19, 10, 0, DateTimeKind.Utc);
        var outputs = zone.Drain() with
        {
            Overflow = [new OverflowMinute("IT-HLW band", minute, 2, 6)],
            OverflowChanges = [new OverflowChange("IT-HLW band", minute, OverflowChangeKind.Occupied, 6, null)],
            DeskReadings = [new DeskZoneSample("DMO/IMM/HLW1", DeskSource.StaffZone, minute, 1, false)],
            Outages = [new DeviceOutage("DMO/IT-HLW", "S-42", minute, minute.AddMinutes(5))]
        };
        outputs.Minutes.Should().NotBeEmpty();
        outputs.Bins.Should().NotBeEmpty();
        outputs.Live.Should().NotBeEmpty();
        outputs.Health.Should().NotBeEmpty();
        outputs.Lines.Should().NotBeEmpty();
        var desk = new DeskMinute("DMO/IMM/HLW1", "CIT", minute, TimeSpan.Zero, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(40), TimeSpan.Zero, TimeSpan.Zero, 2,
            TimeSpan.Zero, TimeSpan.Zero, false);
        var checkpoint = new StreamCheckpoint("it-hlw.queue", [outputs], [zone.Capture()], [desk], [new StreamOffset("ariva.device.track-sample.v1", 0, 42)]);
        var store = new StreamStore(stream, TimeProvider.System, NullLogger<StreamStore>.Instance);
        await store.SaveAsync(checkpoint, Ct);
        await store.SaveAsync(checkpoint, Ct);

        (await RowAsync(database, Statement.StreamRows)).Should().Equal(
            [$"{outputs.Live.Select(l => l.MinuteUtc).Concat(outputs.Minutes.Select(m => m.StartUtc)).Distinct().Count()}", "True", "True", $"{outputs.Lines.Count}", "1", "1", "1", "1", "1", "1", "1"],
            "every output written once, the second checkpoint through the upserts: queue minutes, bins, health, lines, overflow, its event, desk readings, desk minutes, outages, state, offset");

        // The sensing archive: every kind of event by COPY, with its batch rows and the day's pseudonym key.
        var archive = new SensingArchive(stream, new ManualClock(new DateTimeOffset(At.AddMinutes(10))), NullLogger<SensingArchive>.Instance);
        var written = await archive.WriteAsync([
            Batch(new TrackSampleBatch { Samples = [new Sensed<TrackPosition>(new TrackPosition("S-83/1", 10, 12, 1.7, At), At, SensedFlags.None)] }),
            Batch(new VendorLineCrossingBatch
            {
                Crossings = [new Sensed<LineCrossing>(new LineCrossing("Entry A", CrossingDirection.In, "S-83/1", At.AddSeconds(2)), At.AddSeconds(2), SensedFlags.None)]
            }),
            Batch(new ZoneOccupancyBatch { Occupancy = [new Sensed<ZoneOccupancy>(new ZoneOccupancy("Snake A", 4, At.AddSeconds(3)), At.AddSeconds(3), SensedFlags.None)] }),
            Batch(new IntervalCountBatch { Intervals = [new Sensed<IntervalCount>(new IntervalCount("Exit A", 2, 3, At.AddMinutes(-5), At), At, SensedFlags.None)] })
        ], Ct);
        written.Should().Be(new SensingArchiveWrite(4, 0, 4, 0), "Stream's login archives each batch and its events");
        (await archive.ReadAsync(new SensingReplayQuery("HLW", "Snake A", At.AddMinutes(-1), At.AddMinutes(1)), Ct).ToListAsync(Ct)).Should().HaveCount(4,
            "and reads them back, as a replay does");

        // The desk feed: its desk minutes and the sensor-only engine's, by COPY in one transaction, as DeskFeed writes them; then a
        // round of the feed for the demo airport, every read and its state's write.
        await using (var connection = new NpgsqlConnection(stream.BuildConnectionString()))
        {
            await connection.OpenAsync(Ct);
            await using var transaction = await connection.BeginTransactionAsync(Ct);
            var later = desk with { MinuteUtc = minute.AddMinutes(1) };
            await StreamStore.WriteDeskMinutesAsync(connection, [later], DateTime.UtcNow, Ct);
            await StreamStore.WriteDeskSensorMinutesAsync(connection, [later], DateTime.UtcNow, Ct);
            await transaction.CommitAsync(Ct);
        }

        (await RowAsync(database, Statement.DeskFeedRows)).Should().Equal(["2", "1"], "the desk feed's minutes and the sensor-only minute");
        var feed = new DeskFeed(stream, new ManualClock(new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero)), new DeskFeedSettings(), NullLogger<DeskFeed>.Instance);
        (await feed.SitesAsync(Ct)).Should().Contain(site => site.Code == "DMO", "the demo airport has AMAN desk code mappings");
        (await feed.SiteAsync("DMO", Ct)).Should().BeTrue("Stream's login reads the site's desks, records and zones and keeps the feed's state");
        (await RowAsync(database, Statement.DeskFeedState))[0].Should().Be("1");
    }

    #endregion

    #region Ariva.Api.Integration

    [Fact]
    public async Task IntegrationLogin_Should_StoreTheAodbAndImmigrationRecords_When_TheFeedsSendThem()
    {
        var (database, logins) = await MigratedAsync();
        await SeededAsync();
        await using var integration = Host(database, logins, "Integration", configure: services =>
        {
            services.AddArivaBorderFeed(new ConfigurationBuilder().Build());
            services.AddArivaFlights(new ConfigurationBuilder().Build(), watchFeeds: false);
        });
        await integration.StartAsync();
        await AssertRunsAsAsync(integration, logins, "Integration");
        var now = integration.Clock.GetUtcNow().UtcDateTime;
        Task<T> AsIntegration<T>(Func<IServiceProvider, Task<T>> work) => integration.AsCallerAsync(null, work);

        // The AODB intake: legs, a milestone and counters through the API, then the emulated AODB's AIDX for a demo minute.
        var arrival = new FlightLegData("DM214-HLW-A", "DM", "214", null, "Arrival", now.AddMinutes(120), Origin: "BEY", Destination: "DMO", Stand: "B12", Seats: 180,
            PaxEstimate: 160);
        var departure = new FlightLegData("XR331-HLW-D", "XR", "331", null, "Departure", now.AddMinutes(180), Origin: "DMO", Destination: "DXB", Seats: 180);
        var legs = await AsIntegration(s => s.GetRequiredService<ISvcFlightIntake>().ApplyLegsAsync("DMO", "api", [arrival, departure], now.AddMinutes(-10), Ct));
        var events = await AsIntegration(s => s.GetRequiredService<ISvcFlightIntake>().ApplyEventsAsync("DMO", "aidx",
            [new FlightEventData(arrival.FlightKey, "Landed", now.AddMinutes(118))], now.AddMinutes(-5), Ct));
        var counters = await AsIntegration(s => s.GetRequiredService<ISvcFlightIntake>().ApplyAllocationsAsync("DMO", "api",
            [new CounterAllocationData(departure.FlightKey, "CI", ["CNT-1", "CNT-2"], now.AddMinutes(60), now.AddMinutes(150))], now.AddMinutes(-5), Ct));
        legs.Concat(events).Concat(counters).Should().OnlyContain(r => r.Applied && !r.HasErrors, "Integration's login writes legs, milestones and allocations");

        var day = ScenarioDay.Run(ScenarioConfig.Reference(9303));
        var dayStart = now.AddMinutes(-1110);
        var scheduled = Ariva.Simulation.Api.Emulators.Aodb.AodbSchedule.At(day, 1110, m => dayStart.AddMinutes(m), "DMO");
        var (message, error) = Ariva.Infra.Flights.Aidx.AidxReader.Read(System.Text.Encoding.UTF8.GetBytes(
            Ariva.Simulation.Api.Emulators.Aodb.AodbSchedule.Aidx(scheduled, now, "SIM-HLW-1110")));
        error.Should().BeNull();
        var aidx = await AsIntegration(s => s.GetRequiredService<ISvcAidxIntake>().ApplyAsync("DMO", "api-sim-aodb", message, Ct));
        aidx.Where(r => !r.Applied).Select(r => r.FlightKey + ": " + string.Join(" ", r.Errors.Concat(r.Warnings))).Should().BeEmpty();
        aidx.Should().HaveCount(scheduled.Count);
        (await AsIntegration(s => s.GetRequiredService<ISvcFeedFreshness>().SweepAsync(Ct))).Ran.Should().BeTrue("Integration's login sweeps the feeds' freshness");

        // The immigration intake: a minute of the emulated AMAN feed, every kind of record.
        var start = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc).AddMinutes(-1110);
        var minute = AmanFeed.Build(day, 1110, m => start.AddMinutes(m), "DMO", BorderSides.Both, true);
        var immigration = new List<ImmigrationItemResult>();
        immigration.AddRange(await AsIntegration(s => s.GetRequiredService<ISvcImmigrationIntake>().ApplyDeskSessionsAsync(ImmigrationScope.RecordSite, "aman-kafka", minute.Sessions, Ct)));
        immigration.AddRange(await AsIntegration(s => s.GetRequiredService<ISvcImmigrationIntake>().ApplyDeskIntervalsAsync(ImmigrationScope.RecordSite, "aman-kafka", minute.Desks, Ct)));
        immigration.AddRange(await AsIntegration(s => s.GetRequiredService<ISvcImmigrationIntake>().ApplyEgateIntervalsAsync(ImmigrationScope.RecordSite, "aman-kafka", minute.Gates, Ct)));
        immigration.AddRange(await AsIntegration(s => s.GetRequiredService<ISvcImmigrationIntake>().ApplyLaneDemandAsync(ImmigrationScope.ForSite("DMO"), "aman-kafka", minute.Demand, Ct)));
        immigration.Should().HaveCount(minute.Count).And.OnlyContain(r => r.Applied && !r.HasErrors, "Integration's login stores every kind of immigration record");

        (await RowAsync(database, Statement.IntakeRows)).Should().Equal(
            ["2", $"{scheduled.Count}", "1", "1", $"{minute.Sessions.Count}", $"{minute.Desks.Count}", $"{minute.Gates.Count}", "True", "True"],
            "legs, the milestone, the allocation, sessions, desk and e-gate intervals, lane demand and the feeds' freshness");
    }

    #endregion

    #region Helpers

    /// <summary>One database migrated by a cluster's job with every host's login, for the class.</summary>
    private async Task<(string Database, Dictionary<string, string> Logins)> MigratedAsync()
    {
        await Gate.WaitAsync(Ct);
        try
        {
            if (_database is null)
            {
                var database = await fixture.CreateDatabaseAsync(TestDatabase.HostLoginWritePaths);
                var logins = HostLogins.ClusterSettings(Prefix);
                var settings = new Dictionary<string, string>(logins, StringComparer.Ordinal)
                {
                    ["Application:Environment"] = "k8s-dev",
                    ["Database:Host"] = fixture.Hostname,
                    ["Database:Port"] = fixture.Port.ToString(CultureInfo.InvariantCulture),
                    ["Database:Name"] = database,
                    ["Database:Migration:Username"] = fixture.AdminUsername,
                    ["Database:Migration:Password"] = fixture.AdminPassword,
                    ["Database:Username"] = logins["Database:HostLogins:Main:Username"],
                    ["Database:Password"] = logins["Database:HostLogins:Main:Password"]
                };
                (await DatabaseMigration.RunAsync(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), Ct)).Should().Be(0,
                    "the cluster's job with every host login");
                _logins = logins;
                _database = database;
            }

            return (_database, _logins);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Once for the class, as Ariva.Api.Main under its login: the demo airport (DMO, its AMAN desk code mappings among it), an
    /// administrator, a terminal duty manager of DMO with an email address, and a daily report schedule at 06:00 for that account.
    /// </summary>
    private async Task<(Guid Admin, Guid Recipient, Guid Schedule)> SeededAsync()
    {
        var (database, logins) = await MigratedAsync();
        await Gate.WaitAsync(Ct);
        try
        {
            if (_seeded is { } known)
                return known;

            await using var main = Host(database, logins, "Main");
            var admin = await main.CreateUserAsync("it.hlw.admin", roles: [RoleCodes.SystemAdministrator], allSites: true);
            await main.AsCallerAsync(null, s =>
                new DemoTopologySeed(s.GetRequiredService<IUnitOfWork>(), s.GetRequiredService<ICurrentUser>(), main.Clock, s.GetRequiredService<AuditTrail>()).RunAsync(Ct));
            var recipient = await main.CreateUserAsync("it.hlw.report", roles: [RoleCodes.TerminalDutyManager]);
            (await main.AsCallerAsync(admin, s => s.GetRequiredService<ISvcUsers>().SetSitesAsync(recipient, new SiteAccessRequest(false, ["DMO"]), Ct)))
                .HasErrors.Should().BeFalse();
            await ExecuteAsync(database, Statement.SetEmail, new Dictionary<string, object> { ["id"] = recipient, ["email"] = RecipientEmail });
            var schedule = await main.AsCallerAsync(admin, s => s.GetRequiredService<ISvcReportSchedules>().CreateAsync(
                new ReportScheduleRequest("DMO", "Host login reports", null, "06:00", [recipient]), Ct));
            schedule.HasErrors.Should().BeFalse(string.Join(", ", schedule.ErrorMessages ?? []));
            _seeded = (admin, recipient, schedule.Data.Id);
            return _seeded.Value;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>A host's own login on the class's database, as its Database section gives it.</summary>
    private DatabaseSettings LoginSettings(string database, Dictionary<string, string> logins, string host) => new()
    {
        Host = fixture.Hostname,
        Port = fixture.Port,
        Name = database,
        Username = logins[$"Database:HostLogins:{host}:Username"],
        Password = logins[$"Database:HostLogins:{host}:Password"]
    };

    /// <summary>The production persistence, messaging and account registration of a host, connected as that host's own login.</summary>
    private AccountsHost Host(string database, Dictionary<string, string> logins, string host, Dictionary<string, string> settings = null,
        Action<IServiceCollection> configure = null)
    {
        var values = new Dictionary<string, string>(settings ?? [], StringComparer.Ordinal)
        {
            ["Database:Username"] = logins[$"Database:HostLogins:{host}:Username"],
            ["Database:Password"] = logins[$"Database:HostLogins:{host}:Password"]
        };
        return new AccountsHost(fixture, values, configure: configure, databaseName: database);
    }

    private static T Batch<T>(T batch) where T : SensingBatch
    {
        batch.Id = Guid.NewGuid();
        batch.DeviceId = Guid.Parse("0199a000-0000-7000-8000-0000000d0083");
        batch.DeviceCode = "S-83";
        batch.SiteCode = "HLW";
        batch.QueueZoneName = "Snake A";
        batch.Dialect = "Canonical";
        batch.Commissioned = true;
        batch.ReceivedUtc = At.AddSeconds(4);
        batch.Clock = new ClockReading(0, true, ClockState.Ok);
        return batch;
    }

    /// <summary>
    /// The ARV-083d review: the host's unit of work and the database settings its other services take (the COPY writers, the
    /// report reader) connect as that host's own login, so no test here passes on another login.
    /// </summary>
    private static async Task AssertRunsAsAsync(AccountsHost host, Dictionary<string, string> logins, string name)
    {
        var login = logins[$"Database:HostLogins:{name}:Username"];
        var current = await host.AsCallerAsync(null, async s => (await s.GetRequiredService<IUnitOfWork>().StorageProvider
            .ExecuteSqlAsync<NameRow>("SELECT CAST(current_user AS text) AS \"Value\"", ct: Ct))[0].Value);
        current.Should().Be(login, $"{name}'s unit of work connects as its own login");
        var settings = host.Provider.GetRequiredService<DatabaseSettings>();
        settings.Username.Should().Be(login);
        (await CurrentUserAsync(settings)).Should().Be(login, $"{name}'s database settings connect as its own login");
    }

    /// <summary>The login a connection with these settings runs as.</summary>
    private static async Task<string> CurrentUserAsync(DatabaseSettings settings)
    {
        await using var connection = new NpgsqlConnection(settings.BuildConnectionString());
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT CAST(current_user AS text)", connection);
        return (string)await command.ExecuteScalarAsync(Ct);
    }

    private sealed class NameRow
    {
        public string Value { get; set; }
    }

    private static bool RequiresMigration(IPersistedDataProtector protector, string value)
    {
        protector.DangerousUnprotect(Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlDecode(value), false, out var requiresMigration, out _);
        return requiresMigration;
    }

    private static X509Certificate2 Wrapping()
    {
        using var rsa = RSA.Create(DataProtectionCertificates.MinimumRsaKeyBits);
        using var created = new CertificateRequest("CN=Ariva DP (host login write paths)", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12, "test"), "test", X509KeyStorageFlags.Exportable);
    }

    private static X509Certificate2 Signer()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Ariva key ring signing (host login write paths)", ecdsa, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12, "test"), "test", X509KeyStorageFlags.Exportable);
    }

    private enum Statement
    {
        SetEmail,
        InsertProtectedSecrets,
        ProtectedSecrets,
        Deliveries,
        StreamRows,
        DeskFeedRows,
        DeskFeedState,
        IntakeRows,
        StaffingRows
    }

    // A switch over string literals keeps every command text constant (CA2100); values are bound as parameters.
    private static string Sql(Statement statement) => statement switch
    {
        Statement.SetEmail => "UPDATE \"user\" SET email = @email WHERE id = @id",
        Statement.InsertProtectedSecrets => """
            INSERT INTO "user" (id, user_name, password_hash, password_salt, password_algorithm, password_iterations, must_change_password, totp_enrolled,
                is_disabled, failed_login_count, is_break_glass, all_sites, totp_secret_protected)
            VALUES (@user, 'it.hlw.reprotect', 'hash', 'salt', 'pbkdf2-sha256', 600000, false, true, false, 0, false, false, @seed);
            INSERT INTO integration_client (id, client_id, name, kind, status, scope_names, site_codes, allowed_networks, require_totp_per_request,
                secret_algorithm, secret_iterations, secret_salt, secret_hash, secret_changed_utc, totp_secret_protected, totp_changed_utc,
                tokens_valid_from_utc, token_version)
            VALUES (@client, 'ic_hostloginreprotectionabcde', 'Host login reprotect client', 'Other', 'Active', 'queues:read', 'DMO', '10.0.0.0/8', false,
                'pbkdf2-sha256', 600000, 'salt', 'hash', now(), @clientSeed, now(), now(), 0);
            INSERT INTO outbound_endpoint (id, code, name, purpose, status, site_codes, base_url, allowed_networks, auth_kind, header_name,
                totp_per_request, timeout_seconds, retry_count, breaker_failures, break_seconds, poll_seconds, secret_protected,
                has_client_certificate, secret_changed_utc, client_version)
            VALUES (@endpoint, 'hlw-reprotect', 'Host login reprotect partner', 'Generic', 'Active', 'DMO', 'https://partner.example/', '10.0.0.0/8', 'ApiKeyHeader',
                'X-Api-Key', false, 10, 0, 5, 30, 0, @secret, false, now(), 0)
            """,
        Statement.ProtectedSecrets => """
            SELECT (SELECT totp_secret_protected FROM "user" WHERE id = @user), (SELECT totp_secret_protected FROM integration_client WHERE id = @client),
                   (SELECT secret_protected FROM outbound_endpoint WHERE id = @endpoint)
            """,
        Statement.Deliveries => "SELECT string_agg(status || ':' || attempts, ',' ORDER BY status) FROM report_delivery WHERE schedule_id = @schedule",
        Statement.StreamRows => """
            SELECT (SELECT count(*) FROM queue_minute WHERE zone_key = 'DMO/IT-HLW'),
                   (SELECT count(*) > 0 FROM queue_bin WHERE zone_key = 'DMO/IT-HLW'),
                   (SELECT count(*) > 0 FROM zone_health_bin WHERE zone_key = 'DMO/IT-HLW'),
                   (SELECT count(*) FROM line_minute WHERE zone_key = 'DMO/IT-HLW'),
                   (SELECT count(*) FROM overflow_minute WHERE zone_key = 'DMO/IT-HLW'),
                   (SELECT count(*) FROM outbox_message WHERE topic = 'ariva.flow.overflow-detected.v1' AND message_key = 'DMO/IT-HLW'),
                   (SELECT count(*) FROM desk_zone_reading WHERE desk_code = 'DMO/IMM/HLW1'),
                   (SELECT count(*) FROM desk_minute WHERE desk_code = 'DMO/IMM/HLW1'),
                   (SELECT count(*) FROM zone_outage WHERE zone_key = 'DMO/IT-HLW'),
                   (SELECT count(*) FROM stream_zone_state WHERE zone_key = 'DMO/IT-HLW'),
                   (SELECT count(*) FROM stream_offset WHERE consumer_group = 'it-hlw.queue')
            """,
        Statement.DeskFeedRows => """
            SELECT (SELECT count(*) FROM desk_minute WHERE desk_code = 'DMO/IMM/HLW1'), (SELECT count(*) FROM desk_sensor_minute WHERE desk_code = 'DMO/IMM/HLW1')
            """,
        Statement.DeskFeedState => "SELECT count(*) FROM desk_feed_state WHERE site_code = 'DMO'",
        Statement.IntakeRows => """
            SELECT (SELECT count(*) FROM flight_leg WHERE flight_key IN ('DM214-HLW-A', 'XR331-HLW-D')),
                   (SELECT count(*) FROM flight_leg WHERE feed = 'api-sim-aodb'),
                   (SELECT count(*) FROM flight_event e JOIN flight_leg l ON l.id = e.flight_leg_id WHERE l.flight_key = 'DM214-HLW-A'),
                   (SELECT count(*) FROM counter_allocation WHERE flight_key = 'XR331-HLW-D'),
                   (SELECT count(*) FROM border_desk_session WHERE feed = 'aman-kafka' AND site_code = 'DMO'),
                   (SELECT count(*) FROM border_desk_interval WHERE feed = 'aman-kafka'),
                   (SELECT count(*) FROM border_egate_interval WHERE feed = 'aman-kafka'),
                   (SELECT count(*) > 0 FROM inbound_lane_demand WHERE site_code = 'DMO'),
                   (SELECT count(*) > 0 FROM feed_freshness WHERE site_code = 'DMO')
            """,
        Statement.StaffingRows => "SELECT count(*) FROM lane_staffing WHERE zone_key LIKE 'DMO/%'",
        _ => throw new ArgumentOutOfRangeException(nameof(statement))
    };

    /// <summary>Runs a statement as the migration login (the fixture's superuser), its values bound as parameters.</summary>
    private async Task ExecuteAsync(string database, Statement statement, IReadOnlyDictionary<string, object> values)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(database));
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // every command text is a literal from Sql(Statement) above
        await using var command = new NpgsqlCommand(Sql(statement), connection);
#pragma warning restore CA2100
        foreach (var (name, value) in values)
            command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>The one row of a statement as the migration login, each value as invariant text (null as "null").</summary>
    private async Task<List<string>> RowAsync(string database, Statement statement, IReadOnlyDictionary<string, object> values = null)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(database));
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // every command text is a literal from Sql(Statement) above
        await using var command = new NpgsqlCommand(Sql(statement), connection);
#pragma warning restore CA2100
        foreach (var (name, value) in values ?? new Dictionary<string, object>())
            command.Parameters.AddWithValue(name, value);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).Should().BeTrue();
        return [.. Enumerable.Range(0, reader.FieldCount).Select(k => reader.IsDBNull(k) ? "null" : Convert.ToString(reader.GetValue(k), CultureInfo.InvariantCulture))];
    }

    /// <summary>The key ring in memory, as SecretReprotectionTests keeps it (no database row is needed for the round).</summary>
    private sealed class MemoryRepository : IXmlRepository
    {
        private readonly List<XElement> _elements = [];

        public IReadOnlyCollection<XElement> GetAllElements()
        {
            lock (_elements)
                return [.. _elements.Select(e => new XElement(e))];
        }

        public void StoreElement(XElement element, string friendlyName)
        {
            lock (_elements)
                _elements.Add(new XElement(element));
        }
    }

    /// <summary>A relay that takes every report and keeps it.</summary>
    private sealed class RecordingTransport : IEmailTransport
    {
        private readonly List<OutgoingEmail> _sent = [];

        public IReadOnlyList<OutgoingEmail> Sent
        {
            get
            {
                lock (_sent)
                    return [.. _sent];
            }
        }

        public Task<EmailBatch> SendAsync(IReadOnlyList<OutgoingEmail> emails, CancellationToken ct)
        {
            lock (_sent)
                _sent.AddRange(emails);
            return Task.FromResult(EmailBatch.Tried(new Dictionary<Guid, string>()));
        }
    }

    #endregion
}
