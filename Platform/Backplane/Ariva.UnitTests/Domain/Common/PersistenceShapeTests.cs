using System.Reflection;
using Ariva.Core.Domain.Common;
using FluentAssertions;

namespace Ariva.UnitTests.Domain.Common;

/// <summary>
/// NHibernate (ARV-005) proxies entities for lazy loading, which needs every public instance member of the base
/// classes to be virtual. A non-virtual member would fail at session factory build time; this catches it earlier.
/// </summary>
public sealed class PersistenceShapeTests
{
    public static TheoryData<Type> BaseTypes =>
    [
        typeof(EntityBase<>),
        typeof(BaseAuditableEntity<>),
        typeof(BaseSoftDeletableEntity<>)
    ];

    [Theory]
    [MemberData(nameof(BaseTypes))]
    public void GetMethods_Should_BeVirtual_When_MemberIsPublicInstance(Type baseType)
    {
        var nonVirtual = baseType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsVirtual || method.IsFinal)
            .Select(method => method.Name)
            .ToList();

        nonVirtual.Should().BeEmpty($"{baseType.Name} is a base for NHibernate mapped entities");
    }
}
