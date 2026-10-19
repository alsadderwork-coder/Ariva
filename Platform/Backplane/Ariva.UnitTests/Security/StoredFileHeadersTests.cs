using System.Text;
using System.Text.RegularExpressions;
using Ariva.Api.Common.Security;
using Ariva.Core.Domain.Entities;
using Ariva.Infra.Storage;
using FluentAssertions;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-092 (ASVS 5.0 V5.4.1 and V4.1.1): a stored upload goes out inline under a name the server makes from the endpoint's
/// prefix, the record's id and the stored type, with the same extension as the file's storage key, so the name never
/// comes from a request and never needs escaping. SVG, the one text type, names its charset.
/// </summary>
public sealed class StoredFileHeadersTests
{
    #region Fields

    private static readonly Guid Id = Guid.Parse("0199a000-0000-7000-8000-0000000000aa");

    #endregion

    #region Data

    /// <summary>
    /// Every type an upload is stored as, with the extension of its storage key: floor plans as the inspector types them
    /// (ARV-018) and advertisement images and videos (ARV-142a, ARV-142b).
    /// </summary>
    public static TheoryData<string, string> StoredTypes()
    {
        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, .. "IHDR"u8, 0, 0, 0, 16, 0, 0, 0, 16];
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x10, 0x00, 0x10, 0x03, 0, 0, 0, 0];
        var svg = Encoding.UTF8.GetBytes("""<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"/>""");
        foreach (var plan in new[] { png, jpeg, svg }.Select(FloorPlanFiles.Inspect))
            types[plan.ContentType] = plan.Extension;
        foreach (var (type, extension) in AdvertisingMediaFile.ImageTypes.Concat(AdvertisingMediaFile.VideoTypes))
            types[type] = extension;

        var data = new TheoryData<string, string>();
        foreach (var (type, extension) in types)
            data.Add(type, extension);
        return data;
    }

    #endregion

    #region Tests

    [Theory]
    [MemberData(nameof(StoredTypes))]
    public void InlineDisposition_Should_NameTheFileFromThePrefixIdAndStoredExtension_When_TheTypeIsOneAnUploadIsStoredAs(string type, string extension)
    {
        var disposition = StoredFileHeaders.InlineDisposition("floor-plan", Id, type);

        disposition.Should().Be($"inline; filename=\"floor-plan-0199a000-0000-7000-8000-0000000000aa.{extension}\"");
        Regex.IsMatch(disposition, "^inline; filename=\"[a-z0-9-]+\\.[a-z0-9]+\"\\z", RegexOptions.None, TimeSpan.FromSeconds(1))
            .Should().BeTrue("the name is lower-case ASCII letters, digits, hyphens and one dot: nothing to escape, no filename* needed");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("text/html")]
    [InlineData("application/octet-stream")]
    [InlineData("IMAGE/PNG")]
    [InlineData("image/svg+xml; charset=utf-8")]
    public void InlineDisposition_Should_GiveTheNameNoExtension_When_TheTypeIsNotOneAnUploadIsStoredAs(string type)
    {
        StoredFileHeaders.InlineDisposition("advertisement", Id, type)
            .Should().Be("inline; filename=\"advertisement-0199a000-0000-7000-8000-0000000000aa\"", "an unknown type never picks an extension of its own");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Floor-Plan")]
    [InlineData("plan.svg")]
    [InlineData("plan\"; filename=page.html")]
    [InlineData("plan\r\nSet-Cookie: a=b")]
    [InlineData("pläne")]
    public void InlineDisposition_Should_Refuse_When_ThePrefixIsNotLowerCaseAsciiLettersAndHyphens(string prefix)
    {
        FluentActions.Invoking(() => StoredFileHeaders.InlineDisposition(prefix, Id, "image/png")).Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("image/svg+xml", "image/svg+xml; charset=utf-8")]
    [InlineData("image/png", "image/png")]
    [InlineData("image/jpeg", "image/jpeg")]
    [InlineData("image/webp", "image/webp")]
    [InlineData("video/mp4", "video/mp4")]
    [InlineData("video/webm", "video/webm")]
    public void ContentType_Should_NameUtf8ForSvgOnly_When_AStoredTypeIsServed(string stored, string sent)
    {
        StoredFileHeaders.ContentType(stored).Should().Be(sent, "SVG is XML text; the other stored types are binary");
    }

    #endregion
}
