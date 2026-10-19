using System.Text.Json;
using Ariva.Api.Common.Logging;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Ariva.UnitTests.Logging;

/// <summary>
/// ARV-091 (ASVS 5.0 V16.2.5, CWE-532): the log protection levels. Staff email addresses are masked, sensor track ids and desk
/// codes are hashed with a keyed HMAC (the same value gives the same pseudonym under one key and another under another key),
/// credentials stay omitted; by property name in any case and spelling, inside objects, dictionaries and arrays, and in the
/// rendered message.
/// </summary>
public sealed class LogProtectionTests
{
    #region Fields

    private const string Address = "aisha.rahman@example.com";
    private const string DeskCode = "IMM-D07";
    private const string TrackId = "trk-0042-canary";

    private static readonly byte[] KeyA = [.. Enumerable.Range(1, 32).Select(i => (byte)i)];
    private static readonly byte[] KeyB = [.. Enumerable.Range(101, 32).Select(i => (byte)i)];

    #endregion

    #region Theory data

    public static TheoryData<string, LogProtectionLevel> Names => new()
    {
        { "Email", LogProtectionLevel.Mask },
        { "email", LogProtectionLevel.Mask },
        { "EMAIL_ADDRESS", LogProtectionLevel.Mask },
        { "staff-email", LogProtectionLevel.Mask },
        { "AlertRecipients", LogProtectionLevel.Mask },
        { "recipient", LogProtectionLevel.Mask },
        { "TrackId", LogProtectionLevel.Hash },
        { "track_id", LogProtectionLevel.Hash },
        { "TRACK-IDS", LogProtectionLevel.Hash },
        { "NamespacedTrackId", LogProtectionLevel.Hash },
        { "DeskCode", LogProtectionLevel.Hash },
        { "desk_code", LogProtectionLevel.Hash },
        { "AmanDeskCode", LogProtectionLevel.Hash },
        { "Desk", LogProtectionLevel.Hash },
        { "desk-key", LogProtectionLevel.Hash },
        { "Password", LogProtectionLevel.Omit },
        { "x-totp-code", LogProtectionLevel.Omit },
        { "PseudonymKey", LogProtectionLevel.Omit },
        { "Site", LogProtectionLevel.None },
        { "Zone", LogProtectionLevel.None },
        { "DesksRecorded", LogProtectionLevel.None },
        { "Code", LogProtectionLevel.None },
        { "UserId", LogProtectionLevel.None },
        { "", LogProtectionLevel.None }
    };

    #endregion

    #region Helpers

    private sealed class ListSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private static (Logger Logger, ListSink Sink) CreateLogger(byte[] key = null)
    {
        var sink = new ListSink();
        var logger = new LoggerConfiguration().MinimumLevel.Verbose()
            .Enrich.With(new RedactionEnricher(new LogProtection(key ?? KeyA)))
            .WriteTo.Sink(sink)
            .CreateLogger();
        return (logger, sink);
    }

    private static string Rendered(LogEvent logEvent)
    {
        using var writer = new StringWriter();
        new Serilog.Formatting.Json.JsonFormatter(renderMessage: true).Format(logEvent, writer);
        return writer.ToString();
    }

    private static string Scalar(LogEvent logEvent, string name) => ((ScalarValue)logEvent.Properties[name]).Value as string;

    #endregion

    #region Levels

    [Theory]
    [MemberData(nameof(Names))]
    public void LevelOf_Should_ChooseTheLevelByName_When_CaseOrSeparatorsDiffer(string name, LogProtectionLevel expected)
    {
        LogProtection.LevelOf(name).Should().Be(expected);
    }

    [Theory]
    [InlineData("Email")]
    [InlineData("email_address")]
    [InlineData("Recipient")]
    public void Enrich_Should_MaskTheAddress_When_ThePropertyIsAnEmail(string name)
    {
        var (logger, sink) = CreateLogger();

        logger.ForContext(name, Address).Information("Mail queued");

        Scalar(sink.Events.Single(), name).Should().Be("a***@example.com");
        Rendered(sink.Events.Single()).Should().NotContain("aisha");
    }

