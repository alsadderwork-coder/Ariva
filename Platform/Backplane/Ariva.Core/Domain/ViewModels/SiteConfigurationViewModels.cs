namespace Ariva.Core.Domain.ViewModels;

/// <summary>
/// A site configuration bundle (ARV-122a): the document an export answers inside <see cref="SiteConfigurationExportViewModel"/>
/// and an import takes as its body. Each part is the base64 of its bytes; the manifest holds the SHA-256 (lower-case hex)
/// of each part's decoded bytes.
/// </summary>
public sealed record SiteConfigurationBundleViewModel(
    int FormatVersion,
    string SourceSite,
    string ExportedAtUtc,
    IReadOnlyDictionary<string, string> Manifest,
    IReadOnlyDictionary<string, string> Parts);

/// <summary>How many records of each kind a bundle holds.</summary>
public sealed record SiteConfigurationCountsViewModel(
    int Terminals,
    int Levels,
    int Checkpoints,
    int Desks,
    int DeskCodeMappings,
    int Zones,
    int Lines,
    int FloorPlans,
    int Devices,
    int AlertRules,
    int Displays);

/// <summary>A device's new credential (ARV-122b), shown this once like a registration's; only its prefix and hash are kept.</summary>
public sealed record SiteConfigurationDeviceCredentialViewModel(string Code, string Credential);

/// <summary>
/// An export's answer: the bundle and its hash (compare it with the import's answer out of band), whether the site's
/// published zone profile is in it with its F22 v1 and portable geometry hashes, the counts, and fixed notes.
/// </summary>
public sealed record SiteConfigurationExportViewModel(
    string BundleSha256,
    bool ZoneProfileIncluded,
    int? ProfileVersion,
    string GeometryHash,
    string PortableGeometryHash,
    SiteConfigurationCountsViewModel Counts,
    IReadOnlyList<string> Notes,
    SiteConfigurationBundleViewModel Bundle);

/// <summary>
/// An import's answer: the bundle hash it verified, the bundle's claimed source site (not authenticated), the draft zone
/// profile it created (publish it with the existing step-up action) and that draft's portable geometry hash, the counts,
/// each device's new credential (shown this once), the codes of the devices whose access settings (source networks,
/// certificate pin) and of the alert rules whose escalation contact must be set again, and fixed notes. Never cached.
/// </summary>
public sealed record SiteConfigurationImportViewModel(
    string BundleSha256,
    string SourceSite,
    Guid? DraftProfileId,
    string PortableGeometryHash,
    SiteConfigurationCountsViewModel Counts,
    IReadOnlyList<SiteConfigurationDeviceCredentialViewModel> DeviceCredentials,
    IReadOnlyList<string> DevicesNeedingAccessSettings,
    IReadOnlyList<string> RulesNeedingEscalationContact,
    IReadOnlyList<string> Notes);
