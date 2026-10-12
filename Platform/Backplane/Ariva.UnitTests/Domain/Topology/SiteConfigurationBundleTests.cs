using System.Text;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Topology;
using FluentAssertions;

namespace Ariva.UnitTests.Domain.Topology;

/// <summary>
/// ARV-122a: the bundle format. Each part is hashed by its decoded bytes and the bundle hash covers the canonical text of
/// the envelope and the manifest; an import refuses, with fixed texts that repeat nothing, a tampered part or hash, a
/// missing, extra or unknown part, a manifest entry without its part, another format version, and any part beyond its
/// bound, the bound checked on the base64 length before anything is decoded (CWE-120, CWE-501).
/// </summary>
public sealed class SiteConfigurationBundleTests
{
    private static readonly DateTime ExportedAt = new(2026, 10, 11, 8, 0, 0, DateTimeKind.Utc);

    private const string EmptyTopology = """{"terminals":[],"levels":[],"checkpoints":[],"desks":[]}""";

    private static Dictionary<string, byte[]> EmptyParts() => new(StringComparer.Ordinal)
    {
        [SiteConfigurationBundles.Topology] = Encoding.UTF8.GetBytes(EmptyTopology),
        [SiteConfigurationBundles.DeskCodeMappings] = "[]"u8.ToArray(),
        [SiteConfigurationBundles.ZoneProfile] = "[]"u8.ToArray(),
        [SiteConfigurationBundles.FloorPlans] = "[]"u8.ToArray(),
        [SiteConfigurationBundles.Devices] = "[]"u8.ToArray(),
        [SiteConfigurationBundles.AlertRules] = "[]"u8.ToArray(),
        [SiteConfigurationBundles.Displays] = "[]"u8.ToArray()
    };

    private static ImportSiteConfigurationRequest Request(SiteConfigurationBundleViewModel bundle) =>
        new(bundle.FormatVersion, bundle.SourceSite, bundle.ExportedAtUtc, new Dictionary<string, string>(bundle.Manifest), new Dictionary<string, string>(bundle.Parts));

    private static ImportSiteConfigurationRequest Valid(Dictionary<string, byte[]> parts = null) =>
        Request(SiteConfigurationBundles.Build("DMO", ExportedAt, parts ?? EmptyParts()));

    #region Build and hash

    [Fact]
    public void Build_Should_HashEachPartsBytesAndTheCanonicalText_When_Built()
    {
        var bundle = SiteConfigurationBundles.Build("DMO", ExportedAt, EmptyParts());

        bundle.FormatVersion.Should().Be(1);
        bundle.ExportedAtUtc.Should().Be("2026-10-11T08:00:00Z");
        bundle.Manifest[SiteConfigurationBundles.Topology].Should().Be("a260042e53e7353cd821da18263c63d0fdb1182c0b58d4b0007a3087fb2438ca");
        bundle.Manifest[SiteConfigurationBundles.FloorPlans].Should().Be("4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945");
        bundle.Parts[SiteConfigurationBundles.Topology].Should().Be(Convert.ToBase64String(Encoding.UTF8.GetBytes(EmptyTopology)));
        // The canonical form is a contract: an operator compares this value out of band. Recomputed independently in Python.
        SiteConfigurationBundles.BundleSha256(bundle.FormatVersion, bundle.SourceSite, bundle.ExportedAtUtc, bundle.Manifest)
            .Should().Be("be523a50b6a59a947bddc1b5bc33afab5258fcda8e77dce81ab2054b45d504cc");
    }

    [Fact]
    public void CanonicalText_Should_OrderTheEntriesByName_When_TheManifestIsInAnyOrder()
    {
        var text = SiteConfigurationBundles.CanonicalText(1, "DMO", "2026-10-11T08:00:00Z",
            new Dictionary<string, string> { ["zoneProfile"] = "b", ["deskCodeMappings"] = "a" });

        text.Should().Be("ariva-site-configuration-v1\nformatVersion|1\nsourceSite|DMO\nexportedAtUtc|2026-10-11T08:00:00Z\npart|deskCodeMappings|a\npart|zoneProfile|b\n");
    }

