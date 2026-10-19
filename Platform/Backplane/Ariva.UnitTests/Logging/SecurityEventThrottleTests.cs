using System.Net;
using Ariva.Api.Common.Logging;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Ariva.UnitTests.Logging;

/// <summary>
/// ARV-091 (CWE-400, CWE-779, CWE-223, CWE-778): a flood of refusals writes a bounded number of events per kind and second,
/// and one source's flood cannot hide another source's event: each source has a small share of the kind's ceiling. Events
/// left out are counted, written as a summary line per kind every period (so a flood that stops still reports its count)
/// and at shutdown, and counted in the <c>ariva.security.events</c> metric, written or not.
/// </summary>
public sealed class SecurityEventThrottleTests
{
    #region Fixtures

    private sealed class StepClock : TimeProvider
    {
        public long Milliseconds { get; set; } = 1_000_000;

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => Milliseconds;

        public override ITimer CreateTimer(TimerCallback callback, object state, TimeSpan dueTime, TimeSpan period) => new NoTimer();

        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
                // Nothing runs: the tests write the summaries themselves.
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>A clock whose timer runs when the test fires it, with the execution context a system timer would capture.</summary>
    private sealed class FiringClock : TimeProvider
    {
        private (TimerCallback Callback, ExecutionContext Context) _timer;

        public override ITimer CreateTimer(TimerCallback callback, object state, TimeSpan dueTime, TimeSpan period)
        {
            _timer = (callback, ExecutionContext.Capture());
            return new StepClock().CreateTimer(callback, state, dueTime, period);
        }

        public void Fire()
        {
            if (_timer.Context is null)
                _timer.Callback(null);
            else
                ExecutionContext.Run(_timer.Context, _ => _timer.Callback(null), null);
        }
    }

    private sealed class NanosecondClock : TimeProvider
    {
        public override long TimestampFrequency => 1_000_000_000;
    }

    private sealed class SerilogSink : Serilog.Core.ILogEventSink
    {
        public List<Serilog.Events.LogEvent> Events { get; } = [];

        public void Emit(Serilog.Events.LogEvent logEvent) => Events.Add(logEvent);
    }

    private sealed class CapturedLogger : ILogger<SecurityEventLog>
    {
        public List<(EventId Id, IReadOnlyDictionary<string, object> Values)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter) =>
            Entries.Add((eventId, state is IEnumerable<KeyValuePair<string, object>> pairs ? pairs.ToDictionary(p => p.Key, p => p.Value) : []));
    }

    private static HttpContext From(string address)
    {
        var context = new DefaultHttpContext { Request = { Method = "GET" } };
        context.Features.Set(new RequestLogContext(IPAddress.Parse(address)));
        return context;
    }

    #endregion

    #region The throttle

    [Fact]
    public void TryTake_Should_GiveEachSourceItsShare_When_OneSourceFloods()
    {
        var throttle = new SecurityEventThrottle(perSecond: 60, perSource: 3, maxSources: 16, () => 5_000);

        var flood = Enumerable.Range(0, 1_000).Count(_ => throttle.TryTake("address:198.51.100.1"));
        var other = throttle.TryTake("address:203.0.113.9");

        flood.Should().Be(3);
        other.Should().BeTrue("another source's event is still written in the same second");
        throttle.TakeSuppressed().Should().Be(997);
        throttle.TakeSuppressed().Should().Be(0, "the count is handed over once");
    }

    [Fact]
    public void TryTake_Should_StopAtTheKindsCeiling_When_ManySourcesFloodTogether()
    {
        var throttle = new SecurityEventThrottle(perSecond: 10, perSource: 3, maxSources: 1_000, () => 5_000);

        var written = Enumerable.Range(0, 100).Count(i => throttle.TryTake($"address:10.0.0.{i}"));

        written.Should().Be(10);
    }

    [Fact]
    public void TryTake_Should_ShareOneOverflowBucket_When_TheSourceMapIsFull()
    {
        var throttle = new SecurityEventThrottle(perSecond: 100, perSource: 2, maxSources: 2, () => 5_000);

        throttle.TryTake("a").Should().BeTrue();
        throttle.TryTake("b").Should().BeTrue();
        var late = new[] { "c", "d", "e", null }.Count(throttle.TryTake);

        late.Should().Be(2, "sources beyond the map, and an unknown source, share one bucket of one share");
        throttle.TryTake("a").Should().BeTrue("a counted source keeps its own share");
    }

