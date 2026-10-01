using System.Reflection;

namespace Ariva.Infra.Timescale;

/// <summary>
/// The scripts shipped in Ariva.Infra as embedded resources (<c>Timescale/Scripts/NNNN_*.sql</c>), in order.
/// Number 0000 is the folder's README and is never applied. Duplicate numbers fail.
/// </summary>
public static class SqlScriptCatalog
{
    private const string ResourcePrefix = "Ariva.Infra.Timescale.Scripts.";

    public static IReadOnlyList<SqlScript> Embedded() => Load(typeof(SqlScriptCatalog).Assembly);

    internal static IReadOnlyList<SqlScript> Load(Assembly assembly)
    {
        var scripts = new List<SqlScript>();
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            if (resource[ResourcePrefix.Length..].StartsWith("0000_", StringComparison.Ordinal))
                continue; // the folder README
            using var stream = assembly.GetManifestResourceStream(resource);
            using var reader = new StreamReader(stream);
            scripts.Add(SqlScript.From(resource[ResourcePrefix.Length..], reader.ReadToEnd()));
        }

        return Order(scripts);
    }

    public static IReadOnlyList<SqlScript> Order(IEnumerable<SqlScript> scripts)
    {
        var ordered = scripts.Where(s => s.Number > 0).OrderBy(s => s.Number).ToList();
        var duplicate = ordered.GroupBy(s => s.Number).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Duplicate script number {duplicate.Key:D4}: {string.Join(", ", duplicate.Select(s => s.Name))}.");
        return ordered;
    }
}
