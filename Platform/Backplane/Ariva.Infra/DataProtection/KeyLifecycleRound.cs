using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Ariva.Infra.Monitoring;
using Ariva.Infra.Settings;
using Npgsql;

namespace Ariva.Infra.DataProtection;

/// <summary>
/// What one key ring purge found (ARV-207): the keys it deleted, and those it left: active (unexpired), kept (expired within
/// their retention) and held (past their retention and still in the table, because a stored value needs them, no unexpired key
/// is in the ring, or the run's batches were spent).
/// </summary>
public sealed record KeyRingRetention(int Deleted, int Active, int Kept, int Held);

/// <summary>
/// What one key lifecycle run did (ARV-207): the key ring purge, and when the oldest key or credential of each dated kind still
/// in use was made (<see cref="KeyAgeMetrics"/> kinds), for the key-age gauges; and, since the ARV-211 review (finding N1), the
/// stored values that need a key the hosts refuse (<see cref="KeyRingUpgradeCheck.Reported"/>: those without a row signature only
/// once the upgrade window has closed, a retired signer's always), null when the run had no signer.
/// </summary>
public sealed record KeyLifecycleRun(KeyRingRetention KeyRing, IReadOnlyDictionary<string, DateTime> OldestMadeUtc, KeyRingUpgradeFinding Unsigned = null);

