using System.Text.Json;
using Ariva.Core.Domain.Constants;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Validation;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.Infra.Services.Validation;

/// <summary>
/// The validation results as served and frozen (ARV-104g; script 0050). A caller reads a campaign's results only within its own
/// sites (ISiteScope first; another site's campaign answers NotFound, CWE-863, CWE-204), and only as it may read them
/// (<see cref="ValidationResultsViewModel.For"/>, from its stored roles: desk-state results to border roles, observer-level results
/// to View or Manage holders, the shadow's figures to View holders), as the JSON the results type writes itself.
/// <para>
/// Frozen at close: the first computation of a closed campaign (the host's background freeze, <see cref="ValidationResultsFreezer"/>,
/// or the first read, whichever comes first; one flight per campaign shared by both) stores revision 1 of the results document with
/// its SHA-256 content hash, which the database checks against the bytes at insert, and audits it as the system. Every later read
/// serves the stored document, its hash checked again first (CWE-345: a document that does not match is not served). A
/// recomputation (Validation.Manage with a second factor at the controller) computes the closed campaign again and adds the next
/// revision with the caller's reason, audited in the same unit of work; nothing is ever edited (script 0050 refuses updates and
/// deletes). A campaign not closed is computed when asked and never stored.
/// </para>
/// <para>
/// Memory (M3 of the ARV-104g2 review, Proposed in docs/product/decisions.md): every computation, live, freeze or recomputation,
/// takes a turn of the host-wide limit (<c>Validation:Results:MaxConcurrentComputations</c>, 1 by default), and a campaign holds at
/// most <see cref="ValidationCampaign.MaxZoneDays"/> zone-days, so one computation peaks near 200 MB; the results hold no
/// per-minute list. The stored document is at most 16 MB.
/// </para>
/// </summary>
internal sealed partial class SvcValidationResults
{
    #region Constants

    /// <summary>The flight purposes besides the live results (whose purpose is empty): a closed campaign's freeze and a recomputation.</summary>
    internal const string FreezePurpose = "freeze";

    internal const string RecomputePurpose = "recompute";

    /// <summary>The system identity the freeze runs and is audited as.</summary>
    internal const string FreezeUser = "validation-results-freeze";

    /// <summary>Stable audit action codes; never rename one that has shipped.</summary>
    internal static class Actions
    {
        public const string Frozen = "ValidationResults.Frozen";
        public const string Recomputed = "ValidationResults.Recomputed";
    }

    private const string AuditTarget = "ValidationResultRevision";

    /// <summary>PostgreSQL: a check constraint refused a row (more revisions than 1,000).</summary>
    private const string CheckViolation = "23514";

    // The latest revision, with how many there are (the window counts before the LIMIT).
    private const string LatestRevisionSql = """
        SELECT id AS "Id", revision AS "Number", CAST(count(*) OVER () AS integer) AS "Revisions", content_sha256 AS "ContentSha256",
               frozen_utc AS "FrozenUtc", reason AS "Reason", document AS "Document"
          FROM validation_result_revision
         WHERE campaign_id = :campaign AND site_code = :site
         ORDER BY revision DESC
         LIMIT 1
        """;

    private const string RevisionSql = """
        SELECT r.id AS "Id", r.revision AS "Number",
               (SELECT CAST(count(*) AS integer) FROM validation_result_revision n WHERE n.campaign_id = r.campaign_id) AS "Revisions",
               r.content_sha256 AS "ContentSha256", r.frozen_utc AS "FrozenUtc", r.reason AS "Reason", r.document AS "Document"
          FROM validation_result_revision r
         WHERE r.campaign_id = :campaign AND r.site_code = :site AND r.revision = :revision
        """;

    // Revision 1, frozen by the system at close; a revision already there (another replica, a read at the same time) stays.
    private const string FreezeSql = """
        INSERT INTO validation_result_revision (id, campaign_id, site_code, revision, reason, document, content_sha256, computed_utc, frozen_utc, frozen_by_id)
        VALUES (:id, :campaign, :site, 1, NULL, :document, :hash, :computed, :frozen, NULL)
        ON CONFLICT (campaign_id, revision) DO NOTHING
        RETURNING id AS "Value"
        """;

