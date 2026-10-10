using Ariva.Core.Queueing;
using Ariva.Infra.Live;

namespace Ariva.Infra.Streaming;

/// <summary>
/// The stream's projection of a live minute into the screens' snapshot (ARV-035): the published values only. It lives on the
/// stream's side (ARV-104g1) so that no live type takes the stream's <see cref="QueueLiveMinute"/>, which also carries the shadow
/// nowcast: the live snapshot is built from the published fields here and never receives the shadow at all (the exposure
/// tests' signature check in Ariva.UnitTests).
/// </summary>
public static class LiveMinuteSnapshots
{
    public static LiveZoneSnapshot From(QueueLiveMinute live, DateTime publishedUtc)
    {
        ArgumentNullException.ThrowIfNull(live);
        return new LiveZoneSnapshot(live.ZoneKey, live.MinuteUtc, live.QueueLength, live.LengthMeasured, live.LengthDegraded, live.NowcastMinutes,
            live.Throughput, live.NoService?.ToString(), live.NowcastDegraded, publishedUtc);
    }
}
