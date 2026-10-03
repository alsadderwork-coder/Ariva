using Ariva.Infra.Caching;
using FluentAssertions;
using Moq;
using StackExchange.Redis;

namespace Ariva.UnitTests.Caching;

/// <summary>Ending the shared Redis connection at shutdown never throws for the library's close race or a connection that never connected.</summary>
public sealed class RedisConnectionTests
{
    [Fact]
    public async Task Close_Should_NotThrow_When_TheLibraryThrowsFromItsOwnClose()
    {
        var multiplexer = new Mock<IConnectionMultiplexer>();
        multiplexer.Setup(m => m.DisposeAsync()).Throws<NullReferenceException>();

        var close = async () => await RedisConnection.CloseQuietlyAsync(Task.FromResult(multiplexer.Object));

        await close.Should().NotThrowAsync();
        multiplexer.Verify(m => m.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task Close_Should_NotThrow_When_TheFirstConnectFailed()
    {
        var close = async () => await RedisConnection.CloseQuietlyAsync(
            Task.FromException<IConnectionMultiplexer>(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "no Redis")));

        await close.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Close_Should_StillThrow_When_SomethingElseFails()
    {
        var multiplexer = new Mock<IConnectionMultiplexer>();
        multiplexer.Setup(m => m.DisposeAsync()).Throws(new InvalidOperationException("not the close race"));

        var close = async () => await RedisConnection.CloseQuietlyAsync(Task.FromResult(multiplexer.Object));

        await close.Should().ThrowAsync<InvalidOperationException>("only the two known shutdown cases are tolerated");
    }

    [Fact]
    public async Task Dispose_Should_DoNothing_When_NeverConnected()
    {
        var connection = new RedisConnection(new RedisSettings { Enabled = true, ConnectionString = "localhost:1" });

        var dispose = async () => await connection.DisposeAsync();

        await dispose.Should().NotThrowAsync();
    }
}
