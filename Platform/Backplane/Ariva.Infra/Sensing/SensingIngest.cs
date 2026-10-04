using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Sensing;
using Ariva.Core.Services.Sensing;
using ZiggyCreatures.Caching.Fusion;

namespace Ariva.Infra.Sensing;

/// <summary>What a push came to (ARV-023): events accepted, refused, flagged and ignored on purpose, the clock estimate, and the first problems.</summary>
public sealed record IngestOutcome(int Accepted, int Rejected, int Flagged, int Ignored, ClockReading Clock, IReadOnlyList<string> Problems);

/// <summary>The ingest settings (<c>Ingest</c> section).</summary>
public sealed class IngestSettings
{
    public const string SectionName = "Ingest";

    /// <summary>Events in one message at most (tracks, crossings, occupancy and intervals together).</summary>
    public int MaxEventsPerMessage { get; set; } = 2_000;

    /// <summary>Request bodies at most (256 KB, wiki/09).</summary>
    public const int MaxBodyBytes = 256 * 1024;

    /// <summary>
    /// The largest setting allowed: 3,000 worst-case track samples serialise to about 900 KB, under Kafka's 1 MB message
    /// limit; more would fail every publish of a full message.
    /// </summary>
    public const int MaxEventsLimit = 3_000;
}

/// <summary>
/// Per-device clock estimates and health throttling, in memory per Ingest process (ARV-023). A device's pushes reach
/// any replica; each keeps its own estimate, which converges within a few pushes. Bounded: past
/// <see cref="MaxDevices"/> devices it starts over rather than grow.
/// </summary>
public sealed class DeviceClockStore
{
    public const int MaxDevices = 20_000;
    private readonly ConcurrentDictionary<Guid, ClockOffset> _clocks = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _health = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _lastSent = new();

    public ClockOffset Current(Guid device) => _clocks.TryGetValue(device, out var clock) ? clock : ClockOffset.None;

    /// <summary>
    /// One more reading, unless the device sent this time before or an earlier one (a resend after a failure carries its
    /// original, stale send time, which would drag the estimate).
    /// </summary>
    public ClockOffset Observe(Guid device, DateTime deviceUtc, DateTime receivedUtc)
    {
        if (_clocks.Count > MaxDevices)
        {
            _clocks.Clear();
            _lastSent.Clear();
        }

        if (_lastSent.TryGetValue(device, out var last) && deviceUtc <= last)
            return Current(device);
        // A forged or broken send time must not become the mark later readings are compared with.
        if (Math.Abs((deviceUtc - receivedUtc).TotalMilliseconds) > CanonicalEventRules.MaxClockOffsetMilliseconds)
            return Current(device);
        _lastSent[device] = deviceUtc;
        return _clocks.AddOrUpdate(device, _ => ClockOffset.None.Next(deviceUtc, receivedUtc), (_, clock) => clock.Next(deviceUtc, receivedUtc));
    }

    /// <summary>True when a synthesised health report is due (at most one per device every <paramref name="every"/>).</summary>
    public bool HealthDue(Guid device, DateTime nowUtc, TimeSpan every) =>
        !_health.TryGetValue(device, out var last) || nowUtc - last >= every;

    /// <summary>Records that a health report went out (after it was published).</summary>
    public void HealthSent(Guid device, DateTime nowUtc)
    {
        if (_health.Count > MaxDevices)
            _health.Clear();
        _health[device] = nowUtc;
    }
}

