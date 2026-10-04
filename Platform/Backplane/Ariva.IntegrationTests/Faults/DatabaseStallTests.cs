using System.Diagnostics;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Sensing;
using Ariva.Core.Services;
using Ariva.Di.Extensions;
using Ariva.Infra.Resilience;
using Ariva.Infra.Sensing;
using Ariva.Infra.Services.Sensing;
using Ariva.Infra.Settings;
using Ariva.IntegrationTests.Security;
using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Npgsql;
using ZiggyCreatures.Caching.Fusion;

namespace Ariva.IntegrationTests.Faults;

/// <summary>
/// ARV-072, PostgreSQL stalled or cut off under the sensing path. On a push, Ariva.Api.Ingest needs the database only to
/// look the device's credential and its zone up (cached a minute); a cold lookup against a stalled database fails after
/// SvcDeviceGateway.LookupTimeout as a dependency outage, which the host answers 503 with Retry-After, so the sensor
/// keeps its batch and pushes it again (the back-pressure) instead of piling requests up for Npgsql's 30 seconds. Behind
/// Kafka, Ariva.Api.Stream archives every batch; a database outage is waited out and the batch is archived once it is
/// back, exactly once even when Kafka delivers it again (no data loss, no duplicate).
/// </summary>
[Collection(FaultsCollection.Name)]
public sealed class DatabaseStallTests(FaultsFixture faults) : IAsyncLifetime
{
    private const string Proxy = "postgres";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await faults.RestoreAsync(Proxy);

    private DatabaseSettings Settings => new()
    {
        Host = faults.Host,
        Port = faults.PostgresPort,
        Name = FaultsFixture.Database,
        Username = faults.PostgresUsername,
        Password = faults.PostgresPassword
    };