    [Theory]
    [InlineData("DeskCode", DeskCode)]
    [InlineData("desk-code", DeskCode)]
    [InlineData("TRACK_ID", TrackId)]
    [InlineData("Desk", DeskCode)]
    public void Enrich_Should_HashTheValue_When_ThePropertyIsATrackIdOrADeskCode(string name, string value)
    {
        var (logger, sink) = CreateLogger();

        logger.ForContext(name, value).Information("Seen");

        var hashed = Scalar(sink.Events.Single(), name);
        hashed.Should().Be(new LogProtection(KeyA).Hash(value)).And.MatchRegex("^#[0-9a-f]{12}$");
        Rendered(sink.Events.Single()).Should().NotContain(value);
    }

    [Fact]
    public void Enrich_Should_RenderTheMessageWithTheProtectedValues_When_TheTemplateNamesThem()
    {
        var (logger, sink) = CreateLogger();

        logger.Warning("Border feed code {DeskCode} of site {Site} for {Email}, track {TrackId}", DeskCode, "DMO", Address, TrackId);

        var message = sink.Events.Single().RenderMessage();
        var protection = new LogProtection(KeyA);
        message.Should().Be($"Border feed code \"{protection.Hash(DeskCode)}\" of site \"DMO\" for \"a***@example.com\", track \"{protection.Hash(TrackId)}\"");
        Rendered(sink.Events.Single()).Should().NotContain(DeskCode).And.NotContain(TrackId).And.NotContain("aisha");
    }

    [Fact]
    public void Enrich_Should_ProtectNestedValues_When_ObjectsDictionariesAndArraysAreDestructured()
    {
        var (logger, sink) = CreateLogger();

        logger.Information("Batch {@Batch} {@ByDesk} {@People}",
            new
            {
                Site = "DMO",
                Desk = new { Code = DeskCode, Checkpoint = "IMM" },
                Crossings = new object[] { new { track_id = TrackId, Line = "L1" }, new { track_id = 12345, Line = "L2" } },
                Owner = new { StaffEmail = Address, Password = "pass-canary-77" }
            },
            new Dictionary<string, object> { ["desk_code"] = DeskCode, ["count"] = 3, ["Recipients"] = new[] { Address, "b.x@example.org" } },
            new[] { new { Email = Address } });

        var json = Rendered(sink.Events.Single());
        var protection = new LogProtection(KeyA);
        json.Should().NotContain(DeskCode).And.NotContain(TrackId).And.NotContain("aisha").And.NotContain("b.x@").And.NotContain("pass-canary-77");
        json.Should().Contain(protection.Hash(DeskCode)).And.Contain(protection.Hash(TrackId)).And.Contain(protection.Hash("12345"),
            "a track id sent as a number is hashed as its text");
        json.Should().Contain("a***@example.com").And.Contain("b***@example.org").And.Contain("[REDACTED]");
        json.Should().Contain($"\"Checkpoint\":\"{protection.Hash("IMM")}\"", "everything beneath a hashed name is hashed, a desk's other members too");
        json.Should().Contain("\"Site\":\"DMO\"").And.Contain("\"Line\":\"L1\"").And.Contain("\"count\":3", "other values are logged as they are");
    }

    [Fact]
    public void Enrich_Should_MaskAddressesInsideText_When_NoNameSaysSo()
    {
        var (logger, sink) = CreateLogger();

        logger.Information("Delivery failed: {Reason}", $"mailbox {Address} unavailable; postgres@localhost kept");

        Scalar(sink.Events.Single(), "Reason").Should().Be("mailbox a***@example.com unavailable; postgres@localhost kept");
    }

