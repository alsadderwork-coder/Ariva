using System.Globalization;
using System.Text.Json;
using Ariva.Core.Domain.Entities;
using Ariva.Simulation.Api.Emulators.Sensors;
using Ariva.Simulation.Api.Emulators.Validation;
using Ariva.Simulation.Api.Scenarios.Engine;
using FluentAssertions;

namespace Ariva.UnitTests.Simulation;

/// <summary>
/// ARV-104i: the scenario's ground truth and what the emulated validation observers report from it. The truth is the sensor
/// emulator's own people (a line's true count is the crossings the sensors push, a minute's true wait is what pairing their
/// tracks gives), desk states are what an observer at the desk sees, S-17's outage is laid on the day; the error injection is
/// systematic and the slips are drawn from the seed (table-driven, deterministic); the plan maps a campaign's lines, zones
/// and desks by the demo profile's names, leaves out what the scenario does not know, and is the same every time.
/// </summary>
public sealed class ValidationEmulatorTests
{
    private static readonly Lazy<ScenarioDay> Day = new(() => ScenarioDay.Run(ScenarioConfig.Reference() with { Lite = true }));

    /// <summary>The scenario's 00:00 laid on 2026-10-09 in Dubai (UTC+4).</summary>
    private static readonly DateTime DayStart = new(2026, 10, 8, 20, 0, 0, DateTimeKind.Utc);

    private static ValidationTruth Truth(DateTime? dayStart = null) => new(Day.Value, dayStart ?? DayStart);

    private static DateTime At(int minute) => DayStart.AddMinutes(minute);

    #region Truth

    /// <summary>The crossings the sensor emulator pushes for a queue's lead sensor over [from, to), laid on <see cref="DayStart"/> at speed 1.</summary>
    private static List<(string Line, string Track, DateTime Utc)> Pushed(string queue, int fromMinute, int toMinute)
    {
        var lead = Day.Value.Site.Sensors.Single(s => s.Role == SensorRole.QueueLead && s.QueueZone == queue);
        var crossings = new List<(string, string, DateTime)>();
        for (var minute = fromMinute; minute < toMinute; minute++)
        {
            var push = SensorTraffic.Build(Day.Value, lead, EmulatedDialect.Canonical, minute, minute, m => DayStart.AddMinutes(m), DayStart);
            if (push is null)
                continue;
            using var document = JsonDocument.Parse(push.Json);
            if (!document.RootElement.TryGetProperty("crossings", out var list))
                continue;
            foreach (var c in list.EnumerateArray())
            {
                crossings.Add((c.GetProperty("lineName").GetString(), c.GetProperty("trackId").GetString(),
                    DateTime.Parse(c.GetProperty("timeUtc").GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)));
            }
        }

        return crossings;
    }

    [Theory]
    [InlineData("A-VIS")]
    [InlineData("A-RES")]
    [InlineData("A-CIT")]
    [InlineData("D-VIS")]
    public void Entries_Should_EqualTheSensorEmulatorsCrossingsPerBin_When_TheEveningIsCounted(string queue)
    {
        var truth = Truth();
        var q = truth.QueueOf(queue)!.Value;
        var pushed = Pushed(queue, 1075, 1205);

        for (var bin = 1080; bin < 1200; bin += 15)
        {
            var (from, to) = (At(bin), At(bin + 15));
            truth.Entries(q, from, to).Count.Should().Be(pushed.Count(c => c.Line == queue + " entry" && c.Utc >= from && c.Utc < to), "bin {0} of {1}", bin, queue);
            truth.Exits(q, from, to).Count.Should().Be(pushed.Count(c => c.Line == queue + " exit" && c.Utc >= from && c.Utc < to), "bin {0} of {1}", bin, queue);
        }

        truth.Entries(q, At(1080), At(1200)).Should().NotBeEmpty("people arrive at {0} in the evening", queue);
    }

    [Theory]
    [InlineData("A-RES", 1080, 1110, 1200)]
    [InlineData("A-VIS", 1080, 1100, 1300)]
    public void EntrantWaits_Should_EqualTheWaitsPairedFromTheEmulatorsTracks_When_EveryEntrantHasLeft(string queue, int from, int to, int horizon)
    {
        var truth = Truth();
        var q = truth.QueueOf(queue)!.Value;
        var pushed = Pushed(queue, from - 2, horizon);
        var exits = pushed.Where(c => c.Line == queue + " exit").ToDictionary(c => c.Track, c => c.Utc, StringComparer.Ordinal);

        for (var minute = from; minute < to; minute++)
        {
            var start = At(minute);
            var entrants = pushed.Where(c => c.Line == queue + " entry" && c.Utc >= start && c.Utc < start.AddMinutes(1)).ToList();
            // Ariva's queue engine gives no wait to a pair whose exit is stamped before its entry (ARV-031).
            var waits = entrants.Where(e => exits.ContainsKey(e.Track) && exits[e.Track] >= e.Utc).Select(e => (exits[e.Track] - e.Utc).TotalMinutes).ToList();

            var expected = truth.EntrantWaits(q, start);

            expected.Entries.Should().Be(entrants.Count, "minute {0}", minute);
            expected.Waits.Should().Be(waits.Count, "minute {0}", minute);
            if (waits.Count == 0)
                expected.MeanWaitMinutes.Should().BeNull();
            else
                expected.MeanWaitMinutes.Should().BeApproximately(waits.Average(), 1e-4, "minute {0}", minute);
        }
    }

