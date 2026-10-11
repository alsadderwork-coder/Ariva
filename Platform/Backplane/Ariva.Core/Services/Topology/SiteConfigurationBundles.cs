using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;

namespace Ariva.Core.Services.Topology;

/// <summary>A bundle whose manifest checked out: each part's decoded bytes, by part name.</summary>
public sealed record OpenedSiteBundle(int FormatVersion, string SourceSite, string ExportedAtUtc, string BundleSha256, IReadOnlyDictionary<string, byte[]> Parts)
{
    /// <summary>How many floor plan file parts the bundle holds (numbered 1 to this).</summary>
    public int FloorPlanFiles => Parts.Keys.Count(name => SiteConfigurationBundles.FloorPlanFileNumber(name) is not null);
}

/// <summary>
/// The site configuration bundle format, version 1 (ARV-122a, docs/product/decisions.md). One JSON document:
/// <c>{ formatVersion, sourceSite, exportedAtUtc, manifest: { part: sha256 }, parts: { part: base64 } }</c>. The parts are
/// <see cref="Topology"/>, <see cref="DeskCodeMappings"/>, <see cref="ZoneProfile"/> and <see cref="FloorPlans"/>, always
/// present (an empty list when the site has none), and one <c>floorPlanFile.N</c> per floor plan. A later story adds its
/// parts by name without a new format version; until then an unknown part is refused.
/// <para>
/// Trust (CWE-501): the manifest proves that the parts were not changed or cut after export; it does not prove who made the
/// bundle, since anyone can recompute SHA-256. The trust comes from the import being a step-up critical action of an
/// administrator, and every part is checked again as the existing endpoints check what an administrator types.
/// </para>
/// </summary>
public static partial class SiteConfigurationBundles
{
    public const int FormatVersion = 1;

    /// <summary>The first line of the bundle hash's canonical text.</summary>
    public const string HashPrefix = "ariva-site-configuration-v1";

    public const string Topology = "topology";
    public const string DeskCodeMappings = "deskCodeMappings";
    public const string ZoneProfile = "zoneProfile";
    public const string FloorPlans = "floorPlans";
    public const string FloorPlanFilePrefix = "floorPlanFile.";

    /// <summary>The configuration parts every bundle of this version holds.</summary>
    public static readonly IReadOnlyList<string> ConfigurationParts = [Topology, DeskCodeMappings, ZoneProfile, FloorPlans];

    private const string ExportedAtFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    #region Names and hashes

    public static string FloorPlanFile(int number) => string.Create(CultureInfo.InvariantCulture, $"{FloorPlanFilePrefix}{number}");

    /// <summary>The number of a floor plan file part (1 to <see cref="SiteConfigurationLimits.MaxFloorPlans"/>), or null.</summary>
    public static int? FloorPlanFileNumber(string name)
    {
        if (name is null || !FileName().IsMatch(name))
            return null;
        var number = int.Parse(name.AsSpan(FloorPlanFilePrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture);
        return number <= SiteConfigurationLimits.MaxFloorPlans ? number : null;
    }

    /// <summary>A part this version imports: one of the configuration parts or a floor plan file.</summary>
    public static bool IsKnownPart(string name) =>
        ConfigurationParts.Any(part => string.Equals(part, name, StringComparison.Ordinal)) || FloorPlanFileNumber(name) is not null;

    /// <summary>An export time as the bundle writes it: UTC to the second, <c>2026-10-11T08:00:00Z</c>.</summary>
    public static string ExportedAt(DateTime utc) => utc.ToUniversalTime().ToString(ExportedAtFormat, CultureInfo.InvariantCulture);

    public static bool IsExportedAt(string text) =>
        text is { Length: 20 } && DateTime.TryParseExact(text, ExportedAtFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _);

    /// <summary>A SHA-256 as the manifest writes it: 64 lower-case hex digits.</summary>
    public static bool IsSha256(string text) => text is { Length: 64 } && text.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>
    /// The canonical text the bundle hash covers: the prefix line, the format version, the source site and the export time,
    /// then one line per manifest entry ordered by part name (ordinal). Part names, site codes and times are checked before
    /// this is built, and none can hold a bar or a line break.
    /// </summary>
    public static string CanonicalText(int formatVersion, string sourceSite, string exportedAtUtc, IReadOnlyDictionary<string, string> manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var text = new StringBuilder(HashPrefix).Append('\n')
            .Append(CultureInfo.InvariantCulture, $"formatVersion|{formatVersion}\n")
            .Append(CultureInfo.InvariantCulture, $"sourceSite|{sourceSite}\n")
            .Append(CultureInfo.InvariantCulture, $"exportedAtUtc|{exportedAtUtc}\n");
        foreach (var (name, hash) in manifest.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            text.Append(CultureInfo.InvariantCulture, $"part|{name}|{hash}\n");
        return text.ToString();
    }

    /// <summary>The bundle hash: the SHA-256 of <see cref="CanonicalText"/> in UTF-8. Export and import answer the same value.</summary>
    public static string BundleSha256(int formatVersion, string sourceSite, string exportedAtUtc, IReadOnlyDictionary<string, string> manifest) =>
        Sha256(Encoding.UTF8.GetBytes(CanonicalText(formatVersion, sourceSite, exportedAtUtc, manifest)));

    #endregion

    #region Build and open

    /// <summary>The bundle of an export: the manifest of each part's SHA-256 and each part in base64.</summary>
    public static SiteConfigurationBundleViewModel Build(string sourceSite, DateTime exportedAtUtc, IReadOnlyDictionary<string, byte[]> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Keys.Any(name => !IsKnownPart(name)))
            throw new ArgumentException("Only known parts go into a bundle.", nameof(parts));
        var manifest = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var encoded = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, bytes) in parts)
        {
            manifest[name] = Sha256(bytes);
            encoded[name] = Convert.ToBase64String(bytes);
        }