    [Fact]
    public void Enrich_Should_KeepOmittingCredentials_When_TheyAreInsideAHashedValue()
    {
        var (logger, sink) = CreateLogger();

        logger.Information("Desk {@Desk}", new { Code = DeskCode, Token = "tok-canary-5150" });

        var json = Rendered(sink.Events.Single());
        json.Should().NotContain("tok-canary-5150").And.Contain("[REDACTED]");
    }

    #endregion

    #region Hashing

    [Fact]
    public void Hash_Should_GiveTheSamePseudonym_When_TheSameValueIsHashedWithTheSameKey()
    {
        var first = new LogProtection(KeyA);
        var second = new LogProtection(KeyA);

        first.Hash(DeskCode).Should().Be(first.Hash(DeskCode)).And.Be(second.Hash(DeskCode));
        first.Hash(DeskCode).Should().NotBe(first.Hash("IMM-D08"));
        first.Hash(DeskCode).Should().HaveLength(1 + LogProtection.HashLength).And.StartWith(LogProtection.HashMarker);
    }

    [Fact]
    public void Hash_Should_GiveAnotherPseudonym_When_TheKeyDiffers()
    {
        new LogProtection(KeyA).Hash(DeskCode).Should().NotBe(new LogProtection(KeyB).Hash(DeskCode));
        new LogProtection().Hash(DeskCode).Should().NotBe(new LogProtection().Hash(DeskCode), "without a configured key each process draws its own");
    }

