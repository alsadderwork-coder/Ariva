using System.Text.RegularExpressions;
using Ariva.Core.Domain.Components;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.Events;

namespace Ariva.Core.Domain.Entities;

/// <summary>
/// A people-sensing device (ARV-021): its family, model, transport and dialect, where it hangs (level, position,
/// mounting height, orientation) and what it covers (the coverage footprint), the one queue zone that owns its events,
/// its clock source, its lifecycle state and the hash of its credential. A device starts in
/// <see cref="DeviceState.Commissioning"/> and becomes <see cref="DeviceState.Online"/> only after a passed
/// calibration; moving it sends it back to commissioning. Retired is final and revokes the credential.
/// </summary>
public partial class Device : BaseSoftDeletableEntity<Device>, ISiteBound
{
    public const double MinMountingHeightMetres = 2;
    public const double MaxMountingHeightMetres = 20;
    public const int ModelLength = 100;
    public const int QueueZoneNameLength = 200;

    /// <summary>Why a passing calibration is refused while the owning zone is not live.</summary>
    public const string ZoneNotPublishedMessage =
        "The device's queue zone is not in the site's published zone profile, on its level and within its footprint; publish the zone (or move the device) before it goes online.";

    protected Device()
    {
    }

    public Device(string code, DeviceFamily family, string model, DeviceTransport transport, DeviceDialect dialect, ClockSource clockSource,
        Level level, double x, double y, double mountingHeightMetres, double orientationDegrees, CoverageFootprint footprint, string queueZoneName,
        DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(level);
        if (level.IsDeleted)
            throw new InvalidOperationException("The level is deleted.");
        Id = NewId();
        Code = TopologyCodes.Require(code, nameof(code));
        Family = Defined(family, nameof(family));
        SiteCode = level.SiteCode;
        State = DeviceState.Commissioning;
        UpdateDetails(model, transport, dialect, clockSource);
        Place(level, x, y, mountingHeightMetres, orientationDegrees, footprint, queueZoneName);
        Raise("Registered", utcNow);
    }

    public virtual string Code { get; protected set; }
    public virtual string SiteCode { get; protected set; }
    public virtual DeviceFamily Family { get; protected set; }
    public virtual string Model { get; protected set; }
    public virtual DeviceTransport Transport { get; protected set; }
    public virtual DeviceDialect Dialect { get; protected set; }
    public virtual ClockSource ClockSource { get; protected set; }
    public virtual DeviceState State { get; protected set; }

    public virtual Level Level { get; protected set; }

    /// <summary>Position of the sensor on the level, in the level's floor coordinates (metres).</summary>
    public virtual double X { get; protected set; }

    public virtual double Y { get; protected set; }
    public virtual double MountingHeightMetres { get; protected set; }

    /// <summary>Direction of the footprint's length, degrees counter-clockwise from the level's x axis (0 to 359.9).</summary>
    public virtual double OrientationDegrees { get; protected set; }

    public virtual double? FootprintLengthMetres { get; protected set; }
    public virtual double? FootprintWidthMetres { get; protected set; }
    public virtual double? FootprintRadiusMetres { get; protected set; }
    public virtual FootprintSource FootprintSource { get; protected set; }

    /// <summary>
    /// The name of the queue zone that owns this device's events, as named in the site's zone profiles. A name, not a
    /// zone id: every profile version copies its zones with new ids but keeps their names, so the Kafka key of a
    /// device's events stays the same across versions (ADR-0019 keys sensor events by the owning zone).
    /// </summary>
    public virtual string QueueZoneName { get; protected set; }

    /// <summary>The first characters of the credential, stored in clear to find the device for a presented key (ARV-022).</summary>
    public virtual string CredentialPrefix { get; protected set; }

    /// <summary>SHA-256 (hex) of the whole credential; the credential itself is shown once and never stored.</summary>
    public virtual string CredentialHash { get; protected set; }

    public virtual DateTime? CredentialIssuedOn { get; protected set; }
    public virtual DateTime? RetiredOn { get; protected set; }