        return new SiteConfigurationBundleViewModel(FormatVersion, sourceSite, ExportedAt(exportedAtUtc), manifest, encoded);
    }

    /// <summary>
    /// Checks a bundle before anything in it is used, in this order: the envelope and the format version; every manifest
    /// entry a known part with a well-formed hash; the manifest and the parts naming exactly the same parts, every
    /// configuration part present and the file parts numbered 1 to N without a gap; each part's base64 length within its
    /// bound before it is decoded (413 beyond), then valid base64 within the bound; each part's SHA-256 equal to its
    /// manifest entry. Every refusal is a fixed text that repeats nothing from the bundle.
    /// </summary>
    public static Result<OpenedSiteBundle> Open(ImportSiteConfigurationRequest request)
    {
        if (request is null || request.Manifest is null || request.Parts is null || request.FormatVersion is null)
            return Result.Error<OpenedSiteBundle>(SiteConfigurationErrors.NotABundle);
        if (request.FormatVersion != FormatVersion)
            return Result.Error<OpenedSiteBundle>(SiteConfigurationErrors.UnsupportedFormat);
        if (!Site.IsValidCode(request.SourceSite) || !IsExportedAt(request.ExportedAtUtc))
            return Result.Error<OpenedSiteBundle>(SiteConfigurationErrors.NotABundle);
        if (request.Manifest.Count > SiteConfigurationLimits.MaxParts || request.Parts.Count > SiteConfigurationLimits.MaxParts)
            return Result.Error<OpenedSiteBundle>(SiteConfigurationErrors.PartsDoNotMatchManifest);
        if (request.Manifest.Keys.Any(name => !IsKnownPart(name)) || request.Parts.Keys.Any(name => !IsKnownPart(name)))
            return Result.Error<OpenedSiteBundle>(SiteConfigurationErrors.PartsDoNotMatchManifest);
        if (request.Manifest.Values.Any(hash => !IsSha256(hash)))
            return Result.Error<OpenedSiteBundle>(SiteConfigurationErrors.ManifestNotValid);
        if (!SameNames(request.Manifest.Keys, request.Parts.Keys) || ConfigurationParts.Any(name => !request.Manifest.ContainsKey(name)))
            return Result.Error<OpenedSiteBundle>(SiteConfigurationErrors.PartsDoNotMatchManifest);
        var files = request.Manifest.Keys.Select(FloorPlanFileNumber).Where(number => number is not null).Select(number => number.GetValueOrDefault()).Order().ToList();
        if (files.Where((number, index) => number != index + 1).Any())
            return Result.Error<OpenedSiteBundle>(SiteConfigurationErrors.PartsDoNotMatchManifest);

        var decoded = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (name, text) in request.Parts.OrderBy(part => part.Key, StringComparer.Ordinal))
        {
            var limit = FloorPlanFileNumber(name) is null ? SiteConfigurationLimits.MaxJsonPartBytes : SiteConfigurationLimits.MaxFileBytes;
            var (bytes, tooLarge) = Decode(text, limit);
            if (tooLarge)
                return Result.Error<OpenedSiteBundle>(SiteConfigurationErrors.PartTooLarge);
            if (bytes is null)
                return Result.Error<OpenedSiteBundle>(SiteConfigurationErrors.PartNotReadable);
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sha256(bytes)), Encoding.ASCII.GetBytes(request.Manifest[name])))
                return Result.Error<OpenedSiteBundle>(SiteConfigurationErrors.HashMismatch);
            decoded[name] = bytes;
        }

        var bundleSha256 = BundleSha256(FormatVersion, request.SourceSite, request.ExportedAtUtc, request.Manifest);
        return new Result<OpenedSiteBundle>(new OpenedSiteBundle(FormatVersion, request.SourceSite, request.ExportedAtUtc, bundleSha256, decoded));
    }

    /// <summary>
    /// The bytes of a base64 text, refused as too large when the text is longer than the base64 of <paramref name="limit"/>
    /// bytes (checked before decoding) or decodes to more; null bytes when it is not base64.
    /// </summary>
    public static (byte[] Bytes, bool TooLarge) Decode(string text, int limit)
    {
        if (text is null)
            return (null, false);
        if (text.Length > SiteConfigurationLimits.Base64Length(limit))
            return (null, true);
        var buffer = new byte[(text.Length + 3) / 4 * 3];
        if (!Convert.TryFromBase64String(text, buffer, out var written))
            return (null, false);
        return written > limit ? (null, true) : (buffer.AsSpan(0, written).ToArray(), false);
    }

    private static bool SameNames(IEnumerable<string> first, IEnumerable<string> second) =>
        new HashSet<string>(first, StringComparer.Ordinal).SetEquals(second);

    [GeneratedRegex("^floorPlanFile\\.[1-9][0-9]{0,2}\\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex FileName();

    #endregion
}
