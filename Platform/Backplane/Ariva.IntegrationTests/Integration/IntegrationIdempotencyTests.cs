using Ariva.Core;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Integration;
using Ariva.Core.Services.Administration;
using Ariva.Core.Services.Integration;
using Ariva.Di.Extensions;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ariva.IntegrationTests.Integration;

/// <summary>
/// ARV-043 against TimescaleDB with script 0026: an Idempotency-Key is claimed in the transaction that applies its batch;
/// a retry with the same request gets the stored answer, another request under the key is refused, a concurrent retry
/// waits for the first and then gets its answer, a rolled-back claim frees the key, an expired key is claimed afresh,
/// and the runtime role can neither rewrite an answer nor delete a live key.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class IntegrationIdempotencyTests(PostgresFixture fixture) : IAsyncDisposable
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _ready;
    private const string Client = "ic_abcdefghijklmnopqrstuvwxyz";

    private readonly AccountsHost _host = new(fixture, database: TestDatabase.IntegrationBatches, configure: services =>
    {
        services.AddArivaFlights(new ConfigurationBuilder().Build(), watchFeeds: false);
        services.AddArivaIntegrationBatches();
    });

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task ReadyAsync()
    {
        await Gate.WaitAsync(Ct);
        try
        {
            if (_ready)
            {
                await _host.CreateUserAsync("it.batch.probe." + Guid.NewGuid().ToString("N")[..8]);
                return;
            }

            var admin = await _host.CreateUserAsync("it.batch.admin", roles: [RoleCodes.SystemAdministrator]);
            await _host.ReadAsync<int>("UPDATE \"user\" SET all_sites = true WHERE id = @id RETURNING 1", admin);
            foreach (var code in new[] { "DMO", "SEC" })
                (await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSites>().CreateAsync(new CreateSiteRequest(code, code + " airport"), Ct))).HasErrors.Should().BeFalse();
            _ready = true;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static IdempotencyRequest Request(string key, string sha = null, string operation = IntegrationBatches.Legs, string site = "DMO") =>
        new(Client, key, operation, site, sha ?? new string('a', 64));

    private static string NewKey() => Guid.NewGuid().ToString();

    private Task<IdempotencyClaim> ClaimAsync(IdempotencyRequest request) =>
        _host.AsCallerAsync(null, s => s.GetRequiredService<ISvcIntegrationIdempotency>().ClaimAsync(request, Ct));

    private Task<IdempotencyClaim> ClaimAndCompleteAsync(IdempotencyRequest request, string answer) =>
        _host.AsCallerAsync(null, async s =>
        {
            var service = s.GetRequiredService<ISvcIntegrationIdempotency>();
            var claim = await service.ClaimAsync(request, Ct);
            if (claim.Outcome == IdempotencyOutcome.Claimed)
                await service.CompleteAsync(request, 200, answer, Ct);
            return claim;
        });

    // A completed key straight into the table, with the database's clock (the sweep and the trigger use it).
    private async Task InsertAsync(string key, bool expired)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO integration_idempotency (client_id, idempotency_key, operation, site_code, request_sha256, created_utc, expires_utc, status_code, response_body)
            VALUES (@client, @key, 'flights.legs', 'DMO', repeat('e', 64), now() + @shift - interval '1 day', now() + @shift, 200, '{}')
            """, connection);
        command.Parameters.AddWithValue("client", Client);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("shift", expired ? TimeSpan.FromDays(-1) : TimeSpan.FromDays(1));
        await command.ExecuteNonQueryAsync(Ct);
    }

    [Fact]
    public async Task Key_Should_ReplayTheFirstAnswerAndRefuseAnotherRequest_When_Reused()
    {
        await ReadyAsync();
        var key = NewKey();

        (await ClaimAndCompleteAsync(Request(key), """{"received":1}""")).Outcome.Should().Be(IdempotencyOutcome.Claimed);
        var again = await ClaimAndCompleteAsync(Request(key), """{"received":2}""");
        again.Should().Be(new IdempotencyClaim(IdempotencyOutcome.Replay, 200, """{"received":1}"""), "the same request gets the first answer");

        (await ClaimAsync(Request(key, sha: new string('b', 64)))).Outcome.Should().Be(IdempotencyOutcome.Mismatch, "another body");
        (await ClaimAsync(Request(key, operation: IntegrationBatches.Events))).Outcome.Should().Be(IdempotencyOutcome.Mismatch, "another operation");
        (await ClaimAsync(Request(key, site: "SEC"))).Outcome.Should().Be(IdempotencyOutcome.Mismatch, "another site");
        (await _host.ReadAsync<long>("SELECT count(*) FROM integration_idempotency WHERE idempotency_key = @secret", secret: key)).Should().Be(1);
    }

    [Fact]
    public async Task Key_Should_BelongToOneClient_When_AnotherClientSendsTheSameKeyAndBody()
    {
        await ReadyAsync();
        var key = NewKey();
        (await ClaimAndCompleteAsync(Request(key), """{"client":"a"}""")).Outcome.Should().Be(IdempotencyOutcome.Claimed);

        var other = Request(key) with { ClientId = "ic_zyxwvutsrqponmlkjihgfedcba" };
        (await ClaimAndCompleteAsync(other, """{"client":"b"}""")).Outcome.Should().Be(IdempotencyOutcome.Claimed, "a key is per client: never another client's answer");

        (await ClaimAsync(Request(key))).ResponseBody.Should().Be("""{"client":"a"}""");
        (await ClaimAsync(other)).ResponseBody.Should().Be("""{"client":"b"}""");
    }

    [Fact]
    public async Task Retry_Should_WaitForTheFirstCallAndGetItsAnswer_When_SentAtTheSameTime()
    {
        await ReadyAsync();
        var key = NewKey();
        var claimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = _host.AsCallerAsync(null, async s =>
        {
            var service = s.GetRequiredService<ISvcIntegrationIdempotency>();
            var claim = await service.ClaimAsync(Request(key), Ct);
            claimed.SetResult();
            await release.Task.WaitAsync(Ct);
            await service.CompleteAsync(Request(key), 200, """{"first":true}""", Ct);
            return claim;
        });
        await Task.WhenAny(claimed.Task, first).WaitAsync(Ct);
        first.IsFaulted.Should().BeFalse();
        var second = ClaimAndCompleteAsync(Request(key), """{"second":true}""");

        await Task.Delay(500, Ct);
        second.IsCompleted.Should().BeFalse("the retry waits on the uncommitted claim");
        release.SetResult();

        (await first).Outcome.Should().Be(IdempotencyOutcome.Claimed);
        (await second).Should().Be(new IdempotencyClaim(IdempotencyOutcome.Replay, 200, """{"first":true}"""));
    }

    [Fact]
    public async Task Key_Should_BeFree_When_TheClaimingCallRolledBack()
    {
        await ReadyAsync();
        var key = NewKey();

        // As the API's unit of work filter does when the action throws: the transaction is rolled back.
        var failing = () => _host.AsCallerAsync<bool>(null, async s =>
        {
            await s.GetRequiredService<ISvcIntegrationIdempotency>().ClaimAsync(Request(key), Ct);
            await s.GetRequiredService<Ariva.Core.Services.IUnitOfWork>().RollbackAsync();
            throw new InvalidOperationException("the batch failed");
        });
        await failing.Should().ThrowAsync<InvalidOperationException>();

        (await ClaimAndCompleteAsync(Request(key, sha: new string('c', 64)), "{}")).Outcome.Should().Be(IdempotencyOutcome.Claimed, "nothing was kept of the failed call");
    }

    [Fact]
    public async Task Key_Should_BeClaimedAfresh_When_Expired()
    {
        await ReadyAsync();
        var key = NewKey();
        (await ClaimAndCompleteAsync(Request(key), """{"old":true}""")).Outcome.Should().Be(IdempotencyOutcome.Claimed);

        _host.Clock.Advance(IntegrationBatches.KeyLifetime + TimeSpan.FromMinutes(1));

        (await ClaimAndCompleteAsync(Request(key, sha: new string('d', 64)), """{"new":true}""")).Outcome.Should().Be(IdempotencyOutcome.Claimed);
        (await ClaimAsync(Request(key, sha: new string('d', 64)))).ResponseBody.Should().Be("""{"new":true}""");
    }

    [Fact]
    public async Task Sweep_Should_DeleteOnlyExpiredKeys_When_Run()
    {
        await ReadyAsync();
        var live = NewKey();
        var old = NewKey();
        await InsertAsync(live, expired: false);
        await InsertAsync(old, expired: true);

        var deleted = await _host.AsCallerAsync(null, s => s.GetRequiredService<ISvcIntegrationIdempotency>().SweepAsync(1000, Ct));

        deleted.Should().BeGreaterThanOrEqualTo(1);
        (await _host.ReadAsync<long>("SELECT count(*) FROM integration_idempotency WHERE idempotency_key = @secret", secret: old)).Should().Be(0);
        (await _host.ReadAsync<long>("SELECT count(*) FROM integration_idempotency WHERE idempotency_key = @secret", secret: live)).Should().Be(1);
    }

    [Theory]
    [InlineData("UPDATE integration_idempotency SET response_body = '{\"received\":0}' WHERE status_code IS NOT NULL", "23514")]
    [InlineData("UPDATE integration_idempotency SET request_sha256 = repeat('f', 64)", "42501")]
    [InlineData("UPDATE integration_idempotency SET expires_utc = now() - interval '1 day'", "42501")]
    [InlineData("DELETE FROM integration_idempotency WHERE expires_utc > now() + interval '1 hour'", "23514")]
    [InlineData("TRUNCATE integration_idempotency", "42501")]
    public async Task Database_Should_RefuseRewritingAnAnswerOrDeletingALiveKey_When_TheRuntimeRoleTries(string sql, string state)
    {
        await ReadyAsync();
        var key = NewKey();
        await InsertAsync(key, expired: false);

        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE ariva_runtime", connection, transaction))
            await role.ExecuteNonQueryAsync(Ct);
#pragma warning disable CA2100 // literal statements from the inline data above
        await using var command = new NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100

        var change = () => command.ExecuteNonQueryAsync(Ct);

        (await change.Should().ThrowAsync<PostgresException>(sql)).Which.SqlState.Should().Be(state);
        await transaction.RollbackAsync(Ct);
    }
}
