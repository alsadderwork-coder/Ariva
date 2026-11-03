using System.Data;
using Ariva.IntegrationTests.Persistence.Samples;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using NHibernate.Linq;
using Npgsql;

namespace Ariva.IntegrationTests.Persistence;

/// <summary>
/// ARV-005: the NHibernate storage provider and unit of work against a real PostgreSQL (TimescaleDB image): audit
/// fields, a domain event written in the same transaction, soft delete, rollback, atomicity with the outbox,
/// parameterised SQL and quoting of reserved names; the typed reader (ARV-228b, <c>ReadSqlAsync</c>): rows mapped by position, a
/// list bound as one array, null and UTC times bound and other times refused, the session's own transaction, and a session
/// that stays usable after a read outside a transaction or a map that throws.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class UnitOfWorkRoundTripTests(PostgresFixture fixture)
{
    private PersistenceHost Host => fixture.Host;

    // The random end of a version 7 id: its first 12 digits are its millisecond, so two codes made in the same millisecond were equal
    // (ARV-228b found it: a test of two codes then held one code twice).
    private static string NewCode() => "Z-" + Guid.CreateVersion7().ToString("N")[^12..];

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
    public async Task ReadSqlAsync_Should_MapRowsByPositionAndBindAListAsOneArray_When_KeysArePassed()
    {
        var (a, b) = (NewCode(), NewCode());
        await SaveZoneAsync(a);
        await SaveZoneAsync(b);

        await Host.InScopeAsync(async (uow, _) =>
        {
            const string sql = "select code, name from sample_zone where code = ANY(:codes) order by code";
            var storage = uow.StorageProvider;
            var found = await storage.ReadSqlAsync(sql, new Dictionary<string, object> { ["codes"] = new List<string> { b, a, "missing" } },
                r => (Code: r.GetString(0), Name: r.GetString(1)));
            var asArray = await storage.ReadSqlAsync(sql, new Dictionary<string, object> { ["codes"] = new[] { a } }, r => r.GetString(0));
            var injected = await storage.ReadSqlAsync(sql,
                new Dictionary<string, object> { ["codes"] = new List<string> { "x') OR ('1'='1", "x' OR '1'='1", a + "'] --" } }, r => r.GetString(0));
            var empty = await storage.ReadSqlAsync(sql, new Dictionary<string, object> { ["codes"] = new List<string>() }, r => r.GetString(0));

            found.Should().Equal(new[] { (a, "Arrivals A"), (b, "Arrivals A") }.OrderBy(r => r.Item1, StringComparer.Ordinal),
                "each row is mapped by position, in the statement's order");
            asArray.Should().Equal([a]);
            injected.Should().BeEmpty("each element is a value of the array, never SQL");
            empty.Should().BeEmpty("an empty array matches nothing: = ANY of no value is valid SQL, unlike IN ()");
            return true;
        });
    }

    [Fact]
    public async Task ReadSqlAsync_Should_BindNullAndAUtcTimeAndRefuseAnyOtherTime_When_ValuesArePassed()
    {
        var at = new DateTime(2026, 9, 28, 17, 0, 0, DateTimeKind.Utc);
        const string sql = "select CAST(:missing AS integer) IS NULL, CAST(:at AS timestamptz)";

        await Host.InScopeAsync(async (uow, _) =>
        {
            var storage = uow.StorageProvider;
            var row = (await storage.ReadSqlAsync(sql, new Dictionary<string, object> { ["missing"] = null, ["at"] = at }, r => (Null: r.GetBoolean(0), At: r.GetDateTime(1))))
                .Should().ContainSingle().Subject;
            row.Null.Should().BeTrue("null binds as SQL NULL");
            row.At.Should().Be(at);
            row.At.Kind.Should().Be(DateTimeKind.Utc);

            foreach (var kind in new[] { DateTimeKind.Unspecified, DateTimeKind.Local })
            {
                var other = () => storage.ReadSqlAsync(sql, new Dictionary<string, object> { ["missing"] = null, ["at"] = DateTime.SpecifyKind(at, kind) }, r => r.GetBoolean(0));
                (await other.Should().ThrowAsync<ArgumentException>()).WithMessage("*'at'*not UTC*", $"a {kind} time would be read in the server's zone");
            }

            var noStatement = () => storage.ReadSqlAsync(" ", null, r => r.GetInt32(0));
            var noMap = () => storage.ReadSqlAsync<int>("select 1", null, null);
            await noStatement.Should().ThrowAsync<ArgumentException>();
            await noMap.Should().ThrowAsync<ArgumentNullException>();
            return true;
        });
    }

    [Fact]
    public async Task ReadSqlAsync_Should_ReadInTheUnitOfWorksTransaction_When_OneIsOpen()
    {
        var code = NewCode();

        await Host.InScopeAsync(async (uow, _) =>
        {
            var storage = uow.StorageProvider;
            await storage.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            await storage.SaveAsync(new SampleZone(code, "Not committed", SampleZoneKind.ServiceArea));
            await storage.FlushAsync();

            var seen = await storage.ReadSqlAsync("select name from sample_zone where code = :code", new Dictionary<string, object> { ["code"] = code }, r => r.GetString(0));

            seen.Should().Equal(["Not committed"], "the read is the session's own command, enlisted in its transaction");
            storage.IsTransactionActive().Should().BeTrue("a read in a transaction leaves it as it was");
            return true;
        });

        (await CountZonesAsync(code)).Should().Be(0, "nothing promised to commit, so the row the read saw was rolled back");
    }

    [Fact]
    public async Task ReadSqlAsync_Should_LeaveTheSessionUsable_When_ItReadsOutsideATransactionOrItsMapThrows()
    {
        var code = NewCode();
        await SaveZoneAsync(code);
        var parameters = new Dictionary<string, object> { ["code"] = code };

        await Host.InScopeAsync(async (uow, _) =>
        {
            var storage = uow.StorageProvider;
            var failing = () => storage.ReadSqlAsync<string>("select name from sample_zone where code = :code", parameters, _ => throw new InvalidOperationException("map"));
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            var cancelledRead = () => storage.ReadSqlAsync("select name from sample_zone where code = :code", parameters, r => r.GetString(0), cancelled.Token);

            await failing.Should().ThrowAsync<InvalidOperationException>();
            await cancelledRead.Should().ThrowAsync<OperationCanceledException>();
            (await storage.ReadSqlAsync("select name from sample_zone where code = :code", parameters, r => r.GetString(0))).Should().Equal(["Arrivals A"]);
            storage.IsTransactionActive().Should().BeFalse("a read outside a transaction opens none");
            (await storage.Query<SampleZone>().CountAsync(zone => zone.Code == code)).Should().Be(1, "the session's own queries still run");
            await storage.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            (await storage.ReadSqlAsync("select 1", null, r => r.GetInt32(0))).Should().Equal([1], "and a transaction begins on the connection after them");
            return true;
        });
    }

    [Fact]
    public async Task ReadSqlAsync_Should_HandItsConnectionBack_When_ItReadsOutsideATransaction()
    {
        // A pool of two connections (the least a host takes) and a 2-second wait for one: while the session kept its connection
        // after a read, the second of two more connections from the same pool would wait out the 2 seconds and be refused.
        await using var host = fixture.PersistenceHostWith(new Dictionary<string, string> { ["Database:MaxPoolSize"] = "2", ["Database:ConnectTimeoutSeconds"] = "2" });
        const string Rows = "select n from generate_series(1, 20000) n";

        await host.InScopeAsync(async (uow, _) =>
        {
            var storage = uow.StorageProvider;
            (await storage.ReadSqlAsync(Rows, null, r => r.GetInt32(0))).Should().HaveCount(20000);
            await TwoMoreConnectionsOpenAsync(host.ConnectionString, "after a read that succeeded");

            var failing = () => storage.ReadSqlAsync<int>(Rows, null, _ => throw new InvalidOperationException("map"));
            await failing.Should().ThrowAsync<InvalidOperationException>();
            await TwoMoreConnectionsOpenAsync(host.ConnectionString, "after a map that threw");

            using var cancelling = new CancellationTokenSource();
            var mapped = 0;
            var cancelled = () => storage.ReadSqlAsync(Rows, null, r =>
            {
                if (++mapped == 1)
                    cancelling.Cancel();
                return r.GetInt32(0);
            }, cancelling.Token);
            await cancelled.Should().ThrowAsync<OperationCanceledException>();
            mapped.Should().Be(1, "the read ends at the next row once its token is cancelled, though Npgsql holds the rows already");
            await TwoMoreConnectionsOpenAsync(host.ConnectionString, "after a read cancelled part way");

            using var waiting = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            var slow = () => storage.ReadSqlAsync("select 1 from pg_sleep(10)", null, r => r.GetInt32(0), waiting.Token);
            await slow.Should().ThrowAsync<OperationCanceledException>("the statement is cancelled on the server");
            await TwoMoreConnectionsOpenAsync(host.ConnectionString, "after a statement cancelled on the server");

            (await storage.ReadSqlAsync("select 1", null, r => r.GetInt32(0))).Should().Equal([1], "and the session reads on");
            storage.IsTransactionActive().Should().BeFalse();
            return true;
        });
    }

    [Fact]
    public async Task ReadSqlAsync_Should_RefuseAStatementThatIsNotARead_When_ItIsGiven()
    {
        await Host.InScopeAsync(async (uow, _) =>
        {
            var storage = uow.StorageProvider;
            var update = () => storage.ReadSqlAsync("update sample_zone set name = 'x' returning code", null, r => r.GetString(0));
            var selected = () => storage.ReadSqlAsync("selection", null, r => r.GetString(0));
            (await update.Should().ThrowAsync<ArgumentException>()).WithMessage("*SELECT or WITH*");
            await selected.Should().ThrowAsync<ArgumentException>("a word that only begins with SELECT is not one");
            (await storage.ReadSqlAsync("  WITH t AS (SELECT 2 AS v) SELECT v FROM t", null, r => r.GetInt32(0))).Should().Equal([2]);
            return true;
        });
    }

    // Two connections from the pool the host's sessions use, each within the pool's 2-second wait.
    private static async Task TwoMoreConnectionsOpenAsync(string connectionString, string because)
    {
        await using var first = new NpgsqlConnection(connectionString);
        await using var second = new NpgsqlConnection(connectionString);
        var open = async () =>
        {
            await first.OpenAsync();
            await second.OpenAsync();
        };
        await open.Should().NotThrowAsync(because);
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
