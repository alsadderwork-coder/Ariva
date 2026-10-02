using Ariva.Core.Sensing;

namespace Ariva.Core.Queueing;

/// <summary>
/// The checks a zone snapshot passes before it is restored (CWE-501: a snapshot comes back from storage and is not
/// trusted): it belongs to the expected zone, geometry and profile version, every count is non-negative and consistent,
/// and every time is either unset (the calendar's start, for an engine that never stepped) or plausible and not after
/// the host's clock plus a day. The engines' own Restore methods add their bounds (sizes, alignment).
/// </summary>
public static class SnapshotChecks
{
    public static IEnumerable<string> Of(ZoneProcessorState state, string zoneKey, string queueZone, int profileVersion, DateTime notAfterUtc)
    {
        if (state is null)
        {
            yield return "The snapshot is empty.";
            yield break;
        }

        if (state.Version is < 1 or > ZoneProcessorState.CurrentVersion)
            yield return $"Version {state.Version} is not 1 to {ZoneProcessorState.CurrentVersion}.";
        if (!string.Equals(state.ZoneKey, zoneKey, StringComparison.Ordinal))
            yield return "The snapshot is for another zone.";
        if (state.ProfileVersion != profileVersion || profileVersion < 0)
            yield return "The snapshot is for another profile version.";
        if (state.Engine is null || state.Bins is null)
        {
            yield return "The engine or bin state is missing.";
            yield break;
        }

        if (!string.Equals(state.Engine.QueueZone, queueZone, StringComparison.Ordinal) || !string.Equals(state.Bins.QueueZone, queueZone, StringComparison.Ordinal))
            yield return "The engine or bin state is for another queue zone.";
        if (state.Bins.ProfileVersion != profileVersion)
            yield return "The bin state is for another profile version.";

        var latest = notAfterUtc < ZoneProcessor.LatestUtc ? notAfterUtc.AddDays(1) : ZoneProcessor.LatestUtc;
        var bad = 0;
        void Time(DateTime t)
        {
            if (t != DateTime.MinValue && (t < ZoneProcessor.EarliestUtc || t > latest))
                bad++;
        }

        void Times(DateTime? t)
        {
            if (t is { } value)
                Time(value);
        }

        // Counts are non-negative and far below the 64-bit edge, so that sums of them cannot wrap.
        void Count(long n)
        {
            if (n is < 0 or > MaxCount)
                bad++;
        }

        if (state.ReferenceUtc != DateTime.MinValue && (state.ReferenceUtc < ZoneProcessor.EarliestUtc || state.ReferenceUtc > notAfterUtc))
            yield return "The reference time is not plausible.";
        Times(state.LastLiveMinuteUtc);
        if (state.LastLiveMinuteUtc is { } live && live > state.ReferenceUtc)
            bad++;

        var e = state.Engine;
        Time(e.WatermarkUtc);
        Time(e.CursorUtc);
        Times(e.EarliestLateUtc);
        if (e.WatermarkUtc > state.ReferenceUtc && state.ReferenceUtc != DateTime.MinValue)
            bad++;
        Count(e.Sequence);
        Count(e.Order);
        Count(e.Reanchors);
        Count(e.BeyondHorizon);
        foreach (var c in e.Counters ?? [])
            Count(c);
        foreach (var b in e.Buffer ?? [])
        {
            if (b?.Input is null || b.Sequence < 0 || b.Sequence >= e.Sequence)
                bad++;
            else
            {
                Time(b.Input.TimeUtc);
                Times(b.Input.FromUtc);
                if (b.Input.In < 0 || b.Input.Out < 0 || b.Input.Count < 0)
                    bad++;
            }
        }

        foreach (var h in e.Held ?? [])
        {
            if (h is null || h.Order < 0 || h.Order >= e.Order)
                bad++;
            else
            {
                Time(h.EntryUtc);
                Times(h.LastSeenUtc);
            }
        }

        foreach (var o in e.Occupancy ?? [])
        {
            if (o is null || o.Count < 0)
                bad++;
            else
                Time(o.AtUtc);
        }

        foreach (var h in e.Handovers ?? [])
        {
            if (h is null)
                bad++;
            else
            {
                Time(h.Seen);
                Time(h.Deadline);
            }
        }

        foreach (var m in e.Movements ?? [])
        {
            if (m is null)
                bad++;
            else
            {
                Time(m.MinuteUtc);
                Count(m.Entries);
                Count(m.Exits);
                Count(m.DegradedEntries);
                Count(m.DegradedExits);
            }
        }

        foreach (var w in e.Waits ?? [])
        {
            if (w is null || w.ExitUtc < w.EntryUtc)
                bad++;
            else
            {
                Time(w.EntryUtc);
                Time(w.ExitUtc);
            }
        }

        foreach (var r in e.Resolutions ?? [])
        {
            if (r is null)
                bad++;
            else
            {
                Time(r.EntryUtc);
                Time(r.ResolvedUtc);
            }
        }

        foreach (var l in e.LateByMinute ?? [])
        {
            if (l is null)
                bad++;
            else
            {
                Time(l.MinuteUtc);
                Count(l.Count);
            }
        }

        var bins = state.Bins;
        Time(bins.WatermarkUtc);
        Times(bins.NextBinUtc);
        Times(bins.FinalHighUtc);
        foreach (var bin in bins.Open ?? [])
        {
            if (bin?.Total is null)
            {
                bad++;
                continue;
            }

            Time(bin.StartUtc);
            bad += Tally(bin.Total);
            foreach (var m in bin.Minutes ?? [])
            {
                if (m?.Tally is null || m.MinuteUtc < bin.StartUtc)
                    bad++;
                else
                {
                    Time(m.MinuteUtc);
                    bad += Tally(m.Tally);
                }
            }
        }

        foreach (var m in bins.Marks ?? [])
        {
            if (m is null || m.ToUtc < m.FromUtc || !Enum.IsDefined(m.Quality))
                bad++;
            else
            {
                Time(m.FromUtc);
                Time(m.ToUtc);
            }
        }

        foreach (var x in state.Exits ?? [])
        {
            if (x is null)
                bad++;
            else
            {
                Time(x.MinuteUtc);
                Count(x.Exits);
                Count(x.Degraded);
            }
        }

        // Device liveness (ARV-036): codes as Ingest writes them, times plausible, an outage starting no later than the
        // device was last heard, a bounded list.
        // Device times are always set: unlike the engines' "never stepped" start, none of them may be the calendar's start.
        void Set(DateTime t)
        {
            if (t == DateTime.MinValue)
                bad++;
            else
                Time(t);
        }

        if ((state.Devices?.Count ?? 0) > MaxDevices || (state.RecentOutages?.Count ?? 0) > MaxDevices * 4)
        {
            yield return "The snapshot tracks more devices or outages than a zone keeps.";
            yield break;
        }

        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in state.Devices ?? [])
        {
            if (d is null || !DeviceCodes.IsValid(d.DeviceCode) || !codes.Add(d.DeviceCode) || d.OutSinceUtc > d.LastHeardUtc || d.MarkedThroughUtc < d.OutSinceUtc)
                bad++;
            else
            {
                Set(d.LastHeardUtc);
                if (d.OutSinceUtc is { } o)
                    Set(o);
                if (d.MarkedThroughUtc is { } m)
                    Set(m);
            }
        }

