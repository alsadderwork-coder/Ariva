using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Ariva.Core.Queueing;
using Ariva.UnitTests.Setup;
using FluentAssertions;

namespace Ariva.UnitTests.Architecture;

/// <summary>
/// ARV-117 and ARV-117a (CWE-862, CWE-863): the shadow nowcast without AMAN inputs is written by the stream and read only
/// by the validation comparison. Since ARV-117a it has its own table, <c>queue_minute_shadow</c>
/// (script 0043), whose value columns the runtime role cannot read: the database enforces the rule (proven on PostgreSQL by
/// QueueStreamTests in Ariva.IntegrationTests, with the exact list of the table's dependents), so the ARV-117 scans of
/// wildcard and whole-row reads of queue_minute are retired. What stays here: no type outside the stream's write path can
/// hold the shadow (field graph over every Ariva assembly), the live snapshot, the hub, displays, alert inputs, reports
/// and view models have no shadow member, and only the stream store names the shadow table. The sensor-only desk minutes
/// (<c>desk_sensor_minute</c>, script 0044) stay readable by the runtime role, because the stream's desk term reads them;
/// the same kind of checks keep every other reader out.
/// <para>
/// ARV-104f (changed on purpose): the comparison engine (<see cref="ComparisonNamespace"/>, pure Ariva.Core code with no I/O)
/// compares the shadow it is given as plain rows (<see cref="Ariva.Core.Validation.Comparison.ShadowMinuteRow"/>) with the
/// realised wait, so its members, and its files listed below, may name the shadow and the sensor cycle time; it never names
/// the table and reads nothing.
/// </para>
/// <para>
/// ARV-104g1 (changed on purpose again, with the read path): the validation service (<see cref="ValidationService"/>) reads the
/// table through the validation reader login, the only login that holds <c>ariva_validation_reader</c> (script 0049), in
/// exactly one file (<see cref="ReaderSource"/>), and the results projection (<see cref="ResultsProjection"/>, ARV-104g) will
/// carry the shadow's figures to <c>Validation.View</c> holders. Those two types, and the types nested in them, are the only
/// ones outside the write path and the engine that may hold the shadow rows or the figures computed from them, or have members
/// named after the shadow; and no controller, hub, view model, report, alerting, live, display or contract type takes or
/// returns <see cref="Ariva.Core.Validation.Comparison.ComparisonResult"/>,
/// <see cref="Ariva.Core.Validation.Comparison.ComparisonInput"/> or a shadow-bearing type, except that projection
/// (<see cref="Signatures_Should_NeverTakeOrReturnTheShadow_When_TheTypeServesScreensRulesReportsOrContracts"/>). The database
/// side is QueueStreamTests' dependents test (now also the exact grantees that may read a value of the table) and the
/// integration tests of the reader login (ValidationReaderLoginTests).
/// </para>
/// </summary>
public sealed class ShadowNowcastExposureTests
{
    /// <summary>The F18 comparison engine (ARV-104e, ARV-104f): pure code that is handed the shadow rows by its caller.</summary>
    private const string ComparisonNamespace = "Ariva.Core.Validation.Comparison";

    /// <summary>
    /// The validation results service (ARV-104g2 builds it out); its shadow part (ARV-104g1) is the one reader of the table,
    /// through the validation reader login. Matched with its assembly (Ariva.Infra).
    /// </summary>
    private const string ValidationService = "Ariva.Infra.Services.Validation.SvcValidationResults";

    /// <summary>
    /// The validation results projection (ARV-104g): the view model that serves the shadow's figures to <c>Validation.View</c>
    /// holders of the campaign's site, with the types nested in it. Matched with its assembly (Ariva.Core); it does not exist
    /// yet, and ARV-104g creates it under this name (or changes these tests on purpose).
    /// </summary>
    private const string ResultsProjection = "Ariva.Core.Domain.ViewModels.ValidationResultsViewModel";

