using Ariva.Infra.Caching;
using Ariva.Infra.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

namespace Ariva.Api.Common.Hosting;

/// <summary>
/// ARV-083b (CWE-522, CWE-269; ASVS 5.0 V13.3.1, V13.3.2): in a cluster (k8s-dev, k8s-demo, k8s-prd) the shared base settings
/// (<c>appsettings.base.json</c> and <c>appsettings.base.&lt;environment&gt;.json</c>, which the chart mounts in every pod from
/// base-appsettings-secret) carry no credential, so no pod holds another host's: each host's database login, Kafka principal
/// and Redis user come from its own secret (the chart's databaseLogins, kafkaLogins and redisLogins), the migration login's
/// password from the migration job's own (migration.passwordSecret), and any other password from the one pod that needs it.
/// A host or job whose base settings give a credential a value does not start: any setting whose name is a credential's (the
/// names the logs omit, <see cref="SecretText.IsSensitiveName"/>, read for the setting's own name and for its whole path, so
/// <c>Otlp:Headers</c> counts too, and the names of <see cref="CredentialSuffixes"/>), any value that holds a credential by its
/// shape (what <see cref="SecretText.Redact"/> masks: a <c>Password=</c> pair inside any connection string, a bearer token, a
/// JWT, a private key), and a Redis connection string that names a password or a user. The message names the setting and the
/// file, never the value. On a developer machine (vm-local) the base settings may hold the development logins.
/// </summary>
public static class SharedSettings
{
    #region Constants

    /// <summary>The file name of the shared base settings (with or without the environment).</summary>
    public const string BaseFilePrefix = "appsettings.base.";

    /// <summary>
    /// Setting name endings that name a credential beyond the logs' own list (ARV-083b review): any key, token, seed, passphrase
    /// or certificate bundle a setting of that name may hold. Not Credentials (Security:Cors:AllowCredentials is a switch).
    /// </summary>
    private static readonly string[] CredentialSuffixes =
        ["Password", "Secret", "ApiKey", "Token", "PrivateKey", "SigningKey", "HmacKey", "PseudonymKey", "Pwd", "Credential", "Passphrase", "Seed", "PfxBase64"];

    #endregion

    #region Methods

    /// <summary>Stops the host when, in a cluster, its shared base settings give a credential a value (ARV-083b).</summary>
    /// <param name="configuration">The host's configuration, before anything reads it.</param>
    /// <param name="environment">The resolved Ariva environment.</param>
    /// <exception cref="InvalidOperationException">A base settings file carries a credential.</exception>
    public static void EnsureNoCredentials(IConfiguration configuration, string environment)
    {
        var problems = CredentialProblems(configuration, environment);
        if (problems.Count > 0)
            throw new InvalidOperationException(
                "base-appsettings-secret is mounted in every pod, so it carries no credential (ARV-083b): " + string.Join("; ", problems) +
                ". Remove each from it and give it from its own secret (wiki 04, section 6.2).");
    }

    /// <summary>Each credential the shared base settings give a value, as "&lt;file&gt; gives &lt;setting&gt;"; empty outside a cluster.</summary>
    /// <param name="configuration">The host's configuration.</param>
    /// <param name="environment">The resolved Ariva environment.</param>
    public static IReadOnlyList<string> CredentialProblems(IConfiguration configuration, string environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (environment is null || !environment.StartsWith("k8s-", StringComparison.Ordinal) || configuration is not IConfigurationRoot root)
            return [];
        var problems = new List<string>();
        foreach (var json in root.Providers.OfType<JsonConfigurationProvider>())
        {
            var file = Path.GetFileName(json.Source.Path ?? string.Empty);
            if (!file.StartsWith(BaseFilePrefix, StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var key in Keys(json, null))
            {
                if (!json.TryGet(key, out var value) || string.IsNullOrEmpty(value))
                    continue;
                if (IsCredential(key) || HoldsCredential(value) ||
                    (string.Equals(key, "Redis:ConnectionString", StringComparison.OrdinalIgnoreCase) && RedisSettings.NamesCredentials(value)))
                    problems.Add($"{file} gives {key}");
            }
        }

        return problems;
    }

    /// <summary>
    /// Whether <paramref name="key"/> names a credential: its own name or its whole path is one the logs omit
    /// (<see cref="SecretText.IsSensitiveName"/>), or its own name ends in one of <see cref="CredentialSuffixes"/>.
    /// </summary>
    public static bool IsCredential(string key)
    {
        if (string.IsNullOrEmpty(key))
            return false;
        var name = ConfigurationPath.GetSectionKey(key);
        var normalized = SecretText.Normalize(name);
        return SecretText.IsSensitiveName(name) || SecretText.IsSensitiveName(key) ||
               CredentialSuffixes.Any(suffix => normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether a value holds a credential by its shape: the logs' redaction would change it (<see cref="SecretText.Redact"/>).</summary>
    public static bool HoldsCredential(string value) =>
        !string.IsNullOrEmpty(value) && !string.Equals(SecretText.Redact(value), value, StringComparison.Ordinal);

    /// <summary>Every key the provider holds under <paramref name="parent"/>, at every depth.</summary>
    private static IEnumerable<string> Keys(IConfigurationProvider provider, string parent)
    {
        foreach (var child in provider.GetChildKeys([], parent).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var key = parent is null ? child : ConfigurationPath.Combine(parent, child);
            yield return key;
            foreach (var nested in Keys(provider, key))
                yield return nested;
        }
    }

    #endregion
}
