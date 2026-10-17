using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ariva.Core.Domain.Enums;

namespace Ariva.Core.Domain.Components;

/// <summary>
/// One tracer run of a batch as the capturing device recorded it (ARV-104b), after the request rules: the queue zone, the
/// campaign's tracer label, the join and exit times on the device's own clock (UTC, to the millisecond) and whether the
/// tracer left the queue without being served.
/// </summary>
public sealed record TracerRunInput(Guid ZoneId, string TracerCode, DateTime JoinedRawUtc, DateTime ExitedRawUtc, bool Abandoned);

/// <summary>
/// One desk's minutes in a desk observation batch (ARV-104b): the state the observer saw in each minute of the 15-minute bin,
/// in order from the bin's first minute; null for a minute not observed.
/// </summary>
public sealed record DeskMinutesInput(Guid DeskId, IReadOnlyList<ObservedDeskState?> States);

/// <summary>
/// The fingerprint of a capture batch (ARV-104b): SHA-256 over a canonical text of what the batch asks for, so a resent batch
/// with the same Idempotency-Key is recognised as the same request (the stored batch is the answer) or as another one (409).
/// The device's clock reading is left out on purpose: a retry reads the clock again.
/// </summary>
public static class RequestFingerprint
{
    /// <summary>Lower-case hex SHA-256 of the lines joined by line feeds.</summary>
    public static string Of(IEnumerable<string> lines) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines ?? []))));

    /// <summary>A tracer batch: the campaign, then each run's zone, code, device times (to the millisecond) and flag, in the order sent.</summary>
    public static string OfTracerRuns(Guid campaignId, IReadOnlyList<TracerRunInput> runs) =>
        Of([
            "tracer-runs/1", campaignId.ToString("D"),
            .. (runs ?? []).Select(r => string.Create(CultureInfo.InvariantCulture,
                $"{r.ZoneId:D}|{r.TracerCode}|{Instant(r.JoinedRawUtc)}|{Instant(r.ExitedRawUtc)}|{(r.Abandoned ? 1 : 0)}"))
        ]);

    /// <summary>A desk batch: the campaign and bin, then each desk with its 15 states (empty for a minute not observed), in the order sent.</summary>
    public static string OfDeskMinutes(Guid campaignId, DateTime binStartUtc, IReadOnlyList<DeskMinutesInput> desks) =>
        Of([
            "desk-observations/1", campaignId.ToString("D"), Instant(binStartUtc),
            .. (desks ?? []).Select(d => $"{d.DeskId:D}|{string.Join(',', (d.States ?? []).Select(s => s?.ToString() ?? string.Empty))}")
        ]);

    private static string Instant(DateTime value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