    // The next revision after the latest, with its reason and who asked; two at once meet the unique key (23505).
    private const string RecomputedSql = """
        INSERT INTO validation_result_revision (id, campaign_id, site_code, revision, reason, document, content_sha256, computed_utc, frozen_utc, frozen_by_id)
        SELECT :id, :campaign, :site, max(revision) + 1, :reason, :document, :hash, :computed, :frozen, :by
          FROM validation_result_revision
         WHERE campaign_id = :campaign AND site_code = :site
        HAVING count(*) > 0
        RETURNING id AS "Value"
        """;

    // Closed campaigns of every site whose results are not frozen yet, the longest closed first (the background freeze), leaving
    // out those the freeze is waiting to retry before the LIMIT (first security review of ARV-104g, M3: a LIMIT before the skip
    // let twenty failing campaigns starve every newer one). The skipped ids are a bound list of text values (never inlined).
    private const string DueSql = """
        SELECT c.id AS "Campaign", c.site_code AS "Site"
          FROM validation_campaign c
         WHERE c.status = 'Closed'
           AND NOT EXISTS (SELECT 1 FROM validation_result_revision r WHERE r.campaign_id = c.id)
           AND CAST(c.id AS text) NOT IN (:skip)
         ORDER BY c.closed_utc, c.id
         LIMIT :limit
        """;

    /// <summary>The skip list when nothing is skipped: not a GUID, so it matches no campaign (an empty list is not SQL).</summary>
    private const string NoCampaign = "none";

    #endregion

    #region Service

    public async Task<Result<ValidationResultsJson>> GetAsync(string siteCode, Guid campaignId, int? revision = null, CancellationToken ct = default)
    {
        if (revision is < 1 or > ValidationResultsViewModel.MaxRevisions)
            return Result.Error<ValidationResultsJson>(ValidationResultsErrors.InvalidRevision);
        if (!await VisibleAsync(siteCode, ct) || await CampaignAsync(siteCode, campaignId, ct) is not { } campaign)
            return Result.Error<ValidationResultsJson>(ValidationErrors.NotFound);
        var reader = await ReaderAsync(ct);

        var stored = revision is { } number ? await RevisionAsync(siteCode, campaignId, number, ct) : await LatestAsync(siteCode, campaignId, ct);
        if (stored is not null)
            return Served(Stored(stored, siteCode, campaignId), reader);
        if (revision is not null)
            return Result.Error<ValidationResultsJson>(ValidationErrors.NotFound);

        // A closed campaign not frozen yet is frozen now (one flight with the background freeze); any other is computed and not stored.
        // Nothing above opened a transaction (reads only; the session holds a connection only for the length of each statement
        // outside one), so a request waiting here for its turn holds no connection (L3 of the first review of ARV-104g).
        var results = campaign.Status == ValidationCampaignStatus.Closed
            ? await flights.RunAsync(campaignId, FreezePurpose, token => FreezeApartAsync(siteCode, campaignId, token), ct)
            : await flights.RunAsync(campaignId, token => ComputeApartAsync(siteCode, campaignId, token), ct);
        return Served(results, reader);
    }

