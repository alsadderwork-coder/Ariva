using System.Globalization;
using System.Text.RegularExpressions;
using Ariva.Infra.Monitoring;
using Ariva.Infra.Settings;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Ariva.Infra.DataProtection;

/// <summary>
/// What <see cref="KeyRingUpgradeCheck"/> found: the stored values, by kind, that need a key without a row signature (written before
/// ARV-211, which the upgrade window admits); those that need a key a signing certificate no host has configured signed
/// (<see cref="RetiredSigner"/>: a signing rotation whose previous signer is gone, which only that signer admits; the hosts' count
/// alone, the migration job cannot tell); and how many name a key a configured signer signed (<see cref="OnSignedKeys"/>).
/// </summary>
public sealed record KeyRingUpgradeFinding(long UserSeeds, long ClientSeeds, long OutboundSecrets, long OnSignedKeys = 0, long RetiredSigner = 0)
{
    /// <summary>The values that need a key without a row signature (from before ARV-211): the upgrade window admits those keys.</summary>
    public long Unsigned => UserSeeds + ClientSeeds + OutboundSecrets;

    /// <summary>The values that need a key no configured signer signed, for either reason.</summary>
    public long Total => Unsigned + RetiredSigner;

    /// <summary>
    /// Whether the installation was ever upgraded to ARV-211: some stored value names a key whose row a configured signer signed (the
    /// re-protection job moved it there, or it was written since). False on an installation that holds only values from before.
    /// </summary>
    public bool Upgraded => OnSignedKeys > 0;
}

/// <summary>
/// The upgrade check of ARV-211 (the review's findings H1 and N1). The key ring's rows written before ARV-211 carry no row signature
/// (script 0073), and the hosts read them only while <c>DataProtection:Signing:CertificateSignedUntil</c> is open. Two cases, told
/// apart by whether any stored value names a key a configured signer signed (<see cref="KeyRingUpgradeFinding.Upgraded"/>):
/// <list type="bullet">
/// <item>Never upgraded: an upgrade, or a restore of a backup from before ARV-211, without the window. Every value would be
/// unreadable at once (users could not sign in with their authenticator, integration clients could not authenticate, outbound calls
/// would lose their secrets), so the migration job (after its scripts, <c>DatabaseMigration</c>, exit code 2) and every host at start
/// (<see cref="KeyRingUpgradeGate"/>) refuse, with <see cref="Problem"/>, pointing at wiki 04 section 6.2: set the window.</item>
/// <item>Upgraded, with leftovers: values the re-protection job could not move (under a revoked or unreadable key, or corrupt), or
/// a row a database writer planted. The window cannot help them once the job has run, so nothing refuses: the hosts and the
/// migration job log <see cref="Leftovers"/>, and the hosts at start and the Cronz key lifecycle job every day report the count
/// (<see cref="KeyAgeMetrics.ProblemsName"/>, outcome <see cref="KeyAgeMetrics.Unsigned"/>, the keyRingUnsigned alert; wiki 10
/// section 4.25).</item>
/// </list>
/// A stored value names its key in its first 28 characters (the Data Protection header: magic 09 F0 C9 F0 and the key id in .NET's
/// byte order), read as script 0072's purge reads it: the same three columns, the same header test, nothing unprotected, constant
/// statements (CWE-89). The hosts verify each row's signature under the signers they hold (<see cref="KeyRingSigning.CheckRow"/>);
/// the migration job holds no signing certificate, so for it a row "signed" is one that carries a row signature in the shape
/// script 0073 holds (a forged one could make it go on; the hosts would still refuse at start).
/// </summary>
public static partial class KeyRingUpgradeCheck
{
    #region Constants

