using Ariva.Infra.NHibernate.Interceptors;
using FluentAssertions;

namespace Ariva.UnitTests.Persistence;

/// <summary>
/// ARV-273: a change is stamped after the row's last stamp, to the microsecond PostgreSQL keeps, so the last change's time is a
/// version no two stored states of a row share, whatever the clock of the pod that saves it.
/// </summary>
public sealed class AuditInterceptorTests
{
    #region Fields

    private static readonly DateTime Last = new(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc);

    #endregion

    #region Tests

    [Fact]
    public void After_Should_StampNow_When_TheClockIsPastTheLastStamp()
    {
        AuditInterceptor.After(Last, Last.AddSeconds(3)).Should().Be(Last.AddSeconds(3));
        AuditInterceptor.After(Last, Last.AddTicks(TimeSpan.TicksPerMicrosecond)).Should().Be(Last.AddTicks(TimeSpan.TicksPerMicrosecond));
        AuditInterceptor.After(null, Last).Should().Be(Last, "a row without a stamp yet");
    }

    [Theory]
    [InlineData(-50_000_000L, "a pod whose clock is five seconds behind the one that stamped the row")]
    [InlineData(0L, "the same instant (a clock that has not moved)")]
    [InlineData(9L, "a later instant within the same microsecond")]
    public void After_Should_StampOneMicrosecondAfterTheLastStamp_When_TheClockIsNotPastIt(long ticks, string reason)
    {
        var stamped = AuditInterceptor.After(Last, Last.AddTicks(ticks));

        stamped.Should().Be(Last.AddTicks(TimeSpan.TicksPerMicrosecond), reason);
        stamped.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void After_Should_CompareAtTheMicrosecond_When_TheLastStampCarriesTicksPostgreSqlDropped()
    {
        // A stamp kept in memory with ticks below the microsecond, which the stored row no longer has.
        var inMemory = Last.AddTicks(7);

        AuditInterceptor.After(inMemory, Last.AddTicks(8)).Should().Be(Last.AddTicks(TimeSpan.TicksPerMicrosecond));
        AuditInterceptor.After(inMemory, Last.AddTicks(13)).Should().Be(Last.AddTicks(13), "past the last stamp's microsecond");
    }

    #endregion
}
