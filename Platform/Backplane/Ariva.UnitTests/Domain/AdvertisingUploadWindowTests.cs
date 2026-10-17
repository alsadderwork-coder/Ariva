using Ariva.Infra.Services.Displays;
using FluentAssertions;

namespace Ariva.UnitTests.Domain;

/// <summary>
/// ARV-142a (security review, CWE-400): each account may attempt at most 30 advertisement uploads in 10 minutes, counted
/// before the file is read; another account has its own window, and a new window starts once the old one has ended.
/// </summary>
public sealed class AdvertisingUploadWindowTests
{
    [Fact]
    public void TryEnter_Should_RefuseTheThirtyFirstAttempt_When_TheyFallInOneWindow()
    {
        var clock = new Clock(new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.Zero));
        var window = new AdvertisingUploadWindow(clock);
        var account = Guid.NewGuid();

        Enumerable.Range(0, AdvertisingUploadWindow.MaxAttempts).Select(_ => window.TryEnter(account)).ToList().Should().AllBeEquivalentTo(true);
        window.TryEnter(account).Should().BeFalse("the 31st attempt in 10 minutes");
        window.TryEnter(Guid.NewGuid()).Should().BeTrue("another account has its own window");

        clock.Advance(AdvertisingUploadWindow.Window - TimeSpan.FromSeconds(1));
        window.TryEnter(account).Should().BeFalse("still the same window");
        clock.Advance(TimeSpan.FromSeconds(1));
        window.TryEnter(account).Should().BeTrue("a new window");
    }

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
