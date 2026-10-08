using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Ariva.Simulation.Api.Emulators.Aman;
using Ariva.Simulation.Api.Emulators.Integration;
using Ariva.Simulation.Api.Emulators.Sensors;

namespace Ariva.Simulation.Api.Emulators;

/// <summary>
/// Simulation:Ariva. Where the emulated AODB, AMAN and immigration system reach Ariva's Integration API (ARV-029) and the
/// Ariva site they write to. Plain HTTP needs <see cref="AllowInsecureTransport"/> and a loopback or in-cluster address,
/// because client secrets and tokens travel in it (CWE-319).
/// </summary>
public sealed class ArivaTargetSettings : IValidatableObject
{
    public const string Section = "Simulation:Ariva";

    public string IntegrationUrl { get; set; }
    public bool AllowInsecureTransport { get; set; }
    public int RequestTimeoutSeconds { get; set; } = 10;

    /// <summary>The Ariva site the feeds write to (the path of every Integration API call).</summary>
    public string SiteCode { get; set; } = "DMO";

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!string.IsNullOrEmpty(IntegrationUrl))
        {
            if (!Uri.TryCreate(IntegrationUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                yield return new ValidationResult("Simulation:Ariva:IntegrationUrl must be an absolute http or https address without credentials, query or fragment.");
            else if (uri.Scheme == "http" && (!AllowInsecureTransport || !SensorEmulatorSettings.IsLocalOrInCluster(uri)))
                yield return new ValidationResult("Simulation:Ariva:IntegrationUrl may use http only with AllowInsecureTransport and a loopback or in-cluster address.");
        }

        if (RequestTimeoutSeconds is < 1 or > 60)
            yield return new ValidationResult("Simulation:Ariva:RequestTimeoutSeconds is from 1 to 60.");
        if (SiteCode is null || !FeedRules.Code().IsMatch(SiteCode))
            yield return new ValidationResult("Simulation:Ariva:SiteCode is an Ariva site code.");
    }
}

/// <summary>Kafka for the AMAN feed topics (<c>aman.feed.*.v1</c>), as AMAN's own cluster would be reached.</summary>
public sealed class AmanKafkaSettings
{
    public string BootstrapServers { get; set; }
    public string SecurityProtocol { get; set; } = "Plaintext";
    public string SaslMechanism { get; set; }
    public string SaslUsername { get; set; }
    public string SaslPassword { get; set; }
    public string SslCaLocation { get; set; }

    public bool IsConfigured => !string.IsNullOrEmpty(BootstrapServers);
}

/// <summary>
/// A client of the mock AMAN Integration API (Ariva's outbound AMAN connector, ARV-050): its id, the SHA-256 of its
/// secret (lower-case hex; never the secret), its Base32 TOTP seed and whether every call needs <c>X-TOTP-Code</c>.
/// </summary>
public sealed class MockAmanClient
{
    public string ClientId { get; set; }
    public string SecretSha256 { get; set; }
    public string TotpSecret { get; set; }
    public bool PerRequestTotp { get; set; } = true;
}

/// <summary>The mock AMAN's auth endpoint and feed API: its clients, token lifetime and exchanges allowed per address per minute.</summary>
public sealed class MockAmanSettings
{
    public List<MockAmanClient> Clients { get; set; } = [];
    public int TokenMinutes { get; set; } = 15;
    public int AuthPerMinute { get; set; } = 30;
}

/// <summary>
/// Simulation:Aman. The emulated AMAN (ARV-029): its site code, the border sides it reports, the Kafka cluster it
/// publishes to, the Ariva integration client it pushes with (scope immigration:write, per-request TOTP as AMAN's
/// clients use), how long its feed API keeps records, and the mock AMAN Integration API's clients.
/// </summary>
public sealed class AmanEmulatorSettings : IValidatableObject
{
    public const string Section = "Simulation:Aman";

