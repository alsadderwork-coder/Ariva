using System.Globalization;
using System.Text.Json;
using Ariva.Core.Queueing.Replay;
using Ariva.Core.Sensing;
using Ariva.Di.Extensions;
using Ariva.Infra.Sensing;
using Ariva.Infra.Streaming;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Ariva.Di;

/// <summary>
/// The golden replay command of Ariva.Api.Stream (ARV-036). Run instead of the web host:
/// <list type="bullet">
/// <item><c>--replay --replay-site=DMO [--replay-zone=A-VIS ...] --replay-from=2026-09-28T17:00:00Z --replay-to=2026-09-28T20:30:00Z
/// [--replay-profile-version=12] [--replay-output=path]</c> replays the archive and prints the manifest and hashes as JSON;
/// with an output path the export (one JSON line per input and output, hash-chained) is written there, a new file with
/// owner-only permissions. Exit code 0, or 1 on failure (logged), 2 for bad arguments.</item>
/// <item><c>--verify-replay=path</c> checks an export's chains and looks its replay hash up in replay_run. Exit code 0
/// when the chains hold and the run is on record, 3 otherwise.</item>
/// </list>
/// </summary>
public static class ReplayCommand
{
    public const string ReplayFlag = "--replay";
    public const string VerifyPrefix = "--verify-replay=";
    private const string Prefix = "--replay-";

    public static bool IsRequested(string[] args) =>
        args?.Any(a => a == ReplayFlag || a.StartsWith(VerifyPrefix, StringComparison.Ordinal)) == true;

    /// <summary>The arguments without this command's, so the configuration command line provider never sees them.</summary>
    public static string[] WithoutFlags(string[] args) =>
        (args ?? []).Where(a => a != ReplayFlag && !a.StartsWith(Prefix, StringComparison.Ordinal) && !a.StartsWith(VerifyPrefix, StringComparison.Ordinal)).ToArray();

    private static readonly JsonSerializerOptions Pretty = new(ReplayLedger.Json) { WriteIndented = true };

    /// <summary>The request the arguments describe, or the problems with them.</summary>
    public static (ReplayRequest Request, string OutputPath, IReadOnlyList<string> Problems) Parse(string[] args, string requestedBy)
    {
        var problems = new List<string>();
        string Value(string name) => args.LastOrDefault(a => a.StartsWith(Prefix + name + "=", StringComparison.Ordinal))?[(Prefix.Length + name.Length + 1)..];
        DateTime Time(string name)
        {
            if (DateTime.TryParse(Value(name), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) &&
                Value(name)!.EndsWith('Z'))
                return DateTime.SpecifyKind(t, DateTimeKind.Utc);
            problems.Add($"{Prefix}{name} is an ISO 8601 UTC time ending in Z.");
            return default;
        }

        var site = Value("site");
        var zones = args.Where(a => a.StartsWith(Prefix + "zone=", StringComparison.Ordinal)).Select(a => a[(Prefix.Length + 5)..]).ToList();
        var from = Time("from");
        var to = Time("to");
        int? version = null;
        if (Value("profile-version") is { } v)
        {
            if (int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                version = parsed;
            else
                problems.Add($"{Prefix}profile-version is a whole number.");
        }

        var request = new ReplayRequest(site, zones, from, to, version, requestedBy);
        if (problems.Count == 0)
            problems.AddRange(ReplayRunner.Problems(request));
        return (request, Value("output"), problems);
    }

    public static async Task<int> RunAsync(IConfiguration configuration, string[] args, TextWriter output, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(output);
        args ??= [];

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddSimpleConsole(options => options.SingleLine = true));
        services.AddArivaPersistence(configuration);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ISensingArchive, SensingArchive>();
        services.AddSingleton<IDeviceHealthArchive, DeviceHealthArchive>();
        services.AddSingleton<ZoneGeometrySource>();
        services.AddSingleton<ReplayStore>();
        services.AddSingleton<ReplayRunner>();
        await using var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ReplayCommand));

        var verifyPath = args.LastOrDefault(a => a.StartsWith(VerifyPrefix, StringComparison.Ordinal))?[VerifyPrefix.Length..];
        if (verifyPath is not null)
            return await VerifyAsync(provider.GetRequiredService<ReplayStore>(), verifyPath, output, logger, ct);

        var (request, outputPath, problems) = Parse(args, $"{Environment.UserName}@{Environment.MachineName}");
        if (problems.Count > 0)
        {
            foreach (var problem in problems)
                logger.LogError("Replay: {Problem}", problem);
            return 2;
        }

        try
        {
            ReplayRun run;
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                run = await provider.GetRequiredService<ReplayRunner>().RunAsync(request, null, ct);
            }
            else
            {
                await using var writer = CreateExport(outputPath);
                run = await provider.GetRequiredService<ReplayRunner>().RunAsync(request, writer, ct);
                logger.LogInformation("Replay export written to {Path}", outputPath);
            }

            await output.WriteLineAsync(Summary(run));
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogCritical(ex, "Replay failed");
            return 1;
        }
    }

    /// <summary>What the command prints for a run: its id, manifest, hashes, counters and row hash (the anchor to keep outside the database).</summary>
    public static string Summary(ReplayRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return JsonSerializer.Serialize(new { run.Id, run.Manifest, run.Hashes, run.Counters, run.RowHash }, Pretty);
    }

    /// <summary>
    /// The export file: always a new file (an existing file or a link in its place is refused, CWE-73), readable and
    /// writable by the owner only, UTF-8 without a byte order mark.
    /// </summary>
    public static StreamWriter CreateExport(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new StreamWriter(path, new System.Text.UTF8Encoding(false), options);
    }

    private static async Task<int> VerifyAsync(ReplayStore store, string path, TextWriter output, ILogger logger, CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(path);
            var verified = ReplayLedger.Verify(reader);
            var recorded = verified.Valid && verified.Hashes is { } h ? await store.FindAsync(h.ReplayHash, ct) : [];
            var onRecord = recorded.Any(r => r.InputHead == verified.Hashes.InputHead && r.OutputHead == verified.Hashes.OutputHead);
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                verified.Valid,
                OnRecord = onRecord,
                Runs = recorded,
                verified.Manifest,
                verified.Hashes,
                verified.Problems
            }, Pretty));
            return verified.Valid && onRecord ? 0 : 3;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Data.Common.DbException)
        {
            logger.LogCritical(ex, "Replay verification failed");
            return 1;
        }
    }
}
