using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ariva.UnitTests.Security;

/// <summary>
/// CWE-120: the input limits from <c>Security:Limits</c> reach Kestrel, form parsing and System.Text.Json in every
/// host. The options are read from the built host, so the tests see exactly what the server uses.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class RequestLimitsTests
{
    #region Fields

    private const long OneMegabyte = 1_048_576;

    #endregion

    #region Tests

    [Theory]
    [MemberData(nameof(ArivaHosts.BackplaneHosts), MemberType = typeof(ArivaHosts))]
    public async Task GetKestrelOptions_Should_ApplyConfiguredLimits_When_HostIsBuilt(string host)
    {
        await using var app = ArivaHosts.Create(host);

        var kestrel = app.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;

        kestrel.AddServerHeader.Should().BeFalse();
        kestrel.Limits.MaxRequestBodySize.Should().Be(OneMegabyte);
        kestrel.Limits.MaxRequestLineSize.Should().Be(8_192);
        kestrel.Limits.MaxRequestHeadersTotalSize.Should().Be(32_768);
        kestrel.Limits.MaxRequestHeaderCount.Should().Be(64);
        kestrel.Limits.RequestHeadersTimeout.Should().Be(TimeSpan.FromSeconds(15));
    }

    [Theory]
    [MemberData(nameof(ArivaHosts.BackplaneHosts), MemberType = typeof(ArivaHosts))]
    public async Task GetFormOptions_Should_LimitBodyValuesAndKeys_When_HostIsBuilt(string host)
    {
        await using var app = ArivaHosts.Create(host);

        var form = app.Services.GetRequiredService<IOptions<FormOptions>>().Value;

        form.MultipartBodyLengthLimit.Should().Be(OneMegabyte);
        form.BufferBodyLengthLimit.Should().Be(OneMegabyte);
        form.ValueCountLimit.Should().Be(1_024);
        form.ValueLengthLimit.Should().Be(65_536);
        form.KeyLengthLimit.Should().Be(2_048);
    }

    [Theory]
    [MemberData(nameof(ArivaHosts.BackplaneHosts), MemberType = typeof(ArivaHosts))]
    public async Task GetJsonOptions_Should_LimitDepthTo32_When_HostIsBuilt(string host)
    {
        await using var app = ArivaHosts.Create(host);

        var minimalApis = app.Services.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value;
        var controllers = app.Services.GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>().Value;

        minimalApis.SerializerOptions.MaxDepth.Should().Be(32);
        controllers.JsonSerializerOptions.MaxDepth.Should().Be(32);
    }

    [Fact]
    public async Task GetKestrelOptions_Should_UseConfiguredValue_When_BodyLimitIsOverridden()
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Main, configure: builder => builder
            .UseSetting("Security:Limits:MaxRequestBodyBytes", "262144")
            .UseSetting("Security:Limits:MaxJsonDepth", "16"));

        var kestrel = app.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;
        var json = app.Services.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value;

        kestrel.Limits.MaxRequestBodySize.Should().Be(262_144);
        json.SerializerOptions.MaxDepth.Should().Be(16);
    }

    [Fact]
    public async Task GetKestrelOptions_Should_LimitBodyAndHideServer_When_HostIsSimulation()
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Simulation);

        var kestrel = app.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;

        kestrel.AddServerHeader.Should().BeFalse();
        kestrel.Limits.MaxRequestBodySize.Should().Be(OneMegabyte);
    }

    [Fact]
    public async Task CreateClient_Should_FailAtStartup_When_LimitIsNotPositive()
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Main, configure: builder => builder.UseSetting("Security:Limits:MaxRequestBodyBytes", "0"));

        Action start = () => app.CreateClient();

        start.Should().Throw<Exception>().WithMessage("*Security:Limits*");
    }

    #endregion
}
