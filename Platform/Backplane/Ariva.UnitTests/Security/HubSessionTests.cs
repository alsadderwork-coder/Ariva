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

    private sealed class FakeCaller(Guid sessionId) : HubCallerContext
    {
        private readonly CancellationTokenSource _aborted = new();

        public bool Aborted => _aborted.IsCancellationRequested;

        public override string ConnectionId { get; } = Guid.NewGuid().ToString("N");

        public override string UserIdentifier => null;

        public override ClaimsPrincipal User { get; } = new(new ClaimsIdentity(
            [new Claim("sub", Guid.CreateVersion7().ToString()), new Claim("sid", sessionId.ToString())], "Bearer"));

        public override IDictionary<object, object> Items { get; } = new Dictionary<object, object>();

        public override IFeatureCollection Features { get; } = new FeatureCollection();

        public override CancellationToken ConnectionAborted => _aborted.Token;

        public override void Abort() => _aborted.Cancel();
    }
}