    /// <summary>The reader's file: the only production source besides the stream store and the scripts that reads the table.</summary>
    private const string ReaderSource = "Backplane/Ariva.Infra/Services/Validation/SvcValidationResults.Shadow.cs";

    /// <summary>The start-up and migration check of who can read the table (privileges only, never a row; ARV-104g1 review, M2).</summary>
    private const string AccessCheckSource = "Backplane/Ariva.Infra/Timescale/ShadowReadAccess.cs";

    /// <summary>
    /// The validation read path (ARV-104g1): the validation service or the results projection, or a type nested in either
    /// (their closures, state machines and nested records), each in its own assembly.
    /// </summary>
    private static bool IsValidationReadPath(Type type)
    {
        for (var current = type; current is not null; current = current.DeclaringType)
        {
            if ((current.FullName == ValidationService && current.Assembly == typeof(Ariva.Infra._IAssemblyMark).Assembly) ||
                (current.FullName == ResultsProjection && current.Assembly == typeof(Ariva.Core._IAssemblyMark).Assembly))
                return true;
        }

        return false;
    }

    /// <summary>The results projection or a type nested in it: the one exposed type that may take or return the shadow's figures.</summary>
    private static bool IsResultsProjection(Type type)
    {
        for (var current = type; current is not null; current = current.DeclaringType)
        {
            if (current.FullName == ResultsProjection && current.Assembly == typeof(Ariva.Core._IAssemblyMark).Assembly)
                return true;
        }

        return false;
    }

    /// <summary>The comparison engine's files that name the shadow's value (ARV-104f).</summary>
    private static readonly string[] ComparisonShadowSources =
    [
        "Backplane/Ariva.Core/Validation/Comparison/ComparisonResults.cs", "Backplane/Ariva.Core/Validation/Comparison/NowcastErrors.cs"
    ];

    /// <summary>The comparison engine's files that name the sensor cycle time the shadow took (ARV-104f).</summary>
    private static readonly string[] ComparisonCycleSources =
    [
        "Backplane/Ariva.Core/Validation/Comparison/ComparisonData.cs", "Backplane/Ariva.Core/Validation/Comparison/ComparisonInputs.cs",
        "Backplane/Ariva.Core/Validation/Comparison/NowcastErrors.cs", "Backplane/Ariva.Core/Validation/Comparison/NowcastResults.cs"
    ];

    /// <summary>
    /// A type of the comparison engine: declared in Ariva.Core and in exactly the engine's namespace (no sub-namespace, no other
    /// assembly that reuses the name; narrowed after the security review of ARV-104f).
    /// </summary>
    private static bool IsComparisonEngine(Type type) =>
        type is not null && type.Assembly == typeof(Ariva.Core._IAssemblyMark).Assembly && string.Equals(type.Namespace, ComparisonNamespace, StringComparison.Ordinal);

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
        // ARV-104g1: the validation results projection (ARV-104g) alone may name the shadow's figures it serves.
        exposed.Where(t => !IsResultsProjection(t))
            .SelectMany(t => t.GetMembers(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(m => m.Name.Contains("Shadow", StringComparison.OrdinalIgnoreCase)).Select(m => $"{t.FullName}.{m.Name}"))
            .Should().BeEmpty("no member of a screen, rule or report type but the validation results projection is named after the shadow");
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
                .Select(m => (Owner: Owner(t).FullName, OwnerType: Owner(t), Member: $"{t.FullName}.{m.Name}")))
            .ToList();

