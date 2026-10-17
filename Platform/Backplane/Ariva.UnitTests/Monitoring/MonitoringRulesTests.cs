using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.UnitTests.Monitoring;

/// <summary>
/// ARV-125b: the SigNoz alert rules and dashboards the chart ships (Charts/platform/files/monitoring) read only metrics that
/// exist: every metric is an instrument Ariva registers (ARV-125a), used as its kind allows (a counter through an increase
/// or a rate, a gauge never), or one of the reviewed external metrics (tests/monitoring.json: disk and the ingress
/// certificate, which Ariva cannot measure). Every label they filter, group or print by is on the allowlist and none names
/// a person, a credential or a document; every runbook link names a heading of the operations runbook; every rule the
/// story names has its source. The chart tests (checks.mjs) check the same on every render.
/// </summary>
public sealed partial class MonitoringRulesTests
{
    private const string Sources = "Platform/Cloud/Ariva.K8s/Helm/Charts/platform/files/monitoring";

    private static readonly Lazy<JsonObject> Spec = new(() =>
        JsonNode.Parse(File.ReadAllText(RepositoryPaths.Resolve("Platform/Cloud/Ariva.K8s/tests/monitoring.json")))!.AsObject());

    /// <summary>Every rule and dashboard source, by file name.</summary>
    private static IReadOnlyList<(string File, string Kind, JsonNode Json)> Files()
    {
        var files = new List<(string, string, JsonNode)>();
        foreach (var kind in new[] { "alerts", "dashboards" })
        {
            foreach (var path in Directory.EnumerateFiles(RepositoryPaths.Resolve($"{Sources}/{kind}"), "*.json").Order(StringComparer.Ordinal))
                files.Add(($"{kind}/{Path.GetFileName(path)}", kind, JsonNode.Parse(File.ReadAllText(path))!));
        }

        return files;
    }

    private static IReadOnlyList<string> Strings(string name) => [.. Spec.Value[name]!.AsArray().Select(n => n!.GetValue<string>())];

