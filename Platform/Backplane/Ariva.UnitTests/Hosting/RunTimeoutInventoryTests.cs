using System.Diagnostics;
using System.Reflection;
using Ariva.Infra.NHibernate;
using Ariva.Infra.Resilience;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TickerQ.Utilities.Base;

namespace Ariva.UnitTests.Hosting;

/// <summary>
/// ARV-096e (CWE-400, ASVS 5.0 V15.2.2): every background loop a host runs, and every TickerQ job, ends each run at its run
/// timeout (<see cref="IBoundedRuns"/>, <see cref="RunBound"/>), or is named here with why it needs none. Each bounded loop and
/// job is then run against a database (and a malware scanner) that accepts connections and never answers: the run ends at the
/// timeout set for the test with <see cref="RunTimeoutException"/>, so a stalled dependency frees the loop for its next run.
/// Npgsql does not take the token while it opens a connection, so against a server that never answers the connection timeout
/// (2 s here) ends the wait. That a statement waiting on the server is cancelled at the run timeout is proven for the AMAN and
/// ACRIS polls (integration <c>OutboundEndpointTests</c>); ARV-235 proves it for the other loops and jobs.
/// </summary>
public sealed class RunTimeoutInventoryTests
{
    #region Inventory

    /// <summary>The hosted services that are not loops of runs, or bound their runs another way, and why.</summary>
    private static readonly IReadOnlyDictionary<string, string> Exempt = new Dictionary<string, string>
    {
        ["Ariva.Infra.Messaging.Outbox.OutboxRelay"] = "a pass's sends end within Kafka:Outbox:PassSeconds (ARV-166) and its statements within the database command timeout",
        ["Ariva.Infra.Streaming.QueueStreamWorker"] = "the stream engine's consume loop: each record is bounded by the engine's merge bounds and each read and write by the database command timeout (ARV-230 reworks its checkpoint)",
        ["Ariva.Api.Main.Hubs.LiveRelay"] = "renews a Redis lease every 5 s, each call within the Redis client's own timeout; the snapshots it forwards are callbacks",
        ["Ariva.Infra.Messaging.Kafka.KafkaConsumerEndpoints"] = "connects the consumers at start within Kafka:Consumers:StartWaitSeconds and watches for missing topics with metadata requests within the admin client's timeout",
        ["Ariva.Infra.Messaging.Kafka.TopicProvisioner"] = "creates the topics at start, each admin request within its own timeout",
        ["Ariva.Infra.Messaging.Kafka.KafkaProducerLifetime"] = "flushes the producer at stop within KafkaProduce.FlushTimeout",
        ["Ariva.Infra.Messaging.Kafka.KafkaEndpointsLifetime"] = "stops the endpoints at stop within KafkaEndpoints.StopTimeout",
        ["Ariva.Infra.Monitoring.CertificateMetrics"] = "reads the host's certificate files for a gauge; no database or network",
        ["Ariva.Infra.Timescale.DevelopmentMigrationService"] = "vm-local only: migrates at start, its scripts without a timeout by design (as the migration job)",
        ["Ariva.Infra.Timescale.SchemaVersionGate"] = "one read at start within the database command timeout",
        ["Ariva.Infra.Timescale.ValidationReaderGuard"] = "checks the validation reader's settings at start",
        ["Ariva.Infra.Storage.UploadScanStartNotice"] = "writes one line at start",
        ["Ariva.Infra.Security.DevelopmentUserSeed"] = "vm-local only: seeds the development accounts at start",
        ["Ariva.Infra.Services.Seed.DemoSeedService"] = "demo only: seeds the demo topology once at start, a bounded number of attempts",
        ["Ariva.Api.Ingest.Mqtt.MqttDeviceBroker"] = "a server: each device connection is bounded by the broker's keep-alive and limits (ARV-024)",
        ["Ariva.Api.Common.Hubs.HubSessionSweeper"] = "session code under the owner's sign-in freeze (2026-10): left as it was until the freeze lifts; a pass checks the replica's open sessions through the session cache, each within the database command timeout"
    };

    /// <summary>The loops each host must run with a run timeout (with the optional ones turned on below).</summary>
    private static readonly IReadOnlyDictionary<string, string[]> BoundedLoops = new Dictionary<string, string[]>
    {
        [ArivaHosts.Main] = ["DeviceHeartbeatMonitor", "ValidationResultsFreezer", "ScannerSignatureMonitor"],
        [ArivaHosts.Stream] = ["AlertEvaluationWorker", "DeskFeedWorker"],
        [ArivaHosts.Integration] = ["IdempotencySweeper", "AcrisPoller", "AmanPoller", "FeedFreshnessMonitor", "EmailSenderWorker"],
        [ArivaHosts.Ingest] = [],
        [ArivaHosts.Cronz] = []
    };

    public static TheoryData<string> Hosts => [.. BoundedLoops.Keys];

    private static bool IsAriva(Type type) => type.Assembly.GetName().Name?.StartsWith("Ariva.", StringComparison.Ordinal) == true;

