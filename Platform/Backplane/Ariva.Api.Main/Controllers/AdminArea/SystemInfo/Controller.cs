using System.Reflection;
using Ariva.Core.Domain.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.AdminArea.SystemInfo;

/// <summary>
/// Product information for signed in users. It is the first protected endpoint, so tests can prove that default
/// deny answers 401 until the authentication story lands. That story replaces <c>[Authorize]</c> with the
/// <c>Permission</c> attribute.
/// </summary>
[ApiController]
[Authorize]
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
