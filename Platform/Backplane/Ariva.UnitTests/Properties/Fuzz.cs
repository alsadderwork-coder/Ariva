using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsCheck;

namespace Ariva.UnitTests.Properties;

/// <summary>
/// Generated inputs for the parser properties (ARV-070, CWE-120): random bytes, a valid sample cut short, the sample with a
/// few bytes replaced, inserted or removed (mostly JSON and XML punctuation, digits and invalid UTF-8), and for JSON samples
/// a value somewhere in the document replaced by another of any type. CsCheck shrinks a failing input to a small one and
/// prints the seed that reproduces it.
/// </summary>
public static class Fuzz
{
    /// <summary>
    /// How many inputs each property tries per generator (CsCheck's default is 100); ARIVA_FUZZ_ITERATIONS raises it for a
    /// deeper run (ARV-070 ran the parser properties at 20,000 and the formula properties at five times that setting).
    /// </summary>
    public static readonly int Iterations =
        int.TryParse(Environment.GetEnvironmentVariable("ARIVA_FUZZ_ITERATIONS"), out var n) && n is > 0 and <= 1_000_000 ? n : 400;

    private static readonly byte[] Interesting = Encoding.ASCII.GetBytes("{}[]\":,\\0123456789-+.eE ntfl<>/=&;'\r\n\t")
        .Concat(new byte[] { 0x00, 0x7F, 0x80, 0xBF, 0xC0, 0xC3, 0xE2, 0xEF, 0xF0, 0xFF }).ToArray();

    public static readonly Gen<byte[]> Bytes = Gen.Byte.Array[0, 2048];

    private static readonly Gen<byte> Byte = Gen.Frequency((3, Gen.OneOfConst(Interesting)), (1, Gen.Byte));

    /// <summary>The sample cut at any length.</summary>
    public static Gen<byte[]> Truncated(byte[] seed) => Gen.Int[0, seed.Length].Select(n => seed[..n]);

    /// <summary>The sample with 1 to 8 edits: a byte replaced, inserted or removed.</summary>
    public static Gen<byte[]> Mutated(byte[] seed) =>
        Gen.Select(Gen.Int[0, 2], Gen.Double[0, 1], Byte).Array[1, 8].Select(edits =>
        {
            var bytes = new List<byte>(seed);
            foreach (var (kind, at, value) in edits)
            {
                var i = (int)(at * bytes.Count);
                switch (kind)
                {
                    case 0 when bytes.Count > 0:
                        bytes[Math.Min(i, bytes.Count - 1)] = value;
                        break;
                    case 1:
                        bytes.Insert(i, value);
                        break;
                    case 2 when bytes.Count > 0:
                        bytes.RemoveAt(Math.Min(i, bytes.Count - 1));
                        break;
                }
            }

            return bytes.ToArray();
        });

    /// <summary>Any JSON value, with member names taken from <paramref name="names"/> so the mappers look inside it.</summary>
    public static Gen<JsonNode> Json(string[] names, int depth = 3)
    {
        var name = names.Length == 0 ? Gen.String[0, 8] : Gen.Frequency((4, Gen.OneOfConst(names)), (1, Gen.String[0, 8]));
        var scalar = Gen.OneOf<JsonNode>(
            Gen.Const((JsonNode)null),
            Gen.Bool.Select(b => (JsonNode)JsonValue.Create(b)),
            Gen.Int.Select(i => (JsonNode)JsonValue.Create(i)),
            Gen.Long.Select(l => (JsonNode)JsonValue.Create(l)),
            Gen.Double.Where(double.IsFinite).Select(d => (JsonNode)JsonValue.Create(d)),
            Gen.OneOfConst("", "2026-10-03T18:30:00Z", "2026-10-03T18:30:00", "1791000000000", "-1", "1e308", "NaN", "\u0000", new string('a', 300))
                .Select(s => (JsonNode)JsonValue.Create(s)),
            Gen.String[0, 16].Select(s => (JsonNode)JsonValue.Create(s)));
        if (depth == 0)
            return scalar;
        var inner = Json(names, depth - 1);
        return Gen.Frequency(
            (4, scalar),
            (2, inner.Array[0, 4].Select(items => (JsonNode)new JsonArray(items.Select(i => i?.DeepClone()).ToArray()))),
            (2, Gen.Select(name, inner).Array[0, 5].Select(members =>
            {
                var o = new JsonObject();
                foreach (var (key, value) in members)
                    o[key] = value?.DeepClone();
                return (JsonNode)o;
            })));
    }