    /// <summary>Turns on the loops a test host leaves off by default: the scanner's signature check needs a scanner named.</summary>
    private static Action<IWebHostBuilder> WithOptionalLoops(int scannerPort) => builder =>
    {
        builder.UseSetting("MalwareScan:Host", "127.0.0.1");
        builder.UseSetting("MalwareScan:Port", scannerPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
    };

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task HostedServices_Should_EachEndAtARunTimeoutOrBeExemptWithAReason_When_TheHostStarts(string host)
    {
        var expected = BoundedLoops[host];
        await using var app = ArivaHosts.Create(host, configure: WithOptionalLoops(1));

        var hosted = app.Services.GetServices<IHostedService>().Where(s => IsAriva(s.GetType())).ToList();

        hosted.Where(s => s is not IBoundedRuns).Select(s => s.GetType().FullName).Should().OnlyContain(name => Exempt.ContainsKey(name),
            $"every loop {host} runs ends its runs at a run timeout or is named in Exempt with its reason");
        hosted.OfType<IBoundedRuns>().Select(s => s.GetType().Name).Should().BeEquivalentTo(expected, $"the loops {host} runs with a run timeout");
        foreach (var loop in hosted.OfType<IBoundedRuns>())
            loop.RunTimeout.Should().BeGreaterThanOrEqualTo(RunBound.MinRunTimeout).And.BeLessThanOrEqualTo(RunBound.MaxRunTimeout, loop.GetType().Name);
    }

    /// <summary>The assemblies that hold Ariva's hosted services and jobs.</summary>
    private static readonly Assembly[] ArivaAssemblies =
    [
        typeof(Ariva.Infra._IAssemblyMark).Assembly, typeof(Ariva.Api.Common.Hubs.HubSessionSweeper).Assembly, typeof(Ariva.Api.Main._IAssemblyMark).Assembly,
        typeof(Ariva.Api.Ingest._IAssemblyMark).Assembly, typeof(Ariva.Api.Stream._IAssemblyMark).Assembly, typeof(Ariva.Api.Cronz._IAssemblyMark).Assembly,
        typeof(Ariva.Api.Integration._IAssemblyMark).Assembly
    ];

    private static Type ArivaType(Func<Type, bool> match) => ArivaAssemblies.SelectMany(a => a.GetTypes()).Single(match);

    [Fact]
    public void Exempt_Should_NameOnlyHostedServicesWithoutARunTimeout_When_Listed()
    {
        foreach (var (name, reason) in Exempt)
        {
            var type = ArivaType(t => t.FullName == name);
            typeof(IHostedService).IsAssignableFrom(type).Should().BeTrue(name);
            typeof(IBoundedRuns).IsAssignableFrom(type).Should().BeFalse($"{name} has a run timeout, so it needs no exemption");
            reason.Should().NotBeNullOrWhiteSpace();
        }
    }

    /// <summary>Every TickerQ function of Ariva.Api.Cronz, by its class.</summary>
    public static TheoryData<string> Jobs => [.. JobTypes().Select(t => t.Name)];

    private static IEnumerable<Type> JobTypes() => typeof(Ariva.Api.Cronz._IAssemblyMark).Assembly.GetTypes()
        .Where(t => t.GetMethods().Any(m => m.GetCustomAttribute<TickerFunctionAttribute>() is not null)).OrderBy(t => t.Name, StringComparer.Ordinal);

    [Fact]
    public async Task Jobs_Should_EachEndAtARunTimeout_When_Scheduled()
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Cronz);
        await using var scope = app.Services.CreateAsyncScope();

