using Ariva.Api.Common.Logging;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Ariva.UnitTests.Logging;

/// <summary>ARV-007 (CWE-532): every credential named in the story is removed before a sink sees the event.</summary>
public sealed class RedactionEnricherTests
{
    // Built at run time so secret scanners see no token literal in the source.
    private static readonly string Jwt = string.Join('.', Base64Url("{\"alg\":\"ES256\"}"), Base64Url("{\"sub\":\"user-1\"}"), Base64Url("signature-value"));

    private static string Base64Url(string text) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static TheoryData<string, string> TextCases => new()
    {
        { "/hubs/live?access_token=" + Jwt + "&id=abc", "/hubs/live?access_token=[REDACTED]&id=abc" },
        { "https://ariva/api/x?a=1&access_token=opaque-token-value", "https://ariva/api/x?a=1&access_token=[REDACTED]" },
        { "sent Authorization: Bearer opaque.token-value_1", "sent Authorization: Bearer [REDACTED]" },
        { "header Basic dXNlcjpwYXNzd29yZA==", "header Basic [REDACTED]" },
        { "token " + Jwt + " in text", "token [REDACTED] in text" },
        { "nothing secret here", "nothing secret here" }
    };

    private sealed class ListSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private static (Logger Logger, ListSink Sink) CreateLogger()
    {
        var sink = new ListSink();
        var logger = new LoggerConfiguration().MinimumLevel.Verbose().Enrich.With<RedactionEnricher>().WriteTo.Sink(sink).CreateLogger();
        return (logger, sink);
    }

    private static string Rendered(LogEvent logEvent)
    {
        using var writer = new StringWriter();
        new Serilog.Formatting.Json.JsonFormatter(renderMessage: true).Format(logEvent, writer);
        return writer.ToString();
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("Cookie")]
    [InlineData("Set-Cookie")]
    [InlineData("X-TOTP-Code")]
    [InlineData("access_token")]
    [InlineData("Password")]
    public void Enrich_Should_RedactValue_When_PropertyNameIsSensitive(string name)
    {
        var (logger, sink) = CreateLogger();

        logger.ForContext(name, "secret-value-123").Information("event");

        sink.Events.Should().ContainSingle();
        sink.Events[0].Properties[name].ToString().Should().Be("\"[REDACTED]\"");
        Rendered(sink.Events[0]).Should().NotContain("secret-value-123");
    }

    [Fact]
    public void Enrich_Should_RedactSensitiveHeaders_When_HeadersAreLoggedAsDictionary()
    {
        var (logger, sink) = CreateLogger();
        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer " + Jwt,
            ["Cookie"] = "__Secure-ariva_rt=refresh-secret",
            ["Set-Cookie"] = "__Secure-ariva_rt=refresh-secret-2; Path=/api/auth",
            ["X-TOTP-Code"] = "123456",
            ["Accept"] = "application/json"
        };

        logger.Information("Headers {@Headers}", headers);

        var rendered = Rendered(sink.Events[0]);
        rendered.Should().NotContain(Jwt).And.NotContain("refresh-secret").And.NotContain("123456");
        rendered.Should().Contain("application/json", "values with harmless names are kept");
    }

    [Theory]
    [MemberData(nameof(TextCases))]
    public void RedactText_Should_MaskCredentials_When_TheyAppearInsideText(string input, string expected)
    {
        RedactionEnricher.RedactText(input).Should().Be(expected);
    }

    [Fact]
    public void Enrich_Should_RedactQueryString_When_RequestPathIsLogged()
    {
        var (logger, sink) = CreateLogger();

        logger.Information("Request starting {Path}{QueryString}", "/hubs/live", "?access_token=" + Jwt);

        sink.Events[0].RenderMessage().Should().Be("Request starting \"/hubs/live\"\"?access_token=[REDACTED]\"");
    }

    [Fact]
    public void Enrich_Should_RedactNestedMembers_When_ObjectsAreDestructured()
    {
        var (logger, sink) = CreateLogger();

        logger.Information("Login {@Request} {@Tokens}",
            new { UserName = "officer.one", Password = "correct horse battery staple", Totp = new { TotpCode = "654321" } },
            new[] { "Bearer " + Jwt, "plain" });

        var rendered = Rendered(sink.Events[0]);
        rendered.Should().Contain("officer.one");
        rendered.Should().NotContain("correct horse").And.NotContain("654321").And.NotContain(Jwt);
        rendered.Should().Contain("plain");
    }

    [Fact]
    public void Configure_Should_KeepHostingAtWarning_When_ConfigurationAsksForDebug()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["Serilog:MinimumLevel:Default"] = "Debug",
                ["Serilog:MinimumLevel:Override:Microsoft.AspNetCore.Hosting"] = "Debug"
            })
            .Build();

        using var logger = ArivaLogging.Configure(new LoggerConfiguration(), configuration, "test").CreateLogger();

        logger.ForContext(Constants.SourceContextPropertyName, "Microsoft.AspNetCore.Hosting.Diagnostics")
            .IsEnabled(LogEventLevel.Information).Should().BeFalse("the request-start line carries the query string with access_token");
        logger.ForContext(Constants.SourceContextPropertyName, "Ariva.Infra.Timescale.SqlScriptRunner")
            .IsEnabled(LogEventLevel.Debug).Should().BeTrue();
    }

    [Fact]
    public void BaseSettings_Should_SetHostingToWarning_When_Read()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.base.json"))
            .Build();

        configuration["Serilog:MinimumLevel:Override:Microsoft.AspNetCore.Hosting"].Should().Be("Warning");
    }

    [Fact]
    public void OtlpSettings_Should_BeOff_When_NothingIsConfigured()
    {
        var settings = OtlpSettings.From(new ConfigurationBuilder().Build());

        settings.Enabled.Should().BeFalse("an unreachable collector must never affect startup");
    }

    [Fact]
    public void OtlpSettings_Should_FollowChartVariables_When_EndpointIsSet()
    {
        var settings = OtlpSettings.From(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://otel-collector:4318",
                ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf"
            })
            .Build());

        settings.Enabled.Should().BeTrue();
        settings.Endpoint.Should().Be(new Uri("http://otel-collector:4318"));
        settings.UseHttp.Should().BeTrue();
    }

    [Theory]
    [InlineData("api-${Application:ShortName}", "main", "api-main")]
    [InlineData("custom-name", "main", "custom-name")]
    [InlineData(null, null, "ariva")]
    public void ApplicationName_Should_ResolveShortName_When_BaseNameIsTemplated(string name, string shortName, string expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string> { ["Application:Name"] = name, ["Application:ShortName"] = shortName })
            .Build();

        ArivaLogging.ApplicationName(configuration).Should().Be(expected);
    }
}
