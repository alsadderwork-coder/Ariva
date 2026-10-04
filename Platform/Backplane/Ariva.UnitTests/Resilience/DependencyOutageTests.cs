using System.Net.Sockets;
using System.Text.RegularExpressions;
using Ariva.Infra.Resilience;
using FluentAssertions;
using NHibernate.Exceptions;
using Npgsql;
using StackExchange.Redis;

namespace Ariva.UnitTests.Resilience;

/// <summary>ARV-072: which exceptions mean a dependency is unavailable (503 with Retry-After) and which are faults (500).</summary>
public sealed class DependencyOutageTests
{
    public static TheoryData<Exception> Outages => new()
    {
        new NpgsqlException("connection refused", new SocketException(111)),
        new GenericADOException("could not execute query", new NpgsqlException("connection reset", new IOException("reset"))),
        new RedisConnectionException(ConnectionFailureType.UnableToConnect, "no connection"),
        new RedisTimeoutException("timeout", CommandStatus.WaitingToBeSent),
        new TimeoutException("factory hard timeout"),
        new AggregateException(new InvalidOperationException("other"), new TimeoutException("one of them")),
        new InvalidOperationException("outer", new InvalidOperationException("middle", new TimeoutException("inner")))
    };

    public static TheoryData<Exception> Faults => new()
    {
        new InvalidOperationException("a bug"),
        new NpgsqlException("syntax error"),
        new RegexMatchTimeoutException("ReDoS guard"),
        new GenericADOException("constraint", new InvalidOperationException("not transient")),
        new RedisServerException("WRONGTYPE"),
        new OperationCanceledException()
    };

    [Theory]
    [MemberData(nameof(Outages))]
    public void Is_Should_BeTrue_When_ADependencyIsUnreachableOrStalled(Exception exception) =>
        DependencyOutage.Is(exception).Should().BeTrue();

    [Theory]
    [MemberData(nameof(Faults))]
    public void Is_Should_BeFalse_When_TheErrorIsNotAnOutage(Exception exception) =>
        DependencyOutage.Is(exception).Should().BeFalse();

    [Fact]
    public void Is_Should_BeTrue_When_PostgresIsShuttingDownOrOutOfConnections()
    {
        foreach (var state in new[] { "08006", "57P01", "57P03", "53300" })
            DependencyOutage.Is(new PostgresException("server", "FATAL", "FATAL", state)).Should().BeTrue(state);
        DependencyOutage.Is(new PostgresException("unique", "ERROR", "ERROR", "23505")).Should().BeFalse();
    }

    [Fact]
    public void Is_Should_StopSearching_When_TheChainIsDeeperThanEightLevels()
    {
        Exception e = new TimeoutException("deep");
        for (var i = 0; i < 8; i++)
            e = new InvalidOperationException("wrap", e);
        DependencyOutage.Is(e).Should().BeFalse("only the outer eight levels are searched");
    }
}
