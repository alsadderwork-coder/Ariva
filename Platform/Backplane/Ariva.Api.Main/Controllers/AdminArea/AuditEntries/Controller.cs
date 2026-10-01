using Ariva.Api.Common.Security;
using Ariva.Core;
using Ariva.Core.Domain.Criteria;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Administration;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.AdminArea.AuditEntries;

/// <summary>
/// The audit trail, read-only (ARV-011). There is deliberately no create, update or delete endpoint; the database
/// refuses UPDATE and DELETE on audit_entry for the runtime login as well.
/// </summary>
[ApiController]
[Route("api/v1/admin/audit-entries")]
public sealed class AuditEntriesController(ISvcAuditEntries entries) : ControllerBase
{
    [HttpGet]
    [Permission(nameof(Global.Defaults.Permissions.SearchAuditEntry))]
    [ProducesResponseType<PageViewModel<AuditEntryViewModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Search([FromQuery] AuditEntryCriteria criteria, CancellationToken ct)
    {
        var result = await entries.SearchAsync(criteria, ct);
        return result.HasErrors ? AdministrationProblems.For(this, result.ErrorMessages) : Ok(result.Data);
    }

    [HttpGet("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.ViewAuditEntry))]
    [ProducesResponseType<AuditEntryViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var result = await entries.GetAsync(id, ct);
        return result.HasErrors ? AdministrationProblems.For(this, result.ErrorMessages) : Ok(result.Data);
    }
}
