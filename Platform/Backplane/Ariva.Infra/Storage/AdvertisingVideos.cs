using System.Buffers.Binary;
using System.Numerics;
using Ariva.Core.Domain.Entities;

namespace Ariva.Infra.Storage;

/// <summary>An advertisement video as checked (its bytes stripped of metadata in place): its type, its picture size and its length.</summary>
public sealed record CheckedVideo(string ContentType, string Extension, int WidthPixels, int HeightPixels, long DurationMilliseconds);

/// <summary>
/// Takes advertisement videos (ARV-142b, CWE-434, CWE-79, CWE-400, CWE-120): an MP4 with H.264 video (AAC audio or none)
/// or a WebM with VP8, VP9 or AV1 video (Opus or Vorbis audio or none), typed by its container, never by its name or the
/// declared content type. Managed code reads the container only (no decoder runs): every box and element must lie within
/// its parent and the file, the nesting is at most <see cref="MaxDepth"/> deep and at most <see cref="MaxElements"/> are
/// read, nothing is allocated from a declared size, and the media data itself is never walked. A file whose structure
/// does not hold, that is truncated, fragmented or live (no length), that has data after its last top-level box or element,
/// another kind of track, another codec, or more than one picture or sound track is refused. Metadata is stripped without
/// moving the media (the sample offsets stay valid): MP4 user data, <c>meta</c> and <c>uuid</c> boxes become free space,
/// WebM <c>Tags</c>, <c>Attachments</c>, <c>Chapters</c>, titles and track names become <c>Void</c> elements, and every free
/// space, handler name, compressor name and creation time is zeroed. The compressed media can still hold bytes the
/// uploader chose, so serving each file with its own type, <c>nosniff</c> and a sandboxing content security policy is the
/// control against content sniffing, as for images.
/// </summary>
public static class AdvertisingVideos
{
    /// <summary>The deepest a box or element may nest (an MP4's codec configuration sits eight boxes down).</summary>
    public const int MaxDepth = 12;

    /// <summary>The most boxes or elements read in one file; the media data (MP4 <c>mdat</c>, WebM clusters) is never walked.</summary>
    public const int MaxElements = 4096;

    /// <summary>
    /// How far, in milliseconds, a track or media header (MP4) or a cluster's start (WebM) may state beyond the file's own
    /// length: an encoder's priming of the sound stays well within it. Beyond it the file is refused, so the length held to
    /// the limit is not a header that hides a longer stream.
    /// </summary>
    public const int StatedLengthTolerance = 1000;

    /// <summary>Whether the bytes start like a video container (an MP4 <c>ftyp</c> box or an EBML header): the upload is then a video, never an image.</summary>
    public static bool LooksLikeVideo(ReadOnlySpan<byte> bytes) => Mp4Container.Starts(bytes) || WebmContainer.Starts(bytes);

    /// <summary>
    /// The video as checked, its metadata stripped in <paramref name="bytes"/> itself, or null when the bytes are not an MP4
    /// or WebM video within the rules (refused bytes may be partly stripped and are discarded).
    /// </summary>
    public static CheckedVideo Check(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (Mp4Container.Starts(bytes))
            return new Mp4Container(bytes).Check();
        return WebmContainer.Starts(bytes) ? new WebmContainer(bytes).Check() : null;
    }

    internal static bool Fits(long width, long height) =>
        width is > 0 and <= AdvertisingMedia.MaxSide && height is > 0 and <= AdvertisingMedia.MaxSide && width * height <= AdvertisingMedia.MaxPixels;
}

/// <summary>
/// The ISO base media file format (ISO/IEC 14496-12) as an MP4 of ARV-142b: an <c>ftyp</c> box first with an MP4 brand,
/// one <c>moov</c> (not fragmented: no <c>mvex</c>, and no <c>moof</c> at the top), media data, free space and metadata
/// only at the top, each track a picture (<c>avc1</c> or <c>avc3</c> with its <c>avcC</c>) or a sound (<c>mp4a</c>
/// carrying AAC), and the movie's length known.
/// </summary>
internal sealed class Mp4Container(byte[] bytes)
{
    private static readonly uint Ftyp = Code("ftyp"), Moov = Code("moov"), Mdat = Code("mdat"), Mvhd = Code("mvhd"), Trak = Code("trak"),
        Tkhd = Code("tkhd"), Mdia = Code("mdia"), Mdhd = Code("mdhd"), Hdlr = Code("hdlr"), Minf = Code("minf"), Stbl = Code("stbl"),
        Stsd = Code("stsd"), Mvex = Code("mvex"), Moof = Code("moof"), Avc1 = Code("avc1"), Avc3 = Code("avc3"), AvcC = Code("avcC"),
        Mp4a = Code("mp4a"), Esds = Code("esds"), Vide = Code("vide"), Soun = Code("soun"), Free = Code("free"), Skip = Code("skip"),
        Wide = Code("wide"), Meta = Code("meta"), Uuid = Code("uuid"), Udta = Code("udta");

