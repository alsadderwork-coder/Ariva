using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Ariva.Simulation.Api.Security;

/// <summary>
/// One simulator operator key: a name for the audit log, the SHA-256 of the key (lower-case hex; the key itself is
/// never configured) and its scopes, <c>read</c> and or <c>control</c>.
/// </summary>
public sealed class SimulationKey
{
    public string Name { get; set; }
    public string Sha256 { get; set; }
    public List<string> Scopes { get; set; } = [];
}

/// <summary>
/// Simulation:Control. Operator keys for the scenario endpoints (ARV-027). Supplied by the simulation-appsettings
/// secret per environment; none by default, so nothing authenticates until an operator key is provisioned.
/// </summary>
public sealed partial class SimulationControlSettings : IValidatableObject
{
    public const string Section = "Simulation:Control";

    /// <summary>The operator keys (at most 16).</summary>
    public List<SimulationKey> Keys { get; set; } = [];

    /// <summary>Scenario re-runs allowed per key per minute (a re-run recomputes the whole day).</summary>
    public int RerunsPerMinute { get; set; } = 6;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Keys.Count > 16)
            yield return new ValidationResult("At most 16 simulator operator keys.");
        if (RerunsPerMinute is < 1 or > 60)
            yield return new ValidationResult("RerunsPerMinute must be between 1 and 60.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        var digests = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in Keys)
        {
            if (key is null || key.Name is null || !NamePattern().IsMatch(key.Name) || !names.Add(key.Name))
                yield return new ValidationResult("Each simulator key needs a unique name of 1 to 64 letters, digits, dots, dashes or underscores.");
            if (key?.Sha256 is null || !DigestPattern().IsMatch(key.Sha256))
                yield return new ValidationResult("Each simulator key needs the SHA-256 of the key as 64 lower-case hex characters.");
            else if (!digests.Add(key.Sha256))
                yield return new ValidationResult("Two simulator keys share a digest; the audit log could not tell them apart.");
            if (key?.Scopes is null || key.Scopes.Count == 0 || key.Scopes.Any(s => s is not (SimulationScopes.Read or SimulationScopes.Control)))
                yield return new ValidationResult("Each simulator key needs scopes read and or control.");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex DigestPattern();
}

/// <summary>Scopes and policies of the simulator's operator keys.</summary>
public static class SimulationScopes
{
    public const string Claim = "scope";
    public const string Read = "read";
    public const string Control = "control";

    /// <summary>Reads the simulated day: read or control scope.</summary>
    public const string ReadPolicy = "simulation.read";

    /// <summary>Changes the simulated day: control scope.</summary>
    public const string ControlPolicy = "simulation.control";

    /// <summary>Rate limit of scenario re-runs, per key.</summary>
    public const string RerunLimit = "simulation.rerun";

    /// <summary>Rate limit of minutes played at once on the feed emulators, per key (ARV-029): 60 a minute.</summary>
    public const string PlayLimit = "simulation.play";
}

/// <summary>
/// The simulator's bearer scheme (CWE-306): <c>Authorization: Bearer &lt;operator key&gt;</c>. The key is hashed with
/// SHA-256 and compared in constant time with every configured digest (CWE-208); a match signs in as the key's name with
/// its scopes. No header means no result (the fallback policy then challenges with 401); a wrong key fails without
/// saying why. Keys are never logged.
/// </summary>
internal sealed class SimulationKeyHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptionsMonitor<SimulationControlSettings> settings) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private const string Prefix = "Bearer ";
    private const int MaxKeyLength = 256;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AuthenticateResult.NoResult());
        var presented = header[Prefix.Length..].Trim();
        if (presented.Length is 0 or > MaxKeyLength)
            return Task.FromResult(AuthenticateResult.Fail("Invalid credential."));

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        SimulationKey match = null;
        foreach (var key in settings.CurrentValue.Keys)
        {
            // Every configured digest is compared, whatever matched first, so timing does not tell which key is close.
            var expected = Convert.FromHexString(key.Sha256);
            if (CryptographicOperations.FixedTimeEquals(digest, expected) && match is null)
                match = key;
        }

        if (match is null)
            return Task.FromResult(AuthenticateResult.Fail("Invalid credential."));

        var claims = new List<Claim> { new(ClaimTypes.Name, match.Name) };
        claims.AddRange(match.Scopes.Distinct(StringComparer.Ordinal).Select(s => new Claim(SimulationScopes.Claim, s)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}
