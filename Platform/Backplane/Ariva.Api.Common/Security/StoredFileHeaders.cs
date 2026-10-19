using System.Collections.Frozen;

namespace Ariva.Api.Common.Security;

/// <summary>
/// The headers of a stored upload that a host serves back (ARV-092; ASVS 5.0 V5.4.1 and V4.1.1; CWE-79). The uploader's
/// file name is never sent: Content-Disposition names the file from the endpoint's fixed prefix, the record's id and the
/// extension of the type its bytes were read as at upload, so the name holds only lower-case ASCII letters, digits,
/// hyphens and one dot. It needs no escaping and no <c>filename*</c> parameter (RFC 6266 section 4.3 calls for that only
/// for characters outside ASCII). The file is shown in place (<c>inline</c>): the screens and the display player fetch it
/// with credentials and show it as an image or play it as a video; a person who saves it gets the server's name. SVG, the
/// one text type, goes out with <c>charset=utf-8</c>, the encoding its sanitiser writes.
/// </summary>
public static class StoredFileHeaders
{
    #region Fields

    private const string Svg = "image/svg+xml";

    /// <summary>
    /// The extension of every type an upload is stored as: floor plans (ARV-018) and advertisement images and videos
    /// (ARV-142a, ARV-142b), the same extensions as their storage keys.
    /// </summary>
    private static readonly FrozenDictionary<string, string> Extensions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["image/png"] = "png",
        ["image/jpeg"] = "jpg",
        ["image/webp"] = "webp",
        [Svg] = "svg",
        ["video/mp4"] = "mp4",
        ["video/webm"] = "webm"
    }.ToFrozenDictionary(StringComparer.Ordinal);

    #endregion

    #region Methods

    /// <summary>
    /// <c>inline; filename="{prefix}-{id}.{extension}"</c>, with the extension of the stored type (none for a type that is
    /// not listed). The prefix is the endpoint's constant, lower-case ASCII letters and hyphens.
    /// </summary>
    /// <param name="prefix">What the file is, for example <c>floor-plan</c>.</param>
    /// <param name="id">The id of the record the URL names.</param>
    /// <param name="storedType">The content type recorded for the file at upload.</param>
    /// <returns>The Content-Disposition value.</returns>
    public static string InlineDisposition(string prefix, Guid id, string storedType)
    {
        if (string.IsNullOrEmpty(prefix) || !prefix.All(c => char.IsAsciiLetterLower(c) || c == '-'))
            throw new ArgumentException("The prefix is lower-case ASCII letters and hyphens.", nameof(prefix));

        var name = storedType is not null && Extensions.TryGetValue(storedType, out var extension)
            ? $"{prefix}-{id:D}.{extension}"
            : $"{prefix}-{id:D}";
        return $"inline; filename=\"{name}\"";
    }

    /// <summary>
    /// The Content-Type to send for a stored type: SVG (XML, so text) with <c>charset=utf-8</c>; the binary types as stored.
    /// </summary>
    /// <param name="storedType">The content type recorded for the file at upload.</param>
    /// <returns>The Content-Type value.</returns>
    public static string ContentType(string storedType) =>
        string.Equals(storedType, Svg, StringComparison.Ordinal) ? $"{Svg}; charset=utf-8" : storedType;

    #endregion
}
