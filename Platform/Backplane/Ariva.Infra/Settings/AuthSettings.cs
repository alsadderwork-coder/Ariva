using Microsoft.Extensions.Configuration;

namespace Ariva.Infra.Settings;

/// <summary>The "Auth" section (ADR-0026, ARV-010a and ARV-010b).</summary>
public sealed class AuthSettings
{
    public const string SectionName = "Auth";

    public TokenSettings Tokens { get; init; } = new();
    public LockoutSettings Lockout { get; init; } = new();
    public SessionSettings Sessions { get; init; } = new();
    public TotpSettings Totp { get; init; } = new();

    /// <summary>
    /// Whether an account without TOTP is limited to the pending scope (ARV-010c). On in every committed file; a unit
    /// test keeps it on. An enrolled account always needs its code, whatever this says.
    /// </summary>
    public bool TotpRequired { get; init; } = true;

    /// <summary>Extra words a password may not contain, besides the username and "ariva" (for example the site code).</summary>
    public List<string> ContextWords { get; init; } = [];

    /// <summary>vm-local only: accounts created at startup for development and the E2E suite.</summary>
    public List<DevelopmentUserSettings> DevelopmentUsers { get; init; } = [];

    public static AuthSettings From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.GetSection(SectionName).Get<AuthSettings>() ?? new AuthSettings();
    }
}

public sealed class TokenSettings
{
    public string Issuer { get; init; } = "ariva";
    public string Audience { get; init; } = "ariva-users";
    public int LifetimeMinutes { get; init; } = 15;
    public int ClockSkewSeconds { get; init; } = 30;

    /// <summary>PEM EC P-256 private key. Only Ariva.Api.Main has it (mounted from the ariva-token-signing secret).</summary>
    public string SigningKeyPath { get; init; } = string.Empty;

    /// <summary>PEM public keys every host accepts: the current key first, then previous keys during a rotation.</summary>
    public List<string> PublicKeyPaths { get; init; } = [];

    /// <summary>vm-local: a key pair generated once in the user profile and shared by the local hosts.</summary>
    public bool UseDevelopmentKeys { get; init; }

    public string DevelopmentKeyDirectory { get; init; } = string.Empty;
}

/// <summary>TOTP (ARV-010c). The algorithm parameters are fixed in <see cref="Security.Totp"/>.</summary>
public sealed class TotpSettings
{
    /// <summary>The issuer shown in authenticator apps; add the site, for example "Ariva AUH", so accounts are told apart.</summary>
    public string Issuer { get; init; } = "Ariva";

    /// <summary>Steps accepted either side of the current one (clock drift between the phone and the server).</summary>
    public int SkewSteps { get; init; } = 1;

    /// <summary>The break-glass account's username; created only by the installer command.</summary>
    public string BreakGlassUserName { get; init; } = "break-glass";
}

/// <summary>Server-side session lifetimes (ARV-010b). Seconds, so tests and the E2E run can shorten them.</summary>
public sealed class SessionSettings
{
    /// <summary>12 hours from sign-in for every role, whatever the activity.</summary>
    public int AbsoluteSeconds { get; init; } = 12 * 3600;

    /// <summary>30 minutes without a refresh for SystemAdministrator, and for accounts without an operational role.</summary>
    public int IdleSecondsAdministrator { get; init; } = 30 * 60;

    /// <summary>4 hours without a refresh for the operational roles (desk and duty staff on long shifts).</summary>
    public int IdleSecondsOperational { get; init; } = 4 * 3600;

    /// <summary>How long a used refresh token still returns its successor once (two tabs refreshing together).</summary>
    public int RefreshGraceSeconds { get; init; } = 30;

    /// <summary>
    /// How long a node trusts its memory copy of a session. Revocation removes the copy everywhere through the Redis
    /// backplane at once; without one it reaches every node within this time. 4 seconds keeps the 5 second promise
    /// including the request that notices it.
    /// </summary>
    public int CacheSeconds { get; init; } = 4;
}

public sealed class LockoutSettings
{
    public int Threshold { get; init; } = 10;
    public int DurationSeconds { get; init; } = 900;
}

public sealed class DevelopmentUserSettings
{
    public string UserName { get; init; }
    public string Password { get; init; }
    public List<string> Roles { get; init; } = [];

    /// <summary>Creates the account with a temporary password (pending scope).</summary>
    public bool Temporary { get; init; }

    /// <summary>Base32 TOTP secret: the account is created with TOTP enrolled, so the E2E suite can compute codes.</summary>
    public string TotpSecret { get; init; }

    /// <summary>Site codes the account may access ("*" for every site); missing sites are created (ARV-012).</summary>
    public List<string> Sites { get; init; } = [];
}
