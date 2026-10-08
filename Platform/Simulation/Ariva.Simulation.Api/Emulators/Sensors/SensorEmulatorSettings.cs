using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Ariva.Simulation.Api.Scenarios;
using Ariva.Simulation.Api.Scenarios.Engine;

namespace Ariva.Simulation.Api.Emulators.Sensors;

/// <summary>
/// One emulated device: the scenario site and sensor it plays (ARV-139b: DMO, the default, with S-01 to S-59; AUH-TA
/// with the seed's sensor codes such as Q-VIS-01), the dialect it speaks and the device credential Ariva issued when the
/// device was registered or its credential rotated (ARV-021). The credential is a secret: it comes from the simulation
/// appsettings secret or from the devices endpoint, is held in memory only and is never returned or logged.
/// </summary>
public sealed class EmulatedDeviceSettings
{
    /// <summary>The scenario site of the sensor (an Ariva site code the simulator plays); DMO when not given.</summary>
    public string Site { get; set; }

    public string Sensor { get; set; }
    public EmulatedDialect Dialect { get; set; } = EmulatedDialect.Canonical;
    public string Credential { get; set; }
}

/// <summary>
/// Simulation:Sensors. The sensor emulator (ARV-028): where Ingest is, how fast the demo day may run and the devices it
/// plays. Plain HTTP to Ingest needs <see cref="AllowInsecureTransport"/>, because device credentials travel in the
/// Authorization header (CWE-319); set it only for vm-local or an in-cluster service address.
/// </summary>
public sealed partial class SensorEmulatorSettings : IValidatableObject
{
    public const string Section = "Simulation:Sensors";

    /// <summary>Every sensor of both scenario sites fits (DMO 59, AUH-TA 84); bounded all the same (CWE-400).</summary>
    public const int MaxDevices = 150;

    /// <summary>Ingest's base address, for example https://ingest.example or http://api-ingest-service.</summary>
    public string IngestUrl { get; set; }

    public bool AllowInsecureTransport { get; set; }

    /// <summary>Demo minutes per wall minute, at most.</summary>
    public double MaxSpeed { get; set; } = 60;

    public int TickMilliseconds { get; set; } = 250;

    public int RequestTimeoutSeconds { get; set; } = 10;

    /// <summary>Pushes in flight at once.</summary>
    public int Concurrency { get; set; } = 8;

    public List<EmulatedDeviceSettings> Devices { get; set; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!string.IsNullOrEmpty(IngestUrl))
        {
            if (!Uri.TryCreate(IngestUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                yield return new ValidationResult("Simulation:Sensors:IngestUrl must be an absolute http or https address without credentials, query or fragment.");
            else if (uri.Scheme == "http" && !AllowInsecureTransport)
                yield return new ValidationResult("Simulation:Sensors:IngestUrl uses http: set AllowInsecureTransport only for vm-local or an in-cluster address.");
            else if (uri.Scheme == "http" && !IsLocalOrInCluster(uri))
                yield return new ValidationResult("Simulation:Sensors:IngestUrl may use http only for a loopback address or an in-cluster service name.");
        }

        if (!double.IsFinite(MaxSpeed) || MaxSpeed is < 1 or > 120)
            yield return new ValidationResult("Simulation:Sensors:MaxSpeed is from 1 to 120.");
        if (TickMilliseconds is < 50 or > 5000)
            yield return new ValidationResult("Simulation:Sensors:TickMilliseconds is from 50 to 5000.");
        if (RequestTimeoutSeconds is < 1 or > 60)
            yield return new ValidationResult("Simulation:Sensors:RequestTimeoutSeconds is from 1 to 60.");
        if (Concurrency is < 1 or > 32)
            yield return new ValidationResult("Simulation:Sensors:Concurrency is from 1 to 32.");
        foreach (var problem in DeviceProblems(Devices))
            yield return new ValidationResult(problem);
    }

    /// <summary>Loopback, or a Kubernetes service name: a single label, or one ending in .svc or .svc.cluster.local.</summary>
    public static bool IsLocalOrInCluster(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return uri.IsLoopback || (uri.HostNameType == UriHostNameType.Dns &&
                                  (!uri.Host.Contains('.', StringComparison.Ordinal) ||
                                   uri.Host.EndsWith(".svc", StringComparison.OrdinalIgnoreCase) ||
                                   uri.Host.EndsWith(".svc.cluster.local", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>What is wrong with a device list, never quoting a credential.</summary>
    public static IEnumerable<string> DeviceProblems(IReadOnlyList<EmulatedDeviceSettings> devices)
    {
        if (devices is null)
            yield break;
        if (devices.Count > MaxDevices)
            yield return $"At most {MaxDevices} emulated devices.";
        var sensors = new HashSet<string>(StringComparer.Ordinal);
        var credentials = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < devices.Count; i++)
        {
            var d = devices[i];
            // CWE-501: the site and sensor are looked up, never echoed unless they are known codes.
            var site = d?.Site ?? ScenarioEngine.ReferenceSite;
            if (!ScenarioEngine.HasSite(site))
                yield return $"Device {i}: the site is a scenario site the simulator plays ({ScenarioSites.Codes}).";
            else if (d?.Sensor is null || SensorTraffic.Sensor(site, d.Sensor) is null)
                yield return $"Device {i}: the sensor must be one of the scenario sensors of site {site} (DMO: S-01 to S-59; AUH-TA: the seed's sensor codes).";
            else if (!sensors.Add(site + "/" + d.Sensor))
                yield return $"Device {i}: sensor {d.Sensor} of site {site} is listed twice.";
            if (d is not null && !Enum.IsDefined(d.Dialect))
                yield return $"Device {i}: the dialect is Canonical or Xovis.";
            if (d?.Credential is null || !CredentialPattern().IsMatch(d.Credential))
                yield return $"Device {i}: the credential must be a device credential issued by Ariva (ardk_ and 43 characters).";
            else if (!credentials.Add(d.Credential))
                yield return $"Device {i}: two devices share a credential.";
        }
    }

    [GeneratedRegex("^ardk_[A-Za-z0-9_-]{43}\\z")]
    private static partial Regex CredentialPattern();
}
