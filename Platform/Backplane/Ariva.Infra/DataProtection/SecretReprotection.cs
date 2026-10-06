using System.Security.Cryptography;
using Ariva.Infra.Integration;
using Ariva.Infra.Security;
using Ariva.Infra.Services.Foundation;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.Infra.DataProtection;

/// <summary>What one re-protection round did, per kind of secret and in total.</summary>
public sealed record SecretReprotectionRun(int Checked, int Reprotected, int Failed);

/// <summary>
/// Re-protects the long-lived secrets under the current Data Protection key (ARV-080, ASVS V11.2.2): user TOTP seeds,
/// integration client TOTP seeds and outbound endpoint secrets. Data Protection payloads carry the id of the key that
/// protected them, and that key names its algorithm, so a value is rewritten only when Data Protection reports it needs
/// migration (its key is no longer the default key: rolled after 90 days, or older than a change of algorithm).
/// Each rewrite is one compare-and-swap UPDATE on the old value, so a secret replaced meanwhile (a user re-enrolling)
/// is left alone. Refresh token successors and flight previews live minutes and are not re-protected.
/// </summary>
internal sealed class SecretReprotection(IUnitOfWork unitOfWork, IDataProtectionProvider dataProtection, ILogger<SecretReprotection> logger) : SvcDb(unitOfWork)
{
    public async Task<SecretReprotectionRun> RunAsync(CancellationToken ct = default)
    {
        int seen = 0, rewritten = 0, failed = 0;

        void Count((int Checked, int Reprotected, int Failed) part)
        {
            seen += part.Checked;
            rewritten += part.Reprotected;
            failed += part.Failed;
        }

        Count(await ReprotectAsync(
            await ExecuteSqlAsync<SecretRow>("""SELECT id AS "Id", totp_secret_protected AS "Value" FROM "user" WHERE totp_secret_protected IS NOT NULL""", ct: ct),
            Totp.DataProtectionPurpose, "user TOTP seed",
            (row, value) => ExecuteCommandAsync<IdRow>(
                """UPDATE "user" SET totp_secret_protected = :value WHERE id = :id AND totp_secret_protected = :old RETURNING id AS "Id" """,
                Parameters(row, value), ct),
            ct));

        Count(await ReprotectAsync(
            await ExecuteSqlAsync<SecretRow>("""SELECT id AS "Id", totp_secret_protected AS "Value" FROM integration_client""", ct: ct),
            IntegrationCredentials.SeedProtectionPurpose, "integration client TOTP seed",
            (row, value) => ExecuteCommandAsync<IdRow>(
                """UPDATE integration_client SET totp_secret_protected = :value WHERE id = :id AND totp_secret_protected = :old RETURNING id AS "Id" """,
                Parameters(row, value), ct),
            ct));

        Count(await ReprotectAsync(
            await ExecuteSqlAsync<SecretRow>("""SELECT id AS "Id", secret_protected AS "Value" FROM outbound_endpoint""", ct: ct),
            OutboundSecrets.Purpose, "outbound endpoint secret",
            (row, value) => ExecuteCommandAsync<IdRow>(
                """UPDATE outbound_endpoint SET secret_protected = :value WHERE id = :id AND secret_protected = :old RETURNING id AS "Id" """,
                Parameters(row, value), ct),
            ct));

        return new SecretReprotectionRun(seen, rewritten, failed);
    }

    private async Task<(int Checked, int Reprotected, int Failed)> ReprotectAsync(
        List<SecretRow> rows, string purpose, string kind, Func<SecretRow, string, Task<List<IdRow>>> update, CancellationToken ct)
    {
        var protector = (IPersistedDataProtector)dataProtection.CreateProtector(purpose);
        int rewritten = 0, failed = 0;
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            byte[] plaintext = null;
            try
            {
                // Revoked keys are refused (ignoreRevocationErrors false): a secret under a revoked key is re-enrolled, not
                // carried over to a new key.
                plaintext = protector.DangerousUnprotect(WebEncoders.Base64UrlDecode(row.Value), ignoreRevocationErrors: false, out var requiresMigration, out _);
                if (!requiresMigration)
                    continue;

                var replaced = await update(row, WebEncoders.Base64UrlEncode(protector.Protect(plaintext)));
                rewritten += replaced.Count;
            }
            catch (Exception e) when (e is CryptographicException or FormatException)
            {
                // Never the value or the plaintext: only which row could not be read.
                logger.LogWarning(e, "The {Kind} of {Id} could not be re-protected; it stays as it is", kind, row.Id);
                failed++;
            }
            finally
            {
                if (plaintext is not null)
                    CryptographicOperations.ZeroMemory(plaintext);
            }
        }

        return (rows.Count, rewritten, failed);
    }

    private static Dictionary<string, object> Parameters(SecretRow row, string value) =>
        new() { ["value"] = value, ["id"] = row.Id, ["old"] = row.Value };

    private sealed class SecretRow
    {
        public Guid Id { get; set; }

        public string Value { get; set; }
    }

    private sealed class IdRow
    {
        public Guid Id { get; set; }
    }
}

/// <summary>One re-protection round in its own scope and unit of work, for the Cronz job and tests.</summary>
public sealed class SecretReprotectionRound(IServiceScopeFactory scopes)
{
    public async Task<SecretReprotectionRun> RunAsync(CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        try
        {
            var run = await scope.ServiceProvider.GetRequiredService<SecretReprotection>().RunAsync(ct);
            await unitOfWork.EndAsync(ct);
            return run;
        }
        catch
        {
            await unitOfWork.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
