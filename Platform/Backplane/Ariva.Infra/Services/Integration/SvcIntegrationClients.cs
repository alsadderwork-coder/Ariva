using System.Text.Json;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
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
/// Integration clients for administrators (ARV-042). A client is visible only when the caller's sites cover all of its
/// sites (404 otherwise, CWE-863), and can be bound only to sites the caller holds (CWE-269). The secret is hashed with
/// PBKDF2 and the TOTP seed protected with the Data Protection key ring before anything is stored; both are returned
/// once. Every change is audited as JSON without secrets.
/// </summary>
internal sealed class SvcIntegrationClients(
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    ISiteScope siteScope,
    AuditTrail audit,
    IDataProtectionProvider dataProtection,
    AuthSettings authSettings,
    ILogger<SvcIntegrationClients> logger) : SvcBase(unitOfWork, currentUser, timeProvider), ISvcIntegrationClients
{
    private const int MaxListed = 500;
    private readonly IDataProtector _totpSecrets = dataProtection.CreateProtector(IntegrationCredentials.SeedProtectionPurpose);

    public async Task<Result<IReadOnlyList<IntegrationClientViewModel>>> ListAsync(string siteCode, CancellationToken ct = default)
    {
        var access = await siteScope.GetAsync(ct);
        var clients = await Query<IntegrationClient>().OrderBy(c => c.Name).Take(MaxListed * 4).ToListAsync(ct);
        var visible = clients.Where(c => Covers(access, c) && (string.IsNullOrWhiteSpace(siteCode) || c.Serves(siteCode))).Take(MaxListed).Select(View).ToList();
        return new Result<IReadOnlyList<IntegrationClientViewModel>>(visible);
    }

    public async Task<Result<IntegrationClientViewModel>> GetAsync(Guid id, CancellationToken ct = default) =>
        await VisibleAsync(id, ct, forChange: false) is { } client ? new Result<IntegrationClientViewModel>(View(client)) : Result.Error<IntegrationClientViewModel>(IntegrationErrors.NotFound);

    public async Task<Result<IntegrationClientCredentialsViewModel>> CreateAsync(CreateIntegrationClientRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Kind is null || !Enum.GetNames<IntegrationClientKind>().Contains(request.Kind, StringComparer.Ordinal))
            return Result.Error<IntegrationClientCredentialsViewModel>(IntegrationErrors.InvalidKind);
        var kind = Enum.Parse<IntegrationClientKind>(request.Kind);
        var (access, errors) = await AccessAsync(request.Scopes, request.SiteCodes, request.AllowedNetworks, request.RequireTotpPerRequest ?? kind == IntegrationClientKind.Immigration, ct);
        if (errors.Count > 0)
            return Result.Error<IntegrationClientCredentialsViewModel>([.. errors]);
        if (!IsName(request.Name))
            return Result.Error<IntegrationClientCredentialsViewModel>("A name is 1 to 100 characters of plain text.");

        var now = UtcNow;
        var secret = IntegrationCredentials.NewSecret();
        var seed = Totp.NewSecret();
        var client = new IntegrationClient(IntegrationCredentials.NewClientId(), request.Name, kind, access, PasswordHasher.Hash(secret),
            _totpSecrets.Protect(Base32.Encode(seed)), now);
        await SaveAsync(client, ct);
        await audit.RecordAsync("IntegrationClient.Created", "IntegrationClient", client.Id, client.ClientId, null, Summary(client), ct);
        logger.LogInformation("Integration client {ClientId} created by {AdministratorId}", client.ClientId, CurrentUser.Id);
        return new Result<IntegrationClientCredentialsViewModel>(Credentials(client, secret, seed));
    }

    public async Task<Result<IntegrationClientViewModel>> UpdateAsync(Guid id, UpdateIntegrationClientRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var client = await VisibleAsync(id, ct);
        if (client is null)
            return Result.Error<IntegrationClientViewModel>(IntegrationErrors.NotFound);
        var (access, errors) = await AccessAsync(request.Scopes, request.SiteCodes, request.AllowedNetworks, request.RequireTotpPerRequest ?? false, ct);
        if (errors.Count > 0)
            return Result.Error<IntegrationClientViewModel>([.. errors]);
        if (!IsName(request.Name))
            return Result.Error<IntegrationClientViewModel>("A name is 1 to 100 characters of plain text.");

        var before = Summary(client);
        client.Rename(request.Name);
        client.Apply(access, UtcNow);
        var after = Summary(client);
        if (after != before)
        {
            await UpdateAsync(client, ct);
            await audit.RecordAsync("IntegrationClient.Updated", "IntegrationClient", client.Id, client.ClientId, before, after, ct);
        }

        return new Result<IntegrationClientViewModel>(View(client));
    }

    public async Task<Result<IntegrationClientCredentialsViewModel>> RotateSecretAsync(Guid id, CancellationToken ct = default)
    {
        var client = await VisibleAsync(id, ct);
        if (client is null)
            return Result.Error<IntegrationClientCredentialsViewModel>(IntegrationErrors.NotFound);
        var secret = IntegrationCredentials.NewSecret();
        client.SetSecret(PasswordHasher.Hash(secret), UtcNow);
        await UpdateAsync(client, ct);
        await audit.RecordAsync("IntegrationClient.SecretRotated", "IntegrationClient", client.Id, client.ClientId, null, null, ct);
        return new Result<IntegrationClientCredentialsViewModel>(Credentials(client, secret, null));
    }

    public async Task<Result<IntegrationClientCredentialsViewModel>> ResetTotpAsync(Guid id, CancellationToken ct = default)
    {
        var client = await VisibleAsync(id, ct);
        if (client is null)
            return Result.Error<IntegrationClientCredentialsViewModel>(IntegrationErrors.NotFound);
        var seed = Totp.NewSecret();
        client.SetTotp(_totpSecrets.Protect(Base32.Encode(seed)), UtcNow);
        await UpdateAsync(client, ct);
        await audit.RecordAsync("IntegrationClient.TotpReset", "IntegrationClient", client.Id, client.ClientId, null, null, ct);
        return new Result<IntegrationClientCredentialsViewModel>(Credentials(client, null, seed));
    }

    public Task<Result<IntegrationClientViewModel>> DisableAsync(Guid id, CancellationToken ct = default) =>
        ChangeAsync(id, "IntegrationClient.Disabled", c => c.Disable(UtcNow), ct);

    public Task<Result<IntegrationClientViewModel>> EnableAsync(Guid id, CancellationToken ct = default) =>
        ChangeAsync(id, "IntegrationClient.Enabled", c => c.Enable(), ct);

    public Task<Result<IntegrationClientViewModel>> UnlockAsync(Guid id, CancellationToken ct = default) =>
        ChangeAsync(id, "IntegrationClient.Unlocked", c => c.Unlock(), ct);

    private async Task<Result<IntegrationClientViewModel>> ChangeAsync(Guid id, string action, Func<IntegrationClient, bool> change, CancellationToken ct)
    {
        var client = await VisibleAsync(id, ct);
        if (client is null)
            return Result.Error<IntegrationClientViewModel>(IntegrationErrors.NotFound);
        var before = Summary(client);
        if (change(client))
        {
            await UpdateAsync(client, ct);
            await audit.RecordAsync(action, "IntegrationClient", client.Id, client.ClientId, before, Summary(client), ct);
        }

        return new Result<IntegrationClientViewModel>(View(client));
    }

    // Scope first: a site outside the caller's own answers the same whether it exists or not (CWE-204).
    private async Task<(IntegrationClientAccess Access, List<string> Errors)> AccessAsync(IEnumerable<string> scopes, IReadOnlyList<string> sites, IEnumerable<string> networks,
        bool requireTotp, CancellationToken ct)
    {
        var (access, errors) = IntegrationClientAccess.Check(scopes, sites, networks, requireTotp);
        if (errors.Count > 0)
            return (null, [.. errors]);
        var mine = await siteScope.GetAsync(ct);
        if (!mine.Covers(new SiteAccess(false, access.Sites.ToHashSet(StringComparer.Ordinal))))
            return (null, [IntegrationErrors.BeyondOwnSites]);
        var codes = access.Sites.ToList();
        if (await Query<Site>().CountAsync(s => codes.Contains(s.Code), ct) != codes.Count)
            return (null, [IntegrationErrors.UnknownSite]);
        return (access, []);
    }

    /// <summary>
    /// The client when all its sites are the caller's. For a change, the row is locked first (to the end of the
    /// transaction), so two administrators changing one client run one after the other: each reads the token version the
    /// other wrote and moves it on, and a token exchange in between waits and then fails on the new version.
    /// </summary>
    private async Task<IntegrationClient> VisibleAsync(Guid id, CancellationToken ct, bool forChange = true)
    {
        if (forChange)
            await ExecuteCommandAsync<LockRow>("SELECT 1 AS \"Value\" FROM integration_client WHERE id = :id FOR UPDATE", new Dictionary<string, object> { ["id"] = id }, ct);
        var client = await GetAsync<IntegrationClient>(id, ct);
        return client is not null && Covers(await siteScope.GetAsync(ct), client) ? client : null;
    }

    private static bool Covers(SiteAccess access, IntegrationClient client) => client.Sites.All(access.Allows);

    private static bool IsName(string name)
    {
        var trimmed = name?.Trim();
        return !string.IsNullOrEmpty(trimmed) && trimmed.Length <= IntegrationClient.MaxNameLength && Ariva.Core.Domain.Components.DisplayText.IsClean(trimmed);
    }

    private IntegrationClientCredentialsViewModel Credentials(IntegrationClient client, string secret, byte[] seed) =>
        new(View(client), secret, seed is null ? null : Base32.Encode(seed),
            seed is null ? null : Totp.OtpAuthUri(authSettings.Totp.Issuer + " integration", client.ClientId, seed));

    // JSON, so a name cannot pass for another field (CWE-117); never the secret or the seed.
    private static string Summary(IntegrationClient client) => JsonSerializer.Serialize(new
    {
        name = client.Name,
        kind = client.Kind.ToString(),
        status = client.Status.ToString(),
        scopes = client.Scopes,
        sites = client.Sites,
        networks = client.Networks,
        totpPerRequest = client.RequireTotpPerRequest,
        locked = client.LockedUntilUtc is not null
    });

    private IntegrationClientViewModel View(IntegrationClient c) =>
        new(c.Id!.Value, c.ClientId, c.Name, c.Kind.ToString(), c.Status.ToString(), c.Scopes, c.Sites, c.Networks, c.RequireTotpPerRequest, c.IsLocked(UtcNow),
            c.LockedUntilUtc, c.FailedAttempts, c.SecretChangedUtc, c.TotpChangedUtc, c.LastTokenUtc);

    private sealed class LockRow
    {
        public int Value { get; set; }
    }
}