    // The migration job's form: the values that name keys whose every row is unsigned, by kind, and those that name a key with a
    // signed row.
    private const string Count = """
        WITH keys AS (
            SELECT pg_catalog.substr(g, 7, 2) || pg_catalog.substr(g, 5, 2) || pg_catalog.substr(g, 3, 2) || pg_catalog.substr(g, 1, 2)
                   || pg_catalog.substr(g, 11, 2) || pg_catalog.substr(g, 9, 2) || pg_catalog.substr(g, 15, 2) || pg_catalog.substr(g, 13, 2)
                   || pg_catalog.substr(g, 17, 16) AS wire,
                   d.signature IS NOT NULL AS signed
              FROM public.data_protection_key d
             CROSS JOIN LATERAL pg_catalog.regexp_match(d.xml, '^<key id="([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})"') AS m
             CROSS JOIN LATERAL pg_catalog.replace(m[1], '-', '') AS g
             WHERE m IS NOT NULL),
        stored AS (
            SELECT v.kind, pg_catalog.decode(pg_catalog.translate(pg_catalog.left(v.value, 28), '-_', '+/'), 'base64') AS head
              FROM (SELECT 'user' AS kind, u.totp_secret_protected AS value FROM public."user" u
                    UNION ALL SELECT 'client', c.totp_secret_protected FROM public.integration_client c
                    UNION ALL SELECT 'outbound', o.secret_protected FROM public.outbound_endpoint o) v
             WHERE v.value ~ '^[A-Za-z0-9_-]{28}'),
        named AS (
            SELECT s.kind, pg_catalog.encode(pg_catalog.substring(s.head, 5, 16), 'hex') AS wire,
                   EXISTS (SELECT 1 FROM keys k WHERE k.wire = pg_catalog.encode(pg_catalog.substring(s.head, 5, 16), 'hex') AND k.signed) AS on_signed,
                   EXISTS (SELECT 1 FROM keys k WHERE k.wire = pg_catalog.encode(pg_catalog.substring(s.head, 5, 16), 'hex')) AS has_row
              FROM stored s
             WHERE pg_catalog.substring(s.head, 1, 4) = pg_catalog.decode('09f0c9f0', 'hex'))
        SELECT pg_catalog.count(*) FILTER (WHERE n.kind = 'user' AND n.has_row AND NOT n.on_signed),
               pg_catalog.count(*) FILTER (WHERE n.kind = 'client' AND n.has_row AND NOT n.on_signed),
               pg_catalog.count(*) FILTER (WHERE n.kind = 'outbound' AND n.has_row AND NOT n.on_signed),
               pg_catalog.count(*) FILTER (WHERE n.on_signed)
          FROM named n
        """;

    // The hosts' form, step one: how many values of each kind name each key id (in .NET's byte order, hex).
    private const string Named = """
        WITH stored AS (
            SELECT v.kind, pg_catalog.decode(pg_catalog.translate(pg_catalog.left(v.value, 28), '-_', '+/'), 'base64') AS head
              FROM (SELECT 'user' AS kind, u.totp_secret_protected AS value FROM public."user" u
                    UNION ALL SELECT 'client', c.totp_secret_protected FROM public.integration_client c
                    UNION ALL SELECT 'outbound', o.secret_protected FROM public.outbound_endpoint o) v
             WHERE v.value ~ '^[A-Za-z0-9_-]{28}')
        SELECT s.kind, pg_catalog.encode(pg_catalog.substring(s.head, 5, 16), 'hex'), pg_catalog.count(*)
          FROM stored s
         WHERE pg_catalog.substring(s.head, 1, 4) = pg_catalog.decode('09f0c9f0', 'hex')
         GROUP BY 1, 2
        """;

    // Step two: the key rows, each to be checked against the signers the host holds.
    private const string KeyRows = """
        SELECT d.friendly_name, d.xml, d.signature FROM public.data_protection_key d WHERE d.xml ~ '^<key id="[0-9a-f-]{36}"'
        """;

    #endregion

    #region Methods

