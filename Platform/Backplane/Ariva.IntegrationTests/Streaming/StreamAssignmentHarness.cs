using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Ariva.Core.Messaging;
using Ariva.Core.Queueing;
using Ariva.Di.Extensions;
using Ariva.Infra.Settings;
using Ariva.Infra.Streaming;
using Ariva.Infra.Timescale;
using Ariva.IntegrationTests.Messaging;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Ariva.IntegrationTests.Streaming;

/// <summary>The host's clock, which a test can move on (ARV-274): idle ticks and the restores' waits after a failure follow it.</summary>
internal sealed class MovableClock : TimeProvider
{
    private long _shiftTicks;

    public void MoveOn(TimeSpan by) => Interlocked.Add(ref _shiftTicks, by.Ticks);

    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + TimeSpan.FromTicks(Interlocked.Read(ref _shiftTicks));
}

/// <summary>The queue stream worker's log from Information up (ARV-274), to read what became of its restores.</summary>
internal sealed class WorkerLog : ILoggerProvider
{
    public ConcurrentQueue<(LogLevel Level, string Message)> Lines { get; } = new();

    public ILogger CreateLogger(string categoryName) =>
        categoryName == typeof(QueueStreamWorker).FullName ? new Logger(Lines) : NullLogger.Instance;

    /// <summary>The lines that start with <paramref name="text"/>.</summary>
    public int Count(string text) => Lines.Count(l => l.Message.StartsWith(text, StringComparison.Ordinal));

    /// <summary>Waits up to <paramref name="seconds"/> for <paramref name="count"/> lines that start with <paramref name="text"/>.</summary>
    public async Task<int> WaitForAsync(string text, int count, int seconds, CancellationToken ct)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (Count(text) < count && DateTime.UtcNow < until)
            await Task.Delay(200, ct);
        return Count(text);
    }

    public void Dispose()
    {
    }

    private sealed class Logger(ConcurrentQueue<(LogLevel Level, string Message)> lines) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            if (IsEnabled(logLevel))
                lines.Enqueue((logLevel, formatter(state, exception)));
        }
    }
}

/// <summary>
/// A queue stream host on the class's broker and a database of the test's own (ARV-274), saved zone states seeded in it, and literal
/// queries against it (values bound, never interpolated, CWE-89).
/// </summary>
internal sealed class StreamAssignmentHarness(PostgresFixture postgres, KafkaFixture kafka)
{
    /// <summary>When the seeded states were saved: a row still at it was not saved again.</summary>
    public const string SeededOn = "2026-10-01T00:00:00Z";

    public string BootstrapServers => kafka.BootstrapServers;

    public async Task CreateTopicsAsync()
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = kafka.BootstrapServers }).Build();
        try
        {
            await admin.CreateTopicsAsync(QueueStreamWorker.Topics.Concat(QueueStreamWorker.Topics.Select(KafkaTopics.DeadLetter))
                .Select(t => new TopicSpecification { Name = t, NumPartitions = t.EndsWith(KafkaTopics.DeadLetterSuffix, StringComparison.Ordinal) ? 1 : 3, ReplicationFactor = 1 }));
        }
        catch (CreateTopicsException e) when (e.Results.All(r => r.Error.Code is ErrorCode.TopicAlreadyExists or ErrorCode.NoError))
        {
            // Created already on this broker.
        }
    }

    /// <summary>A new database with every script applied.</summary>
    public async Task<string> DatabaseAsync(TestDatabase database, CancellationToken ct)
    {
        var name = await postgres.CreateDatabaseAsync(database);
        await new SqlScriptRunner(postgres.ConnectionString(name), NullLogger<SqlScriptRunner>.Instance).ApplyAsync(SqlScriptCatalog.Embedded(), ct);
        return name;
    }

    public DatabaseSettings Settings(string database) => new()
    {
        Host = postgres.Hostname, Port = postgres.Port, Name = database, Username = postgres.AdminUsername, Password = postgres.AdminPassword
    };

    /// <summary>A fresh state of each zone (<see cref="FixedGeometrySource"/>, profile version 7), saved at <see cref="SeededOn"/>.</summary>
    public async Task SeedAsync(string database, IEnumerable<string> zones, CancellationToken ct)
    {
        var store = new StreamStore(Settings(database), new ManualClock(DateTimeOffset.Parse(SeededOn, System.Globalization.CultureInfo.InvariantCulture)),
            NullLogger<StreamStore>.Instance);
        var geometries = new FixedGeometrySource();
        var states = new List<ZoneProcessorState>();
        foreach (var zone in zones)
        {
            var geometry = await geometries.LoadAsync("DMO", zone, ct);
            states.Add(new ZoneProcessor("DMO/" + zone, geometry.Geometry, geometry.ProfileVersion).Capture());
        }

        await store.SaveAsync(new StreamCheckpoint("it-assigned-seed", [], states, [], []), ct);
    }

    public IHost Host(string database, IReadOnlyDictionary<string, string> extra, TimeProvider clock, ILoggerProvider log = null)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        if (log is not null)
            builder.Logging.AddProvider(log);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Database:Host"] = postgres.Hostname,
            ["Database:Port"] = postgres.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Database:Name"] = database,
            ["Database:Username"] = postgres.AdminUsername,
            ["Database:Password"] = postgres.AdminPassword,
            ["Kafka:Enabled"] = "true",
            ["Kafka:BootstrapServers"] = kafka.BootstrapServers,
            ["Kafka:ServiceName"] = "it-assigned",
            ["Kafka:Consumers:CheckpointSeconds"] = "1",
            ["Stream:Enabled"] = "true",
            ["Stream:IdleTickSeconds"] = "0",
            ["Stream:Sites:DMO:LatenessSeconds"] = "90",
            ["Stream:Sites:DMO:PushIntervalSeconds"] = "60"
        });
        builder.Configuration.AddInMemoryCollection(extra);
        builder.Services.AddSingleton(clock);
        builder.Services.AddArivaPersistence(builder.Configuration);
        builder.Services.AddArivaMessaging(builder.Configuration, messaging => messaging.StreamQueueZones());
        builder.Services.AddArivaQueueStream(builder.Configuration);
        builder.Services.Replace(ServiceDescriptor.Singleton<ZoneGeometrySource, FixedGeometrySource>());
        return builder.Build();
    }

    /// <summary>The rows of a literal query, values bound as <paramref name="parameters"/> (never interpolated, CWE-89).</summary>
    public async Task<List<string>> RowsAsync(string database, [ConstantExpected] string sql, IReadOnlyDictionary<string, object> parameters, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString(database));
        await connection.OpenAsync(ct);
#pragma warning disable CA2100 // every caller passes a literal
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(k => reader.IsDBNull(k) ? "null" : Convert.ToString(reader.GetValue(k), System.Globalization.CultureInfo.InvariantCulture))));
        return rows;
    }

    /// <summary>A transaction holding <c>stream_zone_state</c> in ACCESS EXCLUSIVE mode: every read of it waits to its command timeout.</summary>
    public async Task<(NpgsqlConnection Connection, NpgsqlTransaction Hold)> LockZoneStatesAsync(string database, CancellationToken ct)
    {
        var holder = new NpgsqlConnection(postgres.ConnectionString(database));
        await holder.OpenAsync(ct);
        var hold = await holder.BeginTransactionAsync(ct);
        await using (var lockTable = new NpgsqlCommand("LOCK TABLE stream_zone_state IN ACCESS EXCLUSIVE MODE", holder, hold))
            await lockTable.ExecuteNonQueryAsync(ct);
        return (holder, hold);
    }
}
