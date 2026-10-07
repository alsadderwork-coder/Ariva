using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Ariva.Core.Queueing;
using Ariva.UnitTests.Setup;
using FluentAssertions;

namespace Ariva.UnitTests.Architecture;

/// <summary>
/// ARV-117 and ARV-117a (CWE-862, CWE-863): the shadow nowcast without AMAN inputs is written by the stream and read only
/// by the validation comparison (ARV-104f, not built yet). Since ARV-117a it has its own table, <c>queue_minute_shadow</c>
/// (script 0043), whose value columns the runtime role cannot read: the database enforces the rule (proven on PostgreSQL by
/// QueueStreamTests in Ariva.IntegrationTests, with the exact list of the table's dependents), so the ARV-117 scans of
/// wildcard and whole-row reads of queue_minute are retired. What stays here: no type outside the stream's write path can
/// hold the shadow (field graph over every Ariva assembly), the live snapshot, the hub, displays, alert inputs, reports
/// and view models have no shadow member, and only the stream store names the shadow table. The sensor-only desk minutes
/// (<c>desk_sensor_minute</c>, script 0044) stay readable by the runtime role, because the stream's desk term reads them;
/// the same kind of checks keep every other reader out. A read path added later must change these tests on purpose, with
/// its own authorization.
/// </summary>
public sealed class ShadowNowcastExposureTests
{
    private static readonly Assembly[] Assemblies =
    [
        typeof(Ariva.Core._IAssemblyMark).Assembly,
        typeof(Ariva.Infra._IAssemblyMark).Assembly,
        typeof(Ariva.Di._IAssemblyMark).Assembly,
        typeof(Ariva.Api.Common._IAssemblyMark).Assembly,
        typeof(Ariva.Api.Main._IAssemblyMark).Assembly,
        typeof(Ariva.Api.Ingest._IAssemblyMark).Assembly,
        typeof(Ariva.Api.Stream._IAssemblyMark).Assembly,
        typeof(Ariva.Api.Cronz._IAssemblyMark).Assembly,
        typeof(Ariva.Api.Integration._IAssemblyMark).Assembly,
        typeof(Ariva.Business.Contracts._IAssemblyMark).Assembly
    ];

    // The stream's write path: the engine and its outputs (the replay ledger reads the outputs but never serialises the
    // shadow, ShadowNowcastTests); the checkpoint, the worker and the store are added in the test.
    private static readonly string[] WritePath =
    [
        "Ariva.Core.Queueing.ShadowNowcast",
        "Ariva.Core.Queueing.QueueLiveMinute",
        "Ariva.Core.Queueing.ZoneOutputs",
        "Ariva.Core.Queueing.ZoneProcessor"
    ];

