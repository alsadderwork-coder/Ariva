using Ariva.Core.Availability;
using Ariva.Core.Domain.Criteria;
using Ariva.Core.Security;
using Ariva.Core.Services.Quality;
using Ariva.Infra.Services.Foundation;
using Ariva.Infra.Services.Reports;

namespace Ariva.Infra.Services.Quality;

/// <summary>
/// Reads a site's availability ledger (ARV-118, <see cref="ISvcAvailability"/>, script 0042): the site first (outside the
/// caller's sites, or unknown, answers NotFound, CWE-204, CWE-863), then the range (<see cref="AvailabilityCriteria"/>,
/// at most 92 local days). One constant, parameterised query (CWE-89, <see cref="AvailabilityLedgerReader"/>, shared with the
/// weekly summary of ARV-119) counts the ledger's minutes per local date as the ledger recorded it; weeks and the range are
/// sums of the days, and the ratio is available operating minutes over operating minutes (<see cref="AvailabilitySummary"/>).
/// </summary>
internal sealed class SvcAvailability(IUnitOfWork unitOfWork, ICurrentUser currentUser, TimeProvider timeProvider, ISiteScope siteScope, ReportReader reader,
    AvailabilityLedgerReader ledger)
    : SvcBase(unitOfWork, currentUser, timeProvider), ISvcAvailability
{
    public async Task<Result<AvailabilityViewModel>> GetAsync(string siteCode, AvailabilityCriteria criteria, CancellationToken ct = default)
    {
        if (siteCode is null || !Site.IsValidCode(siteCode) || !(await siteScope.GetAsync(ct)).Allows(siteCode) || !await reader.SiteExistsAsync(siteCode, ct))
            return Result.Error<AvailabilityViewModel>(AvailabilityErrors.NotFound);
        criteria ??= new AvailabilityCriteria(null, null);
        var valid = await AvailabilityCriteria.Rules().ValidateAllAsync(criteria);
        if (valid.HasErrors || AvailabilityCriteria.Bounds(criteria) is not { } range)
            return Result.Error<AvailabilityViewModel>(valid.ErrorMessages.FirstOrDefault() ?? AvailabilityCriteria.InvalidRange);

        var zone = await reader.TimeZoneAsync(siteCode, ct);
        var counts = await ledger.CountsByDayAsync(siteCode, range.From, range.To, ct);
        var days = AvailabilitySummary.Days(range.From, range.To, counts);
        return new Result<AvailabilityViewModel>(new AvailabilityViewModel(siteCode, zone.Id, range.From, range.To, AvailabilitySummary.PilotTarget,
            AvailabilitySummary.Total(days), AvailabilitySummary.Weeks(days), days));
    }
}
