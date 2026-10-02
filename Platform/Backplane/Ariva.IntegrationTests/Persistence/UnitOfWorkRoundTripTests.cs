using Ariva.IntegrationTests.Persistence.Samples;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using NHibernate.Linq;

namespace Ariva.IntegrationTests.Persistence;

/// <summary>
/// ARV-005: the NHibernate storage provider and unit of work against a real PostgreSQL (TimescaleDB image): audit
/// fields, a domain event written in the same transaction, soft delete, rollback, atomicity with the outbox,
/// parameterised SQL and quoting of reserved names.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class UnitOfWorkRoundTripTests(PostgresFixture fixture)
{
    private PersistenceHost Host => fixture.Host;

    private static string NewCode() => "Z-" + Guid.CreateVersion7().ToString("N")[..12];

    [Fact]
    public async Task EndAsync_Should_PersistEntityAuditFieldsAndDomainEvent_When_CommandPromisedToCommit()
    {
        var code = NewCode();
        var systemId = Guid.CreateVersion7();
        var openedAt = DateTime.UtcNow;

        var zoneId = await Host.InScopeAsync(async (uow, user) =>
        {
            user.SetSystemUser(systemId, "sensor-job");
            var zone = new SampleZone(code, "Arrivals A", SampleZoneKind.SnakeQueue);
            zone.Open(120, openedAt);
            await uow.StorageProvider.SaveAsync(zone);
            await uow.StorageProvider.SaveAsync(new SampleDesk(zone, 7));
            uow.PromiseToCommit();
            return zone.Id.Value;
        });

        await Host.InScopeAsync(async (uow, _) =>
        {
            var zone = await uow.StorageProvider.GetAsync<SampleZone>(zoneId);

            zone.Should().NotBeNull();
            zone.Id.Value.Version.Should().Be(7);
            zone.Kind.Should().Be(SampleZoneKind.SnakeQueue);
            zone.Capacity.Should().Be(120);
            zone.CreatedById.Should().Be(systemId);
            zone.CreatedBy.Should().Be("sensor-job");
            zone.CreatedOn.Should().NotBeNull();
            zone.CreatedOn.Value.Kind.Should().Be(DateTimeKind.Utc);
            zone.OpenedAt.Value.Kind.Should().Be(DateTimeKind.Utc);
            zone.OpenedAt.Value.Should().BeCloseTo(openedAt, TimeSpan.FromMilliseconds(1), "timestamptz keeps microseconds");
            zone.Desks.Should().ContainSingle().Which.Number.Should().Be(7);
            zone.DomainEvents.Should().BeEmpty();

            var outboxRows = await uow.StorageProvider.Query<SampleOutboxRow>().Where(row => row.PartitionKey == code).ToListAsync();
            outboxRows.Should().ContainSingle().Which.EventType.Should().Be(nameof(SampleZoneOpened));
            return true;
        });
    }

    [Fact]
    public async Task EndAsync_Should_StampModification_When_EntityChanges()
    {
        var code = NewCode();
        var zoneId = await SaveZoneAsync(code);

        await Host.InScopeAsync(async (uow, user) =>
        {
            user.SetSystemUser(Guid.CreateVersion7(), "duty-manager");
            var zone = await uow.StorageProvider.GetAsync<SampleZone>(zoneId);
            zone.Rename("Arrivals Alpha");
            await uow.StorageProvider.UpdateAsync(zone);
            uow.PromiseToCommit();
            return true;
        });

        await Host.InScopeAsync(async (uow, _) =>
        {
            var zone = await uow.StorageProvider.GetAsync<SampleZone>(zoneId);
            zone.Name.Should().Be("Arrivals Alpha");
            zone.ModifiedBy.Should().Be("duty-manager");
            zone.ModifiedOn.Should().BeOnOrAfter(zone.CreatedOn.Value);
            return true;
        });
    }

    [Fact]
    public async Task DeleteAsync_Should_HideRowButKeepIt_When_EntityIsSoftDeletable()
    {
        var code = NewCode();
        var zoneId = await SaveZoneAsync(code);

        await Host.InScopeAsync(async (uow, _) =>
        {
            var zone = await uow.StorageProvider.GetAsync<SampleZone>(zoneId);
            await uow.StorageProvider.DeleteAsync(zone);
            uow.PromiseToCommit();
            return true;
        });

        await Host.InScopeAsync(async (uow, _) =>
        {
            (await uow.StorageProvider.GetAsync<SampleZone>(zoneId)).Should().BeNull();
            (await uow.StorageProvider.Query<SampleZone>().CountAsync(zone => zone.Code == code)).Should().Be(0);

            var raw = await uow.StorageProvider.ExecuteSqlAsync<ZoneNameRow>(
                "select code as \"Code\", deleted_by as \"Name\" from sample_zone where code = :code and deleted_on is not null",
                new Dictionary<string, object> { ["code"] = code });
            raw.Should().ContainSingle().Which.Name.Should().Be("anonymous");
            return true;
        });
    }

    [Fact]
    public async Task EndAsync_Should_RollBack_When_NoCommandPromisedToCommit()
    {
        var code = NewCode();

        await Host.InScopeAsync(async (uow, _) =>
        {
            await uow.StorageProvider.SaveAsync(new SampleZone(code, "Never committed", SampleZoneKind.ServiceArea));
            return true;
        });

        (await CountZonesAsync(code)).Should().Be(0);
    }

    [Fact]
    public async Task EndAsync_Should_RollBackEntityChanges_When_OutboxWriteFails()
    {
        var code = NewCode();
        Host.Outbox.FailNextWrite = true;

        var act = () => Host.InScopeAsync(async (uow, _) =>
        {
            var zone = new SampleZone(code, "Atomic", SampleZoneKind.OverflowBand);
            zone.Open(5, DateTime.UtcNow);
            await uow.StorageProvider.SaveAsync(zone);
            uow.PromiseToCommit();
            return true;
        });

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await CountZonesAsync(code)).Should().Be(0, "the entity and its events commit together or not at all");
    }

    [Fact]
    public async Task ExecuteSqlAsync_Should_TreatInjectionPayloadAsData_When_PassedAsParameter()
    {
        var code = NewCode();
        await SaveZoneAsync(code);

        await Host.InScopeAsync(async (uow, _) =>
        {
            var injected = await uow.StorageProvider.ExecuteSqlAsync<ZoneNameRow>(
                "select code as \"Code\", name as \"Name\" from sample_zone where code = :code",
                new Dictionary<string, object> { ["code"] = "x' OR '1'='1" });
            var found = await uow.StorageProvider.ExecuteSqlAsync<ZoneNameRow>(
                "select code as \"Code\", name as \"Name\" from sample_zone where code = :code",
                new Dictionary<string, object> { ["code"] = code });

            injected.Should().BeEmpty();
            found.Should().ContainSingle().Which.Code.Should().Be(code);
            return true;
        });
    }

    [Fact]
    public async Task ExecuteSqlAsync_Should_BindEachListElementAsData_When_AListIsPassed()
    {
        var (a, b) = (NewCode(), NewCode());
        await SaveZoneAsync(a);
        await SaveZoneAsync(b);

        await Host.InScopeAsync(async (uow, _) =>
        {
            const string sql = "select code as \"Code\", name as \"Name\" from sample_zone where code in (:codes) order by code";
            var found = await uow.StorageProvider.ExecuteSqlAsync<ZoneNameRow>(sql, new Dictionary<string, object> { ["codes"] = new List<string> { a, b, "missing" } });
            var injected = await uow.StorageProvider.ExecuteSqlAsync<ZoneNameRow>(sql,
                new Dictionary<string, object> { ["codes"] = new List<string> { "x') OR ('1'='1", "x' OR '1'='1", a + "') --" } });
            var empty = () => uow.StorageProvider.ExecuteSqlAsync<ZoneNameRow>(sql, new Dictionary<string, object> { ["codes"] = new List<string>() });

            found.Select(r => r.Code).Should().Equal(new[] { a, b }.Order(StringComparer.Ordinal), "ARV-038: a list binds one parameter per element");
            injected.Should().BeEmpty("each element is a value, never SQL");
            (await empty.Should().ThrowAsync<ArgumentException>()).WithMessage("*'codes'*empty*");
            return true;
        });
    }

    [Fact]
    public async Task SaveAsync_Should_QuoteTableName_When_EntityNameIsReservedWord()
    {
        var userName = "officer." + Guid.CreateVersion7().ToString("N")[..8];

        await Host.InScopeAsync(async (uow, _) =>
        {
            await uow.StorageProvider.SaveAsync(new User(userName));
            uow.PromiseToCommit();
            return true;
        });

        await Host.InScopeAsync(async (uow, _) =>
        {
            (await uow.StorageProvider.Query<User>().CountAsync(user => user.UserName == userName)).Should().Be(1);
            return true;
        });
    }

    private Task<Guid> SaveZoneAsync(string code) =>
        Host.InScopeAsync(async (uow, _) =>
        {
            var zone = new SampleZone(code, "Arrivals A", SampleZoneKind.SnakeQueue);
            await uow.StorageProvider.SaveAsync(zone);
            uow.PromiseToCommit();
            return zone.Id.Value;
        });

    private Task<int> CountZonesAsync(string code) =>
        Host.InScopeAsync((uow, _) => uow.StorageProvider.Query<SampleZone>().CountAsync(zone => zone.Code == code));
}
