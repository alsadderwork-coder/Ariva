using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ariva.Core.Flights;
using Ariva.Core.Integration;

namespace Ariva.Api.Integration.Batches;

/// <summary>
/// Reads an Integration API batch body (ARV-043): JSON only, at most 1 MB (also when chunked), read strictly (CWE-20,
/// CWE-501): member names exactly as documented (camelCase), no unknown member, no member twice, no comments or trailing
/// commas, numbers as numbers, at most 8 levels deep. A refusal names where the body went wrong only through members
/// Ariva knows, so a client's text is never echoed.
/// </summary>
public static class BatchBody
{
    public static readonly JsonSerializerOptions Strict = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        NumberHandling = JsonNumberHandling.Strict,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        MaxDepth = 8
    };

    private static readonly HashSet<string> Members = new(
        new[] { typeof(IntegrationBatch<object>), typeof(FlightLegData), typeof(FlightEventData), typeof(CounterAllocationData) }
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name)),
        StringComparer.Ordinal);

    /// <summary>True when the content type is JSON (application/json or a +json type).</summary>
    public static bool IsJson(string contentType) =>
        Microsoft.Net.Http.Headers.MediaTypeHeaderValue.TryParse(contentType, out var type) &&
        (type.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) || type.Suffix.Equals("json", StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the content type is XML (application/xml, text/xml or a +xml type).</summary>
    public static bool IsXml(string contentType) =>
        Microsoft.Net.Http.Headers.MediaTypeHeaderValue.TryParse(contentType, out var type) &&
        (type.MediaType.Equals("application/xml", StringComparison.OrdinalIgnoreCase) || type.MediaType.Equals("text/xml", StringComparison.OrdinalIgnoreCase) ||
         type.Suffix.Equals("xml", StringComparison.OrdinalIgnoreCase));

    /// <summary>The body, or null when it is larger than <see cref="IntegrationBatches.MaxBodyBytes"/>.</summary>
    public static Task<byte[]> ReadAsync(HttpRequest request, CancellationToken ct) => ReadAsync(request, IntegrationBatches.MaxBodyBytes, ct);

    /// <summary>The body, or null when it is larger than <paramref name="maxBytes"/> (also when it has no length).</summary>
    public static async Task<byte[]> ReadAsync(HttpRequest request, int maxBytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ContentLength > maxBytes)
            return null;
        using var buffer = new MemoryStream(request.ContentLength is { } length ? (int)length : 16 * 1024);
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>The batch, or why it is not one.</summary>
    public static (IntegrationBatch<T> Batch, string Error) Parse<T>(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        IntegrationBatch<T> batch;
        try
        {
            batch = JsonSerializer.Deserialize<IntegrationBatch<T>>(body, Strict);
        }
        catch (JsonException e)
        {
            return (null, $"Not a batch at {KnownPath(e.Path)}: malformed JSON, an unknown or repeated member, a wrong type or a value out of range.");
        }

        if (batch is null)
            return (null, "A batch is a JSON object with items.");
        if (batch.Items is null || batch.Items.Count is 0 or > IntegrationBatches.MaxItems)
            return (null, $"A batch has 1 to {IntegrationBatches.MaxItems} items.");
        return (batch, null);
    }

    // The path up to the first member Ariva does not know (array indices kept), so an unknown member's name is never echoed.
    private static string KnownPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 200 || path[0] != '$')
            return "$";
        var kept = "$";
        var rest = path[1..];
        while (rest.Length > 0)
        {
            if (rest[0] == '.')
            {
                var end = rest.IndexOfAny(['.', '['], 1) is var next and > 0 ? next : rest.Length;
                var name = rest[1..end];
                if (!Members.Contains(name))
                    break;
                kept += "." + name;
                rest = rest[end..];
            }
            else if (rest[0] == '[' && rest.IndexOf(']', StringComparison.Ordinal) is var close and > 1 && int.TryParse(rest[1..close], out var index))
            {
                kept += $"[{index}]";
                rest = rest[(close + 1)..];
            }
            else
                break;
        }

        return kept;
    }
}
