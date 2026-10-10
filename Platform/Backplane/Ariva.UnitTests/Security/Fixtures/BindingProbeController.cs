using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Ariva.Api.Common.Security;
using Ariva.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.UnitTests.Security;

/// <summary>
/// Fixture: an MVC controller that <see cref="ErrorHandlingTests"/> mounts (<see cref="Mount"/>) in a host whose own endpoints
/// bind no typed route or query value and no JSON body through MVC (Ingest reads its pushes itself; Integration binds one
/// anonymous body), so the shared <c>AddAppControllers</c> settings are proven on every host: a route or query value that does
/// not bind and a JSON body that does not parse answer 400 without repeating what was sent (ARV-104b, CWE-501). Never mounted
/// outside that test; it holds a permission like any action.
/// </summary>
[ApiController]
[Route("fixture/binding")]
public sealed class BindingProbeController : ControllerBase
{
    #region Actions

    [HttpGet("{number}")]
    [Permission(nameof(Global.Defaults.Permissions.ViewSite))]
    public IActionResult Values(int number, [FromQuery] Guid? id, [FromQuery] DateTime? from) => Ok(new { number, id, from });

    [HttpPost("body")]
    [Permission(nameof(Global.Defaults.Permissions.ViewSite))]
    public IActionResult Body([FromBody] BindingProbeRequest request) => Ok(request);

    #endregion

    #region Mounting

    /// <summary>Adds this controller, and no other type of the test assembly, to the host's MVC controllers.</summary>
    public static void Mount(IServiceCollection services) =>
        services.AddMvcCore().ConfigureApplicationPartManager(manager => manager.FeatureProviders.Add(new Feature()));

    private sealed class Feature : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            var probe = typeof(BindingProbeController).GetTypeInfo();
            if (!feature.Controllers.Contains(probe))
                feature.Controllers.Add(probe);
        }
    }

    #endregion
}

/// <summary>Fixture: the probe's body.</summary>
public sealed record BindingProbeRequest(Guid? Id, [property: MaxLength(16)] string Name);
