using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ariva.Core.Sensing;

namespace Ariva.Infra.Sensing;

/// <summary>
/// The id Ingest gives a sensing batch (ARV-023, ARV-168, ARV-174): the same data from the same device always gets the same
/// id, so a resend (after a 503, a dropped request or a vendor retry) is a duplicate that the inbox, the archive and the
/// queue engine drop; other data gets a new id.
/// <list type="bullet">
/// <item>A kind whose events carry their own times (every canonical and Xovis kind, a declarative section with a time
/// path): the device, the kind, the vendor's package ids (sorted) and the kind's events as the device sent them (raw
/// device times, before any clock correction), each event encoded field by field and the encodings sorted, so the send
/// time (<c>sentUtc</c>, which a device may stamp again on a retry), the layout of the body and the order of the events do
/// not change it. Two pushes get one id only when they carry the same package ids and the same events to the precision of
/// the device's clock. That makes them one report when the events have an identity of their own (a track id, a line's
/// interval, a zone's reading at a time); anonymous crossings have none (two people crossing one line in the same second
/// look alike), so in a push without a package whose crossings include one without a track id, the crossings' id also
/// hashes the device's send time, or Ariva's receipt time when the push has none (ARV-174 security review, R1): a retry
/// that keeps its send time is recognised, one that stamps it again is counted again.</item>
/// <item>A kind stamped with Ariva's receipt time (a declarative section with <c>"received": true</c>) has no time of its
/// own, so the same body later may be a new reading: the body's hash and the package ids when the mapping supplies one (a
/// resend of the package is recognised), else the body's hash and the receipt time (any resend is new data: the residual
/// the mapping guidance states, and the conformance kit warns about for crossings).</item>
/// </list>
/// The result is a version 8 GUID (RFC 9562, custom).
/// </summary>
internal static class SensingBatchIds
{
    public static Guid Of(Guid device, PushKinds kind, MappedPush push, ReadOnlySpan<byte> bodyHash, DateTime receivedUtc)
    {
        ArgumentNullException.ThrowIfNull(push);
        var name = Name(kind);
        // Sorted, so a list of envelopes in another order (Xovis) is the same identity.
        var packages = string.Join(',', push.PackageIds.Order().Select(p => p.ToString(CultureInfo.InvariantCulture)));
        byte[] digest;
        if ((push.ReceiptTimed & kind) != 0)
        {
            // Unchanged for a receipt-timed kind without a package, so its ids are those Ariva gave before ARV-174.
            var suffix = push.PackageIds.Count > 0 ? "|package:" + packages : "|" + receivedUtc.Ticks.ToString(CultureInfo.InvariantCulture);
            digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{device:N}|{name}|{Convert.ToHexString(bodyHash)}{suffix}"));
        }
        else
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(Encoding.UTF8.GetBytes($"ariva-batch-v2|{device:N}|{name}|package:{packages}|{Sent(kind, push, receivedUtc)}|"));
            var events = Events(kind, push);
            events.Sort(static (a, b) => a.AsSpan().SequenceCompareTo(b));
            var length = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, events.Count);
            hash.AppendData(length);
            foreach (var e in events)
            {
                BinaryPrimitives.WriteInt32LittleEndian(length, e.Length);
                hash.AppendData(length);
                hash.AppendData(e);
            }

            digest = hash.GetHashAndReset();
        }

        digest[6] = (byte)((digest[6] & 0x0F) | 0x80); // version 8 (custom, RFC 9562)
        digest[8] = (byte)((digest[8] & 0x3F) | 0x80);
        return new Guid(digest.AsSpan(0, 16), bigEndian: true);
    }

    /// <summary>
    /// The send stamp of anonymous crossings (R1): empty unless the kind is crossings, the push has no package and a crossing
    /// has no track id; then the device's send time, or the receipt time when the push carries none (every message is then
    /// new data, never merged with another).
    /// </summary>
    internal static string Sent(PushKinds kind, MappedPush push, DateTime receivedUtc) =>
        kind == PushKinds.Crossings && push.PackageIds.Count == 0 && push.Crossings.Any(c => c.TrackId is null)
            ? push.DeviceSentUtc is { } sent
                ? "sent:" + sent.Ticks.ToString(CultureInfo.InvariantCulture)
                : "received:" + receivedUtc.Ticks.ToString(CultureInfo.InvariantCulture)
            : string.Empty;

    internal static string Name(PushKinds kind) => kind switch
    {
        PushKinds.Tracks => "tracks",
        PushKinds.Crossings => "crossings",
        PushKinds.Occupancy => "occupancy",
        PushKinds.Intervals => "intervals",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), "One kind of events.")
    };

    /// <summary>Each event of the kind encoded field by field (strings as UTF-16 code units with a length, null apart from empty).</summary>
    private static List<byte[]> Events(PushKinds kind, MappedPush push) => kind switch
    {
        PushKinds.Tracks => [.. push.Tracks.Select(t => Encode(w =>
        {
            Text(w, t.TrackId);
            Number(w, t.X);
            Number(w, t.Y);
            Flag(w, t.HeightMetres.HasValue);
            Number(w, t.HeightMetres ?? 0);
            Ticks(w, t.TimeUtc);
        }))],
        PushKinds.Crossings => [.. push.Crossings.Select(c => Encode(w =>
        {
            Text(w, c.LineName);
            Integer(w, (int)c.Direction);
            Text(w, c.TrackId);
            Ticks(w, c.TimeUtc);
        }))],
        PushKinds.Occupancy => [.. push.Occupancy.Select(o => Encode(w =>
        {
            Text(w, o.ZoneName);
            Integer(w, o.Count);
            Ticks(w, o.TimeUtc);
        }))],
        PushKinds.Intervals => [.. push.Intervals.Select(i => Encode(w =>
        {
            Text(w, i.LineName);
            Integer(w, i.In);
            Integer(w, i.Out);
            Ticks(w, i.FromUtc);
            Ticks(w, i.TimeUtc);
        }))],
        _ => throw new ArgumentOutOfRangeException(nameof(kind), "One kind of events.")
    };

    private static byte[] Encode(Action<ArrayBufferWriter<byte>> write)
    {
        var writer = new ArrayBufferWriter<byte>(64);
        write(writer);
        return writer.WrittenSpan.ToArray();
    }

    private static void Integer(ArrayBufferWriter<byte> w, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(w.GetSpan(4), value);
        w.Advance(4);
    }

    private static void Long(ArrayBufferWriter<byte> w, long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(w.GetSpan(8), value);
        w.Advance(8);
    }

    private static void Flag(ArrayBufferWriter<byte> w, bool value) => Integer(w, value ? 1 : 0);

    private static void Number(ArrayBufferWriter<byte> w, double value) => Long(w, BitConverter.DoubleToInt64Bits(value));

    private static void Ticks(ArrayBufferWriter<byte> w, DateTime value) => Long(w, value.Ticks);

    private static void Text(ArrayBufferWriter<byte> w, string value)
    {
        if (value is null)
        {
            Integer(w, -1);
            return;
        }

        Integer(w, value.Length);
        foreach (var c in value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(w.GetSpan(2), c);
            w.Advance(2);
        }
    }
}
