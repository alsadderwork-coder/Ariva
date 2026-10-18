using Ariva.Core.Domain.Entities;
using Ariva.Infra.Storage;
using CsCheck;
using FluentAssertions;

namespace Ariva.UnitTests.Properties;

/// <summary>
/// ARV-142b, CWE-120, CWE-400 and CWE-434: the advertisement video check given valid MP4 and WebM videos cut short or
/// mutated, anywhere or within their headers (where the boxes and elements are): each answers with a video of an allowed
/// type within the limits, of the same length in bytes, that the check takes again as it is, or with null; never an
/// exception. A failure prints CsCheck's shrunk input and seed.
/// </summary>
public sealed class AdvertisingVideoPropertyTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Media", name));

    private static void Holds(byte[] input)
    {
        var bytes = (byte[])input.Clone();
        var video = AdvertisingVideos.Check(bytes);
        if (video is null)
            return;
        AdvertisingMediaFile.VideoTypes.Should().ContainKey(video.ContentType);
        AdvertisingMediaFile.VideoTypes[video.ContentType].Should().Be(video.Extension);
        AdvertisingVideos.Fits(video.WidthPixels, video.HeightPixels).Should().BeTrue("never larger than a screen");
        video.DurationMilliseconds.Should().BePositive("a video has a length");
        bytes.Length.Should().Be(input.Length, "stripping never moves the media");
        AdvertisingVideos.Check(bytes).Should().Be(video, "what was taken is taken again as it is");
    }

    [Theory]
    [InlineData("h264-aac.mp4")]
    [InlineData("h264-moov-last.mp4")]
    [InlineData("vp9-opus.webm")]
    [InlineData("vp8-vorbis.webm")]
    [InlineData("av1.webm")]
    public void Check_Should_GiveAVideoWithinTheLimitsOrNothing_ForAnyMutationOfAValidVideo(string name) =>
        Fuzz.From(Fixture(name), json: false).Sample(Holds, iter: Fuzz.Iterations, print: b => Convert.ToBase64String(b));

    [Theory]
    [InlineData("h264-aac.mp4", 2400)]
    [InlineData("vp9-opus.webm", 600)]
    public void Check_Should_GiveAVideoWithinTheLimitsOrNothing_ForAnyMutationOfItsHeader(string name, int header)
    {
        var seed = Fixture(name);
        Fuzz.Mutated(seed[..header]).Select(head => (byte[])[.. head, .. seed[header..]])
            .Sample(Holds, iter: Fuzz.Iterations, print: b => Convert.ToBase64String(b));
    }
}
