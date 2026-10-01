using FluentAssertions;

namespace Ariva.UnitTests.Domain.Common;

/// <summary>ARV-004: audit stamps and soft delete of the auditable base classes.</summary>
public sealed class AuditableEntityTests
{
    private static readonly DateTime Created = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly Guid UserId = Guid.CreateVersion7();

    #region Audit Stamps

    [Fact]
    public void StampCreated_Should_SetCreatedFields_When_InstantIsUtc()
    {
        var zone = new SampleZone();

        zone.StampCreated(UserId, "supervisor1", Created);

        zone.CreatedById.Should().Be(UserId);
        zone.CreatedBy.Should().Be("supervisor1");
        zone.CreatedOn.Should().Be(Created);
        zone.ModifiedOn.Should().BeNull();
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void StampCreated_Should_Throw_When_InstantIsNotUtc(DateTimeKind kind)
    {
        var zone = new SampleZone();

        var act = () => zone.StampCreated(UserId, "supervisor1", DateTime.SpecifyKind(Created, kind));

        act.Should().Throw<ArgumentException>();
        zone.CreatedOn.Should().BeNull();
    }

    [Fact]
    public void StampModified_Should_SetModifiedFields_When_AfterCreation()
    {
        var zone = new SampleZone();
        zone.StampCreated(UserId, "supervisor1", Created);
        var modifier = Guid.CreateVersion7();

        zone.StampModified(modifier, "manager2", Created.AddMinutes(5));

        zone.ModifiedById.Should().Be(modifier);
        zone.ModifiedBy.Should().Be("manager2");
        zone.ModifiedOn.Should().Be(Created.AddMinutes(5));
        zone.CreatedBy.Should().Be("supervisor1");
    }

    [Fact]
    public void StampModified_Should_Throw_When_InstantPrecedesCreation()
    {
        var zone = new SampleZone();
        zone.StampCreated(UserId, "supervisor1", Created);

        var act = () => zone.StampModified(UserId, "supervisor1", Created.AddSeconds(-1));

        act.Should().Throw<ArgumentOutOfRangeException>();
        zone.ModifiedOn.Should().BeNull();
    }

    [Fact]
    public void StampModified_Should_Throw_When_InstantIsLocal()
    {
        var zone = new SampleZone();

        var act = () => zone.StampModified(UserId, "supervisor1", DateTime.Now);

        act.Should().Throw<ArgumentException>();
    }

    #endregion

    #region Soft Delete

    [Fact]
    public void SoftDelete_Should_MarkDeleted_When_Called()
    {
        var zone = new SampleZone();

        zone.SoftDelete("admin", Created);

        zone.IsDeleted.Should().BeTrue();
        zone.DeletedBy.Should().Be("admin");
        zone.DeletedOn.Should().Be(Created);
    }

    [Fact]
    public void SoftDelete_Should_KeepFirstDeletion_When_CalledTwice()
    {
        var zone = new SampleZone();
        zone.SoftDelete("admin", Created);

        zone.SoftDelete("other", Created.AddHours(1));

        zone.DeletedBy.Should().Be("admin");
        zone.DeletedOn.Should().Be(Created);
    }

    [Fact]
    public void SoftDelete_Should_Throw_When_InstantIsNotUtc()
    {
        var zone = new SampleZone();

        var act = () => zone.SoftDelete("admin", DateTime.Now);

        act.Should().Throw<ArgumentException>();
        zone.IsDeleted.Should().BeFalse();
    }

    [Fact]
    public void Restore_Should_ClearDeletion_When_EntityWasDeleted()
    {
        var zone = new SampleZone();
        zone.SoftDelete("admin", Created);

        zone.Restore();

        zone.IsDeleted.Should().BeFalse();
        zone.DeletedBy.Should().BeNull();
        zone.DeletedOn.Should().BeNull();
    }

    #endregion
}