        foreach (var r in state.RecentOutages ?? [])
        {
            if (r is null || !DeviceCodes.IsValid(r.DeviceCode) || r.ToUtc <= r.FromUtc)
                bad++;
            else
            {
                Set(r.FromUtc);
                Set(r.ToUtc);
            }
        }

        if (bad > 0)
            yield return $"{bad} values of the snapshot are not plausible (negative counts, implausible times or empty items).";
    }

    /// <summary>Devices a snapshot may track at most (the largest <see cref="ZoneProcessorSettings.MaxDevices"/>).</summary>
    public const int MaxDevices = 4_096;

    /// <summary>The largest count a snapshot may hold (a trillion people or events).</summary>
    public const long MaxCount = 1_000_000_000_000;

    private static int Tally(BinTallyState t)
    {
        var bad = new[] { t.Entries, t.Exits, t.Abandoned, t.Fragmented, t.Censored, t.Reanchored, t.Rejected, t.Resolved, t.LateEvents, t.Waits, t.WithinTarget }
            .Count(n => n is < 0 or > MaxCount);
        if (t.WithinTarget > t.Waits || !double.IsFinite(t.Sum) || t.Sum < 0 || t.Sum > MaxCount)
            bad++;
        if ((t.Exact ?? []).Any(w => !double.IsFinite(w) || w < 0))
            bad++;
        var buckets = new HashSet<int>();
        foreach (var h in t.Histogram ?? [])
        {
            if (h is null || h.Bucket is < 0 or > 1_000_000 || h.Count is < 0 or > MaxCount || !buckets.Add(h.Bucket))
                bad++;
        }

        if ((t.Histogram ?? []).Where(h => h is not null && h.Count > 0).Sum(h => (decimal)h.Count) > MaxCount)
            bad++;
        return bad;
    }
}
