using System.Globalization;
using Ariva.Core.Domain.Common;
using FluentAssertions;

namespace Ariva.UnitTests.Domain.Common;

/// <summary>ARV-004: identity, equality, hash stability, domain events and hash keys of <see cref="EntityBase{T}"/>.</summary>
public sealed class EntityBaseTests
{
    #region Identity

    [Fact]
    public void NewId_Should_ReturnVersion7Guid_When_Called()
    {
        var id = SampleZone.NewId();

        id.Version.Should().Be(7);
        id.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void NewId_Should_ReturnUniqueIds_When_CalledManyTimes()
    {
        var ids = Enumerable.Range(0, 10_000).Select(_ => SampleZone.NewId()).ToList();

        ids.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void NewId_Should_SortByCreationTime_When_GeneratedInDifferentMilliseconds()
    {
        var first = SampleZone.NewId();
        Thread.Sleep(5);
        var second = SampleZone.NewId();

        string.CompareOrdinal(first.ToString("D"), second.ToString("D")).Should().BeNegative(
            "version 7 ids start with a millisecond timestamp, so later ids sort after earlier ones");
    }

    [Fact]
    public void IsTransient_Should_BeTrue_When_IdIsNullOrEmpty()
    {
        new SampleZone().IsTransient.Should().BeTrue();
        new SampleZone { Id = Guid.Empty }.IsTransient.Should().BeTrue();
        new SampleZone { Id = SampleZone.NewId() }.IsTransient.Should().BeFalse();
    }

    [Fact]
    public void GetId_Should_ReturnTypedId_When_IdIsSet()
    {
        var id = SampleZone.NewId();
        var zone = new SampleZone { Id = id };

        zone.GetId<Guid?>().Should().Be(id);
        zone.GetId().Should().Be(id);
    }

    #endregion

    #region Equality

    [Fact]
    public void Equals_Should_BeFalse_When_BothEntitiesAreTransient()
    {
        var a = new SampleZone();
        var b = new SampleZone();

        a.Equals(b).Should().BeFalse();
        (a == b).Should().BeFalse();
        a.Equals(a).Should().BeTrue();
    }

    [Fact]
    public void Equals_Should_BeTrue_When_IdsMatch()
    {
        var id = SampleZone.NewId();
        var a = new SampleZone { Id = id };
        var b = new SampleZone { Id = id };

        a.Equals(b).Should().BeTrue();
        (a == b).Should().BeTrue();
        (a != b).Should().BeFalse();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void Equals_Should_BeFalse_When_TypesDiffer()
    {
        var id = SampleZone.NewId();

        new SampleZone { Id = id }.Equals(new SampleDesk { Id = id }).Should().BeFalse();
    }

    [Fact]
    public void Equals_Should_BeFalse_When_OtherIsNull()
    {
        var zone = new SampleZone { Id = SampleZone.NewId() };

        zone.Equals(null).Should().BeFalse();
        (zone == null).Should().BeFalse();
    }

    [Fact]
    public void GetHashCode_Should_StayStable_When_TransientEntityReceivesId()
    {
        var zone = new SampleZone();
        var set = new HashSet<SampleZone> { zone };

        zone.Id = SampleZone.NewId();

        set.Contains(zone).Should().BeTrue("an entity added before save must still be found after it gets an id");
    }

    #endregion

    #region Domain Events

    [Fact]
    public void RaiseDomainEvent_Should_CollectEvents_When_EventsAreRaised()
    {
        var zone = new SampleZone();
        var first = new SampleZoneOpened();
        var second = new SampleZoneOpened();

        zone.RaiseDomainEvent(first);
        zone.RaiseDomainEvent(second);

        zone.DomainEvents.Should().Equal(first, second);
    }

    [Fact]
    public void RaiseDomainEvent_Should_Throw_When_EventIsNull()
    {
        var zone = new SampleZone();

        var act = () => zone.RaiseDomainEvent(null);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void DequeueDomainEvents_Should_ReturnAndClearEvents_When_Called()
    {
        var zone = new SampleZone();
        var opened = new SampleZoneOpened();
        zone.RaiseDomainEvent(opened);

        var dequeued = zone.DequeueDomainEvents();

        dequeued.Should().Equal(opened);
        zone.DomainEvents.Should().BeEmpty();
        zone.DequeueDomainEvents().Should().BeEmpty("a dispatcher must never receive the same event twice");
    }

    [Fact]
    public void DomainEvents_Should_NotBeModifiable_When_ReadByCaller()
    {
        var zone = new SampleZone();
        zone.RaiseDomainEvent(new SampleZoneOpened());

        var events = zone.DomainEvents;

        events.Should().BeAssignableTo<IReadOnlyList<Ariva.Core.Domain.Contracts.IEvent>>();
        (events as ICollection<Ariva.Core.Domain.Contracts.IEvent>)?.IsReadOnly.Should().BeTrue();
    }

    #endregion

    #region Hash Key

    [Fact]
    public void GenerateHashKey_Should_ReturnPinnedValue_When_InputIsKnown()
    {
        // SHA-256 of "zone-1|1.5|"; pinned so a change to normalisation is caught (stored keys would stop matching).
        SampleZone.Key("Zone-1", 1.5m).Should().Be("9E1C07C766877DE6A119F02ADBDCCA531FEF7C74DCEBB54F89B97094142BDB8B");
    }

    [Fact]
    public void GenerateHashKey_Should_IgnoreCaseAndOuterSpaces_When_PartsAreStrings()
    {
        SampleZone.Key("  ZONE-1 ", "Desk").Should().Be(SampleZone.Key("zone-1", "desk"));
    }

    [Fact]
    public void GenerateHashKey_Should_Differ_When_BoundaryBetweenPartsMoves()
    {
        SampleZone.Key("ab", "c").Should().NotBe(SampleZone.Key("a", "bc"));
    }

    [Fact]
    public void GenerateHashKey_Should_IgnoreCurrentCulture_When_PartsAreNumbers()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var german = SampleZone.Key("zone-1", 1.5m);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-JO");
            var arabic = SampleZone.Key("zone-1", 1.5m);

            german.Should().Be(arabic);
            german.Should().Be(SampleZone.Key("zone-1", "1.5"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void GenerateHashKey_Should_TreatNullAsEmpty_When_PartIsNull()
    {
        SampleZone.Key("zone-1", null).Should().Be(SampleZone.Key("zone-1", string.Empty));
    }

    #endregion
}