    /// <summary>The JSON sample with one value, anywhere in it, replaced by a generated one.</summary>
    public static Gen<byte[]> Retyped(byte[] seed)
    {
        var paths = Paths(JsonNode.Parse(seed)).ToArray();
        var names = Names(JsonNode.Parse(seed)).Distinct().ToArray();
        return Gen.Select(Gen.Int[0, paths.Length - 1], Json(names, 2)).Select(pair =>
        {
            var root = JsonNode.Parse(seed)!;
            var path = paths[pair.Item1];
            if (path.Count == 0)
                return JsonSerializer.SerializeToUtf8Bytes(pair.Item2);
            var parent = Walk(root, path.Take(path.Count - 1));
            switch (parent, path[^1])
            {
                case (JsonObject o, string key):
                    o[key] = pair.Item2?.DeepClone();
                    break;
                case (JsonArray a, int index):
                    a[index] = pair.Item2?.DeepClone();
                    break;
            }

            return JsonSerializer.SerializeToUtf8Bytes(root);
        });
    }

    /// <summary>
    /// The JSON sample with a JSON-escaped lone surrogate (\uD800 or \uDC00) after 1 to 3 of its quotes, so a member name or
    /// a string starts with one: valid UTF-8 that parses, then fails when read (the review of ARV-070 found readers that did).
    /// </summary>
    public static Gen<byte[]> Surrogates(byte[] seed)
    {
        var quotes = seed.Select((b, i) => (b, i)).Where(x => x.b == (byte)'"').Select(x => x.i + 1).ToArray();
        return Gen.Select(Gen.OneOfConst(quotes).Array[1, 3], Gen.OneOfConst(@"\uD800", @"\uDC00", @"\uDFFF", @"\uD83D")).Select((at, escape) =>
        {
            var bytes = new List<byte>(seed);
            foreach (var i in at.Distinct().OrderDescending())
                bytes.InsertRange(i, Encoding.ASCII.GetBytes(escape));
            return bytes.ToArray();
        });
    }

    /// <summary>Every input kind for one sample: truncated, mutated and, for JSON, retyped or with escaped lone surrogates; plus random bytes.</summary>
    public static Gen<byte[]> From(byte[] seed, bool json) => json
        ? Gen.Frequency((1, Bytes), (2, Truncated(seed)), (3, Mutated(seed)), (3, Retyped(seed)), (2, Surrogates(seed)))
        : Gen.Frequency((1, Bytes), (2, Truncated(seed)), (3, Mutated(seed)));

    private static JsonNode Walk(JsonNode node, IEnumerable<object> path)
    {
        foreach (var step in path)
            node = step is string key ? node![key] : node![(int)step];
        return node;
    }

    private static IEnumerable<List<object>> Paths(JsonNode node, List<object> prefix = null)
    {
        prefix ??= [];
        yield return prefix;
        switch (node)
        {
            case JsonObject o:
                foreach (var (key, value) in o)
                    foreach (var p in Paths(value, [.. prefix, key]))
                        yield return p;
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                    foreach (var p in Paths(a[i], [.. prefix, i]))
                        yield return p;
                break;
        }
    }

    private static IEnumerable<string> Names(JsonNode node) => node switch
    {
        JsonObject o => o.SelectMany(m => Names(m.Value).Prepend(m.Key)),
        JsonArray a => a.SelectMany(Names),
        _ => []
    };
}
