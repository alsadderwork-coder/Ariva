using System.Reflection;
using Ariva.Api.Cronz.Jobs;
using FluentAssertions;
using TickerQ.Utilities.Base;

namespace Ariva.UnitTests.Jobs;

/// <summary>
/// ARV-124a: TickerQ 10.0.2 (in memory, ADR-0020) starts a cron job's next occurrence while the previous run is still going,
/// skips occurrences missed while the process was stopped, and now and then fires one occurrence twice, 1 to 10 ms apart,
/// whether or not runs overlap (measured on a lab machine: a 5-second job every 2 seconds ran three to five at once; 13 of
/// 118 occurrences fired twice, with a 5-second job and with a 100 ms one; wiki 05). Every job must therefore tolerate a
/// concurrent copy of itself on the same replica, as it already must across replicas. Each job here was reviewed for it; a
/// new job fails this test until it is reviewed and added with its guard.
/// </summary>
public sealed class JobConcurrencyTests
{
    /// <summary>Every job function with what makes a concurrent copy of itself harmless.</summary>
    private static readonly Dictionary<string, string> Reviewed = new(StringComparer.Ordinal)
    {
        [AvailabilityJobs.Ledger] = "site advisory lock (class 50), skipped when held; minutes inserted ON CONFLICT DO NOTHING",
        [ReportJobs.Deliveries] = "preparation under a transaction advisory lock (skipped when held), deliveries unique per schedule, day and recipient; each delivery claimed FOR UPDATE SKIP LOCKED with its status checked again, so a sent one is never sent again; a failed one may be tried by both copies, within MaxAttempts",
        [StaffingJobs.Recommendations] = "site advisory lock (class 51), skipped when held; lane minutes inserted ON CONFLICT DO NOTHING",
        [RetentionJobs.Purge] = "session advisory lock (class 52), skipped when held",
        [SecretJobs.Reprotect] = "compare-and-swap UPDATE of each value under an older key",
        [TimescaleJobs.PolicyJobs] = "reads only; each check's report replaces the last, so two copies report the same counts"
    };

    [Fact]
    public void EveryCronzJob_Should_BeReviewedForAConcurrentCopyOfItself_When_TickerQSchedulesIt()
    {
        var functions = typeof(StaffingJobs).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            .SelectMany(m => m.CustomAttributes.Where(a => a.AttributeType == typeof(TickerFunctionAttribute)))
            .Select(a => (string)a.ConstructorArguments[0].Value)
            .ToList();

        functions.Should().NotBeEmpty();
        functions.Should().OnlyHaveUniqueItems();
        functions.Should().BeEquivalentTo(Reviewed.Keys,
            "TickerQ runs a job again while it runs and may fire one occurrence twice (ARV-124a): a job is listed here once a concurrent copy of it is harmless");
        Reviewed.Values.Should().OnlyContain(guard => guard.Length > 10);
    }
}