        named.Should().Contain(n => n.Member == "Ariva.Core.Queueing.QueueLiveMinute.Shadow", "the reflection sees the write path's own member");
        named.Should().Contain(n => n.Member == "Ariva.Core.Validation.Comparison.NowcastZoneErrors.Shadow", "the reflection sees the comparison's own member");
        named.Should().Contain(n => n.Member == $"{ValidationService}.ReadShadowAsync", "the reflection sees the validation service's read (ARV-104g1)");
        named.Where(n => !allowed.Contains(n.Owner) && !IsComparisonEngine(n.OwnerType) && !IsValidationReadPath(n.OwnerType)).Select(n => n.Member).Should().BeEmpty(
            "only the engine, its outputs, the stream's checkpoint, worker and store, the comparison engine (ARV-104f), the validation service and the " +
            "results projection (ARV-104g1) have members named after the shadow");
    }

    /// <summary>
    /// Every type that carries the stored shadow or the figures computed from it (the reading, the minute, the statistics, the
    /// summaries, the paired errors, the per-reason counts and the result), in the comparison engine.
    /// </summary>
    private static readonly string[] ShadowCarriers =
    [
        "ShadowMinuteRow", "NowcastReading", "NowcastMinuteError", "NowcastErrorStats", "NoServiceCount", "NowcastErrorSummary", "NowcastPairedErrors",
        "NowcastZoneErrors", "ComparisonResult"
    ];

    [Fact]
    public void ShadowRows_Should_StayInTheEngineAndTheValidationReadPath_When_TheirHoldersAreReflected()
    {
        // ARV-104f: the comparison engine is handed the stored shadow as rows. ARV-104g1 (changed on purpose): outside the engine,
        // only the validation service (which reads the rows through the reader login and runs the engine) and the results
        // projection (ARV-104g) may hold them or the results that carry the shadow's figures, so nothing else can show, alert on,
        // report or publish them.
        var core = typeof(Ariva.Core._IAssemblyMark).Assembly;
        foreach (var carrier in ShadowCarriers)
        {
            var type = core.GetType($"{ComparisonNamespace}.{carrier}", throwOnError: true)!;
            IsComparisonEngine(type).Should().BeTrue();
            HoldersOf(type).Select(Owner).Where(t => !IsComparisonEngine(t) && !IsValidationReadPath(t)).Select(t => t.FullName).Should().BeEmpty(
                $"only the comparison engine, the validation service and the results projection hold {type.Name}");
        }

        // The read path is seen: the validation service holds the rows it reads (its shadow part, ARV-104g1).
        HoldersOf(typeof(Ariva.Core.Validation.Comparison.ShadowMinuteRow)).Select(Owner).Select(t => t.FullName).Should().Contain(ValidationService);

        // The narrowed checks themselves: a sub-namespace, another namespace or another assembly is not the engine, and only the
        // two named types (and what is nested in them) are the read path.
        IsComparisonEngine(typeof(Ariva.Core.Validation.Comparison.ComparisonResult)).Should().BeTrue();
        IsComparisonEngine(typeof(Ariva.Core.Queueing.ShadowNowcast)).Should().BeFalse();
        IsComparisonEngine(typeof(ShadowNowcastExposureTests)).Should().BeFalse();
        IsComparisonEngine(typeof(Ariva.Infra.Streaming.StreamStore)).Should().BeFalse();
        IsValidationReadPath(typeof(Ariva.Infra.Services.Validation.SvcValidationResults)).Should().BeTrue();
        IsValidationReadPath(typeof(Ariva.Infra.Services.Validation.SvcValidationCampaigns)).Should().BeFalse();
        IsValidationReadPath(typeof(Ariva.Core.Domain.ViewModels.ValidationCampaignViewModel)).Should().BeFalse();
        IsValidationReadPath(typeof(ShadowNowcastExposureTests)).Should().BeFalse();
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

        // ARV-104g1 (changed on purpose): exactly one reader's file, the validation service's shadow part, which reads the table
        // through the validation reader login; and, after the review (M2), the access check that every host and the migration job
        // run, which names the table only to ask has_column_privilege about it and reads no row (ShadowReadAccess).
        SourcesMatching(table, SourceKinds).Should().Equal(
            ["Backplane/Ariva.Core/Queueing/ShadowNowcast.cs", "Backplane/Ariva.Core/Queueing/ZoneProcessor.cs", ReaderSource,
                "Backplane/Ariva.Infra/Streaming/StreamStore.cs",
                "Backplane/Ariva.Infra/Timescale/Scripts/0041_shadow_nowcast.sql", "Backplane/Ariva.Infra/Timescale/Scripts/0043_queue_minute_shadow.sql",
                "Backplane/Ariva.Infra/Timescale/Scripts/0044_desk_sensor_minute.sql", "Backplane/Ariva.Infra/Timescale/Scripts/0045_shadow_sensor_cycle.sql",
                AccessCheckSource],
            "only the engine names the shadow, only the stream store writes its table and only the validation service's reader reads it; no screen, rule or report does");

        // The value itself: only the engine that computes it, the store that writes it and (ARV-104f) the comparison engine's
        // files that compare it name it.
        // \bShadow\s*: catches property patterns ({ Shadow: var s }) and named arguments that reach it without a member access.
        var value = new Regex(@"\bShadowNowcasts?\b|\.Shadow\b|\bShadow\s*:", RegexOptions.CultureInvariant, RegexTimeout);
        SourcesMatching(value, ".cs").Should().Equal(
            ["Backplane/Ariva.Core/Queueing/ShadowNowcast.cs", "Backplane/Ariva.Core/Queueing/ZoneProcessor.cs", .. ComparisonShadowSources,
                "Backplane/Ariva.Infra/Streaming/StreamStore.cs"]);
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

        // The engine and its samples: the desk feed runs the engine, the desk term and DeskTerms read the samples, and
        // (ARV-117b) the sensor cycle time sums their Serving seconds (the simulator, which is no Ariva host, has members of
        // its own named Sensor).
        var engine = new Regex(@"\bSensorOnlyDeskEngine\b|\bDeskSensorSample\b|\.Sensor\b", RegexOptions.CultureInvariant, RegexTimeout);
        SourcesMatching(engine, ".cs").Where(f => f.StartsWith("Backplane/", StringComparison.Ordinal)).Should().Equal(
            ["Backplane/Ariva.Core/Desks/SensorOnlyDeskEngine.cs", "Backplane/Ariva.Core/Queueing/DeskTerm.cs", "Backplane/Ariva.Core/Queueing/SensorCycle.cs",
                "Backplane/Ariva.Infra/Border/DeskFeed.cs", "Backplane/Ariva.Infra/Streaming/DeskTermSource.cs"]);
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

    #region ARV-117b: the sensor cycle time

    [Fact]
    public void SensorCycleValues_Should_StayOnTheStreamsShadowPath_When_TheirHoldersAreReflected()
    {
        // ARV-117b (CWE-862, CWE-863): the sensor cycle time is held only in memory for the shadow and in
        // queue_minute_shadow. Its result is held by no type at all (the zone computes it per minute and hands its value to
        // the shadow nowcast, whose holders are pinned above); the busy window it is computed from (aggregates, no desk
        // key) rides on the desk term's sensor-only part, held by the zone processor and the stream's desk term source and
        // worker. None of them reaches the live snapshot, the hub, displays, alert inputs, reports, view models,
        // controllers or the AMAN contracts.
        HoldersOf(typeof(SensorCycleResult)).Select(Owner).Select(t => t.FullName).Distinct().Order(StringComparer.Ordinal)
            .Should().BeEquivalentTo(["Ariva.Core.Queueing.SensorCycleResult"], "the sensor cycle time's result is never stored in a field");
        var busy = HoldersOf(typeof(SensorBusyWindow)).Select(Owner).Select(t => t.FullName).Distinct().Order(StringComparer.Ordinal).ToList();
        busy.Should().BeEquivalentTo(BusyWindowHolders);

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
        exposed.Should().Contain(typeof(Ariva.Infra.Live.LiveZoneSnapshot));
        exposed.Where(t => busy.Contains(t.FullName)).Should().BeEmpty();
        exposed.SelectMany(t => t.GetMembers(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(m => m.Name.Contains("SensorCycle", StringComparison.OrdinalIgnoreCase) || m.Name.Contains("SensorBusy", StringComparison.OrdinalIgnoreCase))
                .Select(m => $"{t.FullName}.{m.Name}"))
            .Should().BeEmpty("no member of a screen, rule or report type is named after the sensor cycle time");
    }

    private static readonly string[] BusyWindowHolders =
    [
        "Ariva.Core.Queueing.DeskTerm",
        "Ariva.Core.Queueing.SensorBusyWindow",
        "Ariva.Core.Queueing.ZoneProcessor",
        "Ariva.Infra.Streaming.DeskTermSource",
        "Ariva.Infra.Streaming.QueueStreamWorker"
    ];

    [Fact]
    public void Sources_Should_NameTheSensorCycleTimeOnlyOnTheShadowPath_When_Scanned()
    {
        // The column (script 0045) is written by the stream store only; the formula, the desk term that carries its busy
        // window, the zone that computes it, the desk term source and worker that pass its settings, and nothing else.
        var cycle = new Regex(@"sensor_?cycle|sensor_?busy|shadow_?cycle", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

        SourcesMatching(cycle, SourceKinds).Should().Equal(SensorCycleSources,
            "only the engine computes the sensor cycle time, only the stream store writes it, only the validation service's reader reads it (ARV-104g1) " +
            "and only the comparison engine (ARV-104f) compares it; no screen, rule or report reads it");
    }

    private static readonly string[] SensorCycleSources =
    [
        "Backplane/Ariva.Core/Queueing/DeskTerm.cs", "Backplane/Ariva.Core/Queueing/SensorCycle.cs", "Backplane/Ariva.Core/Queueing/ShadowNowcast.cs",
        "Backplane/Ariva.Core/Queueing/ZoneProcessor.cs", .. ComparisonCycleSources, ReaderSource, "Backplane/Ariva.Infra/Streaming/DeskTermSource.cs",
        "Backplane/Ariva.Infra/Streaming/QueueStreamWorker.cs", "Backplane/Ariva.Infra/Streaming/StreamStore.cs",
        "Backplane/Ariva.Infra/Timescale/Scripts/0045_shadow_sensor_cycle.sql"
    ];

    #endregion

    #region ARV-104g1: signatures of exposed types

    /// <summary>
    /// A type that serves screens, rules, reports or other systems: controllers of every host, hubs, view models, reports,
    /// alerting, the live snapshot, displays and the contracts (the AMAN feed and the domain contracts).
    /// </summary>
    private static bool IsExposed(Type type) => type.FullName is { } name && (
        name.Contains(".Controllers.", StringComparison.Ordinal) ||
        name.Contains(".Hubs.", StringComparison.Ordinal) ||
        name.Contains(".ViewModels.", StringComparison.Ordinal) ||
        name.EndsWith("ViewModel", StringComparison.Ordinal) ||
        name.Contains(".Reports.", StringComparison.Ordinal) ||
        name.Contains(".Alerting.", StringComparison.Ordinal) ||
        name.Contains(".Live.", StringComparison.Ordinal) ||
        name.Contains(".Displays.", StringComparison.Ordinal) ||
        name.Contains(".Contracts.", StringComparison.Ordinal) ||
        name.StartsWith("Ariva.Business.Contracts.", StringComparison.Ordinal));

    /// <summary>
    /// Every type that carries the shadow: the stream's <see cref="ShadowNowcast"/>, the stored rows and the figures computed from
    /// them, <see cref="Ariva.Core.Validation.Comparison.ComparisonInput"/> and
    /// <see cref="Ariva.Core.Validation.Comparison.ComparisonResult"/>, and every Ariva type that holds one of them.
    /// </summary>
    private static HashSet<Type> ShadowBearing()
    {
        var core = typeof(Ariva.Core._IAssemblyMark).Assembly;
        var roots = ShadowCarriers.Select(c => core.GetType($"{ComparisonNamespace}.{c}", throwOnError: true)!)
            .Append(typeof(ShadowNowcast)).Append(typeof(Ariva.Core.Validation.Comparison.ComparisonInput));
        var bearing = new HashSet<Type>();
        foreach (var root in roots)
            bearing.UnionWith(HoldersOf(root));
        return bearing;
    }

    /// <summary>
    /// The members of <paramref name="types"/> whose signature (a method's or constructor's parameters and return type, a
    /// property's or field's type, an event's handler; generic arguments, arrays and tasks unwrapped) mentions a shadow-bearing
    /// type, except the results projection on either side.
    /// </summary>
    private static List<string> SignatureViolations(IEnumerable<Type> types, HashSet<Type> bearing)
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        bool Bears(Type type) => Mentioned(type).Any(m => !IsResultsProjection(m) && (bearing.Contains(m) || (m.IsGenericType && bearing.Contains(m.GetGenericTypeDefinition()))));
        var violations = new List<string>();
        foreach (var type in types.Where(t => !IsResultsProjection(t)))
        {
            foreach (var member in type.GetMembers(all))
            {
                var signature = member switch
                {
                    MethodInfo method => method.GetParameters().Select(p => p.ParameterType).Append(method.ReturnType),
                    ConstructorInfo constructor => constructor.GetParameters().Select(p => p.ParameterType),
                    PropertyInfo property => [property.PropertyType],
                    FieldInfo field => [field.FieldType],
                    EventInfo @event => [@event.EventHandlerType],
                    _ => []
                };
                foreach (var mentioned in signature.Where(t => t is not null && Bears(t)))
                    violations.Add($"{type.FullName}.{member.Name}: {mentioned}");
            }
        }

        return violations.Distinct(StringComparer.Ordinal).ToList();
    }

    [Fact]
    public void Signatures_Should_NeverTakeOrReturnTheShadow_When_TheTypeServesScreensRulesReportsOrContracts()
    {
        // ARV-104g1 (CWE-862, CWE-863): beside the holders and member names, the signatures. No controller, hub, view model, report,
        // alerting, live, display or contract type takes or returns ComparisonResult, ComparisonInput, the stored shadow rows, the
        // figures computed from them or any type that carries one of them, except the validation results projection (ARV-104g),
        // which alone maps the comparison's result to what Validation.View holders of the campaign's site see.
        var bearing = ShadowBearing();
        bearing.Should().Contain(typeof(Ariva.Core.Validation.Comparison.ComparisonResult)).And.Contain(typeof(Ariva.Core.Validation.Comparison.ComparisonInput))
            .And.Contain(typeof(Ariva.Core.Validation.Comparison.ShadowMinuteRow)).And.Contain(typeof(ShadowNowcast)).And.Contain(typeof(Ariva.Core.Queueing.QueueLiveMinute));
        bearing.Select(Owner).Should().Contain(typeof(Ariva.Infra.Services.Validation.SvcValidationResults), "the validation service's read holds the rows it reads");

        var exposed = Assemblies.SelectMany(TypesOf).Where(IsExposed).ToList();
        exposed.Should().Contain(t => t.FullName == "Ariva.Api.Main.Hubs.LiveHub").And.Contain(typeof(Ariva.Infra.Live.LiveZoneSnapshot))
            .And.Contain(typeof(Ariva.Infra.Alerting.AlertInputs)).And.Contain(typeof(Ariva.Core.Reports.ReportMinute))
            .And.Contain(typeof(Ariva.Core.Domain.ViewModels.ValidationCampaignViewModel))
            .And.Contain(t => t.FullName != null && t.FullName.StartsWith("Ariva.Api.Main.Controllers.OpsArea.Validation.", StringComparison.Ordinal))
            .And.Contain(t => t.FullName != null && t.FullName.StartsWith("Ariva.Business.Contracts.", StringComparison.Ordinal));
        exposed.Should().NotContain(typeof(Ariva.Infra.Services.Validation.SvcValidationResults), "the validation service is not exposed itself");

        SignatureViolations(exposed, bearing).Should().BeEmpty(
            "no screen, rule, report or contract type takes or returns the shadow, the comparison's input or its result but the validation results projection");
    }

    [Fact]
    public void SignatureViolations_Should_FindEveryWayInOrOut_When_AProbeTakesOrReturnsTheShadow()
    {
        // The check bites: a return value (also inside a task), a parameter (also inside a list), a constructor, a property (its
        // accessors and backing field), a field and an event over the shadow are each found; the clean probe is not.
        var bearing = ShadowBearing();
        SignatureViolations([typeof(LeakingProbe)], bearing).Should().BeEquivalentTo(
        [
            $"{typeof(LeakingProbe).FullName}.Get: {typeof(Task<Ariva.Core.Validation.Comparison.ComparisonResult>)}",
            $"{typeof(LeakingProbe).FullName}.Post: {typeof(IReadOnlyList<Ariva.Core.Validation.Comparison.ShadowMinuteRow>)}",
            $"{typeof(LeakingProbe).FullName}..ctor: {typeof(Ariva.Core.Validation.Comparison.ComparisonInput)}",
            $"{typeof(LeakingProbe).FullName}.get_Errors: {typeof(Ariva.Core.Validation.Comparison.NowcastZoneErrors[])}",
            $"{typeof(LeakingProbe).FullName}.set_Errors: {typeof(Ariva.Core.Validation.Comparison.NowcastZoneErrors[])}",
            $"{typeof(LeakingProbe).FullName}.Errors: {typeof(Ariva.Core.Validation.Comparison.NowcastZoneErrors[])}",
            $"{typeof(LeakingProbe).FullName}.<Errors>k__BackingField: {typeof(Ariva.Core.Validation.Comparison.NowcastZoneErrors[])}",
            $"{typeof(LeakingProbe).FullName}.Live: {typeof(ShadowNowcast)}",
            $"{typeof(LeakingProbe).FullName}.Updated: {typeof(Action<Ariva.Core.Validation.Comparison.ComparisonResult>)}",
            $"{typeof(LeakingProbe).FullName}.add_Updated: {typeof(Action<Ariva.Core.Validation.Comparison.ComparisonResult>)}",
            $"{typeof(LeakingProbe).FullName}.remove_Updated: {typeof(Action<Ariva.Core.Validation.Comparison.ComparisonResult>)}"
        ]);
        SignatureViolations([typeof(CleanProbe)], bearing).Should().BeEmpty();
        IsResultsProjection(typeof(CleanProbe)).Should().BeFalse();
        IsExposed(typeof(Ariva.Infra.Services.Validation.SvcValidationResults)).Should().BeFalse();
    }

#pragma warning disable CA1822, CA1812, CS0067, CS0649, IDE0051, IDE0060, S1144 // probes for the reflection above, never run
    private sealed class LeakingProbe
    {
        public LeakingProbe(Ariva.Core.Validation.Comparison.ComparisonInput input)
        {
        }

        public Task<Ariva.Core.Validation.Comparison.ComparisonResult> Get() => Task.FromResult<Ariva.Core.Validation.Comparison.ComparisonResult>(null);
        public int Post(IReadOnlyList<Ariva.Core.Validation.Comparison.ShadowMinuteRow> rows) => rows.Count;
        public Ariva.Core.Validation.Comparison.NowcastZoneErrors[] Errors { get; set; }
        public ShadowNowcast Live;
        public event Action<Ariva.Core.Validation.Comparison.ComparisonResult> Updated;
    }

    private sealed class CleanProbe
    {
        public Task<int> Get() => Task.FromResult(0);
        public string Name { get; set; }
    }
#pragma warning restore CA1822, CA1812, CS0067, CS0649, IDE0051, IDE0060, S1144

    #endregion

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
