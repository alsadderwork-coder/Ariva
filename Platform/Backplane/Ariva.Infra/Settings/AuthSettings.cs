using Microsoft.Extensions.Configuration;

namespace Ariva.Infra.Settings;

/// <summary>The "Auth" section (ADR-0026, ARV-010a).</summary>
public sealed class AuthSettings
{
    public const string SectionName = "Auth";

    public TokenSettings Tokens { get; init; } = new();
    public LockoutSettings Lockout { get; init; } = new();

    /// <summary>
    /// Whether an account without TOTP is limited to the pending scope. Off until TOTP enrolment exists (ARV-010c
    /// turns it on for every environment); a unit test keeps it from being switched off again after that.
    /// </summary>
    public bool TotpRequired { get; init; }

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
}