/// <summary>
/// Turns one push into sensing batches (ARV-023): the dialect must be the device's own; the payload is mapped
/// (refused whole when malformed); every event is checked against the canonical bounds, and lines and zones must be named
/// as in the published geometry of the device's zone; the device's clock offset is estimated from its send time
/// (F19, EWMA) and events are corrected when it is off but stable; events beyond the tolerance from receipt are
/// flagged, far-future or older-than-retention ones refused; track ids are namespaced by device; the batches go to the
/// sink keyed by zone, with a health report at most every 10 seconds per device.
/// </summary>
public sealed class SensingIngest(
    ISvcDeviceGateway gateway,
    ISensingSink sink,
    DeviceClockStore clocks,
    IFusionCache cache,
    Microsoft.Extensions.Options.IOptions<IngestSettings> settings,
    Declarative.DeclarativeMappingCatalog mappings)
{
    /// <summary>Further ahead than this, an event cannot be a clock glitch; it is refused.</summary>
    public static readonly TimeSpan MaxFuture = TimeSpan.FromMinutes(5);

    /// <summary>Older than the sensing topics keep (3 days), an event would be dropped by Kafka anyway; it is refused.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(3);

    /// <summary>Events older than this when they arrive are late (a backlog, a re-push or a stuck clock) and flagged.</summary>
    public static readonly TimeSpan Late = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan HealthEvery = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The adapter boundary of a push (ARV-023, CWE-120): the body parsed as JSON (depth 16, no comments) and mapped by the
    /// dialect's mapper to at most <paramref name="maxEvents"/> events. Any body gives a push or Ariva's reason, never an
    /// exception (ARV-070 checks this with generated, truncated and mutated payloads).
    /// </summary>
    public static (MappedPush Push, string Error) Read(ReadOnlyMemory<byte> body, DeviceDialect dialect, DevicePose pose, Declarative.DeclarativeMapping mapping,
        int maxEvents, DateTime receivedUtc)
    {
        // JsonDocument accepts a string holding invalid UTF-8 and only fails when it is read (an InvalidOperationException
        // from the mapper): the whole body is checked first (found by ARV-070's generated payloads).
        if (!System.Text.Unicode.Utf8.IsValid(body.Span))
            return (null, "The body is not valid UTF-8.");
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16, CommentHandling = JsonCommentHandling.Disallow });
            return (dialect switch
            {
                DeviceDialect.Xovis => XovisPushMapper.Map(document.RootElement, pose, maxEvents),
                DeviceDialect.Canonical => CanonicalPushMapper.Map(document.RootElement, maxEvents),
                DeviceDialect.Declarative => Declarative.DeclarativeMapper.Map(document.RootElement,
                    mapping ?? throw new PushFormatException("The device's declarative mapping is not in this version of Ariva."), pose, maxEvents, receivedUtc),
                _ => throw new PushFormatException($"The {dialect} dialect has no push endpoint.")
            }, null);
        }
        catch (JsonException)
        {
            return (null, "The body is not well-formed JSON.");
        }
        catch (InvalidOperationException)
        {
            // A string or member name escaping a lone surrogate (\uDC00) parses and fails only when read (found by ARV-070's review).
            return (null, "The body is not well-formed JSON.");
        }
        catch (PushFormatException e)
        {
            return (null, e.Message);
        }
    }

    public async Task<Fluentx.Result<IngestOutcome>> IngestAsync(DeviceCredentialRecord device, DeviceDialect dialect, ReadOnlyMemory<byte> body, DateTime receivedUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!string.Equals(device.Dialect, dialect.ToString(), StringComparison.Ordinal))
            return Fluentx.Result.Error<IngestOutcome>($"This device is registered for the {device.Dialect} dialect.");

        var (push, error) = Read(body, dialect, new DevicePose(device.X, device.Y, device.OrientationDegrees),
            device.MappingName is null ? null : mappings.Find(device.MappingName), Math.Max(1, settings.Value.MaxEventsPerMessage), receivedUtc);
        if (error is not null)
            return Fluentx.Result.Error<IngestOutcome>(error);

        var clock = push.DeviceSentUtc is { } sent ? clocks.Observe(device.DeviceId, sent, receivedUtc) : clocks.Current(device.DeviceId);
        if (push.ConnectionTest)
            return new Fluentx.Result<IngestOutcome>(new IngestOutcome(0, 0, 0, 0, clock.Reading, []));

        var problems = new List<string>(push.Notes.Select(n => "ignored: " + n));
        var rejected = 0;
        var flagged = 0;
        var names = await NamesAsync(device, ct);

        List<Sensed<T>> Check<T>(IReadOnlyList<T> events, string kind, Func<T, string> nameOf, bool isLine, Func<T, T> rewrite, PushKinds kindFlag) where T : CanonicalEvent
        {
            // Stamped with the receipt time by the mapping: already Ariva's clock, nothing to correct.
            var receiptTimed = (push.ReceiptTimed & kindFlag) != 0;
            var kept = new List<Sensed<T>>(events.Count);
            for (var i = 0; i < events.Count; i++)
            {
                var e = events[i];
                var why = CanonicalEventRules.Validate(e).FirstOrDefault();
                if (why is null && nameOf is not null)
                {
                    var name = nameOf(e);
                    if (names is null)
                        why = $"zone {device.QueueZoneName} is not in the published zone profile, so {name} cannot be checked";
                    else if (!(isLine ? names.Value.Lines : names.Value.Zones).Contains(name))
                        why = $"{(isLine ? "line" : "zone")} {PushJson.Quote(name)} is not in the published geometry of {device.QueueZoneName}";
                }

                if (why is not null)
                {
                    rejected++;
                    if (problems.Count < 10)
                        problems.Add($"{kind}[{i}]: {why}");
                    continue;
                }

                var time = receiptTimed ? e.TimeUtc : clock.Correct(e.TimeUtc);
                if (why is null && time > receivedUtc + MaxFuture)
                    why = "the time is more than 5 minutes ahead of Ariva's clock";
                if (why is null && time < receivedUtc - MaxAge)
                    why = "the time is older than the 3 days the sensing topics keep";
                if (why is not null)
                {
                    rejected++;
                    if (problems.Count < 10)
                        problems.Add($"{kind}[{i}]: {why}");
                    continue;
                }

                var flags = time != e.TimeUtc ? SensedFlags.Corrected : SensedFlags.None;
                if (clock.Reading.State == ClockState.Unreliable)
                    flags |= SensedFlags.ClockUnreliable;
                if (time > receivedUtc.AddMilliseconds(ClockOffset.ToleranceMilliseconds) || time < receivedUtc - Late)
                    flags |= SensedFlags.Skewed;
                if ((flags & SensedFlags.Skewed) != 0)
                    flagged++;
                kept.Add(new Sensed<T>(rewrite(e), time, flags));
            }

            return kept;
        }

        TrackPosition Namespaced(TrackPosition t) => t with { TrackId = CanonicalEventRules.NamespacedTrackId(device.Code, t.TrackId) };
        LineCrossing NamespacedCrossing(LineCrossing c) => c.TrackId is null ? c : c with { TrackId = CanonicalEventRules.NamespacedTrackId(device.Code, c.TrackId) };
        IntervalCount CorrectedInterval(IntervalCount c) => c with { FromUtc = clock.Correct(c.FromUtc), TimeUtc = clock.Correct(c.TimeUtc) };

        var tracks = Check(push.Tracks, "tracks", null, false, Namespaced, PushKinds.Tracks);
        var crossings = Check(push.Crossings, "crossings", c => c.LineName, true, NamespacedCrossing, PushKinds.Crossings);
        var occupancy = Check(push.Occupancy, "occupancy", o => o.ZoneName, false, o => o, PushKinds.Occupancy);
        var intervals = Check(push.Intervals, "intervals", c => c.LineName, true, CorrectedInterval, PushKinds.Intervals);

        var reading = clock.Reading;
        var commissioned = device.State is "Online" or "Degraded" or "Offline";
        var packages = push.PackageIds;
        var bodyHash = SHA256.HashData(body.Span);
        var events = new List<Ariva.Core.Domain.Contracts.IEvent>();
        T Batch<T>(T batch, string kind) where T : SensingBatch
        {
            batch.Id = BatchId(device.DeviceId, kind, bodyHash, (push.ReceiptTimed & KindFlag(kind)) != 0 ? receivedUtc : null);
            batch.DeviceId = device.DeviceId;
            batch.DeviceCode = device.Code;
            batch.SiteCode = device.SiteCode;
            batch.QueueZoneName = device.QueueZoneName;
            batch.Dialect = dialect == DeviceDialect.Declarative ? $"{dialect}:{device.MappingName}" : dialect.ToString();
            batch.Commissioned = commissioned;
            batch.ReceivedUtc = receivedUtc;
            batch.OccurredOn = receivedUtc;
            batch.PackageId = packages.Count > 0 ? packages[0] : null;
            batch.Clock = reading;
            return batch;
        }

        if (tracks.Count > 0)
            events.Add(Batch(new TrackSampleBatch { Samples = tracks }, "tracks"));
        if (crossings.Count > 0)
            events.Add(Batch(new VendorLineCrossingBatch { Crossings = crossings }, "crossings"));
        if (occupancy.Count > 0)
            events.Add(Batch(new ZoneOccupancyBatch { Occupancy = occupancy }, "occupancy"));
        if (intervals.Count > 0)
            events.Add(Batch(new IntervalCountBatch { Intervals = intervals }, "intervals"));

        var statusAccepted = false;
        var health = false;
        if (push.Status is { } status)
        {
            var statusTime = clock.Correct(status.TimeUtc);
            var why = CanonicalEventRules.Validate(status).FirstOrDefault()
                      ?? (statusTime > receivedUtc + MaxFuture ? "the time is more than 5 minutes ahead of Ariva's clock" : null)
                      ?? (statusTime < receivedUtc - MaxAge ? "the time is older than the 3 days the sensing topics keep" : null);
            if (why is not null)
            {
                rejected++;
                problems.Add("status: " + why);
            }
            else
            {
                statusAccepted = true;
                health = true;
                events.Add(Health(device, status with { TimeUtc = statusTime }, reading, receivedUtc));
            }
        }
        else if (clocks.HealthDue(device.DeviceId, receivedUtc, HealthEvery))
        {
            health = true;
            events.Add(Health(device, new DeviceStatus(true, null, null, reading.OffsetMilliseconds, receivedUtc), reading, receivedUtc));
        }

        if (events.Count > 0)
        {
            try
            {
                await sink.PublishAsync(events, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new SensingSinkUnavailableException(e);
            }
        }

        if (health)
            clocks.HealthSent(device.DeviceId, receivedUtc);

        var accepted = tracks.Count + crossings.Count + occupancy.Count + intervals.Count + (statusAccepted ? 1 : 0);
        return new Fluentx.Result<IngestOutcome>(new IngestOutcome(accepted, rejected, flagged, push.Ignored, reading, problems));
    }

    private static DeviceHealthReported Health(DeviceCredentialRecord device, DeviceStatus status, ClockReading clock, DateTime receivedUtc) => new()
    {
        DeviceId = device.DeviceId,
        DeviceCode = device.Code,
        SiteCode = device.SiteCode,
        QueueZoneName = device.QueueZoneName,
        Status = status,
        Clock = clock,
        ReceivedUtc = receivedUtc,
        Commissioned = device.State is "Online" or "Degraded" or "Offline",
        OccurredOn = status.TimeUtc
    };

    /// <summary>
    /// The same body from the same device always gives the same batch id, so a resend (after a 503, or a vendor retry)
    /// is a duplicate that consumers drop; any other body, including one whose vendor package counter restarted, gives
    /// a new id.
    /// </summary>
    private static Guid BatchId(Guid device, string kind, byte[] bodyHash, DateTime? receivedUtc)
    {
        // Events stamped at receipt (ARV-024) carry no time of their own: the same body later is a new reading, not a
        // resend, so the receipt time is part of the id.
        var receipt = receivedUtc is { } at ? "|" + at.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{device:N}|{kind}|{Convert.ToHexString(bodyHash)}{receipt}"));
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80); // version 8 (custom, RFC 9562)
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes.AsSpan(0, 16), bigEndian: true);
    }

    private static PushKinds KindFlag(string kind) => kind switch
    {
        "tracks" => PushKinds.Tracks,
        "crossings" => PushKinds.Crossings,
        "occupancy" => PushKinds.Occupancy,
        "intervals" => PushKinds.Intervals,
        _ => PushKinds.None
    };

    /// <summary>The line and zone names of the device's zone in the published profile (cached a minute), or null when it is not published.</summary>
    private async Task<(HashSet<string> Lines, HashSet<string> Zones)?> NamesAsync(DeviceCredentialRecord device, CancellationToken ct)
    {
        var geometry = await cache.GetOrSetAsync<DeviceZoneViewModel>($"ingest:zone:{device.SiteCode}/{device.QueueZoneName}", async (_, token) =>
        {
            var result = await gateway.PublishedZoneAsync(device.SiteCode, device.QueueZoneName, token);
            return result.HasErrors ? null : result.Data;
        }, options =>
        {
            options.Duration = TimeSpan.FromMinutes(1);
            options.IsFailSafeEnabled = false;
            options.SkipDistributedCacheRead = true;
            options.SkipDistributedCacheWrite = true;
        }, token: ct);
        return geometry is null
            ? null
            : (new HashSet<string>(geometry.Lines.Select(l => l.Name), StringComparer.Ordinal), new HashSet<string>(geometry.Zones.Select(z => z.Name), StringComparer.Ordinal));
    }
}

/// <summary>The events could not be stored (the broker or the transport failed); the device should send again (503).</summary>
public sealed class SensingSinkUnavailableException(Exception inner) : Exception("The sensing events could not be stored.", inner);

/// <summary>The hosts' sink: every batch to Kafka through the message bus, keyed as the batch says.</summary>
internal sealed class MessageBusSensingSink(ISvcMessageBus bus) : ISensingSink
{
    public async Task PublishAsync(IReadOnlyList<Ariva.Core.Domain.Contracts.IEvent> events, CancellationToken ct = default)
    {
        foreach (var e in events)
            await bus.PublishAsync(e, ct);
    }
}
