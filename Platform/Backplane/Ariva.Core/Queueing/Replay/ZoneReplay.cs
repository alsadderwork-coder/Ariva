using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ariva.Core.Queueing.Replay;

/// <summary>
/// Golden replay of one queue zone (ARV-036): the archived records of a range, in the stream's order, through a fresh
/// <see cref="ZoneProcessor"/> under a given zone profile version and settings. Every input and every output goes
/// through the <see cref="ReplayLedger"/> in a fixed order, so the same records always give the same output hash. The
/// zone starts empty at the range's start (people already queueing then count from their first crossing), and at the
/// end the engine settles without watching devices, so every bin before the end becomes final and no outage is invented
/// after it. Pure: no I/O and no clock.
/// </summary>
public static class ZoneReplay
{
    /// <summary>The extra time after the range's end the engine is stepped to, so that the range's bins become final.</summary>
    public static TimeSpan Settle(ZoneProcessorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Engine.CensorAfter + settings.Engine.Lateness + settings.Bins.BinLength + TimeSpan.FromMinutes(1);
    }

    /// <summary>SHA-256 of the settings as JSON: a replay under other settings is a different replay.</summary>
    public static string SettingsHash(ZoneProcessorSettings settings) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(settings ?? new ZoneProcessorSettings(), ReplayLedger.Json))));

    /// <summary>Replays the zone's <paramref name="inputs"/> (already in order) into <paramref name="ledger"/>; returns the zone's counters.</summary>
    public static ZoneProcessorCounters Run(ReplayLedger ledger, string zoneKey, QueueZoneGeometry geometry, int profileVersion, ZoneProcessorSettings settings,
        IEnumerable<ReplayInput> inputs, DateTime fromUtc, DateTime toUtc)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(inputs);
        settings ??= new ZoneProcessorSettings();
        if (fromUtc.Kind != DateTimeKind.Utc || toUtc.Kind != DateTimeKind.Utc || toUtc <= fromUtc)
            throw new ArgumentOutOfRangeException(nameof(toUtc), "A replay covers a UTC range, from before to.");
        var zone = new ZoneProcessor(zoneKey, geometry, profileVersion, settings);
        // A receive time beyond the range (a clock fault the archive let through) cannot move the zone past its end.
        var cap = toUtc;
        foreach (var input in inputs)
        {
            if (!string.Equals(input.ZoneKey, zoneKey, StringComparison.Ordinal))
                throw new ArgumentException($"An input is for {input.ZoneKey}, not {zoneKey}.", nameof(inputs));
            ledger.Input(zoneKey, input);
            try
            {
                zone.Offer(input.ToBatch(), cap);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or OverflowException or InvalidDataException)
            {
                // The stream dead-letters a batch the engine refuses; the replay records the same and goes on.
                ledger.Output(zoneKey, "refused", new { input.Id, Reason = e.GetType().Name });
            }

            if (zone.Full)
                ledger.Outputs(zone.Drain());
        }

        zone.Finish(toUtc, toUtc + Settle(settings));
        ledger.Outputs(zone.Drain());
        return zone.Counters;
    }
}