    /// <summary>The brands of an MP4 a browser plays; QuickTime (<c>qt  </c>) and the others are refused.</summary>
    private static readonly HashSet<uint> Brands =
        [Code("isom"), Code("iso2"), Code("iso4"), Code("iso5"), Code("iso6"), Code("mp41"), Code("mp42"), Code("avc1"), Code("M4V ")];

    /// <summary>AAC as an MPEG-4 decoder configuration names it: MPEG-4 audio and the three MPEG-2 AAC profiles.</summary>
    private static readonly HashSet<byte> AacObjectTypes = [0x40, 0x66, 0x67, 0x68];

    private int _elements;
    private int _pictures;
    private int _sounds;
    private int _width;
    private int _height;
    private ulong _timescale;
    private ulong _duration;
    private bool _movieHeader;

    /// <summary>The longest a track header states, in the movie's timescale.</summary>
    private ulong _longestTrack;

    /// <summary>The longest a media header states, in milliseconds.</summary>
    private UInt128 _longestMedia;

    public static bool Starts(ReadOnlySpan<byte> bytes) => bytes.Length >= 16 && bytes.Slice(4, 4).SequenceEqual("ftyp"u8);

    public CheckedVideo Check()
    {
        var movies = 0;
        var media = 0;
        var position = 0;
        while (position < bytes.Length)
        {
            if (!TryRead(position, bytes.Length, 1, out var box))
                return null;
            if (position == 0)
            {
                if (box.Type != Ftyp || !Branded(box))
                    return null;
            }
            else if (box.Type == Moov)
            {
                if (++movies > 1 || !Movie(box))
                    return null;
            }
            else if (box.Type == Mdat)
                media++;
            else if (box.Type == Ftyp || box.Type == Moof || !StripMetadata(box))
                return null;
            position = box.End;
        }

        if (movies != 1 || media == 0 || !_movieHeader || _pictures != 1 || _timescale == 0 || _duration == 0)
            return null;
        var milliseconds = (((UInt128)_duration * 1000) + _timescale - 1) / _timescale;
        // The movie's length is what is held to the limit; no track or media header may state more than a second beyond
        // it (an encoder's priming of the sound stays well within that), so a short movie header does not hide a long stream.
        var longestTrack = (((UInt128)_longestTrack * 1000) + _timescale - 1) / _timescale;
        if (longestTrack > milliseconds + AdvertisingVideos.StatedLengthTolerance || _longestMedia > milliseconds + AdvertisingVideos.StatedLengthTolerance)
            return null;
        return new CheckedVideo("video/mp4", "mp4", _width, _height, milliseconds > long.MaxValue ? long.MaxValue : (long)milliseconds);
    }

