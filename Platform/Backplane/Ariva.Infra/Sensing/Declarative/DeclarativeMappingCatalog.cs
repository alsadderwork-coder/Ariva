using System.Collections.Frozen;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Sensing;

namespace Ariva.Infra.Sensing.Declarative;

/// <summary>
/// The declarative mappings shipped with Ariva (ARV-024): the JSON files under Ariva.Infra/Sensing/Mappings, embedded in
/// the assembly, each named like its file. They are parsed and checked once; a broken mapping stops the host at
/// start-up instead of refusing a device's pushes later. Nothing is read from the file system, configuration or the
/// database, so a mapping cannot be changed at run time (CWE-94, CWE-501).
/// </summary>
public sealed class DeclarativeMappingCatalog : IDeviceMappingCatalog
{
    private const string ResourcePrefix = "Ariva.Infra.Sensing.Mappings.";
    private static readonly Lazy<DeclarativeMappingCatalog> Shipped = new(() => FromAssembly(typeof(DeclarativeMappingCatalog).Assembly), LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly FrozenDictionary<string, DeclarativeMapping> _mappings;

    public DeclarativeMappingCatalog(IEnumerable<DeclarativeMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        var byName = new Dictionary<string, DeclarativeMapping>(StringComparer.Ordinal);
        foreach (var mapping in mappings)
        {
            if (!byName.TryAdd(mapping.Name, mapping))
                throw new MappingException($"Two mappings are named {mapping.Name}.");
        }

        _mappings = byName.ToFrozenDictionary(StringComparer.Ordinal);
        Mappings = [.. _mappings.Values.OrderBy(m => m.Name, StringComparer.Ordinal).Select(View)];
    }

    /// <summary>The mappings embedded in Ariva.Infra; the first use parses them, and throws if one is broken.</summary>
    public static DeclarativeMappingCatalog Embedded => Shipped.Value;

    public IReadOnlyList<DeviceMappingViewModel> Mappings { get; }

    public bool Contains(string name) => name is not null && _mappings.ContainsKey(name);

    /// <summary>The mapping of that name, or null.</summary>
    public DeclarativeMapping Find(string name) => name is not null && _mappings.TryGetValue(name, out var mapping) ? mapping : null;

    internal static DeclarativeMappingCatalog FromAssembly(System.Reflection.Assembly assembly)
    {
        var mappings = new List<DeclarativeMapping>();
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            var name = resource[ResourcePrefix.Length..^".json".Length];
            using var stream = assembly.GetManifestResourceStream(resource) ?? throw new MappingException($"Mapping {name} cannot be read.");
            if (stream.Length > DeclarativeMapping.MaxDocumentBytes)
                throw new MappingException($"Mapping {name} is larger than {DeclarativeMapping.MaxDocumentBytes / 1024} KB.");
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            mappings.Add(DeclarativeMapping.Parse(buffer.ToArray(), name));
        }

        return new DeclarativeMappingCatalog(mappings);
    }

    private static DeviceMappingViewModel View(DeclarativeMapping m) => new(m.Name, m.Title, m.Source,
    [
        .. new[] { m.Tracks is null ? null : "tracks", m.Crossings is null ? null : "crossings", m.Occupancy is null ? null : "occupancy", m.Intervals is null ? null : "intervals" }
            .Where(k => k is not null)
    ]);
}
