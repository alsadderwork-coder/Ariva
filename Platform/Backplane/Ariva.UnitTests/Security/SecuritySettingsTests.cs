using Ariva.Api.Common.Hosting;
using Ariva.Api.Common.Settings;
using Bogus;
using FluentAssertions;

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

    #endregion

    #region Helpers

    /// <summary>An exact origin (scheme, host and optional port, no path), as CORS compares it.</summary>
    private static string Origin(string scheme, string host, int port = -1) =>
        new UriBuilder(scheme, host, port).Uri.GetLeftPart(UriPartial.Authority);

    #endregion
}
