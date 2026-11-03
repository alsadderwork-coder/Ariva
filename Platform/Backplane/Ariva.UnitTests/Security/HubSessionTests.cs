using System.Security.Claims;
using Ariva.Api.Common.Hubs;
using Ariva.Core.Security;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-010b for SignalR: a hub connection is refused when its session is not active, every invocation re-checks it,
/// and the sweeper closes the connections of a session that ends while they are open.
/// </summary>
public sealed class HubSessionTests
{
    private readonly FakeSessionValidator _sessions = new();
    private readonly HubSessionRegistry _registry = new();
    private readonly ServiceProvider _services;

    public HubSessionTests()
    {
        _services = new ServiceCollection().AddSingleton<ISessionValidator>(_sessions).BuildServiceProvider();
    }

    [Fact]
    public async Task OnConnected_Should_RegisterTheConnection_When_SessionIsActive()
    {
        var sessionId = Guid.CreateVersion7();
        var caller = new FakeCaller(sessionId);

        await new SessionHubFilter(_registry).OnConnectedAsync(new HubLifetimeContext(caller, _services, null), _ => Task.CompletedTask);

        _registry.Count.Should().Be(1);
        caller.Aborted.Should().BeFalse();
    }

    [Theory]
    [InlineData(SessionState.Revoked)]
    [InlineData(SessionState.Expired)]
    [InlineData(SessionState.Unknown)]
    public async Task OnConnected_Should_RefuseAndAbort_When_SessionIsNotActive(SessionState state)
    {
        var sessionId = Guid.CreateVersion7();
        _sessions[sessionId] = state;
        var caller = new FakeCaller(sessionId);
        var reached = false;

        var connect = () => new SessionHubFilter(_registry).OnConnectedAsync(new HubLifetimeContext(caller, _services, null), _ => { reached = true; return Task.CompletedTask; });

        await connect.Should().ThrowAsync<HubException>().WithMessage("session_expired");
        caller.Aborted.Should().BeTrue();
        reached.Should().BeFalse();
        _registry.Count.Should().Be(0);
    }

    [Fact]
    public async Task Sweep_Should_CloseOnlyTheConnectionsOfEndedSessions_When_ASessionIsRevoked()
    {
        var ended = Guid.CreateVersion7();
        var live = Guid.CreateVersion7();
        var first = new FakeCaller(ended);
        var second = new FakeCaller(ended);
        var other = new FakeCaller(live);
        _registry.TryAdd(ended, first).Should().BeTrue();
        _registry.TryAdd(ended, second).Should().BeTrue();
        _registry.TryAdd(live, other).Should().BeTrue();
        _sessions[ended] = SessionState.Revoked;

        var sweeper = new HubSessionSweeper(_registry, _services.GetRequiredService<IServiceScopeFactory>(), NullLogger<HubSessionSweeper>.Instance);
        var closed = await sweeper.SweepAsync(TestContext.Current.CancellationToken);

        closed.Should().Be(2);
        first.Aborted.Should().BeTrue();
        second.Aborted.Should().BeTrue();
        other.Aborted.Should().BeFalse();
        _registry.Count.Should().Be(1);
        HubSessionSweeper.Interval.Should().BeLessThan(TimeSpan.FromSeconds(5), "revoked sessions lose their hub connections within 5 seconds");
    }

    [Fact]
    public async Task OnConnected_Should_RefuseAndAbort_When_TheSessionHoldsTheMostConnections()
    {
        var sessionId = Guid.CreateVersion7();
        for (var k = 0; k < HubSessionRegistry.MaxConnectionsPerSession; k++)
            await new SessionHubFilter(_registry).OnConnectedAsync(new HubLifetimeContext(new FakeCaller(sessionId), _services, null), _ => Task.CompletedTask);
        var extra = new FakeCaller(sessionId);
        var other = new FakeCaller(Guid.CreateVersion7());

        var connect = () => new SessionHubFilter(_registry).OnConnectedAsync(new HubLifetimeContext(extra, _services, null), _ => Task.CompletedTask);

        await connect.Should().ThrowAsync<HubException>().WithMessage("too_many_connections");
        extra.Aborted.Should().BeTrue();
        _registry.Count.Should().Be(HubSessionRegistry.MaxConnectionsPerSession);
        await new SessionHubFilter(_registry).OnConnectedAsync(new HubLifetimeContext(other, _services, null), _ => Task.CompletedTask);
        other.Aborted.Should().BeFalse("another session has its own allowance");
    }