    [Fact]
    public void TryTake_Should_StartAfresh_When_TheNextSecondBegins()
    {
        var now = 5_000L;
        var throttle = new SecurityEventThrottle(perSecond: 60, perSource: 3, maxSources: 16, () => now);
        for (var i = 0; i < 10; i++)
            throttle.TryTake("a");

        now += 1_000;

        throttle.TryTake("a").Should().BeTrue();
        throttle.TakeSuppressed().Should().Be(7);
    }

    [Fact]
    public void TryTake_Should_StayWithinTheShares_When_ManyThreadsTakeAtOnce()
    {
        var throttle = new SecurityEventThrottle(perSecond: 20, perSource: 3, maxSources: 1_024, () => 50_000);
        var oneSource = 0;
        var manySources = 0;

        Parallel.For(0, 10_000, i =>
        {
            if (throttle.TryTake("flood"))
                Interlocked.Increment(ref oneSource);
            if (throttle.TryTake("source:" + i))
                Interlocked.Increment(ref manySources);
        });

        oneSource.Should().Be(3);
        (oneSource + manySources).Should().Be(20);
        throttle.TakeSuppressed().Should().Be(20_000 - 20);
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 1, 0)]
    public void Constructor_Should_Refuse_When_ABoundIsNotPositive(int perSecond, int perSource, int maxSources)
    {
        var create = () => new SecurityEventThrottle(perSecond, perSource, maxSources);

        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    #endregion

    #region The security event log

    [Fact]
    public void Challenged_Should_StillWriteAnotherAddressesEvent_When_OneAddressFloodsInTheSameSecond()
    {
        var logger = new CapturedLogger();
        using var events = new SecurityEventLog(logger, timeProvider: new StepClock());

        for (var i = 0; i < 1_000; i++)
            events.Challenged(From("198.51.100.1"), SecurityEvents.NotAuthenticated);
        var beforeB = logger.Entries.Count;
        events.Challenged(From("203.0.113.9"), SecurityEvents.NotAuthenticated);

        beforeB.Should().Be(SecurityEventLog.MaxPerSourcePerSecond, "the decoy writes its share and no more");
        logger.Entries.Should().HaveCount(beforeB + 1, "the other address's event is written");
    }

    [Fact]
    public void Challenged_Should_ShareOneBudget_When_AddressesAreInOneIpv6Slash64()
    {
        var logger = new CapturedLogger();
        using var events = new SecurityEventLog(logger, timeProvider: new StepClock());

        for (var host = 1; host <= 100; host++)
            events.Challenged(From($"2001:db8:1:2::{host:x}"), SecurityEvents.NotAuthenticated);
        var fromOneNetwork = logger.Entries.Count;
        events.Challenged(From("2001:db8:1:3::1"), SecurityEvents.NotAuthenticated);

        fromOneNetwork.Should().Be(SecurityEventLog.MaxPerSourcePerSecond, "100 addresses of one /64 are one source");
        logger.Entries.Should().HaveCount(fromOneNetwork + 1, "another /64 keeps its own share");
    }

    [Fact]
    public void ElapsedMilliseconds_Should_StayExact_When_ANanosecondClockHasRunForMonths()
    {
        var clock = new NanosecondClock();
        var twoHundredDays = (long)TimeSpan.FromDays(200).TotalMilliseconds;
        var timestamp = twoHundredDays * 1_000_000;

        SecurityEventLog.ElapsedMilliseconds(clock, 0, timestamp).Should().Be(twoHundredDays, "a timestamp times 1,000 would overflow a long here");
        SecurityEventLog.ElapsedMilliseconds(clock, timestamp, timestamp + 1_500_000_000).Should().Be(1_500);
    }

    [Fact]
    public void WriteSummaries_Should_ReportTheCountOnce_When_AFloodHasStopped()
    {
        var logger = new CapturedLogger();
        var clock = new StepClock();
        using var events = new SecurityEventLog(logger, timeProvider: clock);
        for (var i = 0; i < 30; i++)
            events.RateLimited(From("198.51.100.1"), SecurityEvents.GlobalPolicy, _ => SecurityEvents.AddressPartition);

        clock.Milliseconds += 10_000;
        events.WriteSummaries();
        events.WriteSummaries();

        var summary = logger.Entries.Should().ContainSingle(e => e.Id.Id == SecurityEvents.EventsSuppressedId).Subject;
        summary.Id.Name.Should().Be(SecurityEvents.EventsSuppressedName);
        summary.Values["Count"].Should().Be(27L);
        summary.Values["Kind"].Should().Be(nameof(SecurityEventKind.RateLimitRejected));
        summary.Values["KindEventId"].Should().Be(SecurityEvents.RateLimitRejectedId);
        summary.Values["Seconds"].Should().Be(10L);
    }

    [Fact]
    public void Summary_Should_NameNoCaller_When_TheLogWasCreatedDuringARequest()
    {
        var sink = new SerilogSink();
        using var serilog = new Serilog.LoggerConfiguration().Enrich.FromLogContext().WriteTo.Sink(sink).CreateLogger();
        using var factory = new Serilog.Extensions.Logging.SerilogLoggerFactory(serilog);
        var clock = new FiringClock();
        SecurityEventLog events;
        using (Serilog.Context.LogContext.Push(new RequestLogContext(IPAddress.Parse("198.51.100.1"))))
        {
            // First resolved while a request runs, as in a host.
            events = new SecurityEventLog(new Logger<SecurityEventLog>(factory), timeProvider: clock);
            for (var i = 0; i < 10; i++)
                events.InvalidModel(From("198.51.100.1"));
        }

        clock.Fire();
        events.Dispose();

        var summary = sink.Events.Should().ContainSingle(e => e.MessageTemplate.Text.Contains("left out by the throttle", StringComparison.Ordinal)).Subject;
        summary.Properties.Should().NotContainKey(RequestLogContext.CallerAddressProperty, "the timer runs without the creating request's context");
        summary.Properties.Should().NotContainKey(RequestLogContext.CallerUserIdProperty);
    }

    [Fact]
    public void Dispose_Should_WriteThePendingCounts_When_TheHostStops()
    {
        var logger = new CapturedLogger();
        var events = new SecurityEventLog(logger, timeProvider: new StepClock());
        for (var i = 0; i < 5; i++)
            events.InvalidModel(From("198.51.100.1"));

        events.Dispose();
        events.Dispose();

        logger.Entries.Where(e => e.Id.Id == SecurityEvents.EventsSuppressedId).Should().ContainSingle()
            .Which.Values["Count"].Should().Be(2L);
    }

    [Fact]
    public void Metric_Should_CountEveryEvent_When_TheThrottleWritesOrLeavesItOut()
    {
        using var metrics = new SecurityEventMetrics();
        using var reader = new InstrumentReader(metrics.Meter);
        using var events = new SecurityEventLog(new CapturedLogger(), metrics, new StepClock());

        for (var i = 0; i < 50; i++)
            events.Forbidden(From("198.51.100.1"), null);
        events.Forbidden(From("203.0.113.9"), null);

        reader.Sum(SecurityEventMetrics.EventsName, ("kind", nameof(SecurityEventKind.AuthorizationForbidden)), ("outcome", SecurityEventMetrics.Written)).Should().Be(4);
        reader.Sum(SecurityEventMetrics.EventsName, ("kind", nameof(SecurityEventKind.AuthorizationForbidden)), ("outcome", SecurityEventMetrics.Suppressed)).Should().Be(47);
        reader.Sum(SecurityEventMetrics.EventsName).Should().Be(51, "every event is counted, written or not");
    }

    [Fact]
    public void Record_Should_MarkTheRequest_When_TheThrottleLeavesTheEventOut()
    {
        using var events = new SecurityEventLog(new CapturedLogger(), timeProvider: new StepClock());
        for (var i = 0; i < SecurityEventLog.MaxPerSourcePerSecond; i++)
            events.RepeatedQuery(From("198.51.100.1"));
        var left = From("198.51.100.1");

        events.RepeatedQuery(left);

        RequestLogContext.Of(left).Recorded.Should().BeTrue("a refusal left out is still recorded, so no generic event repeats it");
    }

    [Fact]
    public void Forbidden_Should_CountAFailureAndThrowNothing_When_TheEventCannotBeBuilt()
    {
        using var metrics = new SecurityEventMetrics();
        using var reader = new InstrumentReader(metrics.Meter);
        using var events = new SecurityEventLog(new CapturedLogger(), metrics, new StepClock());
        var broken = Microsoft.AspNetCore.Authorization.AuthorizationFailure.Failed([new Ariva.Api.Common.Security.PermissionRequirement(null)]);

        var forbid = () => events.Forbidden(From("198.51.100.1"), broken);

        forbid.Should().NotThrow();
        reader.Sum(SecurityEventMetrics.EventsName, ("outcome", SecurityEventMetrics.Failed)).Should().Be(1);
    }

    #endregion
}