    [Theory]
    [InlineData("serving", false, false, ObservedDeskState.Serving)]
    [InlineData("idle", false, true, ObservedDeskState.Idle)]
    [InlineData("paused", true, true, ObservedDeskState.Paused)]
    [InlineData("closed", false, true, ObservedDeskState.Closed)]
    [InlineData("oos", true, false, ObservedDeskState.Closed)]
    [InlineData("unknown", true, true, ObservedDeskState.Paused)]
    [InlineData("unknown", false, true, ObservedDeskState.Serving)]
    [InlineData("unknown", false, false, ObservedDeskState.Idle)]
    public void Observed_Should_BeWhatAnObserverAtTheDeskSees_When_TheScenarioStateIs(string scenarioState, bool pausing, bool peopleQueue, ObservedDeskState expected) =>
        ValidationTruth.Observed(scenarioState, pausing, peopleQueue).Should().Be(expected);

    [Fact]
    public void DeskState_Should_FollowTheScenariosServers_When_ReadMinuteByMinute()
    {
        var truth = Truth();
        var q = truth.QueueOf("A-VIS")!.Value;
        var seen = new HashSet<ObservedDeskState>();
        for (var minute = 1080; minute < 1170; minute++)
        {
            var servers = Day.Value.ServerStates(q, minute);
            for (var k = 0; k < servers.Count; k++)
            {
                var state = truth.DeskState(q, k, At(minute));
                var i = minute + ScenarioModel.Pre;
                state.Should().Be(ValidationTruth.Observed(servers[k].State, Day.Value.Servers[q][k].Pause[i] != 0, Day.Value.L[q][i] > 0.5));
                seen.Add(state!.Value);
            }
        }

        seen.Should().Contain([ObservedDeskState.Serving, ObservedDeskState.Closed], "the Visitors desks serve and some stay closed in the evening");
        truth.DeskOf("AR-08").Should().Be((q, 0));
        truth.DeskOf("AR-22").Should().Be((q, 14));
        truth.DeskOf("ZZ-99").Should().BeNull();
        truth.DeskState(q, 0, At(-5)).Should().BeNull("outside the scenario day nobody observes");
    }

    [Fact]
    public void Outages_Should_LayS17OnTheDay_When_TheWindowCoversTheEvening()
    {
        var truth = Truth();

        truth.Outages(At(1080), At(1170)).Should().Equal(new TruthOutage("S-17", "A-VIS", "A-VIS", At(1100), At(1110)));
        truth.Outages(At(1110), At(1170)).Should().BeEmpty("S-17 is back at 18:30");
    }

    [Fact]
    public void At_Should_LayClockMinutesOnTheDayStartToTheMillisecond_When_TheStartIsNotOnAWholeMinute()
    {
        var start = DayStart.AddSeconds(37.25);
        var truth = Truth(start);

        truth.At(1080).Should().Be(start.AddMinutes(1080));
        truth.At(1080.5).Should().Be(start.AddMinutes(1080).AddSeconds(30));
        truth.At(1080.0001).Should().Be(start.AddMinutes(1080).AddMilliseconds(6), "6 ms of a minute, cut to the whole millisecond");
        truth.ClockOf(start.AddMinutes(1081)).Should().BeApproximately(1081, 1e-9);
        RehearsalPlanner.BinsWithin(truth.At(1080), truth.At(1110)).Should().Equal([At(1095)], "only a whole quarter hour inside the window is counted");
        RehearsalPlanner.BinsWithin(At(1080), At(1110)).Should().Equal(At(1080), At(1095));
    }

    #endregion

    #region Settings

    /// <summary>
    /// CWE-918: the observers reach only the Ariva.Api.Main of the simulator's own deployment: loopback (vm-local, the AppHost, E2E),
    /// the chart's api-main-service alone (resolved in the pod's own namespace), or with the pod's own namespace (from the downward
    /// API) and .svc or .svc.cluster.local. Every other name, namespace or address is refused, metadata and private addresses too.
    /// </summary>
    [Theory]
    [InlineData("http://localhost:51001", true, null, true)]
    [InlineData("http://[::1]:51001", true, null, true)]
    [InlineData("https://127.0.0.1:51001", false, null, true)]
    [InlineData("http://api-main-service", true, null, true)]
    [InlineData("http://api-main-service", true, "ariva-dev", true)]
    [InlineData("https://api-main-service.ariva-dev.svc.cluster.local", false, "ariva-dev", true)]
    [InlineData("http://api-main-service.ariva-dev.svc", true, "ariva-dev", true)]
    [InlineData("https://API-MAIN-SERVICE.Ariva-Dev.svc.cluster.local", false, "ariva-dev", true)]
    [InlineData("https://api-main-service.ariva-dev.svc.cluster.local", false, null, false)]
    [InlineData("https://api-main-service.ariva-prd.svc.cluster.local", false, "ariva-dev", false)]
    [InlineData("http://api-main-service.ariva-prd.svc", true, "ariva-dev", false)]
    [InlineData("http://api-main-service.ariva-dev", true, "ariva-dev", false)]
    [InlineData("https://api-main-service.ariva-dev.svc.cluster.local.", false, "ariva-dev", false)]
    [InlineData("https://api-main-service.ariva-dev.svc.cluster.local", false, "Not_A_Namespace", false)]
    [InlineData("http://api-integration-service", true, "ariva-dev", false)]
    [InlineData("http://metadata", true, "ariva-dev", false)]
    [InlineData("http://169.254.169.254", true, null, false)]
    [InlineData("http://10.0.0.5", true, null, false)]
    [InlineData("http://[fd00::1]", true, null, false)]
    [InlineData("http://api-main-service", false, null, false)]
    [InlineData("https://api-main-prd-ariva.example.com", false, null, false)]
    [InlineData("https://ariva.example.com", true, null, false)]
    [InlineData("https://user:secret@api-main-service", false, null, false)]
    [InlineData("https://api-main-service/?next=evil", false, null, false)]
    [InlineData("ftp://api-main-service", false, null, false)]
    public void MainUrl_Should_BeTheAddressOfTheSimulatorsOwnDeployment_When_Checked(string mainUrl, bool allowInsecure, string ownNamespace, bool valid)
    {
        var settings = new Ariva.Simulation.Api.Emulators.ArivaTargetSettings { MainUrl = mainUrl, AllowInsecureTransport = allowInsecure, OwnNamespace = ownNamespace };

        var problems = settings.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(settings)).ToList();

