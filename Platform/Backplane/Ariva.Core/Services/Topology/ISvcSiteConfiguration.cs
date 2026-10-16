using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;

namespace Ariva.Core.Services.Topology;

/// <summary>
/// Site configuration export and import (ARV-122a, ARV-122b): a site's topology, desk code mappings, published zone
/// profile, floor plans, devices, alert rules and displays as one bundle with a SHA-256 manifest, imported into an empty
/// site only. No credential, calibration, access setting, escalation contact or pairing secret is exported; an import
/// registers each device through the registration path with a new credential (answered once) and leaves displays
/// unpaired. Both are critical actions (the controller asks for a recent second factor), scoped to the caller's sites
/// (404 outside, nothing echoed) and audited with the bundle hash, the counts and the outcome, never a part's content
/// or a credential.
/// </summary>
public interface ISvcSiteConfiguration : ISvcScoped
{
    /// <summary>The site's configuration as a bundle; NotFound outside the caller's sites; a conflict when it cannot be exported whole.</summary>
    Task<Result<SiteConfigurationExportViewModel>> ExportAsync(string siteCode, CancellationToken ct = default);

    /// <summary>
    /// Imports a bundle into an empty site in one transaction: every part is verified against the manifest, decoded and
    /// checked as the existing endpoints check what an administrator types, then created with new ids; the zone profile
    /// arrives as a draft. <paramref name="airportCode"/>, when given, places every terminal at that existing airport
    /// instead of the bundle's own.
    /// </summary>
    Task<Result<SiteConfigurationImportViewModel>> ImportAsync(string siteCode, ImportSiteConfigurationRequest request, string airportCode, CancellationToken ct = default);
}

/// <summary>
/// The fixed answers of the site configuration endpoints (CWE-79, CWE-501): none repeats a value from the bundle or the
/// request. The controller maps <see cref="TopologyErrors.NotFound"/> to 404, <see cref="Conflicts"/> to 409,
/// <see cref="PartTooLarge"/> to 413 and the rest to 400.
/// </summary>
public static class SiteConfigurationErrors
{
    public const string NotABundle = "The body is not a site configuration bundle: it needs formatVersion, sourceSite, exportedAtUtc, manifest and parts.";
    public const string UnsupportedFormat = "This version of Ariva imports bundles of format version 1 only.";
    public const string ManifestNotValid = "The bundle's manifest is not valid: each entry gives a part's SHA-256 in lower-case hex.";
    public const string PartsDoNotMatchManifest = "The bundle's parts do not match its manifest: a part is missing, extra or unknown, or an entry has no part.";
    public const string HashMismatch = "A part of the bundle does not match its SHA-256 in the manifest.";
    public const string PartNotReadable = "A part of the bundle is not valid base64, or not valid JSON of its kind.";
    public const string PartTooLarge = "A part of the bundle is larger than an import takes.";
    public const string InvalidTopology = "The topology part of the bundle is not valid.";
    public const string InvalidDeskCodeMappings = "The desk code mappings part of the bundle is not valid.";
    public const string InvalidZoneProfile = "The zone profile part of the bundle is not valid.";
    public const string QueueZoneNameTooLong = "A queue zone name of the bundle is too long for this site: the site code, a slash and the name hold at most 200 characters.";
    public const string ProfileNotReproduced = "The zone profile of the bundle does not reproduce its portable geometry hash.";
    public const string InvalidFloorPlans = "The floor plans part of the bundle is not valid.";
    public const string FloorPlanFileRefused = "A floor plan file of the bundle is not a PNG, JPEG or SVG image within the floor plan limits, or its name is not safe.";
    public const string InvalidAirportCode = "airportCode is an IATA code: three upper case letters.";
    public const string UnknownAirport = "An airport the import names does not exist here; an import never creates an airport.";
    public const string SiteNotEmpty = "The site already has configuration; a bundle is imported into an empty site only.";
    public const string TerminalTaken = "A terminal code of the bundle is already used at its airport.";
    public const string AmbiguousTerminals = "The site has two terminals with one code at different airports, which a bundle cannot tell apart.";
    public const string ProfileOnRemovedTopology = "The published zone profile names a level or desk that no longer exists; publish one on the current topology first.";
    public const string TooManyRecords = "The site has more records of a kind than a bundle holds.";
    public const string TooLargeToExport = "The site's configuration is larger than an import takes.";
    public const string FloorPlanFileUnreadable = "A floor plan file of the site is missing from storage or does not match its record.";
    public const string InvalidDevices = "The devices part of the bundle is not valid, or a device does not meet the registration's rules at this site.";
    public const string InvalidAlertRules = "The alert rules part of the bundle is not valid.";
    public const string AlertRuleZonesNotInProfile = "An alert rule of the bundle names a zone the imported zone profile does not have as a queue zone or overflow band (health checks and open desks below recommendation: queue zones only).";
    public const string InvalidDisplays = "The displays part of the bundle is not valid.";
    public const string DisplayZonesNotInProfile = "A display of the bundle shows a zone that is not a queue zone of the imported zone profile.";
    public const string DisplayCodeTaken = "A display code of the bundle is already used by a live display here; display codes are unique across the deployment.";
    public const string DeviceOnRemovedLevel = "A device of the site hangs on a level that no longer exists; move or retire it first.";
    public const string ReferencesOutsideProfile = "A device, alert rule or display names a zone the published zone profile does not have; change it, or publish a profile with that zone, first.";
    public const string RuleCodeTooHigh = "An alert rule code of the bundle is above R-100000, which an import does not take, so the target site keeps codes for new rules.";
    public const string RuleCodesBeyondImportBound = "An alert rule of the site has a code above R-100000, which an import does not take, so the site cannot be exported whole.";

