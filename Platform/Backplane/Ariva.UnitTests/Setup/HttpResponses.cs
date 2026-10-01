using System.Text.Json;

namespace Ariva.UnitTests.Setup;

/// <summary>
/// Reads headers and ProblemDetails bodies from test responses.
/// </summary>
public static class HttpResponses
{
    #region Constants

    /// <summary>Problem JSON media type (RFC 9457).</summary>
    public const string ProblemJson = "application/problem+json";

    /// <summary>Text that must never reach a client: stack frames, source paths and framework or exception names.</summary>
    public static readonly IReadOnlyList<string> LeakMarkers =
    [
        "   at ",
        ".cs:line",
        "Microsoft.AspNetCore",
        "System.",
        "Kestrel",
        "Exception"
    ];

    #endregion

    #region Headers

    /// <summary>Returns a response or content header joined by commas, or null when it is absent.</summary>
    /// <param name="response">The response.</param>
    /// <param name="name">The header name.</param>
    /// <returns>The value, or null.</returns>
    public static string Header(this HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values))
        {
            return string.Join(", ", values);
        }

        return null;
    }

    #endregion

    #region Bodies

    /// <summary>Reads the body as text.</summary>
    /// <param name="response">The response.</param>
    /// <returns>The body.</returns>
    public static Task<string> BodyAsync(this HttpResponseMessage response) =>
        response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

    /// <summary>Parses a ProblemDetails body into a JSON element.</summary>
    /// <param name="body">The body text.</param>
    /// <returns>The root element, cloned so it outlives the document.</returns>
    public static JsonElement ParseProblem(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    #endregion
}
