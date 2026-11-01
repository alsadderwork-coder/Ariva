using Ariva.Infra.Storage;
using Ariva.UnitTests.Setup;
using CsCheck;
using FluentAssertions;

namespace Ariva.UnitTests.Properties;

/// <summary>
/// ARV-201, CWE-120 and CWE-434: the floor plan inspector given random bytes and a PNG or JPEG with metadata cut short or
/// mutated answers with a raster within the limits or with null, never an exception. What it keeps is never larger than the
/// upload and passes the inspector again unchanged, with the same size: a plan exported and imported elsewhere is stored as
/// it was. A failure prints CsCheck's shrunk input and seed.
/// </summary>
public sealed class FloorPlanRasterPropertyTests
{
    private static void Holds(byte[] input)
    {
        var file = FloorPlanFiles.Inspect(input);
        if (file is null || file.ContentType == "image/svg+xml")
            return;
        file.ContentType.Should().BeOneOf("image/png", "image/jpeg");
        FloorPlanLimits.Fits(file.WidthPixels ?? 0, file.HeightPixels ?? 0).Should().BeTrue("never larger than the limits");
        file.Bytes.Length.Should().BeInRange(1, input.Length, "nothing is added");

        var again = FloorPlanFiles.Inspect(file.Bytes);
        again.Should().NotBeNull("a kept plan passes again");
        again.Bytes.Should().Equal(file.Bytes, "and is kept as it is");
        (again.ContentType, again.WidthPixels, again.HeightPixels).Should().Be((file.ContentType, file.WidthPixels, file.HeightPixels));
    }

    [Theory]
    [InlineData("png")]
    [InlineData("jpeg")]
    public void Inspect_Should_KeepARasterThatPassesAgainUnchangedOrNothing_ForAnyMutationOfOneWithMetadata(string type) =>
        Fuzz.From(type == "png" ? PlanImages.PngWithMetadata(24, 12) : PlanImages.JpegWithMetadata(24, 12), json: false)
            .Sample(Holds, iter: Fuzz.Iterations, print: b => Convert.ToBase64String(b));
}