    /// <summary>
    /// Source networks the device may push from (ARV-022), as CIDR blocks separated by commas, for example
    /// "10.20.0.0/24,10.20.1.17/32"; empty means any address. Checked against the client address after the trusted
    /// proxies (Security:ForwardedHeaders).
    /// </summary>
    [System.ComponentModel.DataAnnotations.MaxLength(AllowedSourcesLength)]
    public virtual string AllowedSources { get; protected set; }

    /// <summary>
    /// SHA-256 (hex) of the client certificate the device must present (ARV-022), or null when the credential alone
    /// authenticates it. Pins one certificate, so a stolen credential alone is not enough.
    /// </summary>
    public virtual string ClientCertificateSha256 { get; protected set; }

    public const int MaxAllowedSources = 16;
    public const int AllowedSourcesLength = 1_000;

    /// <summary>The allowed source networks, parsed; empty when any address may push.</summary>
    public virtual IReadOnlyList<System.Net.IPNetwork> AllowedNetworks =>
        string.IsNullOrEmpty(AllowedSources) ? [] : [.. AllowedSources.Split(',').Select(c => System.Net.IPNetwork.Parse(c))];

    /// <summary>
    /// Sets where the device may push from and the client certificate it must present (ARV-022). Every block is
    /// checked before anything changes; a host bit set below the prefix (10.0.0.1/24) is refused as a likely typo.
    /// </summary>
    public virtual void SetAccess(IReadOnlyList<string> allowedSources, string clientCertificateSha256, DateTime utcNow)
    {
        EnsureNotRetired();
        var blocks = (allowedSources ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        if (blocks.Count > MaxAllowedSources)
            throw new ArgumentException($"At most {MaxAllowedSources} source networks.", nameof(allowedSources));
        var normalised = new List<string>();
        foreach (var block in blocks)
        {
            if (!System.Net.IPNetwork.TryParse(block, out var network) || !block.Contains('/', StringComparison.Ordinal))
                throw new ArgumentException($"'{(block.Length > 50 ? block[..50] : block)}' is not a network in CIDR form, for example 10.20.0.0/24.", nameof(allowedSources));
            if (!System.Net.IPAddress.TryParse(block[..block.IndexOf('/', StringComparison.Ordinal)], out var given) || !given.Equals(network.BaseAddress))
                throw new ArgumentException($"'{block}' has host bits set; the network is {network}.", nameof(allowedSources));
            if (network.PrefixLength == 0)
                throw new ArgumentException("A /0 network allows every address; leave the list empty instead.", nameof(allowedSources));
            normalised.Add(network.ToString());
        }

        string pin = null;
        if (!string.IsNullOrWhiteSpace(clientCertificateSha256))
        {
            pin = clientCertificateSha256.Trim().Replace(":", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
            if (!Sha256Hex().IsMatch(pin))
                throw new ArgumentException("A certificate fingerprint is its SHA-256: 64 hex characters, colons allowed.", nameof(clientCertificateSha256));
        }

        var sources = normalised.Count == 0 ? null : string.Join(',', normalised.Distinct(StringComparer.Ordinal));
        if (sources == AllowedSources && pin == ClientCertificateSha256)
            return;
        AllowedSources = sources;
        ClientCertificateSha256 = pin;
        Raise("AccessChanged", utcNow);
    }

    public virtual IList<DeviceCalibration> Calibrations { get; protected set; } = [];

    public virtual CoverageFootprint Footprint =>
        new(FootprintLengthMetres, FootprintWidthMetres, FootprintRadiusMetres, FootprintSource,
            FootprintSource == FootprintSource.Vendor ? "From the vendor's footprint table" : CoverageFootprint.Assumed(Family, MountingHeightMetres).Note);

    /// <summary>The footprint on the floor, for drawing and for the coverage check.</summary>
    public virtual IReadOnlyList<FloorPoint> FootprintRing => Footprint.Ring(new FloorPoint(X, Y), OrientationDegrees);

    /// <summary>True once a calibration has passed and the device has not been moved since.</summary>
    public virtual bool IsCommissioned => State is DeviceState.Online or DeviceState.Degraded or DeviceState.Offline;

    /// <summary>
    /// Model, transport, dialect and clock source. A calibration was measured with one model sending through one
    /// transport and mapped by one dialect, so changing any of those sends a commissioned device back to
    /// <see cref="DeviceState.Commissioning"/>; a clock source change does not. Returns true when it went back.
    /// </summary>
    public virtual bool UpdateDetails(string model, DeviceTransport transport, DeviceDialect dialect, ClockSource clockSource, DateTime? utcNow = null)
    {
        EnsureNotRetired();
        // Everything is checked before anything changes, so a refused update leaves the device as it was.
        var checkedModel = DisplayText.Require(model, ModelLength, nameof(model));
        var (t, d, c) = (Defined(transport, nameof(transport)), Defined(dialect, nameof(dialect)), Defined(clockSource, nameof(clockSource)));
        var measuredWithChanged = Model is not null && (!string.Equals(Model, checkedModel, StringComparison.Ordinal) || Transport != t || Dialect != d);
        var changed = measuredWithChanged || ClockSource != c;
        (Transport, Dialect, ClockSource, Model) = (t, d, c, checkedModel);
        if (Model is null || !changed || utcNow is null)
            return false;

        var recommission = measuredWithChanged && State != DeviceState.Commissioning;
        if (measuredWithChanged)
            State = DeviceState.Commissioning;
        Raise("Updated", utcNow.Value);
        return recommission;
    }

    /// <summary>
    /// Moves, re-aims or reassigns the sensor. Anything that changes what it sees or whose events they are sends it back
    /// to <see cref="DeviceState.Commissioning"/> until a new calibration passes (wiki/11 "Replace or move").
    /// Returns true when the device went back to commissioning.
    /// </summary>
    public virtual bool Move(Level level, double x, double y, double mountingHeightMetres, double orientationDegrees, CoverageFootprint footprint, string queueZoneName, DateTime utcNow)
    {
        EnsureNotRetired();
        ArgumentNullException.ThrowIfNull(level);
        if (level.SiteCode != SiteCode)
            throw new InvalidOperationException("A device stays in its site.");
        if (level.IsDeleted)
            throw new InvalidOperationException("The level is deleted.");

        var before = (Level?.Id, X, Y, MountingHeightMetres, OrientationDegrees, FootprintLengthMetres, FootprintWidthMetres, FootprintRadiusMetres, FootprintSource, QueueZoneName);
        Place(level, x, y, mountingHeightMetres, orientationDegrees, footprint, queueZoneName);
        var after = (Level?.Id, X, Y, MountingHeightMetres, OrientationDegrees, FootprintLengthMetres, FootprintWidthMetres, FootprintRadiusMetres, FootprintSource, QueueZoneName);
        if (before.Equals(after))
            return false;

        var recommission = State != DeviceState.Commissioning;
        State = DeviceState.Commissioning;
        Raise("Moved", utcNow);
        return recommission;
    }

    /// <summary>Stores a new credential's prefix and hash, replacing the previous one at once (the old key stops working).</summary>
    public virtual void IssueCredential(string prefix, string hash, DateTime utcNow)
    {
        EnsureNotRetired();
        if (prefix is null || !CredentialPrefixPattern().IsMatch(prefix))
            throw new ArgumentException("Not a device credential prefix.", nameof(prefix));
        if (hash is null || !Sha256Hex().IsMatch(hash))
            throw new ArgumentException("A credential hash is 64 lower case hex characters.", nameof(hash));
        var rotated = CredentialHash is not null;
        CredentialPrefix = prefix;
        CredentialHash = hash;
        CredentialIssuedOn = Utc(utcNow);
        if (rotated)
            Raise("CredentialRotated", utcNow);
    }

    /// <summary>
    /// Records a calibration. A pass makes the device <see cref="DeviceState.Online"/> (it counts and covers its zone);
    /// a failure takes it out of service until a calibration passes. <paramref name="zonePublished"/> says whether the
    /// owning queue zone is in the site's published profile: a device cannot go online for a zone that is not live.
    /// </summary>
    public virtual DeviceCalibration RecordCalibration(CalibrationMethod method, int sampleSize, double countingAccuracyPercent, double waitTimeErrorMinutes,
        double thresholdPercent, string notes, bool zonePublished, DateTime utcNow)
    {
        EnsureNotRetired();
        var calibration = new DeviceCalibration(this, method, sampleSize, countingAccuracyPercent, waitTimeErrorMinutes, thresholdPercent, notes, utcNow);
        if (calibration.Passed && !zonePublished)
            throw new InvalidOperationException(ZoneNotPublishedMessage);
        Calibrations.Add(calibration);
        State = calibration.Passed ? DeviceState.Online : DeviceState.Commissioning;
        Raise(calibration.Passed ? "CalibrationPassed" : "CalibrationFailed", utcNow);
        return calibration;
    }

    /// <summary>Health (ARV-025) moves a commissioned device between Online, Degraded and Offline; nothing else.</summary>
    public virtual void SetHealth(DeviceState state, DateTime utcNow)
    {
        if (state is not (DeviceState.Online or DeviceState.Degraded or DeviceState.Offline))
            throw new ArgumentOutOfRangeException(nameof(state), state, "Health sets Online, Degraded or Offline.");
        if (!IsCommissioned)
            throw new InvalidOperationException("Only a commissioned device changes with its health; a passed calibration comes first.");
        if (State == state)
            return;
        State = state;
        Raise("Health" + state, utcNow);
    }

    /// <summary>Removes a device registered by mistake: only one that was never calibrated (otherwise retire it).</summary>
    public virtual void Remove(string removedBy, DateTime utcNow)
    {
        EnsureNotRetired();
        if (State != DeviceState.Commissioning || Calibrations.Count > 0)
            throw new InvalidOperationException("Only a device that was never calibrated can be removed; retire it instead.");
        CredentialHash = null;
        SoftDelete(removedBy, Utc(utcNow));
        Raise("Removed", utcNow);
    }

    /// <summary>Takes the device out of use for good; its credential stops working and its history stays.</summary>
    public virtual void Retire(DateTime utcNow)
    {
        EnsureNotRetired();
        State = DeviceState.Retired;
        RetiredOn = Utc(utcNow);
        CredentialHash = null;
        Raise("Retired", utcNow);
    }

    private void Place(Level level, double x, double y, double mountingHeightMetres, double orientationDegrees, CoverageFootprint footprint, string queueZoneName)
    {
        ArgumentNullException.ThrowIfNull(footprint);
        if (!double.IsFinite(x) || !double.IsFinite(y) || !level.Contains(x, y))
            throw new ArgumentOutOfRangeException(nameof(x), "The device's position lies inside its level.");
        if (!double.IsFinite(mountingHeightMetres) || mountingHeightMetres < MinMountingHeightMetres || mountingHeightMetres > MaxMountingHeightMetres)
            throw new ArgumentOutOfRangeException(nameof(mountingHeightMetres), mountingHeightMetres,
                $"The mounting height is from {MinMountingHeightMetres} to {MaxMountingHeightMetres} m.");
        if (!double.IsFinite(orientationDegrees) || orientationDegrees < 0 || orientationDegrees >= 360)
            throw new ArgumentOutOfRangeException(nameof(orientationDegrees), orientationDegrees, "The orientation is from 0 up to 360 degrees.");
        if (footprint.IsCircle != (Family == DeviceFamily.Lidar))
            throw new ArgumentException(Family == DeviceFamily.Lidar ? "A LiDAR footprint is a radius." : "This device's footprint is a length and a width.", nameof(footprint));
        var source = Defined(footprint.Source, nameof(footprint));
        var zoneName = DisplayText.Require(queueZoneName, QueueZoneNameLength, nameof(queueZoneName));

        Level = level;
        X = Math.Round(x, 3);
        Y = Math.Round(y, 3);
        MountingHeightMetres = Math.Round(mountingHeightMetres, 2);
        OrientationDegrees = Math.Round(orientationDegrees, 1) % 360;
        FootprintLengthMetres = footprint.LengthMetres;
        FootprintWidthMetres = footprint.WidthMetres;
        FootprintRadiusMetres = footprint.RadiusMetres;
        FootprintSource = source;
        QueueZoneName = zoneName;
    }

    private void Raise(string change, DateTime utcNow) =>
        RaiseDomainEvent(new DeviceRegistryChanged
        {
            DeviceId = Id.GetValueOrDefault(),
            SiteCode = SiteCode,
            Code = Code,
            Change = change,
            State = State.ToString(),
            QueueZoneName = QueueZoneName,
            OccurredOn = Utc(utcNow)
        });

    private void EnsureNotRetired()
    {
        if (State == DeviceState.Retired)
            throw new InvalidOperationException("The device is retired; register a new one instead.");
        if (IsDeleted)
            throw new InvalidOperationException("The device is deleted.");
    }

    private static TEnum Defined<TEnum>(TEnum value, string paramName) where TEnum : struct, Enum =>
        Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(paramName, value, $"Unknown {typeof(TEnum).Name}.");

    internal static DateTime Utc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : throw new ArgumentException("Times are UTC.", nameof(value));

    [GeneratedRegex("^ardk_[A-Za-z0-9_-]{8}\\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex CredentialPrefixPattern();

    [GeneratedRegex("^[0-9a-f]{64}\\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Sha256Hex();
}

/// <summary>
/// One calibration of a device (wiki/07 section 6): how it was measured, the sample, the counting accuracy and
/// wait-time error found, and the pass threshold (95 percent by default). Never changed or deleted.
/// </summary>
public class DeviceCalibration : BaseAuditableEntity<DeviceCalibration>, ISiteBound
{
    public const double DefaultThresholdPercent = 95;
    public const int MinSampleSize = 50;
    public const int MaxSampleSize = 5_000;
    public const int NotesLength = 1_000;

    protected DeviceCalibration()
    {
    }

    internal DeviceCalibration(Device device, CalibrationMethod method, int sampleSize, double countingAccuracyPercent, double waitTimeErrorMinutes,
        double thresholdPercent, string notes, DateTime utcNow)
    {
        if (!Enum.IsDefined(method))
            throw new ArgumentOutOfRangeException(nameof(method), method, "Unknown calibration method.");
        if (sampleSize is < MinSampleSize or > MaxSampleSize)
            throw new ArgumentOutOfRangeException(nameof(sampleSize), sampleSize, $"The sample is {MinSampleSize} to {MaxSampleSize} passengers.");
        if (!double.IsFinite(countingAccuracyPercent) || countingAccuracyPercent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(countingAccuracyPercent), countingAccuracyPercent, "Counting accuracy is from 0 to 100 percent.");
        if (!double.IsFinite(waitTimeErrorMinutes) || waitTimeErrorMinutes is < 0 or > 30)
            throw new ArgumentOutOfRangeException(nameof(waitTimeErrorMinutes), waitTimeErrorMinutes, "The wait-time error is from 0 to 30 minutes.");
        if (!double.IsFinite(thresholdPercent) || thresholdPercent is < 50 or > 100)
            throw new ArgumentOutOfRangeException(nameof(thresholdPercent), thresholdPercent, "The pass threshold is from 50 to 100 percent.");

        Id = NewId();
        Device = device;
        SiteCode = device.SiteCode;
        Method = method;
        SampleSize = sampleSize;
        CountingAccuracyPercent = Math.Round(countingAccuracyPercent, 2);
        WaitTimeErrorMinutes = Math.Round(waitTimeErrorMinutes, 2);
        ThresholdPercent = Math.Round(thresholdPercent, 2);
        Passed = CountingAccuracyPercent >= ThresholdPercent;
        Notes = DisplayText.Optional(notes, NotesLength, nameof(notes), allowLineBreaks: true);
        PerformedOn = Device.Utc(utcNow);
    }

    public virtual Device Device { get; protected set; }
    public virtual string SiteCode { get; protected set; }
    public virtual CalibrationMethod Method { get; protected set; }
    public virtual int SampleSize { get; protected set; }
    public virtual double CountingAccuracyPercent { get; protected set; }
    public virtual double WaitTimeErrorMinutes { get; protected set; }
    public virtual double ThresholdPercent { get; protected set; }
    public virtual bool Passed { get; protected set; }

    [System.ComponentModel.DataAnnotations.MaxLength(NotesLength)]
    public virtual string Notes { get; protected set; }

    public virtual DateTime PerformedOn { get; protected set; }
}
