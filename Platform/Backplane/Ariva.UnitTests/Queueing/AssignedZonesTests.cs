using Ariva.Infra.Streaming;
using Ariva.UnitTests.Setup;
using FluentAssertions;

namespace Ariva.UnitTests.Queueing;

/// <summary>
/// ARV-274: on an assignment the queue stream worker restores the zones of its partitions that have a saved state, so a zone whose
/// devices all stay silent is held without a record of it. Which zones are its own: those whose key hashes to an assigned partition
/// under the topics' partition count now (where their records go), keys checked as data from storage (CWE-501).
/// </summary>
public sealed class AssignedZonesTests
{
    private static readonly string[] Keys = [.. Enumerable.Range(0, 40).Select(i => $"DMO/Q{i}")];

    [Fact]
    public void ZonesOf_Should_KeepOnlyTheZonesOfTheAssignedPartitions_When_TheirKeysHashThere()
    {
        var stored = Keys.Select((k, i) => (k, 10 + i)).ToList();

        var zones = QueueStreamWorker.ZonesOf(stored, new HashSet<int> { 0, 2 }, 3, out var malformed);

        zones.Should().NotBeEmpty().And.OnlyContain(z => z.Partition == QueueStreamWorker.ExpectedPartition(z.ZoneKey, 3) && (z.Partition == 0 || z.Partition == 2));
        zones.Select(z => z.ZoneKey).Should().Equal(Keys.Where(k => QueueStreamWorker.ExpectedPartition(k, 3) != 1), "every other zone, in the order listed");
        zones.Should().OnlyContain(z => z.ProfileVersion == 10 + Array.IndexOf(Keys, z.ZoneKey), "each with the version it was saved under");
        malformed.Should().Be(0);
        QueueStreamWorker.ZonesOf(stored, new HashSet<int> { 1 }, 3, out _).Select(z => z.ZoneKey).Should().BeEquivalentTo(Keys.Where(k => QueueStreamWorker.ExpectedPartition(k, 3) == 1));
        QueueStreamWorker.ZonesOf(stored, new HashSet<int>(), 3, out _).Should().BeEmpty("no partition, no zone");
        QueueStreamWorker.ZonesOf(stored, new HashSet<int> { 0 }, 1, out _).Should().HaveCount(Keys.Length, "one partition holds every zone");
    }

    [Fact]
    public void ZonesOf_Should_HashUnderTheCountGiven_When_TheTopicsGrew()
    {
        // After a scale-out a zone's new records go to its partition under the new count; that partition's owner restores it.
        var moved = Keys.First(k => QueueStreamWorker.ExpectedPartition(k, 3) != QueueStreamWorker.ExpectedPartition(k, 6));

        QueueStreamWorker.ZonesOf([(moved, 1)], new HashSet<int> { QueueStreamWorker.ExpectedPartition(moved, 6) }, 6, out _).Should().ContainSingle();
        QueueStreamWorker.ZonesOf([(moved, 1)], new HashSet<int> { QueueStreamWorker.ExpectedPartition(moved, 3) }, 6, out _).Should().BeEmpty();
    }

