using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;

namespace Ariva.Core.Services.Validation;

/// <summary>
/// Validation campaigns of a site (ARV-104a, formulas F18): plan one over the site's published zone profile version, its queue
/// zones, lines and local days; start it; close it (a critical action, step-up MFA on the endpoint); read campaigns and every
/// observer's manual counts. A site the caller does not reach answers NotFound before anything else is checked, and so does
/// a campaign of another site (CWE-863, CWE-204). Create, start and close are audited in the same unit of work.
/// </summary>
public interface ISvcValidationCampaigns : ISvcScoped
{
    Task<Result<PageViewModel<ValidationCampaignSummaryViewModel>>> SearchAsync(string siteCode, ValidationCampaignCriteria criteria, CancellationToken ct = default);

    Task<Result<ValidationCampaignViewModel>> GetAsync(string siteCode, Guid id, CancellationToken ct = default);

    Task<Result<ValidationCampaignViewModel>> CreateAsync(string siteCode, CreateValidationCampaignRequest request, CancellationToken ct = default);

    Task<Result<ValidationCampaignViewModel>> StartAsync(string siteCode, Guid id, CancellationToken ct = default);

    Task<Result<ValidationCampaignViewModel>> CloseAsync(string siteCode, Guid id, CancellationToken ct = default);

    Task<Result<PageViewModel<ManualCountViewModel>>> SearchCountsAsync(string siteCode, Guid id, ManualCountCriteria criteria, CancellationToken ct = default);
}