    [Fact]
    public void Build_Should_RefuseAnUnknownPart_When_OneIsGiven()
    {
        var parts = EmptyParts();
        parts["users"] = "[]"u8.ToArray();

        var build = () => SiteConfigurationBundles.Build("DMO", ExportedAt, parts);

        build.Should().Throw<ArgumentException>();
    }

    #endregion

    #region Open

    [Fact]
    public void Open_Should_DecodeEveryPartAndAnswerTheExportsHash_When_NothingChanged()
    {
        var parts = EmptyParts();
        parts[SiteConfigurationBundles.FloorPlanFile(1)] = [1, 2, 3];
        var bundle = SiteConfigurationBundles.Build("DMO", ExportedAt, parts);

        var opened = SiteConfigurationBundles.Open(Request(bundle));

        opened.HasErrors.Should().BeFalse(string.Join(", ", opened.ErrorMessages ?? []));
        opened.Data.BundleSha256.Should().Be(SiteConfigurationBundles.BundleSha256(1, "DMO", bundle.ExportedAtUtc, bundle.Manifest));
        opened.Data.Parts[SiteConfigurationBundles.FloorPlanFile(1)].Should().Equal(1, 2, 3);
        opened.Data.FloorPlanFiles.Should().Be(1);
        opened.Data.SourceSite.Should().Be("DMO");
    }

    public static TheoryData<string, string> Tampered => new()
    {
        { "a part's bytes changed", SiteConfigurationErrors.HashMismatch },
        { "a manifest hash changed", SiteConfigurationErrors.HashMismatch },
        { "a part removed", SiteConfigurationErrors.PartsDoNotMatchManifest },
        { "a manifest entry without its part", SiteConfigurationErrors.PartsDoNotMatchManifest },
        { "a part without its entry", SiteConfigurationErrors.PartsDoNotMatchManifest },
        { "a configuration part missing from both", SiteConfigurationErrors.PartsDoNotMatchManifest },
        { "an unknown part in both", SiteConfigurationErrors.PartsDoNotMatchManifest },
        { "a part this version never exports in both", SiteConfigurationErrors.PartsDoNotMatchManifest },
        { "an ARV-122b part missing from both", SiteConfigurationErrors.PartsDoNotMatchManifest },
        { "an extra file part after a gap", SiteConfigurationErrors.PartsDoNotMatchManifest },
        { "a file part beyond the floor plan bound", SiteConfigurationErrors.PartsDoNotMatchManifest },
        { "an upper-case hash", SiteConfigurationErrors.ManifestNotValid },
        { "a short hash", SiteConfigurationErrors.ManifestNotValid },
        { "a part that is not base64", SiteConfigurationErrors.PartNotReadable },
        { "format version 2", SiteConfigurationErrors.UnsupportedFormat },
        { "format version 0", SiteConfigurationErrors.UnsupportedFormat },
        { "no format version", SiteConfigurationErrors.NotABundle },
        { "no manifest", SiteConfigurationErrors.NotABundle },
        { "no parts", SiteConfigurationErrors.NotABundle },
        { "a source site of another shape", SiteConfigurationErrors.NotABundle },
        { "an export time of another shape", SiteConfigurationErrors.NotABundle }
    };

