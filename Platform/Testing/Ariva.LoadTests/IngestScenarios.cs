using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Ariva.LoadTests;

/// <summary>
/// Sensor pushes (ARV-071): each device posts canonical track batches to its own zone at a vendor rate (a T3 sensor
/// sends its tracks about once a second: 30 people at 5 Hz is 150 positions a push), then three times that rate as a
/// burst, with a device far past its rate during the burst; then bodies over the limit: bodies over 256 KB must get 413 and a device past its rate 429 with Retry-After, never a 5xx.
/// </summary>
public sealed class IngestScenarios(LoadSettings settings, HttpClient http)
{
    private Uri Events => new(settings.Ingest, $"api/v1/ingest/zones/{Uri.EscapeDataString(settings.ZoneName)}/events");

    /// <summary>One push: <c>people</c> tracks walking through the zone (10 to 34 x 10 to 22 m), <c>samples</c> positions each.</summary>
    public byte[] Body(long packageId, int people, int samples, int padding = 0)
    {
        var now = DateTime.UtcNow;
        var tracks = new List<object>(people * samples);
        for (var p = 0; p < people; p++)
        {
            for (var s = 0; s < samples; s++)
            {
                var at = now.AddMilliseconds(-1000 + (s * 1000.0 / samples));
                tracks.Add(new
                {
                    trackId = Invariant($"{packageId % 1000}.{p}"),
                    x = Math.Round(11 + (((p * 0.7) + (s * 0.1)) % 22.0), 2),
                    y = 11.0 + (p % 10),
                    heightMetres = 1.7,
                    timeUtc = at.ToString("O", CultureInfo.InvariantCulture)
                });
            }
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(new { sentUtc = now.ToString("O", CultureInfo.InvariantCulture), packageId, tracks });
        if (padding <= 0)
            return json;
        // An oversized body: the same push with a long, valid JSON string member appended (it never gets parsed).
        var text = Encoding.UTF8.GetString(json);
        return Encoding.UTF8.GetBytes(text[..^1] + ",\"padding\":\"" + new string('x', padding) + "\"}");
    }

    private static string Invariant(FormattableString value) => LoadSettings.Invariant(value);

    private async Task PushAsync(Measure measure, string key, byte[] body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Events) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add("X-Ariva-Device-Key", key);
        var clock = Stopwatch.StartNew();
        try
        {
            using var response = await http.SendAsync(request, ct);
            measure.Record(clock.Elapsed.TotalMilliseconds, response.StatusCode, body.Length);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && response.Headers.RetryAfter is null)
                measure.Record(0, "429 without Retry-After");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            measure.Record(clock.Elapsed.TotalMilliseconds, "error " + e.GetType().Name);
        }
    }

    /// <summary>Every device pushing at <paramref name="perDevice"/> a second for <paramref name="seconds"/>.</summary>
    public async Task<Measure> PushesAsync(string name, double perDevice, int seconds, CancellationToken ct)
    {
        var measure = new Measure(name);
        var keys = settings.DeviceKeys;
        measure.Start();
        await Inject.RunAsync(perDevice * keys.Count, seconds,
            n => PushAsync(measure, keys[(int)(n % keys.Count)], Body(n, settings.PeoplePerPush, settings.SamplesPerPerson), ct), ct);
        measure.Stop();
        return measure;
    }

    /// <summary>Bodies just over the 256 KB limit, from a measured device: 413 each, never parsed.</summary>
    public async Task<Measure> OversizeAsync(CancellationToken ct)
    {
        var measure = new Measure("oversize");
        var body = Body(1, 1, 1, padding: 256 * 1024 + 10);
        measure.Start();
        await Inject.RunAsync(5, 4, _ => PushAsync(measure, settings.DeviceKeys[0], body, ct), ct);
        measure.Stop();
        return measure;
    }

    /// <summary>One device sending far past its rate: 429 with Retry-After, never a failure.</summary>
    public async Task<Measure> FloodAsync(CancellationToken ct)
    {
        var measure = new Measure("flood");
        var body = Body(1, 1, 1);
        measure.Start();
        await Inject.RunAsync(settings.FloodPerSecond, settings.FloodSeconds, _ => PushAsync(measure, settings.FloodKey, body, ct), ct);
        measure.Stop();
        return measure;
    }
}