    /// <summary>The <c>ftyp</c> box: an MP4 brand as its major brand or among its compatible brands.</summary>
    private bool Branded(Box box)
    {
        var length = box.End - box.Payload;
        if (length < 8 || (length - 8) % 4 != 0)
            return false;
        if (Brands.Contains(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(box.Payload))))
            return true;
        for (var at = box.Payload + 8; at < box.End; at += 4)
        {
            if (Brands.Contains(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at))))
                return true;
        }

        return false;
    }

    private bool Movie(Box movie)
    {
        foreach (var box in Children(movie.Payload, movie.End, 2))
        {
            if (box is null)
                return false;
            var child = box.Value;
            if (child.Type == Mvhd)
            {
                if (_movieHeader || !MovieHeader(child))
                    return false;
                _movieHeader = true;
            }
            else if (child.Type == Trak)
            {
                if (!Track(child))
                    return false;
            }
            else if (child.Type == Mvex)
                return false;
            else
                StripMetadata(child);
        }

        return true;
    }

    /// <summary>The movie header: its timescale and length kept, its creation and change times zeroed.</summary>
    private bool MovieHeader(Box box)
    {
        if (!Times(box, out var at))
            return false;
        var version = bytes[box.Payload];
        _timescale = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at));
        _duration = version == 0 ? BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at + 4)) : BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(at + 4));
        // All ones is "unknown": a file without a length is refused.
        return _duration != (version == 0 ? uint.MaxValue : ulong.MaxValue);
    }

    /// <summary>
    /// A full box that opens with a creation and a change time (<c>mvhd</c>, <c>tkhd</c>, <c>mdhd</c>), version 0 (32-bit
    /// times) or 1 (64-bit): the times are zeroed; <paramref name="after"/> is where the box's next field starts.
    /// </summary>
    private bool Times(Box box, out int after)
    {
        after = 0;
        if (box.End - box.Payload < 4)
            return false;
        var version = bytes[box.Payload];
        var times = version switch { 0 => 8, 1 => 16, _ => -1 };
        // Room for the next field too: a timescale and a length, or a track id and a length.
        if (times < 0 || box.End - box.Payload < 4 + times + (version == 0 ? 8 : 12))
            return false;
        bytes.AsSpan(box.Payload + 4, times).Clear();
        after = box.Payload + 4 + times;
        return true;
    }

    /// <summary>A track header: its times zeroed, its length (in the movie's timescale, after the track id and a reserved word) read.</summary>
    private bool TrackHeader(Box box)
    {
        if (!Times(box, out var at))
            return false;
        var version = bytes[box.Payload];
        if (box.End - at < (version == 0 ? 12 : 16))
            return false;
        var duration = version == 0 ? BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at + 8)) : BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(at + 8));
        // All ones is "unknown", which a track never needs to say: a fragmented file is refused anyway.
        if (duration == (version == 0 ? uint.MaxValue : ulong.MaxValue))
            return false;
        _longestTrack = Math.Max(_longestTrack, duration);
        return true;
    }

    /// <summary>A media header: its times zeroed, its own timescale and length read.</summary>
    private bool MediaHeader(Box box)
    {
        if (!Times(box, out var at))
            return false;
        var version = bytes[box.Payload];
        var timescale = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at));
        var duration = version == 0 ? BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at + 4)) : BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(at + 4));
        if (timescale == 0 || duration == (version == 0 ? uint.MaxValue : ulong.MaxValue))
            return false;
        var milliseconds = (((UInt128)duration * 1000) + timescale - 1) / timescale;
        if (milliseconds > _longestMedia)
            _longestMedia = milliseconds;
        return true;
    }

    private bool Track(Box track)
    {
        var media = 0;
        foreach (var box in Children(track.Payload, track.End, 3))
        {
            if (box is null)
                return false;
            var child = box.Value;
            if (child.Type == Tkhd)
            {
                if (!TrackHeader(child))
                    return false;
            }
            else if (child.Type == Mdia)
            {
                if (++media > 1 || !Media(child))
                    return false;
            }
            else
                StripMetadata(child);
        }

        return media == 1;
    }

    private bool Media(Box mediaBox)
    {
        uint handler = 0;
        var handlers = 0;
        var headers = 0;
        Box? information = null;
        foreach (var box in Children(mediaBox.Payload, mediaBox.End, 4))
        {
            if (box is null)
                return false;
            var child = box.Value;
            if (child.Type == Mdhd)
            {
                if (++headers > 1 || !MediaHeader(child))
                    return false;
            }
            else if (child.Type == Hdlr)
            {
                if (++handlers > 1 || !Handler(child, out handler))
                    return false;
            }
            else if (child.Type == Minf)
            {
                if (information is not null)
                    return false;
                information = child;
            }
            else
                StripMetadata(child);
        }

        // A picture or a sound track; a text, subtitle, timecode, hint or metadata track is refused.
        return headers == 1 && handlers == 1 && information is { } minf && (handler == Vide || handler == Soun) && MediaInformation(minf, handler);
    }

    /// <summary>A handler box: its type read, its name (free text) zeroed.</summary>
    private bool Handler(Box box, out uint handler)
    {
        handler = 0;
        if (box.End - box.Payload < 24)
            return false;
        handler = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(box.Payload + 8));
        bytes.AsSpan(box.Payload + 24, box.End - box.Payload - 24).Clear();
        return true;
    }

    private bool MediaInformation(Box information, uint handler)
    {
        var tables = 0;
        foreach (var box in Children(information.Payload, information.End, 5))
        {
            if (box is null)
                return false;
            var child = box.Value;
            if (child.Type == Stbl)
            {
                if (++tables > 1 || !SampleTable(child, handler))
                    return false;
            }
            else if (child.Type == Hdlr)
            {
                if (!Handler(child, out _))
                    return false;
            }
            else
                StripMetadata(child);
        }

        return tables == 1;
    }

    private bool SampleTable(Box table, uint handler)
    {
        var descriptions = 0;
        foreach (var box in Children(table.Payload, table.End, 6))
        {
            if (box is null)
                return false;
            var child = box.Value;
            if (child.Type == Stsd)
            {
                if (++descriptions > 1 || !SampleDescription(child, handler))
                    return false;
            }
            else
                StripMetadata(child);
        }

        return descriptions == 1;
    }

    /// <summary>The sample description: exactly one entry, filling the box, of the codec the track's kind allows.</summary>
    private bool SampleDescription(Box description, uint handler)
    {
        if (description.End - description.Payload < 8 || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(description.Payload + 4)) != 1 ||
            !TryRead(description.Payload + 8, description.End, 7, out var entry) || entry.End != description.End)
            return false;
        return handler == Vide ? Picture(entry) : Sound(entry);
    }

    /// <summary>
    /// An H.264 sample entry (ISO/IEC 14496-15): the visual sample entry's 78 bytes (its size in pixels read, its compressor
    /// name zeroed) and its boxes, one of them the decoder configuration (<c>avcC</c>, version 1).
    /// </summary>
    private bool Picture(Box entry)
    {
        if ((entry.Type != Avc1 && entry.Type != Avc3) || entry.End - entry.Payload < 78 || ++_pictures > 1)
            return false;
        _width = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(entry.Payload + 24));
        _height = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(entry.Payload + 26));
        if (!AdvertisingVideos.Fits(_width, _height))
            return false;
        bytes.AsSpan(entry.Payload + 42, 32).Clear();
        var configurations = 0;
        foreach (var box in Children(entry.Payload + 78, entry.End, 8))
        {
            if (box is null)
                return false;
            var child = box.Value;
            if (child.Type == AvcC)
            {
                if (++configurations > 1 || child.End - child.Payload < 7 || bytes[child.Payload] != 1)
                    return false;
            }
            else
                StripMetadata(child);
        }

        return configurations == 1;
    }

    /// <summary>
    /// An AAC sample entry: the audio sample entry's 28 bytes (version 0, as an MP4 writes it; QuickTime's versions 1 and 2
    /// are refused) and its boxes, one of them the elementary stream descriptor naming AAC (<c>esds</c>).
    /// </summary>
    private bool Sound(Box entry)
    {
        if (entry.Type != Mp4a || entry.End - entry.Payload < 28 || BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(entry.Payload + 8)) != 0 ||
            ++_sounds > 1)
            return false;
        var descriptors = 0;
        foreach (var box in Children(entry.Payload + 28, entry.End, 8))
        {
            if (box is null)
                return false;
            var child = box.Value;
            if (child.Type == Esds)
            {
                if (++descriptors > 1 || !Aac(child))
                    return false;
            }
            else
                StripMetadata(child);
        }

        return descriptors == 1;
    }

    /// <summary>
    /// The elementary stream descriptor (ISO/IEC 14496-1): an ES descriptor, its optional fields skipped by their flags, and
    /// within it the decoder configuration, whose object type must be AAC. Every length is checked against its parent.
    /// </summary>
    private bool Aac(Box box)
    {
        var end = box.End;
        if (end - box.Payload < 4 || !Descriptor(box.Payload + 4, end, out var tag, out var start, out var length) || tag != 0x03)
            return false;
        end = start + length;
        var at = start + 3;
        if (at > end)
            return false;
        var flags = bytes[start + 2];
        if ((flags & 0x80) != 0)
            at += 2;
        if ((flags & 0x40) != 0)
        {
            if (at >= end)
                return false;
            at += 1 + bytes[at];
        }

        if ((flags & 0x20) != 0)
            at += 2;
        return at < end && Descriptor(at, end, out tag, out start, out length) && tag == 0x04 && length >= 1 && AacObjectTypes.Contains(bytes[start]);
    }

    /// <summary>A descriptor's tag and its length (up to four bytes of seven bits each), within <paramref name="end"/>.</summary>
    private bool Descriptor(int at, int end, out byte tag, out int start, out int length)
    {
        tag = 0;
        start = 0;
        length = 0;
        if (at >= end)
            return false;
        tag = bytes[at++];
        for (var i = 0; i < 4; i++)
        {
            if (at >= end)
                return false;
            var next = bytes[at++];
            length = (length << 7) | (next & 0x7F);
            if ((next & 0x80) == 0)
            {
                start = at;
                return length <= end - at;
            }
        }

        return false;
    }

    /// <summary>
    /// Free space and metadata: free space (<c>free</c>, <c>skip</c>, <c>wide</c>) is zeroed; user data, <c>meta</c> and
    /// <c>uuid</c> boxes become zeroed free space of the same size, so nothing after them moves. False for any other box.
    /// </summary>
    private bool StripMetadata(Box box)
    {
        if (box.Type == Free || box.Type == Skip || box.Type == Wide)
        {
            bytes.AsSpan(box.Payload, box.End - box.Payload).Clear();
            return true;
        }

        if (box.Type != Udta && box.Type != Meta && box.Type != Uuid)
            return false;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(box.Start + 4), Free);
        // The header is 8 bytes, or 16 with a 64-bit size; a uuid's extended type is cleared with the rest.
        var header = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(box.Start)) == 1 ? 16 : 8;
        bytes.AsSpan(box.Start + header, box.End - box.Start - header).Clear();
        return true;
    }

    /// <summary>The boxes from <paramref name="start"/> to <paramref name="end"/>, each within it; a null ends the walk refused.</summary>
    private IEnumerable<Box?> Children(int start, int end, int depth)
    {
        var position = start;
        while (position < end)
        {
            if (!TryRead(position, end, depth, out var box))
            {
                yield return null;
                yield break;
            }

            yield return box;
            position = box.End;
        }
    }

    /// <summary>
    /// The box at <paramref name="start"/>: a 32-bit size of at least its header, or 1 and a 64-bit size, within
    /// <paramref name="limit"/>. A size of 0 (to the end of the file) is refused, as are boxes past the depth and count limits.
    /// </summary>
    private bool TryRead(int start, int limit, int depth, out Box box)
    {
        box = default;
        if (depth > AdvertisingVideos.MaxDepth || ++_elements > AdvertisingVideos.MaxElements || limit - start < 8)
            return false;
        var size = (long)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(start));
        var type = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(start + 4));
        var header = 8;
        if (size == 1)
        {
            if (limit - start < 16)
                return false;
            var large = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(start + 8));
            if (large > (ulong)(limit - start))
                return false;
            size = (long)large;
            header = 16;
        }

        if (type == Uuid)
            header += 16;
        if (size < header || size > limit - start)
            return false;
        box = new Box(type, start, start + header, start + (int)size);
        return true;
    }

    private static uint Code(string fourCharacters) => BinaryPrimitives.ReadUInt32BigEndian(System.Text.Encoding.ASCII.GetBytes(fourCharacters));

    /// <summary>A box: its type, where it starts, where its payload starts (after the header) and where it ends.</summary>
    private readonly record struct Box(uint Type, int Start, int Payload, int End);
}

