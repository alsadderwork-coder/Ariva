using System.Net;
using System.Text;
using System.Text.Json;
using Ariva.Infra.Sensing;
using Ariva.Simulation.Api.Emulators.Sensors;
using Ariva.Simulation.Api.Scenarios;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ariva.UnitTests.Simulation;

/// <summary>
/// ARV-028: the emulator's demo clock and its pushes. A 10-minute run at speed 60 plays exactly ten minutes in order,
/// authenticated with each device's credential, and Ingest's answers add up to what the emulator expected; pause,
/// speed, jump and a stop minute behave; S-17 is silent while offline; Ingest failures are counted, not fatal; the
/// credential never appears in the status.
/// </summary>
public sealed class SensorEmulatorTests
{
    #region Fakes

    private const string Credential15 = "ardk_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA1";
    private const string Credential17 = "ardk_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA2";
    private const string Credential50 = "ardk_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA3";

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 2, 14, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Monitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string name) => value;
        public IDisposable OnChange(Action<T, string> listener) => null;
    }

    /// <summary>Ingest stand-in: answers 202 with what Ingest's own mappers accept, or a fixed failure.</summary>
    private sealed class FakeIngest : HttpMessageHandler
    {
        public List<(string Path, string Credential, string Body)> Requests { get; } = [];
        public HttpStatusCode? FailWith { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            lock (Requests)
                Requests.Add((request.RequestUri!.AbsolutePath, request.Headers.Authorization?.Parameter, body));
            if (FailWith is { } status)
                return new HttpResponseMessage(status);
            using var document = JsonDocument.Parse(body);
            var mapped = request.RequestUri.AbsolutePath.EndsWith("/xovis", StringComparison.Ordinal)
                ? XovisPushMapper.Map(document.RootElement, new DevicePose(0, 0, 0), 3000)
                : CanonicalPushMapper.Map(document.RootElement, 3000);
            var accepted = mapped.Crossings.Count + mapped.Occupancy.Count + mapped.Intervals.Count + (mapped.Status is null ? 0 : 1);
            return new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent($"{{\"accepted\":{accepted}}}", Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("https://ingest.test/") };
    }

    private static (SensorEmulator Emulator, ManualTime Time, FakeIngest Ingest) Create(params (string Sensor, EmulatedDialect Dialect, string Credential)[] devices)
    {
        var time = new ManualTime();
        var ingest = new FakeIngest();
        var settings = new SensorEmulatorSettings
        {
            IngestUrl = "https://ingest.test",
            Devices = [.. devices.Select(d => new EmulatedDeviceSettings { Sensor = d.Sensor, Dialect = d.Dialect, Credential = d.Credential })]
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["Simulation:Seed"] = "9303" }).Build();
        var engine = new ScenarioEngine(configuration, NullLogger<ScenarioEngine>.Instance, time);
        var emulator = new SensorEmulator(engine, new Factory(ingest), new Monitor<SensorEmulatorSettings>(settings), time, NullLogger<SensorEmulator>.Instance);
        return (emulator, time, ingest);
    }

    private static async Task RunAsync(SensorEmulator emulator, ManualTime time, TimeSpan wall, TimeSpan step)
    {
        for (var t = TimeSpan.Zero; t < wall; t += step)
        {
            time.Now += step;
            await emulator.TickAsync(TestContext.Current.CancellationToken);
        }
    }

    #endregion

    [Fact]
    public async Task Start_Should_PlayTenMinutesInOrder_When_RunAtSpeed60UntilAStopMinute()
    {
        var (emulator, time, ingest) = Create(("S-15", EmulatedDialect.Canonical, Credential15), ("S-50", EmulatedDialect.Xovis, Credential50));

        emulator.Start(1090, 60, 1100, "test");
        await RunAsync(emulator, time, TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(250));

        var status = emulator.Status();
        status.Running.Should().BeFalse("the run pauses at its stop minute");
        status.NextMinute.Should().Be(1100);
        var visitors = status.Devices.Single(d => d.Sensor == "S-15");
        visitors.QueueZone.Should().Be("A-VIS");
        visitors.Role.Should().Be(SensorRole.QueueLead);
        visitors.Pushes.Should().Be(10);
        visitors.Failures.Should().Be(0);
        visitors.AcceptedEvents.Should().Be(visitors.ExpectedEvents).And.BeGreaterThan(20, "the Visitors wave crosses the lines");
        var checkIn = status.Devices.Single(d => d.Sensor == "S-50");
        checkIn.QueueZone.Should().Be("CI-C");
        checkIn.Pushes.Should().Be(10);
        checkIn.AcceptedEvents.Should().Be(checkIn.ExpectedEvents).And.Be(30, "two line intervals and the occupancy per minute");

        ingest.Requests.Where(r => r.Path.EndsWith("/events", StringComparison.Ordinal)).Should().HaveCount(10)
            .And.OnlyContain(r => r.Path == "/api/v1/ingest/zones/A-VIS/events" && r.Credential == Credential15);
        ingest.Requests.Where(r => r.Path.EndsWith("/xovis", StringComparison.Ordinal)).Should().HaveCount(10)
            .And.OnlyContain(r => r.Path == "/api/v1/ingest/zones/CI-C/xovis" && r.Credential == Credential50);
        var packages = ingest.Requests.Where(r => r.Credential == Credential15)
            .Select(r => JsonDocument.Parse(r.Body).RootElement.GetProperty("packageId").GetInt64()).ToList();
        packages.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Start_Should_KeepS17Silent_When_ItIsOfflineInTheScenario()
    {
        var (emulator, time, ingest) = Create(("S-17", EmulatedDialect.Canonical, Credential17));

        emulator.Start(1095, 60, 1115, "test");
        await RunAsync(emulator, time, TimeSpan.FromSeconds(25), TimeSpan.FromMilliseconds(500));

        emulator.Status().Devices.Single().Pushes.Should().Be(10, "1095 to 1099 and 1110 to 1114; nothing from 18:20 to 18:30");
        ingest.Requests.Should().HaveCount(10);
    }

    [Fact]
    public async Task Pause_Should_StopTheClock_When_Asked()
    {
        var (emulator, time, ingest) = Create(("S-15", EmulatedDialect.Canonical, Credential15));

        emulator.Start(600, 60, null, "test");
        await RunAsync(emulator, time, TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(250));
        emulator.Pause("test");
        var played = ingest.Requests.Count;
        await RunAsync(emulator, time, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1));

        played.Should().Be(3);
        ingest.Requests.Should().HaveCount(played, "a paused clock plays nothing");
        emulator.Status().Running.Should().BeFalse();

        emulator.Start(null, null, null, "test");
        await RunAsync(emulator, time, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(250));
        ingest.Requests.Should().HaveCount(5, "resuming continues where the clock stopped");
    }

    [Fact]
    public async Task SpeedAndJump_Should_MoveTheClock_When_Asked()
    {
        var (emulator, time, ingest) = Create(("S-15", EmulatedDialect.Canonical, Credential15));

        emulator.Start(600, 1, null, "test");
        await RunAsync(emulator, time, TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(5));
        ingest.Requests.Should().HaveCount(2, "speed 1 is one demo minute per wall minute");

        emulator.SetSpeed(30, "test");
        await RunAsync(emulator, time, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(500));
        ingest.Requests.Should().HaveCount(7, "speed 30 plays five more minutes in ten seconds");

        emulator.Jump(1200, "test");
        emulator.Status().NextMinute.Should().Be(1200);
        await RunAsync(emulator, time, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(500));
        var last = JsonDocument.Parse(ingest.Requests[^1].Body).RootElement.GetProperty("occupancy")[0].GetProperty("timeUtc").GetString();
        ingest.Requests.Should().HaveCount(8, "the jump skips the minutes in between");
        DateTime.Parse(last!, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal)
            .Should().BeOnOrBefore(time.Now.UtcDateTime, "events are never stamped in the future");
    }

    [Fact]
    public async Task Start_Should_KeepTheElapsedTime_When_ANewSpeedIsGivenWhileRunning()
    {
        var (emulator, time, _) = Create(("S-15", EmulatedDialect.Canonical, Credential15));

        emulator.Start(600, 1, null, "test");
        time.Now += TimeSpan.FromMinutes(10);
        emulator.Start(null, 60, null, "test");

        emulator.Status().Minute.Should().BeApproximately(610, 0.001, "ten wall minutes at speed 1 are ten demo minutes, whatever the new speed");
    }

    [Fact]
    public void Settings_Should_RefuseANonFiniteMaxSpeed_When_Validated()
    {
        var settings = new SensorEmulatorSettings { MaxSpeed = double.NaN };

        settings.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(settings)).Should().NotBeEmpty();
    }

    [Fact]
    public async Task Tick_Should_CountFailures_When_IngestRefuses()
    {
        var (emulator, time, ingest) = Create(("S-15", EmulatedDialect.Canonical, Credential15));
        ingest.FailWith = HttpStatusCode.ServiceUnavailable;

        emulator.Start(600, 60, 603, "test");
        await RunAsync(emulator, time, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(250));

        var device = emulator.Status().Devices.Single();
        device.Failures.Should().Be(3);
        device.AcceptedEvents.Should().Be(0);
        device.LastStatus.Should().Be(503);
        device.LastError.Should().StartWith("503");
    }

    [Fact]
    public void Status_Should_NeverShowACredential_When_Serialised()
    {
        var (emulator, _, _) = Create(("S-15", EmulatedDialect.Canonical, Credential15), ("S-17", EmulatedDialect.Xovis, Credential17));

        var json = JsonSerializer.Serialize(emulator.Status());

        json.Should().NotContain("ardk_").And.Contain("S-17");
    }

    [Theory]
    [InlineData("http://ingest.test", false, true)]
    [InlineData("http://api-ingest-service", true, false)]
    [InlineData("https://user:pass@ingest.test", false, true)]
    [InlineData("https://ingest.test/?x=1", false, true)]
    [InlineData("ftp://ingest.test", false, true)]
    [InlineData("https://ingest.test", false, false)]
    [InlineData("http://ingest.example.com", true, true)]
    [InlineData("http://localhost:51002", true, false)]
    [InlineData("http://api-ingest-service.ariva-dev.svc.cluster.local", true, false)]
    public void Settings_Should_RefuseInsecureOrOddIngestAddresses_When_Validated(string url, bool allowInsecure, bool refused)
    {
        var settings = new SensorEmulatorSettings { IngestUrl = url, AllowInsecureTransport = allowInsecure };

        settings.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(settings)).Any().Should().Be(refused);
    }

    [Fact]
    public void DeviceProblems_Should_NameEveryMistakeWithoutTheCredential_When_TheListIsWrong()
    {
        var problems = SensorEmulatorSettings.DeviceProblems(
        [
            new EmulatedDeviceSettings { Sensor = "S-99", Credential = Credential15 },
            new EmulatedDeviceSettings { Sensor = "S-15", Credential = "ardk_short" },
            new EmulatedDeviceSettings { Sensor = "S-15", Credential = Credential15 },
            new EmulatedDeviceSettings { Sensor = "S-16", Credential = Credential17, Dialect = (EmulatedDialect)9 }
        ]).ToList();

        problems.Should().HaveCount(5);
        string.Join(" ", problems).Should().NotContain("ardk_A").And.NotContain("ardk_short");
    }
}
