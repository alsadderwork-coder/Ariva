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
        command.Parameters.AddWithValue("iata", "Q" + suffix[..2]);
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