    /// <summary>Every instrument Ariva's metrics types publish once driven, with whether it is a counter.</summary>
    private static IReadOnlyDictionary<string, bool> RegisteredInstruments()
    {
        using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var factory = services.GetRequiredService<IMeterFactory>();
        var instruments = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, factory))
                    instruments[instrument.Name] = instrument.GetType().IsGenericType && instrument.GetType().GetGenericTypeDefinition() == typeof(Counter<>);
            }
        };
        listener.Start();
        var built = new List<IDisposable>();
        try
        {
            foreach (var (type, _) in MeterExportTests.MeterTypes())
            {
                var metrics = ActivatorUtilities.CreateInstance(services, type);
                built.Add((IDisposable)metrics);
                MetricTagTests.Drives[type](metrics);
            }
        }
        finally
        {
            foreach (var metrics in built)
                metrics.Dispose();
        }

        return instruments;
    }

    /// <summary>Every query of a source: the builder and PromQL queries of the rules and the dashboards' panels.</summary>
    private static IEnumerable<(string Type, JsonObject Spec)> Queries(JsonNode json)
    {
        foreach (var node in Walk(json))
        {
            if (node is not JsonObject item)
                continue;
            if (item["type"]?.GetValueKind() == JsonValueKind.String && item["spec"] is JsonObject spec)
                yield return (item["type"]!.GetValue<string>(), spec);
            if (item["kind"]?.GetValueKind() == JsonValueKind.String && item["kind"]!.GetValue<string>() == "signoz/BuilderQuery" && item["spec"] is JsonObject builder)
                yield return ("builder_query", builder);
        }
    }

    private static IEnumerable<JsonNode> Walk(JsonNode node)
    {
        if (node is null)
            yield break;
        yield return node;
        if (node is JsonObject obj)
        {
            foreach (var child in obj.Select(p => p.Value).SelectMany(Walk))
                yield return child;
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array.SelectMany(Walk))
                yield return child;
        }
    }

    /// <summary>The metric names a source reads, with the time aggregation of each builder query.</summary>
    private static IEnumerable<(string Metric, string TimeAggregation)> Metrics(JsonNode json)
    {
        foreach (var (type, spec) in Queries(json))
        {
            if (type == "builder_query")
            {
                foreach (var aggregation in spec["aggregations"]?.AsArray() ?? [])
                    yield return (aggregation!["metricName"]!.GetValue<string>(), aggregation["timeAggregation"]?.GetValue<string>());
            }
            else if (type == "promql")
            {
                // Without its strings, label matchers and grouping clauses, a PromQL query names metrics and functions.
                var query = PromQlNoise().Replace(spec["query"]!.GetValue<string>(), " ");
                foreach (Match name in PromQlNames().Matches(query))
                {
                    if (!PromQlWords.Contains(name.Value) && !query[(name.Index + name.Length)..].TrimStart().StartsWith('('))
                        yield return (name.Value, null);
                }
            }
        }
    }

    private static readonly HashSet<string> PromQlWords = new(StringComparer.Ordinal)
    {
        "by", "without", "on", "ignoring", "group_left", "group_right", "bool", "and", "or", "unless", "offset",
        "sum", "min", "max", "avg", "count", "group", "stddev", "stdvar", "topk", "bottomk", "quantile", "count_values"
    };

    /// <summary>The label keys a source filters, groups or prints by, and the labels its annotations use.</summary>
    internal static IEnumerable<string> Labels(JsonNode json)
    {
        foreach (var (_, spec) in Queries(json))
        {
            if (spec["filter"]?["expression"]?.GetValue<string>() is { Length: > 0 } filter)
            {
                foreach (Match key in FilterKeys().Matches(filter))
                    yield return key.Groups["key"].Value;
            }

            foreach (var group in spec["groupBy"]?.AsArray() ?? [])
                yield return group!["name"]!.GetValue<string>();
            if (spec["legend"]?.GetValue<string>() is { } legend)
            {
                foreach (Match key in LegendKeys().Matches(legend))
                    yield return key.Groups["key"].Value;
            }

            if (spec["query"]?.GetValue<string>() is { } promql)
            {
                foreach (Match clause in PromQlClauses().Matches(promql))
                {
                    foreach (var key in clause.Groups["list"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        yield return key.Trim('"');
                }

                foreach (Match key in PromQlMatchers().Matches(promql))
                    yield return key.Groups["key"].Value;
            }
        }

        foreach (var group in json["notificationSettings"]?["groupBy"]?.AsArray() ?? [])
            yield return group!.GetValue<string>();
        foreach (var annotation in json["annotations"]?.AsObject().Select(a => a.Value!.GetValue<string>()) ?? [])
        {
            foreach (Match key in AnnotationKeys().Matches(annotation))
            {
                if (key.Groups["key"].Value is not ("value" or "threshold"))
                    yield return key.Groups["key"].Value;
            }
        }
    }

    [Fact]
    public void EveryMetric_Should_BeARegisteredInstrumentOrAReviewedExternalOne_When_ARuleOrDashboardReadsIt()
    {
        var registered = RegisteredInstruments();
        var external = Spec.Value["externalMetrics"]!.AsArray().ToDictionary(m => m!["metric"]!.GetValue<string>(), m => m!, StringComparer.Ordinal);
        var used = Files().SelectMany(f => Metrics(f.Json).Select(m => (f.File, m.Metric, m.TimeAggregation))).ToList();

        used.Should().NotBeEmpty();
        used.Where(u => u.Metric.StartsWith("ariva.", StringComparison.Ordinal) && !registered.ContainsKey(u.Metric)).Select(u => $"{u.File}: {u.Metric}")
            .Should().BeEmpty("an Ariva metric a rule or dashboard reads is an instrument Ariva registers");
        used.Where(u => !u.Metric.StartsWith("ariva.", StringComparison.Ordinal) && !external.ContainsKey(u.Metric)).Select(u => $"{u.File}: {u.Metric}")
            .Should().BeEmpty("any other metric is on the reviewed external list (tests/monitoring.json)");
        foreach (var (file, metric, _) in used.Where(u => external.ContainsKey(u.Metric)))
        {
            var rule = Path.GetFileNameWithoutExtension(file);
            file.Should().StartWith("alerts/", $"{metric} is external: the dashboards show only what Ariva measures");
            external[metric]["rules"]!.AsArray().Select(r => r!.GetValue<string>()).Should().Contain(rule, $"{metric} is reviewed for the rules its entry names");
        }

        external.Keys.Should().OnlyContain(metric => used.Any(u => u.Metric == metric), "no stale entry on the external list");
    }

    [Fact]
    public void EveryArivaMetric_Should_BeAggregatedAsItsKindAllows_When_ABuilderQueryReadsIt()
    {
        var registered = RegisteredInstruments();
        foreach (var (file, metric, aggregation) in Files().SelectMany(f => Metrics(f.Json).Select(m => (f.File, m.Metric, m.TimeAggregation)))
                     .Where(u => registered.ContainsKey(u.Metric)))
        {
            if (registered[metric])
                aggregation.Should().BeOneOf(["increase", "rate"], $"{file}: {metric} is a counter, read as an increase or a rate");
            else
                aggregation.Should().BeOneOf(["latest", "max", "min", "avg", "sum"], $"{file}: {metric} is a gauge, never an increase or a rate");
        }
    }

    [Fact]
    public void EveryLabel_Should_BeAllowedAndNoneNamePersonCredentialOrDocument_When_ARuleOrDashboardUsesIt()
    {
        var labels = Spec.Value["labels"]!.AsObject();
        var allowed = labels.SelectMany(l => l.Value!.AsArray().Select(k => k!.GetValue<string>())).ToHashSet(StringComparer.Ordinal);
        var exceptions = Strings("forbiddenExceptions").ToHashSet(StringComparer.Ordinal);
        var forbidden = new Regex(Spec.Value["forbidden"]!.GetValue<string>(), RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

        var used = Files().SelectMany(f => Labels(f.Json).Select(label => (f.File, Label: label))).ToList();

        used.Should().NotBeEmpty();
        used.Where(u => !allowed.Contains(u.Label)).Select(u => $"{u.File}: {u.Label}").Should().BeEmpty("every label is on the allowlist (tests/monitoring.json)");
        used.Where(u => forbidden.IsMatch(u.Label) && !exceptions.Contains(u.Label)).Select(u => $"{u.File}: {u.Label}")
            .Should().BeEmpty("no label names a person, a credential or a document");
        allowed.Where(key => forbidden.IsMatch(key)).Should().BeSubsetOf(exceptions, "an allowed key that looks like one is a reviewed exception (a resource's name)");
    }

    [Fact]
    public void EveryQuery_Should_ReadItsOwnReleaseOnly_When_ARuleOrDashboardReadsAMetric()
    {
        // One SigNoz may take several environments and clusters: an Ariva metric is read for the release's
        // deployment.environment (the chart fills ${environment}), an external one within the release's namespaces.
        const string environment = "deployment.environment = '${environment}'";
        var queries = Files().SelectMany(f => Queries(f.Json).Select(q => (f.File, q.Type, q.Spec))).ToList();

        queries.Should().NotBeEmpty();
        foreach (var (file, type, spec) in queries)
        {
            var rule = Path.GetFileNameWithoutExtension(file);
            if (type == "builder_query")
            {
                var filter = spec["filter"]?["expression"]?.GetValue<string>() ?? "";
                foreach (var metric in spec["aggregations"]?.AsArray().Select(a => a!["metricName"]!.GetValue<string>()) ?? [])
                {
                    if (metric.StartsWith("ariva.", StringComparison.Ordinal))
                        (filter == environment || filter.StartsWith(environment + " AND ", StringComparison.Ordinal)).Should().BeTrue($"{file}: {metric} is read with {environment} first");
                    else
                        filter.Should().Contain($"k8s.namespace.name IN ${{{rule}.namespaces}}", $"{file}: the external {metric} is read in the release's namespaces");
                    filter.Should().NotContainEquivalentOf(" OR ", $"{file}: no OR widens the scope");
                }
            }
            else if (type == "promql")
            {
                spec["query"]!.GetValue<string>().Should().Contain($"{{${{{rule}.namespaceLabel}}=~\"${{{rule}.namespacesPattern}}\"", $"{file}: the PromQL query reads the release's namespaces");
            }
        }
    }

    [Fact]
    public void ArivaLabels_Should_BeTheTagKeysTheInstrumentsMayUse_When_Compared() =>
        Spec.Value["labels"]!["ariva"]!.AsArray().Select(k => k!.GetValue<string>()).Should().BeEquivalentTo(MetricTagTests.AllowedKeys,
            "the rules filter by the tags the instruments carry, one allowlist for both");

    [Fact]
    public void EveryRunbookLink_Should_NameAHeadingOfTheOperationsRunbook_When_ARuleCarriesIt()
    {
        var headings = File.ReadAllLines(RepositoryPaths.Resolve("wiki/10-Operations-Runbook.md"))
            .Where(line => line.StartsWith('#'))
            .Select(line => Slug(line.TrimStart('#').Trim()))
            .ToHashSet(StringComparer.Ordinal);
        var prefix = Spec.Value["runbook"]!.GetValue<string>();

        foreach (var (file, _, json) in Files().Where(f => f.Kind == "alerts"))
        {
            var link = json["annotations"]?["runbook"]?.GetValue<string>();
            link.Should().StartWith(prefix, $"{file} links the operations runbook");
            headings.Should().Contain(link![prefix.Length..], $"{file}: the anchor names a heading of wiki/10");
        }
    }

    [Fact]
    public void EveryRuleTheStoryNames_Should_HaveItsSource_When_TheChartShipsTheRules()
    {
        var sources = Files().Where(f => f.Kind == "alerts").Select(f => Path.GetFileNameWithoutExtension(f.File)).ToList();

        sources.Should().BeEquivalentTo(Strings("criterionRules"), "the criteria's rules and the notes' Kafka signals, nothing unreviewed");
        foreach (var (file, _, json) in Files().Where(f => f.Kind == "alerts"))
        {
            json["version"]!.GetValue<string>().Should().Be("v5", file);
            json["schemaVersion"]!.GetValue<string>().Should().Be("v2alpha1", file);
            json["alertType"]!.GetValue<string>().Should().Be("METRIC_BASED_ALERT", file);
        }

        foreach (var (file, _, json) in Files().Where(f => f.Kind == "dashboards"))
        {
            json["schemaVersion"]!.GetValue<string>().Should().Be("v6", file);
            json["spec"]!["panels"]!.AsObject().Should().NotBeEmpty(file);
        }
    }

    [Theory]
    [InlineData("4.6 Kafka consumer lag growing", "46-kafka-consumer-lag-growing")]
    [InlineData("4.5a Outbox backlog and dead letters (ARV-020)", "45a-outbox-backlog-and-dead-letters-arv-020")]
    [InlineData("2a. Metrics Ariva exports (ARV-125a)", "2a-metrics-ariva-exports-arv-125a")]
    public void Slug_Should_FollowGitHubsAnchors_When_AHeadingHasPunctuation(string heading, string slug) => Slug(heading).Should().Be(slug);

    [Fact]
    public void Labels_Should_FindEveryKey_When_ARuleFiltersGroupsAndPrints()
    {
        var json = JsonNode.Parse("""
            {"condition":{"compositeQuery":{"queries":[{"type":"builder_query","spec":{"name":"A","filter":{"expression":"check = 'kafka' AND user_id IN ['a'] AND NOT k8s.pod.name EXISTS"},
              "groupBy":[{"name":"service.name"}],"legend":"{{topic}}"}},{"type":"promql","spec":{"name":"B","query":"max by (email) (x{passport=\"1\"})"}}]}},
             "notificationSettings":{"groupBy":["group"]},"annotations":{"summary":"{{$traveller}} at {{$value}}"}}
            """)!;

        Labels(json).Should().BeEquivalentTo(["check", "user_id", "k8s.pod.name", "service.name", "topic", "email", "passport", "group", "traveller"]);
        Metrics(json).Select(m => m.Metric).Should().Equal(["x"], "functions, aggregations, label names and strings are not metrics");
    }

    /// <summary>GitHub's heading anchor: lower case, punctuation other than hyphens and underscores dropped, spaces to hyphens.</summary>
    private static string Slug(string heading) => string.Concat(heading.ToLowerInvariant()
        .Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ')).Replace(' ', '-');

    [GeneratedRegex(@"(?<key>[A-Za-z_][A-Za-z0-9_.]*)\s*(?:=|!=|<=|>=|<|>|\bNOT\s+IN\b|\bIN\b|\bNOT\s+LIKE\b|\bLIKE\b|\bCONTAINS\b|\bEXISTS\b|\bREGEXP\b)", RegexOptions.IgnoreCase)]
    private static partial Regex FilterKeys();

    [GeneratedRegex(@"\{\{\s*(?<key>[A-Za-z_][A-Za-z0-9_.]*)\s*\}\}")]
    private static partial Regex LegendKeys();

    [GeneratedRegex(@"\{\{\s*\$(?<key>[A-Za-z_][A-Za-z0-9_.]*)\s*\}\}")]
    private static partial Regex AnnotationKeys();

    // by (a, b), without (...), on (...), ignoring (...), group_left (...), group_right (...)
    [GeneratedRegex(@"\b(?:by|without|on|ignoring|group_left|group_right)\s*\((?<list>[^)]*)\)")]
    private static partial Regex PromQlClauses();

    // a label matcher inside braces: {a="x", b=~"y"}
    [GeneratedRegex(@"[{,]\s*(?<key>[A-Za-z_][A-Za-z0-9_]*)\s*(?:=~|!~|!=|=)")]
    private static partial Regex PromQlMatchers();

    // strings, label matchers in braces and grouping clauses
    [GeneratedRegex(@"""[^""]*""|'[^']*'|\{[^}]*\}|\b(?:by|without|on|ignoring|group_left|group_right)\s*\([^)]*\)")]
    private static partial Regex PromQlNoise();

    [GeneratedRegex(@"\b[a-zA-Z_:][a-zA-Z0-9_:]*\b")]
    private static partial Regex PromQlNames();
}