    public async Task<Result<ValidationResultsJson>> RecomputeAsync(string siteCode, Guid campaignId, RecomputeValidationResultsRequest request,
        CancellationToken ct = default)
    {
        if (!await VisibleAsync(siteCode, ct) || await CampaignAsync(siteCode, campaignId, ct) is not { } campaign || Caller is not { } caller)
            return Result.Error<ValidationResultsJson>(ValidationErrors.NotFound);
        request ??= new RecomputeValidationResultsRequest(null);
        var valid = await ValidationRules.Recompute().ValidateAllAsync(request);
        if (valid.HasErrors)
            return Result.Error<ValidationResultsJson>(ValidationResultsErrors.InvalidReason);
        if (campaign.Status != ValidationCampaignStatus.Closed || await LatestAsync(siteCode, campaignId, ct) is null)
            return Result.Error<ValidationResultsJson>(ValidationResultsErrors.NotFrozen);
        if (flights.IsFlying(campaignId, RecomputePurpose))
            return Result.Error<ValidationResultsJson>(ValidationResultsErrors.RecomputeInProgress);

        // Reads only so far: no transaction and no connection is held while the computation runs (L3); the insert opens it after.

        var computed = await flights.RunAsync(campaignId, RecomputePurpose, token => ComputeApartAsync(siteCode, campaignId, token), ct);
        if (computed.HasErrors)
            return Result.Error<ValidationResultsJson>(computed.ErrorMessages);
        var document = computed.Data.ToDocument();
        if (document.Length > ValidationResultsViewModel.MaxDocumentBytes)
            return Result.Error<ValidationResultsJson>(ValidationResultsErrors.DocumentTooLarge);

        var hash = ValidationResultsViewModel.Hash(document);
        var reason = request.Reason.Trim();
        var id = Guid.CreateVersion7();
        var inserted = await InsertAsync(RecomputedSql, new Dictionary<string, object>
        {
            ["id"] = id, ["campaign"] = campaignId, ["site"] = siteCode, ["reason"] = reason, ["document"] = document, ["hash"] = hash,
            ["computed"] = computed.Data.ComputedUtc, ["frozen"] = UtcNow, ["by"] = caller
        }, ct);
        if (inserted != true)
            return Result.Error<ValidationResultsJson>(ValidationResultsErrors.RevisionConflict);

        // The revision and its audit entry commit together or not at all.
        var stored = await LatestAsync(siteCode, campaignId, ct);
        if (stored is null || stored.Id != id)
        {
            UnitOfWork.PromiseNotToCommit();
            return Result.Error<ValidationResultsJson>(ValidationResultsErrors.RevisionConflict);
        }

        await audit.RecordAsync(Actions.Recomputed, AuditTarget, id, siteCode, null, AuditSummary(campaignId, stored.Number, hash, document.Length, reason), ct);
        return Served(Stored(stored, siteCode, campaignId), await ReaderAsync(ct));
    }

    #endregion

    #region Freeze

    /// <summary>
    /// A closed campaign's freeze in a scope of its own, as the system (<see cref="FreezeUser"/>), committed there: every caller of
    /// the flight (the background freeze and any read meanwhile) gets the stored revision. The caller has checked the site and the
    /// campaign (a read), or found them among the closed campaigns (the background freeze).
    /// </summary>
    private async Task<Result<ValidationResultsViewModel>> FreezeApartAsync(string siteCode, Guid campaignId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ICurrentUser>().SetSystemUser(Guid.Empty, FreezeUser);
        var service = scope.ServiceProvider.GetRequiredService<SvcValidationResults>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var frozen = await Guarded(() => service.FreezeAsync(siteCode, campaignId, ct), logger, ct);
        if (frozen.HasErrors)
            unitOfWork.PromiseNotToCommit();
        await unitOfWork.EndAsync(ct);
        return frozen;
    }

