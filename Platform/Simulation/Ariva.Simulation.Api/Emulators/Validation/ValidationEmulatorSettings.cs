using System.Buffers;
using System.ComponentModel.DataAnnotations;
using System.Text;
using Ariva.Simulation.Api.Emulators.Integration;

namespace Ariva.Simulation.Api.Emulators.Validation;

/// <summary>
/// One Ariva account the simulator signs in with as a validation observer (ARV-104i): its user name, password and, when the
/// account has an authenticator, the Base32 TOTP seed the simulator generates its sign-in code from. Ariva issues the account
/// (an administrator grants the Validation observer role and the site); the simulator signs in through Ariva's normal sign-in,
/// never a back door. Secrets: held in memory only, never returned or logged (CWE-287, CWE-532).
/// </summary>
public sealed class ObserverCredentials
{
    public string UserName { get; set; }
    public string Password { get; set; }
    public string TotpSecret { get; set; }

    /// <summary>Whether a TOTP seed is configured: the simulator then sends a code with every sign-in.</summary>
    public bool HasSecondFactor => !string.IsNullOrEmpty(TotpSecret);

    /// <summary>
    /// A user name as Ariva matches it (Ariva.Core's UserNames.Normalize, mirrored because the simulator references no Ariva
    /// layer): white space trimmed, Unicode NFKC, lower case (invariant). "Obs1", "obs1" and the full-width "ｏｂｓ1" are one
    /// account. A name that is not well-formed text (a lone surrogate, which <see cref="Problems"/> refuses) is only trimmed and
    /// lower-cased, since normalising it would throw.
    /// </summary>
    public static string Normalize(string userName)
    {
        if (userName is null)
            return null;
        var trimmed = userName.Trim();
        return (IsWellFormed(trimmed) ? trimmed.Normalize(NormalizationForm.FormKC) : trimmed).ToLowerInvariant();
    }

    /// <summary>Whether the text is well-formed UTF-16 (every surrogate in a pair).</summary>
    private static bool IsWellFormed(string text)
    {
        var rest = text.AsSpan();
        while (!rest.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(rest, out _, out var used) != OperationStatus.Done)
                return false;
            rest = rest[used..];
        }

        return true;
    }

    /// <summary>What is wrong with one observer's credentials, never quoting them.</summary>
    public static IEnumerable<string> Problems(ObserverCredentials observer, string name)
    {
        if (observer is null)
        {
            yield return $"{name}: the observer's user name and password.";
            yield break;
        }

        // Ariva's sign-in takes at most 256 characters of user name and 512 of password (LoginRequest).
        if (string.IsNullOrWhiteSpace(observer.UserName) || observer.UserName.Length > 256 || observer.UserName.Any(char.IsControl) || !IsWellFormed(observer.UserName))
            yield return $"{name}: the user name of an Ariva account (1 to 256 characters of text, no control characters).";
        if (string.IsNullOrEmpty(observer.Password) || observer.Password.Length > 512 || observer.Password.Any(char.IsControl))
            yield return $"{name}: the account's password (1 to 512 characters, no control characters).";
        if (observer.HasSecondFactor && Totp.FromBase32(observer.TotpSecret) is null)
            yield return $"{name}: the TOTP seed is the account's Base32 seed of at least 80 bits.";
    }

    /// <summary>What is wrong with a list of observers (1 to <see cref="ValidationEmulatorSettings.MaxObservers"/>, one account each), never quoting them.</summary>
    public static IEnumerable<string> ListProblems(IReadOnlyList<ObserverCredentials> observers, string name, bool allowEmpty)
    {
        if (observers is null || (observers.Count == 0 && !allowEmpty))
        {
            yield return $"{name}: 1 to {ValidationEmulatorSettings.MaxObservers} observer accounts.";
            yield break;
        }

        if (observers.Count > ValidationEmulatorSettings.MaxObservers)
        {
            yield return $"{name}: at most {ValidationEmulatorSettings.MaxObservers} observer accounts.";
            yield break;
        }

        // One account per observer, as Ariva matches user names (Normalize): two spellings of one name are one account.
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < observers.Count; i++)
        {
            foreach (var problem in Problems(observers[i], $"{name} {i + 1}"))
                yield return problem;
            if (observers[i]?.UserName is { } userName && !names.Add(Normalize(userName)))
                yield return $"{name} {i + 1}: each observer is a different account.";
        }
    }
}

/// <summary>
/// Simulation:Validation. The emulated validation observers (ARV-104i): the Ariva accounts they sign in with (from the
/// simulation appsettings secret, or <c>PUT api/v1/simulation/validation/observers</c>; none by default, never committed) and
/// the bounds of one rehearsal. Ariva.Api.Main's address is <c>Simulation:Ariva:MainUrl</c>.
/// </summary>
public sealed class ValidationEmulatorSettings : IValidatableObject
{
    public const string Section = "Simulation:Validation";

    /// <summary>Observer accounts at most (a campaign rarely has more than a few observers per shift).</summary>
    public const int MaxObservers = 8;

    /// <summary>The longest window of the scenario day one rehearsal plays, in minutes (16 bins).</summary>
    public const int MaxWindowMinutes = 240;

    /// <summary>The longest window the truth endpoint answers, in minutes (a rehearsal's window with the waits after it).</summary>
    public const int MaxTruthMinutes = 360;

    /// <summary>Most calls to Ariva one rehearsal may plan (counts, tracer batches, desk batches), so a request stays bounded (CWE-400).</summary>
    public const int MaxCalls = 2000;

    public List<ObserverCredentials> Observers { get; set; } = [];

    /// <summary>The longest a rehearsal may take before it stops, in seconds.</summary>
    public int RehearsalTimeoutSeconds { get; set; } = 180;

    /// <summary>Rehearsals one operator key may start per minute (one runs at a time in any case).</summary>
    public int RehearsalsPerMinute { get; set; } = 10;

    /// <summary>Truth reads (<c>GET api/v1/simulation/validation/truth</c>) one operator key may make per minute.</summary>
    public int TruthReadsPerMinute { get; set; } = 60;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var problem in ObserverCredentials.ListProblems(Observers, "Simulation:Validation:Observers", allowEmpty: true))
            yield return new ValidationResult(problem);
        if (RehearsalTimeoutSeconds is < 10 or > 900)
            yield return new ValidationResult("Simulation:Validation:RehearsalTimeoutSeconds is from 10 to 900.");
        if (RehearsalsPerMinute is < 1 or > 60)
            yield return new ValidationResult("Simulation:Validation:RehearsalsPerMinute is from 1 to 60.");
        if (TruthReadsPerMinute is < 1 or > 600)
            yield return new ValidationResult("Simulation:Validation:TruthReadsPerMinute is from 1 to 600.");
    }
}
