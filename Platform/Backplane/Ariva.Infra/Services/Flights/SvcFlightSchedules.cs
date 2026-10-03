using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Flights;
using Ariva.Core.Services.Flights;
using Ariva.Infra.Flights.Aidx;
using Ariva.Infra.Flights.Ssim;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Foundation;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NHibernate.Linq;

namespace Ariva.Infra.Services.Flights;

/// <summary>
/// SSIM imports (ARV-046). The file is read as a stream (<see cref="SsimReader"/>), expanded for the site's airports
/// within the horizon, and either summarised (preview, with a token that binds the file's SHA-256, the site, the horizon,
/// the window and the user for two hours) or, when the token matches, applied through
/// <see cref="ISvcFlightIntake.ApplyScheduleLegsAsync"/> as the fallback feed <c>ssim</c>: it creates legs and updates only
/// legs a schedule file set, never one a live feed has reported. Legs go in units of 500, each in its own transaction (a
/// whole season in one transaction would hold tens of thousands of leg locks); a unit that fails is rolled back, and
/// once the first unit is committed the rest are applied even if the caller goes away. The import is audited in its
/// own transaction whatever happens, with the counts and the outcome (completed, or failed after N units).
/// </summary>
internal sealed class SvcFlightSchedules(IUnitOfWork unitOfWork, ICurrentUser currentUser, TimeProvider timeProvider, IServiceScopeFactory scopes,
    IDataProtectionProvider dataProtection, ILogger<SvcFlightSchedules> logger)
    : SvcBase(unitOfWork, currentUser, timeProvider), ISvcFlightSchedules
{
    public const string Feed = "ssim";
    public const string PreviewPurpose = "Ariva.FlightSchedules.Preview.v1";
    public static readonly TimeSpan PreviewLifetime = TimeSpan.FromHours(2);

    // Not the time-limited protector: it reads the system clock, and Ariva's clock is the TimeProvider.
    private readonly IDataProtector _previews = dataProtection.CreateProtector(PreviewPurpose);

    public Task<bool> SiteExistsAsync(string siteCode, CancellationToken ct = default) =>
        siteCode is null ? Task.FromResult(false) : Query<Site>().AnyAsync(s => s.Code == siteCode, ct);

    public async Task<Result<SsimPreviewViewModel>> PreviewAsync(string siteCode, Stream file, int horizonDays, CancellationToken ct = default)
    {
        var (schedule, error) = await ReadAsync(siteCode, file, horizonDays, ct);
        if (error is not null)
            return Result.Error<SsimPreviewViewModel>(error);
        var arrivals = schedule.Legs.Count(l => l.Direction == "Arrival");
        var token = _previews.Protect(Binding(schedule, siteCode, horizonDays) + "|" + UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
        return new Result<SsimPreviewViewModel>(new SsimPreviewViewModel(schedule.Sha256, token, schedule.Lines, schedule.LegRecords, schedule.LegRecordsOfSite,
            schedule.Legs.Count, arrivals, schedule.Legs.Count - arrivals, schedule.From, schedule.To,
            [.. schedule.Legs.OrderBy(l => l.ScheduledUtc).Take(20).Select(l => new SsimLegViewModel(l.FlightKey, l.Direction, l.ScheduledUtc!.Value, l.Origin, l.Destination))],
            [.. schedule.Errors.Select(e => new SsimLineErrorViewModel(e.Line, e.Reason))], schedule.ErrorCount));
    }

    public async Task<Result<SsimImportViewModel>> ImportAsync(string siteCode, Stream file, int horizonDays, string previewToken, CancellationToken ct = default)
    {
        var (schedule, error) = await ReadAsync(siteCode, file, horizonDays, ct);
        if (error is not null)
            return Result.Error<SsimImportViewModel>(error);
        if (!IsThePreview(previewToken, Binding(schedule, siteCode, horizonDays)))
            return Result.Error<SsimImportViewModel>(FlightScheduleErrors.NotThePreview);

        var source = FlightRules.Micro(UtcNow);
        var (applied, unchanged, refused, units) = (0, 0, 0, 0);
        var outcome = "failed";
        try
        {
            foreach (var chunk in System.Linq.Enumerable.Chunk(schedule.Legs, FlightRules.MaxBatch))
            {
                // Until something is committed the caller may still cancel; after that the import runs to its end.
                var unitCt = units == 0 ? ct : CancellationToken.None;
                await using var scope = scopes.CreateAsyncScope();
                var chunkUnitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                IReadOnlyList<FlightItemResult> results;
                try
                {
                    results = await scope.ServiceProvider.GetRequiredService<ISvcFlightIntake>().ApplyScheduleLegsAsync(siteCode, Feed, chunk, source, unitCt);
                    chunkUnitOfWork.PromiseToCommit();
                    // The commit itself is never cancelled: a commit cut short would leave its outcome, and the audit's count, unknown.
                    await chunkUnitOfWork.EndAsync(CancellationToken.None);
                }
                catch
                {
                    // Disposing a unit that was never ended commits what was promised: a unit that failed part way is rolled back.
                    await chunkUnitOfWork.RollbackAsync(CancellationToken.None);
                    throw;
                }

                units++;
                applied += results.Count(r => r.Applied);
                refused += results.Count(r => r.HasErrors);
                unchanged += results.Count(r => !r.Applied && !r.HasErrors);
            }

            outcome = "completed";
        }
        finally
        {
            try
            {
                await AuditAsync(siteCode, JsonSerializer.Serialize(new
                {
                    feed = Feed, sha256 = schedule.Sha256, horizonDays, from = schedule.From, to = schedule.To, outcome, unitsCommitted = units,
                    legs = schedule.Legs.Count, applied, unchanged, refused, lineErrors = schedule.ErrorCount
                }));
            }
            catch (Exception e) when (outcome != "completed")
            {
                // The import's own failure is what the caller sees; the audit failure is logged beside it.
                logger.LogError(e, "The audit entry of a failed SSIM import of {Site} could not be written ({Units} units committed)", siteCode, units);
            }
        }

        return new Result<SsimImportViewModel>(new SsimImportViewModel(schedule.Sha256, schedule.Legs.Count, applied, unchanged, refused, schedule.ErrorCount));
    }

    // The audit entry in a transaction of its own, so it is written whether the import completed or failed part way.
    private async Task AuditAsync(string siteCode, string summary)
    {
        await using var scope = scopes.CreateAsyncScope();
        var auditUnitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        try
        {
            // This request's caller, explicitly: the new scope is not the request's.
            await new AuditTrail(auditUnitOfWork, CurrentUser, timeProvider).RecordAsync("FlightSchedule.Imported", "Site", null, siteCode, null, summary, CancellationToken.None);
            auditUnitOfWork.PromiseToCommit();
            await auditUnitOfWork.EndAsync(CancellationToken.None);
        }
        catch
        {
            await auditUnitOfWork.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    // What the preview showed and the import must match: the file, the site, the horizon, the window and the user.
    private string Binding(SsimSchedule schedule, string siteCode, int horizonDays) =>
        string.Join('|', schedule.Sha256, siteCode, horizonDays.ToString(CultureInfo.InvariantCulture), schedule.From.ToString("O", CultureInfo.InvariantCulture),
            schedule.To.ToString("O", CultureInfo.InvariantCulture), CurrentUser.Id?.ToString("N") ?? "-");

    private bool IsThePreview(string token, string binding)
    {
        if (string.IsNullOrEmpty(token))
            return false;
        try
        {
            var bound = _previews.Unprotect(token);
            var cut = bound.LastIndexOf('|');
            if (cut < 0 || !long.TryParse(bound.AsSpan(cut + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
                return false;
            var age = UtcNow - new DateTime(ticks, DateTimeKind.Utc);
            return age <= PreviewLifetime && age >= -TimeSpan.FromMinutes(5)
                && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(bound[..cut]), System.Text.Encoding.UTF8.GetBytes(binding));
        }
        catch (CryptographicException)
        {
            return false; // altered, or of another key ring
        }
    }

    private async Task<(SsimSchedule Schedule, string Error)> ReadAsync(string siteCode, Stream file, int horizonDays, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        var rows = await ExecuteSqlAsync<CodeRow>("""
            SELECT DISTINCT a.iata_code AS "Iata", a.icao_code AS "Icao"
              FROM airport a JOIN terminal t ON t.airport_id = a.id
             WHERE t.site_code = :site AND t.deleted_on IS NULL AND a.deleted_on IS NULL
            """, new Dictionary<string, object> { ["site"] = siteCode ?? string.Empty }, ct);
        var airports = AidxReader.Airports(rows.SelectMany(r => new[] { r.Iata, r.Icao }));
        if (airports.Count == 0)
            return (null, "The site has no airport (a terminal of the site under an airport); SSIM legs are placed by airport.");
        return await SsimReader.ReadAsync(file, airports, UtcNow, horizonDays, ct);
    }

    private sealed class CodeRow
    {
        public string Iata { get; set; }
        public string Icao { get; set; }
    }
}
