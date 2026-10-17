using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Security;
using Ariva.Core.Services.Imports;
using Ariva.Core.Services.Sensing;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Imports;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Foundation;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using NHibernate.Linq;

namespace Ariva.Infra.Services.Sensing;

/// <summary>
/// The device import (ARV-123a). The file is hashed (at most 1 MB), then read as a stream (<see cref="CsvImportReader"/>)
/// and each row checked: the generic cell rules, codes and kinds by their exact names (<see cref="DeviceImportRows"/>),
/// the level by its terminal and level code among the site's live levels, and then the registration path itself
/// (<see cref="ISvcDevices.RegisterAsync"/>, unchanged: the mapping catalog, the supported transports, the queue zone on
/// the device's level within its footprint's reach, the code free at the site). The preview runs that registration as a
/// dry run under the site lock and rolls it back; a file without a refused row gets a token. The commit checks the token
/// against the file's hash before it parses anything, checks every row again and registers them all in one transaction:
/// any refusal keeps nothing. Every outcome once the service runs past the site check is audited (counts and hash only); a
/// preview's and a refusal's entry in a transaction of its own. Not audited: the controller's refusals before the service
/// (no file, a file over 1 MB, a token of the wrong shape) and a cancelled request.
/// </summary>
internal sealed class SvcDeviceImports(
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    ISiteScope siteScope,
    ISvcDevices devices,
    AuditTrail audit,
    IServiceScopeFactory scopes,
    IDataProtectionProvider dataProtection,
    ILogger<SvcDeviceImports> logger) : SvcBase(unitOfWork, currentUser, timeProvider), ISvcDeviceImports
{
    private const string Target = "DeviceImport";
    private const string Kind = "devices";

    /// <summary>The audit reason of a commit rolled back after its devices were registered (a failed commit included).</summary>
    private const string RolledBack = "The import was rolled back after its devices were registered.";

    private readonly ImportPreviewTokens _tokens = ImportPreviewTokens.Devices(dataProtection);

    public async Task<bool> SiteReachableAsync(string siteCode, CancellationToken ct = default) =>
        siteCode is not null && (await siteScope.GetAsync(ct)).Allows(siteCode) && await Query<Site>().AnyAsync(s => s.Code == siteCode, ct);

    #region Preview

    public async Task<Result<DeviceImportPreviewViewModel>> PreviewAsync(string siteCode, Func<Stream> openFile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(openFile);
        if (!await SiteReachableAsync(siteCode, ct))
            return Result.Error<DeviceImportPreviewViewModel>(TopologyErrors.NotFound);

        string sha = null;
        try
        {
            sha = await HashAsync(openFile, ct);
            if (sha is null)
                return await RefusePreviewAsync(siteCode, null, CsvImportErrors.FileTooLarge);
            var (planned, table, error) = await ReadAsync(siteCode, openFile, ct);
            if (error is not null)
                return await RefusePreviewAsync(siteCode, sha, error);

            // A dry run of the registration itself, under the site lock, rolled back below: the preview shows exactly what
            // the commit would refuse. Its credentials are dropped here and never leave the service.
            await LockSiteAsync(siteCode, ct);
            var passing = new List<DeviceImportRowViewModel>();
            foreach (var (row, request) in planned)
            {
                var registered = await devices.RegisterAsync(request, ct);
                if (registered.HasErrors)
                {
                    var (column, reason) = Refusal(registered.ErrorMessages.First());
                    table.Errors.Add(row.Row, column is null ? null : table.ColumnNumbers.GetValueOrDefault(column), reason);
                    continue;
                }

                passing.Add(new DeviceImportRowViewModel(row.Row, row.Code, row.Family, row.Model, row.Terminal, row.Level, row.QueueZone));
            }

            UnitOfWork.PromiseNotToCommit();
            var token = table.Errors.Count == 0 && CurrentUser.Id is { } user ? _tokens.Issue(sha, siteCode, user, UtcNow) : null;
            await WriteOwnAuditAsync("Previewed", siteCode,
                ImportAudit.Summary(Kind, token is null ? "refused rows" : "previewed", null, sha, table.Rows, passing.Count, table.Errors.Count, 0));
            return new Result<DeviceImportPreviewViewModel>(new DeviceImportPreviewViewModel(sha, table.Rows, passing, table.Errors.Listed, table.Errors.Count, token,
                token is null ? null : UtcNow + CsvImportLimits.PreviewLifetime));
        }
        catch (OperationCanceledException)
        {
            UnitOfWork.PromiseNotToCommit();
            throw;
        }
        catch (Exception)
        {
            UnitOfWork.PromiseNotToCommit();
            await TryWriteOwnAuditAsync("Failed", siteCode, ImportAudit.Summary(Kind, "failed", "preview", sha, 0, 0, 0, 0));
            throw;
        }
    }

    private async Task<Result<DeviceImportPreviewViewModel>> RefusePreviewAsync(string siteCode, string sha, string error)
    {
        UnitOfWork.PromiseNotToCommit();
        await TryWriteOwnAuditAsync("Previewed", siteCode, ImportAudit.Summary(Kind, "refused", error, sha, 0, 0, 0, 0));
        return Result.Error<DeviceImportPreviewViewModel>(error);
    }

    #endregion

    #region Commit

    public async Task<Result<DeviceImportCommitViewModel>> CommitAsync(string siteCode, Func<Stream> openFile, string previewToken, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(openFile);
        if (!await SiteReachableAsync(siteCode, ct))
            return Result.Error<DeviceImportCommitViewModel>(TopologyErrors.NotFound);

        string sha = null;
        try
        {
            sha = await HashAsync(openFile, ct);
            if (sha is null)
                return await RefuseCommitAsync(siteCode, null, CsvImportErrors.FileTooLarge);
            // The token first: a file, site or user other than the preview's costs nothing more than its hash.
            if (!_tokens.Matches(previewToken, sha, siteCode, CurrentUser.Id, UtcNow))
                return await RefuseCommitAsync(siteCode, sha, CsvImportErrors.NotThePreview);
            var (planned, table, error) = await ReadAsync(siteCode, openFile, ct);
            if (error is not null)
                return await RefuseCommitAsync(siteCode, sha, error);
            if (table.Errors.Count > 0)
                return await RefuseCommitAsync(siteCode, sha, CsvImportErrors.RowsRefused);

            await LockSiteAsync(siteCode, ct);
            var credentials = new List<ImportedDeviceCredentialViewModel>();
            foreach (var (row, request) in planned)
            {
                var registered = await devices.RegisterAsync(request, ct);
                if (registered.HasErrors)
                    return await RefuseCommitAsync(siteCode, sha, CsvImportErrors.AtRow(row.Row, Refusal(registered.ErrorMessages.First()).Reason));
                credentials.Add(new ImportedDeviceCredentialViewModel(registered.Data.Device.Code, registered.Data.Credential));
            }

            try
            {
                // Constraint violations (a code registered meanwhile by another request) surface here, not at the commit.
                await FlushAsync(ct);
            }
            catch (global::NHibernate.Exceptions.GenericADOException e) when (e.InnerException is global::Npgsql.PostgresException { SqlState: "23505" })
            {
                return await RefuseCommitAsync(siteCode, sha, CsvImportErrors.RowsRefused);
            }

            await audit.RecordAsync($"{Target}.Committed", "Site", null, siteCode, null,
                ImportAudit.Summary(Kind, "committed", null, sha, table.Rows, credentials.Count, 0, credentials.Count), ct);
            // The commit comes after this answer (the unit of work's filter); should it fail, the entry above goes with it
            // and this records the import as failed in a transaction of its own.
            var failed = ImportAudit.Summary(Kind, "failed", RolledBack, sha, table.Rows, 0, 0, 0);
            RegisterPostRollbackAction(() => ImportAudit.WriteOwnAsync(scopes, CurrentUser, TimeProvider, $"{Target}.Failed", siteCode, failed));
            return new Result<DeviceImportCommitViewModel>(new DeviceImportCommitViewModel(sha, credentials.Count, credentials, [CsvImportErrors.DevicesArriveCommissioning]));
        }
        catch (OperationCanceledException)
        {
            UnitOfWork.PromiseNotToCommit();
            throw;
        }
        catch (Exception)
        {
            UnitOfWork.PromiseNotToCommit();
            await TryWriteOwnAuditAsync("Failed", siteCode, ImportAudit.Summary(Kind, "failed", "commit", sha, 0, 0, 0, 0));
            throw;
        }
    }

    private async Task<Result<DeviceImportCommitViewModel>> RefuseCommitAsync(string siteCode, string sha, string error)
    {
        UnitOfWork.PromiseNotToCommit();
        await TryWriteOwnAuditAsync("Refused", siteCode, ImportAudit.Summary(Kind, "refused", error, sha, 0, 0, 0, 0));
        return Result.Error<DeviceImportCommitViewModel>(error);
    }

    #endregion

    #region Helpers

    private static async Task<string> HashAsync(Func<Stream> openFile, CancellationToken ct)
    {
        await using var file = openFile();
        return await CsvImportReader.Sha256Async(file, ct);
    }

    /// <summary>The rows with their registration requests, the table (with every refused cell so far), or the file's refusal.</summary>
    private async Task<(IReadOnlyList<(DeviceImportRow Row, RegisterDeviceRequest Request)> Planned, CsvTable Table, string Error)> ReadAsync(string siteCode,
        Func<Stream> openFile, CancellationToken ct)
    {
        CsvTable table;
        string error;
        await using (var file = openFile())
            (table, error) = await CsvImportReader.ReadAsync(file, DeviceImportRows.Schema, ct);
        if (error is not null)
            return (null, null, error);

        var rows = DeviceImportRows.Check(table);
        // A level by terminal and level code; terminal codes are unique per airport, so a site with one code at two airports
        // cannot tell its levels apart.
        var levels = (await Query<Level>().Where(l => l.SiteCode == siteCode && l.DeletedOn == null).Fetch(l => l.Terminal).ToListAsync(ct))
            .GroupBy(l => SiteConfigurationRules.LevelKey(l.Terminal.Code, l.Code), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count() == 1 ? g.Single() : null, StringComparer.Ordinal);
        var planned = new List<(DeviceImportRow, RegisterDeviceRequest)>();
        foreach (var row in rows)
        {
            if (!levels.TryGetValue(SiteConfigurationRules.LevelKey(row.Terminal, row.Level), out var level))
            {
                table.Errors.Add(row.Row, table.Column("level"), CsvImportErrors.LevelNotFound);
                continue;
            }

            if (level is null)
            {
                table.Errors.Add(row.Row, table.Column("terminal"), CsvImportErrors.LevelAmbiguous);
                continue;
            }

            planned.Add((row, new RegisterDeviceRequest(row.Code, row.Family, row.Model, row.Transport, row.Dialect, row.ClockSource,
                new DevicePlacement(level.Id, row.X, row.Y, row.MountingHeightMetres, row.OrientationDegrees, row.QueueZone, row.FootprintLengthMetres,
                    row.FootprintWidthMetres, row.FootprintRadiusMetres), string.IsNullOrEmpty(row.MappingName) ? null : row.MappingName)));
        }

        return (planned, table, null);
    }

    /// <summary>
    /// The column and fixed text of a registration's refusal: its own fixed texts as they are, anything else (an entity's
    /// message, which may repeat a value) as one fixed text.
    /// </summary>
    internal static (string Column, string Reason) Refusal(string message) => message switch
    {
        DeviceErrors.ZoneNotFound or DeviceErrors.ZoneOnAnotherLevel => ("queueZone", message),
        DeviceErrors.OutOfReach => ("x", message),
        DeviceErrors.UnknownMapping => ("mappingName", message),
        DeviceErrors.UnknownFamily => ("family", message),
        DeviceErrors.UnknownTransport or DeviceErrors.UnsupportedTransport => ("transport", message),
        DeviceErrors.UnknownDialect => ("dialect", message),
        DeviceErrors.UnknownClockSource => ("clockSource", message),
        TopologyErrors.Duplicate => ("code", message),
        TopologyErrors.NotFound => ("level", CsvImportErrors.LevelNotFound),
        _ => (null, CsvImportErrors.RegistrationRefused)
    };

    private Task LockSiteAsync(string siteCode, CancellationToken ct) =>
        ExecuteCommandAsync<LockRow>("""SELECT 1 AS "Value" FROM site WHERE code = :code FOR UPDATE""", new Dictionary<string, object> { ["code"] = siteCode }, ct);

    private Task WriteOwnAuditAsync(string verb, string siteCode, string summary) =>
        ImportAudit.WriteOwnAsync(scopes, CurrentUser, TimeProvider, $"{Target}.{verb}", siteCode, summary);

    /// <summary>A refusal's or failure's entry: an audit write that fails is logged beside the answer, never silent.</summary>
    private async Task TryWriteOwnAuditAsync(string verb, string siteCode, string summary)
    {
        try
        {
            await WriteOwnAuditAsync(verb, siteCode, summary);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "The audit entry of a device import at {Site} could not be written", siteCode);
        }
    }

    private sealed class LockRow
    {
        public int Value { get; set; }
    }

    #endregion
}
