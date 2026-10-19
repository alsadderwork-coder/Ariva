namespace Ariva.UnitTests.Logging.Fixtures;

/// <summary>A request the collector received: its request line and its headers (names compared ignoring case).</summary>
/// <param name="RequestLine">For example <c>POST /loki/api/v1/push HTTP/1.1</c>.</param>
/// <param name="Headers">The headers; a header sent twice keeps its last value.</param>
public sealed record CapturedRequest(string RequestLine, IReadOnlyDictionary<string, string> Headers)
{
    /// <summary>Reads a request head (the request line, then one header per line).</summary>
    /// <param name="head">The head, without the blank line that ends it.</param>
    /// <returns>The request.</returns>
    public static CapturedRequest Parse(string head)
    {
        ArgumentNullException.ThrowIfNull(head);
        var lines = head.Split("\r\n");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        return new CapturedRequest(lines[0], headers);
    }
}
