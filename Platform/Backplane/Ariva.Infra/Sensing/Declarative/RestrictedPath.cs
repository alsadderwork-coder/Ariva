using System.Text;
using System.Text.Json;

namespace Ariva.Infra.Sensing.Declarative;

/// <summary>
/// The path syntax of declarative mappings (ARV-024, CWE-94): a small, closed subset of JSONPath that can only walk a
/// JSON document. A path starts at <c>$</c> (the payload), <c>^</c> (the current group) or <c>@</c> (the current item)
/// and continues with member names (<c>.name</c> or <c>['name with spaces']</c>), array indexes (<c>[0]</c>) and, only
/// where a mapping enumerates, one wildcard (<c>[*]</c>). There are no filters, no recursive descent, no slices, no
/// unions, no functions and no expressions of any kind: nothing in a path is ever executed, and evaluating one touches
/// only the JSON values on its way.
/// </summary>
public sealed class RestrictedPath
{
    public const int MaxLength = 200;
    public const int MaxSegments = 16;
    public const int MaxNameLength = 64;

    private readonly Segment[] _segments;

    private RestrictedPath(char root, Segment[] segments, string text)
    {
        Root = root;
        _segments = segments;
        Text = text;
    }

    /// <summary>'$', '^' or '@'.</summary>
    public char Root { get; }

    public string Text { get; }

    public bool HasWildcard => _segments.Any(s => s.Kind == SegmentKind.Wildcard);

    private enum SegmentKind
    {
        Member,
        Index,
        Wildcard
    }

    private readonly record struct Segment(SegmentKind Kind, string Name, int Index);

    /// <summary>Parses a path; <paramref name="allowWildcard"/> permits exactly one <c>[*]</c>. Refuses everything else.</summary>
    public static RestrictedPath Parse(string text, bool allowWildcard, string where)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength)
            throw new MappingException($"{where}: a path is 1 to {MaxLength} characters.");
        var root = text[0];
        if (root is not ('$' or '^' or '@'))
            throw new MappingException($"{where}: a path starts with $, ^ or @.");

        var segments = new List<Segment>();
        var i = 1;
        while (i < text.Length)
        {
            if (segments.Count >= MaxSegments)
                throw new MappingException($"{where}: at most {MaxSegments} steps.");
            if (text[i] == '.')
            {
                var start = ++i;
                while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] is '_' or '-'))
                    i++;
                var name = text[start..i];
                if (name.Length == 0 || name.Length > MaxNameLength || !(char.IsAsciiLetter(name[0]) || name[0] == '_'))
                    throw new MappingException($"{where}: '.' is followed by a member name (a letter or _, then letters, digits, _ or -; at most {MaxNameLength}).");
                segments.Add(new Segment(SegmentKind.Member, name, 0));
            }
            else if (text[i] == '[')
            {
                var close = text.IndexOf(']', i);
                if (close < 0)
                    throw new MappingException($"{where}: an unclosed '['.");
                var inner = text[(i + 1)..close];
                if (inner == "*")
                {
                    if (!allowWildcard)
                        throw new MappingException($"{where}: a wildcard [*] is only allowed in groups and items paths.");
                    if (segments.Any(s => s.Kind == SegmentKind.Wildcard))
                        throw new MappingException($"{where}: at most one wildcard [*] per path.");
                    segments.Add(new Segment(SegmentKind.Wildcard, null, 0));
                }
                else if (inner.Length is > 0 and <= 6 && inner.All(char.IsAsciiDigit))
                {
                    segments.Add(new Segment(SegmentKind.Index, null, int.Parse(inner, System.Globalization.CultureInfo.InvariantCulture)));
                }
                else if (inner.Length >= 3 && inner[0] == '\'' && inner[^1] == '\'')
                {
                    var name = inner[1..^1];
                    if (name.Length is 0 or > MaxNameLength || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '_' or '-' or '.'))
                        throw new MappingException($"{where}: a quoted member name is 1 to {MaxNameLength} letters, digits, spaces, _, - or '.'.");
                    segments.Add(new Segment(SegmentKind.Member, name, 0));
                }
                else
                {
                    throw new MappingException($"{where}: inside [ ] only an index, * or a quoted name (no filters, slices, unions or expressions).");
                }

                i = close + 1;
            }
            else
            {
                throw new MappingException($"{where}: unexpected character at position {i}; paths have no expressions, functions or recursive descent.");
            }
        }

        return new RestrictedPath(root, [.. segments], text);
    }

    /// <summary>The value at the path (no wildcard), or null when a step is missing or of the wrong kind.</summary>
    public JsonElement? Single(JsonElement root, JsonElement group, JsonElement item)
    {
        var current = Start(root, group, item);
        foreach (var segment in _segments)
        {
            if (!Step(ref current, segment))
                return null;
        }

        return current.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null : current;
    }

    /// <summary>The values the wildcard enumerates (at most <paramref name="max"/> + 1, so the caller can refuse more).</summary>
    public IReadOnlyList<JsonElement> Many(JsonElement root, JsonElement group, int max)
    {
        var current = Start(root, group, default);
        for (var s = 0; s < _segments.Length; s++)
        {
            var segment = _segments[s];
            if (segment.Kind != SegmentKind.Wildcard)
            {
                if (!Step(ref current, segment))
                    return [];
                continue;
            }

            if (current.ValueKind != JsonValueKind.Array)
                return [];
            var results = new List<JsonElement>();
            foreach (var element in current.EnumerateArray())
            {
                var value = element;
                var ok = true;
                for (var r = s + 1; r < _segments.Length && ok; r++)
                    ok = Step(ref value, _segments[r]);
                if (ok && value.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
                    results.Add(value);
                if (results.Count > max)
                    break;
            }

            return results;
        }

        return current.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? [] : [current];
    }

    private JsonElement Start(JsonElement root, JsonElement group, JsonElement item) => Root switch
    {
        '$' => root,
        '^' => group,
        _ => item
    };

    private static bool Step(ref JsonElement current, Segment segment)
    {
        switch (segment.Kind)
        {
            case SegmentKind.Member when current.ValueKind == JsonValueKind.Object && current.TryGetProperty(segment.Name, out var next):
                current = next;
                return true;
            case SegmentKind.Index when current.ValueKind == JsonValueKind.Array && segment.Index < current.GetArrayLength():
                current = current[segment.Index];
                return true;
            default:
                return false;
        }
    }

    public override string ToString() => Text;
}

/// <summary>A mapping definition breaks a rule; the message names where.</summary>
public sealed class MappingException(string message) : Exception(message);