    public static TheoryData<string, string> Malformed => new()
    {
        { "no slash", "DMOQ1" },
        { "no site code", "/Q1" },
        { "no zone name", "DMO/" },
        { "a site code in lower case", "dmo/Q1" },
        { "a site code too long", "ABCDEFGHIJ/Q1" },
        { "a right-to-left override in the name", "DMO/Q1\u202E" },
        { "a line feed in the name", "DMO/Q1\nQ2" },
        { "a key longer than a message key", "DMO/" + new string('Q', 197) },
        { "nothing", "" }
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public void ZonesOf_Should_LeaveOutAKey_When_ItIsNotAZoneKey(string name, string key)
    {
        QueueStreamWorker.TrySplitZoneKey(key, out var site, out var zone).Should().BeFalse(name);
        (site, zone).Should().Be((null, null), name);
        QueueStreamWorker.ZonesOf([(key, 1), ("DMO/Q1", 2)], new HashSet<int> { 0 }, 1, out var malformed).Should().ContainSingle(name)
            .Which.Should().Be(("DMO/Q1", 0, 2), "the zone keys around it are kept");
        malformed.Should().Be(1, "counted for the summary of the restores");
    }

    [Fact]
    public void TrySplitZoneKey_Should_SplitAtTheFirstSlash_When_TheNameHoldsOne()
    {
        QueueStreamWorker.TrySplitZoneKey("DMO/Arrivals/Visitors", out var site, out var zone).Should().BeTrue();
        (site, zone).Should().Be(("DMO", "Arrivals/Visitors"));
        QueueStreamWorker.TrySplitZoneKey("AUH-TA/A-VIS", out site, out zone).Should().BeTrue();
        (site, zone).Should().Be(("AUH-TA", "A-VIS"));
        QueueStreamWorker.TrySplitZoneKey(null, out _, out _).Should().BeFalse();
        QueueStreamWorker.TrySplitZoneKey("DMO/" + new string('Q', 196), out _, out _).Should().BeTrue("a key of 200 characters fits");
    }

    [Fact]
    public void ChooseRestore_Should_RestoreOnlyAZoneOfItsPublishedVersion_When_ItsGeometryIsLoaded()
    {
        // A zone retired from the profile has no published geometry: not restored. One saved under another version starts afresh with
        // its first record, as it always has; one saved under the published version is restored.
        var geometry = new ZoneGeometry(new Ariva.Core.Queueing.QueueZoneGeometry("Q1", new HashSet<string> { "Q1 entry" }, new HashSet<string> { "Q1 exit" },
            new HashSet<string>(), new HashSet<string>()), 7);

        QueueStreamWorker.ChooseRestore(null, 7).Should().Be(QueueStreamWorker.RestoreOutcome.NotPublished);
        QueueStreamWorker.ChooseRestore(geometry, 6).Should().Be(QueueStreamWorker.RestoreOutcome.OtherVersion);
        QueueStreamWorker.ChooseRestore(geometry, 8).Should().Be(QueueStreamWorker.RestoreOutcome.OtherVersion, "a version ahead of the published one is not its own either");
        QueueStreamWorker.ChooseRestore(geometry, 7).Should().Be(QueueStreamWorker.RestoreOutcome.Restored);
    }

    [Fact]
    public void ZonesOf_Should_Refuse_When_ThereIsNoPartition()
    {
        var act = () => QueueStreamWorker.ZonesOf([], new HashSet<int>(), 0, out _);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(new[] { 0, 1, 2 }, 3, true)]
    [InlineData(new[] { 2 }, 3, true)]
    [InlineData(new[] { 0, 3 }, 3, false)]
    [InlineData(new[] { 4 }, 3, false)]
    [InlineData(new[] { -1 }, 3, false)]
    [InlineData(new[] { 0 }, 0, false)]
    [InlineData(new int[0], 3, false)]
    public void CanList_Should_ListOnlyPartitionsBelowTheCountReadOnTheAssignment_When_AskedForThem(int[] partitions, int count, bool expected)
    {
        // CWE-501: a partition at or past the count read from the brokers, or no count read yet, means which zones go to it now is not
        // known here: nothing is listed, and those zones are restored when a record of them arrives.
        QueueStreamWorker.CanList(partitions, count).Should().Be(expected);
    }

    [Fact]
    public void Listed_Should_CutToTheMaximum_When_TheStoreReturnsOneRowMore()
    {
        (string, int)[] three = [("DMO/Q1", 1), ("DMO/Q2", 1), ("DMO/Q3", 1)];

        QueueStreamWorker.Listed(three, 2, out var cut).Select(z => z.ZoneKey).Should().Equal("DMO/Q1", "DMO/Q2");
        cut.Should().BeTrue("the store reads the maximum and one more, so a cut is seen");
        QueueStreamWorker.Listed(three, 3, out cut).Should().HaveCount(3);
        cut.Should().BeFalse();
    }

    private static int[] Tally() => new int[Enum.GetValues<QueueStreamWorker.RestoreOutcome>().Length];

    [Fact]
    public async Task DrainAsync_Should_RestoreAtMostItsShareAPass_When_ManyZonesWait()
    {
        var queue = new Queue<int>(Enumerable.Range(0, 120));
        var tally = Tally();
        var seen = new List<int>();

        await QueueStreamWorker.DrainAsync(queue, QueueStreamWorker.RestoresPerPass, () => true, z => { seen.Add(z); return Task.FromResult(QueueStreamWorker.RestoreOutcome.Restored); }, tally);

        seen.Should().Equal(Enumerable.Range(0, 50), "50 a pass, in the order listed");
        queue.Should().HaveCount(70);
        tally[(int)QueueStreamWorker.RestoreOutcome.Restored].Should().Be(50);
    }

    [Fact]
    public async Task DrainAsync_Should_StopStartingZones_When_ThePassBudgetIsSpent()
    {
        var queue = new Queue<int>(Enumerable.Range(0, 10));
        var tally = Tally();
        var budget = 3;

        await QueueStreamWorker.DrainAsync(queue, 50, () => budget-- > 0, _ => Task.FromResult(QueueStreamWorker.RestoreOutcome.NotPublished), tally);

        queue.Should().HaveCount(7, "no zone is started once the pass's time is spent");
        tally[(int)QueueStreamWorker.RestoreOutcome.NotPublished].Should().Be(3);
    }

    [Fact]
    public async Task DrainAsync_Should_KeepAZoneFirst_When_TheDatabaseDoesNotAnswerForIt()
    {
        var queue = new Queue<int>([1, 2, 3]);
        var tally = Tally();
        Task<QueueStreamWorker.RestoreOutcome> FailOn2(int z) =>
            z == 2 ? throw new TimeoutException("the database did not answer") : Task.FromResult(QueueStreamWorker.RestoreOutcome.Restored);

        var act = () => QueueStreamWorker.DrainAsync(queue, 50, () => true, FailOn2, tally);

        await act.Should().ThrowAsync<TimeoutException>();
        queue.Should().Equal([2, 3], "a zone is taken off the queue once its outcome is known");
        tally[(int)QueueStreamWorker.RestoreOutcome.Restored].Should().Be(1);

        await QueueStreamWorker.DrainAsync(queue, 50, () => true, _ => Task.FromResult(QueueStreamWorker.RestoreOutcome.Restored), tally);
        queue.Should().BeEmpty();
        tally[(int)QueueStreamWorker.RestoreOutcome.Restored].Should().Be(3, "the next attempt restores it");
    }

    [Fact]
    public async Task DrainAsync_Should_EndTheQueueAndTallyEveryZoneLeft_When_TheFirstZoneIsBeyondMaxZones()
    {
        var queue = new Queue<int>([1, 2, 3, 4, 5]);
        var tally = Tally();

        await QueueStreamWorker.DrainAsync(queue, 50, () => true,
            z => Task.FromResult(z < 3 ? QueueStreamWorker.RestoreOutcome.Restored : z == 3 ? QueueStreamWorker.RestoreOutcome.Full : QueueStreamWorker.RestoreOutcome.Restored), tally);

        queue.Should().BeEmpty();
        tally[(int)QueueStreamWorker.RestoreOutcome.Restored].Should().Be(2);
        tally[(int)QueueStreamWorker.RestoreOutcome.Full].Should().Be(3, "the zone met at the bound and the two after it");
    }

    [Fact]
    public void RestoreRetry_Should_WaitAfterEachFailureAndGiveUpAtTheThird_When_TheDatabaseDoesNotAnswer()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
        var retry = new RestoreRetry(clock, QueueStreamWorker.RestoreRetryAfter, QueueStreamWorker.RestoreAttempts);
        retry.Due.Should().BeTrue();

        retry.Failed().Should().BeFalse("attempt 1 of 3");
        retry.Failures.Should().Be(1);
        retry.Due.Should().BeFalse();
        clock.Advance(TimeSpan.FromSeconds(59));
        retry.Due.Should().BeFalse("a minute between attempts");
        clock.Advance(TimeSpan.FromSeconds(1));
        retry.Due.Should().BeTrue();

        retry.Failed().Should().BeFalse("attempt 2 of 3");
        clock.Advance(QueueStreamWorker.RestoreRetryAfter);
        retry.Failed().Should().BeTrue("the third failure in a row gives up");
        retry.Failures.Should().Be(0, "and starts afresh");
        retry.Due.Should().BeTrue("the next assignment's restores do not wait");
    }