    [Theory]
    [MemberData(nameof(Tampered))]
    public void Open_Should_RefuseWithAFixedText_When_TheBundleIsTampered(string change, string expected)
    {
        var request = Valid();
        var topologyHash = request.Manifest[SiteConfigurationBundles.Topology];
        var hostile = "<script>alert(1)</script>";
        request = change switch
        {
            "a part's bytes changed" => With(request, parts: p => p[SiteConfigurationBundles.Topology] = Convert.ToBase64String("""{"terminals":[],"levels":[],"checkpoints":[],"desks":[ ]}"""u8)),
            "a manifest hash changed" => With(request, manifest: m => m[SiteConfigurationBundles.Topology] = new string('0', 64)),
            "a part removed" => With(request, parts: p => p.Remove(SiteConfigurationBundles.Topology)),
            "a manifest entry without its part" => With(request, manifest: m => m[SiteConfigurationBundles.FloorPlanFile(1)] = topologyHash),
            "a part without its entry" => With(request, parts: p => p[SiteConfigurationBundles.FloorPlanFile(1)] = "AQID"),
            "a configuration part missing from both" => With(request, manifest: m => m.Remove(SiteConfigurationBundles.ZoneProfile), parts: p => p.Remove(SiteConfigurationBundles.ZoneProfile)),
            "an unknown part in both" => With(request, manifest: m => m[hostile] = topologyHash, parts: p => p[hostile] = "W10="),
            "a part this version never exports in both" => With(request, manifest: m => m["users"] = SiteConfigurationBundles.Sha256("[]"u8), parts: p => p["users"] = "W10="),
            "an ARV-122b part missing from both" => With(request, manifest: m => m.Remove(SiteConfigurationBundles.Devices), parts: p => p.Remove(SiteConfigurationBundles.Devices)),
            "an extra file part after a gap" => With(request, manifest: m => m[SiteConfigurationBundles.FloorPlanFile(2)] = SiteConfigurationBundles.Sha256([1]),
                parts: p => p[SiteConfigurationBundles.FloorPlanFile(2)] = "AQ=="),
            "a file part beyond the floor plan bound" => With(request, manifest: m => m["floorPlanFile.201"] = SiteConfigurationBundles.Sha256([1]), parts: p => p["floorPlanFile.201"] = "AQ=="),
            "an upper-case hash" => With(request, manifest: m => m[SiteConfigurationBundles.Topology] = topologyHash.ToUpperInvariant()),
            "a short hash" => With(request, manifest: m => m[SiteConfigurationBundles.Topology] = topologyHash[..63]),
            "a part that is not base64" => With(request, parts: p => p[SiteConfigurationBundles.Topology] = hostile),
            "format version 2" => request with { FormatVersion = 2 },
            "format version 0" => request with { FormatVersion = 0 },
            "no format version" => request with { FormatVersion = null },
            "no manifest" => request with { Manifest = null },
            "no parts" => request with { Parts = null },
            "a source site of another shape" => request with { SourceSite = "dmo" },
            "an export time of another shape" => request with { ExportedAtUtc = "2026-10-11T08:00:00+04:00" },
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };

        var opened = SiteConfigurationBundles.Open(request);

        opened.ErrorMessages.Should().Equal([expected], change);
        opened.ErrorMessages.Should().NotContain(m => m.Contains("script", StringComparison.OrdinalIgnoreCase), "a refusal repeats nothing from the bundle");
    }

    [Fact]
    public void Open_Should_RefuseMorePartsThanTheBound_When_OneMoreIsListed()
    {
        var parts = EmptyParts();
        for (var number = 1; number <= SiteConfigurationLimits.MaxFloorPlans; number++)
            parts[SiteConfigurationBundles.FloorPlanFile(number)] = [(byte)number];
        var atBound = Valid(parts);
        atBound.Manifest.Should().HaveCount(SiteConfigurationLimits.MaxParts);

        SiteConfigurationBundles.Open(atBound).HasErrors.Should().BeFalse("7 configuration parts and 200 files are the bound");

        var beyond = With(atBound, manifest: m => m["extra"] = new string('0', 64), parts: p => p["extra"] = "AQ==");
        SiteConfigurationBundles.Open(beyond).ErrorMessages.Should().Equal(SiteConfigurationErrors.PartsDoNotMatchManifest);
    }

    [Fact]
    public void Open_Should_TakeAConfigurationPartAtItsBoundAndRefuseOneByteMore_When_Decoded()
    {
        var atBound = EmptyParts();
        atBound[SiteConfigurationBundles.DeskCodeMappings] = new byte[SiteConfigurationLimits.MaxJsonPartBytes];
        var beyond = EmptyParts();
        beyond[SiteConfigurationBundles.DeskCodeMappings] = new byte[SiteConfigurationLimits.MaxJsonPartBytes + 1];

        SiteConfigurationBundles.Open(Valid(atBound)).HasErrors.Should().BeFalse("the bound is inclusive");
        SiteConfigurationBundles.Open(Valid(beyond)).ErrorMessages.Should().Equal(SiteConfigurationErrors.PartTooLarge);
    }

