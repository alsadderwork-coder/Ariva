using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Ariva.AppHost;
using Ariva.Di.Extensions;
using Ariva.Infra.Services.Seed;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.UnitTests.Seed;

/// <summary>
/// ARV-139c (CWE-200): the development-only NBJ-BC1 site, derived from a third party's confidential drawings, stays on the
/// owner's machine. <c>Seed:NbjSite</c> on refuses to start unless the host environment and <c>Application:Environment</c>
/// are both vm-local, outside a Codespace or a cloud agent session; it registers the seed only on top of
/// <c>Seed:DemoTopology</c>; a Release build carries no NBJ-BC1 type or text and refuses the setting; and no committed
/// settings file, chart, AppHost, E2E environment, script, pipeline, agent hook or dev container turns it on, except
/// run-ariva.ps1's switch through the AppHost's own key, line for line. These tests compile and run in Debug and in
/// Release (CI runs them in Release).
/// </summary>
public sealed partial class NbjSiteScopeTests
{
    #region Registration

    private static IServiceCollection Register(string hostEnvironment, string applicationEnvironment, bool demoTopology, bool? nbj, params (string Key, string Value)[] extra)
    {
        var settings = new Dictionary<string, string> { [DemoSeedExtensions.SettingName] = demoTopology ? "true" : "false" };
        foreach (var (key, value) in extra)
            settings[key] = value;
        if (applicationEnvironment is not null)
            settings["Application:Environment"] = applicationEnvironment;
        if (nbj is not null)
            settings[DemoSeedExtensions.NbjSettingName] = nbj.Value ? "true" : "false";
        return new ServiceCollection().AddArivaDemoSeed(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), hostEnvironment);
    }

    private static List<Type> Seeds(IServiceCollection services) =>
        services.Where(d => d.ServiceType == typeof(IDemoTopologySeed)).Select(d => d.ImplementationType).ToList();

    [Theory]
    [InlineData("k8s-dev", "k8s-dev", true)]
    [InlineData("k8s-dev", "k8s-dev", false)]
    [InlineData("k8s-demo", "k8s-demo", true)]
    [InlineData("k8s-demo", "k8s-demo", false)]
    [InlineData("k8s-prd", "k8s-prd", true)]
    [InlineData("k8s-prd", "k8s-prd", false)]
    [InlineData("vm-local", "k8s-demo", true)] // Application__Environment injected on a developer machine
    [InlineData("k8s-demo", "vm-local", true)] // a cluster whose settings say vm-local (Helm sets the host environment)
    [InlineData("vm-local", null, true)]
    [InlineData("", "vm-local", true)]
    [InlineData(null, "vm-local", false)]
    public void AddArivaDemoSeed_Should_RefuseToStart_When_NbjSiteIsOnOutsideVmLocal(string hostEnvironment, string applicationEnvironment, bool demoTopology)
    {
        var act = () => Register(hostEnvironment, applicationEnvironment, demoTopology, nbj: true);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Seed:NbjSite*developer machine only*");
    }

    [Theory]
    [InlineData("CODESPACES", "true")]
    [InlineData("CODESPACES", "TRUE")]
    [InlineData("CLAUDE_CODE_REMOTE", "true")]
    public void AddArivaDemoSeed_Should_RefuseToStart_When_NbjSiteIsOnInACodespaceOrACloudSession(string marker, string value)
    {
        var act = () => Register("vm-local", "vm-local", demoTopology: true, nbj: true, (marker, value));

#if ARIVA_DEV_SEED
        act.Should().Throw<InvalidOperationException>().WithMessage($"*Seed:NbjSite*owner's own machine only*{marker} is true*");
#else
        act.Should().Throw<InvalidOperationException>().WithMessage("*Seed:NbjSite*not a Debug build*", "a Release build refuses it first");
#endif
        // Off, the markers change nothing.
        Seeds(Register("vm-local", "vm-local", demoTopology: true, nbj: false, (marker, value))).Should().Equal(typeof(DemoTopologySeed), typeof(AuhTerminalASeed));
    }

    [Fact]
    public void AddArivaDemoSeed_Should_NameNoSitePathOrDocument_When_ItRefusesTheNbjSite()
    {
        var messages = new Func<IServiceCollection>[]
        {
            () => Register("k8s-dev", "k8s-dev", demoTopology: true, nbj: true),
            () => Register("vm-local", "vm-local", demoTopology: true, nbj: true, ("CODESPACES", "true")),
            () => Register("vm-local", "vm-local", demoTopology: true, nbj: true)
        }.Select(act =>
        {
            try
            {
                act();
                return null;
            }
            catch (InvalidOperationException e)
            {
                return e.Message;
            }
        }).Where(m => m is not null).ToList();

        messages.Should().NotBeEmpty();
        messages.Should().OnlyContain(m => !m.Contains("nbj-bc1", StringComparison.OrdinalIgnoreCase) && !m.Contains("nbjbc1", StringComparison.OrdinalIgnoreCase) &&
                                           !m.Contains("docs/", StringComparison.Ordinal) && !m.Contains(".md", StringComparison.Ordinal));
    }

    [Fact]
    public void AddArivaDemoSeed_Should_RegisterTheNbjSiteLast_When_OptedInOnVmLocal()
    {
        var act = () => Register("vm-local", "vm-local", demoTopology: true, nbj: true);

#if ARIVA_DEV_SEED
        Seeds(act()).Should().Equal(typeof(DemoTopologySeed), typeof(AuhTerminalASeed), typeof(NbjBc1Seed));
#else
        act.Should().Throw<InvalidOperationException>().WithMessage("*Seed:NbjSite*not a Debug build*");
#endif
    }

    [Fact]
    public void AddArivaDemoSeed_Should_RegisterNothing_When_NbjSiteIsOnWithoutDemoTopology()
    {
        var act = () => Register("vm-local", "vm-local", demoTopology: false, nbj: true);

#if ARIVA_DEV_SEED
        act().Should().BeEmpty("the NBJ site rides on the demo seed: without Seed:DemoTopology nothing is registered");
#else
        act.Should().Throw<InvalidOperationException>().WithMessage("*Seed:NbjSite*not a Debug build*");
#endif
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public void AddArivaDemoSeed_Should_LeaveTheNbjSiteOut_When_NotOptedIn(bool? nbj)
    {
        Seeds(Register("vm-local", "vm-local", demoTopology: true, nbj)).Should().Equal(typeof(DemoTopologySeed), typeof(AuhTerminalASeed));
        Seeds(Register("k8s-demo", "k8s-demo", demoTopology: true, nbj)).Should().Equal([typeof(DemoTopologySeed), typeof(AuhTerminalASeed)],
            "off, the setting changes nothing where the demo seed runs");
        Register("k8s-prd", "k8s-prd", demoTopology: false, nbj).Should().BeEmpty();
    }

    #endregion

    #region Release builds

    /// <summary>The site's texts, lower case: the code in both spellings, the type names' stem, the airport's name and city.</summary>
    private static readonly string[] SiteTexts = ["nbj-bc1", "nbjbc1", "agostinho", "luanda"];

    /// <summary>
    /// Whether an assembly's bytes hold a text in any letter case: ASCII letters folded to lower case, then searched as UTF-8
    /// (metadata names, the doc path's spelling) and as UTF-16 (string literals).
    /// </summary>
    private static bool Holds(Assembly assembly, string lowerCaseText)
    {
        var image = File.ReadAllBytes(assembly.Location);
        for (var i = 0; i < image.Length; i++)
        {
            if (image[i] is >= (byte)'A' and <= (byte)'Z')
                image[i] = (byte)(image[i] + 32);
        }

        return image.AsSpan().IndexOf(Encoding.UTF8.GetBytes(lowerCaseText)) >= 0 || image.AsSpan().IndexOf(Encoding.Unicode.GetBytes(lowerCaseText)) >= 0;
    }

    /// <summary>
    /// The Release proof that runs in CI: the backend job builds the solution and runs this project in Release (ci.yml), so
    /// this test then reads the Release Ariva.Infra.dll and Ariva.Di.dll it loaded and finds no NbjBc1 type and none of the
    /// site's texts in their bytes, in any letter case (type names in UTF-8 metadata, literals in UTF-16). In Debug it
    /// proves the switch is the build configuration: the same assemblies carry the seed.
    /// </summary>
    [Fact]
    public void NbjSeed_Should_BeCompiledIntoDebugBuildsOnly()
    {
        var infra = typeof(AuhTerminalASeed).Assembly;
        var di = typeof(DemoSeedExtensions).Assembly;
        var types = infra.GetTypes().Where(t => t.FullName?.Contains("NbjBc1", StringComparison.Ordinal) == true).Select(t => t.Name).ToList();
        var configuration = infra.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration;

#if ARIVA_DEV_SEED
        configuration.Should().Be("Debug");
        DemoSeedExtensions.NbjSeedCompiled.Should().BeTrue();
        types.Should().Contain(["NbjBc1Layout", "NbjBc1Plan", "NbjBc1Seed"]);
        Holds(infra, "nbjbc1seed").Should().BeTrue("the check reads type names");
        Holds(infra, "nbj-bc1").Should().BeTrue("the check reads string literals in any letter case");
        Holds(di, "nbjbc1seed").Should().BeTrue("a Debug Ariva.Di registers the seed");
#else
        configuration.Should().NotBe("Debug");
        DemoSeedExtensions.NbjSeedCompiled.Should().BeFalse();
        types.Should().BeEmpty("a Release Ariva.Infra.dll carries no NBJ-BC1 seed type");
        foreach (var assembly in new[] { infra, di })
        {
            foreach (var text in SiteTexts)
                Holds(assembly, text).Should().BeFalse("a Release {0} holds no '{1}' in any letter case", assembly.GetName().Name, text);
        }
#endif
    }

    #endregion

    #region Committed configuration

    /// <summary>A mention of the setting in any notation: NbjSite, Seed__NbjSite, Seed:NbjSite, nbjSite, nbj_site (not a longer word).</summary>
    [GeneratedRegex(@"nbj[_-]?site\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Mention();

    /// <summary>A mention that sets it false, in JSON, YAML, an environment variable, a command line, C# or TypeScript.</summary>
    [GeneratedRegex(@"nbj[_-]?site[""']?\s*[:=,]\s*[""']?false\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SetFalse();

    /// <summary>An assignment to a variable of the session in PowerShell: the switch must never set one.</summary>
    [GeneratedRegex(@"\$env:\w+\s*=", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SessionVariable();

    /// <summary>
    /// The only committed lines that may mention the setting without setting it false, by file (trimmed, exact): the owner's
    /// switch in run-ariva.ps1, which reaches the AppHost only as <c>--AppHost:NbjSite=true</c>; the AppHost's own key and
    /// property; and the one gated line that gives api-main its value, after the host variables file.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> AllowedLines = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["run-ariva.ps1"] =
        [
            ".PARAMETER NbjSite",
            @".\run-ariva.ps1 -NbjSite",
            "[switch] $NbjSite",
            "$DevelopmentSite = $NbjSite.IsPresent",
            """$appHostArguments = if ($DevelopmentSite) { "'--AppHost:NbjSite=true' '--AppHost:AccountsFile=$AppHostSettings'" } else { "'--AppHost:HostEnvironmentFile=$AppHostSettings'" }"""
        ],
        ["Platform/Cloud/Ariva.AppHost/AppHost.cs"] = ["""host.WithEnvironment("Seed__NbjSite", settings.NbjSite ? "true" : "false");"""],
        ["Platform/Cloud/Ariva.AppHost/AppHostSettings.cs"] =
        [
            """public const string DevelopmentSiteKey = "AppHost:NbjSite";""",
            "public bool NbjSite { get; init; }",
            "private bool DevelopmentSite => NbjSite;"
        ]
    };

    private static string Relative(string file) => Path.GetRelativePath(RepositoryPaths.Root, file).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>The lines of a text that mention the setting without setting it false (a true, a variable or a value on the next line).</summary>
    private static IEnumerable<string> Offending(string relativePath, string text)
    {
        var allowed = AllowedLines.TryGetValue(relativePath, out var lines) ? lines : [];
        return text.Split('\n').Select(line => line.Trim())
            .Where(line => Mention().Count(line) != SetFalse().Count(line) && !allowed.Contains(line, StringComparer.Ordinal));
    }

    private static bool Skipped(string file) =>
        file.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj" or "node_modules") ||
        file.EndsWith("appsettings.local.json", StringComparison.Ordinal);

    private static IEnumerable<string> Files(string relativeFolder, params string[] patterns)
    {
        var folder = RepositoryPaths.Resolve(relativeFolder);
        return Directory.Exists(folder)
            ? patterns.SelectMany(p => Directory.EnumerateFiles(folder, p, SearchOption.AllDirectories)).Where(f => !Skipped(f)).Distinct()
            : [];
    }

    /// <summary>
    /// The committed sources that configure a run, by who runs it: the hosts' and the simulator's settings, the Helm charts
    /// and Helmfile, the Aspire AppHost, the E2E environment (playwright.config.ts and its scripts), the developer scripts
    /// (dev-up, demo-local, run-ariva), CI (GitHub Actions and Azure DevOps), Codespaces (.devcontainer, Compose) and the
    /// agents' settings and hooks (.claude, read only). The git-ignored appsettings.local.json is the owner's own and is
    /// not read.
    /// </summary>
    public static TheoryData<string> Sources => new()
    {
        "hosts", "simulation", "helm", "apphost", "e2e", "scripts", "ci", "codespaces", "agents"
    };

    private static IReadOnlyList<string> SourceFiles(string source) => source switch
    {
        "hosts" => [.. Files("Platform/Backplane", "appsettings*.json", "launchSettings.json", "environment.json")],
        "simulation" => [.. Files("Platform/Simulation", "appsettings*.json", "launchSettings.json", "environment.json")],
        "helm" => [.. Files("Platform/Cloud/Ariva.K8s/Helm", "*.yaml", "*.yml", "*.tpl", "*.gotmpl", "*.json")],
        "apphost" => [.. Files("Platform/Cloud/Ariva.AppHost", "*.cs", "*.json")],
        "e2e" => [RepositoryPaths.Resolve("Platform/Testing/Ariva.E2E/playwright.config.ts"),
            .. Files("Platform/Testing/Ariva.E2E/scripts", "*.mjs", "*.ts", "*.js"), .. Files("Platform/Testing/Ariva.E2E/tests/support", "*.ts")],
        "scripts" => [.. Files("scripts", "*.mjs"), RepositoryPaths.Resolve("run-ariva.ps1"), RepositoryPaths.Resolve("run-ariva.cmd")],
        "ci" => [.. Files(".github/workflows", "*.yml", "*.yaml"), .. Files("Platform/Cloud/Ariva.Cicd/AzureDevOps", "*.yml", "*.yaml")],
        "codespaces" => [.. Files(".devcontainer", "*"), .. Directory.EnumerateFiles(RepositoryPaths.Root, "docker-compose*.yml"), RepositoryPaths.Resolve(".env.example")],
        "agents" => [RepositoryPaths.Resolve(".claude/settings.json"), .. Files(".claude/hooks", "*.mjs")],
        _ => throw new ArgumentOutOfRangeException(nameof(source))
    };

    [Theory]
    [MemberData(nameof(Sources))]
    public void CommittedSources_Should_NeverTurnOnTheNbjSite_When_TheyConfigureARun(string source)
    {
        var files = SourceFiles(source);

        files.Should().NotBeEmpty("the {0} sources are where this test expects them", source);
        files.Should().OnlyContain(f => File.Exists(f));
        files.SelectMany(f => Offending(Relative(f), File.ReadAllText(f)).Select(line => $"{Relative(f)}: {line}")).Should()
            .BeEmpty("Seed:NbjSite is turned on only on the owner's machine, never by a committed {0} source (docs/demo/nbj-bc1.md)", source);
    }

    [Fact]
    public void AllowedLines_Should_EachStandOnceInTheirFile_AndTheScriptSetNoSessionVariableAndRefuseASwitchedRun()
    {
        foreach (var (relativePath, lines) in AllowedLines)
        {
            var text = File.ReadAllText(RepositoryPaths.Resolve(relativePath)).Split('\n').Select(line => line.Trim()).ToList();
            foreach (var line in lines)
                text.Count(l => l == line).Should().Be(1, "{0} holds the allowed line once: {1}", relativePath, line);
        }

        var script = File.ReadAllText(RepositoryPaths.Resolve("run-ariva.ps1"));
        SessionVariable().IsMatch(script).Should().BeFalse("the switch reaches the AppHost as its argument, never as a variable of the session");
        script.Should().NotContain("Seed__", "the script never names the hosts' seed setting");
        // Every run of the script refuses to start beside a switched run's database container, which outlives the AppHost,
        // and looks for it on the volume the AppHost gives a switched run.
        script.Should().Contain($"docker ps -q --filter volume={new AppHostSettings().DatabaseVolume}{AppHostSettings.DevelopmentSiteVolumeSuffix}");
    }

    [Fact]
    public void Playwright_Should_PinTheNbjSiteOffForApiMain()
    {
        var config = File.ReadAllText(RepositoryPaths.Resolve("Platform/Testing/Ariva.E2E/playwright.config.ts"));
        var start = config.IndexOf("'api-main': dotnetHost(", StringComparison.Ordinal);
        var end = start < 0 ? -1 : config.IndexOf("'api-integration': dotnetHost(", start, StringComparison.Ordinal);

        start.Should().BeGreaterThanOrEqualTo(0, "playwright.config.ts starts api-main");
        end.Should().BeGreaterThan(start);
        config[start..end].Should().Contain("Seed__NbjSite: 'false'", "the E2E run pins the setting off, whatever the shell or appsettings.local.json says");
    }

    [Fact]
    public void VmLocalSettings_Should_KeepTheDemoSeedOnAndNameNoNbjSite()
    {
        var file = RepositoryPaths.Resolve("Platform/Backplane/Ariva.Api.Main/appsettings.service.vm-local.json");
        var configuration = new ConfigurationBuilder().AddJsonFile(file).Build();

        configuration.GetValue<bool>(DemoSeedExtensions.SettingName).Should().BeTrue("DMO and AUH-TA stay on for every developer");
        configuration[DemoSeedExtensions.NbjSettingName].Should().BeNull("the owner turns the NBJ site on for a run, not the committed file");
        Mention().IsMatch(File.ReadAllText(file)).Should().BeFalse();
    }

    [Theory]
    [InlineData("\"Seed\": { \"NbjSite\": true }", false)]
    [InlineData("\"Seed:NbjSite\": \"True\"", false)]
    [InlineData("Seed__NbjSite: 'true'", false)]
    [InlineData(".WithEnvironment(\"Seed__NbjSite\", \"true\")", false)]
    [InlineData(".WithEnvironment(\"Seed__NbjSite\", nbj)", false)]
    [InlineData("- name: Seed__NbjSite\n  value: \"false\"", false)]
    [InlineData("nbjSite: {{ .Values.nbj }}", false)]
    [InlineData("export Seed__NbjSite=1", false)]
    [InlineData("--Seed:NbjSite=true", false)]
    [InlineData("[switch] $NbjSite", false)] // allowed in run-ariva.ps1 only
    [InlineData("Seed__NbjSite: 'false'", true)]
    [InlineData("\"NbjSite\": false", true)]
    [InlineData("\"DemoTopology\": true", true)]
    [InlineData("// NbjSiteScopeTests", true)]
    public void Offending_Should_AcceptOnlyMentionsThatSetItFalse_When_TheFileHasNoAllowedLines(string text, bool accepted)
    {
        Offending("Platform/Testing/Ariva.E2E/playwright.config.ts", text).Any().Should().Be(!accepted, text);
    }

    [Fact]
    public void Offending_Should_AllowAnExactLine_OnlyInItsOwnFile()
    {
        Offending("run-ariva.ps1", "    [switch] $NbjSite").Should().BeEmpty();
        Offending("run-ariva.ps1", "    [switch] $NbjSite = $true").Should().NotBeEmpty("only the exact line");
        Offending("Platform/Cloud/Ariva.AppHost/AppHost.cs", """host.WithEnvironment("Seed__NbjSite", "true");""").Should().NotBeEmpty();
        Offending("scripts/demo-local.mjs", "[switch] $NbjSite").Should().NotBeEmpty("another file has no allowed lines");
    }

    #endregion
}