        problems.Should().HaveCount(valid ? 0 : 1);
        problems.Should().NotContain(p => p.ErrorMessage!.Contains("secret", StringComparison.Ordinal), "a configured value is never quoted");
    }

    [Fact]
    public void Observers_Should_BeRefusedWithoutQuotingThem_When_TheyCannotBeAccounts()
    {
        var good = new ObserverCredentials { UserName = "sim.observer1", Password = "observer-one-Pass-0a1b2c3d", TotpSecret = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP" };

        ObserverCredentials.ListProblems([good], "observer", allowEmpty: false).Should().BeEmpty();
        new ValidationEmulatorSettings().Validate(new System.ComponentModel.DataAnnotations.ValidationContext(new object())).Should().BeEmpty("no observer is configured by default");
        foreach (var bad in new[]
                 {
                     new ObserverCredentials { UserName = "leaked\tuser", Password = "leaked-password" },
                     new ObserverCredentials { UserName = "leaked.user", Password = "leaked\npassword" },
                     new ObserverCredentials { UserName = new string('l', 257), Password = "leaked-password" },
                     new ObserverCredentials { UserName = "leaked.user", Password = new string('l', 513) },
                     new ObserverCredentials { UserName = "leaked.user", Password = "leaked-password", TotpSecret = "leaked!" },
                     null
                 })
        {
            var problems = ObserverCredentials.ListProblems([bad], "observer", allowEmpty: false).ToList();
            problems.Should().NotBeEmpty();
            problems.Should().NotContain(p => p.Contains("leaked", StringComparison.Ordinal) || p.Contains("lll", StringComparison.Ordinal));
        }

        ObserverCredentials.ListProblems([good, good], "observer", allowEmpty: false).Should().ContainSingle("one account per observer");
        var sameAccount = new ObserverCredentials { UserName = " SIM.Observer1 ", Password = "observer-one-Pass-0a1b2c3d" };
        var fullWidth = new ObserverCredentials { UserName = "\uFF53\uFF49\uFF4D.observer1", Password = "observer-one-Pass-0a1b2c3d" };
        ObserverCredentials.ListProblems([good, sameAccount], "observer", allowEmpty: false).Should().ContainSingle("Ariva matches user names normalised: one account");
        ObserverCredentials.ListProblems([good, fullWidth], "observer", allowEmpty: false).Should().ContainSingle("NFKC makes the full-width name the same account");
        ObserverCredentials.ListProblems([new ObserverCredentials { UserName = "leaked\uD800user", Password = "leaked-password" }], "observer", allowEmpty: false)
            .Should().ContainSingle().Which.Should().NotContain("leaked", "a lone surrogate is not text, and never quoted");
        ObserverCredentials.ListProblems([.. Enumerable.Repeat(good, 9)], "observer", allowEmpty: false).Should().ContainSingle("at most 8 observers");
        ObserverCredentials.ListProblems([], "observer", allowEmpty: false).Should().ContainSingle();
    }

    #endregion

    #region Sign-in

    [Theory]
    [InlineData(" sim.observer1", "sim.observer1")]
    [InlineData("SIM.Observer1", "sim.observer1")]
    [InlineData("\uFF53\uFF49\uFF4D.observer1", "sim.observer1")]
    [InlineData("sim.observer1", "sim.observer1")]
    [InlineData("bad\uD800name", "bad\uD800name")]
    [InlineData(null, null)]
    public void Normalize_Should_MatchUserNamesAsArivaDoes_When_Given(string userName, string expected) =>
        ObserverCredentials.Normalize(userName).Should().Be(expected);

    /// <summary>Ariva.Api.Main's sign-in stand-in: records each sign-in body and answers a token.</summary>
    private sealed class FakeLogin : HttpMessageHandler
    {
        public List<JsonElement> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone());
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"accessToken\":\"tok-0a1b2c3d4e5f6a7b\",\"tokenType\":\"Bearer\",\"expiresIn\":900,\"scope\":null}", System.Text.Encoding.UTF8,
                    "application/json")
            };
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task SignIn_Should_UseANewerTotpStep_When_TheSameAccountSignsInAgainWithinAStep()
    {
        const string seed = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";
        var credentials = new ObserverCredentials { UserName = "sim.observer1", Password = "observer-one-Pass-0a1b2c3d", TotpSecret = seed };
        var steps = new System.Collections.Concurrent.ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        // 50 ms before a step ends: the second sign-in waits for the next step (a real 150 ms) rather than reuse this one.
        var step = Ariva.Simulation.Api.Emulators.Integration.Totp.Step(DateTimeOffset.UtcNow) + 10;
        var clock = new FixedClock(DateTimeOffset.FromUnixTimeSeconds((step + 1) * Ariva.Simulation.Api.Emulators.Integration.Totp.StepSeconds).AddMilliseconds(-50));
        var ariva = new FakeLogin();
        HttpClient Http() => new(ariva, disposeHandler: false) { BaseAddress = new Uri("https://api-main-service/") };
        var first = new ArivaObserverClient(1, credentials, Http, clock, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, steps);
        // The replacement writes the same account's name otherwise: Ariva's replay guard is per account, so is the step kept.
        var respelled = new ObserverCredentials { UserName = "SIM.Observer1", Password = credentials.Password, TotpSecret = seed };
        var replaced = new ArivaObserverClient(1, respelled, Http, clock, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, steps);

        (await first.SignInAsync(TestContext.Current.CancellationToken)).Should().Be(ObserverSignIn.SignedIn);
        (await replaced.SignInAsync(TestContext.Current.CancellationToken)).Should().Be(ObserverSignIn.SignedIn);

        var key = Ariva.Simulation.Api.Emulators.Integration.Totp.FromBase32(seed);
        ariva.Bodies.Select(b => b.GetProperty("code").GetString()).Should().Equal(
            Ariva.Simulation.Api.Emulators.Integration.Totp.Code(key, step), Ariva.Simulation.Api.Emulators.Integration.Totp.Code(key, step + 1));
        ariva.Bodies.Select(b => b.GetProperty("userName").GetString()).Should().Equal("sim.observer1", "SIM.Observer1");
        steps.Keys.Should().Equal("sim.observer1");
        steps["sim.observer1"].Should().Be(step + 1);
        first.SignedIn.Should().BeTrue();
    }

    /// <summary>Answers every sign-in with the body given.</summary>
    private sealed class FixedAnswer(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
    }

    [Theory]
    [InlineData("{\"accessToken\":\"tok-0a1b2c3d4e5f6a7b\",\"expiresIn\":900,\"scope\":null}", ObserverSignIn.SignedIn)]
    [InlineData("{\"accessToken\":\"eyJhbGciOiJFUzI1NiJ9.eyJzdWIiOiIxIn0.c2ln\",\"expiresIn\":900}", ObserverSignIn.SignedIn)]
    [InlineData("{\"accessToken\":\"tok-0a1b2c3d4e5f6a7b\",\"expiresIn\":900,\"scope\":\"pending\"}", ObserverSignIn.Pending)]
    [InlineData("{\"accessToken\":12345,\"expiresIn\":900}", ObserverSignIn.Unreachable)]
    [InlineData("{\"accessToken\":{\"value\":\"tok\"},\"expiresIn\":900}", ObserverSignIn.Unreachable)]
    [InlineData("{\"accessToken\":\"tok-0a1b2c3d4e5f6a7b\",\"expiresIn\":\"900\"}", ObserverSignIn.Unreachable)]
    [InlineData("{\"accessToken\":\"tok-0a1b2c3d4e5f6a7b\",\"expiresIn\":1e100}", ObserverSignIn.Unreachable)]
    [InlineData("{\"accessToken\":\"tok-0a1b2c3d4e5f6a7b\",\"expiresIn\":-5}", ObserverSignIn.Unreachable)]
    [InlineData("{\"accessToken\":\"tok-0a1b2c3d4e5f6a7b\",\"expiresIn\":null}", ObserverSignIn.Unreachable)]
    [InlineData("{\"accessToken\":\"\",\"expiresIn\":900}", ObserverSignIn.Unreachable)]
    [InlineData("{\"accessToken\":\"tok\\r\\nX-Evil: 1\",\"expiresIn\":900}", ObserverSignIn.Unreachable)]
    [InlineData("{\"accessToken\":\"tok with spaces\",\"expiresIn\":900}", ObserverSignIn.Unreachable)]
    [InlineData("[\"tok-0a1b2c3d4e5f6a7b\",900]", ObserverSignIn.Unreachable)]
    [InlineData("\"tok-0a1b2c3d4e5f6a7b\"", ObserverSignIn.Unreachable)]
    [InlineData("{\"accessToken\":", ObserverSignIn.Unreachable)]
    public async Task SignIn_Should_TakeOnlyAUsableTokenAndNeverThrow_When_TheAnswerIsMalformed(string body, ObserverSignIn expected)
    {
        var credentials = new ObserverCredentials { UserName = "sim.observer2", Password = "observer-two-Pass-6a7b8c9d" };
        var ariva = new FixedAnswer(body);
        HttpClient Http() => new(ariva, disposeHandler: false) { BaseAddress = new Uri("https://api-main-service/") };
        var client = new ArivaObserverClient(2, credentials, Http, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var outcome = await client.SignInAsync(TestContext.Current.CancellationToken);

        outcome.Should().Be(expected);
        client.SignedIn.Should().Be(expected == ObserverSignIn.SignedIn);
    }

    #endregion

    #region Errors and slips

    [Theory]
    [InlineData(100, 0, 100)]
    [InlineData(100, 6, 106)]
    [InlineData(100, -6, 94)]
    [InlineData(250, 6, 265)]
    [InlineData(10, 6, 11)]
    [InlineData(8, 6, 8)]
    [InlineData(0, 6, 0)]
    [InlineData(3, -50, 2)]
    [InlineData(1, -50, 1)]
    [InlineData(5000, 50, 7500)]
    [InlineData(9000, 50, 10_000)]
    public void Count_Should_ScaleTheTruthAndRoundHalfAwayFromZero_When_ACountErrorIsInjected(int truth, double percent, int expected) =>
        ObserverSlips.Count(truth, percent).Should().Be(expected);

    [Theory]
    [InlineData(600_000, 0, 0, 600_000)]
    [InlineData(600_000, 2, 0, 720_000)]
    [InlineData(600_000, 0, 10, 660_000)]
    [InlineData(600_000, 1, 10, 720_000)]
    [InlineData(600_000, -2, 0, 480_000)]
    [InlineData(600_000, -30, 0, 1_000)]
    [InlineData(10_200_000, 30, 0, 10_800_000)]
    [InlineData(123_456, 0, -50, 61_728)]
    public void TracerWait_Should_ScaleAndShiftTheTrueWaitWithinARunsBounds_When_ATracerErrorIsInjected(int trueMs, double minutes, double percent, int expectedMs) =>
        ObserverSlips.TracerWait(TimeSpan.FromMilliseconds(trueMs), minutes, percent).Should().Be(TimeSpan.FromMilliseconds(expectedMs));

    [Fact]
    public void Missed_Should_DrawTheSameItemsFromTheSameSeed_When_SlipsAreInjected()
    {
        var keys = Enumerable.Range(0, 10_000).Select(i => "line-" + i.ToString(CultureInfo.InvariantCulture)).ToList();

        keys.Should().NotContain(k => ObserverSlips.Missed(9303, "count", k, 0));
        keys.Should().OnlyContain(k => ObserverSlips.Missed(9303, "count", k, 100));
        var quarter = keys.Where(k => ObserverSlips.Missed(9303, "count", k, 25)).ToList();
        quarter.Count.Should().BeInRange(2_300, 2_700, "a quarter of the items, drawn");
        keys.Where(k => ObserverSlips.Missed(9303, "count", k, 25)).Should().Equal(quarter, "the same seed misses the same items");
        keys.Where(k => ObserverSlips.Missed(7, "count", k, 25)).Should().NotEqual(quarter, "another seed misses others");
        keys.Where(k => ObserverSlips.Missed(9303, "desk-bin", k, 25)).Should().NotEqual(quarter, "each kind draws on its own");
    }

    [Theory]
    [InlineData(51, 0, 0, 0, 0)]
    [InlineData(0, 31, 0, 0, 0)]
    [InlineData(0, 0, -51, 0, 0)]
    [InlineData(0, 0, 0, 101, 0)]
    [InlineData(0, 0, 0, 0, -1)]
    [InlineData(double.NaN, 0, 0, 0, 0)]
    [InlineData(0, double.PositiveInfinity, 0, 0, 0)]
    public void Problems_Should_RefuseErrorsOutOfRange_When_Checked(double count, double tracerMinutes, double tracerPercent, double missedBins, double missedMinutes) =>
        new ObserverErrors(count, null, tracerMinutes, tracerPercent, missedBins, missedMinutes).Problems().Should().ContainSingle();

    [Fact]
    public void Problems_Should_RefuseLineNamesThatCannotBeLines_When_Checked()
    {
        ObserverErrors.None.Problems().Should().BeEmpty();
        new ObserverErrors(6, ["A-VIS entry"], 0, 0, 0, 0).Problems().Should().BeEmpty();
        new ObserverErrors(6, ["A-VIS\nentry"], 0, 0, 0, 0).Problems().Should().ContainSingle();
        new ObserverErrors(6, [new string('x', 201)], 0, 0, 0, 0).Problems().Should().ContainSingle();
        new ObserverErrors(6, [.. Enumerable.Repeat("A-VIS entry", 51)], 0, 0, 0, 0).Problems().Should().ContainSingle();
        new ObserverErrors(6, ["A-VIS entry"], 0, 0, 0, 0).CountErrorApplies("A-VIS entry").Should().BeTrue();
        new ObserverErrors(6, ["A-VIS entry"], 0, 0, 0, 0).CountErrorApplies("a-vis entry").Should().BeFalse("names match exactly");
        new ObserverErrors(6, null, 0, 0, 0, 0).CountErrorApplies("A-RES exit").Should().BeTrue("no line named: every line");
        ObserverErrors.None.CountErrorApplies("A-RES exit").Should().BeFalse();
    }

    /// <summary>The line names a rehearsal takes (and its report repeats): letters, digits, spaces and - _ . ( ) / only, so no markup is ever echoed.</summary>
    [Theory]
    [InlineData("A-VIS entry", true)]
    [InlineData("A-RES exit (east)", true)]
    [InlineData("Gate_3/Level.2", true)]
    [InlineData("خط الدخول 1", true)]
    [InlineData("<script>alert(1)</script>", false)]
    [InlineData("A-VIS entry\" onmouseover=\"alert(1)", false)]
    [InlineData("A-VIS entry'", false)]
    [InlineData("A-VIS&amp;entry", false)]
    [InlineData("A-VIS\tentry", false)]
    [InlineData("   ", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsLineName_Should_TakeOnlyPlainLineNames_When_Checked(string name, bool expected)
    {
        ObserverErrors.IsLineName(name).Should().Be(expected);
        new ObserverErrors(6, [name], 0, 0, 0, 0).Problems().Should().HaveCount(expected ? 0 : 1);
    }

    #endregion

    #region Plan

    private static readonly Guid VisEntry = Guid.Parse("0199a000-0000-7000-8000-00000000e001");
    private static readonly Guid VisExit = Guid.Parse("0199a000-0000-7000-8000-00000000e002");
    private static readonly Guid ResEntry = Guid.Parse("0199a000-0000-7000-8000-00000000e003");
    private static readonly Guid ResExit = Guid.Parse("0199a000-0000-7000-8000-00000000e004");
    private static readonly Guid BandEntry = Guid.Parse("0199a000-0000-7000-8000-00000000e005");
    private static readonly Guid Unknown = Guid.Parse("0199a000-0000-7000-8000-00000000e006");
    private static readonly Guid VisZone = Guid.Parse("0199a000-0000-7000-8000-00000000f001");
    private static readonly Guid ResZone = Guid.Parse("0199a000-0000-7000-8000-00000000f002");

    private static Guid DeskId(int n) => Guid.Parse(string.Create(CultureInfo.InvariantCulture, $"0199a000-0000-7000-8000-0000000d{n:0000}"));

    /// <summary>A campaign over DMO's v12 names: A-VIS and A-RES with their lines, the band's line, a line of a zone the scenario lacks, and desks.</summary>
    private static CaptureCampaign Campaign(bool desksIncluded = true, params string[] desks) => new(Guid.NewGuid(), "E2ER", "Rehearsal", "Asia/Dubai", ["2026-10-09"], 15,
        [
            new CaptureLine(VisEntry, "A-VIS entry", "Entry", "A-VIS"), new CaptureLine(VisExit, "A-VIS exit", "Exit", "A-VIS"),
            new CaptureLine(ResEntry, "A-RES entry", "Entry", "A-RES"), new CaptureLine(ResExit, "A-RES exit", "Exit", "A-RES"),
            new CaptureLine(BandEntry, "A-OV entry", "OverflowEntry", "A-VIS"), new CaptureLine(Unknown, "Z-9 entry", "Entry", "Z-9")
        ],
        [new CaptureZone(VisZone, "A-VIS"), new CaptureZone(ResZone, "A-RES"), new CaptureZone(Guid.NewGuid(), "Z-9")],
        desksIncluded,
        [.. desks.Select((code, i) => new CaptureDesk(DeskId(i), "IMM", code))],
        300);

    private static readonly RehearsalWindow Evening = new(1080, 1170);

    private static RehearsalPlan Plan(CaptureCampaign campaign, ObserverErrors errors = null, int observers = 1, IReadOnlyList<int> deskObservers = null, uint seed = 9303,
        int every = 10) =>
        RehearsalPlanner.Plan(Truth(), campaign, observers, deskObservers ?? [0], Evening, errors ?? ObserverErrors.None, seed, every);

    [Fact]
    public void Plan_Should_ReportTheTruthOnEveryLineAndWholeBin_When_NoErrorIsInjected()
    {
        var truth = Truth();
        var plan = Plan(Campaign());

        plan.Summary.Should().BeEquivalentTo(new PlanSummary(6, 2, 3, 1, 0, 0, 0, 0, 0, 0, 0));
        plan.Counts.Should().HaveCount(4 * 6, "four lines the scenario knows, six whole bins from 18:00 to 19:30");
        plan.Counts.Select(c => c.BinStartUtc).Distinct().Should().Equal(Enumerable.Range(0, 6).Select(b => At(1080 + (15 * b))));
        var vis = truth.QueueOf("A-VIS")!.Value;
        var res = truth.QueueOf("A-RES")!.Value;
        foreach (var count in plan.Counts)
        {
            var (from, to) = (count.BinStartUtc, count.BinStartUtc.AddMinutes(15));
            var (inside, outside) = count.LineId switch
            {
                var id when id == VisEntry => (truth.Entries(vis, from, to).Count, 0),
                var id when id == VisExit => (0, truth.Exits(vis, from, to).Count),
                var id when id == ResEntry => (truth.Entries(res, from, to).Count, 0),
                _ => (0, truth.Exits(res, from, to).Count)
            };
            (count.CrossingsIn, count.CrossingsOut).Should().Be((inside, outside));
            (count.TrueIn, count.TrueOut).Should().Be((inside, outside));
        }

        // The reference evening: the Visitors wave of 18:00 empties the hall by 18:30, and nobody enters until 19:00.
        plan.Counts.Where(c => c.LineId == VisEntry).Select(c => c.CrossingsIn > 0).Should().Equal(true, true, false, false, true, true);
        plan.Counts.Should().NotContain(c => c.LineId == BandEntry || c.LineId == Unknown, "the band's line and an unknown zone have no truth");
    }

    [Fact]
    public void Plan_Should_ApplyTheCountErrorToTheNamedLineOnly_When_SixPercentIsInjected()
    {
        var truthful = Plan(Campaign());
        var plan = Plan(Campaign(), new ObserverErrors(6, ["A-VIS entry"], 0, 0, 0, 0));

        plan.Summary.CountErrorLines.Should().Be(1);
        plan.Counts.Where(c => c.LineId == VisEntry).Should().OnlyContain(c => c.CrossingsIn == ObserverSlips.Count(c.TrueIn, 6) && c.CrossingsIn >= c.TrueIn)
            .And.Contain(c => c.CrossingsIn > c.TrueIn, "the Visitors bins hold enough people for 6 percent to show");
        plan.Counts.Where(c => c.LineId != VisEntry).Should().BeEquivalentTo(truthful.Counts.Where(c => c.LineId != VisEntry), o => o.WithStrictOrdering());
        Plan(Campaign(), new ObserverErrors(6, null, 0, 0, 0, 0)).Summary.CountErrorLines.Should().Be(4, "with no line named the error is on every line");
    }

    [Fact]
    public void Plan_Should_GiveLinesTracersAndDesksToTheObserversInTurn_When_ThereAreSeveral()
    {
        var plan = Plan(Campaign(true, "AR-08", "AR-09", "AR-05"), observers: 2, deskObservers: [0, 1]);

        // Lines in order of zone and name: A-RES entry, A-RES exit, A-VIS entry, A-VIS exit.
        plan.Counts.GroupBy(c => c.LineId).ToDictionary(g => g.Key, g => g.Select(c => c.Observer).Distinct().Single())
            .Should().BeEquivalentTo(new Dictionary<Guid, int> { [ResEntry] = 0, [ResExit] = 1, [VisEntry] = 0, [VisExit] = 1 });
        plan.Runs.Select(r => r.Observer).Should().Equal(plan.Runs.Select((_, i) => i % 2));
        // Desks in order of checkpoint and code: AR-05, AR-08, AR-09; observer 0 logs AR-05 and AR-09, observer 1 AR-08.
        plan.DeskBatches.Where(b => b.Observer == 0).Should().OnlyContain(b => b.Desks.Select(d => d.DeskId).SequenceEqual(new[] { DeskId(2), DeskId(1) }));
        plan.DeskBatches.Where(b => b.Observer == 1).Should().OnlyContain(b => b.Desks.Select(d => d.DeskId).SequenceEqual(new[] { DeskId(0) }));
    }

    [Fact]
    public void Plan_Should_TimeEachTracerFromTheMiddleOfAMinuteWithItsEntrantsMeanWait_When_NoErrorIsInjected()
    {
        var truth = Truth();
        var plan = Plan(Campaign());

        plan.Runs.Should().NotBeEmpty();
        plan.Runs.Select(r => r.TracerCode).Should().Equal(plan.Runs.Select((_, i) => RehearsalPlanner.TracerCode(i + 1)));
        foreach (var run in plan.Runs)
        {
            run.JoinedUtc.Second.Should().Be(30);
            run.JoinedUtc.Millisecond.Should().Be(0);
            var zone = run.ZoneId == VisZone ? "A-VIS" : "A-RES";
            var mean = truth.EntrantWaits(truth.QueueOf(zone)!.Value, run.JoinedUtc.AddSeconds(-30)).MeanWaitMinutes!.Value;
            run.TrueWaitMinutes.Should().Be(mean);
            (run.ExitedUtc - run.JoinedUtc).TotalMilliseconds.Should().Be(Math.Max(1000, Math.Round(mean * 60_000, MidpointRounding.AwayFromZero)));
        }

        // One tracer per zone every 10 minutes: A-RES from 18:01, A-VIS from 18:02 (zones a minute apart), each in the first minute with a wait.
        plan.Runs.Where(r => r.ZoneId == ResZone).Select(r => (r.JoinedUtc - At(1080)).TotalMinutes).Should().OnlyContain(m => m >= 1 && m < 90);
        var visitors = plan.Runs.Where(r => r.ZoneId == VisZone).Select(r => (int)(r.JoinedUtc - At(1080)).TotalMinutes).ToList();
        visitors.Should().HaveCountGreaterThan(2).And.OnlyContain(m => m >= 2 && m < 90);
        visitors.Select(m => (m - 2) / 10).Should().OnlyHaveUniqueItems("one tracer per 10-minute slot");
        visitors.Should().Contain([2, 12], "18:02 and 18:12 have entrants").And.NotContain(m => m >= 30 && m < 60, "nobody enters A-VIS from 18:30 to 19:00");
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(0, 10)]
    [InlineData(-1, 5)]
    public void Plan_Should_AddTheTracerError_When_MinutesOrPercentAreInjected(double minutes, double percent)
    {
        var truthful = Plan(Campaign());
        var plan = Plan(Campaign(), new ObserverErrors(0, null, minutes, percent, 0, 0));

        plan.Runs.Select(r => (r.ZoneId, r.TracerCode, r.JoinedUtc)).Should().Equal(truthful.Runs.Select(r => (r.ZoneId, r.TracerCode, r.JoinedUtc)));
        foreach (var (run, truth) in plan.Runs.Zip(truthful.Runs))
            (run.ExitedUtc - run.JoinedUtc).Should().Be(ObserverSlips.TracerWait(truth.ExitedUtc - truth.JoinedUtc, minutes, percent));
    }

    [Fact]
    public void Plan_Should_LogEveryDeskMinuteAsTheObserverSeesIt_When_DesksAreShown()
    {
        var truth = Truth();
        var vis = truth.QueueOf("A-VIS")!.Value;
        var plan = Plan(Campaign(true, "AR-08", "AR-21", "ZZ-01"));

        plan.Summary.Desks.Should().Be(3);
        plan.Summary.DesksWithoutTruth.Should().Be(1);
        plan.DeskBatches.Should().HaveCount(6, "one batch per bin for the one desk observer");
        foreach (var batch in plan.DeskBatches)
        {
            batch.Desks.Select(d => d.DeskId).Should().Equal(DeskId(0), DeskId(1));
            for (var m = 0; m < 15; m++)
            {
                batch.Desks[0].States[m].Should().Be(truth.DeskState(vis, 0, batch.BinStartUtc.AddMinutes(m)).ToString());
                batch.Desks[1].States[m].Should().Be(truth.DeskState(vis, 13, batch.BinStartUtc.AddMinutes(m)).ToString());
            }
        }

        Plan(Campaign(false, "AR-08")).DeskBatches.Should().BeEmpty("Ariva shows no desk to this observer");
        var unseen = Plan(Campaign(true, "AR-08"), deskObservers: []);
        unseen.DeskBatches.Should().BeEmpty();
        unseen.Summary.DesksNotShown.Should().Be(1, "no observer may log the desk");
    }

    [Fact]
    public void Plan_Should_ChunkDeskBatchesAtTwentyDesks_When_AnObserverLogsMore()
    {
        var desks = Enumerable.Range(1, 22).Select(n => string.Create(CultureInfo.InvariantCulture, $"AR-{n:00}")).ToArray();
        var plan = Plan(Campaign(true, desks));

        plan.DeskBatches.Should().HaveCount(12, "two batches per bin");
        plan.DeskBatches.GroupBy(b => b.BinStartUtc).Should().OnlyContain(g => g.Select(b => b.Desks.Count).SequenceEqual(new[] { 20, 2 }));
    }

    [Fact]
    public void Plan_Should_MissBinsAndMinutesFromTheSeed_When_SlipsAreInjected()
    {
        var campaign = Campaign(true, "AR-08", "AR-09");
        var all = Plan(campaign, new ObserverErrors(0, null, 0, 0, 100, 0));
        all.Counts.Should().BeEmpty();
        all.DeskBatches.Should().BeEmpty();
        all.Summary.CountBinsMissed.Should().Be(24);
        all.Summary.DeskBatchesMissed.Should().Be(6);

        var half = Plan(campaign, new ObserverErrors(0, null, 0, 0, 0, 50));
        half.Summary.DeskMinutesMissed.Should().BeInRange(60, 120, "about half of 6 bins x 2 desks x 15 minutes");
        half.DeskBatches.SelectMany(b => b.Desks).Sum(d => d.States.Count(s => s is null)).Should().Be(half.Summary.DeskMinutesMissed);

        var some = Plan(campaign, new ObserverErrors(0, null, 0, 0, 25, 0));
        some.Counts.Count.Should().Be(24 - some.Summary.CountBinsMissed);
        some.Summary.CountBinsMissed.Should().BeInRange(1, 23);
        Plan(campaign, new ObserverErrors(0, null, 0, 0, 25, 0)).Counts.Should().BeEquivalentTo(some.Counts, o => o.WithStrictOrdering(), "the same seed misses the same bins");
        Plan(campaign, new ObserverErrors(0, null, 0, 0, 25, 0), seed: 7).Counts.Select(c => (c.LineId, c.BinStartUtc)).Should()
            .NotEqual(some.Counts.Select(c => (c.LineId, c.BinStartUtc)), "another seed misses other bins");
    }

    [Fact]
    public void Plan_Should_BeTheSame_When_PlannedTwice()
    {
        var campaign = Campaign(true, "AR-08", "AR-05", "AR-12");
        var errors = new ObserverErrors(3, ["A-RES exit"], 0.5, 2, 10, 10);

        var first = Plan(campaign, errors, observers: 2, deskObservers: [1]);
        var second = Plan(campaign, errors, observers: 2, deskObservers: [1]);

        second.Should().BeEquivalentTo(first, o => o.WithStrictOrdering());
    }

    [Theory]
    [InlineData(1, "T-01")]
    [InlineData(9, "T-09")]
    [InlineData(99, "T-99")]
    [InlineData(100, "T-100")]
    [InlineData(999, "T-999")]
    public void TracerCode_Should_BeTheCampaignsLabel_When_Numbered(int n, string expected)
    {
        RehearsalPlanner.TracerCode(n).Should().Be(expected);
        TracerRun.IsTracerCode(expected).Should().BeTrue("Ariva takes it as a tracer label");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    public void TracerCode_Should_Refuse_When_OutsideT01ToT999(int n)
    {
        var label = () => RehearsalPlanner.TracerCode(n);

        label.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Key_Should_BeAnIdempotencyKeyArivaTakesAndDifferPerAccountAndItem_When_Derived()
    {
        var campaign = Guid.Parse("0199a000-0000-7000-8000-0000000000c1");
        var key = ValidationEmulator.Key(campaign, "sim.observer1", "count", "line|bin");

        ManualCount.IsIdempotencyKey(key).Should().BeTrue();
        key.Should().MatchRegex("^sim-[0-9a-f]{40}$");
        ValidationEmulator.Key(campaign, "sim.observer1", "count", "line|bin").Should().Be(key);
        new[]
        {
            ValidationEmulator.Key(campaign, "sim.observer2", "count", "line|bin"),
            ValidationEmulator.Key(campaign, "sim.observer1", "desks", "line|bin"),
            ValidationEmulator.Key(campaign, "sim.observer1", "count", "line|bin2"),
            ValidationEmulator.Key(Guid.NewGuid(), "sim.observer1", "count", "line|bin")
        }.Should().OnlyHaveUniqueItems().And.NotContain(key);
    }

    #endregion
}