    public string SiteCode { get; set; } = "DMO";
    public BorderSides Sides { get; set; } = BorderSides.Both;
    public AmanKafkaSettings Kafka { get; set; } = new();
    public IntegrationClientSettings Client { get; set; } = new();
    public int RetainMinutes { get; set; } = 180;
    public MockAmanSettings Mock { get; set; } = new();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (SiteCode is null || !FeedRules.Code().IsMatch(SiteCode))
            yield return new ValidationResult("Simulation:Aman:SiteCode is AMAN's site code (2 to 17 letters, digits or hyphens).");
        if (Sides is BorderSides.None || (Sides & ~BorderSides.Both) != 0)
            yield return new ValidationResult("Simulation:Aman:Sides is Arrival, Departure or Both.");
        if (Kafka?.SecurityProtocol is null || !Enum.TryParse<Confluent.Kafka.SecurityProtocol>(Kafka.SecurityProtocol, ignoreCase: true, out _))
            yield return new ValidationResult("Simulation:Aman:Kafka:SecurityProtocol is Plaintext, Ssl, SaslPlaintext or SaslSsl.");
        if (!string.IsNullOrEmpty(Kafka?.SaslMechanism) && !Enum.TryParse<Confluent.Kafka.SaslMechanism>(Kafka.SaslMechanism, ignoreCase: true, out _))
            yield return new ValidationResult("Simulation:Aman:Kafka:SaslMechanism is a Kafka SASL mechanism.");
        foreach (var problem in KafkaTransportProblems(Kafka))
            yield return new ValidationResult(problem);
        foreach (var problem in Client?.Problems("Simulation:Aman:Client") ?? [])
            yield return new ValidationResult(problem);
        if (RetainMinutes is < 10 or > 1440)
            yield return new ValidationResult("Simulation:Aman:RetainMinutes is from 10 to 1440.");
        foreach (var problem in MockProblems(Mock))
            yield return new ValidationResult(problem);
    }

    /// <summary>
    /// CWE-319: SASL credentials travel only over TLS (SaslSsl), and an unencrypted protocol (Plaintext, SaslPlaintext)
    /// only to loopback or in-cluster brokers, as for HTTP.
    /// </summary>
    public static IEnumerable<string> KafkaTransportProblems(AmanKafkaSettings kafka)
    {
        if (kafka?.IsConfigured != true || !Enum.TryParse<Confluent.Kafka.SecurityProtocol>(kafka.SecurityProtocol, ignoreCase: true, out var protocol))
            yield break;
        var credentials = !string.IsNullOrEmpty(kafka.SaslUsername) || !string.IsNullOrEmpty(kafka.SaslPassword);
        if (credentials && protocol != Confluent.Kafka.SecurityProtocol.SaslSsl)
            yield return "Simulation:Aman:Kafka: SASL credentials need SecurityProtocol SaslSsl, so they never cross the network in clear.";
        if (protocol is Confluent.Kafka.SecurityProtocol.Plaintext or Confluent.Kafka.SecurityProtocol.SaslPlaintext)
        {
            foreach (var broker in kafka.BootstrapServers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Uri.TryCreate("kafka://" + broker, UriKind.Absolute, out var uri) || !SensorEmulatorSettings.IsLocalOrInCluster(uri))
                {
                    yield return "Simulation:Aman:Kafka: an unencrypted protocol only to loopback or in-cluster brokers; use Ssl or SaslSsl otherwise.";
                    yield break;
                }
            }
        }
    }

    private static IEnumerable<string> MockProblems(MockAmanSettings mock)
    {
        if (mock is null)
            yield break;
        if (mock.TokenMinutes is < 1 or > 60)
            yield return "Simulation:Aman:Mock:TokenMinutes is from 1 to 60.";
        if (mock.AuthPerMinute is < 1 or > 600)
            yield return "Simulation:Aman:Mock:AuthPerMinute is from 1 to 600.";
        if (mock.Clients.Count > 16)
            yield return "At most 16 mock AMAN clients.";
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < mock.Clients.Count; i++)
        {
            var c = mock.Clients[i];
            if (c?.ClientId is null || !FeedRules.ClientId().IsMatch(c.ClientId) || !ids.Add(c.ClientId))
                yield return $"Mock AMAN client {i}: a unique client id of 4 to 64 letters, digits, dots, dashes or underscores.";
            if (c?.SecretSha256 is null || !FeedRules.Digest().IsMatch(c.SecretSha256))
                yield return $"Mock AMAN client {i}: the SHA-256 of the secret as 64 lower-case hex characters.";
            if (Totp.FromBase32(c?.TotpSecret) is null)
                yield return $"Mock AMAN client {i}: a Base32 TOTP seed of at least 80 bits.";
        }
    }
}

/// <summary>
/// Simulation:Immigration. A mock immigration system other than AMAN (ARV-029): it sends the same V1 contracts through
/// Ariva's generic REST endpoints with its own client, for the border sides it covers (departures by default).
/// </summary>
public sealed class ImmigrationEmulatorSettings : IValidatableObject
{
    public const string Section = "Simulation:Immigration";

