using System.Globalization;
using System.Text;
using Ariva.Simulation.Api.Emulators.Integration;
using Ariva.Simulation.Api.Scenarios;
using Microsoft.Extensions.Options;

namespace Ariva.Simulation.Api.Emulators.Aodb;

/// <summary>The emulated AODB: the last minute played, legs known, AIDX messages and legs pushed, failures, the ACRIS snapshot's time.</summary>
public sealed record AodbStatus(string Airport, int? LastMinute, int Legs, bool AidxConfigured, long Messages, long LegsPushed, long Failures, int? LastStatus,
    string LastError, DateTimeOffset? AcrisLastModified, bool AcrisKeyConfigured);

/// <summary>
/// The emulated AODB (ARV-029). Each demo minute it rebuilds the site's schedule as known then
/// (<see cref="AodbSchedule"/>): the ACRIS snapshot Ariva pulls (<c>aodb/acris/flights</c>) is replaced, with its
/// Last-Modified, when anything changed; and AIDX notifications go to Ariva's AIDX endpoint
/// (<c>api/v1/integration/sites/{site}/aodb/aidx</c>) with its own client: every leg at the first minute of a run,
/// then the legs that changed that minute, at most 500 per message, stamped with the real time of sending (Ariva
/// orders messages by it).
/// </summary>
public sealed class AodbEmulator(ScenarioEngine engine, FeedTime time, IOptionsMonitor<AodbEmulatorSettings> settings, IOptionsMonitor<ArivaTargetSettings> target,
    ArivaIntegrationClient client, TimeProvider clock, ILogger<AodbEmulator> logger) : IDemoMinuteSink
{
    private const int MaxLegsPerMessage = 500;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _play = new(1, 1);
    private int? _lastMinute;
    private int _legs;
    private byte[] _acris = "[]"u8.ToArray();
    private DateTimeOffset? _acrisModified;
    private long _messages;
    private long _pushed;
    private long _failures;
    private int? _lastStatus;
    private string _lastError;

    public ArivaIntegrationClient Client => client;

    public AodbStatus Status()
    {
        lock (_gate)
        {
            var current = settings.CurrentValue;
            return new AodbStatus(current.Airport, _lastMinute, _legs, client.IsConfigured, Interlocked.Read(ref _messages), Interlocked.Read(ref _pushed),
                Interlocked.Read(ref _failures), _lastStatus, _lastError, _acrisModified, !string.IsNullOrEmpty(current.AcrisKeySha256));
        }
    }

    /// <summary>The ACRIS answer and its Last-Modified (whole seconds), as of the last minute played.</summary>
    public (byte[] Body, DateTimeOffset? LastModified) Acris()
    {
        lock (_gate)
            return (_acris, _acrisModified);
    }

    /// <summary>Plays a minute; one at a time (the demo clock's pump and a manual play may meet).</summary>
    public async Task PlayAsync(int minute, Func<double, DateTime> wallOf, CancellationToken ct)
    {
        await _play.WaitAsync(ct);
        try
        {
            await PlayOneAsync(minute, wallOf, ct);
        }
        finally
        {
            _play.Release();
        }
    }

    private async Task PlayOneAsync(int minute, Func<double, DateTime> wallOf, CancellationToken ct)
    {
        var firstOfRun = _lastMinute is not { } last || minute != last + 1;
        var at = time.Observe(minute, wallOf);
        var dayStart = at.AddMinutes(-minute);
        var airport = settings.CurrentValue.Airport;
        var legs = engine.Read(day => AodbSchedule.At(day, minute, m => dayStart.AddMinutes(m), airport));
        var changed = firstOfRun ? legs : [.. legs.Where(l => l.ChangedAt == minute)];
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            _lastMinute = minute;
            _legs = legs.Count;
            if (changed.Count > 0 || _acrisModified is null)
            {
                _acris = AodbSchedule.Acris(legs);
                _acrisModified = new DateTimeOffset(now.UtcTicks - now.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
            }
        }

        if (changed.Count == 0 || !client.IsConfigured)
            return;
        var site = target.CurrentValue.SiteCode;
        for (var start = 0; start < changed.Count; start += MaxLegsPerMessage)
        {
            var part = changed.Skip(start).Take(MaxLegsPerMessage).ToList();
            var transaction = $"SIM-{minute.ToString("D4", CultureInfo.InvariantCulture)}-{(start / MaxLegsPerMessage).ToString(CultureInfo.InvariantCulture)}";
            var xml = AodbSchedule.Aidx(part, clock.GetUtcNow().UtcDateTime, transaction);
            var result = await client.SendAsync(HttpMethod.Post, $"api/v1/integration/sites/{site}/aodb/aidx",
                new StringContent(xml, Encoding.UTF8, "application/xml"), idempotencyKey: null, ct);
            lock (_gate)
            {
                _lastStatus = result.Status;
                _lastError = result.Error;
            }

            Interlocked.Increment(ref _messages);
            if (result.Succeeded)
            {
                Interlocked.Add(ref _pushed, part.Count);
            }
            else
            {
                Interlocked.Add(ref _failures, part.Count);
                logger.LogWarning("AODB emulator: Ariva did not take an AIDX message of {Legs} legs ({Error})", part.Count, result.Error);
            }
        }
    }
}
