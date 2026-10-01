using System.Reflection;
using Ariva.Core.Domain.ViewModels;
using Ariva.Api.Common.Security;
using Ariva.Core;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.AdminArea.SystemInfo;

/// <summary>
/// Product name and version, for system administrators only (ViewSystemInfo): operators have no need for the build
/// version, and it helps an attacker pick exploits (CWE-200). The first [Permission] endpoint, so the permission matrix
/// tests prove 401, 403 and 200 end to end.
/// </summary>
[ApiController]
[Permission(nameof(Global.Defaults.Permissions.ViewSystemInfo))]
[Route("api/v1/system")]
public sealed class SystemInfoController : ControllerBase
{
    #region Fields

    private const string ProductName = "Ariva";

    private static readonly string ProductVersion = ReadVersion();

    #endregion

    #region Endpoints

    /// <summary>Returns the product name and version.</summary>
    /// <returns>The product information.</returns>
    [HttpGet("info")]
    [ProducesResponseType<SystemInfoViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<SystemInfoViewModel> GetInfo() => Ok(new SystemInfoViewModel(ProductName, ProductVersion));

    #endregion

    #region Helpers

    private static string ReadVersion()
    {
        var assembly = typeof(SystemInfoController).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = string.IsNullOrWhiteSpace(informational)
            ? assembly.GetName().Version?.ToString(3) ?? "0.0.0"
            : informational;

        // Build metadata after '+' (the commit id) is not shown to clients.
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? version : version[..plus];
    }

    #endregion
}