/// <summary>
/// A WebM file (Matroska's WebM profile on EBML, RFC 8794 and RFC 9559): an EBML header naming the <c>webm</c> document type,
/// then one segment of known size that ends where the file ends, holding the segment information with its length, the
/// tracks (one picture of VP8, VP9 or AV1, and at most one sound of Opus or Vorbis, neither encrypted nor compressed) and at
/// least one cluster, timed in milliseconds (the timestamp scale WebM writers use). No element of unknown size (a live
/// stream) is taken.
/// </summary>
internal sealed class WebmContainer(byte[] bytes)
{
    private const uint Ebml = 0x1A45DFA3, EbmlVersion = 0x4286, EbmlReadVersion = 0x42F7, EbmlMaxIdLength = 0x42F2,
        EbmlMaxSizeLength = 0x42F3, DocType = 0x4282, DocTypeVersion = 0x4287, DocTypeReadVersion = 0x4285, Segment = 0x18538067,
        SeekHead = 0x114D9B74, Info = 0x1549A966, Tracks = 0x1654AE6B, Cues = 0x1C53BB6B, Cluster = 0x1F43B675, Tags = 0x1254C367,
        Attachments = 0x1941A469, Chapters = 0x1043A770, Void = 0xEC, Crc32 = 0xBF, TimestampScale = 0x2AD7B1, Duration = 0x4489,
        Title = 0x7BA9, DateUtc = 0x4461, TrackEntry = 0xAE, TrackType = 0x83, CodecId = 0x86, Video = 0xE0, Name = 0x536E,
        ContentEncodings = 0x6D80, PixelWidth = 0xB0, PixelHeight = 0xBA, ClusterTimestamp = 0xE7;

