using System.Text.Json;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Integration;
using Ariva.Core.Security;
using Ariva.Core.Services.Integration;
using Ariva.Infra.Integration;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Foundation;
using NHibernate.Linq;

namespace Ariva.Infra.Services.Integration;

/// <summary>
/// Outbound endpoints for administrators (ARV-045). Visible only when the caller's sites cover all of the endpoint's
/// (404 otherwise, CWE-863); bound only to sites the caller holds (CWE-269). The connection is checked by
/// <see cref="OutboundRules"/> (CWE-918: HTTPS, networks, paths on the same origin) and the secret by its kind, then
/// protected; neither the secret nor a certificate is ever returned. Every change is audited as JSON without secrets.
/// </summary>
internal sealed class SvcOutboundEndpoints(
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    ISiteScope siteScope,
    AuditTrail audit,
    OutboundSecrets secrets,
    OutboundSettings settings,
    ILogger<SvcOutboundEndpoints> logger) : SvcBase(unitOfWork, currentUser, timeProvider), ISvcOutboundEndpoints
{
    private const int MaxListed = 500;

    /// <summary>Moving an endpoint (URL, networks, pinned CA, token path) without its secret would send the stored secret to the new place.</summary>
    public const string SecretAgain = "Changing the base URL, networks, pinned CA or token path needs the secret again, in secret.";

    public async Task<Result<IReadOnlyList<OutboundEndpointViewModel>>> ListAsync(string siteCode, CancellationToken ct = default)
    {
        var access = await siteScope.GetAsync(ct);
        var endpoints = await Query<OutboundEndpoint>().OrderBy(e => e.Code).Take(MaxListed * 4).ToListAsync(ct);
        var visible = endpoints.Where(e => Covers(access, e) && (string.IsNullOrWhiteSpace(siteCode) || e.Serves(siteCode))).Take(MaxListed).Select(View).ToList();
        return new Result<IReadOnlyList<OutboundEndpointViewModel>>(visible);
    }

    public async Task<Result<OutboundEndpointViewModel>> GetAsync(Guid id, CancellationToken ct = default) =>
        await VisibleAsync(id, ct, forChange: false) is { } endpoint ? new Result<OutboundEndpointViewModel>(View(endpoint)) : Result.Error<OutboundEndpointViewModel>(OutboundErrors.NotFound);

    public async Task<Result<OutboundEndpointViewModel>> CreateAsync(CreateOutboundEndpointRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Purpose is null || !Enum.GetNames<OutboundEndpointPurpose>().Contains(request.Purpose, StringComparer.Ordinal))
            return Result.Error<OutboundEndpointViewModel>(OutboundErrors.InvalidPurpose);
        var purpose = Enum.Parse<OutboundEndpointPurpose>(request.Purpose);
        var errors = new List<string>();
        if (!OutboundRules.IsCode(request.Code))
            errors.Add("code is 2 to 24 lower case letters, digits or hyphens.");
        if (!IsName(request.Name))
            errors.Add("A name is 1 to 100 characters of plain text.");
        var (connection, connectionErrors) = OutboundRules.Check(request.Connection, purpose, settings.LabHostSet, settings.AllowLoopback);
        errors.AddRange(connectionErrors);
        if (connection is not null)
            errors.AddRange(OutboundRules.CheckSecret(request.Secret, connection.AuthKind));
        if (connection?.PinnedCaPem is { } createPem && !OutboundSecrets.IsCertificatePem(createPem))
            errors.Add("pinnedCaPem does not load as a certificate.");
        var siteErrors = await SitesAsync(request.SiteCodes, purpose, ct);
        if (siteErrors.Count > 0 || errors.Count > 0)
            return Result.Error<OutboundEndpointViewModel>([.. siteErrors, .. errors]);
        if (await Query<OutboundEndpoint>().AnyAsync(e => e.Code == request.Code, ct))
            return Result.Error<OutboundEndpointViewModel>(OutboundErrors.DuplicateCode);

        var (protectedSecret, hasCertificate, secretError) = secrets.Protect(request.Secret, connection!.AuthKind);
        if (secretError is not null)
            return Result.Error<OutboundEndpointViewModel>(secretError);
        var endpoint = new OutboundEndpoint(request.Code, request.Name, purpose, Sites(request.SiteCodes), connection, protectedSecret, hasCertificate, UtcNow);
        await SaveAsync(endpoint, ct);
        try
        {
            // Now, so a code taken by a concurrent registration is a 409 here rather than a failed commit.
            await FlushAsync(ct);
        }
        catch (global::NHibernate.Exceptions.GenericADOException e) when (e.InnerException is global::Npgsql.PostgresException { SqlState: "23505" })
        {
            UnitOfWork.PromiseNotToCommit();
            return Result.Error<OutboundEndpointViewModel>(OutboundErrors.DuplicateCode);
        }

        await audit.RecordAsync("OutboundEndpoint.Created", "OutboundEndpoint", endpoint.Id, endpoint.Code, null, Summary(endpoint), ct);
        logger.LogInformation("Outbound endpoint {Code} created by {AdministratorId}", endpoint.Code, CurrentUser.Id);
        return new Result<OutboundEndpointViewModel>(View(endpoint));
    }

    public async Task<Result<OutboundEndpointViewModel>> UpdateAsync(Guid id, UpdateOutboundEndpointRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var endpoint = await VisibleAsync(id, ct);
        if (endpoint is null)
            return Result.Error<OutboundEndpointViewModel>(OutboundErrors.NotFound);
        var errors = new List<string>();
        if (!IsName(request.Name))
            errors.Add("A name is 1 to 100 characters of plain text.");
        var (connection, connectionErrors) = OutboundRules.Check(request.Connection, endpoint.Purpose, settings.LabHostSet, settings.AllowLoopback);
        errors.AddRange(connectionErrors);
        if (connection is not null && connection.AuthKind != endpoint.AuthKind)
            errors.Add("The authentication kind of an endpoint does not change; register a new endpoint.");
        if (connection?.PinnedCaPem is { } updatePem && !OutboundSecrets.IsCertificatePem(updatePem))
            errors.Add("pinnedCaPem does not load as a certificate.");
        var moves = connection is not null && (connection.BaseUrl.AbsoluteUri != endpoint.BaseUrl || string.Join(' ', connection.Networks) != endpoint.AllowedNetworks ||
                                                connection.PinnedCaPem != endpoint.PinnedCaPem || connection.TokenPath != endpoint.TokenPath);
        if (moves && request.Secret is null)
            errors.Add(SecretAgain);
        if (request.Secret is not null && connection is not null)
            errors.AddRange(OutboundRules.CheckSecret(request.Secret, endpoint.AuthKind));
        var siteErrors = await SitesAsync(request.SiteCodes, endpoint.Purpose, ct);
        if (siteErrors.Count > 0 || errors.Count > 0)
            return Result.Error<OutboundEndpointViewModel>([.. siteErrors, .. errors]);
        (string Protected, bool HasCertificate, string Error) replacement = default;
        if (request.Secret is not null)
        {
            replacement = secrets.Protect(request.Secret, endpoint.AuthKind);
            if (replacement.Error is not null)
                return Result.Error<OutboundEndpointViewModel>(replacement.Error);
        }

        var before = Summary(endpoint);
        endpoint.Rename(request.Name);
        endpoint.Apply(Sites(request.SiteCodes), connection!);
        if (replacement.Protected is not null)
            endpoint.SetSecret(replacement.Protected, replacement.HasCertificate, UtcNow);
        var after = Summary(endpoint);
        await UpdateAsync(endpoint, ct);
        await audit.RecordAsync("OutboundEndpoint.Updated", "OutboundEndpoint", endpoint.Id, endpoint.Code, before, after, ct);
        if (replacement.Protected is not null)
            await audit.RecordAsync("OutboundEndpoint.SecretChanged", "OutboundEndpoint", endpoint.Id, endpoint.Code, null, null, ct);
        return new Result<OutboundEndpointViewModel>(View(endpoint));
    }

    public async Task<Result<OutboundEndpointViewModel>> SetSecretAsync(Guid id, OutboundSecretRequest request, CancellationToken ct = default)
    {
        var endpoint = await VisibleAsync(id, ct);
        if (endpoint is null)
            return Result.Error<OutboundEndpointViewModel>(OutboundErrors.NotFound);
        var errors = OutboundRules.CheckSecret(request, endpoint.AuthKind);
        if (errors.Count > 0)
            return Result.Error<OutboundEndpointViewModel>([.. errors]);
        var (protectedSecret, hasCertificate, secretError) = secrets.Protect(request, endpoint.AuthKind);
        if (secretError is not null)
            return Result.Error<OutboundEndpointViewModel>(secretError);
        endpoint.SetSecret(protectedSecret, hasCertificate, UtcNow);
        await UpdateAsync(endpoint, ct);
        await audit.RecordAsync("OutboundEndpoint.SecretChanged", "OutboundEndpoint", endpoint.Id, endpoint.Code, null, null, ct);
        return new Result<OutboundEndpointViewModel>(View(endpoint));
    }

    public Task<Result<OutboundEndpointViewModel>> DisableAsync(Guid id, CancellationToken ct = default) => ChangeAsync(id, "OutboundEndpoint.Disabled", e => e.Disable(), ct);

    public Task<Result<OutboundEndpointViewModel>> EnableAsync(Guid id, CancellationToken ct = default) => ChangeAsync(id, "OutboundEndpoint.Enabled", e => e.Enable(), ct);

    private async Task<Result<OutboundEndpointViewModel>> ChangeAsync(Guid id, string action, Func<OutboundEndpoint, bool> change, CancellationToken ct)
    {
        var endpoint = await VisibleAsync(id, ct);
        if (endpoint is null)
            return Result.Error<OutboundEndpointViewModel>(OutboundErrors.NotFound);
        var before = Summary(endpoint);
        if (change(endpoint))
        {
            await UpdateAsync(endpoint, ct);
            await audit.RecordAsync(action, "OutboundEndpoint", endpoint.Id, endpoint.Code, before, Summary(endpoint), ct);
        }

        return new Result<OutboundEndpointViewModel>(View(endpoint));
    }

    // Scope first: a site outside the caller's own answers the same whether it exists or not (CWE-204).
    private async Task<List<string>> SitesAsync(IReadOnlyList<string> sites, OutboundEndpointPurpose purpose, CancellationToken ct)
    {
        var codes = Sites(sites);
        if (codes.Count is 0 or > OutboundEndpoint.MaxSites || codes.Any(s => !Site.IsValidCode(s)))
            return ["An endpoint is bound to 1 to 32 site codes."];
        if (purpose == OutboundEndpointPurpose.AcrisFlights && codes.Count != 1)
            return ["An ACRIS flight pull feeds exactly one site."];
        var mine = await siteScope.GetAsync(ct);
        if (!mine.Covers(new SiteAccess(false, codes.ToHashSet(StringComparer.Ordinal))))
            return [OutboundErrors.BeyondOwnSites];
        if (await Query<Site>().CountAsync(s => codes.Contains(s.Code), ct) != codes.Count)
            return [OutboundErrors.UnknownSite];
        return [];
    }

    private static List<string> Sites(IReadOnlyList<string> sites) =>
        (sites ?? []).Where(s => s is not null).Select(s => s.Trim()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    /// <summary>The endpoint when all its sites are the caller's; for a change, its row is locked first (concurrent changes run in turn).</summary>
    private async Task<OutboundEndpoint> VisibleAsync(Guid id, CancellationToken ct, bool forChange = true)
    {
        if (forChange)
            await ExecuteCommandAsync<LockRow>("SELECT 1 AS \"Value\" FROM outbound_endpoint WHERE id = :id FOR UPDATE", new Dictionary<string, object> { ["id"] = id }, ct);
        var endpoint = await GetAsync<OutboundEndpoint>(id, ct);
        return endpoint is not null && Covers(await siteScope.GetAsync(ct), endpoint) ? endpoint : null;
    }

    private static bool Covers(SiteAccess access, OutboundEndpoint endpoint) => endpoint.Sites.All(access.Allows);

    private static bool IsName(string name)
    {
        var trimmed = name?.Trim();
        return !string.IsNullOrEmpty(trimmed) && trimmed.Length <= OutboundEndpoint.MaxNameLength && Ariva.Core.Domain.Components.DisplayText.IsClean(trimmed);
    }

    // JSON, so a name cannot pass for another field (CWE-117); never the secret.
    private static string Summary(OutboundEndpoint e) => JsonSerializer.Serialize(new
    {
        name = e.Name,
        purpose = e.Purpose.ToString(),
        status = e.Status.ToString(),
        sites = e.SiteCodes,
        baseUrl = e.BaseUrl,
        networks = e.AllowedNetworks,
        auth = e.AuthKind.ToString(),
        tokenPath = e.TokenPath,
        clientId = e.ClientId,
        headerName = e.HeaderName,
        keyId = e.KeyId,
        pinnedCa = e.PinnedCaPem is not null,
        pullPath = e.PullPath,
        pollSeconds = e.PollSeconds
    });

    private static OutboundEndpointViewModel View(OutboundEndpoint e) => new(e.Id!.Value, e.Code, e.Name, e.Purpose.ToString(), e.Status.ToString(), e.Sites, e.BaseUrl, e.Networks,
        e.AuthKind.ToString(), e.TokenPath, e.ClientId, e.Scope, e.HeaderName, e.KeyId, e.TotpPerRequest, e.PinnedCaPem is not null, e.HasClientCertificate, e.TimeoutSeconds,
        e.RetryCount, e.BreakerFailures, e.BreakSeconds, e.PullPath, e.PollSeconds, e.SecretChangedUtc, e.LastPollUtc, e.LastStatus, e.ConsecutiveFailures);

    private sealed class LockRow
    {
        public int Value { get; set; }
    }
}
