using System.Text;
using Ariva.Core.Domain.Entities;
using Ariva.Infra.Storage;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace Ariva.UnitTests.Storage;

/// <summary>
/// ARV-018: plan uploads are typed by their bytes, SVG is rebuilt without active content, and storage keys cannot
/// reach outside the storage root.
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
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x01, 0x2C, 0x02, 0x58, 0x03, 0x01, 0x22, 0x00];

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
        static byte[] Png(int width, int height, string chunk = "IHDR")
        {
            var bytes = new byte[33];
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13 }.CopyTo(bytes, 0);
            Encoding.ASCII.GetBytes(chunk).CopyTo(bytes, 12);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), width);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), height);
            return bytes;
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
}