    private static IEnumerable<Type> TypesOf(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.Where(t => t is not null)!;
        }
    }

    private static IEnumerable<Type> Mentioned(Type type)
    {
        if (type.IsArray || type.IsByRef || type.IsPointer)
        {
            foreach (var t in Mentioned(type.GetElementType()!))
                yield return t;
            yield break;
        }

        yield return type;
        if (type.IsGenericType)
            foreach (var argument in type.GetGenericArguments())
                foreach (var t in Mentioned(argument))
                    yield return t;
    }

    // The outermost type a compiler-generated type (a closure, an async state machine) belongs to.
    private static Type Owner(Type type)
    {
        while (type.DeclaringType is not null && type.IsDefined(typeof(CompilerGeneratedAttribute), false))
            type = type.DeclaringType;
        return type;
    }

    /// <summary>Every Ariva type whose instance or static fields can hold a shadow nowcast, directly or through other types.</summary>
    private static HashSet<Type> Holders() => HoldersOf(typeof(ShadowNowcast));

    [Fact]
    public void Holders_Should_StayOnTheStreamsWritePath_When_TheShadowIsComputed()
    {
        var owners = Holders().Select(Owner).Select(t => t.FullName).Distinct().Order(StringComparer.Ordinal).ToList();

        owners.Should().BeEquivalentTo(WritePath.Concat(
        [
            "Ariva.Infra.Streaming.StreamCheckpoint",
            "Ariva.Infra.Streaming.StreamStore",
            "Ariva.Infra.Streaming.QueueStreamWorker"
        ]), "only the engine and the stream's checkpoint, worker and store may hold the shadow nowcast");
    }

    [Fact]
    public void ExposedTypes_Should_NotHoldTheShadow_When_TheyServeScreensRulesOrReports()
    {
        var holders = Holders();
        var exposed = Assemblies.SelectMany(TypesOf).Where(t => t.FullName is { } name && (
            name == typeof(Ariva.Infra.Live.LiveZoneSnapshot).FullName ||
            name.StartsWith("Ariva.Infra.Live.", StringComparison.Ordinal) ||
            name.StartsWith("Ariva.Api.Main.Hubs.", StringComparison.Ordinal) ||
            name.Contains(".Displays.", StringComparison.Ordinal) ||
            name.Contains(".Alerting.", StringComparison.Ordinal) ||
            name.Contains(".Reports.", StringComparison.Ordinal) ||
            name.Contains(".ViewModels.", StringComparison.Ordinal) ||
            name.EndsWith("ViewModel", StringComparison.Ordinal) ||
            name.StartsWith("Ariva.Api.Main.Controllers.", StringComparison.Ordinal) ||
            name.StartsWith("Ariva.Business.Contracts.", StringComparison.Ordinal))).ToList();

        exposed.Should().Contain(typeof(Ariva.Infra.Live.LiveZoneSnapshot)).And.Contain(typeof(Ariva.Infra.Alerting.AlertInputs))
            .And.Contain(t => t.FullName == "Ariva.Api.Main.Hubs.LiveHub").And.Contain(typeof(Ariva.Core.Reports.ReportMinute));
        exposed.Where(holders.Contains).Should().BeEmpty("the live snapshot, the hub, displays, alert inputs, reports and view models never carry the shadow");
        exposed.SelectMany(t => t.GetMembers(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(m => m.Name.Contains("Shadow", StringComparison.OrdinalIgnoreCase)).Select(m => $"{t.FullName}.{m.Name}"))
            .Should().BeEmpty("no member of a screen, rule or report type is named after the shadow");
    }

    [Fact]
    public void Members_Should_BeNamedAfterTheShadowOnlyOnTheWritePath_When_EveryArivaTypeIsReflected()
    {
        // ARV-117 second review: every member (field, property, method, event, nested type, including compiler-generated
        // closure and state machine fields) of every Ariva type is checked, not only the exposed ones. A property such as
        // ShadowNowcastMinutes on an entity or a row type would be mapped to shadow_nowcast_minutes by a naming convention
        // and read without the column name ever appearing in source: it fails here, however its name was built.
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var allowed = WritePath.Concat(["Ariva.Infra.Streaming.StreamCheckpoint", "Ariva.Infra.Streaming.StreamStore", "Ariva.Infra.Streaming.QueueStreamWorker"])
            .ToHashSet(StringComparer.Ordinal);
        var named = Assemblies.SelectMany(TypesOf)
            .SelectMany(t => t.GetMembers(all).Where(m => m.Name.Contains("Shadow", StringComparison.OrdinalIgnoreCase))
                .Select(m => (Owner: Owner(t).FullName, Member: $"{t.FullName}.{m.Name}")))
            .ToList();

        named.Should().Contain(n => n.Member == "Ariva.Core.Queueing.QueueLiveMinute.Shadow", "the reflection sees the write path's own member");
        named.Where(n => !allowed.Contains(n.Owner)).Select(n => n.Member).Should().BeEmpty(
            "only the engine, its outputs and the stream's checkpoint, worker and store have members named after the shadow");
    }

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly string[] SkipInScans =
    [
        $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", $"{Path.DirectorySeparatorChar}.svelte-kit{Path.DirectorySeparatorChar}",
        "Ariva.UnitTests", "Ariva.IntegrationTests", "Ariva.E2E", "Ariva.LoadTests"
    ];

    // The production sources (code, scripts, configuration and the web app) that match a pattern; tests are not readers.
    private static List<string> SourcesMatching(Regex pattern, params string[] extensions) =>
        Directory.EnumerateFiles(RepositoryPaths.Platform, "*", SearchOption.AllDirectories)
            .Where(f => extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase) && !SkipInScans.Any(s => f.Contains(s, StringComparison.Ordinal)))
            .Where(f => pattern.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(RepositoryPaths.Platform, f).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

    private static readonly string[] SourceKinds = [".cs", ".ts", ".svelte", ".js", ".mjs", ".sql", ".json"];

    [Fact]
    public void Sources_Should_NameTheShadowTableOnlyInTheStreamStore_When_Scanned()
    {
        // The table (script 0043) is written by the stream store; the runtime role cannot read its values, and nothing
        // else names it. Case-insensitive with optional underscores, so the snake_case names and the PascalCase or
        // camelCase names a convention mapper turns into them (QueueMinuteShadow, ShadowNowcastMinutes) are both caught;
        // the 0041 column names (shadow_nowcast_minutes, shadow_no_service) are kept in the pattern for history.
        var table = new Regex(@"queue_?minute_?shadow|shadow_?(nowcast|no_?service)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

        SourcesMatching(table, SourceKinds).Should().Equal(
            ["Backplane/Ariva.Core/Queueing/ShadowNowcast.cs", "Backplane/Ariva.Core/Queueing/ZoneProcessor.cs", "Backplane/Ariva.Infra/Streaming/StreamStore.cs",
                "Backplane/Ariva.Infra/Timescale/Scripts/0041_shadow_nowcast.sql", "Backplane/Ariva.Infra/Timescale/Scripts/0043_queue_minute_shadow.sql",
                "Backplane/Ariva.Infra/Timescale/Scripts/0044_desk_sensor_minute.sql"],
            "only the engine names the shadow and only the stream store writes its table; no service, screen, rule or report reads it");

        // The value itself: only the engine that computes it and the store that writes it name it.
        // \bShadow\s*: catches property patterns ({ Shadow: var s }) and named arguments that reach it without a member access.
        var value = new Regex(@"\bShadowNowcasts?\b|\.Shadow\b|\bShadow\s*:", RegexOptions.CultureInvariant, RegexTimeout);
        SourcesMatching(value, ".cs").Should().Equal(
            ["Backplane/Ariva.Core/Queueing/ShadowNowcast.cs", "Backplane/Ariva.Core/Queueing/ZoneProcessor.cs", "Backplane/Ariva.Infra/Streaming/StreamStore.cs"]);
    }

    [Fact]
    public void Sources_Should_NameTheSensorOnlyDeskMinutesOnlyOnTheDeskFeedAndTheDeskTerm_When_Scanned()
    {
        // ARV-117a (CWE-862, CWE-863): desk_sensor_minute (script 0044) is written by the desk feed through the stream
        // store and read by the stream's desk term (DeskTermSource; DeskTerm.cs only documents it) for the shadow nowcast; no screen, hub, display, alert
        // input, report or AMAN contract reads it. The runtime role keeps SELECT on it (the desk term needs it), so this
        // scan and the dependents test in QueueStreamTests are what keep other readers out.
        var table = new Regex(@"desk_?sensor_?minute", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

        SourcesMatching(table, SourceKinds).Should().Equal(
            ["Backplane/Ariva.Core/Queueing/DeskTerm.cs", "Backplane/Ariva.Infra/Border/DeskFeed.cs", "Backplane/Ariva.Infra/Streaming/DeskTermSource.cs",
                "Backplane/Ariva.Infra/Streaming/StreamStore.cs", "Backplane/Ariva.Infra/Timescale/Scripts/0044_desk_sensor_minute.sql"],
            "only the desk feed (through the store) writes the sensor-only desk minutes and only the stream's desk term reads them");

        // The engine and its samples: the desk feed runs the engine, the desk term and DeskTerms read the samples (the
        // simulator, which is no Ariva host, has members of its own named Sensor).
        var engine = new Regex(@"\bSensorOnlyDeskEngine\b|\bDeskSensorSample\b|\.Sensor\b", RegexOptions.CultureInvariant, RegexTimeout);
        SourcesMatching(engine, ".cs").Where(f => f.StartsWith("Backplane/", StringComparison.Ordinal)).Should().Equal(
            ["Backplane/Ariva.Core/Desks/SensorOnlyDeskEngine.cs", "Backplane/Ariva.Core/Queueing/DeskTerm.cs", "Backplane/Ariva.Infra/Border/DeskFeed.cs",
                "Backplane/Ariva.Infra/Streaming/DeskTermSource.cs"]);
    }

    /// <summary>Every Ariva type whose fields can hold an instance of <paramref name="held"/>, directly or through other types.</summary>
    private static HashSet<Type> HoldersOf(Type held)
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var fields = Assemblies.SelectMany(TypesOf).Distinct().ToDictionary(t => t, t =>
        {
            var mentioned = new HashSet<Type>();
            for (var current = t; current is not null && current != typeof(object); current = current.BaseType)
                foreach (var field in current.GetFields(all))
                    foreach (var m in Mentioned(field.FieldType))
                        mentioned.Add(m.IsGenericType ? m.GetGenericTypeDefinition() : m);
            return mentioned;
        });
        var holders = new HashSet<Type> { held };
        bool grown;
        do
        {
            grown = false;
            foreach (var (type, mentioned) in fields)
            {
                if (!holders.Contains(type) && mentioned.Any(holders.Contains))
                    grown = holders.Add(type) || grown;
            }
        }
        while (grown);

        return holders;
    }

    [Fact]
    public void SensorOnlyDeskValues_Should_StayOnTheDeskFeedAndTheDeskTerm_When_TheirHoldersAreReflected()
    {
        // ARV-117a: the sensor-only engine is held by the desk feed only, and a sensor-only desk minute as the desk term
        // reads it (DeskSensorSample) by the desk minute sample and the desk term's reader only; none of them reaches the
        // live snapshot, the hub, displays, alert inputs, reports, view models, controllers or the AMAN contracts.
        HoldersOf(typeof(Ariva.Core.Desks.SensorOnlyDeskEngine)).Select(Owner).Select(t => t.FullName).Distinct().Order(StringComparer.Ordinal)
            .Should().BeEquivalentTo(["Ariva.Core.Desks.SensorOnlyDeskEngine", "Ariva.Infra.Border.DeskFeed"]);
        HoldersOf(typeof(DeskSensorSample)).Select(Owner).Select(t => t.FullName).Distinct().Order(StringComparer.Ordinal)
            .Should().BeEquivalentTo(["Ariva.Core.Queueing.DeskMinuteSample", "Ariva.Core.Queueing.DeskSensorSample", "Ariva.Core.Queueing.DeskTerms", "Ariva.Infra.Streaming.DeskTermSource"]);

        var exposed = Assemblies.SelectMany(TypesOf).Where(t => t.FullName is { } name && (
            name.StartsWith("Ariva.Infra.Live.", StringComparison.Ordinal) ||
            name.StartsWith("Ariva.Api.Main.Hubs.", StringComparison.Ordinal) ||
            name.Contains(".Displays.", StringComparison.Ordinal) ||
            name.Contains(".Alerting.", StringComparison.Ordinal) ||
            name.Contains(".Reports.", StringComparison.Ordinal) ||
            name.Contains(".ViewModels.", StringComparison.Ordinal) ||
            name.EndsWith("ViewModel", StringComparison.Ordinal) ||
            name.StartsWith("Ariva.Api.Main.Controllers.", StringComparison.Ordinal) ||
            name.StartsWith("Ariva.Business.Contracts.", StringComparison.Ordinal))).ToList();
        exposed.Should().Contain(t => t.FullName == "Ariva.Api.Main.Hubs.LiveHub");
        exposed.SelectMany(t => t.GetMembers(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(m => m.Name.Contains("SensorOnly", StringComparison.OrdinalIgnoreCase) || m.Name.Contains("DeskSensor", StringComparison.OrdinalIgnoreCase))
                .Select(m => $"{t.FullName}.{m.Name}"))
            .Should().BeEmpty("no member of a screen, rule or report type is named after the sensor-only desk values");
    }

    #region Script 0043 against the engine

    [Fact]
    public void Script0043_Should_AllowExactlyTheNoServiceReasons_When_ItsCheckIsParsed()
    {
        // The CHECK on queue_minute_shadow.no_service names the reasons literally; a reason added to the enum must be added
        // there too (in a new script), or the stream's checkpoint would fail on it (23514) and stall.
        var script = File.ReadAllText(Path.Combine(RepositoryPaths.Platform, "Backplane", "Ariva.Infra", "Timescale", "Scripts", "0043_queue_minute_shadow.sql"));
        var list = Regex.Match(script, @"no_service\s+varchar\(20\)\s+CHECK\s*\(no_service\s+IN\s*\(([^)]*)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        list.Success.Should().BeTrue("script 0043 checks no_service against a literal list");
        var names = Regex.Matches(list.Groups[1].Value, "'([^']*)'", RegexOptions.CultureInvariant, RegexTimeout).Select(m => m.Groups[1].Value).ToList();

        names.Should().OnlyHaveUniqueItems().And.BeEquivalentTo(Enum.GetNames<NoServiceReason>());
        names.Should().OnlyContain(n => n.Length <= 20, "no_service is varchar(20)");
    }

    #endregion
}