    private static readonly HashSet<string> PictureCodecs = new(StringComparer.Ordinal) { "V_VP8", "V_VP9", "V_AV1" };
    private static readonly HashSet<string> SoundCodecs = new(StringComparer.Ordinal) { "A_OPUS", "A_VORBIS" };

    private int _elements;
    private int _pictures;
    private int _sounds;
    private long _width;
    private long _height;
    private ulong _timestampScale = 1_000_000;
    private double _duration = double.NaN;

    /// <summary>The latest a cluster starts, in the segment's timestamp units.</summary>
    private ulong _latestCluster;

    public static bool Starts(ReadOnlySpan<byte> bytes) => bytes.Length >= 4 && bytes[0] == 0x1A && bytes[1] == 0x45 && bytes[2] == 0xDF && bytes[3] == 0xA3;

    public CheckedVideo Check()
    {
        if (!TryRead(0, bytes.Length, 1, out var header) || header.Id != Ebml || !Header(header) ||
            !TryRead(header.End, bytes.Length, 1, out var segment) || segment.Id != Segment || segment.End != bytes.Length || !SegmentBody(segment))
            return null;
        if (_pictures != 1 || double.IsNaN(_duration))
            return null;
        var milliseconds = Math.Ceiling(_duration * _timestampScale / 1_000_000d);
        // The stated length is what is held to the limit; a cluster starting beyond it would play past it.
        if (Math.Ceiling((double)_latestCluster * _timestampScale / 1_000_000d) > milliseconds + AdvertisingVideos.StatedLengthTolerance)
            return null;
        return double.IsFinite(milliseconds) && milliseconds > 0
            ? new CheckedVideo("video/webm", "webm", (int)_width, (int)_height, milliseconds >= long.MaxValue ? long.MaxValue : (long)milliseconds)
            : null;
    }