    [Fact]
    public async Task OnConnected_Should_RefuseAndAbortWithHubFull_When_TheReplicaHoldsItsMostConnections()
    {
        // ARV-096b: every session together, on top of each session's own allowance.
        var registry = new HubSessionRegistry(3);
        var open = Enumerable.Range(0, 3).Select(_ => new FakeCaller(Guid.CreateVersion7())).ToList();
        foreach (var caller in open)
            await new SessionHubFilter(registry).OnConnectedAsync(new HubLifetimeContext(caller, _services, null), _ => Task.CompletedTask);
        var extra = new FakeCaller(Guid.CreateVersion7());

        var connect = () => new SessionHubFilter(registry).OnConnectedAsync(new HubLifetimeContext(extra, _services, null), _ => Task.CompletedTask);

        registry.Full.Should().BeTrue();
        await connect.Should().ThrowAsync<HubException>().WithMessage("hub_full");
        extra.Aborted.Should().BeTrue();
        registry.Count.Should().Be(3);

        await new SessionHubFilter(registry).OnDisconnectedAsync(new HubLifetimeContext(open[0], _services, null), null, (_, _) => Task.CompletedTask);
        var next = new FakeCaller(Guid.CreateVersion7());
        await new SessionHubFilter(registry).OnConnectedAsync(new HubLifetimeContext(next, _services, null), _ => Task.CompletedTask);
        next.Aborted.Should().BeFalse("a closed connection frees its place");
        registry.Full.Should().BeTrue();
    }

    [Fact]
    public async Task OnConnected_Should_GiveThePlaceBack_When_WhatRunsAfterTheAdmissionThrows()
    {
        // Security review of ARV-096b, L3: SignalR does not call OnDisconnectedAsync when OnConnectedAsync throws.
        var registry = new HubSessionRegistry(1);
        var caller = new FakeCaller(Guid.CreateVersion7());

        var connect = () => new SessionHubFilter(registry).OnConnectedAsync(new HubLifetimeContext(caller, _services, null),
            _ => throw new InvalidOperationException("the hub's own OnConnectedAsync failed"));

        await connect.Should().ThrowAsync<InvalidOperationException>();
        registry.Count.Should().Be(0, "the connection never opened");
        registry.Full.Should().BeFalse();
        await new SessionHubFilter(registry).OnConnectedAsync(new HubLifetimeContext(new FakeCaller(Guid.CreateVersion7()), _services, null), _ => Task.CompletedTask);
        registry.Count.Should().Be(1, "its place went to the next connection");
    }

    [Fact]
    public void Admit_Should_CountAConnectionWithoutASession_When_ItHoldsTheReplicasTotal()
    {
        var registry = new HubSessionRegistry(2);

        registry.Admit(null, new FakeCaller(Guid.CreateVersion7())).Should().Be(HubAdmission.Admitted);
        registry.Admit(null, new FakeCaller(Guid.CreateVersion7())).Should().Be(HubAdmission.Admitted, "a connection without a session has no allowance of its own");
        registry.Admit(Guid.CreateVersion7(), new FakeCaller(Guid.CreateVersion7())).Should().Be(HubAdmission.ReplicaFull);
        registry.Sessions().Should().BeEmpty("the sweeper checks sessions only");
        registry.Count.Should().Be(2);
    }