    /// <summary>
    /// ARV-162: the export refuses what its import would refuse of values saved under older rules (topology names, the
    /// published profile's names and outlines, floor plan file names, device models, alert rule names and display texts);
    /// each text names the kind of record, never a value.
    /// </summary>
    public const string TopologyNamesNotImportable =
        "A terminal, level, checkpoint or desk of the site has a name with a control, invisible format or broken character, which an import refuses; rename it first.";

    public const string ProfileNotImportable =
        "The published zone profile has a name with a control, invisible format or broken character, or a zone whose outline touches or crosses itself, which an import refuses; publish a corrected version first.";

    public const string FloorPlanNamesNotImportable =
        "A floor plan of the site has a file name with a path, a control, invisible format or broken character, or more than 200 characters, which an import refuses; upload the plan again under a clean name first.";

    public const string TextsNotImportable =
        "A device model, alert rule name or display text of the site has a control, invisible format or broken character, which an import refuses; change it first.";

    /// <summary>Notes of the answers (fixed texts).</summary>
    public const string NoPublishedProfile = "The site has no published zone profile, so the bundle carries none.";
    public const string ProfileArrivesAsDraft = "The zone profile arrived as a draft: review it and publish it with a recent second factor.";
    public const string BundleCarriesNoProfile = "The bundle carries no zone profile.";
    public const string DevicesArriveCommissioning = "Devices arrived in Commissioning, each with a new credential shown this once: install it on the device and calibrate it.";
    public const string AccessSettingsWithheld = "Device access settings are never exported: set the source networks and certificate pin again for the devices listed in devicesNeedingAccessSettings.";
    public const string DisplaysArriveUnpaired = "Displays arrived unpaired: issue each player's credential with the display's credential action.";
    public const string RulesAndDisplaysWaitForPublication = "Alert rules and displays act once the imported zone profile is published.";
    public const string EscalationContactsWithheld = "Escalation contacts are never exported: type them again for the rules listed in rulesNeedingEscalationContact.";

    /// <summary>Errors that mean the site's state does not allow the action (409).</summary>
    public static readonly IReadOnlySet<string> Conflicts = new HashSet<string>(StringComparer.Ordinal)
    {
        SiteNotEmpty, TerminalTaken, AmbiguousTerminals, ProfileOnRemovedTopology, TooManyRecords, TooLargeToExport, FloorPlanFileUnreadable, DisplayCodeTaken,
        ReferencesOutsideProfile, DeviceOnRemovedLevel, RuleCodesBeyondImportBound, TopologyNamesNotImportable, ProfileNotImportable, TextsNotImportable,
        FloorPlanNamesNotImportable
    };
}

/// <summary>
/// The bounds of a bundle (ARV-122a, ARV-122b, CWE-120). The body is at most the 20 MB of a floor plan upload (the
/// file-import class); a configuration part is at most 8 MB decoded and a file part at most the floor plan limit; each kind
/// of record has its own bound, the zone profile's, the alert rules' and the displays' from their own services. A site
/// beyond a bound is refused at export, never cut short.
/// </summary>
public static class SiteConfigurationLimits
{
    public const int MaxBundleBytes = (int)FloorPlan.MaxBytes;
    public const int MaxJsonPartBytes = 8 * 1024 * 1024;
    public const int MaxFileBytes = (int)FloorPlan.MaxBytes;
    public const int MaxTerminals = 50;
    public const int MaxLevels = 200;
    public const int MaxCheckpoints = 1_000;
    public const int MaxDesks = 5_000;
    public const int MaxDeskCodeMappings = 10_000;
    public const int MaxFloorPlans = MaxLevels;
    /// <summary>Devices of a site (ARV-122b): the weekly summary's bound on a site's devices.</summary>
    public const int MaxDevices = 2_000;

    /// <summary>Alert rules of a site (ARV-122b): the rule service's own bound (SvcAlertRules.MaxRulesPerSite).</summary>
    public const int MaxAlertRules = 500;

    /// <summary>Displays of a site (ARV-122b): the display service's own bound (SvcDisplays.MaxDisplaysPerSite).</summary>
    public const int MaxDisplays = 200;

    /// <summary>
    /// The highest alert rule number an import takes (ARV-122b, lead's decision 2026-10-11): imported codes are kept and the
    /// rule service numbers a new rule after the highest code ever used, so this leaves the target at least 899,999 codes.
    /// </summary>
    public const int MaxImportedRuleNumber = 100_000;

    public const int MaxZones = ZoneProfile.MaxZones;
    public const int MaxLines = ZoneProfile.MaxLines;
    public const int MaxNameLength = 200;
    /// <summary>A floor plan's file name, in code points (the column is varchar(200)).</summary>
    public const int MaxFileNameLength = FloorPlan.MaxFileNameLength;

    /// <summary>The seven configuration parts and one file part per floor plan.</summary>
    public const int MaxParts = 7 + MaxFloorPlans;

    /// <summary>The longest base64 text of at most <paramref name="bytes"/> bytes (padded): checked before any decoding.</summary>
    public static int Base64Length(int bytes) => checked(4 * ((bytes + 2) / 3));
}
