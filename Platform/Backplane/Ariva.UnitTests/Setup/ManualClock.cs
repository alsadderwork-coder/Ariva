namespace Ariva.UnitTests.Setup;

/// <summary>A <see cref="TimeProvider"/> that moves only when a test advances it.</summary>
/// <param name="start">The starting instant.</param>
public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>Moves the clock forward.</summary>
    /// <param name="by">How far.</param>
    public void Advance(TimeSpan by) => _now += by;
}