    [Fact]
    public void Enrich_Should_HashWithTheConfiguredKey_When_PseudonymKeyIsSet()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string> { [LogProtection.KeySetting] = Convert.ToBase64String(KeyB) })
            .Build();

        var protection = LogProtection.From(configuration);

        protection.Hash(DeskCode).Should().Be(new LogProtection(KeyB).Hash(DeskCode));
    }

    [Theory]
    [InlineData("not base64 at all!")]
    [InlineData("AAECAwQFBgcICQoLDA0ODw==")]
    public void From_Should_RefuseTheKey_When_ItIsNotBase64OfThirtyTwoBytes(string key)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string> { [LogProtection.KeySetting] = key })
            .Build();

        var from = () => LogProtection.From(configuration);

        from.Should().Throw<InvalidOperationException>().WithMessage($"*{LogProtection.KeySetting}*").Which.Message.Should().NotContain(key);
    }

    [Fact]
    public void From_Should_DrawARandomKey_When_PseudonymKeyIsNotSet()
    {
        var empty = new ConfigurationBuilder().Build();

        LogProtection.From(empty).Hash(DeskCode).Should().NotBe(LogProtection.From(empty).Hash(DeskCode));
    }

    [Fact]
    public void Hash_Should_ChangeWithTheDay_When_TheSameValueIsHashedOnAnotherDay()
    {
        var clock = new Setup.ManualClock(new DateTimeOffset(2026, 10, 19, 23, 59, 0, TimeSpan.Zero));
        var protection = new LogProtection(KeyA, clock);
        var today = protection.Hash(DeskCode);

        protection.Hash(DeskCode).Should().Be(today, "within a day a pseudonym links the lines about one desk");
        clock.Advance(TimeSpan.FromMinutes(2));

        protection.Hash(DeskCode).Should().NotBe(today, "across days it links nothing: no desk code beyond the operating day");
        new LogProtection(KeyA, clock).Hash(DeskCode).Should().Be(protection.Hash(DeskCode), "every host with the key agrees on the day's pseudonym");
    }

    [Fact]
    public void From_Should_RefuseTheKey_When_ItComesFromAFileOutsideVmLocal()
    {
        var folder = Directory.CreateTempSubdirectory("ariva-pseudonym-");
        try
        {
            var file = Path.Combine(folder.FullName, "appsettings.json");
            File.WriteAllText(file, $$"""{ "Logging": { "PseudonymKey": "{{Convert.ToBase64String(KeyA)}}" } }""");
            var fromFile = new ConfigurationBuilder().AddJsonFile(file).Build();
            var fromEnvironment = new ConfigurationBuilder().AddJsonFile(file)
                .AddInMemoryCollection(new Dictionary<string, string> { [LogProtection.KeySetting] = Convert.ToBase64String(KeyB) }).Build();

            var production = () => LogProtection.From(fromFile, "k8s-prd");

            production.Should().Throw<InvalidOperationException>().WithMessage("*configuration file*")
                .Which.Message.Should().NotContain(Convert.ToBase64String(KeyA));
            LogProtection.From(fromFile, "vm-local").Hash(DeskCode).Should().Be(new LogProtection(KeyA).Hash(DeskCode), "a developer machine may keep it in a file");
            LogProtection.From(fromEnvironment, "k8s-prd").Hash(DeskCode).Should().Be(new LogProtection(KeyB).Hash(DeskCode),
                "the value the environment gives wins, and it is not a file's");
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    #endregion

    #region Dictionary keys

    [Fact]
    public void Enrich_Should_HashTheKeys_When_ADictionaryIsKeyedByDeskCode()
    {
        var (logger, sink) = CreateLogger();

        logger.Information("Open desks {@DeskCodes}", new Dictionary<string, int> { [DeskCode] = 3 });

        var json = Rendered(sink.Events.Single());
        json.Should().NotContain(DeskCode).And.Contain(new LogProtection(KeyA).Hash(DeskCode));
    }

    [Fact]
    public void Enrich_Should_MaskTheKeys_When_ADictionaryIsKeyedByAddress()
    {
        var (logger, sink) = CreateLogger();

        logger.Information("Deliveries {@Counts}", new Dictionary<string, int> { [Address] = 2, ["plain"] = 1 });

        var json = Rendered(sink.Events.Single());
        json.Should().NotContain("aisha").And.Contain("a***@example.com").And.Contain("\"plain\":1");
    }

    [Theory]
    [InlineData("Counts")]
    [InlineData("Recipients")]
    public void Enrich_Should_KeepEveryEntry_When_TwoKeysProtectAlike(string name)
    {
        var (logger, sink) = CreateLogger();

        logger.Information("{@" + name + "}", new Dictionary<string, int> { ["alice@x.com"] = 1, ["anna@x.com"] = 2, ["a***@x.com~2"] = 3 });

        var dictionary = (DictionaryValue)sink.Events.Single().Properties[name];
        dictionary.Elements.Select(e => e.Key.Value as string).Should().Equal(["a***@x.com", "a***@x.com~2", "a***@x.com~2~2"],
            "colliding keys get a collision index built from the protected text, also a key that looked like one already");
        dictionary.Elements.Values.Should().HaveCount(3, "no entry is dropped");
        Rendered(sink.Events.Single()).Should().NotContain("alice").And.NotContain("anna");
    }

    [Fact]
    public void Enrich_Should_RedactOnlyTheFailingPropertyAndProtectTheRest_When_AValueCannotBeRead()
    {
        using var metrics = new SecurityEventMetrics();
        using var reader = new Setup.InstrumentReader(metrics.Meter);
        var sink = new ListSink();
        using var logger = new LoggerConfiguration().MinimumLevel.Verbose()
            .Enrich.With(new RedactionEnricher(new LogProtection(KeyA), metrics))
            .WriteTo.Sink(sink)
            .CreateLogger();
        var template = new Serilog.Parsing.MessageTemplateParser().Parse("{DeskCode} {Email} {Password} {Site}");

        logger.Write(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Warning, null, template,
        [
            new LogEventProperty("DeskCode", new ScalarValue(new Unreadable())),
            new LogEventProperty("Email", new ScalarValue(Address)),
            new LogEventProperty("Password", new ScalarValue("pass-canary-4242")),
            new LogEventProperty("Site", new ScalarValue("DMO"))
        ]));

        var written = sink.Events.Should().ContainSingle("the event is still written").Subject;
        Scalar(written, "DeskCode").Should().Be(RedactionEnricher.Redacted, "a value whose protection fails is never written as it is");
        Scalar(written, "Email").Should().Be("a***@example.com", "the properties after it are still protected");
        Scalar(written, "Password").Should().Be(RedactionEnricher.Redacted);
        Scalar(written, "Site").Should().Be("DMO");
        Rendered(written).Should().NotContain("pass-canary-4242").And.NotContain("aisha");
        reader.Sum(SecurityEventMetrics.ProtectionFailuresName).Should().Be(1);
    }

    /// <summary>A value whose text cannot be read.</summary>
    private sealed class Unreadable
    {
        public override string ToString() => throw new InvalidOperationException("unreadable value IMM-D07");
    }

    #endregion

    #region The address rule

    [Fact]
    public void EmailAddress_Should_BeLinearWithoutATimeout_When_Matched()
    {
        var rule = LogProtection.EmailAddress();

        rule.Options.Should().HaveFlag(System.Text.RegularExpressions.RegexOptions.NonBacktracking, "linear time is the bound against ReDoS (CWE-1333)");
        rule.MatchTimeout.Should().Be(System.Text.RegularExpressions.Regex.InfiniteMatchTimeout,
            "a timeout would throw inside the enricher, and Serilog would emit the event unmasked");
    }

    [Theory]
    [InlineData("a@a.a", true)]
    [InlineData("a@a.", true)]
    [InlineData("a.", false)]
    [InlineData("a@", false)]
    [InlineData("%+-.", false)]
    [InlineData(".@.", false)]
    public void MaskAddressesIn_Should_FinishQuickly_When_TheTextIsALongAdversarialRun(string fragment, bool holdsAddresses)
    {
        // 100,000 characters of address fragments and dots, ending in an @ that completes no address.
        var text = string.Concat(Enumerable.Repeat(fragment, 100_000 / fragment.Length)) + "@";
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var masked = LogProtection.MaskAddressesIn(text);

        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2), "the rule is linear in the text's length");
        if (holdsAddresses)
            masked.Should().StartWith("a***@").And.Contain("***@a.a", "the run's addresses are masked");
        else
            masked.Should().BeSameAs(text, "a run without an address is left as it is");
    }

    [Theory]
    [InlineData("mail x@b.c and y.z@d.e.f done", "mail x***@b.c and y***@d.e.f done")]
    [InlineData("a@b.c%d@e.f", "a***@b.c%***@e.f")]
    [InlineData("user@localhost stays", "user@localhost stays")]
    [InlineData("@example.com", "@example.com")]
    public void MaskAddressesIn_Should_MaskEachAddress_When_TextHoldsSeveral(string text, string expected)
    {
        LogProtection.MaskAddressesIn(text).Should().Be(expected);
    }

    #endregion

    #region Masking

    [Theory]
    [InlineData("aisha.rahman@example.com", "a***@example.com")]
    [InlineData(" b@x.org ", "b***@x.org")]
    [InlineData("not-an-address", "n***")]
    [InlineData("@example.com", "@***")]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void Mask_Should_KeepTheFirstCharacterAndTheDomain_When_AValueIsMasked(string value, string expected)
    {
        LogProtection.Mask(value).Should().Be(expected);
    }

    [Fact]
    public void MaskAddressesIn_Should_ReturnTheSameText_When_ItHoldsNoAddress()
    {
        const string text = "nothing to see here";

        LogProtection.MaskAddressesIn(text).Should().BeSameAs(text);
    }

    [Fact]
    public void Rendered_Should_StayValidJson_When_ValuesAreProtected()
    {
        var (logger, sink) = CreateLogger();

        logger.Information("{Email} {DeskCode}", "\"quote\"@example.com", "<script>alert(1)</script>");

        var parse = () => JsonDocument.Parse(Rendered(sink.Events.Single()));
        parse.Should().NotThrow();
        Rendered(sink.Events.Single()).Should().NotContain("<script>");
    }

    #endregion
}
