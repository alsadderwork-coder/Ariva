using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Security;
using Ariva.Core.Services.Imports;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Imports;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Foundation;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using NHibernate.Linq;

namespace Ariva.Infra.Services.Topology;

/// <summary>
/// The desk code mapping import (ARV-123a), as the device import: the file hashed, read as a stream and checked
/// (<see cref="MappingImportRows"/>), each desk found by its checkpoint and desk code among the site's live desks, then the
/// mapping service itself (<see cref="ISvcDeskCodeMappings.CreateAsync"/>, unchanged: the code unique per system at the
/// site, one mapping per system and desk, the system fitting the desk's kind). The preview is a dry run rolled back; the
/// commit checks the token against the file's hash first, every row again, and creates every mapping in one transaction
/// under the site lock, or none. Audited as the device import; not a critical action, like the single mapping endpoint.
/// </summary>
internal sealed class SvcDeskCodeMappingImports(
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    ISiteScope siteScope,
    ISvcDeskCodeMappings mappings,
    AuditTrail audit,
    IServiceScopeFactory scopes,
    IDataProtectionProvider dataProtection,
    ILogger<SvcDeskCodeMappingImports> logger) : SvcBase(unitOfWork, currentUser, timeProvider), ISvcDeskCodeMappingImports
{
    private const string Target = "DeskCodeMappingImport";
    private const string Kind = "deskCodeMappings";
    private const string RolledBack = "The import was rolled back after its mappings were created.";

    private readonly ImportPreviewTokens _tokens = ImportPreviewTokens.Mappings(dataProtection);

    public async Task<bool> SiteReachableAsync(string siteCode, CancellationToken ct = default) =>
        siteCode is not null && (await siteScope.GetAsync(ct)).Allows(siteCode) && await Query<Site>().AnyAsync(s => s.Code == siteCode, ct);

    #region Preview

    public async Task<Result<DeskCodeMappingImportPreviewViewModel>> PreviewAsync(string siteCode, Func<Stream> openFile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(openFile);
        if (!await SiteReachableAsync(siteCode, ct))
            return Result.Error<DeskCodeMappingImportPreviewViewModel>(TopologyErrors.NotFound);

        string sha = null;
        try
        {
            sha = await HashAsync(openFile, ct);
            if (sha is null)
                return await RefusePreviewAsync(siteCode, null, CsvImportErrors.FileTooLarge);
            var (planned, table, error) = await ReadAsync(siteCode, openFile, ct);
            if (error is not null)
                return await RefusePreviewAsync(siteCode, sha, error);

            // A dry run of the mapping service itself, rolled back below.
            await LockSiteAsync(siteCode, ct);
            var passing = new List<DeskCodeMappingImportRowViewModel>();
            foreach (var (row, request) in planned)
            {
                var created = await mappings.CreateAsync(request, ct);
                if (created.HasErrors)
                {
                    var (column, reason) = Refusal(created.ErrorMessages.First());
                    table.Errors.Add(row.Row, table.Column(column), reason);
                    continue;
                }

                passing.Add(new DeskCodeMappingImportRowViewModel(row.Row, row.System, row.ExternalCode, row.Checkpoint, row.Desk));
            }

            UnitOfWork.PromiseNotToCommit();
            var token = table.Errors.Count == 0 && CurrentUser.Id is { } user ? _tokens.Issue(sha, siteCode, user, UtcNow) : null;
            await WriteOwnAuditAsync("Previewed", siteCode,
                ImportAudit.Summary(Kind, token is null ? "refused rows" : "previewed", null, sha, table.Rows, passing.Count, table.Errors.Count, 0));
            return new Result<DeskCodeMappingImportPreviewViewModel>(new DeskCodeMappingImportPreviewViewModel(sha, table.Rows, passing, table.Errors.Listed,
                table.Errors.Count, token, token is null ? null : UtcNow + CsvImportLimits.PreviewLifetime));
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

    private async Task<Result<DeskCodeMappingImportPreviewViewModel>> RefusePreviewAsync(string siteCode, string sha, string error)
    {
        UnitOfWork.PromiseNotToCommit();
        await TryWriteOwnAuditAsync("Previewed", siteCode, ImportAudit.Summary(Kind, "refused", error, sha, 0, 0, 0, 0));
        return Result.Error<DeskCodeMappingImportPreviewViewModel>(error);
    }

    #endregion

    #region Commit

    public async Task<Result<DeskCodeMappingImportCommitViewModel>> CommitAsync(string siteCode, Func<Stream> openFile, string previewToken, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(openFile);
        if (!await SiteReachableAsync(siteCode, ct))
            return Result.Error<DeskCodeMappingImportCommitViewModel>(TopologyErrors.NotFound);

        string sha = null;
        try
        {
            sha = await HashAsync(openFile, ct);
            if (sha is null)
                return await RefuseCommitAsync(siteCode, null, CsvImportErrors.FileTooLarge);
            if (!_tokens.Matches(previewToken, sha, siteCode, CurrentUser.Id, UtcNow))
                return await RefuseCommitAsync(siteCode, sha, CsvImportErrors.NotThePreview);
            var (planned, table, error) = await ReadAsync(siteCode, openFile, ct);
            if (error is not null)
                return await RefuseCommitAsync(siteCode, sha, error);
            if (table.Errors.Count > 0)
                return await RefuseCommitAsync(siteCode, sha, CsvImportErrors.RowsRefused);

            await LockSiteAsync(siteCode, ct);
            foreach (var (row, request) in planned)
            {
                var created = await mappings.CreateAsync(request, ct);
                if (created.HasErrors)
                    return await RefuseCommitAsync(siteCode, sha, CsvImportErrors.AtRow(row.Row, Refusal(created.ErrorMessages.First()).Reason));
            }

            try
            {
                // A code or desk mapped meanwhile through the single mapping endpoint surfaces here, not at the commit.
                await FlushAsync(ct);
            }
            catch (global::NHibernate.Exceptions.GenericADOException e) when (e.InnerException is global::Npgsql.PostgresException { SqlState: "23505" })
            {
                return await RefuseCommitAsync(siteCode, sha, CsvImportErrors.RowsRefused);
            }

            await audit.RecordAsync($"{Target}.Committed", "Site", null, siteCode, null,
                ImportAudit.Summary(Kind, "committed", null, sha, table.Rows, planned.Count, 0, planned.Count), ct);
            var failed = ImportAudit.Summary(Kind, "failed", RolledBack, sha, table.Rows, 0, 0, 0);
            RegisterPostRollbackAction(() => ImportAudit.WriteOwnAsync(scopes, CurrentUser, TimeProvider, $"{Target}.Failed", siteCode, failed));
            return new Result<DeskCodeMappingImportCommitViewModel>(new DeskCodeMappingImportCommitViewModel(sha, planned.Count));
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

    private async Task<Result<DeskCodeMappingImportCommitViewModel>> RefuseCommitAsync(string siteCode, string sha, string error)
    {
        UnitOfWork.PromiseNotToCommit();
        await TryWriteOwnAuditAsync("Refused", siteCode, ImportAudit.Summary(Kind, "refused", error, sha, 0, 0, 0, 0));
        return Result.Error<DeskCodeMappingImportCommitViewModel>(error);
    }

    #endregion

    #region Helpers

    private static async Task<string> HashAsync(Func<Stream> openFile, CancellationToken ct)
    {
        await using var file = openFile();
        return await CsvImportReader.Sha256Async(file, ct);
    }

    private async Task<(IReadOnlyList<(MappingImportRow Row, CreateDeskCodeMappingRequest Request)> Planned, CsvTable Table, string Error)> ReadAsync(string siteCode,
        Func<Stream> openFile, CancellationToken ct)
    {
        CsvTable table;
        string error;
        await using (var file = openFile())
            (table, error) = await CsvImportReader.ReadAsync(file, MappingImportRows.Schema, ct);
        if (error is not null)
            return (null, null, error);

        var rows = MappingImportRows.Check(table);
        // Checkpoint codes are unique among a site's live checkpoints, desk codes within their checkpoint.
        var desks = (await Query<Desk>().Where(d => d.SiteCode == siteCode && d.DeletedOn == null).Fetch(d => d.Checkpoint).ToListAsync(ct))
            .GroupBy(d => SiteConfigurationRules.DeskKey(d.Checkpoint.Code, d.Code), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);
        var planned = new List<(MappingImportRow, CreateDeskCodeMappingRequest)>();
        foreach (var row in rows)
        {
            if (!desks.TryGetValue(SiteConfigurationRules.DeskKey(row.Checkpoint, row.Desk), out var desk))
            {
                table.Errors.Add(row.Row, table.Column("desk"), CsvImportErrors.DeskNotFound);
                continue;
            }

            planned.Add((row, new CreateDeskCodeMappingRequest(row.System, row.ExternalCode, desk)));
        }

        return (planned, table, null);
    }

    /// <summary>The column and fixed text of the mapping service's refusal; an entity's message (which names kinds) becomes a fixed text.</summary>
    internal static (string Column, string Reason) Refusal(string message) => message switch
    {
        TopologyErrors.UnknownKind => ("externalSystem", CsvImportErrors.UnknownSystem),
        TopologyErrors.NotFound => ("desk", CsvImportErrors.DeskNotFound),
        TopologyErrors.Duplicate => ("externalCode", CsvImportErrors.MappingTaken),
        CsvImportErrors.ExternalCode => ("externalCode", CsvImportErrors.ExternalCode),
        _ when message?.Contains(" code cannot map to a ", StringComparison.Ordinal) == true => ("desk", CsvImportErrors.MappingDoesNotFitDesk),
        _ => ("desk", CsvImportErrors.MappingRefused)
    };

    private Task LockSiteAsync(string siteCode, CancellationToken ct) =>
        ExecuteCommandAsync<LockRow>("""SELECT 1 AS "Value" FROM site WHERE code = :code FOR UPDATE""", new Dictionary<string, object> { ["code"] = siteCode }, ct);

    private Task WriteOwnAuditAsync(string verb, string siteCode, string summary) =>
        ImportAudit.WriteOwnAsync(scopes, CurrentUser, TimeProvider, $"{Target}.{verb}", siteCode, summary);

    private async Task TryWriteOwnAuditAsync(string verb, string siteCode, string summary)
    {
        try
        {
            await WriteOwnAuditAsync(verb, siteCode, summary);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "The audit entry of a desk code mapping import at {Site} could not be written", siteCode);
        }
    }

    private sealed class LockRow
    {
        public int Value { get; set; }
    }

    #endregion
}
