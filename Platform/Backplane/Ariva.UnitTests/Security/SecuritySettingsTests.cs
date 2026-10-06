using Ariva.Api.Common.Hosting;
using Ariva.Api.Common.Settings;
using Ariva.UnitTests.Setup;
using Bogus;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace Ariva.UnitTests.Security;

/// <summary>
/// Validation rules of the security settings and the environment resolution that decides whether exception details
/// may be shown.
/// </summary>
public sealed class SecuritySettingsTests
{
    #region Fields

    private static readonly Faker Faker = new();

    #endregion

    #region CORS

    [Fact]
    public void IsValid_Should_BeTrue_When_OriginsAreExact()
    {
        var settings = new CorsSettings
        {
            AllowedOrigins =
            [
                Origin(Uri.UriSchemeHttps, Faker.Internet.DomainName()),
                Origin(Uri.UriSchemeHttp, "localhost", Faker.Random.Int(1024, 65535))
            ]
        };

        settings.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("*")]
    [InlineData("https://*.dalilhub.tech")]
    [InlineData("https://ariva.dalilhub.tech/")]
    [InlineData("https://ariva.dalilhub.tech/app")]
    [InlineData("ftp://ariva.dalilhub.tech")]
    [InlineData("ariva.dalilhub.tech")]
    [InlineData("")]
    public void IsValid_Should_BeFalse_When_OriginIsNotExact(string origin)
    {
        var settings = new CorsSettings { AllowedOrigins = [Origin(Uri.UriSchemeHttps, Faker.Internet.DomainName()), origin] };

        settings.IsValid.Should().BeFalse();
    }

    #endregion

    #region Forwarded headers

