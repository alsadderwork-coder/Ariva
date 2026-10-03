using System.Diagnostics.Metrics;
using System.Net;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Integration;
using Ariva.Core.Security;
using Ariva.Core.Services.Integration;
using Ariva.Infra.Integration;
using Ariva.Infra.Security;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Foundation;
using Ariva.Infra.Settings;
using Microsoft.AspNetCore.DataProtection;
using NHibernate.Linq;

namespace Ariva.Infra.Services.Integration;

/// <summary>
/// Integration client authentication (ARV-042, docs/architecture/integration.md). The token exchange checks, and every
/// failure answers the same <c>invalid_client</c>: the caller is inside the client's networks (checked first: from outside
/// nothing is counted), at most 5 attempts a minute per client (counted in the database, across replicas), the client
/// is active and not locked, the secret matches (PBKDF2 in constant time, also run for an unknown client so timing does
/// not tell), and the TOTP code is valid for a step later than the last accepted one, with the client unchanged since it
/// was read (one UPDATE, CWE-294). Ten consecutive failures lock the client for 15 minutes, logged, counted and audited
/// (the administrator alarm), then the count starts again. Each call of the API is checked again: the client active, the
/// token of the client's current version (every change bumps it), the caller inside its networks, a fresh TOTP code when
/// its policy says so.
/// </summary>
internal sealed class SvcIntegrationAuth(
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    IDataProtectionProvider dataProtection,
    IntegrationTokenIssuer issuer,
    AuthSettings authSettings,
    AuditTrail audit,
    IntegrationMetrics metrics,
    ILogger<SvcIntegrationAuth> logger) : SvcBase(unitOfWork, currentUser, timeProvider), ISvcIntegrationAuth
{
    // Spent on unknown clients and malformed secrets, so a failure takes as long whatever failed.
    private static readonly PasswordHashValue Dummy = PasswordHasher.Hash("ics_not-a-real-secret-only-for-equal-timing-000");

    private readonly IDataProtector _totpSecrets = dataProtection.CreateProtector(IntegrationCredentials.SeedProtectionPurpose);

    public async Task<Result<IntegrationTokenViewModel>> ExchangeAsync(IntegrationTokenRequest request, IPAddress remote, CancellationToken ct = default)
    {
        var now = UtcNow;
        // A malformed id cannot exist (its shape is public): refused at once, nothing counted, no PBKDF2 spent.
        if (!IntegrationClient.IsClientId(request?.ClientId))
        {
            metrics.Exchange("refused");
            logger.LogWarning("Integration token refused: malformed client id");
            return Result.Error<IntegrationTokenViewModel>(IntegrationErrors.InvalidClient);
        }

        var clientId = request.ClientId;
        var client = await Query<IntegrationClient>().Where(c => c.ClientId == clientId).FirstOrDefaultAsync(ct);
        var secretWellFormed = IntegrationCredentials.IsWellFormed(request.ClientSecret);
        // The secret is always checked, so every failure costs the same PBKDF2 work whatever failed.
        var secretMatches = PasswordHasher.Verify(secretWellFormed ? request.ClientSecret : "x", client?.Secret ?? Dummy, out _) && client is not null && secretWellFormed;

        // Outside the client's networks nothing is counted: someone who knows only a client id cannot exhaust its
        // attempts or lock it (CWE-645). A client without networks accepts any address, so give every client networks.
        if (client is not null && client.Networks.Count > 0 && !SourceNetworks.Contains(client.Networks, remote))
            return Refused(client, "address outside the allowed networks");
        if (client is null)
            return Refused(null, "unknown client");

        var counted = await ExecuteCommandAsync<CountRow>("""
            UPDATE integration_client
               SET attempts_in_window = CASE WHEN attempt_window_utc > :windowStart THEN attempts_in_window + 1 ELSE 1 END,
                   attempt_window_utc = CASE WHEN attempt_window_utc > :windowStart THEN attempt_window_utc ELSE :now END
             WHERE id = :id
            RETURNING attempts_in_window AS "Value"
            """, new Dictionary<string, object> { ["id"] = client.Id!.Value, ["windowStart"] = now.AddMinutes(-1), ["now"] = now }, ct);
        if (counted.Count == 1 && counted[0].Value > authSettings.IntegrationAttemptsPerMinute)
            return Refused(client, "too many attempts in a minute");
        if (client.Status == IntegrationClientStatus.Disabled)
            return Refused(client, "disabled");
        if (client.IsLocked(now))
            return Refused(client, "locked");

        string reason = null;
        if (!secretMatches)
            reason = "wrong secret";
        else if (Totp.Match(Seed(client), request.TotpCode, now, client.TotpLastStep, authSettings.Totp.SkewSteps) is not { } step)
            reason = "wrong or reused TOTP code";
        else
        {
            // One UPDATE: a later step than the last accepted (replay guard, CWE-294) and the client unchanged since it
            // was read (an administrator's change in between makes this exchange fail rather than outlive the change).
            var accepted = await ExecuteCommandAsync<CountRow>("""
                UPDATE integration_client SET totp_last_step = :step, failed_attempts = 0, last_token_utc = :now
                 WHERE id = :id AND (totp_last_step IS NULL OR totp_last_step < :step) AND token_version = :version AND status = 'Active'
                   AND (locked_until_utc IS NULL OR locked_until_utc <= :now)
                RETURNING 1 AS "Value"
                """, new Dictionary<string, object> { ["step"] = step, ["id"] = client.Id!.Value, ["now"] = now, ["version"] = client.TokenVersion }, ct);
            if (accepted.Count == 0)
                reason = "wrong or reused TOTP code, or the client changed meanwhile";
        }

        if (reason is not null)
        {
            await CountFailureAsync(client, now, ct);
            return Refused(client, reason);
        }

        var sessionId = Guid.NewGuid();
        var (token, expires) = issuer.Issue(client.ClientId, sessionId, client.TokenVersion, client.Scopes, client.Sites);
        metrics.Exchange("issued");
        logger.LogInformation("Integration client {ClientId} got a token (session {SessionId})", client.ClientId, sessionId);
        return new Result<IntegrationTokenViewModel>(new IntegrationTokenViewModel(token, expires, sessionId));
    }

    public async Task<IntegrationCallCheck> CheckCallAsync(string clientId, int tokenVersion, IPAddress remote, string totpCode, CancellationToken ct = default)
    {
        if (!IntegrationClient.IsClientId(clientId))
            return Check(clientId, "malformed client id");
        var client = await Query<IntegrationClient>().Where(c => c.ClientId == clientId).FirstOrDefaultAsync(ct);
        // A lock stops new tokens, not the ones a client already holds: a lock someone else caused must not stop its feed.
        if (client is null)
            return Check(clientId, "unknown client");
        if (client.Status != IntegrationClientStatus.Active)
            return Check(clientId, "disabled");
        if (tokenVersion != client.TokenVersion)
            return Check(clientId, "token issued before the client's last change");
        if (client.Networks.Count > 0 && !SourceNetworks.Contains(client.Networks, remote))
            return Check(clientId, "address outside the allowed networks");
        if (client.RequireTotpPerRequest && Totp.Match(Seed(client), totpCode, UtcNow, lastStep: null, authSettings.Totp.SkewSteps) is null)
            return Check(clientId, "missing or wrong X-TOTP-Code");
        return new IntegrationCallCheck(new IntegrationCallerViewModel(client.ClientId, client.Name, client.Scopes, client.Sites, client.RequireTotpPerRequest), null);
    }

    private IntegrationCallCheck Check(string clientId, string reason)
    {
        logger.LogWarning("Integration call refused for {ClientId}: {Reason}", IntegrationClient.IsClientId(clientId) ? clientId : "(malformed)", reason);
        return new IntegrationCallCheck(null, reason);
    }

    public async Task RecordCallAsync(IntegrationCallRecord call, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        await ExecuteCommandAsync<CountRow>("""
            INSERT INTO integration_call (id, client_id, session_id, scope, method, route, site_code, status, payload_sha256, payload_bytes, remote_address, at_utc)
            VALUES (:id, :clientId, NULLIF(:sessionId, CAST('00000000-0000-0000-0000-000000000000' AS uuid)), NULLIF(:scope, ''), :method, :route, NULLIF(:site, ''), :status,
                    NULLIF(:sha, ''), :bytes, NULLIF(:remote, ''), :at)
            RETURNING 1 AS "Value"
            """, new Dictionary<string, object>
        {
            ["id"] = Guid.NewGuid(),
            ["clientId"] = call.ClientId,
            // Typed values only (the session cannot guess a type for null); the empty value stands for none.
            ["sessionId"] = call.SessionId ?? Guid.Empty,
            ["scope"] = call.Scope ?? string.Empty,
            ["method"] = call.Method,
            ["route"] = call.Route,
            ["site"] = call.SiteCode ?? string.Empty,
            ["status"] = call.Status,
            ["sha"] = call.PayloadSha256 ?? string.Empty,
            ["bytes"] = call.PayloadBytes,
            ["remote"] = call.Remote?.ToString() ?? string.Empty,
            ["at"] = call.AtUtc
        }, ct);
    }

    private Result<IntegrationTokenViewModel> Refused(IntegrationClient client, string reason)
    {
        metrics.Exchange("refused");
        if (client is null)
            logger.LogWarning("Integration token refused: {Reason}", reason);
        else
            logger.LogWarning("Integration token refused for {ClientId}: {Reason}", client.ClientId, reason);
        return Result.Error<IntegrationTokenViewModel>(IntegrationErrors.InvalidClient);
    }

    // A wrong secret or code from inside the client's networks. At the threshold the client is locked and the count starts
    // again, so a lock that expires is not re-armed by a single failure; every lock raises the administrator alarm.
    private async Task CountFailureAsync(IntegrationClient client, DateTime now, CancellationToken ct)
    {
        var threshold = authSettings.Lockout.Threshold;
        var until = now.AddSeconds(authSettings.Lockout.DurationSeconds);
        var rows = await ExecuteCommandAsync<LockRow>("""
            UPDATE integration_client
               SET failed_attempts = CASE WHEN failed_attempts + 1 >= :threshold THEN 0 ELSE failed_attempts + 1 END,
                   locked_until_utc = CASE WHEN failed_attempts + 1 >= :threshold THEN :until ELSE locked_until_utc END
             WHERE id = :id AND (locked_until_utc IS NULL OR locked_until_utc <= :now)
            RETURNING failed_attempts AS "Failed", locked_until_utc AS "LockedUntil"
            """, new Dictionary<string, object> { ["threshold"] = threshold, ["until"] = until, ["id"] = client.Id!.Value, ["now"] = now }, ct);
        if (rows.Count == 1 && rows[0].LockedUntil == until)
        {
            metrics.LockedOut();
            logger.LogWarning("Integration client {ClientId} locked until {Until:o} after {Failures} consecutive failures (runbook: integration client locked out)",
                client.ClientId, until, threshold);
            await audit.RecordAsync("IntegrationClient.LockedOut", "IntegrationClient", client.Id, client.ClientId, null,
                System.Text.Json.JsonSerializer.Serialize(new { failures = threshold, until }), ct);
        }
    }

    private byte[] Seed(IntegrationClient client) => Base32.Decode(_totpSecrets.Unprotect(client.TotpSecretProtected));

    private sealed class CountRow
    {
        public int Value { get; set; }
    }

    private sealed class LockRow
    {
        public int Failed { get; set; }
        public DateTime? LockedUntil { get; set; }
    }
}

/// <summary>Integration client metrics (meter <c>Ariva.Integration</c>): token exchanges by outcome, lockouts.</summary>
public sealed class IntegrationMetrics : IDisposable
{
    public const string MeterName = "Ariva.Integration";

    private readonly Meter _meter;
    private readonly Counter<long> _exchanges;
    private readonly Counter<long> _lockouts;

    public IntegrationMetrics(IMeterFactory meterFactory = null)
    {
        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);
        _exchanges = _meter.CreateCounter<long>("ariva.integration.token_exchanges", description: "Integration token exchanges by outcome (issued, refused)");
        _lockouts = _meter.CreateCounter<long>("ariva.integration.lockouts", description: "Integration clients locked after consecutive failures");
    }

    public void Exchange(string outcome) => _exchanges.Add(1, new KeyValuePair<string, object>("outcome", outcome));

    public void LockedOut() => _lockouts.Add(1);

    public void Dispose() => _meter.Dispose();
}