    /// <summary>
    /// Revision 1 of a closed campaign's results: the stored one when there is one, otherwise computed now, stored with its hash
    /// and audited as the system; then read back, so that what is served is exactly what is stored.
    /// </summary>
    internal async Task<Result<ValidationResultsViewModel>> FreezeAsync(string siteCode, Guid campaignId, CancellationToken ct)
    {
        var campaign = await CampaignByIdAsync(siteCode, campaignId, ct);
        if (campaign is null)
            return Result.Error<ValidationResultsViewModel>(ValidationErrors.NotFound);
        if (campaign.Status != ValidationCampaignStatus.Closed)
            return Result.Error<ValidationResultsViewModel>(ValidationResultsErrors.NotFrozen);
        if (await LatestAsync(siteCode, campaignId, ct) is { } existing)
            return Stored(existing, siteCode, campaignId);

        var computed = await ComputeAsync(siteCode, campaignId, ct);
        if (computed.HasErrors)
            return computed;
        var document = computed.Data.ToDocument();
        if (document.Length > ValidationResultsViewModel.MaxDocumentBytes)
            return Result.Error<ValidationResultsViewModel>(ValidationResultsErrors.DocumentTooLarge);

        var hash = ValidationResultsViewModel.Hash(document);
        var id = Guid.CreateVersion7();
        var inserted = await InsertAsync(FreezeSql, new Dictionary<string, object>
        {
            ["id"] = id, ["campaign"] = campaignId, ["site"] = siteCode, ["document"] = document, ["hash"] = hash, ["computed"] = computed.Data.ComputedUtc,
            ["frozen"] = UtcNow
        }, ct);
        // A refused statement aborted the transaction: nothing more is read in it (L1 of the first review: 25P02). A revision 1
        // committed meanwhile by another replica or read is not a refusal: ON CONFLICT leaves it, and it is read back below.
        if (inserted is null)
            return Result.Error<ValidationResultsViewModel>(ValidationResultsErrors.RevisionConflict);
        if (inserted == true)
        {
            await audit.RecordAsync(Actions.Frozen, AuditTarget, id, siteCode, null, AuditSummary(campaignId, 1, hash, document.Length, null), ct);
            await FlushAsync(ct);
        }

        var stored = await LatestAsync(siteCode, campaignId, ct);
        return stored is null ? Result.Error<ValidationResultsViewModel>(ValidationResultsErrors.RevisionConflict) : Stored(stored, siteCode, campaignId);
    }

    /// <summary>
    /// The background freeze's pass (<see cref="ValidationResultsFreezer"/>): up to <paramref name="limit"/> closed campaigns of
    /// every site whose results are not frozen, the longest closed first, other than those in <paramref name="skip"/> (left out in
    /// the query, before its limit); each frozen through the same flight a read would join. Returns each campaign tried and the
    /// first error of its freeze (null when frozen), never the results themselves.
    /// </summary>
    internal async Task<IReadOnlyList<(Guid Campaign, string Error)>> FreezeDueAsync(IReadOnlyCollection<Guid> skip, int limit, CancellationToken ct)
    {
        IReadOnlyCollection<string> skipped = skip is { Count: > 0 } ? [.. skip.Select(id => id.ToString("D"))] : [NoCampaign];
        var due = await ExecuteSqlAsync<DueRow>(DueSql, new Dictionary<string, object> { ["skip"] = skipped, ["limit"] = Math.Clamp(limit, 1, 100) }, ct);
        var outcomes = new List<(Guid Campaign, string Error)>();
        foreach (var row in due)
        {
            var frozen = await flights.RunAsync(row.Campaign, FreezePurpose, token => FreezeApartAsync(row.Site, row.Campaign, token), ct);
            outcomes.Add((row.Campaign, frozen.HasErrors ? frozen.ErrorMessages.FirstOrDefault() ?? ValidationResultsErrors.Busy : null));
        }

        return outcomes;
    }

    #endregion

    #region Revisions

    private Task<RevisionRow> LatestAsync(string siteCode, Guid campaignId, CancellationToken ct) =>
        FirstAsync(ExecuteSqlAsync<RevisionRow>(LatestRevisionSql, new Dictionary<string, object> { ["campaign"] = campaignId, ["site"] = siteCode }, ct));

    private Task<RevisionRow> RevisionAsync(string siteCode, Guid campaignId, int revision, CancellationToken ct) =>
        FirstAsync(ExecuteSqlAsync<RevisionRow>(RevisionSql, new Dictionary<string, object> { ["campaign"] = campaignId, ["site"] = siteCode, ["revision"] = revision }, ct));

    private static async Task<RevisionRow> FirstAsync(Task<List<RevisionRow>> rows) => (await rows).FirstOrDefault();

