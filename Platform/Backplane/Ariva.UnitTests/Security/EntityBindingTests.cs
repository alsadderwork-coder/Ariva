using System.Reflection;
using Ariva.UnitTests.Security.Fixtures.Domain.Entities;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.UnitTests.Security;

/// <summary>
/// CWE-501: domain entities are never bound from a request. Controllers bind Create*/Update* request models, validate
/// them and map them; an entity parameter would let a caller set fields such as ids, site codes or audit columns.
/// </summary>
public sealed class EntityBindingTests
{
    #region Fields

    private const string EntityNamespace = "Ariva.Core.Domain.Entities";

    private static readonly string FixtureEntityNamespace = typeof(FixtureZone).Namespace;

    #endregion

    #region Tests

    [Fact]
    public void GetParameters_Should_ContainNoDomainEntity_When_ActionIsOnAnyArivaHost()
    {
        var controllers = ArivaAssemblies.Hosts.SelectMany(assembly => assembly.GetTypes());

        var violations = FindEntityBindings(controllers, EntityNamespace);

        violations.Should().BeEmpty("controllers bind request models, never domain entities (CWE-501)");
    }

    [Fact]
    public void FindEntityBindings_Should_ReportEveryEntityParameter_When_ControllerBindsEntities()
    {
        var violations = FindEntityBindings([typeof(FixtureZonesController)], FixtureEntityNamespace);

        violations.Should().BeEquivalentTo(new[]
        {
            "FixtureZonesController.Create(zone: FixtureZone)",
            "FixtureZonesController.ReplaceAll(zones: List`1)",
            "FixtureZonesController.Import(zones: FixtureZone[])"
        });
    }

    [Fact]
    public void FindEntityBindings_Should_ReportNothing_When_ControllerBindsRequestModels()
    {
        var violations = FindEntityBindings([typeof(FixtureRequestsController)], FixtureEntityNamespace);

        violations.Should().BeEmpty();
    }

    #endregion

    #region Rule

    /// <summary>
    /// Lists every public action parameter of the given controllers whose type, element type or generic argument
    /// lives in <paramref name="entityNamespace"/> or below it.
    /// </summary>
    private static List<string> FindEntityBindings(IEnumerable<Type> types, string entityNamespace) =>
        types
            .Where(IsController)
            .SelectMany(controller => Actions(controller)
                .SelectMany(action => action.GetParameters()
                    .Where(parameter => References(parameter.ParameterType, entityNamespace))
                    .Select(parameter => $"{controller.Name}.{action.Name}({parameter.Name}: {parameter.ParameterType.Name})")))
            .ToList();

    private static bool IsController(Type type) =>
        type.IsClass && !type.IsAbstract && typeof(ControllerBase).IsAssignableFrom(type);

    private static IEnumerable<MethodInfo> Actions(Type controller) =>
        controller
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => !method.IsSpecialName
                             && method.DeclaringType != typeof(object)
                             && method.DeclaringType != typeof(ControllerBase)
                             && method.DeclaringType != typeof(Controller)
                             && !method.IsDefined(typeof(NonActionAttribute), inherit: true));

    private static bool References(Type type, string entityNamespace)
    {
        if (type.HasElementType)
        {
            return References(type.GetElementType(), entityNamespace);
        }

        if (type.Namespace == entityNamespace || (type.Namespace?.StartsWith(entityNamespace + ".", StringComparison.Ordinal) ?? false))
        {
            return true;
        }

        return type.IsGenericType && type.GetGenericArguments().Any(argument => References(argument, entityNamespace));
    }

    #endregion

    #region Fixtures

    /// <summary>Request model in the test namespace, outside any entities namespace.</summary>
    public sealed record CreateFixtureZoneRequest(string Name);

    /// <summary>
    /// Test only controller that binds entities in every way the rule must catch. It is authorized like a real
    /// controller, so the entity binding is its only defect.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("fixtures/zones")]
    public sealed class FixtureZonesController : ControllerBase
    {
        [Authorize]
        [HttpPost]
        public IActionResult Create([FromBody] FixtureZone zone) => Ok(zone?.Id);

        [Authorize]
        [HttpPut]
        public IActionResult ReplaceAll([FromBody] List<FixtureZone> zones) => Ok(zones?.Count);

        [Authorize]
        [HttpPost("import")]
        public IActionResult Import([FromBody] FixtureZone[] zones) => Ok(zones?.Length);

        [NonAction]
        public static FixtureZone Map(CreateFixtureZoneRequest request) => new() { Name = request?.Name };

        [NonAction]
        public void Touch(FixtureZone zone) => zone?.Name?.Trim();
    }

    /// <summary>Test only controller that binds request models only.</summary>
    [ApiController]
    [Authorize]
    [Route("fixtures/requests")]
    public sealed class FixtureRequestsController : ControllerBase
    {
        [Authorize]
        [HttpPost]
        public IActionResult Create([FromBody] CreateFixtureZoneRequest request) => Ok(request?.Name);

        [Authorize]
        [HttpGet("{id:guid}")]
        public IActionResult Get(Guid id) => Ok(id);
    }

    #endregion
}