    [Fact]
    public void Admit_Should_NameTheSessionsLimit_When_TheReplicaHasRoomButTheSessionHasNone()
    {
        var registry = new HubSessionRegistry(100);
        var sessionId = Guid.CreateVersion7();
        for (var k = 0; k < HubSessionRegistry.MaxConnectionsPerSession; k++)
            registry.Admit(sessionId, new FakeCaller(sessionId)).Should().Be(HubAdmission.Admitted);

        registry.Admit(sessionId, new FakeCaller(sessionId)).Should().Be(HubAdmission.SessionFull);
        registry.Full.Should().BeFalse();
    }

    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(1000, true)]
    [InlineData(20_000, true)]
    [InlineData(20_001, false)]
    public void HubSettings_Should_HoldTheTotalWithinItsBounds_When_ItIsRead(int maxConnections, bool valid)
    {
        new Ariva.Api.Common.Settings.HubSettings { MaxConnections = maxConnections }.IsValid.Should().Be(valid);
    }

    #region One account's most (ARV-229a)

    [Fact]
    public async Task OnConnected_Should_RefuseAndAbortWithAccountFull_When_TheAccountHoldsItsMostConnections()
    {
        // Every sign-in of one subject together, beside each session's own 8: two sessions of one account reach its 10.
        var registry = new HubSessionRegistry(100, 10);
        const string account = "0199a000-0000-7000-8000-000000022901";
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var open = new List<FakeCaller>();
        for (var k = 0; k < HubSessionRegistry.MaxConnectionsPerSession; k++)
            open.Add(new FakeCaller(first, account));
        open.Add(new FakeCaller(second, account));
        open.Add(new FakeCaller(second, account));
        foreach (var caller in open)
            await new SessionHubFilter(registry).OnConnectedAsync(new HubLifetimeContext(caller, _services, null), _ => Task.CompletedTask);
        var sameSession = new FakeCaller(second, account);
        var newSignIn = new FakeCaller(Guid.CreateVersion7(), account);
        var otherAccount = new FakeCaller(Guid.CreateVersion7());

        var connectSame = () => new SessionHubFilter(registry).OnConnectedAsync(new HubLifetimeContext(sameSession, _services, null), _ => Task.CompletedTask);
        var connectNew = () => new SessionHubFilter(registry).OnConnectedAsync(new HubLifetimeContext(newSignIn, _services, null), _ => Task.CompletedTask);

        await connectSame.Should().ThrowAsync<HubException>().WithMessage("account_full");
        await connectNew.Should().ThrowAsync<HubException>().WithMessage("account_full", "a new sign-in of the account has no allowance of its own past the account's");
        sameSession.Aborted.Should().BeTrue();
        newSignIn.Aborted.Should().BeTrue();
        registry.Count.Should().Be(10);
        registry.CountOf(account).Should().Be(10);
        registry.Full.Should().BeFalse("the replica has room: only the account is full");
        await new SessionHubFilter(registry).OnConnectedAsync(new HubLifetimeContext(otherAccount, _services, null), _ => Task.CompletedTask);
        otherAccount.Aborted.Should().BeFalse("another account has its own allowance");

        await new SessionHubFilter(registry).OnDisconnectedAsync(new HubLifetimeContext(open[0], _services, null), null, (_, _) => Task.CompletedTask);
        var next = new FakeCaller(Guid.CreateVersion7(), account);
        await new SessionHubFilter(registry).OnConnectedAsync(new HubLifetimeContext(next, _services, null), _ => Task.CompletedTask);
        next.Aborted.Should().BeFalse("a closed connection of the account frees its place");
        registry.CountOf(account).Should().Be(10);
    }

    [Fact]
    public void Admit_Should_NameTheNarrowerLimit_When_TheSessionAndItsAccountAreBothFull()
    {
        // At the least allowed (8), one session fills its account too: its ninth is the session's limit, as before ARV-229a; another
        // sign-in of the account meets the account's.
        var registry = new HubSessionRegistry(100, HubSessionRegistry.MaxConnectionsPerSession);
        const string account = "0199a000-0000-7000-8000-000000022902";
        var sessionId = Guid.CreateVersion7();
        for (var k = 0; k < HubSessionRegistry.MaxConnectionsPerSession; k++)
            registry.Admit(sessionId, new FakeCaller(sessionId, account)).Should().Be(HubAdmission.Admitted);

        registry.Admit(sessionId, new FakeCaller(sessionId, account)).Should().Be(HubAdmission.SessionFull);
        registry.Admit(Guid.CreateVersion7(), new FakeCaller(sessionId, account)).Should().Be(HubAdmission.AccountFull);
        registry.Admit(Guid.CreateVersion7(), new FakeCaller(sessionId)).Should().Be(HubAdmission.Admitted, "another account");
    }

    [Fact]
    public void AdmissionOf_Should_NameTheReplicaThenTheAccount_When_ANegotiateArrives()
    {
        var registry = new HubSessionRegistry(4, 2);
        const string full = "0199a000-0000-7000-8000-000000022903";
        const string other = "0199a000-0000-7000-8000-000000022904";
        registry.Admit(Guid.CreateVersion7(), new FakeCaller(Guid.CreateVersion7(), full)).Should().Be(HubAdmission.Admitted);
        var held = new FakeCaller(Guid.CreateVersion7(), full);
        registry.Admit(Guid.CreateVersion7(), held).Should().Be(HubAdmission.Admitted);

        registry.AdmissionOf(Principal(full)).Should().Be(HubAdmission.AccountFull);
        registry.AdmissionOf(Principal(other)).Should().Be(HubAdmission.Admitted);
        registry.AdmissionOf(new ClaimsPrincipal(new ClaimsIdentity())).Should().Be(HubAdmission.Admitted, "a principal without a subject counts in the total only");
        registry.AdmissionOf(null).Should().Be(HubAdmission.Admitted);

        registry.Admit(null, new FakeCaller(Guid.CreateVersion7())).Should().Be(HubAdmission.Admitted);
        registry.Admit(null, new FakeCaller(Guid.CreateVersion7())).Should().Be(HubAdmission.Admitted);
        registry.AdmissionOf(Principal(other)).Should().Be(HubAdmission.ReplicaFull);
        registry.AdmissionOf(Principal(full)).Should().Be(HubAdmission.ReplicaFull, "the replica's total is named first");

        registry.Remove(held.ConnectionId);
        registry.AdmissionOf(Principal(full)).Should().Be(HubAdmission.Admitted, "a closed connection frees its place in the total and in its account");
    }

    [Fact]
    public async Task Counts_Should_FollowEveryWayAConnectionLeaves_When_ConnectionsCloseAndSessionsEnd()
    {
        var registry = new HubSessionRegistry(100, 20);
        const string account = "0199a000-0000-7000-8000-000000022905";
        var ended = Guid.CreateVersion7();
        var live = Guid.CreateVersion7();
        var endedCallers = Enumerable.Range(0, 3).Select(_ => new FakeCaller(ended, account)).ToList();
        var liveCaller = new FakeCaller(live, account);
        endedCallers.ForEach(caller => registry.Admit(ended, caller).Should().Be(HubAdmission.Admitted));
        registry.Admit(live, liveCaller).Should().Be(HubAdmission.Admitted);
        registry.Admit(live, liveCaller).Should().Be(HubAdmission.Admitted, "a connection tracked already keeps its place");
        registry.CountOf(account).Should().Be(4, "and is not counted twice");

        _sessions[ended] = SessionState.Revoked;
        var sweeper = new HubSessionSweeper(registry, _services.GetRequiredService<IServiceScopeFactory>(), NullLogger<HubSessionSweeper>.Instance);
        (await sweeper.SweepAsync(TestContext.Current.CancellationToken)).Should().Be(3);
        endedCallers.Should().OnlyContain(caller => caller.Aborted);
        foreach (var caller in endedCallers)
            registry.Remove(caller.ConnectionId);
        registry.CountOf(account).Should().Be(1, "an aborted connection's own disconnect finds nothing to give back a second time");
        registry.Sessions().Should().Equal(live);

        registry.Remove(liveCaller.ConnectionId);
        registry.Remove(liveCaller.ConnectionId);
        registry.Remove("never-tracked");
        registry.CountOf(account).Should().Be(0);
        registry.CountOf(null).Should().Be(0);
        registry.Count.Should().Be(0);
        registry.Sessions().Should().BeEmpty();
        for (var k = 0; k < 20; k++)
            registry.Admit(Guid.CreateVersion7(), new FakeCaller(Guid.CreateVersion7(), account)).Should().Be(HubAdmission.Admitted, "the account has all its allowance back");
        registry.Admit(Guid.CreateVersion7(), new FakeCaller(Guid.CreateVersion7(), account)).Should().Be(HubAdmission.AccountFull);
    }

    [Fact]
    public async Task Admit_Should_HoldTheAccountToItsMost_When_ItsSignInsConnectAtOnce()
    {
        var registry = new HubSessionRegistry(10_000, 50);
        const string account = "0199a000-0000-7000-8000-000000022906";
        var callers = Enumerable.Range(0, 400).Select(_ => new FakeCaller(Guid.CreateVersion7(), account)).ToList();

        var admissions = await Task.WhenAll(callers.Select(caller => Task.Run(() => registry.Admit(Guid.CreateVersion7(), caller), TestContext.Current.CancellationToken)));

        admissions.Count(a => a == HubAdmission.Admitted).Should().Be(50);
        admissions.Count(a => a == HubAdmission.AccountFull).Should().Be(350);
        registry.CountOf(account).Should().Be(50);
        await Task.WhenAll(callers.Select(caller => Task.Run(() => registry.Remove(caller.ConnectionId), TestContext.Current.CancellationToken)));
        registry.CountOf(account).Should().Be(0);
        registry.Count.Should().Be(0);
    }

    [Fact]
    public async Task Counts_Should_StayExact_When_ConnectionsOfOneAccountOpenAndCloseAtOnce()
    {
        // Every count changes under one lock: eight threads over two sessions of one account, each taking up to six connections a
        // round and giving them back. In the first round every thread holds what it took until all have taken theirs: 48 attempts
        // on two sessions of 8 under an account of 15, so one session fills (the rest of its attempts are refused as the session's)
        // and the account fills with the other session below its 8 (refused as the account's). Every count ends at zero, and the
        // account never holds more than its most at any moment.
        var registry = new HubSessionRegistry(10_000, 15);
        const string account = "0199a000-0000-7000-8000-000000022908";
        Guid[] sessions = [Guid.CreateVersion7(), Guid.CreateVersion7()];
        var ct = TestContext.Current.CancellationToken;
        var most = 0;
        var sessionFull = 0;
        var accountFull = 0;
        using var start = new Barrier(8);
        using var holding = new Barrier(8);

        var workers = Enumerable.Range(0, 8).Select(worker => Task.Factory.StartNew(() =>
        {
            var session = sessions[worker % sessions.Length];
            var held = new List<FakeCaller>(6);
            start.SignalAndWait(ct);
            for (var k = 0; k < 2000; k++)
            {
                for (var c = 0; c < 6; c++)
                {
                    var caller = new FakeCaller(session, account);
                    switch (registry.Admit(session, caller))
                    {
                        case HubAdmission.Admitted:
                            held.Add(caller);
                            break;
                        case HubAdmission.SessionFull:
                            Interlocked.Increment(ref sessionFull);
                            break;
                        case HubAdmission.AccountFull:
                            Interlocked.Increment(ref accountFull);
                            break;
                    }
                }

                var now = registry.CountOf(account);
                int seen;
                while (now > (seen = Volatile.Read(ref most)) && Interlocked.CompareExchange(ref most, now, seen) != seen)
                {
                }

                if (k == 0)
                    holding.SignalAndWait(ct);
                held.ForEach(caller => registry.Remove(caller.ConnectionId));
                held.Clear();
            }
        }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToList();
        await Task.WhenAll(workers);

        registry.CountOf(account).Should().Be(0);
        registry.Count.Should().Be(0);
        registry.Sessions().Should().BeEmpty();
        most.Should().Be(15, "the first round filled the account, and it never held more than its most");
        sessionFull.Should().BePositive("a session's 8 was reached");
        accountFull.Should().BePositive("and the account's 15");
    }

    [Fact]
    public void ReasonOf_Should_NameEachLimit_When_AConnectionIsRefused()
    {
        SessionHubFilter.ReasonOf(HubAdmission.ReplicaFull).Should().Be("hub_full");
        SessionHubFilter.ReasonOf(HubAdmission.AccountFull).Should().Be("account_full");
        SessionHubFilter.ReasonOf(HubAdmission.SessionFull).Should().Be("too_many_connections");
        var admitted = () => SessionHubFilter.ReasonOf(HubAdmission.Admitted);
        admitted.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void AccountOf_Should_ReadTheSubjectOnly_When_ItIsPresentAndNotBlank()
    {
        HubSessionRegistry.AccountOf(Principal("0199a000-0000-7000-8000-000000022907")).Should().Be("0199a000-0000-7000-8000-000000022907");
        HubSessionRegistry.AccountOf(Principal(" ")).Should().BeNull();
        HubSessionRegistry.AccountOf(new ClaimsPrincipal(new ClaimsIdentity([new Claim("name", "officer.one")], "Bearer"))).Should().BeNull("only the subject names an account");
        HubSessionRegistry.AccountOf(null).Should().BeNull();
    }

    [Theory]
    [InlineData(7, false)]
    [InlineData(8, true)]
    [InlineData(200, true)]
    [InlineData(20_000, true)]
    [InlineData(20_001, false)]
    public void HubSettings_Should_HoldTheAccountsMostWithinItsBounds_When_ItIsRead(int perAccount, bool valid)
    {
        new Ariva.Api.Common.Settings.HubSettings { MaxConnectionsPerAccount = perAccount }.IsValid.Should().Be(valid);
    }

    [Fact]
    public void HubSettings_Should_HoldTheWallOfTheLoadRunsOnOneReplica_When_TheDefaultsApply()
    {
        // The load harness signs a wall of screens in under one account (150 screens in its full run, 50 in the pilot's): the default
        // holds them, and is still a fifth of the default total.
        var defaults = new Ariva.Api.Common.Settings.HubSettings();
        defaults.MaxConnectionsPerAccount.Should().Be(200).And.BeGreaterThanOrEqualTo(150);
        defaults.MaxConnectionsPerAccount.Should().BeLessThan(defaults.MaxConnections);
        Ariva.Api.Common.Settings.HubSettings.LeastMaxConnectionsPerAccount.Should().Be(HubSessionRegistry.MaxConnectionsPerSession, "a session always has its own allowance");
        new HubSessionRegistry().MaxConnectionsPerAccount.Should().Be(defaults.MaxConnectionsPerAccount);
        var zero = () => new HubSessionRegistry(10, 0);
        zero.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static ClaimsPrincipal Principal(string subject) => new(new ClaimsIdentity([new Claim("sub", subject)], "Bearer"));

    #endregion

    private sealed class FakeCaller(Guid sessionId, string account = null) : HubCallerContext
    {
        private readonly CancellationTokenSource _aborted = new();

        public bool Aborted => _aborted.IsCancellationRequested;

        public override string ConnectionId { get; } = Guid.NewGuid().ToString("N");

        public override string UserIdentifier => null;

        public override ClaimsPrincipal User { get; } = new(new ClaimsIdentity(
            [new Claim("sub", account ?? Guid.CreateVersion7().ToString()), new Claim("sid", sessionId.ToString())], "Bearer"));

        public override IDictionary<object, object> Items { get; } = new Dictionary<object, object>();

        public override IFeatureCollection Features { get; } = new FeatureCollection();

        public override CancellationToken ConnectionAborted => _aborted.Token;

        public override void Abort() => _aborted.Cancel();
    }
}
