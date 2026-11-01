using System.Text;
using Ariva.Core.Domain.Entities;
using Ariva.Infra.Storage;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace Ariva.UnitTests.Storage;

/// <summary>
/// ARV-018: plan uploads are typed by their bytes, SVG is rebuilt without active content, and storage keys cannot
/// reach outside the storage root. ARV-201: PNG and JPEG plans are kept without their metadata (text, EXIF, XMP, colour
/// profiles, comments, thumbnails, animation and private chunks, and anything after the image's end), with the pieces
/// that draw the pixels copied as they are, so the pixels and their size do not change; a file whose chunks or segments
/// do not read through to its end is refused.
/// </summary>
public sealed class FloorPlanFilesTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAADCAYAAAC56t6BAAAAEUlEQVR4nGP4z8DwHxkzEAcARg8P8RODp8QAAAAASUVORK5CYII=");

    private static string Svg(string body) =>
        $"<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" viewBox=\"0 0 100 50\">{body}</svg>";

    private static string Clean(string svg) => Encoding.UTF8.GetString(FloorPlanFiles.SanitizeSvg(Encoding.UTF8.GetBytes(svg)));

    [Fact]
    public void Inspect_Should_ReadPngSize_When_BytesArePng()
    {
        var file = FloorPlanFiles.Inspect(Png);

        file.ContentType.Should().Be("image/png");
        file.Extension.Should().Be("png");
        (file.WidthPixels, file.HeightPixels).Should().Be((2, 3));
    }

    [Fact]
    public void Inspect_Should_ReadJpegSize_When_BytesAreJpeg()
    {
        var jpeg = PlanImages.Jpeg(600, 300);

        var file = FloorPlanFiles.Inspect(jpeg);

        file.ContentType.Should().Be("image/jpeg");
        (file.WidthPixels, file.HeightPixels).Should().Be((600, 300));
    }

    [Theory]
    [InlineData("GIF89a.......")]
    [InlineData("<html><body>hi</body></html>")]
    [InlineData("<svg>no namespace</svg>")]
    [InlineData("%PDF-1.7")]
    [InlineData("")]
    public void Inspect_Should_Refuse_When_BytesAreNotAnAllowedImage(string text)
    {
        FloorPlanFiles.Inspect(Encoding.UTF8.GetBytes(text)).Should().BeNull();
    }

    [Fact]
    public void Inspect_Should_IgnoreTheName_When_ANamedPngIsHtml()
    {
        // The type comes from the bytes only: an HTML file stays refused whatever it was called.
        FloorPlanFiles.Inspect(Encoding.UTF8.GetBytes("<!DOCTYPE html><svg xmlns=\"http://www.w3.org/2000/svg\"/>")).Should().BeNull();
    }

    [Theory]
    [InlineData("<script>alert(1)</script><rect width=\"10\" height=\"10\"/>", "script")]
    [InlineData("<foreignObject><body xmlns=\"http://www.w3.org/1999/xhtml\"><img src=\"x\" onerror=\"alert(1)\"/></body></foreignObject>", "foreignObject")]
    [InlineData("<rect onload=\"alert(1)\" onclick=\"x()\" width=\"1\" height=\"1\"/>", "onload")]
    [InlineData("<a xlink:href=\"javascript:alert(1)\"><text>x</text></a>", "javascript")]
    [InlineData("<use href=\"https://evil.example/x.svg#a\"/>", "evil.example")]
    [InlineData("<image href=\"data:image/png;base64,AAAA\"/>", "image")]
    [InlineData("<style>@import url(https://evil.example/a.css);</style>", "evil.example")]
    [InlineData("<rect style=\"fill:url(https://evil.example/p)\" width=\"1\" height=\"1\"/>", "evil.example")]
    [InlineData("<rect fill=\"url(https://evil.example/p)\" width=\"1\" height=\"1\"/>", "evil.example")]
    [InlineData("<animate attributeName=\"href\" to=\"javascript:alert(1)\"/>", "animate")]
    [InlineData("<iframe src=\"https://evil.example\"/>", "iframe")]
    public void SanitizeSvg_Should_RemoveActiveContent_When_Present(string body, string removed)
    {
        Clean(Svg(body)).Should().NotContain(removed);
    }

    [Fact]
    public void SanitizeSvg_Should_KeepDrawingAndLocalReferences_When_Safe()
    {
        var clean = Clean(Svg("<defs><linearGradient id=\"g\"/></defs><rect x=\"1\" y=\"2\" width=\"30\" height=\"20\" fill=\"url(#g)\" style=\"stroke:#000\"/><use href=\"#g\"/><text x=\"5\" y=\"5\">Hall A</text>"));

        clean.Should().Contain("<rect").And.Contain("fill=\"url(#g)\"").And.Contain("Hall A").And.Contain("href=\"#g\"").And.Contain("stroke:#000");
    }

    [Fact]
    public void SanitizeSvg_Should_NeutraliseTheE2EFixture_When_EverythingIsHostileAtOnce()
    {
        // The same drawing as tests/api/floor-plans.spec.ts, so the E2E expectation is proven here first.
        const string hostile = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="200" height="100" onload="alert(1)">
            <script>alert(2)</script>
            <rect width="50" height="50" fill="#123456" onclick="alert(3)"/>
            <foreignObject width="10" height="10"><div xmlns="http://www.w3.org/1999/xhtml">x</div></foreignObject>
            <image href="https://attacker.example/p.png" width="1" height="1"/>
            <text x="5" y="80">Gate</text>
            <a xlink:href="javascript:alert(4)"><text x="5" y="90">click</text></a>
            </svg>
            """;

        var clean = Clean(hostile);

        foreach (var removed in new[] { "<script", "onload", "onclick", "foreignObject", "attacker.example", "javascript:", "alert(" })
            clean.Should().NotContain(removed);
        clean.Should().Contain("#123456").And.Contain("Gate");
    }

    [Theory]
    [InlineData("<rect width=\"1\" height=\"1\" fill=\"url(#a) url(https://evil.example/f.svg#x)\"/>", "evil.example")]
    [InlineData("<rect width=\"1\" height=\"1\" fill=\"\\75rl(https://evil.example/p)\"/>", "evil.example")]
    [InlineData("<rect width=\"1\" height=\"1\" style=\"fill:\\75rl(https://evil.example/p)\"/>", "evil.example")]
    [InlineData("<rect width=\"1\" height=\"1\" cursor=\"url(#a), url(https://evil.example/c.png), auto\"/>", "evil.example")]
    [InlineData("<rect width=\"1\" height=\"1\" filter=\"url(https://evil.example/f.svg#x)\"/>", "evil.example")]
    [InlineData("<g xml:base=\"https://evil.example/\"><use href=\"#a\"/></g>", "evil.example")]
    [InlineData("<h:meta xmlns:h=\"http://www.w3.org/1999/xhtml\" http-equiv=\"refresh\" content=\"0;url=https://evil.example\"/>", "evil.example")]
    [InlineData("<h:form xmlns:h=\"http://www.w3.org/1999/xhtml\" action=\"java&#9;script:alert(1)\"/>", "script")]
    [InlineData("<math xmlns=\"http://www.w3.org/1998/Math/MathML\"><mi>evil.example</mi></math>", "evil.example")]
    [InlineData("<use href=\"java&#10;script:alert(1)\"/>", "script")]
    [InlineData("<rect width=\"1\" height=\"1\" style=\"behavior:url(#a);-moz-binding:url(#b)\"/>", "binding")]
    [InlineData("<rect width=\"1\" height=\"1\" style=\"fill:image-set(\'https://evil.example/a.png\' 1x)\"/>", "evil.example")]
    [InlineData("<metadata><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">evil.example</rdf:RDF></metadata>", "evil.example")]
    public void SanitizeSvg_Should_RemoveExternalReferencesAndForeignMarkup_When_Hidden(string body, string removed)
    {
        Clean(Svg(body)).Should().NotContain(removed);
    }

    [Fact]
    public void SanitizeSvg_Should_EscapeCdata_When_ItHidesMarkup()
    {
        var clean = Clean(Svg("<text><![CDATA[</text><script>alert(1)</script>]]></text>"));

        clean.Should().NotContain("<script").And.Contain("&lt;script&gt;");
    }

    [Fact]
    public void SanitizeSvg_Should_Refuse_When_TooManyElementsOrTooDeep()
    {
        var flood = Svg(string.Concat(Enumerable.Repeat("<g/>", FloorPlanLimits.MaxSvgElements + 1)));
        var hiddenFlood = Svg("<script>" + string.Concat(Enumerable.Repeat("<g/>", FloorPlanLimits.MaxSvgElements + 1)) + "</script>");
        var deep = Svg(string.Concat(Enumerable.Repeat("<g>", FloorPlanLimits.MaxSvgDepth + 1)) + string.Concat(Enumerable.Repeat("</g>", FloorPlanLimits.MaxSvgDepth + 1)));

        FloorPlanFiles.SanitizeSvg(Encoding.UTF8.GetBytes(flood)).Should().BeNull("an element flood costs memory to parse and to draw");
        FloorPlanFiles.SanitizeSvg(Encoding.UTF8.GetBytes(hiddenFlood)).Should().BeNull("skipped elements count too");
        FloorPlanFiles.SanitizeSvg(Encoding.UTF8.GetBytes(deep)).Should().BeNull();
        FloorPlanFiles.SanitizeSvg(Encoding.UTF8.GetBytes(Svg(string.Concat(Enumerable.Repeat("<g/>", 1000))))).Should().NotBeNull();
    }

    [Fact]
    public void Inspect_Should_ReadSvgSize_When_ViewBoxOrPixelSizeIsGiven()
    {
        var fromViewBox = FloorPlanFiles.Inspect(Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 3000 1200\" width=\"30cm\"/>"));
        var fromSize = FloorPlanFiles.Inspect(Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"640px\" height=\"480\"/>"));
        var unknown = FloorPlanFiles.Inspect(Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"30cm\" height=\"20cm\"/>"));

        (fromViewBox.WidthPixels, fromViewBox.HeightPixels).Should().Be((3000, 1200));
        (fromSize.WidthPixels, fromSize.HeightPixels).Should().Be((640, 480));
        (unknown.WidthPixels, unknown.HeightPixels).Should().Be((null, null));
    }

    [Fact]
    public void Inspect_Should_RefuseRasterBombsAndBrokenHeaders_When_Uploaded()
    {
        // A header of the given size (and first chunk type), then an empty IDAT and IEND: the size is read from the header,
        // and nothing decodes the pixels.
        static byte[] Png(int width, int height, string chunk = "IHDR")
        {
            var bytes = new byte[33];
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13 }.CopyTo(bytes, 0);
            Encoding.ASCII.GetBytes(chunk).CopyTo(bytes, 12);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), width);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), height);
            return [.. bytes, .. PlanImages.Chunk("IDAT", []), .. PlanImages.Chunk("IEND", [])];
        }

        FloorPlanFiles.Inspect(Png(60_000, 60_000)).Should().BeNull("about 14 GB once decoded");
        FloorPlanFiles.Inspect(Png(16_384, 16_384)).Should().BeNull("268 megapixels");
        FloorPlanFiles.Inspect(Png(16_385, 10)).Should().BeNull();
        FloorPlanFiles.Inspect(Png(100, 100, "IDAT")).Should().BeNull("IHDR must come first");
        FloorPlanFiles.Inspect(Png(9_933, 14_043)).Should().BeNull("an A0 sheet at 300 dpi is 139.5 megapixels; export at 250 dpi");
        FloorPlanFiles.Inspect(Png(8_268, 11_693)).Should().NotBeNull("an A0 sheet at 250 dpi");
        FloorPlanFiles.Inspect([0xFF, 0xD8, 0xFF, 0xD9, 0, 0, 0, 0, 0, 0, 0, 0]).Should().BeNull("a JPEG needs a start-of-frame with a size");
    }

    [Fact]
    public void SanitizeSvg_Should_Refuse_When_TheXmlIsMalformed()
    {
        FloorPlanFiles.SanitizeSvg(Encoding.UTF8.GetBytes(Svg("<img src=x onerror=alert(1)>"))).Should().BeNull();
    }

    [Fact]
    public void SanitizeSvg_Should_RefuseDtdsAndEntities_When_Present()
    {
        var xxe = "<?xml version=\"1.0\"?><!DOCTYPE svg [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><svg xmlns=\"http://www.w3.org/2000/svg\"><text>&x;</text></svg>";

        FloorPlanFiles.SanitizeSvg(Encoding.UTF8.GetBytes(xxe)).Should().BeNull("DTDs are prohibited (CWE-611)");
    }

    [Fact]
    public void SanitizeSvg_Should_DropProcessingInstructionsAndComments_When_Present()
    {
        var clean = Clean("<?xml-stylesheet href=\"https://evil.example/s.xsl\"?><!-- secret --><svg xmlns=\"http://www.w3.org/2000/svg\"><rect width=\"1\" height=\"1\"/></svg>");

        clean.Should().NotContain("evil.example").And.NotContain("secret").And.StartWith("<svg");
    }

    [Theory]
    [InlineData("0199a0000000700080000000000000a1.png", true)]
    [InlineData("0199a0000000700080000000000000a1.svg", true)]
    [InlineData("../0199a0000000700080000000000000a1.png", false)]
    [InlineData("0199a0000000700080000000000000a1.png/../../etc/passwd", false)]
    [InlineData("0199A0000000700080000000000000A1.png", false)]
    [InlineData("0199a0000000700080000000000000a1.exe", false)]
    [InlineData("0199a0000000700080000000000000a1.mp4", true)]
    [InlineData("0199a0000000700080000000000000a1.webm", true)]
    [InlineData("0199a0000000700080000000000000a1.mov", false)]
    [InlineData("0199a0000000700080000000000000a1.mp4.html", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData(null, false)]
    public void IsValidKey_Should_AcceptOnlyGeneratedKeys_When_Checked(string key, bool valid)
    {
        LocalDiskFileStorage.IsValidKey(key).Should().Be(valid);
    }

    [Fact]
    public async Task LocalDisk_Should_RoundTripAndRefuseForeignKeys_When_Used()
    {
        var root = Path.Combine(Path.GetTempPath(), "ariva-files-test-" + Guid.NewGuid().ToString("N"));
        var storage = new LocalDiskFileStorage(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["Storage:LocalRoot"] = root }).Build());
        const string key = "0199a0000000700080000000000000a1.png";
        try
        {
            await storage.SaveAsync(key, new MemoryStream(Png), TestContext.Current.CancellationToken);
            await using (var read = await storage.OpenReadAsync(key, TestContext.Current.CancellationToken))
            {
                using var copy = new MemoryStream();
                await read.CopyToAsync(copy, TestContext.Current.CancellationToken);
                copy.ToArray().Should().Equal(Png);
            }

            var traversal = () => storage.OpenReadAsync("../../etc/passwd", TestContext.Current.CancellationToken);
            await traversal.Should().ThrowAsync<ArgumentException>();
            await storage.DeleteAsync(key, TestContext.Current.CancellationToken);
            (await storage.OpenReadAsync(key, TestContext.Current.CancellationToken)).Should().BeNull();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(-0.1, 0, 0)]
    [InlineData(10.5, 0, 0)]
    [InlineData(double.NaN, 0, 0)]
    [InlineData(0.05, double.PositiveInfinity, 0)]
    [InlineData(0.05, 0, 2_000_000)]
    public void Calibrate_Should_RefuseScalesAndOriginsOutOfRange_When_Set(double metresPerPixel, double x, double y)
    {
        var level = new Airport("DMO", null, "Demo", "Asia/Amman").AddTerminal("T1", "T1", "DMO-T1").AddLevel("L0", "L", 0, 50, 50);

        var create = () => new FloorPlan(level, "0199a0000000700080000000000000a1.png", "image/png", 10, "x", "plan.png", 2, 3, metresPerPixel, x, y);

        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    #region Rasters (ARV-201)

    /// <summary>The chunk types of a PNG, in order.</summary>
    private static string[] ChunkTypes(byte[] png) => [.. PlanImages.Chunks(png).Select(c => c.Type)];

    [Fact]
    public void Inspect_Should_KeepAPngWithoutItsMetadata_When_ItCarriesTextExifXmpAProfileAndATrailingPage()
    {
        var plain = PlanImages.Png(64, 48);
        var upload = PlanImages.PngWithMetadata(64, 48);
        PlanImages.Found(upload, PlanImages.Markers).Should().Equal(PlanImages.Markers, "the upload carries every piece");

        var file = FloorPlanFiles.Inspect(upload);

        (file.ContentType, file.Extension, file.WidthPixels, file.HeightPixels).Should().Be(("image/png", "png", 64, 48));
        PlanImages.Found(file.Bytes, PlanImages.Markers).Should().BeEmpty();
        ChunkTypes(file.Bytes).Should().Equal(ChunkTypes(plain));
        file.Bytes.Should().Equal(plain, "the encoder's own chunks are all kept, each as it was");
        PlanImages.Decode(file.Bytes).Should().BeEquivalentTo(PlanImages.Decode(plain), "the same pixels");
    }

    [Fact]
    public void Inspect_Should_KeepAJpegWithoutItsMetadata_When_ItCarriesExifXmpAProfileCommentsAndATrailingPage()
    {
        var plain = PlanImages.Jpeg(64, 48);
        var upload = PlanImages.JpegWithMetadata(64, 48);
        string[] jpegMarkers = [.. PlanImages.Markers.Except(["acTL", "prVt"])];
        PlanImages.Found(upload, jpegMarkers).Should().Equal(jpegMarkers, "the upload carries every piece");

        var file = FloorPlanFiles.Inspect(upload);

        (file.ContentType, file.Extension, file.WidthPixels, file.HeightPixels).Should().Be(("image/jpeg", "jpg", 64, 48));
        PlanImages.Found(file.Bytes, PlanImages.Markers).Should().BeEmpty();
        PlanImages.HeaderMarkers(file.Bytes).Should().Equal(PlanImages.HeaderMarkers(plain));
        file.Bytes.Should().Equal(plain, "the encoder's own segments and scan are all kept, each as it was");
        PlanImages.Decode(file.Bytes).Should().BeEquivalentTo(PlanImages.Decode(plain), "the same pixels");
    }

    [Theory]
    [InlineData("tEXt")]
    [InlineData("zTXt")]
    [InlineData("iTXt")]
    [InlineData("eXIf")]
    [InlineData("tIME")]
    [InlineData("iCCP")]
    [InlineData("pHYs")]
    [InlineData("bKGD")]
    [InlineData("sPLT")]
    [InlineData("hIST")]
    [InlineData("acTL")]
    [InlineData("fcTL")]
    [InlineData("fdAT")]
    [InlineData("cICP")]
    [InlineData("caBX")]
    [InlineData("prVt")]
    public void Inspect_Should_DropAnAncillaryPngChunk_When_ItIsMetadata(string type)
    {
        var plain = PlanImages.Png(8, 8);
        var upload = PlanImages.InsertPng(plain, PlanImages.Chunk(type, PlanImages.Latin1("CadExport")), PlanImages.Chunk(type, [1, 2, 3]));

        FloorPlanFiles.Inspect(upload).Bytes.Should().Equal(plain, "before the pixels and after them");
    }

    [Theory]
    [InlineData("PLTE", 3)]
    [InlineData("PLTE", 768)]
    [InlineData("tRNS", 6)]
    [InlineData("tRNS", 256)]
    [InlineData("gAMA", 4)]
    [InlineData("cHRM", 32)]
    [InlineData("sRGB", 1)]
    [InlineData("sBIT", 1)]
    [InlineData("sBIT", 4)]
    public void Inspect_Should_KeepAPngChunk_When_ItSaysHowTheSamplesReadAtItsOwnSize(string type, int length)
    {
        var upload = PlanImages.InsertPng(PlanImages.Png(8, 8), PlanImages.Chunk(type, [.. Enumerable.Repeat((byte)1, length)]));

        FloorPlanFiles.Inspect(upload).Bytes.Should().Equal(upload);
    }

    [Theory]
    [InlineData("tRNS", 257)]
    [InlineData("gAMA", 5)]
    [InlineData("cHRM", 4)]
    [InlineData("sRGB", 2)]
    [InlineData("sBIT", 0)]
    [InlineData("sBIT", 5)]
    public void Inspect_Should_DropAKeptAncillaryPngChunk_When_ItIsNotItsOwnSize(string type, int length)
    {
        // A decoder ignores such a chunk; its bytes beyond the numbers it holds could carry anything.
        var plain = PlanImages.Png(8, 8);

        FloorPlanFiles.Inspect(PlanImages.InsertPng(plain, PlanImages.Chunk(type, [.. Enumerable.Repeat((byte)0x41, length)]))).Bytes.Should().Equal(plain);
    }

    [Theory]
    [InlineData("no IEND")]
    [InlineData("cut inside a chunk")]
    [InlineData("no IDAT")]
    [InlineData("a critical chunk Ariva does not know")]
    [InlineData("a chunk type that is not four letters")]
    [InlineData("a length past the end")]
    [InlineData("a length over two gigabytes")]
    [InlineData("IHDR twice")]
    [InlineData("IHDR of another length")]
    [InlineData("an empty palette")]
    [InlineData("a palette not of three-byte entries")]
    [InlineData("a palette of more than 256 entries")]
    [InlineData("IEND with data")]
    public void Inspect_Should_RefuseAPng_When_ItsChunksDoNotReadThroughToIend(string fault)
    {
        var png = PlanImages.Png(8, 8);
        var header = png.AsSpan(8, 25).ToArray();
        byte[] signature = [.. png.AsSpan(0, 8)];
        byte[] broken = fault switch
        {
            "no IEND" => png[..^12],
            "cut inside a chunk" => png[..^20],
            "no IDAT" => [.. signature, .. PlanImages.Chunks(png).Where(c => c.Type != "IDAT").SelectMany(c => PlanImages.Chunk(c.Type, c.Data))],
            // Apple's CgBI makes a file no other decoder reads.
            "a critical chunk Ariva does not know" => PlanImages.InsertPng(png, PlanImages.Chunk("CgBI", [0x50, 0, 0x20, 2])),
            "a chunk type that is not four letters" => PlanImages.InsertPng(png, PlanImages.Chunk("t3Xt", [1])),
            "a length past the end" => [.. png[..^12], 0, 0, 0x03, 0xE8, .. "IDAT"u8, 1, 2, 3, .. png[^12..]],
            "a length over two gigabytes" => [.. png[..^12], 0xFF, 0xFF, 0xFF, 0xF0, .. "tEXt"u8, 1, 2, 3, .. png[^12..]],
            "IHDR twice" => PlanImages.InsertPng(png, header),
            "IHDR of another length" => [.. signature, .. PlanImages.Chunk("IHDR", [.. header.AsSpan(8, 13), 0]), .. png[33..]],
            "an empty palette" => PlanImages.InsertPng(png, PlanImages.Chunk("PLTE", [])),
            "a palette not of three-byte entries" => PlanImages.InsertPng(png, PlanImages.Chunk("PLTE", [1, 2, 3, 4])),
            "a palette of more than 256 entries" => PlanImages.InsertPng(png, PlanImages.Chunk("PLTE", new byte[771])),
            "IEND with data" => [.. png[..^12], .. PlanImages.Chunk("IEND", PlanImages.Latin1("<html>a page</html>"))],
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };

        FloorPlanFiles.Inspect(broken).Should().BeNull();
    }

    [Fact]
    public void Inspect_Should_KeepJfifWithoutItsThumbnailAndAdobesColourTransform_When_AJpegHasThem()
    {
        var plain = PlanImages.Jpeg(16, 8);
        var encoderApp0 = plain[3] == 0xE0 ? 2 + ((plain[4] << 8) | plain[5]) : 0;
        byte[] bare = [.. plain[..2], .. plain[(2 + encoderApp0)..]];
        // Version 1.02, dots per inch, 72 by 72, and a 2 by 1 thumbnail of six bytes.
        var jfif = PlanImages.Segment(0xE0, [.. "JFIF\0"u8, 1, 2, 1, 0, 72, 0, 72, 2, 1, 0xFF, 0, 0, 0, 0xFF, 0]);
        var adobe = PlanImages.Segment(0xEE, [.. "Adobe"u8, 0, 100, 0, 0, 0, 0, 1]);

        var file = FloorPlanFiles.Inspect(PlanImages.InsertJpeg(bare, [.. jfif, .. adobe]));

        file.Bytes.AsSpan(2, 18).ToArray().Should().Equal(PlanImages.Segment(0xE0, [.. "JFIF\0"u8, 1, 2, 1, 0, 72, 0, 72, 0, 0]),
            "the identifier, version, units and densities as they were, without the thumbnail");
        file.Bytes.AsSpan(20, adobe.Length).ToArray().Should().Equal(adobe, "the transform flag says how the colours are coded");
        file.Bytes[(20 + adobe.Length)..].Should().Equal(bare[2..]);
        PlanImages.Decode(file.Bytes).Should().BeEquivalentTo(PlanImages.Decode(plain), "the same pixels");
    }

    [Fact]
    public void Inspect_Should_DropJfif_When_ItComesAfterAKeptSegmentOrASecondTime()
    {
        var plain = PlanImages.Jpeg(16, 8);
        var encoderApp0 = plain[3] == 0xE0 ? 2 + ((plain[4] << 8) | plain[5]) : 0;
        byte[] bare = [.. plain[..2], .. plain[(2 + encoderApp0)..]];
        var jfif = PlanImages.Segment(0xE0, [.. "JFIF\0"u8, 1, 2, 0, 0, 1, 0, 1, 0, 0]);
        var adobe = PlanImages.Segment(0xEE, [.. "Adobe"u8, 0, 100, 0, 0, 0, 0, 1]);

        FloorPlanFiles.Inspect(PlanImages.InsertJpeg(bare, [.. adobe, .. jfif])).Bytes.Should().Equal(PlanImages.InsertJpeg(bare, adobe),
            "a JFIF header belongs first; one behind a kept segment is not the file's");
        FloorPlanFiles.Inspect(PlanImages.InsertJpeg(bare, [.. jfif, .. jfif])).Bytes.Should().Equal(PlanImages.InsertJpeg(bare, jfif),
            "one JFIF header only");
    }

    [Theory]
    [InlineData(0xE0, "JFXX")]
    [InlineData(0xE1, "Exif")]
    [InlineData(0xE1, "http://ns.adobe.com/xap/1.0/")]
    [InlineData(0xE2, "ICC_PROFILE")]
    [InlineData(0xE2, "MPF")]
    [InlineData(0xED, "Photoshop 3.0")]
    [InlineData(0xEE, "Adobe")]
    [InlineData(0xEF, "anything")]
    [InlineData(0xFE, "a comment")]
    public void Inspect_Should_DropAJpegSegment_When_ItIsMetadata(int marker, string identifier)
    {
        // The identifier, its NUL and a payload: an Adobe segment of another size than its fixed one is metadata too.
        var plain = PlanImages.Jpeg(8, 8);
        var segment = PlanImages.Segment((byte)marker, PlanImages.Latin1(identifier + "\0 CadExport"));

        FloorPlanFiles.Inspect(PlanImages.InsertJpeg(plain, segment, segment)).Bytes.Should().Equal(plain, "before the frame and after the scan");
    }

    [Fact]
    public void Inspect_Should_KeepEveryScanWithItsStuffedBytesAndRestarts_When_AJpegHasSeveral()
    {
        // A progressive frame of 6 by 4 pixels, one component, with a restart interval, two scans and a number of lines. Their
        // entropy-coded data holds a stuffed byte (FF 00), restarts (FF D0, and FF D1 after a fill byte) and a fill byte
        // before the second scan. Nothing decodes it: the structure is what is kept.
        var tables = PlanImages.Segment(0xDB, [0, .. Enumerable.Repeat((byte)1, 64)]);
        var huffman = PlanImages.Segment(0xC4, [0, 1, .. new byte[15], 0]);
        var restarts = PlanImages.Segment(0xDD, [0, 2]);
        var frame = PlanImages.Segment(0xC2, [8, 0, 4, 0, 6, 1, 1, 0x11, 0]);
        byte[] first = [.. PlanImages.Segment(0xDA, [1, 1, 0, 0, 0, 0]), 0x12, 0xFF, 0x00, 0x34, 0xFF, 0xD0, 0x56, 0xFF, 0xFF, 0xD1, 0x78];
        byte[] second = [.. PlanImages.Segment(0xDA, [1, 1, 0, 1, 5, 0]), 0x9A, 0xFF, 0x00];
        var lines = PlanImages.Segment(0xDC, [0, 4]);
        var comment = PlanImages.Segment(0xFE, PlanImages.Latin1("between the scans"));
        byte[] upload = [0xFF, 0xD8, .. tables, .. huffman, .. restarts, .. frame, .. first, .. comment, .. huffman, 0xFF, 0xFF, .. second, .. lines, 0xFF, 0xD9];

        var file = FloorPlanFiles.Inspect(upload);

        file.Bytes.Should().Equal([0xFF, 0xD8, .. tables, .. huffman, .. restarts, .. frame, .. first, .. huffman, .. second, .. lines, 0xFF, 0xD9]);
        (file.WidthPixels, file.HeightPixels).Should().Be((6, 4));
    }

    [Theory]
    [InlineData("no end-of-image")]
    [InlineData("an end-of-image before any scan")]
    [InlineData("a scan without a frame")]
    [InlineData("a second frame")]
    [InlineData("a frame header too short for its size")]
    [InlineData("a frame without a height")]
    [InlineData("a stray restart marker")]
    [InlineData("a second start-of-image")]
    [InlineData("a hierarchical marker")]
    [InlineData("a JPEG-LS frame")]
    [InlineData("the reserved JPG marker")]
    [InlineData("a segment past the end")]
    [InlineData("a length below two")]
    [InlineData("bytes between segments")]
    [InlineData("entropy-coded data to the end")]
    [InlineData("a differential frame")]
    [InlineData("a frame header of another length than its components")]
    [InlineData("a scan header of another length than its components")]
    [InlineData("a quantisation table cut short")]
    [InlineData("a quantisation table of another precision")]
    [InlineData("Huffman codes beyond the table")]
    [InlineData("an empty table segment")]
    [InlineData("a restart interval of another length")]
    public void Inspect_Should_RefuseAJpeg_When_ItsSegmentsDoNotReadThroughToItsEnd(string fault)
    {
        var jpeg = PlanImages.Jpeg(8, 8);
        var (frameAt, frameLength) = SegmentOf(jpeg, 0xC0);
        var (scanAt, _) = SegmentOf(jpeg, 0xDA);
        var frame = jpeg[frameAt..(frameAt + frameLength)];
        byte[] broken = fault switch
        {
            "no end-of-image" => jpeg[..^2],
            "an end-of-image before any scan" => [.. jpeg[..scanAt], 0xFF, 0xD9],
            "a scan without a frame" => [.. jpeg[..frameAt], .. jpeg[(frameAt + frameLength)..]],
            "a second frame" => [.. jpeg[..scanAt], .. frame, .. jpeg[scanAt..]],
            "a frame header too short for its size" => PlanImages.InsertJpeg(jpeg, PlanImages.Segment(0xC0, [8, 0])),
            "a frame without a height" => [.. jpeg[..(frameAt + 5)], 0, 0, .. jpeg[(frameAt + 7)..]],
            "a stray restart marker" => PlanImages.InsertJpeg(jpeg, [0xFF, 0xD0]),
            "a second start-of-image" => PlanImages.InsertJpeg(jpeg, [0xFF, 0xD8]),
            "a hierarchical marker" => PlanImages.InsertJpeg(jpeg, PlanImages.Segment(0xDE, [0, 0, 0, 0])),
            "a JPEG-LS frame" => PlanImages.InsertJpeg(jpeg, PlanImages.Segment(0xF7, [8, 0, 8, 0, 8, 1, 1, 0x11, 0])),
            "the reserved JPG marker" => PlanImages.InsertJpeg(jpeg, PlanImages.Segment(0xC8, [0, 0])),
            "a segment past the end" => PlanImages.InsertJpeg(jpeg, [0xFF, 0xE1, 0xFF, 0xFF]),
            "a length below two" => PlanImages.InsertJpeg(jpeg, [0xFF, 0xE1, 0x00, 0x01]),
            "bytes between segments" => PlanImages.InsertJpeg(jpeg, [0x00]),
            "entropy-coded data to the end" => [.. jpeg[..^2], 0x12, 0xFF, 0x00],
            // SOF5 is only meaningful in a hierarchical file, which is refused.
            "a differential frame" => [.. jpeg[..frameAt], 0xFF, 0xC5, .. jpeg[(frameAt + 2)..]],
            // The test JPEG's frame names three components; its last three bytes are cut and the length says so.
            "a frame header of another length than its components" => [.. jpeg[..frameAt], .. PlanImages.Segment(0xC0, frame[4..^3]), .. jpeg[(frameAt + frameLength)..]],
            "a scan header of another length than its components" => [.. jpeg[..scanAt], .. PlanImages.Segment(0xDA, [1, 1, 0, 0, 0x3F, 0, 0]), .. jpeg[scanAt..]],
            "a quantisation table cut short" => PlanImages.InsertJpeg(jpeg, PlanImages.Segment(0xDB, [0, .. new byte[63]])),
            "a quantisation table of another precision" => PlanImages.InsertJpeg(jpeg, PlanImages.Segment(0xDB, [0x20, .. new byte[64]])),
            "Huffman codes beyond the table" => PlanImages.InsertJpeg(jpeg, PlanImages.Segment(0xC4, [0, 2, .. new byte[15], 7])),
            "an empty table segment" => PlanImages.InsertJpeg(jpeg, PlanImages.Segment(0xC4, [])),
            "a restart interval of another length" => PlanImages.InsertJpeg(jpeg, PlanImages.Segment(0xDD, [0, 2, 0])),
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };

        FloorPlanFiles.Inspect(broken).Should().BeNull();
    }

    /// <summary>Where the first segment with this marker starts (its FF) and how long it is with its marker.</summary>
    private static (int At, int Length) SegmentOf(byte[] jpeg, byte marker)
    {
        var at = 2;
        while (jpeg[at + 1] != marker)
            at += 2 + ((jpeg[at + 2] << 8) | jpeg[at + 3]);
        return (at, 2 + ((jpeg[at + 2] << 8) | jpeg[at + 3]));
    }

    #endregion
}
