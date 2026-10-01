using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ariva.UnitTests.Setup;

/// <summary>
/// One exception in security/allowlist.json (see security/README.md).
/// </summary>
/// <param name="Rule">The scanner rule, for example SEC-052.</param>
/// <param name="Path">Repository relative file, forward slashes.</param>
/// <param name="Contains">Optional text that must appear on the flagged line.</param>
/// <param name="Routes">For SEC-052, the anonymous route patterns the entry covers.</param>
/// <param name="Reason">Why the exception is safe.</param>
/// <param name="ProposedBy">Who proposed it.</param>
/// <param name="ApprovedBy">Who approved it, or "PENDING: name".</param>
/// <param name="Date">ISO date of the proposal.</param>
public sealed record AllowlistEntry(
    string Rule,
    string Path,
    string Contains,
    IReadOnlyList<string> Routes,
    string Reason,
    string ProposedBy,
    string ApprovedBy,
    string Date);

/// <summary>
/// Reads and validates security/allowlist.json.
/// </summary>
public static partial class SecurityAllowlist
{
    #region Constants

    /// <summary>The rule for anonymous endpoints, whose entries must list their routes.</summary>
    public const string AnonymousRule = "SEC-052";

    private static readonly string[] RequiredFields = ["rule", "path", "reason", "proposedBy", "approvedBy", "date"];

    #endregion

    #region Loading

    /// <summary>Loads the repository's allowlist.</summary>
    /// <returns>The entries, in file order.</returns>
    public static IReadOnlyList<AllowlistEntry> Load()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepositoryPaths.Allowlist));
        return document.RootElement.EnumerateArray().Select(ToEntry).ToList();
    }

    #endregion

    #region Validation

    /// <summary>
    /// Returns every problem in an allowlist document: a missing or empty required field, a malformed rule id or
    /// date, a path that does not exist (when <paramref name="repositoryRoot"/> is given), an empty
    /// <c>contains</c>, or an anonymous endpoint entry without routes.
    /// </summary>
    /// <param name="json">The allowlist JSON.</param>
    /// <param name="repositoryRoot">The repository root used to check paths, or null to skip that check.</param>
    /// <returns>The problems; empty when the allowlist is complete.</returns>
    public static IReadOnlyList<string> Validate(string json, string repositoryRoot)
    {
        var problems = new List<string>();
        using var document = JsonDocument.Parse(json);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return ["the allowlist must be a JSON array"];
        }

        var index = 0;
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var label = $"entry {index++}";
            problems.AddRange(ValidateEntry(element, label, repositoryRoot));
        }

        return problems;
    }

    private static IEnumerable<string> ValidateEntry(JsonElement element, string label, string repositoryRoot)
    {
        foreach (var field in RequiredFields)
        {
            if (string.IsNullOrWhiteSpace(ReadString(element, field)))
            {
                yield return $"{label}: '{field}' is missing or empty";
            }
        }

        var entry = ToEntry(element);

        if (entry.Rule is not null && !RuleId().IsMatch(entry.Rule))
        {
            yield return $"{label}: rule '{entry.Rule}' is not SEC-nnn";
        }

        if (entry.Date is not null && !DateOnly.TryParseExact(entry.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            yield return $"{label}: date '{entry.Date}' is not yyyy-MM-dd";
        }

        if (entry.Path is not null && entry.Path.Contains('\\', StringComparison.Ordinal))
        {
            yield return $"{label}: path '{entry.Path}' must use forward slashes";
        }
        else if (entry.Path is not null && repositoryRoot is not null
                 && !File.Exists(System.IO.Path.Combine([repositoryRoot, .. entry.Path.Split('/')])))
        {
            yield return $"{label}: path '{entry.Path}' does not exist";
        }

        if (element.TryGetProperty("contains", out _) && string.IsNullOrWhiteSpace(entry.Contains))
        {
            yield return $"{label}: 'contains' is empty";
        }

        if (entry.Rule == AnonymousRule && (entry.Routes.Count == 0 || entry.Routes.Any(route => !route.StartsWith('/'))))
        {
            yield return $"{label}: {AnonymousRule} entries must list the anonymous routes they cover in 'routes'";
        }
    }

    #endregion

    #region Helpers

    private static AllowlistEntry ToEntry(JsonElement element) => new(
        ReadString(element, "rule"),
        ReadString(element, "path"),
        ReadString(element, "contains"),
        element.TryGetProperty("routes", out var routes) && routes.ValueKind == JsonValueKind.Array
            ? routes.EnumerateArray().Select(route => route.GetString() ?? string.Empty).ToList()
            : [],
        ReadString(element, "reason"),
        ReadString(element, "proposedBy"),
        ReadString(element, "approvedBy"),
        ReadString(element, "date"));

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [GeneratedRegex(@"^SEC-\d{3}$")]
    private static partial Regex RuleId();

    #endregion
}