    /// <summary>
    /// Inserts a revision through the unit of work (committed with it): true when inserted; false when the statement left an
    /// existing revision 1 in place (<see cref="FreezeSql"/>'s ON CONFLICT, the transaction still usable); null when the statement
    /// was refused (two recomputations at once, 23505; every revision used, 23514; a campaign the script's trigger refuses, 23001),
    /// which aborts the transaction, so the unit of work is promised not to commit and nothing more may be read in it.
    /// </summary>
    private async Task<bool?> InsertAsync([System.Diagnostics.CodeAnalysis.ConstantExpected] string sql, Dictionary<string, object> parameters, CancellationToken ct)
    {
        try
        {
            return (await ExecuteCommandAsync<GuidRow>(sql, parameters, ct)).Count == 1;
        }
        catch (global::NHibernate.Exceptions.GenericADOException e) when (e.InnerException is global::Npgsql.PostgresException
            { SqlState: UniqueViolation or RestrictViolation or CheckViolation })
        {
            UnitOfWork.PromiseNotToCommit();
            return null;
        }
    }

    /// <summary>
    /// A stored revision's results with <see cref="ValidationResultsViewModel.Revision"/> set, after its document's hash is checked
    /// again (CWE-345): a document that does not match its hash, is not results, or is the results of another campaign or site is
    /// not served (an error, logged with the revision's id only).
    /// </summary>
    private Result<ValidationResultsViewModel> Stored(RevisionRow row, string siteCode, Guid campaignId)
    {
        var document = row.Document ?? [];
        var results = !string.Equals(ValidationResultsViewModel.Hash(document), row.ContentSha256, StringComparison.Ordinal) ? null : ValidationResultsViewModel.FromJson(document);
        if (results is null || results.CampaignId != campaignId || !string.Equals(results.SiteCode, siteCode, StringComparison.Ordinal))
        {
            logger?.LogError("A frozen revision of validation results does not match its content hash: {Revision}", row.Id);
            return Result.Error<ValidationResultsViewModel>(ValidationResultsErrors.Corrupt);
        }

        return new Result<ValidationResultsViewModel>(results with
        {
            Revision = new ValidationResultsViewModel.RevisionView(row.Number, row.Revisions, row.ContentSha256, Utc(row.FrozenUtc), row.Reason)
        });
    }

    /// <summary>The results as <paramref name="reader"/> may read them, written as JSON by the results type (an error passes through).</summary>
    private static Result<ValidationResultsJson> Served(Result<ValidationResultsViewModel> results, ValidationResultsViewModel.Reader reader)
    {
        if (results.HasErrors)
            return Result.Error<ValidationResultsJson>(results.ErrorMessages);
        var projected = results.Data.For(reader);
        return new Result<ValidationResultsJson>(new ValidationResultsJson(projected.ToJson(), projected.Revision?.Number, projected.Revision?.ContentSha256));
    }

    /// <summary>The caller as a reader of the results, from its stored roles (never the token) and its Ariva user id.</summary>
    private async Task<ValidationResultsViewModel.Reader> ReaderAsync(CancellationToken ct) =>
        ValidationResultsViewModel.Reader.Of(await callerRoles.GetAsync(ct), Caller);

    /// <summary>What the audit keeps of a revision: the campaign, the number, the hash, the size and the reason as a JSON string; no results.</summary>
    private static string AuditSummary(Guid campaignId, int revision, string hash, int bytes, string reason) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"campaign={campaignId}; revision={revision}; sha256={hash}; bytes={bytes}; reason={JsonSerializer.Serialize(reason)}");

    /// <summary>A stored revision as read (the document as stored, its exact bytes).</summary>
    private sealed class RevisionRow
    {
        public Guid Id { get; set; }
        public int Number { get; set; }
        public int Revisions { get; set; }
        public string ContentSha256 { get; set; }
        public DateTime FrozenUtc { get; set; }
        public string Reason { get; set; }
#pragma warning disable CA1819 // a row of a query: the stored document's bytes, hashed and parsed once
        public byte[] Document { get; set; }
#pragma warning restore CA1819
    }

    private sealed class DueRow
    {
        public Guid Campaign { get; set; }
        public string Site { get; set; }
    }

    #endregion
}