    /// <summary>The EBML header: EBML version 1, identifiers of at most 4 bytes and sizes of at most 8, and the <c>webm</c> document type.</summary>
    private bool Header(Element header)
    {
        var webm = false;
        foreach (var element in Children(header, 2))
        {
            if (element is null)
                return false;
            var child = element.Value;
            switch (child.Id)
            {
                case EbmlVersion or EbmlReadVersion:
                    if (!Unsigned(child, out var version) || version != 1)
                        return false;
                    break;
                case EbmlMaxIdLength:
                    if (!Unsigned(child, out var idLength) || idLength is 0 or > 4)
                        return false;
                    break;
                case EbmlMaxSizeLength:
                    if (!Unsigned(child, out var sizeLength) || sizeLength is 0 or > 8)
                        return false;
                    break;
                case DocType:
                    webm = Text(child, 16) == "webm";
                    if (!webm)
                        return false;
                    break;
                case DocTypeVersion:
                    if (!Unsigned(child, out _))
                        return false;
                    break;
                case DocTypeReadVersion:
                    if (!Unsigned(child, out var readVersion) || readVersion is 0 or > 4)
                        return false;
                    break;
                case Void or Crc32:
                    MakeVoid(child);
                    break;
                default:
                    return false;
            }
        }

        return webm;
    }

