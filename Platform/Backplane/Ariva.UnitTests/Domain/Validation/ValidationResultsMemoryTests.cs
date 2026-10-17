using System.Diagnostics;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Queueing;
using Ariva.Core.Validation;
using Ariva.Core.Validation.Comparison;
using FluentAssertions;

namespace Ariva.UnitTests.Domain.Validation;

/// <summary>The memory test runs alone: it measures the process's managed heap, which tests running beside it would grow.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MemoryCollection
{
    public const string Name = "Ariva memory";
}

/// <summary>
/// ARV-104g, M3 of the ARV-104g2 security review (a load or memory test at the bounds): a campaign at the zone-days bound
/// (<see cref="ValidationCampaign.MaxZoneDays"/>: 50 queue zones for 8 days, 576,000 planned zone-minutes, every one with a
/// published and a shadow nowcast and a realised wait) computed the way the validation service computes it (each zone's slice with
/// its minutes, margins, shadow nowcasts and bins; the base; pooled; the results built; the stored document written) stays within
/// the memory budget one computation has on api-main (1000Mi: Proposed 300 MB of live managed heap at the peak, measured near
/// 200 MB), and its stored document is far below the 16 MB bound because it lists no minute. The peak is sampled with full
/// collections, so it is the live heap, not garbage waiting to be collected.
/// </summary>
[Collection(MemoryCollection.Name)]
public sealed class ValidationResultsMemoryTests(ITestOutputHelper output)
{
    private const int Version = 7;
    private const int Zones = 50;
    private const int Days = 8;
    private const long BudgetBytes = 300L * 1024 * 1024;

    [Fact]
    public void Computation_Should_StayWithinItsMemoryBudget_When_TheCampaignIsAtTheZoneDaysBound()
    {
        ValidationCampaign.IsWithinZoneDays(Zones, Days).Should().BeTrue();
        ValidationCampaign.IsWithinZoneDays(Zones, Days + 1).Should().BeFalse("the test runs at the bound itself");
        var start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var windows = Enumerable.Range(0, Days).Select(d => new UtcWindow(start.AddDays(d), start.AddDays(d + 1))).ToList();
        var zones = Enumerable.Range(0, Zones).Select(i => new ScopeZone(Guid.CreateVersion7(), $"Z-{i:D2}")).ToList();
        var scope = new ComparisonScope(Version, zones, [], windows);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        var baseline = GC.GetTotalMemory(forceFullCollection: true);
        long peak = 0;
        using var stop = new ManualResetEventSlim();
        var sampler = new Thread(() =>
        {
            do
                peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: true));
            while (!stop.Wait(100));
        }) { IsBackground = true };
        sampler.Start();

        var watch = Stopwatch.StartNew();
        var document = Compute(scope, start);
        watch.Stop();
        stop.Set();
        sampler.Join();

        var used = peak - baseline;
        output.WriteLine($"{Zones} zones x {Days} days: {used / (1024 * 1024)} MB live at the peak, {watch.Elapsed.TotalSeconds:F1} s, document {document.Length / 1024} KB");
        used.Should().BeLessThan(BudgetBytes, "one computation at the zone-days bound fits api-main's 1000Mi beside the host itself");
        document.Length.Should().BeLessThan(1024 * 1024, "the stored document holds summaries, never a list of minutes");
        Results(document).Nowcast.Overall.Published.Judged.Minutes.Should().BeGreaterThan(Zones * Days * 1000, "every zone and day was compared");
    }

    private static ValidationResultsViewModel Results(byte[] document) => ValidationResultsViewModel.FromJson(document);

    /// <summary>The service's flow (SvcValidationResults.CompareAsync and ComputeAsync) over generated rows instead of reads.</summary>
    private static byte[] Compute(ComparisonScope scope, DateTime start)
    {
        var slices = new List<ZoneSlice>();
        foreach (var zone in ComparisonSlices.ZonesOf(scope))
        {
            var minutes = new List<QueueMinuteRow>();
            var shadows = new List<ShadowMinuteRow>();
            for (var m = -3; m < (Days * 1440) + 3; m++)
            {
                var at = start.AddMinutes(m);
                minutes.Add(new QueueMinuteRow(zone.Name, at, Version, BinStatus.Final, 10, 4 + (m % 7), 3 + (m % 5), null, false));
                if (m is >= 0 and < Days * 1440)
                    shadows.Add(new ShadowMinuteRow(zone.Name, at, 2 + (m % 3), null, false, null));
            }

            var bins = Enumerable.Range(-1, (Days * 96) + 98)
                .Select(b => new QueueBinRow(zone.Name, start.AddMinutes(15 * b), TimeSpan.FromMinutes(15), 1, BinStatus.Final, BinQuality.Good, Version, 100, 0)).ToList();
            slices.Add(ComparisonSlices.CompareZone(new ComparisonInput
            {
                Scope = ComparisonSlices.ZoneScope(scope, zone),
                QueueMinutes = minutes,
                ShadowMinutes = shadows,
                QueueBins = bins
            }));
        }

        var pooled = ComparisonSlices.Pool(ValidationComparison.Compare(new ComparisonInput { Scope = scope }), [.. slices.Select(s => s.Result)]);
        pooled.Problem.Should().BeNull();
        var facts = new ValidationResultsViewModel.CampaignFacts(Guid.CreateVersion7(), "DMO", "Bound", ValidationCampaignStatus.Closed, Version, new string('c', 64),
            [.. Enumerable.Range(0, Days).Select(d => DateOnly.FromDateTime(start.AddDays(d)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))],
            new CampaignTargets(20, 30, true), HasDesks: false);
        var results = ValidationResultsViewModel.From(pooled, new ValidationResultsViewModel.Sources(facts, "Asia/Dubai", start.AddDays(Days), null, null, true,
            [.. slices.Where(s => s.Coverage is not null).Select(s => s.Coverage)], null, []));
        return results.ToDocument();
    }
}
