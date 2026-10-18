using System.Text.Json.Serialization;

namespace Ariva.Core.Domain.Enums;

/// <summary>
/// What a playlist item plays (ARV-143a); stored by name, in columns and in the playlist's JSON alike. Media: an
/// advertisement of the site's library. Slide: an instruction slide of the site.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AdvertisingItemKind>))]
public enum AdvertisingItemKind
{
    Media,
    Slide
}