    private bool SegmentBody(Element segment)
    {
        var information = 0;
        var tracks = 0;
        var clusters = 0;
        foreach (var element in Children(segment, 2))
        {
            if (element is null)
                return false;
            var child = element.Value;
            switch (child.Id)
            {
                case SeekHead or Cues:
                    break;
                case Cluster:
                    clusters++;
                    if (!ClusterStart(child))
                        return false;
                    break;
                case Info:
                    if (++information > 1 || !SegmentInformation(child))
                        return false;
                    break;
                case Tracks:
                    if (++tracks > 1 || !TrackList(child))
                        return false;
                    break;
                case Tags or Attachments or Chapters or Void or Crc32:
                    MakeVoid(child);
                    break;
                default:
                    return false;
            }
        }

        return information == 1 && tracks == 1 && clusters > 0;
    }

    /// <summary>
    /// A cluster's start: its first element is its timestamp (as Matroska writers put it, before any block), read without
    /// walking the blocks; no cluster may start after the file's length (<see cref="Check"/>).
    /// </summary>
    private bool ClusterStart(Element cluster)
    {
        if (!TryRead(cluster.Data, cluster.End, 3, out var first) || first.Id != ClusterTimestamp || !Unsigned(first, out var start))
            return false;
        _latestCluster = Math.Max(_latestCluster, start);
        return true;
    }

    /// <summary>The segment information: the timestamp scale and the length kept, the title and the date made void.</summary>
    private bool SegmentInformation(Element information)
    {
        foreach (var element in Children(information, 3))
        {
            if (element is null)
                return false;
            var child = element.Value;
            switch (child.Id)
            {
                case TimestampScale:
                    // A millisecond, as WebM writers put it: a block's 16-bit offset from its cluster's start then spans
                    // at most 32.8 seconds, so a cluster cannot play far past the stated length with coarse units.
                    if (!Unsigned(child, out _timestampScale) || _timestampScale != 1_000_000)
                        return false;
                    break;
                case Duration:
                    if (!Float(child, out _duration) || !double.IsFinite(_duration) || _duration <= 0)
                        return false;
                    break;
                case Title or DateUtc or Void or Crc32:
                    MakeVoid(child);
                    break;
            }
        }

        return true;
    }

    private bool TrackList(Element list)
    {
        foreach (var element in Children(list, 3))
        {
            if (element is null)
                return false;
            var child = element.Value;
            if (child.Id == TrackEntry)
            {
                if (!Track(child))
                    return false;
            }
            else if (child.Id is Void or Crc32)
                MakeVoid(child);
        }

        return true;
    }

    /// <summary>A track: a picture of VP8, VP9 or AV1 within the size limits, or a sound of Opus or Vorbis; its name made void.</summary>
    private bool Track(Element track)
    {
        ulong type = 0;
        string codec = null;
        long width = 0;
        long height = 0;
        foreach (var element in Children(track, 4))
        {
            if (element is null)
                return false;
            var child = element.Value;
            switch (child.Id)
            {
                case TrackType:
                    if (!Unsigned(child, out type))
                        return false;
                    break;
                case CodecId:
                    codec = Text(child, 32);
                    if (codec is null)
                        return false;
                    break;
                case Video:
                    if (!Picture(child, out width, out height))
                        return false;
                    break;
                case ContentEncodings:
                    // Encrypted or compressed blocks: refused.
                    return false;
                case Name or Void or Crc32:
                    MakeVoid(child);
                    break;
            }
        }

        if (type != 1)
            return type == 2 && SoundCodecs.Contains(codec ?? string.Empty) && ++_sounds == 1;
        if (!PictureCodecs.Contains(codec ?? string.Empty) || ++_pictures > 1 || !AdvertisingVideos.Fits(width, height))
            return false;
        // The picture track's own size, never another track's.
        _width = width;
        _height = height;
        return true;
    }

    /// <summary>A track's picture settings: its size in pixels (both required, so a track without them is refused).</summary>
    private bool Picture(Element video, out long width, out long height)
    {
        width = 0;
        height = 0;
        foreach (var element in Children(video, 5))
        {
            if (element is null)
                return false;
            var child = element.Value;
            ulong value;
            switch (child.Id)
            {
                case PixelWidth:
                    if (!Unsigned(child, out value) || value > AdvertisingMedia.MaxSide)
                        return false;
                    width = (long)value;
                    break;
                case PixelHeight:
                    if (!Unsigned(child, out value) || value > AdvertisingMedia.MaxSide)
                        return false;
                    height = (long)value;
                    break;
                case Void or Crc32:
                    MakeVoid(child);
                    break;
            }
        }

        return true;
    }

