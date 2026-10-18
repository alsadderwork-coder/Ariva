using Ariva.Core.Domain.Entities;

namespace Ariva.Infra.Services.Displays;

/// <summary>
/// The videos an installation's advertisement library takes (ARV-142b, section <c>Advertising:Videos</c>): at most
/// <see cref="MaxMegabytes"/> and <see cref="MaxSeconds"/>, within the ceilings every installation keeps
/// (<see cref="AdvertisingMedia.MaxVideoBytes"/>, <see cref="AdvertisingMedia.MaxVideoSeconds"/>). A size above 20 MB also
/// needs the main API's ingress body size raised to match (wiki 11 section 9).
/// </summary>
public sealed class AdvertisingVideoSettings
{
    public const string SectionName = "Advertising:Videos";

    public int MaxMegabytes { get; set; } = 20;

    public int MaxSeconds { get; set; } = 30;

    public long MaxBytes => MaxMegabytes * 1024L * 1024L;

    public bool IsValid =>
        MaxMegabytes is >= 1 && MaxBytes <= AdvertisingMedia.MaxVideoBytes && MaxSeconds is >= 1 and <= AdvertisingMedia.MaxVideoSeconds;
}
