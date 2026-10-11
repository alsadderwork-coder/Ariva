using System.Text.Json;
using System.Text.Json.Serialization;
using Ariva.Core.Domain.InputModels;

namespace Ariva.Core.Services.Topology;

/// <summary>The decoded configuration parts of a bundle, before any rule has run.</summary>
public sealed record SiteConfigurationContent(
    TopologyPart Topology,
    IReadOnlyList<DeskCodeMappingPartRow> DeskCodeMappings,
    IReadOnlyList<ZoneProfilePartRow> ZoneProfiles,
    IReadOnlyList<FloorPlanPartRow> FloorPlans,
    IReadOnlyDictionary<int, byte[]> FloorPlanFiles)
{
    /// <summary>The zone profile the bundle carries, or null (the part holds at most one; the rules check it).</summary>
    public ZoneProfilePartRow ZoneProfile => ZoneProfiles is { Count: 1 } ? ZoneProfiles[0] : null;
}

/// <summary>
/// Reads and writes the configuration parts (ARV-122a) as UTF-8 JSON: camel case member names matched exactly (so a
/// member that differs only in case is an unknown one), at most 32 levels deep (the global bound), no unknown member,
/// no duplicate member, numbers only as JSON numbers, no comment, no trailing comma. A part that is not that JSON of its
/// kind is one fixed refusal (CWE-120, CWE-501).
/// </summary>
public static class SiteConfigurationParts
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        MaxDepth = 32,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        NumberHandling = JsonNumberHandling.Strict,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        WriteIndented = false
    };

    public static byte[] Write<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Json);

    /// <summary>The configuration parts of an opened bundle as typed rows, or <see cref="SiteConfigurationErrors.PartNotReadable"/>.</summary>
    public static Result<SiteConfigurationContent> Read(OpenedSiteBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        try
        {
            var topology = JsonSerializer.Deserialize<TopologyPart>(bundle.Parts[SiteConfigurationBundles.Topology], Json);
            var mappings = JsonSerializer.Deserialize<List<DeskCodeMappingPartRow>>(bundle.Parts[SiteConfigurationBundles.DeskCodeMappings], Json);
            var profiles = JsonSerializer.Deserialize<List<ZoneProfilePartRow>>(bundle.Parts[SiteConfigurationBundles.ZoneProfile], Json);
            var plans = JsonSerializer.Deserialize<List<FloorPlanPartRow>>(bundle.Parts[SiteConfigurationBundles.FloorPlans], Json);
            if (topology is null || mappings is null || profiles is null || plans is null)
                return Result.Error<SiteConfigurationContent>(SiteConfigurationErrors.PartNotReadable);
            var files = bundle.Parts
                .Select(part => (Number: SiteConfigurationBundles.FloorPlanFileNumber(part.Key), Bytes: part.Value))
                .Where(part => part.Number is not null)
                .ToDictionary(part => part.Number.GetValueOrDefault(), part => part.Bytes);
            return new Result<SiteConfigurationContent>(new SiteConfigurationContent(topology, mappings, profiles, plans, files));
        }
        catch (JsonException)
        {
            return Result.Error<SiteConfigurationContent>(SiteConfigurationErrors.PartNotReadable);
        }
    }
}
