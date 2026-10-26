using System.Collections;
using System.Reflection;
using System.Text.Json;

namespace Ariva.Testing;

/// <summary>
/// ARV-251: a test project's row file (theory-rows.json, copied beside the test executable) names each theory xUnit lists once,
/// its data being one that cannot be serialized, with its number of rows; the count guard (scripts/test-count-guard.mjs) holds
/// every run to it. This keeps the file current from the theories' own data: every entry a theory of the assembly with exactly
/// that many rows, counted from its InlineData, MemberData and ClassData attributes (any other data attribute is reported, not
/// guessed at; xUnit's own GetData answers no rows outside its pipeline), and no method named twice. It also refuses a test or a
/// data row under a display name of its own, since the guard matches the listing and the run by the method's own name. One
/// source for Ariva.UnitTests and Ariva.IntegrationTests (linked into the second); each project's TheoryRowsTests runs it.
/// </summary>
internal static class TheoryRows
{
    #region Fields

    /// <summary>The row file's name, beside the project file and the test executable.</summary>
    public const string FileName = "theory-rows.json";

    private const BindingFlags Methods = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy;

    #endregion

    #region Methods

    /// <summary>Every problem of an assembly's row file (read beside the test executable) and of its tests' names.</summary>
    public static async Task<IReadOnlyList<string>> ProblemsAsync(Assembly assembly, CancellationToken ct) =>
        [.. FileProblems(assembly, await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, FileName), ct)), .. NameProblems(assembly)];

    /// <summary>The problems of a row file's text against an assembly's theories.</summary>
    public static IReadOnlyList<string> FileProblems(Assembly assembly, string text)
    {
        using var file = JsonDocument.Parse(text);
        if (file.RootElement.ValueKind != JsonValueKind.Object)
            return ["the row file is not a JSON object"];
        var problems = new List<string>();
        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in file.RootElement.EnumerateObject())
        {
            if (!named.Add(entry.Name))
                problems.Add($"{entry.Name}: named twice in the row file");
            else if (entry.Value.ValueKind != JsonValueKind.Number || !entry.Value.TryGetInt32(out var expected) || expected is < 2 or > 100_000)
                problems.Add($"{entry.Name}: the row file's count is not a whole number from 2 to 100,000");
            else if (TheoryOf(assembly, entry.Name) is not { } method)
                problems.Add($"{entry.Name}: no theory of that name in this assembly");
            else if (RowsOf(method) is var actual && actual != expected)
                problems.Add(actual < 0 ? $"{entry.Name}: a data attribute these tests cannot count" : $"{entry.Name}: {expected} rows in the row file, {actual} in its data");
        }

        return problems;
    }

    /// <summary>Every test of an assembly that runs under a display name of its own, for itself or for a data row.</summary>
    public static IReadOnlyList<string> NameProblems(Assembly assembly) =>
    [
        .. assembly.GetTypes().SelectMany(t => t.GetMethods(Methods | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttributes().OfType<FactAttribute>().Any())
            .SelectMany(m => NameProblems($"{m.DeclaringType?.FullName}.{m.Name}", m.GetCustomAttributes().OfType<FactAttribute>().First(),
                m.GetCustomAttributes().OfType<Xunit.v3.IDataAttribute>().ToList(),
                m.GetCustomAttributes().Where(a => a is Xunit.v3.IDataAttribute).SelectMany(a => ItemsOf(m, a) ?? [])))
    ];

    /// <summary>A test's own display name, one its data attributes give their rows, or one a data row gives itself.</summary>
    public static IEnumerable<string> NameProblems(string name, FactAttribute test, IReadOnlyList<Xunit.v3.IDataAttribute> data, IEnumerable<object> rows)
    {
        if (test.DisplayName is not null)
            yield return $"{name}: a display name of its own";
        if (data.Any(d => d.TestDisplayName is not null))
            yield return $"{name}: a data attribute names its rows";
        if (rows.OfType<ITheoryDataRow>().Any(r => r.TestDisplayName is not null))
            yield return $"{name}: a data row names itself";
    }

    /// <summary>The theory a listing names (namespace, class and method, a nested class's name after a dot), or null.</summary>
    public static MethodInfo TheoryOf(Assembly assembly, string name)
    {
        var dot = name.LastIndexOf('.');
        if (dot <= 0)
            return null;
        var (typeName, methodName) = (name[..dot], name[(dot + 1)..]);
        var methods = assembly.GetTypes()
            .Where(t => t.FullName?.Replace('+', '.') == typeName)
            .SelectMany(t => t.GetMethods(Methods))
            .Where(m => m.Name == methodName && m.GetCustomAttribute<TheoryAttribute>() is not null)
            .ToList();
        return methods.Count == 1 ? methods[0] : null;
    }

    /// <summary>The rows of every data attribute of a theory, or -1 when one of them is of a kind not counted here.</summary>
    public static int RowsOf(MethodInfo method)
    {
        var rows = 0;
        foreach (var data in method.GetCustomAttributes().Where(a => a is Xunit.v3.IDataAttribute))
        {
            if (ItemsOf(method, data) is not { } items)
                return -1;
            rows += items.Count();
        }

        return rows;
    }

    /// <summary>The rows a data attribute gives (an inline row is one), or null for a kind not read here.</summary>
    private static IEnumerable<object> ItemsOf(MethodInfo method, Attribute data) => data switch
    {
        InlineDataAttribute inline => [inline.Data],
        MemberDataAttribute member => (MemberValue(method, member) as IEnumerable)?.Cast<object>(),
        ClassDataAttribute source => (Activator.CreateInstance(source.Class) as IEnumerable)?.Cast<object>(),
        _ => null
    };

    /// <summary>The value a member data attribute names: a static property, field or method of the test class or its MemberType.</summary>
    private static object MemberValue(MethodInfo method, MemberDataAttribute member)
    {
        var type = member.MemberType ?? method.ReflectedType;
        if (type.GetProperty(member.MemberName, Members) is { } property)
            return property.GetValue(null);
        if (type.GetField(member.MemberName, Members) is { } field)
            return field.GetValue(null);
        var arguments = member.Arguments ?? [];
        var methods = type.GetMethods(Members).Where(m => m.Name == member.MemberName && m.GetParameters().Length == arguments.Length).ToList();
        return methods.Count == 1 ? methods[0].Invoke(null, arguments) : null;
    }

    #endregion
}
