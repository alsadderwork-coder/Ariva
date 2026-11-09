using Ariva.Api.Common.Hosting;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Ariva.UnitTests.Hosting;

/// <summary>
/// ARV-136a (CWE-501): a k8s-prd host refuses to start while a go-live setting (docs/product/pilot-to-confirm.md) is not set
/// by a k8s-prd source (a k8s-prd settings file, an environment variable, the command line): not set, blank, still reading
/// the placeholder, set by any other source, or in a shape the binder would answer with the code's default. Every host checks
/// it right after its environment check and before any command, and the settings each host ships confirm none of them.
/// </summary>
public sealed class GoLiveSettingsTests : IDisposable
{
    #region Fields

    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("ariva-golive-");

    #endregion

    #region Setup

    public void Dispose() => _folder.Delete(recursive: true);

    private static (string Key, string Value)[] Answers => [.. ArivaHosts.GoLiveAnswers];

    private static (string Key, string Value)[] AnswersWithout(string key) => [.. ArivaHosts.GoLiveAnswers.Where(a => a.Key != key)];

    /// <summary>A settings file in the test's folder holding <paramref name="values"/>, as nested JSON.</summary>
    private string File(string name, params (string Key, string Value)[] values)
    {
        var root = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            var parts = key.Split(':');
            var node = root;
            foreach (var part in parts[..^1])
            {
                if (!node.TryGetValue(part, out var child))
                    node[part] = child = new Dictionary<string, object>(StringComparer.Ordinal);
                node = (Dictionary<string, object>)child;
            }

            node[parts[^1]] = value;
        }