    #endregion

    #region Decode

    [Fact]
    public void Decode_Should_RefuseByLengthBeforeDecoding_When_TheTextIsLongerThanTheBoundsBase64()
    {
        var limit = 6;
        var atBound = Convert.ToBase64String(new byte[limit]);
        atBound.Length.Should().Be(SiteConfigurationLimits.Base64Length(limit));

        var decoded = SiteConfigurationBundles.Decode(atBound, limit);
        decoded.TooLarge.Should().BeFalse("the bound is inclusive");
        decoded.Bytes.Should().Equal(new byte[limit]);
        // One more base64 block is refused by its length alone, whatever it holds (here it is not even base64).
        SiteConfigurationBundles.Decode(atBound + "!!!!", limit).TooLarge.Should().BeTrue();
        // Within the length but decoding to more bytes than the bound (padding counts in the length check).
        SiteConfigurationBundles.Decode(Convert.ToBase64String(new byte[5]), 4).Should().Be((null, true));
        SiteConfigurationBundles.Decode("not base64!", 100).Should().Be((null, false));
        SiteConfigurationBundles.Decode(null, 100).Should().Be((null, false));
    }

    [Theory]
    [InlineData(1, 4)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(20 * 1024 * 1024, 27_962_028)]
    public void Base64Length_Should_BeThePaddedLength_When_Asked(int bytes, int length) =>
        SiteConfigurationLimits.Base64Length(bytes).Should().Be(length);

    #endregion

    #region Names

    [Theory]
    [InlineData("floorPlanFile.1", 1)]
    [InlineData("floorPlanFile.200", 200)]
    [InlineData("floorPlanFile.201", null)]
    [InlineData("floorPlanFile.0", null)]
    [InlineData("floorPlanFile.01", null)]
    [InlineData("floorPlanFile.1 ", null)]
    [InlineData("FloorPlanFile.1", null)]
    [InlineData("floorPlanFile.-1", null)]
    [InlineData("topology", null)]
    public void FloorPlanFileNumber_Should_ReadOnlyTheNumberedFileParts_When_Named(string name, int? number) =>
        SiteConfigurationBundles.FloorPlanFileNumber(name).Should().Be(number);

    [Theory]
    [InlineData("topology", true)]
    [InlineData("deskCodeMappings", true)]
    [InlineData("zoneProfile", true)]
    [InlineData("floorPlans", true)]
    [InlineData("floorPlanFile.7", true)]
    [InlineData("devices", true)]
    [InlineData("alertRules", true)]
    [InlineData("displays", true)]
    [InlineData("Topology", false)]
    [InlineData("users", false)]
    [InlineData("deviceCalibrations", false)]
    public void IsKnownPart_Should_KnowOnlyThisVersionsParts_When_Asked(string name, bool known) =>
        SiteConfigurationBundles.IsKnownPart(name).Should().Be(known);

    [Theory]
    [InlineData("2026-10-11T08:00:00Z", true)]
    [InlineData("2026-10-11T08:00:00.000Z", false)]
    [InlineData("2026-10-11T08:00:00+00:00", false)]
    [InlineData("2026-13-11T08:00:00Z", false)]
    [InlineData("2026-10-11 08:00:00Z", false)]
    [InlineData(null, false)]
    public void IsExportedAt_Should_TakeUtcToTheSecondOnly_When_Read(string text, bool valid) =>
        SiteConfigurationBundles.IsExportedAt(text).Should().Be(valid);

    #endregion

    private static ImportSiteConfigurationRequest With(ImportSiteConfigurationRequest request, Action<Dictionary<string, string>> manifest = null,
        Action<Dictionary<string, string>> parts = null)
    {
        var newManifest = new Dictionary<string, string>(request.Manifest);
        var newParts = new Dictionary<string, string>(request.Parts);
        manifest?.Invoke(newManifest);
        parts?.Invoke(newParts);
        return request with { Manifest = newManifest, Parts = newParts };
    }
}
