namespace Ariva.Core.Domain.Enums;

/// <summary>
/// The uploads Ariva.Api.Main takes (ARV-092b): each is scanned for malware before it is parsed, stored or imported, and
/// a refusal is audited under its kind (the audit entry's target type).
/// </summary>
public enum UploadKind
{
    /// <summary>A level's floor plan (ARV-018).</summary>
    FloorPlan,

    /// <summary>An SSIM schedule file, at preview and again at import (ARV-046).</summary>
    FlightSchedule,

    /// <summary>A device CSV import, at preview and again at commit (ARV-123a).</summary>
    DeviceImport,

    /// <summary>A desk code mapping CSV import, at preview and again at commit (ARV-123a).</summary>
    DeskCodeMappingImport,

    /// <summary>Every decoded part of a site configuration bundle (ARV-122a).</summary>
    SiteConfiguration,

    /// <summary>An advertisement image or video (ARV-142a, ARV-142b).</summary>
    AdvertisingMedia
}

/// <summary>The verdict of a malware scan (ARV-092b).</summary>
public enum MalwareVerdict
{
    /// <summary>No known threat was found.</summary>
    Clean,

    /// <summary>The scanner found a known threat.</summary>
    Infected,

    /// <summary>The scanner gave no verdict (not configured, not reachable, an error, a timeout, content beyond the limit).</summary>
    Unavailable
}