        JobTypes().Select(t => t.Name).Should().BeEquivalentTo(["AvailabilityJobs", "ReportJobs", "RetentionJobs", "SecretJobs", "StaffingJobs"]);
        foreach (var type in JobTypes())
        {
            var job = ActivatorUtilities.CreateInstance(scope.ServiceProvider, type);
            job.Should().BeAssignableTo<IBoundedRuns>(type.Name).Which.RunTimeout.Should().BeGreaterThanOrEqualTo(RunBound.MinRunTimeout).And.BeLessThanOrEqualTo(RunBound.MaxRunTimeout, type.Name);
        }
    }

    #endregion

    #region Stalled dependencies

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The stalled host's connection timeout (<c>Database:ConnectTimeoutSeconds</c>, 15 s in the hosts): Npgsql's open does not take
    /// a run's token while a server that accepted the connection holds its startup (measured on Npgsql 10.0.0, ARV-242), so such a
    /// server holds a run until this ends the open. Every open is asynchronous since ARV-242 (NHibernate's before a transaction
    /// begins too), where this timeout always ends it; NHibernate's synchronous open could wait for ever when less than a
    /// millisecond of it was left for a read (a story gate's two hours). A run here ends by its timeout or, when its open started
    /// late, at most one connection timeout past it; in the hosts every run timeout is at least the longest connection timeout a
    /// host may set (<see cref="RunBound.MinRunTimeout"/>), so a run whose first step is the open fails within its own timeout.
    /// </summary>
    private const int ConnectTimeoutSeconds = 2;

    /// <summary>The longest a run may take here: its timeout, the connection timeout and a margin for a busy machine.</summary>
    private static readonly TimeSpan Promptly = TimeSpan.FromSeconds(ConnectTimeoutSeconds + 6);

    /// <summary>How long the silent server's accept loop has to count a connection the run made (ARV-169's story gate).</summary>
    private static readonly TimeSpan AcceptWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A host whose database and malware scanner are <paramref name="silent"/>, without its hosted services (the loops are built
    /// by the test), so it starts without touching either.
    /// </summary>
    private static IArivaHost Stalled(string host, SilentServer silent) => ArivaHosts.Create(host, configure: builder =>
    {
        WithOptionalLoops(silent.Port)(builder);
        builder.UseSetting("Database:Host", "127.0.0.1");
        builder.UseSetting("Database:Port", silent.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("Database:ConnectTimeoutSeconds", ConnectTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.ConfigureTestServices(services =>
        {
            foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService) &&
                         d.ImplementationType?.FullName != "Microsoft.AspNetCore.Hosting.GenericWebHostService").ToList())
                services.Remove(descriptor);
        });
    });

    private static void ShortenRunTimeout(object instance) =>
        instance.GetType().GetProperty(nameof(IBoundedRuns.RunTimeout))!.SetValue(instance, TestTimeout);

    /// <summary>
    /// Builds the host's NHibernate session factory before a run is timed (it never connects: keywords are off), so the run spends
    /// its second at the stalled server. Built lazily inside the run, the mappings took the whole second on a busy machine and the
    /// validation results freeze was cut before it reached the server (seen in a story gate, and two runs in three under load).
    /// </summary>
    private static void BuildSessionFactory(IArivaHost app) => _ = app.Services.GetService<NHibernateSessionFactoryProvider>()?.SessionFactory;

    /// <summary>Every bounded loop of every host.</summary>
    public static TheoryData<string, string> Loops
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var (host, loops) in BoundedLoops)
            {
                foreach (var loop in loops)
                    data.Add(host, loop);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Loops))]
    public async Task Loop_Should_EndItsRunAtTheRunTimeout_When_ItsDatabaseOrScannerStalls(string host, string loop)
    {
        await using var silent = new SilentServer();
        await using var app = Stalled(host, silent);
        var type = ArivaType(t => t.Name == loop && typeof(IBoundedRuns).IsAssignableFrom(t));
        var instance = ActivatorUtilities.CreateInstance(app.Services, type);
        ShortenRunTimeout(instance);
        BuildSessionFactory(app);
        var runOnce = type.GetMethod("RunOnceAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, [typeof(CancellationToken)]);
        runOnce.Should().NotBeNull($"{loop} runs each run through RunOnceAsync, the run its loop makes");
        var watch = Stopwatch.StartNew();

        var run = () => (Task)runOnce.Invoke(instance, [TestContext.Current.CancellationToken])!;

        (await run.Should().ThrowAsync<RunTimeoutException>($"{loop} waits on a stalled dependency")).Which.Timeout.Should().Be(TestTimeout);
        watch.Elapsed.Should().BeGreaterThanOrEqualTo(TestTimeout - TimeSpan.FromMilliseconds(20)).And.BeLessThan(TestTimeout + Promptly);
        // The server counts a connection when its accept loop runs on the thread pool, after the kernel took it: on a loaded
        // machine that can come after the run's timeout (one failure in a story gate run right after a container restart).
        var counted = Stopwatch.StartNew();
        while (silent.Accepted == 0 && counted.Elapsed < AcceptWait)
            await Task.Delay(50, TestContext.Current.CancellationToken);
        silent.Accepted.Should().BePositive($"{loop} reached the stalled database or scanner (none counted within {AcceptWait.TotalSeconds:F0} s of the run's end: the run timed out before it connected)");
    }

    [Theory]
    [MemberData(nameof(Jobs))]
    public async Task Job_Should_EndItsRunAtTheRunTimeout_When_ItsDatabaseStalls(string job)
    {
        await using var silent = new SilentServer();
        await using var app = Stalled(ArivaHosts.Cronz, silent);
        await using var scope = app.Services.CreateAsyncScope();
        var type = JobTypes().Single(t => t.Name == job);
        var instance = ActivatorUtilities.CreateInstance(scope.ServiceProvider, type);
        ShortenRunTimeout(instance);
        BuildSessionFactory(app);
        var function = type.GetMethods().Single(m => m.GetCustomAttribute<TickerFunctionAttribute>() is not null);
        var watch = Stopwatch.StartNew();

        var run = () => (Task)function.Invoke(instance, [new TickerFunctionContext(), TestContext.Current.CancellationToken])!;

        (await run.Should().ThrowAsync<RunTimeoutException>($"{job} waits on a stalled database")).Which.Timeout.Should().Be(TestTimeout);
        watch.Elapsed.Should().BeGreaterThanOrEqualTo(TestTimeout - TimeSpan.FromMilliseconds(20)).And.BeLessThan(TestTimeout + Promptly);
        silent.Accepted.Should().BePositive($"{job} reached the stalled database");
    }

    #endregion
}