    [Fact]
    public void RestoreRetry_Should_CountFailuresInARowOnly_When_APassSucceedsBetween()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
        var retry = new RestoreRetry(clock, TimeSpan.FromSeconds(60), 3);

        retry.Failed().Should().BeFalse();
        clock.Advance(TimeSpan.FromSeconds(60));
        retry.Failed().Should().BeFalse();
        retry.Succeeded();
        retry.Failures.Should().Be(0);
        clock.Advance(TimeSpan.FromSeconds(60));
        retry.Failed().Should().BeFalse("two failures, but not in a row");
        retry.Failed().Should().BeFalse();
        retry.Failed().Should().BeTrue();

        retry.Failed().Should().BeFalse("after giving up it starts afresh");
        retry.Reset();
        retry.Failures.Should().Be(0);
        retry.Due.Should().BeTrue("a reset ends the wait (the assignment's partitions were taken away)");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RestoreRetry_Should_Refuse_When_ItWouldNeverTry(int attempts)
    {
        var act = () => new RestoreRetry(TimeProvider.System, TimeSpan.FromSeconds(60), attempts);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void RestoreRetry_Should_Refuse_When_ItHasNoClockOrANegativeWait()
    {
        ((Action)(() => _ = new RestoreRetry(null, TimeSpan.FromSeconds(60), 3))).Should().Throw<ArgumentNullException>();
        ((Action)(() => _ = new RestoreRetry(TimeProvider.System, TimeSpan.FromSeconds(-1), 3))).Should().Throw<ArgumentOutOfRangeException>();
    }
}