    /// <summary>The migration job's finding (it holds no signing certificate): a row counts as signed when it carries a row signature.</summary>
    public static async Task<KeyRingUpgradeFinding> FindAsync(string connectionString, CancellationToken ct)
    {
        await using var connection = PostgresConnections.Create(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(Count, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new KeyRingUpgradeFinding(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
    }

    /// <summary>The hosts' finding: a row counts as signed only when its signature verifies under a signer <paramref name="signing"/> holds.</summary>
    public static async Task<KeyRingUpgradeFinding> FindAsync(string connectionString, KeyRingSigning signing, CancellationToken ct)
    {
        await using var connection = PostgresConnections.Create(connectionString);
        await connection.OpenAsync(ct);
        return await FindAsync(connection, signing, ct);
    }

    /// <inheritdoc cref="FindAsync(string, KeyRingSigning, CancellationToken)"/>
    public static async Task<KeyRingUpgradeFinding> FindAsync(NpgsqlConnection connection, KeyRingSigning signing, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(signing);
        var named = new List<(string Kind, Guid KeyId, long Count)>();
        await using (var command = new NpgsqlCommand(Named, connection))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                named.Add((reader.GetString(0), new Guid(Convert.FromHexString(reader.GetString(1))), reader.GetInt64(2)));
        }

        var rows = new List<KeyRingRow>();
        await using (var command = new NpgsqlCommand(KeyRows, connection))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                rows.Add(new KeyRingRow(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return Classify(named, rows, signing);
    }

    /// <summary>
    /// The hosts' rule, apart from the database: a value is on a signed key when a row of its key id verifies under a signer the host
    /// holds (current or previous); on a retired signer's key (the review's note on rotations) when no row verifies and one carries a row
    /// signature that names a signer the host does not hold; and needs a key from before when its key id has rows and neither holds (no
    /// row signature, or one that does not verify under the configured signer it names: a planted row). A value whose key id has no row
    /// at all is not this check's (its key is gone).
    /// </summary>
    public static KeyRingUpgradeFinding Classify(IEnumerable<(string Kind, Guid KeyId, long Count)> named, IEnumerable<KeyRingRow> rows, KeyRingSigning signing)
    {
        ArgumentNullException.ThrowIfNull(named);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(signing);
        var withRow = new HashSet<Guid>();
        var signed = new HashSet<Guid>();
        var retired = new HashSet<Guid>();
        foreach (var row in rows)
        {
            var id = KeyIdOf(row.Xml);
            if (id is not { } keyId)
                continue;
            withRow.Add(keyId);
            if (row.Signature is null)
                continue;
            if (signing.CheckRow(row, out _) != SignerStanding.Refused)
                signed.Add(keyId);
            else if (NamesUnconfiguredSigner(row.Signature, signing))
                retired.Add(keyId);
        }

        long user = 0, client = 0, outbound = 0, onSigned = 0, onRetired = 0;
        foreach (var (kind, keyId, count) in named)
        {
            if (signed.Contains(keyId))
                onSigned += count;
            else if (retired.Contains(keyId))
                onRetired += count;
            else if (withRow.Contains(keyId) && kind == "user")
                user += count;
            else if (withRow.Contains(keyId) && kind == "client")
                client += count;
            else if (withRow.Contains(keyId) && kind == "outbound")
                outbound += count;
        }

        return new KeyRingUpgradeFinding(user, client, outbound, onSigned, onRetired);
    }

    /// <summary>Whether a row signature in script 0073's shape names a signer that is neither current nor previous here.</summary>
    private static bool NamesUnconfiguredSigner(string signature, KeyRingSigning signing)
    {
        var parts = signature.Split(':');
        return parts.Length == 3 && string.Equals(parts[0], KeyRingSigning.Algorithm, StringComparison.Ordinal) && parts[1].Length == 64 &&
               parts[1].All(Uri.IsHexDigit) && signing.Standing(parts[1], out _) == SignerStanding.Refused;
    }

    /// <summary>
    /// Null unless the installation was never upgraded (no stored value names a key a configured signer signed) and the hosts would refuse
    /// a key the values need: one without a row signature while the window is not open, or one a signer no host has configured signed
    /// (which the window never admits). Then what to do for each: set the window (wiki 04 section 6.2, the upgrade), configure the
    /// previous signer again (the signing rotation). Leftovers after an upgrade never refuse.
    /// </summary>
    public static string Problem(KeyRingUpgradeFinding finding, DateTimeOffset? certificateSignedUntil, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(finding);
        var unsigned = finding.Unsigned > 0 && !WindowOpen(certificateSignedUntil, now);
        if (finding.Upgraded || (!unsigned && finding.RetiredSigner == 0))
            return null;

        var steps = new List<string>();
        if (unsigned)
            steps.Add($"{Describe(finding)} need keys written before ARV-211, without a row signature, and DataProtection:Signing:CertificateSignedUntil " +
                      $"{Window(certificateSignedUntil)}: an upgrade, or a restore of a backup from before ARV-211, without its window. Set " +
                      "DataProtection:Signing:CertificateSignedUntil to a date 7 to 90 days ahead in the environment's base appsettings and run the upgrade " +
                      "again; remove it once SecretReprotection has run (wiki 04 section 6.2, upgrade from the shared key).");
        if (finding.RetiredSigner > 0)
            steps.Add(string.Create(CultureInfo.InvariantCulture, $"{finding.RetiredSigner} stored value(s) need keys signed by a key ring signing certificate ") +
                      "no host has configured: a signing rotation whose previous signer was removed, or never configured, before SecretReprotection moved them, " +
                      "or a restore of a backup from before the rotation. The window does not admit these keys: configure the previous signing certificate " +
                      "again (its tls.crt alone, through dataProtectionSigningPreviousSecretName) and roll out again; remove it once SecretReprotection has " +
                      "run (wiki 04 section 6.2, signing rotation).");
        return "No stored value is under a key a configured key ring signer signed, so the hosts would refuse every key the stored values need: the users " +
               "could not sign in with their authenticator, the integration clients could not authenticate and the outbound endpoints would lose their " +
               "secrets. " + string.Join(" ", steps);
    }

    /// <summary>
    /// Null unless the installation was upgraded and values still need keys the hosts refuse (without a row signature outside the window,
    /// or signed by a signer no host has configured): then the warning the hosts and the migration job log, which separates a window or a
    /// previous signer removed too early from leftovers.
    /// </summary>
    public static string Leftovers(KeyRingUpgradeFinding finding, DateTimeOffset? certificateSignedUntil, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(finding);
        var unsigned = finding.Unsigned > 0 && !WindowOpen(certificateSignedUntil, now);
        if (!finding.Upgraded || (!unsigned && finding.RetiredSigner == 0))
            return null;

        var causes = new List<string>();
        if (unsigned)
            causes.Add($"{Describe(finding)} need keys written before ARV-211 and DataProtection:Signing:CertificateSignedUntil {Window(certificateSignedUntil)}: " +
                       "if SecretReprotection has not run successfully since the upgrade, reopen the window and run it (wiki 04 section 6.2).");
        if (finding.RetiredSigner > 0)
            causes.Add(string.Create(CultureInfo.InvariantCulture, $"{finding.RetiredSigner} stored value(s) need keys signed by a key ring signing certificate ") +
                       "no host has configured: if SecretReprotection has not run successfully since the signing rotation, configure the previous signer again " +
                       "(dataProtectionSigningPreviousSecretName) and run it (wiki 04 section 6.2, signing rotation).");
        return string.Create(CultureInfo.InvariantCulture, $"{finding.OnSignedKeys} stored value(s) are under signed keys, so the hosts start; ") +
               string.Join(" ", causes) + " Otherwise these are leftovers: find the rows SecretReprotection named as not re-protected and have each set " +
               "again, and treat a key row a host did not write as a database writer's (wiki 10 section 4.25).";
    }

    /// <summary>The finding in words: counts only.</summary>
    public static string Describe(KeyRingUpgradeFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return string.Create(CultureInfo.InvariantCulture,
            $"{finding.UserSeeds} user TOTP seed(s), {finding.ClientSeeds} integration client seed(s) and {finding.OutboundSecrets} outbound endpoint secret(s)");
    }

    private static bool WindowOpen(DateTimeOffset? certificateSignedUntil, DateTimeOffset now) => certificateSignedUntil is { } until && now < until;

    private static string Window(DateTimeOffset? certificateSignedUntil) => certificateSignedUntil is { } closed
        ? $"closed on {closed.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"
        : "is not set";

    private static Guid? KeyIdOf(string xml)
    {
        var match = KeyIdPrefix().Match(xml ?? string.Empty);
        return match.Success ? Guid.ParseExact(match.Groups["id"].Value, "D") : null;
    }

    [GeneratedRegex(@"\A<key id=""(?<id>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})""", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex KeyIdPrefix();

    #endregion
}

/// <summary>
/// Every host outside vm-local (Database:VerifySchemaOnStartup), after the schema check (the review's findings H1 and N1): refuses to
/// start only in the never-upgraded case of <see cref="KeyRingUpgradeCheck.Problem"/>, verifying each row's signature under the
/// signers it holds; otherwise it starts, warns while the window is open or values still need keys no configured signer signed
/// (<see cref="KeyRingUpgradeCheck.Leftovers"/>), and reports their count for <see cref="StartReportFreshFor"/>
/// (<see cref="KeyAgeMetrics.UpgradeCheckReported"/>). One
/// pass of two reads with the runtime login, each within the database command timeout.
/// </summary>
internal sealed class KeyRingUpgradeGate(DatabaseSettings settings, KeyRingSigning signing, TimeProvider time, ILogger<KeyRingUpgradeGate> logger,
    KeyAgeMetrics metrics = null) : IHostedService
{
    /// <summary>
    /// How long a host's count at start stands: long enough for the keyRingUnsigned alert's window, short enough that a fix shows
    /// within hours (the Cronz key lifecycle job reports the count every day, for a day and two hours).
    /// </summary>
    internal static readonly TimeSpan StartReportFreshFor = TimeSpan.FromHours(2);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var finding = await KeyRingUpgradeCheck.FindAsync(settings.BuildConnectionString(), signing, cancellationToken);
        metrics?.UpgradeCheckReported(finding, StartReportFreshFor);
        var problem = KeyRingUpgradeCheck.Problem(finding, signing.CertificateSignedUntil, now);
        if (problem is not null)
            throw new InvalidOperationException(problem);

        if (signing.ReadsCertificateSigned(now))
        {
            logger.LogWarning(
                "DataProtection:Signing:CertificateSignedUntil is open until {Until}: keys written before ARV-211 are read, only to unprotect, and {Needed} still need them; remove the window once SecretReprotection has run (wiki 04 section 6.2)",
                signing.CertificateSignedUntil?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), KeyRingUpgradeCheck.Describe(finding));
        }

        var leftovers = KeyRingUpgradeCheck.Leftovers(finding, signing.CertificateSignedUntil, now);
        if (leftovers is not null)
            logger.LogWarning("Key ring upgrade check: {Leftovers}", leftovers);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
