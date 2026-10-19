using Ariva.Api.Common.Logging;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Ariva.UnitTests.Logging;

/// <summary>
/// ARV-091 (CWE-15, ASVS 5.0 V16.2.3, V16.4.3): only <c>Serilog:MinimumLevel</c> is read; any other Serilog setting (a sink,
/// an assembly to load, an enricher, a filter) stops the host instead of being honoured or ignored, and the security events'
/// category stays at Warning whatever the levels say.
/// </summary>
public sealed class ArivaLoggingTests
{
    #region Helpers

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();

    private static Logger Build(IConfiguration configuration) => ArivaLogging.Configure(new LoggerConfiguration(), configuration, "test").CreateLogger();

    private static bool Enabled(Logger logger, string category, LogEventLevel level) =>
        logger.ForContext(Constants.SourceContextPropertyName, category).IsEnabled(level);

    #endregion

    [Theory]
    [InlineData("Serilog:WriteTo:0:Name", "File")]
    [InlineData("Serilog:WriteTo:0:Name", "GrafanaLoki")]
    [InlineData("Serilog:AuditTo:0:Name", "File")]
    [InlineData("Serilog:Using:0", "Serilog.Sinks.File")]
    [InlineData("Serilog:Enrich:0", "FromLogContext")]
    [InlineData("Serilog:Filter:0:Name", "ByExcluding")]
    [InlineData("Serilog:Destructure:0:Name", "ToMaximumDepth")]
    [InlineData("Serilog:LevelSwitches:$controlSwitch", "Verbose")]
    [InlineData("Serilog:Properties:Application", "spoofed")]
    [InlineData("Serilog:MinimumLevel:ControlledBy", "$controlSwitch")]
    public void Configure_Should_RefuseTheSetting_When_SerilogHoldsAnythingButLevels(string key, string value)
    {
        var configure = () => Build(Configuration((key, value)));

        var message = configure.Should().Throw<InvalidOperationException>().WithMessage("*is not read*").Which.Message;
        if (value != "File")
            message.Should().NotContain(value, "the message names the key, never its value");
    }

    [Fact]
    public void Configure_Should_ReadTheLevelsAndOverrides_When_OnlyMinimumLevelIsSet()
    {
        using var logger = Build(Configuration(("Serilog:MinimumLevel:Default", "Error"), ("Serilog:MinimumLevel:Override:Ariva.Infra", "debug")));

        Enabled(logger, "Ariva.Api.Main.Something", LogEventLevel.Warning).Should().BeFalse("the default is Error");
        Enabled(logger, "Ariva.Infra.Timescale.SqlScriptRunner", LogEventLevel.Debug).Should().BeTrue("the override, read ignoring case");
    }

    [Fact]
    public void Configure_Should_ReadALevelName_When_MinimumLevelIsAString()
    {
        using var logger = Build(Configuration(("Serilog:MinimumLevel", "Warning")));

        Enabled(logger, "Ariva.Infra.Any", LogEventLevel.Information).Should().BeFalse();
        Enabled(logger, "Ariva.Infra.Any", LogEventLevel.Warning).Should().BeTrue();
    }

    [Theory]
    [InlineData("Serilog:MinimumLevel:Default", "Loud")]
    [InlineData("Serilog:MinimumLevel:Default", "3")]
    [InlineData("Serilog:MinimumLevel:Override:Ariva", "Everything")]
    public void Configure_Should_RefuseTheLevel_When_ItIsNotASerilogLevelName(string key, string value)
    {
        var configure = () => Build(Configuration((key, value)));

        configure.Should().Throw<InvalidOperationException>().WithMessage("*must be one of Verbose, Debug, Information, Warning, Error or Fatal*");
    }

    [Fact]
    public void Configure_Should_KeepTheSecurityEventsAtWarning_When_TheLevelsWouldSilenceThem()
    {
        using var logger = Build(Configuration(("Serilog:MinimumLevel:Default", "Fatal"), ($"Serilog:MinimumLevel:Override:{ArivaLogging.SecurityEventCategory}", "Fatal"),
            ("Serilog:MinimumLevel:Override:Ariva.Api.Common", "Fatal")));

        Enabled(logger, ArivaLogging.SecurityEventCategory, LogEventLevel.Warning).Should().BeTrue("no level setting silences the security events");
        Enabled(logger, ArivaLogging.SecurityEventCategory, LogEventLevel.Information).Should().BeFalse();
        Enabled(logger, "Ariva.Api.Common.Logging.Other", LogEventLevel.Warning).Should().BeFalse("only the security events' category is held");
    }

    [Fact]
    public void BaseSettings_Should_HoldOnlyLevels_When_Read()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.base.json"))
            .Build();

        var check = () => ArivaLogging.EnsureOnlyLevelsAreConfigured(configuration);

        check.Should().NotThrow("the committed settings set levels only");
    }
}