    private ServiceProvider Provider()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Application:Environment"] = "vm-local",
            ["Database:Host"] = Settings.Host,
            ["Database:Port"] = Settings.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Database:Name"] = Settings.Name,
            ["Database:Username"] = Settings.Username,
            ["Database:Password"] = Settings.Password,
            ["Redis:Enabled"] = "false"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentUser, TestCurrentUser>();
        services.AddArivaPersistence(configuration);
        return services.BuildServiceProvider();
    }

    /// <summary>A registered, commissioned device with a credential, written over the direct connection; returns its prefix.</summary>
    private async Task<string> SeedDeviceAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var site = "F" + suffix;
        var prefix = "ardk_" + Guid.NewGuid().ToString("N")[..8];
        await using var sql = new NpgsqlConnection(faults.DirectConnectionString);
        await sql.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            WITH s AS (INSERT INTO site (id, code, name) VALUES (gen_random_uuid(), @site, @site) RETURNING code),
                 a AS (INSERT INTO airport (id, iata_code, name, time_zone_id) VALUES (gen_random_uuid(), @iata, 'Faults', 'Asia/Dubai') RETURNING id),
                 t AS (INSERT INTO terminal (id, airport_id, code, name, site_code) SELECT gen_random_uuid(), a.id, 'T1', 'T1', s.code FROM a, s RETURNING id, site_code),
                 l AS (INSERT INTO level (id, terminal_id, site_code, code, name, floor_number, width_metres, depth_metres)
                       SELECT gen_random_uuid(), t.id, t.site_code, 'L0', 'L0', 0, 100, 50 FROM t RETURNING id, site_code)
            INSERT INTO device (id, code, site_code, family, model, transport, dialect, clock_source, state, level_id, x, y, mounting_height_metres,
                                orientation_degrees, footprint_source, footprint_radius_metres, queue_zone_name, credential_prefix, credential_hash)
            SELECT gen_random_uuid(), 'FLT-01', l.site_code, 'StereoVision', 'PC2SE', 'HttpsPush', 'Canonical', 'Ntp', 'Online', l.id, 10, 10, 5,
                   0, 'Vendor', 3, 'Hall', @prefix, repeat('a', 64) FROM l
            """, sql);
        command.Parameters.AddWithValue("site", site);
        // Three letters (the airport check); the hex suffix may hold digits.
        command.Parameters.AddWithValue("iata", new string([.. Enumerable.Range(0, 3).Select(_ => (char)('A' + System.Security.Cryptography.RandomNumberGenerator.GetInt32(26)))]));
        command.Parameters.AddWithValue("prefix", prefix);
        (await command.ExecuteNonQueryAsync(Ct)).Should().Be(1);
        return prefix;
    }

    private static async Task<(Exception Failure, TimeSpan Answered)> LookUpAsync(ServiceProvider provider, IFusionCache cache, string prefix)
    {
        var clock = Stopwatch.StartNew();
        // The request's scope (and the stalled connection under it) is released after the answer has gone out, as in the
        // host, so only the time to the exception is the device's wait.
        await using var scope = provider.CreateAsyncScope();
        var gateway = new SvcDeviceGateway(scope.ServiceProvider.GetRequiredService<IUnitOfWork>(), cache);
        try
        {
            await gateway.FindByPrefixAsync(prefix, Ct);
            return (null, clock.Elapsed);
        }
        catch (Exception e) when (e is not OperationCanceledException || !Ct.IsCancellationRequested)
        {
            return (e, clock.Elapsed);
        }
    }

    [Fact]
    public async Task CredentialLookup_Should_FailAsAnOutageWithinItsBoundAndCacheNothing_When_TheDatabaseStalls()
    {
        await using var provider = Provider();
        using var cache = new FusionCache(new FusionCacheOptions());
        var prefix = await SeedDeviceAsync();

        await faults.StallAsync(Proxy);

        var (failure, answered) = await LookUpAsync(provider, cache, prefix);
        failure.Should().NotBeNull("a registered device cannot be looked up while the database does not answer, and must not be let in");
        DependencyOutage.Is(failure!).Should().BeTrue($"the push is answered 503 with Retry-After, not 401 ({failure.GetType().Name})");
        answered.Should().BeGreaterThanOrEqualTo(SvcDeviceGateway.LookupTimeout - TimeSpan.FromMilliseconds(200))
            .And.BeLessThan(SvcDeviceGateway.LookupTimeout + TimeSpan.FromSeconds(5), "the device is answered after the lookup's bound, not Npgsql's 30 seconds");

        await faults.RestoreAsync(Proxy);

        // Back, with the same cache: the timed-out lookup left no cached miss, so the device is found on its next push.
        DeviceCredentialRecord found = null;
        var until = DateTime.UtcNow.AddSeconds(60);
        while (found is null)
        {
            await using var scope = provider.CreateAsyncScope();
            var gateway = new SvcDeviceGateway(scope.ServiceProvider.GetRequiredService<IUnitOfWork>(), cache);
            try
            {
                found = await gateway.FindByPrefixAsync(prefix, Ct);
                found.Should().NotBeNull("a timeout must not be remembered as an unknown device");
            }
            catch (Exception e) when (DependencyOutage.Is(e) && DateTime.UtcNow < until)
            {
                await Task.Delay(500, Ct);
            }
        }

        found.Code.Should().Be("FLT-01");
    }

    [Fact]
    public async Task Session_Should_FailAsAnOutageRatherThanPassOnAStaleCopy_When_RevokedAndTheDatabaseStalls()
    {
        // ARV-081 (ASVS V16.5.3): with the hosts' default cache options (fail-safe, 500 ms soft timeout) the expired copy
        // "active" would be served for up to an hour; the session check must fail (503) instead, then see the revocation.
        await using var provider = Provider();
        using var cache = new FusionCache(new FusionCacheOptions
        {
            DefaultEntryOptions = new FusionCacheEntryOptions
            {
                Duration = TimeSpan.FromMinutes(5),
                IsFailSafeEnabled = true,
                FailSafeMaxDuration = TimeSpan.FromHours(1),
                FactorySoftTimeout = TimeSpan.FromMilliseconds(500),
                FactoryHardTimeout = TimeSpan.FromSeconds(30),
                AllowTimedOutFactoryBackgroundCompletion = true
            }
        });
        var settings = new Ariva.Infra.Settings.AuthSettings { Sessions = new Ariva.Infra.Settings.SessionSettings { CacheSeconds = 1 } };
        var sessionId = await SeedSessionAsync();
        async Task<(Ariva.Core.Security.SessionState? State, Exception Failure)> CheckAsync()
        {
            await using var scope = provider.CreateAsyncScope();
            var validator = new Ariva.Infra.Security.SessionValidator(scope.ServiceProvider.GetRequiredService<IUnitOfWork>(), cache, settings, TimeProvider.System);
            try
            {
                return (await validator.CheckAsync(sessionId, Ct), null);
            }
            catch (Exception e) when (e is not OperationCanceledException || !Ct.IsCancellationRequested)
            {
                return (null, e);
            }
        }

        (await CheckAsync()).State.Should().Be(Ariva.Core.Security.SessionState.Active);

        // Revoked while the eviction is lost (the outage case), then the database stalls once the copy has expired.
        await ExecuteDirectAsync("UPDATE user_session SET revoked_on = now() WHERE id = @id", sessionId);
        await faults.StallAsync(Proxy);
        await Task.Delay(TimeSpan.FromSeconds(1.5), Ct);

        var (state, failure) = await CheckAsync();
        state.Should().BeNull($"a revoked session must not pass on the expired copy (it answered {state})");
        DependencyOutage.Is(failure!).Should().BeTrue($"the request is answered 503 with Retry-After ({failure?.GetType().Name})");

        await faults.RestoreAsync(Proxy);

        var until = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            var (after, error) = await CheckAsync();
            if (error is null)
            {
                after.Should().Be(Ariva.Core.Security.SessionState.Revoked);
                break;
            }

            DependencyOutage.Is(error).Should().BeTrue(error.GetType().Name);
            DateTime.UtcNow.Should().BeBefore(until);
            await Task.Delay(500, Ct);
        }
    }

    /// <summary>A user and an active session (idle and absolute deadlines a day away), written over the direct connection.</summary>
    private async Task<Guid> SeedSessionAsync()
    {
        var sessionId = Guid.NewGuid();
        await using var sql = new NpgsqlConnection(faults.DirectConnectionString);
        await sql.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            WITH u AS (INSERT INTO "user" (id, user_name, password_hash, password_salt, password_algorithm, password_iterations)
                       VALUES (gen_random_uuid(), @name, 'x', 'x', 'PBKDF2-SHA256', 600000) RETURNING id)
            INSERT INTO user_session (id, user_id, family_id, started_on, authenticated_on, authentication_methods, last_seen_on,
                                      idle_timeout_seconds, idle_expires_on, absolute_expires_on)
            SELECT @session, u.id, gen_random_uuid(), now(), now(), 'pwd otp', now(), 14400, now() + interval '1 day', now() + interval '1 day' FROM u
            """, sql);
        command.Parameters.AddWithValue("name", "it.faults." + sessionId.ToString("N")[..8]);
        command.Parameters.AddWithValue("session", sessionId);
        (await command.ExecuteNonQueryAsync(Ct)).Should().Be(1);
        return sessionId;
    }

    private async Task ExecuteDirectAsync(string statement, Guid id)
    {
        await using var sql = new NpgsqlConnection(faults.DirectConnectionString);
        await sql.OpenAsync(Ct);
#pragma warning disable CA2100 // the statement is a constant of this class; the id is a parameter
        await using var command = new NpgsqlCommand(statement, sql);
#pragma warning restore CA2100
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(Ct);
    }

    [Fact]
    public async Task SensingArchive_Should_ArchiveTheBatchOnceTheDatabaseIsBack_When_ItWasCutOffMeanwhile()
    {
        var archive = new SensingArchive(Settings, TimeProvider.System);
        var at = DateTime.UtcNow.AddSeconds(-30);
        var batch = new TrackSampleBatch
        {
            Id = Guid.NewGuid(),
            DeviceId = Guid.NewGuid(),
            DeviceCode = "FLT-01",
            SiteCode = "FLT",
            QueueZoneName = "Hall",
            Dialect = "Canonical",
            Commissioned = true,
            ReceivedUtc = at.AddSeconds(1),
            Clock = new ClockReading(0, true, ClockState.Ok),
            Samples = [.. Enumerable.Range(0, 25).Select(i => new Sensed<TrackPosition>(
                new TrackPosition($"FLT-01/{i % 5}", 10 + i % 10, 12, 1.7, at.AddMilliseconds(i * 200)), at.AddMilliseconds(i * 200), SensedFlags.None))]
        };
        var context = new Mock<ConsumeContext<TrackSampleBatch>>();
        context.SetupGet(c => c.Message).Returns(batch);
        context.SetupGet(c => c.CancellationToken).Returns(Ct);
        var consumer = new SensingArchiveConsumer<TrackSampleBatch>(archive, TimeProvider.System);

        await faults.CutAsync(Proxy);
        var consuming = consumer.Consume(context.Object);
        await Task.Delay(TimeSpan.FromSeconds(5), Ct);
        consuming.IsCompleted.Should().BeFalse("the consumer waits the outage out rather than failing the message to the dead-letter topic");
        (await CountAsync(batch.Id)).Should().Be((0, 0));

        await faults.RestoreAsync(Proxy);
        await consuming.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        (await CountAsync(batch.Id)).Should().Be((1, 25), "every position of the batch is archived");

        // Kafka delivers it again (the offset was not committed before a crash): still one batch.
        await consumer.Consume(context.Object);
        (await CountAsync(batch.Id)).Should().Be((1, 25));
    }

    private async Task<(long Batches, long Events)> CountAsync(Guid batchId)
    {
        await using var sql = new NpgsqlConnection(faults.DirectConnectionString);
        await sql.OpenAsync(Ct);
        await using var batches = new NpgsqlCommand("SELECT count(*) FROM sensing_batch WHERE id = @id", sql);
        batches.Parameters.AddWithValue("id", batchId);
        await using var events = new NpgsqlCommand("SELECT count(*) FROM sensing_event WHERE batch_id = @id", sql);
        events.Parameters.AddWithValue("id", batchId);
        return ((long)(await batches.ExecuteScalarAsync(Ct))!, (long)(await events.ExecuteScalarAsync(Ct))!);
    }
}