    public string SiteCode { get; set; } = "DMO";
    public BorderSides Sides { get; set; } = BorderSides.Departure;
    public IntegrationClientSettings Client { get; set; } = new();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (SiteCode is null || !FeedRules.Code().IsMatch(SiteCode))
            yield return new ValidationResult("Simulation:Immigration:SiteCode is the system's site code.");
        if (Sides is BorderSides.None || (Sides & ~BorderSides.Both) != 0)
            yield return new ValidationResult("Simulation:Immigration:Sides is Arrival, Departure or Both.");
        foreach (var problem in Client?.Problems("Simulation:Immigration:Client") ?? [])
            yield return new ValidationResult(problem);
    }
}

/// <summary>
/// The emulated AODB of a scenario site other than the reference (ARV-139b, Simulation:Aodb:Sites:{site}): the Ariva
/// integration client it pushes AIDX with (scope flights:write on that site). Its airport and Ariva site are the scenario
/// site's (AUH-TA: airport AUH, site AUH-TA); without a client it keeps its ACRIS snapshot and pushes nothing.
/// </summary>
public sealed class AodbSiteSettings
{
    public IntegrationClientSettings Client { get; set; } = new();
}

/// <summary>
/// Simulation:Aodb. The emulated AODB (ARV-029): the reference site's airport (IATA), the Ariva integration client it
/// pushes AIDX with (scope flights:write), and the API key Ariva's ACRIS pull presents (its header and SHA-256; never the
/// key); ARV-139b: <see cref="Sites"/> holds the client of each other scenario site's AODB (AUH-TA).
/// </summary>
public sealed class AodbEmulatorSettings : IValidatableObject
{
    public const string Section = "Simulation:Aodb";

    public string Airport { get; set; } = "DMO";
    public IntegrationClientSettings Client { get; set; } = new();
    public string AcrisHeader { get; set; } = "X-Api-Key";
    public string AcrisKeySha256 { get; set; }

    /// <summary>The AODBs of the other scenario sites, by Ariva site code (AUH-TA).</summary>
    public Dictionary<string, AodbSiteSettings> Sites { get; set; } = new(StringComparer.Ordinal);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Airport is null || !FeedRules.Airport().IsMatch(Airport))
            yield return new ValidationResult("Simulation:Aodb:Airport is the site airport's IATA code.");
        foreach (var problem in Client?.Problems("Simulation:Aodb:Client") ?? [])
            yield return new ValidationResult(problem);
        foreach (var (site, aodb) in Sites ?? [])
        {
            // A site's code is named only once it is known to be one of the simulator's (never a configured value as such).
            if (!Scenarios.ScenarioEngine.HasSite(site) || site == Scenarios.ScenarioEngine.ReferenceSite)
            {
                yield return new ValidationResult("Simulation:Aodb:Sites holds only scenario sites other than the reference (AUH-TA).");
                continue;
            }

            foreach (var problem in aodb?.Client?.Problems($"Simulation:Aodb:Sites:{site}:Client") ?? [])
                yield return new ValidationResult(problem);
        }
        if (AcrisHeader is null || !FeedRules.Header().IsMatch(AcrisHeader) ||
            AcrisHeader.ToUpperInvariant() is "AUTHORIZATION" or "COOKIE" or "HOST" or "CONTENT-LENGTH" or "TRANSFER-ENCODING")
            yield return new ValidationResult("Simulation:Aodb:AcrisHeader is a header name other than a transport or credential header.");
        if (!string.IsNullOrEmpty(AcrisKeySha256) && !FeedRules.Digest().IsMatch(AcrisKeySha256))
            yield return new ValidationResult("Simulation:Aodb:AcrisKeySha256 is the SHA-256 of the key as 64 lower-case hex characters.");
    }
}

internal static partial class FeedRules
{
    [GeneratedRegex("^[A-Z0-9]{2,8}(-[A-Z0-9]{1,8})?\\z")]
    public static partial Regex Code();

    [GeneratedRegex("^[A-Z]{3}\\z")]
    public static partial Regex Airport();

    [GeneratedRegex("^[A-Za-z0-9._-]{4,64}\\z")]
    public static partial Regex ClientId();

    [GeneratedRegex("^[0-9a-f]{64}\\z")]
    public static partial Regex Digest();

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}\\z")]
    public static partial Regex Header();
}
