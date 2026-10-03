using System.Text.Json;
using System.Text.Json.Serialization;
using Ariva.Core.Flights;

namespace Ariva.Infra.Flights.Acris;

/// <summary>
/// Reads an ACRIS flight answer (ARV-045): a JSON array of flights, or an object with a <c>flights</c> array, of at
/// most <see cref="MaxFlights"/> flights. Strict where it matters (types, repeated members, depth 16, no comments) and
/// tolerant of members Ariva does not read, since a partner's resource carries more than Ariva uses. A refusal names
/// no value from the answer.
/// </summary>
public static class AcrisReader
{
    public const int MaxFlights = 5_000;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        AllowDuplicateProperties = false,
        NumberHandling = JsonNumberHandling.Strict,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        MaxDepth = 16
    };

    public static (IReadOnlyList<AcrisFlight> Flights, string Error) Read(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16, CommentHandling = JsonCommentHandling.Disallow });
            var root = document.RootElement;
            var array = root.ValueKind switch
            {
                JsonValueKind.Array => root,
                JsonValueKind.Object when root.TryGetProperty("flights", out var flights) && flights.ValueKind == JsonValueKind.Array => flights,
                _ => default
            };
            if (array.ValueKind != JsonValueKind.Array)
                return (null, "The answer is not a list of flights.");
            if (array.GetArrayLength() > MaxFlights)
                return (null, $"The answer has more than {MaxFlights} flights.");
            return (array.Deserialize<List<AcrisFlight>>(Options) ?? [], null);
        }
        catch (JsonException)
        {
            return (null, "The answer is not ACRIS flight JSON as Ariva reads it (malformed, a repeated member or a wrong type).");
        }
    }
}
