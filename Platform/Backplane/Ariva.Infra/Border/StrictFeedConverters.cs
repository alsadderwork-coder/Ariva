using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ariva.Business.Contracts.Aman.V1;

namespace Ariva.Infra.Border;

/// <summary>
/// The JSON converters that read the AMAN feed contracts strictly on both transports (ARV-048, CWE-501): enums only by
/// their exact member name (<see cref="ExactEnumNameConverter{T}"/>) and times only with an explicit offset
/// (<see cref="ExplicitOffsetConverter"/>).
/// </summary>
public static class StrictFeedConverters
{
    /// <summary>The converters the immigration contracts are read with, REST (<c>BatchBody.StrictWithEnumNames</c>) and Kafka (<see cref="AmanFeedJson{T}"/>).</summary>
    public static IEnumerable<JsonConverter> All() =>
        [new ExactEnumNameConverter<DeskSessionState>(), new ExactEnumNameConverter<EGateRejectCategory>(), new ExplicitOffsetConverter()];
}

/// <summary>
/// An enum read only by one of its member names, compared ordinally, as a value and as a dictionary key. Unlike
/// <see cref="JsonStringEnumConverter"/>, another case (<c>"opened"</c>), padding (<c>" Opened"</c>), a number
/// (<c>1</c>, <c>"1"</c>) and comma-joined names (<c>"Opened, Closed"</c>, which it would OR into a third member) are
/// all unreadable. Written by member name.
/// </summary>
public sealed class ExactEnumNameConverter<T> : JsonConverter<T> where T : struct, Enum
{
    private static readonly Dictionary<string, T> ByName = Enum.GetNames<T>().ToDictionary(n => n, Enum.Parse<T>, StringComparer.Ordinal);

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String ? Named(reader.GetString()) : throw new JsonException();

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(Enum.GetName(value) ?? throw new JsonException());
    }

    public override T ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => Named(reader.GetString());

    public override void WriteAsPropertyName(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WritePropertyName(Enum.GetName(value) ?? throw new JsonException());
    }

    private static T Named(string name) => name is not null && ByName.TryGetValue(name, out var value) ? value : throw new JsonException();
}

/// <summary>
/// A time read only with an explicit offset (<c>Z</c> or <c>+hh:mm</c>), as ISO 8601: one without an offset would
/// otherwise be read in the server's local time.
/// </summary>
public sealed class ExplicitOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException();
        var text = reader.GetString();
        if (text is null || text.Length < 20 || !(text[^1] == 'Z' || (text[^6] is '+' or '-' && text[^3] == ':')))
            throw new JsonException();
        return reader.TryGetDateTimeOffset(out var value) ? value : throw new JsonException();
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture));
    }
}