    /// <summary>
    /// Turns the element into a <c>Void</c> element of the same total length, its data zeroed, so nothing after it moves:
    /// the one-byte Void identifier, then a size of up to eight bytes that takes up the rest of the old header.
    /// </summary>
    private void MakeVoid(Element element)
    {
        var total = element.End - element.Start;
        var sizeLength = Math.Clamp(element.Data - element.Start - 1, 1, 8);
        var dataLength = (ulong)(total - 1 - sizeLength);
        bytes[element.Start] = (byte)Void;
        var size = dataLength | (1UL << (7 * sizeLength));
        for (var i = sizeLength - 1; i >= 0; i--)
        {
            bytes[element.Start + 1 + i] = (byte)size;
            size >>= 8;
        }

        bytes.AsSpan(element.Start + 1 + sizeLength, (int)dataLength).Clear();
    }

    /// <summary>An unsigned integer element of at most eight bytes (none is zero).</summary>
    private bool Unsigned(Element element, out ulong value)
    {
        value = 0;
        if (element.End - element.Data > 8)
            return false;
        for (var at = element.Data; at < element.End; at++)
            value = (value << 8) | bytes[at];
        return true;
    }

    /// <summary>A float element of four or eight bytes (none is zero).</summary>
    private bool Float(Element element, out double value)
    {
        value = 0;
        switch (element.End - element.Data)
        {
            case 0:
                return true;
            case 4:
                value = BinaryPrimitives.ReadSingleBigEndian(bytes.AsSpan(element.Data));
                return true;
            case 8:
                value = BinaryPrimitives.ReadDoubleBigEndian(bytes.AsSpan(element.Data));
                return true;
            default:
                return false;
        }
    }

    /// <summary>An ASCII string element of at most <paramref name="maxLength"/> bytes (zero padding allowed after it); null otherwise.</summary>
    private string Text(Element element, int maxLength)
    {
        var length = element.End - element.Data;
        if (length > maxLength)
            return null;
        var span = bytes.AsSpan(element.Data, length);
        var end = span.IndexOf((byte)0);
        if (end >= 0)
        {
            if (span[end..].ContainsAnyExcept((byte)0))
                return null;
            span = span[..end];
        }

        foreach (var b in span)
        {
            if (b is < 0x20 or > 0x7E)
                return null;
        }

        return System.Text.Encoding.ASCII.GetString(span);
    }

    private IEnumerable<Element?> Children(Element parent, int depth)
    {
        var position = parent.Data;
        while (position < parent.End)
        {
            if (!TryRead(position, parent.End, depth, out var element))
            {
                yield return null;
                yield break;
            }

            yield return element;
            position = element.End;
        }
    }

    /// <summary>
    /// The element at <paramref name="start"/>: an identifier of one to four bytes and a size of one to eight, both
    /// variable-length integers, the size known (not all ones) and within <paramref name="limit"/>; refused past the depth
    /// and count limits.
    /// </summary>
    private bool TryRead(int start, int limit, int depth, out Element element)
    {
        element = default;
        if (depth > AdvertisingVideos.MaxDepth || ++_elements > AdvertisingVideos.MaxElements || start >= limit || bytes[start] == 0)
            return false;
        var idLength = BitOperations.LeadingZeroCount((uint)bytes[start]) - 23;
        if (idLength > 4 || limit - start < idLength + 1)
            return false;
        uint id = 0;
        for (var i = 0; i < idLength; i++)
            id = (id << 8) | bytes[start + i];
        var at = start + idLength;
        if (bytes[at] == 0)
            return false;
        var sizeLength = BitOperations.LeadingZeroCount((uint)bytes[at]) - 23;
        if (limit - at < sizeLength)
            return false;
        var size = (ulong)(bytes[at] & (0xFF >> sizeLength));
        for (var i = 1; i < sizeLength; i++)
            size = (size << 8) | bytes[at + i];
        var data = at + sizeLength;
        if (size == (1UL << (7 * sizeLength)) - 1 || size > (ulong)(limit - data))
            return false;
        element = new Element(id, start, data, data + (int)size);
        return true;
    }

    /// <summary>An element: its identifier, where it starts, where its data starts and where it ends.</summary>
    private readonly record struct Element(uint Id, int Start, int Data, int End);
}