/// <summary>
/// The key lifecycle job's work (ARV-207), run by Ariva.Api.Cronz once a day after the re-protection job:
/// <list type="number">
/// <item>Deletes the expired Data Protection keys past <see cref="KeyLifecycleSettings.ExpiredKeyRetentionDays"/> that no stored
/// value needs, in batches of <see cref="KeyLifecycleSettings.BatchSize"/>, each one call of <c>ariva_data_protection_key_purge</c>
/// (script 0072): the job connects with the hosts' runtime login, which may not delete a key itself (0002), and the database
/// function decides what goes (the floor of 30 days, the stored values that name each key, an unexpired key left in the ring).
/// This class passes only the days and the batch size, as bind parameters of constant statements (CWE-89, CWE-269).</item>
/// <item>Reads when the oldest of each dated credential in use was made (integration client secrets and seeds, device and
/// display credentials, outbound endpoint secrets) and the oldest key in the ring, one constant statement of aggregates: no
/// value, hash or name is read.</item>
/// <item>Since the ARV-211 review (finding N1), after the purge and the re-protection of the night: the stored values that still
/// need a key the hosts refuse, the leftovers of an upgrade or a rotation, counted with <see cref="KeyRingUpgradeCheck"/> (each
/// row's signature checked here) as <see cref="KeyRingUpgradeCheck.Reported"/> gives them (while the upgrade window is open the
/// values it admits are not counted, the review's note I-5) and logged; the job reports the count.</item>
/// </list>
/// Logs carry counts only.
/// </summary>
public sealed class KeyLifecycleRound(DatabaseSettings database, KeyLifecycleSettings settings, ILogger<KeyLifecycleRound> logger, KeyRingSigning signing = null,
    TimeProvider timeProvider = null)
{
    #region Constants

    private const string Purge = "SELECT deleted, active, kept, held FROM ariva_data_protection_key_purge(@days, @limit)";

    // The dates only: the oldest change of each credential still in use, and the creation of the oldest key in the ring, read with
    // the purge's own bounded pattern (UTC only; compared as text, so a planted row cannot make the read fail).
    private const string Oldest = """
        SELECT (SELECT min(c.secret_changed_utc) FROM integration_client c WHERE c.status = 'Active'),
               (SELECT min(c.totp_changed_utc) FROM integration_client c WHERE c.status = 'Active'),
               (SELECT min(d.credential_issued_on) FROM device d WHERE d.credential_hash IS NOT NULL),
               (SELECT min(p.credential_issued_on) FROM display p WHERE p.credential_hash IS NOT NULL AND p.deleted_on IS NULL),
               (SELECT min(o.secret_changed_utc) FROM outbound_endpoint o WHERE o.status = 'Active'),
               (SELECT min(substring(k.xml FROM '^<key id="[0-9a-f-]{36}" version="1"><creationDate>([0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2})(?:\.[0-9]{1,7})?(?:Z|\+00:00)</creationDate>') COLLATE "C")
                  FROM data_protection_key k)
        """;

    #endregion

    #region Methods

    public async Task<KeyLifecycleRun> RunAsync(CancellationToken ct = default)
    {
        await using var connection = PostgresConnections.Create(database.BuildConnectionString());
        await connection.OpenAsync(ct);

        var ring = new KeyRingRetention(0, 0, 0, 0);
        var deleted = 0;
        for (var batch = 0; batch < settings.MaxBatchesPerRun; batch++)
        {
            ring = await PurgeAsync(connection, ct);
            deleted += ring.Deleted;
            if (ring.Deleted < settings.BatchSize)
                break;
        }

        ring = ring with { Deleted = deleted };
        var oldest = await OldestAsync(connection, ct);
        Report(ring);
        var unsigned = signing is null
            ? null
            : KeyRingUpgradeCheck.Reported(await KeyRingUpgradeCheck.FindAsync(connection, signing, ct), signing.CertificateSignedUntil,
                (timeProvider ?? TimeProvider.System).GetUtcNow());
        if (unsigned is { Total: > 0 })
            logger.LogWarning(
                "Key lifecycle: {Needed} still need a key without a row signature, and {Retired} value(s) a key signed by a key ring signer no host has configured; reopen the window or configure the previous signer if SecretReprotection has not run since, otherwise find the rows it named as not re-protected (wiki 10 section 4.25)",
                KeyRingUpgradeCheck.Describe(unsigned), unsigned.RetiredSigner);
        return new KeyLifecycleRun(ring, oldest, unsigned);
    }

    private async Task<KeyRingRetention> PurgeAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = Command(connection, Purge, ("days", settings.ExpiredKeyRetentionDays), ("limit", settings.BatchSize));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return new KeyRingRetention(0, 0, 0, 0);
        return new KeyRingRetention(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
    }

    private static async Task<IReadOnlyDictionary<string, DateTime>> OldestAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        var oldest = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        await using var command = Command(connection, Oldest);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return oldest;

        string[] kinds =
        [
            KeyAgeMetrics.IntegrationClientSecret, KeyAgeMetrics.IntegrationClientSeed, KeyAgeMetrics.DeviceCredential,
            KeyAgeMetrics.DisplayCredential, KeyAgeMetrics.OutboundSecret
        ];
        for (var i = 0; i < kinds.Length; i++)
        {
            if (!reader.IsDBNull(i))
                oldest[kinds[i]] = DateTime.SpecifyKind(reader.GetDateTime(i), DateTimeKind.Utc);
        }

        if (!reader.IsDBNull(kinds.Length) &&
            DateTime.TryParseExact(reader.GetString(kinds.Length), "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var created))
            oldest[KeyAgeMetrics.DataProtectionKey] = created;
        return oldest;
    }

    private void Report(KeyRingRetention ring)
    {
        if (ring.Held > 0)
        {
            logger.LogWarning(
                "Key lifecycle: {Deleted} expired Data Protection keys deleted; {Held} past their retention of {Days} days are kept, because a stored value still needs them (SecretReprotection could not move it), no unexpired key is in the ring, or the run's batches were spent",
                ring.Deleted, ring.Held, settings.ExpiredKeyRetentionDays);
        }
        else if (ring.Deleted > 0)
        {
            logger.LogInformation("Key lifecycle: {Deleted} expired Data Protection keys deleted after {Days} days; {Active} active and {Kept} expired keys kept",
                ring.Deleted, settings.ExpiredKeyRetentionDays, ring.Active, ring.Kept);
        }
        else
        {
            logger.LogDebug("Key lifecycle: no Data Protection key to delete; {Active} active and {Kept} expired keys", ring.Active, ring.Kept);
        }
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, [ConstantExpected] string sql, params (string Name, object Value)[] parameters)
    {
#pragma warning disable CA2100 // every caller passes one of the constants above (ConstantExpected)
        var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return command;
    }

    #endregion
}
