using System.Buffers.Binary;
using System.Text;
using Ariva.Core.Domain.Entities;
using Ariva.Infra.Services.Displays;
using Ariva.Infra.Storage;
using FluentAssertions;

namespace Ariva.UnitTests.Storage;

/// <summary>
/// ARV-142b: advertisement videos are typed by their container, read by managed code only, and stripped of their metadata
/// without moving the media. MP4 with H.264 (AAC or no sound) and WebM with VP8, VP9 or AV1 (Opus, Vorbis or no sound), of
/// ffmpeg's own making (Platform/Testing/Media), are taken with their size and length; other codecs and track kinds,
/// fragmented, live, truncated and encrypted files, files with data after their end, lying sizes and too many boxes are
/// refused; user data, meta and uuid boxes become zeroed free space, WebM tags, titles and names become Void elements, and
/// markup hidden in free space is zeroed.
/// </summary>
public sealed class AdvertisingVideosTests
{
    private const string Markup = "<html><script>alert(document.domain)</script></html>";

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Media", name));

    private static bool Holds(byte[] bytes, string text) => bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(text)) >= 0;

    #region MP4 helpers

    private static uint Code(string type) => BinaryPrimitives.ReadUInt32BigEndian(Encoding.ASCII.GetBytes(type));

    /// <summary>The boxes from start to end: (type, start, payload, end), as a test reads them (32-bit sizes only).</summary>
    private static IEnumerable<(string Type, int Start, int Payload, int End)> Boxes(byte[] bytes, int start, int end)
    {
        for (var at = start; at + 8 <= end;)
        {
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at));
            if (size < 8)
                yield break;
            yield return (Encoding.ASCII.GetString(bytes, at + 4, 4), at, at + 8, at + size);
            at += size;
        }
    }

    /// <summary>The first box along the path ("moov", "trak", "mdia", "hdlr"); a sample entry is reached as "stsd", then its type.</summary>
    private static (string Type, int Start, int Payload, int End) Box(byte[] bytes, params string[] path)
    {
        var (start, end) = (0, bytes.Length);
        (string Type, int Start, int Payload, int End) found = default;
        foreach (var type in path)
        {
            found = Boxes(bytes, start, end).First(b => b.Type == type);
            (start, end) = found.Type == "stsd" ? (found.Payload + 8, found.End) : (found.Payload, found.End);
        }

        return found;
    }

    private static byte[] AppendBox(byte[] bytes, string type, byte[] payload)
    {
        var header = type == "uuid" ? 24 : 8;
        var box = new byte[header + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        payload.CopyTo(box, header);
        return [.. bytes, .. box];
    }

    private static void SetType(byte[] bytes, int start, string type) => Encoding.ASCII.GetBytes(type).CopyTo(bytes, start + 4);

    #endregion

    #region WebM helpers

    private static int Find(byte[] bytes, params byte[] id) => bytes.AsSpan().IndexOf(id);

    /// <summary>The variable-length integer at <paramref name="at"/>: its value (marker kept for an identifier) and its length.</summary>
    private static (ulong Value, int Length) Vint(byte[] bytes, int at, bool marker)
    {
        var length = System.Numerics.BitOperations.LeadingZeroCount((uint)bytes[at]) - 23;
        var value = marker ? bytes[at] : (ulong)(bytes[at] & (0xFF >> length));
        for (var i = 1; i < length; i++)
            value = (value << 8) | bytes[at + i];
        return (value, length);
    }

    /// <summary>Where the Segment's own child with this identifier starts (not a SeekHead's reference to it).</summary>
    private static int SegmentChild(byte[] bytes, uint id)
    {
        var segment = Find(bytes, 0x18, 0x53, 0x80, 0x67);
        var (size, sizeLength) = Vint(bytes, segment + 4, false);
        var at = segment + 4 + sizeLength;
        var end = at + (int)size;
        while (at < end)
        {
            var (child, idLength) = Vint(bytes, at, true);
            if (child == id)
                return at;
            var (length, lengthLength) = Vint(bytes, at + idLength, false);
            at += idLength + lengthLength + (int)length;
        }

        return -1;
    }

    private static int Find(byte[] bytes, string text) => bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(text));

    #endregion

    [Theory]
    [InlineData("h264-aac.mp4", "video/mp4", "mp4")]
    [InlineData("h264-moov-last.mp4", "video/mp4", "mp4")]
    [InlineData("vp9-opus.webm", "video/webm", "webm")]
    [InlineData("vp8-vorbis.webm", "video/webm", "webm")]
    [InlineData("av1.webm", "video/webm", "webm")]
    public void Check_Should_TakeTheVideoWithItsSizeAndLength_When_ItIsAnAllowedContainerAndCodec(string name, string contentType, string extension)
    {
        var bytes = Fixture(name);
        var length = bytes.Length;

        AdvertisingVideos.LooksLikeVideo(bytes).Should().BeTrue();
        var video = AdvertisingVideos.Check(bytes);

        video.Should().NotBeNull(name);
        video.ContentType.Should().Be(contentType);
        video.Extension.Should().Be(extension);
        (video.WidthPixels, video.HeightPixels).Should().Be((160, 90));
        video.DurationMilliseconds.Should().BeInRange(1_950, 2_050, "two seconds, as ffprobe reads it");
        bytes.Length.Should().Be(length, "nothing is added or removed");
        AdvertisingVideos.Check(bytes).Should().Be(video, "a stripped video is taken again as it is");
    }

    [Theory]
    [InlineData("h264-aac.mp4")]
    [InlineData("h264-moov-last.mp4")]
    public void Check_Should_StripTheMetadataWithoutMovingTheMedia_When_AnMp4CarriesUserData(string name)
    {
        var original = Fixture(name);
        var bytes = (byte[])original.Clone();
        var media = Box(original, "mdat");
        var userData = Box(original, "moov", "udta");
        Holds(original, "Lavf").Should().BeTrue("ffmpeg names itself in the user data");
        Holds(original, "VideoHandler").Should().BeTrue("and in the handler names");

        AdvertisingVideos.Check(bytes).Should().NotBeNull();

        bytes.AsSpan(media.Start, media.End - media.Start).SequenceEqual(original.AsSpan(media.Start, media.End - media.Start))
            .Should().BeTrue("the media data and its offsets stay where they were");
        Encoding.ASCII.GetString(bytes, userData.Start + 4, 4).Should().Be("free");
        bytes.AsSpan(userData.Payload, userData.End - userData.Payload).ContainsAnyExcept((byte)0).Should().BeFalse("its contents zeroed");
        foreach (var text in new[] { "Fixture title", "fixture comment", "Lavf", "VideoHandler", "SoundHandler", "ilst" })
            Holds(bytes, text).Should().BeFalse(text);
        var entry = Box(bytes, "moov", "trak", "mdia", "minf", "stbl", "stsd", "avc1");
        bytes.AsSpan(entry.Payload + 42, 32).ContainsAnyExcept((byte)0).Should().BeFalse("the compressor name is zeroed");
    }

    [Fact]
    public void Check_Should_ZeroTheCreationAndChangeTimes_When_AnMp4CarriesThem()
    {
        var bytes = Fixture("h264-aac.mp4");
        var header = Box(bytes, "moov", "mvhd");
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(header.Payload + 4), 3_800_000_000);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(header.Payload + 8), 3_800_000_001);

        AdvertisingVideos.Check(bytes).Should().NotBeNull();

        bytes.AsSpan(header.Payload + 4, 8).ContainsAnyExcept((byte)0).Should().BeFalse();
        BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(header.Payload + 12)).Should().BeGreaterThan(0, "the timescale stays");
    }

    [Theory]
    [InlineData("vp9-opus.webm")]
    [InlineData("av1.webm")]
    public void Check_Should_MakeTagsTitlesAndNamesVoid_When_AWebmCarriesThem(string name)
    {
        var original = Fixture(name);
        var bytes = (byte[])original.Clone();
        var cluster = SegmentChild(original, 0x1F43B675);
        var tags = SegmentChild(original, 0x1254C367);
        tags.Should().BePositive("ffmpeg writes its encoder in the tags");

        AdvertisingVideos.Check(bytes).Should().NotBeNull();

        bytes[tags].Should().Be(0xEC, "the tags are a Void element now");
        SegmentChild(bytes, 0x1254C367).Should().Be(-1);
        SegmentChild(bytes, 0xEC).Should().BePositive();
        Holds(bytes, "Fixture title").Should().BeFalse();
        Holds(bytes, "ENCODER").Should().BeFalse();
        bytes.AsSpan(cluster).SequenceEqual(original.AsSpan(cluster)).Should().BeTrue("the clusters and what follows them stay where they were");
    }

    [Theory]
    [InlineData("free")]
    [InlineData("skip")]
    [InlineData("uuid")]
    [InlineData("meta")]
    [InlineData("udta")]
    public void Check_Should_ZeroMarkupHiddenInFreeSpaceOrMetadata_When_AnMp4CarriesIt(string type)
    {
        var bytes = AppendBox(Fixture("h264-aac.mp4"), type, Encoding.ASCII.GetBytes(Markup));
        var start = bytes.Length - (Markup.Length + (type == "uuid" ? 24 : 8));

        AdvertisingVideos.Check(bytes).Should().NotBeNull("free space and metadata are taken, emptied");

        Holds(bytes, "<script>").Should().BeFalse();
        Encoding.ASCII.GetString(bytes, start + 4, 4).Should().Be(type is "skip" ? "skip" : "free");
    }

    [Theory]
    [InlineData("h264-aac.mp4")]
    [InlineData("h264-moov-last.mp4")]
    [InlineData("vp9-opus.webm")]
    [InlineData("av1.webm")]
    public void Check_Should_Refuse_When_DataFollowsTheLastBoxOrElement(string name)
    {
        AdvertisingVideos.Check([.. Fixture(name), .. Encoding.ASCII.GetBytes(Markup)]).Should().BeNull("a polyglot's page after the video");
        AdvertisingVideos.Check([.. Fixture(name), 0, 0, 0]).Should().BeNull("even a few bytes");
    }

    [Theory]
    [InlineData("h264-aac.mp4")]
    [InlineData("h264-moov-last.mp4")]
    [InlineData("vp9-opus.webm")]
    [InlineData("vp8-vorbis.webm")]
    public void Check_Should_Refuse_When_TheFileIsTruncated(string name)
    {
        var bytes = Fixture(name);
        AdvertisingVideos.Check(bytes[..^1]).Should().BeNull("one byte short");
        AdvertisingVideos.Check(bytes[..(bytes.Length / 2)]).Should().BeNull("half of it");
        AdvertisingVideos.Check(bytes[..40]).Should().BeNull("its header only");
    }

    [Fact]
    public void Check_Should_Refuse_When_AnMp4HoldsAnotherCodec()
    {
        var hevc = Fixture("h264-aac.mp4");
        SetType(hevc, Box(hevc, "moov", "trak", "mdia", "minf", "stbl", "stsd", "avc1").Start, "hvc1");
        AdvertisingVideos.Check(hevc).Should().BeNull("HEVC");

        var mp3 = Fixture("h264-aac.mp4");
        var movie = Box(mp3, "moov");
        var sound = Boxes(mp3, movie.Payload, movie.End).Where(b => b.Type == "trak").Skip(1).First();
        // After the esds box's header and its version and flags: the ES descriptor (tag 3, its length, its id and flags),
        // then the decoder configuration (tag 4, its length) whose first byte is the object type.
        var descriptor = mp3.AsSpan(sound.Start, sound.End - sound.Start).IndexOf("esds"u8) + sound.Start + 8;
        var at = mp3.AsSpan(descriptor, sound.End - descriptor).IndexOf((byte)0x04) + descriptor + 1;
        while ((mp3[at] & 0x80) != 0)
            at++;
        mp3[at + 1].Should().Be(0x40, "AAC, as ffmpeg writes it");
        mp3[at + 1] = 0x6B;
        AdvertisingVideos.Check(mp3).Should().BeNull("MP3 sound");
    }

    [Theory]
    [InlineData("vp9-opus.webm", "V_VP9", "V_XYZ")]
    [InlineData("vp9-opus.webm", "A_OPUS", "A_FLAC")]
    [InlineData("av1.webm", "V_AV1", "V_MS/")]
    public void Check_Should_Refuse_When_AWebmHoldsAnotherCodec(string name, string codec, string other)
    {
        var bytes = Fixture(name);
        Encoding.ASCII.GetBytes(other).CopyTo(bytes, Find(bytes, codec));
        AdvertisingVideos.Check(bytes).Should().BeNull(other);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(uint.MaxValue)]
    public void Check_Should_Refuse_When_AnMp4HasNoLength(uint duration)
    {
        var bytes = Fixture("h264-aac.mp4");
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(Box(bytes, "moov", "mvhd").Payload + 16), duration);
        AdvertisingVideos.Check(bytes).Should().BeNull();
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-2008d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Check_Should_Refuse_When_AWebmHasNoLength(double duration)
    {
        var bytes = Fixture("vp9-opus.webm");
        var at = Find(bytes, 0x44, 0x89, 0x88);
        BinaryPrimitives.WriteDoubleBigEndian(bytes.AsSpan(at + 3), duration);
        AdvertisingVideos.Check(bytes).Should().BeNull();

        var missing = Fixture("vp9-opus.webm");
        missing[at] = 0x45;
        missing[at + 1] = 0x89;
        AdvertisingVideos.Check(missing).Should().BeNull("an unknown element where the length was: a live recording has none");
    }

    [Theory]
    [InlineData(2_900u, true, "within a second of the movie's two seconds")]
    [InlineData(3_001u, false, "more than a second beyond it")]
    [InlineData(uint.MaxValue, false, "unknown")]
    public void Check_Should_HoldTrackLengthsToTheMovies_When_AnMp4StatesThem(uint trackDuration, bool taken, string reason)
    {
        var bytes = Fixture("h264-aac.mp4");
        var header = Box(bytes, "moov", "trak", "tkhd");
        // Version 0: creation and change times, the track id and a reserved word, then the length in the movie's timescale.
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(header.Payload + 4 + 8 + 8), trackDuration);
        (AdvertisingVideos.Check(bytes) is not null).Should().Be(taken, reason);
    }

    [Fact]
    public void Check_Should_HoldMediaLengthsToTheMovies_When_AnMp4StatesThem()
    {
        var bytes = Fixture("h264-aac.mp4");
        var header = Box(bytes, "moov", "trak", "mdia", "mdhd");
        var timescale = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(header.Payload + 12));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(header.Payload + 16), timescale * 60);
        AdvertisingVideos.Check(bytes).Should().BeNull("a picture of a minute behind a movie of two seconds");

        var unscaled = Fixture("h264-aac.mp4");
        BinaryPrimitives.WriteUInt32BigEndian(unscaled.AsSpan(header.Payload + 12), 0);
        AdvertisingVideos.Check(unscaled).Should().BeNull("a media timescale of zero");
    }

    /// <summary>Writes a variable-length size of this length (its marker bit set) at the position.</summary>
    private static void WriteVint(byte[] bytes, int at, int length, ulong value)
    {
        var encoded = value | (1UL << (7 * length));
        for (var i = length - 1; i >= 0; i--)
        {
            bytes[at + i] = (byte)encoded;
            encoded >>= 8;
        }
    }

    [Theory]
    [InlineData(2_000, true, "the cluster starts before the length of 2,008 ms")]
    [InlineData(3_008, true, "within the second's tolerance")]
    [InlineData(3_009, false, "more than a second past the length")]
    [InlineData(60_000, false, "a minute in")]
    public void Check_Should_HoldClusterStartsToTheLength_When_AWebmStatesThem(int start, bool taken, string reason)
    {
        var original = Fixture("vp9-opus.webm");
        var cluster = SegmentChild(original, 0x1F43B675);
        var (clusterSize, clusterSizeLength) = Vint(original, cluster + 4, false);
        var timestamp = cluster + 4 + clusterSizeLength;
        original.AsSpan(timestamp, 3).SequenceEqual(new byte[] { 0xE7, 0x81, 0x00 }).Should().BeTrue("ffmpeg starts the cluster at 0, in one byte");
        // The cluster's timestamp widened to two bytes: the cluster and the segment grow by one.
        byte[] bytes = [.. original[..timestamp], 0xE7, 0x82, (byte)(start >> 8), (byte)start, .. original[(timestamp + 3)..]];
        WriteVint(bytes, cluster + 4, clusterSizeLength, clusterSize + 1);
        var segmentStart = Find(bytes, 0x18, 0x53, 0x80, 0x67);
        var (segmentSize, segmentSizeLength) = Vint(bytes, segmentStart + 4, false);
        WriteVint(bytes, segmentStart + 4, segmentSizeLength, segmentSize + 1);

        (AdvertisingVideos.Check(bytes) is not null).Should().Be(taken, reason);
    }

    [Fact]
    public void Check_Should_Refuse_When_AWebmIsNotTimedInMilliseconds()
    {
        var bytes = Fixture("vp9-opus.webm");
        var scale = Find(bytes, 0x2A, 0xD7, 0xB1, 0x83, 0x0F, 0x42, 0x40);
        bytes[scale + 6] = 0x41;
        AdvertisingVideos.Check(bytes).Should().BeNull("a timestamp unit of 999,999 ns: a block's offset could then reach past the length");
    }

    [Fact]
    public void Check_Should_Refuse_When_AWebmClusterDoesNotStartWithItsTimestamp()
    {
        var bytes = Fixture("vp9-opus.webm");
        var cluster = SegmentChild(bytes, 0x1F43B675);
        var (_, sizeLength) = Vint(bytes, cluster + 4, false);
        bytes[cluster + 4 + sizeLength] = 0xA3;
        AdvertisingVideos.Check(bytes).Should().BeNull("a block before the cluster's timestamp");
    }

    [Fact]
    public void Check_Should_Refuse_When_AnMp4IsFragmentedOrQuickTime()
    {
        var fragmented = Fixture("h264-aac.mp4");
        SetType(fragmented, Box(fragmented, "moov", "udta").Start, "mvex");
        AdvertisingVideos.Check(fragmented).Should().BeNull("movie extends");

        var fragment = Fixture("h264-aac.mp4");
        SetType(fragment, Box(fragment, "free").Start, "moof");
        AdvertisingVideos.Check(fragment).Should().BeNull("a movie fragment");

        var quickTime = Fixture("h264-aac.mp4");
        var brands = Box(quickTime, "ftyp");
        for (var at = brands.Payload; at < brands.End; at += 4)
        {
            if (at != brands.Payload + 4)
                Encoding.ASCII.GetBytes("qt  ").CopyTo(quickTime, at);
        }

        AdvertisingVideos.Check(quickTime).Should().BeNull("QuickTime");

        var second = Fixture("h264-aac.mp4");
        SetType(second, Box(second, "free").Start, "ftyp");
        AdvertisingVideos.Check(second).Should().BeNull("a second file type box");
    }

    [Theory]
    [InlineData(0u, "zero: to the end of the file")]
    [InlineData(4u, "smaller than its header")]
    [InlineData(100_000u, "beyond its parent")]
    public void Check_Should_Refuse_When_ABoxSizeLies(uint size, string reason)
    {
        var bytes = Fixture("h264-aac.mp4");
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(Box(bytes, "moov", "trak").Start), size);
        AdvertisingVideos.Check(bytes).Should().BeNull(reason);

        var media = Fixture("h264-aac.mp4");
        BinaryPrimitives.WriteUInt32BigEndian(media.AsSpan(Box(media, "mdat").Start), size);
        AdvertisingVideos.Check(media).Should().BeNull(reason);
    }

    [Fact]
    public void Check_Should_Refuse_When_A64BitBoxSizeIsLargerThanTheFile()
    {
        var bytes = Fixture("h264-aac.mp4");
        var box = new byte[24];
        BinaryPrimitives.WriteUInt32BigEndian(box, 1);
        Encoding.ASCII.GetBytes("free").CopyTo(box, 4);
        BinaryPrimitives.WriteUInt64BigEndian(box.AsSpan(8), long.MaxValue);
        AdvertisingVideos.Check([.. bytes, .. box]).Should().BeNull();

        BinaryPrimitives.WriteUInt64BigEndian(box.AsSpan(8), 24);
        AdvertisingVideos.Check([.. bytes, .. box]).Should().NotBeNull("a 64-bit size that holds");
    }

    [Theory]
    [InlineData("text")]
    [InlineData("sbtl")]
    [InlineData("meta")]
    [InlineData("tmcd")]
    public void Check_Should_Refuse_When_AnMp4TrackIsNotAPictureOrASound(string handler)
    {
        var bytes = Fixture("h264-aac.mp4");
        Encoding.ASCII.GetBytes(handler).CopyTo(bytes, Box(bytes, "moov", "trak", "mdia", "hdlr").Payload + 8);
        AdvertisingVideos.Check(bytes).Should().BeNull(handler);
    }

    [Fact]
    public void Check_Should_Refuse_When_AnMp4HasTwoPictures()
    {
        var bytes = Fixture("h264-aac.mp4");
        var movie = Box(bytes, "moov");
        var track = Box(bytes, "moov", "trak");
        var copy = bytes[track.Start..track.End];
        var twice = bytes[..track.End].Concat(copy).Concat(bytes[track.End..]).ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(twice.AsSpan(movie.Start), (uint)(movie.End - movie.Start + copy.Length));
        AdvertisingVideos.Check(twice).Should().BeNull();
    }

    [Theory]
    [InlineData(3840, 2160, true)]
    [InlineData(2160, 3840, true)]
    [InlineData(3841, 90, false)]
    [InlineData(3840, 2161, false)]
    [InlineData(0, 90, false)]
    public void Check_Should_HoldThePictureToAScreen_When_AnMp4StatesItsSize(int width, int height, bool taken)
    {
        var bytes = Fixture("h264-aac.mp4");
        var entry = Box(bytes, "moov", "trak", "mdia", "minf", "stbl", "stsd", "avc1");
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(entry.Payload + 24), (ushort)width);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(entry.Payload + 26), (ushort)height);

        var video = AdvertisingVideos.Check(bytes);

        (video is not null).Should().Be(taken, $"{width} by {height}");
        if (taken)
            (video.WidthPixels, video.HeightPixels).Should().Be((width, height));
    }

    [Fact]
    public void Check_Should_Refuse_When_AWebmPictureHasNoSize()
    {
        var bytes = Fixture("vp9-opus.webm");
        bytes[Find(bytes, 0xB0, 0x81, 0xA0) + 2] = 0;
        AdvertisingVideos.Check(bytes).Should().BeNull("a width of zero");
    }

    [Fact]
    public void Check_Should_Refuse_When_AWebmIsNotAWebmOrIsLive()
    {
        var matroska = Fixture("vp9-opus.webm");
        Encoding.ASCII.GetBytes("mkv ").CopyTo(matroska, Find(matroska, "webm"));
        AdvertisingVideos.Check(matroska).Should().BeNull("another document type");

        var live = Fixture("vp9-opus.webm");
        var segment = Find(live, 0x18, 0x53, 0x80, 0x67);
        live[segment + 4] = 0x01;
        live.AsSpan(segment + 5, 7).Fill(0xFF);
        AdvertisingVideos.Check(live).Should().BeNull("a segment of unknown size, as a live recording writes");

        var subtitles = Fixture("vp9-opus.webm");
        var type = Find(subtitles, 0x83, 0x81, 0x01);
        subtitles[type + 2] = 0x11;
        AdvertisingVideos.Check(subtitles).Should().BeNull("a subtitle track");
    }

    [Fact]
    public void Check_Should_Refuse_When_AWebmTrackIsEncrypted()
    {
        var bytes = Fixture("vp9-opus.webm");
        // The first track's unique id (0x73C5) becomes its content encodings (0x6D80): encrypted or compressed blocks.
        var uid = Find(bytes, 0x73, 0xC5, 0x88);
        bytes[uid] = 0x6D;
        bytes[uid + 1] = 0x80;
        AdvertisingVideos.Check(bytes).Should().BeNull();
    }

    [Theory]
    [InlineData(100, true)]
    [InlineData(AdvertisingVideos.MaxElements, false)]
    public void Check_Should_ReadAtMostTheBoxLimit_When_AnMp4IsFullOfTinyBoxes(int count, bool taken)
    {
        var bytes = Fixture("h264-aac.mp4");
        var tiny = new byte[8 * count];
        for (var i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(tiny.AsSpan(i * 8), 8);
            Encoding.ASCII.GetBytes("free").CopyTo(tiny, (i * 8) + 4);
        }

        (AdvertisingVideos.Check([.. bytes, .. tiny]) is not null).Should().Be(taken);
    }

    [Theory]
    [InlineData(100, true)]
    [InlineData(AdvertisingVideos.MaxElements, false)]
    public void Check_Should_ReadAtMostTheElementLimit_When_AWebmIsFullOfTinyElements(int count, bool taken)
    {
        var bytes = Fixture("vp9-opus.webm");
        var segment = Find(bytes, 0x18, 0x53, 0x80, 0x67);
        bytes[segment + 4].Should().Be(0x01, "ffmpeg writes the segment's size in eight bytes");
        var size = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(segment + 4)) & 0x00FF_FFFF_FFFF_FFFF;
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(segment + 4), (size + (ulong)(2 * count)) | 0x0100_0000_0000_0000);
        var voids = Enumerable.Repeat(new byte[] { 0xEC, 0x80 }, count).SelectMany(v => v).ToArray();

        (AdvertisingVideos.Check([.. bytes, .. voids]) is not null).Should().Be(taken);
    }

    [Fact]
    public void Check_Should_Refuse_When_TheFileIsNotAVideo()
    {
        AdvertisingVideos.LooksLikeVideo([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52]).Should().BeFalse("a PNG");
        AdvertisingVideos.Check(Encoding.ASCII.GetBytes(Markup)).Should().BeNull();
        AdvertisingVideos.Check([]).Should().BeNull();
        var header = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(header, 16);
        Encoding.ASCII.GetBytes("ftypisom").CopyTo(header, 4);
        AdvertisingVideos.Check(header).Should().BeNull("a file type box and nothing else");
        AdvertisingVideos.Check([0x1A, 0x45, 0xDF, 0xA3]).Should().BeNull("an EBML signature and nothing else");
    }

    [Theory]
    [InlineData(20, 30, true)]
    [InlineData(1, 1, true)]
    [InlineData(100, 120, true)]
    [InlineData(0, 30, false)]
    [InlineData(101, 30, false)]
    [InlineData(20, 0, false)]
    [InlineData(20, 121, false)]
    public void Settings_Should_StayWithinTheCeilings_When_AnInstallationSetsThem(int megabytes, int seconds, bool valid) =>
        new AdvertisingVideoSettings { MaxMegabytes = megabytes, MaxSeconds = seconds }.IsValid.Should().Be(valid);

    [Fact]
    public void Settings_Should_TakeTwentyMegabytesAndThirtySeconds_When_NothingIsSet()
    {
        var settings = new AdvertisingVideoSettings();
        (settings.MaxBytes, settings.MaxSeconds).Should().Be((20L * 1024 * 1024, 30));
        settings.MaxBytes.Should().BeLessThan(AdvertisingMedia.MaxVideoBytes);
    }
}
