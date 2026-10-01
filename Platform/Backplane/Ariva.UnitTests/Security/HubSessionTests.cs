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
        _registry.Add(ended, first);
        _registry.Add(ended, second);
        _registry.Add(live, other);
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