    [Fact]
    public void IsValid_Should_BeTrue_When_ProxiesAndNetworksParse()
    {
        var settings = new ForwardedHeadersSettings
        {
            KnownProxies = [Faker.Internet.Ip(), Faker.Internet.Ipv6()],
            KnownNetworks = ["10.0.0.0/8", "fd00::/8"]
        };

        settings.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("10.0.0.0/33", null)]
    [InlineData("10.0.0.0", null)]
    [InlineData(null, "300.1.1.1")]
    [InlineData(null, "proxy.local")]
    public void IsValid_Should_BeFalse_When_ProxyOrNetworkDoesNotParse(string network, string proxy)
    {
        var settings = new ForwardedHeadersSettings
        {
            KnownNetworks = network is null ? [] : [network],
            KnownProxies = proxy is null ? [] : [proxy]
        };

        settings.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Defaults_Should_TrustNoProxy_When_NothingIsConfigured()
    {
        var settings = new ForwardedHeadersSettings();

        settings.KnownProxies.Should().BeEmpty();
        settings.KnownNetworks.Should().BeEmpty();
        settings.ForwardLimit.Should().Be(1);
    }

    #endregion

    #region Rate limiting

    [Theory]
    [InlineData(0, 60, 0)]
    [InlineData(10, 0, 0)]
    [InlineData(10, 60, -1)]
    public void IsValid_Should_BeFalse_When_WindowIsNotUsable(int permitLimit, int windowSeconds, int queueLimit)
    {
        var window = new FixedWindowSettings { PermitLimit = permitLimit, WindowSeconds = windowSeconds, QueueLimit = queueLimit };

        window.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Defaults_Should_MakeAuthStricterThanGlobal_When_NothingIsConfigured()
    {
        var settings = new RateLimitingSettings();

        settings.Global.IsValid.Should().BeTrue();
        settings.Auth.IsValid.Should().BeTrue();
        settings.Auth.PermitLimit.Should().BeLessThan(settings.Global.PermitLimit);
    }

    #endregion

    #region Environment

    [Theory]
    [InlineData("--environment=k8s-prd", ArivaEnvironment.K8sPrd)]
    [InlineData("--environment=vm-local", ArivaEnvironment.VmLocal)]
    [InlineData("--Environment=k8s-dev", ArivaEnvironment.K8sDev)]
    public void Resolve_Should_PreferCommandLine_When_EnvironmentArgumentIsGiven(string argument, string expected)
    {
        var environment = ArivaEnvironment.Resolve([argument], AppContext.BaseDirectory);

        environment.Should().Be(expected);
    }

    [Fact]
    public void Resolve_Should_ReadEnvironmentFile_When_NoArgumentOrVariableIsSet()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") is not null
            || Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") is not null)
        {
            Assert.Skip("An environment variable names the environment on this machine.");
        }

        var folder = Directory.CreateTempSubdirectory("ariva-env-");
        try
        {
            File.WriteAllText(Path.Combine(folder.FullName, ArivaEnvironment.FileName), """{ "Environment": "k8s-demo" }""");

            ArivaEnvironment.Resolve([], folder.FullName).Should().Be(ArivaEnvironment.K8sDemo);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void Resolve_Should_RefuseToStart_When_NothingNamesTheEnvironment()
    {
        // ARV-098: an image has no environment file, so a pod without DOTNET_ENVIRONMENT must not run as vm-local.
        if (Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") is not null
            || Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") is not null)
        {
            Assert.Skip("An environment variable names the environment on this machine.");
        }

        var folder = Directory.CreateTempSubdirectory("ariva-env-");
        try
        {
            var resolve = () => ArivaEnvironment.Resolve([], folder.FullName);

            resolve.Should().Throw<InvalidOperationException>().WithMessage("*DOTNET_ENVIRONMENT*");
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("--environment=Production")]
    [InlineData("--environment=prd")]
    [InlineData("--environment=Development")]
    public void Resolve_Should_RefuseAnUnknownName_When_ItWouldSelectNoSettings(string argument)
    {
        var resolve = () => ArivaEnvironment.Resolve([argument], AppContext.BaseDirectory);

        resolve.Should().Throw<InvalidOperationException>().WithMessage("*Unknown Ariva environment*");
    }

    [Fact]
    public void Resolve_Should_RefuseAnUnknownName_When_TheEnvironmentFileNamesIt()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") is not null
            || Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") is not null)
        {
            Assert.Skip("An environment variable names the environment on this machine.");
        }

        var folder = Directory.CreateTempSubdirectory("ariva-env-");
        try
        {
            File.WriteAllText(Path.Combine(folder.FullName, ArivaEnvironment.FileName), """{ "Environment": "Development" }""");

            var resolve = () => ArivaEnvironment.Resolve([], folder.FullName);

            resolve.Should().Throw<InvalidOperationException>().WithMessage("*Unknown Ariva environment*environment.json*");
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("--environment=K8S-PRD", ArivaEnvironment.K8sPrd)]
    [InlineData("--environment= vm-local ", ArivaEnvironment.VmLocal)]
    public void Resolve_Should_ReturnTheCanonicalName_When_CaseOrSpacesDiffer(string argument, string expected)
    {
        ArivaEnvironment.Resolve([argument], AppContext.BaseDirectory).Should().Be(expected);
    }

    [Theory]
    [InlineData("Platform/Backplane/Ariva.Api.Main/Ariva.Api.Main.csproj")]
    [InlineData("Platform/Backplane/Ariva.Api.Ingest/Ariva.Api.Ingest.csproj")]
    [InlineData("Platform/Backplane/Ariva.Api.Stream/Ariva.Api.Stream.csproj")]
    [InlineData("Platform/Backplane/Ariva.Api.Cronz/Ariva.Api.Cronz.csproj")]
    [InlineData("Platform/Backplane/Ariva.Api.Integration/Ariva.Api.Integration.csproj")]
    [InlineData("Platform/Simulation/Ariva.Simulation.Api/Ariva.Simulation.Api.csproj")]
    public void EnvironmentFile_Should_StayOutOfThePublishedImage_When_AHostIsPublished(string project)
    {
        // ARV-098: environment.json names vm-local for developer runs (build output); images are published from the
        // publish output, which must not carry it.
        var document = System.Xml.Linq.XDocument.Load(Path.Combine(RepositoryPaths.Root, project));
        var item = document.Descendants("Content").Single(c => (string)c.Attribute("Update") == ArivaEnvironment.FileName);

        ((string)item.Element("CopyToPublishDirectory")).Should().Be("Never");
        ((string)item.Element("CopyToOutputDirectory")).Should().Be("Always", "developer runs and the E2E suite still read it from the build output");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("k8s-prd")]
    [InlineData("VM-LOCAL")]
    public void EnsureConfigured_Should_Refuse_When_TheSettingsDoNotNameTheHostEnvironment(string configured)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string> { ["Application:Environment"] = configured })
            .Build();

        var ensure = () => ArivaEnvironment.EnsureConfigured(configuration, ArivaEnvironment.VmLocal);

        ensure.Should().Throw<InvalidOperationException>().WithMessage("Application:Environment*");
    }

    [Theory]
    [InlineData(ArivaHosts.Main)]
    [InlineData(ArivaHosts.Ingest)]
    [InlineData(ArivaHosts.Stream)]
    [InlineData(ArivaHosts.Cronz)]
    [InlineData(ArivaHosts.Integration)]
    public async Task Host_Should_RefuseToStart_When_ApplicationEnvironmentDisagreesWithTheHost(string host)
    {
        // ARV-098: a replaced appsettings.base.<environment>.json, or a variable, naming another environment.
        await using var app = ArivaHosts.Create(host, ArivaEnvironment.K8sDev, builder =>
            builder.UseSetting("Application:Environment", ArivaEnvironment.VmLocal));

        var start = () => app.CreateClient();

        start.Should().Throw<InvalidOperationException>().WithMessage("*must match*");
    }

    [Fact]
    public void BaseSettings_Should_NameNoEnvironment_When_TheEnvironmentFileDoesNot()
    {
        // ARV-098: Application:Environment comes only from appsettings.base.<environment>.json, never from a default.
        var common = Path.Combine(RepositoryPaths.Root, "Platform", "Backplane", "Ariva.Api.Common");
        static string Named(string file) => new ConfigurationBuilder().AddJsonFile(file).Build()["Application:Environment"];

        Named(Path.Combine(common, "appsettings.base.json")).Should().BeNull();
        foreach (var environment in ArivaEnvironment.Known)
            Named(Path.Combine(common, $"appsettings.base.{environment}.json")).Should().Be(environment);
    }

    #endregion

    #region Helpers

    /// <summary>An exact origin (scheme, host and optional port, no path), as CORS compares it.</summary>
    private static string Origin(string scheme, string host, int port = -1) =>
        new UriBuilder(scheme, host, port).Uri.GetLeftPart(UriPartial.Authority);

    #endregion
}