        var path = Path.Combine(_folder.FullName, name);
        System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(root));
        return path;
    }

    /// <summary>The values as a release's command line would give them.</summary>
    private static string[] CommandLine(params (string Key, string Value)[] values) => [.. values.Select(v => $"--{v.Key}={v.Value}")];

    /// <summary>The answers in a k8s-prd file over a reference file that holds them too, with <paramref name="override"/> in the k8s-prd file.</summary>
    private IConfigurationRoot K8sPrdFile(params (string Key, string Value)[] k8sPrd) => new ConfigurationBuilder()
        .AddJsonFile(File("appsettings.base.json", Answers))
        .AddJsonFile(File("appsettings.base.k8s-prd.json", k8sPrd))
        .Build();

    #endregion

    #region Sources

    [Fact]
    public void Problems_Should_NameEverySetting_When_NothingIsSet()
    {
        var problems = GoLiveSettings.Problems(new ConfigurationBuilder().Build());

        problems.Should().HaveCount(GoLiveSettings.All.Count).And.OnlyContain(p => p.EndsWith(" not set", StringComparison.Ordinal));
        foreach (var setting in GoLiveSettings.All)
            problems.Should().ContainSingle(p => p.StartsWith(setting.Key + " (" + setting.Item + ", ", StringComparison.Ordinal));
        GoLiveSettings.All.Select(s => s.Item).Distinct().Should().BeEquivalentTo(["TC-01", "TC-02", "TC-44", "TC-47"],
            "TC-03, TC-08 and TC-09 are a site's data, not settings");
    }

    [Theory]
    [InlineData("appsettings.base.json")]
    [InlineData("appsettings.service.json")]
    [InlineData("appsettings.local.json")]
    [InlineData("appsettings.json")]
    [InlineData("appsettings.base.k8s-dev.json")]
    [InlineData("k8s-prd.json.bak")]
    public void Problems_Should_RefuseTheValue_When_OnlyAFileThatIsNotAK8sPrdFileSetsIt(string file)
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(File(file, Answers)).Build();

        GoLiveSettings.Problems(configuration).Should().HaveCount(GoLiveSettings.All.Count)
            .And.OnlyContain(p => p.Contains(" only a reference default, from no k8s-prd source", StringComparison.Ordinal));
    }

    [Fact]
    public void Problems_Should_RefuseTheValue_When_AnInMemorySourceSetsIt()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(Answers.Select(a => new KeyValuePair<string, string>(a.Key, a.Value)))
            .Build();

        GoLiveSettings.Problems(configuration).Should().HaveCount(GoLiveSettings.All.Count, "an in-memory source is no k8s-prd source");
    }

    [Theory]
    [InlineData("appsettings.base.k8s-prd.json")]
    [InlineData("appsettings.service.k8s-prd.json")]
    [InlineData("APPSETTINGS.SERVICE.K8S-PRD.JSON")]
    public void Problems_Should_ConfirmEverySetting_When_AK8sPrdFileSetsIt(string k8sPrd)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(File("appsettings.base.json", Answers))
            .AddJsonFile(File(k8sPrd, Answers))
            .Build();

        GoLiveSettings.Problems(configuration).Should().BeEmpty();
    }

    [Fact]
    public void Problems_Should_ConfirmEverySetting_When_TheReleasesCommandLineSetsIt()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(File("appsettings.base.json", Answers))
            .AddCommandLine(CommandLine(Answers))
            .Build();

        GoLiveSettings.Problems(configuration).Should().BeEmpty();
    }

    [Fact]
    public void Problems_Should_ConfirmEverySetting_When_TheReleasesEnvironmentVariablesSetIt()
    {
        // As the chart's ConfigMap gives a pod its settings (Stream__CensorAfterMinutes), under a prefix of this test's own.
        var prefix = $"ARIVA_GOLIVE_TEST_{Guid.NewGuid():N}_";
        var names = Answers.Select(a => (Name: prefix + a.Key.Replace(":", "__", StringComparison.Ordinal), a.Value)).ToList();
        try
        {
            foreach (var (name, value) in names)
                Environment.SetEnvironmentVariable(name, value);
            var configuration = new ConfigurationBuilder()
                .AddJsonFile(File("appsettings.base.json", Answers))
                .AddEnvironmentVariables(prefix)
                .Build();

            GoLiveSettings.Problems(configuration).Should().BeEmpty();
        }
        finally
        {
            foreach (var (name, _) in names)
                Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void Problems_Should_ReadThroughAChainedConfiguration_When_ItWrapsTheSources()
    {
        var reference = new ConfigurationBuilder().AddJsonFile(File("appsettings.base.json", Answers)).Build();
        var k8sPrd = new ConfigurationBuilder().AddJsonFile(File("appsettings.base.k8s-prd.json", Answers)).Build();

        GoLiveSettings.Problems(new ConfigurationBuilder().AddConfiguration(reference).Build()).Should().HaveCount(GoLiveSettings.All.Count,
            "a chained configuration confirms only what its own source would");
        GoLiveSettings.Problems(new ConfigurationBuilder().AddConfiguration(k8sPrd).Build()).Should().BeEmpty();
    }

    [Fact]
    public void Problems_Should_ConfirmNothing_When_TheConfigurationIsNotARoot()
    {
        var root = new ConfigurationBuilder().AddCommandLine(CommandLine(Answers)).Build();

        GoLiveSettings.Problems(root).Should().BeEmpty();
        GoLiveSettings.Problems(new SectionOnly(root)).Should().HaveCount(GoLiveSettings.All.Count)
            .And.OnlyContain(p => p.EndsWith(" from settings whose sources cannot be told", StringComparison.Ordinal));
    }

    /// <summary>A configuration that reads through to another and is not a root, so its sources cannot be told.</summary>
    private sealed class SectionOnly(IConfiguration inner) : IConfiguration
    {
        public string this[string key] { get => inner[key]; set => inner[key] = value; }
        public IEnumerable<IConfigurationSection> GetChildren() => inner.GetChildren();
        public Microsoft.Extensions.Primitives.IChangeToken GetReloadToken() => inner.GetReloadToken();
        public IConfigurationSection GetSection(string key) => inner.GetSection(key);
    }

    #endregion

    #region Values and shapes

    [Theory]
    [InlineData("To confirm")]
    [InlineData("TO CONFIRM (TC-01)")]
    [InlineData("to  confirm with the border authority")]
    [InlineData("To\nconfirm")]
    public void Problems_Should_RefuseThePlaceholder_When_AK8sPrdFileStillHoldsIt(string placeholder)
    {
        var configuration = K8sPrdFile([.. AnswersWithout("Border:EgateCoupling:RejectLane"), ("Border:EgateCoupling:RejectLane", placeholder)]);

        GoLiveSettings.Problems(configuration).Should().ContainSingle()
            .Which.Should().StartWith("Border:EgateCoupling:RejectLane (TC-01, ").And.EndWith(" is still To confirm");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Problems_Should_RefuseABlankValue_When_AK8sPrdFileClearsTheReferenceOne(string blank)
    {
        var configuration = K8sPrdFile([.. AnswersWithout("Kafka:Topics:Partitions"), ("Kafka:Topics:Partitions", blank)]);

        GoLiveSettings.Problems(configuration).Should().ContainSingle().Which.Should().StartWith("Kafka:Topics:Partitions (TC-47, ").And.EndWith(" is not set");
    }

    [Fact]
    public void Problems_Should_RefuseAPart_When_TheK8sPrdFileLeavesItToTheReferenceFile()
    {
        var configuration = K8sPrdFile(AnswersWithout("Flights:ArrivalWave:Mix:Vis"));

        GoLiveSettings.Problems(configuration).Should().ContainSingle()
            .Which.Should().StartWith("Flights:ArrivalWave:Mix (TC-02, ").And.EndWith(" part Vis is only a reference default, from no k8s-prd source");
    }

    [Fact]
    public void Problems_Should_RefuseTheLaneMix_When_AK8sPrdSourceSetsOnlySomeOfItsParts()
    {
        // The binder would take the code's reference shares for the parts left out.
        var configuration = new ConfigurationBuilder().AddCommandLine(CommandLine([.. Answers.Where(a => !a.Key.StartsWith("Flights:ArrivalWave:Mix:", StringComparison.Ordinal)),
            ("Flights:ArrivalWave:Mix:Cit", "0.35")])).Build();

        GoLiveSettings.Problems(configuration).Should().ContainSingle().Which.Should().EndWith(" part Res is not set");
    }

    [Fact]
    public void Problems_Should_RefuseTheLaneMix_When_ASourceGivesItOneValue()
    {
        // An environment variable Flights__ArrivalWave__Mix holding text: the binder ignores it and runs the reference mix.
        var configuration = new ConfigurationBuilder()
            .AddCommandLine(CommandLine([.. Answers.Where(a => !a.Key.StartsWith("Flights:ArrivalWave:Mix:", StringComparison.Ordinal)), ("Flights:ArrivalWave:Mix", "{\"Cit\":0.5}")]))
            .Build();

        GoLiveSettings.Problems(configuration).Should().ContainSingle()
            .Which.Should().EndWith(" is one value where the parts Cit, Res, Vis, Crw, Trf, EGateShare belong");
    }

    [Fact]
    public void Problems_Should_RefuseTheLaneMix_When_ItHasAPartTheMixDoesNotHave()
    {
        var configuration = new ConfigurationBuilder().AddCommandLine(CommandLine([.. Answers, ("Flights:ArrivalWave:Mix:Transit", "0.1")])).Build();

        GoLiveSettings.Problems(configuration).Should().ContainSingle().Which.Should().EndWith(" has a part that is not one of Cit, Res, Vis, Crw, Trf, EGateShare");
    }

    [Theory]
    [InlineData("Border:EgateCoupling:RejectLane")]
    [InlineData("Stream:HandoverWindowSeconds")]
    [InlineData("Kafka:Topics:Partitions")]
    public void Problems_Should_RefuseAOneValueSetting_When_ASourceGivesItASectionInstead(string key)
    {
        // RejectLane:x binds RejectLane to its default, and an int? with only a child binds to 0.
        var configuration = new ConfigurationBuilder().AddCommandLine(CommandLine([.. AnswersWithout(key), ($"{key}:x", "CIT")])).Build();

        GoLiveSettings.Problems(configuration).Should().ContainSingle().Which.Should().StartWith(key + " (").And.EndWith(" is a section where one value belongs");
    }

    [Fact]
    public void Problems_Should_RefuseAPart_When_ItIsASectionItself()
    {
        var configuration = new ConfigurationBuilder()
            .AddCommandLine(CommandLine([.. AnswersWithout("Flights:ArrivalWave:Mix:Crw"), ("Flights:ArrivalWave:Mix:Crw:x", "0.02")]))
            .Build();

        GoLiveSettings.Problems(configuration).Should().ContainSingle().Which.Should().EndWith(" part Crw is a section where one value belongs");
    }

    #endregion

    #region EnsureConfirmed

    [Theory]
    [InlineData(ArivaEnvironment.VmLocal)]
    [InlineData(ArivaEnvironment.K8sDev)]
    [InlineData(ArivaEnvironment.K8sDemo)]
    public void EnsureConfirmed_Should_LetTheHostStartOnTheReferenceDefaults_When_ItIsNotK8sPrd(string environment)
    {
        var start = () => GoLiveSettings.EnsureConfirmed(new ConfigurationBuilder().Build(), environment);

        start.Should().NotThrow();
    }

    [Fact]
    public void EnsureConfirmed_Should_RefuseToStart_When_K8sPrdLeavesASettingUnconfirmed()
    {
        var configuration = new ConfigurationBuilder().AddCommandLine(CommandLine(AnswersWithout("Stream:CensorAfterMinutes"))).Build();

        var start = () => GoLiveSettings.EnsureConfirmed(configuration, ArivaEnvironment.K8sPrd);

        start.Should().Throw<InvalidOperationException>()
            .WithMessage("k8s-prd does not start on go-live settings nobody confirmed (docs/product/pilot-to-confirm.md): Stream:CensorAfterMinutes (TC-44, *) is not set. Set each one for k8s-prd*");
        var confirmed = () => GoLiveSettings.EnsureConfirmed(new ConfigurationBuilder().AddCommandLine(CommandLine(Answers)).Build(), ArivaEnvironment.K8sPrd);
        confirmed.Should().NotThrow();
    }

    [Fact]
    public void GoLiveAnswers_Should_AnswerEverySettingInItsShape_When_TheTestHostsAreGivenThem()
    {
        var expected = GoLiveSettings.All.SelectMany(s => s.Parts is null ? [s.Key] : s.Parts.Select(part => $"{s.Key}:{part}")).ToList();

        ArivaHosts.GoLiveAnswers.Select(a => a.Key).Should().BeEquivalentTo(expected, "one answer for every value the check reads, nothing else");
        ArivaHosts.GoLiveAnswers.Should().OnlyContain(a => !string.IsNullOrWhiteSpace(a.Value));
    }

    #endregion

    #region Hosts

    public static TheoryData<string> Hosts => new() { ArivaHosts.Main, ArivaHosts.Ingest, ArivaHosts.Stream, ArivaHosts.Cronz, ArivaHosts.Integration };

    /// <summary>The folder of a host's project, from its host name.</summary>
    private static string Project(string host) => host switch
    {
        ArivaHosts.Main => "Ariva.Api.Main",
        ArivaHosts.Ingest => "Ariva.Api.Ingest",
        ArivaHosts.Stream => "Ariva.Api.Stream",
        ArivaHosts.Cronz => "Ariva.Api.Cronz",
        ArivaHosts.Integration => "Ariva.Api.Integration",
        _ => throw new ArgumentOutOfRangeException(nameof(host), host, "Not a k8s-prd host.")
    };

    /// <summary>A host's own settings files, layered in its Program.cs's order, for k8s-prd.</summary>
    private static IConfigurationRoot ShippedK8sPrd(string host) => new ConfigurationBuilder()
        .AddJsonFile(RepositoryPaths.Resolve("Platform/Backplane/Ariva.Api.Common/appsettings.base.json"), optional: false)
        .AddJsonFile(RepositoryPaths.Resolve("Platform/Backplane/Ariva.Api.Common/appsettings.base.k8s-prd.json"), optional: true)
        .AddJsonFile(RepositoryPaths.Resolve($"Platform/Backplane/{Project(host)}/appsettings.service.json"), optional: false)
        .AddJsonFile(RepositoryPaths.Resolve($"Platform/Backplane/{Project(host)}/appsettings.service.k8s-prd.json"), optional: true)
        .Build();

    [Theory]
    [MemberData(nameof(Hosts))]
    public void Host_Should_ConfirmNothing_When_ItRunsOnTheSettingsItShips(string host)
    {
        var shipped = ShippedK8sPrd(host);

        GoLiveSettings.Problems(shipped).Should().HaveCount(GoLiveSettings.All.Count, "no answer is in yet, so the shipped k8s-prd settings confirm nothing");
        GoLiveSettings.Problems(shipped).Should().Contain(p => p.StartsWith("Kafka:Topics:Partitions (TC-47, ", StringComparison.Ordinal) &&
            p.EndsWith(" is only a reference default, from no k8s-prd source", StringComparison.Ordinal), "the base file's 6 partitions is a reference value");
        var answered = new ConfigurationBuilder().AddConfiguration(shipped).AddCommandLine(CommandLine(Answers)).Build();
        GoLiveSettings.Problems(answered).Should().BeEmpty("the release's command line or environment confirms each one");
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task Host_Should_RefuseToStartInK8sPrd_When_AGoLiveAnswerIsMissing(string host)
    {
        await using var app = ArivaHosts.Create(host, ArivaEnvironment.K8sPrd, builder => builder.UseSetting("Stream:CensorAfterMinutes", string.Empty));

        var start = () => app.Services;

        start.Should().Throw<InvalidOperationException>()
            .WithMessage("k8s-prd does not start on go-live settings nobody confirmed*Stream:CensorAfterMinutes (TC-44, *) is not set*");
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task Host_Should_StartInK8sPrd_When_EveryGoLiveAnswerIsIn(string host)
    {
        await using var app = ArivaHosts.Create(host, ArivaEnvironment.K8sPrd);

        var start = () => app.Services;

        start.Should().NotThrow();
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void Host_Should_CheckTheGoLiveSettings_When_ItsEnvironmentIsChecked(string host)
    {
        var program = System.IO.File.ReadAllLines(RepositoryPaths.Resolve($"Platform/Backplane/{Project(host)}/Program.cs"));
        var environment = Array.FindIndex(program, line => line.Trim() == "ArivaEnvironment.EnsureConfigured(builder.Configuration, environment);");
        var goLive = Array.FindIndex(program, line => line.Trim() == "GoLiveSettings.EnsureConfirmed(builder.Configuration, environment);");
        var firstReturn = Array.FindIndex(program, line => line.TrimStart().StartsWith("return", StringComparison.Ordinal));

        environment.Should().BeGreaterThan(0);
        goLive.Should().BeGreaterThan(environment, "the check follows the environment check");
        program.Skip(environment + 1).Take(goLive - environment - 1).Should().OnlyContain(line => line.TrimStart().StartsWith("//", StringComparison.Ordinal),
            "nothing reads a setting between the two checks");
        if (firstReturn >= 0)
            goLive.Should().BeLessThan(firstReturn, "no command (a migration, a replay, a break-glass) runs before the check");
        program.Count(line => line.Contains("GoLiveSettings.EnsureConfirmed", StringComparison.Ordinal)).Should().Be(1);
    }

    #endregion
}
