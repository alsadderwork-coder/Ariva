namespace Ariva.UnitTests.Security.Fixtures.Domain.Entities;

/// <summary>
/// Stand in for a domain entity, in its own entities namespace, used by <c>EntityBindingTests</c> to prove the rule
/// reports a controller that binds an entity directly.
/// </summary>
public sealed class FixtureZone
{
    /// <summary>Identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Display name.</summary>
    public string Name { get; set; }

    /// <summary>Owning site; binding it from a request would let a caller move the zone to another site.</summary>
    public string SiteCode { get; set; }
}
