using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Ariva.LoadTests;

/// <summary>One push: its body, and the crossings it reports (entries and exits) for the run's count check.</summary>
public sealed record PilotPush(byte[] Body, int Entries, int Exits);

/// <summary>
/// A T3 sensor's push in the pilot hall (ARV-124a), as a stereo sensor reports a queue: the positions of the people it
/// sees, sampled at 5 Hz, the line crossings of the people who passed the zone's entry or exit line since its last push,
/// and the zone's occupancy as it sees it now. The people walk the zone's path, in at the entry line (x0, y 12 to 16)
/// and out across the exit line (y 22, x0 + 20 to x0 + 24), each taking <see cref="PilotSettings.DwellSeconds"/>, and keep
/// their track id for the whole walk (the crossings carry it, so the queue engine pairs each exit with its entry); the
/// next person takes the place under a new id. So Ariva.Api.Stream does a site's work: entrants, exits, waits, occupancy
/// and a bounded set of open tracks, not the ARV-071 bodies' new track at every push. Positions are the level's floor
/// coordinates in metres, as the canonical dialect sends them.
/// </summary>
public static class PilotTracks
{
    /// <summary>The walk: from just outside the entry line along its lane to x0 + 20.5 to x0 + 23.5, then across the exit line.</summary>
    private const double FirstLeg = 22.5;
    private const double SecondLeg = 8.5;
    private const double Length = FirstLeg + SecondLeg;

    /// <param name="since">The end of the device's previous push (its crossings since then are this push's).</param>
    public static PilotPush Body(PilotZone zone, int device, long packageId, DateTime since, DateTime now, int people, int samples, int dwellSeconds, int padding = 0)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var tracks = new List<object>(people * samples);
        var crossings = new List<object>();
        var occupancy = 0;
        int entries = 0, exits = 0;
        var from = Seconds(since);
        var to = Seconds(now);
        for (var p = 0; p < people; p++)
        {
            // Spread the people along the walk, and the sensors of a zone apart from one another.
            var offset = ((p * (double)dwellSeconds / people) + (device * 7.3)) % dwellSeconds;
            var lane = p % 7 * 0.5;
            // Where the walk crosses the entry line (x = x0) and the exit line (y = 22).
            var atEntry = 0.5 * FirstLeg / (21 + lane);
            var atExit = FirstLeg + ((9.5 - lane) * SecondLeg / (10 - lane));
            for (var s = 0; s < samples; s++)
            {
                var at = now.AddMilliseconds(-1000 + (s * 1000.0 / samples));
                var (generation, walked) = Walk(Seconds(at), offset, dwellSeconds);
                var (x, y) = Position(zone, walked, lane);
                tracks.Add(new
                {
                    trackId = TrackId(p, generation),
                    x = Math.Round(x, 2),
                    y = Math.Round(y, 2),
                    heightMetres = 1.7,
                    timeUtc = at.ToString("O", CultureInfo.InvariantCulture)
                });
            }

            foreach (var (line, direction, walkedAt) in new[] { ("entry", "In", atEntry), ("exit", "Out", atExit) })
            {
                // The one time in (since, now] the walk is at that point, if any (a walk takes minutes, a push a second).
                var k = Math.Ceiling((from + offset - (walkedAt / Length * dwellSeconds)) / dwellSeconds);
                var crossedAt = (k * dwellSeconds) + (walkedAt / Length * dwellSeconds) - offset;
                if (crossedAt <= from || crossedAt > to)
                    continue;
                crossings.Add(new
                {
                    lineName = zone.Name + " " + line,
                    direction,
                    trackId = TrackId(p, (long)k),
                    timeUtc = DateTime.UnixEpoch.AddTicks((long)(crossedAt * TimeSpan.TicksPerSecond)).ToString("O", CultureInfo.InvariantCulture)
                });
                if (direction == "In")
                    entries++;
                else
                    exits++;
            }

            var (_, now0) = Walk(to, offset, dwellSeconds);
            if (now0 > atEntry && now0 < atExit)
                occupancy++;
        }

        var body = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["sentUtc"] = now.ToString("O", CultureInfo.InvariantCulture),
            ["packageId"] = packageId,
            ["tracks"] = tracks,
            ["occupancy"] = new[] { new { zoneName = zone.Name, count = occupancy, timeUtc = now.ToString("O", CultureInfo.InvariantCulture) } }
        };
        if (crossings.Count > 0)
            body["crossings"] = crossings;
        var json = JsonSerializer.SerializeToUtf8Bytes(body);
        if (padding <= 0)
            return new PilotPush(json, entries, exits);
        // An oversized body: the same push with a long, valid JSON string member appended (it never gets parsed).
        var text = Encoding.UTF8.GetString(json);
        return new PilotPush(Encoding.UTF8.GetBytes(text[..^1] + ",\"padding\":\"" + new string('x', padding) + "\"}"), 0, 0);
    }

    private static double Seconds(DateTime at) => (at - DateTime.UnixEpoch).Ticks / (double)TimeSpan.TicksPerSecond;

    private static string TrackId(int person, long generation) => LoadSettings.Invariant($"{person}.{generation % 1_000_000}");

    /// <summary>The walk a person is on at <paramref name="clock"/> (seconds since the epoch) and how far along it, in metres.</summary>
    private static (long Generation, double Walked) Walk(double clock, double offset, int dwellSeconds)
    {
        var shifted = clock + offset;
        var generation = (long)Math.Floor(shifted / dwellSeconds);
        return (generation, (shifted - (generation * (double)dwellSeconds)) / dwellSeconds * Length);
    }

    private static (double X, double Y) Position(PilotZone zone, double walked, double lane) =>
        walked < FirstLeg
            ? (zone.X0 - 0.5 + (walked * (21 + lane) / FirstLeg), 12.5 + lane)
            : (zone.X0 + 20.5 + lane, 12.5 + lane + ((walked - FirstLeg) * (10 - lane) / SecondLeg));
}
